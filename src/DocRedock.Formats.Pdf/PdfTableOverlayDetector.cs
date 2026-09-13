using DocRedock.Core.Documents;

namespace DocRedock.Formats.Pdf;

// -------------------------------------------------------------------------------------------
// P-Overlay: PDF port of the table-overlay feature (table-overlay-spec.md /
// table-overlay-spec-xlsx-docx-pdf.md section 3). Japanese schedule pages draw arrow/bar/marker
// (diamond/triangle)/line shapes directly on top of a ruled table whose columns are dates. This
// maps each such shape -- reconstructed here purely from vector geometry, since a PDF page has no
// native "shape"/"table" object -- to the table row/column range it visually covers, using the
// exact JSON contract (PascalCase field names) the PPTX/XLSX/DOCX adapters already emit as
// `table_overlays`, so ReadableMarkdownSerializer.ApplyTableOverlays (already format-neutral)
// needs no PDF-specific change at all: it folds the glyph straight into the cell text.
// -------------------------------------------------------------------------------------------
public static class PdfTableOverlayDetector
{
    /// <summary>P6: result of <see cref="DetectAndFoldFurniture"/> -- the detected overlays plus
    /// any candidate raw path ids reclassified as table furniture rather than overlay content
    /// (see <see cref="DetectAndFoldFurniture"/>'s own remarks).</summary>
    public sealed record Detection(IReadOnlyList<PdfTableOverlay> Overlays, IReadOnlyList<string> FurniturePathIds);

    /// <summary>Detects every arrow/bar/marker/line shape drawn on top of <paramref name="table"/>.
    /// Candidates are every <see cref="VisualNode"/>, <see cref="VisualEdge"/> and raw
    /// <see cref="VisualPath"/> in <paramref name="graph"/> that is not one of the table's own
    /// ruling-line paths (<see cref="PdfTable.SourcePathIds"/>) and is not an arrowhead triangle
    /// already consumed as evidence for some other shaft (see
    /// <see cref="PdfTableInference.FindArrowShaftMatches"/>). Equivalent to
    /// <see cref="DetectAndFoldFurniture"/>'s own <see cref="Detection.Overlays"/> alone -- kept for
    /// callers (and tests) that only need the overlay list, not the furniture fold-back.</summary>
    public static IReadOnlyList<PdfTableOverlay> Detect(PdfTable table, VisualGraph graph) =>
        DetectAndFoldFurniture(table, graph).Overlays;

    /// <summary>Same detection as <see cref="Detect"/>, but ALSO reports which candidate shapes
    /// were reclassified as table furniture instead of overlay content (P6): an axis-aligned
    /// filled rectangle that would otherwise classify as a "bar" is furniture -- not a bar overlay
    /// -- when it sits within a single cell/row band and its own short side already covers 80% or
    /// more of that row's height (a header-row cell fill or status badge, not a slim progress bar).
    /// The caller is expected to fold <see cref="Detection.FurniturePathIds"/> into the table's own
    /// <see cref="PdfTable.SourcePathIds"/> so it disappears from the readable graph exactly like a
    /// ruling line (see <see cref="PdfVisualOutputCompactor.RemoveConsumedTableVisuals"/>), rather
    /// than lingering as a "━━" bar glyph that visually implies a status bar which isn't one.</summary>
    public static Detection DetectAndFoldFurniture(PdfTable table, VisualGraph graph)
    {
        ArgumentNullException.ThrowIfNull(table); ArgumentNullException.ThrowIfNull(graph);
        if (table.Rows.Count == 0 || table.Rows[0].Cells.Count == 0) return new Detection([], []);
        var (xs, ysTopDown) = BuildGrid(table);
        if (xs.Length < 2 || ysTopDown.Length < 2) return new Detection([], []);
        var tableBounds = table.Bounds;
        var excludedPathIds = table.SourcePathIds.ToHashSet(StringComparer.Ordinal);

        var allPaths = graph.Paths ?? [];
        // Every VisualNode/VisualEdge in this codebase is backed by exactly one raw VisualPath
        // (matched by Geometry for a node, by Points *reference* for an edge -- the same technique
        // PdfTextExtractor.BuildSourceItems uses). Finding that path is how a promoted shape's own
        // vertex list (Points) becomes available for shape classification below; a path matched
        // this way is also removed from separate raw-path candidacy so it is never double-counted.
        // Its own id is also what P6's furniture fold-back and PdfVisualOutputCompactor's ledger
        // updates key off -- never the promoted node/edge's own id.
        var pathsByGeometry = new Dictionary<Geometry, VisualPath>();
        var pathsByPoints = new Dictionary<IReadOnlyList<VisualPathPoint>, VisualPath>(ReferenceEqualityComparer.Instance);
        foreach (var path in allPaths)
        {
            if (path.Geometry is { } geometry) pathsByGeometry.TryAdd(geometry, path);
            if (path.Points is { } points) pathsByPoints.TryAdd(points, path);
        }
        // An arrowhead triangle drawn at one end of a shaft (see FindArrowShaftMatches) is
        // evidence for that shaft's own arrow classification below, never an independent overlay
        // in its own right -- exclude every matched marker path id from separate candidacy.
        var shaftMatchByShaftPathId = new Dictionary<string, PdfTableInference.ArrowShaftMatch>(StringComparer.Ordinal);
        var markerPathIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var match in PdfTableInference.FindArrowShaftMatches(allPaths))
        {
            shaftMatchByShaftPathId[match.ShaftPathId] = match;
            markerPathIds.Add(match.MarkerPathId);
        }

