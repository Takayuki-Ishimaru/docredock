using System.Text;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;

namespace DocRedock.Tests.Pdf;

/// <summary>v0.2.10 evaluation, section 5: native text and vector drawing inside a Form XObject that
/// a page draws next to its own content disappeared without a warning, because only the page's
/// content stream reached text and vector analysis. A drawn form is now analyzed where it is drawn;
/// one that cannot be analyzed is reported instead of vanishing.</summary>
public sealed class PdfFormXObjectContentTests
{
    /// <summary>A one-page PDF whose content stream is <paramref name="page"/>, with the XObject
    /// names in <paramref name="resources"/> and the extra objects in <paramref name="objects"/>.</summary>
    internal static byte[] PagePdf(string page, string resources = "", string objects = "") => Encoding.Latin1.GetBytes(
        "%PDF-1.4\n1 0 obj << /Type /Page /MediaBox [0 0 612 792] /Contents 2 0 R" +
        (resources.Length == 0 ? "" : " /Resources << /XObject << " + resources + " >> >>") + " >> endobj\n" +
        "2 0 obj << /Length " + page.Length + " >> stream\n" + page + "\nendstream endobj\n" + objects + "%%EOF");

    internal static string Form(int id, string content, string matrix = "", string resources = "",
        string bbox = "0 0 612 792", string filter = "") =>
        id + " 0 obj << /Type /XObject /Subtype /Form /BBox [" + bbox + "]" +
        (matrix.Length == 0 ? "" : " /Matrix [" + matrix + "]") +
        (resources.Length == 0 ? "" : " /Resources << /XObject << " + resources + " >> >>") +
        (filter.Length == 0 ? "" : " /Filter " + filter) +
        " /Length " + content.Length + " >> stream\n" + content + "\nendstream endobj\n";

    private const string Table =
        "60 700 m 460 700 l S\n60 660 m 460 660 l S\n60 620 m 460 620 l S\n60 580 m 460 580 l S\n" +
        "60 580 m 60 700 l S\n193 580 m 193 700 l S\n326 580 m 326 700 l S\n460 580 m 460 700 l S\n" +
        "BT 1 0 0 1 70 685 Tm (Item) Tj ET\nBT 1 0 0 1 203 685 Tm (Owner) Tj ET\nBT 1 0 0 1 336 685 Tm (Status) Tj ET\n" +
        "BT 1 0 0 1 70 645 Tm (Design) Tj ET\nBT 1 0 0 1 203 645 Tm (Aoki) Tj ET\nBT 1 0 0 1 336 645 Tm (Done) Tj ET\n" +
        "BT 1 0 0 1 70 605 Tm (Build) Tj ET\nBT 1 0 0 1 203 605 Tm (Sato) Tj ET\nBT 1 0 0 1 336 605 Tm (Open) Tj ET\n";

    private const string LowerBlock =
        "BT 1 0 0 1 72 520 Tm (FORM_TEXT_SENTINEL: approved) Tj ET\n" +
        "100 400 110 44 re S\n360 400 110 44 re S\n" +
        "BT 1 0 0 1 140 418 Tm (START) Tj ET\nBT 1 0 0 1 405 418 Tm (END) Tj ET\n" +
        "210 422 m 350 422 l S\n360 422 m 350 427 l 350 417 l h f\n";

    private const string StrayDiagonal = "480 300 m 540 360 l S\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Form_content_is_extracted_exactly_like_the_same_content_painted_on_the_page(bool withUnresolvedLine)
    {
        var lower = LowerBlock + (withUnresolvedLine ? StrayDiagonal : string.Empty);
        var flat = PdfTextExtractor.Extract(PagePdf(Table + lower));
        var form = PdfTextExtractor.Extract(PagePdf(Table + "/Fm1 Do\n", "/Fm1 5 0 R", Form(5, lower)));

        Assert.Contains("FORM_TEXT_SENTINEL: approved", form.Text, StringComparison.Ordinal);
        Assert.Equal(flat.Text, form.Text);
        // Warnings included: the form page must not read as complete when the flat page does not.
        Assert.Equal(flat.Diagnostics, form.Diagnostics);
        Assert.Equal(withUnresolvedLine, form.Diagnostics!.Any(d => d.StartsWith("VisualSemanticProjectionUnavailable:", StringComparison.Ordinal)));
        Assert.Equal(Shape(flat), Shape(form));
        Assert.Contains(form.VisualProjections![1].Graph.Nodes, node => node.Label == "START");
        Assert.Contains(form.VisualProjections![1].Graph.Nodes, node => node.Label == "END");
        Assert.Null(form.Pages[0].UnparsedFormXObjects);
        Assert.DoesNotContain(form.Diagnostics!, d => d.StartsWith("PdfFormXObjectUnparsed", StringComparison.Ordinal));
    }

