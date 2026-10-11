using System.Text;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.MeasuredPdf;

namespace DocRedock.Tests.Pdf;

/// <summary>
/// Text in boxes side by side, or on the two sides of a vertical rule that divides a frame, is read a
/// column at a time: the left one top to bottom, then the next, and the page goes on below. Lines of two
/// boxes used to be interleaved by baseline, and a line that wrapped was a paragraph of its own or was
/// glued to the other column's line on its baseline. Inside such a box a wrapped line is joined to the
/// line it continues - only when every sign says so. Pages without such boxes read as they always did.
/// </summary>
public sealed partial class PdfBoxedColumnReadingOrderTests
{
    private const string Fonts = "<< /Font << /F1 4 0 R /F2 7 0 R /F3 10 0 R >> >>";

    /// <summary>A fixed-pitch Latin font: every character is 0.6 of the size wide.</summary>
    private static readonly string FixedPitchFont =
        "<< /Type /Font /Subtype /TrueType /BaseFont /Mono /FirstChar 32 /LastChar 126 /Widths [" + string.Join(" ", Enumerable.Repeat(600, 95)) + "] >>";

    /// <summary>A Latin font that gives no widths: its text is placed, but how wide it is is only an estimate.</summary>
    private const string UnmeasuredFont = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";

    /// <summary>A page built from content: Latin text in the measured Helvetica of <see cref="MeasuredPdf"/>
    /// (F1) or, when asked, in a fixed-pitch font (F3), anything else in a font with a code for each character
    /// it is given (F2), an em wide each, as Japanese text is set; and the rectangles and rules a page frames its
    /// text with.</summary>
    private sealed class Sheet(bool measured = true)
    {
        private readonly StringBuilder content = new();
        private readonly Dictionary<char, int> codes = [];
        public int Shows { get; private set; }

        public Sheet Add(string operators)
        {
            content.AppendLine(operators);
            return this;
        }

        /// <summary>A frame drawn as four rules, as ReportLab draws the box of a table.</summary>
        public Sheet Box(double x, double y, double width, double height) =>
            Rule(x, y, x + width, y).Rule(x, y + height, x + width, y + height).Rule(x, y, x, y + height).Rule(x + width, y, x + width, y + height);

        public Sheet Rule(double x1, double y1, double x2, double y2) => Add($"0.6 w {N(x1)} {N(y1)} m {N(x2)} {N(y2)} l S");

        public Sheet Frame(double x, double y, double width, double height) => Add($"0.6 w {N(x)} {N(y)} {N(width)} {N(height)} re S");

        public Sheet Fill(double x, double y, double width, double height) => Add($"{N(x)} {N(y)} {N(width)} {N(height)} re f");

        /// <summary>A line of text starting at <paramref name="x"/>: a show for each run of Latin text or of
        /// anything else, each where the one before it ends. Latin text is set in the fixed-pitch font when
        /// <paramref name="monoLatin"/> is true.</summary>
        public Sheet Text(double x, double y, double size, string text, bool monoLatin = false)
        {
            for (var start = 0; start < text.Length;)
            {
                var latin = IsLatin(text[start]);
                var end = start;
                while (end < text.Length && IsLatin(text[end]) == latin) end++;
                var run = text[start..end];
                if (latin)
                    content.AppendLine($"BT /{(monoLatin ? "F3" : "F1")} {N(size)} Tf 1 0 0 1 {N(x)} {N(y)} Tm ({Escaped(run)}) Tj ET");
                else
                    content.AppendLine($"BT /F2 {N(size)} Tf 1 0 0 1 {N(x)} {N(y)} Tm <{string.Concat(run.Select(Code))}> Tj ET");
                x += latin ? monoLatin ? run.Length * 0.6 * size : Width(run, size) : run.Length * size;
                Shows++;
                start = end;
            }
            return this;
        }

        /// <summary>Latin text drawn as <paramref name="shown"/> and given as <paramref name="actual"/> (/ActualText), as a
        /// producer does for a ligature: the font's widths do not go with the text that is given.</summary>
        public Sheet Actual(double x, double y, double size, string shown, string actual)
        {
            content.AppendLine($"/Span << /ActualText ({Escaped(actual)}) >> BDC BT /F1 {N(size)} Tf 1 0 0 1 {N(x)} {N(y)} Tm ({Escaped(shown)}) Tj ET EMC");
            Shows++;
            return this;
        }

