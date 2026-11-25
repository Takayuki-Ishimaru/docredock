using System.Globalization;
using System.Text;
using System.Xml;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.OpenXml.Drawing;

/// <summary>Result of reading one DrawingML chart part: the cached-data model plus diagnostics.</summary>
public sealed record DrawingChartReadResult(DrawingChart? Chart, IReadOnlyList<string> Warnings);

/// <summary>
/// Safe cached-data-only reader for DrawingML chart parts. It parses the raw numeric caches and string caches
/// point by point, so display formatting (percent signs, thousands separators, dates) can never move a plot value.
/// Number formats only shape the text of category, tick and data labels.
/// </summary>
public static class DrawingChartReader
{
    private const int MaxChartPoints = 100_000;
    private const int MaxSeries = 256;
    private const int MaxXmlCharacters = 16_777_216;

    private static readonly XmlReaderSettings SafeXml = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = MaxXmlCharacters,
    };

    private static readonly Dictionary<string, string> Supported = new(StringComparer.Ordinal)
    {
        ["barChart"] = "bar",
        ["lineChart"] = "line",
        ["pieChart"] = "pie",
        ["doughnutChart"] = "doughnut",
        ["areaChart"] = "area",
        ["scatterChart"] = "scatter",
        // A 3-D chart is drawn as its 2-D equivalent: the same data, flat.
        ["bar3DChart"] = "bar",
        ["line3DChart"] = "line",
        ["area3DChart"] = "area",
        ["pie3DChart"] = "pie",
        ["radarChart"] = "radar",
        ["bubbleChart"] = "bubble",
        ["stockChart"] = "stock",
        // A surface is drawn from above as a contour chart, with or without depth in the source.
        ["surfaceChart"] = "surface",
        ["surface3DChart"] = "surface",
        // The points of the second plot of a pie-of-pie or bar-of-pie chart stay slices of the one pie.
        ["ofPieChart"] = "pie",
    };

    /// <summary>A scatter or bubble group: its points have x values and it has two value axes.</summary>
    private static bool IsXy(string type) => type is "scatter" or "bubble";

    /// <param name="theme">The theme of the host document, which resolves the scheme colors of gradient and
    /// pattern fills, backgrounds and label text. Without it only literal colors are resolved.</param>
    /// <param name="imageResolver">Maps the r:embed of an a:blipFill in the chart part to a picture reference (a data:
    /// URI), or to null with a reason. Without it a picture fill is diagnosed and the element keeps its color.</param>
    public static DrawingChartReadResult Read(byte[] xml, XmlDocument? theme = null, DrawingMlStyleReader.DrawingImageResolver? imageResolver = null)
    {
        imageResolver ??= static (string _, out string? reason) =>
        {
            reason = "this reader has no access to the pictures of the package";
            return null;
        };
        ArgumentNullException.ThrowIfNull(xml);
        var warnings = new List<string>();
        XmlDocument document;
        try
        {
            document = new XmlDocument { PreserveWhitespace = false, XmlResolver = null };
            using var reader = XmlReader.Create(new MemoryStream(xml, writable: false), SafeXml);
            document.Load(reader);
        }
        catch (Exception exception) when (exception is XmlException or DecoderFallbackException or OutOfMemoryException)
        {
            return new DrawingChartReadResult(null, [Invalid("chart part", exception)]);
        }

        var groups = FindChartGroups(document, warnings);
        if (groups.Length == 0)
            return new DrawingChartReadResult(null, warnings.Count > 0 ? warnings : ["No chart type element found."]);
        // Excel writes a:sp3d on the walls, the floor and the series of a 3-D chart; there it belongs to the depth
        // that is not reproduced. On a 2-D chart it is a bevel or a material, which is not drawn.
        var threeD = groups.Any(element => element.LocalName.EndsWith("3DChart", StringComparison.Ordinal));
        if (!threeD && document.SelectNodes("//*[local-name()='scene3d' or local-name()='sp3d']")?.Count > 0)
            return new DrawingChartReadResult(null, [.. warnings, "3-D shape formatting is unsupported; the source frame and saved data must be used."]);
        if (groups.Any(element => element.LocalName == "surface3DChart"))
            warnings.Add("The 3-D surface chart is drawn as a flat top view (a contour chart); depth, perspective and rotation are not reproduced.");
        else if (threeD)
            warnings.Add("The 3-D chart is drawn flat as its 2-D equivalent; depth, perspective, rotation and 3-D shapes are not reproduced.");
        if (groups.Any(element => element.LocalName == "ofPieChart"))
            warnings.Add("The second plot of the pie-of-pie or bar-of-pie chart is not drawn; all points are slices of one pie.");
        ReadEffectWarnings(document, warnings);

        var title = ReadTitle(document);
        var showLegend = ReadLegend(document, warnings, out var legendPosition);
        var date1904 = document.SelectSingleNode("/*[local-name()='chartSpace']/*[local-name()='date1904']") is XmlElement dateSystem &&
            Flag(dateSystem.GetAttribute("val")) == true;
        var combination = groups.Length > 1;
        var read = new List<ChartGroup>();
        foreach (var element in groups)
        {
            var group = ReadGroup(element, combination, date1904, MaxSeries - read.Sum(item => item.Series.Count), warnings);
            if (group is null) return new DrawingChartReadResult(null, warnings);
            read.Add(group);
        }
        if (combination && read.All(group => group.Series.Count == 0)) warnings.Add("Chart has no series.");

        // A pie or doughnut group has no axes; the other groups of a combination chart share the plot area.
        var pie = read.FirstOrDefault(group => group.Type is "pie" or "doughnut");
        var plots = read.Where(group => group.Type is not ("pie" or "doughnut")).ToArray();
        var barGroups = plots.Where(group => group.Type == "bar").ToArray();
        var horizontalBars = barGroups.Any(group => group.Horizontal);
        if (combination)
        {
            string? refusal = null;
            if (read.FirstOrDefault(group => group.Type is "radar" or "bubble" or "surface") is { } alone)
                refusal = $"A {alone.Type} chart group combined with other chart groups is unsupported.";
            else if (plots.Length > 0 && read.Count - plots.Length > 1)
                refusal = "More than one pie or doughnut group combined with other chart groups is unsupported.";
            else if (barGroups.Select(group => group.Horizontal).Distinct().Count() > 1)
                refusal = "Bar groups of both directions in one chart are unsupported.";
            else if (horizontalBars && plots.Any(group => group.Type == "scatter"))
                refusal = "A scatter group combined with horizontal bars is unsupported: its axes cannot be told from the axes of the bars.";
            else if (horizontalBars && plots.Any(group => group.Type != "bar" && barGroups.Any(bar => ReferenceEquals(bar.ValueAxis, group.ValueAxis))))
                refusal = "Line or area groups on the value axis of horizontal bars are unsupported: Excel draws them along the horizontal axis on a value axis of their own.";
            if (refusal is not null) return new DrawingChartReadResult(null, [.. warnings, refusal + " The chart is not drawn."]);
            if (plots.Length == 0)
                warnings.Add(read.Select(group => group.Type).Distinct().Count() > 1
                    ? "The pie and doughnut groups are drawn around one centre, the pie under the rings of the doughnut, as Excel layers them."
                    : "The pie or doughnut groups are drawn side by side; Excel draws them in the same circle, where one hides the other.");
            else if (pie is not null)
                warnings.Add("The pie or doughnut group of the combination chart is drawn in the middle of the plot area behind the other groups; its size is approximate.");
        }

        // The groups of a combination chart share the category axis; a group bound to another value axis than
        // the first one is scaled by the secondary axis, which Office draws on the opposite side (c:crosses max).
        var primaryAxis = read.Select(group => group.ValueAxis).FirstOrDefault(axis => axis is not null);
        var secondaryAxis = read.Select(group => group.ValueAxis)
            .FirstOrDefault(axis => axis is not null && !ReferenceEquals(axis, primaryAxis));
        if (read.Any(group => group.ValueAxis is { } axis && !ReferenceEquals(axis, primaryAxis) && !ReferenceEquals(axis, secondaryAxis)))
            return new DrawingChartReadResult(null, [.. warnings, "More than two value axes are unsupported; the chart is not drawn."]);
        bool Secondary(ChartGroup group) => secondaryAxis is not null && ReferenceEquals(group.ValueAxis, secondaryAxis);
        var first = combination && plots.Length > 0 ? plots[0] : read[0];
        var type = first.Type;
        var bars = read.FirstOrDefault(group => group.Type == "bar");
        var grouping = combination ? bars?.Grouping ?? (first.Grouping.Length > 0 ? "standard" : string.Empty) : first.Grouping;
        var seriesList2 = read.SelectMany((group, index) => group.Series.Select(series => combination
            ? series with { Plot = group.Type, SecondaryAxis = Secondary(group), Group = index,
                Grouping = group.Grouping.Length > 0 ? group.Grouping : group.Type == "bar" ? "clustered" : null }
            : series)).ToList();
        var primary = read.FirstOrDefault(group => !Secondary(group) && group.ValueAxis is not null) ?? first;
        var secondary = read.FirstOrDefault(Secondary);
        if (pie is not null && pie.Series.Take(pie.Type == "pie" ? 1 : int.MaxValue).Any(series => series.Values.Any(value => value < 0)))
            warnings.Add("A pie or doughnut chart plots the absolute value of a negative number, as Excel does.");
        var scatter = plots.FirstOrDefault(group => group.Type == "scatter");
        if (combination && scatter is not null && plots.Any(group => group.Type != "scatter") &&
            (type == "scatter" ? plots.Where(group => group.Type != "scatter").Select(group => group.CategoryAxis) : [scatter.XAxis])
                .Any(axis => axis is not null && Flag(DirectChild(axis, "delete")?.GetAttribute("val") ?? "0") != true))
            warnings.Add("The tick labels of the second horizontal axis of the combination chart are not drawn.");

        var blankDisplay = ReadBlankDisplay(document, warnings);
        if (blankDisplay is "zero" or "span" && read.All(group => group.Type is not ("line" or "area")))
            warnings.Add("Explicit blank display settings are applied only to line/area charts; other chart types keep their existing missing-point layout.");
        var xy = IsXy(type);
        var (axisTitle, categoryAxisTitle) = ReadAxisTitles(document, first.Element, type, warnings);
        var secondaryTitle = secondaryAxis is null ? null : ReadAxisTitle([secondaryAxis], "secondary value", warnings);
        var valueOptions = ReadAxisOptions(primary.ValueAxis, "value", theme, warnings);
        var categoryOptions = ReadAxisOptions(xy ? first.XAxis : first.CategoryAxis, xy ? "x" : "category", theme, warnings);
        var secondaryOptions = ReadAxisOptions(secondaryAxis, "secondary value", theme, warnings);
        // Excel removes the title of an axis together with the axis.
        if (valueOptions?.Deleted == true) axisTitle = null;
        if (categoryOptions?.Deleted == true) categoryAxisTitle = null;
        if (secondaryOptions?.Deleted == true) secondaryTitle = null;
        if (type == "radar" && valueOptions is { LogBase: not null } or { Reversed: true })
            warnings.Add("A logarithmic or reversed value axis of a radar chart is drawn as a linear axis from the centre.");
        if (!xy && valueOptions?.CrossesAt > 1)
            warnings.Add("A value axis that crosses between the categories is drawn at the edge of the plot area.");
        foreach (var (options, groupsOnAxis) in new[] { (valueOptions, read.Where(group => !Secondary(group))), (secondaryOptions, read.Where(Secondary)) })
            if (options?.LogBase is not null && groupsOnAxis.Where(group => group.Type is not ("pie" or "doughnut"))
                    .SelectMany(group => group.Series).SelectMany(series => series.Values).Any(value => value <= 0))
                warnings.Add("Values of zero or less cannot be plotted on a logarithmic axis, as Excel warns; those points are left out.");
        if (categoryOptions?.LogBase is not null && scatter is not null && scatter.Series.SelectMany(series => series.XValues).Any(value => value <= 0))
            warnings.Add("X values of zero or less cannot be plotted on a logarithmic axis, as Excel warns; those points are left out.");
        // A percent-stacked axis plots shares of each slot, so the format of the source values does not apply to it.
        string? AxisFormat(ChartGroup group, XmlElement? axis, string what)
        {
            var percentStacked = grouping == "percentStacked" && (!combination || bars is not null && Secondary(bars) == Secondary(group));
            var sameAxis = read.Where(item => Secondary(item) == Secondary(group)).ToArray();
            return UsableFormat(
                ReadAxisFormat(axis, percentStacked ? null : sameAxis.SelectMany(item => item.ValueFormats).FirstOrDefault()),
                what, percentStacked ? [0, .5, 1] : AxisSamples(sameAxis.SelectMany(item => item.Series).SelectMany(item => item.Values), group.Minimum, group.Maximum),
                date1904, warnings);
        }
        var valueAxisFormat = AxisFormat(primary, primary.ValueAxis, "value axis");
        var secondaryAxisFormat = secondary is null ? null : AxisFormat(secondary, secondaryAxis, "secondary value axis");
        var xAxisFormat = UsableFormat(
            ReadAxisFormat((scatter ?? first).XAxis, (scatter ?? first).XFormats.FirstOrDefault()),
            "x axis", AxisSamples(seriesList2.SelectMany(series => series.XValues), (scatter ?? first).XMinimum, (scatter ?? first).XMaximum), date1904, warnings);

        var chartElement = document.SelectSingleNode("/*[local-name()='chartSpace']/*[local-name()='chart']") as XmlElement;
        var plotArea = chartElement is null ? null : DirectChild(chartElement, "plotArea");
        var plotLayout = ReadLayout(plotArea, "plot area", warnings);
        var legendLayout = ReadLayout(chartElement is null ? null : DirectChild(chartElement, "legend"), "legend", warnings);
        var titleLayout = ReadLayout(chartElement is null ? null : DirectChild(chartElement, "title"), "chart title", warnings);
        if (document.SelectNodes("//*[local-name()='manualLayout']")!.OfType<XmlElement>().Any(manual =>
                manual.ParentNode?.ParentNode is not { } holder || !ReferenceEquals(holder.ParentNode, chartElement) ||
                holder.LocalName is not ("plotArea" or "legend" or "title")))
            warnings.Add("Manual layout of axis titles, data labels or other chart elements is not applied; they use the automatic layout.");
        // The fills of series, points, the chart area and the plot area are drawn as gradients; other gradients are not.
        if (document.SelectNodes("//*[local-name()='gradFill']")!.OfType<XmlElement>().Any(gradient =>
                gradient.ParentNode is not XmlElement { LocalName: "spPr", ParentNode: XmlElement { LocalName: "ser" or "dPt" or "plotArea" or "chartSpace" } }))
            warnings.Add("Gradients of lines, markers and text are drawn as one representative color.");
        if (document.SelectNodes("//*[local-name()='blipFill']")!.OfType<XmlElement>().Any(picture =>
                picture.ParentNode is not XmlElement { LocalName: "spPr", ParentNode: XmlElement { LocalName: "ser" or "dPt" or "plotArea" or "chartSpace" } }))
            warnings.Add("Picture fills of lines, markers, text and other chart elements are not drawn.");
        var seriesElements = groups.SelectMany(group => group.ChildNodes.OfType<XmlElement>().Where(child => child.LocalName == "ser")).ToArray();
        if (seriesElements.Length == seriesList2.Count)
            seriesList2 = seriesList2.Select((series, index) => ReadFills(series, seriesElements[index], theme, warnings, imageResolver) with
            { Shadow = ReadShadow(seriesElements[index], theme, warnings) }).ToList();
        var (chartFill, chartPaint) = ReadBackground(document.DocumentElement, theme, warnings, imageResolver);
        var (plotFill, plotPaint) = ReadBackground(plotArea, theme, warnings, imageResolver);
        // Excel draws text without a color of its own in the text color of the theme, which is dark as a rule.
        var textColor = ReadTextColor(document.DocumentElement, theme, warnings);
        foreach (var (fill, what) in new[] { (chartFill, "chart area"), (plotFill, "plot area") })
            if (textColor is null && valueOptions?.TextColor is null && fill is { Length: 7 } && IsDark(fill))
                warnings.Add($"The dark {what} fill has no label color in the chart; labels on it are drawn in a light color to stay readable.");
        // A plot area without a fill shows the chart area behind it.
        if (plotFill is null && plotArea is not null && DirectChild(plotArea, "spPr") is { } plotShape && DirectChild(plotShape, "noFill") is not null)
            plotFill = chartPaint is null ? chartFill : "none";

        var chart = new DrawingChart(type, title, seriesList2, combination ? horizontalBars : first.Horizontal, grouping, showLegend)
        { ValueAxisFormat = valueAxisFormat, XAxisFormat = xAxisFormat, Date1904 = date1904,
          ValueAxisMinimum = primary.Minimum, ValueAxisMaximum = primary.Maximum, XAxisMinimum = (scatter ?? first).XMinimum, XAxisMaximum = (scatter ?? first).XMaximum,
          DoughnutHolePercent = (read.FirstOrDefault(group => group.Type == "doughnut") ?? pie ?? first).Hole, FirstSliceAngleDegrees = (pie ?? first).FirstAngle,
          BlankDisplay = blankDisplay, ValueAxisTitle = axisTitle, CategoryAxisTitle = categoryAxisTitle,
          VaryColors = (pie ?? first).VaryColors,
          SecondaryAxisMinimum = secondary?.Minimum, SecondaryAxisMaximum = secondary?.Maximum,
          SecondaryAxisFormat = secondaryAxisFormat, SecondaryAxisTitle = secondaryTitle,
          PlotLayout = plotLayout, LegendLayout = legendLayout, TitleLayout = titleLayout,
          ChartFill = chartFill, PlotFill = plotFill, ChartFillPaint = chartPaint, PlotFillPaint = plotPaint,
          ValueAxisOptions = valueOptions, CategoryAxisOptions = categoryOptions, SecondaryAxisOptions = secondaryOptions,
          LegendPosition = legendPosition, TextColor = textColor,
          TitleColor = ReadTextColor(chartElement is null ? null : DirectChild(chartElement, "title"), theme, warnings),
          LegendColor = ReadTextColor(chartElement is null ? null : DirectChild(chartElement, "legend"), theme, warnings),
          ScatterOnCategoryAxis = combination && scatter is { XAxis: null, CategoryAxis: not null },
          PlotShadow = ReadShadow(plotArea, theme, warnings) };
        return new DrawingChartReadResult(ReadTypeSettings(chart, read, theme, warnings), warnings);
    }

    /// <summary>The settings of a radar, bubble, stock or surface group.</summary>
    private static DrawingChart ReadTypeSettings(DrawingChart chart, List<ChartGroup> groups, XmlDocument? theme, List<string> warnings)
    {
        string? Value(XmlElement holder, string name) => DirectChild(holder, name)?.GetAttribute("val");
        bool On(XmlElement holder, string name) => DirectChild(holder, name) is { } flag && Flag(flag.GetAttribute("val")) == true;
        string? Solid(XmlElement? holder) => holder is not null && DirectChild(holder, "spPr") is { } shape && DirectChild(shape, "solidFill") is { } fill &&
            ResolveFill(fill, theme, warnings) is { Kind: "solid", Color: { Length: 7 } color } ? color : null;
        foreach (var group in groups)
        {
            var element = group.Element;
            switch (group.Type)
            {
                case "radar":
                    chart = chart with { RadarStyle = Value(element, "radarStyle") switch { "marker" => "marker", "filled" => "filled", _ => "standard" } };
                    break;
                case "bubble":
                    var scale = ParseNumber(Value(element, "bubbleScale") ?? "");
                    if (scale is < 0 or > 300)
                    {
                        // ST_BubbleScale: 0 to 300 percent.
                        warnings.Add("Invalid bubble scale; the default size is used.");
                        scale = null;
                    }
                    chart = chart with { BubbleScale = scale, BubbleSizeIsWidth = Value(element, "sizeRepresents") == "w", ShowNegativeBubbles = On(element, "showNegBubbles") };
                    if (group.Series.SelectMany(series => series.BubbleSizes).Any(size => size < 0) && !chart.ShowNegativeBubbles)
                        warnings.Add("Bubbles with a negative size are not shown, as in Excel.");
                    break;
                case "stock":
                    var bars = DirectChild(element, "upDownBars");
                    var gap = bars is null ? null : ParseNumber(Value(bars, "gapWidth") ?? "");
                    chart = chart with
                    {
                        HighLowLines = DirectChild(element, "hiLowLines") is not null,
                        UpDownBars = bars is null ? null : new DrawingChartUpDownBars(Solid(DirectChild(bars, "upBars")), Solid(DirectChild(bars, "downBars")),
                            gap is >= 0 and <= 500 ? gap.Value : 150),
                    };
                    if (group.Series.Count is not (3 or 4))
                        warnings.Add($"A stock chart plots three (high, low, close) or four (open, high, low, close) series; this one has {group.Series.Count} and its marks are approximate.");
                    warnings.Add("The stock chart is drawn with its high-low lines, up/down bars and close marks; the lines and markers of its series are not drawn.");
                    break;
                case "surface":
                    var colors = new Dictionary<int, string>();
                    foreach (var band in DirectChild(element, "bandFmts")?.ChildNodes.OfType<XmlElement>().Where(child => child.LocalName == "bandFmt") ?? [])
                        if (int.TryParse(Value(band, "idx"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index is >= 0 and < 1024 &&
                            Solid(band) is { } color)
                            colors[index] = color;
                    chart = chart with { Wireframe = On(element, "wireframe"), BandColors = colors };
                    warnings.Add("The surface chart is drawn from above as a grid of cells colored by value band; the bands follow the ticks of the drawn value scale, not the major unit of the source axis.");
                    break;
            }
        }
        return chart;
    }

    /// <summary>
    /// What is drawn of the effects of the chart: an outer shadow of a series or of the plot area. Every other
    /// effect is named once.
    /// </summary>
    private static void ReadEffectWarnings(XmlDocument document, List<string> warnings)
    {
        var other = new List<string>();
        var elsewhere = false;
        foreach (var effects in document.SelectNodes("//*[local-name()='effectLst' or local-name()='effectDag']")!.OfType<XmlElement>())
        {
            var children = effects.ChildNodes.OfType<XmlElement>().ToArray();
            if (children.Length == 0) continue;
            foreach (var child in children)
            {
                var name = effects.LocalName == "effectDag" ? "effectDag" : child.LocalName.Length <= 32 ? child.LocalName : child.LocalName[..32];
                if (name != "outerShdw") { if (!other.Contains(name, StringComparer.Ordinal) && other.Count < 16) other.Add(name); }
                else if (effects.ParentNode is not XmlElement { LocalName: "spPr", ParentNode: XmlElement { LocalName: "ser" or "plotArea" } }) elsewhere = true;
            }
        }
        if (other.Count > 0)
            warnings.Add($"Chart effects other than an outer shadow ({string.Join(", ", other)}) are not drawn.");
        if (elsewhere)
            warnings.Add("Shadows of chart elements other than series and the plot area are not drawn.");
    }

    /// <summary>
    /// The a:outerShdw of a series or of the plot area: a:dist and a:blurRad in EMU, a:dir in 60000ths of a degree
    /// clockwise from the right, and its color with alpha.
    /// </summary>
    private static DrawingChartShadow? ReadShadow(XmlElement? holder, XmlDocument? theme, List<string> warnings)
    {
        if (holder is null || DirectChild(holder, "spPr") is not { } shape || DirectChild(shape, "effectLst") is not { } effects ||
            DirectChild(effects, "outerShdw") is not { } shadow) return null;
        double Emu(string name) => ParseNumber(shadow.GetAttribute(name)) is { } value ? Math.Clamp(value / 9525d, 0, 1000) : 0;
        var color = shadow.ChildNodes.OfType<XmlElement>().FirstOrDefault(child => child.LocalName is "srgbClr" or "schemeClr" or "scrgbClr" or "sysClr" or "hslClr" or "prstClr");
        DrawingPaint? paint = null;
        if (color is not null)
        {
            var fill = shadow.OwnerDocument.CreateElement("a", "solidFill", "http://schemas.openxmlformats.org/drawingml/2006/main");
            fill.AppendChild(color.CloneNode(deep: true));
            paint = ResolveFill(fill, theme, warnings);
        }
        var distance = Emu("dist");
        var radians = (ParseNumber(shadow.GetAttribute("dir")) ?? 0) / 60000d * Math.PI / 180;
        return new DrawingChartShadow(Math.Round(distance * Math.Cos(radians), 3), Math.Round(distance * Math.Sin(radians), 3), Emu("blurRad"),
            paint is { Kind: "solid", Color: { Length: 7 } rgb } ? rgb : "#000000",
            paint is { Kind: "solid" } && double.IsFinite(paint.Opacity) ? Math.Clamp(paint.Opacity, 0, 1) : 1);
    }

    /// <summary>One chart group (c:barChart, c:lineChart, ...) of the plot area with its series and value axis.</summary>
    private sealed record ChartGroup(string Type, bool Horizontal, string Grouping, IReadOnlyList<DrawingChartSeries> Series,
        IReadOnlyList<string?> ValueFormats, IReadOnlyList<string?> XFormats, XmlElement? ValueAxis, XmlElement? XAxis,
        double? Minimum, double? Maximum, double? XMinimum, double? XMaximum, double? Hole, double? FirstAngle, bool? VaryColors)
    {
        public required XmlElement Element { get; init; }

        /// <summary>The category or date axis of the group; of a scatter group, the one it shares with other groups.</summary>
        public XmlElement? CategoryAxis { get; init; }
    }

    private static ChartGroup? ReadGroup(XmlElement typeElement, bool combination, bool date1904, int seriesLimit,
        List<string> warnings)
    {
        var horizontal = false;
        var grouping = string.Empty;
        var barDirection = DirectChild(typeElement, "barDir");
        if (barDirection is not null)
        {
            horizontal = string.Equals(barDirection.GetAttribute("val"), "bar", StringComparison.OrdinalIgnoreCase);
            if (!horizontal && barDirection.GetAttribute("val").Length > 0 &&
                !string.Equals(barDirection.GetAttribute("val"), "col", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Unknown bar direction \"{barDirection.GetAttribute("val")}\"; the chart is not drawn.");
                return null;
            }
        }

        var groupingElement = DirectChild(typeElement, "grouping");
        var supportedGrouping = groupingElement?.GetAttribute("val") ?? string.Empty;
        if (groupingElement is not null)
        {
            if (supportedGrouping.Length == 0)
            {
                warnings.Add("Chart grouping has no value; the chart is not drawn.");
                return null;
            }
            else if (!IsGrouping(supportedGrouping))
            {
                warnings.Add($"Unsupported grouping \"{supportedGrouping}\"; the chart is not drawn.");
                return null;
            }

            grouping = supportedGrouping;
        }

        var type = Supported.TryGetValue(typeElement.LocalName, out var known) ? known : typeElement.LocalName;

        var categoryFormat = ReadCategoryAxisFormat(typeElement);
        var seriesList = new List<DrawingChartSeries>();
        var sourceFormats = new List<(string? Values, string? XValues)>();
        var pointFormats = new List<IReadOnlyDictionary<int, string>>();
        foreach (var seriesElement in typeElement.ChildNodes.OfType<XmlElement>().Where(child => child.LocalName == "ser"))
        {
            if (seriesList.Count >= seriesLimit)
            {
                warnings.Add($"Chart has more than {MaxSeries} series; the extra series were dropped.");
                break;
            }

            var read = ReadSeries(seriesElement, IsXy(type), categoryFormat, date1904, warnings,
                out var valueFormat, out var xFormat, out var ownFormats);
            if (type == "bubble")
                read = read with { BubbleSizes = ReadNumbers(DirectChild(seriesElement, "bubbleSize"), "bubble sizes", warnings) };
            else if (type == "radar")
                read = read with
                {
                    HideMarkers = DirectChild(seriesElement, "marker") is { } marker && DirectChild(marker, "symbol")?.GetAttribute("val") == "none",
                    // The color of a radar series is the color of its line where it has no fill.
                    Color = read.Color is { Length: > 0 } ? read.Color
                        : DirectChild(seriesElement, "spPr") is { } shape && DirectChild(shape, "ln") is { } line ? ReadColor(line) : read.Color,
                };
            seriesList.Add(read);
            sourceFormats.Add((valueFormat, xFormat));
            pointFormats.Add(ownFormats);
        }

        if (seriesList.Count == 0 && !combination) warnings.Add("Chart has no series.");
        if (type == "pie" && seriesList.Count > 1)
            warnings.Add($"A pie chart plots one series; only the first of {seriesList.Count} is drawn, the others stay in the saved data.");

        if (!ReadAxisRanges(typeElement, type, warnings, out var minimum, out var maximum, out var xMinimum, out var xMaximum,
            out var valueAxis, out var xAxis, out var categoryAxis))
            return null;
        if (!ReadSetting(typeElement, "holeSize", 10, 90, warnings, out var hole) ||
            !ReadSetting(typeElement, "firstSliceAng", 0, 360, warnings, out var firstAngle))
            return null;
        ReadDataLabels(typeElement, warnings, out var chartShowVal, out var chartShowPercent,
            out var chartFormat, out var chartPosition, out _);
        var pie = type is "pie" or "doughnut";
        var seriesList2 = new List<DrawingChartSeries>();
        var serElements = typeElement.ChildNodes.OfType<XmlElement>()
            .Where(child => child.LocalName == "ser").ToArray();
        for (var s = 0; s < seriesList.Count; s++)
        {
            bool? serShowVal = null, serShowPercent = null;
            LabelNumberFormat? serFormat = null;
            string? serPosition = null;
            Dictionary<int, PointLabelSource>? serPoints = null;
            if (s < serElements.Length)
                ReadDataLabels(serElements[s], warnings, out serShowVal, out serShowPercent,
                    out serFormat, out serPosition, out serPoints);
            var showVal = serShowVal ?? chartShowVal;
            var showPercent = serShowPercent ?? chartShowPercent;
            // A label without a format of its own, or linked to its source, shows the format of the plotted values.
            var labelFormat = serFormat ?? chartFormat;
            var finite = seriesList[s].Values.OfType<double>().ToArray();
            seriesList2.Add(seriesList[s] with
            {
                ShowValueLabels = showVal,
                ShowPercentLabels = showPercent,
                LabelFormat = showVal == true
                    ? UsableFormat(labelFormat is { SourceLinked: false } ? labelFormat.Code : sourceFormats[s].Values,
                        "data label", finite, date1904, warnings)
                    : null,
                PercentLabelFormat = pie && showPercent == true && labelFormat is { SourceLinked: false }
                    ? UsableFormat(labelFormat.Code, "percent label", [0, .5, 1], date1904, warnings)
                    : null,
                LabelPosition = serPosition ?? chartPosition,
                PointLabels = ReadPointLabels(seriesList[s], serPoints, pointFormats[s], sourceFormats[s].Values,
                    labelFormat, showVal, pie, date1904, warnings),
            });
        }
        if (type is not ("pie" or "doughnut") && seriesList2.Any(s => s.ShowPercentLabels == true || s.PointLabels.Values.Any(p => p.ShowPercent == true)))
        {
            warnings.Add("Percent data labels are supported only on pie and doughnut charts; that label content is not drawn.");
            seriesList2 = seriesList2.Select(s => s with
            {
                ShowPercentLabels = false,
                PointLabels = s.PointLabels.ToDictionary(p => p.Key, p => p.Value with { ShowPercent = p.Value.ShowPercent == true ? false : p.Value.ShowPercent }),
            }).ToList();
        }
        return new ChartGroup(type, horizontal, grouping, seriesList2, sourceFormats.Select(format => format.Values).ToArray(),
            sourceFormats.Select(format => format.XValues).ToArray(), valueAxis, xAxis, minimum, maximum, xMinimum, xMaximum,
            hole, firstAngle, DirectChild(typeElement, "varyColors") is { } vary ? Flag(vary.GetAttribute("val")) : null)
        { Element = typeElement, CategoryAxis = categoryAxis };
    }

    /// <summary>One c:dLbl as written: c:delete, the flags it sets and its own c:numFmt.</summary>
    private sealed record PointLabelSource(bool Delete, bool? ShowVal, bool? ShowPercent, LabelNumberFormat? Format);

    /// <summary>
    /// The labels of single points. A c:dLbl replaces the flags and the number format of the series labels for its
    /// point, and a label linked to its source shows the c:formatCode of its own c:pt where the point has one, as
    /// the worksheet cell does.
    /// </summary>
    private static Dictionary<int, DrawingChartPointLabel> ReadPointLabels(DrawingChartSeries series,
        Dictionary<int, PointLabelSource>? sources, IReadOnlyDictionary<int, string> pointFormats, string? cacheFormat,
        LabelNumberFormat? seriesFormat, bool? seriesShowsValue, bool pie, bool date1904, List<string> warnings)
    {
        var labels = new Dictionary<int, DrawingChartPointLabel>();
        double[] Sample(int index) => index < series.Values.Count && series.Values[index] is { } value ? [value] : [0];
        string Usable(string? code, int index) => UsableFormat(code, "data label", Sample(index), date1904, warnings) ?? "General";
        if (sources is not null)
            foreach (var (index, source) in sources)
            {
                var label = new DrawingChartPointLabel
                {
                    ShowValue = source.Delete ? false : source.ShowVal,
                    ShowPercent = source.Delete ? false : source.ShowPercent,
                };
                if (source.Format is { SourceLinked: false } own)
                    label = label with
                    {
                        Format = Usable(own.Code, index),
                        PercentFormat = pie ? UsableFormat(own.Code, "percent label", [0, .5, 1], date1904, warnings) ?? "General" : null,
                    };
                else if (source.Format is not null && seriesFormat is { SourceLinked: false })
                    // Linked to its source although the series labels have a format of their own.
                    label = label with
                    {
                        Format = Usable(pointFormats.TryGetValue(index, out var linked) ? linked : cacheFormat, index),
                        PercentFormat = pie ? "General" : null,
                    };
                // A label switched on for this point alone has no series label format to fall back on.
                if (label.Format is null && label.ShowValue == true && seriesShowsValue != true)
                    label = label with
                    {
                        Format = Usable(seriesFormat is { SourceLinked: false } explicitFormat ? explicitFormat.Code
                            : pointFormats.TryGetValue(index, out var code) ? code : cacheFormat, index),
                    };
                labels[index] = label;
            }

        if (seriesFormat is { SourceLinked: false }) return labels;
        foreach (var (index, code) in pointFormats)
        {
            var label = labels.GetValueOrDefault(index);
            if ((label?.ShowValue ?? seriesShowsValue) != true || label?.Format is not null) continue;
            labels[index] = (label ?? new DrawingChartPointLabel()) with { Format = Usable(code, index) };
        }
        return labels;
    }

    /// <summary>The c:manualLayout of the plot area, the legend or the chart title.</summary>
    private static DrawingChartLayout? ReadLayout(XmlElement? holder, string what, List<string> warnings)
    {
        if (holder is null || DirectChild(holder, "layout") is not { } layout || DirectChild(layout, "manualLayout") is not { } manual)
            return null;
        var valid = true;
        double? Number(string name)
        {
            if (DirectChild(manual, name) is not { } element) return null;
            var number = ParseNumber(element.GetAttribute("val"));
            valid &= number is { } finite && Math.Abs(finite) <= 16;
            return number;
        }
        // ST_LayoutMode: "factor" (the default) moves x and y from the automatic position and reads w and h as a
        // size; "edge" reads x and y from the chart edge and w and h as the right and bottom edge. All values are
        // fractions of the chart width or height.
        bool Edge(string name) => DirectChild(manual, name)?.GetAttribute("val") == "edge";
        var result = new DrawingChartLayout
        {
            X = Number("x"), Y = Number("y"), Width = Number("w"), Height = Number("h"),
            XIsOffset = !Edge("xMode"), YIsOffset = !Edge("yMode"),
            WidthIsRight = Edge("wMode"), HeightIsBottom = Edge("hMode"),
            // The default target is the outer rectangle, which includes the tick labels.
            Inner = DirectChild(manual, "layoutTarget")?.GetAttribute("val") == "inner",
        };
        if (valid) return result;
        warnings.Add($"Invalid manual layout of the {what}; the automatic layout is used.");
        return null;
    }

    /// <summary>
    /// The fill of the chart area or the plot area: its color (the representative color of a gradient or the
    /// background color of a pattern) and, for a gradient or pattern, the paint itself. A fill that is more than
    /// half transparent is not drawn.
    /// </summary>
    private static (string? Color, DrawingPaint? Paint) ReadBackground(XmlElement? holder, XmlDocument? theme, List<string> warnings,
        DrawingMlStyleReader.DrawingImageResolver? images)
    {
        if (holder is null || DirectChild(holder, "spPr") is not { } shape) return default;
        var fill = DirectChild(shape, "solidFill") ?? DirectChild(shape, "gradFill") ?? DirectChild(shape, "pattFill") ?? DirectChild(shape, "blipFill");
        if (fill is null) return default;
        if (fill.SelectSingleNode(".//*[local-name()='alpha']") is XmlElement alpha && ParseNumber(alpha.GetAttribute("val")) is < 50000)
            return default;
        var color = ReadColor(holder);
        if (color.Length == 7 && fill.LocalName == "solidFill") return (color, null);
        // A scheme color, a gradient, a pattern or a picture is resolved as the fill of a shape is.
        var paint = ResolveFill(fill, theme, warnings, images);
        if (color.Length != 7)
            color = paint switch
            {
                { Kind: "solid", Color: { } solid } => solid,
                { Kind: "pattern", BackColor: { } back } => back,
                { Kind: "linear" or "radial", Stops: { Count: > 0 } stops } => stops[stops.Count / 2].Color,
                // Labels on a picture are placed as on a light background.
                { Kind: "picture" } => "#FFFFFF",
                _ => string.Empty,
            };
        return color.Length == 7 ? (color, paint is { Kind: "linear" or "radial" or "pattern" or "picture" } ? paint : null) : default;
    }

    /// <summary>A fill element (a:solidFill, a:gradFill, a:pattFill) as the paint of a shape with that fill.</summary>
    private static DrawingPaint? ResolveFill(XmlElement fill, XmlDocument? theme, List<string> warnings,
        DrawingMlStyleReader.DrawingImageResolver? images = null)
    {
        var document = new XmlDocument { XmlResolver = null };
        var shape = document.CreateElement("sp");
        document.AppendChild(shape);
        var properties = document.CreateElement("spPr");
        shape.AppendChild(properties);
        properties.AppendChild(document.ImportNode(fill, deep: true));
        var appearance = DrawingMlStyleReader.Read(shape, theme, imageResolver: images);
        foreach (var warning in appearance.Warnings)
            if (!warnings.Contains(warning, StringComparer.Ordinal)) warnings.Add(warning);
        return appearance.FillPaint;
    }

    /// <summary>The gradient or pattern fill of a series or a point; a solid fill stays the color the caller read.</summary>
    private static DrawingPaint? ReadFillPaint(XmlElement holder, XmlDocument? theme, List<string> warnings,
        DrawingMlStyleReader.DrawingImageResolver? images) =>
        DirectChild(holder, "spPr") is { } shape && (DirectChild(shape, "gradFill") ?? DirectChild(shape, "pattFill") ?? DirectChild(shape, "blipFill")) is { } fill &&
        DirectChild(shape, "solidFill") is null ? ResolveFill(fill, theme, warnings, images) : null;

    /// <summary>Adds the gradient and pattern fills of a series and of its points (c:dPt).</summary>
    private static DrawingChartSeries ReadFills(DrawingChartSeries series, XmlElement element, XmlDocument? theme, List<string> warnings,
        DrawingMlStyleReader.DrawingImageResolver? images)
    {
        // A pattern is represented by its foreground color where one color is needed (lines, markers).
        static string? Representative(DrawingPaint? paint) => paint is { Kind: "pattern" or "solid", Color: { Length: 7 } color } ? color : null;
        static DrawingPaint? Drawn(DrawingPaint? paint) => paint is { Kind: "linear" or "radial" or "pattern" or "picture" } ? paint : null;
        var own = ReadFillPaint(element, theme, warnings, images);
        var colors = new Dictionary<int, string>(series.PointColors);
        var paints = new Dictionary<int, DrawingPaint>();
        foreach (var point in element.ChildNodes.OfType<XmlElement>().Where(child => child.LocalName == "dPt"))
        {
            if (!int.TryParse(DirectChild(point, "idx")?.GetAttribute("val"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
                index < 0 || index >= MaxChartPoints || ReadFillPaint(point, theme, warnings, images) is not { } paint) continue;
            if (Drawn(paint) is { } drawn) paints[index] = drawn;
            if (!colors.ContainsKey(index) && Representative(paint) is { } color) colors[index] = color;
        }
        if (own is null && paints.Count == 0 && colors.Count == series.PointColors.Count) return series;
        return series with
        {
            Color = string.IsNullOrEmpty(series.Color) ? Representative(own) ?? series.Color : series.Color,
            FillPaint = Drawn(own), PointColors = colors, PointFillPaints = paints,
        };
    }

    /// <summary>The solid color of the text of a chart element: its c:txPr, or the rich text of a title.</summary>
    private static string? ReadTextColor(XmlElement? holder, XmlDocument? theme, List<string> warnings) =>
        holder?.SelectSingleNode("./*[local-name()='txPr' or local-name()='tx']//*[local-name()='defRPr' or local-name()='rPr']/*[local-name()='solidFill']") is XmlElement fill &&
        ResolveFill(fill, theme, warnings) is { Kind: "solid", Color: { Length: 7 } color } ? color : null;

    /// <summary>WCAG relative luminance below 0.25: #222 text has less than a contrast of 4.5:1 on such a color.</summary>
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
    /// The options of an axis. c:crosses and c:crossesAt say where the axis lies on the axis it crosses
    /// (c:crossAx): on a category axis they are a value, on a value axis a category number.
    /// </summary>
    private static DrawingChartAxis? ReadAxisOptions(XmlElement? axis, string what, XmlDocument? theme, List<string> warnings)
    {
        if (axis is null) return null;
        var scaling = DirectChild(axis, "scaling");
        double? logBase = null;
        if (axis.LocalName == "valAx" && scaling is not null && DirectChild(scaling, "logBase") is { } log)
        {
            // ST_LogBase: 2 to 1000.
            logBase = ParseNumber(log.GetAttribute("val"));
            if (logBase is not (>= 2 and <= 1000))
            {
                warnings.Add($"Invalid logarithmic base of the {what} axis; a linear scale is used.");
                logBase = null;
            }
        }
        var crosses = DirectChild(axis, "crosses")?.GetAttribute("val");
        var options = new DrawingChartAxis
        {
            LogBase = logBase,
            Reversed = scaling is not null && DirectChild(scaling, "orientation")?.GetAttribute("val") == "maxMin",
            Deleted = DirectChild(axis, "delete") is { } delete && Flag(delete.GetAttribute("val")) == true,
            Crosses = crosses is "min" or "max" ? crosses : null,
            CrossesAt = DirectChild(axis, "crossesAt") is { } at ? ParseNumber(at.GetAttribute("val")) : null,
            TextColor = ReadTextColor(axis, theme, warnings),
        };
        return options == new DrawingChartAxis() ? null : options;
    }

    private static bool? Flag(string value) =>
        value is "" or "1" or "true" ? true : value is "0" or "false" ? false : null;

    /// <summary>A c:numFmt: its format code, and whether the format of the source values replaces it.</summary>
    private sealed record LabelNumberFormat(string Code, bool SourceLinked);

    private static LabelNumberFormat ReadNumberFormat(XmlElement numFmt) =>
        new(numFmt.GetAttribute("formatCode"), numFmt.GetAttribute("sourceLinked") is "1" or "true");

    /// <summary>
    /// The format code a label can show, or null to keep the plain number: a General or missing code silently, a
    /// code that is too long or that the number-format engine cannot apply to the plotted numbers with a diagnostic.
    /// </summary>
    private static string? UsableFormat(string? code, string what, IReadOnlyCollection<double> samples, bool date1904,
        List<string> warnings)
    {
        if (ExcelNumberFormat.IsGeneral(code)) return null;
        string? warning = null;
        if (code!.Length > ExcelNumberFormat.MaxFormatCodeLength)
            warning = $"The {what} number format exceeds {ExcelNumberFormat.MaxFormatCodeLength} characters; the plain number is shown.";
        else if (samples.DefaultIfEmpty(0).Any(sample => ExcelNumberFormat.Format(sample, code, date1904) is null))
            warning = $"The {what} number format \"{code}\" cannot be applied; the plain number is shown.";
        if (warning is null) return code;
        if (!warnings.Contains(warning, StringComparer.Ordinal)) warnings.Add(warning);
        return null;
    }

    private static double[] AxisSamples(IEnumerable<double?> values, double? minimum, double? maximum)
    {
        var finite = values.Append(minimum).Append(maximum).OfType<double>().ToArray();
        return finite.Length == 0 ? [0] : [0, finite.Min(), finite.Max()];
    }

    /// <summary>The format code of an axis, where a source-linked axis takes the format of the plotted values.</summary>
    private static string? ReadAxisFormat(XmlElement? axis, string? sourceFormat) =>
        axis is null || DirectChild(axis, "numFmt") is not { } numFmt ? null :
        ReadNumberFormat(numFmt) is { SourceLinked: false } own ? own.Code : sourceFormat;

    /// <summary>The explicit format of the category or date axis: Office shows numeric categories with it.</summary>
    private static string? ReadCategoryAxisFormat(XmlElement chart)
    {
        var ids = chart.ChildNodes.OfType<XmlElement>().Where(element => element.LocalName == "axId")
            .Select(element => element.GetAttribute("val")).ToHashSet(StringComparer.Ordinal);
        var axes = chart.ParentNode?.ChildNodes.OfType<XmlElement>().Where(element => element.LocalName is "catAx" or "dateAx" &&
            (ids.Count == 0 || ids.Contains(DirectChild(element, "axId")?.GetAttribute("val") ?? ""))).ToArray() ?? [];
        return axes.Length == 1 && DirectChild(axes[0], "numFmt") is { } numFmt &&
            ReadNumberFormat(numFmt) is { SourceLinked: false } own && !ExcelNumberFormat.IsGeneral(own.Code) ? own.Code : null;
    }

    private static void ReadDataLabels(XmlElement holder, List<string> warnings,
        out bool? showVal, out bool? showPercent, out LabelNumberFormat? format, out string? position,
        out Dictionary<int, PointLabelSource>? points)
    {
        showVal = showPercent = null;
        format = null;
        position = null;
        points = null;
        var dLbls = DirectChild(holder, "dLbls");
        if (dLbls is null) return;
        foreach (var child in dLbls.ChildNodes.OfType<XmlElement>())
        {
            var val = child.GetAttribute("val");
            if (child.LocalName is "showVal" or "showPercent" or "delete" or "showCatName" or "showSerName" or "showLegendKey" or "showBubbleSize" && Flag(val) is null)
                warnings.Add($"Invalid data label flag {child.LocalName}; the setting is not applied.");
            switch (child.LocalName)
            {
                case "showVal": showVal = Flag(val); break;
                case "showPercent": showPercent = Flag(val); break;
                case "delete": if (Flag(val) == true) showVal = showPercent = false; break;
                case "numFmt":
                    format = ReadNumberFormat(child);
                    break;
                case "dLblPos":
                    if (!string.Equals(val, "outEnd", StringComparison.OrdinalIgnoreCase))
                    {
                        position = val.Length <= 128 ? val : null;
                        warnings.Add($"Unsupported data label position \"{position ?? "oversized position"}\"; labels use a basic layout.");
                    }
                    break;
                case "showCatName":
                case "showSerName":
                case "showLegendKey":
                case "showBubbleSize":
                    if (Flag(val) == true)
                        warnings.Add($"Data label content \"{child.LocalName}\" is unsupported; " +
                            "only value and percent labels are drawn.");
                    break;
                case "dLbl":
                    if (!int.TryParse(DirectChild(child, "idx")?.GetAttribute("val"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
                        index < 0 || index >= MaxChartPoints)
                    { warnings.Add("Invalid data label point index; the label setting is not applied."); break; }
                    bool? PointFlag(string name) => DirectChild(child, name) is { } flag ? Flag(flag.GetAttribute("val")) : null;
                    (points ??= [])[index] = new PointLabelSource(PointFlag("delete") == true, PointFlag("showVal"), PointFlag("showPercent"),
                        DirectChild(child, "numFmt") is { } own ? ReadNumberFormat(own) : null);
                    if (DirectChild(child, "tx") is not null && !warnings.Contains(CustomLabelText, StringComparer.Ordinal))
                        warnings.Add(CustomLabelText);
                    if (child.ChildNodes.OfType<XmlElement>().Any(part => part.LocalName is "layout" && part.HasChildNodes || part.LocalName is "spPr" or "txPr" or "dLblPos") &&
                        !warnings.Contains(PointLabelGeometry, StringComparer.Ordinal))
                        warnings.Add(PointLabelGeometry);
                    break;
                case "spPr":
                case "txPr":
                    warnings.Add(PointLabelGeometry);
                    break;
                default:
                    warnings.Add($"Unsupported data label setting {child.LocalName}; basic value/percent labels are retained.");
                    break;
            }
        }
    }

    private const string PointLabelGeometry = "Per-point data label geometry is unsupported; labels use the default layout.";
    private const string CustomLabelText = "Custom data label text is unsupported; the value of the point is shown.";

    private static string? ReadBlankDisplay(XmlDocument document, List<string> warnings)
    {
        var element = document.SelectSingleNode(
            "/*[local-name()='chartSpace']/*[local-name()='chart']/*[local-name()='dispBlanksAs']") as XmlElement;
        if (element is null) return null;
        var val = element.GetAttribute("val");
        if (val is "gap" or "zero" or "span") return val;
        warnings.Add($"Unsupported blank display mode \"{val}\"; blanks are diagnosed and drawn as gaps.");
        return "gap";
    }

    private static (string? Value, string? Category) ReadAxisTitles(XmlDocument document, XmlElement typeElement,
        string type, List<string> warnings)
    {
        var ids = typeElement.ChildNodes.OfType<XmlElement>().Where(element => element.LocalName == "axId")
            .Select(element => element.GetAttribute("val")).ToHashSet(StringComparer.Ordinal);
        XmlElement[] Axes(string names) => document.SelectNodes("//*[" + names + "]")?.OfType<XmlElement>().Where(element =>
            ids.Count == 0 || ids.Contains(DirectChild(element, "axId")?.GetAttribute("val") ?? ""))
            .ToArray() ?? [];
        var valueAxes = Axes("local-name()='valAx'");
        var categoryAxes = Axes("local-name()='catAx' or local-name()='dateAx'");
        if (IsXy(type))
        {
            // Both scatter axes are value axes; the horizontal one takes the place of the category axis.
            categoryAxes = valueAxes.Where(axis => DirectChild(axis, "axPos")?.GetAttribute("val") is "b" or "t").ToArray();
            valueAxes = valueAxes.Except(categoryAxes).ToArray();
        }
        return (ReadAxisTitle(valueAxes, "value", warnings), ReadAxisTitle(categoryAxes, "category", warnings));
    }

    private static string? ReadAxisTitle(XmlElement[] axes, string what, List<string> warnings)
    {
        var withTitle = axes.Where(element => DirectChild(element, "title") is not null).ToArray();
        if (withTitle.Length == 0) return null;
        if (withTitle.Length > 1)
        {
            warnings.Add($"Multiple {what} axis titles are unsupported; the axis title is not drawn.");
            return null;
        }
        var title = DirectChild(withTitle[0], "title")!;
        if (title.SelectNodes(".//*[local-name()='bodyPr']")?.OfType<XmlElement>().Any(p =>
                p.GetAttribute("rot") is not ("" or "0") || p.GetAttribute("vert") is not ("" or "horz")) == true)
            warnings.Add($"Explicit {what} axis title text rotation is unsupported; the title uses the basic axis layout.");
        var paragraphs = title.SelectNodes(".//*[local-name()='rich']/*[local-name()='p']")?.OfType<XmlElement>().ToArray() ?? [];
        var text = string.Join("\n", paragraphs.Select(paragraph =>
            string.Concat(paragraph.SelectNodes(".//*[local-name()='t' or local-name()='br']")!.OfType<XmlElement>()
                .Select(element => element.LocalName == "br" ? "\n" : element.InnerText))));
        if (string.IsNullOrWhiteSpace(text))
        {
            warnings.Add($"The {what} axis title has no readable text; it is not drawn.");
            return null;
        }
        text = text.Trim();
        if (text.Length > 4096)
        {
            warnings.Add($"The {what} axis title exceeds the text limit; consult the original chart.");
            return null;
        }
        return text;
    }



    private static bool ReadSetting(XmlElement chart, string name, double lower, double upper,
        List<string> warnings, out double? value)
    {
        var element = DirectChild(chart, name);
        value = element is null ? null : ParseNumber(element.GetAttribute("val"));
        if (element is null || value is { } number && number >= lower && number <= upper) return true;
        warnings.Add($"Invalid chart {name}; the chart is not drawn.");
        return false;
    }

    private static bool ReadAxisRanges(XmlElement chart, string type, List<string> warnings,
        out double? minimum, out double? maximum, out double? xMinimum, out double? xMaximum,
        out XmlElement? valueAxis, out XmlElement? xAxis, out XmlElement? categoryAxis)
    {
        minimum = maximum = xMinimum = xMaximum = null;
        valueAxis = xAxis = categoryAxis = null;
        if (type is "pie" or "doughnut") return true;
        var ids = chart.ChildNodes.OfType<XmlElement>().Where(element => element.LocalName == "axId")
            .Select(element => element.GetAttribute("val")).ToHashSet(StringComparer.Ordinal);
        XmlElement[] Axes(Func<string, bool> name) => chart.ParentNode?.ChildNodes.OfType<XmlElement>().Where(element => name(element.LocalName) &&
            (ids.Count == 0 || ids.Contains(DirectChild(element, "axId")?.GetAttribute("val") ?? ""))).ToArray() ?? [];
        var categoryAxes = Axes(name => name is "catAx" or "dateAx");
        // A scatter group names no category axis, unless it shares the one of other groups.
        if (categoryAxes.Length == 1 && (!IsXy(type) || ids.Count > 0)) categoryAxis = categoryAxes[0];
        var axes = Axes(name => name == "valAx");
        if (axes.Length == 0) return true;
        var ownX = IsXy(type) && categoryAxis is null;
        var yAxes = ownX ? axes.Where(axis => DirectChild(axis, "axPos")?.GetAttribute("val") is "l" or "r").ToArray() : axes;
        var xAxes = ownX ? axes.Where(axis => DirectChild(axis, "axPos")?.GetAttribute("val") is "b" or "t").ToArray() : [];
        if (yAxes.Length != 1 || ownX && xAxes.Length != 1)
        { warnings.Add("Ambiguous chart value axes; the chart is not drawn."); return false; }
        valueAxis = yAxes[0];
        xAxis = ownX ? xAxes[0] : null;
        return ReadScale(yAxes[0], warnings, out minimum, out maximum) &&
            (!ownX || ReadScale(xAxes[0], warnings, out xMinimum, out xMaximum));
    }

    private static bool ReadScale(XmlElement axis, List<string> warnings, out double? minimum, out double? maximum)
    {
        minimum = maximum = null;
        var scale = DirectChild(axis, "scaling");
        if (scale is null) return true;
        foreach (var child in scale.ChildNodes.OfType<XmlElement>().Where(element => element.LocalName is "min" or "max"))
        {
            var number = ParseNumber(child.GetAttribute("val"));
            if (number is null)
            { warnings.Add("A value axis bound is not finite; the chart is not drawn."); return false; }
            if (child.LocalName == "min") minimum = number; else maximum = number;
        }
        if (minimum is { } min && maximum is { } max && (min >= max || !double.IsFinite(max - min)))
        { warnings.Add("Invalid or overflowing value axis range; the chart is not drawn."); return false; }
        if (DirectChild(scale, "logBase") is not null && (minimum <= 0 || maximum <= 0))
        {
            // Excel accepts only positive bounds on a logarithmic axis.
            warnings.Add("A bound of zero or less on a logarithmic axis is not applied.");
            if (minimum <= 0) minimum = null;
            if (maximum <= 0) maximum = null;
        }
        return true;
    }

    private static bool IsGrouping(string value) => value is "clustered" or "stacked" or "percentStacked" or "standard";

    /// <summary>
    /// The chart groups of the plot area in source order: one, or the groups of a combination chart. Empty when
    /// the chart cannot be drawn.
    /// </summary>
    private static XmlElement[] FindChartGroups(XmlDocument document, List<string> warnings)
    {
        var supported = new List<XmlElement>();
        var unsupported = new List<string>();
        foreach (var element in document.SelectNodes("//*[local-name()='barChart' or local-name()='bar3DChart' or " +
            "local-name()='lineChart' or local-name()='line3DChart' or local-name()='pieChart' or local-name()='pie3DChart' or " +
            "local-name()='doughnutChart' or local-name()='ofPieChart' or local-name()='areaChart' or local-name()='area3DChart' or " +
            "local-name()='scatterChart' or local-name()='bubbleChart' or local-name()='radarChart' or " +
            "local-name()='stockChart' or local-name()='surfaceChart' or local-name()='surface3DChart']")
            ?.OfType<XmlElement>() ?? [])
        {
            if (Supported.ContainsKey(element.LocalName))
            {
                supported.Add(element);
                continue;
            }

            if (!unsupported.Contains(element.LocalName, StringComparer.Ordinal)) unsupported.Add(element.LocalName);
        }

        foreach (var name in unsupported)
            warnings.Add($"Unsupported chart type \"{name}\"; the chart is not drawn.");
        if (supported.Count > 0 && unsupported.Count > 0)
        {
            warnings.Add("A chart that combines an unsupported chart type with other chart groups is not drawn; the complete source chart and saved data must be used.");
            return [];
        }
        return supported.ToArray();
    }

    private static string? ReadTitle(XmlDocument document)
    {
        var chart = document.SelectSingleNode("/*[local-name()='chartSpace']/*[local-name()='chart']") as XmlElement;
        var title = chart is null ? null : DirectChild(chart, "title");
        if (title is null) return null;
        var paragraphs = title.SelectNodes(".//*[local-name()='rich']/*[local-name()='p']")?.OfType<XmlElement>().ToArray() ?? [];
        var text = paragraphs.Length > 0 ? string.Join("\n", paragraphs.Select(paragraph =>
            string.Concat(paragraph.SelectNodes(".//*[local-name()='t' or local-name()='br']")!.OfType<XmlElement>()
                .Select(element => element.LocalName == "br" ? "\n" : element.InnerText)))) :
            string.Concat(title.SelectNodes(".//*[local-name()='v' or local-name()='t']")!.OfType<XmlElement>().Select(element => element.InnerText));
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static bool ReadLegend(XmlDocument document, List<string> warnings, out string? position)
    {
        position = null;
        var legend = document.SelectSingleNode("//*[local-name()='legend']") as XmlElement;
        if (legend is null) return false;
        // ST_LegendPos: b, tr, l, r (the default) or t.
        var place = DirectChild(legend, "legendPos")?.GetAttribute("val");
        if (place is "l" or "t" or "b" or "tr") position = place;
        else if (place is not (null or "" or "r")) warnings.Add("Unknown legend position; the legend is drawn on the right.");
        var deleted = DirectChild(legend, "delete")?.GetAttribute("val");
        return deleted is not ("1" or "true");
    }

    private static DrawingChartSeries ReadSeries(XmlElement series, bool isScatter, string? categoryFormat,
        bool date1904, List<string> warnings, out string? valueFormat, out string? xFormat,
        out IReadOnlyDictionary<int, string> pointFormats)
    {
        var nameElement = DirectChild(series, "tx");
        var name = nameElement is null ? string.Empty : string.Concat(Descendants(nameElement, "t", "v")
            .Select(node => node.InnerText)).Trim();

        var color = ReadColor(series);
        var categories = ReadCategories(series, isScatter ? "cat" : "cat", categoryFormat, date1904, warnings);
        var valueElement = DirectChild(series, isScatter ? "yVal" : "val");
        var values = ReadNumbers(valueElement, "values", warnings);
        valueFormat = CacheFormat(valueElement);
        pointFormats = PointFormats(valueElement);
        // Office places the points of a scatter series without numeric x values (none, or text) at 1, 2, 3, ...
        var xElement = isScatter ? DirectChild(series, "xVal") : null;
        var ordinalX = isScatter && values.Count > 0 &&
            (xElement is null || UnwrapCache(xElement)?.LocalName is "strRef" or "strCache" or "strLit");
        if (ordinalX)
            warnings.Add("Scatter series has no numeric x values; its points are placed at 1, 2, 3, ... as Office does.");
        var xValues = ordinalX ? Enumerable.Range(1, values.Count).Select(position => (double?)position).ToArray() :
            isScatter ? ReadNumbers(xElement, "x values", warnings) : [];
        xFormat = ordinalX ? null : CacheFormat(xElement);

        if (values.Count > 0 && xValues.Count > 0 && values.Count != xValues.Count)
            warnings.Add($"Series x values and y values have different counts ({xValues.Count} and {values.Count}); " +
                "the shorter range is used.");

        var pointColors = new Dictionary<int, string>();
        foreach (var point in series.ChildNodes.OfType<XmlElement>().Where(element => element.LocalName == "dPt"))
        {
            if (!int.TryParse(DirectChild(point, "idx")?.GetAttribute("val"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
                index < 0 || index >= MaxChartPoints)
            { warnings.Add("Invalid chart point color index; the color is not applied."); continue; }
            var pointColor = ReadColor(point);
            if (pointColor.Length > 0) pointColors[index] = pointColor;
        }
        return new DrawingChartSeries(name.Length == 0 ? null : name, categories, values, xValues, color)
        { PointColors = pointColors };
    }

    /// <summary>The c:formatCode attributes of single points of a numeric cache, by point index.</summary>
    private static Dictionary<int, string> PointFormats(XmlElement? element)
    {
        var formats = new Dictionary<int, string>();
        if (element is null || UnwrapCache(element) is not { LocalName: "numCache" or "numLit" } cache) return formats;
        foreach (var point in cache.ChildNodes.OfType<XmlElement>().Where(child => child.LocalName == "pt"))
            if (point.GetAttribute("formatCode") is { Length: > 0 } code && PointIndex(point) is >= 0 and < MaxChartPoints and var index)
                formats[index] = code;
        return formats;
    }

    private static string ReadColor(XmlElement series)
    {
        var properties = DirectChild(series, "spPr") ?? series;
        var fill = DirectChild(properties, "solidFill");
        if (fill is null && DirectChild(properties, "gradFill") is { } gradient)
        {
            // A gradient is drawn as one color: the color halfway along it, or its first theme color.
            var stops = DirectChild(gradient, "gsLst")?.ChildNodes.OfType<XmlElement>().Where(stop => stop.LocalName == "gs")
                .Select(stop => (Position: ParseNumber(stop.GetAttribute("pos")) ?? 0, Stop: stop))
                .OrderBy(stop => stop.Position).ToArray() ?? [];
            if (stops.Length == 0) return string.Empty;
            var colors = stops.Select(stop => DirectChild(stop.Stop, "srgbClr")?.GetAttribute("val") is { Length: 6 } value &&
                int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb) ? rgb : -1).ToArray();
            if (colors.Any(rgb => rgb < 0)) fill = stops[0].Stop;
            else
            {
                var after = Array.FindIndex(stops, stop => stop.Position >= 50000);
                if (after < 0) after = stops.Length - 1;
                var before = Math.Max(0, stops[after].Position > 50000 ? after - 1 : after);
                var span = stops[after].Position - stops[before].Position;
                var share = span > 0 ? (50000 - stops[before].Position) / span : 0;
                int Mix(int shift) => (int)Math.Round(((colors[before] >> shift) & 255) * (1 - share) + ((colors[after] >> shift) & 255) * share);
                return "#" + ((Mix(16) << 16) | (Mix(8) << 8) | Mix(0)).ToString("X6", CultureInfo.InvariantCulture);
            }
        }
        var color = fill is null ? null : DirectChild(fill, "srgbClr");
        if (color is not null)
        {
            var value = color.GetAttribute("val");
            if (value.Length == 6 && int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                return "#" + value.ToUpperInvariant();
        }

        var theme = fill is null ? null : DirectChild(fill, "schemeClr");
        var themeValue = theme?.GetAttribute("val") ?? string.Empty;
        return themeValue.Length == 0 ? string.Empty : "scheme:" + themeValue;
    }

    /// <summary>The c:formatCode of the numeric cache below a c:val, c:xVal or c:cat element.</summary>
    private static string? CacheFormat(XmlElement? element) =>
        element is not null && UnwrapCache(element) is { LocalName: "numCache" or "numLit" } cache
            ? DirectChild(cache, "formatCode")?.InnerText.Trim()
            : null;

    private static IReadOnlyList<string> ReadCategories(XmlElement series, string childName, string? axisFormat,
        bool date1904, List<string> warnings)
    {
        var element = DirectChild(series, childName);
        if (element is null) return [];
        var cache = UnwrapCache(element);
        if (cache is null) return [];
        if (cache.LocalName is "numCache" or "numLit")
        {
            // Numeric categories (dates, years, amounts) are shown as the worksheet shows them: with the explicit
            // format of the axis, else the format of the point, else the format of the cache.
            var cacheFormat = DirectChild(cache, "formatCode")?.InnerText.Trim();
            return ReadStringPoints(cache, DeclaredCount(cache), (point, raw) =>
            {
                var code = axisFormat ?? (point.GetAttribute("formatCode") is { Length: > 0 } own ? own : cacheFormat);
                return ParseNumber(raw) is { } number && UsableFormat(code, "category", [number], date1904, warnings) is { } usable
                    ? ExcelNumberFormat.Format(number, usable, date1904) ?? raw
                    : raw;
            }).Values;
        }
        // A multi-level cache holds one c:lvl per level, innermost first: that level has one label per point.
        if (cache.LocalName is "multiLvlStrCache" or "multiLvlStrLit" && DirectChild(cache, "lvl") is { } level)
            return ReadStringPoints(level, DeclaredCount(cache)).Values;
        var (values, _) = ReadStringPoints(cache, DeclaredCount(cache));
        return values;
    }

    private static (IReadOnlyList<string> Values, bool Truncated) ReadStringPoints(XmlElement cache, int declared,
        Func<XmlElement, string, string>? display = null)
    {
        var points = cache.ChildNodes.OfType<XmlElement>().Where(child => child.LocalName == "pt").ToArray();
        if (points.Length == 0)
        {
            var text = string.Concat(Descendants(cache, "v", "t").Select(node => node.InnerText).Select(value => value.Trim()))
                .Trim();
            return ([text], false);
        }

        if (points.Length > MaxChartPoints) return ([], true);
        var count = Math.Max(declared, MaxIndex(points) + 1);
        if (count > MaxChartPoints) return ([], true);
        var values = Enumerable.Repeat(string.Empty, count).ToArray();
        foreach (var point in points)
        {
            var index = PointIndex(point);
            if (index < 0 || index >= count) continue;
            values[index] = string.Concat(Descendants(point, "v", "t").Select(child => child.InnerText));
            if (display is not null) values[index] = display(point, values[index]);
        }

        return (values, false);
    }

    private static IReadOnlyList<double?> ReadNumbers(XmlElement? element, string what, List<string> warnings)
    {
        if (element is null) return [];
        var cache = UnwrapCache(element) ?? element;
        var declared = DeclaredCount(cache);
        var points = cache.ChildNodes.OfType<XmlElement>().Where(child => child.LocalName == "pt").ToArray();
        if (points.Length == 0)
        {
            // A cache without point elements lists its values in document order.
            var literals = Descendants(cache, "v").Select(node => node.InnerText.Trim()).ToArray();
            if (declared > MaxChartPoints || literals.Length > MaxChartPoints)
            {
                warnings.Add($"Chart {what} declare more than {MaxChartPoints} points; the series is not read.");
                return [];
            }

            var single = new double?[Math.Max(declared, literals.Length)];
            for (var index = 0; index < literals.Length && index < single.Length; index++)
            {
                single[index] = ParseNumber(literals[index]);
                if (single[index] is null && literals[index].Length > 0)
                    warnings.Add($"Chart {what} point {index} is not a finite number; it is a gap.");
            }

            return single;
        }

        if (declared > MaxChartPoints || MaxIndex(points) + 1 > MaxChartPoints)
        {
            warnings.Add($"Chart {what} declare more than {MaxChartPoints} points; the series is not read.");
            return [];
        }

        var count = Math.Max(declared, MaxIndex(points) + 1);
        var values = new double?[count];
        foreach (var point in points)
        {
            var index = PointIndex(point);
            if (index < 0 || index >= count)
            {
                warnings.Add($"Chart {what} hold an invalid point index; the point is a gap.");
                continue;
            }

            var raw = string.Concat(Descendants(point, "v").Select(child => child.InnerText)).Trim();
            var parsed = ParseNumber(raw);
            if (parsed is null && raw.Length > 0)
                warnings.Add($"Chart {what} point {index} is not a finite number; it is a gap.");
            values[index] = parsed;
        }

        return values;
    }

    private static IEnumerable<XmlElement> Descendants(XmlElement element, params string[] localNames) =>
        element.SelectNodes(".//*")?.OfType<XmlElement>()
            .Where(node => localNames.Contains(node.LocalName, StringComparer.Ordinal)) ?? [];

    /// <summary>Walks ref/cache wrappers down to the element that actually holds the pt children.</summary>
    private static XmlElement? UnwrapCache(XmlElement element)
    {
        var current = element;
        for (var depth = 0; depth < 4 && current is not null; depth++)
        {
            var next = current.ChildNodes.OfType<XmlElement>().FirstOrDefault(child => child.LocalName is
                "numRef" or "strRef" or "numLit" or "strLit" or "numCache" or "strCache" or
                "multiLvlStrRef" or "multiLvlStrCache" or "multiLvlStrLit");
            if (next is null) return current;
            current = next;
        }

        return current;
    }

    private static double? ParseNumber(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return null;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
            return value;
        return null;
    }

    private static int DeclaredCount(XmlElement cache)
    {
        var declared = 0;
        foreach (var element in cache.ChildNodes.OfType<XmlElement>().Where(child => child.LocalName == "ptCount"))
        {
            if (!int.TryParse(element.GetAttribute("val"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
                value < 0)
            {
                continue;
            }

            declared = Math.Max(declared, value);
        }

        return declared;
    }

    private static int MaxIndex(XmlElement[] points) => points.Length == 0 ? -1 : points.Max(PointIndex);

    private static int PointIndex(XmlElement point) =>
        int.TryParse(point.GetAttribute("idx"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0
            ? index
            : -1;

    private static XmlElement? DirectChild(XmlElement element, string localName) =>
        element.ChildNodes.OfType<XmlElement>().FirstOrDefault(child => child.LocalName == localName);

    private static string Invalid(string what, Exception exception) =>
        $"Could not parse the {what}: {exception.GetType().Name}.";
}
