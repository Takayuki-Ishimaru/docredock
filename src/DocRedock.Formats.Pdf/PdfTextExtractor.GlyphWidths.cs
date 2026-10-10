using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocRedock.Formats.Pdf;

// Glyph widths, so that text is placed where a viewer draws it. A shown string's width and where the
// text after it continues used to be estimated at 6 units a character whatever the font, its size and
// the spacing in force: a line whose runs a producer places one by one (LibreOffice gives every change
// of format its own show at its exact position) split wherever the estimate overshot the next run, and
// a strikethrough over part of a show could not be placed. A font that does not give its widths - one
// of the standard 14 without /Widths, a vertical font, a composite font with a CMap other than
// Identity-H, a font the extractor cannot read - keeps the estimate, exactly as before.
public static partial class PdfTextExtractor
{
    /// <summary>The glyph widths a font dictionary gives: how far each character code moves the text
    /// position for a font size of 1, in text space units (a simple font's <c>/Widths</c> and a
    /// CIDFont's <c>/W</c> are thousandths of that; a Type3 font's <c>/FontMatrix</c> scales its
    /// <c>/Widths</c>). A code is <see cref="CodeLength"/> bytes: one in a simple font, two in a
    /// composite font encoded Identity-H, whose codes are CIDs.</summary>
    private sealed class PdfGlyphWidths
    {
        private readonly double[]? byCode;
        private readonly Dictionary<int, double>? byCid;
        private readonly double missing;
        private readonly double scale;

        private PdfGlyphWidths(int codeLength, double[]? byCode, Dictionary<int, double>? byCid, double missing, double scale)
        {
            CodeLength = codeLength;
            this.byCode = byCode;
            this.byCid = byCid;
            this.missing = missing;
            this.scale = scale;
        }

        public int CodeLength { get; }

        /// <summary>How far <paramref name="code"/> moves the text position for a font size of 1.</summary>
        public double Width(int code) => scale * (byCode is not null
            ? code is >= 0 and < 256 ? byCode[code] : missing
            : byCid!.TryGetValue(code, out var width) ? width : missing);

        public static PdfGlyphWidths Simple(double[] byCode, double missing, double scale) => new(1, byCode, null, missing, scale);

        public static PdfGlyphWidths Composite(Dictionary<int, double> byCid, double missing) => new(2, null, byCid, missing, 0.001);
    }

    /// <summary>How many CID widths a <c>/W</c> array may assign in all, its ranges counted code by
    /// code. An Identity-H code is two bytes, so a real font assigns at most 65,536; a larger array
    /// is malformed and the font keeps the estimate.</summary>
    private const int MaxCidWidthAssignments = 4 * 65_536;

    /// <summary>The widths a font dictionary gives, or null when it gives none the extractor follows.</summary>
    private static PdfGlyphWidths? ReadGlyphWidths(IReadOnlyList<(string Key, string Value)> font, IReadOnlyDictionary<int, string> objects)
    {
        switch (ReadName(EntryValue(font, "Subtype")))
        {
            case "Type1" or "MMType1" or "TrueType":
                return ReadSimpleWidths(font, objects, 0.001);
            case "Type3":
                // Glyph space is mapped to text space by /FontMatrix; only a plain horizontal scale is followed.
                return ReadNumbers(ResolveValue(EntryValue(font, "FontMatrix"), objects)) is [var a, var b, _, _, _, _] && a > 0 && b == 0
                    ? ReadSimpleWidths(font, objects, a)
                    : null;
            case "Type0":
                return ReadCompositeWidths(font, objects);
            default:
                return null;
        }
    }

    private static PdfGlyphWidths? ReadSimpleWidths(IReadOnlyList<(string Key, string Value)> font, IReadOnlyDictionary<int, string> objects, double scale)
    {
        // The standard 14 fonts may leave their widths out; their metrics are not built in here.
        if (ReadNumbers(ResolveValue(EntryValue(font, "Widths"), objects)) is not { Count: > 0 } widths) return null;
        var firstChar = ResolveValue(EntryValue(font, "FirstChar"), objects);
        var first = 0;
        if (firstChar is not null)
        {
            if (ReadNumber(firstChar) is not { } value || value < 0 || value > 255 || value != Math.Floor(value)) return null;
            first = (int)value;
        }
        var missing = ResolveDictionary(EntryValue(font, "FontDescriptor"), objects) is { } descriptor &&
            ReadNumber(ResolveValue(EntryValue(ReadDictionaryEntries(descriptor), "MissingWidth"), objects)) is { } declared
                ? declared
                : 0;
        var byCode = new double[256];
        Array.Fill(byCode, missing);
        for (var index = 0; index < widths.Count && first + index < byCode.Length; index++) byCode[first + index] = widths[index];
        return PdfGlyphWidths.Simple(byCode, missing, scale);
    }

