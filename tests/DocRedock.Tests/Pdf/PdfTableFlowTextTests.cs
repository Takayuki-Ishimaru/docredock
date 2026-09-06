using System.Text;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Formats.Pdf;

namespace DocRedock.Tests.Pdf;

/// <summary>Regression coverage for F-01. Reconstructing a ruled table used to delete unrelated
/// native body text, because cells recorded positions in the pre-merge fragment list while the
/// projection removed those positions from the post-merge region list.</summary>
public sealed class PdfTableFlowTextTests
{
    private const string Sentinel = "NATIVE_TEXT_MUST_SURVIVE";

    [Fact]
    public void Compound_grid_with_a_trailing_move_only_subpath_still_reconstructs_the_table()
    {
        var rules = GridRulesCompound(100);
        var normal = PdfTextExtractor.Extract(Page(GridText(100) + "\n" + rules));
        var trailingMove = PdfTextExtractor.Extract(Page(GridText(100) + "\n" + rules[..^1] + "500 500 m S"));

        Assert.Single(normal.Tables![1]);
        var table = Assert.Single(trailingMove.Tables![1]);
        Assert.Equal(normal.Tables[1][0].Rows.Select(row => string.Join("|", row.Cells.Select(cell => cell.Text))),
            table.Rows.Select(row => string.Join("|", row.Cells.Select(cell => cell.Text))));
        Assert.DoesNotContain(trailingMove.VisualGraphs![1].Diagnostics!,
            diagnostic => diagnostic.Message.Contains("Unpainted", StringComparison.Ordinal));
    }


    [Theory]
    [InlineData(500)]
    [InlineData(530)]
    [InlineData(560)]
    [InlineData(730)]
    public void Ruled_table_keeps_independent_body_text_at_every_height(int bodyY)
    {
        var ruled = PdfTextExtractor.Extract(Page(TitleText() + "\n" + GridText(100) + "\n" + BodyText(100, bodyY) + "\n" + GridRules(100)));
        var plain = PdfTextExtractor.Extract(Page(TitleText() + "\n" + GridText(100) + "\n" + BodyText(100, bodyY)));

        // Control: with no rules there is no table, and the body text obviously survives.
        Assert.Null(plain.Tables?.GetValueOrDefault(1));
        Assert.Contains(Sentinel, plain.Text, StringComparison.Ordinal);

        Assert.Single(ruled.Tables![1]);
        Assert.Contains(Sentinel, ruled.Text, StringComparison.Ordinal);
        Assert.Contains("TITLE_LINE", ruled.Text, StringComparison.Ordinal);
        var nodes = Project(ruled);
        Assert.Single(nodes, node => node.Kind == NodeKind.Table);
        Assert.Equal(1, Paragraphs(nodes).Count(text => text.Contains(Sentinel, StringComparison.Ordinal)));
        Assert.Equal(1, Paragraphs(nodes).Count(text => text.Contains("TITLE_LINE", StringComparison.Ordinal)));
        // Cell text belongs to the table node only; it must not be duplicated as a paragraph.
        Assert.DoesNotContain(Paragraphs(nodes), text => text.Contains("R2C2", StringComparison.Ordinal));
        Assert.Contains(CellTexts(nodes), text => text == "R2C2");
        Assert.DoesNotContain(ruled.Diagnostics!, message =>
            message.StartsWith(PdfTextAccounting.UnaccountedDiagnosticCode, StringComparison.Ordinal));
    }

