using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Pptx;
using DocRedock.Markdown;
using DocRedock.Render;

namespace DocRedock.Tests.Pptx;

/// <summary>
/// Flowcharts and sequence diagrams drawn as shapes on a slide: explicit connector bindings inside lanes, branch labels beside
/// connectors, and UML-style sequence diagrams (participants, lifelines, messages, activations, alt/opt/loop frames). The slides
/// are synthetic; geometry is in EMU.
/// </summary>
public sealed class PptxDiagramRecognitionTests
{
    // ------------------------------------------------------------------------------ flowcharts

    [Fact]
    public void Bound_connectors_inside_lane_rectangles_are_all_resolved_even_beyond_the_inference_limit()
    {
        const int columns = 12;
        const int count = 46;
        var body = new StringBuilder();
        // Two large lane rectangles without text hold the boxes.
        body.Append(Shape(2, "Lane 1", 100_000, 100_000, columns * 1_000_000 + 200_000, 3_100_000));
        body.Append(Shape(3, "Lane 2", 100_000, 3_300_000, columns * 1_000_000 + 200_000, 3_100_000));
        for (var index = 0; index < count; index++)
        {
            var (row, column) = (index / columns, index % columns);
            body.Append(Shape(10 + index, "Step " + index, 200_000 + column * 1_000_000, 300_000 + row * 1_500_000, 800_000, 500_000, "S" + index));
        }
        for (var index = 0; index + 1 < count; index++)
        {
            var (row, column) = (index / columns, index % columns);
            var (nextRow, nextColumn) = ((index + 1) / columns, (index + 1) % columns);
            var (x1, y1) = (200_000 + column * 1_000_000 + 800_000, 300_000 + row * 1_500_000 + 250_000);
            var (x2, y2) = (200_000 + nextColumn * 1_000_000, 300_000 + nextRow * 1_500_000 + 250_000);
            body.Append(Connector(200 + index, "Link " + index, "straightConnector1", Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1),
                start: 10 + index, end: 10 + index + 1, flipH: x2 < x1, flipV: y2 < y1, tail: "triangle"));
        }

        var graph = VisualGraphs(Package(body.ToString())).Single();

