#nullable enable
using System.Numerics;
using HumanoidHandRetargeter.Mapping;
using Skel=HumanoidHandRetargeter.Skeleton.Skeleton;
namespace HumanoidHandRetargeter.Calibration;
using Vector3=System.Numerics.Vector3;

/// <summary>A fixed eye origin shared by weapon-space baking and the preview, in centimeters.</summary>
public static class HandViewSpace
{
    internal static IReadOnlyDictionary<int,Vector3> FreeShoulderPositions(Skel rig,HandMappingResult mapping)
    {
        var result=new Dictionary<int,Vector3>();
        var left=mapping.Hands.SingleOrDefault(h=>h.Side==HandSide.Left);
        var right=mapping.Hands.SingleOrDefault(h=>h.Side==HandSide.Right);
        if(left?.UpperArm is not int l||right?.UpperArm is not int r
            ||rig[l].ParentIndex>=0||rig[r].ParentIndex>=0)return result;
        var a=rig.RestWorld[l].Pos;var b=rig.RestWorld[r].Pos;var lateral=a-b;
        // Correct a clear quarter-turn export convention only for detached arms.
        // A body-owned shoulder must stay attached to its authored torso.
        if(lateral.LengthSquared()<1e-8f||MathF.Abs(lateral.X)/lateral.Length()<.9f)return result;
        var yaw=MathF.PI*.5f-MathF.Atan2(lateral.Y,lateral.X);
        var rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitZ,yaw);var center=(a+b)*.5f;
        result[l]=center+Vector3.Transform(a-center,rotation);
        result[r]=center+Vector3.Transform(b-center,rotation);
        return result;
    }

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
