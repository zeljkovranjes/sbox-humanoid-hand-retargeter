#nullable enable
namespace HumanoidHandRetargeter.Validation;

/// <summary>Structured, actionable validation shared by core callers and the editor.</summary>
public sealed class RigValidationException : Exception
{
    public IReadOnlyList<RigIssue> Issues { get; }
    public RigValidationException(IEnumerable<RigIssue> issues) : this(issues.ToArray()) { }
    private RigValidationException(RigIssue[] issues) : base(string.Join("\n", issues.Select(i => i.Message)))
        => Issues = Array.AsReadOnly(issues);
}
