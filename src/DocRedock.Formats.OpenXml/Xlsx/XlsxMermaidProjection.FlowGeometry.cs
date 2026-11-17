namespace DocRedock.Formats.OpenXml.Xlsx;

// Geometry rules for drawn flowcharts: swimlane containers with a header band, and the small text boxes that
// label a connector. As with the drawn sequence diagram, only positions and sizes are used.
internal static partial class XlsxMermaidProjection
{
    private enum LaneAxis { Columns, Rows }

    private sealed record DrawnLane(Shp Container, Shp Header, LaneAxis Axis)
    {
        public string Label => Header.Text;

        public bool Contains(Box box) => Axis == LaneAxis.Columns
            ? box.CenterX >= Container.Box.Left && box.CenterX < Container.Box.Right &&
              box.CenterY >= Container.Box.Top && box.CenterY <= Container.Box.Bottom
            : box.CenterY >= Container.Box.Top && box.CenterY < Container.Box.Bottom &&
              box.CenterX >= Container.Box.Left && box.CenterX <= Container.Box.Right;
    }

    /// <summary>Swimlanes drawn as a large text-less rectangle with a header band at its top (or left) edge.
    /// At least two lanes of the same direction that do not overlap, or none.</summary>
    private static List<DrawnLane> FindDrawnLanes(IReadOnlyList<XlsxDrawingShapeRecord> shapes, XlsxWorksheetMetrics? metrics)
    {
        var all = shapes.Where(shape => !shape.IsHidden && !IsLineLike(shape) && BoxOf(shape, metrics) is not null)
            .Select(shape => new Shp(shape, BoxOf(shape, metrics)!.Value)).ToList();
        var headers = all.Where(item => item.Text.Length > 0 && item.Shape.Geometry.Equals("rect", StringComparison.OrdinalIgnoreCase)).ToList();
        var lanes = new List<DrawnLane>();
        foreach (var container in all.Where(item => item.Text.Length == 0 &&
                                                    item.Shape.Geometry.Equals("rect", StringComparison.OrdinalIgnoreCase) &&
                                                    item.Box.Width > 0 && item.Box.Height > 0))
        {
            DrawnLane? lane = null;
            foreach (var header in headers)
            {
                var tolerance = Math.Max(60_000, Math.Min(header.Box.Width, header.Box.Height) * 0.1);
                if (Math.Abs(header.Box.Top - container.Box.Top) > tolerance || Math.Abs(header.Box.Left - container.Box.Left) > tolerance) continue;
                if (header.Box.Width >= container.Box.Width * 0.8 && header.Box.Width <= container.Box.Width * 1.05 &&
                    header.Box.Height <= container.Box.Height * 0.35)
                { lane = new DrawnLane(container, header, LaneAxis.Columns); break; }
                if (header.Box.Height >= container.Box.Height * 0.8 && header.Box.Height <= container.Box.Height * 1.05 &&
                    header.Box.Width <= container.Box.Width * 0.35)
                { lane = new DrawnLane(container, header, LaneAxis.Rows); break; }
            }
            // A lane holds something besides its header.
            if (lane is not null && all.Any(item => item.Text.Length > 0 && !ReferenceEquals(item, lane.Header) &&
                                                    lane.Contains(item.Box)))
                lanes.Add(lane);
        }
        if (lanes.Count < 2) return [];
        var axis = lanes.GroupBy(lane => lane.Axis).OrderByDescending(group => group.Count()).First().Key;
        lanes = lanes.Where(lane => lane.Axis == axis).ToList();
        lanes = axis == LaneAxis.Columns
            ? lanes.OrderBy(lane => lane.Container.Box.Left).ToList()
            : lanes.OrderBy(lane => lane.Container.Box.Top).ToList();
        for (var index = 1; index < lanes.Count; index++)
        {
            var (previous, current) = (lanes[index - 1].Container.Box, lanes[index].Container.Box);
            var overlap = axis == LaneAxis.Columns ? previous.Right - current.Left : previous.Bottom - current.Top;
            var extent = axis == LaneAxis.Columns ? current.Width : current.Height;
            if (overlap > extent * 0.1) return [];
        }
        return lanes.Count >= 2 ? lanes : [];
    }

    private static int LaneIndexOf(IReadOnlyList<DrawnLane> lanes, Box box)
    {
        for (var index = 0; index < lanes.Count; index++)
            if (lanes[index].Contains(box)) return index;
        return -1;
    }

    // A small text box with no outline or fill of its own, or a text box proper, is what labels a connector.
    private static bool LooksLikeConnectorLabel(Shp item) =>
        item.Shape.Geometry.Equals("rect", StringComparison.OrdinalIgnoreCase) &&
        (item.Shape.IsUnpainted || item.Shape.IsTextBox) &&
        item.Text.Length <= 40 && item.Box.Width <= 3_200_000 && item.Box.Height <= 700_000;

