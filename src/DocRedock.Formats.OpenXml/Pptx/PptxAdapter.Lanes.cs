using DocRedock.Core.Documents;
using DocRedock.VisualInference;

namespace DocRedock.Formats.OpenXml.Pptx;

// Swimlanes: large textless rectangles side by side (or stacked), each enclosing flowchart nodes and titled by a header text at
// its top or left edge. The nodes get the lane, the lane gets a group, and the title is consumed instead of left as a paragraph.
public sealed partial class PptxAdapter
{
    private static List<VisualGraph> BuildVisualGraphs(PptxSlideRecord slide, out HashSet<string> edgeLabelShapeIds,
        TimeSpan? inferenceTimeout, CancellationToken cancellationToken)
    {
        var graphs = BuildVisualGraphsCore(slide, out edgeLabelShapeIds, inferenceTimeout, cancellationToken);
        for (var index = 0; index < graphs.Count; index++)
            graphs[index] = ApplyLanes(slide, graphs[index], edgeLabelShapeIds);
        return graphs;
    }

    private static VisualGraph ApplyLanes(PptxSlideRecord slide, VisualGraph graph, HashSet<string> consumedTextShapeIds)
    {
        if (graph.Nodes.Count < 2 || graph.Nodes.Any(SequenceVisualConventions.IsFrame)) return graph;
        var nodeShapeIds = graph.Nodes.Select(node => node.SourceNodeId).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        var own = slide.Shapes.Where(shape => shape.InheritedFrom is null && !shape.IsHidden && shape.Geometry is not null &&
            !StringComparer.Ordinal.Equals(shape.ShapeType, "connector") && !shape.IsTable).ToArray();
        static VisualPoint Centre(Geometry g) => new(g.X + g.Width / 2, g.Y + g.Height / 2);
        var nodes = graph.Nodes.Where(node => node.Geometry is not null).ToArray();
        var candidates = own.Where(shape => string.IsNullOrWhiteSpace(shape.Text) && !nodeShapeIds.Contains(shape.ShapeId) &&
                (shape.ShapePreset is null or "rect" or "roundRect"))
            .Select(shape => (Shape: shape, Members: nodes.Where(node => new VisualRect(shape.Geometry!.X, shape.Geometry.Y, shape.Geometry.Width, shape.Geometry.Height)
                .Contains(Centre(node.Geometry!))).ToArray()))
            .Where(item => item.Members.Length >= 2 && item.Members.Length < nodes.Length)
            .ToList();
        // An outer frame that holds other candidates is not a lane.
        static bool Holds(Geometry outer, Geometry inner) => outer.X <= inner.X && outer.Y <= inner.Y && outer.X + outer.Width >= inner.X + inner.Width &&
            outer.Y + outer.Height >= inner.Y + inner.Height && outer.Width * outer.Height > inner.Width * inner.Height;
        candidates = candidates.Where(a => !candidates.Any(b => !ReferenceEquals(a.Shape, b.Shape) && Holds(a.Shape.Geometry!, b.Shape.Geometry!))).ToList();
        static bool Overlaps(Geometry a, Geometry b)
        {
            var w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
            var h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
            return w > 0 && h > 0 && w * h > Math.Min(a.Width * a.Height, b.Width * b.Height) * .05;
        }
        if (candidates.Count < 2 || candidates.Any(a => candidates.Any(b => !ReferenceEquals(a.Shape, b.Shape) && Overlaps(a.Shape.Geometry!, b.Shape.Geometry!)))) return graph;

        var lanes = new List<(PptxShapeRecord Frame, string Title, string TitleShapeId)>();
        foreach (var (frame, _) in candidates)
        {
            var g = frame.Geometry!;
            PptxShapeRecord? title = null;
            var best = double.MaxValue;
            foreach (var text in own)
            {
                var t = text.Geometry!;
                if (string.IsNullOrWhiteSpace(text.Text) || nodeShapeIds.Contains(text.ShapeId) || ReferenceEquals(text, frame)) continue;
                var top = t.Y >= g.Y - t.Height && t.Y <= g.Y + Math.Max(t.Height * 2, g.Height * .1) && t.X >= g.X - 1 && t.X + t.Width <= g.X + g.Width * 1.02;
                var left = t.X >= g.X - t.Width && t.X <= g.X + g.Width * .2 && t.Y >= g.Y - 1 && t.Y + t.Height <= g.Y + g.Height * 1.02 && t.Height > t.Width;
                if (!top && !left) continue;
                var distance = Math.Abs(t.Y - g.Y) + Math.Abs(t.X - g.X);
                if (distance < best) { best = distance; title = text; }
            }
            if (title is null) return graph;
            lanes.Add((frame, FirstLine(title.Text), title.ShapeId));
        }
        if (lanes.Select(lane => lane.Title).Distinct(StringComparer.Ordinal).Count() != lanes.Count) return graph;

        var laneOfNode = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var centre = Centre(node.Geometry!);
            var index = -1;
            var area = double.MaxValue;
            for (var i = 0; i < lanes.Count; i++)
            {
                var g = lanes[i].Frame.Geometry!;
                if (centre.X < g.X || centre.X > g.X + g.Width || centre.Y < g.Y || centre.Y > g.Y + g.Height || g.Width * g.Height >= area) continue;
                (index, area) = (i, g.Width * g.Height);
            }
            if (index >= 0) laneOfNode[node.Id] = index;
        }
        if (laneOfNode.Count == 0) return graph;
        var usedLanes = laneOfNode.Values.Distinct().OrderBy(i => lanes[i].Frame.Geometry!.X).ThenBy(i => lanes[i].Frame.Geometry!.Y).ToArray();
        var groups = usedLanes.Select(i => new VisualGroup("lane_" + SafeVisualId(lanes[i].Frame.ShapeId), lanes[i].Title,
            laneOfNode.Where(pair => pair.Value == i).Select(pair => pair.Key).OrderBy(id => id, StringComparer.Ordinal).ToArray(), lanes[i].Title)).ToArray();
        foreach (var i in usedLanes) consumedTextShapeIds.Add(lanes[i].TitleShapeId);
        var laneNodes = graph.Nodes.Select(node => laneOfNode.TryGetValue(node.Id, out var i) ? node with { Lane = lanes[i].Title } : node).ToArray();
        return graph with { Nodes = laneNodes, Groups = groups };
    }
}
