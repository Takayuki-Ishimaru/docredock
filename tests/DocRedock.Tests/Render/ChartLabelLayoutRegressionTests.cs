using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Drawing;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

/// <summary>Charts whose labels do not fit, whose axes carry titles or one-sided bounds, and series shapes Office
/// still draws: each used to lose the whole chart or a label.</summary>
public sealed class ChartLabelLayoutRegressionTests
{
    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    [Theory]
    [InlineData("line")]
    [InlineData("bar")]
    public void A_crowded_category_axis_thins_its_labels_instead_of_dropping_the_chart(string type)
    {
        var categories = Enumerable.Range(1, 60).Select(i => "2024-" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        var chart = new DrawingChart(type, "Trend", [Series("Sales", categories, categories.Select((_, i) => (double?)i).ToArray())], false, null, true);
        var svg = Render(chart);
        Assert.Equal(60, svg.Descendants().Count(e => e.Attribute("data-point-index") is not null));
        var labels = Labels(svg, "category");
        Assert.InRange(labels.Length, 5, 59);
        Assert.StartsWith("202", labels[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Long_category_labels_are_shortened_with_an_ellipsis_and_stay_in_the_tooltips(bool horizontal)
    {
        var categories = Enumerable.Range(1, 12).Select(i => "Category number " + i.ToString(CultureInfo.InvariantCulture) + " with a long descriptive label").ToArray();
        var chart = new DrawingChart("bar", null, [Series("Sales", categories, categories.Select((_, i) => (double?)i).ToArray())], horizontal, "clustered", true);
        var svg = Render(chart);
        var labels = Labels(svg, "category");
        Assert.Contains(labels, label => label.EndsWith('…'));
        Assert.Contains(labels, label => label.StartsWith("Cat", StringComparison.Ordinal));
        Assert.Contains(svg.Descendants().Where(e => e.Name.LocalName == "title").Select(e => e.Value),
            tooltip => tooltip.StartsWith(categories[11], StringComparison.Ordinal));
    }

    [Fact]
    public void Short_labels_are_not_broken_inside_a_word_when_the_slot_is_narrow()
    {
        var chart = new DrawingChart("bar", null,
            [Series("Revenue", Months, Months.Select((_, i) => (double?)i).ToArray()), Series("Cost", Months, Months.Select((_, i) => (double?)i).ToArray())],
            false, "clustered", true);
        var svg = Render(chart);
        Assert.Equal(Months, Labels(svg, "category"));
        Assert.Equal(["Revenue", "Cost"], Labels(svg, "legend"));
    }

    [Theory]
    [InlineData("line", 20, 3)]
    [InlineData("pie", 1, 30)]
    public void A_legend_longer_than_the_plot_keeps_the_entries_that_fit_and_counts_the_rest(string type, int seriesCount, int points)
    {
        var categories = Enumerable.Range(1, points).Select(i => "Slice " + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        var series = Enumerable.Range(1, seriesCount).Select(s => Series("Series " + s.ToString(CultureInfo.InvariantCulture), categories,
            categories.Select((_, i) => (double?)(i + s)).ToArray())).ToArray();
        var svg = Render(new DrawingChart(type, null, series, false, null, true));
        var legend = Labels(svg, "legend");
        var total = type == "pie" ? points : seriesCount;
        Assert.InRange(legend.Length, 3, total);
        Assert.Equal("… +" + (total - legend.Length + 1).ToString(CultureInfo.InvariantCulture), legend[^1]);
        Assert.Equal(legend.Length - 1, svg.Descendants().Count(e => (string?)e.Attribute("data-chart-label") == "legend-swatch"));
    }

    [Fact]
    public void Axis_ticks_and_data_labels_show_plain_numbers()
    {
        var categories = new[] { "A", "B", "C" };
        var large = Render(new DrawingChart("bar", null, [Series("s", categories, [12345, 250000, 1234567])], false, "clustered", false));
        var ticks = large.Descendants().Where(e => e.Name.LocalName == "text" && e.Attribute("data-chart-label") is null).Select(e => e.Value).ToArray();
        Assert.Equal(["0", "308600", "617300", "925900", "1235000"], ticks);
        var thirds = Render(new DrawingChart("bar", null, [Series("s", categories, [1d / 3, 2d / 3, 1]) with { ShowValueLabels = true }], false, "clustered", false));
        Assert.Equal(["0.3333333333", "0.6666666667", "1"], Labels(thirds, "data"));
        // The cached number itself is not rounded.
        Assert.Contains(thirds.Descendants(), e => (string?)e.Attribute("data-value") == (1d / 3).ToString("R", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(null, -5d)]
    [InlineData(50d, null)]
    public void One_explicit_axis_bound_beyond_the_data_still_draws_the_chart(double? minimum, double? maximum)
    {
        var chart = new DrawingChart("bar", null, [Series("s", ["A", "B", "C"], [10, 20, 30])], false, "clustered", false)
        { ValueAxisMinimum = minimum, ValueAxisMaximum = maximum };
        var plot = Render(chart).Descendants().Single(e => (string?)e.Attribute("data-chart-plot") == "true");
        var low = double.Parse((string)plot.Attribute("data-axis-min")!, CultureInfo.InvariantCulture);
        var high = double.Parse((string)plot.Attribute("data-axis-max")!, CultureInfo.InvariantCulture);
        Assert.True(low < high);
        if (minimum is { } min) Assert.Equal(min, low);
        if (maximum is { } max) Assert.Equal(max, high);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_scatter_series_without_numeric_x_values_is_plotted_at_one_two_three(bool textValues)
    {
        var x = textValues ? "<c:xVal><c:strRef><c:f>S!A2:A4</c:f><c:strCache><c:ptCount val='3'/><c:pt idx='0'><c:v>Mon</c:v></c:pt><c:pt idx='1'><c:v>Tue</c:v></c:pt><c:pt idx='2'><c:v>Wed</c:v></c:pt></c:strCache></c:strRef></c:xVal>" : "";
        var result = Read("scatterChart", SeriesXml("Points", x + Numbers("yVal", 3, 5, 4)));
        Assert.Equal(new double?[] { 1, 2, 3 }, Assert.Single(result.Chart!.Series).XValues);
        Assert.Contains(result.Warnings, warning => warning.Contains("x values", StringComparison.Ordinal));
        var points = Render(result.Chart).Descendants().Where(e => e.Name.LocalName == "circle").ToArray();
        Assert.Equal(new string?[] { "1", "2", "3" }, points.Select(p => (string?)p.Attribute("data-x-value")).ToArray());
    }

    [Fact]
    public void Scatter_axis_titles_go_to_their_own_axes()
    {
        string Axis(int id, string position, string title) => $"<c:valAx><c:axId val='{id}'/><c:scaling/><c:axPos val='{position}'/><c:title><c:tx><c:rich><a:p><a:r><a:t>{title}</a:t></a:r></a:p></c:rich></c:tx></c:title></c:valAx>";
        var result = Read("scatterChart", SeriesXml("Points", Numbers("xVal", 1, 2, 3) + Numbers("yVal", 3, 5, 4)), axes: Axis(1, "b", "Time (s)") + Axis(2, "l", "Speed"));
        Assert.Equal("Speed", result.Chart!.ValueAxisTitle);
        Assert.Equal("Time (s)", result.Chart.CategoryAxisTitle);
        var svg = Render(result.Chart);
        var horizontal = svg.Descendants().Single(e => (string?)e.Attribute("data-chart-label") == "category-axis-title");
        Assert.Equal("Time (s)", horizontal.Value);
        Assert.Same(svg.Root, horizontal.Parent);
        var vertical = svg.Descendants().Single(e => (string?)e.Attribute("data-chart-label") == "axis-title");
        Assert.Equal("Speed", vertical.Value);
        Assert.Contains("rotate(-90)", (string?)vertical.Parent!.Attribute("transform"));
    }

    [Theory]
    [InlineData("col")]
    [InlineData("bar")]
    public void The_category_axis_title_is_drawn(string direction)
    {
        var result = Read("barChart", SeriesXml("Sales", Categories("A", "B", "C") + Numbers("val", 1, 2, 3)), $"<c:barDir val='{direction}'/>",
            "<c:catAx><c:axId val='1'/><c:title><c:tx><c:rich><a:p><a:r><a:t>Month</a:t></a:r></a:p></c:rich></c:tx></c:title></c:catAx><c:valAx><c:axId val='2'/></c:valAx>");
        Assert.Equal("Month", result.Chart!.CategoryAxisTitle);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("axis title", StringComparison.OrdinalIgnoreCase));
        var title = Render(result.Chart).Descendants().Single(e => (string?)e.Attribute("data-chart-label") == "category-axis-title");
        Assert.Equal("Month", title.Value);
        Assert.Equal(direction == "bar", ((string?)title.Parent!.Attribute("transform"))?.Contains("rotate(-90)", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Multi_level_categories_label_each_point_with_its_innermost_level()
    {
        var categories = "<c:cat><c:multiLvlStrRef><c:f>S!A2:B3</c:f><c:multiLvlStrCache><c:ptCount val='2'/>" +
            "<c:lvl><c:pt idx='0'><c:v>Q1</c:v></c:pt><c:pt idx='1'><c:v>Q2</c:v></c:pt></c:lvl><c:lvl><c:pt idx='0'><c:v>2024</c:v></c:pt></c:lvl>" +
            "</c:multiLvlStrCache></c:multiLvlStrRef></c:cat>";
        var result = Read("barChart", SeriesXml("Sales", categories + Numbers("val", 1, 2)), "<c:barDir val='col'/>");
        Assert.Equal(new[] { "Q1", "Q2" }, Assert.Single(result.Chart!.Series).Categories);
        Assert.Equal(["Q1", "Q2"], Labels(Render(result.Chart), "category"));
    }

    [Fact]
    public void A_pie_with_several_series_draws_its_first_series_and_says_so()
    {
        var result = Read("pieChart", SeriesXml("First", Categories("A", "B", "C") + Numbers("val", 3, 1, 2)) +
            SeriesXml("Second", Categories("A", "B", "C") + Numbers("val", 1, 1, 1)));
        Assert.Contains(result.Warnings, warning => warning.Contains("only the first", StringComparison.Ordinal));
        var slices = Render(result.Chart!).Descendants().Where(e => e.Name.LocalName == "path").ToArray();
        Assert.Equal(new string?[] { "3", "1", "2" }, slices.Select(slice => (string?)slice.Attribute("data-value")).ToArray());
    }

    [Fact]
    public void Vary_colors_gives_each_point_of_a_single_series_its_color_and_its_absence_keeps_a_pie_in_one()
    {
        var bars = Read("barChart", SeriesXml("Sales", Categories("A", "B", "C") + Numbers("val", 1, 2, 3)), "<c:barDir val='col'/><c:varyColors val='1'/>");
        Assert.True(bars.Chart!.VaryColors);
        Assert.Equal(3, Fills(Render(bars.Chart)).Distinct().Count());
        var plain = Read("barChart", SeriesXml("Sales", Categories("A", "B", "C") + Numbers("val", 1, 2, 3)), "<c:barDir val='col'/><c:varyColors val='0'/>");
        Assert.Single(Fills(Render(plain.Chart!)).Distinct());
        var pie = Read("pieChart", SeriesXml("Sales", Categories("A", "B", "C") + Numbers("val", 1, 2, 3)), "<c:varyColors val='0'/>");
        Assert.Single(Fills(Render(pie.Chart!)).Distinct());
        var varied = Read("pieChart", SeriesXml("Sales", Categories("A", "B", "C") + Numbers("val", 1, 2, 3)));
        Assert.Equal(3, Fills(Render(varied.Chart!)).Distinct().Count());
    }

    private static string[] Fills(XDocument svg) => svg.Descendants().Where(e => e.Attribute("data-point-index") is not null)
        .Select(e => (string)e.Attribute("fill")!).ToArray();

    private static string[] Labels(XDocument svg, string kind) => svg.Descendants()
        .Where(e => e.Name.LocalName == "text" && (string?)e.Attribute("data-chart-label") == kind).Select(e => e.Value).ToArray();

    private static XDocument Render(DrawingChart chart) => XDocument.Parse(Assert.IsType<string>(DrawingChartSvg.Render(chart, 480, 320)));

    private static DrawingChartSeries Series(string name, string[] categories, double?[] values) => new(name, categories, values, [], null);

    private static DrawingChartReadResult Read(string type, string series, string typeSettings = "", string axes = "") =>
        DrawingChartReader.Read(Encoding.UTF8.GetBytes("<c:chartSpace xmlns:c='http://schemas.openxmlformats.org/drawingml/2006/chart' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><c:chart><c:plotArea><c:" +
            type + ">" + typeSettings + series + "<c:axId val='1'/><c:axId val='2'/></c:" + type + ">" + axes + "</c:plotArea></c:chart></c:chartSpace>"));

    private static string SeriesXml(string name, string content) =>
        $"<c:ser><c:idx val='0'/><c:order val='0'/><c:tx><c:strRef><c:f>S!A1</c:f><c:strCache><c:ptCount val='1'/><c:pt idx='0'><c:v>{name}</c:v></c:pt></c:strCache></c:strRef></c:tx>{content}</c:ser>";

    private static string Categories(params string[] labels) => "<c:cat><c:strRef><c:f>S!A2:A9</c:f><c:strCache><c:ptCount val='" + labels.Length + "'/>" +
        string.Concat(labels.Select((label, i) => $"<c:pt idx='{i}'><c:v>{label}</c:v></c:pt>")) + "</c:strCache></c:strRef></c:cat>";

    private static string Numbers(string element, params double[] values) => "<c:" + element + "><c:numRef><c:f>S!B2:B9</c:f><c:numCache><c:formatCode>General</c:formatCode><c:ptCount val='" + values.Length + "'/>" +
        string.Concat(values.Select((value, i) => $"<c:pt idx='{i}'><c:v>{value.ToString(CultureInfo.InvariantCulture)}</c:v></c:pt>")) + "</c:numCache></c:numRef></c:" + element + ">";
}
