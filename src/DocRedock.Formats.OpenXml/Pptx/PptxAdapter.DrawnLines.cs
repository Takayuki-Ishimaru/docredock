using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Common;

namespace DocRedock.Formats.OpenXml.Pptx;

// Lines drawn over slide text. People cancel text on a slide by drawing a straight line through it (often red, sometimes
// double) and emphasize text by drawing a line under it. Such a line is text formatting, not a drawing: it is read as a
// strikethrough or underline of the text it crosses and leaves the slide's drawings, so it does not also become a table
// overlay symbol, a diagram edge or an unresolved line to review.
//
// A drawing has no notion of the text under it, so the text is placed from what the slide gives: the frame of its shape
// or table cell, the insets, the anchor, the alignment, the font sizes, the spacing and an estimate of the text's width.
// The shape rules and the vertical zones relative to the baseline are those of PdfTextDecorations (DrawnLineRules), so a
// slide and its PDF agree.
public sealed partial class PptxAdapter
{
    private const double DrawnEmuPerPoint = 12700d;
    // The size of text that nothing else sizes.
    private const double DrawnDefaultFontSize = 18;
    // PowerPoint's single line spacing is 1.2 em whatever the font (its object model reports 14.4, 21.6 and 28.8 pt for 12,
    // 18 and 24 pt text in Calibri, Arial, Meiryo, Yu Gothic and MS Gothic alike). The first baseline lies 0.95 em (from the
    // metrics of Calibri) to 1.0 em (LibreOffice's rendering of Carlito) under the top of its line; the rest of the line,
    // 0.23 em here, is below it. A spacing other than single adds to the line above the text, or takes from there.
    private const double DrawnLineEm = 1.2;
    private const double DrawnBelowBaselineEm = 0.23;
    // PowerPoint's default insets of a text frame and margins of a table cell, in points.
    private const double DrawnInsetX = 7.2;
    private const double DrawnInsetY = 3.6;
    // A line turned by up to this many degrees off the horizontal is placed by its own end points.
    private const double DrawnMaxTurn = 10;
    // A bar turned a half turn is still a bar; one turned any other way is not placed.
    private const double DrawnMaxBarTurn = 1;

    private static readonly XNamespace DrawnA = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace DrawnP = "http://schemas.openxmlformats.org/presentationml/2006/main";

    /// <summary>What the drawn lines decorate in one shape's text or one table cell's text: the ranges of characters
    /// ([start, end) offsets into that text) they strike or underline, and whether one strikes part of it so that which
    /// characters it strikes could not be determined. The text is then left unmarked and needs source review.</summary>
    private sealed class DrawnTextDecoration
    {
        public List<(int Start, int End)> Struck { get; } = [];
        public List<(int Start, int End)> Underlined { get; } = [];
        public bool StrikeUnplaced { get; set; }
    }

    /// <summary>What the drawn lines of a slide decorate: the text of its shapes and the cells of its tables they strike
    /// or underline (or strike in part), and the shapes that were taken as that formatting.</summary>
    private sealed class DrawnLineDecorations
    {
        public static readonly DrawnLineDecorations None = new();

        public IReadOnlyDictionary<PptxShapeRecord, DrawnTextDecoration> Shapes { get; init; } =
            new Dictionary<PptxShapeRecord, DrawnTextDecoration>(ReferenceEqualityComparer.Instance);
        public IReadOnlyDictionary<PptxShapeRecord, Dictionary<(int Row, int Cell), DrawnTextDecoration>> Cells { get; init; } =
            new Dictionary<PptxShapeRecord, Dictionary<(int Row, int Cell), DrawnTextDecoration>>(ReferenceEqualityComparer.Instance);
        public IReadOnlySet<PptxShapeRecord> Consumed { get; init; } = new HashSet<PptxShapeRecord>(ReferenceEqualityComparer.Instance);
        /// <summary>The lines that were taken as formatting, by the shape whose text each of them lies over.</summary>
        public IReadOnlyDictionary<PptxShapeRecord, IReadOnlyList<PptxShapeRecord>> LinesOver { get; init; } =
            new Dictionary<PptxShapeRecord, IReadOnlyList<PptxShapeRecord>>(ReferenceEqualityComparer.Instance);
    }

    // The parts of a presentation the placement reads more than once, loaded when the first slide with a line needs them.
    private sealed class DrawnPartCache
    {
        private readonly Dictionary<string, XDocument?> documents = new(StringComparer.Ordinal);

        public XDocument? Load(Dictionary<string, byte[]> package, string? part)
        {
            if (part is null) return null;
            if (documents.TryGetValue(part, out var cached)) return cached;
            XDocument? document = null;
            if (package.TryGetValue(part, out var bytes))
            {
                try
                {
                    using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
                    document = XDocument.Load(reader);
                }
                catch (XmlException) { }
            }
            return documents[part] = document;
        }
    }

    /// <summary>What sets a paragraph's text: its size, alignment, margin and indent, line spacing, the space around it and
    /// whether it has a bullet. Each is found in the paragraph, then the shape's list style, the layout and master it
    /// stands on, and the presentation's defaults; a value that is not found is null.</summary>
    private sealed record DrawnParagraphStyle(double? Size, string? Align, double? MarginLeft, double? Indent, double? SpacingPercent,
        double? SpacingPoints, double? BeforePoints, double? BeforePercent, double? AfterPoints, double? AfterPercent, bool? Bullet)
    {
        public static readonly DrawnParagraphStyle Empty = new(null, null, null, null, null, null, null, null, null, null, null);

        /// <summary>This style, with what it does not say taken from <paramref name="other"/>.</summary>
        public DrawnParagraphStyle Over(DrawnParagraphStyle other) => new(Size ?? other.Size, Align ?? other.Align,
            MarginLeft ?? other.MarginLeft, Indent ?? other.Indent,
            SpacingPercent ?? (SpacingPoints is null ? other.SpacingPercent : null), SpacingPoints ?? (SpacingPercent is null ? other.SpacingPoints : null),
            BeforePoints ?? (BeforePercent is null ? other.BeforePoints : null), BeforePercent ?? (BeforePoints is null ? other.BeforePercent : null),
            AfterPoints ?? (AfterPercent is null ? other.AfterPoints : null), AfterPercent ?? (AfterPoints is null ? other.AfterPercent : null),
            Bullet ?? other.Bullet);
    }

    private sealed record DrawnRun(string Text, double Size, bool Strike);

    private sealed record DrawnParagraph(IReadOnlyList<DrawnRun> Runs, double EndSize, DrawnParagraphStyle Style)
    {
        public string Text => string.Concat(Runs.Select(run => run.Text));
    }

    /// <summary>The frame a text is set in, in points on the slide: the box, the insets, the anchor, whether the text
    /// wraps, how much of the line spacing an autofit took away, and the paragraphs. The text the node holds begins at
    /// paragraph <paramref name="HeldFrom"/> (and, for a cell, ends before its empty paragraphs at the edges), and lies on a
    /// fill when <paramref name="Painted"/>.</summary>
    private sealed record DrawnFrame(double Left, double Top, double Width, double Height, double LeftInset, double TopInset,
        double RightInset, double BottomInset, string Anchor, bool Wrap, double SpacingReduction, IReadOnlyList<DrawnParagraph> Paragraphs,
        bool TrimEmptyEdges, int HeldFrom = 0, bool Painted = false);

