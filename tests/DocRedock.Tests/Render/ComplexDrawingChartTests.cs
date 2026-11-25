using System.Globalization;
using System.Security;
using System.Text;
using System.Xml.Linq;
using DocRedock.Formats.OpenXml.Drawing;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

/// <summary>Native chart contracts use explicit cached values and semantic SVG data attributes.</summary>
public sealed class ComplexDrawingChartTests
{
    private const string Title = "Source <script>& title";
    private const string Name = "Series <script>& label";

    [Theory]
    [InlineData("barChart", "bar")]
    [InlineData("lineChart", "line")]
    [InlineData("pieChart", "pie")]
    [InlineData("scatterChart", "scatter")]
    public void Four_supported_types_keep_cached_data_and_emit_safe_native_svg(string sourceType, string expectedType)
    {
        var result = DrawingChartReader.Read(Chart(sourceType));
        Assert.NotNull(result.Chart);
        var chart = result.Chart!;
        Assert.Equal(expectedType, chart.Type);
        Assert.Equal(Title, chart.Title);
        Assert.True(chart.ShowLegend);
        var series = Assert.Single(chart.Series);
        Assert.Equal(Name, series.Name);
        Assert.Equal(new double?[] { 3, 7 }, series.Values);
        Assert.Equal("#336699", series.Color, ignoreCase: true);
        if (expectedType == "scatter") Assert.Equal(new double?[] { 10, 20 }, series.XValues);
        else Assert.Equal(new[] { "A<&", "B" }, series.Categories);
        Assert.Empty(result.Warnings);

        var svg = DrawingChartSvg.Render(chart, 320, 180);
        Assert.NotNull(svg);
        Assert.DoesNotContain("<script", svg!, StringComparison.OrdinalIgnoreCase);
        var root = XDocument.Parse(svg!).Root!;
        Assert.Equal("svg", root.Name.LocalName);
        Assert.Equal("http://www.w3.org/2000/svg", root.Name.NamespaceName);
        Assert.Equal(expectedType, (string?)root.Attribute("data-chart-type"));
        Assert.Contains(Title, root.Value, StringComparison.Ordinal);
        Assert.Contains(Name, root.Value, StringComparison.Ordinal);
        Assert.Equal(320, double.Parse(root.Attribute("width")!.Value, CultureInfo.InvariantCulture));
        Assert.Equal(180, double.Parse(root.Attribute("height")!.Value, CultureInfo.InvariantCulture));
        var points = Points(root);
        Assert.Equal(new[] { 0, 1 }, points.Select(PointIndex).Order().ToArray());
        Assert.Equal(new[] { 3d, 7d }, points.OrderBy(PointIndex).Select(Value).ToArray());
    }

    [Fact]
    public void Horizontal_stacked_bars_keep_negative_and_zero_values_without_inventing_a_legend()
    {
        var result = DrawingChartReader.Read(Chart("barChart",
            values: Cache(3, (0, "-4"), (1, "0"), (2, "6")), horizontal: true, grouping: "stacked", legend: false));
        Assert.NotNull(result.Chart);
        Assert.True(result.Chart.Horizontal);
        Assert.Equal("stacked", result.Chart.Grouping);
        Assert.False(result.Chart.ShowLegend);
        Assert.Equal(new double?[] { -4, 0, 6 }, Assert.Single(result.Chart.Series).Values);
        Assert.Empty(result.Warnings);
        var svg = DrawingChartSvg.Render(result.Chart, 320, 180);
        Assert.NotNull(svg);
        var points = Points(XDocument.Parse(svg!).Root!);
        Assert.Contains(points, point => PointIndex(point) == 0 && Value(point) == -4);
        Assert.Contains(points, point => PointIndex(point) == 2 && Value(point) == 6);
        Assert.All(points, point => Assert.True(double.IsFinite(Value(point))));
    }

    [Fact]
    public void Sparse_cache_indices_keep_missing_points_in_place_instead_of_shifting_values_left()
    {
        var result = DrawingChartReader.Read(Chart("barChart", values: Cache(4, (0, "2"), (2, "-4"))));
        Assert.NotNull(result.Chart);
        Assert.Equal(new double?[] { 2, null, -4, null }, Assert.Single(result.Chart!.Series).Values);
        Assert.Empty(result.Warnings);
        var svg = DrawingChartSvg.Render(result.Chart, 320, 180);
        Assert.NotNull(svg);
        var points = Points(XDocument.Parse(svg!).Root!);
        Assert.Equal(new[] { 0, 2 }, points.Select(PointIndex).Order().ToArray());
        Assert.Equal(new[] { 2d, -4d }, points.OrderBy(PointIndex).Select(Value).ToArray());
    }

