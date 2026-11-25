using System.Globalization;
using System.Security;
using System.Text;
using DocRedock.Core.Documents;

namespace DocRedock.Render;

/// <summary>
/// Stateless native SVG renderer for cached chart data. It draws real geometry from the raw cached numbers, keeps
/// gaps in place, and refuses anything it cannot plot honestly (unsupported types, nonfinite bounds, oversized output).
/// </summary>
public static partial class DrawingChartSvg
{
    private const int MaxSeries = 256;
    private const int MaxPoints = 5000;
    private const int MaxDataLabels = 512;
    private const double MinWidth = 64;
    private const double MinHeight = 64;
    private const double MinPieDiameter = 8;
    private static readonly string[] Palette = ["#4E79A7", "#F28E2B", "#E15759", "#76B7B2", "#59A14F", "#EDC948", "#B07AA1", "#9C755F"];

    /// <param name="idPrefix">
    /// Prefix of the ids of the gradients and patterns the chart defines; it must be unique in the document the SVG
    /// is placed in. Without it a gradient or pattern fill is drawn as its representative color.
    /// </param>
    public static string? Render(DrawingChart chart, double width, double height, string? idPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        if (!double.IsFinite(width) || !double.IsFinite(height)) return null;
        if (width < MinWidth || height < MinHeight || width > 100_000_000 || height > 100_000_000) return null;
        if (chart.Series.Count == 0 || chart.Series.Count > MaxSeries) return null;
        if (!IsSupported(chart.Type)) return null;
        if (chart.Grouping is not (null or "" or "standard" or "clustered" or "stacked" or "percentStacked")) return null;

        var total = chart.Series.Sum(series => (long)series.Values.Count);
        if (total == 0 || total > MaxPoints) return null;
        var labelCount = chart.Series.Sum(series => series.PointLabels is { Count: > 0 }
            ? Enumerable.Range(0, series.Values.Count).LongCount(index => ShowsValue(series, index) || ShowsPercent(series, index))
            : series.ShowValueLabels == true || series.ShowPercentLabels == true ? series.Values.Count : 0L);
        if (labelCount > MaxDataLabels) return null;
        if (chart.Series.Any(series => series.ShowPercentLabels == true && Kind(chart, series) is not ("pie" or "doughnut"))) return null;

        // A combination chart draws its groups in one plot area; a pie plots its first series only, as Office does.
        var combination = chart.Type is "bar" or "line" or "area" or "scatter" or "stock" && chart.Series.Any(series =>
            series.SecondaryAxis || Kind(chart, series) != chart.Type || series.Group != chart.Series[0].Group);
        if (chart.Series.Any(series => !IsSupported(Kind(chart, series)) ||
                series.Grouping is not (null or "standard" or "clustered" or "stacked" or "percentStacked")))
            return null;
        try
        {
        // A radar chart has polar axes and a surface chart a grid of its own; neither shares the layout of the others.
        if (chart.Type == "radar") return RenderRadar(chart, width, height, idPrefix);
        if (chart.Type == "surface") return RenderSurface(chart, width, height, idPrefix);
        if (!TryScales(chart, combination, out var primary, out var secondary)) return null;
        ValueScale? xScale = null;
        var scatter = Enumerable.Range(0, chart.Series.Count).Where(index => Kind(chart, chart.Series[index]) is "scatter" or "bubble").ToArray();
        if (scatter.Length > 0 && (Xy(chart) || !chart.ScatterOnCategoryAxis) && !TryXScale(chart, scatter, out xScale))
            return null;

        // Where the axes lie. The value axis crosses the category axis at its first category unless c:crosses
        // says "max", so a reversed category axis takes it to the other side, as in Excel; the category axis
        // lies where it crosses the value axis. A secondary value axis lies opposite the primary one.
        var slots = Slots(chart);
        var valueFar = false;
        var categoryFar = false;
        if (primary is not null)
        {
            valueFar = Xy(chart)
                ? xScale is not null && AtFarEnd(xScale)
                : (chart.ValueAxisOptions?.Crosses == "max" || chart.ValueAxisOptions?.CrossesAt > slots) != (chart.CategoryAxisOptions?.Reversed == true);
            categoryFar = !primary.Horizontal && AtFarEnd(primary);
        }
        var secondaryFar = secondary is not null && primary is not null && (secondary.Horizontal != primary.Horizontal || !valueFar);
        string[] left = [], right = [];
        var below = false;
        var above = false;
        void Place(ValueScale scale, bool far)
        {
            if (scale.Deleted) return;
            if (scale.Horizontal) { above |= far; below |= !far; }
            else if (far) right = TickLabels(scale, chart.Date1904);
            else left = TickLabels(scale, chart.Date1904);
        }
        if (primary is not null) Place(primary, valueFar);
        if (secondary is not null) Place(secondary, secondaryFar);
        var categories = primary is null || Xy(chart) || chart.CategoryAxisOptions?.Deleted == true ? "none"
            : primary.Horizontal ? "left" : categoryFar ? "top" : "bottom";
        if (Xy(chart) && xScale is { Deleted: false }) { above |= categoryFar; below |= !categoryFar; }
        var bands = new Bands(left, right, categories, below, above);

        // A manual layout that leaves no room for the plot falls back to the automatic layout.
        if (!TryCreatePlot(chart, width, height, bands, out var plot) &&
            (chart.PlotLayout is null || !TryCreatePlot(chart with { PlotLayout = null }, width, height, bands, out plot)))
            return null;
        plot = plot with
        {
            Paints = new Paints(idPrefix) { Width = width, Height = height },
            Sides = new Sides(valueFar, secondaryFar, categoryFar, !Xy(chart) && chart.CategoryAxisOptions?.Reversed == true),
        };
        return chart.Type switch
        {
            _ when combination => RenderCombination(chart, width, height, plot, primary!, secondary, xScale),
            "bar" => RenderBars(chart, width, height, plot, primary!, stacked: chart.Grouping is "stacked" or "percentStacked"),
            "line" => RenderStroke(chart, width, height, plot, primary!, filled: false),
            "area" => RenderStroke(chart, width, height, plot, primary!, filled: true),
            "pie" or "doughnut" => RenderPie(chart, width, height, plot, chart.Type == "doughnut"),
            "scatter" or "bubble" => RenderScatter(chart, width, height, plot, primary!, xScale!),
            "stock" => RenderStock(chart, width, height, plot, primary!),
            _ => null,
        };
        }
        catch (ArithmeticException) { return null; }
    }

    private static bool IsSupported(string type) => type is "bar" or "line" or "pie" or "doughnut" or "area" or "scatter" or
        "radar" or "bubble" or "stock" or "surface";

    /// <summary>A scatter or bubble chart: both of its axes are value axes.</summary>
    private static bool Xy(DrawingChart chart) => chart.Type is "scatter" or "bubble";

    /// <summary>The chart group type of a series: its own in a combination chart, else the type of the chart.</summary>
    private static string Kind(DrawingChart chart, DrawingChartSeries series) => series.Plot ?? chart.Type;

    /// <summary>
    /// Number of slots of the category axis: the longest series that is plotted by category, or the longest
    /// series of a chart without one.
    /// </summary>
    private static int Slots(DrawingChart chart)
    {
        var byCategory = chart.Series.Where(series => Kind(chart, series) is "bar" or "line" or "area" or "stock").ToArray();
        return (byCategory.Length > 0 ? byCategory : chart.Series).Max(series => series.PointCount);
    }

    /// <summary>The series of one chart group, which cluster and stack among themselves.</summary>
    private sealed record PlotGroup(string Kind, int[] Members, bool Secondary, string Grouping)
    {
        public bool Stacked => Grouping is "stacked" or "percentStacked";
    }

    private static PlotGroup[] Groups(DrawingChart chart) => Enumerable.Range(0, chart.Series.Count)
        .GroupBy(index => (chart.Series[index].Group, Kind: Kind(chart, chart.Series[index]), chart.Series[index].SecondaryAxis))
        .Select(group => new PlotGroup(group.Key.Kind, group.ToArray(), group.Key.SecondaryAxis,
            chart.Series[group.First()].Grouping ?? (group.Key.Kind == "bar" ? chart.Grouping ?? "" : "standard")))
        .ToArray();

    /// <summary>
    /// A value axis: its bounds, whether it shows shares of a percent stack, its number format and its options.
    /// </summary>
    private sealed record ValueScale(double Minimum, double Maximum, bool Percent, string? Format)
    {
        /// <summary>Base of a logarithmic scale; null is a linear scale.</summary>
        public double? LogBase { get; init; }
        public bool Reversed { get; init; }
        public bool Deleted { get; init; }

        /// <summary>The axis runs along the width of the plot (the value axis of horizontal bars, the x axis of a scatter chart).</summary>
        public bool Horizontal { get; init; }

        /// <summary>The value where the other axis crosses this one: bars and areas start there.</summary>
        public double Cross { get; init; }
        public bool ExplicitCross { get; init; }
        public string? TextColor { get; init; }

        /// <summary>A logarithmic scale has no place for zero or a negative number.</summary>
        public bool Plots(double value) => double.IsFinite(value) && (LogBase is null || value > 0);

