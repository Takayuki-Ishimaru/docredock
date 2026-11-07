using System.Text;
using DocRedock.Api;
using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.MeasuredPdf;

namespace DocRedock.Tests.Pdf;

/// <summary>A running header or footer that is the same text on every page but for a number ("REVIEW FOOTER 1",
/// "REVIEW FOOTER 2") is page furniture like one that is the same on every page (the v0.3.10 field evaluation).</summary>
public sealed class PdfNumberedFooterFurnitureTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-numbered-footer-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    // A page is 595 x 842: its bottom band ends at y = 101, its top band starts at y = 741.
    private const double FooterY = 30, HeaderY = 812;

    private static string Text(double x, double y, string text, double size = 9) =>
        $"BT /F1 {N(size)} Tf 1 0 0 1 {N(x)} {N(y)} Tm ({text}) Tj ET";

    // Three long lines of the body in the middle of the page: they give the size of the body text (10 points).
    private static string Body(int page, double y = 500) => string.Join("\n", Enumerable.Range(1, 3).Select(index =>
        Text(72, y - (index - 1) * 14, $"Body line {index} of page {page} sets out the measured results in full.", 10)));

    private static string[] Numbered(string pattern, int pages, int first = 1) =>
        Enumerable.Range(0, pages).Select(index => string.Format(pattern, first + index)).ToArray();

    // Pages drawing the content given; fonts are objects 4 and up (by default SimpleFont as object 4, /F1).
    private static byte[] Document(IReadOnlyList<string> pages, IReadOnlyDictionary<int, string>? fonts = null)
    {
        var pdf = new StringBuilder("%PDF-1.4\n");
        pdf.Append("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        pdf.Append("2 0 obj << /Type /Pages /Kids [")
            .Append(string.Join(" ", pages.Select((_, index) => $"{10 + index * 2} 0 R"))).Append($"] /Count {pages.Count} >> endobj\n");
        foreach (var (id, body) in fonts ?? new Dictionary<int, string> { [4] = SimpleFont })
            pdf.Append($"{id} 0 obj {body} endobj\n");
        for (var index = 0; index < pages.Count; index++)
        {
            var id = 10 + index * 2;
            pdf.Append($"{id} 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources {Resources} /Contents {id + 1} 0 R >> endobj\n")
                .Append($"{id + 1} 0 obj ").Append(Stream(pages[index])).Append(" endobj\n");
        }
        return Encoding.Latin1.GetBytes(pdf.Append("%%EOF").ToString());
    }

    // The pages of 'texts' drawn at (x, y) in 9 points over the same body on every page.
    private static byte[] WithLine(IReadOnlyList<string> texts, double x, double y, double size = 9, bool body = true) =>
        Document(texts.Select((text, index) => (body ? Body(index + 1) + "\n" : "") + Text(x, y, text, size)).ToArray());

    private static string[] FurnitureOf(PdfExtractionResult result) =>
        result.Pages.SelectMany(page => page.Regions.Where(region => region.IsPageFurniture).Select(region => region.Text)).ToArray();

    private static string[] BodyOf(PdfExtractionResult result) =>
        result.Pages.SelectMany(page => page.Regions.Where(region => !region.IsPageFurniture).Select(region => region.Text)).ToArray();

    [Theory]
    [InlineData("REVIEW FOOTER {0}", FooterY)]
    [InlineData("Confidential - {0} / 12", FooterY)]
    [InlineData("Doc-ABC rev 7 p. {0}", FooterY)]
    [InlineData("Sheet no.{0} of the approved design", FooterY)]
    [InlineData("Quarterly review, part {0}", HeaderY)]
    public void A_line_in_the_margin_that_differs_only_in_a_number_from_page_to_page_is_furniture(string pattern, double y)
    {
        var texts = Numbered(pattern, 3);
        var result = PdfTextExtractor.Extract(WithLine(texts, 72, y));

        Assert.Equal(texts, FurnitureOf(result));
        Assert.All(result.Pages, page => Assert.Equal(1, page.Regions.Count(region => region.IsPageFurniture)));
        // The body of the pages stays.
        Assert.Equal(3, BodyOf(result).Length);
        Assert.All(BodyOf(result), text => Assert.StartsWith("Body line 1 of page", text, StringComparison.Ordinal));
    }

    [Fact]
    public void A_footer_made_of_text_at_the_left_and_a_page_number_at_the_right_margin_is_furniture_and_a_title_is_not()
    {
        // The evaluator's fixture: "REVIEW FOOTER" at the left and the page number at the right on one baseline,
        // a 9-point header, and over the body of each page a title of 17 points ("PAGE 1 TECHNICAL REQUIREMENTS")
        // which lies in the top band as well (on the evaluator's Letter pages it is at y = 726 of 792).
        var pages = Enumerable.Range(1, 3).Select(page => string.Join("\n",
            Text(48, 820, "ENGINEERING REVIEW HEADER"),
            Text(48, 765, $"PAGE {page} TECHNICAL REQUIREMENTS", 17),
            Body(page),
            Text(48, 28, "REVIEW FOOTER"), Text(540, 28, page.ToString()))).ToArray();
        var result = PdfTextExtractor.Extract(Document(pages));

        Assert.Equal(["ENGINEERING REVIEW HEADER", "REVIEW FOOTER 1", "ENGINEERING REVIEW HEADER", "REVIEW FOOTER 2",
            "ENGINEERING REVIEW HEADER", "REVIEW FOOTER 3"], FurnitureOf(result));
        Assert.Equal(["PAGE 1 TECHNICAL REQUIREMENTS", "PAGE 2 TECHNICAL REQUIREMENTS", "PAGE 3 TECHNICAL REQUIREMENTS"],
            BodyOf(result).Where(text => text.StartsWith("PAGE", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("flush right")]
    [InlineData("centred")]
    public void A_number_of_another_width_does_not_move_a_footer_that_is_set_flush_right_or_centred(string alignment)
    {
        // 30 points: a digit is 16.7 wide, so the start of "REVIEW FOOTER 99" differs by more than the 12 allowed
        // from that of "REVIEW FOOTER 100" although the footer stands in the same place.
        var texts = new[] { "REVIEW FOOTER 98", "REVIEW FOOTER 99", "REVIEW FOOTER 100" };
        var pages = texts.Select((text, index) =>
        {
            var width = Width(text, 30);
            var x = alignment == "flush right" ? 523 - width : 297.5 - width / 2;
            return Text(x, FooterY, text, 30);
        }).ToArray();
        var result = PdfTextExtractor.Extract(Document(pages));
        Assert.Equal(texts, FurnitureOf(result));
    }

    [Fact]
    public void Full_width_digits_and_japanese_text_make_a_numbered_footer_as_well()
    {
        // 「設計書 第2版 - 5」 on pages 5 to 7 and 「設計書 第２版 － ５」 in full-width digits (only the digits vary).
        var ascii = new[] { "設計書 第2版 - 5", "設計書 第2版 - 6", "設計書 第2版 - 7" };
        var wide = new[] { "設計書 第２版 － １", "設計書 第２版 － ２", "設計書 第２版 － ３" };
        foreach (var texts in new[] { ascii, wide })
        {
            var characters = texts.SelectMany(text => text).Distinct().ToArray();
            var fonts = new Dictionary<int, string>
            {
                [4] = "<< /Type /Font /Subtype /Type0 /BaseFont /Gothic /Encoding /Identity-H /DescendantFonts [5 0 R] /ToUnicode 6 0 R >>",
                [5] = "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Gothic /DW 1000 >>",
                [6] = TwoByteCMap(characters.Select(character => ((int)character, character)).ToArray()),
            };
            var pages = texts.Select(text => $"BT /F1 9 Tf 1 0 0 1 72 {FooterY} Tm <{string.Concat(text.Select(character => ((int)character).ToString("X4")))}> Tj ET").ToArray();
            var result = PdfTextExtractor.Extract(Document(pages, fonts));
            Assert.Equal(texts, result.Pages.SelectMany(page => page.Regions).Select(region => region.Text).ToArray());
            Assert.Equal(texts, FurnitureOf(result));
        }
    }

    [Theory]
    [InlineData("REVIEW FOOTER {0}")]
    [InlineData("Company confidential")]
    public void Footers_that_alternate_between_the_left_and_the_right_margin_are_furniture_on_both_sides(string pattern)
    {
        // Facing pages: odd pages carry the footer at the left, even pages flush right; each side holds half the pages.
        var pages = Enumerable.Range(1, 12).Select(page =>
        {
            var text = string.Format(pattern, page);
            return Body(page) + "\n" + Text(page % 2 == 1 ? 72 : 523 - Width(text, 9), FooterY, text);
        }).ToArray();
        var result = PdfTextExtractor.Extract(Document(pages));

        Assert.Equal(Enumerable.Range(1, 12).Select(page => string.Format(pattern, page)), FurnitureOf(result));
    }

    [Theory]
    [InlineData("REVIEW FOOTER {0}")]
    [InlineData("Company confidential")]
    public void A_footer_that_drifts_a_little_from_page_to_page_is_furniture_where_its_neighbours_stand_with_it(string pattern)
    {
        // Each page sets the footer 1.5 points higher than the page before: each line is near those of the pages next to
        // it, though the first and the last are 7.5 points apart.
        var pages = Enumerable.Range(1, 6).Select(page => Body(page) + "\n" + Text(72, FooterY + (page - 1) * 1.5, string.Format(pattern, page))).ToArray();
        var result = PdfTextExtractor.Extract(Document(pages));

        Assert.Equal(Enumerable.Range(1, 6).Select(page => string.Format(pattern, page)), FurnitureOf(result));
    }

    [Fact]
    public void Running_lines_of_a_long_document_are_furniture_on_every_page()
    {
        var pages = Enumerable.Range(1, 400).Select(page => Body(page) + "\n" +
            Text(72, HeaderY, "Specification - internal") + "\n" + Text(72, FooterY, $"REVIEW FOOTER {page}")).ToArray();
        var result = PdfTextExtractor.Extract(Document(pages));

        Assert.All(result.Pages, page => Assert.Equal(2, page.Regions.Count(region => region.IsPageFurniture)));
        Assert.Equal(400, BodyOf(result).Length);
    }

    [Fact]
    public void A_footer_on_the_pages_after_a_cover_is_furniture_and_one_on_a_few_of_many_pages_is_not()
    {
        // Pages 2 to 4 of 4 carry the footer; of 6 pages, two do - fewer than half, as for a line that is the same on every page.
        string[] Cover(int count, int[] numbered) => Enumerable.Range(1, count).Select(page => Body(page) +
            (numbered.Contains(page) ? "\n" + Text(72, FooterY, $"REVIEW FOOTER {page - 1}") : "")).ToArray();

        var afterCover = PdfTextExtractor.Extract(Document(Cover(4, [2, 3, 4])));
        Assert.Equal(["REVIEW FOOTER 1", "REVIEW FOOTER 2", "REVIEW FOOTER 3"], FurnitureOf(afterCover));

        var few = PdfTextExtractor.Extract(Document(Cover(6, [2, 3])));
        Assert.Empty(FurnitureOf(few));
        Assert.Contains("REVIEW FOOTER 1", BodyOf(few));
    }

    [Fact]
    public void Body_text_in_the_middle_of_the_page_is_no_furniture_even_when_it_repeats_with_other_numbers()
    {
        var pages = Enumerable.Range(1, 3).Select(page => Body(page) + "\n" + Text(72, 420, $"Measurement series {page} summary") + "\n" +
            Text(72, 400, $"Revision {page}")).ToArray();
        var result = PdfTextExtractor.Extract(Document(pages));
        Assert.Empty(FurnitureOf(result));
    }

    [Fact]
    public void Numbered_headings_and_list_items_outside_the_band_or_at_other_places_are_no_furniture()
    {
        // "1. Overview", "2. Overview", "3. Overview" in the middle of the page; list items "1. Prepare", "2. Execute" which
        // differ in more than the number; and a heading of the same text in the top band but lower by a line on each page.
        var pages = Enumerable.Range(1, 3).Select(page => string.Join("\n",
            Body(page),
            Text(72, 700, $"{page}. Overview"),
            Text(72, 650, page == 1 ? "1. Prepare" : page == 2 ? "2. Execute" : "3. Report"),
            Text(72, 790 - (page - 1) * 20, $"{page}. Scope of the review"))).ToArray();
        var result = PdfTextExtractor.Extract(Document(pages));
        Assert.Empty(FurnitureOf(result));
    }

    [Fact]
    public void A_line_whose_text_other_than_its_number_differs_from_page_to_page_is_no_furniture()
    {
        var result = PdfTextExtractor.Extract(WithLine(["REVIEW FOOTER 1", "REVIEW NOTICE 2", "OTHER REPORT 3"], 72, FooterY));
        Assert.Empty(FurnitureOf(result));
    }

    [Fact]
    public void A_numbered_line_of_a_single_page_document_is_no_furniture_but_a_page_number_still_is()
    {
        var footer = PdfTextExtractor.Extract(WithLine(["REVIEW FOOTER 1"], 72, FooterY));
        Assert.Empty(FurnitureOf(footer));

        var pageNumber = PdfTextExtractor.Extract(WithLine(["- 1 -"], 270, FooterY));
        Assert.Equal(["- 1 -"], FurnitureOf(pageNumber));
    }

    [Fact]
    public void A_table_row_in_the_band_is_no_furniture_but_the_same_text_outside_a_table_is()
    {
        // A ruled table at the foot of each page with "Item 1", "Item 2", "Item 3" in its first column.
        string Table(int page) => string.Join("\n",
            "50 30 300 80 re S 200 30 m 200 110 l S 50 70 m 350 70 l S",
            Text(60, 90, "Name"), Text(210, 90, "Amount"), Text(60, 50, $"Item {page}"), Text(210, 50, "100"));
        var pages = Enumerable.Range(1, 3).Select(page => Body(page) + "\n" + Table(page)).ToArray();
        var withTable = PdfTextExtractor.Extract(Document(pages));
        Assert.All(Enumerable.Range(1, 3), page => Assert.NotEmpty(withTable.Tables!.GetValueOrDefault(page) ?? []));
        Assert.Empty(FurnitureOf(withTable));

        // Without the rules the same words are lines at the foot of the page, as numbered as "REVIEW FOOTER 1".
        var loose = Enumerable.Range(1, 3).Select(page => Body(page) + "\n" + Text(60, 50, $"Item {page}")).ToArray();
        Assert.Equal(["Item 1", "Item 2", "Item 3"], FurnitureOf(PdfTextExtractor.Extract(Document(loose))));
    }

    [Fact]
    public void A_line_larger_than_the_body_is_a_heading_and_no_furniture()
    {
        var pages = Enumerable.Range(1, 3).Select(page => Body(page) + "\n" + Text(72, HeaderY, $"Chapter {page} overview", 24)).ToArray();
        var result = PdfTextExtractor.Extract(Document(pages));
        Assert.Empty(FurnitureOf(result));

        // At the size of the body (10 points) it is a running header.
        var running = PdfTextExtractor.Extract(Document(Enumerable.Range(1, 3).Select(page => Body(page) + "\n" +
            Text(72, HeaderY, $"Chapter {page} overview", 10)).ToArray()));
        Assert.Equal(["Chapter 1 overview", "Chapter 2 overview", "Chapter 3 overview"], FurnitureOf(running));
    }

    [Fact]
    public void A_number_that_does_not_count_the_pages_makes_no_furniture()
    {
        // The amount carried forward at the foot of each page is text of the page; page numbers grow.
        var carried = PdfTextExtractor.Extract(WithLine(["Carried forward 1250", "Carried forward 980", "Carried forward 1400"], 72, FooterY));
        Assert.Empty(FurnitureOf(carried));
        Assert.Contains("Carried forward 980", BodyOf(carried));

        var counted = PdfTextExtractor.Extract(WithLine(["Carried forward 12", "Carried forward 13", "Carried forward 14"], 72, FooterY));
        Assert.Equal(3, FurnitureOf(counted).Length);
    }

    [Fact]
    public void Lines_that_are_the_same_text_on_most_pages_stay_furniture_when_another_page_differs_in_a_number()
    {
        // Pages 1 and 2 say "Draft 7", page 3 "Draft 8": the first two are the same text, as today.
        var result = PdfTextExtractor.Extract(WithLine(["Draft 7", "Draft 7", "Draft 8"], 72, FooterY));
        Assert.Contains("Draft 7", FurnitureOf(result));
        Assert.Equal(2, FurnitureOf(result).Count(text => text == "Draft 7"));

        // A line that is the same on every page, with no number at all, and a page number: unchanged.
        var plain = PdfTextExtractor.Extract(Document(Enumerable.Range(1, 3).Select(page => Body(page) + "\n" +
            Text(72, FooterY, "Internal use only") + "\n" + Text(270, 12, $"Page {page} of 3")).ToArray()));
        Assert.Equal(6, FurnitureOf(plain).Length);
    }

    [Theory]
    [InlineData("visible")]
    [InlineData("sanitized")]
    public async Task A_numbered_footer_is_listed_as_reference_in_visible_output_and_left_out_when_sanitized(string policy)
    {
        var pages = Enumerable.Range(1, 3).Select(page => Body(page) + "\n" + Text(72, FooterY, $"REVIEW FOOTER {page}")).ToArray();
        var source = Path.Combine(root, $"footer-{policy}.pdf");
        await File.WriteAllBytesAsync(source, Document(pages));
        var markdownPath = Path.Combine(root, $"footer-{policy}.md");
        await new DocumentService(null, null, discoverPdfRasterizer: false).ExportReadableAsync(
            new ReadableDocumentExportOptions(source, markdownPath, ContentPolicy: policy, IncludePdfFallbackImages: false));
        var markdown = (await File.ReadAllTextAsync(markdownPath)).Replace("\r\n", "\n");

        foreach (var page in Enumerable.Range(1, 3))
            Assert.Contains($"Body line 1 of page {page} sets out", markdown, StringComparison.Ordinal);
        if (policy == "sanitized")
        {
            Assert.DoesNotContain("REVIEW FOOTER", markdown, StringComparison.Ordinal);
            return;
        }
        var reference = markdown.IndexOf("Document headers and footers (reference)", StringComparison.Ordinal);
        Assert.True(reference > 0);
        // Listed once, as its first and last forms, after the body.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(markdown, "REVIEW FOOTER 1 … REVIEW FOOTER 3"));
        Assert.True(markdown.IndexOf("REVIEW FOOTER 1", StringComparison.Ordinal) > reference);
        Assert.DoesNotContain("REVIEW FOOTER 2", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_footer_counted_past_nine_pages_is_listed_from_its_first_to_its_last_page()
    {
        // "REVIEW FOOTER 1" is part of "REVIEW FOOTER 12" as text, and page 10 sorts before page 2 by name: neither may
        // decide the listing.
        var pages = Enumerable.Range(1, 12).Select(page => Body(page) + "\n" + Text(72, FooterY, $"REVIEW FOOTER {page}")).ToArray();
        var source = Path.Combine(root, "twelve.pdf");
        await File.WriteAllBytesAsync(source, Document(pages));
        var markdownPath = Path.Combine(root, "twelve.md");
        await new DocumentService(null, null, discoverPdfRasterizer: false).ExportReadableAsync(
            new ReadableDocumentExportOptions(source, markdownPath, IncludePdfFallbackImages: false));
        var markdown = await File.ReadAllTextAsync(markdownPath);

        var line = Assert.Single(markdown.Split('\n'), text => text.Contains("REVIEW FOOTER", StringComparison.Ordinal));
        Assert.EndsWith("REVIEW FOOTER 1 … REVIEW FOOTER 12", line.TrimEnd('\r'), StringComparison.Ordinal);
    }
}