    /// <summary>Attaches each small label text box to the connector it sits on or beside: the nearest connector path
    /// within about a line of text, provided the box is not inside another node.</summary>
    private static Dictionary<string, XlsxDrawingShapeRecord> AttachConnectorLabels(
        IReadOnlyList<XlsxDrawingShapeRecord> shapes, IReadOnlyList<XlsxDrawingShapeRecord> nodeShapes,
        XlsxWorksheetMetrics? metrics)
    {
        var result = new Dictionary<string, XlsxDrawingShapeRecord>(StringComparer.Ordinal);
        var items = nodeShapes.Where(shape => BoxOf(shape, metrics) is not null)
            .Select(shape => new Shp(shape, BoxOf(shape, metrics)!.Value)).ToList();
        var candidates = items.Where(LooksLikeConnectorLabel).ToList();
        if (candidates.Count == 0) return result;
        var candidateIds = candidates.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var nodes = items.Where(item => !candidateIds.Contains(item.Id)).ToList();
        var nodeIds = nodes.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var connectors = shapes.Where(shape => shape.IsConnector && shape.StartConnectionId is not null &&
                                               shape.EndConnectionId is not null &&
                                               nodeIds.Contains(shape.StartConnectionId) && nodeIds.Contains(shape.EndConnectionId) &&
                                               string.IsNullOrWhiteSpace(shape.Text) && BoxOf(shape, metrics) is not null)
            .Select(shape =>
            {
                var paths = ConnectorPaths(shape, BoxOf(shape, metrics)!.Value);
                var points = paths.SelectMany(path => path).ToArray();
                return (Shape: shape, Paths: paths,
                    Bounds: new Box(points.Min(point => point.X), points.Min(point => point.Y), points.Max(point => point.X), points.Max(point => point.Y)));
            }).ToList();
        if (connectors.Count == 0) return result;
        var pairs = new List<(double Distance, double Tie, Shp Label, XlsxDrawingShapeRecord Connector)>();
        foreach (var label in candidates)
        {
            // Inside another node it is that node's own text, not a label.
            if (nodes.Any(node => node.Box.Left <= label.Box.CenterX && node.Box.Right >= label.Box.CenterX &&
                                  node.Box.Top <= label.Box.CenterY && node.Box.Bottom >= label.Box.CenterY)) continue;
            // About one line of text: the box is as high as its text, so its height stands for the font size.
            var tolerance = Math.Clamp(label.Box.Height * 1.2, 150_000, 450_000);
            foreach (var (connector, paths, bounds) in connectors)
            {
                // The path lies inside its bounds, so a box farther than the tolerance from them cannot be near it.
                if (label.Box.Left > bounds.Right + tolerance || label.Box.Right < bounds.Left - tolerance ||
                    label.Box.Top > bounds.Bottom + tolerance || label.Box.Bottom < bounds.Top - tolerance) continue;
                var distance = paths.Min(path => DistanceToPath(label.Box, path));
                if (distance > tolerance) continue;
                var start = paths[0][0];
                var tie = Math.Abs(start.X - label.Box.CenterX) + Math.Abs(start.Y - label.Box.CenterY);
                pairs.Add((distance, tie, label, connector));
            }
        }
        var usedLabels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in pairs.OrderBy(item => Math.Round(item.Distance / 20_000)).ThenBy(item => item.Tie))
        {
            if (result.ContainsKey(pair.Connector.Id) || !usedLabels.Add(pair.Label.Id)) continue;
            result[pair.Connector.Id] = pair.Label.Shape;
        }
        return result;
    }

    private readonly record struct Pt(double X, double Y);

    // The line(s) a connector may follow inside its bounds: its diagonal when straight, and both elbow routes
    // (horizontal first or vertical first) when bent, since the adjust values are not read.
    private static List<List<Pt>> ConnectorPaths(XlsxDrawingShapeRecord shape, Box box)
    {
        var start = new Pt(shape.FlipHorizontal ? box.Right : box.Left, shape.FlipVertical ? box.Bottom : box.Top);
        var end = new Pt(shape.FlipHorizontal ? box.Left : box.Right, shape.FlipVertical ? box.Top : box.Bottom);
        List<List<Pt>> paths;
        if (!IsBentLine(shape)) paths = [[start, end]];
        else if (shape.Geometry.StartsWith("bentConnector2", StringComparison.OrdinalIgnoreCase))
            paths = [[start, new Pt(end.X, start.Y), end], [start, new Pt(start.X, end.Y), end]];
        else
        {
            var middleX = (start.X + end.X) / 2d;
            var middleY = (start.Y + end.Y) / 2d;
            paths =
            [
                [start, new Pt(middleX, start.Y), new Pt(middleX, end.Y), end],
                [start, new Pt(start.X, middleY), new Pt(end.X, middleY), end],
            ];
        }
        // The extent is stored before the shape's rotation (a rotated elbow has its width and height swapped).
        var turn = shape.RotationDegrees % 360d;
        if (Math.Abs(turn) < 0.5) return paths;
        var (centerX, centerY) = (box.CenterX, box.CenterY);
        var (sin, cos) = Math.SinCos(turn * Math.PI / 180d);
        return paths.Select(path => path.Select(point =>
        {
            var (dx, dy) = (point.X - centerX, point.Y - centerY);
            return new Pt(centerX + dx * cos - dy * sin, centerY + dx * sin + dy * cos);
        }).ToList()).ToList();
    }

    private static double DistanceToPath(Box box, List<Pt> path)
    {
        var best = double.MaxValue;
        for (var index = 1; index < path.Count; index++)
        {
            var (from, to) = (path[index - 1], path[index]);
            var length = Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));
            var steps = Math.Max(1, (int)Math.Ceiling(length / 25_000d));
            for (var step = 0; step <= steps; step++)
            {
                var fraction = step / (double)steps;
                var x = from.X + (to.X - from.X) * fraction;
                var y = from.Y + (to.Y - from.Y) * fraction;
                var dx = Math.Max(Math.Max(box.Left - x, 0), x - box.Right);
                var dy = Math.Max(Math.Max(box.Top - y, 0), y - box.Bottom);
                best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
                if (best == 0) return 0;
            }
        }
        return best;
    }
}
