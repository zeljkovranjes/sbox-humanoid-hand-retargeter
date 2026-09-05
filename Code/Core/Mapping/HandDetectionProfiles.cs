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
        return hints;
    }
    internal static bool IsDigit(string role)=>new[]{"Thumb","Index","Middle","Ring","Pinky"}.Any(role.StartsWith);
    private static bool IsArm(string role)=>role.StartsWith("Clavicle")||role.StartsWith("UpperArm")||role.StartsWith("LowerArm");
}
