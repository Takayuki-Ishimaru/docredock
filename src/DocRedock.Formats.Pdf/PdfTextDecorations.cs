using System.Globalization;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.Pdf;

/// <summary>
/// Underlines and strikethroughs drawn over PDF text. A PDF has no text decoration of its own: a
/// producer paints a thin horizontal line or bar across the glyphs (Word and LibreOffice a filled bar
/// or a stroked line; a dashed or dotted one piece by piece; a wavy one as a curve, or Chrome as a
/// pattern in a bar). Such a line is text formatting, not a drawing, so it is taken out of the visual
/// graph before tables are inferred from its lines, and the text it covers is marked as struck or
/// underlined; Acrobat's StrikeOut, Underline and Squiggly annotations, and a Line or Ink annotation
/// laid straight across text, mark it the same way. When the font gives its glyph widths, each
/// character's place is known and a line marks exactly the characters it crosses. Otherwise only text
/// a line covers as a whole show (one Tj/TJ string, which is how most producers write a run of one
/// format) is marked. A strikethrough over part of a show is reported instead of guessed, because the
/// text it cancels is content; an underline over part of one is left out, because it is emphasis.
/// A drawn underline that only styles a link (it lies within the link annotation's area) is not emphasis,
/// and neither is a rule drawn under a row of cells, which runs over the gaps between the columns; an
/// Underline or Squiggly annotation is somebody's markup and marks its text, link or not.
/// </summary>
internal static class PdfTextDecorations
{
    internal enum DecorationKind { Strike, Underline }

    /// <summary>A decoration line: the paths it was drawn with (every dash of a dashed one), and where
    /// it lies in page space.</summary>
    internal sealed record Decoration(IReadOnlyList<string> PathIds, DecorationKind Kind, double Left, double Right, double Y);

    /// <summary>A line to put on the text it crosses, from a drawn decoration or an annotation.
    /// <paramref name="Kind"/> is null for a line drawn by hand (a Line or Ink annotation), whose kind the text
    /// it lies on decides, <paramref name="Band"/> is how far its points stray vertically (0 for a line
    /// whose shape was judged already), and <paramref name="Drawn"/> says it was found among the page's
    /// drawings (an annotation is somebody's deliberate markup of the text it covers).</summary>
    internal readonly record struct TextLine(DecorationKind? Kind, double Left, double Right, double Y, double Band = 0,
        bool Drawn = false);

    /// <summary>What the annotations of a page say about its text: the lines they put on it, and the
    /// areas of its links.</summary>
    internal sealed record PageMarkup(IReadOnlyList<TextLine> Lines, IReadOnlyList<Geometry> Links)
    {
        public static PageMarkup None { get; } = new([], []);
    }

