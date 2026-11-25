using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DocRedock.Formats.OpenXml.Drawing;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

/// <summary>
/// Charts that used to be refused: combinations with scatter, pie, several bar groups, horizontal bars and stacked
/// lines; logarithmic, reversed, crossing and deleted axes; legend positions; pattern and gradient fills, scheme
/// and dark backgrounds; 3-D charts drawn flat.
/// </summary>
public sealed class ChartAxisCombinationFillTests
{
    private const string Axes = "<c:axId val='1'/><c:axId val='2'/>";
    private const string SecondAxes = "<c:axId val='3'/><c:axId val='4'/>";
    private const string AxisParts = "<c:catAx><c:axId val='1'/></c:catAx><c:valAx><c:axId val='2'/><c:axPos val='l'/></c:valAx>";
    private const string SecondAxisParts = "<c:catAx><c:axId val='3'/><c:delete val='1'/></c:catAx><c:valAx><c:axId val='4'/><c:axPos val='r'/><c:crosses val='max'/></c:valAx>";
    private const string Columns = "<c:barDir val='col'/>";

    // 1. Combination charts -------------------------------------------------------------------------------------

    [Fact]
    public void A_scatter_group_on_the_category_axis_places_x_1_at_the_first_category()
    {
        var result = Read(Group("lineChart", Series("Trend", Text("A", "B", "C", "D"), Numbers("val", 2, 4, 6, 8)) + Axes) +
            Group("scatterChart", Series("Marks", Numbers("xVal", 1, 2.5, 4), Numbers("yVal", 8, 4, 0), index: 1) + Axes), AxisParts);
        Assert.Empty(result.Warnings);
        var chart = result.Chart!;
        Assert.Equal("line", chart.Type);
        Assert.True(chart.ScatterOnCategoryAxis);
        Assert.Equal([null, "scatter"], chart.Series.Select(series => series.Plot == "line" ? null : series.Plot));
        var svg = Render(result);
        var frame = Frame(svg);
        Assert.Equal(8, D(frame, "data-axis-max"));
        var marks = Points(svg).Where(point => (string?)point.Attribute("data-series-index") == "1").ToArray();
        Assert.Equal(["1", "2.5", "4"], marks.Select(mark => (string)mark.Attribute("data-x-value")!));
        // Four categories: x = 1 is the middle of the first one, x = 2.5 the border between the second and third.
        Assert.Equal(new[] { .125, .5, .875 }, marks.Select(mark => (D(mark, "cx") - D(frame, "x")) / D(frame, "width")), new Tolerance());
        Assert.Equal(new[] { 0, .5, 1 }, marks.Select(mark => (D(mark, "cy") - D(frame, "y")) / D(frame, "height")), new Tolerance());
        Assert.Equal(["A", "B", "C", "D"], Labels(svg, "category").Select(label => label.Value));
    }

    [Fact]
    public void A_scatter_group_with_axes_of_its_own_is_scaled_by_them_over_the_bars()
    {
        var axes = AxisParts + "<c:valAx><c:axId val='3'/><c:scaling><c:min val='0'/><c:max val='10'/></c:scaling><c:delete val='1'/><c:axPos val='t'/></c:valAx>" +
            "<c:valAx><c:axId val='4'/><c:scaling><c:min val='0'/><c:max val='100'/></c:scaling><c:axPos val='r'/><c:crosses val='max'/></c:valAx>";
        var result = Read(Group("barChart", Columns + Series("Units", Text("A", "B"), Numbers("val", 5, 10)) + Axes) +
            Group("scatterChart", Series("Marks", Numbers("xVal", 5, 10), Numbers("yVal", 50, 100), index: 1) + SecondAxes), axes);
        Assert.Empty(result.Warnings);
        Assert.False(result.Chart!.ScatterOnCategoryAxis);
        Assert.Equal([false, true], result.Chart.Series.Select(series => series.SecondaryAxis));
        var svg = Render(result);
        var frame = Frame(svg);
        var points = Points(svg);
        Assert.Equal(["rect", "rect", "circle", "circle"], points.Select(point => point.Name.LocalName));
        Assert.Equal(D(frame, "x") + D(frame, "width") / 2, D(points[2], "cx"), 3);
        Assert.Equal(D(frame, "y") + D(frame, "height") / 2, D(points[2], "cy"), 3);
        Assert.Equal(D(frame, "x") + D(frame, "width"), D(points[3], "cx"), 3);
        Assert.Equal(["0", "25", "50", "75", "100"], Labels(svg, "secondary-tick").Select(tick => tick.Value));
    }

