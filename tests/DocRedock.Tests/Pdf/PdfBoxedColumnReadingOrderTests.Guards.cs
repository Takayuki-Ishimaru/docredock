using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.MeasuredPdf;

namespace DocRedock.Tests.Pdf;

// What is drawn on a page and is no layout of boxes leaves the reading order as it was, and the lines
// of a box that wrap are joined only when every sign says so. Each test here pins a rule of
// PdfTextExtractor.BoxedColumns.cs (how boxes are found, which are columns, where a group is read, which
// lines are joined and how): take that rule out and the test fails. The guards that cannot show in a
// page's text - limits on the size of a drawing, checks that another rule already makes - have no test.
public sealed partial class PdfBoxedColumnReadingOrderTests
{
    /// <summary>What a page reads as with nothing drawn on it: the order before boxes were known.</summary>
    private static string[] ReadPlain(Action<Sheet> text) => ReadDrawn(_ => { }, text);

    private static string[] ReadDrawn(Action<Sheet> drawing, Action<Sheet> text)
    {
        var sheet = new Sheet();
        drawing(sheet);
        text(sheet);
        return Texts(sheet);
    }

    private static Sheet TwoFrames(Sheet sheet) => sheet.Frame(50, 600, 240, 110).Frame(310, 600, 240, 110);

    private static void TwoParagraphs(Sheet sheet)
    {
        Paragraph(sheet, 58, 690, 10, 12, 224, LeftSentence);
        Paragraph(sheet, 318, 690, 10, 12, 224, RightSentence);
    }

    /// <summary>Two columns of text on the page, a gutter between them and five lines or more in each; returns their lines.</summary>
    private static (List<string> Left, List<string> Right) PageColumns(Sheet sheet)
    {
        var left = Wrap(LeftSentence + " and then the words of the left column go on and on", 10, 224);
        var right = Wrap(RightSentence + " and then the words of the right column go on and on", 10, 224);
        for (var line = 0; line < left.Count; line++) sheet.Text(60, 700 - line * 14, 10, left[line]);
        for (var line = 0; line < right.Count; line++) sheet.Text(330, 700 - line * 14, 10, right[line]);
        return (left, right);
    }

    /// <summary>Two frames with a paragraph each are read a box at a time; with <paramref name="spoiler"/> drawn as well, as the
    /// text alone is read.</summary>
    private static void AssertSpoiled(Action<Sheet> spoiler)
    {
        var plain = ReadPlain(TwoParagraphs);
        Assert.NotEqual([LeftSentence, RightSentence], plain);
        Assert.Equal([LeftSentence, RightSentence], ReadDrawn(sheet => TwoFrames(sheet), TwoParagraphs));
        Assert.Equal(plain, ReadDrawn(sheet => { TwoFrames(sheet); spoiler(sheet); }, TwoParagraphs));
    }

    // --- How boxes are drawn

    /// <summary>A heading and a paragraph in each half of a frame divided at 300, the text of each half reaching to within
    /// four points of the rule: no channel of white space is left, so only the rule can tell the halves apart.</summary>
    private static string[] TightText(Sheet sheet)
    {
        const string left = LeftSentence + " and then the words of the left column go on";
        const string right = RightSentence + " and then the words of the right column go on";
        sheet.Text(58, 640, 12, "Applicants");
        Paragraph(sheet, 58, 622, 10, 12, 240, left);
        sheet.Text(302, 640, 12, "Approvers");
        Paragraph(sheet, 302, 622, 10, 12, 240, right);
        return ["Applicants", left, "Approvers", right];
    }

    [Theory]
    [InlineData("rules")]
    [InlineData("rectangle")]
    [InlineData("bars")]
    public void A_frame_and_the_rule_that_divides_it_are_found_however_they_are_drawn(string style)
    {
        var sheet = new Sheet();
        if (style == "rules") sheet.Box(50, 500, 500, 160).Rule(300, 500, 300, 660);
        else if (style == "rectangle") sheet.Frame(50, 500, 500, 160).Rule(300, 500, 300, 660);
        // Thin filled bars, as word processors draw borders.
        else sheet.Fill(50, 499.7, 500, 0.6).Fill(50, 659.7, 500, 0.6).Fill(49.7, 500, 0.6, 160).Fill(549.7, 500, 0.6, 160).Fill(299.7, 500, 0.6, 160);
        var expected = TightText(sheet);
        var result = PdfTextExtractor.Extract(sheet.Pdf());
        Assert.Equal(expected, Texts(result));
        Assert.Equal(2, result.Pages[0].ColumnCount);
    }

