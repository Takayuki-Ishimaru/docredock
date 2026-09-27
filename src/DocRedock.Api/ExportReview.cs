using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Markdown;

namespace DocRedock.Api;

/// <summary>What a person has to look at, in the terms they see on the page: an unresolved line,
/// arrow, label, or shape. Diagnostic records are deliberately not the unit here; one diagonal line
/// can produce several diagnostics but is one thing to check.</summary>
public enum ReviewElementKind
{
    /// <summary>A diagonal line drawn across a reconstructed table; the table text was exported.</summary>
    TableDiagonalLine,
    /// <summary>A diagonal arrow drawn across a reconstructed table; the table text was exported.</summary>
    TableDiagonalArrow,
    /// <summary>A line whose endpoints or meaning could not be determined.</summary>
    Line,
    /// <summary>An arrow whose endpoints or meaning could not be determined.</summary>
    Arrow,
    /// <summary>Text that could not be assigned to one shape or line.</summary>
    Label,
    /// <summary>A shape or path kept only as vector fallback.</summary>
    Shape,
    /// <summary>A drawing component whose text and graphics could not be analyzed at all (a PDF
    /// Form XObject the extractor could not read); it is missing from the Markdown.</summary>
    UnanalyzedContent,
}

/// <summary>A rectangle on the review image, normalized to 0..1 with the origin at the top left.</summary>
public sealed record ReviewRegion(double X, double Y, double Width, double Height);

/// <summary>A point on the review image, normalized like <see cref="ReviewRegion"/>.</summary>
public sealed record ReviewPoint(double X, double Y);

/// <param name="Outline">The element's own stroke on the review image, when its path is known, so a
/// viewer can trace a diagonal line instead of framing its whole bounding box.</param>
public sealed record ReviewElement(ReviewElementKind Kind, string SourceId, ReviewRegion? Region = null, string? LineStyle = null,
    IReadOnlyList<ReviewPoint>? Outline = null);

/// <summary>One page (PDF), slide, sheet, or document part that needs comparison with the source.</summary>
public sealed record ReviewPage(
    int Number,
    string PartitionId,
    DocumentFormatKind Format,
    IReadOnlyList<ReviewElement> Elements,
    string? ReviewImageReference,
    bool ReviewImageUnavailable,
    bool HasTables)
{
    public int CountOf(ReviewElementKind kind) => Elements.Count(element => element.Kind == kind);
}

/// <summary>OCR results that should be compared with the source image: confidence below the
/// readable threshold or not reported by the engine. Independent of warnings and exit codes.</summary>
public sealed record OcrReviewSummary(int Images, int Regions, int ReviewItems)
{
    public bool Required => ReviewItems > 0;
}

public sealed record ExportReview(IReadOnlyList<ReviewPage> Pages, OcrReviewSummary Ocr)
{
    public int ReviewImagePages => Pages.Count(page => page.ReviewImageReference is not null);
    public int Elements => Pages.Sum(page => page.Elements.Count);
    public bool Required => Pages.Count > 0 || Ocr.Required;
}

