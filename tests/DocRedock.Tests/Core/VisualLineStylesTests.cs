using DocRedock.Core.Documents;

namespace DocRedock.Tests.Core;

public sealed class VisualLineStylesTests
{
    [Theory]
    [InlineData(new double[0], 1, null)]
    [InlineData(new double[] { 0 }, 1, null)]
    [InlineData(new double[] { 3, 0 }, 1, null)]
    [InlineData(new double[] { -1, 2 }, 1, null)]
    [InlineData(new double[] { 3 }, 1, "dashed")]
    [InlineData(new double[] { 4, 3 }, 1, "dashed")]
    [InlineData(new double[] { 4, 3, 1, 3 }, 1, "dashed")]
    [InlineData(new double[] { 1, 1 }, 1, "dotted")]
    [InlineData(new double[] { 0, 2 }, 1, "dotted")]
    [InlineData(new double[] { 2, 2 }, 2, "dotted")]
    [InlineData(new double[] { 2, 2 }, 0.5, "dashed")]
    [InlineData(new double[] { 6, 6 }, 2, "dashed")]
    public void Pdf_dash_arrays_classify_by_dash_length_relative_to_line_width(double[] dashArray, double lineWidth, string? expected) =>
        Assert.Equal(expected, VisualLineStyles.FromDashArray(dashArray, lineWidth));

    [Fact]
    public void Missing_pdf_dash_array_is_solid() => Assert.Null(VisualLineStyles.FromDashArray(null, 1));

    [Theory]
    [InlineData(null, false, null)]
    [InlineData("solid", false, null)]
    [InlineData("dash", false, "dashed")]
    [InlineData("lgDashDot", false, "dashed")]
    [InlineData("sysDash", false, "dashed")]
    [InlineData("sysDot", false, "dotted")]
    [InlineData("dot", false, "dotted")]
    [InlineData("shortdot", false, "dotted")]
    [InlineData(null, true, "dashed")]
    [InlineData("1 1", false, "dotted")]
    [InlineData("4 3", false, "dashed")]
    [InlineData("0", false, null)]
    public void Office_dash_names_map_to_line_styles(string? value, bool custom, string? expected) =>
        Assert.Equal(expected, VisualLineStyles.FromOfficeDash(value, custom));
}
