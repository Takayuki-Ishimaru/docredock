using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.PdfFontResourceScopeTests;

namespace DocRedock.Tests.Pdf;

/// <summary>v0.2.11 evaluation, section 7: text a Form XObject draws outside its /BBox is clipped
/// away on screen, yet CLIPPED_SENTINEL reached the visible Markdown with no warning. Text that no
/// viewer shows - entirely outside a form's /BBox, a clipping path or the page, or on a layer that
/// is off when the document opens - is now extracted as hidden content, and text whose glyphs may
/// still show is never hidden.</summary>
public sealed class PdfTextVisibilityTests
{
    private const string Helvetica = "10 0 obj << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj\n";

    private static byte[] PagePdf(string content, string resources = "", string objects = "", string boxes = "/MediaBox [0 0 612 792]") =>
        Document(Object(1, $"<< /Type /Page {boxes} /Contents 2 0 R /Resources << /Font << /F1 10 0 R >> {resources} >> >>"),
            Stream(2, "", content), Helvetica, objects);

    private static string Form(int id, string content, string bbox, string extra = "", string resources = "/Font << /F1 10 0 R >>") =>
        Stream(id, $"/Type /XObject /Subtype /Form /BBox [{bbox}] /Resources << {resources} >>{extra}", content);

    private static string[] Visible(PdfExtractionResult result) =>
        result.Pages.SelectMany(page => page.Regions.OrderBy(region => region.ReadingOrder)).Select(region => region.Text).ToArray();

    private static (string Text, PdfHiddenTextReason Reason)[] Hidden(PdfExtractionResult result) =>
        result.Pages.SelectMany(page => page.HiddenRegions ?? []).Select(hidden => (hidden.Region.Text, hidden.Reason)).ToArray();

