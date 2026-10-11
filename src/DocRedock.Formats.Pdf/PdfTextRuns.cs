using System.Buffers;
using System.Text;

namespace DocRedock.Formats.Pdf;

/// <summary>
/// How the shows of a line follow one another when their fonts give glyph widths, so that their edges are
/// where a viewer draws them: whether the gap between two is a word space (a tab, or a space a producer
/// left out of the text and positioned instead), and which small shows are the superscripts and
/// subscripts of the text they sit on. A width estimated at 6 units a character says nothing about the gap
/// after it, so shows whose widths are estimated keep being joined as before.
/// </summary>
internal static class PdfTextRuns
{
    // A space is about a quarter of an em wide (Helvetica 0.278, Times 0.25, Calibri 0.226): two shows
    // this far apart are two words although the first one's text ends without a space.
    internal const double WordGapEm = 0.2;
    // Between an East Asian character and another script word processors add a gap of their own (a fifth
    // of an em in LibreOffice), which is no space of the text; only a gap well beyond it is.
    private const double AsianGapEm = 0.5;

    // Superscripts and subscripts are set at 0.58-0.83 of the size of the text they sit on, up to about
    // 0.4 em above its baseline or 0.25 em below it. The windows are wider, but stop short of what is
    // another line: a drop cap's neighbours are far smaller, furigana sits a full em above.
    private const double ScriptSizeFrom = 0.4;
    private const double ScriptSizeTo = 0.9;
    private const double ScriptLowest = -0.45;
    private const double ScriptHighest = 0.75;
    // How far from the text it sits on a script may start (or end), and how far it may reach into it.
    private const double ScriptGapEm = 0.35;
    private const double ScriptOverlapEm = 0.15;
    // A script is a few characters: a longer run of small text beside larger text is a line of its own.
    private const int MaxScriptCharacters = 12;
    // Bounds of the search for scripts, which is meant for ordinary text: a page with more shows, a show
    // wider or a font larger than this keeps the positions it has.
    private const int MaxSearched = 100_000;
    private const double MaxSearchedWidth = 2_000;
    private const double MaxSearchedSize = 100;
    // Shows piled on one square of the page (text drawn over itself) are not all looked at.
    private const int MaxPerSquare = 256;
    private const double Square = 16;

    /// <summary>Sorts nearly identical baselines as one row before sorting its fragments by X.
    /// Producers round separately positioned text to different decimal places; sorting by exact Y
    /// put the last letters (468.42) before the start of their word (468.4181).</summary>
    public static PdfTextRegion[] OrderByBaseline(IReadOnlyList<PdfTextRegion> fragments)
    {
        var ordered = fragments.OrderByDescending(region => region.BoundingBox.Y).ToArray();
        var result = new List<PdfTextRegion>(ordered.Length);
        for (var from = 0; from < ordered.Length;)
        {
            var to = from + 1;
            // Bound each group against its first baseline, rather than chaining neighbouring
            // baselines until unrelated lines drift into the same row.
            while (to < ordered.Length && ordered[from].BoundingBox.Y - ordered[to].BoundingBox.Y <= 0.01) to++;
            result.AddRange(ordered[from..to].OrderBy(region => region.BoundingBox.X).ThenBy(region => region.ReadingOrder));
            from = to;
        }
        return result.ToArray();
    }

    /// <summary>Whether the gap between the end of <paramref name="left"/> and the start of
    /// <paramref name="right"/> is a word space, for shows whose edges are measured and that do not
    /// already carry a space at the join. A gap beside an East Asian character has to be wider, and none
    /// is one between two East Asian characters, which a line is also spread over (distributed
    /// alignment) without a space between them. In a text set a glyph to a show and spaced out letter by
    /// letter, a gap is a word space only beyond the <paramref name="typical"/> one between its glyphs
    /// (see <see cref="TypicalGaps"/>).</summary>
    public static bool IsWordGap(PdfTextRegion left, PdfTextRegion right, double gap, double typical = 0)
    {
        if (Size(left) is not { } leftSize || Size(right) is not { } rightSize) return false;
        if (Rune.DecodeLastFromUtf16(left.Text, out var last, out _) != OperationStatus.Done ||
            Rune.DecodeFromUtf16(right.Text, out var first, out _) != OperationStatus.Done) return false;
        return IsWordGap(last, first, gap - typical, Math.Max(leftSize, rightSize));
    }

