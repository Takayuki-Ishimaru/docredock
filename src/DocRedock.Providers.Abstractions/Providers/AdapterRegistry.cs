namespace DocRedock.Providers.Abstractions.Providers;

/// <summary>Runs only explicitly registered probes, preserves stream position, and reports every rejection/failure.</summary>
public sealed class AdapterRegistry : IAdapterRegistry
{
    private readonly IReadOnlyList<IFormatProbe> probes;

    public AdapterRegistry(IEnumerable<IFormatProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        this.probes = probes.OrderBy(probe => probe.Descriptor.ProviderId, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<ProviderDescriptor> ListProviders() => probes.Select(probe => probe.Descriptor).ToArray();

    public async ValueTask<AdapterSelection> SelectAsync(RewindableInput input, AdapterSelectionPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);
        var attempts = new List<(IFormatProbe Probe, ProbeResult Result)>();
        var failures = new List<ProbeFailure>();
        var warnings = new List<ProbeWarning>();
        foreach (var probe in probes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (probe.Descriptor.InterfaceVersion != policy.RequiredInterfaceVersion)
            {
                warnings.Add(new("InterfaceVersionRejected", $"{probe.Descriptor.ProviderId} has incompatible interface version."));
                continue;
            }
            if (policy.Allowlist is not null && !policy.Allowlist.IsAllowed(probe.Descriptor, out var reason))
            {
                warnings.Add(new("AllowlistRejected", $"{probe.Descriptor.ProviderId}: {reason}"));
                continue;
            }
            try
            {
                input.Reset();
                var result = await probe.ProbeAsync(input, new ProbeContext(), cancellationToken).ConfigureAwait(false);
                attempts.Add((probe, result));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add(new(probe.Descriptor.ProviderId, exception.GetType().Name, exception.Message));
            }
            finally { input.Reset(); }
        }

        var supported = attempts.Where(item => item.Result.IsSupported)
            .OrderByDescending(item => item.Result.Priority)
            .ThenByDescending(item => item.Result.Confidence)
            .ThenBy(item => item.Probe.Descriptor.ProviderId, StringComparer.Ordinal)
            .ToArray();
        if (supported.Length == 0)
            return new(AdapterSelectionStatus.NoSupportedAdapter, null, null, attempts.Select(item => item.Result).ToArray(), failures, warnings);

        var top = supported[0];
        var ambiguous = supported.Skip(1).Any(candidate =>
            candidate.Result.Priority == top.Result.Priority &&
            Math.Abs(candidate.Result.Confidence - top.Result.Confidence) <= policy.AmbiguityConfidenceDelta);
        if (ambiguous && policy.Strict)
        {
            warnings.Add(new("AmbiguousAdapter", "Multiple adapters have equivalent priority and confidence."));
            return new(AdapterSelectionStatus.Ambiguous, null, null, attempts.Select(item => item.Result).ToArray(), failures, warnings);
        }
        return new(AdapterSelectionStatus.Selected, top.Probe, top.Result, attempts.Select(item => item.Result).ToArray(), failures, warnings);
    }
}
