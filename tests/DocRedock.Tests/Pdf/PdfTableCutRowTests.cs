using System.Text;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;
using DocRedock.Markdown;
using static DocRedock.Tests.Pdf.MeasuredPdf;

namespace DocRedock.Tests.Pdf;

/// <summary>A ruled table whose last row on a page is cut by the page end: the row lacks its inner column rules and is
/// still several cells, a real merged cell stays merged, and a row split by the page break is one row again when the
/// table goes on.</summary>
public sealed class PdfTableCutRowTests
{
    private static string Text(double x, double y, string text) => $"BT /F1 10 Tf 1 0 0 1 {N(x)} {N(y)} Tm ({text}) Tj ET";

    /// <summary>A three-column table from x 50 to 350 (rules at 150 and 250) between <paramref name="bottom"/> and
    /// <paramref name="top"/>; rows are 40 high from the top, and the last row, whatever is left of it, has no inner
    /// column rules when <paramref name="cutLast"/> is set. Row texts are placed at the left of their cells.</summary>
    private static string Table(double top, double bottom, string[][] rows, bool cutLast, int[]? lostRules = null, params string[] extraText)
    {
        var content = new StringBuilder($"50 {N(bottom)} 300 {N(top - bottom)} re S\n");
        var lastTop = top - 40 * (rows.Length - 1);
        for (var row = 1; row < rows.Length; row++) content.Append($"50 {N(top - 40 * row)} m 350 {N(top - 40 * row)} l S\n");
        foreach (var x in new[] { 150, 250 })
            content.Append($"{x} {N(cutLast && (lostRules ?? [150, 250]).Contains(x) ? lastTop : bottom)} m {x} {N(top)} l S\n");
        for (var row = 0; row < rows.Length; row++)
            for (var column = 0; column < 3; column++)
                if (rows[row][column].Length > 0)
                    content.Append(Text(60 + column * 100, top - 40 * row - 25, rows[row][column])).Append('\n');
        foreach (var text in extraText) content.Append(text).Append('\n');
        return content.ToString();
    }

    private static DocumentGraph Graph(params string[] pages) =>
        PdfDocumentGraphProjection.CreateGraph(PdfTextExtractor.Extract(Document(pages)), "0123456789abcdef");

    private static string[][] Lines(string markdown) => markdown.Split('\n').Where(line => line.StartsWith('|') && !line.StartsWith("| ---"))
        .Select(line => line.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray()).ToArray();

