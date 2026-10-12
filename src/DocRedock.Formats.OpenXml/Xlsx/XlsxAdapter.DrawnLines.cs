using System.Globalization;
using System.Text.Json;
using System.Xml;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.OpenXml.Xlsx;

// Lines drawn over worksheet text. People cancel a value in Excel by drawing a straight line through its
// text (often red, sometimes double), and emphasize text by drawing a line under it. Such a line is text
// formatting, not a drawing: it is read as a strikethrough or underline of the cell it crosses and leaves
// the sheet's shapes, so it does not also become an overlay symbol or a diagram edge.
//
// A drawing has no notion of the text under it, so the cell's text is placed from what the sheet gives: the
// row and column geometry, the cell's font size, alignment and wrapping, and an estimate of the text's
// width. The shape rules and the vertical zones relative to the baseline are those of PdfTextDecorations,
// so a workbook and its PDF agree.
public sealed partial class XlsxAdapter
{
    /// <summary>Cell extension marking a cell whose text a drawn line strikes through in part: which
    /// characters it strikes could not be determined, so the text is left unmarked and the cell needs
    /// source review (<c>ReviewElementKind.StrikethroughUnplaced</c>). The key is shared with the other
    /// adapters that read drawn lines, see <see cref="ReviewExtensionKeys.StrikeUnplaced"/>.</summary>
    public const string StrikeUnplacedExtension = ReviewExtensionKeys.StrikeUnplaced;

    private const double DrawnEmuPerPoint = 12700d;
    private const double DrawnDefaultFontSize = 11;

    // A decoration is no thicker than this many ems and at least four times as long as it is thick.
    private const double DrawnMaxThicknessEm = 0.2;
    // Where a line can lie relative to the baseline of the text it decorates, in ems: an underline from
    // 0.35 em below it to 0.15 em above, a strikethrough from there to half an em above. A line higher than
    // that is over the letters, an overline, and stays a drawing. The slack covers the estimate of where
    // the baseline is; it is the one PdfTextDecorations allows when it places a strikethrough.
    private const double DrawnUnderlineLowEm = -0.35;
    private const double DrawnStrikeFromEm = 0.15;
    private const double DrawnStrikeToEm = 0.5;
    private const double DrawnZoneSlackEm = 0.05;
    // A line that comes within this many points of another line or shape with an end is a connector or a
    // rule, not a decoration.
    private const double DrawnTouchDistance = 1.5;
    // A line on the bottom edge of a cell's row is that cell's border rule, not an underline of its text.
    private const double DrawnEdgeMargin = 0.5;
    // Over the text's horizontal extent: a line must overlap the text by this many ems, and covers all of it
    // when it reaches within DrawnReachEm of each end, plus DrawnWidthError of the estimated width at an end
    // whose place depends on that estimate (the text's far end, or both ends of centered text).
    private const double DrawnOverlapEm = 0.3;
    private const double DrawnMinLengthEm = 0.4;
    private const double DrawnReachEm = 0.5;
    private const double DrawnWidthError = 0.15;
    // Where text sits in a cell, in ems of the font size: from the bottom edge up to the baseline of
    // bottom-aligned text, from the middle down to the baseline of centered text, from the top edge down
    // to the baseline of top-aligned text. Measured on LibreOffice's rendering of Arial and of Calibri's
    // metric twin (bottom 0.30-0.34 em, middle 0.25-0.35 em, top 0.85-1.0 em; the values here are in between);
    // Excel is taken to place text alike. Wrapped lines are a line pitch apart.
    private const double DrawnBottomBaselineEm = 0.33;
    private const double DrawnCenterBaselineEm = 0.30;
    private const double DrawnTopBaselineEm = 0.95;
    private const double DrawnLinePitchEm = 1.25;
    // Space between a cell's edge and its text, in points; an indent step is about a character.
    private const double DrawnCellPadding = 1.5;
    private const double DrawnIndentEm = 0.55;
    // A cell merged over more rows than this is looked up from its first rows only.
    private const int DrawnMaxMergedRows = 256;
    // A line turned by up to this many degrees off the horizontal (or a half turn) is placed by its own frame.
    private const double DrawnMaxTurn = 10;

