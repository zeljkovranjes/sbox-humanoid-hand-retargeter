#nullable enable
namespace HumanoidHandRetargeter.Retargeting;

public sealed class HandMotionOptions
{
    public bool TransferWristPosition { get; init; } = true;
    public bool ScaleWristTravel { get; init; } = true;
    /// <summary>Known source-to-target model axes, independent of the wrists' rest rotations.</summary>
    public System.Numerics.Quaternion? WristTravelBasis {get;init;}
    public bool SolveArmIk { get; init; } = true;
}
