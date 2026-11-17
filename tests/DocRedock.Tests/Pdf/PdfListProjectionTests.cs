using System.Text;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.MeasuredPdf;

namespace DocRedock.Tests.Pdf;

/// <summary>Lists a PDF draws - with a marker typed as text or with a small shape drawn beside the line - reach the
/// graph as list items with their level and number, and short bold cells of a row are no headings.</summary>
public sealed class PdfListProjectionTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef";

    private static string Line(double x, double y, string text, double size = 10.5, string font = "F1") =>
        $"BT /{font} {N(size)} Tf 1 0 0 1 {N(x)} {N(y)} Tm ({Escaped(text)}) Tj ET";

    private static string Escaped(string text) => text.Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);

    /// <summary>A filled square of 3.8 points whose middle is level with the middle of the lower-case letters of a line.</summary>
    private static string Bullet(double x, double baseline, double size = 10.5) =>
        $"{N(x)} {N(baseline + size * 0.2)} 3.8 3.8 re f";

    private static string Prose(string seed) => seed + " keeps running text long enough to count as the body of the page";

    private static IReadOnlyList<DocumentNode> Nodes(byte[] pdf) =>
        PdfDocumentGraphProjection.CreateGraph(PdfTextExtractor.Extract(pdf), Hash).Partitions!.SelectMany(partition => partition.Nodes)
            .Where(node => node.Content is TextNodeContent).ToArray();

    private static string TextOf(DocumentNode node) => ((TextNodeContent)node.Content!).Text;

    private static int Level(DocumentNode node) => node.Extensions!["list_level"].GetInt32();

    private static bool IsOrdered(DocumentNode node) => node.Extensions!.TryGetValue("list_format", out var format) && format.GetString() == "ordered";

    // A font (F3) whose code 1 is a bullet and code 2 a Japanese middle dot, as a word processor's bullet font is, an em wide each.
    private static readonly Dictionary<int, string> Objects = new()
    {
        [4] = SimpleFont,
        [5] = "<< /Type /Font /Subtype /Type0 /BaseFont /Symbols /Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 7 0 R >>",
        [6] = "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Symbols /DW 1000 >>",
        [7] = TwoByteCMap((1, '•'), (2, '・')),
    };

    private const string Fonts = "<< /Font << /F1 4 0 R /F3 5 0 R >> >>";

    /// <summary>A line whose marker is the glyph <paramref name="code"/> of the bullet font, a space after it, and then the text.</summary>
    private static string GlyphLine(double x, double y, int code, string text, double size = 10.5) =>
        $"BT /F3 {N(size)} Tf 1 0 0 1 {N(x)} {N(y)} Tm <{code:X4}> Tj ET\n{Line(x + size + 3, y, text, size)}";

    private static byte[] Document(params string[] commands) => Page(string.Join("\n", commands), Fonts, Objects);

    [Fact]
    public void Numbered_steps_become_ordered_list_items_without_their_numbers_in_the_text()
    {
        var nodes = Nodes(Document(Line(72, 700, Prose("Before the steps the page")),
            Line(72, 670, "1. Check the work plan"), Line(72, 650, "2. Submit the record"), Line(72, 630, "3. Accept the inquiry"),
            Line(72, 600, Prose("After the steps the page"))));
        var items = nodes.Where(node => node.Kind == NodeKind.ListItem).ToArray();
        Assert.Equal(["Check the work plan", "Submit the record", "Accept the inquiry"], items.Select(TextOf));
        Assert.All(items, item => Assert.True(IsOrdered(item)));
        Assert.Equal([1, 2, 3], items.Select(item => item.Extensions!["list_number"].GetInt32()));
        Assert.All(items, item => Assert.Equal(0, Level(item)));
        Assert.Equal(2, nodes.Count(node => node.Kind == NodeKind.Paragraph));
    }

    [Theory]
    [InlineData("1) First", "2) Second")]
    [InlineData("(1) First", "(2) Second")]
    [InlineData("a. First", "b. Second")]
    [InlineData("A) First", "B) Second")]
    public void Other_numbering_styles_are_ordered_lists_too(string first, string second)
    {
        var items = Nodes(Document(Line(72, 700, first), Line(72, 680, second))).Where(node => node.Kind == NodeKind.ListItem).ToArray();
        Assert.Equal(["First", "Second"], items.Select(TextOf));
        Assert.All(items, item => Assert.True(IsOrdered(item)));
        Assert.Equal([1, 2], items.Select(item => item.Extensions!["list_number"].GetInt32()));
    }

    [Fact]
    public void Bullet_glyphs_make_unordered_items_and_one_alone_is_enough()
    {
        var items = Nodes(Document(Line(72, 700, "- A dash alone is prose"), Line(72, 660, "* Neither is a star alone"),
            GlyphLine(72, 600, 1, "Monitoring is reviewed monthly"), GlyphLine(72, 580, 1, "Backups are checked daily"))).ToArray();
        Assert.Equal(["Monitoring is reviewed monthly", "Backups are checked daily"], items.Where(node => node.Kind == NodeKind.ListItem).Select(TextOf));
        Assert.All(items.Where(node => node.Kind == NodeKind.ListItem), item => Assert.False(IsOrdered(item)));
        Assert.Equal(2, items.Count(node => node.Kind == NodeKind.Paragraph));
    }

    [Fact]
    public void Dashes_are_a_list_only_when_two_lines_in_a_row_start_with_one()
    {
        var items = Nodes(Document(Line(72, 700, "- First point"), Line(72, 680, "- Second point"))).Where(node => node.Kind == NodeKind.ListItem).ToArray();
        Assert.Equal(["First point", "Second point"], items.Select(TextOf));
    }

    [Fact]
    public void Shapes_drawn_beside_lines_are_their_bullets()
    {
        var nodes = Nodes(Document(Line(72, 700, Prose("Before the list the page")),
            Bullet(60, 670), Line(72, 670, "User accounts are inventoried"),
            Bullet(60, 650), Line(72, 650, "Backups get a double check"),
            Line(72, 610, Prose("After the list the page"))));
        Assert.Equal(["User accounts are inventoried", "Backups get a double check"], nodes.Where(node => node.Kind == NodeKind.ListItem).Select(TextOf));
        Assert.Equal(2, nodes.Count(node => node.Kind == NodeKind.Paragraph));
    }

    [Fact]
    public void A_lone_shape_beside_a_line_and_a_checkbox_are_no_bullets()
    {
        var alone = Nodes(Document(Bullet(60, 670), Line(72, 670, "A drawing mark beside a single line"), Line(72, 640, Prose("Running text"))));
        Assert.DoesNotContain(alone, node => node.Kind == NodeKind.ListItem);
        // A box as tall as the line is a check box of a form, not a bullet.
        var boxes = Nodes(Document("60 668 9 9 re S", Line(72, 670, "Agree to the terms"), "60 648 9 9 re S", Line(72, 650, "Subscribe to the news")));
        Assert.DoesNotContain(boxes, node => node.Kind == NodeKind.ListItem);
    }

    [Fact]
    public void Indented_items_are_nested_under_the_item_above_them()
    {
        var items = Nodes(Document(
            Line(72, 700, "1. Prepare the request"), Bullet(100, 680), Line(112, 680, "Attach the documents"),
            Bullet(100, 660), Line(112, 660, "Check the seal field"), Line(72, 640, "2. Submit the request"),
            Line(72, 620, "3. Wait for the answer"))).Where(node => node.Kind == NodeKind.ListItem).ToArray();
        Assert.Equal(["Prepare the request", "Attach the documents", "Check the seal field", "Submit the request", "Wait for the answer"], items.Select(TextOf));
        Assert.Equal([0, 1, 1, 0, 0], items.Select(Level));
        Assert.Equal([true, false, false, true, true], items.Select(IsOrdered));
        Assert.Equal([1, 2, 3], items.Where(IsOrdered).Select(item => item.Extensions!["list_number"].GetInt32()));
    }

    [Fact]
    public void A_line_wrapped_under_the_text_of_an_item_belongs_to_the_item()
    {
        var nodes = Nodes(Document(
            GlyphLine(72, 700, 1, "Receipts are accepted as PDF or image files of up to"), Line(86, 688, "ten megabytes each"),
            GlyphLine(72, 668, 1, "Amounts state their currency code"),
            Line(72, 630, Prose("The paragraph after the list"))));
        var items = nodes.Where(node => node.Kind == NodeKind.ListItem).ToArray();
        Assert.Equal(["Receipts are accepted as PDF or image files of up to ten megabytes each", "Amounts state their currency code"], items.Select(TextOf));
        Assert.Single(nodes, node => node.Kind == NodeKind.Paragraph);
    }

    [Fact]
    public void Numbers_in_running_text_and_section_numbers_are_no_list()
    {
        var nodes = Nodes(Document(
            Line(72, 700, Prose("The page")), Line(72, 680, "1. Preheat the oven before anything else is done to it"),
            Line(72, 640, Prose("The next paragraph")), Line(72, 600, "1.1 Overview of the method"),
            Line(72, 580, "2026. The year the work was done and nothing else"),
            Line(72, 540, "5. Another number far from the first"), Line(72, 500, Prose("The last paragraph"))));
        Assert.DoesNotContain(nodes, node => node.Kind == NodeKind.ListItem);
        Assert.Contains(nodes, node => TextOf(node) == "1. Preheat the oven before anything else is done to it");
        Assert.Contains(nodes, node => TextOf(node) == "1.1 Overview of the method");
    }

    [Fact]
    public void Numbers_that_do_not_follow_each_other_are_no_list()
    {
        var nodes = Nodes(Document(Line(72, 700, "1. First thing to do"), Line(72, 680, "7. Seventh thing to do")));
        Assert.DoesNotContain(nodes, node => node.Kind == NodeKind.ListItem);
    }

    [Fact]
    public void Short_bold_cells_of_a_row_are_no_headings_but_a_line_of_its_own_still_is()
    {
        var fonts = new Dictionary<int, string>
        {
            [4] = SimpleFont,
            [7] = SimpleFont.Replace("/BaseFont /Measured", "/BaseFont /Measured-Bold", StringComparison.Ordinal),
        };
        var pdf = Page(string.Join("\n",
                Line(72, 760, "Results", 10, "F2"),
                Line(72, 740, Prose("First paragraph of the page"), 10), Line(72, 726, Prose("Second paragraph of the page"), 10),
                Line(72, 700, "Item", 10, "F2"), Line(200, 700, "Description", 10, "F2"), Line(400, 700, "Owner", 10, "F2"),
                Line(72, 684, "Monitoring", 10), Line(200, 684, "Reviewed monthly", 10), Line(400, 684, "IT department", 10),
                Line(72, 668, "Backups", 10), Line(200, 668, "Checked daily", 10), Line(400, 668, "Operations", 10)),
            "<< /Font << /F1 4 0 R /F2 7 0 R >> >>", fonts);
        var page = PdfTextExtractor.Extract(pdf).Pages[0];
        Assert.Equal(3, PdfPageProjection.HeadingLevel(page, page.Regions.Single(region => region.Text == "Results")));
        foreach (var cell in new[] { "Item", "Description", "Owner" })
            Assert.Equal(0, PdfPageProjection.HeadingLevel(page, page.Regions.Single(region => region.Text == cell)));
        var headings = Nodes(pdf).Where(node => node.Kind == NodeKind.Heading).Select(TextOf).ToArray();
        Assert.Equal(["Results"], headings);
    }

    [Fact]
    public void A_recognised_table_set_smaller_than_the_body_does_not_lower_the_size_headings_are_judged_by()
    {
        var fonts = new Dictionary<int, string>
        {
            [4] = SimpleFont,
            [7] = SimpleFont.Replace("/BaseFont /Measured", "/BaseFont /Measured-Bold", StringComparison.Ordinal),
        };
        var commands = new List<string>
        {
            Line(50, 780, "Section heading", 14, "F2"),
            Line(50, 760, Prose("First body line of the page"), 10.5), Line(50, 745, Prose("Second body line of the page"), 10.5),
            Line(50, 730, Prose("Third body line of the page"), 10.5),
            Line(50, 700, "Sub heading", 11.5, "F2"),
            Line(50, 680, Prose("Body line under the sub heading"), 10.5),
            "50 400 400 100 re S", "50 433 m 450 433 l S", "50 466 m 450 466 l S", "183 400 m 183 500 l S", "316 400 m 316 500 l S",
        };
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                commands.Add(Line(55 + column * 133, 480 - row * 33, $"Cell text of row {row} column {column}", 9.5));
        var pdf = Page(string.Join("\n", commands), "<< /Font << /F1 4 0 R /F2 7 0 R >> >>", fonts);
        var result = PdfTextExtractor.Extract(pdf);
        Assert.Single(result.Tables![1]);
        var headings = PdfDocumentGraphProjection.CreateGraph(result, Hash).Partitions!.SelectMany(partition => partition.Nodes)
            .Where(node => node.Kind == NodeKind.Heading).ToDictionary(TextOf, node => node.Extensions!["heading_level"].GetInt32());
        Assert.Equal(2, headings["Section heading"]);
        Assert.Equal(3, headings["Sub heading"]);
    }

    [Fact]
    public void A_page_that_is_mostly_a_table_still_judges_its_heading_by_the_text_it_has()
    {
        // No body line outside the table: the page's size is then taken with the table's text, as before tables were
        // left out of it, instead of leaving the page without a body size and its heading without a level.
        var fonts = new Dictionary<int, string>
        {
            [4] = SimpleFont,
            [7] = SimpleFont.Replace("/BaseFont /Measured", "/BaseFont /Measured-Bold", StringComparison.Ordinal),
        };
        var commands = new List<string>
        {
            Line(50, 780, "Release schedule", 14, "F2"),
            "50 400 400 100 re S", "50 433 m 450 433 l S", "50 466 m 450 466 l S", "183 400 m 183 500 l S", "316 400 m 316 500 l S",
        };
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                commands.Add(Line(55 + column * 133, 480 - row * 33, $"Cell text of row {row} column {column}", 9.5));
        var pdf = Page(string.Join("\n", commands), "<< /Font << /F1 4 0 R /F2 7 0 R >> >>", fonts);
        var result = PdfTextExtractor.Extract(pdf);
        Assert.Single(result.Tables![1]);
        var heading = Assert.Single(PdfDocumentGraphProjection.CreateGraph(result, Hash).Partitions!.SelectMany(partition => partition.Nodes),
            node => node.Kind == NodeKind.Heading);
        Assert.Equal("Release schedule", TextOf(heading));
        Assert.Equal(2, heading.Extensions!["heading_level"].GetInt32());
    }

    [Theory]
    [InlineData("schedule-overlay.pdf")]
    [InlineData("schedule-marker-last.pdf")]
    public async Task The_title_above_a_page_wide_schedule_table_stays_a_heading(string name)
    {
        var source = FixturePath(Path.Combine("Evaluation", "V036"), name);
        var output = Path.Combine(Path.GetTempPath(), "docredock-pdf-heading-" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, output, Title: "Schedule"));
            var lines = (await File.ReadAllTextAsync(result.MarkdownPath)).Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
            Assert.Contains(lines, line => line.StartsWith('#') && line.EndsWith(" Release schedule", StringComparison.Ordinal));
        }
        finally { File.Delete(output); }
    }

    [Fact]
    public async Task The_benchmark_report_exports_its_bullets_numbers_and_nested_items_as_markdown_lists()
    {
        var source = FixturePath("Pdf", "list-benchmark-report.pdf");
        var output = Path.Combine(Path.GetTempPath(), "docredock-pdf-lists-" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, output, Title: "Lists"));
            var lines = (await File.ReadAllTextAsync(result.MarkdownPath)).Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
            Assert.Contains("- User accounts are inventoried on a regular basis", lines);
            Assert.Contains("- Notification mails have a clearly assigned owner", lines);
            Assert.Contains("  - Check the seal field for omissions", lines);
            Assert.Contains("1. Backups are capped at 90 entries", lines);
            Assert.Contains("4. Configuration files are capped at 180 entries", lines);
            Assert.DoesNotContain(lines, line => line.StartsWith("1\\. ", StringComparison.Ordinal));
            Assert.DoesNotContain("#### Item", lines);
        }
        finally { File.Delete(output); }
    }

    private static string FixturePath(string folder, string name)
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", folder, name);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException($"{name} was not found.");
    }
}
