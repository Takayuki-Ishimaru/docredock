using Rtmd.Api;
using Rtmd.Core.Diff;
using Rtmd.Core.Documents;
using Rtmd.Markdown;

namespace Rtmd.Tests.Api;

public sealed class MarkdownGraphEditorTests
{
    [Fact]
    public void Applies_text_edit_and_explicit_delete_to_the_same_graph_nodes()
    {
        var baseline = new DocumentGraph(
            DocumentGraph.CurrentSchemaVersion,
            "doc_1",
            DocumentFormatKind.Docx,
            [new DocumentPartition("part-0001", 0,
            [
                Node("n_edit", "before", 0),
                Node("n_delete", "delete me", 1),
            ])]);
        var markdown = new RtmdMarkdownSerializer().Serialize(baseline).Markdown
            .Replace("before", "after", StringComparison.Ordinal)
            .Replace(
                "<!--rtmd:block id=n_delete kind=paragraph editability=text operations=replace-text,explicit-delete constraints=preserve-kind,preserve-order-->\ndelete me\n\n",
                "<!--rtmd:delete id=n_delete-->\n",
                StringComparison.Ordinal);

        var result = new MarkdownGraphEditor().Apply(baseline, markdown);

        Assert.True(result.IsValid);
        Assert.Equal("after", Assert.IsType<TextNodeContent>(result.EditedGraph.FindNode("n_edit")!.Content).Text);
        Assert.Null(result.EditedGraph.FindNode("n_delete"));
        Assert.Contains(result.Diff.PatchSet.Operations, operation => operation.Kind == PatchOperationKind.ReplaceContent && operation.NodeId == "n_edit");
        Assert.Contains(result.Diff.PatchSet.Operations, operation => operation.Kind == PatchOperationKind.ExplicitDelete && operation.NodeId == "n_delete");
    }

    [Fact]
    public void Applies_same_shape_table_edits_and_rejects_passthrough_delete()
    {
        var table = new DocumentNode("table-1", NodeKind.Table, null, 0, ContentLayer.Body,
            new TableNodeContent([new[] { "A", "B" }]), Editability: NodeEditability.EditableWithConstraints);
        var image = new DocumentNode("image-1", NodeKind.Image, null, 1, ContentLayer.Body,
            new ReferenceNodeContent("asset.png", "diagram"), Editability: NodeEditability.Passthrough);
        var graph = new DocumentGraph("1.1", "doc_1", DocumentFormatKind.Docx,
            [new DocumentPartition("part-1", 0, [table, image])]);
        var projection = new RtmdMarkdownSerializer().Serialize(graph, new MarkdownSerializationOptions
        {
            ProjectionId = "proj_1",
            RoundTripStore = "doc.rtmd",
        }).Markdown;
        projection = projection.Replace("A | B", "A | Changed", StringComparison.Ordinal)
            .Replace(
                "<!--rtmd:block id=image-1 kind=image editability=protected operations=none constraints=preserve-marker,preserve-content-->\n![diagram](asset.png)\n\n",
                "<!--rtmd:delete id=image-1-->\n",
                StringComparison.Ordinal);

        var result = new MarkdownGraphEditor().Apply(graph, projection);

        Assert.Equal("Changed", Assert.IsType<TableNodeContent>(result.EditedGraph.FindNode("table-1")!.Content).Rows[0][1]);
        Assert.NotNull(result.EditedGraph.FindNode("image-1"));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ProtectedNodeDelete");
    }

    [Fact]
    public void Unchanged_protected_table_and_trailing_document_whitespace_are_projection_equivalent()
    {
        var graph = new DocumentGraph("1.1", "doc_1", DocumentFormatKind.Pptx,
        [
            new DocumentPartition("slide1", 0,
            [
                new DocumentNode("table-1", NodeKind.Table, null, 0, ContentLayer.Body,
                    new TableNodeContent([new[] { "Gate", "Status" }, new[] { "Integrity", "PASS" }]),
                    Editability: NodeEditability.Protected),
                new DocumentNode("footer-1", NodeKind.Footer, null, 1, ContentLayer.Furniture,
                    new TextNodeContent("Internal footer  "), Editability: NodeEditability.Protected),
            ])
        ]);
        var projection = new RtmdMarkdownSerializer().Serialize(graph).Markdown;

        var result = new MarkdownGraphEditor().Apply(graph, projection);

        Assert.True(result.IsValid);
        Assert.Empty(result.Diff.PatchSet.Operations);
    }

