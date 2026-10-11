using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Xlsx;
using DocRedock.Markdown;

namespace DocRedock.Tests.Api;

/// <summary>
/// A line drawn over worksheet text is text formatting, not a drawing: people cancel a value in Excel by
/// drawing a straight line through its text (often red, sometimes double) and emphasize text by drawing a
/// line under it. Such a line strikes the cell it crosses (~~...~~ in the Markdown) or underlines it (the
/// cell node's "underline" extension), leaves the sheet's shapes so it is no overlay symbol, and a line
/// over only part of the text is reported for review instead of guessed.
///
/// Unless a test says otherwise, a sheet here has 24 pt rows and 11 pt text, bottom aligned, so a line's place
/// against the text is known: the baseline is 20.37 pt below the row's top, a strikethrough lies 14.3-18.7 pt
/// below the top (0.15-0.5 em above the baseline, a little more for the estimate), an underline 18.7-23.5 pt
/// below it, and anything higher than 14.3 pt is an overline. A column of 24 characters is 126 pt wide, one of
/// 12 is 63 pt, and Latin text is estimated at about 0.5 em a character, full-width text at 1 em.
/// </summary>
public sealed class ExcelDrawnLineDecorationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-drawn-lines-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);
    public void Dispose() => Directory.Delete(root, true);

    private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const double StrikeY = 17;      // 0.31 em above the baseline
    private const double UnderlineY = 21.5; // 0.10 em below it
    private const double OverlineY = 11.5;  // 0.80 em above it
    private const double ColumnA = 126, ColumnB = 63, ColumnC = 63;

    // ---- workbooks ----------------------------------------------------------------------------------------

    // Cell styles: 0 plain, 1 bold header, 2 centered, 3 right aligned, 4 wrapped, 5 #,##0, 6 16 pt.
    private const string Styles =
        "<fonts><font><sz val=\"11\"/></font><font><b/><sz val=\"11\"/></font><font><sz val=\"16\"/></font></fonts>" +
        "<cellXfs><xf fontId=\"0\"/><xf fontId=\"1\"/><xf fontId=\"0\"><alignment horizontal=\"center\"/></xf>" +
        "<xf fontId=\"0\"><alignment horizontal=\"right\"/></xf><xf fontId=\"0\"><alignment wrapText=\"1\"/></xf>" +
        "<xf fontId=\"0\" numFmtId=\"3\"/><xf fontId=\"2\"/></cellXfs>";

    private const string ItemColumns = "<cols><col min=\"1\" max=\"1\" width=\"24\" customWidth=\"1\"/><col min=\"2\" max=\"3\" width=\"12\" customWidth=\"1\"/></cols>";

    private static string Text(string reference, string text, int style = 0) =>
        $"<c r=\"{reference}\" s=\"{style}\" t=\"inlineStr\"><is><t>{text}</t></is></c>";

    private static string Number(string reference, string value, int style = 0) => $"<c r=\"{reference}\" s=\"{style}\"><v>{value}</v></c>";

    private static string Row(int number, double height, params string[] cells) =>
        $"<row r=\"{number}\" ht=\"{height.ToString(CultureInfo.InvariantCulture)}\" customHeight=\"1\">{string.Concat(cells)}</row>";

    private static string Row(int number, params string[] cells) => Row(number, 24, cells);

    // A table of items: a bold header, then one row per item (24 pt rows).
    private static string Items(params (string Name, string Quantity, string Price)[] rows) =>
        Row(1, Text("A1", "Item", 1), Text("B1", "Qty", 1), Text("C1", "Price", 1)) +
        string.Concat(rows.Select((item, index) => Row(index + 2, Text($"A{index + 2}", item.Name),
            Number($"B{index + 2}", item.Quantity), Number($"C{index + 2}", item.Price))));

    private static readonly (string, string, string)[] FiveItems =
        [("Apple", "10", "120"), ("Banana", "20", "80"), ("Cherry", "5", "300"), ("Date", "7", "90"), ("Elderberry", "3", "450")];

    private string Workbook(string name, string sheetData, string drawing = "", string cols = ItemColumns, string mergeCells = "", string sheetName = "Items",
        string styles = Styles, string sheetState = "visible")
    {
        var path = Path.Combine(root, name);
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
            ["xl/workbook.xml"] = $"<workbook xmlns=\"{SpreadsheetNs}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"{sheetName}\" sheetId=\"1\" state=\"{sheetState}\" r:id=\"rId1\" /></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"worksheet\" Target=\"worksheets/sheet1.xml\" /></Relationships>",
            ["xl/styles.xml"] = $"<styleSheet xmlns=\"{SpreadsheetNs}\">{styles}</styleSheet>",
            ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"{SpreadsheetNs}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">{cols}<sheetData>{sheetData}</sheetData>{mergeCells}{(drawing.Length == 0 ? "" : "<drawing r:id=\"rDrawing\" />")}</worksheet>",
        };
        if (drawing.Length > 0)
        {
            parts["xl/worksheets/_rels/sheet1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rDrawing\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"/xl/drawings/drawing1.xml\" /></Relationships>";
            parts["xl/drawings/drawing1.xml"] = $"<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\">{drawing}</xdr:wsDr>";
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

    private const string Red = "<a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill>";

    private static string AnchorFrom(int column, int row, double xPoints, double yPoints, double widthPoints, double heightPoints, string shape) =>
        $"<xdr:oneCellAnchor><xdr:from><xdr:col>{column}</xdr:col><xdr:colOff>{Emu(xPoints)}</xdr:colOff><xdr:row>{row}</xdr:row><xdr:rowOff>{Emu(yPoints)}</xdr:rowOff></xdr:from>" +
        $"<xdr:ext cx=\"{Emu(widthPoints)}\" cy=\"{Emu(heightPoints)}\"/>{shape}<xdr:clientData/></xdr:oneCellAnchor>";

    /// <summary>A straight line starting x/y points from the top left of the cell at (column, row), both
    /// zero-based, and rise points lower at its far end.</summary>
    private static string Line(int id, int column, int row, double x, double y, double length, double rise = 0, string outline = Red,
        string transform = "", string connection = "", string preset = "line", string ends = "", string attributes = "", string name = "") =>
        AnchorFrom(column, row, x, y, length, rise, LineShape(id, length, rise, outline, transform, connection, preset, ends, attributes, name));

    // The line's own element. Its a:off is where Excel keeps the shape on the sheet, which the anchor overrides.
    private static string LineShape(int id, double length, double rise = 0, string outline = Red, string transform = "", string connection = "",
        string preset = "line", string ends = "", string attributes = "", string name = "", string offset = "x=\"0\" y=\"0\"") =>
        $"<xdr:cxnSp macro=\"\"><xdr:nvCxnSpPr><xdr:cNvPr id=\"{id}\" name=\"{(name.Length == 0 ? "Line " + id : name)}\"{attributes}/><xdr:cNvCxnSpPr>{connection}</xdr:cNvCxnSpPr></xdr:nvCxnSpPr>" +
        $"<xdr:spPr><a:xfrm{transform}><a:off {offset}/><a:ext cx=\"{Emu(length)}\" cy=\"{Emu(rise)}\"/></a:xfrm><a:prstGeom prst=\"{preset}\"><a:avLst/></a:prstGeom>" +
        $"<a:ln w=\"19050\">{outline}{ends}</a:ln></xdr:spPr></xdr:cxnSp>";

    private static string Rectangle(int id, int column, int row, double x, double y, double width, double height, string fill = "<a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill>",
        string text = "", string transform = "") =>
        AnchorFrom(column, row, x, y, width, height,
            $"<xdr:sp macro=\"\" textlink=\"\"><xdr:nvSpPr><xdr:cNvPr id=\"{id}\" name=\"Rectangle {id}\"/><xdr:cNvSpPr/></xdr:nvSpPr><xdr:spPr>" +
            $"<a:xfrm{transform}><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Emu(width)}\" cy=\"{Emu(height)}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>{fill}<a:ln><a:noFill/></a:ln></xdr:spPr>" +
            (text.Length == 0 ? "" : $"<xdr:txBody><a:bodyPr/><a:p><a:r><a:t>{text}</a:t></a:r></a:p></xdr:txBody>") + "</xdr:sp>");

    // ---- reading back -------------------------------------------------------------------------------------

    private async Task<(string Markdown, ReadableDocumentExportResult Result)> Export(string source)
    {
        var output = Path.Combine(root, Path.GetFileNameWithoutExtension(source) + ".md");
        var result = await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, output, EnableOcr: false));
        return ((await File.ReadAllTextAsync(output)).Replace("\r\n", "\n", StringComparison.Ordinal), result);
    }

    private static XlsxExtractionResult Extract(string source)
    {
        using var stream = File.OpenRead(source);
        return new XlsxAdapter().Extract(stream);
    }

    private static DocumentNode CellNode(XlsxExtractionResult extraction, string address) =>
        extraction.Graph.Nodes.Single(node => node.Kind == NodeKind.Cell && node.Extensions!["address"].GetString() == address);

    private static bool Has(DocumentNode node, string key) =>
        node.Extensions!.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.True;

    // What a cell node says about its decoration: "strike", "underline", "unplaced", or both strike and
    // underline joined, or "none".
    private static string Decoration(XlsxExtractionResult extraction, string address)
    {
        var node = CellNode(extraction, address);
        var found = new[] { Has(node, "strike") ? "strike" : null, Has(node, "underline") ? "underline" : null,
            Has(node, XlsxAdapter.StrikeUnplacedExtension) ? "unplaced" : null }.OfType<string>().ToArray();
        return found.Length == 0 ? "none" : string.Join("+", found);
    }

    private static bool IsRetained(XlsxExtractionResult extraction) =>
        extraction.Warnings.Any(warning => warning.Contains("DrawingML shape(s) were retained", StringComparison.Ordinal));

    // ---- strikethrough, end to end ------------------------------------------------------------------------

    [Fact]
    public async Task A_line_through_one_text_cell_strikes_it_and_is_no_overlay_symbol()
    {
        // The red line runs through "Banana" (a 36 pt word in a 126 pt cell) a little past both ends.
        var source = Workbook("single.xlsx", Items(FiveItems), Line(2, 0, 2, 0, StrikeY, 44));
        var (markdown, _) = await Export(source);
        Assert.Contains("| Apple | 10 | 120 |\n| ~~Banana~~ | 20 | 80 |\n| Cherry | 5 | 300 |", markdown, StringComparison.Ordinal);
        // The line is formatting only: no `──` symbol, no leftover drawing.
        Assert.DoesNotContain("─", markdown, StringComparison.Ordinal);
        var extraction = Extract(source);
        Assert.Equal("strike", Decoration(extraction, "A3"));
        Assert.Equal("none", Decoration(extraction, "A2"));
        Assert.False(IsRetained(extraction));
        // The worksheet record still lists the shape: the facts of the file are kept.
        Assert.Single(Assert.Single(extraction.Worksheets).DrawingShapes!);
    }

    [Fact]
    public async Task One_line_across_a_row_strikes_every_text_cell_it_covers()
    {
        var source = Workbook("row.xlsx", Items(FiveItems), Line(2, 0, 2, 0, StrikeY, ColumnA + ColumnB + ColumnC));
        var (markdown, _) = await Export(source);
        Assert.Contains("| Apple | 10 | 120 |\n| ~~Banana~~ | ~~20~~ | ~~80~~ |\n| Cherry | 5 | 300 |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_through_numbers_that_a_table_overlay_could_cover_strikes_them_and_adds_no_symbol()
    {
        // B3:C3 has a label to its left and headers above it, so a line over it is a line symbol in both cells
        // when it is no strikethrough (here, at the middle of the row, over the letters).
        var over = Workbook("overlay-over.xlsx", Items(FiveItems), Line(2, 1, 2, 0, OverlineY, ColumnB + ColumnC));
        Assert.Contains("| Banana | 20<br>── | 80<br>── |", (await Export(over)).Markdown, StringComparison.Ordinal);
        var through = Workbook("overlay-through.xlsx", Items(FiveItems), Line(2, 1, 2, 0, StrikeY, ColumnB + ColumnC));
        var (markdown, _) = await Export(through);
        Assert.Contains("| Banana | ~~20~~ | ~~80~~ |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("─", markdown, StringComparison.Ordinal);
        // With an arrow over B5:C5 as well, which is an overlay and so keeps the overlay detection running over
        // the sheet's shapes, the strikethrough is still no symbol and the arrow still is one.
        var both = Workbook("overlay-both.xlsx", Items(FiveItems),
            Line(2, 1, 2, 0, StrikeY, ColumnB + ColumnC) + Line(3, 1, 4, 0, StrikeY, ColumnB + ColumnC, ends: "<a:tailEnd type=\"triangle\"/>"));
        var (withArrow, _) = await Export(both);
        Assert.Contains("| Banana | ~~20~~ | ~~80~~ |", withArrow, StringComparison.Ordinal);
        Assert.Contains("| Date | 7<br>━━ | 90<br>━━▶ |", withArrow, StringComparison.Ordinal);
        Assert.DoesNotContain("─", withArrow.Replace("━", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_red_double_line_strikes_the_text_once()
    {
        // 見え消し: two red lines 3 pt apart through "Elderberry", as a person draws a cancelled entry.
        var source = Workbook("double.xlsx", Items(FiveItems), Line(2, 0, 5, 0, StrikeY - 1.5, 62) + Line(3, 0, 5, 0, StrikeY + 1.5, 62));
        var (markdown, _) = await Export(source);
        Assert.Contains("| Date | 7 | 90 |\n| ~~Elderberry~~ | 3 | 450 |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("─", markdown, StringComparison.Ordinal);
        Assert.False(IsRetained(Extract(source)));
    }

    [Fact]
    public void The_two_lines_of_a_double_line_close_beside_each_other_are_not_a_connector()
    {
        // 1.2 pt apart the lines would meet each other's ends (within the 1.5 pt of a touch) were they not
        // taken as the two lines of one double line.
        var source = Workbook("close.xlsx", Items(FiveItems), Line(2, 0, 5, 0, StrikeY - 0.6, 62) + Line(3, 0, 5, 0, StrikeY + 0.6, 62));
        var extraction = Extract(source);
        Assert.Equal("strike", Decoration(extraction, "A6"));
        Assert.False(IsRetained(extraction));
    }

    [Fact]
    public void A_line_pasted_twice_still_strikes()
    {
        // The copy a fifth of a point off: each of the two would otherwise meet the other end to end.
        var source = Workbook("pasted.xlsx", Items(FiveItems), Line(2, 0, 2, 0, StrikeY, 44) + Line(3, 0, 2, 0.2, StrikeY + 0.2, 44));
        var extraction = Extract(source);
        Assert.Equal("strike", Decoration(extraction, "A3"));
        Assert.False(IsRetained(extraction));
    }

    [Fact]
    public void Lines_close_beside_each_other_are_a_double_line_only_over_the_same_span()
    {
        // Two lines 1.2 pt apart, the second 20 pt longer: not the two lines of a double line but two lines
        // meeting at their ends, a rule.
        var source = Workbook("different.xlsx", Items(FiveItems), Line(2, 0, 2, 0, StrikeY - 0.6, 44) + Line(3, 0, 2, 0, StrikeY + 0.6, 64));
        Assert.Equal("none", Decoration(Extract(source), "A3"));
        // Two lines end to end on the same level are one rule, not a strikethrough each.
        var continued = Workbook("continued.xlsx", Items(FiveItems), Line(2, 0, 2, 0, StrikeY, 40) + Line(3, 0, 2, 40, StrikeY, 40));
        Assert.Equal("none", Decoration(Extract(continued), "A3"));
        // A bar thicker than a decoration right above the line is not its second line.
        var bar = Workbook("barabove.xlsx", Items(FiveItems), Rectangle(10, 0, 2, 0, 10, 44, 6) + Line(2, 0, 2, 0, StrikeY, 44));
        Assert.Equal("none", Decoration(Extract(bar), "A3"));
    }

    [Fact]
    public async Task A_struck_number_reads_as_struck_in_its_shown_form()
    {
        var sheet = Row(1, Text("A1", "Item", 1), Text("B1", "Amount", 1)) + Row(2, Text("A2", "Rent"), Number("B2", "1200", 5)) + Row(3, Text("A3", "Fees"), Number("B3", "45", 5));
        // "1,200" is right aligned in the 63 pt column B and about 27 pt wide: the line spans the cell.
        var source = Workbook("number.xlsx", sheet, Line(2, 1, 1, 0, StrikeY, ColumnB));
        var (markdown, _) = await Export(source);
        Assert.Contains("| Rent | ~~1,200~~ |\n| Fees | 45 |", markdown, StringComparison.Ordinal);
        // A number sits at the right of its cell: a line over the right half strikes it, one over the left half is beside it.
        Assert.Equal("strike", Decoration(Extract(Workbook("numberright.xlsx", sheet, Line(2, 1, 1, 30, StrikeY, 33))), "B2"));
        Assert.Equal("none", Decoration(Extract(Workbook("numberleft.xlsx", sheet, Line(2, 1, 1, 0, StrikeY, 25))), "B2"));
    }

    [Fact]
    public void Full_width_text_is_as_wide_as_it_is_tall()
    {
        // Nine full-width characters at 11 pt are 99 pt; a line over the first 60 pt crosses only part of it.
        var sheet = Row(1, Text("A1", "Item", 1)) + Row(2, Text("A2", "要件定義の確認作業"));
        Assert.Equal("unplaced", Decoration(Extract(Workbook("wide.xlsx", sheet, Line(2, 0, 1, 0, StrikeY, 60))), "A2"));
        Assert.Equal("strike", Decoration(Extract(Workbook("widewhole.xlsx", sheet, Line(2, 0, 1, 0, StrikeY, 100))), "A2"));
    }

    [Fact]
    public void A_cell_the_font_strikes_in_part_is_struck_whole_by_a_line_across_all_of_it()
    {
        var shared = "<r><rPr><strike/></rPr><t>Banana </t></r><r><t>split</t></r>";
        var sheet = Row(1, Text("A1", "Item", 1)) + Row(2, Text("A2", "Apple")) + Row(3, $"<c r=\"A3\" t=\"inlineStr\"><is>{shared}</is></c>");
        var source = Workbook("richstrike.xlsx", sheet, Line(2, 0, 2, 0, StrikeY, 80));
        var node = CellNode(Extract(source), "A3");
        Assert.True(Has(node, "strike"));
        Assert.False(node.Extensions!.ContainsKey("strike_ranges"));
    }

    // ---- underline ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_line_under_a_cell_underlines_it()
    {
        var source = Workbook("underline.xlsx", Items(FiveItems), Line(2, 0, 4, 0, UnderlineY, 30, outline: "<a:solidFill><a:srgbClr val=\"0000FF\"/></a:solidFill>"));
        var extraction = Extract(source);
        Assert.Equal("underline", Decoration(extraction, "A5"));
        Assert.Equal("none", Decoration(extraction, "A4"));
        Assert.False(IsRetained(extraction));
        // Underlining is not struck text, and the line leaves no symbol behind.
        var (markdown, _) = await Export(source);
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("─", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_double_underline_underlines_once()
    {
        var source = Workbook("doubleunder.xlsx", Items(FiveItems), Line(2, 0, 4, 0, UnderlineY - 1.5, 30) + Line(3, 0, 4, 0, UnderlineY + 1.5, 30));
        var extraction = Extract(source);
        Assert.Equal("underline", Decoration(extraction, "A5"));
        Assert.False(IsRetained(extraction));
    }

    [Fact]
    public void A_line_on_the_bottom_edge_of_the_row_is_its_border_rule_not_an_underline()
    {
        var source = Workbook("border.xlsx", Items(FiveItems), Line(2, 0, 4, 0, 23.8, 40));
        var extraction = Extract(source);
        Assert.Equal("none", Decoration(extraction, "A5"));
        Assert.True(IsRetained(extraction));
    }

    [Fact]
    public void A_line_across_part_of_the_text_underlines_nothing_and_asks_for_no_review()
    {
        // 20 pt of the 100 pt "Cherry pie with cream": a partial underline marks nothing and is no review.
        var sheet = Row(1, Text("A1", "Item", 1)) + Row(2, Text("A2", "Cherry pie with cream"));
        var source = Workbook("partialunder.xlsx", sheet, Line(2, 0, 1, 0, UnderlineY, 20));
        var extraction = Extract(source);
        Assert.Equal("none", Decoration(extraction, "A2"));
        Assert.DoesNotContain(extraction.Warnings, warning => warning.StartsWith("XlsxStrikethroughUnplaced", StringComparison.Ordinal));
        Assert.False(IsRetained(extraction));
        Assert.Empty(ExportReviewBuilder.Build(extraction.Graph, []).Pages);
    }

    // ---- partial strikes ask for review -------------------------------------------------------------------

    [Fact]
    public async Task A_line_through_part_of_a_long_text_marks_nothing_and_asks_for_review()
    {
        var sheet = Items(("Apple", "10", "120"), ("Cherry pie with cream", "5", "300"), ("Date", "7", "90"));
        // 28 pt of the roughly 100 pt text: which characters are struck cannot be told.
        var source = Workbook("partial.xlsx", sheet, Line(2, 0, 2, 0, StrikeY, 28));
        var (markdown, result) = await Export(source);
        Assert.Contains("| Cherry pie with cream | 5 | 300 |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("─", markdown, StringComparison.Ordinal);

        var extraction = Extract(source);
        Assert.Equal("unplaced", Decoration(extraction, "A3"));
        Assert.True(Has(CellNode(extraction, "A3"), ReadableMarkdownSerializer.SourceReviewRequiredExtension));
        Assert.False(IsRetained(extraction));
        var warning = Assert.Single(extraction.Warnings, item => item.StartsWith("XlsxStrikethroughUnplaced", StringComparison.Ordinal));
        Assert.Contains("(A3)", warning, StringComparison.Ordinal);
        Assert.EndsWith("(worksheet: Items)", warning, StringComparison.Ordinal);

        // One review element for the cell, on the sheet's page, in the existing wording.
        Assert.Equal(1, ExportSummaryBuilder.Build(result.Graph, result.Diagnostics).ReviewPages);
        var page = Assert.Single(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages);
        Assert.Equal(DocumentFormatKind.Xlsx, page.Format);
        Assert.Equal(ReviewElementKind.StrikethroughUnplaced, Assert.Single(page.Elements).Kind);
        Assert.Equal("シート「Items」：文字1件は、一部に取り消し線がありますが、どの文字が取り消されているかを確定できませんでした（取り消し線の印を付けずにMarkdownに含めています）。元のファイルで内容を確認してください。",
            ExportReviewText.DescribeJapanese(page));
        Assert.Equal("sheet 'Items': 1 text item(s) with a strikethrough over part of them whose struck characters could not be determined; kept in the Markdown without the mark",
            ExportReviewText.DescribeEnglish(page));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.StartsWith("XlsxStrikethroughUnplaced", StringComparison.Ordinal));
    }

    [Fact]
    public void The_text_of_a_hidden_sheet_is_left_alone()
    {
        // Not in the output, so neither marked nor reported.
        var sheet = Items(("Banana", "20", "80"), ("Cherry pie with cream", "5", "300"));
        var extraction = Extract(Workbook("hiddensheet.xlsx", sheet, Line(2, 0, 1, 0, StrikeY, 44) + Line(3, 0, 2, 0, StrikeY, 28), sheetState: "hidden"));
        Assert.Equal("none", Decoration(extraction, "A2"));
        Assert.Equal("none", Decoration(extraction, "A3"));
        Assert.Empty(ExportReviewBuilder.Build(extraction.Graph, []).Pages);
    }

    [Fact]
    public void A_cell_the_font_already_strikes_whole_needs_no_review_for_a_line_across_part_of_it()
    {
        var sheet = Row(1, Text("A1", "Item", 1)) + Row(2, Text("A2", "Cherry pie with cream", 6));
        var styles = Styles.Replace("<font><sz val=\"16\"/></font>", "<font><strike/><sz val=\"11\"/></font>", StringComparison.Ordinal);
        var extraction = Extract(Workbook("struckfont.xlsx", sheet, Line(2, 0, 1, 0, StrikeY, 28), styles: styles));
        Assert.Equal("strike", Decoration(extraction, "A2"));
        Assert.Empty(ExportReviewBuilder.Build(extraction.Graph, []).Pages);
    }

    [Fact]
    public void The_review_marker_uses_the_key_the_readable_projection_asks_for()
    {
        Assert.Equal("source_review_required", ReadableMarkdownSerializer.SourceReviewRequiredExtension);
        Assert.Equal("strike_unplaced", XlsxAdapter.StrikeUnplacedExtension);
    }

    // ---- what is no decoration ----------------------------------------------------------------------------

    [Theory]
    [InlineData(OverlineY, "none")]        // over the letters: an overline
    [InlineData(5.0, "none")]              // far above the text
    [InlineData(14.1, "none")]             // 0.57 em above the baseline: over the letters
    [InlineData(14.6, "strike")]           // 0.52 em: the estimate of the baseline may be that far off
    [InlineData(StrikeY, "strike")]
    [InlineData(18.2, "strike")]           // 0.2 em above the baseline
    [InlineData(19.5, "underline")]        // just under the strikethrough zone
    public void A_line_decorates_only_at_the_height_of_a_strikethrough_or_an_underline(double y, string expected)
    {
        var extraction = Extract(Workbook("zones.xlsx", Items(FiveItems), Line(2, 0, 2, 0, y, 44)));
        Assert.Equal(expected, Decoration(extraction, "A3"));
        Assert.Equal(expected == "none", IsRetained(extraction));
    }

    [Fact]
    public async Task A_line_at_overline_height_is_no_decoration()
    {
        var source = Workbook("overline.xlsx", Items(FiveItems), Line(2, 0, 2, 0, OverlineY, 44));
        var (markdown, _) = await Export(source);
        Assert.Contains("| Banana | 20 | 80 |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);
        Assert.True(IsRetained(Extract(source)));
    }

    [Fact]
    public void An_arrow_is_not_a_decoration()
    {
        var arrow = "<a:tailEnd type=\"triangle\"/>";
        var extraction = Extract(Workbook("arrow.xlsx", Items(FiveItems), Line(2, 0, 2, 0, StrikeY, 44, ends: arrow)));
        Assert.Equal("none", Decoration(extraction, "A3"));
        Assert.True(IsRetained(extraction));
        // A head or tail of type "none" is no arrowhead.
        var plain = Extract(Workbook("noheads.xlsx", Items(FiveItems), Line(2, 0, 2, 0, StrikeY, 44, ends: "<a:headEnd type=\"none\"/><a:tailEnd type=\"none\"/>")));
        Assert.Equal("strike", Decoration(plain, "A3"));
    }

    [Fact]
    public void A_line_a_few_degrees_slanted_is_not_horizontal_enough()
    {
        // 8 degrees over 44 pt rises 6 pt, more than a fifth of an em; a degree rises 0.8 pt.
        var slanted = Extract(Workbook("eight.xlsx", Items(FiveItems), Line(2, 0, 2, 0, StrikeY, 44, transform: " rot=\"480000\"")));
        Assert.Equal("none", Decoration(slanted, "A3"));
    }

    [Fact]
    public void Only_a_nearly_horizontal_line_is_a_decoration()
    {
        // Upright: a line straight down the cell, at the height of the text.
        var upright = Extract(Workbook("upright.xlsx", Items(FiveItems), Line(2, 0, 2, 20, 0, 0, rise: 24)));
        Assert.Equal("none", Decoration(upright, "A3"));
        // Slanted: a cancellation stroke "/" over the text, 10 pt of rise in 44.
        var slanted = Extract(Workbook("slanted.xlsx", Items(FiveItems), Line(2, 0, 2, 0, 12, 44, rise: 10)));
        Assert.Equal("none", Decoration(slanted, "A3"));
    }

    [Fact]
    public void A_slightly_tilted_line_through_the_text_still_strikes()
    {
        // 1.5 pt of rise over 44 pt: a hand-drawn line a degree or two off the horizontal.
        var extraction = Extract(Workbook("tilt.xlsx", Items(FiveItems), Line(2, 0, 2, 0, StrikeY - 0.75, 44, rise: 1.5)));
        Assert.Equal("strike", Decoration(extraction, "A3"));
    }

    [Fact]
    public void A_line_that_joins_two_shapes_is_a_connector_not_a_decoration()
    {
        // A connector glued to a box at each end, running through "Banana" at the height of a strikethrough.
        var boxes = Rectangle(10, 0, 2, 0, 4, 6, 6, text: "A") + Rectangle(11, 0, 2, 44, 4, 6, 6, text: "B");
        var glued = Line(2, 0, 2, 0, StrikeY, 44, connection: "<a:stCxn id=\"10\" idx=\"3\"/><a:endCxn id=\"11\" idx=\"1\"/>");
        var extraction = Extract(Workbook("glued.xlsx", Items(FiveItems), boxes + glued));
        Assert.Equal("none", Decoration(extraction, "A3"));
    }

    [Fact]
    public void A_line_whose_end_meets_another_shape_or_line_is_not_a_decoration()
    {
        // Nothing is glued, but the strike line (0-44 pt) ends on the edge of a box...
        var box = Rectangle(10, 0, 2, 44, 4, 8, 20);
        var onBox = Extract(Workbook("touchbox.xlsx", Items(FiveItems), box + Line(2, 0, 2, 0, StrikeY, 44)));
        Assert.Equal("none", Decoration(onBox, "A3"));
        // ...or on another line crossing its end.
        var upright = Line(11, 0, 2, 44, 10, 0, rise: 14);
        var onLine = Extract(Workbook("touchline.xlsx", Items(FiveItems), upright + Line(2, 0, 2, 0, StrikeY, 44)));
        Assert.Equal("none", Decoration(onLine, "A3"));
        // One 1 pt from its end is met (within the 1.5 pt of a touch); one 3 pt away is not.
        var close = Rectangle(10, 0, 2, 45, 4, 8, 20);
        Assert.Equal("none", Decoration(Extract(Workbook("closebox.xlsx", Items(FiveItems), close + Line(2, 0, 2, 0, StrikeY, 44))), "A3"));
        var near = Rectangle(10, 0, 2, 47, 4, 8, 20);
        Assert.Equal("strike", Decoration(Extract(Workbook("nearbox.xlsx", Items(FiveItems), near + Line(2, 0, 2, 0, StrikeY, 44))), "A3"));
        // A box nobody sees is not met.
        var hidden = Rectangle(10, 0, 2, 44, 4, 8, 20).Replace("name=\"Rectangle 10\"", "name=\"Rectangle 10\" hidden=\"1\"", StringComparison.Ordinal);
        Assert.Equal("strike", Decoration(Extract(Workbook("hiddenbox.xlsx", Items(FiveItems), hidden + Line(2, 0, 2, 0, StrikeY, 44))), "A3"));
    }

    [Fact]
    public void A_slanted_line_is_met_where_it_is_drawn_not_where_its_frame_corners_are()
    {
        // A line in the frame 44-50 pt by 17-31 pt: unflipped it starts at the end of the strike line (44, 17);
        // flipped, it runs from the bottom left to the top right and passes 5 pt from that end.
        var meets = Line(11, 0, 2, 44, 17, 6, rise: 14);
        var apart = Line(11, 0, 2, 44, 17, 6, rise: 14, transform: " flipV=\"1\"");
        Assert.Equal("none", Decoration(Extract(Workbook("slantmeets.xlsx", Items(FiveItems), meets + Line(2, 0, 2, 0, StrikeY, 44))), "A3"));
        Assert.Equal("strike", Decoration(Extract(Workbook("slantapart.xlsx", Items(FiveItems), apart + Line(2, 0, 2, 0, StrikeY, 44))), "A3"));
    }

    [Fact]
    public void A_thin_bar_decorates_like_a_line_and_a_thick_one_does_not()
    {
        var thin = Extract(Workbook("thinbar.xlsx", Items(FiveItems), Rectangle(2, 0, 2, 0, StrikeY - 0.5, 44, 1)));
        Assert.Equal("strike", Decoration(thin, "A3"));
        var thick = Extract(Workbook("thickbar.xlsx", Items(FiveItems), Rectangle(2, 0, 2, 0, StrikeY - 3, 44, 6)));
        Assert.Equal("none", Decoration(thick, "A3"));
        // Not even a thin one carries text.
        var labelled = Extract(Workbook("labelledbar.xlsx", Items(FiveItems), Rectangle(2, 0, 2, 0, StrikeY - 0.5, 44, 1, text: "x")));
        Assert.Equal("none", Decoration(labelled, "A3"));
    }

    [Theory]
    [InlineData(" hidden=\"1\"", "")]                      // a hidden shape
    [InlineData("", "<a:ln w=\"19050\"><a:noFill/></a:ln>")] // a line that paints nothing
    public void A_line_nobody_sees_decorates_nothing(string attributes, string outline)
    {
        var drawing = outline.Length == 0
            ? Line(2, 0, 2, 0, StrikeY, 44, attributes: attributes)
            : AnchorFrom(0, 2, 0, StrikeY, 44, 0,
                "<xdr:cxnSp macro=\"\"><xdr:nvCxnSpPr><xdr:cNvPr id=\"2\" name=\"Line 2\"/><xdr:cNvCxnSpPr/></xdr:nvCxnSpPr><xdr:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/>" +
                $"<a:ext cx=\"{Emu(44)}\" cy=\"0\"/></a:xfrm><a:prstGeom prst=\"line\"><a:avLst/></a:prstGeom>{outline}</xdr:spPr></xdr:cxnSp>");
        var extraction = Extract(Workbook("invisible.xlsx", Items(FiveItems), drawing));
        Assert.Equal("none", Decoration(extraction, "A3"));
    }

    [Fact]
    public async Task A_line_through_cells_that_hold_only_symbols_decorates_nothing()
    {
        // Milestone marks in a schedule row: a line through them is a line, not a cancellation, and stays the
        // line symbol of the table overlay it was.
        var sheet = Row(1, Text("A1", "Item", 1), Text("B1", "Mon", 1), Text("C1", "Tue", 1)) + Row(2, Text("A2", "Release"), Text("B2", "◆", 2), Text("C2", "○", 2));
        var source = Workbook("symbols.xlsx", sheet, Line(2, 1, 1, 0, StrikeY, ColumnB + ColumnC));
        var extraction = Extract(source);
        Assert.Equal("none", Decoration(extraction, "B2"));
        Assert.Equal("none", Decoration(extraction, "C2"));
        var (markdown, _) = await Export(source);
        Assert.Contains("| Release | ◆<br>── | ○<br>── |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_thin_bar_over_small_text_decorates_it_and_does_not_meet_itself()
    {
        // 6 pt text: a decoration is at most 0.2 em (1.2 pt) thick, but a bar of up to 1.5 pt counts as thin.
        var styles = Styles.Replace("</fonts>", "<font><sz val=\"6\"/></font></fonts>", StringComparison.Ordinal)
            .Replace("</cellXfs>", "<xf fontId=\"3\"/></cellXfs>", StringComparison.Ordinal);
        // The baseline is 22 pt down (24 - 0.33 * 6), so 20 pt down is 0.33 em above it.
        var extraction = Extract(Workbook("smalltext.xlsx", Row(1, Text("A1", "Banana", 7)), Rectangle(2, 0, 0, 0, 19.3, 28, 1.4), styles: styles));
        Assert.Equal("strike", Decoration(extraction, "A1"));
    }

    [Fact]
    public void A_speck_of_a_line_decorates_nothing()
    {
        // 4 pt long inside "Banana": it overlaps the text, but a line shorter than 0.4 em (4.4 pt) is no stroke.
        var extraction = Extract(Workbook("speck.xlsx", Items(FiveItems), Line(2, 0, 2, 2, StrikeY, 4)));
        Assert.Equal("none", Decoration(extraction, "A3"));
    }

    [Theory]
    [InlineData("line", "strike")]
    [InlineData("straightConnector1", "strike")]
    [InlineData("bentConnector3", "none")]
    [InlineData("curvedConnector3", "none")]
    public void Only_a_straight_line_shape_is_a_decoration(string preset, string expected)
    {
        var extraction = Extract(Workbook("preset.xlsx", Items(FiveItems), Line(2, 0, 2, 0, StrikeY, 44, preset: preset)));
        Assert.Equal(expected, Decoration(extraction, "A3"));
    }

    [Fact]
    public void A_bar_turned_a_half_turn_is_still_a_bar_and_one_turned_a_quarter_turn_is_not_placed()
    {
        var half = Extract(Workbook("barhalf.xlsx", Items(FiveItems), Rectangle(2, 0, 2, 0, StrikeY - 0.5, 44, 1, transform: " rot=\"10800000\"")));
        Assert.Equal("strike", Decoration(half, "A3"));
        var quarter = Extract(Workbook("barquarter.xlsx", Items(FiveItems), Rectangle(2, 0, 2, 0, StrikeY - 0.5, 44, 1, transform: " rot=\"5400000\"")));
        Assert.Equal("none", Decoration(quarter, "A3"));
    }

    [Fact]
    public void A_line_that_stops_a_little_short_of_the_estimated_end_of_the_text_still_covers_it()
    {
        // "Banana split" is estimated at 61.6 pt (ending at 63 pt) but is shorter in most fonts (Calibri about
        // 56 pt): a line to 52 pt covers it within the 15% an estimate can be off, one to 44 pt does not.
        var sheet = Row(1, Text("A1", "Item", 1)) + Row(2, Text("A2", "Banana split"));
        Assert.Equal("strike", Decoration(Extract(Workbook("estimate.xlsx", sheet, Line(2, 0, 1, 0, StrikeY, 52))), "A2"));
        Assert.Equal("unplaced", Decoration(Extract(Workbook("estimateshort.xlsx", sheet, Line(2, 0, 1, 0, StrikeY, 44))), "A2"));
    }

    [Fact]
    public void A_cell_struck_in_part_by_one_line_and_whole_by_another_is_struck()
    {
        // The two lines are 2 pt apart: not meeting, and not a double line either (their spans differ).
        var drawing = Line(2, 0, 2, 0, StrikeY, 28) + Line(3, 0, 2, 0, StrikeY - 2, 70);
        var extraction = Extract(Workbook("wholeandpart.xlsx", Items(FiveItems), drawing));
        Assert.Equal("strike", Decoration(extraction, "A3"));
        Assert.False(IsRetained(extraction));
    }

    [Fact]
    public void A_line_that_starts_inside_one_cell_strikes_the_cells_it_covers_and_reports_the_one_it_enters()
    {
        // From 20 pt, inside "Banana", across B and C to the end of the row.
        var extraction = Extract(Workbook("midrow.xlsx", Items(FiveItems), Line(2, 0, 2, 20, StrikeY, ColumnA + ColumnB + ColumnC - 20)));
        Assert.Equal("unplaced", Decoration(extraction, "A3"));
        Assert.Equal("strike", Decoration(extraction, "B3"));
        Assert.Equal("strike", Decoration(extraction, "C3"));
    }

    [Fact]
    public async Task Each_cell_a_line_crosses_in_part_is_one_review_element_listed_in_order()
    {
        var sheet = Items(("Apple pie with a long name", "10", "120"), ("Banana", "20", "80"), ("Cherry pie with cream", "5", "300"));
        var source = Workbook("twopartial.xlsx", sheet, Line(2, 0, 3, 0, StrikeY, 28) + Line(3, 0, 1, 0, StrikeY, 28));
        var (_, result) = await Export(source);
        var page = Assert.Single(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages);
        Assert.Equal(2, page.Elements.Count);
        Assert.All(page.Elements, element => Assert.Equal(ReviewElementKind.StrikethroughUnplaced, element.Kind));
        var warning = Assert.Single(Extract(source).Warnings, item => item.StartsWith("XlsxStrikethroughUnplaced", StringComparison.Ordinal));
        Assert.Contains("2 cell(s) (A2, A4)", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_that_only_grazes_the_text_decorates_nothing()
    {
        // It starts 2 pt before the end of "Banana" (to about 37.8 pt), less than a third of an em.
        var extraction = Extract(Workbook("graze.xlsx", Items(FiveItems), Line(2, 0, 2, 35.8, StrikeY, 44.2)));
        Assert.Equal("none", Decoration(extraction, "A3"));
    }

    // ---- placement: group, flip, rotation, AlternateContent ----------------------------------------------------

    // A group anchored at A3 (row 3 starts 48 pt down) that shows its children at half size from a child space
    // starting at (1000000, 2000000): its child, the line at local (1000000, 2431800) of length 1117600, lies
    // 215900 EMU (17 pt) below the row's top and is 558800 EMU (44 pt) long.
    private static string GroupedLine(string groupTransform = "", bool nested = false)
    {
        const string line =
            "<xdr:cxnSp macro=\"\"><xdr:nvCxnSpPr><xdr:cNvPr id=\"2\" name=\"Line 2\"/><xdr:cNvCxnSpPr/></xdr:nvCxnSpPr><xdr:spPr>" +
            "<a:xfrm><a:off x=\"1000000\" y=\"2431800\"/><a:ext cx=\"1117600\" cy=\"0\"/></a:xfrm><a:prstGeom prst=\"line\"><a:avLst/></a:prstGeom>" +
            "<a:ln w=\"19050\"><a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill></a:ln></xdr:spPr></xdr:cxnSp>";
        var group = "<xdr:grpSp><xdr:nvGrpSpPr><xdr:cNvPr id=\"20\" name=\"group\"/><xdr:cNvGrpSpPr/></xdr:nvGrpSpPr>" +
            $"<xdr:grpSpPr><a:xfrm{groupTransform}><a:off x=\"0\" y=\"609600\"/><a:ext cx=\"1600200\" cy=\"304800\"/><a:chOff x=\"1000000\" y=\"2000000\"/><a:chExt cx=\"3200400\" cy=\"609600\"/></a:xfrm></xdr:grpSpPr>{line}</xdr:grpSp>";
        if (nested)
            group = "<xdr:grpSp><xdr:nvGrpSpPr><xdr:cNvPr id=\"21\" name=\"outer\"/><xdr:cNvGrpSpPr/></xdr:nvGrpSpPr>" +
                $"<xdr:grpSpPr><a:xfrm><a:off x=\"0\" y=\"609600\"/><a:ext cx=\"1600200\" cy=\"304800\"/><a:chOff x=\"0\" y=\"609600\"/><a:chExt cx=\"1600200\" cy=\"304800\"/></a:xfrm></xdr:grpSpPr>{group}</xdr:grpSp>";
        return "<xdr:twoCellAnchor><xdr:from><xdr:col>0</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>2</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>" +
            $"<xdr:to><xdr:col>1</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>3</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>{group}<xdr:clientData/></xdr:twoCellAnchor>";
    }

    [Fact]
    public void A_line_in_a_group_with_a_transform_is_placed_through_the_transform()
    {
        var extraction = Extract(Workbook("group.xlsx", Items(FiveItems), GroupedLine()));
        Assert.Equal("strike", Decoration(extraction, "A3"));
        Assert.False(IsRetained(extraction));
    }

    [Theory]
    [InlineData(" rot=\"10800000\"")]
    [InlineData(" flipH=\"1\"")]
    [InlineData(" flipV=\"1\"")]
    public void A_line_in_a_group_that_is_turned_or_flipped_is_not_placed(string transform)
    {
        // The group's own turn or flip moves its children in a way the group transform here does not apply.
        var extraction = Extract(Workbook("groupturned.xlsx", Items(FiveItems), GroupedLine(transform)));
        Assert.Equal("none", Decoration(extraction, "A3"));
    }

    [Fact]
    public void A_line_in_a_group_inside_a_group_is_not_placed()
    {
        var extraction = Extract(Workbook("groupnested.xlsx", Items(FiveItems), GroupedLine(nested: true)));
        Assert.Equal("none", Decoration(extraction, "A3"));
    }

    [Theory]
    [InlineData(" rot=\"10800000\"")]                  // turned half a turn
    [InlineData(" flipH=\"1\"")]
    [InlineData(" flipV=\"1\"")]
    [InlineData(" rot=\"10800000\" flipH=\"1\" flipV=\"1\"")]
    public void A_line_flipped_or_turned_half_a_turn_still_strikes(string transform)
    {
        var extraction = Extract(Workbook("flipped.xlsx", Items(FiveItems), Line(2, 0, 2, 0, StrikeY, 44, transform: transform)));
        Assert.Equal("strike", Decoration(extraction, "A3"));
    }

    [Fact]
    public void A_line_turned_a_quarter_turn_is_not_placed()
    {
        // Stored horizontal and turned about its center: upright.
        var upright = Extract(Workbook("turned.xlsx", Items(FiveItems), Line(2, 0, 2, 0, StrikeY, 44, transform: " rot=\"5400000\"")));
        Assert.Equal("none", Decoration(upright, "A3"));
        // A few degrees off the horizontal is still a line through the text.
        var tilted = Extract(Workbook("slightturn.xlsx", Items(FiveItems), Line(2, 0, 2, 0, StrikeY, 44, transform: " rot=\"60000\"")));
        Assert.Equal("strike", Decoration(tilted, "A3"));
        // Stored upright (44 pt tall) and turned a quarter turn about its center (22, 17 below the row's top) it
        // would lie across the text, but the anchor of a turned shape may hold its turned or its unturned frame.
        var lying = Extract(Workbook("lying.xlsx", Items(FiveItems), AnchorFrom(0, 1, 22, 19, 0, 44,
            "<xdr:cxnSp macro=\"\"><xdr:nvCxnSpPr><xdr:cNvPr id=\"2\" name=\"Line 2\"/><xdr:cNvCxnSpPr/></xdr:nvCxnSpPr><xdr:spPr>" +
            $"<a:xfrm rot=\"5400000\"><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"{Emu(44)}\"/></a:xfrm><a:prstGeom prst=\"line\"><a:avLst/></a:prstGeom><a:ln w=\"19050\">{Red}</a:ln></xdr:spPr></xdr:cxnSp>")));
        Assert.Equal("none", Decoration(lying, "A3"));
    }

    [Fact]
    public void Lines_in_a_two_cell_anchor_and_in_an_absolute_anchor_are_placed_like_any_other()
    {
        // As Excel writes a line: a two cell anchor from and to the same height, the shape's own a:off in sheet
        // coordinates, and a style element.
        var style = "<xdr:style><a:lnRef idx=\"1\"><a:schemeClr val=\"accent1\"/></a:lnRef><a:fillRef idx=\"0\"><a:schemeClr val=\"accent1\"/></a:fillRef>" +
            "<a:effectRef idx=\"0\"><a:schemeClr val=\"accent1\"/></a:effectRef><a:fontRef idx=\"minor\"><a:schemeClr val=\"tx1\"/></a:fontRef></xdr:style>";
        string Shape(int id) => LineShape(id, 44, offset: "x=\"0\" y=\"825500\"").Replace("</xdr:cxnSp>", style + "</xdr:cxnSp>", StringComparison.Ordinal);
        var twoCell = "<xdr:twoCellAnchor editAs=\"oneCell\"><xdr:from><xdr:col>0</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>2</xdr:row>" +
            $"<xdr:rowOff>{Emu(StrikeY)}</xdr:rowOff></xdr:from><xdr:to><xdr:col>0</xdr:col><xdr:colOff>{Emu(44)}</xdr:colOff><xdr:row>2</xdr:row>" +
            $"<xdr:rowOff>{Emu(StrikeY)}</xdr:rowOff></xdr:to>{Shape(2)}<xdr:clientData/></xdr:twoCellAnchor>";
        Assert.Equal("strike", Decoration(Extract(Workbook("twocell.xlsx", Items(FiveItems), twoCell)), "A3"));
        // The same line at its absolute place on the sheet: row 3 starts 48 pt down.
        var absolute = $"<xdr:absoluteAnchor><xdr:pos x=\"0\" y=\"{Emu(48 + StrikeY)}\"/><xdr:ext cx=\"{Emu(44)}\" cy=\"0\"/>{Shape(2)}<xdr:clientData/></xdr:absoluteAnchor>";
        Assert.Equal("strike", Decoration(Extract(Workbook("absolute.xlsx", Items(FiveItems), absolute)), "A3"));
    }

    [Fact]
    public void Lines_as_other_writers_save_them_are_read_like_any_other()
    {
        // A hand-written drawing (and openpyxl's): a two cell anchor over a shape whose a:xfrm is all zeros.
        string Zero(int id, string ends) =>
            "<xdr:twoCellAnchor><xdr:from><xdr:col>0</xdr:col><xdr:colOff>12700</xdr:colOff><xdr:row>2</xdr:row>" +
            $"<xdr:rowOff>{Emu(StrikeY)}</xdr:rowOff></xdr:from><xdr:to><xdr:col>0</xdr:col><xdr:colOff>{Emu(46)}</xdr:colOff><xdr:row>2</xdr:row>" +
            $"<xdr:rowOff>{Emu(StrikeY)}</xdr:rowOff></xdr:to><xdr:cxnSp macro=\"\"><xdr:nvCxnSpPr><xdr:cNvPr id=\"{id}\" name=\"Line {id}\"/><xdr:cNvCxnSpPr/></xdr:nvCxnSpPr>" +
            $"<xdr:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/></a:xfrm><a:prstGeom prst=\"line\"><a:avLst/></a:prstGeom><a:ln w=\"15875\">{Red}{ends}</a:ln></xdr:spPr></xdr:cxnSp><xdr:clientData/></xdr:twoCellAnchor>";
        Assert.Equal("strike", Decoration(Extract(Workbook("zerosize.xlsx", Items(FiveItems), Zero(2, ""))), "A3"));
        Assert.Equal("none", Decoration(Extract(Workbook("zerosizearrow.xlsx", Items(FiveItems), Zero(2, "<a:tailEnd type=\"triangle\"/>"))), "A3"));

        // LibreOffice's: a straight connector with the line's place on the sheet in a:off, a hair of height (360 EMU),
        // a round join, and a length and width on its arrowhead, in a two cell anchor of editAs "twoCell".
        string Saved(int id, string ends) =>
            "<xdr:twoCellAnchor editAs=\"twoCell\"><xdr:from><xdr:col>0</xdr:col><xdr:colOff>25200</xdr:colOff><xdr:row>2</xdr:row>" +
            $"<xdr:rowOff>{Emu(StrikeY)}</xdr:rowOff></xdr:from><xdr:to><xdr:col>0</xdr:col><xdr:colOff>{25200 + Emu(44)}</xdr:colOff><xdr:row>2</xdr:row>" +
            $"<xdr:rowOff>{Emu(StrikeY)}</xdr:rowOff></xdr:to><xdr:cxnSp><xdr:nvCxnSpPr><xdr:cNvPr id=\"{id}\" name=\"Line {id}\"></xdr:cNvPr><xdr:cNvCxnSpPr></xdr:cNvCxnSpPr></xdr:nvCxnSpPr>" +
            $"<xdr:spPr><a:xfrm><a:off x=\"25200\" y=\"{Emu(48 + StrikeY)}\"/><a:ext cx=\"{Emu(44)}\" cy=\"360\"/></a:xfrm><a:prstGeom prst=\"straightConnector1\"><a:avLst/></a:prstGeom>" +
            $"<a:ln w=\"15875\">{Red}<a:round/>{ends}</a:ln></xdr:spPr></xdr:cxnSp><xdr:clientData/></xdr:twoCellAnchor>";
        Assert.Equal("strike", Decoration(Extract(Workbook("saved.xlsx", Items(FiveItems), Saved(2, ""))), "A3"));
        Assert.Equal("none", Decoration(Extract(Workbook("savedarrow.xlsx", Items(FiveItems), Saved(2, "<a:tailEnd len=\"med\" type=\"triangle\" w=\"med\"/>"))), "A3"));
    }

    [Fact]
    public void A_line_in_mc_AlternateContent_inside_its_anchor_is_placed_by_the_anchor_and_counted_once()
    {
        // Real files keep a shape's a:off in sheet coordinates, far from the anchor's own cell: the anchor
        // places the shape, as it does when nothing wraps it.
        var choice = LineShape(2, 44, offset: "x=\"1600200\" y=\"609600\"");
        var fallback = LineShape(3, 44, offset: "x=\"1600200\" y=\"609600\"");
        var drawing = AnchorFrom(0, 2, 0, StrikeY, 44, 0,
            $"<mc:AlternateContent><mc:Choice Requires=\"a14\">{choice}</mc:Choice><mc:Fallback>{fallback}</mc:Fallback></mc:AlternateContent>");
        var extraction = Extract(Workbook("alternateinner.xlsx", Items(FiveItems), drawing));
        Assert.Equal("strike", Decoration(extraction, "A3"));
        Assert.Single(Assert.Single(extraction.Worksheets).DrawingShapes!);
        Assert.False(IsRetained(extraction));
    }

    [Fact]
    public void A_line_under_mc_AlternateContent_is_read_and_counted_once()
    {
        // The choice and its fallback each carry the line; only the choice is read.
        string Anchor(int id) => Line(id, 0, 2, 0, StrikeY, 44);
        var drawing = $"<mc:AlternateContent><mc:Choice Requires=\"a14\">{Anchor(2)}</mc:Choice><mc:Fallback>{Anchor(3)}</mc:Fallback></mc:AlternateContent>";
        var extraction = Extract(Workbook("alternate.xlsx", Items(FiveItems), drawing));
        Assert.Equal("strike", Decoration(extraction, "A3"));
        Assert.Single(Assert.Single(extraction.Worksheets).DrawingShapes!);
        Assert.False(IsRetained(extraction));
    }

    // ---- where the text is: alignment, overflow, merging, wrapping ---------------------------------------

    [Fact]
    public async Task Centered_text_is_struck_where_it_lies_not_across_the_cell()
    {
        // "Banana" centered in the 126 pt column A spans about 45-81 pt of it.
        var sheet = Row(1, Text("A1", "Item", 1)) + Row(2, Text("A2", "Banana", 2)) + Row(3, Text("A3", "Cherry", 2));
        var onText = Workbook("centered.xlsx", sheet, Line(2, 0, 1, 0, StrikeY, ColumnA));
        var (markdown, _) = await Export(onText);
        Assert.Contains("\n~~Banana~~\n\nCherry\n", markdown, StringComparison.Ordinal);
        // The same line over a stretch with no text under it, the left 30 pt of the cell, strikes nothing.
        var beside = Extract(Workbook("centeredbeside.xlsx", sheet, Line(2, 0, 1, 0, StrikeY, 30)));
        Assert.Equal("none", Decoration(beside, "A2"));
        // And a line over only the first half of the word is a partial strike.
        var half = Extract(Workbook("centeredhalf.xlsx", sheet, Line(2, 0, 1, 0, StrikeY, 63)));
        Assert.Equal("unplaced", Decoration(half, "A2"));
    }

    [Fact]
    public void Right_aligned_text_is_struck_where_it_lies()
    {
        // "1,200" right aligned in the 63 pt column B: its text spans about 34-61 pt of the cell.
        var sheet = Row(1, Text("A1", "Item", 1), Text("B1", "Amount", 1)) + Row(2, Text("A2", "Rent"), Text("B2", "1,200", 3));
        var onText = Extract(Workbook("right.xlsx", sheet, Line(2, 1, 1, 30, StrikeY, 33)));
        Assert.Equal("strike", Decoration(onText, "B2"));
        var beside = Extract(Workbook("rightbeside.xlsx", sheet, Line(2, 1, 1, 0, StrikeY, 25)));
        Assert.Equal("none", Decoration(beside, "B2"));
    }

    [Fact]
    public void Text_runs_on_into_the_empty_cells_beside_it_and_stops_at_the_first_that_holds_something()
    {
        // The text is about 240 pt long and its cell 126 pt wide: past the cell it shows over B and C when
        // they are empty (row 2), and is cut off at B when B holds something (row 3).
        var text = "Cherry pie with cream and a very long description";
        var sheet = Row(1, Text("A1", "Item", 1), Text("B1", "Note", 1)) + Row(2, Text("A2", text)) + Row(3, Text("A3", text), Text("B3", "x"));
        var toTheEdge = Extract(Workbook("overflow.xlsx", sheet, Line(2, 0, 1, 0, StrikeY, ColumnA) + Line(3, 0, 2, 0, StrikeY, ColumnA)));
        // A line that stops at the cell's edge crosses only part of the text that shows past it...
        Assert.Equal("unplaced", Decoration(toTheEdge, "A2"));
        // ...but all of the text that shows when the neighbor cuts it off.
        Assert.Equal("strike", Decoration(toTheEdge, "A3"));
        // A line that runs on to the end of the text strikes all of row 2's.
        var toTheEnd = Extract(Workbook("overflowwhole.xlsx", sheet, Line(2, 0, 1, 0, StrikeY, 215)));
        Assert.Equal("strike", Decoration(toTheEnd, "A2"));
    }

    [Fact]
    public void Right_aligned_text_runs_on_to_the_left_into_empty_cells_and_stops_at_one_that_holds_something()
    {
        // About 183 pt of text, right aligned in the 63 pt column B: it shows over A when A is empty (row 2),
        // and is cut off at A's edge when A holds something (row 3).
        var text = "Right aligned long text that overflows";
        var sheet = Row(1, Text("A1", "Item", 1), Text("B1", "Note", 1)) + Row(2, Text("B2", text, 3)) + Row(3, Text("A3", "x"), Text("B3", text, 3));
        var overColumnB = Extract(Workbook("rightrun.xlsx", sheet, Line(2, 1, 1, 0, StrikeY, ColumnB) + Line(3, 1, 2, 0, StrikeY, ColumnB)));
        Assert.Equal("unplaced", Decoration(overColumnB, "B2"));
        Assert.Equal("strike", Decoration(overColumnB, "B3"));
        var acrossAll = Extract(Workbook("rightrunwhole.xlsx", sheet, Line(2, 0, 1, 0, StrikeY, ColumnA + ColumnB)));
        Assert.Equal("strike", Decoration(acrossAll, "B2"));
    }

    [Fact]
    public void Centered_text_runs_on_to_both_sides_into_empty_cells_and_stops_at_one_that_holds_something()
    {
        // About 229 pt of text centered on column B (157 pt): row 2 shows it from 43 to 272 pt over A, B and C,
        // row 3 has something in A, so it is cut off there at 126 pt.
        var text = "Centered long text overflowing both sides here";
        var sheet = Row(1, Text("A1", "Item", 1), Text("B1", "Note", 1)) + Row(2, Text("B2", text, 2)) + Row(3, Text("A3", "x"), Text("B3", text, 2));
        var overColumnB = Extract(Workbook("centerrun.xlsx", sheet, Line(2, 1, 1, 0, StrikeY, ColumnB) + Line(3, 1, 2, 0, StrikeY, ColumnB)));
        Assert.Equal("unplaced", Decoration(overColumnB, "B2"));
        Assert.Equal("unplaced", Decoration(overColumnB, "B3"));
        var across = Extract(Workbook("centerrunwhole.xlsx", sheet, Line(2, 0, 1, 40, StrikeY, 235) + Line(3, 1, 2, 0, StrikeY, 190)));
        Assert.Equal("strike", Decoration(across, "B2"));
        Assert.Equal("strike", Decoration(across, "B3"));
    }

    [Fact]
    public async Task A_merged_cell_is_struck_over_the_whole_merged_area()
    {
        var sheet = Row(1, Text("A1", "Cancelled order #12", 2), Text("B1", "", 2), Text("C1", "", 2)) + Row(2, Text("A2", "Item", 1), Text("B2", "Qty", 1), Text("C2", "Price", 1)) +
            Row(3, Text("A3", "Apple"), Number("B3", "10"), Number("C3", "120"));
        var merge = "<mergeCells count=\"1\"><mergeCell ref=\"A1:C1\"/></mergeCells>";
        // The title is centered in the 252 pt merged area, about 95 pt wide: a line across the whole area covers it.
        var source = Workbook("merged.xlsx", sheet, Line(2, 0, 0, 0, StrikeY, ColumnA + ColumnB + ColumnC), mergeCells: merge);
        var (markdown, _) = await Export(source);
        Assert.Contains("# ~~Cancelled order \\#12~~\n", markdown, StringComparison.Ordinal);
        // A line over the first column only, 126 pt of the area, ends short of the centered text: a partial strike.
        var partial = Extract(Workbook("mergedpart.xlsx", sheet, Line(2, 0, 0, 0, StrikeY, 100), mergeCells: merge));
        Assert.Equal("unplaced", Decoration(partial, "A1"));
    }

    [Fact]
    public void A_line_in_the_last_row_of_a_merged_area_strikes_its_text()
    {
        // The merged area spans three rows (A1:A3, centered text bottom aligned in 72 pt): the line is in the last
        // row, two rows below the cell that holds the text.
        var sheet = Row(1, Text("A1", "Banana", 2)) + Row(2, Text("A2", "")) + Row(3, Text("A3", ""));
        var merge = "<mergeCells count=\"1\"><mergeCell ref=\"A1:A3\"/></mergeCells>";
        var extraction = Extract(Workbook("tall.xlsx", sheet, Line(2, 0, 2, 40, StrikeY, 46), mergeCells: merge));
        Assert.Equal("strike", Decoration(extraction, "A1"));
        // The same line in the middle row is 24 pt above the text: far over it.
        Assert.Equal("none", Decoration(Extract(Workbook("tallmiddle.xlsx", sheet, Line(2, 0, 1, 40, StrikeY, 46), mergeCells: merge)), "A1"));
    }

    [Fact]
    public void Wrapped_text_that_fits_one_line_is_judged_like_any_other()
    {
        // A 40 pt row: the text sits at its bottom, baseline 36.4 pt down, so a strikethrough is at 33 pt.
        var sheet = Row(1, 40, Text("A1", "Banana", 4));
        var extraction = Extract(Workbook("wrapone.xlsx", sheet, Line(2, 0, 0, 0, 33, 44)));
        Assert.Equal("strike", Decoration(extraction, "A1"));
    }

    [Fact]
    public async Task A_line_through_one_line_of_wrapped_text_is_a_partial_strike()
    {
        // About 150 pt of text in 123 pt: two lines, the last baseline 36.4 pt down and the first 13.75 pt above it.
        var sheet = Row(1, 40, Text("A1", "Cherry pie with cream and a bit", 4));
        var lastLine = Workbook("wraplast.xlsx", sheet, Line(2, 0, 0, 0, 33, ColumnA));
        var extraction = Extract(lastLine);
        Assert.Equal("unplaced", Decoration(extraction, "A1"));
        var firstLine = Extract(Workbook("wrapfirst.xlsx", sheet, Line(2, 0, 0, 0, 19.3, ColumnA)));
        Assert.Equal("unplaced", Decoration(firstLine, "A1"));
        // Between the two lines there is no text to cross.
        var between = Extract(Workbook("wrapbetween.xlsx", sheet, Line(2, 0, 0, 0, 27.5, ColumnA)));
        Assert.Equal("none", Decoration(between, "A1"));
        var (markdown, _) = await Export(lastLine);
        Assert.Contains("Cherry pie with cream and a bit", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_aligned_to_the_top_or_the_middle_of_a_tall_row_is_found_there()
    {
        var styles = Styles.Replace("</cellXfs>", "<xf fontId=\"0\"><alignment vertical=\"top\"/></xf><xf fontId=\"0\"><alignment vertical=\"center\"/></xf></cellXfs>", StringComparison.Ordinal);
        // 60 pt rows. Top: baseline 10.5 pt down (0.95 em), strike at 7.5. Center: baseline 33.3 pt down, strike at 30.
        var sheet = Row(1, 60, Text("A1", "Banana", 7), Text("B1", "Banana", 8));
        var top = Extract(Workbook("top.xlsx", sheet, Line(2, 0, 0, 0, 7.5, 44), styles: styles));
        Assert.Equal("strike", Decoration(top, "A1"));
        Assert.Equal("none", Decoration(top, "B1"));
        var middle = Extract(Workbook("middle.xlsx", sheet, Line(2, 1, 0, 0, 30, 44), styles: styles));
        Assert.Equal("strike", Decoration(middle, "B1"));
        Assert.Equal("none", Decoration(middle, "A1"));
        // The bottom-aligned reading of the same sheet would put a strikethrough near 55 pt.
        var bottomLine = Extract(Workbook("notbottom.xlsx", sheet, Line(2, 0, 0, 0, 55, 44), styles: styles));
        Assert.Equal("none", Decoration(bottomLine, "A1"));
        // An underline of the top-aligned text is just under its baseline (12 pt, 0.14 em below it); a line
        // 20 pt down, 0.9 em below the baseline, is under nothing.
        Assert.Equal("underline", Decoration(Extract(Workbook("topunder.xlsx", sheet, Line(2, 0, 0, 0, 12, 44), styles: styles)), "A1"));
        Assert.Equal("none", Decoration(Extract(Workbook("toplow.xlsx", sheet, Line(2, 0, 0, 0, 20, 44), styles: styles)), "A1"));
    }

    [Fact]
    public void Text_with_an_indent_starts_further_in()
    {
        var styles = Styles.Replace("</cellXfs>", "<xf fontId=\"0\"><alignment horizontal=\"left\" indent=\"3\"/></xf></cellXfs>", StringComparison.Ordinal);
        // Three steps of 0.55 em at 11 pt put "Banana" at 19.7-56 pt: a line from 25 pt covers it, as one from 0 does.
        var sheet = Row(1, Text("A1", "Banana", 7));
        Assert.Equal("strike", Decoration(Extract(Workbook("indent.xlsx", sheet, Line(2, 0, 0, 25, StrikeY, 44), styles: styles)), "A1"));
        // Without the indent the same line would start 23 pt into the text.
        Assert.Equal("unplaced", Decoration(Extract(Workbook("noindent.xlsx", Row(1, Text("A1", "Banana")), Line(2, 0, 0, 25, StrikeY, 44))), "A1"));
    }

    [Fact]
    public void Rotated_text_is_not_placed()
    {
        var styles = Styles.Replace("</cellXfs>", "<xf fontId=\"0\"><alignment textRotation=\"90\"/></xf></cellXfs>", StringComparison.Ordinal);
        var extraction = Extract(Workbook("rotatedtext.xlsx", Row(1, 60, Text("A1", "Banana", 7)), Line(2, 0, 0, 0, 55, 44), styles: styles));
        Assert.Equal("none", Decoration(extraction, "A1"));
    }

    [Fact]
    public void Text_centered_across_selected_cells_is_struck_where_it_lies()
    {
        var styles = Styles.Replace("</cellXfs>", "<xf fontId=\"0\"><alignment horizontal=\"centerContinuous\"/></xf></cellXfs>", StringComparison.Ordinal);
        // "Quarterly report" (about 82 pt) centered over A1:C1 (252 pt wide) spans 85-167 pt.
        var sheet = Row(1, Text("A1", "Quarterly report", 7), "<c r=\"B1\" s=\"7\"/>", "<c r=\"C1\" s=\"7\"/>") + Row(2, Text("A2", "Item", 1));
        Assert.Equal("strike", Decoration(Extract(Workbook("across.xlsx", sheet, Line(2, 0, 0, 80, StrikeY, 92), styles: styles)), "A1"));
        // A line over the first column only (126 pt) reaches into the text but not to its end.
        Assert.Equal("unplaced", Decoration(Extract(Workbook("acrosspart.xlsx", sheet, Line(2, 0, 0, 0, StrikeY, 126), styles: styles)), "A1"));
    }

    [Fact]
    public void Larger_text_has_its_strikethrough_higher()
    {
        // 16 pt text, bottom aligned in a 24 pt row: baseline at 24 - 0.33 * 16 = 18.7 pt down; 0.3 em is 4.8 pt above it.
        var sheet = Row(1, Text("A1", "Banana", 6));
        var struck = Extract(Workbook("big.xlsx", sheet, Line(2, 0, 0, 0, 13.9, 70)));
        Assert.Equal("strike", Decoration(struck, "A1"));
        // 17 pt down, the strikethrough height of 11 pt text, is only 1.7 pt (0.1 em) above this text's baseline.
        var low = Extract(Workbook("biglow.xlsx", sheet, Line(2, 0, 0, 0, StrikeY, 70)));
        Assert.Equal("underline", Decoration(low, "A1"));
    }

    // ---- sheets with and without a table, and the overlay lines that are not decorations ------------------

    private const string ScheduleSheet = """
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <cols><col min="1" max="1" width="8.43" customWidth="1"/><col min="2" max="2" width="14" customWidth="1"/><col min="3" max="3" width="10" customWidth="1"/><col min="4" max="9" width="8" customWidth="1"/></cols>
          <sheetData>
            <row r="1" ht="15" customHeight="1" />
            <row r="2" ht="24" customHeight="1"><c r="B2" t="inlineStr"><is><t>工程</t></is></c><c r="C2" t="inlineStr"><is><t>担当</t></is></c><c r="D2" t="inlineStr"><is><t>9/1</t></is></c><c r="E2" t="inlineStr"><is><t>9/2</t></is></c><c r="F2" t="inlineStr"><is><t>9/3</t></is></c><c r="G2" t="inlineStr"><is><t>9/4</t></is></c><c r="H2" t="inlineStr"><is><t>9/5</t></is></c><c r="I2" t="inlineStr"><is><t>9/8</t></is></c></row>
            <row r="3" ht="24" customHeight="1"><c r="B3" t="inlineStr"><is><t>要件定義</t></is></c><c r="C3" t="inlineStr"><is><t>山田</t></is></c><c r="D3"/><c r="E3"/><c r="F3"/><c r="G3"/><c r="H3"/><c r="I3"/></row>
            <row r="4" ht="24" customHeight="1"><c r="B4" t="inlineStr"><is><t>設計</t></is></c><c r="C4" t="inlineStr"><is><t>佐藤</t></is></c><c r="D4"/><c r="E4"/><c r="F4"/><c r="G4"/><c r="H4"/><c r="I4"/></row>
            <row r="5" ht="24" customHeight="1"><c r="B5" t="inlineStr"><is><t>実装</t></is></c><c r="C5" t="inlineStr"><is><t>鈴木</t></is></c><c r="D5"/><c r="E5"/><c r="F5"/><c r="G5"/><c r="H5"/><c r="I5"/></row>
          </sheetData>
          <drawing r:id="rDrawing" />
        </worksheet>
        """;

    // The schedule sheet's drawing package (the shared helper writes the worksheet itself from sheetData).
    private async Task<(string Markdown, XlsxExtractionResult Extraction)> Schedule(string name, string drawing, string worksheet = ScheduleSheet)
    {
        var path = Path.Combine(root, name);
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
            ["xl/workbook.xml"] = $"<workbook xmlns=\"{SpreadsheetNs}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"スケジュール\" sheetId=\"1\" r:id=\"rId1\" /></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"worksheet\" Target=\"worksheets/sheet1.xml\" /></Relationships>",
            ["xl/worksheets/sheet1.xml"] = worksheet,
            ["xl/worksheets/_rels/sheet1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rDrawing\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"/xl/drawings/drawing1.xml\" /></Relationships>",
            ["xl/drawings/drawing1.xml"] = $"<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">{drawing}</xdr:wsDr>",
        };
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (entry, text) in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        var (markdown, _) = await Export(path);
        return (markdown, Extract(path));
    }

    [Fact]
    public async Task A_line_over_empty_schedule_cells_stays_a_line_symbol_as_before()
    {
        // Column D of the schedule starts 170.25 pt from the left and each of D-I is 42 pt wide: 80 pt from 2 pt in
        // is D3 and E3, with nothing in them to strike.
        var (markdown, extraction) = await Schedule("schedule-line.xlsx", Line(2, 3, 2, 2, 12, 80));
        Assert.Contains("| 要件定義 | 山田 | ── | ── |", markdown, StringComparison.Ordinal);
        Assert.Single(extraction.Graph.Nodes, node => node.Kind == NodeKind.Shape);
    }

    [Fact]
    public async Task A_line_through_the_label_text_of_a_schedule_row_strikes_it()
    {
        // Through 要件定義 (B3: four full-width characters, 44 pt) and 山田 (C3: 22 pt), at the height of a strikethrough.
        var (markdown, extraction) = await Schedule("schedule-strike.xlsx", Line(2, 1, 2, 0, StrikeY, 110));
        Assert.Contains("| ~~要件定義~~ | ~~山田~~ |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("─", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Kind == NodeKind.Shape);
    }

    [Fact]
    public async Task A_line_through_text_in_the_schedule_grid_strikes_it_and_adds_no_symbol_to_the_cells_it_passes()
    {
        // 要確認 in D3 (three full-width characters, 33 pt): a line across D3:E3 could be the line symbol of a
        // table overlay (row label to the left, dates above), and is the strikethrough of the text instead.
        var worksheet = ScheduleSheet.Replace("<c r=\"D3\"/>", "<c r=\"D3\" t=\"inlineStr\"><is><t>要確認</t></is></c>", StringComparison.Ordinal);
        var (markdown, extraction) = await Schedule("schedule-grid.xlsx", Line(2, 3, 2, 2, StrikeY, 80), worksheet);
        Assert.Contains("| 要件定義 | 山田 | ~~要確認~~ |  |  |  |  |  |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("─", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Kind == NodeKind.Shape);
        // The same line a little higher, over the letters, is no decoration and stays the overlay it was.
        var (above, overlay) = await Schedule("schedule-grid-above.xlsx", Line(2, 3, 2, 2, OverlineY, 80), worksheet);
        Assert.DoesNotContain("~~", above, StringComparison.Ordinal);
        Assert.Contains("要確認<br>──", above, StringComparison.Ordinal);
        Assert.Single(overlay.Graph.Nodes, node => node.Kind == NodeKind.Shape);
    }

    [Fact]
    public async Task An_arrow_over_text_stays_an_arrow_symbol_and_strikes_nothing()
    {
        // An arrow from B3 to C3 (20 and 80) at the height of a strikethrough: a table overlay, as before.
        var source = Workbook("arrow-overlay.xlsx", Items(FiveItems), Line(2, 1, 2, 0, StrikeY, 120, ends: "<a:tailEnd type=\"triangle\"/>"));
        var (markdown, _) = await Export(source);
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);
        Assert.Contains("| Banana | 20<br>━━ | 80<br>━━▶ |", markdown, StringComparison.Ordinal);
        Assert.Equal("none", Decoration(Extract(source), "B3"));
    }

    [Fact]
    public async Task A_line_in_a_sheet_that_is_not_table_like_still_strikes()
    {
        // Three notes and no table: no header row, no label column, so no overlay could ever apply.
        var sheet = Row(1, Text("A1", "Memo")) + Row(3, Text("A3", "Delivery on Friday")) + Row(5, Text("A5", "Call the office"));
        var source = Workbook("notes.xlsx", sheet, Line(2, 0, 2, 0, StrikeY, 100));
        var (markdown, _) = await Export(source);
        Assert.Contains("~~Delivery on Friday~~", markdown, StringComparison.Ordinal);
        Assert.Contains("Call the office", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("~~Call", markdown, StringComparison.Ordinal);
    }

    // ---- determinism -------------------------------------------------------------------------------------

    [Fact]
    public void Extraction_is_deterministic()
    {
        var drawing = Line(2, 0, 2, 0, StrikeY, 44) + Line(3, 0, 4, 0, UnderlineY, 30) + Line(4, 0, 3, 0, StrikeY, 28) + Line(5, 0, 5, 0, StrikeY - 1.5, 62) + Line(6, 0, 5, 0, StrikeY + 1.5, 62);
        var source = Workbook("deterministic.xlsx", Items(FiveItems), drawing);
        string Serialize() => JsonSerializer.Serialize(Extract(source).Graph);
        Assert.Equal(Serialize(), Serialize());
    }
}
