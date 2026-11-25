using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Drawing;
using DocRedock.Markdown;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

public sealed class ComplexDrawingSceneTests
{
    [Fact]
    public void Separate_paths_keep_their_own_fill_stroke_and_tonal_mode()
    {
        var shape = Shape() with { Fill = "#204060", Stroke = "#102030", Paths =
        [Path(true, false), Path(false, true), Path(true, true) with { FillMode = "lighten" }] };
        var result = Render(shape);
        var svg = XDocument.Parse(Regex.Match(result.Html, "<svg\\b.*?</svg>", RegexOptions.Singleline).Value);
        var paths = svg.Root!.Elements().Where(e => e.Name.LocalName == "path").ToArray();
        Assert.Equal(3, paths.Length);
        Assert.Equal("#204060", (string?)paths[0].Attribute("fill"));
        Assert.Equal("none", (string?)paths[0].Attribute("stroke"));
        Assert.Equal("none", (string?)paths[1].Attribute("fill"));
        Assert.Equal("#102030", (string?)paths[1].Attribute("stroke"));
        Assert.Equal("#798CA0", (string?)paths[2].Attribute("fill"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "HtmlSourceDrawingUnsupported");
    }

    [Fact]
    public void Gradient_stops_and_opacity_are_live_SVG_paint_and_caption_styles_are_per_run()
    {
        var shape = Shape() with { Text = "LEFT\nRIGHT", FillPaint = new DrawingPaint
        { Kind = "linear", AngleDegrees = 0, Opacity = .5, Stops = [new(0, "#FF0000", .25), new(1, "#0000FF", .75)] },
            Paragraphs = [new([new() { Text = "LEFT", Bold = true, Underline = true, LinkTarget = "https://example.invalid/a?b=1&c=2" }]),
                new([new() { Text = "RIGHT", Italic = true, Color = "#123456" }])],
            Paths = [Path(true, true)] };
        var result = Render(shape);
        var svg = XDocument.Parse(Regex.Match(result.Html, "<svg\\b.*?</svg>", RegexOptions.Singleline).Value);
        var gradient = Assert.Single(svg.Descendants(), e => e.Name.LocalName == "linearGradient");
        Assert.Equal("0", (string?)gradient.Attribute("x1"));
        Assert.Equal("100", (string?)gradient.Attribute("x2"));
        Assert.Equal(new[] { "0.25", "0.75" }, gradient.Elements().Select(e => (string?)e.Attribute("stop-opacity")));
        var path = Assert.Single(svg.Root!.Elements(), e => e.Name.LocalName == "path");
        Assert.StartsWith("url(#source-drawing-", (string?)path.Attribute("fill"));
        Assert.Equal("0.5", (string?)path.Attribute("fill-opacity"));
        Assert.Equal(2, Regex.Matches(result.Html, "class=\"source-caption-paragraph\"").Count);
        Assert.Contains("text-decoration:underline\"", result.Html);
        Assert.DoesNotContain("underline none", result.Html);
        Assert.Contains("https://example.invalid/a?b=1&amp;c=2", result.Html);
        Assert.Contains("color:#123456", result.Html);
    }

    [Theory]
    [InlineData("visible", false)]
    [InlineData("sanitized", false)]
    [InlineData("complete", true)]
    public void Source_scene_policy_filters_pixels_and_captions_together(string policy, bool included)
    {
        var visible = new DocumentNode("v", NodeKind.Shape, null, 0, ContentLayer.Body, new TextNodeContent("PUBLIC"));
        var hidden = new DocumentNode("h", NodeKind.Shape, null, 1, ContentLayer.Hidden, new TextNodeContent("PRIVATE"));
        var source = new SourceDrawingScene("source", 400, 300,
            [Shape() with { Id = "public-shape", SourceNodeId = "v", Text = "PUBLIC" },
             Shape() with { Id = "private-shape", SourceNodeId = "h", Text = "PRIVATE", Hidden = true }]);
        var graph = new DocumentGraph("1.1", "d", DocumentFormatKind.Pptx, [new("p", 0, [visible, hidden])],
            FormatExtensions: new Dictionary<string, JsonElement> { [SourceDrawingSceneJson.ExtensionKey] =
                SourceDrawingSceneJson.Write(new Dictionary<string, IReadOnlyList<SourceDrawingScene>> { ["p"] = [source] }) });
        var projection = ReadableProjectionBuilder.Build(graph, new(ContentPolicy: policy));
        var html = ReadableHtmlWriter.WriteDocument(projection.Document).Html;
        Assert.Contains("public-shape", html);
        Assert.Equal(included, html.Contains("private-shape", StringComparison.Ordinal));
        Assert.Equal(included, html.Contains("PRIVATE", StringComparison.Ordinal));
        Assert.Single(Regex.Matches(html, ">PUBLIC<"));
    }

    [Fact]
    public void Native_table_cells_are_rendered_inside_the_same_source_frame_and_keep_merge_spans()
    {
        var table = new ReadableTable([new(1), new(2)],
            [new([new(ReadableContent.Text("MERGED"), 2), new(ReadableContent.Text("A"))]),
             new([new(ReadableContent.Text("B"), SourceColumn: 2)])]);
        var drawing = Shape() with { Kind = "table", SourceNodeId = "t", Transform = WorkbookDrawingTransform.Translate(40, 70) };
        var scene = new ReadableSourceDrawingScene(new("frame", 400, 300, [drawing]))
        { ObjectBlocks = new Dictionary<string, ReadableBlock> { ["t"] = table } };
        var result = ReadableHtmlWriter.WriteDocument(new("doc", "en", DocumentFormatKind.Pptx, [scene]));
        Assert.Contains("class=\"source-native-table\"", result.Html);
        Assert.Contains("transform:matrix(1,0,0,1,40,70)", result.Html);
        Assert.Contains("rowspan=\"2\"", result.Html);
        Assert.Single(Regex.Matches(result.Html, ">MERGED<"));
    }

    [Fact]
    public void Theme_mapping_and_inherited_line_geometry_are_resolved_before_rendering()
    {
        var theme = new XmlDocument(); theme.LoadXml("""
            <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:themeElements>
            <a:clrScheme><a:accent1><a:srgbClr val="112233"/></a:accent1><a:accent2><a:srgbClr val="ABCDEF"/></a:accent2></a:clrScheme>
            <a:fmtScheme><a:lnStyleLst><a:ln w="28575"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="dash"/><a:headEnd type="triangle"/><a:tailEnd type="oval"/></a:ln></a:lnStyleLst></a:fmtScheme>
            </a:themeElements><a:clrMap tx1="accent2"/></a:theme>
            """);
        var shape = new XmlDocument(); shape.LoadXml("""
            <a:sp xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:spPr/><a:style><a:lnRef idx="1"><a:schemeClr val="tx1"/></a:lnRef></a:style></a:sp>
            """);
        var appearance = DrawingMlStyleReader.Read(shape.DocumentElement!, theme);
        Assert.Empty(appearance.Warnings);
        Assert.Equal("#ABCDEF", appearance.StrokePaint.Color);
        Assert.Equal(3, appearance.StrokeWidth);
        Assert.Equal("dash", appearance.Dash);
        Assert.Equal("triangle", appearance.HeadArrow);
        Assert.Equal("oval", appearance.TailArrow);
    }

    [Theory]
    [InlineData("+mn-lt", "Arial")]
    [InlineData("+mj-lt", "Georgia")]
    [InlineData("+mn-ea", "Yu Gothic")]
    [InlineData("+mj-cs", "Georgia")]
    public void Explicit_theme_typeface_tokens_resolve_to_actual_family_names(string token, string expected)
    {
        var theme = new XmlDocument(); theme.LoadXml("""
            <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:themeElements><a:fontScheme>
            <a:majorFont><a:latin typeface="Georgia"/><a:cs typeface=""/></a:majorFont>
            <a:minorFont><a:latin typeface="Arial"/><a:ea typeface="Yu Gothic"/></a:minorFont>
            </a:fontScheme></a:themeElements></a:theme>
            """);
        var source = new XmlDocument(); source.LoadXml($"""
            <a:sp xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:spPr/>
            <a:txBody><a:bodyPr/><a:p><a:r><a:rPr><a:latin typeface="{token}"/></a:rPr><a:t>CAPTION</a:t></a:r></a:p></a:txBody></a:sp>
            """);
        var appearance = DrawingMlStyleReader.Read(source.DocumentElement!, theme);
        Assert.Empty(appearance.Warnings);
        Assert.Equal(expected, Assert.Single(Assert.Single(appearance.Paragraphs).Runs).FontFamily);
    }

    [Fact]
    public void Repeated_picture_instances_share_asset_budget_but_keep_distinct_alternative_text()
    {
        var first = Shape() with { Kind = "picture", ImageReference = "images/same.png", Text = "FIRST <instance>" };
        var second = first with { Id = "second", Text = "SECOND & instance", Transform = WorkbookDrawingTransform.Translate(120, 0) };
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lRkAAAAASUVORK5CYII=");
        var result = ReadableHtmlWriter.WriteDocument(new("doc", "en", DocumentFormatKind.Pptx,
            [new ReadableSourceDrawingScene(new("source", 400, 200, [first, second]))]),
            new(Assets: new Dictionary<string, ReadableHtmlAsset> { ["images/same.png"] = new("image/png", bytes) }, MaxTotalImageBytes: bytes.Length));
        Assert.Equal(1, result.EmbeddedImageCount);
        Assert.Equal(bytes.Length, result.EmbeddedImageBytes);
        Assert.Contains("alt=\"FIRST &lt;instance&gt;\"", result.Html);
        Assert.Contains("alt=\"SECOND &amp; instance\"", result.Html);
        Assert.DoesNotContain("{docredock-source-image-instance-alt}", result.Html);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == ReadableHtmlWriter.ImageEmbedSkipped);
    }

