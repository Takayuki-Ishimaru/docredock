using System.Globalization;
using System.Text.Json;
using DocRedock.Core.Documents;
using static DocRedock.Markdown.ReadableGraphPreparation;

namespace DocRedock.Markdown;

public static partial class ReadableProjectionBuilder
{
    private sealed partial class Projection
    {
        private IReadOnlyList<ReadableBlock> Paragraphs(DocumentNode node)
        {
            var details = Extension(node, "paragraph_details");
            if (details.ValueKind != JsonValueKind.Array) return [new ReadableParagraph(ReadableRunProjection.Content(node)) { Origin = Origin(node) }];
            var paragraphs = new List<(ReadableContent Content, bool Bullet, int Level, int? Number, int Prefix)>();
            foreach (var detail in details.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var runs = Property(detail, "Runs", "runs");
                var content = runs.ValueKind == JsonValueKind.Array ? new ReadableContent(runs.EnumerateArray().Select(run => new TextRun(
                    Text(run, "Text", "text") ?? "", Bold: Flag(run, "Bold", "bold"), Italic: Flag(run, "Italic", "italic"),
                    Underline: Flag(run, "Underline", "underline"), Strike: Flag(run, "Strike", "strike"),
                    LinkTarget: Text(run, "LinkTarget", "linkTarget", "link_target"), Color: Text(run, "Color", "color"))).ToArray()) :
                    ReadableContent.Text(Text(detail, "Text", "text") ?? "");
                var bullet = Flag(detail, "IsBullet", "isBullet", "is_bullet");
                var text = content.PlainText;
                var leading = text.Length - text.TrimStart().Length;
                var prefix = 0;
                if (leading < text.Length && "•●○▪■‣◦".Contains(text[leading]))
                {
                    var from = leading + 1;
                    while (from < text.Length && char.IsWhiteSpace(text[from])) from++;
                    prefix = from; bullet = true;
                }
                var ordinal = Number(Property(detail, "Number", "number", "ListNumber", "listNumber"));
                paragraphs.Add((content, bullet, Math.Clamp(Int(detail, "Level", "level"), 0, 16), ordinal is > 0 and < int.MaxValue ? (int)ordinal.Value : null, prefix));
            }
            var joined = paragraphs.SelectMany((p, i) => i == 0 ? p.Content.Runs : new[] { new TextRun("\n") }.Concat(p.Content.Runs)).ToArray();
            var decorated = new ReadableContent(ReadableRunProjection.Decorate(node, joined));
            var offset = 0;
            var result = new List<ReadableBlock>();
            foreach (var (rawContent, bullet, level, number, prefix) in paragraphs)
            {
                var content = Slice(decorated, offset + prefix, rawContent.PlainText.Length - prefix);
                offset += rawContent.PlainText.Length + 1;
                if (bullet) result.Add(new ReadableList([new(content, level, number is not null, number) { Origin = Origin(node) }]));
                else if (content.PlainText.Length > 0) result.Add(new ReadableParagraph(content) { Origin = Origin(node) });
            }
            return CombineLists(result);
        }

