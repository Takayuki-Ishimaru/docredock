using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Docx;
using DocRedock.Markdown;

namespace DocRedock.Tests.Docx;

/// <summary>A flowchart or sequence diagram drawn in a Word drawing canvas (wpc:wpc holding wps:wsp shapes and
/// connectors bound to them) projects the same visual graph a PPTX slide does.</summary>
public sealed class DocxCanvasDiagramTests
{
    private const int Emu = 10000;

    [Fact]
    public async Task Bound_connectors_in_a_canvas_become_directed_edges_between_typed_nodes()
    {
        var canvas = Rect(2, "Start", 100, 10, 120, 40, "flowChartTerminator") +
            Rect(3, "Check input", 100, 100, 120, 40, "flowChartProcess") +
            Rect(4, "Valid?", 100, 190, 120, 60, "flowChartDecision") +
            Rect(5, "Reject", 300, 200, 120, 40, "flowChartProcess") +
            Link(10, 2, 3, 160, 50, 0, 50) + Link(11, 3, 4, 160, 140, 0, 50) + Link(12, 4, 5, 220, 220, 80, 0) +
            Label(20, "No", 235, 200, 40, 16);
        var (graph, markdown) = await Extract(Paragraph(Canvas(canvas)));

        var visual = Single(graph);
        Assert.Equal(4, visual.Nodes.Count(node => node.Label != "No"));
        Assert.Equal(VisualNodeKind.Terminator, visual.Nodes.Single(node => node.Label == "Start").Kind);
        Assert.Equal(VisualNodeKind.Decision, visual.Nodes.Single(node => node.Label == "Valid?").Kind);
        Assert.Equal(3, visual.Edges.Count(edge => edge.SourceId is not null && edge.TargetId is not null));
        var branch = Assert.Single(visual.Edges, edge => Label(visual, edge.SourceId) == "Valid?");
        Assert.Equal("Reject", Label(visual, branch.TargetId));
        Assert.Equal(VisualEdgeResolution.NativeConnection, branch.Resolution);
        Assert.Equal(VisualEdgeDirection.Directed, branch.EdgeDirection);
        Assert.Equal("No", branch.Label);
        Assert.Contains("flowchart", markdown);
        Assert.Contains("|No|", markdown);
    }

    [Fact]
    public async Task A_branch_label_beside_the_far_segment_of_an_elbow_connector_attaches_to_that_connector()
    {
        // The elbow runs right from A, then down into B; the label sits beside the vertical part, far from the straight
        // line between the connector's corners. A second, straight connector passes nearer to the corner line.
        var canvas = Rect(2, "Decide", 10, 10, 100, 40, "flowChartDecision") + Rect(3, "Approved", 200, 120, 100, 40) +
            Rect(4, "Other", 10, 200, 100, 40) +
            $"<wps:wsp><wps:cNvPr id=\"10\" name=\"Elbow Connector\"/><wps:cNvCnPr><a:stCxn id=\"2\" idx=\"3\"/><a:endCxn id=\"3\" idx=\"0\"/></wps:cNvCnPr><wps:spPr>{Xfrm(110, 30, 140, 90)}" +
            "<a:prstGeom prst=\"bentConnector2\"><a:avLst/></a:prstGeom><a:ln w=\"9206\"><a:solidFill><a:srgbClr val=\"404040\"/></a:solidFill><a:tailEnd type=\"triangle\"/></a:ln></wps:spPr><wps:bodyPr/></wps:wsp>" +
            Link(11, 2, 4, 60, 50, 0, 150) + Label(20, "yes", 253, 60, 30, 16);
        var (graph, _) = await Extract(Paragraph(Canvas(canvas)));

        var visual = Single(graph);
        var elbow = Assert.Single(visual.Edges, edge => Label(visual, edge.TargetId) == "Approved");
        Assert.Equal("yes", elbow.Label);
        Assert.Null(Assert.Single(visual.Edges, edge => Label(visual, edge.TargetId) == "Other").Label);
    }

