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
    IReadOnlyList<string> SourcePathIds,
    // P-Overlay: schedule-arrow/bar/marker/line shapes drawn on top of this table's own rows and
    // columns -- see PdfTableOverlayDetector.Detect. Populated after inference (Infer itself never
    // sets this), trailing/optional so every existing positional PdfTable(...) construction across
    // the test suite keeps compiling.
    IReadOnlyList<PdfTableOverlay>? Overlays = null);

/// <summary>A shape (arrow/bar/marker/line) detected as visually overlaying a PDF table's
/// row/column range -- see table-overlay-spec-xlsx-docx-pdf.md section 3. Row/Column indices are
/// 0-based and End-inclusive, row 0 is the table's own top row (PDF user space grows upward, so
/// this is the opposite order from the raw Y axis). Serialized with default
/// <see cref="System.Text.Json.JsonSerializer"/> options (PascalCase) -- the same JSON contract
/// PptxTableOverlay/XlsxSheetOverlay use. <see cref="Text"/> is always "": a PDF overlay's label
/// text is native page text already assigned to the cell by table-cell text extraction, not part
/// of the vector shape itself, so there is nothing extra to carry here (see ReadableMarkdownSerializer
/// which requires no PDF-specific change: the label already precedes the glyph in the cell text).</summary>
public sealed record PdfTableOverlay(
    string ShapeId,
    string Text,
    string Kind,
    string Direction,
    string Axis,
    int StartRow, int EndRow,
    int StartColumn, int EndColumn,
    string? ShapePreset);

public sealed record PdfTableRow(IReadOnlyList<PdfTableCell> Cells);

public sealed record PdfTableCell(
    int Row,
    int Column,
    int RowSpan,
    int ColumnSpan,
    Geometry Bounds,
    string Text,
    /// <summary>Stable ids of the parsed text fragments assigned to this cell. These are never list
    /// positions: the region list is re-ordered and merged after inference runs.</summary>
    IReadOnlyList<int> SourceTextIds);

public enum PdfTableConfidence { NativeTagged, HighConfidenceInferred }