        /// <summary>Position of a value along the axis: 0 at its near end (bottom or left), 1 at its far end.</summary>
        public double Position(double value)
        {
            var share = LogBase is not null ? value <= 0 ? 0 : Math.Log(value / Minimum) / Math.Log(Maximum / Minimum)
                : Maximum == Minimum ? 0.5 : (value - Minimum) / (Maximum - Minimum);
            return Reversed ? 1 - share : share;
        }

        /// <summary>
        /// The ticks: five on a linear axis; on a logarithmic one the minimum times each power of the base, as
        /// Excel multiplies the major unit.
        /// </summary>
        public (double Value, double Share)[] Ticks()
        {
            if (LogBase is not { } logBase)
                return Enumerable.Range(0, 5).Select(tick => (Minimum + (Maximum - Minimum) * (tick / 4d), Reversed ? 1 - tick / 4d : tick / 4d)).ToArray();
            var count = (int)Math.Min(4096, Math.Floor(Math.Log(Maximum / Minimum, logBase) + 1e-9));
            var step = Math.Max(1, (int)Math.Ceiling(count / 12d));
            return Enumerable.Range(0, count / step + 1).Select(tick => Minimum * Math.Pow(logBase, tick * step))
                .Select(value => (value, Position(value))).ToArray();
        }
    }

    /// <summary>The axis the scale is crossed by lies at its far end (top or right).</summary>
    private static bool AtFarEnd(ValueScale scale) =>
        (scale.ExplicitCross || scale.Reversed) && scale.Position(scale.Cross) >= 1 - 1e-9;

    /// <summary>
    /// A scale for the plotted range. A logarithmic axis reaches from the power of its base below the smallest
    /// positive value to the power above the largest one, as Excel's automatic bounds do. <paramref name="crossing"/>
    /// is the axis that crosses this one: its c:crosses or c:crossesAt gives the value where it lies, else zero
    /// (the minimum of a logarithmic axis).
    /// </summary>
    private static bool TryScale(double low, double high, IEnumerable<double> plotted, double? requestedMinimum,
        double? requestedMaximum, bool percent, string? format, DrawingChartAxis? axis, DrawingChartAxis? crossing, out ValueScale scale)
    {
        scale = new(0, 1, false, null);
        double minimum, maximum;
        double? logBase = axis?.LogBase is { } given && double.IsFinite(given) && given is >= 2 and <= 1000 ? given : null;
        if (logBase is { } power)
        {
            var positive = plotted.Where(value => value > 0 && double.IsFinite(value)).ToArray();
            var smallest = positive.Length == 0 ? 1 : positive.Min();
            var largest = positive.Length == 0 ? power : positive.Max();
            minimum = requestedMinimum is > 0 ? requestedMinimum.Value : Math.Pow(power, Math.Floor(Math.Log(smallest, power) + 1e-9));
            maximum = requestedMaximum is > 0 ? requestedMaximum.Value : Math.Pow(power, Math.Ceiling(Math.Log(largest, power) - 1e-9));
            if (maximum <= minimum)
            {
                if (requestedMaximum is > 0 && requestedMinimum is not > 0) minimum = maximum / power;
                else if (requestedMinimum is > 0 && requestedMaximum is > 0) return false;
                else maximum = minimum * power;
            }
            if (!(minimum > 0) || !double.IsFinite(maximum) || !(maximum > minimum) || !(Math.Log(maximum / minimum) > 0)) return false;
            percent = false;
        }
        else if (!TryBounds(low, high, requestedMinimum, requestedMaximum, out minimum, out maximum)) return false;
        var cross = crossing?.CrossesAt is { } at && double.IsFinite(at) ? Math.Clamp(at, minimum, maximum)
            : crossing?.Crosses == "max" ? maximum
            : crossing?.Crosses == "min" || logBase is not null ? minimum
            : Math.Clamp(0d, minimum, maximum);
        scale = new(minimum, maximum, percent, format)
        {
            LogBase = logBase, Reversed = axis?.Reversed == true, Deleted = axis?.Deleted == true, Cross = cross,
            ExplicitCross = crossing is { CrossesAt: not null } or { Crosses: "min" or "max" }, TextColor = axis?.TextColor,
        };
        return true;
    }

    /// <summary>
    /// The scale of the value axis, and of the secondary value axis of a combination chart. A pie has neither.
    /// </summary>
    private static bool TryScales(DrawingChart chart, bool combination, out ValueScale? primary, out ValueScale? secondary)
    {
        primary = secondary = null;
        if (chart.Type is "pie" or "doughnut") return true;
        var all = Enumerable.Range(0, chart.Series.Count).ToArray();
        var stacked = chart.Grouping is "stacked" or "percentStacked";
        var percent = chart.Grouping == "percentStacked";
        if (combination)
        {
            var groups = Groups(chart);
            foreach (var second in new[] { false, true })
            {
                var members = groups.Where(group => group.Secondary == second && group.Kind is not ("pie" or "doughnut")).ToArray();
                if (members.Length == 0) continue;
                var low = 0d; var high = 0d;
                var shares = false;
                var plotted = new List<double>();
                foreach (var group in members)
                {
                    IEnumerable<double> ends;
                    if (group.Kind == "bar")
                    {
                        var rows = PlotValues(chart, group.Members, group.Grouping);
                        if (!TryBarRange(chart, rows, group.Members, group.Stacked, out var barLow, out var barHigh)) return false;
                        ends = group.Members.SelectMany(index => rows[index]).OfType<double>().Append(barLow).Append(barHigh);
                        shares |= group.Grouping == "percentStacked";
                    }
                    else if (group.Kind == "scatter") ends = ScatterPairs(chart, group.Members).Select(pair => pair.Y);
                    else if (group.Stacked)
                    {
                        if (StrokeStack(chart, group.Members, group.Grouping == "percentStacked") is not { } stack) return false;
                        ends = group.Members.SelectMany(index => stack.Top[index]).OfType<double>();
                        shares |= group.Grouping == "percentStacked";
                    }
                    else ends = group.Members.SelectMany(index => chart.Series[index].Values).OfType<double>().Where(double.IsFinite);
                    foreach (var value in ends)
                    {
                        low = Math.Min(low, value); high = Math.Max(high, value);
                        plotted.Add(value);
                    }
                }
                if (members.All(group => group.Kind == "stock") && StockRange(plotted) is { } prices) (low, high) = prices;
                // Horizontal bars have a horizontal value axis; a line, an area or a scatter group cannot share it.
                var horizontal = chart.Horizontal && members.Any(group => group.Kind == "bar");
                if (horizontal && members.Any(group => group.Kind != "bar")) return false;
                if (!TryScale(low, high, plotted, second ? chart.SecondaryAxisMinimum : chart.ValueAxisMinimum,
                    second ? chart.SecondaryAxisMaximum : chart.ValueAxisMaximum, shares,
                    second ? chart.SecondaryAxisFormat : chart.ValueAxisFormat,
                    second ? chart.SecondaryAxisOptions : chart.ValueAxisOptions, second ? null : chart.CategoryAxisOptions, out var scale)) return false;
                scale = scale with { Horizontal = horizontal };
                if (second) secondary = scale; else primary = scale;
            }
            return primary is not null && FiniteValues(chart).Length > 0;
        }

        double dataMinimum, dataMaximum;
        IEnumerable<double> values;
        switch (chart.Type)
        {
            case "bar":
                var barRows = PlotValues(chart, all, chart.Grouping);
                if (!barRows.SelectMany(row => row).OfType<double>().Any()) return false;
                if (!TryBarRange(chart, barRows, all, stacked, out dataMinimum, out dataMaximum)) return false;
                values = barRows.SelectMany(row => row).OfType<double>().Append(dataMaximum);
                break;
            case "scatter":
            case "bubble":
                var ys = ScatterPairs(chart, all).Select(pair => pair.Y).ToArray();
                if (ys.Length == 0 || ys.Length > MaxPoints) return false;
                (dataMinimum, dataMaximum) = (Math.Min(0d, ys.Min()), Math.Max(0d, ys.Max()));
                if (chart.Type == "bubble") (dataMinimum, dataMaximum) = (Math.Min(0d, BubbleRoom(ys).Low), Math.Max(0d, BubbleRoom(ys).High));
                values = ys;
                percent = false;
                break;
            default:
                var finite = FiniteValues(chart);
                if (finite.Length == 0) return false;
                if (stacked)
                {
                    if (StrokeStack(chart, all, percent) is not { } stack) return false;
                    finite = stack.Top.SelectMany(row => row).OfType<double>().ToArray();
                    if (finite.Length == 0) return false;
                }
                else percent = false;
                (dataMinimum, dataMaximum) = (Math.Min(0d, finite.Min()), Math.Max(0d, finite.Max()));
                if (chart.Type == "stock" && StockRange(finite) is { } range) (dataMinimum, dataMaximum) = range;
                values = finite;
                break;
        }
        if (!TryScale(dataMinimum, dataMaximum, values, chart.ValueAxisMinimum, chart.ValueAxisMaximum, percent, chart.ValueAxisFormat,
            chart.ValueAxisOptions, chart.CategoryAxisOptions, out var single)) return false;
        primary = single with { Horizontal = chart.Type == "bar" && chart.Horizontal };
        return true;
    }

