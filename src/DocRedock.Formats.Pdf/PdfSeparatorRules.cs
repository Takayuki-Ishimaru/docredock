using DocRedock.Core.Documents;

namespace DocRedock.Formats.Pdf;

/// <summary>
/// Lone straight rules of a page: the line under a running header or a title, the one above a footer, a
/// bar in the margin beside changed text. Such a line divides the page; it joins nothing, so it is no
/// connector of a diagram and gives no reason to compare the page with the original. It is taken out of
/// the visual graph after the text decorations (an underline or a strikethrough is the text's formatting
/// and is recognized first). A rule is one horizontal or vertical stroke, or a filled bar no thicker than
/// <see cref="MaxBarThickness"/>, or several of them in a row (a line broken into pieces), with no arrow
/// head. It is left in the graph when anything makes it part of a drawing: its end meets another line or
/// shape (a connector meets the boxes it joins, a grid's rule the rules across it), it stops within
/// <see cref="AttachReach"/> of the side of a box it points at, it lies in a grid of lines (a table, and
/// what is drawn on one), visual inference resolved it as an edge, or it runs through the middle of text
/// (a strikethrough not recognized as one: the text it may cancel asks for a look at the original). A line
/// that runs from one side of a box to the opposite side divides the box, and is a rule too.
/// </summary>
internal static class PdfSeparatorRules
{
    // How close a rule's end must come to another drawing to meet it.
    private const double Touch = 3;
    // How far short of the side of a box a connector may stop.
    private const double AttachReach = 8;
    // A bar no thicker than this, and at least BarRatio times as long, is a rule drawn as a filled rectangle.
    private const double MaxBarThickness = 2.5;
    private const double BarRatio = 8;
    // Shorter strokes are marks, not rules; they stay as they were.
    private const double MinLength = 6;
    // Pieces of one rule lie on one line within this distance and leave gaps of at most PieceGap.
    private const double Collinear = 1;
    private const double PieceGap = 6;
    // Where a strikethrough crosses text, in ems above its baseline.
    private const double StrikeFromEm = 0.05;
    private const double StrikeToEm = 0.6;
    // A rule within NearTextEm of the text above or below it that is shorter than MinTextRuleEm of that text belongs to it.
    private const double NearTextEm = 1;
    private const double MinTextRuleEm = 15;
    // A busy page is left as it is: the checks compare every rule with every drawing.
    private const int MaxPaths = 2_000;
    private const int MaxSegments = 3_000;

    private sealed record Segment(int Path, double X1, double Y1, double X2, double Y2)
    {
        public bool Horizontal => Math.Abs(Y2 - Y1) <= Math.Abs(X2 - X1) * .0175;
        public bool Vertical => Math.Abs(X2 - X1) <= Math.Abs(Y2 - Y1) * .0175;
    }

    private sealed record Rule(int Path, bool Horizontal, double Fixed, double Minimum, double Maximum);

    /// <summary>A lone rule: the paths it was drawn with, and where it lies.</summary>
    internal sealed record Found(IReadOnlyList<string> PathIds, PdfSeparatorRule Rule);

    /// <summary>The lone rules among the paths of <paramref name="graph"/>.</summary>
    public static IReadOnlyList<Found> Find(VisualGraph graph, IReadOnlyList<PdfTextRegion> fragments,
        CancellationToken cancellationToken = default)
    {
        var paths = graph.Paths ?? [];
        if (paths.Count == 0 || paths.Count > MaxPaths) return [];
        var shafts = PdfTableInference.FindArrowShaftMatches(paths)
            .SelectMany(match => new[] { match.ShaftPathId, match.MarkerPathId }).ToHashSet(StringComparer.Ordinal);
        var edgesByPoints = new Dictionary<IReadOnlyList<VisualPathPoint>, VisualEdge>(ReferenceEqualityComparer.Instance);
        foreach (var edge in graph.Edges ?? [])
            if (edge.Path is { } points) edgesByPoints.TryAdd(points, edge);
        var nodesBySource = (graph.SourceItems ?? []).Where(item => item.ProjectedNodeId is not null)
            .ToDictionary(item => item.Id, item => item.ProjectedNodeId!, StringComparer.Ordinal);
        var nodeLabels = (graph.Nodes ?? []).ToDictionary(node => node.Id, node => node.Label, StringComparer.Ordinal);

        var rules = new List<Rule>();
        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            if (shafts.Contains(path.Id) || RuleOf(index, path) is not { } rule) continue;
            // Inference that joined it to a box or gave it an arrow made it part of a drawing. A label alone does
            // not: the heading above a rule is near it the way a connector's label is.
            if (path.Points is { } points && edgesByPoints.TryGetValue(points, out var edge) &&
                (edge.SourceId is not null || edge.TargetId is not null ||
                 edge.EdgeDirection is { } direction && direction != VisualEdgeDirection.Undirected))
                continue;
            if (nodesBySource.TryGetValue(path.Id, out var nodeId) && !string.IsNullOrWhiteSpace(nodeLabels.GetValueOrDefault(nodeId)))
                continue;
            rules.Add(rule);
        }
        if (rules.Count == 0) return [];
        cancellationToken.ThrowIfCancellationRequested();

