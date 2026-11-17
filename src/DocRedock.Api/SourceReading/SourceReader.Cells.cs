using System.Globalization;
using System.Text;
using DocRedock.Formats.OpenXml.Xlsx;

namespace DocRedock.Api.SourceReading;

public sealed partial class SourceReader
{
    private const int MaxExcelRows = 1_048_576;
    private const int MaxExcelColumns = 16_384;
    private const int MaxListedHidden = 500;
    private const int MaxListedMerges = 200;

    private readonly record struct CellRange(int FirstRow, int FirstColumn, int LastRow, int LastColumn)
    {
        public bool Contains(int row, int column) => row >= FirstRow && row <= LastRow && column >= FirstColumn && column <= LastColumn;

        public bool Intersects(CellRange other) => FirstRow <= other.LastRow && other.FirstRow <= LastRow &&
            FirstColumn <= other.LastColumn && other.FirstColumn <= LastColumn;

        public override string ToString() => FirstRow == LastRow && FirstColumn == LastColumn
            ? Address(FirstRow, FirstColumn)
            : Address(FirstRow, FirstColumn) + ":" + Address(LastRow, LastColumn);
    }

    private SourceCellsResult ReadSheet(string input, string path, string requestedSheet, string? requestedRange, CancellationToken cancellationToken)
    {
        CellRange? requested = null;
        if (requestedRange is not null)
            requested = TryParseRange(requestedRange, out var parsed) ? parsed
                : throw new SourceReadException("invalid_range",
                    $"'{requestedRange}' is not a cell range. Use a cell such as B7 or a range such as A1:H40 (columns A to XFD, rows 1 to {MaxExcelRows.ToString(CultureInfo.InvariantCulture)}).");

        DocumentService.PreflightOfficePackage(path);
        XlsxExtractionResult extraction;
        using (var stream = File.OpenRead(path)) extraction = new XlsxAdapter().Extract(stream, cancellationToken);
        var sheets = extraction.Worksheets;
        var sheet = sheets.FirstOrDefault(candidate => string.Equals(candidate.Name, requestedSheet, StringComparison.Ordinal));
        if (sheet is null)
        {
            var folded = sheets.Where(candidate => string.Equals(candidate.Name, requestedSheet, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (folded.Length == 1) sheet = folded[0];
        }
        if (sheet is null)
            throw new SourceReadException("unknown_sheet",
                $"Unknown sheet '{requestedSheet}'. The sheets are: {string.Join(", ", sheets.Select(candidate => "\"" + candidate.Name + "\""))}.");

        var cells = sheet.Cells
            .Select(cell => (Cell: cell, Row: cell.RowIndex, Column: cell.ColumnIndex))
            .Where(item => item.Row > 0 && item.Column > 0).ToArray();
        CellRange? used = TryParseRange(sheet.UsedRange ?? "", out var declared) ? declared
            : cells.Length == 0 ? null
            : new CellRange(cells.Min(item => item.Row), cells.Min(item => item.Column), cells.Max(item => item.Row), cells.Max(item => item.Column));
        var range = requested ?? used;

        var selected = range is { } window
            ? cells.Where(item => !item.Cell.IsBlank && window.Contains(item.Row, item.Column)).OrderBy(item => item.Row).ThenBy(item => item.Column).ToArray()
            : [];
        var merges = sheet.MergedRanges?.Select(merge => TryParseRange(merge, out var parsedMerge) ? (CellRange?)parsedMerge : null)
            .OfType<CellRange>().ToArray() ?? [];
        var truncated = selected.Length > MaxCells;
        var returned = selected.Take(MaxCells).Select(item =>
        {
            var cell = item.Cell;
            var shown = cell.DisplayValue ?? cell.Value;
            var raw = cell.Value is not null && cell.DisplayValue is not null && !string.Equals(cell.Value, cell.DisplayValue, StringComparison.Ordinal)
                ? cell.Value : null;
            var merged = merges.Where(merge => merge.Contains(item.Row, item.Column)).Select(merge => (CellRange?)merge).FirstOrDefault();
            return new SourceCell(Address(item.Row, item.Column), shown, raw, string.IsNullOrEmpty(cell.Formula) ? null : cell.Formula, merged?.ToString());
        }).ToArray();

        var mergedInRange = range is { } scope ? merges.Where(merge => merge.Intersects(scope)).Take(MaxListedMerges).Select(merge => merge.ToString()).ToArray() : [];
        var hiddenRows = range is { } rowScope && sheet.HiddenRows is { } rows
            ? rows.Where(row => row >= rowScope.FirstRow && row <= rowScope.LastRow).Order().Take(MaxListedHidden).ToArray() : [];
        var hiddenColumns = range is { } columnScope && sheet.HiddenColumns is { } columns
            ? columns.Where(column => column >= columnScope.FirstColumn && column <= columnScope.LastColumn).Order().Take(MaxListedHidden).Select(ColumnName).ToArray() : [];

        string? note = null;
        if (truncated)
            note = $"Only the first {MaxCells.ToString(CultureInfo.InvariantCulture)} of {selected.Length.ToString(CultureInfo.InvariantCulture)} non-empty cells (row by row) are returned; " +
                $"read the rest with a narrower --range, starting after {returned[^1].Address}.";
        else if (range is null) note = "This sheet has no cells.";
        else if (requested is null) note = $"No --range was given, so the sheet's used range {range} was read.";
        return new SourceCellsResult
        {
            Input = input, Format = "xlsx",
            Locator = Locator("sheet", ("sheet", sheet.Name), ("range", range?.ToString())),
            Sheet = sheet.Name, SheetState = sheet.SheetState, Range = range?.ToString(), UsedRange = used?.ToString(),
            Cells = returned, Truncated = truncated, MergedRanges = mergedInRange, HiddenRows = hiddenRows, HiddenColumns = hiddenColumns,
            Note = note,
        };
    }

    /// <summary>A cell or a range of cells in A1 notation (dollar signs and lower case are accepted); the corners may come in any order.</summary>
    private static bool TryParseRange(string text, out CellRange range)
    {
        range = default;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 2 || !TryParseCell(parts[0], out var first)) return false;
        var last = first;
        if (parts.Length == 2 && !TryParseCell(parts[1], out last)) return false;
        range = new CellRange(Math.Min(first.Row, last.Row), Math.Min(first.Column, last.Column),
            Math.Max(first.Row, last.Row), Math.Max(first.Column, last.Column));
        return true;
    }

    private static bool TryParseCell(string text, out (int Row, int Column) cell)
    {
        cell = default;
        text = text.Replace("$", "", StringComparison.Ordinal).Trim();
        var split = 0;
        while (split < text.Length && split < 3 && char.IsAsciiLetter(text[split])) split++;
        if (split == 0 || split == text.Length || !int.TryParse(text.AsSpan(split), NumberStyles.None, CultureInfo.InvariantCulture, out var row) ||
            row is < 1 or > MaxExcelRows) return false;
        var column = 0;
        foreach (var letter in text[..split]) column = column * 26 + char.ToUpperInvariant(letter) - 'A' + 1;
        if (column > MaxExcelColumns) return false;
        cell = (row, column);
        return true;
    }

    private static string Address(int row, int column) => ColumnName(column) + row.ToString(CultureInfo.InvariantCulture);

    private static string ColumnName(int column)
    {
        var name = new StringBuilder();
        while (column > 0)
        {
            column--;
            name.Insert(0, (char)('A' + column % 26));
            column /= 26;
        }
        return name.ToString();
    }
}
