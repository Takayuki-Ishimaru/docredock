using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.MeasuredPdf;

namespace DocRedock.Tests.Pdf;

// A line of a box is joined to the one below it when the break after it was forced: the first word of the next line, with the
// space before it, would not have fitted after the line within the column (see PdfTextExtractor.BoxedColumns.cs). Where a
// column ends is known where its right edge is drawn; in a block that white space cuts out of a frame it is only known that it
// ends no sooner than the longest line. Lines that follow each other but fall short of that are not a paragraph, and neither
// are rows of labels and values, which pair up across a frame. Each test pins one rule: take it out and the test fails.
public sealed partial class PdfBoxedColumnReadingOrderTests
{
    // --- Boxes whose edges are drawn: two frames side by side, the left one holding a paragraph so that the group is arranged.
    // --- The text of the right one starts at 318 and ends at 542 at the most; the break test allows 14 points more.

    /// <summary>What the right box of two frames holds, as read, with <paramref name="draw"/> drawn in it.</summary>
    private static string[] RightBox(Action<Sheet> draw)
    {
        var sheet = TwoFrames(new Sheet());
        Paragraph(sheet, 58, 690, 10, 12, 224, LeftSentence);
        draw(sheet);
        return Texts(sheet).Skip(1).ToArray();
    }

    [Theory]
    [InlineData("extraordinarily", true)]
    [InlineData("so", false)]
    public void A_line_wraps_when_the_next_word_would_not_have_fitted_after_it_and_not_when_it_would(string word, bool joined)
    {
        // The line ends 40 points short of the end of the box's text. "extraordinarily" is 70 points wide and could not follow
        // it there; "so" is 11 wide and could.
        var first = FilledLine(10, 184, "the first line of the box has");
        var second = word + " follows it.";
        string[] expected = joined ? [first + " " + second] : [first, second];
        Assert.Equal(expected, RightBox(sheet => sheet.Text(318, 690, 10, first).Text(318, 678, 10, second)));
    }

    [Fact]
    public void The_space_before_the_next_word_counts_in_the_break_test()
    {
        // The box's text ends at 542 and the test allows 14 points more than it: 528. The line ends at 514.7: the next word,
        // "so", is 10.6 wide, so it would just have fitted but for the 2.8 of the space before it.
        var first = FilledLine(10, 197, "the first line of the box has", tolerance: 0.6);
        Assert.Equal([first + " so follows it."], RightBox(sheet => sheet.Text(318, 690, 10, first).Text(318, 678, 10, "so follows it.")));
    }

    [Fact]
    public void A_paragraph_in_a_box_whose_text_stops_a_little_short_of_its_right_edge_is_joined()
    {
        // The text is set 8 points from the left edge and wrapped 20 from the right one: a word that did not fit would have
        // fitted in the 12 points between, and the break was forced all the same.
        var sheet = TwoFrames(new Sheet());
        Paragraph(sheet, 58, 690, 10, 12, 224, LeftSentence);
        Paragraph(sheet, 318, 690, 10, 12, 212, RightSentence);
        Assert.Equal([LeftSentence, RightSentence], Texts(sheet));
    }

    [Fact]
    public void A_paragraph_whose_last_line_fills_the_box_is_joined_where_the_edge_of_the_box_is_drawn()
    {
        var first = FilledLine(10, 224, "the first line of the box has words enough to");
        var second = FilledLine(10, 224, "and the second line fills it as well with its own");
        Assert.Equal([first + " " + second], RightBox(sheet => sheet.Text(318, 690, 10, first).Text(318, 678, 10, second)));
    }

