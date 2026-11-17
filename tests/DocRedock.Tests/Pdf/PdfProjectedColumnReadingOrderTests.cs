using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;

namespace DocRedock.Tests.Pdf;

/// <summary>Columns whose lines do not share baselines, and whose text arrives a word at a time (as Chromium
/// prints it), were read line by line across the gutter: the half sentences of the two columns alternated. The
/// gutter is found from the strip of the page the lines of the columns leave free.</summary>
public sealed class PdfProjectedColumnReadingOrderTests
{
    private const double Size = 10;
    private const double ColumnWidth = 230;
    private const double LeftX = 50;
    private const double RightX = 315;

    private const string Abstract =
        "Under requirement R-223 the platform visualizes usage on a weekly basis within three minutes, which raises the " +
        "reliability of the data. Throughout the pilot period the external vendor detects early signs of failure, which " +
        "evens out waiting time for users and keeps the effort of manual checks low.";

    private const string LeftText =
        "In change record CR-903 the platform handles incoming requests in the order they arrive and records each decision " +
        "so that it can be traced later. When the load peaks the external vendor reconciles totals against the source data, " +
        "which evens out waiting time for users and lowers the risk of single person dependency.";

    private const string RightText =
        "Backups are verified every night and a failed run is reported to the owner before the next business day begins, " +
        "so that no data is lost. The monitoring agent keeps the change history in time order within twenty minutes, " +
        "which shortens the time to recovery after an incident.";

