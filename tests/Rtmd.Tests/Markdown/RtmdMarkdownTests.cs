using Rtmd.Markdown;
using Rtmd.Core.Documents;

namespace Rtmd.Tests.Markdown;

public sealed class RtmdMarkdownTests
{
    [Fact]
    public void SerializesDeterministicFrontMatterMarkersAndContributorMap()
    {
        var graph = new FakeGraph("doc_1", "docx", [
            new FakePartition("part-0001", [
                new FakeNode("n_heading", "Heading", "計画", 0),
                new FakeNode("n_body", "Paragraph", "本文", 1)
            ])
        ]);

        var serializer = new RtmdMarkdownSerializer();
        var first = serializer.Serialize(graph, new MarkdownSerializationOptions { ProjectionId = "p1" });
        var second = serializer.Serialize(graph, new MarkdownSerializationOptions { ProjectionId = "p1" });

        Assert.Equal(first.Markdown, second.Markdown);
        Assert.Contains("rtmd_schema: 1.0", first.Markdown);
        Assert.Contains("rtmd_rules: 1.0", first.Markdown);
        Assert.Contains("<!--rtmd:partition-begin id=part-0001 baseline_nodes=2-->", first.Markdown);
        Assert.Contains("<!--rtmd:document-end id=doc_1 partitions=1-->", first.Markdown);
        Assert.Contains(first.Contributions, x => x.NodeId == "n_heading" && x.Role == ProjectionRole.HeadingLabel);
        Assert.Contains(first.Contributions, x => x.NodeId == "n_body" && x.Role == ProjectionRole.PrimaryText);
    }

    [Fact]
    public void ParserMapsEditedTextToTheSameNodeAndSupportsNewAndExplicitDelete()
    {
        const string markdown = """
            ---
            rtmd_schema: 1.0
            document_id: doc_1
            source_format: docx
            roundtrip_store: doc.rtmd
            ---
            <!--rtmd:partition-begin id=part-0001 baseline_nodes=2-->
            <!--rtmd:block id=n_body kind=paragraph-->
            編集済み

            <!--rtmd:new kind=paragraph-->
            追加

            <!--rtmd:delete id=n_removed-->
            <!--rtmd:partition-end id=part-0001 baseline_nodes=2-->
            <!--rtmd:document-end id=doc_1 partitions=1-->
            """;

        var parsed = new RtmdMarkdownParser().Parse(markdown);
        Assert.True(parsed.IsComplete);
        var edits = RtmdMarkdownParser.MapEditsToNodes(parsed);
        Assert.Equal("編集済み", edits["n_body"].Text);
        Assert.Contains(parsed.Blocks, x => x.IsNew && x.Text == "追加");
        Assert.Contains(parsed.Blocks, x => x.IsExplicitDelete && x.NodeId == "n_removed");
    }

    [Fact]
    public void MissingBaselineNodeIsWarningAndNotImplicitDelete()
    {
        var parsed = new RtmdMarkdownParser().Parse("""
            ---
            rtmd_schema: 1.0
            document_id: doc_1
            source_format: docx
            ---
            <!--rtmd:partition-begin id=part-0001 baseline_nodes=1-->
            <!--rtmd:block id=n_present kind=paragraph-->
            残った本文
            <!--rtmd:partition-end id=part-0001 baseline_nodes=1-->
            <!--rtmd:document-end id=doc_1 partitions=1-->
            """);

        var diagnostics = RtmdMarkdownParser.FindMissingNodes(parsed, ["n_present", "n_missing"]);
        var missing = Assert.Single(diagnostics);
        Assert.Equal("RTMD007", missing.Code);
        Assert.Equal(MarkdownDiagnosticSeverity.Warning, missing.Severity);
        Assert.Contains("preserved", missing.Message);
    }

    [Fact]
    public void StrictParserRejectsTruncatedDocument()
    {
        var parsed = new RtmdMarkdownParser().Parse("""
            ---
            rtmd_schema: 1.0
            document_id: doc_1
            source_format: docx
            ---
            <!--rtmd:partition-begin id=part-0001 baseline_nodes=1-->
            <!--rtmd:block id=n_body kind=paragraph-->
            途中で切断
            """);

        Assert.False(parsed.IsComplete);
        Assert.Contains(parsed.Diagnostics, x => x.Code == "RTMD004" && x.Severity == MarkdownDiagnosticSeverity.Error);
    }