    /// <summary>One line of text (a paragraph, or the part of one between two line breaks): where it lies, and which
    /// characters of the shape's text or the cell's text it is.</summary>
    private sealed class DrawnUnit(PptxShapeRecord shape, (int Row, int Cell)? cell, int start, int end, DrawnTextExtent extent, bool isStruck, bool isPainted)
    {
        public PptxShapeRecord Shape { get; } = shape;
        public (int Row, int Cell)? Cell { get; } = cell;
        public int Start { get; } = start;
        public int End { get; } = end;
        public DrawnTextExtent Extent { get; } = extent;
        /// <summary>The line is already struck through, all of it, by the formatting of its runs.</summary>
        public bool IsStruck { get; } = isStruck;
        /// <summary>The text lies on a fill of its shape or table cell: a line drawn before (under) that shape or table is out of
        /// sight there, and is no decoration of this text.</summary>
        public bool IsPainted { get; } = isPainted;
    }

    // ------------------------------------------------------------------------------------------- the lines of a slide

    private static bool HasDrawnArrowhead(string? arrow) => !string.IsNullOrWhiteSpace(arrow) && !arrow.Equals("none", StringComparison.OrdinalIgnoreCase);

    private static bool IsDrawnLinePreset(string? preset) =>
        preset is not null && (preset.Equals("line", StringComparison.OrdinalIgnoreCase) || preset.Equals("straightConnector1", StringComparison.OrdinalIgnoreCase));

    // Cheap, before any XML is read again: could this shape be a line or a thin bar that decorates text?
    private static bool MaybeDrawnLine(PptxShapeRecord shape)
    {
        // A line that carries text is a label, and a line glued to a shape or with an arrowhead joins or points at something.
        if (shape.InheritedFrom is not null || shape.IsHidden || shape.IsHiddenByGroup || shape.IsTable || shape.Geometry is null ||
            shape.ConnectorStartId is not null || shape.ConnectorEndId is not null || !string.IsNullOrWhiteSpace(shape.Text)) return false;
        if (HasDrawnArrowhead(shape.ConnectorHeadArrow) || HasDrawnArrowhead(shape.ConnectorTailArrow)) return false;
        if (IsDrawnLinePreset(shape.ShapePreset)) return true;
        // A thin bar: a rectangle with no text, a few points high.
        return string.Equals(shape.ShapeType, "shape", StringComparison.Ordinal) && !shape.IsTextBox &&
               shape.ImageRelationshipIds.Count == 0 && shape.ChartRelationshipIds is not { Count: > 0 } && shape.DiagramRelationshipIds is not { Count: > 0 } &&
               (shape.ShapePreset is null || shape.ShapePreset.Equals("rect", StringComparison.OrdinalIgnoreCase)) &&
               shape.Geometry.Height <= 16 * DrawnEmuPerPoint && shape.Geometry.Width >= 4 * shape.Geometry.Height;
    }

    /// <summary>The slide's shapes, by element, in the order <see cref="ReadShapes"/> found them (its own shapes come first in
    /// <see cref="PptxSlideRecord.Shapes"/>), or null when the two do not line up.</summary>
    private static List<XElement>? DrawnShapeElements(XDocument slide, IReadOnlyList<PptxShapeRecord> own)
    {
        var elements = new List<XElement>();
        void Collect(XElement parent)
        {
            foreach (var child in parent.Elements())
            {
                if (child.Name.LocalName is "sp" or "graphicFrame" or "pic" or "cxnSp") elements.Add(child);
                else Collect(child);
            }
        }
        if (slide.Root is not { } root) return null;
        Collect(root);
        if (elements.Count != own.Count) return null;
        for (var index = 0; index < elements.Count; index++)
        {
            var id = elements[index].Descendants().FirstOrDefault(element => element.Name.LocalName == "cNvPr")?.Attribute("id")?.Value ?? "";
            if (!string.Equals(id, own[index].ShapeId, StringComparison.Ordinal)) return null;
        }
        return elements;
    }

