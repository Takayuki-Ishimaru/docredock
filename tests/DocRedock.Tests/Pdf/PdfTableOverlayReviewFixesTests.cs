using System.Text;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;

namespace DocRedock.Tests.Pdf;

/// <summary>
/// Regression coverage for the opus review of the "PDF table overlay" feature
/// (review-fixes-pdf.md, 2026-09-10): P1-P10. Guiding principle throughout: a PDF with no
/// overlay content must reconstruct exactly as it did before P-Overlay existed -- several tests
/// below therefore assert a specific row/column count or grid shape derived by reasoning about
/// the PRE-P-Overlay code path (see each test's own comment), not merely "whatever the current
/// code happens to produce".
/// </summary>
public sealed class PdfTableOverlayReviewFixesTests
{
    private static byte[] Page(string content) => Encoding.Latin1.GetBytes(
        "%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length " +
        content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        " >> stream\n" + content + "\nendstream\n%%EOF");

    private static string Line(int x, int y, string text) => $"BT 1 0 0 1 {x} {y} Tm ({text}) Tj ET";

    // Shared main-table geometry, same shape as PdfTableOverlayTests' own MainTable (60,60,4,5,
    // 100,40 => table spans X:[60,560] Y:[60,220]) but with a text label in EVERY cell (rather
    // than column 0 only), since several tests below place overlay content over arbitrary cells.
    private const int Left = 60, Bottom = 60, Rows = 4, Columns = 5, CellWidth = 100, CellHeight = 40;

    private static IEnumerable<string> FullyLabelledRuledTable(int left = Left, int bottom = Bottom,
        int rows = Rows, int columns = Columns, int cellWidth = CellWidth, int cellHeight = CellHeight)
    {
        for (var row = 0; row <= rows; row++)
            yield return $"{left} {bottom + row * cellHeight} m {left + columns * cellWidth} {bottom + row * cellHeight} l S";
        for (var column = 0; column <= columns; column++)
            yield return $"{left + column * cellWidth} {bottom} m {left + column * cellWidth} {bottom + rows * cellHeight} l S";
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
            yield return Line(left + column * cellWidth + 10, bottom + row * cellHeight + 10, $"R{row}C{column}");
    }

    // Same table drawn with the outer boundary as a single `re S` rectangle and only the INSIDE
    // dividers as plain `m`/`l` rules -- an everyday, non-overlay authoring style (P3).
    private static IEnumerable<string> FrameAndPlainRuledTable(int left = Left, int bottom = Bottom,
        int rows = Rows, int columns = Columns, int cellWidth = CellWidth, int cellHeight = CellHeight)
    {
        yield return $"{left} {bottom} {columns * cellWidth} {rows * cellHeight} re S";
        for (var column = 1; column < columns; column++)
            yield return $"{left + column * cellWidth} {bottom} m {left + column * cellWidth} {bottom + rows * cellHeight} l S";
        for (var row = 1; row < rows; row++)
            yield return $"{left} {bottom + row * cellHeight} m {left + columns * cellWidth} {bottom + row * cellHeight} l S";
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
            yield return Line(left + column * cellWidth + 10, bottom + row * cellHeight + 10, $"R{row}C{column}");
    }

    private const string ThinBar = "370 85 180 12 re f"; // row3(output0)... see PdfTableOverlayTests.Bar

    private static PdfTable SingleTable(PdfExtractionResult result, int rows = Rows, int columns = Columns)
    {
        var table = Assert.Single(result.Tables![1]);
        Assert.Equal(rows, table.Rows.Count);
        Assert.All(table.Rows, row => Assert.Equal(columns, row.Cells.Count));
        return table;
    }

