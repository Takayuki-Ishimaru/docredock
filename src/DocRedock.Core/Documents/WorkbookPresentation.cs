using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocRedock.Core.Documents;

/// <summary>Inches of paper a print job leaves around the sheet.</summary>
public sealed record WorkbookPrintMargins(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Left = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Right = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Top = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Bottom = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Header = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Footer = null);

/// <summary>Raw header/footer text exactly as stored in the sheet part; Excel formatting and field
/// codes (&amp;P, &amp;A, font and color runs, escaped ampersands) are never interpreted here.</summary>
public sealed record WorkbookHeaderFooter(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OddHeader = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OddFooter = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EvenHeader = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EvenFooter = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FirstHeader = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FirstFooter = null)
{
    public bool DifferentOddEven { get; init; }
    public bool DifferentFirst { get; init; }
    public bool AlignWithMargins { get; init; } = true;
    public bool ScaleWithDocument { get; init; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string?>? ThemeColors { get; init; }
}

/// <summary>How one sheet prints: paper, scaling, repeated titles, print areas and manual breaks.</summary>
public sealed record WorkbookPrintSettings(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PaperSize = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Orientation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkbookPrintMargins? Margins = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? FitToPage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FitToWidth = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FitToHeight = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Scale = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Areas = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TitleRows = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TitleColumns = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? RowBreaks = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? ColumnBreaks = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? HorizontalCentering = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? VerticalCentering = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? HeaderFooterPresent = null)
{
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkbookHeaderFooter? HeaderFooter { get; init; }

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FirstPageNumber { get; init; }

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? UseFirstPageNumber { get; init; }
    public int HeaderFooterInvalidCount { get; init; }
    public string? PageOrder { get; init; }
    public bool FixedSourcePagination { get; init; }
}

/// <summary>One sheet's presentation metadata: geometry that survives as layout, not as content.
/// Column and row sizes are sparse and hold the SOURCE dimension even for hidden lines.</summary>
public sealed record WorkbookSheetPresentation(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PartUri = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? State = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UsedRange = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DefaultColumnWidth = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DefaultRowHeight = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<int, double>? ColumnWidths = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<int, double>? RowHeights = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? MergedRanges = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FrozenRows = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FrozenColumns = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PaneState = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TopLeftCell = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AutoFilterRange = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? TableRanges = null,
    int ConditionalFormatUnsupportedCount = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkbookPrintSettings? Print = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? HiddenColumns = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? HiddenRows = null);

/// <summary>A workbook's layout metadata, keyed "sheet-" plus the ORIGINAL sheet name.</summary>
public sealed record WorkbookPresentationLayout(
    int Version,
    IReadOnlyDictionary<string, WorkbookSheetPresentation> Sheets);

/// <summary>Safe write and bounded read of <see cref="WorkbookPresentationLayout"/> as the "xlsx_layout"
/// format extension. Reading never throws: malformed values are dropped with a bounded diagnostic list.</summary>
public static class WorkbookPresentationLayoutJson
{
    public const string ExtensionKey = "xlsx_layout";
    public const int CurrentVersion = 1;
    public const int MaxSheets = 4096;
    public const int MaxEntriesPerSheet = 65536;
    public const int MaxDiagnostics = 32;
    public const double MaxColumnWidth = 255d;
    public const double MaxRowHeight = 409.5d;
    public const int MaxHeaderFooterLength = 32767;
    public const int MaxFirstPageNumber = 32767;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static JsonElement Write(WorkbookPresentationLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return JsonSerializer.SerializeToElement(layout, WriteOptions);
    }

    public static bool TryRead(JsonElement element, out WorkbookPresentationLayout? layout, IList<string>? diagnostics = null)
    {
        layout = null;
        if (element.ValueKind != JsonValueKind.Object) { Note(diagnostics, "xlsx_layout is not a JSON object."); return false; }
        if (!element.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionNumber) || versionNumber != CurrentVersion)
        {
            Note(diagnostics, "xlsx_layout has no supported version.");
            return false;
        }
        if (!element.TryGetProperty("sheets", out var sheets) || sheets.ValueKind != JsonValueKind.Object)
        {
            Note(diagnostics, "xlsx_layout has no sheets object.");
            return false;
        }
        var result = new Dictionary<string, WorkbookSheetPresentation>(StringComparer.Ordinal);
        foreach (var sheet in sheets.EnumerateObject())
        {
            if (result.Count >= MaxSheets) { Note(diagnostics, "xlsx_layout sheet limit reached; later sheets dropped."); break; }
            if (sheet.Value.ValueKind != JsonValueKind.Object) { Note(diagnostics, $"xlsx_layout sheet {sheet.Name} is not an object."); continue; }
            var parsed = ReadSheet(sheet.Value, diagnostics);
            if (parsed is not null) result[sheet.Name] = parsed;
        }
        layout = new WorkbookPresentationLayout(CurrentVersion, result);
        return true;
    }

