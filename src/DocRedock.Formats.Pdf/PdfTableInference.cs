using DocRedock.Core.Documents;

namespace DocRedock.Formats.Pdf;

/// <summary>Geometry-only table detection. It deliberately rejects partial grids, rotated
/// content, overlapping cells, and text that cannot be placed in exactly one cell.</summary>
public static class PdfTableInference
{
    private const int MaxGridLines = 4_096;
    private const int MaxGridIntersections = 16_384;
    private const int MaxTextRegions = 10_000;

    public static IReadOnlyList<PdfTable> Infer(int pageNumber, IReadOnlyList<PdfTextRegion> regions,
        VisualGraph graph, int maxCandidates = 64, bool nativeTagged = false)
    {
        ArgumentNullException.ThrowIfNull(regions); ArgumentNullException.ThrowIfNull(graph);
        var lines = (graph.Paths ?? []).SelectMany(AxisLine.CreateAll).ToArray();
        if (lines.Length > MaxGridLines || regions.Count > MaxTextRegions) return [];
        var components = SeparateGridComponents(lines);
        if (components.Count > 1)
        {
            // Infer disconnected grids independently so a table beside a second table or
            // diagram cannot inflate one candidate's bounds. Marked-content scope is not
            // available per component, therefore confidence remains inferred conservatively.
            var tables = new List<PdfTable>();
            foreach (var component in components.Take(maxCandidates))
            {
                var ids = component.Select(line => line.PathId).ToHashSet(StringComparer.Ordinal);
                var componentGraph = graph with { Paths = (graph.Paths ?? []).Where(path => ids.Contains(path.Id)).ToArray() };
                var table = Infer(pageNumber, regions, componentGraph, 1, nativeTagged: false).FirstOrDefault();
                if (table is not null) tables.Add(table with { Id = $"pdf-p{pageNumber}-table-{tables.Count + 1}" });
            }
            return tables;
        }
        var horizontal = lines.Where(line => line.Horizontal).ToArray();
        var vertical = lines.Where(line => !line.Horizontal).ToArray();
        if (horizontal.Length < 3 || vertical.Length < 3 || maxCandidates <= 0) return [];
        if ((long)horizontal.Length * vertical.Length > MaxGridIntersections) return [];

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
            var indexes = regions.Select((region, index) => (region, index))
                .Where(item => IsInside(cell, item.region.BoundingBox)).Select(item => item.index).ToArray();
            if (indexes.Any(index => !assigned.Add(index))) return [];
            var text = string.Join(" ", indexes.Select(index => regions[index].Text).Where(text => !string.IsNullOrWhiteSpace(text)));
            cells.Add(new PdfTableCell(row, column, 1, 1, cell, text, indexes));
        }
        var candidateBounds = new Geometry("pdf-user-space", left, bottom, right - left, top - bottom);
        // Text touching a cell boundary has no unique owner. Do not silently leave it in
        // native flow while also emitting the surrounding table.
        if (regions.Select((region, index) => (region, index)).Any(item => Intersects(candidateBounds, item.region.BoundingBox) && !assigned.Contains(item.index))) return [];
        // At least a 2x2 grid and four independently located text regions avoid promoting
        // decorative grids or a single-axis ruled list.
        if (assigned.Count < 4 || assigned.Select(index => CellFor(cells, index)).Distinct().Count() < 4) return [];
        var ordered = cells.GroupBy(cell => cell.Row).OrderByDescending(group => group.Key)
            .Select((group, outputRow) => new PdfTableRow(group.OrderBy(cell => cell.Column)
                .Select(cell => cell with { Row = outputRow }).ToArray())).ToArray();
        var bounds = candidateBounds;
        var sourceIds = lines.Where(line => line.Horizontal
                ? line.Fixed >= bottom - 1.5 && line.Fixed <= top + 1.5 && line.Maximum >= left - 1.5 && line.Minimum <= right + 1.5
                : line.Fixed >= left - 1.5 && line.Fixed <= right + 1.5 && line.Maximum >= bottom - 1.5 && line.Minimum <= top + 1.5)
            .Select(line => line.PathId).Distinct().ToArray();
        // A flow elsewhere on the page must not prevent a well-formed table from being
        // reconstructed. Only directed evidence that crosses this candidate disqualifies it.
        if (graph.Edges.Any(edge => edge.EdgeDirection == VisualEdgeDirection.Directed && edge.Geometry is { } geometry && Intersects(bounds, geometry))) return [];
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

    private static int CellFor(IEnumerable<PdfTableCell> cells, int region) => cells.First(cell => cell.TextRegionIndexes.Contains(region)).Row * 10000 + cells.First(cell => cell.TextRegionIndexes.Contains(region)).Column;
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
