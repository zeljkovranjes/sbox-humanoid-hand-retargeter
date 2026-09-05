#nullable enable
using System.Numerics;
using HumanoidHandRetargeter.Mapping;
using HumanoidHandRetargeter.Maths;
using HumanoidHandRetargeter.Validation;
using SkeletonModel = HumanoidHandRetargeter.Skeleton.Skeleton;

namespace HumanoidHandRetargeter.Calibration;
using Vector3 = System.Numerics.Vector3;

/// <summary>Immutable calibration reused for every pose and clip sharing these skeletons.</summary>
public sealed class HandRetargetProfile
{
    public SkeletonModel Source { get; }
    public SkeletonModel Target { get; }
    public IReadOnlyList<string> Notes { get; }
    internal RotationPair[] Pairs { get; }
    internal DigitDistribution[] Distributions { get; }
    internal (int Source,int Target)[] PreservedTracks {get;private set;}=Array.Empty<(int,int)>();
    internal WristMotionPair[] WristMotion { get; private set; } = Array.Empty<WristMotionPair>();

    private HandRetargetProfile(SkeletonModel source, SkeletonModel target, List<RotationPair> pairs,
        List<DigitDistribution> distributions, List<string> notes)
    {
        Source = source;
        Target = target;
        Pairs = pairs.ToArray();
        Distributions = distributions.ToArray();
        Notes = notes.AsReadOnly();
    }

    /// <summary>Build a manual palm frame from model-space forward and dorsal directions.</summary>
    public static bool TryCreatePalmFrame(Vector3 forward, Vector3 dorsal, out Quaternion frame)
        => HandFrames.TryBasis(forward, dorsal, out frame);