    // Letter spacing moves every glyph of a text on by the same amount (a heading tracked out by a quarter of an em), and
    // the space between its words is only that much wider: it takes the moves of a few glyphs to tell the two apart.
    private const int MinGapsForTypical = 2;

    /// <summary>The gap between glyphs that a text keeps throughout - the median of <paramref name="gaps"/>, the
    /// lower of the two middle ones, so that a few word spaces among them do not raise it - when there are enough
    /// of them to say, otherwise none; never less than none. A word space is a gap wider than this by a word
    /// space (<see cref="IsWordGap(Rune, Rune, double, double)"/> is told the excess), so that a heading with a
    /// quarter of an em between all of its glyphs is one word, not a row of letters.</summary>
    public static double TypicalGap(IReadOnlyCollection<double> gaps) =>
        gaps.Count < MinGapsForTypical ? 0 : Math.Max(0, gaps.Order().ElementAt((gaps.Count - 1) / 2));

    /// <summary>For each fragment of <paramref name="ordered"/> (in reading order), the gap typical of the run of
    /// single-glyph shows it follows - a producer that sets every glyph on its own, as when text is spaced out - in
    /// the units of the page; 0 for any other. A run is shows of one character each, measured, on one baseline,
    /// each beside the one before it as a line joins shows (see <c>MergeAdjacentIntoLines</c>).</summary>
    public static double[] TypicalGaps(IReadOnlyList<PdfTextRegion> ordered)
    {
        var typical = new double[ordered.Count];
        var run = new List<double>();
        for (var from = 1; from < ordered.Count;)
        {
            run.Clear();
            var to = from;
            for (; to < ordered.Count && IsSingleGlyph(ordered[to - 1]) && IsSingleGlyph(ordered[to]) && GapBetween(ordered[to - 1], ordered[to]) is { } gap; to++)
                // As inside a show, a gap beside an East Asian character says nothing of how other letters are spaced.
                if (!IsAsian(Rune.GetRuneAt(ordered[to - 1].Text, 0)) && !IsAsian(Rune.GetRuneAt(ordered[to].Text, 0))) run.Add(gap);
            if (to == from) { from++; continue; }
            Array.Fill(typical, TypicalGap(run), from, to - from);
            from = to;
        }
        return typical;
    }

    private static bool IsSingleGlyph(PdfTextRegion fragment) =>
        Size(fragment) is not null && (fragment.Text.Length == 1 && !char.IsSurrogate(fragment.Text[0]) || fragment.Text.Length == 2 && char.IsSurrogatePair(fragment.Text, 0));

    /// <summary>The gap from <paramref name="left"/> to <paramref name="right"/>, when they are on one baseline and
    /// no further apart than a line glues shows; otherwise null.</summary>
    private static double? GapBetween(PdfTextRegion left, PdfTextRegion right)
    {
        var (a, b) = (left.BoundingBox, right.BoundingBox);
        var gap = b.X - (a.X + a.Width);
        var height = Math.Max(a.Height, b.Height);
        return Math.Abs(a.Y - b.Y) <= Math.Max(1, b.Height * 0.35) && gap >= -height * 0.5 && gap <= height * 1.5 ? gap : null;
    }

    /// <summary>Whether a gap of <paramref name="gap"/> (in the units of <paramref name="size"/>, the font
    /// size) between the characters <paramref name="last"/> and <paramref name="first"/> is a word space.
    /// The same rule decides it between two shows and between two glyphs of one show, where a producer
    /// writes a word space as a move (a large <c>TJ</c> number, character spacing) instead of a space.</summary>
    public static bool IsWordGap(Rune last, Rune first, double gap, double size)
    {
        if (Rune.IsWhiteSpace(last) || Rune.IsWhiteSpace(first)) return false;
        var asian = (IsAsian(last) ? 1 : 0) + (IsAsian(first) ? 1 : 0);
        return asian < 2 && gap >= (asian == 0 ? WordGapEm : AsianGapEm) * size;
    }

