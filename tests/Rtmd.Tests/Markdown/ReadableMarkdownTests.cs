using System.Text.Json;
using Rtmd.Core.Documents;
using Rtmd.Markdown;

namespace Rtmd.Tests.Markdown;

public sealed class ReadableMarkdownTests
{
    [Fact]
    public void Workbook_diagram_replaces_the_flattened_cell_region_with_a_mermaid_fence()
    {
        var diagram = new DocumentNode(
            "diagram-1",
            NodeKind.Diagram,
            null,
            10,
            ContentLayer.Derived,
            new TextNodeContent("flowchart TD\n    N_A1[\"開始\"]\n    N_B2[\"完了\"]\n    N_A1 --> N_B2"),
            Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["diagram_language"] = JsonSerializer.SerializeToElement("mermaid"),
                ["diagram_type"] = JsonSerializer.SerializeToElement("flowchart"),
                ["diagram_min_row"] = JsonSerializer.SerializeToElement(5),
                ["diagram_max_row"] = JsonSerializer.SerializeToElement(10),
            });
        var graph = new DocumentGraph(
            DocumentGraph.CurrentSchemaVersion,
            "doc-diagram",
            DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-フロー", 0,
            [
                Cell("A1", 1, 1, "設計書"),
                Cell("A3", 3, 1, "1.1 処理フロー"),
                Cell("A5", 5, 1, "開始セル"),
                Cell("A8", 8, 1, "途中セル"),
                Cell("A12", 12, 1, "図の後の注記"),
                diagram,
            ])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        Assert.DoesNotContain("<svg xmlns=\"http://www.w3.org/2000/svg\"", markdown, StringComparison.Ordinal);
        Assert.Contains("```mermaid\nflowchart TD\n    N_A1[\"開始\"]\n    N_B2[\"完了\"]\n    N_A1 --> N_B2\n```", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("開始セル", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("途中セル", markdown, StringComparison.Ordinal);
        Assert.Contains("図の後の注記", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stateDiagram-v2\n    state \"申請中\" as S_1\n    state \"承認済み\" as S_2\n    [*] --> S_1\n    S_1 --> S_2: 承認")]
    [InlineData("sequenceDiagram\n    participant P1 as 申請者\n    participant P2 as 承認API\n    P1->>P2: 申請する")]
    public void Supported_mermaid_diagrams_are_emitted_as_standard_fences_by_default(string mermaid)
    {
        var diagram = new DocumentNode(
            "diagram-preview",
            NodeKind.Diagram,
            null,
            0,
            ContentLayer.Derived,
            new TextNodeContent(mermaid),
            Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["diagram_language"] = JsonSerializer.SerializeToElement("mermaid"),
                ["diagram_min_row"] = JsonSerializer.SerializeToElement(1),
                ["diagram_max_row"] = JsonSerializer.SerializeToElement(1),
            });
        var graph = new DocumentGraph(
            DocumentGraph.CurrentSchemaVersion,
            "doc-svg-preview",
            DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-diagram", 0, [diagram])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        Assert.DoesNotContain("<svg", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<details>", markdown, StringComparison.Ordinal);
        Assert.Contains("```mermaid", markdown, StringComparison.Ordinal);
        Assert.Contains(mermaid, markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Workbook_is_reconstructed_as_headings_metadata_and_named_tables()
    {
        var nodes = new[]
        {
            Cell("A1", 1, 1, "経費精算システム 設計書"),
            Cell("G14", 14, 7, "文書ID"), Cell("O14", 14, 15, "EXP-001"),
            Cell("AC14", 14, 29, "作成日"), Cell("AJ14", 14, 36, "2026-08-23"),
            Cell("G17", 17, 7, "作成者"), Cell("O17", 17, 15, "開発部"),
            Cell("AC17", 17, 29, "状態"), Cell("AJ17", 17, 36, "レビュー中"),
            Cell("E29", 29, 5, "1. 文書の目的"),
            Cell("E31", 31, 5, "経費精算システムの仕様を定義します。"),
            Cell("F42", 42, 6, "01"), Cell("I42", 42, 9, "システム概要"), Cell("W42", 42, 23, "利用者と対象範囲"),
            Cell("F43", 43, 6, "02"), Cell("I43", 43, 9, "機能要件"), Cell("W43", 43, 23, "申請・承認・精算"),
            Cell("F56", 56, 6, "版"), Cell("J56", 56, 10, "日付"), Cell("R56", 56, 18, "変更内容"), Cell("AN56", 56, 40, "作成者"),
            Cell("F57", 57, 6, "1.0"), Cell("J57", 57, 10, "2026-08-23"), Cell("R57", 57, 18, "初版"), Cell("AN57", 57, 40, "開発部"),
        };
        var graph = new DocumentGraph(
            DocumentGraph.CurrentSchemaVersion,
            "doc-readable",
            DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-00_表紙・文書情報", 0, nodes)]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        Assert.Contains("# 経費精算システム 設計書", markdown, StringComparison.Ordinal);
        Assert.Contains("## 00 表紙・文書情報", markdown, StringComparison.Ordinal);
        Assert.Contains("### 文書情報", markdown, StringComparison.Ordinal);
        Assert.Contains("| 項目 | 内容 | 項目 | 内容 |", markdown, StringComparison.Ordinal);
        Assert.Contains("### 1. 文書の目的", markdown, StringComparison.Ordinal);
        Assert.Contains("利用者と対象範囲", markdown, StringComparison.Ordinal);
        Assert.Contains("| 版 | 日付 | 変更内容 | 作成者 |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("rtmd:", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("| Row |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("| G |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("G14", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Workbook_uses_style_headers_regions_and_display_values_without_showing_formulas()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc-regions", DocumentFormatKind.Xlsx,
        [new DocumentPartition("sheet-仕様", 0,
        [
            Cell("A1", 1, 1, "仕様書"),
            Cell("A3", 3, 1, "1. 注意事項", isBold: true),
            Cell("A5", 5, 1, "項目", isBold: true), Cell("B5", 5, 2, "金額", isBold: true),
            Cell("A6", 6, 1, "A"), Cell("B6", 6, 2, "12,800 円"),
            Cell("F5", 5, 6, "項目", isBold: true), Cell("G5", 5, 7, "日付", isBold: true),
            Cell("F6", 6, 6, "B"), Cell("G6", 6, 7, "2026-08-23"),
            Cell("A9", 9, 1, "HTTP/1.1 400 Bad Request\n{\n  \"error\": \"invalid\"\n}"),
            FormulaCell("A11", 11, 1, "OK", "IF(A1=\"x\",\"OK\",\"NG\")"),
        ])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        Assert.Contains("### 1. 注意事項", markdown, StringComparison.Ordinal);
        Assert.Equal(2, markdown.Split("| 項目 |", StringSplitOptions.None).Length - 1);
        Assert.Contains("12,800 円", markdown, StringComparison.Ordinal);
        Assert.Contains("```\nHTTP/1.1 400 Bad Request", markdown, StringComparison.Ordinal);
        Assert.Contains("OK", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("=IF(", markdown, StringComparison.Ordinal);

        var withFormulas = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(ShowFormulas: true)).Serialize(graph);
        Assert.Contains("=IF(", withFormulas, StringComparison.Ordinal);
    }

    [Fact]
    public void Document_projection_preserves_heading_levels_tight_lists_code_and_images()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc-docx", DocumentFormatKind.Docx,
        [new DocumentPartition("document", 0,
        [
            Node("h1", NodeKind.Heading, 0, "設計書", ("heading_level", 1)),
            Node("h3", NodeKind.Heading, 1, "詳細", ("heading_level", 3)),
            Node("li1", NodeKind.ListItem, 2, "一つ目", ("list_level", 0)),
            Node("li2", NodeKind.ListItem, 3, "二つ目", ("list_level", 0)),
            Node("code", NodeKind.CodeBlock, 4, "var answer = 42;"),
            new DocumentNode("image", NodeKind.Image, null, 5, ContentLayer.Body,
                new ReferenceNodeContent("media/diagram 1.png", "構成図")),
        ])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        Assert.Contains("# 設計書", markdown, StringComparison.Ordinal);
        Assert.Contains("### 詳細", markdown, StringComparison.Ordinal);
        Assert.Contains("- 一つ目\n- 二つ目\n", markdown, StringComparison.Ordinal);
        Assert.Contains("```\nvar answer = 42;\n```", markdown, StringComparison.Ordinal);
        Assert.Contains("![構成図](<media/diagram 1.png>)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Pptx_paragraph_details_preserve_bullets_levels_and_run_emphasis()
    {
        var details = new[]
        {
            new { Text = "通常段落", Level = 0, IsBullet = false, Runs = new[] { new { Text = "通常段落", Bold = false, Italic = false, Underline = false } } },
            new { Text = "重要", Level = 0, IsBullet = true, Runs = new[] { new { Text = "重要", Bold = true, Italic = false, Underline = false } } },
            new { Text = "補足", Level = 1, IsBullet = true, Runs = new[] { new { Text = "補足", Bold = false, Italic = true, Underline = false } } },
        };
        var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["shape_role"] = JsonSerializer.SerializeToElement("body"),
            ["paragraph_details"] = JsonSerializer.SerializeToElement(details),
        };
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc-pptx", DocumentFormatKind.Pptx,
        [new DocumentPartition("slide1", 0,
        [
            new DocumentNode("title", NodeKind.Shape, null, 0, ContentLayer.Body, new TextNodeContent("概要"),
                Extensions: new Dictionary<string, JsonElement> { ["shape_role"] = JsonSerializer.SerializeToElement("title") }),
            new DocumentNode("body", NodeKind.Shape, null, 1, ContentLayer.Body, new TextNodeContent("通常段落\n重要\n補足"), Extensions: extensions),
        ])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        Assert.Contains("# 概要", markdown, StringComparison.Ordinal);
        Assert.Contains("通常段落", markdown, StringComparison.Ordinal);
        Assert.Contains("- **重要**\n  - _補足_", markdown, StringComparison.Ordinal);
    }

    private static DocumentNode Cell(string address, int row, int column, string value, bool isBold = false) => new(
        "cell-" + address,
        NodeKind.Cell,
        null,
        row * 1000 + column,
        ContentLayer.Body,
        new TextNodeContent(value),
        new SourceAnchor("xlsx", "/xl/worksheets/sheet1.xml", [new AnchorLocator("cell_address", address)]),
        Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["row"] = JsonSerializer.SerializeToElement(row),
            ["column"] = JsonSerializer.SerializeToElement(column),
            ["is_bold"] = JsonSerializer.SerializeToElement(isBold),
        });

    private static DocumentNode FormulaCell(string address, int row, int column, string value, string formula) => new(
        "cell-" + address,
        NodeKind.Cell,
        null,
        row * 1000 + column,
        ContentLayer.Body,
        new TextNodeContent(value),
        new SourceAnchor("xlsx", "/xl/worksheets/sheet1.xml", [new AnchorLocator("cell_address", address)]),
        Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["row"] = JsonSerializer.SerializeToElement(row),
            ["column"] = JsonSerializer.SerializeToElement(column),
            ["formula"] = JsonSerializer.SerializeToElement(formula),
        });

    private static DocumentNode Node(string id, NodeKind kind, int order, string value, params (string Key, int Value)[] extensions) => new(
        id, kind, null, order, ContentLayer.Body, new TextNodeContent(value),
        Extensions: extensions.ToDictionary(item => item.Key, item => JsonSerializer.SerializeToElement(item.Value), StringComparer.Ordinal));
}
