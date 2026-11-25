using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Drawing;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

/// <summary>Pattern, picture, group and path-gradient fills, line ends, tint and shade, default fonts, Word's
/// colour mapping and captions of flat shapes. Expected values come from Office's rendering, not from the reader.</summary>
public sealed class DrawingPaintGapTests : IDisposable
{
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private readonly string root = Directory.CreateTempSubdirectory("docredock-paint-gaps-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    // 1. Pattern fills -------------------------------------------------------------------------------------

    [Theory]
    // Set pixels of the 8x8 bitmap of each preset (GDI+ hatch styles, which the DrawingML presets name).
    [InlineData("pct5", 2)]
    [InlineData("pct50", 32)]
    [InlineData("horz", 8)]
    [InlineData("vert", 8)]
    [InlineData("ltHorz", 16)]
    [InlineData("dkVert", 32)]
    [InlineData("dnDiag", 8)]
    [InlineData("upDiag", 8)]
    [InlineData("cross", 15)]
    [InlineData("diagCross", 16)]
    [InlineData("smGrid", 28)]
    [InlineData("lgGrid", 15)]
    [InlineData("dotGrid", 7)]
    [InlineData("smCheck", 32)]
    [InlineData("lgCheck", 32)]
    [InlineData("pct90", 62)]
    public void Pattern_fill_is_drawn_as_an_svg_pattern_of_the_preset_bitmap(string preset, int pixels)
    {
        var value = DrawingMlStyleReader.Read(Shape($"""
            <a:pattFill prst='{preset}'><a:fgClr><a:srgbClr val='FF0000'><a:alpha val='50000'/></a:srgbClr></a:fgClr>
            <a:bgClr><a:srgbClr val='FFFF00'/></a:bgClr></a:pattFill>
            """));
        Assert.Equal("pattern", value.FillPaint.Kind);
        Assert.Empty(value.Warnings);
        var html = Render(new WorkbookDrawingObject("s", "shape", 100, 50, WorkbookDrawingTransform.Identity) { FillPaint = value.FillPaint, StrokePaint = new() });
        var pattern = Regex.Match(html, "<pattern id=\"([^\"]+)\" patternUnits=\"userSpaceOnUse\" width=\"8\" height=\"8\"[^>]*>(.*?)</pattern>");
        Assert.True(pattern.Success, html);
        Assert.Contains("fill=\"url(#" + pattern.Groups[1].Value + ")\"", html);
        var body = pattern.Groups[2].Value;
        Assert.Contains("<rect width=\"8\" height=\"8\" fill=\"#FFFF00\" fill-opacity=\"1\"/>", body);
        Assert.Contains("fill=\"#FF0000\" fill-opacity=\"0.5\"", body);
        var data = Regex.Match(body, "<path d=\"([^\"]*)\"").Groups[1].Value;
        Assert.Equal(pixels, Regex.Matches(data, @"h(\d+)v1").Sum(run => int.Parse(run.Groups[1].Value, CultureInfo.InvariantCulture)));
        // The alpha belongs to the foreground pixels, not to the whole fill.
        Assert.Contains("fill-opacity=\"1\" stroke-opacity", html);
    }

    [Fact]
    public void Horizontal_and_diagonal_patterns_have_the_direction_of_their_name()
    {
        string Data(string preset) => Regex.Match(Render(new WorkbookDrawingObject("s", "shape", 100, 50, WorkbookDrawingTransform.Identity)
        {
            FillPaint = DrawingMlStyleReader.Read(Shape($"<a:pattFill prst='{preset}'><a:fgClr><a:srgbClr val='000000'/></a:fgClr><a:bgClr><a:srgbClr val='FFFFFF'/></a:bgClr></a:pattFill>")).FillPaint,
        }), "<pattern[^>]*>.*?<path d=\"([^\"]*)\"").Groups[1].Value;
        Assert.Equal("M0 0h8v1h-8z", Data("horz"));
        // Downward: each row's pixel is one further right than that of the row above.
        Assert.Equal(string.Concat(Enumerable.Range(0, 8).Select(i => $"M{i} {i}h1v1h-1z")), Data("dnDiag"));
        Assert.Equal(string.Concat(Enumerable.Range(0, 8).Select(i => $"M{7 - i} {i}h1v1h-1z")), Data("upDiag"));
    }

    [Fact]
    public void Unknown_pattern_preset_becomes_a_solid_blend_with_a_warning()
    {
        var value = DrawingMlStyleReader.Read(Shape("<a:pattFill prst='futurePattern'><a:fgClr><a:srgbClr val='FF0000'/></a:fgClr><a:bgClr><a:srgbClr val='FFFFFF'/></a:bgClr></a:pattFill>"));
        Assert.Equal("solid", value.FillPaint.Kind);
        Assert.Equal("#FF8080", value.FillPaint.Color);
        Assert.Contains(value.Warnings, warning => warning.Contains("pattern", StringComparison.OrdinalIgnoreCase));
    }

    // 2. Picture and group fills ---------------------------------------------------------------------------

    [Fact(Timeout = 30000)]
    public async Task Picture_fill_of_a_Word_shape_is_drawn_inside_the_shape_outline()
    {
        var html = await ExportHtml(WriteDocx("picture-fill", WordShape("ellipse", "<a:blipFill><a:blip r:embed='rImage'/><a:stretch><a:fillRect/></a:stretch></a:blipFill>")));
        var pattern = Regex.Match(html, "<pattern id=\"([^\"]+)\" patternUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"100\" height=\"50\">(.*?)</pattern>");
        Assert.True(pattern.Success, html.Length > 4000 ? html[^4000..] : html);
        Assert.Contains("href=\"data:image/png;base64,", pattern.Groups[2].Value);
        // The outline path is what the picture is clipped to: it is the path's fill.
        Assert.Matches("<path d=\"M [^\"]* A [^\"]*\" fill=\"url\\(#" + Regex.Escape(pattern.Groups[1].Value) + "\\)\"", html);
    }

    [Theory(Timeout = 30000)]
    // The 4x2 pixel picture at 96 dpi, scaled 200 %: an 8x4 tile; mirrored tiles repeat after two of them.
    [InlineData("none", "tl", 8, 4, 0, 0)]
    [InlineData("xy", "tl", 16, 8, 0, 0)]
    [InlineData("x", "tl", 16, 4, 0, 0)]
    // Anchored to the bottom right of the 100x50 frame: a tile ends there.
    [InlineData("none", "br", 8, 4, 4, 2)]
    public async Task Tiled_picture_fill_repeats_the_picture_at_its_scaled_pixel_size(string flip, string align, int width, int height, int x, int y)
    {
        var html = await ExportHtml(WriteDocx("tile-" + flip + align, WordShape("rect",
            $"<a:blipFill><a:blip r:embed='rImage'/><a:tile tx='0' ty='0' sx='200000' sy='200000' flip='{flip}' algn='{align}'/></a:blipFill>")));
        var pattern = Regex.Match(html, $"<pattern id=\"[^\"]+\" patternUnits=\"userSpaceOnUse\" x=\"{x}\" y=\"{y}\" width=\"{width}\" height=\"{height}\">(.*?)</pattern>");
        Assert.True(pattern.Success, html.Length > 4000 ? html[^4000..] : html);
        Assert.Equal(flip == "xy" ? 4 : flip == "x" ? 2 : 1, Regex.Matches(pattern.Groups[1].Value, "<image ").Count);
    }

    [Fact]
    public void Group_fill_takes_the_fill_of_the_enclosing_group()
    {
        var xml = Load($"""
            <p:grpSp xmlns:p='{P}' xmlns:a='{A}'>
            <p:nvGrpSpPr><p:cNvPr id='1' name='g'/></p:nvGrpSpPr>
            <p:grpSpPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='1905000' cy='952500'/><a:chOff x='0' y='0'/><a:chExt cx='1905000' cy='952500'/></a:xfrm>
              <a:solidFill><a:srgbClr val='00B050'/></a:solidFill></p:grpSpPr>
            <p:grpSp><p:nvGrpSpPr><p:cNvPr id='2' name='inner'/></p:nvGrpSpPr>
              <p:grpSpPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='1905000' cy='952500'/><a:chOff x='0' y='0'/><a:chExt cx='1905000' cy='952500'/></a:xfrm><a:grpFill/></p:grpSpPr>
              <p:sp><p:nvSpPr><p:cNvPr id='3' name='s'/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
              <p:spPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='952500' cy='476250'/></a:xfrm><a:prstGeom prst='rect'/><a:grpFill/></p:spPr></p:sp>
            </p:grpSp></p:grpSp>
            """);
        var warnings = new List<string>();
        var shape = Assert.Single(DrawingMlSceneReader.Read(xml, "pptx", "/ppt/slides/slide1.xml", null, warnings: warnings));
        Assert.Equal("solid", shape.FillPaint!.Kind);
        Assert.Equal("#00B050", shape.FillPaint.Color);
        Assert.Empty(warnings);
        // A reader given the shape inside its document (the worksheet adapter) resolves the same.
        var direct = DrawingMlStyleReader.Read((XmlElement)xml.SelectSingleNode("//*[local-name()='sp']")!);
        Assert.Equal("#00B050", direct.FillPaint.Color);
    }

    // 3. Path gradients ------------------------------------------------------------------------------------

    [Theory]
    // fillToRect insets name the point the gradient starts from; the circle reaches the farthest corner of the frame.
    [InlineData("l='50000' t='50000' r='50000' b='50000'", "matrix(100,0,0,50,50,25)", "0.7071")]
    [InlineData("r='100000' b='100000'", "matrix(100,0,0,50,0,0)", "1.4142")]
    [InlineData("l='100000' t='100000'", "matrix(100,0,0,50,100,50)", "1.4142")]
    public void Circle_path_gradient_is_a_radial_gradient_centred_by_its_fill_to_rect(string rect, string transform, string radius)
    {
        var value = DrawingMlStyleReader.Read(Shape($"""
            <a:gradFill><a:gsLst><a:gs pos='0'><a:srgbClr val='FF0000'/></a:gs><a:gs pos='100000'><a:srgbClr val='0000FF'/></a:gs></a:gsLst>
            <a:path path='circle'><a:fillToRect {rect}/></a:path></a:gradFill>
            """));
        Assert.Equal("radial", value.FillPaint.Kind);
        Assert.Empty(value.Warnings);
        var html = Render(new WorkbookDrawingObject("s", "shape", 100, 50, WorkbookDrawingTransform.Identity) { FillPaint = value.FillPaint });
        var gradient = Regex.Match(html, "<radialGradient id=\"([^\"]+)\" gradientUnits=\"userSpaceOnUse\" cx=\"0\" cy=\"0\" r=\"([^\"]+)\" gradientTransform=\"([^\"]+)\">(.*?)</radialGradient>");
        Assert.True(gradient.Success, html);
        Assert.Equal(radius, gradient.Groups[2].Value);
        Assert.Equal(transform, gradient.Groups[3].Value);
        // Position 0 is the centre, as in SVG.
        Assert.StartsWith("<stop offset=\"0\" stop-color=\"#FF0000\"", gradient.Groups[4].Value);
        Assert.Contains("fill=\"url(#" + gradient.Groups[1].Value + ")\"", html);
    }

    [Theory]
    [InlineData("rect")]
    [InlineData("shape")]
    public void Rectangular_and_shape_path_gradients_are_radial_with_a_warning(string path)
    {
        var value = DrawingMlStyleReader.Read(Shape($"""
            <a:gradFill><a:gsLst><a:gs pos='0'><a:srgbClr val='FF0000'/></a:gs><a:gs pos='100000'><a:srgbClr val='0000FF'/></a:gs></a:gsLst>
            <a:path path='{path}'><a:fillToRect l='50000' t='50000' r='50000' b='50000'/></a:path></a:gradFill>
            """));
        Assert.Equal("radial", value.FillPaint.Kind);
        Assert.Contains(value.Warnings, warning => warning.Contains("radial", StringComparison.OrdinalIgnoreCase));
    }

    // 4. Line ends -----------------------------------------------------------------------------------------

    [Fact]
    public void Line_ends_follow_the_line_width_their_size_and_type_and_the_stroke_opacity()
    {
        // A 4 px line: small = 2, medium = 3 and large = 5 line widths.
        var html = Render(Line("38100", "<a:headEnd type='stealth' w='lg' len='sm'/><a:tailEnd type='triangle'/>", "<a:alpha val='50000'/>"));
        var head = Regex.Match(html, "<marker id=\"([^\"]+-head)\" viewBox=\"0 0 8 20\" refX=\"[^\"]+\" refY=\"10\" markerWidth=\"8\" markerHeight=\"20\" orient=\"auto-start-reverse\" markerUnits=\"userSpaceOnUse\">(.*?)</marker>");
        var tail = Regex.Match(html, "<marker id=\"([^\"]+-tail)\" viewBox=\"0 0 12 12\" refX=\"[^\"]+\" refY=\"6\" markerWidth=\"12\" markerHeight=\"12\" orient=\"auto-start-reverse\" markerUnits=\"userSpaceOnUse\">(.*?)</marker>");
        Assert.True(head.Success && tail.Success, html);
        // Stealth: a notch at 60 % of the length behind the tip. Triangle: the plain isosceles head.
        Assert.Contains("d=\"M 8 10 L 0 0 L 3.2 10 L 0 20 Z\"", head.Groups[2].Value);
        Assert.Contains("d=\"M 12 6 L 0 0 L 0 12 Z\"", tail.Groups[2].Value);
        Assert.Contains("fill=\"#000000\" fill-opacity=\"0.5\"", head.Groups[2].Value);
        Assert.Contains("fill=\"#000000\" fill-opacity=\"0.5\"", tail.Groups[2].Value);
        Assert.Contains("marker-start=\"url(#" + head.Groups[1].Value + ")\"", html);
        Assert.Contains("marker-end=\"url(#" + tail.Groups[1].Value + ")\"", html);
    }

    [Theory]
    [InlineData("triangle", "sm", "lg", "viewBox=\"0 0 13.3333 5.3333\"")]
    // The open arrow is half a line width larger and drawn with the line's own stroke.
    [InlineData("arrow", "med", "med", "viewBox=\"0 0 9.3333 9.3333\"")]
    // Ovals and diamonds are centred on the end of the line.
    [InlineData("oval", "med", "med", "refX=\"4\" refY=\"4\"")]
    [InlineData("diamond", "lg", "lg", "refX=\"6.6667\" refY=\"6.6667\"")]
    public void A_line_thinner_than_two_points_gets_the_ends_of_a_two_point_line(string type, string width, string length, string expected)
    {
        var html = Render(Line("9525", $"<a:tailEnd type='{type}' w='{width}' len='{length}'/>"));
        Assert.Contains(expected, Regex.Match(html, "<marker [^>]*>").Value);
        if (type == "arrow") Assert.Matches("<marker [^>]*><path d=\"[^\"]*\" fill=\"none\" stroke=\"#000000\" stroke-opacity=\"1\" stroke-width=\"1\"", html);
        if (type == "oval") Assert.Contains("<ellipse cx=\"4\" cy=\"4\" rx=\"4\" ry=\"4\"", html);
    }

    [Fact]
    public void A_closed_outline_has_no_line_ends()
    {
        var xml = Load($"""
            <p:sp xmlns:p='{P}' xmlns:a='{A}'><p:nvSpPr><p:cNvPr id='2' name='s'/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
            <p:spPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='952500' cy='476250'/></a:xfrm><a:prstGeom prst='rect'/>
            <a:ln w='19050'><a:solidFill><a:srgbClr val='000000'/></a:solidFill><a:headEnd type='triangle'/><a:tailEnd type='triangle'/></a:ln></p:spPr></p:sp>
            """);
        var html = Render(Assert.Single(DrawingMlSceneReader.Read(xml, "pptx", "/ppt/slides/slide1.xml", null)));
        Assert.DoesNotContain("<marker", html);
        Assert.DoesNotContain("marker-start", html);
        Assert.DoesNotContain("marker-end", html);
    }

    [Fact]
    public void The_line_stops_inside_a_triangle_head_whose_tip_is_the_end_of_the_line()
    {
        // 100 px line, 4 px wide: the 12 px head starts at x = 88; the stroke ends just inside it.
        var html = Render(Line("38100", "<a:tailEnd type='triangle'/>"));
        var path = Regex.Match(html, "<path d=\"M 0 0 L ([0-9.]+) 0\"");
        Assert.True(path.Success, html);
        var end = double.Parse(path.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.InRange(end, 88, 92);
        var refX = double.Parse(Regex.Match(html, "<marker [^>]*refX=\"([^\"]+)\"").Groups[1].Value, CultureInfo.InvariantCulture);
        // The marker is placed at the shortened end; its tip (x = 12 in the marker) lands on the source end.
        Assert.Equal(100, end + (12 - refX), 3);
    }

    // 5. Tint and shade ------------------------------------------------------------------------------------

    [Theory]
    // Office 2010 theme: the default shape outline is accent1 4F81BD shaded 50 %, drawn 385D8A;
    // "Medium Style 2 - Accent 1" table bands are accent1 tinted 40 % and 20 %, drawn D0D8E8 and E9EDF4.
    [InlineData("4F81BD", "<a:shade val='50000'/>", "#385D8A")]
    [InlineData("4F81BD", "<a:tint val='40000'/>", "#D0D8E8")]
    [InlineData("4F81BD", "<a:tint val='20000'/>", "#E9EDF4")]
    // Office 2013 and later theme: the default shape outline is accent1 4472C4 shaded 50 %, drawn 2F528F.
    [InlineData("4472C4", "<a:shade val='50000'/>", "#2F528F")]
    [InlineData("4472C4", "<a:shade val='100000'/>", "#4472C4")]
    [InlineData("4472C4", "<a:tint val='100000'/>", "#4472C4")]
    [InlineData("4472C4", "<a:shade val='0'/>", "#000000")]
    [InlineData("4472C4", "<a:tint val='0'/>", "#FFFFFF")]
    public void Tint_and_shade_are_applied_in_linear_light_as_Office_does(string color, string transform, string expected)
    {
        var value = DrawingMlStyleReader.Read(Shape($"<a:solidFill><a:srgbClr val='{color}'>{transform}</a:srgbClr></a:solidFill>"));
        Assert.Equal(expected, value.FillPaint.Color);
        Assert.Empty(value.Warnings);
    }

    // 6. Default fonts and Word's colour mapping -----------------------------------------------------------

    [Fact]
    public void A_run_without_a_font_takes_the_minor_theme_font_of_its_script()
    {
        var theme = Load($"""
            <a:theme xmlns:a='{A}'><a:themeElements><a:fontScheme name='t'>
            <a:majorFont><a:latin typeface='Major Latin'/><a:ea typeface=''/><a:cs typeface=''/></a:majorFont>
            <a:minorFont><a:latin typeface='Minor Latin'/><a:ea typeface=''/><a:cs typeface=''/><a:font script='Jpan' typeface='Minor Japanese'/></a:minorFont>
            </a:fontScheme></a:themeElements></a:theme>
            """).OwnerDocument;
        var shape = Load($"<a:sp xmlns:a='{A}'><a:spPr/><a:txBody><a:bodyPr/><a:p><a:r><a:rPr lang='ja-JP'/><a:t>ABC日本語</a:t></a:r></a:p></a:txBody></a:sp>");
        var runs = Assert.Single(DrawingMlStyleReader.Read(shape, theme).Paragraphs).Runs;
        Assert.Equal(["ABC", "日本語"], runs.Select(run => run.Text).ToArray());
        Assert.Equal(["Minor Latin", "Minor Japanese"], runs.Select(run => run.FontFamily).ToArray());
        // An explicit font still wins, and without a theme the application default stays.
        var own = Load($"<a:sp xmlns:a='{A}'><a:spPr/><a:txBody><a:bodyPr/><a:p><a:r><a:rPr><a:latin typeface='Own'/></a:rPr><a:t>ABC</a:t></a:r></a:p></a:txBody></a:sp>");
        Assert.Equal("Own", Assert.Single(Assert.Single(DrawingMlStyleReader.Read(own, theme).Paragraphs).Runs).FontFamily);
        Assert.Equal("Calibri", Assert.Single(Assert.Single(DrawingMlStyleReader.Read(shape).Paragraphs).Runs).FontFamily);
    }

    [Fact(Timeout = 30000)]
    public async Task Word_shapes_resolve_theme_colours_through_the_colour_mapping_of_the_settings()
    {
        // bg1 is mapped to the dark colour (a document with a dark background mapping): Word draws 112233.
        var mapped = await ExportHtml(WriteDocx("mapped", WordShape("rect", "<a:solidFill><a:schemeClr val='bg1'/></a:solidFill>"),
            "<w:clrSchemeMapping w:bg1='dark1' w:t1='light1' w:bg2='dark2' w:t2='light2' w:accent1='accent1' w:accent2='accent2' w:accent3='accent3' w:accent4='accent4' w:accent5='accent5' w:accent6='accent6' w:hyperlink='hyperlink' w:followedHyperlink='followedHyperlink'/>"));
        Assert.Matches("<path d=\"[^\"]*\" fill=\"#112233\"", mapped);
        var plain = await ExportHtml(WriteDocx("unmapped", WordShape("rect", "<a:solidFill><a:schemeClr val='bg1'/></a:solidFill>"), ""));
        Assert.Matches("<path d=\"[^\"]*\" fill=\"#FFEEDD\"", plain);
    }

    // 7. Captions of flat shapes ---------------------------------------------------------------------------

    [Theory]
    // A vertical line: the text rectangle has no width. Nothing can wrap in it, so the caption is one
    // unwrapped line per paragraph, placed about the line by its alignment.
    [InlineData("<a:prstGeom prst='line'/>", 0, 952500, "ctr", "align-items:center")]
    [InlineData("<a:prstGeom prst='line'/>", 0, 952500, "l", "align-items:flex-start")]
    [InlineData("<a:prstGeom prst='line'/>", 0, 952500, "r", "align-items:flex-end")]
    // A custom geometry whose a:rect has r = l gives the same zero-width rectangle inside an ordinary frame.
    [InlineData("<a:custGeom><a:avLst/><a:gdLst/><a:rect l='0' t='0' r='0' b='b'/><a:pathLst><a:path w='100' h='100'><a:moveTo><a:pt x='0' y='0'/></a:moveTo><a:lnTo><a:pt x='100' y='100'/></a:lnTo></a:path></a:pathLst></a:custGeom>", 952500, 952500, "ctr", "align-items:center")]
    public void Caption_of_a_zero_width_text_rectangle_is_not_wrapped_character_by_character(string geometry, int cx, int cy, string align, string expected)
    {
        var style = CaptionStyle(geometry, cx, cy, align);
        Assert.Contains(";white-space:pre;", style);
        Assert.Contains(expected, style);
        // The insets have no room either.
        Assert.Matches(@"padding:4\.8px 0px 4\.8px 0px", style);
        Assert.Contains("overflow:visible", style);
    }

    [Fact]
    public void Caption_of_a_horizontal_line_wraps_in_its_length_and_is_placed_about_the_line()
    {
        var style = CaptionStyle("<a:prstGeom prst='line'/>", 952500, 0, "ctr");
        Assert.Contains(";white-space:pre-wrap;", style);
        // A zero-height rectangle keeps no vertical inset: the anchor places the text about the line itself.
        Assert.Matches(@"padding:0px 9\.6px 0px 9\.6px", style);
        Assert.Contains("justify-content:center", style);
        Assert.Contains("overflow:visible", style);
        Assert.DoesNotContain("align-items", style);
        // The paragraphs keep their height, so that the anchor centres all of the text on the line.
        Assert.Contains("class=\"source-caption-paragraph\" style=\"min-height:1em;flex:none;text-align:center", CaptionHtml("<a:prstGeom prst='line'/>", 952500, 0, "ctr"));
    }

    // Helpers ----------------------------------------------------------------------------------------------

    private static string CaptionStyle(string geometry, int cx, int cy, string align) =>
        Regex.Match(CaptionHtml(geometry, cx, cy, align), "class=\"source-drawing-text\" style=\"([^\"]*)\"").Groups[1].Value;

    private static string CaptionHtml(string geometry, int cx, int cy, string align)
    {
        var xml = Load($"""
            <p:sp xmlns:p='{P}' xmlns:a='{A}'><p:nvSpPr><p:cNvPr id='2' name='s'/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
            <p:spPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='{cx}' cy='{cy}'/></a:xfrm>{geometry}
            <a:ln w='9525'><a:solidFill><a:srgbClr val='000000'/></a:solidFill></a:ln></p:spPr>
            <p:txBody><a:bodyPr anchor='ctr' vertOverflow='clip' horzOverflow='clip'/><a:p><a:pPr algn='{align}'/><a:r><a:t>CAPTION TEXT</a:t></a:r></a:p></p:txBody></p:sp>
            """);
        var shape = Assert.Single(DrawingMlSceneReader.Read(xml, "pptx", "/ppt/slides/slide1.xml", null));
        return Render(shape);
    }

    private static WorkbookDrawingObject Line(string width, string ends, string colorTransform = "")
    {
        var xml = Load($"""
            <p:cxnSp xmlns:p='{P}' xmlns:a='{A}'><p:nvCxnSpPr><p:cNvPr id='2' name='c'/><p:cNvCxnSpPr/><p:nvPr/></p:nvCxnSpPr>
            <p:spPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='952500' cy='0'/></a:xfrm><a:prstGeom prst='line'/>
            <a:ln w='{width}'><a:solidFill><a:srgbClr val='000000'>{colorTransform}</a:srgbClr></a:solidFill>{ends}</a:ln></p:spPr></p:cxnSp>
            """);
        return Assert.Single(DrawingMlSceneReader.Read(xml, "pptx", "/ppt/slides/slide1.xml", null));
    }

    private static string Render(WorkbookDrawingObject shape) => ReadableHtmlWriter.WriteDocument(
        new("doc", "en", DocumentFormatKind.Pptx, [new ReadableSourceDrawingScene(new("source", 400, 300, [shape]))])).Html;

    private static XmlElement Load(string text)
    {
        var xml = new XmlDocument { XmlResolver = null };
        xml.LoadXml(text);
        return xml.DocumentElement!;
    }

    private static XmlElement Shape(string properties) => Load($"<a:sp xmlns:a='{A}'><a:spPr>{properties}</a:spPr></a:sp>");

    private static string WordShape(string preset, string fill) =>
        "<w:p><w:r><w:drawing><wp:inline><wp:extent cx='952500' cy='476250'/><wp:docPr id='7' name='Shape 7'/><wp:cNvGraphicFramePr/>" +
        "<a:graphic><a:graphicData uri='http://schemas.microsoft.com/office/word/2010/wordprocessingShape'><wps:wsp><wps:cNvSpPr/>" +
        $"<wps:spPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='952500' cy='476250'/></a:xfrm><a:prstGeom prst='{preset}'><a:avLst/></a:prstGeom>{fill}<a:ln><a:noFill/></a:ln></wps:spPr>" +
        "<wps:bodyPr/></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>";

    private async Task<string> ExportHtml(string source)
    {
        var htmlPath = Path.ChangeExtension(source, ".html");
        await new DocumentService(null, null, discoverPdfRasterizer: false).ExportReadableAsync(new ReadableDocumentExportOptions(source, htmlPath,
            ContentPolicy: "visible", Language: "en", OutputFormat: ReadableOutputFormat.Html));
        return await File.ReadAllTextAsync(htmlPath);
    }

    private string WriteDocx(string name, string body, string settings = "")
    {
        var path = Path.Combine(root, name + ".docx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Write(string part, string xml)
        {
            using var writer = new StreamWriter(zip.CreateEntry(part).Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }
        Write("[Content_Types].xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='xml' ContentType='application/xml'/><Default Extension='png' ContentType='image/png'/><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
        Write("_rels/.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rDocument' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>");
        Write("word/document.xml", "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships' xmlns:wp='http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main' xmlns:wps='http://schemas.microsoft.com/office/word/2010/wordprocessingShape'><w:body>" + body + "<w:sectPr/></w:body></w:document>");
        Write("word/_rels/document.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rImage' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/image' Target='media/image1.png'/></Relationships>");
        Write("word/settings.xml", "<w:settings xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'>" + settings + "</w:settings>");
        Write("word/theme/theme1.xml", $"<a:theme xmlns:a='{A}' name='t'><a:themeElements><a:clrScheme name='t'><a:dk1><a:srgbClr val='112233'/></a:dk1><a:lt1><a:srgbClr val='FFEEDD'/></a:lt1>" +
            "<a:dk2><a:srgbClr val='445566'/></a:dk2><a:lt2><a:srgbClr val='EEDDCC'/></a:lt2><a:accent1><a:srgbClr val='4472C4'/></a:accent1></a:clrScheme></a:themeElements></a:theme>");
        using var image = zip.CreateEntry("word/media/image1.png").Open();
        image.Write(PngRasterImage.Encode(4, 2, [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0]));
        return path;
    }
}
