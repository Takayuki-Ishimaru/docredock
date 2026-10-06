using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Markdown;

namespace DocRedock.Tests.Api;

/// <summary>
/// The opt-in row blocks of an AI package: a worksheet table too large for one part is cut between
/// rows, every block starts with the header, no row is lost, repeated or reordered, the blocks name
/// their original cell ranges and chain to each other, and nothing changes when the option is off.
/// </summary>
public sealed class AiPackageTableRowBlockTests
{
    private const string Source = "book.xlsx";

    [Fact]
    public void Off_by_default_a_large_sheet_stays_one_part()
    {
        var graph = Sheet(Table(1, 300));
        var parts = AiPackageContentBuilder.Build(graph, new(), Source, 2000);
        var part = Assert.Single(parts);
        Assert.True(part.ExceedsTarget);
        Assert.Null(part.TableBlock);
        Assert.Equal(parts.Select(item => item.Markdown), AiPackageContentBuilder.Build(graph, new(), Source,
            new AiPackagePartOptions(2000, TableRowBlocks: false)).Select(item => item.Markdown));
    }

    [Fact]
    public void A_sheet_that_fits_is_unchanged_with_row_blocks_on()
    {
        var graph = Sheet(Table(1, 5));
        var plain = AiPackageContentBuilder.Build(graph, new(), Source, 2000);
        var blocks = AiPackageContentBuilder.Build(graph, new(), Source, new AiPackagePartOptions(2000, TableRowBlocks: true));
        Assert.Equal(plain.Select(part => (part.Markdown, part.TableBlock)), blocks.Select(part => (part.Markdown, part.TableBlock)));
        Assert.Equal(plain.Single().NodeIds, blocks.Single().NodeIds);
    }

