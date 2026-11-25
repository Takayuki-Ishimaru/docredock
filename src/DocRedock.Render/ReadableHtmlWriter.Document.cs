using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;

namespace DocRedock.Render;

public static partial class ReadableHtmlWriter
{
    /// <summary>Writes a native presentation without serializing or parsing Markdown.</summary>
    public static ReadableHtmlResult WriteDocument(ReadableDocument document,
        ReadableHtmlOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new HtmlDocumentWriter(options ?? new()).RunDocument(document, cancellationToken);
    }

    private sealed partial class HtmlDocumentWriter
    {
        private ReadableDocument _document = null!;
        private CancellationToken _typedCancellation;
        private WorkbookPresentationLayout? _workbook;
        private WorkbookSheetPresentation? _sheet;
        private string? _sourceSheetName;
        private bool _renderingPrintCopy;
        private readonly Dictionary<string, string> _styleClasses = new(StringComparer.Ordinal);
        private readonly StringBuilder _pageStyles = new();

        public ReadableHtmlResult RunDocument(ReadableDocument document, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _document = document; _typedCancellation = cancellationToken;
            foreach (var mode in new[] { "original", "light", "dark" }) _usedIds.Add("docredock-theme-" + mode);
            var language = _options.Language ?? document.Language;
            _ja = language.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
            language = _ja ? "ja" : "en";
            if (document.WorkbookLayout is { } raw)
            {
                var notices = new List<string>();
                WorkbookPresentationLayoutJson.TryRead(raw, out _workbook, notices);
                foreach (var notice in notices) _diagnostics.Add(new("HtmlWorkbookLayoutInvalid", notice));
            }
            var styleCss = SourceStyles();
            IndexCellTargets(document.Blocks);
            IndexSlideTargets(document.Blocks);
            foreach (var block in document.Blocks) TypedBlock(block, 0);
            var title = string.IsNullOrWhiteSpace(_options.Title) ? document.Title : _options.Title.Trim();
            if (string.IsNullOrWhiteSpace(title)) title = "DocRedock";
            var workbookControls = document.SourceFormat == DocumentFormatKind.Xlsx;
            var fixedPages = document.Blocks.Any(b=>b is ReadableOfficePages);
            var page = new StringBuilder(_body.Length + 8192);
            page.Append("<!doctype html>\n<html lang=\"").Append(language).Append("\"><head>\n<meta charset=\"utf-8\">\n")
                .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'");
            if (workbookControls) page.Append("; script-src '").Append(ReadableHtmlInteractions.CspHash).Append('\'');
            page.Append("\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            if (!string.IsNullOrWhiteSpace(_options.Generator)) page.Append("<meta name=\"generator\" content=\"").Append(Enc(_options.Generator)).Append("\">\n");
            page.Append("<title>").Append(Enc(title)).Append("</title><style>").Append(Css).Append(DirectLayoutCss).Append(WorksheetSceneCss).Append(OfficePageCss).Append(styleCss).Append(_pageStyles)
                .Append(workbookControls ? ReadableHtmlInteractions.Css : "")
                .Append("</style></head>\n<body class=\"direct-document").Append(workbookControls ? " workbook-document" : "").Append(fixedPages?" office-fixed-pages":"").Append("\">\n");
            var original = document.SourceFormat == DocumentFormatKind.Xlsx;
            foreach (var mode in new[] { "original", "light", "dark" })
                page.Append("<input class=\"theme-input\" type=\"radio\" name=\"docredock-theme\" id=\"docredock-theme-").Append(mode).Append('"')
                    .Append((original && mode == "original" || !original && mode == "light") ? " checked" : "").Append(">\n");
            page.Append("<div class=\"reader-shell\"><aside class=\"reader-nav\"><nav aria-label=\"").Append(_ja ? "目次" : "Contents").Append("\"><h2>")
                .Append(_ja ? "目次" : "Contents").Append("</h2><ol>");
            foreach (var heading in _headings.Where(h => h.Level is 2 or 3))
                page.Append("<li><a href=\"#").Append(Enc(heading.Id)).Append("\">").Append(Enc(heading.Text)).Append("</a></li>\n");
            page.Append("</ol></nav></aside><main class=\"reader-content\"><div class=\"reader-page\"><div class=\"reader-toolbar\">");
            foreach (var (mode, ja, en) in new[] { ("original", "元の配色", "Source colors"), ("light", "ライト", "Light"), ("dark", "ダーク", "Dark") })
                page.Append("<label for=\"docredock-theme-").Append(mode).Append("\">").Append(_ja ? ja : en).Append("</label>");
            page.Append("</div><h1 class=\"reader-title\">").Append(Enc(title)).Append("</h1>\n");
            if (!string.IsNullOrWhiteSpace(document.SourceName)) page.Append("<p class=\"source-reference reader-source\">").Append(Enc(document.SourceName)).Append("</p>\n");
            if (workbookControls) page.Append(ReadableHtmlInteractions.Controls(_ja));
            AppendConversionReport(page);
            page.Append(_body).Append("</div></main></div>");
            if (workbookControls) page.Append("<script>").Append(ReadableHtmlInteractions.Script).Append("</script>");
            page.Append("</body></html>\n");
            cancellationToken.ThrowIfCancellationRequested();
            return new(page.ToString(), _diagnostics) { EmbeddedImageCount = _embeddedCount, EmbeddedImageBytes = _embeddedBytes };
        }

