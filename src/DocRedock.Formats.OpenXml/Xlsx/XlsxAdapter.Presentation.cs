using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.OpenXml.Xlsx;

public sealed partial class XlsxAdapter
{
    private sealed class SheetLayoutScan
    {
        public Dictionary<int, double> ColumnWidths { get; } = [];
        public Dictionary<int, double> RowHeights { get; } = [];
        public int FrozenRows, FrozenColumns, ConditionalFormatCount;
        public string? PaneState, TopLeftCell, AutoFilterRange, BreakOwner;
        public int? PaperSize, FitToWidth, FitToHeight, Scale;
        public string? Orientation;
        public WorkbookPrintMargins? Margins;
        public bool? FitToPage, HorizontalCentering, VerticalCentering;
        public bool HeaderFooterPresent;
        public bool HasWorksheetDrawing;
        public string? HfOddHeader, HfOddFooter, HfEvenHeader, HfEvenFooter, HfFirstHeader, HfFirstFooter;
        public bool? HfDifferentOddEven, HfDifferentFirst, HfAlignWithMargins, HfScaleWithDocument;
        public int? FirstPageNumber;
        public bool? UseFirstPageNumber;
        public int HfInvalidCount;
        public string? PageOrder;
        public List<int> RowBreaks { get; } = [];
        public List<int> ColumnBreaks { get; } = [];
    }

