using System.Text;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.Pdf;

// Columns the drawing makes. Reading order is worked out from where text sits: lines by baseline, and
// columns from a gutter of white space that persists down the page (see ApplyColumnLayout). A page that
// frames its text defeats both. Two boxes side by side, or one frame that a vertical rule divides,
// hold short wrapped lines on baselines that match, so the lines of one box are interleaved with those
// of the other, and a line that wraps reads as a paragraph of its own or is glued to the other box's
// line on its baseline. The drawing says where the columns are: the rectangles and the long vertical
// rules of the page's vector graphics. Text in a box is read with that box, top to bottom, one box
// after the other from left to right, at the place where the first line of the boxes would have been
// read; the page goes on below them. Inside such a box a line that wraps is joined to the line it
// continues, when the break was forced: the first word of the next line would not have fitted after
// the line within the column. A page without boxes that hold text side by side keeps the reading order
// it had, and so does what only looks like such a page: a table or a form (rules run across it), a chart,
// the boxes of a diagram that is projected as one (edges join them), a listing in a fixed-pitch font (it
// keeps its lines), and rows of labels and values in one frame (the lines pair up across it, and none of
// them wraps).
public static partial class PdfTextExtractor
{
    // The thickest a rectangle may be to count as a rule drawn with one, and how far apart two edges of
    // the drawing may lie and still be one edge (the tolerance table inference reads grids with).
    private const double BoxRuleThickness = 1.5;
    private const double BoxEdgeTolerance = 1.5;
    // A box smaller than this holds no more than a line of text; a swatch, a checkbox or a bullet is
    // none. A frame that fills most of the page is the page's own border, not a box of text.
    private const double MinBoxWidth = 24;
    private const double MinBoxHeight = 12;
    private const double MinRuleLength = 5;
    private const double PageFrameWidth = 0.85;
    private const double PageFrameHeight = 0.8;
    // A vertical rule divides a frame when it runs this much of the way from its top to its bottom.
    private const double DividerCoverage = 0.9;
    // Beyond these a page is a drawing, or a ruled form, and not a layout of boxes.
    private const int MaxBoxPaths = 4_000;
    private const int MaxBoxRules = 600;
    private const int MaxBoxes = 400;
    // Cells one above the other with the same edges and no more than this between them are the rows of a
    // grid, however it is drawn: a table whose cells are rectangles. A box is a grid also when a rule
    // crosses at least this share of its width inside it.
    private const double StackedGap = 6;
    private const double RuledShare = 0.5;
    // A box with a curve or a slanted line this long in it holds a drawing - a chart - and not a column of text.
    private const double DrawingSize = 24;

    // Boxes lie side by side when they share at least this much of the height of the shorter one and
    // are no further apart than this share of the page's width.
    private const double SideBySideOverlap = 0.5;
    private const double MaxColumnGap = 0.2;
    private const double MinColumnGapPoints = 24;

    // A channel of white space inside one box that no line crosses divides it into columns when it is at
    // least this many ems wide, text lies on both sides of it on at least two lines, and each side is a
    // block of text on at least two lines and this many ems wide. A narrower block is a label.
    private const double ChannelEm = 2.5;
    private const double MinChannel = 12;
    private const double BlockEm = 8;
    private const int ChannelBaselines = 2;
    private const int MaxChannels = 6;

    // Text set in a fixed-pitch font - code, a listing, typed text - keeps its lines: its columns are tabs and the end of
    // a line is the end of a statement, so no channel of white space cuts it into blocks and no line is joined to
    // the next. It is fixed pitch when most of its characters are ASCII and the narrow ones (i, l, t, a full stop) are
    // about as wide as the broad ones (a, e, o, n, m): a proportional font sets them at half the width or less.
    private const double FixedPitchRatio = 0.9;
    private const string NarrowCharacters = "iljtfr.,:;!|'()[]";
    private const string BroadCharacters = "aeonudhpqbgmwMWOQ@";

    // Lines of one size are lines whose sizes differ by no more than this; a continuation is indented
    // to the text of the bullet it continues, or starts at the paragraph's edge, within half an em.
    private const double SameSize = 0.1;
    private const double ContinuationIndentEm = 0.5;
    // A line is wrapped when the break after it was forced: the first word of the next line and a space (or
    // its first character, with the marks that cannot start a line) would not have fitted after it within the
    // column. Where a column ends is known when an edge is drawn there: the text of a box ends at its right
    // edge less a padding as wide as the one on the left (up to MaxPadding), or sooner by up to RightEdgeSlack.
    // Where none is - a block that a channel of white space cuts out of a frame, but the last - it is the end
    // of the longest line, the least it can be. That line is followed by a forced break whatever follows it,
    // so there the last line of a paragraph has to fall short of the longest by half an em at least, the
    // paragraph is at least MinRaggedLines long unless it ends a sentence, and it is prose - it holds some
    // punctuation or goes on in lower case: a list of names or labels of one length is none. The lines
    // under a bullet or a number are the rest of its item, and need no more than to fall short. ForcedSlack
    // is for the rounding of widths. A space is about SpaceEm wide where the line holds none to measure.
    private const double MaxPadding = 12;
    private const double RightEdgeSlack = 14;
    private const double ForcedSlack = 0.5;
    private const double PartialLastLineEm = 0.5;
    private const int MinRaggedLines = 3;
    private const double SpaceEm = 0.27;
    // East Asian marks that cannot start a line, and those that cannot end one.
    private const string NoLineStart = "、。，．）〕］｝〉》」』】！？：；・…‥";
    private const string NoLineEnd = "（〔［｛〈《「『【";
    // A line that ends with one of these closes its sentence or its item: a capital letter after it starts the next.
    private const string ClosingMarks = ".!?:;…)]}»”’\"'。．！？：；）］｝」』】〕》〉";
    private const string SentenceMarks = ".!?。．！？";
    private const string ProseMarks = ".!?,;。．！？、，；";
    // The gap between a line and the one it continues is at most the column's own line spacing (the
    // least gap between two lines of one size there) with a little to spare, and never more than this
    // many ems.
    private const double LeadingSlack = 1.15;
    private const double MaxLeadingEm = 1.8;

    /// <summary>A box the page draws, in page units: a frame, a cell a rule makes of one, or a filled
    /// rectangle. <paramref name="Drawn"/> marks a box that is not a column of text: rules cross it from
    /// side to side or it lies in a grid (a form, a table), or a curve is drawn in it (a chart).</summary>
    private readonly record struct BoxCell(double Left, double Bottom, double Right, double Top, bool Drawn = false)
    {
        public double Width => Right - Left;
        public double Height => Top - Bottom;
        public double Area => Width * Height;
        public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Bottom && y <= Top;

        public bool Matches(BoxCell other) => Math.Abs(Left - other.Left) <= BoxEdgeTolerance &&
            Math.Abs(Right - other.Right) <= BoxEdgeTolerance && Math.Abs(Bottom - other.Bottom) <= BoxEdgeTolerance &&
            Math.Abs(Top - other.Top) <= BoxEdgeTolerance;
    }

    /// <summary>A straight rule along an axis: its fixed coordinate and the stretch it covers.</summary>
    private readonly record struct BoxRule(bool Horizontal, double Fixed, double Minimum, double Maximum);

