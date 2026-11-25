using System.Globalization;
using DocRedock.Core.Documents;
using static DocRedock.Markdown.ReadableGraphPreparation;

namespace DocRedock.Markdown;

public static partial class ReadableProjectionBuilder
{
    private sealed partial class Projection
    {
        /// <summary>Cell styles of the tables drawn inside source scenes, by their CSS declarations.</summary>
        private readonly Dictionary<string, StyleDescriptor> sceneCellStyles = new(StringComparer.Ordinal);

        private IReadOnlyDictionary<string, StyleDescriptor>? StylesWithSceneCells()
        {
            if (sceneCellStyles.Count == 0) return graph.Styles;
            var styles = new Dictionary<string, StyleDescriptor>(graph.Styles ?? new Dictionary<string, StyleDescriptor>(), StringComparer.Ordinal);
            foreach (var style in sceneCellStyles.Values) styles[style.Id] = style;
            return styles;
        }

        /// <summary>Prefix of the <see cref="ReadableSourceDrawingScene.ObjectBlocks"/> key of a table without a node: the id of its drawing object follows.</summary>
        private const string LentTableKey = "drawing:";

        private static string Css(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        private static string? FontFamily(IReadOnlyList<string>? fonts) => fonts is not { Count: > 0 } ? null :
            string.Concat(fonts.Where(font => font.Length > 0 && font.IndexOfAny(['"', '\\', ';', '{', '}', '<', '>']) < 0 && !font.Any(char.IsControl))
                .Select(font => "\"" + font + "\",")) + "sans-serif";

        /// <summary>A gradient fill as a CSS gradient. DrawingML measures the angle clockwise from left to right, CSS from bottom to top.</summary>
        private static string? GradientCss(DrawingPaint? paint)
        {
            if (paint is not { Kind: "linear" or "radial", Stops.Count: >= 2 and <= 32 } || !double.IsFinite(paint.AngleDegrees)) return null;
            static string Stop(DrawingGradientStop stop) => (stop.Opacity >= 1 || !double.IsFinite(stop.Opacity) ? stop.Color :
                "rgba(" + Convert.ToInt32(stop.Color[1..3], 16) + "," + Convert.ToInt32(stop.Color[3..5], 16) + "," + Convert.ToInt32(stop.Color[5..7], 16) + "," +
                Math.Clamp(stop.Opacity, 0, 1).ToString("0.###", CultureInfo.InvariantCulture) + ")") + " " + Css(100 * Math.Clamp(stop.Position, 0, 1)) + "%";
            if (paint.Stops.Any(stop => stop.Color is not { Length: 7 } color || color[0] != '#' || color.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0 || !double.IsFinite(stop.Position))) return null;
            var stops = string.Join(',', paint.Stops.Select(Stop));
            if (paint.Kind == "linear") return "linear-gradient(" + Css(((paint.AngleDegrees + 90) % 360 + 360) % 360) + "deg," + stops + ")";
            var focus = paint.Focus ?? new(.5, .5, .5, .5);
            static double Centre(double near, double far) => double.IsFinite(near) && double.IsFinite(far) ? Math.Clamp((near + 1 - far) / 2, -10, 11) : .5;
            return "radial-gradient(ellipse farthest-corner at " + Css(100 * Centre(focus.Left, focus.Right)) + "% " + Css(100 * Centre(focus.Top, focus.Bottom)) + "%," + stops + ")";
        }

        /// <summary>
        /// A pattern fill as its 8x8 bitmap in a repeated SVG picture, one bitmap pixel per CSS pixel as for a shape:
        /// the foreground pixels as one path over the background color.
        /// </summary>
        private static string? PatternCss(DrawingPaint? paint)
        {
            static bool Hex(string? color) => color is { Length: 7 } && color[0] == '#' && color.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
            if (paint is not { Kind: "pattern" } || !DrawingPatternPresets.IsKnown(paint.Pattern) || !Hex(paint.Color) || !Hex(paint.BackColor)) return null;
            static string Opacity(double value) => (double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1).ToString("0.###", CultureInfo.InvariantCulture);
            var path = new System.Text.StringBuilder();
            for (var y = 0; y < 8; y++)
                for (var x = 0; x < 8; x++)
                {
                    if (!DrawingPatternPresets.IsForeground(paint.Pattern, x, y)) continue;
                    var start = x;
                    while (x < 7 && DrawingPatternPresets.IsForeground(paint.Pattern, x + 1, y)) x++;
                    path.Append('M').Append(start).Append(' ').Append(y).Append('h').Append(x - start + 1).Append("v1h-").Append(x - start + 1).Append('z');
                }
            return "url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='8' height='8' shape-rendering='crispEdges'%3E" +
                "%3Crect width='8' height='8' fill='%23" + paint.BackColor![1..] + "' fill-opacity='" + Opacity(paint.BackOpacity) + "'/%3E" +
                "%3Cpath d='" + path + "' fill='%23" + paint.Color![1..] + "' fill-opacity='" + Opacity(paint.Opacity) + "'/%3E%3C/svg%3E\")";
        }

        /// <summary>A diagonal of a cell as a gradient whose middle band is the line: "to top right" runs it from the top left to the bottom right corner.</summary>
        private static string? DiagonalCss(SourceDrawingTableBorder? line, bool down)
        {
            if (line is not { Width: > 0, Color: { Length: 7 } color } || color[0] != '#' || color.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0) return null;
            var half = Css(Math.Clamp(line.Width, .5, 64) / 2) + "px";
            return "linear-gradient(to " + (down ? "top" : "bottom") + " right,transparent calc(50% - " + half + ")," + color + " calc(50% - " + half + ")," +
                color + " calc(50% + " + half + "),transparent calc(50% + " + half + "))";
        }

        private StyleDescriptor SceneStyle(string prefix, SortedDictionary<string, string> css)
        {
            var key = prefix + "|" + string.Join(';', css.Select(declaration => declaration.Key + ":" + declaration.Value));
            if (!sceneCellStyles.TryGetValue(key, out var descriptor))
                sceneCellStyles[key] = descriptor = new(prefix + "-" + sceneCellStyles.Count.ToString("D5", CultureInfo.InvariantCulture), Properties: css);
            return descriptor;
        }

        /// <summary>
        /// The cell's text with the style of each source run that is formatted differently from the cell. The source
        /// runs are matched to the text by position; a text the cell's paragraphs do not hold is left as it is.
        /// When a paragraph of the cell is aligned or indented on its own (<see cref="SourceDrawingTableCellStyle.ParagraphStyles"/>),
        /// every paragraph begins with a run that carries the paragraph's style ("scene-table-par-"): an empty one before the
        /// first, the line break between two paragraphs for the others. A line break inside a paragraph (a:br) stays as it is.
        /// </summary>
        private ReadableContent StyledSceneRuns(ReadableContent content, SourceDrawingTableCellStyle cell)
        {
            if (cell.Paragraphs is not { Count: > 0 } paragraphs) return content;
            // Each source run with the paragraph it belongs to; Starts marks the break that begins a paragraph after the first.
            var tagged = paragraphs.SelectMany((paragraph, index) => (index == 0 ? [] : new[] { (Run: new SourceDrawingTableRun("\n") { FontSize = cell.FontSize,
                Bold = cell.Bold, Italic = cell.Italic, Color = cell.Color, Fonts = cell.Fonts }, Paragraph: index, Starts: true) })
                .Concat(paragraph.Select(run => (Run: run, Paragraph: index, Starts: false)))).Where(item => item.Run.Text.Length > 0).ToArray();
            var source = tagged.Select(item => item.Run).ToArray();
            var plain = content.PlainText;
            var start = plain.Length == 0 ? -1 : string.Concat(source.Select(run => run.Text)).IndexOf(plain, StringComparison.Ordinal);
            if (start < 0) return content;
            var paragraphStyles = cell.ParagraphStyles is { } given && given.Count == paragraphs.Count ? given : null;
            string ParagraphStyle(int index)
            {
                var css = new SortedDictionary<string, string>(StringComparer.Ordinal);
                var paragraph = paragraphStyles![index];
                if (paragraph.TextAlign is { } align) css["text-align"] = align;
                if (paragraph.MarginLeft is > 0 and <= 5376 and var margin) css["padding-left"] = Css(margin) + "px";
                if (paragraph.Indent is > 0 and <= 5376 and var indent) css["text-indent"] = Css(indent) + "px";
                return SceneStyle("scene-table-par", css).Id;
            }
            string? StyleOf(SourceDrawingTableRun run)
            {
                var css = new SortedDictionary<string, string>(StringComparer.Ordinal);
                if (run.FontSize is { } size && size != cell.FontSize) css["font-size"] = Css(size) + "pt";
                if (run.Bold is { } bold && bold != cell.Bold) css["font-weight"] = bold ? "bold" : "normal";
                if (run.Italic is { } italic && italic != cell.Italic) css["font-style"] = italic ? "italic" : "normal";
                if (run.Color is { } color && color != cell.Color) css["color"] = color;
                if (FontFamily(run.Fonts) is { } family && family != FontFamily(cell.Fonts)) css["font-family"] = family;
                return css.Count == 0 ? null : SceneStyle("scene-table-run", css).Id;
            }
            var result = new List<TextRun>();
            var sourceIndex = 0; var sourceStart = -start; var position = 0;
            if (paragraphStyles is not null)
            {
                // The paragraph the text begins in: the one before a break that begins the text.
                var offset = 0; var first = tagged[^1].Paragraph;
                foreach (var item in tagged)
                {
                    if (offset + item.Run.Text.Length > start) { first = item.Starts ? item.Paragraph - 1 : item.Paragraph; break; }
                    offset += item.Run.Text.Length;
                }
                result.Add(new TextRun("", StyleId: ParagraphStyle(first)));
            }
            foreach (var run in content.Runs)
            {
                if (run.Kind != TextRunKind.Text || run.Text.Length == 0)
                {
                    // A break kept as a run of its own may be the one between two paragraphs.
                    var paragraphBreak = false;
                    if (paragraphStyles is not null && run.Text == "\n")
                    {
                        while (sourceIndex < source.Length - 1 && sourceStart + source[sourceIndex].Text.Length <= position) sourceStart += source[sourceIndex++].Text.Length;
                        paragraphBreak = tagged[sourceIndex].Starts && sourceStart == position;
                    }
                    result.Add(paragraphBreak ? new TextRun("\n", StyleId: ParagraphStyle(tagged[sourceIndex].Paragraph)) : run);
                    position += run.Text.Length; continue;
                }
                var end = position + run.Text.Length;
                while (position < end)
                {
                    while (sourceIndex < source.Length - 1 && sourceStart + source[sourceIndex].Text.Length <= position) sourceStart += source[sourceIndex++].Text.Length;
                    var until = Math.Min(end, Math.Max(position + 1, sourceStart + source[sourceIndex].Text.Length));
                    var piece = run.Text[(position - (end - run.Text.Length))..(until - (end - run.Text.Length))];
                    if (paragraphStyles is not null && tagged[sourceIndex].Starts && sourceStart == position && piece == "\n")
                    { result.Add(new TextRun("\n", StyleId: ParagraphStyle(tagged[sourceIndex].Paragraph))); position = until; continue; }
                    result.Add(piece.Trim('\n').Length == 0 ? run with { Text = piece } : run with { Text = piece, StyleId = StyleOf(source[sourceIndex]) ?? run.StyleId });
                    position = until;
                }
            }
            return new(result);
        }

        /// <summary>
        /// The table of a slide layout or master, which has no node: its cells and their text as the scene holds them.
        /// It is drawn in the scene only; no block of the slide and no Markdown comes from it.
        /// </summary>
        private static ReadableTable? LentSceneTable(SourceDrawingTableStyle style)
        {
            if (style.Cells.Count == 0 || style.Cells.All(row => row.All(cell => cell is null))) return null;
            var columns = Math.Max(style.ColumnWidths.Count, style.Cells.Max(row => row.Count));
            return new(Enumerable.Range(1, columns).Select(index => new ReadableColumn(index)).ToArray(), style.Cells.Select((row, r) => new ReadableTableRow(
                row.Select((cell, c) => (cell, c)).Where(item => item.cell is not null).Select(item =>
                {
                    var paragraphs = (item.cell!.Paragraphs ?? []).SkipWhile(paragraph => paragraph.All(run => run.Text.Length == 0)).ToList();
                    while (paragraphs.Count > 0 && paragraphs[^1].All(run => run.Text.Length == 0)) paragraphs.RemoveAt(paragraphs.Count - 1);
                    var runs = paragraphs.SelectMany((paragraph, index) => (index == 0 ? [] : new[] { new TextRun("\n") })
                        .Concat(paragraph.Where(run => run.Text.Length > 0).Select(run => new TextRun(run.Text, Underline: run.Underline, Strike: run.Strike)))).ToArray();
                    return new ReadableTableCell(new(runs), item.cell.RowSpan, item.cell.ColumnSpan, item.c + 1);
                }).ToArray(), r + 1)).ToArray(), HeaderRowCount: 0);
        }

        /// <summary>
        /// The table block as it is placed in its source frame: source column widths and row heights, and each cell's
        /// source fill, text and edges as a style of the document. An edge between two cells is written once, as the
        /// right or bottom edge of the cell before it; the table's Markdown comes from the unstyled block.
        /// </summary>
        private ReadableTable StyledSceneTable(ReadableTable table, SourceDrawingTableStyle style)
        {
            SourceDrawingTableCellStyle? At(int row, int column) =>
                row >= 0 && row < style.Cells.Count && column >= 0 && column < style.Cells[row].Count ? style.Cells[row][column] : null;
            static string? Edge(SourceDrawingTableBorder? own, SourceDrawingTableBorder? neighbor)
            {
                var edge = own is { Width: > 0 } || neighbor is not { Width: > 0 } ? own ?? neighbor : neighbor;
                if (edge is null) return null;
                if (edge.Width <= 0 || edge.Color is not { Length: 7 } color) return "none";
                // The source width; a browser draws a double line from 3 px.
                return Css(Math.Clamp(edge.Width, edge.Style == "double" ? 3 : .25, 64)) + "px " + (edge.Style ?? "solid") + " " + color;
            }
            var rows = table.Rows.Select((row, r) => row with
            {
                HeightPixels = style.RowHeights.Count == table.Rows.Count ? style.RowHeights[r] : row.HeightPixels,
                Cells = row.Cells.Select(cell =>
                {
                    if (cell.SourceColumn is not { } sourceColumn || At(r, sourceColumn - 1) is not { } source) return cell;
                    var c = sourceColumn - 1; var lastRow = r + Math.Max(1, cell.RowSpan) - 1; var lastColumn = c + Math.Max(1, cell.ColSpan) - 1;
                    var css = new SortedDictionary<string, string>(StringComparer.Ordinal);
                    void Set(string property, string? value) { if (value is not null) css[property] = value; }
                    Set("background-color", source.Fill == "none" ? "transparent" : source.Fill);
                    Set("color", source.Color);
                    Set("background-image", string.Join(',', new[] { DiagonalCss(source.DiagonalDown, down: true), DiagonalCss(source.DiagonalUp, down: false),
                        GradientCss(source.FillPaint), PatternCss(source.FillPaint) }.OfType<string>()) is { Length: > 0 } images ? images : null);
                    Set("font-family", FontFamily(source.Fonts));
                    Set("font-size", source.FontSize is { } size ? Css(size) + "pt" : null);
                    Set("font-weight", source.Bold is { } bold ? bold ? "bold" : "normal" : null);
                    Set("font-style", source.Italic is { } italic ? italic ? "italic" : "normal" : null);
                    Set("text-align", source.TextAlign); Set("vertical-align", source.VerticalAlign);
                    Set("border-right", Edge(source.Right, At(r, lastColumn + 1)?.Left));
                    Set("border-bottom", Edge(source.Bottom, At(lastRow + 1, c)?.Top));
                    // The reader's own rules draw a left edge on a first cell and a top edge on a first row only.
                    Set("border-left", c == 0 ? Edge(source.Left, null) : source.Left is null ? null : "none");
                    Set("border-top", r == 0 ? Edge(source.Top, null) : source.Top is null ? null : "none");
                    var content = StyledSceneRuns(cell.Content, source);
                    return css.Count == 0 ? cell with { Content = content } : cell with { Content = content, StyleId = SceneStyle("scene-table-cell", css).Id };
                }).ToArray(),
            }).ToArray();
            var columns = style.ColumnWidths.Count == table.Columns.Count
                ? table.Columns.Select((column, index) => column with { WidthPixels = style.ColumnWidths[index] }).ToArray() : table.Columns;
            return table with { Columns = columns, Rows = rows };
        }

        private List<ReadableBlock> PreferSourceDrawings(DocumentPartition part, IReadOnlyDictionary<string, DocumentNode> nodes,
            List<ReadableBlock> blocks)
        {
            if (!sourceDrawingScenes.TryGetValue(part.Id, out var raw) || raw.Count == 0) return blocks;
            var complete = DocumentContentPolicyRules.Parse(options.ContentPolicy) == DocumentContentPolicy.Complete;
            var scenes = new List<ReadableSourceDrawingScene>();
            var sourceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in raw)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var objects = new List<WorkbookDrawingObject>();
                var objectBlocks = new Dictionary<string, ReadableBlock>(StringComparer.Ordinal);
                var chartData = new Dictionary<string, IReadOnlyList<ReadableBlock>>(StringComparer.Ordinal);
                foreach (var item in source.Objects)
                {
                    if (!complete && item.Hidden) continue;
                    DocumentNode? node = null;
                    if (item.SourceNodeId is {} nodeId && !nodes.TryGetValue(nodeId, out node) &&
                        !(item.Kind == "shape" && item.Text.Length == 0 && !item.Hidden)) continue;
                    if (item.Kind == "picture")
                    {
                        // A picture another part lends to the scene (a slide layout's or master's) has no node: the scene names its package part.
                        if (node is null && item.SourceNodeId is null && item.ImageReference is not null && graph.Format == DocumentFormatKind.Pptx &&
                            item.Source?.PartUri is { } owner && owner.TrimStart('/') != source.Id) { objects.Add(item); continue; }
                        if (node?.Content is not ReferenceNodeContent image) continue;
                        objects.Add(item with { ImageReference = ExtensionBool(node, "image_omitted") ? null : image.Reference });
                        renderedImages.Add(node.Id);
                    }
                    else if (item.Kind == "table" && node is null && item.SourceNodeId is null)
                    {
                        // A table another part lends to the scene (a slide layout's or master's) has no node: the scene holds its cells.
                        if (graph.Format != DocumentFormatKind.Pptx || source.TableStyles?.GetValueOrDefault(item.Id) is not { } lentStyle ||
                            LentSceneTable(lentStyle) is not { } lent) continue;
                        objectBlocks[LentTableKey + item.Id] = StyledSceneTable(lent, lentStyle);
                        objects.Add(item);
                    }
                    else if (item.Kind == "table")
                    {
                        var table = blocks.OfType<ReadableTable>().FirstOrDefault(b => b.Origin?.NodeIds?.Contains(item.SourceNodeId!) == true);
                        if (table is null || node is null) continue;
                        objectBlocks[node.Id] = source.TableStyles?.GetValueOrDefault(item.Id) is { } tableStyle ? StyledSceneTable(table, tableStyle) : table;
                        objects.Add(item);
                    }
                    else
                    {
                        objects.Add(item);
                        if (item.Kind == "chart" && node is not null)
                            chartData[node.Id] = blocks.Where(b => b.Origin?.NodeIds?.Contains(node.Id) == true).ToArray();
                    }
                    if (node is not null) sourceIds.Add(node.Id);
                }
                if (objects.Count == 0) continue;
                scenes.Add(new(source with { Objects = objects }) { ObjectBlocks = objectBlocks, ChartData = chartData,
                    Origin = new(part.Id, NodeIds: objects.Select(o => o.SourceNodeId).OfType<string>().Distinct(StringComparer.Ordinal).ToArray()) });
            }
            if (scenes.Count == 0) return blocks;
            bool IsSourceBlock(ReadableBlock b) => b.Origin?.NodeIds is { Count: > 0 } ids && ids.All(sourceIds.Contains) ||
                b is ReadableList list && list.Items.Count > 0 && list.Items.All(item =>
                    item.Origin?.NodeIds is { Count: > 0 } itemIds && itemIds.All(sourceIds.Contains));
            bool IsSynthesizedGrid(ReadableBlock b) => b.Origin?.NodeIds?.Any(id => nodes.TryGetValue(id, out var node) && ExtensionBool(node, "shape_grid_table")) == true;
            bool IsDerivedSourceDiagram(ReadableBlock b) => b is ReadableDiagram && b.Origin?.NodeIds?.Any(id =>
                nodes.TryGetValue(id, out var node) && HasExtension(node, "visual_graph")) == true;
            var descriptions = blocks.Where(b => IsDerivedSourceDiagram(b) || IsSynthesizedGrid(b) || IsSourceBlock(b) &&
                b.Origin?.NodeIds?.All(id => !nodes.TryGetValue(id, out var node) || node.Kind != NodeKind.Chart) != false &&
                b is not (ReadableParagraph or ReadableHeading or ReadableImage or ReadableTable)).ToArray();
            var retained = blocks.Where(b => !IsSourceBlock(b) && !IsSynthesizedGrid(b) && !IsDerivedSourceDiagram(b)).ToList();
            if (graph.Format == DocumentFormatKind.Pptx) retained.InsertRange(0, scenes);
            else
            {
                // Anchor-only paragraphs are intentionally absent from the text projection.
                // Use their stable source order, not the next coincidentally matching caption.
                foreach (var scene in scenes.OrderBy(s => s.Scene.AnchorOrder ?? int.MaxValue))
                {
                    var anchor = scene.Scene.AnchorNodeId;
                    // The paragraph's plain pictures stay ordinary blocks: a frame goes between them where the document has it.
                    var place = scene.Origin?.NodeIds?.Min(id => nodes.TryGetValue(id, out var n) ? ExtensionInt(n, DocumentSourceOrder.SiblingSequenceKey) : null);
                    bool IsSibling(ReadableBlock b, bool earlier) => place is not null && b.Origin?.NodeIds?.Any(id =>
                        nodes.TryGetValue(id, out var n) && n.ParentId == anchor && ExtensionInt(n, DocumentSourceOrder.SiblingSequenceKey) is { } other &&
                        (earlier ? other < place : other > place)) == true;
                    // A later frame of the same paragraph follows the frames already placed after it.
                    var exact = retained.FindLastIndex(b => b.Origin?.NodeIds?.Contains(anchor!) == true || IsSibling(b, earlier: true) ||
                        anchor is not null && b is ReadableSourceDrawingScene placed && placed.Scene.AnchorNodeId == anchor);
                    var index = exact >= 0 ? exact + 1 : retained.FindIndex(b => IsSibling(b, earlier: false) || b.Origin?.NodeIds?.Any(id =>
                        nodes.TryGetValue(id, out var n) && n.Order > (scene.Scene.AnchorOrder ?? int.MaxValue)) == true);
                    retained.Insert(index < 0 ? retained.Count : index, scene);
                }
            }
            if (descriptions.Length > 0) retained.Add(new ReadableDetails("source-diagram-description",
                ReadableContent.Text(ja ? "図の抽出データ・構造の説明" : "Extracted drawing data and structure"), descriptions));
            return retained;
        }
    }
}