    [Theory]
    [InlineData("bottom")]
    [InlineData("right")]
    public void A_figure_with_an_edge_missing_is_not_a_frame(string shortEdge)
    {
        var plain = new Sheet();
        DividedText(plain);
        DividedFrame(out var byColumns);
        Assert.NotEqual(Texts(plain), byColumns);
        // A divided frame whose bottom, or right, edge runs a third of the way only.
        var sheet = new Sheet().Rule(50, 660, 550, 660).Rule(50, 500, 50, 660).Rule(300, 500, 300, 660);
        if (shortEdge == "bottom") sheet.Rule(50, 500, 200, 500).Rule(550, 500, 550, 660);
        else sheet.Rule(50, 500, 550, 500).Rule(550, 500, 550, 550);
        DividedText(sheet);
        Assert.Equal(Texts(plain), Texts(sheet));
    }

    // --- Boxes that are not columns of text

    [Fact]
    public void A_drawing_in_one_of_the_boxes_makes_them_a_figure_and_not_columns_of_text() =>
        // A slanted line under the text: an axis of a chart.
        AssertSpoiled(sheet => sheet.Add("0.6 w 60 605 m 120 625 l S"));

    [Fact]
    public void Boxes_that_a_rule_runs_across_are_a_form_and_not_columns_of_text() =>
        // Under the text of each box, short of its edges.
        AssertSpoiled(sheet => sheet.Rule(55, 612, 285, 612).Rule(315, 612, 545, 612));

    [Fact]
    public void A_page_with_hundreds_of_rules_is_a_drawing_and_not_a_layout_of_boxes() =>
        // A hatched area of 650 short rules.
        AssertSpoiled(sheet => { for (var line = 0; line < 650; line++) sheet.Rule(500, 100 + line * 0.4, 540, 100 + line * 0.4); });

    [Fact]
    public void Cells_of_a_grid_whose_rows_are_cut_differently_are_not_columns_of_text()
    {
        // Two rows of rectangles that touch, the first cut at 300 and the second at 250: no two cells are the rows of one column.
        void Text(Sheet sheet)
        {
            Paragraph(sheet, 58, 690, 10, 12, 234, LeftSentence);
            Paragraph(sheet, 308, 690, 10, 12, 234, RightSentence);
            Paragraph(sheet, 58, 570, 10, 12, 184, RightSentence);
            Paragraph(sheet, 258, 570, 10, 12, 284, LeftSentence);
        }
        // Each pair of cells beside each other would be read as columns without the grid.
        Assert.NotEqual(ReadPlain(Text), ReadDrawn(sheet => sheet.Frame(50, 600, 250, 110).Frame(300, 600, 250, 110), Text));
        Assert.Equal(ReadPlain(Text), ReadDrawn(sheet => sheet.Frame(50, 600, 250, 110).Frame(300, 600, 250, 110).Frame(50, 480, 200, 120).Frame(250, 480, 300, 120), Text));
    }

    [Fact]
    public void A_border_around_the_whole_page_is_not_a_box()
    {
        void Text(Sheet sheet) => PageColumns(sheet);
        Assert.Equal(ReadPlain(Text), ReadDrawn(sheet => sheet.Frame(20, 20, 555, 802), Text));
    }

    [Fact]
    public void A_box_too_small_to_hold_a_column_is_not_one()
    {
        // A badge to the right of a frame, level with its first line.
        void Text(Sheet sheet)
        {
            Paragraph(sheet, 58, 690, 10, 12, 224, LeftSentence);
            sheet.Text(304, 693, 8, "NEW");
        }
        Assert.Equal(ReadPlain(Text), ReadDrawn(sheet => sheet.Frame(50, 600, 240, 110).Fill(300, 690, 22, 10), Text));
        // A box as wide as a word of its own is.
        Assert.NotEqual(ReadPlain(Text), ReadDrawn(sheet => sheet.Frame(50, 600, 240, 110).Fill(300, 688, 40, 16), Text));
    }

