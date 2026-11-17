using System.Text;
using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.MeasuredPdf;

namespace DocRedock.Tests.Pdf;

/// <summary>Ruled tables whose borders are thin filled bars, one per cell side (a browser's print of a
/// collapsed-border table): wide columns used to lose a bar to the node pass and with it the table.</summary>
public sealed class PdfBarBorderTableTests
{
    private static readonly string[][] English =
    [
        ["Item", "Description", "Owner"],
        ["Help desk contact", "Checked daily, capped at 80 entries", "Operations team"],
        ["Authentication method", "Reviewed monthly, capped at 10 entries", "IT department"],
        ["Retention period", "Audited quarterly, capped at 30 entries", "Platform team"],
    ];

    // Narrow columns, as a Japanese table's are, and the same rules.
    private static readonly string[][] Narrow =
    [
        ["Item", "Note", "Owner"],
        ["Alpha", "Daily", "Ops"],
        ["Beta", "Weekly", "IT"],
        ["Gamma", "Monthly", "QA"],
    ];

    [Fact]
    public void Wide_english_columns_drawn_as_cell_bars_form_one_table()
    {
        var table = Assert.Single(PdfTextExtractor.Extract(Page(BarTable(English, [140, 240, 120]))).Tables![1]);
        Assert.Equal(4, table.Rows.Count);
        for (var row = 0; row < English.Length; row++)
            Assert.Equal(English[row], table.Rows[row].Cells.OrderBy(cell => cell.Column).Select(cell => cell.Text));
    }

    [Fact]
    public void Narrow_columns_drawn_as_cell_bars_form_one_table()
    {
        var table = Assert.Single(PdfTextExtractor.Extract(Page(BarTable(Narrow, [60, 60, 60]))).Tables![1]);
        for (var row = 0; row < Narrow.Length; row++)
            Assert.Equal(Narrow[row], table.Rows[row].Cells.OrderBy(cell => cell.Column).Select(cell => cell.Text));
    }

    [Fact]
    public void A_table_of_many_cells_drawn_as_cell_bars_is_not_rejected_for_its_number_of_pieces()
    {
        var rows = new List<string[]> { new[] { "Item", "Target", "Actual", "Owner" } };
        for (var i = 1; i <= 34; i++) rows.Add([$"Row {i}", $"{i * 7}", $"{i}.5", "Team"]);
        var table = Assert.Single(PdfTextExtractor.Extract(Page(BarTable(rows.ToArray(), [90, 60, 60, 80], rowHeight: 20, top: 820))).Tables![1]);
        Assert.Equal(35, table.Rows.Count);
        Assert.Equal("Row 34", table.Rows[34].Cells.OrderBy(cell => cell.Column).First().Text);
    }

    [Fact]
    public void A_last_row_without_its_inner_column_rules_still_closes_the_table()
    {
        // The page ends in the middle of a table: the last row has lost its inner column rules.
        var table = Assert.Single(PdfTextExtractor.Extract(Page(BarTable(English, [140, 240, 120], skipInnerVerticalsInLastRow: true))).Tables![1]);
        Assert.Equal(4, table.Rows.Count);
        Assert.Equal("Help desk contact", table.Rows[1].Cells.OrderBy(cell => cell.Column).First().Text);
        var lastRow = string.Join(" ", table.Rows[3].Cells.OrderBy(cell => cell.Column).Select(cell => cell.Text));
        Assert.Contains("Retention period", lastRow, StringComparison.Ordinal);
        Assert.Contains("Platform team", lastRow, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("edge-ruled-report-en.pdf", "Item", "Description", "Owner")]
    [InlineData("edge-ruled-report-ja.pdf", "項目", "内容", "担当")]
    public void Benchmark_report_tables_are_recognised_in_english_and_japanese(string file, params string[] header)
    {
        var tables = PdfTextExtractor.Extract(PdfBytes(file)).Tables!.SelectMany(page => page.Value).ToArray();
        Assert.Equal(3, tables.Length);
        Assert.Contains(tables, table => header.SequenceEqual(table.Rows[0].Cells.OrderBy(cell => cell.Column).Select(cell => cell.Text)));
        Assert.All(tables, table => Assert.True(table.Rows.Count >= 5));
    }

    [Fact]
    public void Benchmark_table_cut_by_a_page_end_is_found_on_both_pages()
    {
        var tables = PdfTextExtractor.Extract(PdfBytes("edge-ruled-spec-pagebreak-en.pdf")).Tables!.SelectMany(page => page.Value).ToArray();
        var big = Assert.Single(tables, table => table.Rows.Count >= 30);
        Assert.Equal(["Item", "Target", "Actual", "Owner"], big.Rows[0].Cells.OrderBy(cell => cell.Column).Select(cell => cell.Text));
        // Its repeated header starts the part on the next page.
        Assert.Contains(tables, table => table.Rows.Count is >= 4 and < 10 &&
            table.Rows[0].Cells.OrderBy(cell => cell.Column).Select(cell => cell.Text).SequenceEqual(["Item", "Target", "Actual", "Owner"]));
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

    /// <summary>A table whose every cell side is a bar 0.75 pt thick, header cells filled, text 10 pt.</summary>
    private static string BarTable(string[][] rows, double[] widths, double rowHeight = 24, double top = 780, bool skipInnerVerticalsInLastRow = false)
    {
        const double left = 50, thick = 0.75;
        var content = new StringBuilder();
        var xs = new List<double> { left };
        foreach (var width in widths) xs.Add(xs[^1] + width);
        content.Append("0.91 0.93 0.95 rg\n");
        content.Append($"{N(left)} {N(top - rowHeight)} {N(xs[^1] - left)} {N(rowHeight)} re f\n");
        content.Append("0.4 0.4 0.4 rg\n");
        for (var row = 0; row < rows.Length; row++)
        {
            var y = top - row * rowHeight;
            var last = row == rows.Length - 1;
            for (var column = 0; column < widths.Length; column++)
            {
                if (!(skipInnerVerticalsInLastRow && last && column > 1))
                    content.Append($"{N(xs[column])} {N(y - rowHeight)} {N(thick)} {N(rowHeight)} re f\n");
                content.Append($"{N(xs[column] + thick)} {N(y)} {N(widths[column] - thick)} {N(thick)} re f\n");
                if (last) content.Append($"{N(xs[column] + thick)} {N(y - rowHeight)} {N(widths[column] - thick)} {N(thick)} re f\n");
            }
            content.Append($"{N(xs[^1] - thick)} {N(y - rowHeight)} {N(thick)} {N(rowHeight)} re f\n");
        }
        content.Append("0 g\n");
        for (var row = 0; row < rows.Length; row++)
            for (var column = 0; column < widths.Length; column++)
                content.Append($"BT /F1 10 Tf 1 0 0 1 {N(xs[column] + 4)} {N(top - row * rowHeight - rowHeight / 2 - 3)} Tm ({rows[row][column]}) Tj ET\n");
        return content.ToString();
    }

    private static byte[] Page(string content) => MeasuredPdf.Page(content);
}