    [Fact]
    public async Task A_label_two_text_heights_from_an_elbow_connector_still_attaches()
    {
        var canvas = Rect(2, "Decide", 10, 10, 100, 40) + Rect(3, "Approved", 200, 120, 100, 40) +
            $"<wps:wsp><wps:cNvPr id=\"10\" name=\"Elbow Connector\"/><wps:cNvCnPr><a:stCxn id=\"2\" idx=\"3\"/><a:endCxn id=\"3\" idx=\"0\"/></wps:cNvCnPr><wps:spPr>{Xfrm(110, 30, 140, 90)}" +
            "<a:prstGeom prst=\"bentConnector2\"><a:avLst/></a:prstGeom><a:ln w=\"9206\"><a:solidFill><a:srgbClr val=\"404040\"/></a:solidFill><a:tailEnd type=\"triangle\"/></a:ln></wps:spPr><wps:bodyPr/></wps:wsp>" +
            Label(20, "yes", 282, 60, 30, 16);
        var (graph, _) = await Extract(Paragraph(Canvas(canvas)));

        Assert.Equal("yes", Assert.Single(Single(graph).Edges).Label);
    }

    [Fact]
    public async Task A_stray_duplicate_label_far_from_every_connector_does_not_invalidate_the_diagram()
    {
        var canvas = Rect(2, "Decide", 10, 10, 100, 40) + Rect(3, "Approved", 200, 120, 100, 40) + Rect(4, "Other", 10, 200, 100, 40) +
            Link(10, 2, 3, 110, 30, 140, 0) + Link(11, 2, 4, 60, 50, 0, 150) +
            Label(20, "no", 130, 8, 30, 16) + Label(21, "no", 800, 700, 30, 16);
        var (graph, markdown) = await Extract(Paragraph(Canvas(canvas)));

        var visual = Single(graph);
        Assert.DoesNotContain(visual.Nodes, node => node.Label == "no");
        Assert.Equal(2, visual.Edges.Count(edge => edge.SourceId is not null && edge.TargetId is not null));
        Assert.Contains(visual.Diagnostics!, diagnostic => diagnostic.Code == "VisualEdgeLabelUnresolved");
        Assert.NotEqual(VisualGraphQuality.Invalid, VisualGraphValidator.Validate(visual).Quality);
        Assert.Contains("flowchart", markdown);
        Assert.Contains("no", markdown);
    }

    [Fact]
    public async Task More_bound_connectors_than_the_solver_settles_per_cluster_still_resolve()
    {
        const int count = 46;
        var canvas = new StringBuilder();
        for (var index = 0; index < count; index++) canvas.Append(Rect(100 + index, "Step " + index, 100, 10 + index * 60, 100, 30));
        for (var index = 0; index < count - 1; index++) canvas.Append(Link(200 + index, 100 + index, 101 + index, 150, 40 + index * 60, 0, 30));
        var (graph, _) = await Extract(Paragraph(Canvas(canvas.ToString())));

        var visual = Single(graph);
        Assert.Equal(count - 1, visual.Edges.Count(edge => edge.SourceId is not null && edge.TargetId is not null));
        Assert.DoesNotContain(visual.Diagnostics ?? [], diagnostic => diagnostic.Code == "VisualConnectorUnresolved");
        var last = Assert.Single(visual.Edges, edge => Label(visual, edge.TargetId) == "Step " + (count - 1));
        Assert.Equal("Step " + (count - 2), Label(visual, last.SourceId));
    }

