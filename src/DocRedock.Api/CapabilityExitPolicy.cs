namespace DocRedock.Api;

/// <summary>
/// The single exit-code rule shared by `doctor` and `doctor --json`. Only capabilities tagged
/// "required" gate the default exit code; optional gaps are informational there. `--strict`
/// additionally fails on any capability that is not "ready", except an optional capability whose
/// function is already provided by a ready alternative (<see cref="CapabilityStatus.SatisfiedBy"/>
/// naming a provider that is itself "ready" — "partial" never satisfies).
/// </summary>
public static class CapabilityExitPolicy
{
    public static DoctorVerdict Evaluate(IReadOnlyList<CapabilityStatus> capabilities, bool strict)
    {
        var requiredReady = capabilities.Where(item => item.Tier == "required").All(item => item.Status == "ready");
        var optionalGaps = capabilities.Where(item => item.Tier == "optional" && item.Status != "ready")
            .Select(item => item.Id).ToArray();
        var strictFailures = capabilities.Where(item => item.Status != "ready" && !IsSatisfiedByReadyAlternative(item, capabilities))
            .Select(item => item.Id).ToArray();
        var exitCode = strict ? (strictFailures.Length > 0 ? 1 : 0) : (requiredReady ? 0 : 1);
        return new DoctorVerdict(exitCode, requiredReady, optionalGaps, strictFailures);
    }

    private static bool IsSatisfiedByReadyAlternative(CapabilityStatus item, IReadOnlyList<CapabilityStatus> capabilities) =>
        item.Tier == "optional" && item.SatisfiedBy is not null &&
        capabilities.Any(alternative => string.Equals(alternative.Provider, item.SatisfiedBy, StringComparison.Ordinal)
            && alternative.Status == "ready");
}

/// <summary>Exit code plus the id lists used to render both the text summary block and the JSON `summary`.</summary>
public sealed record DoctorVerdict(int ExitCode, bool RequiredReady, IReadOnlyList<string> OptionalGaps, IReadOnlyList<string> StrictFailures);
