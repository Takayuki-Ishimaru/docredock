using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Xlsx;
using DocRedock.Markdown;

namespace DocRedock.Tests.Xlsx;

/// <summary>
/// What a sheet costs must follow what is in it, not what its parts multiply to. A sheet drawn in thousands of merged cells
/// (Japanese "Excel graph paper"), a banner or a value merged across the whole width in every one of thousands of rows, a long
/// list with thousands of shapes drawn on it, and a damaged drawing whose numbers are at the limit of a long are all converted
/// here under a time limit, so that a regression fails by name instead of keeping the suite waiting. Where the time of the
/// right answer and of the wrong one are too close to tell apart on any machine, the bytes allocated are counted instead.
/// </summary>
public sealed class ExcelMergeScaleTests : IDisposable
{
    // The conversions take a few seconds at most; the quadratic ones they stand for take close to a minute.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private readonly string root = Directory.CreateTempSubdirectory("docredock-merge-scale-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);

    public void Dispose()
    {
        try { Directory.Delete(root, true); }
        catch (IOException) { /* a conversion that outlived its test (a regression) may still hold a file */ }
    }

    private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const long Megabyte = 1024 * 1024;

    // Cell styles: 0 plain, 1 bold, 2 plain (the blank cells of graph paper).
    private const string Styles =
        "<fonts><font><sz val=\"11\"/></font><font><b/><sz val=\"11\"/></font></fonts>" +
        "<cellXfs><xf fontId=\"0\"/><xf fontId=\"1\"/><xf fontId=\"0\"/></cellXfs>";

    // ---- workbooks ----------------------------------------------------------------------------------------

    private static string ColumnName(int column)
    {
        var name = new StringBuilder();
        for (; column > 0; column = (column - 1) / 26) name.Insert(0, (char)('A' + (column - 1) % 26));
        return name.ToString();
    }

    private static string Text(string reference, string text, int style = 0) =>
        $"<c r=\"{reference}\" s=\"{style}\" t=\"inlineStr\"><is><t>{text}</t></is></c>";

    private static string Number(string reference, string value) => $"<c r=\"{reference}\"><v>{value}</v></c>";

    private static string Blank(string reference, int style = 0) => $"<c r=\"{reference}\" s=\"{style}\"/>";

    private static string Row(int number, double height, params string[] cells) =>
        $"<row r=\"{number}\" ht=\"{height.ToString(CultureInfo.InvariantCulture)}\" customHeight=\"1\">{string.Concat(cells)}</row>";

    private static string Row(int number, params string[] cells) => Row(number, 24, cells);

