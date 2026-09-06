using System.Text;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;

namespace DocRedock.Tests.Pdf;

/// <summary>Regression coverage for F-03. A page with two independent text columns at shared
/// baselines used to be exported line-by-line ("LEFT_1 RIGHT_1", "LEFT_2 RIGHT_2", ...) instead of
/// column by column, because <c>SortReadingOrder</c> only ever grouped fragments into baselines and
/// merged left-to-right without checking for a persistent vertical gutter.</summary>
public sealed class PdfColumnReadingOrderTests
{
    [Fact]
    public void Table_overlapping_columns_does_not_split_a_body_line_or_duplicate_its_fragments()
    {
        var tableText = string.Join("\n", Enumerable.Range(0, 3).SelectMany(row =>
            Enumerable.Range(0, 2).Select(column => Line(210 + column * 100, 615 + row * 40, $"R{row}C{column}"))));
        var grid = string.Join("\n", Enumerable.Range(0, 4).Select(i => $"200 {600 + i * 40} m 400 {600 + i * 40} l S")
            .Concat(Enumerable.Range(0, 3).Select(i => $"{200 + i * 100} 600 m {200 + i * 100} 720 l S")));
        var body = string.Join("\n", Enumerable.Range(0, 3).SelectMany(i => new[]
        {
            Line(20, 695 - i * 40, $"LEFT_{i}_A"), Line(75, 695 - i * 40, $"LEFT_{i}_B"),
            Line(500, 695 - i * 40, $"RIGHT_{i}")
        }));

        var result = PdfTextExtractor.Extract(Page(tableText + "\n" + grid + "\n" + body));

        Assert.Equal(2, result.Pages[0].ColumnCount);
        Assert.Single(result.Tables![1]);
        Assert.Equal(new[] { "LEFT_0_ALEFT_0_B", "LEFT_1_ALEFT_1_B", "LEFT_2_ALEFT_2_B", "RIGHT_0", "RIGHT_1", "RIGHT_2" },
            Paragraphs(Project(result)));
        var ids = result.Pages[0].Regions.SelectMany(region => region.SourceTextIds).ToArray();
        Assert.Equal(15, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.True(PdfTextAccounting.Reconcile(result.Pages[0], result.Tables[1]).IsComplete);
    }

    private static byte[] Page(string content) => Encoding.Latin1.GetBytes(
        "%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length " +
        content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        " >> stream\n" + content + "\nendstream\n%%EOF");

    private static string Line(int x, int y, string text) => $"BT 1 0 0 1 {x} {y} Tm ({text}) Tj ET";

    /// <summary>Builds a two-column block: <paramref name="left"/> lines at <paramref name="leftX"/>
    /// and <paramref name="right"/> lines at <paramref name="rightX"/>, sharing a baseline whenever
    /// both columns still have a line left (allows ragged column heights).</summary>
    private static IEnumerable<string> TwoColumnLines(IReadOnlyList<string> left, IReadOnlyList<string> right,
        int leftX = 100, int rightX = 400, int topY = 700, int lineHeight = 20)
    {
        var rows = Math.Max(left.Count, right.Count);
        for (var i = 0; i < rows; i++)
        {
            var y = topY - i * lineHeight;
            if (i < left.Count) yield return Line(leftX, y, left[i]);
            if (i < right.Count) yield return Line(rightX, y, right[i]);
        }
    }

    private static int IndexOfText(string haystack, string needle)
    {
        // F-Issue7: readable Markdown now backslash-escapes a literal "_" (e.g. "LEFT_1" ->
        // "LEFT\_1") so it can never be read as emphasis. Strip escaping backslashes before
        // searching; only relative ordering between matches is asserted, so the index space
        // just needs to be consistent, not identical to the original string's offsets.
        var normalizedHaystack = haystack.Replace("\\", string.Empty, StringComparison.Ordinal);
        var index = normalizedHaystack.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"expected to find \"{needle}\" in: {haystack}");
        return index;
    }

