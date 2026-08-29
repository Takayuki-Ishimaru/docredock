using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Pptx;
using DocRedock.Markdown;

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
        Assert.Contains("v_100 -->|YES| v_101", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(extraction.Warnings, warning => warning.StartsWith("VisualConnectorUnresolved", StringComparison.Ordinal));
    }

    [Fact]
    public void MermaidEscapesQuotedEdgeLabels()
    {
        var entries = Entries(CreateGeometryConnectorPackage(includeLabel: true));
        var xml = Encoding.UTF8.GetString(entries["ppt/slides/slide1.xml"])
            .Replace("<a:t>YES</a:t>", "<a:t>He said \"YES\"</a:t>", StringComparison.Ordinal);
        entries["ppt/slides/slide1.xml"] = Encoding.UTF8.GetBytes(xml);
        var markdown = new ReadableMarkdownSerializer().Serialize(new PptxAdapter().Extract(new MemoryStream(Repack(entries))).Graph);

        Assert.Contains("v_100 -->|He said &quot;YES&quot;| v_101", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("#quot;", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void AmbiguousUnsnappedConnectorIsDiagnosedAndNotInvented()
    {
        var extraction = new PptxAdapter().Extract(new MemoryStream(CreateGeometryConnectorPackage(ambiguousStart: true)));
        var visual = VisualGraphOf(extraction);

        Assert.Contains(visual.Edges, edge => edge.Resolution == VisualEdgeResolution.Unresolved && edge.SourceId is null && edge.TargetId is null);
        Assert.Contains(extraction.Warnings, warning => warning.StartsWith("VisualConnectorUnresolved", StringComparison.Ordinal));
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

    private static VisualGraph VisualGraphOf(PptxExtractionResult extraction)
    {
        var node = Assert.Single(extraction.Graph.Nodes, candidate => candidate.Extensions?.ContainsKey("visual_graph") == true);
        return node.Extensions!["visual_graph"].Deserialize<VisualGraph>()!;
    }

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
}