        private void TypedBlock(ReadableBlock block, int depth)
        {
            _typedCancellation.ThrowIfCancellationRequested();
            if (depth > MaxDepth)
            { _diagnostics.Add(new("HtmlNestingLimit", "Nested content exceeded the readable layout depth limit.")); return; }
            var outsidePrint = block is not ReadableSection && OutsidePrintArea(block.Origin);
            if (outsidePrint) _body.Append("<div class=\"outside-print-area\">");
            switch (block)
            {
                case ReadableSection section:
                    var previous = _sheet;
                    var previousName = _sourceSheetName;
                    _sourceSheetName = section.Origin?.SheetName;
                    _sheet = section.Origin?.PartitionId is { } partition ? _workbook?.Sheets.GetValueOrDefault(partition) : null;
                    var id = _slideSectionIds.TryGetValue(section, out var slideSectionId) ? slideSectionId : ReserveId(section.Id);
                    _headings.Add(new(section.HeadingLevel, id, section.Title.PlainText));
                    var hasSourcePages = section.Blocks.Any(b => b is ReadableWorksheetScene);
                    _body.Append("<section class=\"source-section").Append(_sheet?.Name?.Contains("表紙", StringComparison.Ordinal) == true ? " sheet-cover" : "").Append(hasSourcePages ? " scene-section" : "")
                        .Append("\" id=\"").Append(Enc(id)).Append('"').Append(hasSourcePages ? "" : PrintPageAttributes()).Append(OriginAttributes(section.Origin)).Append('>');
                    TypedHeading(section.HeadingLevel, section.Title, section.Origin, addToContents: false);
                    foreach (var child in section.Blocks) TypedBlock(child, depth + 1);
                    _body.Append("</section>\n"); _sheet = previous; _sourceSheetName = previousName;
                    break;
                case ReadableHeading heading:
                    TypedHeading(heading.Level, heading.Content, heading.Origin);
                    break;
                case ReadableParagraph paragraph:
                    _body.Append("<p").Append(OriginAttributes(paragraph.Origin)).Append('>'); TypedRuns(paragraph.Content);
                    _body.Append("</p>\n"); break;
                case ReadableList list:
                    TypedList(list); break;
                case ReadableQuote quote:
                    _body.Append("<blockquote").Append(OriginAttributes(quote.Origin)).Append(">\n");
                    foreach (var child in quote.Blocks) TypedBlock(child, depth + 1);
                    _body.Append("</blockquote>\n"); break;
                case ReadableCode code:
                    _body.Append("<pre").Append(OriginAttributes(code.Origin)).Append("><code");
                    if (code.Language is { } codeLanguage)
                    {
                        var safe = new string(codeLanguage.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '+' or '#').Take(40).ToArray());
                        _body.Append(" class=\"language-").Append(safe).Append('"');
                    }
                    _body.Append('>').Append(Enc(code.Text)).Append("</code></pre>\n"); break;
                case ReadableDetails details:
                    _body.Append("<details data-kind=\"").Append(Enc(details.Kind)).Append('"').Append(OriginAttributes(details.Origin));
                    if (details.InitiallyOpen) _body.Append(" open");
                    _body.Append("><summary>"); TypedRuns(details.Summary); _body.Append("</summary>\n");
                    foreach (var child in details.Blocks) TypedBlock(child, depth + 1);
                    _body.Append("</details>\n"); break;
                case ReadablePageBreak:
                    _body.Append("<hr class=\"page-break\">\n"); break;
                case ReadableOfficePages officePages:
                    OfficePages(officePages); break;
                case ReadableSourceDrawingScene sourceScene:
                    SourceLabel(sourceScene.Origin);
                    _body.Append("<div class=\"source-diagram-scroll\"><div class=\"source-diagram-frame\" data-source-scene=\"")
                        .Append(Enc(sourceScene.Scene.Id)).Append("\" data-coordinate-space=\"").Append(Enc(sourceScene.Scene.CoordinateSpace))
                        .Append("\" style=\"width:").Append(Px(sourceScene.Scene.Width)).Append("px;height:")
                        .Append(Px(sourceScene.Scene.Height)).Append("px\">");
                    SceneBackground(sourceScene.Scene);
                    var shiftOrigin = sourceScene.Scene.OriginX != 0 || sourceScene.Scene.OriginY != 0;
                    if (shiftOrigin)
                        _body.Append("<div class=\"source-diagram-origin\" data-source-origin-x=\"").Append(Px(sourceScene.Scene.OriginX))
                            .Append("\" data-source-origin-y=\"").Append(Px(sourceScene.Scene.OriginY))
                            .Append("\" style=\"position:absolute;inset:0;transform:translate(").Append(Px(-sourceScene.Scene.OriginX)).Append("px,")
                            .Append(Px(-sourceScene.Scene.OriginY)).Append("px)\">");
                    foreach (var drawing in sourceScene.Scene.Objects)
                    {
                        // A table without a node (a slide layout's or master's) is held under the id of its drawing object.
                        if (drawing.Kind == "table" && sourceScene.ObjectBlocks.TryGetValue(drawing.SourceNodeId ?? "drawing:" + drawing.Id, out var tableBlock))
                        {
                            _body.Append("<div class=\"source-native-table\" style=\"position:absolute;left:0;top:0;transform-origin:0 0;width:")
                                .Append(Px(drawing.Width)).Append("px;height:").Append(Px(drawing.Height)).Append("px;transform:matrix(")
                                .Append(Matrix(drawing.Transform)).Append(")\">");
                            SceneTableStyles(tableBlock); TypedBlock(tableBlock, depth + 1); _body.Append("</div>");
                        }
                        else if (drawing.Kind == "picture" && drawing.SourceNodeId is null && sourceScene.Scene.CoordinateSpace == "slide") SharedScenePicture(drawing);
                        else SourceDrawing(drawing);
                    }
                    if (shiftOrigin) _body.Append("</div>");
                    _body.Append("</div></div>\n");
                    foreach (var chartDrawing in sourceScene.Scene.Objects.Where(o => o.Kind == "chart"))
                    {
                        if (chartDrawing.SourceNodeId is not {} chartId || !sourceScene.ChartData.TryGetValue(chartId, out var chartData)) continue;
                        var fallback = _sourceChartFallbacks.Contains(chartDrawing.Id);
                        _body.Append("<details data-kind=\"source-chart-data\" data-chart-fallback=\"").Append(fallback ? "true" : "false").Append('"');
                        if (fallback) _body.Append(" open");
                        _body.Append("><summary>").Append(_ja ? "グラフの保存済みデータ" : "Saved chart data").Append("</summary>");
                        foreach (var child in chartData) TypedBlock(child, depth + 1);
                        _body.Append("</details>\n");
                    }
                    break;
                case ReadableImage image:
                    SourceLabel(image.Origin);
                    _body.Append("<figure").Append(OriginAttributes(image.Origin)).Append('>');
                    EmitImage(_body, image.Reference, image.AltText, Dimension(image.WidthPixels), Dimension(image.HeightPixels),
                        preserveSourceAspect: true);
                    _body.Append("<figcaption>").Append(Enc(image.AltText)).Append("</figcaption></figure>\n"); break;
                case ReadableDiagram diagram:
                    SourceLabel(diagram.Origin);
                    _body.Append("<figure class=\"diagram\"").Append(OriginAttributes(diagram.Origin)).Append('>');
                    var svg = diagram.MermaidSource is { Length: > 0 } source ? SafeMermaidSvg(source) : null;
                    if (svg is not null) _body.Append(svg);
                    if (diagram.FallbackBlocks.Count == 0 && diagram.MermaidSource is { Length: > 0 } textSource)
                    {
                        var described = MermaidTooLarge(textSource) ? null : MermaidDiagramSvg.Render(textSource);
                        if (described is not null) AppendDiagramText(described);
                    }
                    foreach (var child in diagram.FallbackBlocks) TypedBlock(child, depth + 1);
                    _body.Append("<figcaption>").Append(Enc(diagram.AltText)).Append("</figcaption></figure>\n");
                    if (diagram.MermaidSource is { Length: > 0 } mermaid)
                        _body.Append("<details><summary>Mermaid</summary><pre><code class=\"language-mermaid\">").Append(Enc(mermaid)).Append("</code></pre></details>\n");
                    break;
                case ReadableWorksheetScene scene:
                    WorksheetScene(scene, depth); break;
                case ReadableTable table:
                    var copies = PrintTables(table);
                    if (copies.Count == 0) TypedTable(table, depth);
                    else
                    {
                        _body.Append("<div class=\"screen-copy\">"); TypedTable(table, depth); _body.Append("</div>");
                        var wasPrint = _renderingPrintCopy; _renderingPrintCopy = true;
                        foreach (var copy in copies) { _body.Append("<div class=\"print-copy\">"); TypedTable(copy, depth); _body.Append("</div>"); }
                        _renderingPrintCopy = wasPrint;
                    }
                    break;
                default:
                    _diagnostics.Add(new("HtmlBlockUnsupported", "A presentation block could not be laid out: " + block.GetType().Name));
                    break;
            }
            if (outsidePrint) _body.Append("</div>");
        }