        var candidates = new List<(string ShapeId, string RawPathId, Geometry Geometry, IReadOnlyList<VisualPathPoint>? Points, string? ArrowheadEvidence, bool ArrowheadAtEnd)>();
        var consumedPathIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in graph.Nodes ?? [])
        {
            if (node.Geometry is not { } geometry) continue;
            VisualPath? source = pathsByGeometry.GetValueOrDefault(geometry);
            if (source is not null) consumedPathIds.Add(source.Id);
            candidates.Add((node.Id, source?.Id ?? node.Id, geometry, source?.Points, null, false));
        }
        foreach (var edge in graph.Edges ?? [])
        {
            if (edge.Geometry is not { } geometry) continue;
            VisualPath? source = edge.Path is { } path ? pathsByPoints.GetValueOrDefault(path) : null;
            if (source is not null) consumedPathIds.Add(source.Id);
            var shaftPathId = source?.Id;
            string? arrowheadEvidence = edge.Evidence?.ArrowheadEvidence;
            var arrowheadAtEnd = StringComparer.OrdinalIgnoreCase.Equals(arrowheadEvidence, "end");
            // A resolved, promoted edge already carries its own ArrowheadEvidence (set by
            // BuildVisualGraph once it links two nodes); an edge that never resolved into a
            // relation (no diagram node at either endpoint -- the common case for a schedule
            // "today line") never gets that field populated (VisualEdgeDirectionContradiction
            // forbids setting it on an Undirected edge), so fall back to the same geometric match
            // PdfTableInference.Infer itself uses to keep the shaft out of ruling-line candidacy.
            if (arrowheadEvidence is null && shaftPathId is not null && shaftMatchByShaftPathId.TryGetValue(shaftPathId, out var geometricMatch))
            {
                arrowheadEvidence = geometricMatch.MarkerNearEnd ? "end" : "start";
                arrowheadAtEnd = geometricMatch.MarkerNearEnd;
            }
            candidates.Add((edge.Id, shaftPathId ?? edge.Id, geometry, source?.Points ?? edge.Path, arrowheadEvidence, arrowheadAtEnd));
        }
        foreach (var path in allPaths)
        {
            if (path.Geometry is not { } geometry || consumedPathIds.Contains(path.Id) ||
                excludedPathIds.Contains(path.Id) || markerPathIds.Contains(path.Id)) continue;
            candidates.Add((path.Id, path.Id, geometry, path.Points, null, false));
        }

        var overlays = new List<PdfTableOverlay>();
        var furniturePathIds = new List<string>();
        foreach (var (shapeId, rawPathId, geometry, points, arrowheadEvidence, arrowheadAtEnd) in candidates)
        {
            if (excludedPathIds.Contains(shapeId)) continue;
            if (!TryScoreOverlay(geometry, tableBounds, out _)) continue;
            var isLineShaped = geometry.Width <= 0 || geometry.Height <= 0;
            // Spec: a line segment that coincides with one of the table's own row/column
            // boundaries is a residual ruling-line fragment (e.g. a grid edge that
            // SuppressTableGridEdges could not fold away), not overlay content.
            if (isLineShaped && CoincidesWithGridBoundary(geometry, xs, ysTopDown)) continue;

            var (startRow, endRow) = ComputeRowRange(geometry.Y, geometry.Y + geometry.Height, ysTopDown);
            var (startColumn, endColumn) = ComputeAxisRange(geometry.X, geometry.X + geometry.Width, xs);

            string kind; string direction; string? preset; string axis;
            if (isLineShaped)
            {
                if (arrowheadEvidence is not null)
                {
                    kind = "arrow";
                    (direction, axis) = ClassifyLineDirection(points, arrowheadAtEnd);
                }
                else { kind = "line"; direction = "none"; axis = CoverageAxis(startRow, endRow, startColumn, endColumn); }
                preset = null;
            }
            else
            {
                (kind, direction, preset) = ClassifyByPoints(points);
                axis = kind == "arrow" ? ArrowAxisFromDirection(direction) : CoverageAxis(startRow, endRow, startColumn, endColumn);
            }
            // P6/P6b: an axis-aligned filled rectangle ("bar") that nearly fills the COVERED ROW
            // BAND vertically -- its top within 20% of a row-height of the band's own top edge,
            // and its bottom within 20% of a row-height of the band's own bottom edge -- is a cell
            // fill/badge (e.g. a header row's background), not a slim status bar; column span
            // never matters here (a full-row-width fill is just the common case of this same
            // rule). Comparing against a single table-wide AVERAGE row height (the original P6
            // rule) missed this on a table whose rows have very different heights: a short header
            // row's fill rectangle is a much smaller fraction of the table-wide average than of
            // its OWN row, so it slipped under the old 80%-of-average threshold and rendered as a
            // bogus "━━" bar. A schedule bar, by contrast, floats within its row (margin on both
            // top and bottom well past 20% of the row height), so it is untouched by this rule.
            // A single-row table has no header/body distinction to fold away in the first place
            // -- the "row band" IS the whole table, so an ordinary tall bar spanning most of its
            // height (see the multi-table shape-assignment scoring tests, which use exactly this
            // shape) would otherwise be misclassified as furniture too. Require a real multi-row
            // table, matching the header-vs-body scenario this rule targets.
            if (kind == "bar" && table.Rows.Count > 1)
            {
                var rowBandTop = ysTopDown[startRow];
                var rowBandBottom = ysTopDown[endRow + 1];
                var rowBandHeight = rowBandTop - rowBandBottom;
                if (rowBandHeight > 0)
                {
                    var topGap = Math.Abs(geometry.Y + geometry.Height - rowBandTop);
                    var bottomGap = Math.Abs(geometry.Y - rowBandBottom);
                    // Strict `<`, not `<=`: a schedule bar at exactly the spec's own "~60% of row
                    // height, centered" boundary case has a gap of EXACTLY 20% on each side --
                    // that must still read as "floating" (stay a bar), not tip over into furniture.
                    if (topGap < rowBandHeight * 0.2 && bottomGap < rowBandHeight * 0.2)
                    {
                        furniturePathIds.Add(rawPathId);
                        continue;
                    }
                }
            }
            overlays.Add(new PdfTableOverlay(shapeId, "", kind, direction, axis, startRow, endRow, startColumn, endColumn, preset));
        }

        return new Detection(
            overlays.OrderBy(overlay => overlay.StartRow).ThenBy(overlay => overlay.StartColumn)
                .ThenBy(overlay => overlay.ShapeId, OverlayShapeIdComparer.Instance).ToArray(),
            furniturePathIds);
    }

    /// <summary>P8: page-level entry point. Detects overlays for every table on the page at once so
    /// a shape scoring >= 50% intersection against MORE THAN ONE table (see
    /// <see cref="TryScoreOverlay"/> -- e.g. two tables close together, or one nested inside
    /// another's bounds) is credited to the single table it intersects most, never duplicated
    /// across tables. Also applies each table's own P6 furniture fold-back to its
    /// <see cref="PdfTable.SourcePathIds"/>.</summary>
    public static IReadOnlyList<PdfTable> DetectForPage(IReadOnlyList<PdfTable> tables, VisualGraph graph)
    {
        ArgumentNullException.ThrowIfNull(tables); ArgumentNullException.ThrowIfNull(graph);
        var perTable = tables.Select(table => DetectAndFoldFurniture(table, graph)).ToArray();
        if (tables.Count > 1)
        {
            // A ShapeId naming the same candidate in more than one table's overlay list is scored
            // again (by its own resolved geometry, against each competing table's own bounds) and
            // kept only for the table with the greatest TryScoreOverlay score; ties keep the
            // earliest table for determinism.
            var bestTableIndex = new Dictionary<string, (int Index, double Score)>(StringComparer.Ordinal);
            for (var i = 0; i < tables.Count; i++)
                foreach (var overlay in perTable[i].Overlays)
                {
                    var geometry = ResolveShapeGeometry(graph, overlay.ShapeId);
                    if (geometry is null) continue;
                    TryScoreOverlay(geometry, tables[i].Bounds, out var score);
                    if (!bestTableIndex.TryGetValue(overlay.ShapeId, out var current) || score > current.Score)
                        bestTableIndex[overlay.ShapeId] = (i, score);
                }
            for (var i = 0; i < perTable.Length; i++)
                perTable[i] = perTable[i] with
                {
                    Overlays = perTable[i].Overlays
                        .Where(overlay => !bestTableIndex.TryGetValue(overlay.ShapeId, out var best) || best.Index == i)
                        .ToArray()
                };
        }
        return tables.Select((table, i) => table with
        {
            SourcePathIds = perTable[i].FurniturePathIds.Count > 0
                ? table.SourcePathIds.Concat(perTable[i].FurniturePathIds).Distinct(StringComparer.Ordinal).ToArray()
                : table.SourcePathIds,
            Overlays = perTable[i].Overlays,
        }).ToArray();
    }

    /// <summary>P8: resolves whatever <see cref="PdfTableOverlay.ShapeId"/> names -- a promoted
    /// <see cref="VisualNode"/>'s or <see cref="VisualEdge"/>'s own id, or a raw <see cref="VisualPath"/>
    /// id directly -- back to its geometry, so a shape appearing in more than one table's overlay
    /// list can be re-scored against each competing table.</summary>
    internal static Geometry? ResolveShapeGeometry(VisualGraph graph, string shapeId) =>
        (graph.Nodes ?? []).FirstOrDefault(node => node.Id == shapeId)?.Geometry ??
        (graph.Edges ?? []).FirstOrDefault(edge => edge.Id == shapeId)?.Geometry ??
        (graph.Paths ?? []).FirstOrDefault(path => path.Id == shapeId)?.Geometry;

    // Same stable ordering rule PptxAdapter.DetectTableOverlays uses: "(StartRow, StartColumn,
    // ShapeId as a number where possible, non-numeric ids sort last, ordinal otherwise)".
    private sealed class OverlayShapeIdComparer : IComparer<string>
    {
        public static readonly OverlayShapeIdComparer Instance = new();
        public int Compare(string? x, string? y)
        {
            var xIsNumeric = long.TryParse(x, out var xValue);
            var yIsNumeric = long.TryParse(y, out var yValue);
            if (xIsNumeric && yIsNumeric) return xValue.CompareTo(yValue);
            if (xIsNumeric) return -1;
            if (yIsNumeric) return 1;
            return string.CompareOrdinal(x, y);
        }
    }

    /// <summary>Reconstructs column boundaries (ascending, left to right) and row boundaries
    /// (descending, top to bottom -- PDF user space grows upward, so this is "row 0 is the top
    /// row") directly from the inferred table's own cell bounds. PdfTableInference.Infer always
    /// emits exactly one cell per column per row (RowSpan/ColumnSpan are always 1), so the first
    /// row alone carries every column boundary and each row's own cell carries that row's height.</summary>
    private static (double[] Xs, double[] YsTopDown) BuildGrid(PdfTable table)
    {
        var firstRow = table.Rows[0].Cells.OrderBy(cell => cell.Column).ToArray();
        var xs = new double[firstRow.Length + 1];
        xs[0] = firstRow[0].Bounds.X;
        for (var index = 0; index < firstRow.Length; index++)
            xs[index + 1] = firstRow[index].Bounds.X + firstRow[index].Bounds.Width;
        var ys = new double[table.Rows.Count + 1];
        ys[0] = table.Rows[0].Cells[0].Bounds.Y + table.Rows[0].Cells[0].Bounds.Height;
        for (var index = 0; index < table.Rows.Count; index++)
            ys[index + 1] = table.Rows[index].Cells[0].Bounds.Y;
        return (xs, ys);
    }

    // Common geometric candidacy rule (table-overlay-spec-xlsx-docx-pdf.md preamble): at least 50%
    // of the shape's own area/length lies inside the table, and the shape does not itself cover
    // >= 90% of the table's own area (a decorative background frame, not overlay content).
    private static bool TryScoreOverlay(Geometry geometry, Geometry tableBounds, out double score)
    {
        score = 0;
        var tableArea = Math.Max(0, tableBounds.Width) * Math.Max(0, tableBounds.Height);
        if (tableArea <= 0) return false;
        var overlapX = Math.Max(0, Math.Min(geometry.X + geometry.Width, tableBounds.X + tableBounds.Width) - Math.Max(geometry.X, tableBounds.X));
        var overlapY = Math.Max(0, Math.Min(geometry.Y + geometry.Height, tableBounds.Y + tableBounds.Height) - Math.Max(geometry.Y, tableBounds.Y));
        var intersectionArea = overlapX * overlapY;
        if (geometry.Width > 0 && geometry.Height > 0)
        {
            var shapeArea = geometry.Width * geometry.Height;
            if (shapeArea <= 0 || intersectionArea < shapeArea * 0.5) return false;
            if (intersectionArea >= tableArea * 0.9) return false;
            score = intersectionArea;
            return true;
        }
        // Degenerate line: candidacy compares how much of its *length* falls inside the table,
        // gated on its fixed cross-axis coordinate actually falling inside the table's own span.
        var length = Math.Max(geometry.Width, geometry.Height);
        if (length <= 0) return false;
        var tableLeft = tableBounds.X; var tableRight = tableBounds.X + tableBounds.Width;
        var tableBottom = tableBounds.Y; var tableTop = tableBounds.Y + tableBounds.Height;
        var inside = geometry.Height <= geometry.Width
            ? (geometry.Y >= tableBottom && geometry.Y <= tableTop ? overlapX : 0)
            : (geometry.X >= tableLeft && geometry.X <= tableRight ? overlapY : 0);
        if (inside < length * 0.5) return false;
        if (intersectionArea >= tableArea * 0.9) return false;
        score = inside;
        return true;
    }

    private static bool CoincidesWithGridBoundary(Geometry geometry, IReadOnlyList<double> xs, IReadOnlyList<double> ysTopDown)
    {
        if (geometry.Width <= .5 && xs.Any(x => Math.Abs(geometry.X - x) <= 1.5)) return true;
        if (geometry.Height <= .5 && ysTopDown.Any(y => Math.Abs(geometry.Y - y) <= 1.5)) return true;
        return false;
    }

    // Shared row/column resolution (same 50%-of-band-width rule PptxAdapter.ComputeOverlayAxisRange
    // uses): a band is "covered" when the shape's span on that axis overlaps it by at least half
    // the band's own width; with no band covered, fall back to the band containing the shape's own
    // center (clamped to the grid).
    private static (int Start, int End) ComputeAxisRange(double min, double max, IReadOnlyList<double> boundariesAscending)
    {
        var covered = new List<int>();
        for (var index = 0; index < boundariesAscending.Count - 1; index++)
        {
            var bandStart = boundariesAscending[index]; var bandEnd = boundariesAscending[index + 1];
            var bandWidth = bandEnd - bandStart;
            if (bandWidth <= 0) continue;
            var overlap = Math.Max(0, Math.Min(max, bandEnd) - Math.Max(min, bandStart));
            if (overlap >= bandWidth * 0.5) covered.Add(index);
        }
        if (covered.Count > 0) return (covered[0], covered[^1]);
        var clamped = Math.Clamp((min + max) / 2, boundariesAscending[0], boundariesAscending[^1]);
        for (var index = 0; index < boundariesAscending.Count - 1; index++)
            if (clamped >= boundariesAscending[index] && clamped <= boundariesAscending[index + 1]) return (index, index);
        return (0, 0);
    }

    // Rows are stored top-down (descending Y); ComputeAxisRange itself only ever reasons over an
    // ascending boundary list, so reverse to ascending Y first and map the resulting ascending-band
    // index back to a top-down row index (ascending band 0 = bottom-most = the LAST row).
    private static (int Start, int End) ComputeRowRange(double minY, double maxY, IReadOnlyList<double> ysTopDown)
    {
        var rowCount = ysTopDown.Count - 1;
        var ascending = ysTopDown.Reverse().ToArray();
        var (startAscending, endAscending) = ComputeAxisRange(minY, maxY, ascending);
        return (rowCount - 1 - endAscending, rowCount - 1 - startAscending);
    }

    // Spec coverage-based axis rule (shared with PPTX): "covered columns > 1, or covered rows ==
    // 1" reads horizontal; only a multi-row, single-column span reads vertical.
    private static string CoverageAxis(int startRow, int endRow, int startColumn, int endColumn) =>
        endRow > startRow && startColumn == endColumn ? "vertical" : "horizontal";

    private static string ArrowAxisFromDirection(string direction) => direction is "up" or "down" ? "vertical" : "horizontal";

    // Path vector (start -> end) determines the shaft's own axis; PDF user space grows upward, so
    // a positive Y delta reads as "up" (opposite of the PPTX/EMU convention, where Y grows down).
    // ArrowheadEvidence "end" means the arrowhead sits at the path's own end point, so the arrow
    // points forward along that vector; "start" means it points backward (toward the start).
    private static (string Direction, string Axis) ClassifyLineDirection(IReadOnlyList<VisualPathPoint>? points, bool atEnd)
    {
        if (points is not { Count: >= 2 }) return ("none", "horizontal");
        var start = points[0]; var end = points[^1];
        var dx = end.X - start.X; var dy = end.Y - start.Y;
        var axis = Math.Abs(dx) >= Math.Abs(dy) ? "horizontal" : "vertical";
        var forward = axis == "horizontal" ? (dx >= 0 ? "right" : "left") : (dy >= 0 ? "up" : "down");
        return (atEnd ? forward : Opposite(forward), axis);
    }

    private static string Opposite(string direction) => direction switch
    {
        "right" => "left", "left" => "right", "up" => "down", "down" => "up", _ => direction,
    };

    // Shape classification from raw vertices (table-overlay-spec-xlsx-docx-pdf.md section 3): a
    // closed subpath's Points always repeats its first point as the last (PDF `h`/closepath), so
    // that duplicate is dropped before counting vertices.
    private static (string Kind, string Direction, string? Preset) ClassifyByPoints(IReadOnlyList<VisualPathPoint>? points)
    {
        if (points is not { Count: >= 3 }) return ("bar", "none", null);
        var vertices = points.Count > 1 && points[0] == points[^1] ? points.Take(points.Count - 1).ToArray() : [.. points];
        switch (vertices.Length)
        {
            case 3: return ("marker", "none", "triangle");
            case 4:
            {
                // P9 fix: cluster within a small tolerance rather than an exact `Distinct()` --
                // four corners hand-authored or re-emitted by a PDF producer routinely carry a
                // sub-point rounding jitter (e.g. 160.00 vs 160.01) between what are meant to be
                // the SAME shared X or Y coordinate, which `Distinct()` counted as 4 separate
                // values instead of 2 and misclassified an ordinary axis-aligned rectangle as a
                // diamond marker.
                var distinctXs = CountDistinctClustered(vertices.Select(vertex => vertex.X));
                var distinctYs = CountDistinctClustered(vertices.Select(vertex => vertex.Y));
                // An axis-aligned rectangle has exactly 2 distinct X's and 2 distinct Y's among
                // its 4 corners; anything else (a 45-degree diamond has 3 of each) is a marker.
                return distinctXs == 2 && distinctYs == 2 ? ("bar", "none", null) : ("marker", "none", "diamond");
            }
            default:
                if (vertices.Length >= 5 && TryDetectArrow(vertices, out var direction, out var axis))
                    return ("arrow", direction, null);
                return ("bar", "none", null);
        }
    }

    // P9: clusters values that are within a small absolute tolerance of each other instead of
    // relying on exact double equality -- see ClassifyByPoints' case-4 branch.
    private static int CountDistinctClustered(IEnumerable<double> values, double tolerance = 0.5)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        var count = 0; double? previous = null;
        foreach (var value in sorted)
        {
            if (previous is null || value - previous.Value > tolerance) count++;
            previous = value;
        }
        return count;
    }

    // "A single extreme vertex on one side and >= 2 on the opposite side" points the arrow toward
    // the lone vertex (its tip); "one on each opposing side" (both ends taper to a single point,
    // as in a double-headed arrow) reads as "both" -- but ONLY when the shape also has a genuine
    // flared shaft (P10 fix, see below). Checked on whichever axis actually shows this taper
    // signature; a shape's non-arrow axis (e.g. a horizontal arrow's Y extent) never shows it,
    // since several vertices always share both the top and bottom edge there.
    private static bool TryDetectArrow(IReadOnlyList<VisualPathPoint> vertices, out string direction, out string axis)
    {
        direction = "none"; axis = "horizontal";
        var minX = vertices.Min(vertex => vertex.X); var maxX = vertices.Max(vertex => vertex.X);
        var minY = vertices.Min(vertex => vertex.Y); var maxY = vertices.Max(vertex => vertex.Y);
        var tolX = Math.Max(1e-6, (maxX - minX) * .02);
        var tolY = Math.Max(1e-6, (maxY - minY) * .02);
        var atMinX = vertices.Count(vertex => vertex.X <= minX + tolX);
        var atMaxX = vertices.Count(vertex => vertex.X >= maxX - tolX);
        var atMinY = vertices.Count(vertex => vertex.Y <= minY + tolY);
        var atMaxY = vertices.Count(vertex => vertex.Y >= maxY - tolY);
        // P10 fix: a single vertex tapering EACH horizontal extreme (atMinX==1 && atMaxX==1)
        // describes any simple pointed-both-ends polygon -- an ordinary hexagon or pentagon tapers
        // exactly this way too, with no separate arrowhead "wings" at all -- so this is a genuine
        // double-headed arrow only when the shape ALSO has a flared shaft: >= 2 vertices on BOTH
        // the opposite (perpendicular) extremes, and at least 7 vertices overall (2 tips plus at
        // least 2 wing/shaft corners per side). A hexagon/pentagon falls through to "bar" via
        // neither oneSidedTaper nor doubleTipTaper being satisfied, exactly like any other polygon
        // whose taper signature does not otherwise match a single-headed arrow.
        var oneSidedTaperX = atMinX == 1 && atMaxX >= 2 || atMaxX == 1 && atMinX >= 2;
        var doubleTipTaperX = atMinX == 1 && atMaxX == 1 && atMinY >= 2 && atMaxY >= 2 && vertices.Count >= 7;
        var horizontalTaper = oneSidedTaperX || doubleTipTaperX;
        var oneSidedTaperY = atMinY == 1 && atMaxY >= 2 || atMaxY == 1 && atMinY >= 2;
        var doubleTipTaperY = atMinY == 1 && atMaxY == 1 && atMinX >= 2 && atMaxX >= 2 && vertices.Count >= 7;
        var verticalTaper = oneSidedTaperY || doubleTipTaperY;
        if (horizontalTaper && (!verticalTaper || maxX - minX >= maxY - minY))
        {
            axis = "horizontal";
            direction = doubleTipTaperX ? "both" : atMaxX == 1 ? "right" : "left";
            return true;
        }
        if (verticalTaper)
        {
            axis = "vertical";
            // PDF user space grows upward: the vertex at the MAX Y is the visually TOP tip.
            direction = doubleTipTaperY ? "both" : atMaxY == 1 ? "up" : "down";
            return true;
        }
        return false;
    }
}
