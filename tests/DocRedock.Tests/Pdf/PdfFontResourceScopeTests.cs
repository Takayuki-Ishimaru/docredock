using System.Text;
using DocRedock.Formats.Pdf;

namespace DocRedock.Tests.Pdf;

/// <summary>v0.2.11 evaluation, section 6: a page and the forms it draws each called a different font
/// /F1, and every one of them was decoded with whichever /F1 came last in the file - XXX / YYY / ZZZ
/// became ZZZ / ZZZ / ZZZ with exit code 0 and no warning. A font name is now resolved in the
/// resource dictionary of the page or form that uses it, and a name that cannot be resolved there is
/// never silently read with another scope's font.</summary>
public sealed class PdfFontResourceScopeTests
{
    /// <summary>A document from numbered objects, in the order given.</summary>
    internal static byte[] Document(params string[] objects) =>
        Encoding.Latin1.GetBytes("%PDF-1.4\n" + string.Concat(objects) + "%%EOF");

    internal static string Object(int id, string body) => $"{id} 0 obj {body} endobj\n";

    internal static string Stream(int id, string dictionary, string content) =>
        $"{id} 0 obj << {dictionary}{(dictionary.Length == 0 ? "" : " ")}/Length {content.Length} >> stream\n{content}\nendstream endobj\n";

    /// <summary>A ToUnicode CMap that reads the code of 'A' as <paramref name="meaning"/> - what a
    /// subset font does: the code is a glyph slot, and only the map says which character it is.</summary>
    internal static string CMap(int id, char meaning) => Stream(id, "", $"1 beginbfchar <41> <{(int)meaning:X4}> endbfchar");

    internal static string Font(int id, int cmap) =>
        Object(id, $"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode {cmap} 0 R >>");

    /// <summary>Fonts 10/12/14 read "A" as X/Y/Z through CMaps 11/13/15.</summary>
    private static readonly string Fonts = Font(10, 11) + CMap(11, 'X') + Font(12, 13) + CMap(13, 'Y') + Font(14, 15) + CMap(15, 'Z');

    private static string Form(int id, string content, string resources = "", string bbox = "0 0 612 792") =>
        Stream(id, $"/Type /XObject /Subtype /Form /BBox [{bbox}]" + (resources.Length == 0 ? "" : $" /Resources << {resources} >>"), content);

    private static string Page(int id, int contents, string resources) =>
        Object(id, $"<< /Type /Page /MediaBox [0 0 612 792] /Contents {contents} 0 R /Resources << {resources} >> >>");

