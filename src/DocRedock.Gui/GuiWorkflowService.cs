using System.Text;
using DocRedock.Api;
using DocRedock.Core.Reporting;
using DocRedock.Ocr.Tesseract;
using DocRedock.RoundTrip;

namespace DocRedock.Gui;

public sealed record GuiExportResult(
    string MarkdownPath,
    string SidecarPath,
    string Format,
    string Fidelity,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool IsReadable = false,
    SidecarForm? SidecarForm = null)
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

    private static readonly HashSet<string> SupportedSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx", ".pdf",
    };

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
        bool zipSidecar = false)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        var extension = Path.GetExtension(sourcePath);
        if (!SupportedSourceExtensions.Contains(extension))
            throw new NotSupportedException("DOCX, XLSX, PPTX, and PDF files are supported.");

        Directory.CreateDirectory(outputDirectory);
        var baseName = SafeBaseName(Path.GetFileNameWithoutExtension(sourcePath));
        if (useUniqueName) baseName = NextAvailableBaseName(outputDirectory, baseName, readable);
        var markdownPath = Path.Combine(outputDirectory, baseName + ".md");
        var sidecarPath = Path.Combine(outputDirectory, baseName + ".drmd");
        var packagePath = Path.Combine(outputDirectory, baseName + ".drmdpkg");
        if (readable)
        {
            EnsureOutputDoesNotExist(markdownPath);
            var readableService = new DocumentService(OcrEngineFactory.CreateDefault());
            try
            {
                var exported = await readableService.ExportReadableAsync(new ReadableDocumentExportOptions(
                    sourcePath,
                    markdownPath,
                    enableOcr,
                    NormalizeLanguages(ocrLanguages),
                    ShowFormulas: showFormulas,
                    IncludeSvgPreviews: includeSvgPreviews,
                    IncludeDiagrams: includeDiagrams,
                    EmbedImages: embedReadableImages), cancellationToken).ConfigureAwait(false);
                return new GuiExportResult(
                    markdownPath,
                    string.Empty,
                    exported.Graph.Format.ToString().ToLowerInvariant(),
                    "Readable Markdown (one-way)",
                    exported.Diagnostics,
                    IsReadable: true);
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
        var service = new DocumentService(OcrEngineFactory.CreateDefault());

        try
        {
            var exported = await service.ExportAsync(new DocumentExportOptions(
                sourcePath,
                sidecarPath,
                markdownPath,
                enableOcr,
                NormalizeLanguages(ocrLanguages)), cancellationToken).ConfigureAwait(false);
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
            return new GuiExportResult(markdownPath, sidecarPath, format, fidelity, exported.Diagnostics, SidecarForm: sidecarForm);
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
            if (!SupportedSourceExtensions.Contains(sourceExtension))
                throw new InvalidDataException("The DocRedock restore information contains an unsupported source format.");

            var baseName = SafeBaseName(Path.GetFileNameWithoutExtension(workspace.Manifest.Source.FileName));
            if (useUniqueName) baseName = NextAvailableRestoreBaseName(outputDirectory, baseName, sourceExtension);
            var outputPath = Path.Combine(outputDirectory, baseName + "-restored" + sourceExtension.ToLowerInvariant());
            EnsureOutputDoesNotExist(outputPath);
            var service = new DocumentService(OcrEngineFactory.CreateDefault());
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
