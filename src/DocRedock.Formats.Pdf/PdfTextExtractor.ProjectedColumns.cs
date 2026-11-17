namespace DocRedock.Formats.Pdf;

public static partial class PdfTextExtractor
{
    // Column detection from the text itself, for pages whose columns do not share baselines. The per-baseline
    // detection of ApplyColumnLayout needs fragments of both columns on three consecutive baselines, and it measures
    // a gutter between two single fragments. Text whose columns have different line positions (a heading with
    // space above it in one column shifts every line below it) and text that arrives a word at a time (so no
    // fragment is 8 em wide) gives it neither. Here the gutter is the strip of the page that no line of the
    // columns reaches into, and a block of lines is a column layout only when both sides read as columns of text.

    /// <summary>Fewest lines each side of the gutter must have to be taken for a column of text.</summary>
    private const int MinProjectedColumnLines = 4;

    /// <summary>Lines wider than this share of the text span reach across the page (title, abstract, wide
    /// paragraph) and say nothing about where the gutter is.</summary>
    private const double SpanningLineShare = 0.55;

    private readonly record struct ProjectedLine(double Top, double Left, double Right);

    private static List<GutterRun> FindProjectedColumnRuns(IReadOnlyList<List<PdfTextRegion>> baselines, IReadOnlyList<double> tops,
        IReadOnlyList<PdfTextRegion> tableFragments, double minX, double maxX)
    {
        var runs = new List<GutterRun>();
        if (baselines.Count < 2 * MinProjectedColumnLines) return runs;
        var all = baselines.SelectMany(baseline => baseline).ToArray();
        // Only measured text has widths a gutter can be read from.
        if (all.Count(fragment => fragment.MeasuredWidth) < all.Length * 0.9) return runs;
        var sizes = all.Select(fragment => fragment.FontSize ?? fragment.BoundingBox.Height).Order().ToArray();
        var em = sizes[sizes.Length / 2];
        if (em <= 0) return runs;
        var channel = Math.Max(12, 1.5 * em);
        var span = maxX - minX;

        // A running header or a page number stands apart from the body of lines, at a distance of several line pitches,
        // and does not belong to the columns.
        var steps = Enumerable.Range(1, baselines.Count - 1).Select(index => tops[index - 1] - tops[index]).Where(step => step > 0).Order().ToArray();
        if (steps.Length == 0) return runs;
        var pitch = steps[steps.Length / 2];
        bool Apart(int index) =>
            (index == 0 || tops[index - 1] - tops[index] > 2.5 * pitch) &&
            (index == baselines.Count - 1 || tops[index] - tops[index + 1] > 2.5 * pitch);

        // The lines of the baselines: runs of fragments with no gap as wide as a gutter between them. Those as wide
        // as the text span the gutter and say nothing about where it is, and a few characters fill none of it.
        var narrow = new List<(double Left, double Right)>();
        // The previous baseline when it is one line reaching across the page, with where it starts.
        (double Left, double Top)? across = null;
        for (var at = 0; at < baselines.Count; at++)
        {
            var baseline = baselines[at];
            var apart = Apart(at);
            var lines = new List<(double Left, double Right)>();
            var left = baseline[0].BoundingBox.X;
            var right = left + baseline[0].BoundingBox.Width;
            for (var index = 1; index <= baseline.Count; index++)
            {
                if (index < baseline.Count && baseline[index].BoundingBox.X - right < channel)
                {
                    right = Math.Max(right, baseline[index].BoundingBox.X + baseline[index].BoundingBox.Width);
                    continue;
                }
                lines.Add((left, right));
                if (index == baseline.Count) break;
                left = baseline[index].BoundingBox.X;
                right = left + baseline[index].BoundingBox.Width;
            }
            // The short last line of a paragraph that runs across the page is no column line, whatever its length.
            var tail = across is { } above && lines.Count == 1 && Math.Abs(lines[0].Left - above.Left) <= 3 &&
                above.Top - tops[at] <= 1.9 * Math.Max(em, baseline.Max(fragment => fragment.FontSize ?? fragment.BoundingBox.Height));
            across = lines.Count == 1 && lines[0].Right - lines[0].Left > span * SpanningLineShare ? (lines[0].Left, tops[at]) : null;
            if (apart || tail) continue;
            foreach (var line in lines)
                if (line.Right - line.Left is var width && width <= span * SpanningLineShare && width >= 6 * em) narrow.Add(line);
        }
        if (narrow.Count < 2 * MinProjectedColumnLines) return runs;

        // The strips between the narrow lines that no line reaches into.
        var gutters = new List<(double Start, double End)>();
        var reach = double.NegativeInfinity;
        foreach (var cluster in narrow.OrderBy(cluster => cluster.Left))
        {
            if (double.IsFinite(reach) && cluster.Left - reach >= channel) gutters.Add((reach, cluster.Left));
            reach = Math.Max(reach, cluster.Right);
        }
        foreach (var (start, end) in gutters)
        {
            // 0 left of the gutter, 1 right of it, 2 across it.
            var sides = baselines.Select(baseline => baseline.Select(fragment =>
                fragment.BoundingBox.X + fragment.BoundingBox.Width <= start + 0.01 ? 0 :
                fragment.BoundingBox.X >= end - 0.01 ? 1 : 2).ToArray()).ToArray();
            var cursor = 0;
            while (cursor < baselines.Count)
            {
                if (sides[cursor].Contains(2)) { cursor++; continue; }
                var last = cursor;
                while (last + 1 < baselines.Count && !sides[last + 1].Contains(2)) last++;
                // The short last line of the paragraph above is left out before the block is cut at gaps in its columns.
                var begin = cursor;
                while (begin < last && EndsTheLineAbove(baselines, tops, sides, begin)) begin++;
                foreach (var (first, final) in SplitAtBlockEnds(baselines, tops, sides, tableFragments, begin, last))
                {
                    if (ReadsAsColumns(baselines, tops, sides, first, final, em) is { } run)
                    {
                        run.FirstBaseline = first;
                        run.LastBaseline = final;
                        runs.Add(run);
                    }
                }
                cursor = last + 1;
            }
        }
        return ReconcileProjectedRuns(runs);
    }


