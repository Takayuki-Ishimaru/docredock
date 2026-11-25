using System.Text;
using DocRedock.Core.Documents;

namespace DocRedock.Render;

public static partial class DrawingChartSvg
{
    /// <summary>A legend entry: a series, or one point of a pie or doughnut series.</summary>
    private sealed record LegendItem(string Label, int ColorIndex, string[] Lines)
    {
        public int Point { get; init; } = -1;
    }

    /// <summary>
    /// What the layout leaves room for around the plot: the tick labels of a vertical axis on the left and on the
    /// right, the category labels ("bottom", "top", "left" or "none"), and a row of tick labels of a horizontal
    /// value axis below or above.
    /// </summary>
    private sealed record Bands(string[] Left, string[] Right, string Categories, bool TicksBelow, bool TicksAbove)
    {
        /// <summary>The entries of the legend where it does not name the series: the value bands of a surface chart.</summary>
        public string[]? Legend { get; init; }
    }

    private sealed record Decoration(string[] Title, LegendItem[] Legend, string[][] Categories,
        double LegendX, bool Horizontal)
    {
        public bool CategoriesOnTop { get; init; }

        /// <summary>Middle of the band of the sideways title of the vertical axis.</summary>
        public double SideTitleX { get; init; } = 16;
        public string[] CategoryTitle { get; init; } = [];
        public double CategoryLabelHeight { get; init; }
        public double TitleX { get; init; } = 8;
        public double TitleY { get; init; } = 8;

        /// <summary>Top left corner of each legend entry.</summary>
        public (double X, double Y)[] LegendAt { get; init; } = [];

        /// <summary>Room for a tick label of the value axis, and of the secondary value axis on the right.</summary>
        public double TickWidth { get; init; } = 56;
        public double SecondaryTickWidth { get; init; }
        public string[] SecondaryTitle { get; init; } = [];
    }

    /// <summary>The share of the chart width that the labels of the vertical axis may take.</summary>
    private const double MaxGutterShare = .4;

