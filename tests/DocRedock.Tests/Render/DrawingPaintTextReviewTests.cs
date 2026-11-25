using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Drawing;
using DocRedock.Formats.OpenXml.Xlsx;
using DocRedock.Markdown;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

/// <summary>Review regressions for source DrawingML paint and text: expected values come from the DrawingML rules, not from the reader.</summary>
public sealed class DrawingPaintTextReviewTests
{
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    [Theory]
    [InlineData("FF0000", "<a:comp/>", "#00FFFF")]
    [InlineData("204060", "<a:inv/>", "#DFBF9F")]
    [InlineData("FF0000", "<a:hueOff val='7200000'/>", "#00FF00")]
    [InlineData("00FF00", "<a:hue val='14400000'/>", "#0000FF")]
    [InlineData("4472C4", "<a:lumMod val='20000'/><a:lumOff val='80000'/>", "#DAE3F3")]
    [InlineData("4472C4", "<a:lumMod val='300000'/>", "#FFFFFF")]
    public void Color_transforms_of_the_source_are_applied(string color, string transforms, string expected)
    {
        var value = DrawingMlStyleReader.Read(Shape($"<a:solidFill><a:srgbClr val='{color}'>{transforms}</a:srgbClr></a:solidFill>"));
        Assert.Equal("solid", value.FillPaint.Kind);
        Assert.Equal(expected, value.FillPaint.Color);
        Assert.Empty(value.Warnings);
    }

    [Fact]
    public void Gray_transform_gives_a_neutral_color_without_a_warning()
    {
        var value = DrawingMlStyleReader.Read(Shape("<a:solidFill><a:srgbClr val='FF0000'><a:gray/></a:srgbClr></a:solidFill>"));
        var color = value.FillPaint.Color!;
        Assert.Equal(color.Substring(1, 2), color.Substring(3, 2));
        Assert.Equal(color.Substring(1, 2), color.Substring(5, 2));
        Assert.NotEqual("#000000", color);
        Assert.Empty(value.Warnings);
    }

    [Theory]
    [InlineData("windowText", "#000000")]
    [InlineData("window", "#FFFFFF")]
    public void System_color_without_a_saved_last_color_uses_its_standard_value(string name, string expected)
    {
        var value = DrawingMlStyleReader.Read(Shape($"<a:solidFill><a:sysClr val='{name}'/></a:solidFill>"));
        Assert.Equal("solid", value.FillPaint.Kind);
        Assert.Equal(expected, value.FillPaint.Color);
    }

    [Fact]
    public void Fully_transparent_fill_stays_a_paint_and_leaves_the_visible_line()
    {
        var value = DrawingMlStyleReader.Read(Shape("""
            <a:solidFill><a:srgbClr val='000000'><a:alpha val='0'/></a:srgbClr></a:solidFill>
            <a:ln w='19050'><a:solidFill><a:srgbClr val='000000'/></a:solidFill></a:ln>
            """));
        Assert.Equal("solid", value.FillPaint.Kind);
        Assert.Equal(0, value.FillPaint.Opacity);
        Assert.Equal("solid", value.StrokePaint.Kind);
        Assert.Equal("#000000", value.StrokePaint.Color);
        Assert.Equal(1, value.StrokePaint.Opacity);
        var item = new WorkbookDrawingObject("s", "shape", 100, 50, WorkbookDrawingTransform.Identity)
        { FillPaint = value.FillPaint, StrokePaint = value.StrokePaint, StrokeWidth = value.StrokeWidth };
        var html = Render(item);
        Assert.Contains("fill-opacity=\"0\" stroke-opacity=\"1\"", html);
        Assert.Contains("stroke=\"#000000\"", html);
    }

    [Fact]
    public void Gradient_stops_saved_out_of_order_are_ordered_by_position()
    {
        var value = DrawingMlStyleReader.Read(Shape("""
            <a:gradFill><a:gsLst>
            <a:gs pos='100000'><a:srgbClr val='0000FF'/></a:gs>
            <a:gs pos='0'><a:srgbClr val='FF0000'/></a:gs>
            <a:gs pos='50000'><a:srgbClr val='00FF00'><a:alpha val='50000'/></a:srgbClr></a:gs>
            </a:gsLst><a:lin ang='0'/></a:gradFill>
            """));
        Assert.Equal(new[] { 0d, .5, 1 }, value.FillPaint.Stops.Select(s => s.Position));
        Assert.Equal(new[] { "#FF0000", "#00FF00", "#0000FF" }, value.FillPaint.Stops.Select(s => s.Color));
        Assert.Equal(.5, value.FillPaint.Stops[1].Opacity);
    }

