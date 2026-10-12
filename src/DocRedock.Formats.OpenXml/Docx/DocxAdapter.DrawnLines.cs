using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Common;

namespace DocRedock.Formats.OpenXml.Docx;

// Lines drawn over Word text. People cancel text in a Word document by drawing a straight line through it (often red,
// sometimes double) and emphasize text by drawing a line under it. Such a line is text formatting, not a drawing: it is read
// as a strikethrough or underline of the paragraph it crosses and leaves the document's drawings, so it does not also become
// a table overlay symbol, a diagram edge or an unresolved line to review.
//
// A drawing has no notion of the text under it, and without page layout the position of a drawing against the text is known
// in few cases only: a shape anchored in the paragraph it crosses (or in a table cell's), placed relative to that
// paragraph's top or line, and to the column it is set in, that crosses a paragraph of one line. The line's place against
// the text is then estimated from the font size, the line spacing and the width of the text. Every other line over text
// -- a page-anchored line, a line over a paragraph of several lines, a line in a list -- stays the drawing it was, and asks
// for no review: drawn separators are common, and a review there would be noise.
public sealed partial class DocxAdapter
{
    private const double DrawnTwipsPerPoint = 20;
    private const double DrawnEmuPerPoint = 12700;
    // The size of text that nothing sizes (OOXML's default is 10 pt) and the margin of a table cell (108 twips).
    private const double DrawnDefaultSize = 10;
    private const double DrawnDefaultCellMargin = 5.4;
    // A line of single spacing is 1.2 em (Calibri 1.22, Arial and Times New Roman 1.15). The first baseline lies about 0.88
    // em under the top of the line: 0.95 from the metrics of Calibri in Word, 0.90 for Arial and Times New Roman, 0.86 for
    // MS Mincho and MS Gothic, 0.75 in LibreOffice's rendering of Carlito. A spacing of more than one line adds the extra
    // space above the text in Word and below it in LibreOffice, so half of it is counted above.
    private const double DrawnLineEm = 1.22;
    private const double DrawnBaselineEm = 0.88;
    // Past this many lines of spacing it is no longer known where the baseline is.
    private const double DrawnMaxLineMultiple = 1.25;
    // Text is a single line when its estimated width is no more than this share of the space it is set in.
    private const double DrawnSingleLineShare = 0.9;

    /// <summary>What the drawn lines decorate in one paragraph: whether they strike or underline all of its text, and whether
    /// one strikes part of it so that which characters it strikes could not be determined.</summary>
    private sealed class DocxDrawnDecoration
    {
        public bool Strike { get; set; }
        public bool Underline { get; set; }
        public bool StrikeUnplaced { get; set; }
    }

    /// <summary>The paragraphs the drawn lines of a document decorate, and the shapes that were taken as that formatting.</summary>
    private sealed class DocxDrawnLines
    {
        public static readonly DocxDrawnLines None = new();

        public Dictionary<XElement, DocxDrawnDecoration> Paragraphs { get; init; } = new Dictionary<XElement, DocxDrawnDecoration>();
        public IReadOnlySet<XElement> Consumed { get; init; } = new HashSet<XElement>();
    }

    // -------------------------------------------------------------------------------------------------------- styles

    /// <summary>The styles of a document, for the properties that place text: sizes, line spacing, indents and alignment.
    /// A property is found in the paragraph or run itself, then its styles and the styles they are based on, the style of
    /// the table it is in, and the document's defaults.</summary>
    private sealed class DocxDrawnStyles
    {
        private readonly Dictionary<string, XElement> styles = new(StringComparer.Ordinal);
        private readonly XElement? runDefaults;
        private readonly XElement? paragraphDefaults;
        private readonly string? defaultParagraph;
        private readonly string? defaultTable;

        public DocxDrawnStyles(XDocument? document)
        {
            var root = document?.Root;
            foreach (var style in root?.Elements(W + "style") ?? [])
                if ((string?)style.Attribute(W + "styleId") is { } id) styles.TryAdd(id, style);
            runDefaults = root?.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr");
            paragraphDefaults = root?.Element(W + "docDefaults")?.Element(W + "pPrDefault")?.Element(W + "pPr");
            string? DefaultOf(string type) => root?.Elements(W + "style").FirstOrDefault(style =>
                string.Equals((string?)style.Attribute(W + "type"), type, StringComparison.OrdinalIgnoreCase) &&
                (string?)style.Attribute(W + "default") is "1" or "true" or "on")?.Attribute(W + "styleId")?.Value;
            defaultParagraph = DefaultOf("paragraph");
            defaultTable = DefaultOf("table");
        }

        public string? DefaultTable => defaultTable;

