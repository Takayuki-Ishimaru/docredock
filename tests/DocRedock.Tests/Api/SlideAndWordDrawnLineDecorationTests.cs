using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Docx;
using DocRedock.Formats.OpenXml.Pptx;
using DocRedock.Formats.OpenXml.Xlsx;
using DocRedock.Markdown;
using DocRedock.Render;

namespace DocRedock.Tests.Api;

/// <summary>
/// A line drawn over slide or Word text is text formatting, not a drawing: people cancel text by drawing a straight line
/// through it (often red, sometimes double) and emphasize text by drawing a line under it. Such a line strikes the text it
/// crosses (~~...~~ in the Markdown) or underlines it (&lt;u&gt;), leaves the drawings so it is no overlay symbol, diagram
/// edge or unresolved line, and a line over only part of the text is reported for review instead of guessed. A line that
/// joins shapes, has an arrowhead, or lies over no text, and a line in Word whose place against the text is not known
/// without page layout, stays the drawing it was.
///
/// The text is placed from what the file gives, as in the worksheet reader: in a slide a text frame (or a table cell) with
/// its insets, anchor and sizes, whose first baseline lies 0.97 em under the top of its first line, a line being 1.2 em
/// high; in Word a paragraph of one line, the baseline 0.88 em under its top. A line decorates in the zones of
/// PdfTextDecorations: a strikethrough 0.15 to 0.5 em above the baseline, an underline from 0.35 em below it to 0.15 em above.
/// Unless a test says otherwise, a slide here has a 24 pt text box at (72 pt, 100 pt) with no insets and no wrapping, top
/// anchored: the baseline is 123.3 pt down the slide, a strikethrough lies at about 116 pt, an underline at about 126.5 pt, and
/// "Old price 1000 yen" is estimated at 201.6 pt (72 to 273.6 pt). A Word document has 12 pt text in a 468 pt column: the
/// baseline is 10.6 pt under the paragraph's top, a strikethrough about 7 pt under it, an underline about 12 pt under it, and
/// the same text is estimated at 100.8 pt.
/// </summary>
public sealed partial class SlideAndWordDrawnLineDecorationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-drawn-slide-word-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);
    public void Dispose() => Directory.Delete(root, true);

    private const string StrikeY = "116";
    private const string UnderlineY = "126.5";

    private string Write(string name, IReadOnlyDictionary<string, string> parts)
    {
        var path = Path.Combine(root, name);
        // A test may build several variants under one name.
        if (File.Exists(path)) File.Delete(path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (entry, text) in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        return path;
    }

    private async Task<(string Markdown, ReadableDocumentExportResult Result)> Export(string source)
    {
        var output = Path.Combine(root, Path.GetFileNameWithoutExtension(source) + ".md");
        var result = await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, output, EnableOcr: false));
        return ((await File.ReadAllTextAsync(output)).Replace("\r\n", "\n", StringComparison.Ordinal), result);
    }

    private static long Emu(double points) => (long)Math.Round(points * 12700);

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------------------------------------------- PowerPoint

    // `defaults` is what the presentation says after its slide list (its default text style, say).
    private string Presentation(string name, string shapes, string defaults = "") => Write(name, new Dictionary<string, string>
    {
        ["[Content_Types].xml"] = """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
            </Types>
            """,
        ["_rels/.rels"] = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>
            </Relationships>
            """,
        ["ppt/presentation.xml"] = $"""
            <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                            xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:sldIdLst><p:sldId id="256" r:id="rId1"/></p:sldIdLst>{defaults}
            </p:presentation>
            """,
        ["ppt/_rels/presentation.xml.rels"] = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="slide" Target="slides/slide1.xml"/>
            </Relationships>
            """,
        ["ppt/slides/slide1.xml"] = $"""
            <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                   xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:cSld><p:spTree>{shapes}</p:spTree></p:cSld>
            </p:sld>
            """,
    });

    private const string FrameLess = " wrap=\"none\" lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"0\"";

    private static string SlideParagraph(string text, int size = 24, string runProperties = "", string properties = "", string children = "") =>
        $"<a:p>{(properties.Length == 0 ? "" : "<a:pPr " + properties + "/>")}<a:r><a:rPr lang=\"en-US\" sz=\"{size * 100}\"{runProperties}>{children}</a:rPr><a:t>{text}</a:t></a:r></a:p>";

    // A text box of the given paragraphs (the default text is 24 pt in a box with no insets, top anchored).
    private static string TextBox(int id, double x, double y, double width, double height, string paragraphs, string body = FrameLess + " anchor=\"t\"",
        string bodyChildren = "", string listStyle = "") =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Text {id}\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr>" +
        $"<p:spPr><a:xfrm><a:off x=\"{Emu(x)}\" y=\"{Emu(y)}\"/><a:ext cx=\"{Emu(width)}\" cy=\"{Emu(height)}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></p:spPr>" +
        $"<p:txBody><a:bodyPr{body}>{bodyChildren}</a:bodyPr><a:lstStyle>{listStyle}</a:lstStyle>{paragraphs}</p:txBody></p:sp>";

    private static string DefaultText(int id = 2, string text = "Old price 1000 yen") => TextBox(id, 72, 100, 400, 40, SlideParagraph(text));

    private const string RedLine = "<a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill>";

    // A line the style of the shape paints (a:lnRef), as PowerPoint writes the lines it draws.
    private const string LineStyle = "<p:style><a:lnRef idx=\"1\"><a:schemeClr val=\"accent1\"/></a:lnRef></p:style>";

    /// <summary>A line from (left, y) to (right, y + rise) points on the slide.</summary>
    private static string SlideLine(int id, double left, double y, double right, double rise = 0, string ends = "", string connection = "", string outline = RedLine,
        string preset = "line", string attributes = "", string style = "", int weight = 19050) =>
        $"<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"{id}\" name=\"Line {id}\"{attributes}/><p:cNvCxnSpPr>{connection}</p:cNvCxnSpPr><p:nvPr/></p:nvCxnSpPr>" +
        $"<p:spPr><a:xfrm><a:off x=\"{Emu(left)}\" y=\"{Emu(y)}\"/><a:ext cx=\"{Emu(right - left)}\" cy=\"{Emu(rise)}\"/></a:xfrm>" +
        $"<a:prstGeom prst=\"{preset}\"><a:avLst/></a:prstGeom><a:ln w=\"{weight}\">{outline}{ends}</a:ln></p:spPr>{style}</p:cxnSp>";

    /// <summary>A line written as a plain shape (p:sp with the line preset), turned by <paramref name="degrees"/> about its centre.</summary>
    private static string SlideShapeLine(int id, double left, double y, double width, double degrees = 0) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Line {id}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>" +
        $"<p:spPr><a:xfrm{(degrees == 0 ? "" : $" rot=\"{(long)Math.Round(degrees * 60000)}\"")}><a:off x=\"{Emu(left)}\" y=\"{Emu(y)}\"/><a:ext cx=\"{Emu(width)}\" cy=\"0\"/></a:xfrm>" +
        $"<a:prstGeom prst=\"line\"><a:avLst/></a:prstGeom><a:ln w=\"19050\">{RedLine}</a:ln></p:spPr></p:sp>";

    /// <summary>A rectangle with no text: a thin bar when it is a few points high, a block of colour otherwise.</summary>
    private static string Rectangle(int id, double x, double y, double width, double height, string fill = "<a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill>",
        string outline = "<a:ln><a:noFill/></a:ln>", double degrees = 0, string style = "") =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Rectangle {id}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>" +
        $"<p:spPr><a:xfrm{(degrees == 0 ? "" : $" rot=\"{(long)Math.Round(degrees * 60000)}\"")}><a:off x=\"{Emu(x)}\" y=\"{Emu(y)}\"/><a:ext cx=\"{Emu(width)}\" cy=\"{Emu(height)}\"/></a:xfrm>" +
        $"<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>{fill}{outline}</p:spPr>{style}</p:sp>";

    private static string Box(int id, double x, double y, double width, double height, string text) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Box {id}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>" +
        $"<p:spPr><a:xfrm><a:off x=\"{Emu(x)}\" y=\"{Emu(y)}\"/><a:ext cx=\"{Emu(width)}\" cy=\"{Emu(height)}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" +
        $"<a:solidFill><a:srgbClr val=\"DDDDDD\"/></a:solidFill><a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill></a:ln></p:spPr>" +
        $"<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"en-US\" sz=\"1800\"/><a:t>{text}</a:t></a:r></a:p></p:txBody></p:sp>";

    // A table of two columns of 144 pt and two rows of 36 pt at (72 pt, 200 pt), 18 pt text with the default margins
    // (7.2 pt, 3.6 pt) and anchored at the top: the baseline of the second row is 257.1 pt down the slide, a strikethrough
    // lies at about 251.7 pt, and "Old plan" is estimated at 65.7 pt from 79.2 pt.
    private static string SlideTable(string firstCell, string secondRowFirstCell, string secondRowSecondCell = "800") =>
        "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"4\" name=\"Table\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>" +
        $"<p:xfrm><a:off x=\"{Emu(72)}\" y=\"{Emu(200)}\"/><a:ext cx=\"{Emu(288)}\" cy=\"{Emu(72)}\"/></p:xfrm><a:graphic><a:graphicData><a:tbl>" +
        $"<a:tblGrid><a:gridCol w=\"{Emu(144)}\"/><a:gridCol w=\"{Emu(144)}\"/></a:tblGrid>" +
        $"<a:tr h=\"{Emu(36)}\">{Cell(firstCell)}{Cell("Amount")}</a:tr><a:tr h=\"{Emu(36)}\">{Cell(secondRowFirstCell)}{Cell(secondRowSecondCell)}</a:tr>" +
        "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";

    // A table cell of 18 pt text, or of the given paragraphs; `cellProperties` are the attributes of its a:tcPr (a margin, the anchor).
    private static string Cell(string text, string cellProperties = "", string? paragraphs = null) =>
        $"<a:tc><a:txBody><a:bodyPr/><a:lstStyle/>{paragraphs ?? $"<a:p><a:r><a:rPr lang=\"en-US\" sz=\"1800\"/><a:t>{text}</a:t></a:r></a:p>"}</a:txBody><a:tcPr{cellProperties}/></a:tc>";

    // A table at (x, y) with the given widths of columns and heights of rows, and one row of cells (Cell) for each row.
    private static string GridTable(double x, double y, double[] columns, double[] rows, params string[][] cells)
    {
        var xml = new StringBuilder("<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"4\" name=\"Table\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>");
        xml.Append($"<p:xfrm><a:off x=\"{Emu(x)}\" y=\"{Emu(y)}\"/><a:ext cx=\"{Emu(columns.Sum())}\" cy=\"{Emu(rows.Sum())}\"/></p:xfrm><a:graphic><a:graphicData><a:tbl><a:tblGrid>");
        foreach (var width in columns) xml.Append($"<a:gridCol w=\"{Emu(width)}\"/>");
        xml.Append("</a:tblGrid>");
        for (var row = 0; row < rows.Length; row++) xml.Append($"<a:tr h=\"{Emu(rows[row])}\">").Append(string.Concat(cells[row])).Append("</a:tr>");
        return xml.Append("</a:tbl></a:graphicData></a:graphic></p:graphicFrame>").ToString();
    }

    private static PptxExtractionResult ExtractSlides(string source)
    {
        using var stream = File.OpenRead(source);
        return new PptxAdapter().Extract(stream);
    }

    private static DocumentNode ShapeNode(PptxExtractionResult extraction, string name) =>
        extraction.Graph.Nodes.Single(node => node.Extensions is { } extensions && extensions.TryGetValue("shape_name", out var value) && value.GetString() == name);

    private static string? Extension(DocumentNode node, string key) =>
        node.Extensions is { } extensions && extensions.TryGetValue(key, out var value) ? value.GetRawText() : null;

    // The same graph with the readable-only ranges taken off its nodes: the round-trip projection must not notice.
    private static DocumentGraph WithoutRanges(DocumentGraph graph) => graph with
    {
        Partitions = graph.Partitions.Select(partition => partition with
        {
            Nodes = partition.Nodes.Select(node => node with
            {
                Extensions = node.Extensions?.Where(item => item.Key is not ("strike_ranges" or "underline_ranges")).ToDictionary(item => item.Key, item => item.Value),
            }).ToArray(),
        }).ToArray(),
    };

    // What the extraction made of a slide: whether a line is still a drawing the visual inference reports.
    private static bool HasUnresolvedLine(PptxExtractionResult extraction) =>
        extraction.Warnings.Any(warning => warning.StartsWith("VisualConnectorUnresolved", StringComparison.Ordinal));

    // ---- strikethrough ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_line_through_a_text_box_strikes_it_and_is_no_diagram_edge_or_review()
    {
        var source = Presentation("single.pptx", DefaultText() + SlideLine(5, 70, 116, 280));
        var (markdown, result) = await Export(source);
        Assert.Contains("\n~~Old price 1000 yen~~\n", markdown, StringComparison.Ordinal);
        // The line is formatting only: no diagram of an unresolved line, no review, nothing to look at again.
        Assert.DoesNotContain("mermaid", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("未解決", markdown, StringComparison.Ordinal);
        Assert.Empty(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages);

        var extraction = ExtractSlides(source);
        var node = ShapeNode(extraction, "Text 2");
        Assert.Equal("[[0,18]]", Extension(node, "strike_ranges"));
        Assert.False(HasUnresolvedLine(extraction));
        // The record of the slide still lists the line: the facts of the file are kept.
        Assert.Contains(Assert.Single(extraction.Slides).Shapes, shape => shape.ShapeId == "5");
    }

    [Fact]
    public async Task A_red_double_line_strikes_the_text_once()
    {
        // 見え消し: two red lines 3 pt apart through the text, as a person draws a cancelled entry.
        var source = Presentation("double.pptx", DefaultText() + SlideLine(5, 70, 114.5, 280) + SlideLine(6, 70, 117.5, 280));
        var (markdown, _) = await Export(source);
        Assert.Contains("\n~~Old price 1000 yen~~\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("~~~~", markdown, StringComparison.Ordinal);
        Assert.False(HasUnresolvedLine(ExtractSlides(source)));

        // Two lines 1 pt apart reach within a touch of each other's ends: each is the other line of a double line all the same, not a
        // line that ends on another one.
        var close = Presentation("doubleclose.pptx", DefaultText() + SlideLine(5, 70, 115.5, 280) + SlideLine(6, 70, 116.5, 280));
        var (closeMarkdown, _) = await Export(close);
        Assert.Contains("\n~~Old price 1000 yen~~\n", closeMarkdown, StringComparison.Ordinal);
        Assert.False(HasUnresolvedLine(ExtractSlides(close)));
    }

    [Fact]
    public async Task A_line_as_wide_as_the_text_box_from_edge_to_edge_strikes_its_text()
    {
        // The line starts and ends on the box's own frame (72 to 472 pt): the box is the text it strikes, not another shape.
        var source = Presentation("edges.pptx", DefaultText() + SlideLine(5, 72, 116, 472));
        var (markdown, _) = await Export(source);
        Assert.Contains("\n~~Old price 1000 yen~~\n", markdown, StringComparison.Ordinal);

        // The same when the box has a fill and an outline of its own: its frame is the text's, not another shape's to end on.
        var painted = DefaultText().Replace("<a:noFill/></p:spPr>",
            "<a:solidFill><a:srgbClr val=\"DDDDDD\"/></a:solidFill><a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill></a:ln></p:spPr>", StringComparison.Ordinal);
        Assert.Contains("\n~~Old price 1000 yen~~\n", (await Export(Presentation("edgespainted.pptx", painted + SlideLine(5, 72, 116, 472)))).Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_through_one_paragraph_of_several_strikes_that_paragraph()
    {
        // Second paragraph: its baseline is 100 + 28.8 (the first line) + 0.97 * 24 under the box's top.
        var paragraphs = SlideParagraph("First line") + SlideParagraph("Old price 1000 yen") + SlideParagraph("Third line");
        var source = Presentation("paragraphs.pptx", TextBox(2, 72, 100, 400, 120, paragraphs) + SlideLine(5, 70, 144.9, 280));
        var (markdown, _) = await Export(source);
        Assert.Contains("First line\n\n~~Old price 1000 yen~~\n\nThird line", markdown, StringComparison.Ordinal);
        Assert.Equal("[[11,29]]", Extension(ShapeNode(ExtractSlides(source), "Text 2"), "strike_ranges"));
    }

    [Theory]
    // The baseline is 100 + 0.97 * 24 = 123.28 pt down the slide; the zones are measured from it in ems of 24 pt (one em is 24 pt).
    [InlineData(105.0, "none")]            // 0.76 em above the baseline: an overline
    [InlineData(108.0, "none")]            // 0.64 em above it: over the letters
    [InlineData(110.5, "strike")]          // 0.53 em: the estimate of the baseline may be that far off
    [InlineData(116.0, "strike")]          // 0.30 em
    [InlineData(119.5, "strike")]          // 0.16 em: still above the zone where it turns into an underline
    [InlineData(120.5, "underline")]       // 0.12 em
    [InlineData(126.5, "underline")]       // 0.13 em below the baseline
    [InlineData(131.0, "underline")]       // 0.32 em below it
    [InlineData(133.5, "none")]            // 0.43 em below it: no longer an underline
    public void A_line_decorates_only_at_the_height_of_a_strikethrough_or_an_underline(double y, string expected)
    {
        var source = Presentation("zones.pptx", DefaultText() + SlideLine(5, 70, y, 280));
        var node = ShapeNode(ExtractSlides(source), "Text 2");
        var actual = Extension(node, "strike_ranges") is not null ? "strike" : Extension(node, "underline_ranges") is not null ? "underline" : "none";
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task An_arrow_is_not_a_decoration()
    {
        // The same line with a head: it points at something, and the text stays unmarked while the arrow stays a drawing.
        var source = Presentation("arrow.pptx", DefaultText() + SlideLine(5, 70, 116, 280, ends: "<a:tailEnd type=\"triangle\"/>"));
        var (markdown, _) = await Export(source);
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);
        Assert.True(HasUnresolvedLine(ExtractSlides(source)));
        // A head or tail of type none is no arrowhead.
        var plain = Presentation("noheads.pptx", DefaultText() + SlideLine(5, 70, 116, 280, ends: "<a:headEnd type=\"none\"/><a:tailEnd type=\"none\"/>"));
        Assert.Contains("\n~~Old price 1000 yen~~\n", (await Export(plain)).Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connector_that_joins_two_shapes_is_unchanged()
    {
        // Boxes A and B joined by a connector glued to both, and a free line from one to the other that runs through the
        // text of a box at the height of a strikethrough: neither is a decoration.
        var boxes = Box(10, 400, 100, 80, 40, "A") + Box(11, 400, 200, 80, 40, "B");
        var glued = SlideLine(6, 440, 140, 440, rise: 60, connection: "<a:stCxn id=\"10\" idx=\"2\"/><a:endCxn id=\"11\" idx=\"0\"/>");
        var source = Presentation("glued.pptx", DefaultText() + boxes + glued);
        var (markdown, _) = await Export(source);
        Assert.Contains("v_10 --> v_11", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);

        // A strike line that is glued to a box at one end is a connector, however it runs.
        var onBox = Presentation("gluedstrike.pptx", DefaultText() + Box(10, 280, 100, 80, 40, "A") +
            SlideLine(6, 70, 116, 280, connection: "<a:endCxn id=\"10\" idx=\"1\"/>"));
        Assert.DoesNotContain("~~", (await Export(onBox)).Markdown, StringComparison.Ordinal);

        // The glue says it, not the frame it is glued to: that frame may be one nobody sees (no fill, no outline).
        var onUnseen = Presentation("glueunseen.pptx", DefaultText() + Rectangle(10, 280, 100, 80, 40, fill: "<a:noFill/>") +
            SlideLine(6, 70, 116, 280, connection: "<a:endCxn id=\"10\" idx=\"1\"/>"));
        Assert.Null(StrikeRanges(ExtractSlides(onUnseen)));
    }

    [Fact]
    public void A_line_whose_end_meets_another_shape_is_not_a_decoration()
    {
        // Nothing is glued, but the line (70 to 280 pt) ends on the edge of a box...
        var onBox = ExtractSlides(Presentation("touchbox.pptx", DefaultText() + Box(10, 280, 100, 80, 40, "A") + SlideLine(5, 70, 116, 280)));
        Assert.Null(Extension(ShapeNode(onBox, "Text 2"), "strike_ranges"));
        // ...a box 3 pt from its end is not met...
        var near = ExtractSlides(Presentation("nearbox.pptx", DefaultText() + Box(10, 283, 100, 80, 40, "A") + SlideLine(5, 70, 116, 280)));
        Assert.Equal("[[0,18]]", Extension(ShapeNode(near, "Text 2"), "strike_ranges"));
        // ...and neither is one nobody sees (no fill, no outline).
        var invisible = "<p:sp><p:nvSpPr><p:cNvPr id=\"10\" name=\"Frame\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"" + Emu(280) + "\" y=\"" + Emu(100) +
            "\"/><a:ext cx=\"" + Emu(80) + "\" cy=\"" + Emu(40) + "\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/><a:ln><a:noFill/></a:ln></p:spPr></p:sp>";
        var unseen = ExtractSlides(Presentation("unseen.pptx", DefaultText() + invisible + SlideLine(5, 70, 116, 280)));
        Assert.Equal("[[0,18]]", Extension(ShapeNode(unseen, "Text 2"), "strike_ranges"));
    }

    [Fact]
    public void A_line_that_paints_nothing_or_a_hidden_one_decorates_nothing()
    {
        var unpainted = ExtractSlides(Presentation("unpainted.pptx", DefaultText() + SlideLine(5, 70, 116, 280, outline: "<a:noFill/>")));
        Assert.Null(Extension(ShapeNode(unpainted, "Text 2"), "strike_ranges"));
        var hidden = ExtractSlides(Presentation("hidden.pptx", DefaultText() + SlideLine(5, 70, 116, 280, attributes: " hidden=\"1\"")));
        Assert.Null(Extension(ShapeNode(hidden, "Text 2"), "strike_ranges"));
    }

    [Fact]
    public void A_line_that_is_slanted_or_over_no_text_is_left_alone()
    {
        // 14 pt of rise in 210: a cancellation stroke, not a line through the text.
        var slanted = ExtractSlides(Presentation("slanted.pptx", DefaultText() + SlideLine(5, 70, 112, 280, rise: 14)));
        Assert.Null(Extension(ShapeNode(slanted, "Text 2"), "strike_ranges"));
        // A line beside the text, over none of it.
        var beside = ExtractSlides(Presentation("beside.pptx", DefaultText() + SlideLine(5, 300, 116, 420)));
        Assert.Null(Extension(ShapeNode(beside, "Text 2"), "strike_ranges"));
        // Over empty slide.
        var below = ExtractSlides(Presentation("below.pptx", DefaultText() + SlideLine(5, 70, 400, 280)));
        Assert.Null(Extension(ShapeNode(below, "Text 2"), "strike_ranges"));
    }

    [Fact]
    public void A_thin_bar_decorates_like_a_line_and_a_thick_one_does_not()
    {
        string Bar(double height) =>
            "<p:sp><p:nvSpPr><p:cNvPr id=\"5\" name=\"Bar\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>" +
            $"<p:spPr><a:xfrm><a:off x=\"{Emu(70)}\" y=\"{Emu(116 - height / 2)}\"/><a:ext cx=\"{Emu(210)}\" cy=\"{Emu(height)}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" +
            "<a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill><a:ln><a:noFill/></a:ln></p:spPr></p:sp>";
        Assert.Equal("[[0,18]]", Extension(ShapeNode(ExtractSlides(Presentation("thinbar.pptx", DefaultText() + Bar(1.2))), "Text 2"), "strike_ranges"));
        Assert.Null(Extension(ShapeNode(ExtractSlides(Presentation("thickbar.pptx", DefaultText() + Bar(10))), "Text 2"), "strike_ranges"));
        // A rectangle with text, however thin, is a label.
        var labelled = Bar(1.2).Replace("</p:spPr></p:sp>", "</p:spPr><p:txBody><a:bodyPr/><a:p><a:r><a:t>x</a:t></a:r></a:p></p:txBody></p:sp>", StringComparison.Ordinal);
        Assert.Null(Extension(ShapeNode(ExtractSlides(Presentation("labelled.pptx", DefaultText() + labelled)), "Text 2"), "strike_ranges"));
    }

    // ---- underline ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_line_under_the_text_underlines_it()
    {
        var source = Presentation("underline.pptx", DefaultText() + SlideLine(5, 70, double.Parse(UnderlineY, CultureInfo.InvariantCulture), 280,
            outline: "<a:solidFill><a:srgbClr val=\"0000FF\"/></a:solidFill>"));
        var (markdown, result) = await Export(source);
        Assert.Contains("\n<u>Old price 1000 yen</u>\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);
        Assert.Empty(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages);
        Assert.Equal("[[0,18]]", Extension(ShapeNode(ExtractSlides(source), "Text 2"), "underline_ranges"));
    }

    [Fact]
    public async Task A_double_underline_underlines_once_and_a_line_across_part_of_the_text_underlines_nothing_and_asks_for_no_review()
    {
        var doubled = Presentation("doubleunder.pptx", DefaultText() + SlideLine(5, 70, 125, 280) + SlideLine(6, 70, 128, 280));
        Assert.Contains("\n<u>Old price 1000 yen</u>\n", (await Export(doubled)).Markdown, StringComparison.Ordinal);

        // 20 pt of the 200 pt text: a partial underline marks nothing, and is no review.
        var partial = Presentation("partialunder.pptx", DefaultText() + SlideLine(5, 72, 126.5, 92));
        var (markdown, result) = await Export(partial);
        Assert.Contains("\nOld price 1000 yen\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<u>", markdown, StringComparison.Ordinal);
        Assert.Empty(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages);
        // The line is formatting all the same: it is no unresolved line.
        Assert.False(HasUnresolvedLine(ExtractSlides(partial)));
    }

    [Fact]
    public async Task Underline_and_strikethrough_on_the_same_text_nest_underline_outside()
    {
        var source = Presentation("both.pptx", DefaultText() + SlideLine(5, 70, 116, 280) + SlideLine(6, 70, 126.5, 280));
        var (markdown, _) = await Export(source);
        Assert.Contains("\n<u>~~Old price 1000 yen~~</u>\n", markdown, StringComparison.Ordinal);
    }

    // ---- strike over part of the text ---------------------------------------------------------------------------

    [Fact]
    public async Task A_line_through_part_of_the_text_marks_nothing_and_asks_for_review()
    {
        // 78 pt of the roughly 200 pt text: which characters are struck cannot be told.
        var source = Presentation("partial.pptx", DefaultText() + SlideLine(5, 72, 116, 150));
        var (markdown, result) = await Export(source);
        Assert.Contains("\nOld price 1000 yen\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);

        var extraction = ExtractSlides(source);
        var node = ShapeNode(extraction, "Text 2");
        Assert.Equal("true", Extension(node, ReviewExtensionKeys.StrikeUnplaced));
        Assert.Equal("true", Extension(node, ReadableMarkdownSerializer.SourceReviewRequiredExtension));
        Assert.Null(Extension(node, "strike_ranges"));
        Assert.False(HasUnresolvedLine(extraction));

        // One review element for the shape, on the slide, in the same words as every other format.
        Assert.Equal(1, ExportSummaryBuilder.Build(result.Graph, result.Diagnostics).ReviewPages);
        var page = Assert.Single(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages);
        Assert.Equal(DocumentFormatKind.Pptx, page.Format);
        Assert.Equal(ReviewElementKind.StrikethroughUnplaced, Assert.Single(page.Elements).Kind);
        Assert.Equal("スライド1：文字1件は、一部に取り消し線がありますが、どの文字が取り消されているかを確定できませんでした（取り消し線の印を付けずにMarkdownに含めています）。元のファイルで内容を確認してください。",
            ExportReviewText.DescribeJapanese(page));
        Assert.Equal("slide 1: 1 text item(s) with a strikethrough over part of them whose struck characters could not be determined; kept in the Markdown without the mark",
            ExportReviewText.DescribeEnglish(page));
    }

    [Fact]
    public void A_line_through_a_line_of_a_wrapped_paragraph_asks_for_review()
    {
        // A box 150 pt wide: the text, about 366 pt of it, wraps onto three lines (the baselines are 123.3 pt, 152.1 pt and 180.9 pt down).
        var wrapped = TextBox(2, 72, 100, 150, 80, SlideParagraph("Old price 1000 yen with a long tail"), " wrap=\"square\" lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"0\"");
        var first = ExtractSlides(Presentation("wrapped.pptx", wrapped + SlideLine(5, 70, 116, 220)));
        Assert.Equal("true", Extension(ShapeNode(first, "Text 2"), ReviewExtensionKeys.StrikeUnplaced));
        Assert.Null(Extension(ShapeNode(first, "Text 2"), "strike_ranges"));
        // A line through the second of them is a strike over part of the text as well.
        var second = ExtractSlides(Presentation("wrappedsecond.pptx", wrapped + SlideLine(5, 70, 152.1 - 7.2, 220)));
        Assert.Equal("true", Extension(ShapeNode(second, "Text 2"), ReviewExtensionKeys.StrikeUnplaced));
    }

    [Fact]
    public void A_line_across_part_of_text_the_runs_already_strike_needs_no_review()
    {
        var struck = TextBox(2, 72, 100, 400, 40, SlideParagraph("Old price 1000 yen", runProperties: " strike=\"sngStrike\""));
        var extraction = ExtractSlides(Presentation("alreadystruck.pptx", struck + SlideLine(5, 72, 116, 150)));
        Assert.Null(Extension(ShapeNode(extraction, "Text 2"), ReviewExtensionKeys.StrikeUnplaced));
    }

    // ---- tables -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_line_through_a_table_cell_strikes_it_and_is_no_overlay_symbol()
    {
        var source = Presentation("cell.pptx", SlideTable("Item", "Old plan") + SlideLine(5, 76, 251.7, 150));
        var (markdown, _) = await Export(source);
        Assert.Contains("| Item | Amount |\n| --- | --- |\n| ~~Old plan~~ | 800 |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("─", markdown, StringComparison.Ordinal);
        var extraction = ExtractSlides(source);
        Assert.Equal("[[1,0,[[0,8]]]]", Extension(ShapeNode(extraction, "Table"), "strike_cells"));
        Assert.Null(Extension(ShapeNode(extraction, "Table"), "table_overlays"));
    }

    [Fact]
    public async Task A_line_through_a_cell_that_a_table_overlay_could_cover_strikes_it_and_one_over_the_letters_is_still_a_symbol()
    {
        // At the middle of the row, over the letters, the same line is the line symbol of the table overlay it always was.
        var over = Presentation("celloverline.pptx", SlideTable("Item", "Old plan") + SlideLine(5, 76, 243, 150));
        Assert.Contains("Old plan<br>─", (await Export(over)).Markdown, StringComparison.Ordinal);
        var through = Presentation("cellthrough.pptx", SlideTable("Item", "Old plan") + SlideLine(5, 76, 251.7, 150));
        Assert.DoesNotContain("─", (await Export(through)).Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_under_a_table_cell_underlines_it_and_part_of_a_cell_asks_for_review()
    {
        var under = Presentation("cellunder.pptx", SlideTable("Item", "Old plan") + SlideLine(5, 76, 260.5, 150));
        Assert.Contains("| <u>Old plan</u> | 800 |", (await Export(under)).Markdown, StringComparison.Ordinal);

        var partial = Presentation("cellpartial.pptx", SlideTable("Item", "Old plan") + SlideLine(5, 79, 251.7, 100));
        var (markdown, result) = await Export(partial);
        Assert.Contains("| Old plan | 800 |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);
        var node = ShapeNode(ExtractSlides(partial), "Table");
        Assert.Equal("true", Extension(node, ReviewExtensionKeys.StrikeUnplaced));
        Assert.Equal(ReviewElementKind.StrikethroughUnplaced, Assert.Single(Assert.Single(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages).Elements).Kind);
    }

    [Fact]
    public void A_line_through_a_cell_keeps_the_ranges_the_cell_runs_already_had()
    {
        var table = SlideTable("Item", "Old plan").Replace("<a:t>Old plan</a:t>", "<a:t>Old plan</a:t>", StringComparison.Ordinal)
            .Replace("<a:tr h=\"" + Emu(36) + "\"><a:tc><a:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"en-US\" sz=\"1800\"/><a:t>Item</a:t>",
                "<a:tr h=\"" + Emu(36) + "\"><a:tc><a:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"en-US\" sz=\"1800\" strike=\"sngStrike\"/><a:t>Item</a:t>", StringComparison.Ordinal);
        var extraction = ExtractSlides(Presentation("cellruns.pptx", table + SlideLine(5, 76, 251.7, 150)));
        Assert.Equal("[[0,0,[[0,4]]],[1,0,[[0,8]]]]", Extension(ShapeNode(extraction, "Table"), "strike_cells"));
    }

    // ---- the geometry -------------------------------------------------------------------------------------------

    [Theory]
    // A box 120 pt high at 100 pt with the default insets (3.6 pt) and one line of 24 pt text, 28.8 pt high, whose baseline lies 23.28 pt under
    // the top of the line: at the top the line starts at 103.6 pt, at the middle at 145.5 pt, at the bottom at 187.6 pt.
    [InlineData("t", 126.88)]
    [InlineData("ctr", 168.78)]
    [InlineData("b", 210.88)]
    public void The_baseline_follows_the_anchor_of_the_frame(string anchor, double baseline)
    {
        var box = TextBox(2, 72, 100, 400, 120, SlideParagraph("Old price 1000 yen"), $" wrap=\"none\" anchor=\"{anchor}\"");
        // A strikethrough 0.3 em (7.2 pt) above the baseline.
        Assert.Equal("[[0,18]]", Extension(ShapeNode(ExtractSlides(Presentation("anchors.pptx", box + SlideLine(5, 70, baseline - 7.2, 280))), "Text 2"), "strike_ranges"));
        // Moved down by 0.75 em or up by 0.9 em the same line is no strikethrough of this text.
        foreach (var shift in new[] { -22.0, 18.0 })
            Assert.Null(Extension(ShapeNode(ExtractSlides(Presentation("anchorsmoved.pptx", box + SlideLine(5, 70, baseline - 7.2 + shift, 280))), "Text 2"), "strike_ranges"));
    }

    [Fact]
    public void The_baseline_follows_the_spacing_of_the_lines_and_the_space_before_paragraphs()
    {
        // 90 % spacing: the first line is 0.9 * 28.8 = 25.9 pt high and its baseline 25.9 - 0.23 * 24 = 20.4 pt under the box's top.
        var spaced = TextBox(2, 72, 100, 400, 120, SlideParagraph("Old price 1000 yen", properties: "") .Replace("<a:p>", "<a:p><a:pPr><a:lnSpc><a:spcPct val=\"90000\"/></a:lnSpc></a:pPr>", StringComparison.Ordinal));
        Assert.Equal("[[0,18]]", Extension(ShapeNode(ExtractSlides(Presentation("spacing.pptx", spaced + SlideLine(5, 70, 120.4 - 7.2, 280))), "Text 2"), "strike_ranges"));
        // Space before the second paragraph (10 pt) moves its baseline down by that much, and the first paragraph's not at all.
        var before = TextBox(2, 72, 100, 400, 120,
            SlideParagraph("First line") + SlideParagraph("Old price 1000 yen").Replace("<a:p>", "<a:p><a:pPr><a:spcBef><a:spcPts val=\"1000\"/></a:spcBef></a:pPr>", StringComparison.Ordinal));
        var second = 100 + 28.8 + 10 + 0.97 * 24 - 7.2;
        Assert.Equal("[[11,29]]", Extension(ShapeNode(ExtractSlides(Presentation("before.pptx", before + SlideLine(5, 70, second, 280))), "Text 2"), "strike_ranges"));
        Assert.Null(Extension(ShapeNode(ExtractSlides(Presentation("beforenot.pptx", before + SlideLine(5, 70, second - 10, 280))), "Text 2"), "strike_ranges"));
    }

    [Fact]
    public void The_extent_of_the_text_follows_its_alignment_and_its_bullet()
    {
        // Right aligned in a 400 pt box: the text ends at 472 pt (it is estimated 201.6 pt wide, so it starts at 270.4 pt).
        var right = TextBox(2, 72, 100, 400, 40, SlideParagraph("Old price 1000 yen", properties: "algn=\"r\""));
        Assert.Equal("[[0,18]]", Extension(ShapeNode(ExtractSlides(Presentation("right.pptx", right + SlideLine(5, 268, 116, 475))), "Text 2"), "strike_ranges"));
        // A line over the left end of the box is beside the text.
        Assert.Null(Extension(ShapeNode(ExtractSlides(Presentation("rightbeside.pptx", right + SlideLine(5, 72, 116, 200))), "Text 2"), "strike_ranges"));
        // A bullet with a 27 pt hanging indent: the text starts 27 pt in.
        var bullet = TextBox(2, 72, 100, 400, 40, SlideParagraph("Old price 1000 yen", properties: "marL=\"" + Emu(27) + "\" indent=\"" + Emu(-27) + "\"", children: ""))
            .Replace("<a:pPr marL=\"" + Emu(27) + "\" indent=\"" + Emu(-27) + "\"/>", "<a:pPr marL=\"" + Emu(27) + "\" indent=\"" + Emu(-27) + "\"><a:buChar char=\"•\"/></a:pPr>", StringComparison.Ordinal);
        Assert.Equal("[[0,18]]", Extension(ShapeNode(ExtractSlides(Presentation("bullet.pptx", bullet + SlideLine(5, 97, 116, 305))), "Text 2"), "strike_ranges"));
    }

    [Fact]
    public void Text_that_is_turned_vertical_or_in_columns_is_not_placed()
    {
        foreach (var body in new[] { FrameLess + " vert=\"vert\"", FrameLess + " rot=\"5400000\"", FrameLess + " numCol=\"2\"", FrameLess + " anchorCtr=\"1\"" })
        {
            var source = Presentation("notplaced.pptx", TextBox(2, 72, 100, 400, 40, SlideParagraph("Old price 1000 yen"), body) + SlideLine(5, 70, 116, 280));
            Assert.Null(Extension(ShapeNode(ExtractSlides(source), "Text 2"), "strike_ranges"));
        }
    }

    [Fact]
    public void A_placeholder_takes_its_place_and_sizes_from_the_layout_and_master()
    {
        // A title whose shape says nothing of its position or size: the layout gives its frame (72, 60, 576 x 100 pt, anchored at the
        // middle), the master its size (44 pt). No insets are set, so 3.6 pt on each side leave 92.8 pt, and the line of 52.8 pt lies from
        // 60 + 3.6 + (92.8 - 52.8) / 2 = 83.6 pt: its baseline is 83.6 + 0.97 * 44 = 126.28 pt down.
        Dictionary<string, string> Title(double lineY) => new()
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>",
            ["_rels/.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>",
            ["ppt/presentation.xml"] = "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\"/></p:sldIdLst></p:presentation>",
            ["ppt/_rels/presentation.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"slide\" Target=\"slides/slide1.xml\"/></Relationships>",
            ["ppt/slides/_rels/slide1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/></Relationships>",
            ["ppt/slideLayouts/_rels/slideLayout1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>",
            ["ppt/slideLayouts/slideLayout1.xml"] = "<p:sldLayout xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree>" +
                "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"" + Emu(72) + "\" y=\"" + Emu(60) + "\"/><a:ext cx=\"" + Emu(576) + "\" cy=\"" + Emu(100) +
                "\"/></a:xfrm></p:spPr><p:txBody><a:bodyPr anchor=\"ctr\"/><a:lstStyle/><a:p><a:endParaRPr lang=\"en-US\"/></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sldLayout>",
            ["ppt/slideMasters/slideMaster1.xml"] = "<p:sldMaster xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree/></p:cSld>" +
                "<p:txStyles><p:titleStyle><a:lvl1pPr algn=\"l\"><a:defRPr sz=\"4400\"/></a:lvl1pPr></p:titleStyle><p:bodyStyle/><p:otherStyle/></p:txStyles></p:sldMaster>",
            ["ppt/slides/slide1.xml"] = "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree>" +
                "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title 1\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"en-US\"/><a:t>Old price 1000 yen</a:t></a:r></a:p></p:txBody></p:sp>" +
                SlideLine(5, 70, lineY, 400) + "</p:spTree></p:cSld></p:sld>",
        };
        string Kind(string name, double lineY)
        {
            var node = ShapeNode(ExtractSlides(Write(name, Title(lineY))), "Title 1");
            return Extension(node, "strike_ranges") is not null ? "strike" : Extension(node, "underline_ranges") is not null ? "underline" : "none";
        }
        // 0.3 em above the baseline is a strikethrough of the 44 pt text the master sizes (0.49 em above it, 104 pt down, is one too, and with
        // the default size of 18 pt it would be 0.7 em above the baseline of that text: none).
        Assert.Equal("strike", Kind("placeholder.pptx", 126.28 - 0.3 * 44));
        Assert.Equal("strike", Kind("placeholdersize.pptx", 104));
        // The layout anchors the text at the middle of the frame: at the top its baseline would be 106.28 pt down, and this line, 113.08 pt down,
        // would be 0.15 em under it.
        Assert.Equal("strike", Kind("placeholderanchor.pptx", 113.08));
    }

    // ---- the rules, one by one ----------------------------------------------------------------------------------

    private static string? StrikeRanges(PptxExtractionResult extraction, string name = "Text 2") => Extension(ShapeNode(extraction, name), "strike_ranges");

    private static string? UnderlineRanges(PptxExtractionResult extraction, string name = "Text 2") => Extension(ShapeNode(extraction, name), "underline_ranges");

    private static string? Unplaced(PptxExtractionResult extraction, string name = "Text 2") => Extension(ShapeNode(extraction, name), ReviewExtensionKeys.StrikeUnplaced);

    [Fact]
    public void A_line_that_covers_only_the_second_half_of_the_text_strikes_part_of_it()
    {
        // From 170 pt to the end of the text at 273.6 pt: the start of the text is not covered.
        var extraction = ExtractSlides(Presentation("secondhalf.pptx", DefaultText() + SlideLine(5, 170, 116, 280)));
        Assert.Null(StrikeRanges(extraction));
        Assert.Equal("true", Unplaced(extraction));
    }

    [Fact]
    public void A_stroke_as_thick_as_a_marker_or_shorter_than_a_letter_is_no_decoration()
    {
        string Struck(string name, string shapes) => StrikeRanges(ExtractSlides(Presentation(name, DefaultText() + shapes))) ?? "-";
        Assert.Equal("[[0,18]]", Struck("weight3.pptx", SlideLine(5, 70, 116, 280, weight: 38100)));        // 3 pt
        Assert.Equal("[[0,18]]", Struck("weight11.pptx", SlideLine(5, 70, 116, 280, weight: 139700)));      // 11 pt: not yet half an em (12 pt) of this text
        Assert.Equal("-", Struck("weight15.pptx", SlideLine(5, 70, 116, 280, weight: 190500)));             // 15 pt: a marker pen, a redaction
        // 8 pt long is less than 0.4 em (9.6 pt) of this text: no line through it, and no stroke across part of it either.
        Assert.Null(Unplaced(ExtractSlides(Presentation("short.pptx", DefaultText() + SlideLine(5, 100, 116, 108)))));
        Assert.Equal("true", Unplaced(ExtractSlides(Presentation("long.pptx", DefaultText() + SlideLine(5, 100, 116, 112)))));
    }

    [Fact]
    public void A_line_on_the_bottom_edge_of_the_text_frame_is_its_border_not_an_underline()
    {
        // A frame 31 pt high holds 28.8 pt of text, whose baseline is 123.28 pt down: the underline zone reaches 132.9 pt, past the bottom of the frame at 131 pt.
        var box = TextBox(2, 72, 100, 400, 31, SlideParagraph("Old price 1000 yen"));
        Assert.Null(UnderlineRanges(ExtractSlides(Presentation("border.pptx", box + SlideLine(5, 70, 131, 280)))));
        Assert.Equal("[[0,18]]", UnderlineRanges(ExtractSlides(Presentation("insideborder.pptx", box + SlideLine(5, 70, 129.5, 280)))));
    }

    [Fact]
    public void Centered_text_is_centered_between_its_margin_and_the_right_inset_whatever_the_indent_of_its_first_line()
    {
        // 201.6 pt of text centered in 400 pt from 72 pt lies from 171.2 pt to 372.8 pt: a line from 180 pt to 368 pt covers it...
        var centered = TextBox(2, 72, 100, 400, 40, SlideParagraph("Old price 1000 yen", properties: "algn=\"ctr\""));
        Assert.Equal("[[0,18]]", StrikeRanges(ExtractSlides(Presentation("centered.pptx", centered + SlideLine(5, 180, 116, 368)))));
        // ...and one from the left of the box to the middle of the text covers half of it.
        var half = ExtractSlides(Presentation("centeredhalf.pptx", centered + SlideLine(5, 72, 116, 272)));
        Assert.Null(StrikeRanges(half));
        Assert.Equal("true", Unplaced(half));
        // The indent of the first line does not move centered text.
        var indented = TextBox(2, 72, 100, 400, 40, SlideParagraph("Old price 1000 yen", properties: $"algn=\"ctr\" indent=\"{Emu(60)}\""));
        Assert.Equal("[[0,18]]", StrikeRanges(ExtractSlides(Presentation("centeredindent.pptx", indented + SlideLine(5, 180, 116, 368)))));
    }

    [Fact]
    public void The_default_insets_of_a_text_frame_place_its_text()
    {
        // A frame that says nothing of its insets has 7.2 pt at the sides and 3.6 pt at the top: the text starts at 79.2 pt and its baseline is
        // 126.88 pt down, so a line 0.3 em (7.2 pt) under the baseline is an underline of all of it, though it begins 11 pt inside it.
        var box = TextBox(2, 72, 100, 400, 40, SlideParagraph("Old price 1000 yen"), " wrap=\"none\" anchor=\"t\"");
        Assert.Equal("[[0,18]]", UnderlineRanges(ExtractSlides(Presentation("insets.pptx", box + SlideLine(5, 90, 134.08, 290)))));
    }

    [Fact]
    public void The_size_of_text_comes_from_the_run_the_lists_of_the_shape_the_defaults_of_the_presentation_and_then_18_pt()
    {
        string Kind(string shape, string defaults, double y)
        {
            var node = ShapeNode(ExtractSlides(Presentation("sizes.pptx", shape + SlideLine(5, 70, y, 345), defaults)), "Text 2");
            return Extension(node, "strike_ranges") is not null ? "strike" : Extension(node, "underline_ranges") is not null ? "underline" : "none";
        }
        const string run = "<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>Old price 1000 yen</a:t></a:r></a:p>";
        // Nothing sizes it: 18 pt, the baseline 117.46 pt down; a line at 128.5 pt is 0.6 em under it (24 pt text would have it 0.22 em under).
        var plain = TextBox(2, 72, 100, 400, 40, run);
        Assert.Equal("strike", Kind(plain, "", 112.06));
        Assert.Equal("none", Kind(plain, "", 128.5));
        // The list style of the shape says 32 pt (baseline 131.04 pt): 121.44 pt is 0.3 em above it, and 0.22 em under the baseline of 18 pt text.
        Assert.Equal("strike", Kind(TextBox(2, 72, 100, 400, 40, run, listStyle: "<a:lvl1pPr><a:defRPr sz=\"3200\"/></a:lvl1pPr>"), "", 121.44));
        // The default text style of the presentation says 20 pt (baseline 119.4 pt): 126 pt is 0.33 em under it, and 0.47 em under 18 pt text.
        Assert.Equal("underline", Kind(plain, "<p:defaultTextStyle><a:lvl1pPr><a:defRPr sz=\"2000\"/></a:lvl1pPr></p:defaultTextStyle>", 126));
    }

    [Fact]
    public void Autofit_shrinks_the_text_and_the_space_between_its_lines_as_the_slide_says()
    {
        // 32 pt text at 62.5 % is 20 pt, its baseline 119.4 pt down: 122.5 pt is 0.15 em under it (32 pt text would have it 0.27 em above its baseline).
        var scaled = TextBox(2, 72, 100, 400, 40, SlideParagraph("Old price 1000 yen", 32), bodyChildren: "<a:normAutofit fontScale=\"62500\"/>");
        Assert.Equal("[[0,18]]", UnderlineRanges(ExtractSlides(Presentation("fontscale.pptx", scaled + SlideLine(5, 70, 122.5, 245)))));
        // 20 pt text with the space between lines reduced by 20 %: the line is 19.2 pt high and the baseline 114.6 pt down, so 104.5 pt is 0.5 em above it.
        var tight = TextBox(2, 72, 100, 400, 40, SlideParagraph("Old price 1000 yen", 20), bodyChildren: "<a:normAutofit lnSpcReduction=\"20000\"/>");
        Assert.Equal("[[0,18]]", StrikeRanges(ExtractSlides(Presentation("reduction.pptx", tight + SlideLine(5, 70, 104.5, 245)))));
    }

    [Fact]
    public void Text_that_does_not_wrap_stays_one_line_however_narrow_its_frame()
    {
        // 201.6 pt of text in a frame of 100 pt that does not wrap: a line from 70 pt to 280 pt strikes all of it.
        var narrow = TextBox(2, 72, 100, 100, 40, SlideParagraph("Old price 1000 yen"));
        Assert.Equal("[[0,18]]", StrikeRanges(ExtractSlides(Presentation("nowrap.pptx", narrow + SlideLine(5, 70, 116, 280)))));
    }

    [Fact]
    public void Spacing_in_points_and_the_space_after_a_paragraph_move_the_lines_below_and_the_space_before_the_first_paragraph_is_not_set()
    {
        // A line of 48 pt: the baseline of 24 pt text is 100 + 48 - 5.52 = 142.48 pt down.
        var exact = TextBox(2, 72, 100, 400, 80, SlideParagraph("Old price 1000 yen").Replace("<a:p>", "<a:p><a:pPr><a:lnSpc><a:spcPts val=\"4800\"/></a:lnSpc></a:pPr>", StringComparison.Ordinal));
        Assert.Equal("[[0,18]]", StrikeRanges(ExtractSlides(Presentation("points.pptx", exact + SlideLine(5, 70, 142.48 - 7.2, 280)))));
        // 20 pt after the first paragraph: the second one's baseline is 100 + 28.8 + 20 + 23.28 = 172.08 pt down.
        var after = TextBox(2, 72, 100, 400, 120,
            SlideParagraph("First line").Replace("<a:p>", "<a:p><a:pPr><a:spcAft><a:spcPts val=\"2000\"/></a:spcAft></a:pPr>", StringComparison.Ordinal) + SlideParagraph("Old price 1000 yen"));
        Assert.Equal("[[11,29]]", StrikeRanges(ExtractSlides(Presentation("after.pptx", after + SlideLine(5, 70, 172.08 - 7.2, 280)))));
        // Space before the first paragraph of a frame is not set: the baseline stays 123.28 pt down.
        var first = TextBox(2, 72, 100, 400, 80, SlideParagraph("Old price 1000 yen").Replace("<a:p>", "<a:p><a:pPr><a:spcBef><a:spcPts val=\"2000\"/></a:spcBef></a:pPr>", StringComparison.Ordinal));
        Assert.Equal("[[0,18]]", StrikeRanges(ExtractSlides(Presentation("firstbefore.pptx", first + SlideLine(5, 70, 116.08, 280)))));
    }

    [Fact]
    public void The_width_of_text_is_estimated_by_the_kind_of_its_characters()
    {
        // Eight full-width characters are 192 pt at 24 pt, from 72 pt to 264 pt: a line to 262 pt covers them, one to 170 pt does not.
        var japanese = TextBox(2, 72, 100, 400, 40, SlideParagraph("取り消す文章です"));
        Assert.Equal("[[0,8]]", StrikeRanges(ExtractSlides(Presentation("wide.pptx", japanese + SlideLine(5, 70, 116, 262)))));
        var part = ExtractSlides(Presentation("widepart.pptx", japanese + SlideLine(5, 70, 116, 170)));
        Assert.Null(StrikeRanges(part));
        Assert.Equal("true", Unplaced(part));
        // Twenty-nine narrow characters (i, l and spaces) are about 209 pt: a line to 275 pt covers them, where characters of the common width would be 383 pt and wrap.
        var narrow = TextBox(2, 72, 100, 400, 40, SlideParagraph("illi illi illi illi illi illi"));
        Assert.Equal("[[0,29]]", StrikeRanges(ExtractSlides(Presentation("narrow.pptx", narrow + SlideLine(5, 70, 116, 275)))));
    }

    [Fact]
    public void A_short_line_beside_a_line_through_all_of_the_text_asks_for_nothing()
    {
        // The long line strikes the text. A short one 3.5 pt above it, not touching it, crosses part of the same text: no more to review.
        var extraction = ExtractSlides(Presentation("wholeandpart.pptx", DefaultText() + SlideLine(5, 70, 116, 280) + SlideLine(6, 72, 119.5, 100)));
        Assert.Equal("[[0,18]]", StrikeRanges(extraction));
        Assert.Null(Unplaced(extraction));
    }

    [Fact]
    public void A_line_is_painted_by_its_own_outline_or_else_by_the_line_of_its_style_and_so_is_the_frame_of_a_shape()
    {
        // Lines as PowerPoint draws them have no colour of their own: the style says they are drawn.
        Assert.Equal("[[0,18]]", StrikeRanges(ExtractSlides(Presentation("styledline.pptx", DefaultText() + SlideLine(5, 70, 116, 280, outline: "", style: LineStyle)))));
        // An outline that says no fill paints nothing whatever the style says.
        Assert.Null(StrikeRanges(ExtractSlides(Presentation("styledhidden.pptx", DefaultText() + SlideLine(5, 70, 116, 280, outline: "<a:noFill/>", style: LineStyle)))));
        // The line ends on the left edge of a block at 280 pt: a block filled by its style is there to be met, one whose fill says none is not.
        const string fillStyle = "<p:style><a:fillRef idx=\"1\"><a:schemeClr val=\"accent1\"/></a:fillRef></p:style>";
        var styled = Rectangle(10, 280, 100, 80, 40, fill: "", style: fillStyle);
        Assert.Null(StrikeRanges(ExtractSlides(Presentation("styledblock.pptx", DefaultText() + styled + SlideLine(5, 70, 116, 280)))));
        var unfilled = Rectangle(10, 280, 100, 80, 40, fill: "<a:noFill/>", style: fillStyle);
        Assert.Equal("[[0,18]]", StrikeRanges(ExtractSlides(Presentation("styledunfilled.pptx", DefaultText() + unfilled + SlideLine(5, 70, 116, 280)))));
        // A block of plain colour with no outline is met too.
        Assert.Null(StrikeRanges(ExtractSlides(Presentation("colourblock.pptx", DefaultText() + Rectangle(10, 280, 100, 80, 40, fill: RedLine) + SlideLine(5, 70, 116, 280)))));
    }

    [Fact]
    public void A_line_that_ends_on_a_table_or_on_another_line_is_not_a_decoration()
    {
        // The left edge of a table at 280 pt...
        var table = GridTable(280, 100, [144, 144], [36, 36], [Cell("Item"), Cell("Amount")], [Cell("Old plan"), Cell("800")]);
        Assert.Null(StrikeRanges(ExtractSlides(Presentation("ontable.pptx", DefaultText() + table + SlideLine(5, 70, 116, 280)))));
        // ...and a vertical line it ends on.
        Assert.Null(StrikeRanges(ExtractSlides(Presentation("online.pptx", DefaultText() + SlideLine(6, 280, 90, 280, rise: 60) + SlideLine(5, 70, 116, 280)))));
        // The same line with nothing at its end.
        Assert.Equal("[[0,18]]", StrikeRanges(ExtractSlides(Presentation("freeend.pptx", DefaultText() + SlideLine(6, 290, 90, 290, rise: 60) + SlideLine(5, 70, 116, 280)))));
    }

    [Fact]
    public void A_line_written_as_a_plain_shape_decorates_unless_it_is_turned()
    {
        Assert.Equal("[[0,18]]", StrikeRanges(ExtractSlides(Presentation("plainline.pptx", DefaultText() + SlideShapeLine(5, 70, 116, 210)))));
        // 20 pt long: turned by 5 degrees it is a line across the first letters (and asks for review), turned by 12 degrees (4.2 pt in 20) it is not horizontal.
        Assert.Equal("true", Unplaced(ExtractSlides(Presentation("turned5.pptx", DefaultText() + SlideShapeLine(5, 80, 116, 20, 5)))));
        Assert.Null(Unplaced(ExtractSlides(Presentation("turned12.pptx", DefaultText() + SlideShapeLine(5, 80, 116, 20, 12)))));
    }

    [Fact]
    public void A_thin_bar_is_a_decoration_unless_it_is_turned_or_paints_nothing()
    {
        // 40 pt text: the baseline is 100 + 48 - 9.2 = 138.8 pt down, and the bar through it at 126.8 pt. Turned by 1.5 degrees a bar 210 pt long is 6.7 pt high
        // as a whole, thin enough for 40 pt text, but it is not horizontal.
        var text = TextBox(2, 72, 100, 500, 60, SlideParagraph("Old price 1000 yen", 40));
        string Partial(string name, string bar) => Unplaced(ExtractSlides(Presentation(name, text + bar))) ?? "-";
        Assert.Equal("true", Partial("barstraight.pptx", Rectangle(5, 72, 126.2, 210, 1.2)));
        Assert.Equal("-", Partial("barturned.pptx", Rectangle(5, 72, 126.2, 210, 1.2, degrees: 1.5)));
        // A bar with neither fill nor outline paints nothing.
        Assert.Equal("-", Partial("barinvisible.pptx", Rectangle(5, 72, 126.2, 210, 1.2, fill: "<a:noFill/>")));
    }

    // ---- table cells --------------------------------------------------------------------------------------------

    [Fact]
    public void The_cell_of_a_table_is_placed_by_its_column_and_by_its_own_margins()
    {
        // A table at (72, 200) of two columns of 144 pt: "800" in the second column (29.7 pt wide) starts at 216 + 7.2 pt, and the baseline of its row is 257.06 pt down.
        var second = ExtractSlides(Presentation("column.pptx", SlideTable("Item", "Old plan") + SlideLine(5, 220, 251.7, 256)));
        Assert.Equal("[[1,1,[[0,3]]]]", Extension(ShapeNode(second, "Table"), "strike_cells"));
        // A left margin of 36 pt of the cell's own puts "Old plan" at 108 pt: a line from 76 pt to 150 pt covers only part of it.
        var wide = GridTable(72, 200, [144, 144], [36, 36], [Cell("Item"), Cell("Amount")], [Cell("Old plan", $" marL=\"{Emu(36)}\""), Cell("800")]);
        var margin = ExtractSlides(Presentation("margin.pptx", wide + SlideLine(5, 76, 251.7, 150)));
        Assert.Null(Extension(ShapeNode(margin, "Table"), "strike_cells"));
        Assert.Equal("true", Extension(ShapeNode(margin, "Table"), ReviewExtensionKeys.StrikeUnplaced));
    }

    [Theory]
    // The second row of the table spans 236 pt to 272 pt, the text is 21.6 pt high: its baseline lies at 257.06 pt (top), 260.66 pt (middle) or 264.26 pt (bottom),
    // and the underline zone reaches 7.2 pt under it.
    [InlineData("t", 266.0, "none")]
    [InlineData("ctr", 266.0, "underline")]
    [InlineData("ctr", 269.5, "none")]
    [InlineData("b", 269.5, "underline")]
    public void The_text_of_a_table_cell_follows_the_anchor_of_the_cell(string anchor, double y, string expected)
    {
        var table = GridTable(72, 200, [144, 144], [36, 36], [Cell("Item"), Cell("Amount")], [Cell("Old plan", $" anchor=\"{anchor}\""), Cell("800")]);
        var node = ShapeNode(ExtractSlides(Presentation("cellanchor.pptx", table + SlideLine(5, 76, y, 150))), "Table");
        var actual = Extension(node, "underline_cells") is not null ? "underline" : Extension(node, "strike_cells") is not null ? "strike" : "none";
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void A_cell_with_an_empty_paragraph_above_its_text_is_placed_by_the_text_it_holds()
    {
        // The cell's text is "Old plan": the empty paragraph above it is a line of its own (21.6 pt) and no character of the text.
        var cell = Cell("", paragraphs: "<a:p><a:endParaRPr lang=\"en-US\" sz=\"1800\"/></a:p><a:p><a:r><a:rPr lang=\"en-US\" sz=\"1800\"/><a:t>Old plan</a:t></a:r></a:p>");
        var table = GridTable(72, 200, [144, 144], [36, 72], [Cell("Item"), Cell("Amount")], [cell, Cell("800")]);
        // Its baseline is 236 + 3.6 + 21.6 + 17.46 = 278.66 pt down.
        var extraction = ExtractSlides(Presentation("emptyfirst.pptx", table + SlideLine(5, 76, 278.66 - 5.4, 150)));
        Assert.Equal("[[1,0,[[0,8]]]]", Extension(ShapeNode(extraction, "Table"), "strike_cells"));
    }

    [Fact]
    public async Task A_symbol_in_a_table_cell_is_no_text_to_strike()
    {
        // A milestone mark on a schedule: the line through the cell is the schedule's line symbol, as it always was.
        var (markdown, _) = await Export(Presentation("milestone.pptx", SlideTable("Item", "◆") + SlideLine(5, 76, 251.7, 150)));
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);
        Assert.Contains("─", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cell_tab_stop_definition_does_not_shift_its_text_decoration()
    {
        // A tab stop defines placement; it contributes no character that could shift the text's decoration offsets.
        var cell = Cell("", paragraphs: "<a:p><a:pPr><a:tabLst><a:tab pos=\"914400\" algn=\"l\"/></a:tabLst></a:pPr><a:r><a:rPr lang=\"en-US\" sz=\"1800\"/><a:t>Old plan</a:t></a:r></a:p>");
        var table = GridTable(72, 200, [144, 144], [36, 36], [Cell("Item"), Cell("Amount")], [cell, Cell("800")]);
        var extraction = ExtractSlides(Presentation("tabstop.pptx", table + SlideLine(5, 76, 251.7, 150)));
        Assert.Equal("[[1,0,[[0,8]]]]", Extension(ShapeNode(extraction, "Table"), "strike_cells"));
    }

    [Fact]
    public void A_cell_with_a_literal_tab_is_not_placed()
    {
        var cell = Cell("", paragraphs: "<a:p><a:tab/><a:r><a:rPr lang=\"en-US\" sz=\"1800\"/><a:t>Old plan</a:t></a:r></a:p>");
        var table = GridTable(72, 200, [144, 144], [36, 36], [Cell("Item"), Cell("Amount")], [cell, Cell("800")]);
        var extraction = ExtractSlides(Presentation("literal-tab.pptx", table + SlideLine(5, 76, 251.7, 150)));
        Assert.Null(Extension(ShapeNode(extraction, "Table"), "strike_cells"));
    }

    // ---- text that something else writes ------------------------------------------------------------------------

    [Fact]
    public async Task A_line_over_the_text_of_a_box_in_a_diagram_stays_a_drawing_that_asks_for_review()
    {
        // The diagram writes the label of a box without formatting, so a line taken as formatting of it would be dropped without a trace.
        // The text of box A, "Old step", lies from 207.2 pt to 272.9 pt with its baseline 121.06 pt down: a line at 115.66 pt strikes it.
        var diagram = Box(10, 200, 100, 160, 40, "Old step") + Box(11, 200, 200, 160, 40, "Next step") +
            SlideLine(6, 280, 140, 280, rise: 60, connection: "<a:stCxn id=\"10\" idx=\"2\"/><a:endCxn id=\"11\" idx=\"0\"/>");
        var struck = ExtractSlides(Presentation("diagramstrike.pptx", diagram + SlideLine(5, 205, 115.66, 276)));
        Assert.Null(StrikeRanges(struck, "Box 10"));
        Assert.True(HasUnresolvedLine(struck));
        // The same line over a box that no connector touches is the strikethrough of its text.
        var alone = ExtractSlides(Presentation("alonestrike.pptx", Box(10, 200, 100, 160, 40, "Old step") + SlideLine(5, 205, 115.66, 276)));
        Assert.Equal("[[0,8]]", StrikeRanges(alone, "Box 10"));
        // Not glued, but an arrow from one box to the other that the geometry resolves: the box is a box of the diagram all the same.
        var soft = Box(10, 200, 100, 160, 40, "Old step") + Box(11, 200, 200, 160, 40, "Next step") +
            SlideLine(6, 280, 140, 280, rise: 60, ends: "<a:tailEnd type=\"triangle\"/>");
        var arrow = ExtractSlides(Presentation("softstrike.pptx", soft + SlideLine(5, 205, 115.66, 276)));
        Assert.Null(StrikeRanges(arrow, "Box 10"));
        // A connector glued to the box at one end, the other end free: the glue says the box is a box of the diagram.
        var stub = Box(10, 200, 100, 160, 40, "Old step") + SlideLine(6, 280, 140, 280, rise: 60, connection: "<a:stCxn id=\"10\" idx=\"2\"/>");
        Assert.Null(StrikeRanges(ExtractSlides(Presentation("gluedstub.pptx", stub + SlideLine(5, 205, 115.66, 276))), "Box 10"));
        // The label of an edge is written by the diagram too.
        var labelled = diagram + TextBox(7, 290, 160, 100, 24, SlideParagraph("Old note", 18), FrameLess + " anchor=\"t\"");
        var label = ExtractSlides(Presentation("labelstrike.pptx", labelled + SlideLine(5, 288, 172.06, 358)));
        Assert.Null(StrikeRanges(label, "Text 7"));
        // Text a line decorates is text, though a line of no diagram's is near it: that line stays what it was, the text is written as such.
        var near = Presentation("neartext.pptx", DefaultText() + SlideLine(6, 290, 170, 290, rise: 60) + SlideLine(5, 70, 116, 280));
        var beside = ExtractSlides(near);
        Assert.Equal("[[0,18]]", StrikeRanges(beside));
        Assert.True(HasUnresolvedLine(beside));
        Assert.Contains("\n~~Old price 1000 yen~~\n", (await Export(near)).Markdown, StringComparison.Ordinal);
    }

    private static string FindFixture(string fileName)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var path = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Pptx", fileName);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(fileName);
    }

    // The first slide of the schedule built of shapes, with more shapes (XML) added to it.
    private string GridWith(string name, string shapes)
    {
        var path = Path.Combine(root, name);
        using var input = ZipFile.OpenRead(FindFixture("schedule-shape-grid.pptx"));
        using var output = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in input.Entries)
        {
            using var reader = entry.Open();
            using var writer = output.CreateEntry(entry.FullName).Open();
            if (entry.FullName == "ppt/slides/slide1.xml")
            {
                var xml = new StreamReader(reader, Encoding.UTF8).ReadToEnd().Replace("</p:spTree>", shapes + "</p:spTree>", StringComparison.Ordinal);
                writer.Write(Encoding.UTF8.GetBytes(xml));
            }
            else reader.CopyTo(writer);
        }
        return path;
    }

    private string GridWithLine(string name, double left, double y, double right) => GridWith(name, SlideLine(900, left, y, right));

    [Fact]
    public async Task A_line_over_the_text_of_a_shape_grid_stays_the_line_symbol_it_was()
    {
        // The label 実装 of the schedule built of shapes (a box 115.2 x 43.2 pt from (36, 230.4) with 10.5 pt text) is written by the table without
        // formatting: a line through it (252.7 pt down, from 80 to 108 pt) would be dropped without a trace, so it is left as the line symbol of its cell.
        var (markdown, _) = await Export(GridWithLine("gridstrike.pptx", 80, 252.7, 108));
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);
        Assert.Contains("| 実装<br>─ | 鈴木 |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_over_the_text_of_an_overlay_symbol_stays_the_line_symbol_it_was()
    {
        // The arrow 設計 of the schedule (337 x 25.9 pt from (349.9, 195.8), 10 pt text) is written by the table as the label of its bar: a line through
        // its text (209.45 pt down, from 505 to 532 pt) is left as a line symbol of the cell it lies in.
        var (markdown, _) = await Export(GridWithLine("gridarrow.pptx", 505, 209.45, 532));
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);
        Assert.Contains("| 設計 | 佐藤 |  | 設計 ━━ | │<br>━━<br>─ | ━━▶ | ▲レビュー |  |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_through_a_note_beside_a_shape_grid_strikes_it_though_it_reaches_back_into_the_grid()
    {
        // A note to the right of the schedule (from 925 pt, 12 pt text) struck by a line that begins in the last column of its body (880 pt): the line is
        // the note's strikethrough and no symbol of the grid, whose last row stays as it was.
        var (markdown, _) = await Export(GridWith("gridnote.pptx", TextBox(901, 925, 322, 120, 24, SlideParagraph("Old note", 12)) + SlideLine(900, 880, 330, 975)));
        Assert.Contains("\n~~Old note~~\n", markdown, StringComparison.Ordinal);
        Assert.Contains("| リリース | 全員 |  |  | ▼ |  |  | ◆ |\n", markdown, StringComparison.Ordinal);
    }

    // ---- what the serializer and the round trip make of it ------------------------------------------------------

    [Fact]
    public void The_marks_are_readable_only_the_node_and_the_round_trip_projection_stay_as_the_source_has_them()
    {
        var struck = Presentation("rt-struck.pptx", DefaultText() + SlideLine(5, 70, 116, 280));
        var plain = Presentation("rt-plain.pptx", DefaultText() + SlideLine(5, 70, 116, 280, ends: "<a:tailEnd type=\"triangle\"/>"));
        var graph = ExtractSlides(struck).Graph;
        var shape = graph.Nodes.Single(node => node.Kind == NodeKind.Shape);
        Assert.IsType<TextNodeContent>(shape.Content);
        Assert.Equal("Old price 1000 yen", ((TextNodeContent)shape.Content).Text);
        // The consumed line is still a node of the graph, as it was: the nodes of the slide, and the round-trip blocks written from
        // them, do not change (the diagram of the unresolved line it used to be is the one node that is no longer derived).
        Assert.Contains(graph.Nodes, node => node.Kind == NodeKind.Connector);
        Assert.Equal(ExtractSlides(plain).Graph.Nodes.Count(node => node.Kind != NodeKind.Diagram), graph.Nodes.Count(node => node.Kind != NodeKind.Diagram));
        // The round-trip projection is what it is without the marks.
        var roundTrip = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;
        Assert.Equal(new DocRedockMarkdownSerializer().Serialize(WithoutRanges(graph)).Markdown, roundTrip);
        Assert.DoesNotContain("~~", roundTrip, StringComparison.Ordinal);
        Assert.Contains("kind=connector", roundTrip, StringComparison.Ordinal);
    }

    [Fact]
    public void Ranges_over_a_rich_paragraph_cut_its_runs_where_they_begin_and_end()
    {
        // Runs "ab" (bold), "cd" and "ef" (italic): characters 1 to 5 are struck through and the fourth (3 to 4) is underlined.
        var node = new DocumentNode("p1", NodeKind.Paragraph, null, 0, ContentLayer.Body,
            new RichTextNodeContent([new TextRun("ab", Bold: true), new TextRun("cd"), new TextRun("ef", Italic: true)]),
            Extensions: new Dictionary<string, JsonElement>
            {
                ["strike_ranges"] = JsonSerializer.SerializeToElement(new[] { new[] { 1, 5 } }),
                ["underline_ranges"] = JsonSerializer.SerializeToElement(new[] { new[] { 3, 4 } }),
            });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc-ranges", DocumentFormatKind.Docx, [new DocumentPartition("document", 0, [node])]);
        var markdown = new ReadableMarkdownSerializer().Serialize(graph);
        // Cut into a, b | cd | e, f. The underline is the outer mark: its stretch (d) is written once, and the strike is written once over each
        // stretch of struck text that it does not cut (b and c, then e), never as adjacent pairs that GFM cannot read.
        Assert.Contains("**a**~~**b**c~~<u>~~d~~</u>~~_e_~~_f_", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ranges_on_a_rich_text_shape_cut_its_runs_and_the_paragraphs_find_their_own_place()
    {
        // Bold "Old" and plain " price" in one paragraph, a second paragraph under it: a line through the first strikes both runs.
        var paragraphs = SlideParagraph("Old", runProperties: " b=\"1\"").Replace("</a:p>", "<a:r><a:rPr lang=\"en-US\" sz=\"2400\"/><a:t> price 1000 yen</a:t></a:r></a:p>", StringComparison.Ordinal) +
                         SlideParagraph("Second");
        var source = Presentation("rich.pptx", TextBox(2, 72, 100, 400, 80, paragraphs) + SlideLine(5, 70, 116, 280));
        var (markdown, _) = await Export(source);
        Assert.Contains("\n**~~Old~~** ~~price 1000 yen~~\n\nSecond\n", markdown, StringComparison.Ordinal);
    }

    // ---- character formatting of slide text ---------------------------------------------------------------------

    [Fact]
    public async Task A_line_through_runs_written_alike_strikes_them_as_one()
    {
        var paragraph = SlideParagraph("Old").Replace("</a:p>", "<a:r><a:rPr lang=\"en-US\" sz=\"2400\"/><a:t> price 1000 yen</a:t></a:r></a:p>", StringComparison.Ordinal);
        var source = Presentation("alike.pptx", TextBox(2, 72, 100, 400, 40, paragraph) + SlideLine(5, 70, 116, 280));
        Assert.Contains("\n~~Old price 1000 yen~~\n", (await Export(source)).Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_through_the_second_line_of_a_paragraph_strikes_the_runs_there()
    {
        // "Old price" and "New price" in runs of one paragraph with a line break between them: the second line has its baseline 152.08 pt down.
        const string properties = "<a:rPr lang=\"en-US\" sz=\"2400\"/>";
        var paragraph = $"<a:p><a:r>{properties}<a:t>Old price</a:t></a:r><a:br>{properties}</a:br><a:r>{properties}<a:t>New price</a:t></a:r></a:p>";
        var source = Presentation("secondline.pptx", TextBox(2, 72, 100, 400, 80, paragraph) + SlideLine(5, 70, 152.08 - 7.2, 180));
        Assert.Equal("[[10,19]]", StrikeRanges(ExtractSlides(source)));
        var markdown = (await Export(source)).Markdown;
        Assert.Contains("~~New price~~", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("~~Old", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bold_and_underlined_slide_text_writes_the_underline_outside_and_the_preview_shows_both()
    {
        var paragraphs = SlideParagraph("bold", runProperties: " b=\"1\" u=\"sng\"") + SlideParagraph("struck", runProperties: " u=\"sng\" strike=\"sngStrike\"") +
                         SlideParagraph("italic", runProperties: " i=\"1\" u=\"sng\"");
        var (markdown, _) = await Export(Presentation("nesting.pptx", TextBox(2, 72, 100, 400, 120, paragraphs)));
        Assert.Contains("<u>**bold**</u>", markdown, StringComparison.Ordinal);
        Assert.Contains("<u>~~struck~~</u>", markdown, StringComparison.Ordinal);
        Assert.Contains("<u>_italic_</u>", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("**<u>", markdown, StringComparison.Ordinal);

        // The project's own reader (the GUI preview) reads the tags around the emphasis for what they are.
        var runs = MarkdownInlineParser.Runs("<u>**bold**</u>");
        var bold = Assert.Single(runs);
        Assert.True(bold.Bold && bold.Underline);
        Assert.Equal("bold", bold.Text);
        var struck = Assert.Single(MarkdownInlineParser.Runs("<u>~~struck~~</u>"));
        Assert.True(struck.Strike && struck.Underline);
        Assert.Equal("<u><strong>bold</strong></u>", MarkdownInlineParser.Html("<u>**bold**</u>"));
        Assert.Equal("<u><del>struck</del></u>", MarkdownInlineParser.Html("<u>~~struck~~</u>"));
    }

    [Fact]
    public async Task A_link_run_with_an_underline_of_its_own_gets_no_underline_in_the_readable_text_only()
    {
        // Google Slides underlines the run of a link itself, and gives it a:hlinkClick: the underline is how a link looks.
        var link = "<a:p><a:r><a:rPr lang=\"en-US\" sz=\"2400\" u=\"sng\"><a:hlinkClick r:id=\"rId2\"/></a:rPr><a:t>link</a:t></a:r>" +
                   "<a:r><a:rPr lang=\"en-US\" sz=\"2400\"/><a:t> and </a:t></a:r><a:r><a:rPr lang=\"en-US\" sz=\"2400\" u=\"sng\"/><a:t>emphasis</a:t></a:r></a:p>";
        var source = Presentation("link.pptx", TextBox(2, 72, 100, 400, 40, link));
        var (markdown, _) = await Export(source);
        Assert.Contains("\nlink and <u>emphasis</u>\n", markdown, StringComparison.Ordinal);
        // The round-trip projection keeps the run as it is.
        var shape = ExtractSlides(source).Graph.Nodes.Single(node => node.Kind == NodeKind.Shape);
        var rich = Assert.IsType<RichTextNodeContent>(shape.Content);
        Assert.Contains(rich.Runs, run => run.Text == "link" && run.Underline);
        Assert.Contains("<u>link</u>", DocRedockInlineMarkdown.Serialize(rich.Runs), StringComparison.Ordinal);
        // The paragraph's JSON says which run is a link, and only for a link.
        var details = shape.Extensions!["paragraph_details"].GetRawText();
        Assert.Equal(1, details.Split("\"IsLink\":true", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("\"IsLink\":false", details, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------- Word

    private const string WordNamespaces =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" xmlns:v=\"urn:schemas-microsoft-com:vml\"";

    private const string SectionProperties =
        "<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\"/></w:sectPr>";

    private const string Styles =
        "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val=\"24\"/></w:rPr></w:rPrDefault></w:docDefaults>" +
        "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style></w:styles>";

    private string Document(string name, string body, IReadOnlyDictionary<string, string>? parts = null, string section = SectionProperties,
        string relationships = "", string styles = Styles)
    {
        var all = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = """
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                </Types>
                """,
            ["_rels/.rels"] = """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """,
            ["word/_rels/document.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + relationships + "</Relationships>",
            ["word/styles.xml"] = styles,
            ["word/document.xml"] = $"<w:document {WordNamespaces}><w:body>{body}{section}</w:body></w:document>",
        };
        foreach (var (entry, text) in parts ?? new Dictionary<string, string>()) all[entry] = text;
        return Write(name, all);
    }

    private static string WordRun(string text, string properties = "") =>
        $"<w:r>{(properties.Length == 0 ? "" : "<w:rPr>" + properties + "</w:rPr>")}<w:t xml:space=\"preserve\">{text}</w:t></w:r>";

    private static int nextShape = 100;

    private const string RedFill = "<a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill>";

    /// <summary>A line anchored in the paragraph that holds this run, as Word writes one: x and y points from the reference named by
    /// relativeFrom, length points long and rise points lower at its far end. With another <paramref name="preset"/> it is a
    /// rectangle (a bar, or a box) of that height; <paramref name="outline"/> replaces the stroke (an a:ln), <paramref name="connection"/>
    /// is what it is glued to, <paramref name="textBox"/> the text it carries, <paramref name="rotation"/> its turn in 60000ths of a degree.</summary>
    private static string WordLine(double x, double y, double length, string relativeH = "column", string relativeV = "paragraph", string color = "FF0000",
        double rise = 0, string ends = "", string extraProperties = "", string preset = "line", string fill = "", string outline = "", string connection = "",
        string textBox = "", long rotation = 0, long weight = 19050)
    {
        var id = nextShape++;
        var stroke = outline.Length > 0 ? outline : $"<a:ln w=\"{weight}\"><a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill>{ends}</a:ln>";
        return $"<w:r><w:drawing><wp:anchor distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\" simplePos=\"0\" relativeHeight=\"{id}\" behindDoc=\"0\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"1\">" +
            "<wp:simplePos x=\"0\" y=\"0\"/>" +
            $"<wp:positionH relativeFrom=\"{relativeH}\"><wp:posOffset>{Emu(x)}</wp:posOffset></wp:positionH>" +
            $"<wp:positionV relativeFrom=\"{relativeV}\"><wp:posOffset>{Emu(y)}</wp:posOffset></wp:positionV>" +
            $"<wp:extent cx=\"{Emu(length)}\" cy=\"{Emu(rise)}\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/><wp:wrapNone/><wp:docPr id=\"{id}\" name=\"Line {id}\"/><wp:cNvGraphicFramePr/>" +
            $"<a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:cNvCnPr>{connection}</wps:cNvCnPr>" +
            $"<wps:spPr><a:xfrm{(rotation == 0 ? "" : $" rot=\"{rotation}\"")}><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Emu(length)}\" cy=\"{Emu(rise)}\"/></a:xfrm><a:prstGeom prst=\"{preset}\"><a:avLst/></a:prstGeom>" +
            $"{fill}{stroke}{extraProperties}</wps:spPr>{textBox}<wps:bodyPr/></wps:wsp></a:graphicData></a:graphic></wp:anchor></w:drawing></w:r>";
    }

    // A table cell of Word paragraphs (`properties` are the cell's own: its width, say) and a table of rows of them.
    private static string WordCell(string content, string properties = "<w:tcW w:w=\"4680\" w:type=\"dxa\"/>") => $"<w:tc><w:tcPr>{properties}</w:tcPr>{content}</w:tc>";

    private static string WordTable(string rows, string properties = "", string grid = "<w:gridCol w:w=\"4680\"/><w:gridCol w:w=\"4680\"/>") =>
        $"<w:tbl><w:tblPr><w:tblW w:w=\"9360\" w:type=\"dxa\"/>{properties}</w:tblPr><w:tblGrid>{grid}</w:tblGrid>{rows}</w:tbl>";

    // The anchored line runs at the strike and underline height of a 12 pt paragraph of one line.
    private const double WordStrike = 7;
    private const double WordUnderline = 12;

    private static string WordParagraph(string text, string lines = "", string properties = "", string runProperties = "") =>
        $"<w:p>{(properties.Length == 0 ? "" : "<w:pPr>" + properties + "</w:pPr>")}{WordRun(text, runProperties)}{lines}</w:p>";

    private static DocxExtractionResult ExtractDocument(string source) => new DocxAdapter().ExtractAsync(source).AsTask().GetAwaiter().GetResult();

    private static IEnumerable<DocumentNode> ParagraphNodes(DocxExtractionResult extraction) =>
        extraction.Graph.Nodes.Where(node => node.Kind is NodeKind.Paragraph or NodeKind.Heading or NodeKind.ListItem);

    private static bool HasUnresolvedLine(DocxExtractionResult extraction) =>
        extraction.Graph.Nodes.Any(node => node.Kind == NodeKind.Diagram && node.Extensions?.ContainsKey("visual_graph") == true);

    // ---- strikethrough and underline ----------------------------------------------------------------------------

    [Fact]
    public async Task A_line_anchored_to_a_one_line_paragraph_strikes_it_and_is_no_unresolved_line()
    {
        var source = Document("single.docx", WordParagraph("Introduction") + WordParagraph("Old price 1000 yen", WordLine(-2, WordStrike, 104)) + WordParagraph("New price 800 yen"));
        var (markdown, result) = await Export(source);
        Assert.Contains("\nIntroduction\n\n~~Old price 1000 yen~~\n\nNew price 800 yen\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("図の抽出結果", markdown, StringComparison.Ordinal);
        Assert.Empty(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages);
        var extraction = ExtractDocument(source);
        Assert.False(HasUnresolvedLine(extraction));
        var node = ParagraphNodes(extraction).Single(item => item.Content is TextNodeContent { Text: "Old price 1000 yen" });
        Assert.Equal("[[0,18]]", Extension(node, "strike_ranges"));
    }

    [Fact]
    public async Task A_line_under_a_one_line_paragraph_underlines_it_and_a_double_line_marks_it_once()
    {
        var under = Document("underline.docx", WordParagraph("Old price 1000 yen", WordLine(0, WordUnderline, 102, color: "0000FF")));
        Assert.Contains("\n<u>Old price 1000 yen</u>\n", (await Export(under)).Markdown, StringComparison.Ordinal);
        var doubled = Document("double.docx", WordParagraph("Old price 1000 yen", WordLine(-2, WordStrike - 1.5, 104) + WordLine(-2, WordStrike + 1.5, 104)));
        var (markdown, _) = await Export(doubled);
        Assert.Contains("\n~~Old price 1000 yen~~\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("~~~~", markdown, StringComparison.Ordinal);
        Assert.False(HasUnresolvedLine(ExtractDocument(doubled)));
        // Two lines 1 pt apart reach within a touch of each other's ends, and are the two lines of a double line all the same.
        var close = Document("doubleclose.docx", WordParagraph("Old price 1000 yen", WordLine(-2, WordStrike - 0.5, 104) + WordLine(-2, WordStrike + 0.5, 104)));
        Assert.Contains("\n~~Old price 1000 yen~~\n", (await Export(close)).Markdown, StringComparison.Ordinal);
        Assert.False(HasUnresolvedLine(ExtractDocument(close)));
        var both = Document("both.docx", WordParagraph("Old price 1000 yen", WordLine(-2, WordStrike, 104) + WordLine(-2, WordUnderline, 104)));
        Assert.Contains("\n<u>~~Old price 1000 yen~~</u>\n", (await Export(both)).Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_through_a_table_cell_paragraph_strikes_it_and_is_no_overlay_symbol()
    {
        string TableCell(string text, string lines = "") => $"<w:tc><w:tcPr><w:tcW w:w=\"4680\" w:type=\"dxa\"/></w:tcPr>{WordParagraph(text, lines)}</w:tc>";
        var table = "<w:tbl><w:tblPr><w:tblW w:w=\"9360\" w:type=\"dxa\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"4680\"/><w:gridCol w:w=\"4680\"/></w:tblGrid>" +
            $"<w:tr>{TableCell("Plan")}{TableCell("Cost")}</w:tr><w:tr>{TableCell("Old plan", WordLine(-1, WordStrike, 46))}{TableCell("1000")}</w:tr></w:tbl>";
        var source = Document("cell.docx", WordParagraph("Intro") + table + WordParagraph("After"));
        var (markdown, _) = await Export(source);
        Assert.Contains("| Plan | Cost |\n| --- | --- |\n| ~~Old plan~~ | 1000 |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("─", markdown, StringComparison.Ordinal);
        var extraction = ExtractDocument(source);
        var tableNode = Assert.Single(extraction.Graph.Nodes, node => node.Kind == NodeKind.Table);
        Assert.Equal("[[1,0,[[0,8]]]]", Extension(tableNode, "strike_cells"));
        Assert.Null(Extension(tableNode, "table_overlays"));
        Assert.False(HasUnresolvedLine(extraction));
    }

    [Fact]
    public async Task A_line_across_part_of_the_paragraph_marks_nothing_and_asks_for_review_for_a_strike_only()
    {
        // 30 pt of the 100 pt text.
        var source = Document("partial.docx", WordParagraph("Old price 1000 yen", WordLine(0, WordStrike, 30)));
        var (markdown, result) = await Export(source);
        Assert.Contains("\nOld price 1000 yen\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);
        var extraction = ExtractDocument(source);
        var node = ParagraphNodes(extraction).Single();
        Assert.Equal("true", Extension(node, ReviewExtensionKeys.StrikeUnplaced));
        Assert.Equal("true", Extension(node, ReadableMarkdownSerializer.SourceReviewRequiredExtension));
        Assert.False(HasUnresolvedLine(extraction));
        var page = Assert.Single(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages);
        Assert.Equal(ReviewElementKind.StrikethroughUnplaced, Assert.Single(page.Elements).Kind);
        Assert.Equal("本文：文字1件は、一部に取り消し線がありますが、どの文字が取り消されているかを確定できませんでした（取り消し線の印を付けずにMarkdownに含めています）。元のファイルで内容を確認してください。",
            ExportReviewText.DescribeJapanese(page));

        // The same part of the text underlined: nothing is marked, and nothing is asked.
        var under = Document("partialunder.docx", WordParagraph("Old price 1000 yen", WordLine(0, WordUnderline, 30)));
        var (underMarkdown, underResult) = await Export(under);
        Assert.Contains("\nOld price 1000 yen\n", underMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<u>", underMarkdown, StringComparison.Ordinal);
        Assert.Empty(ExportReviewBuilder.Build(underResult.Graph, underResult.Diagnostics).Pages);
        Assert.False(HasUnresolvedLine(ExtractDocument(under)));
    }

    [Fact]
    public async Task A_paragraph_of_rich_text_is_struck_by_cutting_its_runs_in_a_copy()
    {
        // Bold "Old" and plain " price 1000 yen": a line through all of it strikes both runs.
        var paragraph = $"<w:p>{WordRun("Old", "<w:b/>")}{WordRun(" price 1000 yen")}{WordLine(-2, WordStrike, 104)}</w:p>";
        var source = Document("rich.docx", paragraph);
        var (markdown, _) = await Export(source);
        Assert.Contains("\n~~**Old** price 1000 yen~~\n", markdown, StringComparison.Ordinal);
        var node = ParagraphNodes(ExtractDocument(source)).Single();
        Assert.IsType<RichTextNodeContent>(node.Content);
        Assert.Equal("[[0,18]]", Extension(node, "strike_ranges"));
    }

    [Fact]
    public void The_marks_are_readable_only_and_the_round_trip_projection_is_byte_identical()
    {
        var paragraph = $"<w:p>{WordRun("Old", "<w:b/>")}{WordRun(" price 1000 yen")}{WordLine(-2, WordStrike, 104)}</w:p>" +
                        WordParagraph("Plain text", WordLine(-2, WordStrike, 60)) + WordParagraph("Underlined", WordLine(0, WordUnderline, 60));
        var graph = ExtractDocument(Document("roundtrip.docx", paragraph)).Graph;
        Assert.Equal(3, graph.Nodes.Count(node => node.Extensions?.ContainsKey("strike_ranges") == true || node.Extensions?.ContainsKey("underline_ranges") == true));
        var roundTrip = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;
        Assert.Equal(new DocRedockMarkdownSerializer().Serialize(WithoutRanges(graph)).Markdown, roundTrip);
        Assert.DoesNotContain("~~", roundTrip, StringComparison.Ordinal);
        Assert.DoesNotContain("<u>", roundTrip, StringComparison.Ordinal);
        // DocRedockInlineMarkdown is given the runs as they are.
        var rich = graph.Nodes.Select(node => node.Content).OfType<RichTextNodeContent>().Single();
        Assert.Equal("**Old** price 1000 yen", DocRedockInlineMarkdown.Serialize(rich.Runs));
    }

    // ---- what is not placed -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_page_anchored_line_is_no_decoration_and_is_left_as_it_was()
    {
        // Drawn separators are common: a line anchored to the page, over nothing the file can tie to the text, stays a drawing
        // (here the unresolved line the visual fallback keeps) and asks for nothing new.
        var source = Document("page.docx", WordParagraph("A page-anchored line elsewhere", WordLine(300, 400, 120, "page", "page", "0000FF")));
        var (markdown, result) = await Export(source);
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);
        Assert.Contains("図の抽出結果", markdown, StringComparison.Ordinal);
        Assert.True(HasUnresolvedLine(ExtractDocument(source)));
        // Even one that lies exactly where a strikethrough would: the page says nothing of the paragraph.
        var over = Document("pagestrike.docx", WordParagraph("Old price 1000 yen", WordLine(72, 72 + WordStrike, 104, "page", "page")));
        Assert.DoesNotContain("~~", (await Export(over)).Markdown, StringComparison.Ordinal);
        _ = result;
    }

    [Theory]
    [InlineData("margin", "paragraph", true)]       // the margin is the column's left edge in a body of one column
    [InlineData("column", "line", true)]
    [InlineData("page", "paragraph", false)]
    [InlineData("column", "page", false)]
    [InlineData("column", "margin", false)]
    [InlineData("leftMargin", "paragraph", false)]
    [InlineData("column", "bottomMargin", false)]
    public async Task Only_a_line_set_against_the_paragraph_or_line_and_the_column_or_margin_is_placed(string relativeH, string relativeV, bool placed)
    {
        var source = Document("frames.docx", WordParagraph("Old price 1000 yen", WordLine(-2, WordStrike, 104, relativeH, relativeV)));
        var markdown = (await Export(source)).Markdown;
        Assert.Equal(placed, markdown.Contains("~~Old price 1000 yen~~", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_line_set_against_a_character_is_placed_only_where_the_anchor_is_at_the_start_of_the_paragraph()
    {
        // The anchor run first: the character it sits at is the first of the line, which starts at the paragraph's left edge.
        var atStart = Document("charstart.docx", $"<w:p>{WordLine(-2, WordStrike, 104, "character")}{WordRun("Old price 1000 yen")}</w:p>");
        Assert.Contains("\n~~Old price 1000 yen~~\n", (await Export(atStart)).Markdown, StringComparison.Ordinal);
        // The anchor run after the text: where the character is depends on the width of what comes before it.
        var atEnd = Document("charend.docx", WordParagraph("Old price 1000 yen", WordLine(-2, WordStrike, 104, "character")));
        var (markdown, _) = await Export(atEnd);
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);
        Assert.Contains("図の抽出結果", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_over_a_paragraph_of_several_lines_is_left_alone_and_asks_for_no_review_of_its_own()
    {
        // 150 characters at about 6 pt each are over 800 pt: the paragraph wraps in the 468 pt column, and which line is struck is
        // not known without the layout. The line stays the drawing it was.
        var text = string.Concat(Enumerable.Repeat("Old price 1000 yen ", 8)).TrimEnd();
        var source = Document("several.docx", WordParagraph(text, WordLine(-2, WordStrike, 460)));
        var (markdown, result) = await Export(source);
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);
        Assert.Contains("図の抽出結果", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages.SelectMany(page => page.Elements), element => element.Kind == ReviewElementKind.StrikethroughUnplaced);
        Assert.Null(Extension(ParagraphNodes(ExtractDocument(source)).Single(), ReviewExtensionKeys.StrikeUnplaced));
    }

    [Theory]
    [InlineData("<w:ind w:left=\"720\"/><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>", "")]   // a list item starts after its number
    [InlineData("<w:spacing w:line=\"480\" w:lineRule=\"auto\"/>", "")]                                         // double spacing: the baseline is not known
    [InlineData("<w:spacing w:line=\"300\" w:lineRule=\"exact\"/>", "")]                                        // exact height
    [InlineData("<w:ind w:leftChars=\"200\"/>", "")]                                                            // an indent counted in characters
    [InlineData("<w:bidi/>", "")]                                                                               // right to left
    [InlineData("<w:framePr w:w=\"2000\" w:hSpace=\"180\" w:wrap=\"around\" w:vAnchor=\"text\" w:hAnchor=\"text\"/>", "")]
    [InlineData("<w:pBdr><w:bottom w:val=\"single\" w:sz=\"4\" w:space=\"1\" w:color=\"auto\"/></w:pBdr>", "")]       // a border moves the text
    [InlineData("<w:textDirection w:val=\"tbRl\"/>", "")]                                                        // vertical text
    [InlineData("", "tab")]                                                                                      // a tab in the text
    [InlineData("", "break")]                                                                                    // a line break
    [InlineData("", "return")]                                                                                   // a carriage return
    [InlineData("", "equation")]                                                                                 // an equation in the line
    public async Task A_paragraph_whose_line_is_not_known_without_layout_keeps_the_drawing(string properties, string variant)
    {
        var runs = variant switch
        {
            "tab" => $"{WordRun("Old price")}<w:r><w:tab/></w:r>{WordRun("1000 yen")}",
            "break" => $"{WordRun("Old price")}<w:r><w:br/></w:r>{WordRun("1000 yen")}",
            "return" => $"{WordRun("Old price")}<w:r><w:cr/></w:r>{WordRun("1000 yen")}",
            "equation" => $"{WordRun("Old price")}<m:oMath xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\"><m:r><m:t>x</m:t></m:r></m:oMath>{WordRun("1000 yen")}",
            _ => WordRun("Old price 1000 yen"),
        };
        var source = Document("unplaced.docx", $"<w:p>{(properties.Length == 0 ? "" : "<w:pPr>" + properties + "</w:pPr>")}{runs}{WordLine(-2, WordStrike, 104)}</w:p>");
        var markdown = (await Export(source)).Markdown;
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);
        Assert.Contains("図の抽出結果", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_doc_grid_places_snapped_paragraph_text_while_a_loose_paragraph_uses_font_metrics()
    {
        var gridSection = SectionProperties.Replace("</w:sectPr>", "<w:docGrid w:type=\"lines\" w:linePitch=\"360\"/></w:sectPr>", StringComparison.Ordinal);
        var grid = Document("grid.docx", WordParagraph("Old price 1000 yen", WordLine(-2, WordStrike, 104)), section: gridSection);
        Assert.Contains("~~Old price 1000 yen~~", (await Export(grid)).Markdown, StringComparison.Ordinal);
        // A larger pitch moves the snapped baseline down; the same line now lies in the strike zone.
        var wideGridSection = SectionProperties.Replace("</w:sectPr>", "<w:docGrid w:type=\"lines\" w:linePitch=\"480\"/></w:sectPr>", StringComparison.Ordinal);
        var wideGrid = Document("wide-grid.docx", WordParagraph("Old price 1000 yen", WordLine(-2, 12, 104)), section: wideGridSection);
        Assert.Contains("~~Old price 1000 yen~~", (await Export(wideGrid)).Markdown, StringComparison.Ordinal);
        // A paragraph that does not snap to the grid is set as the font sets it.
        var loose = Document("gridloose.docx", WordParagraph("Old price 1000 yen", WordLine(-2, WordStrike, 104), "<w:snapToGrid w:val=\"0\"/>"), section: gridSection);
        Assert.Contains("~~Old price 1000 yen~~", (await Export(loose)).Markdown, StringComparison.Ordinal);
        // Space before the paragraph: a line set against the paragraph may start above or below it, but one set against the line is placed.
        var spaced = Document("spaced.docx", WordParagraph("Old price 1000 yen", WordLine(-2, WordStrike, 104), "<w:spacing w:before=\"240\"/>"));
        Assert.DoesNotContain("~~", (await Export(spaced)).Markdown, StringComparison.Ordinal);
        var byLine = Document("spacedline.docx", WordParagraph("Old price 1000 yen", WordLine(-2, WordStrike, 104, relativeV: "line"), "<w:spacing w:before=\"240\"/>"));
        Assert.Contains("~~Old price 1000 yen~~", (await Export(byLine)).Markdown, StringComparison.Ordinal);
        // The same when the space before is the spacing of the style's automatic or line-counted kind.
        foreach (var (name, kind) in new[] { ("autospaced.docx", "w:beforeAutospacing=\"1\""), ("linespaced.docx", "w:beforeLines=\"100\"") })
        {
            var automatic = Document(name, WordParagraph("Old price 1000 yen", WordLine(-2, WordStrike, 104), $"<w:spacing {kind}/>"));
            Assert.DoesNotContain("~~", (await Export(automatic)).Markdown, StringComparison.Ordinal);
        }
        // Two columns.
        var columns = Document("columns.docx", WordParagraph("Old price 1000 yen", WordLine(-2, WordStrike, 104)),
            section: SectionProperties.Replace("</w:sectPr>", "<w:cols w:num=\"2\" w:space=\"720\"/></w:sectPr>", StringComparison.Ordinal));
        Assert.DoesNotContain("~~", (await Export(columns)).Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_size_and_spacing_come_from_the_styles_of_the_paragraph_and_the_defaults_of_the_document()
    {
        // 24 pt text in a style: the baseline is 21.1 pt under the top, and a line 14 pt under it a strikethrough (0.3 em above the
        // baseline). Under the 12 pt text of the document's defaults the same line lies 0.3 em below the baseline: an underline.
        var styles = "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val=\"24\"/></w:rPr></w:rPrDefault></w:docDefaults>" +
            "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Big\"><w:name w:val=\"Big\"/><w:basedOn w:val=\"Normal\"/><w:rPr><w:sz w:val=\"48\"/></w:rPr></w:style></w:styles>";
        var big = Document("style.docx", WordParagraph("Old price", WordLine(-2, 14, 130), "<w:pStyle w:val=\"Big\"/>"), styles: styles);
        Assert.Contains("~~Old price~~", (await Export(big)).Markdown, StringComparison.Ordinal);
        var small = Document("stylesmall.docx", WordParagraph("Old price", WordLine(-2, 14, 130)), styles: styles);
        var smallMarkdown = (await Export(small)).Markdown;
        Assert.Contains("<u>Old price</u>", smallMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("~~", smallMarkdown, StringComparison.Ordinal);
    }

    // ---- the rules, one by one ----------------------------------------------------------------------------------

    // Whether the readable Markdown of the document strikes the paragraph "Old price 1000 yen".
    private async Task<bool> IsStruck(string source) =>
        (await Export(source)).Markdown.Contains("~~Old price 1000 yen~~", StringComparison.Ordinal);

    [Theory]
    // 12 pt "Old price 1000 yen" is estimated at 100.8 pt, in a column of 468 pt.
    [InlineData("<w:ind w:left=\"720\"/>", 34, 106, true)]                                       // from 36 pt on
    [InlineData("<w:ind w:left=\"720\" w:hanging=\"360\"/>", 16, 96, true)]                       // the first line starts 18 pt in (a hanging indent)
    [InlineData("<w:ind w:firstLine=\"360\"/>", 16, 96, true)]                                    // ...and so does one with a first-line indent
    [InlineData("<w:jc w:val=\"right\"/>", 365, 105, true)]                                       // 367.2 pt to 468 pt
    [InlineData("<w:jc w:val=\"center\"/>", 181, 106, true)]                                      // 183.6 pt to 284.4 pt
    [InlineData("<w:ind w:right=\"720\"/><w:jc w:val=\"right\"/>", 329, 107, true)]               // 331.2 pt to 432 pt
    [InlineData("<w:jc w:val=\"right\"/>", 0, 105, false)]                                        // beside the text, at the other end of the line
    public async Task The_extent_of_the_text_follows_the_indents_and_the_alignment_of_the_paragraph(string properties, double x, double length, bool covers)
    {
        var source = Document("extent.docx", WordParagraph("Old price 1000 yen", WordLine(x, WordStrike, length), properties));
        Assert.Equal(covers, await IsStruck(source));
    }

    [Fact]
    public async Task A_line_spacing_of_a_little_over_one_adds_half_of_its_extra_space_above_the_text()
    {
        // At 1.15 lines a line is 16.8 pt high and the baseline 11.66 pt down, not 10.56 pt: a line 16 pt down is 0.36 em under the baseline, an
        // underline. Under single spacing 16 pt is 0.45 em under it, and lies past the end of the line.
        var spaced = Document("spacing115.docx", WordParagraph("Old price 1000 yen", WordLine(0, 16, 102), "<w:spacing w:line=\"276\" w:lineRule=\"auto\"/>"));
        Assert.Contains("<u>Old price 1000 yen</u>", (await Export(spaced)).Markdown, StringComparison.Ordinal);
        var single = Document("spacing100.docx", WordParagraph("Old price 1000 yen", WordLine(0, 16, 102)));
        Assert.DoesNotContain("<u>", (await Export(single)).Markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(480, 14.28)]    // double spacing: half of the extra line would put the baseline 17.9 pt down, a strikethrough at 14.3 pt
    [InlineData(360, 10.62)]    // one and a half lines: 14.2 pt down
    public async Task A_line_spacing_of_more_than_one_and_a_quarter_lines_is_not_placed(int line, double y)
    {
        var source = Document("widespacing.docx", WordParagraph("Old price 1000 yen", WordLine(-2, y, 104), $"<w:spacing w:line=\"{line}\" w:lineRule=\"auto\"/>"));
        Assert.False(await IsStruck(source));
    }

    [Fact]
    public async Task A_heading_is_struck_by_a_line_over_it_and_takes_no_underline_from_one()
    {
        var styles = Styles.Replace("</w:styles>", "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:outlineLvl w:val=\"0\"/></w:pPr></w:style></w:styles>",
            StringComparison.Ordinal);
        string Heading(string line) => $"<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr>{WordRun("Old", "<w:b/>")}{WordRun(" price 1000 yen")}{line}</w:p>";
        var struck = (await Export(Document("headingstrike.docx", Heading(WordLine(-2, WordStrike, 104)), styles: styles))).Markdown;
        Assert.Contains("# ~~**Old** price 1000 yen~~\n", struck, StringComparison.Ordinal);
        // A heading is written with no underline (it is the way a heading is set apart): so a line under it adds none.
        var underlined = (await Export(Document("headingunder.docx", Heading(WordLine(0, WordUnderline, 102)), styles: styles))).Markdown;
        Assert.DoesNotContain("<u>", underlined, StringComparison.Ordinal);
        Assert.Contains("# **Old** price 1000 yen\n", underlined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_that_nothing_sizes_is_10_pt()
    {
        var unsized = "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style></w:styles>";
        // 84 pt of 10 pt text, the baseline 8.8 pt down: a line at 5.8 pt is 0.3 em above it. A line at 14 pt would be 0.29 em under the baseline of 12 pt text,
        // an underline, but it lies past the end of the 12.2 pt line of 10 pt text.
        var struck = Document("size10.docx", WordParagraph("Old price 1000 yen", WordLine(-2, 5.8, 88)), styles: unsized);
        Assert.Contains("~~Old price 1000 yen~~", (await Export(struck)).Markdown, StringComparison.Ordinal);
        var low = Document("size10low.docx", WordParagraph("Old price 1000 yen", WordLine(-2, 14, 88)), styles: unsized);
        Assert.DoesNotContain("<u>", (await Export(low)).Markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plain", true)]
    [InlineData("floating", false)]
    [InlineData("bidi", false)]
    [InlineData("vertical", false)]
    [InlineData("rightmargin", true)]
    [InlineData("zeromargins", false)]
    [InlineData("narrowcell", true)]
    [InlineData("spanned", true)]
    [InlineData("margin", false)]
    public async Task A_line_in_a_table_cell_is_placed_by_the_cell_it_is_in(string variant, bool covers)
    {
        // A cell of 234 pt has margins of 5.4 pt, so text is set in 223.2 pt. Right aligned, "Old price 1000 yen" spans 122.4 pt to 223.2 pt of it: a line from
        // 120.4 pt to 220.2 pt stops 3 pt short of its end, which is near enough with the margins of 5.4 pt, and not with none.
        const string width = "<w:tcW w:w=\"4680\" w:type=\"dxa\"/>";
        var left = WordParagraph("Old price 1000 yen", WordLine(-2, WordStrike, 104));
        var right = WordParagraph("Old price 1000 yen", WordLine(120.4, WordStrike, 99.8), "<w:jc w:val=\"right\"/>");
        string Row(string cellProperties, string paragraph) => $"<w:tr>{WordCell(paragraph, cellProperties)}{WordCell(WordParagraph("Cost"))}</w:tr>";
        var table = variant switch
        {
            "plain" => WordTable(Row(width, left)),
            // The margin of the page is not where a cell's text begins.
            "margin" => WordTable(Row(width, WordParagraph("Old price 1000 yen", WordLine(-2, WordStrike, 104, relativeH: "margin")))),
            "floating" => WordTable(Row(width, left), "<w:tblpPr w:leftFromText=\"180\" w:rightFromText=\"180\" w:vertAnchor=\"text\" w:tblpY=\"1\"/>"),
            "bidi" => WordTable(Row(width, left), "<w:bidiVisual/>"),
            "vertical" => WordTable(Row(width + "<w:textDirection w:val=\"tbRl\"/>", left)),
            "rightmargin" => WordTable(Row(width, right)),
            "zeromargins" => WordTable(Row(width, right), "<w:tblCellMar><w:left w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"0\" w:type=\"dxa\"/></w:tblCellMar>"),
            // A cell 180 pt wide where the grid says 234 pt: text is set in 169.2 pt, from 68.4 pt to 169.2 pt.
            "narrowcell" => WordTable(Row("<w:tcW w:w=\"3600\" w:type=\"dxa\"/>", WordParagraph("Old price 1000 yen", WordLine(66.4, WordStrike, 100), "<w:jc w:val=\"right\"/>"))),
            // One cell across both columns, with no width of its own: 468 pt less the margins, the text from 356.4 pt to 457.2 pt.
            "spanned" => WordTable("<w:tr>" + WordCell(WordParagraph("Old price 1000 yen", WordLine(354.4, WordStrike, 105), "<w:jc w:val=\"right\"/>"), "<w:gridSpan w:val=\"2\"/>") + "</w:tr>"),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        Assert.Equal(covers, await IsStruck(Document("cellplace.docx", table)));
    }

    [Theory]
    [InlineData("plain", true)]
    [InlineData("arrow", false)]
    [InlineData("glued", false)]
    [InlineData("labelled", false)]
    [InlineData("unpainted", false)]
    [InlineData("turned", false)]
    [InlineData("marker", false)]
    [InlineData("bar", true)]
    [InlineData("thickbar", false)]
    [InlineData("invisiblebar", false)]
    public async Task Only_a_plain_line_or_a_thin_bar_decorates(string variant, bool strikes)
    {
        const string noOutline = "<a:ln><a:noFill/></a:ln>";
        var shape = variant switch
        {
            "plain" => WordLine(-2, WordStrike, 104),
            "arrow" => WordLine(-2, WordStrike, 104, ends: "<a:tailEnd type=\"triangle\"/>"),
            "glued" => WordLine(-2, WordStrike, 104, connection: "<a:stCxn id=\"5\" idx=\"0\"/>"),
            "labelled" => WordLine(-2, WordStrike, 104, textBox: "<wps:txbx><w:txbxContent><w:p><w:r><w:t>x</w:t></w:r></w:p></w:txbxContent></wps:txbx>"),
            "unpainted" => WordLine(-2, WordStrike, 104, outline: "<a:ln w=\"19050\"><a:noFill/></a:ln>"),
            "turned" => WordLine(-2, WordStrike, 104, rotation: 5400000),
            "marker" => WordLine(-2, WordStrike, 104, weight: 152400),
            "bar" => WordLine(-2, WordStrike - 0.6, 104, preset: "rect", rise: 1.2, fill: RedFill, outline: noOutline),
            "thickbar" => WordLine(-2, WordStrike - 3, 104, preset: "rect", rise: 6, fill: RedFill, outline: noOutline),
            "invisiblebar" => WordLine(-2, WordStrike - 0.6, 104, preset: "rect", rise: 1.2, fill: "<a:noFill/>", outline: noOutline),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        Assert.Equal(strikes, await IsStruck(Document("shapes.docx", WordParagraph("Old price 1000 yen", shape))));
    }

    [Fact]
    public async Task A_vml_line_is_a_decoration_in_a_table_cell_and_nowhere_else()
    {
        string Vml(string attributes = "") =>
            $"<w:r><w:pict><v:line id=\"L1\" from=\"-2pt,7pt\" to=\"102pt,7pt\" strokecolor=\"red\" strokeweight=\"1.5pt\"{attributes}/></w:pict></w:r>";
        string InCell(string line) => WordTable($"<w:tr>{WordCell(WordParagraph("Old price 1000 yen", line))}{WordCell(WordParagraph("Cost"))}</w:tr>");
        Assert.True(await IsStruck(Document("vmlcell.docx", InCell(Vml()))));
        // A stroke switched off paints nothing.
        Assert.False(await IsStruck(Document("vmloff.docx", InCell(Vml(" stroked=\"f\"")))));
        // In the body a VML line has no place against the paragraph that can be told.
        Assert.False(await IsStruck(Document("vmlbody.docx", WordParagraph("Old price 1000 yen", Vml()))));
    }

    [Fact]
    public async Task A_line_whose_end_meets_another_shape_or_line_is_not_a_decoration()
    {
        string Box(double x) => WordLine(x, 0, 60, preset: "rect", rise: 40, fill: "<a:solidFill><a:srgbClr val=\"DDDDDD\"/></a:solidFill>", color: "000000");
        async Task<bool> Strikes(string name, string shapes) =>
            await IsStruck(Document(name, WordParagraph("Old price 1000 yen", WordLine(-2, WordStrike, 104) + shapes)));
        // The line ends at 102 pt: on the edge of a box there, or on a vertical line there; 8 pt apart from either it is free.
        Assert.False(await Strikes("touchbox.docx", Box(102)));
        Assert.True(await Strikes("apartbox.docx", Box(110)));
        Assert.False(await Strikes("touchline.docx", WordLine(102, -10, 0, rise: 40)));
        Assert.True(await Strikes("apartline.docx", WordLine(110, -10, 0, rise: 40)));
    }

    [Fact]
    public async Task A_line_anchored_in_the_paragraph_of_a_text_box_stays_a_drawing()
    {
        // The text box has a frame of its own that the file does not give: its paragraph is not placed against the body.
        var body = "<w:p>" + WordRun("Body text") + "<w:r><w:pict><v:shape id=\"box1\" type=\"#_x0000_t202\"><v:textbox><w:txbxContent>" +
                   WordParagraph("Old price 1000 yen", WordLine(-2, WordStrike, 104)) + "</w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>";
        var source = Document("textboxline.docx", body);
        Assert.False(await IsStruck(source));
        Assert.True(HasUnresolvedLine(ExtractDocument(source)));
    }

    [Fact]
    public async Task Hidden_text_does_not_count_towards_the_length_of_the_line()
    {
        var hidden = WordRun(string.Concat(Enumerable.Repeat(" hidden words", 20)), "<w:vanish/>");
        Assert.True(await IsStruck(Document("hidden.docx", $"<w:p>{WordRun("Old price 1000 yen")}{hidden}{WordLine(-2, WordStrike, 104)}</w:p>")));
    }

    [Fact]
    public void A_line_through_all_of_a_paragraph_and_a_short_one_beside_it_ask_for_no_review()
    {
        // The long line strikes the paragraph; a short one 2.5 pt above it, not touching it, crosses part of it.
        var source = Document("wholeandpart.docx", WordParagraph("Old price 1000 yen", WordLine(-2, WordStrike, 104) + WordLine(0, 4.5, 20)));
        var node = ParagraphNodes(ExtractDocument(source)).Single();
        Assert.Equal("[[0,18]]", Extension(node, "strike_ranges"));
        Assert.Null(Extension(node, ReviewExtensionKeys.StrikeUnplaced));
    }

    [Fact]
    public async Task A_line_through_the_paragraph_before_a_table_strikes_it_and_is_no_symbol_of_the_table()
    {
        var table = WordTable($"<w:tr>{WordCell(WordParagraph("Plan"))}{WordCell(WordParagraph("Cost"))}</w:tr><w:tr>{WordCell(WordParagraph("Old plan"))}{WordCell(WordParagraph("1000"))}</w:tr>");
        var (markdown, _) = await Export(Document("beforetable.docx", WordParagraph("Old price 1000 yen", WordLine(0, WordStrike, 102)) + table));
        Assert.Contains("\n~~Old price 1000 yen~~\n", markdown, StringComparison.Ordinal);
        Assert.Contains("| Plan | Cost |\n| --- | --- |\n| Old plan | 1000 |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("─", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_across_part_of_text_the_runs_already_strike_needs_no_review_in_word_either()
    {
        var source = Document("alreadystruck.docx", WordParagraph("Old price 1000 yen", WordLine(0, WordStrike, 30), runProperties: "<w:strike/>"));
        Assert.Null(Extension(ParagraphNodes(ExtractDocument(source)).Single(), ReviewExtensionKeys.StrikeUnplaced));
    }

    [Fact]
    public async Task A_line_under_a_table_cell_paragraph_underlines_it_and_a_line_across_part_of_it_asks_for_review()
    {
        string Table(string line) => WordTable($"<w:tr>{WordCell(WordParagraph("Plan"))}{WordCell(WordParagraph("Cost"))}</w:tr><w:tr>{WordCell(WordParagraph("Old plan", line))}{WordCell(WordParagraph("1000"))}</w:tr>");
        var under = Document("cellunder.docx", WordParagraph("Intro") + Table(WordLine(0, WordUnderline, 44)));
        Assert.Contains("| <u>Old plan</u> | 1000 |", (await Export(under)).Markdown, StringComparison.Ordinal);

        var partial = Document("cellpartial.docx", WordParagraph("Intro") + Table(WordLine(-1, WordStrike, 20)));
        var (markdown, result) = await Export(partial);
        Assert.Contains("| Old plan | 1000 |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);
        var table = Assert.Single(ExtractDocument(partial).Graph.Nodes, node => node.Kind == NodeKind.Table);
        Assert.Equal("true", Extension(table, ReviewExtensionKeys.StrikeUnplaced));
        Assert.Equal(ReviewElementKind.StrikethroughUnplaced, Assert.Single(Assert.Single(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages).Elements).Kind);
    }

    [Fact]
    public async Task A_line_through_a_paragraph_of_a_nested_table_strikes_it()
    {
        // The table in the first cell of the second row of another: its cell's text is written into that cell, struck through.
        var inner = WordTable($"<w:tr>{WordCell(WordParagraph("Old plan", WordLine(-1, WordStrike, 46)), "<w:tcW w:w=\"4000\" w:type=\"dxa\"/>")}</w:tr>", grid: "<w:gridCol w:w=\"4000\"/>");
        var outer = WordTable($"<w:tr>{WordCell(WordParagraph("Plan"))}{WordCell(WordParagraph("Cost"))}</w:tr><w:tr>{WordCell(inner + "<w:p/>")}{WordCell(WordParagraph("1000"))}</w:tr>");
        var source = Document("nested.docx", WordParagraph("Intro") + outer);
        Assert.Contains("| ~~Old plan~~ | 1000 |", (await Export(source)).Markdown, StringComparison.Ordinal);
        Assert.Contains(ExtractDocument(source).Graph.Nodes, node => node.Kind == NodeKind.Table && Extension(node, "strike_cells") == "[[0,0,[[0,8]]]]");
    }

    [Fact]
    public async Task Each_section_sets_text_in_its_own_columns()
    {
        var twoColumns = SectionProperties.Replace("</w:sectPr>", "<w:cols w:num=\"2\" w:space=\"720\"/></w:sectPr>", StringComparison.Ordinal);
        // The first paragraph ends a section of two columns, the second is in the last section of one.
        var source = Document("sections.docx", WordParagraph("Old price 1000 yen", WordLine(-2, WordStrike, 104), twoColumns) + WordParagraph("New price 800 yen", WordLine(-2, WordStrike, 104)));
        var markdown = (await Export(source)).Markdown;
        Assert.DoesNotContain("~~Old price 1000 yen~~", markdown, StringComparison.Ordinal);
        Assert.Contains("~~New price 800 yen~~", markdown, StringComparison.Ordinal);
    }

    // ---- character formatting of text Word drops ----------------------------------------------------------------

    [Fact]
    public async Task Strikethrough_and_underline_in_headers_footers_footnotes_endnotes_and_text_boxes_are_read_and_bold_is_left_alone()
    {
        string Part(string root, string content) =>
            $"<w:{root} {WordNamespaces}>{content}</w:{root}>";
        var body = "<w:p>" + WordRun("Body text") + "<w:r><w:pict><v:shape id=\"box1\" type=\"#_x0000_t202\"><v:textbox><w:txbxContent>" +
                   "<w:p>" + WordRun("boxed", "<w:strike/>") + WordRun(" and ") + WordRun("lined", "<w:u w:val=\"single\"/>") + WordRun(" bold", "<w:b/>") + "</w:p>" +
                   "<w:p>" + WordRun("second", "<w:strike/>") + "</w:p>" +
                   "</w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>";
        var header = Part("hdr", "<w:p>" + WordRun("old header", "<w:strike/>") + WordRun(" and ") + WordRun("underlined", "<w:u/>") + WordRun(" plus ") + WordRun("bold", "<w:b/>") + "</w:p>");
        var footer = Part("ftr", "<w:p>" + WordRun("old footer", "<w:dstrike/>") + WordRun(" end") + "</w:p>");
        var footnotes = Part("footnotes", "<w:footnote w:id=\"1\"><w:p>" + WordRun("old note", "<w:strike/>") + WordRun(" kept") + "</w:p></w:footnote>");
        var endnotes = Part("endnotes", "<w:endnote w:id=\"1\"><w:p>" + WordRun("end ") + WordRun("lined", "<w:u w:val=\"double\"/>") + "</w:p></w:endnote>");
        var relationships =
            "<Relationship Id=\"rIdH\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/header\" Target=\"header1.xml\"/>" +
            "<Relationship Id=\"rIdF\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer\" Target=\"footer1.xml\"/>";
        var source = Document("parts.docx", body, new Dictionary<string, string>
        {
            ["word/header1.xml"] = header, ["word/footer1.xml"] = footer, ["word/footnotes.xml"] = footnotes, ["word/endnotes.xml"] = endnotes,
        }, relationships: relationships);
        var (markdown, _) = await Export(source);
        Assert.Contains("- ヘッダー: ~~old header~~ and <u>underlined</u> plus bold", markdown, StringComparison.Ordinal);
        Assert.Contains("- フッター: ~~old footer~~ end", markdown, StringComparison.Ordinal);
        Assert.Contains("1. ~~old note~~ kept", markdown, StringComparison.Ordinal);
        Assert.Contains("1. end <u>lined</u>", markdown, StringComparison.Ordinal);
        Assert.Contains("~~boxed~~ and <u>lined</u> bold", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("**", markdown, StringComparison.Ordinal);

        // The nodes carry readable-only ranges over their plain text, and the round-trip projection is what it was.
        var graph = ExtractDocument(source).Graph;
        string? Ranges(NodeKind kind, string key) => graph.Nodes.Where(node => node.Kind == kind).Select(node => Extension(node, key)).FirstOrDefault(value => value is not null);
        Assert.Equal("[[0,10]]", Ranges(NodeKind.Header, "strike_ranges"));
        Assert.Equal("[[15,25]]", Ranges(NodeKind.Header, "underline_ranges"));
        Assert.Equal("[[0,10]]", Ranges(NodeKind.Footer, "strike_ranges"));
        Assert.Equal("[[0,8]]", Ranges(NodeKind.Footnote, "strike_ranges"));
        Assert.Equal("[[4,9]]", Ranges(NodeKind.Endnote, "underline_ranges"));
        // The second paragraph of the box begins after the first one's 20 characters and the line break between them.
        Assert.Equal("[[0,5],[21,27]]", Ranges(NodeKind.TextBox, "strike_ranges"));
        Assert.Equal("[[10,15]]", Ranges(NodeKind.TextBox, "underline_ranges"));
        Assert.Equal(new DocRedockMarkdownSerializer().Serialize(WithoutRanges(graph)).Markdown, new DocRedockMarkdownSerializer().Serialize(graph).Markdown);
    }

    [Fact]
    public void The_review_keys_have_one_home_and_every_adapter_writes_them()
    {
        Assert.Equal("strike_unplaced", ReviewExtensionKeys.StrikeUnplaced);
        Assert.Equal("source_review_required", ReviewExtensionKeys.SourceReviewRequired);
        Assert.Equal(ReviewExtensionKeys.StrikeUnplaced, XlsxAdapter.StrikeUnplacedExtension);
        Assert.Equal(ReviewExtensionKeys.SourceReviewRequired, ReadableMarkdownSerializer.SourceReviewRequiredExtension);
    }
}
