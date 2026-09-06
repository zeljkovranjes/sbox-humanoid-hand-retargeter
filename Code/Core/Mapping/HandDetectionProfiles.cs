#nullable enable
using System.Text.RegularExpressions;
using Skel = HumanoidHandRetargeter.Skeleton.Skeleton;

namespace HumanoidHandRetargeter.Mapping;

/// <summary>Reference conventions supply semantic hints; they never bypass hierarchy validation.</summary>
internal static partial class HandDetectionProfiles
{
    internal sealed record Alias(string Role,string Name);
    internal sealed record Hint(string Role,HandSide Side,string Profile);
    internal sealed class Profile
    {
        public string Name { get; }
        private readonly Regex[] prefixes;
        public Dictionary<string,string[]> Roles { get; }
        public int RoleCount { get; }
        public Profile(string name,string[] patterns,Alias[] aliases)
        {
            Name=name;prefixes=patterns.Select(p=>new Regex(p,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)).ToArray();
            Roles=aliases.GroupBy(a=>Key(a.Name)).ToDictionary(g=>g.Key,g=>g.Select(a=>a.Role).Distinct().ToArray());
            RoleCount=aliases.Select(a=>a.Role).Distinct().Count();
        }
        public string Strip(string name)
        {
            // Strip FBX namespaces independently of exporter prefixes (e.g. Character:CC_Base_L_Hand).
            name=name[(name.LastIndexOf(':')+1)..];
            var hash=name.LastIndexOf('#');
            if(hash>=0&&name[(hash+1)..].All(char.IsDigit))name=name[..hash];
            foreach(var prefix in prefixes)name=prefix.Replace(name,"");
            return name;
        }
    }
    internal static string Key(string name)=>string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    internal static Hint?[] Resolve(Skel rig)
    {
        var proposals=new Dictionary<int,List<(int Score,Hint Hint)>>();
        foreach(var profile in Catalog)
        {
            var matches=new Dictionary<int,string>();
            foreach(var bone in rig.Bones)
                if(profile.Roles.TryGetValue(Key(profile.Strip(bone.Name)),out var roles)&&roles.Length==1)
                    matches[bone.Index]=roles[0];
            foreach(var wrist in matches.Where(p=>p.Value is "HandL" or "HandR"))
            {
                var side=wrist.Value[^1];
                var relevant=matches.Where(p=>p.Value[^1]==side && (p.Key==wrist.Key ||
                    (IsDigit(p.Value)&&rig.DescendsFrom(p.Key,wrist.Key)) ||
                    (IsArm(p.Value)&&rig.DescendsFrom(wrist.Key,p.Key)))).ToArray();
                // Match coverage distinguishes e.g. SOMA metacarpals from Mixamo phalanges.
                // Namespace evidence breaks equivalent-name ties without overpowering anatomy.
                var score=relevant.Length*1000+relevant.Select(p=>p.Value).Distinct().Count()*100/Math.Max(1,profile.RoleCount/2)
                    +relevant.Count(p=>profile.Strip(rig[p.Key].Name)!=rig[p.Key].Name)*2;
                foreach(var match in relevant)
                {
                    if(!proposals.TryGetValue(match.Key,out var list))proposals[match.Key]=list=new();
                    list.Add((score,new(match.Value[..^1],side=='L'?HandSide.Left:HandSide.Right,profile.Name)));
                }
            }
        }
        var hints=new Hint?[rig.Count];
        foreach(var pair in proposals)
        {
            var best=pair.Value.Where(p=>p.Score==pair.Value.Max(v=>v.Score)).Select(p=>p.Hint).ToArray();
            if(best.Select(p=>(p.Role,p.Side)).Distinct().Count()==1)hints[pair.Key]=best[0];
        }
        ResolveNumberedFpsHands(rig,hints);
        return hints;
    }
    // Numbered FPS rays can run from thumb to pinky or in reverse, with an
    // additional palm joint on the four fingers. Require the whole convention
    // and its radial geometry; bare finger numbers alone are ambiguous.
    private static void ResolveNumberedFpsHands(Skel rig,Hint?[] hints)
    {
        foreach(var side in new[]{HandSide.Left,HandSide.Right})
        {
            var suffix=side==HandSide.Left?"l":"r";
            string Name(int bone)=>Key(rig[bone].Name[(rig[bone].Name.LastIndexOf(':')+1)..]).Replace("left","l").Replace("right","r");
            var wrists=rig.Bones.Where(b=>Name(b.Index)=="hand"+suffix).ToArray();
            if(wrists.Length!=1)continue;
            var wrist=wrists[0].Index;
            foreach(var thumbNumber in new[]{1,5})
            {
            var chains=new List<int[]>();
            for(var finger=1;finger<=5;finger++)
            {
                var chain=new List<int>();var parent=wrist;
                for(var joint=1;joint<=(finger==thumbNumber?3:4);joint++)
                {
                    var pattern=$"^finger0*{finger}0*{joint}{suffix}$";
                    var children=rig.ChildrenOf(parent).Where(i=>Regex.IsMatch(Name(i),pattern)).ToArray();
                    if(children.Length!=1)break;
                    parent=children[0];chain.Add(parent);
                }
                if(chain.Count!=(finger==thumbNumber?3:4))break;
                // A three-joint thumb must not just be a truncated four-joint finger.
                if(finger==thumbNumber&&rig.ChildrenOf(parent).Any(i=>Regex.IsMatch(Name(i),$"^finger0*{finger}0*4{suffix}$")))break;
                chains.Add(chain.ToArray());
            }
            if(chains.Count!=5)continue;
            var thumb=rig.RestWorld[chains[thumbNumber-1][0]].Pos;
            var radial=System.Numerics.Vector3.DistanceSquared(thumb,rig.RestWorld[chains[thumbNumber==5?3:1][0]].Pos);
            var ulnar=System.Numerics.Vector3.DistanceSquared(thumb,rig.RestWorld[chains[thumbNumber==5?0:4][0]].Pos);
            if(!float.IsFinite(radial)||!float.IsFinite(ulnar)||radial>=ulnar*.8f)continue;
            var roles=thumbNumber==5?new[]{"Pinky","Ring","Middle","Index","Thumb"}:new[]{"Thumb","Index","Middle","Ring","Pinky"};
            if(chains.SelectMany(c=>c).Any(i=>hints[i] is not null))continue;
            for(var finger=0;finger<5;finger++)
                for(var joint=0;joint<chains[finger].Length;joint++)
                    hints[chains[finger][joint]]=new(roles[finger]+(finger!=thumbNumber-1&&joint==0?"Meta":"Prox"),side,"numbered_fps_hands");
            }
        }
    }
    internal static bool IsDigit(string role)=>new[]{"Thumb","Index","Middle","Ring","Pinky"}.Any(role.StartsWith);
    private static bool IsArm(string role)=>role.StartsWith("Clavicle")||role.StartsWith("UpperArm")||role.StartsWith("LowerArm");
}