    private static double? DrawnNumber(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;

    private static double? DrawnPoints(string? emu) => DrawnNumber(emu) / DrawnEmuPerPoint;

    // Whether the shape's outline paints: a:ln with a fill of its own, or the style's line reference when it says nothing.
    private static bool HasDrawnOutline(XElement shape)
    {
        var outline = shape.Element(DrawnP + "spPr")?.Element(DrawnA + "ln");
        if (outline?.Element(DrawnA + "noFill") is not null) return false;
        if (outline is not null && (outline.Element(DrawnA + "solidFill") ?? outline.Element(DrawnA + "gradFill") ?? outline.Element(DrawnA + "pattFill")) is not null) return true;
        return (DrawnNumber(shape.Element(DrawnP + "style")?.Element(DrawnA + "lnRef")?.Attribute("idx")?.Value) ?? 0) > 0;
    }

    private static bool IsDrawnFillElement(XElement element) =>
        element.Name == DrawnA + "solidFill" || element.Name == DrawnA + "gradFill" || element.Name == DrawnA + "pattFill" ||
        element.Name == DrawnA + "blipFill" || element.Name == DrawnA + "grpFill";

    // What the fill among the children of a holder (a:spPr, a:tcPr, a:tblPr) says: it paints, it does not, or the holder says nothing.
    private static bool? DrawnFillOf(XElement? holder) =>
        holder is null ? null : holder.Element(DrawnA + "noFill") is not null ? false : holder.Elements().Any(IsDrawnFillElement) ? true : null;

    // The same for a shape, whose style may carry a fill reference.
    private static bool? DrawnFillOfShape(XElement? shape) =>
        DrawnFillOf(shape?.Element(DrawnP + "spPr")) ??
        ((DrawnNumber(shape?.Element(DrawnP + "style")?.Element(DrawnA + "fillRef")?.Attribute("idx")?.Value) ?? 0) > 0 ? true : null);

    private static bool HasDrawnFill(XElement shape) => DrawnFillOfShape(shape) == true;

    // A frame, picture or table paints; a shape paints when it has an outline or a fill. A line has no area to fill.
    private static bool IsDrawnShapePainted(PptxShapeRecord shape, XElement element) =>
        element.Name.LocalName is "graphicFrame" or "pic" || HasDrawnOutline(element) || !IsDrawnLinePreset(shape.ShapePreset) && HasDrawnFill(element);

    // Whether the text of a shape lies on a fill: its own, else (for a placeholder) the one it takes from its layout or master.
    private static bool IsDrawnTextPainted(XElement element, DrawnInheritance inheritance) =>
        (DrawnFillOfShape(element) ?? DrawnFillOfShape(inheritance.LayoutShape) ?? DrawnFillOfShape(inheritance.MasterShape)) == true;

    // The two table styles of PowerPoint that paint nothing: "No Style, No Grid" and "No Style, Table Grid".
    private static readonly string[] DrawnUnpaintedTableStyles = ["{2D5ABB26-0587-4C30-8999-92F81FD0307C}", "{5940675A-B579-460E-94D1-54222C63F5DA}"];

    // Whether a table paints a background under its cells where they say nothing of their own: a fill of its own, or a style that fills some
    // part of a table. A style the file does not define is one of PowerPoint's own (a file need not carry those), and all of them but the
    // two without a style fill. Taking a table for painted when only a part of it is keeps a line beneath it a line, as before.
    private static bool IsDrawnTablePainted(XElement? tableProperties, XDocument? styleList)
    {
        if (DrawnFillOf(tableProperties) == true) return true;
        var id = tableProperties?.Element(DrawnA + "tableStyleId")?.Value.Trim();
        if (string.IsNullOrEmpty(id)) return false;
        var style = styleList?.Descendants(DrawnA + "tblStyle").FirstOrDefault(item => string.Equals(item.Attribute("styleId")?.Value, id, StringComparison.OrdinalIgnoreCase));
        if (style is null) return !DrawnUnpaintedTableStyles.Contains(id, StringComparer.OrdinalIgnoreCase);
        return style.Descendants().Any(item => item.Name == DrawnA + "fill" && item.Elements().Any(fill => fill.Name != DrawnA + "noFill") ||
                                              item.Name == DrawnA + "fillRef" && (DrawnNumber(item.Attribute("idx")?.Value) ?? 0) > 0);
    }

    // A straight line without an arrowhead, or a thin bar with no text, that is free (glued to no shape), visible and
    // placed reliably. Whether it is horizontal enough, and thin enough, is judged against the text it crosses.
    private static DrawnStroke? AsDrawnStroke(PptxShapeRecord shape, XElement element)
    {
        if (!MaybeDrawnLine(shape) || shape.Geometry is not { } geometry) return null;
        var outline = element.Element(DrawnP + "spPr")?.Element(DrawnA + "ln");
        if (IsDrawnLinePreset(shape.ShapePreset))
        {
            if (!HasDrawnOutline(element)) return null;
            double x1, y1, x2, y2;
            if (shape.ConnectorPathPoints is { Count: >= 2 } path) (x1, y1, x2, y2) = (path[0].X, path[0].Y, path[^1].X, path[^1].Y);
            else
            {
                // A line written as a plain shape: its frame's corners, mirrored as the shape is flipped. The turn of a
                // shape that is flipped across reads 180 degrees more.
                var turn = Math.IEEERemainder(geometry.RotationDegrees - (shape.FlipH ? 180 : 0), 360);
                if (Math.Abs(turn) > DrawnMaxTurn) return null;
                (x1, y1, x2, y2) = shape.FlipH ^ shape.FlipV
                    ? (geometry.X, geometry.Y + geometry.Height, geometry.X + geometry.Width, geometry.Y)
                    : (geometry.X, geometry.Y, geometry.X + geometry.Width, geometry.Y + geometry.Height);
            }
            var weight = DrawnPoints(outline?.Attribute("w")?.Value) ?? 1;
            (x1, y1, x2, y2) = (x1 / DrawnEmuPerPoint, y1 / DrawnEmuPerPoint, x2 / DrawnEmuPerPoint, y2 / DrawnEmuPerPoint);
            return x1 <= x2 ? new DrawnStroke(x1, y1, x2, y2, weight, false) : new DrawnStroke(x2, y2, x1, y1, weight, false);
        }
        // A thin rectangle: it has to paint something.
        if (element.Name.LocalName != "sp" || !HasDrawnFill(element) && !HasDrawnOutline(element)) return null;
        if (Math.Abs(Math.IEEERemainder(geometry.RotationDegrees, 180)) > DrawnMaxBarTurn) return null;
        var (left, top, width, height) = (geometry.X / DrawnEmuPerPoint, geometry.Y / DrawnEmuPerPoint, geometry.Width / DrawnEmuPerPoint, geometry.Height / DrawnEmuPerPoint);
        if (width <= 0 || height < 0 || width < 4 * height) return null;
        return new DrawnStroke(left, top + height / 2, left + width, top + height / 2, height, true);
    }

    // ----------------------------------------------------------------------------------- styles, from the slide to the defaults

    private static DrawnParagraphStyle ReadDrawnParagraphStyle(XElement? properties)
    {
        if (properties is null) return DrawnParagraphStyle.Empty;
        var spacing = properties.Element(DrawnA + "lnSpc");
        var before = properties.Element(DrawnA + "spcBef");
        var after = properties.Element(DrawnA + "spcAft");
        double? Percent(XElement? holder) => DrawnNumber(holder?.Element(DrawnA + "spcPct")?.Attribute("val")?.Value) / 100000;
        double? Points(XElement? holder) => DrawnNumber(holder?.Element(DrawnA + "spcPts")?.Attribute("val")?.Value) / 100;
        bool? bullet = properties.Element(DrawnA + "buNone") is not null ? false
            : properties.Element(DrawnA + "buChar") is not null || properties.Element(DrawnA + "buAutoNum") is not null || properties.Element(DrawnA + "buBlip") is not null ? true
            : null;
        return new DrawnParagraphStyle(DrawnNumber(properties.Element(DrawnA + "defRPr")?.Attribute("sz")?.Value) / 100,
            properties.Attribute("algn")?.Value, DrawnPoints(properties.Attribute("marL")?.Value), DrawnPoints(properties.Attribute("indent")?.Value),
            Percent(spacing), Points(spacing), Points(before), Percent(before), Points(after), Percent(after), bullet);
    }

    // The style a list style (a:lstStyle, p:titleStyle, p:defaultTextStyle, ...) gives paragraphs of one level.
    private static DrawnParagraphStyle ReadDrawnLevelStyle(XElement? holder, int level) =>
        holder is null ? DrawnParagraphStyle.Empty
            : ReadDrawnParagraphStyle(holder.Element(DrawnA + $"lvl{level + 1}pPr")).Over(ReadDrawnParagraphStyle(holder.Element(DrawnA + "defPPr")));

    private static XElement? DrawnPlaceholder(XElement shape) =>
        shape.Element(DrawnP + "nvSpPr")?.Element(DrawnP + "nvPr")?.Element(DrawnP + "ph");

    // The category a placeholder's type gives its text: a title, a date, footer or slide number, or the body.
    private static string DrawnPlaceholderCategory(string? type) => (type ?? "body").ToLowerInvariant() switch
    {
        "title" or "ctrtitle" => "title",
        "dt" or "ftr" or "sldnum" => "other",
        _ => "body",
    };

    // The shape on a layout or master that a slide's placeholder stands on: the same index when both have one, else the
    // same kind of placeholder.
    private static XElement? DrawnFindPlaceholder(XDocument? part, string? type, string? index)
    {
        if (part is null) return null;
        XElement? ofKind = null;
        foreach (var shape in part.Descendants(DrawnP + "sp"))
        {
            if (DrawnPlaceholder(shape) is not { } candidate) continue;
            if (index is not null && string.Equals(candidate.Attribute("idx")?.Value, index, StringComparison.Ordinal)) return shape;
            if (ofKind is null && DrawnPlaceholderCategory(candidate.Attribute("type")?.Value) == DrawnPlaceholderCategory(type)) ofKind = shape;
        }
        return ofKind;
    }

    /// <summary>Everything a shape of the slide inherits: the layout and master placeholders it stands on and the list
    /// styles above them.</summary>
    private sealed record DrawnInheritance(XElement? LayoutShape, XElement? MasterShape, XElement? MasterStyle, XElement? DefaultStyle);

    private static DrawnInheritance DrawnInherit(Dictionary<string, byte[]> package, PptxSlideRecord slide, DrawnPartCache cache, XElement shape)
    {
        var (layoutPart, masterPart) = ResolveLayoutAndMaster(package, slide.PartUri);
        var layout = cache.Load(package, layoutPart);
        var master = cache.Load(package, masterPart);
        var presentation = cache.Load(package, "ppt/presentation.xml");
        var placeholder = DrawnPlaceholder(shape);
        var type = placeholder?.Attribute("type")?.Value;
        var index = placeholder?.Attribute("idx")?.Value;
        var layoutShape = placeholder is null ? null : DrawnFindPlaceholder(layout, type, index);
        // A layout's placeholder stands on the master's of the same kind.
        var masterShape = placeholder is null ? null : DrawnFindPlaceholder(master, layoutShape is null ? type : DrawnPlaceholder(layoutShape)?.Attribute("type")?.Value ?? type, null);
        var category = placeholder is null ? "other" : DrawnPlaceholderCategory(type);
        var styles = master?.Descendants(DrawnP + "txStyles").FirstOrDefault();
        var masterStyle = styles?.Element(DrawnP + (category == "title" ? "titleStyle" : category == "body" ? "bodyStyle" : "otherStyle"));
        return new DrawnInheritance(layoutShape, masterShape, masterStyle, presentation?.Descendants(DrawnP + "defaultTextStyle").FirstOrDefault());
    }

    // A frame inherited from a layout or master placeholder: its position and size, in EMU.
    private static (double X, double Y, double Width, double Height)? DrawnInheritedFrame(DrawnInheritance inheritance)
    {
        foreach (var shape in new[] { inheritance.LayoutShape, inheritance.MasterShape })
        {
            var transform = shape?.Element(DrawnP + "spPr")?.Element(DrawnA + "xfrm");
            var offset = transform?.Element(DrawnA + "off");
            var extent = transform?.Element(DrawnA + "ext");
            if (DrawnNumber(offset?.Attribute("x")?.Value) is { } x && DrawnNumber(offset?.Attribute("y")?.Value) is { } y &&
                DrawnNumber(extent?.Attribute("cx")?.Value) is { } width && DrawnNumber(extent?.Attribute("cy")?.Value) is { } height &&
                (DrawnNumber(transform?.Attribute("rot")?.Value) ?? 0) == 0)
                return (x, y, width, height);
        }
        return null;
    }

    // What a text body sets for its frame: the own body properties first, then the layout's and the master's.
    private static DrawnFrame? ReadDrawnFrame(XElement? body, DrawnInheritance inheritance, double left, double top, double width, double height,
        Func<double, IReadOnlyList<DrawnParagraph>> readParagraphs)
    {
        var bodies = new[] { body, inheritance.LayoutShape?.Element(DrawnP + "txBody")?.Element(DrawnA + "bodyPr"),
            inheritance.MasterShape?.Element(DrawnP + "txBody")?.Element(DrawnA + "bodyPr") };
        string? Attribute(string name) => bodies.Select(properties => properties?.Attribute(name)?.Value).FirstOrDefault(value => value is not null);
        // Text that is vertical, turned in its frame, in columns or centered as a block is not placed.
        if (Attribute("vert") is { } vertical && !vertical.Equals("horz", StringComparison.OrdinalIgnoreCase)) return null;
        if ((DrawnNumber(Attribute("rot")) ?? 0) != 0 || (DrawnNumber(Attribute("numCol")) ?? 1) > 1 || Attribute("anchorCtr") is "1" or "true") return null;
        double Inset(string name, double fallback) => DrawnPoints(Attribute(name)) ?? fallback;
        var fit = bodies.Select(properties => properties?.Element(DrawnA + "normAutofit")).FirstOrDefault(element => element is not null);
        var scale = DrawnNumber(fit?.Attribute("fontScale")?.Value) / 100000 ?? 1;
        var reduction = DrawnNumber(fit?.Attribute("lnSpcReduction")?.Value) / 100000 ?? 0;
        var anchor = Attribute("anchor") is "ctr" or "b" ? Attribute("anchor")! : "t";
        var wrap = !string.Equals(Attribute("wrap"), "none", StringComparison.OrdinalIgnoreCase);
        return new DrawnFrame(left, top, width, height, Inset("lIns", DrawnInsetX), Inset("tIns", DrawnInsetY), Inset("rIns", DrawnInsetX),
            Inset("bIns", DrawnInsetY), anchor, wrap, reduction, readParagraphs(scale), TrimEmptyEdges: false);
    }

    // The paragraphs of a text body, with the sizes and styles they inherit. The text of a paragraph is what ReadShapes
    // reads: every a:t, and a line break for every a:br.
    private static List<DrawnParagraph> ReadDrawnParagraphs(XElement body, IReadOnlyList<XElement?> styleHolders, double scale)
    {
        var result = new List<DrawnParagraph>();
        foreach (var paragraph in body.Elements(DrawnA + "p"))
        {
            var properties = paragraph.Element(DrawnA + "pPr");
            var level = Math.Clamp((int)(DrawnNumber(properties?.Attribute("lvl")?.Value) ?? 0), 0, 8);
            var style = ReadDrawnParagraphStyle(properties);
            foreach (var holder in styleHolders) style = style.Over(ReadDrawnLevelStyle(holder, level));
            double SizeOf(XElement? runProperties) => (DrawnNumber(runProperties?.Attribute("sz")?.Value) / 100 ?? style.Size ?? DrawnDefaultFontSize) * scale;
            var runs = new List<DrawnRun>();
            foreach (var element in paragraph.Descendants().Where(element => element.Name.LocalName is "t" or "br"))
            {
                var runProperties = element.Name.LocalName == "br" ? element.Element(DrawnA + "rPr") : element.Parent?.Element(DrawnA + "rPr");
                var strike = runProperties?.Attribute("strike")?.Value is { } value && !value.Equals("noStrike", StringComparison.OrdinalIgnoreCase);
                runs.Add(new DrawnRun(element.Name.LocalName == "br" ? "\n" : element.Value, SizeOf(runProperties), strike));
            }
            result.Add(new DrawnParagraph(runs, SizeOf(paragraph.Element(DrawnA + "endParaRPr")), style));
        }
        return result;
    }

    // ------------------------------------------------------------------------------------------- where the text lies

    private sealed record DrawnRow(int Offset, string Text, double Width, double Size, double LineHeight, int VisualLines, double Before, double After,
        DrawnParagraphStyle Style, bool FirstOfParagraph, bool IsStruck);

    // The units of one frame: each line of text with the offsets of its characters in the frame's text and where it lies.
    private static List<DrawnUnit> LayoutDrawnFrame(PptxShapeRecord shape, (int Row, int Cell)? cell, DrawnFrame frame)
    {
        var units = new List<DrawnUnit>();
        var innerLeft = frame.Left + frame.LeftInset;
        var innerRight = frame.Left + frame.Width - frame.RightInset;
        var frameBottom = frame.Top + frame.Height;

        // The offsets of the characters in the text the node holds: the paragraphs joined by line breaks (for a cell, without
        // its empty paragraphs at either edge; for a shape of formatted text, without those before its first paragraph with runs).
        var first = frame.HeldFrom;
        var last = frame.Paragraphs.Count;
        if (frame.TrimEmptyEdges)
        {
            while (first < last && frame.Paragraphs[first].Text.Length == 0) first++;
            while (last > first && frame.Paragraphs[last - 1].Text.Length == 0) last--;
        }

        var rows = new List<DrawnRow>();
        var offset = 0;
        for (var index = 0; index < frame.Paragraphs.Count; index++)
        {
            var paragraph = frame.Paragraphs[index];
            var style = paragraph.Style;
            var kept = index >= first && index < last;
            // A line is a stretch of runs between two line breaks. An empty paragraph is a line of its own size.
            var lines = new List<List<(string Text, double Size, bool Strike)>> { new() };
            foreach (var run in paragraph.Runs)
            {
                var pieces = run.Text.Split('\n');
                for (var piece = 0; piece < pieces.Length; piece++)
                {
                    if (piece > 0) lines.Add([]);
                    if (pieces[piece].Length > 0) lines[^1].Add((pieces[piece], run.Size, run.Strike));
                }
            }
            var position = kept ? offset : -1;
            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                var line = lines[lineIndex];
                var text = string.Concat(line.Select(piece => piece.Text));
                var size = line.Count > 0 ? line.Max(piece => piece.Size) : paragraph.EndSize;
                var spacing = style.SpacingPoints is { } points ? points
                    : DrawnLineEm * size * Math.Max(0.1, (style.SpacingPercent ?? 1) - frame.SpacingReduction);
                double Space(double? points, double? percent) => points ?? (percent ?? 0) * DrawnLineEm * size;
                var firstOfParagraph = lineIndex == 0;
                var width = line.Sum(piece => DrawnLineRules.TextWidth(piece.Text, piece.Size));
                var allStruck = line.Count > 0 && line.Where(piece => piece.Text.Any(char.IsLetterOrDigit)).All(piece => piece.Strike) &&
                                line.Any(piece => piece.Text.Any(char.IsLetterOrDigit));
                // The width this line has to wrap at, to count its lines.
                var leftMargin = style.MarginLeft ?? 0;
                var indent = style.Indent ?? 0;
                var lineLeft = firstOfParagraph && !(style.Bullet == true && indent < 0) ? leftMargin + indent : leftMargin;
                var available = Math.Max(1, innerRight - innerLeft - lineLeft);
                var visual = !frame.Wrap || width <= 0.9 * available ? 1 : Math.Max(2, (int)Math.Ceiling(width / available));
                rows.Add(new DrawnRow(position < 0 ? -1 : position, text, width, size, spacing, visual,
                    firstOfParagraph && index > 0 ? Space(style.BeforePoints, style.BeforePercent) : 0,
                    lineIndex == lines.Count - 1 ? Space(style.AfterPoints, style.AfterPercent) : 0, style, firstOfParagraph, allStruck));
                if (position >= 0) position += text.Length + 1;
            }
            if (kept) offset += paragraph.Text.Length + 1;
        }
        if (rows.Count == 0) return units;

        // The height of the text and where it starts: from the top of the frame, its middle or its bottom, inside the insets.
        var total = rows.Sum(row => row.Before + row.VisualLines * row.LineHeight + row.After) - rows[^1].After;
        var innerTop = frame.Top + frame.TopInset;
        var innerBottom = frameBottom - frame.BottomInset;
        var blockTop = frame.Anchor switch
        {
            "ctr" => innerTop + (innerBottom - innerTop - total) / 2,
            "b" => innerBottom - total,
            _ => innerTop,
        };

        var y = blockTop;
        foreach (var row in rows)
        {
            y += row.Before;
            var rowTop = y;
            y += row.VisualLines * row.LineHeight + row.After;
            // Only words and numbers are struck or underlined: a milestone symbol on a schedule is no text to cancel.
            if (row.Offset < 0 || !row.Text.Any(char.IsLetterOrDigit)) continue;
            var style = row.Style;
            var leftMargin = style.MarginLeft ?? 0;
            var indent = style.Indent ?? 0;
            var areaLeft = innerLeft + (row.FirstOfParagraph && !(style.Bullet == true && indent < 0) ? leftMargin + indent : leftMargin);
            var baselines = Enumerable.Range(0, row.VisualLines).Select(line => rowTop + (line + 1) * row.LineHeight - DrawnBelowBaselineEm * row.Size).ToArray();
            DrawnTextExtent extent;
            if (row.VisualLines == 1)
            {
                var alignment = style.Align switch { "ctr" => DrawnAlignment.Center, "r" => DrawnAlignment.Right, "dist" or "thaiDist" => DrawnAlignment.Fill, _ => DrawnAlignment.Left };
                // Centered text is centered between its margin and the right inset, whatever the first line's indent.
                extent = DrawnLineRules.SingleLine(row.Size, frameBottom, alignment == DrawnAlignment.Center ? innerLeft + leftMargin : areaLeft, innerRight, row.Width,
                    alignment, baselines[0]);
            }
            else extent = DrawnLineRules.SeveralLines(row.Size, frameBottom, areaLeft, innerRight, baselines);
            units.Add(new DrawnUnit(shape, cell, row.Offset, row.Offset + row.Text.Length, extent, row.IsStruck, frame.Painted));
        }
        return units;
    }

