using System.Text.Json;

namespace DocRedock.Core.Documents;

/// <summary>Source DrawingML, kept independently of semantic/Mermaid reconstruction. Units are CSS pixels at 96dpi.</summary>
public sealed record WorkbookDrawingScene(string SheetName, IReadOnlyList<WorkbookDrawingObject> Objects);
public sealed record WorkbookDrawingObject(string Id, string Kind, double Width, double Height, WorkbookDrawingTransform Transform)
{
    public string Preset { get; init; } = "rect";
    public string? SourceNodeId { get; init; }
    public SourceAnchor? Source { get; init; }
    public string? ImageReference { get; init; }
    public string Text { get; init; } = "";
    public string Fill { get; init; } = "none";
    public string Stroke { get; init; } = "#000000";
    public double StrokeWidth { get; init; } = 1;
    public string? Dash { get; init; }
    public string? HeadArrow { get; init; }
    public string? TailArrow { get; init; }
    /// <summary>Source line end w and len (sm, med, lg); null is med.</summary>
    public string? HeadArrowWidth { get; init; }
    public string? HeadArrowLength { get; init; }
    public string? TailArrowWidth { get; init; }
    public string? TailArrowLength { get; init; }
    public string FontFamily { get; init; } = "Calibri";
    public double FontSize { get; init; } = 14.6667;
    public string FontColor { get; init; } = "#000000";
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public string TextAlign { get; init; } = "center";
    public string VerticalAlign { get; init; } = "middle";
    public double TextInsetLeft { get; init; } = 9.6;
    public double TextInsetRight { get; init; } = 9.6;
    public double TextInsetTop { get; init; } = 4.8;
    public double TextInsetBottom { get; init; } = 4.8;
    public bool WrapText { get; init; } = true;
    public bool ClipText { get; init; }
    public bool Hidden { get; init; }
    public bool Printable { get; init; } = true;
    public bool MoveWithCells { get; init; } = true;
    public bool SizeWithCells { get; init; } = true;
    public double CropLeft { get; init; }
    public double CropTop { get; init; }
    public double CropRight { get; init; }
    public double CropBottom { get; init; }
    public IReadOnlyList<WorkbookDrawingPath> Paths { get; init; } = [];
    public Geometry? TextBounds { get; init; }
    public DrawingPaint? FillPaint { get; init; }
    public DrawingPaint? StrokePaint { get; init; }
    public IReadOnlyList<DrawingTextParagraph> Paragraphs { get; init; } = [];
    public DrawingTextLayout? TextLayout { get; init; }
    public DrawingChart? Chart { get; init; }
    public IReadOnlyDictionary<string, double> Adjustments { get; init; } = new Dictionary<string, double>();
}
public sealed record WorkbookDrawingPath(IReadOnlyList<WorkbookDrawingCommand> Commands, bool Fill = true, bool Stroke = true)
{
    public string FillMode { get; init; } = "norm";
}
public sealed record WorkbookDrawingCommand(string Kind, IReadOnlyList<double> Values);
public sealed record WorkbookDrawingTransform(double A = 1, double B = 0, double C = 0, double D = 1, double E = 0, double F = 0)
{
    public static WorkbookDrawingTransform Identity { get; } = new();
    public static WorkbookDrawingTransform Translate(double x, double y) => new(E:x, F:y);
    public static WorkbookDrawingTransform Scale(double x, double y) => new(A:x, D:y);
    public static WorkbookDrawingTransform Rotate(double degrees)
    { var r = degrees * Math.PI / 180; return new(Math.Cos(r), Math.Sin(r), -Math.Sin(r), Math.Cos(r)); }
    public (double X, double Y) Point(double x, double y) => (A*x+C*y+E, B*x+D*y+F);
    public static WorkbookDrawingTransform operator *(WorkbookDrawingTransform l, WorkbookDrawingTransform r)
    {
        ArgumentNullException.ThrowIfNull(l); ArgumentNullException.ThrowIfNull(r);
        return new(l.A*r.A+l.C*r.B, l.B*r.A+l.D*r.B, l.A*r.C+l.C*r.D, l.B*r.C+l.D*r.D, l.A*r.E+l.C*r.F+l.E, l.B*r.E+l.D*r.F+l.F);
    }
}

