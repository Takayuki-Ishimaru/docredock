using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Drawing;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

/// <summary>
/// Chart types and formatting that used to leave the chart to its fallback: radar, bubble, stock and surface
/// charts, two pie groups in one plot area, picture fills and outer shadows.
/// </summary>
public sealed class ChartSpecialTypeTests
{
    private const string Axes = "<c:axId val='1'/><c:axId val='2'/>";
    private const string AxisParts = "<c:catAx><c:axId val='1'/></c:catAx><c:valAx><c:axId val='2'/><c:axPos val='l'/></c:valAx>";
    private const string XyAxisParts = "<c:valAx><c:axId val='1'/><c:axPos val='b'/></c:valAx><c:valAx><c:axId val='2'/><c:axPos val='l'/></c:valAx>";
    private const string Pixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    // 1. Radar ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_radar_chart_draws_rings_spokes_axis_labels_and_one_closed_line_with_markers_for_each_series()
    {
        var result = Read(Group("radarChart", "<c:radarStyle val='marker'/>" +
            Series("East", Text("Speed", "Range", "Cost", "Size"), Numbers("val", 8, 4, 2, 6)) +
            Series("West", Text("Speed", "Range", "Cost", "Size"), Numbers("val", 4, 4, 4, 4), index: 1) + Axes), AxisParts, legend: true);
        Assert.Empty(result.Warnings);
        Assert.Equal(("radar", "marker"), (result.Chart!.Type, result.Chart.RadarStyle));
        var svg = Render(result);
        Assert.Equal("radar", (string?)svg.Root!.Attribute("data-chart-type"));
        var frame = Frame(svg);
        Assert.Equal(0, D(frame, "data-axis-min")); Assert.Equal(8, D(frame, "data-axis-max"));
        var cx = D(frame, "x") + D(frame, "width") / 2; var cy = D(frame, "y") + D(frame, "height") / 2;

        // Four rings (the ticks 2, 4, 6 and 8) with a corner on each of the four spokes.
        var rings = svg.Descendants().Where(e => e.Attribute("data-chart-ring") is not null).ToArray();
        Assert.Equal(4, rings.Length);
        Assert.All(rings, ring => Assert.Equal(4, Coordinates((string)ring.Attribute("points")!).Length));
        var spokes = svg.Descendants().Where(e => e.Attribute("data-chart-spoke") is not null).ToArray();
        Assert.Equal(4, spokes.Length);
        var radius = cy - D(spokes[0], "y2");
        Assert.True(radius > 20);
        Assert.Equal(cx, D(spokes[0], "x2"), 2);
        // The second spoke points to the right: the categories run clockwise.
        Assert.Equal(cx + radius, D(spokes[1], "x2"), 2); Assert.Equal(cy, D(spokes[1], "y2"), 2);

        // The labels of the value axis stand on the first spoke, the lowest at the centre.
        var ticks = Ticks(svg);
        Assert.Equal(["0", "2", "4", "6", "8"], ticks.Select(tick => tick.Value));
        Assert.Equal(new[] { cy + 3, cy - radius / 4 + 3, cy - radius / 2 + 3, cy - radius * 3 / 4 + 3, cy - radius + 3 }, ticks.Select(tick => D(tick, "y")), new Tolerance());
        Assert.Equal(["Speed", "Range", "Cost", "Size"], Labels(svg, "category").Select(label => label.Value));

        var polygons = svg.Descendants().Where(e => e.Name.LocalName == "polygon" && e.Attribute("data-series-index") is not null).ToArray();
        Assert.Equal(2, polygons.Length);
        Assert.All(polygons, polygon => Assert.Equal("none", (string?)polygon.Attribute("fill")));
        var east = Coordinates((string)polygons[0].Attribute("points")!);
        Assert.Equal(new[] { (cx, cy - radius), (cx + radius / 2, cy), (cx, cy + radius / 4), (cx - radius * 3 / 4, cy) }, east, new PointTolerance());
        var markers = Points(svg);
        Assert.Equal(8, markers.Length);
        Assert.All(markers, marker => Assert.Equal("circle", marker.Name.LocalName));
        Assert.Equal(["East", "West"], Labels(svg, "legend").Select(label => label.Value));
    }

    [Theory]
    [InlineData("filled", true, 0)]
    [InlineData("standard", false, 0)]
    [InlineData("marker", false, 3)]
    public void The_radar_style_decides_the_fill_and_the_markers(string style, bool filled, int markers)
    {
        var svg = Render(Read(Group("radarChart", "<c:radarStyle val='" + style + "'/>" +
            Series("East", Text("A", "B", "C"), Numbers("val", 3, 2, 1), "<c:spPr><a:solidFill><a:srgbClr val='336699'/></a:solidFill></c:spPr>") + Axes), AxisParts));
        var polygon = svg.Descendants().Single(e => e.Name.LocalName == "polygon" && e.Attribute("data-series-index") is not null);
        Assert.Equal(filled ? "#336699" : "none", (string?)polygon.Attribute("fill"));
        Assert.Equal(filled ? "0.35" : null, (string?)polygon.Attribute("fill-opacity"));
        Assert.Equal("#336699", (string?)polygon.Attribute("stroke"));
        Assert.Equal(markers, Points(svg).Length);
    }

    [Fact]
    public void A_blank_breaks_the_line_of_a_radar_series_and_a_series_without_markers_has_none()
    {
        var values = "<c:val><c:numRef><c:numCache><c:ptCount val='5'/><c:pt idx='0'><c:v>1</c:v></c:pt><c:pt idx='1'><c:v>2</c:v></c:pt>" +
            "<c:pt idx='3'><c:v>4</c:v></c:pt><c:pt idx='4'><c:v>5</c:v></c:pt></c:numCache></c:numRef></c:val>";
        var result = Read(Group("radarChart", "<c:radarStyle val='marker'/>" +
            Series("Open", Text("A", "B", "C", "D", "E"), values, "<c:marker><c:symbol val='none'/></c:marker>") + Axes), AxisParts);
        Assert.True(result.Chart!.Series[0].HideMarkers);
        var svg = Render(result);
        Assert.DoesNotContain(svg.Descendants(), e => e.Name.LocalName == "polygon" && e.Attribute("data-series-index") is not null);
        // D, E, A and B are one run around the circle; C is the gap.
        var line = svg.Descendants().Single(e => e.Name.LocalName == "polyline");
        Assert.Equal(4, Coordinates((string)line.Attribute("points")!).Length);
        Assert.Empty(Points(svg));
    }

