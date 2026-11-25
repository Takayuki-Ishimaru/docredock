using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Markdown;

namespace DocRedock.Api;

/// <summary>Options for the human-readable workbook review directory export. The document
/// options drive extraction and Markdown serialization; <see name="PortableMarkdown"/> selects
/// CommonMark-records output (no raw HTML) for the sheet files.</summary>
public sealed record WorkbookReviewExportOptions(ReadableDocumentExportOptions Document, string OutputDirectory, bool PortableMarkdown = true);

/// <summary>One file written into the review directory.</summary>
public sealed record WorkbookReviewFile(string Path, string? SheetName = null, string? PartitionId = null);

/// <summary>The published review directory and everything the conversion learned about the source.</summary>
public sealed record WorkbookReviewExportResult(
    string DirectoryPath,
    IReadOnlyList<WorkbookReviewFile> Files,
    DocumentGraph Graph,
    IReadOnlyList<Diagnostic> Diagnostics,
    ReadableProjectionReport Projection)
{
    public ReadableConversionReport? Conversion { get; init; }
}
