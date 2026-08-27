using System.Diagnostics.CodeAnalysis;

namespace DocRedock.Core.Documents;

/// <summary>
/// A validated rectangular view of a table's visual grid.  The source rows remain
/// untouched; slots only point back to their physical origin cell.
/// </summary>
public sealed record TableGridSlot(
    int Row,
    int Column,
    int OriginRow,
    int OriginCellIndex,
    TableCell Origin,
    bool IsContinuation);

public sealed class TableGrid
{
    private TableGrid(IReadOnlyList<IReadOnlyList<TableGridSlot>> rows)
    {
        Rows = rows;
        RowCount = rows.Count;
        ColumnCount = rows.Count == 0 ? 0 : rows[0].Count;
    }

    public IReadOnlyList<IReadOnlyList<TableGridSlot>> Rows { get; }
    public int RowCount { get; }
    public int ColumnCount { get; }

    /// <summary>
    /// Validates spans and expands a physical table layout to visual coordinates.
    /// A RowSpan of zero is the canonical vertical-merge continuation placeholder.
    /// </summary>
    public static bool TryCreate(TableNodeContent table, [NotNullWhen(true)] out TableGrid? grid, out string? error)
    {
        ArgumentNullException.ThrowIfNull(table);
        grid = null;
        error = null;
        if (table.Rows.Count == 0)
        {
            grid = new TableGrid([]);
            return true;
        }

        var width = 0;
        foreach (var row in table.Rows)
        {
            var rowWidth = 0;
            foreach (var cell in row)
            {
                if (cell.ColSpan < 1 || cell.RowSpan < 0)
                {
                    error = "Table cells must have a positive column span and a non-negative row span.";
                    return false;
                }
                try { rowWidth = checked(rowWidth + cell.ColSpan); }
                catch (OverflowException)
                {
                    error = "Table column span is too large.";
                    return false;
                }
            }
            width = Math.Max(width, rowWidth);
        }
        if (width == 0)
        {
            error = "A non-empty table row has no visual columns.";
            return false;
        }

        var slots = Enumerable.Range(0, table.Rows.Count)
            .Select(_ => Enumerable.Repeat<TableGridSlot?>(null, width).ToArray())
            .ToArray();
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var column = 0;
            var row = table.Rows[rowIndex];
            for (var cellIndex = 0; cellIndex < row.Count; cellIndex++)
            {
                var cell = row[cellIndex];
                if (column + cell.ColSpan > width)
                {
                    error = "A table cell extends beyond the visual grid width.";
                    return false;
                }

                if (cell.RowSpan == 0)
                {
                    var origin = slots[rowIndex][column];
                    if (origin is null || origin.OriginRow == rowIndex)
                    {
                        error = "A vertical-merge continuation has no origin cell above it.";
                        return false;
                    }
                    for (var offset = 0; offset < cell.ColSpan; offset++)
                    {
                        var covered = slots[rowIndex][column + offset];
                        if (covered is null || covered.OriginRow != origin.OriginRow ||
                            covered.OriginCellIndex != origin.OriginCellIndex)
                        {
                            error = "A vertical-merge continuation does not match its origin span.";
                            return false;
                        }
                        slots[rowIndex][column + offset] = new TableGridSlot(
                            rowIndex, column + offset, covered.OriginRow, covered.OriginCellIndex,
                            covered.Origin, true);
                    }
                }
                else
                {
                    if (rowIndex + cell.RowSpan > table.Rows.Count)
                    {
                        error = "A table row span extends beyond the final row.";
                        return false;
                    }
                    for (var targetRow = rowIndex; targetRow < rowIndex + cell.RowSpan; targetRow++)
                    for (var offset = 0; offset < cell.ColSpan; offset++)
                    {
                        if (slots[targetRow][column + offset] is not null)
                        {
                            error = "Table spans overlap.";
                            return false;
                        }
                        slots[targetRow][column + offset] = new TableGridSlot(
                            targetRow, column + offset, rowIndex, cellIndex, cell,
                            targetRow != rowIndex || offset != 0);
                    }
                }
                column += cell.ColSpan;
            }
            if (column != width)
            {
                error = "Table rows do not have a consistent visual width.";
                return false;
            }
        }

        if (slots.Any(row => row.Any(slot => slot is null)))
        {
            error = "Table spans leave an unassigned visual grid slot.";
            return false;
        }
        grid = new TableGrid(slots.Select(row => (IReadOnlyList<TableGridSlot>)row.Select(slot => slot!).ToArray()).ToArray());
        return true;
    }
}