    [Fact]
    public void Text_a_form_draws_outside_its_bbox_is_hidden_content()
    {
        // The evaluation's new-bbox.pdf: the form's /BBox holds VISIBLE_TEXT; CLIPPED_SENTINEL is
        // drawn by the same form above the box, and no viewer shows it.
        var pdf = PagePdf("BT /F1 12 Tf 72 720 Td (PAGE_TEXT) Tj ET\nq 1 0 0 1 100 400 cm /Fm1 Do Q", "/XObject << /Fm1 20 0 R >>",
            Form(20, "BT /F1 12 Tf 10 40 Td (VISIBLE_TEXT) Tj ET\nBT /F1 12 Tf 10 200 Td (CLIPPED_SENTINEL) Tj ET", "0 0 200 100"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["PAGE_TEXT", "VISIBLE_TEXT"], Visible(result));
        Assert.Equal([("CLIPPED_SENTINEL", PdfHiddenTextReason.OutsideVisibleArea)], Hidden(result));
        Assert.Empty(result.Diagnostics!);
    }

    [Theory]
    [InlineData("10 -150", "below")]
    [InlineData("300 40", "to the right")]
    [InlineData("-400 40", "to the left")]
    public void Text_entirely_on_any_side_of_the_bbox_is_hidden(string position, string side)
    {
        var pdf = PagePdf("q 1 0 0 1 200 400 cm /Fm1 Do Q", "/XObject << /Fm1 20 0 R >>",
            Form(20, $"BT /F1 12 Tf 10 40 Td (INSIDE) Tj ET\nBT /F1 12 Tf {position} Td (OUTSIDE) Tj ET", "0 0 200 100"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.True(Visible(result).SequenceEqual(["INSIDE"]), side);
        Assert.Equal(["OUTSIDE"], Hidden(result).Select(hidden => hidden.Text));
    }

    [Theory]
    [InlineData("150 40")]   // starts inside, runs past the right edge
    [InlineData("-20 40")]   // starts just left of the box and runs into it
    [InlineData("10 -8")]    // baseline below the box, ascenders inside it
    public void Text_whose_glyphs_may_reach_into_the_bbox_stays_visible(string position)
    {
        var pdf = PagePdf("q 1 0 0 1 200 400 cm /Fm1 Do Q", "/XObject << /Fm1 20 0 R >>",
            Form(20, $"BT /F1 12 Tf {position} Td (EDGE_TEXT) Tj ET", "0 0 200 100"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["EDGE_TEXT"], Visible(result));
        Assert.Empty(Hidden(result));
    }

    [Fact]
    public void A_bbox_is_placed_by_the_form_matrix_and_the_ctm_that_draw_it()
    {
        // /Matrix scales the form by 2 and the page translates it: the box covers page space
        // (100,100)-(300,300). Text at form (60,60) lands at (220,220), inside; at (120,60) it lands
        // at (340,220), outside.
        var pdf = PagePdf("q 1 0 0 1 100 100 cm /Fm1 Do Q", "/XObject << /Fm1 20 0 R >>",
            Form(20, "BT /F1 6 Tf 60 60 Td (IN) Tj ET\nBT /F1 6 Tf 120 60 Td (OUT) Tj ET", "0 0 100 100", " /Matrix [2 0 0 2 0 0]"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["IN"], Visible(result));
        Assert.Equal(["OUT"], Hidden(result).Select(hidden => hidden.Text));
    }

    [Fact]
    public void Nested_forms_clip_to_the_overlap_of_their_boxes()
    {
        // The inner box reaches past the outer one; text in that overhang is clipped by the outer.
        var pdf = PagePdf("q 1 0 0 1 100 100 cm /Outer Do Q", "/XObject << /Outer 20 0 R >>",
            Form(20, "/Inner Do", "0 0 200 200", resources: "/XObject << /Inner 21 0 R >>") +
            Form(21, "BT /F1 12 Tf 20 20 Td (BOTH) Tj ET\nBT /F1 12 Tf 20 350 Td (INNER_ONLY) Tj ET", "0 0 400 400"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["BOTH"], Visible(result));
        Assert.Equal(["INNER_ONLY"], Hidden(result).Select(hidden => hidden.Text));
    }

    [Fact]
    public void Text_outside_a_clipping_path_is_hidden_until_the_graphics_state_is_restored()
    {
        var pdf = PagePdf("q 0 0 300 300 re W n\n" +
            "BT /F1 12 Tf 50 50 Td (INSIDE_CLIP) Tj ET\nBT /F1 12 Tf 400 500 Td (OUTSIDE_CLIP) Tj ET\nQ\n" +
            "BT /F1 12 Tf 400 500 Td (AFTER_RESTORE) Tj ET");

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["AFTER_RESTORE", "INSIDE_CLIP"], Visible(result).Order(StringComparer.Ordinal));
        Assert.Equal(["OUTSIDE_CLIP"], Hidden(result).Select(hidden => hidden.Text));
    }

    [Fact]
    public void A_clipping_path_with_no_area_does_not_hide_text()
    {
        var pdf = PagePdf("q 0 0 m 300 0 l W n\nBT /F1 12 Tf 50 50 Td (STILL_SHOWN) Tj ET\nQ");

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["STILL_SHOWN"], Visible(result));
        Assert.Empty(Hidden(result));
    }

    [Fact]
    public void Text_off_the_page_or_outside_the_crop_box_is_hidden()
    {
        var pdf = PagePdf(
            "BT /F1 12 Tf 72 700 Td (ON_PAGE) Tj ET\nBT /F1 12 Tf 900 700 Td (OFF_PAGE) Tj ET\n" +
            "BT /F1 12 Tf 72 10 Td (BLEED_SLUG) Tj ET",
            boxes: "/MediaBox [0 0 612 792] /CropBox [0 36 612 792]");

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["ON_PAGE"], Visible(result));
        Assert.Equal(["BLEED_SLUG", "OFF_PAGE"], Hidden(result).Select(hidden => hidden.Text).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Text_sized_by_the_text_matrix_is_measured_with_it()
    {
        // A 1pt font scaled to 12pt by Tm: the text is 12pt tall and its box must be measured so.
        var pdf = PagePdf("q 1 0 0 1 200 400 cm /Fm1 Do Q", "/XObject << /Fm1 20 0 R >>",
            Form(20, "BT /F1 1 Tf 12 0 0 12 10 -8 Tm (SCALED_EDGE) Tj ET\nBT /F1 1 Tf 12 0 0 12 10 160 Tm (SCALED_OUT) Tj ET", "0 0 200 100"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["SCALED_EDGE"], Visible(result));
        Assert.Equal(["SCALED_OUT"], Hidden(result).Select(hidden => hidden.Text));
    }

    [Fact]
    public void Lines_of_rotated_text_are_followed_through_the_text_matrix()
    {
        // Each Td moves one line along the rotated text space - to the right on the page. Moving the
        // naive (untransformed) way would carry later lines below the page and hide them.
        var lines = string.Concat(Enumerable.Range(1, 20).Select(line => $"(LINE{line:D2}) Tj 0 -14 Td\n"));
        var pdf = PagePdf($"BT /F1 12 Tf 0 1 -1 0 300 100 Tm\n{lines}ET");

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(20, Visible(result).Length);
        Assert.Empty(Hidden(result));
    }

    [Fact]
    public void Vertical_text_is_measured_down_the_page()
    {
        // An Identity-V font hangs its glyphs below the origin: text starting above the box runs
        // down into it and must stay visible; a horizontal run from the same origin would miss it.
        var pdf = Document(
            Object(1, "<< /Type /Page /MediaBox [0 0 612 792] /Contents 2 0 R /Resources << /Font << /V1 11 0 R >> /XObject << /Fm1 20 0 R >> >> >>"),
            Stream(2, "", "q 1 0 0 1 100 100 cm /Fm1 Do Q"),
            Object(11, "<< /Type /Font /Subtype /Type0 /BaseFont /Mincho /Encoding /Identity-V >>"),
            Stream(20, "/Type /XObject /Subtype /Form /BBox [0 0 100 200] /Resources << /Font << /V1 11 0 R >> >>",
                "BT /V1 12 Tf 50 240 Td <00410042004300440045004600470048> Tj ET"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Single(Visible(result));
        Assert.Empty(Hidden(result));
    }

    [Fact]
    public void Text_without_a_font_size_is_never_judged_hidden()
    {
        var pdf = PagePdf("q 0 0 100 100 re W n\nBT 400 500 Td (NO_FONT) Tj ET\nQ");

        Assert.Equal(["NO_FONT"], Visible(PdfTextExtractor.Extract(pdf)));
    }

    [Fact]
    public void A_page_whose_only_text_is_hidden_is_a_text_page_not_an_image_only_page()
    {
        var pdf = PagePdf("BT /F1 12 Tf 900 700 Td (ONLY_OFF_PAGE) Tj ET");

        var result = PdfTextExtractor.Extract(pdf);
        var page = Assert.Single(result.Pages);

        Assert.False(page.IsImageOnly);
        Assert.Empty(page.Regions);
        Assert.Equal(["ONLY_OFF_PAGE"], Hidden(result).Select(hidden => hidden.Text));
        Assert.DoesNotContain(result.Diagnostics!, d => d.StartsWith("PdfRasterizerUnavailable", StringComparison.Ordinal));
    }

    /// <summary>A catalog whose default configuration turns off <paramref name="off"/> of the two
    /// layers 30 (ON_LAYER) and 31 (OFF_LAYER).</summary>
    private static string Layers(string configuration) =>
        Object(40, $"<< /Type /Catalog /Pages 41 0 R /OCProperties << /OCGs [30 0 R 31 0 R] /D << {configuration} >> >> >>") +
        Object(30, "<< /Type /OCG /Name (Shown) >>") + Object(31, "<< /Type /OCG /Name (Hidden) >>");

    [Theory]
    [InlineData("/OFF [31 0 R]")]
    [InlineData("/BaseState /OFF /ON [30 0 R]")]
    public void Text_on_a_layer_that_is_off_when_the_document_opens_is_hidden_content(string configuration)
    {
        var pdf = PagePdf("/OC /L1 BDC BT /F1 12 Tf 72 700 Td (ON_LAYER) Tj ET EMC\n" +
                "/OC /L2 BDC BT /F1 12 Tf 72 650 Td (OFF_LAYER) Tj ET EMC\nBT /F1 12 Tf 72 600 Td (NO_LAYER) Tj ET",
            "/Properties << /L1 30 0 R /L2 31 0 R >>", Layers(configuration));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["ON_LAYER", "NO_LAYER"], Visible(result));
        Assert.Equal([("OFF_LAYER", PdfHiddenTextReason.HiddenLayer)], Hidden(result));
        Assert.Empty(result.Diagnostics!);
    }

    [Fact]
    public void A_form_whose_own_layer_is_off_draws_nothing_visible()
    {
        var pdf = PagePdf("BT /F1 12 Tf 72 700 Td (PAGE_TEXT) Tj ET\n/Fm1 Do", "/XObject << /Fm1 20 0 R >>",
            Form(20, "BT /F1 12 Tf 72 650 Td (LAYERED_FORM) Tj ET", "0 0 612 792", " /OC 31 0 R") + Layers("/OFF [31 0 R]"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["PAGE_TEXT"], Visible(result));
        Assert.Equal([("LAYERED_FORM", PdfHiddenTextReason.HiddenLayer)], Hidden(result));
    }

    [Fact]
    public void A_membership_dictionary_is_evaluated_by_its_policy()
    {
        var pdf = PagePdf("/OC /Any BDC BT /F1 12 Tf 72 700 Td (ANY_ON) Tj ET EMC\n/OC /All BDC BT /F1 12 Tf 72 650 Td (ALL_ON) Tj ET EMC",
            "/Properties << /Any 32 0 R /All 33 0 R >>",
            Layers("/OFF [31 0 R]") +
            Object(32, "<< /Type /OCMD /OCGs [30 0 R 31 0 R] /P /AnyOn >>") +
            Object(33, "<< /Type /OCMD /OCGs [30 0 R 31 0 R] /P /AllOn >>"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["ANY_ON"], Visible(result));
        Assert.Equal(["ALL_ON"], Hidden(result).Select(hidden => hidden.Text));
    }

    [Fact]
    public void A_layer_whose_visibility_cannot_be_evaluated_is_kept_and_flagged()
    {
        var pdf = PagePdf("/OC /Expr BDC BT /F1 12 Tf 72 700 Td (MAYBE_SHOWN) Tj ET EMC\nBT /F1 12 Tf 72 650 Td (PLAIN) Tj ET",
            "/Properties << /Expr 32 0 R >>",
            Layers("/OFF [31 0 R]") + Object(32, "<< /Type /OCMD /VE [/Not 31 0 R] >>"));

        var result = PdfTextExtractor.Extract(pdf);
        var page = Assert.Single(result.Pages);

        Assert.Equal(["MAYBE_SHOWN", "PLAIN"], Visible(result));
        var doubt = Assert.Single(page.UncertainText!);
        Assert.Equal((PdfUncertainTextReason.LayerVisibilityUnknown, 1), (doubt.Reason, doubt.Fragments));
        Assert.StartsWith("PdfLayerVisibilityUnknown: PDF page 1: 1 text fragment(s) belong to optional content",
            Assert.Single(result.Diagnostics!), StringComparison.Ordinal);
    }

    [Fact]
    public void Layer_marks_are_ignored_when_the_document_hides_no_layer()
    {
        var pdf = PagePdf("/OC /L2 BDC BT /F1 12 Tf 72 700 Td (MARKED) Tj ET EMC", "/Properties << /L2 31 0 R >>", Layers("/ON [30 0 R 31 0 R]"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(["MARKED"], Visible(result));
        Assert.Null(result.Pages[0].UncertainText);
        Assert.Empty(result.Diagnostics!);
    }

    [Fact]
    public void An_unbalanced_marked_content_end_inside_a_form_cannot_end_the_pages_hidden_layer()
    {
        var pdf = PagePdf("/OC /L2 BDC /Fm1 Do BT /F1 12 Tf 72 650 Td (STILL_HIDDEN) Tj ET EMC",
            "/Properties << /L2 31 0 R >> /XObject << /Fm1 20 0 R >>",
            Form(20, "EMC EMC", "0 0 612 792") + Layers("/OFF [31 0 R]"));

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Empty(Visible(result));
        Assert.Equal(["STILL_HIDDEN"], Hidden(result).Select(hidden => hidden.Text));
    }
}
