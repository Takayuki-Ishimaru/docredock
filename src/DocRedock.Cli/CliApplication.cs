using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using DocRedock.Ocr.Tesseract;
using DocRedock.Render;
using DocRedock.RoundTrip;

namespace DocRedock.Cli;

public enum ExitCode
{
    Success = 0, SuccessWithWarnings = 1, InvalidInput = 2, WorkspaceInvalid = 3,
    Unsupported = 4, SecurityPolicyViolation = 5, RestoreConflict = 6,
    ValidationFailed = 7, OcrPartialFailure = 8, LicenseValidationFailed = 9, InternalError = 10,
}

public sealed class CliApplication(TextWriter output, TextWriter error, DocumentService? documentService = null)
{
    private DocumentService Service { get; } = documentService ?? new DocumentService(OcrEngineFactory.CreateDefault());

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { WriteHelp(); return 0; }
        try
        {
            var parsed = Arguments.Parse(args[1..]);
            return args[0].ToLowerInvariant() switch
            {
                "export" => await ExportAsync(parsed, cancellationToken),
                "restore" => await RestoreAsync(parsed, cancellationToken),
                "render" => await RenderAsync(parsed, cancellationToken),
                "inspect" => await InspectAsync(parsed, cancellationToken),
                "diff" => await DiffAsync(parsed, cancellationToken),
                "verify" => await VerifyAsync(parsed, cancellationToken),
                "rebase" => await RebaseAsync(parsed, cancellationToken),
                "pack" => await PackAsync(parsed, cancellationToken),
                "unpack" => await UnpackAsync(parsed, cancellationToken),
                "rules" => await RulesAsync(parsed, cancellationToken),
                "migrate" => await MigrateAsync(parsed, cancellationToken),
                _ => Invalid($"Unknown command '{args[0]}'."),
            };
        }
        catch (OperationCanceledException) { await error.WriteLineAsync("Operation cancelled."); return (int)ExitCode.InvalidInput; }
        catch (WorkspaceIntegrityException ex) { await error.WriteLineAsync($"Workspace verification failed: {ex.Message}"); return (int)ExitCode.WorkspaceInvalid; }
        catch (UnauthorizedAccessException ex) { await error.WriteLineAsync(ex.Message); return (int)ExitCode.SecurityPolicyViolation; }
        catch (NotSupportedException ex) { await error.WriteLineAsync(ex.Message); return (int)ExitCode.Unsupported; }
        catch (InvalidDataException ex) { await error.WriteLineAsync(ex.Message); return (int)ExitCode.ValidationFailed; }
        catch (InvalidOperationException ex) { await error.WriteLineAsync(ex.Message); return (int)ExitCode.RestoreConflict; }
        catch (FileNotFoundException ex) { await error.WriteLineAsync(ex.Message); return (int)ExitCode.InvalidInput; }
        catch (IOException ex) { await error.WriteLineAsync(ex.Message); return (int)ExitCode.InvalidInput; }
        catch (Exception ex)
        {
            // Keep the type for diagnostics, but include the message so a CLI
            // user can act on malformed input without reproducing under a debugger.
            await error.WriteLineAsync($"Internal error: {ex.GetType().Name}: {ex.Message}");
            return (int)ExitCode.InternalError;
        }
    }

    private async Task<int> ExportAsync(Arguments args, CancellationToken token)
    {
        var source = RequireExistingFile(args);
        var profile = args.Option("profile") ?? "roundtrip";
        if (profile is not ("roundtrip" or "readable" or "audit")) return Unsupported("Built-in export supports roundtrip, readable, and audit profiles.");
        var markdown = Path.GetFullPath(args.Option("output") ?? Path.ChangeExtension(source, ".md"));
        var ocrMode = (args.Option("ocr") ?? "auto").ToLowerInvariant();
        if (ocrMode is not ("auto" or "on" or "off")) return Invalid("--ocr must be auto, on, or off.");
        var contentPolicy = (args.Option("content-policy") ?? "visible").ToLowerInvariant();
        if (contentPolicy is not ("visible" or "complete" or "sanitized"))
            return Invalid("--content-policy must be visible, complete, or sanitized.");
        var languages = (args.Option("ocr-lang") ?? "jpn+eng").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (languages.Length == 0) return Invalid("--ocr-lang must contain at least one language identifier.");
        var force = args.HasFlag("force");
        var quiet = args.HasFlag("quiet");
        if (args.HasFlag("sidecar")) return Invalid("export --sidecar requires dir or zip.");
        var sidecarForm = args.Option("sidecar") ?? "dir";
        if (sidecarForm is not ("dir" or "zip")) return Invalid("--sidecar must be dir or zip.");
        if (profile == "readable")
        {
            var embedImages = args.HasFlag("embed-images");
            var readableAssets = Path.Combine(Path.GetDirectoryName(markdown)!, Path.GetFileNameWithoutExtension(markdown) + ".assets");
            var readableTargets = embedImages ? new[] { markdown } : new[] { markdown, readableAssets };
            using var stagedOutputs = new StagedOutputTransaction(readableTargets, force);
            var stagedMarkdown = stagedOutputs.PathFor(markdown);
            var sheets = args.Option("sheets")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var readable = await Service.ExportReadableAsync(new ReadableDocumentExportOptions(
                source, stagedMarkdown, ocrMode != "off", languages, contentPolicy,
                ShowFormulas: args.HasFlag("show-formulas"),
                IncludeSvgPreviews: args.HasFlag("svg-previews"),
                IncludeDiagrams: !args.HasFlag("no-diagrams"),
                Sheets: sheets,
                Title: args.Option("title"),
                EmbedImages: embedImages), token);
            stagedOutputs.Commit();
            await output.WriteLineAsync($"Exported: {markdown}");
            await output.WriteLineAsync($"Format:   {readable.Graph.Format.ToString().ToLowerInvariant()}");
            await output.WriteLineAsync("Mode:     Readable Markdown (one-way; no sidecar)");
            foreach (var item in readable.Diagnostics.Where(item => !quiet || item.Severity != DocRedock.Core.Reporting.DiagnosticSeverity.Information))
                await output.WriteLineAsync($"{item.Severity.ToString().ToUpperInvariant()} {item.Code}: {item.Message}");
            if (!readable.Graph.Nodes.Any())
            {
                await output.WriteLineAsync("WARNING EmptyProjection: no extractable content was found.");
                return 1;
            }
            return readable.Diagnostics.Any(item => item.Severity != DocRedock.Core.Reporting.DiagnosticSeverity.Information) ? 1 : 0;
        }

        var sidecarPath = SidecarFor(markdown);
        using var stagedRoundTrip = new StagedOutputTransaction([markdown, sidecarPath], force);
        var stagedRoundTripMarkdown = stagedRoundTrip.PathFor(markdown);
        var stagedSidecar = stagedRoundTrip.PathFor(sidecarPath);
        var result = await Service.ExportAsync(new DocumentExportOptions(source, stagedSidecar, stagedRoundTripMarkdown,
            ocrMode != "off", languages, contentPolicy, Profile: profile), token);
        if (sidecarForm == "zip")
            await SidecarContainer.PackInPlaceAsync(result.Workspace.RootPath, stagedRoundTripMarkdown, token);
        stagedRoundTrip.Commit();
        await output.WriteLineAsync($"Exported: {markdown}");
        await output.WriteLineAsync($"Sidecar:  {sidecarPath} ({(sidecarForm == "zip" ? "zip" : "directory")})");
        await output.WriteLineAsync($"Format:   {result.Graph.Format.ToString().ToLowerInvariant()}");
        await output.WriteLineAsync(result.Graph.Format == DocumentFormatKind.Pdf
            ? "Fidelity: F0 baseline; edited PDF requires explicit F3 render fallback"
            : "Fidelity: F0 baseline; supported Office edits use F1");
        foreach (var item in result.Diagnostics.Where(item => !quiet || item.Severity != DocRedock.Core.Reporting.DiagnosticSeverity.Information))
            await output.WriteLineAsync($"{item.Severity.ToString().ToUpperInvariant()} {item.Code}: {item.Message}");
        if (!result.Graph.Nodes.Any()) { await output.WriteLineAsync("WARNING EmptyProjection: no extractable content was found."); return 1; }
        return result.Diagnostics.Any(item => item.Severity != DocRedock.Core.Reporting.DiagnosticSeverity.Information) ? 1 : 0;
    }

    private async Task<int> RestoreAsync(Arguments args, CancellationToken token)
    {
        if (args.HasFlag("strict")) return Invalid("--strict was removed because strict Markdown validation is always enabled.");
        var markdown = RequireExistingFile(args);
        var parsed = await ParseMarkdownAsync(markdown, token); WriteMarkdownDiagnostics(parsed);
        if (!parsed.IsComplete) return (int)ExitCode.WorkspaceInvalid;
        var workspacePath = ResolveWorkspace(markdown, parsed.RoundTripStore);
        await using var lease = await SidecarContainer.OpenAsync(workspacePath, token);
        var workspace = await RoundTripWorkspace.OpenAsync(lease.RootPath, token);
        var destination = Path.GetFullPath(args.Option("output") ?? Path.Combine(Path.GetDirectoryName(markdown)!,
            Path.GetFileNameWithoutExtension(markdown) + "-restored" + Path.GetExtension(workspace.Manifest.Source.FileName)));
        using var stagedOutput = new StagedOutputTransaction([destination], args.HasFlag("force"));
        var stagedDestination = stagedOutput.PathFor(destination);
        var result = await Service.RestoreAsync(new DocumentRestoreOptions(lease.RootPath, stagedDestination, markdown,
            args.HasFlag("allow-render-fallback")), token);
        foreach (var item in result.Diagnostics)
        {
            var writer = item.Severity == DocRedock.Core.Reporting.DiagnosticSeverity.Error ? error : output;
            await writer.WriteLineAsync($"{item.Severity.ToString().ToUpperInvariant()} {item.Code}: {item.Message}");
        }
        if (lease.Form == SidecarForm.Zip)
            await output.WriteLineAsync("INFORMATION SidecarZipFormReadOnly: サイドカーは zip 形のため、workspace 内のレポートは保存されません。`docredock unpack <base>.drmd --in-place` で展開してください。");
        if (!result.Succeeded) return (int)ExitCode.RestoreConflict;
        stagedOutput.Commit();
        await output.WriteLineAsync($"Restored: {destination}"); await output.WriteLineAsync($"Fidelity: {result.Fidelity}");
        return result.Diagnostics.Any(item => item.Severity == DocRedock.Core.Reporting.DiagnosticSeverity.Warning) ? 1 : 0;
    }

    private async Task<int> RenderAsync(Arguments args, CancellationToken token)
    {
        var input = RequireExistingFile(args);
        var value = args.Option("format") ?? throw new IOException("render requires --format docx|pptx|xlsx|pdf.");
        if (!Enum.TryParse<RenderFormat>(value, true, out var format)) return Unsupported($"Unsupported render format '{value}'.");
        var destination = Path.GetFullPath(args.Option("output") ?? Path.ChangeExtension(input, "." + value.ToLowerInvariant()));
        using var stagedOutput = new StagedOutputTransaction([destination], args.HasFlag("force"));
        var result = await Service.RenderAsync(new DocumentRenderOptions(await File.ReadAllTextAsync(input, token), stagedOutput.PathFor(destination), format,
            new RenderOptions(TemplatePath: args.Option("template"), MermaidExecutablePath: args.Option("mermaid-cli") ?? "mmdc")), token);
        stagedOutput.Commit();
        await output.WriteLineAsync($"Rendered: {destination}");
        await output.WriteLineAsync($"Fidelity: {result.FidelityLevel} (new document, not restore)");
        foreach (var warning in result.Warnings) await output.WriteLineAsync($"WARNING Render: {warning}");
        return result.Warnings.Count == 0 ? 0 : 1;
    }

    private async Task<int> InspectAsync(Arguments args, CancellationToken token)
    {
        var path = RequireExistingFile(args);
        if (Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = await ParseMarkdownAsync(path, token);
            if (!parsed.IsComplete) { WriteMarkdownDiagnostics(parsed); return 3; }
            var workspacePath = ResolveWorkspace(path, parsed.RoundTripStore);
            await using var lease = await SidecarContainer.OpenAsync(workspacePath, token);
            var workspace = await RoundTripWorkspace.OpenAsync(lease.RootPath, token);
            var report = await workspace.VerifyAsync(path, false, token);
            await output.WriteLineAsync($"Document ID: {workspace.Manifest.DocumentId}");
            await output.WriteLineAsync($"Format: {workspace.Manifest.Source.Format}");
            await output.WriteLineAsync($"Macro enabled: {workspace.Manifest.Source.MacroEnabled.ToString().ToLowerInvariant()}");
            await output.WriteLineAsync($"Projection changed: {report.ProjectionChanged.ToString().ToLowerInvariant()}");
            await output.WriteLineAsync($"Byte restore: {workspace.Manifest.Capabilities.ByteRestore.ToString().ToLowerInvariant()}");
            await output.WriteLineAsync($"Editable restore: {workspace.Manifest.Capabilities.EditableRestore.ToString().ToLowerInvariant()}");
            await output.WriteLineAsync($"OCR completed/unavailable/failed: {workspace.Manifest.Ocr.StatusSummary.Completed}/{workspace.Manifest.Ocr.StatusSummary.Unavailable}/{workspace.Manifest.Ocr.StatusSummary.Failed}");
            return report.IsValid ? 0 : 3;
        }
        var format = await DocumentService.DetectFormatAsync(path, token);
        await output.WriteLineAsync($"Format: {format.ToString().ToLowerInvariant()}"); await output.WriteLineAsync("Network access: denied");
        if (format == DocumentFormatKind.Unknown) return 4;
        if (format is DocumentFormatKind.Docx or DocumentFormatKind.Xlsx or DocumentFormatKind.Pptx)
        {
            using var archive = ZipFile.OpenRead(path);
            await output.WriteLineAsync($"Macro enabled: {archive.Entries.Any(entry => entry.FullName.EndsWith("/vbaProject.bin", StringComparison.OrdinalIgnoreCase)).ToString().ToLowerInvariant()}");
            await output.WriteLineAsync($"External relationships: {CountExternalRelationships(archive)}");
            await output.WriteLineAsync($"Embedded media: {archive.Entries.Count(entry => entry.FullName.Contains("/media/", StringComparison.OrdinalIgnoreCase))}");
        }
        await output.WriteLineAsync(format == DocumentFormatKind.Pdf ? "Restore: F0; edited content requires explicit F3 render fallback" : "Restore: F0/F1 after export");
        return 0;
    }

    private async Task<int> DiffAsync(Arguments args, CancellationToken token)
    {
        var markdown = RequireExistingFile(args); var parsed = await ParseMarkdownAsync(markdown, token);
        if (!parsed.IsComplete) { WriteMarkdownDiagnostics(parsed); return 3; }
        var workspacePath = ResolveWorkspace(markdown, parsed.RoundTripStore);
        await using var lease = await SidecarContainer.OpenAsync(workspacePath, token);
        var result = await Service.DiffAsync(lease.RootPath, markdown, token);
        if (args.HasFlag("json")) await output.WriteLineAsync(DeterministicJson.Serialize(result.Edit.Diff));
        else
        {
            await output.WriteLineAsync($"Operations: {result.Edit.Diff.PatchSet.Operations.Count}");
            await output.WriteLineAsync($"Original mutations: {result.Edit.Diff.DirtySet.Nodes.Count(node => node.MutatesOriginal)}");
            foreach (var operation in result.Edit.Diff.PatchSet.Operations)
                await output.WriteLineAsync($"{operation.Kind}: {operation.NodeId} (mutates_original={operation.MutatesOriginal.ToString().ToLowerInvariant()})");
        }
        if (lease.Form == SidecarForm.Zip)
            await output.WriteLineAsync("INFORMATION SidecarZipFormReadOnly: サイドカーは zip 形のため、workspace 内のレポートは保存されません。`docredock unpack <base>.drmd --in-place` で展開してください。");
        return result.Edit.IsValid ? 0 : 6;
    }

    private async Task<int> VerifyAsync(Arguments args, CancellationToken token)
    {
        var path = RequireExistingPath(args);
        if (SidecarContainer.IsBundle(path))
        {
            var parent = Path.Combine(Path.GetTempPath(), "docredock-verify", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(parent);
            try { var unpacked = await RoundTripPackage.UnpackAsync(path, Path.Combine(parent, "content"), token); return await VerifyMarkdownAsync(unpacked.MarkdownPath, token); }
            finally { if (Directory.Exists(parent)) Directory.Delete(parent, true); }
        }
        if (File.Exists(path) && Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)) return await VerifyMarkdownAsync(path, token);
        return await VerifySidecarAsync(path, token);
    }

    private async Task<int> VerifyMarkdownAsync(string markdown, CancellationToken token)
    {
        var parsed = await ParseMarkdownAsync(markdown, token); WriteMarkdownDiagnostics(parsed); if (!parsed.IsComplete) return 3;
        var workspacePath = ResolveWorkspace(markdown, parsed.RoundTripStore);
        await using var lease = await SidecarContainer.OpenAsync(workspacePath, token);
        var workspace = await RoundTripWorkspace.OpenAsync(lease.RootPath, token);
        var report = await workspace.VerifyAsync(markdown, false, token);
        foreach (var issue in report.Issues) await error.WriteLineAsync($"ERROR {issue.Code}: {issue.Message}");
        foreach (var warning in report.Warnings) await output.WriteLineAsync($"WARNING {warning.Code}: {warning.Message}");
        if (!report.IsValid) return 3;
        await WriteVerificationSummaryAsync(report.ProjectionChanged);
        return report.Warnings.Count == 0 ? 0 : 1;
    }

    private async Task<int> VerifySidecarAsync(string sidecarPath, CancellationToken token)
    {
        await using var lease = await SidecarContainer.OpenAsync(sidecarPath, token);
        var workspace = await RoundTripWorkspace.OpenAsync(lease.RootPath, token);
        var markdown = Path.Combine(Path.GetDirectoryName(lease.OriginalPath)!, Path.GetFileName(workspace.Manifest.Projection.FileName));
        var report = await workspace.VerifyAsync(markdown, false, token);
        foreach (var issue in report.Issues) await error.WriteLineAsync($"ERROR {issue.Code}: {issue.Message}");
        foreach (var warning in report.Warnings) await output.WriteLineAsync($"WARNING {warning.Code}: {warning.Message}");
        if (!report.IsValid) return 3;
        await WriteVerificationSummaryAsync(report.ProjectionChanged);
        return report.Warnings.Count == 0 ? 0 : 1;
    }

    private async Task<int> RebaseAsync(Arguments args, CancellationToken token)
    {
        var markdown = RequireExistingFile(args);
        var source = args.Option("source") is { } value && File.Exists(Path.GetFullPath(value)) ? Path.GetFullPath(value) : throw new FileNotFoundException("rebase requires an existing --source document.");
        var parsed = await ParseMarkdownAsync(markdown, token);
        if (!parsed.IsComplete) { WriteMarkdownDiagnostics(parsed); return 3; }
        var currentPath = ResolveWorkspace(markdown, parsed.RoundTripStore);
        await using var lease = await SidecarContainer.OpenAsync(currentPath, token);
        var current = await RoundTripWorkspace.OpenAsync(lease.RootPath, token);
        var outputMarkdown = Path.GetFullPath(args.Option("output") ?? Path.Combine(Path.GetDirectoryName(markdown)!, Path.GetFileNameWithoutExtension(markdown) + "-rebased.md"));
        var result = await Service.RebaseAsync(new DocumentRebaseOptions(source, SidecarFor(outputMarkdown), outputMarkdown, current.Manifest.DocumentId), token);
        await output.WriteLineAsync($"Rebased baseline: {result.MarkdownPath}"); await output.WriteLineAsync("The previous baseline was not modified."); return 0;
    }

    private async Task<int> PackAsync(Arguments args, CancellationToken token)
    {
        var markdown = RequireExistingFile(args);
        if (args.HasFlag("sidecar"))
        {
            if (args.HasFlag("in-place") == (args.Option("output") is not null))
                return Invalid("pack --sidecar requires exactly one of --in-place or --output.");
            var parsed = await ParseMarkdownAsync(markdown, token);
            if (!parsed.IsComplete) { WriteMarkdownDiagnostics(parsed); return 3; }
            var sidecar = ResolveWorkspace(markdown, parsed.RoundTripStore);
            if (args.HasFlag("in-place"))
            {
                var packed = await SidecarContainer.PackInPlaceAsync(sidecar, markdown, token);
                await output.WriteLineAsync($"Packed sidecar: {packed} (zip)");
            }
            else
            {
                var packed = await SidecarContainer.PackToAsync(sidecar, markdown, Path.GetFullPath(args.Option("output")!), token);
                await output.WriteLineAsync($"Packed sidecar: {packed} (zip)");
            }
            return 0;
        }
        var package = Path.GetFullPath(args.Option("output") ?? Path.ChangeExtension(markdown, ".drmdpkg"));
        using var stagedOutput = new StagedOutputTransaction([package], args.HasFlag("force"));
        var markdownDocument = await ParseMarkdownAsync(markdown, token);
        if (!markdownDocument.IsComplete) { WriteMarkdownDiagnostics(markdownDocument); return 3; }
        var result = await RoundTripPackage.PackAsync(markdown, ResolveWorkspace(markdown, markdownDocument.RoundTripStore), stagedOutput.PathFor(package), token);
        stagedOutput.Commit();
        await output.WriteLineAsync($"Packed: {package} ({result.EntryCount} entries)"); return 0;
    }

    private async Task<int> UnpackAsync(Arguments args, CancellationToken token)
    {
        var package = RequireExistingFile(args);
        if (SidecarContainer.IsBundle(package))
        {
            var destination = Path.GetFullPath(args.Option("output") ?? Path.Combine(Path.GetDirectoryName(package)!, Path.GetFileNameWithoutExtension(package)));
            using var stagedOutput = new StagedOutputTransaction([destination], args.HasFlag("force"));
            var result = await RoundTripPackage.UnpackAsync(package, stagedOutput.PathFor(destination), token);
            stagedOutput.Commit();
            await output.WriteLineAsync($"Unpacked: {destination} ({result.EntryCount} entries)"); return 0;
        }
        if (args.HasFlag("in-place") == (args.Option("output") is not null))
            return Invalid("unpack sidecar requires exactly one of --in-place or --output.");
        await using var lease = await SidecarContainer.OpenAsync(package, token);
        if (lease.Form != SidecarForm.Zip) return Invalid("unpack requires a zip-form sidecar.");
        var workspace = await RoundTripWorkspace.OpenAsync(lease.RootPath, token);
        var markdown = Path.Combine(Path.GetDirectoryName(package)!, Path.GetFileName(workspace.Manifest.Projection.FileName));
        if (args.HasFlag("in-place"))
        {
            var unpacked = await SidecarContainer.UnpackInPlaceAsync(package, markdown, token);
            await output.WriteLineAsync($"Unpacked sidecar: {unpacked} (directory)");
        }
        else
        {
            var unpacked = await SidecarContainer.UnpackToAsync(package, markdown, Path.GetFullPath(args.Option("output")!), token);
            await output.WriteLineAsync($"Unpacked sidecar: {unpacked} (directory)");
        }
        return 0;
    }

    private async Task<int> LicensesAsync(Arguments args, CancellationToken token)
    {
        if (args.Positionals.Count != 0) return Invalid("licenses does not accept an input file.");
        var root = FindRepositoryRoot(); var path = Path.Combine(root, "licenses", "allowlist.json"); if (!File.Exists(path)) return LicenseFailure("License allowlist was not found.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, token)); var packages = document.RootElement.GetProperty("packages").EnumerateArray().ToArray();
        if (args.HasFlag("json")) await output.WriteLineAsync(document.RootElement.GetRawText());
        else { await output.WriteLineAsync($"Allowlisted dependencies: {packages.Length}"); foreach (var item in packages.OrderBy(item => item.GetProperty("id").GetString(), StringComparer.OrdinalIgnoreCase)) await output.WriteLineAsync($"{item.GetProperty("id").GetString()} {item.GetProperty("version").GetString()} — {item.GetProperty("license").GetString()}"); }
        if (args.HasFlag("verify"))
        {
            var violations = VerifyLockedPackages(root, packages); foreach (var item in violations) await error.WriteLineAsync(item);
            if (violations.Count > 0) return 9; await output.WriteLineAsync("License verification: passed");
        }
        return 0;
    }

    private async Task<int> RulesAsync(Arguments args, CancellationToken token)
    {
        if (args.Positionals.Count != 0) return Invalid("rules does not accept an input file.");
        await using var stream = typeof(CliApplication).Assembly.GetManifestResourceStream("DocRedock.DRMD_AI_EDITING_RULES.md")
            ?? throw new InvalidDataException("The embedded DRMD AI editing rules are missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var rules = await reader.ReadToEndAsync(token);
        await output.WriteAsync(rules);
        if (!rules.EndsWith('\n')) await output.WriteLineAsync();
        return (int)ExitCode.Success;
    }

    private async Task<int> MigrateAsync(Arguments args, CancellationToken token)
    {
        var markdown = RequireExistingFile(args); var target = args.Option("to-schema") ?? throw new IOException("migrate requires --to-schema.");
        if (target != RoundTripWorkspace.CurrentSchemaVersion) return Unsupported($"Migration target '{target}' is not supported.");
        var code = await VerifyMarkdownAsync(markdown, token); if (code is 0 or 1) await output.WriteLineAsync("Workspace already uses schema 1.1; no migration was required."); return code;
    }

    private async Task WriteVerificationSummaryAsync(bool projectionChanged)
    {
        await output.WriteLineAsync("Workspace integrity: OK");
        if (projectionChanged)
        {
            await output.WriteLineAsync("Edit applicability: NOT CHECKED (run `docredock diff <file.md>`).");
            await output.WriteLineAsync("Restore readiness: NOT CHECKED.");
        }
        else
        {
            await output.WriteLineAsync("Edit applicability: not applicable (projection unchanged).");
            await output.WriteLineAsync("Restore readiness: F0 eligible.");
        }
    }

    private static async Task<TypedMarkdownDocument> ParseMarkdownAsync(string path, CancellationToken token) => new DocRedockMarkdownParser().Parse(await File.ReadAllTextAsync(path, Encoding.UTF8, token), new MarkdownParseOptions { Strict = true });
    private static string RequireExistingFile(Arguments args) { if (args.Positionals.Count != 1) throw new FileNotFoundException("Exactly one input file path is required."); var path = Path.GetFullPath(args.Positionals[0]); return File.Exists(path) ? path : throw new FileNotFoundException("Input file was not found.", path); }
    private static string RequireExistingPath(Arguments args) { if (args.Positionals.Count != 1) throw new FileNotFoundException("Exactly one input path is required."); var path = Path.GetFullPath(args.Positionals[0]); return File.Exists(path) || Directory.Exists(path) ? path : throw new FileNotFoundException("Input path was not found.", path); }
    private static string SidecarFor(string markdown) => Path.Combine(Path.GetDirectoryName(markdown)!, Path.GetFileNameWithoutExtension(markdown) + ".drmd");
    private static string ResolveWorkspace(string markdownPath, string? reference)
    {
        reference ??= Path.GetFileNameWithoutExtension(markdownPath) + ".drmd"; if (Path.IsPathRooted(reference)) throw new WorkspaceIntegrityException("roundtrip_store must be a relative local path.");
        var root = Path.GetDirectoryName(Path.GetFullPath(markdownPath))!; var path = Path.GetFullPath(Path.Combine(root, reference)); var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new WorkspaceIntegrityException("roundtrip_store escapes the Markdown directory."); return path;
    }
    private static int CountExternalRelationships(ZipArchive archive)
    {
        var count = 0; foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))) { using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true); count += reader.ReadToEnd().Split("TargetMode=\"External\"", StringSplitOptions.None).Length - 1; }
        return count;
    }
    private static string FindRepositoryRoot() { for (var current = new DirectoryInfo(Directory.GetCurrentDirectory()); current is not null; current = current.Parent) if (File.Exists(Path.Combine(current.FullName, "DocRedock.sln"))) return current.FullName; throw new FileNotFoundException("DRMD repository root was not found."); }
    private static IReadOnlyList<string> VerifyLockedPackages(string root, IReadOnlyList<JsonElement> entries)
    {
        var allowed = entries.Select(item => (item.GetProperty("id").GetString()!.ToLowerInvariant(), item.GetProperty("version").GetString()!)).ToHashSet(); var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "packages.lock.json", SearchOption.AllDirectories).Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
        { using var document = JsonDocument.Parse(File.ReadAllText(path)); if (!document.RootElement.TryGetProperty("dependencies", out var frameworks)) continue; foreach (var framework in frameworks.EnumerateObject()) foreach (var package in framework.Value.EnumerateObject()) { if (package.Value.TryGetProperty("type", out var type) && type.GetString() == "Project") continue; var version = package.Value.TryGetProperty("resolved", out var resolved) ? resolved.GetString() : null; if (version is null || !allowed.Contains((package.Name.ToLowerInvariant(), version))) violations.Add($"Unallowlisted package: {package.Name} {version ?? "(unresolved)"}"); } }
        return violations.Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
    }
    private void WriteMarkdownDiagnostics(TypedMarkdownDocument document) { foreach (var item in document.Diagnostics) { var writer = item.Severity == MarkdownDiagnosticSeverity.Error ? error : output; writer.WriteLine($"{item.Severity.ToString().ToUpperInvariant()} {item.Code}: {item.Message}"); } }
    private int Invalid(string message) { error.WriteLine(message); return 2; }
    private int Unsupported(string message) { error.WriteLine(message); return 4; }
    private int LicenseFailure(string message) { error.WriteLine(message); return 9; }
    private void WriteHelp() => output.WriteLine("""
        DocRedock 0.1.0 Public Beta
          docredock export <source> [--output file.md] [--profile roundtrip|readable|audit] [--sidecar dir|zip] [--ocr auto|on|off] [--ocr-lang jpn+eng] [--force] [--quiet]
                      readable: [--show-formulas] [--svg-previews] [--no-diagrams] [--embed-images] [--sheets Sheet1,Sheet2] [--title text]
          docredock restore <file.md> [--output file] [--allow-render-fallback]
          docredock render <file.md> --format docx|pptx|xlsx|pdf [--template file] [--mermaid-cli mmdc] [--output file]
          docredock inspect <source-or-file.md>
          docredock diff <file.md> [--json]
          docredock verify <file.md|file.drmd|file.drmdpkg>
          docredock rebase <file.md> --source <document> [--output rebased.md]
          docredock pack <file.md> [--output file.drmdpkg]
          docredock pack <file.md> --sidecar (--in-place | --output file.drmd)
          docredock unpack <file.drmdpkg> [--output directory]
          docredock unpack <file.drmd> (--in-place | --output directory)
          docredock rules
          docredock migrate <file.md> --to-schema 1.1
        """);

    private sealed class Arguments
    {
        private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal) { "output", "content-policy", "ocr", "ocr-lang", "profile", "sidecar", "format", "template", "mermaid-cli", "source", "to-schema", "sheets", "title" };
        private static readonly HashSet<string> FlagOptions = new(StringComparer.Ordinal) { "strict", "allow-render-fallback", "json", "verify", "force", "quiet", "show-formulas", "svg-previews", "no-diagrams", "embed-images", "sidecar", "in-place" };
        private readonly Dictionary<string, string> options = new(StringComparer.Ordinal); private readonly HashSet<string> flags = new(StringComparer.Ordinal);
        public List<string> Positionals { get; } = []; public string? Option(string name) => options.GetValueOrDefault(name); public bool HasFlag(string name) => flags.Contains(name);
        public static Arguments Parse(string[] values)
        {
            var result = new Arguments(); for (var index = 0; index < values.Length; index++) { var value = values[index]; if (!value.StartsWith("--", StringComparison.Ordinal)) { result.Positionals.Add(value); continue; } var equals = value.IndexOf('='); var key = value[2..(equals < 0 ? value.Length : equals)]; if (key == "sidecar" && (equals >= 0 || index + 1 < values.Length && !values[index + 1].StartsWith("--", StringComparison.Ordinal))) { result.options[key] = equals >= 0 ? value[(equals + 1)..] : values[++index]; continue; } if (FlagOptions.Contains(key)) { if (equals >= 0) throw new IOException($"Flag '--{key}' does not accept a value."); result.flags.Add(key); continue; } if (!ValueOptions.Contains(key)) throw new IOException($"Unknown option '--{key}'."); var optionValue = equals >= 0 ? value[(equals + 1)..] : ++index < values.Length && !values[index].StartsWith("--", StringComparison.Ordinal) ? values[index] : throw new IOException($"Option '--{key}' requires a value."); result.options[key] = optionValue; }
            return result;
        }
    }
}