    // ------------------------------------------------------------------------------------------------- the slide's lines

    /// <summary>The text the lines of a slide strike through or underline. A line decorates a line of text when it lies in
    /// the strike or underline zone of the text and spans it; a line over only part of it strikes nothing and is reported
    /// for review (strike) or ignored (underline), and a line over no text is left as it is. A line that connects shapes, or
    /// has an arrowhead, is no decoration. A line in <paramref name="excluded"/> is left as it is, though it still counts as a
    /// line the others may not end on.</summary>
    private static DrawnLineDecorations FindDrawnLineDecorations(Dictionary<string, byte[]> package, PptxSlideRecord slide, DrawnPartCache cache,
        IReadOnlySet<PptxShapeRecord> excluded)
    {
        var own = slide.Shapes.Where(shape => shape.InheritedFrom is null).ToList();
        if (!own.Any(MaybeDrawnLine) || !own.Any(shape => !shape.IsHidden && !shape.IsHiddenByGroup && (shape.IsTable || !string.IsNullOrWhiteSpace(shape.Text)))) return DrawnLineDecorations.None;
        if (cache.Load(package, slide.PartUri) is not { } document || DrawnShapeElements(document, own) is not { } elements) return DrawnLineDecorations.None;

        var strokes = new Dictionary<PptxShapeRecord, DrawnStroke>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < own.Count; index++)
            if (AsDrawnStroke(own[index], elements[index]) is { } stroke) strokes[own[index]] = stroke;
        if (strokes.Count == 0) return DrawnLineDecorations.None;

