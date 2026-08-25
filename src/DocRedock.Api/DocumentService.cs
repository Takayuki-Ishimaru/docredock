using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using DocRedock.Core.Diff;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Formats.OpenXml.Docx;
using DocRedock.Formats.OpenXml.Pptx;
using DocRedock.Formats.OpenXml.Xlsx;
using DocRedock.Formats.Pdf;
using DocRedock.Markdown;
using DocRedock.Ocr.Tesseract;
using DocRedock.Providers.Abstractions.Providers;
using DocRedock.Render;
using DocRedock.RoundTrip;

namespace DocRedock.Api;

public sealed record DocumentExportOptions(
    string SourcePath,
    string WorkspacePath,
    string? MarkdownPath = null,
    bool EnableOcr = false,
    IReadOnlyList<string>? OcrLanguages = null,
    string ContentPolicy = "visible",
    string? DocumentId = null,
    string Profile = "roundtrip");
public sealed record DocumentExportResult(string MarkdownPath, RoundTripWorkspace Workspace, DocumentGraph Graph, IReadOnlyList<Diagnostic> Diagnostics);
public sealed record ReadableDocumentExportOptions(
    string SourcePath,
    string MarkdownPath,
    bool EnableOcr = false,
    IReadOnlyList<string>? OcrLanguages = null,
    string ContentPolicy = "visible",
    bool ShowFormulas = false,
    bool IncludeSvgPreviews = false,
    bool IncludeDiagrams = true,
    IReadOnlyList<string>? Sheets = null,
    string? Title = null,
    bool EmbedImages = false);
public sealed record ReadableDocumentExportResult(string MarkdownPath, DocumentGraph Graph, IReadOnlyList<Diagnostic> Diagnostics);
public sealed record DocumentDiffResult(DocumentGraph Baseline, GraphEditResult Edit, IReadOnlyList<Diagnostic> Diagnostics);
public sealed record DocumentRestoreOptions(string WorkspacePath, string OutputPath, string? MarkdownPath = null, bool AllowRenderFallback = false);
public sealed record DocumentRestoreResult(string OutputPath, FidelityLevel Fidelity, bool Succeeded, IReadOnlyList<Diagnostic> Diagnostics);
public sealed record DocumentRenderOptions(string Markdown, string OutputPath, RenderFormat Format, RenderOptions? Options = null);
public sealed record DocumentRebaseOptions(string SourcePath, string WorkspacePath, string? MarkdownPath = null, string? DocumentId = null);
public sealed record OcrAssetRecord(
    [property: JsonPropertyName("asset_id")] string AssetId,
    [property: JsonPropertyName("status")] OcrProcessingStatus Status,
    [property: JsonPropertyName("languages")] IReadOnlyList<string> Languages,
    [property: JsonIgnore] OcrResult? Result,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<OcrDiagnostic> Diagnostics)
{
    [JsonPropertyName("text")] public string? Text => Result?.Text;
    [JsonPropertyName("regions")] public IReadOnlyList<OcrTextRegion> Regions => Result?.Regions ?? [];
}
public sealed record OfficePackageEntry(string Name, long Size, long CompressedSize, string Sha256);
public sealed record OfficePackageIndex(string SchemaVersion, IReadOnlyList<OfficePackageEntry> Entries);
public sealed record OfficeRelationship(string PartUri, string Id, string Type, string Target, bool IsExternal);

/// <summary>Typed, local-only job orchestrator. Restore is deliberately separate from Render.</summary>
public sealed class DocumentService
{
    // Keep a readable Markdown export bounded even when an Office package contains
    // unexpectedly large or numerous bitmaps.
    private const int MaxEmbeddedImageBytes = 10 * 1024 * 1024;
    private const int MaxTotalEmbeddedImageBytes = 50 * 1024 * 1024;
    private readonly DocxAdapter docx = new();
    private readonly XlsxAdapter xlsx = new();
    private readonly PptxAdapter pptx = new();
    private readonly MarkdownRenderer renderer = new();
    private readonly IOcrEngine? ocr;
    private readonly IPdfRasterizer? pdfRasterizer;
    public IFormatAdapterRegistry Adapters { get; }

    public DocumentService(IOcrEngine? ocrEngine = null, IPdfRasterizer? pdfRasterizer = null)
    {
        (ocr, this.pdfRasterizer) = (ocrEngine, pdfRasterizer);
        Adapters = BuiltInAdapterCatalog.CreateRegistry();
    }

