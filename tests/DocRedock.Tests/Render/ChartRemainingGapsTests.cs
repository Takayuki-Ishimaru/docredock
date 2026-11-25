using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocRedock.Formats.OpenXml.Drawing;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

/// <summary>
/// Chart features Excel and PowerPoint draw that used to crowd the labels or lose the whole chart: long tick
/// labels, labels and formats of single points, stacked lines and areas, combination charts, doughnut rings,
/// negative and all-zero pies, percent formats, manual layouts and background fills.
/// </summary>
public sealed class ChartRemainingGapsTests
{
    private const string Axes = "<c:axId val='1'/><c:axId val='2'/>";
    private const string AxisParts = "<c:catAx><c:axId val='1'/></c:catAx><c:valAx><c:axId val='2'/><c:axPos val='l'/></c:valAx>";

    // 1. Long tick labels ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_long_value_axis_label_widens_the_gutter_instead_of_crowding_the_edge_or_the_axis_title(bool axisTitle)
    {
        var axis = "<c:catAx><c:axId val='1'/></c:catAx><c:valAx><c:axId val='2'/><c:scaling><c:min val='0'/><c:max val='4000000'/></c:scaling><c:axPos val='l'/>" +
            (axisTitle ? "<c:title><c:tx><c:rich><a:p><a:r><a:t>Revenue</a:t></a:r></a:p></c:rich></c:tx></c:title>" : "") +
            "<c:numFmt formatCode='\"JPY \"#,##0.00' sourceLinked='0'/></c:valAx>";
        var svg = Render(Read(Group("barChart", "<c:barDir val='col'/>" + Series("Sales", Text("A", "B"), Numbers("val", 1000000, 4000000)) + Axes), axis));
        var ticks = Ticks(svg);
        Assert.Equal(["JPY 0.00", "JPY 1,000,000.00", "JPY 2,000,000.00", "JPY 3,000,000.00", "JPY 4,000,000.00"], ticks.Select(tick => tick.Value));
        // Right-aligned labels: the estimated left end of the widest one stays inside the chart, right of the title band.
        var leftEnd = ticks.Min(tick => D(tick, "x") - tick.Value.Length * .6 * 9);
        Assert.True(leftEnd >= (axisTitle ? 36 : 8) - .01, leftEnd.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(D(Frame(svg), "x") - 4, D(ticks[0], "x"), 3);
    }

    [Fact]
    public void A_tick_label_beyond_the_largest_gutter_is_cut_with_an_ellipsis_and_cjk_counts_a_full_em()
    {
        var format = "\"" + new string('W', 60) + " \"0";
        var axis = "<c:catAx><c:axId val='1'/></c:catAx><c:valAx><c:axId val='2'/><c:axPos val='l'/><c:numFmt formatCode='" + format + "' sourceLinked='0'/></c:valAx>";
        var svg = Render(Read(Group("lineChart", Series("Sales", Text("A", "B"), Numbers("val", 1, 4)) + Axes), axis), 300, 200);
        Assert.InRange(D(Frame(svg), "x"), 100, 300 * .4 + .01);
        Assert.All(Ticks(svg), tick => Assert.EndsWith("…", tick.Value, StringComparison.Ordinal));
        Assert.All(Ticks(svg), tick => Assert.True(D(tick, "x") - tick.Value.Length * .6 * 9 >= 8 - .01));

        // Eleven CJK characters need eleven em; eleven Latin letters need 0.6 em each.
        string Yen(string unit) => "<c:catAx><c:axId val='1'/></c:catAx><c:valAx><c:axId val='2'/><c:axPos val='l'/><c:numFmt formatCode='0\"" + unit + "\"' sourceLinked='0'/></c:valAx>";
        var cjk = Render(Read(Group("lineChart", Series("Sales", Text("A", "B"), Numbers("val", 1, 4)) + Axes), Yen("千円単位の売上高合計額")));
        var latin = Render(Read(Group("lineChart", Series("Sales", Text("A", "B"), Numbers("val", 1, 4)) + Axes), Yen("abcdefghijk")));
        static double Em(string text) => text.Sum(character => character >= '⺀' ? 1 : .6);
        Assert.All(Ticks(cjk), tick => Assert.Equal(11, tick.Value.Count(character => character >= '⺀')));
        Assert.Equal(8 + Ticks(cjk).Max(tick => Em(tick.Value)) * 9 + 4, D(Frame(cjk), "x"), 3);
        Assert.Equal(8 + Ticks(latin).Max(tick => Em(tick.Value)) * 9 + 4, D(Frame(latin), "x"), 3);
        Assert.True(D(Frame(cjk), "x") - D(Frame(latin), "x") >= 11 * .4 * 9 - .01);
        Assert.DoesNotContain(Ticks(cjk), tick => tick.Value.EndsWith('…'));
    }

    [Fact]
    public void Horizontal_bars_size_their_category_gutter_from_the_labels()
    {
        var names = new[] { "Quarterly net revenue", "四半期売上高の合計金額", "Costs" };
        var svg = Render(Read(Group("barChart", "<c:barDir val='bar'/>" + Series("Sales", Text(names), Numbers("val", 1, 2, 3)) + Axes), AxisParts));
        Assert.Equal(names, Labels(svg, "category").Select(label => label.Value));
        // 8 px margin, the widest label (21 Latin characters at 10 px) and 8 px to the axis.
        Assert.Equal(8 + 21 * .6 * 10 + 8, D(Frame(svg), "x"), 3);
        var crowded = Render(Read(Group("barChart", "<c:barDir val='bar'/>" + Series("Sales", Text(new string('W', 80), "B"), Numbers("val", 1, 2)) + Axes), AxisParts));
        Assert.Equal(480 * .4, D(Frame(crowded), "x"), 3);
        Assert.All(Labels(crowded, "category"), line => Assert.True(line.Value.Length * .6 * 10 <= 480 * .4 - 16 + .01));
    }

    // 2. Labels and formats of single points --------------------------------------------------------------------

    [Fact]
    public void A_point_label_overrides_delete_show_value_and_number_format_of_its_series()
    {
        var labels = "<c:dLbls><c:dLbl><c:idx val='1'/><c:delete val='1'/></c:dLbl>" +
            "<c:dLbl><c:idx val='2'/><c:numFmt formatCode='0.0\" kg\"' sourceLinked='0'/><c:showVal val='1'/></c:dLbl><c:showVal val='1'/></c:dLbls>";
        var result = Read(Group("barChart", "<c:barDir val='col'/>" + Series("Sales", Text("A", "B", "C"), Numbers("val", 8, 6, 4), labels) + Axes), AxisParts);
        Assert.Empty(result.Warnings);
        var svg = Render(result);
        Assert.Equal(["8", "4.0 kg"], Labels(svg, "data").Select(label => label.Value));
        Assert.Equal(["8", "6", "4"], Points(svg).Select(point => (string)point.Attribute("data-value")!));

        var single = "<c:dLbls><c:dLbl><c:idx val='0'/><c:showVal val='1'/></c:dLbl><c:showVal val='0'/></c:dLbls>";
        var one = Render(Read(Group("lineChart", Series("Sales", Text("A", "B"), Numbers("val", "#,##0.0", 1500, 2500), single) + Axes), AxisParts));
        Assert.Equal(["1,500.0"], Labels(one, "data").Select(label => label.Value));
    }

    [Fact]
    public void A_point_label_of_a_pie_overrides_the_percentage_and_its_format()
    {
        var labels = "<c:dLbls><c:dLbl><c:idx val='0'/><c:numFmt formatCode='0.0%' sourceLinked='0'/><c:showVal val='0'/><c:showPercent val='1'/></c:dLbl><c:showVal val='1'/></c:dLbls>";
        var svg = Render(Read(Group("pieChart", Series("Sales", Text("A", "B"), Numbers("val", 2, 6), labels))));
        Assert.Equal(["25.0%", "6"], Labels(svg, "data").Select(label => label.Value));
    }

    [Fact]
    public void A_source_linked_label_shows_the_format_code_of_its_own_point()
    {
        var values = "<c:val><c:numRef><c:f>S!B2:B4</c:f><c:numCache><c:formatCode>General</c:formatCode><c:ptCount val='3'/>" +
            "<c:pt idx='0'><c:v>0.1</c:v></c:pt><c:pt idx='1' formatCode='0.00%'><c:v>0.5</c:v></c:pt><c:pt idx='2' formatCode='\"$\"#,##0'><c:v>1234.5</c:v></c:pt></c:numCache></c:numRef></c:val>";
        string Chart(string labels) => Group("barChart", "<c:barDir val='col'/>" + Series("Sales", Text("A", "B", "C"), values, labels) + Axes);
        var linked = Read(Chart("<c:dLbls><c:numFmt formatCode='General' sourceLinked='1'/><c:showVal val='1'/></c:dLbls>"), AxisParts);
        Assert.Empty(linked.Warnings);
        var svg = Render(linked);
        Assert.Equal(["0.1", "50.00%", "$1,235"], Labels(svg, "data").Select(label => label.Value));
        Assert.Equal(["0.1", "0.5", "1234.5"], Points(svg).Select(point => (string)point.Attribute("data-value")!));
        // A label with a format of its own does not follow the cells.
        var own = Render(Read(Chart("<c:dLbls><c:numFmt formatCode='0.0' sourceLinked='0'/><c:showVal val='1'/></c:dLbls>"), AxisParts));
        Assert.Equal(["0.1", "0.5", "1234.5"], Labels(own, "data").Select(label => label.Value));
    }

    // 3. Stacked lines and areas --------------------------------------------------------------------------------

    [Theory]
    [InlineData("lineChart")]
    [InlineData("areaChart")]
    public void Stacked_lines_and_areas_plot_each_series_on_the_sum_of_the_series_before_it(string type)
    {
        var result = Read(Group(type, "<c:grouping val='stacked'/>" +
            Series("First", Text("A", "B"), Numbers("val", 2, 4)) + Series("Second", Text("A", "B"), Numbers("val", 6, 1), index: 1) + Axes), AxisParts);
        Assert.Equal("stacked", result.Chart!.Grouping);
        Assert.Empty(result.Warnings);
        var svg = Render(result);
        var frame = Frame(svg);
        Assert.Equal(0, D(frame, "data-axis-min")); Assert.Equal(8, D(frame, "data-axis-max"));
        double Y(double value) => D(frame, "y") + D(frame, "height") * (1 - value / 8);
        var points = Points(svg);
        Assert.Equal(["2", "4", "6", "1"], points.Select(point => (string)point.Attribute("data-value")!));
        Assert.Equal(new[] { Y(2), Y(4), Y(8), Y(5) }, points.Select(point => D(point, "cy")), new Tolerance());
        if (type == "areaChart")
        {
            // The second band lies between its own line and the line of the first series.
            var bands = svg.Descendants().Where(e => e.Name.LocalName == "polygon").Select(e => Coordinates((string)e.Attribute("points")!)).ToArray();
            Assert.Equal(2, bands.Length);
            Assert.Equal(new[] { Y(2), Y(4), Y(0), Y(0) }, bands[0].Select(point => point.Y), new Tolerance());
            Assert.Equal(new[] { Y(8), Y(5), Y(4), Y(2) }, bands[1].Select(point => point.Y), new Tolerance());
        }
    }

    [Theory]
    [InlineData("lineChart")]
    [InlineData("areaChart")]
    public void Percent_stacked_lines_and_areas_plot_shares_of_each_category_on_a_percent_axis(string type)
    {
        var result = Read(Group(type, "<c:grouping val='percentStacked'/>" +
            Series("First", Text("A", "B"), Numbers("val", 2, 30)) + Series("Second", Text("A", "B"), Numbers("val", 6, 10), index: 1) + Axes),
            "<c:catAx><c:axId val='1'/></c:catAx><c:valAx><c:axId val='2'/><c:axPos val='l'/><c:numFmt formatCode='0%' sourceLinked='1'/></c:valAx>");
        var svg = Render(result);
        var frame = Frame(svg);
        Assert.Equal(0, D(frame, "data-axis-min")); Assert.Equal(1, D(frame, "data-axis-max"));
        Assert.Equal(["0%", "25%", "50%", "75%", "100%"], Ticks(svg).Select(tick => tick.Value));
        double Y(double share) => D(frame, "y") + D(frame, "height") * (1 - share);
        Assert.Equal(new[] { Y(.25), Y(.75), Y(1), Y(1) }, Points(svg).Select(point => D(point, "cy")), new Tolerance());
        Assert.Equal(["2", "30", "6", "10"], Points(svg).Select(point => (string)point.Attribute("data-value")!));
    }

    // 4. Combination charts -------------------------------------------------------------------------------------

    [Fact]
    public void A_bar_and_line_combination_shares_the_axes_and_draws_the_line_over_the_bars()
    {
        var result = Read(Group("barChart", "<c:barDir val='col'/><c:grouping val='clustered'/>" +
                Series("Plan", Text("A", "B"), Numbers("val", 4, 8)) + Series("Actual", Text("A", "B"), Numbers("val", 2, 6), index: 1) + Axes) +
            Group("lineChart", "<c:grouping val='standard'/>" + Series("Trend", Text("A", "B"), Numbers("val", 3, 12), index: 2) + Axes), AxisParts, legend: true);
        Assert.Empty(result.Warnings);
        var chart = result.Chart!;
        Assert.Equal("bar", chart.Type);
        Assert.Equal(["Plan", "Actual", "Trend"], chart.Series.Select(series => series.Name));
        var svg = Render(result);
        var frame = Frame(svg);
        Assert.Equal(0, D(frame, "data-axis-min")); Assert.Equal(12, D(frame, "data-axis-max"));
        var points = Points(svg);
        Assert.Equal(["rect", "rect", "rect", "rect", "circle", "circle"], points.Select(point => point.Name.LocalName));
        Assert.Equal(["4", "8", "2", "6", "3", "12"], points.Select(point => (string)point.Attribute("data-value")!));
        var band = D(frame, "width") / 2;
        // Two bar series share each slot; the line marks the middle of the slot at its own value.
        Assert.All(points.Take(4), bar => Assert.Equal(band * .72 / 2, D(bar, "width"), 3));
        Assert.Equal(D(frame, "x") + band * 1.5, D(points[5], "cx"), 3);
        Assert.Equal(D(frame, "y"), D(points[5], "cy"), 3);
        Assert.Equal(["Plan", "Actual", "Trend"], Labels(svg, "legend").Select(label => label.Value));
        Assert.DoesNotContain(svg.Descendants(), e => e.Attribute("data-chart-secondary-axis") is not null);
    }

    [Fact]
    public void A_secondary_value_axis_is_drawn_on_the_right_and_scales_its_own_series()
    {
        var axes = "<c:catAx><c:axId val='1'/></c:catAx><c:valAx><c:axId val='2'/><c:scaling><c:min val='0'/><c:max val='10'/></c:scaling><c:axPos val='l'/></c:valAx>" +
            "<c:catAx><c:axId val='3'/><c:delete val='1'/></c:catAx><c:valAx><c:axId val='4'/><c:scaling><c:min val='0'/><c:max val='1'/></c:scaling><c:axPos val='r'/>" +
            "<c:title><c:tx><c:rich><a:p><a:r><a:t>Share</a:t></a:r></a:p></c:rich></c:tx></c:title><c:numFmt formatCode='0%' sourceLinked='0'/><c:crosses val='max'/></c:valAx>";
        var result = Read(Group("barChart", "<c:barDir val='col'/>" + Series("Units", Text("A", "B"), Numbers("val", 5, 10)) + Axes) +
            Group("lineChart", Series("Rate", Text("A", "B"), Numbers("val", .5, 1), index: 1) + "<c:axId val='3'/><c:axId val='4'/>"), axes);
        Assert.Empty(result.Warnings);
        var chart = result.Chart!;
        Assert.Equal([false, true], chart.Series.Select(series => series.SecondaryAxis));
        Assert.Equal("0%", chart.SecondaryAxisFormat);
        Assert.Equal("Share", chart.SecondaryAxisTitle);
        var svg = Render(result);
        var frame = Frame(svg);
        Assert.Equal(10, D(frame, "data-axis-max"));
        var secondary = svg.Descendants().Single(e => (string?)e.Attribute("data-chart-secondary-axis") == "true");
        Assert.Equal(0, D(secondary, "data-axis-min")); Assert.Equal(1, D(secondary, "data-axis-max"));
        var right = D(frame, "x") + D(frame, "width");
        var ticks = Labels(svg, "secondary-tick");
        Assert.Equal(["0%", "25%", "50%", "75%", "100%"], ticks.Select(tick => tick.Value));
        Assert.All(ticks, tick => Assert.True(D(tick, "x") > right && D(tick, "x") + 4 * .6 * 9 <= 480));
        Assert.Equal(["0", "2.5", "5", "7.5", "10"], Ticks(svg).Select(tick => tick.Value));
        // 0.5 and 1 on the secondary axis are as high as 5 and 10 on the primary one.
        var line = Points(svg).Where(point => point.Name.LocalName == "circle").ToArray();
        var bars = Points(svg).Where(point => point.Name.LocalName == "rect").ToArray();
        Assert.Equal(bars.Select(bar => D(bar, "y")), line.Select(point => D(point, "cy")), new Tolerance());
        Assert.Equal(["0.5", "1"], line.Select(point => (string)point.Attribute("data-value")!));
        var title = Labels(svg, "secondary-axis-title").Single();
        Assert.Equal("Share", title.Value);
        Assert.Matches(@"^translate\((\d+(\.\d+)?),", (string)title.Parent!.Attribute("transform")!);
        Assert.True(double.Parse(Regex.Match((string)title.Parent.Attribute("transform")!, @"\(([\d.]+),").Groups[1].Value, CultureInfo.InvariantCulture) > right);
    }

    [Theory]
    [InlineData("radarChart", "")]
    [InlineData("barChart", "<c:barDir val='bar'/>")]
    [InlineData("lineChart", "<c:grouping val='nonsense'/>")]
    public void Combinations_that_cannot_share_the_axes_stay_a_diagnosed_fallback(string second, string settings)
    {
        var result = Read(Group("barChart", "<c:barDir val='col'/>" + Series("Units", Text("A", "B"), Numbers("val", 5, 10)) + Axes) +
            Group(second, settings + Series("Other", Text("A", "B"), Numbers("val", 1, 2), index: 1) + Axes), AxisParts);
        Assert.Null(result.Chart);
        Assert.Contains(result.Warnings, warning => warning.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
    }

    // 5. Doughnut rings -----------------------------------------------------------------------------------------

    [Fact]
    public void A_doughnut_draws_one_ring_for_each_series_with_the_first_series_innermost()
    {
        var result = Read(Group("doughnutChart", Series("2023", Text("A", "B"), Numbers("val", 1, 3)) +
            Series("2024", Text("A", "B"), Numbers("val", 2, 2), index: 1) + "<c:holeSize val='50'/>"), legend: true);
        Assert.Empty(result.Warnings);
        var svg = Render(result);
        var slices = Points(svg);
        Assert.Equal(["0", "0", "1", "1"], slices.Select(slice => (string)slice.Attribute("data-series-index")!));
        Assert.Equal(["1", "3", "2", "2"], slices.Select(slice => (string)slice.Attribute("data-value")!));
        // "M x y A r r ... L x y A r r ...": the outer and the inner radius of each slice.
        (double Outer, double Inner) Radii(XElement slice)
        {
            var arcs = Regex.Matches((string)slice.Attribute("d")!, @"A ([\d.]+) ");
            return (double.Parse(arcs[0].Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(arcs[1].Groups[1].Value, CultureInfo.InvariantCulture));
        }
        var first = Radii(slices[0]); var second = Radii(slices[2]);
        Assert.Equal(first.Outer, second.Inner, 3);
        Assert.Equal(second.Outer * .5, first.Inner, 2);
        Assert.Equal(second.Outer - second.Inner, first.Outer - first.Inner, 2);
        Assert.Equal(["A", "B"], Labels(svg, "legend").Select(label => label.Value));
    }

    // 6. Negative and all-zero pies -----------------------------------------------------------------------------

    [Theory]
    [InlineData("pieChart")]
    [InlineData("doughnutChart")]
    public void A_negative_pie_value_is_plotted_as_its_absolute_value_and_keeps_its_sign_in_the_data(string type)
    {
        var result = Read(Group(type, Series("Sales", Text("A", "B"), Numbers("val", 3, -1)) + "<c:dLbls><c:showVal val='1'/><c:showPercent val='1'/></c:dLbls>"));
        Assert.Contains(result.Warnings, warning => warning.Contains("absolute value", StringComparison.Ordinal));
        var svg = Render(result);
        Assert.Equal(["3", "-1"], Points(svg).Select(slice => (string)slice.Attribute("data-value")!));
        Assert.Equal(["3 (75%)", "-1 (25%)"], Labels(svg, "data").Select(label => label.Value));
    }

    [Theory]
    [InlineData("pieChart")]
    [InlineData("doughnutChart")]
    public void An_all_zero_pie_keeps_its_legend_and_a_note_without_inventing_slices(string type)
    {
        var result = Read(Group(type, Series("Sales", Text("A", "B"), Numbers("val", 0, 0))), legend: true);
        var svg = Render(result);
        Assert.Empty(Points(svg));
        Assert.Equal(["A", "B"], Labels(svg, "legend").Select(label => label.Value));
        Assert.Equal("No nonzero values", Labels(svg, "note").Single().Value);
    }

    // 7. Percent formats ----------------------------------------------------------------------------------------

    [Fact]
    public void A_source_linked_percent_stacked_axis_shows_whole_percent_ticks_as_excel_does()
    {
        var result = Read(Group("barChart", "<c:barDir val='col'/><c:grouping val='percentStacked'/>" +
            Series("First", Text("A", "B"), Numbers("val", "#,##0.00", 1000, 3000)) + Axes),
            "<c:catAx><c:axId val='1'/></c:catAx><c:valAx><c:axId val='2'/><c:scaling><c:min val='0'/><c:max val='0.5'/></c:scaling><c:axPos val='l'/><c:numFmt formatCode='0%' sourceLinked='1'/></c:valAx>");
        Assert.Equal(["0%", "13%", "25%", "38%", "50%"], Ticks(Render(result)).Select(tick => tick.Value));
    }

    [Fact]
    public void A_source_linked_pie_percentage_shows_excels_default_whole_percent()
    {
        var result = Read(Group("pieChart", Series("Sales", Text("A", "B"), Numbers("val", "#,##0.00", 1, 2)) +
            "<c:dLbls><c:numFmt formatCode='General' sourceLinked='1'/><c:showPercent val='1'/></c:dLbls>"));
        Assert.Equal(["33%", "67%"], Labels(Render(result), "data").Select(label => label.Value));
    }

    // 8. Manual layout and fills --------------------------------------------------------------------------------

    [Fact]
    public void A_manual_inner_layout_places_the_plot_by_fractions_of_the_chart()
    {
        var layout = Layout("<c:layoutTarget val='inner'/><c:xMode val='edge'/><c:yMode val='edge'/><c:x val='0.3'/><c:y val='0.2'/><c:w val='0.5'/><c:h val='0.5'/>");
        var result = Read(Group("barChart", "<c:barDir val='col'/>" + Series("Sales", Text("A", "B"), Numbers("val", 1, 2)) + Axes), layout + AxisParts);
        Assert.Empty(result.Warnings);
        var frame = Frame(Render(result));
        Assert.Equal([144d, 64, 240, 160], new[] { D(frame, "x"), D(frame, "y"), D(frame, "width"), D(frame, "height") }, new Tolerance());
        // "edge" for w and h gives the right and bottom edge instead of a size.
        var edges = Layout("<c:layoutTarget val='inner'/><c:xMode val='edge'/><c:yMode val='edge'/><c:wMode val='edge'/><c:hMode val='edge'/><c:x val='0.3'/><c:y val='0.2'/><c:w val='0.9'/><c:h val='0.8'/>");
        frame = Frame(Render(Read(Group("barChart", "<c:barDir val='col'/>" + Series("Sales", Text("A", "B"), Numbers("val", 1, 2)) + Axes), edges + AxisParts)));
        Assert.Equal([144d, 64, 288, 192], new[] { D(frame, "x"), D(frame, "y"), D(frame, "width"), D(frame, "height") }, new Tolerance());
    }

    [Fact]
    public void A_manual_outer_layout_keeps_the_tick_and_category_labels_inside_its_rectangle()
    {
        var layout = Layout("<c:xMode val='edge'/><c:yMode val='edge'/><c:x val='0.25'/><c:y val='0.25'/><c:w val='0.5'/><c:h val='0.5'/>");
        var svg = Render(Read(Group("barChart", "<c:barDir val='col'/>" + Series("Sales", Text("A", "B"), Numbers("val", 1, 2)) + Axes), layout + AxisParts));
        var frame = Frame(svg);
        // The 60 px tick gutter and the 20 px category row lie inside the rectangle from 120,80 to 360,240.
        Assert.Equal([180d, 80, 180, 140], new[] { D(frame, "x"), D(frame, "y"), D(frame, "width"), D(frame, "height") }, new Tolerance());
        Assert.All(Labels(svg, "category"), label => Assert.InRange(D(label, "y"), 220, 240));
        // The default mode "factor" moves the plot from its automatic place by a fraction of the chart size.
        var automatic = Frame(Render(Read(Group("barChart", "<c:barDir val='col'/>" + Series("Sales", Text("A", "B"), Numbers("val", 1, 2)) + Axes), AxisParts)));
        var moved = Frame(Render(Read(Group("barChart", "<c:barDir val='col'/>" + Series("Sales", Text("A", "B"), Numbers("val", 1, 2)) + Axes),
            Layout("<c:x val='0.1'/><c:y val='0.05'/>") + AxisParts)));
        Assert.Equal(D(automatic, "x") + 48, D(moved, "x"), 3);
        Assert.Equal(D(automatic, "y") + 16, D(moved, "y"), 3);
    }

    [Fact]
    public void Manual_layouts_place_the_title_and_the_legend()
    {
        var result = Read(Group("barChart", "<c:barDir val='col'/>" + Series("First", Text("A", "B"), Numbers("val", 1, 2)) +
            Series("Second", Text("A", "B"), Numbers("val", 2, 1), index: 1) + Series("Third", Text("A", "B"), Numbers("val", 2, 1), index: 2) + Axes), AxisParts,
            title: "<c:title><c:tx><c:rich><a:p><a:r><a:t>Placed</a:t></a:r></a:p></c:rich></c:tx>" + Layout("<c:xMode val='edge'/><c:yMode val='edge'/><c:x val='0.5'/><c:y val='0.1'/>") + "</c:title>",
            legendXml: "<c:legend><c:legendPos val='b'/>" + Layout("<c:xMode val='edge'/><c:yMode val='edge'/><c:x val='0.1'/><c:y val='0.9'/><c:w val='0.8'/><c:h val='0.06'/>") + "</c:legend>");
        Assert.Empty(result.Warnings);
        var svg = Render(result);
        var title = Labels(svg, "title").Single();
        Assert.Equal(240, D(title, "x"), 3); Assert.Equal(32 + 14, D(title, "y"), 3);
        // A wide, low legend lists its entries in one row from its left edge.
        var legend = Labels(svg, "legend");
        Assert.Equal(["First", "Second", "Third"], legend.Select(label => label.Value));
        Assert.Equal(48 + 14, D(legend[0], "x"), 3);
        Assert.All(legend, label => Assert.Equal(288 + 9, D(label, "y"), 3));
        Assert.True(D(legend[0], "x") < D(legend[1], "x") && D(legend[1], "x") < D(legend[2], "x"));
    }

    [Fact]
    public void Solid_fills_of_the_chart_and_plot_area_are_drawn_and_a_gradient_without_ids_becomes_its_middle_color()
    {
        var gradient = "<c:spPr><a:gradFill><a:gsLst><a:gs pos='0'><a:srgbClr val='000000'/></a:gs><a:gs pos='100000'><a:srgbClr val='FFFFFF'/></a:gs></a:gsLst><a:lin ang='5400000'/></a:gradFill></c:spPr>";
        var result = Read(Group("barChart", "<c:barDir val='col'/>" + Series("Sales", Text("A", "B"), Numbers("val", 1, 2), gradient) + Axes),
            "<c:spPr><a:solidFill><a:srgbClr val='FFFFE0'/></a:solidFill></c:spPr>" + AxisParts,
            chartSpace: "<c:spPr><a:solidFill><a:srgbClr val='f2f2f2'/></a:solidFill></c:spPr>");
        // The gradient is kept as a paint; a renderer without an id prefix draws its representative color.
        Assert.Empty(result.Warnings);
        Assert.Equal("linear", result.Chart!.Series[0].FillPaint!.Kind);
        var svg = Render(result);
        var background = svg.Descendants().Single(e => (string?)e.Attribute("data-chart-background") == "true");
        Assert.Equal("#F2F2F2", (string?)background.Attribute("fill"));
        Assert.Equal([0d, 0, 480, 320], new[] { D(background, "x"), D(background, "y"), D(background, "width"), D(background, "height") });
        Assert.Equal("#FFFFE0", (string?)Frame(svg).Attribute("fill"));
        Assert.All(Points(svg), bar => Assert.Equal("#808080", (string?)bar.Attribute("fill")));
    }

    [Fact]
    public void A_dark_background_without_label_colors_is_drawn_under_light_labels_and_is_diagnosed()
    {
        var result = Read(Group("barChart", "<c:barDir val='col'/>" + Series("Sales", Text("A", "B"), Numbers("val", 1, 2)) + Axes), AxisParts,
            chartSpace: "<c:spPr><a:solidFill><a:srgbClr val='111111'/></a:solidFill></c:spPr>");
        Assert.Contains(result.Warnings, warning => warning.Contains("dark chart area fill", StringComparison.Ordinal));
        var svg = Render(result);
        Assert.Equal("#111111", (string?)svg.Descendants().Single(e => e.Attribute("data-chart-background") is not null).Attribute("fill"));
        Assert.All(Labels(svg, "category"), label => Assert.Equal("#F2F2F2", (string?)label.Attribute("fill")));
    }

    [Theory]
    [InlineData("<a:blipFill/>")]
    public void A_picture_fill_without_a_picture_is_diagnosed_and_the_chart_keeps_its_colors(string fill)
    {
        var result = Read(Group("barChart", "<c:barDir val='col'/>" + Series("Sales", Text("A", "B"), Numbers("val", 1, 2), "<c:spPr>" + fill + "</c:spPr>") + Axes), AxisParts);
        Assert.Contains(result.Warnings, warning => warning.Contains("Picture fill", StringComparison.Ordinal));
        Assert.Null(result.Chart!.Series[0].FillPaint);
        Assert.Equal(2, Points(Render(result)).Length);
    }

    private sealed class Tolerance : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < .01;
        public int GetHashCode(double value) => 0;
    }

    private static (double X, double Y)[] Coordinates(string points) => points.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(point => point.Contains(',') ? point.Split(',') : null)
        .Where(pair => pair is not null)
        .Select(pair => (double.Parse(pair![0], CultureInfo.InvariantCulture), double.Parse(pair[1], CultureInfo.InvariantCulture))).ToArray();

    private static XDocument Render(DrawingChartReadResult result, double width = 480, double height = 320) =>
        XDocument.Parse(Assert.IsType<string>(DrawingChartSvg.Render(Assert.IsType<DocRedock.Core.Documents.DrawingChart>(result.Chart), width, height)));

    private static XElement Frame(XDocument svg) => svg.Descendants().Single(e => (string?)e.Attribute("data-chart-plot") == "true");

    private static XElement[] Points(XDocument svg) => svg.Descendants().Where(e => e.Attribute("data-point-index") is not null).ToArray();

    private static XElement[] Labels(XDocument svg, string kind) => svg.Descendants()
        .Where(e => e.Name.LocalName == "text" && (string?)e.Attribute("data-chart-label") == kind).ToArray();

    /// <summary>Tick labels of the primary value axis: the only text without a label kind.</summary>
    private static XElement[] Ticks(XDocument svg) => svg.Descendants()
        .Where(e => e.Name.LocalName == "text" && e.Attribute("data-chart-label") is null).ToArray();

    private static double D(XElement element, string name) => double.Parse(element.Attribute(name)!.Value, CultureInfo.InvariantCulture);

    private static string Layout(string settings) => "<c:layout><c:manualLayout>" + settings + "</c:manualLayout></c:layout>";

    private static string Group(string type, string content) => "<c:" + type + ">" + content + "</c:" + type + ">";

    private static string Series(string name, string categories, string values, string extra = "", int index = 0) =>
        "<c:ser><c:idx val='" + index + "'/><c:order val='" + index + "'/><c:tx><c:v>" + name + "</c:v></c:tx>" + (extra.StartsWith("<c:spPr", StringComparison.Ordinal) ? extra : "") +
        categories + values + (extra.StartsWith("<c:spPr", StringComparison.Ordinal) ? "" : extra) + "</c:ser>";

    private static string Text(params string[] values) =>
        "<c:cat><c:strRef><c:f>S!A2:A9</c:f><c:strCache><c:ptCount val='" + values.Length + "'/>" +
        string.Concat(values.Select((value, index) => "<c:pt idx='" + index + "'><c:v>" + value + "</c:v></c:pt>")) +
        "</c:strCache></c:strRef></c:cat>";

    private static string Numbers(string element, params double[] values) => Numbers(element, "General", values);

    private static string Numbers(string element, string formatCode, params double[] values) =>
        "<c:" + element + "><c:numRef><c:f>S!B2:B9</c:f><c:numCache><c:formatCode>" + System.Security.SecurityElement.Escape(formatCode) + "</c:formatCode>" +
        "<c:ptCount val='" + values.Length + "'/>" +
        string.Concat(values.Select((value, index) => "<c:pt idx='" + index + "'><c:v>" + value.ToString("R", CultureInfo.InvariantCulture) + "</c:v></c:pt>")) +
        "</c:numCache></c:numRef></c:" + element + ">";

    /// <summary>A chart part: the chart groups, then the layout, fill and axes of the plot area.</summary>
    private static DrawingChartReadResult Read(string groups, string plotArea = "", string chartSpace = "", string title = "",
        string legendXml = "", bool legend = false) =>
        DrawingChartReader.Read(Encoding.UTF8.GetBytes(
            "<c:chartSpace xmlns:c='http://schemas.openxmlformats.org/drawingml/2006/chart' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'>" +
            "<c:chart>" + title + "<c:plotArea>" + (plotArea.StartsWith("<c:layout", StringComparison.Ordinal) ? plotArea[..(plotArea.IndexOf("</c:layout>", StringComparison.Ordinal) + 11)] : "") +
            groups + (plotArea.StartsWith("<c:layout", StringComparison.Ordinal) ? plotArea[(plotArea.IndexOf("</c:layout>", StringComparison.Ordinal) + 11)..] : plotArea) +
            "</c:plotArea>" + legendXml + (legend ? "<c:legend><c:legendPos val='r'/></c:legend>" : "") + "</c:chart>" + chartSpace + "</c:chartSpace>"));
}
