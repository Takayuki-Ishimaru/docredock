using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.MeasuredPdf;

namespace DocRedock.Tests.Pdf;

public sealed class PdfFieldEvaluationTests
{
    private static string Text(double x, double y, string text, int size = 11) =>
        $"BT /F1 {size} Tf 1 0 0 1 {N(x)} {N(y)} Tm ({text}) Tj ET";

    private static string Grid => "50 100 300 80 re S 200 100 m 200 180 l S 50 140 m 350 140 l S\n" +
        Text(60, 155, "Name") + "\n" + Text(210, 155, "Amount") + "\n" +
        Text(60, 115, "A") + "\n" + Text(210, 115, "100");

    [Fact]
    public void Paragraph_below_table_does_not_collide_using_legacy_twelve_point_box()
    {
        var result = PdfTextExtractor.Extract(Page(Grid + "\n" + Text(50, 89.5, "End of table")));
        Assert.Single(result.Tables![1]);
        var nodes = PdfPageProjection.ToDocumentNodes(result.Pages[0], result.Tables[1]);
        Assert.Equal(NodeKind.Table, nodes[0].Kind);
        Assert.Equal("End of table", Assert.IsType<TextNodeContent>(nodes[1].Content).Text);
    }

    [Fact]
    public void Real_unassigned_text_touching_table_is_reviewed_and_kept()
    {
        var result = PdfTextExtractor.Extract(Page(Grid + "\n" + Text(40, 93, "Crosses the outside border")));
        Assert.Empty(result.Tables!.GetValueOrDefault(1) ?? []);
        Assert.Contains(result.Diagnostics!, diagnostic => diagnostic.StartsWith("PdfTableReconstructionFailed:"));
        Assert.Contains("Crosses the outside border", result.Text);
        Assert.Equal(1, result.Pages[0].ColumnCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Spaced_headings_and_short_unequal_columns_keep_the_entire_left_column_first(int rightLines)
    {
        var commands = new List<string> { Text(50, 760, "Left heading", 14), Text(320, 760, "Right heading", 14) };
        for (var index = 0; index < 9; index++)
            commands.Add(Text(50, 733 - index * 16, "Left paragraph has several words " + index));
        for (var index = 1; index < rightLines; index++)
            commands.Add(Text(320, 733 - (index - 1) * 19, "Right paragraph " + index));
        var result = PdfTextExtractor.Extract(Page(string.Join('\n', commands)));
        Assert.Equal(2, result.Pages[0].ColumnCount);
        Assert.True(result.Text.IndexOf("Left paragraph has several words 8", StringComparison.Ordinal) <
            result.Text.IndexOf("Right heading", StringComparison.Ordinal), result.Text);
        Assert.Contains(result.Diagnostics!, diagnostic => diagnostic.StartsWith("PdfColumnLayoutDetected:"));
        Assert.DoesNotContain(result.Pages[0].Regions, region => region.Text.Contains("Left") && region.Text.Contains("Right"));
    }

    [Fact]
    public void Right_column_table_is_placed_after_left_column()
    {
        var page = new PdfPageText(1,
        [
            new("Left first", new Geometry("pdf-user-space", 50, 750, 200, 12), 0) { ReadingColumn = 0 },
            new("Left last", new Geometry("pdf-user-space", 50, 200, 200, 12), 1) { ReadingColumn = 0 },
            new("Right first", new Geometry("pdf-user-space", 320, 750, 230, 12), 2) { ReadingColumn = 1 },
            new("Right last", new Geometry("pdf-user-space", 320, 200, 230, 12), 3) { ReadingColumn = 1 }
        ], ColumnCount: 2);
        var table = new PdfTable("table", 1, new Geometry("pdf-user-space", 320, 400, 230, 200),
            [new PdfTableRow([new PdfTableCell(0, 0, 1, 1, new Geometry("pdf-user-space", 320, 400, 230, 200), "Cell", [])])],
            PdfTableConfidence.HighConfidenceInferred, []);
        var nodes = PdfPageProjection.ToDocumentNodes(page, [table]);
        Assert.Equal(new[] { "Left first", "Left last", "Right first", "table", "Right last" },
            nodes.Select(node => node.Content is TextNodeContent text ? text.Text : node.Id));
    }

    [Fact]
    public void Larger_text_above_body_paragraphs_is_projected_as_a_heading()
    {
        var regions = new List<PdfTextRegion> { new("Introduction", new Geometry("pdf-user-space", 50, 760, 150, 12), 0) { FontSize = 15 } };
        for (var index = 0; index < 3; index++)
            regions.Add(new("A substantial body paragraph with ordinary text.", new Geometry("pdf-user-space", 50, 700 - index * 40, 300, 12), index + 1) { FontSize = 11 });
        var nodes = PdfPageProjection.ToDocumentNodes(new PdfPageText(1, regions));
        Assert.Equal(NodeKind.Heading, nodes[0].Kind);
        Assert.Equal(2, nodes[0].Extensions!["heading_level"].GetInt32());
        Assert.All(nodes.Skip(1), node => Assert.Equal(NodeKind.Paragraph, node.Kind));
    }

    [Theory]
    [InlineData("[(is) -120 (available) -120 (now)]", "is available now")]
    [InlineData("[(A) -120 (V)]", "AV")]
    [InlineData("[(wa) 40 (ter)]", "water")]
    [InlineData("[(com) -80 (pile)]", "compile")]
    public void Compressed_justified_word_spaces_are_distinguished_from_small_kerning(string operand, string expected)
    {
        var result = PdfTextExtractor.Extract(Page("BT /F1 11 Tf 50 700 Td " + operand + " TJ ET"));
        Assert.Contains(expected, result.Text);
    }

    [Fact]
    public void Tj_word_spaces_private_bullet_and_ideographic_space_survive()
    {
        var spaced = PdfTextExtractor.Extract(Page("BT /F1 11 Tf 50 700 Td [(is) -280 (available) -280 (now)] TJ ET"));
        Assert.Contains("is available now", spaced.Text);
        var unicode = PdfTextExtractor.Extract(Page("BT /F1 11 Tf 50 700 Td <F0B730004E00> Tj ET", Resources,
            new Dictionary<int, string> { [4] = "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding /UniJIS-UTF16-H >>" }));
        Assert.Contains("•　", unicode.Text);
    }
}