    // A decoration is no thicker than this many ems (Word's bars are about 0.05-0.07 em) and at least
    // four times as long as it is thick.
    private const double MaxThicknessEm = 0.2;
    // A wavy underline drawn as a curve or zigzag wavers within a band this tall, control points
    // included (LibreOffice's is 0.23 em), and is at least four times as long as the band.
    private const double MaxWaveEm = 0.3;
    // Chrome fills a wavy underline with a pattern in a bar 0.3 to 0.5 em tall that lies under the
    // baseline. A bar this much thicker is an underline only if it hangs under the text that way: a
    // marker bar behind the letters reaches above the baseline and is no decoration. The bar's top may
    // rise this far above the baseline.
    private const double MaxBelowEm = 0.55;
    private const double BelowSlackEm = 0.05;
    // Where a line can lie relative to the baseline of the text it decorates, in ems: an underline sits
    // a little below the baseline, a strikethrough about half the x-height above it (0.18-0.35 em in
    // Word and LibreOffice). A line higher than half an em is over the letters, not through them: an
    // overline (a negated signal, a mathematical bar), which stays a drawing. A line above the baseline
    // crosses the letters, so it is a strikethrough, however low a hand drew it (0.1 em in Word).
    private const double LowestEm = -0.35;
    private const double HighestEm = 0.5;
    private const double StrikeFromEm = 0.05;
    private const double StrikeToEm = 0.5;
    // Where an underline can lie when it is applied: an annotation covers the line box, whose bottom
    // is lower than a drawn underline.
    private const double UnderlineFromEm = -0.5;
    private const double UnderlineToEm = 0.2;
    // A line drawn by hand across text counts as straight when it stays within this many ems above and
    // below its middle.
    private const double HandDrawnBandEm = 0.2;
    // A line drawn by hand runs on past the text it crosses (Excel, Word and PowerPoint draw it from where the
    // pointer went down to where it came up: up to 2.6 ems past it in LibreOffice's PDFs of such drawings, and
    // a little before it). Through the text, covering all of it, it may run past either end by up to this many
    // ems; the text it crosses is all the text it reaches, so it crosses no other text on its baseline.
    private const double MaxStrikeOvershootEm = 4;
    // Under the text it may run past the end of one show of text, by less (a rule under a heading or a row of cells
    // is common and runs far past the text), and only right under the baseline.
    private const double MaxUnderlineOvershootEm = 1.5;
    private const double UnderlineOvershootLowestEm = -0.25;
    // A line this many ems or less above the baseline of other text lies on the tops of its glyphs
    // (cap height is about 0.7 em; a fraction's denominator sits about 0.8 em under its bar).
    private const double TopUnderEm = 0.85;
    // How close a line's end must come to another drawn line or shape to meet it.
    private const double TouchDistance = 1.5;
    // The bars of a dashed line are no thicker than this; the dots and dashes of a dotted one are no thicker
    // than the thicker of this and 0.2 em of the largest text on the page.
    private const double MaxDashThickness = 1.5;
    // A link annotation's area holds the underline of its text within this distance of its edges, and
    // is no taller than this many times the largest text on the page (a link over a whole page is no
    // link style of the text under it).
    private const double LinkSlack = 2;
    private const double MaxLinkHeightEm = 3;
    // A space is a quarter to a third of an em and a justified one rarely more than one em. The columns of
    // a table are further apart than that, so a drawn underline that runs on over a gap this wide between two
    // of the characters it lies under is a rule under a row of cells (the header's, in a table without
    // vertical lines), not the underline of a text.
    private const double MaxUnderlinedGapEm = 1;

