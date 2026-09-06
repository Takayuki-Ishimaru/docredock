using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml;

namespace DocRedock.Formats.OpenXml.Xlsx;

public enum XlsxFormulaSafety { Safe, Suspicious, Dangerous }
public sealed record XlsxFormulaDiagnostic(string CellReference, string Formula, XlsxFormulaSafety Safety, string? Reason = null, string? SheetName = null);
public sealed record XlsxCellRecord(
    string SheetName,
    string CellReference,
    string? Value,
    string? Formula,
    string? StyleId,
    bool IsSharedString,
    int RowIndex = 0,
    int ColumnIndex = 0,
    string? CellType = null,
    string? DisplayValue = null,
    XlsxCellStyle? DisplayStyle = null,
    int? MergedToRow = null,
    int? MergedToColumn = null,
    bool IsHiddenRow = false,
    bool IsHiddenColumn = false,
    string SheetState = "visible")
{
    public bool IsBlank => string.IsNullOrEmpty(Value) && string.IsNullOrEmpty(Formula);
}

/// <summary>Presentation properties resolved from styles.xml for readable projections.</summary>
public sealed record XlsxCellStyle(
    bool IsBold = false,
    bool HasFill = false,
    bool HasBorder = false,
    bool IsCentered = false,
    double? FontSize = null,
    string? NumberFormat = null);

/// <summary>A DrawingML shape anchored to worksheet coordinates.</summary>
public sealed record XlsxDrawingShapeRecord(
    string Id,
    string Name,
    string Geometry,
    int Column,
    int Row,
    long ColumnOffset,
    long RowOffset,
    long WidthEmu,
    long HeightEmu,
    int? ToColumn = null,
    int? ToRow = null,
    bool FlipHorizontal = false,
    bool FlipVertical = false,
    string? Text = null,
    string? LineDash = null,
    bool IsConnector = false,
    string? StartConnectionId = null,
    string? EndConnectionId = null,
    string? ParentGroupId = null,
    string AnchorKind = "oneCellAnchor",
    long AbsoluteXEmu = 0,
    long AbsoluteYEmu = 0,
    long AbsoluteWidthEmu = 0,
    long AbsoluteHeightEmu = 0,
    long ToColumnOffset = 0,
    long ToRowOffset = 0,
    string? DrawingPartUri = null,
    int AnchorIndex = 0)
{
    /// <summary>Absolute worksheet-space bounds after anchor and metric resolution.</summary>
    public XlsxDrawingBounds? AbsoluteBounds => AbsoluteWidthEmu > 0 || AbsoluteHeightEmu > 0
        ? new(AbsoluteXEmu, AbsoluteYEmu, AbsoluteWidthEmu, AbsoluteHeightEmu)
        : null;
}

public sealed record XlsxDrawingBounds(long XEmu, long YEmu, long WidthEmu, long HeightEmu)
{
    public long RightEmu => checked(XEmu + WidthEmu);
    public long BottomEmu => checked(YEmu + HeightEmu);
}

/// <summary>Worksheet column/row dimensions used to resolve DrawingML anchors.</summary>
public sealed record XlsxWorksheetMetrics(
    double DefaultColumnWidth = 8.43,
    double DefaultRowHeight = 15,
    IReadOnlyDictionary<int, double>? ColumnWidths = null,
    IReadOnlyDictionary<int, double>? RowHeights = null)
{
    private const double EmuPerPixel = 9525d;
    private const double EmuPerPoint = 12700d;

    public double ColumnWidth(int column) => ColumnWidths is not null && ColumnWidths.TryGetValue(column, out var width)
        ? width : DefaultColumnWidth;
    public double RowHeight(int row) => RowHeights is not null && RowHeights.TryGetValue(row, out var height)
        ? height : DefaultRowHeight;

    // Excel's width is measured in character units. This is the same bounded
    // conversion used by the desktop clients for the default Calibri grid.
    public static double ColumnWidthToPixels(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) return 0;
        var pixels = Math.Floor(((256d * width + Math.Floor(128d / 7d)) / 256d) * 7d);
        return Math.Max(0, pixels);
    }
    public static long ColumnWidthToEmu(double width) => checked((long)Math.Round(ColumnWidthToPixels(width) * EmuPerPixel));
    public static long RowHeightToEmu(double points) => checked((long)Math.Round(Math.Max(0, points) * EmuPerPoint));

    public long ColumnStartEmu(int column)
    {
        if (column <= 1) return 0;
        var defaultEmu = ColumnWidthToEmu(DefaultColumnWidth);
        var total = checked(defaultEmu * (column - 1L));
        if (ColumnWidths is not null)
            foreach (var item in ColumnWidths.Where(item => item.Key > 0 && item.Key < column))
                total = checked(total + ColumnWidthToEmu(item.Value) - defaultEmu);
        return total;
    }
    public long RowStartEmu(int row)
    {
        if (row <= 1) return 0;
        var defaultEmu = RowHeightToEmu(DefaultRowHeight);
        var total = checked(defaultEmu * (row - 1L));
        if (RowHeights is not null)
            foreach (var item in RowHeights.Where(item => item.Key > 0 && item.Key < row))
                total = checked(total + RowHeightToEmu(item.Value) - defaultEmu);
        return total;
    }
    public double ColumnFromEmu(long emu) => FractionalIndex(emu, ColumnStartEmu, column => ColumnWidthToEmu(ColumnWidth(column)), 16_384);
    public double RowFromEmu(long emu) => FractionalIndex(emu, RowStartEmu, row => RowHeightToEmu(RowHeight(row)), 1_048_576);

    private static double FractionalIndex(long emu, Func<int, long> start, Func<int, long> size, int maximum)
    {
        if (emu <= 0) return 1;
        var low = 1;
        var high = maximum;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (start(middle) <= emu) low = middle;
            else high = middle - 1;
        }
        var index = low;
        var width = Math.Max(1, size(index));
        return index + (emu - start(index)) / (double)width;
    }
}

/// <summary>A picture in an XLSX DrawingML part, including its worksheet anchor.</summary>
public sealed record XlsxPictureRecord(
    string Id,
    string Name,
    string? Description,
    string RelationshipId,
    string TargetPartUri,
    int? Column,
    int? Row,
    int? ToColumn,
    int? ToRow,
    long WidthEmu,
    long HeightEmu,
    string DrawingPartUri);

/// <summary>A native chart anchored in an XLSX DrawingML part.</summary>
public sealed record XlsxChartRecord(
    string Id,
    string Name,
    string? Title,
    string? Type,
    IReadOnlyList<OpenXmlChartSeries> Series,
    string RelationshipId,
    string ChartPartUri,
    int? Column,
    int? Row,
    string DrawingPartUri,
    IReadOnlyList<XlsxChartReference>? References = null,
    bool ReferencesParsed = true,
    bool IsHidden = false);

public sealed record XlsxChartReference(string SheetName, int MinRow, int MaxRow, int MinColumn, int MaxColumn);
/// <summary>Worksheet projection metadata used by the Markdown table projector.</summary>
public sealed record XlsxWorksheetRecord(
    string Name,
    string PartUri,
    IReadOnlyList<XlsxCellRecord> Cells,
    string? UsedRange = null,
    int MinRow = 0,
    int MaxRow = 0,
    int MinColumn = 0,
    int MaxColumn = 0,
    IReadOnlyList<string>? MergedRanges = null,
    IReadOnlyList<XlsxDrawingShapeRecord>? DrawingShapes = null,
    IReadOnlyList<XlsxPictureRecord>? Pictures = null,
    IReadOnlyList<XlsxChartRecord>? Charts = null,
    string SheetState = "visible",
    IReadOnlySet<int>? HiddenRows = null,
    IReadOnlySet<int>? HiddenColumns = null,
    XlsxWorksheetMetrics? Metrics = null)
{
    public int RowCount => MinRow == 0 || MaxRow < MinRow ? 0 : MaxRow - MinRow + 1;
    public int ColumnCount => MinColumn == 0 || MaxColumn < MinColumn ? 0 : MaxColumn - MinColumn + 1;
}
public sealed record XlsxExtractionResult(
    DocumentGraph Graph,
    IReadOnlyList<XlsxWorksheetRecord> Worksheets,
    IReadOnlyDictionary<string, string> SharedStrings,
    IReadOnlyList<XlsxFormulaDiagnostic> FormulaDiagnostics,
    IReadOnlyDictionary<string, string> PartSha256,
    IReadOnlyList<string> Warnings);

public sealed record XlsxCellEdit(string SheetName, string CellReference, string? Value = null, string? Formula = null, string? WorksheetPartUri = null);
public sealed record XlsxPatchOptions(bool AllowDangerousFormula = false);
public sealed record XlsxDirtyPartGraph(
    IReadOnlySet<string> DirtyParts,
    IReadOnlyDictionary<string, IReadOnlySet<string>> Reasons)
{
    public bool Contains(string partUri) => DirtyParts.Contains(partUri);
}
public sealed record XlsxPatchPlan(
    IReadOnlyList<XlsxCellEdit> Edits,
    XlsxDirtyPartGraph DirtyPartGraph,
    IReadOnlyList<XlsxFormulaDiagnostic> FormulaDiagnostics);
public sealed record XlsxRestoreResult(byte[] Bytes, bool IsByteIdentical, XlsxPatchPlan Plan, IReadOnlyList<string> Warnings);

/// <summary>BCL-only XLSX extractor and minimal cell patcher. Formulas are classified, never evaluated.</summary>
public sealed class XlsxAdapter
{
    private const int MaxExcelRows = 1_048_576;
    private const int MaxExcelColumns = 16_384;
    private const long MaxChartResolutionCells = 100_000;
    public TimeSpan? VisualInferenceTimeout { get; init; } = TimeSpan.FromSeconds(5);

    private static readonly Regex ChartCellReference = new(
        @"^(?:(?:'(?<quoted>(?:[^']|'')+)'|(?<plain>[^!]+))!)?\$?(?<startColumn>[A-Za-z]{1,3})\$?(?<startRow>\d+)(?::\$?(?<endColumn>[A-Za-z]{1,3})\$?(?<endRow>\d+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly XmlReaderSettings SafeXml = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = false,
        MaxCharactersFromEntities = 0
    };

    public XlsxExtractionResult Extract(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = ReadAll(source);
        var package = Open(bytes);
        var shared = ReadSharedStrings(package);
        var styles = ReadStyles(package);
        var relationships = ReadRelationships(package, "xl/_rels/workbook.xml.rels");
        var workbook = ReadWorkbook(package, relationships);
        var worksheets = new List<XlsxWorksheetRecord>();
        var partitions = new List<DocumentPartition>();
        var formulaDiagnostics = new List<XlsxFormulaDiagnostic>();
        var warnings = new List<string>();
        foreach (var part in package.Keys
                     .Where(part => part.StartsWith("xl/comments", StringComparison.OrdinalIgnoreCase) &&
                                    part.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(part => part, StringComparer.Ordinal))
            warnings.Add($"XlsxLegacyCommentsUnsupported: Legacy cell comments in '/{part}' are preserved in the source package but are not yet emitted in the projection.");
        var sheetVisibility = workbook.Sheets.ToDictionary(
            sheet => sheet.Name,
            sheet => package.TryGetValue(sheet.PartUri, out var worksheetXml)
                ? (sheet.IsHidden, Rows: ReadHiddenRows(worksheetXml), Columns: ReadHiddenColumns(worksheetXml))
                : (sheet.IsHidden, Rows: (IReadOnlySet<int>)new HashSet<int>(), Columns: (IReadOnlySet<int>)new HashSet<int>()),
            StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in workbook.Sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!package.TryGetValue(sheet.PartUri, out var xml)) continue;
            var mergedRanges = ReadMergedRanges(xml);
            var hiddenRows = ReadHiddenRows(xml);
            var hiddenColumns = ReadHiddenColumns(xml);
            var metrics = ReadWorksheetMetrics(xml);
            var cells = ApplyMergedRanges(ReadWorksheet(xml, sheet.Name, shared, styles, formulaDiagnostics, warnings, workbook.Uses1904DateSystem, hiddenRows, hiddenColumns, sheet.State), mergedRanges);
            var used = CalculateUsedRange(cells, mergedRanges, ReadDeclaredDimension(xml));
            var drawingShapes = ReadDrawingShapes(package, sheet.PartUri, metrics);
            var pictures = ReadPictures(package, sheet.PartUri, sheet.Name, warnings);
            var charts = ReadCharts(package, sheet.PartUri, sheet.Name, cells, sheetVisibility, warnings);
            var worksheet = new XlsxWorksheetRecord(sheet.Name, sheet.PartUri, cells, used.Range, used.MinRow, used.MaxRow, used.MinColumn, used.MaxColumn, mergedRanges, drawingShapes, pictures, charts, sheet.State, hiddenRows, hiddenColumns, metrics);
            worksheets.Add(worksheet);
            var nodes = cells
                .Where(cell => !string.IsNullOrWhiteSpace(cell.Value) || !string.IsNullOrWhiteSpace(cell.Formula))
                .Select((cell, index) => ToNode(cell, sheet.PartUri, index))
                .ToList();
            var diagrams = XlsxMermaidProjection.TryCreateAll(worksheet, nodes.Count, VisualInferenceTimeout, cancellationToken);
            if (diagrams.Count > 0)
            {
                nodes.AddRange(diagrams);
                foreach (var diagram in diagrams)
                {
                    if (diagram.Extensions is null || !diagram.Extensions.TryGetValue("visual_graph", out var raw)) continue;
                    VisualGraph? visualGraph;
                    try { visualGraph = raw.Deserialize<VisualGraph>(); }
                    catch (JsonException) { continue; }
                    foreach (var diagnostic in visualGraph?.Diagnostics ?? [])
                    {
                        var warning = $"{diagnostic.ToWarning()} (worksheet: {sheet.Name})";
                        if (!warnings.Contains(warning, StringComparer.Ordinal)) warnings.Add(warning);
                    }
                }
            }
            else if (drawingShapes.Count > 0)
                warnings.Add($"{sheet.Name}: {drawingShapes.Count} DrawingML shape(s) were retained but not projected as a diagram.");
            foreach (var picture in pictures)
                nodes.Add(ToPictureNode(sheet, picture, nodes.Count));
            foreach (var chart in charts)
                nodes.Add(ToChartNode(sheet, chart, nodes.Count));
            if (sheet.IsHidden)
            {
                nodes = nodes.Select(node => node with
                {
                    Layer = ContentLayer.Hidden,
                    Extensions = WithExtension(node.Extensions, "sheet_state", sheet.State)
                }).ToList();
            }
            partitions.Add(new DocumentPartition("sheet-" + sheet.Name, partitions.Count, nodes, sheet.PartUri));
        }
        var graph = new DocumentGraph("1.1", "doc_" + Hash(bytes)[..16], DocumentFormatKind.Xlsx, partitions);
        var hashes = package.ToDictionary(x => x.Key, x => Hash(x.Value), StringComparer.Ordinal);
        return new(graph, worksheets, shared, formulaDiagnostics, hashes, warnings);
    }

