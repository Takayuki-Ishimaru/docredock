namespace DocRedock.Formats.Pdf;

/// <summary>
/// Dashed rules drawn one dash at a time: at least three short strokes on one level, each following
/// the last after a gap the dash allows, the gaps regular. Table inference reads such a run as one
/// rule, and text decoration detection as one line, so a dashed rule under a row of text is never
/// taken dash by dash for underlines while a dashed underline still is one.
/// </summary>
internal static class PdfDashRuns
{
    /// <summary>One straight stroke: its axis, its fixed coordinate, its extent along the axis, and
    /// whether it can be a dash at all (a line, or a thin bar; never the edge of a box).</summary>
    internal readonly record struct Stroke(bool Horizontal, double Fixed, double Minimum, double Maximum, bool CanBeDash);

    private const int MinimumDashes = 3;
    private const double MaxDashLength = 12;
    // Strokes on one level: fixed coordinates within this distance of the level's first stroke.
    private const double LevelTolerance = 0.6;

    /// <summary>The runs among <paramref name="strokes"/>, each as indexes into it in order along the
    /// run. Strokes longer than a dash, and gaps wider than twice the run's usual gap, are never joined.</summary>
    public static List<int[]> Find(IReadOnlyList<Stroke> strokes)
    {
        var runs = new List<int[]>();
        if (strokes.Count < MinimumDashes) return runs;
        foreach (var axis in Enumerable.Range(0, strokes.Count).GroupBy(index => strokes[index].Horizontal))
        {
            List<int>? level = null;
            foreach (var index in axis.OrderBy(index => strokes[index].Fixed).ThenBy(index => index))
            {
                if (level is not null && strokes[index].Fixed - strokes[level[0]].Fixed <= LevelTolerance) { level.Add(index); continue; }
                if (level is not null) AddRuns(level);
                level = [index];
            }
            if (level is not null) AddRuns(level);
        }
        return runs;

        void AddRuns(List<int> level)
        {
            if (level.Count < MinimumDashes) return;
            var ordered = level.OrderBy(index => strokes[index].Minimum).ThenBy(index => index).ToArray();
            for (var start = 0; start < ordered.Length;)
            {
                var end = start;
                while (end + 1 < ordered.Length && IsDash(strokes[ordered[end]]) && IsDash(strokes[ordered[end + 1]]) &&
                       strokes[ordered[end + 1]].Minimum - strokes[ordered[end]].Maximum is var gap && gap > 0.05 && gap <= MaxGap(strokes[ordered[end]]))
                    end++;
                var run = ordered[start..(end + 1)];
                if (run.Length >= MinimumDashes && RegularGaps(strokes, run)) runs.Add(run);
                start = end + 1;
            }
        }
    }

    /// <summary>The middle gap between consecutive dashes of a run.</summary>
    public static double MedianGap(IReadOnlyList<Stroke> strokes, int[] run) =>
        Gaps(strokes, run).OrderBy(gap => gap).ElementAt((run.Length - 1) / 2);

    private static bool IsDash(Stroke stroke) => stroke.CanBeDash && stroke.Maximum - stroke.Minimum <= MaxDashLength;

    private static double MaxGap(Stroke stroke) => Math.Max(4, 2.5 * (stroke.Maximum - stroke.Minimum));

    private static bool RegularGaps(IReadOnlyList<Stroke> strokes, int[] run)
    {
        var median = MedianGap(strokes, run);
        return Gaps(strokes, run).All(gap => gap <= median * 2 + 0.5);
    }

    private static IEnumerable<double> Gaps(IReadOnlyList<Stroke> strokes, int[] run) =>
        run.Zip(run.Skip(1), (left, right) => strokes[right].Minimum - strokes[left].Maximum);
}