    [Fact]
    public void Columns_that_do_not_share_baselines_are_read_one_after_the_other_below_a_full_width_abstract()
    {
        var content = Page(withHeading: true, offsetRight: 6);

        var result = PdfTextExtractor.Extract(MeasuredPdf.Page(content, Resources));
        var text = Normalize(Texts(Project(result)));

        Assert.Equal(2, result.Pages[0].ColumnCount);
        Assert.True(text.Contains(Abstract, StringComparison.Ordinal), text);
        Assert.True(text.Contains(LeftText, StringComparison.Ordinal), text);
        Assert.True(text.Contains(RightText, StringComparison.Ordinal), text);
        Assert.True(text.IndexOf(Abstract, StringComparison.Ordinal) < text.IndexOf(LeftText, StringComparison.Ordinal));
        Assert.True(text.IndexOf(LeftText, StringComparison.Ordinal) < text.IndexOf(RightText, StringComparison.Ordinal));
        Assert.True(text.IndexOf(RightText, StringComparison.Ordinal) < text.IndexOf("Page 1 of 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Columns_at_one_line_pitch_with_a_page_number_between_them_are_read_one_after_the_other()
    {
        var content = Page(withHeading: false, offsetRight: 0);

        var result = PdfTextExtractor.Extract(MeasuredPdf.Page(content, Resources));
        var text = Normalize(Texts(Project(result)));

        Assert.True(text.Contains(LeftText, StringComparison.Ordinal), text);
        Assert.True(text.Contains(RightText, StringComparison.Ordinal), text);
        Assert.True(text.IndexOf(LeftText, StringComparison.Ordinal) < text.IndexOf(RightText, StringComparison.Ordinal));
    }

    [Fact]
    public void A_short_last_abstract_line_reaching_over_the_gutter_does_not_hide_the_columns()
    {
        // The last line of the abstract is far shorter than the others but still runs across the gutter.
        var words = Abstract.Split(' ');
        var abstractText = Abstract;
        for (var count = words.Length; count > 8; count--)
        {
            var candidate = string.Join(' ', words.Take(count));
            var width = MeasuredPdf.Width(Wrap(candidate, 495)[^1], Size);
            if (width is >= 245 and <= 265) { abstractText = candidate; break; }
        }
        Assert.NotEqual(Abstract, abstractText);
        var content = Page(withHeading: true, offsetRight: 6, abstractText);

        var result = PdfTextExtractor.Extract(MeasuredPdf.Page(content, Resources));
        var text = Normalize(Texts(Project(result)));

        Assert.True(text.Contains(abstractText, StringComparison.Ordinal), text);
        Assert.True(text.Contains(LeftText, StringComparison.Ordinal), text);
        Assert.True(text.Contains(RightText, StringComparison.Ordinal), text);
        Assert.True(text.IndexOf(LeftText, StringComparison.Ordinal) < text.IndexOf(RightText, StringComparison.Ordinal));
    }

    [Fact]
    public void A_page_number_close_under_the_columns_does_not_hide_the_gutter()
    {
        var content = Page(withHeading: true, offsetRight: 6, Abstract, footerGap: 30);

        var result = PdfTextExtractor.Extract(MeasuredPdf.Page(content, Resources));
        var text = Normalize(Texts(Project(result)));

        Assert.True(text.Contains(LeftText, StringComparison.Ordinal), text);
        Assert.True(text.Contains(RightText, StringComparison.Ordinal), text);
        Assert.True(text.IndexOf(LeftText, StringComparison.Ordinal) < text.IndexOf(RightText, StringComparison.Ordinal));
    }

    [Fact]
    public void A_single_column_of_text_is_left_as_it_was()
    {
        var lines = Wrap(LeftText + " " + RightText + " " + Abstract, 495);
        var content = string.Join("\n", lines.Select((line, index) => Words(LeftX, 700 - index * 14, line)));

        var result = PdfTextExtractor.Extract(MeasuredPdf.Page(content, Resources));

        Assert.Equal(1, result.Pages[0].ColumnCount);
        Assert.All(result.Pages[0].Regions, region => Assert.Null(region.ReadingColumn));
    }

    [Fact]
    public void Labels_beside_values_are_not_taken_for_columns()
    {
        var rows = new[] { "Name", "Address", "Phone", "Contact", "Region", "Role" };
        var values = new[]
        {
            "The platform handles incoming requests in the order they arrive",
            "Backups are verified every night and a failed run is reported",
            "The monitoring agent keeps the change history in time order",
            "Each decision is recorded so that it can be traced later on",
            "The external vendor reconciles totals against the source data",
            "The inventory console standardizes the working procedure daily",
        };
        var content = string.Join("\n", rows.Select((label, index) =>
            Words(LeftX, 700 - index * 20 + 5, label) + "\n" + Words(250, 700 - index * 20, values[index])));

        var result = PdfTextExtractor.Extract(MeasuredPdf.Page(content, Resources));

        Assert.Equal(1, result.Pages[0].ColumnCount);
    }

    [Fact]
    public void An_English_two_column_paper_reads_each_column_through_before_the_next()
    {
        var text = Normalize(Texts(Project(PdfTextExtractor.Extract(FixturePath("two-column-paper-en.pdf")))));

        string[] sentences =
        [
            "In change record CR-903, the platform handles up to 3 items per cycle and visualizes usage on a weekly basis, which shortens the time to recovery.",
            "During month-end closing, the integration module adjusts job priorities to current conditions, so that the basis of each decision can be traced later.",
            "When load peaks, the external vendor reconciles totals against the source data, which evens out waiting time for users.",
        ];
        Assert.All(sentences, sentence => Assert.Contains(sentence, text, StringComparison.Ordinal));
        // The abstract, then the left column down, then the right column.
        var order = new[] { "Throughout the pilot period", "In change record CR-903", "Ticket T-293 requires", "Ticket T-935 requires", "During month-end closing", "Ticket T-961 requires" }
            .Select(marker => text.IndexOf(marker, StringComparison.Ordinal)).ToArray();
        Assert.All(order, index => Assert.True(index >= 0));
        Assert.Equal(order.Order(), order);
    }

    [Fact]
    public void A_paper_whose_abstract_ends_in_a_short_line_over_the_gutter_still_reads_in_columns()
    {
        var text = Normalize(Texts(Project(PdfTextExtractor.Extract(FixturePath("two-column-paper-short-abstract-line-en.pdf")))));

        Assert.Contains("Ticket T-260 requires that, the intake service merges duplicate registrations automatically, which raises the reliability of the data. The batch pipeline validates configuration changes in advance, which raises the reliability of the data.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Japanese_two_column_paper_reads_each_column_through_before_the_next()
    {
        var text = Normalize(Texts(Project(PdfTextExtractor.Extract(FixturePath("two-column-paper-ja.pdf")))));

        string[] sentences =
        [
            "変更管理番号CR-228では、月末の締め処理では監視エージェントは、障害の兆候を早期に検知して、判断の根拠を後から追跡できるようにする。",
            "在庫管理画面は、12件を上限として利用状況を週次で可視化して、監査時の説明責任を果たせるようにすることを目的とする。",
        ];
        Assert.All(sentences, sentence => Assert.Contains(sentence, text.Replace(" ", string.Empty), StringComparison.Ordinal));
        var compact = text.Replace(" ", string.Empty);
        var order = new[] { "要件R-534", "変更管理番号CR-228", "変更管理番号CR-449", "要件R-944" }
            .Select(marker => compact.IndexOf(marker, StringComparison.Ordinal)).ToArray();
        Assert.All(order, index => Assert.True(index >= 0));
        Assert.Equal(order.Order(), order);
    }

    private const string Resources = "<< /Font << /F1 4 0 R >> >>";

    private static string Page(bool withHeading, double offsetRight, string abstractText = Abstract, double footerGap = 0)
    {
        var content = new List<string> { Words(LeftX, 780, "Anomaly Detection for Monitoring Data") };
        var top = 750.0;
        foreach (var line in Wrap(abstractText, 495)) { content.Add(Words(LeftX, top, line)); top -= 14; }
        top -= 20;
        var left = Wrap(LeftText, ColumnWidth);
        var leftTop = top;
        if (withHeading) { content.Add(Words(LeftX, leftTop, "Response Policy")); leftTop -= 30; }
        for (var index = 0; index < left.Count; index++) content.Add(Words(LeftX, leftTop - index * 14, left[index]));
        var right = Wrap(RightText, ColumnWidth);
        for (var index = 0; index < right.Count; index++) content.Add(Words(RightX, top + offsetRight - index * 14, right[index]));
        var bottom = Math.Min(leftTop - (left.Count - 1) * 14, top + offsetRight - (right.Count - 1) * 14);
        content.Add(Words(285, footerGap > 0 ? bottom - footerGap : 40, "Page 1 of 2"));
        return string.Join("\n", content);
    }

    /// <summary>A line drawn a word at a time, each word with the space that follows it.</summary>
    private static string Words(double x, double y, string line)
    {
        var shows = new List<string>();
        foreach (var word in line.Split(' '))
        {
            var shown = word + " ";
            shows.Add($"BT /F1 {Size} Tf 1 0 0 1 {MeasuredPdf.N(x)} {MeasuredPdf.N(y)} Tm ({shown}) Tj ET");
            x += MeasuredPdf.Width(shown, Size);
        }
        return string.Join("\n", shows);
    }

    private static List<string> Wrap(string text, double width)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in text.Split(' '))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && MeasuredPdf.Width(candidate, Size) > width) { lines.Add(current); current = word; }
            else current = candidate;
        }
        lines.Add(current);
        return lines;
    }

    private static IReadOnlyList<DocumentNode> Project(PdfExtractionResult result) =>
        PdfDocumentGraphProjection.CreateGraph(result, "0123456789abcdef0123456789abcdef")
            .Partitions!.SelectMany(partition => partition.Nodes).ToArray();

    private static string Texts(IEnumerable<DocumentNode> nodes) => string.Join("\n", nodes
        .Where(node => node.Content is TextNodeContent)
        .Select(node => ((TextNodeContent)node.Content).Text));

    private static string Normalize(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

    private static string FixturePath(string name)
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Pdf", name);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(name);
    }
}