    private static PdfGlyphWidths? ReadCompositeWidths(IReadOnlyList<(string Key, string Value)> font, IReadOnlyDictionary<int, string> objects)
    {
        // Identity-H codes are CIDs written across the page. Another CMap maps codes to CIDs through
        // tables that are not read here, and a vertical font advances down the page, not across it.
        if (ReadName(ResolveValue(EntryValue(font, "Encoding"), objects)) != "Identity-H") return null;
        if (DescendantFont(EntryValue(font, "DescendantFonts"), objects) is not { } descendant) return null;
        var entries = ReadDictionaryEntries(descendant);
        var missing = ReadNumber(ResolveValue(EntryValue(entries, "DW"), objects)) ?? 1000;
        var byCid = new Dictionary<int, double>();
        if (ResolveValue(EntryValue(entries, "W"), objects) is { } widths && !ReadCidWidths(widths, byCid)) return null;
        return PdfGlyphWidths.Composite(byCid, missing);
    }

    /// <summary>The CIDFont a Type0 font's <c>/DescendantFonts</c> array (inline or referenced) names first.</summary>
    private static string? DescendantFont(string? value, IReadOnlyDictionary<int, string> objects)
    {
        var array = ResolveValue(value, objects)?.TrimStart();
        if (array is null || !array.StartsWith('[')) return null;
        var first = SkipSpace(array, 1);
        return first < array.Length && array[first] != ']' ? ResolveDictionary(array[first..SkipValue(array, first)], objects) : null;
    }

    /// <summary>Reads a <c>/W</c> array into <paramref name="widths"/>: <c>c [w1 w2 ...]</c> gives c,
    /// c+1, ... their own widths and <c>first last w</c> gives the whole range one; a later entry
    /// overrides an earlier one, as viewers read it. False when the array holds anything else or
    /// assigns more than <see cref="MaxCidWidthAssignments"/> widths.</summary>
    private static bool ReadCidWidths(string text, Dictionary<int, double> widths)
    {
        var index = SkipSpace(text, 0);
        if (index >= text.Length || text[index] != '[') return false;
        index++;
        var pending = new List<double>(3);
        var assignments = 0;
        // width receives how far the CID is from first.
        bool Assign(double first, double last, Func<double, double> width)
        {
            if (first != Math.Floor(first) || last != Math.Floor(last) || last < first) return false;
            // Codes are two bytes; a CID outside them is never shown and needs no width.
            var from = (int)Math.Clamp(first, 0, ushort.MaxValue + 1.0);
            var to = (int)Math.Clamp(last, -1, ushort.MaxValue);
            for (var cid = from; cid <= to; cid++)
            {
                if (++assignments > MaxCidWidthAssignments) return false;
                widths[cid] = width(cid - first);
            }
            return true;
        }
        while (true)
        {
            index = SkipSpace(text, index);
            if (index >= text.Length) return false;
            var character = text[index];
            if (character == ']') return pending.Count == 0;
            if (character == '[')
            {
                // c [w1 w2 ...]
                var end = SkipBracketed(text, index);
                if (pending.Count != 1 || ReadNumbers(text[index..end]) is not { } list) return false;
                if (list.Count > 0 && !Assign(pending[0], pending[0] + list.Count - 1, offset => list[(int)offset])) return false;
                pending.Clear();
                index = end;
                continue;
            }
            var tokenEnd = NameEnd(text, index);
            if (tokenEnd == index || ReadNumber(text[index..tokenEnd]) is not { } number) return false;
            pending.Add(number);
            index = tokenEnd;
            if (pending.Count < 3) continue;
            // first last w
            var shared = pending[2];
            if (!Assign(pending[0], pending[1], _ => shared)) return false;
            pending.Clear();
        }
    }

    /// <summary>The value an entry stands for: the referenced object's body when it is an indirect
    /// reference, otherwise the value as written.</summary>
    private static string? ResolveValue(string? value, IReadOnlyDictionary<int, string> objects) =>
        value is not null && TryReadReference(value, out var id) ? objects.GetValueOrDefault(id)?.Trim() : value;

