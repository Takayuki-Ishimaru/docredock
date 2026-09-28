using DocRedock.Core.Documents;

namespace DocRedock.Formats.Pdf;

// One ordered pass over a page's expanded content that follows the graphics and text state the way
// a renderer does - q/Q, cm, the text matrices, the font, clipping, and optional content - and
// records, for every text-showing operator, which font it draws with and whether its glyphs can be
// seen at all. The regular-expression text parser keeps producing the text and its reading-order
// geometry; this pass answers only the two questions that parser cannot: a font set inside a form
// ends with the form, and text entirely outside a form's /BBox, a clipping path, or the page is not
// visible even though it is in the content stream.
public static partial class PdfTextExtractor
{
    /// <summary>Whether the glyphs of one text-showing operator can be visible.</summary>
    private enum PdfTextVisibility
    {
        Visible,
        /// <summary>Entirely outside the clipping region in force: a Form XObject's <c>/BBox</c>, a
        /// clipping path, or the page's crop box.</summary>
        Clipped,
        /// <summary>Inside optional content (a layer) that is off when the document opens.</summary>
        HiddenLayer,
        /// <summary>Inside optional content whose visibility could not be evaluated.</summary>
        UnknownLayer,
    }

    /// <summary>What the text-showing operator whose string operand starts at a given offset draws
    /// with: the font token in force (see <see cref="PdfFontRegistry"/>) and its visibility.</summary>
    private readonly record struct PdfTextShow(string? Font, PdfTextVisibility Visibility);

    private enum PdfContentEventKind { FormBegin, FormEnd }

    /// <summary>Where an inlined form starts or ends in the expanded content. A form's start carries
    /// its <c>/BBox</c> (in form space; the pass maps it through the CTM in force, which already
    /// includes the form's <c>/Matrix</c>) and the visibility of its own <c>/OC</c>.</summary>
    private readonly record struct PdfContentEvent(int Offset, PdfContentEventKind Kind, Geometry? BBox = null,
        PdfLayerVisibility Layer = PdfLayerVisibility.Visible);

    /// <summary>A page's content with every drawn form inlined and every font renamed to its token,
    /// plus what the inlining knew and the content alone no longer says: where each form begins and
    /// ends, and what each <c>/OC</c> marked-content operator (by offset) evaluates to.</summary>
    private sealed record PdfExpandedContent(string Content, IReadOnlyList<PdfContentEvent> Events,
        IReadOnlyDictionary<int, PdfLayerVisibility> MarkedContent);

    /// <summary>An axis-aligned rectangle in page space.</summary>
    private readonly record struct PdfBox(double Left, double Bottom, double Right, double Top)
    {
        public static PdfBox From(Geometry geometry) => new(geometry.X, geometry.Y, geometry.X + geometry.Width, geometry.Y + geometry.Height);

        public static PdfBox? Around(ReadOnlySpan<(double X, double Y)> points)
        {
            if (points.Length == 0) return null;
            double left = double.PositiveInfinity, bottom = double.PositiveInfinity, right = double.NegativeInfinity, top = double.NegativeInfinity;
            foreach (var (x, y) in points)
            {
                if (!double.IsFinite(x) || !double.IsFinite(y)) return null;
                left = Math.Min(left, x); right = Math.Max(right, x);
                bottom = Math.Min(bottom, y); top = Math.Max(top, y);
            }
            return new PdfBox(left, bottom, right, top);
        }

        public PdfBox Include(double x, double y) => new(Math.Min(Left, x), Math.Min(Bottom, y), Math.Max(Right, x), Math.Max(Top, y));

        /// <summary>The overlap of two rectangles; empty (never intersecting anything) when they do not overlap.</summary>
        public PdfBox Intersect(PdfBox other) => new(Math.Max(Left, other.Left), Math.Max(Bottom, other.Bottom),
            Math.Min(Right, other.Right), Math.Min(Top, other.Top));

        public bool Intersects(PdfBox other) => Left < other.Right && other.Left < Right && Bottom < other.Top && other.Bottom < Top;

        /// <summary>A clipping path this thin encloses nothing a reader could see; it is not trusted
        /// to narrow the visible area (a malformed path must not make text disappear).</summary>
        public bool IsDegenerate => !(Right - Left > 1e-6) || !(Top - Bottom > 1e-6);
    }