    private enum DrawnKind { Strike, Underline }

    private enum DrawnAlignment { Left, Right, Center, Fill }

    /// <summary>What places a cell's text: its font size, alignment, wrapping and indent. Text that is
    /// rotated or stacked is not placed.</summary>
    private sealed record CellTextStyle(double FontSize, string Horizontal, string Vertical, bool Wrap, int Indent, bool Rotated);

    private static CellTextStyle[] ReadCellTextStyles(Dictionary<string, byte[]> package)
    {
        if (!package.TryGetValue("xl/styles.xml", out var bytes)) return [];
        var document = new XmlDocument();
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        document.Load(reader);
        if (document.DocumentElement is not { } root) return [];
        var sizes = DirectChild(root, "fonts")?.ChildNodes.OfType<XmlElement>().Where(x => x.LocalName == "font")
            .Select(font => double.TryParse(DirectChild(font, "sz")?.GetAttribute("val"), NumberStyles.Float, CultureInfo.InvariantCulture, out var size) && size > 0
                ? size : DrawnDefaultFontSize).ToArray() ?? [];
        var xfs = DirectChild(root, "cellXfs")?.ChildNodes.OfType<XmlElement>().Where(x => x.LocalName == "xf") ?? [];
        return xfs.Select(xf =>
        {
            var fontId = AttributeInt(xf, "fontId");
            var alignment = DirectChild(xf, "alignment");
            return new CellTextStyle(
                fontId is >= 0 && fontId < sizes.Length ? sizes[fontId.Value] : DrawnDefaultFontSize,
                alignment?.GetAttribute("horizontal") is { Length: > 0 } horizontal ? horizontal : "general",
                alignment?.GetAttribute("vertical") is { Length: > 0 } vertical ? vertical : "bottom",
                alignment is not null && IsHiddenFlag(alignment.GetAttribute("wrapText")),
                alignment is null ? 0 : AttributeInt(alignment, "indent") ?? 0,
                alignment is not null && AttributeInt(alignment, "textRotation") is not (null or 0));
        }).ToArray();
    }

    /// <summary>A straight, nearly horizontal line or thin bar that could decorate text: its end points in
    /// points (left end first), how thick a bar is, and the shape it was drawn with.</summary>
    private sealed record DrawnLine(XlsxDrawingShapeRecord Shape, double Left, double LeftY, double Right, double RightY,
        double Thickness, bool IsBar)
    {
        public double Y => (LeftY + RightY) / 2;
        public double Rise => Math.Abs(RightY - LeftY);
    }

    /// <summary>Where a cell's visible text lies, in points: the bottom of its row, the extent of the text
    /// that shows (clipped to the cell or merged area, or running on into empty cells), how far the ends
    /// of that extent are uncertain because the text's width is an estimate, and the baseline of each
    /// line of text. Only text of a single line is covered as a whole by one line.</summary>
    private sealed record DrawnTextBox(double Size, double Bottom, double Left, double Right,
        double LeftSlack, double RightSlack, IReadOnlyList<double> Baselines, bool IsSingleLine);

    private sealed record DrawnCellDecoration(bool Strike, bool Underline, bool StrikeUnplaced);

    /// <summary>What the drawn lines of a sheet decorate: the cells they strike or underline (or strike in
    /// part), and the shapes that were taken as that formatting.</summary>
    private sealed record DrawnLineDecorations(IReadOnlyDictionary<(int Row, int Column), DrawnCellDecoration> Cells,
        IReadOnlySet<XlsxDrawingShapeRecord> Consumed)
    {
        public static readonly DrawnLineDecorations None = new(new Dictionary<(int Row, int Column), DrawnCellDecoration>(),
            new HashSet<XlsxDrawingShapeRecord>());
    }

    // Row and column edges of a sheet in points, memoized: XlsxWorksheetMetrics adds up every custom size
    // before an index each time it is asked.
    private sealed class DrawnLineGrid(XlsxWorksheetMetrics metrics)
    {
        private readonly Dictionary<int, double> rows = [];
        private readonly Dictionary<int, double> columns = [];