    private static byte[] Document(params string[] pages)
    {
        var pdf = new StringBuilder("%PDF-1.4\n");
        pdf.Append("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        pdf.Append("2 0 obj << /Type /Pages /MediaBox [0 0 595 842] /Kids [")
            .Append(string.Join(" ", pages.Select((_, index) => $"{10 + index * 2} 0 R")))
            .Append($"] /Count {pages.Length} >> endobj\n");
        pdf.Append("4 0 obj ").Append(SimpleFont).Append(" endobj\n");
        for (var index = 0; index < pages.Length; index++)
        {
            var id = 10 + index * 2;
            pdf.Append($"{id} 0 obj << /Type /Page /Parent 2 0 R /Resources {Resources} /Contents {id + 1} 0 R >> endobj\n")
                .Append($"{id + 1} 0 obj ").Append(Stream(pages[index])).Append(" endobj\n");
        }
        return Encoding.Latin1.GetBytes(pdf.Append("%%EOF").ToString());
    }

    private static readonly string[] Header = ["Name", "Value", "Note"];
    private static readonly string[] First = ["alpha", "10", "first"];

    // A page cut through its last row: the frame and the row rules are there, the inner column rules of that row are not.
    private static string CutPage(params string[] lastRow) => Table(200, 60, [Header, First, lastRow], cutLast: true);

    [Fact]
    public void A_last_row_without_inner_rules_whose_text_sits_in_separate_columns_is_separate_cells()
    {
        var table = Assert.Single(PdfTextExtractor.Extract(Document(CutPage("beta", "20", "second"))).Tables![1]);
        Assert.Equal(3, table.Rows.Count);
        var cells = table.Rows[2].Cells.OrderBy(cell => cell.Column).ToArray();
        Assert.Equal(["beta", "20", "second"], cells.Select(cell => cell.Text));
        Assert.All(cells, cell => Assert.Equal(1, cell.ColumnSpan));
        Assert.True(table.BottomRowCut);
    }

    [Fact]
    public void A_real_merged_cell_in_the_last_row_stays_merged()
    {
        // One block of text under two columns, the rule between them gone.
        var table = Assert.Single(PdfTextExtractor.Extract(Document(Table(200, 60, [Header, First, ["total", "all values are provisional", ""]],
            cutLast: true, lostRules: [250]))).Tables![1]);
        var cells = table.Rows[2].Cells.OrderBy(cell => cell.Column).ToArray();
        Assert.Equal(2, cells.Length);
        Assert.Equal(2, cells[1].ColumnSpan);
        Assert.Equal("all values are provisional", cells[1].Text);
        Assert.False(table.BottomRowCut);
    }

    [Fact]
    public void Text_running_over_a_column_boundary_in_the_last_row_stays_merged()
    {
        // Two fragments of one sentence, the second reaching over the rule at 250.
        var table = Assert.Single(PdfTextExtractor.Extract(Document(Table(200, 60, [Header, First, ["total", "", ""]], cutLast: true, [250],
            Text(158, 75, "provisional"), Text(208, 75, "values ending in the next column")))).Tables![1]);
        var cells = table.Rows[2].Cells.OrderBy(cell => cell.Column).ToArray();
        Assert.Equal(2, cells.Length);
        Assert.Equal(2, cells[1].ColumnSpan);
        Assert.False(table.BottomRowCut);
    }

    [Fact]
    public void One_line_of_text_cut_into_touching_parts_at_a_column_rule_stays_merged()
    {
        // Each part lies in one column, but the second begins where the first ends: it is one sentence.
        var first = "Items other than the abo";
        var table = Assert.Single(PdfTextExtractor.Extract(Document(Table(200, 60, [Header, First, ["total", "", ""]], cutLast: true, [250],
            Text(249 - Width(first, 10), 75, first), Text(250, 75, "ve follow the definition")))).Tables![1]);
        var cells = table.Rows[2].Cells.OrderBy(cell => cell.Column).ToArray();
        Assert.Equal(2, cells.Length);
        Assert.Equal(2, cells[1].ColumnSpan);
        Assert.False(table.BottomRowCut);
    }

    [Fact]
    public void A_fully_ruled_last_row_is_not_a_cut_row()
    {
        var table = Assert.Single(PdfTextExtractor.Extract(Document(Table(200, 60, [Header, First, ["beta", "20", "second"]], cutLast: false))).Tables![1]);
        Assert.Equal(3, table.Rows.Count);
        Assert.False(table.BottomRowCut);
    }

    // The same header again, then the rest of the cut row, then another row.
    private static string NextPage(params string[] fragment) => Table(800, 600, [Header, fragment, ["gamma", "30", "third"]], cutLast: false);

    [Fact]
    public void A_row_split_across_the_page_break_is_one_row_when_the_table_continues()
    {
        var graph = Graph(CutPage("beta", "20", "second row"), NextPage("", "", "goes on"));
        var serializer = new ReadableMarkdownSerializer();
        var rows = Lines(serializer.Serialize(graph));

        Assert.Equal(1, serializer.RenderedTables);
        Assert.Equal(
            [Header, First, ["beta", "20", "second row goes on"], ["gamma", "30", "third"]],
            rows);
    }

    [Fact]
    public void A_row_after_the_break_that_names_a_new_record_is_a_row_of_its_own()
    {
        var serializer = new ReadableMarkdownSerializer();
        var rows = Lines(serializer.Serialize(Graph(CutPage("beta", "20", "second row"), NextPage("delta", "", "goes on"))));

        Assert.Equal(1, serializer.RenderedTables);
        Assert.Equal(5, rows.Length);
        Assert.Equal(["beta", "20", "second row"], rows[2]);
        Assert.Equal(["delta", "", "goes on"], rows[3]);
    }

    [Fact]
    public void A_row_after_the_break_with_text_where_the_cut_row_has_none_is_a_row_of_its_own()
    {
        var serializer = new ReadableMarkdownSerializer();
        var rows = Lines(serializer.Serialize(Graph(CutPage("beta", "", "second row"), NextPage("", "20", ""))));

        Assert.Equal(1, serializer.RenderedTables);
        Assert.Equal(5, rows.Length);
        Assert.Equal(["", "20", ""], rows[3]);
    }

    [Fact]
    public void A_row_after_the_break_stays_apart_when_the_page_before_was_not_cut()
    {
        var serializer = new ReadableMarkdownSerializer();
        var graph = Graph(Table(200, 60, [Header, First, ["beta", "20", "second row"]], cutLast: false), NextPage("", "", "goes on"));
        var rows = Lines(serializer.Serialize(graph));

        Assert.Equal(1, serializer.RenderedTables);
        Assert.Equal(5, rows.Length);
        Assert.Equal(["beta", "20", "second row"], rows[2]);
        Assert.Equal(["", "", "goes on"], rows[3]);
    }

    [Fact]
    public void The_benchmark_table_cut_by_a_page_end_is_one_table_with_one_header_and_whole_rows()
    {
        var graph = PdfDocumentGraphProjection.CreateGraph(PdfTextExtractor.Extract(PdfBytes("edge-ruled-spec-pagebreak-en.pdf")), "0123456789abcdef");
        var serializer = new ReadableMarkdownSerializer();
        var markdown = serializer.Serialize(graph);
        var rows = Lines(markdown);

        // The long table of 34 rows goes over a page break and its header is not repeated in the output.
        Assert.Equal(1, rows.Count(row => row.SequenceEqual(["Item", "Target", "Actual", "Owner"])));
        var long34 = rows.SkipWhile(row => !row[0].StartsWith("Authentication method P361-01", StringComparison.Ordinal)).Take(34).ToArray();
        Assert.Equal(34, long34.Length);
        Assert.StartsWith("Recovery objective P332-34", long34[^1][0], StringComparison.Ordinal);
        Assert.All(long34, row => Assert.All(row, cell => Assert.NotEmpty(cell)));
        // The table with a two-row header that the page break cuts is one table too, its second header row not repeated.
        Assert.Equal(1, rows.Count(row => row[1] == "Item" && row[2] == "Value"));
        var split = rows.SkipWhile(row => row[1] != "Interval 1-1").Take(9).ToArray();
        Assert.Equal(9, split.Length);
        Assert.Equal("40items", split[0][2]);
        Assert.Equal("Capacity 2-1", split[3][1]);
        Assert.Equal("25s", split[3][2]);
        Assert.Equal("Method 3-3", split[8][1]);
        Assert.Equal("40MB", split[8][2]);
        // A real merged cell stays one.
        Assert.Contains(rows, row => row[0] == "Monitoring" && row[1].StartsWith("Items other than the above", StringComparison.Ordinal) &&
            row.Skip(2).All(cell => cell.Length == 0));
    }

    private static byte[] PdfBytes(string name)
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Pdf", name);
            if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
            current = current.Parent;
        }
        throw new FileNotFoundException(name);
    }
}
