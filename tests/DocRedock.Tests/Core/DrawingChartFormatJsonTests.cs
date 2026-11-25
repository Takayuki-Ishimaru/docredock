using DocRedock.Core.Documents;

namespace DocRedock.Tests.Core;

/// <summary>The number-format fields of a chart survive the scene JSON, and oversized ones are rejected.</summary>
public sealed class DrawingChartFormatJsonTests
{
    [Fact]
    public void Chart_number_formats_round_trip_through_the_scene_json()
    {
        var chart = Chart("0%", "yyyy-mm-dd", "#,##0", "0.0%");
        var restored = Assert.Single(RoundTrip(chart)).Chart!;
        Assert.Equal("0%", restored.ValueAxisFormat);
        Assert.Equal("yyyy-mm-dd", restored.XAxisFormat);
        Assert.True(restored.Date1904);
        var series = Assert.Single(restored.Series);
        Assert.Equal("#,##0", series.LabelFormat);
        Assert.Equal("0.0%", series.PercentLabelFormat);
    }

    [Fact]
    public void A_chart_without_number_formats_keeps_the_defaults()
    {
        var restored = Assert.Single(RoundTrip(new DrawingChart("bar", null, [new("S", ["A"], [1], [], null)], false, null, false))).Chart!;
        Assert.Null(restored.ValueAxisFormat);
        Assert.Null(restored.XAxisFormat);
        Assert.False(restored.Date1904);
        Assert.Null(Assert.Single(restored.Series).PercentLabelFormat);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void An_oversized_format_code_is_rejected_before_rendering(bool valueAxis, bool xAxis, bool percentLabel)
    {
        var oversized = new string('0', ExcelNumberFormat.MaxFormatCodeLength + 1);
        Assert.Empty(RoundTrip(Chart(valueAxis ? oversized : "0", xAxis ? oversized : "0", "0", percentLabel ? oversized : "0")));
    }

    [Fact]
    public void Point_labels_combination_series_layouts_and_fills_round_trip_through_the_scene_json()
    {
        var series = new DrawingChartSeries("S", ["A", "B"], [1, 2], [], null)
        {
            Plot = "line", SecondaryAxis = true,
            PointLabels = new Dictionary<int, DrawingChartPointLabel> { [1] = new() { ShowValue = false, ShowPercent = true, Format = "0.0", PercentFormat = "0%" } },
        };
        var chart = new DrawingChart("bar", null, [new("B", ["A", "B"], [3, 4], [], null) { Plot = "bar" }, series], false, "clustered", false)
        {
            SecondaryAxisMinimum = 0, SecondaryAxisMaximum = 5, SecondaryAxisFormat = "0.0", SecondaryAxisTitle = "Right",
            PlotLayout = new() { X = .1, Y = .2, Width = .5, Height = .6, Inner = true, WidthIsRight = true },
            LegendLayout = new() { X = .7, XIsOffset = true }, TitleLayout = new() { Y = .05, YIsOffset = true },
            ChartFill = "#F2F2F2", PlotFill = "#FFFFE0",
        };
        var restored = Assert.Single(RoundTrip(chart)).Chart!;
        Assert.Equal(chart.PlotLayout, restored.PlotLayout);
        Assert.Equal(chart.LegendLayout, restored.LegendLayout);
        Assert.Equal(chart.TitleLayout, restored.TitleLayout);
        Assert.Equal(("#F2F2F2", "#FFFFE0"), (restored.ChartFill, restored.PlotFill));
        Assert.Equal((0, 5, "0.0", "Right"), (restored.SecondaryAxisMinimum, restored.SecondaryAxisMaximum, restored.SecondaryAxisFormat, restored.SecondaryAxisTitle));
        Assert.Equal(("line", true), (restored.Series[1].Plot, restored.Series[1].SecondaryAxis));
        Assert.Equal(series.PointLabels[1], Assert.Single(restored.Series[1].PointLabels).Value);
        Assert.Empty(restored.Series[0].PointLabels);
    }

    [Theory]
    [InlineData("plot")]
    [InlineData("pointFormat")]
    [InlineData("layout")]
    [InlineData("secondaryFormat")]
    public void Invalid_combination_point_label_or_layout_values_are_rejected_before_rendering(string field)
    {
        var oversized = new string('0', ExcelNumberFormat.MaxFormatCodeLength + 1);
        var series = new DrawingChartSeries("S", ["A"], [1], [], null)
        {
            Plot = field == "plot" ? "radar" : null,
            PointLabels = new Dictionary<int, DrawingChartPointLabel> { [0] = new() { Format = field == "pointFormat" ? oversized : "0" } },
        };
        Assert.Empty(RoundTrip(new DrawingChart("bar", null, [series], false, null, false)
        {
            PlotLayout = new() { X = field == "layout" ? 1e9 : .5 },
            SecondaryAxisFormat = field == "secondaryFormat" ? oversized : null,
        }));
    }

    [Fact]
    public void Axis_options_legend_position_paints_and_text_colors_round_trip_through_the_scene_json()
    {
        var gradient = new DrawingPaint { Kind = "linear", AngleDegrees = 90, Stops = [new(0, "#000000"), new(1, "#FFFFFF", .5)] };
        var pattern = new DrawingPaint { Kind = "pattern", Pattern = "pct5", Color = "#FF0000", BackColor = "#FFFF00" };
        var series = new DrawingChartSeries("S", ["A", "B"], [1, 2], [1, 2], "#FF0000")
        {
            Plot = "scatter", Group = 1, Grouping = "stacked", FillPaint = pattern,
            PointFillPaints = new Dictionary<int, DrawingPaint> { [1] = gradient },
        };
        var chart = new DrawingChart("bar", null, [new("B", ["A", "B"], [3, 4], [], null) { Plot = "bar" }, series], true, "clustered", true)
        {
            ValueAxisOptions = new() { LogBase = 10, Reversed = true, TextColor = "#00FFFF" },
            CategoryAxisOptions = new() { Deleted = true, Crosses = "max", CrossesAt = 5 },
            SecondaryAxisOptions = new() { Crosses = "min" },
            LegendPosition = "b", ChartFillPaint = gradient, PlotFillPaint = pattern,
            TextColor = "#FFFF00", TitleColor = "#FFFFFF", LegendColor = "#FF00FF", ScatterOnCategoryAxis = true,
        };
        var restored = Assert.Single(RoundTrip(chart)).Chart!;
        Assert.Equal((chart.ValueAxisOptions, chart.CategoryAxisOptions, chart.SecondaryAxisOptions),
            (restored.ValueAxisOptions, restored.CategoryAxisOptions, restored.SecondaryAxisOptions));
        Assert.Equal(("b", "#FFFF00", "#FFFFFF", "#FF00FF", true),
            (restored.LegendPosition, restored.TextColor, restored.TitleColor, restored.LegendColor, restored.ScatterOnCategoryAxis));
        Assert.Equal(("scatter", 1, "stacked"), (restored.Series[1].Plot, restored.Series[1].Group, restored.Series[1].Grouping));
        foreach (var paint in new[] { restored.Series[1].FillPaint!, restored.PlotFillPaint! })
            Assert.Equal(("pattern", "pct5", "#FF0000", "#FFFF00"), (paint.Kind, paint.Pattern, paint.Color, paint.BackColor));
        foreach (var paint in new[] { restored.ChartFillPaint!, Assert.Single(restored.Series[1].PointFillPaints).Value })
        {
            Assert.Equal(("linear", 90d), (paint.Kind, paint.AngleDegrees));
            Assert.Equal(gradient.Stops, paint.Stops);
        }
    }

    [Theory]
    [InlineData("logBase")]
    [InlineData("crosses")]
    [InlineData("legend")]
    [InlineData("grouping")]
    [InlineData("pointPaint")]
    public void Invalid_axis_options_legend_positions_and_paints_are_rejected_before_rendering(string field)
    {
        var series = new DrawingChartSeries("S", ["A"], [1], [], null)
        {
            Grouping = field == "grouping" ? "sideways" : null,
            PointFillPaints = new Dictionary<int, DrawingPaint> { [0] = new() { Kind = field == "pointPaint" ? "plaid" : "solid", Color = "#000000" } },
        };
        Assert.Empty(RoundTrip(new DrawingChart("bar", null, [series], false, null, false)
        {
            ValueAxisOptions = new() { LogBase = field == "logBase" ? 1 : 10 },
            CategoryAxisOptions = new() { Crosses = field == "crosses" ? "middle" : "max" },
            LegendPosition = field == "legend" ? "center" : "t",
        }));
    }

    private static DrawingChart Chart(string valueAxis, string xAxis, string label, string percentLabel) =>
        new("pie", "T", [new("S", ["A"], [1], [], null) { LabelFormat = label, PercentLabelFormat = percentLabel }], false, null, false)
        { ValueAxisFormat = valueAxis, XAxisFormat = xAxis, Date1904 = true };

    private static IReadOnlyList<WorkbookDrawingObject> RoundTrip(DrawingChart chart)
    {
        var item = new WorkbookDrawingObject("chart", "chart", 120, 50, new()) { Chart = chart };
        var scenes = new Dictionary<string, WorkbookDrawingScene> { { "sheet-A", new("A", [item]) } };
        return WorkbookDrawingSceneJson.Read(WorkbookDrawingSceneJson.Write(scenes))["sheet-A"].Objects;
    }
}