    /// <summary>The thin horizontal lines that sit on upright text and stay within it, neither end
    /// touching another drawn line or shape (a grid's rule meets the rules across it, a connector the
    /// boxes it joins). A dashed or dotted line drawn piece by piece is judged as one line, so the
    /// dashes of a rule under a row of text are not underlines one by one, while a dashed underline
    /// still is one. A wavy line is a line at the middle of its band. The other line of a double
    /// strikethrough or double underline runs close beside a line over the same span without meeting
    /// it, so it does not count as touching. A line drawn by hand across all of a text may run on past
    /// it, by a few ems at most (see <see cref="MayOvershoot"/>).</summary>
    public static IReadOnlyList<Decoration> Find(VisualGraph graph, IReadOnlyList<PdfTextRegion> fragments,
        CancellationToken cancellationToken = default)
    {
        var paths = graph.Paths ?? [];
        var texts = fragments.Where(fragment => fragment.FontSize is > 0 && fragment.Text.Trim().Length > 0).ToArray();
        if (paths.Count == 0 || texts.Length == 0) return [];
        var largest = texts.Max(fragment => fragment.FontSize!.Value);
        var lines = Lines(paths, largest);
        if (lines.Count == 0) return [];
        var result = new List<Decoration>();
        var limits = Limits(texts);
        double Reach(PdfTextRegion fragment) => limits[fragment];
        var textRows = new Rows<PdfTextRegion>(texts, fragment => fragment.BoundingBox.Y);
        var lineRows = new Rows<Line>(lines, line => line.Y);
        SegmentGrid? segments = null;
        for (var number = 0; number < lines.Count; number++)
        {
            if ((number & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var line = lines[number];
            var candidates = textRows.Between(line.Y - HighestEm * largest, line.Y - LowestEm * largest)
                .Where(fragment => Sits(line, fragment) && Overlap(line.Left, line.Right, fragment.BoundingBox.X, Reach(fragment)) > 0)
                .ToArray();
            if (candidates.Length == 0) continue;
            var size = candidates.Max(fragment => fragment.FontSize!.Value);
            if (line.Right - line.Left < 0.4 * size) continue;
            var baseline = candidates.OrderBy(fragment => Math.Abs(fragment.BoundingBox.X - line.Left)).ThenBy(fragment => fragment.ReadingOrder).First();
            var em = (line.Y - baseline.BoundingBox.Y) / baseline.FontSize!.Value;
            // The whole line lies over the text it decorates, not past it, give or take the space after a word;
            // but a line drawn by hand across all of it runs on past it.
            var (textLeft, textRight) = (candidates.Min(fragment => fragment.BoundingBox.X), candidates.Max(fragment => Reach(fragment)));
            if ((line.Left < textLeft - 0.3 * size || line.Right > textRight + 0.3 * size) &&
                !MayOvershoot(line, em, size, textLeft, textRight, candidates.Length)) continue;
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
            // A dash pattern may stop short of the end of the text it runs under, by up to a gap.
            result.Add(new Decoration(line.Paths.Select(index => paths[index].Id).ToArray(),
                em >= StrikeFromEm ? DecorationKind.Strike : DecorationKind.Underline, line.Left, line.Right + line.Reach, line.Y));
        }
        return result;
    }

    /// <summary>Marks the text the lines cover as struck or underlined. A fragment whose characters are
    /// placed (its font gives glyph widths) gets exactly the characters a line crosses the middle of.
    /// Any other fragment is marked as a whole when a line covers it. Those a strikethrough crosses only
    /// part of are returned, once per set of fragments: their struck characters cannot be told apart
    /// without glyph widths, and the text is content. An underline over part of such a fragment marks
    /// nothing and is not reported: it is emphasis. The stretches of a drawn underline that lie within a
    /// link's area are the link's style, not emphasis, and mark nothing; nor does a drawn underline that
    /// runs over the gap between columns (see <see cref="RunsOverColumns"/>). An annotation marks the text
    /// under it whether a link is there or not.</summary>
    public static (List<PdfTextRegion> Fragments, List<IReadOnlyList<PdfTextRegion>> Unplaced) Apply(
        IReadOnlyList<PdfTextRegion> fragments, IReadOnlyList<TextLine> lines, IReadOnlyList<Geometry> links)
    {
        // Several lines can cross one fragment (the two lines of a double strikethrough, a drawn line
        // and a StrikeOut annotation): a fragment one of them covers is marked, and a line that crosses
        // only part of a fragment is reported unless another line strikes that fragment whole.
        var limits = Limits(fragments.Where(fragment => fragment.FontSize is > 0).ToArray());
        var sized = Enumerable.Range(0, fragments.Count)
            .Where(index => fragments[index].FontSize is > 0 && fragments[index].Text.Trim().Length > 0).ToArray();
        var largest = sized.Length == 0 ? 0 : sized.Max(index => fragments[index].FontSize!.Value);
        var rows = new Rows<int>(sized, index => fragments[index].BoundingBox.Y);
        var struck = new Marks(fragments.Count);
        var underlined = new Marks(fragments.Count);
        var crossings = new List<int[]>();
        foreach (var line in lines)
        {
            var partial = new List<int>();
            // Fragments by the height of their baseline under the line, as far as a mark of its kind reaches.
            var (above, below) = line.Kind switch
            {
                DecorationKind.Strike => (StrikeToEm, 0.0),
                DecorationKind.Underline => (UnderlineToEm, -UnderlineFromEm),
                _ => (StrikeToEm, -UnderlineFromEm),
            };
            IReadOnlyList<(double Left, double Right)>? underlines = null;
            // A drawn underline that runs over the gap between columns is a rule under a row of cells: it was
            // taken out of the drawings with the other underlines, and marks no text.
            if (line.Drawn && line.Kind == DecorationKind.Underline)
            {
                underlines = Uncovered(line.Left, line.Right, line.Y, links, MaxLinkHeightEm * largest);
                if (RunsOverColumns(fragments, rows.Between(line.Y - above * largest, line.Y + below * largest), line.Y, underlines, limits)) continue;
            }
            foreach (var index in rows.Between(line.Y - above * largest, line.Y + below * largest))
            {
                var fragment = fragments[index];
                var size = fragment.FontSize!.Value;
                var em = (line.Y - fragment.BoundingBox.Y) / size;
                var kind = line.Kind ?? (em >= StrikeFromEm ? DecorationKind.Strike : DecorationKind.Underline);
                if (kind == DecorationKind.Strike ? em < StrikeFromEm - 0.05 || em > StrikeToEm : em < UnderlineFromEm || em > UnderlineToEm) continue;
                // A line drawn by hand is a decoration only where it runs straight over this text.
                if (line.Kind is null && line.Band > HandDrawnBandEm * size) continue;
                var marks = kind == DecorationKind.Strike ? struck : underlined;
                // Only a drawn underline can be a link's style; an annotation is somebody's markup of the text, links or not.
                IReadOnlyList<(double Left, double Right)> stretches = kind == DecorationKind.Strike || !line.Drawn ? [(line.Left, line.Right)]
                    : underlines ??= Uncovered(line.Left, line.Right, line.Y, links, MaxLinkHeightEm * largest);
                var start = fragment.BoundingBox.X;
                foreach (var (left, right) in stretches)
                {
                    var overlap = Overlap(left, right, start, limits[fragment]);
                    if (overlap < 0.3 * size) continue;
                    if (Placed(fragment) is { } glyphs)
                    {
                        var characters = marks.Characters[index] ??= new bool[glyphs.Count];
                        for (var character = 0; character < glyphs.Count; character++)
                            if ((glyphs[character].Left + glyphs[character].Right) / 2 is var middle && middle >= left && middle <= right)
                                characters[character] = true;
                        continue;
                    }
                    // The line starts at the show and runs through most of its width.
                    if (left <= start + 0.35 * size && right >= start + 0.7 * Width(fragment)) marks.Whole[index] = true;
                    else if (kind == DecorationKind.Strike) partial.Add(index);
                }
            }
            partial.Sort();
            crossings.Add(partial.ToArray());
        }
        // Once per set of fragments: both lines of a double strikethrough cross the same ones.
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var unplaced = new List<IReadOnlyList<PdfTextRegion>>();
        foreach (var partial in crossings)
        {
            var open = partial.Where(index => !struck.Whole[index]).ToArray();
            if (open.Length > 0 && reported.Add(string.Join(",", open)))
                unplaced.Add(open.Select(index => fragments[index]).ToArray());
        }
        var result = fragments.Select((fragment, index) =>
        {
            var marked = struck.Of(index, fragment.Text) is { } struckRanges ? fragment with { Struck = struckRanges } : fragment;
            return underlined.Of(index, fragment.Text) is { } underlinedRanges ? marked with { Underlined = underlinedRanges } : marked;
        }).ToList();
        return (result, unplaced);
    }

    /// <summary>Whether an underline runs over a gap as wide as <see cref="MaxUnderlinedGapEm"/> between two of
    /// the characters it lies under: a rule under a row of cells is as long as the row, an underline as long
    /// as its text. Each stretch of the line is judged by itself, since what lies between two stretches (the text
    /// of a link) is not under the line. A gap is known only where the fonts give the characters' places; when
    /// any text a stretch lies under has none, the line is taken for an underline.</summary>
    private static bool RunsOverColumns(IReadOnlyList<PdfTextRegion> fragments, IEnumerable<int> window, double y,
        IReadOnlyList<(double Left, double Right)> stretches, IReadOnlyDictionary<PdfTextRegion, double> limits)
    {
        foreach (var (left, right) in stretches)
        {
            var spans = new List<(double Left, double Right)>();
            var size = 0.0;
            foreach (var index in window)
            {
                var fragment = fragments[index];
                var fragmentSize = fragment.FontSize!.Value;
                var em = (y - fragment.BoundingBox.Y) / fragmentSize;
                if (em < UnderlineFromEm || em > UnderlineToEm) continue;
                if (Overlap(left, right, fragment.BoundingBox.X, limits[fragment]) < 0.3 * fragmentSize) continue;
                if (Placed(fragment) is not { } glyphs) return false;
                size = Math.Max(size, fragmentSize);
                for (var character = 0; character < glyphs.Count; character++)
                    if (!char.IsWhiteSpace(fragment.Text[character]) &&
                        (glyphs[character].Left + glyphs[character].Right) / 2 is var middle && middle >= left && middle <= right)
                        spans.Add(glyphs[character]);
            }
            if (spans.Count < 2) continue;
            spans.Sort();
            var reach = spans[0].Right;
            foreach (var span in spans.Skip(1))
            {
                if (span.Left - reach > MaxUnderlinedGapEm * size) return true;
                reach = Math.Max(reach, span.Right);
            }
        }
        return false;
    }

    /// <summary>The marks of one kind: the fragments a line covers as a whole, and the characters of each
    /// placed fragment a line crosses.</summary>
    private sealed class Marks(int count)
    {
        public bool[] Whole { get; } = new bool[count];
        public bool[]?[] Characters { get; } = new bool[]?[count];

        /// <summary>The marked ranges of the text of fragment <paramref name="index"/>; null when none.</summary>
        public IReadOnlyList<TextRange>? Of(int index, string text) =>
            Whole[index] ? [new TextRange(0, text.Length)] : Characters[index] is { } marked ? Ranges(text, marked) : null;
    }

    /// <summary>The placed characters of a fragment: one edge pair per character of its text.</summary>
    private static IReadOnlyList<(double Left, double Right)>? Placed(PdfTextRegion fragment) =>
        fragment.Glyphs is { } glyphs && glyphs.Count == fragment.Text.Length ? glyphs : null;

    /// <summary>A placed fragment's marked characters as ranges: the whole text when every character
    /// but white space is marked, as a line over a whole show marks it; null when no character but
    /// white space is.</summary>
    private static IReadOnlyList<TextRange>? Ranges(string text, bool[] marked)
    {
        var ranges = new List<TextRange>();
        for (var index = 0; index < text.Length;)
        {
            if (!marked[index]) { index++; continue; }
            var end = index;
            while (end < text.Length && marked[end]) end++;
            // A line over nothing but a space marks nothing.
            if (!text.AsSpan(index, end - index).IsWhiteSpace()) ranges.Add(new TextRange(index, end));
            index = end;
        }
        if (ranges.Count == 0) return null;
        return Enumerable.Range(0, text.Length).All(index => marked[index] || char.IsWhiteSpace(text[index]))
            ? [new TextRange(0, text.Length)]
            : ranges;
    }

    /// <summary>The stretches of an underline that lie outside every link's area. A link is styled with
    /// an underline of its own that is not emphasis (LibreOffice, Word and Chrome all underline links),
    /// so the part of a line that lies within a link's area, up to <see cref="LinkSlack"/> beyond its
    /// edges, is left out; the rest of the line stays.</summary>
    private static IReadOnlyList<(double Left, double Right)> Uncovered(double left, double right, double y,
        IReadOnlyList<Geometry> links, double maxLinkHeight)
    {
        var stretches = new List<(double Left, double Right)> { (left, right) };
        foreach (var link in links)
        {
            if (link.Height > maxLinkHeight || y < link.Y - LinkSlack || y > link.Y + link.Height + LinkSlack) continue;
            var (from, to) = (link.X - LinkSlack, link.X + link.Width + LinkSlack);
            var rest = new List<(double Left, double Right)>(stretches.Count + 1);
            foreach (var stretch in stretches)
            {
                if (stretch.Right <= from || stretch.Left >= to) { rest.Add(stretch); continue; }
                if (stretch.Left < from) rest.Add((stretch.Left, from));
                if (stretch.Right > to) rest.Add((to, stretch.Right));
            }
            stretches = rest;
        }
        return stretches;
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

    /// <summary>Whether a line that runs past the text it crosses, by more than a space, is a decoration all the
    /// same: it covers all of that text, and runs past it by no more than the limit of its kind.</summary>
    private static bool MayOvershoot(Line line, double em, double size, double textLeft, double textRight, int shows)
    {
        if (line.Left > textLeft + 0.3 * size || line.Right < textRight - 0.3 * size) return false;
        var (before, past) = (textLeft - line.Left, line.Right - textRight);
        return em >= StrikeFromEm
            ? before <= MaxStrikeOvershootEm * size && past <= MaxStrikeOvershootEm * size
            : shows == 1 && em >= UnderlineOvershootLowestEm && before <= 0.3 * size && past <= MaxUnderlineOvershootEm * size;
    }

    private static bool Sits(Line line, PdfTextRegion fragment)
    {
        var size = fragment.FontSize!.Value;
        var em = (line.Y - fragment.BoundingBox.Y) / size;
        if (em < LowestEm || em > HighestEm) return false;
        if (line.Wavy) return line.Thickness <= MaxWaveEm * size;
        if (line.Thickness <= MaxThicknessEm * size) return true;
        // A thicker bar is an underline only when it hangs under the baseline (Chrome's wavy one).
        return line.Thickness <= MaxBelowEm * size && line.Y + line.Thickness / 2 <= fragment.BoundingBox.Y + BelowSlackEm * size;
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

    /// <summary>A thin horizontal line, a dashed or dotted one drawn piece by piece, or a wavy one: the
    /// paths it was drawn with, how thick it is (how tall its band is, for a wavy one), and how far
    /// past its ends its pattern may stop short of what it meets (the usual gap between its dashes;
    /// none for a solid line).</summary>
    private sealed record Line(int[] Paths, double Left, double Right, double Y, double Thickness, double Reach, bool Wavy = false);

    /// <summary>A path seen as a line along a horizontal stretch: a line or bar, a wave, or a dot or dash
    /// that is a line only together with the others of its row.</summary>
    private readonly record struct Shape(double Left, double Right, double Y, double Thickness, bool Wavy = false, bool Dot = false);

    private static List<Line> Lines(IReadOnlyList<VisualPath> paths, double largest)
    {
        // The dots of a dotted line are as thick as the line, which its text allows to be 0.2 em.
        var maxDot = Math.Max(MaxDashThickness, MaxThicknessEm * largest);
        var found = new List<(int Path, Shape Shape)>();
        for (var index = 0; index < paths.Count; index++)
            if ((ShapeOf(paths[index]) ?? DotOf(paths[index], maxDot)) is { } shape) found.Add((index, shape));
        var strokes = found.Select(item => new PdfDashRuns.Stroke(true, item.Shape.Y, item.Shape.Left, item.Shape.Right,
            !item.Shape.Wavy && item.Shape.Thickness <= (item.Shape.Dot ? maxDot : MaxDashThickness))).ToArray();
        var lines = new List<Line>();
        var inRun = new bool[found.Count];
        foreach (var run in PdfDashRuns.Find(strokes))
        {
            foreach (var member in run) inRun[member] = true;
            lines.Add(new Line(run.Select(member => found[member].Path).ToArray(), found[run[0]].Shape.Left, found[run[^1]].Shape.Right,
                run.Average(member => found[member].Shape.Y), run.Max(member => found[member].Shape.Thickness),
                Math.Min(PdfDashRuns.MedianGap(strokes, run), 6)));
        }
        // A dot that is not part of a row of them is no line.
        for (var item = 0; item < found.Count; item++)
            if (!inRun[item] && !found[item].Shape.Dot)
                lines.Add(new Line([found[item].Path], found[item].Shape.Left, found[item].Shape.Right, found[item].Shape.Y,
                    found[item].Shape.Thickness, 0, found[item].Shape.Wavy));
        return lines.OrderBy(line => line.Paths.Min()).ToList();
    }

    // The other line of a double strikethrough or double underline: as thin, over the same span, a
    // little above or below. A line on the same level that continues this one (the next cell's
    // border) is not a twin, nor is a bar or box thicker than a decoration. The same line painted
    // twice (a link's own underline over the underline of the text around it) is a twin too.
    private static bool IsTwin(Line line, Line other, double size)
    {
        var gap = Math.Abs(other.Y - line.Y);
        var (leftShift, rightShift) = (Math.Abs(other.Left - line.Left), Math.Abs(other.Right - line.Right));
        return other.Thickness <= MaxThicknessEm * size && gap <= 0.4 * size &&
            leftShift <= 0.2 * size && rightShift <= 0.2 * size && (gap > 0.3 || leftShift <= 0.5 && rightShift <= 0.5);
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

    private static (double Left, double Right, double Bottom, double Top) Bounds(IReadOnlyList<VisualPathPoint> points)
    {
        double left = double.PositiveInfinity, right = double.NegativeInfinity, bottom = double.PositiveInfinity, top = double.NegativeInfinity;
        foreach (var point in points)
        {
            left = Math.Min(left, point.X); right = Math.Max(right, point.X);
            bottom = Math.Min(bottom, point.Y); top = Math.Max(top, point.Y);
        }
        return (left, right, bottom, top);
    }

    // A painted thin horizontal line: a stroked two-point line, a filled axis-aligned rectangle no
    // taller than it is long / 4, or a stroked open curve or zigzag that stays within a band no taller
    // than it is long / 4 (a wavy underline, at the middle of its band; its control points count, and
    // how tall its band may be is the text's to say). Unpainted subpaths are not decorations. The shape
    // alone decides: a curve is recorded with its control points, so a two-point stroke is always
    // straight, while the confidence visual inference gives a path says nothing about its shape (it is
    // lowered for every path when inference gives up on a busy page, and for a bar it tried to label).
    private static Shape? ShapeOf(VisualPath path)
    {
        if (path.Points is not { Count: >= 2 } points) return null;
        var (left, right, bottom, top) = Bounds(points);
        var length = right - left;
        if (length <= 0) return null;
        if (path.IsStroked == true && path.IsFilled != true)
        {
            if (points.Count == 2) return top - bottom <= length * .02 ? new Shape(left, right, (top + bottom) / 2, 0) : null;
            // A closed path is a box, not a wave.
            if (points[0] == points[^1] || length < 4 * (top - bottom)) return null;
            var straight = top - bottom <= length * .02;
            return new Shape(left, right, (top + bottom) / 2, straight ? 0 : top - bottom, Wavy: !straight);
        }
        if (path.IsFilled == true && path.IsStroked != true && points.Count is 4 or 5 &&
            points.Take(4).All(point => (point.X == left || point.X == right) && (point.Y == bottom || point.Y == top)))
            return length >= 4 * (top - bottom) ? new Shape(left, right, (top + bottom) / 2, top - bottom) : null;
        return null;
    }

    // A small filled shape that is no line, but can be a dot or dash of a dotted or dashed one: a
    // rectangle too short to count as a bar (Chrome draws dashes 3-4 times as long as thick, and dots
    // as squares), or a circle drawn with curves. It is as thick as the line may be, and not much
    // taller than wide: a row of ticks standing on end is no dotted line. How long a dash may be is
    // PdfDashRuns' to say.
    private static Shape? DotOf(VisualPath path, double maxThickness)
    {
        if (path.IsFilled != true || path.Points is not { Count: >= 3 } points) return null;
        var (left, right, bottom, top) = Bounds(points);
        var (width, height) = (right - left, top - bottom);
        if (!(width > 0 && height > 0) || height > maxThickness || width < 0.5 * height) return null;
        return new Shape(left, right, (top + bottom) / 2, height, Dot: true);
    }
}