    [Fact]
    public void Blocks_keep_every_row_once_in_order_under_a_repeated_header_and_within_the_target()
    {
        var graph = Sheet(Table(1, 300));
        var whole = new ReadableMarkdownSerializer().Serialize(graph);
        var parts = AiPackageContentBuilder.Build(graph, new(), Source, new AiPackagePartOptions(2000, TableRowBlocks: true));
        Assert.True(parts.Count >= 3);
        var header = "| Item | Q1 | Q2 |\n| --- | --- | --- |\n";
        var rows = new List<string>();
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            Assert.False(part.ExceedsTarget, part.Markdown.Length.ToString());
            Assert.Contains(header, part.Markdown, StringComparison.Ordinal);
            Assert.Single(part.Markdown.Split('\n'), line => line == "| Item | Q1 | Q2 |");
            rows.AddRange(part.Markdown.Split('\n').Where(line => line.StartsWith("| Item ", StringComparison.Ordinal) && line != "| Item | Q1 | Q2 |"));
            var block = part.TableBlock!;
            Assert.Equal(("table-0001", index + 1, parts.Count, "A1:C1"), (block.TableId, block.Index, block.Count, block.HeaderRange));
            Assert.Equal(index == 0 ? null : parts[index - 1].Id, block.PreviousPart);
            Assert.Equal(index == parts.Count - 1 ? null : parts[index + 1].Id, block.NextPart);
            Assert.Contains($"table_block: table-0001 {index + 1}/{parts.Count}; rows {block.RowRange}; header A1:C1{(index > 0 ? " (repeated)" : string.Empty)}",
                part.Markdown, StringComparison.Ordinal);
        }
        Assert.Equal(whole.Split('\n').Where(line => line.StartsWith("| Item ", StringComparison.Ordinal) && line != "| Item | Q1 | Q2 |"), rows);
        // Row ranges are contiguous and together cover the table exactly.
        var ranges = parts.Select(part => part.TableBlock!.RowRange.Split(':')).Select(range => (int.Parse(range[0][1..]), int.Parse(range[1][1..]))).ToArray();
        Assert.Equal(2, ranges[0].Item1);
        Assert.Equal(301, ranges[^1].Item2);
        for (var index = 1; index < ranges.Length; index++) Assert.Equal(ranges[index - 1].Item2 + 1, ranges[index].Item1);
        Assert.All(parts, part => Assert.StartsWith("A", part.TableBlock!.RowRange, StringComparison.Ordinal));
        Assert.All(parts, part => Assert.Contains(":C", part.TableBlock!.RowRange, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_cell_belongs_to_exactly_one_block_and_header_cells_to_every_block()
    {
        var graph = Sheet(Table(1, 200));
        var parts = AiPackageContentBuilder.Build(graph, new(), Source, new AiPackagePartOptions(1500, TableRowBlocks: true));
        var header = new[] { "A1", "B1", "C1" }.Select(address => "cell-" + address).ToArray();
        foreach (var part in parts) Assert.Subset(part.NodeIds.ToHashSet(), header.ToHashSet());
        var data = parts.SelectMany(part => part.NodeIds).Where(id => !header.Contains(id)).ToArray();
        Assert.Equal(data.Length, data.Distinct().Count());
        Assert.Equal(graph.Nodes.Select(node => node.Id).Except(header).Order(), data.Order());
        Assert.All(parts, part => Assert.Equal(part.NodeIds.Count, part.Sources.Count));
        Assert.All(parts, part => Assert.All(part.Sources, source => Assert.Equal("Data", source.SheetName)));
        var first = parts[0];
        Assert.Equal("A1", first.Sources.First(source => source.NodeId == "cell-A1").CellAddress);
    }

    [Fact]
    public void Text_around_a_cut_table_stays_with_its_first_and_last_block_and_two_cut_tables_never_share_a_part()
    {
        var nodes = new List<DocumentNode> { Cell("A1", "Quarterly report", 0) };
        nodes.AddRange(Table(3, 120, prefix: "First"));
        nodes.Add(Cell("A125", "Note: provisional figures", 0));
        nodes.AddRange(Table(131, 120, prefix: "Second"));
        var graph = Sheet(nodes.ToArray());
        var parts = AiPackageContentBuilder.Build(graph, new(), Source, new AiPackagePartOptions(1500, TableRowBlocks: true));
        Assert.Contains("Quarterly report", parts[0].Markdown, StringComparison.Ordinal);
        var firstTable = parts.Where(part => part.TableBlock?.TableId == "table-0001").ToArray();
        var secondTable = parts.Where(part => part.TableBlock?.TableId == "table-0002").ToArray();
        Assert.True(firstTable.Length >= 2 && secondTable.Length >= 2);
        Assert.Empty(firstTable.Intersect(secondTable));
        Assert.DoesNotContain(parts, part => part.Markdown.Contains("| First ", StringComparison.Ordinal) && part.Markdown.Contains("| Second ", StringComparison.Ordinal));
        var note = Assert.Single(parts, part => part.Markdown.Contains("Note: provisional figures", StringComparison.Ordinal));
        Assert.Contains("cell-A125", note.NodeIds);
        Assert.Equal(parts.Count, parts.Select(part => part.Id).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, parts.Count).Select(number => $"part-{number:D4}"), parts.Select(part => part.Id));
    }

    [Fact]
    public void A_title_between_two_cut_tables_goes_with_the_table_it_introduces()
    {
        var nodes = new List<DocumentNode>();
        nodes.AddRange(Table(1, 120, prefix: "First"));
        nodes.Add(Cell("A122", "Note: first table is provisional", 0));
        nodes.Add(Cell("A128", "2. Costs", 0));
        nodes.AddRange(Table(129, 120, prefix: "Second"));
        var parts = AiPackageContentBuilder.Build(Sheet(nodes.ToArray()), new(), Source, new AiPackagePartOptions(1500, TableRowBlocks: true));
        // The title introduces the second table: block 1 starts with it and later blocks repeat it.
        var titled = parts.Where(part => part.Markdown.Contains("2. Costs", StringComparison.Ordinal)).ToArray();
        Assert.Equal(parts.Where(part => part.TableBlock?.TableId == "table-0002"), titled);
        Assert.Equal(1, titled[0].TableBlock!.Index);
        Assert.All(titled, title =>
        {
            Assert.True(title.Markdown.IndexOf("2. Costs", StringComparison.Ordinal) < title.Markdown.IndexOf("| Item | Q1 | Q2 |", StringComparison.Ordinal));
            Assert.Equal("A128:A128", title.TableBlock!.ContextRange);
            Assert.Contains("cell-A128", title.NodeIds);
        });
        // The note is nearer the first table's last row: it stays there and is not repeated anywhere.
        var note = Assert.Single(parts, part => part.Markdown.Contains("Note: first table is provisional", StringComparison.Ordinal));
        Assert.Equal("table-0001", note.TableBlock!.TableId);
        Assert.Equal(note.TableBlock.Count, note.TableBlock.Index);
        Assert.Contains("cell-A122", note.NodeIds);
        Assert.All(parts.Where(part => part.TableBlock?.TableId == "table-0001"), part => Assert.Null(part.TableBlock!.ContextRange));
    }

    // v0.3.2 evaluation B: a block read alone must still say what its numbers mean. The cell text just
    // above a cut table (title, unit, conditions) is repeated, as written, before the header of every
    // later block, and the block names the cells it came from.
    [Fact]
    public void Later_blocks_repeat_the_title_unit_and_conditions_above_the_table_and_name_their_cells()
    {
        var nodes = new List<DocumentNode>
        {
            Cell("A1", "2025年度 営業報告", 0), Cell("A3", "売上実績（単位: 千円、税抜）", 0), Cell("A4", "対象: 国内全店", 0),
            Cell("A5", "商品", 0, bold: true), Cell("B5", "売上", 0, bold: true),
        };
        for (var index = 1; index <= 300; index++)
        {
            nodes.Add(Cell($"A{5 + index}", $"商品{index}", 0));
            nodes.Add(Cell($"B{5 + index}", (index * 10).ToString(System.Globalization.CultureInfo.InvariantCulture), 0, numeric: true));
        }
        var graph = Sheet(nodes.ToArray());
        var whole = new ReadableMarkdownSerializer().Serialize(graph);
        Assert.Equal(1, Count(whole, "売上実績（単位: 千円、税抜）"));
        var parts = AiPackageContentBuilder.Build(graph, new(), Source, new AiPackagePartOptions(1500, TableRowBlocks: true));
        Assert.True(parts.Count >= 3, parts.Count.ToString());
        var dataRows = new List<string>();
        foreach (var part in parts)
        {
            var block = part.TableBlock!;
            Assert.Equal("A3:A4", block.ContextRange);
            var repeated = block.Index > 1 ? " (repeated)" : string.Empty;
            Assert.Contains($"; header A5:B5{repeated}; context A3:A4{repeated}\n", part.Markdown, StringComparison.Ordinal);
            foreach (var text in new[] { "売上実績（単位: 千円、税抜）", "対象: 国内全店" })
            {
                Assert.Equal(1, Count(part.Markdown, text));
                Assert.True(part.Markdown.IndexOf(text, StringComparison.Ordinal) < part.Markdown.IndexOf("| 商品 | 売上 |", StringComparison.Ordinal));
            }
            Assert.Subset(part.NodeIds.ToHashSet(), new HashSet<string> { "cell-A3", "cell-A4", "cell-A5", "cell-B5" });
            Assert.Equal(part.NodeIds.Count, part.Sources.Count);
            Assert.False(part.ExceedsTarget);
            dataRows.AddRange(part.Markdown.Split('\n').Where(line => line.StartsWith("| 商品", StringComparison.Ordinal) && line != "| 商品 | 売上 |"));
        }
        // Repeating the context never repeats, drops or reorders a data row.
        Assert.Equal(whole.Split('\n').Where(line => line.StartsWith("| 商品", StringComparison.Ordinal) && line != "| 商品 | 売上 |"), dataRows);
        var data = parts.SelectMany(part => part.NodeIds).Where(id => id is not ("cell-A3" or "cell-A4" or "cell-A5" or "cell-B5")).ToArray();
        Assert.Equal(data.Length, data.Distinct().Count());
    }

    [Fact]
    public void Long_text_another_table_or_an_image_above_a_cut_table_is_not_repeated()
    {
        var longText = string.Concat(Enumerable.Repeat("Long explanation of the figures below. ", 12));
        var cases = new Dictionary<string, DocumentNode[]>
        {
            ["long"] = [Cell("A1", "Report", 0), Cell("A3", longText, 0)],
            ["table"] = [Cell("A1", "Report", 0), Cell("A3", "Code", 0, bold: true), Cell("B3", "Name", 0, bold: true),
                Cell("A4", "X1", 0), Cell("B4", "First", 0), Cell("A5", "X2", 0), Cell("B5", "Second", 0)],
        };
        foreach (var (name, above) in cases)
        {
            var parts = AiPackageContentBuilder.Build(Sheet([.. above, .. Table(8, 150)]), new(), Source, new AiPackagePartOptions(1500, TableRowBlocks: true));
            var blocks = parts.Where(part => part.TableBlock is not null).ToArray();
            Assert.True(blocks.Length >= 2, name);
            Assert.All(blocks, part => Assert.Null(part.TableBlock!.ContextRange));
            Assert.All(blocks.Skip(1), part => Assert.DoesNotContain(name == "long" ? "Long explanation" : "| Code |", part.Markdown, StringComparison.Ordinal));
            Assert.DoesNotContain("; context", string.Concat(parts.Select(part => part.Markdown)), StringComparison.Ordinal);
        }
        var picture = new DocumentNode("picture", NodeKind.Image, null, 750, ContentLayer.Body, new ReferenceNodeContent("assets/chart.png", "Chart"),
            Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["row"] = JsonSerializer.SerializeToElement(7) });
        var withImage = AiPackageContentBuilder.Build(Sheet([Cell("A1", "Report", 0), picture, .. Table(8, 150)]), new(), Source,
            new AiPackagePartOptions(1500, TableRowBlocks: true));
        Assert.All(withImage.Where(part => part.TableBlock is not null), part => Assert.Null(part.TableBlock!.ContextRange));
        Assert.Single(withImage, part => part.Markdown.Contains("assets/chart.png", StringComparison.Ordinal));
    }

