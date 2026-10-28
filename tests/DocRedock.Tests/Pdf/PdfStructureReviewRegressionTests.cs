using System.Text;
using DocRedock.Core.Documents;
using DocRedock.Api;
using DocRedock.Markdown;
using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.MeasuredPdf;

namespace DocRedock.Tests.Pdf;

public sealed class PdfStructureReviewRegressionTests
{
    private static string Text(double x, double y, string text, int size = 10) =>
        $"BT /F1 {size} Tf 1 0 0 1 {N(x)} {N(y)} Tm ({text}) Tj ET";

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]
    public void Measured_true_type_columns_keep_a_narrow_persistent_gutter(double gutterMm)
    {
        const string left = "Left column words occupy their own line";
        const string right = "Right column words occupy their own line";
        var rightX = 50 + Width(left, 10) + gutterMm * 72 / 25.4;
        var content = string.Join("\n", Enumerable.Range(0, 4).SelectMany(row => new[]
        {
            Text(50, 700 - row * 12, left), Text(rightX, 700 - row * 12, right)
        }));
        var result = PdfTextExtractor.Extract(Page(content));
        Assert.Equal(2, result.Pages[0].ColumnCount);
        Assert.Equal(Enumerable.Repeat(left, 4).Concat(Enumerable.Repeat(right, 4)), result.Pages[0].Regions.Select(line => line.Text));
        Assert.True(PdfTextAccounting.Reconcile(result.Pages[0], []).IsComplete);
    }

    [Fact]
    public void Japanese_unframed_lines_join_without_inserting_word_spaces()
    {
        const string first = "日本語の文章がここで折り返されて次の行へ続く";
        const string second = "さらに続く。";
        var chars = (first + second).Distinct().ToArray();
        var codes = chars.Select((character, index) => (character, code: index + 1)).ToDictionary(item => item.character, item => item.code);
        string Show(string text, int y) => $"BT /F1 10 Tf 50 {y} Td <{string.Concat(text.Select(character => codes[character].ToString("X4")))}> Tj ET";
        var pdf = Page(Show(first, 700) + "\n" + Show(second, 688), objects: new Dictionary<int, string>
        {
            [4] = "<< /Type /Font /Subtype /Type0 /BaseFont /Mincho /Encoding /Identity-H /DescendantFonts [5 0 R] /ToUnicode 6 0 R >>",
            [5] = "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Mincho /DW 1000 >>",
            [6] = TwoByteCMap(codes.Select(item => (item.Value, item.Key)).ToArray())
        });
        var result = PdfTextExtractor.Extract(pdf);
        Assert.Equal(first + second, Assert.Single(result.Pages[0].Regions).Text);
        Assert.True(PdfTextAccounting.Reconcile(result.Pages[0], []).IsComplete);
    }

    [Fact]
    public void Narrow_gaps_between_short_labels_do_not_establish_columns()
    {
        var content = string.Join("\n", Enumerable.Range(0, 4).SelectMany(row => new[]
        {
            Text(50, 700 - row * 12, "Name"), Text(95, 700 - row * 12, "Value")
        }));
        var result = PdfTextExtractor.Extract(Page(content));
        Assert.Equal(1, result.Pages[0].ColumnCount);
        Assert.All(result.Pages[0].Regions, line => Assert.Equal("Name Value", line.Text));
    }

    [Fact]
    public void Unframed_wrapped_prose_folds_and_keeps_heading_and_list_items_separate()
    {
        const string first = "This sentence continues across the first printed line";
        const string second = "and finishes here.";
        var result = PdfTextExtractor.Extract(Page(string.Join("\n", new[]
        {
            Text(50, 740, "Section heading", 16), Text(50, 700, first), Text(50, 688, second),
            Text(50, 660, "1. First independent item."), Text(50, 648, "2. Second independent item.")
        })));
        Assert.Equal(new[] { "Section heading", first + " " + second, "1. First independent item.", "2. Second independent item." },
            result.Pages[0].Regions.Select(line => line.Text));
        Assert.True(PdfTextAccounting.Reconcile(result.Pages[0], []).IsComplete);
    }

    [Fact]
    public void Paragraphs_never_cross_from_one_column_into_the_other()
    {
        string[] left = ["This left paragraph continues across the line", "and continues across another printed line", "then ends here."];
        string[] right = ["This right paragraph continues across a line", "and continues across another printed line", "then ends there."];
        var content = string.Join("\n", Enumerable.Range(0, 3).SelectMany(row => new[]
        {
            Text(50, 700 - row * 12, left[row]), Text(310, 700 - row * 12, right[row])
        }));
        var result = PdfTextExtractor.Extract(Page(content));
        Assert.Equal(2, result.Pages[0].ColumnCount);
        Assert.Equal(new[] { string.Join(" ", left), string.Join(" ", right) }, result.Pages[0].Regions.Select(line => line.Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ruled_tables_reconstruct_merged_headers_and_row_spans(bool rowSpan)
    {
        var grid = rowSpan
            ? "50 600 300 120 re S 150 600 m 150 720 l S 250 600 m 250 720 l S 50 640 m 350 640 l S 150 680 m 350 680 l S"
            : "50 600 300 120 re S 150 600 m 150 680 l S 250 600 m 250 720 l S 50 640 m 350 640 l S 50 680 m 350 680 l S";
        var labels = rowSpan
            ? new[] { Text(60, 690, "Merged"), Text(160, 690, "B"), Text(260, 690, "C"), Text(160, 650, "D"), Text(260, 650, "E"), Text(60, 610, "F"), Text(160, 610, "G"), Text(260, 610, "H") }
            : new[] { Text(60, 690, "Merged heading"), Text(260, 690, "C"), Text(60, 650, "D"), Text(160, 650, "E"), Text(260, 650, "F"), Text(60, 610, "G"), Text(160, 610, "H"), Text(260, 610, "I") };
        var result = PdfTextExtractor.Extract(Page(grid + "\n" + string.Join("\n", labels)));
        var table = Assert.Single(result.Tables![1]);
        var merged = table.Rows[0].Cells[0];
        Assert.Equal(rowSpan ? 2 : 1, merged.RowSpan);
        Assert.Equal(rowSpan ? 1 : 2, merged.ColumnSpan);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(rowSpan ? 9 : 8, table.Rows.Sum(row => row.Cells.Count));
        Assert.True(PdfTextAccounting.Reconcile(result.Pages[0], [table]).IsComplete);
        var projection = PdfPageProjection.ToDocumentNodes(result.Pages[0], [table]);
        Assert.Single(projection);
        var tableContent = Assert.IsType<TableNodeContent>(projection[0].Content);
        Assert.True(TableGrid.TryCreate(tableContent, out var expanded, out var error), error);
        Assert.Equal(rowSpan ? "D" : "E", expanded.Rows[1][1].Origin.Text);
        var markdown = new ReadableMarkdownSerializer().Serialize(PdfDocumentGraphProjection.CreateGraph(result, "0123456789abcdef0123456789abcdef"));
        Assert.Contains(rowSpan ? "|  | D | E |" : "| D | E | F |", markdown);
        Assert.Contains(rowSpan ? "| Merged | B | C |" : "| Merged heading |  | C |", markdown);
        var overlayResult = PdfTextExtractor.Extract(Page(grid + "\n" + string.Join("\n", labels) + "\n170 665 60 6 re f"));
        var overlay = Assert.Single(Assert.Single(overlayResult.Tables![1]).Overlays!);
        Assert.Equal(1, overlay.StartRow);
        Assert.Equal(1, overlay.EndRow);
        Assert.Equal(1, overlay.StartColumn);
        Assert.Equal(1, overlay.EndColumn);
    }

    [Fact]
    public void A_two_by_two_grid_with_a_merged_header_needs_only_three_populated_cells()
    {
        const string grid = "50 600 200 80 re S 150 600 m 150 640 l S 50 640 m 250 640 l S";
        var result = PdfTextExtractor.Extract(Page(grid + "\n" + string.Join("\n",
            Text(60, 650, "Merged header"), Text(60, 610, "A"), Text(160, 610, "B"))));
        var table = Assert.Single(result.Tables![1]);
        Assert.Equal(2, table.Rows[0].Cells[0].ColumnSpan);
        Assert.Equal(new[] { "Merged header", "A", "B" }, table.Rows.SelectMany(row => row.Cells).Select(cell => cell.Text));
        Assert.True(PdfTextAccounting.Reconcile(result.Pages[0], [table]).IsComplete);
    }

    [Fact]
    public void Unequal_column_widths_are_not_rejected_as_an_irregular_grid()
    {
        const string grid = "50 600 300 80 re S 80 600 m 80 680 l S 50 640 m 350 640 l S";
        var result = PdfTextExtractor.Extract(Page(grid + "\n" + string.Join("\n", Text(55, 650, "A"), Text(90, 650, "Description"),
            Text(55, 610, "B"), Text(90, 610, "Longer description"))));
        Assert.Single(result.Tables![1]);
    }

    [Fact]
    public void Nonrectangular_missing_borders_remain_native_text()
    {
        const string grid = "50 600 200 80 re S 150 600 m 150 640 l S 150 640 m 250 640 l S";
        var result = PdfTextExtractor.Extract(Page(grid + "\n" + string.Join("\n", Text(60, 650, "A"), Text(160, 650, "B"),
            Text(60, 610, "C"), Text(160, 610, "D"))));
        Assert.Empty(result.Tables!.GetValueOrDefault(1) ?? []);
        Assert.True(PdfTextAccounting.Reconcile(result.Pages[0], []).IsComplete);
    }
}