    [Fact]
    public void Scatter_points_keep_the_original_x_y_index_pairing_across_a_gap()
    {
        var result = DrawingChartReader.Read(Chart("scatterChart",
            values: Cache(3, (0, "2"), (2, "6")), xvalues: Cache(3, (0, "1"), (1, "2"), (2, "3"))));
        Assert.NotNull(result.Chart);
        var series = Assert.Single(result.Chart!.Series);
        Assert.Equal(new double?[] { 1, 2, 3 }, series.XValues);
        Assert.Equal(new double?[] { 2, null, 6 }, series.Values);
        var svg = DrawingChartSvg.Render(result.Chart, 320, 180);
        Assert.NotNull(svg);
        var points = Points(XDocument.Parse(svg!).Root!).OrderBy(PointIndex).ToArray();
        Assert.Equal(new[] { 0, 2 }, points.Select(PointIndex));
        Assert.Equal(new[] { 1d, 3d }, points.Select(point => double.Parse(point.Attribute("data-x-value")!.Value, CultureInfo.InvariantCulture)));
        Assert.Equal(new[] { 2d, 6d }, points.Select(Value));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Nonfinite_values_become_named_gaps_and_are_not_drawn_as_real_numbers()
    {
        var result = DrawingChartReader.Read(Chart("lineChart",
            values: Cache(4, (0, "NaN"), (1, "Infinity"), (2, "-Infinity"), (3, "5"))));
        Assert.NotNull(result.Chart);
        Assert.Equal(new double?[] { null, null, null, 5 }, Assert.Single(result.Chart!.Series).Values);
        Assert.NotEmpty(result.Warnings);
        var svg = DrawingChartSvg.Render(result.Chart, 320, 180);
        if (svg is null) return; // A renderer may require two finite points before drawing a line.
        var points = Points(XDocument.Parse(svg).Root!);
        Assert.Equal(new[] { 3 }, points.Select(PointIndex).ToArray());
        Assert.DoesNotContain("NaN", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void All_zero_pie_keeps_its_data_but_does_not_invent_equal_positive_slices()
    {
        var result = DrawingChartReader.Read(Chart("pieChart", values: Cache(2, (0, "0"), (1, "0"))));
        Assert.NotNull(result.Chart);
        Assert.Equal(new double?[] { 0, 0 }, Assert.Single(result.Chart!.Series).Values);
        // Excel leaves the plot of such a pie empty and keeps its legend.
        var root = XDocument.Parse(Assert.IsType<string>(DrawingChartSvg.Render(result.Chart, 320, 180))).Root!;
        Assert.Empty(Points(root));
        Assert.DoesNotContain(root.Descendants(), element => element.Name.LocalName == "path");
        Assert.Contains(root.Descendants(), element => (string?)element.Attribute("data-chart-label") == "legend" && element.Value == "B");
    }

    [Fact]
    public void A_three_dimensional_surface_is_diagnosed_and_drawn_as_a_top_view_never_as_another_chart_type()
    {
        // A surface is drawn from above as a contour chart; bar, line, area and pie 3-D charts are drawn flat. Both with a warning.
        var result = DrawingChartReader.Read(Chart("surface3DChart"));
        Assert.Contains(result.Warnings, warning => warning.Contains("3-D surface chart", StringComparison.Ordinal));
        Assert.Equal("surface", result.Chart!.Type);
        var root = XDocument.Parse(Assert.IsType<string>(DrawingChartSvg.Render(result.Chart, 320, 180))).Root!;
        Assert.Equal("surface", (string?)root.Attribute("data-chart-type"));
        Assert.All(Points(root), cell => Assert.NotNull(cell.Attribute("data-band")));
    }

    private static XElement[] Points(XElement root) => root.Descendants()
        .Where(element => element.Attribute("data-series-index") is not null && element.Attribute("data-point-index") is not null)
        .ToArray();
    private static int PointIndex(XElement element) => int.Parse(element.Attribute("data-point-index")!.Value, CultureInfo.InvariantCulture);
    private static double Value(XElement element) => double.Parse(element.Attribute("data-value")!.Value, CultureInfo.InvariantCulture);

    private static string Cache(int count, params (int Index, string Value)[] points) =>
        "<c:numLit><c:formatCode>General</c:formatCode><c:ptCount val=\"" + count + "\"/>" +
        string.Concat(points.Select(point => $"<c:pt idx=\"{point.Index}\"><c:v>{SecurityElement.Escape(point.Value)}</c:v></c:pt>")) + "</c:numLit>";

    private static byte[] Chart(string type, string? values = null, string? xvalues = null,
        bool horizontal = false, string grouping = "clustered", bool legend = true)
    {
        values ??= Cache(2, (0, "3"), (1, "7"));
        xvalues ??= Cache(2, (0, "10"), (1, "20"));
        var categories = "<c:strLit><c:ptCount val=\"2\"/><c:pt idx=\"0\"><c:v>A&lt;&amp;</c:v></c:pt><c:pt idx=\"1\"><c:v>B</c:v></c:pt></c:strLit>";
        var series = "<c:ser><c:idx val=\"0\"/><c:order val=\"0\"/><c:tx><c:v>" + SecurityElement.Escape(Name) + "</c:v></c:tx>" +
            "<c:spPr><a:solidFill><a:srgbClr val=\"336699\"/></a:solidFill></c:spPr>" +
            (type == "scatterChart" ? "<c:xVal>" + xvalues + "</c:xVal><c:yVal>" + values + "</c:yVal>" :
                "<c:cat>" + categories + "</c:cat><c:val>" + values + "</c:val>") + "</c:ser>";
        var properties = type is "barChart" or "bar3DChart"
            ? $"<c:barDir val=\"{(horizontal ? "bar" : "col")}\"/><c:grouping val=\"{grouping}\"/>" : "";
        var xml = """
            <c:chartSpace xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart"
            xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><c:chart>
            """ + "<c:title><c:tx><c:rich><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>" + SecurityElement.Escape(Title) +
            "</a:t></a:r></a:p></c:rich></c:tx></c:title><c:plotArea><c:" + type + ">" + properties + series +
            "</c:" + type + "></c:plotArea>" + (legend ? "<c:legend><c:legendPos val=\"r\"/></c:legend>" : "") +
            "<c:dispBlanksAs val=\"gap\"/></c:chart></c:chartSpace>";
        return Encoding.UTF8.GetBytes(xml);
    }
}