    /// <summary>
    /// The horizontal value axis of the scatter series. The vertical axis crosses it where its own c:crosses or
    /// c:crossesAt says.
    /// </summary>
    private static bool TryXScale(DrawingChart chart, int[] members, out ValueScale? scale)
    {
        scale = null;
        var xs = ScatterPairs(chart, members).Select(pair => pair.X).ToArray();
        if (xs.Length == 0) return false;
        var own = Xy(chart);
        var (lowest, highest) = chart.Type == "bubble" ? BubbleRoom(xs) : (xs.Min(), xs.Max());
        if (!TryScale(lowest, highest, xs, chart.XAxisMinimum, chart.XAxisMaximum, false, chart.XAxisFormat,
            own ? chart.CategoryAxisOptions : null, own ? chart.ValueAxisOptions : null, out var found)) return false;
        scale = found with { Horizontal = true };
        return true;
    }

    /// <summary>Lowest and highest bar end of the given series, including zero and the totals of stacked slots.</summary>
    private static bool TryBarRange(DrawingChart chart, double?[][] rows, int[] members, bool stacked,
        out double dataMinimum, out double dataMaximum)
    {
        var finite = members.SelectMany(index => rows[index]).OfType<double>().ToArray();
        dataMinimum = Math.Min(0, finite.DefaultIfEmpty(0).Min()); dataMaximum = Math.Max(0, finite.DefaultIfEmpty(0).Max());
        if (!stacked) return true;
        var slots = chart.Series.Max(series => series.PointCount);
        for (var i = 0; i < slots; i++)
        {
            var positive = 0d; var negative = 0d;
            foreach (var index in members)
                if (i < rows[index].Length && rows[index][i] is { } value)
                { positive += Math.Max(0, value); negative += Math.Min(0, value); }
            if (!double.IsFinite(positive) || !double.IsFinite(negative)) return false;
            dataMinimum = Math.Min(dataMinimum, negative); dataMaximum = Math.Max(dataMaximum, positive);
        }
        return true;
    }

    private static string? RenderBars(DrawingChart chart, double width, double height, Plot plot, ValueScale scale, bool stacked)
    {
        var all = Enumerable.Range(0, chart.Series.Count).ToArray();
        var rows = PlotValues(chart, all, chart.Grouping);
        var slots = chart.Series.Max(series => series.PointCount);
        var svg = Begin(chart, width, height, plot);
        WriteFrame(svg, chart, plot, scale);
        BeginPlotClip(svg, plot);
        var dataLabels = new List<string>();
        if (!WriteBars(svg, chart, plot, rows, all, slots, scale, stacked, dataLabels)) return null;
        svg.Append("</svg>");
        foreach (var label in dataLabels) svg.Append(label);
        return End(svg, chart, plot);
    }

    /// <summary>
    /// Draws the bars of the given series, which share the slots of the category axis among themselves. A bar
    /// starts where the category axis crosses the value axis. Horizontal bars have their first category at the
    /// bottom, as in Excel, unless the category axis is reversed.
    /// </summary>
    private static bool WriteBars(StringBuilder svg, DrawingChart chart, Plot plot, double?[][] rows, int[] members,
        int slots, ValueScale scale, bool stacked, List<string> dataLabels)
    {
        var (minimum, maximum) = (scale.Minimum, scale.Maximum);
        var horizontal = scale.Horizontal;
        var flip = horizontal != plot.Sides.ReverseCategories;
        var band = (horizontal ? plot.Height : plot.Width) / slots;
        var inner = band * .72;
        var step = stacked ? inner : inner / members.Length;
        var positiveOffsets = new double[slots]; var negativeOffsets = new double[slots];
        for (var member = 0; member < members.Length; member++)
        {
            var s = members[member];
            var series = chart.Series[s];
            var shadow = plot.Paints.Filter(series.Shadow);
            if (shadow.Length > 0) svg.Append("<g").Append(shadow).Append('>');
            for (var i = 0; i < rows[s].Length; i++)
            {
                if (rows[s][i] is not { } number) continue;
                var raw = series.Values[i]!.Value;
                var pointFill = Paint(chart, plot, s, i, chart.VaryColors == true && chart.Series.Count == 1 ? i : s);
                var from = stacked ? (number >= 0 ? positiveOffsets[i] : negativeOffsets[i]) : scale.Cross;
                var to = stacked ? from + number : number;
                if (!double.IsFinite(to)) return false;
                if (stacked) { if (number >= 0) positiveOffsets[i] = to; else negativeOffsets[i] = to; }
                var low = Math.Clamp(Math.Min(from, to), minimum, maximum);
                var high = Math.Clamp(Math.Max(from, to), minimum, maximum);
                var center = (horizontal ? plot.OriginY : plot.OriginX) + band * (flip ? slots - 1 - i : i) + band * .14;
                var offset = stacked ? 0 : (flip ? members.Length - 1 - member : member) * step;
                // The bar grows away from the axis: upwards or to the right, unless the value axis is reversed.
                var grows = (scale.ExplicitCross ? to >= from : number >= 0) != scale.Reversed;
                if (horizontal)
                {
                    var near = Math.Min(plot.X(low, scale), plot.X(high, scale));
                    var far = Math.Max(plot.X(low, scale), plot.X(high, scale));
                    Rect(svg, near, center + offset, far - near, step, pointFill, s, i, raw, Label(series, i));
                    if (ShowsValue(series, i) && dataLabels.Count < MaxDataLabels)
                        dataLabels.Add(DataLabel(chart, grows ? far + 3 : near - 3, center + offset + step / 2 + 3,
                            LabelNumber(chart, series, i, raw), grows ? "start" : "end"));
                }
                else
                {
                    var upper = Math.Min(plot.Y(high, scale), plot.Y(low, scale));
                    var lower = Math.Max(plot.Y(high, scale), plot.Y(low, scale));
                    Rect(svg, center + offset, upper, step, lower - upper, pointFill, s, i, raw, Label(series, i));
                    if (ShowsValue(series, i) && dataLabels.Count < MaxDataLabels)
                        dataLabels.Add(DataLabel(chart, center + offset + step / 2, grows ? upper - 3 : lower + 10,
                            LabelNumber(chart, series, i, raw), "middle"));
                }
            }
            if (shadow.Length > 0) svg.Append("</g>");
        }
        return true;
    }

    private static string? RenderStroke(DrawingChart chart, double width, double height, Plot plot, ValueScale scale, bool filled)
    {
        var slots = chart.Series.Max(series => series.PointCount);
        if (slots == 0) return null;
        var all = Enumerable.Range(0, chart.Series.Count).ToArray();
        var stack = chart.Grouping is "stacked" or "percentStacked" ? StrokeStack(chart, all, chart.Grouping == "percentStacked") : null;
        var svg = Begin(chart, width, height, plot);
        WriteFrame(svg, chart, plot, scale);
        BeginPlotClip(svg, plot);
        var dataLabels = new List<string>();
        for (var s = 0; s < chart.Series.Count; s++)
            WriteStroke(svg, chart, plot, s, slots, scale, filled, stack, dataLabels);

        svg.Append("</svg>");
        foreach (var label in dataLabels) svg.Append(label);
        return End(svg, chart, plot);
    }

    /// <summary>Running totals of stacked line or area series: where each point starts and ends on the value axis.</summary>
    private sealed record Stack(double?[][] Bottom, double?[][] Top);

    /// <summary>
    /// Stacks the series of one chart group as Excel does for stacked line and area charts: each series is plotted
    /// on top of the sum of the series before it, and a 100 % stack divides by the total of its category. A blank
    /// adds nothing. The rows of series outside the group stay empty.
    /// </summary>
    private static Stack? StrokeStack(DrawingChart chart, int[] members, bool percent)
    {
        var slots = chart.Series.Max(series => series.PointCount);
        double? Value(DrawingChartSeries series, int index) =>
            index < series.Values.Count && BlankValue(series.Values[index], chart.BlankDisplay) is { } value && double.IsFinite(value) ? value : null;
        var running = new double[slots];
        var largest = new double[slots]; var totals = new double[slots];
        if (percent)
            for (var i = 0; i < slots; i++)
            {
                // Scaled by the largest value first, so that a total beyond the range of a double cannot overflow.
                largest[i] = members.Max(index => Math.Abs(Value(chart.Series[index], i) ?? 0));
                totals[i] = largest[i] == 0 ? 0 : members.Sum(index => Math.Abs(Value(chart.Series[index], i) ?? 0) / largest[i]);
            }
        var bottom = new double?[chart.Series.Count][]; var top = new double?[chart.Series.Count][];
        for (var s = 0; s < chart.Series.Count; s++)
        {
            bottom[s] = new double?[slots]; top[s] = new double?[slots];
            if (!members.Contains(s)) continue;
            for (var i = 0; i < slots; i++)
            {
                if (Value(chart.Series[s], i) is not { } value) continue;
                bottom[s][i] = running[i];
                running[i] += percent ? totals[i] == 0 ? 0 : value / largest[i] / totals[i] : value;
                if (!double.IsFinite(running[i])) return null;
                top[s][i] = running[i];
            }
        }
        return new Stack(bottom, top);
    }