    [Fact]
    public void Single_stop_gradient_stays_a_visible_paint()
    {
        var value = DrawingMlStyleReader.Read(Shape("<a:gradFill><a:gsLst><a:gs pos='30000'><a:srgbClr val='123456'/></a:gs></a:gsLst></a:gradFill>"));
        Assert.Equal("linear", value.FillPaint.Kind);
        Assert.Equal("#123456", Assert.Single(value.FillPaint.Stops).Color);
    }

    [Fact]
    public void Scheme_colors_follow_the_color_map_and_style_placeholder()
    {
        var theme = new XmlDocument { XmlResolver = null };
        theme.LoadXml($"""
            <a:theme xmlns:a='{A}'><a:themeElements><a:clrScheme name='t'>
            <a:dk1><a:sysClr val='windowText' lastClr='111111'/></a:dk1><a:lt1><a:sysClr val='window' lastClr='EEEEEE'/></a:lt1>
            <a:dk2><a:srgbClr val='222222'/></a:dk2><a:lt2><a:srgbClr val='DDDDDD'/></a:lt2>
            <a:accent1><a:srgbClr val='4472C4'/></a:accent1></a:clrScheme>
            <a:fmtScheme><a:fillStyleLst><a:solidFill><a:schemeClr val='phClr'><a:lumMod val='50000'/></a:schemeClr></a:solidFill></a:fillStyleLst>
            <a:lnStyleLst><a:ln w='12700'><a:solidFill><a:schemeClr val='phClr'/></a:solidFill></a:ln></a:lnStyleLst></a:fmtScheme>
            </a:themeElements><clrMap bg1='dk1' tx1='lt1' bg2='dk2' tx2='lt2'/></a:theme>
            """);
        var value = DrawingMlStyleReader.Read(Shape("", "ABC", style: """
            <a:style><a:lnRef idx='1'><a:schemeClr val='tx1'/></a:lnRef><a:fillRef idx='1'><a:srgbClr val='FF0000'/></a:fillRef>
            <a:fontRef idx='minor'><a:schemeClr val='bg1'/></a:fontRef></a:style>
            """), theme);
        Assert.Equal("#800000", value.FillPaint.Color);
        Assert.Equal("#EEEEEE", value.StrokePaint.Color);
        Assert.Equal("#111111", Assert.Single(Assert.Single(value.Paragraphs).Runs).Color);
    }

    [Theory]
    [InlineData("baseline='30000'", 30)]
    [InlineData("baseline='-25000'", -25)]
    [InlineData("", 0)]
    public void Run_baseline_is_read(string attribute, double expected)
    {
        var value = DrawingMlStyleReader.Read(Shape("", "x2", attribute));
        Assert.Equal(expected, Assert.Single(Assert.Single(value.Paragraphs).Runs).Baseline, 6);
    }

    [Fact]
    public void Run_formatting_and_baseline_reach_the_caption_markup_with_escaped_text()
    {
        var value = DrawingMlStyleReader.Read(Shape("", "a&lt;b&amp;c", "b='1' i='1' u='sng' strike='sngStrike' sz='2400' baseline='30000'",
            "<a:solidFill><a:srgbClr val='123456'/></a:solidFill>"));
        var run = Assert.Single(Assert.Single(value.Paragraphs).Runs);
        Assert.Equal("a<b&c", run.Text);
        Assert.True(run.Bold && run.Italic && run.Underline && run.Strike);
        Assert.Equal(32, run.FontSize, 6);
        var html = Render(new WorkbookDrawingObject("s", "shape", 100, 50, WorkbookDrawingTransform.Identity)
        { Text = "a<b&c", Paragraphs = value.Paragraphs });
        Assert.Contains("a&lt;b&amp;c", html);
        Assert.DoesNotContain("a<b&c", html);
        Assert.Contains("color:#123456", html);
        Assert.Contains("text-decoration:underline line-through", html);
        Assert.Contains("vertical-align:super", html);
    }

    [Fact]
    public void Shrink_on_overflow_font_scale_is_applied_to_run_sizes()
    {
        var shape = Shape("", "fit", "sz='2400'");
        var body = (XmlElement)shape.GetElementsByTagName("bodyPr", A)[0]!;
        var fit = shape.OwnerDocument.CreateElement("a", "normAutofit", A); fit.SetAttribute("fontScale", "50000"); body.AppendChild(fit);
        var value = DrawingMlStyleReader.Read(shape);
        Assert.Equal(16, Assert.Single(Assert.Single(value.Paragraphs).Runs).FontSize, 6);
    }

