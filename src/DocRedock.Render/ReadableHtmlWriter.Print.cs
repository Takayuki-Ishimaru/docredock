using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;

namespace DocRedock.Render;

public static partial class ReadableHtmlWriter
{
    private sealed partial class HtmlDocumentWriter
    {
        private int _namedPageCount;

        private bool OutsidePrintArea(ReadableSourceOrigin? origin)
        {
            if (_sheet?.Print?.Areas is not { Count: > 0 } areas || origin?.Range is not { } range ||
                ParsePrintArea(range) is not { } bounds) return false;
            var parsed = areas.Select(ParsePrintArea).OfType<(int MinRow, int MaxRow, int MinColumn, int MaxColumn)>().ToArray();
            return parsed.Length > 0 && !parsed.Any(area => area.MinRow <= bounds.MaxRow && area.MaxRow >= bounds.MinRow &&
                area.MinColumn <= bounds.MaxColumn && area.MaxColumn >= bounds.MinColumn);
        }

        private string PrintPageAttributes()
        {
            if (_sheet?.Print is not { } print) return "";
            var name = "source-page-" + (++_namedPageCount).ToString(CultureInfo.InvariantCulture);
            var paper = print.PaperSize switch { 1 => "letter", 5 => "legal", 8 => "A3", 9 => "A4", 11 => "A5", _ => null };
            if (print.PaperSize is > 0 && paper is null) _diagnostics.Add(new("HtmlPrintSettingUnsupported", $"Paper size {print.PaperSize} is not mapped for {_sheet.Name}; the browser paper setting is used."));
            _pageStyles.Append("@page ").Append(name).Append('{');
            if (paper is not null || print.Orientation is "landscape" or "portrait")
                _pageStyles.Append("size:").Append(paper ?? "").Append(' ').Append(print.Orientation is "landscape" or "portrait" ? print.Orientation : "").Append(';');
            if (print.Margins is { } margins)
            {
                void Margin(string edge, double? inches)
                { if (inches is >= 0 and <= 10 and var value) _pageStyles.Append("margin-").Append(edge).Append(':').Append(value.ToString("0.####", CultureInfo.InvariantCulture)).Append("in;"); }
                Margin("top", margins.Top); Margin("right", margins.Right); Margin("bottom", margins.Bottom); Margin("left", margins.Left);
            }
            _pageStyles.Append("}\n");
            if (print.FitToHeight is > 0 || print.FitToWidth is > 1 || print.Scale is > 0 and not 100 || print.VerticalCentering == true)
                _diagnostics.Add(new("HtmlPrintScaleApproximation", $"{_sheet.Name}: browser printing fits the table to page width; Excel scale, fit-to-height and vertical centering cannot be reproduced exactly."));
            return " style=\"page:" + name + "\"";
        }

        private List<ReadableTable> PrintTables(ReadableTable table)
        {
            var copies = new List<ReadableTable>();
            if (table.Origin?.SheetName is null || table.Origin.Range is null || _sheet?.Print is not { } print || table.Rows.Count == 0 || table.Columns.Count == 0) return copies;
            var areas = new List<(int MinRow, int MaxRow, int MinColumn, int MaxColumn, string Label)>();
            foreach (var area in print.Areas ?? [])
                if (ParsePrintArea(area) is { } parsed) areas.Add((parsed.MinRow, parsed.MaxRow, parsed.MinColumn, parsed.MaxColumn, area));
            var columnBreaks = (print.ColumnBreaks ?? []).Where(c => c is > 0 and < 16384).Distinct().Order().ToArray();
            if (areas.Count == 0)
                areas.Add((1, 1048576, table.Columns.Min(c => c.SourceIndex), table.Columns.Max(c => c.SourceIndex), ""));
            if ((long)areas.Count * (columnBreaks.Length + 1) > 64)
            { columnBreaks = []; _diagnostics.Add(new("HtmlPrintSettingUnsupported", $"{_sheet.Name}: too many combinations of print areas and manual column breaks; areas are printed without column breaks.")); }
            var titles = ParseTitles(print.TitleRows, rows: true);
            var titleColumns = ParseTitles(print.TitleColumns, rows: false);
            foreach (var area in areas)
            {
                var first = area.MinColumn;
                foreach (var end in columnBreaks.Where(c => c >= first && c < area.MaxColumn).Append(area.MaxColumn))
                {
                    var selectedRows = Enumerable.Range(0, table.Rows.Count).Where(i => table.Rows[i].SourceRow is { } r &&
                        (r >= area.MinRow && r <= area.MaxRow || titles is { } title && r >= title.Start && r <= title.End)).ToArray();
                    var selectedColumns = Enumerable.Range(0, table.Columns.Count).Where(i =>
                        table.Columns[i].SourceIndex >= first && table.Columns[i].SourceIndex <= end ||
                        titleColumns is { } repeat && table.Columns[i].SourceIndex >= repeat.Start && table.Columns[i].SourceIndex <= repeat.End).ToArray();
                    if (selectedRows.Length > 0 && selectedColumns.Length > 0)
                        copies.Add(SlicePrintTable(table, selectedRows, selectedColumns, titles, area.Label));
                    first = end + 1;
                }
            }
            if (copies.Count == 0 && !OutsidePrintArea(table.Origin)) _diagnostics.Add(new("HtmlPrintAreaEmpty", $"{_sheet.Name}: the source print areas contain no selected cells; compare the browser print preview with the source."));
            return copies;
        }