        Assert.Equal(count - 1, graph.Edges.Count(edge => edge.SourceId is not null && edge.TargetId is not null));
        Assert.DoesNotContain(graph.Diagnostics ?? [], diagnostic => diagnostic.Code == "VisualConnectorUnresolved");
        Assert.DoesNotContain(graph.Nodes, node => node.Label.Length == 0);
    }

    [Fact]
    public void Branch_labels_beside_connector_ends_and_loop_back_elbows_attach_to_their_connectors()
    {
        var body = new StringBuilder();
        body.Append(Shape(10, "Start", 1_000_000, 1_000_000, 1_500_000, 600_000, "Start"));
        body.Append(Shape(11, "Decision", 1_000_000, 2_500_000, 1_500_000, 900_000, "OK?", "flowChartDecision"));
        body.Append(Shape(12, "Done", 1_000_000, 4_500_000, 1_500_000, 600_000, "Done"));
        body.Append(Shape(13, "Retry", 12_000_000, 2_650_000, 1_500_000, 600_000, "Retry"));
        body.Append(Connector(20, "A to D", "straightConnector1", 1_750_000, 1_600_000, 0, 900_000, 10, 11, tail: "triangle"));
        body.Append(Connector(21, "D to Done", "straightConnector1", 1_750_000, 3_400_000, 0, 1_100_000, 11, 12, tail: "triangle"));
        body.Append(Connector(22, "D to Retry", "straightConnector1", 2_500_000, 2_950_000, 9_500_000, 0, 11, 13, tail: "triangle"));
        // A C-shaped loop-back from Done's left side to Start's left side, bent far to the left.
        body.Append(Connector(23, "Loop back", "bentConnector3", 987_300, 1_300_000, 12_700, 3_500_000, 12, 10, flipV: true, tail: "triangle",
            adjustments: "<a:gd name=\"adj1\" fmla=\"val -2000000\"/>"));
        // "Yes" sits beside the first stretch of a short connector, "No" beside the very start of a long one.
        body.Append(TextBox(30, "Yes", 1_850_000, 3_450_000, 420_624, 256_032, "Yes"));
        body.Append(TextBox(31, "No", 2_520_000, 2_650_000, 420_624, 256_032, "No"));
        body.Append(TextBox(32, "Again", 300_000, 3_000_000, 420_000, 256_032, "Again"));

        var extraction = Extract(Package(body.ToString()));
        var graph = VisualGraphs(extraction).Single();
        string Label(string source, string target) => graph.Edges.Single(edge =>
            Node(graph, edge.SourceId).Label == source && Node(graph, edge.TargetId).Label == target).Label ?? string.Empty;

        Assert.Equal("Yes", Label("OK?", "Done"));
        Assert.Equal("No", Label("OK?", "Retry"));
        Assert.Equal("Again", Label("Done", "Start"));
        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);
        foreach (var label in new[] { "Yes", "No", "Again" })
            Assert.DoesNotContain("\n" + label + "\n", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wide_text_box_touching_a_connector_end_is_not_taken_for_its_label()
    {
        var body = Shape(10, "A", 1_000_000, 1_000_000, 1_500_000, 600_000, "A") + Shape(11, "B", 1_000_000, 3_000_000, 1_500_000, 600_000, "B") +
            Connector(20, "A to B", "straightConnector1", 1_750_000, 1_600_000, 0, 1_400_000, 10, 11, tail: "triangle") +
            TextBox(30, "Caption", 100_000, 1_610_000, 4_000_000, 60_000, "A caption that is far wider than a branch label");

        var graph = VisualGraphs(Package(body)).Single();

        Assert.Null(Assert.Single(graph.Edges).Label);
    }

    // ------------------------------------------------------------------------------ sequence diagrams

    [Fact]
    public void Participants_lifelines_messages_and_frames_become_a_sequence_diagram()
    {
        var extraction = Extract(Package(SequenceSlide()));
        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);

        var mermaid = Mermaid(markdown);
        Assert.StartsWith("sequenceDiagram", mermaid, StringComparison.Ordinal);
        Assert.DoesNotContain("flowchart", markdown, StringComparison.Ordinal);
        Assert.Equal(
            ["participant P1 as Client", "participant P2 as Server", "participant P3 as DB",
             "P1->>P2: request", "P2-->>P1: reply", "alt found", "P2-)P3: notify", "else missing", "P3->>P3: compute", "end", "P1->>P3: final"],
            mermaid.Split('\n').Skip(1).Select(line => line.Trim()).ToArray());
        // Nothing the diagram uses is left behind as a paragraph.
        foreach (var text in new[] { "request", "reply", "notify", "compute", "final", "found", "missing" })
            Assert.DoesNotContain("\n" + text + "\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("診断", markdown, StringComparison.Ordinal);

        var svg = MermaidDiagramSvg.Render(mermaid);
        Assert.NotNull(svg);
        Assert.Contains("[found]", svg, StringComparison.Ordinal);
        Assert.Contains("[missing]", svg, StringComparison.Ordinal);
        Assert.Contains("-open)", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sequence_graph_validates_and_carries_the_conventions_other_adapters_can_follow()
    {
        var graph = VisualGraphs(Package(SequenceSlide())).Single();

        Assert.True(VisualGraphValidator.Validate(graph).IsValidForSemanticProjection);
        Assert.Equal(5, graph.Edges.Count(SequenceVisualConventions.IsMessage));
        Assert.Single(graph.Nodes, SequenceVisualConventions.IsFrame);
        Assert.Single(graph.Nodes, SequenceVisualConventions.IsBranch);
        Assert.Equal("alt", graph.Nodes.Single(SequenceVisualConventions.IsFrame).Lane);
        Assert.Equal("found", graph.Nodes.Single(SequenceVisualConventions.IsFrame).Label);
        Assert.Contains(graph.Edges, edge => edge.SourceId == edge.TargetId && edge.Evidence!.EvidenceCodes!.Contains(SequenceVisualConventions.SelfEvidence));
    }

    [Fact]
    public void A_flow_drawn_over_lane_centre_lines_stays_a_flowchart()
    {
        // Two lane headers with a dashed centre line under each and boxes sitting on those lines, joined by bound arrows: the
        // lines look like lifelines, but boxes as large as the headers rest on them, so this is a swimlane flow.
        var body = new StringBuilder();
        body.Append(Shape(10, "Lane A", 500_000, 400_000, 3_000_000, 400_000, "Lane A"));
        body.Append(Shape(11, "Lane B", 4_500_000, 400_000, 3_000_000, 400_000, "Lane B"));
        body.Append(Connector(12, "Centre A", "straightConnector1", 2_000_000, 800_000, 0, 6_000_000, dash: true));
        body.Append(Connector(13, "Centre B", "straightConnector1", 6_000_000, 800_000, 0, 6_000_000, dash: true));
        body.Append(Shape(20, "Receive", 1_100_000, 1_200_000, 1_800_000, 400_000, "Receive"));
        body.Append(Shape(21, "Check", 1_100_000, 2_400_000, 1_800_000, 400_000, "Check"));
        body.Append(Shape(22, "Approve", 5_100_000, 2_400_000, 1_800_000, 400_000, "Approve"));
        body.Append(Shape(23, "Archive", 5_100_000, 3_600_000, 1_800_000, 400_000, "Archive"));
        body.Append(Connector(30, "Down A", "straightConnector1", 2_000_000, 1_600_000, 0, 800_000, 20, 21, tail: "triangle"));
        body.Append(Connector(31, "Across", "straightConnector1", 2_900_000, 2_600_000, 2_200_000, 0, 21, 22, tail: "triangle"));
        body.Append(Connector(32, "Down B", "straightConnector1", 6_000_000, 2_800_000, 0, 800_000, 22, 23, tail: "triangle"));
        body.Append(Connector(33, "Extra", "straightConnector1", 2_900_000, 1_400_000, 2_200_000, 1_200_000, 20, 22, tail: "triangle"));

        var markdown = new ReadableMarkdownSerializer().Serialize(Extract(Package(body.ToString())).Graph);

        Assert.Contains("flowchart", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("sequenceDiagram", markdown, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------ benchmark fixtures

    [Fact]
    public async Task Benchmark_sequence_fixture_exports_participants_messages_and_frames()
    {
        var markdown = await ExportFixture("d3_oidc.pptx", html: false);
        var mermaid = Mermaid(markdown);

        Assert.StartsWith("sequenceDiagram", mermaid, StringComparison.Ordinal);
        Assert.Equal(8, mermaid.Split('\n').Count(line => line.TrimStart().StartsWith("participant ", StringComparison.Ordinal)));
        Assert.Equal(37, mermaid.Split('\n').Count(line => line.Contains("->>", StringComparison.Ordinal) || line.Contains("-)", StringComparison.Ordinal)));
        Assert.Contains("alt 未ログイン", mermaid, StringComparison.Ordinal);
        Assert.Contains("else ログイン済み", mermaid, StringComparison.Ordinal);
        Assert.Contains("opt 同意が未取得", mermaid, StringComparison.Ordinal);
        Assert.Contains("loop データ取得を最大3回繰り返す", mermaid, StringComparison.Ordinal);
        Assert.Contains("P4-)P8: 認可イベントを記録", mermaid, StringComparison.Ordinal);
        Assert.DoesNotContain("診断", markdown, StringComparison.Ordinal);

        var html = await ExportFixture("d3_oidc.pptx", html: true);
        Assert.Contains("Sequence diagram: 8 participants", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Benchmark_laned_flowchart_fixture_resolves_every_bound_connector_with_its_branch_label()
    {
        var markdown = await ExportFixture("d1_approval.pptx", html: false);

        Assert.Equal(57, markdown.Split('\n').Count(line => line.Contains("-->", StringComparison.Ordinal)));
        Assert.Equal(27, markdown.Split('\n').Count(line => line.Contains(" -->|", StringComparison.Ordinal)));
        // The four swimlanes are subgraphs, each node in one of them, and their titles are not left as paragraphs.
        Assert.Equal(4, markdown.Split('\n').Count(line => line.TrimStart().StartsWith("subgraph L", StringComparison.Ordinal)));
        Assert.Contains("subgraph L1[\"申請者\"]", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("\n**申請者**\n", markdown, StringComparison.Ordinal);
        var nodeIds = markdown.Split('\n').Select(line => line.Trim()).Where(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"^v_\d+[\[\(\{]"))
            .Select(line => line[..line.IndexOfAny(['[', '(', '{'])]).ToArray();
        Assert.Equal(nodeIds.Length, nodeIds.Distinct().Count());
        Assert.Contains("-->|差戻し|", markdown, StringComparison.Ordinal);
        Assert.Contains("-->|再連携|", markdown, StringComparison.Ordinal);
        Assert.Contains("-->|送信|", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("診断:", markdown, StringComparison.Ordinal);
        // The labels are on the edges, not left over as paragraphs.
        Assert.DoesNotContain("\nはい\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("\nいいえ\n", markdown, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------ helpers

    private static string SequenceSlide()
    {
        var body = new StringBuilder();
        // The frame is listed first, as PowerPoint keeps back-to-front order; the tab and the texts come after.
        body.Append(Shape(30, "Frame", 300_000, 2_700_000, 8_200_000, 1_500_000));
        foreach (var (id, name, x) in new[] { (10, "Client", 500_000), (11, "Server", 3_500_000), (12, "DB", 6_500_000) })
        {
            body.Append(Shape(id, "Participant " + name, x, 400_000, 1_500_000, 400_000, name));
            body.Append(Connector(id + 3, "Lifeline " + name, "straightConnector1", x + 750_000, 800_000, 0, 6_000_000, start: id, dash: true));
        }
        body.Append(Shape(40, "Activation", 4_170_000, 1_400_000, 160_000, 700_000));
        body.Append(Connector(50, "Message 1", "straightConnector1", 1_250_000, 1_400_000, 3_000_000, 0, tail: "triangle"));
        body.Append(TextBox(51, "Label 1", 1_500_000, 1_150_000, 2_500_000, 256_032, "request"));
        body.Append(Connector(52, "Message 2", "straightConnector1", 1_250_000, 2_000_000, 3_000_000, 0, flipH: true, tail: "triangle", dash: true));
        body.Append(TextBox(53, "Label 2", 1_500_000, 1_750_000, 2_500_000, 256_032, "reply"));
        body.Append(Connector(54, "Message 3", "straightConnector1", 4_250_000, 3_000_000, 3_000_000, 0, tail: "arrow"));
        body.Append(TextBox(55, "Label 3", 4_500_000, 2_750_000, 2_500_000, 256_032, "notify"));
        body.Append(Connector(56, "Message 4", "bentConnector3", 7_250_000, 3_600_000, 12_700, 300_000, tail: "triangle",
            adjustments: "<a:gd name=\"adj1\" fmla=\"val 3000000\"/>"));
        body.Append(TextBox(57, "Label 4", 7_800_000, 3_560_000, 900_000, 256_032, "compute"));
        body.Append(Connector(58, "Message 5", "straightConnector1", 1_250_000, 5_200_000, 6_000_000, 0, tail: "triangle"));
        body.Append(TextBox(59, "Label 5", 3_500_000, 4_950_000, 2_500_000, 256_032, "final"));
        body.Append(Shape(31, "Frame Tab", 300_000, 2_700_000, 600_000, 250_000, "alt", "homePlate"));
        body.Append(TextBox(32, "Condition", 950_000, 2_700_000, 1_400_000, 250_000, "[found]"));
        body.Append(Connector(33 + 100, "Else Divider", "straightConnector1", 300_000, 3_400_000, 8_200_000, 0, dash: true));
        body.Append(TextBox(34, "Else label", 350_000, 3_420_000, 1_200_000, 250_000, "[missing]"));
        return body.ToString();
    }

    private static string Mermaid(string markdown)
    {
        var start = markdown.IndexOf("```mermaid\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "No Mermaid block in:\n" + markdown);
        start += "```mermaid\n".Length;
        return markdown[start..markdown.IndexOf("\n```", start, StringComparison.Ordinal)];
    }

    private static async Task<string> ExportFixture(string name, bool html)
    {
        var fixture = FindFixture(name);
        var root = Path.Combine(Path.GetTempPath(), "docredock-pptx-diagrams", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var output = Path.Combine(root, name + (html ? ".html" : ".md"));
            await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(fixture, output,
                OutputFormat: html ? ReadableOutputFormat.Html : ReadableOutputFormat.Markdown));
            return await File.ReadAllTextAsync(output);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string FindFixture(string fileName)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var path = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Pptx", "DiagramBenchmark", fileName);
            if (File.Exists(path)) return path;
            current = current.Parent;
        }
        throw new FileNotFoundException(fileName);
    }

    private static PptxExtractionResult Extract(byte[] package) => new PptxAdapter().Extract(new MemoryStream(package));

    private static VisualGraph[] VisualGraphs(byte[] package) => VisualGraphs(Extract(package));

    private static VisualGraph[] VisualGraphs(PptxExtractionResult extraction) => extraction.Graph.Nodes
        .Where(node => node.Extensions?.ContainsKey("visual_graph") == true)
        .Select(node => node.Extensions!["visual_graph"].Deserialize<VisualGraph>()!).ToArray();

    private static VisualNode Node(VisualGraph graph, string? id) => graph.Nodes.Single(node => node.Id == id);

    private static string Shape(int id, string name, long x, long y, long width, long height, string text = "", string preset = "rect") =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{width}\" cy=\"{height}\"/></a:xfrm>" +
        $"<a:prstGeom prst=\"{preset}\"><a:avLst/></a:prstGeom></p:spPr>" +
        (text.Length == 0 ? "" : $"<p:txBody><a:bodyPr/><a:p><a:r><a:rPr lang=\"en-US\" sz=\"1000\"/><a:t>{text}</a:t></a:r></a:p></p:txBody>") + "</p:sp>";

    private static string TextBox(int id, string name, long x, long y, long width, long height, string text) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{width}\" cy=\"{height}\"/></a:xfrm>" +
        $"<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></p:spPr><p:txBody><a:bodyPr wrap=\"none\"/><a:p><a:r><a:rPr lang=\"en-US\" sz=\"1000\"/><a:t>{text}</a:t></a:r></a:p></p:txBody></p:sp>";

    private static string Connector(int id, string name, string preset, long x, long y, long width, long height, int start = 0, int end = 0,
        bool flipH = false, bool flipV = false, string tail = "", bool dash = false, string adjustments = "") =>
        $"<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvCxnSpPr>" +
        (start > 0 ? $"<a:stCxn id=\"{start}\" idx=\"2\"/>" : "") + (end > 0 ? $"<a:endCxn id=\"{end}\" idx=\"0\"/>" : "") +
        $"</p:cNvCxnSpPr><p:nvPr/></p:nvCxnSpPr><p:spPr><a:xfrm{(flipH ? " flipH=\"1\"" : "")}{(flipV ? " flipV=\"1\"" : "")}><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{width}\" cy=\"{height}\"/></a:xfrm>" +
        $"<a:prstGeom prst=\"{preset}\"><a:avLst>{adjustments}</a:avLst></a:prstGeom><a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"404040\"/></a:solidFill>" +
        (dash ? "<a:prstDash val=\"dash\"/>" : "") + (tail.Length > 0 ? $"<a:tailEnd type=\"{tail}\"/>" : "") + "</a:ln></p:spPr></p:cxnSp>";

    private static byte[] Package(string spTreeBody)
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
            {
                using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), new UTF8Encoding(false));
                writer.Write(part.Value);
            }
        return output.ToArray();
    }
}
