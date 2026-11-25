using System.Globalization;
using System.Xml;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Drawing;

namespace DocRedock.Formats.OpenXml.Pptx;

public sealed partial class PptxAdapter
{
    /// <summary>Size in points of table text that sets none: the master's p:otherStyle, else the presentation's p:defaultTextStyle, else 18.</summary>
    private static double DefaultTableTextSize(XmlDocument? master, XmlDocument? presentation)
    {
        static double? Size(XmlNode? style) =>
            style?.SelectSingleNode("*[local-name()='lvl1pPr']/*[local-name()='defRPr']/@sz") is XmlAttribute size &&
            double.TryParse(size.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 100 and <= 400000 ? value / 100 : null;
        return Size(master?.SelectSingleNode("/*/*[local-name()='txStyles']/*[local-name()='otherStyle']")) ??
            Size(presentation?.SelectSingleNode("/*/*[local-name()='defaultTextStyle']")) ?? 18;
    }

    /// <summary>
    /// The effective cell appearance of the tables of a slide (a:tbl in a p:graphicFrame). A cell takes, later over earlier:
    /// <list type="bullet">
    /// <item>the table style a:tblPr/a:tableStyleId names in ppt/tableStyles.xml: a:tblBg, a:wholeTbl, a:band1V/a:band2V,
    /// a:band1H/a:band2H, a:lastCol, a:firstCol, a:lastRow, a:firstRow and the corner cells, each only when the a:tblPr
    /// flag (bandCol, bandRow, lastCol, firstCol, lastRow, firstRow) is set. Bands count from the first row or column
    /// after the special one and leave the special rows and columns out. A part gives a fill (a:tcStyle/a:fill or
    /// a:fillRef), text weight, slant, color and font (a:tcTxStyle with a:font or a:fontRef) and edges (a:tcBdr: left,
    /// right, top and bottom on the outline of the part's region, insideH and insideV between its cells, tl2br and tr2bl
    /// across each cell);</item>
    /// <item>the cell's own a:tcPr fill, a:lnL, a:lnR, a:lnT, a:lnB, a:lnTlToBr, a:lnBlToTr and anchor;</item>
    /// <item>for the text, the properties of the cell's first run (a:rPr sz, b, i, solid fill, a:latin, a:ea, a:cs) and the
    /// first paragraph's alignment; a paragraph that is aligned differently or indented (a:pPr algn, marL, a positive indent)
    /// keeps its own. A font no layer names is the one the master's p:otherStyle gives, else the theme's
    /// minor font. Every run is kept with its own effective formatting when one differs from the cell's.</item>
    /// </list>
    /// A gradient fill keeps its stops and a pattern fill (a:pattFill) its preset and two colors; an edge keeps its width, its dash (a:prstDash) and a compound line as a double one.
    /// Without a style id, and under the two "No Style" ids, a cell has no fill and no edge of its own. A style id
    /// the file does not define leaves what the cell does not set unresolved (null), except PowerPoint's default
    /// "Medium Style 2 - Accent 1", whose definition is built in.
    /// </summary>
    private sealed class SlideTableStyles(XmlDocument? definitions)
    {
        private const string BuiltIn = """
            <a:tblStyleLst xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
            <a:tblStyle styleId="{2D5ABB26-0587-4C30-8999-92F81FD0307C}" styleName="No Style, No Grid"><a:wholeTbl><a:tcTxStyle><a:schemeClr val="tx1"/></a:tcTxStyle></a:wholeTbl></a:tblStyle>
            <a:tblStyle styleId="{5940675A-B579-460E-94D1-54222C63F5DA}" styleName="No Style, Table Grid"><a:wholeTbl><a:tcTxStyle><a:schemeClr val="tx1"/></a:tcTxStyle><a:tcStyle><a:tcBdr>
            <a:left><a:ln w="12700"><a:solidFill><a:schemeClr val="tx1"/></a:solidFill></a:ln></a:left><a:right><a:ln w="12700"><a:solidFill><a:schemeClr val="tx1"/></a:solidFill></a:ln></a:right>
            <a:top><a:ln w="12700"><a:solidFill><a:schemeClr val="tx1"/></a:solidFill></a:ln></a:top><a:bottom><a:ln w="12700"><a:solidFill><a:schemeClr val="tx1"/></a:solidFill></a:ln></a:bottom>
            <a:insideH><a:ln w="12700"><a:solidFill><a:schemeClr val="tx1"/></a:solidFill></a:ln></a:insideH><a:insideV><a:ln w="12700"><a:solidFill><a:schemeClr val="tx1"/></a:solidFill></a:ln></a:insideV>
            </a:tcBdr><a:fill><a:noFill/></a:fill></a:tcStyle></a:wholeTbl></a:tblStyle>
            <a:tblStyle styleId="{5C22544A-7EE6-4342-B048-85BB5CCE0C2F}" styleName="Medium Style 2 - Accent 1"><a:wholeTbl><a:tcTxStyle><a:schemeClr val="dk1"/></a:tcTxStyle><a:tcStyle><a:tcBdr>
            <a:left><a:ln w="12700"><a:solidFill><a:schemeClr val="lt1"/></a:solidFill></a:ln></a:left><a:right><a:ln w="12700"><a:solidFill><a:schemeClr val="lt1"/></a:solidFill></a:ln></a:right>
            <a:top><a:ln w="12700"><a:solidFill><a:schemeClr val="lt1"/></a:solidFill></a:ln></a:top><a:bottom><a:ln w="12700"><a:solidFill><a:schemeClr val="lt1"/></a:solidFill></a:ln></a:bottom>
            <a:insideH><a:ln w="12700"><a:solidFill><a:schemeClr val="lt1"/></a:solidFill></a:ln></a:insideH><a:insideV><a:ln w="12700"><a:solidFill><a:schemeClr val="lt1"/></a:solidFill></a:ln></a:insideV>
            </a:tcBdr><a:fill><a:solidFill><a:schemeClr val="accent1"><a:tint val="20000"/></a:schemeClr></a:solidFill></a:fill></a:tcStyle></a:wholeTbl>
            <a:band1H><a:tcStyle><a:tcBdr/><a:fill><a:solidFill><a:schemeClr val="accent1"><a:tint val="40000"/></a:schemeClr></a:solidFill></a:fill></a:tcStyle></a:band1H>
            <a:band1V><a:tcStyle><a:tcBdr/><a:fill><a:solidFill><a:schemeClr val="accent1"><a:tint val="40000"/></a:schemeClr></a:solidFill></a:fill></a:tcStyle></a:band1V>
            <a:lastCol><a:tcTxStyle b="on"><a:schemeClr val="lt1"/></a:tcTxStyle><a:tcStyle><a:tcBdr/><a:fill><a:solidFill><a:schemeClr val="accent1"/></a:solidFill></a:fill></a:tcStyle></a:lastCol>
            <a:firstCol><a:tcTxStyle b="on"><a:schemeClr val="lt1"/></a:tcTxStyle><a:tcStyle><a:tcBdr/><a:fill><a:solidFill><a:schemeClr val="accent1"/></a:solidFill></a:fill></a:tcStyle></a:firstCol>
            <a:lastRow><a:tcTxStyle b="on"><a:schemeClr val="lt1"/></a:tcTxStyle><a:tcStyle><a:tcBdr><a:top><a:ln w="38100"><a:solidFill><a:schemeClr val="lt1"/></a:solidFill></a:ln></a:top></a:tcBdr><a:fill><a:solidFill><a:schemeClr val="accent1"/></a:solidFill></a:fill></a:tcStyle></a:lastRow>
            <a:firstRow><a:tcTxStyle b="on"><a:schemeClr val="lt1"/></a:tcTxStyle><a:tcStyle><a:tcBdr><a:bottom><a:ln w="38100"><a:solidFill><a:schemeClr val="lt1"/></a:solidFill></a:ln></a:bottom></a:tcBdr><a:fill><a:solidFill><a:schemeClr val="accent1"/></a:solidFill></a:fill></a:tcStyle></a:firstRow>
            </a:tblStyle></a:tblStyleLst>
            """;
        private static readonly string[] FillNames = ["noFill", "solidFill", "gradFill", "blipFill", "pattFill", "grpFill"];
        private static readonly SourceDrawingTableBorder NoEdge = new(0);
        private XmlDocument? _builtIn;

        /// <param name="inheritedText">The a:lstStyle-shaped defaults of table text (the master's p:otherStyle), which name its font.</param>
        /// <param name="withText">True for tables without a semantic node: every cell then holds its text as runs.</param>
        public Dictionary<string, SourceDrawingTableStyle> Read(XmlElement tree, IReadOnlyList<WorkbookDrawingObject> objects, XmlDocument? theme, double defaultSize,
            XmlElement? inheritedText = null, bool withText = false)
        {
            var result = new Dictionary<string, SourceDrawingTableStyle>(StringComparer.Ordinal);
            var tables = objects.Where(item => item.Kind == "table").ToArray();
            if (tables.Length == 0) return result;
            var frames = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
            foreach (XmlElement frame in tree.SelectNodes(".//*[local-name()='graphicFrame']")!)
                if (frame.SelectSingleNode("*[local-name()='nvGraphicFramePr']/*[local-name()='cNvPr']/@id") is XmlAttribute id) frames.TryAdd(id.Value, frame);
            var paints = new Paints(theme, inheritedText);
            foreach (var table in tables)
                if (table.Source?.Locators.FirstOrDefault(l => l.Kind == "shape_id")?.Value is { } shapeId && frames.TryGetValue(shapeId, out var frame) &&
                    frame.SelectSingleNode(".//*[local-name()='tbl']") is XmlElement tbl)
                    result[table.Id] = Style(tbl, paints, defaultSize, withText);
            return result;
        }

        private SourceDrawingTableStyle Style(XmlElement tbl, Paints paints, double defaultSize, bool withText)
        {
            var properties = Child(tbl, "tblPr");
            bool Flag(string name) => On(properties, name);
            var styleId = Child(properties, "tableStyleId")?.InnerText.Trim();
            var definition = string.IsNullOrEmpty(styleId) ? null : Definition(definitions, styleId) ?? Definition(_builtIn ??= LoadBuiltIn(), styleId);
            var known = string.IsNullOrEmpty(styleId) || definition is not null;
            var rows = Children(tbl, "tr").Select(row => Children(row, "tc").ToArray()).ToArray();
            var grid = Children(Child(tbl, "tblGrid"), "gridCol").ToArray();
            var rowCount = rows.Length; var columnCount = Math.Max(grid.Length, rows.Select(row => row.Length).DefaultIfEmpty().Max());
            bool firstRow = Flag("firstRow"), lastRow = Flag("lastRow"), firstColumn = Flag("firstCol"), lastColumn = Flag("lastCol"),
                bandRows = Flag("bandRow"), bandColumns = Flag("bandCol");
            XmlElement? Part(string name) => Child(definition, name);
            var tableFill = FillOf(Child(definition, "tblBg")) is { } backgroundFill ? paints.Fill(backgroundFill) : null;
            var tableFillPaint = tableFill is not null && FillOf(Child(definition, "tblBg")) is { } backgroundGradient ? paints.Drawn(backgroundGradient) : null;
            // Text that names no color is the theme's text color (tx1), as for a shape that is no placeholder.
            var defaultColor = paints.Scheme("tx1");
            var cells = new SourceDrawingTableCellStyle?[rowCount][];
            for (var r = 0; r < rowCount; r++)
            {
                cells[r] = new SourceDrawingTableCellStyle?[rows[r].Length];
                for (var c = 0; c < rows[r].Length; c++)
                {
                    var cell = rows[r][c];
                    // A cell covered by a merge has no appearance of its own.
                    if (On(cell, "hMerge") || On(cell, "vMerge")) continue;
                    var lastCellColumn = Math.Min(columnCount - 1, c + Span(cell, "gridSpan") - 1); var lastCellRow = Math.Min(rowCount - 1, r + Span(cell, "rowSpan") - 1);
                    var inFirstRow = firstRow && r == 0; var inLastRow = lastRow && lastCellRow == rowCount - 1 && !inFirstRow;
                    var inFirstColumn = firstColumn && c == 0; var inLastColumn = lastColumn && lastCellColumn == columnCount - 1 && !inFirstColumn;
                    // Each part with the region it formats: its outline takes the part's outer edges, the rest its inside edges.
                    var parts = new List<(XmlElement? Part, int Top, int Bottom, int Left, int Right)> { (Part("wholeTbl"), 0, rowCount - 1, 0, columnCount - 1) };
                    if (bandColumns && !inFirstColumn && !inLastColumn)
                        parts.Add((Part((c - (firstColumn ? 1 : 0)) % 2 == 0 ? "band1V" : "band2V"), 0, rowCount - 1, c, lastCellColumn));
                    if (bandRows && !inFirstRow && !inLastRow)
                        parts.Add((Part((r - (firstRow ? 1 : 0)) % 2 == 0 ? "band1H" : "band2H"), r, lastCellRow, 0, columnCount - 1));
                    if (inLastColumn) parts.Add((Part("lastCol"), 0, rowCount - 1, columnCount - 1, columnCount - 1));
                    if (inFirstColumn) parts.Add((Part("firstCol"), 0, rowCount - 1, 0, 0));
                    if (inLastRow) parts.Add((Part("lastRow"), rowCount - 1, rowCount - 1, 0, columnCount - 1));
                    if (inFirstRow) parts.Add((Part("firstRow"), 0, 0, 0, columnCount - 1));
                    if (inLastRow && inLastColumn) parts.Add((Part("seCell"), r, lastCellRow, c, lastCellColumn));
                    if (inLastRow && inFirstColumn) parts.Add((Part("swCell"), r, lastCellRow, c, lastCellColumn));
                    if (inFirstRow && inLastColumn) parts.Add((Part("neCell"), r, lastCellRow, c, lastCellColumn));
                    if (inFirstRow && inFirstColumn) parts.Add((Part("nwCell"), r, lastCellRow, c, lastCellColumn));

                    var fill = tableFill; var color = known ? defaultColor : null; bool? bold = null, italic = null;
                    var fillPaint = tableFillPaint; XmlElement? font = null;
                    SourceDrawingTableBorder? left = null, top = null, right = null, bottom = null, diagonalDown = null, diagonalUp = null;
                    foreach (var (part, regionTop, regionBottom, regionLeft, regionRight) in parts)
                    {
                        if (part is null) continue;
                        var style = Child(part, "tcStyle"); var text = Child(part, "tcTxStyle");
                        if (FillOf(style) is { } partFill && paints.Fill(partFill) is { } partColor) { fill = partColor; fillPaint = paints.Drawn(partFill); }
                        bold = Switch(text, "b") ?? bold; italic = Switch(text, "i") ?? italic;
                        font = Child(text, "font") ?? Child(text, "fontRef") ?? font;
                        if (Children(text).FirstOrDefault(IsColor) is { } textColor) color = paints.Color(textColor) ?? color;
                        var edges = Child(style, "tcBdr");
                        SourceDrawingTableBorder? Edge(string name) => Child(edges, name) is not { } edge ? null :
                            Child(edge, "ln") is { } line ? paints.Line(line) : Child(edge, "lnRef") is { } reference ? paints.LineReference(reference) : null;
                        left = Edge(c == regionLeft ? "left" : "insideV") ?? left; right = Edge(lastCellColumn == regionRight ? "right" : "insideV") ?? right;
                        top = Edge(r == regionTop ? "top" : "insideH") ?? top; bottom = Edge(lastCellRow == regionBottom ? "bottom" : "insideH") ?? bottom;
                        diagonalDown = Edge("tl2br") ?? diagonalDown; diagonalUp = Edge("tr2bl") ?? diagonalUp;
                    }
                    var own = Child(cell, "tcPr");
                    if (Children(own).FirstOrDefault(e => Array.IndexOf(FillNames, e.LocalName) >= 0) is { } ownFill && paints.Fill(ownFill) is { } ownColor)
                    { fill = ownColor; fillPaint = paints.Drawn(ownFill); }
                    left = OwnEdge(own, "lnL", paints) ?? left; right = OwnEdge(own, "lnR", paints) ?? right;
                    top = OwnEdge(own, "lnT", paints) ?? top; bottom = OwnEdge(own, "lnB", paints) ?? bottom;
                    diagonalDown = OwnEdge(own, "lnTlToBr", paints) ?? diagonalDown; diagonalUp = OwnEdge(own, "lnBlToTr", paints) ?? diagonalUp;
                    // The effective formatting of a run: its own properties over what the table style gives the cell.
                    SourceDrawingTableRun Run(string text, XmlElement? properties) => new(text)
                    {
                        FontSize = properties?.GetAttribute("sz") is { Length: > 0 } sz && double.TryParse(sz, NumberStyles.Float, CultureInfo.InvariantCulture, out var hundredths) &&
                            hundredths is >= 100 and <= 400000 ? hundredths / 100 : defaultSize,
                        Bold = Flagged(properties, "b") ?? bold ?? (known ? false : (bool?)null), Italic = Flagged(properties, "i") ?? italic ?? (known ? false : (bool?)null),
                        Underline = properties?.GetAttribute("u") is { Length: > 0 } underline && underline != "none",
                        Strike = properties?.GetAttribute("strike") is { Length: > 0 } strike && strike != "noStrike",
                        Color = Child(properties, "solidFill") is { } runFill && paints.Fill(runFill) is { } runColor && runColor != "none" ? runColor : color,
                        Fonts = known || new[] { "latin", "ea", "cs" }.Any(name => Child(properties, name) is not null) ? paints.Fonts(font, properties) : null,
                    };
                    var paragraphs = Children(Child(cell, "txBody"), "p").Select(paragraph => Children(paragraph).Where(e => e.LocalName is "r" or "fld" or "br")
                        .Select(e => e.LocalName == "br" ? Run("\n", Child(e, "rPr")) : Run(string.Concat(Children(e, "t").Select(t => t.InnerText)), Child(e, "rPr"))).ToArray()).ToArray();
                    var sourceParagraphs = Children(Child(cell, "txBody"), "p").ToArray();
                    var firstParagraph = sourceParagraphs.FirstOrDefault();
                    var textAlign = Alignment(Child(firstParagraph, "pPr")?.GetAttribute("algn"));
                    // A paragraph without an alignment of its own does not take the first paragraph's: it is aligned as its
                    // level is in the cell's a:lstStyle, else in the inherited table text, else left.
                    string? Inherited(XmlElement? paragraphProperties)
                    {
                        var level = "lvl" + (int.TryParse(paragraphProperties?.GetAttribute("lvl"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 8 ? n + 1 : 1) + "pPr";
                        return Alignment(Child(Child(Child(cell, "txBody"), "lstStyle"), level)?.GetAttribute("algn")) ?? paints.InheritedAlignment(level);
                    }
                    static double? Pixels(XmlElement? paragraphProperties, string name) =>
                        double.TryParse(paragraphProperties?.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var emu) && emu is > 0 and <= 51_206_400 ? emu / 9525 : null;
                    var paragraphStyles = sourceParagraphs.Select(paragraph => Child(paragraph, "pPr")).Select(paragraphProperties => new SourceDrawingTableParagraph
                    {
                        TextAlign = Alignment(paragraphProperties?.GetAttribute("algn")) ?? (textAlign is null ? null : Inherited(paragraphProperties) ?? "left"),
                        MarginLeft = Pixels(paragraphProperties, "marL"), Indent = Pixels(paragraphProperties, "indent"),
                    }).ToArray();
                    var styledParagraphs = paragraphStyles.Any(paragraph => paragraph.TextAlign != textAlign || paragraph.MarginLeft is not null || paragraph.Indent is not null);
                    // The cell is formatted as its first run; a cell without one as a run without properties.
                    var first = Run("", Child(Children(Child(cell, "txBody"), "p").SelectMany(p => Children(p)).FirstOrDefault(e => e.LocalName is "r" or "fld"), "rPr"));
                    bool Differs(SourceDrawingTableRun run) => run.FontSize != first.FontSize || run.Bold != first.Bold || run.Italic != first.Italic ||
                        run.Color != first.Color || !(run.Fonts ?? []).SequenceEqual(first.Fonts ?? [], StringComparer.Ordinal);
                    cells[r][c] = new SourceDrawingTableCellStyle
                    {
                        Fill = fill ?? (known ? "none" : null), Color = first.Color, FontSize = first.FontSize, Bold = first.Bold, Italic = first.Italic, Fonts = first.Fonts,
                        FillPaint = fill is not null && fill != "none" ? fillPaint : null,
                        TextAlign = textAlign, ParagraphStyles = styledParagraphs ? paragraphStyles : null,
                        VerticalAlign = own?.GetAttribute("anchor") switch { "t" => "top", "ctr" => "middle", "b" => "bottom", _ => known ? "top" : null },
                        Left = left ?? (known ? NoEdge : null), Top = top ?? (known ? NoEdge : null),
                        Right = right ?? (known ? NoEdge : null), Bottom = bottom ?? (known ? NoEdge : null),
                        DiagonalDown = diagonalDown is { Width: > 0 } ? diagonalDown : null, DiagonalUp = diagonalUp is { Width: > 0 } ? diagonalUp : null,
                        Paragraphs = withText || styledParagraphs || paragraphs.Any(paragraph => paragraph.Any(run => run.Text != "\n" && Differs(run))) ? paragraphs : null,
                        RowSpan = lastCellRow - r + 1, ColumnSpan = lastCellColumn - c + 1,
                    };
                }
            }
            return new SourceDrawingTableStyle(cells)
            {
                ColumnWidths = Sizes(grid, "w"), RowHeights = Sizes(Children(tbl, "tr").ToArray(), "h"),
            };
        }

        private static double[] Sizes(XmlElement[] elements, string name)
        {
            var sizes = elements.Select(e => double.TryParse(e.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var emu) &&
                double.IsFinite(emu) && emu is > 0 and <= 952_500_000_000 ? emu / 9525 : double.NaN).ToArray();
            return sizes.Any(double.IsNaN) ? [] : sizes;
        }

        private static SourceDrawingTableBorder? OwnEdge(XmlElement? properties, string name, Paints paints) =>
            Child(properties, name) is { } line ? paints.Line(line) : null;

        private static XmlElement? FillOf(XmlElement? style) =>
            Children(Child(style, "fill")).FirstOrDefault(e => Array.IndexOf(FillNames, e.LocalName) >= 0) ?? Child(style, "fillRef");

        private static XmlElement? Definition(XmlDocument? styles, string id) => Children(styles?.DocumentElement, "tblStyle")
            .FirstOrDefault(style => string.Equals(style.GetAttribute("styleId"), id, StringComparison.OrdinalIgnoreCase));

        private static XmlDocument LoadBuiltIn()
        {
            var document = new XmlDocument { XmlResolver = null };
            document.LoadXml(BuiltIn);
            return document;
        }

        private static string? Alignment(string? value) => value switch { "l" => "left", "ctr" => "center", "r" => "right", "just" or "dist" => "justify", _ => null };
        private static bool IsColor(XmlElement element) => element.LocalName is "srgbClr" or "schemeClr" or "scrgbClr" or "sysClr" or "hslClr" or "prstClr";
        private static int Span(XmlElement cell, string name) => int.TryParse(cell.GetAttribute(name), NumberStyles.None, CultureInfo.InvariantCulture, out var span) && span > 1 ? span : 1;
        private static bool On(XmlElement? element, string name) => element?.GetAttribute(name) is "1" or "true";
        private static bool? Flagged(XmlElement? element, string name) => element?.GetAttribute(name) switch { "1" or "true" => true, "0" or "false" => false, _ => null };
        private static bool? Switch(XmlElement? element, string name) => element?.GetAttribute(name) switch { "on" => true, "off" => false, _ => null };
        private static IEnumerable<XmlElement> Children(XmlElement? parent, string? name = null) =>
            parent?.ChildNodes.OfType<XmlElement>().Where(e => name is null || e.LocalName == name) ?? [];
        private static XmlElement? Child(XmlElement? parent, string name) => Children(parent, name).FirstOrDefault();

        /// <summary>Resolves fills, colors and lines of table XML through the shape style reader, against the slide's theme and color map.</summary>
        private sealed class Paints(XmlDocument? theme, XmlElement? inheritedText)
        {
            private readonly Dictionary<string, DrawingAppearance> _read = new(StringComparer.Ordinal);
            private readonly Dictionary<string, string[]> _fonts = new(StringComparer.Ordinal);

            /// <summary>The fill as a paint when it is a gradient with at least two stops or a preset pattern; null for any other fill.</summary>
            public DrawingPaint? Drawn(XmlElement fill) => fill.LocalName is "gradFill" or "pattFill" or "fillRef" &&
                Read(fill.LocalName == "fillRef" ? "style" : "spPr", fill, fill.LocalName).FillPaint is var paint &&
                paint is { Kind: "linear" or "radial", Stops.Count: >= 2 } or { Kind: "pattern" } ? paint : null;

            /// <summary>The alignment the inherited table text gives a paragraph of that level (lvl1pPr ...), or null.</summary>
            public string? InheritedAlignment(string level) => Alignment(Child(inheritedText, level)?.GetAttribute("algn"));

            /// <summary>
            /// The typefaces of Latin, East Asian and complex-script text, without repeats: a:latin, a:ea and a:cs of
            /// <paramref name="run"/>, else of the table style's a:font or the theme font its a:fontRef names, else of the
            /// inherited table text, else the theme's minor font. The shape style reader resolves theme references (+mn-lt).
            /// </summary>
            public string[] Fonts(XmlElement? styleFont, XmlElement? run)
            {
                var prefix = styleFont?.LocalName != "fontRef" ? null : styleFont.GetAttribute("idx") is "major" ? "+mj-" : "+mn-";
                string? Typeface(string script) => Child(run, script)?.GetAttribute("typeface") is { Length: > 0 } own ? own :
                    prefix is not null ? prefix + (script == "latin" ? "lt" : script) :
                    Child(styleFont, script)?.GetAttribute("typeface") is { Length: > 0 } styled ? styled : null;
                var faces = new[] { Typeface("latin"), Typeface("ea"), Typeface("cs") };
                string? Language(string name) => run?.GetAttribute(name) is { Length: > 0 and <= 32 } language ? language : null;
                var key = string.Join('\n', faces.Append(Language("lang")).Append(Language("altLang")));
                if (_fonts.TryGetValue(key, out var cached)) return cached;
                var document = new XmlDocument { XmlResolver = null };
                var shape = document.CreateElement("sp"); document.AppendChild(shape);
                var body = document.CreateElement("txBody"); shape.AppendChild(body);
                var paragraph = document.CreateElement("p"); body.AppendChild(paragraph);
                // One character of each script: the reader names the typeface each one is set in.
                foreach (var sample in new[] { "A", "\u3042", "\u05D0" })
                {
                    var element = document.CreateElement("r"); paragraph.AppendChild(element);
                    var properties = document.CreateElement("rPr"); element.AppendChild(properties);
                    foreach (var name in new[] { "lang", "altLang" })
                        if (Language(name) is { } language) properties.SetAttribute(name, language);
                    foreach (var (script, face) in new[] { "latin", "ea", "cs" }.Zip(faces))
                        if (face is not null) ((XmlElement)properties.AppendChild(document.CreateElement(script))!).SetAttribute("typeface", face);
                    ((XmlElement)element.AppendChild(document.CreateElement("t"))!).InnerText = sample;
                }
                return _fonts[key] = DrawingMlStyleReader.Read(shape, theme, inheritedListStyle: inheritedText).Paragraphs.SelectMany(p => p.Runs)
                    .Select(r => r.FontFamily).Where(family => family is { Length: > 0 and <= 128 }).Distinct(StringComparer.Ordinal).ToArray();
            }

            /// <summary>#RRGGBB (the first color of a gradient, the background color of a pattern), "none" for a:noFill, or null for a fill that resolves to no color.</summary>
            public string? Fill(XmlElement fill)
            {
                if (fill.LocalName == "noFill") return "none";
                var paint = Read(fill.LocalName == "fillRef" ? "style" : "spPr", fill, fill.LocalName).FillPaint;
                return paint.Kind == "pattern" ? paint.BackColor ?? paint.Color : Hex(paint);
            }

            public string? Color(XmlElement color)
            {
                var document = new XmlDocument { XmlResolver = null };
                var fill = document.CreateElement("solidFill"); document.AppendChild(fill);
                fill.AppendChild(document.ImportNode(color, true));
                return Hex(Read("spPr", fill, "solidFill").FillPaint);
            }

            public string? Scheme(string name)
            {
                var document = new XmlDocument { XmlResolver = null };
                var color = document.CreateElement("schemeClr"); document.AppendChild(color);
                color.SetAttribute("val", name);
                return Color(color);
            }

            /// <summary>An a:ln-shaped element (a:ln, a:lnL ...). Null when it names no fill: the edge is then not set here.</summary>
            public SourceDrawingTableBorder? Line(XmlElement line)
            {
                if (!Children(line).Any(e => Array.IndexOf(FillNames, e.LocalName) >= 0)) return null;
                var appearance = Read("spPr", line, "ln");
                return Hex(appearance.StrokePaint) is { } color && appearance.StrokeWidth > 0
                    ? new(Math.Min(appearance.StrokeWidth, 1024), color) { Style = LineStyle(line, appearance) } : NoEdge;
            }

            /// <summary>A compound line (a:ln cmpd) is drawn double, a preset dash dotted or dashed.</summary>
            private static string? LineStyle(XmlElement line, DrawingAppearance appearance) =>
                line.GetAttribute("cmpd") is "dbl" or "thickThin" or "thinThick" or "tri" ? "double" : appearance.Dash switch
                {
                    null or "" or "solid" => null, "dot" or "sysDot" => "dotted", _ => "dashed",
                };

            public SourceDrawingTableBorder? LineReference(XmlElement reference)
            {
                var appearance = Read("style", reference, "lnRef");
                return Hex(appearance.StrokePaint) is { } color && appearance.StrokeWidth > 0 ? new(Math.Min(appearance.StrokeWidth, 1024), color) : null;
            }

            private static string? Hex(DrawingPaint paint) => paint.Kind == "solid" ? paint.Color : paint.Stops.FirstOrDefault()?.Color;

            /// <summary>Reads <paramref name="source"/> as the child <paramref name="name"/> of a shape's spPr or style.</summary>
            private DrawingAppearance Read(string parent, XmlElement source, string name)
            {
                var key = parent + "/" + name + ":" + source.OuterXml;
                if (_read.TryGetValue(key, out var cached)) return cached;
                var document = new XmlDocument { XmlResolver = null };
                var shape = document.CreateElement("sp"); document.AppendChild(shape);
                var copy = document.CreateElement(name);
                foreach (XmlAttribute attribute in source.Attributes)
                    if (attribute.NamespaceURI.Length == 0) copy.SetAttribute(attribute.LocalName, attribute.Value);
                foreach (XmlNode child in source.ChildNodes) copy.AppendChild(document.ImportNode(child, true));
                var holder = document.CreateElement(parent); shape.AppendChild(holder);
                holder.AppendChild(copy);
                return _read[key] = DrawingMlStyleReader.Read(shape, theme);
            }
        }
    }
}
