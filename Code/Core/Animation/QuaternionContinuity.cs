// Reused from humanoid-retargeter 26084c96c3fc870aaf9a5bd798de063ce2fd62df; shared sample alignment extracted.
#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidHandRetargeter.Maths;

namespace HumanoidHandRetargeter.Formats;

/// <summary>
/// Temporal hemisphere alignment for quaternion tracks. <c>q</c> and <c>-q</c> encode the same
/// rotation, but per-frame conversions (e.g. <see cref="Quaternion.CreateFromRotationMatrix"/>
/// branch changes) can flip the sign between consecutive samples; consumers that interpolate
/// numerically between samples (the engine lerps between DMX log keys) then spin the long way
/// around. Aligning each sample onto the previous sample's hemisphere
/// (<c>Dot(prev, cur) &gt;= 0</c>) is semantically a no-op but interpolation-safe.
/// </summary>
public static class QuaternionContinuity
{
    /// <summary>Returns the sample in the same hemisphere as its preceding sample.</summary>
    public static Quaternion Align(Quaternion previous, Quaternion sample)
        => Quaternion.Dot(previous, sample) < 0f ? Quaternion.Negate(sample) : sample;

    /// <summary>
    /// Negates quaternions in place where needed so every consecutive pair of the track has a
    /// non-negative dot product. The first sample is kept as-is.
    /// </summary>
    public static void Align(Span<Quaternion> track)
    {
        for (int i = 1; i < track.Length; i++)
        {
            track[i] = Align(track[i - 1], track[i]);
        }
    }

    /// <summary>
    /// Aligns every bone's orientation track across the clip frames, in place: for each bone
    /// index, consecutive frames' rotations end up on the same hemisphere.
    /// Frames must all have the same bone count.
    /// </summary>
    public static void AlignFrames(IReadOnlyList<XForm[]> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count < 2)
            return;

        int boneCount = frames[0].Length;
        for (int bone = 0; bone < boneCount; bone++)
        {
            var prev = frames[0][bone].Rot;
            for (int f = 1; f < frames.Count; f++)
            {
                var q = Align(prev, frames[f][bone].Rot);
                frames[f][bone].Rot = q;
                prev = q;
            }
        }
    }
}