        private ReadableTable SlicePrintTable(ReadableTable table, int[] selectedRows, int[] selectedColumns,
            (int Start, int End)? titles, string range)
        {
            var cellsByRow = selectedRows.ToDictionary(i => i, _ => new List<(int Column, ReadableTableCell Cell)>());
            var sourceColumns = table.Columns.Select((c, i) => (c.SourceIndex, Index: i)).ToDictionary(c => c.SourceIndex, c => c.Index);
            for (var r = 0; r < table.Rows.Count; r++)
            {
                _typedCancellation.ThrowIfCancellationRequested();
                var position = 0;
                foreach (var cell in table.Rows[r].Cells)
                {
                    if (cell.SourceColumn is { } source && sourceColumns.TryGetValue(source, out var sourceIndex)) position = sourceIndex;
                    var columnStart = LowerBound(selectedColumns, position); var columnEnd = LowerBound(selectedColumns, position + Math.Clamp(cell.ColSpan, 1, 16384));
                    var rowStart = LowerBound(selectedRows, r); var rowEnd = LowerBound(selectedRows, r + Math.Clamp(cell.RowSpan, 1, table.Rows.Count - r));
                    if (columnEnd > columnStart && rowEnd > rowStart && cell.RowSpan != 0)
                        cellsByRow[selectedRows[rowStart]].Add((selectedColumns[columnStart], cell with { ColSpan = columnEnd - columnStart, RowSpan = rowEnd - rowStart, SourceColumn = table.Columns[selectedColumns[columnStart]].SourceIndex }));
                    position += Math.Max(1, cell.ColSpan);
                }
            }
            var resultRows = selectedRows.Select(i => table.Rows[i] with { Cells = cellsByRow[i].OrderBy(c => c.Column).Select(c => c.Cell).ToArray() }).ToArray();
            var headerCount = selectedRows.TakeWhile(i => titles is { } title && table.Rows[i].SourceRow is { } r && r >= title.Start && r <= title.End).Count();
            return table with { Columns = selectedColumns.Select(i => table.Columns[i]).ToArray(), Rows = resultRows, HeaderRowCount = headerCount,
                Origin = table.Origin is null ? null : table.Origin with { Range = range.Length > 0 ? range : table.Origin.Range } };
        }

        private static (int MinRow, int MaxRow, int MinColumn, int MaxColumn)? ParsePrintArea(string range)
        {
            if (range.Length > 512) return null;
            var match = Regex.Match(range, @"^\$?([A-Za-z]{1,3})\$?([0-9]{1,7})(?::\$?([A-Za-z]{1,3})\$?([0-9]{1,7}))?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success) return null;
            var c1 = PrintColumn(match.Groups[1].Value); var c2 = match.Groups[3].Success ? PrintColumn(match.Groups[3].Value) : c1;
            if (!int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var r1)) return null;
            var r2 = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : r1;
            return r1 is > 0 and <= 1048576 && r2 >= r1 && r2 <= 1048576 && c1 > 0 && c2 >= c1 && c2 <= 16384 ? (r1, r2, c1, c2) : null;
        }

        private static (int Start, int End)? ParseTitles(string? range, bool rows)
        {
            if (range is null || range.Length > 64) return null;
            var parts = range.Replace("$", "", StringComparison.Ordinal).Split(':');
            if (parts.Length != 2) return null;
            var start = rows ? int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0 : PrintColumn(parts[0]);
            var end = rows ? int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var e) ? e : 0 : PrintColumn(parts[1]);
            return start > 0 && end >= start && end <= (rows ? 1048576 : 16384) ? (start, end) : null;
        }

        private static int PrintColumn(string text)
        {
            if (text.Length is < 1 or > 3) return 0;
            var value = 0;
            foreach (var c in text.ToUpperInvariant()) { if (c is < 'A' or > 'Z') return 0; value = value * 26 + c - 'A' + 1; }
            return value;
        }

        private static int LowerBound(int[] values, int value)
        { var index = Array.BinarySearch(values, value); return index < 0 ? ~index : index; }
    }
}
