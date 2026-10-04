using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Markdown;

namespace DocRedock.Api;

/// <summary>The one finalized set of visual/export counters, computed once from the final graph
/// and its diagnostics. Every displayed summary line (the CLI's "Visual summary:" line and this
/// record's own <see cref="ToString"/> "Export completed" block) renders from the same instance,
/// so the figures a user sees can never disagree with each other (F-05).</summary>
public sealed record ExportSummary(
    int Warnings,
    int Tables,
    int DiagramsReconstructed,
    int VectorPages,
    int NativeEdges,
    int HighConfidenceEdges,
    int MediumConfidenceEdges,
    int UnresolvedRelations,
    int FallbackPaths,
    int FallbackPages,
    int Rejected,
    int ReviewPages = 0,
    int ReviewImagePages = 0,
    int UnresolvedElements = 0,
    int OcrImages = 0,
    int OcrReviewItems = 0,
    int VisualReviewPages = 0,
    int UnanalyzedContent = 0,
    int UnanalyzedContentPages = 0,
    bool HiddenContentIncluded = false,
    int? RenderedTables = null)
{
    /// <summary>Legacy Tables counts canonical graph Table nodes (including inferred PDF tables).
    /// Worksheet cells rendered as a table are counted separately by RenderedTables.</summary>
    public int NativeTables => Tables;
    /// <summary>OCR text that should be compared with its source image. This is a review hint,
    /// not a warning: it never changes the export status or exit code.</summary>
    public bool OcrReviewRequired => OcrReviewItems > 0;

    /// <summary>False when a recognized visual element was kept only as fallback, a diagnostic, or
    /// an unresolved relation instead of being expressed in the Markdown. It says nothing about
    /// attributes DocRedock does not model (for example colors), nor about content that was never
    /// analyzed at all - <see cref="UnanalyzedContent"/> reports that separately.</summary>
    public bool AllVisualElementsConverted => VisualReviewPages == 0 && UnresolvedElements == 0;

    // "Written", "every visual element converted", and "needs a person" are different facts; each gets
    // its own line so a successful export with review items is never read as either a failure
    // or a complete conversion.
    public override string ToString() =>
        $"Export completed\nOutput: Markdown\n" +
        $"Output written: yes\n" +
        $"Visual elements converted: {(AllVisualElementsConverted ? "all" : $"partial ({UnresolvedElements} unresolved on {VisualReviewPages} page(s))")}\n" +
        $"Unanalyzed content: {(UnanalyzedContent == 0 ? "none" : $"{UnanalyzedContent} item(s) on {UnanalyzedContentPages} page(s)")}\n" +
        $"Human review: {HumanReviewText()}\n" +
        $"Warnings: {Warnings}\nTables reconstructed: {Tables} (graph nodes)\nMarkdown tables rendered: {RenderedTables?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not measured"}\nDiagrams reconstructed: {DiagramsReconstructed}\nFallback pages: {FallbackPages}\nPages requiring review: {ReviewPages}\nReview image pages: {ReviewImagePages}\nUnresolved visual elements: {UnresolvedElements}" +
        (OcrImages > 0 ? $"\nOCR review items: {OcrReviewItems} (confidence below 80% or not reported, in {OcrImages} image(s))" : string.Empty);

    private string HumanReviewText()
    {
        var reasons = new List<string>();
        if (VisualReviewPages > 0) reasons.Add($"visual {VisualReviewPages} page(s)");
        if (UnanalyzedContentPages > 0) reasons.Add($"unanalyzed content {UnanalyzedContentPages} page(s)");
        if (OcrReviewRequired) reasons.Add($"OCR {OcrReviewItems} item(s)");
        var review = reasons.Count == 0 ? "not required" : "required (" + string.Join(", ", reasons) + ")";
        if (!HiddenContentIncluded) return review;
        if (reasons.Count == 0) review = "source comparison not required";
        return review + "; hidden content included - review before sharing";
    }
}

