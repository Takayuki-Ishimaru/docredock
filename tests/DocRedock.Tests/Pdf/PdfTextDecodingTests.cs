using System.Diagnostics;
using System.Text;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.MeasuredPdf;

namespace DocRedock.Tests.Pdf;

/// <summary>
/// Text that left the extractor changed, shortened or missing without a word. A show vanished when its
/// string held balanced parentheses ("(a(b)c)" is one legal string) or its <c>TJ</c> array a number
/// written as .5, 5. or -.5; a word space that pdfTeX writes as a <c>TJ</c> number of -333, and Ghostscript
/// as character spacing, was lost ("Helloworld", "Likeother"); a simple font's codes were read as Latin-1
/// whatever its /Encoding said (the Windows quotes as control characters, a Mac font's é and « as other
/// letters, a ligature or a Greek letter named in /Differences as its code); an /ActualText with a byte
/// order mark came out as "þÿ..."; and the codes a ToUnicode map lacks came out as bytes with no warning. Then the
/// rules for those went too far: a heading tracked out letter by letter read as "A N N U A L", every low code a
/// glyph-index font's map lacks was taken for a space (the digits of Arial are glyphs 19 to 28), a stray "&lt;" or
/// an unbalanced "(" lost the shows after it or set them on one line, and a page that sets its font again for every
/// line built the font's map again each time.
/// </summary>
public sealed class PdfTextDecodingTests
{
    /// <summary>A standard font that gives no widths, so that nothing about a show is measured.</summary>
    private const string Standard = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    /// <summary>Extracts a page drawing <paramref name="content"/> with <paramref name="font"/> as its /F1 (the
    /// measured Helvetica when none is given), beside the objects given.</summary>
    private static PdfExtractionResult Extract(string content, string font = "", IReadOnlyDictionary<int, string>? objects = null)
    {
        var all = new Dictionary<int, string> { [4] = font.Length == 0 ? SimpleFont : font };
        foreach (var (id, body) in objects ?? new Dictionary<int, string>()) all[id] = body;
        return PdfTextExtractor.Extract(Page(content, Resources, all));
    }

    private static string[] Lines(PdfExtractionResult result) =>
        result.Pages.SelectMany(page => page.Regions.OrderBy(region => region.ReadingOrder)).Select(region => region.Text).ToArray();

    private static string[] Lines(string content, string font = "", IReadOnlyDictionary<int, string>? objects = null) =>
        Lines(Extract(content, font, objects));

    private static string OneLine(string content, string font = "", IReadOnlyDictionary<int, string>? objects = null) =>
        Assert.Single(Lines(content, font, objects));

    private static PdfTextRegion Region(string content, string font = "", IReadOnlyDictionary<int, string>? objects = null) =>
        Assert.Single(Extract(content, font, objects).Pages[0].Regions);

    private static string Show(string operands) => $"BT /F1 12 Tf 72 700 Td {operands} ET";

    /// <summary>Compares lines exactly. xUnit compares a collection expression with an array so that a NUL or a soft
    /// hyphen does not count, which would hide the very characters some of these tests are about; control characters are
    /// written as \uXXXX, so that a failure shows them.</summary>
    private static void AssertLines(string[] expected, string[] actual) => Assert.Equal(Escaped(expected), Escaped(actual));

    private static string Escaped(IEnumerable<string> lines) =>
        string.Join("\n", lines.Select(line => string.Concat(line.Select(character =>
            char.IsControl(character) || character is '\u00AD' or '\uFFFD' ? $"\\u{(int)character:X4}" : character.ToString()))));

    // ---- a literal string holds balanced parentheses; a TJ array any form of a number ----------------------------------

    [Theory]
    [InlineData("(a(b)c) Tj", "a(b)c")]
    [InlineData("(a(b(c)d)e) Tj", "a(b(c)d)e")]
    [InlineData("(a(b(c(d)e)f)g) Tj", "a(b(c(d)e)f)g")]
    [InlineData("((x)) Tj", "(x)")]
    [InlineData("(a\\(b(c)d\\)e) Tj", "a(b(c)d)e")]
    [InlineData("(a\\\\) Tj", "a\\")]
    [InlineData("(a(b)c) '", "a(b)c")]
    [InlineData("1 2 (a(b)c) \"", "a(b)c")]
    [InlineData("[(a(b)c) -20 (d(e(f)))] TJ", "a(b)cd(e(f))")]
    [InlineData("[(x) -20 ((y))] TJ", "x(y)")]
    [InlineData("[<4142> -20 (c(d)e)] TJ", "ABc(d)e")]
    public void A_literal_string_holds_balanced_parentheses_to_any_depth(string show, string expected)
    {
        Assert.Equal(expected, OneLine(Show("14 TL " + show)));
        Assert.Equal(expected, OneLine(Show("14 TL " + show), Standard));
    }

    [Fact]
    public void A_string_with_nested_parentheses_does_not_hide_the_shows_after_it()
    {
        AssertLines(["x(y)z", "w", "last"],
            Lines("BT /F1 12 Tf 72 700 Td (x(y)z) Tj 0 -20 Td (w) Tj ET\nBT /F1 12 Tf 72 640 Td (last) Tj ET"));
    }

    [Fact]
    public void The_text_parser_and_the_ordered_pass_start_and_end_a_nested_string_at_the_same_place()
    {
        // The ordered pass keys what it knows of a show (its font size, its word spacing, its line) by the offset its
        // string starts at. Had the parser ended "(a b(c d)e f)" at its first ")", it would have asked for the state of
        // a show at another offset, found none and fallen back to the estimate of 6 units a character.
        const string text = "a b(c d)e f";
        var region = Region($"BT /F1 10 Tf 5 Tw 72 700 Td ({text}) Tj ET");
        Assert.Equal(text, region.Text);
        Assert.Equal(Width(text, 10) + 3 * 5, region.BoundingBox.Width, 6);
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("5.", 5)]
    [InlineData(".5", 0.5)]
    [InlineData("-.5", -0.5)]
    [InlineData("+.5", 0.5)]
    [InlineData("+5", 5)]
    [InlineData("-5.", -5)]
    [InlineData("-0.5", -0.5)]
    [InlineData("100.", 100)]
    [InlineData("+100.25", 100.25)]
    [InlineData("0.", 0)]
    [InlineData(".0", 0)]
    public void Every_written_form_of_a_number_is_read_in_a_TJ_array(string number, double value)
    {
        // A number moves the next glyph back by that many thousandths of the font size: the width of the show says it was read.
        var region = Region($"BT /F1 10 Tf 72 700 Td [(A) {number} (B)] TJ ET");
        Assert.Equal("AB", region.Text);
        Assert.Equal(Width("AB", 10) - value / 1000 * 10, region.BoundingBox.Width, 6);
    }

    [Theory]
    [InlineData("-300.", "A B")]
    [InlineData("-300.0", "A B")]
    [InlineData("-.3", "AB")]
    [InlineData("-0.3", "AB")]
    public void A_number_written_with_a_point_is_the_number_it_spells(string number, string expected)
    {
        // -300. is 300 thousandths of an em; -.3 is not.
        Assert.Equal(expected, OneLine($"BT /F1 10 Tf 72 700 Td [(A) {number} (B)] TJ ET"));
    }

