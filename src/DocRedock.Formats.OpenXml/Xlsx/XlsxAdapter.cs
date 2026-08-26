using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.OpenXml.Xlsx;

public enum XlsxFormulaSafety { Safe, Suspicious, Dangerous }
public sealed record XlsxFormulaDiagnostic(string CellReference, string Formula, XlsxFormulaSafety Safety, string? Reason = null);
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
    int? MergedToColumn = null)
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
    string? ParentGroupId = null);

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
    IReadOnlyList<XlsxPictureRecord>? Pictures = null)
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
    private static readonly XmlReaderSettings SafeXml = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = false,
        MaxCharactersFromEntities = 0
    };

    public XlsxExtractionResult Extract(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
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
        foreach (var sheet in workbook)
        {
            if (!package.TryGetValue(sheet.PartUri, out var xml)) continue;
            var mergedRanges = ReadMergedRanges(xml);
            var cells = ApplyMergedRanges(ReadWorksheet(xml, sheet.Name, shared, styles, formulaDiagnostics), mergedRanges);
            var used = CalculateUsedRange(cells, mergedRanges, ReadDeclaredDimension(xml));
            var drawingShapes = ReadDrawingShapes(package, sheet.PartUri);
            var pictures = ReadPictures(package, sheet.PartUri, sheet.Name, warnings);
            var worksheet = new XlsxWorksheetRecord(sheet.Name, sheet.PartUri, cells, used.Range, used.MinRow, used.MaxRow, used.MinColumn, used.MaxColumn, mergedRanges, drawingShapes, pictures);
            worksheets.Add(worksheet);
            var nodes = cells
                .Where(cell => !string.IsNullOrWhiteSpace(cell.Value) || !string.IsNullOrWhiteSpace(cell.Formula))
                .Select((cell, index) => ToNode(cell, sheet.PartUri, index))
                .ToList();
            if (XlsxMermaidProjection.TryCreate(worksheet, nodes.Count) is { } diagram) nodes.Add(diagram);
            else if (drawingShapes.Count > 0)
                warnings.Add($"{sheet.Name}: {drawingShapes.Count} DrawingML shape(s) were retained but not projected as a diagram.");
            foreach (var picture in pictures)
                nodes.Add(ToPictureNode(sheet, picture, nodes.Count));
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
            var sheet = partUri is null ? workbook.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.Name, editGroup.Key)) : workbook.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.PartUri, partUri));
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
        if (cell.DisplayStyle is not null)
        {
            extension["is_bold"] = JsonSerializer.SerializeToElement(cell.DisplayStyle.IsBold);
            extension["has_fill"] = JsonSerializer.SerializeToElement(cell.DisplayStyle.HasFill);
            extension["has_border"] = JsonSerializer.SerializeToElement(cell.DisplayStyle.HasBorder);
            extension["is_centered"] = JsonSerializer.SerializeToElement(cell.DisplayStyle.IsCentered);
            if (cell.DisplayStyle.FontSize is not null) extension["font_size"] = JsonSerializer.SerializeToElement(cell.DisplayStyle.FontSize.Value);
            if (cell.DisplayStyle.NumberFormat is not null) extension["number_format"] = JsonSerializer.SerializeToElement(cell.DisplayStyle.NumberFormat);
        }
        return new($"n_{Hash(cell.SheetName + "!" + cell.CellReference)[..16]}", NodeKind.Cell, null, order,
            ContentLayer.Body, new TextNodeContent(cell.Value ?? string.Empty),
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
    private sealed record SheetInfo(string Name, string PartUri);
    private static List<SheetInfo> ReadWorkbook(Dictionary<string, byte[]> package, Dictionary<string, string> relationships)
    {
        var result = new List<SheetInfo>();
        if (!package.TryGetValue("xl/workbook.xml", out var bytes)) throw new InvalidDataException("Workbook part missing.");
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        while (reader.Read()) if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet")
        {
            var name = reader.GetAttribute("name") ?? "Sheet" + (result.Count + 1);
            var rid = reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships") ?? reader.GetAttribute("r:id") ?? "";
            if (relationships.TryGetValue(rid, out var target)) result.Add(new(name, target));
        }
        return result;
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
    private static List<XlsxCellRecord> ReadWorksheet(byte[] bytes, string sheet, Dictionary<string, string> shared, IReadOnlyList<XlsxCellStyle> styles, List<XlsxFormulaDiagnostic> diagnostics)
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
            if (type == "s" && value is not null && shared.TryGetValue(value, out var sharedValue)) value = sharedValue;
            if (formula is not null) diagnostics.Add(ClassifyFormula(reference, formula));
            var (row, column) = ParseCellReference(reference);
            var displayStyle = int.TryParse(style, NumberStyles.Integer, CultureInfo.InvariantCulture, out var styleIndex) && styleIndex >= 0 && styleIndex < styles.Count
                ? styles[styleIndex]
                : null;
            var displayValue = FormatDisplayValue(value, type, displayStyle);
            result.Add(new(sheet, reference, value, formula, style, type == "s", row, column, type, displayValue, displayStyle));
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
            9 => "0%", 10 => "0.00%", 11 => "0.00E+00", 12 => "# ?/?", 13 => "# ??/??",
            14 => "yyyy-MM-dd", 15 => "d-MMM-yy", 16 => "d-MMM", 17 => "MMM-yy", 18 => "h:mm tt", 19 => "h:mm:ss tt",
            20 => "h:mm", 21 => "h:mm:ss", 22 => "yyyy-MM-dd h:mm", 37 or 38 or 39 or 40 or 43 or 44 => "#,##0",
            45 => "mm:ss", 46 => "[h]:mm:ss", 47 => "mmss.0", 48 => "@", 49 => "@",
            _ => null
        };

    private static string? FormatDisplayValue(string? raw, string? cellType, XlsxCellStyle? style)
    {
        if (raw is null || cellType is "s" or "str" or "inlineStr" or "b" || style?.NumberFormat is not { Length: > 0 } format) return raw;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return raw;
        if (LooksLikeDateFormat(format))
        {
            try { return DateTime.FromOADate(number).ToString(format.Contains('h') || format.Contains('H') ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd", CultureInfo.InvariantCulture); }
            catch (ArgumentException) { return raw; }
        }
        if (format.Contains('%'))
        {
            var decimals = DecimalPlaces(format);
            return (number * 100).ToString("F" + decimals, CultureInfo.InvariantCulture) + "%";
        }
        if (format.Contains(','))
        {
            var decimals = DecimalPlaces(format);
            var rendered = number.ToString("N" + decimals, CultureInfo.InvariantCulture);
            var suffix = ExtractFormatSuffix(format);
            return string.IsNullOrEmpty(suffix) ? rendered : rendered + " " + suffix;
        }
        return raw;
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
        string worksheetPartUri)
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
            foreach (var anchor in root.ChildNodes.OfType<XmlElement>())
            {
                var from = DirectChild(anchor, "from");
                if (from is null) continue;
                var anchorColumn = ChildInt(from, "col") + 1;
                var anchorRow = ChildInt(from, "row") + 1;
                if (anchorColumn <= 0 || anchorRow <= 0) continue;
                var anchorExtent = DirectChild(anchor, "ext");
                var anchorTo = DirectChild(anchor, "to");
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
                    result.Add(new XlsxDrawingShapeRecord(
                        properties?.GetAttribute("id") ?? $"shape-{result.Count + 1}",
                        properties?.GetAttribute("name") ?? $"shape-{result.Count + 1}",
                        geometry,
                        anchorColumn,
                        anchorRow,
                        ChildLong(from, "colOff") + (isTopLevel ? 0 : AttributeLong(shapeOffset, "x")),
                        ChildLong(from, "rowOff") + (isTopLevel ? 0 : AttributeLong(shapeOffset, "y")),
                        isTopLevel ? AttributeLong(anchorExtent, "cx") : AttributeLong(shapeExtent, "cx"),
                        isTopLevel ? AttributeLong(anchorExtent, "cy") : AttributeLong(shapeExtent, "cy"),
                        isTopLevel && anchorTo is not null ? ChildInt(anchorTo, "col") + 1 : null,
                        isTopLevel && anchorTo is not null ? ChildInt(anchorTo, "row") + 1 : null,
                        StringComparer.Ordinal.Equals(transform?.GetAttribute("flipH"), "1"),
                        StringComparer.Ordinal.Equals(transform?.GetAttribute("flipV"), "1"),
                        string.IsNullOrWhiteSpace(text) ? null : text,
                        Descendant(shape, "prstDash")?.GetAttribute("val"),
                        isConnector,
                        string.IsNullOrWhiteSpace(startConnection) ? null : startConnection,
                        string.IsNullOrWhiteSpace(endConnection) ? null : endConnection,
                        string.IsNullOrWhiteSpace(groupId) ? null : groupId));
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

    private static (int Row, int Column) ParseCellReference(string reference)
    {
        var split = 0;
        while (split < reference.Length && char.IsLetter(reference[split])) split++;
        if (split == 0 || split == reference.Length || !int.TryParse(reference[split..], NumberStyles.None, CultureInfo.InvariantCulture, out var row)) return (0, 0);
        var column = 0;
        foreach (var character in reference[..split].ToUpperInvariant())
        {
            if (character is < 'A' or > 'Z') return (0, 0);
            column = column * 26 + character - 'A' + 1;
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