        // The style, then the styles it is based on, nearest first.
        private IEnumerable<XElement> Chain(string? id)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (id is not null && seen.Count < MaxStyleChainDepth && seen.Add(id) && styles.TryGetValue(id, out var style))
            {
                yield return style;
                id = (string?)style.Element(W + "basedOn")?.Attribute(W + "val");
            }
        }

        /// <summary>The paragraph property sets (w:pPr) that apply to a paragraph, the nearest first.</summary>
        public IReadOnlyList<XElement> ParagraphProperties(XElement paragraph, string? tableStyle)
        {
            var own = paragraph.Element(W + "pPr");
            var style = (string?)own?.Element(W + "pStyle")?.Attribute(W + "val") ?? defaultParagraph;
            return [.. new[] { own }.Concat(Chain(style).Select(item => item.Element(W + "pPr"))).Concat(Chain(tableStyle).Select(item => item.Element(W + "pPr")))
                .Append(paragraphDefaults).OfType<XElement>()];
        }

        /// <summary>The run property sets (w:rPr) that apply to a run of a paragraph, the nearest first.</summary>
        public IReadOnlyList<XElement> RunProperties(XElement run, XElement paragraph, string? tableStyle)
        {
            var own = run.Element(W + "rPr");
            var paragraphStyle = (string?)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? defaultParagraph;
            return [.. new[] { own }.Concat(Chain((string?)own?.Element(W + "rStyle")?.Attribute(W + "val")).Select(item => item.Element(W + "rPr")))
                .Concat(Chain(paragraphStyle).Select(item => item.Element(W + "rPr"))).Concat(Chain(tableStyle).Select(item => item.Element(W + "rPr")))
                .Append(runDefaults).OfType<XElement>()];
        }

