using System.Text;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;

namespace DocRedock.Tests.Pdf;

/// <summary>
/// P-Overlay: PDF port of the "table overlay" feature (table-overlay-spec-xlsx-docx-pdf.md
/// section 3). Covers the Infer relaxation (a directed edge mostly INSIDE a table no longer
/// disqualifies it), the grid-candidate hygiene needed to keep an overlay shape from polluting
/// AxisLine clustering, and PdfTableOverlayDetector's shape classification.
/// </summary>
public sealed class PdfTableOverlayTests
{
    // Same raw-content-stream style as PdfMultiTableGridSuppressionTests: a page object plus a
    // stream carrying literal PDF operators, decoded as Latin-1 so hand-written coordinates and
    // ASCII cell text round-trip byte for byte.
    private static byte[] Page(string content) => Encoding.Latin1.GetBytes(
        "%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length " +
        content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        " >> stream\n" + content + "\nendstream\n%%EOF");

    private static string Line(int x, int y, string text) => $"BT 1 0 0 1 {x} {y} Tm ({text}) Tj ET";

    // A ruled left=60,bottom=60, 4 rows x 5 columns grid (cell 100x40, so the table spans
    // X:[60,560] Y:[60,220]). Unlike PdfMultiTableGridSuppressionTests.RuledTable, text sits ONLY
    // in column 0 (one label per row) -- every other cell is deliberately textless, exactly like a
    // real schedule table's date columns, so an overlay shape placed over a date column never
    // competes with a same-row/same-column text fragment for a "nearest label" match (PdfTextExtractor's
    // own AddClosedNode would otherwise flag two equally-plausible neighbours as ambiguous and
    // retain the shape as an unlabelled fallback path instead of the labelled node some of these
    // tests exercise on purpose).
    private static IEnumerable<string> RuledTableWithColumnZeroLabels(int left, int bottom, int rows, int columns, int cellWidth, int cellHeight)
    {
        for (var row = 0; row <= rows; row++)
            yield return $"{left} {bottom + row * cellHeight} m {left + columns * cellWidth} {bottom + row * cellHeight} l S";
        for (var column = 0; column <= columns; column++)
            yield return $"{left + column * cellWidth} {bottom} m {left + column * cellWidth} {bottom + rows * cellHeight} l S";
        for (var row = 0; row < rows; row++)
            yield return Line(left + 10, bottom + row * cellHeight + 10, $"ROW{row}");
    }

    // ------------------------------------------------------------------------------------------
    // Shared main-table geometry (see class comment above): row/column raw-index -> output-index
    // mapping used throughout this file: raw row 0 (bottom-most, Y:[60,100]) is OUTPUT row 3 (PDF
    // user space grows upward and PdfTableInference.Infer numbers row 0 as the visual top); output
    // column equals raw column (both left-to-right, no flip needed).
    // ------------------------------------------------------------------------------------------
    private const int Left = 60, Bottom = 60, Rows = 4, Columns = 5, CellWidth = 100, CellHeight = 40;

    // Right arrow over output row 1 (raw row 2, Y:[140,180]) columns 1-2 (X:[160,360]). Placed in
    // the upper part of the row band (Y:[165,177]) so it never spatially reaches the row-0-column
    // label text (there is none in columns 1-4 anyway -- see RuledTableWithColumnZeroLabels).
    private const string RightArrow = "170 167 m 170 175 l 330 175 l 330 177 l 350 171 l 330 165 l 330 167 l h f";

    // Left arrow (a right arrow's own points mirrored about the same bbox's center-X) over output
    // row 2 (raw row 1, Y:[100,140]) columns 1-2.
    private const string LeftArrow = "350 127 m 350 135 l 190 135 l 190 137 l 170 131 l 190 125 l 190 127 l h f";

    // Filled bar over output row 3 (raw row 0, Y:[60,100]) columns 3-4 (X:[360,560]).
    private const string Bar = "370 85 180 12 re f";

    // Diamond marker over output row 0 (raw row 3, Y:[180,220]) column 4 (X:[460,560]).
    private const string Diamond = "510 218 m 518 212 l 510 206 l 502 212 l h f";

