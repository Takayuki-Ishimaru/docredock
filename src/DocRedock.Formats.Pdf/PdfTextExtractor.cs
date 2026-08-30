using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.VisualInference;

namespace DocRedock.Formats.Pdf;

public sealed record PdfTextRegion(string Text, Geometry BoundingBox, int ReadingOrder);
public sealed record PdfPageText(
    int PageNumber,
    IReadOnlyList<PdfTextRegion> Regions,
    bool HasVectorContent = false,
    bool IsImageOnly = false)
{
    public string Text => string.Join("\n", Regions.OrderByDescending(region => region.BoundingBox.Y).ThenBy(region => region.BoundingBox.X).Select(region => region.Text));
}
public sealed record PdfExtractionResult(
    int PageCount,
    IReadOnlyList<PdfPageText> Pages,
    IReadOnlyList<string>? Diagnostics = null,
    IReadOnlyDictionary<int, VisualGraph>? VisualGraphs = null)
{
    public string Text => string.Join("\n\n", Pages.Select(page => page.Text));
}

public sealed record PdfExtractionOptions(
    long MaxInputBytes = 134_217_728,
    int MaxPages = 10_000,
    int MaxObjects = 200_000,
    long MaxExpandedStreamBytes = 268_435_456,
    TimeSpan? RegexTimeout = null,
    TimeSpan? VisualInferenceTimeout = null)
{
    public TimeSpan EffectiveRegexTimeout => RegexTimeout is { } value && value > TimeSpan.Zero
        ? value
        : TimeSpan.FromSeconds(1);
    public TimeSpan EffectiveVisualInferenceTimeout => VisualInferenceTimeout ?? TimeSpan.FromSeconds(5);
}

public sealed class PdfExtractionException : Exception
{
    public PdfExtractionException(string message) : base(message) { }
    public PdfExtractionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Conservative BCL-only extraction of text-showing PDF operators.</summary>
public static class PdfTextExtractor
{
    private const string PageMarkerPattern = @"/Type\s*/Page(?!s)\b";
    private const string ObjectPattern = @"\b\d+\s+\d+\s+obj\b";
    private const string TextStringPattern = @"\[(?:\s*(?:\((?:\\.|[^\\)])*\)|<[0-9A-Fa-f\s]+>|[-+]?\d+(?:\.\d+)?))*\s*\]|\((?:\\.|[^\\)])*\)|<[0-9A-Fa-f\s]+>";
    private const string LiteralPattern = @"\((?:\\.|[^\\)])*\)";
    private const string NumberPattern = @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)";

    public static PdfExtractionResult Extract(string path, PdfExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new PdfExtractionOptions();
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("PDF input was not found.", path);
        if (info.Length > options.MaxInputBytes) throw new PdfExtractionException($"PDF input exceeds the {options.MaxInputBytes}-byte limit.");
        return Extract(File.ReadAllBytes(path), options, cancellationToken);
    }