    /// <summary>
    /// Draws one line or area series with its markers; a stack moves it onto the series below. An area reaches to
    /// the category axis. A value a logarithmic axis cannot show is a gap.
    /// </summary>
    private static void WriteStroke(StringBuilder svg, DrawingChart chart, Plot plot, int s, int slots, ValueScale scale,
        bool filled, Stack? stack, List<string> dataLabels)
    {
        // An outer shadow of the series lies under its line, area and markers together.
        var shadow = plot.Paints.Filter(chart.Series[s].Shadow);
        if (shadow.Length > 0) svg.Append("<g").Append(shadow).Append('>');
        WriteStrokeMarks(svg, chart, plot, s, slots, scale, filled, stack, dataLabels);
        if (shadow.Length > 0) svg.Append("</g>");
    }

    private static void WriteStrokeMarks(StringBuilder svg, DrawingChart chart, Plot plot, int s, int slots, ValueScale scale,
        bool filled, Stack? stack, List<string> dataLabels)
    {
        var series = chart.Series[s];
        var fill = Fill(chart, s);
        var area = filled ? plot.Paints.Use(series.FillPaint, fill) : fill;
        // The categories of a line or area group run along the width of the plot, from the right on a reversed axis.
        var flip = plot.Sides.ReverseCategories && !chart.Horizontal;
        double X(int index) => plot.PointX(flip ? slots - 1 - index : index, slots);
        if (stack is null)
        {
            var segment = new List<string>();
            for (var i = 0; i < series.Values.Count; i++)
            {
                var number = BlankValue(series.Values[i], chart.BlankDisplay);
                if (number is null || !scale.Plots(number.Value))
                {
                    if (chart.BlankDisplay != "span") FlushStroke(svg, segment, plot.Y(scale.Cross, scale), fill, area, filled);
                    continue;
                }

                segment.Add(Num(X(i)) + "," + Num(plot.Y(number.Value, scale)));
            }

            FlushStroke(svg, segment, plot.Y(scale.Cross, scale), fill, area, filled);
        }
        else
        {
            var run = new List<int>();
            for (var i = 0; i <= slots; i++)
            {
                if (i < slots && stack.Top[s][i] is not null) { run.Add(i); continue; }
                if (i < slots && chart.BlankDisplay == "span") continue;
                if (run.Count > 1)
                {
                    string Points(double?[] row, IEnumerable<int> indexes) => string.Join(" ", indexes.Select(index =>
                        Num(X(index)) + "," + Num(plot.Y(row[index]!.Value, scale))));
                    var line = Points(stack.Top[s], run);
                    if (filled)
                        svg.Append("<polygon points=\"").Append(line).Append(' ')
                            .Append(Points(stack.Bottom[s], Enumerable.Reverse(run)))
                            .Append("\" fill=\"").Append(area).Append("\" fill-opacity=\"0.35\" stroke=\"none\"/>");
                    svg.Append("<polyline points=\"").Append(line).Append("\" fill=\"none\" stroke=\"").Append(fill)
                        .Append("\" stroke-width=\"1.6\"/>");
                }
                run.Clear();
            }
        }

        for (var i = 0; i < series.Values.Count; i++)
        {
            var number = BlankValue(series.Values[i], chart.BlankDisplay);
            if (number is null || !scale.Plots(number.Value)) continue;
            var y = plot.Y(stack is null ? number.Value : stack.Top[s][i]!.Value, scale);
            Circle(svg, X(i), y, 2.6, FillPoint(chart, s, i, s), s, i, number.Value,
                Label(series, i));
            if (ShowsValue(series, i) && dataLabels.Count < MaxDataLabels)
                dataLabels.Add(DataLabel(chart, X(i), y - 6,
                    LabelNumber(chart, series, i, number.Value), "middle"));
        }
    }

    /// <summary>
    /// The chart groups of a combination chart in one plot area. Excel layers the groups by type, not by their
    /// order in the file: areas at the back, then bars, then lines, then scatter points, and within a type the
    /// primary axis before the secondary one. A pie or doughnut group has no axes; it is drawn behind the others.
    /// </summary>
    private static string? RenderCombination(DrawingChart chart, double width, double height, Plot plot,
        ValueScale primary, ValueScale? secondary, ValueScale? xScale)
    {
        var slots = Slots(chart);
        if (slots == 0) return null;
        var groups = Groups(chart);
        var svg = Begin(chart, width, height, plot);
        WriteFrame(svg, chart, plot, primary);
        if (Xy(chart) && xScale is not null) WriteXTicks(svg, chart, plot, xScale);
        if (secondary is not null) WriteSecondaryAxis(svg, chart, plot, secondary);
        BeginPlotClip(svg, plot);
        var dataLabels = new List<string>();
        foreach (var group in groups.Where(group => group.Kind is "pie" or "doughnut"))
            if (!WriteRings(svg, chart, plot, group.Kind == "pie" ? group.Members[..1] : group.Members, group.Kind == "doughnut", dataLabels, out _))
                return null;
        foreach (var kind in new[] { "area", "bar", "line", "stock", "scatter" })
            foreach (var second in new[] { false, true })
                foreach (var group in groups.Where(group => group.Kind == kind && group.Secondary == second))
                {
                    if ((second ? secondary : primary) is not { } scale) continue;
                    if (kind == "bar")
                    {
                        if (!WriteBars(svg, chart, plot, PlotValues(chart, group.Members, group.Grouping), group.Members, slots, scale,
                            group.Stacked, dataLabels)) return null;
                    }
                    else if (kind == "scatter") WriteScatter(svg, chart, plot, group.Members, scale, xScale, slots, dataLabels);
                    else if (kind == "stock") WriteStock(svg, chart, plot, group.Members, slots, scale);
                    else
                    {
                        var stack = group.Stacked ? StrokeStack(chart, group.Members, group.Grouping == "percentStacked") : null;
                        if (group.Stacked && stack is null) return null;
                        foreach (var s in group.Members)
                            WriteStroke(svg, chart, plot, s, slots, scale, kind == "area", stack, dataLabels);
                    }
                }

        svg.Append("</svg>");
        foreach (var label in dataLabels) svg.Append(label);
        return End(svg, chart, plot);
    }

    /// <summary>The points of the given scatter series that have a finite x and y value.</summary>
    private static List<(int Index, double X, double Y)> ScatterPairs(DrawingChart chart, int[] members)
    {
        var pairs = new List<(int Index, double X, double Y)>();
        foreach (var series in members.Select(index => chart.Series[index]))
        {
            for (var i = 0; i < series.Values.Count; i++)
            {
                var y = series.Values[i];
                if (y is null || !double.IsFinite(y.Value)) continue;
                var x = i < series.XValues.Count ? series.XValues[i] : null;
                if (x is null || !double.IsFinite(x.Value)) continue;
                pairs.Add((i, x.Value, y.Value));
            }
        }
        return pairs;
    }

    private static string? RenderScatter(DrawingChart chart, double width, double height, Plot plot, ValueScale scale, ValueScale xScale)
    {
        var svg = Begin(chart, width, height, plot);
        WriteFrame(svg, chart, plot, scale);
        WriteXTicks(svg, chart, plot, xScale);
        BeginPlotClip(svg, plot);
        var dataLabels = new List<string>();
        WriteScatter(svg, chart, plot, Enumerable.Range(0, chart.Series.Count).ToArray(), scale, xScale, 0, dataLabels);
        svg.Append("</svg>");
        foreach (var label in dataLabels) svg.Append(label);
        return End(svg, chart, plot);
    }

    /// <summary>The tick labels of the horizontal value axis of a scatter chart, below the plot or above it.</summary>
    private static void WriteXTicks(StringBuilder svg, DrawingChart chart, Plot plot, ValueScale xScale)
    {
        if (xScale.Deleted) return;
        var ink = Ink(chart, xScale.TextColor);
        foreach (var (value, share) in xScale.Ticks())
            Text(svg, "x-tick", plot.OriginX + plot.Width * share, plot.Sides.CategoryFar ? plot.OriginY - 5 : plot.OriginY + plot.Height + 14,
                AxisLabel(value, false, xScale.Format, chart.Date1904), 9, "middle", ink);
    }