    [Theory]
    [InlineData("lang='ja-JP' altLang='en-US'")]
    [InlineData("lang='en-US' altLang='ja-JP'")]
    public void Theme_Japanese_typeface_is_found_from_either_language_attribute(string languages)
    {
        var theme = new XmlDocument { XmlResolver = null };
        theme.LoadXml($"""
            <a:theme xmlns:a='{A}'><a:themeElements><a:fontScheme><a:majorFont><a:latin typeface='Head'/><a:ea typeface=''/></a:majorFont>
            <a:minorFont><a:latin typeface='Cambria'/><a:ea typeface=''/><a:cs typeface=''/><a:font script='Jpan' typeface='Yu Gothic'/><a:font script='Hans' typeface='DengXian'/></a:minorFont>
            </a:fontScheme></a:themeElements></a:theme>
            """);
        var value = DrawingMlStyleReader.Read(Shape("", "ABC日本語", languages, "<a:latin typeface='+mn-lt'/><a:ea typeface='+mn-ea'/>"), theme);
        var runs = Assert.Single(value.Paragraphs).Runs;
        Assert.Contains(runs, r => r.Text == "ABC" && r.FontFamily == "Cambria");
        Assert.Contains(runs, r => r.Text == "日本語" && r.FontFamily == "Yu Gothic");
    }

    [Fact]
    public void Run_with_only_an_East_Asian_face_uses_it_for_Japanese_text_in_the_CSS_font_stack()
    {
        var value = DrawingMlStyleReader.Read(Shape("", "日本語", "", "<a:ea typeface='ＭＳ Ｐゴシック'/>"));
        var run = Assert.Single(Assert.Single(value.Paragraphs).Runs);
        Assert.Equal("ＭＳ Ｐゴシック", run.FontFamily);
        var html = Render(new WorkbookDrawingObject("s", "shape", 100, 50, WorkbookDrawingTransform.Identity) { Text = "日本語", Paragraphs = value.Paragraphs });
        Assert.Contains("font-family:&#39;ＭＳ Ｐゴシック&#39;,sans-serif", html);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("../other.html")]
    [InlineData("file:///c:/x.txt")]
    public void Unsafe_or_relative_hyperlink_targets_are_not_activated(string target)
    {
        var xml = new XmlDocument { XmlResolver = null };
        xml.LoadXml($"""
            <p:sp xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main' xmlns:a='{A}'
              xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'>
            <p:nvSpPr><p:cNvPr id='4' name='s'/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
            <p:spPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='952500' cy='476250'/></a:xfrm><a:prstGeom prst='rect'/></p:spPr>
            <p:txBody><a:bodyPr/><a:p><a:r><a:rPr><a:hlinkClick r:id='rId9'/></a:rPr><a:t>LABEL</a:t></a:r><a:r><a:rPr><a:hlinkClick r:id='rId8'/></a:rPr><a:t>SAFE</a:t></a:r></a:p></p:txBody></p:sp>
            """);
        var warnings = new List<string>();
        var shape = Assert.Single(DrawingMlSceneReader.Read(xml.DocumentElement!, "pptx", "/ppt/slides/slide1.xml", null,
            new Dictionary<string, string> { ["rId9"] = target, ["rId8"] = "https://example.invalid/ok" }, warnings: warnings));
        var runs = Assert.Single(shape.Paragraphs).Runs;
        Assert.Null(runs.Single(r => r.Text == "LABEL").LinkTarget);
        Assert.Equal("https://example.invalid/ok", runs.Single(r => r.Text == "SAFE").LinkTarget);
        Assert.NotEmpty(warnings);
        var html = Render(shape with { Paragraphs = [new([new() { Text = "LABEL", LinkTarget = target }])] });
        Assert.DoesNotContain("<a href", html);
        Assert.Contains("LABEL", html);
    }