    [Fact]
    public void A_paragraph_is_joined_whole_or_left_as_it_is_and_never_half_joined()
    {
        // Three lines that each fill the box. The second starts with a capital letter after a line that closes nothing: an item of
        // its own, or the name of a company. Nothing says which, and none of the three is joined.
        var first = FilledLine(10, 224, "the first line of the box has words enough to");
        var second = FilledLine(10, 224, "Google announced that the second line has");
        const string third = "and so on until the end.";
        Assert.Equal([first, second, third], RightBox(sheet => sheet.Text(318, 690, 10, first).Text(318, 678, 10, second).Text(318, 666, 10, third)));
        // The first ends a sentence when it ends in a full stop, and a capital letter starts the next: a wrap, as any other.
        var stopped = FilledLine(10, 224, "the first line of the box has words enough to", ending: ".");
        Assert.Equal([stopped + " " + second + " " + third], RightBox(sheet => sheet.Text(318, 690, 10, stopped).Text(318, 678, 10, second).Text(318, 666, 10, third)));
    }

    [Theory]
    [InlineData(22, true)]
    [InlineData(17, false)]
    public void East_Asian_text_wraps_when_the_next_character_would_not_have_fitted(int characters, bool joined)
    {
        // Characters are an em wide: 22 of them end 4 points short of the end of the box's text, 17 end 54 short.
        var first = "日本語の文章がここで折り返されて次の行へ続く"[..characters];
        const string second = "さらに続く。";
        string[] expected = joined ? [first + second] : [first, second];
        Assert.Equal(expected, RightBox(sheet => sheet.Text(318, 690, 10, first).Text(318, 678, 10, second)));
    }

    [Theory]
    [InlineData("と、続く", true)]
    [InlineData("（注）続く", true)]
    [InlineData("と続く", false)]
    public void A_character_that_cannot_start_a_line_is_one_with_the_character_before_it_and_one_that_cannot_end_a_line_with_the_one_after(string second, bool joined)
    {
        // 19 characters from 320 end at 510, where the next character, 10 points wide, would have fitted (the box's text ends at 540)
        // but not the 20 of a character and the mark that has to stay with it.
        const string first = "日本語の文章がここで折り返されて次の行";
        string[] expected = joined ? [first + second] : [first, second];
        Assert.Equal(expected, RightBox(sheet => sheet.Text(320, 690, 10, first).Text(320, 678, 10, second)));
    }

    [Theory]
    [InlineData("well-known words follow.", false)]
    [InlineData("wellknown words follow.", true)]
    public void A_word_may_be_broken_after_a_hyphen_that_a_letter_follows(string second, bool joined)
    {
        // The line ends 50 points short of the end of the box's text. "well-" could have followed it there, "wellknown" could not.
        var first = FilledLine(10, 172, "the first line of the box has");
        string[] expected = joined ? [first + " " + second] : [first, second];
        Assert.Equal(expected, RightBox(sheet => sheet.Text(318, 690, 10, first).Text(318, 678, 10, second)));
    }

    [Fact]
    public void A_word_of_Latin_text_ends_where_East_Asian_text_begins()
    {
        // The line ends 40 points short of the end of the box's text. The next one starts with "API" and goes on in Japanese with no
        // space; the line may be broken after "API", which is 16 wide and would have fitted.
        const string first = "日本語の文章がここで折り返されて";
        const string second = "API仕様の説明が続く。";
        Assert.Equal([first, second], RightBox(sheet => sheet.Text(318, 690, 10, first).Text(318, 678, 10, second)));
    }

    [Fact]
    public void Two_shows_a_word_space_apart_are_two_words_though_the_text_has_no_space_between_them()
    {
        // The next line is drawn in two shows, "alpha" and "beta", 3 points apart. "alpha" would have fitted after the line, "alphabeta" not.
        var first = FilledLine(10, 172, "the first line of the box has");
        var texts = RightBox(sheet => sheet.Text(318, 690, 10, first).Text(318, 678, 10, "alpha").Text(318 + Width("alpha", 10) + 3, 678, 10, "beta gamma delta."));
        Assert.Equal([first, "alpha beta gamma delta."], texts);
    }

