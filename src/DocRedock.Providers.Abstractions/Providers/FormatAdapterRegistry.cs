using DocRedock.Core.Documents;

namespace DocRedock.Providers.Abstractions.Providers;

public sealed class FormatAdapterRegistry(IEnumerable<IFormatAdapter> adapters) : IFormatAdapterRegistry
{
    private readonly IReadOnlyList<IFormatAdapter> adapters = adapters
        .OrderBy(adapter => adapter.Descriptor.ProviderId, StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyList<IFormatAdapter> ListAdapters() => adapters;
    public IReadOnlyList<ProviderDescriptor> ListProviders() => adapters.Select(adapter => adapter.Descriptor).ToArray();
    public IFormatAdapter? Find(DocumentFormatKind format) => adapters.FirstOrDefault(adapter => adapter.Format == format);

    public ValueTask<AdapterSelection> SelectAsync(
        RewindableInput input,
        AdapterSelectionPolicy policy,
        CancellationToken cancellationToken = default) =>
        new AdapterRegistry(adapters).SelectAsync(input, policy, cancellationToken);
}
