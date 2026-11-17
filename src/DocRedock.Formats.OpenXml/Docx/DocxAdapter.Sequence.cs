using System.Globalization;
using System.Xml.Linq;
using DocRedock.Core.Documents;
using DocRedock.VisualInference;

namespace DocRedock.Formats.OpenXml.Docx;

// A drawing canvas that draws a sequence diagram: participant boxes in a row, a lifeline under each, horizontal message
// arrows between the lifelines and labelled frames over groups of messages. SequenceDiagramRecognizer reads that from
// geometry alone; this file hands it the canvas shapes and connectors and turns what it found into the shared VisualGraph
// (see SequenceVisualConventions for how the graph carries it), exactly as the PPTX adapter does.
public sealed partial class DocxAdapter
{
    private static VisualGraph? TryBuildDocxSequenceGraph(IReadOnlyList<XElement> shapeElements, IReadOnlyList<XElement> connectorElements,
        SourceAnchor anchor, string sourceNodeId, DocxHiddenStyles hiddenStyles)
    {
        if (connectorElements.Count < 5) return null;
        static string? IdOf(XElement element) => (string?)element.Attribute("id") ??
            (string?)element.Descendants().FirstOrDefault(item => item.Name.LocalName == "cNvPr")?.Attribute("id");

        var shapes = new List<SequenceShapeInput>();
        var shapeIds = new HashSet<string>(StringComparer.Ordinal);
        string? space = null;
        foreach (var element in shapeElements)
        {
            var id = IdOf(element);
            var geometry = ReadVisualGeometry(element);
            if (id is null || geometry is null || !shapeIds.Add(id)) continue;
            space ??= geometry.CoordinateSpace;
            shapes.Add(new SequenceShapeInput(id, new VisualRect(geometry.X, geometry.Y, geometry.Width, geometry.Height),
                TextBoxText(element, hiddenStyles).Trim(), (string?)element.Descendants(A + "prstGeom").FirstOrDefault()?.Attribute("prst"),
                element.Name == WPS + "wsp" && IsWpsTextBox(element)));
        }
        var lineElements = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var lines = new List<SequenceLineInput>();
        foreach (var element in connectorElements)
        {
            var id = IdOf(element);
            if (id is null || lineElements.ContainsKey(id) || DocxConnectorRoute(element) is not { Count: >= 2 } route) continue;
            lineElements[id] = element;
            var outline = element.Descendants(A + "ln").FirstOrDefault();
            static string? Arrow(XElement? end) => end is null || string.Equals((string?)end.Attribute("type"), "none", StringComparison.OrdinalIgnoreCase)
                ? null : (string?)end.Attribute("type") ?? "triangle";
            var ownText = TextBoxText(element, hiddenStyles).Trim();
            lines.Add(new SequenceLineInput(id, route, DocxOutlineLineStyle(element) is not null,
                Arrow(outline?.Element(A + "headEnd")), Arrow(outline?.Element(A + "tailEnd")), ownText.Length == 0 ? null : ownText));
        }
        var recognition = SequenceDiagramRecognizer.Recognize(shapes, lines);
        if (recognition is null || space is null) return null;

        SourceAnchor Anchor(string shapeId) => anchor with { Locators = [new AnchorLocator("shape_id", shapeId)] };
        string NodeId(string shapeId) => NodeIdGenerator.CreateForSource("docx-visual", DocumentFormatKind.Docx, Anchor(shapeId));
        Geometry Placed(VisualRect box) => new(space, box.X, box.Y, box.Width, box.Height);

        var nodes = new List<VisualNode>();
        var edges = new List<VisualEdge>();
        var sourceItems = new List<VisualSourceItem>();
        var diagnostics = new List<VisualDiagnostic>();
        var accounted = new HashSet<string>(StringComparer.Ordinal);
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        void Ledger(VisualSourceItem item) { if (itemIds.Add(item.Id)) sourceItems.Add(item); }

        foreach (var participant in recognition.Participants)
        {
            nodes.Add(new VisualNode(NodeId(participant.ShapeId), participant.Label, VisualNodeKind.Process, sourceNodeId,
                Placed(participant.Header), Anchor(participant.ShapeId)));
            Ledger(new VisualSourceItem("shape:" + participant.ShapeId, VisualSourceItemKind.Shape, VisualDisposition.ProjectedNode,
                ProjectedNodeId: NodeId(participant.ShapeId), SourceAnchor: Anchor(participant.ShapeId)));
            accounted.Add(participant.ShapeId);
            Ledger(new VisualSourceItem("lifeline:" + participant.LifelineId, VisualSourceItemKind.Connector,
                VisualDisposition.IgnoredDecorative, Reason: "sequence lifeline", SourceAnchor: Anchor(participant.LifelineId)));
            accounted.Add(participant.LifelineId);
        }
        foreach (var frame in recognition.Frames)
        {
            var frameNodeId = NodeId(frame.ShapeId);
            nodes.Add(new VisualNode(frameNodeId, string.IsNullOrWhiteSpace(frame.Label) ? frame.Kind : frame.Label, VisualNodeKind.Generic,
                sourceNodeId, Placed(frame.Bounds), Anchor(frame.ShapeId), SequenceVisualConventions.FrameGroup, frame.Kind));
            Ledger(new VisualSourceItem("shape:" + frame.ShapeId, VisualSourceItemKind.Shape, VisualDisposition.ProjectedNode,
                ProjectedNodeId: frameNodeId, SourceAnchor: Anchor(frame.ShapeId)));
            accounted.Add(frame.ShapeId);
            foreach (var branch in frame.Branches)
            {
                var branchNodeId = NodeId(branch.LineId);
                nodes.Add(new VisualNode(branchNodeId, string.IsNullOrWhiteSpace(branch.Label) ? branch.Keyword : branch.Label, VisualNodeKind.Generic,
                    sourceNodeId, Placed(branch.Bounds), Anchor(branch.LineId), SequenceVisualConventions.BranchGroup, branch.Keyword));
                Ledger(new VisualSourceItem("branch:" + branch.LineId, VisualSourceItemKind.Shape, VisualDisposition.ProjectedNode,
                    ProjectedNodeId: branchNodeId, SourceAnchor: Anchor(branch.LineId)));
                accounted.Add(branch.LineId);
            }
        }
        var index = 0;
        foreach (var message in recognition.Messages)
        {
            var edgeId = "edge_" + index.ToString("D4", CultureInfo.InvariantCulture);
            var route = lineElements.TryGetValue(message.LineId, out var element) ? DocxConnectorRoute(element)! : [];
            var codes = new List<string> { SequenceVisualConventions.MessageEvidence };
            if (message.IsSelf) codes.Add(SequenceVisualConventions.SelfEvidence);
            if (message.Kind == SequenceMessageKind.Async) codes.Add(SequenceVisualConventions.AsyncEvidence);
            if (message.Kind == SequenceMessageKind.Return) codes.Add(SequenceVisualConventions.ReturnEvidence);
            edges.Add(new VisualEdge(edgeId, NodeId(recognition.Participants[message.From].ShapeId), NodeId(recognition.Participants[message.To].ShapeId),
                message.Label.Length == 0 ? null : message.Label, VisualEdgeResolution.LayoutInferred, sourceNodeId, "directed", Placed(message.Bounds), .9,
                route.Select(point => new VisualPathPoint(point.X, point.Y)).ToArray(), Anchor(message.LineId), VisualEdgeDirection.Directed,
                new VisualConnectionEvidence("sequence-layout", "High", .9, ArrowheadEvidence: "end", EvidenceCodes: codes),
                element is null ? null : DocxOutlineLineStyle(element)));
            Ledger(new VisualSourceItem("connector:" + index.ToString(CultureInfo.InvariantCulture), VisualSourceItemKind.Connector,
                VisualDisposition.ProjectedEdge, ProjectedEdgeId: edgeId, SourceAnchor: Anchor(message.LineId)));
            accounted.Add(message.LineId);
            index++;
        }
        // Connectors the diagram did not take (a stray arrow) stay visible as unresolved relations.
        foreach (var (lineId, element) in lineElements.Where(item => !recognition.ConsumedShapeIds.Contains(item.Key) && !accounted.Contains(item.Key)))
        {
            var outline = element.Descendants(A + "ln").FirstOrDefault();
            if (outline?.Element(A + "headEnd") is null && outline?.Element(A + "tailEnd") is null) continue;
            var edgeId = "edge_" + index.ToString("D4", CultureInfo.InvariantCulture);
            diagnostics.Add(new VisualDiagnostic("VisualConnectorUnresolved", "DOCX connector endpoint could not be resolved.", sourceNodeId,
                Fallback: "connector retained as visual fallback", Remedy: "snap both connector endpoints to distinct shapes",
                Format: "docx", PartUri: anchor.PartUri, PartitionId: "part-0001", SourceObjectId: lineId, SourceObjectType: "connector", Confidence: 0));
            edges.Add(new VisualEdge(edgeId, null, null, null, VisualEdgeResolution.Unresolved, sourceNodeId, Direction: "directed",
                Geometry: ReadVisualGeometry(element), Confidence: 0, SourceAnchor: Anchor(lineId), EdgeDirection: VisualEdgeDirection.Directed,
                LineStyle: DocxOutlineLineStyle(element)));
            Ledger(new VisualSourceItem("connector:" + index.ToString(CultureInfo.InvariantCulture), VisualSourceItemKind.Connector,
                VisualDisposition.DiagnosticOnly, DiagnosticCode: "VisualConnectorUnresolved", Reason: "connector endpoints unresolved", SourceAnchor: Anchor(lineId)));
            accounted.Add(lineId);
            index++;
        }
        // Everything else the recognizer took (activation bars, text beside messages and frames) is part of the drawing; the
        // text shapes it attached are consumed, so their text boxes are not repeated in the body.
        foreach (var shapeId in recognition.ConsumedShapeIds.Where(id => !accounted.Contains(id)).OrderBy(id => id, StringComparer.Ordinal))
        {
            var isText = recognition.TextShapeIds.Contains(shapeId);
            var isConnector = lineElements.ContainsKey(shapeId);
            Ledger(new VisualSourceItem("sequence:" + shapeId,
                isText ? VisualSourceItemKind.TextLabel : isConnector ? VisualSourceItemKind.Connector : VisualSourceItemKind.Shape,
                VisualDisposition.IgnoredDecorative,
                Reason: isText ? "attached as connector label" : "sequence diagram structure", SourceAnchor: Anchor(shapeId)));
        }
        foreach (var message in recognition.Messages)
            if (message.LabelShapeId is { } labelId)
                Ledger(new VisualSourceItem("sequence:" + labelId, VisualSourceItemKind.TextLabel, VisualDisposition.IgnoredDecorative,
                    Reason: "attached as connector label", SourceAnchor: Anchor(labelId)));
        var graph = new VisualGraph("docx_visual_" + sourceNodeId, nodes, edges, diagnostics, "TD", SourceItems: sourceItems);
        return graph with { Quality = VisualGraphValidator.ComputeQuality(graph) };
    }