        var segments = paths.SelectMany((path, index) => SegmentsOf(index, path)).ToArray();
        if (segments.Length > MaxSegments) return [];
        var grids = GridAreas(segments);
        var boxes = (graph.Nodes ?? []).Where(node => node.Geometry is { Width: > 0, Height: > 0 }).Select(node => node.Geometry!).ToArray();
        var texts = fragments.Where(fragment => fragment.FontSize is > 0 && fragment.Text.Trim().Length > 0).ToArray();

        var result = new List<Found>();
        foreach (var run in Runs(rules))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var members = run.Select(rule => rule.Path).ToHashSet();
            var horizontal = run[0].Horizontal;
            var position = run.Average(rule => rule.Fixed);
            var (from, to) = (run.Min(rule => rule.Minimum), run.Max(rule => rule.Maximum));
            if (to - from < MinLength) continue;
            var (left, right, bottom, top) = horizontal ? (from, to, position, position) : (position, position, from, to);
            if (grids.Any(area => Intersects(area, left - Touch, right + Touch, bottom - Touch, top + Touch))) continue;
            if (CrossesText(texts, horizontal, position, from, to) || horizontal && MarksText(texts, position, from, to)) continue;
            var line = new Segment(-1, left, bottom, right, top);
            var met = segments.Where(segment => !members.Contains(segment.Path) && Meets(line, horizontal, segment))
                .Select(segment => segment.Path).ToHashSet();
            if (met.Count > 1 || met.Count == 1 && !Divides(paths[met.Single()], horizontal, position, from, to)) continue;
            if (boxes.Any(box => PointsAt(box, horizontal, position, from, to))) continue;
            result.Add(new Found(run.Select(rule => paths[rule.Path].Id).ToArray(), new PdfSeparatorRule(horizontal, position, from, to)));
        }
        return result;
    }

    /// <summary>A path as a rule: one stroke along an axis, or a thin filled bar.</summary>
    private static Rule? RuleOf(int index, VisualPath path)
    {
        if (path.Points is not { Count: >= 2 } points || path.Confidence is < .8) return null;
        var (left, right, bottom, top) = Bounds(points);
        var (width, height) = (right - left, top - bottom);
        if (path.IsStroked == true && path.IsFilled != true && points[0] != points[^1])
        {
            if (height <= Math.Max(.5, width * .0175) && width >= MinLength) return new Rule(index, true, (bottom + top) / 2, left, right);
            if (width <= Math.Max(.5, height * .0175) && height >= MinLength) return new Rule(index, false, (left + right) / 2, bottom, top);
            return null;
        }
        if (path.IsFilled == true && points.Count is 4 or 5 &&
            points.Take(4).All(point => (point.X == left || point.X == right) && (point.Y == bottom || point.Y == top)))
        {
            if (height <= MaxBarThickness && width >= Math.Max(MinLength, BarRatio * height)) return new Rule(index, true, (bottom + top) / 2, left, right);
            if (width <= MaxBarThickness && height >= Math.Max(MinLength, BarRatio * width)) return new Rule(index, false, (left + right) / 2, bottom, top);
        }
        return null;
    }

    /// <summary>The rules grouped into runs of pieces of one line.</summary>
    private static List<List<Rule>> Runs(List<Rule> rules)
    {
        var runs = new List<List<Rule>>();
        foreach (var group in rules.GroupBy(rule => rule.Horizontal))
        {
            var ordered = group.OrderBy(rule => rule.Fixed).ThenBy(rule => rule.Minimum).ToList();
            var used = new bool[ordered.Count];
            for (var start = 0; start < ordered.Count; start++)
            {
                if (used[start]) continue;
                used[start] = true;
                var run = new List<Rule> { ordered[start] };
                for (var grown = true; grown;)
                {
                    grown = false;
                    for (var other = 0; other < ordered.Count; other++)
                    {
                        if (used[other]) continue;
                        var candidate = ordered[other];
                        if (run.Any(rule => Math.Abs(rule.Fixed - candidate.Fixed) <= Collinear &&
                                candidate.Minimum <= rule.Maximum + PieceGap && candidate.Maximum >= rule.Minimum - PieceGap))
                        {
                            used[other] = true;
                            run.Add(candidate);
                            grown = true;
                        }
                    }
                }
                runs.Add(run);
            }
        }
        return runs;
    }

    /// <summary>Whether another drawing's segment meets the rule: crosses it, or comes within <see cref="Touch"/>
    /// of it, unless it runs beside it (the other line of a double rule, a box's side along it).</summary>
    private static bool Meets(Segment line, bool horizontal, Segment segment)
    {
        if (horizontal ? segment.Horizontal : segment.Vertical)
        {
            var offset = horizontal ? Math.Abs((segment.Y1 + segment.Y2) / 2 - line.Y1) : Math.Abs((segment.X1 + segment.X2) / 2 - line.X1);
            if (offset > Collinear) return false;
            var (from, to) = horizontal ? (line.X1, line.X2) : (line.Y1, line.Y2);
            var (otherFrom, otherTo) = horizontal
                ? (Math.Min(segment.X1, segment.X2), Math.Max(segment.X1, segment.X2))
                : (Math.Min(segment.Y1, segment.Y2), Math.Max(segment.Y1, segment.Y2));
            return otherFrom <= to + Touch && otherTo >= from - Touch;
        }
        return Distance(line, segment) <= Touch;
    }

    /// <summary>Whether a rule that meets only <paramref name="box"/> runs from one of its sides to the other.</summary>
    private static bool Divides(VisualPath box, bool horizontal, double position, double from, double to)
    {
        if (box.Points is not { Count: >= 4 } points || points[0] != points[^1] && box.IsFilled != true) return false;
        var (left, right, bottom, top) = Bounds(points);
        return horizontal
            ? position > bottom + Touch && position < top - Touch && Math.Abs(from - left) <= Touch && Math.Abs(to - right) <= Touch
            : position > left + Touch && position < right - Touch && Math.Abs(from - bottom) <= Touch && Math.Abs(to - top) <= Touch;
    }

    /// <summary>Whether an end of the rule stops short of the side of a box it points at, as a connector does.
    /// A box the rule lies inside holds it, and is not pointed at.</summary>
    private static bool PointsAt(Geometry box, bool horizontal, double position, double from, double to)
    {
        var (left, right, bottom, top) = (box.X, box.X + box.Width, box.Y, box.Y + box.Height);
        if (horizontal)
        {
            if (from >= left - Touch && to <= right + Touch && position >= bottom - Touch && position <= top + Touch) return false;
            if (position < bottom - Collinear || position > top + Collinear) return false;
            return to >= left - AttachReach && to <= right || from <= right + AttachReach && from >= left;
        }
        if (from >= bottom - Touch && to <= top + Touch && position >= left - Touch && position <= right + Touch) return false;
        if (position < left - Collinear || position > right + Collinear) return false;
        return to >= bottom - AttachReach && to <= top || from <= top + AttachReach && from >= bottom;
    }

    /// <summary>Whether the rule runs through the middle of text: a horizontal one over its letters at the
    /// height of a strikethrough, a vertical one across them.</summary>
    private static bool CrossesText(IReadOnlyList<PdfTextRegion> texts, bool horizontal, double position, double from, double to)
    {
        foreach (var text in texts)
        {
            var size = text.FontSize!.Value;
            var (left, baseline) = (text.BoundingBox.X, text.BoundingBox.Y);
            var right = left + Width(text);
            if (horizontal)
            {
                var em = (position - baseline) / size;
                if (em >= StrikeFromEm && em <= StrikeToEm && Math.Min(to, right) - Math.Max(from, left) > .3 * size) return true;
            }
            else if (position > left + .1 * size && position < right - .1 * size &&
                     Math.Min(to, baseline + .8 * size) - Math.Max(from, baseline - .2 * size) > .3 * size)
                return true;
        }
        return false;
    }

    /// <summary>Whether a horizontal rule belongs to the text right above or below it: no longer than
    /// <see cref="MinTextRuleEm"/> of that text, it is a bar over or under it (an overline, a fraction's bar, a line under
    /// a word that is not its underline), not a rule across the page, and it stays a drawing.</summary>
    private static bool MarksText(IReadOnlyList<PdfTextRegion> texts, double position, double from, double to)
    {
        foreach (var text in texts)
        {
            var size = text.FontSize!.Value;
            var em = (position - text.BoundingBox.Y) / size;
            if (em < -NearTextEm || em > 1 + NearTextEm || to - from >= MinTextRuleEm * size) continue;
            var (left, right) = (text.BoundingBox.X, text.BoundingBox.X + Width(text));
            if (Math.Min(to, right) - Math.Max(from, left) > .3 * size) return true;
        }
        return false;
    }

    /// <summary>The areas of grids of lines on the page: lines of two or more drawings that cross or meet one
    /// another, running both ways. A table and what is drawn on it lie in one; a single box is none.</summary>
    private static List<(double Left, double Right, double Bottom, double Top)> GridAreas(Segment[] segments)
    {
        var axis = segments.Where(segment => segment.Horizontal || segment.Vertical).ToArray();
        var parent = Enumerable.Range(0, axis.Length).ToArray();
        int Root(int index) { while (parent[index] != index) index = parent[index] = parent[parent[index]]; return index; }
        for (var a = 0; a < axis.Length; a++)
            for (var b = a + 1; b < axis.Length; b++)
                if (axis[a].Horizontal != axis[b].Horizontal && Distance(axis[a], axis[b]) <= 1.5)
                    parent[Root(a)] = Root(b);
        return axis.Select((segment, index) => (segment, root: Root(index))).GroupBy(item => item.root)
            .Where(group => group.Select(item => item.segment.Path).Distinct().Count() >= 2 &&
                group.Any(item => item.segment.Horizontal) && group.Any(item => item.segment.Vertical))
            .Select(group => (group.Min(item => Math.Min(item.segment.X1, item.segment.X2)), group.Max(item => Math.Max(item.segment.X1, item.segment.X2)),
                group.Min(item => Math.Min(item.segment.Y1, item.segment.Y2)), group.Max(item => Math.Max(item.segment.Y1, item.segment.Y2))))
            .ToList();
    }

    private static IEnumerable<Segment> SegmentsOf(int index, VisualPath path)
    {
        if (path.Points is not { Count: >= 1 } points) yield break;
        if (points.Count == 1) { yield return new Segment(index, points[0].X, points[0].Y, points[0].X, points[0].Y); yield break; }
        for (var i = 1; i < points.Count; i++)
            yield return new Segment(index, points[i - 1].X, points[i - 1].Y, points[i].X, points[i].Y);
        // A filled shape is closed whether or not its last point repeats the first.
        if (path.IsFilled == true && points[0] != points[^1])
            yield return new Segment(index, points[^1].X, points[^1].Y, points[0].X, points[0].Y);
    }

    private static bool Intersects((double Left, double Right, double Bottom, double Top) area, double left, double right, double bottom, double top) =>
        left <= area.Right && right >= area.Left && bottom <= area.Top && top >= area.Bottom;

    private static double Distance(Segment a, Segment b)
    {
        if (Cross(a, b)) return 0;
        return Math.Min(Math.Min(PointDistance(a.X1, a.Y1, b), PointDistance(a.X2, a.Y2, b)),
            Math.Min(PointDistance(b.X1, b.Y1, a), PointDistance(b.X2, b.Y2, a)));
    }

    private static bool Cross(Segment a, Segment b)
    {
        static double Side(double ax, double ay, double bx, double by, double cx, double cy) => (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        var d1 = Side(b.X1, b.Y1, b.X2, b.Y2, a.X1, a.Y1);
        var d2 = Side(b.X1, b.Y1, b.X2, b.Y2, a.X2, a.Y2);
        var d3 = Side(a.X1, a.Y1, a.X2, a.Y2, b.X1, b.Y1);
        var d4 = Side(a.X1, a.Y1, a.X2, a.Y2, b.X2, b.Y2);
        return (d1 > 0 && d2 < 0 || d1 < 0 && d2 > 0) && (d3 > 0 && d4 < 0 || d3 < 0 && d4 > 0);
    }

    private static double PointDistance(double x, double y, Segment segment)
    {
        var (dx, dy) = (segment.X2 - segment.X1, segment.Y2 - segment.Y1);
        var length = dx * dx + dy * dy;
        var t = length <= 0 ? 0 : Math.Clamp(((x - segment.X1) * dx + (y - segment.Y1) * dy) / length, 0, 1);
        return Math.Sqrt(Math.Pow(x - (segment.X1 + t * dx), 2) + Math.Pow(y - (segment.Y1 + t * dy), 2));
    }

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

    // A show's width: measured with its font's glyph widths when known, else full-width characters an em
    // and the others a little over half an em.
    private static double Width(PdfTextRegion text) => text.MeasuredWidth
        ? text.BoundingBox.Width
        : text.Text.Sum(character => character > '⹿' || char.IsSurrogate(character) ? 1.0 : 0.55) * text.FontSize!.Value;
}

/// <summary>A lone rule of a page (see <see cref="PdfSeparatorRules"/>): whether it runs across the page or down it,
/// where it lies (its y, or its x), and from where to where it runs, in PDF user space.</summary>
public sealed record PdfSeparatorRule(bool Horizontal, double Position, double From, double To);