    /// <summary>
    /// Draws the markers of scatter series. Without a horizontal value axis of their own (a scatter group on the
    /// category axis of a combination chart) an x value of 1 lies at the middle of the first category.
    /// </summary>
    private static void WriteScatter(StringBuilder svg, DrawingChart chart, Plot plot, int[] members, ValueScale scale,
        ValueScale? xScale, int slots, List<string> dataLabels)
    {
        var largest = BubbleLargest(chart, members);
        foreach (var s in members)
        {
            var series = chart.Series[s];
            var bubble = Kind(chart, series) == "bubble";
            var shadow = plot.Paints.Filter(series.Shadow);
            if (shadow.Length > 0) svg.Append("<g").Append(shadow).Append('>');
            for (var i = 0; i < series.Values.Count; i++)
            {
                var y = series.Values[i];
                if (y is null || !scale.Plots(y.Value)) continue;
                var x = i < series.XValues.Count ? series.XValues[i] : null;
                if (x is null || !double.IsFinite(x.Value) || xScale is not null && !xScale.Plots(x.Value)) continue;
                var radius = 3.2;
                var fill = FillPoint(chart, s, i, s);
                var extra = $" data-x-value=\"{Data(x.Value)}\"";
                if (bubble)
                {
                    // The area of a bubble (or its width, c:sizeRepresents w) is its share of the largest bubble,
                    // whose diameter is a quarter of the shorter side of the plot at a bubble scale of 100 %.
                    if (BubbleSize(chart, series, i) is not { } size || largest <= 0) continue;
                    var share = Math.Abs(size) / largest;
                    radius = Math.Min(plot.Width, plot.Height) * .125 * Math.Clamp(chart.BubbleScale ?? 100, 0, 300) / 100 *
                        (chart.BubbleSizeIsWidth ? share : Math.Sqrt(share));
                    extra += $" data-bubble-size=\"{Data(size)}\"";
                    // Excel leaves a negative bubble unfilled.
                    if (size < 0) (fill, extra) = ("#ffffff", extra + $" data-bubble-negative=\"true\" stroke=\"{fill}\" stroke-width=\"1\"");
                    else fill = Paint(chart, plot, s, i, s);
                }
                double cx;
                if (xScale is not null) cx = plot.X(x.Value, xScale);
                else
                {
                    var share = (x.Value - .5) / Math.Max(1, slots);
                    cx = plot.OriginX + plot.Width * (plot.Sides.ReverseCategories ? 1 - share : share);
                }
                Circle(svg, cx, plot.Y(y.Value, scale), radius, fill, s, i, y.Value, Label(series, i), Extra: extra);
                if (ShowsValue(series, i))
                    dataLabels.Add(DataLabel(chart, cx, plot.Y(y.Value, scale) - (bubble ? radius + 3 : 6), LabelNumber(chart, series, i, y.Value), "middle"));
            }
            if (shadow.Length > 0) svg.Append("</g>");
        }
    }

    private static string? RenderPie(DrawingChart chart, double width, double height, Plot plot, bool doughnut)
    {
        var cx = plot.OriginX + plot.Width / 2;
        var cy = plot.OriginY + plot.Height / 2;
        var svg = Begin(chart, width, height, plot);
        svg.Append("<rect data-chart-plot=\"true\" x=\"").Append(Num(plot.OriginX)).Append("\" y=\"").Append(Num(plot.OriginY))
            .Append("\" width=\"").Append(Num(plot.Width)).Append("\" height=\"").Append(Num(plot.Height)).Append("\" fill=\"")
            .Append(plot.Paints.Use(chart.PlotFillPaint, Hex(chart.PlotFill) ? chart.PlotFill! : "none")).Append("\" stroke=\"none\"")
            .Append(plot.Paints.Filter(chart.PlotShadow)).Append("/>");
        var dataLabels = new List<string>();
        var any = false;
        var groups = chart.Series.Any(series => series.Plot is not null) ? Groups(chart).Where(group => group.Kind is "pie" or "doughnut").ToArray() : [];
        if (groups.Length > 1)
        {
            // Several pie or doughnut groups. A pie and a doughnut share the centre, the pie under the rings, as
            // Excel layers them; groups of one kind would hide each other there and are drawn side by side.
            var concentric = groups.Select(group => group.Kind).Distinct().Count() > 1;
            var ordered = concentric ? groups.OrderBy(group => group.Kind == "doughnut").ToArray() : groups;
            for (var position = 0; position < ordered.Length; position++)
            {
                var group = ordered[position];
                var area = concentric ? plot : plot with { Width = plot.Width / ordered.Length, OriginX = plot.OriginX + plot.Width * position / ordered.Length };
                svg.Append("<g data-chart-pie-group=\"").Append(position.ToString(CultureInfo.InvariantCulture)).Append("\">");
                if (!WriteRings(svg, chart, area, group.Kind == "pie" ? group.Members[..1] : group.Members, group.Kind == "doughnut", dataLabels, out var some))
                    return null;
                svg.Append("</g>");
                any |= some;
            }
        }
        // A doughnut draws one ring for each series, the first series innermost; a pie plots its first series.
        else if (!WriteRings(svg, chart, plot, Enumerable.Range(0, doughnut ? chart.Series.Count : 1).ToArray(), doughnut, dataLabels, out any))
            return null;

        // Nothing to plot (every value zero or blank): Excel leaves the plot empty; the legend stays.
        if (!any) Text(svg, "note", cx, cy + 4, "No nonzero values", 11, "middle", Ink(chart, null, onPlot: true));
        foreach (var label in dataLabels) svg.Append(label);
        return End(svg, chart, plot);
    }

    /// <summary>
    /// Draws the given series as the rings of a pie or doughnut in the middle of the plot area. Excel plots a
    /// negative value as its absolute value and keeps the sign in the label.
    /// </summary>
    private static bool WriteRings(StringBuilder svg, DrawingChart chart, Plot plot, int[] members, bool doughnut,
        List<string> dataLabels, out bool any)
    {
        any = false;
        var rings = new List<(int Series, List<(int Index, double Value, double Size)> Slices, double Total)>();
        foreach (var s in members)
        {
            var slices = new List<(int Index, double Value, double Size)>();
            for (var i = 0; i < chart.Series[s].Values.Count; i++)
            {
                var value = chart.Series[s].Values[i];
                if (value is null || !double.IsFinite(value.Value) || value.Value == 0) continue;
                slices.Add((i, value.Value, Math.Abs(value.Value)));
            }

            if (slices.Count > MaxPoints) return false;
            var total = slices.Sum(slice => slice.Size);
            if (!double.IsFinite(total)) return false;
            rings.Add((s, slices, total));
        }

        var diameter = Math.Min(plot.Width, plot.Height) * .9;
        if (diameter < MinPieDiameter) return false;
        var radius = diameter / 2;
        var hole = chart.DoughnutHolePercent ?? 55;
        var firstAngle = chart.FirstSliceAngleDegrees ?? 0;
        if (!double.IsFinite(hole) || hole is < 10 or > 90 || !double.IsFinite(firstAngle) || firstAngle is < 0 or > 360) return false;
        var inner = doughnut ? radius * hole / 100 : 0d;
        var ringWidth = (radius - inner) / rings.Count;
        var cx = plot.OriginX + plot.Width / 2;
        var cy = plot.OriginY + plot.Height / 2;
        for (var position = 0; position < rings.Count; position++)
        {
            var ring = rings[position];
            var series = chart.Series[ring.Series];
            var ringInner = inner + ringWidth * position;
            var ringOuter = position == rings.Count - 1 ? radius : ringInner + ringWidth;
            var angle = -Math.PI / 2 + firstAngle * Math.PI / 180;
            var shadow = plot.Paints.Filter(series.Shadow);
            if (shadow.Length > 0) svg.Append("<g").Append(shadow).Append('>');
            foreach (var slice in ring.Slices)
            {
                var sweep = slice.Size / ring.Total * Math.PI * 2;
                var start = angle;
                var end = angle + sweep;
                angle = end;
                var label = Label(series, slice.Index);
                svg.Append("<path d=\"").Append(Arc(cx, cy, ringOuter, ringInner, start, end)).Append("\" fill=\"")
                    .Append(Paint(chart, plot, ring.Series, slice.Index, chart.VaryColors == false ? ring.Series : slice.Index)).Append("\" stroke=\"#ffffff\" stroke-width=\"0.6\"")
                    .Append(" data-series-index=\"").Append(ring.Series.ToString(CultureInfo.InvariantCulture)).Append("\" data-point-index=\"").Append(slice.Index.ToString(CultureInfo.InvariantCulture))
                    .Append("\" data-value=\"").Append(Data(slice.Value)).Append("\">")
                    .Append("<title>").Append(Esc($"{label}: {Data(slice.Value)}")).Append("</title></path>");
                var showPercent = ShowsPercent(series, slice.Index);
                var showValues = ShowsValue(series, slice.Index);
                if ((showPercent || showValues) && dataLabels.Count < MaxDataLabels)
                {
                    var mid = (start + end) / 2;
                    var share = slice.Size / ring.Total;
                    // A percentage linked to its source has no source format: Excel shows it as "0%".
                    var percentage = Formatted(share, PointLabel(series, slice.Index)?.PercentFormat ?? series.PercentLabelFormat, chart.Date1904) ??
                        Formatted(share, "0%", chart.Date1904) ??
                        (share * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
                    var number = LabelNumber(chart, series, slice.Index, slice.Value);
                    var text = showPercent ? showValues ? number + " (" + percentage + ")" : percentage : number;
                    dataLabels.Add(DataLabel(chart, cx + Math.Cos(mid) * (ringOuter + ringInner) / 2,
                        cy + Math.Sin(mid) * (ringOuter + ringInner) / 2 + 3, text, "middle"));
                }
            }
            if (shadow.Length > 0) svg.Append("</g>");
        }

        any = rings.Any(ring => ring.Total > 0);
        return true;
    }

    private static string Arc(double cx, double cy, double radius, double inner, double start, double end)
    {
        var large = end - start > Math.PI ? 1 : 0;
        var (x0, y0) = Polar(cx, cy, radius, start);
        var (x1, y1) = Polar(cx, cy, radius, end);
        if (end - start >= Math.PI * 2 - 1e-10)
        {
            var (xm, ym) = Polar(cx, cy, radius, start + Math.PI);
            var outside = $"M {Num(x0)} {Num(y0)} A {Num(radius)} {Num(radius)} 0 0 1 {Num(xm)} {Num(ym)} A {Num(radius)} {Num(radius)} 0 0 1 {Num(x0)} {Num(y0)} Z";
            if (inner <= 0) return outside;
            var (ix, iy) = Polar(cx, cy, inner, start); var (imx, imy) = Polar(cx, cy, inner, start + Math.PI);
            return outside + $" M {Num(ix)} {Num(iy)} A {Num(inner)} {Num(inner)} 0 0 0 {Num(imx)} {Num(imy)} A {Num(inner)} {Num(inner)} 0 0 0 {Num(ix)} {Num(iy)} Z";
        }
        if (inner <= 0)
            return $"M {Num(cx)} {Num(cy)} L {Num(x0)} {Num(y0)} A {Num(radius)} {Num(radius)} 0 {large} 1 {Num(x1)} {Num(y1)} Z";
        var (x2, y2) = Polar(cx, cy, inner, end);
        var (x3, y3) = Polar(cx, cy, inner, start);
        return $"M {Num(x0)} {Num(y0)} A {Num(radius)} {Num(radius)} 0 {large} 1 {Num(x1)} {Num(y1)} " +
            $"L {Num(x2)} {Num(y2)} A {Num(inner)} {Num(inner)} 0 {large} 0 {Num(x3)} {Num(y3)} Z";
    }

    private static (double X, double Y) Polar(double cx, double cy, double radius, double angle) =>
        (cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));