        private void TypedHeading(int level, ReadableContent content, ReadableSourceOrigin? origin, bool addToContents = true)
        {
            var id = UniqueHeadingId(content.PlainText);
            level = Math.Clamp(level, 1, 6);
            if (addToContents) _headings.Add(new(level, id, content.PlainText));
            _body.Append("<h").Append(level).Append(" id=\"").Append(id).Append('"').Append(OriginAttributes(origin)).Append('>');
            TypedRuns(content); _body.Append("</h").Append(level).Append(">\n");
        }

        private void TypedList(ReadableList list)
        {
            var levels = new List<(int Level, bool Ordered)>();
            var openItem = false;
            foreach (var item in list.Items)
            {
                _typedCancellation.ThrowIfCancellationRequested();
                var level = Math.Clamp(item.Level, 0, 16);
                if (levels.Count == 0) { _body.Append(item.Ordered ? "<ol>" : "<ul>"); levels.Add((level, item.Ordered)); }
                else if (level > levels[^1].Level)
                { _body.Append(item.Ordered ? "<ol>" : "<ul>"); levels.Add((level, item.Ordered)); openItem = false; }
                else
                {
                    if (openItem) _body.Append("</li>");
                    while (levels.Count > 1 && level < levels[^1].Level)
                    { _body.Append(levels[^1].Ordered ? "</ol></li>" : "</ul></li>"); levels.RemoveAt(levels.Count - 1); }
                    if (levels[^1].Ordered != item.Ordered)
                    { _body.Append(levels[^1].Ordered ? "</ol>" : "</ul>"); _body.Append(item.Ordered ? "<ol>" : "<ul>"); levels[^1] = (level, item.Ordered); }
                }
                _body.Append("<li").Append(OriginAttributes(item.Origin));
                if (item.Ordered && item.Number is { } number) _body.Append(" value=\"").Append(number).Append('"');
                _body.Append('>'); TypedRuns(item.Content); openItem = true;
            }
            if (openItem) _body.Append("</li>");
            while (levels.Count > 0)
            { _body.Append(levels[^1].Ordered ? "</ol>" : "</ul>"); levels.RemoveAt(levels.Count - 1); if (levels.Count > 0) _body.Append("</li>"); }
            _body.Append('\n');
        }

