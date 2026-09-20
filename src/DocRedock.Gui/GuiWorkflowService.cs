using System.Text;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Ocr.Tesseract;
using DocRedock.RoundTrip;
using DocRedock.VisualInference;

namespace DocRedock.Gui;

public sealed record GuiExportResult(
    string MarkdownPath,
    string SidecarPath,
    string Format,
    string Fidelity,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool IsReadable = false,
    SidecarForm? SidecarForm = null,
    VisualInferenceMode InferenceMode = VisualInferenceMode.Safe,
    string? VisualSummary = null,
    CapabilityStatus? PdfRasterizer = null,
    string? ExportSummary = null)
{
    public string PackagePath => SidecarPath;
}

public sealed record GuiRestoreResult(
    string OutputPath,
    string Format,
    string Fidelity,
    bool Succeeded,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// File-oriented desktop workflow. The UI supplies local paths directly; all
/// document processing remains in the same local-only services used by the CLI.
/// </summary>
public sealed class GuiWorkflowService
{
    private static readonly HashSet<string> ReservedWindowsBaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    private static readonly HashSet<string> OfficeSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx",
    };

    private static bool IsSupportedSourceExtension(string extension) =>
        OfficeSourceExtensions.Contains(extension) ||
        extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    private static string SupportedSourceDescription => "DOCX, XLSX, PPTX, and PDF";

    public async Task<GuiExportResult> ExportAsync(
        string sourcePath,
        string outputDirectory,
        bool enableOcr,
        IReadOnlyList<string>? ocrLanguages = null,
        bool readable = false,
        CancellationToken cancellationToken = default,
        bool useUniqueName = false,
        bool showFormulas = false,
        bool includeSvgPreviews = false,
        bool includeDiagrams = true,
        bool embedReadableImages = false,
        bool zipSidecar = false,
        string contentPolicy = "visible",
        VisualInferenceMode inferenceMode = VisualInferenceMode.Safe,
        bool includePdfFallbackImages = true,
        DocRedock.Markdown.OcrReviewMode ocrReview = DocRedock.Markdown.OcrReviewMode.LowConfidence)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        var extension = Path.GetExtension(sourcePath);
        if (!IsSupportedSourceExtension(extension))
        {
            throw new NotSupportedException($"{SupportedSourceDescription} files are supported.");
        }
        _ = DocRedock.Core.Documents.DocumentContentPolicyRules.Parse(contentPolicy);
        // Office and PDF workflows are supported directly by the GUI service.
        // The CLI applies its own explicit experimental-feature gate.

        Directory.CreateDirectory(outputDirectory);
        var baseName = SafeBaseName(Path.GetFileNameWithoutExtension(sourcePath));
        if (useUniqueName) baseName = NextAvailableBaseName(outputDirectory, baseName, readable);
        var markdownPath = Path.Combine(outputDirectory, baseName + ".md");
        var sidecarPath = Path.Combine(outputDirectory, baseName + ".drmd");
        var packagePath = Path.Combine(outputDirectory, baseName + ".drmdpkg");
        if (readable)
        {
            EnsureOutputDoesNotExist(markdownPath);
            var readableService = new DocumentService(OcrEngineFactory.CreateDefault(), DiscoverRasterizer());
            try
            {
                var exported = await readableService.ExportReadableAsync(new ReadableDocumentExportOptions(
                    sourcePath,
                    markdownPath,
                    enableOcr,
                    NormalizeLanguages(ocrLanguages),
                    ContentPolicy: contentPolicy,
                    ShowFormulas: showFormulas,
                    IncludeSvgPreviews: includeSvgPreviews,
                    IncludeDiagrams: includeDiagrams,
                    EmbedImages: embedReadableImages,
                    InferenceMode: inferenceMode, IncludePdfFallbackImages: includePdfFallbackImages, OcrReview: ocrReview), cancellationToken).ConfigureAwait(false);
                // Built once so the two GUI summary lines (this one and ExportSummary below) can
                // never disagree with each other or with the CLI's "Visual summary:" line (F-05).
                var summary = ExportSummaryBuilder.Build(exported.Graph, exported.Diagnostics);
                return new GuiExportResult(
                    markdownPath,
                    string.Empty,
                    exported.Graph.Format.ToString().ToLowerInvariant(),
                    "Readable Markdown (one-way)",
                    AddProjectionDiagnostics(exported.Graph, exported.Diagnostics),
                    IsReadable: true,
                    InferenceMode: exported.InferenceMode,
                    VisualSummary: SummarizeVisualGraph(summary),
                    PdfRasterizer: PdfRasterizerFactory.Describe(Environment.GetEnvironmentVariable("DOCREDOCK_PDF_RASTERIZER"),
                        string.Equals(Environment.GetEnvironmentVariable("DOCREDOCK_DISABLE_PDF_RASTERIZER"), "1", StringComparison.Ordinal)),
                    ExportSummary: summary.ToString());
            }
            catch
            {
                TryDeleteFile(markdownPath);
                TryDeleteDirectory(Path.Combine(Path.GetDirectoryName(markdownPath)!,
                    Path.GetFileNameWithoutExtension(markdownPath) + ".assets"));
                throw;
            }
        }

        EnsureOutputDoesNotExist(markdownPath);
        EnsureOutputDoesNotExist(sidecarPath);
        EnsureOutputDoesNotExist(packagePath);
        var service = new DocumentService(OcrEngineFactory.CreateDefault(), DiscoverRasterizer());

        try
        {
            var exported = await service.ExportAsync(new DocumentExportOptions(
                sourcePath,
                sidecarPath,
                markdownPath,
                enableOcr,
                NormalizeLanguages(ocrLanguages),
                ContentPolicy: contentPolicy,
                InferenceMode: inferenceMode, IncludePdfFallbackImages: includePdfFallbackImages), cancellationToken).ConfigureAwait(false);
            var sidecarForm = SidecarForm.Directory;
            if (zipSidecar)
            {
                await SidecarContainer.PackInPlaceAsync(sidecarPath, markdownPath, cancellationToken).ConfigureAwait(false);
                sidecarForm = SidecarForm.Zip;
            }
            var format = exported.Graph.Format.ToString().ToLowerInvariant();
            var fidelity = format == "pdf"
                ? "F0 baseline / edited PDF is F3"
                : "F0 baseline / supported edits are F1";
            // Built once so the two GUI summary lines (this one and ExportSummary below) can
            // never disagree with each other or with the CLI's "Visual summary:" line (F-05).
            var summary = ExportSummaryBuilder.Build(exported.Graph, exported.Diagnostics);
            return new GuiExportResult(markdownPath, sidecarPath, format, fidelity, AddProjectionDiagnostics(exported.Graph, exported.Diagnostics), SidecarForm: sidecarForm, InferenceMode: exported.InferenceMode, VisualSummary: SummarizeVisualGraph(summary),
                PdfRasterizer: PdfRasterizerFactory.Describe(Environment.GetEnvironmentVariable("DOCREDOCK_PDF_RASTERIZER"),
                    string.Equals(Environment.GetEnvironmentVariable("DOCREDOCK_DISABLE_PDF_RASTERIZER"), "1", StringComparison.Ordinal)),
                ExportSummary: summary.ToString());
        }
        catch
        {
            TryDeleteFile(markdownPath);
            TryDeleteFile(sidecarPath);
            TryDeleteDirectory(sidecarPath);
            throw;
        }
    }

    public async Task<GuiRestoreResult> RestoreAsync(
        string markdownPath,
        string sidecarPath,
        string outputDirectory,
        bool allowPdfRenderFallback,
        CancellationToken cancellationToken = default,
        bool useUniqueName = false)
    {
        markdownPath = Path.GetFullPath(markdownPath);
        sidecarPath = Path.GetFullPath(sidecarPath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (!Path.GetExtension(markdownPath).Equals(".md", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The editable projection must be a .md file.");
        Directory.CreateDirectory(outputDirectory);
        var unpackDirectory = Path.Combine(Path.GetTempPath(), "docredock-gui-restore", Guid.NewGuid().ToString("N"));
        try
        {
            string workspacePath;
            string projectionPath;
            var diagnostics = new List<Diagnostic>();
            if (SidecarContainer.IsBundle(sidecarPath))
            {
                var unpacked = await RoundTripPackage.UnpackAsync(sidecarPath, unpackDirectory, cancellationToken).ConfigureAwait(false);
                await CopyReplacingAsync(markdownPath, unpacked.MarkdownPath, cancellationToken).ConfigureAwait(false);
                workspacePath = unpacked.WorkspacePath;
                projectionPath = unpacked.MarkdownPath;
            }
            else
            {
                _ = SidecarContainer.Detect(sidecarPath);
                workspacePath = sidecarPath;
                projectionPath = markdownPath;
            }

            await using var lease = await SidecarContainer.OpenAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            var workspace = await RoundTripWorkspace.OpenAsync(lease.RootPath, cancellationToken).ConfigureAwait(false);
            var sourceExtension = Path.GetExtension(workspace.Manifest.Source.FileName);
            if (!IsSupportedSourceExtension(sourceExtension))
            {
                throw new InvalidDataException("The DocRedock restore information contains an unsupported source format.");
            }

            var baseName = SafeBaseName(Path.GetFileNameWithoutExtension(workspace.Manifest.Source.FileName));
            if (useUniqueName) baseName = NextAvailableRestoreBaseName(outputDirectory, baseName, sourceExtension);
            var outputPath = Path.Combine(outputDirectory, baseName + "-restored" + sourceExtension.ToLowerInvariant());
            EnsureOutputDoesNotExist(outputPath);
            var service = new DocumentService(OcrEngineFactory.CreateDefault(), DiscoverRasterizer());
            var result = await service.RestoreAsync(new DocumentRestoreOptions(
                workspacePath,
                outputPath,
                projectionPath,
                allowPdfRenderFallback), cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(result.Diagnostics);
            return new GuiRestoreResult(
                outputPath,
                workspace.Manifest.Source.Format.ToLowerInvariant(),
                result.Fidelity.ToString(),
                result.Succeeded,
                diagnostics);
        }
        finally
        {
            TryDeleteDirectory(unpackDirectory);
        }
    }

    /// <summary>Whether the OCR toggle should be enabled and what to tell the user, given the
    /// current engine/native/rasterizer capability probe. Extracted as a pure function so the
    /// decision can be unit-tested without an Avalonia window (see
    /// tests/DocRedock.Tests/Gui/GuiWorkflowServiceOcrCapabilityTests.cs).</summary>
    public readonly record struct OcrCapabilityDecision(bool Enabled, string StatusText);

    /// <summary>
    /// A working OCR *function* (the Tesseract engine, or a native OS provider such as Windows
    /// Media OCR / Apple Vision) is what gates the toggle. The PDF rasterizer (pdftoppm/mutool) is
    /// only needed to OCR image-only PDF pages — never for OCR of images embedded directly in
    /// DOCX/XLSX/PPTX — so an unavailable rasterizer must not disable OCR outright; it is reported
    /// as a separate note instead. This is what was wrong before: on a machine with no rasterizer
    /// installed (the common case on Windows, where pdftoppm/mutool are rarely present) the toggle
    /// was force-disabled even when Windows Media OCR or Tesseract were perfectly usable.
    /// </summary>
    public static OcrCapabilityDecision DescribeOcrCapability(
        CapabilityStatus rasterizer, CapabilityStatus engine, CapabilityStatus native, CapabilityStatus jpn, CapabilityStatus eng)
    {
        var enabled = engine.Status == "ready" || native.Status is "ready" or "partial";
        if (!enabled)
        {
            var actions = new[] { engine.Action, native.Action }
                .Where(action => !string.IsNullOrWhiteSpace(action))
                .Distinct()
                .ToArray();
            var reason = actions.Length > 0
                ? string.Join(" ", actions)
                : "Tesseract と対応言語データを構成するか、OS 標準の OCR 機能を有効にしてください。";
            return new OcrCapabilityDecision(false,
                $"画像/PDFのOCRは現在利用できません（engine: {engine.Status}, native: {native.Status}, rasterizer: {rasterizer.Status}）。{reason}");
        }

        var rasterizerNote = rasterizer.Status == "ready"
            ? string.Empty
            : $"\n注意: 画像のみのPDFページをOCRするにはpdftoppmまたはmutoolが必要です（rasterizer: {rasterizer.Status}）。"
              + (string.IsNullOrWhiteSpace(rasterizer.Action) ? string.Empty : rasterizer.Action + " ")
              + "DOCX/XLSX/PPTXに埋め込まれた画像のOCRには影響しません。";

        if (native.Status == "partial" && engine.Status != "ready")
            return new OcrCapabilityDecision(true,
                $"PDF OCR: Verification pending ({native.Provider})\n画像の最初のOCR時にネイティブプロバイダーを確認します。失敗時は警告を表示します。{rasterizerNote}");

        var providerLabel = engine.Status == "ready" ? engine.Provider : native.Provider;
        // ocr-jpn/ocr-eng describe Tesseract data, not the native provider's languages.
        var languageNote = engine.Status == "ready"
            ? $"言語: 日本語 {jpn.Status}, English {eng.Status}"
            : "言語: OS 標準 OCR の導入済み言語を使用します。";
        return new OcrCapabilityDecision(true,
            $"PDF OCR: Ready ({providerLabel})\n{languageNote}{rasterizerNote}");
    }

    private static IReadOnlyList<Diagnostic> AddProjectionDiagnostics(
        DocumentGraph graph,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        if (graph.Nodes.Any() ||
            diagnostics.Any(diagnostic => diagnostic.Code == "EmptyProjection"))
            return diagnostics;

        return diagnostics.Append(new Diagnostic(
            "EmptyProjection",
            "No extractable content was found in the document projection.",
            DiagnosticSeverity.Warning)).ToArray();
    }

    private static DocRedock.Providers.Abstractions.Providers.IPdfRasterizer? DiscoverRasterizer() =>
        PdfRasterizerFactory.Discover(Environment.GetEnvironmentVariable("DOCREDOCK_PDF_RASTERIZER"),
            string.Equals(Environment.GetEnvironmentVariable("DOCREDOCK_DISABLE_PDF_RASTERIZER"), "1", StringComparison.Ordinal));

    // Renders from the same finalized ExportSummary the CLI's "Visual summary:" line uses (F-05),
    // so DiagramsReconstructed/FallbackPaths/etc. mean the same thing in both surfaces and this
    // line can never disagree with the ExportSummary line shown right below it in the GUI.
    private static string? SummarizeVisualGraph(ExportSummary summary)
    {
        if (summary.VectorPages == 0) return null;
        return $"図: {summary.DiagramsReconstructed}件（vector page {summary.VectorPages}） / native {summary.NativeEdges} / high {summary.HighConfidenceEdges} / medium {summary.MediumConfidenceEdges} / unresolved {summary.UnresolvedRelations} / fallback {summary.FallbackPaths}";
    }

    public static string SafeFileName(string untrustedName, string fallbackBaseName)
    {
        var fileName = Path.GetFileName(untrustedName.Replace('\\', '/'));
        var extension = Path.GetExtension(fileName);
        var baseName = SafeBaseName(Path.GetFileNameWithoutExtension(fileName), fallbackBaseName);
        return baseName + extension.ToLowerInvariant();
    }

    private static string SafeBaseName(string? value, string fallback = "document")
    {
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        var builder = new StringBuilder();
        foreach (var character in value ?? string.Empty)
        {
            if (invalid.Contains(character) || char.IsControl(character)) continue;
            builder.Append(character);
            if (builder.Length == 80) break;
        }
        var result = builder.ToString().Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(result)) return fallback;
        return ReservedWindowsBaseNames.Contains(result) ? "_" + result : result;
    }

    private static IReadOnlyList<string> NormalizeLanguages(IReadOnlyList<string>? languages)
    {
        var normalized = (languages is null || languages.Count == 0 ? ["jpn", "eng"] : languages)
            .SelectMany(value => value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => value.Length is > 0 and <= 16 && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        return normalized.Length == 0 ? ["jpn", "eng"] : normalized;
    }

    private static async Task CopyReplacingAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureOutputDoesNotExist(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException($"既存の出力を保護するため停止しました: {path}");
    }

    private static string NextAvailableBaseName(string directory, string baseName, bool readable)
    {
        for (var index = 1; ; index++)
        {
            var suffix = index == 1 ? string.Empty : $" ({index})";
            var candidate = Path.Combine(directory, baseName + suffix);
            var paths = readable
                ? new[] { candidate + ".md" }
                : new[] { candidate + ".md", candidate + ".drmd", candidate + ".drmdpkg" };
            if (paths.All(path => !File.Exists(path) && !Directory.Exists(path))) return baseName + suffix;
        }
    }

    private static string NextAvailableRestoreBaseName(string directory, string baseName, string extension)
    {
        for (var index = 1; ; index++)
        {
            var suffix = index == 1 ? string.Empty : $" ({index})";
            var path = Path.Combine(directory, baseName + suffix + "-restored" + extension.ToLowerInvariant());
            if (!File.Exists(path) && !Directory.Exists(path)) return baseName + suffix;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

}