    public XlsxPatchPlan CreatePatchPlan(DocumentGraph baseline, DocumentGraph edited, XlsxPatchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(edited);
        options ??= new XlsxPatchOptions();
        var before = Cells(baseline).ToDictionary(x => (Sheet: x.Sheet, Ref: CellRef(x.Node)), x => x, StringTupleComparer.Instance);
        var after = Cells(edited).ToDictionary(x => (Sheet: x.Sheet, Ref: CellRef(x.Node)), x => x, StringTupleComparer.Instance);
        var edits = new List<XlsxCellEdit>();
        foreach (var key in after.Keys.OrderBy(x => x.Sheet, StringComparer.Ordinal).ThenBy(x => x.Ref, StringComparer.Ordinal))
        {
            if (!before.TryGetValue(key, out var old) || !StringComparer.Ordinal.Equals(old.Text, after[key].Text) || !StringComparer.Ordinal.Equals(old.Formula, after[key].Formula))
            {
                var formula = after[key].Formula;
                edits.Add(new(key.Sheet, key.Ref, formula is null ? after[key].Text : null, formula, after[key].Node.Source?.PartUri));
            }
        }
        var diagnostics = edits.Where(x => x.Formula is not null).Select(x => ClassifyFormula(x.CellReference, x.Formula!)).ToArray();
        if (!options.AllowDangerousFormula && diagnostics.Any(x => x.Safety == XlsxFormulaSafety.Dangerous))
            throw new InvalidOperationException("Dangerous formula changes require AllowDangerousFormula=true.");
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        var reasons = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            var key = (Sheet: edit.SheetName, Ref: edit.CellReference);
            var part = after.TryGetValue(key, out var cell) ? cell.Node.Source?.PartUri.TrimStart('/') : null;
            Add(part ?? "xl/worksheets/" + SafePartName(edit.SheetName) + ".xml", "worksheet-cell");
            Add("xl/workbook.xml", "calculation-required");
            if (edit.Formula is not null) Add("xl/calcChain.xml", "formula-dependency");
        }
        return new(edits, new XlsxDirtyPartGraph(dirty,
            reasons.ToDictionary(item => item.Key, item => (IReadOnlySet<string>)item.Value, StringComparer.Ordinal)), diagnostics);

