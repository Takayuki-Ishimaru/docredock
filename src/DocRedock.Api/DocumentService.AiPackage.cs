using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using DocRedock.VisualInference;

namespace DocRedock.Api;

public sealed partial class DocumentService
{
    /// <summary>Estimates the same part boundaries without writing an output. OCR is disabled;
    /// its later text may change the sizes. The source and selected options determine the estimate.</summary>
    public async Task<AiPackageSizeSummary> PreviewAiPackageAsync(ReadableDocumentExportOptions options,
        AiPackagePartOptions? partOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = RevisionSharingReview.Policy(options.Revisions, options.ContentPolicy);
        partOptions ??= new();
        if (partOptions.TargetCharacters is < 128 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(partOptions));
        if (!AiTokenEstimate.IsSupportedMultiplier(partOptions.TokenEstimateMultiplier))
            throw new ArgumentOutOfRangeException(nameof(partOptions), "Token estimate multiplier must be between 1 and 100.");
        using var inferenceScope = VisualInferenceContext.Push(options.InferenceMode);
        var previewOptions = options with { EnableOcr = false };
        var (extracted, _, _) = await ExtractReadableGraphAsync(Path.GetFullPath(options.SourcePath), previewOptions, cancellationToken).ConfigureAwait(false);
        var graph = AiPackageGraph(extracted, previewOptions);
        var markdownOptions = new ReadableMarkdownOptions(options.ShowFormulas, options.IncludeSvgPreviews,
            options.IncludeDiagrams, options.Sheets, ReadableTitle(graph, options.SourcePath, options.Title), options.ContentPolicy, options.OcrReview,
            ExpandMergedCells: true, Language: options.Language, FallbackTitle: Path.GetFileNameWithoutExtension(options.SourcePath));
        var markdown = new ReadableMarkdownSerializer(markdownOptions).Serialize(graph);
        var parts = AiPackageContentBuilder.Build(AiMapImageReferences(graph, "../assets/"), markdownOptions,
            Path.GetFileName(options.SourcePath), partOptions, cancellationToken);
        return AiPackageSizeSummary.Build(markdown, parts, partOptions.TargetCharacters) with { Format = graph.Format };
    }
    private static readonly JsonSerializerOptions AiJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        // Sheet names, headings and labels stay readable (and a third of the size for Japanese)
        // instead of \uXXXX escapes; HTML-sensitive and control characters are still escaped.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
    private static readonly JsonSerializerOptions SourceIndexJson = new(AiJson)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Produces a local, one-way AI input package. Existing destinations are never
    /// overwritten; callers may stage replacements using their normal output transaction.</summary>
    public async Task<AiPackageExportResult> ExportAiPackageAsync(AiPackageExportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Document);
        if (options.TargetCharacters < 128 || options.TargetCharacters > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(options), "Target characters must be between 128 and 1000000.");
        if (!Enum.IsDefined(options.Form)) throw new ArgumentOutOfRangeException(nameof(options));
        if (!AiTokenEstimate.IsSupportedMultiplier(options.TokenEstimateMultiplier))
            throw new ArgumentOutOfRangeException(nameof(options), options.TokenEstimateMultiplier,
                "Token estimate multiplier must be between 1 and 100.");
        _ = DocumentContentPolicyRules.Parse(options.Document.ContentPolicy);
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(options.Document.SourcePath);
        var target = Path.GetFullPath(options.OutputPath);
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("AI package output already exists; refusing to overwrite it.");
        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("A package output path is required.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".docredock-ai-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(staging, "package");
        Directory.CreateDirectory(package);
        try
        {
            var sourceHash = await AiHashAsync(source, cancellationToken).ConfigureAwait(false);
            var documentPath = Path.Combine(package, "document.md");
            var documentOptions = options.Document with { MarkdownPath = documentPath, EmbedImages = false, ExpandMergedCells = true };
            var exported = await ExportReadableAsync(documentOptions, cancellationToken).ConfigureAwait(false);
            if (sourceHash != await AiHashAsync(source, cancellationToken).ConfigureAwait(false))
                throw new IOException("Source document changed during conversion; run the export again.");
            var oldAssets = Path.Combine(package, "document.assets");
            if (Directory.Exists(oldAssets)) DocRedock.Core.IO.ExclusiveFileSystem.MoveDirectory(oldAssets, Path.Combine(package, "assets"));
            var graph = AiPackageGraph(exported.Graph, documentOptions);
            var markdownOptions = new ReadableMarkdownOptions(documentOptions.ShowFormulas,
                documentOptions.IncludeSvgPreviews, documentOptions.IncludeDiagrams, documentOptions.Sheets,
                ReadableTitle(graph, source, documentOptions.Title), documentOptions.ContentPolicy, documentOptions.OcrReview,
                ExpandMergedCells: true, Language: documentOptions.Language, FallbackTitle: Path.GetFileNameWithoutExtension(source));
            // Re-render from graph references; string replacement could corrupt literal text or code.
            var documentSerializer = new ReadableMarkdownSerializer(markdownOptions);
            var documentMarkdown = documentSerializer.Serialize(graph);
            await File.WriteAllTextAsync(documentPath, documentMarkdown,
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var partGraph = AiMapImageReferences(graph, "../assets/");
            // Sections the builder could not divide safely are reported; the conversion's own diagnostics stay as they were.
            var splitDiagnostics = new List<DocRedock.Core.Reporting.Diagnostic>();
            var parts = AiPackageContentBuilder.Build(partGraph, markdownOptions, Path.GetFileName(source),
                new AiPackagePartOptions(options.TargetCharacters, options.TableRowBlocks)
                    { TokenEstimateMultiplier = options.TokenEstimateMultiplier }, splitDiagnostics, cancellationToken);
            IReadOnlyList<DocRedock.Core.Reporting.Diagnostic> diagnostics = splitDiagnostics.Count == 0
                ? exported.Diagnostics : [.. exported.Diagnostics, .. splitDiagnostics];
            Directory.CreateDirectory(Path.Combine(package, "parts"));
            foreach (var part in parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!System.Text.RegularExpressions.Regex.IsMatch(part.Path, @"\Aparts/[0-9]{4,}\.md\z"))
                    throw new InvalidDataException("Invalid AI package part path.");
                await File.WriteAllTextAsync(Path.Combine(package, part.Path), part.Markdown,
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
            var summary = ExportSummaryBuilder.BuildReadable(graph, exported.Diagnostics, documentSerializer.Report);
            var review = ExportReviewBuilder.Build(graph, exported.Diagnostics, documentSerializer.Report);
            await File.WriteAllTextAsync(Path.Combine(package, "review.md"), AiReviewMarkdown(review, summary, parts, graph.Format, options.TableRowBlocks, splitDiagnostics),
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            // Codes and counts expose conversion limitations without copying diagnostic messages
            // that may contain text or names excluded by the chosen content policy.
            var report = new
            {
                // 1.1 adds the reading-order/table-structure basis, table boundary review items and
                // review.table_boundaries; 1.2 adds sharing_review and includes it in review.required.
                schema_version = "1.2",
                summary,
                review,
                sharing_review = review.SharingReview,
                diagnostics = diagnostics.GroupBy(d => (d.Code, d.Severity))
                    .OrderBy(g => g.Key.Code, StringComparer.Ordinal).ThenBy(g => g.Key.Severity)
                    .Select(g => new { code = g.Key.Code, severity = g.Key.Severity, count = g.Count() }).ToArray()
            };
            await AiWriteJsonAsync(Path.Combine(package, "report.json"), report, cancellationToken).ConfigureAwait(false);
            // Keep the AI-facing manifest small. Full node identity and cell coordinates live
            // in a separately hashed, compact index; repeated sheet/page/heading metadata is shared.
            var locationIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var locations = new List<object>();
            // A worksheet's parts have a source location for each cell, all but the cell the same: a location is looked up
            // by its shared fields first, and serialized only when they are new.
            var idsByFields = new Dictionary<(string, string, int?, int?, string?, IReadOnlyList<string>?), string>();
            string LocationId(AiPackageSourceLocation sourceLocation)
            {
                var fields = (sourceLocation.PartitionId, sourceLocation.Label, sourceLocation.PageNumber, sourceLocation.SlideNumber,
                    sourceLocation.SheetName, sourceLocation.HeadingPath);
                if (idsByFields.TryGetValue(fields, out var known)) return known;
                var shared = sourceLocation with { NodeId = string.Empty, CellAddress = null };
                var key = JsonSerializer.Serialize(shared, AiJson);
                if (!locationIds.TryGetValue(key, out var id))
                {
                    id = "source-" + (locations.Count + 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
                    locationIds.Add(key, id);
                    locations.Add(new { id, shared.PartitionId, shared.Label, shared.PageNumber,
                        shared.SlideNumber, shared.SheetName, shared.HeadingPath });
                }
                return idsByFields[fields] = id;
            }
            var indexParts = parts.Select(part => new
            {
                part.Id,
                sources = part.Sources.GroupBy(LocationId).Select(group => new
                {
                    source_id = group.Key,
                    node_ids = group.Select(sourceLocation => sourceLocation.NodeId).ToArray(),
                    cell_addresses = group.Any(sourceLocation => sourceLocation.CellAddress is not null)
                        ? group.Select(sourceLocation => sourceLocation.CellAddress).ToArray() : null
                }).ToArray()
            }).ToArray();
            var sourceIndex = new { schema_version = "1.0", parts = indexParts };
            await File.WriteAllTextAsync(Path.Combine(package, "source-index.json"),
                JsonSerializer.Serialize(sourceIndex, SourceIndexJson)
                    + "\n",
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var files = new List<object>();
            foreach (var file in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(new { path = Path.GetRelativePath(package, file).Replace('\\', '/'),
                    bytes = new FileInfo(file).Length, sha256 = await AiHashAsync(file, cancellationToken).ConfigureAwait(false) });
            }
            // 2.1 adds table_row_blocks and, per part, estimated_tokens and table_block (present only on
            // a block of a cut worksheet table); 2.2 adds table_block.context_range, the cells of the
            // title or unit note that later blocks repeat before the header. Every 2.0 field keeps its meaning.
            var manifest = new
            {
                schema_version = "2.2",
                generator_version = typeof(DocumentService).Assembly.GetName().Version?.ToString(3),
                source = new { file_name = Path.GetFileName(source), sha256 = sourceHash, format = graph.Format },
                content_policy = DocumentContentPolicyRules.Name(DocumentContentPolicyRules.Parse(documentOptions.ContentPolicy)),
                visual_inference = documentOptions.InferenceMode switch
                {
                    VisualInferenceMode.NativeOnly => "native-only", VisualInferenceMode.Balanced => "balanced", _ => "safe"
                },
                target_characters = options.TargetCharacters,
                table_row_blocks = options.TableRowBlocks,
                token_estimate_multiplier = options.TokenEstimateMultiplier,
                document = "document.md", review = "review.md", report = "report.json",
                source_index = "source-index.json",
                locations,
                parts = parts.Select(p => ManifestPart(p, p.Sources.Select(LocationId).Distinct(StringComparer.Ordinal).ToArray())).ToArray(),
                files
            };
            await AiWriteJsonAsync(Path.Combine(package, "manifest.json"), manifest, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (options.Form == AiPackageForm.Directory) DocRedock.Core.IO.ExclusiveFileSystem.MoveDirectory(package, target);
            else
            {
                var zipPath = Path.Combine(staging, "package.zip");
                await using (var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                    foreach (var file in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entry = zip.CreateEntry(Path.GetRelativePath(package, file).Replace('\\', '/'), CompressionLevel.Optimal);
                        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        await using var input = File.OpenRead(file);
                        await using var output = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                DocRedock.Core.IO.ExclusiveFileSystem.MoveFile(zipPath, target);
            }
            return new AiPackageExportResult(target, options.Form, graph, diagnostics, parts, summary, review)
                { Size = AiPackageSizeSummary.Build(documentMarkdown, parts, options.TargetCharacters) with { Format = graph.Format } };
        }
        finally { TryDeleteDirectory(staging); }
    }

    private static Dictionary<string, object?> ManifestPart(AiPackagePart part, IReadOnlyList<string> sourceIds)
    {
        var entry = new Dictionary<string, object?>
        {
            ["id"] = part.Id, ["path"] = part.Path, ["node_count"] = part.NodeIds.Count, ["source_ids"] = sourceIds,
            ["exceeds_target"] = part.ExceedsTarget, ["estimated_tokens"] = part.EstimatedTokens,
        };
        if (part.TableBlock is { } block)
            entry["table_block"] = new
            {
                table_id = block.TableId, index = block.Index, count = block.Count, header_range = block.HeaderRange,
                row_range = block.RowRange, previous_part = block.PreviousPart, next_part = block.NextPart,
                context_range = block.ContextRange,
            };
        return entry;
    }

    private static DocumentGraph AiPackageGraph(DocumentGraph graph, ReadableDocumentExportOptions options)
    {
        var policy = DocumentContentPolicyRules.Parse(options.ContentPolicy);
        var scoped = ReadableScopeGraph(graph, options.Sheets);
        scoped = scoped with { Partitions = scoped.Partitions.Select(p => p with
        {
            Nodes = p.Nodes.Where(n => DocumentContentPolicyRules.Includes(n, policy) || AiVisibleSheetOverlay(n)).ToArray()
        }).ToArray() };
        return AiMapImageReferences(scoped, "assets/");
    }

    private static bool AiVisibleSheetOverlay(DocumentNode node) => node.Kind == NodeKind.Shape &&
        node.Extensions?.ContainsKey("sheet_overlay") == true &&
        (!node.Extensions.TryGetValue("sheet_state", out var state) || state.ValueKind == JsonValueKind.Null ||
         state.ValueKind == JsonValueKind.String && state.GetString() == "visible");

    private static DocumentGraph AiMapImageReferences(DocumentGraph graph, string prefix) => graph with
    {
        Partitions = graph.Partitions.Select(p => p with { Nodes = p.Nodes.Select(n =>
            n.Kind == NodeKind.Image && n.Content is ReferenceNodeContent image &&
            (image.Reference.StartsWith("document.assets/", StringComparison.Ordinal) || image.Reference.StartsWith("assets/", StringComparison.Ordinal))
                ? n with { Content = image with { Reference = prefix + image.Reference[(image.Reference.IndexOf('/') + 1)..] } }
                : n).ToArray() }).ToArray()
    };

    private static string AiReviewMarkdown(ExportReview review, ExportSummary summary, IReadOnlyList<AiPackagePart> parts,
        DocumentFormatKind format, bool tableRowBlocks, IReadOnlyList<DocRedock.Core.Reporting.Diagnostic> splitDiagnostics)
    {
        // Sheet names and other source labels are literal text, never Markdown syntax.
        static string Literal(string value) => AiPackageContentBuilder.EscapeLiteral(value);
        var text = new StringBuilder("# 変換後の確認事項 / Conversion review\n\n");
        text.Append("- 原本照合が必要なページ / Source review pages: ").Append(summary.ReviewPages).Append('\n');
        text.Append("- OCR確認 / OCR review items: ").Append(summary.OcrReviewItems).Append('\n');
        text.Append("- 表の区切りの確認 / Table boundaries to compare: ").Append(summary.TableBoundaryReviewItems).Append('\n');
        text.Append("- 非表示内容を含む / Hidden content included: ").Append(summary.HiddenContentIncluded ? "yes" : "no").Append('\n');
        if (review.WorkbookCalculationPending)
            text.Append("- Excelで再計算して保存してから書き出し直してください。保存済みの数式結果が古い可能性があります。 / Recalculate and save in Excel, then export again. Saved formula results may be stale.\n");
        if (review.SharingReview is { } sharing)
        {
            text.Append("- 変更履歴 / Revision policy: ").Append(sharing.RevisionsPolicy).Append('\n');
            text.Append("- 履歴を含む / Revisions included: ").Append(sharing.RevisionsIncluded ? "yes" : "no").Append('\n');
            text.Append("- 削除内容を含む可能性 / Deleted content may be included: ").Append(sharing.DeletedTextIncluded ? "yes" : "no").Append('\n');
            if (sharing.Required) text.Append("変更履歴を含みます。削除内容・著者・日時の共有可否を確認してからAIへ渡してください。 / Review revision history before sharing.\n");
        }
        // What was evaluated: "nothing detected" never means reading order and tables were verified.
        text.Append("- 読み順 / Reading order: ").Append(ExportSummaryText.ReadingOrderJapanese(summary.ReadingOrder))
            .Append(" / ").Append(ExportSummaryText.ReadingOrderEnglish(summary.ReadingOrder)).Append('\n');
        text.Append("- 表構造 / Table structure: ").Append(ExportSummaryText.TableStructureJapanese(summary.TableStructure, summary.TableBoundaryReviewItems))
            .Append(" / ").Append(ExportSummaryText.TableStructureEnglish(summary.TableStructure, summary.TableBoundaryReviewItems)).Append("\n\n");
        if (summary.HiddenContentIncluded) text.Append("非表示内容が含まれています。AIへ渡す前に共有範囲を確認してください。 / Review hidden content before sharing.\n\n");
        foreach (var page in review.Pages)
        {
            text.Append("## ").Append(Literal(ExportReviewText.Location(page))).Append("\n\n");
            text.Append(Literal(ExportReviewText.DescribeJapanese(page))).Append("\n\n");
            foreach (var part in parts.Where(p => p.Sources.Any(s => s.PartitionId == page.PartitionId)))
                text.Append("- [").Append(part.Id).Append("](").Append(part.Path).Append(")\n");
            if (page.ReviewImageReference is { } image)
                text.Append("\n![原本照合用画像](").Append(MarkdownPathEncoder.Encode(image)).Append(")\n");
            text.Append('\n');
        }
        foreach (var boundary in review.TableBoundaries ?? [])
        {
            text.Append("## ").Append(Literal($"シート「{boundary.SheetName}」 {boundary.GapRange}")).Append("\n\n");
            text.Append(Literal(ExportReviewText.TableBoundaryJapanese(boundary))).Append("\n\n");
            foreach (var part in parts.Where(p => p.Sources.Any(s => s.PartitionId == boundary.PartitionId)))
                text.Append("- [").Append(part.Id).Append("](").Append(part.Path).Append(")\n");
            text.Append('\n');
        }
        if (review.Ocr.Required) text.Append(ExportReviewText.OcrJapanese(review.Ocr)).Append("\n\n");
        if (review.UnwrittenImages.Count > 0)
            text.Append("抽出した画像のうち").Append(review.UnwrittenImages.Count)
                .Append("件が本文から参照されていません。原本の図と照合してください。 / ")
                .Append(review.UnwrittenImages.Count).Append(" extracted image(s) are not referenced by the Markdown; compare with the source.\n\n");
        // Why a part is larger than the target depends on what the format splits at; a worksheet table can be cut between
        // rows on request, which a person would otherwise not find out from here.
        var oversize = format switch
        {
            DocumentFormatKind.Xlsx when !tableRowBlocks =>
                "表を行の途中で分けないため分割サイズの目安を超えています。--table-row-blocks（GUIでは「大きなExcelの表を行ブロックに分割」）で表を行単位に分割できます。 / A worksheet table is kept whole; --table-row-blocks splits it between rows.",
            DocumentFormatKind.Xlsx => "1行と見出し・注記だけで分割サイズの目安を超えています。 / One row with its header already exceeds the target.",
            DocumentFormatKind.Pdf => "1ページを1単位として分割するため、分割サイズの目安を超えています。 / Pages are not split.",
            DocumentFormatKind.Pptx => "1スライドを1単位として分割するため、分割サイズの目安を超えています。 / Slides are not split.",
            _ => "見出しの間の表・図・一続きのリストを分けないため、分割サイズの目安を超えています。 / A table, figure or list is kept whole.",
        };
        foreach (var part in parts.Where(p => p.ExceedsTarget))
            text.Append("- [").Append(part.Id).Append("](").Append(part.Path).Append("): ").Append(oversize).Append('\n');
        // The reason is structural wording only (no document text), so it is safe under every content policy.
        foreach (var unsplit in splitDiagnostics)
            text.Append("- 見出しごとに分割できませんでした / Not divided by section: ")
                .Append(Literal(unsplit.Data?.GetValueOrDefault("reason") ?? unsplit.Message)).Append(" (")
                .Append(Literal(unsplit.Data?.GetValueOrDefault("part") ?? string.Empty)).Append(")\n");
        if (!review.Required && !summary.HiddenContentIncluded)
            text.Append("照合が必要な箇所は検出されませんでした。読み順・表構造は上記のとおりで、変換の完全性を保証するものではありません。\n");
        return text.ToString();
    }

    private static async Task<string> AiHashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private static Task AiWriteJsonAsync<T>(string path, T value, CancellationToken token) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, AiJson) + "\n", new UTF8Encoding(false), token);
}
