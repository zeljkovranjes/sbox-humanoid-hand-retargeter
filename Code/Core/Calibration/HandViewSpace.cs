#nullable enable
using System.Numerics;
using HumanoidHandRetargeter.Mapping;
using Skel=HumanoidHandRetargeter.Skeleton.Skeleton;
namespace HumanoidHandRetargeter.Calibration;
using Vector3=System.Numerics.Vector3;

/// <summary>A fixed eye origin shared by weapon-space baking and the preview, in centimeters.</summary>
public static class HandViewSpace
{
    public static Vector3 EyePosition(Skel rig,HandMappingResult mapping)
    {
        foreach(var name in new[]{"camera","view_camera","camera_root","eyes"})
        {
            var bone=rig.Bones.FirstOrDefault(b=>b.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
            if(bone.Name is not null)return rig.RestWorld[bone.Index].Pos;
        }
        var head=rig.Bones.FirstOrDefault(b=>b.Name.Equals("head",StringComparison.OrdinalIgnoreCase));
        if(head.Name is not null)return rig.RestWorld[head.Index].Pos+Vector3.UnitZ*7.62f;
        var arms=mapping.Hands.Where(h=>h.UpperArm.HasValue).ToArray();
        if(arms.Length>0)
        {
            var center=arms.Aggregate(Vector3.Zero,(sum,h)=>sum+rig.RestWorld[h.UpperArm!.Value].Pos)/arms.Length;
            var length=arms.Average(h=>Vector3.Distance(rig.RestWorld[h.UpperArm!.Value].Pos,rig.RestWorld[h.Wrist].Pos));
            return center+Vector3.UnitZ*(length*.3f)-Vector3.UnitX*(length*.05f);
        }
        var wrists=mapping.Hands.Select(h=>rig.RestWorld[h.Wrist].Pos).ToArray();
        return (wrists.Length==0?Vector3.Zero:wrists.Aggregate(Vector3.Zero,(sum,p)=>sum+p)/wrists.Length)
            -Vector3.UnitX*30.48f+Vector3.UnitZ*10.16f;
    }
}
