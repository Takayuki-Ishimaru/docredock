using System.Text.Json;
using DocRedock.Api;
using DocRedock.Cli;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.PdfFontResourceScopeTests;

namespace DocRedock.Tests.Pdf;

/// <summary>Text in an unspecified CID collection (a CID-keyed CFF font written as a Type0
/// font, Identity-H, no ToUnicode map) came out as Latin-1 noise with exit code 0 and no warning: its
/// codes are glyph indexes, and only a ToUnicode map can say which characters they are. A font whose
/// codes the file gives no way to read is now never silently trusted: its text is kept and asks for
/// comparison with the source. Fonts that can be read - by a ToUnicode map, a Unicode CMap, or a
/// standard encoding - are not questioned.</summary>
public sealed class PdfUndecodableTextTests
{
    private static string Page(string fonts) =>
        Object(1, $"<< /Type /Page /MediaBox [0 0 612 792] /Contents 2 0 R /Resources << /Font << {fonts} >> >> >>");

    private static byte[] Pdf(string fonts, string content, params string[] objects) =>
        Document([Page(fonts), Stream(2, "", content), .. objects]);

    /// <summary>An unspecified CID collection in a Type0 font: a CID-keyed CFF descendant font.</summary>
    private static string Composite(int id, string entries = "/Encoding /Identity-H") => Object(id,
        $"<< /Type /Font /Subtype /Type0 /BaseFont /AAAAAB+HiraginoSans-W3 {entries} /DescendantFonts [<< /Type /Font /Subtype /CIDFontType0 " +
        "/BaseFont /AAAAAB+HiraginoSans-W3 /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 7 >> /DW 1000 >>] >>");

    private static string Descriptor(int id, int flags) =>
        Object(id, $"<< /Type /FontDescriptor /FontName /AAAAAB+Subset /Flags {flags} /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>");

    private static string[] Lines(PdfExtractionResult result) =>
        result.Pages.SelectMany(page => page.Regions.OrderBy(region => region.ReadingOrder)).Select(region => region.Text).ToArray();

    private static PdfUncertainText Undecodable(PdfExtractionResult result, string resourceName = "F1")
    {
        var doubt = Assert.Single(Assert.Single(result.Pages).UncertainText!, item => item.ResourceName == resourceName);
        Assert.Equal(PdfUncertainTextReason.TextUndecodable, doubt.Reason);
        return doubt;
    }

    [Fact]
    public void A_composite_font_without_a_to_unicode_map_is_kept_and_asks_for_source_comparison()
    {
        // An Identity CID collection requires its own ToUnicode map.
        var pdf = Pdf("/F1 10 0 R", "BT /F1 18 Tf 50 780 Td <00410042> Tj ET", Composite(10));

        var result = PdfTextExtractor.Extract(pdf);
        var page = Assert.Single(result.Pages);

        Assert.Equal(["\0A\0B"], Lines(result));
        var doubt = Undecodable(result);
        Assert.Equal(1, doubt.Fragments);
        Assert.NotNull(doubt.Bounds);
        var diagnostic = Assert.Single(result.Diagnostics!);
        Assert.StartsWith("PdfTextUndecodable: PDF page 1: 1 text fragment(s) use font resource /F1, which gives no way to tell what its character codes stand for",
            diagnostic, StringComparison.Ordinal);
        Assert.EndsWith("Compare with page 1 of the original.", diagnostic, StringComparison.Ordinal);
        Assert.Single(page.UncertainText!);
    }