    /// <summary>Palm overrides are world-space semantic frames (X forward, Z dorsal), only
    /// needed when geometry cannot determine a palm plane. Ambiguous automatic maps must be corrected first.</summary>
    public static HandRetargetProfile Calibrate(SkeletonModel source, HandMappingResult sourceMap,
        SkeletonModel target, HandMappingResult targetMap,
        IReadOnlyDictionary<HandSide, Quaternion>? sourcePalmFrames = null,
        IReadOnlyDictionary<HandSide, Quaternion>? targetPalmFrames = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sourceMap);
        ArgumentNullException.ThrowIfNull(targetMap);
        var errors = new List<RigIssue>();
        errors.AddRange(HandRigValidator.Validate(source, sourceMap.Hands));
        errors.AddRange(HandRigValidator.Validate(target, targetMap.Hands));
        if (sourceMap.NeedsReview || targetMap.NeedsReview)
            errors.Add(new("mapping-review", "Confirm ambiguous source and target roles in Hand Mapping before calibration."));
        if (errors.Count > 0) throw new RigValidationException(errors);
        var pairs = new List<RotationPair>();
        var distributions = new List<DigitDistribution>();
        var notes = new List<string>();
        var wristMotion = new List<WristMotionPair>();
        var matchedHands = 0;
        foreach (var targetHand in targetMap.Hands)
        {
            var sourceHand = sourceMap.Hands.FirstOrDefault(h => h.Side == targetHand.Side);
            if (sourceHand is null)
            {
                notes.Add($"No source {targetHand.Side} hand; target hand remains at its local rest pose.");
                continue;
            }
            matchedHands++;
            var side = targetHand.Side;
            if (!Palm(source, sourceHand, sourcePalmFrames, "Source", out var sourcePalm)
                | !Palm(target, targetHand, targetPalmFrames, "Target", out var targetPalm)) continue;

            var sourceDorsal = Vector3.Transform(Vector3.UnitZ, sourcePalm);
            var targetDorsal = Vector3.Transform(Vector3.UnitZ, targetPalm);
            float Length(SkeletonModel rig, HandRigDefinition hand)
                => hand.UpperArm is int upper && hand.Forearm is int fore
                    ? Vector3.Distance(rig.RestWorld[upper].Pos, rig.RestWorld[fore].Pos)
                        + Vector3.Distance(rig.RestWorld[fore].Pos, rig.RestWorld[hand.Wrist].Pos)
                    : hand.Digits.SelectMany(d => d.Segments.Take(1)).Select(b => Vector3.Distance(rig.RestWorld[b].Pos, rig.RestWorld[hand.Wrist].Pos)).DefaultIfEmpty(1).Average();
            wristMotion.Add(new(sourceHand.Wrist, targetHand.Wrist, targetHand.UpperArm, targetHand.Forearm,
                MathQ.Normalize(targetPalm * Quaternion.Conjugate(sourcePalm)), Length(target, targetHand) / MathF.Max(Length(source, sourceHand), 1e-5f), targetDorsal));
            var sourceArm = new[] { sourceHand.Clavicle, sourceHand.UpperArm, sourceHand.Forearm, sourceHand.Wrist };
            var targetArm = new[] { targetHand.Clavicle, targetHand.UpperArm, targetHand.Forearm, targetHand.Wrist };
            var previousSource = -1;
            var previousTarget = -1;
            for (var i = 0; i < sourceArm.Length; i++)
            {
                if (sourceArm[i] is not int sb || targetArm[i] is not int tb) continue;
                var sf = sourcePalm;
                var tf = targetPalm;
                if (i < 3)
                {
                    var sn = sourceArm.Skip(i + 1).First(b => b.HasValue)!.Value;
                    var tn = targetArm.Skip(i + 1).First(b => b.HasValue)!.Value;
                    if (!HandFrames.TryBasis(source.RestWorld[sn].Pos - source.RestWorld[sb].Pos, sourceDorsal, out sf)
                        | !HandFrames.TryBasis(target.RestWorld[tn].Pos - target.RestWorld[tb].Pos, targetDorsal, out tf))
                    {
                        errors.Add(new("arm-frame", $"{side} arm frame is degenerate; correct its mapping or rest pose.", side));
                        continue;
                    }
                }
                pairs.Add(new(sb, tb, previousSource, previousTarget, sf, tf));
                previousSource = sb;
                previousTarget = tb;
            }

            foreach (var td in targetHand.Digits)
            {
                var sd = sourceHand.Digits.FirstOrDefault(d => d.Role == td.Role && (d.Role != DigitRole.Extra || d.ExtraSlot == td.ExtraSlot));
                if (sd is null)
                {
                    notes.Add($"No source {side} {td.Role}; target digit remains at its local rest pose.");
                    continue;
                }
                var sj = HandFrames.Joints(sd);
                var tj = HandFrames.Joints(td);
                var sf = Frames(source, sourceHand, sd, sj, sourceDorsal, "Source");
                var tf = Frames(target, targetHand, td, tj, targetDorsal, "Target");
                if (sf is null || tf is null) continue;
                if (sd.Segments.Count == td.Segments.Count && sd.Metacarpal.HasValue == td.Metacarpal.HasValue)
                {
                    for (var i = 0; i < sj.Length; i++)
                        pairs.Add(new(sj[i], tj[i], i > 0 ? sj[i - 1] : sourceHand.Wrist,
                            i > 0 ? tj[i - 1] : targetHand.Wrist, sf[i], tf[i]));
                }
                else if (sd.Segments.Count == 1)
                {
                    // Legacy single-segment policy: move the entire target digit as one ray.
                    pairs.Add(new(sd.Segments[0], tj[0], sourceHand.Wrist, targetHand.Wrist, sf[^1], tf[0]));
                    notes.Add($"{side} {td.Role}: single source segment drives the target digit root; remaining joints keep rest pose.");
                }
                else
                {
                    var targetOffset = td.Metacarpal.HasValue ? 1 : 0;
                    var recipients = td.Segments.ToArray();
                    var sourceLengths = sj.Select((_, i) => HandFrames.Direction(source, sourceHand, sd, sj, i).Length()).ToArray();
                    var targetLengths = recipients.Select((_, i) => HandFrames.Direction(target, targetHand, td, tj, i + targetOffset).Length()).ToArray();
                    distributions.Add(new(sourceHand.Wrist, targetHand.Wrist, sj, recipients, sf,
                        tf.Skip(targetOffset).ToArray(), OverlapWeights(sourceLengths, targetLengths), sd.Metacarpal.HasValue ? 1 : 0));
                    notes.Add($"{side} {td.Role}: curl redistributed by normalized chain length; spread goes to proximal; axial twist is omitted.");
                }
            }
            foreach (var sd in sourceHand.Digits.Where(d => !targetHand.Digits.Any(t => d.Role == t.Role && (d.Role != DigitRole.Extra || d.ExtraSlot == t.ExtraSlot))))
                notes.Add($"Target has no {side} {sd.Role}; source digit skipped.");
        }
        if (matchedHands == 0) errors.Add(new("no-matching-hands", "Source and target have no matching hand side. Correct the mapping before retargeting."));
        if (errors.Count > 0) throw new RigValidationException(errors);
        HashSet<int> Protected(SkeletonModel rig,HandMappingResult mapping)
        {
            var result=mapping.Hands.SelectMany(h=>new int?[]{h.Wrist,h.Clavicle,h.UpperArm,h.Forearm}.Where(i=>i.HasValue).Select(i=>i!.Value).Concat(h.Digits.SelectMany(d=>d.Bones)).Concat(h.TwistOrHelperBones)).ToHashSet();
            foreach(var bone in result.ToArray())for(var parent=rig[bone].ParentIndex;parent>=0;parent=rig[parent].ParentIndex)result.Add(parent);
            return result;
        }
        var sourceProtected=Protected(source,sourceMap);var targetProtected=Protected(target,targetMap);
        var preserved=new List<(int Source,int Target)>();
        foreach(var bone in target.Bones.Where(b=>!targetProtected.Contains(b.Index)))
        {
            var index=source.IndexOf(bone.Name);
            if(index<0||sourceProtected.Contains(index))continue;
            var sourceParent=source[index].ParentIndex<0?null:source[source[index].ParentIndex].Name;
            var targetParent=bone.ParentIndex<0?null:target[bone.ParentIndex].Name;
            if(sourceParent!=targetParent || Vector3.Distance(source.RestWorld[index].Pos,target.RestWorld[bone.Index].Pos)>.01f
                || MathQ.AngleBetween(source.RestWorld[index].Rot,target.RestWorld[bone.Index].Rot)>.001f)continue;
            preserved.Add((index,bone.Index));
        }
        if(preserved.Count>0)notes.Add($"Preserved {preserved.Count} compatible non-hand tracks; target deformation helpers remain target-owned.");
        return new(source, target, pairs, distributions, notes) { WristMotion = wristMotion.ToArray(), PreservedTracks=preserved.ToArray() };