    private static string Shape(PdfExtractionResult result) => string.Join("|",
        (result.Tables?.GetValueOrDefault(1) ?? []).Select(table => $"{table.Rows.Count}x{table.Rows[0].Cells.Count}")
        .Concat(result.VisualProjections![1].Graph.Nodes.Select(node => "node:" + node.Label).Order(StringComparer.Ordinal))
        .Concat(result.VisualProjections[1].Graph.Edges.Select(edge => $"edge:{edge.Resolution}")));

    [Fact]
    public void Form_text_is_placed_by_the_form_matrix_and_the_caller_ctm()
    {
        var pdf = PagePdf("BT 1 0 0 1 10 700 Tm (PAGE) Tj ET\nq 2 0 0 2 0 0 cm /Fm1 Do Q", "/Fm1 5 0 R",
            Form(5, "BT 1 0 0 1 10 20 Tm (INSIDE) Tj ET", "1 0 0 1 100 50"));

        var region = Assert.Single(PdfTextExtractor.Extract(pdf).Pages[0].Regions, item => item.Text == "INSIDE");

        // (10,20) -> /Matrix [1 0 0 1 100 50] -> (110,70) -> CTM [2 0 0 2 0 0] -> (220,140).
        Assert.Equal(220d, region.BoundingBox.X, 6);
        Assert.Equal(140d, region.BoundingBox.Y, 6);
    }

