using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Xlsx;

namespace DocRedock.Tests.Xlsx;

/// <summary>
/// A merge or a drawing can span (nearly) the whole sheet: a merge over A1:XFD1048575, over a whole row or a
/// whole column, a drawing anchored from A1 to XFD1048576, a line drawn down to the last row. Anything the
/// conversion does with such an area (finding the cells a drawing covers, resolving a merge to its origin cell,
/// deciding whether a shape sits on a table, folding its marker into the cells) has to cost what the cells that
/// hold something cost, not the 17 billion cells of the area. Each workbook here is converted under a time
/// limit, so a regression fails by name instead of keeping the suite waiting.
/// </summary>
public sealed class ExcelHugeAreaTests : IDisposable
{
    // The conversions take well under a second; walking the cells of these areas takes hours (and gigabytes).
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private readonly string root = Directory.CreateTempSubdirectory("docredock-huge-area-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);

    public void Dispose()
    {
        try { Directory.Delete(root, true); }
        catch (IOException) { /* a conversion that outlived its test (a regression) may still hold a file */ }
    }

    private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Red = "<a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill>";
    private const string Blue = "<a:solidFill><a:srgbClr val=\"0000FF\"/></a:solidFill>";

    // Cell styles: 0 plain, 1 bold, 2 top aligned. 24 pt rows and 11 pt text make a line's place against the text known:
    // bottom aligned text has its baseline 20.37 pt below the top of its row, top aligned text 10.45 pt below it, a
    // strikethrough lies 0.15-0.5 em above the baseline (and a little more for the estimate), an overline above that.
    private const string Styles =
        "<fonts><font><sz val=\"11\"/></font><font><b/><sz val=\"11\"/></font></fonts>" +
        "<cellXfs><xf fontId=\"0\"/><xf fontId=\"1\"/><xf fontId=\"0\"><alignment vertical=\"top\"/></xf></cellXfs>";

    private const string ItemColumns = "<cols><col min=\"1\" max=\"1\" width=\"24\" customWidth=\"1\"/><col min=\"2\" max=\"3\" width=\"12\" customWidth=\"1\"/></cols>";

    // ---- workbooks ----------------------------------------------------------------------------------------

    private static string Text(string reference, string text, int style = 0) =>
        $"<c r=\"{reference}\" s=\"{style}\" t=\"inlineStr\"><is><t>{text}</t></is></c>";

    private static string Number(string reference, string value) => $"<c r=\"{reference}\"><v>{value}</v></c>";

    private static string Blank(string reference) => $"<c r=\"{reference}\"/>";

    private static string Row(int number, params string[] cells) => $"<row r=\"{number}\" ht=\"24\" customHeight=\"1\">{string.Concat(cells)}</row>";

    private static string Merge(params string[] ranges) =>
        $"<mergeCells count=\"{ranges.Length}\">{string.Concat(ranges.Select(range => $"<mergeCell ref=\"{range}\"/>"))}</mergeCells>";

    private string Workbook(string name, string sheetData, string drawing = "", string mergeCells = "")
    {
        var path = Path.Combine(root, name + ".xlsx");
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
            ["xl/workbook.xml"] = $"<workbook xmlns=\"{SpreadsheetNs}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Items\" sheetId=\"1\" r:id=\"rId1\" /></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"worksheet\" Target=\"worksheets/sheet1.xml\" /></Relationships>",
            ["xl/styles.xml"] = $"<styleSheet xmlns=\"{SpreadsheetNs}\">{Styles}</styleSheet>",
            ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"{SpreadsheetNs}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">{ItemColumns}<sheetData>{sheetData}</sheetData>{mergeCells}{(drawing.Length == 0 ? "" : "<drawing r:id=\"rDrawing\" />")}</worksheet>",
        };
        if (drawing.Length > 0)
        {
            parts["xl/worksheets/_rels/sheet1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rDrawing\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"/xl/drawings/drawing1.xml\" /></Relationships>";
            parts["xl/drawings/drawing1.xml"] = $"<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">{drawing}</xdr:wsDr>";
        }
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
        return path;
    }

    // ---- drawings -----------------------------------------------------------------------------------------

    private static long Emu(double points) => (long)Math.Round(points * 12700);

    /// <summary>A straight line of the given length starting y points below the top left corner of A1.</summary>
    private static string LineOnA1(int id, double y, double length, string outline = Red) =>
        $"<xdr:oneCellAnchor><xdr:from><xdr:col>0</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>0</xdr:row><xdr:rowOff>{Emu(y)}</xdr:rowOff></xdr:from><xdr:ext cx=\"{Emu(length)}\" cy=\"0\"/>" +
        $"<xdr:cxnSp macro=\"\"><xdr:nvCxnSpPr><xdr:cNvPr id=\"{id}\" name=\"Line {id}\"/><xdr:cNvCxnSpPr/></xdr:nvCxnSpPr><xdr:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Emu(length)}\" cy=\"0\"/></a:xfrm>" +
        $"<a:prstGeom prst=\"line\"><a:avLst/></a:prstGeom><a:ln w=\"19050\">{outline}</a:ln></xdr:spPr></xdr:cxnSp><xdr:clientData/></xdr:oneCellAnchor>";

    /// <summary>A filled rectangle (a bar, when it has no text) from the top left corner of one cell to the bottom right corner of another,
    /// both zero-based (a default column is 561975 EMU wide and a default row 190500 EMU high).</summary>
    private static string Bar(int id, int fromColumn, int fromRow, int toColumn, int toRow, string textBox = "", string text = "") =>
        $"<xdr:twoCellAnchor><xdr:from><xdr:col>{fromColumn}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>{fromRow}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>" +
        $"<xdr:to><xdr:col>{toColumn}</xdr:col><xdr:colOff>561975</xdr:colOff><xdr:row>{toRow}</xdr:row><xdr:rowOff>190500</xdr:rowOff></xdr:to>" +
        $"<xdr:sp macro=\"\" textlink=\"\"><xdr:nvSpPr><xdr:cNvPr id=\"{id}\" name=\"Shape {id}\"/><xdr:cNvSpPr{textBox}/></xdr:nvSpPr><xdr:spPr><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" +
        "<a:solidFill><a:srgbClr val=\"4472C4\"/></a:solidFill></xdr:spPr>" +
        (text.Length == 0 ? "" : $"<xdr:txBody><a:bodyPr/><a:p><a:r><a:t>{text}</a:t></a:r></a:p></xdr:txBody>") +
        "</xdr:sp><xdr:clientData/></xdr:twoCellAnchor>";

    /// <summary>A vertical line with an arrowhead at its lower end, in column C, from the top of the first row to the bottom of the last row of the sheet (a default row is 190500 EMU high).</summary>
    private static string LineToTheLastRow(int id) =>
        "<xdr:twoCellAnchor><xdr:from><xdr:col>2</xdr:col><xdr:colOff>300000</xdr:colOff><xdr:row>0</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>" +
        "<xdr:to><xdr:col>2</xdr:col><xdr:colOff>300000</xdr:colOff><xdr:row>1048575</xdr:row><xdr:rowOff>190500</xdr:rowOff></xdr:to>" +
        $"<xdr:cxnSp macro=\"\"><xdr:nvCxnSpPr><xdr:cNvPr id=\"{id}\" name=\"Today\"/><xdr:cNvCxnSpPr/></xdr:nvCxnSpPr><xdr:spPr><a:prstGeom prst=\"line\"><a:avLst/></a:prstGeom>" +
        $"<a:ln w=\"19050\">{Red}<a:tailEnd type=\"triangle\"/></a:ln></xdr:spPr></xdr:cxnSp><xdr:clientData/></xdr:twoCellAnchor>";

    // ---- the sheets under test ----------------------------------------------------------------------------

    private static readonly string TwoItems =
        Row(1, Text("A1", "Whole sheet merged text"), Text("B1", "x")) + Row(2, Text("A2", "Item"), Text("B2", "Qty"));

    // A schedule: dates across the first row, a task name at the start of each row, and the days left blank for bars to be drawn on.
    private static readonly string Schedule =
        Row(1, Blank("A1"), Text("B1", "Mon", 1), Text("C1", "Tue", 1), Text("D1", "Wed", 1)) +
        Row(2, Text("A2", "Design"), Blank("B2"), Blank("C2"), Blank("D2")) +
        Row(3, Text("A3", "Build"), Blank("B3"), Blank("C3"), Blank("D3"));

    // ---- running a conversion under the limit -------------------------------------------------------------

    private async Task<(string Markdown, ReadableDocumentExportResult Result)> ConvertAsync(string source)
    {
        var output = Path.ChangeExtension(source, ".md");
        // The token only stops a conversion that is slow but gets to a check; a loop over the cells of the area never does, so the
        // test stops waiting for it at the limit, a little before the token.
        using var cancellation = new CancellationTokenSource(Limit + TimeSpan.FromSeconds(5));
        var conversion = Task.Run(() => service.ExportReadableAsync(new ReadableDocumentExportOptions(source, output, EnableOcr: false), cancellation.Token));
        try
        {
            var result = await conversion.WaitAsync(Limit);
            var markdown = (await File.ReadAllTextAsync(output)).Replace("\r\n", "\n", StringComparison.Ordinal);
            var expectedTitle = $"# {Path.GetFileNameWithoutExtension(source)}\n";
            Assert.StartsWith(expectedTitle, markdown, StringComparison.Ordinal);
            // Fixture names differ when comparing equivalent sheets; normalize only the checked H1.
            return ("# ドキュメント\n" + markdown[expectedTitle.Length..], result);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"{Path.GetFileName(source)} was still being converted after {Limit.TotalSeconds:0} seconds: something walks the cells of a merge or a drawing that spans the sheet.");
            throw;
        }
    }

    private static XlsxExtractionResult Extract(string source)
    {
        using var stream = File.OpenRead(source);
        return new XlsxAdapter().Extract(stream);
    }

    // Bytes the extraction of a workbook allocates, counted on the thread that does it. Looking up each cell of a merge costs an
    // entry of a few dozen bytes per cell: gigabytes for the merges below, and the extraction itself needs a few megabytes. Unlike
    // the time it takes, this does not depend on how busy the machine is.
    private static async Task<long> AllocatedByExtraction(string source)
    {
        var allocation = Task.Run(() =>
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            Extract(source);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        });
        try { return await allocation.WaitAsync(Limit); }
        catch (TimeoutException)
        {
            Assert.Fail($"{Path.GetFileName(source)} was still being read after {Limit.TotalSeconds:0} seconds: something walks the cells of a merge that spans the sheet.");
            throw;
        }
    }

    private const long Megabyte = 1024 * 1024;

    private static XlsxSheetOverlay SingleOverlay(XlsxExtractionResult extraction)
    {
        var node = Assert.Single(extraction.Graph.Nodes, item => item.Kind == NodeKind.Shape && item.Extensions!.ContainsKey("sheet_overlay"));
        return node.Extensions!["sheet_overlay"].Deserialize<XlsxSheetOverlay>()!;
    }

    // ---- merges -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_merge_over_nearly_the_whole_sheet_and_a_drawn_line_convert_like_the_merge_alone()
    {
        // A1:XFD1048575 is merged, its text is bottom aligned (so at the bottom of the merge, a million rows down), and the line
        // 17 pt below the top of the sheet is nowhere near it: the line stays a drawing, and the sheet's shapes are looked
        // at for overlays, which has to resolve every cell of the merge to its origin.
        const string expected =
            "# ドキュメント\n\n" +
            "## Items\n\n" +
            "<!-- inferred: セル配置から文書情報セクションを推定 -->\n" +
            "### 文書情報\n\n" +
            "<!-- inferred: キー・値の配置を文書情報として分離 -->\n" +
            "- **Whole sheet merged text**: x\n" +
            "- **Item**: Qty\n";
        var merge = Merge("A1:XFD1048575");

        var alone = await ConvertAsync(Workbook("sheet-merge-alone", TwoItems, mergeCells: merge));
        var withLine = await ConvertAsync(Workbook("sheet-merge-with-line", TwoItems, LineOnA1(2, 17, 44), merge));

        Assert.Equal(expected, alone.Markdown);
        Assert.Equal(expected, withLine.Markdown);
        // The line was looked at and left as a drawing.
        Assert.Contains(withLine.Result.Diagnostics, diagnostic => diagnostic.Message.Contains("1 DrawingML shape(s) were retained", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_whole_row_merge_with_a_strike_line_and_an_overline_converts_quickly()
    {
        // The strike line across A1's text is formatting and strikes it; the blue line above the text stays a drawing.
        var source = Workbook("row-merge",
            Row(1, Text("A1", "Whole row merged text")) + Row(2, Text("A2", "Item"), Text("B2", "Qty")),
            LineOnA1(2, 17, 170) + LineOnA1(3, 5.5, 44, Blue),
            Merge("A1:XFD1"));

        var (markdown, result) = await ConvertAsync(source);

        Assert.Equal("# ドキュメント\n\n## Items\n\n### ~~Whole row merged text~~\n\n- Item — Qty\n", markdown);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("1 DrawingML shape(s) were retained", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_whole_column_merge_with_a_strike_line_and_an_overline_converts_quickly()
    {
        // A1:A1048575 is merged and its text is top aligned, so the strike line is on the text; as the merge is a million rows
        // tall, only its first rows are looked at for lines, and the overline above the text stays a drawing.
        var source = Workbook("column-merge",
            Row(1, Text("A1", "Whole column merged text", 2), Text("B1", "x")) + Row(2, Text("B2", "Item"), Text("C2", "Qty")),
            LineOnA1(2, 8.7, 126) + LineOnA1(3, 2, 44, Blue),
            Merge("A1:A1048575"));

        var (markdown, result) = await ConvertAsync(source);

        Assert.Equal(
            "# ドキュメント\n\n## Items\n\n" +
            "| ~~Whole column merged text~~ | x |  |\n" +
            "| --- | --- | --- |\n" +
            "|  | Item | Qty |\n", markdown);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("1 DrawingML shape(s) were retained", StringComparison.Ordinal));
        // A lookup entry for each of the million cells of the merge would take over 30 MB.
        Assert.InRange(await AllocatedByExtraction(source), 0, 20 * Megabyte);
    }

    [Fact]
    public async Task Merges_over_the_whole_width_of_a_thousand_rows_and_a_line_convert_quickly()
    {
        // A form whose every row is a banner merged across all 16,384 columns: 16 million cells in all (over 1 GB of lookup entries).
        // The line above the first banner stays a drawing.
        var banners = Enumerable.Range(1, 1000).ToArray();
        var source = Workbook("banner-rows",
            string.Concat(banners.Select(number => Row(number, Text($"A{number}", $"Banner {number}")))),
            LineOnA1(2, 5.5, 44, Blue),
            Merge(banners.Select(number => $"A{number}:XFD{number}").ToArray()));

        var (markdown, result) = await ConvertAsync(source);

        Assert.Equal("# ドキュメント\n\n## Items\n\n### Banner 1\n\n" + string.Join("\n\n", banners.Skip(1).Select(number => $"Banner {number}")) + "\n", markdown);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("1 DrawingML shape(s) were retained", StringComparison.Ordinal));
        Assert.InRange(await AllocatedByExtraction(source), 0, 100 * Megabyte);
    }

    [Fact]
    public async Task Merges_down_the_whole_height_of_twenty_five_columns_and_a_line_convert_quickly()
    {
        // Twenty-five columns each merged over 1,048,575 rows: 26 million cells in all (over 1 GB of lookup entries).
        static string ColumnName(int column) => ((char)('A' + column - 1)).ToString();
        var columns = Enumerable.Range(1, 25).ToArray();
        var source = Workbook("full-height-columns",
            Row(1, columns.Select(column => Text($"{ColumnName(column)}1", $"Col {column}")).ToArray()),
            LineOnA1(2, 5.5, 44, Blue),
            Merge(columns.Select(column => $"{ColumnName(column)}1:{ColumnName(column)}1048575").ToArray()));

        var (markdown, result) = await ConvertAsync(source);

        Assert.Equal("# ドキュメント\n\n## Items\n\n- " + string.Join(" — ", columns.Select(column => $"Col {column}")) + "\n", markdown);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("1 DrawingML shape(s) were retained", StringComparison.Ordinal));
        Assert.InRange(await AllocatedByExtraction(source), 0, 20 * Megabyte);
    }

    [Fact]
    public async Task A_bar_over_a_merge_that_spans_the_sheet_collapses_onto_the_merges_origin()
    {
        // D2:XFD1048576 is one merge, so every cell of the bar below that lies in it is the origin D2: the bar, drawn from B2 to the
        // last cell of the sheet, reaches columns B to D (the merge adds none beyond its origin) and down to row 1048576.
        var source = Workbook("bar-over-merge", Schedule, Bar(2, 1, 1, 16383, 1048575), Merge("D2:XFD1048576"));

        var (markdown, _) = await ConvertAsync(source);
        var overlay = SingleOverlay(Extract(source));

        Assert.Equal((2, 1048576, 2, 4), (overlay.StartRow, overlay.EndRow, overlay.StartColumn, overlay.EndColumn));
        Assert.Equal(
            "# ドキュメント\n\n## Items\n\n" +
            "|  | Mon | Tue | Wed |\n" +
            "| --- | --- | --- | --- |\n" +
            "| Design | ━━ | ━━ | ━━ |\n" +
            "| Build | ━━ | ━━ | ━━ |\n", markdown);
    }

    // ---- drawings -----------------------------------------------------------------------------------------

    private static readonly string ItemTable =
        Row(1, Text("A1", "Item", 1), Text("B1", "Qty", 1), Text("C1", "Price", 1)) +
        Row(2, Text("A2", "Apple"), Number("B2", "10"), Number("C2", "120")) +
        Row(3, Text("A3", "Banana"), Number("B3", "20"), Number("C3", "80"));

    private const string ItemTableMarkdown =
        "# ドキュメント\n\n## Items\n\n" +
        "| Item | Qty | Price |\n" +
        "| --- | --- | --- |\n" +
        "| Apple | 10 | 120 |\n" +
        "| Banana | 20 | 80 |\n";

    [Fact]
    public async Task A_drawing_anchored_from_A1_to_XFD1048576_converts_quickly()
    {
        // Nothing lies to the left of a shape that starts in column A, so it is no overlay on the table: the table is as it is without it.
        var source = Workbook("anchor-whole-sheet", ItemTable, Bar(2, 0, 0, 16383, 1048575));

        var (markdown, result) = await ConvertAsync(source);

        Assert.Equal(ItemTableMarkdown, markdown);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("1 DrawingML shape(s) were retained", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_drawing_over_the_sheet_converts_quickly_when_the_file_records_twenty_thousand_rows()
    {
        // The height of every row of the file is recorded, so asking where a row starts takes a step for each of them: a
        // drawing that spans the sheet has to be placed from its two ends, not by asking that for every row it covers.
        var rows = new StringBuilder(ItemTable);
        for (var row = 4; row <= 20_000; row++) rows.Append($"<row r=\"{row}\" ht=\"15\" customHeight=\"1\"/>");
        var source = Workbook("anchor-whole-sheet-long-list", rows.ToString(), Bar(2, 0, 0, 16383, 1048575));

        var (markdown, _) = await ConvertAsync(source);

        Assert.Equal(ItemTableMarkdown, markdown);
    }

    [Fact]
    public async Task Fifteen_hundred_shapes_as_large_as_the_sheet_are_all_found_to_sit_on_no_table()
    {
        // Dates across the top of the shapes but no name to the left of any row: a shape is on a table only with both, and to find
        // out that none of the million rows under it has a name beside it, the cells that hold something are looked at, not the rows.
        var dates = Row(1, Text("H1", "Mon", 1), Text("I1", "Tue", 1), Text("J1", "Wed", 1)) + Row(2, Blank("H2"), Blank("I2"), Blank("J2"));
        var boxes = string.Concat(Enumerable.Range(2, 1500).Select(id => Bar(id, 7, 1, 16383, 1048575, textBox: " txBox=\"1\"")));
        var source = Workbook("fifteen-hundred-shapes-on-no-table", dates, boxes);

        var (markdown, result) = await ConvertAsync(source);
        var (alone, _) = await ConvertAsync(Workbook("dates-alone", dates));

        Assert.DoesNotContain(Extract(source).Graph.Nodes, node => node.Kind == NodeKind.Shape);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("1500 DrawingML shape(s) were retained", StringComparison.Ordinal));
        Assert.Contains("Mon", markdown, StringComparison.Ordinal);
        Assert.Equal(alone, markdown);
    }

    [Fact]
    public async Task Two_hundred_text_boxes_as_large_as_the_sheet_are_all_found_to_sit_on_the_table()
    {
        // Whether a shape sits on a table is decided for each shape on its own, and for a shape that spans the sheet from the
        // cells that hold something. These boxes have no text, so they put nothing into the cells.
        var boxes = string.Concat(Enumerable.Range(2, 200).Select(id => Bar(id, 1, 1, 16383, 1048575, textBox: " txBox=\"1\"")));
        var source = Workbook("two-hundred-text-boxes", Schedule, boxes);

        var (markdown, _) = await ConvertAsync(source);
        var (alone, _) = await ConvertAsync(Workbook("schedule-alone", Schedule));

        Assert.Equal(200, Extract(source).Graph.Nodes.Count(node => node.Kind == NodeKind.Shape && node.Extensions!.ContainsKey("sheet_overlay")));
        Assert.Contains("Design", markdown, StringComparison.Ordinal);
        Assert.Equal(alone, markdown);
    }

    [Fact]
    public async Task A_bar_anchored_from_B2_to_XFD1048576_marks_the_cells_of_the_table_only()
    {
        // The bar sits on the schedule (dates above it, task names to its left) and covers a million rows and 16,383 columns of
        // which three columns and two rows hold anything: its marker goes into those six cells, not into 17 billion.
        var source = Workbook("bar-over-table", Schedule, Bar(2, 1, 1, 16383, 1048575));

        var (markdown, _) = await ConvertAsync(source);
        var overlay = SingleOverlay(Extract(source));

        Assert.Equal(("bar", 2, 1048576, 2, 16384), (overlay.Kind, overlay.StartRow, overlay.EndRow, overlay.StartColumn, overlay.EndColumn));
        Assert.Equal(
            "# ドキュメント\n\n## Items\n\n" +
            "|  | Mon | Tue | Wed |\n" +
            "| --- | --- | --- | --- |\n" +
            "| Design | ━━ | ━━ | ━━ |\n" +
            "| Build | ━━ | ━━ | ━━ |\n", markdown);
    }

    [Fact]
    public async Task A_line_drawn_down_to_the_last_row_marks_the_rows_of_the_table_only()
    {
        var source = Workbook("line-to-the-last-row", Schedule, LineToTheLastRow(2));

        var (markdown, _) = await ConvertAsync(source);
        var overlay = SingleOverlay(Extract(source));

        Assert.Equal(("arrow", "down", 1, 1048576, 3, 3), (overlay.Kind, overlay.Direction, overlay.StartRow, overlay.EndRow, overlay.StartColumn, overlay.EndColumn));
        // The arrowhead is at the end of the line, a million rows below the table.
        Assert.Equal(
            "# ドキュメント\n\n## Items\n\n" +
            "|  | Mon | Tue<br>│ | Wed |\n" +
            "| --- | --- | --- | --- |\n" +
            "| Design |  | │ |  |\n" +
            "| Build |  | │ |  |\n", markdown);
    }

    [Fact]
    public async Task A_text_box_as_large_as_the_sheet_keeps_its_label()
    {
        // The box starts at B5, in a row that holds nothing (the table's rows are 1-3 and 6), so its label has no row and
        // column of cells to go into: it is written into the box's first cell all the same.
        var schedule = Schedule + Row(6, Text("A6", "Release"), Blank("B6"));
        var source = Workbook("text-box-over-sheet", schedule, Bar(2, 1, 4, 16383, 1048575, textBox: " txBox=\"1\"", text: "Reminder"));

        var (markdown, _) = await ConvertAsync(source);
        var overlay = SingleOverlay(Extract(source));

        Assert.Equal(("label", "Reminder", 5, 2), (overlay.Kind, overlay.Text, overlay.StartRow, overlay.StartColumn));
        Assert.Equal(1, markdown.Split("Reminder").Length - 1);
    }
}
