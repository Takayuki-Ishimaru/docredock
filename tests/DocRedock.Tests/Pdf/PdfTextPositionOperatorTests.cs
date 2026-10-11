using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.MeasuredPdf;

namespace DocRedock.Tests.Pdf;

/// <summary>
/// The operators that position text work as the PDF specification defines them. A move (Td, TD, T*,
/// ' and ") starts from the line matrix - where the line began - and not from where the text before it
/// ended; Tm sets the line and the position both; a show only advances the position. Chrome writes
/// one show and one Td for every glyph, pdfTeX starts each line of a paragraph with a Td, and ReportLab
/// with a T*. The quote operators move to the next line and show a string: that text is as much text
/// as any other. In a literal string an octal escape above 255 keeps its low eight bits, and a
/// backslash before the end of a line continues the string without showing anything.
/// </summary>
public sealed class PdfTextPositionOperatorTests
{
    private static IReadOnlyList<PdfTextRegion> Lines(string content, string resources = Resources, IReadOnlyDictionary<int, string>? objects = null) =>
        PdfTextExtractor.Extract(Page(content, resources, objects)).Pages[0].Regions;

    /// <summary>A line as its text and where it starts on the page.</summary>
    private static string Placed(PdfTextRegion line) => $"{line.Text}@{N(line.BoundingBox.X)},{N(line.BoundingBox.Y)}";

    [Fact]
    public void A_Td_moves_from_the_start_of_the_line_so_a_paragraph_in_one_text_object_keeps_its_left_edge()
    {
        // As pdfTeX writes a paragraph: one text object, and every line after the first starts with a Td.
        // Moved from where the text before it ended, each line would start further right.
        var lines = Lines("BT /F1 10 Tf 72 700 Td (First line) Tj 0 -12 Td (Second line) Tj 0 -12 Td (Third line) Tj 20 -12 Td (Fourth) Tj ET");
        Assert.Equal(["First line@72,700", "Second line@72,688", "Third line@72,676", "Fourth@92,664"], lines.Select(Placed));
    }

    [Theory]
    // "A", "B" and "C" are 20.01, 20.01 and 21.66 wide at 30, and each Td is the width of the glyph before
    // it. Moved from the end of the text, "B" would start a glyph's width late and the line read "A B C".
    [InlineData(Resources, 30, "20.01", 61.68)]
    // No widths: each glyph is estimated at 6 units, and each Td is 10.
    [InlineData("<< >>", 10, "10", 26)]
    public void Text_placed_glyph_by_glyph_keeps_its_order_and_spacing(string resources, int size, string step, double width)
    {
        var line = Assert.Single(Lines($"BT /F1 {size} Tf 72 700 Td (A) Tj {step} 0 Td (B) Tj {step} 0 Td (C) Tj ET", resources));
        Assert.Equal("ABC", line.Text);
        Assert.Equal(72, line.BoundingBox.X, 6);
        Assert.Equal(width, line.BoundingBox.Width, 6);
    }

    [Fact]
    public void Chrome_writes_every_glyph_as_its_own_show_with_a_Td_under_two_flips()
    {
        // The CTM turns the page upside down and the text matrix turns the text back; a Td is in text
        // space, so a negative one moves down the page. "ABC" is one line, "D" starts the next below it.
        var a = MeasuredPdf.Width("A", 30);
        var lines = Lines($"1 0 0 -1 0 842 cm BT /F1 30 Tf 1 0 0 -1 72 100 Tm (A) Tj {N(a)} 0 Td (B) Tj {N(a)} 0 Td (C) Tj " +
            $"{N(-2 * a)} -36 Td (D) Tj ET");
        Assert.Equal(["ABC@72,742", "D@72,706"], lines.Select(Placed));
        Assert.Equal(MeasuredPdf.Width("ABC", 30), lines[0].BoundingBox.Width, 6);
    }

    [Fact]
    public void A_Td_is_moved_by_the_line_matrix_as_Quartz_and_Cairo_scale_text()
    {
        // The size is in the text matrix and Tf is 1: a Td of (0, -1.2) is 14.4 units down the page, and
        // 0.667 across is the width of "A" (8.004). The last Td goes back up to the first line, behind "A".
        var lines = Lines("BT /F1 1 Tf 12 0 0 12 72 700 Tm (A) Tj 0 -1.2 Td (B) Tj 0.667 1.2 Td (C) Tj ET");
        Assert.Equal(["AC@72,700", "B@72,685.6"], lines.Select(Placed));
        Assert.Equal(MeasuredPdf.Width("AC", 12), lines[0].BoundingBox.Width, 6);
    }