public static class ExportSummaryBuilder
{
    public static ExportSummary Build(DocumentGraph graph, IReadOnlyList<Diagnostic> diagnostics, int? renderedTables = null)
    {
        var graphs = graph.Nodes.Select(ReadVisualGraph).OfType<VisualGraph>().ToArray();
        var edges = graphs.SelectMany(item => item.Edges ?? []).Where(edge => edge is not null).ToArray();
        // A PDF page (or a slide) can yield several diagram nodes. Count each partition once, the
        // same way for "vector_pages" and "fallback pages", so a page total is displayed rather
        // than a node total and the two figures can never disagree with each other.
        var vectorPages = graph.Partitions.Count(partition =>
            partition.Nodes.Select(ReadVisualGraph).OfType<VisualGraph>().Any());
        var fallbackPages = graph.Partitions.Count(partition => partition.Nodes.Select(ReadVisualGraph)
            .OfType<VisualGraph>().Any(item => item.FallbackPathCount > 0));
        var ocr = ExportReviewBuilder.BuildOcr(graph);
        return new ExportSummary(
            Warnings: diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning),
            Tables: graph.Nodes.Count(node => node.Kind == NodeKind.Table),
            DiagramsReconstructed: graphs.Count(HasResolvedKnownEdge),
            VectorPages: vectorPages,
            NativeEdges: edges.Count(edge => edge.Resolution == VisualEdgeResolution.NativeConnection),
            HighConfidenceEdges: edges.Count(edge => edge.Evidence?.ConfidenceBand.Equals("High", StringComparison.OrdinalIgnoreCase) == true),
            MediumConfidenceEdges: edges.Count(edge => edge.Evidence?.ConfidenceBand.Equals("Medium", StringComparison.OrdinalIgnoreCase) == true),
            UnresolvedRelations: graphs.Sum(item => item.UnresolvedRelationCount),
            // One rule everywhere: FallbackPathCount favors the source-item ledger over the raw
            // VisualPath.IsFallback flag when the ledger is present (see VisualGraph.FallbackPathCount).
            FallbackPaths: graphs.Sum(item => item.FallbackPathCount),
            FallbackPages: fallbackPages,
            Rejected: graphs.Sum(item => VisualGraphValidator.Validate(item).Errors.Count),
            ReviewPages: graph.Partitions.Count(p => p.Nodes.Any(ReadableMarkdownSerializer.RequiresSourceReview)),
            // A diagram node carries the visual graph (even when its metadata cannot be read, which
            // itself needs review); every other node that needs review stands for unanalyzed content.
            VisualReviewPages: graph.Partitions.Count(p => p.Nodes.Any(node => node.Kind == NodeKind.Diagram &&
                node.Extensions?.ContainsKey("visual_graph") == true && ReadableMarkdownSerializer.RequiresSourceReview(node))),
            UnanalyzedContent: graph.Nodes.Sum(ExportReviewBuilder.CountUnanalyzedContent),
            UnanalyzedContentPages: graph.Partitions.Count(p => p.Nodes.Any(node => ExportReviewBuilder.CountUnanalyzedContent(node) > 0)),
            ReviewImagePages: diagnostics.Where(d => d.Code == "PdfReviewImageAttached")
                .Select(d => d.PartUri).Distinct(StringComparer.Ordinal).Count(),
            UnresolvedElements: graphs.Sum(CountUnresolvedElements),
            OcrImages: ocr.Images,
            OcrReviewItems: ocr.ReviewItems,
            HiddenContentIncluded: diagnostics.Any(diagnostic => diagnostic.Code == "HiddenContentIncluded"),
            RenderedTables: renderedTables);
    }

    // Count objects, not warnings; a raw shaft and the unresolved edge backed by it
    // represent one element. Suppressed arrowheads do not add another unresolved item.
    private static int CountUnresolvedElements(VisualGraph graph)
    {
        var paths = graph.Paths ?? [];
        var ids = graph.SourceItems is { Count: > 0 } items
            ? items.Where(i => i.Disposition is VisualDisposition.VisualFallback or VisualDisposition.DiagnosticOnly)
                .Select(i => i.FallbackPathId ?? i.Id).ToHashSet(StringComparer.Ordinal)
            : paths.Where(p => p.IsFallback).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var edge in graph.Edges.Where(e => e.SourceId is null || e.TargetId is null || e.Resolution == VisualEdgeResolution.Unresolved))
            ids.Add(paths.FirstOrDefault(p => p.Points is not null && edge.Path is not null && p.Points.SequenceEqual(edge.Path))?.Id ?? edge.Id);
        foreach (var diagnostic in graph.Diagnostics ?? [])
            if (diagnostic.SourceObjectId is { } id && diagnostic.Code is "VisualNodeLabelMissing" or "VisualEdgeLabelUnresolved") ids.Add(id);
        // The Markdown fallback writer can expose paths even when the ledger has only
        // decorative/suppressed entries and no semantic topology survived.
        if (ids.Count == 0 && graph.Nodes.Count == 0 && graph.Edges.Count == 0)
            ids.UnionWith(paths.Where(p => p.IsFallback).Select(p => p.Id));
        return ids.Count;
    }

    private static VisualGraph? ReadVisualGraph(DocumentNode node) => node.Extensions?.TryGetValue("visual_graph", out var value) == true
        ? value.Deserialize<VisualGraph>() : null;

    private static bool HasResolvedKnownEdge(VisualGraph graph)
    {
        var ids = (graph.Nodes ?? []).Where(node => node is not null).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        return (graph.Edges ?? []).Any(edge => edge is not null && edge.Resolution != VisualEdgeResolution.Unresolved &&
            edge.SourceId is not null && edge.TargetId is not null && ids.Contains(edge.SourceId) && ids.Contains(edge.TargetId));
    }
}
