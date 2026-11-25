namespace DocRedock.Core.Documents;

/// <summary>
/// Data label of one point (c:dLbl), where it differs from the labels of its series. A null flag or format
/// inherits the series setting; the format "General" shows the plain number.
/// </summary>
public sealed record DrawingChartPointLabel
{
    public bool? ShowValue { get; init; }
    public bool? ShowPercent { get; init; }

    /// <summary>The label's own c:numFmt, or the c:formatCode of the point when the label is linked to its source.</summary>
    public string? Format { get; init; }
    public string? PercentFormat { get; init; }
}

/// <summary>
/// A c:manualLayout: fractions of the chart width and height. <see cref="XIsOffset"/> / <see cref="YIsOffset"/>
/// (mode "factor") move the element from its automatic position instead of placing it from the chart edge;
/// <see cref="WidthIsRight"/> / <see cref="HeightIsBottom"/> (mode "edge") give the far edge instead of a size.
/// </summary>
public sealed record DrawingChartLayout
{
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public bool XIsOffset { get; init; }
    public bool YIsOffset { get; init; }
    public bool WidthIsRight { get; init; }
    public bool HeightIsBottom { get; init; }

    /// <summary>c:layoutTarget inner: the rectangle is the plot itself, without tick labels and axis titles.</summary>
    public bool Inner { get; init; }
}

/// <summary>
/// Options of one chart axis (c:catAx, c:dateAx or c:valAx) beyond its bounds, title and number format.
/// </summary>
public sealed record DrawingChartAxis
{
    /// <summary>c:scaling/c:logBase: a logarithmic scale with ticks at the powers of this base (2 to 1000).</summary>
    public double? LogBase { get; init; }

    /// <summary>c:scaling/c:orientation maxMin: the axis runs from its maximum (or last category) to its minimum.</summary>
    public bool Reversed { get; init; }

    /// <summary>c:delete: the axis has no line, ticks or tick labels; its series keep their scale.</summary>
    public bool Deleted { get; init; }

    /// <summary>c:crosses "min" or "max": where this axis meets the axis it crosses; null is autoZero.</summary>
    public string? Crosses { get; init; }

    /// <summary>c:crossesAt: the value (or category number) on the crossed axis where this axis lies.</summary>
    public double? CrossesAt { get; init; }

    /// <summary>Color (#RRGGBB) of the tick labels (c:txPr); null inherits the text color of the chart.</summary>
    public string? TextColor { get; init; }
}

/// <summary>
/// An outer shadow (a:outerShdw) of a chart element: its offset and blur radius in CSS pixels, its color (#RRGGBB)
/// and opacity.
/// </summary>
public sealed record DrawingChartShadow(double OffsetX, double OffsetY, double Blur, string Color, double Opacity = 1);

/// <summary>
/// The up/down bars of a stock chart (c:upDownBars): the fills (#RRGGBB) of a bar whose close is above its open and
/// of one whose close is below; null is white and black, as Excel draws them. The gap is c:gapWidth in percent.
/// </summary>
public sealed record DrawingChartUpDownBars(string? UpColor, string? DownColor, double GapWidthPercent = 150);

/// <summary>One plotted series of a native chart, holding the raw cached numbers and labels of the part.</summary>
public sealed record DrawingChartSeries(
    string? Name,
    IReadOnlyList<string> Categories,
    IReadOnlyList<double?> Values,
    IReadOnlyList<double?> XValues,
    string? Color)
{
    public static DrawingChartSeries Empty { get; } = new(null, [], [], [], null);
    public IReadOnlyDictionary<int, string> PointColors { get; init; } = new Dictionary<int, string>();

    /// <summary>Explicit c:showVal flag for this series, or null to inherit the chart-level flag.</summary>
    public bool? ShowValueLabels { get; init; }
    public bool? ShowPercentLabels { get; init; }

    /// <summary>
    /// Excel format code of the value labels: the label's own c:numFmt, or the format of the plotted values when
    /// the label is linked to its source. Null keeps the plain number.
    /// </summary>
    public string? LabelFormat { get; init; }

    /// <summary>Explicit (not source-linked) percent format code of the percentage labels of a pie or doughnut.</summary>
    public string? PercentLabelFormat { get; init; }
    public string? LabelPosition { get; init; }

    /// <summary>Labels of single points that differ from the series labels, by point index.</summary>
    public IReadOnlyDictionary<int, DrawingChartPointLabel> PointLabels { get; init; } = new Dictionary<int, DrawingChartPointLabel>();

    /// <summary>
    /// Chart group type of this series in a combination chart ("bar", "line", "area", "scatter", "stock", "pie" or
    /// "doughnut"); null is the chart type.
    /// </summary>
    public string? Plot { get; init; }

    /// <summary>Number of the chart group of this series in a combination chart: series of one group cluster and stack together.</summary>
    public int Group { get; init; }

    /// <summary>c:grouping of the chart group of this series in a combination chart; null is the grouping of the chart.</summary>
    public string? Grouping { get; init; }

    /// <summary>Gradient, pattern or picture fill of the series; <see cref="Color"/> then holds one representative color.</summary>
    public DrawingPaint? FillPaint { get; init; }

    /// <summary>Gradient, pattern or picture fills of single points (c:dPt), by point index.</summary>
    public IReadOnlyDictionary<int, DrawingPaint> PointFillPaints { get; init; } = new Dictionary<int, DrawingPaint>();

    /// <summary>The series is scaled by the secondary value axis, drawn on the right.</summary>
    public bool SecondaryAxis { get; init; }

    /// <summary>c:bubbleSize of a bubble chart, by point index; empty gives every bubble the size 1.</summary>
    public IReadOnlyList<double?> BubbleSizes { get; init; } = [];

    /// <summary>The series of a radar chart has no markers (c:marker/c:symbol "none").</summary>
    public bool HideMarkers { get; init; }

    /// <summary>Outer shadow of the marks of the series (a:effectLst/a:outerShdw of its c:spPr).</summary>
    public DrawingChartShadow? Shadow { get; init; }

    /// <summary>Number of slots the part declares for this series, including the empty ones.</summary>
    public int PointCount => Math.Max(Values.Count, Math.Max(XValues.Count, Categories.Count));
}

