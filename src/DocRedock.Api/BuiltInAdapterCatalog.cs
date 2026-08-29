using System.Security.Cryptography;
using DocRedock.Core.Diff;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Formats.OpenXml.Docx;
using DocRedock.Formats.OpenXml.Pptx;
using DocRedock.Formats.OpenXml.Xlsx;
using DocRedock.Formats.Pdf;
using DocRedock.Providers.Abstractions.Providers;

namespace DocRedock.Api;

/// <summary>Explicit catalog for the four built-in, versioned format adapters.</summary>
public static class BuiltInAdapterCatalog
{
    public static FormatAdapterRegistry CreateRegistry() => new(
    [
        new BuiltInFormatAdapter(DocumentFormatKind.Docx),
        new BuiltInFormatAdapter(DocumentFormatKind.Xlsx),
        new BuiltInFormatAdapter(DocumentFormatKind.Pptx),
        new BuiltInFormatAdapter(DocumentFormatKind.Pdf),
    ]);
}

internal sealed class BuiltInFormatAdapter : IFormatAdapter
{
    private readonly DocumentFormatKind format;
    private readonly DocxAdapter docx = new();
    private readonly XlsxAdapter xlsx = new();
    private readonly PptxAdapter pptx = new();

    public BuiltInFormatAdapter(DocumentFormatKind format)
    {
        this.format = format;
        Descriptor = new ProviderDescriptor(
            $"docredock.{format.ToString().ToLowerInvariant()}.builtin",
            new Version(0, 2, 0),
            1,
            Capabilities(format),
            "MIT",
            "built-in",
            true);
    }

    public ProviderDescriptor Descriptor { get; }
    public DocumentFormatKind Format => format;

    public async ValueTask<ProbeResult> ProbeAsync(RewindableInput input, ProbeContext context, CancellationToken cancellationToken)
    {
        var result = await new ContainerFormatDetector().ProbeAsync(input, context, cancellationToken).ConfigureAwait(false);
        var detected = result.Evidence.FirstOrDefault(item => item.Kind == "ooxml_part")?.Detail switch
        {
            "docx" => DocumentFormatKind.Docx,
            "xlsx" => DocumentFormatKind.Xlsx,
            "pptx" => DocumentFormatKind.Pptx,
            _ when result.Evidence.Any(item => item.Kind == "magic" && item.Detail == "%PDF-") => DocumentFormatKind.Pdf,
            _ => DocumentFormatKind.Unknown,
        };
        return result.IsSupported && detected == format
            ? result with { AdapterId = Descriptor.ProviderId, Priority = 200 }
            : ProbeResult.Unsupported(Descriptor.ProviderId, $"Input is not {format}.");
    }

    public ValueTask<AdapterInspection> InspectAsync(AdapterInput input, CancellationToken cancellationToken = default)
    {
        EnsureFormat(input);
        return ValueTask.FromResult(new AdapterInspection(
            format,
            false,
            false,
            Path.GetExtension(input.Path).EndsWith('m'),
            false,
            Descriptor.Capabilities,
            []));
    }

    public async ValueTask<AdapterExtraction> ExtractAsync(AdapterInput input, CancellationToken cancellationToken = default)
    {
        EnsureFormat(input);
        return format switch
        {
            DocumentFormatKind.Docx => await ExtractDocxAsync(input.Path, cancellationToken).ConfigureAwait(false),
            DocumentFormatKind.Xlsx => ExtractXlsx(input.Path),
            DocumentFormatKind.Pptx => ExtractPptx(input.Path),
            DocumentFormatKind.Pdf => ExtractPdf(input.Path),
            _ => throw new NotSupportedException(),
        };
    }

    public async ValueTask<AdapterRestoreResult> RestoreAsync(AdapterRestoreRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Diff.DirtySet.HasOriginalMutations)
        {
            await WriteNewAsync(request.DestinationPath, await File.ReadAllBytesAsync(request.SourcePath, cancellationToken), cancellationToken).ConfigureAwait(false);
            return new(request.DestinationPath, new FidelityReport(FidelityLevel.F0, PackagePreservationLevel.ByteIdentical, []), new HashSet<string>(), new HashSet<string>());
        }