    /// <summary>The part of the graphics state this pass follows. Text state (font, spacing,
    /// scaling, leading, rise) belongs to the graphics state and is saved and restored by q/Q.</summary>
    private sealed class PdfShowState
    {
        public PdfMatrix Ctm = PdfMatrix.Identity;
        public PdfBox? Clip;
        public string? Font;
        public double? FontSize;
        public double CharSpacing;
        public double WordSpacing;
        public double HorizontalScale = 1;
        public double Leading;
        public double Rise;
        public PdfLayerVisibility Layer = PdfLayerVisibility.Visible;

        public PdfShowState Copy() => (PdfShowState)MemberwiseClone();
    }

    /// <summary>A string or array operand: where it starts (the offset the text parser's match
    /// starts at), how many bytes of text it shows, how many of them are single-byte spaces, and the
    /// sums of its positive and negative <c>TJ</c> adjustments.</summary>
    private readonly record struct PdfTextOperand(int Start, int Bytes, int Spaces, double PositiveAdjustment, double NegativeAdjustment);

    /// <summary>A glyph is assumed no wider than this many ems when deciding that text lies outside
    /// the visible area. Generous on purpose: overestimating a run can only keep text that is in
    /// fact clipped, never drop text that can be seen.</summary>
    private const double MaxGlyphAdvanceEm = 1.25;

