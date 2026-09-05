#nullable enable
using HumanoidHandRetargeter.Mapping;
using SkeletonModel = HumanoidHandRetargeter.Skeleton.Skeleton;

namespace HumanoidHandRetargeter.Validation;

public sealed record RigIssue(string Code, string Message, HandSide? Side = null, int? Bone = null);

/// <summary>Shared validation for automatic/manual mapping, batch processing and the editor.</summary>
public static class HandRigValidator
{
    public static IReadOnlyList<RigIssue> Validate(SkeletonModel skeleton, IEnumerable<HandRigDefinition> hands)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(hands);
        var issues = new List<RigIssue>();
        var claimed = new HashSet<int>();
        var sides = new HashSet<HandSide>();
        var count = 0;

        foreach (var bone in skeleton.Bones)
        {
            if (!PoseValidator.ValidTransform(bone.RestLocal))
                issues.Add(new("invalid-rest", $"Bone '{bone.Name}' has an invalid rest transform.", Bone: bone.Index));
            var world = skeleton.RestWorld[bone.Index].Pos;
            if (!float.IsFinite(world.X) || !float.IsFinite(world.Y) || !float.IsFinite(world.Z))
                issues.Add(new("invalid-world-rest", $"Bone '{bone.Name}' has an invalid accumulated rest position.", Bone: bone.Index));
        }

        foreach (var hand in hands)
        {
            count++;
            if (hand is null)
            {
                issues.Add(new("missing-hand", "A hand mapping is missing."));
                continue;
            }
            if (!Enum.IsDefined(typeof(HandSide), hand.Side))
                issues.Add(new("invalid-side", "Choose left or right for the hand."));
            if (!sides.Add(hand.Side))
                issues.Add(new("duplicate-side", $"More than one {hand.Side} hand is mapped.", hand.Side));

            var arm = new[] { hand.Clavicle, hand.UpperArm, hand.Forearm, hand.Wrist }
                .Where(b => b.HasValue).Select(b => b!.Value).ToArray();
            CheckChain(arm, hand.Side, "arm", allowZeroLength: true);
            var roles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var digit in hand.Digits)
            {
                if (digit is null)
                {
                    issues.Add(new("missing-digit", "A digit mapping is missing.", hand.Side));
                    continue;
                }
                var key = digit.Role == DigitRole.Extra ? "Extra:" + digit.ExtraSlot : digit.Role.ToString();
                if (!Enum.IsDefined(typeof(DigitRole), digit.Role)
                    || (digit.Role == DigitRole.Extra && string.IsNullOrWhiteSpace(digit.ExtraSlot)))
                    issues.Add(new("invalid-digit-role", "Choose a digit role or name its extra slot.", hand.Side));
                if (!roles.Add(key))
                    issues.Add(new("duplicate-digit-role", $"{hand.Side} {key} is mapped more than once.", hand.Side));
                if (digit.Segments.Count == 0)
                    issues.Add(new("empty-digit", $"{hand.Side} {key} needs at least one segment.", hand.Side));
                var chain = digit.Bones.ToArray();
                CheckChain(chain, hand.Side, key, allowZeroLength: false, tip: digit.Tip);
                if (chain.Length > 0 && Valid(chain[0]) && Valid(hand.Wrist)
                    && !skeleton.DescendsFrom(chain[0], hand.Wrist))
                    issues.Add(new("wrong-wrist", $"{hand.Side} {key} must descend from its wrist.", hand.Side, chain[0]));
            }
            foreach (var helper in hand.TwistOrHelperBones)
            {
                Claim(helper, hand.Side);
                var armRoot = arm[0];
                if (Valid(helper) && Valid(armRoot) && !skeleton.DescendsFrom(helper, armRoot))
                    issues.Add(new("wrong-helper-root", $"{hand.Side} helper must belong to its mapped arm or hand hierarchy.", hand.Side, helper));
            }
        }
        if (count == 0)
            issues.Add(new("no-hands", "Map at least one wrist before retargeting."));
        return issues.AsReadOnly();

        bool Valid(int bone) => bone >= 0 && bone < skeleton.Count;
        void Claim(int bone, HandSide side)
        {
            if (!Valid(bone))
                issues.Add(new("invalid-bone", $"{side} mapping references a missing bone.", side, bone));
            else if (!claimed.Add(bone))
                issues.Add(new("overlapping-bone", $"Bone '{skeleton[bone].Name}' is assigned to multiple roles.", side, bone));
        }
        void CheckChain(int[] bones, HandSide side, string role, bool allowZeroLength, int? tip = null)
        {
            for (var i = 0; i < bones.Length; i++)
            {
                Claim(bones[i], side);
                if (i == 0 || !Valid(bones[i - 1]) || !Valid(bones[i])) continue;
                if (!skeleton.DescendsFrom(bones[i], bones[i - 1]))
                    issues.Add(new("chain-order", $"{side} {role} joints must follow their hierarchy.", side, bones[i]));
                if (!allowZeroLength && bones[i] != tip &&
                    System.Numerics.Vector3.DistanceSquared(skeleton.RestWorld[bones[i - 1]].Pos,
                        skeleton.RestWorld[bones[i]].Pos) < 1e-12f)
                    issues.Add(new("zero-length-digit", $"{side} {role} has coincident joints; correct the chain or classify a helper.", side, bones[i]));
            }
        }
    }
}
