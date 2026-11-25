using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Drawing;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

public sealed class ComplexDrawingChartGeometryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Percent_stacks_normalize_each_category_and_sign_while_retaining_raw_values(bool horizontal)
    {
        var chart = new DrawingChart("bar", "Percent source", [
            Series("First", [2, 4, -5, 0]), Series("Second", [6, 12, -15, 0])],
            horizontal, "percentStacked", false);
        var root = Svg(chart);
        var frame = Frame(root);
        Assert.Equal(-1, D(frame, "data-axis-min")); Assert.Equal(1, D(frame, "data-axis-max"));
        var points = Points(root);
        Assert.Equal(8, points.Length);
        foreach (var index in new[] { 0, 1, 2 })
        {
            var first = Point(points, 0, index); var second = Point(points, 1, index);
            var size = D(frame, horizontal ? "width" : "height");
            Assert.Equal(size * .125, D(first, horizontal ? "width" : "height"), 2);
            Assert.Equal(size * .375, D(second, horizontal ? "width" : "height"), 2);
            if (horizontal)
                Assert.Equal(D(first, "x") + (index == 2 ? -D(second, "width") : D(first, "width")), D(second, "x"), 2);
            else if (index == 2) Assert.Equal(D(first, "y") + D(first, "height"), D(second, "y"), 2);
            else Assert.Equal(D(second, "y") + D(second, "height"), D(first, "y"), 2);
        }
        Assert.Equal(new[] { 2d, 4d, -5d, 0d }, points.Where(p => D(p, "data-series-index") == 0)
            .OrderBy(p => D(p, "data-point-index")).Select(p => D(p, "data-value")));
        Assert.Equal(new[] { 6d, 12d, -15d, 0d }, points.Where(p => D(p, "data-series-index") == 1)
            .OrderBy(p => D(p, "data-point-index")).Select(p => D(p, "data-value")));
        Assert.Equal(0, D(Point(points, 0, 3), horizontal ? "width" : "height"));
        Assert.Contains(root.Descendants(), node => node.Name.LocalName == "text" && node.Value == "100%");
        Finite(root);
    }

    [Fact]
    public void Percent_normalization_does_not_overflow_when_finite_source_totals_exceed_double_maximum()
    {
        var chart = new DrawingChart("bar", null,
            [Series(null, [double.MaxValue]), Series(null, [double.MaxValue])], false, "percentStacked", false);
        var root = Svg(chart); var frame = Frame(root);
        Assert.Equal(0, D(frame, "data-axis-min")); Assert.Equal(1, D(frame, "data-axis-max"));
        Assert.All(Points(root), point =>
        {
            Assert.Equal(double.MaxValue, D(point, "data-value"));
            Assert.Equal(D(frame, "height") / 2, D(point, "height"), 2);
        });
        Finite(root);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Referenced_explicit_axis_minus_two_to_twelve_controls_stacked_geometry(bool horizontal)
    {
        var read = DrawingChartReader.Read(Xml("barChart", "stacked", "-2", "12", horizontal,
            unrelatedAxis: true));
        Assert.NotNull(read.Chart); Assert.Empty(read.Warnings);
        Assert.Equal(-2, read.Chart.ValueAxisMinimum); Assert.Equal(12, read.Chart.ValueAxisMaximum);
        var root = Svg(read.Chart); var frame = Frame(root);
        Assert.Equal(-2, D(frame, "data-axis-min")); Assert.Equal(12, D(frame, "data-axis-max"));
        var point = Point(Points(root), 0, 0);
        Assert.Equal(D(frame, horizontal ? "width" : "height") * 4 / 14, D(point, horizontal ? "width" : "height"), 2);
        var baseline = root.Descendants().Single(node => node.Attribute("data-chart-baseline") is not null);
        Assert.Equal(horizontal ? D(baseline, "x1") : D(baseline, "y1"),
            horizontal ? D(baseline, "x2") : D(baseline, "y2"));
        Finite(root);
    }

    [Theory]
    [InlineData("line")]
    [InlineData("area")]
    [InlineData("bar")]
    public void Explicit_axis_bounds_are_kept_and_outside_values_are_clipped_without_changing_raw_data(string type)
    {
        var root = Svg(new DrawingChart(type, null, [Series("Source", [0, 5, 10])], false, "standard", false)
            { ValueAxisMinimum = 2, ValueAxisMaximum = 8 });
        Assert.Equal(2, D(Frame(root), "data-axis-min")); Assert.Equal(8, D(Frame(root), "data-axis-max"));
        var viewport = root.Descendants().Single(node => node.Attribute("data-chart-series") is not null);
        Assert.Equal("hidden", (string?)viewport.Attribute("overflow"));
        Assert.Equal(new[] { 0d, 5d, 10d }, Points(root).OrderBy(p => D(p, "data-point-index")).Select(p => D(p, "data-value")));
        Finite(root);
    }

    [Fact]
    public void Scatter_explicit_x_and_y_ranges_are_separate()
    {
        var root = Svg(new DrawingChart("scatter", null,
            [Series("Source", [2, 8]) with { XValues = new double?[] { 10, 20 } }], false, null, false)
            { XAxisMinimum = 0, XAxisMaximum = 100, ValueAxisMinimum = -2, ValueAxisMaximum = 12 });
        var frame = Frame(root); var points = Points(root).OrderBy(p => D(p, "data-point-index")).ToArray();
        Assert.Equal(D(frame, "x") + D(frame, "width") * .1, D(points[0], "cx"), 2);
        Assert.Equal(D(frame, "x") + D(frame, "width") * .2, D(points[1], "cx"), 2);
        Assert.Equal(10, D(points[0], "data-x-value")); Assert.Equal(20, D(points[1], "data-x-value"));
        Assert.Equal(-2, D(frame, "data-axis-min")); Assert.Equal(12, D(frame, "data-axis-max"));
    }

    [Theory]
    [InlineData("bar")]
    [InlineData("line")]
    [InlineData("area")]
    [InlineData("pie")]
    [InlineData("doughnut")]
    [InlineData("scatter")]
    public void Visible_title_categories_and_legend_have_space_outside_the_plot(string type)
    {
        var series = Series("First source legend", [3, 7]) with { XValues = new double?[] { 10, 20 } };
        var root = Svg(new DrawingChart(type, "A visible source chart title", [series], false, "standard", true), 400, 260);
        var frame = Frame(root); var plotBottom = D(frame, "y") + D(frame, "height");
        var title = Labels(root, "title"); var legend = Labels(root, "legend");
        Assert.NotEmpty(title); Assert.NotEmpty(legend);
        Assert.All(title, label => Assert.True(D(label, "y") < D(frame, "y")));
        Assert.All(legend, label => Assert.True(D(label, "x") >= D(frame, "x") + D(frame, "width")));
        if (type is "bar" or "line" or "area")
        {
            var categories = Labels(root, "category"); Assert.NotEmpty(categories);
            Assert.All(categories, label => Assert.True(D(label, "y") > plotBottom));
            Assert.Contains(categories, label => label.Value == "Alpha");
            Assert.Contains(categories, label => label.Value == "Beta");
        }
        if (type is "pie" or "doughnut")
        {
            Assert.Contains(legend, label => label.Value == "Alpha");
            Assert.Contains(legend, label => label.Value == "Beta");
        }
        Finite(root);
    }

    [Theory]
    [InlineData("lineChart")]
    [InlineData("areaChart")]
    public void Stacked_line_or_area_is_drawn_as_a_stack_instead_of_a_standard_plot(string type)
    {
        var read = DrawingChartReader.Read(Xml(type, "stacked", "-2", "12"));
        Assert.Equal("stacked", read.Chart!.Grouping); Assert.Empty(read.Warnings);
        var root = Svg(new DrawingChart(type == "lineChart" ? "line" : "area", null,
            [Series(null, [1, 2]), Series(null, [3, 4])], false, "stacked", false), 320, 180);
        // The second series ends on the totals 4 and 6, not on its own values 3 and 4.
        Assert.Equal(6, D(Frame(root), "data-axis-max"));
        Assert.Equal(D(Frame(root), "y"), D(Point(Points(root), 1, 1), "cy"), 2);
        Assert.Equal(4, D(Point(Points(root), 1, 1), "data-value"));
    }

    [Theory]
    [InlineData("NaN", "12")]
    [InlineData("-2", "Infinity")]
    [InlineData("12", "-2")]
    [InlineData("2", "2")]
    [InlineData("-1E308", "1E308")]
    public void Invalid_or_overflowing_source_axis_is_diagnosed_and_not_replaced_with_an_auto_axis(string min, string max)
    {
        var read = DrawingChartReader.Read(Xml("barChart", "stacked", min, max));
        Assert.Null(read.Chart); Assert.NotEmpty(read.Warnings);
    }

    [Fact]
    public void Unrepresentable_ranges_stacks_and_viewports_return_fallback_instead_of_zero_coordinates()
    {
        var extreme = new DrawingChart("bar", null, [Series(null, [-1e308, 1e308])], false, null, false);
        Assert.Null(DrawingChartSvg.Render(extreme, 320, 180));
        var stacked = new DrawingChart("bar", null,
            [Series(null, [double.MaxValue]), Series(null, [double.MaxValue])], false, "stacked", false);
        Assert.Null(DrawingChartSvg.Render(stacked, 320, 180));
        Assert.Null(DrawingChartSvg.Render(extreme with { Series = [Series(null, [1, 2])] }, double.MaxValue, 180));
        Assert.Null(DrawingChartSvg.Render(extreme with { ValueAxisMinimum = double.NaN }, 320, 180));
        Assert.Null(DrawingChartSvg.Render(new DrawingChart("bar", new string('W', 3000),
            [Series("Legend", [1])], false, null, true), 64, 64));
    }

    [Fact]
    public void Raw_point_attributes_keep_more_than_three_decimal_places()
    {
        const double original = .12345678901234567;
        var point = Assert.Single(Points(Svg(new DrawingChart("bar", null,
            [Series(null, [original])], false, null, false))));
        Assert.Equal(original, D(point, "data-value"));
        Assert.Contains(original.ToString("R", CultureInfo.InvariantCulture), point.Value);
    }

    [Fact]
    public void Point_colors_do_not_become_the_series_color_and_doughnut_source_hole_and_angle_are_kept()
    {
        var xml = Encoding.UTF8.GetString(Xml("doughnutChart", "standard", null, null))
            .Replace("<c:v>-1</c:v>", "<c:v>6</c:v>", StringComparison.Ordinal)
            .Replace("<c:cat>", "<c:dPt><c:idx val=\"1\"/><c:spPr><a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill></c:spPr></c:dPt><c:cat>", StringComparison.Ordinal)
            .Replace("</c:doughnutChart>", "<c:holeSize val=\"80\"/><c:firstSliceAng val=\"90\"/></c:doughnutChart>", StringComparison.Ordinal);
        var read = DrawingChartReader.Read(Encoding.UTF8.GetBytes(xml));
        Assert.NotNull(read.Chart); Assert.Empty(read.Warnings);
        Assert.Equal(80, read.Chart.DoughnutHolePercent); Assert.Equal(90, read.Chart.FirstSliceAngleDegrees);
        Assert.All(read.Chart.Series, series => Assert.True(string.IsNullOrEmpty(series.Color)));
        Assert.Equal("#FF0000", read.Chart.Series[0].PointColors[1]);
        var single = read.Chart with { Series = [read.Chart.Series[0] with { Values = new double?[] { 5 }, Categories = new[] { "Alpha" } }] };
        var root = Svg(single); var frame = Frame(root); var slice = Assert.Single(Points(root));
        var path = (string)slice.Attribute("d")!;
        var radii = Regex.Matches(path, @"A ([\d.]+) ([\d.]+)").Select(match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(4, radii.Length); Assert.Equal(radii[0] * .8, radii[2], 2);
        var start = Regex.Match(path, @"^M ([\d.]+) ([\d.]+)");
        Assert.Equal(D(frame, "x") + D(frame, "width") / 2 + radii[0], double.Parse(start.Groups[1].Value, CultureInfo.InvariantCulture), 2);
        Assert.Equal(D(frame, "y") + D(frame, "height") / 2, double.Parse(start.Groups[2].Value, CultureInfo.InvariantCulture), 2);
        var colored = Svg(read.Chart);
        Assert.All(Points(colored).Where(point => D(point, "data-point-index") == 1),
            point => Assert.Equal("#FF0000", (string?)point.Attribute("fill")));
    }


    [Theory]
    [InlineData("picture")]
    [InlineData("effects")]
    [InlineData("mixed")]
    public void Unsupported_source_layout_effect_or_axis_is_a_diagnosed_fallback(string feature)
    {
        var document = XDocument.Parse(Encoding.UTF8.GetString(Xml("barChart", "standard", "-2", "12")));
        var ns = document.Root!.Name.Namespace;
        var axis = document.Descendants(ns + "valAx").Single();
        if (feature == "picture")
            document.Descendants(ns + "plotArea").Single().Add(new XElement(ns + "spPr",
                new XElement(XName.Get("blipFill", "http://schemas.openxmlformats.org/drawingml/2006/main"))));
        if (feature == "effects")
            document.Root.Add(new XElement(XName.Get("effectLst", "http://schemas.openxmlformats.org/drawingml/2006/main"),
                new XElement(XName.Get("glow", "http://schemas.openxmlformats.org/drawingml/2006/main"))));
        if (feature == "mixed")
        {
            var second = new XElement(document.Descendants(ns + "barChart").Single());
            // Bar, line, area, scatter and pie groups share a plot area; a radar group cannot join them.
            second.Name = ns + "radarChart";
            document.Descendants(ns + "plotArea").Single().Add(second);
        }
        var read = DrawingChartReader.Read(Encoding.UTF8.GetBytes(document.ToString()));
        Assert.NotEmpty(read.Warnings);
        // A picture fill that cannot be read and an effect that is not drawn leave the chart itself; the mixed
        // chart has no layout.
        if (feature == "mixed") Assert.Null(read.Chart);
        else Assert.NotNull(DrawingChartSvg.Render(read.Chart!, 480, 320));
        if (feature == "picture") Assert.Contains(read.Warnings, warning => warning.Contains("Picture fill", StringComparison.Ordinal));
        if (feature == "effects") Assert.Contains(read.Warnings, warning => warning.Contains("glow", StringComparison.Ordinal));
    }

    [Fact]
    public void Scatter_reader_associates_horizontal_and_vertical_value_axis_bounds()
    {
        var document = XDocument.Parse(Encoding.UTF8.GetString(Xml("barChart", "standard", "-2", "12")));
        var ns = document.Root!.Name.Namespace;
        var chart = document.Descendants(ns + "barChart").Single();
        chart.Name = ns + "scatterChart";
        foreach (var series in chart.Elements(ns + "ser"))
        {
            series.Element(ns + "val")!.Name = ns + "yVal";
            series.Element(ns + "cat")!.Remove();
            series.Add(new XElement(ns + "xVal", new XElement(ns + "numLit",
                new XElement(ns + "ptCount", new XAttribute("val", "2")),
                new XElement(ns + "pt", new XAttribute("idx", "0"), new XElement(ns + "v", "10")),
                new XElement(ns + "pt", new XAttribute("idx", "1"), new XElement(ns + "v", "20")))));
        }
        var axis = document.Descendants(ns + "catAx").Single();
        axis.Name = ns + "valAx";
        axis.Add(new XElement(ns + "axPos", new XAttribute("val", "b")),
            new XElement(ns + "scaling", new XElement(ns + "min", new XAttribute("val", "0")),
                new XElement(ns + "max", new XAttribute("val", "100"))));
        var read = DrawingChartReader.Read(Encoding.UTF8.GetBytes(document.ToString()));
        Assert.NotNull(read.Chart); Assert.Empty(read.Warnings);
        Assert.Equal(0, read.Chart.XAxisMinimum); Assert.Equal(100, read.Chart.XAxisMaximum);
        Assert.Equal(-2, read.Chart.ValueAxisMinimum); Assert.Equal(12, read.Chart.ValueAxisMaximum);
    }

    [Theory]
    [InlineData("holeSize", "5")]
    [InlineData("holeSize", "NaN")]
    [InlineData("firstSliceAng", "361")]
    public void Invalid_source_pie_settings_are_not_replaced_by_silent_defaults(string name, string value)
    {
        var text = Encoding.UTF8.GetString(Xml("doughnutChart", "standard", null, null))
            .Replace("</c:doughnutChart>", $"<c:{name} val=\"{value}\"/></c:doughnutChart>", StringComparison.Ordinal);
        var read = DrawingChartReader.Read(Encoding.UTF8.GetBytes(text));
        Assert.Null(read.Chart); Assert.NotEmpty(read.Warnings);
    }


    [Fact]
    public void Only_the_chart_title_is_used_and_rich_paragraphs_and_cached_titles_survive()
    {
        var document = XDocument.Parse(Encoding.UTF8.GetString(Xml("barChart", "standard", "-2", "12")));
        var ns = document.Root!.Name.Namespace;
        document.Descendants(ns + "valAx").Single().Add(new XElement(ns + "title",
            new XElement(ns + "tx", new XElement(ns + "v", "Axis units"))));
        Assert.Null(DrawingChartReader.Read(Encoding.UTF8.GetBytes(document.ToString())).Chart!.Title);
        var chart = document.Descendants(ns + "chart").Single();
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        chart.AddFirst(new XElement(ns + "title", new XElement(ns + "tx", new XElement(ns + "rich",
            new XElement(a + "p", new XElement(a + "r", new XElement(a + "t", "First")), new XElement(a + "br"),
                new XElement(a + "r", new XElement(a + "t", "Second"))),
            new XElement(a + "p", new XElement(a + "r", new XElement(a + "t", "Third")))))));
        var rich = DrawingChartReader.Read(Encoding.UTF8.GetBytes(document.ToString())).Chart!;
        Assert.Equal("First\nSecond\nThird", rich.Title);
        Assert.Equal(new[] { "First", "Second", "Third" }, Labels(Svg(rich), "title").Select(label => label.Value));
        chart.Element(ns + "title")!.ReplaceWith(new XElement(ns + "title", new XElement(ns + "tx",
            new XElement(ns + "strRef", new XElement(ns + "strCache", new XElement(ns + "pt", new XAttribute("idx", "0"),
                new XElement(ns + "v", "Cached source & title")))))));
        Assert.Equal("Cached source & title", DrawingChartReader.Read(Encoding.UTF8.GetBytes(document.ToString())).Chart!.Title);
    }

    [Theory]
    [InlineData("grouping")]
    [InlineData("direction")]
    public void Unknown_grouping_or_direction_is_not_drawn_as_a_clustered_vertical_chart(string feature)
    {
        var source = Encoding.UTF8.GetString(Xml("barChart", "standard", "-2", "12"));
        source = feature == "grouping" ? source.Replace("grouping val=\"standard\"", "grouping val=\"gap\"", StringComparison.Ordinal) :
            source.Replace("barDir val=\"col\"", "barDir val=\"unknown\"", StringComparison.Ordinal);
        var read = DrawingChartReader.Read(Encoding.UTF8.GetBytes(source));
        Assert.Null(read.Chart); Assert.NotEmpty(read.Warnings);
        Assert.Null(DrawingChartSvg.Render(new DrawingChart("bar", null, [Series(null, [1, 2])], false, "gap", false), 320, 180));
    }

    [Fact]
    public void Bar_category_labels_are_centered_on_their_own_cluster_instead_of_line_chart_endpoints()
    {
        var root = Svg(new DrawingChart("bar", null,
            [Series("A", [2, 4]), Series("B", [3, 1])], false, "clustered", false));
        var labels = Labels(root, "category");
        var points = Points(root);
        for (var i = 0; i < 2; i++)
        {
            var cluster = points.Where(p => D(p, "data-point-index") == i).ToArray();
            var left = cluster.Min(p => D(p, "x"));
            var right = cluster.Max(p => D(p, "x") + D(p, "width"));
            Assert.Equal((left + right) / 2, D(labels[i], "x"), 2);
        }
    }

    private static DrawingChartSeries Series(string? name, double?[] values) =>
        new(name, values.Select((_, index) => index == 0 ? "Alpha" : index == 1 ? "Beta" : "Category " + index).ToArray(), values, [], null);
    private static XElement Svg(DrawingChart chart, double width = 400, double height = 260) =>
        XDocument.Parse(Assert.IsType<string>(DrawingChartSvg.Render(chart, width, height))).Root!;
    private static XElement Frame(XElement root) => root.Descendants().Single(node => (string?)node.Attribute("data-chart-plot") == "true");
    private static XElement[] Points(XElement root) => root.Descendants().Where(node => node.Attribute("data-point-index") is not null).ToArray();
    private static XElement Point(XElement[] points, int series, int index) =>
        points.Single(point => D(point, "data-series-index") == series && D(point, "data-point-index") == index);
    private static XElement[] Labels(XElement root, string kind) => root.Descendants().Where(node => node.Name.LocalName == "text" &&
        (string?)node.Attribute("data-chart-label") == kind).ToArray();
    private static double D(XElement element, string name) => double.Parse(element.Attribute(name)!.Value, CultureInfo.InvariantCulture);
    private static void Finite(XElement root)
    {
        Assert.DoesNotContain("NaN", root.ToString()); Assert.DoesNotContain("Infinity", root.ToString());
        foreach (var attribute in root.DescendantsAndSelf().Attributes().Where(attribute =>
                     attribute.Name.LocalName is "x" or "y" or "cx" or "cy" or "r" or "width" or "height" or "x1" or "x2" or "y1" or "y2"))
            Assert.True(double.IsFinite(double.Parse(attribute.Value, CultureInfo.InvariantCulture)), attribute.ToString());
    }

    private static byte[] Xml(string type, string grouping, string? min, string? max, bool horizontal = false, bool unrelatedAxis = false)
    {
        string Values(string first, string second) => "<c:numLit><c:ptCount val=\"2\"/><c:pt idx=\"0\"><c:v>" + first +
            "</c:v></c:pt><c:pt idx=\"1\"><c:v>" + second + "</c:v></c:pt></c:numLit>";
        string SeriesXml(int index, string first, string second) =>
            $"<c:ser><c:idx val=\"{index}\"/><c:order val=\"{index}\"/><c:tx><c:v>Series {index}</c:v></c:tx>" +
            "<c:cat><c:strLit><c:ptCount val=\"2\"/><c:pt idx=\"0\"><c:v>Alpha</c:v></c:pt><c:pt idx=\"1\"><c:v>Beta</c:v></c:pt></c:strLit></c:cat>" +
            "<c:val>" + Values(first, second) + "</c:val></c:ser>";
        var scale = (min is null ? "" : $"<c:min val=\"{min}\"/>") + (max is null ? "" : $"<c:max val=\"{max}\"/>");
        var xml = "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
            "<c:chart><c:plotArea><c:" + type + "><c:barDir val=\"" + (horizontal ? "bar" : "col") + "\"/><c:grouping val=\"" + grouping + "\"/>" +
            SeriesXml(0, "4", "-1") + (type is "pieChart" or "doughnutChart" ? "" : SeriesXml(1, "6", "-1")) +
            "<c:axId val=\"10\"/><c:axId val=\"20\"/></c:" + type + ">" +
            (unrelatedAxis ? "<c:valAx><c:axId val=\"99\"/><c:scaling><c:min val=\"-100\"/><c:max val=\"100\"/></c:scaling></c:valAx>" : "") +
            "<c:catAx><c:axId val=\"10\"/></c:catAx><c:valAx><c:axId val=\"20\"/><c:scaling>" + scale +
            "</c:scaling><c:axPos val=\"l\"/></c:valAx></c:plotArea><c:legend/></c:chart></c:chartSpace>";
        return Encoding.UTF8.GetBytes(xml);
    }
}
