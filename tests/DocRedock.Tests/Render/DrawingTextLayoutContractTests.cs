using System.Xml;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Drawing;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

public sealed class DrawingTextLayoutContractTests
{
    [Theory]
    [InlineData("horz", "horizontal-tb", "mixed")]
    [InlineData("vert", "vertical-rl", "sideways")]
    [InlineData("vert270", "sideways-lr", "mixed")]
    [InlineData("eaVert", "vertical-rl", "mixed")]
    [InlineData("wordArtVert", "vertical-lr", "upright")]
    [InlineData("wordArtVertRtl", "vertical-rl", "upright")]
    [InlineData("mongolianVert", "vertical-lr", "mixed")]
    public void Source_direction_is_explicit_and_projects_to_native_vertical_CSS(string direction, string mode, string orientation)
    {
        var warnings = new List<string>();
        var result = DrawingTextLayoutReader.Read(Body("vert='" + direction + "'"), warnings);
        Assert.Equal(direction, result.Direction);
        Assert.Equal(mode, DrawingTextLayoutCss.WritingMode(result));
        Assert.Equal(orientation, DrawingTextLayoutCss.TextOrientation(result));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Independent_text_rotation_and_upright_are_source_values_not_shape_rotation()
    {
        var result = DrawingTextLayoutReader.Read(Body("rot='5400000' upright='1' rtlCol='1'"));
        Assert.Equal(90, result.RotationDegrees, 8);
        Assert.True(result.Upright);
        Assert.True(result.RightToLeftColumns);
        Assert.Equal("horz", result.Direction);
    }

    [Fact]
    public void Defaults_and_unknown_direction_are_bounded_and_diagnosed()
    {
        var defaults = DrawingTextLayoutReader.Read(null);
        Assert.Equal("horz", defaults.Direction);
        var warnings = new List<string>();
        var unknown = DrawingTextLayoutReader.Read(Body("vert='futureDirection' rot='NaN'"), warnings);
        Assert.Equal("horz", unknown.Direction);
        Assert.True(double.IsFinite(unknown.RotationDegrees));
        Assert.NotEmpty(warnings);
    }

    private static XmlElement Body(string attributes)
    {
        var doc = new XmlDocument { XmlResolver = null };
        doc.LoadXml("<a:bodyPr xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main' " + attributes + "/>");
        return doc.DocumentElement!;
    }
}
