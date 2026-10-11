using System.Globalization;
using System.Text;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.MeasuredPdf;

namespace DocRedock.Tests.Api;

/// <summary>
/// Underlined text in a PDF reads as underlined (&lt;u&gt;), as struck-through text reads as struck through:
/// a line drawn under it (a stroked line, a filled bar, a double one, a dashed or dotted one drawn piece by
/// piece, a wavy one drawn as a curve or filled in a bar), Acrobat's Underline and Squiggly annotations, and
/// a Line or Ink annotation drawn across it. A line over part of a show marks the characters under it when
/// the font gives its glyph widths; otherwise an underline over part of a show is left out (it is emphasis,
/// not content, so nothing asks for a comparison). An underline that only styles a link is not emphasis, and
/// neither is a drawn rule under a row of cells that runs over the gaps between its columns. Strikethroughs
/// keep their rules: a hand-drawn line across text cancels it like a drawn one.
/// </summary>
public sealed class PdfUnderlineRecognitionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-pdf-underline-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);
    public void Dispose() => Directory.Delete(root, true);

    private async Task<(string Markdown, ReadableDocumentExportResult Result)> Export(byte[] pdf)
    {
        var source = Path.Combine(root, "page.pdf");
        await File.WriteAllBytesAsync(source, pdf);
        var output = Path.Combine(root, "page.md");
        var result = await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, output, EnableOcr: false));
        return ((await File.ReadAllTextAsync(output)).Replace("\r\n", "\n", StringComparison.Ordinal), result);
    }

    // One page: its text in Helvetica with the widths of its glyphs (measured) or in a font that gives none,
    // and the annotations, an object each.
    private static byte[] Document(bool measured, string content, string[] annotations)
    {
        var pdf = new StringBuilder("%PDF-1.4\n1 0 obj << /Type /Page /MediaBox [0 0 595 842]");
        if (measured) pdf.Append(" /Resources ").Append(Resources);
        pdf.Append(" /Contents 2 0 R");
        if (annotations.Length > 0)
            pdf.Append(" /Annots [").Append(string.Join(" ", annotations.Select((_, index) => $"{10 + index} 0 R"))).Append(']');
        pdf.Append(" >> endobj\n2 0 obj ").Append(Stream(content)).Append(" endobj\n");
        if (measured) pdf.Append("4 0 obj ").Append(SimpleFont).Append(" endobj\n");
        for (var index = 0; index < annotations.Length; index++)
            pdf.Append(10 + index).Append(" 0 obj ").Append(annotations[index]).Append(" endobj\n");
        return Encoding.Latin1.GetBytes(pdf.Append("%%EOF").ToString());
    }

    private static byte[] Measured(string content, params string[] annotations) => Document(true, content, annotations);

    private static byte[] Plain(string content, params string[] annotations) => Document(false, content, annotations);

    private static string Text(double x, double y, string text, double size = 12) =>
        $"BT /F1 {N(size)} Tf 1 0 0 1 {N(x)} {N(y)} Tm ({text}) Tj ET";

    // Where a part of a measured show, drawn from x, starts and ends.
    private static (double Left, double Right) Span(double x, string text, string part, double size = 12)
    {
        var left = x + Width(text[..text.IndexOf(part, StringComparison.Ordinal)], size);
        return (left, left + Width(part, size));
    }

    // Where a decoration lies relative to the baseline: an underline a tenth of an em under it, a
    // strikethrough three tenths over it.
    private static double Under(double baseline, double size = 12) => baseline - 0.1 * size;
    private static double Through(double baseline, double size = 12) => baseline + 0.3 * size;

    // A filled bar centred on y (Word and Chrome), and a stroked line (LibreOffice).
    private static string Bar(double left, double right, double y, double thickness = 0.7) =>
        $"{N(left)} {N(y - thickness / 2)} {N(right - left)} {N(thickness)} re f";

    private static string Rule(double left, double right, double y, double width = 0.7) =>
        $"{N(width)} w {N(left)} {N(y)} m {N(right)} {N(y)} l S";

    private static TextRange[] Whole(string text) => [new TextRange(0, text.Length)];

    private static PdfExtractionResult Extract(byte[] pdf) => PdfTextExtractor.Extract(pdf);

    private static IReadOnlyList<PdfTextRegion> Lines(byte[] pdf) => Extract(pdf).Pages[0].Regions;

    // Pages with tens of thousands of objects take the structure scans longer than a loaded machine allows by default.
    private static IReadOnlyList<PdfTextRegion> PatientLines(byte[] pdf) =>
        PdfTextExtractor.Extract(pdf, new PdfExtractionOptions(RegexTimeout: TimeSpan.FromMinutes(2))).Pages[0].Regions;

    // Decorations are formatting: none is left as a fallback path, an unresolved connector or a review item.
    private static void AssertNoDrawingLeft(PdfExtractionResult result)
    {
        Assert.True(result.VisualGraphs is null || !result.VisualGraphs.ContainsKey(1));
        Assert.DoesNotContain(result.Diagnostics ?? [], diagnostic => diagnostic.StartsWith("Visual", StringComparison.Ordinal));
        Assert.Null(result.Pages[0].UncertainText);
    }

    [Fact]
    public void Pdf_underline_over_a_whole_show_marks_it_and_never_becomes_a_drawing()
    {
        var result = Extract(Plain(string.Join("\n",
            Text(72, 720, "Old price"), Text(140, 720, "1000 yen"), "140 718.4 49.4 0.8 re f",   // Word: a filled bar
            Text(72, 690, "New price 1200 yen"),
            Text(72, 660, "Underlined item"), "0.8 w 72 658.8 m 160 658.8 l S")));               // a stroked line
        var regions = result.Pages[0].Regions;
        Assert.Equal(Whole("Underlined item"), regions.Single(region => region.Text == "Underlined item").Underlined);
        // Merged into one line with the show before it, the underlined show keeps its own characters.
        var line = regions.Single(region => region.Text.StartsWith("Old price", StringComparison.Ordinal));
        Assert.EndsWith("1000 yen", line.Text, StringComparison.Ordinal);
        Assert.Equal([new TextRange(line.Text.Length - "1000 yen".Length, line.Text.Length)], line.Underlined);
        Assert.Null(regions.Single(region => region.Text == "New price 1200 yen").Underlined);
        Assert.All(regions, region => Assert.Null(region.Struck));
        Assert.DoesNotContain(result.Diagnostics ?? [], diagnostic => diagnostic.StartsWith("PdfStrikethroughUnplaced", StringComparison.Ordinal));
        AssertNoDrawingLeft(result);
    }

    [Fact]
    public async Task Pdf_underlines_are_written_as_u_and_need_no_review()
    {
        const string first = "Keep the important word here.", second = "Old 1000 yen now 800 yen.";
        var important = Span(72, first, "important");
        var old = Span(72, second, "1000 yen");
        var current = Span(72, second, "800 yen");
        var (markdown, result) = await Export(Measured(string.Join("\n",
            Text(72, 720, first), Rule(important.Left, important.Right, Under(720)),
            Text(72, 690, second), Bar(old.Left, old.Right, Under(690)), Bar(old.Left, old.Right, Through(690)),
            Bar(current.Left, current.Right, Under(690)))));
        Assert.Contains("\nKeep the <u>important</u> word here.\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\nOld <u>~~1000 yen~~</u> now <u>800 yen</u>.\n", markdown, StringComparison.Ordinal);
        Assert.Equal(0, ExportSummaryBuilder.Build(result.Graph, result.Diagnostics).ReviewPages);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code.StartsWith("Visual", StringComparison.Ordinal));
        Assert.DoesNotContain("図の抽出結果", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_underline_over_part_of_a_measured_show_underlines_the_characters_under_it()
    {
        var (barLeft, barRight) = Span(72, "Old price 1000 yen", "1000 yen");
        var (lineLeft, lineRight) = Span(72, "Keep the word price here", "price");
        var result = Extract(Measured(string.Join("\n",
            Text(72, 720, "Old price 1000 yen"), Bar(barLeft, barRight, Under(720)),
            Text(72, 690, "Keep the word price here"), Rule(lineLeft, lineRight, Under(690)))));
        var regions = result.Pages[0].Regions;
        Assert.Equal([new TextRange(10, 18)], regions.Single(region => region.Text == "Old price 1000 yen").Underlined);
        Assert.Equal([new TextRange(14, 19)], regions.Single(region => region.Text == "Keep the word price here").Underlined);
        AssertNoDrawingLeft(result);
    }

    [Fact]
    public async Task Pdf_part_underlined_measured_text_is_written_underlined()
    {
        var (left, right) = Span(72, "Old price 1000 yen", "1000 yen");
        var (markdown, result) = await Export(Measured(Text(72, 720, "Old price 1000 yen") + "\n" + Bar(left, right, Under(720))));
        Assert.Contains("\nOld price <u>1000 yen</u>\n", markdown, StringComparison.Ordinal);
        Assert.Equal(0, ExportSummaryBuilder.Build(result.Graph, result.Diagnostics).ReviewPages);
    }

    [Fact]
    public async Task Pdf_underline_over_part_of_an_unmeasured_show_marks_nothing_and_asks_for_no_review()
    {
        // The font gives no glyph widths, so which characters the bar crosses cannot be told. A strikethrough there
        // is reported because the text it cancels is content; an underline is emphasis, so it is left out silently.
        var pdf = Plain(Text(72, 720, "Old price 1000 yen") + "\n125.4 718.4 49.4 0.8 re f");
        var result = Extract(pdf);
        Assert.Null(Assert.Single(result.Pages[0].Regions).Underlined);
        Assert.DoesNotContain(result.Diagnostics ?? [], diagnostic => diagnostic.StartsWith("PdfStrikethroughUnplaced", StringComparison.Ordinal));
        AssertNoDrawingLeft(result);
        var (markdown, exported) = await Export(pdf);
        Assert.Contains("\nOld price 1000 yen\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<u>", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("照合", markdown, StringComparison.Ordinal);
        Assert.Equal(0, ExportSummaryBuilder.Build(exported.Graph, exported.Diagnostics).ReviewPages);
        Assert.Empty(ExportReviewBuilder.Build(exported.Graph, exported.Diagnostics).Pages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pdf_double_underlines_mark_the_text_once(bool measured)
    {
        const string first = "Double underlined", second = "Word double";
        // LibreOffice: two stroked lines 1.5 apart, as thin as one. Word: two filled bars.
        var content = string.Join("\n", Text(72, 720, first),
            Rule(72, 72 + Width(first, 12), 719.3, 0.5), Rule(72, 72 + Width(first, 12), 717.8, 0.5),
            Text(72, 690, second), Bar(72, 72 + Width(second, 12), 688.8, 0.5), Bar(72, 72 + Width(second, 12), 687.4, 0.5));
        var result = Extract(measured ? Measured(content) : Plain(content));
        var regions = result.Pages[0].Regions;
        Assert.Equal(Whole(first), regions.Single(region => region.Text == first).Underlined);
        Assert.Equal(Whole(second), regions.Single(region => region.Text == second).Underlined);
        Assert.All(regions, region => Assert.Null(region.Struck));
        AssertNoDrawingLeft(result);
    }

    // The pieces of a dashed or dotted underline, drawn one by one from x for the width of the text. The
    // sizes are those of text of 12 points, times scale for text of another size.
    private static string Pieces(string style, double x, double width, double y, double scale = 1)
    {
        var pieces = new StringBuilder();
        var (dash, thick, period, dot) = (4.5 * scale, 1.2 * scale, 6.9 * scale, 3 * scale);
        switch (style)
        {
            case "bars":   // dashes at least four times as long as thick
                for (var left = x; left + 4 * scale <= x + width + scale; left += 7 * scale) pieces.AppendLine(Bar(left, left + 4 * scale, y, 0.8 * scale));
                break;
            case "chrome dashes":   // 4.5 by 1.2: too short to be bars, and drawn as one compound path
                for (var left = x; left + dash <= x + width + scale; left += period)
                    pieces.AppendLine($"{N(left)} {N(y - thick / 2)} m {N(left + dash)} {N(y - thick / 2)} l {N(left + dash)} {N(y + thick / 2)} l {N(left)} {N(y + thick / 2)} l");
                pieces.AppendLine("f");
                break;
            case "square dots":   // 1.5 by 1.2, every 3
                for (var left = x; left + 1.5 * scale <= x + width + scale; left += dot) pieces.AppendLine($"{N(left)} {N(y - thick / 2)} {N(1.5 * scale)} {N(thick)} re f");
                break;
            case "round dots":   // circles of 1.2 drawn with four curves
                var (radius, bend) = (thick / 2, thick / 2 * 0.5523);
                for (var left = x + radius; left + radius <= x + width + scale; left += dot)
                    pieces.AppendLine(
                        $"{N(left + radius)} {N(y)} m {N(left + radius)} {N(y + bend)} {N(left + bend)} {N(y + radius)} {N(left)} {N(y + radius)} c " +
                        $"{N(left - bend)} {N(y + radius)} {N(left - radius)} {N(y + bend)} {N(left - radius)} {N(y)} c " +
                        $"{N(left - radius)} {N(y - bend)} {N(left - bend)} {N(y - radius)} {N(left)} {N(y - radius)} c " +
                        $"{N(left + bend)} {N(y - radius)} {N(left + radius)} {N(y - bend)} {N(left + radius)} {N(y)} c f");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(style));
        }
        return pieces.ToString();
    }

    [Theory]
    [InlineData("bars", 12)]
    [InlineData("chrome dashes", 12)]
    [InlineData("square dots", 12)]
    [InlineData("round dots", 12)]
    // Larger text has thicker dots and dashes, up to a fifth of an em.
    [InlineData("chrome dashes", 30)]
    [InlineData("square dots", 30)]
    [InlineData("round dots", 30)]
    public void Pdf_dashed_and_dotted_underlines_drawn_piece_by_piece_underline_the_text(string style, double size)
    {
        const string text = "dashed underline run", line = "A dashed underline run here.";
        var result = Extract(Measured(string.Join("\n", Text(72, 720, line, size),
            Pieces(style, Span(72, line, text, size).Left, Width(text, size), Under(720, size), size / 12))));
        Assert.Equal([new TextRange(2, 2 + text.Length)], Assert.Single(result.Pages[0].Regions).Underlined);
        AssertNoDrawingLeft(result);
    }

    [Fact]
    public void Pdf_a_row_of_dots_that_is_not_under_text_stays_a_drawing()
    {
        // Ten small squares in a row are a dotted line only where they lie under the text they would underline.
        var result = Extract(Measured(string.Join("\n", Text(72, 720, "Rating"), Pieces("square dots", 200, 30, 600))));
        Assert.Null(Assert.Single(result.Pages[0].Regions).Underlined);
        Assert.Equal(10, result.VisualGraphs![1].Paths!.Count);
    }

    // The wavy underline LibreOffice draws: a curve whose control points sway between the baseline and a
    // quarter of an em under it, one period every half em or so, over x to x + width.
    private static string Wave(double x, double width, double baseline, double size = 12)
    {
        var periods = Math.Max(1, (int)Math.Round(width / (0.47 * size)));
        var period = width / periods;
        var (middle, amplitude) = (baseline - 0.118 * size, 0.118 * size);
        var curve = new StringBuilder($"0 0 0 RG 0.05 w {N(x)} {N(middle)} m");
        for (var index = 0; index < periods; index++)
        {
            var start = x + index * period;
            curve.Append($" {N(start + period / 4)} {N(middle + amplitude)} {N(start + period / 2)} {N(middle)} v");
            curve.Append($" {N(start + 3 * period / 4)} {N(middle - amplitude)} {N(start + period)} {N(middle)} v");
        }
        return curve.Append(" S").ToString();
    }

    // A zigzag of straight pieces, as other producers draw a wavy underline: 2.4 across and 1.4 up and down.
    private static string Zigzag(double left, double right, double y)
    {
        var steps = (int)Math.Round((right - left) / 2.4);
        var path = new StringBuilder($"0.5 w {N(left)} {N(y)} m");
        for (var step = 1; step <= steps; step++)
            path.Append($" {N(left + (right - left) * step / steps)} {N(y + (step % 2 == 0 ? 0 : -1.4))} l");
        return path.Append(" S").ToString();
    }

    [Fact]
    public void Pdf_wavy_underlines_drawn_as_curves_underline_the_text()
    {
        var wavy = Span(72, "A wavy line here.", "wavy line");
        var small = Span(72, "Small wavy text", "wavy text", 7);
        var zigzag = Span(72, "A zigzag underline.", "zigzag");
        var result = Extract(Measured(string.Join("\n",
            Text(72, 720, "A wavy line here."), Wave(wavy.Left, wavy.Right - wavy.Left, 720),
            Text(72, 690, "Small wavy text", 7), Wave(small.Left, small.Right - small.Left, 690, 7),
            Text(72, 660, "A zigzag underline."), Zigzag(zigzag.Left, zigzag.Right, 660 - 1.8))));
        var regions = result.Pages[0].Regions;
        Assert.Equal([new TextRange(2, 11)], regions.Single(region => region.Text == "A wavy line here.").Underlined);
        Assert.Equal([new TextRange(6, 15)], regions.Single(region => region.Text == "Small wavy text").Underlined);
        Assert.Equal([new TextRange(2, 8)], regions.Single(region => region.Text == "A zigzag underline.").Underlined);
        AssertNoDrawingLeft(result);
    }

    [Fact]
    public async Task Pdf_a_wavy_underline_leaves_no_curve_warning_in_the_export()
    {
        // A curve is recorded with a warning of its own (VisualPathPartial); consumed as a decoration, it takes that along.
        var wavy = Span(72, "A wavy line here.", "wavy line");
        var (markdown, result) = await Export(Measured(Text(72, 720, "A wavy line here.") + "\n" + Wave(wavy.Left, wavy.Right - wavy.Left, 720)));
        Assert.Contains("\nA <u>wavy line</u> here.\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code.StartsWith("Visual", StringComparison.Ordinal));
        Assert.Equal(0, ExportSummaryBuilder.Build(result.Graph, result.Diagnostics).ReviewPages);
    }

    [Fact]
    public void Pdf_chromes_wavy_underline_a_pattern_in_a_bar_under_the_baseline_underlines_the_text()
    {
        // Chrome fills the wave with a pattern in a bar 0.31 em tall that hangs under the baseline.
        var (left, right) = Span(72, "A wavy line here.", "wavy line");
        var result = Extract(Measured(string.Join("\n", Text(72, 720, "A wavy line here."),
            $"/Pattern cs /P7 scn {N(left)} 715.05 {N(right - left)} 3.75 re f")));
        Assert.Equal([new TextRange(2, 11)], Assert.Single(result.Pages[0].Regions).Underlined);
        AssertNoDrawingLeft(result);
    }

    [Fact]
    public void Pdf_shapes_that_are_no_thin_line_along_the_text_stay_drawings()
    {
        var tall = Span(72, "Tall zigzag here", "zigzag");
        var box = Span(72, "A thin box here", "thin box");
        var marker = Span(72, "Marker over text", "over");
        // A zigzag half an em tall (a wave may be 0.3 em), a box drawn as an outline, a bar over the letters
        // 0.46 em tall, and a line 0.6 em under the baseline.
        var zigzag = $"0.5 w {N(tall.Left)} 715 m {N(tall.Left + 6)} 721 l {N(tall.Left + 12)} 715 l {N(tall.Left + 18)} 721 l {N(tall.Left + 24)} 715 l S";
        var result = Extract(Measured(string.Join("\n",
            Text(72, 720, "Tall zigzag here"), zigzag,
            Text(72, 690, "A thin box here"), $"{N(box.Left)} {N(Under(690) - 0.5)} {N(box.Right - box.Left)} 1 re S",
            Text(72, 660, "Marker over text"), Bar(marker.Left, marker.Right, 660 + 0.1 * 12, 5.5),
            Text(72, 630, "Far under the text"), Rule(72, 72 + Width("Far under the text", 12), 630 - 0.6 * 12))));
        var regions = result.Pages[0].Regions;
        Assert.All(regions, region => Assert.Null(region.Underlined));
        Assert.All(regions, region => Assert.Null(region.Struck));
        Assert.Equal(4, result.VisualGraphs![1].Paths!.Count);
    }

    // A table of three columns and three rows, text at 11 points; note holds the shows of the last cell,
    // and extras are drawn after the table (the bars under some of its text, say).
    private static string Table(string note, params string[] extras)
    {
        var grid = "0.8 w 72 610 390 90 re S\n72 670 m 462 670 l S\n72 640 m 462 640 l S\n202 610 m 202 700 l S\n332 610 m 332 700 l S";
        return grid + "\n" + string.Join("\n", Text(78, 680, "Name", 11), Text(208, 680, "Price", 11), Text(338, 680, "Note", 11),
            Text(78, 650, "Apple", 11), Text(208, 650, "120", 11), Text(338, 650, "ok", 11),
            Text(78, 620, "Grape", 11), Text(208, 620, "1,000 1,200", 11), note, string.Join("\n", extras));
    }

    private static string Raised => Text(338, 620, "raised", 11);

    [Fact]
    public void Pdf_underlined_table_cells_carry_their_ranges_into_the_table()
    {
        var (left, right) = Span(208, "1,000 1,200", "1,000", 11);
        var result = Extract(Measured(Table(Raised, Bar(left, right, Under(620, 11)), Bar(78, 78 + Width("Apple", 11), Under(650, 11)))));
        var table = Assert.Single(result.Tables![1]);
        var underlined = table.Rows.SelectMany(row => row.Cells).Where(cell => cell.Underlined is not null).ToArray();
        Assert.Equal(["Apple", "1,000 1,200"], underlined.Select(cell => cell.Text));
        Assert.Equal([new TextRange(0, 5)], underlined[0].Underlined);
        Assert.Equal([new TextRange(0, 5)], underlined[1].Underlined);
        // The bars are no rules of the grid, and nothing else is underlined or struck.
        Assert.DoesNotContain(result.Diagnostics ?? [], diagnostic => diagnostic.StartsWith("Visual", StringComparison.Ordinal));
        Assert.All(table.Rows.SelectMany(row => row.Cells), cell => Assert.Null(cell.Struck));
    }

    [Fact]
    public async Task Pdf_underlined_table_cells_are_written_underlined()
    {
        var (left, right) = Span(208, "1,000 1,200", "1,000", 11);
        var (markdown, _) = await Export(Measured(Table(Raised, Bar(left, right, Under(620, 11)))));
        Assert.Contains("| Grape | <u>1,000</u> 1,200 | raised |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Apple | 120 | ok |", markdown, StringComparison.Ordinal);
    }

    // An annotation over the text of a measured show: the quadrilateral Acrobat writes (top left, top
    // right, bottom left, bottom right), from a fifth of an em under the baseline to 0.9 em over it.
    private static string Markup(string subtype, double left, double right, double baseline, double size = 12, string extra = "")
    {
        var (top, bottom) = (baseline + 0.9 * size, baseline - 0.2 * size);
        return $"<< /Type /Annot /Subtype /{subtype} /Rect [{N(left)} {N(bottom)} {N(right)} {N(top)}] " +
            $"/QuadPoints [{N(left)} {N(top)} {N(right)} {N(top)} {N(left)} {N(bottom)} {N(right)} {N(bottom)}]{extra} >>";
    }

    [Fact]
    public void Pdf_underline_and_squiggly_annotations_underline_the_text_under_them()
    {
        var delete = Span(72, "Keep this. Delete this. Keep that.", "Delete this.");
        var wavy = Span(72, "Wavy markup stays here", "Wavy markup");
        var hidden = Span(72, "Hidden markup here", "Hidden");
        var result = Extract(Measured(string.Join("\n",
                Text(72, 720, "Keep this. Delete this. Keep that."), Text(72, 690, "Wavy markup stays here"), Text(72, 660, "Hidden markup here")),
            Markup("Underline", delete.Left, delete.Right, 720), Markup("Squiggly", wavy.Left, wavy.Right, 690),
            Markup("Underline", hidden.Left, hidden.Right, 660, extra: " /F 2")));
        var regions = result.Pages[0].Regions;
        Assert.Equal([new TextRange(11, 23)], regions.Single(region => region.Text.StartsWith("Keep this.", StringComparison.Ordinal)).Underlined);
        Assert.Equal([new TextRange(0, 11)], regions.Single(region => region.Text.StartsWith("Wavy", StringComparison.Ordinal)).Underlined);
        Assert.Null(regions.Single(region => region.Text.StartsWith("Hidden", StringComparison.Ordinal)).Underlined);
        Assert.All(regions, region => Assert.Null(region.Struck));
    }

    [Fact]
    public void Pdf_underline_annotation_over_a_whole_show_underlines_it_without_glyph_widths()
    {
        var result = Extract(Plain(string.Join("\n", Text(72, 720, "Keep this."), Text(160, 720, "Delete this."), Text(260, 720, "Keep that.")),
            Markup("Underline", 158, 240, 720)));
        var line = Assert.Single(result.Pages[0].Regions);
        var start = line.Text.IndexOf("Delete this.", StringComparison.Ordinal);
        Assert.Equal([new TextRange(start, start + "Delete this.".Length)], line.Underlined);
        Assert.Null(line.Struck);
    }

    [Fact]
    public void Pdf_underline_annotation_without_quadpoints_underlines_the_text_in_its_rectangle()
    {
        var (left, right) = Span(72, "Keep the important word here.", "important");
        var result = Extract(Measured(Text(72, 720, "Keep the important word here."),
            $"<< /Type /Annot /Subtype /Underline /Rect [{N(left)} 717 {N(right)} 731] >>"));
        Assert.Equal([new TextRange(9, 18)], Assert.Single(result.Pages[0].Regions).Underlined);
    }

    [Fact]
    public async Task Pdf_underline_annotations_are_written_underlined()
    {
        var (left, right) = Span(72, "Keep the important word here.", "important");
        var (markdown, result) = await Export(Measured(Text(72, 720, "Keep the important word here."), Markup("Underline", left, right, 720)));
        Assert.Contains("\nKeep the <u>important</u> word here.\n", markdown, StringComparison.Ordinal);
        Assert.Equal(0, ExportSummaryBuilder.Build(result.Graph, result.Diagnostics).ReviewPages);
    }

    // A link annotation over the text it covers, the way LibreOffice, Word and Chrome write one.
    private static string Link(double left, double right, double baseline, double size = 12) =>
        $"<< /Type /Annot /Subtype /Link /Border [0 0 0] /Rect [{N(left - 0.1)} {N(baseline - 0.25 * size)} {N(right + 0.3)} {N(baseline + 0.85 * size)}] " +
        "/A << /S /URI /URI (https://example.com/terms) >> >>";

    [Fact]
    public async Task Pdf_underlines_that_only_style_a_link_are_not_emphasis_but_leave_the_drawing_all_the_same()
    {
        const string text = "See the terms for details.";
        var (left, right) = Span(72, text, "the terms");
        // Chrome and LibreOffice both underline a link's text: the line is the link's style.
        var content = Text(72, 720, text) + "\n" + Bar(left - 0.2, right + 0.3, Under(720), 0.75);
        var linked = Extract(Measured(content, Link(left, right, 720)));
        Assert.Null(Assert.Single(linked.Pages[0].Regions).Underlined);
        AssertNoDrawingLeft(linked);
        // Without the link, the same line is an underline.
        Assert.Equal([new TextRange(4, 13)], Assert.Single(Lines(Measured(content))).Underlined);
        var (markdown, _) = await Export(Measured(content, Link(left, right, 720)));
        Assert.Contains("\nSee the terms for details.\n", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_the_part_of_an_underline_beyond_a_link_is_emphasis()
    {
        const string text = "See the terms now.";
        var (left, right) = Span(72, text, "the terms");
        // One line under all of the text, a link over the middle of it.
        var line = Assert.Single(Lines(Measured(Text(72, 720, text) + "\n" + Rule(72, 72 + Width(text, 12), Under(720)), Link(left, right, 720))));
        Assert.Equal([new TextRange(0, 3), new TextRange(14, 18)], line.Underlined);
    }

    [Fact]
    public void Pdf_a_link_over_two_lines_covers_only_the_text_of_its_quadrilaterals()
    {
        const string upper = "One two three", lower = "Four five six";
        var three = Span(300, upper, "three");
        var four = Span(72, lower, "Four");
        var oneTwo = Span(300, upper, "One two");
        // The link's rectangle spans both lines from the left margin to the end of the upper one, so it holds
        // the underline of "One two" too; its quadrilaterals hold "three" and "Four" alone.
        var link = $"<< /Type /Annot /Subtype /Link /Rect [{N(four.Left)} 687 {N(three.Right)} 730.2] " +
            $"/QuadPoints [{N(three.Left)} 730.2 {N(three.Right)} 730.2 {N(three.Left)} 717 {N(three.Right)} 717 " +
            $"{N(four.Left)} 700.2 {N(four.Right)} 700.2 {N(four.Left)} 687 {N(four.Right)} 687] /A << /S /URI /URI (https://example.com/) >> >>";
        var regions = Lines(Measured(string.Join("\n", Text(300, 720, upper), Text(72, 690, lower),
            Rule(oneTwo.Left, oneTwo.Right, Under(720)), Rule(three.Left, three.Right, Under(720)), Rule(four.Left, four.Right, Under(690))), link));
        Assert.Equal([new TextRange(0, 7)], regions.Single(region => region.Text == upper).Underlined);
        Assert.Null(regions.Single(region => region.Text == lower).Underlined);
    }

    [Fact]
    public void Pdf_a_link_over_a_whole_page_does_not_make_every_underline_a_link_style()
    {
        var (left, right) = Span(72, "Keep the important word here.", "important");
        var line = Assert.Single(Lines(Measured(Text(72, 720, "Keep the important word here.") + "\n" + Rule(left, right, Under(720)),
            "<< /Type /Annot /Subtype /Link /Rect [0 0 595 842] /A << /S /URI /URI (https://example.com/) >> >>")));
        Assert.Equal([new TextRange(9, 18)], line.Underlined);
    }

    [Fact]
    public void Pdf_the_same_underline_painted_twice_is_one_underline()
    {
        // A link inside underlined text: Chrome paints the underline of the text and the link's own over it.
        var (left, right) = Span(72, "Read the terms first.", "the terms");
        var result = Extract(Measured(string.Join("\n", Text(72, 720, "Read the terms first."),
            Bar(left, right, Under(720), 0.75), "0 0 0.93 rg", Bar(left, right, Under(720), 0.75))));
        Assert.Equal([new TextRange(5, 14)], Assert.Single(result.Pages[0].Regions).Underlined);
        AssertNoDrawingLeft(result);
    }

    [Theory]
    [InlineData("Underline")]
    [InlineData("Squiggly")]
    public void Pdf_an_underline_annotation_on_link_text_is_markup_and_so_is_a_strikethrough_of_it(string subtype)
    {
        // A drawn underline on a link is the link's style. An Underline or Squiggly annotation is somebody's deliberate
        // markup of the text, which the link under it does not make less so.
        const string text = "See the terms now.";
        var (left, right) = Span(72, text, "the terms");
        var underlined = Assert.Single(Lines(Measured(Text(72, 720, text), Link(left, right, 720), Markup(subtype, left, right, 720))));
        Assert.Equal([new TextRange(4, 13)], underlined.Underlined);
        // The link's own area, wider than the markup, does not take a part of it either: one word of the link is marked.
        var (word, wordEnd) = Span(72, text, "terms");
        var part = Assert.Single(Lines(Measured(Text(72, 720, text), Link(left, right, 720), Markup(subtype, word, wordEnd, 720))));
        Assert.Equal([new TextRange(8, 13)], part.Underlined);
        var struck = Assert.Single(Lines(Measured(Text(72, 720, text) + "\n" + Rule(left, right, Through(720)), Link(left, right, 720))));
        Assert.Equal([new TextRange(4, 13)], struck.Struck);
    }

    [Fact]
    public void Pdf_a_strike_and_an_underline_on_the_same_text_mark_it_both_ways()
    {
        var (left, right) = Span(72, "Old price 1000 yen now", "1000 yen");
        var line = Assert.Single(Lines(Measured(string.Join("\n", Text(72, 720, "Old price 1000 yen now"),
            Bar(left, right, Under(720)), Bar(left, right, Through(720))))));
        Assert.Equal([new TextRange(10, 18)], line.Struck);
        Assert.Equal([new TextRange(10, 18)], line.Underlined);
    }

    [Fact]
    public async Task Pdf_text_struck_and_underlined_is_written_with_the_underline_outside()
    {
        var (left, right) = Span(72, "Old 1000 yen now", "1000 yen");
        var (markdown, _) = await Export(Measured(string.Join("\n", Text(72, 720, "Old 1000 yen now"),
            Bar(left, right, Under(720)), Bar(left, right, Through(720)))));
        Assert.Contains("\nOld <u>~~1000 yen~~</u> now\n", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_a_line_annotation_across_text_marks_it_like_a_drawn_line()
    {
        var (left, right) = Span(72, "Keep the word price here", "price");
        var (underLeft, underRight) = Span(72, "Mark the word under here", "under");
        var result = Extract(Measured(string.Join("\n", Text(72, 720, "Keep the word price here"), Text(72, 690, "Mark the word under here")),
            // Drawn by hand: a little off the horizontal, through the middle of "price" and under "under".
            $"<< /Type /Annot /Subtype /Line /Rect [{N(left)} 718 {N(right)} 730] /L [{N(left - 1)} 723.3 {N(right + 1)} 724.1] /LE [/None /None] /C [1 0 0] >>",
            $"<< /Type /Annot /Subtype /Line /Rect [{N(underLeft)} 684 {N(underRight)} 692] /L [{N(underLeft - 1)} 686.4 {N(underRight + 1)} 686.6] >>"));
        var regions = result.Pages[0].Regions;
        var struck = regions.Single(region => region.Text.StartsWith("Keep", StringComparison.Ordinal));
        Assert.Equal([new TextRange(14, 19)], struck.Struck);
        Assert.Null(struck.Underlined);
        var underlined = regions.Single(region => region.Text.StartsWith("Mark", StringComparison.Ordinal));
        Assert.Equal([new TextRange(14, 19)], underlined.Underlined);
        Assert.Null(underlined.Struck);
    }

    // The points of a pencil stroke along y from left to right, swaying by wobble above and below.
    private static string Stroke(double left, double right, double y, double wobble)
    {
        var points = new List<string>();
        for (var step = 0; step <= 12; step++)
            points.Add($"{N(left + (right - left) * step / 12)} {N(y + (step % 2 == 0 ? wobble : -wobble))}");
        return "[" + string.Join(" ", points) + "]";
    }

    [Fact]
    public void Pdf_an_ink_stroke_that_is_nearly_straight_across_text_marks_it_like_a_drawn_line()
    {
        var (left, right) = Span(72, "Keep the word price here", "price");
        var (underLeft, underRight) = Span(72, "Mark the word under here", "under");
        var result = Extract(Measured(string.Join("\n", Text(72, 720, "Keep the word price here"), Text(72, 690, "Mark the word under here")),
            $"<< /Type /Annot /Subtype /Ink /Rect [{N(left)} 718 {N(right)} 730] /InkList [{Stroke(left - 1, right + 1, 723.6, 0.5)}] >>",
            $"<< /Type /Annot /Subtype /Ink /Rect [{N(underLeft)} 684 {N(underRight)} 692] /InkList [{Stroke(underLeft - 1, underRight + 1, 686.5, 0.4)}] >>"));
        var regions = result.Pages[0].Regions;
        Assert.Equal([new TextRange(14, 19)], regions.Single(region => region.Text.StartsWith("Keep", StringComparison.Ordinal)).Struck);
        Assert.Equal([new TextRange(14, 19)], regions.Single(region => region.Text.StartsWith("Mark", StringComparison.Ordinal)).Underlined);
    }

    [Theory]
    [InlineData(0.6, 1, true)]    // a twentieth of an em off the straight
    [InlineData(1.8, 1, false)]   // 0.15 em: a scribble, not a line
    [InlineData(0.5, 2, false)]   // two strokes in one annotation
    public void Pdf_an_ink_annotation_is_a_line_only_when_it_is_one_straight_stroke(double wobble, int strokes, bool straight)
    {
        var (left, right) = Span(72, "Keep the word price here", "price");
        var ink = strokes == 1
            ? Stroke(left - 1, right + 1, 723.6, wobble)
            : Stroke(left - 1, left + 6, 723.6, wobble) + " " + Stroke(left + 8, right + 1, 723.6, wobble);
        var line = Assert.Single(Lines(Measured(Text(72, 720, "Keep the word price here"),
            $"<< /Type /Annot /Subtype /Ink /Rect [{N(left)} 718 {N(right)} 730] /InkList [{ink}] >>")));
        if (straight) Assert.Equal([new TextRange(14, 19)], line.Struck);
        else Assert.Null(line.Struck);
    }

    [Theory]
    [InlineData("/L [60 723.6 400 723.6]", true)]                                  // the same line, as a plain mark
    [InlineData("/L [72 700 200 740]", false)]                                     // slanted
    [InlineData("/L [100 700 112 740]", false)]                                    // steep
    [InlineData("/L [60 723.6 400 723.6] /LE [/None /OpenArrow]", false)]          // an arrowhead
    [InlineData("/L [60 723.6 400 723.6] /IT /LineArrow", false)]                  // an arrow by intent
    [InlineData("/L [60 723.6 400 723.6] /Cap true", false)]                       // a caption
    [InlineData("/L [60 723.6 400 723.6] /F 2", false)]                            // hidden
    public void Pdf_a_line_annotation_is_a_decoration_only_when_it_is_a_straight_plain_line_over_the_text(string line, bool struck)
    {
        var region = Assert.Single(Lines(Measured(Text(72, 720, "Keep the word price here"),
            "<< /Type /Annot /Subtype /Line /Rect [60 690 410 740] " + line + " >>")));
        Assert.Equal(struck ? Whole("Keep the word price here") : null, region.Struck);
        Assert.Null(region.Underlined);
    }

    [Fact]
    public void Pdf_a_line_annotation_over_part_of_an_unmeasured_show_follows_the_rules_of_a_strikethrough()
    {
        // Through part of "Old price 1000 yen": which characters it strikes cannot be told, so it is reported.
        var through = Extract(Plain(Text(72, 720, "Old price 1000 yen"),
            "<< /Type /Annot /Subtype /Line /Rect [125 718 175 730] /L [125.4 723.6 174.8 723.6] >>"));
        Assert.Null(Assert.Single(through.Pages[0].Regions).Struck);
        Assert.Single(through.Diagnostics ?? [], diagnostic => diagnostic.StartsWith("PdfStrikethroughUnplaced", StringComparison.Ordinal));
        // The same line under the text is an underline, which is left out without a word.
        var under = Extract(Plain(Text(72, 720, "Old price 1000 yen"),
            "<< /Type /Annot /Subtype /Line /Rect [125 716 175 722] /L [125.4 718.8 174.8 718.8] >>"));
        Assert.Null(Assert.Single(under.Pages[0].Regions).Underlined);
        Assert.DoesNotContain(under.Diagnostics ?? [], diagnostic => diagnostic.StartsWith("PdfStrikethroughUnplaced", StringComparison.Ordinal));
        Assert.Null(under.Pages[0].UncertainText);
    }

    [Fact]
    public async Task Pdf_a_line_annotation_through_text_is_written_struck()
    {
        var (left, right) = Span(72, "Old price 1000 yen", "1000 yen");
        var (markdown, result) = await Export(Measured(Text(72, 720, "Old price 1000 yen"),
            $"<< /Type /Annot /Subtype /Line /Rect [{N(left)} 718 {N(right)} 730] /L [{N(left)} 723.6 {N(right)} 723.6] >>"));
        Assert.Contains("\nOld price ~~1000 yen~~\n", markdown, StringComparison.Ordinal);
        Assert.Equal(0, ExportSummaryBuilder.Build(result.Graph, result.Diagnostics).ReviewPages);
    }

    [Fact]
    public void Pdf_underlines_in_merged_lines_keep_their_place_in_the_joined_text()
    {
        // Shows that carry their spaces and abut, joined into one line: the first and third are underlined.
        var first = Span(72, "One", "One");
        var third = Span(72 + Width("One Two ", 12), "Three", "Three");
        var abutting = Assert.Single(Lines(Measured(string.Join("\n",
            Text(72, 720, "One "), Text(72 + Width("One ", 12), 720, "Two "), Text(72 + Width("One Two ", 12), 720, "Three"),
            Rule(first.Left, first.Right, Under(720)), Rule(third.Left, third.Right, Under(720))))));
        Assert.Equal("One Two Three", abutting.Text);
        Assert.Equal([new TextRange(0, 4), new TextRange(8, 13)], abutting.Underlined);
        // Shows a wide gap apart are joined with a space of their own, which the ranges after it count.
        var (two, three) = (72 + Width("One", 12) + 25, 72 + Width("One", 12) + 25 + Width("Two", 12) + 25);
        var apart = Assert.Single(Lines(Measured(string.Join("\n",
            Text(72, 720, "One"), Text(two, 720, "Two"), Text(three, 720, "Three"),
            Rule(two, two + Width("Two", 12), Under(720)), Rule(three, three + Width("Three", 12), Under(720))))));
        Assert.Equal("One Two Three", apart.Text);
        Assert.Equal([new TextRange(4, 7), new TextRange(8, 13)], apart.Underlined);
    }

    [Fact]
    public void Pdf_an_underline_over_the_second_show_of_a_table_cell_is_placed_in_the_joined_cell_text()
    {
        // The note of the last row is two shows that continue each other; the second is underlined.
        var second = 338 + Width("raised ", 11);
        var result = Extract(Measured(Table(Text(338, 620, "raised ", 11) + "\n" + Text(second, 620, "twice", 11),
            Bar(second, second + Width("twice", 11), Under(620, 11)))));
        var cell = Assert.Single(Assert.Single(result.Tables![1]).Rows.SelectMany(row => row.Cells), cell => cell.Underlined is not null);
        Assert.Equal("raised twice", cell.Text);
        Assert.Equal([new TextRange(7, 12)], cell.Underlined);
    }

    [Fact]
    public async Task Pdf_underlined_text_beside_a_table_is_written_underlined()
    {
        // With a table on the page, the lines around it are projected together with it.
        var (left, right) = Span(72, "Intro to the table here.", "table");
        var (markdown, _) = await Export(Measured(Table(Raised, Text(72, 740, "Intro to the table here."), Bar(left, right, Under(740)))));
        Assert.Contains("\nIntro to the <u>table</u> here.\n", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_a_link_does_not_make_the_underline_of_the_line_below_a_link_style()
    {
        const string text = "See the terms now.";
        var (left, right) = Span(72, text, "the terms");
        // Two lines of the same text, a link on the upper one; both are underlined under "the terms".
        var regions = Lines(Measured(string.Join("\n", Text(72, 720, text), Text(72, 700, text),
            Rule(left, right, Under(720)), Rule(left, right, Under(700))), Link(left, right, 720)));
        Assert.Null(regions.OrderByDescending(region => region.BoundingBox.Y).First().Underlined);
        Assert.Equal([new TextRange(4, 13)], regions.OrderByDescending(region => region.BoundingBox.Y).Last().Underlined);
    }

    [Fact]
    public void Pdf_a_short_squiggle_under_text_is_no_wavy_underline()
    {
        // 2.4 points tall and 8 long: a wave is at least four times as long as it is tall.
        var result = Extract(Measured(string.Join("\n", Text(72, 720, "Short squiggle here"),
            "0.4 w 72 718.8 m 73.5 720 75 717.6 76 718.8 c 77.5 720 79 717.6 80 718.8 c S")));
        Assert.Null(Assert.Single(result.Pages[0].Regions).Underlined);
        Assert.Single(result.VisualGraphs![1].Paths!);
    }

    // Annotations by the thousand, with the target (an Underline over "important") last of them.
    private static string Important(out double left, out double right)
    {
        (left, right) = Span(72, "Keep the important word here.", "important");
        return Text(72, 720, "Keep the important word here.");
    }

    [Fact]
    public void Pdf_an_annotation_with_an_absurd_number_of_quadrilaterals_is_read_only_so_far()
    {
        var content = Important(out var left, out var right);
        var quads = new StringBuilder();
        for (var index = 0; index < 10_000; index++) quads.Append("500 11 501 11 500 10 501 10 ");
        quads.Append($"{N(left)} 730.8 {N(right)} 730.8 {N(left)} 717.6 {N(right)} 717.6");
        var line = Assert.Single(Lines(Measured(content,
            $"<< /Type /Annot /Subtype /Underline /Rect [0 0 595 842] /QuadPoints [{quads}] >>")));
        // The ten thousand before it count, the next one does not.
        Assert.Null(line.Underlined);
        var within = Assert.Single(Lines(Measured(content,
            $"<< /Type /Annot /Subtype /Underline /Rect [0 0 595 842] /QuadPoints [{quads.ToString().Remove(0, 28)}] >>")));
        Assert.Equal([new TextRange(9, 18)], within.Underlined);
    }

    [Fact]
    public void Pdf_a_page_with_a_great_many_annotations_is_read_only_so_far()
    {
        var content = Important(out var left, out var right);
        var underline = Markup("Underline", left, right, 720);
        var harmless = "<< /Type /Annot /Subtype /Link /Rect [500 10 501 11] >>";
        // Fifty thousand links, then the underline; and again with the underline among them.
        var late = Enumerable.Repeat(harmless, 50_000).Append(underline).ToArray();
        Assert.Null(Assert.Single(PatientLines(Measured(content, late))).Underlined);
        var early = Enumerable.Repeat(harmless, 49_999).Append(underline).ToArray();
        Assert.Equal([new TextRange(9, 18)], Assert.Single(PatientLines(Measured(content, early))).Underlined);
    }

    [Fact]
    public void Pdf_a_page_with_a_great_many_marks_is_read_only_so_far()
    {
        var content = Important(out var left, out var right);
        var quads = new StringBuilder();
        for (var index = 0; index < 10_000; index++) quads.Append("500 11 501 11 500 10 501 10 ");
        var crowd = $"<< /Type /Annot /Subtype /Underline /Rect [0 0 595 842] /QuadPoints [{quads}] >>";
        var underline = Markup("Underline", left, right, 720);
        // Ten annotations of ten thousand quadrilaterals fill a page's room for marks.
        Assert.Null(Assert.Single(PatientLines(Measured(content, Enumerable.Repeat(crowd, 10).Append(underline).ToArray()))).Underlined);
        Assert.Equal([new TextRange(9, 18)], Assert.Single(PatientLines(Measured(content, Enumerable.Repeat(crowd, 9).Append(underline).ToArray()))).Underlined);
    }

    // A line drawn by hand: a stroked line of a width the PDF does not say.
    private static string Hand(double left, double right, double y) => Rule(left, right, y, 1.2);

    // How many drawings the page still has: the lines and shapes no decoration took.
    private static int Drawings(PdfExtractionResult result) => result.VisualGraphs?.GetValueOrDefault(1)?.Paths?.Count ?? 0;

    [Fact]
    public void Pdf_a_line_drawn_by_hand_through_a_word_runs_on_past_it_and_strikes_it()
    {
        // Excel's lines run from where the pointer went down to where it came up: this one starts a little after
        // the word and ends 2.4 em past it, before the next word on the baseline.
        var ito = Span(131, "Ito", "Ito");
        var result = Extract(Measured(string.Join("\n",
            Text(55, 715, "Build"), Text(131, 715, "Ito"), Text(207, 715, "active"),
            Hand(132, ito.Right + 2.4 * 12, 715 + 0.4 * 12))));
        var line = Assert.Single(result.Pages[0].Regions);
        Assert.Equal("Build Ito active", line.Text);
        Assert.Equal([new TextRange(6, 9)], line.Struck);
        Assert.Null(line.Underlined);
        AssertNoDrawingLeft(result);
    }

    [Fact]
    public void Pdf_a_double_line_drawn_by_hand_across_a_row_of_cells_strikes_each_text_under_it()
    {
        var dropped = Span(207, "dropped", "dropped");
        var end = dropped.Right + 2.6 * 12;
        var result = Extract(Measured(string.Join("\n",
            Text(55, 695, "Old step"), Text(131, 695, "Kato"), Text(207, 695, "dropped"), Text(207, 675, "planned"),
            Hand(55, end, 695 + 0.47 * 12), Hand(55, end, 695 + 0.34 * 12))));
        var regions = result.Pages[0].Regions;
        var row = regions.Single(region => region.Text.StartsWith("Old", StringComparison.Ordinal));
        Assert.Equal("Old step Kato dropped", row.Text);
        Assert.Equal([new TextRange(0, 8), new TextRange(9, 13), new TextRange(14, 21)], row.Struck);
        Assert.Null(regions.Single(region => region.Text == "planned").Struck);
        AssertNoDrawingLeft(result);
    }

    [Fact]
    public void Pdf_a_line_drawn_by_hand_over_text_without_glyph_widths_runs_on_past_it_too()
    {
        // Through "Ito" and 2.4 em past what the show is estimated to reach.
        var result = Extract(Plain(string.Join("\n", Text(131, 715, "Ito"), Hand(132, 131 + 1.25 * 3 * 0.55 * 12 + 2.4 * 12, 715 + 0.4 * 12))));
        Assert.Equal(Whole("Ito"), Assert.Single(result.Pages[0].Regions).Struck);
        AssertNoDrawingLeft(result);
    }

    [Theory]
    [InlineData(3.9, 0.0, true)]    // past the end by 3.9 ems: the limit is 4
    [InlineData(4.2, 0.0, false)]
    [InlineData(0.0, 3.9, true)]    // and the same before the start
    [InlineData(0.0, 4.2, false)]
    [InlineData(3.9, 3.9, true)]
    public void Pdf_a_line_drawn_by_hand_may_run_past_the_text_it_covers_by_four_ems_and_no_more(double past, double before, bool struck)
    {
        var ito = Span(200, "Ito", "Ito");
        var result = Extract(Measured(string.Join("\n", Text(200, 715, "Ito"), Hand(ito.Left - before * 12, ito.Right + past * 12, 715 + 0.4 * 12))));
        var line = Assert.Single(result.Pages[0].Regions);
        Assert.Equal(struck ? Whole("Ito") : null, line.Struck);
        Assert.Equal(struck ? 0 : 1, Drawings(result));
    }

    [Theory]
    [InlineData(0.1, true)]     // a line through the lower part of the letters, as Word draws one by hand
    [InlineData(0.06, true)]
    [InlineData(0.03, false)]   // right on the baseline it is an underline
    public void Pdf_a_line_above_the_baseline_crosses_the_letters_and_strikes_them(double em, bool struck)
    {
        var (left, right) = Span(72, "Old price", "Old price");
        var line = Assert.Single(Lines(Measured(Text(72, 720, "Old price") + "\n" + Hand(left, right, 720 + em * 12))));
        Assert.Equal(struck ? Whole("Old price") : null, line.Struck);
        Assert.Equal(struck ? null : Whole("Old price"), line.Underlined);
    }

    [Fact]
    public void Pdf_a_line_drawn_by_hand_that_does_not_cover_the_text_it_runs_over_is_no_strikethrough()
    {
        const string text = "Keep the word here";
        var word = Span(200, text, "word");
        var middle = 200 + Width("Keep the ", 12) + 6;
        var through = 715 + 0.4 * 12;
        // From the middle of the text to 2 ems past its end; and from 2 ems before it to the middle.
        var late = Extract(Measured(string.Join("\n", Text(200, 715, text), Hand(middle, 200 + Width(text, 12) + 24, through))));
        Assert.Null(Assert.Single(late.Pages[0].Regions).Struck);
        Assert.Equal(1, Drawings(late));
        var early = Extract(Measured(string.Join("\n", Text(200, 715, text), Hand(200 - 24, middle, through))));
        Assert.Null(Assert.Single(early.Pages[0].Regions).Struck);
        Assert.Equal(1, Drawings(early));
        // Within the text, either way, a line is a partial strikethrough as before.
        var within = Extract(Measured(string.Join("\n", Text(200, 715, text), Hand(word.Left, word.Right + 1, through))));
        Assert.Equal([new TextRange(9, 13)], Assert.Single(within.Pages[0].Regions).Struck);
    }

    [Fact]
    public void Pdf_an_arrow_through_text_and_a_line_over_empty_space_stay_drawings()
    {
        // PowerPoint's arrow through "Moved forward": its head meets the end of the shaft, which ends 1.3 em past the text.
        var moved = Span(72, "Moved forward", "Moved forward", 24);
        var shaft = 296 + 0.35 * 24;
        var head = moved.Right + 1.3 * 24;
        var result = Extract(Measured(string.Join("\n",
            Text(72, 296, "Moved forward", 24), Hand(70, head, shaft),
            $"{N(head + 5.5)} {N(shaft)} m {N(head)} {N(shaft - 3)} l {N(head)} {N(shaft + 3)} l {N(head + 5.5)} {N(shaft)} l h f",
            // A bar over empty cells, in the height of the text of another row.
            Text(72, 250, "Design", 12), Hand(286, 494, 250 + 0.4 * 12))));
        var regions = result.Pages[0].Regions;
        Assert.All(regions, region => Assert.Null(region.Struck));
        Assert.All(regions, region => Assert.Null(region.Underlined));
        Assert.Equal(3, Drawings(result));
    }

    [Fact]
    public void Pdf_a_long_divider_through_a_title_is_no_strikethrough()
    {
        // A rule behind a centred title, with a gap in the paint over it: 20 ems of line on both sides.
        var title = Span(250, "Chapter One", "Chapter One");
        var result = Extract(Measured(string.Join("\n", Text(250, 715, "Chapter One"), Hand(title.Left - 20 * 12, title.Right + 20 * 12, 715 + 0.35 * 12))));
        Assert.Null(Assert.Single(result.Pages[0].Regions).Struck);
        Assert.Equal(1, Drawings(result));
    }

    [Fact]
    public void Pdf_a_thick_bar_through_text_with_overshoot_is_no_strikethrough()
    {
        // A marker bar half an em tall behind the letters, a little longer than the text, is no line.
        var (left, right) = Span(72, "Marked text", "Marked text");
        var result = Extract(Measured(Text(72, 720, "Marked text") + "\n" + Bar(left - 2, right + 12, 720 + 0.3 * 12, 6)));
        Assert.Null(Assert.Single(result.Pages[0].Regions).Struck);
        Assert.Equal(1, Drawings(result));
    }

    [Theory]
    [InlineData(0.9, true)]    // as the line under "planned" in LibreOffice's PDF of an Excel sheet
    [InlineData(1.4, true)]
    [InlineData(1.6, false)]   // the limit is 1.5 ems
    public void Pdf_a_line_drawn_by_hand_under_one_show_may_run_past_its_end_by_a_few_ems(double past, bool underlined)
    {
        var planned = Span(207, "planned", "planned");
        var result = Extract(Measured(string.Join("\n", Text(207, 676, "planned"), Hand(planned.Left + 1, planned.Right + past * 12, 676 - 0.09 * 12))));
        var line = Assert.Single(result.Pages[0].Regions);
        Assert.Equal(underlined ? Whole("planned") : null, line.Underlined);
        Assert.Null(line.Struck);
        Assert.Equal(underlined ? 0 : 1, Drawings(result));
    }

    [Theory]
    [InlineData(676 - 0.09 * 12, 207 - 14, 14)]   // starting well before the text
    [InlineData(676 - 0.3 * 12, 208, 14)]         // a rule lower than a quarter em under it
    public void Pdf_a_rule_that_runs_past_the_text_it_is_under_in_other_ways_stays_a_drawing(double y, double left, double past)
    {
        var planned = Span(207, "planned", "planned");
        var result = Extract(Measured(string.Join("\n", Text(207, 676, "planned"), Hand(left, planned.Right + past, y))));
        Assert.Null(Assert.Single(result.Pages[0].Regions).Underlined);
        Assert.Equal(1, Drawings(result));
    }

    [Fact]
    public void Pdf_a_rule_under_a_row_of_cells_that_runs_past_the_last_of_them_stays_a_drawing()
    {
        // The same line under two shows: a rule under a row, not an underline of one of the texts.
        var second = Span(300, "Quantity", "Quantity");
        var result = Extract(Measured(string.Join("\n",
            Text(207, 676, "Name"), Text(300, 676, "Quantity"), Hand(208, second.Right + 10, 676 - 0.09 * 12))));
        Assert.All(result.Pages[0].Regions, region => Assert.Null(region.Underlined));
        Assert.Equal(1, Drawings(result));
    }

    // The texts a page has underlined, in reading order.
    private static string[] UnderlinedTexts(PdfExtractionResult result) => result.Pages[0].Regions
        .SelectMany(region => (region.Underlined ?? []).Select(range => region.Text[range.Start..range.End].Trim())).ToArray();

    // The header of a table without vertical lines: three cells far apart on one baseline.
    private static string Header(out (double Left, double Right) item, out (double Left, double Right) quantity, out (double Left, double Right) total)
    {
        (item, quantity, total) = (Span(72, "Item", "Item"), Span(250, "Quantity", "Quantity"), Span(400, "Total", "Total"));
        return string.Join("\n", Text(72, 720, "Item"), Text(250, 720, "Quantity"), Text(400, 720, "Total"));
    }

    [Fact]
    public async Task Pdf_a_rule_under_a_row_of_cells_as_long_as_the_row_is_no_underline_of_their_texts()
    {
        // The rule under the header of a table without vertical lines runs over the gaps between the columns,
        // from the first cell to the last: formatting of the table, not of the texts. Like an underline it is
        // no drawing to compare with the page, and it marks nothing.
        var header = Header(out var item, out _, out var total);
        var pdf = Measured(header + "\n" + Rule(item.Left, total.Right, Under(720)));
        var result = Extract(pdf);
        Assert.Empty(UnderlinedTexts(result));
        AssertNoDrawingLeft(result);
        var (markdown, exported) = await Export(pdf);
        Assert.DoesNotContain("<u>", markdown, StringComparison.Ordinal);
        Assert.Equal(0, ExportSummaryBuilder.Build(exported.Graph, exported.Diagnostics).ReviewPages);
    }

    [Fact]
    public void Pdf_an_underline_under_each_cell_of_a_row_underlines_each_text()
    {
        var header = Header(out var item, out var quantity, out var total);
        var result = Extract(Measured(header + "\n" + string.Join("\n",
            new[] { item, quantity, total }.Select(cell => Rule(cell.Left, cell.Right, Under(720))))));
        Assert.Equal(["Item", "Quantity", "Total"], UnderlinedTexts(result));
        AssertNoDrawingLeft(result);
    }

    [Theory]
    [InlineData(0.9, true)]     // as far apart as words in a line of justified text can be
    [InlineData(1.2, false)]    // columns: the limit is 1 em
    public void Pdf_an_underline_over_a_gap_of_a_column_between_two_texts_is_a_rule_under_them(double gap, bool underlined)
    {
        var first = Span(72, "Total", "Total");
        var second = Span(first.Right + gap * 12, "amount", "amount");
        var result = Extract(Measured(string.Join("\n",
            Text(72, 720, "Total"), Text(first.Right + gap * 12, 720, "amount"), Rule(72, second.Right, Under(720)))));
        Assert.Equal(underlined ? new[] { "Total", "amount" } : Array.Empty<string>(), UnderlinedTexts(result));
        AssertNoDrawingLeft(result);
    }

    [Fact]
    public void Pdf_a_run_of_spaces_between_two_texts_is_a_gap_like_any_other()
    {
        // Columns made with spaces, in one show: the rule under the row is as long as the row.
        const string row = "Total                                        amount";
        var (left, right) = Span(72, row, row);
        var result = Extract(Measured(Text(72, 720, row) + "\n" + Rule(left, right, Under(720))));
        Assert.Empty(UnderlinedTexts(result));
        AssertNoDrawingLeft(result);
    }

    [Fact]
    public void Pdf_an_underline_under_one_text_of_a_show_is_no_rule_over_the_gap_that_follows_it()
    {
        // Only the characters the line lies under count: the gap after "Total" is not under it.
        const string row = "Total                                        amount";
        var (left, right) = Span(72, row, "Total");
        var result = Extract(Measured(Text(72, 720, row) + "\n" + Rule(left, right, Under(720))));
        Assert.Equal(["Total"], UnderlinedTexts(result));
        AssertNoDrawingLeft(result);
    }

    [Fact]
    public void Pdf_text_on_another_baseline_does_not_fill_the_gap_between_two_cells()
    {
        // A big title makes the page's largest text 24 points, so the baseline of the text 7.2 points above the
        // rule is within reach of it; 0.6 em above the rule, it is no text the rule underlines, and the gap it
        // sits over is a gap.
        var between = Span(96, "between", "between");
        var total = Span(between.Right + 3, "Total", "Total");
        var result = Extract(Measured(string.Join("\n",
            Text(72, 800, "Title", 24), Text(72, 720, "Item"), Text(96, 726, "between"), Text(between.Right + 3, 720, "Total"),
            Rule(72, total.Right, Under(720)))));
        Assert.Empty(UnderlinedTexts(result));
        AssertNoDrawingLeft(result);
    }

    [Fact]
    public void Pdf_an_underline_annotation_across_a_row_of_cells_is_somebodys_markup_and_underlines_them()
    {
        var header = Header(out var item, out _, out var total);
        var result = Extract(Measured(header, Markup("Underline", item.Left, total.Right, 720)));
        Assert.Equal(["Item", "Quantity", "Total"], UnderlinedTexts(result));
    }

    [Theory]
    [InlineData("Custom", true)]
    [InlineData("Times-Roman", false)]
    public void Pdf_missing_widths_leave_a_cell_gap_unknown_only_for_nonstandard_fonts(string font, bool underlined)
    {
        // Standard metrics place the middle cell even without /Widths, so the line is a row rule.
        // A custom font still leaves its gap unknown and the line stays an underline of all three.
        var middleFont = $"<< /Type /Font /Subtype /Type1 /BaseFont /{font} >>";
        var total = Span(400, "Total", "Total");
        var content = string.Join("\n", Text(72, 720, "Item"), "BT /F2 12 Tf 1 0 0 1 250 720 Tm (Quantity) Tj ET", Text(400, 720, "Total"),
            Rule(72, total.Right, Under(720)));
        var result = Extract(Page(content, "<< /Font << /F1 4 0 R /F2 5 0 R >> >>",
            new Dictionary<int, string> { [4] = SimpleFont, [5] = middleFont }));
        Assert.Equal(underlined ? new[] { "Item", "Quantity", "Total" } : Array.Empty<string>(), UnderlinedTexts(result));
    }

    [Fact]
    public async Task Pdf_lines_drawn_by_hand_in_a_sheet_are_written_struck_or_underlined_and_arrows_stay_drawings()
    {
        // The layout of LibreOffice's PDF of an Excel sheet with lines drawn on it.
        var planned = Span(207, "planned", "planned");
        var dropped = Span(207, "dropped", "dropped");
        var ito = Span(131, "Ito", "Ito");
        var (markdown, result) = await Export(Measured(string.Join("\n",
            Text(55, 735, "Design"), Text(131, 735, "Sato"), Text(207, 735, "done"),
            Text(55, 715, "Build"), Text(131, 715, "Ito"), Text(207, 715, "active"),
            Text(55, 695, "Old step"), Text(131, 695, "Kato"), Text(207, 695, "dropped"),
            Text(55, 676, "Test"), Text(131, 676, "Mori"), Text(207, 676, "planned"),
            Hand(132, ito.Right + 2.4 * 12, 715 + 0.4 * 12),                                     // through "Ito"
            Hand(55, dropped.Right + 2.6 * 12, 695 + 0.47 * 12), Hand(55, dropped.Right + 2.6 * 12, 695 + 0.34 * 12),   // a double line over the row
            Hand(208, planned.Right + 11, 676 - 0.09 * 12),                                       // under "planned"
            Hand(286, 494, 740),                                                                  // over empty cells
            Hand(206, 306.7, 715 + 0.4 * 12), "312.3 719.8 m 306.3 716.8 l 306.3 722.8 l 312.3 719.8 l h f")));    // an arrow through "active"
        Assert.Contains("~~Ito~~", markdown, StringComparison.Ordinal);
        Assert.Contains("~~Old step~~", markdown, StringComparison.Ordinal);
        Assert.Contains("~~Kato~~", markdown, StringComparison.Ordinal);
        Assert.Contains("~~dropped~~", markdown, StringComparison.Ordinal);
        Assert.Contains("<u>planned</u>", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("~~active~~", markdown, StringComparison.Ordinal);
        // The over-empty-cells line, the shaft of the arrow and its head are what the reader is still told to compare.
        Assert.Equal(3, markdown.Split('\n').Count(line => line.StartsWith("- パス:", StringComparison.Ordinal)));
        Assert.Equal(1, ExportSummaryBuilder.Build(result.Graph, result.Diagnostics).ReviewPages);
    }

    [Fact]
    public void Pdf_a_link_area_a_little_above_the_underline_still_holds_it_and_one_further_up_does_not()
    {
        // A link whose rectangle is tight around the text ends a little above an underline under the baseline.
        const string text = "See the terms now.";
        var (left, right) = Span(72, text, "the terms");
        var content = Text(72, 720, text) + "\n" + Rule(left, right, Under(720));
        string Area(double bottom) => $"<< /Type /Annot /Subtype /Link /Rect [{N(left)} {N(bottom)} {N(right)} 730] /A << /S /URI /URI (https://example.com/) >> >>";
        Assert.Null(Assert.Single(Lines(Measured(content, Area(Under(720) + 1.5)))).Underlined);
        Assert.Equal([new TextRange(4, 13)], Assert.Single(Lines(Measured(content, Area(Under(720) + 3.5)))).Underlined);
    }

    [Fact]
    public void Pdf_a_row_of_ticks_standing_on_end_under_text_is_no_dotted_line()
    {
        // Ruler marks, half a point wide and two tall, every three points.
        var result = Extract(Measured(string.Join("\n", Text(72, 720, "Ruler marks here"),
            string.Join("\n", Enumerable.Range(0, 12).Select(index => $"{N(73 + index * 3)} 717 0.5 2 re f")))));
        Assert.Null(Assert.Single(result.Pages[0].Regions).Underlined);
        Assert.Equal(12, Drawings(result));
    }

    [Fact]
    public void Pdf_a_bar_that_hangs_more_than_half_an_em_under_the_baseline_is_no_underline()
    {
        // Chrome's wavy bar is 0.3 to 0.5 em tall; one 0.65 em tall under the text is a block.
        var (left, right) = Span(72, "A wavy line here.", "wavy line");
        var result = Extract(Measured(Text(72, 720, "A wavy line here.") + "\n" + $"{N(left)} 712.2 {N(right - left)} 7.8 re f"));
        Assert.Null(Assert.Single(result.Pages[0].Regions).Underlined);
        Assert.Equal(1, Drawings(result));
    }
}
