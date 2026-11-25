using System.Text.Json;

namespace DocRedock.Core.Documents;

/// <summary>A source diagram frame independent of workbook axes or browser page pagination.</summary>
public sealed record SourceDrawingScene(string Id, double Width, double Height, IReadOnlyList<WorkbookDrawingObject> Objects)
{
    public string CoordinateSpace { get; init; } = "drawing-local";
    public string? AnchorNodeId { get; init; }
    public int? AnchorOrder { get; init; }
    /// <summary>Source coordinate at the top-left of the visible frame; object matrices remain unchanged.</summary>
    public double OriginX { get; init; }
    public double OriginY { get; init; }
    /// <summary>What is painted behind every object over the whole frame (a slide's background); null for none.</summary>
    public DrawingPaint? Background { get; init; }
    /// <summary>Package part of a picture stretched over the whole frame, above <see cref="Background"/>; null for none.</summary>
    public string? BackgroundImageReference { get; init; }
    /// <summary>Source a:tile of the background picture: it repeats at its own size instead of being stretched; null for a stretched picture.</summary>
    public DrawingPictureTile? BackgroundImageTile { get; init; }
    /// <summary>Source a:srcRect of the background picture: the part of the picture that is used; null for the whole picture.</summary>
    public DrawingInsets? BackgroundImageCrop { get; init; }
    /// <summary>Source a:stretch/a:fillRect of a stretched background picture: where the picture lies in the frame; null for the whole frame.</summary>
    public DrawingInsets? BackgroundImageStretch { get; init; }
    /// <summary>Source cell appearance of the scene's tables, by the id of the table's drawing object.</summary>
    public IReadOnlyDictionary<string, SourceDrawingTableStyle>? TableStyles { get; init; }
}

/// <summary>A table edge: width in CSS pixels (0 draws none) and #RRGGBB color.</summary>
public sealed record SourceDrawingTableBorder(double Width, string? Color = null)
{
    /// <summary>dashed, dotted or double; null for a solid line.</summary>
    public string? Style { get; init; }
}

/// <summary>One run of a table cell's text with its effective formatting; a null value was not resolved from the source.</summary>
public sealed record SourceDrawingTableRun(string Text)
{
    /// <summary>Text size in points.</summary>
    public double? FontSize { get; init; }
    public bool? Bold { get; init; }
    public bool? Italic { get; init; }
    public bool Underline { get; init; }
    public bool Strike { get; init; }
    public string? Color { get; init; }
    /// <summary>Typefaces of the run's Latin, East Asian and complex-script text, theme references resolved.</summary>
    public IReadOnlyList<string>? Fonts { get; init; }
}

/// <summary>One paragraph of a table cell: a null value leaves what the cell gives.</summary>
public sealed record SourceDrawingTableParagraph
{
    /// <summary>left, center, right or justify.</summary>
    public string? TextAlign { get; init; }
    /// <summary>Source a:pPr marL in CSS pixels: the left edge of the paragraph's lines.</summary>
    public double? MarginLeft { get; init; }
    /// <summary>Source a:pPr indent in CSS pixels, when positive: how far the first line starts right of <see cref="MarginLeft"/>.</summary>
    public double? Indent { get; init; }
}

