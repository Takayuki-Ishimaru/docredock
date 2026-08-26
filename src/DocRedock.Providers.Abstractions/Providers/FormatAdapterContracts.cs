using DocRedock.Core.Diff;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;

namespace DocRedock.Providers.Abstractions.Providers;

public sealed record AdapterInput(string Path, string FileName, DocumentFormatKind DetectedFormat);

public sealed record AdapterInspection(
    DocumentFormatKind Format,
    bool IsEncrypted,
    bool IsSigned,
    bool HasMacros,
    bool HasExternalRelationships,
    IReadOnlySet<string> Capabilities,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record AdapterExtraction(
    DocumentGraph Graph,
    IReadOnlyList<ExtractedAsset> Assets,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record ExtractedAsset(
    string Id,
    string FileName,
    string MediaType,
    string Sha256,
    ReadOnlyMemory<byte> Content,
    string? SourcePartUri = null);

public sealed record AdapterRestoreRequest(
    string SourcePath,
    string DestinationPath,
    DocumentGraph Baseline,
    DocumentGraph Edited,
    DiffResult Diff,
    bool Strict = true,
    bool AllowRenderFallback = false);

public sealed record AdapterRestoreResult(
    string OutputPath,
    FidelityReport Fidelity,
    IReadOnlySet<string> ChangedPartUris,
    IReadOnlySet<string> PreservedPartUris);

/// <summary>
/// Versioned format boundary. Implementations must probe read-only, extract a
/// canonical graph, and restore by patching an immutable source copy.
/// </summary>
public interface IFormatAdapter : IFormatProbe
{
    DocumentFormatKind Format { get; }

    ValueTask<AdapterInspection> InspectAsync(
        AdapterInput input,
        CancellationToken cancellationToken = default);

    ValueTask<AdapterExtraction> ExtractAsync(
        AdapterInput input,
        CancellationToken cancellationToken = default);

    ValueTask<AdapterRestoreResult> RestoreAsync(
        AdapterRestoreRequest request,
        CancellationToken cancellationToken = default);
}

public interface IFormatAdapterRegistry : IAdapterRegistry
{
    IReadOnlyList<IFormatAdapter> ListAdapters();
    IFormatAdapter? Find(DocumentFormatKind format);
}