    // Vertical down-arrow: a full-height shaft (Y:[60,220], the table's own Y extent) at column
    // 0's center (X=110, not a column boundary), with a small filled triangle at its bottom end.
    private const string VerticalShaft = "110 220 m 110 60 l S";
    private const string VerticalArrowhead = "104 68 m 116 68 l 110 60 l h f";

    private static PdfExtractionResult ExtractMainTable(params string[] extraContent)
    {
        var content = RuledTableWithColumnZeroLabels(Left, Bottom, Rows, Columns, CellWidth, CellHeight)
            .Concat(extraContent);
        return PdfTextExtractor.Extract(Page(string.Join("\n", content)));
    }

    private static PdfTable SingleTable(PdfExtractionResult result)
    {
        var table = Assert.Single(result.Tables![1]);
        Assert.Equal(Rows, table.Rows.Count);
        Assert.All(table.Rows, row => Assert.Equal(Columns, row.Cells.Count));
        return table;
    }

    [Fact]
    public void Right_pointing_filled_arrow_is_detected_as_arrow_right_over_its_covered_row_and_columns()
    {
        var table = SingleTable(ExtractMainTable(RightArrow));

        var overlay = Assert.Single(table.Overlays!);
        Assert.Equal("arrow", overlay.Kind);
        Assert.Equal("right", overlay.Direction);
        Assert.Equal("horizontal", overlay.Axis);
        Assert.Equal(1, overlay.StartRow);
        Assert.Equal(1, overlay.EndRow);
        Assert.Equal(1, overlay.StartColumn);
        Assert.Equal(2, overlay.EndColumn);
        Assert.Equal(string.Empty, overlay.Text);
    }

    [Fact]
    public void Left_pointing_filled_arrow_is_detected_as_arrow_left()
    {
        var table = SingleTable(ExtractMainTable(LeftArrow));

        var overlay = Assert.Single(table.Overlays!);
        Assert.Equal("arrow", overlay.Kind);
        Assert.Equal("left", overlay.Direction);
        Assert.Equal("horizontal", overlay.Axis);
        Assert.Equal(2, overlay.StartRow);
        Assert.Equal(2, overlay.EndRow);
        Assert.Equal(1, overlay.StartColumn);
        Assert.Equal(2, overlay.EndColumn);
    }

    [Fact]
    public void Filled_rectangle_is_detected_as_a_bar_and_does_not_break_grid_regularity()
    {
        var table = SingleTable(ExtractMainTable(Bar));

        var overlay = Assert.Single(table.Overlays!);
        Assert.Equal("bar", overlay.Kind);
        Assert.Equal("none", overlay.Direction);
        Assert.Equal(3, overlay.StartRow);
        Assert.Equal(3, overlay.EndRow);
        Assert.Equal(3, overlay.StartColumn);
        Assert.Equal(4, overlay.EndColumn);
    }

    [Fact]
    public void Four_vertex_non_axis_aligned_shape_is_detected_as_a_diamond_marker()
    {
        var table = SingleTable(ExtractMainTable(Diamond));

        var overlay = Assert.Single(table.Overlays!);
        Assert.Equal("marker", overlay.Kind);
        Assert.Equal("diamond", overlay.ShapePreset);
        Assert.Equal(0, overlay.StartRow);
        Assert.Equal(0, overlay.EndRow);
        Assert.Equal(4, overlay.StartColumn);
        Assert.Equal(4, overlay.EndColumn);
    }

    [Fact]
    public void Vertical_stroke_with_a_triangle_head_at_the_bottom_spans_every_row_as_a_down_arrow()
    {
        var table = SingleTable(ExtractMainTable(VerticalShaft, VerticalArrowhead));

        var overlay = Assert.Single(table.Overlays!);
        Assert.Equal("arrow", overlay.Kind);
        Assert.Equal("down", overlay.Direction);
        Assert.Equal("vertical", overlay.Axis);
        Assert.Equal(0, overlay.StartRow);
        Assert.Equal(Rows - 1, overlay.EndRow);
        Assert.Equal(0, overlay.StartColumn);
        Assert.Equal(0, overlay.EndColumn);
    }

