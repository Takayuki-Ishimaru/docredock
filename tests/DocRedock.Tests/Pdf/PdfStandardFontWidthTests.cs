using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.MeasuredPdf;

namespace DocRedock.Tests.Pdf;

public sealed class PdfStandardFontWidthTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-standard-widths-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    private static string ResourcesFor(string font, string encoding = "") =>
        $"<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /{font} {encoding} >> >> >>";

    private static PdfTextRegion Line(string content, string font = "Helvetica", string encoding = "") =>
        Assert.Single(PdfTextExtractor.Extract(Page(content, ResourcesFor(font, encoding))).Pages[0].Regions);

    [Theory]
    [InlineData("Helvetica", 9.44)]
    [InlineData("Helvetica-Bold", 9.44)]
    [InlineData("Helvetica-Oblique", 9.44)]
    [InlineData("Helvetica-BoldOblique", 9.44)]
    [InlineData("Times-Roman", 9.44)]
    [InlineData("Times-Bold", 10.00)]
    [InlineData("Times-Italic", 8.33)]
    [InlineData("Times-BoldItalic", 8.89)]
    [InlineData("Courier", 6.00)]
    [InlineData("Courier-Bold", 6.00)]
    [InlineData("Courier-Oblique", 6.00)]
    [InlineData("Courier-BoldOblique", 6.00)]
    [InlineData("Symbol", 7.22)]
    [InlineData("ZapfDingbats", 9.74)]
    public void Every_standard_font_supplies_its_own_advance_without_Widths(string font, double expected)
    {
        var text = font == "Symbol" ? "A" : font == "ZapfDingbats" ? "!" : "W";
        Assert.Equal(expected, Line($"BT /F1 10 Tf 72 700 Td ({text}) Tj ET", font).BoundingBox.Width, 6);
    }

    [Theory]
    [InlineData("", "'", 2.22)]
    [InlineData("/Encoding /WinAnsiEncoding", "'", 1.91)]
    [InlineData("/Encoding /WinAnsiEncoding", "\\200", 5.56)]
    [InlineData("/Encoding /MacRomanEncoding", "\\216", 5.56)]
    [InlineData("/Encoding << /Differences [65 /W] >>", "A", 9.44)]
    [InlineData("/Encoding << /BaseEncoding /WinAnsiEncoding /Differences [65 /i] >>", "A", 2.22)]
    [InlineData("/Encoding << /BaseEncoding /WinAnsiEncoding /Differences [65 /fi] >>", "A", 5.00)]
    public void Widths_follow_character_codes_and_glyph_names_in_the_encoding(string encoding, string code, double expected)
    {
        Assert.Equal(expected, Line($"BT /F1 10 Tf 72 700 Td ({code}) Tj ET", encoding: encoding).BoundingBox.Width, 6);
    }

    [Fact]
    public void An_indirect_encoding_and_Differences_array_are_followed()
    {
        var result = PdfTextExtractor.Extract(Page("BT /F1 10 Tf 72 700 Td (A) Tj ET", objects: new Dictionary<int, string>
        {
            [4] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding 5 0 R >>",
            [5] = "<< /BaseEncoding /WinAnsiEncoding /Differences 6 0 R >>",
            [6] = "[65 /W]",
        }));
        Assert.Equal("W", Assert.Single(result.Pages[0].Regions).Text);
        Assert.Equal(9.44, Assert.Single(result.Pages[0].Regions).BoundingBox.Width, 6);
    }

    [Fact]
    public void Explicit_widths_override_standard_metrics()
    {
        var line = Line("BT /F1 10 Tf 72 700 Td (A) Tj ET", encoding: "/FirstChar 65 /Widths [900]");
        Assert.Equal(9, line.BoundingBox.Width, 6);
    }

    [Fact]
    public void ToUnicode_changes_the_text_but_not_the_glyph_advance_or_partial_mark()
    {
        var result = PdfTextExtractor.Extract(Page("BT /F1 10 Tf 72 700 Td (AB) Tj ET\n0.8 w 72 703 m 81.44 703 l S",
            objects: new Dictionary<int, string>
            {
                [4] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Differences [65 /W] >> /ToUnicode 5 0 R >>",
                [5] = Stream("2 beginbfchar <41> <00660069> <42> <0042> endbfchar"),
            }));
        var line = Assert.Single(result.Pages[0].Regions);
        Assert.Equal("fiB", line.Text);
        Assert.Equal(9.44 + 6.67, line.BoundingBox.Width, 6);
        Assert.Equal([new TextRange(0, 2)], line.Struck);
    }

    [Theory]
    [InlineData("Helvetica", "/Widths [broken]")]
    [InlineData("Helvetica", "/Widths []")]
    [InlineData("Helvetica", "/Encoding /UnknownEncoding")]
    [InlineData("Helvetica", "/Encoding << /Differences [65 /unrecognizedGlyph] >>")]
    [InlineData("ABCDEF+Helvetica", "")]
    [InlineData("Custom", "")]
    public void Unrecognized_fonts_encodings_glyphs_and_malformed_explicit_widths_keep_the_estimate(string font, string encoding)
    {
        Assert.Equal(6, Line("BT /F1 10 Tf 72 700 Td (A) Tj ET", font, encoding).BoundingBox.Width, 6);
    }

    [Fact]
    public void Standard_metrics_use_text_spacing_and_horizontal_scaling()
    {
        var line = Line("BT /F1 10 Tf 2 Tc 3 Tw 50 Tz 72 700 Td [(A B) -1000 (C)] TJ (D) Tj ET", encoding: "/Encoding /WinAnsiEncoding");
        Assert.Equal("A B CD", line.Text);
        Assert.Equal(((6.67 + 2) + (2.78 + 2 + 3) + (6.67 + 2) + 10 + (7.22 + 2) + (7.22 + 2)) * 0.5,
            line.BoundingBox.Width, 6);
    }

    [Fact]
    public void Positioned_fragments_keep_words_whole_and_separate_actual_word_gaps()
    {
        var content = "BT /F1 12 Tf 72 700 Td (Com) Tj ET\n" +
            $"BT /F1 12 Tf {N(72 + Width("Com", 12))} 700 Td (parison) Tj ET\n" +
            $"BT /F1 12 Tf {N(72 + Width("Comparison ", 12))} 700 Td (Report) Tj ET";
        Assert.Equal("Comparison Report", Line(content, encoding: "/Encoding /WinAnsiEncoding").Text);
    }

    [Theory]
    [InlineData(703.6, true)]
    [InlineData(698.5, false)]
    public async Task Partial_strikes_and_underlines_are_placed_without_review(double y, bool strike)
    {
        var x = 72 + Width("Old price ", 12);
        var content = "BT /F1 12 Tf 72 700 Td (Old price 1000 yen) Tj ET\n" +
            $"0.8 w {N(x)} {N(y)} m {N(x + Width("1000 yen", 12))} {N(y)} l S";
        var source = Path.Combine(root, strike ? "strike.pdf" : "underline.pdf");
        await File.WriteAllBytesAsync(source, Page(content, ResourcesFor("Helvetica", "/Encoding /WinAnsiEncoding")));
        var output = Path.Combine(root, strike ? "strike.md" : "underline.md");
        var result = await new DocumentService(null, null, discoverPdfRasterizer: false)
            .ExportReadableAsync(new ReadableDocumentExportOptions(source, output, EnableOcr: false));
        Assert.Contains(strike ? "Old price ~~1000 yen~~" : "Old price <u>1000 yen</u>", await File.ReadAllTextAsync(output));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "PdfStrikethroughUnplaced");
        Assert.Null(PdfTextExtractor.Extract(await File.ReadAllBytesAsync(source)).Pages[0].UncertainText);
    }

    [Fact]
    public void Standard_widths_do_not_guess_a_symbolic_fonts_text()
    {
        var result = PdfTextExtractor.Extract(Page("BT /F1 10 Tf 72 700 Td (A) Tj ET", ResourcesFor("Symbol")));
        Assert.Contains(result.Diagnostics!, diagnostic => diagnostic.StartsWith("PdfTextUndecodable:", StringComparison.Ordinal));
    }
}
