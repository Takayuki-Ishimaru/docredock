using System.Globalization;
using System.Text;
using DocRedock.Core.Documents;

namespace DocRedock.Render;

/// <summary>Radar, bubble, stock and surface charts.</summary>
public static partial class DrawingChartSvg
{
    /// <summary>The automatic layout (or the manual one where it leaves room) with the definitions of one chart.</summary>
    private static bool TryPlot(DrawingChart chart, double width, double height, Bands bands, string? idPrefix, out Plot plot)
    {
        if (!TryCreatePlot(chart, width, height, bands, out plot) &&
            (chart.PlotLayout is null || !TryCreatePlot(chart with { PlotLayout = null }, width, height, bands, out plot)))
            return false;
        plot = plot with { Paints = new Paints(idPrefix) { Width = width, Height = height } };
        return true;
    }

    // Radar ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A radar chart: one spoke for each category, the first one upwards and the others clockwise, and the value
    /// axis from the centre along each spoke. The grid lines are rings (polygons) at the ticks of the value axis,
    /// whose labels stand on the first spoke. A series is a closed line through its points, filled for the style
    /// "filled", with markers for the style "marker". A blank breaks the line; in a filled radar it lies at the
    /// centre.
    /// </summary>
    private static string? RenderRadar(DrawingChart source, double width, double height, string? idPrefix)
    {
        // A radar chart has no place for axis titles.
        var chart = source with { ValueAxisTitle = null, CategoryAxisTitle = null, SecondaryAxisTitle = null };
        var slots = Slots(chart);
        var finite = FiniteValues(chart);
        if (slots == 0 || finite.Length == 0) return null;
        if (!TryScale(Math.Min(0d, finite.Min()), Math.Max(0d, finite.Max()), finite, chart.ValueAxisMinimum, chart.ValueAxisMaximum,
            false, chart.ValueAxisFormat, null, null, out var scale)) return null;
        if (!TryPlot(chart, width, height, new Bands([], [], "none", false, false), idPrefix, out var plot)) return null;

        var names = Enumerable.Range(0, slots).Select(index => Category(chart, index)).ToArray();
        if (names.Any(name => name.Length > 4096)) return null;
        var showNames = chart.CategoryAxisOptions?.Deleted != true;
        // The category labels stand around the plot: beside it up to a share of its width, and a line above and below.
        var labelWidth = showNames ? Math.Min(names.Max(name => TextWidth(name, 10)), plot.Width * .22) : 0;
        var radius = Math.Min(plot.Width / 2 - labelWidth - 8, plot.Height / 2 - (showNames ? 16 : 4));
        if (radius < MinPieDiameter / 2) return null;
        var cx = plot.OriginX + plot.Width / 2;
        var cy = plot.OriginY + plot.Height / 2;
        double Angle(int index) => -Math.PI / 2 + Math.PI * 2 * index / slots;
        (double X, double Y) At(int index, double value) => Polar(cx, cy, radius * Math.Clamp(scale.Position(value), 0, 1), Angle(index));
        static string Join(IEnumerable<(double X, double Y)> points) => string.Join(" ", points.Select(point => Num(point.X) + "," + Num(point.Y)));

        var svg = Begin(chart, width, height, plot);
        svg.Append("<rect data-chart-plot=\"true\" data-axis-min=\"").Append(Data(scale.Minimum)).Append("\" data-axis-max=\"").Append(Data(scale.Maximum))
            .Append("\" x=\"").Append(Num(plot.OriginX)).Append("\" y=\"").Append(Num(plot.OriginY))
            .Append("\" width=\"").Append(Num(plot.Width)).Append("\" height=\"").Append(Num(plot.Height)).Append("\" fill=\"")
            .Append(plot.Paints.Use(chart.PlotFillPaint, Hex(chart.PlotFill) ? chart.PlotFill! : "none")).Append("\" stroke=\"none\"")
            .Append(plot.Paints.Filter(chart.PlotShadow)).Append("/>");
        var ticks = scale.Ticks();
        foreach (var (_, share) in ticks)
            if (share > 1e-9)
                svg.Append("<polygon data-chart-ring=\"true\" points=\"")
                    .Append(Join(Enumerable.Range(0, slots).Select(index => Polar(cx, cy, radius * share, Angle(index)))))
                    .Append("\" fill=\"none\" stroke=\"#e6e6e6\"/>");
        for (var index = 0; index < slots; index++)
        {
            var (x, y) = Polar(cx, cy, radius, Angle(index));
            svg.Append("<line data-chart-spoke=\"true\" x1=\"").Append(Num(cx)).Append("\" y1=\"").Append(Num(cy)).Append("\" x2=\"").Append(Num(x))
                .Append("\" y2=\"").Append(Num(y)).Append("\" stroke=\"#cccccc\"/>");
        }
        if (chart.ValueAxisOptions?.Deleted != true)
        {
            var ink = Ink(chart, chart.ValueAxisOptions?.TextColor, onPlot: true, fallback: "#444");
            var labels = TickLabels(scale, chart.Date1904);
            for (var tick = 0; tick < ticks.Length; tick++)
                svg.Append("<text x=\"").Append(Num(cx - 4)).Append("\" y=\"").Append(Num(cy - radius * ticks[tick].Share + 3))
                    .Append("\" font-size=\"9\" fill=\"").Append(ink).Append("\" text-anchor=\"end\">").Append(Esc(labels[tick])).Append("</text>");
        }
        if (showNames)
        {
            var ink = Ink(chart, chart.CategoryAxisOptions?.TextColor);
            // A crowded axis names every n-th spoke.
            var step = Math.Max(1, (int)Math.Ceiling(slots * 12 / Math.Max(1, Math.PI * 2 * radius)));
            for (var index = 0; index < slots; index += step)
            {
                var (cos, sin) = (Math.Cos(Angle(index)), Math.Sin(Angle(index)));
                var (x, y) = Polar(cx, cy, radius + 6, Angle(index));
                var beside = Math.Abs(cos) > .15;
                Text(svg, "category", x, y + (sin > .5 ? 10 : sin < -.5 ? -2 : 3),
                    FitWidth(names[index], beside ? Math.Max(labelWidth, 12) : Math.Max(plot.Width - 16, 12), 10), 10,
                    !beside ? "middle" : cos > 0 ? "start" : "end", ink);
            }
        }

        var filled = chart.RadarStyle == "filled";
        var dataLabels = new List<string>();
        for (var s = 0; s < chart.Series.Count; s++)
        {
            var series = chart.Series[s];
            var color = Fill(chart, s);
            var values = Enumerable.Range(0, slots).Select(index =>
                index < series.Values.Count && series.Values[index] is { } value && double.IsFinite(value) ? (double?)value : null).ToArray();
            if (values.All(value => value is null)) continue;
            var shadow = plot.Paints.Filter(series.Shadow);
            svg.Append("<g data-chart-radar-series=\"").Append(s.ToString(CultureInfo.InvariantCulture)).Append('"').Append(shadow).Append("><title>")
                .Append(Esc(series.Name ?? "Series " + (s + 1).ToString(CultureInfo.InvariantCulture))).Append("</title>");
            if (filled || values.All(value => value is not null))
            {
                svg.Append("<polygon data-series-index=\"").Append(s.ToString(CultureInfo.InvariantCulture)).Append("\" points=\"")
                    .Append(Join(Enumerable.Range(0, slots).Select(index => At(index, values[index] ?? scale.Minimum)))).Append("\" fill=\"");
                if (filled) svg.Append(plot.Paints.Use(series.FillPaint, color)).Append("\" fill-opacity=\"0.35");
                else svg.Append("none");
                svg.Append("\" stroke=\"").Append(color).Append("\" stroke-width=\"1.6\" stroke-linejoin=\"round\"/>");
            }
            else
            {
                // The runs of points between the blanks, around the circle from the first blank on.
                var start = Array.FindIndex(values, value => value is null) + 1;
                var run = new List<(double X, double Y)>();
                for (var offset = 0; offset <= slots; offset++)
                {
                    var index = (start + offset) % slots;
                    if (offset < slots && values[index] is { } value) { run.Add(At(index, value)); continue; }
                    if (run.Count > 1)
                        svg.Append("<polyline data-series-index=\"").Append(s.ToString(CultureInfo.InvariantCulture)).Append("\" points=\"").Append(Join(run))
                            .Append("\" fill=\"none\" stroke=\"").Append(color).Append("\" stroke-width=\"1.6\" stroke-linejoin=\"round\"/>");
                    run.Clear();
                }
            }
            for (var index = 0; index < slots; index++)
            {
                if (values[index] is not { } value) continue;
                var (x, y) = At(index, value);
                if (chart.RadarStyle == "marker" && !series.HideMarkers)
                    Circle(svg, x, y, 2.6, FillPoint(chart, s, index, s), s, index, value, Label(series, index));
                if (ShowsValue(series, index) && dataLabels.Count < MaxDataLabels)
                    dataLabels.Add(DataLabel(chart, x, y - 6, LabelNumber(chart, series, index, value), "middle"));
            }
            svg.Append("</g>");
        }
        foreach (var label in dataLabels) svg.Append(label);
        return End(svg, chart, plot);
    }