        switch (format)
        {
            case DocumentFormatKind.Docx:
                {
                    var deletes = request.Diff.PatchSet.Operations.Where(operation => operation.Kind == PatchOperationKind.ExplicitDelete)
                        .Select(operation => operation.NodeId).ToHashSet(StringComparer.Ordinal);
                    var result = await docx.RestoreAsync(request.SourcePath, request.Baseline, request.Edited, request.DestinationPath,
                        new DiffOptions(deletes), new DocxRestoreOptions(request.Strict), cancellationToken).ConfigureAwait(false);
                    return new(request.DestinationPath, result.Fidelity,
                        result.Diff.DirtySet.DirtyPartUris, result.Fidelity.PreservedPartUris?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>());
                }
            case DocumentFormatKind.Xlsx:
                {
                    await using var source = File.OpenRead(request.SourcePath);
                    var plan = xlsx.CreatePatchPlan(request.Baseline, request.Edited);
                    var result = xlsx.Restore(source, plan);
                    await WriteNewAsync(request.DestinationPath, result.Bytes, cancellationToken).ConfigureAwait(false);
                    return new(request.DestinationPath,
                        new FidelityReport(FidelityLevel.F1, PackagePreservationLevel.PartPayloadIdentical,
                            result.Warnings.Select(message => AdapterWarningDiagnostics.Create("XlsxWarning", message)).ToArray()),
                        plan.DirtyPartGraph.DirtyParts, new HashSet<string>());
                }
            case DocumentFormatKind.Pptx:
                {
                    await using var source = File.OpenRead(request.SourcePath);
                    var plan = pptx.CreatePatchPlan(request.Baseline, request.Edited);
                    var result = pptx.Restore(source, plan);
                    await WriteNewAsync(request.DestinationPath, result.Bytes, cancellationToken).ConfigureAwait(false);
                    return new(request.DestinationPath,
                        new FidelityReport(FidelityLevel.F1, PackagePreservationLevel.PartPayloadIdentical,
                            result.Warnings.Select(message => AdapterWarningDiagnostics.Create("PptxWarning", message)).ToArray()),
                        plan.DirtyParts, new HashSet<string>());
                }
            case DocumentFormatKind.Pdf:
                return new(request.DestinationPath,
                    new FidelityReport(FidelityLevel.FX, PackagePreservationLevel.Unsupported,
                        [new Diagnostic("RenderFallbackRequired", "Edited PDF requires explicit Render, not adapter Restore.", DiagnosticSeverity.Error)]),
                    new HashSet<string>(), new HashSet<string>());
            default: throw new NotSupportedException();
        }
    }

    private async Task<AdapterExtraction> ExtractDocxAsync(string path, CancellationToken cancellationToken)
    {
        var result = await docx.ExtractAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new(result.Graph, [], result.Diagnostics);
    }

    private AdapterExtraction ExtractXlsx(string path)
    {
        using var stream = File.OpenRead(path);
        var result = xlsx.Extract(stream);
        return new(result.Graph, [], result.FormulaDiagnostics.Select(item => new Diagnostic(
            "XlsxFormula" + item.Safety, item.Reason ?? "Formula classified without evaluation.",
            item.Safety == XlsxFormulaSafety.Safe ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning))
            .Concat(result.Warnings.Select(item => AdapterWarningDiagnostics.Create("XlsxProjectionWarning", item))).ToArray());
    }

    private AdapterExtraction ExtractPptx(string path)
    {
        using var stream = File.OpenRead(path);
        var result = pptx.Extract(stream);
        return new(result.Graph, [], result.Warnings.Select(item => AdapterWarningDiagnostics.Create("PptxWarning", item)).ToArray());
    }

    private static AdapterExtraction ExtractPdf(string path)
    {
        var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        var result = PdfTextExtractor.Extract(path);
        return new AdapterExtraction(PdfDocumentGraphProjection.CreateGraph(result, sourceHash), [], PdfDocumentGraphProjection.Diagnostics(result));
    }

    private void EnsureFormat(AdapterInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.DetectedFormat != format) throw new InvalidDataException($"Adapter input format is not {format}.");
        if (!File.Exists(input.Path)) throw new FileNotFoundException("Adapter input was not found.", input.Path);
    }

    private static IReadOnlySet<string> Capabilities(DocumentFormatKind format) => format switch
    {
        DocumentFormatKind.Docx => new HashSet<string>(StringComparer.Ordinal) { "extract.text", "extract.images", "restore.byte_identical", "restore.text_in_place", "restore.insert_node", "restore.delete_node", "preserve.raw_xml_slice", "preserve.unknown_parts" },
        DocumentFormatKind.Xlsx => new HashSet<string>(StringComparer.Ordinal) { "extract.text", "extract.images", "restore.byte_identical", "restore.cell_value", "preserve.unknown_parts" },
        DocumentFormatKind.Pptx => new HashSet<string>(StringComparer.Ordinal) { "extract.text", "extract.images", "restore.byte_identical", "restore.shape_text", "preserve.unknown_parts" },
        DocumentFormatKind.Pdf => new HashSet<string>(StringComparer.Ordinal) { "extract.text", "extract.coordinates", "restore.byte_identical", "render.new_document" },
        _ => new HashSet<string>(),
    };

    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(path)) throw new IOException("Adapter output already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false); File.Move(temporary, path); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