        private void TypedTable(ReadableTable table, int depth)
        {
            SourceLabel(table.Origin);
            _body.Append("<div class=\"table-scroll\"").Append(OriginAttributes(table.Origin)).Append('>');
            var widths = table.Columns.Select(c => c.WidthPixels is > 0 and <= 4096 and var w ? w : 96d).ToArray();
            var total = Math.Max(1, widths.Sum());
            var columnIndexes = table.Columns.Select((c, i) => (c.SourceIndex, Index: i)).GroupBy(c => c.SourceIndex).ToDictionary(g => g.Key, g => g.First().Index);
            var leftOffsets = new double[widths.Length + 1];
            for (var i = 0; i < widths.Length; i++) leftOffsets[i + 1] = leftOffsets[i] + widths[i];
            _body.Append("<table style=\"width:").Append(total.ToString("0.##", CultureInfo.InvariantCulture)).Append("px\"><colgroup>");
            foreach (var width in widths) _body.Append("<col style=\"width:").Append((100 * width / total).ToString("0.####", CultureInfo.InvariantCulture)).Append("%\">");
            _body.Append("</colgroup>\n");
            var headers = Math.Clamp(table.HeaderRowCount, 0, table.Rows.Count);
            for (var r = 0; r < headers; r++) headers = Math.Min(table.Rows.Count, Math.Max(headers, r + table.Rows[r].Cells.Select(c => Math.Clamp(c.RowSpan, 1, table.Rows.Count - r)).DefaultIfEmpty(1).Max()));
            if (headers > 0) _body.Append("<thead>");
            for (var r = 0; r < table.Rows.Count; r++)
            {
                _typedCancellation.ThrowIfCancellationRequested();
                if (r == headers) { if (headers > 0) _body.Append("</thead>"); _body.Append("<tbody>"); }
                var row = table.Rows[r];
                _body.Append("<tr");
                if (row.SourceRow is { } rowNumber && r > 0 && _sheet?.Print?.RowBreaks?.Any(b => b >= (table.Rows[r - 1].SourceRow ?? rowNumber) && b < rowNumber) == true)
                    _body.Append(" class=\"manual-page-break\"");
                if (row.SourceRow is { } sourceRow) _body.Append(" data-source-row=\"").Append(sourceRow).Append('"');
                if (Dimension(row.HeightPixels) is { } height) _body.Append(" style=\"height:").Append(height).Append("px\"");
                _body.Append('>');
                var gridColumn = 0;
                foreach (var cell in row.Cells)
                {
                    _typedCancellation.ThrowIfCancellationRequested();
                    if (cell.RowSpan == 0) { gridColumn += Math.Max(1, cell.ColSpan); continue; }
                    if (cell.SourceColumn is { } sourceColumn)
                    { if (columnIndexes.TryGetValue(sourceColumn, out var index)) gridColumn = index; }
                    var header = r < headers; var tag = header ? "th" : "td";
                    _body.Append('<').Append(tag);
                    if (header) _body.Append(cell.ColSpan > 1 ? " scope=\"colgroup\"" : " scope=\"col\"");
                    var rowspan = Math.Clamp(cell.RowSpan, 1, table.Rows.Count - r);
                    var colspan = Math.Clamp(cell.ColSpan, 1, Math.Max(1, table.Columns.Count - gridColumn));
                    if (rowspan > 1) _body.Append(" rowspan=\"").Append(rowspan).Append('"');
                    if (colspan > 1) _body.Append(" colspan=\"").Append(colspan).Append('"');
                    if (cell.SourceColumn is { } column) _body.Append(" data-source-column=\"").Append(column).Append('"');
                    if (!_renderingPrintCopy && _sourceSheetName is { } sheetName && row.SourceRow is { } sourceRowNumber && cell.SourceColumn is { } sourceColumnNumber)
                        _body.Append(" id=\"").Append(WorkbookCellLinks.Id(sheetName, sourceRowNumber, sourceColumnNumber)).Append('"');
                    var classes = new List<string>();
                    if (cell.IsNumeric) classes.Add("numeric-cell");
                    if (gridColumn < widths.Length && widths[gridColumn] <= 48) classes.Add("narrow-column");
                    if (cell.StyleId is { } styleId && _styleClasses.TryGetValue(styleId, out var styleClass)) classes.Add(styleClass);
                    var frozen = _sheet?.FrozenColumns is { } frozenColumns && cell.SourceColumn is { } startColumn && startColumn <= frozenColumns &&
                        gridColumn + colspan <= table.Columns.Count && table.Columns[gridColumn + colspan - 1].SourceIndex <= frozenColumns;
                    if (frozen) classes.Add("frozen-column");
                    if (classes.Count > 0) _body.Append(" class=\"").Append(string.Join(' ', classes)).Append('"');
                    if (frozen) _body.Append(" style=\"--frozen-left:").Append(leftOffsets[Math.Clamp(gridColumn, 0, widths.Length)].ToString("0.##", CultureInfo.InvariantCulture)).Append("px\"");
                    _body.Append(OriginAttributes(cell.Origin)).Append('>');
                    if (cell.Formula is { } formula)
                    {
                        if (_options.ShowFormulas && !string.IsNullOrWhiteSpace(formula.Expression))
                        { _body.Append("<code>").Append(Enc(formula.Expression.StartsWith('=') ? formula.Expression : "=" + formula.Expression)).Append("</code> → "); }
                        if (formula.HasCachedValue) TypedRuns(cell.Content);
                        else _body.Append("<span class=\"formula-missing\">").Append(_ja ? "保存済み計算値なし" : "No cached value").Append("</span>");
                    }
                    else TypedRuns(cell.Content);
                    // break-after does not apply inside a table cell: the break of a cell paragraph is a visual marker only.
                    foreach (var child in cell.Children)
                        if (child is ReadablePageBreak) _body.Append("<hr class=\"page-break in-cell\">");
                        else TypedBlock(child, depth + 1);
                    _body.Append("</").Append(tag).Append('>'); gridColumn += colspan;
                }
                _body.Append("</tr>\n");
            }
            if (table.Rows.Count > 0 && headers == table.Rows.Count) _body.Append("</thead><tbody>");
            _body.Append("</tbody></table></div>\n");
        }

