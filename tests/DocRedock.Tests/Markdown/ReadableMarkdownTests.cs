using System.Text;
using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml;
using DocRedock.Markdown;

namespace DocRedock.Tests.Markdown;

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
    public void Visual_graph_renders_undirected_escaped_edges_and_retains_invalid_topology_fallback()
    {
        var visual = new VisualGraph("vector-flow",
            [new VisualNode("v_a", "A[&\""), new VisualNode("v_b", "B")],
            [new VisualEdge("e_1", "v_a", "v_b", "YES|NO", EdgeDirection: VisualEdgeDirection.Undirected)]);
        var visualNode = new DocumentNode("visual", NodeKind.Diagram, null, 0, ContentLayer.Derived, new TextNodeContent("Visual flow"),
            Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["visual_graph"] = JsonSerializer.SerializeToElement(visual) });
        var connector = new DocumentNode("connector", NodeKind.Connector, null, 1, ContentLayer.Body, new TextNodeContent("A → B"),
            Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["visual_graph_edge"] = JsonSerializer.SerializeToElement(true) });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "visual", DocumentFormatKind.Pptx,
            [new DocumentPartition("slide1", 0, [visualNode, connector])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        Assert.Contains("v_a[A&#91;&amp;&quot;]", markdown, StringComparison.Ordinal);
        Assert.Contains("v_a ---|YES&#124;NO| v_b", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("- A → B", markdown, StringComparison.Ordinal);

        var invalid = visual with { Edges = [new VisualEdge("dangling", "v_a", "missing")] };
        var invalidNode = visualNode with { Extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["visual_graph"] = JsonSerializer.SerializeToElement(invalid) } };
        var fallbackSerializer = new ReadableMarkdownSerializer();
        var fallback = fallbackSerializer.Serialize(graph with { Partitions = [new DocumentPartition("slide1", 0, [invalidNode, connector])] });

        Assert.DoesNotContain("```mermaid", fallback, StringComparison.Ordinal);
        Assert.Contains("- A → B", fallback, StringComparison.Ordinal);
        Assert.Contains(fallbackSerializer.Diagnostics, diagnostic => diagnostic.Code == "VisualSemanticProjectionPartial");
    }

    [Fact]
    public void Visual_graph_member_marker_suppresses_only_a_renderable_docx_graph()
    {
        var visual = new VisualGraph("docx-flow", [new VisualNode("a", "Start"), new VisualNode("b", "End")], [new VisualEdge("edge", "a", "b")]);
        var diagram = new DocumentNode("diagram", NodeKind.Diagram, null, 0, ContentLayer.Derived, new TextNodeContent("DOCX visual graph"),
            Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["visual_graph"] = JsonSerializer.SerializeToElement(visual) });
        var member = new DocumentNode("member", NodeKind.TextBox, null, 1, ContentLayer.Body, new TextNodeContent("Start"),
            Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["visual_graph_member"] = JsonSerializer.SerializeToElement(true) });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "docx-flow", DocumentFormatKind.Docx,
            [new DocumentPartition("document", 0, [diagram, member])]);

        var rendered = new ReadableMarkdownSerializer().Serialize(graph);
        Assert.Contains("a --> b", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Start\n", rendered, StringComparison.Ordinal);

        var disabled = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(IncludeDiagrams: false)).Serialize(graph);
        Assert.DoesNotContain("```mermaid", disabled, StringComparison.Ordinal);
        Assert.Contains("Start", disabled, StringComparison.Ordinal);

        var invalidDiagram = diagram with { Extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        { ["visual_graph"] = JsonSerializer.SerializeToElement(visual with { Edges = [new VisualEdge("bad", "a", "missing")] }) } };
        var invalid = new ReadableMarkdownSerializer().Serialize(graph with { Partitions = [new DocumentPartition("document", 0, [invalidDiagram, member])] });
        Assert.Contains("Start", invalid, StringComparison.Ordinal);
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
        Assert.Contains("- **文書ID**: EXP-001", markdown, StringComparison.Ordinal);
        Assert.Contains("- **作成日**: 2026-08-23", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("| 項目 | 内容 | 項目 | 内容 |", markdown, StringComparison.Ordinal);
        Assert.Contains("### 1. 文書の目的", markdown, StringComparison.Ordinal);
        Assert.Contains("利用者と対象範囲", markdown, StringComparison.Ordinal);
        Assert.Contains("| 版 | 日付 | 変更内容 | 作成者 |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("drmd:", markdown, StringComparison.Ordinal);
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
        Assert.Contains("![構成図](media/diagram%201.png)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Pptx_paragraph_details_preserve_bullets_levels_and_run_emphasis()
    {
        var details = new[]
        {
            new { Text = "通常段落", Level = 0, IsBullet = false, Runs = new[] { new { Text = "通常段落", Bold = false, Italic = false, Underline = false } } },
            new { Text = "• 重要", Level = 0, IsBullet = true, Runs = new[] { new { Text = "• 重要", Bold = true, Italic = false, Underline = false } } },
            new { Text = "補足", Level = 1, IsBullet = true, Runs = new[] { new { Text = "補足", Bold = false, Italic = true, Underline = false } } },
            new { Text = "• リテラル箇条書き", Level = 0, IsBullet = false, Runs = new[] { new { Text = "• リテラル箇条書き", Bold = false, Italic = false, Underline = false } } },
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
            new DocumentNode("body", NodeKind.Shape, null, 1, ContentLayer.Body, new TextNodeContent("通常段落\n• 重要\n補足\n• リテラル箇条書き"), Extensions: extensions),
        ])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        Assert.Contains("# プレゼンテーション", markdown, StringComparison.Ordinal);
        Assert.Contains("## スライド 1 — 概要", markdown, StringComparison.Ordinal);
        Assert.Contains("通常段落", markdown, StringComparison.Ordinal);
        Assert.Contains("- **重要**\n  - _補足_", markdown, StringComparison.Ordinal);
        Assert.Contains("- リテラル箇条書き", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("• リテラル箇条書き", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Pptx_readable_projection_keeps_slide_boundaries_tables_and_semantic_body_blocks()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc-pptx-preview", DocumentFormatKind.Pptx,
        [
            new DocumentPartition("slide1", 0,
            [
                Shape("title-1", "Overview", "title", 0),
                Shape("body-1", "• First point\n• Second point\nA final note", "body", 1),
                new DocumentNode("table-1", NodeKind.Table, null, 2, ContentLayer.Body,
                    new TableNodeContent([new TableCell[] { "Gate", "Status" }, new TableCell[] { "Quality", "PASS" }])),
                Shape("footer-1", "PROJECT · 1", "footer", 3),
                new DocumentNode("notes-1", NodeKind.SpeakerNotes, null, 4, ContentLayer.Furniture,
                    new TextNodeContent("Sources: local fixture\nKeep rollback enabled.")),
            ]),
            new DocumentPartition("slide2", 1,
            [Shape("title-2", "Decision", "title", 0), Shape("body-2", "Approved", "body", 1)]),
        ]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        Assert.Contains("# プレゼンテーション", markdown, StringComparison.Ordinal);
        Assert.Contains("## スライド 1 — Overview", markdown, StringComparison.Ordinal);
        Assert.Contains("- First point\n- Second point", markdown, StringComparison.Ordinal);
        Assert.Contains("| Gate | Status |", markdown, StringComparison.Ordinal);
        Assert.Contains("## スライド 2 — Decision", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("speaker-notes", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Keep rollback enabled.", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("PROJECT · 1", markdown, StringComparison.Ordinal);

        var complete = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(ContentPolicy: "complete")).Serialize(graph);
        Assert.Contains("<details class=\"speaker-notes\">", complete, StringComparison.Ordinal);
        Assert.Contains("<summary>スピーカーノート（クリックで展開）</summary>", complete, StringComparison.Ordinal);
        Assert.Contains("Keep rollback enabled.", complete, StringComparison.Ordinal);
    }

    [Fact]
    public void Document_table_projects_merged_cells_with_blank_continuations()
    {
        var table = new DocumentNode("table", NodeKind.Table, null, 0, ContentLayer.Body, new TableNodeContent(
        [
            [new TableCell("A1"), new TableCell("B1", RowSpan: 2), new TableCell("C1")],
            [new TableCell("A2"), new TableCell(string.Empty, RowSpan: 0), new TableCell("C2")],
            [new TableCell("Partial", ColSpan: 2), new TableCell("C3")],
        ]));
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc-merged-table", DocumentFormatKind.Docx,
            [new DocumentPartition("document", 0, [table])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        // The vMerge continuation row inherits "B1" from the cell above instead of rendering an
        // empty column (D07-2); a *partial* gridSpan (covering 2 of 3 columns) repeats its text
        // across only the columns it covers instead of leaving them blank (D07-3), so no row ever
        // collapses to a run of empty "|  |" cells.
        Assert.Contains("| A1 | B1 | C1 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| A2 |  | C2 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Partial |  | C3 |", markdown, StringComparison.Ordinal);
        Assert.Contains("|  |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Document_table_full_width_single_cell_row_becomes_a_paragraph_after_the_table()
    {
        var table = new DocumentNode("table", NodeKind.Table, null, 0, ContentLayer.Body, new TableNodeContent(
        [
            [new TableCell("Header A"), new TableCell("Header B")],
            [new TableCell("A1"), new TableCell("B1")],
            [new TableCell("終端状態（○）に達した申請は、いかなる操作によっても他の状態へ遷移しない。", ColSpan: 2)],
        ]));
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc-note-row-table", DocumentFormatKind.Docx,
            [new DocumentPartition("document", 0, [table])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        // A row whose single cell spans the table's full grid width (the common Word "note row"
        // pattern) reads as noise when duplicated across every column, so it renders as a plain
        // paragraph right after the table instead of a repeated-text row.
        Assert.Contains("| Header A | Header B |", markdown, StringComparison.Ordinal);
        Assert.Contains("| A1 | B1 |", markdown, StringComparison.Ordinal);
        Assert.Contains("\n\n終端状態（○）に達した申請は、いかなる操作によっても他の状態へ遷移しない。", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("| 終端状態", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("は、いかなる操作によっても他の状態へ遷移しない。 | 終端状態", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Document_headers_footers_footnotes_and_comments_are_aggregated_into_labeled_sections()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc-furniture", DocumentFormatKind.Docx,
        [
            new DocumentPartition("document", 0,
            [
                new DocumentNode("body", NodeKind.Paragraph, null, 0, ContentLayer.Body, new TextNodeContent("Body text")),
                new DocumentNode("h1", NodeKind.Header, null, 1, ContentLayer.Furniture, new TextNodeContent("Doc Title")),
                new DocumentNode("h2", NodeKind.Header, null, 2, ContentLayer.Furniture, new TextNodeContent("Doc Title (Section 2)")),
                new DocumentNode("f1", NodeKind.Footer, null, 3, ContentLayer.Furniture, new TextNodeContent("Page 1")),
                new DocumentNode("note1", NodeKind.Footnote, null, 4, ContentLayer.Body, new TextNodeContent("Footnote body")),
                new DocumentNode("end1", NodeKind.Endnote, null, 5, ContentLayer.Body, new TextNodeContent("Endnote body")),
                new DocumentNode("comment1", NodeKind.Comment, null, 6, ContentLayer.Body, new TextNodeContent("Please clarify."),
                    Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["comment_author"] = JsonSerializer.SerializeToElement("Reviewer") }),
            ])
        ]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        // Furniture is pulled out of the body flow and aggregated at the end, deduped by
        // containment: the shorter "Doc Title" is dropped since "Doc Title (Section 2)" already
        // contains it (same rule as the existing D04 duplicate-header dedup).
        Assert.Contains("### 文書ヘッダー・フッター（参考）", markdown, StringComparison.Ordinal);
        Assert.Contains("- ヘッダー: Doc Title (Section 2)", markdown, StringComparison.Ordinal);
        Assert.Contains("- フッター: Page 1", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("- ヘッダー: Doc Title\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("\nDoc Title\n", markdown, StringComparison.Ordinal);

        Assert.Contains("### 脚注\n\n1. Footnote body", markdown, StringComparison.Ordinal);
        Assert.Contains("### 文末脚注\n\n1. Endnote body", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("> **コメント** (Reviewer): Please clarify.", markdown, StringComparison.Ordinal);

        var complete = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(ContentPolicy: "complete")).Serialize(graph);
        Assert.Contains("> **コメント** (Reviewer): Please clarify.", complete, StringComparison.Ordinal);
    }

    [Fact]
    public void Readable_projection_folds_ocr_and_normalizes_split_rich_text_runs()
    {
        var image = new DocumentNode("image", NodeKind.Image, null, 0, ContentLayer.Body,
            new ReferenceNodeContent("media/dashboard.png", "Dashboard"));
        var ocr = new DocumentNode("ocr", NodeKind.ImageText, "image", 1, ContentLayer.Derived,
            new TextNodeContent("OCR line one\nOCR line two"));
        var rich = new DocumentNode("rich", NodeKind.Paragraph, null, 2, ContentLayer.Body,
            new RichTextNodeContent([
                new TextRun("Recommendation. ", Bold: true),
                new TextRun("Proceed with the canary."),
                new TextRun("Preservation sentinel: ", Bold: true),
                new TextRun("DOCX-2026", Bold: true),
            ]));
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc-rich-preview", DocumentFormatKind.Docx,
            [new DocumentPartition("document", 0, [image, ocr, rich])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        Assert.Contains("<details class=\"ocr-extraction\">", markdown, StringComparison.Ordinal);
        Assert.Contains("<summary>OCR抽出テキスト（クリックで展開）</summary>", markdown, StringComparison.Ordinal);
        Assert.Contains("> OCR line one  \n> OCR line two  \n", markdown, StringComparison.Ordinal);
        Assert.Contains("**Recommendation.** Proceed", markdown, StringComparison.Ordinal);
        Assert.Contains("**Preservation sentinel: DOCX-2026**", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("**Recommendation. **Proceed", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsDenseWideXlsxTablesAsTables()
    {
        var cells = Enumerable.Range(1, 12)
            .SelectMany(column => new[]
            {
                Cell($"{(char)('A' + column - 1)}1", 1, column, $"H{column}"),
                Cell($"{(char)('A' + column - 1)}2", 2, column, $"V{column}"),
            })
            .ToArray();
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc-wide", DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-Wide", 0, cells)]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        Assert.Contains("| H1 | H2 | H3 | H4 | H5 | H6 | H7 | H8 | H9 | H10 | H11 | H12 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| V1 | V2 | V3 | V4 | V5 | V6 | V7 | V8 | V9 | V10 | V11 | V12 |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void MergedHeadingDoesNotReconnectSideBySideXlsxTables()
    {
        var heading = Cell("A1", 1, 1, "月次分析 | 投資と実行状況");
        var headingExtensions = heading.Extensions!.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        headingExtensions["merged_to_column"] = JsonSerializer.SerializeToElement(11);
        heading = heading with { Extensions = headingExtensions };
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc-regions", DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-Monthly", 0,
            [
                heading,
                Cell("A3", 3, 1, "月"), Cell("B3", 3, 2, "予算"), Cell("C3", 3, 3, "実績"), Cell("D3", 3, 4, "件数"), Cell("E3", 3, 5, "進捗"),
                Cell("A4", 4, 1, "4月"), Cell("B4", 4, 2, "100"), Cell("C4", 4, 3, "80"), Cell("D4", 4, 4, "2"), Cell("E4", 4, 5, "80%"),
                Cell("H3", 3, 8, "カテゴリ"), Cell("I3", 3, 9, "予算"), Cell("J3", 3, 10, "実績"), Cell("K3", 3, 11, "消化率"),
                Cell("H4", 4, 8, "製品"), Cell("I4", 4, 9, "100"), Cell("J4", 4, 10, "80"), Cell("K4", 4, 11, "80%"),
            ])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        Assert.Contains("| 月 | 予算 | 実績 | 件数 | 進捗 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| カテゴリ | 予算 | 実績 | 消化率 |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("| 月 | 予算 | 実績 | 件数 | 進捗 | カテゴリ |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Chart_projection_explains_type_series_and_trend_for_xlsx()
    {
        var chart = new DocumentNode("chart", NodeKind.Chart, null, 1, ContentLayer.Body, new TextNodeContent("売上推移"),
            Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["chart_title"] = JsonSerializer.SerializeToElement("売上推移"),
                ["chart_type"] = JsonSerializer.SerializeToElement("line"),
                ["chart_series"] = JsonSerializer.SerializeToElement(new[] { new { Name = "売上", Categories = new[] { "4月", "5月", "6月" }, Values = new[] { "12", "18", "15" } } }),
            });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc-chart", DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-Chart", 0, [Cell("A1", 1, 1, "ダッシュボード"), chart])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        Assert.Contains("**売上推移**（折れ線グラフ）", markdown, StringComparison.Ordinal);
        Assert.Contains("要約: 1 系列のグラフです。", markdown, StringComparison.Ordinal);
        Assert.Contains("4月 の 12 から 6月 の 15 へ 増加", markdown, StringComparison.Ordinal);
        Assert.Contains("| 4月 | 12 |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Chart_reader_preserves_sparse_cached_point_indexes()
    {
        const string chartXml = """
            <c:chartSpace xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart"><c:chart><c:plotArea><c:lineChart><c:ser>
              <c:cat><c:strLit><c:ptCount val="3"/><c:pt idx="0"><c:v>4月</c:v></c:pt><c:pt idx="1"><c:v>5月</c:v></c:pt><c:pt idx="2"><c:v>6月</c:v></c:pt></c:strLit></c:cat>
              <c:val><c:numLit><c:ptCount val="3"/><c:pt idx="0"><c:v>10</c:v></c:pt><c:pt idx="2"><c:v>30</c:v></c:pt></c:numLit></c:val>
            </c:ser></c:lineChart></c:plotArea></c:chart></c:chartSpace>
            """;

        var chart = OpenXmlChartReader.Read(Encoding.UTF8.GetBytes(chartXml));
        var series = Assert.Single(chart!.Series);

        Assert.Equal(["4月", "5月", "6月"], series.Categories);
        Assert.Equal(["10", "", "30"], series.Values);
    }

    [Fact]
    public void Pie_chart_summary_describes_composition_instead_of_a_trend()
    {
        var chart = new DocumentNode("chart", NodeKind.Chart, null, 1, ContentLayer.Body, new TextNodeContent("内訳"),
            Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["chart_title"] = JsonSerializer.SerializeToElement("内訳"),
                ["chart_type"] = JsonSerializer.SerializeToElement("pie"),
                ["chart_series"] = JsonSerializer.SerializeToElement(new[] { new { Name = "構成", Categories = new[] { "標準", "軽減", "非課税" }, Values = new[] { "58", "27", "15" } } }),
            });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc-pie", DocumentFormatKind.Pptx,
            [new DocumentPartition("slide1", 0, [chart])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        Assert.Contains("最大 標準 58（全体の 58%）", markdown, StringComparison.Ordinal);
        Assert.Contains("最小 非課税 15（全体の 15%）", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(" から ", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Pptx_reading_order_uses_roles_then_canvas_positions_before_notes()
    {
        DocumentNode ShapeAt(string id, string text, string role, int order, double x, double y) => new(id, NodeKind.Shape, null, order,
            ContentLayer.Body, new TextNodeContent(text), Geometry: new Geometry("pptx-emu", x, y, 100, 100),
            Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["shape_role"] = JsonSerializer.SerializeToElement(role) });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc-order", DocumentFormatKind.Pptx,
            [new DocumentPartition("slide1", 0,
            [
                ShapeAt("right", "右", "body", 0, 500, 200),
                new DocumentNode("notes", NodeKind.SpeakerNotes, null, 1, ContentLayer.Furniture, new TextNodeContent("ノート")),
                ShapeAt("left", "左", "body", 2, 100, 200),
                ShapeAt("title", "タイトル", "title", 3, 100, 50),
            ])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        Assert.True(markdown.IndexOf("タイトル", StringComparison.Ordinal) < markdown.IndexOf("左", StringComparison.Ordinal));
        Assert.True(markdown.IndexOf("左", StringComparison.Ordinal) < markdown.IndexOf("右", StringComparison.Ordinal));
        Assert.DoesNotContain("ノート", markdown, StringComparison.Ordinal);

        var complete = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(ContentPolicy: "complete")).Serialize(graph);
        Assert.True(complete.IndexOf("右", StringComparison.Ordinal) < complete.IndexOf("ノート", StringComparison.Ordinal));
    }

    [Fact]
    public void Pptx_two_column_reading_order_finishes_the_left_column_before_the_right()
    {
        DocumentNode ShapeAt(string id, string text, int order, double x, double y, double width = 250, double height = 60) => new(
            id, NodeKind.Shape, null, order, ContentLayer.Body, new TextNodeContent(text),
            Geometry: new Geometry("pptx-emu", x, y, width, height),
            Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["shape_role"] = JsonSerializer.SerializeToElement("body") });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc-columns", DocumentFormatKind.Pptx,
            [new DocumentPartition("slide1", 0,
            [
                ShapeAt("right-heading", "右見出し", 0, 650, 150),
                ShapeAt("left-body", "左本文", 1, 100, 260),
                ShapeAt("full-heading", "全幅見出し", 2, 80, 70, 850, 50),
                ShapeAt("right-body", "右本文", 3, 650, 270),
                ShapeAt("left-heading", "左見出し", 4, 100, 140),
            ])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        Assert.True(markdown.IndexOf("全幅見出し", StringComparison.Ordinal) < markdown.IndexOf("左見出し", StringComparison.Ordinal));
        Assert.True(markdown.IndexOf("左見出し", StringComparison.Ordinal) < markdown.IndexOf("左本文", StringComparison.Ordinal));
        Assert.True(markdown.IndexOf("左本文", StringComparison.Ordinal) < markdown.IndexOf("右見出し", StringComparison.Ordinal));
        Assert.True(markdown.IndexOf("右見出し", StringComparison.Ordinal) < markdown.IndexOf("右本文", StringComparison.Ordinal));
    }

    [Fact]
    public void Rich_text_preserves_safe_color_and_highlight_decorations()
    {
        var graph = new DocumentGraph(
            DocumentGraph.CurrentSchemaVersion,
            "doc-rich-text",
            DocumentFormatKind.Docx,
            [new DocumentPartition("document", 0,
            [
                new DocumentNode(
                    "paragraph",
                    NodeKind.Paragraph,
                    null,
                    0,
                    ContentLayer.Body,
                    new RichTextNodeContent(
                    [
                        new TextRun("colored", Color: "b42318"),
                        new TextRun(" highlighted", HighlightColor: "FFFF00"),
                        new TextRun(" unsafe", Color: "red;display:none"),
                    ])),
            ])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);

        Assert.Contains("<span style=\"color:#B42318\">colored</span>", markdown, StringComparison.Ordinal);
        Assert.Contains("<mark> highlighted</mark>", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("display:none", markdown, StringComparison.Ordinal);
        Assert.Contains(" unsafe", markdown, StringComparison.Ordinal);
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

    private static DocumentNode Shape(string id, string text, string role, int order) => new(
        id, NodeKind.Shape, null, order, ContentLayer.Body, new TextNodeContent(text),
        Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["shape_role"] = JsonSerializer.SerializeToElement(role),
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