    [Fact]
    public void A_hexadecimal_string_may_hold_white_space_inside_an_array()
    {
        Assert.Equal("ABC", OneLine(Show("[<41 4\n2> -20 <43>] TJ")));
    }

    [Fact]
    public void A_string_whose_parentheses_never_balance_ends_at_its_first_closing_parenthesis()
    {
        // No viewer reads such a file as meant; the text after it is not lost with it.
        var text = string.Concat(Lines("BT /F1 12 Tf 72 700 Td (a(b) Tj ET\nBT /F1 12 Tf 72 680 Td (c) Tj ET", Standard));
        Assert.StartsWith("a(b", text, StringComparison.Ordinal);
        Assert.EndsWith("c", text, StringComparison.Ordinal);
    }

    [Theory]
    // The text after it is moved down the page with T* (and the leading TL set), which only the ordered pass follows: it has to
    // end the string where the text parser does, or the shows after it have no line and are set where the first one is.
    [InlineData("BT /F1 12 Tf 14 TL 72 700 Td (Heading) Tj\nT* (Price list (draft) Tj\nT* (First paragraph) Tj\nT* (Second paragraph) Tj ET")]
    [InlineData("BT /F1 12 Tf 14 TL 72 700 Td (Heading) Tj\nT* [(Price list (draft) -20 (s)] TJ\nT* (First paragraph) Tj\nT* (Second paragraph) Tj ET")]
    public void A_string_that_never_balances_leaves_the_shows_after_it_each_on_its_own_line(string content)
    {
        foreach (var font in new[] { "", Standard })
        {
            var lines = Lines(content, font);
            Assert.Equal(4, lines.Length);
            Assert.Equal("Heading", lines[0]);
            Assert.StartsWith("Price list (draft", lines[1], StringComparison.Ordinal);
            Assert.Equal(["First paragraph", "Second paragraph"], lines[2..]);
        }
    }

    [Fact]
    public void A_string_that_never_balances_leaves_the_shows_after_it_where_a_move_to_a_new_position_puts_them()
    {
        // As a producer writes it, a Td to every line: the operators between the strings are all that places them.
        var lines = Lines("BT /F1 12 Tf 72 700 Td (Heading line) Tj ET\nBT /F1 12 Tf 72 670 Td (Price list (draft) Tj ET\n" +
            "BT /F1 12 Tf 72 640 Td (First paragraph of the body.) Tj ET\nBT /F1 12 Tf 300 610 Td (Right side note) Tj ET\n" +
            "BT /F1 12 Tf 72 580 Td (Second paragraph of the body.) Tj ET");
        AssertLines(["Heading line", "Price list (draft", "First paragraph of the body.", "Right side note", "Second paragraph of the body."], lines);
    }

    [Theory]
    // A "<" that starts no hexadecimal string is one character like any other. The text parser used to go on to the next
    // ">" (or to the end of the content) for it, and the ordered pass too: every show after it was lost, with no warning.
    [InlineData("<3 Q")]
    [InlineData("< Q")]
    [InlineData("<zz>")]
    [InlineData("<41 4g>")]
    [InlineData("<3 Q /Pattern << /A 1 >> gs")]
    public void A_stray_less_than_sign_starts_no_string_and_hides_nothing_after_it(string stray)
    {
        // T* places each show by the line matrix the ordered pass follows, so that it has to carry on after the "<" too; and
        // a ">" further on (the dictionary of the marked content after the text) is what the "<" used to be taken to the end of.
        var content = $"BT /F1 12 Tf 14 TL 72 700 Td (First line) Tj\nq 0.5 g {stray} Q\nT* (Second line) Tj\nT* (Third line) Tj ET\n/P << /MCID 0 >> BDC EMC";
        foreach (var font in new[] { "", Standard })
            AssertLines(["First line", "Second line", "Third line"], Lines(content, font));
    }

