using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Markdown;

namespace DocRedock.Api;

/// <summary>Where an aspect of the output's structure (reading order, tables) came from, so a summary
/// says what was actually evaluated instead of implying that everything was checked.</summary>
public enum StructureBasis
{
    /// <summary>Not evaluated by this summary (no readable projection was rendered).</summary>
    NotEvaluated,
    /// <summary>Nothing of this kind is in the output (for tables: no table was written).</summary>
    NotApplicable,
    /// <summary>Taken from the file's own structure: Word body order, Word/PowerPoint/chart table
    /// markup, tagged PDF tables.</summary>
    Source,
    /// <summary>Reconstructed from positions or cell layout without a detected problem. It was not
    /// compared with the source.</summary>
    Inferred,
    /// <summary>Reconstructed from layout, and at least one place could not be decided from it:
    /// compare those places with the source.</summary>
    NeedsComparison,
}

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
    int? RenderedTables = null,
    int TableBoundaryReviewItems = 0,
    StructureBasis ReadingOrder = StructureBasis.NotEvaluated,
    StructureBasis TableStructure = StructureBasis.NotEvaluated)
{
    /// <summary>Legacy Tables counts canonical graph Table nodes (including inferred PDF tables).
    /// Worksheet cells rendered as a table are counted separately by RenderedTables.</summary>
    public int NativeTables => Tables;
    /// <summary>OCR text that should be compared with its source image. This is a review hint,
    /// not a warning: it never changes the export status or exit code.</summary>
    public bool OcrReviewRequired => OcrReviewItems > 0;

    /// <summary>True when the export detected something to compare with the source: figures,
    /// unanalyzed content, OCR text, or a worksheet table boundary that the layout could not decide.
    /// False means none was detected, not that reading order and tables were verified;
    /// <see cref="ReadingOrder"/> and <see cref="TableStructure"/> say how those were obtained.</summary>
    public bool ReviewItemsDetected => VisualReviewPages > 0 || UnanalyzedContentPages > 0 || OcrReviewRequired || TableBoundaryReviewItems > 0;

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
        $"Detected review items: {DetectedReviewItemsText()}\n" +
        $"Reading order: {ExportSummaryText.ReadingOrderEnglish(ReadingOrder)}\n" +
        $"Table structure: {ExportSummaryText.TableStructureEnglish(TableStructure, TableBoundaryReviewItems)}\n" +
        $"Warnings: {Warnings}\nTables reconstructed: {Tables} (graph nodes)\nMarkdown tables rendered: {RenderedTables?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not measured"}\nDiagrams reconstructed: {DiagramsReconstructed}\nFallback pages: {FallbackPages}\nPages requiring review: {ReviewPages}\nReview image pages: {ReviewImagePages}\nUnresolved visual elements: {UnresolvedElements}" +
        (OcrImages > 0 ? $"\nOCR review items: {OcrReviewItems} (confidence below 80% or not reported, in {OcrImages} image(s))" : string.Empty);

    // What was detected, never a claim that nothing needs checking: reading order and table
    // structure have their own lines saying how they were obtained.
    private string DetectedReviewItemsText()
    {
        var reasons = new List<string>();
        if (VisualReviewPages > 0) reasons.Add($"visual {VisualReviewPages} page(s)");
        if (UnanalyzedContentPages > 0) reasons.Add($"unanalyzed content {UnanalyzedContentPages} page(s)");
        if (OcrReviewRequired) reasons.Add($"OCR {OcrReviewItems} item(s)");
        if (TableBoundaryReviewItems > 0) reasons.Add($"table boundaries {TableBoundaryReviewItems}");
        // Sharing hidden content is a different decision from comparing with the source.
        const string hidden = "hidden content included - review before sharing";
        if (!HiddenContentIncluded) return reasons.Count == 0 ? "none" : string.Join(", ", reasons);
        return reasons.Count == 0 ? hidden : string.Join(", ", reasons) + "; " + hidden;
    }
}

/// <summary>Wording for <see cref="StructureBasis"/> shared by the CLI, GUI and AI package review.</summary>
public static class ExportSummaryText
{
    public static string ReadingOrderEnglish(StructureBasis basis) => basis switch
    {
        StructureBasis.Source => "source order",
        StructureBasis.Inferred => "inferred from layout (not compared with the source)",
        StructureBasis.NeedsComparison => "inferred from layout; compare with the source",
        StructureBasis.NotApplicable => "not applicable",
        _ => "not evaluated",
    };

    public static string TableStructureEnglish(StructureBasis basis, int reviewItems) => basis switch
    {
        StructureBasis.Source => "source tables",
        StructureBasis.Inferred => "inferred from layout (not compared with the source)",
        StructureBasis.NeedsComparison => $"inferred from layout; {reviewItems} boundary(ies) need source comparison",
        StructureBasis.NotApplicable => "no tables",
        _ => "not evaluated",
    };