        public double RowStart(int row) => rows.TryGetValue(row, out var edge) ? edge : rows[row] = metrics.RowStartEmu(row) / DrawnEmuPerPoint;

        public double ColumnStart(int column) =>
            columns.TryGetValue(column, out var edge) ? edge : columns[column] = metrics.ColumnStartEmu(column) / DrawnEmuPerPoint;
    }

    /// <summary>The cells whose text the sheet's drawn lines strike through or underline. A line decorates a
    /// cell when it lies in the strike or underline zone of the cell's text and spans it; a line over only
    /// part of the text strikes nothing and is reported for review (strike) or ignored (underline), and a
    /// line over no text is left as it is. A line that connects shapes, or has an arrowhead, is no
    /// decoration.</summary>
    private static DrawnLineDecorations FindDrawnLineDecorations(IReadOnlyList<XlsxCellRecord> cells,
        IReadOnlyList<XlsxDrawingShapeRecord> shapes, XlsxWorksheetMetrics metrics, IReadOnlyList<CellTextStyle> styles)
    {
        var lines = shapes.Select(AsDrawnLine).OfType<DrawnLine>().ToList();
        if (lines.Count == 0) return DrawnLineDecorations.None;
        var grid = new DrawnLineGrid(metrics);

        // Text cells by every row they cover, and the columns of every cell that holds something by row (text
        // runs on into the cells to its side that hold nothing).
        var textCells = new Dictionary<int, List<XlsxCellRecord>>();
        var occupied = new Dictionary<int, List<int>>();
        foreach (var cell in cells)
        {
            if (cell.IsBlank) continue;
            if (!occupied.TryGetValue(cell.RowIndex, out var columns)) occupied[cell.RowIndex] = columns = [];
            columns.Add(cell.ColumnIndex);
            // The text of a hidden sheet is not in the output, so nothing about it is reported or changed.
            if (!string.Equals(cell.SheetState, "visible", StringComparison.OrdinalIgnoreCase)) continue;
            // Only words and numbers are struck or underlined: a milestone symbol in a schedule cell is no text
            // to cancel, and a line through a row of them is a line.
            if (!(cell.DisplayValue ?? cell.Value ?? "").Any(char.IsLetterOrDigit)) continue;
            var lastRow = Math.Min(cell.MergedToRow ?? cell.RowIndex, cell.RowIndex + DrawnMaxMergedRows);
            for (var row = cell.RowIndex; row <= lastRow; row++)
            {
                if (!textCells.TryGetValue(row, out var inRow)) textCells[row] = inRow = [];
                inRow.Add(cell);
            }
        }
        foreach (var columns in occupied.Values) columns.Sort();

        var shapeLines = new Dictionary<XlsxDrawingShapeRecord, DrawnLine>();
        foreach (var line in lines)
            shapeLines.TryAdd(line.Shape, line);

        var boxes = new Dictionary<(int Row, int Column), DrawnTextBox?>();
        var found = new Dictionary<(int Row, int Column), (bool Strike, bool Underline, bool PartialStrike)>();
        var consumed = new HashSet<XlsxDrawingShapeRecord>();
        foreach (var line in lines.OrderBy(line => line.Y).ThenBy(line => line.Left))
        {
            var hits = new List<(XlsxCellRecord Cell, DrawnKind Kind, bool Whole, double Size)>();
            var lineRow = (int)Math.Floor(metrics.RowFromEmu((long)Math.Round(line.Y * DrawnEmuPerPoint)));
            for (var row = Math.Max(1, lineRow - 1); row <= lineRow + 1; row++)
            {
                if (!textCells.TryGetValue(row, out var inRow)) continue;
                foreach (var cell in inRow)
                {
                    if (hits.Any(hit => ReferenceEquals(hit.Cell, cell))) continue;
                    var key = (cell.RowIndex, cell.ColumnIndex);
                    if (!boxes.TryGetValue(key, out var box))
                        boxes[key] = box = BuildDrawnTextBox(cell, DrawnStyleOf(cell, styles), grid, occupied);
                    if (box is null || MatchDrawnLine(line, box) is not { } match) continue;
                    hits.Add((cell, match.Kind, match.Whole, box.Size));
                }
            }
            if (hits.Count == 0) continue;
            if (DrawnLineMeetsAnotherShape(line, hits.Max(hit => hit.Size), shapes, shapeLines)) continue;
            consumed.Add(line.Shape);
            foreach (var (cell, kind, whole, _) in hits)
            {
                var key = (cell.RowIndex, cell.ColumnIndex);
                var before = found.GetValueOrDefault(key);
                found[key] = (before.Strike || kind == DrawnKind.Strike && whole,
                    before.Underline || kind == DrawnKind.Underline && whole,
                    before.PartialStrike || kind == DrawnKind.Strike && !whole);
            }
        }

        var decorated = new Dictionary<(int Row, int Column), DrawnCellDecoration>();
        foreach (var cell in cells)
        {
            if (!found.TryGetValue((cell.RowIndex, cell.ColumnIndex), out var item)) continue;
            var struckWhole = cell.StruckRanges is [{ Start: 0 } all] && all.End >= (cell.Value?.Length ?? 0);
            // A cell the font already strikes whole needs no review for a line across part of it.
            decorated[(cell.RowIndex, cell.ColumnIndex)] = new(item.Strike, item.Underline, !item.Strike && item.PartialStrike && !struckWhole);
        }
        return decorated.Count == 0 && consumed.Count == 0 ? DrawnLineDecorations.None : new(decorated, consumed);
    }

