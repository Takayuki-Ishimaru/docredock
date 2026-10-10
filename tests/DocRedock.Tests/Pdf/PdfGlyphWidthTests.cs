using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.MeasuredPdf;

namespace DocRedock.Tests.Pdf;

/// <summary>
/// Text is placed with its font's glyph widths: a show's width, where the text after it continues,
/// and where each character is drawn follow /Widths (with /MissingWidth outside them) in a simple
/// font, /W and /DW in an Identity-H composite font, TJ adjustments, and the size, character and word
/// spacing and horizontal scaling in force. Estimated at 6 units a character, a run that LibreOffice
/// placed where the previous one ends overlapped it, and the line split there. A font that gives no
/// widths keeps that estimate.
/// </summary>
public sealed class PdfGlyphWidthTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-widths-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    private static PdfTextRegion Line(string content, string resources = Resources, IReadOnlyDictionary<int, string>? objects = null) =>
        Assert.Single(PdfTextExtractor.Extract(Page(content, resources, objects)).Pages[0].Regions);

    [Theory]
    [InlineData("BT /F1 10 Tf 1 0 0 1 72 700 Tm (AV) Tj (W) Tj ET", 22.78)]
    // The size set by the text matrix (as Quartz and Cairo write it) or by the CTM.
    [InlineData("BT /F1 1 Tf 10 0 0 10 72 700 Tm (AV) Tj (W) Tj ET", 22.78)]
    [InlineData("2 0 0 2 0 0 cm BT /F1 10 Tf 1 0 0 1 36 350 Tm (AV) Tj (W) Tj ET", 45.56)]
    public void A_simple_font_measures_each_show_and_places_the_next_one_where_it_ends(string content, double width)
    {
        // (667 + 667) / 1000 x 10 for "AV", and "W" (944) right after it: one line, no gap.
        var line = Line(content);
        Assert.Equal("AVW", line.Text);
        Assert.Equal(72, line.BoundingBox.X, 6);
        Assert.Equal(width, line.BoundingBox.Width, 6);
    }

    [Fact]
    public void Codes_outside_the_widths_take_the_missing_width_and_an_indirect_widths_array_is_read()
    {
        var line = Line("BT /F1 10 Tf 72 700 Td (ABC) Tj ET", objects: new Dictionary<int, string>
        {
            [4] = "<< /Type /Font /Subtype /Type1 /BaseFont /Narrow /FirstChar 65 /Widths 6 0 R /FontDescriptor 5 0 R >>",
            [5] = "<< /Type /FontDescriptor /FontName /Narrow /MissingWidth 250 >>",
            [6] = "[500 600]",
        });
        Assert.Equal(13.5, line.BoundingBox.Width, 6);
    }

    [Fact]
    public void An_Identity_H_font_measures_two_byte_codes_with_its_W_array_and_default_width()
    {
        var line = Line("BT /F1 10 Tf 72 700 Td <00010002000B0063> Tj ET", objects: new Dictionary<int, string>
        {
            [4] = "<< /Type /Font /Subtype /Type0 /BaseFont /Mincho /Encoding /Identity-H /DescendantFonts [5 0 R] /ToUnicode 6 0 R >>",
            [5] = "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Mincho /DW 900 /W [1 [500 600] 10 12 700] >>",
            [6] = TwoByteCMap((0x01, 'あ'), (0x02, 'い'), (0x0B, 'う'), (0x63, 'え')),
        });
        Assert.Equal("あいうえ", line.Text);
        // 500 and 600 listed for CIDs 1 and 2, 700 for the range 10-12, and the default 900 for CID 99.
        Assert.Equal(27, line.BoundingBox.Width, 6);
    }

    [Fact]
    public void TJ_adjustments_character_and_word_spacing_and_horizontal_scaling_move_the_glyphs()
    {
        // Each glyph advances by (w x 10 / 1000 + Tc, plus Tw for the space) x Tz; -1000 in the TJ
        // array moves the next glyph a font size forward. "D" follows where the array ends.
        var line = Line("BT /F1 10 Tf 2 Tc 3 Tw 50 Tz 72 700 Td [(A B) -1000 (C)] TJ (D) Tj ET");
        Assert.Equal("A BCD", line.Text);
        var expected = ((6.67 + 2) + (2.78 + 2 + 3) + (6.67 + 2) + 10 + (7.22 + 2) + (7.22 + 2)) * 0.5;
        Assert.Equal(expected, line.BoundingBox.Width, 6);
    }

    [Fact]
    public void Word_spacing_applies_to_the_single_byte_code_32_only()
    {
        Assert.Equal(6.67 + 2.78 + 5 + 6.67, Line("BT /F1 10 Tf 5 Tw 72 700 Td (A A) Tj ET").BoundingBox.Width, 6);
        // In an Identity-H font the two-byte code 0x0020 is a CID like any other.
        var line = Line("BT /F1 10 Tf 5 Tw 72 700 Td <004100200041> Tj ET", objects: new Dictionary<int, string>
        {
            [4] = "<< /Type /Font /Subtype /Type0 /BaseFont /Gothic /Encoding /Identity-H /DescendantFonts [5 0 R] /ToUnicode 6 0 R >>",
            [5] = "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Gothic /W [32 [250] 65 [500]] >>",
            [6] = TwoByteCMap((0x20, ' '), (0x41, 'A')),
        });
        Assert.Equal("A A", line.Text);
        Assert.Equal(12.5, line.BoundingBox.Width, 6);
    }

    [Fact]
    public void A_Type3_font_scales_its_widths_by_its_font_matrix()
    {
        var line = Line("BT /F1 10 Tf 72 700 Td (AB) Tj ET", objects: new Dictionary<int, string>
        {
            [4] = "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 100 100] /FontMatrix [0.01 0 0 0.01 0 0] /CharProcs << >> " +
                  "/Encoding << /Type /Encoding /Differences [65 /a /b] >> /FirstChar 65 /LastChar 66 /Widths [40 75] >>",
        });
        Assert.Equal(11.5, line.BoundingBox.Width, 6);
    }

    [Theory]
    // A standard 14 font that leaves its widths out, and a font the page does not define.
    [InlineData("<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>")]
    [InlineData("<< >>")]
    public void Fonts_without_widths_keep_the_estimate_of_six_units_a_character(string resources)
    {
        var line = Line("BT /F1 10 Tf 1 0 0 1 72 700 Tm (AV) Tj (W) Tj ET", resources);
        Assert.Equal(18, line.BoundingBox.Width, 6);
    }

    [Fact]
    public void Text_turned_by_its_text_matrix_keeps_the_estimate_and_stays_one_line()
    {
        // Measured, "more" would continue up the page after "rotated" and be read before it; lines and
        // reading order are built for text running across the page.
        var line = Line("BT /F1 10 Tf 0 1 -1 0 300 500 Tm (rotated) Tj (more) Tj ET");
        Assert.Equal("rotatedmore", line.Text);
        Assert.Equal(66, line.BoundingBox.Width, 6);
    }

    [Fact]
    public void A_vertical_font_keeps_the_estimate()
    {
        var line = Line("BT /F1 10 Tf 72 700 Td <00010002> Tj ET", objects: new Dictionary<int, string>
        {
            [4] = "<< /Type /Font /Subtype /Type0 /BaseFont /Mincho /Encoding /Identity-V /DescendantFonts [5 0 R] /ToUnicode 6 0 R >>",
            [5] = "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Mincho /W [1 [500 600]] >>",
            [6] = TwoByteCMap((0x01, 'あ'), (0x02, 'い')),
        });
        Assert.Equal(12, line.BoundingBox.Width, 6);
    }

    [Fact]
    public void A_label_centred_on_a_column_line_inside_a_table_belongs_to_the_cell_it_starts_in()
    {
        // A label centred on a bar drawn over two columns: measured, its middle is exactly on the
        // line between them, and a table with text in no cell at all would be discarded.
        var grid = "0.8 w 72 610 390 90 re S\n72 670 m 462 670 l S\n72 640 m 462 640 l S\n202 610 m 202 700 l S\n332 610 m 332 700 l S";
        var label = "Design";
        var start = 332 - Width(label, 9) / 2;
        string Cell(double x, double y, string text) => $"BT /F1 9 Tf 1 0 0 1 {N(x)} {N(y)} Tm ({text}) Tj ET";
        var content = string.Join("\n", grid, Cell(78, 680, "Task"), Cell(208, 680, "Week 1"), Cell(338, 680, "Week 2"),
            Cell(78, 650, "Plan"), Cell(208, 650, "done"), Cell(338, 650, "-"), Cell(78, 620, "Build"), Cell(start, 620, label));
        var result = PdfTextExtractor.Extract(Page(content));
        var table = Assert.Single(result.Tables![1]);
        Assert.Equal(["Task", "Week 1", "Week 2", "Plan", "done", "-", "Build", "Design", ""],
            table.Rows.SelectMany(row => row.Cells).Select(cell => cell.Text));
    }

    /// <summary>"Status: ~~approved~~ rejected" as LibreOffice writes it: every run its own show at
    /// its exact position, the strikethrough a line over the middle run.</summary>
    private static string StatusLine()
    {
        var (start, status, approved) = (90.1, Width("Status: ", 11), Width("approved", 11));
        return string.Join("\n",
            $"BT {N(start)} 709.55 Td /F1 11 Tf [(Status: )] TJ ET",
            $"BT {N(start + status)} 709.55 Td /F1 11 Tf [(approved)] TJ ET",
            $"q 1 0 0 1 {N(start + status)} 709.55 cm 0.7 w 0 2.7 m {N(approved)} 2.7 l S Q",
            $"BT {N(start + status + approved)} 709.55 Td /F1 11 Tf [( rejected)] TJ ET");
    }

    [Fact]
    public void Runs_placed_one_by_one_where_the_previous_ends_stay_one_line()
    {
        // "Status: " is 37.3 wide; estimated at 48, it overlapped "approved" and became its own line.
        var line = Line(StatusLine());
        Assert.Equal("Status: approved rejected", line.Text);
        Assert.Equal([new TextRange(8, 16)], line.Struck);
    }

    [Fact]
    public async Task Runs_placed_one_by_one_are_written_as_one_paragraph()
    {
        var source = Path.Combine(root, "status.pdf");
        await File.WriteAllBytesAsync(source, Page(StatusLine()));
        var output = Path.Combine(root, "status.md");
        var result = await new DocumentService(null, null, discoverPdfRasterizer: false)
            .ExportReadableAsync(new ReadableDocumentExportOptions(source, output, EnableOcr: false));
        var markdown = (await File.ReadAllTextAsync(output)).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("\nStatus: ~~approved~~ rejected\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("\nStatus:\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "PdfStrikethroughUnplaced");
    }
}