    /// <summary>What reading order takes from a page's drawing: its boxes, smallest first; the width of
    /// the page (0 when unknown); and the text fragments that stay where they are because the page's
    /// diagram has taken them as the labels of its nodes and is projected as a diagram. (The inference
    /// gives a label to any box with a line of text in the middle of it; that makes it no node unless
    /// edges join it to others.)</summary>
    private sealed record BoxLayout(IReadOnlyList<BoxCell> Cells, double PageWidth, IReadOnlySet<int> Fixed);

    /// <summary>A column of boxed text: the area it lies in and the fragments in it (positions in the
    /// geometric order), then the lines they make.</summary>
    private sealed class BoxColumn(BoxCell cell, List<int> indices)
    {
        public BoxCell Cell { get; } = cell;
        public List<int> Indices { get; } = indices;
        public List<PdfTextRegion> Lines { get; set; } = [];
        public bool FixedPitch { get; set; }
        /// <summary>Where the text of the column ends at the most when its right edge is drawn; null when it is not
        /// (a block a channel of white space cuts out of a frame).</summary>
        public double? Limit { get; set; }
    }

    /// <summary>The text of a page's boxed columns, arranged: the fragments left to the ordinary flow,
    /// among them one stand-in for each group of boxes (its leader) that is replaced, once the flow has
    /// been put in order, by the group's lines.</summary>
    private sealed class BoxArrangement(PdfTextRegion[] remaining, IReadOnlySet<int> leaders,
        IReadOnlyDictionary<int, List<PdfTextRegion>> blocks, int columns)
    {
        public PdfTextRegion[] Remaining { get; } = remaining;
        public IReadOnlySet<int> Leaders { get; } = leaders;
        public int Columns { get; } = columns;

        public List<PdfTextRegion> Expand(List<PdfTextRegion> lines)
        {
            var expanded = new List<PdfTextRegion>(lines.Count);
            foreach (var line in lines)
            {
                if (line.SourceTextIds is [var first] && blocks.TryGetValue(first, out var block)) expanded.AddRange(block);
                else expanded.Add(line);
            }
            return expanded;
        }
    }

    /// <summary>The lines of a page in reading order: the text of the boxes that are columns read a box at a time (see
    /// <see cref="ArrangeBoxedColumns"/>) and the rest as the page-level column layout and the baselines give it. Each
    /// group of boxes stands in the flow as its first fragment, which no other fragment is merged into, and is put
    /// in its place once the flow has its order. Every fragment is in exactly one of the lines; should the
    /// arrangement leave one out or put one in twice, the page is read as if it had no boxes.</summary>
    private static List<PdfTextRegion> ReadLines(PdfTextRegion[] geometric, IReadOnlySet<int>? tableSourceIds, BoxLayout? boxes,
        out int columnCount)
    {
        if (boxes is not null && ArrangeBoxedColumns(geometric, tableSourceIds, boxes) is { } arranged)
        {
            var ordered = ApplyColumnLayout(arranged.Remaining, tableSourceIds, out columnCount, arranged.Leaders);
            var lines = arranged.Expand(MergeAdjacentIntoLines(ordered, tableSourceIds, arranged.Leaders));
            if (HoldsEveryFragmentOnce(geometric, lines))
            {
                columnCount = Math.Max(columnCount, arranged.Columns);
                return lines;
            }
        }
        return MergeAdjacentIntoLines(ApplyColumnLayout(geometric, tableSourceIds, out columnCount), tableSourceIds);
    }

    private static bool HoldsEveryFragmentOnce(IEnumerable<PdfTextRegion> fragments, IEnumerable<PdfTextRegion> lines) =>
        fragments.SelectMany(fragment => fragment.SourceTextIds).Order().SequenceEqual(lines.SelectMany(line => line.SourceTextIds).Order());

    /// <summary>The boxes of a page: the filled and the stroked rectangles, and the frames four rules
    /// make, each cut into cells by the vertical rules that run through it. A grid with more than one row
    /// is a table - its own handling decides what it is - and so is whatever table inference took.
    /// Null when the page has no box.</summary>
    private static BoxLayout? FindBoxLayout(VisualGraph? graph, IReadOnlyList<PdfTable> tables, Geometry? page,
        IReadOnlyDictionary<int, string>? consumedLabels)
    {
        if (graph?.Paths is not { Count: > 0 and <= MaxBoxPaths } paths) return null;
        var tablePaths = tables.SelectMany(table => table.SourcePathIds).ToHashSet(StringComparer.Ordinal);
        var boxes = new List<BoxCell>();
        var rules = new List<BoxRule>();
        var shapes = new List<BoxCell>();
        foreach (var path in paths)
        {
            if (path.Points is not { Count: >= 2 } points || tablePaths.Contains(path.Id) ||
                path.IsFilled != true && path.IsStroked != true) continue;
            if (TryBox(points, out var box))
            {
                if (box.Width > BoxRuleThickness && box.Height > BoxRuleThickness)
                {
                    boxes.Add(box);
                    if (path.IsStroked == true)
                    {
                        rules.Add(new BoxRule(true, box.Bottom, box.Left, box.Right));
                        rules.Add(new BoxRule(true, box.Top, box.Left, box.Right));
                        rules.Add(new BoxRule(false, box.Left, box.Bottom, box.Top));
                        rules.Add(new BoxRule(false, box.Right, box.Bottom, box.Top));
                    }
                }
                // A rule is often drawn as a thin rectangle.
                else if (box.Height <= BoxRuleThickness && box.Width >= MinRuleLength)
                    rules.Add(new BoxRule(true, (box.Bottom + box.Top) / 2, box.Left, box.Right));
                else if (box.Width <= BoxRuleThickness && box.Height >= MinRuleLength)
                    rules.Add(new BoxRule(false, (box.Left + box.Right) / 2, box.Bottom, box.Top));
            }
            else if (points.Count == 2 && path.IsStroked == true && TryRule(points[0], points[1]) is { } rule)
                rules.Add(rule);
            // Any other painted path - a curve, a slanted line, a polygon - that is not small.
            else if (path.Geometry is { } shape && Math.Max(shape.Width, shape.Height) >= DrawingSize &&
                double.IsFinite(shape.X) && double.IsFinite(shape.Y))
                shapes.Add(new BoxCell(shape.X, shape.Y, shape.X + shape.Width, shape.Y + shape.Height));
        }
        if (rules.Count > MaxBoxRules) return null;
        var grids = new List<BoxCell>();
        if (rules.Count >= 4) boxes.AddRange(FramesOf(rules, grids));

        var pageWidth = page is { Width: > 0, Height: > 0 } ? page.Width : 0;
        var cells = new List<BoxCell>();
        foreach (var box in boxes)
        {
            if (box.Width < MinBoxWidth || box.Height < MinBoxHeight ||
                page is { Width: > 0, Height: > 0 } && box.Width >= PageFrameWidth * page.Width && box.Height >= PageFrameHeight * page.Height ||
                cells.Any(other => other.Matches(box))) continue;
            cells.Add(box);
            if (cells.Count > MaxBoxes) return null;
        }
        if (cells.Count == 0) return null;
        var horizontals = rules.Where(rule => rule.Horizontal).ToArray();
        return new BoxLayout(cells.Select(cell => cell with
        {
            Drawn = IsRuledInside(cell, horizontals) || Holds(grids, cell) || Holds(shapes, cell),
        }).OrderBy(cell => cell.Area).ToArray(), pageWidth,
            consumedLabels is { Count: > 0 } ? consumedLabels.Keys.ToHashSet() : new HashSet<int>());
    }

