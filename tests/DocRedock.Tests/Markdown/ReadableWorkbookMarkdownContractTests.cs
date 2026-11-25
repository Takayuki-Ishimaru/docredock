using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using GfmTable = Markdig.Extensions.Tables.Table;
using GfmRow = Markdig.Extensions.Tables.TableRow;
using GfmCell = Markdig.Extensions.Tables.TableCell;

namespace DocRedock.Tests.Markdown;

public sealed class ReadableWorkbookMarkdownContractTests
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UsePipeTables().UseMathematics().Build();

    [Theory]
    [InlineData("0")]
    [InlineData("FALSE")]
    [InlineData("#DIV/0!")]
    [InlineData("2026-11-18")]
    public void Default_formula_output_keeps_cached_value_without_formula(string value)
    {
        var markdown = Serialize(FormulaGraph(value, "ROW()-$BH$18", hasCache: true));
        var cell = DataCell(Parse(markdown), 1, 1);

        Assert.Equal(value, Text(cell));
        Assert.Empty(((MarkdownObject)cell).Descendants<CodeInline>());
        Assert.DoesNotContain("ROW()", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("$BH$18", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ROW()", false)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void Master_and_shared_formula_without_cache_keep_their_cells(
        string? formula, bool sharedCopy)
    {
        var graph = FormulaGraph("", formula, sharedCopy, hasCache: false);

        foreach (var show in new[] { false, true })
        {
            var markdown = Serialize(graph, new(ShowFormulas: show));
            var document = Parse(markdown);
            var table = Assert.Single(Tables(document));

            Assert.Equal(2, table.Count);
            Assert.Equal(2, Assert.IsType<GfmRow>(table[1]).Count);
            Assert.Equal("entry", Text(DataCell(document, 1, 0)));
            Assert.Contains("保存済み計算値なし", Text(DataCell(document, 1, 1)),
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("IF(TRUE,\"\",\"\")", false)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void Present_empty_string_cache_is_a_blank_result_not_a_missing_cache(
        string? formula, bool sharedCopy)
    {
        var graph = FormulaGraph("", formula, sharedCopy, hasCache: true);
        var markdown = Serialize(graph);
        var document = Parse(markdown);
        var table = Assert.Single(Tables(document));

        Assert.Equal(2, table.Count);
        Assert.Equal(2, Assert.IsType<GfmRow>(table[1]).Count);
        Assert.Equal("entry", Text(DataCell(document, 1, 0)));
        Assert.Equal("", Text(DataCell(document, 1, 1)));
        Assert.DoesNotContain("保存済み計算値なし", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ROW()-$BH$18")]
    [InlineData("IF(A1=\"$x$\",1,0)")]
    [InlineData("IF(A1=\"a`b|c\\d\",\"*x*\",\"_y_\")")]
    [InlineData("IF(A1=\"``\",1,0)")]
    public void Formula_is_one_code_span_even_with_math_backticks_pipes_and_backslashes(
        string formula)
    {
        foreach (var recordLayout in new[] { false, true })
        {
            var serializer = new ReadableMarkdownSerializer(new(
                ShowFormulas: true, TableLayout: MarkdownTableLayout.Grid))
            {
                RecordWorkbookLayout = recordLayout
            };
            var markdown = serializer.Serialize(
                FormulaGraph("7", formula, hasCache: true));
            var document = Parse(markdown);
            var code = Assert.Single(((MarkdownObject)document).Descendants<CodeInline>());

            Assert.Equal("=" + formula.TrimStart('='), code.Content.ToString());
            if (formula.Contains('|'))
            {
                // GFM and Markdig disagree on pipe escaping inside table code.
                // A record outside the table preserves the exact expression in both.
                Assert.Empty(Tables(document));
                var displayed = MarkdownStructure.DisplayedText(document);
                Assert.Contains("entry", displayed, StringComparison.Ordinal);
                Assert.Contains("→ 7", displayed, StringComparison.Ordinal);
                Assert.Contains("項目", displayed, StringComparison.Ordinal);
                Assert.Contains("値", displayed, StringComparison.Ordinal);
            }
            else
            {
                var cell = DataCell(document, 1, 1);
                Assert.Single(((MarkdownObject)cell).Descendants<CodeInline>());
                Assert.Contains("→ 7", Text(cell), StringComparison.Ordinal);
            }
            Assert.Empty(((MarkdownObject)document).Descendants<MathInline>());
            Assert.Empty(((MarkdownObject)document).Descendants<MathBlock>());
        }
    }

    [Theory]
    [InlineData(MarkdownTableLayout.Grid)]
    [InlineData(MarkdownTableLayout.Records)]
    public void Literal_cell_syntax_is_preserved_as_text(MarkdownTableLayout layout)
    {
        const string literal = "a`b c\\d | *e* _f_ <g> [ref](url) $BH$18";
        var graph = FormulaGraph(literal, null, hasCache: false);
        var markdown = Serialize(graph, new(TableLayout: layout));
        var document = Parse(markdown);

        Assert.Contains(literal, MarkdownStructure.DisplayedText(document),
            StringComparison.Ordinal);
        Assert.Empty(((MarkdownObject)document).Descendants<CodeInline>());
        Assert.Empty(((MarkdownObject)document).Descendants<MathInline>());
        Assert.Empty(MarkdownStructure.Links(document));
        Assert.Empty(MarkdownStructure.Images(document));
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void Auto_uses_grid_for_ten_columns_and_records_above_ten(
        int columns, bool expectedGrid)
    {
        var graph = WideGraph(columns);
        var markdown = Serialize(graph, new(TableLayout: MarkdownTableLayout.Auto));
        var document = Parse(markdown);

        Assert.Equal(expectedGrid ? 1 : 0, Tables(document).Length);
        AssertEveryValue(document, columns, dataRows: 2);
    }

    [Fact]
    public void Explicit_grid_keeps_a_seventeen_column_table()
    {
        var graph = WideGraph(17);
        var markdown = Serialize(graph, new(TableLayout: MarkdownTableLayout.Grid));
        var document = Parse(markdown);
        var table = Assert.Single(Tables(document));

        Assert.Equal(3, table.Count);
        Assert.All(table, row => Assert.Equal(17, Assert.IsType<GfmRow>(row).Count));
        AssertEveryValue(document, 17, dataRows: 2);
    }

    [Fact]
    public void Records_keep_all_field_names_values_and_multiline_text()
    {
        var graph = WideGraph(17);
        var changed = graph.Partitions[0].Nodes.Select(node =>
            node.Extensions!["row"].GetInt32() == 2 &&
            node.Extensions!["column"].GetInt32() == 17
                ? node with { Content = new TextNodeContent("first line\nsecond line") }
                : node).ToArray();
        graph = graph with
        {
            Partitions = [graph.Partitions[0] with { Nodes = changed }]
        };

        var markdown = Serialize(graph, new(TableLayout: MarkdownTableLayout.Records));
        var document = Parse(markdown);
        var displayed = MarkdownStructure.DisplayedText(document);

        Assert.Empty(Tables(document));
        for (var column = 1; column <= 17; column++)
            Assert.Contains(Header(column), displayed, StringComparison.Ordinal);
        for (var column = 1; column < 17; column++)
            Assert.Contains(Value(1, column), displayed, StringComparison.Ordinal);
        for (var column = 1; column <= 17; column++)
            Assert.Contains(Value(2, column), displayed, StringComparison.Ordinal);
        var code = Assert.Single(((MarkdownObject)document).Descendants<FencedCodeBlock>());
        Assert.Equal("first line\nsecond line", code.Lines.ToString().TrimEnd('\n', '\r'));
        Assert.DoesNotContain("<br>", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Source_references_name_the_exact_sheet_and_table_range()
    {
        var graph = WideGraph(2, sheet: "仕様_α");
        var markdown = Serialize(graph, new(
            TableLayout: MarkdownTableLayout.Grid,
            IncludeSourceReferences: true,
            SourceName: "Book_[v1].xlsx"));
        var displayed = MarkdownStructure.DisplayedText(Parse(markdown));

        Assert.Contains("Book_[v1].xlsx", displayed, StringComparison.Ordinal);
        Assert.Contains("仕様_α", displayed, StringComparison.Ordinal);
        Assert.Contains("A1:B3", displayed, StringComparison.Ordinal);
    }

    [Fact]
    public void Records_name_each_original_row_range()
    {
        var markdown = Serialize(WideGraph(17), new(
            TableLayout: MarkdownTableLayout.Records,
            IncludeSourceReferences: true,
            SourceName: "book.xlsx"));
        var displayed = MarkdownStructure.DisplayedText(Parse(markdown));

        Assert.Contains("A2:Q2", displayed, StringComparison.Ordinal);
        Assert.Contains("A3:Q3", displayed, StringComparison.Ordinal);
    }

    [Fact]
    public void Contents_and_sources_do_not_reveal_excluded_sheets_or_cells()
    {
        var visible = WideGraph(2, sheet: "公開");
        var secret = WideGraph(2, sheet: "秘密").Partitions[0];
        secret = secret with
        {
            Nodes = secret.Nodes.Select(node => node with
            {
                Layer = ContentLayer.Hidden,
                Content = new TextNodeContent("SECRET_VALUE")
            }).ToArray()
        };
        var hiddenCell = Cell("公開", 5, 1, "HIDDEN_CELL") with
        {
            Layer = ContentLayer.Hidden
        };
        var graph = visible with
        {
            Partitions =
            [
                visible.Partitions[0] with
                {
                    Nodes = [.. visible.Partitions[0].Nodes, hiddenCell]
                },
                secret with { Order = 1 }
            ]
        };

        var markdown = Serialize(graph, new(
            ContentPolicy: "visible",
            IncludeSourceReferences: true,
            IncludeTableOfContents: true,
            SourceName: "book.xlsx"));

        Assert.DoesNotContain("秘密", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_VALUE", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDEN_CELL", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("A5", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Contents_links_are_deterministic_and_resolve_despite_humanized_name_collisions()
    {
        var names = new[] { "Alpha_A", "Alpha A", "第二節" };
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion,
            "contents", DocumentFormatKind.Xlsx,
            names.Select((name, index) =>
                WideGraph(2, sheet: name).Partitions[0] with { Order = index }).ToArray());
        var options = new ReadableMarkdownOptions(IncludeTableOfContents: true);
        var markdown = Serialize(graph, options);
        var links = MarkdownStructure.Links(Parse(markdown))
            .Where(link => link.Url?.StartsWith('#') == true).ToArray();
        var anchors = Regex.Matches(markdown,
                @"\bid\s*=\s*[""'](?<id>[^""']+)[""']")
            .Select(match => WebUtility.HtmlDecode(match.Groups["id"].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(3, links.Length);
        Assert.Equal(3, links.Select(link => link.Url).Distinct().Count());
        Assert.All(links, link =>
            Assert.Contains(Uri.UnescapeDataString(link.Url![1..]), anchors));
        Assert.Equal(markdown, Serialize(graph, options));
    }

    [Fact]
    public void Grid_layout_recording_keeps_exact_header_and_data_offsets()
    {
        var serializer = new ReadableMarkdownSerializer(new(
            TableLayout: MarkdownTableLayout.Grid,
            IncludeSourceReferences: true,
            IncludeTableOfContents: true,
            SourceName: "book.xlsx"))
        {
            RecordWorkbookLayout = true
        };
        var markdown = serializer.Serialize(WideGraph(17));
        var sheet = Assert.Single(serializer.WorkbookLayout!.Sheets);
        var segment = Assert.Single(sheet.Segments, item => item.Table is not null);
        var table = segment.Table!;

        Assert.Equal(17, table.MaxColumn);
        Assert.Equal(1, table.MinColumn);
        Assert.Equal([1], table.HeaderRows);
        Assert.Equal([2, 3], table.Rows.Select(row => row.Row));
        var header = Parse(markdown[segment.Start..table.HeaderEnd]);
        Assert.Single(Tables(header));
        foreach (var row in table.Rows)
        {
            var rowText = markdown[row.Start..row.End];
            Assert.DoesNotContain('\n', rowText.TrimEnd('\r', '\n'));
            for (var column = 1; column <= 17; column++)
                Assert.Contains(Value(row.Row - 1, column),
                    MarkdownStructure.DisplayedText(Parse(
                        markdown[segment.Start..table.HeaderEnd] + rowText)),
                    StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(MarkdownTableLayout.Auto)]
    [InlineData(MarkdownTableLayout.Records)]
    public void Ai_package_row_blocks_keep_grid_and_every_data_node(
        MarkdownTableLayout requestedLayout)
    {
        var graph = WideGraph(17, dataRows: 90);
        var parts = AiPackageContentBuilder.Build(graph,
            new ReadableMarkdownOptions(TableLayout: requestedLayout),
            "book.xlsx", new AiPackagePartOptions(2000, TableRowBlocks: true));
        var headerIds = graph.Nodes.Where(node =>
            node.Extensions!["row"].GetInt32() == 1)
            .Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var dataIds = graph.Nodes.Where(node =>
            node.Extensions!["row"].GetInt32() > 1)
            .Select(node => node.Id).Order().ToArray();

        Assert.True(parts.Count > 1);
        Assert.All(parts, part =>
        {
            Assert.NotNull(part.TableBlock);
            Assert.Single(Tables(Parse(part.Markdown)));
            Assert.Equal("A1:Q1", part.TableBlock!.HeaderRange);
        });
        Assert.Equal(dataIds, parts.SelectMany(part => part.NodeIds)
            .Where(id => !headerIds.Contains(id)).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ai_package_pipe_formula_falls_back_without_losing_nodes_or_sources(bool rowBlocks)
    {
        const string formula = "IF(A1=\"a|$x$\",1,0)";
        var graph = WideGraph(17, dataRows: 90);
        graph = graph with
        {
            Partitions = [graph.Partitions[0] with
            {
                Nodes = graph.Partitions[0].Nodes.Select(node =>
                {
                    if (node.Extensions!["row"].GetInt32() != 2 ||
                        node.Extensions["column"].GetInt32() != 2) return node;
                    var extensions = node.Extensions.ToDictionary(item => item.Key, item => item.Value);
                    extensions["formula"] = JsonSerializer.SerializeToElement(formula);
                    extensions["is_formula"] = JsonSerializer.SerializeToElement(true);
                    extensions["has_cached_value"] = JsonSerializer.SerializeToElement(true);
                    return node with { Content = new TextNodeContent("7"), Extensions = extensions };
                }).ToArray()
            }]
        };
        var parts = AiPackageContentBuilder.Build(graph,
            new ReadableMarkdownOptions(ShowFormulas: true, TableLayout: MarkdownTableLayout.Grid),
            "book.xlsx", new AiPackagePartOptions(2000, TableRowBlocks: rowBlocks));
        var document = Parse(string.Join("\n\n", parts.Select(part => part.Markdown)));

        Assert.NotEmpty(parts);
        Assert.All(parts, part => Assert.Null(part.TableBlock));
        Assert.Empty(Tables(document));
        var code = Assert.Single(((MarkdownObject)document).Descendants<CodeInline>());
        Assert.Equal("=" + formula, code.Content.ToString());
        Assert.Empty(((MarkdownObject)document).Descendants<MathInline>());
        Assert.Empty(((MarkdownObject)document).Descendants<MathBlock>());
        Assert.Contains("→ 7", MarkdownStructure.DisplayedText(document), StringComparison.Ordinal);
        Assert.Equal(graph.Nodes.Select(node => node.Id).Order(),
            parts.SelectMany(part => part.NodeIds).Order());

        var sources = parts.SelectMany(part => part.Sources).ToArray();
        Assert.Equal(graph.Nodes.Select(node => node.Id).Order(),
            sources.Select(source => source.NodeId).Order());
        foreach (var node in graph.Nodes)
        {
            var source = Assert.Single(sources, item => item.NodeId == node.Id);
            Assert.Equal("Data", source.SheetName);
            Assert.Equal(node.Source!.Locators.Single(locator =>
                locator.Kind == "cell_address").Value, source.CellAddress);
        }
    }

    [Fact]
    public void Public_defaults_keep_low_level_grid_and_enable_readable_export_improvements()
    {
        Assert.Equal(MarkdownTableLayout.Grid, new ReadableMarkdownOptions().TableLayout);
        var options = new ReadableDocumentExportOptions("input.xlsx", "output.md");

        Assert.Equal(MarkdownTableLayout.Auto, options.TableLayout);
        Assert.True(options.IncludeSourceReferences);
        Assert.True(options.IncludeTableOfContents);
    }

    private static string Serialize(DocumentGraph graph, ReadableMarkdownOptions? options = null) =>
        new ReadableMarkdownSerializer(options).Serialize(graph);

    private static MarkdownDocument Parse(string markdown) =>
        global::Markdig.Markdown.Parse(markdown, Pipeline);

    private static GfmTable[] Tables(MarkdownDocument document) =>
        ((MarkdownObject)document).Descendants<GfmTable>().ToArray();

    private static GfmCell DataCell(MarkdownDocument document, int row, int column)
    {
        var table = Assert.Single(Tables(document));
        return Assert.IsType<GfmCell>(Assert.IsType<GfmRow>(table[row])[column]);
    }

    private static string Text(Block block)
    {
        var output = new StringBuilder();
        void Inlines(Inline? first)
        {
            for (var inline = first; inline is not null; inline = inline.NextSibling)
            {
                switch (inline)
                {
                    case LiteralInline literal: output.Append(literal.Content); break;
                    case CodeInline code: output.Append(code.Content); break;
                    case HtmlEntityInline entity: output.Append(entity.Transcoded); break;
                    case LineBreakInline: output.Append('\n'); break;
                    case ContainerInline container: Inlines(container.FirstChild); break;
                }
            }
        }
        void Blocks(Block current)
        {
            if (current is LeafBlock leaf) Inlines(leaf.Inline?.FirstChild);
            else if (current is ContainerBlock container)
                foreach (var child in container) Blocks(child);
        }
        Blocks(block);
        return output.ToString();
    }

    private static void AssertEveryValue(MarkdownDocument document, int columns, int dataRows)
    {
        var displayed = MarkdownStructure.DisplayedText(document);
        for (var row = 1; row <= dataRows; row++)
        for (var column = 1; column <= columns; column++)
            Assert.Contains(Value(row, column), displayed, StringComparison.Ordinal);
    }

    private static DocumentGraph FormulaGraph(string value, string? formula,
        bool sharedCopy = false, bool hasCache = false)
    {
        var result = Cell("Data", 2, 2, value);
        var extensions = result.Extensions!.ToDictionary(item => item.Key, item => item.Value);
        if (formula is not null) extensions["formula"] = JsonSerializer.SerializeToElement(formula);
        if (formula is not null || sharedCopy)
        {
            extensions["is_formula"] = JsonSerializer.SerializeToElement(true);
            extensions["has_cached_value"] = JsonSerializer.SerializeToElement(hasCache);
        }
        result = result with { Extensions = extensions };

        return new(DocumentGraph.CurrentSchemaVersion, "formulas", DocumentFormatKind.Xlsx,
        [
            new DocumentPartition("sheet-Data", 0,
            [
                Cell("Data", 1, 1, "項目", bold: true),
                Cell("Data", 1, 2, "値", bold: true),
                Cell("Data", 2, 1, "entry"), result
            ])
        ]);
    }

    private static DocumentGraph WideGraph(int columns, int dataRows = 2, string sheet = "Data")
    {
        var nodes = new List<DocumentNode>();
        for (var column = 1; column <= columns; column++)
            nodes.Add(Cell(sheet, 1, column, Header(column), bold: true));
        for (var row = 1; row <= dataRows; row++)
        for (var column = 1; column <= columns; column++)
            nodes.Add(Cell(sheet, row + 1, column, Value(row, column)));
        return new(DocumentGraph.CurrentSchemaVersion, "wide", DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-" + sheet, 0, nodes)]);
    }

    private static string Header(int column) => $"FIELD_{column:D2}";
    private static string Value(int row, int column) => $"VALUE_R{row:D3}_C{column:D2}";

    private static DocumentNode Cell(string sheet, int row, int column, string value,
        bool bold = false)
    {
        var address = $"{(char)('A' + column - 1)}{row}";
        return new($"{sheet}-cell-{address}", NodeKind.Cell, null, row * 1000 + column,
            ContentLayer.Body, new TextNodeContent(value),
            new SourceAnchor("xlsx", "/xl/worksheets/sheet1.xml",
                [new AnchorLocator("cell_address", address)]),
            Extensions: new Dictionary<string, JsonElement>
            {
                ["sheet_name"] = JsonSerializer.SerializeToElement(sheet),
                ["row"] = JsonSerializer.SerializeToElement(row),
                ["column"] = JsonSerializer.SerializeToElement(column),
                ["is_bold"] = JsonSerializer.SerializeToElement(bold)
            });
    }
}
