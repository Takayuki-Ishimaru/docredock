namespace DocRedock.Formats.OpenXml.Common;

/// <summary>How a drawn line decorates the text it crosses.</summary>
internal enum DrawnKind { Strike, Underline }

/// <summary>How a line of text is aligned in the space it is set in. Distributed text (Word's "distribute", PowerPoint's "dist")
/// is spread over all of the space, even a single line of it.</summary>
internal enum DrawnAlignment { Left, Right, Center, Fill }

/// <summary>A straight, nearly horizontal line or thin bar that could decorate text: its end points in points (the
/// left end first), how thick its stroke or bar is, and whether it was drawn as a bar (a thin rectangle).</summary>
internal sealed record DrawnStroke(double Left, double LeftY, double Right, double RightY, double Thickness, bool IsBar)
{
    public double Y => (LeftY + RightY) / 2;
    public double Rise => Math.Abs(RightY - LeftY);
}

/// <summary>Where one run of text lies, in points: the bottom of the space it is set in, the extent of the text that
/// shows, how far the ends of that extent are uncertain because the width of the text is an estimate, and the baseline
/// of each line of text. Only text of a single line is covered as a whole by one line.</summary>
internal sealed record DrawnTextExtent(double Size, double Bottom, double Left, double Right, double LeftSlack, double RightSlack,
    IReadOnlyList<double> Baselines, bool IsSingleLine);

/// <summary>Something a decoration may not end on: a straight line (its two end points) or the frame of a shape
/// (left, top, right, bottom). A line that is itself a candidate carries its stroke, so that the other line of a
/// double line can be told from a line it meets.</summary>
internal sealed record DrawnObstacle(bool IsLine, double X1, double Y1, double X2, double Y2, DrawnStroke? Stroke = null);

// The rules that tell a line drawn over text from a line that means something else. People cancel text in a
// presentation or a Word document by drawing a straight line through it (often red, sometimes double) and emphasize
// text by drawing a line under it; such a line is character formatting, not a drawing.
//
// The shape rules and the vertical zones relative to the baseline are those of PdfTextDecorations, so a document and
// its PDF agree, and they are the ones XlsxAdapter.DrawnLines applies to a worksheet: a decoration is at most 0.2 em
// thick, an underline lies from 0.35 em below the baseline to 0.15 em above it, a strikethrough from there to half an em
// above it, and a line higher than that is an overline and stays a drawing. A double line is one decoration, and a line
// whose end meets another shape is a connector. The worksheet's copy of these rules is left as it is; the
// presentation and the Word reader share this one.
internal static class DrawnLineRules
{
    // A decoration is no thicker than this many ems and at least four times as long as it is thick.
    public const double MaxThicknessEm = 0.2;
    // A stroke thicker than this many ems is a marker pen or a redaction, not a line through the text.
    public const double MaxStrokeEm = 0.5;
    // Where a line can lie relative to the baseline of the text it decorates, in ems: an underline from 0.35 em below
    // it to 0.15 em above, a strikethrough from there to half an em above. The slack covers the estimate of where the
    // baseline is; it is the one PdfTextDecorations allows when it places a strikethrough.
    public const double UnderlineLowEm = -0.35;
    public const double StrikeFromEm = 0.15;
    public const double StrikeToEm = 0.5;
    public const double ZoneSlackEm = 0.05;
    // A line that comes within this many points of another line or shape with an end is a connector or a rule, not a
    // decoration.
    public const double TouchDistance = 1.5;
    // A line on the bottom edge of the space the text is set in is that space's border rule, not an underline.
    public const double EdgeMargin = 0.5;
    // Over the text's horizontal extent: a line must overlap the text by this many ems, and covers all of it when it
    // reaches within ReachEm of each end, plus WidthError of the estimated width at an end whose place depends on that
    // estimate (the text's far end, or both ends of centered text).
    public const double OverlapEm = 0.3;
    public const double MinLengthEm = 0.4;
    public const double ReachEm = 0.5;
    public const double WidthError = 0.15;

    /// <summary>Whether the line decorates the text, and how: in the strike or underline zone of one of its lines, over
    /// it for long enough, and covering all of a single line of text.</summary>
    public static (DrawnKind Kind, bool Whole)? Match(DrawnStroke line, DrawnTextExtent box)
    {
        var size = box.Size;
        if (line.Right - line.Left < MinLengthEm * size || line.Rise > MaxThicknessEm * size) return null;
        if (line.IsBar && line.Thickness > Math.Max(1.5, MaxThicknessEm * size)) return null;
        if (!line.IsBar && line.Thickness > MaxStrokeEm * size) return null;
        if (line.Y > box.Bottom - EdgeMargin) return null;
        DrawnKind? kind = null;
        foreach (var baseline in box.Baselines)
        {
            var em = (baseline - line.Y) / size;
            if (em < UnderlineLowEm - ZoneSlackEm || em > StrikeToEm + ZoneSlackEm) continue;
            kind = em >= StrikeFromEm ? DrawnKind.Strike : DrawnKind.Underline;
            break;
        }
        if (kind is null) return null;
        var overlap = Math.Min(line.Right, box.Right) - Math.Max(line.Left, box.Left);
        if (overlap < Math.Min(OverlapEm * size, (box.Right - box.Left) / 2)) return null;
        var whole = box.IsSingleLine && line.Left <= box.Left + ReachEm * size + box.LeftSlack &&
                    line.Right >= box.Right - ReachEm * size - box.RightSlack;
        return (kind.Value, whole);
    }

