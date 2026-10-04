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
    private static readonly JsonSerializerOptions AiJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
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
            var documentOptions = options.Document with { MarkdownPath = documentPath, EmbedImages = false };
            var exported = await ExportReadableAsync(documentOptions, cancellationToken).ConfigureAwait(false);
            if (sourceHash != await AiHashAsync(source, cancellationToken).ConfigureAwait(false))
                throw new IOException("Source document changed during conversion; run the export again.");
            var oldAssets = Path.Combine(package, "document.assets");
            if (Directory.Exists(oldAssets)) Directory.Move(oldAssets, Path.Combine(package, "assets"));
            var graph = AiPackageGraph(exported.Graph, documentOptions);
            var markdownOptions = new ReadableMarkdownOptions(documentOptions.ShowFormulas,
                documentOptions.IncludeSvgPreviews, documentOptions.IncludeDiagrams, documentOptions.Sheets,
                documentOptions.Title, documentOptions.ContentPolicy, documentOptions.OcrReview);
            // Re-render from graph references; string replacement could corrupt literal text or code.
            var documentSerializer = new ReadableMarkdownSerializer(markdownOptions);
            var documentMarkdown = documentSerializer.Serialize(graph);
            await File.WriteAllTextAsync(documentPath, documentMarkdown,
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var partGraph = AiMapImageReferences(graph, "../assets/");
            var parts = AiPackageContentBuilder.Build(partGraph, markdownOptions, Path.GetFileName(source),
                options.TargetCharacters, cancellationToken);
            Directory.CreateDirectory(Path.Combine(package, "parts"));
            foreach (var part in parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!System.Text.RegularExpressions.Regex.IsMatch(part.Path, @"\Aparts/[0-9]{4,}\.md\z"))
                    throw new InvalidDataException("Invalid AI package part path.");
                await File.WriteAllTextAsync(Path.Combine(package, part.Path), part.Markdown,
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
            var summary = ExportSummaryBuilder.Build(graph, exported.Diagnostics, documentSerializer.RenderedTables);
            var review = ExportReviewBuilder.Build(graph, exported.Diagnostics);
            await File.WriteAllTextAsync(Path.Combine(package, "review.md"), AiReviewMarkdown(review, summary, parts),
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            // Codes and counts expose conversion limitations without copying diagnostic messages
            // that may contain text or names excluded by the chosen content policy.
            var report = new
            {
                schema_version = "1.0",
                summary,
                review,
                diagnostics = exported.Diagnostics.GroupBy(d => (d.Code, d.Severity))
                    .OrderBy(g => g.Key.Code, StringComparer.Ordinal).ThenBy(g => g.Key.Severity)
                    .Select(g => new { code = g.Key.Code, severity = g.Key.Severity, count = g.Count() }).ToArray()
            };
            await AiWriteJsonAsync(Path.Combine(package, "report.json"), report, cancellationToken).ConfigureAwait(false);
            // Keep the AI-facing manifest small. Full node identity and cell coordinates live
            // in a separately hashed, compact index; repeated sheet/page/heading metadata is shared.
            var locationIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var locations = new List<object>();
            string LocationId(AiPackageSourceLocation sourceLocation)
            {
                var shared = sourceLocation with { NodeId = string.Empty, CellAddress = null };
                var key = JsonSerializer.Serialize(shared, AiJson);
                if (locationIds.TryGetValue(key, out var existing)) return existing;
                var id = "source-" + (locations.Count + 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
                locationIds.Add(key, id);
                locations.Add(new { id, shared.PartitionId, shared.Label, shared.PageNumber,
                    shared.SlideNumber, shared.SheetName, shared.HeadingPath });
                return id;
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
                JsonSerializer.Serialize(sourceIndex, new JsonSerializerOptions(AiJson)
                { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }) + "\n",
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var files = new List<object>();
            foreach (var file in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(new { path = Path.GetRelativePath(package, file).Replace('\\', '/'),
                    bytes = new FileInfo(file).Length, sha256 = await AiHashAsync(file, cancellationToken).ConfigureAwait(false) });
            }
            var manifest = new
            {
                schema_version = "2.0",
                generator_version = typeof(DocumentService).Assembly.GetName().Version?.ToString(3),
                source = new { file_name = Path.GetFileName(source), sha256 = sourceHash, format = graph.Format },
                content_policy = DocumentContentPolicyRules.Name(DocumentContentPolicyRules.Parse(documentOptions.ContentPolicy)),
                visual_inference = documentOptions.InferenceMode switch
                {
                    VisualInferenceMode.NativeOnly => "native-only", VisualInferenceMode.Balanced => "balanced", _ => "safe"
                },
                target_characters = options.TargetCharacters,
                document = "document.md", review = "review.md", report = "report.json",
                source_index = "source-index.json",
                locations,
                parts = parts.Select(p => new { p.Id, p.Path, node_count = p.NodeIds.Count,
                    source_ids = p.Sources.Select(LocationId).Distinct(StringComparer.Ordinal).ToArray(), p.ExceedsTarget }).ToArray(),
                files
            };
            await AiWriteJsonAsync(Path.Combine(package, "manifest.json"), manifest, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (options.Form == AiPackageForm.Directory) Directory.Move(package, target);
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
                        await using var output = entry.Open();
                        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(zipPath, target);
            }
            return new AiPackageExportResult(target, options.Form, graph, exported.Diagnostics, parts, summary, review);
        }
        finally { TryDeleteDirectory(staging); }
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

    private static string AiReviewMarkdown(ExportReview review, ExportSummary summary, IReadOnlyList<AiPackagePart> parts)
    {
        var text = new StringBuilder("# 変換後の確認事項 / Conversion review\n\n");
        text.Append("- 原本照合が必要なページ / Source review pages: ").Append(summary.ReviewPages).Append('\n');
        text.Append("- OCR確認 / OCR review items: ").Append(summary.OcrReviewItems).Append('\n');
        text.Append("- 非表示内容を含む / Hidden content included: ").Append(summary.HiddenContentIncluded ? "yes" : "no").Append("\n\n");
        if (summary.HiddenContentIncluded) text.Append("非表示内容が含まれています。AIへ渡す前に共有範囲を確認してください。 / Review hidden content before sharing.\n\n");
        foreach (var page in review.Pages)
        {
            text.Append("## ").Append(ExportReviewText.Location(page)).Append("\n\n");
            text.Append(ExportReviewText.DescribeJapanese(page)).Append("\n\n");
            foreach (var part in parts.Where(p => p.Sources.Any(s => s.PartitionId == page.PartitionId)))
                text.Append("- [").Append(part.Id).Append("](").Append(part.Path).Append(")\n");
            if (page.ReviewImageReference is { } image)
                text.Append("\n![原本照合用画像](").Append(MarkdownPathEncoder.Encode(image)).Append(")\n");
            text.Append('\n');
        }
        if (review.Ocr.Required) text.Append(ExportReviewText.OcrJapanese(review.Ocr)).Append("\n\n");
        foreach (var part in parts.Where(p => p.ExceedsTarget))
            text.Append("- [").Append(part.Id).Append("](").Append(part.Path).Append("): 表・図等を保つため分割サイズの目安を超えています。\n");
        if (!review.Required && !summary.HiddenContentIncluded) text.Append("照合が必要な箇所は検出されませんでした。変換の完全性を保証するものではありません。\n");
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
