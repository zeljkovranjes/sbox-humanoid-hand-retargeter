#nullable enable
using System.Text.Json;
using System.Numerics;
using HumanoidHandRetargeter.Calibration;
using HumanoidHandRetargeter.Mapping;
using HumanoidHandRetargeter.Retargeting;
using HumanoidHandRetargeter.Skeleton;
using Skel = HumanoidHandRetargeter.Skeleton.Skeleton;

namespace HumanoidHandRetargeter.Target;

/// <summary>Portable calibration inputs. The source model retains ownership of its complete graph.</summary>
public sealed record LiveHandSetup(BoneDefinition[] Source, BoneDefinition[] Target,
    LiveHandMap[] SourceHands, LiveHandMap[] TargetHands,
    Dictionary<HandSide, Quaternion> SourcePalms, Dictionary<HandSide, Quaternion> TargetPalms,
    HandMotionOptions Motion)
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    public string Serialize() => JsonSerializer.Serialize(this, Json);
    public static LiveHandSetup Deserialize(string text) => JsonSerializer.Deserialize<LiveHandSetup>(text, Json)
        ?? throw new ArgumentException("Missing live hand calibration.");
    public HandRetargetProfile Calibrate() => HandRetargetProfile.Calibrate(Skel.Create(Source), Map(SourceHands),
        Skel.Create(Target), Map(TargetHands), SourcePalms, TargetPalms);
    static HandMappingResult Map(LiveHandMap[] hands) => new(hands.Select(h => h.ToHand()), [], [], false);
    public static LiveHandSetup Create(Skel source, HandMappingResult sourceMap, Skel target, HandMappingResult targetMap,
        IReadOnlyDictionary<HandSide, Quaternion>? sourcePalms, IReadOnlyDictionary<HandSide, Quaternion>? targetPalms, HandMotionOptions motion)
    {
        static BoneDefinition[] Bones(Skel rig) => rig.Bones.Select(b => new BoneDefinition(b.Name,
            b.ParentIndex < 0 ? null : rig[b.ParentIndex].Name, b.RestLocal)).ToArray();
        return new(Bones(source), Bones(target), sourceMap.Hands.Select(LiveHandMap.From).ToArray(),
            targetMap.Hands.Select(LiveHandMap.From).ToArray(), sourcePalms?.ToDictionary(p => p.Key, p => p.Value) ?? new(),
            targetPalms?.ToDictionary(p => p.Key, p => p.Value) ?? new(), motion);
    }
}

public sealed record LiveDigitMap(DigitRole Role, int[] Segments, int? Metacarpal, int? Tip, string ExtraSlot);
public sealed record LiveHandMap(HandSide Side, int Wrist, int? Clavicle, int? UpperArm, int? Forearm,
    int[] Helpers, LiveDigitMap[] Digits)
{
    public static LiveHandMap From(HandRigDefinition h) => new(h.Side, h.Wrist, h.Clavicle, h.UpperArm, h.Forearm,
        h.TwistOrHelperBones.ToArray(), h.Digits.Select(d => new LiveDigitMap(d.Role, d.Segments.ToArray(), d.Metacarpal, d.Tip, d.ExtraSlot)).ToArray());
    public HandRigDefinition ToHand() => new(Side, Wrist, Digits.Select(d => new DigitChain(d.Role, d.Segments,
        d.Metacarpal, d.Tip, d.ExtraSlot)), Clavicle, UpperArm, Forearm, Helpers);
}
