using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Formats.OpenXml.Xlsx;
using DocRedock.Markdown;

namespace DocRedock.Tests.RoundTrip;

/// <summary>
/// Regression tests for the field-evaluation bug (v0.3.10, priority B): a workbook with a two-row
/// vertically merged header plus a small legend table to the side made the roundtrip export write two
/// sheet-table ranges that overlap (<c>A1:F9</c> and <c>D4:F4</c>). Unedited export -> restore is
/// byte-identical, so no existing test noticed, but every single-cell edit was then rejected by
/// <c>preflight</c> (SpreadsheetGridOverlap + SpreadsheetGridPolicyMismatch, exit 6).
/// The export contract is: the sheet-table ranges of one sheet are pairwise disjoint, so the editor's
/// "every baseline cell inside the range" rule and the section's own cells are the same set.
/// </summary>
public sealed class XlsxMergedHeaderEditTests
{
    // ---------------------------------------------------------------------------------------
    // Whole-file tests: build an .xlsx, DocumentService export -> edit -> preflight -> restore
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Export_of_a_merged_header_workbook_writes_disjoint_sheet_table_ranges()
    {
        var fixture = await ExportFixtureAsync();

        var ranges = SheetTableRanges(fixture.Projection, "sheet-Plan");

        // The main table keeps its whole header (both header rows, including the labels that sit
        // beside the horizontally merged "2026" group header) and the legend stays a separate table.
        Assert.Equal(["A1:F9", "J4:K7"], ranges);
        AssertPairwiseDisjoint(ranges);
        Assert.Contains("| 工程 | 2026年 |  | 合計 | 達成率 | 備考 |", fixture.Projection, StringComparison.Ordinal);
        Assert.Contains("baseline_nodes=27 ", fixture.Projection, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unedited_export_restores_byte_identical_to_the_source()
    {
        var fixture = await ExportFixtureAsync();
        var output = Path.Combine(fixture.Root, "unedited.xlsx");

        var preflight = await fixture.Service.PreflightAsync(fixture.Workspace, fixture.Markdown);
        var restored = await fixture.Service.RestoreAsync(new DocumentRestoreOptions(fixture.Workspace, output, fixture.Markdown));

        Assert.True(preflight.WorkspaceValid);
        Assert.False(preflight.ProjectionChanged);
        Assert.True(preflight.CanRestore);
        Assert.True(restored.Succeeded);
        Assert.Equal(await File.ReadAllBytesAsync(fixture.Source), await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task Editing_one_number_cell_passes_preflight_and_changes_only_that_cell()
    {
        var fixture = await ExportFixtureAsync();

        // The unique "`8` -> 8.0" cell is B6 (raw value 8, shown with the 0.0 number format).
        await AssertSingleCellEditAsync(fixture, "| `8` → 8.0 |", "| `10` → 10.0 |", "B6", "10");
    }

    [Fact]
    public async Task Editing_one_text_cell_passes_preflight_and_changes_only_that_cell()
    {
        var fixture = await ExportFixtureAsync();

        await AssertSingleCellEditAsync(fixture, @"| REVIEW\_OK\_319 |", @"| REVIEW\_OK\_320 |", "F9", "REVIEW_OK_320");
    }

    [Theory]
    [InlineData("| 工程 | 2026年 |", "| 工程改 | 2026年 |", "A4", "工程改")]       // top-left header cell
    [InlineData("| 合計 | 達成率 |", "| 総計 | 達成率 |", "D4", "総計")]            // lower-row header cell that was split off
    [InlineData("| 備考 |", "| 備考改 |", "F4", "備考改")]                        // ... and its right neighbour
    [InlineData("| 10月 | 11月 |", "| 10月 | 12月 |", "C5", "12月")]               // second header row of the merged group
    [InlineData("| R | 差戻し |", "| R | 返却 |", "K6", "返却")]                   // legend table cell
    public async Task Editing_a_header_or_legend_text_cell_passes_preflight_and_changes_only_that_cell(
        string original, string edited, string address, string expectedText)
    {
        var fixture = await ExportFixtureAsync();

        await AssertSingleCellEditAsync(fixture, original, edited, address, expectedText);
    }

    [Fact]
    public async Task Editing_cells_in_both_the_main_table_and_the_legend_in_one_pass_restores_both()
    {
        var fixture = await ExportFixtureAsync();
        var markdown = ReplaceOnce(fixture.Projection, "| `8` → 8.0 |", "| `10` → 10.0 |");
        markdown = ReplaceOnce(markdown, "| Q | 照会 |", "| Q | 照会中 |");
        markdown = ReplaceOnce(markdown, "| 合計 | 達成率 |", "| 総計 | 達成率 |");
        await File.WriteAllTextAsync(fixture.Markdown, markdown);
        var output = Path.Combine(fixture.Root, "multi.xlsx");

        var preflight = await fixture.Service.PreflightAsync(fixture.Workspace, fixture.Markdown);
        var restored = await fixture.Service.RestoreAsync(new DocumentRestoreOptions(fixture.Workspace, output, fixture.Markdown));

        AssertNoErrors(preflight);
        Assert.True(restored.Succeeded);
        var before = ReadCells(fixture.Source);
        var after = ReadCells(output);
        var changed = before.Keys.Concat(after.Keys).Distinct().Where(key => !Equals(before.GetValueOrDefault(key), after.GetValueOrDefault(key)))
            .Order().ToArray();
        Assert.Equal(["B6", "D4", "K7"], changed);
        Assert.Equal("10", after["B6"].Text);
        Assert.Equal("総計", after["D4"].Text);
        Assert.Equal("照会中", after["K7"].Text);
    }

    [Fact]
    public async Task Unsafe_edits_to_the_merged_header_workbook_are_still_rejected()
    {
        var fixture = await ExportFixtureAsync();

        // C4 is the continuation of the merged "2026年" header: it is not a baseline cell, so typing a
        // value into it is an addition the grid cannot map to any cell.
        await AssertRejectedAsync(fixture,
            ReplaceOnce(fixture.Projection, "| 工程 | 2026年 |  | 合計 |", "| 工程 | 2026年 | 追加 | 合計 |"),
            "UnsupportedSpreadsheetCellAddition");

        // Dropping a data row changes the table's shape.
        var withoutRow = Regex.Replace(fixture.Projection, @"^\| 試験 \|.*\r?\n", string.Empty, RegexOptions.Multiline);
        Assert.NotEqual(fixture.Projection, withoutRow);
        await AssertRejectedAsync(fixture, withoutRow, "SpreadsheetGridShapeMismatch");

        // Changing the declared range must not be accepted either.
        await AssertRejectedAsync(fixture,
            ReplaceOnce(fixture.Projection, "range=J4:K7 ", "range=J4:K8 "),
            "SpreadsheetRangeMismatch");
    }

    // ---------------------------------------------------------------------------------------
    // Graph-level tests: same layout without the ZIP round trip
    // ---------------------------------------------------------------------------------------

    // The layout v0.3.10 exported: the lower header row D4:F4 as a table of its own whose range lies inside the first
    // table's range, while the first table keeps blank D4:F4 places.
    private static string LegacyOverlappingProjection(string projection)
    {
        Assert.Contains("range=A1:F9 ", projection, StringComparison.Ordinal);
        var legacy = ReplaceOnce(projection, "baseline_nodes=27 ", "baseline_nodes=24 ");
        legacy = ReplaceOnce(legacy, "| 工程 | 2026年 |  | 合計 | 達成率 | 備考 |", "| 工程 | 2026年 |  |  |  |  |");
        return ReplaceOnce(legacy, "<!--drmd:sheet-table range=J4:K7 ",
            "<!--drmd:sheet-table range=D4:F4 source-columns=D,E,F source-rows=4 baseline_nodes=3 " +
            "editability=cell-grid operations=replace-cell constraints=preserve-range,preserve-addresses,no-insert-delete,safe-formula-->\n" +
            "| 合計 | 達成率 | 備考 |\n| --- | --- | --- |\n\n" +
            "<!--drmd:sheet-table range=J4:K7 ");
    }

    [Fact]
    public void Overlapping_ranges_written_by_v0_3_10_are_read_with_each_cell_in_the_smaller_table()
    {
        var graph = MergedHeaderGraph();
        var legacy = LegacyOverlappingProjection(new DocRedockMarkdownSerializer().Serialize(graph).Markdown);

        // Unedited, and with one cell edited in the inner table and one in the outer table.
        Assert.True(new MarkdownGraphEditor().Apply(graph, legacy).IsValid);
        var edited = ReplaceOnce(ReplaceOnce(legacy, "| 合計 | 達成率 | 備考 |", "| 総計 | 達成率 | 備考 |"), "| 要件定義 | 8 |", "| 要件定義 | 10 |");

        var result = new MarkdownGraphEditor().Apply(graph, edited);

        Assert.True(result.IsValid, string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        var cells = new XlsxAdapter().CreatePatchPlan(graph, result.EditedGraph).Edits.Select(edit => (edit.CellReference, edit.Value)).Order().ToArray();
        Assert.Equal([("B6", "10"), ("D4", "総計")], cells);
    }

    [Fact]
    public void Overlapping_ranges_whose_cells_cannot_be_told_apart_are_rejected_with_a_request_to_export_again()
    {
        var graph = MergedHeaderGraph();
        var legacy = LegacyOverlappingProjection(new DocRedockMarkdownSerializer().Serialize(graph).Markdown);
        // A value typed into the outer table's place for a cell of the inner table.
        var ambiguous = ReplaceOnce(legacy, "| 工程 | 2026年 |  |  |  |  |", "| 工程 | 2026年 |  | 総計 |  |  |");

        var result = new MarkdownGraphEditor().Apply(graph, ambiguous);

        Assert.False(result.IsValid);
        var overlap = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "SpreadsheetGridOverlap");
        Assert.Contains("export the workbook again", overlap.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Serializer_merges_sections_whose_bounding_ranges_intersect_and_leaves_disjoint_ones_alone()
    {
        var projection = new DocRedockMarkdownSerializer().Serialize(MergedHeaderGraph()).Markdown;

        var ranges = SheetTableRanges(projection, "sheet-Plan");

        Assert.Equal(["A1:F9", "J4:K7"], ranges);
        AssertPairwiseDisjoint(ranges);
    }

    [Fact]
    public void Every_single_cell_edit_of_the_merged_header_layout_is_accepted_by_the_editor()
    {
        var graph = MergedHeaderGraph();
        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;
        Assert.True(new MarkdownGraphEditor().Apply(graph, projection).IsValid);

        foreach (var cell in graph.Nodes.Where(node => node.Kind == NodeKind.Cell))
        {
            var text = ((TextNodeContent)cell.Content).Text;
            var edited = ReplaceOnce(projection, "| " + text + " |", "| " + text + "改 |", cell.Id);
            var result = new MarkdownGraphEditor().Apply(graph, edited);
            Assert.True(result.IsValid, $"{AddressOf(cell)}: " + string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message)));
            var changed = result.EditedGraph.Nodes.Where(node => node.Kind == NodeKind.Cell)
                .Where(node => ((TextNodeContent)node.Content).Text != ((TextNodeContent)graph.FindNode(node.Id)!.Content).Text)
                .Select(node => node.Id).ToArray();
            Assert.Equal([cell.Id], changed);
        }
    }

    /// <summary>
    /// The invariant behind the fix, checked on many sparse layouts: whatever the section heuristics
    /// decide, the exported ranges of a sheet never intersect, and the editor accepts the projection
    /// both unedited and with a single-cell edit.
    /// </summary>
    [Fact]
    public void Random_sparse_layouts_export_disjoint_ranges_that_the_editor_accepts()
    {
        var checkedWithMergedSections = 0;
        for (var seed = 1; seed <= 400; seed++)
        {
            var random = new Random(seed);
            var columns = random.Next(3, 15);
            var rows = random.Next(3, 15);
            var density = 0.12 + random.NextDouble() * 0.55;
            var cells = new List<DocumentNode>();
            for (var row = 1; row <= rows; row++)
                for (var column = 1; column <= columns; column++)
                    if (random.NextDouble() < density) cells.Add(TextCell(ColumnName(column) + row, "v" + ColumnName(column) + row, cells.Count));
            if (cells.Count == 0) continue;
            var graph = Graph(cells);
            var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;
            var context = $"seed {seed}, {cells.Count} cells";

            var ranges = SheetTableRanges(projection, "sheet-Plan");
            AssertPairwiseDisjoint(ranges, context);
            // Every cell is projected exactly once.
            foreach (var cell in cells)
            {
                var token = " " + ((TextNodeContent)cell.Content).Text + " |";
                Assert.True(CountOccurrences(projection, token) == 1, $"{context}: '{token}' is not projected exactly once.");
            }

            var unedited = new MarkdownGraphEditor().Apply(graph, projection);
            Assert.True(unedited.IsValid, context + ": " + string.Join("; ", unedited.Diagnostics.Select(d => d.Code + ": " + d.Message)));

            var target = cells[random.Next(cells.Count)];
            var targetText = ((TextNodeContent)target.Content).Text;
            var edited = ReplaceOnce(projection, " " + targetText + " |", " " + targetText + "改 |", context);
            var result = new MarkdownGraphEditor().Apply(graph, edited);
            Assert.True(result.IsValid, context + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message)));
            Assert.Equal(targetText + "改", ((TextNodeContent)result.EditedGraph.FindNode(target.Id)!.Content).Text);
            checkedWithMergedSections += ranges.Count > 1 ? 1 : 0;
        }

        Assert.True(checkedWithMergedSections > 50, "The generated layouts should include many multi-section sheets.");
    }

    // ---------------------------------------------------------------------------------------
    // Fixture: the merged-header workbook
    // ---------------------------------------------------------------------------------------

    private sealed record ExportedFixture(string Root, string Source, string Workspace, string Markdown, string Projection, DocumentService Service);

    private static async Task<ExportedFixture> ExportFixtureAsync()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "plan.xlsx");
        await File.WriteAllBytesAsync(source, BuildMergedHeaderWorkbook());
        var markdown = Path.Combine(root, "plan.md");
        var workspace = Path.Combine(root, "plan.drmd");
        var service = new DocumentService();
        await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown));
        return new ExportedFixture(root, source, workspace, markdown, await File.ReadAllTextAsync(markdown), service);
    }

    private static async Task AssertSingleCellEditAsync(
        ExportedFixture fixture, string original, string edited, string address, string expectedText)
    {
        await File.WriteAllTextAsync(fixture.Markdown, ReplaceOnce(fixture.Projection, original, edited));
        var output = Path.Combine(fixture.Root, "edited-" + address + ".xlsx");

        var preflight = await fixture.Service.PreflightAsync(fixture.Workspace, fixture.Markdown);
        var restored = await fixture.Service.RestoreAsync(new DocumentRestoreOptions(fixture.Workspace, output, fixture.Markdown));

        AssertNoErrors(preflight);
        Assert.True(preflight.WorkspaceValid);
        Assert.True(preflight.ProjectionChanged);
        Assert.True(preflight.CanRestore);
        Assert.Equal("F1", preflight.Fidelity);
        Assert.True(restored.Succeeded, string.Join("; ", restored.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Equal(FidelityLevel.F1, restored.Fidelity);

        // Cell level: exactly the edited cell differs; its neighbours, formulas, hidden-column cells and
        // the second sheet keep their values.
        var before = ReadCells(fixture.Source);
        var after = ReadCells(output);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        var changed = before.Keys.Where(key => !Equals(before[key], after[key])).ToArray();
        Assert.Equal([address], changed);
        Assert.Equal(expectedText, after[address].Text);

        // Part level: everything except the edited worksheet (and the recalculation flag DocRedock sets
        // on the workbook for any cell edit) is byte-identical; inside the edited worksheet only that
        // one <c> element differs, so merges, columns, rows and styles are untouched.
        var parts = ZipPartNames(fixture.Source);
        Assert.Equal(parts, ZipPartNames(output));
        var differing = parts.Where(part => !ReadPart(fixture.Source, part).SequenceEqual(ReadPart(output, part))).ToArray();
        Assert.Contains("xl/worksheets/sheet1.xml", differing);
        Assert.All(differing, part => Assert.Contains(part, new[] { "xl/worksheets/sheet1.xml", "xl/workbook.xml", "xl/sharedStrings.xml" }));
        AssertWorksheetDiffersOnlyInCell(fixture.Source, output, "xl/worksheets/sheet1.xml", address);
        Assert.Equal(ReadPart(fixture.Source, "xl/worksheets/sheet2.xml"), ReadPart(output, "xl/worksheets/sheet2.xml"));
        Assert.Equal(ReadPart(fixture.Source, "xl/styles.xml"), ReadPart(output, "xl/styles.xml"));
    }

    private static async Task AssertRejectedAsync(ExportedFixture fixture, string editedProjection, string expectedCode)
    {
        await File.WriteAllTextAsync(fixture.Markdown, editedProjection);

        var preflight = await fixture.Service.PreflightAsync(fixture.Workspace, fixture.Markdown);

        Assert.False(preflight.CanRestore, "The unsafe edit must not be restorable.");
        Assert.Contains(preflight.Diagnostics, diagnostic => diagnostic.Code == expectedCode && diagnostic.Severity == DiagnosticSeverity.Error);
        var output = Path.Combine(fixture.Root, "rejected-" + expectedCode + ".xlsx");
        var restored = await fixture.Service.RestoreAsync(new DocumentRestoreOptions(fixture.Workspace, output, fixture.Markdown));
        Assert.False(restored.Succeeded);
        Assert.False(File.Exists(output), "A rejected edit must not write a restored workbook.");
    }

    private static void AssertNoErrors(DocumentPreflightResult preflight) =>
        Assert.DoesNotContain(preflight.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    private static void AssertWorksheetDiffersOnlyInCell(string beforePath, string afterPath, string part, string address)
    {
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var before = XDocument.Parse(Encoding.UTF8.GetString(ReadPart(beforePath, part))).Root!;
        var after = XDocument.Parse(Encoding.UTF8.GetString(ReadPart(afterPath, part))).Root!;
        var beforeCells = before.Descendants(main + "c").ToDictionary(cell => (string)cell.Attribute("r")!);
        var afterCells = after.Descendants(main + "c").ToDictionary(cell => (string)cell.Attribute("r")!);
        Assert.Equal(beforeCells.Keys.Order(), afterCells.Keys.Order());
        Assert.Equal([address], beforeCells.Keys.Where(key => !XNode.DeepEquals(beforeCells[key], afterCells[key])).ToArray());
        // Row and column definitions, merged regions and everything else outside <sheetData> are identical.
        before.Element(main + "sheetData")!.RemoveNodes();
        after.Element(main + "sheetData")!.RemoveNodes();
        Assert.True(XNode.DeepEquals(before, after), "The worksheet changed outside the edited cell.");
        Assert.Equal(7, XDocument.Parse(Encoding.UTF8.GetString(ReadPart(afterPath, part))).Descendants(main + "mergeCell").Count());
    }

    // ---------------------------------------------------------------------------------------
    // Workbook builder (hand-written OOXML, like the other roundtrip tests)
    // ---------------------------------------------------------------------------------------

    private sealed record SheetCell(string Reference, int Style, object? Value = null, string? Formula = null);

    private static byte[] BuildMergedHeaderWorkbook()
    {
        var strings = new List<string>();
        int Shared(string text)
        {
            var index = strings.IndexOf(text);
            if (index < 0) { strings.Add(text); index = strings.Count - 1; }
            return index;
        }

        // Styles: 0 plain, 1 bold header, 2 "0.0", 3 percent.
        List<SheetCell> plan =
        [
            new("A1", 1, "設計レビュー工数計画"), new("B1", 1), new("C1", 1), new("D1", 1), new("E1", 1), new("F1", 1),
            new("A2", 0, "単位: 人日"),
            new("A4", 1, "工程"), new("B4", 1, "2026年"), new("C4", 1), new("D4", 1, "合計"), new("E4", 1, "達成率"), new("F4", 1, "備考"),
            new("G4", 1, "内部メモ"), new("J4", 1, "コード"), new("K4", 1, "意味"),
            new("A5", 1), new("B5", 1, "10月"), new("C5", 1, "11月"), new("D5", 1), new("E5", 1), new("F5", 1), new("G5", 1),
            new("J5", 0, "A"), new("K5", 0, "承認"),
            new("A6", 0, "要件定義"), new("B6", 2, 8), new("C6", 2, 4), new("D6", 2, 12, "SUM(B6:C6)"), new("E6", 3, 0.75),
            new("F6", 0, "API | DB 確認"), new("G6", 0, "HIDDEN_COL_614"), new("J6", 0, "R"), new("K6", 0, "差戻し"),
            new("A7", 0, "設計"), new("B7", 2, 6), new("C7", 2, 9), new("D7", 2, 15, "SUM(B7:C7)"), new("E7", 3, 0.6),
            new("F7", 0, "一行目\n二行目"), new("G7", 0, "内部コスト見積"), new("J7", 0, "Q"), new("K7", 0, "照会"),
            new("A8", 0, "HIDDEN_ROW_742"), new("B8", 2, 999), new("C8", 2, 777), new("D8", 2, 1776, "SUM(B8:C8)"), new("E8", 3, 0.1),
            new("F8", 0, "非表示行の内容"), new("G8", 0, "非表示行列交差"),
            new("A9", 0, "試験"), new("B9", 2, 5), new("C9", 2, 10), new("D9", 2, 15, "SUM(B9:C9)"), new("E9", 3, 0.4),
            new("F9", 0, "REVIEW_OK_319"), new("G9", 0, "未共有メモ"),
        ];
        List<SheetCell> chart =
        [
            new("A1", 1, "月"), new("B1", 1, "計画"), new("C1", 1, "実績"),
            new("A2", 0, "7月"), new("B2", 0, 10), new("C2", 0, 9),
            new("A3", 0, "8月"), new("B3", 0, 20), new("C3", 0, 21),
        ];

        string Worksheet(IReadOnlyList<SheetCell> cells, string? extraBeforeSheetData, string? afterSheetData, int[] hiddenRows)
        {
            var builder = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
            builder.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            builder.Append(extraBeforeSheetData).Append("<sheetData>");
            foreach (var row in cells.GroupBy(cell => RowOf(cell.Reference)).OrderBy(group => group.Key))
            {
                builder.Append("<row r=\"").Append(row.Key).Append('"').Append(hiddenRows.Contains(row.Key) ? " hidden=\"1\"" : string.Empty).Append('>');
                foreach (var cell in row.OrderBy(item => ColumnOf(item.Reference)))
                {
                    builder.Append("<c r=\"").Append(cell.Reference).Append("\" s=\"").Append(cell.Style).Append('"');
                    switch (cell.Value)
                    {
                        case null:
                            builder.Append("/>");
                            continue;
                        case string text:
                            builder.Append(" t=\"s\"><v>").Append(Shared(text)).Append("</v></c>");
                            continue;
                        default:
                            builder.Append('>');
                            if (cell.Formula is not null) builder.Append("<f>").Append(cell.Formula).Append("</f>");
                            builder.Append("<v>").Append(Convert.ToString(cell.Value, System.Globalization.CultureInfo.InvariantCulture)).Append("</v></c>");
                            continue;
                    }
                }
                builder.Append("</row>");
            }
            builder.Append("</sheetData>").Append(afterSheetData).Append("</worksheet>");
            return builder.ToString();
        }

        var planXml = Worksheet(plan,
            "<cols><col min=\"1\" max=\"6\" width=\"18\" customWidth=\"1\"/><col min=\"7\" max=\"7\" width=\"18\" hidden=\"1\" customWidth=\"1\"/><col min=\"8\" max=\"11\" width=\"18\" customWidth=\"1\"/></cols>",
            "<mergeCells count=\"7\"><mergeCell ref=\"A1:G1\"/><mergeCell ref=\"A4:A5\"/><mergeCell ref=\"B4:C4\"/><mergeCell ref=\"D4:D5\"/>" +
            "<mergeCell ref=\"E4:E5\"/><mergeCell ref=\"F4:F5\"/><mergeCell ref=\"G4:G5\"/></mergeCells>" +
            "<pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/>",
            [8]);
        var chartXml = Worksheet(chart, null, null, []);

        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var parts = new (string Name, string Content)[]
        {
            ("[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                  <Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                  <Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>
                </Types>
                """),
            ("_rels/.rels", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="{rel}/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """),
            ("xl/workbook.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="{main}" xmlns:r="{rel}">
                  <sheets><sheet name="Plan" sheetId="1" r:id="rId1"/><sheet name="Chart" sheetId="2" r:id="rId2"/></sheets>
                </workbook>
                """),
            ("xl/_rels/workbook.xml.rels", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="{rel}/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="{rel}/worksheet" Target="worksheets/sheet2.xml"/>
                  <Relationship Id="rId3" Type="{rel}/styles" Target="styles.xml"/>
                  <Relationship Id="rId4" Type="{rel}/sharedStrings" Target="sharedStrings.xml"/>
                </Relationships>
                """),
            ("xl/styles.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="{main}">
                  <numFmts count="1"><numFmt numFmtId="164" formatCode="0.0"/></numFmts>
                  <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>
                  <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
                  <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
                  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                  <cellXfs count="4">
                    <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
                    <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>
                    <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
                    <xf numFmtId="10" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
                  </cellXfs>
                </styleSheet>
                """),
            ("xl/worksheets/sheet1.xml", planXml),
            ("xl/worksheets/sheet2.xml", chartXml),
        };
        var sharedXml = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<sst xmlns=\"{main}\" count=\"{strings.Count}\" uniqueCount=\"{strings.Count}\">" +
            string.Concat(strings.Select(text => "<si><t xml:space=\"preserve\">" + System.Security.SecurityElement.Escape(text) + "</t></si>")) + "</sst>";

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in parts.Append(("xl/sharedStrings.xml", sharedXml)))
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    // ---------------------------------------------------------------------------------------
    // Graph fixture and helpers
    // ---------------------------------------------------------------------------------------

    /// <summary>The visible cells of the plan sheet (hidden column G and hidden row 8 are not projected).</summary>
    private static DocumentGraph MergedHeaderGraph()
    {
        var cells = new List<DocumentNode>();
        void Add(string address, string text) => cells.Add(TextCell(address, text, cells.Count));
        Add("A1", "設計レビュー工数計画");
        Add("A2", "単位: 人日");
        Add("A4", "工程"); Add("B4", "2026年"); Add("D4", "合計"); Add("E4", "達成率"); Add("F4", "備考");
        Add("J4", "コード"); Add("K4", "意味");
        Add("B5", "10月"); Add("C5", "11月"); Add("J5", "A"); Add("K5", "承認");
        Add("A6", "要件定義"); Add("B6", "8"); Add("C6", "4"); Add("D6", "12"); Add("E6", "0.75"); Add("F6", "確認A"); Add("J6", "R"); Add("K6", "差戻し");
        Add("A7", "設計"); Add("B7", "6"); Add("C7", "9"); Add("D7", "15"); Add("E7", "0.6"); Add("F7", "確認B"); Add("J7", "Q"); Add("K7", "照会");
        Add("A9", "試験"); Add("B9", "5"); Add("C9", "10"); Add("D9", "17"); Add("E9", "0.4"); Add("F9", "確認C");
        return Graph(cells);
    }

    private static DocumentGraph Graph(IReadOnlyList<DocumentNode> cells) =>
        new(DocumentGraph.CurrentSchemaVersion, "doc_merged_header", DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-Plan", 0, cells)]);

    private static DocumentNode TextCell(string address, string text, int order) => new(
        "c_" + address.ToLowerInvariant(), NodeKind.Cell, null, order, ContentLayer.Body, new TextNodeContent(text),
        new SourceAnchor("xlsx", "/xl/worksheets/sheet1.xml", [new AnchorLocator("cell_address", address)]));

    private static string AddressOf(DocumentNode cell) =>
        cell.Source!.Locators.First(locator => locator.Kind == "cell_address").Value;

    private sealed record CellValue(string Text, string? Formula);

    private static IReadOnlyDictionary<string, CellValue> ReadCells(string path)
    {
        using var stream = File.OpenRead(path);
        var extraction = new XlsxAdapter().Extract(stream);
        return extraction.Graph.Partitions.SelectMany(partition => partition.Nodes
                .Where(node => node.Kind == NodeKind.Cell)
                .Select(node => (Key: partition.Id == "sheet-Plan" ? AddressOf(node) : partition.Id + "!" + AddressOf(node),
                    Value: new CellValue(((TextNodeContent)node.Content).Text,
                        node.Extensions is not null && node.Extensions.TryGetValue("formula", out var formula) ? formula.GetString() : null))))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> SheetTableRanges(string projection, string partitionId)
    {
        var begin = projection.IndexOf("<!--drmd:partition-begin id=" + partitionId + " ", StringComparison.Ordinal);
        Assert.True(begin >= 0, "Partition " + partitionId + " was not found in the projection.");
        var end = projection.IndexOf("<!--drmd:partition-end id=" + partitionId + " ", begin, StringComparison.Ordinal);
        return Regex.Matches(projection[begin..end], @"<!--drmd:sheet-table range=(?<range>[A-Z]+\d+:[A-Z]+\d+) ")
            .Select(match => match.Groups["range"].Value).ToArray();
    }

    private static void AssertPairwiseDisjoint(IReadOnlyList<string> ranges, string? context = null)
    {
        var boxes = ranges.Select(ParseRange).ToArray();
        for (var first = 0; first < boxes.Length; first++)
            for (var second = first + 1; second < boxes.Length; second++)
                Assert.False(
                    boxes[first].MinColumn <= boxes[second].MaxColumn && boxes[second].MinColumn <= boxes[first].MaxColumn &&
                    boxes[first].MinRow <= boxes[second].MaxRow && boxes[second].MinRow <= boxes[first].MaxRow,
                    $"{context}: sheet-table ranges {ranges[first]} and {ranges[second]} overlap ({string.Join(", ", ranges)}).");
    }

    private static (int MinColumn, int MinRow, int MaxColumn, int MaxRow) ParseRange(string range)
    {
        var match = Regex.Match(range, @"^(?<c1>[A-Z]+)(?<r1>\d+):(?<c2>[A-Z]+)(?<r2>\d+)$");
        Assert.True(match.Success, range);
        return (ColumnNumber(match.Groups["c1"].Value), int.Parse(match.Groups["r1"].Value),
            ColumnNumber(match.Groups["c2"].Value), int.Parse(match.Groups["r2"].Value));
    }

    private static int ColumnNumber(string name) => name.Aggregate(0, (current, letter) => current * 26 + letter - 'A' + 1);

    private static string ColumnName(int number)
    {
        var name = string.Empty;
        for (; number > 0; number = (number - 1) / 26) name = (char)('A' + (number - 1) % 26) + name;
        return name;
    }

    private static int RowOf(string reference) => int.Parse(new string(reference.SkipWhile(char.IsLetter).ToArray()));

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        for (var index = text.IndexOf(token, StringComparison.Ordinal); index >= 0; index = text.IndexOf(token, index + token.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static int ColumnOf(string reference) => ColumnNumber(new string(reference.TakeWhile(char.IsLetter).ToArray()));

    private static string ReplaceOnce(string text, string oldValue, string newValue, string? context = null)
    {
        var first = text.IndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(first >= 0, $"{context}: '{oldValue}' was not found in the projection.");
        Assert.True(text.IndexOf(oldValue, first + oldValue.Length, StringComparison.Ordinal) < 0,
            $"{context}: '{oldValue}' occurs more than once in the projection.");
        return string.Concat(text.AsSpan(0, first), newValue, text.AsSpan(first + oldValue.Length));
    }

    private static IReadOnlyList<string> ZipPartNames(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray();
    }

    private static byte[] ReadPart(string path, string name)
    {
        using var archive = ZipFile.OpenRead(path);
        using var stream = (archive.GetEntry(name) ?? throw new InvalidOperationException($"Entry '{name}' not found in '{path}'.")).Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "docredock-xlsx-merged-header-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
