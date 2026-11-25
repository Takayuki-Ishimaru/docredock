using DocRedock.Core.Documents;

namespace DocRedock.Markdown;

/// <summary>Source selection and interpretation shared by readable output formats.</summary>
public sealed record ReadableProjectionOptions(
    string ContentPolicy = "visible",
    IReadOnlyList<string>? IncludedSheets = null,
    string? Title = null,
    string? FallbackTitle = null,
    string? Language = null,
    string? SourceName = null,
    bool IncludeDiagrams = true,
    OcrReviewMode OcrReview = OcrReviewMode.LowConfidence);

public sealed record ReadableProjectionResult(ReadableDocument Document,
    IReadOnlyList<MarkdownDiagnostic> Diagnostics, ReadableProjectionReport Report);
