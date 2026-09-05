#nullable enable
using HumanoidHandRetargeter.Maths;
using HumanoidHandRetargeter.Skeleton;
using SkeletonModel = HumanoidHandRetargeter.Skeleton.Skeleton;

namespace HumanoidHandRetargeter.Validation;

public static class PoseValidator
{
    public static IReadOnlyList<RigIssue> Validate(SkeletonModel skeleton, Pose pose)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(pose);
        var errors = new List<RigIssue>();
        if (pose.Locals.Length != skeleton.Count)
            errors.Add(new("pose-size", $"Pose has {pose.Locals.Length} bones; expected {skeleton.Count}."));
        else
            for (var i = 0; i < pose.Locals.Length; i++)
                if (!ValidTransform(pose.Locals[i]))
                    errors.Add(new("pose-transform", $"Bone '{skeleton[i].Name}' has a non-finite position or invalid rotation.", Bone: i));
        return errors.AsReadOnly();
    }

    internal static bool ValidTransform(XForm value)
    {
        var p = value.Pos;
        var length = value.Rot.LengthSquared();
        return float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)
            && float.IsFinite(length) && MathF.Abs(length - 1f) <= 1e-3f;
    }
}
