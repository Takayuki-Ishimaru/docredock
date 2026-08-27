using DocRedock.Markdown;
using DocRedock.Core.Documents;

namespace DocRedock.Tests.Markdown;

public sealed class DocRedockMarkdownTests
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

        var serializer = new DocRedockMarkdownSerializer();
        var first = serializer.Serialize(graph, new MarkdownSerializationOptions { ProjectionId = "p1" });
        var second = serializer.Serialize(graph, new MarkdownSerializationOptions { ProjectionId = "p1" });

        Assert.Equal(first.Markdown, second.Markdown);
        Assert.Contains("drmd_schema: 1.0", first.Markdown);
        Assert.Contains("drmd_rules: 1.1", first.Markdown);
        Assert.Contains("roundtrip_store: document.drmd", first.Markdown);
        Assert.Contains("<!--drmd:partition-begin id=part-0001 baseline_nodes=2-->", first.Markdown);
        Assert.Contains("<!--drmd:document-end id=doc_1 partitions=1-->", first.Markdown);
        Assert.Contains(first.Contributions, x => x.NodeId == "n_heading" && x.Role == ProjectionRole.HeadingLabel);
        Assert.Contains(first.Contributions, x => x.NodeId == "n_body" && x.Role == ProjectionRole.PrimaryText);
    }

    [Fact]
    public void ParserMapsEditedTextToTheSameNodeAndSupportsNewAndExplicitDelete()
    {
        const string markdown = """
            ---
            drmd_schema: 1.0
            document_id: doc_1
            source_format: docx
            roundtrip_store: doc.drmd
            ---
            <!--drmd:partition-begin id=part-0001 baseline_nodes=2-->
            <!--drmd:block id=n_body kind=paragraph-->
            編集済み

            <!--drmd:new kind=paragraph-->
            追加

            <!--drmd:delete id=n_removed-->
            <!--drmd:partition-end id=part-0001 baseline_nodes=2-->
            <!--drmd:document-end id=doc_1 partitions=1-->
            """;

        var parsed = new DocRedockMarkdownParser().Parse(markdown);
        Assert.True(parsed.IsComplete);
        var edits = DocRedockMarkdownParser.MapEditsToNodes(parsed);
        Assert.Equal("編集済み", edits["n_body"].Text);
        Assert.Contains(parsed.Blocks, x => x.IsNew && x.Text == "追加");
        Assert.Contains(parsed.Blocks, x => x.IsExplicitDelete && x.NodeId == "n_removed");
    }

    [Fact]
    public void Parser_preserves_spaces_japanese_and_parentheses_in_roundtrip_store()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_1", DocumentFormatKind.Docx,
            [new DocumentPartition("part-1", 0, [])]);
        var markdown = new DocRedockMarkdownSerializer().Serialize(graph, new MarkdownSerializationOptions
        {
            RoundTripStore = "日本語 document (1).drmd",
        }).Markdown;

        var parsed = new DocRedockMarkdownParser().Parse(markdown);

        Assert.True(parsed.IsComplete);
        Assert.Equal("日本語 document (1).drmd", parsed.RoundTripStore);
    }

    [Fact]
    public void MissingBaselineNodeIsWarningAndNotImplicitDelete()
    {
        var parsed = new DocRedockMarkdownParser().Parse("""
            ---
            drmd_schema: 1.0
            document_id: doc_1
            source_format: docx
            ---
            <!--drmd:partition-begin id=part-0001 baseline_nodes=1-->
            <!--drmd:block id=n_present kind=paragraph-->
            残った本文
            <!--drmd:partition-end id=part-0001 baseline_nodes=1-->
            <!--drmd:document-end id=doc_1 partitions=1-->
            """);

        var diagnostics = DocRedockMarkdownParser.FindMissingNodes(parsed, ["n_present", "n_missing"]);
        var missing = Assert.Single(diagnostics);
        Assert.Equal("DRMD007", missing.Code);
        Assert.Equal(MarkdownDiagnosticSeverity.Warning, missing.Severity);
        Assert.Contains("preserved", missing.Message);
    }

    [Fact]
    public void StrictParserRejectsTruncatedDocument()
    {
        var parsed = new DocRedockMarkdownParser().Parse("""
            ---
            drmd_schema: 1.0
            document_id: doc_1
            source_format: docx
            ---
            <!--drmd:partition-begin id=part-0001 baseline_nodes=1-->
            <!--drmd:block id=n_body kind=paragraph-->
            途中で切断
            """);

        Assert.False(parsed.IsComplete);
        Assert.Contains(parsed.Diagnostics, x => x.Code == "DRMD004" && x.Severity == MarkdownDiagnosticSeverity.Error);
    }

    [Fact]
    public void StrictParserRejectsContentAfterDocumentEnd()
    {
        var parsed = new DocRedockMarkdownParser().Parse("""
            ---
            drmd_schema: 1.0
            document_id: doc_1
            source_format: docx
            ---
            <!--drmd:partition-begin id=part-0001 baseline_nodes=0-->
            <!--drmd:partition-end id=part-0001 baseline_nodes=0-->
            <!--drmd:document-end id=doc_1 partitions=1-->
            unexpected tail
            """);

        Assert.False(parsed.IsComplete);
        Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Code == "DRMD016");
    }

    [Fact]
    public void StrictParserRejectsDeleteMarkerThatDuplicatesBlockId()
    {
        var parsed = new DocRedockMarkdownParser().Parse("""
            ---
            drmd_schema: 1.0
            document_id: doc_1
            source_format: docx
            ---
            <!--drmd:partition-begin id=part-0001 baseline_nodes=1-->
            <!--drmd:block id=n_1 kind=paragraph-->
            text
            <!--drmd:delete id=n_1-->
            <!--drmd:partition-end id=part-0001 baseline_nodes=1-->
            <!--drmd:document-end id=doc_1 partitions=1-->
            """);

        Assert.False(parsed.IsComplete);
        Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Code == "DRMD003");
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
        var serializer = new DocRedockMarkdownSerializer();

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
                new DocumentNode("table", NodeKind.Table, null, 2, ContentLayer.Body, new TableNodeContent([new TableCell[] { "Name", "Value" }, new TableCell[] { "Revenue", "120" }])),
                new DocumentNode("image", NodeKind.Image, null, 3, ContentLayer.Body, new ReferenceNodeContent("img_1", "Architecture")),
                new DocumentNode("link", NodeKind.Link, null, 4, ContentLayer.Body, new ReferenceNodeContent("https://example.test", "Reference"))
            ])
        ],
        Assets: new Dictionary<string, AssetDescriptor> { ["img_1"] = new("img_1", "hash", "image/png", "diagram.png") });

        var projection = new DocRedockMarkdownSerializer().Serialize(graph, new MarkdownSerializationOptions { RoundTripStore = "日本語 proposal (v1).drmd" });

        Assert.Contains("<!--drmd:block id=heading kind=heading editability=text operations=replace-text,explicit-delete constraints=preserve-kind,preserve-order-->", projection.Markdown);
        Assert.Contains("## Plan", projection.Markdown);
        Assert.Contains("- First item", projection.Markdown);
        Assert.Contains("| Name | Value |", projection.Markdown);
        Assert.Contains("| --- | --- |", projection.Markdown);
        Assert.Contains("![Architecture](日本語%20proposal%20%28v1%29.drmd/assets/diagram.png)", projection.Markdown);
        Assert.Contains("[Reference](https://example.test)", projection.Markdown);
    }

    [Fact]
    public void CoreGraphProjectionPreservesOrderedListsCodeBlocksAndInlineLinks()
    {
        const string target = "https://example.test/docs";
        var listExtensions = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["list_format"] = System.Text.Json.JsonSerializer.SerializeToElement("ordered"),
            ["list_number"] = System.Text.Json.JsonSerializer.SerializeToElement(3)
        };
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_visual", DocumentFormatKind.Docx,
        [
            new DocumentPartition("part-0001", 0,
            [
                new DocumentNode("ordered", NodeKind.ListItem, null, 0, ContentLayer.Body,
                    new TextNodeContent("Third item"), Extensions: listExtensions),
                new DocumentNode("code", NodeKind.CodeBlock, null, 1, ContentLayer.Body,
                    new RichTextNodeContent([
                        new TextRun("{", Code: true),
                        new TextRun("\n", Kind: TextRunKind.LineBreak),
                        new TextRun("  \"profile\": \"roundtrip\"", Code: true),
                        new TextRun("\n", Kind: TextRunKind.LineBreak),
                        new TextRun("}", Code: true)
                    ])),
                new DocumentNode("rich", NodeKind.Paragraph, null, 2, ContentLayer.Body,
                    new RichTextNodeContent([
                        new TextRun("See "),
                        new TextRun("Project", Underline: true, LinkTarget: target)
                    ])),
                new DocumentNode("link", NodeKind.Link, "rich", 2, ContentLayer.Body,
                    new ReferenceNodeContent(target, "Project"), Editability: NodeEditability.Passthrough)
            ])
        ]);

        var projection = new DocRedockMarkdownSerializer().Serialize(graph);
        var normalized = projection.Markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        var parsed = new DocRedockMarkdownParser().Parse(projection.Markdown);
        var inline = DocRedockInlineMarkdown.Parse("See [<u>Project</u>](https://example.test/docs)",
            ((RichTextNodeContent)graph.Partitions[0].Nodes[2].Content).Runs);

        Assert.Contains("3. Third item", normalized);
        Assert.Contains("```\n{\n  \"profile\": \"roundtrip\"\n}\n```", normalized);
        Assert.DoesNotContain("`{`<br>", normalized);
        Assert.Contains("[<u>Project</u>](https://example.test/docs)", normalized);
        Assert.Equal(1, normalized.Split("Project", StringSplitOptions.None).Length - 1);
        Assert.Contains("<!--drmd:block id=link kind=link", normalized);
        Assert.Contains(inline.Runs, run => run.Text == "Project" && run.Underline && run.LinkTarget == target);
        Assert.True(parsed.IsComplete);
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
        var serializer = new DocRedockMarkdownSerializer();

        var sheetMarkdown = serializer.Serialize(sheet).Markdown;
        var slideMarkdown = serializer.Serialize(slides).Markdown;

        Assert.Contains("## Summary", sheetMarkdown);
        Assert.Contains("<!--drmd:sheet-table range=B3:B3 source-columns=B source-rows=3 baseline_nodes=1", sheetMarkdown);
        Assert.Contains("| " + (char)96 + "=SUM(A1:A2)" + (char)96 + " |", sheetMarkdown);
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

        var markdown = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;

        Assert.Contains("range=A1:C2 source-columns=A,C source-rows=1,2 baseline_nodes=4", markdown);
        Assert.Contains("| 項目 | 金額 |", markdown);
        Assert.Contains("| 交通費 | 1200 |", markdown);
        Assert.DoesNotContain("| Row | A | C |", markdown);
        Assert.DoesNotContain("| Row | A | B | C |", markdown);
        Assert.DoesNotContain("| A | B | C |", markdown);
        Assert.Contains("range=Z20:Z20 source-columns=Z source-rows=20 baseline_nodes=1", markdown);

        static DocumentNode Cell(string id, string address, string text, int order) => new(
            id, NodeKind.Cell, null, order, ContentLayer.Body, new TextNodeContent(text),
            new SourceAnchor("xlsx", "/xl/worksheets/sheet1.xml", [new AnchorLocator("cell_address", address)]));
    }

    [Fact]
    public void XlsxRoundtripInterleavesSameRowImagesWithSourceLayout()
    {
        var imageExtensions = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
        {
            ["row"] = System.Text.Json.JsonSerializer.SerializeToElement(8),
            ["width_emu"] = System.Text.Json.JsonSerializer.SerializeToElement(4_095_750L),
            ["height_emu"] = System.Text.Json.JsonSerializer.SerializeToElement(2_381_250L),
            ["image_media_type"] = System.Text.Json.JsonSerializer.SerializeToElement("image/png"),
        };
        var left = new DocumentNode("image-left", NodeKind.Image, null, 5, ContentLayer.Body,
            new ReferenceNodeContent("left", "IMG-01"), Editability: NodeEditability.Protected,
            Extensions: new Dictionary<string, System.Text.Json.JsonElement>(imageExtensions, StringComparer.Ordinal)
            {
                ["column"] = System.Text.Json.JsonSerializer.SerializeToElement(2),
            });
        var right = new DocumentNode("image-right", NodeKind.Image, null, 4, ContentLayer.Body,
            new ReferenceNodeContent("right", "IMG-02"), Editability: NodeEditability.Protected,
            Extensions: new Dictionary<string, System.Text.Json.JsonElement>(imageExtensions, StringComparer.Ordinal)
            {
                ["column"] = System.Text.Json.JsonSerializer.SerializeToElement(28),
            });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_images", DocumentFormatKind.Xlsx,
        [
            new DocumentPartition("sheet-Images", 0,
            [
                Cell("a1", "A1", "画像検証", 0),
                Cell("b5", "B5", "9.1", 1),
                Cell("ab5", "AB5", "9.2", 2),
                Cell("b28", "B28", "画像説明", 3),
                right,
                left,
            ])
        ],
        Assets: new Dictionary<string, AssetDescriptor>(StringComparer.Ordinal)
        {
            ["left"] = new("left", "hash-left", "image/png", "left.png"),
            ["right"] = new("right", "hash-right", "image/png", "right.png"),
        });

        var projection = new DocRedockMarkdownSerializer().Serialize(graph, new MarkdownSerializationOptions
        {
            RoundTripStore = "images.drmd",
        });

        var firstTable = projection.Markdown.IndexOf("range=A1:AB5", StringComparison.Ordinal);
        var leftImage = projection.Markdown.IndexOf("id=image-left", StringComparison.Ordinal);
        var rightImage = projection.Markdown.IndexOf("id=image-right", StringComparison.Ordinal);
        var laterTable = projection.Markdown.IndexOf("range=B28:B28", StringComparison.Ordinal);
        Assert.True(firstTable >= 0 && firstTable < leftImage && leftImage < rightImage && rightImage < laterTable);
        var imageLine = projection.Markdown.Split('\n').Single(line => line.Contains("id=image-left", StringComparison.Ordinal));
        Assert.Contains("id=image-right", imageLine, StringComparison.Ordinal);
        Assert.Contains("<img src=\"images.drmd/assets/left.png\" alt=\"IMG-01\" width=\"430\" height=\"250\" style=\"max-width:49%;height:auto\">", imageLine, StringComparison.Ordinal);
        Assert.Contains("<img src=\"images.drmd/assets/right.png\" alt=\"IMG-02\" width=\"430\" height=\"250\" style=\"max-width:49%;height:auto\">", imageLine, StringComparison.Ordinal);
        Assert.Contains(projection.Contributions, contribution => contribution.NodeId == "image-left" && contribution.Role == ProjectionRole.ImageReference);
        Assert.Contains(projection.Contributions, contribution => contribution.NodeId == "image-right" && contribution.Role == ProjectionRole.ImageReference);
        Assert.True(new DocRedockMarkdownParser().Parse(projection.Markdown, new MarkdownParseOptions { Strict = true }).IsComplete);
    }

    [Fact]
    public void ParserKeepsPartitionOwnershipAndIgnoresMarkersInsideCodeFences()
    {
        var parsed = new DocRedockMarkdownParser().Parse("""
            ---
            drmd_schema: 1.0
            document_id: doc_1
            source_format: docx
            ---
            <!--drmd:partition-begin id=part-a baseline_nodes=1-->
            <!--drmd:block id=n_1 kind=paragraph-->
            ```text
            <!--drmd:delete id=not-a-real-marker-->
            ```
            <!--drmd:partition-end id=part-a baseline_nodes=1-->
            <!--drmd:partition-begin id=part-b baseline_nodes=0-->
            <!--drmd:new kind=paragraph-->
            added
            <!--drmd:partition-end id=part-b baseline_nodes=0-->
            <!--drmd:document-end id=doc_1 partitions=2-->
            """);

        Assert.True(parsed.IsComplete);
        Assert.Equal("part-a", Assert.Single(parsed.Blocks, block => block.NodeId == "n_1").PartitionId);
        Assert.Equal("part-b", Assert.Single(parsed.Blocks, block => block.IsNew).PartitionId);
        Assert.DoesNotContain(parsed.Blocks, block => block.NodeId == "not-a-real-marker");
    }

    [Fact]
    public void ParserRejectsBlocksOutsidePartitionsAndBaselineCountMismatch()
    {
        var parsed = new DocRedockMarkdownParser().Parse("""
            ---
            drmd_schema: 1.0
            document_id: doc_1
            source_format: docx
            ---
            <!--drmd:block id=n_1 kind=paragraph-->
            outside
            <!--drmd:partition-begin id=part-a baseline_nodes=2-->
            <!--drmd:block id=n_2 kind=paragraph-->
            inside
            <!--drmd:partition-end id=part-a baseline_nodes=2-->
            <!--drmd:document-end id=doc_1 partitions=1-->
            """);

        Assert.False(parsed.IsComplete);
        Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Code == "DRMD020");
        Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Code == "DRMD021");
    }

    [Fact]
    public void ParserRejectsAnUnsupportedAiRulesVersion()
    {
        var markdown = new DocRedockMarkdownSerializer().Serialize(new DocumentGraph(
            DocumentGraph.CurrentSchemaVersion,
            "doc_1",
            DocumentFormatKind.Docx,
            [new DocumentPartition("part-a", 0, [])])).Markdown
            .Replace("drmd_rules: 1.1", "drmd_rules: 9.9", StringComparison.Ordinal);

        var parsed = new DocRedockMarkdownParser().Parse(markdown);

        Assert.False(parsed.IsComplete);
        Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Code == "DRMD022");
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

        var projection = new DocRedockMarkdownSerializer().Serialize(graph);
        var parsed = new DocRedockMarkdownParser().Parse(projection.Markdown);

        Assert.Contains("rich-text=inline-v1", projection.Markdown);
        Assert.Contains("通常 **重要** と <u>_注記_</u><br>`code`", projection.Markdown);
        Assert.True(parsed.IsComplete);
    }

    [Fact]
    public void ProjectsXlsxCellsAsMetadataAddressedGfmTable()
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

        var projection = new DocRedockMarkdownSerializer().Serialize(graph);
        var parsed = new DocRedockMarkdownParser().Parse(projection.Markdown);

        Assert.Contains("<!--drmd:sheet-table range=A1:B2 source-columns=A,B source-rows=1,2 baseline_nodes=4", projection.Markdown);
        Assert.Contains("| 項目 | 金額 |", projection.Markdown);
        Assert.Contains("| 売上 | 120 |", projection.Markdown);
        Assert.DoesNotContain("| Row | A | B |", projection.Markdown);
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

        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;

        Assert.Contains("| `=SUM(B1:B2)` → 240 |", projection);
    }

    [Fact]
    public void SplitsDisconnectedHorizontalXlsxRegionsIntoIndependentTables()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_regions", DocumentFormatKind.Xlsx,
        [
            new DocumentPartition("sheet-Summary", 0,
            [
                Cell("a1", "A1", "左項目", 0), Cell("b1", "B1", "左値", 1),
                Cell("a2", "A2", "A", 2), Cell("b2", "B2", "10", 3),
                Cell("d1", "D1", "右項目", 4), Cell("e1", "E1", "右値", 5),
                Cell("d2", "D2", "B", 6), Cell("e2", "E2", "20", 7),
            ])
        ]);

        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;

        Assert.Equal(2, projection.Split("<!--drmd:sheet-table ", StringSplitOptions.None).Length - 1);
        Assert.Contains("range=A1:B2", projection, StringComparison.Ordinal);
        Assert.Contains("range=D1:E2", projection, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedHorizontalGapSplitsAHeadingRowWithOneCellOnTheLeft()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_recurring_gap", DocumentFormatKind.Xlsx,
        [
            new DocumentPartition("sheet-Monthly", 0,
            [
                Cell("a3", "A3", "月", 0), Cell("b3", "B3", "予算", 1), Cell("c3", "C3", "実績", 2), Cell("d3", "D3", "件数", 3), Cell("e3", "E3", "進捗", 4),
                Cell("h3", "H3", "カテゴリ別", 5),
                Cell("m3", "M3", "月", 6), Cell("n3", "N3", "予算", 7), Cell("o3", "O3", "実績", 8),
                Cell("a4", "A4", "4月", 9), Cell("b4", "B4", "100", 10), Cell("c4", "C4", "80", 11), Cell("d4", "D4", "2", 12), Cell("e4", "E4", "80%", 13),
                Cell("h4", "H4", "カテゴリ", 14), Cell("i4", "I4", "予算", 15), Cell("j4", "J4", "実績", 16), Cell("k4", "K4", "消化率", 17),
                Cell("m4", "M4", "4月", 18), Cell("n4", "N4", "100", 19), Cell("o4", "O4", "80", 20),
                Cell("a5", "A5", "5月", 21), Cell("b5", "B5", "120", 22), Cell("c5", "C5", "90", 23), Cell("d5", "D5", "3", 24), Cell("e5", "E5", "75%", 25),
                Cell("h5", "H5", "製品", 26), Cell("i5", "I5", "100", 27), Cell("j5", "J5", "80", 28), Cell("k5", "K5", "80%", 29),
                Cell("m5", "M5", "5月", 30), Cell("n5", "N5", "120", 31), Cell("o5", "O5", "90", 32),
            ])
        ]);

        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;

        Assert.Equal(3, projection.Split("<!--drmd:sheet-table ", StringSplitOptions.None).Length - 1);
        Assert.Contains("range=A3:E5", projection, StringComparison.Ordinal);
        Assert.Contains("range=H3:K5", projection, StringComparison.Ordinal);
        Assert.Contains("range=M3:O5", projection, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectsXlsxDisplayValueAlongsideRawCellValue()
    {
        var dateCell = Cell("a1", "A1", "45292", 0) with
        {
            Extensions = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
            {
                ["row"] = System.Text.Json.JsonSerializer.SerializeToElement(1),
                ["column"] = System.Text.Json.JsonSerializer.SerializeToElement(1),
                ["display_value"] = System.Text.Json.JsonSerializer.SerializeToElement("2024-01-01")
            }
        };
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_display", DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-Summary", 0, [dateCell])]);

        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;

        Assert.Contains("| `45292` → 2024-01-01 |", projection, StringComparison.Ordinal);
    }

    private static DocumentNode Cell(string id, string address, string text, int order) => new(
        id, NodeKind.Cell, null, order, ContentLayer.Body, new TextNodeContent(text),
        new SourceAnchor("xlsx", "/xl/worksheets/sheet1.xml", [new AnchorLocator("cell_address", address)]));

    private sealed record FakeGraph(string DocumentId, string Format, IReadOnlyList<FakePartition> Partitions);
    private sealed record FakePartition(string Id, IReadOnlyList<FakeNode> Nodes);
    private sealed record FakeNode(string Id, string Kind, string Text, int Order, string Layer = "Body");
}
