#nullable enable
using System.Numerics;
using HumanoidHandRetargeter.Calibration;
using HumanoidHandRetargeter.Formats;
using HumanoidHandRetargeter.Maths;
using HumanoidHandRetargeter.Skeleton;
using HumanoidHandRetargeter.Validation;

namespace HumanoidHandRetargeter.Retargeting;
using Vector3 = System.Numerics.Vector3;

/// <summary>One offline solve path for static poses and baked clips. Target translations
/// and unmapped local transforms stay authored; helper constraints remain target-owned.</summary>
public static class HandRetargeter
{
    public static Pose RetargetPose(HandRetargetProfile profile, Pose sourcePose)
        => Solve(profile, sourcePose, null);

    /// <summary>Retains timing and fixes quaternion signs. Stateful angle unwrapping is
    /// local to this clip, so a calibrated profile may safely be shared by concurrent jobs.</summary>
    public static Clip RetargetClip(HandRetargetProfile profile, Clip sourceClip, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(sourceClip);
        if (sourceClip.FrameCount == 0)
            throw new RigValidationException(new[] { new RigIssue("empty-clip", "Source animation has no frames.") });
        var frames = new List<XForm[]>(sourceClip.FrameCount);
        var continuity = new AngleHistory(profile);
        foreach (var sourceFrame in sourceClip.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pose = Solve(profile, new Pose(sourceFrame), continuity);
            frames.Add(pose.Locals);
        }
        QuaternionContinuity.AlignFrames(frames);
        return new Clip(sourceClip.Name, sourceClip.Fps, sourceClip.Looping, frames, sourceClip.NativeFps);
    }

    private static Pose Solve(HandRetargetProfile profile, Pose sourcePose, AngleHistory? history)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var errors = PoseValidator.Validate(profile.Source, sourcePose);
        if (errors.Count > 0) throw new RigValidationException(errors);
        var sourceWorld = sourcePose.ToWorld(profile.Source);
        errors = PoseValidator.Validate(profile.Source, new Pose(sourceWorld));
        if (errors.Count > 0) throw new RigValidationException(errors);
        var sourceDeltas = new Quaternion[sourceWorld.Length];
        for (var i = 0; i < sourceWorld.Length; i++)
            sourceDeltas[i] = MathQ.Normalize(sourceWorld[i].Rot * Quaternion.Conjugate(profile.Source.RestWorld[i].Rot));
        var targetDeltas = Enumerable.Repeat(Quaternion.Identity, profile.Target.Count).ToArray();
        var solved = new bool[profile.Target.Count];
        foreach (var pair in profile.Pairs)
        {
            var parentSource = pair.SourceParent >= 0 ? sourceDeltas[pair.SourceParent] : Quaternion.Identity;
            var parentTarget = pair.TargetParent >= 0 ? targetDeltas[pair.TargetParent] : Quaternion.Identity;
            var canonical = MathQ.Normalize(Quaternion.Conjugate(pair.SourceFrame) * Quaternion.Conjugate(parentSource)
                * sourceDeltas[pair.Source] * pair.SourceFrame);
            targetDeltas[pair.Target] = MathQ.Normalize(parentTarget * pair.TargetFrame * canonical * Quaternion.Conjugate(pair.TargetFrame));
            solved[pair.Target] = true;
        }
        for (var d = 0; d < profile.Distributions.Length; d++)
        {
            var digit = profile.Distributions[d];
            var curls = new float[digit.Source.Length];
            var spread = 0f;
            var previous = sourceDeltas[digit.SourceWrist];
            for (var s = 0; s < digit.Source.Length; s++)
            {
                var frame = digit.SourceFrames[s];
                var delta = sourceDeltas[digit.Source[s]];
                var canonical = MathQ.Normalize(Quaternion.Conjugate(frame) * Quaternion.Conjugate(previous) * delta * frame);
                // Legacy FingerSolver decomposition: canonical Y curl, Z splay, discard X twist when redistributing.
                MathQ.SwingTwist(canonical, Vector3.UnitY, out var swing, out var curl);
                curls[s] = SignedAngle(curl, Vector3.UnitY);
                if (history is not null) curls[s] = history.Unwrap(d, s, curls[s], false);
                if (s <= digit.ProximalSourceIndex)
                {
                    MathQ.SwingTwist(swing, Vector3.UnitZ, out _, out var splay);
                    var angle = SignedAngle(splay, Vector3.UnitZ);
                    if (history is not null) angle = history.Unwrap(d, s, angle, true);
                    spread += angle;
                }
                previous = delta;
            }
            var accumulated = targetDeltas[digit.TargetWrist];
            for (var t = 0; t < digit.Target.Length; t++)
            {
                var angle = 0f;
                for (var s = 0; s < curls.Length; s++) angle += digit.Weights[t, s] * curls[s];
                var motion = Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle);
                if (t == 0) motion = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, spread) * motion;
                var frame = digit.TargetFrames[t];
                accumulated = MathQ.Normalize(accumulated * frame * motion * Quaternion.Conjugate(frame));
                targetDeltas[digit.Target[t]] = accumulated;
                solved[digit.Target[t]] = true;
            }
        }

        var targetPose = Pose.Rest(profile.Target);
        var targetWorld = new XForm[profile.Target.Count];
        for (var i = 0; i < targetWorld.Length; i++)
        {
            var parent = profile.Target[i].ParentIndex;
            if (solved[i])
            {
                var desiredWorld = targetDeltas[i] * profile.Target.RestWorld[i].Rot;
                targetPose.Locals[i].Rot = MathQ.Normalize(parent < 0 ? desiredWorld : Quaternion.Conjugate(targetWorld[parent].Rot) * desiredWorld);
            }
            targetWorld[i] = parent < 0 ? targetPose.Locals[i] : XForm.Compose(targetWorld[parent], targetPose.Locals[i]);
        }
        errors = PoseValidator.Validate(profile.Target, targetPose);
        if (errors.Count > 0) throw new RigValidationException(errors);
        errors = PoseValidator.Validate(profile.Target, new Pose(targetWorld));
        if (errors.Count > 0) throw new RigValidationException(errors);
        return targetPose;
    }

    // Same signed twist convention as the legacy FingerSolver.
    private static float SignedAngle(Quaternion twist, Vector3 axis)
    {
        var angle = 2f * MathF.Atan2(twist.X * axis.X + twist.Y * axis.Y + twist.Z * axis.Z, twist.W);
        if (angle > MathF.PI) angle -= 2f * MathF.PI;
        if (angle < -MathF.PI) angle += 2f * MathF.PI;
        return angle;
    }

    private sealed class AngleHistory
    {
        private readonly float[][] _curl;
        private readonly float[][] _spread;
        public AngleHistory(HandRetargetProfile profile)
        {
            _curl = profile.Distributions.Select(d => Enumerable.Repeat(float.NaN, d.Source.Length).ToArray()).ToArray();
            _spread = profile.Distributions.Select(d => Enumerable.Repeat(float.NaN, d.Source.Length).ToArray()).ToArray();
        }
        public float Unwrap(int digit, int joint, float angle, bool spread)
        {
            var values = spread ? _spread : _curl;
            var previous = values[digit][joint];
            if (float.IsFinite(previous))
                angle += 2f * MathF.PI * MathF.Round((previous - angle) / (2f * MathF.PI));
            values[digit][joint] = angle;
            return angle;
        }
    }
}