    [Fact]
    public void Strings_that_never_balance_are_read_in_linear_time()
    {
        // Every "(()" opens more than it closes; looked for again at each one, the end of the stream would be
        // scanned a hundred and fifty thousand times.
        var content = "BT /F1 12 Tf 72 700 Td " + string.Concat(Enumerable.Repeat("(()", 150_000)) + " Tj ET";
        var stopwatch = Stopwatch.StartNew();
        _ = Extract(content, Standard);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"reading the strings took {stopwatch.Elapsed}");
    }

    [Fact]
    public void An_array_with_a_string_that_never_balances_is_split_as_the_text_parser_saw_it()
    {
        // The ordered pass and the parser both end the string at its first ")".
        Assert.Equal("a(bc", OneLine("BT /F1 12 Tf 72 700 Td [(a(b) -20 (c)] TJ ET", Standard));
    }

    [Fact]
    public void An_opening_parenthesis_that_nothing_closes_is_no_string_and_hides_nothing_after_it()
    {
        // Nothing can close it, so it is nothing: the hexadecimal show after it is read, as it always was.
        AssertLines(["A"], Lines("BT /F1 12 Tf 72 700 Td (broken ET\nBT /F1 12 Tf 72 680 Td <41> Tj ET", Standard));
    }

    [Theory]
    [InlineData("(")]
    [InlineData("[(")]
    [InlineData("[((")]
    public void Opening_parentheses_and_arrays_that_nothing_closes_are_read_in_linear_time(string repeated)
    {
        // Looked into again at each of them, the rest of the content would be scanned a hundred thousand times.
        var content = "BT /F1 12 Tf 72 700 Td " + string.Concat(Enumerable.Repeat(repeated, 100_000)) + " TJ ET";
        var stopwatch = Stopwatch.StartNew();
        _ = Extract(content, Standard);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"reading {repeated} a hundred thousand times took {stopwatch.Elapsed}");
    }

    [Fact]
    public void A_font_set_again_for_every_line_builds_its_map_once()
    {
        // Each Tf names the font again. The map a simple font decodes with - a copy of its ToUnicode map and the codes its
        // /Encoding adds - was built for every one of them, whether its font had a token already or not.
        const int lines = 3_000;
        var font = "<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAB+Subset /Encoding /WinAnsiEncoding /ToUnicode 5 0 R >>";
        var map = Stream("1 begincodespacerange <00> <FF> endcodespacerange\n2 beginbfchar <41> <0058> <42> <0059> endbfchar");
        var content = new StringBuilder("BT\n");
        for (var line = 0; line < lines; line++)
            content.Append($"/F1 12 Tf 1 0 0 1 {20 + line / 60 % 10 * 55} {780 - line % 60 * 12} Tm (AB) Tj\n");
        content.Append("ET");
        var pdf = Page(content.ToString(), Resources, new Dictionary<int, string> { [4] = font, [5] = map });

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = PdfTextExtractor.Extract(pdf);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains(result.Pages[0].Regions, region => region.Text.Contains("XY", StringComparison.Ordinal));
        // A map takes some tens of kilobytes: built again for each of the 3,000 Tf, that is over a hundred megabytes (about 110
        // measured), where the extraction itself takes some 15. The bytes allocated, unlike the time taken, do not depend on how
        // busy the machine is.
        Assert.True(allocated < 45_000_000, $"extracting {lines} shows, each after a Tf, allocated {allocated:N0} bytes");
    }

    [Fact]
    public void Actual_texts_that_cannot_be_read_are_looked_past_in_linear_time()
    {
        // Each of sixty thousand "/ActualText" has a string whose parentheses do not balance; scanning on from every one of
        // them to the end of the content would not end. The show at the end is read as it is written.
        var content = "BT /F1 12 Tf 72 700 Td " + string.Concat(Enumerable.Repeat("/ActualText (a(b) ", 60_000)) + " (x) Tj ET";
        var stopwatch = Stopwatch.StartNew();
        var result = Extract(content, Standard);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"reading the actual texts took {stopwatch.Elapsed}");
        AssertLines(["x"], Lines(result));
    }

    [Fact]
    public void A_parenthesis_in_a_comment_or_in_inline_image_data_starts_no_string()
    {
        AssertLines(["kept"], Lines("% a (comment\nBT /F1 12 Tf 72 700 Td (kept) Tj ET"));
        AssertLines(["kept"], Lines("q 1 0 0 1 0 0 cm BI /W 2 /H 2 /BPC 8 /CS /G ID ((\n\u0001 EI Q\nBT /F1 12 Tf 72 700 Td (kept) Tj ET", Standard));
    }

    // ---- a word space written as a move inside one show ---------------------------------------------------------------

    [Theory]
    // pdfTeX and dvipdfm: a large negative TJ number.
    [InlineData("[(Hello)-333(world)] TJ", "Hello world")]
    [InlineData("[(Hel)20(lo)-300(wor)15(ld)] TJ", "Hello world")]
    [InlineData("[(This)-333(pap)-28(er)-333(describ)-28(es)-333(a)-333(metho)-28(d)] TJ", "This paper describes a method")]
    // The size of the move is what counts: a fifth of an em is a word space, a little less is kerning.
    [InlineData("[(a)-200(b)] TJ", "a b")]
    [InlineData("[(a)-199(b)] TJ", "ab")]
    [InlineData("[(Kern)-150(ing)] TJ", "Kerning")]
    // Moves that add up, and a move backwards.
    [InlineData("[(a)-100 -100(b)] TJ", "a b")]
    [InlineData("[(a)-400 150(b)] TJ", "a b")]
    [InlineData("[(a)-300 150(b)] TJ", "ab")]
    [InlineData("[(a)-300 400(b)] TJ", "ab")]
    [InlineData("[(a)300(b)] TJ", "ab")]
    // A space the text has is not doubled, and neither is one beside the move.
    [InlineData("[(two )-333(words)] TJ", "two words")]
    [InlineData("[(two)-333( words)] TJ", "two words")]
    [InlineData("[(two )-333( words)] TJ", "two  words")]
    // A move before the first glyph or after the last is not a gap inside the show.
    [InlineData("[-500(Hello)-500] TJ", "Hello")]
    [InlineData("[-500 (Hello)] TJ", "Hello")]
    public void A_move_inside_a_show_that_is_a_word_space_writes_one(string show, string expected)
    {
        Assert.Equal(expected, OneLine(Show(show)));
        // The move says it, not the glyph widths: a font that gives none reads the same.
        Assert.Equal(expected, OneLine(Show(show), Standard));
    }

    [Theory]
    // Ghostscript puts a word gap into the character spacing of the glyph before it: "Lik" "eo" "ther".
    [InlineData("10 Tf 72 700 Td 2.515 Tc (eo) Tj", "e o")]
    [InlineData("10 Tf 72 700 Td 2 Tc (eo) Tj", "e o")]
    [InlineData("10 Tf 72 700 Td 1.9 Tc (eo) Tj", "eo")]
    [InlineData("10 Tf 72 700 Td -0.15 Tc (ve) Tj", "ve")]
    // The em is the font size: 2 is a fifth of 10 but a sixth of 12.
    [InlineData("12 Tf 72 700 Td 2 Tc (eo) Tj", "eo")]
    // Character spacing and a move add up.
    [InlineData("10 Tf 72 700 Td 1 Tc [(a)-100(b)] TJ", "a b")]
    // Word spacing is for the spaces a text has; it adds none.
    [InlineData("10 Tf 72 700 Td 5 Tw (ab) Tj", "ab")]
    [InlineData("10 Tf 72 700 Td 5 Tw (a b) Tj", "a b")]
    public void Character_spacing_inside_a_show_is_a_word_space_when_it_is_a_fifth_of_an_em(string show, string expected)
    {
        Assert.Equal(expected, OneLine($"BT /F1 {show} ET"));
        Assert.Equal(expected, OneLine($"BT /F1 {show} ET", Standard));
    }

    [Fact]
    public void The_ghostscript_line_reads_with_its_word_space()
    {
        // From the GNU bash article: "Lik" and "ther GNU softw" are shows of their own, the gap between "e" and
        // "o" is the character spacing of "(eo)". It read "Likeother GNU softw".
        const string content = "BT /F1 10 Tf 72 700 Td (Lik) Tj 13.79 0 Td 2.515 Tc (eo) Tj 11.955 0 Td 0 Tc (ther GNU softw) Tj ET";
        Assert.Equal("Like other GNU softw", OneLine(content));
    }

    [Theory]
    // A heading tracked out by a quarter of an em (3 at 12) moves every glyph on alike: that is letter spacing, not a word
    // space after each letter. It read "A N N U A L R E P O R T"; the space between the words is a glyph, or a move beyond it.
    [InlineData("3 Tc (ANNUAL REPORT) Tj", "ANNUAL REPORT")]
    [InlineData("3 Tc [(ANNUAL)-278(REPORT)] TJ", "ANNUAL REPORT")]
    [InlineData("[(A)-250(N)-250(N)-250(U)-250(A)-250(L)-250( )-250(R)-250(E)-250(P)-250(O)-250(R)-250(T)] TJ", "ANNUAL REPORT")]
    [InlineData("[(A)-250(N)-250(N)-250(U)-250(A)-250(L)-528(R)-250(E)-250(P)-250(O)-250(R)-250(T)] TJ", "ANNUAL REPORT")]
    [InlineData("3 Tc [(S)(P)(A)(C)(E)(D)-278(T)(I)(T)(L)(E)] TJ", "SPACED TITLE")]
    // Spread wider, or tighter; it is the same whatever the amount, so long as all the glyphs have it.
    [InlineData("6 Tc (HEADING) Tj", "HEADING")]
    [InlineData("-0.5 Tc (CONDENSED) Tj", "CONDENSED")]
    [InlineData("5 Tc [(A)-500(B)-500(C)-500(D)-500(E)] TJ", "ABCDE")]
    // Words that are only a few letters long do not make the median a word space.
    [InlineData("[(I)-333(am)-333(on)-333(it)] TJ", "I am on it")]
    public void Letter_spacing_is_no_word_space_but_a_move_beyond_it_still_is(string show, string expected)
    {
        Assert.Equal(expected, OneLine($"BT /F1 12 Tf 72 700 Td {show} ET"));
        Assert.Equal(expected, OneLine($"BT /F1 12 Tf 72 700 Td {show} ET", Standard));
    }

    [Fact]
    public void A_strikethrough_over_tracked_text_that_gains_a_space_strikes_the_characters_under_it()
    {
        // "ANNUAL" and "REPORT" tracked by a quarter of an em (2.75 at 11) with a move of the space between them; a line over
        // "REPORT" strikes its six letters, whatever the spaces the show gained are put among them.
        var annual = Width("ANNUAL", 11) + 6 * 2.75 + 0.278 * 11;
        var region = Region(Struck("2.75 Tc [(ANNUAL)-278(REPORT)] TJ", (72 + annual - 0.5, 72 + annual + Width("REPORT", 11) + 6 * 2.75)));
        Assert.Equal("ANNUAL REPORT", region.Text);
        Assert.Equal([new TextRange(7, 13)], region.Struck);
    }

    [Fact]
    public void A_vertical_font_has_no_word_spaces_across_its_moves()
    {
        // The moves of a vertical font are down the page.
        var font = "<< /Type /Font /Subtype /Type0 /BaseFont /Mincho /Encoding /Identity-V /DescendantFonts [5 0 R] /ToUnicode 6 0 R >>";
        var objects = new Dictionary<int, string>
        {
            [5] = "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Mincho /DW 1000 >>",
            [6] = TwoByteCMap((1, 'a'), (2, 'b')),
        };
        Assert.Equal("ab", OneLine("BT /F1 10 Tf 72 700 Td [<0001>-500<0002>] TJ ET", font, objects));
    }

    [Theory]
    // A line of Japanese or Chinese is spread over its width with large moves, and has no word spaces.
    [InlineData("[<0001>-1000<0002>-1000<0003>] TJ", "あいう")]
    [InlineData("[<0001>-200<0002>-5000<0003>] TJ", "あいう")]
    [InlineData("[<0001>-1000<0002>-1000<0003>-1000<0001>-1000<0002>] TJ", "あいうあい")]
    [InlineData("12 Tc [<0001><0002><0003><0001><0002>] TJ", "あいうあい")]
    // Beside a character of another script a move needs half an em.
    [InlineData("[<0001>-300<0061>] TJ", "あa")]
    [InlineData("[<0001>-500<0061>] TJ", "あ a")]
    [InlineData("[<0061>-499<0001>] TJ", "aあ")]
    [InlineData("[<0061>-600<0001>-300<0062>-600<0001>] TJ", "a あb あ")]
    // Two Latin letters need a fifth, here as anywhere.
    [InlineData("[<0061>-200<0062>] TJ", "a b")]
    public void A_large_move_between_east_asian_characters_is_no_word_space(string show, string expected)
    {
        var objects = new Dictionary<int, string>
        {
            [5] = "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Gothic /DW 1000 >>",
            [6] = TwoByteCMap((1, 'あ'), (2, 'い'), (3, 'う'), (0x61, 'a'), (0x62, 'b')),
        };
        var font = "<< /Type /Font /Subtype /Type0 /BaseFont /Gothic /Encoding /Identity-H /DescendantFonts [5 0 R] /ToUnicode 6 0 R >>";
        Assert.Equal(expected, OneLine($"BT /F1 10 Tf 72 700 Td {show} ET", font, objects));
    }

    [Fact]
    public void A_ligature_is_one_glyph_and_the_move_after_it_is_read_against_its_last_letter()
    {
        // One code that decodes to "ff" has no gap inside it.
        var font = "<< /Type /Font /Subtype /Type1 /BaseFont /Measured /FirstChar 1 /LastChar 2 /Widths [1000 300] /ToUnicode 5 0 R >>";
        var objects = new Dictionary<int, string> { [5] = Stream("2 beginbfchar <01> <00660066> <02> <0069> endbfchar") };
        Assert.Equal("ffi", OneLine("BT /F1 10 Tf 72 700 Td <0102> Tj ET", font, objects));
        Assert.Equal("ff i", OneLine("BT /F1 10 Tf 72 700 Td [<01>-250<02>] TJ ET", font, objects));
    }

    // ---- the glyphs of a show that gains a space, and what a strikethrough over it strikes ---------------------------------

    private static string Struck(string text, params (double From, double To)[] strikes) =>
        string.Join("\n", $"BT /F1 11 Tf 1 0 0 1 72 700 Tm {text} ET",
            string.Concat(strikes.Select(strike => $"q 0.7 w {N(strike.From)} 703 m {N(strike.To)} 703 l S Q\n")));

    [Fact]
    public void A_strikethrough_over_text_that_gains_a_space_strikes_the_characters_under_it()
    {
        // "[(Hello)-333(world)]" is drawn as "Hello world" and read so: a line over "world" strikes "world" (the
        // space is not under it), a line over "Hello" strikes "Hello", a line over all of it all. Had the space not
        // been placed among the characters, the line would cross part of a show it cannot place, and be reported.
        var hello = Width("Hello", 11);
        var gap = 0.333 * 11;
        var world = Width("world", 11);
        const string text = "[(Hello)-333(world)] TJ";

        var overWorld = Extract(Struck(text, (72 + hello + gap, 72 + hello + gap + world)));
        var line = Assert.Single(overWorld.Pages[0].Regions);
        Assert.Equal("Hello world", line.Text);
        Assert.Equal([new TextRange(6, 11)], line.Struck);
        Assert.DoesNotContain(overWorld.Diagnostics!, diagnostic => diagnostic.StartsWith("PdfStrikethroughUnplaced", StringComparison.Ordinal));

        var overHello = Region(Struck(text, (72, 72 + hello)));
        Assert.Equal([new TextRange(0, 5)], overHello.Struck);

        var overAll = Extract(Struck(text, (72, 72 + hello + gap + world)));
        Assert.Equal([new TextRange(0, 11)], Assert.Single(overAll.Pages[0].Regions).Struck);
        Assert.DoesNotContain(overAll.Diagnostics!, diagnostic => diagnostic.StartsWith("PdfStrikethroughUnplaced", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_space_a_show_gains_takes_its_place_among_the_characters_a_strikethrough_is_matched_against()
    {
        // Two spaces are put into "Hello brave world"; the line over "brave" in the middle must land on its five letters.
        var hello = Width("Hello", 11);
        var gap = 0.333 * 11;
        var brave = Width("brave", 11);
        var region = Region(Struck("[(Hello)-333(brave)-333(world)] TJ", (72 + hello + gap, 72 + hello + gap + brave)));
        Assert.Equal("Hello brave world", region.Text);
        Assert.Equal([new TextRange(6, 11)], region.Struck);
    }

    [Fact]
    public void A_strikethrough_over_character_spacing_that_makes_a_space_strikes_the_right_letters()
    {
        // "(eo)" with a character spacing that is a word gap reads "e o"; the line starts at the "o".
        var e = Width("e", 11);
        const int gap = 3;
        var region = Region(Struck($"{gap} Tc (eo) Tj", (72 + e + gap, 72 + e + gap + Width("o", 11))));
        Assert.Equal("e o", region.Text);
        Assert.Equal([new TextRange(2, 3)], region.Struck);
    }

    // ---- a simple font's codes read through its /Encoding ---------------------------------------------------------------

    private static string EncodedFont(string encoding, string subtype = "Type1") =>
        $"<< /Type /Font /Subtype /{subtype} /BaseFont /Helvetica /Encoding {encoding} >>";

    /// <summary>The text one string of all the codes from <paramref name="first"/> up reads as in a font.</summary>
    private static string AllCodes(string font, int first = 0x21)
    {
        var codes = string.Concat(Enumerable.Range(first, 0x100 - first).Select(code => code.ToString("X2")));
        return OneLine($"BT /F1 12 Tf 72 700 Td <{codes}> Tj ET", font);
    }

    private static Encoding CodePage(int number) => System.Text.CodePagesEncodingProvider.Instance.GetEncoding(number)!;

    [Fact]
    public void WinAnsiEncoding_reads_the_windows_punctuation_instead_of_control_characters()
    {
        const string shown = "(\\223quoted\\224 \\221single\\222 \\226 \\227 \\200 \\205 \\231 \\225 \\212 \\232 \\237 \\214 \\234 \\216 \\236 \\203 \\206 \\207 \\210 \\211 \\213 \\233)";
        Assert.Equal("“quoted” ‘single’ – — € … ™ • Š š Ÿ Œ œ Ž ž ƒ † ‡ ˆ ‰ ‹ ›", OneLine($"BT /F1 12 Tf 72 700 Td {shown} Tj ET", EncodedFont("/WinAnsiEncoding")));
    }

    [Fact]
    public void WinAnsiEncoding_reads_every_code_as_the_windows_1252_character_with_the_few_differences_of_the_specification()
    {
        // The specification's table is Windows-1252, except that its unused codes are bullets, 0xA0 is a space and 0xAD a hyphen (Annex D).
        var codePage = CodePage(1252);
        var expected = new StringBuilder();
        for (var code = 0x21; code < 0x100; code++)
            expected.Append(code switch
            {
                0x7F or 0x81 or 0x8D or 0x8F or 0x90 or 0x9D => "•",
                0xA0 => " ",
                0xAD => "-",
                _ => codePage.GetString([(byte)code]),
            });
        Assert.Equal(expected.ToString(), AllCodes(EncodedFont("/WinAnsiEncoding")));
        Assert.Equal(expected.ToString(), AllCodes(EncodedFont("/WinAnsiEncoding", "TrueType")));
    }

    [Fact]
    public void MacRomanEncoding_reads_every_code_as_mac_os_roman_with_ligatures_as_letters()
    {
        // As Quartz writes it: « » é and the euro sign at 0xDB; the fi and fl codes read as their letters, a no-break space
        // as a space, and 0xBD as the Greek capital omega (the code page of Windows has the ohm sign there).
        var codePage = CodePage(10000);
        var expected = new StringBuilder();
        for (var code = 0x21; code < 0x100; code++)
            expected.Append(code switch
            {
                0xBD => "\u03A9",
                0xCA => " ",
                0xDE => "fi",
                0xDF => "fl",
                _ => codePage.GetString([(byte)code]),
            });
        Assert.Equal(expected.ToString(), AllCodes(EncodedFont("/MacRomanEncoding", "TrueType")));
    }

    [Fact]
    public void A_quartz_font_reads_its_accents_guillemets_and_ligatures_and_is_not_questioned()
    {
        // Core Graphics writes a TrueType subset with /MacRomanEncoding and no ToUnicode map: "«café» fine – €".
        var font = "<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAB+Helvetica /FirstChar 32 /LastChar 255 /Encoding /MacRomanEncoding /FontDescriptor 5 0 R >>";
        var objects = new Dictionary<int, string> { [5] = "<< /Type /FontDescriptor /FontName /AAAAAB+Helvetica /Flags 32 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>" };
        var result = Extract("BT /F1 18 Tf 50 780 Td (\\307caf\\216\\310 \\336ne \\320 \\333) Tj ET", font, objects);
        AssertLines(["«café» fine – €"], Lines(result));
        Assert.Null(result.Pages[0].UncertainText);
        Assert.Empty(result.Diagnostics!);
    }

    [Fact]
    public void StandardEncoding_reads_the_quotes_ligatures_and_letters_of_the_adobe_standard_set()
    {
        var font = EncodedFont("<< /Type /Encoding /BaseEncoding /StandardEncoding >>");
        Assert.Equal("It’s ‘q’", OneLine("BT /F1 12 Tf 72 700 Td (It's `q') Tj ET", font));
        // The upper half of the standard set; the codes it leaves out are read as before.
        var known = new Dictionary<int, string>
        {
            [0xA1] = "¡", [0xA2] = "¢", [0xA3] = "£", [0xA4] = "⁄", [0xA5] = "¥", [0xA6] = "ƒ", [0xA7] = "§", [0xA8] = "¤", [0xA9] = "'",
            [0xAA] = "“", [0xAB] = "«", [0xAC] = "‹", [0xAD] = "›", [0xAE] = "fi", [0xAF] = "fl", [0xB1] = "–", [0xB2] = "†", [0xB3] = "‡",
            [0xB4] = "·", [0xB6] = "¶", [0xB7] = "•", [0xB8] = "‚", [0xB9] = "„", [0xBA] = "”", [0xBB] = "»", [0xBC] = "…", [0xBD] = "‰",
            [0xBF] = "¿", [0xC1] = "`", [0xC2] = "´", [0xC3] = "ˆ", [0xC4] = "˜", [0xC5] = "¯", [0xC6] = "˘", [0xC7] = "˙", [0xC8] = "¨",
            [0xCA] = "˚", [0xCB] = "¸", [0xCD] = "˝", [0xCE] = "˛", [0xCF] = "ˇ", [0xD0] = "—", [0xE1] = "Æ", [0xE3] = "ª", [0xE8] = "Ł",
            [0xE9] = "Ø", [0xEA] = "Œ", [0xEB] = "º", [0xF1] = "æ", [0xF5] = "ı", [0xF8] = "ł", [0xF9] = "ø", [0xFA] = "œ", [0xFB] = "ß",
        };
        foreach (var (code, text) in known)
            Assert.Equal(text, OneLine($"BT /F1 12 Tf 72 700 Td <{code:X2}> Tj ET", font));
    }

    [Fact]
    public void MacExpertEncoding_reads_small_capitals_figures_fractions_and_ligatures()
    {
        // The part of the expert set that is certain; a small capital stands for the lowercase letter it is set from.
        Assert.Equal("0123 abc ffi fl ½ ⅓", OneLine("BT /F1 12 Tf 72 700 Td <3031323320616263205920582048204E> Tj ET", EncodedFont("/MacExpertEncoding")));
    }

    [Fact]
    public void An_expert_code_the_table_does_not_know_is_not_taken_for_latin_1_and_asks_for_comparison()
    {
        var result = Extract("BT /F1 12 Tf 72 700 Td <8F> Tj ET", EncodedFont("/MacExpertEncoding"));
        AssertLines(["\u008F"], Lines(result));
        Assert.Equal(1, Assert.Single(result.Pages[0].UncertainText!).Fragments);
    }

    [Fact]
    public void A_font_without_an_encoding_keeps_reading_its_codes_as_latin_1()
    {
        // Nothing says otherwise: 0xE9 is é, 0x93 the control character it always was.
        Assert.Equal("é\u0093", OneLine("BT /F1 12 Tf 72 700 Td <E993> Tj ET", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
    }

    [Fact]
    public void An_encoding_dictionary_without_a_base_leaves_the_codes_it_does_not_name_as_they_were()
    {
        var font = EncodedFont("<< /Type /Encoding /Differences [97 /alpha /beta] >>");
        Assert.Equal("αβc é", OneLine("BT /F1 12 Tf 72 700 Td (abc \\351) Tj ET", font));
    }

    [Fact]
    public void Differences_put_their_glyph_names_over_the_base_encoding()
    {
        var font = EncodedFont("<< /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [65 /Euro /Lcommaaccent 130 /fi /fl] >>");
        Assert.Equal("€ĻC“ fifl", OneLine("BT /F1 12 Tf 72 700 Td (ABC\\223 \\202\\203) Tj ET", font));
    }

    [Theory]
    [InlineData("alpha", "α")]
    [InlineData("Omega", "\u03A9")]
    [InlineData("Delta", "\u0394")]
    [InlineData("germandbls", "ß")]
    [InlineData("eacute", "é")]
    [InlineData("Aogonek", "Ą")]
    [InlineData("ccaron", "č")]
    [InlineData("Lcommaaccent", "Ļ")]
    [InlineData("Scommaaccent", "Ș")]
    [InlineData("Euro", "€")]
    [InlineData("bullet", "•")]
    [InlineData("endash", "–")]
    [InlineData("quotedblleft", "“")]
    [InlineData("minus", "\u2212")]
    [InlineData("summation", "∑")]
    [InlineData("arrowright", "→")]
    [InlineData("mu", "\u00B5")]
    // Ligatures are their letters, not the presentation forms.
    [InlineData("fi", "fi")]
    [InlineData("fl", "fl")]
    [InlineData("ff", "ff")]
    [InlineData("ffi", "ffi")]
    [InlineData("ffl", "ffl")]
    [InlineData("f_f_i", "ffi")]
    [InlineData("T_h", "Th")]
    // uniXXXX (one or several code units) and uXXXX to uXXXXXX.
    [InlineData("uni00E9", "é")]
    [InlineData("uni00e9", "é")]
    [InlineData("uni00410042", "AB")]
    [InlineData("u00E9", "é")]
    [InlineData("u1D7D9", "\U0001D7D9")]
    [InlineData("u1F600", "\U0001F600")]
    // A suffix after the first period makes a variant of the glyph.
    [InlineData("a.sc", "a")]
    [InlineData("A.swash", "A")]
    [InlineData("uni00E9.alt", "é")]
    // One letter is its own name.
    [InlineData("Q", "Q")]
    [InlineData("z", "z")]
    public void A_glyph_name_is_the_text_it_stands_for(string name, string expected)
    {
        var result = Extract("BT /F1 12 Tf 72 700 Td <01> Tj ET", EncodedFont($"<< /Type /Encoding /Differences [1 /{name}] >>"));
        AssertLines([expected], Lines(result));
        Assert.Null(result.Pages[0].UncertainText);
    }

    [Theory]
    [InlineData("g12")]
    [InlineData("cid0042")]
    [InlineData("nosuchglyph")]
    [InlineData("uni00E")]
    [InlineData("uniD800")]
    [InlineData("u110000")]
    [InlineData("f_nosuchglyph")]
    public void A_glyph_name_that_is_not_known_is_not_guessed_and_asks_for_comparison(string name)
    {
        // The code keeps the Latin-1 reading it always had, and the page says it may be wrong.
        var result = Extract("BT /F1 12 Tf 72 700 Td (AB) Tj ET", EncodedFont($"<< /Type /Encoding /Differences [65 /{name}] >>"));
        AssertLines(["AB"], Lines(result));
        var doubt = Assert.Single(result.Pages[0].UncertainText!);
        Assert.Equal((PdfUncertainTextReason.TextUndecodable, "F1", 1), (doubt.Reason, doubt.ResourceName, doubt.Fragments));
        Assert.Single(result.Diagnostics!, diagnostic => diagnostic.StartsWith("PdfTextUndecodable: PDF page 1: 1 text fragment(s) use font resource /F1", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_glyph_name_asks_only_where_its_code_is_shown()
    {
        var font = EncodedFont("<< /Type /Encoding /Differences [65 /g12] >>");
        var result = Extract("BT /F1 12 Tf 72 700 Td (BCD) Tj ET\nBT /F1 12 Tf 72 680 Td (A) Tj ET", font);
        AssertLines(["BCD", "A"], Lines(result));
        Assert.Equal(1, Assert.Single(result.Pages[0].UncertainText!).Fragments);
    }

    [Fact]
    public void The_notdef_glyph_is_nothing()
    {
        var result = Extract("BT /F1 12 Tf 72 700 Td (ABC) Tj ET", EncodedFont("<< /Type /Encoding /Differences [66 /.notdef] >>"));
        AssertLines(["AC"], Lines(result));
        Assert.Null(result.Pages[0].UncertainText);
    }

    [Fact]
    public void An_encoding_and_its_differences_may_be_objects_of_their_own()
    {
        var objects = new Dictionary<int, string>
        {
            [5] = "<< /Type /Encoding /BaseEncoding 6 0 R /Differences 7 0 R >>",
            [6] = "/MacRomanEncoding",
            [7] = "[ 97 /alpha 200 /uni00E9 ]",
        };
        Assert.Equal("αb é", OneLine("BT /F1 12 Tf 72 700 Td (ab \\310) Tj ET", EncodedFont("5 0 R"), objects));
        // And an /Encoding that is only a reference to a name.
        Assert.Equal("é", OneLine("BT /F1 12 Tf 72 700 Td (\\216) Tj ET", EncodedFont("8 0 R"), new Dictionary<int, string> { [8] = "/MacRomanEncoding" }));
    }

    [Fact]
    public void A_name_written_with_hash_escapes_is_decoded()
    {
        Assert.Equal("é", OneLine("BT /F1 12 Tf 72 700 Td <01> Tj ET", EncodedFont("<< /Type /Encoding /Differences [1 /uni00#45#39] >>")));
    }

    [Fact]
    public void Differences_may_name_a_code_twice_and_the_last_name_wins()
    {
        var font = EncodedFont("<< /Type /Encoding /Differences [65 /alpha 65 /beta 66 /gamma 66 /delta] >>");
        Assert.Equal("βδ", OneLine("BT /F1 12 Tf 72 700 Td (AB) Tj ET", font));
    }

    [Fact]
    public void The_last_name_for_a_code_decides_whether_the_code_is_doubtful()
    {
        // A name that is known after one that was not: the code is read, and nothing asks for a comparison.
        var known = Extract("BT /F1 12 Tf 72 700 Td (A) Tj ET", EncodedFont("<< /Type /Encoding /Differences [65 /g12 65 /alpha] >>"));
        AssertLines(["α"], Lines(known));
        Assert.Null(known.Pages[0].UncertainText);
        // An expert code the table lacks, named by /Differences.
        var expert = Extract("BT /F1 12 Tf 72 700 Td <8F> Tj ET", EncodedFont("<< /Type /Encoding /BaseEncoding /MacExpertEncoding /Differences [143 /alpha] >>"));
        AssertLines(["α"], Lines(expert));
        Assert.Null(expert.Pages[0].UncertainText);
        // And the other way round: the unknown name that comes last is the one the code has.
        var unknown = Extract("BT /F1 12 Tf 72 700 Td (A) Tj ET", EncodedFont("<< /Type /Encoding /Differences [65 /alpha 65 /g12] >>"));
        AssertLines(["A"], Lines(unknown));
        Assert.Equal(1, Assert.Single(unknown.Pages[0].UncertainText!).Fragments);
    }

    [Fact]
    public void A_type3_font_reads_the_names_of_its_differences()
    {
        var font = "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 100 100] /FontMatrix [0.01 0 0 0.01 0 0] /CharProcs << >> " +
                   "/Encoding << /Type /Encoding /Differences [65 /a /b /fi] >> /FirstChar 65 /LastChar 67 /Widths [40 75 80] >>";
        Assert.Equal("abfi", OneLine("BT /F1 10 Tf 72 700 Td (ABC) Tj ET", font));
    }

    // ---- /ActualText ------------------------------------------------------------------------------------------------------

    [Theory]
    // A byte order mark makes a literal string UTF-16, as it does a hexadecimal one.
    [InlineData("(\\376\\377\\000e\\000\\351)", "eé")]
    [InlineData("<FEFF006500E9>", "eé")]
    [InlineData("(\\377\\376e\\000\\351\\000)", "eé")]
    [InlineData("(\\376\\377\\330\\065\\334\\331)", "\U0001D4D9")]
    // Without one it is PDFDocEncoding (Latin-1 here), and nested parentheses belong to it.
    [InlineData("(caf\\351)", "café")]
    [InlineData("(plain (nested) text)", "plain (nested) text")]
    [InlineData("(a\\(b\\))", "a(b)")]
    public void An_actual_text_string_is_decoded_from_its_bytes_honouring_a_byte_order_mark(string actualText, string expected)
    {
        Assert.Equal(expected, OneLine($"BT /F1 12 Tf 72 700 Td /Span << /ActualText {actualText} >> BDC (x) Tj EMC ET", Standard));
    }

    [Fact]
    public void Actual_text_ends_with_its_marked_content_and_the_text_after_it_is_read_as_shown()
    {
        var content = "BT /F1 12 Tf 72 700 Td /Span << /ActualText (\\376\\377\\000e) >> BDC (x) Tj EMC\n0 -20 Td (plain) Tj ET";
        AssertLines(["e", "plain"], Lines(content, Standard));
    }

    // ---- a ToUnicode map that lacks codes -----------------------------------------------------------------------------------

    private static string SimpleMap(params (int Code, char Character)[] entries) => Stream(
        "1 begincodespacerange <00> <FF> endcodespacerange\n" + $"{entries.Length} beginbfchar\n" +
        string.Concat(entries.Select(entry => $"<{entry.Code:X2}> <{(int)entry.Character:X4}>\n")) + "endbfchar");

    [Fact]
    public void A_simple_font_reads_the_codes_its_to_unicode_map_lacks_through_its_encoding()
    {
        // A and B are mapped (to X and Y); the Windows quotes and é, which are not, are what WinAnsi says.
        var font = "<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAB+Subset /Encoding /WinAnsiEncoding /ToUnicode 5 0 R >>";
        var objects = new Dictionary<int, string> { [5] = SimpleMap((0x41, 'X'), (0x42, 'Y')) };
        var result = Extract("BT /F1 12 Tf 72 700 Td (AB\\223CD\\224 \\351) Tj ET", font, objects);
        AssertLines(["XY“CD” é"], Lines(result));
        Assert.Null(result.Pages[0].UncertainText);
        Assert.Empty(result.Diagnostics!);
    }

    [Fact]
    public void The_to_unicode_map_wins_over_the_encoding_where_it_gives_a_code()
    {
        var font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Type /Encoding /Differences [65 /alpha] >> /ToUnicode 5 0 R >>";
        var objects = new Dictionary<int, string> { [5] = SimpleMap((0x41, 'X')) };
        Assert.Equal("XB", OneLine("BT /F1 12 Tf 72 700 Td (AB) Tj ET", font, objects));
    }

    [Fact]
    public void A_nonsymbolic_simple_font_without_an_encoding_keeps_reading_the_codes_its_map_lacks_as_latin_1()
    {
        var font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 5 0 R >>";
        var objects = new Dictionary<int, string> { [5] = SimpleMap((0x41, 'X')) };
        var result = Extract("BT /F1 12 Tf 72 700 Td (AB\\351) Tj ET", font, objects);
        AssertLines(["XBé"], Lines(result));
        Assert.Null(result.Pages[0].UncertainText);
    }

    /// <summary>A composite font whose glyphs are an em wide (/DW 1000) except those <paramref name="widths"/> (a /W array) gives.</summary>
    private static string Composite(string? toUnicode = "5 0 R", string encoding = "/Identity-H", string widths = "") =>
        $"<< /Type /Font /Subtype /Type0 /BaseFont /AAAAAB+Gothic /Encoding {encoding} " +
        $"/DescendantFonts [<< /Type /Font /Subtype /CIDFontType2 /BaseFont /AAAAAB+Gothic /DW 1000{(widths.Length == 0 ? "" : " /W " + widths)} >>]" +
        (toUnicode is null ? "" : $" /ToUnicode {toUnicode}") + " >>";

    private static IReadOnlyDictionary<int, string> JapaneseMap() => new Dictionary<int, string> { [5] = TwoByteCMap((0x41, '日'), (0x42, '本')) };

    [Fact]
    public void A_composite_font_reports_a_code_its_to_unicode_map_lacks()
    {
        // 0x0043 is not in the map: the text keeps it as the code spells it, and the page asks for comparison.
        var result = Extract("BT /F1 18 Tf 50 780 Td <004100430042> Tj ET", Composite(), JapaneseMap());
        AssertLines(["日\0C本"], Lines(result));
        var doubt = Assert.Single(result.Pages[0].UncertainText!);
        Assert.Equal((PdfUncertainTextReason.TextUndecodable, "F1", 1), (doubt.Reason, doubt.ResourceName, doubt.Fragments));
        Assert.StartsWith("PdfTextUndecodable: PDF page 1: 1 text fragment(s) use font resource /F1", Assert.Single(result.Diagnostics!), StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_font_counts_each_fragment_that_shows_a_missing_code_once()
    {
        var result = Extract("BT /F1 18 Tf 50 780 Td <0041> Tj ET\nBT /F1 18 Tf 50 740 Td <00430043> Tj ET\nBT /F1 18 Tf 50 700 Td <0044> Tj ET", Composite(), JapaneseMap());
        AssertLines(["日", "\0C\0C", "\0D"], Lines(result));
        Assert.Equal(2, Assert.Single(result.Pages[0].UncertainText!).Fragments);
    }

    /// <summary>The widths of a Calibri subset's glyphs 3 (the space), 31 and 32, which a map written for the letters leaves out.</summary>
    private const string SpaceWidths = "[0 [0] 3 [226] 31 32 250]";

    [Theory]
    // The codes a producer leaves out of a map on purpose are no doubt: the space, a control code (the space glyph
    // of a subset font is often one) and the notdef glyph. Of two bytes they are glyph indexes, and only a glyph as wide as a
    // space (here from /W) is a space: this is the Calibri subset of a Word document, whose map lacks glyph 3.
    [InlineData("<004100200042>", "日 本")]
    [InlineData("<004100030042>", "日 本")]
    [InlineData("<004100000042>", "日本")]
    [InlineData("<0041001F0042>", "日 本")]
    public void A_composite_font_does_not_report_the_space_control_and_notdef_codes_it_leaves_out(string codes, string expected)
    {
        var result = Extract($"BT /F1 18 Tf 50 780 Td {codes} Tj ET", Composite(widths: SpaceWidths), JapaneseMap());
        AssertLines([expected], Lines(result));
        Assert.Null(result.Pages[0].UncertainText);
        Assert.Empty(result.Diagnostics!);
    }

    [Fact]
    public void An_omitted_CID_32_stays_undecodable_when_uniform_widths_cannot_identify_it_as_a_space()
    {
        var font = "<< /Type /Font /Subtype /Type0 /BaseFont /Mono /Encoding /Identity-H " +
            "/DescendantFonts [5 0 R] /ToUnicode 6 0 R >>";
        var objects = new Dictionary<int, string>
        {
            [5] = "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Mono /DW 600 /W [0 255 600] >>",
            [6] = TwoByteCMap((0x41, '日'), (0x42, '本')),
        };
        var result = Extract("BT /F1 18 Tf 50 780 Td <004100200042> Tj ET", font, objects);

        AssertLines(["日\0 本"], Lines(result));
        Assert.Single(result.Pages[0].UncertainText!);
        Assert.Contains(result.Diagnostics!, message => message.StartsWith("PdfTextUndecodable:", StringComparison.Ordinal));
    }

    [Fact]
    public void Identity_H_CID_32_can_map_to_a_letter_in_a_subset_font()
    {
        var font = "<< /Type /Font /Subtype /Type0 /BaseFont /Subset /Encoding /Identity-H " +
            "/DescendantFonts [5 0 R] /ToUnicode 6 0 R >>";
        var objects = new Dictionary<int, string>
        {
            [5] = "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Subset /DW 600 /W [0 255 600] >>",
            [6] = TwoByteCMap((0x20, 'A')),
        };

        Assert.Equal("A", OneLine("BT /F1 18 Tf 50 780 Td <0020> Tj ET", font, objects));
    }

    [Theory]
    [InlineData("0013", "日\0\u0013本")]
    [InlineData("001F", "日\0\u001F本")]
    public void A_missing_low_CID_that_is_not_32_stays_undecodable_even_when_widths_are_uniform(string code, string expected)
    {
        var font = "<< /Type /Font /Subtype /Type0 /BaseFont /Mono /Encoding /Identity-H " +
            "/DescendantFonts [5 0 R] /ToUnicode 6 0 R >>";
        var objects = new Dictionary<int, string>
        {
            [5] = "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Mono /DW 600 /W [0 255 600] >>",
            [6] = TwoByteCMap((0x41, '日'), (0x42, '本')),
        };
        var result = Extract($"BT /F1 18 Tf 50 780 Td <0041{code}0042> Tj ET", font, objects);

        AssertLines([expected], Lines(result));
        Assert.Single(result.Pages[0].UncertainText!);
    }

    [Theory]
    // Arial's glyphs 19 to 28 are the digits 0 to 9, 556 thousandths wide, and 3 is the space: a map written for the letters
    // and the space alone lacks the digits, which are no space for being a low code.
    [InlineData("<0041001300140042>", "日\0\u0013\0\u0014本", "[3 [278] 19 28 556]")]
    [InlineData("<004100030013001400030042>", "日 \0\u0013\0\u0014 本", "[3 [278] 19 28 556]")]
    // Glyph widths that are no space's, or none at all (an em, as /DW says), do not make a glyph one.
    [InlineData("<004100200042>", "日\0 本", "[32 [1000]]")]
    [InlineData("<004100200042>", "日\0 本", "[32 [140]]")]
    [InlineData("<004100200042>", "日\0 本", "[32 [410]]")]
    [InlineData("<004100030042>", "日\0\u0003本", "")]
    public void A_composite_font_reports_a_low_code_its_map_lacks_unless_the_font_gives_it_the_width_of_a_space(string codes, string expected, string widths)
    {
        var result = Extract($"BT /F1 18 Tf 50 780 Td {codes} Tj ET", Composite(widths: widths), JapaneseMap());
        AssertLines([expected], Lines(result));
        var doubt = Assert.Single(result.Pages[0].UncertainText!);
        Assert.Equal((PdfUncertainTextReason.TextUndecodable, "F1", 1), (doubt.Reason, doubt.ResourceName, doubt.Fragments));
        Assert.Single(result.Diagnostics!, diagnostic => diagnostic.StartsWith("PdfTextUndecodable: PDF page 1: 1 text fragment(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_space_the_map_lacks_is_a_space_whichever_way_the_text_is_turned()
    {
        // Rotated text is not measured, yet the font still says what width its glyph 3 has.
        var result = Extract("BT /F1 18 Tf 0 1 -1 0 300 400 Tm <004100030042> Tj ET", Composite(widths: SpaceWidths), JapaneseMap());
        AssertLines(["日 本"], Lines(result));
        Assert.Null(result.Pages[0].UncertainText);
    }

    [Fact]
    public void A_simple_font_with_a_partial_map_still_reads_its_low_codes_as_spaces()
    {
        // A single byte is a character code: the codes below the space are control characters a producer leaves out of a map,
        // widths or not.
        var font = "<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAB+Subset /FontDescriptor 6 0 R /ToUnicode 5 0 R >>";
        var objects = new Dictionary<int, string>
        {
            [5] = SimpleMap((0x21, 'T'), (0x22, 'e')),
            [6] = "<< /Type /FontDescriptor /FontName /AAAAAB+Subset /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>",
        };
        var result = Extract("BT /F1 12 Tf 72 700 Td (!\\001\"\\040!) Tj ET", font, objects);
        AssertLines(["T e T"], Lines(result));
        Assert.Null(result.Pages[0].UncertainText);
    }

    [Fact]
    public void A_complete_map_and_a_unicode_cmap_are_not_questioned()
    {
        Assert.Null(Extract("BT /F1 18 Tf 50 780 Td <00410042> Tj ET", Composite(), JapaneseMap()).Pages[0].UncertainText);
        var unicode = Extract("BT /F1 18 Tf 50 780 Td <30533093> Tj ET", Composite(null, "/UniJIS-UCS2-H"));
        AssertLines(["こん"], Lines(unicode));
        Assert.Null(unicode.Pages[0].UncertainText);
    }

    [Fact]
    public void A_symbolic_simple_font_with_a_partial_map_reports_the_codes_it_lacks_like_a_composite_one()
    {
        var font = "<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAB+Subset /FontDescriptor 6 0 R /ToUnicode 5 0 R >>";
        var objects = new Dictionary<int, string>
        {
            [5] = SimpleMap((0x21, 'T'), (0x22, 'e')),
            [6] = "<< /Type /FontDescriptor /FontName /AAAAAB+Subset /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>",
        };
        var result = Extract("BT /F1 12 Tf 72 700 Td (!\"#) Tj ET", font, objects);
        AssertLines(["Te#"], Lines(result));
        Assert.Equal(1, Assert.Single(result.Pages[0].UncertainText!).Fragments);
        Assert.Null(Extract("BT /F1 12 Tf 72 700 Td (!\") Tj ET", font, objects).Pages[0].UncertainText);
    }
}