    [Fact]
    public void The_same_font_with_a_to_unicode_map_is_decoded_and_not_questioned()
    {
        var pdf = Pdf("/F1 10 0 R", "BT /F1 18 Tf 50 780 Td <00410042> Tj ET",
            Composite(10, "/Encoding /Identity-H /ToUnicode 11 0 R"),
            Stream(11, "", "2 beginbfchar <0041> <65E5> <0042> <672C> endbfchar"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["日本"], Lines(result));
        Assert.Null(result.Pages[0].UncertainText);
        Assert.Empty(result.Diagnostics!);
    }

    [Theory]
    [InlineData("not a character map at all")]
    [InlineData("")]
    [InlineData("1 begincodespacerange <0000> <FFFF> endcodespacerange")]
    public void A_to_unicode_map_that_gives_no_character_does_not_make_the_font_readable(string cmap)
    {
        var pdf = Pdf("/F1 10 0 R", "BT /F1 18 Tf 50 780 Td <00410042> Tj ET",
            Composite(10, "/Encoding /Identity-H /ToUnicode 11 0 R"), Stream(11, "", cmap));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(1, Undecodable(result).Fragments);
        Assert.StartsWith("PdfTextUndecodable:", Assert.Single(result.Diagnostics!), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/Encoding /Identity-V")]
    [InlineData("/Encoding /90ms-RKSJ-H")]
    [InlineData("/Encoding /GBK-EUC-H")]
    [InlineData("/Encoding /ETen-B5-H")]
    [InlineData("/Encoding /KSCms-UHC-H")]
    [InlineData("/Encoding /UniJIS-UTF8-H")]
    [InlineData("/Encoding 12 0 R")]
    [InlineData("")]
    public void A_composite_font_whose_cmap_gives_no_unicode_is_questioned(string encoding)
    {
        // Legacy code pages, an embedded CMap and a missing /Encoding all hand over codes, not characters.
        var pdf = Pdf("/F1 10 0 R", "BT /F1 18 Tf 50 780 Td <00410042> Tj ET", Composite(10, encoding),
            Stream(12, "/Type /CMap /CMapName /Custom", "begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange endcmap"));

        Assert.Equal(1, Undecodable(PdfTextExtractor.Extract(pdf)).Fragments);
    }

    [Theory]
    [InlineData("UniJIS-UCS2-H")]
    [InlineData("UniJIS-UCS2-V")]
    [InlineData("UniJIS-UCS2-HW-H")]
    [InlineData("UniJIS-UTF16-H")]
    [InlineData("UniGB-UCS2-H")]
    [InlineData("UniGB-UTF16-V")]
    [InlineData("UniCNS-UCS2-H")]
    [InlineData("UniCNS-UTF16-H")]
    [InlineData("UniKS-UCS2-H")]
    [InlineData("UniKS-UTF16-H")]
    public void A_composite_font_encoded_with_a_unicode_cmap_is_read_as_utf16_and_not_questioned(string cmap)
    {
        // The codes of a Unicode CMap are the characters themselves; before, each byte was read as Latin-1
        // ("0S00o..." for "これは...") with no warning.
        var pdf = Pdf("/F1 10 0 R", "BT /F1 18 Tf 50 780 Td <30533093306B3061306F> Tj ET", Composite(10, $"/Encoding /{cmap}"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["こんにちは"], Lines(result));
        Assert.Null(result.Pages[0].UncertainText);
        Assert.Empty(result.Diagnostics!);
    }

    [Theory]
    [InlineData("<D842DFB7>", "\U00020BB7")]
    [InlineData("<D842>", "\uFFFD")]
    [InlineData("<DFB7D842>", "\uFFFD\uFFFD")]
    [InlineData("<00410042 43>", "ABC")]
    [InlineData("(\\060\\123)", "こ")]
    public void Utf16_codes_make_surrogate_pairs_and_never_unpaired_surrogates(string shown, string expected)
    {
        var pdf = Pdf("/F1 10 0 R", $"BT /F1 18 Tf 50 780 Td {shown} Tj ET", Composite(10, "/Encoding /UniJIS-UTF16-H"));

        var text = string.Concat(Lines(PdfTextExtractor.Extract(pdf)));

        Assert.Equal(expected, text);
        Assert.True(text.Where(char.IsSurrogate).Count() % 2 == 0, "an unpaired surrogate reached the text");
    }

    [Fact]
    public void A_unicode_cmap_decodes_text_shown_in_an_array_and_with_a_to_unicode_map_the_map_wins()
    {
        var pdf = Pdf("/F1 10 0 R /F2 11 0 R", "BT /F1 18 Tf 50 780 Td [<3053> -20 <3093>] TJ ET\nBT /F2 18 Tf 50 740 Td <0041> Tj ET",
            Composite(10, "/Encoding /UniJIS-UCS2-H"),
            Composite(11, "/Encoding /UniJIS-UCS2-H /ToUnicode 12 0 R"), Stream(12, "", "1 beginbfchar <0041> <0058> endbfchar"));

        Assert.Equal(["こん", "X"], Lines(PdfTextExtractor.Extract(pdf)));
    }

    [Theory]
    [InlineData("<< /Type /Font /Subtype /Type1 /BaseFont /Symbol >>")]
    [InlineData("<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >>")]
    [InlineData("<< /Type /Font /Subtype /Type1 /BaseFont /ABCDEF+ZapfDingbats >>")]
    [InlineData("<< /Type /Font /Subtype /Type1 /BaseFont /Symbol~201 >>")]
    [InlineData("<< /Type /Font /Subtype /Type1 /BaseFont /Symbol,Bold >>")]
    [InlineData("<< /Type /Font /Subtype /Type1 /BaseFont /Symbol /FontDescriptor 11 0 R >>", 4)]
    [InlineData("<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAB+Subset /FirstChar 32 /LastChar 34 /Widths [250 500 500] /FontDescriptor 11 0 R >>", 4)]
    [InlineData("<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAB+Subset /FontDescriptor 11 0 R >>", 6)]
    [InlineData("<< /Type /Font /Subtype /MMType1 /BaseFont /AAAAAB+Subset /FontDescriptor 11 0 R >>", 4)]
    public void A_symbolic_simple_font_without_an_encoding_or_map_is_questioned(string font, int flags = 0)
    {
        // Symbol's "a" is alpha, ZapfDingbats' "4" a check mark: the built-in encoding is not Latin-1.
        var pdf = Pdf("/F1 10 0 R", "BT /F1 12 Tf 72 700 Td (abg) Tj ET", Object(10, font), Descriptor(11, flags));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["abg"], Lines(result));
        Assert.Equal(1, Undecodable(result).Fragments);
    }

    [Theory]
    [InlineData("<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAB+Subset /Encoding /WinAnsiEncoding /FontDescriptor 11 0 R >>", 4)]
    [InlineData("<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAB+Subset /Encoding /MacRomanEncoding /FontDescriptor 11 0 R >>", 4)]
    [InlineData("<< /Type /Font /Subtype /Type1 /BaseFont /AAAAAB+Subset /Encoding << /Type /Encoding /Differences [97 /alpha /beta /gamma] >> /FontDescriptor 11 0 R >>", 4, "αβg")]
    [InlineData("<< /Type /Font /Subtype /Type1 /BaseFont /AAAAAB+Subset /Encoding 12 0 R /FontDescriptor 11 0 R >>", 4, "αbg")]
    [InlineData("<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAB+Subset /FontDescriptor 11 0 R >>", 32)]
    [InlineData("<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAB+Subset /FontDescriptor 11 0 R >>", 34)]
    [InlineData("<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAB+Subset /FontDescriptor 11 0 R >>", 36)]
    [InlineData("<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAB+Subset /FontDescriptor 11 0 R >>", 0)]
    [InlineData("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")]
    [InlineData("<< /Type /Font /Subtype /Type1 /BaseFont /Symbola >>")]
    [InlineData("<< /Type /Font /Subtype /TrueType /BaseFont /Measured /FirstChar 32 /LastChar 34 /Widths [250 500 500] >>")]
    [InlineData("<< /Type /Font /Subtype /Type3 /FontBBox [0 0 100 100] /FontMatrix [0.01 0 0 0.01 0 0] /CharProcs << >> /FontDescriptor 11 0 R >>", 4)]
    public void A_simple_font_that_can_be_read_is_not_questioned(string font, int flags = 0, string expected = "abg")
    {
        // Any /Encoding is a way to read the codes; so is a non-symbolic font. Nothing is questioned for
        // lack of evidence: a font with no descriptor and no encoding is an ordinary one. The glyph names
        // of a /Differences array are what the codes they cover stand for.
        var pdf = Pdf("/F1 10 0 R", "BT /F1 12 Tf 72 700 Td (abg) Tj ET", Object(10, font), Descriptor(11, flags),
            Object(12, "<< /Type /Encoding /Differences [97 /alpha] >>"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal([expected], Lines(result));
        Assert.Null(result.Pages[0].UncertainText);
        Assert.Empty(result.Diagnostics!);
    }

    [Fact]
    public void A_symbolic_simple_font_with_a_to_unicode_map_is_decoded_and_not_questioned()
    {
        // The shape of a ReportLab or Quartz TrueType subset: Flags 4, no /Encoding, a ToUnicode map.
        var pdf = Pdf("/F1 10 0 R", "BT /F1 12 Tf 72 700 Td (!\") Tj ET",
            Object(10, "<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAB+Subset /FontDescriptor 11 0 R /ToUnicode 12 0 R >>"),
            Descriptor(11, 4), Stream(12, "", "2 beginbfchar <21> <0054> <22> <0065> endbfchar"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["Te"], Lines(result));
        Assert.Null(result.Pages[0].UncertainText);
    }

    [Fact]
    public void Text_given_by_actual_text_is_not_questioned()
    {
        var pdf = Pdf("/F1 10 0 R", "BT /F1 18 Tf 50 780 Td /Span << /ActualText (Hello) >> BDC <00410042> Tj EMC ET", Composite(10));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["Hello"], Lines(result));
        Assert.Null(result.Pages[0].UncertainText);
        Assert.Empty(result.Diagnostics!);
    }

    [Fact]
    public void Text_no_viewer_shows_is_hidden_not_questioned()
    {
        // Off the page: it is hidden content (the visible policy leaves it out), so nothing to compare.
        var pdf = Pdf("/F1 10 0 R", "BT /F1 18 Tf 900 780 Td <00410042> Tj ET", Composite(10));

        var result = PdfTextExtractor.Extract(pdf);
        var page = Assert.Single(result.Pages);

        Assert.Empty(page.Regions);
        Assert.Single(page.HiddenRegions!);
        Assert.Null(page.UncertainText);
        Assert.DoesNotContain(result.Diagnostics!, diagnostic => diagnostic.StartsWith("PdfTextUndecodable", StringComparison.Ordinal));
    }

    [Fact]
    public void A_font_the_extractor_cannot_find_is_not_questioned()
    {
        // No resources at all: nothing says this is a glyph-code font, so it is read as before.
        var pdf = Document(Object(1, "<< /Type /Page /MediaBox [0 0 612 792] /Contents 2 0 R >>"),
            Stream(2, "", "BT /F1 18 Tf 50 780 Td (Hello) Tj ET"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["Hello"], Lines(result));
        Assert.Null(result.Pages[0].UncertainText);
        Assert.Empty(result.Diagnostics!);
    }

    [Fact]
    public void Each_questioned_font_is_counted_by_its_own_name_and_a_form_keeps_its_own_font()
    {
        // The page's /F1 reads its text; the form's /F1 is an unreadable font; /F2 on the page is another.
        var pdf = Document(
            Object(1, "<< /Type /Page /MediaBox [0 0 612 792] /Contents 2 0 R /Resources << /Font << /F1 10 0 R /F2 13 0 R >> /XObject << /Fm1 20 0 R >> >> >>"),
            Stream(2, "", "BT /F1 18 Tf 50 780 Td <0041> Tj ET\nBT /F2 18 Tf 50 740 Td <0041> Tj ET\nBT /F2 18 Tf 50 700 Td <0042> Tj ET\n/Fm1 Do"),
            Composite(10, "/Encoding /Identity-H /ToUnicode 11 0 R"), Stream(11, "", "1 beginbfchar <0041> <65E5> endbfchar"),
            Composite(13),
            Stream(20, "/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 14 0 R >> >>", "BT /F1 18 Tf 50 660 Td <0041> Tj ET"),
            Composite(14));

        var result = PdfTextExtractor.Extract(pdf);
        var page = Assert.Single(result.Pages);

        Assert.Equal("日", Lines(result)[0]);
        Assert.Equal([("F1", 1), ("F2", 2)], page.UncertainText!.OrderBy(doubt => doubt.ResourceName, StringComparer.Ordinal)
            .Select(doubt => (doubt.ResourceName!, doubt.Fragments)));
        Assert.All(page.UncertainText!, doubt => Assert.Equal(PdfUncertainTextReason.TextUndecodable, doubt.Reason));
        Assert.Equal(2, result.Diagnostics!.Count(diagnostic => diagnostic.StartsWith("PdfTextUndecodable:", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_page_cites_the_name_it_calls_the_font_by()
    {
        // One font object, /F1 on page 1 and /F3 on page 2: each page's note names its own resource.
        var pdf = Document(
            Object(1, "<< /Type /Page /MediaBox [0 0 612 792] /Contents 2 0 R /Resources << /Font << /F1 10 0 R >> >> >>"),
            Stream(2, "", "BT /F1 18 Tf 50 780 Td <0041> Tj ET"),
            Object(3, "<< /Type /Page /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F3 10 0 R >> >> >>"),
            Stream(4, "", "BT /F3 18 Tf 50 780 Td <0041> Tj ET"),
            Composite(10));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["F1", "F3"], result.Pages.Select(page => Assert.Single(page.UncertainText!).ResourceName));
        Assert.Contains(result.Diagnostics!, diagnostic => diagnostic.StartsWith("PdfTextUndecodable: PDF page 2: 1 text fragment(s) use font resource /F3,", StringComparison.Ordinal));
    }

    [Fact]
    public void Questioned_text_adds_to_other_doubts_instead_of_replacing_them()
    {
        // The same page: an unreadable font, and a font name two fonts of the document share.
        var pdf = Document(
            Object(1, "<< /Type /Page /MediaBox [0 0 612 792] /Contents 2 0 R /Resources << /Font << /F1 10 0 R >> /XObject << /Fm1 20 0 R /Fm2 21 0 R >> >> >>"),
            Stream(2, "", "BT /F1 18 Tf 50 780 Td <0041> Tj ET\nBT /F9 18 Tf 50 740 Td (AAA) Tj ET"),
            Composite(10),
            Stream(20, "/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F9 12 0 R >> >>", ""),
            Stream(21, "/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F9 14 0 R >> >>", ""),
            Font(12, 13), CMap(13, 'Y'), Font(14, 15), CMap(15, 'Z'));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal([PdfUncertainTextReason.FontResourceAmbiguous, PdfUncertainTextReason.TextUndecodable],
            result.Pages[0].UncertainText!.Select(doubt => doubt.Reason).Order());
    }

    [Fact]
    public void A_bfrange_written_without_spaces_between_its_brackets_is_read()
    {
        // Quartz writes <21><21><0054>, and read as one range per line with at least one space between
        // the brackets this map had no characters: a Quartz TrueType font came out as "!\"#$%".
        var cmap = "1 begincodespacerange <00><FF> endcodespacerange\n4 beginbfrange\n<21><21><0054>\n<22><24><0061>\n<30><31>[<0058><0059>]\n<40><40> <0050>\nendbfrange";
        var pdf = Pdf("/F1 10 0 R", "BT /F1 12 Tf 72 700 Td (!\"#$01@) Tj ET",
            Object(10, "<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAB+Subset /FontDescriptor 11 0 R /ToUnicode 12 0 R >>"),
            Descriptor(11, 4), Stream(12, "", cmap));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["TabcXYP"], Lines(result));
        Assert.Null(result.Pages[0].UncertainText);
    }

    [Fact]
    public void Several_bfranges_on_one_line_are_all_read()
    {
        var cmap = "2 beginbfrange <01> <01> <0041> <02><02><0042>\n<03> <03> <0043>\nendbfrange";
        var pdf = Pdf("/F1 10 0 R", "BT /F1 12 Tf 72 700 Td <010203> Tj ET",
            Object(10, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 11 0 R >>"), Stream(11, "", cmap));

        Assert.Equal(["ABC"], Lines(PdfTextExtractor.Extract(pdf)));
    }

    private static async Task<(ReadableDocumentExportResult Result, string Markdown, string Root)> ExportAsync(byte[] pdf)
    {
        var root = Directory.CreateTempSubdirectory("docredock-undecodable-").FullName;
        var source = Path.Combine(root, "quartz.pdf");
        await File.WriteAllBytesAsync(source, pdf);
        var markdown = Path.Combine(root, "out.md");
        var result = await new DocumentService(null, null, discoverPdfRasterizer: false)
            .ExportReadableAsync(new ReadableDocumentExportOptions(source, markdown));
        return (result, await File.ReadAllTextAsync(markdown), root);
    }

    [Fact]
    public async Task Undecodable_text_is_sent_to_source_review_with_a_note_a_warning_and_a_review_item()
    {
        var (result, text, root) = await ExportAsync(Pdf("/F1 10 0 R", "BT /F1 18 Tf 50 780 Td <00410042> Tj ET", Composite(10)));
        try
        {
            var summary = ExportSummaryBuilder.Build(result.Graph, result.Diagnostics);
            var page = Assert.Single(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages);

            Assert.Contains("1 text fragment(s) above use a font whose character codes cannot be turned into characters, so their text is probably garbled - compare with the source page", text, StringComparison.Ordinal);
            Assert.DoesNotContain("characters or visibility", text, StringComparison.Ordinal);
            var warning = Assert.Single(result.Diagnostics, d => d.Code == "PdfTextUndecodable");
            Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
            Assert.Equal((1, 1, 1), (summary.ReviewPages, summary.UnanalyzedContent, summary.UnanalyzedContentPages));
            Assert.Contains("Detected review items: unanalyzed content 1 page(s)", summary.ToString(), StringComparison.Ordinal);
            var marker = Assert.Single(result.Graph.Partitions.SelectMany(partition => partition.Nodes),
                node => node.Extensions?.ContainsKey(PdfDocumentGraphProjection.UncertainTextExtension) == true);
            var record = Assert.Single(marker.Extensions![PdfDocumentGraphProjection.UncertainTextExtension]
                .Deserialize<PdfDocumentGraphProjection.PdfUncertainTextRecord[]>()!);
            Assert.Equal(("text-undecodable", "F1", 1), (record.Reason, record.ResourceName, record.Fragments));
            var element = Assert.Single(page.Elements);
            Assert.Equal(ReviewElementKind.UncertainText, element.Kind);
            Assert.EndsWith(":F1", element.SourceId, StringComparison.Ordinal);
            Assert.Equal("1ページ目：文字1件は、文字の対応または表示の有無を確定できませんでした（Markdownには含めています）。" +
                "照合画像を作成できなかったため、原本PDFの1ページ目で内容を確認してください。", ExportReviewText.DescribeJapanese(page));
            Assert.Equal("page 1: 1 text item(s) whose characters or visibility could not be determined; kept in the Markdown",
                ExportReviewText.DescribeEnglish(page));
            Assert.Contains(result.Diagnostics, d => d.Code == "PdfReviewImageUnavailable" &&
                d.Message.StartsWith("PDF page 1: text whose characters or visibility could not be determined needs source comparison", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task A_readable_font_exports_without_a_note_or_a_review_page()
    {
        var (result, text, root) = await ExportAsync(Pdf("/F1 10 0 R", "BT /F1 18 Tf 50 780 Td <00410042> Tj ET",
            Composite(10, "/Encoding /Identity-H /ToUnicode 11 0 R"), Stream(11, "", "2 beginbfchar <0041> <65E5> <0042> <672C> endbfchar")));
        try
        {
            var summary = ExportSummaryBuilder.Build(result.Graph, result.Diagnostics);

            Assert.Contains("日本", text, StringComparison.Ordinal);
            Assert.DoesNotContain("compare with the source page", text, StringComparison.Ordinal);
            Assert.DoesNotContain(result.Diagnostics, d => d.Code == "PdfTextUndecodable");
            Assert.Equal((0, 0, 0), (summary.ReviewPages, summary.UnanalyzedContent, summary.Warnings));
        }
        finally { Directory.Delete(root, true); }
    }
}

/// <summary>What a person running the CLI sees: the warning, the page that needs review, and - with
/// --fail-on-review - exit code 11, which a pipeline can stop on.</summary>
[Collection("Environment variables")]
public sealed class CliUndecodableTextTests : IDisposable
{
    private readonly string? previousExperimental = Environment.GetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL");
    private readonly string root = Directory.CreateTempSubdirectory("docredock-cli-undecodable-").FullName;

    public CliUndecodableTextTests() => Environment.SetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL", "1");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL", previousExperimental);
        Directory.Delete(root, true);
    }

    private async Task<(int Exit, string Output)> ExportAsync(byte[] pdf, params string[] options)
    {
        var source = Path.Combine(root, "source.pdf");
        await File.WriteAllBytesAsync(source, pdf);
        var stdout = new StringWriter();
        var app = new CliApplication(stdout, new StringWriter(), new DocumentService(null, null, discoverPdfRasterizer: false));
        var exit = await app.RunAsync(["export", source, "--output", Path.Combine(root, "out.md"), "--ocr", "off", "--force", .. options]);
        return (exit, stdout.ToString());
    }

    private static byte[] Hiragino(string entries) => Document(
        Object(1, "<< /Type /Page /MediaBox [0 0 612 792] /Contents 2 0 R /Resources << /Font << /C1 10 0 R >> >> >>"),
        Stream(2, "", "BT /C1 18 Tf 50 780 Td <00410042> Tj ET"),
        Object(10, $"<< /Type /Font /Subtype /Type0 /BaseFont /AAAAAB+HiraginoSans-W3 /Encoding /Identity-H {entries} /DescendantFonts [<< /Type /Font /Subtype /CIDFontType0 /BaseFont /AAAAAB+HiraginoSans-W3 /DW 1000 >>] >>"),
        Stream(11, "", "2 beginbfchar <0041> <65E5> <0042> <672C> endbfchar"));

    [Fact]
    public async Task Undecodable_text_is_a_warning_and_a_review_page_and_fails_on_review()
    {
        var (exit, output) = await ExportAsync(Hiragino(""), "--fail-on-review");

        Assert.Equal(11, exit);
        Assert.Contains("WARNING PdfTextUndecodable: PDF page 1: 1 text fragment(s) use font resource /C1", output, StringComparison.Ordinal);
        Assert.Contains("Pages requiring review: 1", output, StringComparison.Ordinal);
        Assert.Contains("Detected review items: unanalyzed content 1 page(s)", output, StringComparison.Ordinal);
        Assert.Contains("Review required:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Undecodable_text_without_the_flag_is_a_warning_exit_code()
    {
        var (exit, output) = await ExportAsync(Hiragino(""));

        Assert.Equal(1, exit);
        Assert.Contains("Warnings: ", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Review required:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_font_with_a_to_unicode_map_exports_cleanly_even_with_fail_on_review()
    {
        var (exit, output) = await ExportAsync(Hiragino("/ToUnicode 11 0 R"), "--fail-on-review");

        Assert.Equal(0, exit);
        Assert.Contains("Pages requiring review: 0", output, StringComparison.Ordinal);
        Assert.Contains("Detected review items: none", output, StringComparison.Ordinal);
        Assert.DoesNotContain("PdfTextUndecodable", output, StringComparison.Ordinal);
    }
}