    // Bubble --------------------------------------------------------------------------------------------------

    /// <summary>
    /// The size of a bubble, or null where none is drawn: no size, zero, or a negative size without
    /// c:showNegBubbles. A series without sizes has bubbles of the size 1, as in Excel.
    /// </summary>
    private static double? BubbleSize(DrawingChart chart, DrawingChartSeries series, int index)
    {
        var size = series.BubbleSizes.Count == 0 ? 1 : index < series.BubbleSizes.Count ? series.BubbleSizes[index] : null;
        return size is { } amount && double.IsFinite(amount) && amount != 0 && (amount > 0 || chart.ShowNegativeBubbles) ? amount : null;
    }

    /// <summary>The largest size among the bubbles that are drawn: the one every other bubble is a share of.</summary>
    private static double BubbleLargest(DrawingChart chart, int[] members) => members
        .Where(index => Kind(chart, chart.Series[index]) == "bubble")
        .SelectMany(index => Enumerable.Range(0, chart.Series[index].Values.Count).Select(point => Math.Abs(BubbleSize(chart, chart.Series[index], point) ?? 0)))
        .DefaultIfEmpty(0).Max();

    /// <summary>The range of the centres of the bubbles with room for the bubbles at its ends, as Excel pads the axes.</summary>
    private static (double Low, double High) BubbleRoom(double[] centres)
    {
        var (low, high) = (centres.Min(), centres.Max());
        var room = high > low ? (high - low) * .15 : Math.Max(1, Math.Abs(high) * .15);
        // The axis does not reach below zero for the room alone.
        return (low >= 0 ? Math.Max(0, low - room) : low - room, high + room);
    }

