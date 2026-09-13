using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Pptx;
using DocRedock.Markdown;
using DocRedock.VisualInference;

namespace DocRedock.Tests.Pptx;

public sealed class PptxAdapterTests
{
    [Fact]
    public void ExtractsShapeTableImageAndNotes()
    {
        var result = new PptxAdapter().Extract(new MemoryStream(CreatePackage()));
        var slide = Assert.Single(result.Slides);
        Assert.Contains(slide.Shapes, shape => shape.ShapeId == "2" && shape.Text == "Hello");
        Assert.Equal("title", slide.Shapes.Single(shape => shape.ShapeId == "2").Role);
        Assert.Contains(slide.Shapes, shape => shape.IsTable);
        Assert.Contains(slide.Shapes, shape => shape.ImageRelationshipIds.Contains("rIdImage"));
        var image = Assert.Single(result.Graph.Nodes, node => node.Kind == DocRedock.Core.Documents.NodeKind.Image);
        Assert.Equal("ppt/media/image1.png", Assert.IsType<DocRedock.Core.Documents.ReferenceNodeContent>(image.Content).Reference);
        Assert.Equal("Speaker note", slide.NotesText);
    }

    [Fact]
    public void UnchangedRestoreIsByteIdenticalAndTextPatchLeavesUnknownPart()
    {
        var original = CreatePackage(); var adapter = new PptxAdapter();
        var empty = adapter.CreatePatchPlan(Array.Empty<PptxShapeTextEdit>());
        Assert.Equal(original, adapter.Restore(new MemoryStream(original), empty).Bytes);
        var plan = adapter.CreatePatchPlan([new PptxShapeTextEdit("slide1", "2", "Changed")]);
        var restored = adapter.Restore(new MemoryStream(original), plan).Bytes;
        var before = Entries(original); var after = Entries(restored);
        Assert.NotEqual(Convert.ToBase64String(before["ppt/slides/slide1.xml"]), Convert.ToBase64String(after["ppt/slides/slide1.xml"]));
        Assert.Equal(before["custom/unknown.bin"], after["custom/unknown.bin"]);
        Assert.Equal(before["ppt/theme/theme1.xml"], after["ppt/theme/theme1.xml"]);
        Assert.Contains("ppt/slides/slide1.xml", plan.DirtyParts);
    }

    [Fact]
    public void TextPatchPreservesRunFontsAndShapeLayout()
    {
        var original = CreatePackage();
        var adapter = new PptxAdapter();
        var restored = adapter.Restore(new MemoryStream(original),
            adapter.CreatePatchPlan([new PptxShapeTextEdit("slide1", "2", "変更後の表題")])).Bytes;
        var xml = Encoding.UTF8.GetString(Entries(restored)["ppt/slides/slide1.xml"]);

        Assert.Contains("typeface=\"Yu Mincho\"", xml);
        Assert.Contains("typeface=\"游明朝\"", xml);
        Assert.Contains("typeface=\"BIZ UDPGothic\"", xml);
        Assert.Contains("sz=\"2800\"", xml);
        Assert.Contains("<a:off x=\"640000\" y=\"320000\"", xml);
        Assert.Contains("<a:ext cx=\"10800000\" cy=\"1000000\"", xml);
        Assert.Equal(Entries(original)["ppt/theme/theme1.xml"], Entries(restored)["ppt/theme/theme1.xml"]);
    }

    [Fact]
    public void ExtractsPlaceholderRolesAndRestoresMultipleBodyParagraphs()
    {
        var original = CreatePackage();
        var adapter = new PptxAdapter();
        var slide = Assert.Single(adapter.Extract(new MemoryStream(original)).Slides);
        var body = Assert.Single(slide.Shapes, shape => shape.ShapeId == "5");

        Assert.Equal("body", body.Role);
        Assert.Equal("One\nTwo\nThree", body.Text);
        Assert.Equal(["One", "Two", "Three"], body.Paragraphs);

        var plan = adapter.CreatePatchPlan([new PptxShapeTextEdit("slide1", "5", "Alpha\nBeta\nGamma\nDelta")]);
        var restored = adapter.Restore(new MemoryStream(original), plan).Bytes;
        var restoredBody = Assert.Single(Assert.Single(adapter.Extract(new MemoryStream(restored)).Slides).Shapes, shape => shape.ShapeId == "5");
        var xml = Encoding.UTF8.GetString(Entries(restored)["ppt/slides/slide1.xml"]);
        Assert.Equal(["Alpha", "Beta", "Gamma", "Delta"], restoredBody.Paragraphs);
        Assert.Contains("<a:t>Alpha</a:t>", xml);
        Assert.Contains("<a:t>Delta</a:t>", xml);
    }

    [Fact]
    public void TitleAndBodyCompletePptxToMarkdownEditToPptxRoundTrip()
    {
        var original = CreatePackage();
        var adapter = new PptxAdapter();
        var extraction = adapter.Extract(new MemoryStream(original));
        var markdown = new DocRedockMarkdownSerializer().Serialize(extraction.Graph).Markdown;

        Assert.Contains("role=title", markdown);
        Assert.Contains("### Hello", markdown);
        Assert.Contains("role=body", markdown);
        Assert.Contains("- One\n- Two\n- Three", markdown);
        var edit = new MarkdownGraphEditor().Apply(extraction.Graph, markdown
            .Replace("### Hello", "### 実行計画", StringComparison.Ordinal)
            .Replace("- Two", "- 第二項", StringComparison.Ordinal));
        var plan = adapter.CreatePatchPlan(extraction.Graph, edit.EditedGraph);
        var restored = adapter.Restore(new MemoryStream(original), plan).Bytes;
        var reexport = adapter.Extract(new MemoryStream(restored));
        var slide = Assert.Single(reexport.Slides);

        Assert.True(edit.IsValid, string.Join(" | ", edit.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        Assert.Equal("実行計画", slide.Shapes.Single(shape => shape.Role == "title").Text);
        Assert.Equal(["One", "第二項", "Three"], slide.Shapes.Single(shape => shape.Role == "body").Paragraphs);
        Assert.Equal(Entries(original)["custom/unknown.bin"], Entries(restored)["custom/unknown.bin"]);
    }

    [Fact]
    public void Extracts_bullet_level_and_run_emphasis_metadata()
    {
        var slide = Assert.Single(new PptxAdapter().Extract(new MemoryStream(CreatePackage(includeRichShape: true))).Slides);
        var shape = Assert.Single(slide.Shapes, item => item.ShapeId == "7");

        var paragraph = Assert.Single(shape.ParagraphDetails!);
        Assert.True(paragraph.IsBullet);
        Assert.Equal(1, paragraph.Level);
        Assert.True(Assert.Single(paragraph.Runs!).Bold);
        var node = Assert.Single(new PptxAdapter().Extract(new MemoryStream(CreatePackage(includeRichShape: true))).Graph.Nodes,
            item => item.Source?.Locators.Any(locator => locator.Value == "7") == true);
        Assert.IsType<DocRedock.Core.Documents.RichTextNodeContent>(node.Content);
    }

    [Fact]
    public void ExtractsAndProtectsConnectorChartAndGroupedTextWhilePreservingComplexParts()
    {
        var original = CreatePackage(includeComplexObjects: true);
        var adapter = new PptxAdapter();
        var extraction = adapter.Extract(new MemoryStream(original));
        var slide = Assert.Single(extraction.Slides);

        var connector = Assert.Single(slide.Shapes, shape => shape.ShapeId == "8");
        var chart = Assert.Single(slide.Shapes, shape => shape.ShapeId == "10");
        var groupedText = Assert.Single(slide.Shapes, shape => shape.ShapeId == "9");
        var footer = Assert.Single(slide.Shapes, shape => shape.ShapeId == "11");
        Assert.Equal("connector", connector.ShapeType);
        Assert.Equal(["rIdChart"], chart.ChartRelationshipIds);
        Assert.Equal("Grouped evidence", groupedText.Text);
        Assert.Equal(10800000, groupedText.Geometry!.Width);
        Assert.Equal("footer", footer.Role);
        Assert.Contains(extraction.Graph.Nodes, node => node.Kind == DocRedock.Core.Documents.NodeKind.Connector && node.Editability == DocRedock.Core.Documents.NodeEditability.Protected && node.Layer == DocRedock.Core.Documents.ContentLayer.Body);
        Assert.Contains(extraction.Graph.Nodes, node => node.Kind == DocRedock.Core.Documents.NodeKind.Chart && node.Editability == DocRedock.Core.Documents.NodeEditability.Protected);
        Assert.Contains(extraction.Graph.Nodes, node => node.Kind == DocRedock.Core.Documents.NodeKind.Table && node.Editability == DocRedock.Core.Documents.NodeEditability.Protected);
        Assert.Contains(extraction.Graph.Nodes, node => node.Source?.Locators.Any(locator => locator.Value == "11") == true && node.Layer == DocRedock.Core.Documents.ContentLayer.Furniture);

        var restored = adapter.Restore(new MemoryStream(original),
            adapter.CreatePatchPlan([new PptxShapeTextEdit("slide1", "2", "Updated title")])).Bytes;
        var before = Entries(original); var after = Entries(restored);
        Assert.Equal(before["ppt/slides/charts/chart1.xml"], after["ppt/slides/charts/chart1.xml"]);
        Assert.Equal(before["ppt/notesSlides/notesSlide1.xml"], after["ppt/notesSlides/notesSlide1.xml"]);
        Assert.Equal(before["ppt/slideMasters/slideMaster1.xml"], after["ppt/slideMasters/slideMaster1.xml"]);
        Assert.Equal(before["ppt/slideLayouts/slideLayout1.xml"], after["ppt/slideLayouts/slideLayout1.xml"]);
        Assert.Equal(before["ppt/media/image1.png"], after["ppt/media/image1.png"]);
        Assert.Contains("Updated title", Assert.Single(adapter.Extract(new MemoryStream(restored)).Slides).Shapes.Single(shape => shape.ShapeId == "2").Text);
    }

    [Fact]
    public void ExtractsChartSeriesDiagramTextAndConnectorTransitionForReadableRendering()
    {
        var original = CreateGapFeaturesPackage();
        var adapter = new PptxAdapter();
        var extraction = adapter.Extract(new MemoryStream(original));
        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);

        // P06: a native chart's c:title + c:ser category/value pairs survive as a bold title and a GFM table.
        Assert.Contains("**Adoption by quarter**（棒グラフ）", markdown, StringComparison.Ordinal);
        Assert.Contains("要約: 1 系列のグラフです。", markdown, StringComparison.Ordinal);
        Assert.Contains("Q1 の 12 から Q2 の 30 へ 増加", markdown, StringComparison.Ordinal);
        Assert.Contains("| Q1 | 12 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Q2 | 30 |", markdown, StringComparison.Ordinal);

        // P07: SmartArt dgm:t text is extracted even though it never appears as ordinary shape text
        // (the "doc" dgm:pt has no dgm:t at all and correctly contributes nothing).
        Assert.Contains("- Intake", markdown, StringComparison.Ordinal);
        Assert.Contains("- Review", markdown, StringComparison.Ordinal);

        // P08 / v0.1.6: the connector's stCxn/endCxn resolve through the shape-id map into a
        // semantic Mermaid edge, rather than leaving the flow as a textual edge enumeration.
        Assert.Contains("v_20 --> v_21", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectsNativeConnectorAsSemanticMermaidWithStableVisualIds()
    {
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateGapFeaturesPackage()));
        var visual = Assert.Single(extraction.Graph.Nodes, node => node.Extensions?.ContainsKey("visual_graph") == true);
        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);

        Assert.Equal(DocRedock.Core.Documents.NodeKind.Diagram, visual.Kind);
        Assert.Contains("```mermaid", markdown, StringComparison.Ordinal);
        Assert.Contains("flowchart LR", markdown, StringComparison.Ordinal);
        Assert.Contains("v_20 --> v_21", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("- ALPHA → BETA", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(extraction.Warnings, warning => warning.StartsWith("VisualConnectorUnresolved", StringComparison.Ordinal));
    }

    [Fact]
    public void R0_FIX08_visual_node_body_text_is_emitted_only_inside_mermaid()
    {
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateGeometryConnectorPackage()));
        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);

