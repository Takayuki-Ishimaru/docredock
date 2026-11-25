using System.Xml;
using DocRedock.Formats.OpenXml.Drawing;

namespace DocRedock.Tests.Render;

public sealed class RemainingDrawingPaintTests
{
    [Theory]
    [InlineData("<a:scrgbClr r='50000' g='50000' b='50000'/>", "#BCBCBC")]
    [InlineData("<a:scrgbClr r='100000' g='0' b='0'/>", "#FF0000")]
    [InlineData("<a:hslClr hue='7200000' sat='100000' lum='50000'/>", "#00FF00")]
    [InlineData("<a:prstClr val='dkBlue'/>", "#00008B")]
    [InlineData("<a:scrgbClr r='1000' g='1000' b='1000'/>", "#191919")]
    [InlineData("<a:prstClr val='black'/>", "#000000")]
    [InlineData("<a:prstClr val='white'/>", "#FFFFFF")]
    [InlineData("<a:prstClr val='medVioletRed'/>", "#C71585")]
    [InlineData("<a:prstClr val='ltGoldenrodYellow'/>", "#FAFAD2")]
    [InlineData("<a:prstClr val='dkSlateGrey'/>", "#2F4F4F")]
    public void Valid_color_encodings_do_not_silently_turn_into_no_fill(string color, string expected)
    {
        var result = DrawingMlStyleReader.Read(Shape("<a:solidFill>" + color + "</a:solidFill>"));
        Assert.Equal("solid", result.FillPaint.Kind);
        Assert.Equal(expected, result.FillPaint.Color);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Unknown_color_choice_is_diagnosed_instead_of_silently_removing_a_meaningful_fill()
    {
        var result = DrawingMlStyleReader.Read(Shape("<a:solidFill><a:unknownColor val='red'/></a:solidFill>"));
        Assert.Equal("none", result.FillPaint.Kind);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Path_gradient_is_not_silently_reinterpreted_as_linear()
    {
        var result = DrawingMlStyleReader.Read(Shape("""
            <a:gradFill><a:gsLst><a:gs pos='0'><a:srgbClr val='FF0000'/></a:gs><a:gs pos='100000'><a:srgbClr val='0000FF'/></a:gs></a:gsLst>
            <a:path path='circle'><a:fillToRect l='50000' t='50000' r='50000' b='50000'/></a:path></a:gradFill>
            """));
        Assert.True(result.FillPaint.Kind != "linear" || result.Warnings.Any(w => w.Contains("gradient", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Linear_RGB_color_keeps_alpha_after_color_space_conversion()
    {
        var result = DrawingMlStyleReader.Read(Shape("<a:solidFill><a:scrgbClr r='50000' g='50000' b='50000'><a:alpha val='40000'/></a:scrgbClr></a:solidFill>"));
        Assert.Equal("#BCBCBC", result.FillPaint.Color);
        Assert.Equal(.4, result.FillPaint.Opacity, 8);
    }

    [Theory]
    [InlineData("<a:scrgbClr r='NaN' g='0' b='0'/>")]
    [InlineData("<a:scrgbClr r='0' g='0'/>")]
    [InlineData("<a:hslClr hue='0' sat='Infinity' lum='50000'/>")]
    [InlineData("<a:prstClr val='Control'/>")]
    [InlineData("<a:prstClr val='Transparent'/>")]
    [InlineData("<a:prstClr val='notAColor'/>")]
    public void Invalid_and_system_color_inputs_are_bounded_and_diagnosed(string color)
    {
        var result = DrawingMlStyleReader.Read(Shape("<a:solidFill>" + color + "</a:solidFill>"));
        Assert.Equal("none", result.FillPaint.Kind);
        Assert.NotEmpty(result.Warnings);
    }

    private static XmlElement Shape(string properties)
    {
        var xml = new XmlDocument { XmlResolver = null };
        xml.LoadXml("<a:sp xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><a:spPr>" + properties + "</a:spPr></a:sp>");
        return xml.DocumentElement!;
    }
}