    /// <summary>The drawn start and end of a DrawingML connector in canvas coordinates, with flips and rotation applied.</summary>
    private static IReadOnlyList<VisualPoint>? DocxConnectorRoute(XElement element)
    {
        var geometry = ReadVisualGeometry(element);
        if (geometry is null) return null;
        var xfrm = element.Descendants(A + "xfrm").FirstOrDefault();
        static bool On(string? value) => value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        var flipH = On((string?)xfrm?.Attribute("flipH"));
        var flipV = On((string?)xfrm?.Attribute("flipV"));
        var rotation = double.TryParse((string?)xfrm?.Attribute("rot"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rot) ? rot / 60000d : 0;
        var center = new VisualPoint(geometry.X + geometry.Width / 2, geometry.Y + geometry.Height / 2);
        VisualPoint Place(double x, double y)
        {
            if (flipH) x = geometry.X + geometry.Width - (x - geometry.X);
            if (flipV) y = geometry.Y + geometry.Height - (y - geometry.Y);
            if (Math.Abs(rotation) < 1e-9) return new VisualPoint(x, y);
            var radians = rotation * Math.PI / 180d;
            var dx = x - center.X;
            var dy = y - center.Y;
            return new VisualPoint(center.X + dx * Math.Cos(radians) - dy * Math.Sin(radians), center.Y + dx * Math.Sin(radians) + dy * Math.Cos(radians));
        }
        // A bent connector runs through its own bends (bentConnector3 across to adj1 of the width, down, and across), which is
        // what a label beside a self-message loop is measured against; a single-guide connector may name it "adj".
        var adjustments = element.Descendants(A + "gd").Select(guide => (Name: (string?)guide.Attribute("name"), Formula: (string?)guide.Attribute("fmla")))
            .Where(guide => guide.Name is not null && guide.Formula is not null && guide.Formula.StartsWith("val ", StringComparison.Ordinal) &&
                double.TryParse(guide.Formula.AsSpan(4), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .GroupBy(guide => guide.Name!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => double.Parse(group.First().Formula!.AsSpan(4), NumberStyles.Float, CultureInfo.InvariantCulture), StringComparer.Ordinal);
        double Adjust(string name) => adjustments.TryGetValue(name, out var value) || name == "adj1" && adjustments.TryGetValue("adj", out value) ? value / 100_000 : .5;
        var preset = (string?)element.Descendants(A + "prstGeom").FirstOrDefault()?.Attribute("prst");
        (double X, double Y)[] unit = preset?.ToLowerInvariant() switch
        {
            "bentconnector2" => [(0, 0), (1, 0), (1, 1)],
            "bentconnector3" => [(0, 0), (Adjust("adj1"), 0), (Adjust("adj1"), 1), (1, 1)],
            "bentconnector4" => [(0, 0), (Adjust("adj1"), 0), (Adjust("adj1"), Adjust("adj2")), (1, Adjust("adj2")), (1, 1)],
            _ => [(0, 0), (1, 1)],
        };
        return unit.Select(point => Place(geometry.X + point.X * geometry.Width, geometry.Y + point.Y * geometry.Height)).ToArray();
    }
}
