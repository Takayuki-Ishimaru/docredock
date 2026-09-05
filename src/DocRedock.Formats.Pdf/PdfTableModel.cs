using DocRedock.Core.Documents;

namespace DocRedock.Formats.Pdf;

/// <summary>A conservatively reconstructed PDF table.  A table is present only when every
/// emitted cell has a unique geometric text assignment.</summary>
public sealed record PdfTable(
    string Id,
    int PageNumber,
    Geometry Bounds,
    IReadOnlyList<PdfTableRow> Rows,
    PdfTableConfidence Confidence,
    IReadOnlyList<string> SourcePathIds);

public sealed record PdfTableRow(IReadOnlyList<PdfTableCell> Cells);

public sealed record PdfTableCell(
    int Row,
    int Column,
    int RowSpan,
    int ColumnSpan,
    Geometry Bounds,
    string Text,
    IReadOnlyList<int> TextRegionIndexes);

public enum PdfTableConfidence { NativeTagged, HighConfidenceInferred }
