#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidHandRetargeter.Calibration;
using HumanoidHandRetargeter.Mapping;
using HumanoidHandRetargeter.Maths;
using HumanoidHandRetargeter.Retargeting;
using HumanoidHandRetargeter.Skeleton;
using SkeletonModel = HumanoidHandRetargeter.Skeleton.Skeleton;

namespace HumanoidHandRetargeter.Sources;

/// <summary>
/// MANO hands (the parametric hand model of Romero et al., used by grasp generators such as
/// GrabNet) as a retargeting source. A MANO hand has 16 joints - the wrist, then index, middle,
/// pinky, ring and thumb with three joints each - whose rest pose carries no rotation, so a MANO
/// pose's joint rotations are exactly the local rotations of this skeleton. Fingertips (5 mesh
/// vertices) end each chain. Positions are in meters (converted to the library's centimeters).
/// </summary>
public static class ManoHand
{
    /// <summary>MANO joint order.</summary>
    public static readonly string[] JointNames =
    {
        "wrist", "index1", "index2", "index3", "middle1", "middle2", "middle3", "pinky1", "pinky2", "pinky3",
        "ring1", "ring2", "ring3", "thumb1", "thumb2", "thumb3",
    };

    /// <summary>MANO's parent of each joint (the wrist is the root).</summary>
    public static readonly int[] Parents = { -1, 0, 1, 2, 0, 4, 5, 0, 7, 8, 0, 10, 11, 0, 13, 14 };

    /// <summary>Fingertip order (thumb, index, middle, ring, pinky), matching MANO's tip vertices.</summary>
    public static readonly string[] TipFingers = { "thumb", "index", "middle", "ring", "pinky" };

    static readonly (DigitRole Role, string Name)[] Digits =
    {
        (DigitRole.Thumb, "thumb"), (DigitRole.Index, "index"), (DigitRole.Middle, "middle"),
        (DigitRole.Ring, "ring"), (DigitRole.Pinky, "pinky"),
    };

    static string Side(HandSide side) => side == HandSide.Left ? "l" : "r";

    /// <summary>The bone name of a MANO joint for a side ("mano_r_index2").</summary>
    public static string BoneName(HandSide side, string joint) => $"mano_{Side(side)}_{joint}";

    /// <summary>
    /// The skeleton of a MANO hand from its rest joints (16, meters) and rest fingertips (5, meters;
    /// thumb, index, middle, ring, pinky). Rest rotations are identity, as in MANO.
    /// </summary>
    public static SkeletonModel Skeleton(HandSide side, IReadOnlyList<Vector3> restJoints, IReadOnlyList<Vector3> restTips)
    {
        if (restJoints.Count != 16)
            throw new ArgumentException("A MANO hand has 16 joints.", nameof(restJoints));
        if (restTips.Count != 5)
            throw new ArgumentException("A MANO hand has 5 fingertips.", nameof(restTips));
        var bones = new List<BoneDefinition>();
        for (var j = 0; j < 16; j++)
        {
            var parent = Parents[j];
            var offset = parent < 0 ? restJoints[j] : restJoints[j] - restJoints[parent];
            bones.Add(new BoneDefinition(BoneName(side, JointNames[j]), parent < 0 ? null : BoneName(side, JointNames[parent]),
                new XForm(offset * 100f, Quaternion.Identity)));
        }
        for (var t = 0; t < 5; t++)
        {
            var last = Array.IndexOf(JointNames, TipFingers[t] + "3");
            bones.Add(new BoneDefinition(BoneName(side, TipFingers[t] + "_tip"), BoneName(side, JointNames[last]),
                new XForm((restTips[t] - restJoints[last]) * 100f, Quaternion.Identity)));
        }
        return SkeletonModel.Create(bones);
    }

    /// <summary>The hand's mapping (wrist, five digits of three segments and a tip).</summary>
    public static HandRigDefinition Definition(HandSide side, SkeletonModel skeleton)
    {
        int Find(string joint) => skeleton.IndexOf(BoneName(side, joint));
        var digits = Digits.Select(d => new DigitChain(d.Role,
            new[] { Find(d.Name + "1"), Find(d.Name + "2"), Find(d.Name + "3") }, tip: Find(d.Name + "_tip")));
        return new HandRigDefinition(side, Find("wrist"), digits);
    }

    /// <summary>
    /// A MANO pose as a pose of <see cref="Skeleton"/>: the 16 joint rotations (the wrist's is the
    /// global orientation) become the joints' local rotations.
    /// </summary>
    public static Pose Pose(SkeletonModel skeleton, HandSide side, IReadOnlyList<Quaternion> rotations)
    {
        if (rotations.Count != 16)
            throw new ArgumentException("A MANO pose has 16 joint rotations.", nameof(rotations));
        var locals = Enumerable.Range(0, skeleton.Count).Select(i => skeleton[i].RestLocal).ToArray();
        for (var j = 0; j < 16; j++)
        {
            var bone = skeleton.IndexOf(BoneName(side, JointNames[j]));
            locals[bone] = new XForm(locals[bone].Pos, Quaternion.Normalize(rotations[j]));
        }
        return new Pose(locals);
    }