    /// <summary>Whether <paramref name="right"/> continues <paramref name="left"/> without a space: both
    /// are measured, on one line (or one is the other's script), no further apart than a line glues
    /// shows (see <c>MergeAdjacentIntoLines</c>), and the gap between them is no word space (beyond the
    /// <paramref name="typical"/> gap between shows of one glyph, see <see cref="TypicalGaps"/>).</summary>
    public static bool Continues(PdfTextRegion left, PdfTextRegion right, double typical = 0)
    {
        if (Size(left) is null || Size(right) is null) return false;
        var (a, b) = (left.BoundingBox, right.BoundingBox);
        var gap = b.X - (a.X + a.Width);
        var height = Math.Max(a.Height, b.Height);
        return (Math.Abs(a.Y - b.Y) <= Math.Max(1, b.Height * 0.35) || IsScriptOf(left, right) || IsScriptOf(right, left)) &&
            gap >= -height * 0.5 && gap <= height * 1.5 && !IsWordGap(left, right, gap, typical);
    }

    /// <summary>Puts the superscripts and subscripts on the baseline of the text they sit on. Sorted by
    /// height, a script lands before or after the whole line it belongs to and is cut off from it ("HO"
    /// and a "2" of its own); on the baseline of its neighbours it is merged in where it stands. A script
    /// is a run of a few characters - shows of one size on one baseline that touch, which may be one
    /// show or a show for each glyph - much smaller than a measured show it touches and a little above
    /// or below that show's baseline; a longer run of small text is a line of its own. Returns
    /// <paramref name="fragments"/> itself when nothing moves.</summary>
    public static IReadOnlyList<PdfTextRegion> PlaceScripts(IReadOnlyList<PdfTextRegion> fragments)
    {
        var measured = new List<int>();
        for (var index = 0; index < fragments.Count; index++)
            if (Size(fragments[index]) is { } size && size <= MaxSearchedSize && fragments[index].BoundingBox.Width <= MaxSearchedWidth) measured.Add(index);
        if (measured.Count < 2 || measured.Count > MaxSearched) return fragments;

        // The shows filed by the squares of the page they span, to find those beside a place without
        // measuring against all of them.
        var squares = new Dictionary<(long X, long Y), List<int>>();
        foreach (var index in measured)
        {
            var box = fragments[index].BoundingBox;
            for (var x = Key(box.X); x <= Key(box.X + box.Width); x++)
            {
                if (!squares.TryGetValue((x, Key(box.Y)), out var list)) squares[(x, Key(box.Y))] = list = [];
                if (list.Count < MaxPerSquare) list.Add(index);
            }
        }
        List<int> Near(double left, double right, double low, double high, double margin)
        {
            var found = new HashSet<int>();
            for (var y = Key(low); y <= Key(high); y++)
                for (var x = Key(left - margin); x <= Key(right + margin); x++)
                    if (squares.TryGetValue((x, y), out var list)) found.UnionWith(list);
            return [.. found];
        }

        // Runs of shows that touch on one baseline at one size.
        var parent = Enumerable.Range(0, fragments.Count).ToArray();
        int Root(int index)
        {
            while (parent[index] != index) index = parent[index] = parent[parent[index]];
            return index;
        }
        foreach (var index in measured)
        {
            var (box, size) = (fragments[index].BoundingBox, Size(fragments[index])!.Value);
            foreach (var other in Near(box.X, box.X + box.Width, box.Y - 0.15 * size, box.Y + 0.15 * size, size))
                if (other > index && IsPeer(fragments[index], fragments[other])) parent[Root(index)] = Root(other);
        }

        PdfTextRegion[]? placed = null;
        foreach (var members in measured.GroupBy(Root).Select(group => group.ToArray()))
        {
            // Runs of small text, and runs of nothing but white space, are not scripts.
            var characters = members.Sum(member => fragments[member].Text.Count(character => !char.IsWhiteSpace(character)));
            if (characters is 0 or > MaxScriptCharacters) continue;
            var script = new Extent(members.Min(member => fragments[member].BoundingBox.X),
                members.Max(member => fragments[member].BoundingBox.X + fragments[member].BoundingBox.Width),
                fragments[members[0]].BoundingBox.Y, members.Max(member => Size(fragments[member])!.Value));
            // What a script sits on is at most 2.5 times its size: its baseline lies within 1.9 sizes
            // above or 1.2 below the script's, and its edge within a size of the script's.
            var root = Root(members[0]);
            PdfTextRegion? text = null;
            foreach (var other in Near(script.Left, script.Right, script.Y - 1.9 * script.Em, script.Y + 1.2 * script.Em, script.Em))
            {
                var candidate = fragments[other];
                if (Root(other) == root || string.IsNullOrWhiteSpace(candidate.Text) || !IsScript(script, Extent.Of(candidate)!.Value)) continue;
                if (text is null || Math.Abs(candidate.BoundingBox.Y - script.Y) < Math.Abs(text.BoundingBox.Y - script.Y)) text = candidate;
            }
            if (text is null || text.BoundingBox.Y == script.Y) continue;
            placed ??= [.. fragments];
            foreach (var member in members)
                placed[member] = fragments[member] with { BoundingBox = fragments[member].BoundingBox with { Y = text.BoundingBox.Y } };
        }
        return placed ?? fragments;
    }