        private ReadableDetails Ocr(DocumentNode node)
        {
            var blocks = new List<ReadableBlock> { new ReadableQuote([new ReadableParagraph(ReadableRunProjection.Content(node))]) };
            var raw = Extension(node, "ocr_regions");
            if (raw.ValueKind == JsonValueKind.Array)
            {
                var regions = raw.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object).ToArray();
                var needsReview = regions.Where(ReadableMarkdownSerializer.OcrRegionNeedsReview).ToArray();
                var shown = options.OcrReview switch { OcrReviewMode.All => regions, OcrReviewMode.Summary => [], _ => needsReview };
                blocks.Add(new ReadableParagraph(ReadableContent.Text(ja
                    ? $"OCR照合情報：信頼度はエンジンの推定値です。識別子・数値を原画像と照合してください。全{regions.Length}件、低信頼・信頼度不明{needsReview.Length}件、詳細表示{shown.Length}件。"
                    : $"OCR confidence is an engine estimate. Compare identifiers and numbers with the source image. Total {regions.Length}; low or unknown {needsReview.Length}; shown {shown.Length}.")));
                if (shown.Length > 0)
                {
                    var rows = new List<ReadableTableRow> { new((ja ? new[] { "行", "認識文字", "信頼度", "原画像の位置" } : ["Line", "Recognized text", "Confidence", "Source image position"])
                        .Select(t => new ReadableTableCell(ReadableContent.Text(t))).ToArray()) };
                    foreach (var region in shown)
                    {
                        var confidence = Number(Property(region, "confidence"));
                        var position = ja ? "未提供" : "not provided";
                        var box = Property(region, "bounding_box");
                        if (box.ValueKind == JsonValueKind.Object)
                        {
                            try { if (box.Deserialize<Geometry>() is { } geometry) position = FormattableString.Invariant($"{geometry.CoordinateSpace}: x={geometry.X:0.##}, y={geometry.Y:0.##}, w={geometry.Width:0.##}, h={geometry.Height:0.##}"); }
                            catch (JsonException) { }
                        }
                        rows.Add(new(new[] { Number(Property(region, "line_number"))?.ToString(CultureInfo.InvariantCulture) ?? position,
                            Text(region, "text") ?? "", confidence is { } score ? (score * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : (ja ? "未提供" : "not provided"), position }
                            .Select(t => new ReadableTableCell(ReadableContent.Text(t))).ToArray()));
                    }
                    blocks.Add(new ReadableTable(Enumerable.Range(1, 4).Select(i => new ReadableColumn(i)).ToArray(), rows));
                }
            }
            return new("ocr-extraction", ReadableContent.Text(ja ? "OCR抽出テキスト・照合情報" : "OCR text and source review"), blocks) { Origin = Origin(node) };
        }

        private ReadableDiagram Diagram(DocumentNode node)
        {
            VisualGraph? visual = null;
            var raw = Extension(node, "visual_graph");
            if (raw.ValueKind == JsonValueKind.Object)
            { try { visual = raw.Deserialize<VisualGraph>(); } catch (JsonException) { } }
            string? source = ExtensionText(node, "mermaid") ?? ExtensionText(node, "mermaid_source") ?? ExtensionText(node, "diagram_mermaid") ??
                (ExtensionText(node, "diagram_language") == "mermaid" ? Plain(node) : null);
            var fallback = new List<ReadableBlock>();
            if (visual is not null)
            {
                var validation = VisualGraphValidator.Validate(visual);
                foreach (var issue in validation.Errors.Concat(validation.Warnings)) Add(issue.Code, issue.Message, issue.SourceNodeId ?? node.Id);
                foreach (var issue in visual.Diagnostics ?? []) Add(issue.Code, issue.Message, node.Id);
                if (options.IncludeDiagrams && validation.Quality != VisualGraphQuality.Invalid)
                    source = ReadableMarkdownSerializer.ProjectVisualGraph(visual);
                if (source is not null && !sourceSceneAvailable)
                    Add("ReadableDiagramLayoutReflowed", "Diagram labels and connections are redrawn; original positions and overlapping annotations require source comparison.", node.Id, MarkdownDiagnosticSeverity.Info);
                if (source is null)
                {
                    Add("VisualSemanticProjectionFallback", "The visual graph could not be drawn safely; source labels and connections are retained.", node.Id);
                }
                // These are useful text, not just an emergency representation. They keep
                // labels searchable and preserve edge/label association without Mermaid.
                var labels = (visual.Nodes ?? []).Where(n => n is not null && !string.IsNullOrEmpty(n.Id)).GroupBy(n => n.Id)
                    .ToDictionary(g => g.Key, g => g.First().Label ?? g.Key, StringComparer.Ordinal);
                foreach (var label in labels.Values.Distinct(StringComparer.Ordinal))
                    fallback.Add(new ReadableParagraph(ReadableContent.Text(label)));
                foreach (var edge in (visual.Edges ?? []).Where(e => e is not null))
                    fallback.Add(new ReadableParagraph(ReadableContent.Text(
                        labels.GetValueOrDefault(edge.SourceId ?? "", edge.SourceId ?? "?") +
                        (edge.IsBidirectional ? " ↔ " : edge.IsUndirected ? " — " : " → ") +
                        labels.GetValueOrDefault(edge.TargetId ?? "", edge.TargetId ?? "?") +
                        (string.IsNullOrWhiteSpace(edge.Label) ? "" : ": " + edge.Label))));
                if (visual.IsPartialProjection) Add("VisualSemanticProjectionPartial", "Some visual relationships require comparison with the source.", node.Id);
            }
            else if (HasExtension(node, "visual_graph")) Add("VisualSemanticProjectionFallback", "Visual metadata could not be read; source content is retained.", node.Id);
            if (!options.IncludeDiagrams) source = null;
            var items = Extension(node, "diagram_items");
            if (items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                {
                    var label = item.ValueKind == JsonValueKind.String ? item.GetString() : Text(item, "text", "Text", "label", "Label");
                    if (!string.IsNullOrEmpty(label)) fallback.Add(new ReadableParagraph(ReadableContent.Text(label)));
                }
            if (source is null && Plain(node).Length > 0) fallback.Add(new ReadableParagraph(ReadableRunProjection.Content(node)));
            return new(source, ExtensionText(node, "diagram_alt") ?? (ja ? "図" : "Diagram"), fallback, visual) { Origin = Origin(node) };
        }

        private IEnumerable<ReadableBlock> Chart(DocumentNode node)
        {
            var title = ExtensionText(node, "chart_title");
            if (!string.IsNullOrEmpty(title)) yield return new ReadableHeading(3, ReadableContent.Text(title)) { Origin = Origin(node) };
            var series = Extension(node, "chart_series");
            if (series.ValueKind != JsonValueKind.Array) yield break;
            foreach (var item in series.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] Values(string upper, string lower)
                {
                    var value = Property(item, upper, lower);
                    return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString()).ToArray() : [];
                }
                var categories = Values("Categories", "categories"); var values = Values("Values", "values");
                var rows = new List<ReadableTableRow> { new([new(ReadableContent.Text(ja ? "カテゴリ" : "Category")), new(ReadableContent.Text(Text(item, "Name", "name") ?? (ja ? "値" : "Value")))]) };
                for (var i = 0; i < Math.Max(categories.Length, values.Length); i++) rows.Add(new([
                    new(ReadableContent.Text(i < categories.Length ? categories[i] : (i + 1).ToString(CultureInfo.InvariantCulture))),
                    new(ReadableContent.Text(i < values.Length ? values[i] : ""))]));
                if (rows.Count > 1) yield return new ReadableTable([new(1), new(2)], rows) { Origin = Origin(node) };
            }
        }