    private static IReadOnlyDictionary<int, PdfTextShow> ScanTextShows(PdfExpandedContent expanded, Geometry? pageBox,
        IReadOnlyDictionary<string, PdfFontBinding> fonts)
    {
        var content = expanded.Content;
        var events = expanded.Events;
        var shows = new Dictionary<int, PdfTextShow>();
        // A page that shows no text has nothing to classify.
        if (!content.Contains("Tj", StringComparison.Ordinal) && !content.Contains("TJ", StringComparison.Ordinal) &&
            !content.Contains('\'') && !content.Contains('"'))
            return shows;
        var state = new PdfShowState { Clip = pageBox is null ? null : PdfBox.From(pageBox) };
        var saved = new Stack<PdfShowState>();
        // Marked content is not part of the graphics state; a form's own sequences are kept inside
        // the form, so an EMC it never opened cannot close the page's layer.
        var marked = new List<PdfLayerVisibility>();
        var formMarkedDepths = new Stack<int>();
        var textMatrix = PdfMatrix.Identity;
        var lineMatrix = PdfMatrix.Identity;
        // How far along the writing direction the next glyph may be from textMatrix's origin: every
        // shown string moves it by an amount only bounded here, since glyph widths are not read.
        double advanceLow = 0, advanceHigh = 0;
        PdfBox? path = null;
        var clipPending = false;
        var operands = new List<double>();
        PdfTextOperand? text = null;
        string? name = null;
        var nextEvent = 0;

        void ApplyEvent(PdfContentEvent contentEvent)
        {
            if (contentEvent.Kind == PdfContentEventKind.FormEnd)
            {
                if (formMarkedDepths.TryPop(out var depth) && marked.Count > depth) marked.RemoveRange(depth, marked.Count - depth);
                return;
            }
            formMarkedDepths.Push(marked.Count);
            if (contentEvent.BBox is { } box && Map(state.Ctm, box) is { IsDegenerate: false } mapped)
                state.Clip = state.Clip is { } clip ? clip.Intersect(mapped) : mapped;
            state.Layer = Combine(state.Layer, contentEvent.Layer);
        }

        void SetLine(PdfMatrix matrix)
        {
            lineMatrix = matrix;
            textMatrix = matrix;
            advanceLow = advanceHigh = 0;
        }

        void Show()
        {
            if (text is not { } operand) return;
            var layer = marked.Aggregate(state.Layer, Combine);
            var vertical = state.Font is { } font && fonts.TryGetValue(font, out var binding) && binding.Vertical;
            var run = RunExtent(operand, state, vertical);
            var visibility = layer == PdfLayerVisibility.Hidden ? PdfTextVisibility.HiddenLayer
                : run is { } extent && state.Clip is { } clip && GlyphBox(extent, state, textMatrix, advanceLow, advanceHigh, vertical) is { } glyphs &&
                  !glyphs.Intersects(clip) ? PdfTextVisibility.Clipped
                : layer == PdfLayerVisibility.Unknown ? PdfTextVisibility.UnknownLayer
                : PdfTextVisibility.Visible;
            shows[operand.Start] = new PdfTextShow(state.Font, visibility);
            if (run is { } advance)
            {
                advanceLow += advance.Low;
                advanceHigh += advance.High;
            }
        }

        for (var index = 0; index < content.Length;)
        {
            while (nextEvent < events.Count && events[nextEvent].Offset <= index) ApplyEvent(events[nextEvent++]);
            var character = content[index];
            if (character == '%')
            {
                while (index < content.Length && content[index] is not '\r' and not '\n') index++;
                continue;
            }
            if (character == '(')
            {
                var end = SkipLiteralString(content, index);
                var (bytes, spaces) = CountLiteralBytes(content, index + 1, end - 1);
                text = new PdfTextOperand(index, bytes, spaces, 0, 0);
                index = end;
                continue;
            }
            if (character == '<')
            {
                if (index + 1 < content.Length && content[index + 1] == '<') { index += 2; continue; }
                var end = SkipHexString(content, index);
                var (bytes, spaces) = CountHexBytes(content, index + 1, end - 1);
                text = new PdfTextOperand(index, bytes, spaces, 0, 0);
                index = end;
                continue;
            }
            if (character == '[')
            {
                var end = SkipBracketed(content, index);
                text = ReadArrayOperand(content, index, end);
                index = end;
                continue;
            }
            if (character == '/')
            {
                var nameEnd = NameEnd(content, index + 1);
                name = content[(index + 1)..nameEnd];
                index = nameEnd;
                continue;
            }
            if (char.IsWhiteSpace(character) || character is '>' or ']' or '{' or '}' or ')') { index++; continue; }
            var start = index;
            index = NameEnd(content, index);
            if (index == start) index++;
            var token = content[start..index];
            if (double.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                operands.Add(number);
                continue;
            }
            switch (token)
            {
                case "q": saved.Push(state.Copy()); break;
                case "Q": if (saved.TryPop(out var restored)) state = restored; break;
                case "cm" when operands.Count >= 6:
                    state.Ctm = state.Ctm.Concat(new PdfMatrix(operands[^6], operands[^5], operands[^4], operands[^3], operands[^2], operands[^1]));
                    break;
                case "BT": SetLine(PdfMatrix.Identity); break;
                case "Tf":
                    state.Font = name;
                    state.FontSize = operands.Count >= 1 ? operands[^1] : null;
                    break;
                case "Tc" when operands.Count >= 1: state.CharSpacing = operands[^1]; break;
                case "Tw" when operands.Count >= 1: state.WordSpacing = operands[^1]; break;
                case "Tz" when operands.Count >= 1: state.HorizontalScale = operands[^1] / 100; break;
                case "TL" when operands.Count >= 1: state.Leading = operands[^1]; break;
                case "Ts" when operands.Count >= 1: state.Rise = operands[^1]; break;
                case "Td" when operands.Count >= 2: SetLine(lineMatrix.Concat(Translation(operands[^2], operands[^1]))); break;
                case "TD" when operands.Count >= 2:
                    state.Leading = -operands[^1];
                    SetLine(lineMatrix.Concat(Translation(operands[^2], operands[^1])));
                    break;
                case "Tm" when operands.Count >= 6:
                    SetLine(new PdfMatrix(operands[^6], operands[^5], operands[^4], operands[^3], operands[^2], operands[^1]));
                    break;
                case "T*": SetLine(lineMatrix.Concat(Translation(0, -state.Leading))); break;
                case "Tj" or "TJ": Show(); break;
                case "'":
                    SetLine(lineMatrix.Concat(Translation(0, -state.Leading)));
                    Show();
                    break;
                case "\"":
                    if (operands.Count >= 2) (state.WordSpacing, state.CharSpacing) = (operands[^2], operands[^1]);
                    SetLine(lineMatrix.Concat(Translation(0, -state.Leading)));
                    Show();
                    break;
                case "m" or "l" when operands.Count >= 2: path = Extend(path, state.Ctm, operands[^2], operands[^1]); break;
                case "c" when operands.Count >= 6:
                    for (var point = 6; point >= 2; point -= 2) path = Extend(path, state.Ctm, operands[^point], operands[^(point - 1)]);
                    break;
                case "v" or "y" when operands.Count >= 4:
                    for (var point = 4; point >= 2; point -= 2) path = Extend(path, state.Ctm, operands[^point], operands[^(point - 1)]);
                    break;
                case "re" when operands.Count >= 4:
                    var (x, y, width, height) = (operands[^4], operands[^3], operands[^2], operands[^1]);
                    path = Extend(path, state.Ctm, x, y);
                    path = Extend(path, state.Ctm, x + width, y);
                    path = Extend(path, state.Ctm, x + width, y + height);
                    path = Extend(path, state.Ctm, x, y + height);
                    break;
                case "W" or "W*": clipPending = true; break;
                case "n" or "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*":
                    // A clipping path takes effect when the path is ended; its bounding box can
                    // only overstate the region it keeps, so text outside the box is clipped.
                    if (clipPending && path is { IsDegenerate: false } clipPath)
                        state.Clip = state.Clip is { } clip ? clip.Intersect(clipPath) : clipPath;
                    path = null;
                    clipPending = false;
                    break;
                case "BI": index = SkipInlineImageData(content, index); break;
                case "BDC": marked.Add(expanded.MarkedContent.TryGetValue(start, out var layer) ? layer : PdfLayerVisibility.Visible); break;
                case "BMC": marked.Add(PdfLayerVisibility.Visible); break;
                case "EMC":
                    if (marked.Count > (formMarkedDepths.Count > 0 ? formMarkedDepths.Peek() : 0)) marked.RemoveAt(marked.Count - 1);
                    break;
            }
            operands.Clear();
            text = null;
            name = null;
        }
        return shows;
    }