    private static WorkbookSheetPresentation? ReadSheet(JsonElement sheet, IList<string>? diagnostics)
    {
        string? name = ReadString(sheet, "name");
        var widths = ReadNumbers(sheet, "columnWidths", MaxColumnWidth, diagnostics);
        var heights = ReadNumbers(sheet, "rowHeights", MaxRowHeight, diagnostics);
        var print = sheet.TryGetProperty("print", out var printElement) && printElement.ValueKind == JsonValueKind.Object
            ? new WorkbookPrintSettings(
                PaperSize: ReadInt(printElement, "paperSize"),
                Orientation: ReadString(printElement, "orientation"),
                Margins: printElement.TryGetProperty("margins", out var margins) && margins.ValueKind == JsonValueKind.Object
                    ? new WorkbookPrintMargins(ReadDouble(margins, "left"), ReadDouble(margins, "right"), ReadDouble(margins, "top"),
                        ReadDouble(margins, "bottom"), ReadDouble(margins, "header"), ReadDouble(margins, "footer"))
                    : null,
                FitToPage: ReadBool(printElement, "fitToPage"),
                FitToWidth: ReadInt(printElement, "fitToWidth"),
                FitToHeight: ReadInt(printElement, "fitToHeight"),
                Scale: ReadInt(printElement, "scale"),
                Areas: ReadStrings(printElement, "areas", diagnostics),
                TitleRows: ReadString(printElement, "titleRows"),
                TitleColumns: ReadString(printElement, "titleColumns"),
                RowBreaks: ReadInts(printElement, "rowBreaks"),
                ColumnBreaks: ReadInts(printElement, "columnBreaks"),
                HorizontalCentering: ReadBool(printElement, "horizontalCentering"),
                VerticalCentering: ReadBool(printElement, "verticalCentering"),
                HeaderFooterPresent: ReadBool(printElement, "headerFooterPresent") ?? false)
            {
                HeaderFooter = ReadHeaderFooterOrNull(printElement, diagnostics),
                FirstPageNumber = ReadFirstPageNumber(printElement, diagnostics),
                UseFirstPageNumber = ReadBool(printElement, "useFirstPageNumber"),
                HeaderFooterInvalidCount = Math.Clamp(ReadInt(printElement, "headerFooterInvalidCount") ?? 0, 0, 6),
                PageOrder = ReadString(printElement, "pageOrder") switch { "overThenDown" => "overThenDown", "downThenOver" => "downThenOver", _ => null },
                FixedSourcePagination = ReadBool(printElement, "fixedSourcePagination") ?? false,
            }
            : null;
        return new WorkbookSheetPresentation(
            name,
            ReadString(sheet, "partUri"),
            ReadString(sheet, "state"),
            ReadString(sheet, "usedRange"),
            ReadDouble(sheet, "defaultColumnWidth"),
            ReadDouble(sheet, "defaultRowHeight"),
            widths,
            heights,
            ReadStrings(sheet, "mergedRanges", diagnostics),
            ReadInt(sheet, "frozenRows"),
            ReadInt(sheet, "frozenColumns"),
            ReadString(sheet, "paneState"),
            ReadString(sheet, "topLeftCell"),
            ReadString(sheet, "autoFilterRange"),
            ReadStrings(sheet, "tableRanges", diagnostics),
            ReadInt(sheet, "conditionalFormatUnsupportedCount") ?? 0,
            print, ReadInts(sheet, "hiddenColumns"), ReadInts(sheet, "hiddenRows"));
    }

    private static void Note(IList<string>? diagnostics, string message)
    {
        if (diagnostics is null || diagnostics.Count >= MaxDiagnostics) return;
        if (diagnostics.Count == MaxDiagnostics - 1) diagnostics.Add("xlsx_layout: further diagnostics suppressed.");
        else diagnostics.Add(message);
    }