    [Fact]
    public async Task Swimlane_rectangles_are_groups_not_nodes_and_their_header_text_stays()
    {
        var canvas = Rect(2, "", 10, 10, 200, 600) + Rect(3, "Applicant", 10, 10, 200, 24) +
            Rect(4, "", 230, 10, 200, 600) + Rect(5, "Manager", 230, 10, 200, 24) +
            Rect(6, "Submit request", 60, 80, 100, 36) + Rect(7, "Review request", 280, 80, 100, 36) +
            Rect(8, "Approve", 280, 200, 100, 36) +
            Link(10, 6, 7, 160, 98, 120, 0) + Link(11, 7, 8, 330, 116, 0, 84);
        var (graph, markdown) = await Extract(Paragraph(Canvas(canvas)));

        var visual = Single(graph);
        Assert.Equal(["Approve", "Review request", "Submit request"], visual.Nodes.Select(node => node.Label).OrderBy(label => label, StringComparer.Ordinal));
        Assert.Equal("Applicant", visual.Nodes.Single(node => node.Label == "Submit request").Lane);
        Assert.Equal("Manager", visual.Nodes.Single(node => node.Label == "Approve").Lane);
        Assert.Equal(2, visual.Groups!.Count);
        var manager = Assert.Single(visual.Groups!, group => group.Label == "Manager");
        Assert.Equal(["Approve", "Review request"], manager.NodeIds!.Select(id => Label(visual, id)).OrderBy(label => label, StringComparer.Ordinal));
        Assert.Equal(230 * Emu, manager.Geometry!.X);
        Assert.Equal(600 * Emu, manager.Geometry.Height);
        Assert.Equal(2, visual.Edges.Count(edge => edge.SourceId is not null && edge.TargetId is not null));
        // The lane headers are accounted for as text the diagram does not consume.
        Assert.Contains(visual.SourceItems!, item => item.Kind == VisualSourceItemKind.TextLabel &&
            item.Disposition == VisualDisposition.IgnoredDecorative && item.Reason!.Contains("header", StringComparison.Ordinal));
        Assert.Contains("subgraph L1[\"Applicant\"]", markdown);
        Assert.Contains("Applicant", markdown);
        Assert.Contains("Manager", markdown);
    }

    [Fact]
    public async Task The_alternate_content_form_of_a_canvas_projects_the_canvas_and_not_its_vml_fallback()
    {
        var canvas = Rect(2, "Alpha", 10, 10, 100, 40) + Rect(3, "Beta", 200, 10, 100, 40) + Link(10, 2, 3, 110, 30, 90, 0);
        var alternate = "<mc:AlternateContent><mc:Choice Requires=\"wpc\">" + Drawing(canvas) + "</mc:Choice><mc:Fallback><w:pict>" +
            "<v:rect id=\"fallback\" style=\"width:100pt;height:40pt\"><v:textbox><w:txbxContent><w:p><w:r><w:t>VML_FALLBACK_ONLY</w:t></w:r></w:p></w:txbxContent></v:textbox></v:rect>" +
            "</w:pict></mc:Fallback></mc:AlternateContent>";
        var (graph, markdown) = await Extract("<w:p><w:r>" + alternate + "</w:r></w:p>");

        var edge = Assert.Single(Single(graph).Edges);
        Assert.Equal("Alpha", Label(Single(graph), edge.SourceId));
        Assert.Equal("Beta", Label(Single(graph), edge.TargetId));
        Assert.DoesNotContain("VML_FALLBACK_ONLY", markdown);
    }

    [Fact]
    public async Task A_canvas_without_connectors_reads_row_by_row_from_the_top_left()
    {
        // Stored in an unrelated order on purpose.
        var canvas = Text(2, "bottom right", 300, 300, 100, 30) + Text(3, "top right", 300, 10, 100, 30) +
            Text(4, "middle left", 10, 150, 100, 30) + Text(5, "top left", 10, 12, 100, 30) + Text(6, "bottom left", 10, 300, 100, 30);
        var (graph, markdown) = await Extract(Paragraph(Canvas(canvas)));

        Assert.DoesNotContain(graph.Nodes, node => node.Kind == NodeKind.Diagram);
        var order = new[] { "top left", "top right", "middle left", "bottom left", "bottom right" }.Select(text => markdown.IndexOf(text, StringComparison.Ordinal)).ToArray();
        Assert.All(order, index => Assert.True(index >= 0));
        Assert.Equal(order.OrderBy(index => index), order);
    }