    private static CellTextStyle DrawnStyleOf(XlsxCellRecord cell, IReadOnlyList<CellTextStyle> styles) =>
        int.TryParse(cell.StyleId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < styles.Count
            ? styles[index]
            : new CellTextStyle(DrawnDefaultFontSize, "general", "bottom", false, 0, false);

    private static bool IsDrawnStraightLine(XlsxDrawingShapeRecord shape) =>
        shape.Geometry.Equals("line", StringComparison.OrdinalIgnoreCase) || shape.Geometry.Equals("straightConnector1", StringComparison.OrdinalIgnoreCase);

    private static bool HasDrawnArrowhead(string? arrow) => !string.IsNullOrWhiteSpace(arrow) && !arrow.Equals("none", StringComparison.OrdinalIgnoreCase);

    // A straight line without an arrowhead, or a thin rectangle with no text, that is free (glued to no
    // shape), visible, and placed reliably. Whether it is horizontal enough, and thin enough, is judged
    // against the text it crosses.
    private static DrawnLine? AsDrawnLine(XlsxDrawingShapeRecord shape)
    {
        if (shape.IsHidden || shape.IsUnpainted || shape.IsInNestedGroup || shape.IsInTransformedGroup ||
            shape.StartConnectionId is not null || shape.EndConnectionId is not null ||
            !string.IsNullOrWhiteSpace(shape.Text) || shape.AbsoluteBounds is not { } bounds) return null;
        if (IsDrawnStraightLine(shape))
        {
            if (HasDrawnArrowhead(shape.HeadArrow) || HasDrawnArrowhead(shape.TailArrow)) return null;
            // A line turned a half turn (or a little off one) is placed by its own frame. One turned a quarter
            // turn lies on its side only if the anchor holds its unturned frame, which the sheet does not say.
            if (Math.Abs(Math.IEEERemainder(shape.RotationDegrees, 180)) > DrawnMaxTurn) return null;
            var (x1, y1, x2, y2) = DrawnLineEnds(shape);
            return x1 <= x2 ? new DrawnLine(shape, x1, y1, x2, y2, 0, false) : new DrawnLine(shape, x2, y2, x1, y1, 0, false);
        }
        if (!shape.Geometry.Equals("rect", StringComparison.OrdinalIgnoreCase) || shape.IsConnector || shape.IsTextBox) return null;
        // A bar turned a half turn is still a bar; one turned any other way is not placed.
        if (Math.Abs(Math.IEEERemainder(shape.RotationDegrees, 180)) > 1) return null;
        var (left, top) = (bounds.XEmu / DrawnEmuPerPoint, bounds.YEmu / DrawnEmuPerPoint);
        var (width, height) = (bounds.WidthEmu / DrawnEmuPerPoint, bounds.HeightEmu / DrawnEmuPerPoint);
        if (width <= 0 || height < 0 || width < 4 * height) return null;
        return new DrawnLine(shape, left, top + height / 2, left + width, top + height / 2, height, true);
    }

    // The end points of a line shape, in points: the corners of its frame, mirrored as the shape is flipped
    // and then turned about the frame's center as the shape is rotated.
    private static (double X1, double Y1, double X2, double Y2) DrawnLineEnds(XlsxDrawingShapeRecord shape)
    {
        var bounds = shape.AbsoluteBounds!;
        var (width, height) = (bounds.WidthEmu / DrawnEmuPerPoint, bounds.HeightEmu / DrawnEmuPerPoint);
        var (centerX, centerY) = (bounds.XEmu / DrawnEmuPerPoint + width / 2, bounds.YEmu / DrawnEmuPerPoint + height / 2);
        var (x1, y1, x2, y2) = (-width / 2, -height / 2, width / 2, height / 2);
        if (shape.FlipHorizontal) (x1, x2) = (-x1, -x2);
        if (shape.FlipVertical) (y1, y2) = (-y1, -y2);
        var angle = shape.RotationDegrees * Math.PI / 180;
        var (sin, cos) = (Math.Sin(angle), Math.Cos(angle));
        return (centerX + x1 * cos - y1 * sin, centerY + x1 * sin + y1 * cos, centerX + x2 * cos - y2 * sin, centerY + x2 * sin + y2 * cos);
    }

    private static bool IsDrawnWideCharacter(char character) =>
        character is >= 'ᄀ' and <= 'ᅟ' or >= '⺀' and <= '꓏' or >= '가' and <= '힣' or
            >= '豈' and <= '﫿' or >= '︰' and <= '﹏' or >= '＀' and <= '｠' or >= '￠' and <= '￦' ||
        char.IsHighSurrogate(character);

    private const string DrawnNarrowCharacters = " !'()-.,/:;I[]\\fijlrt|";

    // A typical width: full-width characters an em, others a little over half an em, the narrow ones of
    // Latin text (a space, punctuation, i j l t f r) a third of one. Within a tenth of what Arial and Calibri
    // give for ordinary words, numbers and sentences.
    private static double DrawnTextWidth(string text, double size) =>
        text.Sum(character => char.IsControl(character) || char.IsLowSurrogate(character) ? 0
            : IsDrawnWideCharacter(character) ? 1.0 : DrawnNarrowCharacters.Contains(character, StringComparison.Ordinal) ? 0.3 : 0.55) * size;


    // Where the text of a cell lies, or null for a cell whose text is not shown or not placed (rotated,
    // in a hidden row or column).
    private static DrawnTextBox? BuildDrawnTextBox(XlsxCellRecord cell, CellTextStyle style, DrawnLineGrid grid,
        IReadOnlyDictionary<int, List<int>> occupied)
    {
        var shown = cell.DisplayValue ?? cell.Value;
        if (string.IsNullOrWhiteSpace(shown) || style.Rotated || cell.IsHiddenRow || cell.IsHiddenColumn) return null;
        var size = style.FontSize;
        var firstColumn = cell.ColumnIndex;
        var lastColumn = Math.Max(cell.MergedToColumn ?? firstColumn, cell.CenterAcrossToColumn ?? firstColumn);
        var lastRow = Math.Max(cell.RowIndex, cell.MergedToRow ?? cell.RowIndex);
        var (top, bottom) = (grid.RowStart(cell.RowIndex), grid.RowStart(lastRow + 1));
        var (cellLeft, cellRight) = (grid.ColumnStart(firstColumn), grid.ColumnStart(lastColumn + 1));
        if (bottom - top < 1 || cellRight - cellLeft < 1) return null;

        var numeric = cell.CellType is null or "n" && double.TryParse(cell.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        var alignment = style.Horizontal switch
        {
            "left" or "justify" => DrawnAlignment.Left,
            "right" => DrawnAlignment.Right,
            "center" or "centerContinuous" => DrawnAlignment.Center,
            "fill" or "distributed" => DrawnAlignment.Fill,
            _ => numeric ? DrawnAlignment.Right : DrawnAlignment.Left,
        };
        var indent = style.Indent * DrawnIndentEm * size;
        var inner = cellRight - cellLeft - 2 * DrawnCellPadding - (alignment is DrawnAlignment.Left or DrawnAlignment.Right ? indent : 0);

        // The lines the text takes. Text that wraps, or may, is more than one line unless it is clearly
        // shorter than the cell; a line break shows as a line break only in a wrapping cell.
        var paragraphs = style.Wrap
            ? shown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
            : [new string(shown.Where(character => character is not ('\r' or '\n')).ToArray())];
        var lineCount = 0;
        foreach (var paragraph in paragraphs)
        {
            var paragraphWidth = DrawnTextWidth(paragraph, size);
            lineCount += !style.Wrap || paragraphWidth <= 0.9 * inner ? 1 : Math.Max(2, (int)Math.Ceiling(paragraphWidth / Math.Max(inner, 1)));
        }
        var isSingleLine = lineCount == 1;

        var pitch = DrawnLinePitchEm * size;
        var baselines = new double[lineCount];
        var first = style.Vertical switch
        {
            "center" => (top + bottom) / 2 + DrawnCenterBaselineEm * size - (lineCount - 1) * pitch / 2,
            "top" or "justify" or "distributed" => top + DrawnTopBaselineEm * size,
            _ => bottom - DrawnBottomBaselineEm * size - (lineCount - 1) * pitch,
        };
        for (var line = 0; line < lineCount; line++) baselines[line] = first + line * pitch;

        if (!isSingleLine)
            return new(size, bottom, cellLeft + DrawnCellPadding, cellRight - DrawnCellPadding, 0, 0, baselines, false);

        // One line: its extent follows from the alignment and the estimated width. Text may run on into the
        // empty cells beside its own (numbers and wrapped, merged or filled cells do not), and is clipped at
        // the first cell that holds something.
        var width = DrawnTextWidth(paragraphs[0], size);
        var (clipLeft, clipRight) = (cellLeft, cellRight);
        var mayRunOn = !numeric && cell.MergedToColumn is null && cell.MergedToRow is null && cell.CenterAcrossToColumn is null &&
            alignment != DrawnAlignment.Fill;
        double start, end, leftShare, rightShare;
        switch (alignment)
        {
            case DrawnAlignment.Right:
                end = cellRight - DrawnCellPadding - indent;
                (start, leftShare, rightShare) = (end - width, 1, 0);
                if (mayRunOn) clipLeft = DrawnNeighborEdge(occupied, cell.RowIndex, firstColumn, grid, towardsLeft: true) ?? double.NegativeInfinity;
                break;
            case DrawnAlignment.Center:
                var middle = (cellLeft + cellRight) / 2;
                (start, end, leftShare, rightShare) = (middle - width / 2, middle + width / 2, 0.5, 0.5);
                if (mayRunOn)
                {
                    clipLeft = DrawnNeighborEdge(occupied, cell.RowIndex, firstColumn, grid, towardsLeft: true) ?? double.NegativeInfinity;
                    clipRight = DrawnNeighborEdge(occupied, cell.RowIndex, lastColumn, grid, towardsLeft: false) ?? double.PositiveInfinity;
                }
                break;
            case DrawnAlignment.Fill:
                (start, end, leftShare, rightShare) = (cellLeft + DrawnCellPadding, cellRight - DrawnCellPadding, 0, 0);
                break;
            default:
                start = cellLeft + DrawnCellPadding + indent;
                (end, leftShare, rightShare) = (start + width, 0, 1);
                if (mayRunOn) clipRight = DrawnNeighborEdge(occupied, cell.RowIndex, lastColumn, grid, towardsLeft: false) ?? double.PositiveInfinity;
                break;
        }
        var (visibleLeft, visibleRight) = (Math.Max(start, clipLeft), Math.Min(end, clipRight));
        if (visibleRight - visibleLeft <= 0) return null;
        // An end the text is clipped at is exactly where the cell or its neighbor is; the other end depends on
        // the estimated width.
        var leftSlack = visibleLeft > start ? 0 : DrawnWidthError * width * leftShare;
        var rightSlack = visibleRight < end ? 0 : DrawnWidthError * width * rightShare;
        return new(size, bottom, visibleLeft, visibleRight, leftSlack, rightSlack, baselines, true);
    }

    // The edge of the nearest cell that holds something on one side of a text cell (the text is clipped
    // there), or null when there is none.
    private static double? DrawnNeighborEdge(IReadOnlyDictionary<int, List<int>> occupied, int row, int column, DrawnLineGrid grid, bool towardsLeft)
    {
        if (!occupied.TryGetValue(row, out var columns)) return null;
        var index = columns.BinarySearch(column);
        if (index < 0) index = ~index;
        else if (!towardsLeft) index++;
        if (towardsLeft)
        {
            // The column before the first one at or after the cell's own.
            return index > 0 ? grid.ColumnStart(columns[index - 1] + 1) : null;
        }
        return index < columns.Count ? grid.ColumnStart(columns[index]) : null;
    }

    // Whether the line decorates the text, and how: in the strike or underline zone of one of its lines,
    // over it for long enough, and covering all of a single line of text.
    private static (DrawnKind Kind, bool Whole)? MatchDrawnLine(DrawnLine line, DrawnTextBox box)
    {
        var size = box.Size;
        if (line.Right - line.Left < DrawnMinLengthEm * size || line.Rise > DrawnMaxThicknessEm * size) return null;
        if (line.IsBar && line.Thickness > Math.Max(1.5, DrawnMaxThicknessEm * size)) return null;
        if (line.Y > box.Bottom - DrawnEdgeMargin) return null;
        DrawnKind? kind = null;
        foreach (var baseline in box.Baselines)
        {
            var em = (baseline - line.Y) / size;
            if (em < DrawnUnderlineLowEm - DrawnZoneSlackEm || em > DrawnStrikeToEm + DrawnZoneSlackEm) continue;
            kind = em >= DrawnStrikeFromEm ? DrawnKind.Strike : DrawnKind.Underline;
            break;
        }
        if (kind is null) return null;
        var overlap = Math.Min(line.Right, box.Right) - Math.Max(line.Left, box.Left);
        if (overlap < Math.Min(DrawnOverlapEm * size, (box.Right - box.Left) / 2)) return null;
        var whole = box.IsSingleLine && line.Left <= box.Left + DrawnReachEm * size + box.LeftSlack &&
                    line.Right >= box.Right - DrawnReachEm * size - box.RightSlack;
        return (kind.Value, whole);
    }

    // Whether an end of the line meets another shape or line: it is then a connector or a rule between
    // things, not a decoration of text. The other line of a double line runs close beside this one over the
    // same span without being part of it.
    private static bool DrawnLineMeetsAnotherShape(DrawnLine line, double size, IReadOnlyList<XlsxDrawingShapeRecord> shapes,
        IReadOnlyDictionary<XlsxDrawingShapeRecord, DrawnLine> shapeLines)
    {
        foreach (var other in shapes)
        {
            if (line.Shape == other || other.IsHidden || other.IsUnpainted || other.AbsoluteBounds is not { } bounds) continue;
            if (shapeLines.TryGetValue(other, out var twin) && IsDrawnTwin(line, twin, size)) continue;
            if (DrawnEndMeets(line.Left, line.LeftY, other, bounds) || DrawnEndMeets(line.Right, line.RightY, other, bounds)) return true;
        }
        return false;
    }

    // The other line of a double strikethrough or double underline: as thin, over the same span, a little
    // above or below (it is within a touch of this line, or it would not matter). A line on the same level
    // that continues this one, or a bar thick enough to be something else, is not a twin.
    private static bool IsDrawnTwin(DrawnLine line, DrawnLine other, double size) =>
        other.Thickness <= DrawnMaxThicknessEm * size &&
        Math.Abs(other.Left - line.Left) <= 0.2 * size && Math.Abs(other.Right - line.Right) <= 0.2 * size;

    private static bool DrawnEndMeets(double x, double y, XlsxDrawingShapeRecord other, XlsxDrawingBounds bounds)
    {
        if (IsDrawnStraightLine(other))
        {
            var (x1, y1, x2, y2) = DrawnLineEnds(other);
            return DrawnDistanceToSegment(x, y, x1, y1, x2, y2) <= DrawnTouchDistance;
        }
        // Any other shape is judged by the outline of its frame.
        var (left, top) = (bounds.XEmu / DrawnEmuPerPoint, bounds.YEmu / DrawnEmuPerPoint);
        var (right, bottom) = (left + bounds.WidthEmu / DrawnEmuPerPoint, top + bounds.HeightEmu / DrawnEmuPerPoint);
        var edges = new[] { (left, top, right, top), (left, bottom, right, bottom), (left, top, left, bottom), (right, top, right, bottom) };
        return edges.Any(edge => DrawnDistanceToSegment(x, y, edge.Item1, edge.Item2, edge.Item3, edge.Item4) <= DrawnTouchDistance);
    }

    private static double DrawnDistanceToSegment(double x, double y, double x1, double y1, double x2, double y2)
    {
        var (dx, dy) = (x2 - x1, y2 - y1);
        var length = dx * dx + dy * dy;
        var t = length <= 0 ? 0 : Math.Clamp(((x - x1) * dx + (y - y1) * dy) / length, 0, 1);
        return Math.Sqrt(Math.Pow(x - (x1 + t * dx), 2) + Math.Pow(y - (y1 + t * dy), 2));
    }

    // The cell nodes with what the drawn lines decorate written on them: "strike" or "underline" when a line
    // covers all of the shown text (each replaces the struck or underlined ranges the cell's runs may have
    // had, which it includes), and for a strike over part of it the review marker, since which characters are struck is
    // not known. The review marker is ReadableMarkdownSerializer.SourceReviewRequiredExtension, which this
    // project does not reference; its key is shared through ReviewExtensionKeys.
    private static List<DocumentNode> ApplyDrawnLineDecorations(List<DocumentNode> nodes, DrawnLineDecorations decorations)
    {
        if (decorations.Cells.Count == 0) return nodes;
        return nodes.Select(node =>
        {
            if (node.Kind != NodeKind.Cell || node.Extensions is not { } extensions ||
                !extensions.TryGetValue("row", out var row) || !row.TryGetInt32(out var rowIndex) ||
                !extensions.TryGetValue("column", out var column) || !column.TryGetInt32(out var columnIndex) ||
                !decorations.Cells.TryGetValue((rowIndex, columnIndex), out var decoration)) return node;
            var changed = new Dictionary<string, JsonElement>(extensions, StringComparer.Ordinal);
            if (decoration.Strike)
            {
                changed["strike"] = JsonSerializer.SerializeToElement(true);
                changed.Remove("strike_ranges");
            }
            if (decoration.Underline)
            {
                changed["underline"] = JsonSerializer.SerializeToElement(true);
                changed.Remove("underline_ranges");
            }
            if (decoration.StrikeUnplaced)
            {
                changed[StrikeUnplacedExtension] = JsonSerializer.SerializeToElement(true);
                changed[ReviewExtensionKeys.SourceReviewRequired] = JsonSerializer.SerializeToElement(true);
            }
            return node with { Extensions = changed };
        }).ToList();
    }

    private static string? DrawnLineWarning(string sheetName, DrawnLineDecorations decorations)
    {
        var unplaced = decorations.Cells.Where(item => item.Value.StrikeUnplaced).Select(item => item.Key)
            .OrderBy(key => key.Row).ThenBy(key => key.Column).ToArray();
        if (unplaced.Length == 0) return null;
        var addresses = string.Join(", ", unplaced.Take(10).Select(key => ColumnName(key.Column) + key.Row.ToString(CultureInfo.InvariantCulture)));
        return string.Create(CultureInfo.InvariantCulture,
            $"XlsxStrikethroughUnplaced: A drawn line crosses part of the text in {unplaced.Length} cell(s) ({addresses}{(unplaced.Length > 10 ? ", ..." : "")}); " +
            $"which characters it strikes could not be determined, so the text is output without the mark. Compare with the original workbook. (worksheet: {sheetName})");
    }
}