        /// <summary>A line of Latin text in the fixed-pitch font, starting at <paramref name="x"/>.</summary>
        public Sheet Mono(double x, double y, double size, string text)
        {
            content.AppendLine($"BT /F3 {N(size)} Tf 1 0 0 1 {N(x)} {N(y)} Tm ({Escaped(text)}) Tj ET");
            Shows++;
            return this;
        }

        private static string Escaped(string run) => run.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

        private string Code(char character)
        {
            if (!codes.TryGetValue(character, out var code)) codes[character] = code = codes.Count + 1;
            return code.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
        }

        public byte[] Pdf() => Page(content.ToString(), Fonts, new Dictionary<int, string>
        {
            [4] = measured ? SimpleFont : UnmeasuredFont,
            [10] = FixedPitchFont,
            [7] = "<< /Type /Font /Subtype /Type0 /BaseFont /Mincho /Encoding /Identity-H /DescendantFonts [8 0 R] /ToUnicode 9 0 R >>",
            [8] = "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Mincho /DW 1000 >>",
            [9] = TwoByteCMap(codes.Count == 0 ? [(1, ' ')] : codes.Select(entry => (entry.Value, entry.Key)).ToArray()),
        });
    }

    private static bool IsLatin(char character) => character is >= ' ' and <= '~';

    private static double AdvanceOf(char character, double size, bool mono = false) =>
        IsLatin(character) ? mono ? 0.6 * size : Advance(character) / 1000.0 * size : size;

