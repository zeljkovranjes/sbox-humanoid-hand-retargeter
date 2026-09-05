#nullable enable
using HumanoidHandRetargeter.Validation;

namespace HumanoidHandRetargeter.Mapping;

/// <summary>Evidence for a proposed semantic role; unselected candidates remain visible.</summary>
public sealed record HandMappingCandidate(HandSide? Side, string Role, IReadOnlyList<int> Bones,
    float Confidence, string Reason, bool Selected);

public sealed class HandMappingResult
{
    public IReadOnlyList<HandRigDefinition> Hands { get; }
    public IReadOnlyList<HandMappingCandidate> Candidates { get; }
    public IReadOnlyList<RigIssue> Issues { get; }
    public bool NeedsReview { get; }

    internal HandMappingResult(IEnumerable<HandRigDefinition> hands, IEnumerable<HandMappingCandidate> candidates,
        IEnumerable<RigIssue> issues, bool needsReview)
    {
        Hands = Array.AsReadOnly(hands.ToArray());
        Candidates = Array.AsReadOnly(candidates.ToArray());
        Issues = Array.AsReadOnly(issues.ToArray());
        NeedsReview = needsReview || Issues.Count > 0;
    }
}
