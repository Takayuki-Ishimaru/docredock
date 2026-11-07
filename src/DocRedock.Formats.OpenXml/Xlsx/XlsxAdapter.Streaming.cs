using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;

namespace DocRedock.Formats.OpenXml.Xlsx;

public sealed partial class XlsxAdapter
{
    /// <summary>A workbook's parts, read from its ZIP archive. Each part is read through once when the package is opened,
    /// for its hash, so that a damaged part fails the extraction there as it always has; the small ones are kept. A large
    /// part (a worksheet of tens of thousands of rows is tens of megabytes of XML) is not: it is read again as a stream
    /// when it is needed (<see cref="OpenPart"/>), and never held whole.</summary>
    private sealed class XlsxPackage : IReadOnlyDictionary<string, byte[]>, IDisposable
    {
        private const long MaxKeptPartBytes = 4 << 20;
        private readonly ZipArchive zip;
        private readonly Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> kept = new(StringComparer.Ordinal);

        public XlsxPackage(byte[] bytes)
        {
            zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.Contains("..", StringComparison.Ordinal) || entry.FullName.StartsWith('/')) throw new InvalidDataException("Unsafe ZIP entry path.");
                entries[entry.FullName] = entry;
                if (entry.Length <= MaxKeptPartBytes)
                {
                    var part = ReadEntry(entry);
                    kept[entry.FullName] = part;
                    Hashes[entry.FullName] = Hash(part);
                }
                else
                {
                    kept.Remove(entry.FullName);
                    using var input = entry.Open();
                    Hashes[entry.FullName] = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
                }
            }
            if (!entries.ContainsKey("[Content_Types].xml")) throw new InvalidDataException("Not an OOXML package.");
        }

        /// <summary>The SHA-256 of each part, in the order of the archive.</summary>
        public Dictionary<string, string> Hashes { get; } = new(StringComparer.Ordinal);

        /// <summary>A part's content as a stream, read from the archive unless it is small enough to have been kept.</summary>
        public Stream OpenPart(string name) => kept.TryGetValue(name, out var part)
            ? new MemoryStream(part, writable: false)
            : entries[name].Open();

        private static byte[] ReadEntry(ZipArchiveEntry entry)
        {
            using var input = entry.Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }

        public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out byte[] value)
        {
            if (kept.TryGetValue(key, out value)) return true;
            if (!entries.TryGetValue(key, out var entry)) return false;
            value = ReadEntry(entry);
            return true;
        }

        public byte[] this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
        public bool ContainsKey(string key) => entries.ContainsKey(key);
        public IEnumerable<string> Keys => entries.Keys;
        public IEnumerable<byte[]> Values => entries.Keys.Select(key => this[key]);
        public int Count => entries.Count;
        public IEnumerator<KeyValuePair<string, byte[]>> GetEnumerator() =>
            entries.Keys.Select(key => new KeyValuePair<string, byte[]>(key, this[key])).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() => zip.Dispose();
    }

    /// <summary>What one pass over a worksheet's XML finds: its cells, and the properties of the sheet the projection
    /// reads. The XML is read as a stream, once; the properties are those the separate readers of the whole XML found
    /// (any element of that name, wherever it is).</summary>
    private sealed class WorksheetScan
    {
        public List<XlsxCellRecord> Cells { get; set; } = [];
        public HashSet<int> HiddenRows { get; } = [];
        public HashSet<int> HiddenColumns { get; } = [];
        public List<string> MergedRanges { get; } = [];
        public XlsxWorksheetMetrics Metrics { get; set; } = new(8.43d, 15d, new Dictionary<int, double>(), new Dictionary<int, double>());

        /// <summary>The warnings about the sizes of rows and columns, which the extraction adds where it reads the sizes.</summary>
        public List<string> MetricsWarnings { get; } = [];

        public string? DeclaredDimension { get; set; }
        public List<string> TableRelationshipIds { get; } = [];
        public Func<int, int, bool> IsHyperlinkCell { get; set; } = (_, _) => false;

        /// <summary>The conditionalFormatting elements in the worksheet's namespace, when they were asked for.</summary>
        public List<XmlElement> ConditionalFormats { get; } = [];
    }

    // The links of a sheet's XML, as the scan reads them: whether one covers the cell (row, column).
    private static Func<int, int, bool> ReadHyperlinkCells(byte[] bytes)
    {
        var scan = new WorksheetScan();
        ReadWorksheet(new MemoryStream(bytes, writable: false), scan, "Sheet1", [], [], [], [], false, "visible", readConditionalFormats: false);
        return scan.IsHyperlinkCell;
    }

    private static void ReadWorksheet(Stream xml, WorksheetScan scan, string sheet, Dictionary<string, XlsxString> shared, IReadOnlyList<XlsxCellStyle> styles,
        List<XlsxFormulaDiagnostic> diagnostics, List<string> warnings, bool uses1904DateSystem, string sheetState, bool readConditionalFormats)
    {
        var retainedCells = new List<ParsedWorksheetCell>();
        // Cells come in order of row and column. Only once one does not is each cell's place looked up, to keep the last
        // of two cells at one place.
        Dictionary<(int Row, int Column), int>? indexByCoordinate = null;
        var lastCoordinate = (Row: 0, Column: 0);
        var linkCells = new HashSet<(int Row, int Column)>();
        var linkAreas = new List<(int MinRow, int MaxRow, int MinColumn, int MaxColumn)>();
        var defaultColumnWidth = 8.43d;
        var defaultRowHeight = 15d;
        var columnWidths = new Dictionary<int, double>();
        var rowHeights = new Dictionary<int, double>();
        var examinedHiddenColumns = 0;
        var hiddenColumnsEnded = false;
        var dimensionRead = false;
        var rootNamespace = (string?)null;
        XmlDocument? formats = null;
        var sourceOrder = 0;
        using var reader = XmlReader.Create(xml, SafeXml);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            rootNamespace ??= reader.NamespaceURI;
            switch (reader.LocalName)
            {
                case "c":
                    ReadCell();
                    break;
                case "row":
                {
                    if (IsHiddenFlag(reader.GetAttribute("hidden")) &&
                        int.TryParse(reader.GetAttribute("r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hiddenRow) &&
                        hiddenRow is >= 1 and <= MaxExcelRows)
                        scan.HiddenRows.Add(hiddenRow);
                    var row = AttributeInt(reader, "r", 0);
                    var height = IsHiddenFlag(reader.GetAttribute("hidden")) ? 0d : ReadDimension(reader, "ht", defaultRowHeight, 409.5d, sheet, scan.MetricsWarnings);
                    if (row > 0) rowHeights[row] = height;
                    break;
                }
                case "col":
                {
                    ReadHiddenColumns();
                    var min = AttributeInt(reader, "min", 1);
                    var max = Math.Min(MaxExcelColumns, AttributeInt(reader, "max", min));
                    // X7: a hidden column contributes zero width to DrawingML anchor math (Excel
                    // visually collapses it), and an explicit width="0" column -- not necessarily
                    // marked hidden -- means the same thing. Both were previously ignored by a
                    // `width > 0` guard below, which fell back to the DEFAULT column width instead and
                    // skewed every absoluteAnchor arrow that crossed a hidden/zero-width column.
                    var width = IsHiddenFlag(reader.GetAttribute("hidden")) ? 0d : ReadDimension(reader, "width", defaultColumnWidth, 255d, sheet, scan.MetricsWarnings);
                    if (min <= max)
                        for (var column = Math.Max(1, min); column <= max; column++) columnWidths[column] = width;
                    break;
                }
                case "sheetFormatPr":
                    defaultColumnWidth = ReadDimension(reader, "defaultColWidth", defaultColumnWidth, 255d, sheet, scan.MetricsWarnings);
                    defaultRowHeight = ReadDimension(reader, "defaultRowHeight", defaultRowHeight, 409.5d, sheet, scan.MetricsWarnings);
                    break;
                case "mergeCell":
                    if (reader.GetAttribute("ref") is { } merged && !string.IsNullOrWhiteSpace(merged)) scan.MergedRanges.Add(merged);
                    break;
                case "dimension" when !dimensionRead:
                    dimensionRead = true;
                    scan.DeclaredDimension = reader.GetAttribute("ref");
                    break;
                case "tablePart":
                    if (reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships") is { Length: > 0 } tableId)
                        scan.TableRelationshipIds.Add(tableId);
                    break;
                case "hyperlink":
                    if (ParseRange(reader.GetAttribute("ref")) is { } area)
                    {
                        if (area.MinRow == area.MaxRow && area.MinColumn == area.MaxColumn) linkCells.Add((area.MinRow, area.MinColumn));
                        else linkAreas.Add(area);
                    }
                    break;
                case "conditionalFormatting" when readConditionalFormats && reader.NamespaceURI == rootNamespace:
                {
                    formats ??= new XmlDocument();
                    using var subtree = reader.ReadSubtree();
                    subtree.Read();
                    if (formats.ReadNode(subtree) is XmlElement format) scan.ConditionalFormats.Add(format);
                    break;
                }
            }
        }
        scan.Metrics = new(defaultColumnWidth, defaultRowHeight, columnWidths, rowHeights);
        var index = linkAreas.Count > 0 ? new LinkAreaIndex(linkAreas) : null;
        var isHyperlinkCell = linkCells.Count == 0 && index is null
            ? (_, _) => false
            : (Func<int, int, bool>)((row, column) => linkCells.Contains((row, column)) || index?.Covers(row, column) == true);
        scan.IsHyperlinkCell = isHyperlinkCell;

        // Resolve shared-formula link flags only from the winning record at each coordinate. A
        // discarded duplicate master must not mark a surviving shared-formula member as a link.
        // Build the map before creating any cells so XML order (including a winning master that
        // appears after its member) cannot change the result.
        var sharedLinks = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var cell in indexByCoordinate is null ? retainedCells : (IEnumerable<ParsedWorksheetCell>)retainedCells.OrderBy(cell => cell.SourceOrder))
            if (cell.SharedFormula is not null && !string.IsNullOrWhiteSpace(cell.Formula))
                sharedLinks[cell.SharedFormula] = HyperlinkFormula.IsMatch(cell.Formula);

        var result = new List<XlsxCellRecord>(retainedCells.Count);
        foreach (var sourceCell in retainedCells)
        {
            var reference = sourceCell.Reference;
            var type = sourceCell.Type;
            var style = sourceCell.Style;
            var formula = sourceCell.Formula;
            var sharedFormula = sourceCell.SharedFormula;
            var row = sourceCell.Row;
            var column = sourceCell.Column;
            var value = sourceCell.Value;
            var runs = sourceCell.Runs;
            if (type == "s" && value is not null && shared.TryGetValue(value, out var sharedValue)) (value, runs) = (sharedValue.Text, sharedValue.Runs);
            var displayStyle = int.TryParse(style, NumberStyles.Integer, CultureInfo.InvariantCulture, out var styleIndex) && styleIndex >= 0 && styleIndex < styles.Count
                ? styles[styleIndex]
                : null;
            var displayValue = FormatDisplayValue(value, type, displayStyle, uses1904DateSystem);
            // A displayed value that reads as the value is the value: one string for both.
            if (displayValue is not null && displayValue == value) displayValue = value;
            var richRuns = type is "s" or "inlineStr" ? runs : null;
            var formulaLinks = false;
            if (sharedFormula is not null && string.IsNullOrWhiteSpace(formula)) formulaLinks = sharedLinks.GetValueOrDefault(sharedFormula);
            else if (formula is not null)
            {
                formulaLinks = HyperlinkFormula.IsMatch(formula);
            }
            var isLink = formulaLinks || isHyperlinkCell(row, column);
            var cell = new XlsxCellRecord(sheet, reference, value, formula, style, type == "s", row, column, type, displayValue, displayStyle,
                IsHiddenRow: scan.HiddenRows.Contains(row), IsHiddenColumn: scan.HiddenColumns.Contains(column), SheetState: sheetState)
            {
                StruckRanges = MarkedRanges(value, richRuns, run => run.Strike, displayStyle?.IsStrike == true),
                UnderlinedRanges = isLink ? null : MarkedRanges(value, richRuns, run => run.Underline, displayStyle?.IsUnderline == true),
            };
            result.Add(cell);
        }
        scan.Cells = result;

        // The hidden columns of a col element, until the columns named reach the width of a sheet.
        void ReadHiddenColumns()
        {
            if (hiddenColumnsEnded || !IsHiddenFlag(reader.GetAttribute("hidden"))) return;
            if (!int.TryParse(reader.GetAttribute("min"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var min) ||
                !int.TryParse(reader.GetAttribute("max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var max) ||
                min < 1 || max < min || max > MaxExcelColumns) return;
            var span = max - min + 1;
            if (span > MaxExcelColumns - examinedHiddenColumns)
            {
                hiddenColumnsEnded = true;
                return;
            }
            examinedHiddenColumns += span;
            for (var column = min; ; column++)
            {
                scan.HiddenColumns.Add(column);
                if (column == max) break;
            }
        }

        // The c element the reader is on, read to its end (the reader is left on its end tag): its value, formula and
        // text, every run of an inline string, never the phonetic guide in place of the text.
        void ReadCell()
        {
            var reference = reader.GetAttribute("r") ?? ""; var type = Common(reader.GetAttribute("t")); var style = Common(reader.GetAttribute("s"));
            string? formula = null; string? value = null;
            string? sharedFormula = null;
            IReadOnlyList<XlsxRun>? runs = null;
            if (!reader.IsEmptyElement)
            {
                var depth = reader.Depth;
                reader.Read();
                while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "is")
                    {
                        var inline = ReadRichString(reader);
                        (value, runs) = (inline.Text, inline.Runs);
                    }
                    else if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "f" or "v" or "t")
                    {
                        var local = reader.LocalName;
                        if (local == "f" && reader.GetAttribute("t") == "shared") sharedFormula = reader.GetAttribute("si");
                        var content = reader.ReadElementContentAsString();
                        if (local == "f") formula = content;
                        else value = content;
                        continue;
                    }
                    reader.Read();
                }
            }
            var (row, column) = ParseCellReference(reference);
            if (row == 0 || column == 0) return;
            if (formula is not null)
            {
                diagnostics.Add(ClassifyFormula(reference, formula) with { SheetName = sheet });
                var cachedValue = value;
                if (type == "s" && cachedValue is not null && shared.TryGetValue(cachedValue, out var cachedSharedValue))
                    cachedValue = cachedSharedValue.Text;
                if (string.IsNullOrWhiteSpace(cachedValue))
                    warnings.Add($"XlsxFormulaCachedValueMissing: Formula cell {sheet}!{reference} has no cached value; DocRedock did not evaluate it.");
            }

            var coordinate = (row, column);
            var parsed = new ParsedWorksheetCell(reference, type, style, formula, value, sharedFormula, runs, row, column, sourceOrder);
            sourceOrder++;
            if (indexByCoordinate is null && coordinate.CompareTo(lastCoordinate) > 0)
            {
                lastCoordinate = coordinate;
                retainedCells.Add(parsed);
                return;
            }
            if (indexByCoordinate is null)
            {
                indexByCoordinate = new Dictionary<(int Row, int Column), int>(retainedCells.Count * 2);
                for (var position = 0; position < retainedCells.Count; position++)
                    indexByCoordinate[(retainedCells[position].Row, retainedCells[position].Column)] = position;
            }
            if (indexByCoordinate.TryGetValue(coordinate, out var existing))
            {
                warnings.Add($"XlsxDuplicateCellReference: {sheet}!{reference} occurred more than once; the last cell was retained.");
                retainedCells[existing] = parsed;
            }
            else
            {
                indexByCoordinate.Add(coordinate, retainedCells.Count);
                retainedCells.Add(parsed);
            }
        }
    }

    // A cell's type and style are a few short values repeated in every cell: one string for each.
    private static string? Common(string? value) => value switch
    {
        null => null,
        "s" => "s", "n" => "n", "b" => "b", "e" => "e", "str" => "str", "inlineStr" => "inlineStr", "d" => "d",
        { Length: <= 3 } when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
                              number < CommonStyleIds.Length && CommonStyleIds[number] == value => CommonStyleIds[number],
        _ => value,
    };

    private static readonly string[] CommonStyleIds = Enumerable.Range(0, 1000).Select(number => number.ToString(CultureInfo.InvariantCulture)).ToArray();
}