    [Fact]
    public void Compound_path_grid_with_one_paint_operator_reconstructs_the_same_table_as_separate_stroked_lines()
    {
        // Per the PDF spec (32000-1 8.5.3) a single painting operator applies to every subpath
        // of the current path, not just the last one. reportlab's canvas.grid() emits exactly
        // this shape: eight `m`/`l` subpaths followed by one `S`. Before the fix, every subpath
        // except the last was finalized immediately as an unpainted fallback path as soon as the
        // next `m` was seen, so the grid was never recognized as a table.
        var separate = PdfTextExtractor.Extract(Page(TitleText() + "\n" + GridText(100) + "\n" + BodyText(100, 530) + "\n" + GridRules(100)));
        var compound = PdfTextExtractor.Extract(Page(TitleText() + "\n" + GridText(100) + "\n" + BodyText(100, 530) + "\n" + GridRulesCompound(100)));

        Assert.Single(separate.Tables![1]);
        Assert.Single(compound.Tables![1]);
        Assert.Equal(
            separate.Tables![1][0].Rows.Select(row => string.Join("|", row.Cells.Select(cell => cell.Text))),
            compound.Tables![1][0].Rows.Select(row => string.Join("|", row.Cells.Select(cell => cell.Text))));

        Assert.Contains(Sentinel, compound.Text, StringComparison.Ordinal);
        var nodes = Project(compound);
        Assert.Single(nodes, node => node.Kind == NodeKind.Table);
        Assert.Equal(1, Paragraphs(nodes).Count(text => text.Contains(Sentinel, StringComparison.Ordinal)));
        Assert.Equal(1, Paragraphs(nodes).Count(text => text.Contains("TITLE_LINE", StringComparison.Ordinal)));

        Assert.DoesNotContain(compound.Diagnostics!, message => message.Contains("Unpainted PDF subpath", StringComparison.Ordinal));
        Assert.DoesNotContain(compound.Diagnostics!, message => message.StartsWith("VisualConnectorUnresolved", StringComparison.Ordinal));
        var compoundGraph = compound.VisualGraphs![1];
        Assert.DoesNotContain(compoundGraph.Diagnostics!, diagnostic => diagnostic.Message.Contains("Unpainted PDF subpath", StringComparison.Ordinal));
        Assert.DoesNotContain(compoundGraph.Diagnostics!, diagnostic => diagnostic.Code == "VisualConnectorUnresolved");

        // The two drawing styles must produce identical output apart from path ids.
        var separateDiagnostics = separate.VisualGraphs![1].Diagnostics!.Select(diagnostic => (diagnostic.Code, diagnostic.Message))
            .OrderBy(item => item.Code, StringComparer.Ordinal).ThenBy(item => item.Message, StringComparer.Ordinal).ToArray();
        var compoundDiagnostics = compoundGraph.Diagnostics!.Select(diagnostic => (diagnostic.Code, diagnostic.Message))
            .OrderBy(item => item.Code, StringComparer.Ordinal).ThenBy(item => item.Message, StringComparer.Ordinal).ToArray();
        Assert.Equal(separateDiagnostics, compoundDiagnostics);
    }

    [Fact]
    public void Body_text_beside_a_table_row_survives_without_being_merged_into_a_cell()
    {
        // Both sentinels share the baseline of the middle table row, one on each side of the grid.
        var content = TitleText() + "\n" + GridText(300) + "\n" +
            $"BT 1 0 0 1 100 655 Tm ({Sentinel}_LEFT) Tj ET\n" +
            $"BT 1 0 0 1 620 655 Tm ({Sentinel}_RIGHT) Tj ET\n" + GridRules(300);

        var result = PdfTextExtractor.Extract(Page(content));
        var nodes = Project(result);

        Assert.Single(result.Tables![1]);
        Assert.Equal(1, Paragraphs(nodes).Count(text => text.Contains(Sentinel + "_LEFT", StringComparison.Ordinal)));
        Assert.Equal(1, Paragraphs(nodes).Count(text => text.Contains(Sentinel + "_RIGHT", StringComparison.Ordinal)));
        // A sentinel glued to a cell would be removed with it; assert the fragments stayed apart.
        Assert.DoesNotContain(Paragraphs(nodes), text => text.Contains("R2C", StringComparison.Ordinal));
        Assert.Contains(Paragraphs(nodes), text => text.Trim() == Sentinel + "_LEFT");
        Assert.Contains(Paragraphs(nodes), text => text.Trim() == Sentinel + "_RIGHT");
    }

    [Fact]
    public void Two_independent_tables_keep_the_body_text_between_them()
    {
        var content = TitleText() + "\n" + GridText(100) + "\n" + GridText(100, 300) + "\n" +
            BodyText(100, 500) + "\n" + GridRules(100) + "\n" + GridRules(100, 300);

        var result = PdfTextExtractor.Extract(Page(content));
        var nodes = Project(result);

        Assert.Equal(2, result.Tables![1].Count);
        Assert.Equal(2, nodes.Count(node => node.Kind == NodeKind.Table));
        Assert.Equal(1, Paragraphs(nodes).Count(text => text.Contains(Sentinel, StringComparison.Ordinal)));
        Assert.Equal(1, Paragraphs(nodes).Count(text => text.Contains("TITLE_LINE", StringComparison.Ordinal)));
    }