        private IEnumerable<ReadableBlock> OverlayNotes(DocumentNode node)
        {
            var raw = Extension(node, "table_overlays");
            var single = Extension(node, "sheet_overlay");
            var items = raw.ValueKind == JsonValueKind.Array ? raw.EnumerateArray().ToArray() : single.ValueKind == JsonValueKind.Object ? new[] { single } : [];
            if (items.Length == 0) yield break;
            var lines = new List<ReadableBlock>();
            foreach (var item in items)
            {
                var text = Text(item, "Text", "text") ?? "";
                var shapeId = Text(item, "ShapeId", "shapeId");
                if (shapeId is not null && (appliedOverlayIds.Contains(node.Id + "\u001f" + shapeId) || nativeDrawingShapeIds.Contains(shapeId))) text = "";
                var kind = Text(item, "Kind", "kind") ?? "shape";
                var direction = Text(item, "Direction", "direction");
                var lineStyle = Text(item, "LineStyle", "lineStyle", "line_style");
                var row = Int(item, "StartRow", "startRow"); var col = Int(item, "StartColumn", "startColumn");
                var endRow = Int(item, "EndRow", "endRow"); var endColumn = Int(item, "EndColumn", "endColumn");
                if (raw.ValueKind == JsonValueKind.Array) { row++; col++; endRow++; endColumn++; }
                if (endRow < row) endRow = row;
                if (endColumn < col) endColumn = col;
                var position = row > 0 && col > 0 ? Column(col) + row + ":" + Column(endColumn) + endRow : "R" + row + "C" + col + "–R" + endRow + "C" + endColumn;
                lines.Add(new ReadableParagraph(ReadableContent.Text(text + " (" + kind + (direction is null ? "" : ", " + direction) +
                    (lineStyle is null ? "" : ", " + lineStyle) + "; " + position + ")")) { Origin = Origin(node) with { Range = position } });
            }
            if (!sourceSceneAvailable && items.Any(item => Text(item, "ShapeId", "shapeId") is not { } id || !appliedOverlayIds.Contains(node.Id + "\u001f" + id)))
                Add("ReadableOverlayLayout", "Shapes over cells are listed with their original row/column ranges; compare their placement with the source.", node.Id);
            yield return new ReadableDetails("cell-overlays", ReadableContent.Text(ja ? "セル上の図形・注記" : "Shapes and annotations over cells"), lines, true) { Origin = Origin(node) };
        }
    }
}
