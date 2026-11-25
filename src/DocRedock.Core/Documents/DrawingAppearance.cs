namespace DocRedock.Core.Documents;

/// <summary>A gradient stop: position 0..1, effective #RRGGBB color and opacity 0..1.</summary>
public sealed record DrawingGradientStop(double Position, string Color, double Opacity = 1);

/// <summary>Fractions of a frame measured inwards from its left, top, right and bottom edges; negative values reach outside.</summary>
public sealed record DrawingInsets(double Left = 0, double Top = 0, double Right = 0, double Bottom = 0);

/// <summary>Source a:tile of a picture fill: offsets in CSS pixels, scales as factors (1 is the picture's own size).</summary>
public sealed record DrawingPictureTile(double OffsetX = 0, double OffsetY = 0, double ScaleX = 1, double ScaleY = 1)
{
    /// <summary>Source algn: the corner or edge of the frame the first tile is anchored to (tl, t, tr, l, ctr, r, bl, b, br).</summary>
    public string Alignment { get; init; } = "tl";
    /// <summary>Source flip: none, x, y or xy; every second tile is mirrored on that axis.</summary>
    public string Flip { get; init; } = "none";
}

/// <summary>Paint resolved from source DrawingML: kind none, solid, linear, radial, pattern or picture.</summary>
public sealed record DrawingPaint
{
    public string Kind { get; init; } = "none";
    /// <summary>The solid color, or the foreground color of a pattern.</summary>
    public string? Color { get; init; }
    /// <summary>Opacity of <see cref="Color"/>, or of the whole picture of a picture fill.</summary>
    public double Opacity { get; init; } = 1;
    public IReadOnlyList<DrawingGradientStop> Stops { get; init; } = [];
    public double AngleDegrees { get; init; }
    public bool Scaled { get; init; }
    public bool RotateWithShape { get; init; } = true;
    /// <summary>Radial: source a:fillToRect, the rectangle (usually a point) the gradient starts from, as insets of the frame.</summary>
    public DrawingInsets? Focus { get; init; }
    /// <summary>Pattern: the source prst name of one of the 8x8 preset bitmaps.</summary>
    public string? Pattern { get; init; }
    /// <summary>Pattern: the background color and its opacity.</summary>
    public string? BackColor { get; init; }
    public double BackOpacity { get; init; } = 1;
    /// <summary>Picture: a data: URI or an exported asset reference.</summary>
    public string? ImageReference { get; init; }
    /// <summary>Picture: source a:srcRect, the part of the picture that is used.</summary>
    public DrawingInsets? Crop { get; init; }
    /// <summary>Picture: source a:stretch/a:fillRect, where the picture lies in the frame.</summary>
    public DrawingInsets? Stretch { get; init; }
    /// <summary>Picture: source a:tile; null for a stretched picture.</summary>
    public DrawingPictureTile? Tile { get; init; }
}

/// <summary>A text run with resolved style; text keeps the exact source characters.</summary>
public sealed record DrawingTextRun
{
    public string Text { get; init; } = string.Empty;
    public string FontFamily { get; init; } = "Calibri";
    public double FontSize { get; init; } = 14.6667;
    public string Color { get; init; } = "#000000";
    public double Opacity { get; init; } = 1;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Strike { get; init; }
    public string? LinkTarget { get; init; }
    /// <summary>Source a:rPr baseline in percent of the font size: positive is superscript, negative subscript.</summary>
    public double Baseline { get; init; }
}

/// <summary>A source a:tab: its position in CSS pixels from the leading edge of the text rectangle, and algn (l, ctr, r or dec).</summary>
public sealed record DrawingTabStop(double Position, string Alignment = "l");