    [Fact]
    public void Unchanged_list_item_is_projection_equivalent()
    {
        var graph = new DocumentGraph("1.1", "doc_1", DocumentFormatKind.Docx,
            [new DocumentPartition("part-1", 0, [new DocumentNode("list-1", NodeKind.ListItem, null, 0,
                ContentLayer.Body, new TextNodeContent("First item"))])]);

        var result = new MarkdownGraphEditor().Apply(graph, new RtmdMarkdownSerializer().Serialize(graph).Markdown);

        Assert.True(result.IsValid);
        Assert.Empty(result.Diff.PatchSet.Operations);
    }

    [Fact]
    public void Rejects_kind_changes_and_adds_new_block_to_its_declared_partition()
    {
        var graph = new DocumentGraph("1.1", "doc_1", DocumentFormatKind.Docx,
        [
            new DocumentPartition("part-a", 0, [Node("n_1", "one", 0)]),
            new DocumentPartition("part-b", 1, [])
        ]);
        var projection = new RtmdMarkdownSerializer().Serialize(graph).Markdown;
        var kindChanged = projection.Replace("id=n_1 kind=paragraph", "id=n_1 kind=heading", StringComparison.Ordinal);

        var rejected = new MarkdownGraphEditor().Apply(graph, kindChanged);
        Assert.False(rejected.IsValid);
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Code == "BlockKindMismatch");