public static class WorkbookDrawingSceneJson
{
    public const string ExtensionKey = "xlsx_drawing_scenes";
    public const int MaxObjects = 16384;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static JsonElement Write(IReadOnlyDictionary<string, WorkbookDrawingScene> scenes) => JsonSerializer.SerializeToElement(scenes, Options);
    public static IReadOnlyDictionary<string, WorkbookDrawingScene> Read(JsonElement value, ICollection<string>? warnings = null)
    {
        if (value.ValueKind != JsonValueKind.Object || value.GetRawText().Length > 32*1024*1024)
        { warnings?.Add("Invalid or oversized workbook drawing scenes; none of their objects are drawn."); return new Dictionary<string, WorkbookDrawingScene>(); }
        try
        {
            var scenes = value.Deserialize<Dictionary<string, WorkbookDrawingScene>>(Options) ?? new();
            if (scenes.Count > 4096) warnings?.Add("Workbook drawing scene limit exceeded; the scenes of the remaining sheets are omitted.");
            var result = new Dictionary<string, WorkbookDrawingScene>(StringComparer.Ordinal);
            foreach (var (key, scene) in scenes.Take(4096))
            {
                if (scene is null || scene.Objects is not { Count: <= MaxObjects })
                { warnings?.Add("Invalid workbook drawing scene or object count above " + MaxObjects + " in " + key + "; its objects are omitted."); continue; }
                var items = scene.Objects.Where(Valid).ToArray();
                if (items.Length != scene.Objects.Count) warnings?.Add("Invalid workbook drawing objects in " + key + "; " + (scene.Objects.Count - items.Length) + " of " + scene.Objects.Count + " are omitted.");
                result[key] = scene with { Objects = items };
            }
            return result;
        }
        catch (JsonException exception) { warnings?.Add("Invalid workbook drawing scene JSON: " + exception.Message); return new Dictionary<string, WorkbookDrawingScene>(); }
    }
    internal static bool Valid(WorkbookDrawingObject item) => item is not null && item.Id is { Length: > 0 and <= 1024 } &&
        item.Kind is "shape" or "picture" or "chart" or "table" && item.Preset is { Length: <= 128 } &&
        item.FontFamily is { Length: <= 1024 } && item.Fill is { Length: <= 64 } && item.Stroke is { Length: <= 64 } && item.FontColor is { Length: <= 64 } &&
        item.Transform is { } t && new[] { item.Width, item.Height, t.A,t.B,t.C,t.D,t.E,t.F,item.FontSize,item.StrokeWidth,item.CropLeft,item.CropTop,item.CropRight,item.CropBottom,item.TextInsetLeft,item.TextInsetRight,item.TextInsetTop,item.TextInsetBottom }.All(double.IsFinite) &&
        item.Width is >= 0 and <= 100_000_000 && item.Height is >= 0 and <= 100_000_000 &&
        Math.Abs(t.E)<=100_000_000 && Math.Abs(t.F)<=100_000_000 && item.Text is { Length: <= 1_000_000 } &&
        ValidPaint(item.FillPaint) && ValidPaint(item.StrokePaint) &&
        new[] { item.HeadArrowWidth, item.HeadArrowLength, item.TailArrowWidth, item.TailArrowLength }.All(size => size is null or "sm" or "med" or "lg") &&
        (item.TextLayout is null || item.TextLayout.Direction is "horz" or "vert" or "vert270" or "eaVert" or "wordArtVert" or "wordArtVertRtl" or "mongolianVert" &&
            double.IsFinite(item.TextLayout.RotationDegrees) && Math.Abs(item.TextLayout.RotationDegrees) <= 360) &&
        item.Paragraphs is { Count: <= 4096 } && item.Paragraphs.All(p => p is not null && ValidParagraphLayout(p) && p.Runs is { Count: <= 8192 } && p.Runs.All(r =>
            r is not null && r.Text is { Length: <= 1_000_000 } && r.FontFamily is null or { Length: <= 1024 } &&
            double.IsFinite(Convert.ToDouble(r.FontSize, System.Globalization.CultureInfo.InvariantCulture)) && double.IsFinite(r.Opacity) && r.Opacity is >= 0 and <= 1)) &&
        (item.Chart is null || item.Chart.ValueAxisTitle is null or { Length: <= 4096 } && item.Chart.CategoryAxisTitle is null or { Length: <= 4096 } && item.Chart.BlankDisplay is null or "gap" or "zero" or "span" &&
            item.Chart.ValueAxisFormat is null or { Length: <= ExcelNumberFormat.MaxFormatCodeLength } &&
            item.Chart.XAxisFormat is null or { Length: <= ExcelNumberFormat.MaxFormatCodeLength } &&
            item.Chart.SecondaryAxisFormat is null or { Length: <= ExcelNumberFormat.MaxFormatCodeLength } &&
            item.Chart.SecondaryAxisTitle is null or { Length: <= 4096 } && item.Chart.ChartFill is null or { Length: <= 64 } && item.Chart.PlotFill is null or { Length: <= 64 } &&
            ValidLayout(item.Chart.PlotLayout) && ValidLayout(item.Chart.LegendLayout) && ValidLayout(item.Chart.TitleLayout) &&
            ValidAxis(item.Chart.ValueAxisOptions) && ValidAxis(item.Chart.CategoryAxisOptions) && ValidAxis(item.Chart.SecondaryAxisOptions) &&
            item.Chart.LegendPosition is null or "r" or "l" or "t" or "b" or "tr" &&
            ValidChartPaint(item.Chart.ChartFillPaint) && ValidChartPaint(item.Chart.PlotFillPaint) &&
            item.Chart.TextColor is null or { Length: <= 64 } && item.Chart.TitleColor is null or { Length: <= 64 } && item.Chart.LegendColor is null or { Length: <= 64 } &&
            new[] { item.Chart.ValueAxisMinimum, item.Chart.ValueAxisMaximum, item.Chart.XAxisMinimum, item.Chart.XAxisMaximum,
            item.Chart.SecondaryAxisMinimum, item.Chart.SecondaryAxisMaximum,
            item.Chart.DoughnutHolePercent, item.Chart.FirstSliceAngleDegrees }.All(v => v is null || double.IsFinite(v.Value)) &&
            item.Chart.RadarStyle is null or "standard" or "marker" or "filled" && item.Chart.BubbleScale is null or (>= 0 and <= 300) &&
            item.Chart.UpDownBars is null or { UpColor: null or { Length: <= 64 }, DownColor: null or { Length: <= 64 }, GapWidthPercent: >= 0 and <= 500 } &&
            item.Chart.BandColors is { Count: <= 1024 } && item.Chart.BandColors.All(band => band.Key is >= 0 and < 1024 && band.Value is { Length: <= 64 }) &&
            ValidShadow(item.Chart.PlotShadow) &&
            item.Chart.Series is { Count: <= 256 } && item.Chart.Series.All(s => s is not null && s.PointColors is { Count: <= 100_000 } &&
            s.BubbleSizes is { Count: <= 100_000 } && s.BubbleSizes.All(v => v is null || double.IsFinite(v.Value)) && ValidShadow(s.Shadow) &&
            s.PointColors.All(p => p.Key is >= 0 and < 100_000 && p.Value is { Length: <= 128 }) &&
            s.LabelFormat is null or { Length: <= 4096 } && s.PercentLabelFormat is null or { Length: <= ExcelNumberFormat.MaxFormatCodeLength } && s.LabelPosition is null or { Length: <= 128 } &&
            s.Plot is null or "bar" or "line" or "area" or "scatter" or "stock" or "pie" or "doughnut" && s.Group is >= 0 and < 256 &&
            s.Grouping is null or "standard" or "clustered" or "stacked" or "percentStacked" && ValidChartPaint(s.FillPaint) &&
            s.PointFillPaints is { Count: <= 100_000 } && s.PointFillPaints.All(p => p.Key is >= 0 and < 100_000 && p.Value is not null && ValidChartPaint(p.Value)) &&
            s.PointLabels is { Count: <= 100_000 } && s.PointLabels.All(p => p.Key is >= 0 and < 100_000 && p.Value is not null &&
                p.Value.Format is null or { Length: <= ExcelNumberFormat.MaxFormatCodeLength } && p.Value.PercentFormat is null or { Length: <= ExcelNumberFormat.MaxFormatCodeLength }) &&
            s.Values is { Count: <= 100_000 } && s.XValues is { Count: <= 100_000 } && s.Categories is { Count: <= 100_000 } &&
            s.Values.Concat(s.XValues).All(v => v is null || double.IsFinite(v.Value)))) &&
        (item.TextBounds is null || new[] { item.TextBounds.X, item.TextBounds.Y, item.TextBounds.Width, item.TextBounds.Height }.All(double.IsFinite)) &&
        item.Paths is { Count: <= 256 } && item.Paths.All(p => p is not null && p.FillMode is "norm" or "none" or "lighten" or "lightenLess" or "darken" or "darkenLess" && p.Commands is { Count: <= 65536 } && p.Commands.All(c =>
            c is not null && c.Values is { Count: <= 8 } && c.Values.All(double.IsFinite) && c.Kind is "M" or "L" or "C" or "Q" or "A" or "Z"));
    // ST_LogBase reaches from 2 to 1000.
    private static bool ValidAxis(DrawingChartAxis? axis) => axis is null ||
        axis.LogBase is null or (>= 2 and <= 1000) && axis.Crosses is null or "min" or "max" &&
        (axis.CrossesAt is null || double.IsFinite(axis.CrossesAt.Value)) && axis.TextColor is null or { Length: <= 64 };
    /// <summary>A chart fill is a gradient, a pattern or a picture.</summary>
    private static bool ValidChartPaint(DrawingPaint? paint) => ValidPaint(paint);
    private static bool ValidShadow(DrawingChartShadow? shadow) => shadow is null ||
        new[] { shadow.OffsetX, shadow.OffsetY, shadow.Blur }.All(v => double.IsFinite(v) && Math.Abs(v) <= 10_000) && shadow.Blur >= 0 &&
        shadow.Color is { Length: <= 64 } && double.IsFinite(shadow.Opacity) && shadow.Opacity is >= 0 and <= 1;
    private static bool ValidLayout(DrawingChartLayout? layout) => layout is null ||
        new[] { layout.X, layout.Y, layout.Width, layout.Height }.All(v => v is null || double.IsFinite(v.Value) && Math.Abs(v.Value) <= 16);
    // Bounds follow ECMA-376: ST_TextMargin and ST_TextIndent reach 51206400 EMU (5376 px), line spacing 13200 %,
    // exact spacing 1584 pt (2112 px) and a bullet 4000 pt; percent spacing before and after depends on the run size.
    // An a:tabLst holds at most 32 stops (21.1.2.2.14).
    private static bool ValidParagraphLayout(DrawingTextParagraph p) =>
        p.MarginLeft is null or (>= 0 and <= 5376) && p.Indent is null or (>= -5376 and <= 5376) &&
        p.LineSpacingPercent is null or (>= 0 and <= 13200) && p.LineSpacingExact is null or (>= 0 and <= 2112) &&
        p.LineSpacingAtLeast is null or (>= 0 and <= 2112) &&
        p.SpaceBefore is null or (>= 0 and <= 100_000) && p.SpaceAfter is null or (>= 0 and <= 100_000) &&
        p.BulletSize is null or (> 0 and <= 5334) && p.BulletFontFamily is null or { Length: <= 1024 } && p.BulletColor is null or { Length: <= 64 } &&
        p.MarginRight is null or (>= 0 and <= 5376) && p.FontAlignment is null or "t" or "ctr" or "b" &&
        p.DefaultTabSize is null or (> 0 and <= 5376) && p.BulletOpacity is null or (>= 0 and <= 1) &&
        p.BulletImage is null or { Length: <= MaxFillImageReference } &&
        (p.TabStops is null || p.TabStops.Count <= 32 && p.TabStops.All(stop => stop is not null && stop.Position is >= 0 and <= 5376 && stop.Alignment is "l" or "ctr" or "r" or "dec"));
    /// <summary>Longest picture-fill reference: a data: URI of a picture of the size a drawing reader embeds (4 MiB), in base64.</summary>
    public const int MaxFillImageReference = 6 * 1024 * 1024;
    // ST_Percentage insets of a:fillToRect, a:srcRect and a:fillRect; a source rarely leaves -10..10 (1000 %).
    private static bool ValidInsets(DrawingInsets? insets) => insets is null ||
        new[] { insets.Left, insets.Top, insets.Right, insets.Bottom }.All(v => double.IsFinite(v) && Math.Abs(v) <= 1000);
    private static bool ValidPaint(DrawingPaint? paint) => paint is null ||
        paint.Kind is "none" or "solid" or "linear" or "radial" or "pattern" or "picture" && paint.Color is null or { Length: <= 64 } &&
        double.IsFinite(paint.Opacity) && paint.Opacity is >= 0 and <= 1 && double.IsFinite(paint.AngleDegrees) &&
        paint.Pattern is null or { Length: <= 32 } && paint.BackColor is null or { Length: <= 64 } &&
        double.IsFinite(paint.BackOpacity) && paint.BackOpacity is >= 0 and <= 1 &&
        paint.ImageReference is null or { Length: <= MaxFillImageReference } &&
        ValidInsets(paint.Focus) && ValidInsets(paint.Crop) && ValidInsets(paint.Stretch) &&
        (paint.Tile is null || new[] { paint.Tile.OffsetX, paint.Tile.OffsetY, paint.Tile.ScaleX, paint.Tile.ScaleY }.All(v => double.IsFinite(v) && Math.Abs(v) <= 100_000_000) &&
            paint.Tile.Alignment is "tl" or "t" or "tr" or "l" or "ctr" or "r" or "bl" or "b" or "br" && paint.Tile.Flip is "none" or "x" or "y" or "xy") &&
        paint.Stops is { Count: <= 256 } && paint.Stops.All(s => s is not null && s.Color is { Length: <= 64 } &&
            double.IsFinite(s.Position) && s.Position is >= 0 and <= 1 && double.IsFinite(s.Opacity) && s.Opacity is >= 0 and <= 1);
}
