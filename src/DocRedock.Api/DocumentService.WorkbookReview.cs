using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Markdown;
using DocRedock.Render;
using DocRedock.RoundTrip;

namespace DocRedock.Api;

/// <summary>Human-readable workbook review directory: one Markdown file per selected worksheet,
/// a combined change history, deduplicated image assets, a native HTML layout companion, and
/// machine-readable manifests. The directory is staged and published with one exclusive move.</summary>
public sealed partial class DocumentService
{
    private static readonly string[] WorkbookReviewChangeHistoryNames =
        ["変更履歴", "改訂履歴", "更新履歴", "changelog", "revision history"];

    public async Task<WorkbookReviewExportResult> ExportWorkbookReviewAsync(
        WorkbookReviewExportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var document = options.Document;
        ArgumentNullException.ThrowIfNull(document);
        if (document.OutputFormat != ReadableOutputFormat.Markdown)
            throw new ArgumentException("Workbook review export requires Markdown output format.", nameof(options));
        if (document.EmbedImages)
            throw new ArgumentException("Workbook review export uses external image assets; EmbedImages must be false.", nameof(options));
        var source = Path.GetFullPath(document.SourcePath);
        if (!File.Exists(source)) throw DocumentSourceException.ForUnreadable(source);
        var target = Path.GetFullPath(options.OutputDirectory);
        if (Directory.Exists(target) || File.Exists(target))
            throw new IOException("Output already exists; refusing to overwrite it.");
        var format = await DetectFormatAsync(source, cancellationToken).ConfigureAwait(false);
        if (format != DocumentFormatKind.Xlsx)
            throw new ArgumentException("Workbook review export supports Xlsx sources only.", nameof(options));
        var sourceHash = await AiHashAsync(source, cancellationToken).ConfigureAwait(false);

        // Extract exactly once; every output below is a projection of this one graph.
        var (graph, extractedDiagnostics, assets) = await ExtractReadableGraphAsync(source, document, cancellationToken).ConfigureAwait(false);
        var diagnostics = extractedDiagnostics.ToList();
        var policy = DocumentContentPolicyRules.Parse(document.ContentPolicy);
        WorkbookPresentationLayout? sourceLayout = null;
        if (graph.FormatExtensions?.TryGetValue(WorkbookPresentationLayoutJson.ExtensionKey, out var rawLayout) == true)
            WorkbookPresentationLayoutJson.TryRead(rawLayout, out sourceLayout);
        var sourceScenes = graph.FormatExtensions?.TryGetValue(WorkbookDrawingSceneJson.ExtensionKey, out var rawScenes) == true
            ? WorkbookDrawingSceneJson.Read(rawScenes) : new Dictionary<string, WorkbookDrawingScene>();
        var selected = graph.Partitions
            .Where(partition => document.Sheets is not { Count: > 0 } || IncludedSheet(partition.Id, document.Sheets))
            .Where(partition => policy == DocumentContentPolicy.Complete || sourceLayout?.Sheets.GetValueOrDefault(partition.Id)?.State is not ("hidden" or "veryHidden"))
            .Where(partition => partition.Nodes.Count == 0 || partition.Nodes.Any(node => DocumentContentPolicyRules.Includes(node, policy)))
            .OrderBy(partition => partition.Order)
            .ThenBy(partition => partition.Id, StringComparer.Ordinal)
            .ToArray();
        if (document.Sheets is { Count: > 0 })
        {
            var unknown = document.Sheets
                .Where(requested => !graph.Partitions.Any(partition => IncludedSheet(partition.Id, [requested])))
                .ToArray();
            if (unknown.Length > 0)
                throw new SheetSelectionException(document.Sheets, unknown,
                    [.. graph.Partitions.Select(partition => WorkbookReviewSheetName(partition.Id))]);
        }

        var fileNames = WorkbookReviewFileNames(selected);
        var imageAssets = document.OmitImages ? [] : WorkbookReviewImages(graph, selected, assets, policy);
        var sheetReferences = imageAssets.ToDictionary(
            asset => asset.Id,
            asset => "../assets/" + WorkbookReviewAssetName(asset),
            StringComparer.Ordinal);
        var rootReferences = imageAssets.ToDictionary(
            asset => asset.Id,
            asset => "assets/" + WorkbookReviewAssetName(asset),
            StringComparer.Ordinal);
        var changeHistory = WorkbookReviewChangeHistory(graph, selected, policy);
        var missingImages = selected.SelectMany(partition => partition.Nodes).Where(node =>
            node.Kind == NodeKind.Image && DocumentContentPolicyRules.Includes(node, policy) &&
            node.Content is ReferenceNodeContent image && !rootReferences.ContainsKey(image.Reference)).ToArray();
        foreach (var image in missingImages)
            diagnostics.Add(new(document.OmitImages ? "ImageOmitted" : "ImageNotWritten",
                document.OmitImages ? "The image is intentionally replaced by its description." : "The image asset is unavailable; its source description is retained.",
                document.OmitImages ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning, NodeId: image.Id));
        var staging = Path.Combine(Path.GetDirectoryName(target)!,
            "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var published = false;
        var files = new List<WorkbookReviewFile>();
        try
        {
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(Path.Combine(staging, "sheets"));
            Directory.CreateDirectory(Path.Combine(staging, "assets"));
            foreach (var asset in imageAssets.DistinctBy(WorkbookReviewAssetName, StringComparer.Ordinal))
                await WriteNewAsync(Path.Combine(staging, "assets", WorkbookReviewAssetName(asset)),
                    asset.Content.ToArray(), cancellationToken).ConfigureAwait(false);
            var serializerDiagnostics = new List<MarkdownDiagnostic>();
            ReadableProjectionReport projection = ReadableProjectionReport.Empty;
            var sheetLinks = new List<(string FileName, string SheetName)>();
            for (var index = 0; index < selected.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var partition = selected[index];
                var sheetName = WorkbookReviewSheetName(partition.Id);
                var sheetGraph = graph with { Partitions = [partition] };
                sheetGraph = WorkbookReviewBindImages(sheetGraph, sheetReferences);
                var serializer = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(
                    document.ShowFormulas, document.IncludeSvgPreviews, document.IncludeDiagrams,
                    [sheetName], ReadableTitle(graph, source, document.Title), document.ContentPolicy,
                    document.OcrReview, document.ExpandMergedCells, document.Language,
                    FallbackTitle: sheetName, TableLayout: MarkdownTableLayout.Records,
                    IncludeSourceReferences: true, IncludeTableOfContents: false,
                    SourceName: Path.GetFileName(source), SeparateWorksheetAnnotations: true,
                    PortableMarkdown: options.PortableMarkdown));
                var body = serializer.Serialize(sheetGraph);
                if (WorkbookReviewIsChangeHistory(sheetName, out _))
                    body = "> " + WorkbookReviewInline(Path.GetFileName(source)) + " / " + WorkbookReviewInline(sheetName) +
                        "\n\n[全履歴 / Full history](../CHANGELOG.md)\n";
                serializerDiagnostics.AddRange(serializer.Diagnostics);
                projection = projection with
                {
                    RenderedTables = projection.RenderedTables + serializer.Report.RenderedTables,
                    SideBySideRegions = projection.SideBySideRegions + serializer.Report.SideBySideRegions,
                    TableBoundaryReviews = [.. projection.TableBoundaryReviews, .. serializer.Report.TableBoundaryReviews],
                    UnwrittenImageIds = [.. projection.UnwrittenImageIds, .. serializer.Report.UnwrittenImageIds],
                };
                var fileName = fileNames[partition.Id];
                var text = new StringBuilder();
                text.Append("# ").Append(WorkbookReviewInline(Path.GetFileName(source))).Append(" / ")
                    .Append(WorkbookReviewInline(sheetName)).AppendLine();
                text.AppendLine();
                if (changeHistory.Count > 0) text.Append("> [変更履歴 / Change Log](../CHANGELOG.md)").AppendLine();
                text.AppendLine();
                text.AppendLine(body);
                await WriteNewAsync(Path.Combine(staging, "sheets", fileName),
                    new UTF8Encoding(false).GetBytes(text.ToString()), cancellationToken).ConfigureAwait(false);
                files.Add(new WorkbookReviewFile("sheets/" + fileName, sheetName, partition.Id));
                sheetLinks.Add((fileName, sheetName));
            }
            if (changeHistory.Count > 0)
            {
                var text = new StringBuilder();
                text.Append("# ").Append(WorkbookReviewInline(Path.GetFileName(source)))
                    .Append(" / ").AppendLine(WorkbookReviewChangeHistoryHeading());
                text.AppendLine();
                foreach (var sheetName in changeHistory.Select(partition => WorkbookReviewSheetName(partition.Id)))
                    text.Append("> ").Append(WorkbookReviewInline(sheetName)).AppendLine();
                text.AppendLine();
                foreach (var partition in changeHistory)
                {
                    var sheetGraph = graph with { Partitions = [partition] };
                    sheetGraph = WorkbookReviewBindImages(sheetGraph, rootReferences);
                    var serializer = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(
                        document.ShowFormulas, document.IncludeSvgPreviews, document.IncludeDiagrams,
                        null, WorkbookReviewChangeHistoryHeading(), document.ContentPolicy,
                        document.OcrReview, document.ExpandMergedCells, document.Language,
                        FallbackTitle: WorkbookReviewSheetName(partition.Id),
                        TableLayout: MarkdownTableLayout.Records, IncludeSourceReferences: true,
                        IncludeTableOfContents: false, SourceName: Path.GetFileName(source),
                        SeparateWorksheetAnnotations: true, PortableMarkdown: options.PortableMarkdown));
                    text.AppendLine(serializer.Serialize(sheetGraph));
                    serializerDiagnostics.AddRange(serializer.Diagnostics);
                }
                await WriteNewAsync(Path.Combine(staging, "CHANGELOG.md"),
                    new UTF8Encoding(false).GetBytes(text.ToString()), cancellationToken).ConfigureAwait(false);
                files.Add(new WorkbookReviewFile("CHANGELOG.md"));
            }
            var htmlAssets = imageAssets.DistinctBy(asset => rootReferences[asset.Id], StringComparer.Ordinal).ToDictionary(
                asset => rootReferences[asset.Id],
                asset => new ReadableHtmlAsset(asset.MediaType, asset.Content),
                StringComparer.Ordinal);
            var layoutGraph = graph with { Partitions = selected };
            layoutGraph = WorkbookReviewBindImages(layoutGraph, rootReferences);
            var projected = ReadableProjectionBuilder.Build(layoutGraph, new ReadableProjectionOptions(
                document.ContentPolicy, [.. selected.Select(partition => WorkbookReviewSheetName(partition.Id))],
                ReadableTitle(graph, source, document.Title), FallbackTitle: Path.GetFileNameWithoutExtension(source),
                Language: document.Language, IncludeDiagrams: document.IncludeDiagrams,
                OcrReview: document.OcrReview, SourceName: Path.GetFileName(source)), cancellationToken);
            var written = ReadableHtmlWriter.WriteDocument(projected.Document, new ReadableHtmlOptions(
                ReadableTitle(graph, source, document.Title), document.Language,
                htmlAssets.Count > 0 ? htmlAssets : null, ShowFormulas: document.ShowFormulas,
                Generator: "DocRedock review"), cancellationToken);
            serializerDiagnostics.AddRange(projected.Diagnostics);
            foreach (var item in serializerDiagnostics)
            {
                if (diagnostics.Any(existing => existing.Code == item.Code &&
                    StringComparer.Ordinal.Equals(existing.NodeId, item.BlockId))) continue;
                diagnostics.Add(new Diagnostic(item.Code, item.Message, item.Severity switch
                {
                    MarkdownDiagnosticSeverity.Info => DiagnosticSeverity.Information,
                    MarkdownDiagnosticSeverity.Error => DiagnosticSeverity.Error,
                    _ => DiagnosticSeverity.Warning,
                }, NodeId: item.BlockId));
            }
            diagnostics.AddRange(written.Diagnostics.Select(item => new Diagnostic(item.Code, item.Message, DiagnosticSeverity.Warning)));
            diagnostics = AdapterWarningDiagnostics.Normalize(diagnostics).ToList();
            await WriteNewAsync(Path.Combine(staging, "layout.html"),
                new UTF8Encoding(false).GetBytes(written.Html), cancellationToken).ConfigureAwait(false);
            files.Add(new WorkbookReviewFile("layout.html"));
            await WriteNewAsync(Path.Combine(staging, "README.md"),
                new UTF8Encoding(false).GetBytes(WorkbookReviewReadme(document, options, sheetLinks,
                    changeHistory.Count > 0, imageAssets.Count, sheetReferences.Count > 0)), cancellationToken).ConfigureAwait(false);
            files.Add(new WorkbookReviewFile("README.md"));
            var selectedNodes = selected.SelectMany(partition => partition.Nodes)
                .Where(node => DocumentContentPolicyRules.Includes(node, policy)).ToArray();
            var indexedNodes = selected.ToDictionary(partition => partition.Id,
                partition => partition.Nodes.Where(node => DocumentContentPolicyRules.Includes(node, policy)).Select(node => node.Id).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
            var manifest = new
            {
                schemaVersion = "1.0",
                kind = "docredock-workbook-review",
                sourceFile = Path.GetFileName(source),
                sourceSha256 = sourceHash,
                convertedAtUtc = DateTime.UtcNow.ToString("O"),
                portableMarkdown = options.PortableMarkdown,
                sheets = selected.Select(partition => new
                {
                    originalSheetName = WorkbookReviewSheetName(partition.Id),
                    partitionId = partition.Id,
                    path = "sheets/" + fileNames[partition.Id],
                    cellRanges = partition.Nodes.Where(node => DocumentContentPolicyRules.Includes(node, policy)).Select(WorkbookReviewRange)
                        .Where(range => range is not null).Distinct().ToArray(),
                }).ToArray(),
            };
            await WriteNewAsync(Path.Combine(staging, "manifest.json"),
                new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(manifest, WorkbookReviewJson) + "\n"), cancellationToken).ConfigureAwait(false);
            files.Add(new WorkbookReviewFile("manifest.json"));
            var sourceIndex = new
            {
                schemaVersion = "1.0",
                sourceFile = Path.GetFileName(source),
                nodes = selected.SelectMany(partition => partition.Nodes
                    .Where(node => DocumentContentPolicyRules.Includes(node, policy)).Select(node => new
                {
                    nodeId = node.Id,
                    sheetName = WorkbookReviewSheetName(partition.Id),
                    kind = node.Kind.ToString(),
                    cellRange = WorkbookReviewRange(node),
                    sourcePart = node.Source?.PartUri,
                    sourceLocators = node.Source?.Locators.Where(locator => locator.Kind is
                        "cell_address" or "cell_range" or "cell_note" or "drawing_part" or "image_relationship" or "shape_id").ToArray(),
                })).ToArray(),
                sourceDrawingObjects = selected.SelectMany(partition => (sourceScenes.GetValueOrDefault(partition.Id)?.Objects ?? [])
                    .Where(item => policy == DocumentContentPolicy.Complete || !item.Hidden)
                    .Where(item => item.Kind != "picture" || item.SourceNodeId is { } nodeId && indexedNodes[partition.Id].Contains(nodeId))
                    .Select(item => new
                    {
                        objectId = item.Id, sourceNodeId = item.SourceNodeId, kind = item.Kind,
                        sheetName = WorkbookReviewSheetName(partition.Id), sourcePart = item.Source?.PartUri,
                        sourceLocators = item.Source?.Locators, width = item.Width, height = item.Height, transform = item.Transform,
                    })).ToArray(),
            };
            await WriteNewAsync(Path.Combine(staging, "source-index.json"),
                new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(sourceIndex, WorkbookReviewJson) + "\n"), cancellationToken).ConfigureAwait(false);
            files.Add(new WorkbookReviewFile("source-index.json"));
            if (!document.OmitImages)
                projection = projection with { UnwrittenImageIds = [.. projection.UnwrittenImageIds, .. missingImages.Select(image => image.Id)] };
            var conversionReport = ReadableConversionReportBuilder.Build(graph, diagnostics, projection,
                Path.GetFileName(source), "markdown-review", document.ContentPolicy, document.Sheets);
            await WriteNewAsync(Path.Combine(staging, "conversion-report.json"),
                new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(conversionReport, WorkbookReviewJson) + "\n"), cancellationToken).ConfigureAwait(false);
            files.Add(new WorkbookReviewFile("conversion-report.json"));
            cancellationToken.ThrowIfCancellationRequested();
            if (await AiHashAsync(source, cancellationToken).ConfigureAwait(false) != sourceHash)
                throw new IOException("The source file changed during export; the review directory was discarded.");
            cancellationToken.ThrowIfCancellationRequested();
            DocRedock.Core.IO.ExclusiveFileSystem.MoveDirectory(staging, target);
            published = true;
            return new WorkbookReviewExportResult(target, files, graph,
                diagnostics, projection) { Conversion = conversionReport };
        }
        catch
        {
            // Only the staging directory this call created is ever removed; a published
            // directory and any pre-existing output are never touched.
            if (!published) TryDeleteDirectory(staging);
            throw;
        }
    }

    private static readonly JsonSerializerOptions WorkbookReviewJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static DocumentGraph WorkbookReviewBindImages(DocumentGraph graph, Dictionary<string, string> references) => graph with
    {
        Partitions = graph.Partitions.Select(partition => partition with
        {
            Nodes = partition.Nodes.Select(node =>
            {
                if (node.Kind != NodeKind.Image || node.Content is not ReferenceNodeContent image) return node;
                if (references.TryGetValue(image.Reference, out var reference)) return node with { Content = image with { Reference = reference } };
                var extensions = node.Extensions?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ?? new();
                extensions[ReadableMarkdownSerializer.ImageOmittedExtension] = JsonSerializer.SerializeToElement(true);
                extensions[ReadableMarkdownSerializer.ImageAssetIdExtension] = JsonSerializer.SerializeToElement(image.Reference);
                return node with { Extensions = extensions };
            }).ToArray()
        }).ToArray()
    };

    /// <summary>The original worksheet name behind a partition id ("sheet-<name>" forms).</summary>
    private static string WorkbookReviewSheetName(string partitionId)
    {
        var name = partitionId.Trim();
        foreach (var prefix in new[] { "sheet-", "worksheet-", "partition-" })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return name[prefix.Length..];
        return name;
    }

    private static string WorkbookReviewSafeText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsControl(character) && character is not ('\n' or '\r' or '\t') ? '?' : character);
        return builder.ToString();
    }

    /// <summary>Escaped inline Markdown text for untrusted names; never emits raw HTML.</summary>
    private static string WorkbookReviewInline(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in WorkbookReviewSafeText(value))
        {
            if (character is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '#' or '|' or '~')
                builder.Append('\\');
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static string? WorkbookReviewRange(DocumentNode node)
    {
        var start = ExtensionText(node, "address") ?? ExtensionText(node, "cell_ref") ?? node.Source?.Locators
            .FirstOrDefault(locator => locator.Kind is "cell_address" or "cell_range" or "cell")?.Value;
        if (start is null || start.Contains(':')) return start;
        if (node.Extensions?.TryGetValue("merged_to_row", out var row) == true && row.TryGetInt32(out var r) &&
            node.Extensions.TryGetValue("merged_to_column", out var column) && column.TryGetInt32(out var c) &&
            r is > 0 and <= 1048576 && c is > 0 and <= 16384)
        {
            var end = new StringBuilder();
            for (var current = c; current > 0; current = (current - 1) / 26) end.Insert(0, (char)('A' + (current - 1) % 26));
            end.Append(r);
            if (!StringComparer.OrdinalIgnoreCase.Equals(start, end.ToString())) return start + ":" + end;
        }
        return start;
    }

    private static bool WorkbookReviewIsChangeHistory(string sheetName, out int matchIndex)
    {
        matchIndex = -1;
        var name = sheetName.Trim();
        for (var index = 0; index < WorkbookReviewChangeHistoryNames.Length; index++)
            if (name.Equals(WorkbookReviewChangeHistoryNames[index], StringComparison.OrdinalIgnoreCase))
            {
                matchIndex = index;
                return true;
            }
        return false;
    }

    private static string WorkbookReviewChangeHistoryHeading() => "変更履歴 / Change Log";

    private static List<DocumentPartition> WorkbookReviewChangeHistory(
        DocumentGraph graph, DocumentPartition[] selected, DocumentContentPolicy policy)
    {
        var matches = selected
            .Where(partition => partition.Nodes.Any(node => DocumentContentPolicyRules.Includes(node, policy)))
            .Where(partition => WorkbookReviewIsChangeHistory(WorkbookReviewSheetName(partition.Id), out _))
            .OrderBy(partition => WorkbookReviewIsChangeHistory(WorkbookReviewSheetName(partition.Id), out var index)
                ? index : int.MaxValue)
            .ThenBy(partition => partition.Order)
            .ToList();
        return matches;
    }

    /// <summary>Stable numbered file names: 001_ prefix keeps order, avoids reserved names and
    /// collisions (A|B vs A_B), and the length cap bounds the sanitized original name.</summary>
    private static Dictionary<string, string> WorkbookReviewFileNames(DocumentPartition[] selected)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < selected.Length; index++)
        {
            var partition = selected[index];
            var stem = WorkbookReviewSanitizeName(WorkbookReviewSheetName(partition.Id));
            var candidate = (index + 1).ToString("000", System.Globalization.CultureInfo.InvariantCulture) + "_" + stem;
            var suffix = 2;
            while (!used.Add(candidate)) candidate = (index + 1).ToString("000", System.Globalization.CultureInfo.InvariantCulture) + "_" + stem + "_" + suffix++;
            names[partition.Id] = candidate + ".md";
        }
        return names;
    }

    private static string WorkbookReviewSanitizeName(string name)
    {
        var builder = new StringBuilder();
        foreach (var character in name)
        {
            if (char.IsControl(character) || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?')
                continue;
            builder.Append(character);
        }
        var sanitized = builder.ToString().Trim().TrimEnd('.', ' ');
        sanitized = Regex.Replace(sanitized, @"\s+", "_");
        if (sanitized.Length > 60) sanitized = sanitized[..60].TrimEnd('.', ' ', '_');
        if (sanitized.Length == 0) sanitized = "sheet";
        // The numeric prefix already keeps CON, PRN, AUX, NUL and COM1-9 from ever matching.
        return sanitized;
    }

    private static string WorkbookReviewAssetName(WorkspaceAsset asset)
    {
        var extension = Path.GetExtension(asset.FileName).ToLowerInvariant();
        if (!Regex.IsMatch(extension, @"^\.[a-z0-9]{1,8}$")) extension = string.Empty;
        return asset.Sha256 + extension;
    }

    /// <summary>Distinct image bytes actually referenced by policy-included nodes of the
    /// selected partitions; identical bytes share one assets/&lt;sha256&gt; file.</summary>
    private static List<WorkspaceAsset> WorkbookReviewImages(
        DocumentGraph graph, DocumentPartition[] selected, IReadOnlyList<WorkspaceAsset> assets,
        DocumentContentPolicy policy)
    {
        var referenced = selected.SelectMany(partition => partition.Nodes)
            .Where(node => DocumentContentPolicyRules.Includes(node, policy))
            .Where(node => node.Kind == NodeKind.Image && node.Content is ReferenceNodeContent)
            .Select(node => ((ReferenceNodeContent)node.Content).Reference)
            .ToHashSet(StringComparer.Ordinal);
        var result = new List<WorkspaceAsset>();
        foreach (var asset in assets)
        {
            if (!asset.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                !referenced.Contains(asset.Id)) continue;
            result.Add(asset);
        }
        return result;
    }

    private static string WorkbookReviewReadme(
        ReadableDocumentExportOptions document, WorkbookReviewExportOptions options,
        List<(string FileName, string SheetName)> sheets, bool hasChangeLog, int assetCount, bool hasImages)
    {
        var text = new StringBuilder();
        text.Append("# ").Append(WorkbookReviewInline(Path.GetFileName(document.SourcePath)))
            .AppendLine(" — レビュー / Review");
        text.AppendLine();
        if (hasChangeLog) text.AppendLine("- [CHANGELOG.md](CHANGELOG.md)").AppendLine();
        foreach (var (fileName, sheetName) in sheets)
            text.Append("- [").Append(WorkbookReviewInline(sheetName)).Append("](sheets/")
                .Append(MarkdownPathEncoder.Encode(fileName)).AppendLine(")");
        text.Append("- [layout.html](layout.html) — source-color/merge/print companion rendered directly from the extraction graph").AppendLine();
        text.Append("- [manifest.json](manifest.json), [source-index.json](source-index.json), [conversion-report.json](conversion-report.json)").AppendLine();
        if (hasImages)
            text.Append("- assets/ — ").Append(assetCount).AppendLine(" image file(s), named by SHA-256, deduplicated by content");
        text.AppendLine();
        text.AppendLine(options.PortableMarkdown
            ? "Markdown dialect: CommonMark with record-style tables (portable; no raw HTML). Math is shown as stored text; no math interpretation is performed."
            : "Markdown dialect: GFM (GitHub Flavored Markdown). Math is shown as stored text; no math interpretation is performed.");
        text.AppendLine();
        text.Append("Content policy: ").Append(WorkbookReviewInline(document.ContentPolicy))
            .AppendLine(". Saved formula results are shown by default; formula expressions are optional. Excel recalculation and data validation are not performed.")
            .AppendLine();
        text.AppendLine("This export is recognized-object accounting, not proof that all source content was parsed.");
        return text.ToString();
    }
}