    // Stock ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The value range of a stock chart whose prices lie close together (the lowest above five sixths of the
    /// highest): Excel does not start such an axis at zero. Null keeps the axis from zero.
    /// </summary>
    private static (double Low, double High)? StockRange(IReadOnlyCollection<double> prices)
    {
        if (prices.Count == 0) return null;
        var (low, high) = (prices.Min(), prices.Max());
        if (!(low > 0) || !(low > high * 5 / 6) || !double.IsFinite(high)) return null;
        var room = high > low ? (high - low) * .1 : high * .01;
        return (low - room, high + room);
    }

    private static string? RenderStock(DrawingChart chart, double width, double height, Plot plot, ValueScale scale)
    {
        var slots = Slots(chart);
        if (slots == 0) return null;
        var svg = Begin(chart, width, height, plot);
        WriteFrame(svg, chart, plot, scale);
        BeginPlotClip(svg, plot);
        WriteStock(svg, chart, plot, Enumerable.Range(0, chart.Series.Count).ToArray(), slots, scale);
        svg.Append("</svg>");
        return End(svg, chart, plot);
    }

    /// <summary>
    /// The marks of a stock group at each category. A high-low line (c:hiLowLines) joins the highest and the
    /// lowest value of the series. An up/down bar (c:upDownBars) reaches from the first series (open) to the last
    /// one (close): white by default where the close is not below the open, black where it is. Without up/down
    /// bars the close is a tick to the right of the line, and the open of four series a tick to the left. The
    /// lines and markers of the series themselves are not drawn.
    /// </summary>
    private static void WriteStock(StringBuilder svg, DrawingChart chart, Plot plot, int[] members, int slots, ValueScale scale)
    {
        if (members.Length == 0) return;
        var flip = plot.Sides.ReverseCategories && !chart.Horizontal;
        var band = plot.Width / slots;
        var tick = Math.Clamp(band * .2, 2, 8);
        static string Slot(int index) => " data-slot=\"" + index.ToString(CultureInfo.InvariantCulture) + "\"";
        for (var i = 0; i < slots; i++)
        {
            var values = members.Select(member => i < chart.Series[member].Values.Count && chart.Series[member].Values[i] is { } value &&
                scale.Plots(value) ? (double?)value : null).ToArray();
            var present = values.OfType<double>().ToArray();
            if (present.Length == 0) continue;
            var x = plot.PointX(flip ? slots - 1 - i : i, slots);
            if (chart.HighLowLines && present.Length > 1)
                svg.Append("<line data-chart-stock=\"high-low\"").Append(Slot(i)).Append(" data-high=\"").Append(Data(present.Max()))
                    .Append("\" data-low=\"").Append(Data(present.Min())).Append("\" x1=\"").Append(Num(x)).Append("\" y1=\"").Append(Num(plot.Y(present.Max(), scale)))
                    .Append("\" x2=\"").Append(Num(x)).Append("\" y2=\"").Append(Num(plot.Y(present.Min(), scale))).Append("\" stroke=\"#444444\" stroke-width=\"1\"/>");
            var open = values[0];
            var close = values[^1];
            if (chart.UpDownBars is { } bars && members.Length > 1)
            {
                if (open is not { } from || close is not { } to) continue;
                var barWidth = band / (1 + Math.Clamp(double.IsFinite(bars.GapWidthPercent) ? bars.GapWidthPercent : 150, 0, 500) / 100);
                var top = Math.Min(plot.Y(from, scale), plot.Y(to, scale));
                var up = to >= from;
                svg.Append("<rect data-chart-stock=\"").Append(up ? "up-bar" : "down-bar").Append('"').Append(Slot(i))
                    .Append(" data-open=\"").Append(Data(from)).Append("\" data-close=\"").Append(Data(to))
                    .Append("\" x=\"").Append(Num(x - barWidth / 2)).Append("\" y=\"").Append(Num(top)).Append("\" width=\"").Append(Num(barWidth))
                    .Append("\" height=\"").Append(Num(Math.Abs(plot.Y(from, scale) - plot.Y(to, scale)))).Append("\" fill=\"")
                    .Append(up ? Hex(bars.UpColor) ? bars.UpColor : "#FFFFFF" : Hex(bars.DownColor) ? bars.DownColor : "#000000")
                    .Append("\" stroke=\"#444444\" stroke-width=\"1\"><title>")
                    .Append(Esc($"{Category(chart, i)}: {Data(from)} → {Data(to)}")).Append("</title></rect>");
                continue;
            }
            void Tick(int member, double value, double from, double to) =>
                svg.Append("<line data-chart-stock=\"").Append(from < x ? "open" : "close").Append('"').Append(Point(member, i, value))
                    .Append(" x1=\"").Append(Num(from)).Append("\" y1=\"").Append(Num(plot.Y(value, scale))).Append("\" x2=\"").Append(Num(to))
                    .Append("\" y2=\"").Append(Num(plot.Y(value, scale))).Append("\" stroke=\"").Append(Fill(chart, member)).Append("\" stroke-width=\"1.6\"><title>")
                    .Append(Esc($"{Label(chart.Series[member], i)}: {Data(value)}")).Append("</title></line>");
            if (members.Length >= 4 && open is { } first) Tick(members[0], first, x - tick, x);
            if (close is { } last) Tick(members[^1], last, x, x + tick);
        }
    }