    [Fact]
    public void A_pie_group_is_drawn_in_its_own_circle_behind_the_other_groups_and_its_points_join_the_legend()
    {
        var result = Read(Group("barChart", Columns + Series("Units", Text("A", "B"), Numbers("val", 5, 10)) + Axes) +
            Group("pieChart", "<c:varyColors val='1'/>" + Series("Share", Text("X", "Y"), Numbers("val", 1, 3), index: 1)), AxisParts, legend: true);
        Assert.Contains(result.Warnings, warning => warning.Contains("pie or doughnut group", StringComparison.Ordinal));
        Assert.Equal("bar", result.Chart!.Type);
        Assert.Equal("pie", result.Chart.Series[1].Plot);
        var svg = Render(result);
        var frame = Frame(svg);
        var points = Points(svg);
        Assert.Equal(["path", "path", "rect", "rect"], points.Select(point => point.Name.LocalName));
        Assert.Equal(["1", "3", "5", "10"], points.Select(point => (string)point.Attribute("data-value")!));
        // The circle fills nine tenths of the shorter side of the plot area, around its middle.
        var radius = Math.Min(D(frame, "width"), D(frame, "height")) * .45;
        Assert.Contains($"A {radius.ToString("0.###", CultureInfo.InvariantCulture)} ", (string)points[0].Attribute("d")!, StringComparison.Ordinal);
        Assert.StartsWith($"M {(D(frame, "x") + D(frame, "width") / 2).ToString("0.###", CultureInfo.InvariantCulture)} {(D(frame, "y") + D(frame, "height") / 2).ToString("0.###", CultureInfo.InvariantCulture)} ",
            (string)points[0].Attribute("d")!, StringComparison.Ordinal);
        Assert.NotEqual((string?)points[0].Attribute("fill"), (string?)points[1].Attribute("fill"));
        Assert.Equal(["Units", "X", "Y"], Labels(svg, "legend").Select(label => label.Value));
        Assert.Equal(10, D(frame, "data-axis-max"));
    }

    [Fact]
    public void Two_bar_groups_cluster_and_stack_among_their_own_series_on_their_own_axes()
    {
        var result = Read(Group("barChart", Columns + "<c:grouping val='clustered'/>" +
                Series("Plan", Text("A", "B"), Numbers("val", 4, 8)) + Series("Actual", Text("A", "B"), Numbers("val", 2, 6), index: 1) + Axes) +
            Group("barChart", Columns + "<c:grouping val='stacked'/>" +
                Series("Low", Text("A", "B"), Numbers("val", 1, 2), index: 2) + Series("High", Text("A", "B"), Numbers("val", 3, 4), index: 3) + SecondAxes),
            AxisParts + SecondAxisParts);
        Assert.Empty(result.Warnings);
        Assert.Equal([0, 0, 1, 1], result.Chart!.Series.Select(series => series.Group));
        Assert.Equal(["clustered", "clustered", "stacked", "stacked"], result.Chart.Series.Select(series => series.Grouping));
        var svg = Render(result);
        var frame = Frame(svg);
        var secondary = svg.Descendants().Single(e => (string?)e.Attribute("data-chart-secondary-axis") == "true");
        Assert.Equal(8, D(frame, "data-axis-max"));
        Assert.Equal(6, D(secondary, "data-axis-max"));
        var bars = Points(svg);
        var band = D(frame, "width") / 2;
        Assert.All(bars.Take(4), bar => Assert.Equal(band * .72 / 2, D(bar, "width"), 3));
        Assert.All(bars.Skip(4), bar => Assert.Equal(band * .72, D(bar, "width"), 3));
        // "High" of category B lies on "Low": from 2 to 6 of the secondary axis, the whole height above a third.
        var high = bars.Single(bar => (string?)bar.Attribute("data-series-index") == "3" && (string?)bar.Attribute("data-point-index") == "1");
        Assert.Equal(D(frame, "y"), D(high, "y"), 3);
        Assert.Equal(D(frame, "height") * 4 / 6, D(high, "height"), 3);
    }

    [Fact]
    public void Horizontal_bar_groups_draw_the_secondary_value_axis_above_the_plot()
    {
        var axes = "<c:catAx><c:axId val='1'/><c:axPos val='l'/></c:catAx><c:valAx><c:axId val='2'/><c:axPos val='b'/></c:valAx>" +
            "<c:catAx><c:axId val='3'/><c:delete val='1'/></c:catAx><c:valAx><c:axId val='4'/><c:scaling><c:min val='0'/><c:max val='1'/></c:scaling><c:axPos val='t'/><c:crosses val='max'/></c:valAx>";
        var result = Read(Group("barChart", "<c:barDir val='bar'/>" + Series("Units", Text("A", "B"), Numbers("val", 5, 10)) + Axes) +
            Group("barChart", "<c:barDir val='bar'/>" + Series("Rate", Text("A", "B"), Numbers("val", .5, 1), index: 1) + SecondAxes), axes);
        Assert.Empty(result.Warnings);
        Assert.True(result.Chart!.Horizontal);
        var svg = Render(result);
        var frame = Frame(svg);
        var bottom = D(frame, "y") + D(frame, "height");
        Assert.All(Ticks(svg), tick => Assert.True(D(tick, "y") > bottom));
        var above = Labels(svg, "secondary-tick");
        Assert.Equal(["0", "0.25", "0.5", "0.75", "1"], above.Select(tick => tick.Value));
        Assert.All(above, tick => Assert.InRange(D(tick, "y"), 8, D(frame, "y")));
        // 0.5 and 1 on the secondary axis are as long as 5 and 10 on the primary one.
        var bars = Points(svg);
        Assert.Equal(bars.Take(2).Select(bar => D(bar, "width")), bars.Skip(2).Select(bar => D(bar, "width")), new Tolerance());
        Assert.Equal(D(frame, "width"), D(bars[1], "width"), 3);
    }

    [Fact]
    public void A_line_group_over_horizontal_bars_runs_along_the_width_on_a_vertical_axis_of_its_own()
    {
        var axes = "<c:catAx><c:axId val='1'/><c:axPos val='l'/></c:catAx><c:valAx><c:axId val='2'/><c:axPos val='b'/></c:valAx>" +
            "<c:catAx><c:axId val='3'/><c:delete val='1'/><c:axPos val='b'/></c:catAx><c:valAx><c:axId val='4'/><c:scaling><c:min val='0'/><c:max val='4'/></c:scaling><c:axPos val='r'/><c:crosses val='max'/></c:valAx>";
        var result = Read(Group("barChart", "<c:barDir val='bar'/>" + Series("Units", Text("A", "B"), Numbers("val", 5, 10)) + Axes) +
            Group("lineChart", Series("Trend", Text("A", "B"), Numbers("val", 2, 4), index: 1) + SecondAxes), axes);
        Assert.Empty(result.Warnings);
        var svg = Render(result);
        var frame = Frame(svg);
        var right = D(frame, "x") + D(frame, "width");
        var bars = Points(svg).Where(point => point.Name.LocalName == "rect").ToArray();
        Assert.Equal(new[] { D(frame, "width") / 2, D(frame, "width") }, bars.Select(bar => D(bar, "width")), new Tolerance());
        var line = Points(svg).Where(point => point.Name.LocalName == "circle").ToArray();
        Assert.Equal(new[] { D(frame, "x") + D(frame, "width") / 4, D(frame, "x") + D(frame, "width") * 3 / 4 }, line.Select(point => D(point, "cx")), new Tolerance());
        Assert.Equal(new[] { D(frame, "y") + D(frame, "height") / 2, D(frame, "y") }, line.Select(point => D(point, "cy")), new Tolerance());
        Assert.All(Labels(svg, "secondary-tick"), tick => Assert.True(D(tick, "x") > right));
        Assert.Equal(["A", "B"], Labels(svg, "category").Select(label => label.Value));
    }

    [Fact]
    public void A_stacked_line_group_of_a_combination_stacks_its_own_series_only()
    {
        var result = Read(Group("barChart", Columns + Series("Units", Text("A", "B"), Numbers("val", 5, 10)) + Axes) +
            Group("lineChart", "<c:grouping val='stacked'/>" + Series("First", Text("A", "B"), Numbers("val", 1, 2), index: 1) +
                Series("Second", Text("A", "B"), Numbers("val", 3, 4), index: 2) + Axes), AxisParts);
        Assert.Empty(result.Warnings);
        var svg = Render(result);
        var frame = Frame(svg);
        Assert.Equal(10, D(frame, "data-axis-max"));
        double Y(double value) => D(frame, "y") + D(frame, "height") * (1 - value / 10);
        var line = Points(svg).Where(point => point.Name.LocalName == "circle").ToArray();
        Assert.Equal(["1", "2", "3", "4"], line.Select(point => (string)point.Attribute("data-value")!));
        Assert.Equal(new[] { Y(1), Y(2), Y(4), Y(6) }, line.Select(point => D(point, "cy")), new Tolerance());
    }

    [Fact]
    public void A_combination_saved_before_groups_had_a_number_and_a_grouping_is_drawn_as_before()
    {
        var result = Read(Group("barChart", Columns + "<c:grouping val='stacked'/>" + Series("Plan", Text("A", "B"), Numbers("val", 4, 8)) +
                Series("Actual", Text("A", "B"), Numbers("val", 2, 6), index: 1) + Axes) +
            Group("lineChart", Series("Trend", Text("A", "B"), Numbers("val", 3, 12), index: 2) + SecondAxes), AxisParts + SecondAxisParts);
        var chart = result.Chart!;
        // A scene of an earlier version has only the type and the axis of each series; the bars take the grouping of the chart.
        var saved = chart with { Series = chart.Series.Select(series => series with { Group = 0, Grouping = null }).ToArray() };
        Assert.Equal("stacked", saved.Grouping);
        Assert.Equal(DrawingChartSvg.Render(chart, 480, 320), DrawingChartSvg.Render(saved, 480, 320));
    }

    [Theory]
    [InlineData("lineChart", "", "value axis of horizontal bars")]
    [InlineData("scatterChart", "", "scatter group combined with horizontal bars")]
    public void Groups_that_cannot_join_horizontal_bars_stay_a_diagnosed_fallback(string second, string settings, string reason)
    {
        var result = Read(Group("barChart", "<c:barDir val='bar'/>" + Series("Units", Text("A", "B"), Numbers("val", 5, 10)) + Axes) +
            Group(second, settings + Series("Other", Text("A", "B"), Numbers("val", 1, 2), index: 1) + Axes), AxisParts);
        Assert.Null(result.Chart);
        Assert.Contains(result.Warnings, warning => warning.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void Two_pie_groups_beside_other_groups_stay_a_diagnosed_fallback()
    {
        // A pie and a doughnut alone are drawn (ChartSpecialTypeTests); with a bar group they have no shared layout.
        var result = Read(Group("pieChart", Series("One", Text("A", "B"), Numbers("val", 5, 10))) +
            Group("doughnutChart", Series("Two", Text("A", "B"), Numbers("val", 1, 2), index: 1)) +
            Group("barChart", "<c:barDir val='col'/>" + Series("Three", Text("A", "B"), Numbers("val", 1, 2), index: 2) + Axes), AxisParts);
        Assert.Null(result.Chart);
        Assert.Contains(result.Warnings, warning => warning.Contains("More than one pie or doughnut group", StringComparison.Ordinal));
    }

    // 2. Axis options -------------------------------------------------------------------------------------------

    [Fact]
    public void A_logarithmic_axis_has_ticks_at_the_powers_of_its_base_and_leaves_out_values_it_cannot_show()
    {
        var axis = "<c:catAx><c:axId val='1'/></c:catAx><c:valAx><c:axId val='2'/><c:scaling><c:logBase val='10'/></c:scaling><c:axPos val='l'/></c:valAx>";
        var result = Read(Group("lineChart", Series("Sales", Text("A", "B", "C", "D"), Numbers("val", 2, 10, 500, 0)) + Axes), axis);
        Assert.Equal(["Values of zero or less cannot be plotted on a logarithmic axis, as Excel warns; those points are left out."], result.Warnings);
        Assert.Equal(10, result.Chart!.ValueAxisOptions!.LogBase);
        var svg = Render(result);
        var frame = Frame(svg);
        Assert.Equal(1, D(frame, "data-axis-min")); Assert.Equal(1000, D(frame, "data-axis-max"));
        var ticks = Ticks(svg);
        Assert.Equal(["1", "10", "100", "1000"], ticks.Select(tick => tick.Value));
        // The powers are evenly spaced.
        Assert.Equal(new[] { 1, 2 / 3d, 1 / 3d, 0 }, ticks.Select(tick => (D(tick, "y") - 3 - D(frame, "y")) / D(frame, "height")), new Tolerance());
        var points = Points(svg);
        Assert.Equal(["2", "10", "500"], points.Select(point => (string)point.Attribute("data-value")!));
        Assert.Equal(D(frame, "y") + D(frame, "height") * 2 / 3, D(points[1], "cy"), 3);
        Assert.Equal(D(frame, "y") + D(frame, "height") * (1 - Math.Log10(500) / 3), D(points[2], "cy"), 3);

        // Bars start at the minimum of a logarithmic axis, where the category axis lies; explicit bounds are kept.
        var bounded = "<c:catAx><c:axId val='1'/></c:catAx><c:valAx><c:axId val='2'/><c:scaling><c:logBase val='2'/><c:max val='64'/><c:min val='4'/></c:scaling><c:axPos val='l'/></c:valAx>";
        var bars = Render(Read(Group("barChart", Columns + Series("Sales", Text("A", "B"), Numbers("val", 16, 64)) + Axes), bounded));
        frame = Frame(bars);
        Assert.Equal(["4", "8", "16", "32", "64"], Ticks(bars).Select(tick => tick.Value));
        Assert.Equal(new[] { D(frame, "height") / 2, D(frame, "height") }, Points(bars).Select(bar => D(bar, "height")), new Tolerance());
        Assert.All(Points(bars), bar => Assert.Equal(D(frame, "y") + D(frame, "height"), D(bar, "y") + D(bar, "height"), 3));
    }

    [Fact]
    public void A_reversed_value_axis_hangs_the_bars_from_the_top_where_the_category_axis_lies()
    {
        var axis = "<c:catAx><c:axId val='1'/></c:catAx><c:valAx><c:axId val='2'/><c:scaling><c:orientation val='maxMin'/></c:scaling><c:axPos val='l'/></c:valAx>";
        var result = Read(Group("barChart", Columns + Series("Sales", Text("A", "B"), Numbers("val", 2, 4), "<c:dLbls><c:showVal val='1'/></c:dLbls>") + Axes), axis);
        Assert.Empty(result.Warnings);
        Assert.True(result.Chart!.ValueAxisOptions!.Reversed);
        var svg = Render(result);
        var frame = Frame(svg);
        var bars = Points(svg);
        Assert.All(bars, bar => Assert.Equal(D(frame, "y"), D(bar, "y"), 3));
        Assert.Equal(new[] { D(frame, "height") / 2, D(frame, "height") }, bars.Select(bar => D(bar, "height")), new Tolerance());
        // Zero is at the top, four at the bottom; the category labels follow their axis to the top.
        var ticks = Ticks(svg);
        Assert.Equal(["0", "1", "2", "3", "4"], ticks.Select(tick => tick.Value));
        Assert.Equal(D(frame, "y") + 3, D(ticks[0], "y"), 3);
        Assert.Equal(D(frame, "y") + D(frame, "height") + 3, D(ticks[4], "y"), 3);
        Assert.All(Labels(svg, "category"), label => Assert.InRange(D(label, "y"), 8, D(frame, "y")));
        Assert.Equal(D(frame, "y"), D(svg.Descendants().Single(e => e.Attribute("data-chart-baseline") is not null), "y1"), 3);
        Assert.Equal(320 - 8, D(frame, "y") + D(frame, "height"), 3);
        // The value labels lie beyond the ends of the bars, below them.
        Assert.Equal(D(bars[0], "y") + D(bars[0], "height") + 10, D(Labels(svg, "data")[0], "y"), 3);
    }

    [Fact]
    public void A_reversed_category_axis_runs_from_the_right_and_takes_the_value_axis_to_the_right()
    {
        var axis = "<c:catAx><c:axId val='1'/><c:scaling><c:orientation val='maxMin'/></c:scaling></c:catAx><c:valAx><c:axId val='2'/><c:axPos val='r'/></c:valAx>";
        string Chart(string type, string settings) => Group(type, settings + Series("First", Text("A", "B"), Numbers("val", 2, 4)) +
            Series("Second", Text("A", "B"), Numbers("val", 3, 1), index: 1) + Axes);
        var result = Read(Chart("barChart", Columns), axis);
        Assert.Empty(result.Warnings);
        var svg = Render(result);
        var frame = Frame(svg);
        var right = D(frame, "x") + D(frame, "width");
        Assert.Equal(8, D(frame, "x"), 3);
        var bars = Points(svg);
        // Category A is on the right, and within a category the first series is on the right.
        Assert.True(D(bars[0], "x") > D(bars[1], "x"));
        Assert.True(D(bars[0], "x") > D(bars[2], "x") && D(bars[2], "x") > D(bars[1], "x"));
        var labels = Labels(svg, "category");
        Assert.Equal(["A", "B"], labels.Select(label => label.Value));
        Assert.True(D(labels[0], "x") > D(labels[1], "x"));
        Assert.All(Ticks(svg), tick => { Assert.Equal(right + 4, D(tick, "x"), 3); Assert.Equal("start", (string?)tick.Attribute("text-anchor")); });

        var line = Points(Render(Read(Chart("lineChart", ""), axis)));
        Assert.True(D(line[0], "cx") > D(line[1], "cx"));
        // c:crosses max brings the value axis back to the left.
        var back = Render(Read(Chart("barChart", Columns), axis.Replace("<c:axPos val='r'/>", "<c:axPos val='l'/><c:crosses val='max'/>", StringComparison.Ordinal)));
        Assert.All(Ticks(back), tick => Assert.Equal("end", (string?)tick.Attribute("text-anchor")));
    }

    [Fact]
    public void Crosses_and_crosses_at_move_the_axes_and_the_start_of_the_bars()
    {
        string Axis(string category, string value) => "<c:catAx><c:axId val='1'/>" + category + "</c:catAx><c:valAx><c:axId val='2'/>" +
            "<c:scaling><c:min val='0'/><c:max val='10'/></c:scaling><c:axPos val='l'/>" + value + "</c:valAx>";
        var bars = Group("barChart", Columns + Series("Sales", Text("A", "B"), Numbers("val", 2, 9)) + Axes);
        var result = Read(bars, Axis("<c:crossesAt val='5'/>", ""));
        Assert.Empty(result.Warnings);
        Assert.Equal(5, result.Chart!.CategoryAxisOptions!.CrossesAt);
        var svg = Render(result);
        var frame = Frame(svg);
        double Y(double value) => D(frame, "y") + D(frame, "height") * (1 - value / 10);
        var points = Points(svg);
        // The category axis lies at 5: the bar of 2 hangs down from it, the bar of 9 stands on it.
        Assert.Equal(new[] { Y(5), Y(9) }, points.Select(point => D(point, "y")), new Tolerance());
        Assert.Equal(new[] { Y(2) - Y(5), Y(5) - Y(9) }, points.Select(point => D(point, "height")), new Tolerance());
        Assert.Equal(Y(5), D(svg.Descendants().Single(e => e.Attribute("data-chart-baseline") is not null), "y1"), 3);

        // The category axis at the maximum: its labels are above the plot and every bar hangs from the top.
        var top = Render(Read(bars, Axis("<c:crosses val='max'/>", "")));
        Assert.All(Points(top), point => Assert.Equal(D(Frame(top), "y"), D(point, "y"), 3));
        Assert.All(Labels(top, "category"), label => Assert.True(D(label, "y") < D(Frame(top), "y")));

        // The value axis at the last category: its labels are on the right.
        var right = Render(Read(bars, Axis("", "<c:crosses val='max'/>")));
        Assert.All(Ticks(right), tick => Assert.True(D(tick, "x") > D(Frame(right), "x") + D(Frame(right), "width")));
        Assert.Equal(8, D(Frame(right), "x"), 3);
    }

    [Fact]
    public void A_deleted_axis_has_no_labels_and_needs_no_room()
    {
        var bars = Group("barChart", Columns + Series("Sales", Text("A", "B"), Numbers("val", 2, 4)) + Axes);
        var noValues = Read(bars, "<c:catAx><c:axId val='1'/></c:catAx><c:valAx><c:axId val='2'/><c:delete val='1'/><c:axPos val='l'/>" +
            "<c:title><c:tx><c:rich><a:p><a:r><a:t>Gone</a:t></a:r></a:p></c:rich></c:tx></c:title></c:valAx>");
        Assert.Empty(noValues.Warnings);
        Assert.True(noValues.Chart!.ValueAxisOptions!.Deleted);
        Assert.Null(noValues.Chart.ValueAxisTitle);
        var svg = Render(noValues);
        Assert.Empty(Ticks(svg));
        Assert.Equal(8, D(Frame(svg), "x"), 3);
        // The grid lines and the bars keep the scale of the axis.
        Assert.Equal(4, D(Frame(svg), "data-axis-max"));
        Assert.Equal(6, svg.Descendants().Count(e => e.Name.LocalName == "line"));
        Assert.Equal(D(Frame(svg), "height"), D(Points(svg)[1], "height"), 3);

        var noCategories = Render(Read(bars, "<c:catAx><c:axId val='1'/><c:delete val='1'/></c:catAx><c:valAx><c:axId val='2'/><c:axPos val='l'/></c:valAx>"));
        Assert.Empty(Labels(noCategories, "category"));
        Assert.Equal(320 - 8, D(Frame(noCategories), "y") + D(Frame(noCategories), "height"), 3);

        var noSecondary = Read(bars + Group("lineChart", Series("Rate", Text("A", "B"), Numbers("val", .5, 1), index: 1) + SecondAxes),
            AxisParts + SecondAxisParts.Replace("<c:axPos val='r'/>", "<c:delete val='1'/><c:axPos val='r'/>", StringComparison.Ordinal));
        Assert.Empty(noSecondary.Warnings);
        svg = Render(noSecondary);
        Assert.Empty(Labels(svg, "secondary-tick"));
        Assert.Equal(480 - 8, D(Frame(svg), "x") + D(Frame(svg), "width"), 3);
        Assert.Equal(1, D(svg.Descendants().Single(e => (string?)e.Attribute("data-chart-secondary-axis") == "true"), "data-axis-max"));
        Assert.Equal(D(Frame(svg), "y"), D(Points(svg).Last(), "cy"), 3);
    }

    [Theory]
    [InlineData("r")]
    [InlineData("tr")]
    [InlineData("l")]
    [InlineData("t")]
    [InlineData("b")]
    public void The_legend_position_places_the_legend_without_a_manual_layout(string position)
    {
        var result = Read(Group("barChart", Columns + Series("First", Text("A", "B"), Numbers("val", 1, 2)) +
            Series("Second", Text("A", "B"), Numbers("val", 2, 1), index: 1) + Axes), AxisParts, legendXml: "<c:legend><c:legendPos val='" + position + "'/></c:legend>");
        Assert.Empty(result.Warnings);
        Assert.Equal(position is "r" ? null : position, result.Chart!.LegendPosition);
        var svg = Render(result);
        var frame = Frame(svg);
        var legend = Labels(svg, "legend");
        Assert.Equal(["First", "Second"], legend.Select(label => label.Value));
        var swatches = svg.Descendants().Where(e => (string?)e.Attribute("data-chart-label") == "legend-swatch").ToArray();
        Assert.Equal(2, swatches.Length);
        var (left, top, right, bottom) = (D(frame, "x"), D(frame, "y"), D(frame, "x") + D(frame, "width"), D(frame, "y") + D(frame, "height"));
        switch (position)
        {
            case "r" or "tr":
                Assert.All(swatches, swatch => Assert.True(D(swatch, "x") > right));
                Assert.True(D(legend[0], "y") < D(legend[1], "y"));
                break;
            case "l":
                Assert.All(swatches, swatch => Assert.Equal(8, D(swatch, "x"), 3));
                Assert.All(legend, label => Assert.True(D(label, "x") + label.Value.Length * 6 < left - 60));
                Assert.Equal(480 - 8, right, 3);
                break;
            default:
                // One centred row above or below the plot and its labels.
                Assert.Equal(D(legend[0], "y"), D(legend[1], "y"), 3);
                Assert.Equal(480 - 8, right, 3);
                Assert.Equal(480, D(swatches[0], "x") + D(legend[1], "x") + legend[1].Value.Length * 6 + 10, 3);
                if (position == "t") Assert.True(D(legend[0], "y") < top);
                else Assert.True(D(legend[0], "y") > Labels(svg, "category").Max(label => D(label, "y")) && D(legend[0], "y") <= 320 - 8);
                break;
        }
    }

    [Fact]
    public void Horizontal_bars_list_their_categories_from_the_bottom_as_excel_does_unless_the_axis_is_reversed()
    {
        string Axis(string scaling) => "<c:catAx><c:axId val='1'/>" + scaling + "<c:axPos val='l'/></c:catAx><c:valAx><c:axId val='2'/><c:axPos val='b'/></c:valAx>";
        var bars = Group("barChart", "<c:barDir val='bar'/>" + Series("Sales", Text("A", "B"), Numbers("val", 2, 4)) + Axes);
        var svg = Render(Read(bars, Axis("")));
        Assert.True(D(Points(svg)[0], "y") > D(Points(svg)[1], "y"));
        Assert.True(D(Labels(svg, "category")[0], "y") > D(Labels(svg, "category")[1], "y"));
        Assert.All(Ticks(svg), tick => Assert.True(D(tick, "y") > D(Frame(svg), "y") + D(Frame(svg), "height")));

        // "Categories in reverse order": the first category is at the top, and the value axis follows it there.
        var reversed = Render(Read(bars, Axis("<c:scaling><c:orientation val='maxMin'/></c:scaling>")));
        Assert.True(D(Points(reversed)[0], "y") < D(Points(reversed)[1], "y"));
        Assert.True(D(Labels(reversed, "category")[0], "y") < D(Labels(reversed, "category")[1], "y"));
        Assert.Equal(5, Ticks(reversed).Length);
        Assert.All(Ticks(reversed), tick => Assert.InRange(D(tick, "y"), 8, D(Frame(reversed), "y")));
        Assert.Equal(320 - 8, D(Frame(reversed), "y") + D(Frame(reversed), "height"), 3);
    }

    [Fact]
    public void The_axes_of_a_scatter_chart_can_be_logarithmic_and_reversed()
    {
        var axes = "<c:valAx><c:axId val='1'/><c:scaling><c:logBase val='10'/></c:scaling><c:axPos val='b'/></c:valAx>" +
            "<c:valAx><c:axId val='2'/><c:scaling><c:orientation val='maxMin'/></c:scaling><c:axPos val='l'/></c:valAx>";
        var result = Read(Group("scatterChart", Series("Marks", Numbers("xVal", 1, 10, 100), Numbers("yVal", 0, 2, 4)) + Axes), axes);
        Assert.Empty(result.Warnings);
        Assert.Equal(10, result.Chart!.CategoryAxisOptions!.LogBase);
        var svg = Render(result);
        var frame = Frame(svg);
        Assert.Equal(["1", "10", "100"], Labels(svg, "x-tick").Select(tick => tick.Value));
        Assert.Equal(new[] { 0, .5, 1 }, Points(svg).Select(point => (D(point, "cx") - D(frame, "x")) / D(frame, "width")), new Tolerance());
        Assert.Equal(new[] { 0, .5, 1 }, Points(svg).Select(point => (D(point, "cy") - D(frame, "y")) / D(frame, "height")), new Tolerance());
        // The horizontal axis crosses the reversed vertical axis at zero, which is at the top.
        Assert.All(Labels(svg, "x-tick"), tick => Assert.True(D(tick, "y") < D(frame, "y")));
    }

    // 3. Fills --------------------------------------------------------------------------------------------------

    [Fact]
    public void A_pattern_fill_is_drawn_as_a_pattern_with_ids_from_the_prefix_of_the_caller()
    {
        var pattern = "<c:spPr><a:pattFill prst='pct5'><a:fgClr><a:srgbClr val='FF0000'/></a:fgClr><a:bgClr><a:srgbClr val='FFFF00'/></a:bgClr></a:pattFill></c:spPr>";
        var result = Read(Group("barChart", Columns + Series("Sales", Text("A", "B"), Numbers("val", 1, 2), pattern) + Axes), AxisParts, legend: true);
        Assert.Empty(result.Warnings);
        Assert.Equal("pattern", result.Chart!.Series[0].FillPaint!.Kind);
        Assert.Equal("#FF0000", result.Chart.Series[0].Color);
        var svg = Render(result, prefix: "chart-7");
        var definition = svg.Descendants().Single(e => e.Name.LocalName == "pattern");
        Assert.Equal("chart-7-0", (string?)definition.Attribute("id"));
        Assert.Equal("defs", definition.Parent!.Name.LocalName);
        Assert.Equal("#FFFF00", (string?)definition.Elements().First().Attribute("fill"));
        Assert.Equal("#FF0000", (string?)definition.Elements().Last().Attribute("fill"));
        Assert.All(Points(svg), bar => Assert.Equal("url(#chart-7-0)", (string?)bar.Attribute("fill")));
        Assert.Equal("url(#chart-7-0)", (string?)svg.Descendants().Single(e => (string?)e.Attribute("data-chart-label") == "legend-swatch").Attribute("fill"));
        // Two charts of one document keep their definitions apart.
        Assert.Equal("other-0", (string?)Render(result, prefix: "other").Descendants().Single(e => e.Name.LocalName == "pattern").Attribute("id"));
        // Without a prefix the chart cannot define anything: the foreground color stands for the pattern.
        var plain = Render(result);
        Assert.DoesNotContain(plain.Descendants(), e => e.Name.LocalName is "defs" or "pattern");
        Assert.All(Points(plain), bar => Assert.Equal("#FF0000", (string?)bar.Attribute("fill")));
    }

    [Fact]
    public void Gradient_fills_of_series_points_and_backgrounds_are_drawn_as_gradients()
    {
        static string Gradient(string from, string to, string angle) => "<c:spPr><a:gradFill><a:gsLst><a:gs pos='0'><a:srgbClr val='" + from +
            "'/></a:gs><a:gs pos='100000'><a:srgbClr val='" + to + "'/></a:gs></a:gsLst><a:lin ang='" + angle + "'/></a:gradFill></c:spPr>";
        var series = "<c:ser><c:idx val='0'/><c:order val='0'/><c:tx><c:v>Sales</c:v></c:tx>" + Gradient("000000", "FFFFFF", "5400000") +
            "<c:dPt><c:idx val='1'/>" + Gradient("FF0000", "0000FF", "0") + "</c:dPt>" + Text("A", "B") + Numbers("val", 1, 2) + "</c:ser>";
        var result = Read(Group("barChart", Columns + series + Axes), "<c:spPr><a:noFill/></c:spPr>" + AxisParts,
            chartSpace: Gradient("FFFFFF", "DDEEFF", "5400000"));
        Assert.Empty(result.Warnings);
        var svg = Render(result, prefix: "g");
        var gradients = svg.Descendants().Where(e => e.Name.LocalName == "linearGradient").ToArray();
        Assert.Equal(["g-0", "g-1", "g-2"], gradients.Select(gradient => (string?)gradient.Attribute("id")));
        // The chart area from top to bottom, the series from top to bottom, the point from left to right.
        Assert.Equal(["0.5", "0", "0.5", "1"], new[] { "x1", "y1", "x2", "y2" }.Select(name => (string?)gradients[1].Attribute(name)));
        Assert.Equal(["0", "0.5", "1", "0.5"], new[] { "x1", "y1", "x2", "y2" }.Select(name => (string?)gradients[2].Attribute(name)));
        Assert.Equal(["#FF0000", "#0000FF"], gradients[2].Elements().Select(stop => (string?)stop.Attribute("stop-color")));
        Assert.Equal(["0", "1"], gradients[2].Elements().Select(stop => (string?)stop.Attribute("offset")));
        Assert.Equal("url(#g-0)", (string?)svg.Descendants().Single(e => (string?)e.Attribute("data-chart-background") == "true").Attribute("fill"));
        // The plot area without a fill shows the gradient of the chart area.
        Assert.Equal("none", (string?)Frame(svg).Attribute("fill"));
        Assert.Equal(["url(#g-1)", "url(#g-2)"], Points(svg).Select(bar => (string?)bar.Attribute("fill")));

        // The gradient of a line is not a fill: it stays one color, and that is said.
        var line = Read(Group("lineChart", Series("Sales", Text("A", "B"), Numbers("val", 1, 2),
            "<c:spPr><a:ln><a:gradFill><a:gsLst><a:gs pos='0'><a:srgbClr val='000000'/></a:gs></a:gsLst></a:gradFill></a:ln></c:spPr>") + Axes), AxisParts);
        Assert.Equal(["Gradients of lines, markers and text are drawn as one representative color."], line.Warnings);
    }

    [Fact]
    public void Scheme_colors_of_the_chart_and_plot_area_are_resolved_with_the_theme()
    {
        var theme = new XmlDocument();
        theme.LoadXml("<a:theme xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><a:themeElements><a:clrScheme name='t'>" +
            "<a:dk1><a:srgbClr val='000000'/></a:dk1><a:lt1><a:srgbClr val='FFFFFF'/></a:lt1><a:dk2><a:srgbClr val='44546A'/></a:dk2><a:lt2><a:srgbClr val='E7E6E6'/></a:lt2>" +
            "<a:accent1><a:srgbClr val='4472C4'/></a:accent1></a:clrScheme></a:themeElements></a:theme>");
        var groups = Group("barChart", Columns + Series("Sales", Text("A", "B"), Numbers("val", 1, 2)) + Axes);
        var plotArea = "<c:spPr><a:solidFill><a:schemeClr val='bg2'/></a:solidFill></c:spPr>" + AxisParts;
        var chartSpace = "<c:spPr><a:solidFill><a:schemeClr val='accent1'/></a:solidFill></c:spPr>";
        var result = Read(groups, plotArea, chartSpace, theme: theme);
        Assert.Equal("#4472C4", result.Chart!.ChartFill);
        Assert.Equal("#E7E6E6", result.Chart.PlotFill);
        var svg = Render(result);
        Assert.Equal("#4472C4", (string?)svg.Descendants().Single(e => (string?)e.Attribute("data-chart-background") == "true").Attribute("fill"));
        Assert.Equal("#E7E6E6", (string?)Frame(svg).Attribute("fill"));
        // Without the theme a scheme color has no value: the default background stays.
        Assert.Null(Read(groups, plotArea, chartSpace).Chart!.ChartFill);
    }

    [Fact]
    public void A_dark_background_is_drawn_with_the_label_colors_of_the_chart()
    {
        static string TextColor(string color) => "<c:txPr><a:bodyPr/><a:p><a:pPr><a:defRPr><a:solidFill><a:srgbClr val='" + color + "'/></a:solidFill></a:defRPr></a:pPr></a:p></c:txPr>";
        var groups = Group("barChart", Columns + Series("Sales", Text("A", "B"), Numbers("val", 1, 2), "<c:dLbls><c:showVal val='1'/></c:dLbls>") + Axes);
        var dark = "<c:spPr><a:solidFill><a:srgbClr val='111111'/></a:solidFill></c:spPr>";
        var axes = "<c:spPr><a:noFill/></c:spPr><c:catAx><c:axId val='1'/></c:catAx><c:valAx><c:axId val='2'/><c:axPos val='l'/>" + TextColor("00FFFF") + "</c:valAx>";
        var result = Read(groups, axes, dark + TextColor("FFFF00"), legendXml: "<c:legend><c:legendPos val='r'/>" + TextColor("FF00FF") + "</c:legend>",
            title: "<c:title><c:tx><c:rich><a:p><a:r><a:rPr><a:solidFill><a:srgbClr val='FFFFFF'/></a:solidFill></a:rPr><a:t>Dark</a:t></a:r></a:p></c:rich></c:tx></c:title>");
        Assert.Empty(result.Warnings);
        Assert.Equal(("#FFFF00", "#FFFFFF", "#FF00FF", "#00FFFF"),
            (result.Chart!.TextColor, result.Chart.TitleColor, result.Chart.LegendColor, result.Chart.ValueAxisOptions!.TextColor));
        var svg = Render(result);
        Assert.Equal("#111111", (string?)svg.Descendants().Single(e => (string?)e.Attribute("data-chart-background") == "true").Attribute("fill"));
        string? Fill(string kind) => Labels(svg, kind).Select(label => (string?)label.Attribute("fill")).Distinct().Single();
        Assert.Equal("#FFFFFF", Fill("title"));
        Assert.Equal("#FF00FF", Fill("legend"));
        Assert.Equal("#FFFF00", Fill("category"));
        Assert.Equal("#FFFF00", Fill("data"));
        Assert.All(Ticks(svg), tick => Assert.Equal("#00FFFF", (string?)tick.Attribute("fill")));

        // A chart that names no label color: its labels would be unreadable, so they are light, and that is said.
        var bare = Read(groups, "<c:spPr><a:noFill/></c:spPr>" + AxisParts, dark);
        Assert.Equal(["The dark chart area fill has no label color in the chart; labels on it are drawn in a light color to stay readable."], bare.Warnings);
        svg = Render(bare);
        Assert.All(svg.Descendants().Where(e => e.Name.LocalName == "text"), text => Assert.Equal("#F2F2F2", (string?)text.Attribute("fill")));
        // A light background keeps the dark labels.
        var light = Render(Read(groups, AxisParts, "<c:spPr><a:solidFill><a:srgbClr val='F2F2F2'/></a:solidFill></c:spPr>"));
        Assert.All(Labels(light, "category"), label => Assert.Equal("#222", (string?)label.Attribute("fill")));
    }

    // 4. 3-D charts ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("bar3DChart", "barChart", "<c:barDir val='col'/><c:grouping val='clustered'/>")]
    [InlineData("line3DChart", "lineChart", "<c:grouping val='standard'/>")]
    [InlineData("area3DChart", "areaChart", "<c:grouping val='stacked'/>")]
    [InlineData("pie3DChart", "pieChart", "<c:varyColors val='1'/>")]
    public void A_3d_chart_is_drawn_as_its_flat_equivalent_with_a_warning(string type, string flat, string settings)
    {
        string Content(string extra) => settings + Series("First", Text("A", "B"), Numbers("val", 2, 4)) +
            Series("Second", Text("A", "B"), Numbers("val", 3, 1), index: 1) + extra + (type == "pie3DChart" ? "" : Axes);
        // What Excel saves with a 3-D chart: the view, and walls with an a:sp3d of their own.
        var view = "<c:view3D><c:rotX val='15'/><c:rotY val='20'/></c:view3D><c:floor><c:spPr><a:noFill/><a:sp3d contourW='9525'/></c:spPr></c:floor>" +
            "<c:backWall><c:spPr><a:noFill/><a:sp3d/></c:spPr></c:backWall>";
        var result = Read(Group(type, Content(type == "pie3DChart" ? "" : "<c:gapDepth val='150'/><c:axId val='9'/>")), type == "pie3DChart" ? "" : AxisParts + "<c:serAx><c:axId val='9'/></c:serAx>", title: view);
        Assert.Contains("The 3-D chart is drawn flat as its 2-D equivalent; depth, perspective, rotation and 3-D shapes are not reproduced.", result.Warnings);
        var expected = Read(Group(flat, Content("")), type == "pie3DChart" ? "" : AxisParts);
        Assert.Equal(DrawingChartSvg.Render(expected.Chart!, 480, 320), DrawingChartSvg.Render(result.Chart!, 480, 320));
    }

    [Fact]
    public void A_three_dimensional_surface_chart_is_drawn_as_a_top_view_with_a_warning()
    {
        var result = Read(Group("surface3DChart", Series("First", Text("A", "B"), Numbers("val", 2, 4)) + Axes), AxisParts);
        Assert.Equal("surface", result.Chart!.Type);
        Assert.Contains(result.Warnings, warning => warning.Contains("3-D surface chart is drawn as a flat top view", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("2-D equivalent", StringComparison.Ordinal));
        Assert.NotNull(DrawingChartSvg.Render(result.Chart, 480, 320));
    }

    private sealed class Tolerance : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < .01;
        public int GetHashCode(double value) => 0;
    }

    private static XDocument Render(DrawingChartReadResult result, double width = 480, double height = 320, string? prefix = null) =>
        XDocument.Parse(Assert.IsType<string>(DrawingChartSvg.Render(Assert.IsType<DocRedock.Core.Documents.DrawingChart>(result.Chart), width, height, prefix)));

    private static XElement Frame(XDocument svg) => svg.Descendants().Single(e => (string?)e.Attribute("data-chart-plot") == "true");

    private static XElement[] Points(XDocument svg) => svg.Descendants().Where(e => e.Attribute("data-point-index") is not null).ToArray();

    private static XElement[] Labels(XDocument svg, string kind) => svg.Descendants()
        .Where(e => e.Name.LocalName == "text" && (string?)e.Attribute("data-chart-label") == kind).ToArray();

    /// <summary>Tick labels of the primary value axis: the only text without a label kind.</summary>
    private static XElement[] Ticks(XDocument svg) => svg.Descendants()
        .Where(e => e.Name.LocalName == "text" && e.Attribute("data-chart-label") is null).ToArray();

    private static double D(XElement element, string name) => double.Parse(element.Attribute(name)!.Value, CultureInfo.InvariantCulture);

    private static string Group(string type, string content) => "<c:" + type + ">" + content + "</c:" + type + ">";

    private static string Series(string name, string categories, string values, string extra = "", int index = 0) =>
        "<c:ser><c:idx val='" + index + "'/><c:order val='" + index + "'/><c:tx><c:v>" + name + "</c:v></c:tx>" + (extra.StartsWith("<c:spPr", StringComparison.Ordinal) ? extra : "") +
        categories + values + (extra.StartsWith("<c:spPr", StringComparison.Ordinal) ? "" : extra) + "</c:ser>";

    private static string Text(params string[] values) =>
        "<c:cat><c:strRef><c:f>S!A2:A9</c:f><c:strCache><c:ptCount val='" + values.Length + "'/>" +
        string.Concat(values.Select((value, index) => "<c:pt idx='" + index + "'><c:v>" + value + "</c:v></c:pt>")) +
        "</c:strCache></c:strRef></c:cat>";

    private static string Numbers(string element, params double[] values) =>
        "<c:" + element + "><c:numRef><c:f>S!B2:B9</c:f><c:numCache><c:formatCode>General</c:formatCode>" +
        "<c:ptCount val='" + values.Length + "'/>" +
        string.Concat(values.Select((value, index) => "<c:pt idx='" + index + "'><c:v>" + value.ToString("R", CultureInfo.InvariantCulture) + "</c:v></c:pt>")) +
        "</c:numCache></c:numRef></c:" + element + ">";

    /// <summary>A chart part: the chart groups, then the fill and axes of the plot area.</summary>
    private static DrawingChartReadResult Read(string groups, string plotArea = "", string chartSpace = "", string title = "",
        string legendXml = "", bool legend = false, XmlDocument? theme = null) =>
        DrawingChartReader.Read(Encoding.UTF8.GetBytes(
            "<c:chartSpace xmlns:c='http://schemas.openxmlformats.org/drawingml/2006/chart' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'>" +
            "<c:chart>" + title + "<c:plotArea>" + groups + plotArea +
            "</c:plotArea>" + legendXml + (legend ? "<c:legend><c:legendPos val='r'/></c:legend>" : "") + "</c:chart>" + chartSpace + "</c:chartSpace>"), theme);
}