    /// <summary>
    /// A right hand's rest joints, tips and joint rotations mirrored into a left hand (across the
    /// X = 0 plane): how a right-hand grasp generator poses a left hand.
    /// </summary>
    public static (Vector3[] Joints, Vector3[] Tips, Quaternion[] Rotations) MirrorToLeft(
        IReadOnlyList<Vector3> joints, IReadOnlyList<Vector3> tips, IReadOnlyList<Quaternion> rotations)
    {
        static Vector3 M(Vector3 v) => new(-v.X, v.Y, v.Z);
        // A reflection M turns a rotation R into M R M: the axis is reflected and the angle flips,
        // which for a quaternion is (x, -y, -z, w).
        static Quaternion Q(Quaternion q) => new(q.X, -q.Y, -q.Z, q.W);
        return (joints.Select(M).ToArray(), tips.Select(M).ToArray(), rotations.Select(Q).ToArray());
    }
}

/// <summary>
/// One call for other libraries (plain arrays, easy to reach by reflection): retargets a MANO hand
/// pose onto a target skeleton's hand and returns the target's local transforms.
/// </summary>
public static class ManoRetargetApi
{
    /// <summary>
    /// Retargets one MANO hand pose onto a target hand.
    /// <para><paramref name="leftHand"/>: which of the target's hands (a left MANO hand is a mirrored right one).</para>
    /// <para><paramref name="restJoints"/>: 16 x (x, y, z) MANO rest joints, meters (right hand; mirrored for a left).</para>
    /// <para><paramref name="restTips"/>: 5 x (x, y, z) rest fingertips, meters (thumb, index, middle, ring, pinky).</para>
    /// <para><paramref name="rotations"/>: 16 x (x, y, z, w) joint rotations of the right-hand pose.</para>
    /// <para>The target: bone names, parent names ("" for roots), and rest locals 7 per bone
    /// (px, py, pz, qx, qy, qz, qw; any unit).</para>
    /// Returns the target's local transforms, 7 per bone in the same order (bones the hand does not
    /// move keep their rest local). Throws <see cref="InvalidOperationException"/> with a plain
    /// message when either hand cannot be mapped.
    /// </summary>
    public static float[] Retarget(bool leftHand, float[] restJoints, float[] restTips, float[] rotations,
        string[] targetBones, string[] targetParents, float[] targetRestLocals)
    {
        if (restJoints.Length != 48 || restTips.Length != 15 || rotations.Length != 64)
            throw new InvalidOperationException("A MANO hand needs 16 rest joints, 5 rest tips and 16 rotations.");
        if (targetParents.Length != targetBones.Length || targetRestLocals.Length != targetBones.Length * 7)
            throw new InvalidOperationException("The target's names, parents and rest locals do not match.");
        var joints = Enumerable.Range(0, 16).Select(i => new Vector3(restJoints[i * 3], restJoints[i * 3 + 1], restJoints[i * 3 + 2])).ToArray();
        var tips = Enumerable.Range(0, 5).Select(i => new Vector3(restTips[i * 3], restTips[i * 3 + 1], restTips[i * 3 + 2])).ToArray();
        var quats = Enumerable.Range(0, 16).Select(i => new Quaternion(rotations[i * 4], rotations[i * 4 + 1], rotations[i * 4 + 2], rotations[i * 4 + 3])).ToArray();
        var side = leftHand ? HandSide.Left : HandSide.Right;
        if (leftHand)
            (joints, tips, quats) = ManoHand.MirrorToLeft(joints, tips, quats);

        var source = ManoHand.Skeleton(side, joints, tips);
        var sourceMap = HandRigDetector.Detect(source, new[] { ManoHand.Definition(side, source) });

        var targetDefinitions = new List<BoneDefinition>();
        for (var i = 0; i < targetBones.Length; i++)
        {
            var local = new XForm(new Vector3(targetRestLocals[i * 7], targetRestLocals[i * 7 + 1], targetRestLocals[i * 7 + 2]),
                Quaternion.Normalize(new Quaternion(targetRestLocals[i * 7 + 3], targetRestLocals[i * 7 + 4], targetRestLocals[i * 7 + 5], targetRestLocals[i * 7 + 6])));
            targetDefinitions.Add(new BoneDefinition(targetBones[i], string.IsNullOrEmpty(targetParents[i]) ? null : targetParents[i], local));
        }
        var target = SkeletonModel.Create(targetDefinitions);
        var targetMap = HandRigDetector.Detect(target);
        var targetHand = targetMap.Hands.FirstOrDefault(h => h.Side == side)
            ?? throw new InvalidOperationException($"The target has no {side.ToString().ToLowerInvariant()} hand the retargeter recognizes.");
        var profile = HandRetargetProfile.Calibrate(source, sourceMap, target,
            HandRigDetector.Detect(target, new[] { targetHand }));
        var result = HandRetargeter.RetargetPose(profile, ManoHand.Pose(source, side, quats));

        // Back in the caller's bone order.
        var output = new float[targetBones.Length * 7];
        for (var i = 0; i < targetBones.Length; i++)
        {
            var at = target.IndexOf(targetBones[i]);
            var local = at >= 0 ? result.Locals[at] : targetDefinitions[i].RestLocal;
            output[i * 7] = local.Pos.X;
            output[i * 7 + 1] = local.Pos.Y;
            output[i * 7 + 2] = local.Pos.Z;
            output[i * 7 + 3] = local.Rot.X;
            output[i * 7 + 4] = local.Rot.Y;
            output[i * 7 + 5] = local.Rot.Z;
            output[i * 7 + 6] = local.Rot.W;
        }
        return output;
    }
}