        bool Palm(SkeletonModel skeleton, HandRigDefinition hand, IReadOnlyDictionary<HandSide, Quaternion>? overrides, string label, out Quaternion frame)
        {
            if (overrides is not null && overrides.TryGetValue(hand.Side, out frame))
            {
                if (float.IsFinite(frame.LengthSquared()) && MathF.Abs(frame.LengthSquared() - 1f) < 1e-3f) return true;
            }
            else if (HandFrames.TryPalm(skeleton, hand, out frame)) return true;
            frame = Quaternion.Identity;
            errors.Add(new("palm-frame", $"{label} {hand.Side} palm orientation cannot be calibrated from this geometry. Correct the digit mapping or provide a palm frame.", hand.Side));
            return false;
        }
        Quaternion[]? Frames(SkeletonModel skeleton, HandRigDefinition hand, DigitChain digit, int[] joints, Vector3 dorsal, string label)
        {
            var result = new Quaternion[joints.Length];
            for (var i = 0; i < joints.Length; i++)
                if (!HandFrames.TryBasis(HandFrames.Direction(skeleton, hand, digit, joints, i), dorsal, out result[i]))
                {
                    errors.Add(new("digit-frame", $"{label} {hand.Side} {digit.Role} has a degenerate joint frame. Correct the chain or terminal mapping.", hand.Side, joints[i]));
                    return null;
                }
            return result;
        }
    }

    // Conserves total source curl while retaining its distribution along normalized chain progress.
    private static float[,] OverlapWeights(float[] source, float[] target)
    {
        var weights = new float[target.Length, source.Length];
        var sourceTotal = source.Sum();
        var targetTotal = target.Sum();
        float s0 = 0;
        for (var s = 0; s < source.Length; s++)
        {
            var s1 = s0 + source[s] / sourceTotal;
            float t0 = 0;
            for (var t = 0; t < target.Length; t++)
            {
                var t1 = t0 + target[t] / targetTotal;
                weights[t, s] = MathF.Max(0, MathF.Min(s1, t1) - MathF.Max(s0, t0)) / (s1 - s0);
                t0 = t1;
            }
            s0 = s1;
        }
        return weights;
    }
}

internal sealed record RotationPair(int Source, int Target, int SourceParent, int TargetParent, Quaternion SourceFrame, Quaternion TargetFrame);
internal sealed record WristMotionPair(int Source, int Target, int? UpperArm, int? Forearm, Quaternion Basis, float Scale, Vector3 BendAxis);
internal sealed record DigitDistribution(int SourceWrist, int TargetWrist, int[] Source, int[] Target,
    Quaternion[] SourceFrames, Quaternion[] TargetFrames, float[,] Weights, int ProximalSourceIndex);