    [Fact]
    public void A_line_that_ends_in_text_given_from_outside_is_as_wide_as_it_is_drawn()
    {
        // The last word of the line is drawn as "WWWWWW" and given as "wording" (/ActualText), as a producer does for a ligature:
        // the font's widths do not go with that text, and its show is 57 points wide.
        var plain = FilledLine(10, 160, "the first line of the box has") + " ";
        var texts = RightBox(sheet => sheet.Text(318, 690, 10, plain).Actual(318 + LineWidth(plain, 10), 690, 10, "WWWWWW", "wording").Text(318, 678, 10, "then follows it."));
        var line = Assert.Single(texts);
        Assert.StartsWith(plain.TrimEnd(), line, StringComparison.Ordinal);
        Assert.EndsWith("wording then follows it.", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("文字欠落なし / ページ順保持 /", "ヘッダー・フッター混入を識別 / 画像参照を保持", " ", true)]
    [InlineData("文字欠落なし / ページ順保持 /", "ヘッダー / 画像参照を保持", " ", false)]
    [InlineData("文字欠落なし / ページ順保持", "ヘッダーフッター混入を識別画像参照を保持", "", false)]
    public void Where_a_break_falls_at_a_space_the_word_after_it_is_all_up_to_the_next_space(string first, string second, string separator, bool joined)
    {
        // Some producers fill a line with the words that spaces set apart and do not break inside them, East Asian text or not. The
        // first line ends 80 points short of the end of the box's text. A word of 14 characters could not follow it there, one of 4
        // could. With no space at the break (the third) it is the next character that counts.
        string[] expected = joined ? [first + separator + second] : [first, second];
        Assert.Equal(expected, RightBox(sheet => sheet.Text(318, 690, 10, first).Text(318, 678, 10, second)));
    }

    // --- Blocks that white space cuts out of one frame: their right edge is not drawn, except the last one's.

    private static readonly string[] Labels = ["Project name for the record", "Planned start date of work", "Owner department and team"];
    private static readonly string[] Values = ["Customer portal renewal phase two", "2026-04-01, subject to approval", "Sales planning office, team B"];

    private static void KeyValueRows(Sheet sheet, string[] values, double[]? valueBaselines = null)
    {
        for (var row = 0; row < 3; row++)
        {
            sheet.Text(58, 640 - row * 16.5, 10.5, Labels[row]);
            sheet.Text(245, valueBaselines?[row] ?? 640 - row * 16.5, 10.5, values[row]);
        }
    }

    [Fact]
    public void Rows_of_labels_and_values_in_one_frame_are_not_paragraphs_and_read_as_they_did()
    {
        // Each label starts with a capital letter and the longest of them is taken to end the column, so the labels look as if
        // they wrapped; the first value is long and the second starts with a word that does not fit after it, so these look as
        // if they wrapped too. The frame has the same number of lines on each side, on the same baselines.
        var plain = ReadPlain(sheet => KeyValueRows(sheet, Values));
        var framed = ReadDrawn(sheet => sheet.Frame(50, 590, 405, 70), sheet => KeyValueRows(sheet, Values));
        Assert.Equal(plain, framed);
        Assert.All(Labels.Concat(Values), text => Assert.Contains(text, framed));
    }

    [Fact]
    public void Labels_that_look_as_if_they_wrapped_are_not_joined_when_the_frame_is_read_by_columns()
    {
        // The last value is a baseline lower than its label: not rows, and the frame is read a column at a time.
        string[] values = ["Portal renewal, phase two", "April 2026, with approval", "Planning office, team B"];
        double[] baselines = [640, 623.5, 595];
        var framed = ReadDrawn(sheet => sheet.Frame(50, 590, 405, 70), sheet => KeyValueRows(sheet, values, baselines));
        Assert.Equal([.. Labels, .. values], framed);
        Assert.NotEqual(framed, ReadPlain(sheet => KeyValueRows(sheet, values, baselines)));
    }

    [Fact]
    public void Two_paragraphs_side_by_side_in_one_frame_are_each_joined_whole_though_the_left_one_ends_short_of_the_channel()
    {
        const string left = "The left paragraph explains the purpose of the review in several sentences that wrap over more than one line in this column.";
        const string right = "The right paragraph lists the criteria that must be met before the release, also wrapping over several lines in its column.";
        void Text(Sheet sheet)
        {
            Paragraph(sheet, 58, 590, 10.5, 12, 180, left);
            Paragraph(sheet, 268, 590, 10.5, 12, 179, right);
        }
        Assert.NotEqual([left, right], ReadPlain(Text));
        Assert.Equal([left, right], ReadDrawn(sheet => sheet.Frame(50, 520, 405, 90), Text));
    }

    [Fact]
    public void Two_lists_of_bullets_in_one_frame_are_read_a_list_at_a_time_though_they_have_as_many_items()
    {
        string[] left = ["• First item of the list on the left", "• Second item of the list on the left"];
        string[] right = ["• First item of the list on the right", "• Second item of the list on the right"];
        void Text(Sheet sheet)
        {
            sheet.Text(58, 640, 10, left[0]).Text(58, 626, 10, left[1]);
            sheet.Text(268, 640, 10, right[0]).Text(268, 626, 10, right[1]);
        }
        Assert.NotEqual([.. left, .. right], ReadPlain(Text));
        Assert.Equal([.. left, .. right], ReadDrawn(sheet => sheet.Frame(50, 610, 450, 50), Text));
    }

    [Fact]
    public void Lines_that_all_end_where_the_longest_does_in_a_block_with_no_edge_are_not_a_paragraph()
    {
        // Labels of nine characters each, with a comma in them: each ends where the longest does, so each break looks forced, and
        // the last does not fall short of the longest. The right block is a paragraph of two lines, so the frame is no rows.
        string[] labels = ["氏名、住所を書く欄", "部署、役職を書く欄", "日時、場所を書く欄"];
        void Text(Sheet sheet)
        {
            for (var row = 0; row < 3; row++) sheet.Text(58, 640 - row * 14, 10, labels[row]);
            Paragraph(sheet, 175, 640, 10, 14, 365, RightSentence);
        }
        Assert.Equal([.. labels, RightSentence], ReadDrawn(sheet => sheet.Frame(50, 590, 500, 70), Text));
    }

    [Fact]
    public void A_line_of_a_block_with_no_edge_that_ends_well_short_of_the_longest_is_not_wrapped()
    {
        // The second line ends 25 points short of the first, which is the longest of the block: "so" would have fitted there.
        var first = FilledLine(10, 190, "a block of text that has just enough", tolerance: 3);
        var second = FilledLine(10, LineWidth(first, 10) - 25, "a second line that is", tolerance: 2);
        const string third = "so on and on";
        const string right = RightSentence + " and then a few more words that keep the right block going over lines and lines until it reaches a fifth line of text in the frame";
        Assert.True(Wrap(right, 10, 240).Count >= 4);
        void Text(Sheet sheet)
        {
            sheet.Text(58, 640, 10, first).Text(58, 628, 10, second).Text(58, 616, 10, third);
            Paragraph(sheet, 300, 640, 10, 12, 240, right);
        }
        Assert.Equal([first, second, third, right], ReadDrawn(sheet => sheet.Frame(50, 560, 500, 100), Text));
    }

    [Fact]
    public void The_last_block_of_a_frame_ends_at_the_frames_edge_and_its_two_lines_are_a_paragraph()
    {
        // The left block is a paragraph of four lines. The right one has two, with no full stop, the first running to the end
        // of the text of the frame (447).
        const string left = "The left paragraph explains the purpose of the review in several sentences that wrap over more than one line in this column.";
        var first = FilledLine(10.5, 179, "the right block has two lines that", tolerance: 3);
        const string second = "so they are one paragraph";
        void Text(Sheet sheet)
        {
            Paragraph(sheet, 58, 590, 10.5, 12, 180, left);
            sheet.Text(268, 590, 10.5, first).Text(268, 578, 10.5, second);
        }
        Assert.Equal([left, first + " " + second], ReadDrawn(sheet => sheet.Frame(50, 520, 405, 90), Text));
    }

    [Fact]
    public void Boxes_side_by_side_with_as_many_short_lines_are_read_a_box_at_a_time()
    {
        string[] left = ["First short line", "Second short line"];
        string[] right = ["Third short line", "Fourth short line"];
        void Text(Sheet sheet)
        {
            for (var row = 0; row < 2; row++) sheet.Text(58, 690 - row * 12, 10, left[row]).Text(318, 690 - row * 12, 10, right[row]);
        }
        Assert.NotEqual([.. left, .. right], ReadPlain(Text));
        Assert.Equal([.. left, .. right], ReadDrawn(sheet => TwoFrames(sheet), Text));
    }

    [Fact]
    public void The_line_under_a_bullet_that_falls_short_of_the_longest_is_the_rest_of_its_item_in_a_block_with_no_edge()
    {
        // The second bullet wraps: "side" would not have fitted after its first line, in a block that ends where the first bullet
        // does. A bullet starts an item, and the line under it is the rest of it, with no full stop to show it.
        string[] left = ["• First item of the list on the left side", "• Second item of the list on the left"];
        void Text(Sheet sheet)
        {
            sheet.Text(58, 640, 10, left[0]).Text(58, 628, 10, left[1]).Text(58, 616, 10, "side");
            Paragraph(sheet, 300, 640, 10, 12, 240, RightSentence);
        }
        Assert.Equal([left[0], left[1] + " side", RightSentence], ReadDrawn(sheet => sheet.Frame(50, 590, 500, 70), Text));
    }

    [Fact]
    public void Three_lines_with_a_comma_in_them_are_a_paragraph_in_a_block_with_no_edge_though_no_sentence_ends()
    {
        // Two lines of thirteen characters and a third of nine, with a Japanese comma in the first and the third.
        string[] lines = ["請求書や領収書の画像、およ", "び関連する表の内容を読み取", "り、結果を確認する"];
        void Text(Sheet sheet)
        {
            for (var row = 0; row < 3; row++) sheet.Text(58, 640 - row * 14, 10, lines[row]);
            Paragraph(sheet, 250, 640, 10, 14, 290, RightSentence);
        }
        Assert.Equal([string.Concat(lines), RightSentence], ReadDrawn(sheet => sheet.Frame(50, 590, 500, 70), Text));
    }

    [Fact]
    public void Lines_of_one_length_with_no_punctuation_are_not_a_paragraph_in_a_block_with_no_edge()
    {
        // Three names and titles of nine characters and a fourth of eight: each of the first three ends where the longest does, and
        // the last falls short of it. There is no comma and no full stop in them, and no capital letter to tell them apart.
        string[] items = ["営業企画部長の山田", "経理総務課員の鈴木", "開発推進室長の佐藤", "品質保証部の高橋"];
        void Text(Sheet sheet)
        {
            for (var row = 0; row < 4; row++) sheet.Text(58, 640 - row * 14, 10, items[row]);
            Paragraph(sheet, 175, 640, 10, 14, 365, RightSentence);
        }
        Assert.Equal([.. items, RightSentence], ReadDrawn(sheet => sheet.Frame(50, 570, 500, 100), Text));
    }

    [Theory]
    [InlineData("two lines of text.", true)]
    [InlineData("two lines of text", false)]
    public void Two_lines_in_a_block_with_no_edge_are_a_paragraph_only_when_they_end_a_sentence(string second, bool joined)
    {
        // The first line is the longest of the block and so ends in a break that looks forced whatever follows it.
        const string first = "a block of text that has just";
        const string right = RightSentence + " and then a few more words that keep the right block going over lines";
        void Text(Sheet sheet)
        {
            sheet.Text(58, 640, 10, first).Text(58, 628, 10, second);
            Paragraph(sheet, 250, 640, 10, 12, 290, right);
        }
        string[] expected = joined ? [first + " " + second, right] : [first, second, right];
        Assert.Equal(expected, ReadDrawn(sheet => sheet.Frame(50, 560, 500, 100), Text));
    }
}