/// <summary>Finds an attached review image on disk: next to the readable Markdown (its .assets
/// folder), inside a directory sidecar, or inline as a data URI. A reference that would leave those
/// locations is ignored.</summary>
public static class ExportReviewImages
{
    public static string? Resolve(string? reference, string markdownPath, string? sidecarDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        if (reference.StartsWith("data:image/", StringComparison.Ordinal)) return reference;
        if (reference.Contains(':', StringComparison.Ordinal) || Path.IsPathRooted(reference)) return null;
        var markdownDirectory = Path.GetDirectoryName(Path.GetFullPath(markdownPath))!;
        var nextToMarkdown = Path.GetFullPath(Path.Combine(markdownDirectory, reference.Replace('/', Path.DirectorySeparatorChar)));
        if (nextToMarkdown.StartsWith(markdownDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(nextToMarkdown))
            return nextToMarkdown;
        if (sidecarDirectory is null || !Directory.Exists(sidecarDirectory) || reference.Contains('/') || reference.Contains('\\')) return null;
        foreach (var extension in new[] { ".png", ".jpg" })
        {
            var candidate = Path.Combine(sidecarDirectory, "assets", reference + extension);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

public static class ExportReviewBuilder
{
    /// <summary>Builds the review list from the finalized export graph. Pages are exactly those
    /// counted by <see cref="ExportSummary.ReviewPages"/>; elements are exactly those counted by
    /// <see cref="ExportSummary.UnresolvedElements"/>, classified by what they look like, plus one
    /// <see cref="ReviewElementKind.UnanalyzedContent"/> per item counted by
    /// <see cref="ExportSummary.UnanalyzedContent"/>.</summary>
    public static ExportReview Build(DocumentGraph graph, IReadOnlyList<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(graph); ArgumentNullException.ThrowIfNull(diagnostics);
        var pages = new List<ReviewPage>();
        foreach (var partition in graph.Partitions.OrderBy(partition => partition.Order))
        {
            if (!partition.Nodes.Any(ReadableMarkdownSerializer.RequiresSourceReview)) continue;
            var tables = partition.Nodes.Where(node => node.Kind == NodeKind.Table && node.Geometry is not null)
                .Select(node => node.Geometry!).ToArray();
            var reviewImage = partition.Nodes.Where(node => node.Kind == NodeKind.Image && node.Content is ReferenceNodeContent &&
                    ExtensionBool(node, "pdf_page_raster"))
                .OrderByDescending(node => ExtensionBool(node, "pdf_review_image")).FirstOrDefault();
            var mapping = reviewImage is null ? null : ReviewImageMapping.From(reviewImage);
            var elements = partition.Nodes.Select(ReadVisualGraph).OfType<VisualGraph>()
                .SelectMany(visual => Classify(visual, tables, mapping))
                .Concat(partition.Nodes.SelectMany(node => UnanalyzedContent(node, mapping))).ToArray();
            var number = partition.Order + 1;
            var reference = (reviewImage?.Content as ReferenceNodeContent)?.Reference;
            pages.Add(new ReviewPage(number, partition.Id, graph.Format, elements, reference,
                diagnostics.Any(d => d.Code == "PdfReviewImageUnavailable" &&
                    (StringComparer.Ordinal.Equals(d.PartUri, $"pdf:page:{number}") || StringComparer.Ordinal.Equals(d.PartUri, partition.SourcePartUri))),
                partition.Nodes.Any(node => node.Kind == NodeKind.Table)));
        }
        return new ExportReview(pages, BuildOcr(graph));
    }

    /// <summary>OCR regions whose confidence is below the readable threshold or missing, the same
    /// rule the readable Markdown uses for its default review table.</summary>
    public static OcrReviewSummary BuildOcr(DocumentGraph graph)
    {
        var images = 0; var regions = 0; var review = 0;
        foreach (var node in graph.Nodes.Where(node => node.Kind == NodeKind.ImageText))
        {
            if (node.Extensions?.TryGetValue("ocr_regions", out var raw) != true || raw.ValueKind != JsonValueKind.Array) continue;
            var items = raw.EnumerateArray().ToArray();
            if (items.Length == 0) continue;
            images++;
            regions += items.Length;
            review += items.Count(ReadableMarkdownSerializer.OcrRegionNeedsReview);
        }
        return new OcrReviewSummary(images, regions, review);
    }

    private static IEnumerable<ReviewElement> Classify(VisualGraph graph, IReadOnlyList<Geometry> tables, ReviewImageMapping? mapping)
    {
        // Malformed metadata (duplicate or missing path IDs) is reported by the validator; the review
        // list must still be built, so the first path with an ID wins.
        var paths = new Dictionary<string, VisualPath>(StringComparer.Ordinal);
        foreach (var path in graph.Paths ?? [])
            if (!string.IsNullOrEmpty(path?.Id)) paths.TryAdd(path.Id, path);
        var edges = (graph.Edges ?? []).Where(edge => edge is not null).ToArray();
        var sourceItems = graph.SourceItems ?? [];
        var arrowPathIds = sourceItems.Where(item => item is not null && item.Disposition == VisualDisposition.SuppressedDuplicate &&
                item.Reason?.Contains("arrowhead", StringComparison.OrdinalIgnoreCase) == true && item.DuplicateOfSourceItemId is not null)
            .Select(item => item.DuplicateOfSourceItemId!).ToHashSet(StringComparer.Ordinal);
        var labels = (graph.Diagnostics ?? []).Where(d => d is not null && d.SourceObjectId is not null &&
                d.Code is "VisualNodeLabelMissing" or "VisualEdgeLabelUnresolved")
            .Select(d => d.SourceObjectId!).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, ReviewElement>(StringComparer.Ordinal);

        void Add(string? id, VisualPath? path, VisualEdge? edge)
        {
            if (string.IsNullOrEmpty(id) || result.ContainsKey(id)) return;
            var geometry = path?.Geometry ?? edge?.Geometry;
            var lineStyle = path?.LineStyle ?? edge?.LineStyle;
            ReviewElementKind kind;
            if (labels.Contains(id)) kind = ReviewElementKind.Label;
            else
            {
                var points = path?.Points ?? edge?.Path;
                var isOpen = edge is not null || points is { Count: >= 2 } && points[0] != points[^1] && path?.IsFilled != true;
                var isArrow = arrowPathIds.Contains(id) || edge?.EdgeDirection == VisualEdgeDirection.Directed &&
                    edge.Evidence?.ArrowheadEvidence is "start" or "end" or "both";
                // Visibly slanted (more than about 6 degrees off an axis), not merely a thick or
                // slightly skewed horizontal/vertical stroke.
                var diagonal = geometry is not null && Math.Min(geometry.Width, geometry.Height) > Math.Max(1, Math.Max(geometry.Width, geometry.Height) * .1);
                var overTable = geometry is not null && tables.Any(table => Intersects(table, geometry));
                kind = !isOpen ? ReviewElementKind.Shape
                    : diagonal && overTable ? isArrow ? ReviewElementKind.TableDiagonalArrow : ReviewElementKind.TableDiagonalLine
                    : isArrow ? ReviewElementKind.Arrow : ReviewElementKind.Line;
            }
            var outline = (path?.Points ?? edge?.Path) is { Count: >= 2 and <= MaxOutlinePoints } stroke && mapping is not null
                ? stroke.Select(point => mapping.MapPoint(point)).OfType<ReviewPoint>().ToArray()
                : null;
            result[id] = new ReviewElement(kind, id, geometry is null ? null : mapping?.Map(geometry), lineStyle,
                outline is { Length: >= 2 } ? outline : null);
        }

        // The same object set ExportSummaryBuilder counts, in the same precedence: ledger fallback,
        // then unresolved relations (a shaft and its raw path are one element), then labels.
        var ids = sourceItems is { Count: > 0 }
            ? sourceItems.Where(item => item is not null && item.Disposition is VisualDisposition.VisualFallback or VisualDisposition.DiagnosticOnly)
                .Select(item => item.FallbackPathId ?? item.Id)
            : paths.Values.Where(path => path.IsFallback).Select(path => path.Id);
        foreach (var id in ids) Add(id, paths.GetValueOrDefault(id), null);
        foreach (var edge in edges.Where(edge => edge.SourceId is null || edge.TargetId is null || edge.Resolution == VisualEdgeResolution.Unresolved))
        {
            var path = paths.Values.FirstOrDefault(candidate => candidate.Points is not null && edge.Path is not null && candidate.Points.SequenceEqual(edge.Path));
            Add(path?.Id ?? edge.Id, path, edge);
        }
        foreach (var id in labels) Add(id, paths.GetValueOrDefault(id), null);
        if (result.Count == 0 && (graph.Nodes ?? []).Count == 0 && edges.Length == 0)
            foreach (var path in paths.Values.Where(path => path.IsFallback)) Add(path.Id, path, null);
        return result.Values;
    }

    /// <summary>One element per content item the adapter recorded as not analyzed, framed where it
    /// paints when that is known. Any other node that asks for source review without being a
    /// visual graph stands for one such item, so the review list, the summary counts, and
    /// <see cref="ReadableMarkdownSerializer.RequiresSourceReview"/> can never disagree.</summary>
    internal static IEnumerable<ReviewElement> UnanalyzedContent(DocumentNode node, ReviewImageMapping? mapping)
    {
        if (node.Extensions?.TryGetValue(PdfDocumentGraphProjection.UnparsedFormXObjectsExtension, out var raw) == true &&
            raw.ValueKind == JsonValueKind.Array && raw.GetArrayLength() > 0)
        {
            var index = 0;
            foreach (var item in raw.EnumerateArray())
            {
                PdfDocumentGraphProjection.PdfUnparsedFormRecord? record;
                try { record = item.Deserialize<PdfDocumentGraphProjection.PdfUnparsedFormRecord>(); }
                catch (JsonException) { record = null; }
                var sourceId = $"{node.Id}#{index++}";
                yield return new ReviewElement(ReviewElementKind.UnanalyzedContent, record?.Name is { Length: > 0 } name ? $"{sourceId}:{name}" : sourceId,
                    record?.Bounds is { } bounds ? mapping?.Map(bounds) : null);
            }
            yield break;
        }
        if (ExtensionBool(node, ReadableMarkdownSerializer.SourceReviewRequiredExtension) && ReadVisualGraph(node) is null &&
            node.Extensions?.ContainsKey("visual_graph") != true)
            yield return new ReviewElement(ReviewElementKind.UnanalyzedContent, node.Id,
                node.Geometry is { } geometry ? mapping?.Map(geometry) : null);
    }

    /// <summary>How many unanalyzed content items a node stands for; see <see cref="UnanalyzedContent"/>.</summary>
    internal static int CountUnanalyzedContent(DocumentNode node) => UnanalyzedContent(node, null).Count();

    private const int MaxOutlinePoints = 256;

    private static bool Intersects(Geometry table, Geometry item) =>
        StringComparer.Ordinal.Equals(table.CoordinateSpace, item.CoordinateSpace) &&
        item.X <= table.X + table.Width && item.X + item.Width >= table.X &&
        item.Y <= table.Y + table.Height && item.Y + item.Height >= table.Y;

    private static VisualGraph? ReadVisualGraph(DocumentNode node)
    {
        if (node.Extensions?.TryGetValue("visual_graph", out var value) != true) return null;
        try { return value.Deserialize<VisualGraph>(); }
        catch (JsonException) { return null; }
    }

    private static bool ExtensionBool(DocumentNode node, string key) =>
        node.Extensions?.TryGetValue(key, out var value) == true && value.ValueKind == JsonValueKind.True;

    /// <summary>Maps PDF user space onto the rendered page image recorded with a review image.
    /// Only unrotated pages are mapped, matching the embedded-image crop rules.</summary>
    internal sealed record ReviewImageMapping(Geometry Box)
    {
        public static ReviewImageMapping? From(DocumentNode image)
        {
            if (image.Extensions is not { } extensions ||
                !extensions.TryGetValue("pdf_page_box", out var rawBox) || rawBox.ValueKind != JsonValueKind.Object) return null;
            if (extensions.TryGetValue("pdf_page_rotation", out var rotation) && rotation.ValueKind == JsonValueKind.Number &&
                rotation.TryGetInt32(out var degrees) && degrees != 0) return null;
            Geometry? box;
            try { box = rawBox.Deserialize<Geometry>(); }
            catch (JsonException) { return null; }
            return box is { Width: > 0, Height: > 0 } ? new ReviewImageMapping(box) : null;
        }

        public ReviewRegion? Map(Geometry geometry)
        {
            if (!StringComparer.Ordinal.Equals(geometry.CoordinateSpace, "pdf-user-space")) return null;
            // A thin line still needs a visible frame; pad by about 1% of the page.
            const double pad = .01;
            var left = Math.Clamp((geometry.X - Box.X) / Box.Width - pad, 0, 1);
            var right = Math.Clamp((geometry.X + geometry.Width - Box.X) / Box.Width + pad, 0, 1);
            var top = Math.Clamp(1 - (geometry.Y + geometry.Height - Box.Y) / Box.Height - pad, 0, 1);
            var bottom = Math.Clamp(1 - (geometry.Y - Box.Y) / Box.Height + pad, 0, 1);
            return right > left && bottom > top ? new ReviewRegion(left, top, right - left, bottom - top) : null;
        }

        public ReviewPoint? MapPoint(VisualPathPoint point)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return null;
            return new ReviewPoint(Math.Clamp((point.X - Box.X) / Box.Width, 0, 1), Math.Clamp(1 - (point.Y - Box.Y) / Box.Height, 0, 1));
        }
    }
}