        var units = new List<DrawnUnit>();
        for (var index = 0; index < own.Count; index++)
        {
            var shape = own[index];
            if (shape.IsHidden || shape.IsHiddenByGroup || strokes.ContainsKey(shape)) continue;
            if (shape.IsTable) units.AddRange(DrawnTableUnits(package, slide, cache, shape, elements[index]));
            else if (!string.IsNullOrWhiteSpace(shape.Text)) units.AddRange(DrawnShapeUnits(package, slide, cache, shape, elements[index]));
        }
        if (units.Count == 0) return DrawnLineDecorations.None;

        // The frames and lines other shapes draw, which a decoration may not end on.
        var painted = new List<(PptxShapeRecord Shape, DrawnObstacle Obstacle)>();
        for (var index = 0; index < own.Count; index++)
        {
            var shape = own[index];
            if (shape.IsHidden || shape.IsHiddenByGroup || shape.Geometry is not { } frame || !IsDrawnShapePainted(shape, elements[index])) continue;
            if (strokes.TryGetValue(shape, out var stroke))
                painted.Add((shape, new DrawnObstacle(true, stroke.Left, stroke.LeftY, stroke.Right, stroke.RightY, stroke)));
            else if (shape.ConnectorPathPoints is { Count: >= 2 } path)
                painted.Add((shape, new DrawnObstacle(true, path[0].X / DrawnEmuPerPoint, path[0].Y / DrawnEmuPerPoint, path[^1].X / DrawnEmuPerPoint, path[^1].Y / DrawnEmuPerPoint)));
            else
                painted.Add((shape, new DrawnObstacle(false, frame.X / DrawnEmuPerPoint, frame.Y / DrawnEmuPerPoint, (frame.X + frame.Width) / DrawnEmuPerPoint, (frame.Y + frame.Height) / DrawnEmuPerPoint)));
        }

