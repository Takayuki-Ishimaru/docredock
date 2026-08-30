using DocRedock.Core.Documents;

namespace DocRedock.VisualInference;

public enum VisualPrimitiveKind { NodeShape, Connector, DirectionalShape, Arrowhead, TextLabel, Group, Container, VectorPath, Image, UnknownVisual }
public enum VisualBoundaryKind { Rectangle, RoundedRectangle, Ellipse, Diamond, Parallelogram, Polygon, BoundingBox }
public readonly record struct VisualPoint(double X, double Y)
{
    public static VisualPoint operator +(VisualPoint point, VisualVector vector) => new(point.X + vector.X, point.Y + vector.Y);
    public static VisualVector operator -(VisualPoint a, VisualPoint b) => new(a.X - b.X, a.Y - b.Y);
}
public readonly record struct VisualVector(double X, double Y)
{
    public double Length => Math.Sqrt(X * X + Y * Y);
    public VisualVector Normalize() => Length <= 1e-12 ? default : new(X / Length, Y / Length);
    public static double Dot(VisualVector a, VisualVector b) => a.X * b.X + a.Y * b.Y;
}
public sealed record VisualRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width; public double Bottom => Y + Height;
    public VisualPoint Center => new(X + Width / 2, Y + Height / 2);
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height) && Width >= 0 && Height >= 0;
    public bool Contains(VisualPoint p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
    /// <summary>Shortest distance to the boundary, including from a point inside the rectangle.</summary>
    public double BoundaryDistanceTo(VisualPoint p)
    {
        if (Contains(p)) return Math.Min(Math.Min(p.X - X, Right - p.X), Math.Min(p.Y - Y, Bottom - p.Y));
        var dx = Math.Max(X - p.X, Math.Max(0, p.X - Right)); var dy = Math.Max(Y - p.Y, Math.Max(0, p.Y - Bottom)); return Math.Sqrt(dx * dx + dy * dy);
    }
    public double DistanceTo(VisualPoint p) => BoundaryDistanceTo(p);
    public bool Intersects(VisualRect other) => X <= other.Right && Right >= other.X && Y <= other.Bottom && Bottom >= other.Y;
}
public sealed record VisualCanvas(string Id, string PartUri, string? PartitionId, double Width, double Height, string CoordinateSpace, SourceAnchor? SourceAnchor = null)
{
    public bool IsFinite => double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;
    public double Diagonal => IsFinite ? Math.Sqrt(Width * Width + Height * Height) : 0;
}
public sealed record VisualIdentityAlias(string Kind, string Value);
public sealed record ArrowheadEvidence(bool Present = false, double? AngleDegrees = null, string? Kind = null, double Confidence = 0);
public sealed record VisualConnectorPath(IReadOnlyList<VisualPoint> Points, VisualVector? StartTangent = null, VisualVector? EndTangent = null, ArrowheadEvidence? StartArrowhead = null, ArrowheadEvidence? EndArrowhead = null, bool IsClosed = false, bool IsCurve = false, bool IsCompound = false)
{
    public VisualPoint Start => Points.Count == 0 ? default : Points[0]; public VisualPoint End => Points.Count == 0 ? default : Points[^1];
    public VisualVector StartDirection => (StartTangent ?? (Points.Count > 1 ? Points[1] - Points[0] : default)).Normalize();
    public VisualVector EndDirection => (EndTangent ?? (Points.Count > 1 ? Points[^1] - Points[^2] : default)).Normalize();
}
public abstract record VisualPrimitive(string Id, string CanvasId, VisualPrimitiveKind Kind, SourceAnchor Source, VisualRect? Bounds = null, string? NativeObjectId = null, string? NativeName = null, string? GroupId = null, string? Text = null, bool IsHidden = false, IReadOnlyDictionary<string,string>? Metadata = null, IReadOnlyList<VisualIdentityAlias>? Aliases = null);
public sealed record VisualNodePrimitive(string Id, string CanvasId, SourceAnchor Source, VisualRect? Bounds, VisualBoundaryKind BoundaryKind = VisualBoundaryKind.Rectangle, string? Text = null, string? GroupId = null, bool IsHidden = false, IReadOnlyDictionary<string,string>? Metadata = null, IReadOnlyList<VisualIdentityAlias>? Aliases = null) : VisualPrimitive(Id, CanvasId, VisualPrimitiveKind.NodeShape, Source, Bounds, GroupId: GroupId, Text: Text, IsHidden: IsHidden, Metadata: Metadata, Aliases: Aliases);
public sealed record VisualConnectorPrimitive(string Id, string CanvasId, SourceAnchor Source, VisualConnectorPath Path, string? NativeSourceAlias = null, string? NativeTargetAlias = null, string? GroupId = null, bool IsHidden = false, IReadOnlyDictionary<string,string>? Metadata = null, IReadOnlyList<VisualIdentityAlias>? Aliases = null) : VisualPrimitive(Id, CanvasId, VisualPrimitiveKind.Connector, Source, Path.Points.Count == 0 ? null : new VisualRect(Path.Points.Min(p => p.X), Path.Points.Min(p => p.Y), Path.Points.Max(p => p.X) - Path.Points.Min(p => p.X), Path.Points.Max(p => p.Y) - Path.Points.Min(p => p.Y)), GroupId: GroupId, IsHidden: IsHidden, Metadata: Metadata, Aliases: Aliases);
public sealed record VisualTextPrimitive(string Id, string CanvasId, SourceAnchor Source, VisualRect? Bounds, string Text, string? GroupId = null) : VisualPrimitive(Id, CanvasId, VisualPrimitiveKind.TextLabel, Source, Bounds, GroupId: GroupId, Text: Text);
public sealed record VisualPrimitiveDocument(string Id, DocumentFormatKind Format, IReadOnlyList<VisualCanvas> Canvases, IReadOnlyList<VisualPrimitive> Primitives, IReadOnlyList<VisualExtractionDiagnostic>? Diagnostics = null);
public sealed record VisualExtractionDiagnostic(string Code, string Message, string? PrimitiveId = null);
public readonly record struct Transform2D(double M11, double M12, double M21, double M22, double Dx, double Dy)
{
    public static Transform2D Identity => new(1, 0, 0, 1, 0, 0);
    public VisualPoint Apply(VisualPoint p) => new(p.X * M11 + p.Y * M21 + Dx, p.X * M12 + p.Y * M22 + Dy);
    public static Transform2D Translation(double x, double y) => new(1, 0, 0, 1, x, y);
    public static Transform2D Scale(double x, double y) => new(x, 0, 0, y, 0, 0);
    public static Transform2D Rotation(double degrees) { var radians = degrees * Math.PI / 180; return new(Math.Cos(radians), Math.Sin(radians), -Math.Sin(radians), Math.Cos(radians), 0, 0); }
    /// <summary>Composes column-vector transforms: <c>a * b</c> applies <c>b</c>, then <c>a</c>.</summary>
    public static Transform2D operator *(Transform2D a, Transform2D b) => new(a.M11*b.M11+a.M21*b.M12, a.M12*b.M11+a.M22*b.M12, a.M11*b.M21+a.M21*b.M22, a.M12*b.M21+a.M22*b.M22, a.M11*b.Dx+a.M21*b.Dy+a.Dx, a.M12*b.Dx+a.M22*b.Dy+a.Dy);
}
public static class GeometryMath
{
    public static double Distance(VisualPoint a, VisualPoint b) => (a - b).Length;
    public static double BoundaryDistanceTo(VisualPoint point, VisualRect rect, VisualBoundaryKind kind)
    {
        if (kind is VisualBoundaryKind.Ellipse or VisualBoundaryKind.Diamond)
        {
            var center = rect.Center;
            var dx = point.X - center.X; var dy = point.Y - center.Y;
            var halfWidth = Math.Max(rect.Width / 2, 1e-12); var halfHeight = Math.Max(rect.Height / 2, 1e-12);
            var radius = kind == VisualBoundaryKind.Ellipse
                ? Math.Sqrt((dx / halfWidth) * (dx / halfWidth) + (dy / halfHeight) * (dy / halfHeight))
                : Math.Abs(dx / halfWidth) + Math.Abs(dy / halfHeight);
            var scale = radius <= 1e-12 ? 1 : 1 / radius;
            var boundary = new VisualPoint(center.X + dx * scale, center.Y + dy * scale);
            return Distance(point, boundary);
        }
        return rect.BoundaryDistanceTo(point);
    }
    public static double AngleDegrees(VisualVector a, VisualVector b) { var d = a.Length * b.Length; return d <= 1e-12 ? 180 : Math.Acos(Math.Clamp(VisualVector.Dot(a, b) / d, -1, 1)) * 180 / Math.PI; }
    public static bool RayIntersectsRect(VisualPoint origin, VisualVector direction, VisualRect rect, out double distance)
    {
        distance = double.PositiveInfinity;
        direction = direction.Normalize();
        if (direction.Length == 0) return false;

        // Allocation-free slab checks: this runs for every candidate and intermediate-node probe.
        if (direction.X != 0)
        {
            var t = (rect.X - origin.X) / direction.X;
            var y = origin.Y + direction.Y * t;
            if (t >= 0 && y >= rect.Y && y <= rect.Bottom) distance = t;
            t = (rect.Right - origin.X) / direction.X;
            y = origin.Y + direction.Y * t;
            if (t >= 0 && y >= rect.Y && y <= rect.Bottom && t < distance) distance = t;
        }
        if (direction.Y != 0)
        {
            var t = (rect.Y - origin.Y) / direction.Y;
            var x = origin.X + direction.X * t;
            if (t >= 0 && x >= rect.X && x <= rect.Right && t < distance) distance = t;
            t = (rect.Bottom - origin.Y) / direction.Y;
            x = origin.X + direction.X * t;
            if (t >= 0 && x >= rect.X && x <= rect.Right && t < distance) distance = t;
        }
        return double.IsFinite(distance);
    }
    public static double DistanceToSegment(VisualPoint point, VisualPoint start, VisualPoint end, out double projection)
    { var segment = end-start; var squared = segment.X*segment.X+segment.Y*segment.Y; projection = squared <= 1e-12 ? 0 : Math.Clamp(VisualVector.Dot(point-start, segment)/squared, 0, 1); return Distance(point, start + new VisualVector(segment.X*projection, segment.Y*projection)); }
    /// <summary>Shortest distance between segment [start,end] and rect; 0 when the segment intersects or touches the rectangle.</summary>
    public static double DistanceToSegmentRect(VisualPoint start, VisualPoint end, VisualRect rect)
    {
        if (rect.Contains(start) || rect.Contains(end)) return 0;
        var direction = end - start;
        if (direction.Length > 1e-12 && RayIntersectsRect(start, direction, rect, out var hit) && hit <= direction.Length + 1e-9) return 0;
        var distance = Math.Min(rect.BoundaryDistanceTo(start), rect.BoundaryDistanceTo(end));
        foreach (var corner in new[] { new VisualPoint(rect.X, rect.Y), new VisualPoint(rect.Right, rect.Y), new VisualPoint(rect.X, rect.Bottom), new VisualPoint(rect.Right, rect.Bottom) })
            distance = Math.Min(distance, DistanceToSegment(corner, start, end, out _));
        return distance;
    }
}
public sealed record AdaptiveScale(double CanvasDiagonal, double MinorAxis, double NeighborGap, double ConnectorLength)
{
    public double SafeEndpointRadius => Math.Clamp(.45 * MinorAxis, .01 * CanvasDiagonal, .06 * CanvasDiagonal);
    public double BalancedEndpointRadius => Math.Clamp(.80 * MinorAxis, .015 * CanvasDiagonal, .10 * CanvasDiagonal);
    public double RayExtension => Math.Max(2 * MinorAxis, .12 * CanvasDiagonal);
    public double CorridorHalfWidth => Math.Max(.35 * MinorAxis, .01 * CanvasDiagonal);
}
