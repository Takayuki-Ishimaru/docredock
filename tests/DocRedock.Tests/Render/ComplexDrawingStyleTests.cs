using System.Xml;
using DocRedock.Formats.OpenXml.Drawing;

namespace DocRedock.Tests.Render;

/// <summary>Source-based style contracts: expected colors, units and inheritance are fixed independently.</summary>
public sealed class ComplexDrawingStyleTests
{
    [Theory]
    [InlineData("FF0000", "lumMod", 50000, "#800000")]
    // Tint and shade mix in linear light, as Office does: half-way to white or black is not half of the sRGB value.
    [InlineData("FF0000", "tint", 50000, "#FFBCBC")]
    [InlineData("204060", "shade", 50000, "#142C44")]
    public void Supported_color_modifiers_produce_effective_rgb_without_an_unsupported_warning(
        string color, string modifier, int amount, string expected)
    {
        var value = DrawingMlStyleReader.Read(Shape(
            $"<a:solidFill><a:srgbClr val=\"{color}\"><a:{modifier} val=\"{amount}\"/></a:srgbClr></a:solidFill>"));
        Assert.Equal("solid", value.FillPaint.Kind);
        Assert.Equal(expected, value.FillPaint.Color, ignoreCase: true);
        Near(1, value.FillPaint.Opacity);
        Assert.Empty(value.Warnings);
    }

    [Fact]
    public void Alpha_and_alpha_modulation_are_retained_as_opacity_instead_of_dropping_the_color()
    {
        var value = DrawingMlStyleReader.Read(Shape("""
            <a:solidFill><a:srgbClr val="336699"><a:alpha val="50000"/><a:alphaMod val="50000"/></a:srgbClr></a:solidFill>
            <a:ln w="19050"><a:noFill/></a:ln>
            """));
        Assert.Equal("#336699", value.FillPaint.Color, ignoreCase: true);
        Near(.25, value.FillPaint.Opacity);
        Assert.Equal("none", value.StrokePaint.Kind);
        Assert.Empty(value.Warnings);
    }

    [Fact]
    public void Theme_fill_line_and_font_references_resolve_placeholder_colors_and_source_widths()
    {
        var value = DrawingMlStyleReader.Read(Shape("", style: """
            <xdr:style>
            <a:lnRef idx="1"><a:schemeClr val="accent1"/></a:lnRef>
            <a:fillRef idx="1"><a:schemeClr val="accent2"/></a:fillRef>
            <a:fontRef idx="major"><a:schemeClr val="accent2"/></a:fontRef>
            </xdr:style>
            """), Theme());
        Assert.Equal("solid", value.FillPaint.Kind);
        Assert.Equal("#008844", value.FillPaint.Color, ignoreCase: true);
        Assert.Equal("#FF0000", value.StrokePaint.Color, ignoreCase: true);
        Near(2, value.StrokeWidth); // 19050 EMU / 9525 = 2 CSS pixels.
        var run = Assert.Single(Assert.Single(value.Paragraphs).Runs);
        Assert.Equal("ThemeHead", run.FontFamily);
        Assert.Equal("#008844", run.Color, ignoreCase: true);
        Assert.Empty(value.Warnings);
    }

    [Fact]
    public void Direct_fill_and_explicit_no_line_override_the_theme_references()
    {
        var value = DrawingMlStyleReader.Read(Shape("""
            <a:solidFill><a:srgbClr val="ABCDEF"/></a:solidFill><a:ln><a:noFill/></a:ln>
            """, style: """
            <xdr:style><a:lnRef idx="1"><a:schemeClr val="accent1"/></a:lnRef>
            <a:fillRef idx="1"><a:schemeClr val="accent2"/></a:fillRef></xdr:style>
            """), Theme());
        Assert.Equal("#ABCDEF", value.FillPaint.Color, ignoreCase: true);
        Assert.Equal("none", value.StrokePaint.Kind);
        Assert.Empty(value.Warnings);
    }

    [Fact]
    public void Linear_gradient_retains_stop_order_alpha_angle_scaling_and_rotation_semantics()
    {
        var value = DrawingMlStyleReader.Read(Shape("""
            <a:gradFill rotWithShape="0"><a:gsLst>
            <a:gs pos="0"><a:srgbClr val="FF0000"/></a:gs>
            <a:gs pos="50000"><a:srgbClr val="00FF00"><a:alpha val="50000"/></a:srgbClr></a:gs>
            <a:gs pos="100000"><a:srgbClr val="0000FF"/></a:gs>
            </a:gsLst><a:lin ang="5400000" scaled="1"/></a:gradFill>
            """));
        var paint = value.FillPaint;
        Assert.Equal("linear", paint.Kind);
        Near(90, paint.AngleDegrees);
        Assert.True(paint.Scaled);
        Assert.False(paint.RotateWithShape);
        Assert.Equal(3, paint.Stops.Count);
        Near(0, paint.Stops[0].Position);
        Near(.5, paint.Stops[1].Position);
        Near(1, paint.Stops[2].Position);
        Assert.Equal(new[] { "#FF0000", "#00FF00", "#0000FF" }, paint.Stops.Select(stop => stop.Color.ToUpperInvariant()));
        Near(.5, paint.Stops[1].Opacity);
        Assert.Empty(value.Warnings);
    }