    // Surface -------------------------------------------------------------------------------------------------

    /// <summary>The color of a value band of a surface chart: its c:bandFmt, else a color of the palette.</summary>
    private static string BandColor(DrawingChart chart, int band) =>
        chart.BandColors is { } colors && colors.TryGetValue(band, out var color) && Hex(color) ? color : Palette[Math.Abs(band) % Palette.Length];

    /// <summary>
    /// A surface chart seen from above (a contour chart): the categories run along the width and the series, the
    /// first one at the bottom, along the height. Each point is a cell in the color of its value band; the bands
    /// lie between the ticks of the value scale and the legend names them. A wireframe chart (c:wireframe) draws
    /// only the lines between cells of different bands.
    /// </summary>
    private static string? RenderSurface(DrawingChart source, double width, double height, string? idPrefix)
    {
        // The value axis is the depth of the surface: its title has no place in the top view.
        var chart = source with { ValueAxisTitle = null, SecondaryAxisTitle = null };
        var slots = Slots(chart);
        var finite = FiniteValues(chart);
        if (slots == 0 || finite.Length == 0) return null;
        if (!TryScale(Math.Min(0d, finite.Min()), Math.Max(0d, finite.Max()), finite, chart.ValueAxisMinimum, chart.ValueAxisMaximum,
            false, chart.ValueAxisFormat, null, null, out var scale)) return null;
        var ticks = scale.Ticks();
        var bandCount = ticks.Length - 1;
        if (bandCount < 1) return null;
        var tickLabels = TickLabels(scale, chart.Date1904);
        var legend = Enumerable.Range(0, bandCount).Select(band => tickLabels[band] + "–" + tickLabels[band + 1]).ToArray();
        var rows = chart.Series.Select((series, index) => string.IsNullOrWhiteSpace(series.Name) ? (index + 1).ToString(CultureInfo.InvariantCulture) : series.Name).ToArray();
        if (rows.Any(row => row.Length > 4096)) return null;
        var bands = new Bands(rows, [], chart.CategoryAxisOptions?.Deleted == true ? "none" : "bottom", false, false) { Legend = legend };
        if (!TryPlot(chart, width, height, bands, idPrefix, out var plot)) return null;

        var count = chart.Series.Count;
        var cellWidth = plot.Width / slots;
        var cellHeight = plot.Height / count;
        int? Band(int series, int point) => point < chart.Series[series].Values.Count && chart.Series[series].Values[point] is { } value && double.IsFinite(value)
            ? Math.Clamp((int)Math.Floor(scale.Position(value) * bandCount), 0, bandCount - 1) : null;
        double Left(int point) => plot.OriginX + cellWidth * point;
        double Top(int series) => plot.OriginY + plot.Height - cellHeight * (series + 1);

        var svg = Begin(chart, width, height, plot);
        svg.Append("<rect data-chart-plot=\"true\" data-axis-min=\"").Append(Data(scale.Minimum)).Append("\" data-axis-max=\"").Append(Data(scale.Maximum))
            .Append("\" x=\"").Append(Num(plot.OriginX)).Append("\" y=\"").Append(Num(plot.OriginY))
            .Append("\" width=\"").Append(Num(plot.Width)).Append("\" height=\"").Append(Num(plot.Height)).Append("\" fill=\"")
            .Append(plot.Paints.Use(chart.PlotFillPaint, chart.PlotFill == "none" ? "none" : Hex(chart.PlotFill) ? chart.PlotFill! : "#ffffff"))
            .Append("\" stroke=\"#cccccc\" stroke-width=\"0.8\"").Append(plot.Paints.Filter(chart.PlotShadow)).Append("/>");
        svg.Append("<g data-chart-surface=\"").Append(chart.Wireframe ? "wireframe" : "bands").Append("\" shape-rendering=\"crispEdges\">");
        for (var s = 0; s < count; s++)
            for (var i = 0; i < slots; i++)
            {
                if (Band(s, i) is not { } band) continue;
                if (!chart.Wireframe)
                {
                    var value = chart.Series[s].Values[i]!.Value;
                    svg.Append("<rect data-band=\"").Append(band.ToString(CultureInfo.InvariantCulture)).Append("\" x=\"").Append(Num(Left(i))).Append("\" y=\"").Append(Num(Top(s)))
                        .Append("\" width=\"").Append(Num(cellWidth)).Append("\" height=\"").Append(Num(cellHeight)).Append("\" fill=\"").Append(BandColor(chart, band)).Append('"')
                        .Append(Point(s, i, value)).Append("><title>").Append(Esc($"{rows[s]}, {Category(chart, i)}: {Data(value)}")).Append("</title></rect>");
                    continue;
                }
                // The boundary to the next cell on the right and to the next series above, in the color of the higher band.
                void Boundary(int other, double x1, double y1, double x2, double y2) =>
                    svg.Append("<line data-chart-band-boundary=\"").Append(Math.Max(band, other).ToString(CultureInfo.InvariantCulture)).Append("\" x1=\"").Append(Num(x1))
                        .Append("\" y1=\"").Append(Num(y1)).Append("\" x2=\"").Append(Num(x2)).Append("\" y2=\"").Append(Num(y2))
                        .Append("\" stroke=\"").Append(BandColor(chart, Math.Max(band, other))).Append("\" stroke-width=\"1.2\"/>");
                if (i + 1 < slots && Band(s, i + 1) is { } right && right != band) Boundary(right, Left(i + 1), Top(s), Left(i + 1), Top(s) + cellHeight);
                if (s + 1 < count && Band(s + 1, i) is { } above && above != band) Boundary(above, Left(i), Top(s), Left(i) + cellWidth, Top(s));
            }
        svg.Append("</g>");
        // The series axis: the names of the series left of their rows; a crowded axis names every n-th row.
        var ink = Ink(chart, null);
        var step = Math.Max(1, (int)Math.Ceiling(11 / Math.Max(.01, cellHeight)));
        for (var s = 0; s < count; s += step)
            Text(svg, "series-axis", plot.OriginX - 4, Top(s) + cellHeight / 2 + 3,
                FitWidth(rows[s], plot.Decoration?.TickWidth ?? double.MaxValue, 9), 9, "end", ink);
        return End(svg, chart, plot);
    }
}