        private void TypedRuns(ReadableContent content)
        {
            // A paragraph of a table cell inside a source scene that is aligned or indented on its own: open until the next one or the end.
            var paragraphOpen = false; var paragraphStart = 0;
            void CloseParagraph()
            {
                if (!paragraphOpen) return;
                // An empty paragraph keeps its line.
                if (_body.Length == paragraphStart) _body.Append("<br>");
                _body.Append("</div>"); paragraphOpen = false;
            }
            foreach (var run in content.Runs)
            {
                _typedCancellation.ThrowIfCancellationRequested();
                if (run.StyleId is { } paragraphStyle && paragraphStyle.StartsWith("scene-table-par-", StringComparison.Ordinal) && run.Text is "" or "\n" &&
                    _styleClasses.TryGetValue(paragraphStyle, out var paragraphClass))
                {
                    CloseParagraph();
                    _body.Append("<div class=\"source-cell-paragraph ").Append(paragraphClass).Append("\">");
                    paragraphOpen = true; paragraphStart = _body.Length; continue;
                }
                var text = Enc(run.Text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);
                if (run.Kind == TextRunKind.LineBreak) { _body.Append("<br>"); continue; }
                if (run.Kind == TextRunKind.Tab) { _body.Append("&#9;"); continue; }
                if (run.Code) text = "<code>" + text + "</code>";
                if (run.Bold) text = "<strong>" + text + "</strong>";
                if (run.Italic) text = "<em>" + text + "</em>";
                if (run.Underline) text = "<u>" + text + "</u>";
                if (run.Strike) text = "<s>" + text + "</s>";
                if (run.Inserted) text = "<ins>" + text + "</ins>";
                if (run.Deleted) text = "<del>" + text + "</del>";
                if (run.HighlightColor is { Length: > 0 } highlight)
                    text = HighlightColor(highlight) is { } highlightColor
                        ? "<mark style=\"background-color:" + highlightColor + "\">" + text + "</mark>" : "<mark>" + text + "</mark>";
                // A run of a table inside a source scene that is formatted differently from its cell.
                if (run.StyleId is { } runStyle && runStyle.StartsWith("scene-table-run-", StringComparison.Ordinal) && _styleClasses.TryGetValue(runStyle, out var runClass))
                    text = "<span class=\"" + runClass + "\">" + text + "</span>";
                if (run.Color is { } color && NormalizeColor(color) is { } normalized)
                {
                    var contrast = _document.SourceFormat == DocumentFormatKind.Xlsx ? null : LowContrastClass(normalized[1..]) switch
                    { "fg-light" => "source-low-light", "fg-dark" => "source-low-dark", _ => null };
                    text = "<span" + (contrast is null ? "" : " class=\"" + contrast + "\"") + " style=\"color:" + normalized + "\">" + text + "</span>";
                }
                if (run.LinkTarget is { } target)
                {
                    if (TrySlideLink(target, out var slideLink)) target = slideLink ?? "";
                    if (target.StartsWith('#') && _sourceSheetName is { } currentSheet && !target.StartsWith("#docredock-", StringComparison.Ordinal))
                    {
                        if (WorkbookLink(target, currentSheet) is { } destination) target = destination;
                        else { _diagnostics.Add(new("HtmlWorkbookLinkUnavailable", "An internal workbook link has an unsupported or excluded destination; its label is retained.")); target = ""; }
                    }
                    if (target.StartsWith('#') && !target.Any(char.IsControl) || IsSafeAbsoluteLink(target))
                        text = "<a href=\"" + Enc(target) + "\">" + text + "</a>";
                }
                _body.Append(text);
            }
            CloseParagraph();
        }

