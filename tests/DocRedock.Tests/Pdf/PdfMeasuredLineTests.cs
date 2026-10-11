using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.MeasuredPdf;

namespace DocRedock.Tests.Pdf;

/// <summary>
/// Lines and table cells are put together from shows whose positions are measured, with the gaps between
/// them: a tab or a positioned space is a word space although no show carries one, shows a producer
/// split at a change of format (a bold word, a subscript, another font) are not, and a superscript or
/// subscript belongs to the line it sits on, not to a line of its own. Shows whose font gives no widths
/// are joined as before.
/// </summary>
public sealed class PdfMeasuredLineTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-lines-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    private static string Show(double x, double y, double size, string text, string font = "F1") =>
        $"BT /{font} {N(size)} Tf 1 0 0 1 {N(x)} {N(y)} Tm ({text}) Tj ET";

    private static IReadOnlyList<PdfTextRegion> Lines(string content, string resources = Resources, IReadOnlyDictionary<int, string>? objects = null) =>
        PdfTextExtractor.Extract(Page(content, resources, objects)).Pages[0].Regions;

    private static PdfTextRegion Line(string content, string resources = Resources, IReadOnlyDictionary<int, string>? objects = null) =>
        Assert.Single(Lines(content, resources, objects));

    [Fact]
    public void Shows_a_tab_apart_are_separate_words()
    {
        // "Name<tab>Value<tab>Note" as LibreOffice writes it: no space in any show, a gap where the tab is.
        var value = 72 + Width("Name", 11) + 8.64;
        var note = value + Width("Value", 11) + 8.64;
        var line = Line(string.Join("\n", Show(72, 700, 11, "Name"), Show(value, 700, 11, "Value"), Show(note, 700, 11, "Note")));
        Assert.Equal("Name Value Note", line.Text);
    }

    [Theory]
    // Touching, overlapping a little (kerning) and a tenth of an em apart are one word; a fifth of an em
    // (about the width of a space) is not.
    [InlineData(0, "GluedBold")]
    [InlineData(-0.5, "GluedBold")]
    [InlineData(1.1, "GluedBold")]
    [InlineData(2.4, "Glued Bold")]
    [InlineData(9, "Glued Bold")]
    public void A_gap_of_about_a_space_separates_shows_that_carry_none(double gap, string expected)
    {
        var line = Line(Show(72, 700, 11, "Glued") + "\n" + Show(72 + Width("Glued", 11) + gap, 700, 11, "Bold"));
        Assert.Equal(expected, line.Text);
    }

    [Theory]
    [InlineData("Name ", "Value")]
    [InlineData("Name", " Value")]
    public void A_space_already_at_the_join_is_not_doubled(string first, string second)
    {
        var line = Line(Show(72, 700, 11, first) + "\n" + Show(72 + Width(first, 11) + 9, 700, 11, second));
        Assert.Equal("Name Value", line.Text);
    }

    [Fact]
    public void A_gap_is_measured_in_the_larger_of_the_two_sizes()
    {
        // 3 units is a word space for 11 point text but a fifth of an em of 18 point text is 3.6.
        var small = Line(Show(72, 700, 11, "Big") + "\n" + Show(72 + Width("Big", 11) + 3, 700, 11, "Word"));
        var large = Line(Show(72, 700, 18, "Big") + "\n" + Show(72 + Width("Big", 18) + 3, 700, 11, "Word"));
        Assert.Equal(["Big Word", "BigWord"], [small.Text, large.Text]);
    }

    [Fact]
    public void Shows_without_glyph_widths_keep_the_old_rule_of_one_and_a_half_heights()
    {
        // Estimated at 6 units a character, "Name" ends at 96. A gap of 12 is under 18 and glues; 20 is over.
        const string resources = "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Custom >> >> >>";
        var glued = Line(Show(72, 700, 11, "Name") + "\n" + Show(108, 700, 11, "Value"), resources);
        var spaced = Line(Show(72, 700, 11, "Name") + "\n" + Show(116, 700, 11, "Value"), resources);
        Assert.Equal(["NameValue", "Name Value"], [glued.Text, spaced.Text]);
    }

    [Fact]
    public void Shows_of_one_glyph_spaced_out_letter_by_letter_are_one_word_and_a_gap_beyond_the_rest_is_a_space()
    {
        // "SPACED TITLE" tracked by a quarter of an em (3 at 12), a show to the glyph and the space not drawn: every gap is 3 but
        // the one at the space, which is the space and 3. Each of them was a word space ("S P A C E D T I T L E").
        var x = 72.0;
        var shows = new List<string>();
        foreach (var glyph in "SPACED TITLE")
        {
            if (glyph != ' ') shows.Add(Show(x, 700, 12, glyph.ToString()));
            x += Width(glyph.ToString(), 12) + 3;
        }
        Assert.Equal("SPACED TITLE", Line(string.Join("\n", shows)).Text);
    }

    [Fact]
    public void A_rating_scale_of_evenly_spaced_digits_keeps_the_items_separate()
    {
        var shows = string.Join("\n", Enumerable.Range(0, 5).Select(index =>
            Show(72 + 18 * index, 700, 12, (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))));

        Assert.Equal("1 2 3 4 5", Line(shows).Text);
    }

    [Fact]
    public void Evenly_spaced_digits_without_glyph_widths_separate_but_touching_digits_join()
    {
        const string resources = "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Custom >> >> >>";
        var spaced = string.Join("\n", Enumerable.Range(0, 5).Select(index =>
            Show(72 + 18 * index, 700, 12, (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var touching = string.Join("\n", Enumerable.Range(0, 5).Select(index =>
            Show(72 + 6 * index, 680, 12, (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))));

        Assert.Equal(["1 2 3 4 5", "12345"], [Line(spaced, resources).Text, Line(touching, resources).Text]);
    }

    [Fact]
    public void A_repeated_aligned_numeric_and_text_boundary_marks_a_cell_separator()
    {
        const int size = 11;
        const string resources = "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Custom >> >> >>";
        var labels = new[] { "Widget", "Gadget", "Plain", "Terms" };
        var notes = new[] { "N/A", "struck", "literal", "link" };
        var amounts = new[] { 1000, 2000, 3000, 5000 };
        var rows = Enumerable.Range(0, labels.Length).Select(index => string.Join("\n",
            Show(72, 700 - index * 15, size, labels[index]),
            Show(index >= 2 ? 152.07 : 150, 700 - index * 15, size, amounts[index].ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Show(176.391, 700 - index * 15, size, notes[index])));
        var lines = Lines(string.Join("\n", rows), resources);

        Assert.Equal(["Widget", "Gadget", "Plain", "Terms", "1000 N/A", "2000 struck", "3000 literal", "5000 link"], lines.Select(line => line.Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repeated_short_numeric_suffixes_stay_one_token_with_or_without_a_leading_label(bool hasLabel)
    {
        const int size = 11;
        const string resources = "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";
        var rows = Enumerable.Range(0, 3).Select(index => string.Join("\n",
            hasLabel ? Show(72, 700 - index * 15, size, "Model") : string.Empty,
            Show(hasLabel ? 140 : 72, 700 - index * 15, size, new[] { "4", "8", "9" }[index]),
            Show(hasLabel ? 147.5 : 79.5, 700 - index * 15, size, "K")));

        var lines = Lines(string.Join("\n", rows), resources).Select(line => line.Text);
        var expected = hasLabel ? new[] { "Model", "Model", "Model", "4K", "8K", "9K" } : new[] { "4K", "8K", "9K" };
        Assert.Equal(expected, lines);
    }

    [Fact]
    public void Repeated_short_units_keep_their_numeric_suffix_together_after_a_leading_label()
    {
        const int size = 11;
        const string resources = "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";
        var content = string.Join("\n",
            Show(72, 700, size, "Bolt"), Show(140, 700, size, "10"), Show(153, 700, size, "mm"),
            Show(72, 685, size, "Pipe"), Show(140, 685, size, "20"), Show(153, 685, size, "cm"),
            Show(72, 670, size, "Wire"), Show(140, 670, size, "30"), Show(153, 670, size, "mm"));

        Assert.Equal(["Bolt", "Pipe", "Wire", "10mm", "20cm", "30mm"], Lines(content, resources).Select(line => line.Text));
    }

    [Fact]
    public void Repeated_long_same_units_stay_attached_to_their_numbers()
    {
        const int size = 11;
        const string resources = "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";
        var content = string.Join("\n",
            Show(72, 700, size, "Bolt"), Show(140, 700, size, "10"), Show(153, 700, size, "kWh"),
            Show(72, 685, size, "Pipe"), Show(140, 685, size, "20"), Show(153, 685, size, "kWh"),
            Show(72, 670, size, "Wire"), Show(140, 670, size, "30"), Show(153, 670, size, "kWh"));

        Assert.Equal(["Bolt", "Pipe", "Wire", "10kWh", "20kWh", "30kWh"], Lines(content, resources).Select(line => line.Text));
    }

    [Fact]
    public void Different_long_letter_units_stay_attached_to_their_numbers()
    {
        const int size = 11;
        const string resources = "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";
        var content = string.Join("\n",
            Show(72, 700, size, "Bolt"), Show(140, 700, size, "10"), Show(153, 700, size, "kWh"),
            Show(72, 685, size, "Pipe"), Show(140, 685, size, "20"), Show(153, 685, size, "mAh"),
            Show(72, 670, size, "Wire"), Show(140, 670, size, "30"), Show(153, 670, size, "rpm"));

        Assert.Equal(["Bolt", "Pipe", "Wire", "10kWh", "20mAh", "30rpm"], Lines(content, resources).Select(line => line.Text));
    }

    [Fact]
    public void Repeated_numeric_text_boundaries_with_unaligned_preceding_labels_do_not_split()
    {
        const int size = 11;
        const string resources = "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";
        var content = string.Join("\n",
            Show(72, 700, size, "Model"), Show(150, 700, size, "1000"), Show(176.391, 700, size, "N/A"),
            Show(80, 685, size, "Device"), Show(150, 685, size, "2000"), Show(176.391, 685, size, "struck"),
            Show(88, 670, size, "Screen"), Show(150, 670, size, "3000"), Show(176.391, 670, size, "link"));

        Assert.Equal(["Model 1000N/A", "Device 2000struck", "Screen 3000link"], Lines(content, resources).Select(line => line.Text));
    }

    [Fact]
    public void Numeric_only_preceding_fragments_do_not_count_as_an_independent_label_column()
    {
        const int size = 11;
        const string resources = "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";
        var content = string.Join("\n",
            Show(72, 700, size, "2026"), Show(150, 700, size, "1000"), Show(176.391, 700, size, "N/A"),
            Show(72, 685, size, "2027"), Show(150, 685, size, "2000"), Show(176.391, 685, size, "struck"),
            Show(72, 670, size, "2028"), Show(150, 670, size, "3000"), Show(176.391, 670, size, "link"));

        Assert.Equal(["2026", "2027", "2028", "1000N/A", "2000struck", "3000link"], Lines(content, resources).Select(line => line.Text));
    }

    [Theory]
    [InlineData("10", "mm")]
    [InlineData("H2", "O")]
    [InlineData("A", "4")]
    [InlineData("2026Q", "4")]
    public void A_single_number_letter_transition_is_not_enough_to_insert_a_cell_space(string left, string right)
    {
        const int size = 11;
        const string resources = "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";
        var content = string.Join("\n", Show(72, 700, size, left), Show(72 + left.Length * 6 + 1, 700, size, right));

        Assert.Equal(left + right, Line(content, resources).Text);
    }

    [Fact]
    public void A_number_and_word_in_one_show_keep_their_written_spacing()
    {
        Assert.Equal("1000N/A", Line(Show(72, 700, 11, "1000N/A")).Text);
    }

    [Theory]
    // Set a glyph at a time without spacing, a word is not a row of letters and a space is still one gap beyond the rest;
    // and a label of Latin letters spread over a box (1.2 em apart) is no more words than a tracked heading.
    [InlineData("ab cd", 0, "ab cd")]
    [InlineData("I am on it", 0, "I am on it")]
    [InlineData("TEL", 12, "TEL")]
    [InlineData("TOTAL", 12, "TOTAL")]
    public void Shows_of_one_glyph_keep_the_word_spaces_of_their_text_and_the_letter_spacing_of_their_words(string text, double spacing, string expected)
    {
        var x = 72.0;
        var shows = new List<string>();
        foreach (var glyph in text)
        {
            if (glyph != ' ') shows.Add(Show(x, 700, 10, glyph.ToString()));
            x += Width(glyph.ToString(), 10) + spacing;
        }
        Assert.Equal(expected, Line(string.Join("\n", shows)).Text);
    }

    [Fact]
    public void Japanese_characters_spread_over_a_label_a_glyph_at_a_time_gain_no_space()
    {
        // 均等割り付け over four characters, 12 apart (an em is 10): none of the gaps is a word space.
        var line = Line(string.Join("\n", Asian(72, "0001"), Asian(72 + 22, "0002"), Asian(72 + 44, "0001"), Asian(72 + 66, "0002")), AsianResources, WithAsianFont);
        Assert.Equal("氏名氏名", line.Text);
    }

    [Fact]
    public void A_strikethrough_follows_the_space_a_gap_adds()
    {
        var value = 72 + Width("Name", 11) + 9;
        var strike = $"q 0.7 w {N(value)} 703 m {N(value + Width("Value", 11))} 703 l S Q";
        var line = Line(string.Join("\n", Show(72, 700, 11, "Name"), Show(value, 700, 11, "Value"), strike));
        Assert.Equal("Name Value", line.Text);
        Assert.Equal([new TextRange(5, 10)], line.Struck);
    }

    /// <summary>A font with a two-byte code for each of 氏, 名, の and the Hangul 한, 글, each an em wide.</summary>
    private static readonly IReadOnlyDictionary<int, string> WithAsianFont = new Dictionary<int, string>
    {
        [4] = SimpleFont,
        [7] = "<< /Type /Font /Subtype /Type0 /BaseFont /Mincho /Encoding /Identity-H /DescendantFonts [8 0 R] /ToUnicode 9 0 R >>",
        [8] = "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Mincho /DW 1000 >>",
        [9] = TwoByteCMap((1, '氏'), (2, '名'), (3, 'の'), (4, '한'), (5, '글')),
    };

    private const string AsianResources = "<< /Font << /F1 4 0 R /F2 7 0 R >> >>";

    private static string Asian(double x, string codes) => Asian(x, 700, codes);

    private static string Asian(double x, double y, string codes) => $"BT /F2 10 Tf 1 0 0 1 {N(x)} {N(y)} Tm <{codes}> Tj ET";

    [Fact]
    public void Japanese_characters_spread_over_a_line_are_not_separated()
    {
        // 均等割り付け: "氏" and "名" more than an em apart are the one word 氏名. (A gap past one and a
        // half heights, 18 here, is still taken for a gap between two fields, as it always was.)
        var line = Line(Asian(72, "0001") + "\n" + Asian(72 + 10 + 12, "0002"), AsianResources, WithAsianFont);
        Assert.Equal("氏名", line.Text);
    }

    [Theory]
    // The gap word processors add between Japanese and Latin text is a quarter of an em: not a space.
    [InlineData(2.5, "Excelの")]
    [InlineData(6, "Excel の")]
    public void A_gap_between_Japanese_and_Latin_text_has_to_be_wider_than_the_one_they_add_themselves(double gap, string expected)
    {
        var line = Line(Show(72, 700, 10, "Excel") + "\n" + Asian(72 + Width("Excel", 10) + gap, "0003"), AsianResources, WithAsianFont);
        Assert.Equal(expected, line.Text);
    }

    [Fact]
    public void Korean_is_written_with_spaces_between_words()
    {
        var line = Line(Asian(72, "0004") + "\n" + Asian(72 + 10 + 3, "0005"), AsianResources, WithAsianFont);
        Assert.Equal("한 글", line.Text);
    }

    /// <summary>"Water is H" + a subscript "2" + "O and CO", and "Area is 5 m" + a superscript "2" + " and E = mc",
    /// set the way LibreOffice sets them: the script at 0.58 of the size, lowered a little or raised a good
    /// deal, its show starting where the previous one ends.</summary>
    private static string ScriptLines()
    {
        string Script(double y, string before, string script, string after, double lift)
        {
            var x = 72 + Width(before, 11);
            return string.Join("\n", Show(72, y, 11, before), Show(x, y + lift, 6.4, script),
                Show(x + Width(script, 6.4), y, 11, after));
        }
        return string.Join("\n", Script(700, "Water is H", "2", "O and CO", -1), Script(680, "Area is 5 m", "2", " and E = mc", 4.4));
    }

    [Fact]
    public void A_subscript_or_superscript_stays_in_the_line_it_sits_on()
    {
        // Sorted by height, a raised "2" came before the line, a lowered one after it, and either one
        // was cut off as a line of its own while the rest read "Water is HO and CO".
        var lines = Lines(ScriptLines());
        Assert.Equal(["Water is H2O and CO", "Area is 5 m2 and E = mc"], lines.Select(line => line.Text));
    }

    [Fact]
    public void A_script_in_a_column_does_not_break_the_column_layout()
    {
        // Three baselines, two columns; the superscript sits 4.4 above its baseline in the left column,
        // where it used to be a baseline (and a column fragment) of its own.
        var x = 72 + Width("Area is 5 m", 11);
        var content = string.Join("\n", Show(72, 700, 11, "Left one"), Show(330, 700, 11, "Right one"),
            Show(72, 680, 11, "Area is 5 m"), Show(x, 684.4, 6.4, "2"), Show(x + Width("2", 6.4), 680, 11, " here"), Show(330, 680, 11, "Right two"),
            Show(72, 660, 11, "Left three"), Show(330, 660, 11, "Right three"));
        var page = PdfTextExtractor.Extract(Page(content)).Pages[0];
        Assert.True(page.ColumnCount >= 2);
        Assert.Equal(["Left one", "Area is 5 m2 here", "Left three", "Right one", "Right two", "Right three"], page.Regions.Select(line => line.Text));
    }

    [Fact]
    public void A_script_between_two_words_does_not_pull_them_apart_or_together()
    {
        // "x" then a raised "2" then " + y": the script touches the text it follows, the space is the text's.
        var x = 72 + Width("x", 11);
        var line = Line(string.Join("\n", Show(72, 700, 11, "x"), Show(x, 704.4, 6.4, "2"), Show(x + Width("2", 6.4), 700, 11, " + y")));
        Assert.Equal("x2 + y", line.Text);
    }

    [Fact]
    public void A_line_of_much_smaller_text_beside_a_larger_letter_is_not_a_script()
    {
        // A drop cap: the first body line starts beside it a little above its baseline, the second on it.
        // The lines are short enough for a script; it is a quarter of the cap's size that rules them out.
        var cap = Width("D", 36);
        var content = string.Join("\n", Show(72, 700, 36, "D"), Show(72 + cap + 4, 710, 9, "first line"), Show(72 + cap + 4, 700, 9, "second line"));
        Assert.Equal(["first line", "Dsecond line"], Lines(content).Select(line => line.Text));
    }

    [Fact]
    public void A_script_shown_a_glyph_at_a_time_is_still_one_script()
    {
        // Chrome writes a show for every glyph: the subscript "i+1" is three shows beside the "x" it sits on.
        var x = 72 + Width("x", 11);
        var (plus, one) = (x + Width("i", 6.4), x + Width("i+", 6.4));
        var content = string.Join("\n", Show(72, 700, 11, "x"), Show(x, 698.8, 6.4, "i"), Show(plus, 698.8, 6.4, "+"),
            Show(one, 698.8, 6.4, "1"), Show(x + Width("i+1", 6.4), 700, 11, " and 10"));
        Assert.Equal("xi+1 and 10", Line(content).Text);
    }

    [Fact]
    public void A_longer_run_of_smaller_text_beside_a_larger_word_is_a_line_of_its_own()
    {
        // Sixteen characters are no superscript, however close to the word they start.
        var content = string.Join("\n", Show(72, 700, 12, "Note"), Show(72 + Width("Note", 12), 703.6, 9, "a line of its own"));
        Assert.Equal(["a line of its own", "Note"], Lines(content).Select(line => line.Text));
    }

    /// <summary>A 3 x 3 grid of 130 x 30 cells with the cell contents given row by row.</summary>
    private static string Table(string cells, string font = "F1") =>
        "0.8 w 72 610 390 90 re S\n72 670 m 462 670 l S\n72 640 m 462 640 l S\n202 610 m 202 700 l S\n332 610 m 332 700 l S\n" +
        $"BT /{font} 9 Tf 1 0 0 1 78 680 Tm (Item) Tj ET\n{Show(208, 680, 9, "Formula", font)}\n{Show(338, 680, 9, "Note", font)}\n" + cells;

    private static PdfTable TableOf(string content, string resources = Resources, IReadOnlyDictionary<int, string>? objects = null) =>
        Assert.Single(PdfTextExtractor.Extract(Page(content, resources, objects)).Tables![1]);

    private static string[] Texts(PdfTable table) => table.Rows.SelectMany(row => row.Cells).Select(cell => cell.Text).ToArray();

    [Fact]
    public void Shows_of_one_word_in_a_table_cell_are_joined_without_a_space()
    {
        // The cells of "Mi" + "xed" (the next format of a word), "H" + a subscript + "O", "one" + a gap
        // + "two", and "Time" + " p95" (a space the second show carries).
        var xed = 78 + Width("Mi", 9);
        var two = 338 + Width("one", 9) + 6;
        var p95 = 78 + Width("Time", 9);
        var script = 208 + Width("H", 9);
        var cells = string.Join("\n", Show(78, 650, 9, "Mi"), Show(xed, 650, 9, "xed"),
            Show(208, 650, 9, "H"), Show(script, 649, 5.2, "2"), Show(script + Width("2", 5.2), 650, 9, "O"),
            Show(338, 650, 9, "one"), Show(two, 650, 9, "two"),
            Show(78, 620, 9, "Time"), Show(p95, 620, 9, " p95"), Show(208, 620, 9, "x"), Show(338, 620, 9, "-"));
        Assert.Equal(["Item", "Formula", "Note", "Mixed", "H2O", "one two", "Time p95", "x", "-"], Texts(TableOf(Table(cells))));
    }

    [Fact]
    public void Shows_of_one_glyph_spaced_out_letter_by_letter_are_one_word_in_a_table_cell_too()
    {
        // Tracked by a quarter of an em (2.25 at 9), a show to the glyph, the space not drawn.
        var x = 78.0;
        var title = new List<string>();
        foreach (var glyph in "SPACED TITLE")
        {
            if (glyph != ' ') title.Add(Show(x, 650, 9, glyph.ToString()));
            x += Width(glyph.ToString(), 9) + 2.25;
        }
        var cells = string.Join("\n", string.Join("\n", title), Show(208, 650, 9, "x"), Show(338, 650, 9, "x"),
            Show(78, 620, 9, "x"), Show(208, 620, 9, "x"), Show(338, 620, 9, "x"));
        Assert.Equal(["Item", "Formula", "Note", "SPACED TITLE", "x", "x", "x", "x", "x"], Texts(TableOf(Table(cells))));
    }

    [Fact]
    public void A_strikethrough_in_a_table_cell_follows_the_parts_that_are_joined()
    {
        var cd = 208 + Width("ab", 9);
        var strike = $"q 0.7 w {N(cd)} 622.7 m {N(cd + Width("cd", 9))} 622.7 l S Q";
        var cells = string.Join("\n", Show(78, 650, 9, "Plain"), Show(208, 650, 9, "Plain"), Show(338, 650, 9, "Plain"),
            Show(78, 620, 9, "Plain"), Show(208, 620, 9, "ab"), Show(cd, 620, 9, "cd"), strike, Show(338, 620, 9, "Plain"));
        var cell = TableOf(Table(cells)).Rows[2].Cells[1];
        Assert.Equal("abcd", cell.Text);
        Assert.Equal([new TextRange(2, 4)], cell.Struck);
    }

    [Fact]
    public void Parts_of_a_cell_in_another_row_or_out_of_order_keep_their_space()
    {
        var cells = string.Join("\n", Show(78, 656, 9, "upper"), Show(78, 645, 9, "lower"),
            Show(208 + Width("tail", 9), 650, 9, "later"), Show(208, 650, 9, "tail"),
            Show(338, 650, 9, "x"), Show(78, 620, 9, "x"), Show(208, 620, 9, "x"), Show(338, 620, 9, "x"));
        Assert.Equal(["Item", "Formula", "Note", "upper lower", "later tail", "x", "x", "x", "x"], Texts(TableOf(Table(cells))));
    }

    [Fact]
    public void A_show_of_nothing_but_a_space_between_two_parts_of_a_cell_is_a_space_of_the_cell()
    {
        // 氏 and 名 are never parted by a gap, but a space of its own between them is the text's.
        var cells = string.Join("\n", Asian(78, 650, "0001"), Show(88, 650, 10, " "), Asian(88 + Width(" ", 10), 650, "0002"),
            Show(208, 650, 9, "x"), Show(338, 650, 9, "x"), Show(78, 620, 9, "x"), Show(208, 620, 9, "x"), Show(338, 620, 9, "x"));
        Assert.Equal(["Item", "Formula", "Note", "氏 名", "x", "x", "x", "x", "x"], Texts(TableOf(Table(cells), AsianResources, WithAsianFont)));
    }

    [Fact]
    public void Shows_without_glyph_widths_in_a_table_cell_are_still_joined_with_a_space()
    {
        const string resources = "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Custom >> >> >>";
        var cells = string.Join("\n", Show(78, 650, 9, "Mi"), Show(90, 650, 9, "xed"), Show(208, 650, 9, "x"), Show(338, 650, 9, "x"),
            Show(78, 620, 9, "x"), Show(208, 620, 9, "x"), Show(338, 620, 9, "x"));
        Assert.Equal(["Item", "Formula", "Note", "Mi xed", "x", "x", "x", "x", "x"], Texts(TableOf(Table(cells), resources)));
    }

    [Fact]
    public void The_cells_of_the_complex_fixtures_KPI_table_are_not_split_where_the_font_changes()
    {
        // ReportLab changes between its Japanese and Latin font subsets in the middle of a word.
        var cells = PdfTextExtractor.Extract(FixturePath("complex-layout.pdf")).Tables![1]
            .SelectMany(table => table.Rows).SelectMany(row => row.Cells).Select(cell => cell.Text).ToArray();
        Assert.Contains("指標ID", cells);
        Assert.Contains("月間申請件数", cells);
        Assert.Contains("申請処理時間 p95", cells);
        Assert.Contains("2026年7月集計", cells);
        Assert.DoesNotContain(cells, text => text.Contains("指 標", StringComparison.Ordinal));
    }

    private static string FixturePath(string name)
    {
        for (var current = new DirectoryInfo(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Pdf", name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Fixture {name} was not found.");
    }

    [Fact]
    public async Task A_tabbed_line_and_a_formula_are_written_as_the_text_reads()
    {
        var source = Path.Combine(root, "lines.pdf");
        var value = 72 + Width("Name", 11) + 8.64;
        var note = value + Width("Value", 11) + 8.64;
        await File.WriteAllBytesAsync(source, Page(string.Join("\n", Show(72, 740, 11, "Name"), Show(value, 740, 11, "Value"), Show(note, 740, 11, "Note"), ScriptLines())));
        var output = Path.Combine(root, "lines.md");
        await new DocumentService(null, null, discoverPdfRasterizer: false)
            .ExportReadableAsync(new ReadableDocumentExportOptions(source, output, EnableOcr: false));
        var markdown = (await File.ReadAllTextAsync(output)).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("\nName Value Note\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\nWater is H2O and CO\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\nArea is 5 m2 and E = mc\n", markdown, StringComparison.Ordinal);
    }
}
