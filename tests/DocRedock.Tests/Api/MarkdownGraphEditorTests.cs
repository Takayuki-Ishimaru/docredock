using DocRedock.Api;
using DocRedock.Core.Diff;
using DocRedock.Core.Documents;
using DocRedock.Markdown;

namespace DocRedock.Tests.Api;

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
        var markdown = new DocRedockMarkdownSerializer().Serialize(baseline).Markdown
            .Replace("before", "after", StringComparison.Ordinal)
            .Replace(
                "<!--drmd:block id=n_delete kind=paragraph editability=text operations=replace-text,explicit-delete constraints=preserve-kind,preserve-order-->\ndelete me\n\n",
                "<!--drmd:delete id=n_delete-->\n",
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
            new TableNodeContent([new TableCell[] { "A", "B" }]), Editability: NodeEditability.EditableWithConstraints);
        var image = new DocumentNode("image-1", NodeKind.Image, null, 1, ContentLayer.Body,
            new ReferenceNodeContent("asset.png", "diagram"), Editability: NodeEditability.Passthrough);
        var graph = new DocumentGraph("1.1", "doc_1", DocumentFormatKind.Docx,
            [new DocumentPartition("part-1", 0, [table, image])]);
        var projection = new DocRedockMarkdownSerializer().Serialize(graph, new MarkdownSerializationOptions
        {
            ProjectionId = "proj_1",
            RoundTripStore = "doc.drmd",
        }).Markdown;
        projection = projection.Replace("A | B", "A | Changed", StringComparison.Ordinal)
            .Replace(
                "<!--drmd:block id=image-1 kind=image editability=protected operations=none constraints=preserve-marker,preserve-content-->\n![diagram](asset.png)\n\n",
                "<!--drmd:delete id=image-1-->\n",
                StringComparison.Ordinal);

        var result = new MarkdownGraphEditor().Apply(graph, projection);

        Assert.Equal("Changed", Assert.IsType<TableNodeContent>(result.EditedGraph.FindNode("table-1")!.Content).Rows[0][1].Text);
        Assert.NotNull(result.EditedGraph.FindNode("image-1"));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ProtectedNodeDelete");
    }

    [Fact]
    public void Merged_table_projection_preserves_spans_and_validates_continuations()
    {
        var table = new DocumentNode("table-1", NodeKind.Table, null, 0, ContentLayer.Body,
            new TableNodeContent(
            [
                [new TableCell("A"), new TableCell("Merged", ColSpan: 2, RowSpan: 2)],
                [new TableCell("A2"), new TableCell(string.Empty, ColSpan: 2, RowSpan: 0)],
            ]),
            Editability: NodeEditability.EditableWithConstraints);
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_merged", DocumentFormatKind.Docx,
            [new DocumentPartition("part-1", 0, [table])]);
        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;

        var unchanged = new MarkdownGraphEditor().Apply(graph, projection);
        var legacy = new MarkdownGraphEditor().Apply(graph,
            projection.Replace("| A2 |  |  |", "| A2 | Merged | Merged |", StringComparison.Ordinal));
        var changed = new MarkdownGraphEditor().Apply(graph,
            projection.Replace("| A | Merged |  |", "| A | Updated |  |", StringComparison.Ordinal));
        var continuationEdit = new MarkdownGraphEditor().Apply(graph,
            projection.Replace("| A2 |  |  |", "| A2 | changed |  |", StringComparison.Ordinal));
        var shapeChange = new MarkdownGraphEditor().Apply(graph,
            projection.Replace("| A | Merged |  |", "| A | Merged |  | extra |", StringComparison.Ordinal));

        Assert.Contains("drmd_rules: 1.1", projection, StringComparison.Ordinal);
        Assert.True(unchanged.IsValid);
        Assert.Empty(unchanged.Diff.PatchSet.Operations);
        Assert.True(legacy.IsValid);
        Assert.Empty(legacy.Diff.PatchSet.Operations);
        Assert.True(changed.IsValid);
        var changedCell = Assert.IsType<TableNodeContent>(changed.EditedGraph.FindNode("table-1")!.Content).Rows[0][1];
        Assert.Equal("Updated", changedCell.Text);
        Assert.Equal(2, changedCell.ColSpan);
        Assert.Equal(2, changedCell.RowSpan);
        Assert.False(continuationEdit.IsValid);
        Assert.Contains(continuationEdit.Diagnostics, diagnostic => diagnostic.Code == "MergedTableContinuationEdited");
        Assert.False(shapeChange.IsValid);
        Assert.Contains(shapeChange.Diagnostics, diagnostic => diagnostic.Code == "MergedTableShapeChanged");
    }

    [Fact]
    public void Unchanged_protected_table_and_trailing_document_whitespace_are_projection_equivalent()
    {
        var graph = new DocumentGraph("1.1", "doc_1", DocumentFormatKind.Pptx,
        [
            new DocumentPartition("slide1", 0,
            [
                new DocumentNode("table-1", NodeKind.Table, null, 0, ContentLayer.Body,
                    new TableNodeContent([new TableCell[] { "Gate", "Status" }, new TableCell[] { "Integrity", "PASS" }]),
                    Editability: NodeEditability.Protected),
                new DocumentNode("footer-1", NodeKind.Footer, null, 1, ContentLayer.Furniture,
                    new TextNodeContent("Internal footer  "), Editability: NodeEditability.Protected),
            ])
        ]);
        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;

        var result = new MarkdownGraphEditor().Apply(graph, projection);

        Assert.True(result.IsValid);
        Assert.Empty(result.Diff.PatchSet.Operations);
    }

    [Fact]
    public void Inline_link_projection_is_f0_equivalent_and_protected_content_stays_rejected()
    {
        const string target = "https://example.test/docs";
        var rich = new DocumentNode("rich", NodeKind.Paragraph, null, 0, ContentLayer.Body,
            new RichTextNodeContent([
                new TextRun("See "),
                new TextRun("Reference", Underline: true, LinkTarget: target)
            ]));
        var link = new DocumentNode("link", NodeKind.Link, "rich", 0, ContentLayer.Body,
            new ReferenceNodeContent(target, "Reference"), Editability: NodeEditability.Passthrough);
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_link", DocumentFormatKind.Docx,
            [new DocumentPartition("part-1", 0, [rich, link])]);
        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;
        var protectedMarker = "<!--drmd:block id=link kind=link editability=protected operations=none constraints=preserve-marker,preserve-content-->";
        var unchanged = new MarkdownGraphEditor().Apply(graph, projection);
        var tampered = new MarkdownGraphEditor().Apply(graph,
            projection.Replace(protectedMarker + Environment.NewLine + Environment.NewLine,
                protectedMarker + Environment.NewLine + "changed" + Environment.NewLine + Environment.NewLine,
                StringComparison.Ordinal));

        Assert.Contains("[<u>Reference</u>](https://example.test/docs)", projection, StringComparison.Ordinal);
        Assert.Equal(1, projection.Split("Reference", StringSplitOptions.None).Length - 1);
        Assert.True(unchanged.IsValid);
        Assert.Empty(unchanged.Diff.PatchSet.Operations);
        Assert.False(tampered.IsValid);
        Assert.Contains(tampered.Diagnostics,
            diagnostic => diagnostic.Code == "ProtectedNodeEdit" && diagnostic.NodeId == "link");
    }

    [Fact]
    public void Unchanged_rich_code_block_is_projection_equivalent()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_code", DocumentFormatKind.Docx,
            [new DocumentPartition("part-1", 0,
            [
                new DocumentNode("code", NodeKind.CodeBlock, null, 0, ContentLayer.Body,
                    new RichTextNodeContent([
                        new TextRun("{", Code: true),
                        new TextRun("\n", Kind: TextRunKind.LineBreak),
                        new TextRun("  \"ok\": true", Code: true),
                        new TextRun("\n", Kind: TextRunKind.LineBreak),
                        new TextRun("}", Code: true)
                    ]))
            ])]);
        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;

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

        var result = new MarkdownGraphEditor().Apply(graph, new DocRedockMarkdownSerializer().Serialize(graph).Markdown);

        Assert.True(result.IsValid);
        Assert.Empty(result.Diff.PatchSet.Operations);
    }

    [Fact]
    public void Encoded_image_path_is_projection_equivalent_for_a_protected_node()
    {
        var image = new DocumentNode("image-1", NodeKind.Image, null, 0, ContentLayer.Body,
            new ReferenceNodeContent("asset-1", "構成図"), Editability: NodeEditability.Protected);
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_1", DocumentFormatKind.Docx,
            [new DocumentPartition("part-1", 0, [image])],
            Assets: new Dictionary<string, AssetDescriptor>
            {
                ["asset-1"] = new("asset-1", "hash", "image/png", "diagram (最終).png"),
            });
        var markdown = new DocRedockMarkdownSerializer().Serialize(graph, new MarkdownSerializationOptions
        {
            RoundTripStore = "日本語 document (1).drmd",
        }).Markdown;

        var result = new MarkdownGraphEditor().Apply(graph, markdown);

        Assert.Contains("日本語%20document%20%281%29.drmd/assets/diagram%20%28最終%29.png", markdown, StringComparison.Ordinal);
        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "ProtectedNodeEdit");
    }

    [Fact]
    public void Xlsx_positioned_html_image_is_projection_equivalent_and_layout_is_protected()
    {
        var cell = SpreadsheetCell("cell-1", "A1", "見出し", 0);
        var image = new DocumentNode("image-1", NodeKind.Image, null, 1, ContentLayer.Body,
            new ReferenceNodeContent("asset-1", "IMG-01"), Editability: NodeEditability.Protected,
            Extensions: new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
            {
                ["row"] = System.Text.Json.JsonSerializer.SerializeToElement(8),
                ["column"] = System.Text.Json.JsonSerializer.SerializeToElement(2),
                ["width_emu"] = System.Text.Json.JsonSerializer.SerializeToElement(4_095_750L),
                ["height_emu"] = System.Text.Json.JsonSerializer.SerializeToElement(2_381_250L),
                ["image_media_type"] = System.Text.Json.JsonSerializer.SerializeToElement("image/png"),
            });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_xlsx_image", DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-Images", 0, [cell, image])],
            Assets: new Dictionary<string, AssetDescriptor>(StringComparer.Ordinal)
            {
                ["asset-1"] = new("asset-1", "hash", "image/png", "image.png"),
            });
        var markdown = new DocRedockMarkdownSerializer().Serialize(graph, new MarkdownSerializationOptions
        {
            RoundTripStore = "images.drmd",
        }).Markdown;

        var unchanged = new MarkdownGraphEditor().Apply(graph, markdown);
        var resized = new MarkdownGraphEditor().Apply(graph,
            markdown.Replace("width=\"430\"", "width=\"431\"", StringComparison.Ordinal));
        var moved = new MarkdownGraphEditor().Apply(graph,
            markdown.Replace("images.drmd/assets/image.png", "images.drmd/assets/other.png", StringComparison.Ordinal));
        var emptySource = new MarkdownGraphEditor().Apply(graph,
            markdown.Replace("src=\"images.drmd/assets/image.png\"", "src=\"\"", StringComparison.Ordinal));
        var injectedAttribute = new MarkdownGraphEditor().Apply(graph,
            markdown.Replace(" alt=\"IMG-01\"", " onerror=\"alert(1)\" alt=\"IMG-01\"", StringComparison.Ordinal));
        var trailingMarkup = new MarkdownGraphEditor().Apply(graph,
            markdown.Replace("height:auto\">", "height:auto\"><!-- injected -->", StringComparison.Ordinal));
        var changedStyle = new MarkdownGraphEditor().Apply(graph,
            markdown.Replace("max-width:49%", "max-width:100%", StringComparison.Ordinal));

        Assert.True(unchanged.IsValid);
        Assert.Empty(unchanged.Diff.PatchSet.Operations);
        Assert.DoesNotContain(unchanged.Diagnostics, diagnostic => diagnostic.Code == "ProtectedNodeEdit");
        foreach (var rejected in new[] { resized, moved, emptySource, injectedAttribute, trailingMarkup, changedStyle })
        {
            Assert.False(rejected.IsValid);
            Assert.Contains(rejected.Diagnostics,
                diagnostic => diagnostic.Code == "ProtectedNodeEdit" && diagnostic.NodeId == "image-1");
        }
    }

    [Fact]
    public void Rejects_kind_changes_and_adds_new_block_to_its_declared_partition()
    {
        var graph = new DocumentGraph("1.1", "doc_1", DocumentFormatKind.Docx,
        [
            new DocumentPartition("part-a", 0, [Node("n_1", "one", 0)]),
            new DocumentPartition("part-b", 1, [])
        ]);
        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;
        var kindChanged = projection.Replace("id=n_1 kind=paragraph", "id=n_1 kind=heading", StringComparison.Ordinal);

        var rejected = new MarkdownGraphEditor().Apply(graph, kindChanged);
        Assert.False(rejected.IsValid);
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Code == "BlockKindMismatch");

        var addition = projection.Replace("<!--drmd:partition-end id=part-b baseline_nodes=0-->",
            "<!--drmd:new kind=paragraph-->\nnew in b\n<!--drmd:partition-end id=part-b baseline_nodes=0-->", StringComparison.Ordinal);
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
        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;
        var unknownBlock = projection.Replace("<!--drmd:partition-end id=part-a baseline_nodes=1-->",
            "<!--drmd:block id=n_unknown kind=paragraph-->\nunknown\n<!--drmd:partition-end id=part-a baseline_nodes=2-->", StringComparison.Ordinal);
        var unknownDelete = projection.Replace(
            "<!--drmd:block id=n_1 kind=paragraph editability=text operations=replace-text,explicit-delete constraints=preserve-kind,preserve-order-->\none\n\n",
            "<!--drmd:delete id=n_unknown-->\n", StringComparison.Ordinal);

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
        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown.Replace(
            "<!--drmd:partition-end id=sheet-Summary baseline_nodes=1-->",
            "<!--drmd:new kind=cell-->\n- **B2:** new value\n<!--drmd:partition-end id=sheet-Summary baseline_nodes=1-->", StringComparison.Ordinal);

        var result = new MarkdownGraphEditor().Apply(graph, projection);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedMarkdownAddition");
    }

    [Fact]
    public void Rejects_tampered_block_policy_attributes()
    {
        var graph = new DocumentGraph("1.1", "doc_1", DocumentFormatKind.Docx,
            [new DocumentPartition("part-a", 0, [Node("n_1", "one", 0)])]);
        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown
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
        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;

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
        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;

        var edited = new MarkdownGraphEditor().Apply(graph, projection.Replace("| 売上 | 120 |", "| 売上 | 125 |", StringComparison.Ordinal));
        var unsupportedProjection = projection
            .Replace(" source-columns=A,C source-rows=1,2", string.Empty, StringComparison.Ordinal)
            .Replace("| 項目 | 金額 |\n| --- | --- |\n| 売上 | 120 |",
                "| Row | A | B | C |\n| --- | --- | --- | --- |\n| 1 | 項目 |  | 金額 |\n| 2 | 売上 | new | 120 |", StringComparison.Ordinal);
        var unsupportedAddition = new MarkdownGraphEditor().Apply(graph, unsupportedProjection);
        var tamperedMetadata = new MarkdownGraphEditor().Apply(graph,
            projection.Replace("source-rows=1,2", "source-rows=1,3", StringComparison.Ordinal));

        Assert.True(edited.IsValid);
        Assert.Equal("125", Assert.IsType<TextNodeContent>(edited.EditedGraph.FindNode("c2")!.Content).Text);
        Assert.False(unsupportedAddition.IsValid);
        Assert.Contains(unsupportedAddition.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedSpreadsheetCellAddition");
        Assert.False(tamperedMetadata.IsValid);
        Assert.Contains(tamperedMetadata.Diagnostics, diagnostic => diagnostic.Code == "SpreadsheetCoordinateMetadataMismatch");
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
                SpreadsheetCell("z2", "Z2", "Far", 2),
            ])
        ]);
        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;

        var result = new MarkdownGraphEditor().Apply(graph,
            projection.Replace("| Far |", "| Updated |", StringComparison.Ordinal));

        Assert.True(result.IsValid);
        Assert.Equal("Updated", Assert.IsType<TextNodeContent>(result.EditedGraph.FindNode("z2")!.Content).Text);
        Assert.Contains("range=A1:C2 source-columns=A,C source-rows=1,2 baseline_nodes=2", projection);
        Assert.Contains("range=Z2:Z2 source-columns=Z source-rows=2 baseline_nodes=1", projection);
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
        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;

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
        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;

        var unchanged = new MarkdownGraphEditor().Apply(graph, projection);

        Assert.True(unchanged.IsValid);
        Assert.Equal("利用者\n（営業担当）", Assert.IsType<TextNodeContent>(unchanged.EditedGraph.FindNode("b4")!.Content).Text);
        Assert.Empty(unchanged.Diff.PatchSet.Operations);
    }

    [Fact]
    public void PreservesFormattedXlsxRawCellWhenOnlyDisplayValueChanges()
    {
        var cell = SpreadsheetCell("a1", "A1", "45292", 0) with
        {
            Extensions = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
            {
                ["display_value"] = System.Text.Json.JsonSerializer.SerializeToElement("2024-01-01")
            }
        };
        var graph = new DocumentGraph("1.1", "doc_display", DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-Summary", 0, [cell])]);
        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;

        Assert.Contains("`45292` → 2024-01-01", projection, StringComparison.Ordinal);
        var unchanged = new MarkdownGraphEditor().Apply(graph, projection);
        var displayOnlyEdit = new MarkdownGraphEditor().Apply(
            graph, projection.Replace("2024-01-01", "2025-01-01", StringComparison.Ordinal));

        Assert.True(unchanged.IsValid);
        Assert.Empty(unchanged.Diff.PatchSet.Operations);
        Assert.True(displayOnlyEdit.IsValid);
        Assert.Empty(displayOnlyEdit.Diff.PatchSet.Operations);
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
        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;

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