    private static string[] Lines(PdfExtractionResult result) =>
        result.Pages.SelectMany(page => page.Regions.OrderBy(region => region.ReadingOrder)).Select(region => region.Text).ToArray();

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_page_and_its_forms_that_reuse_a_font_name_each_keep_their_own_font(bool formsFirstInFile, bool drawSecondFormFirst)
    {
        // The evaluation's new-font-scope.pdf: the page's /F1 reads "AAA" as XXX, the first form's
        // /F1 as YYY, the second form's /F1 as ZZZ. Neither the order of the objects in the file
        // nor the order the forms are drawn in may decide which font a name means.
        var draws = drawSecondFormFirst ? "/Fm2 Do\n/Fm1 Do" : "/Fm1 Do\n/Fm2 Do";
        var page = Page(1, 2, "/Font << /F1 10 0 R >> /XObject << /Fm1 20 0 R /Fm2 21 0 R >>") +
            Stream(2, "", "BT /F1 12 Tf 72 700 Td (AAA) Tj ET\n" + draws);
        var forms = Form(20, "BT /F1 12 Tf 72 650 Td (AAA) Tj ET", "/Font << /F1 12 0 R >>") +
            Form(21, "BT /F1 12 Tf 72 600 Td (AAA) Tj ET", "/Font << /F1 14 0 R >>");
        var pdf = formsFirstInFile ? Document(forms, Fonts, page) : Document(page, Fonts, forms);

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["XXX", "YYY", "ZZZ"], Lines(result));
        Assert.Empty(result.Diagnostics!);
        Assert.Null(result.Pages[0].UncertainText);
    }

    [Fact]
    public void Fonts_written_inline_in_the_resource_dictionary_are_decoded_with_their_own_maps()
    {
        // The evaluation's first control, new-font-flat.pdf, wrote each font dictionary inline and
        // came out as AAA / AAA / AAA: an inline font's ToUnicode was never attached to its name.
        const string inline = "/Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode";
        var pdf = Document(
            Page(1, 2, $"/Font << /F1 << {inline} 11 0 R >> /F2 << {inline} 13 0 R >> /F3 << {inline} 15 0 R >> >>"),
            Stream(2, "", "BT /F1 12 Tf 72 700 Td (AAA) Tj ET\nBT /F2 12 Tf 72 650 Td (AAA) Tj ET\nBT /F3 12 Tf 72 600 Td (AAA) Tj ET"),
            CMap(11, 'X'), CMap(13, 'Y'), CMap(15, 'Z'));

        Assert.Equal(["XXX", "YYY", "ZZZ"], Lines(PdfTextExtractor.Extract(pdf)));
    }

    [Fact]
    public void Separate_names_on_the_page_decode_exactly_like_one_name_reused_across_forms()
    {
        // The evaluation's formal control, new-font-flat-indirect.pdf.
        var pdf = Document(
            Page(1, 2, "/Font << /F1 10 0 R /F2 12 0 R /F3 14 0 R >>"),
            Stream(2, "", "BT /F1 12 Tf 72 700 Td (AAA) Tj ET\nBT /F2 12 Tf 72 650 Td (AAA) Tj ET\nBT /F3 12 Tf 72 600 Td (AAA) Tj ET"),
            Fonts);

        Assert.Equal(["XXX", "YYY", "ZZZ"], Lines(PdfTextExtractor.Extract(pdf)));
    }

    [Fact]
    public void Pages_that_reuse_a_font_name_each_keep_their_own_font()
    {
        var pdf = Document(
            Page(1, 2, "/Font << /F1 10 0 R >>"), Stream(2, "", "BT /F1 12 Tf 72 700 Td (AAA) Tj ET"),
            Page(3, 4, "/Font << /F1 12 0 R >>"), Stream(4, "", "BT /F1 12 Tf 72 700 Td (AAA) Tj ET"),
            Page(5, 6, "/Font << /F1 14 0 R >>"), Stream(6, "", "BT /F1 12 Tf 72 700 Td (AAA) Tj ET"),
            Fonts);

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["XXX", "YYY", "ZZZ"], result.Pages.Select(page => page.Text));
    }

    [Fact]
    public void Nested_forms_resolve_a_reused_font_name_in_their_own_scope()
    {
        var pdf = Document(
            Page(1, 2, "/Font << /F1 10 0 R >> /XObject << /Fm1 20 0 R >>"),
            Stream(2, "", "BT /F1 12 Tf 72 700 Td (AAA) Tj ET\n/Fm1 Do"),
            Form(20, "BT /F1 12 Tf 72 650 Td (AAA) Tj ET\n/Fm1 Do", "/Font << /F1 12 0 R >> /XObject << /Fm1 21 0 R >>"),
            Form(21, "BT /F1 12 Tf 72 600 Td (AAA) Tj ET", "/Font << /F1 14 0 R >>"),
            Fonts);

        Assert.Equal(["XXX", "YYY", "ZZZ"], Lines(PdfTextExtractor.Extract(pdf)));
    }

    [Fact]
    public void A_font_a_form_selects_ends_with_the_form()
    {
        // The text state belongs to the graphics state, which the form's Do saves and restores: the
        // page's last line has no Tf of its own and draws with the page's /F1 again.
        var pdf = Document(
            Page(1, 2, "/Font << /F1 10 0 R >> /XObject << /Fm1 20 0 R >>"),
            Stream(2, "", "BT /F1 12 Tf 72 700 Td (AAA) Tj ET\n/Fm1 Do\nBT 72 600 Td (AAA) Tj ET"),
            Form(20, "BT /F1 12 Tf 72 650 Td (AAA) Tj ET", "/Font << /F1 12 0 R >>"),
            Fonts);

        Assert.Equal(["XXX", "YYY", "XXX"], Lines(PdfTextExtractor.Extract(pdf)));
    }

    [Fact]
    public void A_form_that_selects_no_font_draws_with_the_font_its_caller_selected()
    {
        // The form's own /F1 is a different font, but the form never selects it: the font in the
        // graphics state when the form is drawn is the page's.
        var pdf = Document(
            Page(1, 2, "/Font << /F1 10 0 R >> /XObject << /Fm1 20 0 R >>"),
            Stream(2, "", "BT /F1 12 Tf 72 700 Td (AAA) Tj ET\n/Fm1 Do"),
            Form(20, "BT 72 650 Td (AAA) Tj ET", "/Font << /F1 12 0 R >>"),
            Fonts);

        Assert.Equal(["XXX", "XXX"], Lines(PdfTextExtractor.Extract(pdf)));
    }

    [Fact]
    public void A_form_without_its_own_resources_uses_the_fonts_of_the_page_that_draws_it()
    {
        var pdf = Document(
            Page(1, 2, "/Font << /F1 12 0 R >> /XObject << /Fm1 20 0 R >>"),
            Stream(2, "", "/Fm1 Do"),
            Form(20, "BT /F1 12 Tf 72 650 Td (AAA) Tj ET"),
            // Another /F1 elsewhere in the document must not be used instead.
            Object(30, "<< /Font << /F1 10 0 R >> >>"),
            Fonts);

        Assert.Equal(["YYY"], Lines(PdfTextExtractor.Extract(pdf)));
    }

    [Fact]
    public void An_escaped_font_name_resolves_like_its_plain_spelling()
    {
        var pdf = Document(
            Page(1, 2, "/Font << /F1 10 0 R /F2 12 0 R >>"),
            Stream(2, "", "BT /F#32 12 Tf 72 700 Td (AAA) Tj ET"),
            Fonts);

        Assert.Equal(["YYY"], Lines(PdfTextExtractor.Extract(pdf)));
    }

    [Fact]
    public void A_name_the_page_does_not_define_is_decoded_with_the_only_font_the_document_gives_it()
    {
        // The page's resources cannot be read at all. One font in the whole document is called /F1,
        // so reading the page's /F1 with it is what a viewer that falls back would do - and what
        // the extractor always did.
        var pdf = Document(
            Object(1, "<< /Type /Page /MediaBox [0 0 612 792] /Contents 2 0 R >>"),
            Stream(2, "", "BT /F1 12 Tf 72 700 Td (AAA) Tj ET"),
            Object(30, "<< /Font << /F1 12 0 R >> >>"),
            Font(12, 13), CMap(13, 'Y'));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["YYY"], Lines(result));
        Assert.Empty(result.Diagnostics!);
    }

    [Fact]
    public void A_name_the_page_does_not_define_and_the_document_gives_to_different_fonts_is_not_guessed()
    {
        // /F9 is not in the page's resources, and the document's two forms each call a different
        // font /F9. Reading the page's text with either would be a silent guess.
        var pdf = Document(
            Page(1, 2, "/Font << /F1 10 0 R >> /XObject << /Fm1 20 0 R /Fm2 21 0 R >>"),
            Stream(2, "", "BT /F1 12 Tf 72 700 Td (AAA) Tj ET\nBT /F9 12 Tf 72 650 Td (AAA) Tj ET"),
            Form(20, "BT /F9 12 Tf 72 600 Td (AAA) Tj ET", "/Font << /F9 12 0 R >>"),
            Form(21, "BT /F9 12 Tf 72 550 Td (AAA) Tj ET", "/Font << /F9 14 0 R >>"),
            Fonts);

        var result = PdfTextExtractor.Extract(pdf);
        var page = Assert.Single(result.Pages);

        Assert.Equal(["XXX", "AAA"], Lines(result));
        var doubt = Assert.Single(page.UncertainText!);
        Assert.Equal(PdfUncertainTextReason.FontResourceAmbiguous, doubt.Reason);
        Assert.Equal("F9", doubt.ResourceName);
        Assert.Equal(1, doubt.Fragments);
        var diagnostic = Assert.Single(result.Diagnostics!);
        Assert.StartsWith("PdfFontResourceAmbiguous: PDF page 1: 1 text fragment(s) use font resource /F9", diagnostic, StringComparison.Ordinal);
        Assert.Contains("Compare with page 1 of the original.", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dictionary_operand_that_is_never_closed_does_not_stop_font_resolution()
    {
        // Malformed: the property list of BDC is never closed. Everything after it is still content.
        var pdf = Document(
            Page(1, 2, "/Font << /F1 10 0 R >>"),
            Stream(2, "", "/P << /MCID 0 BDC BT /F1 12 Tf 72 700 Td (AAA) Tj ET EMC"),
            Fonts);

        Assert.Equal(["XXX"], Lines(PdfTextExtractor.Extract(pdf)));
    }

    private static byte[] CMapPdf(string cmap, string shown) => Document(
        Page(1, 2, "/Font << /F1 10 0 R >>"), Stream(2, "", $"BT /F1 12 Tf 72 700 Td {shown} Tj ET"),
        Font(10, 11), Stream(11, "", cmap));

    [Fact]
    public void A_glyph_mapped_to_several_characters_is_decoded_to_all_of_them()
    {
        // LibreOffice shapes "ttp" in a URL as one glyph and maps it to three characters. Reading
        // that destination as one number overflowed and failed the whole export (exit code 10).
        var pdf = CMapPdf("3 beginbfchar <68> <0068> <25> <007400740070> <73> <0073> endbfchar", "(h%s)");

        Assert.Equal("https", PdfTextExtractor.Extract(pdf).Text);
    }

    [Fact]
    public void A_range_of_supplementary_plane_characters_advances_the_last_byte()
    {
        var pdf = CMapPdf("1 beginbfrange <01> <02> <D835DC00> endbfrange", "<0102>");

        Assert.Equal("\U0001D400\U0001D401", PdfTextExtractor.Extract(pdf).Text);
    }

    [Theory]
    [InlineData("1 beginbfrange <FFFFFFF0> <FFFFFFFF> <0041> endbfrange")]
    [InlineData("1 beginbfrange <00000000> <FFFFFFFF> <0041> endbfrange")]
    [InlineData("1 beginbfrange <0000000000> <0000000001> <0041> endbfrange")]
    [InlineData("1 beginbfchar <41> <004100420> endbfchar")]
    public void A_malformed_or_oversized_cmap_neither_fails_nor_hangs_the_extraction(string cmap)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = PdfTextExtractor.Extract(CMapPdf(cmap, "(A)"));

        Assert.Single(result.Pages);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"CMap parsing took {stopwatch.Elapsed}");
    }

    [Fact]
    public void Actual_text_is_not_doubted_when_its_font_cannot_be_resolved()
    {
        // The replacement text is Unicode already; the font's map plays no part in it.
        var pdf = Document(
            Page(1, 2, "/XObject << /Fm1 20 0 R /Fm2 21 0 R >>"),
            Stream(2, "", "BT /F9 12 Tf 72 700 Td /Span << /ActualText (Hello) >> BDC (AAA) Tj EMC ET"),
            Form(20, "", "/Font << /F9 12 0 R >>"),
            Form(21, "", "/Font << /F9 14 0 R >>"),
            Fonts);

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["Hello"], Lines(result));
        Assert.Null(result.Pages[0].UncertainText);
    }
}