        var addition = projection.Replace("<!--rtmd:partition-end id=part-b baseline_nodes=0-->",
            "<!--rtmd:new kind=paragraph-->\nnew in b\n<!--rtmd:partition-end id=part-b baseline_nodes=0-->", StringComparison.Ordinal);
        var added = new MarkdownGraphEditor().Apply(graph, addition);
        Assert.True(added.IsValid);
        Assert.Single(added.EditedGraph.Partitions.Single(partition => partition.Id == "part-b").Nodes);
        Assert.DoesNotContain(added.EditedGraph.Partitions.Single(partition => partition.Id == "part-a").Nodes, node => node.Id != "n_1");
    }

    [Fact]
    public void Rejects_unknown_node_ids_including_explicit_delete()
    {
        var graph = new DocumentGraph("1.1", "doc_1", DocumentFormatKind.Docx,
            [new DocumentPartition("part-a", 0, [Node("n_1", "one", 0)])]);
        var projection = new RtmdMarkdownSerializer().Serialize(graph).Markdown;
        var unknownBlock = projection.Replace("<!--rtmd:partition-end id=part-a baseline_nodes=1-->",
            "<!--rtmd:block id=n_unknown kind=paragraph-->\nunknown\n<!--rtmd:partition-end id=part-a baseline_nodes=2-->", StringComparison.Ordinal);
        var unknownDelete = projection.Replace(
            "<!--rtmd:block id=n_1 kind=paragraph editability=text operations=replace-text,explicit-delete constraints=preserve-kind,preserve-order-->\none\n\n",
            "<!--rtmd:delete id=n_unknown-->\n", StringComparison.Ordinal);

        var blockResult = new MarkdownGraphEditor().Apply(graph, unknownBlock);
        var deleteResult = new MarkdownGraphEditor().Apply(graph, unknownDelete);

        Assert.Contains(blockResult.Diagnostics, diagnostic => diagnostic.Code == "UnknownBlockId");
        Assert.Contains(deleteResult.Diagnostics, diagnostic => diagnostic.Code == "UnknownBlockId");
        Assert.False(blockResult.IsValid);
        Assert.False(deleteResult.IsValid);
    }

    [Fact]
    public void Rejects_new_xlsx_cells_that_have_no_address_anchor()
    {
        var cell = new DocumentNode("cell-1", NodeKind.Cell, null, 0, ContentLayer.Body, new TextNodeContent("value"),
            new SourceAnchor("xlsx", "/xl/worksheets/sheet1.xml", [new AnchorLocator("cell_address", "A1")]));
        var graph = new DocumentGraph("1.1", "doc_1", DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-Summary", 0, [cell])]);
        var projection = new RtmdMarkdownSerializer().Serialize(graph).Markdown.Replace(
            "<!--rtmd:partition-end id=sheet-Summary baseline_nodes=1-->",
            "<!--rtmd:new kind=cell-->\n- **B2:** new value\n<!--rtmd:partition-end id=sheet-Summary baseline_nodes=1-->", StringComparison.Ordinal);

        var result = new MarkdownGraphEditor().Apply(graph, projection);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedMarkdownAddition");
    }

    [Fact]
    public void Rejects_tampered_block_policy_attributes()
    {
        var graph = new DocumentGraph("1.1", "doc_1", DocumentFormatKind.Docx,
            [new DocumentPartition("part-a", 0, [Node("n_1", "one", 0)])]);
        var projection = new RtmdMarkdownSerializer().Serialize(graph).Markdown
            .Replace("operations=replace-text,explicit-delete", "operations=replace-text,move-node", StringComparison.Ordinal);

        var result = new MarkdownGraphEditor().Apply(graph, projection);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "BlockPolicyMismatch");
    }

    [Fact]
    public void AppliesRichTextMarkdownEditsWithoutFlatteningFormatting()
    {
        var graph = new DocumentGraph("1.1", "doc_rich", DocumentFormatKind.Docx,
        [
            new DocumentPartition("part-a", 0,
            [
                new DocumentNode("rich", NodeKind.Paragraph, null, 0, ContentLayer.Body,
                    new RichTextNodeContent([
                        new TextRun("通常 "),
                        new TextRun("重要", "Strong", Bold: true),
                        new TextRun("\n", Kind: TextRunKind.LineBreak),
                        new TextRun("確認", Underline: true),
                    ]))
            ])
        ]);
        var projection = new RtmdMarkdownSerializer().Serialize(graph).Markdown;

        var unchanged = new MarkdownGraphEditor().Apply(graph, projection);
        var edited = new MarkdownGraphEditor().Apply(graph, projection.Replace("**重要**", "**最重要**", StringComparison.Ordinal));

        Assert.True(unchanged.IsValid);
        Assert.Empty(unchanged.Diff.PatchSet.Operations);
        Assert.True(edited.IsValid);
        var content = Assert.IsType<RichTextNodeContent>(edited.EditedGraph.FindNode("rich")!.Content);
        var emphasized = Assert.Single(content.Runs, run => run.Text == "最重要");
        Assert.True(emphasized.Bold);
        Assert.Equal("Strong", emphasized.StyleId);
        Assert.Contains(content.Runs, run => run.Kind == TextRunKind.LineBreak);
        Assert.Contains(content.Runs, run => run.Text == "確認" && run.Underline);
    }

    [Fact]
    public void AppliesXlsxGridEditsAndRejectsContentInUnboundGapCells()
    {
        var graph = new DocumentGraph("1.1", "doc_grid", DocumentFormatKind.Xlsx,
        [
            new DocumentPartition("sheet-Summary", 0,
            [
                SpreadsheetCell("a1", "A1", "項目", 0),
                SpreadsheetCell("c1", "C1", "金額", 1),
                SpreadsheetCell("a2", "A2", "売上", 2),
                SpreadsheetCell("c2", "C2", "120", 3),
            ])
        ]);
        var projection = new RtmdMarkdownSerializer().Serialize(graph).Markdown;

        var edited = new MarkdownGraphEditor().Apply(graph, projection.Replace("| 2 | 売上 | 120 |", "| 2 | 売上 | 125 |", StringComparison.Ordinal));
        var unsupportedProjection = projection
            .Replace("| Row | A | C |", "| Row | A | B | C |", StringComparison.Ordinal)
            .Replace("| --- | --- | --- |", "| --- | --- | --- | --- |", StringComparison.Ordinal)
            .Replace("| 1 | 項目 | 金額 |", "| 1 | 項目 |  | 金額 |", StringComparison.Ordinal)
            .Replace("| 2 | 売上 | 120 |", "| 2 | 売上 | new | 120 |", StringComparison.Ordinal);
        var unsupportedAddition = new MarkdownGraphEditor().Apply(graph, unsupportedProjection);

        Assert.True(edited.IsValid);
        Assert.Equal("125", Assert.IsType<TextNodeContent>(edited.EditedGraph.FindNode("c2")!.Content).Text);
        Assert.False(unsupportedAddition.IsValid);
        Assert.Contains(unsupportedAddition.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedSpreadsheetCellAddition");
    }

    [Fact]
    public void AppliesEditsAcrossMultipleCompactXlsxSections()
    {
        var graph = new DocumentGraph("1.1", "doc_sections", DocumentFormatKind.Xlsx,
        [
            new DocumentPartition("sheet-Summary", 0,
            [
                SpreadsheetCell("a1", "A1", "Title", 0),
                SpreadsheetCell("c2", "C2", "Near", 1),
                SpreadsheetCell("z20", "Z20", "Far", 2),
            ])
        ]);
        var projection = new RtmdMarkdownSerializer().Serialize(graph).Markdown;

        var result = new MarkdownGraphEditor().Apply(graph,
            projection.Replace("| 20 | Far |", "| 20 | Updated |", StringComparison.Ordinal));

        Assert.True(result.IsValid);
        Assert.Equal("Updated", Assert.IsType<TextNodeContent>(result.EditedGraph.FindNode("z20")!.Content).Text);
        Assert.Contains("range=A1:C2 baseline_nodes=2", projection);
        Assert.Contains("range=Z20:Z20 baseline_nodes=1", projection);
    }

    [Fact]
    public void TreatsProjectedFormulaResultAsReadOnlyAndEditsTheFormula()
    {
        var formula = SpreadsheetCell("b3", "B3", "240", 0) with
        {
            Extensions = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["formula"] = System.Text.Json.JsonSerializer.SerializeToElement("SUM(B1:B2)")
            }
        };
        var graph = new DocumentGraph("1.1", "doc_formula", DocumentFormatKind.Xlsx,
        [
            new DocumentPartition("sheet-Summary", 0, [formula])
        ]);
        var projection = new RtmdMarkdownSerializer().Serialize(graph).Markdown;

        var resultOnlyEdit = new MarkdownGraphEditor().Apply(graph, projection.Replace("→ 240", "→ 999", StringComparison.Ordinal));
        var formulaEdit = new MarkdownGraphEditor().Apply(graph, projection.Replace("SUM(B1:B2)", "SUM(B1:B3)", StringComparison.Ordinal));

        Assert.True(resultOnlyEdit.IsValid);
        Assert.Empty(resultOnlyEdit.Diff.PatchSet.Operations);
        Assert.True(formulaEdit.IsValid);
        Assert.Equal("SUM(B1:B3)", formulaEdit.EditedGraph.FindNode("b3")!.Extensions!["formula"].GetString());
        Assert.Equal("240", Assert.IsType<TextNodeContent>(formulaEdit.EditedGraph.FindNode("b3")!.Content).Text);
    }

    [Fact]
    public void PreservesMultilineXlsxCellThroughUnchangedGridProjection()
    {
        var graph = new DocumentGraph("1.1", "doc_multiline", DocumentFormatKind.Xlsx,
        [
            new DocumentPartition("sheet-Flow", 0,
            [
                SpreadsheetCell("b4", "B4", "利用者\n（営業担当）", 0)
            ])
        ]);
        var projection = new RtmdMarkdownSerializer().Serialize(graph).Markdown;

        var unchanged = new MarkdownGraphEditor().Apply(graph, projection);

        Assert.True(unchanged.IsValid);
        Assert.Equal("利用者\n（営業担当）", Assert.IsType<TextNodeContent>(unchanged.EditedGraph.FindNode("b4")!.Content).Text);
        Assert.Empty(unchanged.Diff.PatchSet.Operations);
    }

    [Fact]
    public void ProjectsAndEditsPptxTitleAndBodyRolesStructurally()
    {
        var graph = new DocumentGraph("1.1", "doc_slides", DocumentFormatKind.Pptx,
        [
            new DocumentPartition("slide1", 0,
            [
                Shape("title", "計画", "title", 0),
                Shape("body", "要点1\n要点2", "body", 1),
            ])
        ]);
        var projection = new RtmdMarkdownSerializer().Serialize(graph).Markdown;

        Assert.Contains("kind=shape editability=shape-text operations=replace-text constraints=existing-shape,no-delete,preserve-order role=title", projection);
        Assert.Contains("### 計画", projection);
        Assert.Contains("- 要点1\n- 要点2", projection);

        var edited = new MarkdownGraphEditor().Apply(graph, projection
            .Replace("### 計画", "### 実行計画", StringComparison.Ordinal)
            .Replace("- 要点2", "- 要点B", StringComparison.Ordinal));

        Assert.True(edited.IsValid);
        Assert.Equal("実行計画", Assert.IsType<TextNodeContent>(edited.EditedGraph.FindNode("title")!.Content).Text);
        Assert.Equal("要点1\n要点B", Assert.IsType<TextNodeContent>(edited.EditedGraph.FindNode("body")!.Content).Text);
    }

    private static DocumentNode SpreadsheetCell(string id, string address, string text, int order) => new(
        id, NodeKind.Cell, null, order, ContentLayer.Body, new TextNodeContent(text),
        new SourceAnchor("xlsx", "/xl/worksheets/sheet1.xml", [new AnchorLocator("cell_address", address)]));

    private static DocumentNode Shape(string id, string text, string role, int order) => new(
        id, NodeKind.Shape, null, order, ContentLayer.Body, new TextNodeContent(text),
        new SourceAnchor("pptx", "/ppt/slides/slide1.xml", [new AnchorLocator("shape_id", id)]),
        Editability: NodeEditability.EditableWithConstraints,
        Extensions: new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["shape_role"] = System.Text.Json.JsonSerializer.SerializeToElement(role)
        });

    private static DocumentNode Node(string id, string text, int order) => new(
        id,
        NodeKind.Paragraph,
        null,
        order,
        ContentLayer.Body,
        new TextNodeContent(text),
        new SourceAnchor("docx", "/word/document.xml", [new AnchorLocator("w14_para_id", id)]));
}