    [Fact]
    public void A_short_rule_in_a_frame_does_not_divide_it()
    {
        void Text(Sheet sheet)
        {
            // A heading across the middle of the frame, so that no channel of white space is left to cut it by.
            sheet.Text(58, 690, 10, "A heading that runs across the middle of the frame and both of its halves");
            Paragraph(sheet, 58, 672, 10, 12, 224, LeftSentence);
            Paragraph(sheet, 318, 672, 10, 12, 224, RightSentence);
        }
        Assert.NotEqual(ReadPlain(Text), ReadDrawn(sheet => sheet.Box(50, 560, 500, 160).Rule(300, 560, 300, 720), Text));
        Assert.Equal(ReadPlain(Text), ReadDrawn(sheet => sheet.Box(50, 560, 500, 160).Rule(300, 560, 300, 580), Text));
    }

    // --- Boxes that are columns of each other, and boxes that are not

    [Fact]
    public void Boxes_that_do_not_share_their_height_are_not_columns()
    {
        void Text(Sheet sheet)
        {
            Paragraph(sheet, 58, 690, 10, 12, 224, LeftSentence);
            Paragraph(sheet, 318, 530, 10, 12, 224, RightSentence);
        }
        Assert.Equal(ReadPlain(Text), ReadDrawn(sheet => sheet.Frame(50, 600, 240, 110).Frame(310, 440, 240, 110), Text));
    }

    [Fact]
    public void Boxes_far_apart_are_not_columns_but_boxes_with_a_wide_gutter_are()
    {
        // 240 points between the frames on a page 595 wide: more than a fifth of it.
        void Far(Sheet sheet)
        {
            Paragraph(sheet, 38, 690, 10, 12, 134, LeftSentence);
            Paragraph(sheet, 428, 690, 10, 12, 134, RightSentence);
        }
        Assert.Equal(ReadPlain(Far), ReadDrawn(sheet => sheet.Frame(30, 600, 150, 110).Frame(420, 600, 150, 110), Far));
        // 60 points between them is a gutter.
        void Wide(Sheet sheet)
        {
            Paragraph(sheet, 58, 690, 10, 12, 184, LeftSentence);
            Paragraph(sheet, 318, 690, 10, 12, 184, RightSentence);
        }
        Assert.Equal([LeftSentence, RightSentence], ReadDrawn(sheet => sheet.Frame(50, 600, 200, 110).Frame(310, 600, 200, 110), Wide));
    }

    [Fact]
    public void A_box_inside_another_is_not_a_column_of_it()
    {
        void Text(Sheet sheet)
        {
            Paragraph(sheet, 58, 690, 10, 12, 224, LeftSentence);
            Paragraph(sheet, 308, 670, 10, 12, 224, RightSentence);
        }
        Assert.Equal(ReadPlain(Text), ReadDrawn(sheet => sheet.Frame(50, 500, 500, 210).Frame(300, 520, 240, 170), Text));
    }

    [Fact]
    public void Boxes_one_above_the_other_with_the_same_edges_are_the_rows_of_a_grid_and_not_columns()
    {
        // Four frames, the lower two 4 points under the upper ones.
        void Text(Sheet sheet)
        {
            TwoParagraphs(sheet);
            Paragraph(sheet, 58, 576, 10, 12, 224, RightSentence);
            Paragraph(sheet, 318, 576, 10, 12, 224, LeftSentence);
        }
        Assert.Equal(ReadPlain(Text), ReadDrawn(sheet => TwoFrames(sheet).Frame(50, 486, 240, 110).Frame(310, 486, 240, 110), Text));
    }

    // --- Where a group of boxes is read