/// <summary>One source paragraph; explicit breaks become additional runs.</summary>
public sealed record DrawingTextParagraph(IReadOnlyList<DrawingTextRun> Runs, string? Alignment = null)
{
    public string? BulletText { get; init; }
    public int Level { get; init; }
    /// <summary>Source marL in CSS pixels: the left edge of the paragraph's text. Null when no source layer sets it.</summary>
    public double? MarginLeft { get; init; }
    /// <summary>Source indent in CSS pixels, relative to <see cref="MarginLeft"/>; negative for a hanging bullet.</summary>
    public double? Indent { get; init; }
    /// <summary>Source lnSpc a:spcPct in percent of single spacing (100 is single), after normAutofit lnSpcReduction.</summary>
    public double? LineSpacingPercent { get; init; }
    /// <summary>Source lnSpc a:spcPts as an exact line pitch in CSS pixels.</summary>
    public double? LineSpacingExact { get; init; }
    /// <summary>A Word paragraph's line pitch under lineRule atLeast, in CSS pixels: each line is as high as its text needs,
    /// but not lower than this. Set instead of <see cref="LineSpacingExact"/>, never together with it.</summary>
    public double? LineSpacingAtLeast { get; init; }
    /// <summary>Source spcBef in CSS pixels; a:spcPct is resolved against the single-spaced line (1.2 times the size) of the paragraph's first run.</summary>
    public double? SpaceBefore { get; init; }
    /// <summary>Source spcAft in CSS pixels; a:spcPct is resolved against the single-spaced line (1.2 times the size) of the paragraph's first run.</summary>
    public double? SpaceAfter { get; init; }
    /// <summary>Source marR in CSS pixels: the room kept free at the trailing edge. Null when no source layer sets it.</summary>
    public double? MarginRight { get; init; }
    /// <summary>Source rtl: the paragraph runs from right to left; its margins, indent, tab stops and l/r alignment are mirrored.</summary>
    public bool RightToLeft { get; init; }
    /// <summary>Source fontAlgn where it moves runs of different sizes off the common baseline: t, ctr or b; null for base and auto.</summary>
    public string? FontAlignment { get; init; }
    /// <summary>Source a:tabLst in ascending position; null when no source layer has one.</summary>
    public IReadOnlyList<DrawingTabStop>? TabStops { get; init; }
    /// <summary>Source defTabSz in CSS pixels: the pitch of the default tab stops. Null when no source layer sets it.</summary>
    public double? DefaultTabSize { get; init; }
    /// <summary>Source a:buFont typeface (theme references resolved); null when the bullet follows the text.</summary>
    public string? BulletFontFamily { get; init; }
    /// <summary>Source a:buClr as #RRGGBB; null when the bullet follows the text.</summary>
    public string? BulletColor { get; init; }
    /// <summary>Source a:buSzPct (of the first run's size) or a:buSzPts in CSS pixels; null when the bullet follows the text.
    /// A picture bullet always has one: the height of the picture.</summary>
    public double? BulletSize { get; init; }
    /// <summary>Opacity of <see cref="BulletColor"/> where its alpha makes the bullet translucent; null for an opaque bullet.</summary>
    public double? BulletOpacity { get; init; }
    /// <summary>Source a:buBlip: the picture drawn as the bullet, as a data: URI. <see cref="BulletText"/> then holds the
    /// character that stands for it in plain text and where the picture cannot be embedded.</summary>
    public string? BulletImage { get; init; }
}

