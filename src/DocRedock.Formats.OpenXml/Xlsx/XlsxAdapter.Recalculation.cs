using System.Globalization;
using System.Xml;

namespace DocRedock.Formats.OpenXml.Xlsx;

public sealed partial class XlsxAdapter
{
    // How many of the formula cells a warning names before it only counts the rest.
    private const int NamedFormulaCells = 5;

    /// <summary>The warning a restore gives when formulas keep results that the edits made stale: the formula cells that
    /// may read an edited cell, and the formulas the edits wrote, which have no saved result at all. DocRedock evaluates
    /// no formula; it marks the workbook for a full calculation when it is opened. Null when no formula is affected, as
    /// when the workbook has none or the edited cells feed none of them.</summary>
    private static string? RecalculationWarning(IReadOnlyDictionary<string, byte[]> package, WorkbookInfo workbook, IReadOnlyList<XlsxCellEdit> edits)
    {
        var readers = FormulaCellsReading(package, workbook, edits);
        var written = edits.Where(edit => edit.Formula is not null).Select(edit => edit.SheetName + "!" + edit.CellReference).ToArray();
        if (readers.Count == 0 && written.Length == 0) return null;
        var parts = new List<string>();
        if (readers.Count > 0)
            parts.Add($"{readers.Count.ToString(CultureInfo.InvariantCulture)} formula cell(s) may read the edited cells ({Named(readers)}) and keep their previous results");
        if (written.Length > 0)
            parts.Add($"{written.Length.ToString(CultureInfo.InvariantCulture)} edited formula(s) ({Named(written)}) have no saved result");
        return "XlsxRecalculationRequired: " + string.Join("; ", parts) +
            " until the workbook is recalculated. DocRedock set it to recalculate when Excel opens it; tools that read saved results see the old values until then.";

        static string Named(IReadOnlyList<string> cells) => string.Join(", ", cells.Take(NamedFormulaCells)) +
            (cells.Count > NamedFormulaCells ? $" and {(cells.Count - NamedFormulaCells).ToString(CultureInfo.InvariantCulture)} more" : "");
    }

    /// <summary>The formula cells of the workbook, other than the edited ones, whose formulas may read an edited cell:
    /// those whose references cover one, and those whose references their text does not settle (see
    /// <see cref="XlsxFormulaReferences"/>). A shared formula is named by the range it is shared over.</summary>
    private static List<string> FormulaCellsReading(IReadOnlyDictionary<string, byte[]> package, WorkbookInfo workbook, IReadOnlyList<XlsxCellEdit> edits)
    {
        var edited = edits.Select(edit => XlsxFormulaReferences.TryParseCell(edit.CellReference, out var column, out var row)
                ? (edit.SheetName, Column: column, Row: row) : (edit.SheetName, Column: 0, Row: 0))
            .Where(cell => cell.Column > 0).ToArray();
        var editedCells = edits.Select(edit => (edit.SheetName, edit.CellReference.Replace("$", "", StringComparison.Ordinal).ToUpperInvariant())).ToHashSet();
        var readers = new List<string>();
        var areas = new List<XlsxFormulaReferences.Area>();
        foreach (var sheet in workbook.Sheets)
        {
            if (!package.TryGetValue(sheet.PartUri, out var xml)) continue;
            using var reader = XmlReader.Create(new MemoryStream(xml), SafeXml);
            string? cell = null;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName == "c") { cell = reader.GetAttribute("r"); continue; }
                if (reader.LocalName != "f" || cell is null || reader.IsEmptyElement) continue;
                var type = reader.GetAttribute("t");
                var range = reader.GetAttribute("ref");
                var formula = reader.ReadElementContentAsString();
                // A cell of a shared formula without its text is covered by the cell that holds the text.
                if (string.IsNullOrWhiteSpace(formula) || editedCells.Contains((sheet.Name, cell.ToUpperInvariant()))) continue;
                var shared = type == "shared" && range is not null && range.Contains(':', StringComparison.Ordinal);
                var (extraColumns, extraRows) = shared ? Extent(range!) : (0, 0);
                areas.Clear();
                var reads = type == "dataTable" || !XlsxFormulaReferences.TryRead(formula, extraColumns, extraRows, areas) ||
                    areas.Any(area => edited.Any(target =>
                        string.Equals(area.Sheet ?? sheet.Name, target.SheetName, StringComparison.OrdinalIgnoreCase) && area.Contains(target.Column, target.Row)));
                if (reads) readers.Add(sheet.Name + "!" + (shared ? range : cell));
            }
        }
        return readers;

        static (int Columns, int Rows) Extent(string range)
        {
            var corners = range.Split(':');
            return XlsxFormulaReferences.TryParseCell(corners[0], out var firstColumn, out var firstRow) &&
                XlsxFormulaReferences.TryParseCell(corners[^1], out var lastColumn, out var lastRow)
                    ? (Math.Max(0, lastColumn - firstColumn), Math.Max(0, lastRow - firstRow)) : (XlsxFormulaReferences.MaxColumn, XlsxFormulaReferences.MaxRow);
        }
    }
}
