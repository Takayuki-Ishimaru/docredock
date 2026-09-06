using System.Text;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;

namespace DocRedock.Tests.Pdf;

/// <summary>Regression coverage for the "two tables invent connectors" defect. Table-grid
/// suppression used to test every axis-parallel line on the page as a single lattice, so two
/// independent tables made each other's spacing irregular, nothing was suppressed, and every
/// table rule survived as an unresolvable diagram connector - one phantom relation and one
/// <c>VisualConnectorUnresolved</c> warning per rule. Suppression now runs per crossing-connected
/// component.</summary>
public sealed class PdfMultiTableGridSuppressionTests
{
    private static byte[] Page(string content) => Encoding.Latin1.GetBytes(
        "%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length " +
        content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        " >> stream\n" + content + "\nendstream\n%%EOF");

    private static string Line(int x, int y, string text) => $"BT 1 0 0 1 {x} {y} Tm ({text}) Tj ET";

    /// <summary>A ruled grid of <paramref name="rows"/> x <paramref name="columns"/> cells anchored
    /// at (<paramref name="left"/>, <paramref name="bottom"/>), with one text fragment per cell.</summary>
    private static IEnumerable<string> RuledTable(string prefix, int left, int bottom, int rows, int columns,
        int cellWidth, int cellHeight)
    {
        for (var row = 0; row <= rows; row++)
            yield return $"{left} {bottom + row * cellHeight} m {left + columns * cellWidth} {bottom + row * cellHeight} l S";
        for (var column = 0; column <= columns; column++)
            yield return $"{left + column * cellWidth} {bottom} m {left + column * cellWidth} {bottom + rows * cellHeight} l S";
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
            yield return Line(left + column * cellWidth + 10, bottom + row * cellHeight + 10, $"{prefix}{row}{column}");
    }

    // Two independent grids with deliberately different geometry: 3x3 cells of 100x40 high on the
    // page, and 4x2 cells of 65x30 lower down. Their combined line spacing is irregular, which is
    // exactly what a page-wide lattice test used to trip over.
    private static readonly string[] TwoTables =
    [
        Line(60, 760, "BODY_ABOVE_TABLES"),
        .. RuledTable("A", 60, 600, 3, 3, 100, 40),
        Line(60, 500, "BODY_BETWEEN_TABLES"),
        .. RuledTable("B", 60, 300, 4, 2, 65, 30),
        Line(60, 220, "BODY_BELOW_TABLES"),
    ];

    // A genuine two-node flow with an arrowhead, placed clear of both grids.
    private static readonly string[] ArrowDiagram =
    [
        Line(410, 20, "FLOW_START"),
        "400 0 100 50 re S",
        Line(410, 170, "FLOW_END"),
        "400 150 100 50 re S",
        "450 50 m 450 150 l S",
        "450 150 m 444 140 l 456 140 l h f",
    ];

    private static IReadOnlyList<string> BodyTexts(PdfExtractionResult result) => result.Pages[0].Regions
        .Where(region => region.Text.StartsWith("BODY_", StringComparison.Ordinal))
        .Select(region => region.Text).ToArray();

    [Fact]
    public void Two_independent_tables_are_reconstructed_without_inventing_diagram_connectors()
    {
        var result = PdfTextExtractor.Extract(Page(string.Join("\n", TwoTables)));

        var tables = result.Tables![1];
        Assert.Equal(2, tables.Count);
        Assert.Equal([(3, 3), (4, 2)],
            tables.Select(table => (table.Rows.Count, table.Rows[0].Cells.Count)).OrderBy(shape => shape.Item1).ToArray());
        Assert.DoesNotContain(result.Diagnostics!,
            message => message.StartsWith("VisualConnectorUnresolved", StringComparison.Ordinal));
        Assert.Empty(result.VisualGraphs![1].Edges);
        Assert.DoesNotContain(result.VisualGraphs[1].Diagnostics!, diagnostic => diagnostic.Code == "VisualConnectorUnresolved");
        Assert.Equal(["BODY_ABOVE_TABLES", "BODY_BETWEEN_TABLES", "BODY_BELOW_TABLES"], BodyTexts(result));
    }

    [Fact]
    public void A_real_arrow_below_two_tables_still_resolves_into_a_directed_edge()
    {
        var result = PdfTextExtractor.Extract(Page(string.Join("\n", TwoTables.Concat(ArrowDiagram))));

        Assert.Equal(2, result.Tables![1].Count);
        var graph = result.VisualGraphs![1];
        var edge = Assert.Single(graph.Edges);
        Assert.Equal(VisualEdgeDirection.Directed, edge.EdgeDirection);
        Assert.Equal("FLOW_START", graph.Nodes.Single(node => node.Id == edge.SourceId).Label);
        Assert.Equal("FLOW_END", graph.Nodes.Single(node => node.Id == edge.TargetId).Label);
        Assert.DoesNotContain(result.Diagnostics!,
            message => message.StartsWith("VisualConnectorUnresolved", StringComparison.Ordinal));
        Assert.Equal(["BODY_ABOVE_TABLES", "BODY_BETWEEN_TABLES", "BODY_BELOW_TABLES"], BodyTexts(result));
    }
}