    private static PdfMatrix Translation(double x, double y) => new(1, 0, 0, 1, x, y);

    /// <summary>Hidden wins over unknown, and unknown over visible: content nested in a hidden
    /// layer is hidden whatever the inner layer says.</summary>
    private static PdfLayerVisibility Combine(PdfLayerVisibility outer, PdfLayerVisibility inner) =>
        outer == PdfLayerVisibility.Hidden || inner == PdfLayerVisibility.Hidden ? PdfLayerVisibility.Hidden
        : outer == PdfLayerVisibility.Unknown || inner == PdfLayerVisibility.Unknown ? PdfLayerVisibility.Unknown
        : PdfLayerVisibility.Visible;

    private static PdfBox? Map(PdfMatrix matrix, Geometry box)
    {
        var right = box.X + box.Width;
        var top = box.Y + box.Height;
        return PdfBox.Around([matrix.Apply(box.X, box.Y), matrix.Apply(right, box.Y), matrix.Apply(right, top), matrix.Apply(box.X, top)]);
    }

    private static PdfBox? Extend(PdfBox? path, PdfMatrix ctm, double x, double y)
    {
        var (pageX, pageY) = ctm.Apply(x, y);
        if (!double.IsFinite(pageX) || !double.IsFinite(pageY)) return path;
        return path is { } box ? box.Include(pageX, pageY) : new PdfBox(pageX, pageY, pageX, pageY);
    }

    /// <summary>How far one shown operand can move the text position along the writing direction,
    /// in text space: at least <c>Low</c> (glyphs of no width, spacing and backward adjustments
    /// only) and at most <c>High</c> (every glyph <see cref="MaxGlyphAdvanceEm"/> wide). Null when the
    /// font size is unknown or the state is one this pass does not try to bound (a negative size or
    /// scale mirrors the text), in which case the text is never judged clipped.</summary>
    private static (double Low, double High)? RunExtent(PdfTextOperand operand, PdfShowState state, bool vertical)
    {
        if (state.FontSize is not { } size || !double.IsFinite(size) || size <= 0) return null;
        var scale = vertical ? 1 : state.HorizontalScale;
        if (!double.IsFinite(scale) || scale <= 0) return null;
        // TJ numbers are thousandths of an em subtracted from the advance: horizontally a negative
        // number moves forward, vertically (where the advance itself is negative) a positive one.
        var forward = (vertical ? operand.PositiveAdjustment : operand.NegativeAdjustment) / 1000 * size;
        var backward = (vertical ? operand.NegativeAdjustment : operand.PositiveAdjustment) / 1000 * size;
        var low = (operand.Bytes * state.CharSpacing + operand.Spaces * state.WordSpacing - backward) * scale;
        var high = (operand.Bytes * (MaxGlyphAdvanceEm * size + Math.Max(0, state.CharSpacing)) +
            operand.Spaces * Math.Max(0, state.WordSpacing) + forward) * scale;
        return double.IsFinite(low) && double.IsFinite(high) ? (low, high) : null;
    }

