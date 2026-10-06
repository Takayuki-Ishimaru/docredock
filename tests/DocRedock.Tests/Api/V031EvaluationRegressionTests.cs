using System.Text.Json;
using DocRedock.Api;
using DocRedock.Cli;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Xlsx;

namespace DocRedock.Tests.Api;

/// <summary>
/// Regressions from the v0.3.1 evaluation, through the real XLSX reader: a single blank column
/// must keep one table together (a numeric ID with its quantities, row labels with their numbers)
/// and must separate independent lists, declared Excel tables decide where they apply, and the
/// export summary says what was detected and how reading order and tables were obtained.
/// </summary>
public sealed class V031EvaluationRegressionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-v031-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);
    public void Dispose() => Directory.Delete(root, true);

    internal static string Fixture(string name, string evaluation = "V031")
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var path = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Evaluation", evaluation, name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Evaluation fixture not found.", name);
    }

    private async Task<(string Markdown, ReadableDocumentExportResult Result)> ExportAsync(string name, string policy = "visible")
    {
        var output = Path.Combine(root, Path.GetFileNameWithoutExtension(name) + "-" + policy + ".md");
        var result = await service.ExportReadableAsync(new(Fixture(name), output, ContentPolicy: policy));
        return (await File.ReadAllTextAsync(output), result);
    }

    [Theory]
    [InlineData("unified_numeric_id.xlsx", "Apple", "2", "100")]
    [InlineData("unified_numeric_id.xlsx", "Pear", "3", "200")]
    [InlineData("unified_text_labels.xlsx", "Apple", "2", "100")]
    [InlineData("row_labels_spacer.xlsx", "Revenue", "100", "120")]
    public async Task One_table_across_a_spacer_column_keeps_each_row_together(string name, string label, string first, string second)
    {
        foreach (var policy in new[] { "visible", "complete" })
        {
            var (markdown, result) = await ExportAsync(name, policy);
            Assert.True(SameTableRow(markdown, label, first, second), markdown);
            Assert.Empty(result.Projection.TableBoundaryReviews);
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "XlsxTableBoundaryAmbiguous");
            var summary = ExportSummaryBuilder.BuildReadable(result.Graph, result.Diagnostics, result.Projection);
            Assert.Equal((StructureBasis.Source, StructureBasis.Inferred, false), (summary.ReadingOrder, summary.TableStructure, summary.ReviewItemsDetected));
        }
    }

    [Theory]
    [InlineData("independent_one_column.xlsx", true)]
    [InlineData("independent_two_columns.xlsx", true)]
    [InlineData("excel_table_with_side_list.xlsx", false)]
    public async Task Independent_lists_never_share_a_row_or_block_with_the_revenue_table(string name, bool uncertain)
    {
        var (markdown, result) = await ExportAsync(name);
        var text = markdown.Replace("\\", "", StringComparison.Ordinal);
        foreach (var value in new[] { "Revenue", "Cost", "CANDIDATE_ONE", "CANDIDATE_TWO", "Candidates" })
            Assert.Contains(value, text, StringComparison.Ordinal);
        Assert.DoesNotContain(text.Split('\n'), line => (line.Contains("Revenue") || line.Contains("Cost")) && line.Contains("CANDIDATE"));
        Assert.DoesNotContain(text.Split("\n\n"), block => (block.Contains("Revenue") || block.Contains("Cost")) && block.Contains("CANDIDATE"));
        Assert.True(SameTableRow(markdown, "Revenue", "100"), markdown);
        Assert.Equal(uncertain ? 1 : 0, result.Projection.TableBoundaryReviews.Count);
        Assert.Equal(uncertain ? 1 : 0, result.Diagnostics.Count(diagnostic =>
            diagnostic.Code == "XlsxTableBoundaryAmbiguous" && diagnostic.Severity == DocRedock.Core.Reporting.DiagnosticSeverity.Information));
        var summary = ExportSummaryBuilder.BuildReadable(result.Graph, result.Diagnostics, result.Projection);
        Assert.Equal(uncertain ? StructureBasis.NeedsComparison : StructureBasis.Inferred, summary.TableStructure);
        Assert.Equal(StructureBasis.Inferred, summary.ReadingOrder);
        Assert.Equal(uncertain, summary.ReviewItemsDetected);
        Assert.Equal(0, summary.Warnings);
    }

    [Fact]
    public async Task Two_declared_Excel_tables_stay_separate_even_when_one_holds_only_numbers()
    {
        var (markdown, result) = await ExportAsync("excel_tables_side_by_side.xlsx");
        var text = markdown.Replace("\\", "", StringComparison.Ordinal);
        Assert.DoesNotContain(text.Split('\n'), line => line.Contains("Revenue") && line.Contains("120"));
        Assert.True(SameTableRow(markdown, "Revenue", "100"), markdown);
        Assert.True(SameTableRow(markdown, "4", "120"), markdown);
        Assert.Empty(result.Projection.TableBoundaryReviews);
    }

    [Fact]
    public void Excel_tables_are_read_from_their_table_parts_and_marked_on_their_cells()
    {
        using var stream = File.OpenRead(Fixture("excel_tables_side_by_side.xlsx"));
        var extracted = new XlsxAdapter().Extract(stream);
        var tables = Assert.Single(extracted.Worksheets).Tables!;
        Assert.Equal([("Sales", "A1:B3", 1), ("Targets", "D1:E3", 1)], tables.Select(table => (table.Name, table.Range, table.HeaderRowCount)));
        var membership = extracted.Graph.Nodes.Where(node => node.Kind == NodeKind.Cell).ToDictionary(
            node => node.Source!.Locators.Single(locator => locator.Kind == "cell_address").Value,
            node => node.Extensions!.TryGetValue("excel_table", out var table) ? table.GetString() : null);
        Assert.Equal("Sales", membership["A1"]);
        Assert.Equal("Sales", membership["B3"]);
        Assert.Equal("Targets", membership["D1"]);
        Assert.Equal("Targets", membership["E3"]);
        using var plain = File.OpenRead(Fixture("independent_one_column.xlsx"));
        var none = new XlsxAdapter().Extract(plain);
        Assert.Empty(Assert.Single(none.Worksheets).Tables!);
        Assert.DoesNotContain(none.Graph.Nodes, node => node.Extensions?.ContainsKey("excel_table") == true);
    }

    [Fact]
    public async Task Cli_summary_names_detected_items_reading_order_and_table_structure()
    {
        var cases = new (string Name, int Exit, string[] Lines)[]
        {
            ("independent_one_column.xlsx", 0, [
                "Detected review items: table boundaries 1",
                "Reading order: inferred from layout (not compared with the source)",
                "Table structure: inferred from layout; 1 boundary(ies) need source comparison",
                "Review sheet 'Data': cells on either side of blank column C (A1:B3, D1:D3) were output as separate tables",
                "INFORMATION XlsxTableBoundaryAmbiguous: Sheet 'Data'"]),
            ("unified_numeric_id.xlsx", 0, [
                "Detected review items: none",
                "Reading order: source order",
                "Table structure: inferred from layout (not compared with the source)",
                "Markdown tables rendered: 1"]),
        };
        foreach (var (name, exit, lines) in cases)
        {
            var stdout = new StringWriter();
            var app = new CliApplication(stdout, new StringWriter(), new DocumentService(null, null, discoverPdfRasterizer: false));
            Assert.Equal(exit, await app.RunAsync(["export", Fixture(name), "--output", Path.Combine(root, name + ".md"), "--ocr", "off"]));
            var text = stdout.ToString();
            Assert.DoesNotContain("Human review", text, StringComparison.Ordinal);
            Assert.DoesNotContain("not required", text, StringComparison.Ordinal);
            foreach (var line in lines) Assert.Contains(line, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Word_summary_takes_order_and_tables_from_the_source_and_editable_exports_are_not_evaluated()
    {
        var output = Path.Combine(root, "chunking.md");
        var result = await service.ExportReadableAsync(new(Fixture("chunking.docx", "V030"), output));
        var summary = ExportSummaryBuilder.BuildReadable(result.Graph, result.Diagnostics, result.Projection);
        Assert.Equal((StructureBasis.Source, StructureBasis.Source), (summary.ReadingOrder, summary.TableStructure));
        Assert.Contains("Reading order: source order\nTable structure: source tables\n", summary.ToString(), StringComparison.Ordinal);
        var editable = ExportSummaryBuilder.Build(result.Graph, result.Diagnostics);
        Assert.Contains("Reading order: not evaluated\nTable structure: not evaluated\n", editable.ToString(), StringComparison.Ordinal);
        var empty = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "empty", DocumentFormatKind.Docx, []);
        var none = ExportSummaryBuilder.BuildReadable(empty, [], DocRedock.Markdown.ReadableProjectionReport.Empty);
        Assert.Equal((StructureBasis.NotApplicable, StructureBasis.NotApplicable), (none.ReadingOrder, none.TableStructure));
    }

    [Fact]
    public void Slide_tables_synthesized_from_shapes_are_reported_as_inferred()
    {
        var table = new DocumentNode("grid", NodeKind.Table, null, 0, ContentLayer.Derived,
            new TableNodeContent([new TableCell[] { "A", "B" }, new TableCell[] { "1", "2" }]),
            Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["shape_grid_table"] = JsonSerializer.SerializeToElement(true) });
        var projection = new DocRedock.Markdown.ReadableProjectionReport(1, 0, []);
        DocumentGraph Slides(DocumentNode node) => new(DocumentGraph.CurrentSchemaVersion, "slides", DocumentFormatKind.Pptx, [new DocumentPartition("slide1", 0, [node])]);
        Assert.Equal(StructureBasis.Inferred, ExportSummaryBuilder.BuildReadable(Slides(table), [], projection).TableStructure);
        Assert.Equal(StructureBasis.Source, ExportSummaryBuilder.BuildReadable(Slides(table with { Extensions = null, Layer = ContentLayer.Body }), [], projection).TableStructure);
    }

    [Fact]
    public async Task AI_package_review_and_report_name_the_boundary_and_what_was_evaluated()
    {
        var result = await service.ExportAiPackageAsync(new(new(Fixture("independent_one_column.xlsx"), "unused.md"), Path.Combine(root, "package")));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.OutputPath, "report.json")));
        Assert.Equal("1.1", report.RootElement.GetProperty("schema_version").GetString());
        var summary = report.RootElement.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("table_boundary_review_items").GetInt32());
        Assert.Equal("inferred", summary.GetProperty("reading_order").GetString());
        Assert.Equal("needs_comparison", summary.GetProperty("table_structure").GetString());
        Assert.True(summary.GetProperty("review_items_detected").GetBoolean());
        var boundary = Assert.Single(report.RootElement.GetProperty("review").GetProperty("table_boundaries").EnumerateArray());
        Assert.Equal(("Data", "C1:C3", "A1:B3", "D1:D3"), (boundary.GetProperty("sheet_name").GetString(),
            boundary.GetProperty("gap_range").GetString(), boundary.GetProperty("left_range").GetString(), boundary.GetProperty("right_range").GetString()));
        var review = await File.ReadAllTextAsync(Path.Combine(result.OutputPath, "review.md"));
        Assert.Contains("- 表の区切りの確認 / Table boundaries to compare: 1", review, StringComparison.Ordinal);
        Assert.Contains("- 読み順 / Reading order: 配置から推定（原本とは未照合）", review, StringComparison.Ordinal);
        Assert.Contains("- 表構造 / Table structure: 配置から推定・1か所要照合", review, StringComparison.Ordinal);
        Assert.Contains("C列の空白の左右（A1:B3／D1:D3）を別の表として出力しました", review, StringComparison.Ordinal);
        Assert.Contains("- [part-0001](parts/0001.md)", review, StringComparison.Ordinal);
        Assert.DoesNotContain("照合が必要な箇所は検出されませんでした", review, StringComparison.Ordinal);
    }

    private static bool SameTableRow(string markdown, params string[] values) =>
        markdown.Split('\n').Where(line => line.StartsWith('|')).Select(line =>
            line.Trim('|').Split(" | ").Select(cell => cell.Trim()).ToArray())
            .Any(cells => values.All(value => cells.Contains(value)));
}