    public static string ReadingOrderJapanese(StructureBasis basis) => basis switch
    {
        StructureBasis.Source => "原本の順序",
        StructureBasis.Inferred => "配置から推定（原本とは未照合）",
        StructureBasis.NeedsComparison => "配置から推定・要照合",
        StructureBasis.NotApplicable => "対象なし",
        _ => "未評価",
    };

    public static string TableStructureJapanese(StructureBasis basis, int reviewItems) => basis switch
    {
        StructureBasis.Source => "原本の表",
        StructureBasis.Inferred => "配置から推定（原本とは未照合）",
        StructureBasis.NeedsComparison => $"配置から推定・{reviewItems}か所要照合",
        StructureBasis.NotApplicable => "表なし",
        _ => "未評価",
    };

    /// <summary>One Japanese line for the GUI result panel: what was detected and how reading order
    /// and tables were obtained. It never says that no review is needed.</summary>
    public static string EvaluationJapanese(ExportSummary summary)
    {
        var detected = summary.ReviewItemsDetected || summary.HiddenContentIncluded ? "あり（上記）" : "なし";
        return $"検出された要確認事項: {detected}／読み順: {ReadingOrderJapanese(summary.ReadingOrder)}／" +
            $"表構造: {TableStructureJapanese(summary.TableStructure, summary.TableBoundaryReviewItems)}";
    }
}

public static class ExportSummaryBuilder
{
    /// <summary>Summary of an export without a readable projection, or with only its table count
    /// known. Reading order and table structure are then reported as not evaluated.</summary>
    public static ExportSummary Build(DocumentGraph graph, IReadOnlyList<Diagnostic> diagnostics, int? renderedTables = null) =>
        Create(graph, diagnostics, projection: null) with { RenderedTables = renderedTables };

    /// <summary>Summary of a readable export. <paramref name="projection"/> is the report of the
    /// serializer that wrote the Markdown: what it reconstructed from layout and which worksheet
    /// table boundaries need comparison.</summary>
    public static ExportSummary BuildReadable(DocumentGraph graph, IReadOnlyList<Diagnostic> diagnostics, ReadableProjectionReport projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return Create(graph, diagnostics, projection);
    }

    private static ExportSummary Create(DocumentGraph graph, IReadOnlyList<Diagnostic> diagnostics, ReadableProjectionReport? projection)
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
            RenderedTables: projection?.RenderedTables,
            TableBoundaryReviewItems: projection?.TableBoundaryReviews.Count ?? 0,
            ReadingOrder: ReadingOrderBasis(graph, projection),
            TableStructure: TableStructureBasis(graph, projection));
    }

    // Word body order is the file's own order. Worksheet rows are read in order unless regions that
    // sit side by side were written one after the other. Slide shapes and PDF text are ordered by
    // position: that order is inferred, and nothing here compares it with the source.
    private static StructureBasis ReadingOrderBasis(DocumentGraph graph, ReadableProjectionReport? projection) =>
        projection is null ? StructureBasis.NotEvaluated
        : !graph.Nodes.Any() ? StructureBasis.NotApplicable
        : graph.Format switch
        {
            DocumentFormatKind.Docx => StructureBasis.Source,
            DocumentFormatKind.Xlsx => projection.SideBySideRegions > 0 ? StructureBasis.Inferred : StructureBasis.Source,
            _ => StructureBasis.Inferred,
        };

    // Word and PowerPoint tables (and chart data) come from table markup, and a tagged PDF declares
    // its tables. Worksheet tables are formed from cell layout, PDF tables from drawn grids, and
    // PowerPoint tables synthesized from a grid of shapes from shape positions.
    private static StructureBasis TableStructureBasis(DocumentGraph graph, ReadableProjectionReport? projection)
    {
        if (projection is null) return StructureBasis.NotEvaluated;
        if (projection.RenderedTables == 0) return StructureBasis.NotApplicable;
        if (projection.TableBoundaryReviews.Count > 0) return StructureBasis.NeedsComparison;
        var tables = graph.Nodes.Where(node => node.Kind == NodeKind.Table).ToArray();
        return graph.Format switch
        {
            DocumentFormatKind.Xlsx => StructureBasis.Inferred,
            DocumentFormatKind.Pdf => tables.Length > 0 && tables.All(node =>
                node.Extensions?.TryGetValue("pdf_table_confidence", out var confidence) == true &&
                confidence.ValueKind == JsonValueKind.String && confidence.GetString() == "NativeTagged")
                ? StructureBasis.Source : StructureBasis.Inferred,
            _ => tables.Any(node => node.Extensions?.TryGetValue("shape_grid_table", out var grid) == true && grid.ValueKind == JsonValueKind.True)
                ? StructureBasis.Inferred : StructureBasis.Source,
        };
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