    /// <summary>A rectangle in page space that holds every glyph the operand can paint, given that
    /// it starts somewhere in [<paramref name="low"/>, <paramref name="high"/>] along the writing
    /// direction from <paramref name="textMatrix"/>'s origin. Ascenders, descenders and side bearings
    /// get generous margins; the result only ever errs large.</summary>
    private static PdfBox? GlyphBox((double Low, double High) run, PdfShowState state, PdfMatrix textMatrix,
        double low, double high, bool vertical)
    {
        var size = state.FontSize!.Value;
        double left, right, bottom, top;
        if (vertical)
        {
            // Vertical glyphs hang below their origin, centered on it, and advance downward.
            left = -MaxGlyphAdvanceEm * size;
            right = MaxGlyphAdvanceEm * size;
            top = -(low + Math.Min(0, run.Low)) + 0.5 * size + state.Rise;
            bottom = -(high + Math.Max(0, run.High)) - MaxGlyphAdvanceEm * size + state.Rise;
        }
        else
        {
            var margin = 0.25 * size * state.HorizontalScale;
            left = low + Math.Min(0, run.Low) - margin;
            right = high + Math.Max(0, run.High) + margin;
            bottom = state.Rise - 0.5 * size;
            top = state.Rise + MaxGlyphAdvanceEm * size;
        }
        var toPage = state.Ctm.Concat(textMatrix);
        return PdfBox.Around([toPage.Apply(left, bottom), toPage.Apply(right, bottom), toPage.Apply(right, top), toPage.Apply(left, top)]);
    }

    /// <summary>Bytes and single-byte spaces a literal string (without its parentheses) shows.</summary>
    private static (int Bytes, int Spaces) CountLiteralBytes(string content, int start, int end)
    {
        var bytes = 0;
        var spaces = 0;
        for (var index = start; index < end; index++)
        {
            var character = content[index];
            if (character == '\\' && index + 1 < end)
            {
                var escaped = content[++index];
                if (escaped is '\r' or '\n')
                {
                    // A backslash before a line break continues the string; it shows nothing.
                    if (escaped == '\r' && index + 1 < end && content[index + 1] == '\n') index++;
                    continue;
                }
                if (escaped is >= '0' and <= '7')
                {
                    var value = escaped - '0';
                    for (var digits = 1; digits < 3 && index + 1 < end && content[index + 1] is >= '0' and <= '7'; digits++)
                        value = value * 8 + content[++index] - '0';
                    if (value == ' ') spaces++;
                }
                bytes++;
                continue;
            }
            if (character == ' ') spaces++;
            bytes++;
        }
        return (bytes, spaces);
    }

    private static (int Bytes, int Spaces) CountHexBytes(string content, int start, int end)
    {
        var digits = 0;
        var spaces = 0;
        var high = -1;
        for (var index = start; index < end; index++)
        {
            var value = HexDigitValue(content[index]);
            if (value < 0) continue;
            if (high < 0) { high = value; digits++; continue; }
            if (high * 16 + value == 0x20) spaces++;
            high = -1;
            digits++;
        }
        return ((digits + 1) / 2, spaces);
    }

    private static int HexDigitValue(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'A' and <= 'F' => character - 'A' + 10,
        >= 'a' and <= 'f' => character - 'a' + 10,
        _ => -1,
    };

    /// <summary>The strings and adjustments of a <c>TJ</c> array (or of any array, which the next
    /// operator then ignores unless it is <c>TJ</c>).</summary>
    private static PdfTextOperand ReadArrayOperand(string content, int start, int end)
    {
        int bytes = 0, spaces = 0;
        double positive = 0, negative = 0;
        for (var index = start + 1; index < end - 1;)
        {
            var character = content[index];
            if (character == '(')
            {
                var close = SkipLiteralString(content, index);
                var (count, blank) = CountLiteralBytes(content, index + 1, Math.Min(close, end) - 1);
                bytes += count; spaces += blank;
                index = close;
                continue;
            }
            if (character == '<')
            {
                var close = SkipHexString(content, index);
                var (count, blank) = CountHexBytes(content, index + 1, Math.Min(close, end) - 1);
                bytes += count; spaces += blank;
                index = close;
                continue;
            }
            if (char.IsWhiteSpace(character) || IsPdfDelimiter(character)) { index++; continue; }
            var tokenEnd = NameEnd(content, index);
            if (tokenEnd == index) tokenEnd++;
            if (double.TryParse(content.AsSpan(index, tokenEnd - index), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var adjustment) && double.IsFinite(adjustment))
            {
                if (adjustment > 0) positive += adjustment;
                else negative -= adjustment;
            }
            index = tokenEnd;
        }
        return new PdfTextOperand(start, bytes, spaces, positive, negative);
    }
}