    [Fact]
    public void Scene_count_limits_report_omissions_instead_of_silently_dropping_source_frames()
    {
        var frames = Enumerable.Range(0, 4097).Select(i => new SourceDrawingScene("s" + i, 10, 10, [])).ToArray();
        var json = SourceDrawingSceneJson.Write(new Dictionary<string, IReadOnlyList<SourceDrawingScene>> { ["p"] = frames });
        var notices = new List<string>();
        var result = SourceDrawingSceneJson.Read(json, notices);
        Assert.Equal(4096, result["p"].Count);
        Assert.Contains(notices, notice => notice.Contains("limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Theme_placeholder_color_retains_reference_alpha_before_style_alpha_modulation()
    {
        var theme = new XmlDocument(); theme.LoadXml("""
            <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:themeElements>
            <a:clrScheme><a:accent2><a:srgbClr val="008050"/></a:accent2></a:clrScheme>
            <a:fmtScheme><a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"><a:alphaMod val="50000"/></a:schemeClr></a:solidFill></a:fillStyleLst></a:fmtScheme>
            </a:themeElements></a:theme>
            """);
        var source = new XmlDocument(); source.LoadXml("""
            <a:sp xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:spPr/>
            <a:style><a:fillRef idx="1"><a:schemeClr val="accent2"><a:tint val="25000"/><a:alpha val="60000"/></a:schemeClr></a:fillRef></a:style></a:sp>
            """);
        var appearance = DrawingMlStyleReader.Read(source.DocumentElement!, theme);
        Assert.Empty(appearance.Warnings);
        // 25 % of 008050 and 75 % white, mixed in linear light as Office mixes a tint.
        Assert.Equal("#E1E8E3", appearance.FillPaint.Color);
        Assert.Equal(.3, appearance.FillPaint.Opacity, 8);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Chart_data_is_separate_from_hidden_semantic_descriptions_and_open_for_actual_render_fallback(bool supported)
    {
        var chart = Shape() with { Kind = "chart", SourceNodeId = "chart", Width = 320, Height = 180,
            Chart = supported ? new DrawingChart("bar", "Native", [new("S", ["A", "B"], [8, -4], [], null)], false, "standard", false) : null };
        var cached = new ReadableTable([new(1)], [new([new(ReadableContent.Text("CACHE_MUST_PRINT"))])]);
        var scene = new ReadableSourceDrawingScene(new("source", 400, 300, [chart]))
        { ChartData = new Dictionary<string, IReadOnlyList<ReadableBlock>> { ["chart"] = [cached] } };
        var result = ReadableHtmlWriter.WriteDocument(new("doc", "en", DocumentFormatKind.Pptx, [scene]));
        Assert.Contains("data-kind=\"source-chart-data\" data-chart-fallback=\"" + (supported ? "false\">" : "true\" open>"), result.Html);
        Assert.Contains(">CACHE_MUST_PRINT<", result.Html);
        Assert.DoesNotContain("<details data-kind=\"source-diagram-description\"", result.Html);
        Assert.Equal(!supported, result.Diagnostics.Any(d => d.Code == "HtmlSourceChartFallback"));
    }

    [Fact]
    public void Pattern_fills_of_two_charts_in_one_document_get_ids_of_their_own()
    {
        WorkbookDrawingObject Chart(string id, string color) => Shape() with { Id = id, Kind = "chart", SourceNodeId = id, Width = 320, Height = 180,
            Chart = new DrawingChart("bar", null, [new("S", ["A", "B"], [8, 4], [], color)
                { FillPaint = new DrawingPaint { Kind = "pattern", Pattern = "pct5", Color = color, BackColor = "#FFFFFF" } }], false, "standard", false) };
        var scene = new ReadableSourceDrawingScene(new("source", 400, 400, [Chart("first", "#FF0000"), Chart("second", "#0000FF")]));
        var html = ReadableHtmlWriter.WriteDocument(new("doc", "en", DocumentFormatKind.Pptx, [scene])).Html;
        var ids = System.Text.RegularExpressions.Regex.Matches(html, "<pattern id=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(2, ids.Length);
        Assert.Equal(2, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Equal(2, html.Split("fill=\"url(#" + id + ")\"").Length - 1));
    }

    [Fact]
    public void Unknown_graphic_frames_are_not_misidentified_as_charts_or_hidden_with_reconstructed_diagram_descriptions()
    {
        var xml = new XmlDocument(); xml.LoadXml("""
            <p:graphicFrame xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><p:nvGraphicFramePr><p:cNvPr id="42"/></p:nvGraphicFramePr>
            <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/diagram"><dgm:relIds xmlns:dgm="http://schemas.openxmlformats.org/drawingml/2006/diagram"/></a:graphicData></a:graphic></p:graphicFrame>
            """);
        var notices = new List<string>();
        var objects = DrawingMlSceneReader.Read(xml.DocumentElement!, "pptx", "/ppt/slides/slide1.xml", null, warnings: notices);
        Assert.Empty(objects);
        Assert.Contains(notices, notice => notice.Contains("42", StringComparison.Ordinal) && notice.Contains("graphicFrame", StringComparison.Ordinal));
        var title = new DocumentNode("title", NodeKind.Shape, null, 0, ContentLayer.Body, new TextNodeContent("TITLE"));
        var smartArt = new DocumentNode("smart", NodeKind.Diagram, null, 1, ContentLayer.Body, new TextNodeContent("SMARTART_FALLBACK"),
            Extensions: new Dictionary<string, JsonElement> { ["diagram_items"] = JsonSerializer.SerializeToElement(new[] { "Step A", "Step B" }) });
        var graph = new DocumentGraph("1.1", "d", DocumentFormatKind.Pptx, [new("p", 0, [title, smartArt])],
            FormatExtensions: new Dictionary<string, JsonElement> { [SourceDrawingSceneJson.ExtensionKey] = SourceDrawingSceneJson.Write(
                new Dictionary<string, IReadOnlyList<SourceDrawingScene>> { ["p"] = [new("s", 400, 300, [Shape() with { SourceNodeId = "title", Text = "TITLE" }])] }) });
        var section = Assert.Single(ReadableProjectionBuilder.Build(graph).Document.Blocks.OfType<ReadableSection>());
        var fallback = Assert.Single(section.Blocks.OfType<ReadableDiagram>());
        Assert.Contains("smart", fallback.Origin!.NodeIds!);
    }

    [Theory]
    [InlineData(-1, 1, true)]
    [InlineData(1, -1, true)]
    [InlineData(-1, -1, false)]
    [InlineData(1, 1, false)]
    public void Reflected_shape_outlines_do_not_mirror_the_caption(int horizontal, int vertical, bool needsCorrection)
    {
        // Includes an asymmetric text rectangle and a rotated non-uniform group transform.
        var transform = WorkbookDrawingTransform.Translate(90, 60) * WorkbookDrawingTransform.Rotate(25) *
            WorkbookDrawingTransform.Scale(1.4, .8) * WorkbookDrawingTransform.Scale(horizontal, vertical);
        var shape = Shape() with { Transform = transform, Text = "READABLE", TextBounds = new("drawing-local", 5, 8, 70, 30) };
        var html = Render(shape).Html;
        var counter = Regex.Match(html, "class=\"source-caption-transform\"[^>]*transform:matrix\\(([^)]+)\\)");
        Assert.Equal(needsCorrection, counter.Success);
        var determinant = transform.A * transform.D - transform.B * transform.C;
        if (counter.Success)
        {
            var values = counter.Groups[1].Value.Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            // The glyphs turn back about the text rectangle's own axis (2*5+70), so the rectangle
            // keeps following the flipped outline: it ends 25px from the frame's left, as the outline does.
            Assert.Equal(new double[] { -1, 0, 0, 1, 80, 0 }, values);
            determinant *= values[0] * values[3] - values[1] * values[2];
        }
        Assert.True(determinant > 0, "Text must have a non-reflected final transform.");
        Assert.Contains("left:5px;top:8px;width:70px;height:30px", html);
    }

    private static WorkbookDrawingObject Shape() => new("shape", "shape", 100, 50, WorkbookDrawingTransform.Identity);
    private static WorkbookDrawingPath Path(bool fill, bool stroke) => new([
        new("M", [0, 0]), new("L", [100, 0]), new("L", [100, 50]), new("Z", [])], fill, stroke);
    private static ReadableHtmlResult Render(WorkbookDrawingObject shape) => ReadableHtmlWriter.WriteDocument(
        new("doc", "en", DocumentFormatKind.Pptx, [new ReadableSourceDrawingScene(new("source", 400, 300, [shape]))]));
}