/// <summary>
/// The effective appearance of one table cell after the table style, the cell's own properties and its
/// text properties. A null value was not resolved from the source and leaves the reader's default.
/// </summary>
public sealed record SourceDrawingTableCellStyle
{
    /// <summary>#RRGGBB, or "none" for a cell that shows what is behind the table.</summary>
    public string? Fill { get; init; }
    public string? Color { get; init; }
    /// <summary>Text size in points.</summary>
    public double? FontSize { get; init; }
    public bool? Bold { get; init; }
    public bool? Italic { get; init; }
    /// <summary>Typefaces of the cell's Latin, East Asian and complex-script text, theme references resolved.</summary>
    public IReadOnlyList<string>? Fonts { get; init; }
    /// <summary>
    /// A gradient fill (linear or radial; <see cref="Fill"/> then holds its first color) or a pattern fill
    /// (<see cref="Fill"/> then holds its background color).
    /// </summary>
    public DrawingPaint? FillPaint { get; init; }
    /// <summary>left, center, right or justify.</summary>
    public string? TextAlign { get; init; }
    /// <summary>top, middle or bottom.</summary>
    public string? VerticalAlign { get; init; }
    public SourceDrawingTableBorder? Left { get; init; }
    public SourceDrawingTableBorder? Top { get; init; }
    public SourceDrawingTableBorder? Right { get; init; }
    public SourceDrawingTableBorder? Bottom { get; init; }
    /// <summary>The line from the top left to the bottom right corner (a:lnTlToBr).</summary>
    public SourceDrawingTableBorder? DiagonalDown { get; init; }
    /// <summary>The line from the bottom left to the top right corner (a:lnBlToTr).</summary>
    public SourceDrawingTableBorder? DiagonalUp { get; init; }
    /// <summary>
    /// The cell's paragraphs as runs, when a run differs from the cell's own formatting or the table has no
    /// semantic node that holds its text (a table of a slide layout or master); null otherwise.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<SourceDrawingTableRun>>? Paragraphs { get; init; }
    /// <summary>
    /// The alignment and indent of each paragraph of the cell, in the order of <see cref="Paragraphs"/>, when one is
    /// aligned differently from the cell (<see cref="TextAlign"/>, the first paragraph's) or is indented; null otherwise.
    /// </summary>
    public IReadOnlyList<SourceDrawingTableParagraph>? ParagraphStyles { get; init; }
    /// <summary>Rows and grid columns the cell covers.</summary>
    public int RowSpan { get; init; } = 1;
    public int ColumnSpan { get; init; } = 1;
}

/// <summary>Cell styles by source row and grid column (a merged cell is styled at its first row and column).</summary>
public sealed record SourceDrawingTableStyle(IReadOnlyList<IReadOnlyList<SourceDrawingTableCellStyle?>> Cells)
{
    /// <summary>Source column widths in CSS pixels; empty when the source has none.</summary>
    public IReadOnlyList<double> ColumnWidths { get; init; } = [];
    /// <summary>Source row heights in CSS pixels; empty when the source has none.</summary>
    public IReadOnlyList<double> RowHeights { get; init; } = [];
}

public static class SourceDrawingSceneJson
{
    public const string ExtensionKey = "source_drawing_scenes";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static JsonElement Write(IReadOnlyDictionary<string, IReadOnlyList<SourceDrawingScene>> scenes) => JsonSerializer.SerializeToElement(scenes, Options);
    public static IReadOnlyDictionary<string, IReadOnlyList<SourceDrawingScene>> Read(JsonElement value, ICollection<string>? warnings = null)
    {
        var result = new Dictionary<string, IReadOnlyList<SourceDrawingScene>>(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object || value.GetRawText().Length > 32 * 1024 * 1024)
        { warnings?.Add("Invalid or oversized source drawing scenes."); return result; }
        try
        {
            var source = value.Deserialize<Dictionary<string, SourceDrawingScene[]>>(Options) ?? [];
            if (source.Count > 4096) warnings?.Add("Source drawing scene partition limit exceeded; remaining partitions were omitted.");
            foreach (var (part, scenes) in source.Take(4096))
            {
                if (scenes?.Length > 4096) warnings?.Add("Source drawing scene count limit exceeded in " + part + "; remaining scenes were omitted.");
                var valid = new List<SourceDrawingScene>();
                foreach (var scene in (scenes ?? []).Take(4096))
                {
                    if (scene is null || scene.Id is not { Length: > 0 and <= 1024 } || !double.IsFinite(scene.Width) || !double.IsFinite(scene.Height) ||
                        scene.Width is <= 0 or > 1e8 || scene.Height is <= 0 or > 1e8 ||
                        !double.IsFinite(scene.OriginX) || !double.IsFinite(scene.OriginY) || Math.Abs(scene.OriginX) > 1e8 || Math.Abs(scene.OriginY) > 1e8 ||
                        scene.Objects is not { Count: <= 16384 })
                    { warnings?.Add("Invalid source scene in " + part); continue; }
                    var items = scene.Objects.Where(WorkbookDrawingSceneJson.Valid).ToArray();
                    if (items.Length != scene.Objects.Count) warnings?.Add("Invalid source drawing objects in " + part + ": " + scene.Id);
                    var checkedScene = scene with { Objects = items };
                    // The background is a paint like a shape's fill: the same bounds apply.
                    if (scene.Background is not null && !WorkbookDrawingSceneJson.Valid(new WorkbookDrawingObject("background", "shape", 1, 1, WorkbookDrawingTransform.Identity) { FillPaint = scene.Background }) ||
                        scene.BackgroundImageReference is { Length: 0 or > 1024 } || scene.BackgroundImageTile is { } tile && !ValidTile(tile) ||
                        !ValidInsets(scene.BackgroundImageCrop) || !ValidInsets(scene.BackgroundImageStretch))
                    {
                        warnings?.Add("Invalid source scene background in " + part + ": " + scene.Id);
                        checkedScene = checkedScene with { Background = null, BackgroundImageReference = null, BackgroundImageTile = null, BackgroundImageCrop = null, BackgroundImageStretch = null };
                    }
                    if (scene.TableStyles is not null && !ValidTableStyles(scene.TableStyles))
                    { warnings?.Add("Invalid source table styles in " + part + ": " + scene.Id); checkedScene = checkedScene with { TableStyles = null }; }
                    valid.Add(checkedScene);
                }
                result[part] = valid;
            }
        }
        catch (JsonException exception) { warnings?.Add("Invalid source scene JSON: " + exception.Message); }
        return result;
    }