    [Fact]
    public void StrictParserRejectsContentAfterDocumentEnd()
    {
        var parsed = new RtmdMarkdownParser().Parse("""
            ---
            rtmd_schema: 1.0
            document_id: doc_1
            source_format: docx
            ---
            <!--rtmd:partition-begin id=part-0001 baseline_nodes=0-->
            <!--rtmd:partition-end id=part-0001 baseline_nodes=0-->
            <!--rtmd:document-end id=doc_1 partitions=1-->
            unexpected tail
            """);

        Assert.False(parsed.IsComplete);
        Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Code == "RTMD016");
    }

    [Fact]
    public void StrictParserRejectsDeleteMarkerThatDuplicatesBlockId()
    {
        var parsed = new RtmdMarkdownParser().Parse("""
            ---
            rtmd_schema: 1.0
            document_id: doc_1
            source_format: docx
            ---
            <!--rtmd:partition-begin id=part-0001 baseline_nodes=1-->
            <!--rtmd:block id=n_1 kind=paragraph-->
            text
            <!--rtmd:delete id=n_1-->
            <!--rtmd:partition-end id=part-0001 baseline_nodes=1-->
            <!--rtmd:document-end id=doc_1 partitions=1-->
            """);

        Assert.False(parsed.IsComplete);
        Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Code == "RTMD003");
    }

    [Fact]
    public void ContentPolicyFiltersHiddenMetadataAndAuditOnlyNodes()
    {
        var graph = new FakeGraph("doc_1", "docx", [
            new FakePartition("part-0001", [
                new FakeNode("body", "Paragraph", "body", 0),
                new FakeNode("hidden", "Paragraph", "hidden", 1, "Hidden"),
                new FakeNode("metadata", "Paragraph", "metadata", 2, "Metadata"),
                new FakeNode("comment", "Comment", "comment", 3)
            ])
        ]);
        var serializer = new RtmdMarkdownSerializer();

        var visible = serializer.Serialize(graph, new MarkdownSerializationOptions { ContentPolicy = "visible" });
        var complete = serializer.Serialize(graph, new MarkdownSerializationOptions { ContentPolicy = "complete" });
        var sanitized = serializer.Serialize(graph, new MarkdownSerializationOptions { ContentPolicy = "sanitized" });

        Assert.Contains("id=body", visible.Markdown);
        Assert.DoesNotContain("id=hidden", visible.Markdown);
        Assert.DoesNotContain("id=metadata", visible.Markdown);
        Assert.DoesNotContain("id=comment", visible.Markdown);
        Assert.Contains("id=hidden", complete.Markdown);
        Assert.Contains("id=metadata", complete.Markdown);
        Assert.Contains("id=comment", complete.Markdown);
        Assert.DoesNotContain("id=hidden", sanitized.Markdown);
        Assert.DoesNotContain("id=metadata", sanitized.Markdown);
        Assert.DoesNotContain("id=comment", sanitized.Markdown);
    }

    [Fact]
    public void CoreGraphProjectionUsesNaturalMarkdownWhileRetainingBlockMarkers()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_readable", DocumentFormatKind.Docx,
        [
            new DocumentPartition("part-0001", 0,
            [
                new DocumentNode("heading", NodeKind.Heading, null, 0, ContentLayer.Body, new TextNodeContent("Plan"), StyleId: "Heading2"),
                new DocumentNode("list", NodeKind.ListItem, null, 1, ContentLayer.Body, new TextNodeContent("First item")),
                new DocumentNode("table", NodeKind.Table, null, 2, ContentLayer.Body, new TableNodeContent([new[] { "Name", "Value" }, new[] { "Revenue", "120" }])),
                new DocumentNode("image", NodeKind.Image, null, 3, ContentLayer.Body, new ReferenceNodeContent("img_1", "Architecture")),
                new DocumentNode("link", NodeKind.Link, null, 4, ContentLayer.Body, new ReferenceNodeContent("https://example.test", "Reference"))
            ])
        ],
        Assets: new Dictionary<string, AssetDescriptor> { ["img_1"] = new("img_1", "hash", "image/png", "diagram.png") });

        var projection = new RtmdMarkdownSerializer().Serialize(graph, new MarkdownSerializationOptions { RoundTripStore = "proposal.rtmd" });

        Assert.Contains("<!--rtmd:block id=heading kind=heading editability=text operations=replace-text,explicit-delete constraints=preserve-kind,preserve-order-->", projection.Markdown);
        Assert.Contains("## Plan", projection.Markdown);
        Assert.Contains("- First item", projection.Markdown);
        Assert.Contains("| Name | Value |", projection.Markdown);
        Assert.Contains("| --- | --- |", projection.Markdown);
        Assert.Contains("![Architecture](proposal.rtmd/assets/diagram.png)", projection.Markdown);
        Assert.Contains("[Reference](https://example.test)", projection.Markdown);
    }

    [Fact]
    public void CoreGraphProjectionLabelsSheetsSlidesAndCells()
    {
        var sheet = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_sheet", DocumentFormatKind.Xlsx,
        [
            new DocumentPartition("sheet-Summary", 0,
            [
                new DocumentNode("cell", NodeKind.Cell, null, 0, ContentLayer.Body, new TextNodeContent("=SUM(A1:A2)"),
                    new SourceAnchor("xlsx", "/xl/worksheets/sheet1.xml", [new AnchorLocator("cell_address", "B3")]))
            ])
        ]);
        var slides = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_slides", DocumentFormatKind.Pptx,
            [new DocumentPartition("slide3", 0, [new DocumentNode("shape", NodeKind.Shape, null, 0, ContentLayer.Body, new TextNodeContent("Architecture"))])]);
        var serializer = new RtmdMarkdownSerializer();

        var sheetMarkdown = serializer.Serialize(sheet).Markdown;
        var slideMarkdown = serializer.Serialize(slides).Markdown;

        Assert.Contains("## Summary", sheetMarkdown);
        Assert.Contains("<!--rtmd:sheet-table range=B3:B3 baseline_nodes=1", sheetMarkdown);
        Assert.Contains("| 3 | " + (char)96 + "=SUM(A1:A2)" + (char)96 + " |", sheetMarkdown);
        Assert.Contains("## Slide 3", slideMarkdown);
    }

    [Fact]
    public void XlsxProjectionOmitsEmptyCoordinatesAndSplitsDistantRegions()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_compact", DocumentFormatKind.Xlsx,
        [
            new DocumentPartition("sheet-Summary", 0,
            [
                Cell("a1", "A1", "項目", 0),
                Cell("c1", "C1", "金額", 1),
                Cell("a2", "A2", "交通費", 2),
                Cell("c2", "C2", "1200", 3),
                Cell("z20", "Z20", "注記", 4),
            ])
        ]);

        var markdown = new RtmdMarkdownSerializer().Serialize(graph).Markdown;

        Assert.Contains("range=A1:C2 baseline_nodes=4", markdown);
        Assert.Contains("| Row | A | C |", markdown);
        Assert.DoesNotContain("| Row | A | B | C |", markdown);
        Assert.DoesNotContain("| 3 |", markdown);
        Assert.Contains("range=Z20:Z20 baseline_nodes=1", markdown);

        static DocumentNode Cell(string id, string address, string text, int order) => new(
            id, NodeKind.Cell, null, order, ContentLayer.Body, new TextNodeContent(text),
            new SourceAnchor("xlsx", "/xl/worksheets/sheet1.xml", [new AnchorLocator("cell_address", address)]));
    }

    [Fact]
    public void ParserKeepsPartitionOwnershipAndIgnoresMarkersInsideCodeFences()
    {
        var parsed = new RtmdMarkdownParser().Parse("""
            ---
            rtmd_schema: 1.0
            document_id: doc_1
            source_format: docx
            ---
            <!--rtmd:partition-begin id=part-a baseline_nodes=1-->
            <!--rtmd:block id=n_1 kind=paragraph-->
            ```text
            <!--rtmd:delete id=not-a-real-marker-->
            ```
            <!--rtmd:partition-end id=part-a baseline_nodes=1-->
            <!--rtmd:partition-begin id=part-b baseline_nodes=0-->
            <!--rtmd:new kind=paragraph-->
            added
            <!--rtmd:partition-end id=part-b baseline_nodes=0-->
            <!--rtmd:document-end id=doc_1 partitions=2-->
            """);

        Assert.True(parsed.IsComplete);
        Assert.Equal("part-a", Assert.Single(parsed.Blocks, block => block.NodeId == "n_1").PartitionId);
        Assert.Equal("part-b", Assert.Single(parsed.Blocks, block => block.IsNew).PartitionId);
        Assert.DoesNotContain(parsed.Blocks, block => block.NodeId == "not-a-real-marker");
    }

    [Fact]
    public void ParserRejectsBlocksOutsidePartitionsAndBaselineCountMismatch()
    {
        var parsed = new RtmdMarkdownParser().Parse("""
            ---
            rtmd_schema: 1.0
            document_id: doc_1
            source_format: docx
            ---
            <!--rtmd:block id=n_1 kind=paragraph-->
            outside
            <!--rtmd:partition-begin id=part-a baseline_nodes=2-->
            <!--rtmd:block id=n_2 kind=paragraph-->
            inside
            <!--rtmd:partition-end id=part-a baseline_nodes=2-->
            <!--rtmd:document-end id=doc_1 partitions=1-->
            """);

        Assert.False(parsed.IsComplete);
        Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Code == "RTMD020");
        Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Code == "RTMD021");
    }

    [Fact]
    public void ParserRejectsAnUnsupportedAiRulesVersion()
    {
        var markdown = new RtmdMarkdownSerializer().Serialize(new DocumentGraph(
            DocumentGraph.CurrentSchemaVersion,
            "doc_1",
            DocumentFormatKind.Docx,
            [new DocumentPartition("part-a", 0, [])])).Markdown
            .Replace("rtmd_rules: 1.0", "rtmd_rules: 9.9", StringComparison.Ordinal);

        var parsed = new RtmdMarkdownParser().Parse(markdown);

        Assert.False(parsed.IsComplete);
        Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Code == "RTMD022");
    }

    [Fact]
    public void ProjectsDocxRichTextAsReadableReversibleInlineMarkdown()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_rich", DocumentFormatKind.Docx,
        [
            new DocumentPartition("part-0001", 0,
            [
                new DocumentNode("rich", NodeKind.Paragraph, null, 0, ContentLayer.Body,
                    new RichTextNodeContent([
                        new TextRun("通常 "),
                        new TextRun("重要", Bold: true),
                        new TextRun(" と "),
                        new TextRun("注記", Italic: true, Underline: true),
                        new TextRun("\n", Kind: TextRunKind.LineBreak),
                        new TextRun("code", Code: true),
                    ]))
            ])
        ]);

        var projection = new RtmdMarkdownSerializer().Serialize(graph);
        var parsed = new RtmdMarkdownParser().Parse(projection.Markdown);

        Assert.Contains("rich-text=inline-v1", projection.Markdown);
        Assert.Contains("通常 **重要** と <u>_注記_</u><br>`code`", projection.Markdown);
        Assert.True(parsed.IsComplete);
    }

    [Fact]
    public void ProjectsXlsxCellsAsOneCoordinateAddressedGfmTable()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_sheet", DocumentFormatKind.Xlsx,
        [
            new DocumentPartition("sheet-Summary", 0,
            [
                Cell("a1", "A1", "項目", 0),
                Cell("b1", "B1", "金額", 1),
                Cell("a2", "A2", "売上", 2),
                Cell("b2", "B2", "120", 3),
            ])
        ]);

        var projection = new RtmdMarkdownSerializer().Serialize(graph);
        var parsed = new RtmdMarkdownParser().Parse(projection.Markdown);

        Assert.Contains("<!--rtmd:sheet-table range=A1:B2 baseline_nodes=4", projection.Markdown);
        Assert.Contains("| Row | A | B |", projection.Markdown);
        Assert.Contains("| 2 | 売上 | 120 |", projection.Markdown);
        Assert.DoesNotContain("- **A1:**", projection.Markdown);
        Assert.True(parsed.IsComplete);
        Assert.Single(parsed.Blocks, block => block.Kind == "sheet-table");
    }

    [Fact]
    public void ProjectsXlsxFormulaWithItsCachedCalculatedValue()
    {
        var formula = Cell("b3", "B3", "240", 0) with
        {
            Extensions = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["formula"] = System.Text.Json.JsonSerializer.SerializeToElement("SUM(B1:B2)")
            }
        };
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_formula", DocumentFormatKind.Xlsx,
        [
            new DocumentPartition("sheet-Summary", 0, [formula])
        ]);

        var projection = new RtmdMarkdownSerializer().Serialize(graph).Markdown;

        Assert.Contains("| 3 | `=SUM(B1:B2)` → 240 |", projection);
    }

    private static DocumentNode Cell(string id, string address, string text, int order) => new(
        id, NodeKind.Cell, null, order, ContentLayer.Body, new TextNodeContent(text),
        new SourceAnchor("xlsx", "/xl/worksheets/sheet1.xml", [new AnchorLocator("cell_address", address)]));

    private sealed record FakeGraph(string DocumentId, string Format, IReadOnlyList<FakePartition> Partitions);
    private sealed record FakePartition(string Id, IReadOnlyList<FakeNode> Nodes);
    private sealed record FakeNode(string Id, string Kind, string Text, int Order, string Layer = "Body");
}