    // ============================================================================================
    // P1 -- RemoveConsumedOverlayVisuals must key off the source-item LEDGER, never a Geometry
    // reverse lookup that keeps only the first path for a duplicated rectangle.
    // ============================================================================================
    [Fact]
    public void Rectangle_painted_twice_at_identical_coordinates_keeps_source_accounting_consistent()
    {
        // The same bar rectangle drawn twice at the IDENTICAL coordinates: once filled, once
        // stroked (a common "filled + outlined" authoring pattern). Before the P1 fix, the
        // Geometry-keyed reverse lookup inside RemoveConsumedOverlayVisuals could point the
        // ledger update at the wrong one of the two paths, leaving the other's SourceItem still
        // claiming a ProjectedNodeId/ProjectedEdgeId that no longer exists once the overlay's own
        // node/edge is removed -- VisualSourceItemReferenceInvalid, IsConsistent=false,
        // PdfExtractionException thrown by PdfDiagnosticInvariantValidator.
        var content = FullyLabelledRuledTable().Concat([ThinBar, "370 85 180 12 re S"]);

        var result = PdfTextExtractor.Extract(Page(string.Join("\n", content)));

        var table = SingleTable(result);
        var graph = result.VisualGraphs![1];
        Assert.True(graph.SourceAccounting.IsConsistent,
            $"Unaccounted={graph.SourceAccounting.Unaccounted}, InvalidReferences={graph.SourceAccounting.InvalidReferences}");
        Assert.Equal(0, graph.SourceAccounting.InvalidReferences);
        Assert.NotNull(table.Overlays);
    }

    // ============================================================================================
    // P2 -- removedUnresolvedEdgeCount must count every edge actually removed (including one
    // dropped only because ITS endpoint node was removed), not merely edges named directly by
    // overlayShapeIds.
    // ============================================================================================
    [Fact]
    public void Removing_an_overlay_node_also_trims_the_diagnostic_for_an_edge_that_only_touches_it()
    {
        // A promoted "bar" VisualNode (the overlay) plus an edge with ONE endpoint resolved to
        // that node and the other end dangling (SourceId="bar1", TargetId=null): a classic
        // "half-connected, unresolved" schedule-overlay artifact. RemoveConsumedOverlayVisuals
        // removes this edge via the *node* filter (its SourceId names a removed node), never via
        // `edgesToRemove` (its own Id is never named by overlayShapeIds) -- the pre-fix code only
        // counted edgesToRemove, so the edge's own stale "VisualConnectorUnresolved" diagnostic
        // was never trimmed, desynchronizing it from the (now empty) live edge population: INV-03.
        var barGeometry = new Geometry("pdf-user-space", 100, 100, 50, 20);
        var barPoints = new[]
        {
            new VisualPathPoint(100, 100), new VisualPathPoint(150, 100), new VisualPathPoint(150, 120),
            new VisualPathPoint(100, 120), new VisualPathPoint(100, 100),
        };
        var barPath = new VisualPath("bar-path", barPoints, barGeometry);
        var barNode = new VisualNode("bar1", "Shape 1", Geometry: barGeometry);
        var danglingPoints = new[] { new VisualPathPoint(125, 110), new VisualPathPoint(300, 300) };
        var danglingPath = new VisualPath("dangling-path", danglingPoints,
            new Geometry("pdf-user-space", 125, 110, 175, 190));
        var danglingEdge = new VisualEdge("dangling-edge", "bar1", null, Path: danglingPoints,
            EdgeDirection: VisualEdgeDirection.Undirected);
        var sourceItems = new List<VisualSourceItem>
        {
            new("bar-path", VisualSourceItemKind.Shape, VisualDisposition.ProjectedNode, ProjectedNodeId: "bar1"),
            new("dangling-path", VisualSourceItemKind.Connector, VisualDisposition.VisualFallback,
                FallbackPathId: "dangling-path", Reason: "ambiguous label assignment; vector box retained as fallback"),
        };
        var diagnostics = new List<VisualDiagnostic> { new("VisualConnectorUnresolved", "Edge endpoint is ambiguous.") };
        var graph = new VisualGraph("g", [barNode], [danglingEdge], diagnostics,
            Paths: [barPath, danglingPath], SourceItems: sourceItems);

        var result = PdfVisualOutputCompactor.RemoveConsumedOverlayVisuals(graph, ["bar1"]);

        Assert.Empty(result.Nodes);
        Assert.Empty(result.Edges);
        Assert.True(result.SourceAccounting.IsConsistent,
            $"Unaccounted={result.SourceAccounting.Unaccounted}, InvalidReferences={result.SourceAccounting.InvalidReferences}");
        var issues = PdfDiagnosticInvariantValidator.Validate(result, (result.Diagnostics ?? []).Select(d => d.ToWarning()));
        Assert.Empty(issues);
    }