    [Fact]
    public void Drawing_order_does_not_change_the_table_or_lose_body_text()
    {
        var natural = PdfTextExtractor.Extract(Page(TitleText() + "\n" + GridText(100) + "\n" + BodyText(100, 530) + "\n" + GridRules(100)));
        var shuffled = PdfTextExtractor.Extract(Page(string.Join("\n",
        [
            BodyText(100, 530),
            "BT 1 0 0 1 210 655 Tm (R2C2) Tj ET",
            GridRules(100),
            "BT 1 0 0 1 110 615 Tm (R1C1) Tj ET",
            "BT 1 0 0 1 310 695 Tm (R3C3) Tj ET",
            TitleText(),
            "BT 1 0 0 1 210 615 Tm (R1C2) Tj ET",
            "BT 1 0 0 1 110 695 Tm (R3C1) Tj ET",
            "BT 1 0 0 1 310 615 Tm (R1C3) Tj ET",
            "BT 1 0 0 1 110 655 Tm (R2C1) Tj ET",
            "BT 1 0 0 1 310 655 Tm (R2C3) Tj ET",
            "BT 1 0 0 1 210 695 Tm (R3C2) Tj ET"
        ])));

        var expected = Assert.Single(natural.Tables![1]);
        var actual = Assert.Single(shuffled.Tables![1]);
        Assert.Equal(expected.Rows.Select(row => string.Join("|", row.Cells.Select(cell => cell.Text))),
            actual.Rows.Select(row => string.Join("|", row.Cells.Select(cell => cell.Text))));
        var nodes = Project(shuffled);
        Assert.Equal(1, Paragraphs(nodes).Count(text => text.Contains(Sentinel, StringComparison.Ordinal)));
        Assert.Equal(1, Paragraphs(nodes).Count(text => text.Contains("TITLE_LINE", StringComparison.Ordinal)));
        Assert.DoesNotContain(Paragraphs(nodes), text => text.Contains("R1C1", StringComparison.Ordinal));
    }

    [Fact]
    public void Cell_text_ids_survive_reading_order_merging()
    {
        var result = PdfTextExtractor.Extract(Page(TitleText() + "\n" + GridText(100) + "\n" + BodyText(100, 530) + "\n" + GridRules(100)));

        var table = Assert.Single(result.Tables![1]);
        var page = result.Pages[0];
        var claimed = PdfTextAccounting.TableSourceIds([table]);
        var parsed = page.Regions.SelectMany(region => region.SourceTextIds).ToArray();

        // Every id a cell claims must still exist in the page, and no fragment may be orphaned.
        Assert.All(claimed, id => Assert.Contains(id, parsed));
        Assert.True(PdfTextAccounting.Reconcile(page, [table]).IsComplete);
        Assert.All(page.Regions, region => Assert.NotEmpty(region.SourceTextIds));
        Assert.Equal(parsed.Length, parsed.Distinct().Count());
    }

    [Fact]
    public void Accounting_reports_a_cell_that_claims_an_unknown_fragment_and_keeps_flow_text()
    {
        var page = new PdfPageText(1,
        [
            new PdfTextRegion("Body", new Geometry("pdf-user-space", 0, 100, 30, 10), 0, [0]),
            new PdfTextRegion("A", new Geometry("pdf-user-space", 10, 10, 10, 10), 1, [1])
        ]);
        var table = new PdfTable("ghost", 1, new Geometry("pdf-user-space", 0, 0, 200, 60),
        [
            new PdfTableRow(
            [
                new PdfTableCell(0, 0, 1, 1, new Geometry("pdf-user-space", 0, 0, 100, 30), "A", [1]),
                new PdfTableCell(0, 1, 1, 1, new Geometry("pdf-user-space", 100, 0, 100, 30), "Ghost", [99])
            ])
        ], PdfTableConfidence.HighConfidenceInferred, []);

        var accounting = PdfTextAccounting.Reconcile(page, [table]);

        Assert.False(accounting.IsComplete);
        Assert.Equal([99], accounting.UnknownTableSourceIds);
        Assert.Empty(accounting.UnaccountedSourceIds);
        Assert.Contains(accounting.Describe(1), message =>
            message.StartsWith(PdfTextAccounting.UnaccountedDiagnosticCode + ":", StringComparison.Ordinal));
        // Conservative behaviour: the unrelated flow text is still projected.
        Assert.Contains(Paragraphs(PdfPageProjection.ToDocumentNodes(page, [table])), text => text == "Body");
    }

