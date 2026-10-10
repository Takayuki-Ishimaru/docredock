using System.Globalization;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.Pdf;

/// <summary>
/// Underlines and strikethroughs drawn over PDF text. A PDF has no text decoration of its own: a
/// producer paints a thin horizontal line or bar across the glyphs (Word and LibreOffice a filled bar,
/// others a stroked line). Such a line is text formatting, not a drawing, so it is taken out of the
/// visual graph before tables are inferred from its lines, and a strikethrough marks the text it covers
/// as struck. When the font gives its glyph widths, each character's place is known and a strikethrough
/// marks exactly the characters it crosses. Otherwise only text a strikethrough covers as a whole show
/// (one Tj/TJ string, which is how most producers write a run of one format) is marked, and a
/// strikethrough over part of a show is reported instead of guessed.
/// </summary>
internal static class PdfTextDecorations
{
    internal enum DecorationKind { Strike, Underline }

    /// <summary>A decoration line: the paths it was drawn with (every dash of a dashed one), and where
    /// it lies in page space.</summary>
    internal sealed record Decoration(IReadOnlyList<string> PathIds, DecorationKind Kind, double Left, double Right, double Y);

    // A decoration is no thicker than this many ems (Word's bars are about 0.05-0.07 em) and at least
    // four times as long as it is thick.
    private const double MaxThicknessEm = 0.2;
    // Where a line can lie relative to the baseline of the text it decorates, in ems: an underline sits
    // a little below the baseline, a strikethrough about half the x-height above it (0.18-0.35 em in
    // Word and LibreOffice). A line higher than half an em is over the letters, not through them: an
    // overline (a negated signal, a mathematical bar), which stays a drawing.
    private const double LowestEm = -0.35;
    private const double HighestEm = 0.5;
    private const double StrikeFromEm = 0.15;
    private const double StrikeToEm = 0.5;
    // A line this many ems or less above the baseline of other text lies on the tops of its glyphs
    // (cap height is about 0.7 em; a fraction's denominator sits about 0.8 em under its bar).
    private const double TopUnderEm = 0.85;
    // How close a line's end must come to another drawn line or shape to meet it.
    private const double TouchDistance = 1.5;