    private static void ReadPresentationElement(XmlReader reader, SheetLayoutScan layout)
    {
        int? Integer(string name) => int.TryParse(reader.GetAttribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        double? Number(string name) => double.TryParse(reader.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= 0 ? n : null;
        bool? Flag(string name) => reader.GetAttribute(name) is { } value ? IsOn(value) : null;
        switch (reader.LocalName)
        {
            case "drawing": layout.HasWorksheetDrawing = true; break;
            case "pane":
                layout.PaneState = reader.GetAttribute("state");
                if (layout.PaneState is "frozen" or "frozenSplit")
                {
                    layout.FrozenColumns = Math.Clamp(Integer("xSplit") ?? 0, 0, MaxExcelColumns);
                    layout.FrozenRows = Math.Clamp(Integer("ySplit") ?? 0, 0, MaxExcelRows);
                }
                layout.TopLeftCell = reader.GetAttribute("topLeftCell");
                break;
            case "autoFilter": layout.AutoFilterRange = reader.GetAttribute("ref"); break;
            case "pageSetup":
                layout.PaperSize = Integer("paperSize");
                layout.Orientation = reader.GetAttribute("orientation");
                layout.FitToWidth = Integer("fitToWidth"); layout.FitToHeight = Integer("fitToHeight"); layout.Scale = Integer("scale");
                if (Integer("firstPageNumber") is > 0 and <= WorkbookPresentationLayoutJson.MaxFirstPageNumber and var firstPage) layout.FirstPageNumber = firstPage;
                layout.UseFirstPageNumber = Flag("useFirstPageNumber");
                layout.PageOrder = reader.GetAttribute("pageOrder");
                break;
            case "pageMargins":
                layout.Margins = new(Number("left"), Number("right"), Number("top"), Number("bottom"), Number("header"), Number("footer"));
                break;
            case "pageSetUpPr": layout.FitToPage = Flag("fitToPage"); break;
            case "printOptions":
                layout.HorizontalCentering = Flag("horizontalCentered"); layout.VerticalCentering = Flag("verticalCentered");
                break;
            case "rowBreaks": layout.BreakOwner = "row"; break;
            case "colBreaks": layout.BreakOwner = "column"; break;
            case "brk" when Flag("man") == true:
                if (Integer("id") is > 0 and <= MaxExcelRows and var id)
                {
                    var breaks = layout.BreakOwner == "row" ? layout.RowBreaks : layout.ColumnBreaks;
                    if (breaks.Count < WorkbookPresentationLayoutJson.MaxEntriesPerSheet) breaks.Add(id);
                }
                break;
            case "conditionalFormatting": layout.ConditionalFormatCount++; break;
            // Raw text is stored verbatim for the parent parser; Excel header/footer codes are never interpreted.
            case "headerFooter":
                layout.HfDifferentOddEven = Flag("differentOddEven");
                layout.HfDifferentFirst = Flag("differentFirst");
                layout.HfAlignWithMargins = Flag("alignWithMargins");
                layout.HfScaleWithDocument = Flag("scaleWithDoc") ?? Flag("scaleWithDocument");
                using (var parts = reader.ReadSubtree())
                {
                    string? active = null;
                    StringBuilder? text = null;
                    while (parts.Read())
                    {
                        if (parts.NodeType == XmlNodeType.Element &&
                            parts.LocalName is "oddHeader" or "oddFooter" or "firstHeader" or "firstFooter" or "evenHeader" or "evenFooter")
                        {
                            if (parts.IsEmptyElement) Store(parts.LocalName, string.Empty);
                            else { active = parts.LocalName; text = new StringBuilder(); }
                        }
                        else if (parts.NodeType == XmlNodeType.EndElement && parts.LocalName == active)
                        {
                            Store(active!, text?.ToString() ?? string.Empty);
                            active = null; text = null;
                        }
                        else if (active is not null && parts.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                        {
                            if (text is not null && text.Length <= WorkbookPresentationLayoutJson.MaxHeaderFooterLength)
                                text.Append(parts.Value.AsSpan(0, Math.Min(parts.Value.Length, WorkbookPresentationLayoutJson.MaxHeaderFooterLength+1-text.Length)));
                        }
                    }
                }
                break;
        }
        void Store(string kind,string source)
        {
            if(source.Length>WorkbookPresentationLayoutJson.MaxHeaderFooterLength){layout.HfInvalidCount++;layout.HeaderFooterPresent=true;return;}
            var value=XmlConvert.DecodeName(source);
            switch(kind)
            {
                case "oddHeader": layout.HfOddHeader=value;break;case "oddFooter":layout.HfOddFooter=value;break;
                case "evenHeader":layout.HfEvenHeader=value;break;case "evenFooter":layout.HfEvenFooter=value;break;
                case "firstHeader":layout.HfFirstHeader=value;break;default:layout.HfFirstFooter=value;break;
            }
            if(!string.IsNullOrWhiteSpace(value))layout.HeaderFooterPresent=true;
        }
    }

    private static bool HasHeaderFooterMetadata(SheetLayoutScan layout) =>
        layout.HfOddHeader is not null || layout.HfOddFooter is not null || layout.HfEvenHeader is not null ||
        layout.HfEvenFooter is not null || layout.HfFirstHeader is not null || layout.HfFirstFooter is not null ||
        layout.HfDifferentOddEven is not null || layout.HfDifferentFirst is not null ||
        layout.HfAlignWithMargins is not null || layout.HfScaleWithDocument is not null || layout.HfInvalidCount > 0;

    private static WorkbookPresentationLayout BuildWorkbookPresentation(IReadOnlyDictionary<string, byte[]> package,
        IReadOnlyList<XlsxWorksheetRecord> worksheets, IReadOnlyDictionary<string, WorksheetScan> scans,
        IReadOnlySet<int> fullyHandledConditionalStyles)
    {
        var sheets = new Dictionary<string, WorkbookSheetPresentation>(StringComparer.Ordinal);
        var footerTheme = ReadThemeColors(package);
        foreach (var sheet in worksheets)
        {
            var scan = scans[sheet.Name]; var layout = scan.Layout;
            sheets["sheet-" + sheet.Name] = new WorkbookSheetPresentation(
                Name: sheet.Name, PartUri: sheet.PartUri, State: sheet.SheetState, UsedRange: sheet.UsedRange,
                DefaultColumnWidth: scan.Metrics.DefaultColumnWidth, DefaultRowHeight: scan.Metrics.DefaultRowHeight,
                ColumnWidths: layout.ColumnWidths, RowHeights: layout.RowHeights, MergedRanges: sheet.MergedRanges,
                FrozenRows: layout.FrozenRows, FrozenColumns: layout.FrozenColumns, PaneState: layout.PaneState,
                TopLeftCell: layout.TopLeftCell, AutoFilterRange: layout.AutoFilterRange,
                TableRanges: sheet.Tables?.Select(table => table.Range).ToArray(), ConditionalFormatUnsupportedCount:
                    Math.Max(0, layout.ConditionalFormatCount - scan.ConditionalFormats.Count(format =>
                        format.ChildNodes.OfType<XmlElement>().Where(rule => rule.LocalName == "cfRule").All(rule =>
                            int.TryParse(rule.GetAttribute("dxfId"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && fullyHandledConditionalStyles.Contains(id)))),
                Print: new WorkbookPrintSettings(layout.PaperSize, layout.Orientation, layout.Margins,
                    layout.FitToPage, layout.FitToWidth, layout.FitToHeight, layout.Scale,
                    RowBreaks: layout.RowBreaks, ColumnBreaks: layout.ColumnBreaks,
                    HorizontalCentering: layout.HorizontalCentering, VerticalCentering: layout.VerticalCentering,
                    HeaderFooterPresent: layout.HeaderFooterPresent ? true : null)
                {
                    HeaderFooter = HasHeaderFooterMetadata(layout)
                        ? new WorkbookHeaderFooter(layout.HfOddHeader, layout.HfOddFooter, layout.HfEvenHeader,
                            layout.HfEvenFooter, layout.HfFirstHeader, layout.HfFirstFooter)
                        {
                            DifferentOddEven = layout.HfDifferentOddEven ?? false,
                            DifferentFirst = layout.HfDifferentFirst ?? false,
                            AlignWithMargins = layout.HfAlignWithMargins ?? true,
                            ScaleWithDocument = layout.HfScaleWithDocument ?? true,
                            ThemeColors = footerTheme,
                        }
                        : null,
                    FirstPageNumber = layout.FirstPageNumber,
                    UseFirstPageNumber = layout.UseFirstPageNumber,
                    HeaderFooterInvalidCount = layout.HfInvalidCount,
                    PageOrder = layout.PageOrder,
                    FixedSourcePagination = HasHeaderFooterMetadata(layout),
                },
                HiddenColumns: sheet.HiddenColumns?.Order().ToArray(), HiddenRows: scan.HiddenRows.Order().ToArray());
        }
        if (package.TryGetValue("xl/workbook.xml", out var xml))
        {
            using var reader = XmlReader.Create(new MemoryStream(xml), SafeXml);
            var workbook = XDocument.Load(reader);
            var ns = workbook.Root?.Name.Namespace ?? XNamespace.None;
            var order = workbook.Root?.Element(ns + "sheets")?.Elements(ns + "sheet").Select(s => (string?)s.Attribute("name")).ToArray() ?? [];
            foreach (var name in workbook.Root?.Element(ns + "definedNames")?.Elements(ns + "definedName") ?? [])
            {
                if (!int.TryParse((string?)name.Attribute("localSheetId"), NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= order.Length ||
                    !sheets.TryGetValue("sheet-" + order[index], out var sheet)) continue;
                var print = sheet.Print ?? new();
                var ranges = SplitPrintRanges(name.Value).Take(64).ToArray();
                switch ((string?)name.Attribute("name"))
                {
                    case "_xlnm.Print_Area": print = print with { Areas = ranges.Where(r => ParseRange(r.Replace("$", "", StringComparison.Ordinal)) is not null).ToArray() }; break;
                    case "_xlnm.Print_Titles":
                        foreach (var range in ranges)
                        {
                            var first = range.TrimStart('$');
                            if (first.Length == 0) continue;
                            if (char.IsAsciiDigit(first[0])) print = print with { TitleRows = range };
                            else if (char.IsAsciiLetter(first[0])) print = print with { TitleColumns = range };
                        }
                        break;
                }
                sheets["sheet-" + order[index]] = sheet with { Print = print };
            }
        }
        return new(WorkbookPresentationLayoutJson.CurrentVersion, sheets);
    }

    private static IEnumerable<string> SplitPrintRanges(string value)
    {
        var start = 0; var quoted = false; var bang = -1;
        for (var i = 0; i <= value.Length; i++)
        {
            var ch = i < value.Length ? value[i] : ',';
            if (ch == '\'') quoted = !quoted;
            else if (ch == '!' && !quoted) bang = i;
            else if (ch == ',' && !quoted)
            {
                var range = value[(bang >= start ? bang + 1 : start)..i].Trim();
                if (range.Length is > 0 and <= 512) yield return range;
                start = i + 1; bang = -1;
            }
        }
    }

    private static string? PresentationStyleId(XlsxCellRecord cell, IReadOnlyDictionary<string, StyleDescriptor> styles) =>
        int.TryParse(cell.StyleId ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 &&
        styles.ContainsKey("xlsx-style-" + index.ToString(CultureInfo.InvariantCulture)) ? "xlsx-style-" + index.ToString(CultureInfo.InvariantCulture) : null;

    private static Dictionary<string, StyleDescriptor> ReadPresentationStyles(IReadOnlyDictionary<string, byte[]> package,
        out IReadOnlySet<int> fullyHandledConditionalStyles)
    {
        fullyHandledConditionalStyles = new HashSet<int>();
        var result = new Dictionary<string, StyleDescriptor>(StringComparer.Ordinal);
        if (!package.TryGetValue("xl/styles.xml", out var bytes)) return result;
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        var root = XDocument.Load(reader).Root;
        if (root is null) return result;
        var ns = root.Name.Namespace;
        fullyHandledConditionalStyles = (root.Element(ns + "dxfs")?.Elements(ns + "dxf") ?? []).Select((dxf, index) => (dxf, index))
            .Where(item => item.dxf.Elements().All(e => e.Name == ns + "font") &&
                item.dxf.Element(ns + "font") is { } font && font.Element(ns + "strike") is not null && font.Elements().All(e => e.Name == ns + "strike"))
            .Select(item => item.index).ToHashSet();
        XElement[] Elements(string parent, string child) => root.Element(ns + parent)?.Elements(ns + child).Take(65536).ToArray() ?? [];
        var fonts = Elements("fonts", "font"); var fills = Elements("fills", "fill"); var borders = Elements("borders", "border");
        var inherited = Elements("cellStyleXfs", "xf");
        var theme = ReadThemeColors(package);
        var indexed = root.Element(ns + "colors")?.Element(ns + "indexedColors")?.Elements(ns + "rgbColor").Select(c => (string?)c.Attribute("rgb")).ToArray();
        foreach (var (xf, index) in Elements("cellXfs", "xf").Select((xf, i) => (xf, i)))
        {
            var props = new Dictionary<string, string>(StringComparer.Ordinal);
            var baseId = Integer(xf, "xfId");
            var baseXf = baseId >= 0 && baseId < inherited.Length ? inherited[baseId] : null;
            int Id(string name)
            {
                var apply = name switch { "fontId" => "applyFont", "fillId" => "applyFill", "borderId" => "applyBorder", _ => "" };
                return (string?)xf.Attribute(apply) is "0" or "false" && baseXf is not null ? Integer(baseXf, name) :
                    xf.Attribute(name) is not null ? Integer(xf, name) : baseXf is null ? -1 : Integer(baseXf, name);
            }
            if (Id("fontId") is var fontId && fontId >= 0 && fontId < fonts.Length)
            {
                var font = fonts[fontId];
                AddColor("color", font.Element(ns + "color"));
                if (font.Element(ns + "b") is { } bold && XmlOn(bold)) props["font-weight"] = "bold";
                if (font.Element(ns + "i") is { } italic && XmlOn(italic)) props["font-style"] = "italic";
                // Effective strike/underline (including conditional rules and hyperlink exclusions)
                // live on the cell's text ranges; a static style must not reapply the source font.
                if (Number(font.Element(ns + "sz"), "val") is > 0 and <= 4096 and var size) props["font-size"] = size.ToString("0.###", CultureInfo.InvariantCulture) + "pt";
            }
            if (Id("fillId") is var fillId && fillId >= 0 && fillId < fills.Length && fills[fillId].Element(ns + "patternFill") is { } fill && (string?)fill.Attribute("patternType") == "solid")
                AddColor("background-color", fill.Element(ns + "fgColor"));
            if (Id("borderId") is var borderId && borderId >= 0 && borderId < borders.Length)
                foreach (var side in new[] { "left", "right", "top", "bottom" })
                {
                    var edge = borders[borderId].Element(ns + side);
                    var border = BorderCss((string?)edge?.Attribute("style"));
                    if (border is not null) props["border-" + side] = border + " " + (ResolvePresentationColor(edge?.Element(ns + "color"), theme, indexed) ?? "#000000");
                }
            var effectiveAlignment = (string?)xf.Attribute("applyAlignment") is "0" or "false"
                ? baseXf?.Element(ns + "alignment") : xf.Element(ns + "alignment") ?? baseXf?.Element(ns + "alignment");
            if (effectiveAlignment is { } alignment)
            {
                var horizontal = (string?)alignment.Attribute("horizontal");
                if (horizontal is "left" or "right" or "center" or "justify") props["text-align"] = horizontal;
                var vertical = (string?)alignment.Attribute("vertical");
                if (vertical is "top" or "bottom" or "center") props["vertical-align"] = vertical == "center" ? "middle" : vertical;
                if (IsOn((string?)alignment.Attribute("wrapText") ?? "0")) props["white-space"] = "pre-wrap";
            }
            var id = "xlsx-style-" + index;
            result[id] = new(id, Properties: props);
            void AddColor(string key, XElement? color) { if (ResolvePresentationColor(color, theme, indexed) is { } value) props[key] = value; }
        }
        return result;
    }

    private static int Integer(XElement element, string attr) => int.TryParse((string?)element.Attribute(attr), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
    private static double? Number(XElement? element, string attr) => double.TryParse((string?)element?.Attribute(attr), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
    private static bool XmlOn(XElement element) => IsOn((string?)element.Attribute("val"));
    private static string? BorderCss(string? style) => style switch
    {
        "thin" => "1px solid", "medium" => "2px solid", "thick" => "3px solid", "double" => "3px double", "hair" => "0.5px solid",
        "dotted" => "1px dotted", "dashed" or "dashDot" or "dashDotDot" => "1px dashed",
        "mediumDashed" or "mediumDashDot" or "mediumDashDotDot" or "slantDashDot" => "2px dashed", _ => null,
    };

    private static string?[] ReadThemeColors(IReadOnlyDictionary<string, byte[]> package)
    {
        var result = new string?[12];
        string? themePath = null;
        if (package.TryGetValue("xl/_rels/workbook.xml.rels", out var rels))
        {
            using var relReader = XmlReader.Create(new MemoryStream(rels), SafeXml);
            var relationship = XDocument.Load(relReader).Root?.Elements().FirstOrDefault(rel =>
                ((string?)rel.Attribute("Type"))?.EndsWith("/theme", StringComparison.Ordinal) == true && (string?)rel.Attribute("TargetMode") != "External");
            if ((string?)relationship?.Attribute("Target") is { } target)
                themePath = NormalizePartPath(target.StartsWith('/') ? target : "xl/" + target);
        }
        if (!package.TryGetValue(themePath ?? "xl/theme/theme1.xml", out var xml)) return result;
        using var reader = XmlReader.Create(new MemoryStream(xml), SafeXml);
        var root = XDocument.Load(reader).Root;
        var ns = root?.Name.Namespace ?? XNamespace.None;
        var scheme = root?.Element(ns + "themeElements")?.Element(ns + "clrScheme");
        var names = new[] { "lt1", "dk1", "lt2", "dk2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink" };
        for (var i = 0; i < names.Length; i++)
        {
            var color = scheme?.Element(ns + names[i])?.Elements().FirstOrDefault();
            result[i] = HexColor((string?)color?.Attribute(color.Name.LocalName == "sysClr" ? "lastClr" : "val"));
        }
        return result;
    }

    private static string? ResolvePresentationColor(XElement? color, string?[] theme, string?[]? indexed)
    {
        if (color is null) return null;
        string? value = HexColor((string?)color.Attribute("rgb"));
        if (value is null && Integer(color, "theme") is var slot && slot >= 0 && slot < theme.Length) value = theme[slot];
        if (value is null && Integer(color, "indexed") is var ix && ix >= 0)
            value = HexColor(indexed is not null && ix < indexed.Length ? indexed[ix] : ix < IndexedColors.Length ? IndexedColors[ix] : null);
        return value is not null && Number(color, "tint") is >= -1 and <= 1 and var tint && tint != 0 ? TintColor(value, tint) : value;
    }

    private static string? HexColor(string? value)
    {
        if (value?.Length == 8) value = value[2..];
        return value?.Length == 6 && value.All(Uri.IsHexDigit) ? "#" + value.ToUpperInvariant() : null;
    }

    private static readonly string[] IndexedColors = ("000000 FFFFFF FF0000 00FF00 0000FF FFFF00 FF00FF 00FFFF " +
        "000000 FFFFFF FF0000 00FF00 0000FF FFFF00 FF00FF 00FFFF 800000 008000 000080 808000 800080 008080 C0C0C0 808080 " +
        "9999FF 993366 FFFFCC CCFFFF 660066 FF8080 0066CC CCCCFF 000080 FF00FF FFFF00 00FFFF 800080 800000 008080 0000FF " +
        "00CCFF CCFFFF CCFFCC FFFF99 99CCFF FF99CC CC99FF FFCC99 3366FF 33CCCC 99CC00 FFCC00 FF9900 FF6600 666699 969696 " +
        "003366 339966 003300 333300 993300 993366 333399 333333").Split(' ');

    private static string TintColor(string hex, double tint)
    {
        var rgb = new[] { Convert.ToInt32(hex[1..3], 16) / 255d, Convert.ToInt32(hex[3..5], 16) / 255d, Convert.ToInt32(hex[5..7], 16) / 255d };
        var max = rgb.Max(); var min = rgb.Min(); var delta = max - min; var light = (max + min) / 2;
        var saturation = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * light - 1));
        var hue = delta == 0 ? 0 : max == rgb[0] ? ((rgb[1] - rgb[2]) / delta + (rgb[1] < rgb[2] ? 6 : 0)) / 6 :
            max == rgb[1] ? ((rgb[2] - rgb[0]) / delta + 2) / 6 : ((rgb[0] - rgb[1]) / delta + 4) / 6;
        light = tint < 0 ? light * (1 + tint) : light * (1 - tint) + tint;
        var q = light < 0.5 ? light * (1 + saturation) : light + saturation - light * saturation; var p = 2 * light - q;
        double Channel(double t)
        {
            t = (t + 1) % 1;
            return saturation == 0 ? light : t < 1d / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2d / 3 ? p + (q - p) * (2d / 3 - t) * 6 : p;
        }
        int Byte(double t) => Math.Clamp((int)Math.Round(Channel(t) * 255), 0, 255);
        return $"#{Byte(hue + 1d / 3):X2}{Byte(hue):X2}{Byte(hue - 1d / 3):X2}";
    }
}