        private void SourceLabel(ReadableSourceOrigin? origin)
        {
            if (origin is null || origin.SheetName is null && origin.Range is null) return;
            _body.Append("<p class=\"source-reference\">").Append(Enc(string.Join(" / ", new[] { _document.SourceName, origin.SheetName, origin.Range, origin.ObjectId }.OfType<string>()))).Append("</p>");
        }

        private void AppendDiagramText(string svg)
        {
            try
            {
                using var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(svg), new System.Xml.XmlReaderSettings
                { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
                var document = System.Xml.Linq.XDocument.Load(reader);
                var descriptions = document.Descendants().Where(e => e.Name.LocalName == "desc" &&
                    (string?)e.Attribute("class") == "docredock-connection").Select(e => e.Value).ToArray();
                var labels = descriptions.Length > 0 ? descriptions : document.Descendants()
                    .Where(e => e.Name.LocalName == "text").Select(e => e.Value).ToArray();
                if (labels.Length == 0) return;
                _body.Append("<div class=\"diagram-text\"><ul>");
                foreach (var text in labels.Distinct(StringComparer.Ordinal)) _body.Append("<li>").Append(Enc(text)).Append("</li>");
                _body.Append("</ul></div>");
            }
            catch (System.Xml.XmlException) { _diagnostics.Add(new("HtmlDiagramTextUnavailable", "Diagram text could not be read; compare the source.")); }
        }

        private string OriginAttributes(ReadableSourceOrigin? origin)
        {
            if (origin is null) return "";
            var output = new StringBuilder();
            void Attr(string key, string? value) { if (!string.IsNullOrEmpty(value)) output.Append(" data-source-").Append(key).Append("=\"").Append(Enc(value)).Append('"'); }
            Attr("workbook", _document.SourceName); Attr("partition", origin.PartitionId); Attr("sheet", origin.SheetName);
            Attr("range", origin.Range); Attr("object-id", origin.ObjectId); Attr("part", origin.Anchor?.PartUri);
            if (origin.NodeIds is { Count: > 0 }) Attr("node-ids", string.Join(' ', origin.NodeIds));
            return output.ToString();
        }

        private string SourceStyles()
        {
            var output = new StringBuilder();
            foreach (var style in (_document.Styles ?? new Dictionary<string, StyleDescriptor>()).OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                _typedCancellation.ThrowIfCancellationRequested();
                var name = "source-style-" + _styleClasses.Count.ToString(CultureInfo.InvariantCulture);
                _styleClasses[style.Key] = name;
                var declarations = new StringBuilder();
                foreach (var (key, value) in style.Value.Properties ?? new Dictionary<string, string>())
                    if (SafeStyle(key, value)) declarations.Append(key).Append(':').Append(value).Append(';');
                if (declarations.Length > 0) output.Append(".direct-document table .").Append(name).Append(",.direct-document .source-grid-cell.").Append(name).Append('{').Append(declarations).Append("}\n");
            }
            return output.ToString();
        }

        private static bool SafeStyle(string property, string value) => property switch
        {
            "color" => HexColorValue(value),
            "background-color" => HexColorValue(value) || value == "transparent",
            "font-weight" => value is "bold" or "normal" or "600" or "700",
            "font-style" => value is "normal" or "italic",
            "text-align" => value is "left" or "right" or "center" or "justify",
            "vertical-align" => value is "top" or "middle" or "bottom",
            "white-space" => value is "pre-wrap" or "normal",
            // A paragraph of a table cell inside a source scene: its left margin and first-line indent.
            "padding-left" or "text-indent" => value.Length <= 12 && Regex.IsMatch(value, @"^\d{1,4}(?:[.]\d{1,2})?px$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            "font-size" => value.EndsWith("pt", StringComparison.Ordinal) && double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n is > 0 and <= 4096,
            "border-left" or "border-right" or "border-top" or "border-bottom" => value == "none" || value.Length < 40 && Regex.IsMatch(value, @"^\d{1,2}(?:[.]\d{1,2})?px (?:solid|double|dotted|dashed) #[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            // Quoted family names without a character that could end the string, the declaration or the style element.
            "font-family" => value.Length <= 1024 && Regex.IsMatch(value, @"^(?:""[^""\\;{}<>\p{Cc}]{1,128}"",){0,8}sans-serif$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            // Up to three gradients of colors at percentages or around the middle, or the 8x8 bitmap of a pattern: a cell's diagonals and its gradient or pattern fill.
            "background-image" => value.Length <= 4096 && Regex.IsMatch(value, SafeGradients, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            _ => false,
        };
        private const string SafeGradient = @"(?:linear-gradient\((?:\d{1,3}(?:[.]\d{1,2})?deg|to (?:top|bottom) right)|radial-gradient\(ellipse farthest-corner at -?\d{1,4}(?:[.]\d{1,2})?% -?\d{1,4}(?:[.]\d{1,2})?%)" +
            @"(?:,(?:transparent|#[0-9A-Fa-f]{6}|rgba\(\d{1,3},\d{1,3},\d{1,3},[01](?:[.]\d{1,3})?\)) (?:\d{1,3}(?:[.]\d{1,2})?%|calc\(50% [-+] \d{1,2}(?:[.]\d{1,2})?px\))){2,32}\)";
        // A preset pattern exactly as the projection writes it: an SVG picture of one rectangle and one path of bitmap rows, with no other markup.
        private const string SafePattern = @"url\(""data:image/svg\+xml,%3Csvg xmlns='http://www\.w3\.org/2000/svg' width='8' height='8' shape-rendering='crispEdges'%3E" +
            @"%3Crect width='8' height='8' fill='%23[0-9A-Fa-f]{6}' fill-opacity='[01](?:[.]\d{1,3})?'/%3E" +
            @"%3Cpath d='(?:M\d \dh\dv1h-\dz){0,64}' fill='%23[0-9A-Fa-f]{6}' fill-opacity='[01](?:[.]\d{1,3})?'/%3E%3C/svg%3E""\)";
        private const string SafeGradients = "^(?:" + SafeGradient + "|" + SafePattern + ")(?:,(?:" + SafeGradient + "|" + SafePattern + ")){0,2}$";
        private static bool HexColorValue(string color) => color.Length == 7 && color[0] == '#' && color.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
        private static string? NormalizeColor(string color) => HexColorValue(color) ? color : color.Length == 6 && HexColorValue("#" + color) ? "#" + color : null;
        private static string? HighlightColor(string color) => NormalizeColor(color) ?? color.ToLowerInvariant() switch
        {
            "yellow" => "#FFFF00", "green" => "#00FF00", "cyan" => "#00FFFF", "magenta" => "#FF00FF",
            "blue" => "#0000FF", "red" => "#FF0000", "darkblue" => "#000080", "darkcyan" => "#008080",
            "darkgreen" => "#008000", "darkmagenta" => "#800080", "darkred" => "#800000",
            "darkyellow" => "#808000", "darkgray" => "#808080", "lightgray" => "#C0C0C0",
            "black" => "#000000", "white" => "#FFFFFF", _ => null,
        };
        private static string? Dimension(double? dimension) => dimension is >= 1 and <= 8192 and var n ? n.ToString("0.##", CultureInfo.InvariantCulture) : null;

        private void AppendConversionReport(StringBuilder page)
        {
            var notices = _document.Notices.Concat(_diagnostics.Select(d => new ReadableNotice(d.Code, d.Message))).DistinctBy(n => (n.Code, n.Message)).ToArray();
            if (notices.Length == 0) return;
            page.Append("<details class=\"conversion-report\"><summary>").Append(_ja ? "変換時の確認事項" : "Conversion review").Append(" (").Append(notices.Length).Append(")</summary><ul>");
            foreach (var notice in notices) page.Append("<li><strong>").Append(Enc(notice.Code)).Append("</strong>: ").Append(Enc(notice.Message)).Append("</li>");
            page.Append("</ul></details>\n");
        }
    }
}