    [Fact]
    public void Flipped_group_keeps_child_text_readable_and_vertical_text_options_reach_the_CSS()
    {
        var xml = new XmlDocument { XmlResolver = null };
        xml.LoadXml($"""
            <p:grpSp xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main' xmlns:a='{A}'>
            <p:nvGrpSpPr><p:cNvPr id='1' name='g'/></p:nvGrpSpPr>
            <p:grpSpPr><a:xfrm flipH='1'><a:off x='0' y='0'/><a:ext cx='1905000' cy='952500'/><a:chOff x='0' y='0'/><a:chExt cx='1905000' cy='952500'/></a:xfrm></p:grpSpPr>
            <p:sp><p:nvSpPr><p:cNvPr id='2' name='s'/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
            <p:spPr><a:xfrm rot='5400000'><a:off x='0' y='0'/><a:ext cx='952500' cy='476250'/></a:xfrm><a:prstGeom prst='rect'/></p:spPr>
            <p:txBody><a:bodyPr vert='vert' rot='5400000' upright='1'/><a:p><a:r><a:t>TEXT</a:t></a:r></a:p></p:txBody></p:sp></p:grpSp>
            """);
        var shape = Assert.Single(DrawingMlSceneReader.Read(xml.DocumentElement!, "pptx", "/ppt/slides/slide1.xml", null));
        var t = shape.Transform;
        Assert.True(t.A * t.D - t.B * t.C < 0);
        Assert.Equal("vert", shape.TextLayout!.Direction);
        var html = Render(shape);
        // One horizontal reflection inside the reflected frame restores the glyph direction.
        Assert.Contains("class=\"source-caption-transform\" style=\"position:absolute;inset:0;transform-origin:0 0;transform:matrix(-1,0,0,1,100,0)\"", html);
        var style = Regex.Match(html, "class=\"source-drawing-text\" style=\"([^\"]*)\"").Groups[1].Value;
        Assert.Contains("writing-mode:vertical-rl;text-orientation:sideways", style);
        // upright cancels the composed frame rotation; bodyPr rot then turns the text by 90 degrees on the page.
        var m = Regex.Match(style, @"transform:matrix\(([^)]*)\)").Groups[1].Value.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var frame = t * WorkbookDrawingTransform.Scale(-1, 1) * new WorkbookDrawingTransform(m[0], m[1], m[2], m[3]);
        Assert.Equal(0, frame.A, 6); Assert.Equal(1, frame.B, 6); Assert.Equal(-1, frame.C, 6); Assert.Equal(0, frame.D, 6);
    }

    [Fact]
    public void Vertical_flip_alone_turns_the_text_by_180_degrees_and_a_horizontal_flip_leaves_it()
    {
        foreach (var (flip, expected) in new[] { ("flipV='1'", -1d), ("flipH='1'", 1d) })
        {
            var xml = new XmlDocument { XmlResolver = null };
            xml.LoadXml($"""
                <p:sp xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main' xmlns:a='{A}'>
                <p:nvSpPr><p:cNvPr id='2' name='s'/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                <p:spPr><a:xfrm {flip}><a:off x='0' y='0'/><a:ext cx='952500' cy='476250'/></a:xfrm><a:prstGeom prst='rect'/></p:spPr>
                <p:txBody><a:bodyPr/><a:p><a:r><a:t>TEXT</a:t></a:r></a:p></p:txBody></p:sp>
                """);
            var shape = Assert.Single(DrawingMlSceneReader.Read(xml.DocumentElement!, "pptx", "/ppt/slides/slide1.xml", null));
            Assert.Contains("source-caption-transform", Render(shape));
            var text = shape.Transform * new WorkbookDrawingTransform(-1, 0, 0, 1, shape.Width, 0);
            Assert.Equal(expected, text.A, 6); Assert.Equal(expected, text.D, 6);
            Assert.Equal(0, text.B, 6); Assert.Equal(0, text.C, 6);
        }
    }