    // Inputs from the independent review of the repeated context (2026-10-07).
    [Fact]
    public void A_note_that_belongs_to_the_previous_cut_table_is_never_repeated_with_the_next()
    {
        var nodes = new List<DocumentNode>();
        nodes.AddRange(Table(1, 102, prefix: "First"));
        nodes.Add(Cell("A105", "Note: first table is provisional", 0));
        nodes.Add(Cell("A111", "2. Costs", 0));
        nodes.AddRange(Table(112, 120, prefix: "Second"));
        var parts = AiPackageContentBuilder.Build(Sheet(nodes.ToArray()), new(), Source, new AiPackagePartOptions(1500, TableRowBlocks: true));
        var second = parts.Where(part => part.TableBlock?.TableId == "table-0002").ToArray();
        Assert.True(second.Length >= 2);
        Assert.All(second, part => Assert.Equal("A111:A111", part.TableBlock!.ContextRange));
        Assert.All(second, part => Assert.DoesNotContain("Note: first table is provisional", part.Markdown, StringComparison.Ordinal));
        Assert.Single(parts, part => part.Markdown.Contains("Note: first table is provisional", StringComparison.Ordinal));
    }

    [Fact]
    public void Repeated_context_never_holds_a_data_row_DocRedock_notes_or_text_far_above_the_table()
    {
        // Category rows above a headerless table are not captions: no data row becomes a repeated header.
        var stock = new List<DocumentNode> { Cell("A1", "在庫表", 0), Cell("A3", "食品", 0), Cell("A4", "野菜", 0) };
        for (var row = 5; row <= 300; row++)
            stock.AddRange([Cell($"A{row}", $"品目{row}", 0), Cell($"B{row}", (row * 10).ToString(System.Globalization.CultureInfo.InvariantCulture), 0, numeric: true),
                Cell($"C{row}", (row * 3).ToString(System.Globalization.CultureInfo.InvariantCulture), 0, numeric: true)]);
        var parts = AiPackageContentBuilder.Build(Sheet(stock.ToArray()), new(), Source, new AiPackagePartOptions(1500, TableRowBlocks: true));
        Assert.True(parts.Count >= 3);
        for (var row = 5; row <= 300; row++)
            Assert.Equal(1, parts.Sum(part => Count(part.Markdown, $"| 品目{row} |")));
        // A label/value block DocRedock formed above a table is not the table's title.
        var form = new List<DocumentNode> { Cell("A1", "作成者", 0), Cell("B1", "山田", 0), Cell("A2", "部署", 0), Cell("B2", "営業部", 0) };
        form.AddRange(Table(4, 200));
        parts = AiPackageContentBuilder.Build(Sheet(form.ToArray()), new(), Source, new AiPackagePartOptions(1500, TableRowBlocks: true));
        Assert.All(parts.Where(part => part.TableBlock is not null), part => Assert.Null(part.TableBlock!.ContextRange));
        Assert.Single(parts, part => part.Markdown.Contains("<!-- inferred", StringComparison.Ordinal));
        // A list several rows above is not directly above the table.
        var list = new List<DocumentNode> { Cell("A1", "Report", 0), Cell("A2", "East", 0), Cell("A3", "West", 0), Cell("A4", "North", 0) };
        list.AddRange(Table(9, 200));
        parts = AiPackageContentBuilder.Build(Sheet(list.ToArray()), new(), Source, new AiPackagePartOptions(1500, TableRowBlocks: true));
        Assert.All(parts.Where(part => part.TableBlock is not null), part => Assert.Null(part.TableBlock!.ContextRange));
        Assert.Single(parts, part => part.Markdown.Contains("North", StringComparison.Ordinal));
        // A merged title above the table names its whole merge.
        var merged = new List<DocumentNode> { Cell("A1", "Report", 0), Cell("A3", "Sales (thousands)", 0, toColumn: 3) };
        merged.AddRange(Table(5, 200));
        parts = AiPackageContentBuilder.Build(Sheet(merged.ToArray()), new(), Source, new AiPackagePartOptions(1500, TableRowBlocks: true));
        Assert.All(parts, part => Assert.Equal("A3:C3", part.TableBlock!.ContextRange));
        Assert.All(parts, part => Assert.Equal(1, Count(part.Markdown, "Sales (thousands)")));
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    [Fact]
    public void A_target_smaller_than_the_fixed_lines_still_makes_blocks_of_many_rows()
    {
        var graph = Sheet(Table(1, 600));
        var parts = AiPackageContentBuilder.Build(graph, new(), Source, new AiPackagePartOptions(128, TableRowBlocks: true));
        Assert.InRange(parts.Count, 2, 60);
        Assert.All(parts, part => Assert.True(part.ExceedsTarget));
        var rows = parts.SelectMany(part => part.Markdown.Split('\n')).Count(line => line.StartsWith("| Item ", StringComparison.Ordinal) && line != "| Item | Q1 | Q2 |");
        Assert.Equal(600, rows);
    }

    [Fact]
    public void A_sheet_that_fits_only_without_its_source_line_is_still_cut()
    {
        var graph = Sheet(Table(1, 60));
        var length = new ReadableMarkdownSerializer().Serialize(graph).Trim().Length;
        var parts = AiPackageContentBuilder.Build(graph, new(), Source, new AiPackagePartOptions(length + 5, TableRowBlocks: true));
        Assert.True(parts.Count > 1);
        Assert.All(parts, part => Assert.False(part.ExceedsTarget));
    }

    [Fact]
    public void Overlay_markers_and_image_text_belong_to_the_parts_that_show_them()
    {
        var nodes = Table(1, 150).ToList();
        nodes.Add(new DocumentNode("overlay", NodeKind.Shape, null, 900_000, ContentLayer.Hidden, new TextNodeContent("→"),
            Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["sheet_state"] = JsonSerializer.SerializeToElement("visible"),
                ["sheet_overlay"] = JsonSerializer.SerializeToElement(new
                { ShapeId = "1", Text = "", Kind = "arrow", Direction = "right", Axis = "horizontal",
                    StartRow = 140, EndRow = 140, StartColumn = 2, EndColumn = 3 }),
            }));
        nodes.Add(new DocumentNode("picture", NodeKind.Image, null, 950_000, ContentLayer.Body,
            new ReferenceNodeContent("assets/picture.png", "Picture"),
            Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["row"] = JsonSerializer.SerializeToElement(60) }));
        nodes.Add(new DocumentNode("picture-text", NodeKind.ImageText, "picture", 950_001, ContentLayer.Derived, new TextNodeContent("CAPTION")));
        var parts = AiPackageContentBuilder.Build(Sheet(nodes.ToArray()), new(), Source, new AiPackagePartOptions(1500, TableRowBlocks: true));
        var marked = Assert.Single(parts, part => part.NodeIds.Contains("overlay"));
        Assert.Contains("| Item 139 |", marked.Markdown, StringComparison.Ordinal);
        Assert.Single(parts, part => part.NodeIds.Contains("picture-text"));
        Assert.Single(parts, part => part.NodeIds.Contains("picture"));
    }

    [Fact]
    public void Row_blocks_are_deterministic_and_number_parts_after_earlier_sheets()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "book", DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-Intro", 0, [Cell("A1", "Intro", 0)]), new DocumentPartition("sheet-Data", 1, Table(1, 150))]);
        var options = new AiPackagePartOptions(1500, TableRowBlocks: true);
        var first = AiPackageContentBuilder.Build(graph, new(), Source, options);
        var second = AiPackageContentBuilder.Build(graph, new(), Source, options);
        Assert.Equal(first.Select(part => (part.Id, part.Markdown)), second.Select(part => (part.Id, part.Markdown)));
        Assert.Null(first[0].TableBlock);
        Assert.Equal("part-0002", first[1].Id);
        Assert.Equal("part-0003", first[1].TableBlock!.NextPart);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    [InlineData("売上表", 3)]
    [InlineData("| 売上 | 100 |", 2 + 3)]
    [InlineData("😀", 1)]
    public void Token_estimate_counts_ascii_by_four_and_other_characters_one_each(string text, int expected) =>
        Assert.Equal(expected, AiTokenEstimate.Estimate(text));

    private static DocumentNode[] Table(int firstRow, int dataRows, string prefix = "Item")
    {
        var nodes = new List<DocumentNode>
        {
            Cell($"A{firstRow}", "Item", 0, bold: true), Cell($"B{firstRow}", "Q1", 0, bold: true), Cell($"C{firstRow}", "Q2", 0, bold: true),
        };
        for (var index = 1; index <= dataRows; index++)
        {
            var row = firstRow + index;
            nodes.Add(Cell($"A{row}", $"{prefix} {index}", 0));
            nodes.Add(Cell($"B{row}", (index * 10).ToString(System.Globalization.CultureInfo.InvariantCulture), 0, numeric: true));
            nodes.Add(Cell($"C{row}", (index * 20).ToString(System.Globalization.CultureInfo.InvariantCulture), 0, numeric: true));
        }
        return nodes.ToArray();
    }

    private static DocumentGraph Sheet(params DocumentNode[] nodes) =>
        new(DocumentGraph.CurrentSchemaVersion, "book", DocumentFormatKind.Xlsx, [new DocumentPartition("sheet-Data", 0, nodes)]);

    private static DocumentNode Cell(string address, string text, int order, bool bold = false, bool numeric = false, int toColumn = 0)
    {
        var column = address.TakeWhile(char.IsLetter).Aggregate(0, (sum, letter) => sum * 26 + letter - 'A' + 1);
        var row = int.Parse(address[address.TakeWhile(char.IsLetter).Count()..], System.Globalization.CultureInfo.InvariantCulture);
        return new DocumentNode("cell-" + address, NodeKind.Cell, null, row * 100 + column, ContentLayer.Body, new TextNodeContent(text),
            new SourceAnchor("xlsx", "/xl/worksheets/sheet1.xml", [new AnchorLocator("cell_address", address)]),
            Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["row"] = JsonSerializer.SerializeToElement(row),
                ["column"] = JsonSerializer.SerializeToElement(column),
                ["is_bold"] = JsonSerializer.SerializeToElement(bold),
                ["is_numeric"] = JsonSerializer.SerializeToElement(numeric),
            }.Concat(toColumn > 0 ? [new KeyValuePair<string, JsonElement>("merged_to_column", JsonSerializer.SerializeToElement(toColumn))] : [])
            .ToDictionary(StringComparer.Ordinal));
    }
}