    [Fact]
    public void All_five_overlay_kinds_together_are_all_detected_on_the_same_table()
    {
        var table = SingleTable(ExtractMainTable(RightArrow, LeftArrow, Bar, Diamond, VerticalShaft, VerticalArrowhead));

        Assert.Equal(5, table.Overlays!.Count);
        // Ordered by (StartRow, StartColumn): the down-arrow (row 0, column 0) sorts before the
        // diamond (also row 0, but column 4), then the right/left arrows (rows 1-2), then the bar
        // (row 3) -- see the individual Kind tests above for each shape's own row/column.
        Assert.Equal(["arrow", "marker", "arrow", "arrow", "bar"],
            table.Overlays!.OrderBy(o => o.StartRow).ThenBy(o => o.StartColumn).Select(o => o.Kind).ToArray());
    }

    [Fact]
    public void Accounting_stays_consistent_once_overlays_are_folded_into_the_table()
    {
        var result = ExtractMainTable(RightArrow, Bar, Diamond, VerticalShaft, VerticalArrowhead);
        SingleTable(result);

        var graph = result.VisualProjections![1].Graph;
        Assert.True(graph.SourceAccounting.IsConsistent,
            $"Unaccounted={graph.SourceAccounting.Unaccounted}, InvalidReferences={graph.SourceAccounting.InvalidReferences}");
        Assert.DoesNotContain(result.Diagnostics!, message => message.StartsWith("VisualDirectionalShapeFallback", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Readable_markdown_shows_the_overlay_glyphs_with_no_fallback_section_for_the_overlay_members()
    {
        var root = Path.Combine(Path.GetTempPath(), "docredock-pdf-overlay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "overlay.pdf");
            var markdownPath = Path.Combine(root, "overlay.md");
            File.WriteAllBytes(source, Page(string.Join("\n",
                RuledTableWithColumnZeroLabels(Left, Bottom, Rows, Columns, CellWidth, CellHeight)
                    .Concat([RightArrow, LeftArrow, Bar, Diamond, VerticalShaft, VerticalArrowhead]))));

            await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, markdownPath));
            var markdown = await File.ReadAllTextAsync(markdownPath);

            // Glyphs land on the row/column the overlay covers (see the individual Kind tests
            // above for the exact geometry); the table itself must still render as a GFM table.
            Assert.Contains("| --- | --- | --- | --- | --- |", markdown, StringComparison.Ordinal);
            Assert.Contains("━━▶", markdown, StringComparison.Ordinal); // right arrow head
            Assert.Contains("◀━", markdown, StringComparison.Ordinal); // left arrow head (multi-col body already covered by ━━ above)
            Assert.Contains("━━", markdown, StringComparison.Ordinal); // bar
            Assert.Contains("◆", markdown, StringComparison.Ordinal); // diamond
            Assert.Contains("▼", markdown, StringComparison.Ordinal); // down-arrow head
            Assert.Contains("│", markdown, StringComparison.Ordinal); // down-arrow body

            // None of the five overlay shapes survives as its own "Visual flow" fallback entry: no
            // diagram/mermaid section, and no "ノード:"/"パス:" listing for any of them. A residual
            // *diagnostic*-only fallback section (e.g. an ambiguous nearby-label warning unrelated
            // to any overlay -- the same benign residue the real schedule-arrows.pdf fixture
            // produces, see PdfScheduleOverlayFixtureTests) may still legitimately appear.
            Assert.DoesNotContain("```mermaid", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("ノード: ", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("パス: ", markdown, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_edge_that_crosses_the_table_but_is_mostly_outside_it_still_disqualifies_the_table()
    {
        // Two labelled boxes (a genuine two-node diagram) with box1 INSIDE the table's bounds and
        // box2 well outside it; the connecting shaft spends only 70 of its 160 units (43.75%)
        // inside the table (X:[60,560]) -- below the 50% "overlay candidate" threshold -- so this
        // remains a real diagram connector crossing the table, not a schedule overlay, and the
        // existing (pre-P-Overlay) rejection must still apply.
        var content = RuledTableWithColumnZeroLabels(Left, Bottom, Rows, Columns, CellWidth, CellHeight).Concat(
        [
            Line(455, 155, "NODE1"), "450 140 40 40 re S",
            Line(655, 155, "NODE2"), "650 140 40 40 re S",
            "490 160 m 650 160 l S",
            "644 168 m 644 152 l 656 160 l h f",
        ]);

        var result = PdfTextExtractor.Extract(Page(string.Join("\n", content)));

        Assert.Null(result.Tables?.GetValueOrDefault(1));
        var edge = Assert.Single(result.VisualGraphs![1].Edges, edge => edge.EdgeDirection == VisualEdgeDirection.Directed);
        Assert.Equal("NODE1", result.VisualGraphs![1].Nodes.Single(node => node.Id == edge.SourceId).Label);
        Assert.Equal("NODE2", result.VisualGraphs![1].Nodes.Single(node => node.Id == edge.TargetId).Label);
    }

    [Fact]
    public void A_second_independent_ruled_table_on_the_page_is_unaffected_by_the_first_tables_overlay()
    {
        // A second, wholly separate 3x3 ruled table far to the right of the main table. Distinct
        // crossing-connected components (SeparateGridComponents) must keep the two tables from
        // interfering: the far table carries no overlay at all, while the main table keeps its bar.
        var farLeft = 900;
        var farTable = new List<string>();
        for (var row = 0; row <= 3; row++)
            farTable.Add($"{farLeft} {60 + row * 40} m {farLeft + 3 * 80} {60 + row * 40} l S");
        for (var column = 0; column <= 3; column++)
            farTable.Add($"{farLeft + column * 80} 60 m {farLeft + column * 80} {60 + 3 * 40} l S");
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
            farTable.Add(Line(farLeft + column * 80 + 10, 60 + row * 40 + 10, $"F{row}{column}"));

        var content = RuledTableWithColumnZeroLabels(Left, Bottom, Rows, Columns, CellWidth, CellHeight)
            .Concat([Bar]).Concat(farTable);

        var result = PdfTextExtractor.Extract(Page(string.Join("\n", content)));

        Assert.Equal(2, result.Tables![1].Count);
        var mainTable = result.Tables![1].Single(table => table.Rows.Count == Rows);
        var otherTable = result.Tables![1].Single(table => table.Rows.Count == 3);
        Assert.Equal(Columns, mainTable.Rows[0].Cells.Count);
        Assert.Equal(3, otherTable.Rows[0].Cells.Count);
        Assert.Equal("bar", Assert.Single(mainTable.Overlays!).Kind);
        Assert.True(otherTable.Overlays is null or { Count: 0 });
    }

    [Fact]
    public void A_line_coinciding_with_a_table_boundary_is_not_reported_as_an_overlay()
    {
        // A residual/duplicate ruling-line fragment sitting exactly on the table's own left
        // boundary (X=60), deliberately NOT included in the table's SourcePathIds (as if
        // BuildVisualGraph could not fold it into the recognized grid) -- PdfTableOverlayDetector
        // must still recognize the coincidence and skip it, rather than reporting a "line" overlay.
        var table = SingleTable(ExtractMainTable());
        var strayPoints = new[] { new VisualPathPoint(60, 60), new VisualPathPoint(60, 220) };
        var strayGraph = new VisualGraph("stray", [],
            [new VisualEdge("stray-edge", null, null, Geometry: new Geometry("pdf-user-space", 60, 60, 0, 160),
                Path: strayPoints, EdgeDirection: VisualEdgeDirection.Undirected)],
            Paths: [new VisualPath("stray-edge-path", strayPoints, new Geometry("pdf-user-space", 60, 60, 0, 160))]);

        var overlays = PdfTableOverlayDetector.Detect(table, strayGraph);

        Assert.Empty(overlays);
    }
}
