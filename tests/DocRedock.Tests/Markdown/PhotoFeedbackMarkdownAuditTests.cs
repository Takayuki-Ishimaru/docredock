using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using DocRedock.Render;
using DocRedock.Tests.Xlsx;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownDocument = Markdig.Syntax.MarkdownDocument;
using GfmTable = Markdig.Extensions.Tables.Table;
using GfmRow = Markdig.Extensions.Tables.TableRow;
using GfmCell = Markdig.Extensions.Tables.TableCell;

namespace DocRedock.Tests.Markdown;

/// <summary>
/// Regression audit for photographed-Excel feedback: what a reader (human or model) must be able
/// to recover from the default readable Markdown, asserted against the parsed GFM AST rather than
/// against the presence of escape characters.
/// </summary>
public sealed class PhotoFeedbackMarkdownAuditTests
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UsePipeTables().UseMathematics().Build();

    /// <summary>The public readable-export defaults (DocumentService), expressed as serializer options.</summary>
    private static ReadableMarkdownOptions PublicDefaults(string sourceName)
    {
        var api = new ReadableDocumentExportOptions(sourceName, "out.md");
        Assert.True(api.ExpandMergedCells);
        Assert.Equal(MarkdownTableLayout.Auto, api.TableLayout);
        Assert.True(api.IncludeSourceReferences);
        Assert.True(api.IncludeTableOfContents);
        return new ReadableMarkdownOptions(
            ExpandMergedCells: api.ExpandMergedCells,
            TableLayout: api.TableLayout,
            IncludeSourceReferences: api.IncludeSourceReferences,
            IncludeTableOfContents: api.IncludeTableOfContents,
            SeparateWorksheetAnnotations: api.SeparateWorksheetAnnotations,
            SourceName: sourceName,
            FallbackTitle: Path.GetFileNameWithoutExtension(sourceName));
    }

    [Fact]
    public void Vertically_merged_condition_applies_to_every_covered_row_with_named_blank_column()
    {
        var graph = WorkbookPresentationTests.Extract("""
            <sheetData>
            <row r="1"><c r="A1" s="1" t="inlineStr"><is><t>条件</t></is></c><c r="B1" s="1" t="inlineStr"><is><t>Reserved/予約</t></is></c><c r="C1" s="1" t="inlineStr"><is><t>項目</t></is></c></row>
            <row r="2"><c r="A2" t="inlineStr"><is><t>3行に適用</t></is></c><c r="B2" s="1"/><c r="C2" t="inlineStr"><is><t>Item A</t></is></c></row>
            <row r="3"><c r="A3" s="1"/><c r="B3" s="1"/><c r="C3" t="inlineStr"><is><t>Item B</t></is></c></row>
            <row r="4"><c r="A4" s="1"/><c r="B4" s="1"/><c r="C4" t="inlineStr"><is><t>Item C</t></is></c></row>
            </sheetData>
            <mergeCells count="1"><mergeCell ref="A2:A4"/></mergeCells>
            """).Graph;

        var markdown = Serialize(graph, PublicDefaults("book.xlsx"));
        var document = Parse(markdown);
        var table = Assert.Single(Tables(document));
        Assert.True(table.Count == 4,
            $"The merged condition must reach all three data rows in one table.\n--- markdown ---\n{markdown}\n---");
        Assert.All(table, row => Assert.Equal(3, Assert.IsType<GfmRow>(row).Count));

        // The merge origin row and both continuation rows carry the condition: applicability survived.
        for (var row = 1; row <= 3; row++)
            Assert.Equal("3行に適用", Text(DataCell(document, row, 0)));
        // A named blank field keeps its column: the header reads and items never shift left into it.
        Assert.Equal("Reserved/予約", Text(DataCell(document, 0, 1)));
        for (var row = 1; row <= 3; row++)
            Assert.Equal("", Text(DataCell(document, row, 1)));
        Assert.Equal("Item A", Text(DataCell(document, 1, 2)));
        Assert.Equal("Item B", Text(DataCell(document, 2, 2)));
        Assert.Equal("Item C", Text(DataCell(document, 3, 2)));
    }

    [Fact]
    public void Default_public_export_of_a_seventeen_column_table_avoids_an_enormous_pipe_row()
    {
        var markdown = Serialize(WideGraph(17), PublicDefaults("book.xlsx"));
        var document = Parse(markdown);

        Assert.Empty(Tables(document));
        AssertEveryValue(document, 17, dataRows: 2);
        Assert.All(markdown.Replace("\r\n", "\n").Split('\n'),
            line => Assert.True(line.Count(c => c == '|') < 17,
                $"A 17-cell pipe row was written to the reader: {line}"));
    }

    [Fact]
    public void Combined_literal_formula_and_json_survive_the_gfm_ast_as_exact_reader_text()
    {
        const string literal = """a|b $BH$18 *d* _e_ [f](g) <h> `i` j\k {"ids": [1, 2]}""";
        const string formula = """IF(A1="a`b|c\\d","*x*","_y_")""";
        const string json = "{\n  \"request\": {\n    \"ids\": [1, 2]\n  }\n}";
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "literals", DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-Data", 0,
            [
                Cell("Data", 1, 1, "項目", true), Cell("Data", 1, 2, "値", true),
                Cell("Data", 2, 1, "literal"), Cell("Data", 2, 2, literal),
                Cell("Data", 3, 1, "formula"), FormulaCell("Data", 3, 2, "7", formula),
                Cell("Data", 4, 1, "json"), JsonCell("Data", 4, 2, json),
            ])]);

        var markdown = Serialize(graph, new ReadableMarkdownOptions(
            ShowFormulas: true, TableLayout: MarkdownTableLayout.Records));
        var document = Parse(markdown);
        var displayed = MarkdownStructure.DisplayedText(document);

        // Exact cell correspondence: the literal is reader text, not markup.
        Assert.Contains(literal, displayed, StringComparison.Ordinal);
        Assert.Empty(MarkdownStructure.Links(document));
        Assert.Empty(MarkdownStructure.Images(document));
        Assert.Empty(((MarkdownObject)document).Descendants<MathInline>());
        Assert.Empty(((MarkdownObject)document).Descendants<MathBlock>());
        // The formula is one code span with its exact content, and its cached value reads with it.
        var code = Assert.Single(((MarkdownObject)document).Descendants<CodeInline>());
        Assert.Equal("=" + formula, code.Content.ToString());
        Assert.Contains("→ 7", displayed, StringComparison.Ordinal);
        // The JSON block keeps its fenced form and its source indentation.
        Assert.Contains("```json", markdown, StringComparison.Ordinal);
        Assert.Contains("      \"ids\": [1, 2]", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void An_isolated_diagram_node_has_readable_text_even_without_a_mermaid_renderer()
    {
        var visual = new VisualGraph("isolation",
            [new VisualNode("a", "開始"), new VisualNode("b", "完了"), new VisualNode("c", "孤立した確認事項")],
            [new VisualEdge("edge", "a", "b", "処理")]);
        var diagram = new DocumentNode("diagram-isolation", NodeKind.Diagram, null, 0, ContentLayer.Derived,
            new TextNodeContent(""), Extensions: new Dictionary<string, JsonElement>
            { ["visual_graph"] = JsonSerializer.SerializeToElement(visual), ["diagram_min_row"] = JsonSerializer.SerializeToElement(1), ["diagram_max_row"] = JsonSerializer.SerializeToElement(1) });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "isolated", DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-図", 0, [diagram])]);
        var text = MarkdownStructure.DisplayedText(Parse(Serialize(graph, PublicDefaults("isolated.xlsx"))));
        Assert.Contains("孤立した確認事項", text, StringComparison.Ordinal);
        Assert.Contains("開始", text, StringComparison.Ordinal);
        Assert.Contains("処理", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagram_fence_keeps_searchable_source_and_non_mermaid_fallback()
    {
        const string mermaid = "flowchart TD\n    N_A1[\"開始\"]\n    N_B2[\"完了\"]\n    N_A1 --> N_B2";
        var diagram = new DocumentNode("diagram-1", NodeKind.Diagram, null, 10, ContentLayer.Derived,
            new TextNodeContent(mermaid),
            Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["diagram_language"] = JsonSerializer.SerializeToElement("mermaid"),
                ["diagram_type"] = JsonSerializer.SerializeToElement("flowchart"),
                ["diagram_min_row"] = JsonSerializer.SerializeToElement(5),
                ["diagram_max_row"] = JsonSerializer.SerializeToElement(10),
            });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "flow", DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-フロー", 0,
            [
                Cell("フロー", 1, 1, "設計書"),
                Cell("フロー", 3, 1, "1.1 処理フロー"),
                Cell("フロー", 5, 1, "開始セル"),
                Cell("フロー", 8, 1, "途中セル"),
                Cell("フロー", 12, 1, "図の後の注記"),
                diagram,
            ])]);

        var markdown = Serialize(graph, PublicDefaults("flow.xlsx"));
        Assert.Contains("```mermaid\n" + mermaid + "\n```", markdown, StringComparison.Ordinal);
        // A row interval does not prove membership. These source values are not present
        // in the diagram's labels and must survive, even within its row interval.
        Assert.Contains("開始セル", markdown, StringComparison.Ordinal);
        Assert.Contains("途中セル", markdown, StringComparison.Ordinal);
        Assert.Contains("図の後の注記", markdown, StringComparison.Ordinal);
        // Searchable source label: the original sheet name is reader text.
        Assert.Contains("フロー", MarkdownStructure.DisplayedText(Parse(markdown)), StringComparison.Ordinal);

        // Non-Mermaid consumers: with diagrams off, the fenced block is gone and the covered
        // cells return as readable fallback instead of vanishing.
        var fallback = Serialize(graph, PublicDefaults("flow.xlsx") with { IncludeDiagrams = false });
        Assert.DoesNotContain("```mermaid", fallback, StringComparison.Ordinal);
        Assert.Contains("開始セル", fallback, StringComparison.Ordinal);
        Assert.Contains("途中セル", fallback, StringComparison.Ordinal);
    }

    [Fact]
    public void Multiline_prose_and_code_keep_lines_and_indentation_without_relying_on_br()
    {
        const string prose = "第一行\n    インデント行\n最終行";
        const string json = "{\n  \"request\": {\n    \"ids\": [1, 2]\n  }\n}";
        var graph = WorkbookPresentationTests.Extract($"""
            <sheetData>
            <row r="1"><c r="A1" s="1" t="inlineStr"><is><t>Field</t></is></c><c r="B1" s="1" t="inlineStr"><is><t>Example</t></is></c></row>
            <row r="2"><c r="A2" t="inlineStr"><is><t>prose</t></is></c><c r="B2" t="inlineStr"><is><t xml:space="preserve">{SecurityElement.Escape(prose)}</t></is></c></row>
            <row r="3"><c r="A3" t="inlineStr"><is><t>json</t></is></c><c r="B3" t="inlineStr"><is><t xml:space="preserve">{SecurityElement.Escape(json)}</t></is></c></row>
            </sheetData>
            """).Graph;

        var markdown = Serialize(graph, PublicDefaults("book.xlsx"));
        var displayed = MarkdownStructure.DisplayedText(Parse(markdown));

        Assert.DoesNotContain("<br", markdown, StringComparison.OrdinalIgnoreCase);
        var blocks = ((MarkdownObject)Parse(markdown)).Descendants<FencedCodeBlock>().ToArray();
        Assert.Contains(blocks, block => block.Lines.ToString().TrimEnd('\n', '\r') == prose);
        Assert.Contains("    インデント行", markdown, StringComparison.Ordinal);
        Assert.Contains("```json", markdown, StringComparison.Ordinal);
        Assert.Contains("      \"ids\": [1, 2]", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("visible", false)]
    [InlineData("complete", true)]
    [InlineData("sanitized", false)]
    public void Notes_and_authors_follow_the_same_policy_in_markdown_and_native_html(
        string policy, bool included)
    {
        var graph = WorkbookPresentationTests.Extract(
            """<sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Value</t></is></c></row></sheetData>""").Graph;
        var note = new DocumentNode("note", NodeKind.Comment, null, 9, ContentLayer.Metadata,
            new TextNodeContent("PRIVATE_NOTE"),
            Extensions: new Dictionary<string, JsonElement>
            {
                ["cell_ref"] = JsonSerializer.SerializeToElement("A1"),
                ["comment_author"] = JsonSerializer.SerializeToElement("NOTE_AUTHOR"),
            });
        graph = graph with { Partitions = [graph.Partitions[0] with { Nodes = [.. graph.Partitions[0].Nodes, note] }] };

        var markdown = Markdig.Markdown.ToHtml(new ReadableMarkdownSerializer(
            PublicDefaults("book.xlsx") with { ContentPolicy = policy }).Serialize(graph));
        var html = ReadableHtmlWriter.WriteDocument(
            ReadableProjectionBuilder.Build(graph, new(ContentPolicy: policy)).Document).Html;

        foreach (var expected in new[] { "PRIVATE_NOTE", "NOTE_AUTHOR" })
        {
            Assert.Equal(included, markdown.Contains(expected, StringComparison.Ordinal));
            Assert.Equal(included, html.Contains(expected, StringComparison.Ordinal));
        }
        // When kept, the note stays tied to its anchor cell in both formats.
        if (included)
        {
            Assert.Contains("A1", markdown, StringComparison.Ordinal);
            Assert.Contains("data-source-range=\"A1\"", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Multiple_sheets_with_reserved_markdown_characters_keep_linked_toc_and_original_names()
    {
        var names = new[] { "Sheet|A*B_C", "Sheet [C] <D>", "Sheet`E`{F}" };
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "toc", DocumentFormatKind.Xlsx,
            names.Select((name, index) => WideGraph(2, sheet: name).Partitions[0] with { Order = index }).ToArray());
        var markdown = Serialize(graph, PublicDefaults("book.xlsx"));
        var document = Parse(markdown);

        foreach (var name in names)
            Assert.Contains(name, MarkdownStructure.DisplayedText(document), StringComparison.Ordinal);

        var links = MarkdownStructure.Links(document)
            .Where(link => link.Url?.StartsWith('#') == true).ToArray();
        Assert.Equal(names.Length, links.Length);
        Assert.Equal(names.Length, links.Select(link => link.Url).Distinct().Count());
        var anchors = Regex.Matches(markdown, @"\bid\s*=\s*[""'](?<id>[^""']+)[""']")
            .Select(match => WebUtility.HtmlDecode(match.Groups["id"].Value)).ToHashSet(StringComparer.Ordinal);
        foreach (var (link, name) in links.Zip(names))
        {
            Assert.Contains(Uri.UnescapeDataString(link.Url![1..]), anchors);
            Assert.Equal(name, Text(link.FirstChild));
        }
    }

    private static string Serialize(DocumentGraph graph, ReadableMarkdownOptions options) =>
        new ReadableMarkdownSerializer(options).Serialize(graph);

    private static MarkdownDocument Parse(string markdown) =>
        global::Markdig.Markdown.Parse(markdown ?? string.Empty, Pipeline);

    private static GfmTable[] Tables(MarkdownDocument document) =>
        ((MarkdownObject)document).Descendants<GfmTable>().ToArray();

    private static GfmCell DataCell(MarkdownDocument document, int row, int column)
    {
        var table = Assert.Single(Tables(document));
        return Assert.IsType<GfmCell>(Assert.IsType<GfmRow>(table[row])[column]);
    }

    private static string Text(Inline? first)
    {
        var output = new StringBuilder();
        for (var inline = first; inline is not null; inline = inline.NextSibling)
            switch (inline)
            {
                case LiteralInline literal: output.Append(literal.Content); break;
                case CodeInline code: output.Append(code.Content); break;
                case HtmlEntityInline entity: output.Append(entity.Transcoded); break;
                case LineBreakInline: output.Append('\n'); break;
                case ContainerInline container: output.Append(Text(container.FirstChild)); break;
            }
        return output.ToString();
    }

    private static string Text(Block block)
    {
        var output = new StringBuilder();
        void Blocks(Block current)
        {
            if (current is LeafBlock leaf) output.Append(Text(leaf.Inline?.FirstChild));
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

    private static string Value(int row, int column) => $"VALUE_R{row:D3}_C{column:D2}";

    private static DocumentGraph WideGraph(int columns, int dataRows = 2, string sheet = "Data")
    {
        var nodes = new List<DocumentNode>();
        for (var column = 1; column <= columns; column++)
            nodes.Add(Cell(sheet, 1, column, $"HEADER_C{column:D2}", true));
        for (var row = 1; row <= dataRows; row++)
        for (var column = 1; column <= columns; column++)
            nodes.Add(Cell(sheet, row + 1, column, Value(row, column)));
        return new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "wide", DocumentFormatKind.Xlsx,
            [new DocumentPartition($"sheet-{sheet}", 0, nodes)]);
    }

    private static DocumentNode Cell(string sheet, int row, int column, string value, bool bold = false)
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

    private static DocumentNode FormulaCell(string sheet, int row, int column, string value, string formula)
    {
        var baseCell = Cell(sheet, row, column, value);
        var extensions = baseCell.Extensions!.ToDictionary(i => i.Key, i => i.Value);
        extensions["formula"] = JsonSerializer.SerializeToElement(formula);
        extensions["is_formula"] = JsonSerializer.SerializeToElement(true);
        extensions["has_cached_value"] = JsonSerializer.SerializeToElement(true);
        return baseCell with { Extensions = extensions };
    }

    private static DocumentNode JsonCell(string sheet, int row, int column, string value)
    {
        var baseCell = Cell(sheet, row, column, value);
        var extensions = baseCell.Extensions!.ToDictionary(i => i.Key, i => i.Value);
        extensions["is_json"] = JsonSerializer.SerializeToElement(true);
        return baseCell with { Extensions = extensions };
    }
}