    private static bool TryCreatePlot(DrawingChart chart, double width, double height, Bands bands, out Plot plot)
    {
        plot = new(0, 0);
        // A pie, a doughnut and a radar chart have no axes beside the plot.
        var pie = chart.Type is "pie" or "doughnut" or "radar";
        var horizontal = bands.Categories == "left";
        var slots = Slots(chart);
        if (slots is <= 0 or > MaxPoints || chart.Title?.Length > 4096 || chart.ValueAxisTitle?.Length > 4096 ||
            chart.CategoryAxisTitle?.Length > 4096 || chart.SecondaryAxisTitle?.Length > 4096) return false;
        var title = Wrap(chart.Title ?? "", Math.Max(1, (width - 16) / 14));
        double top = 8 + (title.Length > 0 ? title.Length * 16 + 8 : 0);
        // A manual layout (c:manualLayout) places the title and the legend by fractions of the chart size.
        static double Place(double? value, bool offset, double automatic, double size) =>
            value is not { } fraction ? automatic : offset ? automatic + fraction * size : fraction * size;
        var titleX = Math.Clamp(Place(chart.TitleLayout?.X, chart.TitleLayout?.XIsOffset == true, 8, width), 0, width - 16);
        var titleY = Math.Clamp(Place(chart.TitleLayout?.Y, chart.TitleLayout?.YIsOffset == true, 8, height), 0, height - 16);
        var legendWidth = chart.ShowLegend ? Math.Clamp(width * .25, 76, 160) : 0;
        var legendX = Math.Clamp(Place(chart.LegendLayout?.X, chart.LegendLayout?.XIsOffset == true, width - 8 - legendWidth, width), 0, width - 16);
        var legendY = Math.Clamp(Place(chart.LegendLayout?.Y, chart.LegendLayout?.YIsOffset == true, top, height), 0, height - 12);
        var legendSpan = chart.LegendLayout is { Width: { } manualWidth } manualLegend
            ? Math.Clamp(manualLegend.WidthIsRight ? manualWidth * width - legendX : manualWidth * width, 40, width)
            : legendWidth;
        var legend = new List<LegendItem>();
        if (chart.ShowLegend && bands.Legend is { } entries)
        {
            for (var i = 0; i < entries.Length; i++)
                legend.Add(new(entries[i], i, Fit(Wrap(entries[i], Math.Max(1, (legendSpan - 18) / 10)), 2)));
        }
        else if (chart.ShowLegend)
        {
            var rings = Enumerable.Range(0, chart.Series.Count).Where(index => Kind(chart, chart.Series[index]) is "pie" or "doughnut").ToArray();
            for (var i = 0; i < chart.Series.Count; i++)
            {
                var name = chart.Series[i].Name;
                if (rings.Contains(i) || string.IsNullOrWhiteSpace(name)) continue;
                if (name.Length > 4096) return false;
                legend.Add(new(name, i, Fit(Wrap(name, Math.Max(1, (legendSpan - 18) / 10)), 2)));
            }
            if (rings.Length > 0)
            {
                // The legend of a pie or doughnut names the points; every ring of a doughnut shares them.
                var points = Kind(chart, chart.Series[rings[0]]) == "pie" ? chart.Series[rings[0]].Values.Count : rings.Max(index => chart.Series[index].Values.Count);
                for (var i = 0; i < points; i++)
                {
                    var name = rings.Select(index => i < chart.Series[index].Categories.Count ? chart.Series[index].Categories[i] : "")
                        .FirstOrDefault(label => !string.IsNullOrWhiteSpace(label)) ?? Category(chart, i);
                    if (name.Length > 4096) return false;
                    legend.Add(new(name, rings[0], Fit(Wrap(name, Math.Max(1, (legendSpan - 18) / 10)), 2)) { Point = i });
                }
            }
        }
        if (legend.Count == 0) legendWidth = 0;
        if (chart.LegendLayout is null) legendX = width - 8 - legendWidth;
        var right = legendWidth > 0 ? width - 8 - legendWidth - 8 : width - 8;
        // c:legendPos without a manual layout: "r" and "tr" keep the column on the right; "l" moves it to the left
        // edge; "t" and "b" list the entries in centred rows above or below the plot, as Excel does.
        var position = chart.LegendLayout is null && legend.Count > 0 ? chart.LegendPosition : null;
        var leftEdge = 8d;
        var bottom = height - 8;
        List<(double X, double Y)>? legendRows = null;
        if (position == "l")
        {
            legendX = 8;
            leftEdge = 8 + legendWidth + 8;
            right = width - 8;
        }
        else if (position is "t" or "b")
        {
            right = width - 8;
            // An entry is a swatch, its name on one line and a gap; at most two rows, the rest is counted.
            var rows = new List<List<LegendItem>> { new() };
            var used = 0d;
            double Entry(LegendItem item) => TextWidth(item.Lines[0], 10) + 24;
            foreach (var item in legend.Select(item => item with { Lines = [FitWidth(item.Label, width - 40, 10)] }))
            {
                if (rows[^1].Count > 0 && used + Entry(item) > width - 16) { rows.Add(new()); used = 0; }
                rows[^1].Add(item);
                used += Entry(item);
            }
            if (rows.Count > 2)
            {
                var kept = rows[0].Count + rows[1].Count - 1;
                rows = [rows[0], [.. rows[1][..^1], new("", -1, ["\u2026 +" + (legend.Count - kept).ToString(System.Globalization.CultureInfo.InvariantCulture)])]];
            }
            var rowsHeight = rows.Count * 16 + 4;
            var rowY = position == "t" ? top : height - 8 - rowsHeight + 4;
            if (position == "t") top += rowsHeight; else bottom -= rowsHeight;
            legend = rows.SelectMany(row => row).ToList();
            legendRows = [];
            foreach (var row in rows)
            {
                var x = Math.Max(8, (width - row.Sum(Entry)) / 2);
                foreach (var item in row)
                {
                    legendRows.Add((x, rowY));
                    x += Entry(item);
                }
                rowY += 16;
            }
        }
        // The secondary value axis of a combination chart has its tick labels, and its title, right of the plot.
        var secondaryTickWidth = 0d;
        var secondaryGutter = 0d;
        var secondaryTitle = bands.Right.Length > 0 && !string.IsNullOrWhiteSpace(chart.SecondaryAxisTitle);
        if (bands.Right.Length > 0)
        {
            secondaryTickWidth = Math.Min(Math.Max(28, bands.Right.Max(tick => TextWidth(tick, 9))), Math.Max(28, width * .2));
            secondaryGutter = secondaryTickWidth + 8 + (secondaryTitle ? 20 : 0);
            right -= secondaryGutter;
        }
        var names = new string[bands.Categories == "none" ? 0 : slots];
        for (var i = 0; i < names.Length; i++)
            if ((names[i] = Category(chart, i)).Length > 4096) return false;
        // The gutter of the vertical axis holds the tick labels (the category labels of horizontal bars). It grows
        // with the widest label, measured as the rest of the layout measures text, up to a share of the chart
        // width; only a label that is still longer ends in an ellipsis.
        // An axis without tick labels on the left (deleted, or on the right) needs no gutter.
        var gutter = pie ? 0 : horizontal ? Math.Min(112, width * .27) : bands.Left.Length > 0 ? 60 : 0;
        if (!pie && (horizontal || bands.Left.Length > 0))
        {
            var wanted = horizontal ? names.Max(name => TextWidth(name, 10)) + 8 : bands.Left.Select(tick => TextWidth(tick, 9)).DefaultIfEmpty(0).Max() + 4;
            gutter = Math.Max(gutter, Math.Min(wanted, width * MaxGutterShare - 8));
        }
        var left = leftEdge + gutter;
        var axisTitle = !pie && !string.IsNullOrWhiteSpace(chart.ValueAxisTitle);
        var categoryTitle = !pie && !string.IsNullOrWhiteSpace(chart.CategoryAxisTitle);
        // The title of the vertical axis is written sideways in a band of its own on the left.
        var sideTitle = horizontal ? categoryTitle : axisTitle;
        if (sideTitle) left += 28;
        var labelWidth = gutter - 4;
        double? fixedBottom = null;
        if (chart.PlotLayout is { } manual)
        {
            // The outer rectangle includes the tick labels and axis titles of the vertical axes; the inner one
            // (c:layoutTarget inner) is the plot itself, and its labels use the room that is left outside it.
            var leftGutter = manual.Inner ? 0 : left - leftEdge;
            var rightGutter = manual.Inner ? 0 : secondaryGutter;
            var boxLeft = Place(manual.X, manual.XIsOffset, left - leftGutter, width);
            var boxRight = manual.Width is not { } w ? right + rightGutter : manual.WidthIsRight ? w * width : boxLeft + w * width;
            left = Math.Clamp(boxLeft + leftGutter, 0, width);
            right = Math.Clamp(boxRight - rightGutter, 0, width);
            var boxTop = Math.Clamp(Place(manual.Y, manual.YIsOffset, top, height), 0, height);
            if (manual.Height is { } h)
            {
                var boxBottom = Math.Clamp(manual.HeightIsBottom ? h * height : boxTop + h * height, 0, height);
                if (manual.Inner) fixedBottom = boxBottom; else bottom = boxBottom;
            }
            top = boxTop;
            labelWidth = Math.Max(0, left - (leftEdge - 8) - (sideTitle ? 28 : 0) - 4);
        }
        // The labels above the plot: the categories of a category axis at the top, or the ticks of a horizontal
        // value axis there. A manual inner layout fixes the plot; its labels use the room around it.
        var above = chart.PlotLayout is { Inner: true } ? 0 : bands.TicksAbove ? 16d : 0;
        var plotWidth = right - left;
        if (plotWidth < 24) return false;
        var categories = Array.Empty<string[]>();
        if (axisTitle && horizontal) bottom -= 36;
        var categoryTitleLines = Array.Empty<string>();
        if (categoryTitle && !horizontal)
        {
            categoryTitleLines = Fit(Wrap(chart.CategoryAxisTitle!, Math.Max(1, plotWidth / 11)), 2);
            bottom -= categoryTitleLines.Length * 13 + 4;
        }
        var categoryLabelHeight = 20d;
        if (bands.Categories != "none")
        {
            // A crowded axis shows every n-th label and a long label ends in an ellipsis, as Office does; the
            // full text of every category stays in the tooltip of its mark and in the saved data.
            categories = new string[slots][];
            var step = 1;
            int lines;
            double available;
            if (horizontal)
            {
                bottom = fixedBottom ?? bottom - (bands.TicksBelow ? 20 : 0);
                var band = (bottom - top - above) / slots;
                if (band <= 0) return false;
                while (step < slots && step * band < 12) step++;
                lines = Math.Max(1, (int)((step * band - 2) / 12));
                available = left - leftEdge - 8 - (sideTitle ? 28 : 0);
            }
            else
            {
                var band = plotWidth / slots;
                // Room for the widest label on one line, up to 48 px; a longer label wraps inside that width.
                var wanted = Math.Min(48, names.Max(name => name.Sum(Advance)) * 10);
                while (step < slots && step * band - 4 < wanted) step++;
                lines = Math.Clamp((int)(height * .3 / 12), 1, 4);
                available = step * band - 4;
            }
            for (var i = 0; i < slots; i++)
                categories[i] = i % step == 0 ? Fit(Wrap(names[i], Math.Max(1, available / 10)), lines) : [];
            if (!horizontal)
            {
                categoryLabelHeight = categories.Max(label => label.Length) * 12 + 8;
                if (bands.Categories == "bottom") bottom -= categoryLabelHeight;
                else if (chart.PlotLayout is not { Inner: true }) above += categoryLabelHeight;
            }
        }
        else if (!pie && bands.TicksBelow) bottom -= 20;
        // The title of the horizontal axis follows the labels below the plot.
        if (bands.Categories != "bottom") categoryLabelHeight = bands.TicksBelow ? 20 : 0;
        top += above;
        bottom = fixedBottom ?? bottom;
        var plotHeight = bottom - top;
        if (axisTitle && Wrap(chart.ValueAxisTitle!, Math.Max(1, (horizontal ? plotWidth : plotHeight) / 11)).Length > 2) return false;
        if (plotHeight < 24) return false;
        if (horizontal && categoryTitle) categoryTitleLines = Fit(Wrap(chart.CategoryAxisTitle!, Math.Max(1, plotHeight / 11)), 2);
        // A legend longer than its room (the plot height, unless it has a manual layout) keeps the entries that
        // fit and counts the rest. A wide manual legend that is too low for one column lists its entries in rows.
        var legendRoom = chart.LegendLayout is { Height: { } manualHeight } ? Math.Max(16, manualHeight * height)
            : chart.LegendLayout is null ? plotHeight : Math.Max(16, height - legendY - 4);
        var columns = 1;
        var columnWidth = legendSpan;
        if (chart.LegendLayout is { Width: not null } && legend.Count > 1 && legend.Sum(item => item.Lines.Length * 12 + 4) > legendRoom)
        {
            columnWidth = Math.Min(legendSpan, legend.Max(item => TextWidth(item.Label, 10)) + 22);
            columns = Math.Clamp((int)(legendSpan / columnWidth), 1, legend.Count);
        }
        var legendAt = legendRows ?? [];
        if (legendRows is null && columns > 1)
        {
            var capacity = Math.Max(1, (int)(legendRoom / 16)) * columns;
            var hidden = legend.Count > capacity ? legend.Count - capacity + 1 : 0;
            legend = legend.Take(legend.Count - hidden)
                .Select(item => item with { Lines = Fit(Wrap(item.Label, Math.Max(1, (columnWidth - 18) / 10)), 1) }).ToList();
            if (hidden > 0)
                legend.Add(new("", -1, ["\u2026 +" + hidden.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
            for (var i = 0; i < legend.Count; i++)
                legendAt.Add((legendX + i % columns * columnWidth, legendY + i / columns * 16));
        }
        else if (legendRows is null)
        {
            var shown = 0;
            var legendHeight = 0d;
            while (shown < legend.Count && legendHeight + legend[shown].Lines.Length * 12 + 4 <= legendRoom)
                legendHeight += legend[shown++].Lines.Length * 12 + 4;
            if (shown < legend.Count)
            {
                while (shown > 0 && legendHeight + 16 > legendRoom) legendHeight -= legend[--shown].Lines.Length * 12 + 4;
                var hidden = legend.Count - shown;
                legend.RemoveRange(shown, hidden);
                legend.Add(new("", -1, ["\u2026 +" + hidden.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
            }
            var y = legendY;
            foreach (var item in legend)
            {
                legendAt.Add((legendX, y));
                y += item.Lines.Length * 12 + 4;
            }
        }
        plot = new(plotWidth, plotHeight, left, top)
        {
            Decoration = new(title, legend.ToArray(), categories, legendX, horizontal)
            {
                CategoriesOnTop = bands.Categories == "top", SideTitleX = leftEdge + 8,
                CategoryTitle = categoryTitleLines, CategoryLabelHeight = categoryLabelHeight,
                TitleX = titleX, TitleY = titleY, LegendAt = legendAt.ToArray(),
                TickWidth = labelWidth, SecondaryTickWidth = secondaryTickWidth,
                SecondaryTitle = secondaryTitle ? Fit(Wrap(chart.SecondaryAxisTitle!, Math.Max(1, plotHeight / 11)), 2) : [],
            },
        };
        return true;
    }

    private static string Category(DrawingChart chart, int index) =>
        chart.Series.Select(series => index < series.Categories.Count ? series.Categories[index] : "")
            .FirstOrDefault(label => !string.IsNullOrWhiteSpace(label)) ?? (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Width of one character in em: East Asian and fullwidth characters fill the em, others about 0.6 of it.</summary>
    private static double Advance(char value) => value >= '\u2E80' ? 1 : .6;

    /// <summary>Breaks text into lines of at most <paramref name="ems"/> em, at a space where the line has one.</summary>
    private static string[] Wrap(string text, double ems)
    {
        if (text.Length == 0) return [];
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var remaining = paragraph;
            while (true)
            {
                var used = 0d;
                var cut = 0;
                while (cut < remaining.Length && (used += Advance(remaining[cut])) <= ems + 1e-9) cut++;
                if (cut == remaining.Length) break;
                cut = Math.Max(1, cut);
                var space = remaining[cut] == ' ' ? cut : remaining.LastIndexOf(' ', cut - 1, cut);
                if (space > 0) cut = space;
                lines.Add(remaining[..cut]);
                remaining = remaining[cut..].TrimStart(' ');
            }
            if (remaining.Length > 0) lines.Add(remaining);
        }
        return lines.ToArray();
    }

    /// <summary>Estimated width of a line of text in pixels at the given font size.</summary>
    private static double TextWidth(string text, double size) => text.Sum(Advance) * size;

    /// <summary>The text, or its start with an ellipsis when it is wider than <paramref name="width"/> pixels.</summary>
    private static string FitWidth(string text, double width, double size)
    {
        if (TextWidth(text, size) <= width + 1e-9) return text;
        var kept = text.Length;
        while (kept > 0 && (TextWidth(text[..kept], size) + .6 * size > width || char.IsHighSurrogate(text[kept - 1]))) kept--;
        return text[..kept].TrimEnd() + "\u2026";
    }

    /// <summary>Keeps the first lines and marks the cut with an ellipsis.</summary>
    private static string[] Fit(string[] lines, int maximum)
    {
        if (lines.Length <= maximum) return lines;
        var last = lines[maximum - 1].TrimEnd();
        return [.. lines[..(maximum - 1)], (last.Length > 1 ? last[..^1] : last) + "\u2026"];
    }

    private static void WriteTitle(StringBuilder svg, DrawingChart chart, Plot plot)
    {
        if (plot.Decoration is not { } layout) return;
        for (var i = 0; i < layout.Title.Length; i++)
            Text(svg, "title", layout.TitleX, layout.TitleY + 14 + i * 16, layout.Title[i], 14, fill: Ink(chart, chart.TitleColor));
    }

    private static void WriteLabels(StringBuilder svg, DrawingChart chart, Plot plot)
    {
        if (plot.Decoration is not { } layout) return;
        for (var entry = 0; entry < layout.Legend.Length; entry++)
        {
            var item = layout.Legend[entry];
            var (x, y) = layout.LegendAt[entry];
            if (item.ColorIndex >= 0)
                svg.Append("<rect data-chart-label=\"legend-swatch\" x=\"").Append(Num(x))
                    .Append("\" y=\"").Append(Num(y)).Append("\" width=\"8\" height=\"8\" fill=\"")
                    .Append(chart.Type == "surface" ? BandColor(chart, item.ColorIndex)
                        : item.Point >= 0 ? Paint(chart, plot, item.ColorIndex, item.Point, chart.VaryColors == false ? item.ColorIndex : item.Point)
                        : plot.Paints.Use(chart.Series[item.ColorIndex].FillPaint, Fill(chart, item.ColorIndex))).Append("\"/>");
            for (var i = 0; i < item.Lines.Length; i++)
                Text(svg, "legend", x + 14, y + 9 + i * 12, item.Lines[i], 10, fill: Ink(chart, chart.LegendColor));
        }
        var ink = Ink(chart, null);
        var categoryInk = Ink(chart, Xy(chart) ? null : chart.CategoryAxisOptions?.TextColor);
        // Horizontal bars list their categories from the bottom, as Excel does; a reversed axis turns the order.
        var flip = layout.Horizontal != plot.Sides.ReverseCategories;
        if (layout.SecondaryTitle.Length > 0)
        {
            svg.Append("<g transform=\"translate(").Append(Num(plot.OriginX + plot.Width + layout.SecondaryTickWidth + 20)).Append(',')
                .Append(Num(plot.OriginY + plot.Height / 2)).Append(") rotate(-90)\">");
            for (var i = 0; i < layout.SecondaryTitle.Length; i++)
                Text(svg, "secondary-axis-title", 0, i * 13, layout.SecondaryTitle[i], 11, "middle", ink);
            svg.Append("</g>");
        }
        for (var i = 0; i < layout.Categories.Length; i++)
        {
            var lines = layout.Categories[i];
            var slot = flip ? layout.Categories.Length - 1 - i : i;
            for (var k = 0; k < lines.Length; k++)
            {
                if (layout.Horizontal)
                    Text(svg, "category", plot.OriginX - 8,
                        plot.OriginY + plot.Height * (slot + .5) / layout.Categories.Length -
                        lines.Length * 6 + 9 + k * 12, lines[k], 10, "end", categoryInk);
                else
                    Text(svg, "category", chart.Type == "bar" ? plot.OriginX + plot.Width * (slot + .5) / layout.Categories.Length : plot.PointX(slot, layout.Categories.Length),
                        layout.CategoriesOnTop ? plot.OriginY - 6 - (lines.Length - 1 - k) * 12 : plot.OriginY + plot.Height + 14 + k * 12, lines[k], 10, "middle", categoryInk);
            }
        }
        if (layout.CategoryTitle.Length == 0) return;
        if (layout.Horizontal)
            svg.Append("<g transform=\"translate(").Append(Num(layout.SideTitleX)).Append(',').Append(Num(plot.OriginY + plot.Height / 2)).Append(") rotate(-90)\">");
        for (var i = 0; i < layout.CategoryTitle.Length; i++)
            Text(svg, "category-axis-title", layout.Horizontal ? 0 : plot.OriginX + plot.Width / 2,
                layout.Horizontal ? i * 13 : plot.OriginY + plot.Height + layout.CategoryLabelHeight + 12 + i * 13,
                layout.CategoryTitle[i], 11, "middle", ink);
        if (layout.Horizontal) svg.Append("</g>");
    }

    private static void Text(StringBuilder svg, string kind, double x, double y, string text,
        double size, string anchor = "start", string fill = "#222") =>
        svg.Append("<text data-chart-label=\"").Append(kind).Append("\" x=\"").Append(Num(x))
            .Append("\" y=\"").Append(Num(y)).Append("\" font-size=\"").Append(Num(size))
            .Append("\" text-anchor=\"").Append(anchor).Append("\" fill=\"").Append(fill).Append("\">").Append(Esc(text)).Append("</text>");

    private static string DataLabel(DrawingChart chart, double x, double y, string text, string anchor) =>
        "<text data-chart-label=\"data\" x=\"" + Num(x) + "\" y=\"" + Num(y) +
        "\" font-size=\"10\" text-anchor=\"" + anchor + "\" fill=\"" + Ink(chart, null, onPlot: true) + "\">" + Esc(text) + "</text>";

    private static void WriteValueAxisTitle(StringBuilder svg, DrawingChart chart, Plot plot)
    {
        if (string.IsNullOrWhiteSpace(chart.ValueAxisTitle) || chart.Type is "pie" or "doughnut" or "radar" or "surface") return;
        var horizontal = chart.Type == "bar" && chart.Horizontal;
        var lines = Wrap(chart.ValueAxisTitle, Math.Max(1, (horizontal ? plot.Width : plot.Height) / 11));
        if (!horizontal)
            svg.Append("<g transform=\"translate(").Append(Num(plot.Decoration?.SideTitleX ?? 16)).Append(',').Append(Num(plot.OriginY + plot.Height / 2)).Append(") rotate(-90)\">");
        for (var index = 0; index < lines.Length; index++)
            Text(svg, "axis-title", horizontal ? plot.OriginX + plot.Width / 2 : 0,
                horizontal ? plot.OriginY + plot.Height + 34 + index * 13 : index * 13, lines[index], 11, "middle", Ink(chart, null));
        if (!horizontal) svg.Append("</g>");
    }

    private static double? BlankValue(double? value, string? mode) =>
        value is null && mode == "zero" ? 0d : value;

    private static bool TryBounds(double dataMinimum, double dataMaximum, double? requestedMinimum,
        double? requestedMaximum, out double minimum, out double maximum)
    {
        minimum = requestedMinimum ?? dataMinimum;
        maximum = requestedMaximum ?? dataMaximum;
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum)) return false;
        if (minimum > maximum)
        {
            // One explicit bound beyond the data: the automatic bound moves past it instead of refusing the chart.
            if (requestedMinimum is not null && requestedMaximum is not null) return false;
            if (requestedMinimum is not null) maximum = minimum; else minimum = maximum;
        }
        if (minimum == maximum)
        {
            if (requestedMinimum is not null && requestedMaximum is not null) return false;
            var delta = Math.Max(1, Math.Abs(minimum) * .1);
            if (requestedMinimum is null) minimum -= delta;
            if (requestedMaximum is null) maximum += delta;
        }
        return double.IsFinite(minimum) && double.IsFinite(maximum) && maximum > minimum &&
            double.IsFinite(maximum - minimum);
    }

    /// <summary>
    /// The finite values of every series; in a percent stack, the bars of <paramref name="members"/> become
    /// their share of the slot.
    /// </summary>
    private static double?[][] PlotValues(DrawingChart chart, int[] members, string? grouping)
    {
        var values = chart.Series.Select(series => series.Values.Select(value =>
            value is { } finite && double.IsFinite(finite) ? value : null).ToArray()).ToArray();
        if (grouping != "percentStacked") return values;
        var stack = members.Select(index => values[index]).ToArray();
        var slots = chart.Series.Max(series => series.PointCount);
        for (var i = 0; i < slots; i++)
        {
            var positiveScale = 0d; var negativeScale = 0d;
            foreach (var row in stack)
                if (i < row.Length && row[i] is { } value)
                {
                    positiveScale = Math.Max(positiveScale, value);
                    negativeScale = Math.Max(negativeScale, -value);
                }
            var positive = 0d; var negative = 0d;
            foreach (var row in stack)
                if (i < row.Length && row[i] is { } value)
                {
                    if (value > 0) positive += value / positiveScale;
                    else if (value < 0) negative += -value / negativeScale;
                }
            foreach (var row in stack)
                if (i < row.Length && row[i] is { } value)
                    row[i] = value > 0 ? (value / positiveScale) / positive :
                        value < 0 ? -((-value / negativeScale) / negative) : 0;
        }
        return values;
    }

    private static void BeginPlotClip(StringBuilder svg, Plot plot) =>
        svg.Append("<svg data-chart-series=\"true\" overflow=\"hidden\" x=\"").Append(Num(plot.OriginX))
            .Append("\" y=\"").Append(Num(plot.OriginY)).Append("\" width=\"").Append(Num(plot.Width))
            .Append("\" height=\"").Append(Num(plot.Height)).Append("\" viewBox=\"").Append(Num(plot.OriginX))
            .Append(' ').Append(Num(plot.OriginY)).Append(' ').Append(Num(plot.Width)).Append(' ')
            .Append(Num(plot.Height)).Append("\">");

    private static string Data(double value) => double.IsFinite(value)
        ? value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        : throw new ArithmeticException("Nonfinite chart output.");
    /// <summary>A value as its data label shows it: ten significant digits, not the seventeen of a raw double.</summary>
    private static string LabelNumber(double value)
    {
        var compact = double.IsFinite(value) ? value.ToString("G10", System.Globalization.CultureInfo.InvariantCulture) : Data(value);
        return compact.Contains('E') ? Data(value) : compact;
    }

    /// <summary>
    /// A number as its Excel format code shows it, or null to keep the plain form: no code, General, or a code
    /// the number-format engine cannot apply to this value.
    /// </summary>
    private static string? Formatted(double value, string? format, bool date1904) =>
        ExcelNumberFormat.IsGeneral(format) ? null : ExcelNumberFormat.Format(value, format, date1904);

    /// <summary>The label of one point where it differs from the labels of its series (c:dLbl), else null.</summary>
    private static DrawingChartPointLabel? PointLabel(DrawingChartSeries series, int index) =>
        series.PointLabels is { Count: > 0 } labels && labels.TryGetValue(index, out var label) ? label : null;

    private static bool ShowsValue(DrawingChartSeries series, int index) =>
        (PointLabel(series, index)?.ShowValue ?? series.ShowValueLabels) == true;

    private static bool ShowsPercent(DrawingChartSeries series, int index) =>
        (PointLabel(series, index)?.ShowPercent ?? series.ShowPercentLabels) == true;

    /// <summary>
    /// A value as its data label shows it: with the number format of the point's own label, else of the series
    /// labels, else as a plain number.
    /// </summary>
    private static string LabelNumber(DrawingChart chart, DrawingChartSeries series, int index, double value) =>
        Formatted(value, PointLabel(series, index)?.Format ?? series.LabelFormat, chart.Date1904) ?? LabelNumber(value);

    /// <summary>The labels of the ticks of a value axis, lowest first.</summary>
    private static string[] TickLabels(ValueScale scale, bool date1904) => scale.Ticks().Select(tick =>
        AxisLabel(tick.Value, scale.Percent, scale.Format, date1904)).ToArray();

    /// <summary>A tick as the axis shows it: with the number format of the axis, else as a plain number.</summary>
    private static string AxisLabel(double value, bool percent, string? format, bool date1904) =>
        Formatted(value, format, date1904) ?? AxisLabel(value, percent);

    private static string AxisLabel(double value, bool percent)
    {
        if (!percent)
        {
            // Four significant digits, written out: a tick of 250000 is not "2.5E+05".
            var compact = value.ToString("G4", System.Globalization.CultureInfo.InvariantCulture);
            var magnitude = Math.Abs(value);
            return magnitude != 0 && (magnitude < 1e-4 || magnitude >= 1e15) ? compact
                : double.Parse(compact, System.Globalization.CultureInfo.InvariantCulture)
                    .ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture);
        }
        var number = value * 100;
        if (!double.IsFinite(number)) throw new ArithmeticException("Nonfinite percentage axis.");
        // The axis of a 100 % stack is linked to a source that has no percent format: Excel shows it as "0%".
        return ExcelNumberFormat.Format(value, "0%", false) ??
            number.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }

    private static string FillPoint(DrawingChart chart, int seriesIndex, int pointIndex, int fallbackIndex)
    {
        var points = chart.Series[seriesIndex].PointColors;
        if (points is not null && points.TryGetValue(pointIndex, out var color) && color is { Length: 7 } && color[0] == '#' &&
            int.TryParse(color.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out _)) return color;
        return Fill(chart, fallbackIndex);
    }
}