    private static void FlushStroke(StringBuilder svg, List<string> segment, double baseY, string stroke, string fill, bool filled)
    {
        if (segment.Count > 1)
        {
            var points = string.Join(" ", segment);
            if (filled)
            {
                var first = segment[0].Split(',');
                var last = segment[^1].Split(',');
                svg.Append("<polygon points=\"").Append(Num(double.Parse(first[0], CultureInfo.InvariantCulture)))
                    .Append(' ').Append(Num(baseY)).Append(' ').Append(points).Append(' ')
                    .Append(Num(double.Parse(last[0], CultureInfo.InvariantCulture))).Append(' ').Append(Num(baseY))
                    .Append("\" fill=\"").Append(fill).Append("\" fill-opacity=\"0.35\" stroke=\"none\"/>");
            }

            svg.Append("<polyline points=\"").Append(points).Append("\" fill=\"none\" stroke=\"").Append(stroke)
                .Append("\" stroke-width=\"1.6\"/>");
        }

        segment.Clear();
    }

    private static void Rect(StringBuilder svg, double x, double y, double w, double h, string fill, int seriesIndex,
        int pointIndex, double value, string label) =>
        svg.Append("<rect x=\"").Append(Num(x)).Append("\" y=\"").Append(Num(y)).Append("\" width=\"").Append(Num(w))
            .Append("\" height=\"").Append(Num(h)).Append("\" fill=\"").Append(fill).Append("\"")
            .Append(Point(seriesIndex, pointIndex, value)).Append("><title>").Append(Esc($"{label}: {Data(value)}"))
            .Append("</title></rect>");

    private static void Circle(StringBuilder svg, double cx, double cy, double radius, string fill, int seriesIndex,
        int pointIndex, double value, string label, string? Extra = null) =>
        svg.Append("<circle cx=\"").Append(Num(cx)).Append("\" cy=\"").Append(Num(cy)).Append("\" r=\"").Append(Num(radius))
            .Append("\" fill=\"").Append(fill).Append('"').Append(Point(seriesIndex, pointIndex, value))
            .Append(Extra).Append("><title>").Append(Esc($"{label}: {Data(value)}")).Append("</title></circle>");

    private static StringBuilder Begin(DrawingChart chart, double width, double height, Plot plot)
    {
        var svg = new StringBuilder();
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(Num(width)).Append("\" height=\"")
            .Append(Num(height)).Append("\" viewBox=\"0 0 ").Append(Num(width)).Append(' ').Append(Num(height))
            .Append("\" font-family=\"Arial,sans-serif\" data-chart-type=\"").Append(Esc(chart.Type)).Append("\">")
            .Append("<title>").Append(Esc(string.IsNullOrWhiteSpace(chart.Title) ? chart.Type + " chart" : chart.Title))
            .Append("</title><desc>").Append(Esc(string.Join("; ", chart.Series.Select(series => series.Name))))
            .Append("</desc>");
        plot.Paints.DefinitionsAt = svg.Length;
        if (Hex(chart.ChartFill))
            svg.Append("<rect data-chart-background=\"true\" x=\"0\" y=\"0\" width=\"").Append(Num(width)).Append("\" height=\"")
                .Append(Num(height)).Append("\" fill=\"").Append(plot.Paints.Use(chart.ChartFillPaint, chart.ChartFill!)).Append("\"/>");
        WriteTitle(svg, chart, plot);
        return svg;
    }

    private static string End(StringBuilder svg, DrawingChart chart, Plot plot)
    {
        WriteLabels(svg, chart, plot);
        WriteValueAxisTitle(svg, chart, plot);
        svg.Append("</svg>");
        if (plot.Paints.Definitions.Length > 0)
            svg.Insert(plot.Paints.DefinitionsAt, "<defs>" + plot.Paints.Definitions + "</defs>");
        return svg.ToString();
    }

    /// <summary>
    /// The plot rectangle, the grid lines and tick labels of the value axis, and the line of the category axis
    /// where it crosses the value axis. A deleted axis (c:delete) keeps its grid lines and has no labels.
    /// </summary>
    private static void WriteFrame(StringBuilder svg, DrawingChart chart, Plot plot, ValueScale scale)
    {
        var horizontal = scale.Horizontal;
        var far = plot.Sides.ValueFar;
        svg.Append("<rect data-chart-plot=\"true\" data-axis-min=\"").Append(Data(scale.Minimum))
            .Append("\" data-axis-max=\"").Append(Data(scale.Maximum)).Append("\" x=\"").Append(Num(plot.OriginX)).Append("\" y=\"").Append(Num(plot.OriginY))
            .Append("\" width=\"").Append(Num(plot.Width)).Append("\" height=\"").Append(Num(plot.Height))
            .Append("\" fill=\"").Append(plot.Paints.Use(chart.PlotFillPaint, chart.PlotFill == "none" ? "none" : Hex(chart.PlotFill) ? chart.PlotFill! : "#ffffff"))
            .Append("\" stroke=\"#cccccc\" stroke-width=\"0.8\"").Append(plot.Paints.Filter(chart.PlotShadow)).Append("/>");
        var ticks = scale.Ticks();
        var labels = TickLabels(scale, chart.Date1904);
        var ink = Ink(chart, scale.TextColor, fallback: "#444");
        for (var tick = 0; tick < ticks.Length; tick++)
        {
            if (horizontal)
            {
                var x = plot.OriginX + plot.Width * ticks[tick].Share;
                svg.Append("<line x1=\"").Append(Num(x)).Append("\" y1=\"").Append(Num(plot.OriginY))
                    .Append("\" x2=\"").Append(Num(x)).Append("\" y2=\"").Append(Num(plot.OriginY + plot.Height))
                    .Append("\" stroke=\"#e6e6e6\"/>");
                if (!scale.Deleted)
                    svg.Append("<text x=\"").Append(Num(x)).Append("\" y=\"")
                        .Append(Num(far ? plot.OriginY - 5 : plot.OriginY + plot.Height + 12)).Append("\" font-size=\"9\" fill=\"").Append(ink).Append("\" text-anchor=\"middle\">")
                        .Append(Esc(labels[tick])).Append("</text>");
            }
            else
            {
                var y = plot.Y(ticks[tick].Value, scale);
                svg.Append("<line x1=\"").Append(Num(plot.OriginX)).Append("\" y1=\"").Append(Num(y))
                    .Append("\" x2=\"").Append(Num(plot.OriginX + plot.Width)).Append("\" y2=\"").Append(Num(y))
                    .Append("\" stroke=\"#e6e6e6\"/>");
                if (!scale.Deleted)
                    svg.Append("<text x=\"").Append(Num(far ? plot.OriginX + plot.Width + 4 : plot.OriginX - 4))
                        .Append("\" y=\"").Append(Num(y + 3)).Append("\" font-size=\"9\" fill=\"").Append(ink).Append("\" text-anchor=\"").Append(far ? "start" : "end").Append("\">")
                        .Append(Esc(FitWidth(labels[tick], (far ? plot.Decoration?.SecondaryTickWidth : plot.Decoration?.TickWidth) ?? double.MaxValue, 9))).Append("</text>");
            }
        }

        if (horizontal)
            svg.Append("<line data-chart-baseline=\"true\" x1=\"").Append(Num(plot.X(scale.Cross, scale)))
                .Append("\" y1=\"").Append(Num(plot.OriginY)).Append("\" x2=\"").Append(Num(plot.X(scale.Cross, scale)))
                .Append("\" y2=\"").Append(Num(plot.OriginY + plot.Height)).Append("\" stroke=\"#666666\"/>");
        else
            svg.Append("<line data-chart-baseline=\"true\" x1=\"").Append(Num(plot.OriginX))
                .Append("\" y1=\"").Append(Num(plot.Y(scale.Cross, scale))).Append("\" x2=\"").Append(Num(plot.OriginX + plot.Width))
                .Append("\" y2=\"").Append(Num(plot.Y(scale.Cross, scale))).Append("\" stroke=\"#666666\"/>");
    }