    [Fact]
    public void Text_to_the_right_of_the_first_line_of_the_boxes_is_not_pulled_into_it()
    {
        var sheet = new Sheet().Frame(30, 600, 200, 110).Frame(240, 600, 200, 110);
        Paragraph(sheet, 38, 690, 10, 12, 184, LeftSentence);
        Paragraph(sheet, 248, 690, 10, 12, 184, RightSentence);
        sheet.Text(450, 690, 10, "Right note");
        var result = PdfTextExtractor.Extract(sheet.Pdf());
        Assert.Equal([LeftSentence, RightSentence, "Right note"], Texts(result));
        var ids = result.Pages[0].Regions.SelectMany(region => region.SourceTextIds).ToArray();
        Assert.Equal(sheet.Shows, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void A_note_beside_the_boxes_lower_than_their_first_line_is_read_after_them()
    {
        var sheet = new Sheet().Frame(100, 600, 200, 110).Frame(310, 600, 200, 110);
        Paragraph(sheet, 108, 690, 10, 12, 184, LeftSentence);
        Paragraph(sheet, 318, 690, 10, 12, 184, RightSentence);
        sheet.Text(10, 650, 10, "Side note");
        Assert.Equal([LeftSentence, RightSentence, "Side note"], Texts(sheet));
    }

    [Fact]
    public void The_text_of_boxes_in_a_page_of_two_columns_is_read_in_its_place_and_a_note_beside_it_stays_a_line_of_its_own()
    {
        var sheet = new Sheet();
        var (left, right) = PageColumns(sheet);
        // The first line of the boxes is the next line of the left column of the page, and a note stands to its left on the same baseline.
        var first = 700 - Math.Max(left.Count, right.Count) * 14;
        sheet.Frame(100, first - 40, 92, 52).Frame(196, first - 40, 92, 52);
        sheet.Text(60, first, 9, "Note:");
        sheet.Text(108, first, 9, "Left one").Text(108, first - 11, 9, "Left two").Text(204, first, 9, "Right one").Text(204, first - 11, 9, "Right two");
        var result = PdfTextExtractor.Extract(sheet.Pdf());
        Assert.Equal([.. left, "Note:", "Left one", "Left two", "Right one", "Right two", .. right], Texts(result));
        var ids = result.Pages[0].Regions.SelectMany(region => region.SourceTextIds).ToArray();
        Assert.Equal(sheet.Shows, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void Boxes_that_hold_the_labels_of_a_diagrams_nodes_are_not_columns_of_text()
    {
        // Three boxes with a label each (the third is joined to nothing), and a frame of text beside them.
        void Text(Sheet sheet)
        {
            sheet.Text(38, 628, 10, "Receipt service").Text(198, 628, 10, "Approval workflow").Text(208, 556, 10, "Archive");
            Paragraph(sheet, 358, 670, 10, 12, 204, RightSentence);
        }
        Sheet Boxes(Sheet sheet) => sheet.Frame(30, 600, 120, 60).Frame(190, 600, 120, 60).Frame(200, 535, 100, 50).Frame(350, 470, 220, 220);
        var plain = ReadPlain(Text);
        // Nothing joins the boxes: there is no diagram, and they are boxes of text.
        Assert.NotEqual(plain, ReadDrawn(sheet => Boxes(sheet), Text));
        // An arrow from the first to the second makes a diagram of them. Its nodes - the third as well - are no columns
        // of text: the page's diagram takes their labels out of the text, and the frame of text beside them is read as it was.
        Assert.Equal(plain, ReadDrawn(sheet => Boxes(sheet).Rule(152, 630, 188, 630).Add("188 630 m 180 636 l 180 624 l h f"), Text));
    }

    [Fact]
    public void A_grid_of_cards_of_three_lines_is_read_card_by_card_and_row_by_row()
    {
        // Four cards: a heading and a paragraph of two lines in each, the middle line in the middle of the box.
        var paragraph = Wrap("The text of this card goes on to a second line which is shorter than the first one", 10, 224);
        Assert.Equal(2, paragraph.Count);
        string[] names = ["Card A", "Card B", "Card C", "Card D"];
        var expected = new List<string>();
        var sheet = new Sheet();
        for (var card = 0; card < 4; card++)
        {
            double x = card % 2 == 0 ? 50 : 310, middle = card < 2 ? 680 : 600;
            sheet.Frame(x, middle - 24.5, 240, 56);
            sheet.Text(x + 8, middle + 12, 10, names[card]).Text(x + 8, middle, 10, paragraph[0]).Text(x + 8, middle - 12, 10, paragraph[1]);
            expected.AddRange([names[card], paragraph[0] + " " + paragraph[1]]);
        }
        Assert.Equal(expected, Texts(sheet));
    }

    // --- Text in a fixed-pitch font

    [Fact]
    public void Code_with_comments_aligned_in_a_frame_is_not_cut_into_a_block_of_code_and_a_block_of_comments()
    {
        // Three lines of code and comments beside the first two: the page's own detection of columns does not take it, and
        // lines in step with each other would be rows of labels and values, so what reads them apart is the channel of white space.
        string[] code = ["x = compute(alpha, beta);", "yy = other(gamma, delta);", "zzz = last(epsilon);"];
        string[] comments = ["// first comment of the code", "// second comment here"];
        void Fixed(Sheet sheet)
        {
            for (var line = 0; line < code.Length; line++) sheet.Mono(58, 640 - line * 12, 9, code[line]);
            for (var line = 0; line < comments.Length; line++) sheet.Mono(274, 640 - line * 12, 9, comments[line]);
        }
        void Proportional(Sheet sheet)
        {
            for (var line = 0; line < code.Length; line++) sheet.Text(58, 640 - line * 12, 9, code[line]);
            for (var line = 0; line < comments.Length; line++) sheet.Text(274, 640 - line * 12, 9, comments[line]);
        }
        // The same lines in a proportional font are two blocks of text, with a channel of white space between them.
        Assert.NotEqual(ReadPlain(Proportional), ReadDrawn(sheet => sheet.Frame(50, 590, 500, 70), Proportional));
        // In a fixed-pitch font they are the lines of a listing.
        Assert.Equal(ReadPlain(Fixed), ReadDrawn(sheet => sheet.Frame(50, 590, 500, 70), Fixed));
    }

    [Fact]
    public void Fixed_pitch_lines_in_boxes_are_read_a_box_at_a_time_but_never_joined()
    {
        List<string> Lines(string sentence)
        {
            var lines = new List<string>();
            foreach (var word in sentence.Split(' '))
            {
                if (lines.Count > 0 && lines[^1].Length + 1 + word.Length <= 36) lines[^1] += " " + word;
                else lines.Add(word);
            }
            return lines;
        }
        var (left, right) = (Lines(LeftSentence), Lines(RightSentence));
        var sheet = TwoFrames(new Sheet());
        for (var line = 0; line < left.Count; line++) sheet.Mono(58, 690 - line * 12, 10, left[line]);
        for (var line = 0; line < right.Count; line++) sheet.Mono(318, 690 - line * 12, 10, right[line]);
        Assert.Equal([.. left, .. right], Texts(sheet));
    }

    [Fact]
    public void Japanese_text_with_some_words_in_a_fixed_pitch_font_is_still_a_paragraph()
    {
        const string leftText = "取込みの flow と list と log を毎日確認し、結果を file の画面で見ることができるように整えておく。";
        const string rightText = "承認の item と unit と test は別の画面で確かめ、差戻しの理由は file の画面から入力して記録として残す。";
        var sheet = TwoFrames(new Sheet());
        var left = Wrap(leftText, 10, 224, mono: true);
        var right = Wrap(rightText, 10, 224, mono: true);
        Assert.True(left.Count >= 2 && right.Count >= 2);
        for (var line = 0; line < left.Count; line++) sheet.Text(58, 690 - line * 12, 10, left[line], monoLatin: true);
        for (var line = 0; line < right.Count; line++) sheet.Text(318, 690 - line * 12, 10, right[line], monoLatin: true);
        var texts = Texts(sheet);
        Assert.True(texts.Length == 2, string.Join(" | ", texts));
        Assert.Equal(leftText.Replace(" ", ""), texts[0].Replace(" ", ""));
        Assert.Equal(rightText.Replace(" ", ""), texts[1].Replace(" ", ""));
    }

    // --- Text whose width is only an estimate

    [Fact]
    public void Text_whose_width_is_not_known_is_read_a_box_at_a_time_but_its_lines_are_not_joined()
    {
        var sheet = new Sheet(measured: false);
        TwoFrames(sheet);
        TwoParagraphs(sheet);
        Assert.Equal([.. Wrap(LeftSentence, 10, 224), .. Wrap(RightSentence, 10, 224)], Texts(sheet));
    }

    // --- Blocks of text in one box

    [Fact]
    public void Two_blocks_in_a_frame_that_have_a_single_line_in_common_are_not_columns()
    {
        var left = Wrap(LeftSentence + " and then a few more words of the left block", 8, 220);
        var right = Wrap(RightSentence + " and then a few more words of the right block", 8, 220);
        Assert.True(left.Count >= 3 && right.Count >= 3);
        void Text(Sheet sheet)
        {
            // The left block's third line is the right block's first.
            for (var line = 0; line < 3; line++) sheet.Text(58, 690 - line * 10, 8, left[line]);
            for (var line = 0; line < 3; line++) sheet.Text(318, 670 - line * 10, 8, right[line]);
        }
        Assert.Equal(ReadPlain(Text), ReadDrawn(sheet => sheet.Frame(50, 630, 500, 80), Text));
    }

    [Fact]
    public void Two_blocks_in_a_frame_with_a_gutter_of_less_than_two_and_a_half_ems_are_not_columns()
    {
        var left = Wrap(LeftSentence + " and then a few more words of the left block", 10, 232);
        var right = Wrap(RightSentence + " and then a few more words of the right block", 10, 232);
        Assert.True(left.Count >= 3 && right.Count >= 3);
        var leftEdge = 58 + left.Max(line => Width(line, 10));
        Action<Sheet> Text(double gutter) => sheet =>
        {
            for (var line = 0; line < left.Count; line++) sheet.Text(58, 690 - line * 12, 10, left[line]);
            for (var line = 0; line < right.Count; line++) sheet.Text(leftEdge + gutter, 690 - line * 12, 10, right[line]);
        };
        // 15 points between the longest line of the left block and the right block, at a size of 10; and 40.
        Assert.Equal(ReadPlain(Text(15)), ReadDrawn(sheet => sheet.Frame(50, 480, 500, 230), Text(15)));
        Assert.NotEqual(ReadPlain(Text(40)), ReadDrawn(sheet => sheet.Frame(50, 480, 500, 230), Text(40)));
    }

    // --- Lines that wrap

    [Fact]
    public void A_line_set_under_another_closer_than_six_tenths_of_its_size_is_not_a_continuation()
    {
        var first = FullToTheEdge();
        var texts = RightColumn(sheet => sheet.Text(308, 680, 10, first).Text(308, 675, 10, "overlaps the line above."));
        Assert.Equal([first, "overlaps the line above."], texts);
    }

    [Fact]
    public void Lines_whose_size_has_drifted_a_tenth_at_a_time_from_the_first_are_not_all_joined()
    {
        string Full(double size) => FilledLine(size);
        var (a, b, c) = (Full(10), Full(10.9), Full(11.8));
        var texts = RightColumn(sheet => sheet.Text(308, 690, 10, a).Text(308, 678, 10.9, b).Text(308, 664, 11.8, c));
        Assert.Equal([a + " " + b, c], texts);
    }

    [Fact]
    public void A_line_is_not_joined_when_its_size_differs_from_the_line_above_it_by_more_than_a_tenth_though_it_matches_the_first()
    {
        // 10, 10.9 and 9.5: the third is within a tenth of the first line but not of the second.
        var (a, b, c) = (FilledLine(10), FilledLine(10.9), FilledLine(9.5));
        var texts = RightColumn(sheet => sheet.Text(308, 690, 10, a).Text(308, 678, 10.9, b).Text(308, 666, 9.5, c));
        Assert.Equal([a + " " + b, c], texts);
    }

    [Fact]
    public void The_edge_of_a_paragraph_is_where_its_second_line_starts()
    {
        // Each line starts 3.5 points in from the one above it: the third is 7 from the first, which is more than half an em, and
        // 3.5 from the second.
        var (a, b, c) = (FilledLine(10, 232), FilledLine(10, 228.5), FilledLine(10, 225));
        var texts = RightColumn(sheet => sheet.Text(308, 690, 10, a).Text(311.5, 678, 10, b).Text(315, 666, 10, c));
        Assert.Equal([a + " " + b + " " + c], texts);
    }

    [Fact]
    public void A_line_is_not_joined_when_the_gap_is_larger_than_the_spacing_the_column_keeps()
    {
        var first = FullToTheEdge();
        // The column keeps a spacing of 12 (two lines that are joined). 16 is a third more than that, which is under a line and a half.
        var texts = RightColumn(sheet =>
        {
            sheet.Text(308, 690, 10, first).Text(308, 678, 10, "and its second line.");
            sheet.Text(308, 650, 10, first).Text(308, 634, 10, "and a third that stands further down.");
        });
        Assert.Equal([first + " and its second line.", first, "and a third that stands further down."], texts);
    }

    [Fact]
    public void The_line_under_a_bullet_that_does_not_start_where_the_text_of_the_bullet_does_is_not_joined()
    {
        var longBullet = "• " + Wrap("The bullet text goes on and on across the whole of the column and further", 10, 232 - 10 - Width(" ", 10))[0];
        var start = 308 + 10 + Width(" ", 10);
        var texts = RightColumn(sheet => sheet.Text(308, 680, 10, longBullet).Text(start + 12, 668, 10, "starts further in."));
        Assert.Equal([longBullet, "starts further in."], texts);
    }

    [Theory]
    [InlineData(",")]
    [InlineData(";")]
    [InlineData("/")]
    [InlineData("、")]
    [InlineData("，")]
    [InlineData("；")]
    [InlineData("／")]
    [InlineData("・")]
    public void A_line_that_ends_in_a_mark_that_joins_but_stops_short_of_the_column_is_not_joined(string mark)
    {
        // The line reaches more than a third of the column's width and the next starts at the same edge, but "zeta" would have
        // fitted after it: the break was not forced, whatever the line ends in.
        var line = "Alpha beta gamma delta epsilon" + mark;
        var texts = RightColumn(sheet => sheet.Text(308, 680, 10, line).Text(308, 668, 10, "zeta eta theta."));
        Assert.Equal([line, "zeta eta theta."], texts);
    }

    [Theory]
    [InlineData("・Item after a middle dot")]
    [InlineData("※ A note")]
    [InlineData("◆ A diamond")]
    [InlineData("→ An arrow")]
    [InlineData("* An asterisk")]
    [InlineData("– A dash")]
    [InlineData("− A minus sign")]
    [InlineData("① A circled one")]
    [InlineData("❶ A dingbat one")]
    [InlineData("㉑ A circled twenty-one")]
    [InlineData("第3条 An article")]
    [InlineData("第十二条 An article")]
    [InlineData("(1) A number in parentheses")]
    [InlineData("（2） A number in full-width parentheses")]
    [InlineData("(a) A letter in parentheses")]
    [InlineData("12. A number and a full stop")]
    [InlineData("3) A number and a parenthesis")]
    [InlineData("4、A number and a comma")]
    [InlineData("1.2 A section number")]
    [InlineData("4.1.2 A section number")]
    [InlineData("a. A letter and a full stop")]
    [InlineData("B) A letter and a parenthesis")]
    public void A_line_that_starts_with_any_kind_of_marker_is_an_item_of_its_own(string line)
    {
        var first = FullToTheEdge();
        var texts = RightColumn(sheet => sheet.Text(308, 680, 10, first).Text(308, 668, 10, line));
        Assert.Equal([first, line], texts);
    }

    [Theory]
    [InlineData("100,000 yen in total.")]
    [InlineData("1st place finish.")]
    [InlineData("-5 degrees at night.")]
    [InlineData("e.g. the rest of it.")]
    [InlineData("2.5million is the figure.")]
    public void A_line_that_only_looks_like_a_marker_is_joined_to_the_line_above(string line)
    {
        var first = FullToTheEdge();
        var texts = RightColumn(sheet => sheet.Text(308, 680, 10, first).Text(308, 668, 10, line));
        Assert.Equal([first + " " + line], texts);
    }
}
