using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using DocRedock.Ocr.Tesseract;
using DocRedock.Render;
using DocRedock.RoundTrip;
using DocRedock.VisualInference;

namespace DocRedock.Cli;

public enum ExitCode
{
    Success = 0, SuccessWithWarnings = 1, InvalidInput = 2, WorkspaceInvalid = 3,
    Unsupported = 4, SecurityPolicyViolation = 5, RestoreConflict = 6,
    ValidationFailed = 7, OcrPartialFailure = 8, LicenseValidationFailed = 9, InternalError = 10,
    // Only with export --fail-on-review: the output was written, but something needs comparison with the source.
    ReviewRequired = 11,
}

public sealed class CliApplication(TextWriter output, TextWriter error, DocumentService? documentService = null)
{
    private static readonly JsonSerializerOptions PackageJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) };
    private static readonly JsonSerializerOptions ReportJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) };

    private DocumentService Service { get; } = documentService ?? new DocumentService(OcrEngineFactory.CreateDefault(),
        PdfRasterizerFactory.Discover(Environment.GetEnvironmentVariable("DOCREDOCK_PDF_RASTERIZER"),
            string.Equals(Environment.GetEnvironmentVariable("DOCREDOCK_DISABLE_PDF_RASTERIZER"), "1", StringComparison.Ordinal)));
    private static string Version => typeof(CliApplication).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CliApplication).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { WriteHelp(); return 0; }
        // Command help is intentionally dispatched before experimental gates and
        // input validation so every command documents itself in every environment.
        if (args.Length > 1 && args.Skip(1).Any(argument => argument is "--help" or "-h")) { WriteCommandHelp(args[0]); return 0; }
        if (args[0] == "--version") { await output.WriteLineAsync($"DocRedock {Version}"); return 0; }
        if (!ExperimentalFeatures.IsEnabled && IsExperimentalCommand(args[0]))
            return ExperimentalDisabled(args[0]);
        try
        {
            var parsed = Arguments.Parse(args[1..]);
            return args[0].ToLowerInvariant() switch
            {
                "export" => await ExportAsync(parsed, cancellationToken),
                "package" => await PackageAsync(parsed, cancellationToken),
                "restore" => await RestoreAsync(parsed, cancellationToken),
                "render" => await RenderAsync(parsed, cancellationToken),
                "inspect" => await InspectAsync(parsed, cancellationToken),
                "diff" => await DiffAsync(parsed, cancellationToken),
                "verify" => await VerifyAsync(parsed, cancellationToken),
                "preflight" => await PreflightAsync(parsed, cancellationToken),
                "rebase" => await RebaseAsync(parsed, cancellationToken),
                "pack" => await PackAsync(parsed, cancellationToken),
                "unpack" => await UnpackAsync(parsed, cancellationToken),
                "rules" => await RulesAsync(parsed, cancellationToken),
                "licenses" => await LicensesAsync(parsed, cancellationToken),
                "migrate" => await MigrateAsync(parsed, cancellationToken),
                "doctor" => await DoctorAsync(parsed),
                _ => Invalid($"Unknown command '{args[0]}'."),
            };
        }
        catch (OutOfMemoryException) { await error.WriteLineAsync("Not enough memory to convert this document. Try --sheets to select worksheets or --table-row-blocks with an AI package."); return (int)ExitCode.InternalError; }
        catch (OperationCanceledException) { await error.WriteLineAsync("Operation cancelled."); return (int)ExitCode.InvalidInput; }
        catch (AiPackageVerificationException ex) { await error.WriteLineAsync(ex.Message); return (int)ExitCode.ValidationFailed; }
        catch (WorkspaceIntegrityException ex) { await error.WriteLineAsync($"Workspace verification failed: {ex.Message}"); return (int)ExitCode.WorkspaceInvalid; }
        catch (UnauthorizedAccessException ex) { await error.WriteLineAsync(ex.Message); return (int)ExitCode.SecurityPolicyViolation; }
        catch (NotSupportedException ex) { await error.WriteLineAsync(ex.Message); return (int)ExitCode.Unsupported; }
        catch (InvalidDataException ex) { await error.WriteLineAsync(ex.Message); return (int)ExitCode.ValidationFailed; }
        catch (InvalidOperationException ex) { await error.WriteLineAsync(ex.Message); return (int)ExitCode.RestoreConflict; }
        catch (FileNotFoundException ex) { await error.WriteLineAsync(ex.Message); return (int)ExitCode.InvalidInput; }
        catch (IOException ex) { await error.WriteLineAsync(ex.Message); return (int)ExitCode.InvalidInput; }
        catch (SheetSelectionException ex) { await error.WriteLineAsync(ex.Message); return (int)ExitCode.InvalidInput; }
        catch (Exception ex)
        {
            // Keep the type for diagnostics, but include the message so a CLI
            // user can act on malformed input without reproducing under a debugger.
            await error.WriteLineAsync($"Internal error: {ex.GetType().Name}: {ex.Message}");
            return (int)ExitCode.InternalError;
        }
    }

    private async Task<int> PackageAsync(Arguments args, CancellationToken token)
    {
        if (args.Positionals.Count is < 2 or > 3 || args.Positionals[0] is not ("search" or "read" or "review"))
            return Invalid("Use package search <directory|zip> <text>, package read <directory|zip> --part <id>, or package review <directory|zip> [--json].");
        var action = args.Positionals[0];
        if (action == "review" && (args.Positionals.Count != 2 || args.Option("part") is not null || args.Option("query") is not null || args.HasFlag("with-sources")))
            return Invalid("package review takes only a package path and review options.");
        var selection = args.Positionals.Count == 3 ? args.Positionals[2] : args.Option(action == "read" ? "part" : "query");
        if (action != "review" && string.IsNullOrWhiteSpace(selection)) return Invalid(action == "read" ? "--part is required." : "Search text is required.");
        if (args.HasFlag("with-sources") && !args.HasFlag("json")) return Invalid("--with-sources requires --json; plain output contains only Markdown.");
        if (args.HasFlag("allow-review") && args.HasFlag("fail-on-review")) return Invalid("--allow-review and --fail-on-review conflict.");
        AiPackageManifest manifest;
        AiPackageReview review;
        string? reviewMarkdown = null;
        AiPackageVerifiedPart? selectedPart = null;
        AiPackageVerifiedPackage? package = null;
        if (action == "read")
        {
            try { selectedPart = await AiPackageManifestReader.ReadVerifiedPartAsync(args.Positionals[1], selection!, token); }
            catch (KeyNotFoundException) { return Invalid($"Unknown part '{selection}'."); }
            manifest = selectedPart.Manifest; review = selectedPart.Review;
        }
        else if (action == "review")
        {
            var verified = await AiPackageManifestReader.ReadVerifiedReviewAsync(args.Positionals[1], token);
            manifest = verified.Manifest; review = verified.Review; reviewMarkdown = verified.ReviewMarkdown;
        }
        else
        {
            package = await AiPackageManifestReader.ReadVerifiedPackageAsync(args.Positionals[1], token);
            manifest = package.Manifest; review = package.Review;
        }
        var requireHiddenReview = args.HasFlag("require-hidden-review");
        var reasons = review.StopReasons(requireHiddenReview, manifest.ContentPolicy);
        if (action == "review")
        {
            if (args.HasFlag("json")) await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                verified = true, review_required = reasons.Count > 0,
                conversion_review = new { required = review.ConversionReviewRequired },
                revision_sharing = new { required = review.RevisionSharingRequired, details = review.SharingReview },
                hidden_content = new { included = review.HiddenContentIncluded, require_review = requireHiddenReview,
                    required = review.HiddenReviewRequired(requireHiddenReview, manifest.ContentPolicy) },
                stop_reasons = reasons,
                // Reports do not identify every revision/hidden/OCR item by part; list the full
                // package conservatively, without suggesting unaffected parts are safe to share.
                affected_parts = reasons.Count > 0 ? manifest.Parts.Select(part => part.Id).ToArray() : [],
                affected_parts_scope = reasons.Count > 0 ? "package" : "none",
                review_markdown = reviewMarkdown,
            }, PackageJson));
            else await output.WriteAsync(reviewMarkdown);
            return args.HasFlag("fail-on-review") && reasons.Count > 0 ? (int)ExitCode.ReviewRequired : 0;
        }
        if (reasons.Count > 0 && !args.HasFlag("allow-review"))
        {
            await error.WriteLineAsync("Review required (" + string.Join(", ", reasons) + "): use package review <directory|zip> [--json] and confirm conversion and sharing permissions before reading (use --allow-review after review).");
            return (int)ExitCode.ReviewRequired;
        }
        var jsonOptions = PackageJson;
        if (action == "search")
        {
            var matches = AiPackageSearch.Find(package!, selection!, token);
            if (args.HasFlag("json")) await output.WriteLineAsync(JsonSerializer.Serialize(new { review_required = reasons.Count > 0, matches }, jsonOptions));
            else foreach (var match in matches)
            {
                await output.WriteLineAsync(PackageSearchDisplay(match.PartId) + " — " + string.Join("; ", match.Locations.Select(location => PackageSearchDisplay(location.Label))));
                foreach (var line in match.Lines) await output.WriteLineAsync($"  {line.Number}: {PackageSearchDisplay(line.Text)}");
            }
            if (matches.Count == 0) await error.WriteLineAsync("No matching part.");
            return 0;
        }
        var part = selectedPart!;
        if (args.HasFlag("json")) await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            part_id = part.Part.Id, markdown = part.Markdown, review_required = reasons.Count > 0,
            estimated_tokens = part.Part.EstimatedTokens, token_estimate_multiplier = manifest.TokenEstimateMultiplier,
            sources = args.HasFlag("with-sources") ? part.Sources : null,
            locations = args.HasFlag("with-sources") ? part.Part.Locations : null,
        }, jsonOptions));
        else await output.WriteAsync(part.Markdown);
        return 0;
    }

    // Search results are terminal annotations; keep controls visible without executing them.
    private static string PackageSearchDisplay(string text)
    {
        var shown = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                shown.Append(character).Append(text[++index]);
            else if (character is <= '\u001f' or (>= '\u007f' and <= '\u009f') or '\u061c' or
                     '\u200e' or '\u200f' or '\u2028' or '\u2029' or (>= '\u202a' and <= '\u202e') or
                     (>= '\u2066' and <= '\u2069') || char.IsSurrogate(character))
                shown.Append(character < 0x100 ? "\\x" : "\\u")
                    .Append(((int)character).ToString(character < 0x100 ? "x2" : "x4", System.Globalization.CultureInfo.InvariantCulture));
            else shown.Append(character);
        }
        return shown.ToString();
    }

    private async Task<int> DoctorAsync(Arguments args)
    {
        if (args.Positionals.Count != 0) return Invalid("doctor does not accept an input file.");
        var disabled = string.Equals(Environment.GetEnvironmentVariable("DOCREDOCK_DISABLE_PDF_RASTERIZER"), "1", StringComparison.Ordinal);
        var rasterizer = PdfRasterizerFactory.Describe(Environment.GetEnvironmentVariable("DOCREDOCK_PDF_RASTERIZER"), disabled);
        var capabilities = await new CapabilityReporter().ReportAsync(rasterizer);
        var strict = args.HasFlag("strict");
        // One exit-code decision drives both --json and the text summary below, so changing the
        // display format never changes the exit code (see CapabilityExitPolicy).
        var verdict = CapabilityExitPolicy.Evaluate(capabilities, strict);
        var summary = new CapabilitySummary(verdict.RequiredReady, verdict.OptionalGaps, verdict.StrictFailures);
        var report = new CapabilityReport("1", Version, capabilities, strict, verdict.ExitCode, summary);
        if (args.HasFlag("json"))
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(report, ReportJson));
            return verdict.ExitCode;
        }
        await output.WriteLineAsync($"DocRedock {Version}"); await output.WriteLineAsync("Readable export");
        foreach (var item in capabilities.Where(item => item.Tier == "required"))
            await output.WriteLineAsync($"  {item.Id,-16} {item.Status} (required)");
        await output.WriteLineAsync("OCR");
        foreach (var item in capabilities.Where(item => item.Id.StartsWith("ocr-", StringComparison.Ordinal)))
            await output.WriteLineAsync($"  {item.Id[4..],-16} {item.Status} (optional){(item.Provider is null ? string.Empty : $" ({item.Provider})")}{(item.SatisfiedBy is null ? string.Empty : $" [provided by {item.SatisfiedBy}]")}");
        await output.WriteLineAsync("PDF rasterizer"); await output.WriteLineAsync($"  {rasterizer.Provider ?? "local"}    {rasterizer.Status} (optional){(rasterizer.Path is null ? string.Empty : $" ({rasterizer.Path})")}");
        if (rasterizer.Action is not null) await output.WriteLineAsync($"  Action: {rasterizer.Action}");
        var mermaid = capabilities.Single(item => item.Id == "mermaid-render");
        await output.WriteLineAsync("Mermaid render"); await output.WriteLineAsync($"  {mermaid.Provider ?? "mmdc",-16} {mermaid.Status} (optional)");
        if (mermaid.Action is not null) await output.WriteLineAsync($"  Action: {mermaid.Action}");
        await output.WriteLineAsync(string.Empty);
        await output.WriteLineAsync($"Required capabilities: {(verdict.RequiredReady ? "ready" : "NOT ready")}");
        await output.WriteLineAsync(verdict.OptionalGaps.Count == 0
            ? "Optional gaps: none"
            : $"Optional gaps: {string.Join(", ", verdict.OptionalGaps.Select(id => DescribeGap(id, capabilities)))}");
        await output.WriteLineAsync($"Exit code: {verdict.ExitCode}" + (strict ? string.Empty : " (use --strict to fail on optional gaps)"));
        return verdict.ExitCode;
    }

    private static string DescribeGap(string id, IReadOnlyList<CapabilityStatus> capabilities)
    {
        var item = capabilities.Single(candidate => candidate.Id == id);
        return item.SatisfiedBy is null ? id : $"{id} (provided by {item.SatisfiedBy})";
    }

    private void WriteCommandHelp(string command)
    {
        var synopsis = command.ToLowerInvariant() switch
        {
            "export" => "export <source...|directory> [--output|-o file.md|-] [--output-dir directory] [--content-policy visible|complete|sanitized] [--sheets names] [--force] [--quiet|--verbose|--json] [--allow-pdf] [--expand-merged-cells] [--lang ja|en] [--show-formulas] [--svg-previews] [--no-diagrams] [--embed-images] [--title text] [--sidecar dir|zip] [--profile readable|roundtrip|audit] [--ai-package dir|zip] [--chunk-chars 12000] [--table-row-blocks] [--token-estimate-multiplier 1] [--fail-on-review] [--revisions final|markup|reject] [--allow-revision-sharing] [--ocr auto|on|off] [--ocr-lang jpn+eng] [--ocr-review low-confidence|all|summary] [--pdf-fallback-images auto|off] [--visual-inference native-only|safe|balanced]",
            "restore" => "restore <file.md> [--output file] [--force] [--replace-original (requires --force; retains backup)] [--allow-render-fallback]",
            "preflight" => "preflight <file.md> [--json] [--allow-render-fallback] (checks edits, integrity, and trial restore without modifying inputs)",
            "render" => "render <file.md> --format docx|pptx|xlsx|pdf|html [--mermaid-cli mmdc] [--output file]",
            "doctor" => "doctor [--json] [--strict]",
            "package" => "package review <directory|zip> [--json --fail-on-review --require-hidden-review] | package search <directory|zip> <text> [--json] | package read <directory|zip> --part part-0001 [--with-sources --json] [--allow-review --require-hidden-review] (always verifies; review blocks body by default)",
            "licenses" => "licenses [--json] [--verify] (packages and bundled source data; verification requires the source repository)",
            "inspect" => "inspect <source-or-file.md>",
            "diff" => "diff <file.md> [--json]",
            "verify" => "verify <file.md|file.drmd|file.drmdpkg>",
            "rebase" => "rebase <file.md> --source <document> [--output rebased.md]",
            "pack" => "pack <file.md> [--output file.drmdpkg]",
            "unpack" => "unpack <file.drmdpkg|file.drmd> [--output directory]",
            "rules" => "rules",
            "migrate" => "migrate <file.md> --to-schema 1.1",
            _ => $"{command} (see docredock --help)",
        };
        output.WriteLine($"DocRedock {synopsis}{Environment.NewLine}Help is available without enabling experimental features or supplying input.");
    }

    private async Task<int> ExportAsync(Arguments args, CancellationToken token)
    {
        if (args.HasFlag("strict") || args.Option("strict") is not null)
            return Invalid("--strict was removed because strict Markdown validation is always enabled.");
        if (args.Positionals.Count == 0) return Invalid("At least one input file or directory is required.");
        if (args.Option("output") == "-") return await ExportStdoutAsync(args, token);
        if (args.Positionals.Count > 1 || Directory.Exists(args.Positionals[0]) || args.Option("output-dir") is not null) return await ExportBatchAsync(args, token);
        var source = RequireExistingFile(args);
        if (new[] { ".doc", ".xls", ".ppt" }.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase)) return Unsupported("Legacy Office files are not supported. Open the file in Microsoft Office or LibreOffice and save it as DOCX, XLSX, or PPTX first.");
        var profile = args.Option("profile") ?? "readable";
        if (profile is not ("roundtrip" or "readable" or "audit")) return Unsupported("Built-in export supports roundtrip, readable, and audit profiles.");
        if (!ExperimentalFeatures.IsEnabled && (profile != "readable" || (await DocumentService.DetectFormatAsync(source, token) == DocumentFormatKind.Pdf && !args.HasFlag("allow-pdf"))))
            return ExperimentalDisabled(profile == "readable" ? "PDF export" : profile + " export");
        var aiPackage = args.Option("ai-package");
        if (aiPackage is not null && (profile != "readable" || aiPackage is not ("dir" or "zip")))
            return Invalid("--ai-package requires dir or zip and the readable profile.");
        if (aiPackage is null && args.Option("chunk-chars") is not null)
            return Invalid("--chunk-chars requires --ai-package dir or zip.");
        if (aiPackage is null && args.HasFlag("table-row-blocks"))
            return Invalid("--table-row-blocks requires --ai-package dir or zip.");
        var tokenEstimateMultiplier = 1d;
        if (args.Option("token-estimate-multiplier") is { } margin)
        {
            if (aiPackage is null) return Invalid("--token-estimate-multiplier requires --ai-package dir or zip.");
            if (!double.TryParse(margin, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out tokenEstimateMultiplier) ||
                !AiTokenEstimate.IsSupportedMultiplier(tokenEstimateMultiplier))
                return Invalid("--token-estimate-multiplier must be a number between 1 and 100.");
        }
        var targetCharacters = 12_000;
        if (args.Option("chunk-chars") is { } size && (!int.TryParse(size, out targetCharacters) || targetCharacters is < 128 or > 1_000_000))
            return Invalid("--chunk-chars must be between 128 and 1000000.");
        if (aiPackage is not null && args.HasFlag("embed-images"))
            return Invalid("AI packages store images in assets/; omit --embed-images.");
        var defaultOutput = aiPackage is null ? Path.ChangeExtension(source, ".md")
            : Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + ".ai-package" + (aiPackage == "zip" ? ".zip" : ""));
        var markdown = Path.GetFullPath(args.Option("output") ?? defaultOutput);
        var ocrMode = (args.Option("ocr") ?? "auto").ToLowerInvariant();
        if (ocrMode is not ("auto" or "on" or "off")) return Invalid("--ocr must be auto, on, or off.");
        var visualInference = (args.Option("visual-inference") ?? "safe").ToLowerInvariant();
        if (visualInference is not ("native-only" or "safe" or "balanced"))
            return Invalid("--visual-inference must be native-only, safe, or balanced.");
        var contentPolicy = (args.Option("content-policy") ?? "visible").ToLowerInvariant();
        if (contentPolicy is not ("visible" or "complete" or "sanitized"))
            return Invalid("--content-policy must be visible, complete, or sanitized.");
        var revisions = args.Option("revisions")?.ToLowerInvariant();
        if (revisions is not (null or "final" or "markup" or "reject")) return Invalid("--revisions must be final, markup, or reject.");
        if (profile != "readable" && (revisions is not null || args.HasFlag("allow-revision-sharing")))
            return Invalid("Revision policies apply to readable exports; roundtrip/audit retain the original source.");
        var language = args.Option("lang");
        if (language is not (null or "ja" or "en")) return Invalid("--lang must be ja or en.");
        var languages = (args.Option("ocr-lang") ?? "jpn+eng").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (languages.Length == 0) return Invalid("--ocr-lang must contain at least one language identifier.");
        var pdfFallbackImages = args.Option("pdf-fallback-images") ?? "auto";
        if (pdfFallbackImages is not ("auto" or "off")) return Invalid("--pdf-fallback-images must be auto or off.");
        var ocrReview = args.Option("ocr-review") ?? "low-confidence";
        if (ocrReview is not ("low-confidence" or "all" or "summary"))
            return Invalid("--ocr-review must be low-confidence, all, or summary.");
        if (profile != "readable" && args.Option("ocr-review") is not null)
            return Invalid("--ocr-review applies to the readable profile; audit and roundtrip retain all OCR records.");
        var force = args.HasFlag("force");
        var quiet = args.HasFlag("quiet");
        if (args.HasFlag("sidecar")) return Invalid("export --sidecar requires dir or zip.");
        var sidecarForm = args.Option("sidecar") ?? "dir";
        if (sidecarForm is not ("dir" or "zip")) return Invalid("--sidecar must be dir or zip.");
        if (profile == "readable")
        {
            var inferenceMode = ParseInferenceMode(visualInference);
            var embedImages = args.HasFlag("embed-images");
            var sheets = args.Option("sheets")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // An explicit --sheets that resolves to nothing ("", ",", " , ") is a user
            // error, not "all sheets": that meaning is reserved for omitting --sheets
            // entirely (sheets stays null above).
            if (sheets is { Length: 0 }) return Invalid("--sheets requires at least one sheet name.");
            ReadableDocumentExportOptions ReadableOptions(string path) => new(
                source, path, ocrMode != "off", languages, contentPolicy,
                ShowFormulas: args.HasFlag("show-formulas"),
                IncludeSvgPreviews: args.HasFlag("svg-previews"),
                IncludeDiagrams: !args.HasFlag("no-diagrams"),
                Sheets: sheets,
                Title: args.Option("title"),
                EmbedImages: embedImages,
                InferenceMode: inferenceMode, IncludePdfFallbackImages: pdfFallbackImages != "off",
                OcrReview: ocrReview switch { "all" => DocRedock.Markdown.OcrReviewMode.All,
                    "summary" => DocRedock.Markdown.OcrReviewMode.Summary, _ => DocRedock.Markdown.OcrReviewMode.LowConfidence },
                Revisions: revisions, AllowRevisionSharing: args.HasFlag("allow-revision-sharing"),
                ExpandMergedCells: args.HasFlag("expand-merged-cells"), OcrAuto: ocrMode == "auto", Language: language);
            if (aiPackage is not null)
            {
                // Replacing a package directory must not remove an input anywhere below it.
                var protectedPaths = AiPackageProtectedInputs(source);
                using var packageTransaction = new StagedOutputTransaction([markdown], force, protectedInputs: protectedPaths);
                var packageResult = await Service.ExportAiPackageAsync(new AiPackageExportOptions(
                    ReadableOptions(markdown), packageTransaction.PathFor(markdown),
                    aiPackage == "zip" ? AiPackageForm.Zip : AiPackageForm.Directory, targetCharacters,
                    TableRowBlocks: args.HasFlag("table-row-blocks"))
                    { TokenEstimateMultiplier = tokenEstimateMultiplier }, token);
                token.ThrowIfCancellationRequested();
                packageTransaction.Commit();
                await WriteExportResultAsync(args, markdown, packageResult.Review, packageResult.Diagnostics, packageResult.Summary.ToString(), $"AI package: {markdown} ({aiPackage}; {packageResult.Parts.Count} part(s))\nReview: review.md; report.json; source locations: manifest.json");
                return await ReviewExitCodeAsync(args, packageResult.Review,
                    packageResult.Parts.Count == 0 || packageResult.Diagnostics.Any(d => d.Severity != DocRedock.Core.Reporting.DiagnosticSeverity.Information) ? 1 : 0);
            }
            var readableAssets = Path.Combine(Path.GetDirectoryName(markdown)!, Path.GetFileNameWithoutExtension(markdown) + ".assets");
            using var stagedOutputs = embedImages
                ? new StagedOutputTransaction([markdown], force, protectedInputs: [source])
                : new StagedOutputTransaction([markdown], force, [readableAssets], protectedInputs: [source]);
            var stagedMarkdown = stagedOutputs.PathFor(markdown);
            var readable = await Service.ExportReadableAsync(ReadableOptions(stagedMarkdown), token);
            token.ThrowIfCancellationRequested();
            stagedOutputs.Commit();
            var visualSummary = ExportSummaryBuilder.BuildReadable(readable.Graph, readable.Diagnostics, readable.Projection);
            var readableReview = ExportReviewBuilder.Build(readable.Graph, readable.Diagnostics, readable.Projection);
            await WriteExportResultAsync(args, markdown, readableReview, readable.Diagnostics, VisualInferenceSummary(visualSummary) + Environment.NewLine + visualSummary, $"Format:   {readable.Graph.Format.ToString().ToLowerInvariant()}\nMode:     Readable Markdown (one-way; no sidecar)\nVisual inference: {visualInference} (ambiguous relations remain unresolved)");
            if (args.HasFlag("verbose") && !args.HasFlag("json") && !args.HasFlag("quiet"))
                foreach (var edge in VisualGraphs(readable.Graph).SelectMany(graph => graph.Edges ?? []).Where(edge => edge is not null)) await output.WriteLineAsync(VisualEvidence(edge));
            if (!readable.Graph.Nodes.Any())
            {
                if (!args.HasFlag("quiet") && !args.HasFlag("json")) await output.WriteLineAsync("WARNING EmptyProjection: no extractable content was found.");
                return await ReviewExitCodeAsync(args, readableReview, 1);
            }
            return await ReviewExitCodeAsync(args, readableReview,
                readable.Diagnostics.Any(item => item.Severity != DocRedock.Core.Reporting.DiagnosticSeverity.Information) ? 1 : 0);
        }

        var sidecarPath = SidecarFor(markdown);
        using var stagedRoundTrip = new StagedOutputTransaction([markdown, sidecarPath], force, protectedInputs: [source]);
        var stagedRoundTripMarkdown = stagedRoundTrip.PathFor(markdown);
        var stagedSidecar = stagedRoundTrip.PathFor(sidecarPath);
        var result = await Service.ExportAsync(new DocumentExportOptions(source, stagedSidecar, stagedRoundTripMarkdown,
            ocrMode != "off", languages, contentPolicy, Profile: profile,
            InferenceMode: ParseInferenceMode(visualInference), IncludePdfFallbackImages: pdfFallbackImages != "off", OcrAuto: ocrMode == "auto", Language: args.Option("lang")), token);
        if (sidecarForm == "zip")
            await SidecarContainer.PackInPlaceAsync(result.Workspace.RootPath, stagedRoundTripMarkdown, token);
        token.ThrowIfCancellationRequested();
        stagedRoundTrip.Commit();
        var review = ExportReviewBuilder.Build(result.Graph, result.Diagnostics);
        await WriteExportResultAsync(args, markdown, review, result.Diagnostics, ExportSummaryBuilder.Build(result.Graph, result.Diagnostics).ToString(), $"Sidecar:  {sidecarPath} ({(sidecarForm == "zip" ? "zip" : "directory")})\nFormat:   {result.Graph.Format.ToString().ToLowerInvariant()}\nFidelity: F0 baseline");
        if (!result.Graph.Nodes.Any()) { if (!args.HasFlag("quiet") && !args.HasFlag("json")) await output.WriteLineAsync("WARNING EmptyProjection: no extractable content was found."); return await ReviewExitCodeAsync(args, review, 1); }
        return await ReviewExitCodeAsync(args, review,
            result.Diagnostics.Any(item => item.Severity != DocRedock.Core.Reporting.DiagnosticSeverity.Information) ? 1 : 0);
    }

    private async Task WriteExportResultAsync(Arguments args, string path, ExportReview review, IReadOnlyList<DocRedock.Core.Reporting.Diagnostic> diagnostics, string details, string kind)
    {
        var warnings = diagnostics.Count(d => d.Severity != DocRedock.Core.Reporting.DiagnosticSeverity.Information);
        if (args.HasFlag("json")) { await output.WriteLineAsync(JsonSerializer.Serialize(new { output = path, kind, review_required = review.Required, warning_count = warnings, diagnostics, review }, PackageJson)); return; }
        if (args.HasFlag("quiet")) return;
        await output.WriteLineAsync($"Exported: {path}");
        await output.WriteLineAsync($"Review: {(review.Required ? "required" : "none")}; warnings: {warnings}");
        if (diagnostics.Any(d => d.Code == "OcrUnavailableAuto")) await output.WriteLineAsync("Image text was not read (OCR unavailable).");
        if (!args.HasFlag("verbose")) { await WriteDiagnosticsAsync(diagnostics, true, false); return; }
        await output.WriteLineAsync(kind); await output.WriteLineAsync(details);
        await WriteReviewAsync(review, path, null); await WriteDiagnosticsAsync(diagnostics, false, true);
    }

    private async Task<int> ExportStdoutAsync(Arguments args, CancellationToken token)
    {
        if (args.Positionals.Count != 1 || Directory.Exists(args.Positionals[0]) || args.Option("output-dir") is not null || args.Option("ai-package") is not null || (args.Option("profile") ?? "readable") != "readable" || args.HasFlag("json")) return Invalid("--output - requires one file, readable Markdown, and no --json or --ai-package.");
        var root = Path.Combine(Path.GetTempPath(), "docredock-stdout-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "output.md"); var child = args.Copy(); child.SetOption("output", path); child.SetFlag("embed-images");
            var summary = new StringWriter(); var code = await new CliApplication(summary, error, Service).RunAsync(child.ToCommandLine(), token);
            if (File.Exists(path)) await output.WriteAsync(await File.ReadAllTextAsync(path, token));
            if (!args.HasFlag("quiet")) await error.WriteAsync(summary.ToString().Replace(path, "standard output", StringComparison.Ordinal)); return code;
        }
        finally { Directory.Delete(root, true); }
    }

    private async Task<int> ExportBatchAsync(Arguments args, CancellationToken token)
    {
        if (args.Option("output") is not null) return Invalid("--output is for one input; use --output-dir for multiple inputs.");
        var inputs = new List<string>();
        foreach (var input in args.Positionals)
        {
            if (Directory.Exists(input)) inputs.AddRange(Directory.EnumerateFiles(input).Where(p => new[] { ".docx", ".xlsx", ".pptx", ".pdf", ".doc", ".xls", ".ppt" }.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.Ordinal));
            else if (File.Exists(input)) inputs.Add(input); else return Invalid($"Input file or directory not found: {input}");
        }
        if (inputs.Count == 0) return Invalid("No supported input files found.");
        var outputDir = args.Option("output-dir");
        var targets = inputs.Select(p => Path.GetFullPath(Path.Combine(outputDir ?? Path.GetDirectoryName(Path.GetFullPath(p))!, Path.GetFileNameWithoutExtension(p) + (args.Option("ai-package") is { } form ? ".ai-package" + (form == "zip" ? ".zip" : "") : ".md")))).ToArray();
        if (targets.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != targets.Length) return Invalid("Multiple inputs would produce the same output path.");
        // An assets or sidecar directory for one export must not remove another input.
        var allOutputs = new List<string>(targets);
        if (args.Option("ai-package") is null)
            foreach (var target in targets)
                if ((args.Option("profile") ?? "readable") != "readable") allOutputs.Add(SidecarFor(target));
                else if (!args.HasFlag("embed-images")) allOutputs.Add(Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + ".assets"));
        OutputCollisionGuard.EnsureNoCollision(allOutputs, inputs);
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var canonicalOutputs = allOutputs.Select(OutputCollisionGuard.CanonicalPath).ToArray();
        var outputSet = canonicalOutputs.ToHashSet(comparer);
        if (outputSet.Count != allOutputs.Count) return Invalid("Batch outputs overlap.");
        foreach (var path in canonicalOutputs)
            for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
                if (outputSet.Contains(parent)) return Invalid("Batch outputs overlap.");
        var results = new List<object>(); var exit = 0;
        for (var i = 0; i < inputs.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var child = args.Copy(); child.Positionals.Clear(); child.Positionals.Add(inputs[i]); child.RemoveOption("output-dir"); child.SetOption("output", targets[i]);
            var resultOutput = new StringWriter(); var resultError = new StringWriter(); var code = await new CliApplication(resultOutput, resultError, Service).RunAsync(child.ToCommandLine(), token); exit = code is > 1 and not 11 ? (exit is > 1 and not 11 ? exit : code) : exit == 0 ? code : exit;
            if (args.HasFlag("json")) { JsonElement? summary = null; if (!string.IsNullOrWhiteSpace(resultOutput.ToString())) { using var doc = JsonDocument.Parse(resultOutput.ToString()); summary = doc.RootElement.Clone(); } results.Add(new { input = inputs[i], exit_code = code, summary, error = resultError.ToString().Trim() }); }
            else { await output.WriteAsync(resultOutput.ToString()); await error.WriteAsync(resultError.ToString()); }
        }
        if (args.HasFlag("json")) await output.WriteLineAsync(JsonSerializer.Serialize(new { results }, PackageJson)); return exit;
    }

    // Review items never change the exit code by default. With --fail-on-review an export whose
    // review is required (source-review pages, OCR items, worksheet table boundaries) returns
    // ReviewRequired instead of 0 or 1, so an automated pipeline can stop before handing the output
    // to a model. The output is written either way.
    private async Task<int> ReviewExitCodeAsync(Arguments args, ExportReview review, int exitCode)
    {
        if (!args.HasFlag("fail-on-review") || !review.Required) return exitCode;
        if (!args.HasFlag("quiet") && !args.HasFlag("json")) await output.WriteLineAsync($"Review required: compare the review items with the source before using the output (exit code {(int)ExitCode.ReviewRequired}, --fail-on-review).");
        return (int)ExitCode.ReviewRequired;
    }

    private static VisualInferenceMode ParseInferenceMode(string value) => value switch
    {
        "native-only" => VisualInferenceMode.NativeOnly,
        "balanced" => VisualInferenceMode.Balanced,
        _ => VisualInferenceMode.Safe,
    };

    // One line per page a person should compare with the source, naming what to look at and where
    // the review image is. OCR review is reported separately and never changes the exit code.
    private async Task WriteReviewAsync(ExportReview review, string markdownPath, string? sidecarPath)
    {
        foreach (var page in review.Pages)
        {
            var image = ReviewImageLocation(page, markdownPath, sidecarPath);
            await output.WriteLineAsync($"Review {ExportReviewText.DescribeEnglish(page)}{(image is null ? string.Empty : "; " + image)}");
        }
        foreach (var boundary in review.TableBoundaries ?? [])
            await output.WriteLineAsync($"Review {ExportReviewText.TableBoundaryEnglish(boundary)}");
        if (review.Ocr.Images > 0)
            await output.WriteLineAsync($"OCR summary: images={review.Ocr.Images}; regions={review.Ocr.Regions}; " +
                $"review_items={review.Ocr.ReviewItems}; review_required={(review.Ocr.Required ? "true" : "false")}");
    }

    private static string? ReviewImageLocation(ReviewPage page, string markdownPath, string? sidecarPath)
    {
        if (page.ReviewImageReference is not { } reference)
            return page.Format != DocumentFormatKind.Pdf ? null
                : page.ReviewImageUnavailable ? "review image unavailable" : "review image not attached";
        if (reference.StartsWith("data:", StringComparison.Ordinal)) return "review image embedded in the Markdown";
        return ExportReviewImages.Resolve(reference, markdownPath, sidecarPath) is { } path
            ? "review image: " + path
            : "review image stored in the sidecar";
    }

    private async Task WriteDiagnosticsAsync(
        IReadOnlyList<DocRedock.Core.Reporting.Diagnostic> diagnostics,
        bool quiet,
        bool verbose)
    {
        var visible = diagnostics
            .Where(item => !quiet || item.Severity != DocRedock.Core.Reporting.DiagnosticSeverity.Information)
            .ToArray();
        foreach (var summary in AdapterWarningDiagnostics.SummarizeForDisplay(visible))
        {
            var count = summary.Count > 1 ? $" ({summary.Count} diagnostic entries; use --verbose for details)" : string.Empty;
            await output.WriteLineAsync($"{summary.Severity.ToString().ToUpperInvariant()} {summary.Code}: {summary.Message}{count}");
            if (!verbose) continue;
            foreach (var detail in visible.Where(item => StringComparer.Ordinal.Equals(item.Code, summary.Code)))
            {
                var anchor = string.Join(", ", new[] { detail.PartUri, detail.NodeId }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                await output.WriteLineAsync($"  - {detail.Message}{(anchor.Length > 0 ? $" [{anchor}]" : string.Empty)}");
            }
        }
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
        if (args.HasFlag("replace-original") && !args.HasFlag("force"))
            return Invalid("--replace-original requires --force; the existing document will be backed up.");
        var historicalOriginal = IsHistoricalOriginal(destination, workspace.Manifest.Source, markdown);
        if (historicalOriginal && !args.HasFlag("replace-original"))
            return Invalid("OriginalDocumentProtected: destination matches the historical source document. Use a new output path, or --force --replace-original to retain a backup and explicitly replace it.");
        using var stagedOutput = new StagedOutputTransaction([destination], args.HasFlag("force"), protectedInputs: [markdown, workspacePath, lease.RootPath, workspace.OriginalSourcePath]);
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
        if (historicalOriginal && File.Exists(destination))
        {
            var backup = destination + ".docredock-original-" + Guid.NewGuid().ToString("N") + ".bak";
            File.Copy(destination, backup, overwrite: false);
            await output.WriteLineAsync($"Original backup: {backup}");
        }
        stagedOutput.Commit();
        await output.WriteLineAsync($"Restored: {destination}"); await output.WriteLineAsync($"Fidelity: {result.Fidelity}");
        return result.Diagnostics.Any(item => item.Severity == DocRedock.Core.Reporting.DiagnosticSeverity.Warning) ? 1 : 0;
    }

    private static bool IsHistoricalOriginal(string destination, SourceInfo source, string markdown)
    {
        var original = source.OriginalPath ?? Path.Combine(Path.GetDirectoryName(markdown)!, Path.GetFileName(source.FileName));
        try { OutputCollisionGuard.EnsureNoCollision([destination], [original]); }
        catch (OutputCollidesWithInputException) { return true; }
        if (!File.Exists(destination)) return false;
        using var stream = File.OpenRead(destination);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        return StringComparer.OrdinalIgnoreCase.Equals(hash, source.Sha256);
    }

    private async Task<int> PreflightAsync(Arguments args, CancellationToken token)
    {
        var markdown = RequireExistingFile(args);
        var parsed = await ParseMarkdownAsync(markdown, token);
        if (!parsed.IsComplete) { WriteMarkdownDiagnostics(parsed); return (int)ExitCode.WorkspaceInvalid; }
        var result = await Service.PreflightAsync(ResolveWorkspace(markdown, parsed.RoundTripStore), markdown,
            args.HasFlag("allow-render-fallback"), token);
        if (args.HasFlag("json"))
            await output.WriteLineAsync(JsonSerializer.Serialize(result, ReportJson));
        else
        {
            await output.WriteLineAsync($"Edits: {(result.ProjectionChanged ? "detected (not corruption)" : "none")}");
            await output.WriteLineAsync($"Workspace integrity: {(result.WorkspaceValid ? "valid" : "INVALID")}");
            await output.WriteLineAsync($"Trial restore: {(result.CanRestore ? "passed" : "FAILED")} ({result.Fidelity})");
            foreach (var diagnostic in result.Diagnostics)
                await output.WriteLineAsync($"{diagnostic.Severity.ToString().ToUpperInvariant()} {diagnostic.Code}: {diagnostic.Message}");
        }
        return !result.WorkspaceValid ? (int)ExitCode.WorkspaceInvalid : !result.CanRestore ? (int)ExitCode.RestoreConflict
            : result.Diagnostics.Any(d => d.Severity == DocRedock.Core.Reporting.DiagnosticSeverity.Warning) ? 1 : 0;
    }

    private async Task<int> RenderAsync(Arguments args, CancellationToken token)
    {
        var input = RequireExistingFile(args);
        var value = args.Option("format") ?? throw new IOException("render requires --format docx|pptx|xlsx|pdf|html.");
        if (!Enum.TryParse<RenderFormat>(value, true, out var format)) return Unsupported($"Unsupported render format '{value}'.");
        var destination = Path.GetFullPath(args.Option("output") ?? Path.ChangeExtension(input, "." + value.ToLowerInvariant()));
        int? fontFaceIndex = null;
        var fontFaceValue = args.Option("font-face-index");
        if (fontFaceValue is not null)
        {
            if (!int.TryParse(fontFaceValue, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedFace) || parsedFace < 0)
                throw new IOException("--font-face-index must be a non-negative integer.");
            fontFaceIndex = parsedFace;
        }

        using var stagedOutput = new StagedOutputTransaction([destination], args.HasFlag("force"), protectedInputs: [input]);
        var result = await Service.RenderAsync(new DocumentRenderOptions(await File.ReadAllTextAsync(input, token), stagedOutput.PathFor(destination), format,
            new RenderOptions(
                TemplatePath: args.Option("template"),
                FontPath: args.Option("font-path"),
                FontFaceIndex: fontFaceIndex,
                MermaidExecutablePath: args.Option("mermaid-cli") ?? "mmdc",
                SourceDirectory: Path.GetDirectoryName(input),
                RelativeLinkOutputPath: destination,
                Verbose: args.HasFlag("verbose"))), token);
        stagedOutput.Commit();
        await output.WriteLineAsync($"Rendered: {destination}");
        await output.WriteLineAsync($"Fidelity: {result.FidelityLevel} (new document, not restore)");
        if (!args.HasFlag("quiet"))
            foreach (var information in result.Information) await output.WriteLineAsync(information);
        foreach (var warning in result.Warnings) await output.WriteLineAsync(warning);
        return result.Warnings.Count == 0 ? 0 : 1;
    }

    private async Task<int> InspectAsync(Arguments args, CancellationToken token)
    {
        if (args.HasFlag("sheets") || args.Option("sheets") is not null)
            return Invalid("inspect does not support --sheets; use export --sheets to select worksheets.");
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
            var graphPath = Path.Combine(workspace.RootPath, "graph", "index.json");
            if (File.Exists(graphPath))
            {
                try
                {
                    var visualGraph = DeterministicJson.Deserialize<DocumentGraph>(await File.ReadAllTextAsync(graphPath, token));
                    if (visualGraph is not null) await output.WriteLineAsync(VisualInferenceSummary(ExportSummaryBuilder.Build(visualGraph, [])));
                }
                catch (JsonException) { /* integrity verification above remains the source of truth */ }
            }
            return report.IsValid ? 0 : 3;
        }
        var format = await DocumentService.DetectFormatAsync(path, token);
        await output.WriteLineAsync($"Format: {format.ToString().ToLowerInvariant()}"); await output.WriteLineAsync("Network access: denied");
        if (format == DocumentFormatKind.Unknown) return 4;
        if (format is DocumentFormatKind.Docx or DocumentFormatKind.Xlsx or DocumentFormatKind.Pptx)
        {
            using var archive = await ZipFile.OpenReadAsync(path, token).ConfigureAwait(false);
            await output.WriteLineAsync($"Macro enabled: {archive.Entries.Any(entry => entry.FullName.EndsWith("/vbaProject.bin", StringComparison.OrdinalIgnoreCase)).ToString().ToLowerInvariant()}");
            await output.WriteLineAsync($"External relationships: {CountExternalRelationships(archive)}");
            await output.WriteLineAsync($"Embedded media: {archive.Entries.Count(entry => entry.FullName.Contains("/media/", StringComparison.OrdinalIgnoreCase))}");
        }
        await output.WriteLineAsync(format == DocumentFormatKind.Pdf ? "Restore: F0; edited content requires explicit F3 render fallback" : "Restore: F0/F1 after export");
        await WriteSourceVisualInferenceSummaryAsync(path, token);
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
        OutputCollisionGuard.EnsureNoCollision([outputMarkdown, SidecarFor(outputMarkdown)], [markdown, source, currentPath]);
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
                var packDestination = Path.GetFullPath(args.Option("output")!);
                OutputCollisionGuard.EnsureNoCollision([packDestination], [markdown, sidecar]);
                var packed = await SidecarContainer.PackToAsync(sidecar, markdown, packDestination, token);
                await output.WriteLineAsync($"Packed sidecar: {packed} (zip)");
            }
            return 0;
        }
        var markdownDocument = await ParseMarkdownAsync(markdown, token);
        if (!markdownDocument.IsComplete) { WriteMarkdownDiagnostics(markdownDocument); return 3; }
        var workspacePath = ResolveWorkspace(markdown, markdownDocument.RoundTripStore);
        var package = Path.GetFullPath(args.Option("output") ?? Path.ChangeExtension(markdown, ".drmdpkg"));
        using var stagedOutput = new StagedOutputTransaction([package], args.HasFlag("force"), protectedInputs: [markdown, workspacePath]);
        var result = await RoundTripPackage.PackAsync(markdown, workspacePath, stagedOutput.PathFor(package), token);
        stagedOutput.Commit();
        await output.WriteLineAsync($"Packed: {package} ({result.EntryCount} entries)"); return 0;
    }

    private async Task<int> UnpackAsync(Arguments args, CancellationToken token)
    {
        var package = RequireExistingFile(args);
        if (SidecarContainer.IsBundle(package))
        {
            var destination = Path.GetFullPath(args.Option("output") ?? Path.Combine(Path.GetDirectoryName(package)!, Path.GetFileNameWithoutExtension(package)));
            using var stagedOutput = new StagedOutputTransaction([destination], args.HasFlag("force"), protectedInputs: [package]);
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
            var unpackDestination = Path.GetFullPath(args.Option("output")!);
            OutputCollisionGuard.EnsureNoCollision([unpackDestination], [package, markdown]);
            var unpacked = await SidecarContainer.UnpackToAsync(package, markdown, unpackDestination, token);
            await output.WriteLineAsync($"Unpacked sidecar: {unpacked} (directory)");
        }
        return 0;
    }

    private async Task<int> LicensesAsync(Arguments args, CancellationToken token)
    {
        if (args.Positionals.Count != 0) return Invalid("licenses does not accept an input file.");
        string? root = null;
        try { root = FindRepositoryRoot(); } catch (FileNotFoundException) { }
        var path = root is null ? null : Path.Combine(root, "licenses", "allowlist.json");
        await using var stream = path is not null && File.Exists(path) ? File.OpenRead(path) :
            typeof(CliApplication).Assembly.GetManifestResourceStream("DocRedock.LicenseAllowlist.json");
        if (stream is null) return LicenseFailure("License allowlist was not found.");
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var packages = document.RootElement.GetProperty("packages").EnumerateArray().ToArray();
        if (args.HasFlag("json")) await output.WriteLineAsync(document.RootElement.GetRawText());
        else
        {
            await output.WriteLineAsync($"Allowlisted dependencies: {packages.Length}");
            foreach (var item in packages.OrderBy(item => item.GetProperty("id").GetString(), StringComparer.OrdinalIgnoreCase))
                await output.WriteLineAsync($"{item.GetProperty("id").GetString()} {item.GetProperty("version").GetString()} — {item.GetProperty("license").GetString()}");
            if (document.RootElement.TryGetProperty("sourceData", out var data))
            {
                await output.WriteLineAsync($"Bundled source data: {data.GetArrayLength()}");
                foreach (var item in data.EnumerateArray())
                    await output.WriteLineAsync($"{item.GetProperty("id").GetString()} {item.GetProperty("version").GetString()} — {item.GetProperty("license").GetString()} ({item.GetProperty("source").GetString()})");
            }
        }
        if (args.HasFlag("verify"))
        {
            if (root is null) return LicenseFailure("License verification requires the source repository and package lock files.");
            var violations = VerifyLockedPackages(root, packages); foreach (var item in violations) await error.WriteLineAsync(item);
            if (violations.Length > 0) return 9;
            if (!args.HasFlag("json")) await output.WriteLineAsync("License verification: passed");
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
            await output.WriteLineAsync("Restore readiness: NOT CHECKED. Run `docredock preflight <file.md>` for an integrated trial restore.");
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
    private static string[] VerifyLockedPackages(string root, IReadOnlyList<JsonElement> entries)
    {
        var allowed = entries.Select(item => (item.GetProperty("id").GetString()!.ToLowerInvariant(), item.GetProperty("version").GetString()!)).ToHashSet(); var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "packages.lock.json", SearchOption.AllDirectories).Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
        { using var document = JsonDocument.Parse(File.ReadAllText(path)); if (!document.RootElement.TryGetProperty("dependencies", out var frameworks)) continue; foreach (var framework in frameworks.EnumerateObject()) foreach (var package in framework.Value.EnumerateObject()) { if (package.Value.TryGetProperty("type", out var type) && type.GetString() == "Project") continue; var version = package.Value.TryGetProperty("resolved", out var resolved) ? resolved.GetString() : null; if (version is null || !allowed.Contains((package.Name.ToLowerInvariant(), version))) violations.Add($"Unallowlisted package: {package.Name} {version ?? "(unresolved)"}"); } }
        return violations.Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
    }
    private void WriteMarkdownDiagnostics(TypedMarkdownDocument document) { foreach (var item in document.Diagnostics) { var writer = item.Severity == MarkdownDiagnosticSeverity.Error ? error : output; writer.WriteLine($"{item.Severity.ToString().ToUpperInvariant()} {item.Code}: {item.Message}"); } }
    private int Invalid(string message) { error.WriteLine(message); return 2; }
    private int Unsupported(string message) { error.WriteLine(message); return 4; }
    private static string[] AiPackageProtectedInputs(string source)
    {
        var protectedPaths = new HashSet<string>(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(source);
        while (pending.TryDequeue(out var path))
        {
            path = Path.GetFullPath(path);
            if (!protectedPaths.Add(path)) continue;
            // Drive/share roots are already protected above. Windows cannot query
            // a drive root with ResolveLinkTarget, and a root has no parent link
            // to follow; doing so would reject every otherwise valid AI export.
            if (Path.GetDirectoryName(path) is not { } parent) continue;
            pending.Enqueue(parent);
            // A source reached through a directory/file link also needs protection for
            // the target's ancestors, which need not be ancestors of the lexical path.
            FileSystemInfo item = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path);
            if (item.ResolveLinkTarget(returnFinalTarget: true) is { } target) pending.Enqueue(target.FullName);
        }
        return protectedPaths.ToArray();
    }

    private int ExperimentalDisabled(string feature)
    {
        error.WriteLine($"{feature} is experimental and disabled. Set {ExperimentalFeatures.EnvironmentVariable}=1 to enable it explicitly.");
        return (int)ExitCode.Unsupported;
    }
    private static bool IsExperimentalCommand(string command) => command.ToLowerInvariant() is
        "restore" or "preflight" or "render" or "diff" or "rebase" or "pack" or "unpack" or "migrate";
    private int LicenseFailure(string message) { error.WriteLine(message); return 9; }
    private void WriteHelp() => output.WriteLine($"""
        DocRedock {Version} Public Beta
        Supported commands:
          docredock --version
          docredock export <source...|directory> [--output|-o file.md|-] [--output-dir directory] [--json] [--allow-pdf] [--profile readable|roundtrip|audit (default: readable)] [--sidecar dir|zip] [--content-policy visible|complete|sanitized] [--ocr auto|on|off] [--ocr-lang jpn+eng] [--ocr-review low-confidence|all|summary] [--pdf-fallback-images auto|off] [--visual-inference native-only|safe|balanced (default: safe)] [--fail-on-review] [--verbose] [--force] [--quiet]
                      readable: [--ai-package dir|zip] [--chunk-chars 12000] [--table-row-blocks] [--token-estimate-multiplier 1] [--revisions final|markup|reject] [--allow-revision-sharing] [--show-formulas] [--svg-previews] [--no-diagrams] [--embed-images] [--sheets Sheet1,Sheet2] [--title text]
          docredock package review <directory|zip> [--json] [--fail-on-review] [--require-hidden-review]
          docredock package search <directory|zip> <text> [--json] [--allow-review] [--require-hidden-review]
          docredock package read <directory|zip> --part part-0001 [--json] [--with-sources] [--allow-review] [--require-hidden-review]
          docredock inspect <source-or-file.md>
          docredock verify <file.md|file.drmd|file.drmdpkg>
          docredock rules
          docredock licenses [--json] [--verify]
          docredock doctor [--json] [--strict]
        Experimental commands (require DOCREDOCK_ENABLE_EXPERIMENTAL=1):
          docredock restore <file.md> [--output file] [--force] [--replace-original] [--allow-render-fallback]
          docredock render <file.md> --format docx|pptx|xlsx|pdf|html [--template file] [--font-path file.ttf|file.ttc] [--font-face-index n] [--mermaid-cli mmdc] [--output file] [--verbose] [--quiet]
          docredock diff <file.md> [--json]
          docredock preflight <file.md> [--json] [--allow-render-fallback]
          docredock rebase <file.md> --source <document> [--output rebased.md]
          docredock pack <file.md> [--output file.drmdpkg]
          docredock pack <file.md> --sidecar (--in-place | --output file.drmd)
          docredock unpack <file.drmdpkg> [--output directory]
          docredock unpack <file.drmd> (--in-place | --output directory)
          docredock migrate <file.md> --to-schema 1.1

        Experimental commands require DOCREDOCK_ENABLE_EXPERIMENTAL=1. PDF export can also be enabled with --allow-pdf.
        """);

    private static IEnumerable<VisualGraph> VisualGraphs(DocumentGraph graph) => graph.Nodes
        .Where(node => node.Extensions?.TryGetValue("visual_graph", out _) == true)
        .Select(node => node.Extensions!["visual_graph"].Deserialize<VisualGraph>())
        .OfType<VisualGraph>();

    // Renders from the one finalized ExportSummary so this line's diagrams=/fallback= counters can
    // never disagree with "Diagrams reconstructed:"/"Fallback pages:" printed alongside it (F-05).
    // diagrams= is the count of RECONSTRUCTED diagrams (a resolved edge between known nodes);
    // vector_pages= is how many pages/partitions carried a visual graph at all, resolved or not.
    private static string VisualInferenceSummary(ExportSummary summary) =>
        $"Visual summary: diagrams={summary.DiagramsReconstructed}; vector_pages={summary.VectorPages}; " +
        $"native={summary.NativeEdges}; high={summary.HighConfidenceEdges}; medium={summary.MediumConfidenceEdges}; " +
        $"unresolved={summary.UnresolvedRelations}; fallback={summary.FallbackPaths}; rejected={summary.Rejected}; " +
        $"review_pages={summary.ReviewPages}; review_image_pages={summary.ReviewImagePages}; unresolved_elements={summary.UnresolvedElements}";

    private static string VisualEvidence(VisualEdge edge)
    {
        var evidence = edge.Evidence;
        return $"Visual edge {edge.Id}: method={evidence?.Method ?? "none"}; confidence={evidence?.ConfidenceBand ?? edge.Confidence?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}; boundary={evidence?.BoundaryDistanceNormalized?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"}; ray_first_hit={evidence?.RayFirstHit?.ToString().ToLowerInvariant() ?? "n/a"}; angle={evidence?.AngularDeviationDegrees?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"}; margin={evidence?.CandidateMargin?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"}; intermediate={evidence?.IntermediateNodeCount.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0"}";
    }

    private async Task WriteSourceVisualInferenceSummaryAsync(string sourcePath, CancellationToken token)
    {
        var root = Path.Combine(Path.GetTempPath(), "docredock-inspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = await Service.ExportReadableAsync(new ReadableDocumentExportOptions(
                sourcePath, Path.Combine(root, "projection.md"), EnableOcr: false, ContentPolicy: "visible"), token);
            await output.WriteLineAsync(VisualInferenceSummary(ExportSummaryBuilder.Build(result.Graph, result.Diagnostics)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await output.WriteLineAsync("Visual summary: unavailable");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Arguments
    {
        private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal) { "lang", "output-dir", "ai-package", "chunk-chars", "token-estimate-multiplier", "revisions", "part", "query", "output", "ocr-review", "pdf-fallback-images", "content-policy", "ocr", "ocr-lang", "visual-inference", "profile", "sidecar", "format", "template", "font-path", "font-face-index", "mermaid-cli", "source", "to-schema", "sheets", "title" };
        private static readonly HashSet<string> FlagOptions = new(StringComparer.Ordinal) { "expand-merged-cells", "allow-pdf", "strict", "replace-original", "allow-render-fallback", "json", "verify", "force", "quiet", "verbose", "show-formulas", "svg-previews", "no-diagrams", "embed-images", "sidecar", "in-place", "table-row-blocks", "fail-on-review", "allow-revision-sharing", "allow-review", "with-sources", "require-hidden-review" };
        private readonly Dictionary<string, string> options = new(StringComparer.Ordinal); private readonly HashSet<string> flags = new(StringComparer.Ordinal);
        public List<string> Positionals { get; } = []; public string? Option(string name) => options.GetValueOrDefault(name); public bool HasFlag(string name) => flags.Contains(name);
        public Arguments Copy() { var copy = new Arguments(); copy.Positionals.AddRange(Positionals); foreach (var item in options) copy.options[item.Key] = item.Value; foreach (var flag in flags) copy.flags.Add(flag); return copy; }
        public void SetOption(string name, string value) => options[name] = value;
        public void RemoveOption(string name) => options.Remove(name);
        public void SetFlag(string name) => flags.Add(name);
        public string[] ToCommandLine() => new[] { "export" }.Concat(Positionals).Concat(options.SelectMany(o => new[] { "--" + o.Key, o.Value })).Concat(flags.Select(f => "--" + f)).ToArray();
        public static Arguments Parse(string[] values)
        {
            var result = new Arguments(); for (var index = 0; index < values.Length; index++) { var value = values[index]; if (value == "-o") value = "--output"; else if (value.StartsWith("-", StringComparison.Ordinal) && value != "-" && !value.StartsWith("--", StringComparison.Ordinal)) throw new IOException($"Unknown option '{value}'."); if (!value.StartsWith("--", StringComparison.Ordinal)) { result.Positionals.Add(value); continue; } var equals = value.IndexOf('='); var key = value[2..(equals < 0 ? value.Length : equals)]; if (key == "sidecar" && (equals >= 0 || index + 1 < values.Length && !values[index + 1].StartsWith("--", StringComparison.Ordinal))) { result.options[key] = equals >= 0 ? value[(equals + 1)..] : values[++index]; continue; } if (FlagOptions.Contains(key)) { if (equals >= 0) throw new IOException($"Flag '--{key}' does not accept a value."); result.flags.Add(key); continue; } if (!ValueOptions.Contains(key)) throw new IOException($"Unknown option '--{key}'."); var optionValue = equals >= 0 ? value[(equals + 1)..] : ++index < values.Length && !values[index].StartsWith("--", StringComparison.Ordinal) ? values[index] : throw new IOException($"Option '--{key}' requires a value."); result.options[key] = optionValue; }
            return result;
        }
    }
}