    /// <summary>The thin horizontal lines that sit on upright text and stay within it, neither end
    /// touching another drawn line or shape (a grid's rule meets the rules across it, a connector the
    /// boxes it joins). A dashed line drawn dash by dash is judged as one line, so the dashes of a
    /// rule under a row of text are not underlines one by one. The other line of a double
    /// strikethrough or double underline runs close beside a line over the same span without meeting
    /// it, so it does not count as touching.</summary>
    public static IReadOnlyList<Decoration> Find(VisualGraph graph, IReadOnlyList<PdfTextRegion> fragments,
        CancellationToken cancellationToken = default)
    {
        var paths = graph.Paths ?? [];
        var texts = fragments.Where(fragment => fragment.FontSize is > 0 && fragment.Text.Trim().Length > 0).ToArray();
        if (paths.Count == 0 || texts.Length == 0) return [];
        var lines = Lines(paths);
        if (lines.Count == 0) return [];
        var result = new List<Decoration>();
        var limits = Limits(texts);
        double Reach(PdfTextRegion fragment) => limits[fragment];
        var largest = texts.Max(fragment => fragment.FontSize!.Value);
        var textRows = new Rows<PdfTextRegion>(texts, fragment => fragment.BoundingBox.Y);
        var lineRows = new Rows<Line>(lines, line => line.Y);
        SegmentGrid? segments = null;
        for (var number = 0; number < lines.Count; number++)
        {
            if ((number & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var line = lines[number];
            var candidates = textRows.Between(line.Y - HighestEm * largest, line.Y - LowestEm * largest)
                .Where(fragment => Sits(line.Y, line.Thickness, fragment) && Overlap(line.Left, line.Right, fragment.BoundingBox.X, Reach(fragment)) > 0)
                .ToArray();
            if (candidates.Length == 0) continue;
            var size = candidates.Max(fragment => fragment.FontSize!.Value);
            if (line.Right - line.Left < 0.4 * size) continue;
            // The whole line lies over the text it decorates, not past it.
            if (line.Left < candidates.Min(fragment => fragment.BoundingBox.X) - 0.3 * size ||
                line.Right > candidates.Max(fragment => Reach(fragment)) + 0.3 * size) continue;
            // Text right under the line, the tops of its glyphs just below it, makes it a line between
            // two texts (a fraction bar, a rule between close rows), not a decoration of the upper one.
            // The next line of ordinary text sits a full line pitch lower.
            if (textRows.Between(line.Y - TopUnderEm * largest, line.Y).Any(fragment =>
                    (line.Y - fragment.BoundingBox.Y) / fragment.FontSize!.Value is > HighestEm and <= TopUnderEm &&
                    Overlap(line.Left, line.Right, fragment.BoundingBox.X, Reach(fragment)) > 0)) continue;
            segments ??= new SegmentGrid(paths);
            var ignored = new HashSet<int>(line.Paths);
            foreach (var other in lineRows.Between(line.Y - 0.4 * size, line.Y + 0.4 * size))
                if (!ReferenceEquals(other, line) && IsTwin(line, other, size)) ignored.UnionWith(other.Paths);
            if (segments.Touches(ignored, line.Left, line.Y, -1, line.Reach) || segments.Touches(ignored, line.Right, line.Y, 1, line.Reach)) continue;
            var baseline = candidates.OrderBy(fragment => Math.Abs(fragment.BoundingBox.X - line.Left)).ThenBy(fragment => fragment.ReadingOrder).First();
            var em = (line.Y - baseline.BoundingBox.Y) / baseline.FontSize!.Value;
            result.Add(new Decoration(line.Paths.Select(index => paths[index].Id).ToArray(),
                em >= StrikeFromEm ? DecorationKind.Strike : DecorationKind.Underline, line.Left, line.Right, line.Y));
        }
        return result;
    }

    /// <summary>Marks the text the strikethroughs cover as struck. A fragment whose characters are
    /// placed (its font gives glyph widths) gets exactly the characters a line crosses the middle of.
    /// Any other fragment is struck as a whole when a line covers it; those a line crosses only part
    /// of are returned, once per set of fragments: their struck characters cannot be told apart
    /// without glyph widths.</summary>
    public static (List<PdfTextRegion> Fragments, List<IReadOnlyList<PdfTextRegion>> Unplaced) ApplyStrikes(
        IReadOnlyList<PdfTextRegion> fragments, IEnumerable<(double Left, double Right, double Y)> strikes)
    {
        // Several lines can cross one fragment (the two lines of a double strikethrough, a drawn line
        // and a StrikeOut annotation): a fragment one of them covers is struck, and a line that crosses
        // only part of a fragment is reported unless another line strikes that fragment whole.
        var limits = Limits(fragments.Where(fragment => fragment.FontSize is > 0).ToArray());
        var sized = Enumerable.Range(0, fragments.Count)
            .Where(index => fragments[index].FontSize is > 0 && fragments[index].Text.Trim().Length > 0).ToArray();
        var largest = sized.Length == 0 ? 0 : sized.Max(index => fragments[index].FontSize!.Value);
        var rows = new Rows<int>(sized, index => fragments[index].BoundingBox.Y);
        var whole = new bool[fragments.Count];
        // The struck characters of each placed fragment a line crosses.
        var struckCharacters = new bool[]?[fragments.Count];
        var crossings = new List<int[]>();
        foreach (var (left, right, y) in strikes)
        {
            var partial = new List<int>();
            foreach (var index in rows.Between(y - StrikeToEm * largest, y))
            {
                var fragment = fragments[index];
                var size = fragment.FontSize!.Value;
                var em = (y - fragment.BoundingBox.Y) / size;
                if (em < StrikeFromEm - 0.05 || em > StrikeToEm) continue;
                var start = fragment.BoundingBox.X;
                var overlap = Overlap(left, right, start, limits[fragment]);
                if (overlap < 0.3 * size) continue;
                if (Placed(fragment) is { } glyphs)
                {
                    var struck = struckCharacters[index] ??= new bool[glyphs.Count];
                    for (var character = 0; character < glyphs.Count; character++)
                        if ((glyphs[character].Left + glyphs[character].Right) / 2 is var middle && middle >= left && middle <= right)
                            struck[character] = true;
                    continue;
                }
                // The line starts at the show and runs through most of its width.
                if (left <= start + 0.35 * size && right >= start + 0.7 * Width(fragment)) whole[index] = true;
                else partial.Add(index);
            }
            partial.Sort();
            crossings.Add(partial.ToArray());
        }
        // Once per set of fragments: both lines of a double strikethrough cross the same ones.
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var unplaced = new List<IReadOnlyList<PdfTextRegion>>();
        foreach (var partial in crossings)
        {
            var open = partial.Where(index => !whole[index]).ToArray();
            if (open.Length > 0 && reported.Add(string.Join(",", open)))
                unplaced.Add(open.Select(index => fragments[index]).ToArray());
        }
        var result = fragments.Select((fragment, index) =>
            whole[index] ? fragment with { Struck = [new TextRange(0, fragment.Text.Length)] }
            : struckCharacters[index] is { } struck && Ranges(fragment.Text, struck) is { } ranges ? fragment with { Struck = ranges }
            : fragment).ToList();
        return (result, unplaced);
    }

    /// <summary>The placed characters of a fragment: one edge pair per character of its text.</summary>
    private static IReadOnlyList<(double Left, double Right)>? Placed(PdfTextRegion fragment) =>
        fragment.Glyphs is { } glyphs && glyphs.Count == fragment.Text.Length ? glyphs : null;

    /// <summary>A placed fragment's struck characters as ranges: the whole text when every character
    /// but white space is struck, as a strikethrough over a whole show marks it; null when no character
    /// but white space is.</summary>
    private static IReadOnlyList<TextRange>? Ranges(string text, bool[] struck)
    {
        var ranges = new List<TextRange>();
        for (var index = 0; index < text.Length;)
        {
            if (!struck[index]) { index++; continue; }
            var end = index;
            while (end < text.Length && struck[end]) end++;
            // A line over nothing but a space strikes nothing.
            if (!text.AsSpan(index, end - index).IsWhiteSpace()) ranges.Add(new TextRange(index, end));
            index = end;
        }
        if (ranges.Count == 0) return null;
        return Enumerable.Range(0, text.Length).All(index => struck[index] || char.IsWhiteSpace(text[index]))
            ? [new TextRange(0, text.Length)]
            : ranges;
    }

    /// <summary>Struck ranges of a merged line or cell: each part's own ranges, shifted by where the
    /// part starts in the joined text.</summary>
    public static IReadOnlyList<TextRange>? Shift(IReadOnlyList<TextRange>? ranges, int offset) =>
        ranges is not { Count: > 0 } ? null : ranges.Select(range => new TextRange(range.Start + offset, range.End + offset)).ToArray();

    public static IReadOnlyList<TextRange>? Join(IReadOnlyList<TextRange>? left, IReadOnlyList<TextRange>? right) =>
        left is not { Count: > 0 } ? right : right is not { Count: > 0 } ? left : [.. left, .. right];

    /// <summary>The warning for a strikethrough over part of <paramref name="fragments"/>.</summary>
    internal static string Describe(int pageNumber, IReadOnlyList<PdfTextRegion> fragments)
    {
        var snippet = Snippet(string.Join(" ", fragments.Select(fragment => fragment.Text)));
        return string.Create(CultureInfo.InvariantCulture,
            $"PdfStrikethroughUnplaced: PDF page {pageNumber}: a strikethrough crosses part of the text \"{snippet}\"; which characters it strikes could not be determined, so the text is output without the mark. Compare with page {pageNumber} of the original.");
    }

    // Text a viewer shows, bounded and on one line, for a diagnostic.
    private static string Snippet(string text)
    {
        var shown = new string(text.Select(character => char.IsControl(character) || char.IsSurrogate(character) ? ' ' : character).ToArray()).Trim();
        return shown.Length <= 40 ? shown : shown[..40] + "…";
    }

    private static bool Sits(double y, double thickness, PdfTextRegion fragment)
    {
        var size = fragment.FontSize!.Value;
        var em = (y - fragment.BoundingBox.Y) / size;
        return em >= LowestEm && em <= HighestEm && thickness <= MaxThicknessEm * size;
    }

    /// <summary>How far right each show's glyphs reach: where they end when the font gives its glyph
    /// widths; otherwise its typical width with a quarter to spare, but never past the next show on its
    /// line, which starts where this one's glyphs end.</summary>
    private static Dictionary<PdfTextRegion, double> Limits(IReadOnlyList<PdfTextRegion> fragments)
    {
        var limits = new Dictionary<PdfTextRegion, double>(ReferenceEqualityComparer.Instance);
        var lines = fragments.GroupBy(fragment => Math.Round(fragment.BoundingBox.Y)).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var fragment in fragments)
        {
            if (fragment.MeasuredWidth)
            {
                limits[fragment] = fragment.BoundingBox.X + fragment.BoundingBox.Width;
                continue;
            }
            var start = fragment.BoundingBox.X;
            var limit = start + 1.25 * EstimatedWidth(fragment);
            var key = Math.Round(fragment.BoundingBox.Y);
            foreach (var nearby in new[] { key - 1, key, key + 1 })
                if (lines.TryGetValue(nearby, out var line))
                    foreach (var other in line)
                        if (!ReferenceEquals(other, fragment) && other.BoundingBox.X > start + 0.1 &&
                            Math.Abs(other.BoundingBox.Y - fragment.BoundingBox.Y) <= 0.2 * fragment.FontSize!.Value)
                            limit = Math.Min(limit, other.BoundingBox.X);
            limits[fragment] = limit;
        }
        return limits;
    }

    /// <summary>A thin horizontal line, or a dashed one drawn dash by dash: the paths it was drawn
    /// with, and how far past its ends its pattern may stop short of what it meets (the usual gap
    /// between its dashes; none for a solid line).</summary>
    private sealed record Line(int[] Paths, double Left, double Right, double Y, double Thickness, double Reach);

    private static List<Line> Lines(IReadOnlyList<VisualPath> paths)
    {
        var found = new List<(int Path, (double Left, double Right, double Y, double Thickness) Line)>();
        for (var index = 0; index < paths.Count; index++)
            if (HorizontalOf(paths[index]) is { } line) found.Add((index, line));
        var strokes = found.Select(item => new PdfDashRuns.Stroke(true, item.Line.Y, item.Line.Left, item.Line.Right, item.Line.Thickness <= 1.5)).ToArray();
        var lines = new List<Line>();
        var inRun = new bool[found.Count];
        foreach (var run in PdfDashRuns.Find(strokes))
        {
            foreach (var member in run) inRun[member] = true;
            lines.Add(new Line(run.Select(member => found[member].Path).ToArray(), found[run[0]].Line.Left, found[run[^1]].Line.Right,
                run.Average(member => found[member].Line.Y), run.Max(member => found[member].Line.Thickness),
                Math.Min(PdfDashRuns.MedianGap(strokes, run), 6)));
        }
        for (var item = 0; item < found.Count; item++)
            if (!inRun[item])
                lines.Add(new Line([found[item].Path], found[item].Line.Left, found[item].Line.Right, found[item].Line.Y, found[item].Line.Thickness, 0));
        return lines.OrderBy(line => line.Paths.Min()).ToList();
    }

    // The other line of a double strikethrough or double underline: as thin, over the same span, a
    // little above or below. A line on the same level that continues this one (the next cell's
    // border) is not a twin, nor is a bar or box thicker than a decoration.
    private static bool IsTwin(Line line, Line other, double size)
    {
        var gap = Math.Abs(other.Y - line.Y);
        return other.Thickness <= MaxThicknessEm * size && gap > 0.3 && gap <= 0.4 * size &&
            Math.Abs(other.Left - line.Left) <= 0.2 * size && Math.Abs(other.Right - line.Right) <= 0.2 * size;
    }

    /// <summary>Items grouped by height in bands, to look up those near a height without scanning all of them.</summary>
    private sealed class Rows<T>
    {
        private const double Band = 4;
        private readonly Dictionary<long, List<T>> bands = [];

        public Rows(IEnumerable<T> items, Func<T, double> height)
        {
            foreach (var item in items)
            {
                var key = Key(height(item));
                if (!bands.TryGetValue(key, out var band)) bands[key] = band = [];
                band.Add(item);
            }
        }

        /// <summary>Every item whose height may lie in [low, high]; callers test the exact condition.</summary>
        public IEnumerable<T> Between(double low, double high)
        {
            if (!double.IsFinite(low) || !double.IsFinite(high) || low > high) yield break;
            var (from, to) = (Key(low), Key(high));
            if (to - from > bands.Count)
            {
                foreach (var (key, band) in bands)
                    if (key >= from && key <= to)
                        foreach (var item in band) yield return item;
                yield break;
            }
            for (var key = from; key <= to; key++)
                if (bands.TryGetValue(key, out var band))
                    foreach (var item in band) yield return item;
        }

        private static long Key(double height) => double.IsFinite(height) ? (long)Math.Floor(Math.Clamp(height, -1e12, 1e12) / Band) : long.MinValue;
    }

    /// <summary>Every straight piece of every drawn path, filed by the squares it passes near, to tell
    /// whether a line's end meets one without measuring against all of them.</summary>
    private sealed class SegmentGrid
    {
        private const double Square = 16;
        // A piece crossing more squares than this is measured against every end instead.
        private const double MaxSquares = 4096;
        private readonly List<(int Path, double X1, double Y1, double X2, double Y2)> pieces = [];
        private readonly Dictionary<(long X, long Y), List<int>> squares = [];
        private readonly List<int> everywhere = [];

        public SegmentGrid(IReadOnlyList<VisualPath> paths)
        {
            for (var index = 0; index < paths.Count; index++)
                if (paths[index].Points is { Count: >= 2 } points)
                    for (var point = 1; point < points.Count; point++)
                        Add((index, points[point - 1].X, points[point - 1].Y, points[point].X, points[point].Y));
        }

        private void Add((int Path, double X1, double Y1, double X2, double Y2) piece)
        {
            if (!double.IsFinite(piece.X1) || !double.IsFinite(piece.Y1) || !double.IsFinite(piece.X2) || !double.IsFinite(piece.Y2)) return;
            var number = pieces.Count;
            pieces.Add(piece);
            var (left, right) = (Key(Math.Min(piece.X1, piece.X2) - TouchDistance), Key(Math.Max(piece.X1, piece.X2) + TouchDistance));
            var (bottom, top) = (Key(Math.Min(piece.Y1, piece.Y2) - TouchDistance), Key(Math.Max(piece.Y1, piece.Y2) + TouchDistance));
            if ((double)(right - left + 1) * (top - bottom + 1) > MaxSquares)
            {
                everywhere.Add(number);
                return;
            }
            for (var x = left; x <= right; x++)
                for (var y = bottom; y <= top; y++)
                {
                    if (!squares.TryGetValue((x, y), out var list)) squares[(x, y)] = list = [];
                    list.Add(number);
                }
        }

        /// <summary>Whether a piece of a path not in <paramref name="ignored"/> comes within reach of
        /// the end (x, y), or of the stretch <paramref name="reach"/> beyond it in
        /// <paramref name="direction"/>, where a dash pattern may stop short of what it meets.</summary>
        public bool Touches(HashSet<int> ignored, double x, double y, int direction, double reach)
        {
            for (var step = 0.0; ; step = Math.Min(step + 1, reach))
            {
                if (Meets(ignored, x + direction * step, y)) return true;
                if (step >= reach) return false;
            }
        }

        private bool Meets(HashSet<int> ignored, double x, double y)
        {
            bool Near(int number) => !ignored.Contains(pieces[number].Path) && Distance(x, y, pieces[number]) <= TouchDistance;
            return squares.TryGetValue((Key(x), Key(y)), out var list) && list.Any(Near) || everywhere.Any(Near);
        }

        private static long Key(double value) => (long)Math.Floor(Math.Clamp(value, -1e12, 1e12) / Square);
    }

    private static double Distance(double x, double y, (int Path, double X1, double Y1, double X2, double Y2) segment)
    {
        var (dx, dy) = (segment.X2 - segment.X1, segment.Y2 - segment.Y1);
        var length = dx * dx + dy * dy;
        var t = length <= 0 ? 0 : Math.Clamp(((x - segment.X1) * dx + (y - segment.Y1) * dy) / length, 0, 1);
        return Math.Sqrt(Math.Pow(x - (segment.X1 + t * dx), 2) + Math.Pow(y - (segment.Y1 + t * dy), 2));
    }

    // A show's width: measured with its font's glyph widths when known, else estimated.
    private static double Width(PdfTextRegion fragment) => fragment.MeasuredWidth ? fragment.BoundingBox.Width : EstimatedWidth(fragment);

    // A typical width: full-width characters an em, others a little over half an em.
    private static double EstimatedWidth(PdfTextRegion fragment) =>
        fragment.Text.Sum(character => IsWide(character) ? 1.0 : 0.55) * fragment.FontSize!.Value;

    private static bool IsWide(char character) =>
        character is >= 'ᄀ' and <= 'ᅟ' or >= '⺀' and <= '꓏' or >= '가' and <= '힣' or
            >= '豈' and <= '﫿' or >= '︰' and <= '﹏' or >= '＀' and <= '｠' or >= '￠' and <= '￦' ||
        char.IsSurrogate(character);

    private static double Overlap(double left, double right, double otherLeft, double otherRight) =>
        Math.Min(right, otherRight) - Math.Max(left, otherLeft);

    // A painted thin horizontal line: a stroked two-point line, or a filled axis-aligned rectangle no
    // taller than it is long / 4; unpainted subpaths are not decorations. The shape alone decides: a
    // curve is recorded with its control points, so a two-point stroke is always straight, while the
    // confidence visual inference gives a path says nothing about its shape (it is lowered for every
    // path when inference gives up on a busy page, and for a bar it tried to label).
    private static (double Left, double Right, double Y, double Thickness)? HorizontalOf(VisualPath path)
    {
        if (path.Points is not { Count: >= 2 } points) return null;
        var left = points.Min(point => point.X); var right = points.Max(point => point.X);
        var bottom = points.Min(point => point.Y); var top = points.Max(point => point.Y);
        var length = right - left;
        if (length <= 0) return null;
        if (path.IsStroked == true && path.IsFilled != true && points.Count == 2)
            return top - bottom <= length * .02 ? (left, right, (top + bottom) / 2, 0) : null;
        if (path.IsFilled == true && path.IsStroked != true && points.Count is 4 or 5 &&
            points.Take(4).All(point => (point.X == left || point.X == right) && (point.Y == bottom || point.Y == top)))
            return length >= 4 * (top - bottom) ? (left, right, (top + bottom) / 2, top - bottom) : null;
        return null;
    }
}