    [Fact]
    public void Worksheet_shape_hyperlinks_reach_the_scene_and_the_page()
    {
        const string xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
        const string r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        string Run(string id, string text) => $"<a:r><a:rPr lang='en-US'><a:hlinkClick r:id='{id}'/></a:rPr><a:t>{text}</a:t></a:r>";
        var drawing = $"""
            <xdr:wsDr xmlns:xdr='{xdr}' xmlns:a='{A}' xmlns:r='{r}'><xdr:twoCellAnchor>
            <xdr:from><xdr:col>1</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>1</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>
            <xdr:to><xdr:col>5</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>6</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>
            <xdr:sp><xdr:nvSpPr><xdr:cNvPr id='2' name='S'/><xdr:cNvSpPr/></xdr:nvSpPr>
            <xdr:spPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='1905000' cy='952500'/></a:xfrm><a:prstGeom prst='rect'><a:avLst/></a:prstGeom></xdr:spPr>
            <xdr:txBody><a:bodyPr/><a:p>{Run("rId1", "EXTERNAL")}{Run("rId2", "INTERNAL")}{Run("rId3", "SCRIPT")}{Run("rId4", "RELATIVE")}</a:p></xdr:txBody>
            </xdr:sp><xdr:clientData/></xdr:twoCellAnchor></xdr:wsDr>
            """;
        var rels = $"<Relationship Id='rId1' Type='{r}/hyperlink' Target='https://example.invalid/page?a=1&amp;b=2' TargetMode='External'/>" +
            $"<Relationship Id='rId2' Type='{r}/hyperlink' Target='#Data!A1'/>" +
            $"<Relationship Id='rId3' Type='{r}/hyperlink' Target='javascript:alert(1)' TargetMode='External'/>" +
            $"<Relationship Id='rId4' Type='{r}/hyperlink' Target='other.xlsx' TargetMode='External'/>";
        using var stream = new MemoryStream(Workbook(drawing, rels));
        var graph = new XlsxAdapter().Extract(stream).Graph;
        var scene = Assert.Single(WorkbookDrawingSceneJson.Read(graph.FormatExtensions![WorkbookDrawingSceneJson.ExtensionKey]).Values);
        var runs = Assert.Single(Assert.Single(scene.Objects).Paragraphs).Runs;
        Assert.Equal("https://example.invalid/page?a=1&b=2", runs.Single(x => x.Text == "EXTERNAL").LinkTarget);
        Assert.Equal("#Data!A1", runs.Single(x => x.Text == "INTERNAL").LinkTarget);
        Assert.Null(runs.Single(x => x.Text == "SCRIPT").LinkTarget);
        Assert.Null(runs.Single(x => x.Text == "RELATIVE").LinkTarget);
        var html = ReadableHtmlWriter.WriteDocument(ReadableProjectionBuilder.Build(graph).Document).Html;
        Assert.Contains("<a href=\"https://example.invalid/page?a=1&amp;b=2\" rel=\"noreferrer noopener\"><span", html);
        var internalLink = Regex.Match(html, "<a href=\"(#[^\"]+)\"><span[^>]*>INTERNAL");
        Assert.True(internalLink.Success);
        Assert.Contains(" id=\"" + internalLink.Groups[1].Value[1..] + "\"", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("href=\"other.xlsx", html);
    }

    private static byte[] Workbook(string drawing, string drawingRelationships)
    {
        const string n = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string p = "http://schemas.openxmlformats.org/package/2006/relationships";
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            void Write(string path, string text) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(text); }
            Write("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/></Types>");
            Write("_rels/.rels", $"<Relationships xmlns=\"{p}\"><Relationship Id=\"w\" Type=\"{r}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Write("xl/workbook.xml", $"<workbook xmlns=\"{n}\" xmlns:r=\"{r}\"><sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"s1\"/></sheets></workbook>");
            Write("xl/_rels/workbook.xml.rels", $"<Relationships xmlns=\"{p}\"><Relationship Id=\"s1\" Type=\"{r}/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            Write("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{n}\" xmlns:r=\"{r}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>HOME</t></is></c></row></sheetData><drawing r:id=\"d\"/></worksheet>");
            Write("xl/worksheets/_rels/sheet1.xml.rels", $"<Relationships xmlns=\"{p}\"><Relationship Id=\"d\" Type=\"{r}/drawing\" Target=\"../drawings/drawing1.xml\"/></Relationships>");
            Write("xl/drawings/drawing1.xml", drawing);
            Write("xl/drawings/_rels/drawing1.xml.rels", $"<Relationships xmlns=\"{p}\">{drawingRelationships}</Relationships>");
        }
        return memory.ToArray();
    }

    private static string Render(WorkbookDrawingObject shape) => ReadableHtmlWriter.WriteDocument(
        new("doc", "en", DocumentFormatKind.Pptx, [new ReadableSourceDrawingScene(new("source", 400, 300, [shape]))])).Html;

    private static XmlElement Shape(string properties, string? text = null, string runAttributes = "", string runChildren = "", string style = "")
    {
        var xml = new XmlDocument { XmlResolver = null };
        xml.LoadXml($"<a:sp xmlns:a='{A}'><a:spPr>{properties}</a:spPr>{style}" + (text is null ? "" :
            $"<a:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr {runAttributes}>{runChildren}</a:rPr><a:t>{text}</a:t></a:r></a:p></a:txBody>") + "</a:sp>");
        return xml.DocumentElement!;
    }
}