        // The shapes of a slide are drawn in the order of its shape tree, the later over the earlier.
        var layers = new Dictionary<PptxShapeRecord, int>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < own.Count; index++) layers[own[index]] = index;

        var consumed = new HashSet<PptxShapeRecord>(ReferenceEqualityComparer.Instance);
        var linesOver = new Dictionary<PptxShapeRecord, HashSet<PptxShapeRecord>>(ReferenceEqualityComparer.Instance);
        var found = new Dictionary<DrawnUnit, (bool Strike, bool Underline, bool PartialStrike)>(ReferenceEqualityComparer.Instance);
        foreach (var (lineShape, stroke) in strokes.OrderBy(item => item.Value.Y).ThenBy(item => item.Value.Left).Select(item => (item.Key, item.Value)))
        {
            var hits = new List<(DrawnUnit Unit, DrawnKind Kind, bool Whole)>();
            foreach (var unit in units)
            {
                // A line is the decoration of text a viewer sees it over: one drawn before a shape or table whose fill lies under the text
                // is hidden there, and is the connector, overlay or edge it was.
                if (unit.IsPainted && layers[lineShape] < layers[unit.Shape]) continue;
                if (DrawnLineRules.Match(stroke, unit.Extent) is { } match) hits.Add((unit, match.Kind, match.Whole));
            }
            if (hits.Count == 0 || excluded.Contains(lineShape)) continue;
            // The shape the text belongs to may have the line end on its own frame: a line that spans a text box from edge
            // to edge is the strikethrough of its text.
            var hosts = hits.Select(hit => hit.Unit.Shape).ToHashSet(ReferenceEqualityComparer.Instance);
            var others = painted.Where(item => !ReferenceEquals(item.Shape, lineShape) && !hosts.Contains(item.Shape)).Select(item => item.Obstacle);
            if (DrawnLineRules.MeetsAnother(stroke, hits.Max(hit => hit.Unit.Extent.Size), others)) continue;
            consumed.Add(lineShape);
            foreach (var (unit, kind, whole) in hits)
            {
                if (!linesOver.TryGetValue(unit.Shape, out var over)) linesOver[unit.Shape] = over = new HashSet<PptxShapeRecord>(ReferenceEqualityComparer.Instance);
                over.Add(lineShape);
                var before = found.GetValueOrDefault(unit);
                found[unit] = (before.Strike || kind == DrawnKind.Strike && whole, before.Underline || kind == DrawnKind.Underline && whole,
                    before.PartialStrike || kind == DrawnKind.Strike && !whole);
            }
        }
        if (consumed.Count == 0) return DrawnLineDecorations.None;

        var shapes = new Dictionary<PptxShapeRecord, DrawnTextDecoration>(ReferenceEqualityComparer.Instance);
        var cells = new Dictionary<PptxShapeRecord, Dictionary<(int Row, int Cell), DrawnTextDecoration>>(ReferenceEqualityComparer.Instance);
        foreach (var (unit, item) in found)
        {
            DrawnTextDecoration decoration;
            if (unit.Cell is { } position)
            {
                if (!cells.TryGetValue(unit.Shape, out var table)) cells[unit.Shape] = table = [];
                if (!table.TryGetValue(position, out decoration!)) table[position] = decoration = new();
            }
            else if (!shapes.TryGetValue(unit.Shape, out decoration!)) shapes[unit.Shape] = decoration = new();
            if (item.Strike) decoration.Struck.Add((unit.Start, unit.End));
            if (item.Underline) decoration.Underlined.Add((unit.Start, unit.End));
            // A line the runs already strike whole needs no review for a line across part of it.
            if (!item.Strike && item.PartialStrike && !unit.IsStruck) decoration.StrikeUnplaced = true;
        }
        foreach (var decoration in shapes.Values.Concat(cells.Values.SelectMany(table => table.Values)))
        {
            decoration.Struck.Sort();
            decoration.Underlined.Sort();
        }
        var linesByText = new Dictionary<PptxShapeRecord, IReadOnlyList<PptxShapeRecord>>(ReferenceEqualityComparer.Instance);
        foreach (var (text, lines) in linesOver) linesByText[text] = lines.ToArray();
        return new DrawnLineDecorations { Shapes = shapes, Cells = cells, Consumed = consumed, LinesOver = linesByText };
    }

    // ----------------------------------------------------------------------------------------- the drawings of a slide

    /// <summary>What the drawings of a slide come to once the lines taken as formatting are set aside: the overlays of its tables and
    /// shape grids, the leaders of a table of contents, and its visual graphs with the shapes they account for. The shapes a diagram
    /// really writes (<paramref name="VisualMemberShapeIds"/>) are those a connector is glued to, the ends of the edges it resolved and
    /// the labels of its edges; a text near an unresolved line that the inference took for a box is in
    /// <paramref name="VisualNodeShapeIds"/> only.</summary>
    private sealed record SlideDrawings(
        IReadOnlyDictionary<string, IReadOnlyList<PptxTableOverlay>> TableOverlaysByTable,
        IReadOnlyDictionary<string, string> TableOverlayHostByShapeId,
        IReadOnlyList<ShapeGridTable> ShapeGridTables,
        IReadOnlyDictionary<string, string> GridMemberHostByShapeId,
        IReadOnlyDictionary<string, ShapeGridTable> GridInsertBeforeShapeId,
        IReadOnlySet<string> TocLeaderShapeIds,
        IReadOnlyList<VisualGraph> VisualGraphs,
        IReadOnlySet<string> VisualLabelShapeIds,
        IReadOnlySet<string> VisualConnectorShapeIds,
        IReadOnlySet<string> VisualNodeShapeIds,
        IReadOnlySet<string> VisualMemberShapeIds);

    /// <summary>The lines of a slide that are text formatting, and the slide's drawings without them. A line over a text that another
    /// part of the Markdown writes (the cell of a shape grid, the text of an overlay symbol, the label of a box in a diagram) has no
    /// way to show the formatting there, and would be dropped without a trace: it stays the drawing it was, an overlay symbol or an
    /// unresolved connector to review. Which texts those are follows from the drawings, which depend on the lines taken, so the lines
    /// over such texts are left out and the slide is analyzed again. A text a line does decorate is text, though some other line may
    /// have been near enough for the inference to take it for a box of a diagram: the diagrams are built without it.</summary>
    private (DrawnLineDecorations DrawnLines, SlideDrawings Drawings) AnalyzeSlide(Dictionary<string, byte[]> package, PptxSlideRecord slide,
        DrawnPartCache cache, CancellationToken cancellationToken)
    {
        var excluded = new HashSet<PptxShapeRecord>(ReferenceEqualityComparer.Instance);
        while (true)
        {
            var drawnLines = FindDrawnLineDecorations(package, slide, cache, excluded);
            // With the texts the lines decorate left among the drawings, the diagrams show which of them something else really writes.
            var drawings = AnalyzeSlideDrawings(slide, drawnLines, EmptyShapeIdSet, cancellationToken);
            var owned = LinesOverOwnedText(drawnLines, drawings);
            if (owned.Count > 0 && !owned.IsSubsetOf(excluded))
            {
                excluded.UnionWith(owned);
                continue;
            }
            var texts = drawnLines.Shapes.Keys.Select(shape => shape.ShapeId).ToHashSet(StringComparer.Ordinal);
            return (drawnLines, texts.Count == 0 ? drawings : AnalyzeSlideDrawings(slide, drawnLines, texts, cancellationToken));
        }
    }

    private static HashSet<PptxShapeRecord> LinesOverOwnedText(DrawnLineDecorations drawnLines, SlideDrawings drawings)
    {
        var lines = new HashSet<PptxShapeRecord>(ReferenceEqualityComparer.Instance);
        foreach (var (text, over) in drawnLines.LinesOver)
        {
            // A table writes the formatting of its cells itself.
            if (text.IsTable) continue;
            var id = text.ShapeId;
            if (drawings.GridMemberHostByShapeId.ContainsKey(id) || drawings.TableOverlayHostByShapeId.ContainsKey(id) || drawings.VisualMemberShapeIds.Contains(id))
                lines.UnionWith(over);
        }
        return lines;
    }

    // `outOfDiagrams` are shapes the visual graphs are built without.
    private SlideDrawings AnalyzeSlideDrawings(PptxSlideRecord slide, DrawnLineDecorations drawnLines, IReadOnlySet<string> outOfDiagrams,
        CancellationToken cancellationToken)
    {
        // The slide record keeps every shape; for what looks at the slide's drawings -- table overlays, shape grids, the visual graph --
        // the lines taken as formatting are not drawings, so none of them also becomes an overlay symbol or a diagram edge. Each still
        // has its own node: the node list, and with it the round-trip projection, does not change.
        var drawingSlide = drawnLines.Consumed.Count == 0 ? slide
            : slide with { Shapes = slide.Shapes.Where(shape => !drawnLines.Consumed.Contains(shape)).ToArray() };
        // G11: both DetectTableOverlays and DetectShapeGridTables need this slide's native
        // a:tbl hosts and connector-wired shape ids; computing each once here instead of once
        // per call (DetectTableOverlays, then again inside DetectShapeGridTables' own guard
        // rail (c) and box-candidate gate, then again on every ClassifyOverlayCandidates call)
        // avoids repeating the same LINQ pass 3-4 times per slide for the same answer.
        var nativeHostTables = GetNativeHostTables(drawingSlide.Shapes);
        var wiredShapeIds = GetWiredShapeIds(drawingSlide.Shapes);
        // P-Overlay: strip schedule-arrow/bar/marker/line overlay shapes out of the pool that
        // feeds visual-flow inference (they overlay a table, they are not a diagram edge) before
        // building this slide's visual graphs -- see DetectTableOverlays for the full contract.
        var (tableOverlaysByTable, tableOverlayShapeIds) = DetectTableOverlays(drawingSlide.Shapes, nativeHostTables, wiredShapeIds);
        var tableOverlayHostByShapeId = tableOverlaysByTable
            .SelectMany(entry => entry.Value.Select(overlay => (OverlayId: overlay.ShapeId, HostId: entry.Key)))
            .ToDictionary(pair => pair.OverlayId, pair => pair.HostId, StringComparer.Ordinal);
        // P-ShapeGrid: detect grids of adjacent rectangle shapes standing in for a native table
        // (shape-grid-table-spec.md) -- must run after DetectTableOverlays (so a grid never
        // claims a shape a native table already absorbed) and before BuildVisualGraphs (so
        // grid members/overlays never leak into visual-flow inference either, same contract as
        // tableOverlayShapeIds just above).
        var shapeGridTables = DetectShapeGridTables(drawingSlide.Shapes, tableOverlayShapeIds, nativeHostTables, wiredShapeIds);
        var gridMemberHostByShapeId = shapeGridTables
            .SelectMany(grid => grid.MemberShapeIds.Select(id => (MemberId: id, HostId: grid.GridShapeId)))
            .ToDictionary(pair => pair.MemberId, pair => pair.HostId, StringComparer.Ordinal);
        var gridInsertBeforeShapeId = shapeGridTables.ToDictionary(grid => grid.InsertBeforeShapeId, StringComparer.Ordinal);
        // Grid overlays reuse the exact same table_overlay_host/table_overlay extension wiring
        // as a native table's overlays (below) -- merging them into the same dictionary means
        // ReadableMarkdownSerializer's existing overlay-shape suppression needs no changes at all.
        foreach (var grid in shapeGridTables)
            foreach (var overlayId in grid.OverlayShapeIds)
                tableOverlayHostByShapeId[overlayId] = grid.GridShapeId;
        var visualExclusionShapeIds = tableOverlayShapeIds.Concat(shapeGridTables.SelectMany(grid => grid.MemberShapeIds.Concat(grid.OverlayShapeIds)))
            .Concat(outOfDiagrams).ToHashSet(StringComparer.Ordinal);
        var tocLeaderShapeIds = FindTocLeaderShapeIds(drawingSlide);
        var visualGraphs = BuildVisualGraphs(
            visualExclusionShapeIds.Count > 0 || tocLeaderShapeIds.Count > 0
                ? drawingSlide with { Shapes = drawingSlide.Shapes.Where(s => !visualExclusionShapeIds.Contains(s.ShapeId) && !tocLeaderShapeIds.Contains(s.ShapeId)).ToArray() } : drawingSlide,
            out var visualLabelShapeIds, VisualInferenceTimeout, cancellationToken);
        var visualConnectorShapeIds = visualGraphs.SelectMany(graph => graph.Edges).Where(edge => edge.SourceId is not null && edge.TargetId is not null)
            .Select(edge => edge.SourceNodeId).Where(id => id is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
        var visualNodeShapeIds = visualGraphs.SelectMany(graph => graph.Nodes).Select(node => node.SourceNodeId)
            .Where(id => id is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
        visualNodeShapeIds.UnionWith(drawingSlide.Shapes.Where(shape => StringComparer.Ordinal.Equals(shape.ShapeType, "connector"))
            .SelectMany(shape => new[] { shape.ConnectorStartId, shape.ConnectorEndId })
            .Where(id => id is not null).Cast<string>());
        // The shapes a diagram really writes: those a connector is glued to, the ends of the edges it resolved, and the labels of its edges.
        var visualMemberShapeIds = new HashSet<string>(visualLabelShapeIds, StringComparer.Ordinal);
        visualMemberShapeIds.UnionWith(wiredShapeIds);
        foreach (var graph in visualGraphs)
        {
            var shapeOfNode = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var node in graph.Nodes)
                if (node.SourceNodeId is not null) shapeOfNode.TryAdd(node.Id, node.SourceNodeId);
            foreach (var edge in graph.Edges.Where(edge => edge.SourceId is not null && edge.TargetId is not null))
                foreach (var end in new[] { edge.SourceId!, edge.TargetId! })
                    if (shapeOfNode.TryGetValue(end, out var shapeId)) visualMemberShapeIds.Add(shapeId);
        }
        return new SlideDrawings(tableOverlaysByTable, tableOverlayHostByShapeId, shapeGridTables, gridMemberHostByShapeId, gridInsertBeforeShapeId,
            tocLeaderShapeIds, visualGraphs, visualLabelShapeIds, visualConnectorShapeIds, visualNodeShapeIds, visualMemberShapeIds);
    }

    private static IEnumerable<DrawnUnit> DrawnShapeUnits(Dictionary<string, byte[]> package, PptxSlideRecord slide, DrawnPartCache cache, PptxShapeRecord shape,
        XElement element)
    {
        if (element.Name.LocalName != "sp" || element.Element(DrawnP + "txBody") is not { } body) return [];
        var inheritance = DrawnInherit(package, slide, cache, element);
        // A placeholder that takes its place from the layout or master.
        double left, top, width, height;
        if (shape.Geometry is { } geometry)
        {
            // A shape that is turned or flipped over keeps its text apart from its frame: not placed.
            var turn = Math.IEEERemainder(geometry.RotationDegrees - (shape.FlipH ? 180 : 0), 360);
            if (Math.Abs(turn) > 0.5 || shape.FlipV) return [];
            (left, top, width, height) = (geometry.X / DrawnEmuPerPoint, geometry.Y / DrawnEmuPerPoint, geometry.Width / DrawnEmuPerPoint, geometry.Height / DrawnEmuPerPoint);
        }
        else if (DrawnInheritedFrame(inheritance) is { } inherited)
            (left, top, width, height) = (inherited.X / DrawnEmuPerPoint, inherited.Y / DrawnEmuPerPoint, inherited.Width / DrawnEmuPerPoint, inherited.Height / DrawnEmuPerPoint);
        else return [];
        if (width < 1 || height < 1) return [];

        var holders = new[] { body.Element(DrawnA + "lstStyle"), inheritance.LayoutShape?.Element(DrawnP + "txBody")?.Element(DrawnA + "lstStyle"),
            inheritance.MasterShape?.Element(DrawnP + "txBody")?.Element(DrawnA + "lstStyle"), inheritance.MasterStyle, inheritance.DefaultStyle };
        var frame = ReadDrawnFrame(body.Element(DrawnA + "bodyPr"), inheritance, left, top, width, height, scale => ReadDrawnParagraphs(body, holders, scale));
        // The text of the frame is the text the shape's node holds, or nothing here can be placed in it. A node of formatted text begins
        // at its first paragraph with runs (CreateShapeTextContent): the empty paragraphs before it are no part of its text, and the
        // offsets of the characters that follow are counted from there.
        if (frame is null || string.Join('\n', frame.Paragraphs.Select(paragraph => paragraph.Text)) != shape.Text) return [];
        var held = HeldShapeText(shape);
        var skipped = shape.Text.Length - held.Length;
        if (skipped < 0 || skipped > frame.Paragraphs.Count || !shape.Text.EndsWith(held, StringComparison.Ordinal) ||
            frame.Paragraphs.Take(skipped).Any(paragraph => paragraph.Text.Length > 0)) return [];
        return LayoutDrawnFrame(shape, null, frame with { HeldFrom = skipped, Painted = IsDrawnTextPainted(element, inheritance) });
    }

    private static IEnumerable<DrawnUnit> DrawnTableUnits(Dictionary<string, byte[]> package, PptxSlideRecord slide, DrawnPartCache cache, PptxShapeRecord table,
        XElement element)
    {
        if (element.Name.LocalName != "graphicFrame" || table.Geometry is null || table.TableRows is not { Count: > 0 } records ||
            table.TableColumnWidths is not { Count: > 0 }) return [];
        var turn = Math.IEEERemainder(table.Geometry.RotationDegrees - (table.FlipH ? 180 : 0), 360);
        if (Math.Abs(turn) > 0.5 || table.FlipV) return [];
        var host = BuildHostTable(table);
        var rows = element.Descendants(DrawnA + "tr").ToList();
        if (rows.Count != records.Count || host.RowBoundaries.Count != rows.Count + 1) return [];
        var inheritance = new DrawnInheritance(null, null, null, cache.Load(package, "ppt/presentation.xml")?.Descendants(DrawnP + "defaultTextStyle").FirstOrDefault());
        var tablePainted = IsDrawnTablePainted(element.Descendants(DrawnA + "tblPr").FirstOrDefault(), cache.Load(package, "ppt/tableStyles.xml"));

        var units = new List<DrawnUnit>();
        for (var row = 0; row < rows.Count; row++)
        {
            // A cell another cell's horizontal merge covers has no cell of its own in the table's rows.
            var cells = rows[row].Elements(DrawnA + "tc").Where(cell => !IsOn(cell.Attribute("hMerge")?.Value)).ToList();
            if (cells.Count != records[row].Count) continue;
            var column = 0;
            for (var index = 0; index < cells.Count; index++)
            {
                var record = records[row][index];
                var span = Math.Max(1, record.ColSpan);
                var rowSpan = Math.Max(1, record.RowSpan);
                var start = column;
                column += span;
                if (record.RowSpan == 0 || start + span >= host.ColumnBoundaries.Count || row + rowSpan >= host.RowBoundaries.Count) continue;
                if (cells[index].Element(DrawnA + "txBody") is not { } body) continue;
                var properties = cells[index].Element(DrawnA + "tcPr");
                string? Attribute(string name) => properties?.Attribute(name)?.Value;
                double Margin(string name, double fallback) => DrawnPoints(Attribute(name)) ?? fallback;
                var holders = new[] { body.Element(DrawnA + "lstStyle"), inheritance.DefaultStyle };
                var paragraphs = ReadDrawnParagraphs(body, holders, 1);
                var anchor = Attribute("anchor") is "ctr" or "b" ? Attribute("anchor")! : "t";
                var frame = new DrawnFrame(host.ColumnBoundaries[start] / DrawnEmuPerPoint, host.RowBoundaries[row] / DrawnEmuPerPoint,
                    (host.ColumnBoundaries[start + span] - host.ColumnBoundaries[start]) / DrawnEmuPerPoint,
                    (host.RowBoundaries[row + rowSpan] - host.RowBoundaries[row]) / DrawnEmuPerPoint,
                    Margin("marL", DrawnInsetX), Margin("marT", DrawnInsetY), Margin("marR", DrawnInsetX), Margin("marB", DrawnInsetY), anchor, true, 0, paragraphs, TrimEmptyEdges: true,
                    Painted: DrawnFillOf(properties) ?? tablePainted);
                // The same text the table's cell holds, or it cannot be placed in it.
                var first = 0;
                var last = paragraphs.Count;
                while (first < last && paragraphs[first].Text.Length == 0) first++;
                while (last > first && paragraphs[last - 1].Text.Length == 0) last--;
                if (string.Join('\n', paragraphs.Skip(first).Take(last - first).Select(paragraph => paragraph.Text)) != record.Text) continue;
                units.AddRange(LayoutDrawnFrame(table, (row, index), frame));
            }
        }
        return units;
    }

    // ---------------------------------------------------------------------------------------------------- the node's marks

    // A strike over part of the text that cannot be placed: the node says so, and asks for source review.
    private static void MarkDrawnStrikeUnplaced(Dictionary<string, JsonElement> extension)
    {
        extension[ReviewExtensionKeys.StrikeUnplaced] = JsonSerializer.SerializeToElement(true);
        extension[ReviewExtensionKeys.SourceReviewRequired] = JsonSerializer.SerializeToElement(true);
    }

    /// <summary>The [start, end) ranges as an extension value.</summary>
    private static int[][] DrawnRangeValue(IEnumerable<(int Start, int End)> ranges) =>
        MergeDrawnRanges(ranges).Select(range => new[] { range.Start, range.End }).ToArray();

    // Ranges in order, those that overlap or touch joined.
    private static List<(int Start, int End)> MergeDrawnRanges(IEnumerable<(int Start, int End)> ranges)
    {
        var merged = new List<(int Start, int End)>();
        foreach (var range in ranges.Where(range => range.End > range.Start).OrderBy(range => range.Start).ThenBy(range => range.End))
        {
            if (merged.Count > 0 && merged[^1].End >= range.Start) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
            else merged.Add(range);
        }
        return merged;
    }

    // The cells of a table with their ranges (struck or underlined) in the order of the table's rows, the ranges the runs
    // gave and those the drawn lines gave joined.
    private static object[][] DrawnCellValue(IReadOnlyList<(int Row, int Column, IReadOnlyList<(int Start, int End)> Ranges)>? fromRuns,
        IReadOnlyDictionary<(int Row, int Cell), DrawnTextDecoration>? fromLines, Func<DrawnTextDecoration, List<(int Start, int End)>> pick)
    {
        var cells = new SortedDictionary<(int Row, int Cell), List<(int Start, int End)>>();
        foreach (var (row, column, ranges) in fromRuns ?? [])
            cells[(row, column)] = [.. ranges];
        foreach (var (position, decoration) in fromLines ?? new Dictionary<(int Row, int Cell), DrawnTextDecoration>())
        {
            var drawn = pick(decoration);
            if (drawn.Count == 0) continue;
            if (!cells.TryGetValue(position, out var known)) cells[position] = known = [];
            known.AddRange(drawn);
        }
        return cells.Select(cell => new object[] { cell.Key.Row, cell.Key.Cell, DrawnRangeValue(cell.Value) }).ToArray();
    }
}
