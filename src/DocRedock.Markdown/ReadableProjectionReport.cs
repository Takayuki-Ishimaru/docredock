namespace DocRedock.Markdown;

/// <summary>
/// What the last readable projection reconstructed from layout rather than read from source
/// structure. <see cref="RenderedTables"/> counts GFM tables in the Markdown;
/// <see cref="SideBySideRegions"/> counts worksheet regions that sat beside another region on the
/// same rows and were therefore written one after the other; <see cref="TableBoundaryReviews"/>
/// lists blank columns at which two tables were separated although the layout could not show
/// whether their rows belong together.
/// </summary>
public sealed record ReadableProjectionReport(
    int RenderedTables,
    int SideBySideRegions,
    IReadOnlyList<ReadableTableBoundaryReview> TableBoundaryReviews)
{
    public static ReadableProjectionReport Empty { get; } = new(0, 0, []);
}

/// <summary>A blank worksheet column whose two sides were output as separate tables, with the
/// cell ranges a person should compare. Ranges are A1-style addresses on <see cref="SheetName"/>.</summary>
public sealed record ReadableTableBoundaryReview(
    string PartitionId,
    string SheetName,
    string GapRange,
    string LeftRange,
    string RightRange);

/// <summary>Where the last workbook projection wrote each worksheet, as character offsets into the
/// returned Markdown. Recorded only when <see cref="ReadableMarkdownSerializer.RecordWorkbookLayout"/>
/// is set, so a caller can cut a large table between rows without re-rendering or re-parsing it.</summary>
public sealed record ReadableWorkbookLayout(IReadOnlyList<ReadableSheetLayout> Sheets);

/// <param name="Start">Offset of the sheet heading.</param>
/// <param name="ContentStart">Offset just past the sheet heading, where the sheet's content begins.</param>
/// <param name="End">Offset just past the sheet's last content.</param>
public sealed record ReadableSheetLayout(
    string PartitionId,
    int Start,
    int ContentStart,
    int End,
    IReadOnlyList<ReadableSheetSegment> Segments);

/// <summary>Markdown written for one group of source nodes, in output order. A segment holding a
/// worksheet table also describes its header and every data row line.</summary>
public sealed record ReadableSheetSegment(
    int Start,
    int End,
    IReadOnlyList<string> NodeIds,
    ReadableSheetTable? Table = null);

/// <param name="HeaderEnd">Offset just past the table's separator line; the header lines are
/// <c>[segment.Start, HeaderEnd)</c>.</param>
/// <param name="HeaderRows">Worksheet rows the header was built from; empty when the table has a
/// blank header because its first row is already data.</param>
/// <param name="MinColumn">First worksheet column of the table (1-based).</param>
/// <param name="MaxColumn">Last worksheet column of the table (1-based).</param>
public sealed record ReadableSheetTable(
    int HeaderEnd,
    IReadOnlyList<string> HeaderNodeIds,
    IReadOnlyList<int> HeaderRows,
    int MinColumn,
    int MaxColumn,
    IReadOnlyList<ReadableSheetTableRow> Rows);

/// <summary>One data row line <c>[Start, End)</c> of a worksheet table and the source worksheet row.</summary>
public sealed record ReadableSheetTableRow(int Start, int End, int Row, IReadOnlyList<string> NodeIds);