        void Add(string part, string reason)
        {
            dirty.Add(part);
            if (!reasons.TryGetValue(part, out var values)) reasons[part] = values = new(StringComparer.Ordinal);
            values.Add(reason);
        }
    }

    public XlsxPatchPlan CreatePatchPlan(IEnumerable<XlsxCellEdit> edits, XlsxPatchOptions? options = null)
    {
        options ??= new XlsxPatchOptions();
        var list = edits.ToArray();
        var diagnostics = list.Where(x => x.Formula is not null).Select(x => ClassifyFormula(x.CellReference, x.Formula!)).ToArray();
        if (!options.AllowDangerousFormula && diagnostics.Any(x => x.Safety == XlsxFormulaSafety.Dangerous))
            throw new InvalidOperationException("Dangerous formula changes require AllowDangerousFormula=true.");
        return new(list, BuildDirtyPartGraph(list), diagnostics);
    }

    public XlsxRestoreResult Restore(Stream original, XlsxPatchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(plan);
        var source = ReadAll(original);
        if (plan.Edits.Count == 0) return new(source, true, plan, Array.Empty<string>());
        var package = Open(source);
        var workbook = ReadWorkbook(package, ReadRelationships(package, "xl/_rels/workbook.xml.rels"));
        foreach (var editGroup in plan.Edits.GroupBy(x => x.SheetName, StringComparer.Ordinal))
        {
            var partUri = editGroup.Select(x => x.WorksheetPartUri).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.TrimStart('/');
            var sheet = partUri is null ? workbook.Sheets.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.Name, editGroup.Key)) : workbook.Sheets.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.PartUri, partUri));
            if (sheet is null) throw new InvalidDataException($"Worksheet not found: {editGroup.Key}");
            var xml = package[sheet.PartUri];
            package[sheet.PartUri] = PatchWorksheet(xml, editGroup);
        }
        package["xl/workbook.xml"] = MarkWorkbookForRecalculation(package["xl/workbook.xml"]);
        var warnings = new List<string>
        {
            "Cell edits require formula recalculation; DocRedock requested a full calculation on the next workbook open without evaluating formulas."
        };
        if (plan.Edits.Any(edit => edit.Formula is not null) && package.ContainsKey("xl/calcChain.xml"))
        {
            package["xl/calcChain.xml"] = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><calcChain xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"/>");
            warnings.Add("Formula edits invalidated the calculation chain; no formula was evaluated by DocRedock.");
        }
        return new(WritePackage(package), false, plan, warnings);
    }

    public static XlsxFormulaDiagnostic ClassifyFormula(string cellReference, string formula)
    {
        var normalized = formula.TrimStart('=');
        if (normalized.Contains('[') || normalized.Contains("]", StringComparison.Ordinal) ||
            normalized.Contains("DDE", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("WEBSERVICE", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("CALL(", StringComparison.OrdinalIgnoreCase))
            return new(cellReference, formula, XlsxFormulaSafety.Dangerous, "External link, DDE, network, or native call expression.");
        if (normalized.Contains("INDIRECT", StringComparison.OrdinalIgnoreCase) || normalized.Contains("HYPERLINK", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("IMPORT", StringComparison.OrdinalIgnoreCase))
            return new(cellReference, formula, XlsxFormulaSafety.Suspicious, "Formula uses a dynamic or external-reference function.");
        return new(cellReference, formula, XlsxFormulaSafety.Safe);
    }

    public static XlsxDirtyPartGraph BuildDirtyPartGraph(IEnumerable<XlsxCellEdit> edits)
    {
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        var reasons = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            var sheet = edit.WorksheetPartUri?.TrimStart('/') ?? ("xl/worksheets/" + SafePartName(edit.SheetName) + ".xml");
            Add(sheet, "worksheet-cell");
            Add("xl/workbook.xml", "calculation-required");
            if (edit.Formula is not null) Add("xl/calcChain.xml", "formula-dependency");
            // New strings are emitted as inlineStr, so sharedStrings/styles/workbook remain byte-identical.
        }
        return new(dirty, reasons.ToDictionary(x => x.Key, x => (IReadOnlySet<string>)x.Value, StringComparer.Ordinal));
        void Add(string part, string reason) { dirty.Add(part); if (!reasons.TryGetValue(part, out var set)) reasons[part] = set = new(StringComparer.Ordinal); set.Add(reason); }
    }

    private static DocumentNode ToNode(XlsxCellRecord cell, string partUri, int order)
    {
        var extension = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        extension["sheet_name"] = JsonSerializer.SerializeToElement(cell.SheetName);
        extension["address"] = JsonSerializer.SerializeToElement(cell.CellReference);
        extension["row"] = JsonSerializer.SerializeToElement(cell.RowIndex);
        extension["column"] = JsonSerializer.SerializeToElement(cell.ColumnIndex);
        extension["is_blank"] = JsonSerializer.SerializeToElement(cell.IsBlank);
        extension["is_formula"] = JsonSerializer.SerializeToElement(cell.Formula is not null);
        extension["is_numeric"] = JsonSerializer.SerializeToElement(IsNumericCell(cell));
        if (cell.CellType is not null) extension["cell_type"] = JsonSerializer.SerializeToElement(cell.CellType);
        if (cell.Formula is not null) extension["formula"] = JsonSerializer.SerializeToElement(cell.Formula);
        if (cell.StyleId is not null) extension["style_id"] = JsonSerializer.SerializeToElement(cell.StyleId);
        if (cell.DisplayValue is not null) extension["display_value"] = JsonSerializer.SerializeToElement(cell.DisplayValue);
        if (cell.MergedToRow is not null) extension["merged_to_row"] = JsonSerializer.SerializeToElement(cell.MergedToRow);
        if (cell.MergedToColumn is not null) extension["merged_to_column"] = JsonSerializer.SerializeToElement(cell.MergedToColumn);
        extension["sheet_state"] = JsonSerializer.SerializeToElement(cell.SheetState);
        extension["hidden_row"] = JsonSerializer.SerializeToElement(cell.IsHiddenRow);
        extension["hidden_column"] = JsonSerializer.SerializeToElement(cell.IsHiddenColumn);
        if (cell.DisplayStyle is not null)
        {
            extension["is_bold"] = JsonSerializer.SerializeToElement(cell.DisplayStyle.IsBold);
            extension["has_fill"] = JsonSerializer.SerializeToElement(cell.DisplayStyle.HasFill);
            extension["has_border"] = JsonSerializer.SerializeToElement(cell.DisplayStyle.HasBorder);
            extension["is_centered"] = JsonSerializer.SerializeToElement(cell.DisplayStyle.IsCentered);
            if (cell.DisplayStyle.FontSize is not null) extension["font_size"] = JsonSerializer.SerializeToElement(cell.DisplayStyle.FontSize.Value);
            if (cell.DisplayStyle.NumberFormat is not null) extension["number_format"] = JsonSerializer.SerializeToElement(cell.DisplayStyle.NumberFormat);
        }
        var layer = cell.IsHiddenRow || cell.IsHiddenColumn || !string.Equals(cell.SheetState, "visible", StringComparison.OrdinalIgnoreCase)
            ? ContentLayer.Hidden
            : ContentLayer.Body;
        return new($"n_{Hash(cell.SheetName + "!" + cell.CellReference)[..16]}", NodeKind.Cell, null, order,
            layer, new TextNodeContent(cell.Value ?? string.Empty),
            new SourceAnchor("xlsx", partUri, [new AnchorLocator("cell_address", cell.CellReference)]), Extensions: extension);
    }

    private static DocumentNode ToPictureNode(SheetInfo sheet, XlsxPictureRecord picture, int order)
    {
        var extension = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["sheet_name"] = JsonSerializer.SerializeToElement(sheet.Name),
            ["drawing_part"] = JsonSerializer.SerializeToElement(picture.DrawingPartUri),
            ["image_relationship"] = JsonSerializer.SerializeToElement(picture.RelationshipId),
            ["picture_id"] = JsonSerializer.SerializeToElement(picture.Id),
            ["picture_name"] = JsonSerializer.SerializeToElement(picture.Name),
            ["width_emu"] = JsonSerializer.SerializeToElement(picture.WidthEmu),
            ["height_emu"] = JsonSerializer.SerializeToElement(picture.HeightEmu)
        };
        if (picture.Row is { } row) extension["row"] = JsonSerializer.SerializeToElement(row);
        if (picture.Column is { } column) extension["column"] = JsonSerializer.SerializeToElement(column);
        if (picture.ToRow is { } toRow) extension["to_row"] = JsonSerializer.SerializeToElement(toRow);
        if (picture.ToColumn is { } toColumn) extension["to_column"] = JsonSerializer.SerializeToElement(toColumn);
        if (picture.Row is { } addressRow && picture.Column is { } addressColumn)
        {
            var address = ColumnName(addressColumn) + addressRow.ToString(CultureInfo.InvariantCulture);
            extension["address"] = JsonSerializer.SerializeToElement(address);
        }

        var locators = new List<AnchorLocator>
        {
            new("drawing_part", picture.DrawingPartUri),
            new("image_relationship", picture.RelationshipId)
        };
        if (picture.Row is { } rowValue && picture.Column is { } columnValue)
            locators.Add(new("cell_address", ColumnName(columnValue) + rowValue.ToString(CultureInfo.InvariantCulture)));

        return new(
            "n_" + Hash($"{sheet.Name}!picture:{picture.DrawingPartUri}:{picture.Id}:{picture.RelationshipId}")[..16],
            NodeKind.Image,
            null,
            order,
            ContentLayer.Body,
            new ReferenceNodeContent(picture.TargetPartUri, picture.Description ?? picture.Name),
            new SourceAnchor("xlsx", sheet.PartUri, locators),
            Editability: NodeEditability.Protected,
            Provenance: [new ProvenanceItem(EvidenceKind.Native)],
            Extensions: extension);
    }

    private static DocumentNode ToChartNode(SheetInfo sheet, XlsxChartRecord chart, int order)
    {
        var extension = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["sheet_name"] = JsonSerializer.SerializeToElement(sheet.Name),
            ["drawing_part"] = JsonSerializer.SerializeToElement(chart.DrawingPartUri),
            ["chart_relationship"] = JsonSerializer.SerializeToElement(chart.RelationshipId),
            ["chart_part"] = JsonSerializer.SerializeToElement(chart.ChartPartUri),
            ["chart_name"] = JsonSerializer.SerializeToElement(chart.Name),
        };
        if (!string.IsNullOrWhiteSpace(chart.Title)) extension["chart_title"] = JsonSerializer.SerializeToElement(chart.Title);
        if (!string.IsNullOrWhiteSpace(chart.Type)) extension["chart_type"] = JsonSerializer.SerializeToElement(chart.Type);
        if (chart.Series.Count > 0) extension["chart_series"] = JsonSerializer.SerializeToElement(chart.Series);
        if (chart.IsHidden) extension["hidden_chart_source"] = JsonSerializer.SerializeToElement(true);
        if (chart.Row is { } row) extension["row"] = JsonSerializer.SerializeToElement(row);
        if (chart.Column is { } column) extension["column"] = JsonSerializer.SerializeToElement(column);
        if (chart.Row is { } addressRow && chart.Column is { } addressColumn)
            extension["address"] = JsonSerializer.SerializeToElement(ColumnName(addressColumn) + addressRow.ToString(CultureInfo.InvariantCulture));
        var locators = new List<AnchorLocator> { new("drawing_part", chart.DrawingPartUri), new("chart_relationship", chart.RelationshipId) };
        if (chart.Row is { } locatorRow && chart.Column is { } locatorColumn)
            locators.Add(new("cell_address", ColumnName(locatorColumn) + locatorRow.ToString(CultureInfo.InvariantCulture)));
        return new("n_" + Hash($"{sheet.Name}!chart:{chart.DrawingPartUri}:{chart.Id}:{chart.RelationshipId}")[..16], NodeKind.Chart, null, order,
            chart.IsHidden ? ContentLayer.Hidden : ContentLayer.Body, new TextNodeContent(chart.Title ?? chart.Name), new SourceAnchor("xlsx", sheet.PartUri, locators),
            Editability: NodeEditability.Protected, Provenance: [new ProvenanceItem(EvidenceKind.Native)], Extensions: extension);
    }

    private static IReadOnlyDictionary<string, JsonElement> WithExtension(IReadOnlyDictionary<string, JsonElement>? source, string key, string value)
    {
        var result = source is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(source, StringComparer.Ordinal);
        result[key] = JsonSerializer.SerializeToElement(value);
        return result;
    }

    private static bool IsNumericCell(XlsxCellRecord cell) =>
        (cell.CellType is null or "n") && double.TryParse(cell.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static IEnumerable<(DocumentNode Node, string Sheet, string Text, string? Formula)> Cells(DocumentGraph graph) => graph.Partitions
        .SelectMany(partition => partition.Nodes.Where(node => node.Kind == NodeKind.Cell).Select(node => (node, Sheet: node.Extensions is not null && node.Extensions.TryGetValue("sheet_name", out var sheetName) ? sheetName.GetString() ?? partition.Id : partition.Id.StartsWith("sheet-", StringComparison.Ordinal) ? partition.Id[6..] : partition.Id,
            Text: (node.Content as TextNodeContent)?.Text ?? string.Empty,
            Formula: node.Extensions is not null && node.Extensions.TryGetValue("formula", out var formula) ? formula.GetString() : null)));
    private static string CellRef(DocumentNode node) => node.Source?.Locators.FirstOrDefault(x => x.Kind == "cell_address")?.Value ?? node.Id;
    private static string SafePartName(string sheet) => sheet.Length == 0 ? "sheet1" : sheet.ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal);

    private static Dictionary<string, byte[]> Open(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.Contains("..", StringComparison.Ordinal) || entry.FullName.StartsWith("/", StringComparison.Ordinal)) throw new InvalidDataException("Unsafe ZIP entry path.");
            using var input = entry.Open(); using var output = new MemoryStream(); input.CopyTo(output); result[entry.FullName] = output.ToArray();
        }
        if (!result.ContainsKey("[Content_Types].xml")) throw new InvalidDataException("Not an OOXML package.");
        return result;
    }
    private static byte[] WritePackage(Dictionary<string, byte[]> parts)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var part in parts.OrderBy(x => x.Key, StringComparer.Ordinal)) { var entry = zip.CreateEntry(part.Key, CompressionLevel.Optimal); using var output = entry.Open(); output.Write(part.Value); }
        return stream.ToArray();
    }
    private static byte[] ReadAll(Stream stream) { using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray(); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

    private static Dictionary<string, string> ReadSharedStrings(Dictionary<string, byte[]> package)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!package.TryGetValue("xl/sharedStrings.xml", out var bytes)) return result;
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        var index = 0; var text = new StringBuilder();
        while (reader.Read())
        {
            if (reader is { NodeType: XmlNodeType.Element, LocalName: "si" })
            {
                text.Clear();
                using var sub = reader.ReadSubtree();
                var phoneticDepth = -1;
                while (sub.Read())
                {
                    if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "rPh") phoneticDepth = sub.Depth;
                    else if (sub.NodeType == XmlNodeType.EndElement && sub.LocalName == "rPh") phoneticDepth = -1;
                    else if (sub.NodeType == XmlNodeType.Text && phoneticDepth < 0) text.Append(sub.Value);
                }
                result[index++.ToString(CultureInfo.InvariantCulture)] = text.ToString();
            }
        }
        return result;
    }
    private sealed record SheetInfo(string Name, string PartUri, string State)
    {
        public bool IsHidden => !string.Equals(State, "visible", StringComparison.OrdinalIgnoreCase);
    }
    private sealed record WorkbookInfo(IReadOnlyList<SheetInfo> Sheets, bool Uses1904DateSystem);
    private static WorkbookInfo ReadWorkbook(Dictionary<string, byte[]> package, Dictionary<string, string> relationships)
    {
        var result = new List<SheetInfo>();
        if (!package.TryGetValue("xl/workbook.xml", out var bytes)) throw new InvalidDataException("Workbook part missing.");
        var uses1904DateSystem = false;
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName == "workbookPr")
            {
                var value = reader.GetAttribute("date1904");
                uses1904DateSystem = value is "1" || bool.TryParse(value, out var parsed) && parsed;
            }
            else if (reader.LocalName == "sheet")
            {
                var name = reader.GetAttribute("name") ?? "Sheet" + (result.Count + 1);
                var rid = reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships") ?? reader.GetAttribute("r:id") ?? "";
                var state = reader.GetAttribute("state") ?? "visible";
                if (relationships.TryGetValue(rid, out var target)) result.Add(new(name, target, state));
            }
        }
        return new(result, uses1904DateSystem);
    }
    private static Dictionary<string, string> ReadRelationships(Dictionary<string, byte[]> package, string relsPath)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!package.TryGetValue(relsPath, out var bytes)) return result;
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        while (reader.Read()) if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship")
        {
            var id = reader.GetAttribute("Id"); var target = reader.GetAttribute("Target");
            if (id is null || target is null) continue;
            var basePath = relsPath[..relsPath.LastIndexOf("/_rels/", StringComparison.Ordinal)];
            result[id] = target.StartsWith("/", StringComparison.Ordinal)
                ? NormalizePartPath(target)
                : NormalizePartPath(basePath + "/" + target);
        }
        return result;
    }

    private static string NormalizePartPath(string value)
    {
        var stack = new List<string>();
        foreach (var segment in value.Replace("\\", "/", StringComparison.Ordinal).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); continue; }
            stack.Add(segment);
        }
        return string.Join('/', stack);
    }
    private static IReadOnlySet<int> ReadHiddenRows(byte[] bytes)
    {
        var result = new HashSet<int>();
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row" && IsHiddenFlag(reader.GetAttribute("hidden")) &&
                int.TryParse(reader.GetAttribute("r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var row) &&
                row is >= 1 and <= MaxExcelRows)
                result.Add(row);
        return result;
    }

    private static IReadOnlySet<int> ReadHiddenColumns(byte[] bytes)
    {
        var result = new HashSet<int>();
        var examinedColumns = 0;
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "col" || !IsHiddenFlag(reader.GetAttribute("hidden"))) continue;
            if (!int.TryParse(reader.GetAttribute("min"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var min) ||
                !int.TryParse(reader.GetAttribute("max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var max) ||
                min < 1 || max < min || max > MaxExcelColumns) continue;
            var span = max - min + 1;
            if (span > MaxExcelColumns - examinedColumns) break;
            examinedColumns += span;
            for (var column = min; ; column++)
            {
                result.Add(column);
                if (column == max) break;
            }
        }
        return result;
    }

    private static bool IsHiddenFlag(string? value) => value is "1" || bool.TryParse(value, out var parsed) && parsed;

    private static List<XlsxCellRecord> ReadWorksheet(byte[] bytes, string sheet, Dictionary<string, string> shared, IReadOnlyList<XlsxCellStyle> styles, List<XlsxFormulaDiagnostic> diagnostics, List<string> warnings, bool uses1904DateSystem, IReadOnlySet<int> hiddenRows, IReadOnlySet<int> hiddenColumns, string sheetState)
    {
        var result = new List<XlsxCellRecord>(); using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        while (reader.Read()) if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c")
        {
            var reference = reader.GetAttribute("r") ?? ""; var type = reader.GetAttribute("t"); var style = reader.GetAttribute("s"); string? formula = null; string? value = null;
            using var sub = reader.ReadSubtree();
            sub.Read();
            while (!sub.EOF)
            {
                if (sub.NodeType == XmlNodeType.Element && sub.LocalName is "f" or "v" or "t")
                {
                    var local = sub.LocalName;
                    var content = sub.ReadElementContentAsString();
                    if (local == "f") formula = content;
                    else if (local == "v") value = content;
                    else if (local == "t") value = content;
                    continue;
                }
                sub.Read();
            }
            var (row, column) = ParseCellReference(reference);
            if (row == 0 || column == 0) continue;
            if (type == "s" && value is not null && shared.TryGetValue(value, out var sharedValue)) value = sharedValue;
            if (formula is not null)
            {
                diagnostics.Add(ClassifyFormula(reference, formula) with { SheetName = sheet });
                if (string.IsNullOrWhiteSpace(value))
                    warnings.Add($"XlsxFormulaCachedValueMissing: Formula cell {sheet}!{reference} has no cached value; DocRedock did not evaluate it.");
            }
            var displayStyle = int.TryParse(style, NumberStyles.Integer, CultureInfo.InvariantCulture, out var styleIndex) && styleIndex >= 0 && styleIndex < styles.Count
                ? styles[styleIndex]
                : null;
            var displayValue = FormatDisplayValue(value, type, displayStyle, uses1904DateSystem);
            result.Add(new(sheet, reference, value, formula, style, type == "s", row, column, type, displayValue, displayStyle,
                IsHiddenRow: hiddenRows.Contains(row), IsHiddenColumn: hiddenColumns.Contains(column), SheetState: sheetState));
        }
        return result;
    }

    private static IReadOnlyList<XlsxCellStyle> ReadStyles(Dictionary<string, byte[]> package)
    {
        if (!package.TryGetValue("xl/styles.xml", out var bytes)) return [];
        var document = new XmlDocument();
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        document.Load(reader);
        var root = document.DocumentElement;
        if (root is null) return [];

        var fonts = DirectChild(root, "fonts")?.ChildNodes.OfType<XmlElement>().Where(x => x.LocalName == "font")
            .Select(font => new
            {
                Bold = DirectChild(font, "b") is not null,
                Size = double.TryParse(DirectChild(font, "sz")?.GetAttribute("val"), NumberStyles.Float, CultureInfo.InvariantCulture, out var size) ? size : (double?)null
            }).ToArray() ?? [];
        var fills = DirectChild(root, "fills")?.ChildNodes.OfType<XmlElement>().Where(x => x.LocalName == "fill")
            .Select(fill => DirectChild(fill, "patternFill")?.GetAttribute("patternType") is { Length: > 0 } pattern && pattern is not "none" and not "gray125").ToArray() ?? [];
        var borders = DirectChild(root, "borders")?.ChildNodes.OfType<XmlElement>().Where(x => x.LocalName == "border")
            .Select(border => border.ChildNodes.OfType<XmlElement>().Any(side => side.LocalName is "left" or "right" or "top" or "bottom" && !string.IsNullOrWhiteSpace(side.GetAttribute("style")))).ToArray() ?? [];
        var formats = DirectChild(root, "numFmts")?.ChildNodes.OfType<XmlElement>().Where(x => x.LocalName == "numFmt")
            .Select(format => (Id: int.TryParse(format.GetAttribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : -1, Format: format.GetAttribute("formatCode")))
            .Where(item => item.Id >= 0).ToDictionary(item => item.Id, item => item.Format) ?? new Dictionary<int, string>();

        var xfs = DirectChild(root, "cellXfs")?.ChildNodes.OfType<XmlElement>().Where(x => x.LocalName == "xf") ?? [];
        return xfs.Select(xf =>
        {
            var fontId = AttributeInt(xf, "fontId");
            var fillId = AttributeInt(xf, "fillId");
            var borderId = AttributeInt(xf, "borderId");
            var numberFormatId = AttributeInt(xf, "numFmtId");
            var alignment = DirectChild(xf, "alignment")?.GetAttribute("horizontal");
            return new XlsxCellStyle(
                fontId.HasValue && fontId.Value >= 0 && fontId.Value < fonts.Length && fonts[fontId.Value].Bold,
                fillId.HasValue && fillId.Value >= 0 && fillId.Value < fills.Length && fills[fillId.Value],
                borderId.HasValue && borderId.Value >= 0 && borderId.Value < borders.Length && borders[borderId.Value],
                alignment is "center" or "centerContinuous" or "distributed",
                fontId.HasValue && fontId.Value >= 0 && fontId.Value < fonts.Length ? fonts[fontId.Value].Size : null,
                numberFormatId is null ? null : ResolveNumberFormat(numberFormatId.Value, formats));
        }).ToArray();
    }

    private static int? AttributeInt(XmlElement element, string attribute) =>
        int.TryParse(element.GetAttribute(attribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string? ResolveNumberFormat(int id, IReadOnlyDictionary<int, string> custom) =>
        custom.TryGetValue(id, out var format) ? format : id switch
        {
            // ECMA-376 18.8.30 built-in formats. Ids 5-8 are locale-dependent currency formats and
            // ids 41-44 are accounting formats whose currency symbol also depends on the locale, so
            // those keep only their numeric shape (grouping, decimals, parenthesised negatives, "-"
            // for zero) and never invent a currency glyph.
            1 => "0", 2 => "0.00", 3 => "#,##0", 4 => "#,##0.00",
            9 => "0%", 10 => "0.00%", 11 => "0.00E+00", 12 => "# ?/?", 13 => "# ??/??",
            14 => "yyyy-MM-dd", 15 => "d-MMM-yy", 16 => "d-MMM", 17 => "MMM-yy", 18 => "h:mm tt", 19 => "h:mm:ss tt",
            20 => "h:mm", 21 => "h:mm:ss", 22 => "yyyy-MM-dd h:mm",
            37 => "#,##0;(#,##0)", 38 => "#,##0;[Red](#,##0)", 39 => "#,##0.00;(#,##0.00)", 40 => "#,##0.00;[Red](#,##0.00)",
            41 or 42 => "#,##0;(#,##0);\"-\"", 43 or 44 => "#,##0.00;(#,##0.00);\"-\"",
            45 => "mm:ss", 46 => "[h]:mm:ss", 47 => "mmss.0", 48 => "0.00E+00", 49 => "@",
            _ => null
        };

    private sealed record NumberFormatAnalysis(bool HasDate, bool HasTime, bool IsElapsed, bool HasSeconds, bool HasAmPm, bool HasPercent, bool HasGrouping, int DecimalPlaces, string? Suffix);

    private const int MaxNumberFormatLength = 256;
    private const int MaxPlaceholderDigits = 64;

    private static string? FormatDisplayValue(string? raw, string? cellType, XlsxCellStyle? style, bool uses1904DateSystem)
    {
        if (raw is null || cellType is "s" or "str" or "inlineStr" or "b" || style?.NumberFormat is not { Length: > 0 } format) return raw;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return raw;
        if (format.Length > MaxNumberFormatLength) return raw;

        var analysis = AnalyzeNumberFormat(format, number);
        if (analysis.IsElapsed) return FormatElapsed(number, analysis);
        if (analysis.HasDate || analysis.HasTime)
        {
            try
            {
                if (analysis.HasDate)
                {
                    var date = uses1904DateSystem
                        ? new DateTime(1904, 1, 1).AddDays(number)
                        : DateTime.FromOADate(number);
                    return date.ToString(analysis.HasTime ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
                return FormatClock(number, analysis);
            }
            catch (ArgumentException) { return raw; }
        }
        try
        {
            return FormatNumericSections(format, number) ?? raw;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or IndexOutOfRangeException)
        {
            return raw;
        }
    }

    // Renders the numeric (non date/time/elapsed) sections of an Excel custom number format against
    // a value. When at least one section carries a leading "[&lt;op&gt;value]" condition (see
    // TryParseLeadingCondition), the format switches to condition-driven dispatch: sections are tested
    // against the raw signed value in order and the first true condition wins, falling back to the
    // first unconditioned section, and finally to section 0 if every section is conditional and none
    // matched. A matched conditional section always renders from the value's absolute magnitude with
    // no automatic sign prepended - exactly like a classic dedicated negative section, the format's own
    // literal text (e.g. "-0.0") is responsible for any sign glyph. Without any condition, the classic
    // positive;negative;zero;text grammar applies: negative values are always rendered from their
    // absolute value, with a dedicated negative section supplying its own sign glyph (e.g. "(...)" or a
    // literal "-") while the absence of one falls back to the positive section with a single leading
    // "-" prepended after rendering.
    private static string? FormatNumericSections(string format, double number)
    {
        if (double.IsNaN(number) || double.IsInfinity(number)) return null;

        var wholeTrimmed = format.Trim();
        if (wholeTrimmed.Length == 0 || wholeTrimmed.Equals("General", StringComparison.OrdinalIgnoreCase) || wholeTrimmed == "@")
            return null;

        var sections = SplitFormatSections(format);
        var conditions = sections.Select(TryParseLeadingCondition).ToList();
        if (conditions.Any(condition => condition is not null))
        {
            var matchedIndex = -1;
            for (var i = 0; i < sections.Count; i++)
            {
                if (conditions[i] is { } condition && EvaluateCondition(condition.Op, condition.Threshold, number)) { matchedIndex = i; break; }
            }
            if (matchedIndex < 0) matchedIndex = conditions.FindIndex(condition => condition is null);
            if (matchedIndex < 0) matchedIndex = 0;
            return RenderSection(sections[matchedIndex], Math.Abs(number));
        }

        string section;
        double value;
        var prependMinus = false;
        if (number < 0 && sections.Count >= 2) { section = sections[1]; value = Math.Abs(number); }
        else if (number < 0) { section = sections[0]; value = Math.Abs(number); prependMinus = true; }
        else if (number == 0 && sections.Count >= 3) { section = sections[2]; value = 0; }
        else { section = sections[0]; value = number; }

        var rendered = RenderSection(section, value);
        if (rendered is null) return null;
        return prependMinus && rendered.Length > 0 ? "-" + rendered : rendered;
    }

    private static readonly Regex ConditionTagPattern = new(
        @"^(<=|>=|<>|<|>|=)(-?\d+(?:\.\d+)?)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Parses a leading "[&lt;op&gt;value]" condition tag from a section's opening run of
    /// bracketed tags - colour and condition tags may appear in either order, e.g. "[Red][&gt;=1000]#,##0"
    /// or "[&gt;=1000][Red]#,##0" - stopping at the first character that is not part of that leading
    /// bracket run. Returns null when no leading tag parses as a numeric condition, meaning the section
    /// is unconditioned.</summary>
    private static (string Op, double Threshold)? TryParseLeadingCondition(string section)
    {
        var i = 0;
        while (i < section.Length && section[i] == '[')
        {
            var end = section.IndexOf(']', i + 1);
            if (end < 0) break;
            var match = ConditionTagPattern.Match(section[(i + 1)..end]);
            if (match.Success && double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
                return (match.Groups[1].Value, threshold);
            i = end + 1;
        }
        return null;
    }

    private static bool EvaluateCondition(string op, double threshold, double value) => op switch
    {
        "<" => value < threshold,
        "<=" => value <= threshold,
        ">" => value > threshold,
        ">=" => value >= threshold,
        "=" => value == threshold,
        "<>" => value != threshold,
        _ => false
    };

    /// <summary>Splits a format string on top-level ';' separators, leaving quoted ("...") and
    /// bracketed ([...]) runs untouched so a literal semicolon inside either never splits a section.</summary>
    private static List<string> SplitFormatSections(string format)
    {
        var sections = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c == '\\' && i + 1 < format.Length) { current.Append(c).Append(format[i + 1]); i++; continue; }
            if (c == '"') { inQuote = !inQuote; current.Append(c); continue; }
            if (!inQuote && c == '[')
            {
                var end = format.IndexOf(']', i + 1);
                if (end >= 0) { current.Append(format, i, end - i + 1); i = end; continue; }
            }
            if (!inQuote && c == ';') { sections.Add(current.ToString()); current.Clear(); continue; }
            current.Append(c);
        }
        sections.Add(current.ToString());
        return sections;
    }

    private static string StripBracketsAndTrim(string section)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < section.Length; i++)
        {
            if (section[i] == '[')
            {
                var end = section.IndexOf(']', i + 1);
                if (end >= 0) { i = end; continue; }
            }
            sb.Append(section[i]);
        }
        return sb.ToString().Trim();
    }

    private static string? RenderSection(string section, double value)
    {
        var stripped = StripBracketsAndTrim(section);
        if (stripped.Length == 0) return string.Empty;
        if (stripped.Equals("General", StringComparison.OrdinalIgnoreCase) || stripped == "@") return null;

        if (TryRenderFraction(stripped, value, out var fractionRendered)) return fractionRendered;

        var tokens = Tokenize(section);
        if (tokens.HasText) return null;
        return tokens.IsExponent ? RenderExponent(tokens, value) : RenderFixedPoint(tokens, value);
    }

    // The leading placeholder run only counts as an integer part when it is followed by at least one
    // space/tab before the numerator - "int" and "sep" sit in one optional group that must match both
    // or neither. Without that constraint, a naive "[0#?]*[ \t]*(numerator)" would greedily backtrack
    // an integer-less run like "??/??" into a one-char "integer" placeholder plus a one-char numerator
    // (regex backtracking stops at the first shape that matches at all, not the intended one), instead
    // of leaving the whole two-char run as the numerator. Numerator/denominator placeholders stay
    // '?'-only (matching the pre-existing grammar); only the integer run may mix '0'/'#'/'?'.
    private static readonly Regex FractionSectionPattern = new(
        @"\G(?:(?<int>[0#?]+)(?<sep>[ \t]+))?(?<num>\?+)/(?:(?<denph>\?+)|(?<denfixed>[0-9]+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Bare characters Excel treats as literals without quoting inside a number section. Anything
    // else outside quotes must be a placeholder or an operator, so it ends a literal run.
    private const string BareFractionLiterals = "-+()$¥€£:!^&'~{}=<> ";

    /// <summary>Consumes the literal tokens Excel allows around a fraction core - quoted text,
    /// backslash escapes, <c>_x</c> (a space), <c>*x</c> (nothing) and bare sign/currency/space
    /// characters - starting at <paramref name="index"/>, appending their display text.</summary>
    private static void ReadFractionLiterals(string section, ref int index, StringBuilder text)
    {
        while (index < section.Length)
        {
            var character = section[index];
            if (character == '"')
            {
                var close = section.IndexOf('"', index + 1);
                if (close < 0) { text.Append(section, index + 1, section.Length - index - 1); index = section.Length; return; }
                text.Append(section, index + 1, close - index - 1);
                index = close + 1;
            }
            else if (character == '\\' && index + 1 < section.Length) { text.Append(section[index + 1]); index += 2; }
            else if (character == '_' && index + 1 < section.Length) { text.Append(' '); index += 2; }
            else if (character == '*' && index + 1 < section.Length) { index += 2; }
            else if (BareFractionLiterals.Contains(character)) { text.Append(character); index++; }
            else return;
        }
    }

    // Bounds the magnitude TryRenderFraction will run its denominator search against. A remainder is
    // mathematically within [0, 1) whenever the section has an integer-part placeholder (it is always
    // value - floor(value)), but an integer-less section ("?/?") feeds the *whole* value in as an
    // improper fraction, so a pathologically large cell value must not reach the numerator/denominator
    // int casts below: remainder * maxDenominator (itself capped at 999,999) must stay well inside
    // int.MaxValue for every candidate denominator, which this bound comfortably guarantees.
    private const double MaxFractionRemainderMagnitude = 1000;

    /// <summary>Detects and renders an Excel fraction section ("# ?/?", "# ??/??", "# ?/4", "?/?", ...),
    /// reproducing Excel's TEXT()-style column alignment rather than a packed string: '?' pads its slot
    /// with a space, '0' pads with zero, '#' pads with nothing, and literal characters (the int/fraction
    /// separator, '/') are echoed verbatim. The integer part is right-aligned (padded on the left) via
    /// <see cref="RenderIntegerPart"/> - the same helper fixed-point rendering uses for a mixed
    /// '0'/'#'/'?' run; the numerator is likewise right-aligned (<c>PadLeft</c>); the denominator is
    /// left-aligned (padded on the right, <c>PadRight</c>) - both with spaces, since <see
    /// cref="FractionSectionPattern"/> only allows '?' there. A literal digit denominator ("# ?/4") is
    /// echoed exactly as written (never padded), with the numerator rounded to it and never reduced
    /// (matching Excel). A placeholder denominator ("?"/"??"/"???" =&gt; max 9/99/999, capped at six
    /// digits as a safety bound) is found by scanning every candidate denominator from 1 up to that max
    /// for the smallest rounding error, ties broken in favour of the smaller denominator.
    ///
    /// A remainder that rounds all the way up to the next whole unit (numerator == denominator) carries
    /// into the integer part - but only when the section has one to carry into; an integer-less section
    /// has no such slot, so it is left as numerator == denominator (e.g. "1/1") rather than silently
    /// dropping the unit. Once carrying is resolved, a zero numerator renders one of two ways: with an
    /// integer part, Excel blanks the whole separator+numerator+'/'+denominator run with spaces of the
    /// same total width, so a whole-number cell still lines up in a column with sibling cells that do
    /// show a fraction (e.g. "# ?/?" on an exact 3 =&gt; "3    "); without one there is no integer slot
    /// to fall back on, so the value keeps rendering as a literal fraction (e.g. "?/?" on 0 =&gt; "0/1",
    /// which falls out of the normal numerator/denominator path with no extra special-casing, since the
    /// search above already resolves a zero remainder to numerator 0 over the smallest denominator 1).
    ///
    /// <paramref name="rendered"/> comes back null (caller falls back to raw, matching
    /// FormatNumericSections' other "cannot render" cases) for a non-finite or unreasonably large
    /// remainder - see <see cref="MaxFractionRemainderMagnitude"/> - so no unchecked numeric cast ever
    /// overflows. The method itself returns false only when the section is not this exact shape at all,
    /// letting the caller fall back to the general placeholder tokenizer instead. Every alignment space
    /// stays in the returned text; a caller wanting a leading sign glyph (see FormatNumericSections'
    /// classic single-section negative fallback) prepends it in front of this method's own leading
    /// spaces, exactly like Excel's own "-" + formatted-absolute-value composition.</summary>
    private static bool TryRenderFraction(string strippedSection, double value, out string? rendered)
    {
        rendered = null;
        // "-# ?/?", "(# ?/?)" or "# ?/?\" kg\"": literals may surround the fraction core, and the
        // core itself must then account for every remaining character of the section.
        var prefix = new StringBuilder();
        var coreStart = 0;
        ReadFractionLiterals(strippedSection, ref coreStart, prefix);
        var match = FractionSectionPattern.Match(strippedSection, coreStart);
        if (!match.Success) return false;
        var suffix = new StringBuilder();
        var suffixStart = match.Index + match.Length;
        ReadFractionLiterals(strippedSection, ref suffixStart, suffix);
        if (suffixStart != strippedSection.Length) return false;

        var hasIntegerPart = match.Groups["int"].Success;
        double integerPart;
        double remainder;
        if (hasIntegerPart) { integerPart = Math.Floor(value); remainder = value - integerPart; }
        else { integerPart = 0; remainder = value; }

        if (!double.IsFinite(remainder) || Math.Abs(remainder) >= MaxFractionRemainderMagnitude) return true;

        var numWidth = match.Groups["num"].Value.Length;
        int numerator;
        int denominator;
        string? denominatorLiteral;
        int denWidth;
        if (match.Groups["denfixed"].Success)
        {
            var denfixedText = match.Groups["denfixed"].Value;
            if (!int.TryParse(denfixedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out denominator) || denominator <= 0)
                return false;
            numerator = (int)Math.Round(remainder * denominator, MidpointRounding.AwayFromZero);
            denominatorLiteral = denfixedText;
            denWidth = denfixedText.Length;
        }
        else
        {
            var placeholderDigits = Math.Min(match.Groups["denph"].Value.Length, 6);
            var maxDenominator = Math.Max((int)Math.Pow(10, placeholderDigits) - 1, 1);
            denominator = 1;
            numerator = (int)Math.Round(remainder, MidpointRounding.AwayFromZero);
            var bestError = Math.Abs(remainder - numerator);
            for (var d = 2; d <= maxDenominator; d++)
            {
                var n = (int)Math.Round(remainder * d, MidpointRounding.AwayFromZero);
                var error = Math.Abs(remainder - (double)n / d);
                if (error < bestError - 1e-12) { bestError = error; numerator = n; denominator = d; }
            }
            denominatorLiteral = null;
            denWidth = match.Groups["denph"].Value.Length;
        }

        // Only an integer-bearing section absorbs the carried unit; an integer-less one has nowhere to
        // put it, so numerator == denominator is left standing (e.g. "1/1") instead of vanishing.
        if (numerator == denominator && hasIntegerPart) { integerPart += 1; numerator = 0; }

        var sb = new StringBuilder();
        if (hasIntegerPart)
        {
            var integerDigits = integerPart.ToString("F0", CultureInfo.InvariantCulture);
            var integerText = RenderIntegerPart(integerDigits, match.Groups["int"].Value.ToList());
            // A value that is exactly zero must still show a digit: "#"-only integer placeholders
            // would otherwise leave the whole cell blank once the fraction part is blanked too.
            if (numerator == 0 && integerText.Trim().Length == 0)
                integerText = "0".PadLeft(Math.Max(1, integerText.Length), ' ');
            sb.Append(integerText);

            if (numerator == 0)
            {
                // Nothing left to show as a fraction: blank the separator + numerator + '/' +
                // denominator with spaces of the same total width so this cell's integer still lines
                // up in a column with sibling cells that do render a visible fraction.
                sb.Append(' ', match.Groups["sep"].Value.Length + numWidth + 1 + denWidth);
                rendered = prefix + sb.ToString() + suffix;
                return true;
            }

            sb.Append(match.Groups["sep"].Value);
        }

        sb.Append(numerator.ToString(CultureInfo.InvariantCulture).PadLeft(numWidth, ' '));
        sb.Append('/');
        sb.Append(denominatorLiteral ?? denominator.ToString(CultureInfo.InvariantCulture).PadRight(denWidth, ' '));

        rendered = prefix + sb.ToString() + suffix;
        return true;
    }

    /// <summary>Positions (in the raw section string) of digit placeholders ('0','#','?') that are not
    /// inside a quoted literal, a backslash escape, or a bracketed color/condition tag. Used to decide
    /// whether a comma sits between placeholders (grouping) or after all of them (scale).</summary>
    private static List<int> ScanPlaceholderPositions(string section)
    {
        var positions = new List<int>();
        var i = 0;
        while (i < section.Length)
        {
            var c = section[i];
            if (c == '[') { var end = section.IndexOf(']', i + 1); i = end >= 0 ? end + 1 : i + 1; continue; }
            if (c == '"') { var end = section.IndexOf('"', i + 1); i = end >= 0 ? end + 1 : section.Length; continue; }
            if (c == '\\' && i + 1 < section.Length) { i += 2; continue; }
            if (c is '0' or '#' or '?') { positions.Add(i); i++; continue; }
            i++;
        }
        return positions;
    }

    private sealed class SectionTokens
    {
        public readonly List<char> IntPlaceholders = new();
        public readonly List<char> DecimalPlaceholders = new();
        public bool HasGrouping;
        public int ScaleCommaCount;
        public bool IsPercent;
        public bool HasText;
        public bool IsExponent;
        public char ExponentSign = '+';
        public int ExponentDigitCount;
        public bool ExponentUpper = true;
        public readonly List<(string Text, bool IsQuotedStyle)> PrefixLiterals = new();
        public readonly List<(string Text, bool IsQuotedStyle)> SuffixLiterals = new();
    }

    /// <summary>Tokenizes one number-format section into placeholder runs, grouping/scale/percent flags,
    /// an optional exponent spec, and ordered prefix/suffix literal runs. Digit placeholders are capped at
    /// <see cref="MaxPlaceholderDigits"/> so a pathological format cannot blow up rendering; each comma is
    /// classified as grouping or scale from <see cref="ScanPlaceholderPositions"/>.</summary>
    private static SectionTokens Tokenize(string section)
    {
        var t = new SectionTokens();
        var positions = ScanPlaceholderPositions(section);
        var lastPlaceholderIndex = positions.Count > 0 ? positions[^1] : -1;
        var placeholderSeenCount = 0;
        var inDecimalPart = false;
        var i = 0;
        while (i < section.Length)
        {
            var c = section[i];
            if (c == '[')
            {
                var end = section.IndexOf(']', i + 1);
                i = end >= 0 ? end + 1 : i + 1;
                continue;
            }
            if (c == '"')
            {
                var end = section.IndexOf('"', i + 1);
                var text = end >= 0 ? section[(i + 1)..end] : section[(i + 1)..];
                AddLiteral(text, true);
                i = end >= 0 ? end + 1 : section.Length;
                continue;
            }
            if (c == '\\' && i + 1 < section.Length)
            {
                var next = section[i + 1];
                if (next == ',')
                {
                    ClassifyComma(i);
                    i += 2;
                    continue;
                }
                if (next == '"')
                {
                    var closeSlash = section.IndexOf('\\', i + 2);
                    if (closeSlash >= 0 && closeSlash + 1 < section.Length && section[closeSlash + 1] == '"')
                    {
                        AddLiteral(section[(i + 2)..closeSlash], true);
                        i = closeSlash + 2;
                        continue;
                    }
                    AddLiteral("\"", false);
                    i += 2;
                    continue;
                }
                AddLiteral(next.ToString(), false);
                i += 2;
                continue;
            }
            if (c == '_' && i + 1 < section.Length) { AddLiteral(" ", false); i += 2; continue; }
            if (c == '*' && i + 1 < section.Length) { i += 2; continue; }
            if (c is '0' or '#' or '?')
            {
                if (placeholderSeenCount < MaxPlaceholderDigits)
                    (inDecimalPart ? t.DecimalPlaceholders : t.IntPlaceholders).Add(c);
                placeholderSeenCount++;
                i++;
                continue;
            }
            if (c == '.' && !inDecimalPart)
            {
                inDecimalPart = true;
                i++;
                continue;
            }
            if (c == ',')
            {
                ClassifyComma(i);
                i++;
                continue;
            }
            if (c == '%')
            {
                t.IsPercent = true;
                AddLiteral("%", false);
                i++;
                continue;
            }
            if ((c == 'E' || c == 'e') && i + 1 < section.Length && (section[i + 1] == '+' || section[i + 1] == '-'))
            {
                t.IsExponent = true;
                t.ExponentUpper = c == 'E';
                t.ExponentSign = section[i + 1];
                i += 2;
                var digitCount = 0;
                while (i < section.Length && section[i] is '0' or '#' or '?' && digitCount < MaxPlaceholderDigits)
                {
                    digitCount++;
                    i++;
                }
                t.ExponentDigitCount = digitCount;
                placeholderSeenCount++;
                continue;
            }
            if (c == '@') { t.HasText = true; i++; continue; }
            AddLiteral(c.ToString(), false);
            i++;
        }
        return t;

        void AddLiteral(string text, bool isQuotedStyle) => (placeholderSeenCount == 0 ? t.PrefixLiterals : t.SuffixLiterals).Add((text, isQuotedStyle));
        void ClassifyComma(int index) { if (index < lastPlaceholderIndex) t.HasGrouping = true; else t.ScaleCommaCount++; }
    }

    private static string RenderFixedPoint(SectionTokens t, double value)
    {
        // A section with no digit placeholder at all (e.g. the literal-only "-" zero-section in
        // "#,##0;-#,##0;\"-\"", or a condition's "\"big\"") is a constant: it never displays the value,
        // regardless of magnitude, so numeric rendering is skipped entirely.
        if (t.IntPlaceholders.Count == 0 && t.DecimalPlaceholders.Count == 0)
        {
            var literalOnly = new StringBuilder();
            foreach (var (text, _) in t.PrefixLiterals) literalOnly.Append(text);
            foreach (var (text, _) in t.SuffixLiterals) literalOnly.Append(text);
            return literalOnly.ToString();
        }

        var v = value;
        if (t.IsPercent) v *= 100;
        if (t.ScaleCommaCount > 0) v /= Math.Pow(1000, t.ScaleCommaCount);

        var decimalCount = t.DecimalPlaceholders.Count;
        var mathDecimalCount = Math.Min(decimalCount, 15);
        var rounded = Math.Round(v, mathDecimalCount, MidpointRounding.AwayFromZero);
        var fixedString = rounded.ToString("F" + mathDecimalCount, CultureInfo.InvariantCulture);
        var dotIndex = fixedString.IndexOf('.');
        var integerDigits = (dotIndex >= 0 ? fixedString[..dotIndex] : fixedString).TrimStart('-');
        var decimalDigitsRaw = dotIndex >= 0 ? fixedString[(dotIndex + 1)..] : string.Empty;
        if (decimalCount > mathDecimalCount) decimalDigitsRaw += new string('0', decimalCount - mathDecimalCount);

        var integerOutput = RenderIntegerPart(integerDigits, t.IntPlaceholders);
        if (t.HasGrouping && integerOutput.Length > 0) integerOutput = ApplyGrouping(integerOutput);
        var decimalOutput = RenderDecimalPart(decimalDigitsRaw, t.DecimalPlaceholders);
        var numeric = integerOutput + decimalOutput;

        var sb = new StringBuilder();
        foreach (var (text, _) in t.PrefixLiterals) sb.Append(text);
        sb.Append(numeric);
        for (var index = 0; index < t.SuffixLiterals.Count; index++)
        {
            var (text, isQuoted) = t.SuffixLiterals[index];
            if (index == 0 && isQuoted && numeric.Length > 0 && NeedsCompatibilitySpace(text)) sb.Append(' ');
            sb.Append(text);
        }
        return sb.ToString();
    }

    // A quoted unit written directly after the digits ("円" in #,##0"円") historically rendered with
    // one separating space, and readers rely on that. A literal that already begins with its own
    // whitespace (#,##0" 円", the form Excel itself writes) must not receive a second one.
    private static bool NeedsCompatibilitySpace(string quotedLiteral) =>
        quotedLiteral.Length > 0 && !char.IsWhiteSpace(quotedLiteral[0]);

    // Scientific/engineering notation. The exponent is forced to a multiple of n, the number of integer
    // placeholders before the decimal point ("0.00E+00" has n=1, giving classic single-digit scientific
    // notation; "##0.0E+0" has n=3, Excel's "engineering" form, e.g. 12345 => "12.3E+3"). The mantissa's
    // integer part is then rendered through the same placeholder-aware helpers as fixed-point numbers
    // (0=zero-pad, #/?=no leading pad) so it always lands in [1, 10^n) - or exactly 0 for a zero value -
    // with a post-rounding carry (mirroring RenderFixedPoint's own rounding) if rounding the mantissa
    // pushes it up to 10^n. Assumes value is never negative: callers always render from the absolute
    // magnitude, prepending any sign to the whole rendered section outside of this method.
    private static string RenderExponent(SectionTokens t, double value)
    {
        var placeholderCount = Math.Max(t.IntPlaceholders.Count, 1);
        int exponent;
        double mantissa;
        if (value == 0) { exponent = 0; mantissa = 0; }
        else
        {
            var trueExponent = (int)Math.Floor(Math.Log10(value));
            exponent = placeholderCount * FloorDiv(trueExponent, placeholderCount);
            mantissa = value / Math.Pow(10, exponent);
        }
        var decimals = Math.Min(t.DecimalPlaceholders.Count, 15);
        mantissa = Math.Round(mantissa, decimals, MidpointRounding.AwayFromZero);
        var upperBound = Math.Pow(10, placeholderCount);
        if (mantissa >= upperBound) { mantissa /= Math.Pow(10, placeholderCount); exponent += placeholderCount; mantissa = Math.Round(mantissa, decimals, MidpointRounding.AwayFromZero); }
        else if (mantissa > 0 && mantissa < 1) { mantissa *= Math.Pow(10, placeholderCount); exponent -= placeholderCount; mantissa = Math.Round(mantissa, decimals, MidpointRounding.AwayFromZero); }

        var mantissaFixed = mantissa.ToString("F" + decimals, CultureInfo.InvariantCulture);
        var mantissaDotIndex = mantissaFixed.IndexOf('.');
        var mantissaIntegerDigits = mantissaDotIndex >= 0 ? mantissaFixed[..mantissaDotIndex] : mantissaFixed;
        var mantissaDecimalDigitsRaw = mantissaDotIndex >= 0 ? mantissaFixed[(mantissaDotIndex + 1)..] : string.Empty;
        var mantissaString = RenderIntegerPart(mantissaIntegerDigits, t.IntPlaceholders) + RenderDecimalPart(mantissaDecimalDigitsRaw, t.DecimalPlaceholders);

        var expDigits = Math.Max(t.ExponentDigitCount, 1);
        var sign = exponent < 0 ? "-" : t.ExponentSign == '+' ? "+" : string.Empty;
        var expString = Math.Abs(exponent).ToString(CultureInfo.InvariantCulture).PadLeft(expDigits, '0');

        var sb = new StringBuilder();
        foreach (var (text, _) in t.PrefixLiterals) sb.Append(text);
        sb.Append(mantissaString).Append(t.ExponentUpper ? 'E' : 'e').Append(sign).Append(expString);
        for (var index = 0; index < t.SuffixLiterals.Count; index++)
        {
            var (text, isQuoted) = t.SuffixLiterals[index];
            if (index == 0 && isQuoted && NeedsCompatibilitySpace(text)) sb.Append(' ');
            sb.Append(text);
        }
        return sb.ToString();
    }

    /// <summary>Mathematical floor division (rounds the quotient towards negative infinity, unlike C#'s
    /// truncating '/'), used to snap an exponent down to the nearest multiple of the engineering
    /// placeholder count even when the true exponent is negative.</summary>
    private static int FloorDiv(int dividend, int divisor)
    {
        var quotient = dividend / divisor;
        var remainder = dividend % divisor;
        if (remainder != 0 && (remainder < 0) != (divisor < 0)) quotient--;
        return quotient;
    }

    /// <summary>Left-pads with '0' placeholders only (never '#'/'?'), passing digits through unchanged
    /// once they meet or exceed the placeholder count so real digits are never truncated. A value that
    /// rounds to exactly zero with no '0' placeholder anywhere renders as empty, matching Excel's "#"
    /// applied to 0.</summary>
    private static string RenderIntegerPart(string digits, List<char> placeholders)
    {
        if (digits == "0" && !placeholders.Contains('0')) return string.Empty;
        if (digits.Length >= placeholders.Count) return digits;

        var padCount = placeholders.Count - digits.Length;
        var sb = new StringBuilder();
        for (var i = 0; i < padCount; i++)
        {
            var placeholder = placeholders[i];
            if (placeholder == '0') sb.Append('0');
            else if (placeholder == '?') sb.Append(' ');
        }
        sb.Append(digits);
        return sb.ToString();
    }

    /// <summary>Renders decimal digits against their placeholders: '0' always shows its digit; a trailing
    /// run of '#'/'?' placeholders whose digit is '0' is suppressed (omitted for '#', blanked for '?')
    /// until a non-zero digit or a '0' placeholder is reached.</summary>
    private static string RenderDecimalPart(string digitsRaw, List<char> placeholders)
    {
        if (placeholders.Count == 0) return string.Empty;
        var digits = digitsRaw.Length >= placeholders.Count ? digitsRaw[..placeholders.Count] : digitsRaw.PadRight(placeholders.Count, '0');
        var output = new char?[placeholders.Count];
        var trailing = true;
        for (var i = placeholders.Count - 1; i >= 0; i--)
        {
            var placeholder = placeholders[i];
            var digit = digits[i];
            if (trailing && placeholder != '0' && digit == '0')
            {
                output[i] = placeholder == '?' ? (char?)' ' : null;
                continue;
            }
            trailing = false;
            output[i] = digit;
        }
        var sb = new StringBuilder();
        foreach (var ch in output) if (ch.HasValue) sb.Append(ch.Value);
        return sb.Length == 0 ? string.Empty : "." + sb;
    }

    private static string ApplyGrouping(string digits)
    {
        if (digits.Length <= 3) return digits;
        var sb = new StringBuilder();
        var offset = digits.Length % 3;
        if (offset > 0) sb.Append(digits, 0, offset);
        for (var i = offset; i < digits.Length; i += 3)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(digits, i, 3);
        }
        return sb.ToString();
    }

    private static NumberFormatAnalysis AnalyzeNumberFormat(string format, double number)
    {
        var active = (number < 0 && format.Split(';').Length > 1 ? format.Split(';')[1] : format.Split(';')[0]);
        var hasDate = false;
        var hasTime = false;
        var hasMinuteToken = false;
        var isElapsed = false;
        var hasSeconds = false;
        var hasAmPm = false;
        var hasPercent = false;
        var hasGrouping = false;
        var decimalPlaces = 0;
        var inQuote = false;
        var hasDecimal = false;
        var suffix = new StringBuilder();
        for (var index = 0; index < active.Length; index++)
        {
            var character = active[index];
            if (character == '"') { inQuote = !inQuote; continue; }
            if (inQuote) { suffix.Append(character); continue; }
            if (character == '\\' && index + 1 < active.Length)
            {
                // A backslash before comma is common in OOXML custom formats;
                // retain comma grouping semantics while ignoring other escapes.
                if (active[index + 1] == ',') { hasGrouping = true; index++; continue; }
                if (active[index + 1] == '"')
                {
                    var closingSlash = active.IndexOf('\\', index + 2);
                    if (closingSlash >= 0 && closingSlash + 1 < active.Length && active[closingSlash + 1] == '"')
                    {
                        suffix.Append(active[(index + 2)..closingSlash]);
                        index = closingSlash + 1;
                        continue;
                    }
                }
                suffix.Append(active[++index]); continue;
            }
            if (character is '_' or '*') { if (index + 1 < active.Length) index++; continue; }
            if (character == '[')
            {
                var end = active.IndexOf(']', index + 1);
                if (end >= 0)
                {
                    var bracket = active[(index + 1)..end];
                    if (bracket.Equals("h", StringComparison.OrdinalIgnoreCase) || bracket.Equals("m", StringComparison.OrdinalIgnoreCase) || bracket.Equals("s", StringComparison.OrdinalIgnoreCase))
                    {
                        isElapsed = true; hasTime = true; hasSeconds |= bracket.Equals("s", StringComparison.OrdinalIgnoreCase);
                    }
                    index = end;
                    continue;
                }
            }
            if (character == '%') { hasPercent = true; continue; }
            if (character is 'y' or 'Y' or 'd' or 'D') hasDate = true;
            else if (character is 'h' or 'H') hasTime = true;
            else if (character is 's' or 'S') { hasTime = true; hasSeconds = true; }
            else if (character is 'm' or 'M')
            {
                // Excel uses m for either month or minute. Resolve it after scanning
                // unquoted tokens so quoted/escaped literals cannot affect the result.
                hasMinuteToken = true;
            }
            else if (character == ',') hasGrouping = true;
            else if (character == '.' && !hasDecimal)
            {
                hasDecimal = true;
                decimalPlaces = active[(index + 1)..].TakeWhile(c => c is '0' or '#' or '?').Count();
            }
            if ((character is 'A' or 'a') && active[index..].StartsWith("AM/PM", StringComparison.OrdinalIgnoreCase))
            {
                hasAmPm = true; hasTime = true;
            }
        }
        if (hasMinuteToken) hasDate |= !hasTime;
        var literalSuffix = suffix.ToString().Trim();
        return new(hasDate, hasTime, isElapsed, hasSeconds, hasAmPm, hasPercent, hasGrouping, decimalPlaces, literalSuffix.Length == 0 ? null : literalSuffix);
    }

    private static string FormatClock(double number, NumberFormatAnalysis analysis)
    {
        var fraction = number - Math.Floor(number);
        if (fraction < 0) fraction += 1;
        var totalSeconds = (int)Math.Floor(fraction * 24 * 60 * 60 + 0.5);
        if (totalSeconds >= 24 * 60 * 60) totalSeconds = 0;
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds / 60 % 60;
        var seconds = totalSeconds % 60;
        if (analysis.HasAmPm)
        {
            var date = new DateTime(2000, 1, 1).AddHours(hours).AddMinutes(minutes).AddSeconds(seconds);
            return date.ToString(analysis.HasSeconds ? "h:mm:ss tt" : "h:mm tt", CultureInfo.InvariantCulture);
        }
        return hours.ToString(CultureInfo.InvariantCulture) + ":" + minutes.ToString("D2", CultureInfo.InvariantCulture) + (analysis.HasSeconds ? ":" + seconds.ToString("D2", CultureInfo.InvariantCulture) : string.Empty);
    }

    private static string FormatElapsed(double number, NumberFormatAnalysis analysis)
    {
        var totalSeconds = (long)Math.Floor(Math.Abs(number) * 24 * 60 * 60 + 0.5);
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds / 60 % 60;
        var seconds = totalSeconds % 60;
        var sign = number < 0 ? "-" : string.Empty;
        return sign + hours.ToString(CultureInfo.InvariantCulture) + ":" + minutes.ToString("D2", CultureInfo.InvariantCulture) + (analysis.HasSeconds ? ":" + seconds.ToString("D2", CultureInfo.InvariantCulture) : string.Empty);
    }

    private static string? ExtractFormatSuffix(string format)
    {
        var quoted = Regex.Matches(format, "\\\"(?<value>[^\\\"]+)\\\"")
            .Select(match => match.Groups["value"].Value.Trim('\\', ' '))
            .LastOrDefault(value => value.Length > 0);
        if (!string.IsNullOrWhiteSpace(quoted)) return quoted;
        return Regex.Matches(format, @"\\(?<value>[^0#?,.])")
            .Select(match => match.Groups["value"].Value)
            .LastOrDefault();
    }

    private static bool LooksLikeDateFormat(string format)
    {
        var clean = Regex.Replace(format, "\\\"[^\\\"]*\\\"|\\\\[^\\\\]*\\\\|\\[[^\\]]*\\]", string.Empty);
        return clean.Contains('y') || clean.Contains('Y') || clean.Contains('d') || clean.Contains('D') || clean.Contains('m') || clean.Contains('M');
    }

    private static int DecimalPlaces(string format)
    {
        var dot = format.IndexOf('.');
        if (dot < 0) return 0;
        return format[(dot + 1)..].TakeWhile(character => character is '0' or '#').Count();
    }

    private sealed record UsedRange(string? Range, int MinRow, int MaxRow, int MinColumn, int MaxColumn);

    private static UsedRange CalculateUsedRange(IReadOnlyList<XlsxCellRecord> cells, IReadOnlyList<string> mergedRanges, string? declaredRange)
    {
        var occupied = cells.Where(cell => cell.RowIndex > 0 && cell.ColumnIndex > 0).ToArray();
        var bounds = mergedRanges.Append(declaredRange).Where(x => !string.IsNullOrWhiteSpace(x)).Select(ParseRange).Where(x => x is not null).Select(x => x!.Value).ToArray();
        if (occupied.Length == 0 && bounds.Length == 0) return new(null, 0, 0, 0, 0);
        var minRow = occupied.Length == 0 ? int.MaxValue : occupied.Min(cell => cell.RowIndex);
        var maxRow = occupied.Length == 0 ? 0 : occupied.Max(cell => cell.RowIndex);
        var minColumn = occupied.Length == 0 ? int.MaxValue : occupied.Min(cell => cell.ColumnIndex);
        var maxColumn = occupied.Length == 0 ? 0 : occupied.Max(cell => cell.ColumnIndex);
        foreach (var bound in bounds)
        {
            minRow = Math.Min(minRow, bound.MinRow); maxRow = Math.Max(maxRow, bound.MaxRow);
            minColumn = Math.Min(minColumn, bound.MinColumn); maxColumn = Math.Max(maxColumn, bound.MaxColumn);
        }
        return new($"{ColumnName(minColumn)}{minRow}:{ColumnName(maxColumn)}{maxRow}", minRow, maxRow, minColumn, maxColumn);
    }

    private static IReadOnlyList<string> ReadMergedRanges(byte[] bytes)
    {
        var result = new List<string>();
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "mergeCell")
            {
                var range = reader.GetAttribute("ref");
                if (!string.IsNullOrWhiteSpace(range)) result.Add(range);
            }
        return result;
    }

    private static IReadOnlyList<XlsxChartRecord> ReadCharts(Dictionary<string, byte[]> package, string worksheetPartUri, string sheetName, IReadOnlyList<XlsxCellRecord> cells, IReadOnlyDictionary<string, (bool Hidden, IReadOnlySet<int> Rows, IReadOnlySet<int> Columns)> sheetVisibility, List<string> warnings)
    {
        var directory = worksheetPartUri[..worksheetPartUri.LastIndexOf("/", StringComparison.Ordinal)];
        var relationships = ReadRelationships(package, directory + "/_rels/" + Path.GetFileName(worksheetPartUri) + ".rels");
        var result = new List<XlsxChartRecord>();
        var workbookHasPotentiallyHiddenSources = sheetVisibility.Values.Any(visibility =>
            visibility.Hidden || visibility.Rows.Count > 0 || visibility.Columns.Count > 0);
        foreach (var drawingPart in relationships.Values
                     .Where(path => path.StartsWith("xl/drawings/", StringComparison.Ordinal) && path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.Ordinal))
        {
            if (!package.TryGetValue(drawingPart, out var bytes)) continue;
            var drawingRelationships = ReadRelationships(package, drawingPart[..drawingPart.LastIndexOf("/", StringComparison.Ordinal)] + "/_rels/" + Path.GetFileName(drawingPart) + ".rels");
            var document = new XmlDocument { PreserveWhitespace = false };
            using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
            document.Load(reader);
            foreach (var anchor in document.DocumentElement?.ChildNodes.OfType<XmlElement>()
                         .Where(element => element.LocalName is "twoCellAnchor" or "oneCellAnchor" or "absoluteAnchor") ?? [])
            {
                var from = DirectChild(anchor, "from");
                var column = from is null ? (int?)null : ChildIntNullable(from, "col") + 1;
                var row = from is null ? (int?)null : ChildIntNullable(from, "row") + 1;
                if (column <= 0) column = null;
                if (row <= 0) row = null;
                var properties = anchor.SelectNodes(".//*[local-name()='cNvPr']")?.OfType<XmlElement>().FirstOrDefault();
                var chartElements = anchor.SelectNodes(".//*[local-name()='chart']")?.OfType<XmlElement>() ?? [];
                foreach (var chartElement in chartElements)
                {
                    var relationshipId = chartElement.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    if (string.IsNullOrWhiteSpace(relationshipId)) relationshipId = chartElement.GetAttribute("r:id");
                    if (string.IsNullOrWhiteSpace(relationshipId) || !drawingRelationships.TryGetValue(relationshipId, out var chartPart) || !package.TryGetValue(chartPart, out var chartBytes)) continue;
                    var data = OpenXmlChartReader.Read(chartBytes, reference => ResolveChartReference(reference, sheetName, cells));
                    var (references, referencesParsedFromXml) = ReadChartReferences(chartBytes, sheetName);
                    var referencesParsed = referencesParsedFromXml && references.All(reference => sheetVisibility.ContainsKey(reference.SheetName));
                    var isHidden = referencesParsed
                        ? references.Count > 0 && references.Any(reference =>
                            !sheetVisibility.TryGetValue(reference.SheetName, out var visibility) || visibility.Hidden ||
                            visibility.Rows.Any(hiddenRow => hiddenRow >= reference.MinRow && hiddenRow <= reference.MaxRow) ||
                            visibility.Columns.Any(hiddenColumn => hiddenColumn >= reference.MinColumn && hiddenColumn <= reference.MaxColumn))
                        : workbookHasPotentiallyHiddenSources;
                    if (!referencesParsed)
                        warnings.Add(isHidden
                            ? $"{sheetName}: native chart references could not be fully parsed and the workbook contains potentially hidden sources; the chart was kept hidden."
                            : $"{sheetName}: native chart references could not be fully parsed; cached chart data was retained with an explicit warning.");
                    var id = properties?.GetAttribute("id");
                    if (string.IsNullOrWhiteSpace(id)) id = $"chart-{result.Count + 1}";
                    var name = properties?.GetAttribute("name");
                    if (string.IsNullOrWhiteSpace(name)) name = data?.Title ?? id;
                    result.Add(new XlsxChartRecord(id, name!, data?.Title, data?.Type, data?.Series ?? [], relationshipId, chartPart, column, row, drawingPart, references, referencesParsed, isHidden));
                }
            }
        }
        return result;
    }

    private static (IReadOnlyList<XlsxChartReference> References, bool Parsed) ReadChartReferences(byte[] bytes, string hostSheet)
    {
        var document = new XmlDocument { PreserveWhitespace = false };
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        document.Load(reader);
        var formulas = document.SelectNodes("//*[local-name()='f']")?.OfType<XmlElement>()
            .Select(element => element.InnerText.Trim()).Where(formula => formula.Length > 0).ToArray() ?? [];
        if (formulas.Length == 0) return ([], false);
        var references = new List<XlsxChartReference>();
        foreach (var formula in formulas)
        {
            var match = ChartCellReference.Match(formula.TrimStart('='));
            if (!match.Success) return (references, false);
            var sheet = match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value.Replace("''", "'", StringComparison.Ordinal)
                : match.Groups["plain"].Success ? match.Groups["plain"].Value : hostSheet;
            var start = ParseCellReference(match.Groups["startColumn"].Value + match.Groups["startRow"].Value);
            var end = match.Groups["endColumn"].Success
                ? ParseCellReference(match.Groups["endColumn"].Value + match.Groups["endRow"].Value)
                : start;
            if (start.Row <= 0 || start.Column <= 0 || end.Row <= 0 || end.Column <= 0) return (references, false);
            references.Add(new(sheet, Math.Min(start.Row, end.Row), Math.Max(start.Row, end.Row), Math.Min(start.Column, end.Column), Math.Max(start.Column, end.Column)));
        }
        return (references, true);
    }

    private static IReadOnlyList<string> ResolveChartReference(string formula, string sheetName, IReadOnlyList<XlsxCellRecord> cells)
    {
        var normalized = formula.Trim().TrimStart('=');
        if (normalized.Contains('[', StringComparison.Ordinal) || normalized.Contains(']', StringComparison.Ordinal)) return [];
        var match = ChartCellReference.Match(normalized);
        if (!match.Success) return [];
        var referencedSheet = match.Groups["quoted"].Success
            ? match.Groups["quoted"].Value.Replace("''", "'", StringComparison.Ordinal)
            : match.Groups["plain"].Success ? match.Groups["plain"].Value : sheetName;
        if (!StringComparer.Ordinal.Equals(referencedSheet, sheetName)) return [];

        var start = ParseCellReference(match.Groups["startColumn"].Value + match.Groups["startRow"].Value);
        var end = match.Groups["endColumn"].Success
            ? ParseCellReference(match.Groups["endColumn"].Value + match.Groups["endRow"].Value)
            : start;
        if (start.Row <= 0 || start.Column <= 0 || end.Row <= 0 || end.Column <= 0) return [];
        var minRow = Math.Min(start.Row, end.Row);
        var maxRow = Math.Max(start.Row, end.Row);
        var minColumn = Math.Min(start.Column, end.Column);
        var maxColumn = Math.Max(start.Column, end.Column);
        var rowSpan = (long)maxRow - minRow + 1;
        var columnSpan = (long)maxColumn - minColumn + 1;
        if (rowSpan <= 0 || columnSpan <= 0 || rowSpan > MaxChartResolutionCells / columnSpan) return [];
        var byCoordinate = cells.ToDictionary(cell => (cell.RowIndex, cell.ColumnIndex));
        var values = new List<string>(checked((int)(rowSpan * columnSpan)));
        for (var row = minRow; row <= maxRow; row++)
            for (var column = minColumn; column <= maxColumn; column++)
                values.Add(byCoordinate.TryGetValue((row, column), out var cell)
                    ? cell.DisplayValue ?? cell.Value ?? string.Empty
                    : string.Empty);
        return values;
    }

    private sealed record PictureRelationship(string Target, bool IsExternal);

    private static IReadOnlyList<XlsxPictureRecord> ReadPictures(
        Dictionary<string, byte[]> package,
        string worksheetPartUri,
        string sheetName,
        List<string> warnings)
    {
        var directory = worksheetPartUri[..worksheetPartUri.LastIndexOf("/", StringComparison.Ordinal)];
        var fileName = Path.GetFileName(worksheetPartUri);
        var relationships = ReadRelationships(package, directory + "/_rels/" + fileName + ".rels");
        var result = new List<XlsxPictureRecord>();
        foreach (var drawingPart in relationships.Values
                     .Where(path => path.StartsWith("xl/drawings/", StringComparison.Ordinal) && path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.Ordinal))
        {
            if (!package.TryGetValue(drawingPart, out var bytes)) continue;
            var drawingDirectory = drawingPart[..drawingPart.LastIndexOf("/", StringComparison.Ordinal)];
            var drawingRelationships = ReadPictureRelationships(package, drawingDirectory + "/_rels/" + Path.GetFileName(drawingPart) + ".rels");
            var document = new XmlDocument { PreserveWhitespace = false };
            using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
            document.Load(reader);
            var root = document.DocumentElement;
            if (root is null) continue;

            foreach (var anchor in root.ChildNodes.OfType<XmlElement>()
                         .Where(element => element.LocalName is "twoCellAnchor" or "oneCellAnchor" or "absoluteAnchor"))
            {
                var from = DirectChild(anchor, "from");
                var column = from is null ? (int?)null : ChildIntNullable(from, "col") + 1;
                var row = from is null ? (int?)null : ChildIntNullable(from, "row") + 1;
                if (column is <= 0) column = null;
                if (row is <= 0) row = null;
                var to = DirectChild(anchor, "to");
                var toColumn = to is null ? (int?)null : ChildIntNullable(to, "col") + 1;
                var toRow = to is null ? (int?)null : ChildIntNullable(to, "row") + 1;
                if (toColumn is <= 0) toColumn = null;
                if (toRow is <= 0) toRow = null;
                var anchorExtent = DirectChild(anchor, "ext");

                var pictures = anchor.SelectNodes(".//*[local-name()='pic']")?.OfType<XmlElement>() ?? [];
                foreach (var picture in pictures)
                {
                    var properties = Descendant(picture, "cNvPr");
                    var id = properties?.GetAttribute("id");
                    var name = properties?.GetAttribute("name");
                    if (string.IsNullOrWhiteSpace(id)) id = $"picture-{result.Count + 1}";
                    if (string.IsNullOrWhiteSpace(name)) name = id;
                    var description = properties?.GetAttribute("descr");
                    if (string.IsNullOrWhiteSpace(description)) description = properties?.GetAttribute("title");
                    if (string.IsNullOrWhiteSpace(description)) description = null;

                    var blip = Descendant(picture, "blip");
                    var embed = blip?.GetAttribute("embed", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    if (string.IsNullOrWhiteSpace(embed)) embed = blip?.GetAttribute("r:embed");
                    var link = blip?.GetAttribute("link", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    if (string.IsNullOrWhiteSpace(link)) link = blip?.GetAttribute("r:link");
                    if (string.IsNullOrWhiteSpace(embed))
                    {
                        if (!string.IsNullOrWhiteSpace(link))
                            warnings.Add($"{sheetName}: linked picture '{name}' was skipped (external image).");
                        continue;
                    }
                    if (!drawingRelationships.TryGetValue(embed, out var relationship))
                        continue;
                    if (relationship.IsExternal)
                    {
                        warnings.Add($"{sheetName}: linked picture '{name}' was skipped (external image).");
                        continue;
                    }

                    var pictureExtent = Descendant(picture, "ext");
                    var width = AttributeLong(pictureExtent ?? anchorExtent, "cx");
                    var height = AttributeLong(pictureExtent ?? anchorExtent, "cy");
                    result.Add(new XlsxPictureRecord(
                        id,
                        name!,
                        description,
                        embed,
                        "/" + NormalizePartPath(relationship.Target),
                        column,
                        row,
                        toColumn,
                        toRow,
                        width,
                        height,
                        drawingPart));
                }
            }
        }
        return result;
    }

    private static Dictionary<string, PictureRelationship> ReadPictureRelationships(
        Dictionary<string, byte[]> package,
        string relsPath)
    {
        var result = new Dictionary<string, PictureRelationship>(StringComparer.Ordinal);
        if (!package.TryGetValue(relsPath, out var bytes)) return result;
        var basePath = relsPath[..relsPath.LastIndexOf("/_rels/", StringComparison.Ordinal)];
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship") continue;
            var id = reader.GetAttribute("Id");
            var target = reader.GetAttribute("Target");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(target)) continue;
            var external = StringComparer.OrdinalIgnoreCase.Equals(reader.GetAttribute("TargetMode"), "External");
            var resolvedTarget = external || target.StartsWith("/", StringComparison.Ordinal)
                ? target
                : basePath + "/" + target;
            result[id] = new PictureRelationship(resolvedTarget, external);
        }
        return result;
    }

    private static List<XlsxCellRecord> ApplyMergedRanges(
        List<XlsxCellRecord> cells,
        IReadOnlyList<string> mergedRanges)
    {
        var byCoordinate = cells.ToDictionary(cell => (cell.RowIndex, cell.ColumnIndex));
        foreach (var reference in mergedRanges)
        {
            var range = ParseRange(reference);
            if (range is null || !byCoordinate.TryGetValue((range.Value.MinRow, range.Value.MinColumn), out var cell)) continue;
            var merged = cell with { MergedToRow = range.Value.MaxRow, MergedToColumn = range.Value.MaxColumn };
            cells[cells.IndexOf(cell)] = merged;
            byCoordinate[(merged.RowIndex, merged.ColumnIndex)] = merged;
        }
        return cells;
    }

    private static IReadOnlyList<XlsxDrawingShapeRecord> ReadDrawingShapes(
        Dictionary<string, byte[]> package,
        string worksheetPartUri,
        XlsxWorksheetMetrics metrics)
    {
        var directory = worksheetPartUri[..worksheetPartUri.LastIndexOf("/", StringComparison.Ordinal)];
        var fileName = Path.GetFileName(worksheetPartUri);
        var relationships = ReadRelationships(package, directory + "/_rels/" + fileName + ".rels");
        var result = new List<XlsxDrawingShapeRecord>();
        foreach (var drawingPart in relationships.Values
                     .Where(path => path.StartsWith("xl/drawings/", StringComparison.Ordinal) && path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.Ordinal))
        {
            if (!package.TryGetValue(drawingPart, out var bytes)) continue;
            var document = new XmlDocument { PreserveWhitespace = false };
            using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
            document.Load(reader);
            var root = document.DocumentElement;
            if (root is null) continue;
            var anchorIndex = 0;
            foreach (var anchor in root.ChildNodes.OfType<XmlElement>())
            {
                anchorIndex++;
                var from = DirectChild(anchor, "from");
                var anchorExtent = DirectChild(anchor, "ext");
                var anchorTo = DirectChild(anchor, "to");
                var absolutePosition = DirectChild(anchor, "pos");
                var anchorColumn = from is null ? 1 : ChildInt(from, "col") + 1;
                var anchorRow = from is null ? 1 : ChildInt(from, "row") + 1;
                if (anchor.LocalName is not "absoluteAnchor" && (anchorColumn <= 0 || anchorRow <= 0)) continue;
                var fromColumnOffset = from is null ? 0 : ChildLong(from, "colOff");
                var fromRowOffset = from is null ? 0 : ChildLong(from, "rowOff");
                var toColumnOffset = anchorTo is null ? 0 : ChildLong(anchorTo, "colOff");
                var toRowOffset = anchorTo is null ? 0 : ChildLong(anchorTo, "rowOff");
                var anchorX = anchor.LocalName == "absoluteAnchor" ? AttributeLong(absolutePosition, "x") : metrics.ColumnStartEmu(anchorColumn) + fromColumnOffset;
                var anchorY = anchor.LocalName == "absoluteAnchor" ? AttributeLong(absolutePosition, "y") : metrics.RowStartEmu(anchorRow) + fromRowOffset;
                var anchorWidth = AttributeLong(anchorExtent, "cx");
                var anchorHeight = AttributeLong(anchorExtent, "cy");
                var anchorRight = anchor.LocalName == "twoCellAnchor" && anchorTo is not null
                    ? metrics.ColumnStartEmu(ChildInt(anchorTo, "col") + 1) + toColumnOffset : anchorX + anchorWidth;
                var anchorBottom = anchor.LocalName == "twoCellAnchor" && anchorTo is not null
                    ? metrics.RowStartEmu(ChildInt(anchorTo, "row") + 1) + toRowOffset : anchorY + anchorHeight;
                var drawingElements = anchor.SelectNodes(".//*[local-name()='sp' or local-name()='cxnSp']")
                    ?.OfType<XmlElement>().ToArray() ?? [];
                foreach (var shape in drawingElements)
                {
                    var isTopLevel = ReferenceEquals(shape.ParentNode, anchor);
                    var isConnector = shape.LocalName == "cxnSp";
                    var properties = Descendant(shape, "cNvPr");
                    var transform = Descendant(shape, "xfrm");
                    var shapeOffset = transform is null ? null : DirectChild(transform, "off");
                    var shapeExtent = transform is null ? null : DirectChild(transform, "ext");
                    var geometry = Descendant(shape, "prstGeom")?.GetAttribute("prst") ?? (isConnector ? "line" : "unknown");
                    var paragraphs = shape.SelectNodes(".//*[local-name()='p']")?.OfType<XmlElement>()
                        .Select(paragraph => string.Concat(paragraph.SelectNodes(".//*[local-name()='t']")?.OfType<XmlElement>()
                            .Select(element => element.InnerText) ?? []))
                        .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? [];
                    var text = string.Join("\n", paragraphs);
                    var group = shape.ParentNode as XmlElement;
                    while (group is not null && group.LocalName != "grpSp") group = group.ParentNode as XmlElement;
                    var groupId = group is null ? null : Descendant(group, "cNvPr")?.GetAttribute("id");
                    var startConnection = Descendant(shape, "stCxn")?.GetAttribute("id");
                    var endConnection = Descendant(shape, "endCxn")?.GetAttribute("id");
                    var groupTransform = group is null ? null : Descendant(group, "xfrm");
                    var groupOffset = groupTransform is null ? null : DirectChild(groupTransform, "off");
                    var groupExtent = groupTransform is null ? null : DirectChild(groupTransform, "ext");
                    var groupChildOffset = groupTransform is null ? null : DirectChild(groupTransform, "chOff");
                    var groupChildExtent = groupTransform is null ? null : DirectChild(groupTransform, "chExt");
                    var groupScaleX = Math.Abs(AttributeLong(groupChildExtent, "cx")) > 0 && Math.Abs(AttributeLong(groupExtent, "cx")) > 0
                        ? AttributeLong(groupExtent, "cx") / (double)AttributeLong(groupChildExtent, "cx") : 1d;
                    var groupScaleY = Math.Abs(AttributeLong(groupChildExtent, "cy")) > 0 && Math.Abs(AttributeLong(groupExtent, "cy")) > 0
                        ? AttributeLong(groupExtent, "cy") / (double)AttributeLong(groupChildExtent, "cy") : 1d;
                    var localX = AttributeLong(shapeOffset, "x");
                    var localY = AttributeLong(shapeOffset, "y");
                    var shapeX = isTopLevel ? anchorX : anchorX + AttributeLong(groupOffset, "x") +
                        (localX - AttributeLong(groupChildOffset, "x")) * groupScaleX;
                    var shapeY = isTopLevel ? anchorY : anchorY + AttributeLong(groupOffset, "y") +
                        (localY - AttributeLong(groupChildOffset, "y")) * groupScaleY;
                    var topLevelWidth = anchorWidth > 0 ? anchorWidth : AttributeLong(shapeExtent, "cx");
                    var topLevelHeight = anchorHeight > 0 ? anchorHeight : AttributeLong(shapeExtent, "cy");
                    var shapeWidth = isTopLevel ? Math.Max(anchorRight - anchorX, topLevelWidth) : (long)Math.Round(AttributeLong(shapeExtent, "cx") * groupScaleX);
                    var shapeHeight = isTopLevel ? Math.Max(anchorBottom - anchorY, topLevelHeight) : (long)Math.Round(AttributeLong(shapeExtent, "cy") * groupScaleY);
                    result.Add(new XlsxDrawingShapeRecord(
                        properties?.GetAttribute("id") ?? $"shape-{result.Count + 1}",
                        properties?.GetAttribute("name") ?? $"shape-{result.Count + 1}",
                        geometry,
                        anchorColumn,
                        anchorRow,
                        fromColumnOffset + (isTopLevel ? 0 : AttributeLong(shapeOffset, "x")),
                        fromRowOffset + (isTopLevel ? 0 : AttributeLong(shapeOffset, "y")),
                        shapeWidth,
                        shapeHeight,
                        isTopLevel && anchorTo is not null ? ChildInt(anchorTo, "col") + 1 : null,
                        isTopLevel && anchorTo is not null ? ChildInt(anchorTo, "row") + 1 : null,
                        StringComparer.Ordinal.Equals(transform?.GetAttribute("flipH"), "1"),
                        StringComparer.Ordinal.Equals(transform?.GetAttribute("flipV"), "1"),
                        string.IsNullOrWhiteSpace(text) ? null : text,
                        Descendant(shape, "prstDash")?.GetAttribute("val"),
                        isConnector,
                        string.IsNullOrWhiteSpace(startConnection) ? null : startConnection,
                        string.IsNullOrWhiteSpace(endConnection) ? null : endConnection,
                        string.IsNullOrWhiteSpace(groupId) ? null : groupId,
                        anchor.LocalName,
                        checked((long)Math.Round(shapeX)),
                        checked((long)Math.Round(shapeY)),
                        shapeWidth,
                        shapeHeight,
                        toColumnOffset,
                        toRowOffset,
                        drawingPart,
                        anchorIndex));
                }
            }
        }
        return result;
    }

    private static XmlElement? DirectChild(XmlElement parent, string localName) =>
        parent.ChildNodes.OfType<XmlElement>().FirstOrDefault(element => element.LocalName == localName);

    private static XmlElement? Descendant(XmlElement parent, string localName) =>
        parent.SelectNodes(".//*")?.OfType<XmlElement>().FirstOrDefault(element => element.LocalName == localName);

    private static int ChildInt(XmlElement parent, string localName) =>
        int.TryParse(DirectChild(parent, localName)?.InnerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static int? ChildIntNullable(XmlElement parent, string localName) =>
        int.TryParse(DirectChild(parent, localName)?.InnerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long ChildLong(XmlElement parent, string localName) =>
        long.TryParse(DirectChild(parent, localName)?.InnerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static long AttributeLong(XmlElement? element, string attribute) =>
        long.TryParse(element?.GetAttribute(attribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static string? ReadDeclaredDimension(byte[] bytes)
    {
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "dimension")
                return reader.GetAttribute("ref");
        return null;
    }

    private static XlsxWorksheetMetrics ReadWorksheetMetrics(byte[] bytes)
    {
        var defaultColumnWidth = 8.43d;
        var defaultRowHeight = 15d;
        var columns = new Dictionary<int, double>();
        var rows = new Dictionary<int, double>();
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName == "sheetFormatPr")
            {
                defaultColumnWidth = AttributeDouble(reader, "defaultColWidth", defaultColumnWidth);
                defaultRowHeight = AttributeDouble(reader, "defaultRowHeight", defaultRowHeight);
            }
            else if (reader.LocalName == "col")
            {
                var min = AttributeInt(reader, "min", 1);
                var max = Math.Min(MaxExcelColumns, AttributeInt(reader, "max", min));
                var width = AttributeDouble(reader, "width", defaultColumnWidth);
                if (min <= max && width > 0)
                    for (var column = Math.Max(1, min); column <= max; column++) columns[column] = width;
            }
            else if (reader.LocalName == "row")
            {
                var row = AttributeInt(reader, "r", 0);
                var height = AttributeDouble(reader, "ht", defaultRowHeight);
                if (row > 0 && height > 0) rows[row] = height;
            }
        }
        return new(defaultColumnWidth, defaultRowHeight, columns, rows);
    }

    private static int AttributeInt(XmlReader reader, string name, int fallback) =>
        int.TryParse(reader.GetAttribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static double AttributeDouble(XmlReader reader, string name, double fallback) =>
        double.TryParse(reader.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static (int Row, int Column) ParseCellReference(string reference)
    {
        var split = 0;
        while (split < reference.Length && char.IsLetter(reference[split])) split++;
        if (split is 0 or > 3 || split == reference.Length ||
            !int.TryParse(reference[split..], NumberStyles.None, CultureInfo.InvariantCulture, out var row) ||
            row is < 1 or > MaxExcelRows) return (0, 0);
        var column = 0;
        foreach (var character in reference[..split].ToUpperInvariant())
        {
            if (character is < 'A' or > 'Z') return (0, 0);
            column = checked(column * 26 + character - 'A' + 1);
            if (column > MaxExcelColumns) return (0, 0);
        }
        return (row, column);
    }

    private static (int MinRow, int MaxRow, int MinColumn, int MaxColumn)? ParseRange(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return null;
        var parts = range.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length == 1) parts = [parts[0], parts[0]];
        if (parts.Length != 2) return null;
        var start = ParseCellReference(parts[0]); var end = ParseCellReference(parts[1]);
        if (start.Row <= 0 || start.Column <= 0 || end.Row <= 0 || end.Column <= 0) return null;
        return (Math.Min(start.Row, end.Row), Math.Max(start.Row, end.Row), Math.Min(start.Column, end.Column), Math.Max(start.Column, end.Column));
    }

    private static string ColumnName(int column)
    {
        var result = new StringBuilder();
        while (column > 0) { column--; result.Insert(0, (char)('A' + (column % 26))); column /= 26; }
        return result.ToString();
    }

    private static byte[] PatchWorksheet(byte[] bytes, IEnumerable<XlsxCellEdit> edits)
    {
        var document = new XmlDocument { PreserveWhitespace = true }; using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml); document.Load(reader);
        var ns = new XmlNamespaceManager(document.NameTable); ns.AddNamespace("x", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        var data = document.SelectSingleNode("//x:sheetData", ns) ?? document.DocumentElement!.AppendChild(document.CreateElement("sheetData", ns.LookupNamespace("x")))!;
        foreach (var edit in edits)
        {
            var cell = data.SelectSingleNode($"x:row/x:c[@r='{edit.CellReference}']", ns) as XmlElement;
            if (cell is null)
            {
                var rowNumber = RowNumber(edit.CellReference);
                var row = data.SelectSingleNode($"x:row[@r='{rowNumber}']", ns) as XmlElement;
                if (row is null) { row = document.CreateElement("row", ns.LookupNamespace("x")); row.SetAttribute("r", rowNumber); data.AppendChild(row); }
                cell = document.CreateElement("c", ns.LookupNamespace("x")); cell.SetAttribute("r", edit.CellReference); row.AppendChild(cell);
            }
            var originalType = cell.GetAttribute("t");
            var f = cell.SelectSingleNode("x:f", ns); var v = cell.SelectSingleNode("x:v", ns);
            if (edit.Formula is not null) { if (f is null) { f = document.CreateElement("f", ns.LookupNamespace("x")); cell.AppendChild(f); } f.InnerText = edit.Formula.TrimStart('='); if (v is not null) cell.RemoveChild(v); cell.SetAttribute("t", "n"); }
            else if (edit.Value is not null)
            {
                if (f is not null) cell.RemoveChild(f);
                var inline = cell.SelectSingleNode("x:is", ns);
                var preserveNumeric = originalType is "" or "n" &&
                    double.TryParse(edit.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                if (preserveNumeric)
                {
                    if (inline is not null) cell.RemoveChild(inline);
                    cell.SetAttribute("t", "n");
                    var valueNode = v ?? cell.AppendChild(document.CreateElement("v", ns.LookupNamespace("x")))
                        ?? throw new InvalidDataException("Failed to create the numeric cell value node.");
                    valueNode.InnerText = edit.Value;
                }
                else
                {
                    cell.SetAttribute("t", "inlineStr");
                    if (v is not null) cell.RemoveChild(v);
                    var isNode = inline as XmlElement ?? (XmlElement)cell.AppendChild(document.CreateElement("is", ns.LookupNamespace("x")))!;
                    var t = isNode.SelectSingleNode("x:t", ns) as XmlElement ?? (XmlElement)isNode.AppendChild(document.CreateElement("t", ns.LookupNamespace("x")))!;
                    t.InnerText = edit.Value;
                }
            }
        }
        using var output = new MemoryStream(); using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false, Indent = false })) document.Save(writer); return output.ToArray();
    }

    private static byte[] MarkWorkbookForRecalculation(byte[] bytes)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        document.Load(reader);
        var ns = new XmlNamespaceManager(document.NameTable);
        ns.AddNamespace("x", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        var workbook = document.DocumentElement ?? throw new InvalidDataException("Workbook XML has no root element.");
        var calc = document.SelectSingleNode("/x:workbook/x:calcPr", ns) as XmlElement;
        if (calc is null)
        {
            calc = document.CreateElement("calcPr", ns.LookupNamespace("x"));
            workbook.AppendChild(calc);
        }
        calc.SetAttribute("calcMode", "auto");
        calc.SetAttribute("fullCalcOnLoad", "1");
        calc.SetAttribute("forceFullCalc", "1");
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false, Indent = false }))
            document.Save(writer);
        return output.ToArray();
    }
    private static string RowNumber(string reference)
    {
        var digits = new string(reference.SkipWhile(char.IsLetter).ToArray());
        return digits.Length == 0 ? "1" : digits;
    }
    private sealed class StringTupleComparer : IEqualityComparer<(string Sheet, string Ref)>
    { public static StringTupleComparer Instance { get; } = new(); public bool Equals((string Sheet, string Ref) x, (string Sheet, string Ref) y) => StringComparer.Ordinal.Equals(x.Sheet, y.Sheet) && StringComparer.Ordinal.Equals(x.Ref, y.Ref); public int GetHashCode((string Sheet, string Ref) x) => HashCode.Combine(StringComparer.Ordinal.GetHashCode(x.Sheet), StringComparer.Ordinal.GetHashCode(x.Ref)); }
}