    private static string? ReadString(JsonElement owner, string key) =>
        owner.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 4096 } text ? text : null;

    private static bool? ReadBool(JsonElement owner, string key) =>
        owner.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static int? ReadInt(JsonElement owner, string key) =>
        owner.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static double? ReadDouble(JsonElement owner, string key) =>
        owner.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;

    private static IReadOnlyList<string>? ReadStrings(JsonElement owner, string key, IList<string>? diagnostics)
    {
        if (!owner.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array) return null;
        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (result.Count >= MaxEntriesPerSheet) { Note(diagnostics, $"xlsx_layout {key} limit reached."); break; }
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 and <= 512 } text) result.Add(text);
            else Note(diagnostics, $"xlsx_layout {key} entry is not a string.");
        }
        return result;
    }

    private static IReadOnlyDictionary<int, double>? ReadNumbers(JsonElement owner, string key, double maximum, IList<string>? diagnostics)
    {
        if (!owner.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Object) return null;
        var result = new Dictionary<int, double>();
        foreach (var entry in value.EnumerateObject())
        {
            if (result.Count >= MaxEntriesPerSheet) { Note(diagnostics, $"xlsx_layout {key} limit reached."); break; }
            if (!int.TryParse(entry.Name, out var index) || index < 1 || entry.Value.ValueKind != JsonValueKind.Number ||
                !entry.Value.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0 || number > maximum)
            {
                Note(diagnostics, $"xlsx_layout {key} entry {entry.Name} is malformed.");
                continue;
            }
            result[index] = number;
        }
        return result;
    }

    private static IReadOnlyList<int>? ReadInts(JsonElement owner, string key)
    {
        if (!owner.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array) return null;
        var result = new List<int>();
        foreach (var item in value.EnumerateArray())
        {
            if (result.Count >= MaxEntriesPerSheet) break;
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number)) result.Add(number);
        }
        return result;
    }

    private static WorkbookHeaderFooter? ReadHeaderFooterOrNull(JsonElement owner, IList<string>? diagnostics)
    {
        if (!owner.TryGetProperty("headerFooter", out var value)) return null;
        if (value.ValueKind == JsonValueKind.Object) return ReadHeaderFooter(value, diagnostics);
        if (value.ValueKind != JsonValueKind.Null) Note(diagnostics, "xlsx_layout headerFooter is not an object; ignored.");
        return null;
    }

    private static WorkbookHeaderFooter ReadHeaderFooter(JsonElement element, IList<string>? diagnostics) => new(
        ReadHeaderFooterString(element, "oddHeader", diagnostics),
        ReadHeaderFooterString(element, "oddFooter", diagnostics),
        ReadHeaderFooterString(element, "evenHeader", diagnostics),
        ReadHeaderFooterString(element, "evenFooter", diagnostics),
        ReadHeaderFooterString(element, "firstHeader", diagnostics),
        ReadHeaderFooterString(element, "firstFooter", diagnostics))
    {
        DifferentOddEven = ReadBool(element, "differentOddEven") ?? false,
        DifferentFirst = ReadBool(element, "differentFirst") ?? false,
        AlignWithMargins = ReadBool(element, "alignWithMargins") ?? true,
        ScaleWithDocument = ReadBool(element, "scaleWithDocument") ?? true,
        ThemeColors = element.TryGetProperty("themeColors", out var colors) && colors.ValueKind == JsonValueKind.Array
            ? colors.EnumerateArray().Take(12).Select(c => c.ValueKind == JsonValueKind.String && c.GetString() is { Length: 7 } color &&
                color[0] == '#' && color.AsSpan(1).ToString().All(char.IsAsciiHexDigit) ? c.GetString() : null).ToArray() : null,
    };

    private static string? ReadHeaderFooterString(JsonElement owner, string key, IList<string>? diagnostics)
    {
        if (!owner.TryGetProperty(key, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String)
        {
            Note(diagnostics, $"xlsx_layout headerFooter {key} is not a string; dropped.");
            return null;
        }
        var text = value.GetString()!;
        if (text.Length > MaxHeaderFooterLength)
        {
            Note(diagnostics, $"xlsx_layout headerFooter {key} exceeds {MaxHeaderFooterLength} characters; dropped.");
            return null;
        }
        return text;
    }

    private static int? ReadFirstPageNumber(JsonElement owner, IList<string>? diagnostics)
    {
        if (!owner.TryGetProperty("firstPageNumber", out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number is >= 1 and <= MaxFirstPageNumber) return number;
        if (value.ValueKind != JsonValueKind.Null) Note(diagnostics, "xlsx_layout firstPageNumber is out of range or malformed; dropped.");
        return null;
    }
}