        Assert.Contains("START", markdown, StringComparison.Ordinal);
        Assert.Contains("END", markdown, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(markdown, "START").Cast<Match>());
        Assert.Single(Regex.Matches(markdown, "END").Cast<Match>());
    }

    [Fact]
    public void R3_PPTX_visual_graph_marks_consumed_members_by_stable_shape_id()
    {
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateGeometryConnectorPackage()));
        var start = Assert.Single(extraction.Graph.Nodes, node => node.Source?.Locators.Any(locator => locator.Value == "100") == true);
        var end = Assert.Single(extraction.Graph.Nodes, node => node.Source?.Locators.Any(locator => locator.Value == "101") == true);
        Assert.True(start.Extensions!["visual_graph_node"].GetBoolean());
        Assert.True(end.Extensions!["visual_graph_node"].GetBoolean());
        var diagram = Assert.Single(extraction.Graph.Nodes, node => node.Kind == NodeKind.Diagram && node.Extensions?.ContainsKey("visual_graph") == true);
        var members = diagram.Extensions!["visual_graph_member_shape_ids"].Deserialize<string[]>()!;
        Assert.Contains("100", members);
        Assert.Contains("101", members);
        Assert.Contains("104", members);
    }

    [Fact]
    public void R0_FIX08_no_diagrams_lists_connector_relation_exactly_once()
    {
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateGeometryConnectorPackage()));
        var markdown = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(IncludeDiagrams: false)).Serialize(extraction.Graph);

        Assert.DoesNotContain("```mermaid", markdown, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(markdown, "START — END").Cast<Match>());
        // The combined "START — END" match alone cannot detect a duplicate standalone "START"
        // (or "END") elsewhere in the body text; count each endpoint token independently too.
        Assert.Single(Regex.Matches(markdown, "START").Cast<Match>());
        Assert.Single(Regex.Matches(markdown, "END").Cast<Match>());
    }

    [Fact]
    public void MermaidOmitsUnconnectedTitleShape()
    {
        var entries = Entries(CreateGapFeaturesPackage());
        const string title = "<p:sp><p:nvSpPr><p:cNvPr id=\"19\" name=\"Title\" /><p:nvPr><p:ph type=\"title\" /></p:nvPr></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"200\" /><a:ext cx=\"400\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>Unconnected title</a:t></a:r></a:p></p:txBody></p:sp>";
        entries["ppt/slides/slide1.xml"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(entries["ppt/slides/slide1.xml"])
            .Replace("<p:spTree>", "<p:spTree>" + title, StringComparison.Ordinal));

        var extraction = new PptxAdapter().Extract(new MemoryStream(Repack(entries)));
        var visual = VisualGraphOf(extraction);
        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);

        Assert.DoesNotContain(visual.Nodes, node => node.SourceNodeId == "19");
        Assert.DoesNotContain("v_19[Unconnected title]", markdown, StringComparison.Ordinal);
        Assert.Contains("## スライド 1 — Unconnected title", markdown, StringComparison.Ordinal);
        Assert.Contains("v_20 --> v_21", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void DisablingDiagramProjectionKeepsConnectorTextFallback()
    {
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateGapFeaturesPackage()));
        var markdown = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(IncludeDiagrams: false)).Serialize(extraction.Graph);

        Assert.DoesNotContain("```mermaid", markdown, StringComparison.Ordinal);
        Assert.Contains("- ALPHA → BETA", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptVisualGraphKeepsConnectorFallbackAndReportsPartialProjection()
    {
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateGapFeaturesPackage()));
        var serializer = new ReadableMarkdownSerializer();
        var markdown = serializer.Serialize(CorruptVisualGraph(extraction.Graph));

        Assert.DoesNotContain("```mermaid", markdown, StringComparison.Ordinal);
        Assert.Contains("- ALPHA → BETA", markdown, StringComparison.Ordinal);
        Assert.Contains(serializer.Diagnostics, diagnostic => diagnostic.Code == "VisualSemanticProjectionPartial" &&
            diagnostic.Message.Contains("fallback was not suppressed", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsnappedConnectorUsesUniqueGeometryAndAttachesNearbyEdgeLabel()
    {
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateGeometryConnectorPackage(includeLabel: true)));
        var visual = VisualGraphOf(extraction);
        var edge = Assert.Single(visual.Edges);
        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);

        Assert.Equal(VisualEdgeResolution.GeometryInferred, edge.Resolution);
        Assert.Equal("v_100", edge.SourceId);
        Assert.Equal("v_101", edge.TargetId);
        Assert.Equal("YES", edge.Label);
        Assert.Equal("High", edge.Evidence?.ConfidenceBand);
        Assert.Equal(VisualGraphQuality.HighConfidenceInferred, visual.Quality);
        var validation = VisualGraphValidator.Validate(visual);
        Assert.True(validation.IsValidForSemanticProjection,
            string.Join("; ", validation.Errors.Select(error => error.Code + ": " + error.Message)));
        // Undirected by design: CreateGeometryConnectorPackage's connector carries no
        // <a:tailEnd>/<a:headEnd> marker, so ConnectorDirection/DirectionOf finds no arrowhead
        // evidence at all and resolves Direction=Unknown. With no directional evidence to report,
        // the edge correctly renders with the undirected "---" connector rather than a guessed
        // "-->"; this assertion is intentional, not a fallback or a regression.
        Assert.Contains("v_100 ---|YES| v_101", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(extraction.Warnings, warning => warning.StartsWith("VisualConnectorUnresolved", StringComparison.Ordinal));
    }

    [Fact]
    public void DiagonalUnsnappedConnectorUsesTransformedEndpointsAndSegmentBasedLabelScore()
    {
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateDiagonalGeometryConnectorPackage()));
        var visual = VisualGraphOf(extraction);
        var edge = Assert.Single(visual.Edges);
        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);

        Assert.Equal(VisualEdgeResolution.GeometryInferred, edge.Resolution);
        Assert.Equal("v_100", edge.SourceId);
        Assert.Equal("v_101", edge.TargetId);
        Assert.Equal("YES", edge.Label);
        Assert.Contains("v_100 -->|YES| v_101", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(extraction.Warnings,
            warning => warning.StartsWith("VisualConnectorUnresolved", StringComparison.Ordinal));
        Assert.DoesNotContain(extraction.Warnings,
            warning => warning.StartsWith("VisualEdgeLabelUnresolved", StringComparison.Ordinal));
    }

    [Fact]
    public void Visual_inference_timeout_returns_complete_fallback_without_partial_topology()
    {
        var extraction = new PptxAdapter { VisualInferenceTimeout = TimeSpan.Zero }
            .Extract(new MemoryStream(CreateGeometryConnectorPackage(includeLabel: true)));
        var visual = VisualGraphOf(extraction);

        Assert.DoesNotContain(visual.Edges, edge => edge.SourceId is not null || edge.TargetId is not null);
        var diagnostic = Assert.Single(visual.Diagnostics!, item => item.Code == "VisualInferenceTimeout");
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Fallback));
        Assert.True(visual.Accounting.IsConsistent);
    }

    [Fact]
    public void Timeout_skips_unresolved_shaft_node_retention_atomically()
    {
        var extraction = new PptxAdapter { VisualInferenceTimeout = TimeSpan.Zero }
            .Extract(new MemoryStream(CreateIntermediateNodeGeometryConnectorPackage()));
        var visual = VisualGraphOf(extraction);

        Assert.DoesNotContain(visual.Nodes, node => node.Label == "MIDDLE");
        Assert.DoesNotContain(visual.Edges, edge => edge.SourceId is not null || edge.TargetId is not null);
        Assert.Single(visual.Diagnostics!, item => item.Code == "VisualInferenceTimeout");
        Assert.DoesNotContain(visual.Diagnostics!, item => item.Code == "VisualInferenceBudgetExceeded");
        Assert.True(visual.Accounting.IsConsistent);
    }

    [Fact]
    public void R4_two_independent_unsnapped_arrow_diagrams_are_clustered_without_cross_edges()
    {
        var package = CreateTwoIndependentGeometryConnectorPackage();
        var adapter = new PptxAdapter();
        var first = adapter.Extract(new MemoryStream(package));
        var second = adapter.Extract(new MemoryStream(package));
        var graphs = VisualGraphsOf(first);

        Assert.True(graphs.Length == 2, JsonSerializer.Serialize(graphs));
        Assert.Equal(graphs.Select(graph => graph.Id).OrderBy(id => id), VisualGraphsOf(second).Select(graph => graph.Id).OrderBy(id => id));
        var edges = graphs.SelectMany(graph => graph.Edges).ToArray();
        Assert.Equal(2, edges.Length);
        Assert.Contains(edges, edge => Label(graphs, edge.SourceId) == "PPT_R4_A" && Label(graphs, edge.TargetId) == "PPT_R4_B");
        Assert.Contains(edges, edge => Label(graphs, edge.SourceId) == "PPT_R4_C" && Label(graphs, edge.TargetId) == "PPT_R4_D");
        Assert.DoesNotContain(edges, edge => Label(graphs, edge.SourceId) is not ("PPT_R4_A" or "PPT_R4_C") || Label(graphs, edge.TargetId) is not ("PPT_R4_B" or "PPT_R4_D"));
        Assert.All(edges, edge => Assert.False(string.IsNullOrWhiteSpace(edge.Evidence?.ClusterId)));
        Assert.Equal(2, edges.Select(edge => edge.Evidence!.ClusterId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(JsonSerializer.Serialize(graphs), JsonSerializer.Serialize(VisualGraphsOf(second)));
    }

    [Fact]
    public void Native_only_mode_keeps_unsnapped_connector_as_fallback()
    {
        using var inferenceScope = VisualInferenceContext.Push(VisualInferenceMode.NativeOnly);

        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateGeometryConnectorPackage()));
        var edge = Assert.Single(VisualGraphOf(extraction).Edges);

        Assert.Equal(VisualEdgeResolution.Unresolved, edge.Resolution);
        Assert.Null(edge.SourceId);
        Assert.Null(edge.TargetId);
        Assert.Contains(extraction.Warnings, warning => warning.StartsWith("VisualConnectorUnresolved", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsnappedConnectorWithGenuineMedianSizedIntermediateNodeStaysUnresolved()
    {
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateIntermediateNodeGeometryConnectorPackage()));
        var visual = VisualGraphOf(extraction);
        var edge = Assert.Single(visual.Edges);
        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);

        // MIDDLE is the same 100x100 size as START/END -- squarely at the slide's own median --
        // so it must stay a real node candidate despite sitting on the connector's shaft, and
        // FindIntermediateNodeIds must keep rejecting the START-END pairing it blocks. No
        // ClassifyEdgeLabelCandidateShapeIds bound reclassifies it: unlike CreateGeometryConnector
        // Package(includeLabel: true)'s 40x20 "YES" (well under half that median), 100 is not
        // under half of 100.
        Assert.Equal(VisualEdgeResolution.Unresolved, edge.Resolution);
        Assert.Null(edge.SourceId);
        Assert.Null(edge.TargetId);
        Assert.Contains(extraction.Warnings, warning => warning.StartsWith("VisualConnectorUnresolved", StringComparison.Ordinal));
        // Negative case: no START->END (nor END->START) relation of any kind was projected.
        Assert.DoesNotContain(visual.Edges, resolvedEdge => resolvedEdge.SourceId is not null && resolvedEdge.TargetId is not null);
        Assert.Equal(new[] { "END", "MIDDLE", "START" },
            visual.Nodes.Select(node => node.Label).OrderBy(label => label, StringComparer.Ordinal));
        Assert.Contains("```mermaid\nflowchart", markdown, StringComparison.Ordinal);
        Assert.Contains("[START]", markdown, StringComparison.Ordinal);
        Assert.Contains("[MIDDLE]", markdown, StringComparison.Ordinal);
        Assert.Contains("[END]", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(" --> ", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void MermaidEscapesQuotedEdgeLabels()
    {
        var entries = Entries(CreateGeometryConnectorPackage(includeLabel: true));
        var xml = Encoding.UTF8.GetString(entries["ppt/slides/slide1.xml"])
            .Replace("<a:t>YES</a:t>", "<a:t>He said \"YES\"</a:t>", StringComparison.Ordinal);
        entries["ppt/slides/slide1.xml"] = Encoding.UTF8.GetBytes(xml);
        var markdown = new ReadableMarkdownSerializer().Serialize(new PptxAdapter().Extract(new MemoryStream(Repack(entries))).Graph);

        // Undirected by design: see the rationale in UnsnappedConnectorUsesUniqueGeometryAndAttachesNearbyEdgeLabel.
        // This fixture's connector has no arrowhead evidence either, so "---" (not "-->") is correct here too.
        Assert.Contains("v_100 ---|He said &quot;YES&quot;| v_101", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("#quot;", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void R3_balanced_medium_confidence_edge_is_promoted_with_contract_note_and_partial_warning()
    {
        // See CreateMediumConfidenceUnsnappedPackage for how the MEND candidate's geometry was
        // tuned. Measured via SoftConnectionEngine at VisualInferenceMode.Balanced:
        // Score=0.7244, CandidateMargin=0.4489, ConfidenceBand=Medium (not High: margin is well
        // past HighMargin(.15) but the pair's own Score stays under HighThreshold(.85), and the
        // candidate has no ray hit so it cannot qualify via strongRay either).
        using var inferenceScope = VisualInferenceContext.Push(VisualInferenceMode.Balanced);

        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateMediumConfidenceUnsnappedPackage()));
        var visual = VisualGraphOf(extraction);
        var edge = Assert.Single(visual.Edges);
        var serializer = new ReadableMarkdownSerializer();
        var markdown = serializer.Serialize(extraction.Graph);

        Assert.Equal(VisualEdgeResolution.GeometryInferred, edge.Resolution);
        Assert.Equal("v_100", edge.SourceId);
        Assert.Equal("v_101", edge.TargetId);
        Assert.Equal("Medium", edge.Evidence?.ConfidenceBand);
        Assert.Contains("v_100 --- v_101", markdown, StringComparison.Ordinal);
        Assert.Contains("一部の接続は図形配置から推定されています。診断を確認してください。", markdown, StringComparison.Ordinal);
        Assert.Contains(serializer.Diagnostics, diagnostic => diagnostic.Code == "VisualSemanticProjectionPartial" &&
            diagnostic.Severity == MarkdownDiagnosticSeverity.Warning);
    }

    [Fact]
    public void R3_safe_mode_does_not_promote_the_same_medium_confidence_candidate()
    {
        // Same fixture as the Balanced-mode test above, exercised under the default (Safe) mode.
        // Safe's narrower endpoint radius (45 vs Balanced's 80, for these 100x100 shapes) scores
        // the same MEND candidate low enough that the pair's average score drops under
        // MediumThreshold(.70); ConnectionConfidence.Low then routes the connector to Unresolved,
        // so it is never promoted at any confidence band (Medium included).
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateMediumConfidenceUnsnappedPackage()));
        var visual = VisualGraphOf(extraction);
        var edge = Assert.Single(visual.Edges);

        Assert.Equal(VisualEdgeResolution.Unresolved, edge.Resolution);
        Assert.Null(edge.SourceId);
        Assert.Null(edge.TargetId);
        Assert.Contains(extraction.Warnings, warning => warning.StartsWith("VisualConnectorUnresolved", StringComparison.Ordinal));
    }

    [Fact]
    public void Detached_triangle_arrowhead_is_associated_one_to_one_with_unsnapped_shaft()
    {
        var entries = Entries(CreateGeometryConnectorPackage());
        var xml = Encoding.UTF8.GetString(entries["ppt/slides/slide1.xml"]);
        const string arrowhead =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"105\" name=\"Detached arrowhead\" /></p:nvSpPr>" +
            "<p:spPr><a:xfrm><a:off x=\"305\" y=\"40\" /><a:ext cx=\"20\" cy=\"20\" /></a:xfrm>" +
            "<a:prstGeom prst=\"triangle\"><a:avLst /></a:prstGeom></p:spPr></p:sp>";
        entries["ppt/slides/slide1.xml"] = Encoding.UTF8.GetBytes(
            xml.Replace("</p:spTree>", arrowhead + "</p:spTree>", StringComparison.Ordinal));

        var extraction = new PptxAdapter().Extract(new MemoryStream(Repack(entries)));
        var visual = VisualGraphOf(extraction);
        var edge = Assert.Single(visual.Edges);
        var diagram = Assert.Single(extraction.Graph.Nodes,
            node => node.Kind == NodeKind.Diagram && node.Extensions?.ContainsKey("visual_graph") == true);
        var members = diagram.Extensions!["visual_graph_member_shape_ids"].Deserialize<string[]>()!;

        Assert.False(edge.IsUndirected);
        Assert.Equal("v_100", edge.SourceId);
        Assert.Equal("v_101", edge.TargetId);
        Assert.Equal("end", edge.Evidence?.ArrowheadEvidence);
        Assert.Contains("DetachedArrowhead", edge.Evidence?.EvidenceCodes ?? []);
        Assert.Contains(visual.SourceItems ?? [], item => item.Id == "arrowhead:105" &&
            item.Disposition == VisualDisposition.SuppressedDuplicate &&
            item.DuplicateOfSourceItemId == "connector:0");
        Assert.Contains("105", members);
        Assert.True(visual.Accounting.IsConsistent);
    }

    [Fact]
    public void Hidden_native_endpoint_is_not_promoted_to_visual_topology()
    {
        var entries = Entries(CreateGapFeaturesPackage());
        var xml = Encoding.UTF8.GetString(entries["ppt/slides/slide1.xml"])
            .Replace("id=\"20\"", "id=\"20\" hidden=\"1\"", StringComparison.Ordinal);
        entries["ppt/slides/slide1.xml"] = Encoding.UTF8.GetBytes(xml);

        var extraction = new PptxAdapter().Extract(new MemoryStream(Repack(entries)));
        var visual = VisualGraphOf(extraction);

        Assert.DoesNotContain(visual.Nodes, node => node.SourceNodeId == "20");
        Assert.Contains(visual.Edges, edge => edge.SourceId is null && edge.TargetId is null);
        Assert.Contains(extraction.Warnings, warning => warning.StartsWith("VisualConnectorUnresolved", StringComparison.Ordinal));
    }

    [Fact]
    public void AmbiguousUnsnappedConnectorIsDiagnosedAndNotInvented()
    {
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateGeometryConnectorPackage(ambiguousStart: true)));
        var visual = VisualGraphOf(extraction);

        Assert.Contains(visual.Edges, edge => edge.Resolution == VisualEdgeResolution.Unresolved && edge.SourceId is null && edge.TargetId is null);
        Assert.Contains(extraction.Warnings, warning => warning.StartsWith("VisualConnectorUnresolved", StringComparison.Ordinal));
        var diagnostic = Assert.Single(visual.Diagnostics!, item => item.Code == "VisualConnectorUnresolved");
        Assert.Equal("pptx", diagnostic.Format);
        Assert.Equal("ppt/slides/slide1.xml", diagnostic.PartUri);
        Assert.Equal("slide1", diagnostic.PartitionId);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.SourceObjectId));
        Assert.Equal("connector", diagnostic.SourceObjectType);
        Assert.Equal(0d, diagnostic.Confidence);
        Assert.NotNull(diagnostic.LocationSummary);
        Assert.Contains("format=pptx", diagnostic.LocationSummary!, StringComparison.Ordinal);
        Assert.Contains("part=ppt/slides/slide1.xml", diagnostic.LocationSummary!, StringComparison.Ordinal);
        Assert.Contains("source_type=connector", diagnostic.LocationSummary!, StringComparison.Ordinal);
    }

    [Fact]
    public void Self_edge_connector_remains_unresolved_without_null_reference()
    {
        var entries = Entries(CreatePackage());
        var xml = Encoding.UTF8.GetString(entries["ppt/slides/slide1.xml"]);
        const string selfEdge =
            "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"99\" name=\"Self edge\" /><a:stCxn id=\"2\" /><a:endCxn id=\"2\" /></p:nvCxnSpPr><p:spPr><a:xfrm><a:off x=\"640000\" y=\"320000\" /><a:ext cx=\"100000\" cy=\"0\" /></a:xfrm></p:spPr></p:cxnSp>";
        entries["ppt/slides/slide1.xml"] = Encoding.UTF8.GetBytes(xml.Replace("</p:spTree>", selfEdge + "</p:spTree>", StringComparison.Ordinal));

        var visual = VisualGraphOf(new PptxAdapter().Extract(new MemoryStream(Repack(entries))));
        var edge = Assert.Single(visual.Edges);

        Assert.Equal(VisualEdgeResolution.Unresolved, edge.Resolution);
        Assert.Null(edge.SourceId);
        Assert.Null(edge.TargetId);
        Assert.Contains(visual.Diagnostics!, diagnostic => diagnostic.Code == "VisualConnectorUnresolved");
        Assert.False(visual.HasTopology);
    }

    [Fact]
    public void Reserves_all_connector_endpoints_before_assigning_edge_labels()
    {
        var entries = Entries(CreatePackage());
        var xml = Encoding.UTF8.GetString(entries["ppt/slides/slide1.xml"]);
        const string topology =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"100\" name=\"First start\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>FIRST START</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"101\" name=\"First end\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"1000\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>FIRST END</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"102\" name=\"Second start\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"550\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>SECOND START</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"103\" name=\"Second end\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"1600\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>SECOND END</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"200\" name=\"First connector\" /><a:stCxn id=\"100\" /><a:endCxn id=\"101\" /></p:nvCxnSpPr><p:spPr><a:xfrm><a:off x=\"100\" y=\"50\" /><a:ext cx=\"1000\" cy=\"0\" /></a:xfrm></p:spPr></p:cxnSp>" +
            "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"201\" name=\"Second connector\" /><a:stCxn id=\"102\" /><a:endCxn id=\"103\" /></p:nvCxnSpPr><p:spPr><a:xfrm><a:off x=\"650\" y=\"50\" /><a:ext cx=\"1000\" cy=\"0\" /></a:xfrm></p:spPr></p:cxnSp>";
        entries["ppt/slides/slide1.xml"] = Encoding.UTF8.GetBytes(xml.Replace("</p:spTree>", topology + "</p:spTree>", StringComparison.Ordinal));

        var visual = VisualGraphOf(new PptxAdapter().Extract(new MemoryStream(Repack(entries))));

        Assert.Equal(2, visual.Edges.Count);
        Assert.All(visual.Edges, edge =>
        {
            Assert.NotNull(edge.SourceId);
            Assert.NotNull(edge.TargetId);
        });
        Assert.Contains(visual.Nodes, node => node.SourceNodeId == "102");
        Assert.DoesNotContain(visual.Edges, edge => string.Equals(edge.Label, "SECOND START", StringComparison.Ordinal));
    }

    [Fact]
    public void TextlessUnresolvedConnectorIsRetainedByStableDiagnostic()
    {
        var entries = Entries(CreatePackage());
        var xml = Encoding.UTF8.GetString(entries["ppt/slides/slide1.xml"]);
        const string connector = "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"90\" name=\"Textless arrow\" /></p:nvCxnSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"100\" cy=\"0\" /></a:xfrm></p:spPr></p:cxnSp>";
        entries["ppt/slides/slide1.xml"] = Encoding.UTF8.GetBytes(xml.Replace("</p:spTree>", connector + "</p:spTree>", StringComparison.Ordinal));
        var extraction = new PptxAdapter().Extract(new MemoryStream(Repack(entries)));
        var serializer = new ReadableMarkdownSerializer();
        _ = serializer.Serialize(extraction.Graph);

        Assert.Contains(extraction.Warnings, warning => warning.StartsWith("VisualConnectorUnresolved", StringComparison.Ordinal));
        Assert.Contains(serializer.Diagnostics, diagnostic => diagnostic.Code == "VisualConnectorUnresolved");
    }

    [Fact]
    public void ReadableTableExpandsVerticalMergeAcrossPptxRows()
    {
        var original = CreateGapFeaturesPackage();
        var adapter = new PptxAdapter();
        var extraction = adapter.Extract(new MemoryStream(original));
        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);

        // P09: the merged "Group" label (a:tc rowSpan="2" + vMerge="1" continuation) repeats on
        // every row the span covers instead of only the first, once PptxAdapter carries
        // TableCell.RowSpan through to the shared ExpandTableGrid carry-down logic.
        Assert.Contains("| Group | X |", markdown, StringComparison.Ordinal);
        Assert.Contains("|  | Y |", markdown, StringComparison.Ordinal);
    }

    private static byte[] CreateGapFeaturesPackage()
    {
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
            ["ppt/presentation.xml"] = "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\" /></p:sldIdLst></p:presentation>",
            ["ppt/_rels/presentation.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"slide\" Target=\"slides/slide1.xml\" /></Relationships>",
            ["ppt/slides/slide1.xml"] =
                "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:cSld><p:spTree>" +
                "<p:sp><p:nvSpPr><p:cNvPr id=\"20\" name=\"StateA\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>ALPHA</a:t></a:r></a:p></p:txBody></p:sp>" +
                "<p:sp><p:nvSpPr><p:cNvPr id=\"21\" name=\"StateB\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"200\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>BETA</a:t></a:r></a:p></p:txBody></p:sp>" +
                "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"22\" name=\"Transition\" /><p:cNvCxnSpPr><a:stCxn id=\"20\" idx=\"1\" /><a:endCxn id=\"21\" idx=\"3\" /></p:cNvCxnSpPr></p:nvCxnSpPr><p:spPr /></p:cxnSp>" +
                "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"23\" name=\"Chart\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"1\" cy=\"1\" /></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"><c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"rIdChart\" /></a:graphicData></a:graphic></p:graphicFrame>" +
                "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"24\" name=\"Diagram\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"1\" cy=\"1\" /></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"><dgm:relIds xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" r:dm=\"rIdDgm\" /></a:graphicData></a:graphic></p:graphicFrame>" +
                "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"25\" name=\"MergedTable\" /></p:nvGraphicFramePr><a:graphic><a:graphicData><a:tbl>" +
                "<a:tr><a:tc rowSpan=\"2\"><a:txBody><a:p><a:r><a:t>Group</a:t></a:r></a:p></a:txBody></a:tc><a:tc><a:txBody><a:p><a:r><a:t>X</a:t></a:r></a:p></a:txBody></a:tc></a:tr>" +
                "<a:tr><a:tc vMerge=\"1\"><a:txBody><a:p /></a:txBody></a:tc><a:tc><a:txBody><a:p><a:r><a:t>Y</a:t></a:r></a:p></a:txBody></a:tc></a:tr>" +
                "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>" +
                "</p:spTree></p:cSld></p:sld>",
            ["ppt/slides/_rels/slide1.xml.rels"] =
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rIdChart\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/chart\" Target=\"../charts/chart1.xml\" />" +
                "<Relationship Id=\"rIdDgm\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/diagramData\" Target=\"../diagrams/data1.xml\" />" +
                "</Relationships>",
            ["ppt/charts/chart1.xml"] =
                "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
                "<c:chart><c:title><c:tx><c:rich><a:p><a:r><a:t>Adoption by quarter</a:t></a:r></a:p></c:rich></c:tx></c:title>" +
                "<c:plotArea><c:barChart><c:ser><c:idx val=\"0\" /><c:tx><c:strRef><c:strCache><c:pt idx=\"0\"><c:v>Adopters</c:v></c:pt></c:strCache></c:strRef></c:tx>" +
                "<c:cat><c:strRef><c:strCache><c:pt idx=\"0\"><c:v>Q1</c:v></c:pt><c:pt idx=\"1\"><c:v>Q2</c:v></c:pt></c:strCache></c:strRef></c:cat>" +
                "<c:val><c:numRef><c:numCache><c:pt idx=\"0\"><c:v>12</c:v></c:pt><c:pt idx=\"1\"><c:v>30</c:v></c:pt></c:numCache></c:numRef></c:val></c:ser></c:barChart></c:plotArea></c:chart></c:chartSpace>",
            ["ppt/diagrams/data1.xml"] =
                "<dgm:dataModel xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
                "<dgm:ptLst>" +
                "<dgm:pt modelId=\"{A0000000-0000-0000-0000-000000000000}\" type=\"doc\" />" +
                "<dgm:pt modelId=\"{B0000000-0000-0000-0000-000000000000}\"><dgm:t><a:p><a:r><a:t>Intake</a:t></a:r></a:p></dgm:t></dgm:pt>" +
                "<dgm:pt modelId=\"{C0000000-0000-0000-0000-000000000000}\"><dgm:t><a:p><a:r><a:t>Review</a:t></a:r></a:p></dgm:t></dgm:pt>" +
                "</dgm:ptLst></dgm:dataModel>",
        };
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var part in parts)
            { using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), Encoding.UTF8); writer.Write(part.Value); }
        return output.ToArray();
    }

    private static byte[] CreateGeometryConnectorPackage(bool ambiguousStart = false, bool includeLabel = false)
    {
        var entries = Entries(CreatePackage());
        var xml = Encoding.UTF8.GetString(entries["ppt/slides/slide1.xml"]);
        var shapes =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"100\" name=\"Start\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>START</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"101\" name=\"End\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"300\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>END</a:t></a:r></a:p></p:txBody></p:sp>" +
            (ambiguousStart ? "<p:sp><p:nvSpPr><p:cNvPr id=\"102\" name=\"Competing start\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>OTHER</a:t></a:r></a:p></p:txBody></p:sp>" : string.Empty) +
            (includeLabel ? "<p:sp><p:nvSpPr><p:cNvPr id=\"103\" name=\"Decision label\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"190\" y=\"40\" /><a:ext cx=\"40\" cy=\"20\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>YES</a:t></a:r></a:p></p:txBody></p:sp>" : string.Empty) +
            "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"104\" name=\"Unsnapped connector\" /></p:nvCxnSpPr><p:spPr><a:xfrm><a:off x=\"100\" y=\"50\" /><a:ext cx=\"200\" cy=\"0\" /></a:xfrm></p:spPr></p:cxnSp>";
        entries["ppt/slides/slide1.xml"] = Encoding.UTF8.GetBytes(xml.Replace("</p:spTree>", shapes + "</p:spTree>", StringComparison.Ordinal));
        return Repack(entries);
    }

    private static byte[] CreateDiagonalGeometryConnectorPackage()
    {
        var entries = Entries(CreatePackage());
        var xml = Encoding.UTF8.GetString(entries["ppt/slides/slide1.xml"]);
        const string shapes =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"100\" name=\"Start\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>START</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"101\" name=\"End\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"300\" y=\"300\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>END</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"103\" name=\"Decision label\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"190\" y=\"190\" /><a:ext cx=\"40\" cy=\"20\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>YES</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"104\" name=\"Diagonal connector\" /></p:nvCxnSpPr><p:spPr><a:xfrm><a:off x=\"100\" y=\"100\" /><a:ext cx=\"200\" cy=\"200\" /></a:xfrm><a:ln><a:tailEnd type=\"triangle\" /></a:ln></p:spPr></p:cxnSp>";
        entries["ppt/slides/slide1.xml"] = Encoding.UTF8.GetBytes(
            xml.Replace("</p:spTree>", shapes + "</p:spTree>", StringComparison.Ordinal));
        return Repack(entries);
    }

    // Same START/END geometry as CreateGeometryConnectorPackage, plus a third shape (MIDDLE)
    // the identical 100x100 size sitting squarely on the connector's shaft between them. Unlike
    // CreateGeometryConnectorPackage(includeLabel: true)'s 40x20 "YES", MIDDLE sits exactly at
    // the slide's own median text-bearing-shape size, so PptxAdapter's edge-label
    // pre-classification must never treat it as a label candidate: it has to stay a real node
    // and keep blocking the connector, mirroring SoftConnectionEngineTests.
    // Merged_shaft_intermediate_node_keeps_the_connector_unresolved_instead_of_a_skip_edge at the
    // engine level, exercised here end to end through the PPTX adapter.
    private static byte[] CreateIntermediateNodeGeometryConnectorPackage()
    {
        var entries = Entries(CreatePackage());
        var xml = Encoding.UTF8.GetString(entries["ppt/slides/slide1.xml"]);
        var shapes =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"100\" name=\"Start\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>START</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"105\" name=\"Middle\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"225\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>MIDDLE</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"101\" name=\"End\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"450\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>END</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"104\" name=\"Unsnapped connector\" /></p:nvCxnSpPr><p:spPr><a:xfrm><a:off x=\"100\" y=\"50\" /><a:ext cx=\"350\" cy=\"0\" /></a:xfrm></p:spPr></p:cxnSp>";
        entries["ppt/slides/slide1.xml"] = Encoding.UTF8.GetBytes(xml.Replace("</p:spTree>", shapes + "</p:spTree>", StringComparison.Ordinal));
        return Repack(entries);
    }

    // Deliberately NOT built on CreatePackage(): that fixture's title placeholder carries
    // EMU-scale geometry (offsets in the millions) which balloons the slide's canvas diagonal
    // and, through AdaptiveScale, the endpoint search radius far beyond anything these small
    // hand-placed shapes need. This stays minimal and title-free (like CreateGapFeaturesPackage)
    // so SafeEndpointRadius/BalancedEndpointRadius land at their plain MinorAxis-derived values
    // (45 / 80, for these 100x100 shapes) instead of being clamped upward by canvas size.
    private static byte[] CreateMediumConfidenceUnsnappedPackage()
    {
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
            ["ppt/presentation.xml"] = "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\" /></p:sldIdLst></p:presentation>",
            ["ppt/_rels/presentation.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"slide\" Target=\"slides/slide1.xml\" /></Relationships>",
            ["ppt/slides/slide1.xml"] =
                "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:cSld><p:spTree>" +
                // MSTART touches the connector's start point (100,50) exactly: an unambiguous,
                // maximal-score (1.0) candidate on the start side in either inference mode.
                "<p:sp><p:nvSpPr><p:cNvPr id=\"100\" name=\"Start\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>MSTART</a:t></a:r></a:p></p:txBody></p:sp>" +
                // MEND's nearest corner sits (dx=29, dy=6) from the connector's end point (900,50):
                // boundary distance ~29.6, and its box top edge (y=56) stays clear of the ray band
                // at y=50 so it never earns the SoftConnectionEngine +0.40 ray-first-hit bonus.
                "<p:sp><p:nvSpPr><p:cNvPr id=\"101\" name=\"End\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"929\" y=\"56\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>MEND</a:t></a:r></a:p></p:txBody></p:sp>" +
                // Unsnapped (no stCxn/endCxn): forces geometry inference instead of a native match.
                "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"104\" name=\"Unsnapped\" /></p:nvCxnSpPr><p:spPr><a:xfrm><a:off x=\"100\" y=\"50\" /><a:ext cx=\"800\" cy=\"0\" /></a:xfrm></p:spPr></p:cxnSp>" +
                "</p:spTree></p:cSld></p:sld>",
            ["ppt/slides/_rels/slide1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />",
        };
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var part in parts)
            { using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), Encoding.UTF8); writer.Write(part.Value); }
        return output.ToArray();
    }

    private static byte[] CreateTwoIndependentGeometryConnectorPackage()
    {
        var entries = Entries(CreatePackage());
        var xml = Encoding.UTF8.GetString(entries["ppt/slides/slide1.xml"]);
        const string shapes =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"100\" name=\"A\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>PPT_R4_A</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"101\" name=\"B\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"300\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>PPT_R4_B</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"102\" name=\"C\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"10000\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>PPT_R4_C</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"103\" name=\"D\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"10300\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>PPT_R4_D</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"200\" name=\"A to B\" /></p:nvCxnSpPr><p:spPr><a:xfrm><a:off x=\"100\" y=\"50\" /><a:ext cx=\"200\" cy=\"0\" /></a:xfrm><a:ln><a:tailEnd type=\"triangle\" /></a:ln></p:spPr></p:cxnSp>" +
            "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"201\" name=\"C to D\" /></p:nvCxnSpPr><p:spPr><a:xfrm><a:off x=\"10100\" y=\"50\" /><a:ext cx=\"200\" cy=\"0\" /></a:xfrm><a:ln><a:tailEnd type=\"triangle\" /></a:ln></p:spPr></p:cxnSp>";
        entries["ppt/slides/slide1.xml"] = Encoding.UTF8.GetBytes(xml.Replace("</p:spTree>", shapes + "</p:spTree>", StringComparison.Ordinal));
        return Repack(entries);
    }

    private static VisualGraph VisualGraphOf(PptxExtractionResult extraction)
    {
        var graphs = extraction.Graph.Nodes
            .Where(candidate => candidate.Extensions?.ContainsKey("visual_graph") == true)
            .Select(candidate => candidate.Extensions!["visual_graph"].Deserialize<VisualGraph>()!)
            .ToArray();
        Assert.NotEmpty(graphs);
        if (graphs.Length == 1) return graphs[0];
        var combined = new VisualGraph("combined", graphs.SelectMany(graph => graph.Nodes).ToArray(),
            graphs.SelectMany(graph => graph.Edges).ToArray(), graphs.SelectMany(graph => graph.Diagnostics ?? []).ToArray(),
            graphs[0].Direction, Paths: graphs.SelectMany(graph => graph.Paths ?? []).ToArray(),
            SourceItems: graphs.SelectMany(graph => graph.SourceItems ?? []).ToArray());
        return combined with { Quality = VisualGraphValidator.ComputeQuality(combined) };
    }

    private static VisualGraph[] VisualGraphsOf(PptxExtractionResult extraction) => extraction.Graph.Nodes
        .Where(node => node.Extensions?.ContainsKey("visual_graph") == true)
        .Select(node => node.Extensions!["visual_graph"].Deserialize<VisualGraph>()!)
        .ToArray();

    private static string Label(IEnumerable<VisualGraph> graphs, string? nodeId) => graphs
        .SelectMany(graph => graph.Nodes)
        .Single(node => node.Id == nodeId).Label;

    private static DocumentGraph CorruptVisualGraph(DocumentGraph graph) => graph with
    {
        Partitions = graph.Partitions.Select(partition => partition with
        {
            Nodes = partition.Nodes.Select(node => node.Extensions?.ContainsKey("visual_graph") == true
                ? node with { Extensions = ReplaceVisualGraphExtension(node.Extensions!) }
                : node).ToArray()
        }).ToArray()
    };

    private static IReadOnlyDictionary<string, JsonElement> ReplaceVisualGraphExtension(IReadOnlyDictionary<string, JsonElement> extensions)
    {
        var copy = extensions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        copy["visual_graph"] = JsonSerializer.SerializeToElement("not-a-visual-graph");
        return copy;
    }

    private static byte[] CreatePackage(bool includeRichShape = false, bool includeComplexObjects = false)
    {
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
            ["ppt/presentation.xml"] = "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\" /></p:sldIdLst></p:presentation>",
            ["ppt/_rels/presentation.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"slide\" Target=\"slides/slide1.xml\" /></Relationships>",
            ["ppt/slides/slide1.xml"] = "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:cSld><p:spTree><p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title\" /><p:nvPr><p:ph type=\"title\" /></p:nvPr></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"640000\" y=\"320000\" /><a:ext cx=\"10800000\" cy=\"1000000\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr lIns=\"91440\" rIns=\"91440\" /><a:p><a:r><a:rPr lang=\"ja-JP\" sz=\"2800\"><a:latin typeface=\"Yu Mincho\" /><a:ea typeface=\"游明朝\" /></a:rPr><a:t>He</a:t></a:r><a:r><a:rPr lang=\"ja-JP\" sz=\"2600\"><a:latin typeface=\"BIZ UDPGothic\" /><a:ea typeface=\"BIZ UDPゴシック\" /></a:rPr><a:t>llo</a:t></a:r></a:p></p:txBody></p:sp><p:sp><p:nvSpPr><p:cNvPr id=\"5\" name=\"Body\" /><p:nvPr><p:ph type=\"body\" /></p:nvPr></p:nvSpPr><p:txBody><a:bodyPr /><a:lstStyle /><a:p><a:r><a:t>One</a:t></a:r></a:p><a:p><a:r><a:t>Two</a:t></a:r></a:p><a:p><a:r><a:t>Three</a:t></a:r></a:p></p:txBody></p:sp><p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"Table\" /></p:nvGraphicFramePr><a:graphic><a:graphicData><a:tbl><a:tr><a:tc><a:txBody><a:p><a:r><a:t>Cell</a:t></a:r></a:p></a:txBody></a:tc></a:tr></a:tbl></a:graphicData></a:graphic></p:graphicFrame><p:pic><p:nvPicPr><p:cNvPr id=\"4\" name=\"Image\" /></p:nvPicPr><p:blipFill><a:blip r:embed=\"rIdImage\" /></p:blipFill></p:pic></p:spTree></p:cSld></p:sld>",
            ["ppt/slides/_rels/slide1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdImage\" Type=\"image\" Target=\"../media/image1.png\" /><Relationship Id=\"rIdNotes\" Type=\"notesSlide\" Target=\"../notesSlides/notesSlide1.xml\" /></Relationships>",
            ["ppt/notesSlides/notesSlide1.xml"] = "<p:notes xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:t>Speaker note</a:t></p:notes>",
            ["ppt/theme/theme1.xml"] = "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:themeElements><a:fontScheme name=\"Corporate\"><a:majorFont><a:latin typeface=\"Aptos Display\" /><a:ea typeface=\"Yu Gothic\" /></a:majorFont></a:fontScheme></a:themeElements></a:theme>",
            ["ppt/media/image1.png"] = "image",
            ["custom/unknown.bin"] = "untouched"
        };
        if (includeRichShape)
        {
            const string richShape = "<p:sp><p:nvSpPr><p:cNvPr id=\"7\" name=\"Bullets\" /><p:nvPr><p:ph type=\"body\" /></p:nvPr></p:nvSpPr><p:txBody><a:bodyPr /><a:p><a:pPr lvl=\"1\"><a:buChar char=\"•\" /></a:pPr><a:r><a:rPr b=\"1\" sz=\"2400\" /><a:t>Emphasized</a:t></a:r></a:p></p:txBody></p:sp>";
            parts["ppt/slides/slide1.xml"] = parts["ppt/slides/slide1.xml"].Replace("</p:spTree>", richShape + "</p:spTree>", StringComparison.Ordinal);
        }
        if (includeComplexObjects)
        {
            const string complex = "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"8\" name=\"Flow connector\" /></p:nvCxnSpPr><p:spPr><a:xfrm><a:off x=\"640000\" y=\"1600000\" /><a:ext cx=\"3200000\" cy=\"0\" /></a:xfrm></p:spPr></p:cxnSp><p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"80\" name=\"Evidence group\" /></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"1\" cy=\"1\" /></a:xfrm></p:grpSpPr><p:sp><p:nvSpPr><p:cNvPr id=\"9\" name=\"Grouped text\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"640000\" y=\"1920000\" /><a:ext cx=\"10800000\" cy=\"900000\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>Grouped evidence</a:t></a:r></a:p></p:txBody></p:sp></p:grpSp><p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"10\" name=\"Readiness chart\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"640000\" y=\"3200000\" /><a:ext cx=\"10800000\" cy=\"2500000\" /></p:xfrm><a:graphic><a:graphicData><c:chart r:id=\"rIdChart\" /></a:graphicData></a:graphic></p:graphicFrame><p:sp><p:nvSpPr><p:cNvPr id=\"11\" name=\"Footer\" /><p:nvPr><p:ph type=\"ftr\" /></p:nvPr></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"640000\" y=\"6200000\" /><a:ext cx=\"10800000\" cy=\"300000\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>PROJECT ATLAS  1</a:t></a:r></a:p></p:txBody></p:sp>";
            parts["ppt/slides/slide1.xml"] = parts["ppt/slides/slide1.xml"]
                .Replace("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"", "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"", StringComparison.Ordinal)
                .Replace("</p:spTree>", complex + "</p:spTree>", StringComparison.Ordinal);
            parts["ppt/slides/_rels/slide1.xml.rels"] = parts["ppt/slides/_rels/slide1.xml.rels"]
                .Replace("</Relationships>", "<Relationship Id=\"rIdChart\" Type=\"chart\" Target=\"charts/chart1.xml\" /></Relationships>", StringComparison.Ordinal);
            parts["ppt/slides/charts/chart1.xml"] = "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"><c:chart /></c:chartSpace>";
            parts["ppt/slideMasters/slideMaster1.xml"] = "<p:sldMaster xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" />";
            parts["ppt/slideLayouts/slideLayout1.xml"] = "<p:sldLayout xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" />";
        }
        using var output = new MemoryStream(); using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) foreach (var part in parts) { using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), Encoding.UTF8); writer.Write(part.Value); }
        return output.ToArray();
    }
    private static Dictionary<string, byte[]> Entries(byte[] bytes)
    {
        using var input = new MemoryStream(bytes); using var zip = new ZipArchive(input); var result = new Dictionary<string, byte[]>(); foreach (var entry in zip.Entries) using (var source = entry.Open()) using (var output = new MemoryStream()) { source.CopyTo(output); result[entry.FullName] = output.ToArray(); }
        return result;
    }

    [Fact]
    public void ResolvesNestedGroupTransformsIntoAbsoluteBounds()
    {
        var entries = Entries(CreatePackage());
        var xml = Encoding.UTF8.GetString(entries["ppt/slides/slide1.xml"]);
        const string groups =
            "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"40\" name=\"Outer\" /></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"100\" y=\"200\" /><a:ext cx=\"400\" cy=\"300\" /><a:chOff x=\"10\" y=\"20\" /><a:chExt cx=\"100\" cy=\"100\" /></a:xfrm></p:grpSpPr>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"50\" name=\"Direct\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"10\" y=\"20\" /><a:ext cx=\"20\" cy=\"10\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>Direct</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"41\" name=\"Inner\" /></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"30\" y=\"40\" /><a:ext cx=\"50\" cy=\"50\" /><a:chOff x=\"0\" y=\"0\" /><a:chExt cx=\"100\" cy=\"100\" /></a:xfrm></p:grpSpPr>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"51\" name=\"Nested\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"20\" y=\"20\" /><a:ext cx=\"20\" cy=\"20\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>Nested</a:t></a:r></a:p></p:txBody></p:sp>" +
            "</p:grpSp></p:grpSp>" +
            "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"42\" name=\"Degenerate\" /></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"0\" cy=\"0\" /></a:xfrm></p:grpSpPr>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"52\" name=\"Degenerate child\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"1\" y=\"2\" /><a:ext cx=\"3\" cy=\"4\" /></a:xfrm></p:spPr></p:sp></p:grpSp>" +
            "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"43\" name=\"RotatedFlip\" /></p:nvGrpSpPr><p:grpSpPr><a:xfrm rot=\"1800000\" flipH=\"1\"><a:off x=\"0\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /><a:chOff x=\"0\" y=\"0\" /><a:chExt cx=\"100\" cy=\"100\" /></a:xfrm></p:grpSpPr>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"53\" name=\"Rotated child\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"10\" y=\"20\" /><a:ext cx=\"20\" cy=\"10\" /></a:xfrm></p:spPr></p:sp></p:grpSp>";
        xml = xml.Replace("</p:spTree>", groups + "</p:spTree>", StringComparison.Ordinal);
        entries["ppt/slides/slide1.xml"] = Encoding.UTF8.GetBytes(xml);

        var shapes = Assert.Single(new PptxAdapter().Extract(new MemoryStream(Repack(entries))).Slides).Shapes;
        var direct = Assert.Single(shapes, shape => shape.ShapeId == "50");
        Assert.Equal(100, direct.Geometry!.X);
        Assert.Equal(200, direct.Geometry.Y);
        Assert.Equal(80, direct.Geometry.Width);
        Assert.Equal(30, direct.Geometry.Height);

        var nested = Assert.Single(shapes, shape => shape.ShapeId == "51");
        Assert.Equal(220, nested.Geometry!.X);
        Assert.Equal(290, nested.Geometry.Y);
        Assert.Equal(40, nested.Geometry.Width);
        Assert.Equal(30, nested.Geometry.Height);

        var degenerate = Assert.Single(shapes, shape => shape.ShapeId == "52");
        Assert.True(double.IsFinite(degenerate.Geometry!.X));
        Assert.True(double.IsFinite(degenerate.Geometry.Y));
        var rotated = Assert.Single(shapes, shape => shape.ShapeId == "53");
        Assert.Equal(77.3205, rotated.Geometry!.X, precision: 4);
        Assert.Equal(34.0192, rotated.Geometry.Y, precision: 4);
        Assert.Equal(22.3205, rotated.Geometry.Width, precision: 4);
        Assert.Equal(18.6603, rotated.Geometry.Height, precision: 4);
        Assert.Equal(-150, rotated.Geometry.RotationDegrees, precision: 4);
    }

    [Fact]
    public void HiddenGroupExcludesChildShapesWhileSiblingVisibleGroupIsUnaffected()
    {
        var entries = Entries(CreatePackage());
        const string groups =
            "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"60\" name=\"HiddenGroup\" hidden=\"1\" /></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:grpSpPr>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"61\" name=\"Hidden group child\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"10\" cy=\"10\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>Hidden group child</a:t></a:r></a:p></p:txBody></p:sp>" +
            "</p:grpSp>" +
            "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"62\" name=\"VisibleGroup\" /></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:grpSpPr>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"63\" name=\"Visible group child\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"10\" cy=\"10\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>Visible group child</a:t></a:r></a:p></p:txBody></p:sp>" +
            "</p:grpSp>";
        var xml = Encoding.UTF8.GetString(entries["ppt/slides/slide1.xml"]).Replace("</p:spTree>", groups + "</p:spTree>", StringComparison.Ordinal);
        entries["ppt/slides/slide1.xml"] = Encoding.UTF8.GetBytes(xml);

        var extraction = new PptxAdapter().Extract(new MemoryStream(Repack(entries)));
        var slide = Assert.Single(extraction.Slides);

        var hiddenChild = Assert.Single(slide.Shapes, shape => shape.ShapeId == "61");
        Assert.True(hiddenChild.IsHidden);
        Assert.True(hiddenChild.IsHiddenByGroup);
        var hiddenNode = Assert.Single(extraction.Graph.Nodes, node => node.Source?.Locators.Any(locator => locator.Value == "61") == true);
        Assert.Equal(ContentLayer.Hidden, hiddenNode.Layer);
        Assert.True(hiddenNode.Extensions!["hidden_object"].GetBoolean());
        Assert.True(hiddenNode.Extensions!["hidden_by_group"].GetBoolean());

        var visibleChild = Assert.Single(slide.Shapes, shape => shape.ShapeId == "63");
        Assert.False(visibleChild.IsHidden);
        Assert.False(visibleChild.IsHiddenByGroup);
        var visibleNode = Assert.Single(extraction.Graph.Nodes, node => node.Source?.Locators.Any(locator => locator.Value == "63") == true);
        Assert.Equal(ContentLayer.Body, visibleNode.Layer);
        Assert.False(visibleNode.Extensions!.ContainsKey("hidden_by_group"));
    }

    [Fact]
    public void HiddenAncestorGroupExcludesDeeplyNestedShape()
    {
        var entries = Entries(CreatePackage());
        const string groups =
            "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"70\" name=\"Grandparent\" hidden=\"1\" /></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"200\" cy=\"200\" /></a:xfrm></p:grpSpPr>" +
            "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"71\" name=\"Parent\" /></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"100\" cy=\"100\" /></a:xfrm></p:grpSpPr>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"72\" name=\"Deep child\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"10\" cy=\"10\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>Deep child text</a:t></a:r></a:p></p:txBody></p:sp>" +
            "</p:grpSp></p:grpSp>";
        var xml = Encoding.UTF8.GetString(entries["ppt/slides/slide1.xml"]).Replace("</p:spTree>", groups + "</p:spTree>", StringComparison.Ordinal);
        entries["ppt/slides/slide1.xml"] = Encoding.UTF8.GetBytes(xml);

        var slide = Assert.Single(new PptxAdapter().Extract(new MemoryStream(Repack(entries))).Slides);
        var deepChild = Assert.Single(slide.Shapes, shape => shape.ShapeId == "72");
        // The immediate parent group ("Parent") is not itself hidden -- only the grandparent is.
        // IsHidden must still be true: visibility exclusion has to walk the whole ancestor stack,
        // not just the nearest enclosing group.
        Assert.True(deepChild.IsHidden);
        Assert.True(deepChild.IsHiddenByGroup);
    }

    private static byte[] Repack(Dictionary<string, byte[]> parts)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var part in parts)
            {
                using var target = zip.CreateEntry(part.Key).Open();
                target.Write(part.Value);
            }
        return output.ToArray();
    }

    private static int Count(string value, string needle)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0; index += needle.Length) count++;
        return count;
    }

    // ---------------------------------------------------------------------------------------
    // P17: table cell paragraph/line-break boundaries.
    // ---------------------------------------------------------------------------------------

    private static byte[] WithTableXml(string tableXml)
    {
        var entries = Entries(CreatePackage());
        var xml = Encoding.UTF8.GetString(entries["ppt/slides/slide1.xml"])
            .Replace("<a:tbl><a:tr><a:tc><a:txBody><a:p><a:r><a:t>Cell</a:t></a:r></a:p></a:txBody></a:tc></a:tr></a:tbl>", tableXml, StringComparison.Ordinal);
        entries["ppt/slides/slide1.xml"] = Encoding.UTF8.GetBytes(xml);
        return Repack(entries);
    }

    [Fact]
    public void TableCellJoinsMultipleParagraphsWithNewlineAndTrimsEdgeBlankParagraphs()
    {
        const string table = "<a:tbl><a:tr><a:tc><a:txBody>" +
            "<a:p/>" + // leading blank paragraph: must be trimmed, not rendered as a leading "\n"
            "<a:p><a:r><a:t>A</a:t></a:r></a:p><a:p><a:r><a:t>B</a:t></a:r></a:p><a:p><a:r><a:t>C</a:t></a:r></a:p>" +
            "<a:p/>" + // trailing blank paragraph: must also be trimmed
            "</a:txBody></a:tc></a:tr></a:tbl>";
        var shape = Assert.Single(new PptxAdapter().Extract(new MemoryStream(WithTableXml(table))).Slides).Shapes.Single(item => item.ShapeId == "3");

        var cell = Assert.Single(Assert.Single(shape.TableRows!));
        Assert.Equal("A\nB\nC", cell.Text);
    }

    [Fact]
    public void TableCellConvertsLineBreaksAndTabsToTextWithinAParagraph()
    {
        const string table = "<a:tbl><a:tr><a:tc><a:txBody><a:p>" +
            "<a:r><a:t>Line1</a:t></a:r><a:br /><a:r><a:t>Line2</a:t></a:r><a:tab /><a:r><a:t>Tabbed</a:t></a:r>" +
            "</a:p></a:txBody></a:tc></a:tr></a:tbl>";
        var shape = Assert.Single(new PptxAdapter().Extract(new MemoryStream(WithTableXml(table))).Slides).Shapes.Single(item => item.ShapeId == "3");

        var cell = Assert.Single(Assert.Single(shape.TableRows!));
        Assert.Equal("Line1\nLine2\tTabbed", cell.Text);
    }

    [Fact]
    public void MergedCellWithGridSpanPreservesMultipleParagraphsAndLeavesColumnCountIntact()
    {
        const string table = "<a:tbl><a:tr>" +
            "<a:tc gridSpan=\"2\"><a:txBody><a:p><a:r><a:t>Merged A</a:t></a:r></a:p><a:p><a:r><a:t>Merged B</a:t></a:r></a:p></a:txBody></a:tc>" +
            "<a:tc hMerge=\"1\"><a:txBody><a:p /></a:txBody></a:tc>" +
            "<a:tc><a:txBody><a:p><a:r><a:t>Plain</a:t></a:r></a:p></a:txBody></a:tc>" +
            "</a:tr></a:tbl>";
        var shape = Assert.Single(new PptxAdapter().Extract(new MemoryStream(WithTableXml(table))).Slides).Shapes.Single(item => item.ShapeId == "3");

        // The hMerge continuation is correctly dropped from the physical row (its origin's
        // ColSpan already accounts for it) rather than kept as a placeholder: TableGrid.TryCreate
        // advances its column cursor by ColSpan alone for an ordinary cell, so re-adding the
        // continuation here would double-count the column and misalign the grid.
        var row = Assert.Single(shape.TableRows!);
        Assert.Equal(2, row.Count);
        Assert.Equal("Merged A\nMerged B", row[0].Text);
        Assert.Equal(2, row[0].ColSpan);
        Assert.Equal("Plain", row[1].Text);
        Assert.True(TableGrid.TryCreate(new TableNodeContent(shape.TableRows!), out _, out var error), error);
    }

    // ---------------------------------------------------------------------------------------
    // P16: slide master/layout visible non-placeholder text inheritance.
    // ---------------------------------------------------------------------------------------

    private static byte[] CreateMasterLayoutPackage(bool slideShowMasterSp = true, bool layoutShowMasterSp = true, bool includeHiddenMasterShape = false)
    {
        var slideAttr = slideShowMasterSp ? "" : " showMasterSp=\"0\"";
        var layoutAttr = layoutShowMasterSp ? "" : " showMasterSp=\"0\"";
        var hiddenMasterShape = includeHiddenMasterShape
            ? "<p:sp><p:nvSpPr><p:cNvPr id=\"30\" name=\"Hidden master text\" hidden=\"1\" /></p:nvSpPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>HIDDEN-MASTER-TEXT</a:t></a:r></a:p></p:txBody></p:sp>"
            : "";
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
            ["ppt/presentation.xml"] = "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\" /></p:sldIdLst></p:presentation>",
            ["ppt/_rels/presentation.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"slide\" Target=\"slides/slide1.xml\" /></Relationships>",
            ["ppt/slides/slide1.xml"] =
                $"<p:sld{slideAttr} xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:cSld><p:spTree>" +
                "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title\" /><p:nvPr><p:ph type=\"title\" /></p:nvPr></p:nvSpPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>Slide title</a:t></a:r></a:p></p:txBody></p:sp>" +
                "</p:spTree></p:cSld></p:sld>",
            ["ppt/slides/_rels/slide1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdLayout\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\" /></Relationships>",
            ["ppt/slideLayouts/slideLayout1.xml"] =
                $"<p:sldLayout{layoutAttr} xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree>" +
                "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title Placeholder\" /><p:nvPr><p:ph type=\"title\" /></p:nvPr></p:nvSpPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>Click to edit title</a:t></a:r></a:p></p:txBody></p:sp>" +
                "<p:sp><p:nvSpPr><p:cNvPr id=\"9\" name=\"Layout decoration\" /></p:nvSpPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>LAYOUT-TEXT</a:t></a:r></a:p></p:txBody></p:sp>" +
                "</p:spTree></p:cSld></p:sldLayout>",
            ["ppt/slideLayouts/_rels/slideLayout1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdMaster\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\" /></Relationships>",
            ["ppt/slideMasters/slideMaster1.xml"] =
                "<p:sldMaster xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree>" +
                "<p:sp><p:nvSpPr><p:cNvPr id=\"20\" name=\"Master decoration\" /></p:nvSpPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>MASTER-TEXT</a:t></a:r></a:p></p:txBody></p:sp>" +
                hiddenMasterShape +
                "</p:spTree></p:cSld></p:sldMaster>",
        };
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var part in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), Encoding.UTF8);
                writer.Write(part.Value);
            }
        return output.ToArray();
    }

    [Fact]
    public void InheritsVisibleNonPlaceholderTextFromLayoutAndMasterButNotPlaceholders()
    {
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateMasterLayoutPackage()));
        var slide = Assert.Single(extraction.Slides);

        var layoutNode = Assert.Single(extraction.Graph.Nodes, node => node.Content is TextNodeContent text && text.Text == "LAYOUT-TEXT");
        Assert.Equal("layout", layoutNode.Extensions!["inherited_from"].GetString());
        Assert.Equal("ppt/slideLayouts/slideLayout1.xml", layoutNode.Extensions!["inherited_part"].GetString());
        Assert.Equal(ContentLayer.Body, layoutNode.Layer);
        Assert.Equal(NodeEditability.Protected, layoutNode.Editability);

        var masterNode = Assert.Single(extraction.Graph.Nodes, node => node.Content is TextNodeContent text && text.Text == "MASTER-TEXT");
        Assert.Equal("master", masterNode.Extensions!["inherited_from"].GetString());
        Assert.Equal("ppt/slideMasters/slideMaster1.xml", masterNode.Extensions!["inherited_part"].GetString());
        Assert.Equal(ContentLayer.Body, masterNode.Layer);

        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Content is TextNodeContent text && text.Text.Contains("Click to edit title", StringComparison.Ordinal));

        // Inherited shapes are ordered after the slide's own shapes.
        var order = slide.Shapes.Select((shape, index) => (shape.Text, index)).ToDictionary(item => item.Text, item => item.index);
        Assert.True(order["LAYOUT-TEXT"] > order["Slide title"]);
        Assert.True(order["MASTER-TEXT"] > order["Slide title"]);
    }

    [Fact]
    public void SlideShowMasterSpFalseHidesBothLayoutAndMasterText()
    {
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateMasterLayoutPackage(slideShowMasterSp: false)));

        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Content is TextNodeContent text && text.Text == "LAYOUT-TEXT");
        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Content is TextNodeContent text && text.Text == "MASTER-TEXT");
    }

    [Fact]
    public void LayoutShowMasterSpFalseHidesOnlyMasterText()
    {
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateMasterLayoutPackage(layoutShowMasterSp: false)));

        Assert.Contains(extraction.Graph.Nodes, node => node.Content is TextNodeContent text && text.Text == "LAYOUT-TEXT");
        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Content is TextNodeContent text && text.Text == "MASTER-TEXT");
    }

    [Fact]
    public void HiddenShapeOnMasterIsNotInheritedWhileVisibleMasterTextStillIs()
    {
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateMasterLayoutPackage(includeHiddenMasterShape: true)));

        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Content is TextNodeContent text && text.Text == "HIDDEN-MASTER-TEXT");
        Assert.Contains(extraction.Graph.Nodes, node => node.Content is TextNodeContent text && text.Text == "MASTER-TEXT");
    }

    // ---------------------------------------------------------------------------------------
    // P-Overlay: table-overlay detection (table-overlay-spec.md "抽出側の契約").
    // ---------------------------------------------------------------------------------------

    private static byte[] CreateTableOverlayPackage(string spTreeBody)
    {
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
            ["ppt/presentation.xml"] = "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\" /></p:sldIdLst></p:presentation>",
            ["ppt/_rels/presentation.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"slide\" Target=\"slides/slide1.xml\" /></Relationships>",
            ["ppt/slides/slide1.xml"] =
                "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:cSld><p:spTree>" +
                spTreeBody + "</p:spTree></p:cSld></p:sld>",
            ["ppt/slides/_rels/slide1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />",
        };
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var part in parts)
            { using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), Encoding.UTF8); writer.Write(part.Value); }
        return output.ToArray();
    }

    // 6 columns x 1,200,000 EMU, 3 rows x 400,000 EMU; frame off/ext exactly matches the declared
    // grid (no scaling engaged). Column bounds: 0/1.2M/2.4M/3.6M/4.8M/6.0M/7.2M. Row bounds:
    // 0/0.4M/0.8M/1.2M.
    private static string SixByThreeScheduleTableXml() =>
        "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"Schedule\" /></p:nvGraphicFramePr>" +
        "<p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"7200000\" cy=\"1200000\" /></p:xfrm>" +
        "<a:graphic><a:graphicData><a:tbl><a:tblGrid>" +
        string.Concat(Enumerable.Repeat("<a:gridCol w=\"1200000\" />", 6)) +
        "</a:tblGrid>" +
        string.Concat(Enumerable.Repeat("<a:tr h=\"400000\">" + string.Concat(Enumerable.Repeat("<a:tc><a:txBody><a:p /></a:txBody></a:tc>", 6)) + "</a:tr>", 3)) +
        "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";

    [Fact]
    public void DetectsScheduleOverlaysAndExcludesNonOverlappingHiddenAndBackgroundShapes()
    {
        const string shapes =
            // Arrow: rightArrow "設計" over row1, columns 1-3 (3 columns fully covered).
            "<p:sp><p:nvSpPr><p:cNvPr id=\"10\" name=\"Arrow\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"1200000\" y=\"400000\" /><a:ext cx=\"3600000\" cy=\"400000\" /></a:xfrm><a:prstGeom prst=\"rightArrow\"><a:avLst /></a:prstGeom></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>設計</a:t></a:r></a:p></p:txBody></p:sp>" +
            // Bar: textless rect over row2, columns 3-5.
            "<p:sp><p:nvSpPr><p:cNvPr id=\"11\" name=\"Bar\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"3600000\" y=\"800000\" /><a:ext cx=\"3600000\" cy=\"400000\" /></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst /></a:prstGeom></p:spPr></p:sp>" +
            // Marker: small diamond centered in row2, column 5 (too narrow to cover 50% of the
            // column band -> falls back to the center-X column rule; tall enough to cover >=50%
            // of the row band directly).
            "<p:sp><p:nvSpPr><p:cNvPr id=\"12\" name=\"Marker\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"6500000\" y=\"850000\" /><a:ext cx=\"200000\" cy=\"300000\" /></a:xfrm><a:prstGeom prst=\"diamond\"><a:avLst /></a:prstGeom></p:spPr></p:sp>" +
            // Vertical connector (no stCxn/endCxn): a zero-width straight line through column 4,
            // spanning all 3 rows, with a tail-side (end-point) arrowhead only.
            "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"13\" name=\"Vertical\" /></p:nvCxnSpPr><p:spPr><a:xfrm><a:off x=\"5400000\" y=\"0\" /><a:ext cx=\"0\" cy=\"1200000\" /></a:xfrm><a:ln><a:tailEnd type=\"triangle\" /></a:ln></p:spPr></p:cxnSp>" +
            // Label: a text box "▲レビュー" filling row1, column 4.
            "<p:sp><p:nvSpPr><p:cNvPr id=\"14\" name=\"Label\" /><p:cNvSpPr txBox=\"1\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"4800000\" y=\"400000\" /><a:ext cx=\"1200000\" cy=\"400000\" /></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst /></a:prstGeom></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>▲レビュー</a:t></a:r></a:p></p:txBody></p:sp>" +
            // NOT an overlay: a rect far away from the table (zero intersection).
            "<p:sp><p:nvSpPr><p:cNvPr id=\"15\" name=\"FarAway\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"20000000\" y=\"20000000\" /><a:ext cx=\"500000\" cy=\"500000\" /></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst /></a:prstGeom></p:spPr></p:sp>" +
            // NOT an overlay: a hidden arrow directly over the table.
            "<p:sp><p:nvSpPr><p:cNvPr id=\"16\" name=\"HiddenArrow\" hidden=\"1\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"2400000\" y=\"400000\" /><a:ext cx=\"1200000\" cy=\"400000\" /></a:xfrm><a:prstGeom prst=\"rightArrow\"><a:avLst /></a:prstGeom></p:spPr></p:sp>";
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(SixByThreeScheduleTableXml() + shapes)));

        var tableNode = Assert.Single(extraction.Graph.Nodes, node => node.Kind == NodeKind.Table);
        var overlays = tableNode.Extensions!["table_overlays"].Deserialize<PptxTableOverlay[]>()!;
        // Sort order is (StartRow, StartColumn, ShapeId-as-number): (0,4) < (1,1) < (1,4) < (2,3) < (2,5).
        Assert.Equal(["13", "10", "14", "11", "12"], overlays.Select(o => o.ShapeId).ToArray());

        var connector = overlays.Single(o => o.ShapeId == "13");
        Assert.Equal(("arrow", "down", "vertical", 0, 2, 4, 4),
            (connector.Kind, connector.Direction, connector.Axis, connector.StartRow, connector.EndRow, connector.StartColumn, connector.EndColumn));
        Assert.Equal("", connector.Text);
        Assert.Null(connector.ShapePreset);

        var arrow = overlays.Single(o => o.ShapeId == "10");
        Assert.Equal(("arrow", "right", "horizontal", 1, 1, 1, 3),
            (arrow.Kind, arrow.Direction, arrow.Axis, arrow.StartRow, arrow.EndRow, arrow.StartColumn, arrow.EndColumn));
        Assert.Equal("設計", arrow.Text);
        Assert.Equal("rightArrow", arrow.ShapePreset);

        var label = overlays.Single(o => o.ShapeId == "14");
        Assert.Equal(("label", "none", "horizontal", 1, 1, 4, 4),
            (label.Kind, label.Direction, label.Axis, label.StartRow, label.EndRow, label.StartColumn, label.EndColumn));
        Assert.Equal("▲レビュー", label.Text);

        var bar = overlays.Single(o => o.ShapeId == "11");
        Assert.Equal(("bar", "none", "horizontal", 2, 2, 3, 5),
            (bar.Kind, bar.Direction, bar.Axis, bar.StartRow, bar.EndRow, bar.StartColumn, bar.EndColumn));
        Assert.Equal("rect", bar.ShapePreset);

        var marker = overlays.Single(o => o.ShapeId == "12");
        Assert.Equal(("marker", "none", "horizontal", 2, 2, 5, 5),
            (marker.Kind, marker.Direction, marker.Axis, marker.StartRow, marker.EndRow, marker.StartColumn, marker.EndColumn));
        Assert.Equal("diamond", marker.ShapePreset);

        foreach (var overlayId in new[] { "10", "11", "12", "13", "14" })
        {
            var node = Assert.Single(extraction.Graph.Nodes, n => n.Source?.Locators.Any(l => l.Value == overlayId) == true);
            Assert.Equal("3", node.Extensions!["table_overlay_host"].GetString());
            Assert.True(node.Extensions!["table_overlay"].GetBoolean());
        }

        foreach (var nonOverlayId in new[] { "15", "16" })
        {
            var node = Assert.Single(extraction.Graph.Nodes, n => n.Source?.Locators.Any(l => l.Value == nonOverlayId) == true);
            Assert.False(node.Extensions!.ContainsKey("table_overlay_host"));
            Assert.False(node.Extensions!.ContainsKey("table_overlay"));
        }

        // Every directional shape on the slide is an overlay, so the visual-flow inference input
        // is empty (no connectors, no directional shapes) and no phantom "Visual flow" diagram
        // node is produced.
        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Kind == NodeKind.Diagram);
    }

    [Fact]
    public void RightArrowRotated180DegreesResolvesLeftDirection()
    {
        // Two 500,000-tall rows (F3(b): a host table needs >= 2 rows); the arrow only covers the
        // first, so its row range stays (0,0) exactly as when this table was a single 1,000,000
        // EMU row.
        var table =
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"2000000\" cy=\"1000000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid><a:gridCol w=\"1000000\" /><a:gridCol w=\"1000000\" /></a:tblGrid>" +
            string.Concat(Enumerable.Repeat("<a:tr h=\"500000\">" + string.Concat(Enumerable.Repeat("<a:tc><a:txBody><a:p /></a:txBody></a:tc>", 2)) + "</a:tr>", 2)) +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        const string arrow =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"10\" name=\"Arrow\" /></p:nvSpPr><p:spPr><a:xfrm rot=\"10800000\"><a:off x=\"0\" y=\"0\" /><a:ext cx=\"1000000\" cy=\"500000\" /></a:xfrm><a:prstGeom prst=\"rightArrow\"><a:avLst /></a:prstGeom></p:spPr></p:sp>";
        var tableNode = Assert.Single(new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(table + arrow))).Graph.Nodes, node => node.Kind == NodeKind.Table);
        var overlay = Assert.Single(tableNode.Extensions!["table_overlays"].Deserialize<PptxTableOverlay[]>()!);
        Assert.Equal(("arrow", "left", "horizontal", 0, 0, 0, 0), (overlay.Kind, overlay.Direction, overlay.Axis, overlay.StartRow, overlay.EndRow, overlay.StartColumn, overlay.EndColumn));
    }

    [Fact]
    public void RightArrowFlippedHorizontallyResolvesLeftDirection()
    {
        var table =
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"2000000\" cy=\"1000000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid><a:gridCol w=\"1000000\" /><a:gridCol w=\"1000000\" /></a:tblGrid>" +
            string.Concat(Enumerable.Repeat("<a:tr h=\"500000\">" + string.Concat(Enumerable.Repeat("<a:tc><a:txBody><a:p /></a:txBody></a:tc>", 2)) + "</a:tr>", 2)) +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        const string arrow =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"10\" name=\"Arrow\" /></p:nvSpPr><p:spPr><a:xfrm flipH=\"1\"><a:off x=\"0\" y=\"0\" /><a:ext cx=\"1000000\" cy=\"500000\" /></a:xfrm><a:prstGeom prst=\"rightArrow\"><a:avLst /></a:prstGeom></p:spPr></p:sp>";
        var tableNode = Assert.Single(new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(table + arrow))).Graph.Nodes, node => node.Kind == NodeKind.Table);
        var overlay = Assert.Single(tableNode.Extensions!["table_overlays"].Deserialize<PptxTableOverlay[]>()!);
        Assert.Equal("left", overlay.Direction);
    }

    [Fact]
    public void RightArrowFlippedHorizontallyWithNinetyDegreeRotationResolvesUpDirection()
    {
        // F2: flip is applied BEFORE rotation (DrawingML / ShapeOrientation order), not after --
        // flipH mirrors "right" to "left" first, then a 90-degree turn (right->down->left->up)
        // advances "left" to "up".
        var table =
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"800000\" cy=\"1200000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid><a:gridCol w=\"400000\" /><a:gridCol w=\"400000\" /></a:tblGrid>" +
            string.Concat(Enumerable.Repeat("<a:tr h=\"400000\">" + string.Concat(Enumerable.Repeat("<a:tc><a:txBody><a:p /></a:txBody></a:tc>", 2)) + "</a:tr>", 3)) +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        const string arrow =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"10\" name=\"Arrow\" /></p:nvSpPr><p:spPr><a:xfrm rot=\"5400000\" flipH=\"1\"><a:off x=\"-400000\" y=\"400000\" /><a:ext cx=\"1200000\" cy=\"400000\" /></a:xfrm><a:prstGeom prst=\"rightArrow\"><a:avLst /></a:prstGeom></p:spPr></p:sp>";
        var tableNode = Assert.Single(new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(table + arrow))).Graph.Nodes, node => node.Kind == NodeKind.Table);
        var overlay = Assert.Single(tableNode.Extensions!["table_overlays"].Deserialize<PptxTableOverlay[]>()!);
        Assert.Equal(("up", "vertical"), (overlay.Direction, overlay.Axis));
    }

    [Fact]
    public void RightArrowFlippedVerticallyWithNinetyDegreeRotationResolvesDownDirection()
    {
        // F2: flipV only mirrors up/down, so it leaves the base "right" direction untouched; the
        // measured rotation is the plain 90 degrees (flipV never touches the horizontal reference
        // vector TransformGeometry measures rotation from), and that 90-degree turn alone
        // (right -> down) is what produces "down" here.
        var table =
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"800000\" cy=\"1200000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid><a:gridCol w=\"400000\" /><a:gridCol w=\"400000\" /></a:tblGrid>" +
            string.Concat(Enumerable.Repeat("<a:tr h=\"400000\">" + string.Concat(Enumerable.Repeat("<a:tc><a:txBody><a:p /></a:txBody></a:tc>", 2)) + "</a:tr>", 3)) +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        const string arrow =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"10\" name=\"Arrow\" /></p:nvSpPr><p:spPr><a:xfrm rot=\"5400000\" flipV=\"1\"><a:off x=\"-400000\" y=\"400000\" /><a:ext cx=\"1200000\" cy=\"400000\" /></a:xfrm><a:prstGeom prst=\"rightArrow\"><a:avLst /></a:prstGeom></p:spPr></p:sp>";
        var tableNode = Assert.Single(new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(table + arrow))).Graph.Nodes, node => node.Kind == NodeKind.Table);
        var overlay = Assert.Single(tableNode.Extensions!["table_overlays"].Deserialize<PptxTableOverlay[]>()!);
        Assert.Equal(("down", "vertical"), (overlay.Direction, overlay.Axis));
    }

    [Fact]
    public void LeftRightArrowRotated90DegreesStaysBothButTogglesToVerticalAxis()
    {
        var table =
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"2000000\" cy=\"1000000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid><a:gridCol w=\"1000000\" /><a:gridCol w=\"1000000\" /></a:tblGrid>" +
            string.Concat(Enumerable.Repeat("<a:tr h=\"500000\">" + string.Concat(Enumerable.Repeat("<a:tc><a:txBody><a:p /></a:txBody></a:tc>", 2)) + "</a:tr>", 2)) +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        const string arrow =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"10\" name=\"Arrow\" /></p:nvSpPr><p:spPr><a:xfrm rot=\"5400000\"><a:off x=\"0\" y=\"0\" /><a:ext cx=\"1000000\" cy=\"500000\" /></a:xfrm><a:prstGeom prst=\"leftRightArrow\"><a:avLst /></a:prstGeom></p:spPr></p:sp>";
        var tableNode = Assert.Single(new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(table + arrow))).Graph.Nodes, node => node.Kind == NodeKind.Table);
        var overlay = Assert.Single(tableNode.Extensions!["table_overlays"].Deserialize<PptxTableOverlay[]>()!);
        Assert.Equal(("both", "vertical"), (overlay.Direction, overlay.Axis));
    }

    [Fact]
    public void RightArrowRotated90DegreesResolvesDownDirectionAndVerticalAxis()
    {
        // Two 400,000-wide columns (so the arrow, which only ever covers column 0, never reaches
        // the 90% background-frame exclusion threshold), 3 rows of 400,000 each. The arrow's
        // pre-rotation local geometry is a short-wide rightArrow (1,200,000 x 400,000); after a
        // 90-degree rotation about its own center, TransformGeometry's AABB becomes the tall-thin
        // box (0,0)-(400000,1200000) that spans column 0 across all 3 rows.
        var table =
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"800000\" cy=\"1200000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid><a:gridCol w=\"400000\" /><a:gridCol w=\"400000\" /></a:tblGrid>" +
            string.Concat(Enumerable.Repeat("<a:tr h=\"400000\">" + string.Concat(Enumerable.Repeat("<a:tc><a:txBody><a:p /></a:txBody></a:tc>", 2)) + "</a:tr>", 3)) +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        const string arrow =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"10\" name=\"Arrow\" /></p:nvSpPr><p:spPr><a:xfrm rot=\"5400000\"><a:off x=\"-400000\" y=\"400000\" /><a:ext cx=\"1200000\" cy=\"400000\" /></a:xfrm><a:prstGeom prst=\"rightArrow\"><a:avLst /></a:prstGeom></p:spPr></p:sp>";
        var tableNode = Assert.Single(new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(table + arrow))).Graph.Nodes, node => node.Kind == NodeKind.Table);
        var overlay = Assert.Single(tableNode.Extensions!["table_overlays"].Deserialize<PptxTableOverlay[]>()!);
        Assert.Equal(("arrow", "down", "vertical", 0, 2, 0, 0), (overlay.Kind, overlay.Direction, overlay.Axis, overlay.StartRow, overlay.EndRow, overlay.StartColumn, overlay.EndColumn));
    }

    [Fact]
    public void OverlayDetectionUsesAbsoluteBoundsForATableInsideAGroupTransform()
    {
        // The table lives inside a group whose transform doubles its child coordinate space
        // (chExt half of ext) and offsets it by (500000,500000). The table's absolute geometry
        // becomes (500000,500000,2000000,1000000); its 4 local gridCol widths (250000 each,
        // summing to only half the absolute width) get proportionally re-scaled to 500000 each.
        // The arrow lives OUTSIDE the group at plain absolute coordinates and must still resolve
        // against those absolute, group-transformed table bounds.
        var groupAndTable =
            "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"20\" name=\"Group\" /></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"500000\" y=\"500000\" /><a:ext cx=\"2000000\" cy=\"1000000\" /><a:chOff x=\"0\" y=\"0\" /><a:chExt cx=\"1000000\" cy=\"500000\" /></a:xfrm></p:grpSpPr>" +
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"1000000\" cy=\"500000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid>" + string.Concat(Enumerable.Repeat("<a:gridCol w=\"250000\" />", 4)) + "</a:tblGrid>" +
            string.Concat(Enumerable.Repeat("<a:tr h=\"250000\">" + string.Concat(Enumerable.Repeat("<a:tc><a:txBody><a:p /></a:txBody></a:tc>", 4)) + "</a:tr>", 2)) +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame></p:grpSp>";
        const string arrow =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"10\" name=\"Arrow\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"1000000\" y=\"1000000\" /><a:ext cx=\"1000000\" cy=\"500000\" /></a:xfrm><a:prstGeom prst=\"rightArrow\"><a:avLst /></a:prstGeom></p:spPr></p:sp>";
        var tableNode = Assert.Single(new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(groupAndTable + arrow))).Graph.Nodes, node => node.Kind == NodeKind.Table);
        var overlay = Assert.Single(tableNode.Extensions!["table_overlays"].Deserialize<PptxTableOverlay[]>()!);
        Assert.Equal((1, 2), (overlay.StartColumn, overlay.EndColumn));
        Assert.Equal((1, 1), (overlay.StartRow, overlay.EndRow));
    }

    [Fact]
    public void DeclaredRowHeightsAreScaledToTheFrameHeightBeforeAssigningRows()
    {
        // 3 rows declared h="300000" (summing to 900,000) but the frame's own ext cy is
        // 1,500,000 -- PowerPoint treats a:tr@h as a minimum, so the real per-row height is
        // scaled by 1,500,000/900,000 to 500,000 each (bounds 0/500000/1000000/1500000). A shape
        // at y=700000..900000 falls in the *scaled* second row; under the raw (unscaled)
        // boundaries it would instead land in the third.
        var table =
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"2000000\" cy=\"1500000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid><a:gridCol w=\"1000000\" /><a:gridCol w=\"1000000\" /></a:tblGrid>" +
            string.Concat(Enumerable.Repeat("<a:tr h=\"300000\">" + string.Concat(Enumerable.Repeat("<a:tc><a:txBody><a:p /></a:txBody></a:tc>", 2)) + "</a:tr>", 3)) +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        const string arrow =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"10\" name=\"Arrow\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"700000\" /><a:ext cx=\"1000000\" cy=\"200000\" /></a:xfrm><a:prstGeom prst=\"rightArrow\"><a:avLst /></a:prstGeom></p:spPr></p:sp>";
        var tableNode = Assert.Single(new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(table + arrow))).Graph.Nodes, node => node.Kind == NodeKind.Table);
        var overlay = Assert.Single(tableNode.Extensions!["table_overlays"].Deserialize<PptxTableOverlay[]>()!);
        Assert.Equal((1, 1), (overlay.StartRow, overlay.EndRow));
        Assert.Equal((0, 0), (overlay.StartColumn, overlay.EndColumn));
    }

    [Fact]
    public void BackgroundFrameCoveringTheWholeTableIsExcludedWhileARealConnectorFlowElsewhereStillYieldsADiagram()
    {
        var table =
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"600000\" cy=\"600000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid><a:gridCol w=\"300000\" /><a:gridCol w=\"300000\" /></a:tblGrid>" +
            string.Concat(Enumerable.Repeat("<a:tr h=\"300000\">" + string.Concat(Enumerable.Repeat("<a:tc><a:txBody><a:p /></a:txBody></a:tc>", 2)) + "</a:tr>", 2)) +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        // Exactly coincides with the table -- 100% of the table's area, well over the 90%
        // background-frame exclusion threshold.
        const string background =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"9\" name=\"Background\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"600000\" cy=\"600000\" /></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst /></a:prstGeom></p:spPr></p:sp>";
        // A genuine native-connector flow, far from the table: two labeled rects plus a
        // stCxn/endCxn connector whose endpoints exactly touch both shapes (same pattern as
        // Reserves_all_connector_endpoints_before_assigning_edge_labels above).
        const string flow =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"30\" name=\"FlowA\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"2000000\" y=\"2000000\" /><a:ext cx=\"200000\" cy=\"200000\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>FlowA</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"31\" name=\"FlowB\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"3000000\" y=\"2000000\" /><a:ext cx=\"200000\" cy=\"200000\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>FlowB</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"32\" name=\"Flow\" /><a:stCxn id=\"30\" /><a:endCxn id=\"31\" /></p:nvCxnSpPr><p:spPr><a:xfrm><a:off x=\"2200000\" y=\"2100000\" /><a:ext cx=\"800000\" cy=\"0\" /></a:xfrm><a:ln><a:tailEnd type=\"triangle\" /></a:ln></p:spPr></p:cxnSp>";
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(table + background + flow)));

        var tableNode = Assert.Single(extraction.Graph.Nodes, node => node.Kind == NodeKind.Table);
        Assert.False(tableNode.Extensions!.ContainsKey("table_overlays"));
        var backgroundNode = Assert.Single(extraction.Graph.Nodes, n => n.Source?.Locators.Any(l => l.Value == "9") == true);
        Assert.False(backgroundNode.Extensions!.ContainsKey("table_overlay_host"));

        var visual = VisualGraphOf(extraction);
        Assert.Equal(2, visual.Nodes.Count);
        var edge = Assert.Single(visual.Edges);
        Assert.NotNull(edge.SourceId);
        Assert.NotNull(edge.TargetId);
    }

    [Fact]
    public void ConnectorWithBothStartAndEndConnectionsIsNeverTreatedAsATableOverlay()
    {
        var table =
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"600000\" cy=\"300000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid><a:gridCol w=\"300000\" /><a:gridCol w=\"300000\" /></a:tblGrid>" +
            "<a:tr h=\"300000\">" + string.Concat(Enumerable.Repeat("<a:tc><a:txBody><a:p /></a:txBody></a:tc>", 2)) + "</a:tr>" +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        // Both ends wired (to ids that need not resolve to real shapes for this rule): a native
        // connector edge, never schedule-overlay content, regardless of geometry.
        const string connector =
            "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"20\" name=\"Native\" /><a:stCxn id=\"900\" /><a:endCxn id=\"901\" /></p:nvCxnSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"150000\" /><a:ext cx=\"600000\" cy=\"0\" /></a:xfrm></p:spPr></p:cxnSp>";
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(table + connector)));

        var tableNode = Assert.Single(extraction.Graph.Nodes, node => node.Kind == NodeKind.Table);
        Assert.False(tableNode.Extensions!.ContainsKey("table_overlays"));
        var connectorNode = Assert.Single(extraction.Graph.Nodes, n => n.Source?.Locators.Any(l => l.Value == "20") == true);
        Assert.False(connectorNode.Extensions!.ContainsKey("table_overlay_host"));
        Assert.False(connectorNode.Extensions!.ContainsKey("table_overlay"));
    }

    [Fact]
    public void OverlaysOnTheSameCellSortByNumericShapeIdNotOrdinalText()
    {
        var table =
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"2000000\" cy=\"1000000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid><a:gridCol w=\"1000000\" /><a:gridCol w=\"1000000\" /></a:tblGrid>" +
            string.Concat(Enumerable.Repeat("<a:tr h=\"500000\">" + string.Concat(Enumerable.Repeat("<a:tc><a:txBody><a:p /></a:txBody></a:tc>", 2)) + "</a:tr>", 2)) +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        // Ordinal string comparison would put "10" before "2"; numeric comparison (spec: "ShapeId
        // を数値として...序数比較") must put "2" first.
        const string bars =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"10\" name=\"BarTen\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"1000000\" cy=\"1000000\" /></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst /></a:prstGeom></p:spPr></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"BarTwo\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"1000000\" cy=\"1000000\" /></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst /></a:prstGeom></p:spPr></p:sp>";
        var tableNode = Assert.Single(new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(table + bars))).Graph.Nodes, node => node.Kind == NodeKind.Table);
        var overlays = tableNode.Extensions!["table_overlays"].Deserialize<PptxTableOverlay[]>()!;
        Assert.Equal(["2", "10"], overlays.Select(o => o.ShapeId).ToArray());
    }

    [Fact]
    public void PowerPoint2016ExtensionListDecoysOnGridColAndTrDoNotZeroTheHostTableGeometry()
    {
        // PowerPoint 2016+ writes <a:extLst><a:ext uri="..."><a16:rowId/colId .../></a:ext></a:extLst>
        // on every a:tr and a:gridCol of a saved table, positioned AFTER the graphicFrame's own
        // p:xfrm/a:ext -- each such <a:ext> lacks cx/cy, so ReadShapes' flat "ext" element matcher
        // must not mistake it for that authoritative size element and zero out the table's own
        // Width/Height (F1). A p:cNvPr/a:extLst/a:ext (a16:creationId, PowerPoint's own shape-id
        // extension) on the arrow shape exercises the same guard from the other direction.
        const string a16Ns = "xmlns:a16=\"http://schemas.microsoft.com/office/drawing/2014/main\"";
        static string ColExt(int id, string ns) =>
            $"<a:extLst><a:ext uri=\"{{9D8B030D-6E8A-4147-A177-3AD203B41FA5}}\"><a16:colId {ns} val=\"{id}\" /></a:ext></a:extLst>";
        static string RowExt(int id, string ns) =>
            $"<a:extLst><a:ext uri=\"{{0D108BD9-81ED-4DB2-BD59-A6C34878D82A}}\"><a16:rowId {ns} val=\"{id}\" /></a:ext></a:extLst>";
        var table =
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"2000000\" cy=\"1000000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid>" +
            $"<a:gridCol w=\"1000000\">{ColExt(0, a16Ns)}</a:gridCol><a:gridCol w=\"1000000\">{ColExt(1, a16Ns)}</a:gridCol>" +
            "</a:tblGrid>" +
            $"<a:tr h=\"500000\"><a:tc><a:txBody><a:p /></a:txBody></a:tc><a:tc><a:txBody><a:p /></a:txBody></a:tc>{RowExt(0, a16Ns)}</a:tr>" +
            $"<a:tr h=\"500000\"><a:tc><a:txBody><a:p /></a:txBody></a:tc><a:tc><a:txBody><a:p /></a:txBody></a:tc>{RowExt(1, a16Ns)}</a:tr>" +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        var arrow =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"10\" name=\"Arrow\"><a:extLst><a:ext uri=\"{FF2B5EF4-FFF2-40B4-BE49-F238E27FC236}\">" +
            $"<a16:creationId {a16Ns} id=\"{{00000000-0000-0000-0000-000000000000}}\" /></a:ext></a:extLst></p:cNvPr></p:nvSpPr>" +
            "<p:spPr><a:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"1000000\" cy=\"1000000\" /></a:xfrm><a:prstGeom prst=\"rightArrow\"><a:avLst /></a:prstGeom></p:spPr></p:sp>";
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(table + arrow)));

        var tableNode = Assert.Single(extraction.Graph.Nodes, node => node.Kind == NodeKind.Table);
        Assert.Equal(2000000, tableNode.Geometry!.Width);
        Assert.Equal(1000000, tableNode.Geometry!.Height);
        var overlay = Assert.Single(tableNode.Extensions!["table_overlays"].Deserialize<PptxTableOverlay[]>()!);
        Assert.Equal("arrow", overlay.Kind);
    }

    [Fact]
    public void GroupTransformExtLstAfterXfrmDoesNotZeroTheGroupChildExtent()
    {
        // Mirrors OverlayDetectionUsesAbsoluteBoundsForATableInsideAGroupTransform but with a
        // PowerPoint-style <a:extLst><a:ext uri="..."/></a:extLst> appended to grpSpPr AFTER its
        // own a:xfrm (F1): ParseGroupTransform's "ext" handler must ignore this decoy the same way
        // ReadShapes' shape-level one does, or it re-zeroes the group's already-parsed ext and the
        // scale/absolute-position computation for everything nested inside collapses.
        var groupAndTable =
            "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"20\" name=\"Group\" /></p:nvGrpSpPr>" +
            "<p:grpSpPr><a:xfrm><a:off x=\"500000\" y=\"500000\" /><a:ext cx=\"2000000\" cy=\"1000000\" /><a:chOff x=\"0\" y=\"0\" /><a:chExt cx=\"1000000\" cy=\"500000\" /></a:xfrm>" +
            "<a:extLst><a:ext uri=\"{D1512A61-5D53-4211-9C90-8DDD4CB6BEF4}\" /></a:extLst></p:grpSpPr>" +
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"1000000\" cy=\"500000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid>" + string.Concat(Enumerable.Repeat("<a:gridCol w=\"250000\" />", 4)) + "</a:tblGrid>" +
            string.Concat(Enumerable.Repeat("<a:tr h=\"250000\">" + string.Concat(Enumerable.Repeat("<a:tc><a:txBody><a:p /></a:txBody></a:tc>", 4)) + "</a:tr>", 2)) +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame></p:grpSp>";
        const string arrow =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"10\" name=\"Arrow\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"1000000\" y=\"1000000\" /><a:ext cx=\"1000000\" cy=\"500000\" /></a:xfrm><a:prstGeom prst=\"rightArrow\"><a:avLst /></a:prstGeom></p:spPr></p:sp>";
        var tableNode = Assert.Single(new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(groupAndTable + arrow))).Graph.Nodes, node => node.Kind == NodeKind.Table);
        var overlay = Assert.Single(tableNode.Extensions!["table_overlays"].Deserialize<PptxTableOverlay[]>()!);
        Assert.Equal((1, 2), (overlay.StartColumn, overlay.EndColumn));
        Assert.Equal((1, 1), (overlay.StartRow, overlay.EndRow));
    }

    [Fact]
    public void ShapeWiredAsAConnectorEndpointIsNeverTreatedAsATableOverlayEvenWhenItOverlapsTheTable()
    {
        var table =
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"1000000\" cy=\"1000000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid><a:gridCol w=\"500000\" /><a:gridCol w=\"500000\" /></a:tblGrid>" +
            string.Concat(Enumerable.Repeat("<a:tr h=\"500000\">" + string.Concat(Enumerable.Repeat("<a:tc><a:txBody><a:p /></a:txBody></a:tc>", 2)) + "</a:tr>", 2)) +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        // A roundRect that overlaps the table's bottom edge by 60% of its own area (600,000 of a
        // 1,000,000 EMU square is inside the table -- comfortably above the 50% overlay threshold)
        // but is wired as a connector's stCxn target elsewhere on the slide (F3(a)): it must stay a
        // diagram node, and the connector flow it participates in must still produce a Diagram.
        const string node =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"40\" name=\"Node\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"400000\" /><a:ext cx=\"1000000\" cy=\"1000000\" /></a:xfrm><a:prstGeom prst=\"roundRect\"><a:avLst /></a:prstGeom></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>Node</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"41\" name=\"Other\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"3000000\" y=\"3000000\" /><a:ext cx=\"200000\" cy=\"200000\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>Other</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"42\" name=\"Flow\" /><a:stCxn id=\"40\" /><a:endCxn id=\"41\" /></p:nvCxnSpPr><p:spPr><a:xfrm><a:off x=\"1000000\" y=\"800000\" /><a:ext cx=\"2000000\" cy=\"2200000\" /></a:xfrm><a:ln><a:tailEnd type=\"triangle\" /></a:ln></p:spPr></p:cxnSp>";
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(table + node)));

        var tableNode = Assert.Single(extraction.Graph.Nodes, n => n.Kind == NodeKind.Table);
        Assert.False(tableNode.Extensions!.ContainsKey("table_overlays"));
        var nodeShape = Assert.Single(extraction.Graph.Nodes, n => n.Source?.Locators.Any(l => l.Value == "40") == true);
        Assert.False(nodeShape.Extensions!.ContainsKey("table_overlay_host"));

        var visual = VisualGraphOf(extraction);
        Assert.Equal(2, visual.Nodes.Count);
        var edge = Assert.Single(visual.Edges);
        Assert.NotNull(edge.SourceId);
        Assert.NotNull(edge.TargetId);
    }

    [Fact]
    public void ShapeOverlappingATableEdgeByLessThanHalfItsAreaIsNotAnOverlay()
    {
        var table =
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"1000000\" cy=\"1000000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid><a:gridCol w=\"500000\" /><a:gridCol w=\"500000\" /></a:tblGrid>" +
            string.Concat(Enumerable.Repeat("<a:tr h=\"500000\">" + string.Concat(Enumerable.Repeat("<a:tc><a:txBody><a:p /></a:txBody></a:tc>", 2)) + "</a:tr>", 2)) +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        // A 1,000,000 EMU square (homePlate, an arrow-classified preset) positioned so only
        // 400,000 EMU of its width -- 40% of its own area -- falls inside the table's right edge:
        // below the general 50% overlay threshold.
        const string shape =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"10\" name=\"Edge\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"600000\" y=\"0\" /><a:ext cx=\"1000000\" cy=\"1000000\" /></a:xfrm><a:prstGeom prst=\"homePlate\"><a:avLst /></a:prstGeom></p:spPr></p:sp>";
        var tableNode = Assert.Single(new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(table + shape))).Graph.Nodes, n => n.Kind == NodeKind.Table);
        Assert.False(tableNode.Extensions!.ContainsKey("table_overlays"));
    }

    [Fact]
    public void TextBoxOverlayRequiresNinetyPercentContainmentWhileOtherKindsUseTheGeneralFiftyPercentThreshold()
    {
        var table =
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"1000000\" cy=\"1000000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid><a:gridCol w=\"500000\" /><a:gridCol w=\"500000\" /></a:tblGrid>" +
            string.Concat(Enumerable.Repeat("<a:tr h=\"500000\">" + string.Concat(Enumerable.Repeat("<a:tc><a:txBody><a:p /></a:txBody></a:tc>", 2)) + "</a:tr>", 2)) +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        // Same 400,000 EMU square text box (deliberately much smaller than the table, so its own
        // containment ratio and the table's background-frame coverage ratio never coincide) at two
        // positions along the table's right edge: 60% of its own area inside the table
        // (comfortably above the general 50% overlay threshold, but below the stricter 90% (F3(c))
        // a label/note textbox needs so a note box hanging off a table edge is never swallowed)
        // and 95% inside.
        static string TextBox(string id, int x) =>
            $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Note{id}\" /><p:cNvSpPr txBox=\"1\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"{x}\" y=\"0\" /><a:ext cx=\"400000\" cy=\"400000\" /></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst /></a:prstGeom></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>Note</a:t></a:r></a:p></p:txBody></p:sp>";

        var sixtyPercent = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(table + TextBox("10", 760000))));
        var sixtyPercentTable = Assert.Single(sixtyPercent.Graph.Nodes, n => n.Kind == NodeKind.Table);
        Assert.False(sixtyPercentTable.Extensions!.ContainsKey("table_overlays"));

        var ninetyFivePercent = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(table + TextBox("11", 620000))));
        var ninetyFivePercentTable = Assert.Single(ninetyFivePercent.Graph.Nodes, n => n.Kind == NodeKind.Table);
        var overlay = Assert.Single(ninetyFivePercentTable.Extensions!["table_overlays"].Deserialize<PptxTableOverlay[]>()!);
        Assert.Equal("label", overlay.Kind);
    }

    [Fact]
    public void MissingGridColWidthAndRowHeightAttributesDistributeSizeEvenlyInsteadOfCollapsingToZero()
    {
        // Neither a:gridCol nor a:tr declares w/h (ParseDouble(null) == 0 for both, so
        // ScaleWidthsToTotal's sum <= 0 branch is exercised on both axes) -- F4: the frame's own
        // declared ext must still be distributed evenly across columns/rows instead of every
        // boundary collapsing onto the same origin point.
        var table =
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"800000\" cy=\"600000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid>" + string.Concat(Enumerable.Repeat("<a:gridCol />", 4)) + "</a:tblGrid>" +
            string.Concat(Enumerable.Repeat("<a:tr>" + string.Concat(Enumerable.Repeat("<a:tc><a:txBody><a:p /></a:txBody></a:tc>", 4)) + "</a:tr>", 3)) +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        // Evenly-distributed bounds: columns [0,200000,400000,600000,800000], rows
        // [0,200000,400000,600000]. This shape sits in column index 2 (400000-600000) of row
        // index 1 (200000-400000) -- the third column of the second row.
        const string shape =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"10\" name=\"Bar\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"400000\" y=\"200000\" /><a:ext cx=\"200000\" cy=\"200000\" /></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst /></a:prstGeom></p:spPr></p:sp>";
        var tableNode = Assert.Single(new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(table + shape))).Graph.Nodes, n => n.Kind == NodeKind.Table);
        var overlay = Assert.Single(tableNode.Extensions!["table_overlays"].Deserialize<PptxTableOverlay[]>()!);
        Assert.Equal((1, 1), (overlay.StartRow, overlay.EndRow));
        Assert.Equal((2, 2), (overlay.StartColumn, overlay.EndColumn));
    }

    [Fact]
    public void GridSpanAndHMergeCellsDoNotShiftOverlayColumnAlignmentThroughTheRealAdapterAndSerializer()
    {
        // F9: drives the REAL adapter and serializer end-to-end (raw XML -> PptxAdapter.Extract ->
        // ReadableMarkdownSerializer.Serialize) to prove an overlay over a:gridCol indices 2-3
        // lands in the right Markdown columns even though an earlier gridSpan="2"/hMerge="1" pair
        // in the same row shrinks that row's physical a:tc/TableCell count from 4 to 3.
        var xml =
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"Schedule\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"400000\" cy=\"400000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid>" + string.Concat(Enumerable.Repeat("<a:gridCol w=\"100000\" />", 4)) + "</a:tblGrid>" +
            "<a:tr h=\"200000\">" +
            "<a:tc><a:txBody><a:p><a:r><a:t>工程</a:t></a:r></a:p></a:txBody></a:tc>" +
            "<a:tc><a:txBody><a:p><a:r><a:t>9/1</a:t></a:r></a:p></a:txBody></a:tc>" +
            "<a:tc><a:txBody><a:p><a:r><a:t>9/2</a:t></a:r></a:p></a:txBody></a:tc>" +
            "<a:tc><a:txBody><a:p><a:r><a:t>9/3</a:t></a:r></a:p></a:txBody></a:tc>" +
            "</a:tr>" +
            "<a:tr h=\"200000\">" +
            "<a:tc gridSpan=\"2\"><a:txBody><a:p><a:r><a:t>結合</a:t></a:r></a:p></a:txBody></a:tc>" +
            "<a:tc hMerge=\"1\"><a:txBody><a:p /></a:txBody></a:tc>" +
            "<a:tc><a:txBody><a:p /></a:txBody></a:tc>" +
            "<a:tc><a:txBody><a:p /></a:txBody></a:tc>" +
            "</a:tr>" +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        // Bar overlay over a:gridCol indices 2-3 (columns 9/2 and 9/3) of the second row --
        // geometry deliberately avoids the merged columns 0-1 so the test isolates the
        // index-alignment question from the merge itself.
        const string bar =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"10\" name=\"Bar\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"200000\" y=\"200000\" /><a:ext cx=\"200000\" cy=\"200000\" /></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst /></a:prstGeom></p:spPr></p:sp>";
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(xml + bar)));
        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);

        Assert.Contains(
            "| 工程 | 9/1 | 9/2 | 9/3 |\n" +
            "| --- | --- | --- | --- |\n" +
            "| 結合 |  | ━━ | ━━ |\n",
            markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ShapeOverlappingTwoTablesIsAssignedOnlyToTheLargerIntersectionTable()
    {
        var tableA =
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"TableA\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\" /><a:ext cx=\"400000\" cy=\"400000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid><a:gridCol w=\"200000\" /><a:gridCol w=\"200000\" /></a:tblGrid>" +
            string.Concat(Enumerable.Repeat("<a:tr h=\"200000\">" + string.Concat(Enumerable.Repeat("<a:tc><a:txBody><a:p /></a:txBody></a:tc>", 2)) + "</a:tr>", 2)) +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        var tableB =
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"4\" name=\"TableB\" /></p:nvGraphicFramePr><p:xfrm><a:off x=\"400000\" y=\"0\" /><a:ext cx=\"800000\" cy=\"400000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid><a:gridCol w=\"400000\" /><a:gridCol w=\"400000\" /></a:tblGrid>" +
            string.Concat(Enumerable.Repeat("<a:tr h=\"200000\">" + string.Concat(Enumerable.Repeat("<a:tc><a:txBody><a:p /></a:txBody></a:tc>", 2)) + "</a:tr>", 2)) +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        // Overlaps table A by only 100,000x200,000 EMU (25% of its own area -- below the 50% gate)
        // and table B by 300,000x200,000 EMU (75% -- comfortably above it): must be assigned to B
        // alone, never to both and never to A.
        const string shape =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"10\" name=\"Bar\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"300000\" y=\"0\" /><a:ext cx=\"400000\" cy=\"200000\" /></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst /></a:prstGeom></p:spPr></p:sp>";
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(tableA + tableB + shape)));

        var tableNodeA = Assert.Single(extraction.Graph.Nodes, n => n.Source?.Locators.Any(l => l.Value == "3") == true);
        Assert.False(tableNodeA.Extensions!.ContainsKey("table_overlays"));
        var tableNodeB = Assert.Single(extraction.Graph.Nodes, n => n.Source?.Locators.Any(l => l.Value == "4") == true);
        var overlay = Assert.Single(tableNodeB.Extensions!["table_overlays"].Deserialize<PptxTableOverlay[]>()!);
        Assert.Equal("10", overlay.ShapeId);
        Assert.Equal((0, 0), (overlay.StartColumn, overlay.EndColumn));
        Assert.Equal((0, 0), (overlay.StartRow, overlay.EndRow));

        var shapeNode = Assert.Single(extraction.Graph.Nodes, n => n.Source?.Locators.Any(l => l.Value == "10") == true);
        Assert.Equal("4", shapeNode.Extensions!["table_overlay_host"].GetString());
    }

    // ---------------------------------------------------------------------------------------
    // P-ShapeGrid: shape-grid table detection (shape-grid-table-spec.md). A grid of adjacent
    // rectangle shapes (header row + optional label column(s) + optional body cells) standing in
    // for a native a:tbl, with the same overlay vocabulary as the table-overlay feature above.
    // Geometry below is generated from the same column/row arithmetic every assertion also uses
    // (Cumulative), so a shape's on-slide position and its "which row/column" answer can never
    // drift apart -- same discipline as SixByThreeScheduleTableXml/print_overlay_assignments in the
    // real schedule-shape-grid.pptx fixture and its generator.
    // ---------------------------------------------------------------------------------------

    private static readonly double[] ShapeGridColWidths = [800_000, 700_000, 900_000, 900_000, 900_000, 900_000, 900_000, 900_000];
    private static readonly double[] ShapeGridRowHeights = [500_000, 600_000, 600_000, 600_000, 600_000, 600_000];
    private static readonly string[] ShapeGridHeaderTexts = ["工程", "担当", "D1", "D2", "D3", "D4", "D5", "D6"];
    private static readonly string[] ShapeGridProcessTexts = ["要件定義", "設計", "実装", "テスト", "リリース"];
    private static readonly string[] ShapeGridOwnerTexts = ["山田", "佐藤", "鈴木", "田中", "全員"];

    private static double[] Cumulative(double[] sizes)
    {
        var result = new double[sizes.Length + 1];
        for (var i = 0; i < sizes.Length; i++) result[i + 1] = result[i] + sizes[i];
        return result;
    }

    private static string ShapeGridRect(string id, string name, double x, double y, double width, double height,
        string text = "", string preset = "rect", bool textBox = false) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\" />{(textBox ? "<p:cNvSpPr txBox=\"1\" />" : "")}</p:nvSpPr>" +
        $"<p:spPr><a:xfrm><a:off x=\"{(long)x}\" y=\"{(long)y}\" /><a:ext cx=\"{(long)width}\" cy=\"{(long)height}\" /></a:xfrm>" +
        $"<a:prstGeom prst=\"{preset}\"><a:avLst /></a:prstGeom></p:spPr>" +
        (text.Length > 0 ? $"<p:txBody><a:bodyPr /><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:txBody>" : "") + "</p:sp>";

    private static string ShapeGridVerticalConnector(string id, string name, double x, double y0, double y1) =>
        $"<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"{id}\" name=\"{name}\" /></p:nvCxnSpPr>" +
        $"<p:spPr><a:xfrm><a:off x=\"{(long)x}\" y=\"{(long)y0}\" /><a:ext cx=\"0\" cy=\"{(long)(y1 - y0)}\" /></a:xfrm>" +
        "<a:ln><a:tailEnd type=\"triangle\" /></a:ln></p:spPr></p:cxnSp>";

    // 8 header rects (工程, 担当, D1..D6) flush across ShapeGridColWidths, plus (when requested) 5
    // process-label rects under 工程 and 5 owner-label rects under 担当, flush down ShapeGridRowHeights[1..].
    private static string ShapeGridHeaderAndLabelsXml(bool includeLabels = true)
    {
        var colLeft = Cumulative(ShapeGridColWidths);
        var rowTop = Cumulative(ShapeGridRowHeights);
        var xml = new StringBuilder();
        for (var c = 0; c < ShapeGridHeaderTexts.Length; c++)
            xml.Append(ShapeGridRect($"h{c}", $"GridHeader-{c}", colLeft[c], rowTop[0], ShapeGridColWidths[c], ShapeGridRowHeights[0], ShapeGridHeaderTexts[c]));
        if (includeLabels)
        {
            for (var r = 0; r < ShapeGridProcessTexts.Length; r++)
                xml.Append(ShapeGridRect($"p{r}", $"GridLabelProcess-{r}", colLeft[0], rowTop[r + 1], ShapeGridColWidths[0], ShapeGridRowHeights[r + 1], ShapeGridProcessTexts[r]));
            for (var r = 0; r < ShapeGridOwnerTexts.Length; r++)
                xml.Append(ShapeGridRect($"o{r}", $"GridLabelOwner-{r}", colLeft[1], rowTop[r + 1], ShapeGridColWidths[1], ShapeGridRowHeights[r + 1], ShapeGridOwnerTexts[r]));
        }
        return xml.ToString();
    }

    [Fact]
    public void ShapeGrid_header_and_two_label_columns_with_five_overlay_kinds_synthesizes_the_expected_table()
    {
        var colLeft = Cumulative(ShapeGridColWidths);
        var rowTop = Cumulative(ShapeGridRowHeights);
        var headerAndLabels = ShapeGridHeaderAndLabelsXml();

        // Right arrow "要件定義": row1 (要件定義), columns 2-3 (D1-D2) -- flush across both columns.
        var arrow = ShapeGridRect("10", "Overlay-Arrow", colLeft[2], rowTop[1], colLeft[4] - colLeft[2], ShapeGridRowHeights[1], "要件定義", "rightArrow");
        // Textless bar: row3 (実装), columns 4-6 (D3-D5) -- deliberately INSET (30,000 EMU each
        // horizontal edge, 60% of the row's own height, vertically centred) so it does NOT tile a
        // whole cell -- IsFlushGridMember must reject it as a member; it must resolve as an overlay.
        var barWidth = colLeft[7] - colLeft[4] - 60_000;
        var barHeight = ShapeGridRowHeights[3] * 0.6;
        var bar = ShapeGridRect("11", "Overlay-Bar", colLeft[4] + 30_000, rowTop[3] + (ShapeGridRowHeights[3] - barHeight) / 2, barWidth, barHeight, preset: "rect");
        // Diamond marker: row5 (リリース), column 7 (D6), centred in its cell.
        var diamondCenterX = colLeft[7] + ShapeGridColWidths[7] / 2;
        var diamondCenterY = rowTop[5] + ShapeGridRowHeights[5] / 2;
        var diamond = ShapeGridRect("12", "Overlay-Diamond", diamondCenterX - 100_000, diamondCenterY - 100_000, 200_000, 200_000, preset: "diamond");
        // Vertical connector (a "today line"): column 4 (D3), spanning every row INCLUDING the
        // header, tailEnd-only (no headEnd) -- promoted to arrow/down, same edge case as the real
        // schedule-arrows/schedule-shape-grid today-line connectors.
        var connector = ShapeGridVerticalConnector("13", "Overlay-TodayLine", colLeft[4] + ShapeGridColWidths[4] / 2, rowTop[0], rowTop[6]);
        // Text box label "▲レビュー": row2 (設計), column 6 (D5) -- inset like a real label textbox.
        var label = ShapeGridRect("14", "Overlay-Label", colLeft[6] + 50_000, rowTop[2] + 50_000, ShapeGridColWidths[6] - 100_000, ShapeGridRowHeights[2] - 100_000, "▲レビュー", textBox: true);
        // Flush body-cell text: row4 (テスト), column 2 (D1) -- exactly tiles its cell, so it is a
        // grid MEMBER (not an overlay) and its text lands directly in that cell (spec step 7).
        var bodyText = ShapeGridRect("20", "GridBody-r4c2", colLeft[2], rowTop[4], ShapeGridColWidths[2], ShapeGridRowHeights[4], "済");

        var extraction = new PptxAdapter().Extract(new MemoryStream(
            CreateTableOverlayPackage(headerAndLabels + arrow + bar + diamond + connector + label + bodyText)));

        var tableNode = Assert.Single(extraction.Graph.Nodes, node => node.Kind == NodeKind.Table);
        Assert.Equal("grid:h0", tableNode.Extensions!["shape_id"].GetString());
        Assert.True(tableNode.Extensions!["shape_grid_table"].GetBoolean());
        Assert.Equal(ContentLayer.Derived, tableNode.Layer);
        Assert.Equal(NodeEditability.Protected, tableNode.Editability);

        var table = Assert.IsType<TableNodeContent>(tableNode.Content);
        Assert.Equal(6, table.Rows.Count);
        Assert.Equal(ShapeGridHeaderTexts, table.Rows[0].Select(cell => cell.Text).ToArray());
        Assert.Equal(new[] { "要件定義", "山田", "", "", "", "", "", "" }, table.Rows[1].Select(cell => cell.Text).ToArray());
        Assert.Equal(new[] { "設計", "佐藤", "", "", "", "", "", "" }, table.Rows[2].Select(cell => cell.Text).ToArray());
        Assert.Equal(new[] { "実装", "鈴木", "", "", "", "", "", "" }, table.Rows[3].Select(cell => cell.Text).ToArray());
        // The flush body rect's "済" lands at (row4=テスト, column2=D1), not folded into an overlay.
        Assert.Equal(new[] { "テスト", "田中", "済", "", "", "", "", "" }, table.Rows[4].Select(cell => cell.Text).ToArray());
        Assert.Equal(new[] { "リリース", "全員", "", "", "", "", "", "" }, table.Rows[5].Select(cell => cell.Text).ToArray());

        var overlays = tableNode.Extensions!["table_overlays"].Deserialize<PptxTableOverlay[]>()!;
        Assert.Equal(["13", "10", "14", "11", "12"], overlays.Select(o => o.ShapeId).ToArray());
        var connectorOverlay = overlays.Single(o => o.ShapeId == "13");
        Assert.Equal(("arrow", "down", "vertical", 0, 5, 4, 4), (connectorOverlay.Kind, connectorOverlay.Direction, connectorOverlay.Axis, connectorOverlay.StartRow, connectorOverlay.EndRow, connectorOverlay.StartColumn, connectorOverlay.EndColumn));
        var arrowOverlay = overlays.Single(o => o.ShapeId == "10");
        Assert.Equal(("arrow", "right", "horizontal", 1, 1, 2, 3), (arrowOverlay.Kind, arrowOverlay.Direction, arrowOverlay.Axis, arrowOverlay.StartRow, arrowOverlay.EndRow, arrowOverlay.StartColumn, arrowOverlay.EndColumn));
        Assert.Equal("要件定義", arrowOverlay.Text);
        var labelOverlay = overlays.Single(o => o.ShapeId == "14");
        Assert.Equal(("label", "none", "horizontal", 2, 2, 6, 6), (labelOverlay.Kind, labelOverlay.Direction, labelOverlay.Axis, labelOverlay.StartRow, labelOverlay.EndRow, labelOverlay.StartColumn, labelOverlay.EndColumn));
        var barOverlay = overlays.Single(o => o.ShapeId == "11");
        Assert.Equal(("bar", "none", "horizontal", 3, 3, 4, 6), (barOverlay.Kind, barOverlay.Direction, barOverlay.Axis, barOverlay.StartRow, barOverlay.EndRow, barOverlay.StartColumn, barOverlay.EndColumn));
        var diamondOverlay = overlays.Single(o => o.ShapeId == "12");
        Assert.Equal(("marker", "none", "horizontal", 5, 5, 7, 7), (diamondOverlay.Kind, diamondOverlay.Direction, diamondOverlay.Axis, diamondOverlay.StartRow, diamondOverlay.EndRow, diamondOverlay.StartColumn, diamondOverlay.EndColumn));

        // Every header/label/flush-body-text shape is tagged as a member of this grid table...
        foreach (var memberId in new[] { "h0", "h1", "h2", "h3", "h4", "h5", "h6", "h7", "p0", "p1", "p2", "p3", "p4", "o0", "o1", "o2", "o3", "o4", "20" })
        {
            var node = Assert.Single(extraction.Graph.Nodes, n => n.Source?.Locators.Any(l => l.Value == memberId) == true);
            Assert.Equal("grid:h0", node.Extensions!["table_grid_member_host"].GetString());
        }
        // ...while every overlay shape is tagged with the SAME table_overlay_host/table_overlay
        // extensions a native table's own overlays would carry (shared mechanism, shared markup).
        foreach (var overlayId in new[] { "10", "11", "12", "13", "14" })
        {
            var node = Assert.Single(extraction.Graph.Nodes, n => n.Source?.Locators.Any(l => l.Value == overlayId) == true);
            Assert.Equal("grid:h0", node.Extensions!["table_overlay_host"].GetString());
            Assert.True(node.Extensions!["table_overlay"].GetBoolean());
        }

        // synthesized_from_shapes: header (X order), label column (Y order: 工程), then remaining
        // body members (Y then X: 担当 column, interleaved with the flush body-text rect -- "20"
        // shares row4's Y with "o3" but sits at a larger X, so it sorts immediately after "o3" and
        // before "o4").
        var expectedMembers = new[] { "h0", "h1", "h2", "h3", "h4", "h5", "h6", "h7", "p0", "p1", "p2", "p3", "p4", "o0", "o1", "o2", "o3", "20", "o4" };
        Assert.Equal(expectedMembers, tableNode.Extensions!["synthesized_from_shapes"].Deserialize<string[]>());

        // Every directional/overlay shape on the slide is either a grid member or a grid overlay --
        // none remain to feed visual-flow inference, so no phantom "Visual flow" diagram appears.
        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Kind == NodeKind.Diagram);
    }

    [Fact]
    public void ShapeGrid_without_a_label_column_derives_rows_from_overlay_y_band_clustering()
    {
        // 6 date-only header columns (no 工程/担当), 900,000 EMU wide each, no label rects at all --
        // rows must come purely from clustering the 3 arrows' + 1 diamond's Y centres (spec step 6).
        double[] colWidths = [900_000, 900_000, 900_000, 900_000, 900_000, 900_000];
        var colLeft = Cumulative(colWidths);
        const double headerHeight = 500_000;
        string[] headers = ["D1", "D2", "D3", "D4", "D5", "D6"];
        var header = new StringBuilder();
        for (var c = 0; c < headers.Length; c++)
            header.Append(ShapeGridRect($"h{c}", $"GridHeader-{c}", colLeft[c], 0, colWidths[c], headerHeight, headers[c]));

        // Overlay bars: 360,000 EMU tall, centred on Y = 800,000 / 1,400,000 / 2,000,000 (600,000
        // apart, directly below the header) -- same spacing convention as schedule-shape-grid.pptx
        // slide 4's Y bands.
        var arrow1 = ShapeGridRect("20", "Overlay-Arrow1", colLeft[0], 620_000, colLeft[2] - colLeft[0], 360_000, "要件定義", "rightArrow");
        var arrow2 = ShapeGridRect("21", "Overlay-Arrow2", colLeft[1], 1_220_000, colLeft[4] - colLeft[1], 360_000, "設計", "rightArrow");
        var arrow3 = ShapeGridRect("22", "Overlay-Arrow3", colLeft[2], 1_820_000, colLeft[5] - colLeft[2], 360_000, "実装", "rightArrow");
        var diamond = ShapeGridRect("23", "Overlay-Diamond", colLeft[5] + colWidths[5] / 2 - 100_000, 2_500_000, 200_000, 200_000, preset: "diamond");

        var extraction = new PptxAdapter().Extract(new MemoryStream(
            CreateTableOverlayPackage(header.ToString() + arrow1 + arrow2 + arrow3 + diamond)));

        var tableNode = Assert.Single(extraction.Graph.Nodes, node => node.Kind == NodeKind.Table);
        var table = Assert.IsType<TableNodeContent>(tableNode.Content);
        Assert.Equal(5, table.Rows.Count); // header + 4 overlay-derived rows
        Assert.Equal(headers, table.Rows[0].Select(cell => cell.Text).ToArray());
        // No label column: every data row starts blank (spec: "先頭に空セルすら無い").
        Assert.All(table.Rows.Skip(1), row => Assert.All(row, cell => Assert.Equal("", cell.Text)));

        var overlays = tableNode.Extensions!["table_overlays"].Deserialize<PptxTableOverlay[]>()!;
        var arrow1Overlay = overlays.Single(o => o.ShapeId == "20");
        Assert.Equal((1, 1, 0, 1), (arrow1Overlay.StartRow, arrow1Overlay.EndRow, arrow1Overlay.StartColumn, arrow1Overlay.EndColumn));
        var arrow2Overlay = overlays.Single(o => o.ShapeId == "21");
        Assert.Equal((2, 2, 1, 3), (arrow2Overlay.StartRow, arrow2Overlay.EndRow, arrow2Overlay.StartColumn, arrow2Overlay.EndColumn));
        var arrow3Overlay = overlays.Single(o => o.ShapeId == "22");
        Assert.Equal((3, 3, 2, 4), (arrow3Overlay.StartRow, arrow3Overlay.EndRow, arrow3Overlay.StartColumn, arrow3Overlay.EndColumn));
        var diamondOverlay = overlays.Single(o => o.ShapeId == "23");
        Assert.Equal((4, 4, 5, 5), (diamondOverlay.StartRow, diamondOverlay.EndRow, diamondOverlay.StartColumn, diamondOverlay.EndColumn));
    }

    [Fact]
    public void ShapeGrid_aligned_card_layout_without_any_overlay_is_not_synthesized_into_a_table()
    {
        // A 2x3 grid of aligned roundRect "cards", flush and adjacent, but with NO overlay/arrow/
        // marker anywhere -- guard rail 8b ("少なくとも1つのオーバーレイが本体領域に存在する") must
        // reject this as a table, leaving it as ordinary shape nodes (a card layout, not a schedule).
        const double cardWidth = 900_000; const double cardHeight = 500_000; const double gap = 100_000;
        var xml = new StringBuilder();
        for (var i = 0; i < 6; i++)
        {
            var (r, c) = (i / 3, i % 3);
            xml.Append(ShapeGridRect($"card{i}", $"Card-{i}", c * (cardWidth + gap), r * (cardHeight + gap), cardWidth, cardHeight, $"機能{i}", "roundRect"));
        }
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(xml.ToString())));

        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Kind == NodeKind.Table);
        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Extensions?.ContainsKey("table_grid_member_host") == true);
    }

    [Fact]
    public void ShapeGrid_with_only_two_header_candidates_is_not_synthesized()
    {
        var xml = ShapeGridRect("1", "GridHeader-0", 0, 0, 800_000, 500_000, "工程") +
                  ShapeGridRect("2", "GridHeader-1", 800_000, 0, 900_000, 500_000, "D1");
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(xml)));

        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Kind == NodeKind.Table);
    }

    [Fact]
    public void ShapeGrid_overlapping_a_native_table_by_at_least_half_its_own_area_is_not_synthesized()
    {
        // A 3-column shape-grid header + one overlay, positioned entirely inside a native 6x3
        // schedule table's own frame (guard rail 8c): the grid's own area is 100% covered by the
        // native table, well past the 50% threshold, so no second, redundant table is synthesized.
        var nativeTable = SixByThreeScheduleTableXml(); // X=0..7,200,000, Y=0..1,200,000 (see its own comment)
        var header = ShapeGridRect("h0", "GridHeader-0", 0, 0, 700_000, 400_000, "H0") +
                     ShapeGridRect("h1", "GridHeader-1", 700_000, 0, 700_000, 400_000, "H1") +
                     ShapeGridRect("h2", "GridHeader-2", 1_400_000, 0, 700_000, 400_000, "H2");
        var overlay = ShapeGridRect("50", "Overlay-Arrow", 0, 600_000, 1_400_000, 200_000, "X", "rightArrow");

        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(nativeTable + header + overlay)));

        // The native table itself is unaffected; no shape-grid table is layered on top of it.
        Assert.Single(extraction.Graph.Nodes, node => node.Kind == NodeKind.Table);
        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Extensions?.ContainsKey("shape_grid_table") == true);
    }

    [Fact]
    public void ShapeGrid_detection_does_not_interfere_with_a_genuinely_connected_flow_elsewhere_on_the_slide()
    {
        // A valid, minimal shape-grid table (3 header columns, rows from overlay Y-band
        // clustering) far to the left, plus a completely separate, natively-connected two-shape
        // flow (real a:stCxn/a:endCxn, like schedule-shape-grid.pptx slide 3's 開始→完了) far to
        // the right -- detecting/excluding the grid's own shapes must never swallow the unrelated
        // flow's connector or its endpoints. Two overlays (G1 fix 2: a label-less grid now needs
        // >= 2 derived body rows) sit on distinct Y bands well within the 2x-median-height cutoff
        // (G1 fix 3b) of one another.
        var header = ShapeGridRect("h0", "GridHeader-0", 0, 0, 700_000, 400_000, "H0") +
                     ShapeGridRect("h1", "GridHeader-1", 700_000, 0, 700_000, 400_000, "H1") +
                     ShapeGridRect("h2", "GridHeader-2", 1_400_000, 0, 700_000, 400_000, "H2");
        var overlay = ShapeGridRect("50", "Overlay-Arrow", 0, 600_000, 1_400_000, 200_000, "X", "rightArrow");
        var overlay2 = ShapeGridRect("51", "Overlay-Arrow2", 700_000, 900_000, 700_000, 200_000, "Y", "rightArrow");

        const string start = "<p:sp><p:nvSpPr><p:cNvPr id=\"60\" name=\"Start\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"9000000\" y=\"0\" /><a:ext cx=\"900000\" cy=\"400000\" /></a:xfrm><a:prstGeom prst=\"roundRect\"><a:avLst /></a:prstGeom></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>開始</a:t></a:r></a:p></p:txBody></p:sp>";
        const string end = "<p:sp><p:nvSpPr><p:cNvPr id=\"61\" name=\"End\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"10500000\" y=\"0\" /><a:ext cx=\"900000\" cy=\"400000\" /></a:xfrm><a:prstGeom prst=\"roundRect\"><a:avLst /></a:prstGeom></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>完了</a:t></a:r></a:p></p:txBody></p:sp>";
        const string connector = "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"62\" name=\"Flow\" /><p:cNvCxnSpPr><a:stCxn id=\"60\" idx=\"1\" /><a:endCxn id=\"61\" idx=\"3\" /></p:cNvCxnSpPr></p:nvCxnSpPr><p:spPr><a:xfrm><a:off x=\"9900000\" y=\"200000\" /><a:ext cx=\"600000\" cy=\"0\" /></a:xfrm><a:ln><a:tailEnd type=\"triangle\" /></a:ln></p:spPr></p:cxnSp>";

        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(header + overlay + overlay2 + start + end + connector)));

        var tableNode = Assert.Single(extraction.Graph.Nodes, node => node.Kind == NodeKind.Table);
        Assert.True(tableNode.Extensions!["shape_grid_table"].GetBoolean());
        var diagram = Assert.Single(extraction.Graph.Nodes, node => node.Kind == NodeKind.Diagram);
        var graph = diagram.Extensions!["visual_graph"].Deserialize<VisualGraph>()!;
        var edge = Assert.Single(graph.Edges);
        var startNode = graph.Nodes.Single(n => n.SourceNodeId == "60");
        var endNode = graph.Nodes.Single(n => n.SourceNodeId == "61");
        Assert.Equal(startNode.Id, edge.SourceId);
        Assert.Equal(endNode.Id, edge.TargetId);
    }

    [Fact]
    public void ShapeGrid_gapped_card_layout_with_caption_is_not_synthesized_due_to_header_adjacency()
    {
        // G4: header adjacency tightened from widthMedian*0.5 to widthMedian*0.1 -- a 3x3 card
        // grid with an ~11% gap between cards (previously "adjacent enough" by accident under the
        // old, looser gate) must now fail the header-row check outright, before guard rail (b)
        // even gets a chance to run. A caption textbox sitting below the cards (common on a real
        // card layout) must not change that outcome, nor be swallowed by anything.
        const double cardWidth = 900_000; const double cardHeight = 500_000; const double gap = 100_000; // ~11.1% of cardWidth, safely above widthMedian*0.1 (90,000 EMU)
        var xml = new StringBuilder();
        for (var i = 0; i < 9; i++)
        {
            var (r, c) = (i / 3, i % 3);
            xml.Append(ShapeGridRect($"card{i}", $"Card-{i}", c * (cardWidth + gap), r * (cardHeight + gap), cardWidth, cardHeight, $"機能{i}", "roundRect"));
        }
        const string caption = "<p:sp><p:nvSpPr><p:cNvPr id=\"90\" name=\"Caption\" /><p:cNvSpPr txBox=\"1\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"1900000\" /><a:ext cx=\"2900000\" cy=\"300000\" /></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst /></a:prstGeom></p:spPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>主要機能一覧</a:t></a:r></a:p></p:txBody></p:sp>";

        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(xml.ToString() + caption)));

        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Extensions?.ContainsKey("shape_grid_table") == true);
        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Extensions?.ContainsKey("table_grid_member_host") == true);
        Assert.Contains(extraction.Graph.Nodes, n => n.Source?.Locators.Any(l => l.Value == "90") == true);
    }

    [Fact]
    public void ShapeGrid_non_member_label_is_not_double_registered_in_synthesized_from_shapes()
    {
        // G5: `synthesized_from_shapes` must list EXACTLY the shapes tagged table_grid_member_host.
        // The middle process label ("B") is deliberately shrunk to 50% of its row's own height
        // (vertically centred, so it never overflows its cell) -- it still helps derive the row
        // boundaries themselves (G3 uses every primaryLabel member's own Top/Bottom regardless of
        // whether that member later turns out to be a flush "member"), but fails IsFlushGridMember's
        // 85% coverage gate (G2) and so must never appear in synthesized_from_shapes nor carry
        // table_grid_member_host.
        var header = ShapeGridRect("h0", "GridHeader-0", 0, 0, 900_000, 500_000, "工程") +
                     ShapeGridRect("h1", "GridHeader-1", 900_000, 0, 900_000, 500_000, "D1") +
                     ShapeGridRect("h2", "GridHeader-2", 1_800_000, 0, 900_000, 500_000, "D2");
        var labelA = ShapeGridRect("la", "GridLabel-A", 0, 500_000, 900_000, 600_000, "A");
        var labelB = ShapeGridRect("lb", "GridLabel-B", 0, 1_250_000, 900_000, 300_000, "B");
        var labelC = ShapeGridRect("lc", "GridLabel-C", 0, 1_700_000, 900_000, 600_000, "C");
        var overlay = ShapeGridRect("50", "Overlay-Arrow", 900_000, 500_000, 1_800_000, 400_000, "X", "rightArrow");

        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(header + labelA + labelB + labelC + overlay)));

        var tableNode = Assert.Single(extraction.Graph.Nodes, node => node.Kind == NodeKind.Table);
        var table = Assert.IsType<TableNodeContent>(tableNode.Content);
        Assert.Equal(4, table.Rows.Count); // header + A + B + C
        Assert.Equal("A", table.Rows[1][0].Text);
        // "B" was excluded from membership, so PlaceMember never ran for it -- its row's own label
        // cell stays blank rather than showing "B".
        Assert.Equal("", table.Rows[2][0].Text);
        Assert.Equal("C", table.Rows[3][0].Text);

        var members = tableNode.Extensions!["synthesized_from_shapes"].Deserialize<string[]>()!;
        Assert.DoesNotContain("lb", members);
        Assert.Equal(members.Length, members.Distinct(StringComparer.Ordinal).Count()); // no double registration

        // Every id `synthesized_from_shapes` lists carries a REAL table_grid_member_host tag
        // pointing at this same grid -- the two sets can never drift apart (G5's guarantee).
        var gridId = tableNode.Extensions!["shape_id"].GetString();
        foreach (var memberId in members)
        {
            var node = Assert.Single(extraction.Graph.Nodes, n => n.Source?.Locators.Any(l => l.Value == memberId) == true);
            Assert.Equal(gridId, node.Extensions!["table_grid_member_host"].GetString());
        }

        // "B" itself never became a member -- it must not carry the suppression tag either.
        var labelBNode = Assert.Single(extraction.Graph.Nodes, n => n.Source?.Locators.Any(l => l.Value == "lb") == true);
        Assert.False(labelBNode.Extensions!.ContainsKey("table_grid_member_host"));
    }

    [Fact]
    public void ShapeGrid_textless_flush_rect_spanning_three_columns_becomes_a_bar_overlay_not_a_member()
    {
        // G8: a TEXTLESS box that flush-tiles >= 2 columns at once (here 3) is body-area "bar"
        // overlay content, not a single grid member -- membership would place its (nonexistent)
        // text in just ONE cell, silently dropping the multi-column visual signal a real
        // "━━ ━━ ━━" bar needs to convey, and could even starve guard rail (b) of its one
        // required overlay.
        var header = ShapeGridRect("h0", "GridHeader-0", 0, 0, 900_000, 500_000, "工程") +
                     ShapeGridRect("h1", "GridHeader-1", 900_000, 0, 900_000, 500_000, "H1") +
                     ShapeGridRect("h2", "GridHeader-2", 1_800_000, 0, 900_000, 500_000, "H2") +
                     ShapeGridRect("h3", "GridHeader-3", 2_700_000, 0, 900_000, 500_000, "H3");
        var labelR1 = ShapeGridRect("r1", "GridLabel-R1", 0, 500_000, 900_000, 600_000, "R1");
        var labelR2 = ShapeGridRect("r2", "GridLabel-R2", 0, 1_100_000, 900_000, 600_000, "R2");
        // Flush across H1/H2/H3 (columns 1-3), textless.
        var bar = ShapeGridRect("40", "Overlay-Bar", 900_000, 500_000, 2_700_000, 600_000, preset: "rect");

        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(header + labelR1 + labelR2 + bar)));

        var tableNode = Assert.Single(extraction.Graph.Nodes, node => node.Kind == NodeKind.Table);
        var table = Assert.IsType<TableNodeContent>(tableNode.Content);
        Assert.Equal(new[] { "R1", "", "", "" }, table.Rows[1].Select(cell => cell.Text).ToArray());

        var overlays = tableNode.Extensions!["table_overlays"].Deserialize<PptxTableOverlay[]>()!;
        var barOverlay = Assert.Single(overlays);
        Assert.Equal(("bar", 1, 1, 1, 3), (barOverlay.Kind, barOverlay.StartRow, barOverlay.EndRow, barOverlay.StartColumn, barOverlay.EndColumn));
        var barNode = Assert.Single(extraction.Graph.Nodes, n => n.Source?.Locators.Any(l => l.Value == "40") == true);
        Assert.False(barNode.Extensions!.ContainsKey("table_grid_member_host"));
        Assert.True(barNode.Extensions!.ContainsKey("table_overlay_host"));

        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);
        Assert.Contains("| R1 | ━━ | ━━ | ━━ |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ShapeGrid_picture_fill_shape_is_excluded_from_box_candidates_even_with_a_box_preset()
    {
        // G9: mirrors ClassifyOverlayCandidates' own media/diagram exclusion -- a shape whose FILL
        // is a picture (embedded directly on a plain p:sp via a:blipFill, not via a separate
        // p:pic) must never become a grid header/label/body member just because its own outline
        // uses a "rect" preset. Without this gate, this photo placeholder would complete a valid
        // 3-member header row; with it, only 2 real candidates remain and no grid is synthesized.
        var h0 = ShapeGridRect("h0", "GridHeader-0", 0, 0, 900_000, 500_000, "H0");
        const string hImg = "<p:sp><p:nvSpPr><p:cNvPr id=\"h1\" name=\"GridHeader-1-Photo\" /></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"900000\" y=\"0\" /><a:ext cx=\"900000\" cy=\"500000\" /></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst /></a:prstGeom><a:blipFill><a:blip r:embed=\"rIdPhoto\" /></a:blipFill></p:spPr></p:sp>";
        var h2 = ShapeGridRect("h2", "GridHeader-2", 1_800_000, 0, 900_000, 500_000, "H2");

        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(h0 + hImg + h2)));

        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Extensions?.ContainsKey("shape_grid_table") == true);
    }

    [Fact]
    public void ShapeGrid_bounding_box_overlapping_native_table_area_is_rejected_even_though_no_member_shape_individually_overlaps_it()
    {
        // G10 test rework: a DIFFERENT rejection path than
        // ShapeGrid_overlapping_a_native_table_by_at_least_half_its_own_area_is_not_synthesized
        // above -- there, every grid shape sits INSIDE the native table and gets individually
        // absorbed as one of its overlays before shape-grid detection even runs. Here, every
        // header/label shape sits entirely OUTSIDE the native table (zero individual overlap, so
        // none of them is ever claimed as a native-table overlay), yet the GRID's own overall
        // bounding rectangle -- header width x (header height + both label rows) -- still overlaps
        // the table by more than 50% of the GRID's own area, purely because two of its three
        // columns (D1/D2, with no actual shape dipping below the header there) sit directly above
        // where the table extends down to. Guard rail (c) (HostOverlapRatioOfFirst) must catch
        // this on the abstract bounding box alone.
        const string nativeTable =
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"90\" name=\"NativeTable\" /></p:nvGraphicFramePr>" +
            "<p:xfrm><a:off x=\"700000\" y=\"500000\" /><a:ext cx=\"1400000\" cy=\"2500000\" /></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid><a:gridCol w=\"700000\" /><a:gridCol w=\"700000\" /></a:tblGrid>" +
            "<a:tr h=\"1250000\"><a:tc><a:txBody><a:p /></a:txBody></a:tc><a:tc><a:txBody><a:p /></a:txBody></a:tc></a:tr>" +
            "<a:tr h=\"1250000\"><a:tc><a:txBody><a:p /></a:txBody></a:tc><a:tc><a:txBody><a:p /></a:txBody></a:tc></a:tr>" +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        var header = ShapeGridRect("h0", "GridHeader-0", 0, 0, 700_000, 500_000, "工程") +
                     ShapeGridRect("h1", "GridHeader-1", 700_000, 0, 700_000, 500_000, "D1") +
                     ShapeGridRect("h2", "GridHeader-2", 1_400_000, 0, 700_000, 500_000, "D2");
        var labelR1 = ShapeGridRect("r1", "GridLabel-R1", 0, 500_000, 700_000, 1_000_000, "行1");
        var labelR2 = ShapeGridRect("r2", "GridLabel-R2", 0, 1_500_000, 700_000, 1_000_000, "行2");

        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(nativeTable + header + labelR1 + labelR2)));

        // The native table itself is unaffected; no shape-grid table is layered over/under it.
        Assert.Single(extraction.Graph.Nodes, node => node.Kind == NodeKind.Table);
        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Extensions?.ContainsKey("shape_grid_table") == true);
        // None of the header/label shapes were individually absorbed as a native-table overlay
        // either -- they were rejected by guard rail (c), not by ClassifyOverlayCandidates.
        foreach (var id in new[] { "h0", "h1", "h2", "r1", "r2" })
        {
            var node = Assert.Single(extraction.Graph.Nodes, n => n.Source?.Locators.Any(l => l.Value == id) == true);
            Assert.False(node.Extensions!.ContainsKey("table_overlay_host"));
            Assert.False(node.Extensions!.ContainsKey("table_grid_member_host"));
        }
    }

    [Fact]
    public void ShapeGrid_label_column_present_flush_grid_without_any_overlay_is_not_synthesized()
    {
        // G10 test rework: the EXISTING "aligned card layout" negative test
        // (ShapeGrid_aligned_card_layout_without_any_overlay_is_not_synthesized_into_a_table) has
        // no label column at all. This covers the OTHER branch through DetectShapeGridTables -- a
        // label column IS present (rows come from G3's label-boundary derivation, not from
        // clustering overlay Y bands), the grid is perfectly flush (zero gaps), but there is still
        // no arrow/bar/marker/line anywhere in the body -- guard rail (b) must reject it regardless
        // of which row-derivation path was taken.
        var header = ShapeGridRect("h0", "GridHeader-0", 0, 0, 700_000, 500_000, "工程") +
                     ShapeGridRect("h1", "GridHeader-1", 700_000, 0, 700_000, 500_000, "D1") +
                     ShapeGridRect("h2", "GridHeader-2", 1_400_000, 0, 700_000, 500_000, "D2");
        var labelR1 = ShapeGridRect("r1", "GridLabel-R1", 0, 500_000, 700_000, 600_000, "行1");
        var labelR2 = ShapeGridRect("r2", "GridLabel-R2", 0, 1_100_000, 700_000, 600_000, "行2");

        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(header + labelR1 + labelR2)));

        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Extensions?.ContainsKey("shape_grid_table") == true);
        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Extensions?.ContainsKey("table_grid_member_host") == true);
    }

    [Fact]
    public void ShapeGrid_four_candidates_split_across_two_row_bands_never_forms_a_header()
    {
        // G10 test rework: pins down the "try every row band with >= 3 members" header-selection
        // logic's own boundary case -- 4 boxes that LOOK like they could be one 4-column header,
        // but happen to fall into two separate Y bands of 2 members each (neither reaches the
        // required minimum of 3), so no band is ever even a CANDIDATE for the header-row role and
        // detection correctly finds no grid at all (rather than merging the two thin bands or
        // otherwise misbehaving).
        var boxes = ShapeGridRect("b0", "Box-0", 0, 0, 700_000, 400_000, "B0") +
                    ShapeGridRect("b1", "Box-1", 700_000, 0, 700_000, 400_000, "B1") +
                    ShapeGridRect("b2", "Box-2", 1_400_000, 200_000, 700_000, 400_000, "B2") +
                    ShapeGridRect("b3", "Box-3", 2_100_000, 200_000, 700_000, 400_000, "B3");

        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(boxes)));

        Assert.DoesNotContain(extraction.Graph.Nodes, node => node.Extensions?.ContainsKey("shape_grid_table") == true);
    }

    [Fact]
    public void ShapeGrid_leading_label_column_gets_a_synthetic_column_with_an_empty_header_cell()
    {
        // G10 test rework: the `hasLeadingLabelColumn` branch (spec step 5) -- unlike every other
        // fixture/test in this file, the header here has NO "工程"-equivalent cell at all (just 3
        // plain date columns); the label column sits strictly to the LEFT of the header's own
        // leftmost column, so it gets its own synthetic leading column boundary, and the header
        // row gets an empty first cell.
        var header = ShapeGridRect("h0", "GridHeader-0", 700_000, 0, 700_000, 500_000, "D1") +
                     ShapeGridRect("h1", "GridHeader-1", 1_400_000, 0, 700_000, 500_000, "D2") +
                     ShapeGridRect("h2", "GridHeader-2", 2_100_000, 0, 700_000, 500_000, "D3");
        var labelR1 = ShapeGridRect("r1", "GridLabel-R1", 0, 500_000, 700_000, 600_000, "行1");
        var labelR2 = ShapeGridRect("r2", "GridLabel-R2", 0, 1_100_000, 700_000, 600_000, "行2");
        // Right arrow covering D1-D2 (columns 1-2 once the synthetic leading column is column 0), row1.
        var overlay = ShapeGridRect("50", "Overlay-Arrow", 700_000, 500_000, 1_400_000, 400_000, "X", "rightArrow");

        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateTableOverlayPackage(header + labelR1 + labelR2 + overlay)));

        var tableNode = Assert.Single(extraction.Graph.Nodes, node => node.Kind == NodeKind.Table);
        var table = Assert.IsType<TableNodeContent>(tableNode.Content);
        Assert.Equal(new[] { "", "D1", "D2", "D3" }, table.Rows[0].Select(cell => cell.Text).ToArray());
        Assert.Equal("行1", table.Rows[1][0].Text);
        Assert.Equal("行2", table.Rows[2][0].Text);

        var overlay50 = Assert.Single(tableNode.Extensions!["table_overlays"].Deserialize<PptxTableOverlay[]>()!);
        Assert.Equal((1, 1, 1, 2), (overlay50.StartRow, overlay50.EndRow, overlay50.StartColumn, overlay50.EndColumn));
    }
}