    /// <summary>Whether the middle of one of <paramref name="areas"/> lies in <paramref name="cell"/>, or the
    /// middle of the cell in one of them.</summary>
    private static bool Holds(IReadOnlyList<BoxCell> areas, BoxCell cell)
    {
        var (x, y) = ((cell.Left + cell.Right) / 2, (cell.Bottom + cell.Top) / 2);
        return areas.Any(area => area.Contains(x, y) || cell.Contains((area.Left + area.Right) / 2, (area.Bottom + area.Top) / 2));
    }

    /// <summary>Whether a horizontal rule runs across the inside of <paramref name="cell"/> for at least
    /// <see cref="RuledShare"/> of its width.</summary>
    private static bool IsRuledInside(BoxCell cell, IReadOnlyList<BoxRule> horizontals) =>
        horizontals.Any(rule => rule.Fixed > cell.Bottom + BoxEdgeTolerance && rule.Fixed < cell.Top - BoxEdgeTolerance &&
            Math.Min(rule.Maximum, cell.Right) - Math.Max(rule.Minimum, cell.Left) >= RuledShare * cell.Width);

    /// <summary>Whether a painted path is an axis-aligned rectangle: four corners (the first repeated
    /// when it is closed), every side along one axis.</summary>
    private static bool TryBox(IReadOnlyList<VisualPathPoint> points, out BoxCell box)
    {
        box = default;
        if (points.Count is not (4 or 5) || points.Count == 5 && !(Near(points[0].X, points[4].X) && Near(points[0].Y, points[4].Y)))
            return false;
        double left = double.PositiveInfinity, bottom = double.PositiveInfinity, right = double.NegativeInfinity, top = double.NegativeInfinity;
        for (var corner = 0; corner < 4; corner++)
        {
            if (!double.IsFinite(points[corner].X) || !double.IsFinite(points[corner].Y)) return false;
            left = Math.Min(left, points[corner].X); right = Math.Max(right, points[corner].X);
            bottom = Math.Min(bottom, points[corner].Y); top = Math.Max(top, points[corner].Y);
        }
        for (var corner = 0; corner < 4; corner++)
        {
            VisualPathPoint point = points[corner], next = points[(corner + 1) % 4];
            if (!(Near(point.X, left) || Near(point.X, right)) || !(Near(point.Y, bottom) || Near(point.Y, top))) return false;
            if (Near(point.X, next.X) == Near(point.Y, next.Y)) return false;
        }
        box = new BoxCell(left, bottom, right, top);
        return true;
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) <= 0.01;