    // ============================================================================================
    // P3 -- rectangle-sourced ruling-line dedup must be per-coordinate, not "drop the whole
    // component". Pre-feature code (git diff shows `lines = (graph.Paths ??
    // []).SelectMany(AxisLine.CreateAll).ToArray();` with NO rectangle special-casing at all) put
    // every rectangle-sourced segment through Cluster()/Regular() exactly like a plain line, so a
    // `re`-framed table with plain inner dividers reconstructed with its full, correct row/column
    // count -- these two tests reproduce that same table shape.
    // ============================================================================================
    [Fact]
    public void Re_framed_table_with_plain_inner_rules_keeps_its_full_row_and_column_count()
    {
        var result = PdfTextExtractor.Extract(Page(string.Join("\n", FrameAndPlainRuledTable())));

        SingleTable(result); // Rows=4, Columns=5 -- same shape a pre-feature build would infer.
        Assert.True(result.VisualGraphs![1].SourceAccounting.IsConsistent);
    }

    [Fact]
    public void Re_framed_table_with_a_bar_overlay_keeps_the_same_grid_and_reports_the_bar()
    {
        var content = FrameAndPlainRuledTable().Concat([ThinBar]);

        var result = PdfTextExtractor.Extract(Page(string.Join("\n", content)));

        var table = SingleTable(result); // same Rows=4, Columns=5 grid as the no-overlay case above.
        var overlay = Assert.Single(table.Overlays!);
        Assert.Equal("bar", overlay.Kind);
        Assert.True(result.VisualGraphs![1].SourceAccounting.IsConsistent);
    }

    // ============================================================================================
    // P4 -- FindArrowShaftMatches needs an absolute marker-size cap; the old purely
    // length-relative check treated any small shape near a LONG ruling line's own endpoint as a
    // plausible arrowhead.
    // ============================================================================================
    [Fact]
    public void A_filled_legend_box_near_a_ruling_line_endpoint_does_not_erase_that_ruling_line()
    {
        // 40x40 legend box (diagonal ~56.6pt, comfortably over the new 15pt absolute cap) sitting
        // just outside the table's bottom-left corner, close enough to the 500pt-long bottom rule's
        // own endpoint (60,60) that the old length-relative-only check (markerSize <= shaftLength *
        // .30) wrongly paired it with that rule and erased it from grid candidacy.
        const string legendBox = "10 10 40 40 re f";
        var content = FullyLabelledRuledTable().Concat([legendBox]);

        var result = PdfTextExtractor.Extract(Page(string.Join("\n", content)));

        SingleTable(result); // Rows=4, Columns=5: the bottom rule must survive.
    }