    /// <summary>
    /// The tick labels of the secondary value axis: opposite the primary value axis, or, when the two axes run
    /// in different directions (horizontal bars with lines), on the right or above the plot.
    /// </summary>
    private static void WriteSecondaryAxis(StringBuilder svg, DrawingChart chart, Plot plot, ValueScale scale)
    {
        svg.Append("<g data-chart-secondary-axis=\"true\" data-axis-min=\"").Append(Data(scale.Minimum))
            .Append("\" data-axis-max=\"").Append(Data(scale.Maximum)).Append("\">");
        var labels = scale.Deleted ? [] : TickLabels(scale, chart.Date1904);
        var ticks = scale.Ticks();
        var far = plot.Sides.SecondaryFar;
        var ink = Ink(chart, scale.TextColor);
        for (var tick = 0; tick < labels.Length; tick++)
        {
            if (scale.Horizontal)
                Text(svg, "secondary-tick", plot.X(ticks[tick].Value, scale), far ? plot.OriginY - 5 : plot.OriginY + plot.Height + 12,
                    labels[tick], 9, "middle", ink);
            else if (far)
                Text(svg, "secondary-tick", plot.OriginX + plot.Width + 4, plot.Y(ticks[tick].Value, scale) + 3,
                    FitWidth(labels[tick], plot.Decoration?.SecondaryTickWidth ?? double.MaxValue, 9), 9, "start", ink);
            else
                Text(svg, "secondary-tick", plot.OriginX - 4, plot.Y(ticks[tick].Value, scale) + 3,
                    FitWidth(labels[tick], plot.Decoration?.TickWidth ?? double.MaxValue, 9), 9, "end", ink);
        }
        svg.Append("</g>");
    }

    private static string Point(int seriesIndex, int pointIndex, double value) =>
        " data-series-index=\"" + seriesIndex.ToString(CultureInfo.InvariantCulture) + "\" data-point-index=\"" +
        pointIndex.ToString(CultureInfo.InvariantCulture) + "\" data-value=\"" + Data(value) + "\"";

