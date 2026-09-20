using DocRedock.Core.Documents;

namespace DocRedock.Formats.Pdf;

/// <summary>Geometry-only table detection. It deliberately rejects partial grids, rotated
/// content, overlapping cells, and text that cannot be placed in exactly one cell.</summary>
public static class PdfTableInference
{
    private const int MaxGridLines = 4_096;
    private const int MaxGridIntersections = 16_384;
    private const int MaxTextRegions = 10_000;
    // Defensive bound on the "disconnected components recurse independently" branch below.
    // Narrowing a component back down to its own raw paths cannot preserve a same-component
    // coordinate-duplicate-removal decision made at a shallower recursion level (path-level
    // filtering has no notion of "this ONE segment of a shared rectangle path was already
    // dropped"), so a pathological input could otherwise keep regenerating an ambiguous split
    // indefinitely. An ordinary page's independent tables/diagrams resolve in 1-2 levels.
    private const int MaxComponentRecursionDepth = 8;

    public static IReadOnlyList<PdfTable> Infer(int pageNumber, IReadOnlyList<PdfTextRegion> regions,
        VisualGraph graph, int maxCandidates = 64, bool nativeTagged = false, int recursionDepth = 0)
    {
        ArgumentNullException.ThrowIfNull(regions); ArgumentNullException.ThrowIfNull(graph);
        var rawPaths = graph.Paths ?? [];
        if (rawPaths.Count > MaxGridLines || regions.Count > MaxTextRegions) return [];
        // P-Overlay: a schedule "today line" (a stroked shaft with a small filled triangular
        // arrowhead at one end, drawn ON TOP OF a table -- see PdfTableOverlayDetector) must never
        // be mistaken for one of the table's own ruling lines merely because it happens to span
        // the table's own extent on one axis. ArrowShaftPathIds mirrors the proximity test
        // PdfTextExtractor itself already uses to recognize an arrowhead triangle attached to a
        // shaft, applied directly to the raw path population so this works whether or not that
        // shaft ever resolved into a promoted, directed VisualEdge.
        var arrowShaftPathIds = FindArrowShaftMatches(rawPaths).Select(match => match.ShaftPathId).ToHashSet(StringComparer.Ordinal);
        var lines = rawPaths.Where(path => !arrowShaftPathIds.Contains(path.Id)).SelectMany(AxisLine.CreateAll).ToArray();
        if (lines.Length > MaxGridLines) return [];
        // P3 fix: a filled overlay bar/rectangle drawn ON a table (see PdfTableOverlayDetector) is
        // itself a closed `re`-painted path, which AxisLine.CreateAll -- correctly, for a genuine
        // rectangular cell border -- decomposes into 4 straight segments sharing that one path's
        // id. The previous rule dropped EVERY rectangle-sourced segment in a crossing-connected
        // component (the same grouping SeparateGridComponents uses below) as soon as that component
        // also contained ANY plain (non-rectangle-sourced) line -- which erased a perfectly
        // ordinary table's own outer `re S` frame whenever its interior used plain `m`/`l` rules (an
        // everyday, non-overlay authoring style), reducing its row/column count or losing the table
        // entirely (an output CHANGE for a PDF with no overlay at all). Replaced with per-coordinate
        // duplicate removal, scoped to the same component: a rectangle-sourced segment is dropped
        // only when a plain segment ALREADY occupies the same axis and coordinate (+-1.5pt) in that
        // component -- a genuine duplicate of a boundary the plain lines already established.
        // Anything else (a uniquely positioned frame side, or an entire rectangle-bordered table
        // with no plain lines at all) is retained exactly as before.
        var rectangleSourcedPathIds = rawPaths.Where(IsRectangleSourced).Select(path => path.Id).ToHashSet(StringComparer.Ordinal);
        lines = SeparateGridComponents(lines).SelectMany(component =>
        {
            var plainCoordinatesByAxis = component.Where(line => !rectangleSourcedPathIds.Contains(line.PathId))
                .GroupBy(line => line.Horizontal).ToDictionary(group => group.Key, group => group.Select(line => line.Fixed).ToArray());
            return component.Where(line => !rectangleSourcedPathIds.Contains(line.PathId) ||
                !(plainCoordinatesByAxis.TryGetValue(line.Horizontal, out var coordinates) &&
                  coordinates.Any(coordinate => Math.Abs(coordinate - line.Fixed) <= 1.5)));
        }).ToArray();
        var components = SeparateGridComponents(lines);
        if (components.Count > 1)
        {
            if (recursionDepth >= MaxComponentRecursionDepth) return [];
            // Infer disconnected grids independently so a table beside a second table or
            // diagram cannot inflate one candidate's bounds. Marked-content scope is not
            // available per component, therefore confidence remains inferred conservatively.
            var tables = new List<PdfTable>();
            foreach (var component in components.Take(maxCandidates))
            {
                var ids = component.Select(line => line.PathId).ToHashSet(StringComparer.Ordinal);
                var componentGraph = graph with { Paths = (graph.Paths ?? []).Where(path => ids.Contains(path.Id)).ToArray() };
                var table = Infer(pageNumber, regions, componentGraph, 1, nativeTagged: false, recursionDepth + 1).FirstOrDefault();
                if (table is not null) tables.Add(table with { Id = $"pdf-p{pageNumber}-table-{tables.Count + 1}" });
            }
            return tables;
        }
        var horizontal = lines.Where(line => line.Horizontal).ToArray();
        var vertical = lines.Where(line => !line.Horizontal).ToArray();
        if (horizontal.Length < 3 || vertical.Length < 3 || maxCandidates <= 0) return [];
        if ((long)horizontal.Length * vertical.Length > MaxGridIntersections) return [];

        // A structural rule must cover the established component extent, possibly as
        // adjoining cell-border segments. Length alone cannot turn an interior bar or
        // an unresolved arrow shaft into a new row/column boundary.
        var leftEstimate = horizontal.Min(line => line.Minimum);
        var rightEstimate = horizontal.Max(line => line.Maximum);
        var bottomEstimate = vertical.Min(line => line.Minimum);
        var topEstimate = vertical.Max(line => line.Maximum);
        horizontal = FullyCoveredLevels(horizontal, leftEstimate, rightEstimate);
        vertical = FullyCoveredLevels(vertical, bottomEstimate, topEstimate);
        if (horizontal.Length < 3 || vertical.Length < 3) return [];
        // Propagate the exclusion back into `lines` itself: `sourceIds` below re-derives which
        // paths belong to this table from `lines`, not from `horizontal`/`vertical` alone, so a
        // short bar segment excluded above (but left dangling in the original `lines`) would
        // otherwise still be folded into the table's own SourcePathIds -- silently hiding the
        // whole bar from PdfTableOverlayDetector instead of leaving it as overlay content.
        lines = [.. horizontal, .. vertical];

        // This release recognizes one fully covered, rectilinear grid at a time. A second
        // independent grid is still safely left as native text/vector fallback.
        var xs = Cluster(vertical.Select(line => line.Fixed));
        var ys = Cluster(horizontal.Select(line => line.Fixed));
        if (xs.Length < 3 || ys.Length < 3 || !Regular(xs) || !Regular(ys)) return [];
        var left = xs[0]; var right = xs[^1]; var bottom = ys[0]; var top = ys[^1];
        if (!CoversLevel(horizontal, left, right) || !CoversLevel(vertical, bottom, top)) return [];

        var cells = new List<PdfTableCell>();
        var assigned = new HashSet<int>();
        for (var row = 0; row < ys.Length - 1; row++)
        for (var column = 0; column < xs.Length - 1; column++)
        {
            var cell = new Geometry("pdf-user-space", xs[column], ys[row], xs[column + 1] - xs[column], ys[row + 1] - ys[row]);
            var inside = regions.Where(region => IsInside(cell, region.BoundingBox)).ToArray();
            var ids = inside.SelectMany(region => region.SourceTextIds).ToArray();
            if (ids.Any(id => !assigned.Add(id))) return [];
            var text = string.Join(" ", inside.Select(region => region.Text).Where(value => !string.IsNullOrWhiteSpace(value)));
            cells.Add(new PdfTableCell(row, column, 1, 1, cell, text, ids));
        }
        var candidateBounds = new Geometry("pdf-user-space", left, bottom, right - left, top - bottom);
        // Text touching a cell boundary has no unique owner. Do not silently leave it in
        // native flow while also emitting the surrounding table.
        if (regions.Any(region => Intersects(candidateBounds, region.BoundingBox) &&
            region.SourceTextIds.Any(id => !assigned.Contains(id)))) return [];
        // At least a 2x2 grid and four independently located text regions avoid promoting
        // decorative grids or a single-axis ruled list.
        if (assigned.Count < 4 || assigned.Select(id => CellFor(cells, id)).Distinct().Count() < 4) return [];
        var ordered = cells.GroupBy(cell => cell.Row).OrderByDescending(group => group.Key)
            .Select((group, outputRow) => new PdfTableRow(group.OrderBy(cell => cell.Column)
                .Select(cell => cell with { Row = outputRow }).ToArray())).ToArray();
        var bounds = candidateBounds;
        var sourceIds = lines.Where(line => line.Horizontal
                ? line.Fixed >= bottom - 1.5 && line.Fixed <= top + 1.5 && line.Maximum >= left - 1.5 && line.Minimum <= right + 1.5
                : line.Fixed >= left - 1.5 && line.Fixed <= right + 1.5 && line.Maximum >= bottom - 1.5 && line.Minimum <= top + 1.5)
            .Select(line => line.PathId).Distinct()
            // P-Overlay: a rectangle-sourced path excluded from grid-building above (e.g. a
            // header-row background fill sitting exactly behind the header, whose own left/right
            // edges duplicate the table's outer boundary and whose top/bottom duplicate a row
            // boundary) is redundant table furniture, not overlay content -- fold its path id in
            // here too so PdfTableOverlayDetector never mistakes a full-width background rectangle
            // for a bar spanning the header row. A rectangle whose OWN edges do not each coincide
            // with an established grid coordinate (e.g. a schedule-overlay bar sitting inside a
            // single cell) is excluded from this fold-back and stays a genuine overlay candidate.
            // P7 fix: also require the candidate furniture rectangle's own geometry to actually
            // intersect THIS table's bounds. IsGridAlignedFurniture tests xs/ys coordinate
            // membership only, over rectangleSourcedPathIds drawn from the WHOLE page's raw paths
            // -- without this gate a rectangle belonging to an entirely different table (e.g. a
            // second table stacked below this one, reusing the same column positions) could satisfy
            // that coordinate check purely by numeric coincidence and be wrongly folded into THIS
            // table's own pdf_source_path_ids/readable output.
            .Concat(rectangleSourcedPathIds.Where(pathId => IsGridAlignedFurniture(rawPaths, pathId, xs, ys, bounds)))
            .Distinct().ToArray();
        // A flow elsewhere on the page must not prevent a well-formed table from being
        // reconstructed. Only directed evidence that crosses this candidate disqualifies it --
        // P-Overlay: unless most of that edge's own bbox/length lies inside the table AND it does
        // not coincide with one of the table's own ruling-line coordinates: that shape is a
        // schedule overlay (an arrow/line drawn ON the table), not a diagram connector the table
        // happens to cross, and PdfTableOverlayDetector re-attaches it to the table's own
        // row/column range once the table exists (table-overlay-spec-xlsx-docx-pdf.md section 3).
        // An edge that still spends most of its own bbox/length OUTSIDE the table disqualifies it
        // exactly as before.
        if (graph.Edges.Any(edge => edge.EdgeDirection == VisualEdgeDirection.Directed && edge.Geometry is { } geometry &&
                Intersects(bounds, geometry) && !IsOverlayCandidateEdge(geometry, bounds, xs, ys)))
            return [];
        return [new PdfTable($"pdf-p{pageNumber}-table-1", pageNumber, bounds, ordered,
            nativeTagged ? PdfTableConfidence.NativeTagged : PdfTableConfidence.HighConfidenceInferred, sourceIds)];
    }

