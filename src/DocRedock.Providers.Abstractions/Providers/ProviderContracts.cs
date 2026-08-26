using DocRedock.Core.Documents;

namespace DocRedock.Providers.Abstractions.Providers;

public sealed record ProviderDescriptor(
    string ProviderId,
    Version ProviderVersion,
    int InterfaceVersion,
    IReadOnlySet<string> Capabilities,
    string LicenseExpression,
    string BinarySha256,
    bool IsBuiltIn)
{
    public bool Supports(string capability) => Capabilities.Contains(capability);
}

public sealed record ProbeEvidence(string Kind, string Detail);
public sealed record ProbeWarning(string Code, string Message);
public sealed record ProbeResult(
    string AdapterId,
    double Confidence,
    int Priority,
    IReadOnlyList<ProbeEvidence> Evidence,
    IReadOnlyList<ProbeWarning> Warnings,
    bool RequiresDecryption,
    bool IsMalformed,
    bool IsSupported)
{
    public static ProbeResult Unsupported(string adapterId, string reason) =>
        new(adapterId, 0, 0, Array.Empty<ProbeEvidence>(), new[] { new ProbeWarning("Unsupported", reason) }, false, false, false);
}

public sealed record ProbeContext(long MaxInputBytes = 209_715_200, string? FileName = null);
public sealed record AdapterSelectionPolicy(
    bool Strict = true,
    double AmbiguityConfidenceDelta = 0.05,
    int RequiredInterfaceVersion = 1,
    ProviderAllowlist? Allowlist = null);

public enum AdapterSelectionStatus { Selected, NoSupportedAdapter, Ambiguous }
public sealed record ProbeFailure(string ProviderId, string ExceptionType, string Message);
public sealed record AdapterSelection(
    AdapterSelectionStatus Status,
    IFormatProbe? Selected,
    ProbeResult? SelectedResult,
    IReadOnlyList<ProbeResult> Attempts,
    IReadOnlyList<ProbeFailure> Failures,
    IReadOnlyList<ProbeWarning> Warnings)
{
    public bool IsSuccess => Status == AdapterSelectionStatus.Selected;
}

public interface IFormatProbe
{
    ProviderDescriptor Descriptor { get; }
    ValueTask<ProbeResult> ProbeAsync(RewindableInput input, ProbeContext context, CancellationToken cancellationToken);
}

public interface IAdapterRegistry
{
    IReadOnlyList<ProviderDescriptor> ListProviders();
    ValueTask<AdapterSelection> SelectAsync(RewindableInput input, AdapterSelectionPolicy policy, CancellationToken cancellationToken = default);
}

/// <summary>Explicit registration requirements for external providers. Built-ins do not need a list entry.</summary>
public sealed class ProviderAllowlist
{
    private readonly Dictionary<string, AllowedProvider> entries;

    public ProviderAllowlist(IEnumerable<AllowedProvider> entries)
    {
        this.entries = entries.ToDictionary(entry => entry.ProviderId, StringComparer.Ordinal);
    }

    public bool IsAllowed(ProviderDescriptor descriptor, out string reason)
    {
        if (descriptor.IsBuiltIn) { reason = string.Empty; return true; }
        if (!entries.TryGetValue(descriptor.ProviderId, out var entry)) { reason = "Provider is not explicitly allowlisted."; return false; }
        if (entry.InterfaceVersion != descriptor.InterfaceVersion) { reason = "Provider interface version does not match allowlist."; return false; }
        if (!StringComparer.OrdinalIgnoreCase.Equals(entry.BinarySha256, descriptor.BinarySha256)) { reason = "Provider binary hash does not match allowlist."; return false; }
        if (entry.ProviderVersion is not null && entry.ProviderVersion != descriptor.ProviderVersion) { reason = "Provider version does not match allowlist."; return false; }
        reason = string.Empty;
        return true;
    }
}

public sealed record AllowedProvider(string ProviderId, Version? ProviderVersion, int InterfaceVersion, string BinarySha256);