    private static bool Hex(string? color) => color is { Length: 7 } && color[0] == '#' &&
        int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);

    private static string Fill(DrawingChart chart, int index)
    {
        var series = index < chart.Series.Count ? chart.Series[index].Color : null;
        if (series is not null && series.Length == 7 && series[0] == '#' &&
            int.TryParse(series.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            return series;
        return Palette[index % Palette.Length];
    }

    /// <summary>
    /// The fill of a bar, a slice or a legend swatch: the gradient or pattern of the point or of its series where
    /// the chart can define one, else the color.
    /// </summary>
    private static string Paint(DrawingChart chart, Plot plot, int seriesIndex, int pointIndex, int fallbackIndex)
    {
        var color = FillPoint(chart, seriesIndex, pointIndex, fallbackIndex);
        var series = chart.Series[seriesIndex];
        if (series.PointFillPaints is { Count: > 0 } points && points.TryGetValue(pointIndex, out var own)) return plot.Paints.Use(own, color);
        if (series.PointColors is { } colors && colors.ContainsKey(pointIndex)) return color;
        return plot.Paints.Use(fallbackIndex < chart.Series.Count ? chart.Series[fallbackIndex].FillPaint : null, color);
    }

    /// <summary>
    /// The color of text: its own, else the text color of the chart, else dark, or light where the background
    /// behind it (the plot area or the chart area) is dark.
    /// </summary>
    private static string Ink(DrawingChart chart, string? own, bool onPlot = false, string fallback = "#222")
    {
        if (Hex(own)) return own!;
        if (Hex(chart.TextColor)) return chart.TextColor!;
        var background = !onPlot || chart.PlotFill == "none" ? chart.ChartFill : chart.PlotFill;
        return Hex(background) && IsDark(background!) ? "#F2F2F2" : fallback;
    }

    /// <summary>WCAG relative luminance below 0.25: dark text has less than a contrast of 4.5:1 on such a color.</summary>
    private static bool IsDark(string color)
    {
        double Channel(int offset)
        {
            var value = int.Parse(color.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            return value <= .03928 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        return .2126 * Channel(1) + .7152 * Channel(3) + .0722 * Channel(5) < .25;
    }

    /// <summary>
    /// The gradients, patterns, picture fills and shadow filters one chart defines. Their ids start with the prefix
    /// of the caller, which keeps them unique in the document; without a prefix nothing is defined: a paint falls
    /// back to its color and a shadow is not drawn.
    /// </summary>
    private sealed class Paints(string? prefix)
    {
        private readonly string? _prefix = prefix is { Length: > 0 and <= 128 } &&
            prefix.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':') ? prefix : null;
        private readonly Dictionary<DrawingPaint, string> _references = new(ReferenceEqualityComparer.Instance);

        private readonly Dictionary<DrawingChartShadow, string> _filters = [];

        public StringBuilder Definitions { get; } = new();

        /// <summary>Size of the chart, which bounds the region of a shadow filter.</summary>
        public double Width { get; init; }
        public double Height { get; init; }

        /// <summary>
        /// The filter attribute of an outer shadow (a:outerShdw): the marks are drawn again, moved by the offset,
        /// blurred and in the color of the shadow, under themselves. Empty when there is none to draw.
        /// </summary>
        public string Filter(DrawingChartShadow? shadow)
        {
            if (_prefix is null || shadow is null || !(Width > 0) || !(Height > 0) || !Hex(shadow.Color) ||
                !double.IsFinite(shadow.OffsetX) || !double.IsFinite(shadow.OffsetY) || !double.IsFinite(shadow.Blur) || !double.IsFinite(shadow.Opacity)) return "";
            if (!_filters.TryGetValue(shadow, out var id))
            {
                _filters[shadow] = id = _prefix + "-shadow-" + _filters.Count.ToString(CultureInfo.InvariantCulture);
                // The region is the chart itself, so that a horizontal or vertical line (a box without area) has a shadow too.
                Definitions.Append("<filter id=\"").Append(id).Append("\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"").Append(Num(Width))
                    .Append("\" height=\"").Append(Num(Height)).Append("\"><feDropShadow dx=\"").Append(Num(Math.Clamp(shadow.OffsetX, -1000, 1000)))
                    .Append("\" dy=\"").Append(Num(Math.Clamp(shadow.OffsetY, -1000, 1000)))
                    // A Gaussian blur reaches about two standard deviations: half the blur radius.
                    .Append("\" stdDeviation=\"").Append(Num(Math.Clamp(shadow.Blur, 0, 1000) / 2))
                    .Append("\" flood-color=\"").Append(shadow.Color).Append("\" flood-opacity=\"").Append(Num(Math.Clamp(shadow.Opacity, 0, 1)))
                    .Append("\"/></filter>");
            }
            return " filter=\"url(#" + id + ")\"";
        }

        /// <summary>
        /// A picture fill (a:blipFill) as a pattern. Stretched: the part a:srcRect keeps fills the rectangle
        /// a:fillRect insets from the box of each filled mark. Tiled: the picture repeats at its pixel size times sx
        /// and sy from the origin of the chart, moved by tx and ty; algn and flip are not applied.
        /// </summary>
        private bool Picture(DrawingPaint paint, string id)
        {
            if (paint.ImageReference is not { Length: > 0 } uri || !SafeImage(uri)) return false;
            var opacity = Num(double.IsFinite(paint.Opacity) ? Math.Clamp(paint.Opacity, 0, 1) : 1);
            static bool Usable(DrawingInsets? insets) => insets is not null &&
                new[] { insets.Left, insets.Top, insets.Right, insets.Bottom }.All(value => double.IsFinite(value) && Math.Abs(value) <= 1000) &&
                1 - insets.Left - insets.Right > 1e-6 && 1 - insets.Top - insets.Bottom > 1e-6;
            if (paint.Tile is { } tile && PixelSize(uri) is { } pixels)
            {
                var tileWidth = pixels.Width * Math.Abs(tile.ScaleX); var tileHeight = pixels.Height * Math.Abs(tile.ScaleY);
                if (double.IsFinite(tileWidth) && double.IsFinite(tileHeight) && tileWidth is >= .01 and <= 1e6 && tileHeight is >= .01 and <= 1e6 &&
                    double.IsFinite(tile.OffsetX) && double.IsFinite(tile.OffsetY) && Math.Abs(tile.OffsetX) <= 1e8 && Math.Abs(tile.OffsetY) <= 1e8)
                {
                    Definitions.Append("<pattern id=\"").Append(id).Append("\" patternUnits=\"userSpaceOnUse\" x=\"").Append(Num(tile.OffsetX % tileWidth))
                        .Append("\" y=\"").Append(Num(tile.OffsetY % tileHeight)).Append("\" width=\"").Append(Num(tileWidth)).Append("\" height=\"").Append(Num(tileHeight))
                        .Append("\"><image width=\"").Append(Num(tileWidth)).Append("\" height=\"").Append(Num(tileHeight))
                        .Append("\" preserveAspectRatio=\"none\" opacity=\"").Append(opacity).Append("\" href=\"").Append(uri).Append("\"/></pattern>");
                    return true;
                }
            }
            // In units of the box of the filled mark: the picture is scaled so that the part it keeps fills the rectangle.
            var stretch = Usable(paint.Stretch) ? paint.Stretch! : new DrawingInsets();
            var crop = Usable(paint.Crop) ? paint.Crop! : new DrawingInsets();
            var boxWidth = 1 - stretch.Left - stretch.Right; var boxHeight = 1 - stretch.Top - stretch.Bottom;
            var keptWidth = 1 - crop.Left - crop.Right; var keptHeight = 1 - crop.Top - crop.Bottom;
            Definitions.Append("<pattern id=\"").Append(id).Append("\" patternContentUnits=\"objectBoundingBox\" width=\"1\" height=\"1\"><image x=\"")
                .Append(Num(stretch.Left - crop.Left * boxWidth / keptWidth)).Append("\" y=\"").Append(Num(stretch.Top - crop.Top * boxHeight / keptHeight))
                .Append("\" width=\"").Append(Num(boxWidth / keptWidth)).Append("\" height=\"").Append(Num(boxHeight / keptHeight))
                .Append("\" preserveAspectRatio=\"none\" opacity=\"").Append(opacity).Append("\" href=\"").Append(uri).Append("\"/></pattern>");
            return true;
        }

        /// <summary>A base64 data: URI of a PNG, JPEG, GIF or WebP picture, with nothing that could leave the attribute.</summary>
        private static bool SafeImage(string uri)
        {
            string[] prefixes = ["data:image/png;base64,", "data:image/jpeg;base64,", "data:image/gif;base64,", "data:image/webp;base64,"];
            var prefix = prefixes.FirstOrDefault(candidate => uri.StartsWith(candidate, StringComparison.Ordinal));
            if (prefix is null || uri.Length == prefix.Length) return false;
            for (var index = prefix.Length; index < uri.Length; index++)
                if (!(char.IsAsciiLetterOrDigit(uri[index]) || uri[index] is '+' or '/' or '=')) return false;
            return true;
        }

        /// <summary>The pixel size of the picture of a data: URI, read from its header.</summary>
        private static (int Width, int Height)? PixelSize(string uri)
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]); }
            catch (FormatException) { return null; }
            var size = (Width: 0, Height: 0);
            // PNG: the IHDR chunk follows the signature.
            if (bytes.Length >= 24 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
                size = (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20)));
            else if (SourcePictureCropWindow.TryReadPixelSize(bytes, out var width, out var height)) size = (width, height);
            return size.Width is <= 0 or > 1_000_000 || size.Height is <= 0 or > 1_000_000 ? null : size;
        }

        /// <summary>Where the definitions belong in the SVG: before the first element that may use one.</summary>
        public int DefinitionsAt { get; set; }

        public string Use(DrawingPaint? paint, string fallback)
        {
            if (_prefix is null || paint is null) return fallback;
            if (_references.TryGetValue(paint, out var known)) return known;
            var id = _prefix + "-" + _references.Count.ToString(CultureInfo.InvariantCulture);
            var stops = paint.Stops ?? [];
            if (paint.Kind is "linear" or "radial" && stops.Count is > 0 and <= 256 && stops.All(stop => stop is not null))
            {
                if (paint.Kind == "linear")
                {
                    // In units of the filled element, as DrawingML scales a gradient to the shape it fills: the
                    // line runs through the middle and reaches the edges of the box.
                    var radians = (double.IsFinite(paint.AngleDegrees) ? paint.AngleDegrees : 0) * Math.PI / 180;
                    var dx = Math.Cos(radians); var dy = Math.Sin(radians);
                    var half = (Math.Abs(dx) + Math.Abs(dy)) / 2;
                    Definitions.Append("<linearGradient id=\"").Append(id).Append("\" x1=\"").Append(Num(.5 - dx * half)).Append("\" y1=\"")
                        .Append(Num(.5 - dy * half)).Append("\" x2=\"").Append(Num(.5 + dx * half)).Append("\" y2=\"").Append(Num(.5 + dy * half)).Append("\">");
                }
                else
                {
                    // A path gradient starts at the centre of a:fillToRect and ends at the farthest corner.
                    var focus = paint.Focus ?? new(.5, .5, .5, .5);
                    static double Centre(double near, double far) => double.IsFinite(near) && double.IsFinite(far) ? Math.Clamp((near + 1 - far) / 2, -10, 11) : .5;
                    var cx = Centre(focus.Left, focus.Right); var cy = Centre(focus.Top, focus.Bottom);
                    Definitions.Append("<radialGradient id=\"").Append(id).Append("\" cx=\"").Append(Num(cx)).Append("\" cy=\"").Append(Num(cy)).Append("\" r=\"")
                        .Append(Num(double.Hypot(Math.Max(Math.Abs(cx), Math.Abs(1 - cx)), Math.Max(Math.Abs(cy), Math.Abs(1 - cy))))).Append("\">");
                }
                foreach (var stop in stops)
                    Definitions.Append("<stop offset=\"").Append(Num(double.IsFinite(stop.Position) ? Math.Clamp(stop.Position, 0, 1) : 0)).Append("\" stop-color=\"")
                        .Append(Hex(stop.Color) ? stop.Color : "#000000").Append("\" stop-opacity=\"")
                        .Append(Num(double.IsFinite(stop.Opacity) ? Math.Clamp(stop.Opacity, 0, 1) : 1)).Append("\"/>");
                Definitions.Append(paint.Kind == "linear" ? "</linearGradient>" : "</radialGradient>");
            }
            else if (paint.Kind == "pattern" && DrawingPatternPresets.IsKnown(paint.Pattern))
            {
                // The 8x8 bitmap of the preset, one bitmap pixel per CSS pixel, as the fill of a shape is drawn.
                Definitions.Append("<pattern id=\"").Append(id).Append("\" patternUnits=\"userSpaceOnUse\" width=\"8\" height=\"8\" shape-rendering=\"crispEdges\">")
                    .Append("<rect width=\"8\" height=\"8\" fill=\"").Append(Hex(paint.BackColor) ? paint.BackColor : "#FFFFFF").Append("\" fill-opacity=\"")
                    .Append(Num(double.IsFinite(paint.BackOpacity) ? Math.Clamp(paint.BackOpacity, 0, 1) : 1)).Append("\"/><path d=\"");
                for (var y = 0; y < 8; y++)
                    for (var x = 0; x < 8; x++)
                    {
                        if (!DrawingPatternPresets.IsForeground(paint.Pattern, x, y)) continue;
                        var start = x;
                        while (x < 7 && DrawingPatternPresets.IsForeground(paint.Pattern, x + 1, y)) x++;
                        Definitions.Append('M').Append(start).Append(' ').Append(y).Append('h').Append(x - start + 1).Append("v1h-").Append(x - start + 1).Append('z');
                    }
                Definitions.Append("\" fill=\"").Append(Hex(paint.Color) ? paint.Color : "#000000").Append("\" fill-opacity=\"")
                    .Append(Num(double.IsFinite(paint.Opacity) ? Math.Clamp(paint.Opacity, 0, 1) : 1)).Append("\"/></pattern>");
            }
            else if (paint.Kind != "picture" || !Picture(paint, id)) return fallback;
            return _references[paint] = "url(#" + id + ")";
        }
    }

    private static double[] FiniteValues(DrawingChart chart) => chart.Series
        .SelectMany(series => series.Values)
        .Where(value => value is not null && double.IsFinite(value.Value))
        .Select(value => value!.Value).ToArray();

    private static string Label(DrawingChartSeries series, int index)
    {
        var category = index < series.Categories.Count ? series.Categories[index] : string.Empty;
        if (string.IsNullOrWhiteSpace(category)) category = series.Name ?? "Point";
        return category + " " + (index + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static string Esc(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private static string Num(double value) =>
        double.IsFinite(value) ? value.ToString("0.###", CultureInfo.InvariantCulture) : throw new ArithmeticException("Nonfinite chart coordinate.");

    /// <summary>
    /// Where the axes lie: the value axis and the secondary value axis at the far end (right or top) of the plot,
    /// the category axis at the top, and whether the category axis runs from its last category to its first.
    /// </summary>
    private sealed record Sides(bool ValueFar, bool SecondaryFar, bool CategoryFar, bool ReverseCategories);

    private sealed record Plot(double Width, double Height, double OriginX = 46, double OriginY = 14)
    {
        public Decoration? Decoration { get; init; }
        public Sides Sides { get; init; } = new(false, true, false, false);
        public Paints Paints { get; init; } = new(null);

        public double X(double value, ValueScale scale) => OriginX + Width * scale.Position(value);

        public double Y(double value, ValueScale scale) => OriginY + Height * (1 - scale.Position(value));

        public double PointX(int index, int slots) =>
            slots <= 1 ? OriginX + Width / 2 : OriginX + Width * ((index + 0.5) / slots);
    }
}
