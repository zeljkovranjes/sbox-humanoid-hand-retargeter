#nullable enable
using System.Numerics;
using HumanoidHandRetargeter.Mapping;
using HumanoidHandRetargeter.Maths;
using SkeletonModel = HumanoidHandRetargeter.Skeleton.Skeleton;

namespace HumanoidHandRetargeter.Calibration;
using Vector3 = System.Numerics.Vector3;

/// <summary>HandGeometry/CanonicalFrames specialization from legacy commit 26084c9.
/// Frames use X along the joint, Z dorsal, Y the curl hinge. Local bone axes are irrelevant.</summary>
internal static class HandFrames
{
    internal static bool TryPalm(SkeletonModel skeleton, HandRigDefinition hand, out Quaternion frame)
    {
        frame = Quaternion.Identity;
        var fingers = hand.Digits.Where(d => d.Role is DigitRole.Index or DigitRole.Middle or DigitRole.Ring or DigitRole.Pinky)
            .OrderBy(d => d.Role).ToArray();
        Vector3 first, last;
        if (fingers.Length >= 2)
        {
            first = skeleton.RestWorld[fingers[0].Segments[0]].Pos;
            last = skeleton.RestWorld[fingers[^1].Segments[0]].Pos;
        }
        else
        {
            // A confirmed generic/extra chain (e.g. a mitten rig) can still define a
            // palm geometrically. Use the thumb to identify the radial end; never
            // assign an anatomical digit role just to obtain a frame.
            var thumb = hand.Digits.FirstOrDefault(d => d.Role == DigitRole.Thumb);
            var nonThumb = hand.Digits.Where(d => d.Role != DigitRole.Thumb).ToArray();
            if (thumb is null || nonThumb.Length < 2) return false;
            first = last = Vector3.Zero;
            var span = 0f;
            foreach (var a in nonThumb)
                foreach (var b in nonThumb)
                {
                    var pa = skeleton.RestWorld[a.Segments[0]].Pos;
                    var pb = skeleton.RestWorld[b.Segments[0]].Pos;
                    if (Vector3.DistanceSquared(pa, pb) <= span) continue;
                    span = Vector3.DistanceSquared(pa, pb);
                    first = pa;
                    last = pb;
                }
            var thumbPosition = skeleton.RestWorld[thumb.Segments[0]].Pos;
            var radialEvidence = Vector3.DistanceSquared(first, thumbPosition) - Vector3.DistanceSquared(last, thumbPosition);
            if (MathF.Abs(radialEvidence) < span * 1e-4f) return false;
            if (radialEvidence > 0) (first, last) = (last, first);
        }
        var middle = hand.Digits.Aggregate(Vector3.Zero, (p, d) => p + skeleton.RestWorld[d.Segments[0]].Pos) / hand.Digits.Count;
        var forward = middle - skeleton.RestWorld[hand.Wrist].Pos;
        var dorsal = Vector3.Cross(first - last, forward) * (hand.Side == HandSide.Left ? 1f : -1f);
        return TryBasis(forward, dorsal, out frame);
    }

    internal static bool TryBasis(Vector3 primary, Vector3 dorsal, out Quaternion frame)
    {
        frame = Quaternion.Identity;
        if (!Finite(primary) || !Finite(dorsal) || !float.IsFinite(primary.LengthSquared()) || !float.IsFinite(dorsal.LengthSquared())
            || primary.LengthSquared() < 1e-12f || dorsal.LengthSquared() < 1e-12f) return false;
        var x = Vector3.Normalize(primary);
        var z = Vector3.Normalize(dorsal);
        z -= x * Vector3.Dot(z, x);
        if (z.LengthSquared() < 1e-8f) return false;
        z = Vector3.Normalize(z);
        var y = Vector3.Cross(z, x);
        // Same System.Numerics row-vector basis convention as legacy CanonicalFrames.
        frame = MathQ.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1)));
        return true;
    }

    internal static int[] Joints(DigitChain digit) => (digit.Metacarpal is int meta ? new[] { meta } : Array.Empty<int>())
        .Concat(digit.Segments).ToArray();

    internal static Vector3 Direction(SkeletonModel skeleton, HandRigDefinition hand, DigitChain digit, int[] joints, int i)
    {
        var current = skeleton.RestWorld[joints[i]].Pos;
        if (i + 1 < joints.Length) return skeleton.RestWorld[joints[i + 1]].Pos - current;
        if (digit.Tip is int tip && (skeleton.RestWorld[tip].Pos - current).LengthSquared() > 1e-12f)
            return skeleton.RestWorld[tip].Pos - current;
        // Legacy distal extrapolation: no fabricated terminal bone or assumption about skin weights.
        return current - skeleton.RestWorld[i > 0 ? joints[i - 1] : hand.Wrist].Pos;
    }

    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
