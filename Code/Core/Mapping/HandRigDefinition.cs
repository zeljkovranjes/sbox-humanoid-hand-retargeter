#nullable enable
namespace HumanoidHandRetargeter.Mapping;

public enum HandSide { Left, Right }
public enum DigitRole { Thumb, Index, Middle, Ring, Pinky, Extra }

/// <summary>Ordered anatomical joints; optional palm and terminal bones are explicit.</summary>
public sealed class DigitChain
{
    public DigitRole Role { get; }
    public string ExtraSlot { get; }
    public IReadOnlyList<int> Segments { get; }
    public int? Metacarpal { get; }
    public int? Tip { get; }

    public DigitChain(DigitRole role, IEnumerable<int> segments,
        int? metacarpal = null, int? tip = null, string extraSlot = "")
    {
        ArgumentNullException.ThrowIfNull(segments);
        Role = role;
        Segments = Array.AsReadOnly(segments.ToArray());
        Metacarpal = metacarpal;
        Tip = tip;
        ExtraSlot = extraSlot ?? "";
    }

    public IEnumerable<int> Bones
    {
        get
        {
            if (Metacarpal is int palm) yield return palm;
            foreach (var segment in Segments) yield return segment;
            if (Tip is int tip) yield return tip;
        }
    }
}

/// <summary>A single hand; rigs with only one side do not need a placeholder other hand.</summary>
public sealed class HandRigDefinition
{
    public HandSide Side { get; }
    public int Wrist { get; }
    public int? Clavicle { get; }
    public int? UpperArm { get; }
    public int? Forearm { get; }
    public IReadOnlyList<DigitChain> Digits { get; }
    public IReadOnlyList<int> TwistOrHelperBones { get; }

    public HandRigDefinition(HandSide side, int wrist, IEnumerable<DigitChain> digits,
        int? clavicle = null, int? upperArm = null, int? forearm = null,
        IEnumerable<int>? twistOrHelperBones = null)
    {
        ArgumentNullException.ThrowIfNull(digits);
        Side = side;
        Wrist = wrist;
        Clavicle = clavicle;
        UpperArm = upperArm;
        Forearm = forearm;
        Digits = Array.AsReadOnly(digits.ToArray());
        TwistOrHelperBones = Array.AsReadOnly((twistOrHelperBones ?? Array.Empty<int>()).ToArray());
    }
}