    // ============================================================================================
    // P5 -- SuppressTableGridEdges's "exclude every provisional (unlabelled) node from the
    // touches-a-real-diagram veto" was too broad. Pre-feature code (`var semanticNodes =
    // nodes.Where(node => node.Geometry is not null).ToArray();`, no provisional exclusion at
    // all) let ANY node with geometry -- labelled or not -- veto suppression, so an ordinary
    // architecture diagram of unlabelled boxes connected by lines was never mistaken for a table
    // grid. This reproduces that same "flow, not table" outcome for a textless box diagram.
    // ============================================================================================
    [Fact]
    public void Unlabelled_box_diagram_with_a_dense_connector_lattice_is_not_suppressed_as_a_table_grid()
    {
        // Three horizontal + three vertical FULL-SPAN connector lines (100% crossing density,
        // regular 60pt spacing) meeting SuppressTableGridEdges's own "looks like a candidate
        // grid" bar, with an 80x80 unlabelled box centered on each of the 9 intersections. Each
        // box is larger than the grid's own 60pt cell short side (and not fully contained in the
        // grid's own bbox either), so under the P5 fix it counts as a genuine semantic node and
        // vetoes suppression -- exactly as the pre-feature code (which never special-cased
        // provisional nodes at all) always did for ANY node with geometry.
        const int gridLeft = 130, gridStep = 60; // vertical/horizontal connector coordinates: 130,190,250
        var coordinates = new[] { gridLeft, gridLeft + gridStep, gridLeft + 2 * gridStep };
        var content = new List<string>();
        foreach (var y in coordinates) content.Add($"{coordinates[0]} {y} m {coordinates[^1]} {y} l S");
        foreach (var x in coordinates) content.Add($"{x} {coordinates[0]} m {x} {coordinates[^1]} l S");
        foreach (var x in coordinates)
        foreach (var y in coordinates)
            content.Add($"{x - 40} {y - 40} 80 80 re S");

        var result = PdfTextExtractor.Extract(Page(string.Join("\n", content)));

        Assert.Null(result.Tables?.GetValueOrDefault(1));
        var graph = result.VisualGraphs![1];
        Assert.DoesNotContain(graph.SourceItems ?? [], item =>
            item.Reason?.Contains("table/grid", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(result.Diagnostics!, message => message.StartsWith("VisualConnectorUnresolved", StringComparison.Ordinal));
    }

    // ============================================================================================
    // P6 -- a filled rectangle that covers (>=80% of) a whole row's height is a cell fill/header
    // background, not a slim status bar; it must fold into table furniture (SourcePathIds),
    // never surface as a "bar" overlay stamped into every covered cell.
    // ============================================================================================
    [Fact]
    public void Full_height_row_fill_becomes_furniture_while_a_thin_bar_in_the_same_table_stays_a_bar()
    {
        // Header-row (output row 0, raw row3, Y:[180,220]) background fill spanning the table's
        // FULL width and the row's FULL height (40pt = 100% of the row) -- the exact shape
        // reportlab's own TableStyle("BACKGROUND", (0,0), (-1,0), ...) draws, and precisely what
        // complex-layout.pdf's own KPI/approval tables use for header/zebra-striped rows.
        const string headerFill = "60 180 500 40 re f";
        var content = FullyLabelledRuledTable().Concat([headerFill, ThinBar]);

        var result = PdfTextExtractor.Extract(Page(string.Join("\n", content)));

        var table = SingleTable(result);
        var overlay = Assert.Single(table.Overlays!);
        Assert.Equal("bar", overlay.Kind);
        Assert.Equal(3, overlay.StartRow); // the thin bar's own row (see PdfTableOverlayTests.Bar).

        var fillPath = result.VisualGraphs![1].Paths!.Single(path =>
            path.Geometry == new Geometry("pdf-user-space", 60, 180, 500, 40));
        Assert.Contains(fillPath.Id, table.SourcePathIds);
        Assert.True(result.VisualGraphs![1].SourceAccounting.IsConsistent);
    }

    // ============================================================================================
    // P6b -- furniture must be judged against the COVERED ROW BAND's own height, not a single
    // table-wide average row height: a short header row's near-full fill must fold into furniture
    // even when the table's OTHER rows are much taller (the bug complex-layout.pdf's own
    // "入力|抽出|正規化|出力" table hit -- its ~12pt header row was a much smaller fraction of the
    // table-WIDE average than of its own row height, slipping under the original P6 rule's
    // 80%-of-average threshold), while an ordinary bar comfortably floating within a row (a gap of
    // exactly 20% of THAT row's own height on both top and bottom -- the spec's own boundary case
    // for a schedule bar at ~60% height) must still surface as "bar".
    // ============================================================================================
    [Fact]
    public void Header_row_fill_on_a_short_header_row_becomes_furniture_while_a_floating_bar_in_a_much_taller_body_row_stays_a_bar()
    {
        // Row heights mirror complex-layout.pdf's own 入力/抽出/正規化/出力 table almost exactly
        // (header ~12pt, average ~18pt over a header+body pair -> body ~24pt): a 2x header/body
        // ratio, safely inside PdfTableInference.Regular()'s +-2.5x tolerance for row spacing (a
        // MUCH more different pair of heights is rejected outright as "not a real grid", never
        // reaching PdfTableOverlayDetector at all) while still reproducing the exact bug -- 12pt
        // is only 12/18=0.67 of the OLD rule's table-wide average, under its 80% threshold.
        const int left = 0, columns = 3, cellWidth = 100;
        const int bodyBottom = 0, bodyHeight = 25, headerHeight = 12;
        const int headerBottom = bodyBottom + bodyHeight; // 25
        const int headerTop = headerBottom + headerHeight; // 37

        var content = new List<string>
        {
            $"{left} {bodyBottom} m {left + columns * cellWidth} {bodyBottom} l S",
            $"{left} {headerBottom} m {left + columns * cellWidth} {headerBottom} l S",
            $"{left} {headerTop} m {left + columns * cellWidth} {headerTop} l S",
        };
        for (var column = 0; column <= columns; column++)
            content.Add($"{left + column * cellWidth} {bodyBottom} m {left + column * cellWidth} {headerTop} l S");
        for (var column = 0; column < columns; column++)
        {
            content.Add(Line(left + column * cellWidth + 10, headerBottom + 3, $"H{column}"));
            content.Add(Line(left + column * cellWidth + 10, bodyBottom + 10, $"B{column}"));
        }
        // Header-row background fill: full width, exactly the header row's own height (12pt) --
        // a 0% gap against its own 12pt row band, comfortably furniture regardless of the taller
        // (25pt) body row below it.
        content.Add($"{left} {headerBottom} {columns * cellWidth} {headerHeight} re f");
        // Body-row bar: 60% of the body row's own height (15 of 25), centered -- an exactly-20%
        // gap on both sides (the spec's own boundary case), which must still read as "floating"
        // (stay a bar) under the strict `<` comparison.
        content.Add($"{left + cellWidth + 20} 5 60 15 re f");

        var result = PdfTextExtractor.Extract(Page(string.Join("\n", content)));

        var table = SingleTable(result, rows: 2, columns: columns);
        var overlay = Assert.Single(table.Overlays!);
        Assert.Equal("bar", overlay.Kind);
        Assert.Equal(1, overlay.StartRow); // the body row -- output row 1 (header is output row 0).

        var headerFillPath = result.VisualGraphs![1].Paths!.Single(path =>
            path.Geometry == new Geometry("pdf-user-space", left, headerBottom, columns * cellWidth, headerHeight));
        Assert.Contains(headerFillPath.Id, table.SourcePathIds);
    }

    // ============================================================================================
    // P7 -- IsGridAlignedFurniture must gate on actually intersecting the CANDIDATE table's own
    // bounds, not merely on its coordinates numerically matching that table's xs/ys.
    // ============================================================================================
    [Fact]
    public void Two_tables_stacked_far_apart_never_share_furniture_path_ids()
    {
        // Two independent, far-apart 3x3 tables sharing the same column positions; the LOWER
        // table has its own header-row fill (grid-aligned furniture for ITS OWN table only).
        // Distinct crossing-connected components keep them from interfering with each other
        // (SeparateGridComponents / recursive Infer), and the P7 bounds gate is a defense in
        // depth against a rectangle's coordinates numerically -- but not spatially -- coinciding
        // with an unrelated table's own xs/ys.
        var upper = new List<string>();
        for (var row = 0; row <= 3; row++) upper.Add($"0 {row * 60} m 180 {row * 60} l S");
        for (var col = 0; col <= 3; col++) upper.Add($"{col * 60} 0 m {col * 60} 180 l S");
        for (var row = 0; row < 3; row++)
        for (var col = 0; col < 3; col++)
            upper.Add(Line(col * 60 + 10, row * 60 + 10, $"U{row}{col}"));

        const int lowerBottom = -1000;
        var lower = new List<string> { $"0 {lowerBottom} 180 60 re f" }; // header-row fill, lower table only.
        for (var row = 0; row <= 3; row++) lower.Add($"0 {lowerBottom + row * 60} m 180 {lowerBottom + row * 60} l S");
        for (var col = 0; col <= 3; col++) lower.Add($"{col * 60} {lowerBottom} m {col * 60} {lowerBottom + 180} l S");
        for (var row = 0; row < 3; row++)
        for (var col = 0; col < 3; col++)
            lower.Add(Line(col * 60 + 10, lowerBottom + row * 60 + 10, $"L{row}{col}"));

        var result = PdfTextExtractor.Extract(Page(string.Join("\n", upper.Concat(lower))));

        Assert.Equal(2, result.Tables![1].Count);
        var upperTable = result.Tables![1].Single(table => table.Bounds.Y >= 0);
        var lowerTable = result.Tables![1].Single(table => table.Bounds.Y < 0);
        var lowerFillPath = result.VisualGraphs![1].Paths!.Single(path =>
            path.Geometry == new Geometry("pdf-user-space", 0, lowerBottom, 180, 60));
        Assert.DoesNotContain(lowerFillPath.Id, upperTable.SourcePathIds);
        Assert.Contains(lowerFillPath.Id, lowerTable.SourcePathIds);
    }

    // ============================================================================================
    // P8 -- a shape scoring >=50% intersection against MORE THAN ONE table must be credited to
    // the single table it intersects most, never duplicated.
    // ============================================================================================
    [Fact]
    public void A_shape_scoring_high_against_two_overlapping_tables_is_assigned_to_the_higher_scoring_one_only()
    {
        var table1 = new PdfTable("t1", 1, new Geometry("pdf-user-space", 0, 0, 100, 100),
            [new PdfTableRow([new PdfTableCell(0, 0, 1, 1, new Geometry("pdf-user-space", 0, 0, 100, 100), "", [])])],
            PdfTableConfidence.HighConfidenceInferred, []);
        var table2 = new PdfTable("t2", 1, new Geometry("pdf-user-space", 50, 0, 100, 100),
            [new PdfTableRow([new PdfTableCell(0, 0, 1, 1, new Geometry("pdf-user-space", 50, 0, 100, 100), "", [])])],
            PdfTableConfidence.HighConfidenceInferred, []);
        // A 50x80 rectangle spanning X:[40,90]: 100% inside table1 (score 4000) but only 80%
        // inside table2's own X:[50,150] span (score 3200) -- table1 must keep it, table2 must not.
        var shapePoints = new[]
        {
            new VisualPathPoint(40, 10), new VisualPathPoint(90, 10), new VisualPathPoint(90, 90),
            new VisualPathPoint(40, 90), new VisualPathPoint(40, 10),
        };
        var graph = new VisualGraph("g", [], [],
            Paths: [new VisualPath("shape1", shapePoints, new Geometry("pdf-user-space", 40, 10, 50, 80))]);

        var tables = PdfTableOverlayDetector.DetectForPage([table1, table2], graph);

        Assert.Single(tables[0].Overlays!);
        Assert.Empty(tables[1].Overlays!);
    }

    // ============================================================================================
    // P9 -- ClassifyByPoints' 4-vertex distinct-X/Y test must cluster within a small tolerance,
    // not rely on exact double equality.
    // ============================================================================================
    [Fact]
    public void Four_point_rectangle_with_sub_point_coordinate_jitter_is_still_classified_as_a_bar()
    {
        var table = new PdfTable("t", 1, new Geometry("pdf-user-space", 0, 0, 300, 200),
            [new PdfTableRow([new PdfTableCell(0, 0, 1, 1, new Geometry("pdf-user-space", 0, 0, 300, 200), "", [])])],
            PdfTableConfidence.HighConfidenceInferred, []);
        // Four `l`-drawn corners meant to be an axis-aligned rectangle, but each carries a 0.01pt
        // rounding jitter relative to its true shared X or Y -- routine PDF-producer noise. The
        // un-clustered `Distinct()` this replaces would have counted 4 distinct X's and 4 distinct
        // Y's (instead of 2 and 2) and misclassified this as a diamond marker.
        var jitteredRectangle = new[]
        {
            new VisualPathPoint(60.00, 60.00), new VisualPathPoint(160.01, 60.00),
            new VisualPathPoint(160.00, 100.01), new VisualPathPoint(59.99, 100.00),
            new VisualPathPoint(60.00, 60.00),
        };
        var graph = new VisualGraph("g", [], [],
            Paths: [new VisualPath("rect1", jitteredRectangle, new Geometry("pdf-user-space", 59.99, 60, 100.02, 40.01))]);

        var overlay = Assert.Single(PdfTableOverlayDetector.Detect(table, graph));

        Assert.Equal("bar", overlay.Kind);
    }

    // ============================================================================================
    // P10 -- "both" (double-headed arrow) direction requires a genuine flared shaft (>=2 vertices
    // on each perpendicular extreme, 7+ vertices total), not merely one tapered vertex on each
    // horizontal/vertical extreme -- a plain hexagon or pentagon tapers that way too.
    // ============================================================================================
    [Fact]
    public void Hexagon_is_classified_as_a_bar_not_a_double_headed_arrow()
    {
        var table = new PdfTable("t", 1, new Geometry("pdf-user-space", 0, 0, 200, 200),
            [new PdfTableRow([new PdfTableCell(0, 0, 1, 1, new Geometry("pdf-user-space", 0, 0, 200, 200), "", [])])],
            PdfTableConfidence.HighConfidenceInferred, []);
        // A regular hexagon: single-vertex tips at the left/right extremes (satisfying the old,
        // buggy atMinX==1 && atMaxX==1 "both" rule) but only 6 vertices total and no separate
        // arrowhead "wings" -- never a genuine double-headed arrow.
        var hexagon = new[]
        {
            new VisualPathPoint(0, 50), new VisualPathPoint(20, 90), new VisualPathPoint(80, 90),
            new VisualPathPoint(100, 50), new VisualPathPoint(80, 10), new VisualPathPoint(20, 10),
            new VisualPathPoint(0, 50),
        };
        var graph = new VisualGraph("g", [], [],
            Paths: [new VisualPath("hex1", hexagon, new Geometry("pdf-user-space", 0, 10, 100, 80))]);

        var overlay = Assert.Single(PdfTableOverlayDetector.Detect(table, graph));

        Assert.Equal("bar", overlay.Kind);
    }

    [Fact]
    public void Nine_point_left_right_arrow_shape_is_classified_as_a_both_direction_arrow()
    {
        var table = new PdfTable("t", 1, new Geometry("pdf-user-space", 0, 0, 200, 200),
            [new PdfTableRow([new PdfTableCell(0, 0, 1, 1, new Geometry("pdf-user-space", 0, 0, 200, 200), "", [])])],
            PdfTableConfidence.HighConfidenceInferred, []);
        // A genuine double-headed arrow: single-vertex tips at the left/right extremes, PLUS a
        // flared shaft with 2 vertices at each of the top (Y=70) and bottom (Y=30) extremes, 9
        // vertices in total -- matches the "leftRightArrow" preset family the spec names.
        var leftRightArrow = new[]
        {
            new VisualPathPoint(0, 50), new VisualPathPoint(15, 70), new VisualPathPoint(15, 60),
            new VisualPathPoint(85, 60), new VisualPathPoint(85, 70), new VisualPathPoint(100, 50),
            new VisualPathPoint(85, 30), new VisualPathPoint(15, 30), new VisualPathPoint(15, 40),
            new VisualPathPoint(0, 50),
        };
        var graph = new VisualGraph("g", [], [],
            Paths: [new VisualPath("arrow1", leftRightArrow, new Geometry("pdf-user-space", 0, 30, 100, 40))]);

        var overlay = Assert.Single(PdfTableOverlayDetector.Detect(table, graph));

        Assert.Equal("arrow", overlay.Kind);
        Assert.Equal("both", overlay.Direction);
        Assert.Equal("horizontal", overlay.Axis);
    }
}