    [Fact]
    public void Accounting_reports_a_fragment_that_is_in_neither_a_table_nor_the_flow()
    {
        var flow = new PdfTextRegion("Body", new Geometry("pdf-user-space", 0, 100, 30, 10), 0, [0]);
        var table = new PdfTable("t", 1, new Geometry("pdf-user-space", 0, 0, 100, 30),
            [new PdfTableRow([new PdfTableCell(0, 0, 1, 1, new Geometry("pdf-user-space", 0, 0, 100, 30), "A", [1])])],
            PdfTableConfidence.HighConfidenceInferred, []);

        var accounting = PdfTextAccounting.Reconcile([0, 1, 2], [flow], [table]);

        Assert.Equal([2], accounting.UnaccountedSourceIds);
        Assert.Empty(accounting.UnknownTableSourceIds);
        Assert.False(accounting.IsComplete);
        Assert.Contains("1 native text fragment(s)", string.Join(" ", accounting.Describe(4)), StringComparison.Ordinal);
        Assert.Contains("PDF page 4", string.Join(" ", accounting.Describe(4)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_region_mixing_table_and_body_fragments_is_never_dropped()
    {
        var mixed = new PdfTextRegion("A " + Sentinel, new Geometry("pdf-user-space", 0, 0, 200, 10), 1, [1, 5]);
        var page = new PdfPageText(1, [mixed]);
        var table = new PdfTable("t", 1, new Geometry("pdf-user-space", 0, 0, 100, 30),
            [new PdfTableRow([new PdfTableCell(0, 0, 1, 1, new Geometry("pdf-user-space", 0, 0, 100, 30), "A", [1])])],
            PdfTableConfidence.HighConfidenceInferred, []);

        Assert.False(PdfTextAccounting.IsTableOwned(mixed, PdfTextAccounting.TableSourceIds([table])));
        Assert.Contains(Paragraphs(PdfPageProjection.ToDocumentNodes(page, [table])), text => text.Contains(Sentinel, StringComparison.Ordinal));
    }

    [Fact]
    public void Unaccounted_native_text_is_reported_as_a_warning_diagnostic()
    {
        var page = new PdfPageText(1, [new PdfTextRegion("Body", new Geometry("pdf-user-space", 0, 0, 10, 10), 0, [0])]);
        var extraction = new PdfExtractionResult(1, [page],
            [$"{PdfTextAccounting.UnaccountedDiagnosticCode}: PDF page 1: 1 native text fragment(s) were represented by neither a table cell nor a flow region; they are kept as flow text."]);

        var diagnostic = Assert.Single(PdfDocumentGraphProjection.Diagnostics(extraction));

        Assert.Equal(PdfTextAccounting.UnaccountedDiagnosticCode, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    private static byte[] Page(string content) => Encoding.Latin1.GetBytes(
        "%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length " +
        content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        " >> stream\n" + content + "\nendstream\n%%EOF");

    private static string TitleText() => "BT 1 0 0 1 100 760 Tm (TITLE_LINE) Tj ET";

    private static string BodyText(int x, int y) =>
        $"BT 1 0 0 1 {x} {y} Tm ({Sentinel}) Tj ET";

    /// <summary>Nine cell labels for a 3x3 grid whose left edge is <paramref name="left"/> and whose
    /// bottom edge is <paramref name="bottom"/>.</summary>
    private static string GridText(int left, int bottom = 600) => string.Join("\n",
        Enumerable.Range(0, 3).SelectMany(row => Enumerable.Range(0, 3).Select(column =>
            $"BT 1 0 0 1 {left + 10 + column * 100} {bottom + 15 + row * 40} Tm (R{row + 1}C{column + 1}) Tj ET")));

    private static string GridRules(int left, int bottom = 600) => string.Join("\n",
        Enumerable.Range(0, 4).Select(index => $"{left} {bottom + index * 40} m {left + 300} {bottom + index * 40} l S")
            .Concat(Enumerable.Range(0, 4).Select(index => $"{left + index * 100} {bottom} m {left + index * 100} {bottom + 120} l S")));

    /// <summary>The same eight grid rules as <see cref="GridRules"/>, but drawn as a single
    /// compound path (eight `m`/`l` subpaths) stroked by one trailing `S`, the way reportlab's
    /// canvas.grid() emits a ruled table.</summary>
    private static string GridRulesCompound(int left, int bottom = 600) => string.Join("\n",
        Enumerable.Range(0, 4).Select(index => $"{left} {bottom + index * 40} m {left + 300} {bottom + index * 40} l")
            .Concat(Enumerable.Range(0, 4).Select(index => $"{left + index * 100} {bottom} m {left + index * 100} {bottom + 120} l"))) + " S";

    private static IReadOnlyList<DocumentNode> Project(PdfExtractionResult result) =>
        PdfDocumentGraphProjection.CreateGraph(result, "0123456789abcdef0123456789abcdef")
            .Partitions!.SelectMany(partition => partition.Nodes).ToArray();

    private static IEnumerable<string> Paragraphs(IEnumerable<DocumentNode> nodes) => nodes
        .Where(node => node.Kind == NodeKind.Paragraph && node.Content is TextNodeContent)
        .Select(node => ((TextNodeContent)node.Content).Text);

    private static IEnumerable<string> CellTexts(IEnumerable<DocumentNode> nodes) => nodes
        .Where(node => node.Content is TableNodeContent)
        .SelectMany(node => ((TableNodeContent)node.Content).Rows).SelectMany(row => row).Select(cell => cell.Text);
}