    [Fact]
    public async Task Two_canvases_are_two_diagrams_even_when_they_share_shape_ids_and_origin()
    {
        var first = Rect(2, "One A", 10, 10, 100, 40) + Rect(3, "One B", 200, 10, 100, 40) + Link(4, 2, 3, 110, 30, 90, 0);
        var second = Rect(2, "Two A", 10, 10, 100, 40) + Rect(3, "Two B", 200, 10, 100, 40) + Link(4, 2, 3, 110, 30, 90, 0);
        var (graph, _) = await Extract(Paragraph(Canvas(first)) + Paragraph(Canvas(second)));

        var diagrams = Diagrams(graph);
        Assert.Equal(2, diagrams.Length);
        Assert.All(diagrams, visual => Assert.Single(visual.Edges, edge => edge.SourceId is not null && edge.TargetId is not null));
        Assert.Contains(diagrams, visual => visual.Nodes.Any(node => node.Label == "One A") && visual.Nodes.All(node => node.Label.StartsWith("One", StringComparison.Ordinal)));
        Assert.Contains(diagrams, visual => visual.Nodes.Any(node => node.Label == "Two A") && visual.Nodes.All(node => node.Label.StartsWith("Two", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_sequence_diagram_canvas_keeps_participants_lifelines_messages_labels_and_frames()
    {
        var canvas = Rect(2, "", 5, 150, 330, 140, name: "Frame alt") +
            Rect(3, "Client", 20, 10, 80, 30) + Rect(4, "Server", 140, 10, 80, 30) + Rect(5, "Store", 260, 10, 80, 30) +
            Link(10, 3, null, 60, 40, 0, 400, dashed: true, arrow: false) +
            Link(11, 4, null, 180, 40, 0, 400, dashed: true, arrow: false) +
            Link(12, 5, null, 300, 40, 0, 400, dashed: true, arrow: false) +
            Rect(20, "", 176, 90, 8, 40) + Rect(21, "", 296, 170, 8, 40) +
            Link(30, null, 20, 60, 90, 120, 0, arrow: true) + Label(31, "request", 70, 72, 100, 16) +
            Link(32, 21, null, 180, 170, 120, 0, arrow: true) + Label(33, "query", 190, 152, 100, 16) +
            Link(34, 20, null, 60, 250, 120, 0, flipH: true, arrow: true, dashed: true) + Label(35, "reply", 70, 232, 100, 16) +
            SelfLoop(36, 180, 200) + Label(37, "check", 200, 198, 60, 16) +
            Rect(40, "alt", 5, 150, 45, 18, "homePlate") + Label(41, "[found]", 55, 150, 80, 16);
        var (graph, markdown) = await Extract(Paragraph(Canvas(canvas)));

        var visual = Single(graph);
        // Participants are nodes at the top; an activation bar is not a node.
        foreach (var name in new[] { "Client", "Server", "Store" }) Assert.Contains(visual.Nodes, node => node.Label == name && node.Geometry is not null);
        Assert.DoesNotContain(visual.Nodes, node => node.Label.StartsWith("Shape ", StringComparison.Ordinal));
        // Three messages, each between two participants, in the sequence-diagram conventions.
        var messages = visual.Edges.Where(SequenceVisualConventions.IsMessage).ToArray();
        Assert.Equal(4, messages.Length);
        Assert.All(messages, edge => Assert.NotNull(edge.SourceId));
        var self = Assert.Single(messages, edge => edge.Label == "check");
        Assert.Equal(self.SourceId, self.TargetId);
        Assert.Equal("Server", Label(visual, self.SourceId));
        Assert.Contains(messages, edge => edge.Label == "request" && Label(visual, edge.SourceId) == "Client" && Label(visual, edge.TargetId) == "Server");
        var reply = Assert.Single(messages, edge => edge.Label == "reply");
        Assert.Equal("Server", Label(visual, reply.SourceId));
        Assert.Equal(VisualLineStyles.Dashed, reply.LineStyle);
        // The frame is a node of the frame group carrying its keyword.
        var frame = Assert.Single(visual.Nodes, SequenceVisualConventions.IsFrame);
        Assert.Equal("alt", frame.Lane);
        Assert.Contains("sequenceDiagram", markdown);
        foreach (var text in new[] { "Client", "Server", "Store", "request", "query", "reply", "check" })
            Assert.Contains(text, markdown);
    }

    [Fact]
    public async Task The_lane_fixture_resolves_every_connector_and_groups_its_lanes()
    {
        var path = FindFixture("diagram-canvas-lanes.docx");
        var graph = (await new DocxAdapter().ExtractAsync(path)).Graph;
        var visual = Single(graph);

        Assert.True(visual.Edges.Count >= 56);
        Assert.Equal(visual.Edges.Count, visual.Edges.Count(edge => edge.SourceId is not null && edge.TargetId is not null));
        Assert.Equal(4, visual.Groups!.Count);
        Assert.Equal(4, visual.Groups!.Select(group => group.Label).Distinct().Count());
        Assert.All(visual.Groups!, group => Assert.NotEmpty(group.NodeIds!));
        Assert.All(visual.Nodes.Where(node => node.Geometry is { Y: > 500000 }), node => Assert.NotNull(node.Lane));
        Assert.DoesNotContain(visual.Nodes, node => node.Label.StartsWith("Shape ", StringComparison.Ordinal));
        var markdown = new ReadableMarkdownSerializer().Serialize(graph);
        Assert.Contains("flowchart", markdown);
    }

    private static string Label(VisualGraph graph, string? id) => graph.Nodes.Single(node => node.Id == id).Label;

    private static VisualGraph Single(DocumentGraph graph) => Assert.Single(Diagrams(graph));

    private static VisualGraph[] Diagrams(DocumentGraph graph) => graph.Nodes
        .Where(node => node.Kind == NodeKind.Diagram && node.Extensions?.ContainsKey("visual_graph") == true)
        .Select(node => node.Extensions!["visual_graph"].Deserialize<VisualGraph>()!).ToArray();

    private static async Task<(DocumentGraph Graph, string Markdown)> Extract(string body)
    {
        var path = await Write(body);
        var graph = (await new DocxAdapter().ExtractAsync(path)).Graph;
        return (graph, new ReadableMarkdownSerializer().Serialize(graph));
    }

    private static string Paragraph(string drawing) => "<w:p><w:r>" + drawing + "</w:r></w:p>";

    private static string Canvas(string shapes) => Drawing(shapes);

    private static string Drawing(string shapes) =>
        "<w:drawing><wp:anchor distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\" simplePos=\"0\" relativeHeight=\"251659264\" behindDoc=\"0\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"1\">" +
        "<wp:simplePos x=\"0\" y=\"0\"/><wp:positionH relativeFrom=\"margin\"><wp:posOffset>0</wp:posOffset></wp:positionH>" +
        "<wp:positionV relativeFrom=\"paragraph\"><wp:posOffset>0</wp:posOffset></wp:positionV><wp:extent cx=\"6000000\" cy=\"8000000\"/>" +
        "<wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/><wp:wrapTopAndBottom/><wp:docPr id=\"900\" name=\"Drawing Canvas\"/><wp:cNvGraphicFramePr/>" +
        "<a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas\"><wpc:wpc>" +
        "<wpc:bg><a:noFill/></wpc:bg><wpc:whole><a:ln><a:noFill/></a:ln></wpc:whole>" + shapes +
        "</wpc:wpc></a:graphicData></a:graphic></wp:anchor></w:drawing>";

    private static string Xfrm(int x, int y, int width, int height, bool flipH = false) =>
        $"<a:xfrm{(flipH ? " flipH=\"1\"" : "")}><a:off x=\"{x * Emu}\" y=\"{y * Emu}\"/><a:ext cx=\"{width * Emu}\" cy=\"{height * Emu}\"/></a:xfrm>";

    private static string Body(string text) => text.Length == 0 ? "" :
        $"<wps:txbx><w:txbxContent><w:p><w:r><w:t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(text)}</w:t></w:r></w:p></w:txbxContent></wps:txbx>";

    private static string Rect(int id, string text, int x, int y, int width, int height, string preset = "rect", string? name = null) =>
        $"<wps:wsp><wps:cNvPr id=\"{id}\" name=\"{name ?? "Shape " + id}\"/><wps:cNvSpPr/><wps:spPr>{Xfrm(x, y, width, height)}<a:prstGeom prst=\"{preset}\"><a:avLst/></a:prstGeom>" +
        "<a:solidFill><a:srgbClr val=\"DEEBF7\"/></a:solidFill><a:ln w=\"9206\"><a:solidFill><a:srgbClr val=\"404040\"/></a:solidFill></a:ln></wps:spPr>" +
        Body(text) + "<wps:bodyPr/></wps:wsp>";

    private static string Text(int id, string text, int x, int y, int width, int height) => Label(id, text, x, y, width, height);

    /// <summary>A Word text box with neither fill nor outline.</summary>
    private static string Label(int id, string text, int x, int y, int width, int height) =>
        $"<wps:wsp><wps:cNvPr id=\"{id}\" name=\"TextBox {id}\"/><wps:cNvSpPr txBox=\"1\"/><wps:spPr>{Xfrm(x, y, width, height)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" +
        "<a:noFill/><a:ln><a:noFill/></a:ln></wps:spPr>" + Body(text) + "<wps:bodyPr/></wps:wsp>";

    private static string Link(int id, int? from, int? to, int x, int y, int width, int height, bool flipH = false, bool arrow = true, bool dashed = false) =>
        $"<wps:wsp><wps:cNvPr id=\"{id}\" name=\"Straight Arrow Connector {id}\"/><wps:cNvCnPr>" +
        (from is null ? "" : $"<a:stCxn id=\"{from}\" idx=\"2\"/>") + (to is null ? "" : $"<a:endCxn id=\"{to}\" idx=\"0\"/>") +
        $"</wps:cNvCnPr><wps:spPr>{Xfrm(x, y, width, height, flipH)}<a:prstGeom prst=\"straightConnector1\"><a:avLst/></a:prstGeom>" +
        $"<a:ln w=\"11507\"><a:solidFill><a:srgbClr val=\"404040\"/></a:solidFill>{(dashed ? "<a:prstDash val=\"dash\"/>" : "")}<a:round/>{(arrow ? "<a:tailEnd type=\"triangle\"/>" : "")}</a:ln></wps:spPr><wps:bodyPr/></wps:wsp>";