    [Fact]
    public void Tm_and_Td_apply_in_the_order_they_are_written()
    {
        // The Td moves the line Tm has set, whatever the text object has done before.
        Assert.Equal(["A@100,588"], Lines("BT /F1 10 Tf 72 700 Td 1 0 0 1 100 600 Tm 0 -12 Td (A) Tj ET").Select(Placed));
        Assert.Equal(["A@100,600"], Lines("BT /F1 10 Tf 0 -12 Td 1 0 0 1 100 600 Tm (A) Tj ET").Select(Placed));
    }

    [Fact]
    public void A_new_text_object_starts_at_the_origin_again()
    {
        Assert.Equal(["A@72,700", "B@72,600"], Lines("BT /F1 10 Tf 72 700 Td (A) Tj ET BT /F1 10 Tf 72 600 Td (B) Tj ET").Select(Placed));
    }

    [Fact]
    public void T_star_and_TD_move_down_by_the_leading()
    {
        // T* is a Td of (0, -TL); TD is a Td that also sets TL to the distance moved down.
        var lines = Lines("BT /F1 10 Tf 14 TL 72 700 Td (One) Tj T* (Two) Tj 0 -20 TD (Three) Tj T* (Four) Tj ET");
        Assert.Equal(["One@72,700", "Two@72,686", "Three@72,666", "Four@72,646"], lines.Select(Placed));
    }

    [Fact]
    public void ReportLab_starts_the_second_line_of_a_paragraph_with_T_star_and_a_Td()
    {
        // As in a table cell of tests/DocRedock.Tests/Fixtures/Pdf/complex-layout.pdf: a hanging first
        // line (-7), then the next line one leading down, indented again (7). It used to continue on the
        // first line, behind its text.
        var lines = Lines("q 1 0 0 1 100 600 cm BT 1 0 0 1 9 14.2 Tm -7 0 Td /F1 8 Tf 11 TL (First line of it) Tj T* 7 0 Td (second line) Tj ET Q");
        Assert.Equal(["First line of it@102,614.2", "second line@109,603.2"], lines.Select(Placed));
    }

    [Fact]
    public void Operands_that_are_not_finite_numbers_move_nothing()
    {
        // A position that is not a number would make every comparison false and merge the page into one line.
        var lines = Lines("BT /F1 10 Tf 72 700 Td (Before) Tj NaN 0 Td (After) Tj 1e999 TL T* (Last) Tj ET BT /F1 10 Tf 72 600 Td (Below) Tj ET");
        Assert.Equal(["BeforeAfterLast@72,700", "Below@72,600"], lines.Select(Placed));
    }

    [Fact]
    public void The_leading_belongs_to_the_graphics_state_that_q_and_Q_save()
    {
        // The 30 set inside q ... Q is gone after Q, and the 12 before it is back.
        var lines = Lines("12 TL q 30 TL BT /F1 10 Tf 72 700 Td (One) Tj T* (Two) Tj ET Q BT /F1 10 Tf 72 600 Td (Three) Tj T* (Four) Tj ET");
        Assert.Equal(["One@72,700", "Two@72,670", "Three@72,600", "Four@72,588"], lines.Select(Placed));
    }

    [Fact]
    public void The_quote_operator_moves_to_the_next_line_and_shows_its_string()
    {
        // `(Line two) '` is T* and then Tj. It used to vanish from the output without a word.
        var lines = Lines("BT /F1 10 Tf 14 TL 72 700 Td (Line one) Tj (Line two) ' (Line three) ' ET");
        Assert.Equal(["Line one@72,700", "Line two@72,686", "Line three@72,672"], lines.Select(Placed));
        // Measured with the font's widths, like a string shown with Tj.
        Assert.Equal(MeasuredPdf.Width("Line two", 10), lines[1].BoundingBox.Width, 6);
    }

    [Fact]
    public void The_double_quote_operator_sets_word_and_character_spacing_and_then_acts_as_the_quote_operator()
    {
        // `3 1 (a b) "` is 3 Tw 1 Tc and then the quote operator; the spacing stays for the shows after it.
        var lines = Lines("BT /F1 10 Tf 14 TL 72 700 Td (Line one) Tj 3 1 (a b) \" (c d) Tj ET");
        Assert.Equal(["Line one@72,700", "a bc d@72,686"], lines.Select(Placed));
        Assert.Equal(MeasuredPdf.Width("a bc d", 10) + 6 * 1 + 2 * 3, lines[1].BoundingBox.Width, 6);
    }

    [Fact]
    public void Text_a_quote_operator_draws_is_hidden_when_it_lies_outside_the_page_like_any_other()
    {
        // The page is 842 high; the second line is 900 below the first.
        var result = PdfTextExtractor.Extract(Page("BT /F1 10 Tf 900 TL 72 100 Td (Seen) Tj (Beyond the page) ' ET"));
        Assert.Equal(["Seen"], result.Pages[0].Regions.Select(region => region.Text));
        var hidden = Assert.Single(result.Pages[0].HiddenRegions!);
        Assert.Equal(("Beyond the page", PdfHiddenTextReason.OutsideVisibleArea), (hidden.Region.Text, hidden.Reason));
    }