    /// <summary>The numbers of an array value, or null when it is not an array of numbers only (a
    /// reference among them, say).</summary>
    private static List<double>? ReadNumbers(string? text)
    {
        if (text is null) return null;
        var index = SkipSpace(text, 0);
        if (index >= text.Length || text[index] != '[') return null;
        var numbers = new List<double>();
        for (index++; ;)
        {
            index = SkipSpace(text, index);
            if (index >= text.Length) return null;
            if (text[index] == ']') return numbers;
            var end = NameEnd(text, index);
            if (end == index || ReadNumber(text[index..end]) is not { } number) return null;
            numbers.Add(number);
            index = end;
        }
    }

    /// <summary>A number written on its own, or null.</summary>
    private static double? ReadNumber(string? text) =>
        text is not null && double.TryParse(text.AsSpan().Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

    /// <summary>A show measured with its font's glyph widths, in text space from where the show
    /// starts: the characters it shows and where each is drawn along the baseline, the extent of all
    /// its glyphs, and how far it moves the text position (glyphs, spacing and TJ adjustments).</summary>
    private sealed record PdfMeasuredShow(string Text, (double Start, double End)[] Characters, double Left, double Right, double Advance);

    /// <summary>The strings and TJ numbers of a show's operand, in order; a string is matched as the
    /// text parser matches it, so the characters measured are the characters decoded.</summary>
    private const string ShowPartPattern = @"(?<string>\((?:\\.|[^\\)])*\)|<[0-9A-Fa-f\s]+>)|(?<number>[-+]?(?:\d+(?:\.\d*)?|\.\d+))";

    /// <summary>Measures a string or TJ array: each glyph moves the text position by
    /// (w × Tf + Tc + Tw for the single-byte code 32) × Tz, and each TJ number moves it back by that
    /// many thousandths of the font size. Null when the numbers overflow.</summary>
    private static PdfMeasuredShow? MeasureShow(string operand, PdfGlyphWidths widths, PdfToUnicodeMap? map,
        PdfTextShow show, double size, Regex parts)
    {
        var (charSpacing, wordSpacing, horizontalScale) = (show.CharSpacing, show.WordSpacing, show.HorizontalScale);
        var text = new StringBuilder();
        var characters = new List<(double Start, double End)>();
        var sources = new List<(int Start, int End)>();
        double position = 0, left = double.PositiveInfinity, right = double.NegativeInfinity;
        foreach (Match part in parts.Matches(operand))
        {
            if (!part.Groups["string"].Success)
            {
                if (InvariantNumber(part.Value) is { } adjustment) position -= adjustment / 1000 * size * horizontalScale;
                continue;
            }
            var bytes = StringBytes(part.Value);
            var extents = new (double Start, double End)[bytes.Length];
            for (var at = 0; at < bytes.Length; at += widths.CodeLength)
            {
                var length = Math.Min(widths.CodeLength, bytes.Length - at);
                var code = 0;
                for (var offset = 0; offset < length; offset++) code = code << 8 | bytes[at + offset];
                // Word spacing applies to the single-byte code 32 only, never to a two-byte code.
                var advance = (widths.Width(code) * size + charSpacing + (widths.CodeLength == 1 && code == 32 ? wordSpacing : 0)) * horizontalScale;
                for (var offset = 0; offset < length; offset++) extents[at + offset] = (position, position + advance);
                left = Math.Min(left, Math.Min(position, position + advance));
                right = Math.Max(right, Math.Max(position, position + advance));
                position += advance;
            }
            sources.Clear();
            var decoded = Decode(bytes, map, sources);
            // The characters one code decodes to - a ligature's letters, the two halves of a
            // supplementary character - are all drawn by its glyph, so each is placed over all of it
            // and a strikethrough takes them together.
            foreach (var (start, end) in sources) characters.Add((extents[start].Start, extents[end - 1].End));
            text.Append(decoded);
        }
        if (double.IsPositiveInfinity(left)) (left, right) = (0, 0);
        if (!double.IsFinite(position) || !double.IsFinite(left) || !double.IsFinite(right) ||
            characters.Any(character => !double.IsFinite(character.Start) || !double.IsFinite(character.End)))
            return null;
        return new PdfMeasuredShow(text.ToString(), characters.ToArray(), left, right, position);
    }
}