    /// <summary>Whether the baseline at <paramref name="index"/> is the short last line of the full-width paragraph
    /// above it rather than the first line of a column: one side of the gutter only, the line spacing of that
    /// paragraph below it, at its left edge and in its size.</summary>
    private static bool EndsTheLineAbove(IReadOnlyList<List<PdfTextRegion>> baselines, IReadOnlyList<double> tops, int[][] sides, int index)
    {
        if (index == 0 || !sides[index - 1].Contains(2) || sides[index].Distinct().Count() != 1) return false;
        var above = baselines[index - 1];
        var line = baselines[index];
        var size = line.Max(fragment => fragment.FontSize ?? fragment.BoundingBox.Height);
        var aboveSize = above.Max(fragment => fragment.FontSize ?? fragment.BoundingBox.Height);
        return Math.Abs(size - aboveSize) <= 0.1 * Math.Max(size, aboveSize) &&
            tops[index - 1] - tops[index] <= 1.9 * size &&
            Math.Abs(line.Min(fragment => fragment.BoundingBox.X) - above.Min(fragment => fragment.BoundingBox.X)) <= 3;
    }

    /// <summary>The baselines [first, last] cut where a column leaves a gap far beyond its own line pitch - a
    /// page-bottom footer, a figure - unless a table fills the gap, in which case the column goes on below it.</summary>
    private static List<(int First, int Last)> SplitAtBlockEnds(IReadOnlyList<List<PdfTextRegion>> baselines, IReadOnlyList<double> tops,
        int[][] sides, IReadOnlyList<PdfTextRegion> tableFragments, int first, int last)
    {
        var members = new List<int>[2] { [], [] };
        var gaps = new List<double>();
        for (var side = 0; side < 2; side++)
        {
            for (var index = first; index <= last; index++) if (sides[index].Contains(side)) members[side].Add(index);
            for (var at = 1; at < members[side].Count; at++) gaps.Add(tops[members[side][at - 1]] - tops[members[side][at]]);
        }
        var pieces = new List<(int, int)>();
        if (gaps.Count < 3) { pieces.Add((first, last)); return pieces; }
        gaps.Sort();
        var pitch = gaps[gaps.Count / 2];
        var cuts = new SortedSet<int>();
        for (var side = 0; side < 2; side++)
        {
            var extentLeft = members[side].Count == 0 ? 0 : members[side].Min(index => baselines[index].Where((_, at) => sides[index][at] == side).Min(f => f.BoundingBox.X));
            var extentRight = members[side].Count == 0 ? 0 : members[side].Max(index => baselines[index].Where((_, at) => sides[index][at] == side).Max(f => f.BoundingBox.X + f.BoundingBox.Width));
            for (var at = 1; at < members[side].Count; at++)
            {
                var above = tops[members[side][at - 1]];
                var below = tops[members[side][at]];
                if (above - below <= pitch * BlockEndPitchFactor) continue;
                var filled = tableFragments.Any(table => table.BoundingBox.Y < above && table.BoundingBox.Y > below &&
                    table.BoundingBox.X + table.BoundingBox.Width / 2 >= extentLeft && table.BoundingBox.X + table.BoundingBox.Width / 2 <= extentRight);
                if (!filled) cuts.Add(members[side][at]);
            }
        }
        var from = first;
        foreach (var cut in cuts)
        {
            if (cut > from) pieces.Add((from, cut - 1));
            from = cut;
        }
        pieces.Add((from, last));
        return pieces;
    }

