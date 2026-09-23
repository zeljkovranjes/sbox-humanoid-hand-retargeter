#nullable enable
using System.Numerics;
using HumanoidHandRetargeter.Mapping;
using HumanoidHandRetargeter.Maths;
using HumanoidHandRetargeter.Skeleton;
using Skel=HumanoidHandRetargeter.Skeleton.Skeleton;
namespace HumanoidHandRetargeter.Calibration;
using Vector3=System.Numerics.Vector3;

/// <summary>A fixed eye origin shared by weapon-space baking and the preview, in centimeters.</summary>
public static class HandViewSpace
{
    internal static Vector3 ClearElbowFold(Vector3 shoulder,Vector3 elbow,Vector3 wrist,Vector3 grip)
    {
        var upper=Vector3.Distance(shoulder,elbow);var lower=Vector3.Distance(elbow,wrist);
        // An interior angle of 65 degrees leaves clearance at a sharply bent elbow.
        // Move backward along the FPS view axis, preserving shoulder width/height
        // and both segment lengths. The wrist remains constrained to the weapon.
        var minimumSquared=upper*upper+lower*lower-2*upper*lower*MathF.Cos(65*MathF.PI/180);
        var delta=grip-shoulder;
        var back=MathF.Sqrt(MathF.Max(0,minimumSquared-delta.Y*delta.Y-delta.Z*delta.Z));
        // Keep the open shoulder behind the grip even if it passes behind the
        // original root; choosing a branch by distance alone would jump there.
        return new Vector3(MathF.Min(shoulder.X,grip.X-back),shoulder.Y,shoulder.Z);
    }

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

    /// <summary>Rotation taking a file's native axes to the shared X-forward, Y-left, Z-up view space.
    /// Up comes from the file; left from the mapped arms (their sides are named), so the
    /// facing never depends on exporter front-axis conventions.</summary>
    public static Quaternion ViewRotation(SourceScene scene,HandMappingResult mapping)
    {
        ArgumentNullException.ThrowIfNull(scene);ArgumentNullException.ThrowIfNull(mapping);
        Vector3 Axis(int axis,int sign)=>(axis switch{0=>Vector3.UnitX,1=>Vector3.UnitY,_=>Vector3.UnitZ})*(sign<0?-1:1);
        var up=Axis(scene.UpAxis,scene.UpAxisSign);
        var rig=scene.Skeleton;
        Vector3? Root(HandSide side)=>mapping.Hands.FirstOrDefault(h=>h.Side==side) is {} hand
            ?rig.RestWorld[hand.Clavicle??hand.UpperArm??hand.Forearm??hand.Wrist].Pos:null;
        var left=Root(HandSide.Left) is {} l&&Root(HandSide.Right) is {} r?l-r:Vector3.Zero;
        left-=up*Vector3.Dot(left,up);
        // One-sided rigs: FBX front is where the character faces, so left = up x front.
        if(left.LengthSquared()<1e-6f)left=Vector3.Cross(up,Axis(scene.FrontAxis,scene.FrontAxisSign));
        if(!HandFrames.TryBasis(Vector3.Cross(left,up),up,out var frame))return Quaternion.Identity;
        return MathQ.Normalize(Quaternion.Conjugate(frame));
    }

    /// <summary>The same scene with its roots turned into view space. Bone order, locals below the
    /// roots and therefore mappings are unchanged.</summary>
    public static SourceScene ToViewAxes(SourceScene scene,HandMappingResult mapping)
    {
        var rotation=ViewRotation(scene,mapping);
        if(MathQ.AngleBetween(rotation,Quaternion.Identity)<1e-5f&&scene.UpAxis==2)return scene;
        XForm Turn(XForm value)=>new(Vector3.Transform(value.Pos,rotation),MathQ.Normalize(rotation*value.Rot));
        Skel Rotate(Skel rig)=>Skel.Create(rig.Bones.Select(b=>new BoneDefinition(b.Name,b.ParentIndex<0?null:rig[b.ParentIndex].Name,
            b.ParentIndex<0?Turn(b.RestLocal):b.RestLocal)).ToArray());
        var roots=scene.Skeleton.Bones.Where(b=>b.ParentIndex<0).Select(b=>b.Index).ToArray();
        var clips=scene.Clips.Select(c=>new Clip(c.Name,c.Fps,c.Looping,c.Frames.Select(f=>
        {
            var frame=f.ToArray();foreach(var root in roots)frame[root]=Turn(frame[root]);return frame;
        }).ToList(),c.NativeFps)).ToArray();
        return new SourceScene(Rotate(scene.Skeleton),clips,scene.UnitScaleCm,upAxis:2,frontAxis:0,coordAxis:1,
            originalUpAxis:scene.OriginalUpAxis,notes:scene.Notes)
            {MidPoseBindSkeleton=scene.MidPoseBindSkeleton is {} bind?Rotate(bind):null};
    }

    public static Vector3 EyePosition(Skel rig,HandMappingResult mapping)
    {
        int Find(string name)=>rig.Bones.FirstOrDefault(b=>b.Name.Equals(name,StringComparison.OrdinalIgnoreCase)) is {Name:not null} bone?bone.Index:-1;
        foreach(var name in new[]{"camera","view_camera","camera_root"})
            if(Find(name) is var camera and >=0)return rig.RestWorld[camera].Pos;
        // Rigify's "eyes"/"eye.L" are look-at controls far in front of the face;
        // the ORG/DEF layers hold the actual eyeballs.
        foreach(var prefix in new[]{"ORG-","DEF-"})
            if(Find(prefix+"eye.L") is var left and >=0&&Find(prefix+"eye.R") is var right and >=0)
                return (rig.RestWorld[left].Pos+rig.RestWorld[right].Pos)*.5f;
        if(Find("eyes") is var eyes and >=0&&Find("MCH-eyes_parent")<0)return rig.RestWorld[eyes].Pos;
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