    /// <summary>Breaks <paramref name="text"/> into lines of at most <paramref name="width"/>, as a word processor
    /// would: after a space, or after any Japanese character. Latin characters are 0.6 of the size wide when
    /// <paramref name="mono"/> is true.</summary>
    private static List<string> Wrap(string text, double size, double width, bool mono = false)
    {
        var lines = new List<string>();
        var (start, lastBreak, used) = (0, -1, 0.0);
        for (var index = 0; index < text.Length; index++)
        {
            used += AdvanceOf(text[index], size, mono);
            if (used <= width + 1e-6)
            {
                if (text[index] == ' ' || !IsLatin(text[index])) lastBreak = index;
                continue;
            }
            var end = lastBreak >= start ? lastBreak : index - 1;
            lines.Add(text[start..(end + 1)].TrimEnd());
            start = end + 1;
            while (start < text.Length && text[start] == ' ') start++;
            (index, lastBreak, used) = (start - 1, -1, 0.0);
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    /// <summary>A paragraph wrapped to <paramref name="width"/> and drawn from <paramref name="y"/> down, a
    /// line to a line. Returns the baseline of its last line.</summary>
    private static double Paragraph(Sheet sheet, double x, double y, double size, double leading, double width, string text)
    {
        var lines = Wrap(text, size, width);
        for (var index = 0; index < lines.Count; index++) sheet.Text(x, y - index * leading, size, lines[index]);
        return y - (lines.Count - 1) * leading;
    }

    /// <summary>A bullet whose text hangs from it: the first line starts with "• ", the lines after it where the text
    /// after the bullet starts, plus <paramref name="slip"/> (ReportLab sets them a little off).</summary>
    private static double Bullet(Sheet sheet, double x, double y, double size, double leading, double width, string text, double slip = 0)
    {
        var start = x + size + Width(" ", size);
        var lines = Wrap(text, size, x + width - start);
        sheet.Text(x, y, size, "• " + lines[0]);
        for (var index = 1; index < lines.Count; index++) sheet.Text(start + slip, y - index * leading, size, lines[index]);
        return y - (lines.Count - 1) * leading;
    }

    private static string[] Texts(PdfExtractionResult result) => result.Pages[0].Regions.Select(region => region.Text).ToArray();

    private static string[] Texts(Sheet sheet) => Texts(PdfTextExtractor.Extract(sheet.Pdf()));

    private const string LeftSentence = "The first box holds a paragraph of plain text that wraps over several lines of the box it is in";
    private const string RightSentence = "A second box stands beside it and holds more words which wrap over just as many lines in their own place";

    [Fact]
    public void Two_boxes_side_by_side_are_read_one_box_at_a_time_and_their_wrapped_lines_are_joined()
    {
        var sheet = new Sheet().Frame(50, 600, 240, 110).Frame(310, 600, 240, 110);
        sheet.Text(58, 690, 10, "Purpose");
        Paragraph(sheet, 58, 677, 10, 12, 224, LeftSentence);
        sheet.Text(318, 690, 10, "Criteria");
        Paragraph(sheet, 318, 677, 10, 12, 224, RightSentence);

        var result = PdfTextExtractor.Extract(sheet.Pdf());

        Assert.Equal(["Purpose", LeftSentence, "Criteria", RightSentence], Texts(result));
        Assert.Equal(2, result.Pages[0].ColumnCount);
        // Every fragment is in exactly one line.
        var ids = result.Pages[0].Regions.SelectMany(region => region.SourceTextIds).ToArray();
        Assert.Equal(sheet.Shows, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void Boxes_drawn_as_filled_rectangles_are_columns_too()
    {
        var sheet = new Sheet().Fill(50, 600, 240, 110).Fill(310, 600, 240, 110);
        Paragraph(sheet, 58, 690, 10, 12, 224, LeftSentence);
        Paragraph(sheet, 318, 690, 10, 12, 224, RightSentence);
        Assert.Equal([LeftSentence, RightSentence], Texts(sheet));
    }

    /// <summary>The frame of page 2 of complex-layout.pdf in Latin: a rule divides it, a heading and bullets
    /// in each column, one bullet of each wrapping, and a heading under the whole.</summary>
    private static Sheet DividedFrame(out string[] expected)
    {
        var sheet = new Sheet().Box(50, 500, 500, 160).Rule(300, 500, 300, 660);
        expected = DividedText(sheet);
        return sheet;
    }

    /// <summary>The text of <see cref="DividedFrame"/>, written on <paramref name="sheet"/>; the order it reads in.</summary>
    private static string[] DividedText(Sheet sheet)
    {
        sheet.Text(58, 640, 12, "4.1 Applicants");
        Bullet(sheet, 60, 622, 9, 11, 232, "Receipts are accepted as PDF or image files of up to ten megabytes each", slip: 2.9);
        Bullet(sheet, 60, 590, 9, 11, 232, "Amounts state their currency code");
        sheet.Text(58, 570, 12, "4.2 Interface");
        Bullet(sheet, 60, 552, 9, 11, 232, "Every call answers with a problem document that names the field at fault and the rule it broke", slip: 2.9);
        sheet.Text(308, 640, 12, "4.3 Approvers");
        Bullet(sheet, 310, 622, 9, 11, 232, "Approval records its reason and the state before and after it, which are added to the audit log", slip: 2.9);
        Bullet(sheet, 310, 590, 9, 11, 232, "Large amounts go to two approvers");
        sheet.Text(308, 570, 12, "4.4 Operations");
        Bullet(sheet, 310, 552, 9, 11, 232, "Retries are spaced one five and thirty seconds apart");
        sheet.Text(58, 480, 14, "5. Images and notes");
        return
        [
            "4.1 Applicants",
            "• Receipts are accepted as PDF or image files of up to ten megabytes each",
            "• Amounts state their currency code",
            "4.2 Interface",
            "• Every call answers with a problem document that names the field at fault and the rule it broke",
            "4.3 Approvers",
            "• Approval records its reason and the state before and after it, which are added to the audit log",
            "• Large amounts go to two approvers",
            "4.4 Operations",
            "• Retries are spaced one five and thirty seconds apart",
            "5. Images and notes",
        ];
    }

    [Fact]
    public void A_frame_divided_by_a_rule_reads_the_left_column_then_the_right_and_a_heading_below_comes_last()
    {
        var sheet = DividedFrame(out var expected);
        var result = PdfTextExtractor.Extract(sheet.Pdf());
        Assert.Equal(expected, Texts(result));
        Assert.Equal(2, result.Pages[0].ColumnCount);
    }

    [Fact]
    public void A_title_above_the_frame_and_text_below_it_keep_their_places()
    {
        var sheet = DividedFrame(out var expected);
        sheet.Text(58, 690, 14, "4. Business rules");
        Paragraph(sheet, 58, 460, 10, 12, 480, "Below the frame comes a paragraph that is part of the page and not of either column of the frame above it.");
        var texts = Texts(sheet);
        Assert.Equal(["4. Business rules", .. expected, "Below the frame comes a paragraph that is part of the page and not of either column of the frame above it."], texts);
    }

    [Fact]
    public void Wrapped_bullets_are_joined_whatever_the_script_and_a_closing_full_stop_alone_on_a_line_joins_without_a_space()
    {
        // The first bullet wraps in Latin text, the second in Japanese: 24 characters fill the line and the full stop that
        // ends the sentence has no room for it, so it stands alone on the next line.
        const string japanese = "承認や差戻しには理由を記録し前後の状態を監査ログ。";
        var sheet = new Sheet().Box(50, 500, 500, 100).Rule(300, 500, 300, 600);
        Bullet(sheet, 60, 580, 9, 11, 232, "A bullet in Latin text goes on to a second line which belongs to it", slip: 2.9);
        Assert.Equal(["承認や差戻しには理由を記録し前後の状態を監査ログ", "。"], Wrap(japanese, 9, 310 + 232 - (310 + 9 + Width(" ", 9))));
        Bullet(sheet, 310, 580, 9, 11, 232, japanese, slip: 2.9);
        Assert.Equal(["• A bullet in Latin text goes on to a second line which belongs to it", "• " + japanese], Texts(sheet));
    }

    [Fact]
    public void A_ruled_table_stays_a_table_and_a_frame_below_it_is_still_read_by_columns()
    {
        var sheet = new Sheet();
        // A regular 3 x 3 grid with text in every cell: table inference takes it.
        for (var line = 0; line <= 3; line++) sheet.Rule(50, 700 + line * 30, 350, 700 + line * 30).Rule(50 + line * 100, 700, 50 + line * 100, 790);
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++) sheet.Text(58 + column * 100, 770 - row * 30, 9, $"R{row + 1}C{column + 1}");
        sheet.Frame(50, 500, 240, 100).Frame(310, 500, 240, 100);
        Paragraph(sheet, 58, 580, 10, 12, 224, LeftSentence);
        Paragraph(sheet, 318, 580, 10, 12, 224, RightSentence);

        var result = PdfTextExtractor.Extract(sheet.Pdf());

        var table = Assert.Single(result.Tables![1]);
        Assert.Equal(["R1C1", "R1C2", "R1C3", "R2C1", "R2C2", "R2C3", "R3C1", "R3C2", "R3C3"],
            table.Rows.SelectMany(row => row.Cells).Select(cell => cell.Text));
        Assert.Equal([LeftSentence, RightSentence], Texts(result).Where(text => !text.StartsWith('R')));
        Assert.True(PdfTextAccounting.Reconcile(result.Pages[0], result.Tables[1]).IsComplete);
    }

    [Fact]
    public void Two_columns_without_a_box_read_as_they_did_and_their_wrapped_lines_stay_lines()
    {
        // Five baselines, a gutter between the columns: the page-level column detection takes this, no box is involved.
        var sheet = new Sheet();
        var (left, right) = PageColumns(sheet);

        var result = PdfTextExtractor.Extract(sheet.Pdf());

        Assert.Equal([.. left, .. right], Texts(result));
        Assert.Equal(2, result.Pages[0].ColumnCount);
    }

    [Fact]
    public void Boxes_that_hold_a_single_line_each_keep_the_line_they_make_with_the_text_beside_them()
    {
        var sheet = new Sheet().Frame(50, 600, 150, 30).Frame(250, 600, 150, 30);
        sheet.Text(58, 610, 10, "Total 100").Text(258, 610, 10, "Tax 10");
        Assert.Equal(["Total 100 Tax 10"], Texts(sheet));
    }

    [Fact]
    public void A_single_frame_with_no_rule_and_no_neighbour_is_left_as_it_is()
    {
        var sheet = new Sheet().Frame(50, 600, 240, 110);
        Paragraph(sheet, 58, 690, 10, 12, 224, LeftSentence);
        var lines = Wrap(LeftSentence, 10, 224);
        Assert.Equal(lines, Texts(sheet));
    }

    [Fact]
    public void A_grid_of_more_rows_that_is_no_table_is_not_read_by_columns()
    {
        // Three rows, two columns of very different widths: table inference declines (the columns are not regular), and
        // each row is read across, as it always was.
        var sheet = new Sheet();
        for (var line = 0; line <= 3; line++) sheet.Rule(50, 600 + line * 30, 550, 600 + line * 30);
        sheet.Rule(50, 600, 50, 690).Rule(100, 600, 100, 690).Rule(550, 600, 550, 690);
        for (var row = 0; row < 3; row++) sheet.Text(55, 670 - row * 30, 9, $"Item {row + 1}").Text(105, 670 - row * 30, 9, $"Its description {row + 1}");
        var result = PdfTextExtractor.Extract(sheet.Pdf());
        Assert.Empty(result.Tables ?? new Dictionary<int, IReadOnlyList<PdfTable>>());
        Assert.Equal(["Item 1 Its description 1", "Item 2 Its description 2", "Item 3 Its description 3"], Texts(result));
    }

    [Fact]
    public void A_frame_whose_text_is_two_wide_blocks_with_a_channel_between_them_is_read_block_by_block()
    {
        // The summary box of complex-layout.pdf: one frame, no rule, two paragraphs side by side.
        var sheet = new Sheet().Fill(50, 640, 500, 70).Box(50, 640, 500, 70);
        sheet.Text(58, 690, 8, "Purpose");
        Paragraph(sheet, 58, 680, 8, 10, 230, "The report gathers a complex layout and plain text and tables and images in a single document to check the structure after conversion");
        sheet.Text(308, 690, 8, "Criteria");
        Paragraph(sheet, 308, 680, 8, 10, 230, "no character lost / pages in order / tables in row order / headers and footers told apart / images kept as references");
        Assert.Equal(["Purpose",
            "The report gathers a complex layout and plain text and tables and images in a single document to check the structure after conversion",
            "Criteria",
            "no character lost / pages in order / tables in row order / headers and footers told apart / images kept as references"],
            Texts(sheet));
    }

    [Fact]
    public void A_frame_of_labels_and_values_is_not_cut_in_two()
    {
        // The labels are narrow: a block of a few characters is a label and not a column. (Two rows: the page-level
        // detection of columns wants three, and has its own say about more. The second value goes on in a third line, so
        // that the blocks do not pair up line for line, which would leave them as they are in any case.)
        var sheet = new Sheet().Frame(50, 590, 400, 70);
        for (var row = 0; row < 2; row++) sheet.Text(58, 640 - row * 14, 10, $"Name {row + 1}:").Text(180, 640 - row * 14, 10, $"A long value in the second place of row {row + 1} of the form");
        sheet.Text(180, 612, 10, "which goes on in a line of its own");
        Assert.Equal(["Name 1: A long value in the second place of row 1 of the form", "Name 2: A long value in the second place of row 2 of the form", "which goes on in a line of its own"],
            Texts(sheet).Select(text => text.Replace("  ", " ")));
    }

    // --- Lines that are joined, and lines that are not. Each page is a frame with a rule down the middle; the right column
    // --- holds the lines under test, and the left one a paragraph, so that the group is arranged whatever happens to them.

    private static string[] RightColumn(Action<Sheet> draw)
    {
        var sheet = new Sheet().Box(50, 500, 500, 200).Rule(300, 500, 300, 700);
        Paragraph(sheet, 58, 680, 10, 12, 232, LeftSentence);
        draw(sheet);
        return Texts(sheet).Skip(Wrap(LeftSentence, 10, 232).Count > 1 ? 1 : 0).ToArray();
    }

    /// <summary>A line that runs to within two and a half points of the end of the text of the right column of
    /// <see cref="RightColumn"/> (540): the break after it is forced, whatever word follows.</summary>
    private static string FullToTheEdge() => FilledLine(10, 232, "The line under test has words enough to fill");

    /// <summary>How wide <see cref="Sheet.Text"/> sets a line: Helvetica for Latin text, an em a character for the rest.</summary>
    private static double LineWidth(string text, double size, bool mono = false) => text.Sum(character => AdvanceOf(character, size, mono));

    /// <summary>Words that fill a line to within <paramref name="tolerance"/> points of <paramref name="width"/> at <paramref name="size"/>,
    /// started with <paramref name="prefix"/> and ended with <paramref name="ending"/> (a full stop, say). With the default
    /// tolerance no word, however short, would have fitted after it in a column that wide.</summary>
    private static string FilledLine(double size, double width = 232, string prefix = "the line under test has words enough to fill", string ending = "",
        double tolerance = 2.5)
    {
        string[] pool = ["the", "word", "of", "a", "column", "that", "and", "text", "all", "way", "to", "its", "end", "more", "room", "space", "edge",
            "page", "side", "box", "long", "set", "free", "much", "wide", "see", "now", "one", "two", "new", "old"];
        foreach (var first in pool.Prepend(""))
            foreach (var middle in pool.Prepend(""))
                foreach (var last in pool)
                {
                    var line = $"{prefix} {first} {middle} {last}{ending}".Replace("   ", " ").Replace("  ", " ");
                    var used = LineWidth(line, size);
                    if (used <= width && used >= width - tolerance) return line;
                }
        throw new InvalidOperationException($"No line of {width} points at {size} starts with '{prefix}'.");
    }

    [Fact]
    public void A_line_that_fills_the_column_is_joined_to_the_line_below_it_at_the_same_edge_and_spacing()
    {
        var first = FullToTheEdge();
        var texts = RightColumn(sheet => sheet.Text(308, 680, 10, first).Text(308, 668, 10, "and goes on here."));
        Assert.Contains(first + " and goes on here.", texts);
    }

    [Theory]
    [InlineData(30, "a gap larger than the spacing of the paragraph")]
    [InlineData(18.5, "a gap of more than one and a half lines")]
    public void A_line_is_not_joined_to_the_one_above_it_when_the_gap_is_larger_than_the_line_spacing(double gap, string why)
    {
        var first = FullToTheEdge();
        var texts = RightColumn(sheet => sheet.Text(308, 680, 10, first).Text(308, 680 - gap, 10, "starts a paragraph of its own."));
        Assert.True(texts.Contains(first) && texts.Contains("starts a paragraph of its own."), why);
    }

    [Fact]
    public void A_line_that_starts_with_a_bullet_or_a_number_is_never_joined_to_the_one_above_it()
    {
        var first = FullToTheEdge();
        foreach (var marker in new[] { "• ", "- ", "1. ", "(2) ", "a) ", "4.1 " })
        {
            var texts = RightColumn(sheet => sheet.Text(308, 680, 10, first).Text(308, 668, 10, marker + "the next item"));
            Assert.Contains(first, texts);
            Assert.Contains(marker + "the next item", texts);
        }
    }

    [Fact]
    public void A_heading_is_not_joined_to_the_text_above_it_or_below_it()
    {
        var first = FullToTheEdge();
        // A larger heading under a full line, and a short line of the same size above one.
        var texts = RightColumn(sheet => sheet.Text(308, 680, 10, first).Text(308, 666, 14, "Heading")
            .Text(308, 650, 10, "Notes").Text(308, 638, 10, first));
        Assert.Contains(first, texts);
        Assert.Contains("Heading", texts);
        Assert.Contains("Notes", texts);
    }

    [Theory]
    [InlineData(7.5, false)]
    [InlineData(10.4, true)]
    [InlineData(9.2, true)]
    public void A_change_of_size_of_more_than_a_tenth_stops_the_join(double size, bool joined)
    {
        var first = FullToTheEdge();
        var texts = RightColumn(sheet => sheet.Text(308, 680, 10, first).Text(308, 668, size, "and goes on here."));
        Assert.Equal(joined, texts.Contains(first + " and goes on here."));
        Assert.Equal(!joined, texts.Contains(first) && texts.Contains("and goes on here."));
    }

    [Fact]
    public void A_short_line_is_not_taken_for_one_that_wraps_even_when_the_next_one_stands_under_it()
    {
        // "Purpose" and the line under it start at the same edge and are one line spacing apart; the first does not fill the column.
        var texts = RightColumn(sheet => sheet.Text(308, 680, 10, "Purpose").Text(308, 668, 10, FullToTheEdge()));
        Assert.Contains("Purpose", texts);
        Assert.Contains(FullToTheEdge(), texts);
    }

    [Fact]
    public void A_line_that_does_not_start_at_the_edge_of_the_paragraph_is_not_joined()
    {
        var first = FullToTheEdge();
        // A first line indented by an em (a new paragraph in Japanese text), and a line set well to the right.
        foreach (var indent in new[] { 10, 30 })
        {
            var texts = RightColumn(sheet => sheet.Text(308, 680, 10, first).Text(308 + indent, 668, 10, "starts further in."));
            Assert.Contains(first, texts);
            Assert.Contains("starts further in.", texts);
        }
    }

    [Fact]
    public void A_line_that_wraps_below_a_bullet_at_the_bullets_own_edge_needs_the_bullet_line_to_fill_the_column()
    {
        // Flush with the bullet, not under its text: that is a new paragraph unless the line above it fills the column.
        var longBullet = "• " + Wrap("The bullet text goes on and on across the whole of the column and further", 10, 232 - 10 - Width(" ", 10))[0];
        var texts = RightColumn(sheet => sheet.Text(308, 680, 10, longBullet).Text(308, 668, 10, "flush under the bullet."));
        Assert.Contains(longBullet + " flush under the bullet.", texts);
        var short_ = RightColumn(sheet => sheet.Text(308, 680, 10, "• A short bullet").Text(308, 668, 10, "flush under the bullet."));
        Assert.Contains("• A short bullet", short_);
        Assert.Contains("flush under the bullet.", short_);
    }

    [Theory]
    [InlineData("see TCP/", "IP and more", "see TCP/IP and more")]
    [InlineData("a well-", "known case", "a well-known case")]
    [InlineData("the price is", "100 yen", "the price is 100 yen")]
    [InlineData("quoted text,", "(see below)", "quoted text, (see below)")]
    [InlineData("one -", "two", "one - two")]
    [InlineData("an item", "。", "an item。")]
    [InlineData("all kinds of 道具", "と材料", "all kinds of 道具と材料")]
    [InlineData("日本語の", "text follows", "日本語のtext follows")]
    [InlineData("日本語の", "Text follows", "日本語のText follows")]
    [InlineData("日本語の ", "Text follows", "日本語の Text follows")]
    [InlineData("the end", ", the rest", "the end, the rest")]
    [InlineData("and", ")", "and)")]
    [InlineData("a trailing space ", "follows", "a trailing space follows")]
    [InlineData("a leading", " space follows", "a leading space follows")]
    public void Lines_are_joined_with_a_space_between_words_and_none_where_the_text_has_none(string first, string second, string expected)
    {
        // The bullet's first line runs to the end of the text of its column (288: the rule is at 300 and the padding 12), so the
        // break after it is forced whatever the line under it starts with; that line hangs from the bullet, and the join is all
        // that is tried.
        const double size = 9;
        var x = 288 - LineWidth("• " + first.TrimEnd(), size);
        var sheet = new Sheet().Box(50, 500, 500, 100).Rule(300, 500, 300, 600);
        sheet.Text(x, 580, size, "• " + first).Text(x + size + Width(" ", size), 569, size, second);
        const string right = "A bullet in the right column goes on to a second line that belongs to it";
        Bullet(sheet, 310, 580, size, 11, 230, right);
        Assert.Equal(["• " + expected, "• " + right], Texts(sheet));
    }

    [Fact]
    public void A_strikethrough_follows_the_text_it_crosses_when_the_lines_are_joined()
    {
        var sheet = new Sheet().Box(50, 500, 500, 100).Rule(300, 500, 300, 600);
        // The first line runs to the end of the text of its column (288), so that the break after it is forced.
        var x = 288 - LineWidth("• Price before", 9);
        var start = x + 9 + Width(" ", 9);
        sheet.Text(x, 580, 9, "• Price before").Text(start, 569, 9, "30 now 20");
        // A rule through "30" on the second line.
        sheet.Add($"{N(start)} {N(569 + 3)} {N(Width("30", 9))} 0.6 re f");
        sheet.Text(310, 580, 9, "Right column").Text(310, 569, 9, "second line");
        var result = PdfTextExtractor.Extract(sheet.Pdf());
        var line = Assert.Single(result.Pages[0].Regions, region => region.Text.StartsWith("• Price", StringComparison.Ordinal));
        Assert.Equal("• Price before 30 now 20", line.Text);
        Assert.Equal([new TextRange(15, 17)], line.Struck);
    }

    [Fact]
    public void An_underline_follows_the_text_it_crosses_when_the_lines_are_joined()
    {
        // As the strikethrough above, with a rule under "30" - and one under "Price" on the first line, which is joined to it.
        var sheet = new Sheet().Box(50, 500, 500, 100).Rule(300, 500, 300, 600);
        var x = 288 - LineWidth("• Price before", 9);
        var start = x + 9 + Width(" ", 9);
        sheet.Text(x, 580, 9, "• Price before").Text(start, 569, 9, "30 now 20");
        sheet.Add($"{N(start)} {N(569 - 1.5)} {N(Width("30", 9))} 0.6 re f");
        sheet.Add($"{N(start)} {N(580 - 1.5)} {N(Width("Price", 9))} 0.6 re f");
        sheet.Text(310, 580, 9, "Right column").Text(310, 569, 9, "second line");
        var result = PdfTextExtractor.Extract(sheet.Pdf());
        var line = Assert.Single(result.Pages[0].Regions, region => region.Text.StartsWith("• Price", StringComparison.Ordinal));
        Assert.Equal("• Price before 30 now 20", line.Text);
        Assert.Equal([new TextRange(2, 7), new TextRange(15, 17)], line.Underlined);
        Assert.Null(line.Struck);
    }

    [Fact]
    public void Text_beside_a_box_on_the_same_baseline_is_not_pulled_into_it()
    {
        var sheet = new Sheet().Frame(200, 600, 150, 80).Frame(370, 600, 150, 80);
        sheet.Text(60, 660, 10, "Margin note");
        Paragraph(sheet, 208, 660, 10, 12, 134, "Left box words that wrap over lines");
        Paragraph(sheet, 378, 660, 10, 12, 134, "Right box words that wrap over lines");
        var texts = Texts(sheet);
        Assert.Equal("Margin note", texts[0]);
        Assert.Equal(["Left box words that wrap over lines", "Right box words that wrap over lines"], texts.Skip(1));
    }

    // --- complex-layout.pdf, checked against its rendering.

    private static string FixturePath()
    {
        for (var current = new DirectoryInfo(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Pdf", "complex-layout.pdf");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Complex PDF fixture was not found.");
    }

    [Fact]
    public void The_summary_box_and_the_two_column_box_of_the_complex_fixtures_first_page_read_as_drawn()
    {
        var result = PdfTextExtractor.Extract(FixturePath());
        var texts = result.Pages[0].Regions.Select(region => region.Text).ToArray();
        var start = Array.IndexOf(texts, "目的");
        Assert.True(start >= 0);
        Assert.Equal(
        [
            "目的",
            "複雑な帳票レイアウト、自然な日本語、座標付きテキスト、表・画像を一つのPDFに集約し、変換後の構造と可読性を検証する。",
            "判定基準",
            "文字欠落なし / ページ順保持 / 表の行列順保持 / ヘッダー・フッター混入を識別 / 画像参照を保持",
            "1. 検証観点",
            "入力レイアウト",
            "• A4縦、固定ヘッダー/フッター、ページ番号",
            "• 日本語と英数字・記号・長い識別子の混在",
            "• 表のセル内改行、桁区切り、状態ラベル",
            "• 画像・キャプション・注釈の位置関係",
            "変換で守る情報",
            "• 見出しの階層と本文の段落境界",
            "• 複数カラムの読み順（左→右、上→下）",
            "• 表のヘッダー、列数、行順",
            "• 画像はasset参照または説明を伴う",
            "2. システム構成とデータフロー",
        ], texts.Skip(start).Take(16));
        // The three ruled tables of the page are still tables, with their rows as before.
        Assert.Equal(3, result.Tables![1].Count);
        Assert.Equal(["指標ID", "指標名", "目標値", "実績", "判定", "備考"], result.Tables[1][1].Rows[0].Cells.Select(cell => cell.Text));
    }

    [Fact]
    public void The_framed_columns_of_the_complex_fixtures_second_page_are_read_a_column_at_a_time_with_their_wrapped_lines_joined()
    {
        var result = PdfTextExtractor.Extract(FixturePath());
        var texts = result.Pages[1].Regions.Select(region => region.Text).ToArray();
        var start = Array.IndexOf(texts, "4.1 申請者向け");
        Assert.True(start >= 0);
        Assert.Equal(
        [
            "4.1 申請者向け",
            "• 領収書はPDF/JPEG/PNG、1ファイル10MB、合計50MBまで。",
            "• 金額は税込・税抜を明示し、通貨コードはISO 4217を使用。",
            "• 同一Idempotency-Keyの再送は同じclaim_idを返す。",
            "4.2 API境界",
            "• POST /v1/expense-claims は201 CreatedまたはRFC 7807形式のエラーを返す。",
            "• Authorization: Bearer JWT、TLS 1.2以上、監査IDを必須化。",
            "4.3 承認者向け",
            "• 承認・差戻しには理由を記録し、前後状態を監査ログへ追記する。",
            "• 金額が100,000円を超える場合は二段階承認へ分岐。",
            "• 代理承認は有効期限と委任元を明記する。",
            "4.4 運用・障害対応",
            "• 再送間隔は1/5/30/120/600秒、最大5回。",
            "• DLQ滞留が100件を超えた場合は重大アラートを発報する。",
            "5. 画像・注釈・測定結果",
        ], texts.Skip(start).Take(15));
        // The heading above the frame stays above it; the ruled list of notes below keeps its label column before its values.
        Assert.Equal("4. 左右カラムの業務仕様", texts[start - 1]);
        Assert.True(Array.IndexOf(texts, "注記A") < Array.IndexOf(texts, "注記B") && Array.IndexOf(texts, "注記C") < Array.IndexOf(texts, "画像の文字は本文テキストではなく、OCRを有効化した場合のみ派生テキストになる。"));
    }
}