    private static bool ValidTableStyles(IReadOnlyDictionary<string, SourceDrawingTableStyle> styles) =>
        styles.Count <= 4096 && styles.All(style => style.Key is { Length: > 0 and <= 1024 } && style.Value is { Cells: { Count: <= 4096 } rows } table &&
            table.ColumnWidths is { Count: <= 16384 } && table.RowHeights is { Count: <= 4096 } &&
            table.ColumnWidths.Concat(table.RowHeights).All(size => double.IsFinite(size) && size is >= 0 and <= 100_000_000) &&
            rows.All(row => row is { Count: <= 16384 } && row.All(cell => cell is null ||
                cell.Fill is null or { Length: <= 64 } && cell.Color is null or { Length: <= 64 } &&
                cell.FontSize is null or (> 0 and <= 4000) && ValidFonts(cell.Fonts) &&
                cell.RowSpan is >= 1 and <= 4096 && cell.ColumnSpan is >= 1 and <= 16384 &&
                (cell.FillPaint is null || WorkbookDrawingSceneJson.Valid(new WorkbookDrawingObject("cell", "shape", 1, 1, WorkbookDrawingTransform.Identity) { FillPaint = cell.FillPaint })) &&
                cell.TextAlign is null or "left" or "center" or "right" or "justify" && cell.VerticalAlign is null or "top" or "middle" or "bottom" &&
                cell.ParagraphStyles is null or { Count: <= 4096 } && (cell.ParagraphStyles ?? []).All(ValidParagraph) &&
                cell.Paragraphs is null or { Count: <= 4096 } && (cell.Paragraphs ?? []).All(paragraph => paragraph is { Count: <= 4096 } && paragraph.All(run =>
                    run is { Text.Length: <= 1_000_000 } && run.FontSize is null or (> 0 and <= 4000) && run.Color is null or { Length: <= 64 } && ValidFonts(run.Fonts))) &&
                new[] { cell.Left, cell.Top, cell.Right, cell.Bottom, cell.DiagonalDown, cell.DiagonalUp }.All(border => border is null ||
                    double.IsFinite(border.Width) && border.Width is >= 0 and <= 1024 && border.Color is null or { Length: <= 64 } &&
                    border.Style is null or "dashed" or "dotted" or "double"))));

    private static bool ValidFonts(IReadOnlyList<string>? fonts) => fonts is null || fonts.Count <= 8 && fonts.All(font => font is { Length: > 0 and <= 128 });

    // ST_Percentage insets of a:srcRect and a:fillRect, as for the picture fill of a shape.
    private static bool ValidInsets(DrawingInsets? insets) => insets is null ||
        new[] { insets.Left, insets.Top, insets.Right, insets.Bottom }.All(value => double.IsFinite(value) && Math.Abs(value) <= 1000);

    private static bool ValidParagraph(SourceDrawingTableParagraph? paragraph) => paragraph is not null &&
        paragraph.TextAlign is null or "left" or "center" or "right" or "justify" &&
        paragraph.MarginLeft is null or (>= 0 and <= 100_000) && paragraph.Indent is null or (>= 0 and <= 100_000);

    private static bool ValidTile(DrawingPictureTile tile) =>
        new[] { tile.OffsetX, tile.OffsetY, tile.ScaleX, tile.ScaleY }.All(value => double.IsFinite(value) && Math.Abs(value) <= 100_000_000) &&
        tile.Alignment is "tl" or "t" or "tr" or "l" or "ctr" or "r" or "bl" or "b" or "br" && tile.Flip is "none" or "x" or "y" or "xy";
}