    /// <summary>Recognizes a real marked-content table operator while ignoring comments and
    /// literal text. It is deliberately narrow: a missing tag merely yields inferred
    /// confidence; a false tag must never elevate a table's confidence.</summary>
    public static bool HasNativeTableMarkedContent(string content)
    {
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '%') { while (index < content.Length && content[index] is not '\r' and not '\n') index++; continue; }
            if (IsTokenAt(content, index, "BI"))
            {
                // Inline-image dictionaries and bytes are opaque content. They can contain
                // arbitrary strings such as `/Table BMC`, which are not marked-content tags.
                index += 2;
                while (index < content.Length && !IsTokenAt(content, index, "EI")) index++;
                index++; // the loop increment consumes the second E/I character
                continue;
            }
            if (content[index] == '(')
            {
                for (index++; index < content.Length; index++)
                {
                    if (content[index] == '\\') { index++; continue; }
                    if (content[index] == ')') break;
                }
                continue;
            }
            if (content[index] == '/')
            {
                var cursor = index + 1;
                var name = new System.Text.StringBuilder();
                while (cursor < content.Length && !char.IsWhiteSpace(content[cursor]) && content[cursor] is not '/' and not '(' and not ')' and not '<' and not '>')
                {
                    if (content[cursor] == '#' && cursor + 2 < content.Length &&
                        int.TryParse(content.AsSpan(cursor + 1, 2), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var escaped))
                    { name.Append((char)escaped); cursor += 3; continue; }
                    name.Append(content[cursor++]);
                }
                if (!string.Equals(name.ToString(), "Table", StringComparison.Ordinal)) continue;
                while (cursor < content.Length && char.IsWhiteSpace(content[cursor])) cursor++;
                if (content.AsSpan(cursor).StartsWith("BMC".AsSpan(), StringComparison.Ordinal) &&
                    (cursor + 3 == content.Length || char.IsWhiteSpace(content[cursor + 3])))
                {
                    var scopeEnd = cursor + 3;
                    while (scopeEnd < content.Length && !IsTokenAt(content, scopeEnd, "EMC")) scopeEnd++;
                    if (scopeEnd >= content.Length) return false;
                    // Marked content applies only to its own drawing scope. A Table tag for
                    // one object must not elevate an unrelated grid later on the page.
                    return !ContainsPaintOperator(content, 0, cursor) &&
                        !ContainsPaintOperator(content, scopeEnd + 3, content.Length);
                }
            }
        }
        return false;

        static bool IsTokenAt(string value, int index, string token) => index >= 0 && index + token.Length <= value.Length &&
            value.AsSpan(index, token.Length).SequenceEqual(token) &&
            (index == 0 || char.IsWhiteSpace(value[index - 1])) &&
            (index + token.Length == value.Length || char.IsWhiteSpace(value[index + token.Length]));

        static bool ContainsPaintOperator(string value, int start, int end)
        {
            for (var index = start; index < end; index++)
            {
                if (value[index] == '%') { while (index < end && value[index] is not '\r' and not '\n') index++; continue; }
                if (value[index] == '(') { while (++index < end && value[index] != ')') if (value[index] == '\\') index++; continue; }
                foreach (var token in new[] { "m", "l", "re", "S", "s", "f", "F", "B", "b" })
                    if (IsTokenAt(value, index, token)) return true;
            }
            return false;
        }
    }

    private static int CellFor(IEnumerable<PdfTableCell> cells, int sourceTextId)
    {
        var cell = cells.First(candidate => candidate.SourceTextIds.Contains(sourceTextId));
        return cell.Row * 10000 + cell.Column;
    }

    /// <summary>A small closed shape (<see cref="MarkerPathId"/>) matched to the straight
    /// two-point shaft (<see cref="ShaftPathId"/>) it sits at one end of -- an arrowhead attached
    /// to its line, recognized purely from raw path geometry (see <see
    /// cref="FindArrowShaftMatches"/>). <see cref="MarkerNearEnd"/> says which of the shaft's two
    /// points (its <c>Points[1]</c> when true, else <c>Points[0]</c>) the marker sits closest to --
    /// the same "start"/"end" distinction <see cref="Core.Documents.VisualConnectionEvidence.ArrowheadEvidence"/>
    /// uses elsewhere in this codebase.</summary>
    internal readonly record struct ArrowShaftMatch(string ShaftPathId, string MarkerPathId, bool MarkerNearEnd);

    // P-Overlay: identifies which raw paths are the straight SHAFT of a schedule "today line"
    // (a stroked line + small filled triangular arrowhead, drawn ON TOP OF a table -- see
    // PdfTableOverlayDetector / table-overlay-spec-xlsx-docx-pdf.md section 3), so it is never
    // mistaken for one of the table's own ruling lines even when BuildVisualGraph itself could not
    // resolve it into a promoted, directed VisualEdge (no diagram node sits at either endpoint),
    // and so PdfTableOverlayDetector can tell an arrow shaft's own overlay classification apart
    // from its (separately drawn, but not an independent overlay) arrowhead triangle. Mirrors the
    // *pairing discipline* of PdfTextExtractor.BuildVisualGraph's own triangle-to-shaft matching:
    // each small marker-shaped candidate is matched to its single NEAREST shaft (not "any shaft
    // within a loose radius"), so one small shape can implicate at most one line -- otherwise a
    // small marker sitting near a table CORNER (where many long ruling lines converge, e.g. a
    // milestone diamond a few points from the table's bottom-right corner) would satisfy a
    // length-scaled tolerance against several long lines at once and wrongly strip the whole grid.
    internal static IReadOnlyList<ArrowShaftMatch> FindArrowShaftMatches(IReadOnlyList<VisualPath> allPaths)
    {
        var shafts = allPaths.Where(path => path.Points is { Count: 2 }).ToArray();
        var matches = new List<ArrowShaftMatch>();
        foreach (var marker in allPaths)
        {
            // Rectangles/diamonds/legends are not arrowheads, regardless of size.
            var vertices = marker.Points?.Distinct().ToArray();
            if (vertices is not { Length: 3 } || marker.Geometry is not { Width: > 0, Height: > 0 } box) continue;
            var size = Math.Sqrt(box.Width * box.Width + box.Height * box.Height);
            var candidates = new List<(VisualPath Shaft, bool AtEnd, double Distance)>();
            foreach (var shaft in shafts)
            {
                var points = shaft.Points!;
                var length = Distance(points[0], points[1]);
                if (length <= 0 || size > length * .30) continue;
                foreach (var atEnd in new[] { false, true })
                {
                    var end = points[atEnd ? 1 : 0];
                    var start = points[atEnd ? 0 : 1];
                    var ux = (end.X - start.X) / length; var uy = (end.Y - start.Y) / length;
                    var projected = vertices.Select(p => (Along: (p.X - end.X) * ux + (p.Y - end.Y) * uy,
                        Across: -(p.X - end.X) * uy + (p.Y - end.Y) * ux)).OrderBy(p => p.Along).ToArray();
                    var tolerance = Math.Max(.75, size * .10);
                    // Two base corners straddle the shaft axis; one tip points along it.
                    // The shaft ends on/inside the triangle, not merely near its bbox.
                    if (Math.Abs(projected[0].Along - projected[1].Along) > tolerance ||
                        projected[0].Across * projected[1].Across >= 0 ||
                        Math.Abs(projected[2].Across) > tolerance ||
                        Math.Abs((projected[0].Across + projected[1].Across) / 2) > tolerance ||
                        projected[2].Along - projected[1].Along < size * .20 ||
                        projected[0].Along > tolerance || projected[2].Along < -tolerance) continue;
                    candidates.Add((shaft, atEnd, Math.Abs(projected[0].Along)));
                }
            }
            var nearest = candidates.OrderBy(c => c.Distance).ToArray();
            if (nearest.Length == 0 || (nearest.Length > 1 && Math.Abs(nearest[0].Distance - nearest[1].Distance) < .1)) continue;
            matches.Add(new ArrowShaftMatch(nearest[0].Shaft.Id, marker.Id, nearest[0].AtEnd));
        }
        return matches;
    }

    private static AxisLine[] FullyCoveredLevels(AxisLine[] lines, double minimum, double maximum) =>
        Cluster(lines.Select(line => line.Fixed))
            .Select(level => lines.Where(line => Math.Abs(line.Fixed - level) <= 1.5).ToArray())
            .Where(level => CoversLevel(level, minimum, maximum)).SelectMany(level => level).ToArray();

    internal static double Distance(VisualPathPoint a, VisualPathPoint b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    // `re` (and any other closed, axis-aligned quadrilateral path) is always represented as a
    // 5-point closed subpath (first point repeated last) -- see AxisLine.CreateAll's own rectangle
    // branch just below, which this mirrors as a plain data check with no line-derivation of its own.
    private static bool IsRectangleSourced(VisualPath path) =>
        path.Points is { Count: 5 } points && points[0] == points[^1];

    // A rectangle is "furniture" (part of the table's own established structure, not overlay
    // content) only when EVERY one of its 4 own edges coincides with a coordinate the final grid
    // already established on the matching axis -- e.g. a header-row background fill whose left and
    // right edges duplicate the table's own outer left/right boundary, and whose top and bottom
    // duplicate two adjacent row boundaries. A schedule-overlay bar dropped inside a single cell
    // never satisfies this (none of its 4 edges land on an established xs/ys coordinate).
    private static bool IsGridAlignedFurniture(IReadOnlyList<VisualPath> rawPaths, string pathId,
        IReadOnlyList<double> xs, IReadOnlyList<double> ys, Geometry bounds)
    {
        var path = rawPaths.FirstOrDefault(candidate => candidate.Id == pathId);
        // P7 fix: gate on actual spatial intersection with THIS table's own bounds first, so a
        // rectangle belonging to a different table/component can never qualify merely because its
        // coordinates numerically coincide with this table's xs/ys by chance.
        if (path?.Geometry is not { } geometry || !Intersects(bounds, geometry)) return false;
        var left = geometry.X; var right = geometry.X + geometry.Width;
        var bottom = geometry.Y; var top = geometry.Y + geometry.Height;
        return xs.Any(x => Math.Abs(x - left) <= 1.5) && xs.Any(x => Math.Abs(x - right) <= 1.5) &&
            ys.Any(y => Math.Abs(y - bottom) <= 1.5) && ys.Any(y => Math.Abs(y - top) <= 1.5);
    }

    // P-Overlay: candidacy test for the directed-edge relaxation above -- mirrors
    // PptxAdapter.TryScoreTableOverlay's degenerate-line handling (a connector/line has no area, so
    // candidacy compares how much of its *length*, not area, falls inside the table).
    private static bool IsOverlayCandidateEdge(Geometry geometry, Geometry bounds, IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        if (CoincidesWithGridBoundary(geometry, xs, ys)) return false;
        var overlapX = Math.Max(0, Math.Min(geometry.X + geometry.Width, bounds.X + bounds.Width) - Math.Max(geometry.X, bounds.X));
        var overlapY = Math.Max(0, Math.Min(geometry.Y + geometry.Height, bounds.Y + bounds.Height) - Math.Max(geometry.Y, bounds.Y));
        if (geometry.Width > 0 && geometry.Height > 0)
        {
            var area = geometry.Width * geometry.Height;
            return area > 0 && overlapX * overlapY >= area * 0.5;
        }
        var length = Math.Max(geometry.Width, geometry.Height);
        if (length <= 0) return false;
        var inside = geometry.Height <= geometry.Width ? overlapX : overlapY;
        return inside >= length * 0.5;
    }

    // P-Overlay: a near-zero-width (vertical) or near-zero-height (horizontal) bbox sitting at one
    // of the table's own row/column boundary coordinates is the boundary line itself (or a
    // residual fragment of it), not overlay content drawn on top of the table.
    private static bool CoincidesWithGridBoundary(Geometry geometry, IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        if (geometry.Width <= .5 && xs.Any(x => Math.Abs(geometry.X - x) <= 1.5)) return true;
        if (geometry.Height <= .5 && ys.Any(y => Math.Abs(geometry.Y - y) <= 1.5)) return true;
        return false;
    }
    private static bool IsInside(Geometry cell, Geometry text)
    {
        var x = text.X + text.Width / 2; var y = text.Y + text.Height / 2;
        return x > cell.X + .01 && x < cell.X + cell.Width - .01 && y > cell.Y + .01 && y < cell.Y + cell.Height - .01;
    }
    private static bool Covers(AxisLine line, double minimum, double maximum) => line.Minimum <= minimum + 1.5 && line.Maximum >= maximum - 1.5;
    private static bool CoversLevel(IEnumerable<AxisLine> lines, double minimum, double maximum)
    {
        // Adjacent rectangle edges are allowed to form a rule together. A bent/open path
        // never reaches this code because AxisLine.CreateAll expands only a true rectangle.
        var spans = lines.OrderBy(line => line.Minimum).Select(line => (line.Minimum, line.Maximum)).ToArray();
        if (spans.Length == 0 || spans[0].Minimum > minimum + 1.5) return false;
        var covered = spans[0].Maximum;
        foreach (var span in spans.Skip(1))
        {
            if (span.Minimum > covered + 1.5) return false;
            covered = Math.Max(covered, span.Maximum);
        }
        return covered >= maximum - 1.5;
    }
    private static double[] Cluster(IEnumerable<double> values) => values.OrderBy(value => value).Aggregate(new List<double>(), (result, value) =>
    { if (result.Count == 0 || Math.Abs(result[^1] - value) > 1.5) result.Add(value); return result; }).ToArray();
    private static bool Regular(IReadOnlyList<double> values)
    {
        var gaps = values.Zip(values.Skip(1), (a, b) => b - a).Where(gap => gap > 1.5).ToArray();
        return gaps.Length >= 2 && gaps.Min() >= gaps.Max() * .4;
    }
    private static bool Intersects(Geometry left, Geometry right) => left.X <= right.X + right.Width && left.X + left.Width >= right.X &&
        left.Y <= right.Y + right.Height && left.Y + left.Height >= right.Y;
    private static IReadOnlyList<IReadOnlyList<AxisLine>> SeparateGridComponents(IReadOnlyList<AxisLine> lines)
    {
        var pending = new HashSet<int>(Enumerable.Range(0, lines.Count));
        var result = new List<IReadOnlyList<AxisLine>>();
        while (pending.Count > 0)
        {
            var start = pending.First(); pending.Remove(start);
            var component = new List<int> { start };
            for (var cursor = 0; cursor < component.Count; cursor++)
            {
                var current = lines[component[cursor]];
                foreach (var candidate in pending.Where(index => Crosses(current, lines[index])).ToArray())
                { pending.Remove(candidate); component.Add(candidate); }
            }
            result.Add(component.Select(index => lines[index]).ToArray());
        }
        return result;
    }
    private static bool Crosses(AxisLine left, AxisLine right)
    {
        if (left.Horizontal == right.Horizontal) return false;
        var horizontal = left.Horizontal ? left : right; var vertical = left.Horizontal ? right : left;
        return vertical.Fixed >= horizontal.Minimum - 1.5 && vertical.Fixed <= horizontal.Maximum + 1.5 &&
            horizontal.Fixed >= vertical.Minimum - 1.5 && horizontal.Fixed <= vertical.Maximum + 1.5;
    }
    private sealed record AxisLine(string PathId, bool Horizontal, double Fixed, double Minimum, double Maximum)
    {
        public static IEnumerable<AxisLine> CreateAll(VisualPath path)
        {
            if (path.Points is not { Count: >= 2 } points) return [];
            // A filled area has no structural border. Thin filled rules may still
            // act as grid strokes; backgrounds and duration bars cannot add rows.
            if (path.IsFilled == true && path.IsStroked == false &&
                path.Geometry is { Width: > 1.5, Height: > 1.5 }) return [];
            // PdfTextExtractor marks Bezier paths as low-confidence fallback. A curve whose
            // endpoints happen to align horizontally is still not a table rule.
            if (path.Confidence is { } confidence && confidence < .8) return [];
            if (points.Count == 2) return CreateSegment(path.Id, points[0], points[1]) is { } line ? [line] : [];
            // `re` is represented as a closed five-point rectangle. Do not split arbitrary
            // polylines: that would turn a bent connector into synthetic table rules.
            if (points.Count != 5 || points[0] != points[^1]) return [];
            var xs = points.Take(4).Select(point => point.X).Distinct().OrderBy(value => value).ToArray();
            var ys = points.Take(4).Select(point => point.Y).Distinct().OrderBy(value => value).ToArray();
            if (xs.Length != 2 || ys.Length != 2) return [];
            return points.Take(4).Zip(points.Skip(1).Take(4), (a, b) => CreateSegment(path.Id, a, b))
                .Where(line => line is not null).Cast<AxisLine>().ToArray();
        }

        private static AxisLine? CreateSegment(string pathId, VisualPathPoint a, VisualPathPoint b)
        {
            var dx = b.X - a.X; var dy = b.Y - a.Y; var length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= .01) return null;
            if (Math.Abs(dy) <= length * .0175) return new(pathId, true, (a.Y + b.Y) / 2, Math.Min(a.X, b.X), Math.Max(a.X, b.X));
            if (Math.Abs(dx) <= length * .0175) return new(pathId, false, (a.X + b.X) / 2, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y));
            return null;
        }
    }
}