    [Fact]
    public void Two_columns_are_read_column_by_column_not_interleaved_by_baseline()
    {
        var left = new[] { "LEFT_1", "LEFT_2", "LEFT_3", "LEFT_4" };
        var right = new[] { "RIGHT_1", "RIGHT_2", "RIGHT_3", "RIGHT_4" };
        var result = PdfTextExtractor.Extract(Page(string.Join("\n", TwoColumnLines(left, right))));

        Assert.Equal(2, result.Pages[0].ColumnCount);
        var text = result.Text;
        var lastLeft = left.Max(term => IndexOfText(text, term));
        var firstRight = right.Min(term => IndexOfText(text, term));
        Assert.True(lastLeft < firstRight, $"expected every LEFT_* before every RIGHT_* in: {text}");
        Assert.DoesNotContain(text.Split('\n'), line => line.Contains("LEFT_", StringComparison.Ordinal) && line.Contains("RIGHT_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_columns_reach_readable_markdown_export_in_column_major_order()
    {
        var left = new[] { "LEFT_1", "LEFT_2", "LEFT_3", "LEFT_4" };
        var right = new[] { "RIGHT_1", "RIGHT_2", "RIGHT_3", "RIGHT_4" };
        var pdf = Page(string.Join("\n", TwoColumnLines(left, right)));
        var root = Path.Combine(Path.GetTempPath(), "docredock-pdf-columns-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "columns.pdf");
            var output = Path.Combine(root, "columns.md");
            await File.WriteAllBytesAsync(source, pdf);

            var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, output));

            var markdown = await File.ReadAllTextAsync(result.MarkdownPath);
            var lastLeft = left.Max(term => IndexOfText(markdown, term));
            var firstRight = right.Min(term => IndexOfText(markdown, term));
            Assert.True(lastLeft < firstRight, $"expected every LEFT_* before every RIGHT_* in markdown: {markdown}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Two_columns_between_a_full_width_title_and_footer_keep_the_title_first_and_footer_last()
    {
        var left = new[] { "LEFT_1", "LEFT_2", "LEFT_3", "LEFT_4" };
        var right = new[] { "RIGHT_1", "RIGHT_2", "RIGHT_3", "RIGHT_4" };
        var lines = new List<string> { Line(100, 760, "TITLE_LINE") };
        lines.AddRange(TwoColumnLines(left, right));
        lines.Add(Line(50, 600, "FOOTER_" + new string('X', 60)));
        var result = PdfTextExtractor.Extract(Page(string.Join("\n", lines)));

        Assert.Equal(2, result.Pages[0].ColumnCount);
        var text = result.Text;
        var title = IndexOfText(text, "TITLE_LINE");
        var footer = IndexOfText(text, "FOOTER_");
        var lastLeft = left.Max(term => IndexOfText(text, term));
        var firstRight = right.Min(term => IndexOfText(text, term));
        Assert.True(title < lastLeft, "title must come before the columns");
        Assert.True(lastLeft < firstRight, "every LEFT_* must come before every RIGHT_*");
        Assert.True(firstRight < footer, "footer must come after the columns");
    }

    [Fact]
    public void A_single_wide_gap_on_one_line_among_normal_lines_does_not_create_columns()
    {
        var lines = new List<string>
        {
            Line(100, 700, "This is a normal body line one."),
            Line(100, 680, "This is a normal body line two."),
            Line(100, 660, "Label"),
            Line(450, 660, "Value"),
            Line(100, 640, "This is a normal body line three."),
            Line(100, 620, "This is a normal body line four."),
        };
        var result = PdfTextExtractor.Extract(Page(string.Join("\n", lines)));

        Assert.Equal(1, result.Pages[0].ColumnCount);
        Assert.Contains(result.Pages[0].Regions, region => region.Text.Contains("Label", StringComparison.Ordinal) &&
            region.Text.Contains("Value", StringComparison.Ordinal));
    }

    [Fact]
    public void Widely_spaced_labels_on_one_flow_diagram_baseline_do_not_create_columns()
    {
        var lines = new List<string>
        {
            Line(100, 700, "Processing pipeline overview follows below."),
            Line(100, 660, "INPUT"),
            Line(220, 660, "EXTRACT"),
            Line(360, 660, "NORMALIZE"),
            Line(520, 660, "OUTPUT"),
            Line(100, 620, "Each stage hands its result to the next stage in turn."),
        };
        var result = PdfTextExtractor.Extract(Page(string.Join("\n", lines)));

        Assert.Equal(1, result.Pages[0].ColumnCount);
        Assert.Contains(result.Pages[0].Regions, region => region.Text.Contains("INPUT", StringComparison.Ordinal) &&
            region.Text.Contains("EXTRACT", StringComparison.Ordinal) &&
            region.Text.Contains("NORMALIZE", StringComparison.Ordinal) &&
            region.Text.Contains("OUTPUT", StringComparison.Ordinal));
    }

    [Fact]
    public void Ragged_columns_of_different_lengths_still_read_column_major()
    {
        var left = new[] { "LEFT_1", "LEFT_2", "LEFT_3" };
        var right = new[] { "RIGHT_1", "RIGHT_2", "RIGHT_3", "RIGHT_4", "RIGHT_5" };
        var result = PdfTextExtractor.Extract(Page(string.Join("\n", TwoColumnLines(left, right))));

        Assert.Equal(2, result.Pages[0].ColumnCount);
        var text = result.Text;
        var lastLeft = left.Max(term => IndexOfText(text, term));
        var firstRight = right.Min(term => IndexOfText(text, term));
        Assert.True(lastLeft < firstRight, $"expected every LEFT_* before every RIGHT_* in: {text}");
    }

    [Fact]
    public void Ragged_columns_still_read_column_major_when_the_left_column_is_the_longer_one()
    {
        var left = new[] { "LEFT_1", "LEFT_2", "LEFT_3", "LEFT_4", "LEFT_5" };
        var right = new[] { "RIGHT_1", "RIGHT_2", "RIGHT_3" };
        var result = PdfTextExtractor.Extract(Page(string.Join("\n", TwoColumnLines(left, right))));

        Assert.Equal(2, result.Pages[0].ColumnCount);
        var text = result.Text;
        var lastLeft = left.Max(term => IndexOfText(text, term));
        var firstRight = right.Min(term => IndexOfText(text, term));
        Assert.True(lastLeft < firstRight, $"expected every LEFT_* before every RIGHT_* in: {text}");
    }

    // --- F-01 interaction: a ruled table and a two-column block share the same page. ---

    private const string Sentinel = "NATIVE_TEXT_MUST_SURVIVE";

    private static string TitleText() => Line(100, 760, "TITLE_LINE");

    private static string BodyText(int x, int y) => Line(x, y, Sentinel);

    private static string GridText(int left, int bottom = 600) => string.Join("\n",
        Enumerable.Range(0, 3).SelectMany(row => Enumerable.Range(0, 3).Select(column =>
            Line(left + 10 + column * 100, bottom + 15 + row * 40, $"R{row + 1}C{column + 1}"))));

    private static string GridRules(int left, int bottom = 600) => string.Join("\n",
        Enumerable.Range(0, 4).Select(index => $"{left} {bottom + index * 40} m {left + 300} {bottom + index * 40} l S")
            .Concat(Enumerable.Range(0, 4).Select(index => $"{left + index * 100} {bottom} m {left + index * 100} {bottom + 120} l S")));

    private static IReadOnlyList<DocumentNode> Project(PdfExtractionResult result) =>
        PdfDocumentGraphProjection.CreateGraph(result, "0123456789abcdef0123456789abcdef")
            .Partitions!.SelectMany(partition => partition.Nodes).ToArray();

    private static IEnumerable<string> Paragraphs(IEnumerable<DocumentNode> nodes) => nodes
        .Where(node => node.Kind == NodeKind.Paragraph && node.Content is TextNodeContent)
        .Select(node => ((TextNodeContent)node.Content).Text);

    [Fact]
    public void A_ruled_table_and_a_two_column_block_on_the_same_page_both_read_correctly()
    {
        var left = new[] { "LEFT_1", "LEFT_2", "LEFT_3", "LEFT_4" };
        var right = new[] { "RIGHT_1", "RIGHT_2", "RIGHT_3", "RIGHT_4" };
        var content = string.Join("\n",
        [
            TitleText(),
            GridText(100),
            BodyText(100, 530),
            GridRules(100),
            .. TwoColumnLines(left, right, leftX: 100, rightX: 400, topY: 440, lineHeight: 20)
        ]);

        var result = PdfTextExtractor.Extract(Page(content));
        var nodes = Project(result);

        // The ruled table is still reconstructed and the body sentinel still survives exactly once.
        Assert.Single(result.Tables![1]);
        Assert.Equal(1, Paragraphs(nodes).Count(text => text.Contains(Sentinel, StringComparison.Ordinal)));
        Assert.Equal(1, Paragraphs(nodes).Count(text => text.Contains("TITLE_LINE", StringComparison.Ordinal)));
        Assert.DoesNotContain(Paragraphs(nodes), text => text.Contains("R2C2", StringComparison.Ordinal));

        // The two-column block below the table still reads column by column.
        Assert.True(result.Pages[0].ColumnCount >= 2);
        var text = result.Text;
        var lastLeft = left.Max(term => IndexOfText(text, term));
        var firstRight = right.Min(term => IndexOfText(text, term));
        Assert.True(lastLeft < firstRight, $"expected every LEFT_* before every RIGHT_* in: {text}");
    }

    // --- Columns set at different line pitches, and what closes a column block. ---

    /// <summary>Two columns whose line pitches differ, so their lines drift out of alignment: the
    /// left column advances 12pt per line, the right 15pt, and the right column's first line sits
    /// 2pt above the left's. Baselines are grouped with a vertical tolerance, so a left and a right
    /// line a few points apart still share one - and on such a baseline the right-column fragment
    /// can be the higher, and therefore the first, of the two in geometric (Y-major) order.</summary>
    private static IEnumerable<string> MixedPitchColumnLines(IReadOnlyList<string> left, IReadOnlyList<string> right,
        int leftX = 100, int rightX = 400, int topY = 700, int leftPitch = 12, int rightPitch = 15, int rightTopOffset = 2)
    {
        for (var index = 0; index < left.Count; index++) yield return Line(leftX, topY - index * leftPitch, left[index]);
        for (var index = 0; index < right.Count; index++)
            yield return Line(rightX, topY + rightTopOffset - index * rightPitch, right[index]);
    }

    private static readonly string[] MixedPitchLeft = ["LEFT_1", "LEFT_2", "LEFT_3", "LEFT_4", "LEFT_5"];
    private static readonly string[] MixedPitchRight = ["RIGHT_1", "RIGHT_2", "RIGHT_3", "RIGHT_4"];

    private static string[] ReadingOrderTexts(PdfExtractionResult result)
    {
        var regions = result.Pages[0].Regions.OrderBy(region => region.ReadingOrder).ToArray();
        // ReadingOrder stays a dense 0..n-1 sequence over the final emitted order.
        Assert.Equal(Enumerable.Range(0, regions.Length), regions.Select(region => region.ReadingOrder));
        return regions.Select(region => region.Text).ToArray();
    }

    [Fact]
    public void Columns_set_at_different_line_pitches_each_keep_their_own_line_order()
    {
        var result = PdfTextExtractor.Extract(Page(string.Join("\n", MixedPitchColumnLines(MixedPitchLeft, MixedPitchRight))));

        Assert.Equal(2, result.Pages[0].ColumnCount);
        Assert.Equal([.. MixedPitchLeft, .. MixedPitchRight], ReadingOrderTexts(result));
    }

    [Fact]
    public void A_short_footer_inside_the_left_columns_extent_is_read_after_both_columns()
    {
        // "Page 1" sits at the left column's own X, well below the block's 12pt line pitch. It used
        // to be absorbed as the left column's last line and therefore emitted before RIGHT_1.
        var content = MixedPitchColumnLines(MixedPitchLeft, MixedPitchRight).Append(Line(100, 560, "Page 1"));
        var result = PdfTextExtractor.Extract(Page(string.Join("\n", content)));

        Assert.Equal(2, result.Pages[0].ColumnCount);
        Assert.Equal([.. MixedPitchLeft, .. MixedPitchRight, "Page 1"], ReadingOrderTexts(result));
    }

    [Fact]
    public void A_full_width_footer_spanning_the_gutter_reaches_the_same_position()
    {
        // The pre-existing guard: a footer wide enough to straddle the gutter ends the block too,
        // and must land in exactly the same place as the short one above.
        var content = MixedPitchColumnLines(MixedPitchLeft, MixedPitchRight)
            .Append(Line(50, 640, "FOOTER_" + new string('X', 60)));
        var result = PdfTextExtractor.Extract(Page(string.Join("\n", content)));

        Assert.Equal(2, result.Pages[0].ColumnCount);
        Assert.Equal([.. MixedPitchLeft, .. MixedPitchRight, "FOOTER_" + new string('X', 60)], ReadingOrderTexts(result));
    }
}