/// <summary>A chart read from a DrawingML chart part: type, cached values and layout flags, with no live formulas.</summary>
public sealed record DrawingChart(
    string Type,
    string? Title,
    IReadOnlyList<DrawingChartSeries> Series,
    bool Horizontal,
    string? Grouping,
    bool ShowLegend)
{
    public double? ValueAxisMinimum { get; init; }
    public double? ValueAxisMaximum { get; init; }
    public double? XAxisMinimum { get; init; }
    public double? XAxisMaximum { get; init; }
    public double? DoughnutHolePercent { get; init; }
    public double? FirstSliceAngleDegrees { get; init; }
    public string? BlankDisplay { get; init; }
    public string? ValueAxisTitle { get; init; }

    /// <summary>Title of the category axis, or of the horizontal value axis of a scatter chart.</summary>
    public string? CategoryAxisTitle { get; init; }

    /// <summary>Explicit c:varyColors flag: each point of a single series (or each pie slice) takes its own color.</summary>
    public bool? VaryColors { get; init; }

    /// <summary>
    /// Excel format code of the value-axis tick labels: c:valAx/c:numFmt, or the format of the plotted values
    /// when the axis is linked to its source. Null keeps the plain number.
    /// </summary>
    public string? ValueAxisFormat { get; init; }

    /// <summary>Excel format code of the tick labels of the horizontal value axis of a scatter chart.</summary>
    public string? XAxisFormat { get; init; }

    /// <summary>c:date1904 of the chart space: date serials count from 1904-01-01 instead of 1899-12-30.</summary>
    public bool Date1904 { get; init; }

    public double? SecondaryAxisMinimum { get; init; }
    public double? SecondaryAxisMaximum { get; init; }
    public string? SecondaryAxisFormat { get; init; }
    public string? SecondaryAxisTitle { get; init; }

    /// <summary>Manual layout of the plot area, the legend and the chart title; null keeps the automatic layout.</summary>
    public DrawingChartLayout? PlotLayout { get; init; }
    public DrawingChartLayout? LegendLayout { get; init; }
    public DrawingChartLayout? TitleLayout { get; init; }

    /// <summary>
    /// Fill (#RRGGBB) of the chart area and of the plot area; null keeps the default. For a gradient or pattern
    /// fill it is one representative color and the paint holds the fill itself; a picture fill has the paint and white.
    /// </summary>
    public string? ChartFill { get; init; }
    public string? PlotFill { get; init; }
    public DrawingPaint? ChartFillPaint { get; init; }
    public DrawingPaint? PlotFillPaint { get; init; }

    /// <summary>Options of the value axis, of the category axis (the horizontal value axis of a scatter chart) and of the secondary value axis.</summary>
    public DrawingChartAxis? ValueAxisOptions { get; init; }
    public DrawingChartAxis? CategoryAxisOptions { get; init; }
    public DrawingChartAxis? SecondaryAxisOptions { get; init; }

    /// <summary>c:legendPos without a manual layout: "r" (also null), "l", "t", "b" or "tr".</summary>
    public string? LegendPosition { get; init; }

    /// <summary>Text colors (#RRGGBB) read from c:txPr: of the whole chart, of its title and of its legend.</summary>
    public string? TextColor { get; init; }
    public string? TitleColor { get; init; }
    public string? LegendColor { get; init; }

    /// <summary>
    /// The scatter group of a combination chart shares the category axis: an x value of 1 lies at the first
    /// category. Otherwise it has a horizontal value axis of its own.
    /// </summary>
    public bool ScatterOnCategoryAxis { get; init; }

    /// <summary>c:radarStyle of a radar chart: "standard" (lines), "marker" (lines with markers) or "filled".</summary>
    public string? RadarStyle { get; init; }

    /// <summary>
    /// Bubble chart: c:bubbleScale in percent (null is 100), c:sizeRepresents "w" (the size is the width of the
    /// bubble instead of its area) and c:showNegBubbles.
    /// </summary>
    public double? BubbleScale { get; init; }
    public bool BubbleSizeIsWidth { get; init; }
    public bool ShowNegativeBubbles { get; init; }

    /// <summary>Stock chart: c:hiLowLines join the highest and the lowest value of each category.</summary>
    public bool HighLowLines { get; init; }

    /// <summary>Stock chart: c:upDownBars between the first series (open) and the last one (close); null draws none.</summary>
    public DrawingChartUpDownBars? UpDownBars { get; init; }

    /// <summary>Surface chart: c:wireframe draws the boundaries of the value bands without filling them.</summary>
    public bool Wireframe { get; init; }

    /// <summary>Surface chart: the colors (#RRGGBB) of the value bands (c:bandFmts), by band index from the lowest band.</summary>
    public IReadOnlyDictionary<int, string> BandColors { get; init; } = new Dictionary<int, string>();

    /// <summary>Outer shadow of the plot area.</summary>
    public DrawingChartShadow? PlotShadow { get; init; }
}