    /// <summary>An unbound bentConnector3 loop on one lifeline whose only guide is named "adj" (the width is a few thousand EMU).</summary>
    private static string SelfLoop(int id, int x, int y) =>
        $"<wps:wsp><wps:cNvPr id=\"{id}\" name=\"Self Message {id}\"/><wps:cNvCnPr/><wps:spPr><a:xfrm><a:off x=\"{x * Emu}\" y=\"{y * Emu}\"/><a:ext cx=\"6596\" cy=\"{18 * Emu}\"/></a:xfrm>" +
        "<a:prstGeom prst=\"bentConnector3\"><a:avLst><a:gd name=\"adj\" fmla=\"val 4672000\"/></a:avLst></a:prstGeom>" +
        "<a:ln w=\"10472\"><a:solidFill><a:srgbClr val=\"404040\"/></a:solidFill><a:round/><a:tailEnd type=\"triangle\"/></a:ln></wps:spPr><wps:bodyPr/></wps:wsp>";

    private static async Task<string> Write(string body)
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-canvas", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "canvas.docx");
        await using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            await Entry(archive, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            await Entry(archive, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"/>");
            await Entry(archive, "word/document.xml",
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
                "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" " +
                "xmlns:wpc=\"http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
                $"xmlns:v=\"urn:schemas-microsoft-com:vml\"><w:body>{body}</w:body></w:document>");
        }
        return path;
    }

    private static async Task Entry(ZipArchive archive, string name, string content)
    {
        await using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        await writer.WriteAsync(content);
    }

    private static string FindFixture(string fileName)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var path = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Docx", fileName);
            if (File.Exists(path)) return path;
            current = current.Parent;
        }
        throw new FileNotFoundException(fileName);
    }
}