    [Fact]
    public void A_radar_group_beside_other_groups_stays_a_diagnosed_fallback()
    {
        var result = Read(Group("barChart", "<c:barDir val='col'/>" + Series("Units", Text("A", "B"), Numbers("val", 5, 10)) + Axes) +
            Group("radarChart", Series("Other", Text("A", "B"), Numbers("val", 1, 2), index: 1) + Axes), AxisParts);
        Assert.Null(result.Chart);
        Assert.Contains("A radar chart group combined with other chart groups is unsupported. The chart is not drawn.", result.Warnings);
    }

    // 2. Bubble -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", 2d, 1d)]
    [InlineData("<c:sizeRepresents val='area'/>", 2d, 1d)]
    [InlineData("<c:sizeRepresents val='w'/>", 4d, 1d)]
    [InlineData("<c:bubbleScale val='200'/>", 2d, 2d)]
    public void The_area_or_the_width_of_a_bubble_is_its_share_of_the_largest_bubble(string settings, double ratio, double scale)
    {
        var result = Read(Group("bubbleChart", Bubbles("Sales", [1, 2, 3], [10, 20, 30], [1, 4, 2]) + settings + Axes), XyAxisParts);
        Assert.Empty(result.Warnings);
        Assert.Equal("bubble", result.Chart!.Type);
        Assert.Equal(new double?[] { 1, 4, 2 }, result.Chart.Series[0].BubbleSizes);
        var svg = Render(result);
        Assert.Equal("bubble", (string?)svg.Root!.Attribute("data-chart-type"));
        var frame = Frame(svg);
        var bubbles = Points(svg);
        Assert.Equal(["1", "4", "2"], bubbles.Select(bubble => (string)bubble.Attribute("data-bubble-size")!));
        Assert.Equal(["1", "2", "3"], bubbles.Select(bubble => (string)bubble.Attribute("data-x-value")!));
        Assert.Equal(["10", "20", "30"], bubbles.Select(bubble => (string)bubble.Attribute("data-value")!));
        Assert.Equal(ratio, D(bubbles[1], "r") / D(bubbles[0], "r"), 2);
        // The largest bubble is a quarter of the shorter side of the plot wide at a scale of 100 %.
        Assert.Equal(Math.Min(D(frame, "width"), D(frame, "height")) * .125 * scale, D(bubbles[1], "r"), 2);
        // The centres lie inside the plot, in the order of their x and y values.
        Assert.True(D(bubbles[0], "cx") < D(bubbles[1], "cx") && D(bubbles[1], "cx") < D(bubbles[2], "cx"));
        Assert.True(D(bubbles[0], "cy") > D(bubbles[1], "cy") && D(bubbles[1], "cy") > D(bubbles[2], "cy"));
        Assert.All(bubbles, bubble => Assert.InRange(D(bubble, "cx"), D(frame, "x"), D(frame, "x") + D(frame, "width")));
    }

    [Fact]
    public void A_negative_bubble_is_drawn_only_when_the_chart_shows_negative_bubbles()
    {
        var hidden = Read(Group("bubbleChart", Bubbles("Sales", [1, 2], [10, 20], [4, -1]) + Axes), XyAxisParts);
        Assert.Equal(["Bubbles with a negative size are not shown, as in Excel."], hidden.Warnings);
        Assert.Equal("4", (string?)Assert.Single(Points(Render(hidden))).Attribute("data-bubble-size"));

        var shown = Read(Group("bubbleChart", Bubbles("Sales", [1, 2], [10, 20], [4, -1], "<c:spPr><a:solidFill><a:srgbClr val='336699'/></a:solidFill></c:spPr>") +
            "<c:showNegBubbles val='1'/>" + Axes), XyAxisParts);
        Assert.Empty(shown.Warnings);
        Assert.True(shown.Chart!.ShowNegativeBubbles);
        var bubbles = Points(Render(shown));
        Assert.Equal(2, bubbles.Length);
        Assert.Equal(2, D(bubbles[0], "r") / D(bubbles[1], "r"), 2);
        Assert.Equal(("#336699", null), ((string?)bubbles[0].Attribute("fill"), (string?)bubbles[0].Attribute("data-bubble-negative")));
        Assert.Equal(("#ffffff", "#336699", "true"), ((string?)bubbles[1].Attribute("fill"), (string?)bubbles[1].Attribute("stroke"), (string?)bubbles[1].Attribute("data-bubble-negative")));
    }

    // 3. Stock ------------------------------------------------------------------------------------------------