        // The cell margin of a table style (w:tblCellMar), its nearest declaration.
        public XElement? TableCellMargins(string? tableStyle) =>
            Chain(tableStyle).Select(item => item.Element(W + "tblPr")?.Element(W + "tblCellMar")).FirstOrDefault(item => item is not null);
    }

    private static string? DrawnAttribute(IEnumerable<XElement> properties, string element, string attribute) =>
        properties.Select(item => (string?)item.Element(W + element)?.Attribute(W + attribute)).FirstOrDefault(value => value is not null);

    private static double? DrawnTwips(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var twips) && double.IsFinite(twips) ? twips / DrawnTwipsPerPoint : null;

    // w:linePitch is ST_DecimalNumber: reject fractional and exponent spellings instead of treating them as twips.
    private static double? DrawnLinePitch(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var twips) && twips > 0 ? twips / DrawnTwipsPerPoint : null;

    private static bool IsDocxExplicitOn(XElement element) =>
        (string?)element.Attribute(W + "val") is not { } value || value.Equals("1", StringComparison.Ordinal) ||
        value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("on", StringComparison.OrdinalIgnoreCase);

    /// <summary>What sets a paragraph's line: its spacing and the space before it, its indents and its alignment; null for a
    /// paragraph whose lines cannot be placed (in a list, in a frame, with a border, right to left, in a spacing of exact
    /// height, indented in characters).</summary>
    private sealed record DocxDrawnFormat(double LineMultiple, double BeforePoints, bool BeforeUnknown, double LeftPoints, double RightPoints,
        double FirstLinePoints, DrawnAlignment Alignment, bool SnapsToGrid);

    private static DocxDrawnFormat? ReadDocxDrawnFormat(IReadOnlyList<XElement> properties)
    {
        // A list item's text starts after its number; a frame, a border or a text direction moves the text.
        if (properties.Any(item => item.Element(W + "framePr") is not null || item.Element(W + "pBdr") is not null ||
                item.Element(W + "textDirection") is not null || IsDocxOoxmlFlagSet(item.Element(W + "bidi")) ||
                item.Element(W + "numPr") is { } numbering && (string?)numbering.Element(W + "numId")?.Attribute(W + "val") is not (null or "0"))) return null;
        var indents = properties.Select(item => item.Element(W + "ind")).OfType<XElement>().ToArray();
        if (indents.Any(item => item.Attributes().Any(attribute => attribute.Name.LocalName.EndsWith("Chars", StringComparison.Ordinal)))) return null;
        string? Indent(params string[] names) => indents.SelectMany(item => names.Select(name => (string?)item.Attribute(W + name))).FirstOrDefault(value => value is not null);

        var rule = DrawnAttribute(properties, "spacing", "lineRule");
        if (rule is not null && !rule.Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
        var line = DrawnAttribute(properties, "spacing", "line");
        var multiple = double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var twips) ? twips / 240 : 1.0;
        if (multiple <= 0 || multiple > DrawnMaxLineMultiple) return null;
        var beforeUnknown = DrawnAttribute(properties, "spacing", "beforeAutospacing") is "1" or "true" or "on" || DrawnAttribute(properties, "spacing", "beforeLines") is { } lines && lines != "0";
        var hanging = DrawnTwips(Indent("hanging")) ?? 0;
        var jc = DrawnAttribute(properties, "jc", "val");
        return new DocxDrawnFormat(multiple, DrawnTwips(DrawnAttribute(properties, "spacing", "before")) ?? 0, beforeUnknown,
            DrawnTwips(Indent("left", "start")) ?? 0, DrawnTwips(Indent("right", "end")) ?? 0,
            hanging > 0 ? -hanging : DrawnTwips(Indent("firstLine")) ?? 0,
            jc switch
            {
                "center" => DrawnAlignment.Center,
                "right" or "end" => DrawnAlignment.Right,
                "distribute" or "thaiDistribute" or "thaiDist" => DrawnAlignment.Fill,
                _ => DrawnAlignment.Left
            },
            !(DrawnAttribute(properties, "snapToGrid", "val") is "0" or "false" or "off"));
    }

    // --------------------------------------------------------------------------------------------- where text is set

    /// <summary>The space a paragraph is set in: the width of its column, or of its table cell inside the margins; and the
    /// style of the table it is in.</summary>
    private sealed record DocxDrawnSpace(double Width, string? TableStyle, bool InCell);

    // The section that holds each top-level block: the first section break at or after it, else the document's own.
    private static XElement?[] DrawnSections(XDocument document, XElement[] bodyElements)
    {
        var sections = new XElement?[bodyElements.Length];
        var next = document.Root?.Element(W + "body")?.Element(W + "sectPr");
        for (var index = bodyElements.Length - 1; index >= 0; index--)
        {
            if (bodyElements[index].Name == W + "p" && bodyElements[index].Element(W + "pPr")?.Element(W + "sectPr") is { } own) next = own;
            sections[index] = next;
        }
        return sections;
    }

    // The width of the column a section sets text in, or null when the section has several columns or no page. A line
    // grid supplies the line height used to place paragraphs that snap to it.
    private static double? DrawnColumnWidth(XElement? section, out double? linePitch, out bool hasLineGrid, out bool hasUnsupportedCharacterGrid)
    {
        linePitch = null;
        hasLineGrid = false;
        hasUnsupportedCharacterGrid = false;
        var page = section?.Element(W + "pgSz");
        var margins = section?.Element(W + "pgMar");
        if (DrawnTwips((string?)page?.Attribute(W + "w")) is not { } width ||
            DrawnTwips((string?)margins?.Attribute(W + "left")) is not { } left || DrawnTwips((string?)margins?.Attribute(W + "right")) is not { } right) return null;
        var columns = section!.Element(W + "cols");
        if (columns is not null && (int.TryParse((string?)columns.Attribute(W + "num"), out var count) ? count : 1) > 1) return null;
        var docGrid = section.Element(W + "docGrid");
        if (docGrid is not null && ((string?)docGrid.Attribute(W + "type") is "lines" or "linesAndChars"))
        {
            hasLineGrid = true;
            linePitch = DrawnLinePitch((string?)docGrid.Attribute(W + "linePitch"));
        }
        hasUnsupportedCharacterGrid = (string?)docGrid?.Attribute(W + "type") is "linesAndChars" or "snapToChars" &&
            (string?)docGrid?.Attribute(W + "charSpace") is { } characterSpace &&
            (!int.TryParse(characterSpace, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCharacterSpace) || parsedCharacterSpace != 0);
        return Math.Max(0, width - left - right);
    }

    // The space a paragraph in a table cell is set in. A cell whose width or margins are not given in twips is not placed.
    private static DocxDrawnSpace? DrawnCellSpace(XElement cell, DocxDrawnStyles styles)
    {
        if (cell.Parent is not { } row || row.Name != W + "tr" || row.Parent is not { } table || table.Name != W + "tbl") return null;
        var tableProperties = table.Element(W + "tblPr");
        if (tableProperties?.Element(W + "tblpPr") is not null || IsDocxOoxmlFlagSet(tableProperties?.Element(W + "bidiVisual"))) return null;
        var cellProperties = cell.Element(W + "tcPr");
        if (cellProperties?.Element(W + "textDirection") is not null) return null;
        var columns = table.Element(W + "tblGrid")?.Elements(W + "gridCol").Select(item => DrawnTwips((string?)item.Attribute(W + "w")) ?? 0).ToArray() ?? [];
        var start = ParseDocxGridBefore(row.Element(W + "trPr"));
        foreach (var previous in row.Elements(W + "tc").TakeWhile(item => !ReferenceEquals(item, cell)))
            start += ParsePositiveInt((string?)previous.Element(W + "tcPr")?.Element(W + "gridSpan")?.Attribute(W + "val")) ?? 1;
        var span = ParsePositiveInt((string?)cellProperties?.Element(W + "gridSpan")?.Attribute(W + "val")) ?? 1;
        var declared = cellProperties?.Element(W + "tcW");
        double? width = ((string?)declared?.Attribute(W + "type") is null or "dxa") ? DrawnTwips((string?)declared?.Attribute(W + "w")) : null;
        if (width is null or <= 0)
        {
            if (start + span > columns.Length) return null;
            width = columns.Skip(start).Take(span).Sum();
        }
        var tableStyle = (string?)tableProperties?.Element(W + "tblStyle")?.Attribute(W + "val") ?? styles.DefaultTable;
        double Margin(string side)
        {
            XElement?[] holders =
            [
                cellProperties?.Element(W + "tcMar"), tableProperties?.Element(W + "tblCellMar"), styles.TableCellMargins(tableStyle),
            ];
            foreach (var holder in holders)
                if ((holder?.Element(W + side) ?? holder?.Element(W + (side == "left" ? "start" : "end"))) is { } element &&
                    ((string?)element.Attribute(W + "type") is null or "dxa") && DrawnTwips((string?)element.Attribute(W + "w")) is { } margin) return margin;
            return DrawnDefaultCellMargin;
        }
        return new DocxDrawnSpace(Math.Max(0, width.Value - Margin("left") - Margin("right")), tableStyle, true);
    }

    // ------------------------------------------------------------------------------------------------ the paragraph

    /// <summary>One line of text: its place in the paragraph's frame, in points (x from the left edge of the column, y from
    /// the top of the line), and the text.</summary>
    private sealed record DocxDrawnLine(DrawnTextExtent Extent, double TextLeft, bool BeforeUnknown, bool AlreadyStruck, bool WidthUnsupported = false);

    // The line a paragraph of one line is set in, or null when the paragraph is not placed: it is empty, in a list, of
    // more than one line, with a tab, an equation or a line break in it, in a section with an unusable line grid, or
    // sized in a way the styles do not give.
    private static DocxDrawnLine? DrawnParagraphLine(XElement paragraph, DocxDrawnSpace space, double? linePitch, bool hasLineGrid,
        bool hasUnsupportedCharacterGrid, DocxDrawnStyles styles, DocxHiddenStyles hiddenStyles)
    {
        var properties = styles.ParagraphProperties(paragraph, space.TableStyle);
        if (ReadDocxDrawnFormat(properties) is not { } format || format.SnapsToGrid && hasLineGrid && linePitch is null) return null;

        var text = new System.Text.StringBuilder();
        var size = 0.0;
        var width = 0.0;
        var widthUnsupported = false;
        var allStruck = true;
        foreach (var element in RelevantDescendants(paragraph))
        {
            if (IsHiddenContentElement(element, hiddenStyles)) continue;
            if (IsMathRoot(element) || element.Name == W + "tab" || element.Name == W + "br" && !IsPageBreakElement(element) || element.Name == W + "cr") return null;
            if (element.Name != W + "t" || element.Parent is not { } run || run.Name != W + "r") continue;
            var runProperties = styles.RunProperties(run, paragraph, space.TableStyle);
            var half = DrawnAttribute(runProperties, "sz", "val");
            var runSize = double.TryParse(half, NumberStyles.Float, CultureInfo.InvariantCulture, out var halfPoints) && halfPoints > 0 ? halfPoints / 2 : DrawnDefaultSize;
            var value = element.Value;
            // Paragraph snapToGrid controls line pitch; the run property independently controls
            // character pitch. Resolve the nearest run declaration before trusting natural widths.
            var snap = runProperties.Select(item => item.Element(W + "snapToGrid")).FirstOrDefault(item => item is not null);
            var snapValue = (string?)snap?.Attribute(W + "val");
            var snapsToCharacters = snapValue != "0" && !string.Equals(snapValue, "false", StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(snapValue, "off", StringComparison.OrdinalIgnoreCase);
            widthUnsupported |= hasUnsupportedCharacterGrid && snapsToCharacters && value.Length > 0;
            text.Append(value);
            if (value.Any(char.IsLetterOrDigit))
            {
                size = Math.Max(size, runSize);
                allStruck &= ReadRunProperties(run).Strike;
            }
            width += DrawnLineRules.TextWidth(value, runSize);
        }
        // Only words and numbers are struck or underlined.
        if (!text.ToString().Any(char.IsLetterOrDigit) || size <= 0) return null;

        // Of one line: its width is no more than 0.9 of the space it is set in.
        var available = space.Width - format.LeftPoints - format.RightPoints;
        if (available <= 1 || width > DrawnSingleLineShare * available) return null;
        var naturalLineHeight = DrawnLineEm * size * format.LineMultiple;
        var lineHeight = format.SnapsToGrid && linePitch is { } pitch ? Math.Max(naturalLineHeight, pitch * format.LineMultiple) : naturalLineHeight;
        var baseline = size * DrawnBaselineEm + (format.LineMultiple - 1) * DrawnLineEm * size / 2 +
                       (lineHeight - naturalLineHeight) / 2;
        var textLeft = format.LeftPoints + format.FirstLinePoints;
        var areaLeft = format.Alignment == DrawnAlignment.Center ? format.LeftPoints : textLeft;
        var areaRight = space.Width - format.RightPoints;
        // Unknown character pitch leaves only a broad candidate span: a strike requires review.
        var extent = widthUnsupported
            ? new DrawnTextExtent(size, lineHeight, areaLeft, areaRight, 0, 0, [baseline], false)
            : DrawnLineRules.SingleLine(size, lineHeight, areaLeft, areaRight, width, format.Alignment, baseline);
        return new DocxDrawnLine(extent, textLeft, format.BeforeUnknown || format.BeforePoints > 0, allStruck,
            widthUnsupported);
    }

    // ------------------------------------------------------------------------------------------------ the shapes

    private static bool IsDrawnLinePreset(string? preset) => preset is "line" or "straightconnector1";

    /// <summary>A shape anchored in a paragraph: where it lies against the paragraph and what it draws, when that is known. A
    /// line or a thin bar has a stroke; any shape whose place is known is an obstacle a decoration may not end on.</summary>
    private sealed record DocxDrawnShape(DocxOverlayCandidate? Candidate, DrawnStroke? Stroke, DrawnObstacle? Obstacle);

    // Where a shape anchored in a paragraph lies, in points from the left edge of the column and the top of the paragraph, or
    // null when that is not known without page layout: the shape is relative to the page or margin of a table, or to
    // anything but the paragraph (or line) and the column (or the margin, outside a table), or to a character that is not
    // at the start of the paragraph. An inline shape has no position of its own, so it is relative to nothing.
    private static (double X, double Y)? DrawnShapeOrigin(DocxOverlayCandidate candidate, XElement paragraph, DocxDrawnLine line, bool inCell)
    {
        if (candidate.VerticalRelativeFrom is not ("paragraph" or "line")) return null;
        var origin = candidate.HorizontalRelativeFrom switch
        {
            "column" => 0,
            "margin" when !inCell => 0,
            "character" when DrawnAnchorIsAtStart(candidate.Owner, paragraph) => line.TextLeft,
            _ => (double?)null,
        };
        return origin is null ? null : (origin.Value + candidate.HorizontalOffsetTwips / DrawnTwipsPerPoint, candidate.VerticalOffsetTwips / DrawnTwipsPerPoint);
    }

    // Whether nothing that shows comes before the run a shape is anchored in: the anchor character is then the first of the
    // paragraph's line.
    private static bool DrawnAnchorIsAtStart(XElement shape, XElement paragraph)
    {
        var anchorRun = shape.Ancestors(W + "r").FirstOrDefault();
        if (anchorRun is null) return false;
        foreach (var element in RelevantDescendants(paragraph))
        {
            if (ReferenceEquals(element, anchorRun)) return true;
            if (element.Name == W + "t" && element.Value.Length > 0 || element.Name == W + "tab" || element.Name == W + "br") return false;
        }
        return false;
    }

    private static DocxDrawnShape ReadDocxDrawnShape(XElement owner, XElement paragraph, DocxDrawnLine line, bool inCell, DocxHiddenStyles hiddenStyles)
    {
        // A shape of a group lies where its group says; a shape in VML lies where the paragraph's text does only in a table cell
        // (ReadDocxOverlayCandidate).
        if (owner.Ancestors(WPG + "wgp").Any() || owner.Name.Namespace == V && !inCell ||
            ReadDocxOverlayCandidate(owner, hiddenStyles) is not { } candidate ||
            DrawnShapeOrigin(candidate, paragraph, line, inCell) is not { } origin)
            return new DocxDrawnShape(null, null, null);
        var (width, height) = (candidate.WidthTwips / DrawnTwipsPerPoint, candidate.HeightTwips / DrawnTwipsPerPoint);
        var stroke = AsDocxDrawnStroke(candidate, origin, width, height);
        return new DocxDrawnShape(candidate, stroke, stroke is null ? new DrawnObstacle(false, origin.X, origin.Y, origin.X + width, origin.Y + height)
            : new DrawnObstacle(true, stroke.Left, stroke.LeftY, stroke.Right, stroke.RightY, stroke));
    }

    // A straight line without an arrowhead, or a thin bar with no text, that is free (glued to no shape), visible and not turned.
    // Whether it is horizontal enough, and thin enough, is judged against the text it crosses.
    private static DrawnStroke? AsDocxDrawnStroke(DocxOverlayCandidate candidate, (double X, double Y) origin, double width, double height)
    {
        var owner = candidate.Owner;
        if (owner.Descendants(W + "txbxContent").Any() ||
            owner.Descendants().Any(item => item.Name == A + "stCxn" || item.Name == A + "endCxn")) return null;
        // A shape turned any other way than half a turn lies where its own frame does not say.
        if (Math.Abs(Math.IEEERemainder(candidate.RotationDegrees, 180)) > 1) return null;
        var isVml = owner.Name.Namespace == V;
        if (isVml)
        {
            // A line in VML: a stroke that is switched off paints nothing.
            if ((string?)owner.Attribute("stroked") is "f" or "false" || (string?)owner.Descendants(V + "stroke").FirstOrDefault()?.Attribute("on") is "f" or "false") return null;
            if (owner.Name != V + "line") return null;
        }
        var weight = DrawnStrokeWeight(owner);
        if (DrawnIsLine(candidate))
        {
            if (HasDrawnArrow(candidate.HeadArrowType) || HasDrawnArrow(candidate.TailArrowType) || !isVml && !HasDocxDrawnOutline(owner)) return null;
            // The ends: opposite corners of the frame, the other pair when the shape is flipped one way.
            var rising = candidate.FlipH ^ candidate.FlipV;
            return rising ? new DrawnStroke(origin.X, origin.Y + height, origin.X + width, origin.Y, weight, false)
                : new DrawnStroke(origin.X, origin.Y, origin.X + width, origin.Y + height, weight, false);
        }
        // A thin rectangle that paints something.
        if (candidate.Preset != "rect" || candidate.IsTextBox || isVml || width < 4 * height || !HasDocxDrawnFill(owner) && !HasDocxDrawnOutline(owner)) return null;
        return new DrawnStroke(origin.X, origin.Y + height / 2, origin.X + width, origin.Y + height / 2, height, true);
    }

    private static bool DrawnIsLine(DocxOverlayCandidate candidate) => IsDrawnLinePreset(candidate.Preset);

    private static bool HasDrawnArrow(string? arrow) => !string.IsNullOrWhiteSpace(arrow) && !arrow.Equals("none", StringComparison.OrdinalIgnoreCase);

    // The weight of the stroke of a line, in points (a:ln@w in EMU, or VML's strokeweight).
    private static double DrawnStrokeWeight(XElement owner)
    {
        if (owner.Name.Namespace == V)
        {
            var text = (string?)owner.Attribute("strokeweight") ?? "";
            var number = new string(text.TakeWhile(character => char.IsDigit(character) || character == '.').ToArray());
            return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight) ? weight : 1;
        }
        var line = DrawnShapeProperties(owner)?.Element(A + "ln");
        return double.TryParse((string?)line?.Attribute("w"), NumberStyles.Float, CultureInfo.InvariantCulture, out var emu) ? emu / DrawnEmuPerPoint : 1;
    }

    // wps:spPr of a DrawingML shape (or the a:sp's own p:spPr / a:spPr).
    private static XElement? DrawnShapeProperties(XElement owner) =>
        owner.Elements().FirstOrDefault(item => item.Name.LocalName == "spPr");

    // Whether the outline paints: a:ln with a fill of its own, or the style's line reference when it says nothing.
    private static bool HasDocxDrawnOutline(XElement owner)
    {
        var outline = DrawnShapeProperties(owner)?.Element(A + "ln");
        if (outline?.Element(A + "noFill") is not null) return false;
        if (outline is not null && (outline.Element(A + "solidFill") ?? outline.Element(A + "gradFill") ?? outline.Element(A + "pattFill")) is not null) return true;
        var reference = owner.Elements().FirstOrDefault(item => item.Name.LocalName == "style")?.Element(A + "lnRef");
        return int.TryParse((string?)reference?.Attribute("idx"), out var index) && index > 0;
    }

    private static bool HasDocxDrawnFill(XElement owner)
    {
        var properties = DrawnShapeProperties(owner);
        if (properties?.Element(A + "noFill") is not null) return false;
        if (properties is not null && properties.Elements().Any(item => item.Name == A + "solidFill" || item.Name == A + "gradFill" ||
                item.Name == A + "pattFill" || item.Name == A + "blipFill")) return true;
        var reference = owner.Elements().FirstOrDefault(item => item.Name.LocalName == "style")?.Element(A + "fillRef");
        return int.TryParse((string?)reference?.Attribute("idx"), out var index) && index > 0;
    }

    // ----------------------------------------------------------------------------------------- the document's lines

    /// <summary>The paragraphs the drawn lines of the document strike through or underline. A line decorates a paragraph when
    /// it is anchored in it, lies in the strike or underline zone of its line, and spans it; a line over only part of the text
    /// strikes nothing and is reported for review (strike) or ignored (underline), and a line over no text, or over text whose
    /// place is not known, is left as it is. A line that connects shapes, or has an arrowhead, is no decoration.</summary>
    private static DocxDrawnLines FindDocxDrawnLines(XDocument document, XElement[] bodyElements, XDocument? styleDocument,
        DocxHiddenStyles hiddenStyles, bool adjustLineHeightInTable)
    {
        // Quick exit: most documents have no line shape at all.
        var anchored = bodyElements.SelectMany(element => element.Descendants().Where(IsDocxOverlayOwnerElement)).ToList();
        if (anchored.Count == 0) return DocxDrawnLines.None;
        var styles = new DocxDrawnStyles(styleDocument);
        var sections = DrawnSections(document, bodyElements);
        var consumed = new HashSet<XElement>();
        var decorations = new Dictionary<XElement, DocxDrawnDecoration>();

        for (var index = 0; index < bodyElements.Length; index++)
        {
            // The shapes of this block, by the paragraph they are anchored in.
            var byParagraph = new Dictionary<XElement, List<XElement>>();
            foreach (var owner in bodyElements[index].Descendants().Where(IsDocxOverlayOwnerElement))
            {
                if (owner.Ancestors(W + "p").FirstOrDefault() is not { } paragraph) continue;
                if (!byParagraph.TryGetValue(paragraph, out var list)) byParagraph[paragraph] = list = [];
                list.Add(owner);
            }
            foreach (var (paragraph, owners) in byParagraph)
            {
                // A paragraph in a text box has the text box's own frame.
                if (paragraph.Ancestors(W + "txbxContent").Any()) continue;
                var cell = paragraph.Ancestors(W + "tc").FirstOrDefault();
                if (DrawnColumnWidth(sections[index], out var linePitch, out var hasLineGrid, out var hasUnsupportedCharacterGrid) is not { } columnWidth) continue;
                var space = cell is null ? new DocxDrawnSpace(columnWidth, null, false) : DrawnCellSpace(cell, styles);
                if (space is null) continue;
                var applyGridPitch = !space.InCell || adjustLineHeightInTable;
                if (DrawnParagraphLine(paragraph, space, applyGridPitch ? linePitch : null, hasLineGrid && applyGridPitch,
                    hasUnsupportedCharacterGrid, styles, hiddenStyles) is not { } line) continue;

                var shapes = owners.Select(owner => ReadDocxDrawnShape(owner, paragraph, line, cell is not null, hiddenStyles)).ToList();
                var obstacles = shapes.Where(shape => shape.Obstacle is not null).ToList();
                var decoration = new DocxDrawnDecoration();
                var partialStrike = false;
                var any = false;
                foreach (var shape in shapes)
                {
                    if (shape.Stroke is not { } stroke || shape.Candidate is not { } candidate || DrawnLineRules.Match(stroke, line.Extent) is not { } match) continue;
                    // Unknown character pitch cannot establish an underline's coverage. Keep the
                    // drawing available to the existing visual fallback instead of consuming it.
                    if (line.WidthUnsupported && match.Kind == DrawnKind.Underline) continue;
                    // A paragraph-relative line of a paragraph with space before it lies where the layout says: not known here.
                    if (candidate.VerticalRelativeFrom == "paragraph" && line.BeforeUnknown) continue;
                    var others = obstacles.Where(other => !ReferenceEquals(other, shape)).Select(other => other.Obstacle!);
                    if (DrawnLineRules.MeetsAnother(stroke, line.Extent.Size, others)) continue;
                    consumed.Add(candidate.Owner);
                    any = true;
                    if (match.Kind == DrawnKind.Strike) { if (match.Whole && !line.WidthUnsupported) decoration.Strike = true; else partialStrike = true; }
                    else if (match.Whole && !line.WidthUnsupported) decoration.Underline = true;
                }
                if (!any) continue;
                // A paragraph the runs already strike whole needs no review for a line across part of it.
                decoration.StrikeUnplaced = !decoration.Strike && partialStrike && !line.AlreadyStruck;
                decorations[paragraph] = decoration;
            }
        }
        return consumed.Count == 0 ? DocxDrawnLines.None : new DocxDrawnLines { Paragraphs = decorations, Consumed = consumed };
    }

    // ------------------------------------------------------------------------------------------------------ the marks

    // A strike over part of the text that cannot be placed: the node says so, and asks for source review.
    private static void MarkDrawnStrikeUnplaced(Dictionary<string, JsonElement> extensions)
    {
        extensions[ReviewExtensionKeys.StrikeUnplaced] = JsonSerializer.SerializeToElement(true);
        extensions[ReviewExtensionKeys.SourceReviewRequired] = JsonSerializer.SerializeToElement(true);
    }

    // The paragraph's node with what the drawn lines decorate written on it, as readable-only ranges over the text the node
    // holds: for a paragraph of rich text, over the text of its runs, which the readable projection cuts the runs at in a copy.
    // The node's content, and with it the round-trip projection, stays as the source has it.
    private static DocumentNode ApplyDrawnParagraphMarks(DocumentNode node, XElement paragraph, DocxDrawnLines drawn)
    {
        if (!drawn.Paragraphs.TryGetValue(paragraph, out var decoration)) return node;
        var length = node.Content switch
        {
            TextNodeContent text => text.Text.Length,
            RichTextNodeContent rich => rich.Runs.Sum(run => run.Text.Length),
            _ => 0,
        };
        if (length == 0) return node;
        var extensions = node.Extensions is null ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(node.Extensions, StringComparer.Ordinal);
        if (decoration.Strike) extensions["strike_ranges"] = JsonSerializer.SerializeToElement(new[] { new[] { 0, length } });
        if (decoration.Underline) extensions["underline_ranges"] = JsonSerializer.SerializeToElement(new[] { new[] { 0, length } });
        if (decoration.StrikeUnplaced) MarkDrawnStrikeUnplaced(extensions);
        return node with { Extensions = extensions };
    }

    // ------------------------------------------------------------- formatting that plain text nodes carry as ranges

    /// <summary>The text a node of plain text holds, with the characters its struck-through and underlined runs hold, as [start, end)
    /// offsets into it: headers and footers, footnotes and endnotes, and text boxes (<see cref="MarkedParagraphText"/> for each
    /// of their paragraphs, joined with line breaks as <see cref="TextBoxText"/> joins them).</summary>
    private static (string Text, IReadOnlyList<(int Start, int End)> Struck, IReadOnlyList<(int Start, int End)> Underlined, IReadOnlyList<(int Start, int End)> Inserted) MarkedTextBoxText(
        XElement textBox, DocxHiddenStyles hiddenStyles)
    {
        var boxes = textBox.Name == W + "txbxContent" ? [textBox] : textBox.Descendants(W + "txbxContent").ToArray();
        var paragraphs = boxes.SelectMany(box => box.Elements(W + "p")).ToArray();
        if (paragraphs.Length == 0) return MarkedParagraphTextOf(textBox, hiddenStyles);
        var text = new System.Text.StringBuilder();
        var struck = new List<(int Start, int End)>();
        var underlined = new List<(int Start, int End)>();
        var inserted = new List<(int Start, int End)>();
        for (var index = 0; index < paragraphs.Length; index++)
        {
            if (index > 0) text.Append('\n');
            var marked = MarkedParagraphText(paragraphs[index], hiddenStyles);
            struck.AddRange(marked.Struck.Select(range => (range.Start + text.Length, range.End + text.Length)));
            underlined.AddRange(marked.Underlined.Select(range => (range.Start + text.Length, range.End + text.Length)));
            inserted.AddRange(marked.Inserted.Select(range => (range.Start + text.Length, range.End + text.Length)));
            text.Append(marked.Text);
        }
        return (text.ToString(), struck, underlined, inserted);
    }

    private static (string Text, IReadOnlyList<(int Start, int End)> Struck, IReadOnlyList<(int Start, int End)> Underlined, IReadOnlyList<(int Start, int End)> Inserted) MarkedParagraphTextOf(
        XContainer container, DocxHiddenStyles hiddenStyles)
    {
        var marked = MarkedParagraphText(container, hiddenStyles);
        return (marked.Text, marked.Struck, marked.Underlined, marked.Inserted);
    }

    // Node extensions for the ranges the formatting of a text gives (nothing when there is none, or when the text is not the
    // text the node holds).
    private static Dictionary<string, JsonElement>? WithMarkedRanges(Dictionary<string, JsonElement>? extensions, string nodeText,
        (string Text, IReadOnlyList<(int Start, int End)> Struck, IReadOnlyList<(int Start, int End)> Underlined, IReadOnlyList<(int Start, int End)> Inserted) marked)
    {
        if (marked.Text != nodeText || marked.Struck.Count == 0 && marked.Underlined.Count == 0 && marked.Inserted.Count == 0) return extensions;
        extensions ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (marked.Struck.Count > 0)
            extensions["strike_ranges"] = JsonSerializer.SerializeToElement(marked.Struck.Select(range => new[] { range.Start, range.End }).ToArray());
        if (marked.Underlined.Count > 0)
            extensions["underline_ranges"] = JsonSerializer.SerializeToElement(marked.Underlined.Select(range => new[] { range.Start, range.End }).ToArray());
        if (marked.Inserted.Count > 0)
            extensions["insert_ranges"] = JsonSerializer.SerializeToElement(marked.Inserted.Select(range => new[] { range.Start, range.End }).ToArray());
        return extensions;
    }
}