    [Fact]
    public void Rich_caption_keeps_distinct_runs_and_direct_false_overrides_paragraph_defaults()
    {
        var value = DrawingMlStyleReader.Read(Shape("", """
            <xdr:txBody><a:bodyPr/><a:lstStyle/><a:p>
            <a:pPr><a:defRPr b="1" sz="1200"><a:solidFill><a:srgbClr val="112233"/></a:solidFill><a:latin typeface="Consolas"/></a:defRPr></a:pPr>
            <a:r><a:rPr b="0"><a:solidFill><a:srgbClr val="FF0000"/></a:solidFill></a:rPr><a:t>RED_</a:t></a:r>
            <a:r><a:rPr i="1" sz="1800" u="sng" strike="sngStrike"><a:solidFill><a:srgbClr val="0000FF"/></a:solidFill></a:rPr><a:t>BLUE</a:t></a:r>
            </a:p></xdr:txBody>
            """));
        var runs = Assert.Single(value.Paragraphs).Runs;
        Assert.Equal(2, runs.Count);
        Assert.Equal("RED_", runs[0].Text);
        Assert.Equal("Consolas", runs[0].FontFamily);
        Near(16, runs[0].FontSize); // 12pt * 96/72.
        Assert.False(runs[0].Bold);
        Assert.Equal("#FF0000", runs[0].Color, ignoreCase: true);
        Assert.Equal("BLUE", runs[1].Text);
        Near(24, runs[1].FontSize);
        Assert.True(runs[1].Bold);
        Assert.True(runs[1].Italic);
        Assert.True(runs[1].Underline);
        Assert.True(runs[1].Strike);
        Assert.Equal("#0000FF", runs[1].Color, ignoreCase: true);
        Assert.Empty(value.Warnings);
    }

    [Fact]
    public void Paragraph_boundaries_explicit_breaks_and_source_characters_survive_without_markup_interpretation()
    {
        var value = DrawingMlStyleReader.Read(Shape("", """
            <xdr:txBody><a:bodyPr/><a:lstStyle/>
            <a:p><a:r><a:t>first</a:t></a:r><a:br/><a:r><a:t>second</a:t></a:r></a:p>
            <a:p><a:r><a:t>&lt;script&gt;&amp; source</a:t></a:r></a:p>
            </xdr:txBody>
            """));
        Assert.Equal(2, value.Paragraphs.Count);
        Assert.Equal("first\nsecond", string.Concat(value.Paragraphs[0].Runs.Select(run => run.Text)));
        Assert.Equal("<script>& source", string.Concat(value.Paragraphs[1].Runs.Select(run => run.Text)));
        Assert.Empty(value.Warnings);
    }

    private static void Near(double expected, double actual) => Assert.InRange(Math.Abs(expected - actual), 0, 1e-7);

    private static XmlElement Shape(string properties, string? text = null, string style = "")
    {
        var xml = new XmlDocument { XmlResolver = null };
        xml.LoadXml("""
            <xdr:sp xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing"
            xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
            <xdr:nvSpPr><xdr:cNvPr id="1" name="SourceShape"/><xdr:cNvSpPr/></xdr:nvSpPr>
            """ + "<xdr:spPr>" + properties + "</xdr:spPr>" + style +
            (text ?? "<xdr:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>CAPTION</a:t></a:r></a:p></xdr:txBody>") + "</xdr:sp>");
        return xml.DocumentElement!;
    }

    private static XmlDocument Theme()
    {
        var xml = new XmlDocument { XmlResolver = null };
        var fills = string.Concat(Enumerable.Repeat("<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>", 3));
        var lines = string.Concat(Enumerable.Repeat("<a:ln w=\"19050\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln>", 3));
        var effects = string.Concat(Enumerable.Repeat("<a:effectStyle><a:effectLst/></a:effectStyle>", 3));
        xml.LoadXml("""
            <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Contract"><a:themeElements>
            <a:clrScheme name="Contract"><a:dk1><a:srgbClr val="000000"/></a:dk1><a:lt1><a:srgbClr val="FFFFFF"/></a:lt1>
            <a:dk2><a:srgbClr val="222222"/></a:dk2><a:lt2><a:srgbClr val="EEEEEE"/></a:lt2>
            <a:accent1><a:srgbClr val="FF0000"/></a:accent1><a:accent2><a:srgbClr val="008844"/></a:accent2>
            <a:accent3><a:srgbClr val="333333"/></a:accent3><a:accent4><a:srgbClr val="444444"/></a:accent4>
            <a:accent5><a:srgbClr val="555555"/></a:accent5><a:accent6><a:srgbClr val="666666"/></a:accent6>
            <a:hlink><a:srgbClr val="0000FF"/></a:hlink><a:folHlink><a:srgbClr val="800080"/></a:folHlink></a:clrScheme>
            <a:fontScheme name="Contract"><a:majorFont><a:latin typeface="ThemeHead"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont>
            <a:minorFont><a:latin typeface="ThemeBody"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont></a:fontScheme>
            """ + "<a:fmtScheme name=\"Contract\"><a:fillStyleLst>" + fills + "</a:fillStyleLst><a:lnStyleLst>" + lines +
            "</a:lnStyleLst><a:effectStyleLst>" + effects + "</a:effectStyleLst><a:bgFillStyleLst>" + fills +
            "</a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>");
        return xml;
    }
}
