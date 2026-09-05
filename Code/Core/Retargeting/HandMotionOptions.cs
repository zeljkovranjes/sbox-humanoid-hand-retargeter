#nullable enable
namespace HumanoidHandRetargeter.Retargeting;

public sealed class HandMotionOptions
{
    public bool TransferWristPosition { get; init; } = true;
    public bool ScaleWristTravel { get; init; } = true;
    /// <summary>Known source-to-target model axes, independent of the wrists' rest rotations.</summary>
    public System.Numerics.Quaternion? WristTravelBasis {get;init;}
    public bool SolveArmIk { get; init; } = true;
    public bool PreserveWeaponGrip { get; init; } = true;
    /// <summary>Shared source weapon-to-target translation in centimeters. Grip motion is never scaled per hand.</summary>
    public System.Numerics.Vector3? WeaponSpaceOffset {get;init;}
}
