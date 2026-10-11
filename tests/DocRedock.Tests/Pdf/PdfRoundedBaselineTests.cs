using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.MeasuredPdf;

namespace DocRedock.Tests.Pdf;

public sealed class PdfRoundedBaselineTests
{
    private static string Show(double x, double y, string text) =>
        $"BT /F1 12 Tf 1 0 0 1 {N(x)} {N(y)} Tm ({text}) Tj ET";

    [Theory]
    [InlineData(468.4181, 468.42)]
    [InlineData(468.42, 468.4181)]
    [InlineData(700.0049, 700.0051)]
    public void Fragments_on_a_rounded_baseline_follow_their_horizontal_positions(double leftY, double rightY)
    {
        var content = Show(72, leftY, "Com") + "\n" + Show(72 + Width("Com", 12), rightY, "parison Report");
        var result = PdfTextExtractor.Extract(Page(content));
        Assert.Equal("Comparison Report", Assert.Single(result.Pages[0].Regions).Text);
    }

    [Fact]
    public void A_table_header_split_in_the_middle_of_a_word_keeps_its_cell_separator()
    {
        var content = "BT /F1 12 Tf 72 547.1586 Td [(Student characteristics) -1000 (borrowed) -1000 (amoun)] TJ ET\n" +
            Show(72 + Width("Student characteristicsborrowedamoun", 12) + 24, 547.16, "t") + "\n" +
            Show(72 + Width("Student characteristicsborrowedamount", 12) + 36, 547.16, "Note");
        Assert.Equal("Student characteristics borrowed amount Note",
            Assert.Single(PdfTextExtractor.Extract(Page(content)).Pages[0].Regions).Text);
    }

    [Fact]
    public void Rounding_does_not_join_separate_lines_or_reorder_text_columns()
    {
        var content = Show(72, 700.001, "Left top") + "\n" + Show(300, 700, "Right top") + "\n" +
            Show(72, 680, "Left middle") + "\n" + Show(300, 680.002, "Right middle") + "\n" +
            Show(72, 660.003, "Left bottom") + "\n" + Show(300, 660, "Right bottom");
        var page = Assert.Single(PdfTextExtractor.Extract(Page(content)).Pages);
        Assert.Equal(2, page.ColumnCount);
        Assert.Equal(["Left top", "Left middle", "Left bottom", "Right top", "Right middle", "Right bottom"],
            page.Regions.Select(region => region.Text));
    }
}