    private static string Merge(IEnumerable<string> ranges)
    {
        var all = ranges.ToArray();
        return $"<mergeCells count=\"{all.Length}\">{string.Concat(all.Select(range => $"<mergeCell ref=\"{range}\"/>"))}</mergeCells>";
    }

    private const string ItemColumns = "<cols><col min=\"1\" max=\"1\" width=\"24\" customWidth=\"1\"/><col min=\"2\" max=\"3\" width=\"12\" customWidth=\"1\"/></cols>";

    private sealed record Sheet(string Name, string Data, string Merges = "", string Drawing = "", string Columns = "");

    private string Workbook(string name, params Sheet[] sheets)
    {
        var path = Path.Combine(root, name + ".xlsx");
        var parts = new List<(string Entry, string Text)>
        {
            ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />"),
            ("xl/workbook.xml", $"<workbook xmlns=\"{SpreadsheetNs}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
                string.Concat(sheets.Select((sheet, index) => $"<sheet name=\"{sheet.Name}\" sheetId=\"{index + 1}\" r:id=\"rId{index + 1}\" />")) + "</sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                string.Concat(sheets.Select((_, index) => $"<Relationship Id=\"rId{index + 1}\" Type=\"worksheet\" Target=\"worksheets/sheet{index + 1}.xml\" />")) + "</Relationships>"),
            ("xl/styles.xml", $"<styleSheet xmlns=\"{SpreadsheetNs}\">{Styles}</styleSheet>"),
        };
        for (var index = 0; index < sheets.Length; index++)
        {
            var sheet = sheets[index];
            parts.Add(($"xl/worksheets/sheet{index + 1}.xml", $"<worksheet xmlns=\"{SpreadsheetNs}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                $"{sheet.Columns}<sheetData>{sheet.Data}</sheetData>{sheet.Merges}{(sheet.Drawing.Length == 0 ? "" : "<drawing r:id=\"rDrawing\" />")}</worksheet>"));
            if (sheet.Drawing.Length == 0) continue;
            parts.Add(($"xl/worksheets/_rels/sheet{index + 1}.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                $"<Relationship Id=\"rDrawing\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"/xl/drawings/drawing{index + 1}.xml\" /></Relationships>"));
            parts.Add(($"xl/drawings/drawing{index + 1}.xml", "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
                $"{sheet.Drawing}</xdr:wsDr>"));
        }
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
        return path;
    }

    private string Workbook(string name, string sheetData, string drawing = "", string mergeCells = "", string columns = ItemColumns) =>
        Workbook(name, new Sheet("Items", sheetData, mergeCells, drawing, columns));

    // ---- running a conversion under the limit -------------------------------------------------------------

    private async Task<T> WithinLimit<T>(string what, Func<CancellationToken, T> work)
    {
        // The token only stops a conversion that is slow but gets to a check; a quadratic loop never does, so the test stops waiting for
        // it at the limit, a little before the token.
        using var cancellation = new CancellationTokenSource(Limit + TimeSpan.FromSeconds(5));
        try { return await Task.Run(() => work(cancellation.Token)).WaitAsync(Limit); }
        catch (TimeoutException)
        {
            Assert.Fail($"{what} was still being converted after {Limit.TotalSeconds:0} seconds: something costs the product of the sizes in the sheet, not their sum.");
            throw;
        }
    }

    private async Task<(string Markdown, ReadableDocumentExportResult Result)> ConvertAsync(string source)
    {
        var output = Path.ChangeExtension(source, ".md");
        using var cancellation = new CancellationTokenSource(Limit + TimeSpan.FromSeconds(5));
        var conversion = Task.Run(() => service.ExportReadableAsync(new ReadableDocumentExportOptions(source, output, EnableOcr: false), cancellation.Token));
        try
        {
            var result = await conversion.WaitAsync(Limit);
            return ((await File.ReadAllTextAsync(output)).Replace("\r\n", "\n", StringComparison.Ordinal), result);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"{Path.GetFileName(source)} was still being converted after {Limit.TotalSeconds:0} seconds: something costs the product of the sizes in the sheet, not their sum.");
            throw;
        }
    }

    private Task<XlsxExtractionResult> ExtractAsync(string source) => WithinLimit(Path.GetFileName(source), token =>
    {
        using var stream = File.OpenRead(source);
        return new XlsxAdapter().Extract(stream, token);
    });

    // ---- drawings -----------------------------------------------------------------------------------------

    private const string Fill = "<a:solidFill><a:srgbClr val=\"4472C4\"/></a:solidFill>";

    /// <summary>A filled rectangle (a bar, when it has no text) from the top left corner of one cell to a point in another, both zero-based.</summary>
    private static string Bar(int id, int fromColumn, int fromRow, int toColumn, int toRow, long toColumnOffset, long toRowOffset) =>
        $"<xdr:twoCellAnchor><xdr:from><xdr:col>{fromColumn}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>{fromRow}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>" +
        $"<xdr:to><xdr:col>{toColumn}</xdr:col><xdr:colOff>{toColumnOffset}</xdr:colOff><xdr:row>{toRow}</xdr:row><xdr:rowOff>{toRowOffset}</xdr:rowOff></xdr:to>" +
        $"<xdr:sp macro=\"\" textlink=\"\"><xdr:nvSpPr><xdr:cNvPr id=\"{id}\" name=\"Shape {id}\"/><xdr:cNvSpPr/></xdr:nvSpPr><xdr:spPr><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>{Fill}</xdr:spPr></xdr:sp><xdr:clientData/></xdr:twoCellAnchor>";

    // ---- merges -------------------------------------------------------------------------------------------

    private const int GraphPaperRows = 2_000, GraphPaperBlocks = 32;

    // "Excel graph paper": a grid of small blank cells (96 across) in which every piece of text sits in three cells merged into one.
    private string GraphPaper()
    {
        var data = new StringBuilder();
        var merges = new List<string>();
        for (var row = 1; row <= GraphPaperRows; row++)
        {
            data.Append($"<row r=\"{row}\" ht=\"12\" customHeight=\"1\">");
            for (var block = 0; block < GraphPaperBlocks; block++)
            {
                var first = 1 + block * 3;
                data.Append(Text($"{ColumnName(first)}{row}", $"r{row}c{block + 1}"))
                    .Append(Blank($"{ColumnName(first + 1)}{row}", 2)).Append(Blank($"{ColumnName(first + 2)}{row}", 2));
                merges.Add($"{ColumnName(first)}{row}:{ColumnName(first + 2)}{row}");
            }
            data.Append("</row>");
        }
        return Workbook("graph-paper", data.ToString(), mergeCells: Merge(merges), columns: "");
    }

    [Fact]
    public async Task Sixty_four_thousand_merges_on_graph_paper_of_192_thousand_cells_convert_quickly()
    {
        // Each merge finds its origin cell among the 192,000: searching for it takes a step for every cell before it, which is
        // 64,000 times tens of thousands of steps in all. (Ten thousand merges on a hundred thousand cells already take seconds.)
        var source = GraphPaper();

        var (markdown, result) = await ConvertAsync(source);

        var rows = Enumerable.Range(1, GraphPaperRows).Select(row =>
            "- " + string.Join(" — ", Enumerable.Range(1, GraphPaperBlocks).Select(block => $"r{row}c{block}")));
        Assert.Equal("# r1c1\n\n## Items\n\n" + string.Join("\n\n", rows) + "\n", markdown);
        var merged = result.Graph.Nodes.Where(node => node.Extensions?.ContainsKey("merged_to_column") == true).ToArray();
        Assert.Equal(GraphPaperRows * GraphPaperBlocks, merged.Length);
        // The origin of a block holds the far corner of its merge.
        var origin = Assert.Single(merged, node => node.Extensions!["row"].GetInt32() == 1500 && node.Extensions!["column"].GetInt32() == 7);
        Assert.Equal((1500, 9), (origin.Extensions!["merged_to_row"].GetInt32(), origin.Extensions!["merged_to_column"].GetInt32()));
    }

    // ---- merges across the whole width --------------------------------------------------------------------

    private string LabelsAndWideMerges(string name, int rows)
    {
        var data = new StringBuilder(Row(1, Text("A1", "Specification", 1)));
        var merges = new List<string> { "A1:XFD1" };
        for (var row = 2; row <= rows + 1; row++)
        {
            data.Append(Row(row, Text($"A{row}", $"Label {row - 1}"), Text($"B{row}", $"Value {row - 1}")));
            merges.Add($"B{row}:XFD{row}");
        }
        return Workbook(name, data.ToString(), mergeCells: Merge(merges));
    }

    [Fact]
    public async Task Three_thousand_rows_of_a_label_and_a_value_merged_across_the_whole_width_convert_quickly()
    {
        // A row with an ordinary cell (the label) counts every column of its merged value as covered: 16,383 of them for each of 3,000 rows.
        var source = LabelsAndWideMerges("labels-and-wide-merges", 3000);

        var (markdown, _) = await ConvertAsync(source);

        Assert.StartsWith("# Specification\n\n## Items\n\n", markdown, StringComparison.Ordinal);
        Assert.Equal(3000, markdown.Split('\n').Count(line => line.StartsWith("- **Label ", StringComparison.Ordinal)));
        Assert.Contains("- **Label 3000**: Value 3000\n", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_thousand_sheets_with_a_merge_across_the_whole_width_allocate_nothing_for_the_width()
    {
        // The columns a merge spans must not be counted one by one: for each sheet that took a set of 16,383 numbers, half a megabyte
        // of garbage (over 500 MB for these sheets, against under 30 MB without it). Allocation does not depend on how busy the machine
        // is, and the time of the two differs by a factor of two or three only.
        var sheets = Enumerable.Range(1, 1000).Select(sheet => new Sheet($"S{sheet}",
            string.Concat(Enumerable.Range(1, 3).Select(row => Row(row, Text($"A{row}", $"Label {sheet}.{row}"), Text($"B{row}", $"Value {sheet}.{row}")))),
            Merge(Enumerable.Range(1, 3).Select(row => $"B{row}:XFD{row}")))).ToArray();
        var source = Workbook("thousand-sheets", sheets);

        var (markdown, allocated) = await WithinLimit("thousand-sheets.xlsx", token =>
        {
            using var stream = File.OpenRead(source);
            var graph = new XlsxAdapter().Extract(stream, token).Graph;
            var before = GC.GetAllocatedBytesForCurrentThread();
            var text = new ReadableMarkdownSerializer().Serialize(graph);
            return (text, GC.GetAllocatedBytesForCurrentThread() - before);
        });

        Assert.Equal(3000, markdown.Split('\n').Count(line => line.StartsWith("- **Label ", StringComparison.Ordinal)));
        Assert.Contains("- **Label 1000.3**: Value 1000.3\n", markdown.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.InRange(allocated, 0, 100 * Megabyte);
    }

    // ---- a long sheet with many shapes --------------------------------------------------------------------

    // A long list: the file records the height of every one of its rows (in three sizes, so where a row starts is the sum of all that
    // precede it), nearly all of them empty, and a bar is drawn over three day columns on some rows, with the days across a row three
    // above it and a name beside it.
    private static double HeightOf(int row) => row % 3 == 0 ? 18 : row % 7 == 0 ? 24 : 15;

    private string LongSchedule(string name, int rows, int bars, out int[] barRows)
    {
        barRows = Enumerable.Range(0, bars).Select(index => 10 + (int)((long)index * (rows - 20) / bars)).ToArray();
        var barRowSet = barRows.ToHashSet();
        var headerRowSet = barRows.Select(row => row - 3).ToHashSet();
        var data = new StringBuilder();
        for (var row = 1; row <= rows; row++)
            data.Append(
                barRowSet.Contains(row) ? Row(row, HeightOf(row), Text($"A{row}", $"Task {row}"), Blank($"B{row}"), Blank($"C{row}"), Blank($"D{row}"))
                : headerRowSet.Contains(row) ? Row(row, HeightOf(row), Text($"B{row}", "Mon", 1), Text($"C{row}", "Tue", 1), Text($"D{row}", "Wed", 1))
                : $"<row r=\"{row}\" ht=\"{HeightOf(row).ToString(CultureInfo.InvariantCulture)}\" customHeight=\"1\"/>");
        var drawing = string.Concat(barRows.Select((row, index) => Bar(index + 2, 1, row - 1, 3, row - 1, 561975, (long)Math.Round(HeightOf(row) * 12700))));
        return Workbook(name, data.ToString(), drawing);
    }

    [Fact]
    public async Task Six_hundred_bars_on_a_sheet_of_two_hundred_thousand_rows_are_found_on_their_rows_quickly()
    {
        // Every shape is placed by asking where rows far down the sheet start (a binary search for each of its edges, and each
        // step of that search adds up the heights of all the rows before it), and then asked what rows it covers.
        var source = LongSchedule("long-schedule", 200_000, 600, out var barRows);

        var (markdown, result) = await ConvertAsync(source);
        var overlays = result.Graph.Nodes
            .Where(node => node.Kind == NodeKind.Shape && node.Extensions!.ContainsKey("sheet_overlay"))
            .Select(node => node.Extensions!["sheet_overlay"].Deserialize<XlsxSheetOverlay>()!)
            .OrderBy(overlay => overlay.StartRow).ToArray();

        Assert.Equal(barRows.Length, overlays.Length);
        Assert.All(overlays.Zip(barRows), pair => Assert.Equal((pair.Second, pair.Second, 2, 4), (pair.First.StartRow, pair.First.EndRow, pair.First.StartColumn, pair.First.EndColumn)));
        Assert.Equal(barRows.Length * 3, markdown.Split("━━").Length - 1);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Message.Contains("retained but not projected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Where_a_row_or_a_column_starts_is_the_sum_of_the_sizes_before_it()
    {
        // 300,000 rows with a recorded height, some of them none at all (hidden), and 1,000 columns. Finding where a row starts takes a
        // step for each recorded row if they are added up every time, and finding which row a position lies in does that twenty times.
        var heights = new Dictionary<int, double>();
        for (var row = 1; row <= 300_000; row++) heights[row] = row % 11 == 0 ? 0 : row % 3 == 0 ? 18 : row % 7 == 0 ? 24.5 : 15;
        heights[-4] = 99; heights[0] = 99;
        var widths = new Dictionary<int, double>();
        for (var column = 1; column <= 1000; column++) widths[column] = column % 5 == 0 ? 0 : column % 2 == 0 ? 12 : 20.25;
        var metrics = new XlsxWorksheetMetrics(8.43, 15, widths, heights);

        // The answer, added up the long way once, row by row.
        var rowStart = new long[310_002];
        for (var row = 2; row < rowStart.Length; row++)
            rowStart[row] = rowStart[row - 1] + XlsxWorksheetMetrics.RowHeightToEmu(heights.TryGetValue(row - 1, out var height) ? height : 15);
        var columnStart = new long[1_003];
        for (var column = 2; column < columnStart.Length; column++)
            columnStart[column] = columnStart[column - 1] + XlsxWorksheetMetrics.ColumnWidthToEmu(widths.TryGetValue(column - 1, out var width) ? width : 8.43);

        var random = new Random(7);
        var checkedRows = await WithinLimit("the metrics of a sheet of 300,000 rows", _ =>
        {
            for (var round = 0; round < 3000; round++)
            {
                var row = random.Next(-2, 310_000);
                Assert.Equal(row <= 1 ? 0 : rowStart[row], metrics.RowStartEmu(row));
                var position = (long)(random.NextDouble() * rowStart[^1]);
                // The row the position lies in, and how far into it: the last row that starts at or before it (a hidden row starts
                // where the next one does).
                int low = 1, high = rowStart.Length - 1;
                while (low < high)
                {
                    var middle = low + (high - low + 1) / 2;
                    if (rowStart[middle] <= position) low = middle; else high = middle - 1;
                }
                var found = low;
                var size = Math.Max(1, XlsxWorksheetMetrics.RowHeightToEmu(heights.TryGetValue(found, out var rowHeight) ? rowHeight : 15));
                Assert.Equal(found + (position - rowStart[found]) / (double)size, metrics.RowFromEmu(position));
            }
            return true;
        });
        Assert.True(checkedRows);
        for (var column = 0; column < columnStart.Length; column++)
            Assert.Equal(column <= 1 ? 0 : columnStart[column], metrics.ColumnStartEmu(column));
        Assert.Equal(1.0, metrics.ColumnFromEmu(0));
        Assert.Equal(2.5, metrics.ColumnFromEmu(columnStart[2] + (columnStart[3] - columnStart[2]) / 2), 6);
    }

    // ---- a damaged drawing --------------------------------------------------------------------------------

    private const string MaxLong = "9223372036854775807", MinLong = "-9223372036854775808";

    private static string Shape(int id, string preset = "rect", string transform = "") =>
        $"<xdr:sp macro=\"\" textlink=\"\"><xdr:nvSpPr><xdr:cNvPr id=\"{id}\" name=\"Shape {id}\"/><xdr:cNvSpPr/></xdr:nvSpPr><xdr:spPr>{transform}<a:prstGeom prst=\"{preset}\"><a:avLst/></a:prstGeom>{Fill}</xdr:spPr></xdr:sp>";

    private static string OneCell(string shape, string width, string height, string columnOffset = "0", string rowOffset = "0") =>
        $"<xdr:oneCellAnchor><xdr:from><xdr:col>1</xdr:col><xdr:colOff>{columnOffset}</xdr:colOff><xdr:row>1</xdr:row><xdr:rowOff>{rowOffset}</xdr:rowOff></xdr:from><xdr:ext cx=\"{width}\" cy=\"{height}\"/>{shape}<xdr:clientData/></xdr:oneCellAnchor>";

    private static string Absolute(string shape, string x, string y, string width, string height) =>
        $"<xdr:absoluteAnchor><xdr:pos x=\"{x}\" y=\"{y}\"/><xdr:ext cx=\"{width}\" cy=\"{height}\"/>{shape}<xdr:clientData/></xdr:absoluteAnchor>";

    // A group placed at the origin of the sheet, its child scaled by the ratio of the group's extent to the extent of its children.
    private static string GroupWithChild(string extent, string childExtent, string childOffset) =>
        $"<xdr:absoluteAnchor><xdr:pos x=\"0\" y=\"0\"/><xdr:ext cx=\"{extent}\" cy=\"{extent}\"/><xdr:grpSp><xdr:nvGrpSpPr><xdr:cNvPr id=\"2\" name=\"Group\"/><xdr:cNvGrpSpPr/></xdr:nvGrpSpPr>" +
        $"<xdr:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{extent}\" cy=\"{extent}\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"{childExtent}\" cy=\"{childExtent}\"/></a:xfrm></xdr:grpSpPr>" +
        $"<xdr:sp macro=\"\" textlink=\"\"><xdr:nvSpPr><xdr:cNvPr id=\"3\" name=\"Child\"/><xdr:cNvSpPr/></xdr:nvSpPr><xdr:spPr><a:xfrm><a:off x=\"{childOffset}\" y=\"{childOffset}\"/><a:ext cx=\"10\" cy=\"10\"/></a:xfrm>" +
        $"<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>{Fill}</xdr:spPr></xdr:sp></xdr:grpSp><xdr:clientData/></xdr:absoluteAnchor>";

    public static TheoryData<string> DamagedDrawings => new()
    {
        "wide", "tall", "both", "line", "arrow", "far-offset", "far-position", "far-negative-position", "far-position-and-size", "scaled-group", "frame-size",
    };

    private static string DamagedDrawing(string kind) => kind switch
    {
        "wide" => OneCell(Shape(2), MaxLong, "190500"),
        "tall" => OneCell(Shape(2), "561975", MaxLong),
        "both" => OneCell(Shape(2), MaxLong, MaxLong),
        "line" => OneCell(Shape(2, "line"), MaxLong, "0"),
        "arrow" => OneCell(Shape(2, "rightArrow"), MaxLong, MaxLong),
        "far-offset" => OneCell(Shape(2), MaxLong, MaxLong, MaxLong, MaxLong),
        "far-position" => Absolute(Shape(2), MaxLong, MaxLong, "561975", "190500"),
        "far-negative-position" => Absolute(Shape(2), MinLong, MinLong, "561975", "190500"),
        "far-position-and-size" => Absolute(Shape(2), MaxLong, MaxLong, MaxLong, MaxLong),
        "scaled-group" => GroupWithChild(MaxLong, "1", "1000000"),
        "frame-size" => OneCell(Shape(2, transform: $"<a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{MaxLong}\" cy=\"{MaxLong}\"/></a:xfrm>"), "0", "0"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

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

    [Theory]
    [MemberData(nameof(DamagedDrawings))]
    public async Task A_drawing_with_numbers_at_the_limit_of_a_long_is_left_out_and_the_rest_of_the_sheet_stays(string kind)
    {
        // Nothing of a drawing like this can be placed: the shape stays in the workbook as one that was retained but not projected,
        // and the cells are converted as they are without it.
        var source = Workbook("damaged-" + kind, ItemTable, DamagedDrawing(kind));

        var (markdown, result) = await ConvertAsync(source);
        var shape = Assert.Single(Assert.Single((await ExtractAsync(source)).Worksheets).DrawingShapes!);

        Assert.Equal(ItemTableMarkdown, markdown);
        Assert.Null(shape.AbsoluteBounds);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("1 DrawingML shape(s) were retained", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_group_whose_extents_are_the_smallest_long_is_scaled_like_any_other_group()
    {
        // The scale is the group's extent over the extent of its children: -2^63 over -2^63, which a test for a nonzero extent must not
        // take the absolute value of (there is none). The child is placed at its offset, a tiny shape in the corner of the sheet.
        var source = Workbook("group-of-the-smallest-extents", ItemTable, GroupWithChild(MinLong, MinLong, "100"));

        var (markdown, result) = await ConvertAsync(source);
        var shape = Assert.Single(Assert.Single((await ExtractAsync(source)).Worksheets).DrawingShapes!);

        Assert.Equal(ItemTableMarkdown, markdown);
        Assert.Equal(new XlsxDrawingBounds(100, 100, 10, 10), shape.AbsoluteBounds);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("1 DrawingML shape(s) were retained", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fifty_damaged_drawings_beside_a_bar_over_a_schedule_leave_the_bar_and_the_cells()
    {
        var schedule =
            Row(1, Blank("A1"), Text("B1", "Mon", 1), Text("C1", "Tue", 1), Text("D1", "Wed", 1)) +
            Row(2, Text("A2", "Design"), Blank("B2"), Blank("C2"), Blank("D2")) +
            Row(3, Text("A3", "Build"), Blank("B3"), Blank("C3"), Blank("D3"));
        var damaged = string.Concat(Enumerable.Range(3, 50).Select(id => OneCell(Shape(id), (long.MaxValue - id).ToString(CultureInfo.InvariantCulture), "190500")));
        var source = Workbook("damaged-beside-a-bar", schedule, Bar(2, 1, 1, 3, 1, 561975, 190500) + damaged);

        var (markdown, result) = await ConvertAsync(source);
        var shapes = Assert.Single((await ExtractAsync(source)).Worksheets).DrawingShapes!;

        // The bar over Design is found where it is; the fifty others are retained, with no bounds to place them by.
        Assert.Equal(
            "# ドキュメント\n\n## Items\n\n" +
            "|  | Mon | Tue | Wed |\n" +
            "| --- | --- | --- | --- |\n" +
            "| Design | ━━ | ━━ | ━━ |\n\n" +
            "Build\n", markdown);
        Assert.Equal(51, shapes.Count);
        Assert.Equal(["2"], shapes.Where(shape => shape.AbsoluteBounds is not null).Select(shape => shape.Id));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("50 DrawingML shape(s) were retained", StringComparison.Ordinal));
    }
}