    [Fact]
    public void Nested_names_resolve_in_the_scope_of_the_form_that_draws_them()
    {
        // The page's /Fm1 is object 5; object 5's own /Fm1 is object 7. Resolving the nested draw
        // in the page scope would draw object 5 again (a cycle) and lose DEEP.
        var pdf = PagePdf("BT 1 0 0 1 10 700 Tm (PAGE) Tj ET\n/Fm1 Do", "/Fm1 5 0 R",
            Form(5, "BT 1 0 0 1 10 600 Tm (OUTER) Tj ET\n/Fm1 Do", resources: "/Fm1 7 0 R") +
            Form(7, "BT 1 0 0 1 10 500 Tm (DEEP) Tj ET"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["PAGE", "OUTER", "DEEP"], result.Pages[0].Regions.OrderBy(region => region.ReadingOrder).Select(region => region.Text));
        Assert.Empty(result.Diagnostics!);
    }

    [Fact]
    public void Unbalanced_graphics_states_inside_a_form_cannot_move_the_page_content_after_it()
    {
        // The form restores two states it never saved and leaves one open. Inlined verbatim, its
        // Q Q would undo the page's 3x scale and SCALED would land at (50,50) instead of (30,30).
        var pdf = PagePdf("q 3 0 0 3 0 0 cm /Fm1 Do BT 1 0 0 1 10 10 Tm (SCALED) Tj ET Q", "/Fm1 5 0 R",
            Form(5, "Q Q 5 0 0 5 0 0 cm q 7 0 0 7 0 0 cm BT 1 0 0 1 1 1 Tm (FORMTEXT) Tj ET"));

        var regions = PdfTextExtractor.Extract(pdf).Pages[0].Regions;

        var scaled = Assert.Single(regions, region => region.Text == "SCALED");
        Assert.Equal(30d, scaled.BoundingBox.X, 6);
        Assert.Equal(30d, scaled.BoundingBox.Y, 6);
        var inside = Assert.Single(regions, region => region.Text == "FORMTEXT");
        Assert.Equal(105d, inside.BoundingBox.X, 6);
        Assert.Equal(105d, inside.BoundingBox.Y, 6);
    }

    [Fact]
    public void A_draw_written_inside_a_string_a_dictionary_or_a_comment_is_not_a_draw()
    {
        // Only the last line really draws /Fm1; the other three merely contain its text.
        var pdf = PagePdf("BT 1 0 0 1 10 700 Tm (/Fm1 Do) Tj ET\n/Span << /ActualText (/Fm1 Do) >> BDC EMC\n% /Fm1 Do\n/Fm1 Do",
            "/Fm1 5 0 R", Form(5, "BT 1 0 0 1 10 600 Tm (INSIDE) Tj ET"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["/Fm1 Do", "INSIDE"], result.Pages[0].Regions.OrderBy(region => region.ReadingOrder).Select(region => region.Text));
    }

    [Theory]
    [InlineData("0.0000001 0 0 0.0000001 0 0", "1 0 0 1 10 10", 10e-7)]
    [InlineData("1000000000000000000000 0 0 1000000000000000000000 0 0", "1 0 0 1 0.00000000000000000001 0.00000000000000000001", 10)]
    public void A_form_matrix_far_from_one_is_written_back_in_a_form_the_text_parser_reads(string matrix, string textMatrix, double expected)
    {
        // .NET prints these as 1E-07 and 1E+21, which the text parser's number pattern does not
        // read: the form's text would silently lose its /Matrix. The page draws nothing else, so
        // the enormous glyph box of the second case cannot merge with another line.
        var pdf = PagePdf("/Fm1 Do", "/Fm1 5 0 R", Form(5, $"BT {textMatrix} Tm (SCALED) Tj ET", matrix));

        var region = Assert.Single(PdfTextExtractor.Extract(pdf).Pages[0].Regions, item => item.Text == "SCALED");

        Assert.Equal(expected, region.BoundingBox.X, 6);
    }

    [Fact]
    public void Forms_that_draw_each_other_are_inlined_once_without_a_warning()
    {
        var pdf = PagePdf("BT 1 0 0 1 10 700 Tm (PAGE) Tj ET\n/Fm1 Do", "/Fm1 5 0 R",
            Form(5, "BT 1 0 0 1 10 600 Tm (ONE) Tj ET\n/Fm2 Do", resources: "/Fm2 7 0 R") +
            Form(7, "BT 1 0 0 1 10 500 Tm (TWO) Tj ET\n/Fm1 Do", resources: "/Fm1 5 0 R"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["PAGE", "ONE", "TWO"], result.Pages[0].Regions.OrderBy(region => region.ReadingOrder).Select(region => region.Text));
        Assert.Null(result.Pages[0].UnparsedFormXObjects);
        Assert.Empty(result.Diagnostics!);
    }

    [Fact]
    public void Forms_the_page_never_draws_are_neither_analyzed_nor_reported()
    {
        // Object 5 is offered by the page resources but not drawn; object 7 (an annotation
        // appearance, say) is not reachable from the page at all.
        var pdf = PagePdf("BT 1 0 0 1 10 700 Tm (BODY) Tj ET", "/Fm1 5 0 R",
            Form(5, "BT 1 0 0 1 10 600 Tm (UNUSED) Tj ET") +
            Form(7, "BT 1 0 0 1 10 500 Tm (APPEARANCE) Tj ET", filter: "/LZWDecode"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal("BODY", result.Text);
        Assert.Empty(result.Diagnostics!);
        Assert.Null(result.Pages[0].UnparsedFormXObjects);
    }

    [Fact]
    public void A_form_nested_deeper_than_the_limit_is_reported_where_it_paints()
    {
        // Forms 11..19 each draw the next one, one level deeper and 10pt further right. Levels 1-8
        // are analyzed; the ninth is past the nesting limit.
        var forms = new StringBuilder();
        for (var level = 1; level <= 9; level++)
            forms.Append(Form(10 + level, $"BT 1 0 0 1 0 {800 - level * 20} Tm (LEVEL{level}) Tj ET" + (level < 9 ? "\n/Next Do" : string.Empty),
                "1 0 0 1 10 0", level < 9 ? $"/Next {11 + level} 0 R" : string.Empty, "0 0 100 100"));
        var pdf = PagePdf("BT 1 0 0 1 10 780 Tm (PAGE) Tj ET\n/Next Do", "/Next 11 0 R", forms.ToString());

        var result = PdfTextExtractor.Extract(pdf);
        var page = result.Pages[0];

        for (var level = 1; level <= 8; level++) Assert.Contains($"LEVEL{level}", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("LEVEL9", result.Text, StringComparison.Ordinal);
        var unparsed = Assert.Single(page.UnparsedFormXObjects!);
        Assert.Equal(PdfFormXObjectUnparsedReason.NestingLimit, unparsed.Reason);
        Assert.Equal("Next", unparsed.Name);
        // Eight translations are in force at the draw, and the ninth /Matrix adds one more.
        Assert.Equal(90d, unparsed.Bounds!.X, 6);
        Assert.Equal(0d, unparsed.Bounds.Y, 6);
        Assert.Equal(100d, unparsed.Bounds.Width, 6);
        var diagnostic = Assert.Single(result.Diagnostics!, d => d.StartsWith("PdfFormXObjectUnparsed:", StringComparison.Ordinal));
        Assert.Contains("PDF page 1: 1 Form XObject draw(s) could not be analyzed (nested deeper than 8 levels)", diagnostic, StringComparison.Ordinal);
        Assert.Contains("Compare with page 1 of the original.", diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/LZWDecode")]
    [InlineData("[/ASCII85Decode /RunLengthDecode]")]
    public void A_form_whose_stream_cannot_be_decoded_is_reported_and_its_bytes_are_never_parsed(string filter)
    {
        // The payload happens to be readable operators: parsing still-encoded bytes would invent
        // text, so an undecodable stream must be reported and not read.
        var pdf = PagePdf("BT 1 0 0 1 10 700 Tm (BODY) Tj ET\nq 1 0 0 1 20 30 cm /Fm1 Do Q", "/Fm1 5 0 R",
            Form(5, "BT 1 0 0 1 10 10 Tm (ENCODED) Tj ET", bbox: "0 0 200 100", filter: filter));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.DoesNotContain("ENCODED", result.Text, StringComparison.Ordinal);
        var unparsed = Assert.Single(result.Pages[0].UnparsedFormXObjects!);
        Assert.Equal(PdfFormXObjectUnparsedReason.UnreadableStream, unparsed.Reason);
        Assert.Equal(new Geometry("pdf-user-space", 20, 30, 200, 100), unparsed.Bounds);
        Assert.Contains(result.Diagnostics!, d => d.StartsWith("PdfFormXObjectUnparsed: PDF page 1: 1 Form XObject draw(s) could not be analyzed (stream could not be decoded)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_form_object_without_a_stream_paints_nothing_and_is_not_reported()
    {
        var pdf = PagePdf("BT 1 0 0 1 10 700 Tm (BODY) Tj ET\n/Fm1 Do", "/Fm1 5 0 R",
            "5 0 obj << /Type /XObject /Subtype /Form /BBox [0 0 10 10] >> endobj\n");

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal("BODY", result.Text);
        Assert.Null(result.Pages[0].UnparsedFormXObjects);
        Assert.DoesNotContain(result.Diagnostics!, d => d.StartsWith("PdfFormXObjectUnparsed", StringComparison.Ordinal));
    }

    [Fact]
    public void Form_content_past_the_page_allowance_is_reported_instead_of_inlined()
    {
        var content = "BT 1 0 0 1 10 10 Tm (" + new string('x', 60) + ") Tj ET";
        var pdf = PagePdf("BT 1 0 0 1 10 700 Tm (BODY) Tj ET\n/Fm1 Do", "/Fm1 5 0 R", Form(5, content, bbox: "0 0 50 50"));

        var result = PdfTextExtractor.Extract(pdf, new PdfExtractionOptions(MaxExpandedStreamBytes: 40));

        Assert.DoesNotContain("xxxx", result.Text, StringComparison.Ordinal);
        Assert.Equal(PdfFormXObjectUnparsedReason.SizeLimit, Assert.Single(result.Pages[0].UnparsedFormXObjects!).Reason);
        Assert.Contains(result.Diagnostics!, d => d.Contains("(inlined form content limit reached)", StringComparison.Ordinal));
    }

    [Fact]
    public void The_document_allowance_is_shared_across_pages()
    {
        // Each page draws the same 34-character form. With 40 characters for the whole document,
        // page 1 inlines it and page 2 must report it.
        const string form = "BT 1 0 0 1 10 10 Tm (SHARED) Tj ET";
        const string page = "BT 1 0 0 1 10 700 Tm (BODY) Tj ET\n/Fm1 Do";
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n" +
            "1 0 obj << /Type /Page /Contents 2 0 R /Resources << /XObject << /Fm1 5 0 R >> >> >> endobj\n" +
            "2 0 obj << /Length " + page.Length + " >> stream\n" + page + "\nendstream endobj\n" +
            "3 0 obj << /Type /Page /Contents 4 0 R /Resources << /XObject << /Fm1 5 0 R >> >> >> endobj\n" +
            "4 0 obj << /Length " + page.Length + " >> stream\n" + page + "\nendstream endobj\n" +
            Form(5, form) + "%%EOF");

        var result = PdfTextExtractor.Extract(pdf, new PdfExtractionOptions(MaxExpandedStreamBytes: 40));

        Assert.Contains("SHARED", result.Pages[0].Text, StringComparison.Ordinal);
        Assert.Null(result.Pages[0].UnparsedFormXObjects);
        Assert.DoesNotContain("SHARED", result.Pages[1].Text, StringComparison.Ordinal);
        Assert.Equal(PdfFormXObjectUnparsedReason.SizeLimit, Assert.Single(result.Pages[1].UnparsedFormXObjects!).Reason);
    }

    [Fact]
    public void An_unresolvable_draw_is_reported_as_a_form_only_when_the_document_has_no_images()
    {
        // The page draws /Fm9, which its resources do not name.
        const string page = "BT 1 0 0 1 10 700 Tm (BODY) Tj ET\nq 100 0 0 100 50 50 cm /Fm9 Do Q";
        var formsOnly = PdfTextExtractor.Extract(PagePdf(page, "/Fm1 5 0 R", Form(5, "BT 1 0 0 1 10 10 Tm (UNUSED) Tj ET")));
        var withImage = PdfTextExtractor.Extract(PagePdf(page, "/Fm1 5 0 R",
            Form(5, "BT 1 0 0 1 10 10 Tm (UNUSED) Tj ET") + "6 0 obj << /Type /XObject /Subtype /Image /Width 8 /Height 8 >> endobj\n"));

        var unparsed = Assert.Single(formsOnly.Pages[0].UnparsedFormXObjects!);
        Assert.Equal(PdfFormXObjectUnparsedReason.UnresolvedResource, unparsed.Reason);
        Assert.Null(unparsed.Bounds);
        Assert.Equal(0, formsOnly.Pages[0].EmbeddedImageCount);
        // With image XObjects around, the image scan already reports the draw as an image.
        Assert.Null(withImage.Pages[0].UnparsedFormXObjects);
        Assert.Equal(1, withImage.Pages[0].EmbeddedImageCount);
        Assert.Contains(withImage.Diagnostics!, d => d.StartsWith("PdfEmbeddedImageOmitted:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_page_drawn_entirely_by_a_form_is_native_text_not_an_image_only_page()
    {
        var pdf = PagePdf("/Fm1 Do", "/Fm1 5 0 R", Form(5, "BT 1 0 0 1 10 700 Tm (ONLY_IN_FORM) Tj ET"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.False(result.Pages[0].IsImageOnly);
        Assert.Equal("ONLY_IN_FORM", result.Text);
        Assert.DoesNotContain(result.Diagnostics!, d => d.StartsWith("PdfRasterizerUnavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void An_inline_image_inside_a_form_is_still_counted_once()
    {
        var pdf = PagePdf("BT 1 0 0 1 10 700 Tm (BODY) Tj ET\n/Fm1 Do", "/Fm1 5 0 R",
            Form(5, "q 50 0 0 40 10 20 cm BI /W 2 /H 2 /CS /G /BPC 8 ID 0000 EI Q"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(1, result.Pages[0].EmbeddedImageCount);
        Assert.Equal(new Geometry("pdf-user-space", 10, 20, 50, 40), Assert.Single(result.Pages[0].EmbeddedImages!));
        Assert.Single(result.Diagnostics!, d => d.StartsWith("PdfEmbeddedImageOmitted:", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_contents_references_image_and_font_streams_are_never_read_as_page_content()
    {
        // No page names its /Contents, so every stream is a candidate page. The image samples and
        // the font program happen to contain text operators; neither may reach the text.
        const string page = "BT 1 0 0 1 10 700 Tm (REAL_TEXT) Tj ET";
        const string samples = "BT 1 0 0 1 10 10 Tm (IMAGE_SAMPLES) Tj ET";
        const string program = "BT 1 0 0 1 10 10 Tm (FONT_PROGRAM) Tj ET";
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n" +
            "2 0 obj << /Length " + page.Length + " >> stream\n" + page + "\nendstream endobj\n" +
            "3 0 obj << /Type /XObject /Subtype /Image /Width 4 /Height 4 /Length " + samples.Length + " >> stream\n" + samples + "\nendstream endobj\n" +
            "4 0 obj << /Length1 " + program.Length + " /Length " + program.Length + " >> stream\n" + program + "\nendstream endobj\n%%EOF");

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal("REAL_TEXT", result.Text);
    }

    [Fact]
    public void An_image_drawn_by_a_form_is_counted_once_after_the_form_is_inlined()
    {
        var pdf = PagePdf("BT 1 0 0 1 10 700 Tm (BODY) Tj ET\n/Fm1 Do", "/Fm1 5 0 R",
            Form(5, "BT 1 0 0 1 10 600 Tm (CAPTION) Tj ET\nq 100 0 0 50 5 5 cm /Im1 Do Q", resources: "/Im1 6 0 R") +
            "6 0 obj << /Type /XObject /Subtype /Image /Width 8 /Height 8 >> endobj\n");

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Contains("CAPTION", result.Text, StringComparison.Ordinal);
        Assert.Equal(1, result.Pages[0].EmbeddedImageCount);
        Assert.Equal(new Geometry("pdf-user-space", 5, 5, 100, 50), Assert.Single(result.Pages[0].EmbeddedImages!));
    }
}