    private static BoxRule? TryRule(VisualPathPoint a, VisualPathPoint b)
    {
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(length) || length < MinRuleLength) return null;
        if (Math.Abs(dy) <= length * .0175) return new BoxRule(true, (a.Y + b.Y) / 2, Math.Min(a.X, b.X), Math.Max(a.X, b.X));
        if (Math.Abs(dx) <= length * .0175) return new BoxRule(false, (a.X + b.X) / 2, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y));
        return null;
    }

    /// <summary>The frames that rules make: groups of rules that cross form a lattice, and a lattice of
    /// one row - a top and a bottom rule, a vertical rule on each side and any number between - is a
    /// frame and the cells the rules in between cut it into. A lattice of more rows is a grid, whose
    /// area is added to <paramref name="grids"/>: what is written in it is read as the table it is.</summary>
    private static IEnumerable<BoxCell> FramesOf(IReadOnlyList<BoxRule> rules, List<BoxCell> grids)
    {
        var parent = Enumerable.Range(0, rules.Count).ToArray();
        int Root(int index)
        {
            while (parent[index] != index) index = parent[index] = parent[parent[index]];
            return index;
        }
        for (var h = 0; h < rules.Count; h++)
        {
            if (!rules[h].Horizontal) continue;
            for (var v = 0; v < rules.Count; v++)
            {
                if (rules[v].Horizontal) continue;
                var (horizontal, vertical) = (rules[h], rules[v]);
                if (vertical.Fixed >= horizontal.Minimum - BoxEdgeTolerance && vertical.Fixed <= horizontal.Maximum + BoxEdgeTolerance &&
                    horizontal.Fixed >= vertical.Minimum - BoxEdgeTolerance && horizontal.Fixed <= vertical.Maximum + BoxEdgeTolerance)
                    parent[Root(h)] = Root(v);
            }
        }
        foreach (var group in Enumerable.Range(0, rules.Count).GroupBy(Root))
        {
            var members = group.Select(index => rules[index]).ToArray();
            var horizontals = members.Where(rule => rule.Horizontal).ToArray();
            var verticals = members.Where(rule => !rule.Horizontal).ToArray();
            if (horizontals.Length < 2 || verticals.Length < 2) continue;
            var ys = Levels(horizontals);
            var xs = Levels(verticals);
            if (ys.Count >= 3 && xs.Count >= 2) grids.Add(new BoxCell(xs[0], ys[0], xs[^1], ys[^1]));
            if (ys.Count != 2 || xs.Count < 2) continue;
            double bottom = ys[0], top = ys[1], left = xs[0], right = xs[^1];
            if (!Covers(horizontals, bottom, left, right, 1) || !Covers(horizontals, top, left, right, 1) ||
                !Covers(verticals, left, bottom, top, 1) || !Covers(verticals, right, bottom, top, 1)) continue;
            yield return new BoxCell(left, bottom, right, top);
            var edges = new List<double> { left };
            edges.AddRange(xs.Skip(1).Take(xs.Count - 2).Where(x => Covers(verticals, x, bottom, top, DividerCoverage)));
            edges.Add(right);
            if (edges.Count <= 2) continue;
            for (var cell = 0; cell + 1 < edges.Count; cell++)
                yield return new BoxCell(edges[cell], bottom, edges[cell + 1], top);
        }
    }

    /// <summary>The distinct coordinates rules lie at, rules within the edge tolerance being one.</summary>
    private static List<double> Levels(IEnumerable<BoxRule> rules)
    {
        var levels = new List<double>();
        foreach (var coordinate in rules.Select(rule => rule.Fixed).Order())
            if (levels.Count == 0 || coordinate - levels[^1] > BoxEdgeTolerance) levels.Add(coordinate);
        return levels;
    }

    /// <summary>Whether the rules at <paramref name="level"/> cover at least <paramref name="share"/> of
    /// the stretch from <paramref name="from"/> to <paramref name="to"/>; the ends of a rule may fall
    /// short of it by the edge tolerance.</summary>
    private static bool Covers(IEnumerable<BoxRule> rules, double level, double from, double to, double share)
    {
        var length = to - from;
        if (length <= 0) return false;
        var covered = 0d;
        var reach = from;
        foreach (var rule in rules.Where(rule => Math.Abs(rule.Fixed - level) <= BoxEdgeTolerance).OrderBy(rule => rule.Minimum))
        {
            var start = Math.Max(rule.Minimum, reach);
            var end = Math.Min(rule.Maximum, to);
            if (end > start) covered += end - start;
            reach = Math.Max(reach, rule.Maximum);
        }
        return covered >= share * length - 2 * BoxEdgeTolerance;
    }

    /// <summary>A fragment can be a box's text when its size is known (it runs across the page) and it is
    /// not a table's.</summary>
    private static bool CanBeBoxed(PdfTextRegion fragment, IReadOnlySet<int>? tableSourceIds) =>
        fragment.FontSize is > 0 && double.IsFinite(fragment.FontSize.Value) && fragment.Text.Length > 0 &&
        double.IsFinite(fragment.BoundingBox.X) && double.IsFinite(fragment.BoundingBox.Y) &&
        !PdfTextAccounting.IsTableOwned(fragment, tableSourceIds);

    /// <summary>Where a fragment is for deciding which box holds it: the middle of its glyphs, or - when
    /// its width is only estimated - just after its start.</summary>
    private static (double X, double Y) BoxAnchor(PdfTextRegion fragment) =>
        (fragment.MeasuredWidth ? fragment.BoundingBox.X + fragment.BoundingBox.Width / 2 : fragment.BoundingBox.X + 1,
         fragment.BoundingBox.Y + 0.3 * fragment.FontSize!.Value);

    /// <summary>Puts the text of boxes in order, a box after the other and each from its top. Boxes
    /// that lie side by side are the columns of one group, and so are the cells one frame is divided
    /// into by its vertical rules, and the blocks a channel of white space cuts one box into; a group is
    /// read where its first line stands in the page, and everything else keeps its place. A group in
    /// which no column has two lines is left as it is: the order of single lines does not change, and
    /// neither do the lines. So are the blocks that white space divides a frame into when they are rows
    /// of labels and values (see <see cref="LooksLikeRows"/>). Null when no group is arranged.</summary>
    private static BoxArrangement? ArrangeBoxedColumns(PdfTextRegion[] geometric,
        IReadOnlySet<int>? tableSourceIds, BoxLayout layout)
    {
        var cells = layout.Cells;
        // The smallest box each fragment lies in. A box that holds the label of a node of a diagram is that
        // node and not a column of text, and the text of a grid or a drawing is left to read as it always was.
        var members = new Dictionary<int, List<int>>();
        var nodes = new HashSet<int>();
        for (var index = 0; index < geometric.Length; index++)
        {
            if (!CanBeBoxed(geometric[index], tableSourceIds)) continue;
            var (x, y) = BoxAnchor(geometric[index]);
            for (var cell = 0; cell < cells.Count; cell++)
            {
                if (!cells[cell].Contains(x, y)) continue;
                if (geometric[index].SourceTextIds.Any(layout.Fixed.Contains)) nodes.Add(cell);
                else if (!cells[cell].Drawn)
                {
                    if (!members.TryGetValue(cell, out var list)) members[cell] = list = [];
                    list.Add(index);
                }
                break;
            }
        }
        foreach (var node in nodes) members.Remove(node);
        if (members.Count == 0) return null;

        var pageWidth = layout.PageWidth > 0 ? layout.PageWidth
            : geometric.Max(fragment => fragment.BoundingBox.X + fragment.BoundingBox.Width) - geometric.Min(fragment => fragment.BoundingBox.X);
        var withText = members.Keys.Order().ToArray();
        var parent = Enumerable.Range(0, withText.Length).ToArray();
        int Root(int index)
        {
            while (parent[index] != index) index = parent[index] = parent[parent[index]];
            return index;
        }
        for (var a = 0; a < withText.Length; a++)
            for (var b = a + 1; b < withText.Length; b++)
                if (SideBySide(cells[withText[a]], cells[withText[b]], pageWidth)) parent[Root(a)] = Root(b);

        var claimed = new HashSet<int>();
        var leaders = new HashSet<int>();
        var blocks = new Dictionary<int, List<PdfTextRegion>>();
        var columnCount = 1;
        foreach (var group in Enumerable.Range(0, withText.Length).GroupBy(Root).OrderBy(group => group.Min()))
        {
            // Boxes with another above or below them that has the same edges are the rows of a table.
            if (group.Any(position => HasStackedNeighbour(cells, withText[position]))) continue;
            var columns = group.Select(position => new BoxColumn(cells[withText[position]], members[withText[position]]))
                .OrderBy(column => column.Cell.Left).ThenByDescending(column => column.Cell.Top).ToList();
            foreach (var column in columns) column.Limit = TextLimit(column.Cell, column.Indices.Select(index => geometric[index]));
            var divided = columns.Count == 1;
            if (divided) columns = SplitByChannels(columns[0], geometric);
            if (columns.Count < 2) continue;
            foreach (var column in columns)
            {
                var fragments = column.Indices.Select(index => geometric[index]).ToArray();
                column.Lines = MergeAdjacentIntoLines(fragments, null);
                column.FixedPitch = IsFixedPitch(fragments);
            }
            if (columns.All(column => column.Lines.Count < 2)) continue;

            var all = columns.SelectMany(column => column.Indices).ToArray();
            var byId = all.Select(index => geometric[index]).ToDictionary(fragment => fragment.SourceTextIds[0]);
            var wrapped = columns.Select(column => column.FixedPitch ? new WrappedColumn(column.Lines, false) : JoinWrappedLines(column, byId)).ToList();
            // Blocks of a frame that white space divides, which pair up line by line, are rows of labels and values.
            if (divided && LooksLikeRows(columns, wrapped)) continue;
            var leader = all.Min();
            var block = new List<PdfTextRegion>();
            foreach (var column in wrapped) block.AddRange(column.Lines);
            leaders.Add(geometric[leader].SourceTextIds[0]);
            blocks[geometric[leader].SourceTextIds[0]] = block;
            foreach (var index in all) if (index != leader) claimed.Add(index);
            columnCount = Math.Max(columnCount, columns.Count);
        }
        if (blocks.Count == 0) return null;
        return new BoxArrangement(geometric.Where((_, index) => !claimed.Contains(index)).ToArray(), leaders, blocks, columnCount);
    }

    /// <summary>Whether another box lies directly above or below <paramref name="index"/>, with the same
    /// left and right edges and at most <see cref="StackedGap"/> between them.</summary>
    private static bool HasStackedNeighbour(IReadOnlyList<BoxCell> cells, int index)
    {
        var cell = cells[index];
        for (var other = 0; other < cells.Count; other++)
        {
            if (other == index) continue;
            var below = cells[other];
            if (Math.Abs(cell.Left - below.Left) > 2 * BoxEdgeTolerance || Math.Abs(cell.Right - below.Right) > 2 * BoxEdgeTolerance) continue;
            var gap = Math.Max(cell.Bottom - below.Top, below.Bottom - cell.Top);
            if (gap >= -BoxEdgeTolerance && gap <= StackedGap) return true;
        }
        return false;
    }

    /// <summary>Whether two boxes sit next to each other: they share half the height of the shorter,
    /// do not overlap, and are not far apart.</summary>
    private static bool SideBySide(BoxCell a, BoxCell b, double pageWidth)
    {
        var overlap = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        if (overlap < SideBySideOverlap * Math.Min(a.Height, b.Height)) return false;
        var (left, right) = a.Left <= b.Left ? (a, b) : (b, a);
        var gap = right.Left - left.Right;
        return gap >= -BoxEdgeTolerance && gap <= Math.Max(MinColumnGapPoints, MaxColumnGap * pageWidth);
    }

    /// <summary>Cuts one box's text into blocks at the channels of white space no line crosses, when
    /// they are wide, text lies on both sides of them and each block is a block of text (see
    /// <see cref="ChannelEm"/>); one column, unchanged, when the box has no such channel.</summary>
    private static List<BoxColumn> SplitByChannels(BoxColumn column, PdfTextRegion[] geometric)
    {
        var fragments = column.Indices.Select(index => geometric[index]).ToArray();
        if (fragments.Length < 4 || fragments.Any(fragment => !fragment.MeasuredWidth) || IsFixedPitch(fragments)) return [column];
        var size = Median(fragments.Select(fragment => fragment.FontSize!.Value));
        var minimum = Math.Max(ChannelEm * size, MinChannel);
        // The stretches of the box's width some text covers: what lies between two of them is empty on every line.
        var spans = fragments.Where(fragment => !string.IsNullOrWhiteSpace(fragment.Text))
            .Select(fragment => (Left: fragment.BoundingBox.X, Right: fragment.BoundingBox.X + fragment.BoundingBox.Width))
            .OrderBy(span => span.Left).ToArray();
        if (spans.Length < 4) return [column];
        var dividers = new List<double>();
        var reach = spans[0].Right;
        foreach (var span in spans.Skip(1))
        {
            if (span.Left - reach >= minimum) dividers.Add((reach + span.Left) / 2);
            reach = Math.Max(reach, span.Right);
        }
        if (dividers.Count == 0 || dividers.Count > MaxChannels) return [column];

        var parts = Enumerable.Range(0, dividers.Count + 1).Select(_ => new List<int>()).ToArray();
        foreach (var index in column.Indices)
        {
            var middle = geometric[index].BoundingBox.X + geometric[index].BoundingBox.Width / 2;
            parts[dividers.Count(divider => divider < middle)].Add(index);
        }
        foreach (var part in parts)
        {
            if (part.Count == 0) return [column];
            var text = part.Select(index => geometric[index]).Where(fragment => !string.IsNullOrWhiteSpace(fragment.Text)).ToArray();
            if (text.Length == 0 || BaselinesOf(text).Count < ChannelBaselines ||
                text.Max(fragment => fragment.BoundingBox.X + fragment.BoundingBox.Width) - text.Min(fragment => fragment.BoundingBox.X) < BlockEm * size)
                return [column];
        }
        // Side by side, not one block above the other that happen not to overlap: on two lines at least
        // there is text on both sides of each channel.
        var baselines = BaselinesOf(fragments.Where(fragment => !string.IsNullOrWhiteSpace(fragment.Text)));
        foreach (var divider in dividers)
        {
            var both = baselines.Count(baseline =>
                baseline.Any(fragment => fragment.BoundingBox.X + fragment.BoundingBox.Width / 2 < divider) &&
                baseline.Any(fragment => fragment.BoundingBox.X + fragment.BoundingBox.Width / 2 > divider));
            if (both < ChannelBaselines) return [column];
        }
        var edges = new List<double> { column.Cell.Left };
        edges.AddRange(dividers);
        edges.Add(column.Cell.Right);
        // The right edge of the frame is drawn, the channels between blocks are not.
        return parts.Select((part, cell) => new BoxColumn(
            new BoxCell(edges[cell], column.Cell.Bottom, edges[cell + 1], column.Cell.Top), part)
        {
            Limit = cell == parts.Length - 1 ? TextLimit(column.Cell, fragments) : null,
        }).ToList();
    }

    /// <summary>Whether <paramref name="fragments"/> are set in a fixed-pitch font (see <see cref="FixedPitchRatio"/>);
    /// false when their glyph widths are not known.</summary>
    private static bool IsFixedPitch(IEnumerable<PdfTextRegion> fragments)
    {
        int ascii = 0, others = 0, narrowCount = 0, broadCount = 0;
        double narrow = 0, broad = 0;
        foreach (var fragment in fragments)
        {
            if (fragment.Glyphs is not { } glyphs || fragment.FontSize is not > 0) continue;
            for (var index = 0; index < fragment.Text.Length && index < glyphs.Count; index++)
            {
                var character = fragment.Text[index];
                if (char.IsWhiteSpace(character)) continue;
                if (character is < '!' or > '~')
                {
                    others++;
                    continue;
                }
                ascii++;
                var advance = (glyphs[index].Right - glyphs[index].Left) / fragment.FontSize.Value;
                if (NarrowCharacters.Contains(character))
                {
                    narrow += advance;
                    narrowCount++;
                }
                else if (BroadCharacters.Contains(character))
                {
                    broad += advance;
                    broadCount++;
                }
            }
        }
        return ascii >= 2 * others && narrowCount >= 3 && broadCount >= 3 && narrow / narrowCount >= FixedPitchRatio * (broad / broadCount);
    }

    /// <summary>The fragments grouped by baseline, with the tolerance <see cref="MergeAdjacentIntoLines"/>
    /// uses.</summary>
    private static List<List<PdfTextRegion>> BaselinesOf(IEnumerable<PdfTextRegion> fragments)
    {
        var baselines = new List<List<PdfTextRegion>>();
        foreach (var fragment in fragments.OrderByDescending(fragment => fragment.BoundingBox.Y))
        {
            if (baselines.Count > 0 &&
                Math.Abs(baselines[^1][0].BoundingBox.Y - fragment.BoundingBox.Y) <= Math.Max(1, fragment.BoundingBox.Height * 0.35))
                baselines[^1].Add(fragment);
            else baselines.Add([fragment]);
        }
        return baselines;
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return ordered[ordered.Length / 2];
    }

    /// <summary>One character of a line as it is drawn: where it starts and ends along the baseline, and
    /// whether a word space lies before it that the text does not hold (a gap between two shows).</summary>
    private readonly record struct DrawnCharacter(char Character, double Left, double Right, bool Detached);

    /// <summary>One line of a boxed column as wrapped text reads it: its size, where it starts and ends,
    /// what starts it - a bullet or a number - and where the text after that begins, and its characters
    /// as drawn: <paramref name="End"/> is where the last one that is not white space ends, and
    /// <paramref name="FirstCharacter"/> and <paramref name="LastCharacter"/> are the first and the last.</summary>
    private sealed record WrapLine(PdfTextRegion Line, double Size, bool HasMarker, double? TextStart,
        IReadOnlyList<DrawnCharacter> Characters, double SpaceWidth, double End, char FirstCharacter, char LastCharacter)
    {
        public double Left => Line.BoundingBox.X;
        public double Right => Line.BoundingBox.X + Line.BoundingBox.Width;
        public double Baseline => Line.BoundingBox.Y;
    }

    /// <summary>The lines of a column that follow one another the way the lines of a paragraph do.</summary>
    private sealed class WrapParagraph(WrapLine first)
    {
        public WrapLine First { get; } = first;
        public WrapLine Last { get; set; } = first;
        public int Lines { get; set; } = 1;
        // The edge the lines after the first start at, once there are two.
        public double ContinuationLeft { get; set; } = first.HasMarker && first.TextStart is { } start ? start : first.Left;
    }

    /// <summary>What the break between two lines is. <see cref="Soft"/>: the next line's first word would not
    /// have fitted after the line - the break was forced - and nothing says the next line is an item of its
    /// own. <see cref="Unforced"/>: it would have fitted; the line ends short of the column, so it ends a
    /// paragraph or is a line of its own. <see cref="Doubtful"/>: the break was forced but the next line
    /// looks like the start of an item (a capital letter after a line that closes nothing); a paragraph
    /// with such a break in it is left as it is.</summary>
    private enum WrapBreak { Soft, Unforced, Doubtful }

    /// <summary>A boxed column with its wrapped lines joined: the lines as they are read, and whether the
    /// column is one paragraph - every line but a heading above it joined into one.</summary>
    private sealed record WrappedColumn(List<PdfTextRegion> Lines, bool Paragraph);

    /// <summary>Joins each line of a boxed column that wraps to the line it continues. Lines belong to one
    /// paragraph only if they follow one another like its lines do: the next sits directly below the line,
    /// at most one line spacing of the column away, is of the same size (to a tenth), starts with neither a
    /// bullet nor a number, and begins where the text of a bullet does or at the paragraph's edge. Of such
    /// lines each wraps to the next only when the break after it was forced (see <see cref="ClassifyBreak"/>).
    /// A run of lines whose breaks are forced is joined only if no break in it is doubtful, so that a
    /// paragraph is joined whole or not at all; where no edge is drawn at the column's right it also has
    /// to end in a line that falls short of the longest by half an em, be at least
    /// <see cref="MinRaggedLines"/> lines long or end a sentence, and be prose (the lines under a bullet or
    /// a number need only fall short). The text keeps every character: lines are joined without a space
    /// between East Asian characters and before closing punctuation, and with one between words.</summary>
    private static WrappedColumn JoinWrappedLines(BoxColumn column, IReadOnlyDictionary<int, PdfTextRegion> fragments)
    {
        var lines = column.Lines;
        var described = lines.Select(line => DescribeLine(line, fragments)).ToArray();
        if (described.Count(line => line is not null) < 2) return new WrappedColumn([.. lines], false);

        // The column's own line spacing: the least gap between two lines of one size.
        var leading = double.PositiveInfinity;
        for (var index = 1; index < described.Length; index++)
        {
            if (described[index - 1] is not { } above || described[index] is not { } below || !SimilarSize(above.Size, below.Size)) continue;
            var gap = above.Baseline - below.Baseline;
            if (gap >= 0.6 * below.Size) leading = Math.Min(leading, gap);
        }

        // Where the text may run to: the least that is known of the right boundary of the column.
        var longest = described.Where(line => line is not null).Max(line => line!.End);
        var boundary = column.Limit is { } limit ? limit - RightEdgeSlack : longest;

        // Runs of lines that follow one another like the lines of a paragraph.
        var runs = new List<List<int>>();
        WrapParagraph? structure = null;
        for (var index = 0; index < described.Length; index++)
        {
            if (described[index] is not { } next)
            {
                structure = null;
                runs.Add([index]);
                continue;
            }
            if (structure is not null && ContinuesStructurally(structure, next, leading))
            {
                runs[^1].Add(index);
                if (structure.Lines == 1) structure.ContinuationLeft = next.Left;
                structure.Last = next;
                structure.Lines++;
                continue;
            }
            structure = new WrapParagraph(next);
            runs.Add([index]);
        }

        // In a run, the lines whose breaks are forced form a paragraph; one that ends short ends it.
        var joined = new List<PdfTextRegion>();
        var paragraphs = new List<(int From, int To)>();
        var sequence = new List<int>();
        var doubtful = false;
        void Close()
        {
            if (sequence.Count >= 2 && !doubtful && EndsLikeAParagraph())
            {
                joined.Add(JoinLines(sequence.Select(index => described[index]!).ToArray()));
                paragraphs.Add((sequence[0], sequence[^1]));
            }
            else
                foreach (var index in sequence) joined.Add(lines[index]);
            sequence.Clear();
            doubtful = false;
        }
        bool EndsLikeAParagraph()
        {
            if (column.Limit is not null) return true;
            var last = described[sequence[^1]]!;
            var shorter = last.End <= longest - PartialLastLineEm * last.Size;
            // The lines under a bullet or a number are the rest of its item, however many they are.
            if (described[sequence[0]]!.HasMarker) return shorter;
            var prose = sequence.Any(index => described[index]!.Line.Text.Any(ProseMarks.Contains)) ||
                sequence.Skip(1).Any(index => char.IsLower(described[index]!.FirstCharacter));
            return shorter && (sequence.Count >= MinRaggedLines || SentenceMarks.Contains(last.LastCharacter)) && prose;
        }
        foreach (var run in runs)
        {
            foreach (var index in run)
            {
                if (sequence.Count > 0)
                {
                    var kind = ClassifyBreak(described[sequence[^1]]!, described[index]!, boundary);
                    if (kind == WrapBreak.Unforced) Close();
                    else doubtful |= kind == WrapBreak.Doubtful;
                }
                sequence.Add(index);
            }
            Close();
        }
        var single = paragraphs.Count == 1 && paragraphs[0].To == lines.Count - 1 && paragraphs[0].From <= 1;
        return new WrappedColumn(joined, single);
    }

    private static PdfTextRegion JoinLines(IReadOnlyList<WrapLine> lines)
    {
        var text = lines[0].Line;
        foreach (var next in lines.Skip(1))
        {
            var separator = JoinSeparator(text.Text, next.Line.Text);
            text = new PdfTextRegion(text.Text + separator + next.Line.Text,
                new Geometry(text.BoundingBox.CoordinateSpace, Math.Min(text.BoundingBox.X, next.Left),
                    Math.Max(text.BoundingBox.Y, next.Baseline), Math.Max(text.BoundingBox.X + text.BoundingBox.Width, next.Right) - Math.Min(text.BoundingBox.X, next.Left),
                    Math.Max(text.BoundingBox.Height, next.Line.BoundingBox.Height)),
                text.ReadingOrder, [.. text.SourceTextIds, .. next.Line.SourceTextIds])
            {
                Struck = PdfTextDecorations.Join(text.Struck, PdfTextDecorations.Shift(next.Line.Struck, text.Text.Length + separator.Length)),
                Underlined = PdfTextDecorations.Join(text.Underlined, PdfTextDecorations.Shift(next.Line.Underlined, text.Text.Length + separator.Length)),
            };
        }
        return text;
    }

    private static bool SimilarSize(double a, double b) => Math.Abs(a - b) <= SameSize * Math.Max(a, b);

    /// <summary>What a line of a boxed column is for deciding whether it wraps; null when it cannot be
    /// decided (a size or a width that is not measured).</summary>
    private static WrapLine? DescribeLine(PdfTextRegion line, IReadOnlyDictionary<int, PdfTextRegion> fragments)
    {
        var parts = line.SourceTextIds.Select(id => fragments.GetValueOrDefault(id)).ToArray();
        if (parts.Length == 0 || parts.Any(part => part is not { MeasuredWidth: true, FontSize: > 0 })) return null;
        var measured = parts.Select(part => part!).ToArray();
        var size = measured.Max(part => part.FontSize!.Value);
        var characters = DrawnCharactersOf(measured);
        var last = characters.FindLastIndex(character => !char.IsWhiteSpace(character.Character));
        if (last < 0) return null;
        var first = characters.FindIndex(character => !char.IsWhiteSpace(character.Character));
        var marker = ItemMarkerLength(line.Text);
        var spaces = characters.Where(character => character.Character == ' ' && character.Right - character.Left > 0.01 * size)
            .Select(character => character.Right - character.Left).Order().ToArray();
        return new WrapLine(line, size, marker > 0, marker > 0 ? TextStartAfterMarker(measured, marker) : null, characters,
            spaces.Length > 0 ? spaces[spaces.Length / 2] : SpaceEm * size, characters[last].Right, characters[first].Character, characters[last].Character);
    }

    /// <summary>The characters of the shows of a line, each with the edges it is drawn between: its glyph's
    /// where the font's widths are placed, and otherwise an equal share of the show's width (a ligature
    /// that was given its text from outside, for one).</summary>
    private static List<DrawnCharacter> DrawnCharactersOf(PdfTextRegion[] parts)
    {
        var characters = new List<DrawnCharacter>();
        for (var part = 0; part < parts.Length; part++)
        {
            var fragment = parts[part];
            var share = fragment.BoundingBox.Width / Math.Max(1, fragment.Text.Length);
            for (var index = 0; index < fragment.Text.Length; index++)
            {
                var (left, right) = fragment.Glyphs is { } glyphs && index < glyphs.Count
                    ? glyphs[index]
                    : (fragment.BoundingBox.X + index * share, fragment.BoundingBox.X + (index + 1) * share);
                var detached = false;
                if (index == 0 && characters.Count > 0)
                {
                    detached = PdfTextRuns.IsWordGap(parts[part - 1], fragment, left - characters[^1].Right);
                }
                characters.Add(new DrawnCharacter(fragment.Text[index], left, right, detached));
            }
        }
        return characters;
    }

    private static bool IsAsianCharacter(char character) => Rune.TryCreate(character, out var rune) && PdfTextRuns.IsAsian(rune);

    /// <summary>The width of the first thing a line could not have been broken inside, and its first
    /// character: a word of Latin text (up to a space, a hyphen that a letter follows, or an East Asian
    /// character), or an East Asian character with the marks that cannot start a line behind it (and the
    /// character an opening bracket needs behind it). With <paramref name="token"/> it is all up to the
    /// next space, whatever it is made of: some producers fill lines with the words that spaces set apart
    /// and do not break inside them, East Asian characters or not. False for a line of white space.</summary>
    private static bool TryFirstUnit(WrapLine line, bool token, out double width, out char first)
    {
        var characters = line.Characters;
        var start = 0;
        while (start < characters.Count && char.IsWhiteSpace(characters[start].Character)) start++;
        if (start >= characters.Count)
        {
            (width, first) = (0, default);
            return false;
        }
        first = characters[start].Character;
        var end = start;
        if (IsAsianCharacter(first))
        {
            while (end + 1 < characters.Count && NoLineEnd.Contains(characters[end].Character)) end++;
            while (end + 1 < characters.Count && NoLineStart.Contains(characters[end + 1].Character)) end++;
        }
        else
        {
            while (end + 1 < characters.Count)
            {
                var next = characters[end + 1];
                if (char.IsWhiteSpace(next.Character) || IsAsianCharacter(next.Character) || next.Detached ||
                    (end > start && characters[end].Character is '-' or '‐' or '–' or '—' && char.IsLetter(next.Character))) break;
                end++;
            }
        }
        // All up to the next space, where a word is followed by East Asian characters or is one.
        if (token && (IsAsianCharacter(first) || end + 1 < characters.Count && IsAsianCharacter(characters[end + 1].Character)))
            while (end + 1 < characters.Count && !char.IsWhiteSpace(characters[end + 1].Character) && !characters[end + 1].Detached) end++;
        width = characters[end].Right - characters[start].Left;
        return true;
    }

    /// <summary>What the break between <paramref name="line"/> and <paramref name="next"/> is, in a column
    /// whose text runs to <paramref name="boundary"/> at the least. The break was forced when the first word
    /// of the next line, with the space before it, would not have fitted after the line (East Asian text:
    /// its first character, with the marks that cannot start a line; no space). Where the break falls at a
    /// space, the word is all up to the next space, East Asian characters in it or not. A forced break is
    /// doubtful when, between words (a space goes between the lines), the next line starts with a capital
    /// letter and the line closes nothing (no full stop, no closing bracket, no colon): it looks like the
    /// start of an item. A capital after a full stop is an ordinary start of a sentence, and a forced
    /// break there is a wrap. (A mark that cannot start a line, at the start of one, says the line
    /// continues the one above, whatever the producer did about it.)</summary>
    private static WrapBreak ClassifyBreak(WrapLine line, WrapLine next, double boundary)
    {
        var separator = JoinSeparator(line.Line.Text, next.Line.Text);
        var spaced = separator.Length > 0 || line.Line.Text.Length > 0 && char.IsWhiteSpace(line.Line.Text[^1]) ||
            next.Line.Text.Length > 0 && char.IsWhiteSpace(next.Line.Text[0]);
        if (!TryFirstUnit(next, spaced, out var unit, out var first)) return WrapBreak.Unforced;
        if (line.End + unit + (spaced ? line.SpaceWidth : 0) <= boundary - ForcedSlack) return WrapBreak.Unforced;

        var last = line.LastCharacter;
        return spaced && !IsAsianCharacter(last) && !IsAsianCharacter(first) && char.IsUpper(first) && !ClosingMarks.Contains(last)
            ? WrapBreak.Doubtful
            : WrapBreak.Soft;
    }

    /// <summary>Whether <paramref name="next"/> follows the paragraph being put together the way its lines
    /// follow one another: see <see cref="JoinWrappedLines"/>.</summary>
    private static bool ContinuesStructurally(WrapParagraph paragraph, WrapLine next, double leading)
    {
        var last = paragraph.Last;
        var size = paragraph.First.Size;
        if (next.HasMarker || !SimilarSize(size, next.Size) || !SimilarSize(last.Size, next.Size)) return false;
        var gap = last.Baseline - next.Baseline;
        var allowed = double.IsFinite(leading) ? Math.Min(MaxLeadingEm * size, leading * LeadingSlack + 0.5) : MaxLeadingEm * size;
        if (gap < 0.6 * size || gap > allowed) return false;

        var tolerance = ContinuationIndentEm * size;
        // Text that hangs from a bullet or a number: its next line is indented to where that text begins.
        var hangs = paragraph.First.HasMarker && paragraph.ContinuationLeft - paragraph.First.Left > 0.3 * size &&
            Math.Abs(next.Left - paragraph.ContinuationLeft) <= tolerance;
        // Otherwise the line starts at the paragraph's edge (under the bullet or the number, if it has one).
        return hangs || Math.Abs(next.Left - (paragraph.Lines >= 2 ? paragraph.ContinuationLeft : paragraph.First.Left)) <= tolerance;
    }

    /// <summary>Whether the blocks that white space divides a frame into are rows of labels and values
    /// and not columns of text: the same number of lines in each, on the same baselines, and the first is no
    /// paragraph (a paragraph is one block of lines that wrap, with a heading above it at most). The lines of
    /// rows pair up across the frame, and none of them wraps; they are read as they always were.</summary>
    private static bool LooksLikeRows(IReadOnlyList<BoxColumn> columns, IReadOnlyList<WrappedColumn> wrapped)
    {
        var rows = columns[0].Lines.Count;
        if (rows < 2 || wrapped[0].Paragraph || columns.Any(column => column.Lines.Count != rows)) return false;
        // A list of bullets or numbers is a column of items, and reads as one.
        if (2 * columns[0].Lines.Count(line => ItemMarkerLength(line.Text) > 0) >= rows) return false;
        for (var row = 0; row < rows; row++)
        {
            var baseline = columns[0].Lines[row].BoundingBox.Y;
            if (columns.Any(column => Math.Abs(column.Lines[row].BoundingBox.Y - baseline) > Math.Max(1, column.Lines[row].BoundingBox.Height * 0.35)))
                return false;
        }
        return true;
    }

    /// <summary>Where the text of a box with a right edge drawn ends at the most: the edge less a padding as
    /// wide as the one on the left, which the leftmost text shows (up to <see cref="MaxPadding"/>).</summary>
    private static double TextLimit(BoxCell cell, IEnumerable<PdfTextRegion> fragments)
    {
        var left = fragments.Where(fragment => !string.IsNullOrWhiteSpace(fragment.Text)).Select(fragment => fragment.BoundingBox.X)
            .DefaultIfEmpty(cell.Left).Min();
        return cell.Right - Math.Clamp(left - cell.Left, 0, MaxPadding);
    }

    /// <summary>What goes between two lines joined into one. East Asian text has no spaces between its
    /// words, and none goes before closing punctuation or after an opening bracket or a mark that is
    /// part of a word ("TCP/", "well-"); between two words there is one, and after a mark that stands
    /// on its own (" /") the space that followed it.</summary>
    private static string JoinSeparator(string before, string after)
    {
        if (before.Length == 0 || after.Length == 0) return string.Empty;
        char last = before[^1], first = after[0];
        if (char.IsWhiteSpace(last) || char.IsWhiteSpace(first)) return string.Empty;
        if (last is '/' or '／' or '-' or '–' or '—' or '|' or '＆' or '&' && before.Length >= 2 && char.IsWhiteSpace(before[^2])) return " ";
        if (Rune.DecodeLastFromUtf16(before, out var lastRune, out _) != System.Buffers.OperationStatus.Done ||
            Rune.DecodeFromUtf16(after, out var firstRune, out _) != System.Buffers.OperationStatus.Done ||
            PdfTextRuns.IsAsian(lastRune) || PdfTextRuns.IsAsian(firstRune)) return string.Empty;
        if (".,;:!?)]}%…”’".Contains(first) || "([{/\\-–—_~#@$“‘".Contains(last)) return string.Empty;
        return " ";
    }

    /// <summary>How many characters, not counting white space, the bullet or the number that starts
    /// <paramref name="text"/> is made of; 0 when it starts with neither. A line that starts with one
    /// is the start of an item of its own.</summary>
    private static int ItemMarkerLength(string text)
    {
        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        if (start >= text.Length) return 0;
        var rest = text.AsSpan(start);
        var character = rest[0];
        if ("•◦▪▫■□●○◆◇▶▷►▸‣⁃・･·∙※＊*★☆✓✔✗✘➢➤→⇒".Contains(character)) return 1;
        if (character is '-' or '–' or '—' or '−') return rest.Length > 1 && char.IsWhiteSpace(rest[1]) ? 1 : 0;
        // Circled numbers.
        if (character is >= '①' and <= '⑳' or >= '❶' and <= '❿' or >= '㉑' and <= '㉟' or >= '㊱' and <= '㊿') return 1;
        if (character is '第')
        {
            var digits = 1;
            while (digits < rest.Length && digits <= 4 && (IsDigit(rest[digits]) || "一二三四五六七八九十百".Contains(rest[digits]))) digits++;
            return digits > 1 && digits < rest.Length && "条章節項款".Contains(rest[digits]) ? digits + 1 : 0;
        }
        if (character is '(' or '（')
        {
            var length = 1;
            while (length < rest.Length && length <= 3 && IsDigit(rest[length])) length++;
            if (length == 1 && rest.Length > 2 && char.IsAsciiLetter(rest[1])) length = 2;
            return length > 1 && length < rest.Length && rest[length] is ')' or '）' ? length + 1 : 0;
        }
        if (IsDigit(character))
        {
            var length = 1;
            while (length < rest.Length && length < 3 && IsDigit(rest[length])) length++;
            // 1. 2) 3、 - but not 2.5 - and decimal section numbers, 4.1 or 4.1.2, that a space follows.
            if (length < rest.Length && rest[length] is '.' or ')' or '）' or '．' or '、' &&
                (length + 1 >= rest.Length || !IsDigit(rest[length + 1]))) return length + 1;
            var end = length;
            var parts = 1;
            while (end + 1 < rest.Length && rest[end] == '.' && IsDigit(rest[end + 1]))
            {
                end++;
                while (end < rest.Length && IsDigit(rest[end])) end++;
                parts++;
            }
            return parts > 1 && (end >= rest.Length || char.IsWhiteSpace(rest[end])) ? end : 0;
        }
        // a) A.
        if (char.IsAsciiLetter(character) && rest.Length > 2 && rest[1] is ')' or '.' && char.IsWhiteSpace(rest[2])) return 2;
        return 0;
    }

    private static bool IsDigit(char character) => character is >= '0' and <= '9' or >= '０' and <= '９';

    /// <summary>Where the text after a line's bullet or number begins: the left edge of the first
    /// character that follows the marker (<paramref name="markerLength"/> characters, white space
    /// apart). Null when the glyphs of that text are not placed.</summary>
    private static double? TextStartAfterMarker(IReadOnlyList<PdfTextRegion> parts, int markerLength)
    {
        var remaining = markerLength;
        foreach (var part in parts)
            for (var index = 0; index < part.Text.Length; index++)
            {
                var character = part.Text[index];
                if (remaining > 0)
                {
                    if (!char.IsWhiteSpace(character)) remaining--;
                    continue;
                }
                if (char.IsWhiteSpace(character)) continue;
                return part.Glyphs is { } glyphs && index < glyphs.Count ? glyphs[index].Left : null;
            }
        return null;
    }
}