    /// <summary>The gutter of the baselines [first, last] when both sides of it read as columns of running text:
    /// enough lines each, lines of a column's width, a common left edge, and the two sides overlapping in height.
    /// Label-and-value rows and short captions beside text fail one of these and keep the order they have.</summary>
    private static GutterRun? ReadsAsColumns(IReadOnlyList<List<PdfTextRegion>> baselines, IReadOnlyList<double> tops, int[][] sides,
        int first, int last, double em)
    {
        var columns = new List<ProjectedLine>[2] { [], [] };
        for (var index = first; index <= last; index++)
            for (var side = 0; side < 2; side++)
            {
                var members = baselines[index].Where((_, at) => sides[index][at] == side).ToArray();
                if (members.Length == 0) continue;
                columns[side].Add(new ProjectedLine(tops[index], members.Min(f => f.BoundingBox.X), members.Max(f => f.BoundingBox.X + f.BoundingBox.Width)));
            }
        if (columns[0].Count < MinProjectedColumnLines || columns[1].Count < MinProjectedColumnLines) return null;
        foreach (var lines in columns)
        {
            var widths = lines.Select(line => line.Right - line.Left).Order().ToArray();
            var widest = widths[^1];
            if (widths[widths.Length / 2] < 8 * em) return null;
            // Text wrapped to a column fills most of its lines; labels and captions do not.
            if (widths.Count(width => width >= widest * 0.8) < lines.Count * 0.4) return null;
            var edges = lines.Select(line => line.Left).Order().ToArray();
            var edge = edges[edges.Length / 2];
            if (lines.Count(line => Math.Abs(line.Left - edge) <= 3) < lines.Count * 0.6) return null;
        }
        var overlap = Math.Min(columns[0].Max(line => line.Top), columns[1].Max(line => line.Top)) -
            Math.Max(columns[0].Min(line => line.Top), columns[1].Min(line => line.Top));
        var shorter = Math.Min(columns[0].Max(line => line.Top) - columns[0].Min(line => line.Top),
            columns[1].Max(line => line.Top) - columns[1].Min(line => line.Top));
        if (shorter <= 0 || overlap < shorter * 0.5) return null;
        return new GutterRun
        {
            Start = columns[0].Max(line => line.Right), End = columns[1].Min(line => line.Left),
            TrueMatchCount = Math.Min(columns[0].Count, columns[1].Count),
        };
    }

    /// <summary>Gutters of one block (the two of three columns) take the block's common baselines; a gutter whose
    /// block only partly overlaps another's is dropped, as the columns there are not told apart.</summary>
    private static List<GutterRun> ReconcileProjectedRuns(List<GutterRun> runs)
    {
        var kept = new List<GutterRun>();
        foreach (var run in runs.OrderBy(run => run.FirstBaseline).ThenBy(run => run.Start))
        {
            var clash = kept.Where(other => other.FirstBaseline <= run.LastBaseline && run.FirstBaseline <= other.LastBaseline).ToArray();
            if (clash.Length == 0) { kept.Add(run); continue; }
            var same = clash.All(other => other.End <= run.Start + 0.01 || run.End <= other.Start + 0.01) &&
                clash.All(other => other.FirstBaseline == run.FirstBaseline && other.LastBaseline == run.LastBaseline);
            if (same) kept.Add(run);
            else foreach (var other in clash) kept.Remove(other);
        }
        return kept;
    }
}