    public async Task<DocumentExportResult> ExportAsync(DocumentExportOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var source = Path.GetFullPath(options.SourcePath);
        var format = await DetectFormatAsync(source, cancellationToken).ConfigureAwait(false);
        var diagnostics = new List<Diagnostic>();
        DocumentGraph graph;
        IReadOnlyList<RawSliceRef> slices = Array.Empty<RawSliceRef>();
        var macro = false;
        switch (format)
        {
            case DocumentFormatKind.Docx:
                {
                    var extraction = await docx.ExtractAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
                    graph = extraction.Graph; diagnostics.AddRange(extraction.Diagnostics); macro = extraction.SourceIndex.HasMacro;
                    slices = extraction.SourceIndex.BlockSlices.Values.ToArray();
                    break;
                }
            case DocumentFormatKind.Xlsx:
                await using (var stream = File.OpenRead(source))
                {
                    var extraction = xlsx.Extract(stream);
                    graph = extraction.Graph;
                    AddFormulaDiagnostics(diagnostics, extraction.FormulaDiagnostics);
                    diagnostics.AddRange(extraction.Warnings.Select(warning =>
                        new Diagnostic("XlsxProjectionWarning", warning, DiagnosticSeverity.Warning)));
                }
                break;
            case DocumentFormatKind.Pptx:
                await using (var stream = File.OpenRead(source))
                {
                    var extraction = pptx.Extract(stream);
                    graph = extraction.Graph;
                    diagnostics.AddRange(extraction.Warnings.Select(warning => new Diagnostic("PptxWarning", warning, DiagnosticSeverity.Warning)));
                }
                break;
            case DocumentFormatKind.Pdf:
                graph = PdfGraph(source);
                break;
            default: throw new NotSupportedException("Only DOCX, XLSX, PPTX, and PDF are supported.");
        }
        if (format is DocumentFormatKind.Docx or DocumentFormatKind.Xlsx or DocumentFormatKind.Pptx)
        {
            var packageDiagnostics = InspectOfficePackage(source, out var packageHasMacro);
            macro |= packageHasMacro;
            diagnostics.AddRange(packageDiagnostics);
        }
        if (!string.IsNullOrWhiteSpace(options.DocumentId)) graph = graph with { DocumentId = options.DocumentId };

        var assets = format == DocumentFormatKind.Pdf
            ? await RasterizeTextlessPdfPagesAsync(source, graph, diagnostics, cancellationToken).ConfigureAwait(false)
            : await ExtractOfficeAssetsAsync(source, cancellationToken).ConfigureAwait(false);
        var ocrResults = await CollectOcrAsync(format, graph, assets, options.EnableOcr,
            options.OcrLanguages ?? ["jpn", "eng"], diagnostics, cancellationToken).ConfigureAwait(false);
        graph = AttachAssetsAndOcr(graph, assets, ocrResults);
        AddImageDisplayDiagnostics(diagnostics, graph);

        var serializer = new DocRedockMarkdownSerializer();
        var projection = serializer.Serialize(graph, new MarkdownSerializationOptions
        {
            ProjectionId = "pending",
            RoundTripStore = Path.GetFileName(options.WorkspacePath),
            ContentPolicy = options.ContentPolicy
        });
        var projectionId = "proj_" + Hash(System.Text.Encoding.UTF8.GetBytes(projection.Markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal)))[..16];
        projection = serializer.Serialize(graph, new MarkdownSerializationOptions
        {
            ProjectionId = projectionId,
            RoundTripStore = Path.GetFileName(options.WorkspacePath),
            ContentPolicy = options.ContentPolicy
        });
        var markdownPath = options.MarkdownPath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.WorkspacePath))!, Path.GetFileNameWithoutExtension(source) + ".md");
        var providerSet = ProvidersFor(format, options.EnableOcr && ocr is not null ? ocr.Descriptor : null);
        var workspace = await RoundTripWorkspace.CreateAsync(options.WorkspacePath, source, new RoundTripWorkspaceOptions
        {
            MarkdownPath = markdownPath,
            MarkdownContent = projection.Markdown,
            ProjectionId = projectionId,
            DocumentId = graph.DocumentId,
            SourceFormat = format.ToString().ToLowerInvariant(),
            SourceMacroEnabled = macro,
            ContentPolicy = options.ContentPolicy,
            Profile = options.Profile,
            OcrEnabled = options.EnableOcr,
            EditableRestore = format != DocumentFormatKind.Pdf,
            Render = true,
            GraphChunks = true,
            Providers = providerSet,
            Ocr = CreateOcrManifest(options.EnableOcr, options.OcrLanguages ?? ["jpn", "eng"], ocrResults),
            Preservation = new PreservationInfo
            {
                F1Target = format switch
                {
                    DocumentFormatKind.Docx => "slice-preserving",
                    DocumentFormatKind.Xlsx or DocumentFormatKind.Pptx => "part-payload-identical",
                    _ => "unsupported",
                },
                OriginalSliceIndexed = slices.Count > 0,
            }
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            await workspace.WriteGraphAsync(graph, cancellationToken).ConfigureAwait(false);
            await workspace.WriteProjectionMapAsync(projection.Contributions.Select(DeterministicJson.Serialize), cancellationToken).ConfigureAwait(false);
            await workspace.WriteRawSliceIndexAsync(slices.Select(DeterministicJson.Serialize), cancellationToken).ConfigureAwait(false);
            if (format is DocumentFormatKind.Docx or DocumentFormatKind.Xlsx or DocumentFormatKind.Pptx)
            {
                var indexes = await BuildOfficeIndexesAsync(source, cancellationToken).ConfigureAwait(false);
                await workspace.WriteSourceIndexAsync("package-index.json", DeterministicJson.Serialize(indexes.Package), cancellationToken).ConfigureAwait(false);
                await workspace.WriteSourceIndexAsync("relationship-graph.json", DeterministicJson.Serialize(indexes.Relationships), cancellationToken).ConfigureAwait(false);
            }
            await workspace.WriteChunksAsync(new GraphChunker().Chunk(graph).Select(DeterministicJson.Serialize), cancellationToken).ConfigureAwait(false);

            await workspace.WriteAssetsAsync(assets, cancellationToken).ConfigureAwait(false);
            foreach (var item in ocrResults)
                await workspace.WriteDerivedOcrAsync(item.AssetId, item, cancellationToken).ConfigureAwait(false);
            await workspace.WriteReportAsync("export-service-report.json", new FidelityReport(FidelityLevel.F0, PackagePreservationLevel.ByteIdentical, diagnostics), cancellationToken).ConfigureAwait(false);
            return new(markdownPath, workspace, graph, diagnostics);
        }
        catch
        {
            TryDeleteDirectory(workspace.RootPath);
            TryDeleteFile(markdownPath);
            throw;
        }
    }

    /// <summary>
    /// Exports a one-way, presentation-oriented Markdown file directly from the
    /// extraction graph. No round-trip workspace or sidecar is materialized.
    /// </summary>
    public async Task<ReadableDocumentExportResult> ExportReadableAsync(
        ReadableDocumentExportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var markdownPath = Path.GetFullPath(options.MarkdownPath);
        if (File.Exists(markdownPath)) throw new IOException("Output already exists; refusing to overwrite it.");
        var assetDirectoryPath = Path.Combine(Path.GetDirectoryName(markdownPath)!,
            Path.GetFileNameWithoutExtension(markdownPath) + ".assets");
        var createdAssetDirectory = false;

        try
        {
            var source = Path.GetFullPath(options.SourcePath);
            var (graph, extractedDiagnostics, assets) = await ExtractReadableGraphAsync(source, options, cancellationToken).ConfigureAwait(false);
            var diagnostics = extractedDiagnostics.ToList();
            var imageAssets = assets.Where(asset => asset.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (imageAssets.Length > 0)
            {
                if (options.EmbedImages)
                {
                    var references = new Dictionary<string, string>(StringComparer.Ordinal);
                    var totalEmbeddedBytes = 0;
                    foreach (var asset in imageAssets)
                    {
                        if (asset.Content.Length > MaxTotalEmbeddedImageBytes - totalEmbeddedBytes)
                        {
                            diagnostics.Add(new Diagnostic("ReadableImageEmbedSkipped",
                                $"Image '{asset.FileName}' was omitted from self-contained Markdown: embedding it would exceed the {MaxTotalEmbeddedImageBytes / (1024 * 1024)} MiB total image limit",
                                DiagnosticSeverity.Warning));
                        }
                        else if (TryCreateEmbeddedImageReference(asset, out var reference, out var reason))
                        {
                            references.Add(asset.Id, reference);
                            totalEmbeddedBytes += asset.Content.Length;
                        }
                        else
                            diagnostics.Add(new Diagnostic("ReadableImageEmbedSkipped",
                                $"Image '{asset.FileName}' was omitted from self-contained Markdown: {reason}",
                                DiagnosticSeverity.Warning));
                    }
                    // Do not leave a broken external reference in a self-contained
                    // export when an asset fails the conservative embedding policy.
                    graph = RebindReadableImageReferences(graph, references, omitUnresolvedImages: true);
                }
                else
                {
                    if (File.Exists(assetDirectoryPath) || Directory.Exists(assetDirectoryPath))
                        throw new IOException("Readable Markdown asset output already exists; refusing to overwrite it.");
                    Directory.CreateDirectory(assetDirectoryPath);
                    createdAssetDirectory = true;
                    foreach (var asset in imageAssets)
                        await WriteNewAsync(Path.Combine(assetDirectoryPath, Path.GetFileName(asset.FileName)), asset.Content.ToArray(), cancellationToken).ConfigureAwait(false);
                    graph = RebindReadableImageReferences(graph, imageAssets.ToDictionary(
                        asset => asset.Id,
                        asset => Path.GetFileName(assetDirectoryPath).Replace('\\', '/') + "/" + Path.GetFileName(asset.FileName),
                        StringComparer.Ordinal));
                }
            }
            var markdown = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(
                options.ShowFormulas,
                options.IncludeSvgPreviews,
                options.IncludeDiagrams,
                options.Sheets,
                options.Title)).Serialize(graph);
            await WriteNewAsync(markdownPath, Encoding.UTF8.GetBytes(markdown), cancellationToken).ConfigureAwait(false);
            return new ReadableDocumentExportResult(markdownPath, graph, diagnostics);
        }
        catch
        {
            TryDeleteFile(markdownPath);
            if (createdAssetDirectory) TryDeleteDirectory(assetDirectoryPath);
            throw;
        }
    }

    private async Task<(DocumentGraph Graph, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<WorkspaceAsset> Assets)> ExtractReadableGraphAsync(
        string source,
        ReadableDocumentExportOptions options,
        CancellationToken cancellationToken)
    {
        var format = await DetectFormatAsync(source, cancellationToken).ConfigureAwait(false);
        var diagnostics = new List<Diagnostic>();
        DocumentGraph graph;
        switch (format)
        {
            case DocumentFormatKind.Docx:
                {
                    var extraction = await docx.ExtractAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
                    graph = extraction.Graph;
                    diagnostics.AddRange(extraction.Diagnostics);
                    break;
                }
            case DocumentFormatKind.Xlsx:
                await using (var stream = File.OpenRead(source))
                {
                    var extraction = xlsx.Extract(stream);
                    graph = extraction.Graph;
                    AddFormulaDiagnostics(diagnostics, extraction.FormulaDiagnostics);
                    diagnostics.AddRange(extraction.Warnings.Select(warning =>
                        new Diagnostic("XlsxProjectionWarning", warning, DiagnosticSeverity.Warning)));
                }
                break;
            case DocumentFormatKind.Pptx:
                await using (var stream = File.OpenRead(source))
                {
                    var extraction = pptx.Extract(stream);
                    graph = extraction.Graph;
                    diagnostics.AddRange(extraction.Warnings.Select(warning =>
                        new Diagnostic("PptxWarning", warning, DiagnosticSeverity.Warning)));
                }
                break;
            case DocumentFormatKind.Pdf:
                graph = PdfGraph(source);
                break;
            default:
                throw new NotSupportedException("Only DOCX, XLSX, PPTX, and PDF are supported.");
        }

        if (format is DocumentFormatKind.Docx or DocumentFormatKind.Xlsx or DocumentFormatKind.Pptx)
            diagnostics.AddRange(InspectOfficePackage(source, out _));
        var assets = format == DocumentFormatKind.Pdf
            ? await RasterizeTextlessPdfPagesAsync(source, graph, diagnostics, cancellationToken).ConfigureAwait(false)
            : await ExtractOfficeAssetsAsync(source, cancellationToken).ConfigureAwait(false);
        var ocrResults = await CollectOcrAsync(format, graph, assets, options.EnableOcr,
            options.OcrLanguages ?? ["jpn", "eng"], diagnostics, cancellationToken).ConfigureAwait(false);
        graph = AttachAssetsAndOcr(graph, assets, ocrResults);
        AddImageDisplayDiagnostics(diagnostics, graph);
        return (graph, diagnostics, assets);
    }

    private static DocumentGraph RebindReadableImageReferences(
        DocumentGraph graph,
        IReadOnlyDictionary<string, string> references,
        bool omitUnresolvedImages = false)
    {
        return graph with
        {
            Partitions = graph.Partitions.Select(partition => partition with
            {
                Nodes = partition.Nodes.Select(node =>
                {
                    if (node.Kind != NodeKind.Image || node.Content is not ReferenceNodeContent image ||
                        !references.TryGetValue(image.Reference, out var reference))
                        return omitUnresolvedImages && node.Kind == NodeKind.Image ? null : node;
                    return node with { Content = image with { Reference = reference } };
                }).Where(node => node is not null).Cast<DocumentNode>().ToArray()
            }).ToArray()
        };
    }

    private static bool TryCreateEmbeddedImageReference(WorkspaceAsset asset, out string reference, out string reason)
    {
        reference = string.Empty;
        reason = string.Empty;
        if (asset.Content.Length > MaxEmbeddedImageBytes)
        {
            reason = $"it exceeds the {MaxEmbeddedImageBytes / (1024 * 1024)} MiB per-image limit";
            return false;
        }

        if (!HasSafeImageSignature(asset.MediaType, asset.Content.Span))
        {
            reason = "its type or binary signature is not supported for data-URI embedding";
            return false;
        }

        reference = $"data:{asset.MediaType};base64,{Convert.ToBase64String(asset.Content.Span)}";
        return true;
    }

    private static bool HasSafeImageSignature(string mediaType, ReadOnlySpan<byte> bytes) => mediaType switch
    {
        "image/png" => bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/jpeg" => bytes.Length >= 3 && bytes[..3].SequenceEqual(new byte[] { 255, 216, 255 }),
        "image/gif" => bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)),
        "image/webp" => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
        _ => false,
    };

    private static void AddFormulaDiagnostics(
        ICollection<Diagnostic> diagnostics,
        IReadOnlyList<XlsxFormulaDiagnostic> formulaDiagnostics)
    {
        foreach (var group in formulaDiagnostics.GroupBy(item => item.Safety).OrderBy(group => group.Key))
        {
            var examples = string.Join(", ", group.Take(5).Select(item => item.CellReference));
            var more = group.Count() > 5 ? $" and {group.Count() - 5} more" : string.Empty;
            diagnostics.Add(new Diagnostic(
                "XlsxFormula" + group.Key,
                $"{group.Count()} formula(s) classified as {group.Key} ({examples}{more}).",
                group.Key == XlsxFormulaSafety.Safe ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning));
        }
    }

    public async Task<DocumentDiffResult> DiffAsync(string workspacePath, string? markdownPath = null, CancellationToken cancellationToken = default)
    {
        await using var lease = await SidecarContainer.OpenAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var workspace = await RoundTripWorkspace.OpenAsync(lease.RootPath, cancellationToken).ConfigureAwait(false);
        var projectionPath = markdownPath ?? ProjectionPath(lease, workspace);
        var verification = await workspace.VerifyAsync(projectionPath, requireUnchangedProjection: false, cancellationToken).ConfigureAwait(false);
        if (!verification.IsValid)
            throw new WorkspaceIntegrityException(string.Join("; ", verification.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
        var baseline = await LoadGraphAsync(workspace, cancellationToken).ConfigureAwait(false);
        var markdown = await File.ReadAllTextAsync(projectionPath, cancellationToken).ConfigureAwait(false);
        var edit = new MarkdownGraphEditor().Apply(baseline, markdown);
        var diagnostics = AddSidecarDiagnostics(edit.Diagnostics, lease);
        return new(baseline, edit, diagnostics);
    }

    public async Task<DocumentRestoreResult> RestoreAsync(DocumentRestoreOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using var lease = await SidecarContainer.OpenAsync(options.WorkspacePath, cancellationToken).ConfigureAwait(false);
        var workspace = await RoundTripWorkspace.OpenAsync(lease.RootPath, cancellationToken).ConfigureAwait(false);
        var output = Path.GetFullPath(options.OutputPath);
        if (File.Exists(output)) throw new IOException("Restore output already exists; refusing to overwrite it.");
        var projectionPath = options.MarkdownPath ?? ProjectionPath(lease, workspace);
        var verification = await workspace.VerifyAsync(projectionPath, requireUnchangedProjection: false, cancellationToken).ConfigureAwait(false);
        if (!verification.IsValid)
            throw new WorkspaceIntegrityException(string.Join("; ", verification.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
        var baseline = await LoadGraphAsync(workspace, cancellationToken).ConfigureAwait(false);
        var markdownText = await File.ReadAllTextAsync(projectionPath, cancellationToken).ConfigureAwait(false);
        var edit = new MarkdownGraphEditor().Apply(baseline, markdownText);
        var diff = new DocumentDiffResult(baseline, edit, AddSidecarDiagnostics(edit.Diagnostics, lease));
        if (!diff.Edit.IsValid) return new(output, FidelityLevel.FX, false, diff.Diagnostics);
        var markdown = projectionPath;
        if (!diff.Edit.Diff.DirtySet.HasOriginalMutations)
        {
            await workspace.RestoreOriginalForDiffAsync(output, diff.Edit.Diff, markdown, cancellationToken).ConfigureAwait(false);
            return new(output, FidelityLevel.F0, true, diff.Diagnostics);
        }

        var source = workspace.OriginalSourcePath;
        switch (workspace.Manifest.Source.Format.ToLowerInvariant())
        {
            case "docx":
                {
                    var result = await docx.RestoreAsync(source, diff.Baseline, diff.Edit.EditedGraph, output,
                        new DiffOptions(diff.Edit.Diff.PatchSet.Operations.Where(operation => operation.Kind == PatchOperationKind.ExplicitDelete).Select(operation => operation.NodeId).ToHashSet(StringComparer.Ordinal)), cancellationToken: cancellationToken).ConfigureAwait(false);
                    await workspace.WriteReportAsync("restore-service-report.json", result.Fidelity, cancellationToken).ConfigureAwait(false);
                    return new(output, result.Fidelity.Level, result.Succeeded, AddSidecarDiagnostics(result.Diagnostics, lease));
                }
            case "xlsx":
                {
                    if (diff.Edit.Diff.PatchSet.Operations.Any(operation => operation.MutatesOriginal &&
                        (operation.Kind == PatchOperationKind.ExplicitDelete || operation.After?.Kind != NodeKind.Cell)))
                        return new(output, FidelityLevel.FX, false, AddSidecarDiagnostics(
                            [new("UnsupportedXlsxPatch", "XLSX F1 supports cell value/formula updates and additions, not deletion or structural edits.", DiagnosticSeverity.Error)], lease));
                    await using var input = File.OpenRead(source);
                    var plan = xlsx.CreatePatchPlan(diff.Baseline, diff.Edit.EditedGraph);
                    if (plan.Edits.Count == 0)
                        return new(output, FidelityLevel.FX, false, AddSidecarDiagnostics(
                            [new("UnplannedXlsxChange", "The XLSX DirtySet could not be represented by the cell patch planner.", DiagnosticSeverity.Error)], lease));
                    var result = xlsx.Restore(input, plan);
                    await WriteNewAsync(output, result.Bytes, cancellationToken).ConfigureAwait(false);
                    var diagnostics = result.Warnings.Select(w => new Diagnostic("XlsxWarning", w, DiagnosticSeverity.Warning)).ToArray();
                    await workspace.WriteReportAsync("restore-service-report.json", new FidelityReport(FidelityLevel.F1, PackagePreservationLevel.PartPayloadIdentical, diagnostics,
                        plan.Edits.Select(edit => edit.SheetName + "!" + edit.CellReference).ToArray()), cancellationToken).ConfigureAwait(false);
                    return new(output, FidelityLevel.F1, true, AddSidecarDiagnostics(diagnostics, lease));
                }
            case "pptx":
                {
                    if (diff.Edit.Diff.PatchSet.Operations.Any(operation => operation.MutatesOriginal &&
                        (operation.Kind != PatchOperationKind.ReplaceContent || operation.After?.Kind != NodeKind.Shape)))
                        return new(output, FidelityLevel.FX, false, AddSidecarDiagnostics(
                            [new("UnsupportedPptxPatch", "PPTX F1 supports existing shape text replacement only.", DiagnosticSeverity.Error)], lease));
                    await using var input = File.OpenRead(source);
                    var plan = pptx.CreatePatchPlan(diff.Baseline, diff.Edit.EditedGraph);
                    if (plan.Edits.Count == 0)
                        return new(output, FidelityLevel.FX, false, AddSidecarDiagnostics(
                            [new("UnplannedPptxChange", "The PPTX DirtySet could not be represented by the shape patch planner.", DiagnosticSeverity.Error)], lease));
                    var result = pptx.Restore(input, plan);
                    await WriteNewAsync(output, result.Bytes, cancellationToken).ConfigureAwait(false);
                    var diagnostics = result.Warnings.Select(w => new Diagnostic("PptxWarning", w, DiagnosticSeverity.Warning)).ToArray();
                    await workspace.WriteReportAsync("restore-service-report.json", new FidelityReport(FidelityLevel.F1, PackagePreservationLevel.PartPayloadIdentical, diagnostics,
                        plan.Edits.Select(edit => edit.SlideId + ":" + edit.ShapeId).ToArray()), cancellationToken).ConfigureAwait(false);
                    return new(output, FidelityLevel.F1, true, AddSidecarDiagnostics(diagnostics, lease));
                }
            case "pdf" when options.AllowRenderFallback:
                await renderer.RenderAsync(ToGenericMarkdown(diff.Edit.Markdown), RenderFormat.Pdf, output, cancellationToken: cancellationToken).ConfigureAwait(false);
                await workspace.WriteReportAsync("restore-service-report.json", new FidelityReport(FidelityLevel.F3, PackagePreservationLevel.ReSerialized,
                    [new("PdfRendered", "Edited PDF was rendered as a new PDF; this is not package restore.", DiagnosticSeverity.Warning)]), cancellationToken).ConfigureAwait(false);
                return new(output, FidelityLevel.F3, true, AddSidecarDiagnostics(
                    [new("PdfRendered", "Edited PDF was rendered as a new PDF.", DiagnosticSeverity.Warning)], lease));
            case "pdf":
                return new(output, FidelityLevel.FX, false, AddSidecarDiagnostics(
                    [new("RenderFallbackRequired", "Edited PDF requires explicit AllowRenderFallback.", DiagnosticSeverity.Error)], lease));
            default: return new(output, FidelityLevel.FX, false, AddSidecarDiagnostics(
                [new("UnsupportedFormat", "Workspace format is unsupported.", DiagnosticSeverity.Error)], lease));
        }
    }

    public Task<RenderResult> RenderAsync(DocumentRenderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (File.Exists(Path.GetFullPath(options.OutputPath))) throw new IOException("Render output already exists; refusing to overwrite it.");
        return renderer.RenderAsync(options.Markdown, options.Format, options.OutputPath, options.Options, cancellationToken);
    }

    public async Task<DocumentExportResult> RebaseAsync(DocumentRebaseOptions options, CancellationToken cancellationToken = default)
    {
        // Rebase is explicit: it creates a fresh workspace/baseline and never rewrites an existing one.
        return await ExportAsync(new DocumentExportOptions(options.SourcePath, options.WorkspacePath, options.MarkdownPath, DocumentId: options.DocumentId), cancellationToken).ConfigureAwait(false);
    }

    public static async Task<DocumentFormatKind> DetectFormatAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Document source was not found.", path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var context = new ProbeContext(FileName: path);
        await using var input = await RewindableInput.CreateAsync(stream, context.MaxInputBytes, cancellationToken).ConfigureAwait(false);
        var security = ContainerSecurityGate.Assess(input);
        if (!security.IsAllowed)
            throw new UnauthorizedAccessException(string.Join("; ", security.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
        var probe = await new ContainerFormatDetector().ProbeAsync(input, context, cancellationToken).ConfigureAwait(false);
        if (!probe.IsSupported) return DocumentFormatKind.Unknown;
        return probe.Evidence.FirstOrDefault(item => item.Kind == "ooxml_part")?.Detail switch
        {
            "docx" => DocumentFormatKind.Docx,
            "xlsx" => DocumentFormatKind.Xlsx,
            "pptx" => DocumentFormatKind.Pptx,
            _ when probe.Evidence.Any(item => item.Kind == "magic" && item.Detail == "%PDF-") => DocumentFormatKind.Pdf,
            _ => DocumentFormatKind.Unknown,
        };
    }

    private static DocumentGraph PdfGraph(string path)
    {
        PdfExtractionResult extraction;
        try { extraction = PdfTextExtractor.Extract(path); }
        catch (PdfExtractionException exception)
        {
            throw new InvalidDataException($"PDF text extraction failed: {exception.Message}", exception);
        }
        var partitions = extraction.Pages.Select(page => new DocumentPartition($"page-{page.PageNumber:D4}", page.PageNumber - 1,
            page.Regions.Select(region => new DocumentNode(
                "n_" + Hash($"{path}:{page.PageNumber}:{region.ReadingOrder}")[..16], NodeKind.Paragraph, null, region.ReadingOrder,
                ContentLayer.Body, new TextNodeContent(region.Text),
                new SourceAnchor("pdf", $"pdf:page:{page.PageNumber}", [new AnchorLocator("reading_order", region.ReadingOrder.ToString())]),
                Geometry: region.BoundingBox, Editability: NodeEditability.RenderOnly, Provenance: [new ProvenanceItem(EvidenceKind.Native, PageNumber: page.PageNumber, Bbox: region.BoundingBox)])).ToArray(),
            $"pdf:page:{page.PageNumber}")).ToArray();
        return new(DocumentGraph.CurrentSchemaVersion, "doc_" + Hash(File.ReadAllBytes(path))[..16], DocumentFormatKind.Pdf, partitions);
    }

    private static ProviderSet ProvidersFor(DocumentFormatKind format, ProviderDescriptor? ocrDescriptor) => new()
    {
        FormatAdapter = new ProviderInfo(format switch
        {
            DocumentFormatKind.Docx => "docredock.docx.openxml",
            DocumentFormatKind.Xlsx => "docredock.xlsx.openxml",
            DocumentFormatKind.Pptx => "docredock.pptx.openxml",
            DocumentFormatKind.Pdf => "docredock.pdf.builtin",
            _ => "docredock.adapter.none",
        }, "0.2.0", 1),
        Markdown = new ProviderInfo("docredock.markdown.default", "0.2.0", 1),
        Ocr = ocrDescriptor is null ? new ProviderInfo("docredock.ocr.none", "0.2.0", 1) : new ProviderInfo(ocrDescriptor.ProviderId, ocrDescriptor.ProviderVersion.ToString(), ocrDescriptor.InterfaceVersion) { Sha256 = ocrDescriptor.BinarySha256 }
    };

    private async Task<IReadOnlyList<OcrAssetRecord>> CollectOcrAsync(
        DocumentFormatKind format,
        DocumentGraph graph,
        IReadOnlyList<WorkspaceAsset> assets,
        bool enabled,
        IReadOnlyList<string> languages,
        ICollection<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var records = new List<OcrAssetRecord>();
        foreach (var asset in assets)
        {
            OcrAttemptResult attempt;
            if (!enabled)
                attempt = new(OcrProcessingStatus.SkippedByPolicy, null,
                    [new OcrDiagnostic("OcrDisabled", "OCR was disabled by policy for this asset.", DiagnosticSeverity.Information)]);
            else if (!asset.MediaType.StartsWith("image/", StringComparison.Ordinal))
                attempt = new(OcrProcessingStatus.Unavailable, null,
                    [new OcrDiagnostic("RasterizerUnavailable", "The embedded media requires a rasterizer that is not configured.", DiagnosticSeverity.Warning)]);
            else if (ocr is null)
                attempt = new(OcrProcessingStatus.Unavailable, null, [new OcrDiagnostic("OcrProviderUnavailable", "No OCR provider was explicitly configured.", DiagnosticSeverity.Warning)]);
            else
            {
                await using var image = new MemoryStream(asset.Content.ToArray(), writable: false);
                attempt = await ocr.RecognizeAsync(new OcrInput(asset.Id, image, asset.MediaType), new OcrOptions(languages), cancellationToken).ConfigureAwait(false);
            }
            records.Add(new OcrAssetRecord(asset.Id, attempt.Status, languages, attempt.Result, attempt.Diagnostics));
            foreach (var item in attempt.Diagnostics)
                diagnostics.Add(new Diagnostic(item.Code, item.Message, item.Severity, asset.Id));
        }

        if (format == DocumentFormatKind.Pdf)
        {
            foreach (var partition in graph.Partitions.Where(partition => partition.Nodes.Count == 0 &&
                         records.All(record => !StringComparer.Ordinal.Equals(record.AssetId, partition.Id))))
            {
                var status = enabled ? OcrProcessingStatus.Unavailable : OcrProcessingStatus.SkippedByPolicy;
                var diagnostic = enabled
                    ? new OcrDiagnostic("PdfRasterizerUnavailable", "This page has no native text and no local PDF rasterizer is configured.", DiagnosticSeverity.Warning)
                    : new OcrDiagnostic("OcrDisabled", "OCR was disabled by policy for this textless PDF page.", DiagnosticSeverity.Information);
                records.Add(new OcrAssetRecord(partition.Id, status, languages, null, [diagnostic]));
                diagnostics.Add(new Diagnostic(diagnostic.Code, diagnostic.Message, diagnostic.Severity, partition.Id, partition.SourcePartUri));
            }
        }
        return records;
    }

    private async Task<IReadOnlyList<WorkspaceAsset>> RasterizeTextlessPdfPagesAsync(
        string sourcePath,
        DocumentGraph graph,
        ICollection<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var pages = graph.Partitions.Where(partition => partition.Nodes.Count == 0)
            .Select(partition => int.TryParse(partition.Id.AsSpan("page-".Length), out var page) ? page : partition.Order + 1)
            .ToArray();
        if (pages.Length == 0 || pdfRasterizer is null) return [];
        try
        {
            var rasterized = await pdfRasterizer.RasterizeAsync(sourcePath, pages, new PdfRasterizationOptions(), cancellationToken).ConfigureAwait(false);
            var result = new List<WorkspaceAsset>();
            long totalPixels = 0;
            foreach (var page in rasterized.OrderBy(page => page.PageNumber))
            {
                var pixels = checked((long)page.PixelWidth * page.PixelHeight);
                totalPixels = checked(totalPixels + pixels);
                if (page.PixelWidth <= 0 || page.PixelHeight <= 0 || pixels > 40_000_000 || totalPixels > 200_000_000)
                    throw new InvalidDataException("PDF rasterizer exceeded the configured pixel budget.");
                var extension = page.MediaType == "image/jpeg" ? ".jpg" : ".png";
                var id = $"page-{page.PageNumber:D4}";
                var bytes = page.Content.ToArray();
                result.Add(new WorkspaceAsset(id, id + extension, page.MediaType, Hash(bytes), bytes, $"pdf:page:{page.PageNumber}"));
            }
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic("PdfRasterizationFailed", $"PDF rasterization failed without aborting native extraction: {exception.GetType().Name}.", DiagnosticSeverity.Warning));
            return [];
        }
    }

    private static DocumentGraph AttachAssetsAndOcr(
        DocumentGraph graph,
        IReadOnlyList<WorkspaceAsset> assets,
        IReadOnlyList<OcrAssetRecord> ocrResults)
    {
        var descriptors = assets.ToDictionary(
            asset => asset.Id,
            asset => new AssetDescriptor(asset.Id, asset.Sha256, asset.MediaType, asset.FileName),
            StringComparer.Ordinal);
        graph = AttachAssetReferences(graph, assets);
        var completed = ocrResults.Where(item => item.Status == OcrProcessingStatus.Completed && item.Result is not null && item.Result.Text.Length > 0).ToArray();
        if (completed.Length == 0) return graph with { Assets = descriptors };
        var partitions = graph.Partitions.ToList();
        if (partitions.Count == 0) partitions.Add(new DocumentPartition("part-0001", 0, []));
        var target = partitions[^1];
        var nodes = target.Nodes.ToList();
        foreach (var item in completed)
        {
            var parent = graph.Nodes.FirstOrDefault(node => node.Kind == NodeKind.Image &&
                node.Content is ReferenceNodeContent reference &&
                StringComparer.Ordinal.Equals(FindAsset(reference.Reference, assets)?.Id, item.AssetId));
            var confidence = item.Result!.Regions.Where(region => region.Confidence is not null).Select(region => region.Confidence!.Value).DefaultIfEmpty().Average();
            // D13-4: place OCR text right after its own image (readable renders it inline there)
            // instead of always at the very end of the document, which put it far from its
            // caption/context. Order still falls back to "append at the end" when there is no
            // matching Image node (e.g. a rasterized, textless PDF page has none).
            var ocrOrder = parent is not null ? parent.Order + 1 : nodes.Count == 0 ? 0 : nodes.Max(node => node.Order) + 1;
            nodes.Add(new DocumentNode(
                "n_" + Hash(graph.DocumentId + ":ocr:" + item.AssetId)[..16],
                NodeKind.ImageText,
                parent?.Id,
                ocrOrder,
                ContentLayer.Derived,
                new TextNodeContent(item.Result.Text),
                new SourceAnchor(graph.Format.ToString().ToLowerInvariant(), parent?.Source?.PartUri ?? "asset:" + item.AssetId,
                    [new AnchorLocator("asset_id", item.AssetId)]),
                Editability: NodeEditability.AnnotationOnly,
                Provenance: [new ProvenanceItem(EvidenceKind.Ocr, confidence, DerivedFromNodeId: parent?.Id)]));
        }
        partitions[^1] = target with { Nodes = nodes };
        return graph with { Partitions = partitions, Assets = descriptors };
    }

    private static DocumentGraph AttachAssetReferences(DocumentGraph graph, IReadOnlyList<WorkspaceAsset> assets)
    {
        if (assets.Count == 0) return graph;
        var boundAssetIds = new HashSet<string>(StringComparer.Ordinal);
        var partitions = graph.Partitions.Select(partition => partition with
        {
            Nodes = partition.Nodes.Select(node =>
            {
                if (node.Kind != NodeKind.Image || node.Content is not ReferenceNodeContent reference) return node;
                var asset = FindAsset(reference.Reference, assets);
                if (asset is null) return node;
                boundAssetIds.Add(asset.Id);
                var extensions = node.Extensions is null
                    ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    : new Dictionary<string, JsonElement>(node.Extensions, StringComparer.Ordinal);
                extensions["image_media_type"] = JsonSerializer.SerializeToElement(asset.MediaType);
                return node with
                {
                    Content = reference with { Reference = asset.Id },
                    Extensions = extensions,
                };
            }).ToArray()
        }).ToList();

        var unboundImages = assets
            .Where(asset => asset.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !boundAssetIds.Contains(asset.Id))
            .OrderBy(asset => asset.Id, StringComparer.Ordinal)
            .ToArray();
        if (unboundImages.Length == 0) return graph with { Partitions = partitions };
        if (partitions.Count == 0) partitions.Add(new DocumentPartition("part-0001", 0, []));
        var target = partitions[^1];
        var nodes = target.Nodes.ToList();
        var nextOrder = nodes.Count == 0 ? 0 : nodes.Max(node => node.Order) + 1;
        foreach (var asset in unboundImages)
        {
            nodes.Add(new DocumentNode(
                "n_" + Hash(graph.DocumentId + ":asset:" + asset.Id)[..16],
                NodeKind.Image,
                null,
                nextOrder++,
                ContentLayer.Furniture,
                new ReferenceNodeContent(asset.Id, Path.GetFileNameWithoutExtension(asset.SourcePartUri ?? asset.FileName)),
                new SourceAnchor(graph.Format.ToString().ToLowerInvariant(), asset.SourcePartUri ?? "asset:" + asset.Id,
                    [new AnchorLocator("asset_id", asset.Id)]),
                Editability: NodeEditability.Protected,
                Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["image_media_type"] = JsonSerializer.SerializeToElement(asset.MediaType),
                }));
        }
        partitions[^1] = target with { Nodes = nodes };
        return graph with { Partitions = partitions };
    }

    private static WorkspaceAsset? FindAsset(string reference, IReadOnlyList<WorkspaceAsset> assets)
    {
        var normalizedReference = reference.Replace('\\', '/').TrimStart('/');
        var exact = assets.FirstOrDefault(asset => StringComparer.Ordinal.Equals(asset.Id, reference));
        if (exact is not null) return exact;
        static IEnumerable<string> PartUris(WorkspaceAsset asset) =>
            (asset.SourcePartUri is null ? Array.Empty<string>() : [asset.SourcePartUri])
            .Concat(asset.AliasPartUris);

        var suffixMatches = assets.Where(asset =>
        {
            return PartUris(asset).Any(partUri =>
            {
                var source = partUri.Replace('\\', '/').TrimStart('/');
                return source.Length > 0 && (source.EndsWith(normalizedReference, StringComparison.OrdinalIgnoreCase) ||
                    normalizedReference.EndsWith(source, StringComparison.OrdinalIgnoreCase));
            });
        }).ToArray();
        if (suffixMatches.Length == 1) return suffixMatches[0];
        var fileName = Path.GetFileName(normalizedReference);
        var nameMatches = assets.Where(asset => PartUris(asset)
            .Append(asset.FileName)
            .Any(partUri => StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(partUri), fileName))).ToArray();
        return nameMatches.Length == 1 ? nameMatches[0] : null;
    }

    private static OcrManifestInfo CreateOcrManifest(bool enabled, IReadOnlyList<string> languages, IReadOnlyList<OcrAssetRecord> records)
    {
        int Count(OcrProcessingStatus status) => records.Count(item => item.Status == status);
        return new OcrManifestInfo
        {
            Enabled = enabled,
            Languages = languages,
            StatusSummary = new OcrStatusSummary
            {
                Completed = Count(OcrProcessingStatus.Completed),
                NotRequired = Count(OcrProcessingStatus.NotRequired),
                SkippedByPolicy = Count(OcrProcessingStatus.SkippedByPolicy),
                SkippedByBudget = Count(OcrProcessingStatus.SkippedByBudget),
                Unavailable = Count(OcrProcessingStatus.Unavailable),
                Failed = Count(OcrProcessingStatus.Failed),
            },
        };
    }

    private static async Task<IReadOnlyList<WorkspaceAsset>> ExtractOfficeAssetsAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var result = new List<WorkspaceAsset>();
        var assetsByHash = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var stream = File.OpenRead(sourcePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var index = 0;
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.Contains("/media/", StringComparison.OrdinalIgnoreCase)).OrderBy(entry => entry.FullName, StringComparer.Ordinal))
        {
            await using var content = entry.Open();
            using var output = new MemoryStream();
            await content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            var bytes = output.ToArray();
            var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
            var hash = Hash(bytes);
            var sourcePartUri = "/" + entry.FullName;
            if (assetsByHash.TryGetValue(hash, out var existingIndex))
            {
                var existing = result[existingIndex];
                result[existingIndex] = existing with                {
                    AliasPartUris = existing.AliasPartUris.Append(sourcePartUri).ToArray(),
                };
                continue;
            }
            result.Add(new WorkspaceAsset($"img-{++index:D4}", $"img-{index:D4}{extension}", MediaType(extension), hash, bytes, sourcePartUri));
            assetsByHash.Add(hash, result.Count - 1);
        }
        return result;
    }

    private static IReadOnlyList<Diagnostic> InspectOfficePackage(string sourcePath, out bool hasMacro)
    {
        using var archive = ZipFile.OpenRead(sourcePath);
        hasMacro = archive.Entries.Any(entry => entry.FullName.EndsWith("/vbaProject.bin", StringComparison.OrdinalIgnoreCase));
        var result = new List<Diagnostic>();
        if (hasMacro) result.Add(new Diagnostic("MacroPresent", "A VBA project is preserved but never executed.", DiagnosticSeverity.Warning));
        if (archive.Entries.Any(entry => entry.FullName.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)))
            result.Add(new Diagnostic("SignaturePresent", "Package signatures are preserved for F0; an edited package cannot retain their validity.", DiagnosticSeverity.Warning));
        if (archive.Entries.Any(entry => entry.FullName.Contains("/embeddings/", StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains("/activeX/", StringComparison.OrdinalIgnoreCase)))
            result.Add(new Diagnostic("EmbeddedObjectPresent", "Embedded or ActiveX content is preserved as passthrough and never executed.", DiagnosticSeverity.Warning));
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
            if (reader.ReadToEnd().Contains("TargetMode=\"External\"", StringComparison.OrdinalIgnoreCase))
                result.Add(new Diagnostic("ExternalRelationshipPresent", "External relationships are recorded but never fetched.", DiagnosticSeverity.Warning, PartUri: "/" + entry.FullName));
        }
        return result;
    }

    private static async Task<(OfficePackageIndex Package, IReadOnlyList<OfficeRelationship> Relationships)> BuildOfficeIndexesAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var entries = new List<OfficePackageEntry>();
        var relationships = new List<OfficeRelationship>();
        await using var stream = File.OpenRead(sourcePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
        {
            await using (var content = entry.Open())
                entries.Add(new OfficePackageEntry(entry.FullName, entry.Length, entry.CompressedLength,
                    Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false)).ToLowerInvariant()));
            if (!entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;
            await using var relationshipContent = entry.Open();
            using var reader = XmlReader.Create(relationshipContent, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 16_777_216,
            });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship") continue;
                var id = reader.GetAttribute("Id");
                var type = reader.GetAttribute("Type");
                var target = reader.GetAttribute("Target");
                if (id is null || type is null || target is null) continue;
                relationships.Add(new OfficeRelationship("/" + entry.FullName, id, type, target,
                    StringComparer.OrdinalIgnoreCase.Equals(reader.GetAttribute("TargetMode"), "External")));
            }
        }
        return (new OfficePackageIndex("1.0", entries), relationships);
    }

    private static async Task<DocumentGraph> LoadGraphAsync(RoundTripWorkspace workspace, CancellationToken cancellationToken)
    {
        var path = Path.Combine(workspace.RootPath, "graph", "index.json");
        var graph = DeterministicJson.Deserialize<DocumentGraph>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        return graph ?? throw new WorkspaceIntegrityException("Baseline graph is missing or invalid.");
    }

    private static string ProjectionPath(SidecarLease lease, RoundTripWorkspace workspace) => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(lease.OriginalPath)) ?? workspace.RootPath,
        workspace.Manifest.Projection.FileName);
    private static IReadOnlyList<Diagnostic> AddSidecarDiagnostics(IReadOnlyList<Diagnostic> diagnostics, SidecarLease lease)
    {
        var result = diagnostics.ToList();
        if (lease.Form == SidecarForm.Zip)
            result.Add(new Diagnostic(
                "SidecarZipFormReadOnly",
                "サイドカーは zip 形のため、workspace 内のレポートは保存されません。`docredock unpack <base>.drmd --in-place` で展開してください。",
                DiagnosticSeverity.Information));
        return result;
    }

    private static void AddImageDisplayDiagnostics(ICollection<Diagnostic> diagnostics, DocumentGraph graph)
    {
        foreach (var node in graph.Nodes.Where(node => node.Kind == NodeKind.Image))
        {
            if (node.Extensions is null ||
                !node.Extensions.TryGetValue("image_media_type", out var mediaTypeElement) ||
                mediaTypeElement.ValueKind != JsonValueKind.String ||
                mediaTypeElement.GetString() is not { } mediaType ||
                ImageDisplayPolicy.IsMarkdownDisplayable(mediaType))
                continue;
            diagnostics.Add(new Diagnostic(
                "ImageFormatNotDisplayable",
                $"Image '{node.Id}' uses {mediaType}, which Markdown previews cannot display reliably.",
                DiagnosticSeverity.Warning,
                NodeId: node.Id));
        }
    }
    private static string ToGenericMarkdown(TypedMarkdownDocument document) => string.Join("\n\n", document.Blocks
        .Where(block => !block.IsExplicitDelete)
        .Select(block => block.Kind.ToLowerInvariant() switch
        {
            "heading" or "title" => "# " + block.Text.TrimStart().TrimStart('#').TrimStart(),
            "quote" => string.Join("\n", block.Text.Split('\n').Select(line => line.StartsWith('>') ? line : "> " + line)),
            _ => block.Text,
        }));
    private static string MediaType(string extension) => extension switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".svg" => "image/svg+xml",
        ".emf" => "image/emf",
        ".wmf" => "image/wmf",
        _ => "application/octet-stream",
    };
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Hash(string value) => Hash(System.Text.Encoding.UTF8.GetBytes(value));
    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* Preserve the original export failure. */ }
    }
    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* Preserve the original export failure. */ }
    }
    private static async Task WriteNewAsync(string outputPath, byte[] bytes, CancellationToken cancellationToken)
    {
        if (File.Exists(outputPath)) throw new IOException("Output already exists; refusing to overwrite it.");
        var directory = Path.GetDirectoryName(outputPath) ?? throw new IOException("Output has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try { await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false); File.Move(temporary, outputPath); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}