    [Fact]
    public void Text_a_quote_operator_draws_is_struck_through_where_a_line_crosses_it()
    {
        var left = 72 + MeasuredPdf.Width("Old price ", 12);
        var result = PdfTextExtractor.Extract(Page(string.Join("\n",
            "BT /F1 12 Tf 24 TL 72 744 Td (Heading) Tj (Old price 1000 yen) ' ET",
            $"{N(left)} 723.6 {N(MeasuredPdf.Width("1000 yen", 12))} 0.8 re f")));
        var line = result.Pages[0].Regions.Single(region => region.Text == "Old price 1000 yen");
        Assert.Equal([new TextRange(10, 18)], line.Struck);
        Assert.DoesNotContain(result.Diagnostics ?? [], diagnostic => diagnostic.StartsWith("PdfStrikethroughUnplaced", StringComparison.Ordinal));
    }

    [Fact]
    public void Table_cells_drawn_with_quote_operators_are_in_the_table()
    {
        var grid = "0.8 w 72 610 390 90 re S\n72 670 m 462 670 l S\n72 640 m 462 640 l S\n202 610 m 202 700 l S\n332 610 m 332 700 l S";
        var columns = string.Join("\n",
            "BT /F1 11 Tf 30 TL 78 680 Td (Name) Tj (Apple) ' (Grape) ' ET",
            "BT /F1 11 Tf 30 TL 208 680 Td (Qty) Tj (10) ' (30) ' ET",
            "BT /F1 11 Tf 30 TL 338 680 Td (Note) Tj (ok) ' (cancelled) ' ET");
        var result = PdfTextExtractor.Extract(Page(grid + "\n" + columns));
        var table = Assert.Single(result.Tables![1]);
        Assert.Equal(["Name", "Qty", "Note", "Apple", "10", "ok", "Grape", "30", "cancelled"],
            table.Rows.SelectMany(row => row.Cells).Select(cell => cell.Text));
    }

    [Fact]
    public void A_quote_after_bytes_that_only_look_like_a_string_in_an_inline_image_shows_nothing()
    {
        // The data of an inline image is binary: `(secret)'` in it is not text.
        var lines = Lines("BT /F1 10 Tf 72 700 Td (Real) Tj ET\nq 8 0 0 8 72 600 cm BI /W 8 /H 1 /BPC 8 /CS /G ID (secret)'x EI Q");
        Assert.Equal(["Real@72,700"], lines.Select(Placed));
    }

    [Theory]
    // \777 is 511, which has the low eight bits 255; \501 is 321, which is 65; \541 is 353, which is 97.
    [InlineData("A\\777B", "AÿB")]
    [InlineData("\\501\\502C", "ABC")]
    [InlineData("x\\5419", "xa9")]
    public void An_octal_escape_above_255_keeps_its_low_eight_bits_in_a_font_with_and_without_widths(string literal, string expected)
    {
        // It used to throw an OverflowException and stop the extraction of the whole document.
        foreach (var resources in new[] { Resources, "<< >>" })
            Assert.Equal(expected, Assert.Single(Lines($"BT /F1 10 Tf 72 700 Td ({literal}) Tj ET", resources)).Text);
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void A_backslash_before_the_end_of_a_line_continues_the_string_and_shows_nothing(string lineEnd)
    {
        // Quartz wraps long strings so, in the middle of words: "o\<CR>f" is "of", not a break inside the word.
        foreach (var resources in new[] { Resources, "<< >>" })
            Assert.Equal("of the use", Assert.Single(Lines($"BT /F1 10 Tf 72 700 Td (o\\{lineEnd}f the \\{lineEnd}use) Tj ET", resources)).Text);
    }

    [Fact]
    public void An_octal_escape_above_255_in_an_actual_text_replacement_does_not_stop_the_extraction()
    {
        var lines = Lines("/Span << /ActualText (\\777\\501) >> BDC BT /F1 10 Tf 72 700 Td (xy) Tj ET EMC");
        Assert.Equal("ÿA", Assert.Single(lines).Text);
    }

    [Fact]
    public void Octal_escapes_of_one_two_and_three_digits_and_the_other_escapes_are_decoded_as_before()
    {
        // \101 and \102 are "AB", \043 is "#" (and the 9 after it a digit of its own), \7 a bell.
        var line = Assert.Single(Lines("BT /F1 10 Tf 72 700 Td (\\101\\102\\0439\\7\\\\\\(\\)) Tj ET"));
        Assert.Equal("AB#9\a\\()", line.Text);
    }
}