    /// <summary>Where a show or a run of shows lies along the line: its edges, baseline and font size.</summary>
    private readonly record struct Extent(double Left, double Right, double Y, double Em)
    {
        public static Extent? Of(PdfTextRegion fragment) => Size(fragment) is { } size
            ? new Extent(fragment.BoundingBox.X, fragment.BoundingBox.X + fragment.BoundingBox.Width, fragment.BoundingBox.Y, size)
            : null;
    }

    /// <summary>Whether <paramref name="script"/> is set as a superscript or subscript of
    /// <paramref name="text"/>: smaller, a little off its baseline, and touching it on either side.</summary>
    private static bool IsScript(Extent script, Extent text)
    {
        var ratio = script.Em / text.Em;
        var raised = (script.Y - text.Y) / text.Em;
        return ratio >= ScriptSizeFrom && ratio <= ScriptSizeTo && raised >= ScriptLowest && raised <= ScriptHighest &&
            (TouchesAt(script.Left - text.Right, text.Em) || TouchesAt(text.Left - script.Right, text.Em));
    }

    private static bool IsScriptOf(PdfTextRegion script, PdfTextRegion text) =>
        !string.IsNullOrWhiteSpace(script.Text) && !string.IsNullOrWhiteSpace(text.Text) &&
        Extent.Of(script) is { } small && Extent.Of(text) is { } large && IsScript(small, large);

    private static bool TouchesAt(double gap, double size) => gap >= -ScriptOverlapEm * size && gap <= ScriptGapEm * size;

    /// <summary>Whether <paramref name="other"/> is about the size of <paramref name="show"/>, on its
    /// baseline and touching it: the two are shows of one run of text.</summary>
    private static bool IsPeer(PdfTextRegion show, PdfTextRegion other)
    {
        if (Size(show) is not { } size || Size(other) is not { } otherSize) return false;
        if (Math.Abs(otherSize - size) > 0.1 * size || Math.Abs(other.BoundingBox.Y - show.BoundingBox.Y) > 0.15 * size) return false;
        var (a, b) = (show.BoundingBox, other.BoundingBox);
        return IsBeside(b.X - (a.X + a.Width), size) || IsBeside(a.X - (b.X + b.Width), size);
    }

    private static bool IsBeside(double gap, double size) => gap >= -0.15 * size && gap <= 0.5 * size;

    /// <summary>The font size of a show whose edges are measured; null for an estimated width, or for
    /// text that is turned or mirrored, which leaves the gap around it unknown.</summary>
    private static double? Size(PdfTextRegion fragment) =>
        fragment is { MeasuredWidth: true, FontSize: { } size } && size > 0 && double.IsFinite(size) &&
        double.IsFinite(fragment.BoundingBox.X) && double.IsFinite(fragment.BoundingBox.Y) && double.IsFinite(fragment.BoundingBox.Width)
            ? size
            : null;

    private static long Key(double value) => (long)Math.Floor(Math.Clamp(value, -1e12, 1e12) / Square);

    // Japanese and Chinese text: kana, ideographs and their punctuation, the full-width and half-width
    // forms. Hangul is not: Korean is written with spaces between words.
    internal static bool IsAsian(Rune rune) => rune.Value is
        (>= 0x2E80 and <= 0x312F) or (>= 0x3190 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xF900 and <= 0xFAFF) or
        (>= 0xFE30 and <= 0xFE4F) or (>= 0xFF00 and <= 0xFF9F) or (>= 0x1B000 and <= 0x1B16F) or (>= 0x20000 and <= 0x3FFFF);
}