    public static PdfExtractionResult Extract(ReadOnlyMemory<byte> bytes, PdfExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PdfExtractionOptions();
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > options.MaxInputBytes) throw new PdfExtractionException($"PDF input exceeds the {options.MaxInputBytes}-byte limit.");
        var raw = bytes.ToArray();
        if (raw.Length < 5 || !raw.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) throw new PdfExtractionException("Input does not have a PDF header.");
        var latin = Encoding.Latin1.GetString(raw);
        if (!latin.Contains("%%EOF", StringComparison.Ordinal)) throw new PdfExtractionException("PDF end marker is missing.");
        // Object/page markers live outside streams. Excluding stream payloads avoids
        // running structural regular expressions over multi-megabyte embedded fonts
        // and images while retaining the original bounded stream parser below.
        var structure = StripStreamPayloads(latin);
        int pageCount;
        try
        {
            pageCount = Math.Max(1, Regex.Matches(structure, PageMarkerPattern, RegexOptions.Compiled, options.EffectiveRegexTimeout).Count);
            var objectCount = Regex.Matches(structure, ObjectPattern, RegexOptions.Compiled, options.EffectiveRegexTimeout).Count;
            if (objectCount > options.MaxObjects) throw new PdfExtractionException($"PDF object count exceeds the {options.MaxObjects}-object limit.");
        }
        catch (RegexMatchTimeoutException exception)
        {
            throw new PdfExtractionException("PDF structure matching exceeded its time limit.", exception);
        }
        if (pageCount > options.MaxPages) throw new PdfExtractionException($"PDF page count exceeds the {options.MaxPages}-page limit.");
        IReadOnlySet<int> contentObjectIds;
        try { contentObjectIds = ReadContentObjectIds(structure, options.EffectiveRegexTimeout); }
        catch (RegexMatchTimeoutException exception) { throw new PdfExtractionException("PDF content reference matching exceeded its time limit.", exception); }
        // Resolve the font encoding before reading page content.  A Type0 font
        // commonly stores glyph codes in the content stream and puts the actual
        // Unicode mapping in a separate ToUnicode CMap object.  Treating those
        // codes as Latin-1 is what turns Japanese text into punctuation.
        IReadOnlyDictionary<string, PdfToUnicodeMap> fontMaps;
        try { fontMaps = ReadFontMaps(raw, latin, structure, options); }
        catch (RegexMatchTimeoutException exception) { throw new PdfExtractionException("PDF font encoding matching exceeded its time limit.", exception); }
        var streams = ReadStreams(raw, latin, options, contentObjectIds).ToArray();
        var pageMap = ReadContentObjectPages(structure, options.EffectiveRegexTimeout);
        var pages = new List<PdfPageText>();
        var diagnostics = new List<string>();
        var visualGraphs = new Dictionary<int, VisualGraph>();
        var streamPage = 1;
        foreach (var pageGroup in streams.GroupBy(stream => pageMap.TryGetValue(stream.ObjectId ?? -1, out var mapped) ? mapped : streamPage++).OrderBy(group => group.Key))
        {
            var stream = string.Join("\n", pageGroup.Select(item => item.Payload));
            var regions = ParseOperators(stream, options, fontMaps);
            var vector = ContainsVectorOperators(stream);
            var image = ContainsImageOperator(stream);
            var pageNumber = Math.Min(pageGroup.Key, pageCount);
            var imageOnly = image && regions.Count == 0;
            if (!imageOnly && regions.Count == 0 && !vector) continue;
            var vectorPlaceholder = false;
            if (vector && regions.Count == 0)
            {
                regions.Add(new PdfTextRegion("[PDF visual content: vector drawing; semantic reconstruction unavailable]", new Geometry("pdf-user-space", 0, 0, 1, 1), 0));
                vectorPlaceholder = true;
            }
            if (imageOnly)
            {
                diagnostics.Add($"PdfRasterizerUnavailable: PDF page {pageNumber} contains image-only content; rasterizer/OCR may be required.");
                regions.Add(new PdfTextRegion($"[PDF page {pageNumber} contains image-only content; rasterizer/OCR unavailable]", new Geometry("pdf-user-space", 0, 0, 1, 1), 0));
            }
            cancellationToken.ThrowIfCancellationRequested();
            VisualGraph? visualGraph = vector
                ? BuildVisualGraph(pageNumber, stream, regions, diagnostics, options.EffectiveVisualInferenceTimeout, cancellationToken)
                : null;
            if (vector && visualGraph is not null)
            {
                var accounting = visualGraph.Accounting;
                var partial = accounting.UnresolvedEdges > 0 || accounting.FallbackPaths > 0 || accounting.Diagnostics > 0;
                if (vectorPlaceholder && !partial)
                    regions.RemoveAll(region => region.Text.StartsWith("[PDF visual content:", StringComparison.Ordinal));
                if (partial)
                    diagnostics.Add($"VisualSemanticProjectionUnavailable: PDF page {pageNumber} contains partial vector topology.");
                visualGraphs[pageNumber] = visualGraph;
            }
            pages.Add(new PdfPageText(pageNumber, SortReadingOrder(regions), vector, imageOnly));
        }
        if (pages.Count == 0)
        {
            diagnostics.Add("PdfRasterizerUnavailable: PDF page 1 has no native text. This build does not include a PDF rasterizer; rasterizer/OCR may be required.");
            pages.Add(new PdfPageText(1, [new PdfTextRegion("[PDF page 1 contains image-only content; rasterizer/OCR unavailable]", new Geometry("pdf-user-space", 0, 0, 1, 1), 0)], false, true));
        }
        for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
        {
            if (pages.Any(page => page.PageNumber == pageNumber)) continue;
            diagnostics.Add($"PdfRasterizerUnavailable: PDF page {pageNumber} has no native text. This build does not include a PDF rasterizer; rasterizer/OCR may be required.");
            pages.Add(new PdfPageText(pageNumber, [new PdfTextRegion($"[PDF page {pageNumber} contains image-only content; rasterizer/OCR unavailable]", new Geometry("pdf-user-space", 0, 0, 1, 1), 0)], false, true));
        }
        pages.Sort((left, right) => left.PageNumber.CompareTo(right.PageNumber));
        return new PdfExtractionResult(pageCount, pages, diagnostics, visualGraphs);
    }

    private static VisualGraph BuildVisualGraph(int pageNumber, string content, IReadOnlyList<PdfTextRegion> regions,
        List<string> diagnostics, TimeSpan? inferenceTimeout, CancellationToken cancellationToken)
    {
        var allowGeometryInference = VisualInferenceContext.Current != VisualInferenceMode.NativeOnly;
        var nodes = new List<VisualNode>();
        var edges = new List<VisualEdge>();
        var paths = new List<VisualPath>();
        var graphDiagnostics = new List<VisualDiagnostic>();
        var state = new Stack<(double A, double B, double C, double D, double E, double F)>();
        var ctm = (A: 1d, B: 0d, C: 0d, D: 1d, E: 0d, F: 0d);
        var operands = new List<double>();
        var current = new List<VisualPathPoint>();
        var pendingClosedSubpaths = new List<IReadOnlyList<VisualPathPoint>>();
        var arrowheadTips = new List<VisualPathPoint>();
        var triangleCandidates = new List<(IReadOnlyList<VisualPathPoint> Points, Geometry Geometry, string PathId)>();
        var unlabelledClosedCandidates = new List<(Geometry Geometry, string PathId)>();
        var provisionalUnlabelledNodeIds = new HashSet<string>(StringComparer.Ordinal);
        var assignedLabelRegions = new HashSet<int>();
        var duplicatePathIds = new HashSet<string>(StringComparer.Ordinal);
        var arrowheadPathIds = new HashSet<string>(StringComparer.Ordinal);
        var mergedArrowheadPathIds = new HashSet<string>(StringComparer.Ordinal);
        var unresolvedPathIds = new HashSet<string>(StringComparer.Ordinal);
        var closed = false;
        var curveSeen = false;
        var anchor = new SourceAnchor("pdf", $"pdf:page:{pageNumber}", [new AnchorLocator("visual_path", pageNumber.ToString())]);

        foreach (var token in Tokens(content))
        {
            if (double.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                operands.Add(number);
                continue;
            }
            switch (token)
            {
                case "q": state.Push(ctm); operands.Clear(); break;
                case "Q": if (state.Count > 0) ctm = state.Pop(); operands.Clear(); break;
                case "cm":
                    if (operands.Count >= 6)
                    {
                        var m = (A: operands[^6], B: operands[^5], C: operands[^4], D: operands[^3], E: operands[^2], F: operands[^1]);
                        ctm = (ctm.A * m.A + ctm.C * m.B, ctm.B * m.A + ctm.D * m.B,
                            ctm.A * m.C + ctm.C * m.D, ctm.B * m.C + ctm.D * m.D,
                            ctm.A * m.E + ctm.C * m.F + ctm.E, ctm.B * m.E + ctm.D * m.F + ctm.F);
                    }
                    break;
                case "m" when operands.Count >= 2:
                    if (current.Count > 1)
                    {
                        if (closed) pendingClosedSubpaths.Add(current.ToArray());
                        else RetainSubpath();
                    }
                    current.Clear(); current.Add(Transform(operands[^2], operands[^1])); closed = false; curveSeen = false; break;
                case "l" when operands.Count >= 2:
                    current.Add(Transform(operands[^2], operands[^1])); break;
                case "c" when operands.Count >= 6:
                    current.Add(Transform(operands[^6], operands[^5])); current.Add(Transform(operands[^4], operands[^3]));
                    current.Add(Transform(operands[^2], operands[^1])); curveSeen = true; break;
                case "v" when operands.Count >= 4:
                    current.Add(Transform(operands[^4], operands[^3])); current.Add(Transform(operands[^2], operands[^1])); curveSeen = true; break;
                case "y" when operands.Count >= 4:
                    current.Add(Transform(operands[^4], operands[^3])); current.Add(Transform(operands[^2], operands[^1]));
                    current.Add(Transform(operands[^2], operands[^1])); curveSeen = true; break;
                case "re" when operands.Count >= 4:
                    if (current.Count > 1)
                    {
                        if (closed) pendingClosedSubpaths.Add(current.ToArray());
                        else RetainSubpath();
                    }
                    var x = operands[^4]; var y = operands[^3]; var w = operands[^2]; var h = operands[^1];
                    current.Clear(); current.Add(Transform(x, y)); current.Add(Transform(x + w, y));
                    current.Add(Transform(x + w, y + h)); current.Add(Transform(x, y + h)); current.Add(Transform(x, y)); closed = true; break;
                case "h": if (current.Count > 1) { current.Add(current[0]); closed = true; } break;
                case "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "n":
                    if (current.Count > 1)
                    {
                        // `re` starts a new subpath; promote prior closed rectangles only when the
                        // following paint operator confirms that the compound path is painted.
                        foreach (var subpath in pendingClosedSubpaths)
                            AddPaintedClosedSubpath(subpath);
                        pendingClosedSubpaths.Clear();
                        var points = current.ToArray();
                        var isClosed = closed || token is "s" or "b" or "b*" or "f" or "F" or "f*" or "B" or "B*" ||
                            (points[0].X == points[^1].X && points[0].Y == points[^1].Y);
                        var painted = token is not "n";
                        var isStroke = token is "S" or "s" or "B" or "B*" or "b" or "b*";
                        var minX = points.Min(point => point.X); var minY = points.Min(point => point.Y);
                        var maxX = points.Max(point => point.X); var maxY = points.Max(point => point.Y);
                        var pathId = $"pdf_p{pageNumber}_path{paths.Count + 1}";
                        paths.Add(new VisualPath(pathId, points, new Geometry("pdf-user-space", minX, minY, maxX - minX, maxY - minY), anchor,
                            curveSeen ? 0.45 : 0.9, curveSeen || !isClosed || !painted, SourceNodeId: null));
                        if (isClosed && painted) AddClosedNode(points);
                        else if (isStroke)
                        {
                            // Edge labels are assigned only after all closed paths have claimed
                            // their text regions as node labels. PDF content streams routinely
                            // paint a connector before its endpoint rectangles and text.
                            edges.Add(new VisualEdge($"pdf_p{pageNumber}_e{edges.Count + 1}", null, null,
                                null, VisualEdgeResolution.Unresolved, Direction: "undirected",
                                Geometry: new Geometry("pdf-user-space", minX, minY, maxX - minX, maxY - minY), Confidence: 0.2,
                                Path: points, SourceAnchor: anchor, EdgeDirection: VisualEdgeDirection.Undirected));
                            diagnostics.Add($"VisualConnectorUnresolved: PDF page {pageNumber} edge endpoint is ambiguous.");
                            graphDiagnostics.Add(Diag("VisualConnectorUnresolved", "Edge endpoint is ambiguous.", 0.2));
                        }
                        if (curveSeen) { diagnostics.Add($"VisualPathPartial: PDF page {pageNumber} curve path retained as fallback."); graphDiagnostics.Add(Diag("VisualPathPartial", "Curve path retained as fallback.", 0.45)); }
                    }
                    current.Clear(); operands.Clear(); closed = false; curveSeen = false; break;
                default: operands.Clear(); break;
            }
        }
        foreach (var subpath in pendingClosedSubpaths)
            RetainClosedSubpath(subpath);
        var labelledAreas = nodes.Where(node => node.Geometry is not null)
            .Select(node => Math.Abs(node.Geometry!.Width * node.Geometry.Height))
            .Where(area => area > 0).OrderBy(area => area).ToArray();
        var medianLabelledArea = labelledAreas.Length == 0 ? 0 : labelledAreas[labelledAreas.Length / 2];
        var visualBounds = paths.Select(path => path.Geometry).Where(geometry => geometry is not null).Cast<Geometry>()
            .Concat(regions.Select(region => region.BoundingBox)).ToArray();
        var pageDiagonalSquared = visualBounds.Length == 0 ? 1 :
            Math.Pow(visualBounds.Max(geometry => geometry.X + geometry.Width) - visualBounds.Min(geometry => geometry.X), 2) +
            Math.Pow(visualBounds.Max(geometry => geometry.Y + geometry.Height) - visualBounds.Min(geometry => geometry.Y), 2);
        // PDF user space can be scaled by a CTM. Classify small unlabelled closed paths
        // relative to the labelled-node population and page extent, never by a fixed size.
        var decorativeAreaThreshold = Math.Max(medianLabelledArea * .05, pageDiagonalSquared * .00005);
        foreach (var candidate in unlabelledClosedCandidates)
        {
            var area = Math.Abs(candidate.Geometry.Width * candidate.Geometry.Height);
            if (area <= decorativeAreaThreshold) continue;
            var node = new VisualNode($"pdf_p{pageNumber}_n{nodes.Count + 1}", $"Vector node {nodes.Count + 1}",
                VisualNodeKind.Generic, Geometry: candidate.Geometry, SourceAnchor: anchor);
            nodes.Add(node);
            provisionalUnlabelledNodeIds.Add(node.Id);
        }
        foreach (var candidate in triangleCandidates)
        {
            var tip = ArrowTip(candidate.Points);
            var nearest = edges.Where(edge => edge.Path is { Count: >= 2 })
                .Select(edge => (Edge: edge,
                    EndpointDistance: Math.Min(Distance(edge.Path![0], tip), Distance(edge.Path[^1], tip)),
                    Length: edge.Path.Zip(edge.Path.Skip(1), Distance).Sum()))
                .OrderBy(item => item.EndpointDistance).FirstOrDefault();
            var markerSize = Math.Sqrt(candidate.Geometry.Width * candidate.Geometry.Width +
                candidate.Geometry.Height * candidate.Geometry.Height);
            var isArrowhead = nearest.Edge is not null && nearest.Length > 0 &&
                markerSize <= nearest.Length * .30 &&
                nearest.EndpointDistance <= Math.Max(markerSize * 1.5, nearest.Length * .08);
            var pathIndex = paths.FindIndex(path => path.Id == candidate.PathId);
            if (isArrowhead)
            {
                arrowheadTips.Add(tip);
                arrowheadPathIds.Add(candidate.PathId);
                if (pathIndex >= 0) paths[pathIndex] = paths[pathIndex] with { Confidence = .3, IsFallback = true };
            }
            else if (Math.Abs(candidate.Geometry.Width * candidate.Geometry.Height) > decorativeAreaThreshold)
            {
                var node = new VisualNode($"pdf_p{pageNumber}_n{nodes.Count + 1}", $"Vector node {nodes.Count + 1}",
                    VisualNodeKind.Generic, Geometry: candidate.Geometry, SourceAnchor: anchor);
                nodes.Add(node);
                provisionalUnlabelledNodeIds.Add(node.Id);
                if (pathIndex >= 0) paths[pathIndex] = paths[pathIndex] with { Confidence = .75, IsFallback = false };
            }
            else if (pathIndex >= 0)
            {
                paths[pathIndex] = paths[pathIndex] with { Confidence = .35, IsFallback = true };
            }
        }

        var edgeLabelChoices = new List<EdgeLabelCandidate>();
        var labelRegions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (region, regionIndex) in regions.Select((region, index) => (region, index)))
        {
            if (assignedLabelRegions.Contains(regionIndex) || string.IsNullOrWhiteSpace(region.Text)) continue;
            var labelId = "region:" + regionIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var bestScore = double.NegativeInfinity;
            string? bestEdgeId = null;
            var center = new VisualPoint(region.BoundingBox.X + region.BoundingBox.Width / 2,
                region.BoundingBox.Y + region.BoundingBox.Height / 2);
            foreach (var edge in edges.Where(edge => edge.Path is { Count: >= 2 }))
            {
                for (var pointIndex = 1; pointIndex < edge.Path!.Count; pointIndex++)
                {
                    var start = edge.Path[pointIndex - 1];
                    var end = edge.Path[pointIndex];
                    var dx = end.X - start.X; var dy = end.Y - start.Y;
                    var length = Math.Sqrt(dx * dx + dy * dy);
                    if (length <= 1e-9) continue;
                    var projection = ((center.X - start.X) * dx + (center.Y - start.Y) * dy) / (length * length);
                    if (projection is < -.1 or > 1.1) continue;
                    var distance = GeometryMath.DistanceToSegment(center,
                        new VisualPoint(start.X, start.Y), new VisualPoint(end.X, end.Y), out _);
                    var tolerance = Math.Max(Math.Max(region.BoundingBox.Width, region.BoundingBox.Height) * 1.5, length * .12);
                    var score = 1 - distance / Math.Max(1, tolerance) - Math.Abs(projection - .5) * .15;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestEdgeId = edge.Id;
                    }
                }
            }
            if (bestEdgeId is null || bestScore <= 0) continue;
            edgeLabelChoices.Add(new EdgeLabelCandidate(labelId, bestEdgeId, bestScore));
            labelRegions[labelId] = regionIndex;
        }
        var deferredLabels = EdgeLabelAssigner.Assign(edgeLabelChoices);
        foreach (var (labelId, edgeId) in deferredLabels)
        {
            if (!labelRegions.TryGetValue(labelId, out var regionIndex)) continue;
            var edgeIndex = edges.FindIndex(edge => edge.Id == edgeId);
            if (edgeIndex < 0) continue;
            edges[edgeIndex] = edges[edgeIndex] with { Label = regions[regionIndex].Text };
            assignedLabelRegions.Add(regionIndex);
        }
        foreach (var labelId in labelRegions.Keys.Where(labelId => !deferredLabels.ContainsKey(labelId)))
        {
            diagnostics.Add($"VisualEdgeLabelUnresolved: PDF page {pageNumber} text remained independent.");
            graphDiagnostics.Add(Diag("VisualEdgeLabelUnresolved", "Text could not be uniquely assigned to an edge.", 0.2));
        }

        // Resolve all endpoints together after every path has been visited. This keeps CTM
        // transforms, adaptive scale, cluster boundaries, and global ambiguity handling in
        // the same shared inference engine used by the Office adapters.
        if (allowGeometryInference && nodes.Count > 0 && edges.Count > 0)
        {
            var canvasId = $"pdf-page-{pageNumber}";
            var nodePrimitives = nodes.Where(node => node.Geometry is not null).Select(node =>
                (VisualPrimitive)new VisualNodePrimitive(node.Id, canvasId, node.SourceAnchor ?? anchor,
                    new VisualRect(node.Geometry!.X, node.Geometry.Y, node.Geometry.Width, node.Geometry.Height),
                    Text: node.Label)).ToArray();
            var connectorPrimitives = edges.Where(edge => edge.Path is { Count: >= 2 }).Select(edge =>
                (VisualPrimitive)new VisualConnectorPrimitive(edge.Id, canvasId, edge.SourceAnchor ?? anchor,
                    new VisualConnectorPath(edge.Path!.Select(point => new VisualPoint(point.X, point.Y)).ToArray()))).ToArray();
            var primitives = nodePrimitives.Concat(connectorPrimitives).ToArray();
            var primitiveBounds = primitives.Select(item => item.Bounds).Where(item => item is not null).Cast<VisualRect>().ToArray();
            var minCanvasX = primitiveBounds.Select(item => item.X).DefaultIfEmpty(0).Min();
            var minCanvasY = primitiveBounds.Select(item => item.Y).DefaultIfEmpty(0).Min();
            var maxCanvasX = primitiveBounds.Select(item => item.Right).DefaultIfEmpty(1).Max();
            var maxCanvasY = primitiveBounds.Select(item => item.Bottom).DefaultIfEmpty(1).Max();
            var document = new VisualPrimitiveDocument($"pdf-page-{pageNumber}", DocumentFormatKind.Pdf,
                [new VisualCanvas(canvasId, $"pdf:page:{pageNumber}", $"page-{pageNumber}",
                    Math.Max(1, maxCanvasX - minCanvasX), Math.Max(1, maxCanvasY - minCanvasY), "pdf-user-space", anchor)],
                primitives);
            // A PDF page's content stream is already one analysis unit: every primitive built
            // above shares this single VisualCanvas, unlike a DOCX/PPTX/XLSX canvas, which can
            // legitimately hold several unrelated diagrams far apart on the same sheet/slide.
            // Do not let DiagramClusterer's proximity/touch heuristics fragment that unit
            // further. Those heuristics union a node with a connector only when the node
            // touches one of the connector's two path *endpoints*, or sits within a generic
            // center-distance radius of another already-unioned shape; a node whose box lies
            // along the *middle* of a straight connector -- touching neither endpoint -- can
            // satisfy neither test (a purely horizontal or vertical connector also has a
            // zero-length minor axis, which collapses that generic radius to nothing) and is
            // split into its own single-node cluster. FindIntermediateNodeIds only ever sees
            // the nodes inside the connector's own cluster, so a node stranded that way becomes
            // invisible to the corridor check that exists specifically to stop the flanking
            // nodes from resolving a "skip" edge across it that the page never drew. Passing one
            // explicit whole-canvas cluster keeps every recognized node on the page visible to
            // that check for every connector on the page -- the same explicit-cluster shape
            // DocxAdapter and XlsxMermaidProjection already pass, rather than leaving it to
            // SoftConnectionEngine's internal per-call default.
            var clusters = new[] { new DiagramCluster(canvasId,
                primitives.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray()) };
            SoftConnectionResult inference;
            try
            {
                using var inferenceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (inferenceTimeout is { } timeout)
                {
                    if (timeout <= TimeSpan.Zero) inferenceCts.Cancel();
                    else inferenceCts.CancelAfter(timeout);
                }
                inference = new SoftConnectionEngine().Infer(document, clusters,
                    new SoftConnectionOptions(VisualInferenceContext.Current), inferenceCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                const string message = "PDF visual inference exceeded its configured time budget; all vector connectors remain fallback geometry.";
                diagnostics.Add($"VisualInferenceTimeout: PDF page {pageNumber} {message}");
                graphDiagnostics.Add(new VisualDiagnostic("VisualInferenceTimeout", message,
                    Fallback: "vector connectors retained as visual fallback",
                    Remedy: "increase VisualInferenceTimeout or simplify the diagram",
                    Format: "pdf", PartUri: $"pdf:page:{pageNumber}", PartitionId: $"page-{pageNumber}",
                    SourceObjectType: "inference", Confidence: 0));
                inference = new SoftConnectionResult([], [], []);
            }
            foreach (var pair in inference.Resolved)
            {
                var edgeIndex = edges.FindIndex(edge => edge.Id == pair.ConnectorId);
                if (edgeIndex < 0 || pair.SourceId is null || pair.TargetId is null) continue;
                var edge = edges[edgeIndex];
                var physicalStartId = pair.Direction == ConnectionDirection.Reverse ? pair.TargetId : pair.SourceId;
                var physicalEndId = pair.Direction == ConnectionDirection.Reverse ? pair.SourceId : pair.TargetId;
                var selected = inference.Candidates.Where(candidate => candidate.ConnectorId == pair.ConnectorId &&
                        (candidate.IsStart && candidate.NodeId == physicalStartId || !candidate.IsStart && candidate.NodeId == physicalEndId))
                    .ToArray();
                var margin = selected.Select(candidate => candidate.Features.CandidateMargin).DefaultIfEmpty(0).Min();
                var confidence = pair.Confidence == ConnectionConfidence.Medium ? .75 : Math.Max(.85, pair.Score);
                edges[edgeIndex] = edge with
                {
                    SourceId = pair.SourceId,
                    TargetId = pair.TargetId,
                    Resolution = VisualEdgeResolution.GeometryInferred,
                    Confidence = confidence,
                    Evidence = new VisualConnectionEvidence("pdf-soft-geometry", pair.Confidence.ToString(), pair.Score,
                        SecondBestScore: Math.Max(0, pair.Score - margin), CandidateMargin: margin,
                        BoundaryDistanceNormalized: selected.Select(candidate => candidate.Features.BoundaryDistanceNormalized).DefaultIfEmpty(0).Max(),
                        RayIntersects: selected.All(candidate => candidate.Features.RayIntersects),
                        RayFirstHit: selected.All(candidate => candidate.Features.RayFirstHit),
                        AngularDeviationDegrees: selected.Select(candidate => candidate.Features.AngularDeviationDegrees).DefaultIfEmpty(0).Max(),
                        PerpendicularOffsetNormalized: selected.Select(candidate => candidate.Features.PerpendicularOffsetNormalized).DefaultIfEmpty(0).Max(),
                        IntermediateNodeCount: selected.Select(candidate => candidate.Features.IntermediateNodeCount).DefaultIfEmpty(0).Max(),
                        ArrowheadEvidence: "none", ClusterId: pair.ClusterId,
                        RejectedCandidateIds: pair.RejectedCandidateIds)
                };
                var deferredDiagnostic = graphDiagnostics.FindIndex(item => item.Code == "VisualConnectorUnresolved" &&
                    item.Message.Contains("endpoint", StringComparison.OrdinalIgnoreCase));
                if (deferredDiagnostic >= 0) graphDiagnostics.RemoveAt(deferredDiagnostic);
                var deferredGlobal = diagnostics.FindIndex(message =>
                    message.StartsWith($"VisualConnectorUnresolved: PDF page {pageNumber} edge endpoint", StringComparison.Ordinal));
                if (deferredGlobal >= 0) diagnostics.RemoveAt(deferredGlobal);
            }
        }
        // A small closed triangle touching a shaft is an arrowhead, not a node.
        // Promote the shaft to directed only when that evidence is unambiguous.
        foreach (var tip in arrowheadTips)
        {
            var matching = edges.Select((edge, index) => (edge, index))
                .Where(item => item.edge.Path is { Count: >= 2 } &&
                    (Distance(item.edge.Path![^1], tip) <= 18 || Distance(item.edge.Path[0], tip) <= 18))
                .OrderBy(item => Math.Min(Distance(item.edge.Path![^1], tip), Distance(item.edge.Path[0], tip)))
                .ToArray();
            if (matching.Length != 1) continue;
            var match = matching[0];
            var edge = match.edge;
            var atEnd = Distance(edge.Path![^1], tip) <= Distance(edge.Path[0], tip);
            if (!atEnd && edge.SourceId is not null && edge.TargetId is not null)
                edge = edge with { SourceId = edge.TargetId, TargetId = edge.SourceId };
            edges[match.index] = edge with
            {
                Direction = "directed",
                EdgeDirection = VisualEdgeDirection.Directed,
                Evidence = (edge.Evidence ?? new VisualConnectionEvidence("pdf-arrowhead-geometry", "High", edge.Confidence ?? .75,
                    ClusterId: $"page-{pageNumber}")) with { ArrowheadEvidence = atEnd ? "end" : "start" }
            };
        }
        // Merge an open, two-segment V adjacent to a shaft into one directed edge.
        // A standalone V has no qualifying shaft and remains a fallback/decorative path.
        for (var shaftIndex = 0; shaftIndex < edges.Count; shaftIndex++)
        {
            var shaft = edges[shaftIndex];
            if (shaft.Path is not { Count: >= 2 }) continue;
            var shaftLength = shaft.Path.Zip(shaft.Path.Skip(1), Distance).Sum();
            if (shaftLength <= 0 || shaft.SourceId is null || shaft.TargetId is null) continue;
            foreach (var endpoint in new[] { (Point: shaft.Path[0], AtStart: true), (Point: shaft.Path[^1], AtStart: false) })
            {
                var decoration = edges.Select((edge, index) => (edge, index))
                    .Where(item => item.index != shaftIndex && item.edge.Path is { Count: >= 3 })
                    .Select(item => (item.edge, item.index, points: item.edge.Path!, length: item.edge.Path!.Zip(item.edge.Path!.Skip(1), Distance).Sum()))
                    .Where(item => item.length > 0 && item.length <= shaftLength * .75 &&
                        IsVDecoration(item.points, endpoint.Point, shaftLength,
                            endpoint.AtStart
                                ? new VisualVector(shaft.Path[0].X - shaft.Path[^1].X, shaft.Path[0].Y - shaft.Path[^1].Y)
                                : new VisualVector(shaft.Path[^1].X - shaft.Path[0].X, shaft.Path[^1].Y - shaft.Path[0].Y)))
                    .OrderBy(item => Distance(item.points.First(), endpoint.Point)).FirstOrDefault();
                if (decoration.edge is null) continue;
                var source = endpoint.AtStart ? shaft.TargetId : shaft.SourceId;
                var target = endpoint.AtStart ? shaft.SourceId : shaft.TargetId;
                edges[shaftIndex] = shaft with
                {
                    SourceId = source, TargetId = target, Direction = "directed", EdgeDirection = VisualEdgeDirection.Directed,
                    Evidence = (shaft.Evidence ?? new VisualConnectionEvidence("pdf-v-arrowhead-geometry", "High", shaft.Confidence ?? .8,
                        ClusterId: $"page-{pageNumber}")) with { ArrowheadEvidence = endpoint.AtStart ? "start" : "end" }
                };
                var decorationPath = paths.FirstOrDefault(path => ReferenceEquals(path.Points, decoration.edge.Path));
                if (decorationPath is not null) mergedArrowheadPathIds.Add(decorationPath.Id);
                edges.RemoveAt(decoration.index);
                if (decoration.index < shaftIndex) shaftIndex--;
                break;
            }
        }

        // A textless closed path is only a semantic node if it participated in a resolved
        // relation. All others remain paths so decorative marks cannot leak into Mermaid.
        var connectedNodeIds = edges.Where(edge => edge.SourceId is not null && edge.TargetId is not null)
            .SelectMany(edge => new[] { edge.SourceId!, edge.TargetId! }).ToHashSet(StringComparer.Ordinal);
        foreach (var node in nodes.Where(node => provisionalUnlabelledNodeIds.Contains(node.Id) && !connectedNodeIds.Contains(node.Id)).ToArray())
        {
            nodes.Remove(node);
            var pathIndex = paths.FindIndex(path => path.Geometry == node.Geometry);
            if (pathIndex >= 0) paths[pathIndex] = paths[pathIndex] with { IsFallback = true, Confidence = .35 };
        }
        var sourceItems = new List<VisualSourceItem>();
        foreach (var path in paths)
        {
            var edge = edges.FirstOrDefault(candidate => candidate.Path is not null && ReferenceEquals(candidate.Path, path.Points));
            var node = nodes.FirstOrDefault(candidate => candidate.Geometry == path.Geometry);
            var itemId = path.Id;
            if (duplicatePathIds.Contains(path.Id))
            {
                var canonical = paths.FirstOrDefault(candidate => candidate.Id != path.Id && candidate.Geometry == path.Geometry);
                sourceItems.Add(canonical is not null
                    ? new VisualSourceItem(itemId, VisualSourceItemKind.VectorPath, VisualDisposition.SuppressedDuplicate,
                        DuplicateOfSourceItemId: canonical.Id, Reason: "duplicate closed path canonicalized", SourceAnchor: path.SourceAnchor)
                    : new VisualSourceItem(itemId, VisualSourceItemKind.VectorPath, VisualDisposition.VisualFallback,
                        FallbackPathId: path.Id, Reason: "duplicate closed path retained without canonical reference", SourceAnchor: path.SourceAnchor));
            }
            else if (arrowheadPathIds.Contains(path.Id) || mergedArrowheadPathIds.Contains(path.Id))
            {
                var related = edges.FirstOrDefault(candidate => candidate.Path is { Count: >= 2 } &&
                    arrowheadTips.Any(tip => Distance(candidate.Path![^1], tip) <= 18 || Distance(candidate.Path[0], tip) <= 18));
                var relatedPath = paths.FirstOrDefault(candidate => related?.Path is not null && ReferenceEquals(candidate.Points, related.Path));
                sourceItems.Add(relatedPath is not null
                    ? new VisualSourceItem(itemId, VisualSourceItemKind.VectorPath, VisualDisposition.SuppressedDuplicate,
                        DuplicateOfSourceItemId: relatedPath.Id, Reason: "arrowhead attached to shaft edge; not a node", SourceAnchor: path.SourceAnchor)
                    : new VisualSourceItem(itemId, VisualSourceItemKind.VectorPath, VisualDisposition.VisualFallback,
                        FallbackPathId: path.Id, Reason: "arrowhead direction could not be resolved", SourceAnchor: path.SourceAnchor));
            }
            else if (edge is not null && edge.SourceId is not null && edge.TargetId is not null)
                sourceItems.Add(new VisualSourceItem(itemId, VisualSourceItemKind.VectorPath, VisualDisposition.ProjectedEdge,
                    ProjectedEdgeId: edge.Id, SourceAnchor: path.SourceAnchor));
            else if (node is not null)
                sourceItems.Add(new VisualSourceItem(itemId, VisualSourceItemKind.VectorPath, VisualDisposition.ProjectedNode,
                    ProjectedNodeId: node.Id, SourceAnchor: path.SourceAnchor));
            else if (path.IsFallback)
                sourceItems.Add(new VisualSourceItem(itemId, VisualSourceItemKind.VectorPath, VisualDisposition.VisualFallback,
                    FallbackPathId: path.Id, Reason: unresolvedPathIds.Contains(path.Id) ? "ambiguous label assignment; vector box retained as fallback" : "vector path retained as fallback", SourceAnchor: path.SourceAnchor));
            else
                sourceItems.Add(new VisualSourceItem(itemId, VisualSourceItemKind.VectorPath, VisualDisposition.IgnoredDecorative,
                    Reason: "non-semantic decorative vector path", SourceAnchor: path.SourceAnchor));
        }
        var graph = new VisualGraph($"pdf-page-{pageNumber}-visual", nodes, edges, graphDiagnostics, "LR", Paths: paths,
            SourceItems: sourceItems);
        return graph with { Quality = VisualGraphValidator.ComputeQuality(graph) };

        IEnumerable<string> Tokens(string value)
        {
            for (var i = 0; i < value.Length;)
            {
                if (value[i] == '%') { while (i < value.Length && value[i] is not '\r' and not '\n') i++; continue; }
                if (value[i] == '(') { var depth = 1; i++; while (i < value.Length && depth > 0) { if (value[i] == '\\') i += Math.Min(2, value.Length - i); else if (value[i++] == '(') depth++; else if (value[i - 1] == ')') depth--; } continue; }
                if (value[i] == '<') { i++; if (i < value.Length && value[i] == '<') { i++; continue; } while (i < value.Length && value[i++] != '>') { } continue; }
                if (value[i] == '/') { i++; while (i < value.Length && !char.IsWhiteSpace(value[i]) && !"()<>[]{}/%".Contains(value[i])) i++; continue; }
                if (char.IsWhiteSpace(value[i])) { i++; continue; }
                var start = i++; while (i < value.Length && !char.IsWhiteSpace(value[i]) && !"()<>[]{}/%".Contains(value[i])) i++;
                yield return value[start..i];
            }
        }
        VisualPathPoint Transform(double x, double y) => new(ctm.A * x + ctm.C * y + ctm.E, ctm.B * x + ctm.D * y + ctm.F);
        static double Distance(VisualPathPoint left, VisualPathPoint right) => Math.Sqrt(Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));
        VisualDiagnostic Diag(string code, string message, double? confidence = null) => new(code, message,
            Format: "pdf", PartUri: $"pdf:page:{pageNumber}", PartitionId: $"page-{pageNumber}", Confidence: confidence);

        void RetainSubpath()
        {
            var minX = current.Min(point => point.X); var minY = current.Min(point => point.Y);
            var maxX = current.Max(point => point.X); var maxY = current.Max(point => point.Y);
            paths.Add(new VisualPath($"pdf_p{pageNumber}_subpath{paths.Count + 1}", current.ToArray(),
                new Geometry("pdf-user-space", minX, minY, maxX - minX, maxY - minY), anchor,
                curveSeen ? 0.25 : 0.35, IsFallback: true, SourceNodeId: null));
            graphDiagnostics.Add(Diag("VisualPathPartial", "Unpainted PDF subpath retained as fallback.", 0.35));
        }

        void RetainClosedSubpath(IReadOnlyList<VisualPathPoint> subpath)
        {
            var minX = subpath.Min(point => point.X); var minY = subpath.Min(point => point.Y);
            var maxX = subpath.Max(point => point.X); var maxY = subpath.Max(point => point.Y);
            paths.Add(new VisualPath($"pdf_p{pageNumber}_subpath{paths.Count + 1}", subpath,
                new Geometry("pdf-user-space", minX, minY, maxX - minX, maxY - minY), anchor,
                0.35, IsFallback: true, SourceNodeId: null));
            graphDiagnostics.Add(Diag("VisualPathPartial", "Unpainted PDF subpath retained as fallback.", 0.35));
        }

        void AddPaintedClosedSubpath(IReadOnlyList<VisualPathPoint> subpath)
        {
            var minX = subpath.Min(point => point.X); var minY = subpath.Min(point => point.Y);
            var maxX = subpath.Max(point => point.X); var maxY = subpath.Max(point => point.Y);
            paths.Add(new VisualPath($"pdf_p{pageNumber}_path{paths.Count + 1}", subpath,
                new Geometry("pdf-user-space", minX, minY, maxX - minX, maxY - minY), anchor,
                0.9, IsFallback: false, SourceNodeId: null));
            AddClosedNode(subpath);
        }

        void AddClosedNode(IReadOnlyList<VisualPathPoint> subpath)
        {
            var minX = subpath.Min(point => point.X); var minY = subpath.Min(point => point.Y);
            var maxX = subpath.Max(point => point.X); var maxY = subpath.Max(point => point.Y);
            var width = maxX - minX; var height = maxY - minY;
            var geometry = new Geometry("pdf-user-space", minX, minY, width, height);
            // Canonicalize repeated paint operations for the same visual box.
            if (nodes.Any(node => IoU(node.Geometry, geometry) >= 0.90))
            {
                if (paths.Count > 0) duplicatePathIds.Add(paths[^1].Id);
                return;
            }
            var labelCandidate = regions.Select((region, index) => (region, index, score: LabelScore(region.BoundingBox, geometry)))
                .Where(item => item.score > 0 && !assignedLabelRegions.Contains(item.index))
                .OrderByDescending(item => item.score).ThenBy(item => item.region.ReadingOrder).ToArray();
            var ambiguousLabel = labelCandidate.Length > 1 && labelCandidate[0].score - labelCandidate[1].score < 0.15;
            if (ambiguousLabel)
            {
                if (paths.Count > 0)
                {
                    unresolvedPathIds.Add(paths[^1].Id);
                    paths[^1] = paths[^1] with { IsFallback = true };
                }
                graphDiagnostics.Add(Diag("VisualNodeLabelMissing", "Text region assignment was ambiguous; vector box retained as fallback.", 0.2));
                return;
            }
            var hasLabel = labelCandidate.Length > 0;
            var label = hasLabel ? labelCandidate[0].region.Text : null;
            if (hasLabel) assignedLabelRegions.Add(labelCandidate[0].index);
            // Closed triangles are arrowhead evidence, regardless of document scale. Other
            // closed shapes remain candidate nodes; fixed absolute size cutoffs would make
            // connection inference change under a PDF CTM scale transform.
            if (string.IsNullOrWhiteSpace(label) && IsTriangle(subpath))
            {
                if (paths.Count == 0 || paths[^1].Geometry != geometry)
                    paths.Add(new VisualPath($"pdf_p{pageNumber}_decorative{paths.Count + 1}", subpath, geometry, anchor, 0.3, IsFallback: true));
                triangleCandidates.Add((subpath, geometry, paths[^1].Id));
                return;
            }
            if (string.IsNullOrWhiteSpace(label))
            {
                unlabelledClosedCandidates.Add((geometry, paths[^1].Id));
                return;
            }
            nodes.Add(new VisualNode($"pdf_p{pageNumber}_n{nodes.Count + 1}", label, VisualNodeKind.Generic,
                Geometry: geometry, SourceAnchor: anchor));
        }

        // A text region should only "win" a label slot when it is actually near the shape it
        // would label. Without a floor, 1/(1+centerDistance) stays positive for any finite
        // distance, so a bare, unrelated text region anywhere on the page could still out-score
        // "no candidate" and get absorbed as a node's label -- or, worse, as an arrowhead
        // triangle's label, which routes the triangle away from IsTriangle's arrowhead-evidence
        // branch below (see AddClosedNode) and turns it into a spurious semantic node carrying
        // meaning the source document never expressed. Gate acceptance on real proximity: either
        // the label box overlaps the shape at all, or its center falls within
        // LabelDistanceGateFactor times the larger of the two diagonals. That bound is relative
        // to the shapes themselves (matching the pageDiagonalSquared/decorativeAreaThreshold
        // convention above), so it does not change under a PDF CTM scale transform, and it
        // leaves genuinely unassociated text unassigned rather than inventing a label for it.
        // 3.5 (not the smaller ~1.5 first tried) is the calibrated value: with two rectangle
        // nodes plus a small arrowhead triangle sharing a page, and text regions comparable in
        // size to the node they label, a tighter floor started rejecting one of two similarly-far
        // *unclaimed* candidates for the triangle while keeping the other -- turning what used to
        // be a correctly-detected tie (both candidates weak and near-equal, see the ambiguous-
        // label check below) into an artificial single "winner" for R3_S0_7's inverted paint
        // order. 3.5 keeps both of that test's candidates on equal footing (so the pre-existing
        // ambiguity check still resolves it) while still rejecting text that is astronomically far
        // (page-scale-and-beyond) from the shape, which is the defect this gate exists to close.
        static double LabelScore(Geometry label, Geometry node)
        {
            const double LabelDistanceGateFactor = 3.5;
            var left = Math.Max(label.X, node.X); var right = Math.Min(label.X + label.Width, node.X + node.Width);
            var bottom = Math.Max(label.Y, node.Y); var top = Math.Min(label.Y + label.Height, node.Y + node.Height);
            var overlap = Math.Max(0, right - left) * Math.Max(0, top - bottom);
            var labelArea = Math.Max(1, label.Width * label.Height);
            var centerDistance = Math.Sqrt(Math.Pow(label.X + label.Width / 2 - (node.X + node.Width / 2), 2) +
                Math.Pow(label.Y + label.Height / 2 - (node.Y + node.Height / 2), 2));
            if (overlap <= 0)
            {
                var nodeDiagonal = Math.Sqrt(node.Width * node.Width + node.Height * node.Height);
                var labelDiagonal = Math.Sqrt(label.Width * label.Width + label.Height * label.Height);
                if (centerDistance > LabelDistanceGateFactor * Math.Max(nodeDiagonal, labelDiagonal)) return 0;
            }
            return overlap / labelArea + 1d / (1d + centerDistance);
        }

        static double IoU(Geometry? left, Geometry right)
        {
            if (left is null) return 0;
            var intersection = Math.Max(0, Math.Min(left.X + left.Width, right.X + right.Width) - Math.Max(left.X, right.X)) *
                Math.Max(0, Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y));
            var union = Math.Max(1, left.Width * left.Height + right.Width * right.Height - intersection);
            return intersection / union;
        }

        static bool IsVDecoration(IReadOnlyList<VisualPathPoint> points, VisualPathPoint shaftEndpoint,
            double shaftLength, VisualVector shaftDirection)
        {
            if (points.Count < 3) return false;
            var apexIndex = Enumerable.Range(0, points.Count).OrderBy(index => Distance(points[index], shaftEndpoint)).First();
            if (Distance(points[apexIndex], shaftEndpoint) > Math.Max(18, shaftLength * .12)) return false;
            var first = points[(apexIndex + 1) % points.Count];
            var second = points[(apexIndex + points.Count - 1) % points.Count];
            var ax = first.X - points[apexIndex].X; var ay = first.Y - points[apexIndex].Y;
            var bx = second.X - points[apexIndex].X; var by = second.Y - points[apexIndex].Y;
            var firstLength = Math.Sqrt(ax * ax + ay * ay);
            var secondLength = Math.Sqrt(bx * bx + by * by);
            var product = firstLength * secondLength;
            if (product <= 0) return false;
            var direction = shaftDirection.Normalize();
            if (direction.Length <= 0) return false;
            var firstProjection = (ax * direction.X + ay * direction.Y) / firstLength;
            var secondProjection = (bx * direction.X + by * direction.Y) / secondLength;
            var openingCosine = (ax * bx + ay * by) / product;
            var cross = Math.Abs(ax * by - ay * bx) / product;
            return cross > .25 && openingCosine > 0 &&
                firstProjection < -.15 && secondProjection < -.15;
        }

        static bool IsTriangle(IReadOnlyList<VisualPathPoint> points)
        {
            var distinct = points.Distinct().Count();
            // Closed triangles contain the repeated start point, therefore they
            // have exactly three unique vertices. Rectangles/circles are never
            // treated as arrowhead evidence merely because they are small.
            return distinct == 3;
        }

        static VisualPathPoint ArrowTip(IReadOnlyList<VisualPathPoint> points)
        {
            var minX = points.Min(point => point.X); var maxX = points.Max(point => point.X);
            var minY = points.Min(point => point.Y); var maxY = points.Max(point => point.Y);
            return points.OrderByDescending(point =>
                Math.Min(Math.Abs(point.X - minX), Math.Abs(point.X - maxX)) +
                Math.Min(Math.Abs(point.Y - minY), Math.Abs(point.Y - maxY))).First();
        }
    }


    private static string StripStreamPayloads(string value)
    {
        var output = new StringBuilder(Math.Min(value.Length, 1_048_576));
        var cursor = 0;
        while (cursor < value.Length)
        {
            var stream = value.IndexOf("stream", cursor, StringComparison.Ordinal);
            if (stream < 0) { output.Append(value, cursor, value.Length - cursor); break; }
            output.Append(value, cursor, stream - cursor + "stream".Length);
            var payload = stream + "stream".Length;
            if (payload < value.Length && value[payload] == '\r') payload++;
            if (payload < value.Length && value[payload] == '\n') payload++;
            var end = value.IndexOf("endstream", payload, StringComparison.Ordinal);
            if (end < 0) { output.Append("\n"); break; }
            output.Append("\nendstream");
            cursor = end + "endstream".Length;
        }
        return output.ToString();
    }

    private static IReadOnlyDictionary<int, int> ReadContentObjectPages(string structure, TimeSpan timeout)
    {
        var result = new Dictionary<int, int>();
        var pageNumber = 0;
        foreach (Match page in Regex.Matches(structure, @"(?<pageId>\d+)\s+\d+\s+obj\b(?<body>.*?)(?=\bendobj\b)", RegexOptions.Singleline, timeout))
        {
            var body = page.Groups["body"].Value;
            if (!Regex.IsMatch(body, @"/Type\s*/Page\b", RegexOptions.None, timeout)) continue;
            pageNumber++;
            foreach (Match scalar in Regex.Matches(body, @"/Contents\s+(?<id>\d+)\s+\d+\s+R\b", RegexOptions.None, timeout))
                if (int.TryParse(scalar.Groups["id"].Value, out var id)) result[id] = pageNumber;
            foreach (Match array in Regex.Matches(body, @"/Contents\s*\[(?<refs>[^\]]*)\]", RegexOptions.None, timeout))
                foreach (Match reference in Regex.Matches(array.Groups["refs"].Value, @"(?<id>\d+)\s+\d+\s+R\b", RegexOptions.None, timeout))
                    if (int.TryParse(reference.Groups["id"].Value, out var id)) result[id] = pageNumber;
        }
        return result;
    }

    private static IReadOnlySet<int> ReadContentObjectIds(string structure, TimeSpan timeout)
    {
        var ids = new HashSet<int>();
        foreach (Match match in Regex.Matches(structure, @"/Contents\s+(?<id>\d+)\s+\d+\s+R\b", RegexOptions.None, timeout))
            if (int.TryParse(match.Groups["id"].Value, out var id)) ids.Add(id);
        foreach (Match array in Regex.Matches(structure, @"/Contents\s*\[(?<refs>[^\]]*)\]", RegexOptions.None, timeout))
            foreach (Match reference in Regex.Matches(array.Groups["refs"].Value, @"(?<id>\d+)\s+\d+\s+R\b", RegexOptions.None, timeout))
                if (int.TryParse(reference.Groups["id"].Value, out var id)) ids.Add(id);
        return ids;
    }

    private static IEnumerable<(int? ObjectId, string Payload)> ReadStreams(byte[] bytes, string latin, PdfExtractionOptions options, IReadOnlySet<int> contentObjectIds)
    {
        var marker = Encoding.ASCII.GetBytes("stream");
        var endMarker = Encoding.ASCII.GetBytes("endstream");
        var offset = 0;
        while ((offset = IndexOf(bytes, marker, offset)) >= 0)
        {
            var start = offset + marker.Length;
            if (start < bytes.Length && bytes[start] == '\r') start++;
            if (start < bytes.Length && bytes[start] == '\n') start++;
            var end = IndexOf(bytes, endMarker, start);
            if (end < 0) throw new PdfExtractionException("PDF stream is missing endstream.");
            var payload = bytes[start..end];
            while (payload.Length > 0 && (payload[^1] == '\r' || payload[^1] == '\n')) payload = payload[..^1];
            var header = ReadContainingObjectHeader(latin, offset, 2048);
            int? objectId = TryReadContainingObjectId(header, options.EffectiveRegexTimeout, out var parsedObjectId)
                ? parsedObjectId
                : null;
            if (contentObjectIds.Count > 0 && (objectId is null || !contentObjectIds.Contains(objectId.Value)))
            {
                offset = end + endMarker.Length;
                continue;
            }
            payload = DecodeFilteredStream(payload, header, options.MaxExpandedStreamBytes);
            yield return (objectId, Encoding.Latin1.GetString(payload));
            offset = end + endMarker.Length;
        }
    }

    private static bool TryReadContainingObjectId(string header, TimeSpan timeout, out int objectId)
    {
        objectId = 0;
        var matches = Regex.Matches(header, @"(?<id>\d+)\s+\d+\s+obj\b", RegexOptions.None, timeout);
        return matches.Count > 0 && int.TryParse(matches[^1].Groups["id"].Value, out objectId);
    }

    private static bool ContainsVectorOperators(string content) =>
        ContainsOperatorToken(content, static token => token is "m" or "l" or "c" or "v" or "y" or "re" or "S" or "s" or "f" or "F" or "B" or "b");

    private static bool ContainsImageOperator(string content) =>
        ContainsOperatorToken(content, static token => token == "Do");

    private static bool ContainsOperatorToken(string content, Func<string, bool> predicate)
    {
        for (var index = 0; index < content.Length;)
        {
            if (content[index] == '%')
            {
                while (index < content.Length && content[index] is not '\r' and not '\n') index++;
                continue;
            }
            if (content[index] == '(')
            {
                var depth = 1;
                index++;
                while (index < content.Length && depth > 0)
                {
                    if (content[index] == '\\') index += Math.Min(2, content.Length - index);
                    else if (content[index++] == '(') depth++;
                    else if (content[index - 1] == ')') depth--;
                }
                continue;
            }
            if (content[index] == '<')
            {
                index++;
                if (index < content.Length && content[index] == '<') { index++; continue; }
                while (index < content.Length && content[index++] != '>') { }
                continue;
            }
            if (content[index] == '/')
            {
                index++;
                while (index < content.Length && !char.IsWhiteSpace(content[index]) && !"()<>[]{}/%".Contains(content[index])) index++;
                continue;
            }
            if (char.IsLetter(content[index]))
            {
                var start = index++;
                while (index < content.Length && char.IsLetter(content[index])) index++;
                if (predicate(content[start..index])) return true;
                continue;
            }
            index++;
        }
        return false;
    }

    private static List<PdfTextRegion> ParseOperators(string content, PdfExtractionOptions options,
        IReadOnlyDictionary<string, PdfToUnicodeMap> fontMaps)
    {
        var regions = new List<PdfTextRegion>();
        var x = 0d;
        var y = 0d;
        var operatorCursor = 0;
        var actualTextCursor = 0;
        var transform = PdfMatrix.Identity;
        var graphicsStack = new Stack<PdfMatrix>();
        string? currentFont = null;
        Regex textString;
        try { textString = new Regex(TextStringPattern, RegexOptions.Compiled | RegexOptions.NonBacktracking, options.EffectiveRegexTimeout); }
        catch (ArgumentOutOfRangeException exception) { throw new PdfExtractionException("Invalid PDF regex timeout.", exception); }
        try
        {
            foreach (Match match in textString.Matches(content))
            {
                var operatorContext = content[operatorCursor..match.Index];
                var actualTextContext = content[actualTextCursor..match.Index];
                operatorCursor = match.Index + match.Length;
                UpdateGraphicsTransform(operatorContext, options.EffectiveRegexTimeout, graphicsStack, ref transform);
                var fonts = Regex.Matches(operatorContext, @"/(?<font>[A-Za-z][A-Za-z0-9_.+-]*)\s+(?:[-+]?\d+(?:\.\d*)?|\.\d+)\s+Tf\b", RegexOptions.None, options.EffectiveRegexTimeout);
                if (fonts.Count > 0) currentFont = fonts[^1].Groups["font"].Value;
                if (Regex.IsMatch(operatorContext, @"\bBT\b", RegexOptions.NonBacktracking, options.EffectiveRegexTimeout)) (x, y) = (0, 0);
                foreach (Match td in Regex.Matches(operatorContext, $@"({NumberPattern})\s+({NumberPattern})\s+Td", RegexOptions.NonBacktracking, options.EffectiveRegexTimeout))
                    if (double.TryParse(td.Groups[1].Value, out var dx) && double.TryParse(td.Groups[2].Value, out var dy)) (x, y) = (x + dx, y + dy);
                foreach (Match tm in Regex.Matches(operatorContext, $@"({NumberPattern})\s+({NumberPattern})\s+({NumberPattern})\s+({NumberPattern})\s+({NumberPattern})\s+({NumberPattern})\s+Tm", RegexOptions.NonBacktracking, options.EffectiveRegexTimeout))
                    if (double.TryParse(tm.Groups[5].Value, out var tx) && double.TryParse(tm.Groups[6].Value, out var ty)) (x, y) = (tx, ty);
                var after = content[(match.Index + match.Length)..];
                var operatorMatch = Regex.Match(after, @"^\s*(?<op>Tj|TJ)\b", RegexOptions.None, options.EffectiveRegexTimeout);
                // Dictionary strings (notably /ActualText) may occur between two
                // shown strings. Keep them in the context for the next Tj/TJ.
                if (!operatorMatch.Success) continue;
                var actualText = FindActiveActualText(actualTextContext, options.EffectiveRegexTimeout);
                var text = actualText ?? (match.Value[0] == '['
                    ? string.Join("", Regex.Matches(match.Value, LiteralPattern + @"|<[0-9A-Fa-f\s]+>", RegexOptions.None, options.EffectiveRegexTimeout)
                        .Select(item => DecodeString(item.Value, FindFontMap(fontMaps, currentFont))))
                    : DecodeString(match.Value, FindFontMap(fontMaps, currentFont)));
                if (text.Length > 0)
                {
                    var point = transform.Apply(x, y);
                    var right = transform.Apply(x + text.Length * 6, y);
                    var top = transform.Apply(x, y + 12);
                    var width = Math.Max(1, Math.Sqrt(Math.Pow(right.X - point.X, 2) + Math.Pow(right.Y - point.Y, 2)));
                    var height = Math.Max(1, Math.Sqrt(Math.Pow(top.X - point.X, 2) + Math.Pow(top.Y - point.Y, 2)));
                    regions.Add(new PdfTextRegion(text,
                        new Geometry("pdf-user-space", point.X, point.Y, width, height), regions.Count));
                }
                x += text.Length * 6;
                actualTextCursor = match.Index + match.Length;
            }
        }
        catch (RegexMatchTimeoutException exception)
        {
            throw new PdfExtractionException("PDF text operator matching exceeded its time limit.", exception);
        }
        return regions;
    }

    private static void UpdateGraphicsTransform(
        string content,
        TimeSpan timeout,
        Stack<PdfMatrix> stack,
        ref PdfMatrix transform)
    {
        var pattern = $@"(?<!\S)(?<state>q|Q)(?!\S)|(?<a>{NumberPattern})\s+(?<b>{NumberPattern})\s+(?<c>{NumberPattern})\s+(?<d>{NumberPattern})\s+(?<e>{NumberPattern})\s+(?<f>{NumberPattern})\s+cm\b";
        foreach (Match match in Regex.Matches(content, pattern, RegexOptions.None, timeout))
        {
            var state = match.Groups["state"].Value;
            if (state == "q")
            {
                stack.Push(transform);
                continue;
            }
            if (state == "Q")
            {
                if (stack.TryPop(out var restored)) transform = restored;
                continue;
            }
            if (!TryNumber(match.Groups["a"], out var a) ||
                !TryNumber(match.Groups["b"], out var b) ||
                !TryNumber(match.Groups["c"], out var c) ||
                !TryNumber(match.Groups["d"], out var d) ||
                !TryNumber(match.Groups["e"], out var e) ||
                !TryNumber(match.Groups["f"], out var f))
                continue;
            transform = transform.Concat(new PdfMatrix(a, b, c, d, e, f));
        }
    }

    private static bool TryNumber(Group group, out double value) =>
        double.TryParse(group.Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);

    private readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
    {
        public static PdfMatrix Identity { get; } = new(1, 0, 0, 1, 0, 0);

        public (double X, double Y) Apply(double x, double y) =>
            (A * x + C * y + E, B * x + D * y + F);

        public PdfMatrix Concat(PdfMatrix next) => new(
            A * next.A + C * next.B,
            B * next.A + D * next.B,
            A * next.C + C * next.D,
            B * next.C + D * next.D,
            A * next.E + C * next.F + E,
            B * next.E + D * next.F + F);
    }

    private static string? FindActiveActualText(string preceding, TimeSpan timeout)
    {
        var matches = Regex.Matches(preceding,
            @"/ActualText\s*(?<value><[0-9A-Fa-f\s]+>|\((?:\\.|[^\\)])*\))",
            RegexOptions.NonBacktracking, timeout);
        if (matches.Count == 0) return null;
        var actual = matches[^1];
        if (preceding.LastIndexOf("EMC", StringComparison.Ordinal) > actual.Index) return null;
        var token = actual.Groups["value"].Value;
        if (!token.StartsWith('<')) return DecodeString(token);
        var hex = Regex.Replace(token[1..^1], @"\s+", string.Empty, RegexOptions.CultureInvariant);
        if (hex.Length % 2 != 0) hex += "0";
        var bytes = Convert.FromHexString(hex);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        return bytes.Length % 2 == 0 ? Encoding.BigEndianUnicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
    }

    private static IReadOnlyList<PdfTextRegion> SortReadingOrder(IEnumerable<PdfTextRegion> regions)
    {
        var ordered = regions.OrderByDescending(region => region.BoundingBox.Y)
            .ThenBy(region => region.BoundingBox.X).ThenBy(region => region.ReadingOrder).ToArray();
        var lines = new List<PdfTextRegion>();
        foreach (var region in ordered)
        {
            if (lines.Count == 0 || Math.Abs(lines[^1].BoundingBox.Y - region.BoundingBox.Y) > Math.Max(1, region.BoundingBox.Height * 0.35))
            {
                lines.Add(region);
                continue;
            }
            var previous = lines[^1];
            var previousRight = previous.BoundingBox.X + previous.BoundingBox.Width;
            var gap = region.BoundingBox.X - previousRight;
            var overlapTolerance = Math.Max(previous.BoundingBox.Height, region.BoundingBox.Height) * 0.5;
            if (gap < -overlapTolerance)
            {
                // Text drawn in nested coordinate frames (for example table cells)
                // can share a local Y while their estimated horizontal bounds overlap.
                // Keep those runs as separate regions instead of collapsing a page
                // into one unreadable line.
                lines.Add(region);
                continue;
            }
            var separator = gap > Math.Max(previous.BoundingBox.Height, region.BoundingBox.Height) * 1.5 ? " " : string.Empty;
            var right = Math.Max(previousRight, region.BoundingBox.X + region.BoundingBox.Width);
            lines[^1] = new PdfTextRegion(previous.Text + separator + region.Text,
                new Geometry(previous.BoundingBox.CoordinateSpace, previous.BoundingBox.X,
                    Math.Max(previous.BoundingBox.Y, region.BoundingBox.Y), right - previous.BoundingBox.X,
                    Math.Max(previous.BoundingBox.Height, region.BoundingBox.Height)), previous.ReadingOrder);
        }
        return lines;
    }

    private static PdfToUnicodeMap? FindFontMap(IReadOnlyDictionary<string, PdfToUnicodeMap> maps, string? font) =>
        font is not null && maps.TryGetValue(font, out var map) ? map : null;

    private static string DecodeString(string value, PdfToUnicodeMap? map = null)
    {
        if (value.StartsWith("<", StringComparison.Ordinal))
        {
            var hex = Regex.Replace(value[1..^1], @"\s+", string.Empty, RegexOptions.CultureInvariant);
            if (hex.Length % 2 != 0) hex += "0";
            var hexBytes = new byte[hex.Length / 2];
            for (var i = 0; i < hexBytes.Length; i++) if (byte.TryParse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var item)) hexBytes[i] = item;
            return map?.Decode(hexBytes) ?? Encoding.Latin1.GetString(hexBytes);
        }
        var inner = value.Length >= 2 ? value[1..^1] : value;
        var raw = new List<byte>(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != (char)92 || i + 1 >= inner.Length) { raw.Add((byte)inner[i]); continue; }
            var escaped = inner[++i];
            if (escaped is >= '0' and <= '7')
            {
                var octal = new StringBuilder(3).Append(escaped);
                while (octal.Length < 3 && i + 1 < inner.Length && inner[i + 1] is >= '0' and <= '7')
                    octal.Append(inner[++i]);
                raw.Add(Convert.ToByte(octal.ToString(), 8));
                continue;
            }
            raw.Add((byte)(escaped switch { 'n' => (char)10, 'r' => (char)13, 't' => (char)9, 'b' => (char)8, 'f' => (char)12, '(' => (char)40, ')' => (char)41, _ => escaped }));
        }
        var bytes = raw.ToArray();
        return map?.Decode(bytes) ?? Encoding.Latin1.GetString(bytes);
    }

    private static IReadOnlyDictionary<string, PdfToUnicodeMap> ReadFontMaps(byte[] bytes, string latin, string structure,
        PdfExtractionOptions options)
    {
        var maps = new Dictionary<string, PdfToUnicodeMap>(StringComparer.Ordinal);
        // Object dictionaries are deliberately parsed from the already bounded
        // Latin-1 view.  The CMap payload itself is read from the matching stream
        // object below, so arbitrary binary font data never enters this regex.
        var objectBodies = new Dictionary<int, string>();
        try
        {
            foreach (Match match in Regex.Matches(structure, @"(?m)(?<id>\d+)\s+\d+\s+obj\b(?<body>.*?)endobj\b", RegexOptions.Singleline, options.EffectiveRegexTimeout))
                if (int.TryParse(match.Groups["id"].Value, out var id)) objectBodies[id] = match.Groups["body"].Value;
        }
        catch (RegexMatchTimeoutException exception)
        {
            throw new PdfExtractionException("PDF font dictionary matching exceeded its time limit.", exception);
        }
        if (objectBodies.Count == 0) return maps;

        var cmapByObject = new Dictionary<int, PdfToUnicodeMap>();
        foreach (var stream in ReadObjectStreams(bytes, latin, options))
        {
            if (stream.ObjectId is not { } objectId) continue;
            var cmap = PdfToUnicodeMap.Parse(stream.Payload, options.EffectiveRegexTimeout);
            if (cmap.Count > 0) cmapByObject[objectId] = cmap;
        }
        foreach (var font in objectBodies)
        {
            var toUnicode = Regex.Match(font.Value, @"/ToUnicode\s+(?<id>\d+)\s+\d+\s+R\b", RegexOptions.None, options.EffectiveRegexTimeout);
            if (!toUnicode.Success || !int.TryParse(toUnicode.Groups["id"].Value, out var cmapObject) || !cmapByObject.TryGetValue(cmapObject, out var map)) continue;
            foreach (Match alias in Regex.Matches(structure, $@"(?<alias>/[A-Za-z][A-Za-z0-9_.+-]*)\s+{font.Key}\s+\d+\s+R\b", RegexOptions.None, options.EffectiveRegexTimeout))
                maps[alias.Groups["alias"].Value[1..]] = map;
            var fontName = Regex.Match(font.Value, @"/Name\s+/(?<alias>[A-Za-z][A-Za-z0-9_.+-]*)", RegexOptions.None, options.EffectiveRegexTimeout);
            if (fontName.Success) maps[fontName.Groups["alias"].Value] = map;
        }
        return maps;
    }

    private static IEnumerable<PdfObjectStream> ReadObjectStreams(byte[] bytes, string latin, PdfExtractionOptions options)
    {
        var marker = Encoding.ASCII.GetBytes("stream");
        var endMarker = Encoding.ASCII.GetBytes("endstream");
        var offset = 0;
        while ((offset = IndexOf(bytes, marker, offset)) >= 0)
        {
            var start = offset + marker.Length;
            if (start < bytes.Length && bytes[start] == '\r') start++;
            if (start < bytes.Length && bytes[start] == '\n') start++;
            var end = IndexOf(bytes, endMarker, start);
            if (end < 0) throw new PdfExtractionException("PDF stream is missing endstream.");
            var payload = bytes[start..end];
            while (payload.Length > 0 && (payload[^1] == '\r' || payload[^1] == '\n')) payload = payload[..^1];
            var header = ReadContainingObjectHeader(latin, offset, 4096);
            var objectId = TryReadContainingObjectId(header, options.EffectiveRegexTimeout, out var id) ? id : (int?)null;
            payload = DecodeFilteredStream(payload, header, options.MaxExpandedStreamBytes);
            yield return new PdfObjectStream(objectId, header, Encoding.Latin1.GetString(payload));
            offset = end + endMarker.Length;
        }
    }

    private sealed record PdfObjectStream(int? ObjectId, string Header, string Payload);

    private static string ReadContainingObjectHeader(string latin, int streamOffset, int maxLookback)
    {
        var start = Math.Max(0, streamOffset - maxLookback);
        var window = latin[start..streamOffset];
        var objectMarker = window.LastIndexOf(" obj", StringComparison.Ordinal);
        if (objectMarker >= 0)
        {
            // Retain the object number immediately before "obj", but exclude
            // filters belonging to preceding objects in a compact PDF.
            start += Math.Max(0, objectMarker - 32);
        }
        return latin[start..streamOffset];
    }

    private sealed class PdfToUnicodeMap
    {
        private readonly Dictionary<string, string> _entries;
        private readonly int _maxSourceBytes;
        private PdfToUnicodeMap(Dictionary<string, string> entries)
        {
            _entries = entries;
            _maxSourceBytes = entries.Keys.Select(key => key.Length / 2).DefaultIfEmpty(1).Max();
        }
        public int Count => _entries.Count;

        public string Decode(ReadOnlySpan<byte> bytes)
        {
            var output = new StringBuilder();
            for (var index = 0; index < bytes.Length;)
            {
                string? mapped = null;
                var consumed = 0;
                for (var width = Math.Min(_maxSourceBytes, bytes.Length - index); width >= 1; width--)
                {
                    var key = Convert.ToHexString(bytes.Slice(index, width));
                    if (_entries.TryGetValue(key, out mapped)) { consumed = width; break; }
                }
                if (consumed == 0) { output.Append((char)bytes[index++]); continue; }
                output.Append(mapped);
                index += consumed;
            }
            return output.ToString();
        }

        public static PdfToUnicodeMap Parse(string cmap, TimeSpan timeout)
        {
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (Match block in Regex.Matches(cmap, @"beginbfchar(?<body>.*?)endbfchar", RegexOptions.Singleline, timeout))
                    ParsePairs(block.Groups["body"].Value, entries, timeout);
                foreach (Match block in Regex.Matches(cmap, @"beginbfrange(?<body>.*?)endbfrange", RegexOptions.Singleline, timeout))
                    ParseRanges(block.Groups["body"].Value, entries, timeout);
            }
            catch (RegexMatchTimeoutException exception)
            {
                throw new PdfExtractionException("PDF ToUnicode CMap matching exceeded its time limit.", exception);
            }
            return new PdfToUnicodeMap(entries);
        }

        private static void ParsePairs(string body, IDictionary<string, string> entries, TimeSpan timeout)
        {
            var tokens = Regex.Matches(body, @"<(?<hex>[0-9A-Fa-f\s]+)>", RegexOptions.None, timeout).Select(match => NormalizeHex(match.Groups["hex"].Value)).ToArray();
            for (var index = 0; index + 1 < tokens.Length; index += 2) entries[tokens[index]] = DecodeDestination(tokens[index + 1]);
        }

        private static void ParseRanges(string body, IDictionary<string, string> entries, TimeSpan timeout)
        {
            var lines = body.Split('\n');
            foreach (var line in lines)
            {
                var range = Regex.Match(line, @"<(?<start>[0-9A-Fa-f\s]+)>\s+<(?<end>[0-9A-Fa-f\s]+)>\s+(?<dest>\[.*\]|<[0-9A-Fa-f\s]+>)", RegexOptions.None, timeout);
                if (!range.Success) continue;
                var sourceStart = NormalizeHex(range.Groups["start"].Value); var sourceEnd = NormalizeHex(range.Groups["end"].Value);
                var start = Convert.ToUInt32(sourceStart, 16); var end = Convert.ToUInt32(sourceEnd, 16); var destinationToken = range.Groups["dest"].Value.Trim();
                if (destinationToken.StartsWith("[", StringComparison.Ordinal))
                {
                    var destinations = Regex.Matches(destinationToken, @"<(?<hex>[0-9A-Fa-f\s]+)>", RegexOptions.None, timeout)
                        .Select(match => NormalizeHex(match.Groups["hex"].Value)).ToArray();
                    for (var index = 0; index < destinations.Length && start + (uint)index <= end; index++)
                        entries[(start + (uint)index).ToString("X" + sourceStart.Length, System.Globalization.CultureInfo.InvariantCulture)] = DecodeDestination(destinations[index]);
                    continue;
                }
                var destination = NormalizeHex(destinationToken[1..^1]);
                for (var value = start; value <= end; value++)
                {
                    var source = value.ToString("X" + sourceStart.Length, System.Globalization.CultureInfo.InvariantCulture);
                    var codePoint = Convert.ToUInt32(destination, 16) + (value - start);
                    entries[source] = codePoint <= 0xFFFF ? ((char)codePoint).ToString() : char.ConvertFromUtf32((int)codePoint);
                }
            }
        }

        private static string NormalizeHex(string value) => Regex.Replace(value, @"\s+", string.Empty, RegexOptions.CultureInvariant).ToUpperInvariant();
        private static string DecodeDestination(string hex)
        {
            if (hex.Length == 0) return string.Empty;
            var value = Convert.ToUInt32(hex, 16);
            // ToUnicode destinations are UTF-16BE strings.  Four-byte values are
            // commonly used for supplementary-plane characters.
            if (hex.Length > 4) return Encoding.BigEndianUnicode.GetString(Convert.FromHexString(hex));
            return value <= 0xFFFF ? ((char)value).ToString() : char.ConvertFromUtf32((int)value);
        }
    }

    private static byte[] DecodeFilteredStream(byte[] payload, string header, long maxExpandedBytes)
    {
        // ReportLab and many production PDFs encode streams as ASCII85 followed
        // by Flate. Decode filters in the order advertised by the PDF dictionary.
        if (header.Contains("/ASCII85Decode", StringComparison.Ordinal))
            payload = DecodeAscii85(payload, maxExpandedBytes);
        if (header.Contains("/FlateDecode", StringComparison.Ordinal))
            payload = Inflate(payload, maxExpandedBytes);
        return payload;
    }

    private static byte[] DecodeAscii85(byte[] payload, long maxDecodedBytes)
    {
        using var output = new MemoryStream();
        ulong tuple = 0;
        var digits = 0;
        void Write(byte value)
        {
            if (output.Length >= maxDecodedBytes)
                throw new PdfExtractionException($"PDF ASCII85 stream exceeds the {maxDecodedBytes}-byte limit.");
            output.WriteByte(value);
        }

        foreach (var value in payload)
        {
            if (value is 32 or 9 or 13 or 10 or 12 or 0)
                continue;
            if (value == (byte)'~') break;
            if (value == (byte)'z')
            {
                if (digits != 0) throw new PdfExtractionException("PDF ASCII85 zero group appeared mid-tuple.");
                Write(0); Write(0); Write(0); Write(0);
                continue;
            }
            if (value < (byte)'!' || value > (byte)'u')
                throw new PdfExtractionException("PDF ASCII85 stream contains an invalid character.");
            tuple = checked(tuple * 85 + (uint)(value - (byte)'!'));
            digits++;
            if (digits != 5) continue;
            if (tuple > uint.MaxValue) throw new PdfExtractionException("PDF ASCII85 tuple exceeds 32 bits.");
            Write((byte)(tuple >> 24)); Write((byte)(tuple >> 16));
            Write((byte)(tuple >> 8)); Write((byte)tuple);
            tuple = 0;
            digits = 0;
        }

        if (digits > 0)
        {
            var count = digits;
            for (var i = digits; i < 5; i++) tuple = checked(tuple * 85 + 84);
            // A partial group is padded to five digits and may exceed 32 bits;
            // only the low 32 bits form the emitted 1-3 bytes.
            var bytes = new[] { (byte)(tuple >> 24), (byte)(tuple >> 16), (byte)(tuple >> 8), (byte)tuple };
            for (var i = 0; i < count - 1; i++) Write(bytes[i]);
        }
        return output.ToArray();
    }

    private static byte[] Inflate(byte[] payload, long maxExpandedBytes)
    {
        try
        {
            using var input = new MemoryStream(payload);
            using var stream = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            long total = 0;
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                total = checked(total + read);
                if (total > maxExpandedBytes) throw new PdfExtractionException($"PDF expanded stream exceeds the {maxExpandedBytes}-byte limit.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (PdfExtractionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or OverflowException)
        {
            throw new PdfExtractionException("PDF Flate stream is malformed.", exception);
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++) if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }
}

public sealed record PdfRestoreDecision(FidelityLevel Fidelity, bool ByteIdentical, string Message);

public static class PdfRestorePolicy
{
    public static PdfRestoreDecision For(bool projectionChanged) => projectionChanged
        ? new(FidelityLevel.F3, false, "Edited PDF content must be rendered as a new PDF; F1 package restore is not supported.")
        : new(FidelityLevel.F0, true, "Unedited PDF can be returned byte-identically from the preserved original.");
}