    [Fact]
    public void An_open_high_low_close_chart_draws_high_low_lines_and_up_down_bars_in_their_fills()
    {
        var days = Text("Mon", "Tue", "Wed");
        var result = Read(Group("stockChart",
            Series("Open", days, Numbers("val", 10, 14, 12)) + Series("High", days, Numbers("val", 16, 15, 12), index: 1) +
            Series("Low", days, Numbers("val", 8, 9, 4), index: 2) + Series("Close", days, Numbers("val", 14, 10, 12), index: 3) +
            "<c:hiLowLines/><c:upDownBars><c:gapWidth val='100'/><c:upBars><c:spPr><a:solidFill><a:srgbClr val='00B050'/></a:solidFill></c:spPr></c:upBars>" +
            "<c:downBars><c:spPr><a:solidFill><a:srgbClr val='FF0000'/></a:solidFill></c:spPr></c:downBars></c:upDownBars>" + Axes), AxisParts);
        Assert.Equal(["The stock chart is drawn with its high-low lines, up/down bars and close marks; the lines and markers of its series are not drawn."], result.Warnings);
        Assert.Equal("stock", result.Chart!.Type);
        Assert.True(result.Chart.HighLowLines);
        Assert.Equal(new DrawingChartUpDownBars("#00B050", "#FF0000", 100), result.Chart.UpDownBars);
        var svg = Render(result);
        var frame = Frame(svg);
        Assert.Equal(0, D(frame, "data-axis-min")); Assert.Equal(16, D(frame, "data-axis-max"));
        double Y(double value) => D(frame, "y") + D(frame, "height") * (1 - value / 16);

        var lines = Stock(svg, "high-low");
        Assert.Equal(3, lines.Length);
        Assert.Equal(new[] { Y(16), Y(15), Y(12) }, lines.Select(line => D(line, "y1")), new Tolerance());
        Assert.Equal(new[] { Y(8), Y(9), Y(4) }, lines.Select(line => D(line, "y2")), new Tolerance());
        Assert.All(lines, line => Assert.Equal(D(line, "x1"), D(line, "x2")));
        // Three categories: the lines stand in the middle of their thirds.
        Assert.Equal(new[] { 1 / 6d, .5, 5 / 6d }, lines.Select(line => (D(line, "x1") - D(frame, "x")) / D(frame, "width")), new Tolerance());

        // Close above open is an up bar, below a down bar; equal prices are an up bar without height.
        var bars = svg.Descendants().Where(e => ((string?)e.Attribute("data-chart-stock"))?.EndsWith("-bar", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(["up-bar", "down-bar", "up-bar"], bars.Select(bar => (string)bar.Attribute("data-chart-stock")!));
        Assert.Equal(["#00B050", "#FF0000", "#00B050"], bars.Select(bar => (string)bar.Attribute("fill")!));
        Assert.Equal(new[] { Y(14), Y(14), Y(12) }, bars.Select(bar => D(bar, "y")), new Tolerance());
        Assert.Equal(new[] { Y(10) - Y(14), Y(10) - Y(14), 0 }, bars.Select(bar => D(bar, "height")), new Tolerance());
        // A gap of 100 % leaves half of the band to the bar, centred on the line.
        Assert.All(bars, bar => Assert.Equal(D(frame, "width") / 3 / 2, D(bar, "width"), 2));
        Assert.Equal(lines.Select(line => D(line, "x1")), bars.Select(bar => D(bar, "x") + D(bar, "width") / 2), new Tolerance());
        Assert.Empty(Stock(svg, "close"));
        Assert.Equal(["Mon", "Tue", "Wed"], Labels(svg, "category").Select(label => label.Value));
    }

    [Fact]
    public void A_high_low_close_chart_marks_the_close_right_of_the_line_and_default_bars_are_white_and_black()
    {
        var days = Text("Mon", "Tue");
        string Group3(string extra) => Group("stockChart", Series("High", days, Numbers("val", 16, 15)) +
            Series("Low", days, Numbers("val", 8, 9), index: 1) + Series("Close", days, Numbers("val", 14, 10), index: 2) + extra + Axes);
        var svg = Render(Read(Group3("<c:hiLowLines/>"), AxisParts));
        var lines = Stock(svg, "high-low");
        Assert.Equal(2, lines.Length);
        var closes = Stock(svg, "close");
        Assert.Equal(["14", "10"], closes.Select(close => (string)close.Attribute("data-value")!));
        Assert.All(closes, close => Assert.Equal("2", (string?)close.Attribute("data-series-index")));
        Assert.Equal(lines.Select(line => D(line, "x1")), closes.Select(close => D(close, "x1")), new Tolerance());
        Assert.All(closes, close => Assert.True(D(close, "x2") > D(close, "x1") && D(close, "y1") == D(close, "y2")));
        Assert.Empty(Stock(svg, "open"));

        var bars = Render(Read(Group3("<c:upDownBars/>"), AxisParts)).Descendants().Where(e => e.Name.LocalName == "rect" && e.Attribute("data-chart-stock") is not null).ToArray();
        // High 16 to close 14 is a down bar, high 15 to close 10 as well: the first series opens, the last one closes.
        Assert.Equal(["#000000", "#000000"], bars.Select(bar => (string)bar.Attribute("fill")!));
    }

    [Fact]
    public void Prices_close_together_do_not_start_the_axis_at_zero()
    {
        var days = Text("Mon", "Tue");
        var svg = Render(Read(Group("stockChart", Series("High", days, Numbers("val", 104, 106)) +
            Series("Low", days, Numbers("val", 96, 100), index: 1) + Series("Close", days, Numbers("val", 100, 105), index: 2) + "<c:hiLowLines/>" + Axes), AxisParts));
        var frame = Frame(svg);
        Assert.Equal(95, D(frame, "data-axis-min"), 6); Assert.Equal(107, D(frame, "data-axis-max"), 6);
    }

    [Fact]
    public void A_volume_chart_draws_the_bars_on_the_primary_axis_and_the_prices_on_the_secondary_axis()
    {
        var days = Text("Mon", "Tue", "Wed");
        var axes = "<c:catAx><c:axId val='1'/></c:catAx><c:valAx><c:axId val='2'/><c:axPos val='l'/></c:valAx>" +
            "<c:catAx><c:axId val='3'/><c:delete val='1'/></c:catAx><c:valAx><c:axId val='4'/><c:axPos val='r'/><c:crosses val='max'/></c:valAx>";
        var result = Read(Group("barChart", "<c:barDir val='col'/>" + Series("Volume", days, Numbers("val", 1000, 4000, 2000)) + Axes) +
            Group("stockChart", Series("High", days, Numbers("val", 40, 30, 20), index: 1) + Series("Low", days, Numbers("val", 10, 20, 0), index: 2) +
                Series("Close", days, Numbers("val", 20, 25, 10), index: 3) + "<c:hiLowLines/><c:axId val='3'/><c:axId val='4'/>"), axes);
        Assert.NotNull(result.Chart);
        Assert.Equal("bar", result.Chart.Type);
        Assert.Equal(["bar", "stock", "stock", "stock"], result.Chart.Series.Select(series => series.Plot));
        Assert.Equal([false, true, true, true], result.Chart.Series.Select(series => series.SecondaryAxis));
        var svg = Render(result);
        var frame = Frame(svg);
        Assert.Equal(4000, D(frame, "data-axis-max"));
        var secondary = svg.Descendants().Single(e => e.Attribute("data-chart-secondary-axis") is not null);
        Assert.Equal(0, D(secondary, "data-axis-min")); Assert.Equal(40, D(secondary, "data-axis-max"));
        var bars = Points(svg).Where(point => point.Name.LocalName == "rect").ToArray();
        Assert.Equal(["1000", "4000", "2000"], bars.Select(bar => (string)bar.Attribute("data-value")!));
        var lines = Stock(svg, "high-low");
        Assert.Equal(3, lines.Length);
        // 40 on the secondary axis is as high as 4000 on the primary one, above the middle of its bar.
        Assert.Equal(D(frame, "y"), D(lines[0], "y1"), 2);
        Assert.Equal(D(frame, "y") + D(frame, "height"), D(lines[2], "y2"), 2);
        Assert.Equal(bars.Select(bar => D(bar, "x") + D(bar, "width") / 2), lines.Select(line => D(line, "x1")), new Tolerance());
        Assert.Equal(3, Stock(svg, "close").Length);
        // The marks of the prices lie over the bars.
        var order = svg.Descendants().Where(e => e.Attribute("data-chart-stock") is not null || e.Attribute("data-point-index") is not null && e.Name.LocalName == "rect").ToArray();
        Assert.Equal("rect", order[0].Name.LocalName); Assert.Equal("line", order[^1].Name.LocalName);
    }

    // 4. Surface ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_surface_chart_is_a_grid_of_cells_in_the_colors_of_their_value_bands_with_a_legend_of_the_bands()
    {
        var result = Read(SurfaceGroup("surfaceChart", "<c:bandFmts><c:bandFmt><c:idx val='0'/><c:spPr><a:solidFill><a:srgbClr val='112233'/></a:solidFill></c:spPr></c:bandFmt></c:bandFmts>"),
            AxisParts + "<c:serAx><c:axId val='9'/></c:serAx>", legend: true);
        Assert.Equal(["The surface chart is drawn from above as a grid of cells colored by value band; the bands follow the ticks of the drawn value scale, not the major unit of the source axis."], result.Warnings);
        Assert.Equal(("surface", false), (result.Chart!.Type, result.Chart.Wireframe));
        Assert.Equal("#112233", Assert.Single(result.Chart.BandColors).Value);
        var svg = Render(result);
        Assert.Equal("surface", (string?)svg.Root!.Attribute("data-chart-type"));
        var frame = Frame(svg);
        Assert.Equal(0, D(frame, "data-axis-min")); Assert.Equal(8, D(frame, "data-axis-max"));

        var cells = Points(svg);
        Assert.Equal(6, cells.Length);
        Assert.Equal(["1", "3", "5", "3", "7", "8"], cells.Select(cell => (string)cell.Attribute("data-value")!));
        // The bands are 0-2, 2-4, 4-6 and 6-8; the highest value belongs to the last band.
        Assert.Equal(["0", "1", "2", "1", "3", "3"], cells.Select(cell => (string)cell.Attribute("data-band")!));
        Assert.Equal("#112233", (string?)cells[0].Attribute("fill"));
        Assert.Equal((string?)cells[1].Attribute("fill"), (string?)cells[3].Attribute("fill"));
        Assert.Equal(4, cells.Select(cell => (string?)cell.Attribute("fill")).Distinct().Count());
        // Three columns and two rows fill the plot; the first series is the bottom row.
        Assert.All(cells, cell => Assert.Equal((D(frame, "width") / 3, D(frame, "height") / 2), (D(cell, "width"), D(cell, "height")), new PointTolerance()));
        Assert.Equal(D(frame, "y") + D(frame, "height") / 2, D(cells[0], "y"), 2);
        Assert.Equal(D(frame, "y"), D(cells[3], "y"), 2);
        Assert.Equal(new[] { 0, 1 / 3d, 2 / 3d }, cells.Take(3).Select(cell => (D(cell, "x") - D(frame, "x")) / D(frame, "width")), new Tolerance());

        Assert.Equal(["0–2", "2–4", "4–6", "6–8"], Labels(svg, "legend").Select(label => label.Value));
        var swatches = svg.Descendants().Where(e => (string?)e.Attribute("data-chart-label") == "legend-swatch").Select(e => (string)e.Attribute("fill")!).ToArray();
        Assert.Equal(["#112233", (string)cells[1].Attribute("fill")!, (string)cells[2].Attribute("fill")!, (string)cells[4].Attribute("fill")!], swatches);
        Assert.Equal(["Front", "Back"], Labels(svg, "series-axis").Select(label => label.Value));
        Assert.Equal(["A", "B", "C"], Labels(svg, "category").Select(label => label.Value));
    }

    [Fact]
    public void A_three_dimensional_surface_chart_is_the_same_top_view_with_a_warning()
    {
        var flat = Read(SurfaceGroup("surfaceChart", ""), AxisParts + "<c:serAx><c:axId val='9'/></c:serAx>");
        var deep = Read(SurfaceGroup("surface3DChart", ""), AxisParts + "<c:serAx><c:axId val='9'/></c:serAx>", title: "<c:view3D><c:rotX val='15'/></c:view3D>");
        Assert.Equal("The 3-D surface chart is drawn as a flat top view (a contour chart); depth, perspective and rotation are not reproduced.", deep.Warnings[0]);
        Assert.Equal(DrawingChartSvg.Render(flat.Chart!, 480, 320), DrawingChartSvg.Render(deep.Chart!, 480, 320));
    }

    [Fact]
    public void A_wireframe_surface_draws_only_the_boundaries_between_the_bands()
    {
        var result = Read(SurfaceGroup("surfaceChart", "<c:wireframe val='1'/>"), AxisParts + "<c:serAx><c:axId val='9'/></c:serAx>");
        Assert.True(result.Chart!.Wireframe);
        var svg = Render(result);
        Assert.Empty(Points(svg));
        Assert.DoesNotContain(svg.Descendants(), e => e.Attribute("data-band") is not null);
        // Front 0|1|2 and back 1|3|3: three vertical boundaries, and one horizontal boundary in each column.
        var boundaries = svg.Descendants().Where(e => e.Attribute("data-chart-band-boundary") is not null).ToArray();
        Assert.Equal(6, boundaries.Length);
        Assert.Equal(3, boundaries.Count(line => D(line, "x1") == D(line, "x2")));
        Assert.Equal(3, boundaries.Count(line => D(line, "y1") == D(line, "y2")));
        Assert.All(boundaries, line => Assert.Equal("line", line.Name.LocalName));
    }

    // 5. Two pie groups ---------------------------------------------------------------------------------------

    [Fact]
    public void Two_pie_groups_are_drawn_side_by_side()
    {
        var result = Read(Group("pieChart", Series("One", Text("A", "B"), Numbers("val", 5, 10))) +
            Group("pieChart", Series("Two", Text("A", "B"), Numbers("val", 1, 3), index: 1)), legend: true);
        Assert.Equal(["The pie or doughnut groups are drawn side by side; Excel draws them in the same circle, where one hides the other."], result.Warnings);
        Assert.Equal("pie", result.Chart!.Type);
        Assert.Equal([0, 1], result.Chart.Series.Select(series => series.Group));
        var svg = Render(result);
        var frame = Frame(svg);
        var groups = svg.Descendants().Where(e => e.Attribute("data-chart-pie-group") is not null).ToArray();
        Assert.Equal(2, groups.Length);
        Assert.All(groups, group => Assert.Equal(2, group.Elements().Count(e => e.Name.LocalName == "path")));
        Assert.Equal(["5", "10", "1", "3"], Points(svg).Select(slice => (string)slice.Attribute("data-value")!));
        // Each pie stands in the middle of its half of the plot area.
        var centres = groups.Select(group => Centre(group.Elements().First(e => e.Name.LocalName == "path"))).ToArray();
        Assert.Equal(D(frame, "x") + D(frame, "width") / 4, centres[0].X, 2);
        Assert.Equal(D(frame, "x") + D(frame, "width") * 3 / 4, centres[1].X, 2);
        Assert.Equal(centres[0].Y, centres[1].Y, 2);
        // The legend names the points once.
        Assert.Equal(["A", "B"], Labels(svg, "legend").Select(label => label.Value));
    }

    [Fact]
    public void A_pie_and_a_doughnut_group_share_the_centre_with_the_pie_under_the_rings()
    {
        var result = Read(Group("doughnutChart", Series("Ring", Text("A", "B"), Numbers("val", 1, 3)) + "<c:holeSize val='50'/>") +
            Group("pieChart", Series("Core", Text("A", "B"), Numbers("val", 5, 5), index: 1)));
        Assert.Equal(["The pie and doughnut groups are drawn around one centre, the pie under the rings of the doughnut, as Excel layers them."], result.Warnings);
        Assert.Equal(50, result.Chart!.DoughnutHolePercent);
        var svg = Render(result);
        var frame = Frame(svg);
        var slices = Points(svg);
        // The pie is drawn first, although its group follows the doughnut in the file.
        Assert.Equal(["1", "1", "0", "0"], slices.Select(slice => (string)slice.Attribute("data-series-index")!));
        var pie = Centre(slices[0]);
        Assert.Equal((D(frame, "x") + D(frame, "width") / 2, D(frame, "y") + D(frame, "height") / 2), pie, new PointTolerance());
        // The ring keeps its hole: half of the radius of the pie.
        var radii = Regex.Matches((string)slices[2].Attribute("d")!, @"A ([\d.]+) ").Select(match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().ToArray();
        var pieRadius = double.Parse(Regex.Match((string)slices[0].Attribute("d")!, @"A ([\d.]+) ").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.Equal(new[] { pieRadius, pieRadius / 2 }, radii, new Tolerance());
    }

    [Fact]
    public void A_pie_of_pie_chart_is_one_pie_with_a_warning()
    {
        var result = Read(Group("ofPieChart", "<c:ofPieType val='pie'/>" + Series("Share", Text("A", "B", "C"), Numbers("val", 5, 3, 2)) + "<c:splitPos val='1'/><c:secondPieSize val='75'/>"));
        Assert.Equal(["The second plot of the pie-of-pie or bar-of-pie chart is not drawn; all points are slices of one pie."], result.Warnings);
        Assert.Equal("pie", result.Chart!.Type);
        Assert.Equal(["5", "3", "2"], Points(Render(result)).Select(slice => (string)slice.Attribute("data-value")!));
    }

    // 6. Picture fills ----------------------------------------------------------------------------------------

    [Fact]
    public void A_picture_fill_of_a_series_a_point_and_the_backgrounds_is_a_pattern_of_the_picture()
    {
        const string picture = "<a:blipFill><a:blip r:embed='rId7'><a:alphaModFix amt='50000'/></a:blip><a:stretch><a:fillRect/></a:stretch></a:blipFill>";
        var asked = new List<string>();
        string? Resolve(string id, out string? reason)
        {
            asked.Add(id);
            reason = null;
            return Pixel;
        }
        var result = Read(Group("barChart", "<c:barDir val='col'/>" + Series("Sales", Text("A", "B"), Numbers("val", 1, 2),
                "<c:spPr>" + picture + "</c:spPr><c:dPt><c:idx val='1'/><c:spPr>" + picture + "</c:spPr></c:dPt>") + Axes),
            AxisParts + "<c:spPr>" + picture + "</c:spPr>", chartSpace: "<c:spPr>" + picture + "</c:spPr>", images: Resolve);
        Assert.Empty(result.Warnings);
        Assert.All(asked, id => Assert.Equal("rId7", id));
        var chart = result.Chart!;
        Assert.All(new[] { chart.Series[0].FillPaint, chart.Series[0].PointFillPaints[1], chart.PlotFillPaint, chart.ChartFillPaint },
            paint => Assert.Equal(("picture", Pixel, .5), (paint!.Kind, paint.ImageReference, paint.Opacity)));

        var svg = XDocument.Parse(Assert.IsType<string>(DrawingChartSvg.Render(chart, 480, 320, "c1")));
        var patterns = svg.Descendants().Where(e => e.Name.LocalName == "pattern").ToArray();
        Assert.Equal(4, patterns.Length);
        Assert.All(patterns, pattern =>
        {
            // Stretched over the box of the mark it fills.
            Assert.Equal(("objectBoundingBox", "1", "1"), ((string?)pattern.Attribute("patternContentUnits"), (string?)pattern.Attribute("width"), (string?)pattern.Attribute("height")));
            var image = Assert.Single(pattern.Elements());
            Assert.Equal(("image", Pixel, "none", "0.5"), (image.Name.LocalName, (string?)image.Attribute("href"), (string?)image.Attribute("preserveAspectRatio"), (string?)image.Attribute("opacity")));
            Assert.Equal(("0", "0", "1", "1"), ((string?)image.Attribute("x"), (string?)image.Attribute("y"), (string?)image.Attribute("width"), (string?)image.Attribute("height")));
        });
        var ids = patterns.Select(pattern => "url(#" + (string)pattern.Attribute("id")! + ")").ToArray();
        Assert.All(ids, id => Assert.StartsWith("url(#c1-", id, StringComparison.Ordinal));
        var bars = Points(svg);
        Assert.Contains((string?)bars[0].Attribute("fill"), ids);
        Assert.Contains((string?)bars[1].Attribute("fill"), ids);
        Assert.NotEqual((string?)bars[0].Attribute("fill"), (string?)bars[1].Attribute("fill"));
        Assert.Contains((string?)Frame(svg).Attribute("fill"), ids);
        Assert.Contains((string?)svg.Descendants().Single(e => e.Attribute("data-chart-background") is not null).Attribute("fill"), ids);

        // Without a prefix for the ids nothing is defined and the bars keep a color.
        var plain = XDocument.Parse(DrawingChartSvg.Render(chart, 480, 320)!);
        Assert.DoesNotContain(plain.Descendants(), e => e.Name.LocalName is "pattern" or "image");
        Assert.All(Points(plain), bar => Assert.StartsWith("#", (string?)bar.Attribute("fill"), StringComparison.Ordinal));
    }

    [Fact]
    public void A_tiled_and_a_cropped_picture_fill_keep_their_tile_size_and_source_rectangle()
    {
        string? Resolve(string id, out string? reason)
        {
            reason = null;
            return Pixel;
        }
        var tiled = Read(Group("barChart", "<c:barDir val='col'/>" + Series("Sales", Text("A", "B"), Numbers("val", 1, 2),
            "<c:spPr><a:blipFill><a:blip r:embed='rId1'/><a:tile tx='19050' ty='0' sx='800000' sy='400000'/></a:blipFill></c:spPr>") + Axes), AxisParts, images: Resolve);
        var pattern = XDocument.Parse(DrawingChartSvg.Render(tiled.Chart!, 480, 320, "t")!).Descendants().Single(e => e.Name.LocalName == "pattern");
        // The 1x1 pixel picture at 800 % by 400 %, moved 2 px to the right.
        Assert.Equal(("userSpaceOnUse", "2", "0", "8", "4"), ((string?)pattern.Attribute("patternUnits"), (string?)pattern.Attribute("x"), (string?)pattern.Attribute("y"),
            (string?)pattern.Attribute("width"), (string?)pattern.Attribute("height")));
        Assert.Equal(("8", "4", Pixel), ((string?)pattern.Elements().Single().Attribute("width"), (string?)pattern.Elements().Single().Attribute("height"), (string?)pattern.Elements().Single().Attribute("href")));

        var cropped = Read(Group("barChart", "<c:barDir val='col'/>" + Series("Sales", Text("A", "B"), Numbers("val", 1, 2),
            "<c:spPr><a:blipFill><a:blip r:embed='rId1'/><a:srcRect l='50000'/><a:stretch><a:fillRect/></a:stretch></a:blipFill></c:spPr>") + Axes), AxisParts, images: Resolve);
        var image = XDocument.Parse(DrawingChartSvg.Render(cropped.Chart!, 480, 320, "t")!).Descendants().Single(e => e.Name.LocalName == "image");
        // The right half of the picture fills the box: the picture is twice as wide and starts one box to the left.
        Assert.Equal(("-1", "0", "2", "1"), ((string?)image.Attribute("x"), (string?)image.Attribute("y"), (string?)image.Attribute("width"), (string?)image.Attribute("height")));
    }

    [Fact]
    public void A_picture_fill_that_cannot_be_read_or_is_not_an_embeddable_image_leaves_the_color()
    {
        var series = Series("Sales", Text("A", "B"), Numbers("val", 1, 2), "<c:spPr><a:blipFill><a:blip r:embed='rId1'/></a:blipFill></c:spPr>");
        var unread = Read(Group("barChart", "<c:barDir val='col'/>" + series + Axes), AxisParts);
        Assert.Equal(["Picture fill is not drawn: this reader has no access to the pictures of the package."], unread.Warnings);
        Assert.Null(unread.Chart!.Series[0].FillPaint);
        Assert.NotNull(DrawingChartSvg.Render(unread.Chart, 480, 320, "c"));

        // A reference that is not a plain base64 image never reaches the SVG.
        var unsafePaint = new DrawingPaint { Kind = "picture", ImageReference = "data:image/svg+xml;base64,AAAA\" onload=\"x" };
        var chart = new DrawingChart("bar", null, [new DrawingChartSeries("S", ["A"], [1], [], "#336699") { FillPaint = unsafePaint }], false, "clustered", false);
        var svg = Assert.IsType<string>(DrawingChartSvg.Render(chart, 480, 320, "c"));
        Assert.DoesNotContain("onload", svg, StringComparison.Ordinal);
        Assert.Equal("#336699", (string?)Assert.Single(Points(XDocument.Parse(svg))).Attribute("fill"));

        // A picture on a line or a marker is named.
        var marker = Read(Group("lineChart", Series("Sales", Text("A", "B"), Numbers("val", 1, 2),
            "<c:marker><c:spPr><a:blipFill><a:blip r:embed='rId1'/></a:blipFill></c:spPr></c:marker>") + Axes), AxisParts);
        Assert.Equal(["Picture fills of lines, markers, text and other chart elements are not drawn."], marker.Warnings);
    }

    [Fact]
    public void The_chart_part_relationships_resolve_the_picture_of_a_fill_in_the_package()
    {
        var png = Convert.FromBase64String(Pixel[(Pixel.IndexOf(',') + 1)..]);
        var chartXml = ChartXml(Group("barChart", "<c:barDir val='col'/>" + Series("Sales", Text("A", "B"), Numbers("val", 1, 2),
            "<c:spPr><a:blipFill><a:blip r:embed='rId2'/><a:stretch><a:fillRect/></a:stretch></a:blipFill></c:spPr>") + Axes), AxisParts);
        var package = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["xl/charts/_rels/chart1.xml.rels"] = Encoding.UTF8.GetBytes(
                "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'>" +
                "<Relationship Id='rId1' Type='x' Target='https://example.invalid/a.png' TargetMode='External'/>" +
                "<Relationship Id='rId2' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/image' Target='../media/image1.png'/></Relationships>"),
            ["xl/media/image1.png"] = png,
        };
        var read = new List<string>();
        byte[]? ReadPart(string uri)
        {
            read.Add(uri);
            return package.GetValueOrDefault(uri);
        }
        var warnings = new List<string>();
        var chart = SourceDrawingPackage.Chart(chartXml, null, warnings, "chart", "/xl/charts/chart1.xml", ReadPart);
        Assert.Empty(warnings);
        Assert.Equal(("picture", Pixel), (chart!.Series[0].FillPaint!.Kind, chart.Series[0].FillPaint!.ImageReference));
        Assert.Equal(["xl/charts/_rels/chart1.xml.rels", "xl/media/image1.png"], read);

        // An external or missing relationship leaves the color, with the reason.
        var external = SourceDrawingPackage.Chart(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(chartXml).Replace("rId2", "rId1", StringComparison.Ordinal)),
            null, warnings, "chart", "xl/charts/chart1.xml", ReadPart);
        Assert.Null(external!.Series[0].FillPaint);
        Assert.Equal("SourceDrawingScenePartial: chart: Picture fill is not drawn: its relationship is missing or external.", Assert.Single(warnings));
    }

    // 7. Effects ----------------------------------------------------------------------------------------------

    [Fact]
    public void An_outer_shadow_of_a_series_and_of_the_plot_area_is_a_drop_shadow_filter()
    {
        const string shadow = "<a:effectLst><a:outerShdw blurRad='38100' dist='19050' dir='5400000' algn='tl' rotWithShape='0'><a:srgbClr val='102030'><a:alpha val='40000'/></a:srgbClr></a:outerShdw></a:effectLst>";
        var result = Read(Group("barChart", "<c:barDir val='col'/>" +
            Series("Sales", Text("A", "B"), Numbers("val", 1, 2), "<c:spPr><a:solidFill><a:srgbClr val='336699'/></a:solidFill>" + shadow + "</c:spPr>") +
            Series("Costs", Text("A", "B"), Numbers("val", 2, 1), index: 1) + Axes), AxisParts + "<c:spPr>" + shadow + "</c:spPr>");
        Assert.Empty(result.Warnings);
        // 19050 EMU are 2 px, straight down; a blur radius of 38100 EMU is 4 px.
        var expected = new DrawingChartShadow(0, 2, 4, "#102030", .4);
        Assert.Equal(expected, result.Chart!.Series[0].Shadow);
        Assert.Null(result.Chart.Series[1].Shadow);
        Assert.Equal(expected, result.Chart.PlotShadow);

        var svg = XDocument.Parse(Assert.IsType<string>(DrawingChartSvg.Render(result.Chart, 480, 320, "c1")));
        // The same shadow is defined once.
        var filter = svg.Descendants().Single(e => e.Name.LocalName == "filter");
        Assert.Equal(("c1-shadow-0", "userSpaceOnUse", "480", "320"), ((string?)filter.Attribute("id"), (string?)filter.Attribute("filterUnits"), (string?)filter.Attribute("width"), (string?)filter.Attribute("height")));
        var drop = Assert.Single(filter.Elements());
        Assert.Equal("feDropShadow", drop.Name.LocalName);
        Assert.Equal(("0", "2", "2", "#102030", "0.4"), ((string?)drop.Attribute("dx"), (string?)drop.Attribute("dy"), (string?)drop.Attribute("stdDeviation"),
            (string?)drop.Attribute("flood-color"), (string?)drop.Attribute("flood-opacity")));
        var bars = Points(svg);
        Assert.All(bars.Where(bar => (string?)bar.Attribute("data-series-index") == "0"), bar => Assert.Equal("url(#c1-shadow-0)", (string?)bar.Parent!.Attribute("filter")));
        Assert.All(bars.Where(bar => (string?)bar.Attribute("data-series-index") == "1"), bar => Assert.Null(bar.Parent!.Attribute("filter")));
        Assert.Equal("url(#c1-shadow-0)", (string?)Frame(svg).Attribute("filter"));

        // Without a prefix for the ids the chart is drawn without the shadow.
        Assert.DoesNotContain("filter", DrawingChartSvg.Render(result.Chart, 480, 320)!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lineChart", "")]
    [InlineData("areaChart", "")]
    [InlineData("pieChart", "")]
    [InlineData("scatterChart", "")]
    [InlineData("radarChart", "<c:radarStyle val='marker'/>")]
    public void The_shadow_of_a_series_lies_under_all_of_its_marks(string type, string settings)
    {
        const string shadow = "<c:spPr><a:effectLst><a:outerShdw blurRad='19050' dist='9525'><a:prstClr val='black'/></a:outerShdw></a:effectLst></c:spPr>";
        var xy = type == "scatterChart";
        var result = Read(Group(type, settings + (xy ? Bubbles("Sales", [1, 2, 3], [3, 1, 2], null, shadow) : Series("Sales", Text("A", "B", "C"), Numbers("val", 3, 1, 2), shadow)) +
            (type == "pieChart" ? "" : Axes)), type == "pieChart" ? "" : xy ? XyAxisParts : AxisParts);
        Assert.Equal(new DrawingChartShadow(1, 0, 2, "#000000"), result.Chart!.Series[0].Shadow);
        var svg = XDocument.Parse(Assert.IsType<string>(DrawingChartSvg.Render(result.Chart, 480, 320, "s")));
        Assert.Single(svg.Descendants(), e => e.Name.LocalName == "feDropShadow");
        var marks = Points(svg);
        Assert.Equal(3, marks.Length);
        Assert.All(marks, mark => Assert.Equal("url(#s-shadow-0)", (string?)mark.Parent!.Attribute("filter")));
    }

    [Fact]
    public void Other_effects_and_shadows_of_other_elements_are_named_and_the_chart_is_drawn_without_them()
    {
        var result = Read(Group("barChart", "<c:barDir val='col'/>" + Series("Sales", Text("A", "B"), Numbers("val", 1, 2),
            "<c:spPr><a:effectLst><a:glow rad='63500'><a:srgbClr val='FF0000'/></a:glow><a:softEdge rad='31750'/></a:effectLst></c:spPr>") + Axes), AxisParts,
            chartSpace: "<c:spPr><a:effectLst><a:outerShdw blurRad='38100' dist='19050'><a:srgbClr val='000000'/></a:outerShdw></a:effectLst></c:spPr>");
        Assert.Equal(["Chart effects other than an outer shadow (glow, softEdge) are not drawn.",
            "Shadows of chart elements other than series and the plot area are not drawn."], result.Warnings);
        Assert.Null(result.Chart!.Series[0].Shadow);
        var svg = Assert.IsType<string>(DrawingChartSvg.Render(result.Chart, 480, 320, "c"));
        Assert.DoesNotContain("<filter", svg, StringComparison.Ordinal);
        Assert.Equal(2, Points(XDocument.Parse(svg)).Length);

        // An empty effect list, as Office writes it to switch the effects of the theme off, says nothing.
        Assert.Empty(Read(Group("barChart", "<c:barDir val='col'/>" + Series("Sales", Text("A", "B"), Numbers("val", 1, 2), "<c:spPr><a:effectLst/></c:spPr>") + Axes), AxisParts).Warnings);
    }

    [Fact]
    public void Three_dimensional_shape_formatting_of_a_flat_chart_stays_a_diagnosed_fallback()
    {
        var result = Read(Group("barChart", "<c:barDir val='col'/>" + Series("Sales", Text("A", "B"), Numbers("val", 1, 2), "<c:spPr><a:sp3d><a:bevelT/></a:sp3d></c:spPr>") + Axes), AxisParts);
        Assert.Null(result.Chart);
        Assert.Equal(["3-D shape formatting is unsupported; the source frame and saved data must be used."], result.Warnings);
    }

    // 8. Scene JSON -------------------------------------------------------------------------------------------

    [Fact]
    public void The_settings_of_the_new_chart_types_round_trip_through_the_scene_json()
    {
        var shadow = new DrawingChartShadow(1, 2, 3, "#102030", .5);
        var picture = new DrawingPaint { Kind = "picture", ImageReference = Pixel, Tile = new(1, 2, 3, 4) };
        var series = new DrawingChartSeries("S", [], [1, 2], [3, 4], "#FF0000") { BubbleSizes = [5, null], HideMarkers = true, Shadow = shadow, FillPaint = picture, Plot = "stock" };
        var chart = new DrawingChart("bubble", null, [series], false, null, false)
        {
            RadarStyle = "filled", BubbleScale = 150, BubbleSizeIsWidth = true, ShowNegativeBubbles = true, HighLowLines = true,
            UpDownBars = new("#00FF00", null, 80), Wireframe = true, BandColors = new Dictionary<int, string> { [2] = "#0000FF" },
            PlotShadow = shadow, PlotFillPaint = picture,
        };
        var item = new WorkbookDrawingObject("chart", "chart", 120, 50, new()) { Chart = chart };
        IReadOnlyList<WorkbookDrawingObject> RoundTrip(WorkbookDrawingObject source) => WorkbookDrawingSceneJson.Read(
            WorkbookDrawingSceneJson.Write(new Dictionary<string, WorkbookDrawingScene> { { "sheet-A", new("A", [source]) } }))["sheet-A"].Objects;
        var restored = Assert.Single(RoundTrip(item)).Chart!;
        Assert.Equal(("filled", 150d, true, true, true, true), (restored.RadarStyle, restored.BubbleScale, restored.BubbleSizeIsWidth, restored.ShowNegativeBubbles, restored.HighLowLines, restored.Wireframe));
        Assert.Equal(chart.UpDownBars, restored.UpDownBars);
        Assert.Equal("#0000FF", restored.BandColors[2]);
        Assert.Equal(shadow, restored.PlotShadow);
        Assert.Equal(("picture", Pixel, picture.Tile), (restored.PlotFillPaint!.Kind, restored.PlotFillPaint.ImageReference, restored.PlotFillPaint.Tile));
        var one = Assert.Single(restored.Series);
        Assert.Equal(new double?[] { 5, null }, one.BubbleSizes);
        Assert.Equal((true, shadow, "stock", "picture"), (one.HideMarkers, one.Shadow, one.Plot, one.FillPaint!.Kind));

        // Values outside their ranges are rejected before rendering.
        Assert.Empty(RoundTrip(item with { Chart = chart with { RadarStyle = "spiral" } }));
        Assert.Empty(RoundTrip(item with { Chart = chart with { BubbleScale = 301 } }));
        Assert.Empty(RoundTrip(item with { Chart = chart with { PlotShadow = shadow with { Opacity = 2 } } }));
    }

    // Helpers -------------------------------------------------------------------------------------------------

    private sealed class Tolerance : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < .01;
        public int GetHashCode(double value) => 0;
    }

    private sealed class PointTolerance : IEqualityComparer<(double X, double Y)>
    {
        public bool Equals((double X, double Y) a, (double X, double Y) b) => Math.Abs(a.X - b.X) < .01 && Math.Abs(a.Y - b.Y) < .01;
        public int GetHashCode((double X, double Y) value) => 0;
    }

    private static (double X, double Y)[] Coordinates(string points) => points.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(point => point.Split(','))
        .Select(pair => (double.Parse(pair[0], CultureInfo.InvariantCulture), double.Parse(pair[1], CultureInfo.InvariantCulture))).ToArray();

    /// <summary>The centre of a pie slice: where its path starts.</summary>
    private static (double X, double Y) Centre(XElement slice)
    {
        var start = Regex.Match((string)slice.Attribute("d")!, @"^M ([\d.]+) ([\d.]+) L");
        Assert.True(start.Success);
        return (double.Parse(start.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(start.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    private static XDocument Render(DrawingChartReadResult result, double width = 480, double height = 320) =>
        XDocument.Parse(Assert.IsType<string>(DrawingChartSvg.Render(Assert.IsType<DrawingChart>(result.Chart), width, height)));

    private static XElement Frame(XDocument svg) => svg.Descendants().Single(e => (string?)e.Attribute("data-chart-plot") == "true");

    private static XElement[] Points(XDocument svg) => svg.Descendants().Where(e => e.Attribute("data-point-index") is not null).ToArray();

    private static XElement[] Stock(XDocument svg, string kind) => svg.Descendants().Where(e => (string?)e.Attribute("data-chart-stock") == kind).ToArray();

    private static XElement[] Labels(XDocument svg, string kind) => svg.Descendants()
        .Where(e => e.Name.LocalName == "text" && (string?)e.Attribute("data-chart-label") == kind).ToArray();

    /// <summary>Tick labels of the value axis: the only text without a label kind.</summary>
    private static XElement[] Ticks(XDocument svg) => svg.Descendants()
        .Where(e => e.Name.LocalName == "text" && e.Attribute("data-chart-label") is null).ToArray();

    private static double D(XElement element, string name) => double.Parse(element.Attribute(name)!.Value, CultureInfo.InvariantCulture);

    private static string Group(string type, string content) => "<c:" + type + ">" + content + "</c:" + type + ">";

    private static string Series(string name, string categories, string values, string extra = "", int index = 0) =>
        "<c:ser><c:idx val='" + index + "'/><c:order val='" + index + "'/><c:tx><c:v>" + name + "</c:v></c:tx>" + (extra.StartsWith("<c:spPr", StringComparison.Ordinal) ? extra : "") +
        categories + values + (extra.StartsWith("<c:spPr", StringComparison.Ordinal) ? "" : extra) + "</c:ser>";

    /// <summary>A series of a bubble chart, or of a scatter chart without sizes.</summary>
    private static string Bubbles(string name, double[] x, double[] y, double[]? sizes, string shape = "") =>
        "<c:ser><c:idx val='0'/><c:order val='0'/><c:tx><c:v>" + name + "</c:v></c:tx>" + shape + Numbers("xVal", x) + Numbers("yVal", y) +
        (sizes is null ? "" : Numbers("bubbleSize", sizes)) + "</c:ser>";

    /// <summary>Two series of three points: the front row 1, 3, 5 and the back row 3, 7, 8.</summary>
    private static string SurfaceGroup(string type, string settings)
    {
        // c:wireframe stands before the series, c:bandFmts after them.
        var first = settings.StartsWith("<c:wireframe", StringComparison.Ordinal);
        return Group(type, (first ? settings : "") +
            Series("Front", Text("A", "B", "C"), Numbers("val", 1, 3, 5)) + Series("Back", Text("A", "B", "C"), Numbers("val", 3, 7, 8), index: 1) +
            (first ? "" : settings) + Axes + "<c:axId val='9'/>");
    }

    private static string Text(params string[] values) =>
        "<c:cat><c:strRef><c:f>S!A2:A9</c:f><c:strCache><c:ptCount val='" + values.Length + "'/>" +
        string.Concat(values.Select((value, index) => "<c:pt idx='" + index + "'><c:v>" + value + "</c:v></c:pt>")) +
        "</c:strCache></c:strRef></c:cat>";

    private static string Numbers(string element, params double[] values) =>
        "<c:" + element + "><c:numRef><c:f>S!B2:B9</c:f><c:numCache><c:formatCode>General</c:formatCode>" +
        "<c:ptCount val='" + values.Length + "'/>" +
        string.Concat(values.Select((value, index) => "<c:pt idx='" + index + "'><c:v>" + value.ToString("R", CultureInfo.InvariantCulture) + "</c:v></c:pt>")) +
        "</c:numCache></c:numRef></c:" + element + ">";

    /// <summary>A chart part: the chart groups, then the fill and the axes of the plot area.</summary>
    private static byte[] ChartXml(string groups, string plotArea = "", string chartSpace = "", string title = "", bool legend = false) => Encoding.UTF8.GetBytes(
        "<c:chartSpace xmlns:c='http://schemas.openxmlformats.org/drawingml/2006/chart' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main' " +
        "xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'>" +
        "<c:chart>" + title + "<c:plotArea>" + groups + plotArea + "</c:plotArea>" + (legend ? "<c:legend><c:legendPos val='r'/></c:legend>" : "") + "</c:chart>" + chartSpace + "</c:chartSpace>");

    private static DrawingChartReadResult Read(string groups, string plotArea = "", string chartSpace = "", string title = "", bool legend = false,
        DrawingMlStyleReader.DrawingImageResolver? images = null) =>
        DrawingChartReader.Read(ChartXml(groups, plotArea, chartSpace, title, legend), null, images);
}
