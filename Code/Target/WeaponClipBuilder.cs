#nullable enable
using System.Numerics;
using HumanoidHandRetargeter.Maths;
using HumanoidHandRetargeter.Skeleton;
using Skel=HumanoidHandRetargeter.Skeleton.Skeleton;
namespace HumanoidHandRetargeter.Target;
using Vector3=System.Numerics.Vector3;

/// <summary>One animation stream containing original weapon bones and namespaced custom arm bones.</summary>
public static class WeaponClipBuilder
{
    public const string HandPrefix="hr_hand_";
    public sealed record Result(Skel Skeleton,Clip Animation);
    public static Result Combine(Skel source,Clip original,Skel target,Clip baked,Vector3 weaponOffset,string name)
    {
        if(original.FrameCount!=baked.FrameCount||MathF.Abs(original.Fps-baked.Fps)>.001f)
            throw new ArgumentException("Weapon and hand animation timing must match.");
        if(source.Bones.Any(b=>b.Name.StartsWith(HandPrefix,StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Source bone names conflict with the generated hand namespace.");
        var definitions=source.Bones.Select(b=>new BoneDefinition(b.Name,b.ParentIndex<0?null:source[b.ParentIndex].Name,b.RestLocal)).ToList();
        definitions.AddRange(target.Bones.Select(b=>new BoneDefinition(HandPrefix+b.Name,b.ParentIndex<0?null:HandPrefix+target[b.ParentIndex].Name,
            new XForm(b.RestLocal.Pos-(b.ParentIndex<0?weaponOffset:Vector3.Zero),b.RestLocal.Rot))));
        var rig=Skel.Create(definitions);
        var sourceIndices=source.Bones.Select(b=>rig.IndexOf(b.Name)).ToArray();
        var targetIndices=target.Bones.Select(b=>rig.IndexOf(HandPrefix+b.Name)).ToArray();
        var frames=new List<XForm[]>();
        for(var f=0;f<baked.FrameCount;f++)
        {
            var locals=new XForm[rig.Count];
            for(var i=0;i<source.Count;i++)locals[sourceIndices[i]]=original.Frames[f][i];
            for(var i=0;i<target.Count;i++)
            {
                var value=baked.Frames[f][i];
                locals[targetIndices[i]]=new XForm(value.Pos-(target[i].ParentIndex<0?weaponOffset:Vector3.Zero),value.Rot);
            }
            frames.Add(locals);
        }
        return new(rig,new Clip(name,baked.Fps,baked.Looping,frames,baked.NativeFps));
    }
}
