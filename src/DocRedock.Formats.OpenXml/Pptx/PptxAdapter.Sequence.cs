using DocRedock.Core.Documents;
using DocRedock.VisualInference;

namespace DocRedock.Formats.OpenXml.Pptx;

// A slide that draws a sequence diagram: participant boxes in a row, a lifeline under each, horizontal message arrows between
// the lifelines and, often, labelled frames over groups of messages. SequenceDiagramRecognizer reads that from geometry alone;
// this file turns what it found into the shared VisualGraph (see SequenceVisualConventions for how the graph carries it).
public sealed partial class PptxAdapter
{
    private static VisualGraph? TryBuildSequenceGraph(PptxSlideRecord slide, out HashSet<string> labelShapeIds)
    {
        labelShapeIds = new HashSet<string>(StringComparer.Ordinal);
        var own = slide.Shapes.Where(shape => shape.InheritedFrom is null && !shape.IsHidden).ToArray();
        var connectors = own.Where(shape => StringComparer.Ordinal.Equals(shape.ShapeType, "connector")).ToArray();
        if (connectors.Length < 5) return null;
        var shapes = own.Where(shape => !StringComparer.Ordinal.Equals(shape.ShapeType, "connector") && !shape.IsTable &&
                shape.Geometry is { Width: >= 0, Height: >= 0 } && shape.ImageRelationshipIds.Count == 0)
            .GroupBy(shape => shape.ShapeId, StringComparer.Ordinal).Select(group => group.First())
            .Select(shape => new SequenceShapeInput(shape.ShapeId,
                new VisualRect(shape.Geometry!.X, shape.Geometry.Y, shape.Geometry.Width, shape.Geometry.Height), shape.Text, shape.ShapePreset, shape.IsTextBox))
            .ToArray();
        var shapeById = own.GroupBy(shape => shape.ShapeId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var lines = connectors.GroupBy(shape => shape.ShapeId, StringComparer.Ordinal).Select(group => group.First())
            .Select(connector => (Connector: connector, Route: connector.ConnectorPathPoints is { Count: >= 2 } route ? route : ConnectorPoints(connector)))
            .Where(item => item.Route.Count >= 2)
            .Select(item => new SequenceLineInput(item.Connector.ShapeId, item.Route, item.Connector.LineStyle is not null,
                item.Connector.ConnectorHeadArrow, item.Connector.ConnectorTailArrow, item.Connector.ConnectorOwnText))
            .ToArray();
        var recognition = SequenceDiagramRecognizer.Recognize(shapes, lines);
        if (recognition is null) return null;

        string NodeId(string shapeId) => "v_" + SafeVisualId(shapeId);
        Geometry Placed(VisualRect box) => new("pptx-emu", box.X, box.Y, box.Width, box.Height);
        SourceAnchor Anchor(string shapeId) => new("pptx", slide.PartUri, [new AnchorLocator("shape_id", shapeId)]);

        var nodes = new List<VisualNode>();
        var edges = new List<VisualEdge>();
        var sourceItems = new List<VisualSourceItem>();
        var diagnostics = new List<VisualDiagnostic>();
        var accounted = new HashSet<string>(StringComparer.Ordinal);
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        void Ledger(VisualSourceItem item) { if (itemIds.Add(item.Id)) sourceItems.Add(item); }

        foreach (var participant in recognition.Participants)
        {
            nodes.Add(new VisualNode(NodeId(participant.ShapeId), participant.Label, VisualNodeKind.Process, participant.ShapeId,
                Placed(participant.Header), Anchor(participant.ShapeId)));
            Ledger(new VisualSourceItem("shape:" + SafeVisualId(participant.ShapeId), VisualSourceItemKind.Shape, VisualDisposition.ProjectedNode,
                ProjectedNodeId: NodeId(participant.ShapeId), SourceAnchor: Anchor(participant.ShapeId)));
            accounted.Add(participant.ShapeId);
            Ledger(new VisualSourceItem("lifeline:" + SafeVisualId(participant.LifelineId), VisualSourceItemKind.Connector,
                VisualDisposition.IgnoredDecorative, Reason: "sequence lifeline", SourceAnchor: Anchor(participant.LifelineId)));
            accounted.Add(participant.LifelineId);
        }
        foreach (var frame in recognition.Frames)
        {
            var frameNodeId = NodeId(frame.ShapeId);
            nodes.Add(new VisualNode(frameNodeId, string.IsNullOrWhiteSpace(frame.Label) ? frame.Kind : frame.Label, VisualNodeKind.Generic,
                frame.ShapeId, Placed(frame.Bounds), Anchor(frame.ShapeId), SequenceVisualConventions.FrameGroup, frame.Kind));
            Ledger(new VisualSourceItem("shape:" + SafeVisualId(frame.ShapeId), VisualSourceItemKind.Shape, VisualDisposition.ProjectedNode,
                ProjectedNodeId: frameNodeId, SourceAnchor: Anchor(frame.ShapeId)));
            accounted.Add(frame.ShapeId);
            foreach (var branch in frame.Branches)
            {
                var branchNodeId = "v_branch_" + SafeVisualId(branch.LineId);
                nodes.Add(new VisualNode(branchNodeId, string.IsNullOrWhiteSpace(branch.Label) ? branch.Keyword : branch.Label, VisualNodeKind.Generic,
                    branch.LineId, Placed(branch.Bounds), Anchor(branch.LineId), SequenceVisualConventions.BranchGroup, branch.Keyword));
                Ledger(new VisualSourceItem("branch:" + SafeVisualId(branch.LineId), VisualSourceItemKind.Shape, VisualDisposition.ProjectedNode,
                    ProjectedNodeId: branchNodeId, SourceAnchor: Anchor(branch.LineId)));
                accounted.Add(branch.LineId);
            }
        }
        var index = 0;
        foreach (var message in recognition.Messages)
        {
            var edgeId = "e_" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            shapeById.TryGetValue(message.LineId, out var connector);
            var codes = new List<string> { SequenceVisualConventions.MessageEvidence };
            if (message.IsSelf) codes.Add(SequenceVisualConventions.SelfEvidence);
            if (message.Kind == SequenceMessageKind.Async) codes.Add(SequenceVisualConventions.AsyncEvidence);
            if (message.Kind == SequenceMessageKind.Return) codes.Add(SequenceVisualConventions.ReturnEvidence);
            var route = connector?.ConnectorPathPoints is { Count: >= 2 } placed ? placed : connector is null ? [] : ConnectorPoints(connector);
            edges.Add(new VisualEdge(edgeId, NodeId(recognition.Participants[message.From].ShapeId), NodeId(recognition.Participants[message.To].ShapeId),
                message.Label.Length == 0 ? null : message.Label, VisualEdgeResolution.LayoutInferred, message.LineId, "directed", Placed(message.Bounds), .9,
                route.Select(point => new VisualPathPoint(point.X, point.Y)).ToArray(), Anchor(message.LineId), VisualEdgeDirection.Directed,
                new VisualConnectionEvidence("sequence-layout", "High", .9, ArrowheadEvidence: "end", EvidenceCodes: codes),
                connector?.LineStyle));
            Ledger(new VisualSourceItem("connector:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), VisualSourceItemKind.Connector,
                VisualDisposition.ProjectedEdge, ProjectedEdgeId: edgeId, SourceAnchor: Anchor(message.LineId)));
            accounted.Add(message.LineId);
            index++;
        }
        // Connectors the diagram did not take (a stray arrow) stay visible as unresolved relations.
        foreach (var connector in connectors.GroupBy(shape => shape.ShapeId, StringComparer.Ordinal).Select(group => group.First())
                     .Where(shape => !recognition.ConsumedShapeIds.Contains(shape.ShapeId) && !accounted.Contains(shape.ShapeId)))
        {
            var arrowed = ConnectorDirection(connector) != ConnectionDirection.Unknown;
            if (!arrowed) continue;
            var edgeId = "e_" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            diagnostics.Add(new VisualDiagnostic("VisualConnectorUnresolved", "A PPTX connector could not be uniquely associated with source and target shapes.",
                connector.ShapeId, Fallback: "connector retained as visual fallback", Remedy: "snap both connector endpoints to shapes or make geometry unambiguous",
                Format: "pptx", PartUri: slide.PartUri, PartitionId: slide.SlideId, SourceObjectId: connector.ShapeId, SourceObjectType: "connector", Confidence: 0));
            edges.Add(new VisualEdge(edgeId, null, null, null, VisualEdgeResolution.Unresolved, connector.ShapeId, Direction: "directed",
                Geometry: connector.Geometry, Confidence: 0, SourceAnchor: Anchor(connector.ShapeId), EdgeDirection: VisualEdgeDirection.Directed,
                LineStyle: connector.LineStyle));
            Ledger(new VisualSourceItem("connector:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), VisualSourceItemKind.Connector,
                VisualDisposition.DiagnosticOnly, DiagnosticCode: "VisualConnectorUnresolved", Reason: "connector endpoints unresolved", SourceAnchor: Anchor(connector.ShapeId)));
            accounted.Add(connector.ShapeId);
            index++;
        }
        // Everything else the recognizer took (activation bars, text beside messages and frames) is part of the drawing.
        foreach (var shapeId in recognition.ConsumedShapeIds.Where(id => !accounted.Contains(id)).OrderBy(id => id, StringComparer.Ordinal))
        {
            var isText = recognition.TextShapeIds.Contains(shapeId);
            if (isText) labelShapeIds.Add(shapeId);
            var isConnector = shapeById.TryGetValue(shapeId, out var shape) && StringComparer.Ordinal.Equals(shape.ShapeType, "connector");
            Ledger(new VisualSourceItem("sequence:" + SafeVisualId(shapeId),
                isText ? VisualSourceItemKind.TextLabel : isConnector ? VisualSourceItemKind.Connector : VisualSourceItemKind.Shape,
                VisualDisposition.IgnoredDecorative,
                Reason: isText ? "attached as sequence message or frame label" : "sequence diagram structure", SourceAnchor: Anchor(shapeId)));
        }
        foreach (var message in recognition.Messages)
            if (message.LabelShapeId is { } labelId) labelShapeIds.Add(labelId);
        var graph = new VisualGraph("pptx_" + SafeVisualId(slide.SlideId), nodes, edges, diagnostics, "TD", SourceItems: sourceItems);
        return graph with { Quality = VisualGraphValidator.ComputeQuality(graph) };
    }
}
