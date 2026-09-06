#nullable enable
using Vec = System.Numerics.Vector3;
using Skel = HumanoidHandRetargeter.Skeleton.Skeleton;

namespace HumanoidHandRetargeter.Mapping;

internal static partial class HandDetectionProfiles
{
    // Split biceps/forearms, a wrist-to-palm link, and five palm-rooted rays.
    // Numeric/duplicate suffixes identify objects, not sides or anatomical fingers.
    private static void ResolveUnlabelledSplitArms(Skel rig,Hint?[] hints)
    {
        string Name(int i)
        {
            var name=rig[i].Name[(rig[i].Name.LastIndexOf(':')+1)..];
            return Key(name.Split('#')[0].Replace("_duplicate","",StringComparison.OrdinalIgnoreCase)).TrimEnd("0123456789".ToCharArray());
        }
        int Child(int i,string name)=>rig.ChildrenOf(i).Count==1&&Name(rig.ChildrenOf(i)[0])==name?rig.ChildrenOf(i)[0]:-1;
        var proposals=new List<(HandSide Side,Dictionary<int,string> Roles)>();
        foreach(var upper in rig.Bones.Where(b=>Name(b.Index) is "bisep" or "bicep" or "biceps"))
        {
            var upperSplit=Child(upper.Index,Name(upper.Index));if(upperSplit<0)continue;
            var lower=Child(upperSplit,"forearm");if(lower<0)continue;
            var lowerSplit=Child(lower,"forearm");if(lowerSplit<0)continue;
            var wrist=Child(lowerSplit,"wrist");if(wrist<0)continue;
            var palm=Child(wrist,"hand");if(palm<0||rig.ChildrenOf(palm).Count!=5)continue;
            var rays=new List<int[]>();
            foreach(var root in rig.ChildrenOf(palm))
            {
                var ray=new List<int>{root};var current=root;
                while(rig.ChildrenOf(current).Count==1){current=rig.ChildrenOf(current)[0];ray.Add(current);}
                if(rig.ChildrenOf(current).Count!=0){rays.Clear();break;}
                if(Name(ray[^1]).Contains("end",StringComparison.Ordinal))ray.RemoveAt(ray.Count-1);
                rays.Add(ray.ToArray());
            }
            if(rays.Count!=5)continue;
            var thumbs=rays.Where(r=>r.Length==3&&Name(r[0]) is "thum" or "thumb").ToArray();
            var fingers=rays.Where(r=>r.Length==4&&Name(r[0])=="finger").ToArray();
            if(thumbs.Length!=1||fingers.Length!=4)continue;
            var thumb=thumbs[0];
            Vec Pos(int i)=>rig.RestWorld[i].Pos;
            var ordered=fingers.OrderBy(r=>Vec.DistanceSquared(Pos(r[1]),Pos(thumb[1]))).ToArray();
            var radial=Pos(ordered[0][1])-Pos(ordered[3][1]);
            if(radial.LengthSquared()<1e-8f)continue;
            radial=Vec.Normalize(radial);
            // Require four distinctly ordered knuckles, not just a nearest-thumb guess.
            var spacing=ordered.Select(r=>Vec.Dot(Pos(r[1])-Pos(ordered[3][1]),radial)).ToArray();
            if(Enumerable.Range(0,3).Any(i=>spacing[i]-spacing[i+1]<spacing[0]*.1f))continue;
            var forward=ordered.Select(r=>Pos(r[3])-Pos(r[1])).Aggregate(Vec.Zero,(a,b)=>a+b);
            var normal=Vec.Cross(forward,radial);var thumbBend=Pos(thumb[2])-Pos(thumb[1]);
            if(normal.LengthSquared()<1e-8f||thumbBend.LengthSquared()<1e-8f)continue;
            var chirality=Vec.Dot(Vec.Normalize(normal),Vec.Normalize(thumbBend));
            // An out-of-plane thumb bend supplies the palmar direction. Flat or
            // ambiguous geometry remains for review rather than guessing a side.
            if(MathF.Abs(chirality)<.25f)continue;
            var side=chirality>0?HandSide.Left:HandSide.Right;
            var roles=new Dictionary<int,string>{{upper.Index,"UpperArm"},{lower,"LowerArm"},{wrist,"Hand"},{palm,"PalmMeta"}};
            var digitNames=new[]{"Index","Middle","Ring","Pinky"};
            for(var digit=0;digit<4;digit++)
                for(var joint=0;joint<4;joint++)roles[ordered[digit][joint]]=digitNames[digit]+(joint==0?"Meta":"Prox");
            foreach(var bone in thumb)roles[bone]="ThumbProx";
            if(roles.Keys.Any(i=>hints[i] is not null))continue;
            proposals.Add((side,roles));
        }
        // Require a complete, disjoint bilateral pair before using unnamed sides.
        if(proposals.Count!=2||proposals[0].Side==proposals[1].Side||proposals[0].Roles.Keys.Intersect(proposals[1].Roles.Keys).Any())return;
        foreach(var proposal in proposals)
            foreach(var role in proposal.Roles)hints[role.Key]=new(role.Value,proposal.Side,"split_arm_palm_rays");
    }
}