/// <summary>Resolved appearance of one source shape, with non-fatal source findings.</summary>
public sealed record DrawingAppearance
{
    public string? Dash { get; init; }
    public string? HeadArrow { get; init; }
    public string? TailArrow { get; init; }
    /// <summary>Source a:headEnd / a:tailEnd w and len (sm, med, lg); null is the default, med.</summary>
    public string? HeadArrowWidth { get; init; }
    public string? HeadArrowLength { get; init; }
    public string? TailArrowWidth { get; init; }
    public string? TailArrowLength { get; init; }
    public DrawingPaint FillPaint { get; init; } = new();
    public DrawingPaint StrokePaint { get; init; } = new();
    public double StrokeWidth { get; init; }
    public IReadOnlyList<DrawingTextParagraph> Paragraphs { get; init; } = [];
    public DrawingTextLayout? TextLayout { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// The 8x8 bitmaps of the DrawingML preset patterns (ST_PresetPatternVal). The presets are the GDI+ hatch styles
/// under DrawingML names; the rows are those of Wine's gdiplus (dlls/gdiplus/brush.c, HatchBrushes), which
/// reproduce the bitmaps of GDI+. Pixel (x, y) is foreground when bit 7 - x of row 7 - y is set.
/// </summary>
public static class DrawingPatternPresets
{
    private static readonly Dictionary<string, byte[]> Rows = new(StringComparer.Ordinal)
    {
        ["horz"] = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xff], ["vert"] = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80],
        ["dnDiag"] = [0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80], ["upDiag"] = [0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01],
        ["cross"] = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0xff], ["diagCross"] = [0x81, 0x42, 0x24, 0x18, 0x18, 0x24, 0x42, 0x81],
        ["pct5"] = [0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x80], ["pct10"] = [0x00, 0x08, 0x00, 0x80, 0x00, 0x08, 0x00, 0x80],
        ["pct20"] = [0x00, 0x22, 0x00, 0x88, 0x00, 0x22, 0x00, 0x88], ["pct25"] = [0x22, 0x88, 0x22, 0x88, 0x22, 0x88, 0x22, 0x88],
        ["pct30"] = [0x11, 0xaa, 0x44, 0xaa, 0x11, 0xaa, 0x44, 0xaa], ["pct40"] = [0x15, 0xaa, 0x55, 0xaa, 0x51, 0xaa, 0x55, 0xaa],
        ["pct50"] = [0x55, 0xaa, 0x55, 0xaa, 0x55, 0xaa, 0x55, 0xaa], ["pct60"] = [0x55, 0xbb, 0x55, 0xee, 0x55, 0xbb, 0x55, 0xee],
        ["pct70"] = [0xdd, 0x77, 0xdd, 0x77, 0xdd, 0x77, 0xdd, 0x77], ["pct75"] = [0xff, 0xdd, 0xff, 0x77, 0xff, 0xdd, 0xff, 0x77],
        ["pct80"] = [0xff, 0xfe, 0xff, 0xef, 0xff, 0xfe, 0xff, 0xef], ["pct90"] = [0x7f, 0xff, 0xff, 0xff, 0xf7, 0xff, 0xff, 0xff],
        ["ltDnDiag"] = [0x11, 0x22, 0x44, 0x88, 0x11, 0x22, 0x44, 0x88], ["ltUpDiag"] = [0x88, 0x44, 0x22, 0x11, 0x88, 0x44, 0x22, 0x11],
        ["dkDnDiag"] = [0x99, 0x33, 0x66, 0xcc, 0x99, 0x33, 0x66, 0xcc], ["dkUpDiag"] = [0x99, 0xcc, 0x66, 0x33, 0x99, 0xcc, 0x66, 0x33],
        ["wdDnDiag"] = [0x83, 0x07, 0x0e, 0x1c, 0x38, 0x70, 0xe0, 0xc1], ["wdUpDiag"] = [0xc1, 0xe0, 0x70, 0x38, 0x1c, 0x0e, 0x07, 0x83],
        ["ltVert"] = [0x88, 0x88, 0x88, 0x88, 0x88, 0x88, 0x88, 0x88], ["ltHorz"] = [0x00, 0x00, 0x00, 0xff, 0x00, 0x00, 0x00, 0xff],
        ["narVert"] = [0x55, 0x55, 0x55, 0x55, 0x55, 0x55, 0x55, 0x55], ["narHorz"] = [0x00, 0xff, 0x00, 0xff, 0x00, 0xff, 0x00, 0xff],
        ["dkVert"] = [0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc], ["dkHorz"] = [0x00, 0x00, 0xff, 0xff, 0x00, 0x00, 0xff, 0xff],
        ["dashDnDiag"] = [0x00, 0x00, 0x11, 0x22, 0x44, 0x88, 0x00, 0x00], ["dashUpDiag"] = [0x00, 0x00, 0x88, 0x44, 0x22, 0x11, 0x00, 0x00],
        ["dashHorz"] = [0x00, 0x00, 0x00, 0x0f, 0x00, 0x00, 0x00, 0xf0], ["dashVert"] = [0x08, 0x08, 0x08, 0x08, 0x80, 0x80, 0x80, 0x80],
        ["smConfetti"] = [0x04, 0x20, 0x01, 0x10, 0x02, 0x40, 0x08, 0x80], ["lgConfetti"] = [0x8d, 0x0c, 0xc0, 0xd8, 0x1b, 0x03, 0x30, 0xb1],
        ["zigZag"] = [0x18, 0x24, 0x42, 0x81, 0x18, 0x24, 0x42, 0x81], ["wave"] = [0xc0, 0x25, 0x18, 0x00, 0xc0, 0x25, 0x18, 0x00],
        ["diagBrick"] = [0x81, 0x42, 0x24, 0x18, 0x08, 0x04, 0x02, 0x01], ["horzBrick"] = [0x08, 0x08, 0x08, 0xff, 0x80, 0x80, 0x80, 0xff],
        ["weave"] = [0x51, 0x22, 0x14, 0x88, 0x45, 0x22, 0x54, 0x88], ["plaid"] = [0xf0, 0xf0, 0xf0, 0xf0, 0x55, 0xaa, 0x55, 0xaa],
        ["divot"] = [0x80, 0x01, 0x80, 0x00, 0x10, 0x08, 0x10, 0x00], ["dotGrid"] = [0x00, 0x80, 0x00, 0x80, 0x00, 0x80, 0x00, 0xaa],
        ["dotDmnd"] = [0x00, 0x22, 0x00, 0x08, 0x00, 0x22, 0x00, 0x80], ["shingle"] = [0x01, 0x01, 0x02, 0x0c, 0x30, 0x48, 0x84, 0x03],
        ["trellis"] = [0x99, 0xff, 0x66, 0xff, 0x99, 0xff, 0x66, 0xff], ["sphere"] = [0xf8, 0xf8, 0x98, 0x77, 0x8f, 0x8f, 0x89, 0x77],
        ["smGrid"] = [0x88, 0x88, 0x88, 0xff, 0x88, 0x88, 0x88, 0xff], ["smCheck"] = [0x99, 0x66, 0x66, 0x99, 0x99, 0x66, 0x66, 0x99],
        ["lgCheck"] = [0x0f, 0x0f, 0x0f, 0x0f, 0xf0, 0xf0, 0xf0, 0xf0], ["openDmnd"] = [0x01, 0x82, 0x44, 0x28, 0x10, 0x28, 0x44, 0x82],
        ["solidDmnd"] = [0x00, 0x10, 0x38, 0x7c, 0xfe, 0x7c, 0x38, 0x10],
        // GDI+ has one bitmap for HatchStyleCross and HatchStyleLargeGrid.
        ["lgGrid"] = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0xff],
    };

    public static bool IsKnown(string? preset) => preset is not null && Rows.ContainsKey(preset);

    /// <summary>True when pixel (x, y) of the 8x8 tile is drawn in the foreground color; false for an unknown preset.</summary>
    public static bool IsForeground(string? preset, int x, int y) =>
        preset is not null && Rows.TryGetValue(preset, out var rows) && (rows[7 - (y & 7)] & (0x80 >> (x & 7))) != 0;
}