    /// <summary>The extent of one line of text of the given estimated width, set in the space from
    /// <paramref name="areaLeft"/> to <paramref name="areaRight"/> and aligned in it. The ends the alignment fixes are
    /// exact; the other end depends on the estimate of the width.</summary>
    public static DrawnTextExtent SingleLine(double size, double bottom, double areaLeft, double areaRight, double width,
        DrawnAlignment alignment, double baseline)
    {
        double start, end, leftShare, rightShare;
        switch (alignment)
        {
            case DrawnAlignment.Right:
                (end, start, leftShare, rightShare) = (areaRight, areaRight - width, 1, 0);
                break;
            case DrawnAlignment.Center:
                var middle = (areaLeft + areaRight) / 2;
                (start, end, leftShare, rightShare) = (middle - width / 2, middle + width / 2, 0.5, 0.5);
                break;
            case DrawnAlignment.Fill:
                // Both ends are the space's own: the estimate of the width does not move them.
                (start, end, leftShare, rightShare) = (areaLeft, areaRight, 0, 0);
                break;
            default:
                (start, end, leftShare, rightShare) = (areaLeft, areaLeft + width, 0, 1);
                break;
        }
        return new(size, bottom, start, end, WidthError * width * leftShare, WidthError * width * rightShare, [baseline], true);
    }

    /// <summary>The extent of text of more than one line: only where its lines are is known.</summary>
    public static DrawnTextExtent SeveralLines(double size, double bottom, double areaLeft, double areaRight, IReadOnlyList<double> baselines) =>
        new(size, bottom, areaLeft, areaRight, 0, 0, baselines, false);

    /// <summary>Whether an end of the line meets another shape or line: it is then a connector or a rule between things,
    /// not a decoration of text. The other line of a double line runs close beside this one over the same span without
    /// being part of it.</summary>
    public static bool MeetsAnother(DrawnStroke line, double size, IEnumerable<DrawnObstacle> others)
    {
        foreach (var other in others)
        {
            if (other.Stroke is { } twin && IsTwin(line, twin, size)) continue;
            if (EndMeets(line.Left, line.LeftY, other) || EndMeets(line.Right, line.RightY, other)) return true;
        }
        return false;
    }

    // The other line of a double strikethrough or double underline: as thin, over the same span, a little above or
    // below (it is within a touch of this line, or it would not matter). A line on the same level that continues this
    // one, or a bar thick enough to be something else, is not a twin.
    private static bool IsTwin(DrawnStroke line, DrawnStroke other, double size) =>
        other.Thickness <= MaxThicknessEm * size &&
        Math.Abs(other.Left - line.Left) <= 0.2 * size && Math.Abs(other.Right - line.Right) <= 0.2 * size;

    private static bool EndMeets(double x, double y, DrawnObstacle other)
    {
        if (other.IsLine) return DistanceToSegment(x, y, other.X1, other.Y1, other.X2, other.Y2) <= TouchDistance;
        // Any other shape is judged by the outline of its frame.
        var (left, top, right, bottom) = (other.X1, other.Y1, other.X2, other.Y2);
        var edges = new[] { (left, top, right, top), (left, bottom, right, bottom), (left, top, left, bottom), (right, top, right, bottom) };
        return edges.Any(edge => DistanceToSegment(x, y, edge.Item1, edge.Item2, edge.Item3, edge.Item4) <= TouchDistance);
    }

    public static double DistanceToSegment(double x, double y, double x1, double y1, double x2, double y2)
    {
        var (dx, dy) = (x2 - x1, y2 - y1);
        var length = dx * dx + dy * dy;
        var t = length <= 0 ? 0 : Math.Clamp(((x - x1) * dx + (y - y1) * dy) / length, 0, 1);
        return Math.Sqrt(Math.Pow(x - (x1 + t * dx), 2) + Math.Pow(y - (y1 + t * dy), 2));
    }

    // Hangul jamo, CJK (radicals to Yi), Hangul syllables, CJK compatibility ideographs and forms, and the full-width
    // forms; a high surrogate stands for a character outside the plane, and its low surrogate has no width of its own.
    private static bool IsWideCharacter(char character) =>
        character is >= 'ᄀ' and <= 'ᅟ' or >= '⺀' and <= '꓏' or >= '가' and <= '힣' or
            >= '豈' and <= '﫿' or >= '︰' and <= '﹏' or >= '＀' and <= '｠' or >= '￠' and <= '￦' ||
        char.IsHighSurrogate(character);

    private const string NarrowCharacters = " !'()-.,/:;I[]\\fijlrt|";

    /// <summary>A typical width of the text: full-width characters an em, others a little over half an em, the narrow
    /// ones of Latin text (a space, punctuation, i j l t f r) a third of one. Within a tenth of what Arial, Calibri and
    /// the Japanese gothic fonts give for ordinary words, numbers and sentences.</summary>
    public static double TextWidth(string text, double size) =>
        text.Sum(character => char.IsControl(character) || char.IsLowSurrogate(character) ? 0
            : IsWideCharacter(character) ? 1.0 : NarrowCharacters.Contains(character, StringComparison.Ordinal) ? 0.3 : 0.55) * size;
}
