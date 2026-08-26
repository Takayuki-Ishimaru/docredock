using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;

namespace DocRedock.Formats.Pdf;

public sealed record PdfTextRegion(string Text, Geometry BoundingBox, int ReadingOrder);
public sealed record PdfPageText(int PageNumber, IReadOnlyList<PdfTextRegion> Regions)
{
    public string Text => string.Join("\n", Regions.OrderByDescending(region => region.BoundingBox.Y).ThenBy(region => region.BoundingBox.X).Select(region => region.Text));
}
public sealed record PdfExtractionResult(int PageCount, IReadOnlyList<PdfPageText> Pages)
{
    public string Text => string.Join("\n\n", Pages.Select(page => page.Text));
}

public sealed record PdfExtractionOptions(
    long MaxInputBytes = 134_217_728,
    int MaxPages = 10_000,
    int MaxObjects = 200_000,
    long MaxExpandedStreamBytes = 268_435_456,
    TimeSpan? RegexTimeout = null)
{
    public TimeSpan EffectiveRegexTimeout => RegexTimeout is { } value && value > TimeSpan.Zero
        ? value
        : TimeSpan.FromSeconds(1);
}

public sealed class PdfExtractionException : Exception
{
    public PdfExtractionException(string message) : base(message) { }
    public PdfExtractionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Conservative BCL-only extraction of text-showing PDF operators.</summary>
public static class PdfTextExtractor
{
    private const string PageMarkerPattern = @"/Type\s*/Page(?!s)\b";
    private const string ObjectPattern = @"\b\d+\s+\d+\s+obj\b";
    private const string TextStringPattern = @"\[(?:\s*(?:\((?:\\.|[^\\)])*\)|<[0-9A-Fa-f\s]+>|[-+]?\d+(?:\.\d+)?))*\s*\]|\((?:\\.|[^\\)])*\)|<[0-9A-Fa-f\s]+>";
    private const string LiteralPattern = @"\((?:\\.|[^\\)])*\)";
    private const string NumberPattern = @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)";

    public static PdfExtractionResult Extract(string path, PdfExtractionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new PdfExtractionOptions();
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("PDF input was not found.", path);
        if (info.Length > options.MaxInputBytes) throw new PdfExtractionException($"PDF input exceeds the {options.MaxInputBytes}-byte limit.");
        return Extract(File.ReadAllBytes(path), options);
    }

    public static PdfExtractionResult Extract(ReadOnlyMemory<byte> bytes, PdfExtractionOptions? options = null)
    {
        options ??= new PdfExtractionOptions();
        if (bytes.Length > options.MaxInputBytes) throw new PdfExtractionException($"PDF input exceeds the {options.MaxInputBytes}-byte limit.");
        var raw = bytes.ToArray();
        if (raw.Length < 5 || !raw.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) throw new PdfExtractionException("Input does not have a PDF header.");
        var latin = Encoding.Latin1.GetString(raw);
        if (!latin.Contains("%%EOF", StringComparison.Ordinal)) throw new PdfExtractionException("PDF end marker is missing.");
        // Object/page markers live outside streams. Excluding stream payloads avoids
        // running structural regular expressions over multi-megabyte embedded fonts
        // and images while retaining the original bounded stream parser below.
        var structure = StripStreamPayloads(latin);
        int pageCount;
        try
        {
            pageCount = Math.Max(1, Regex.Matches(structure, PageMarkerPattern, RegexOptions.Compiled, options.EffectiveRegexTimeout).Count);
            var objectCount = Regex.Matches(structure, ObjectPattern, RegexOptions.Compiled, options.EffectiveRegexTimeout).Count;
            if (objectCount > options.MaxObjects) throw new PdfExtractionException($"PDF object count exceeds the {options.MaxObjects}-object limit.");
        }
        catch (RegexMatchTimeoutException exception)
        {
            throw new PdfExtractionException("PDF structure matching exceeded its time limit.", exception);
        }
        if (pageCount > options.MaxPages) throw new PdfExtractionException($"PDF page count exceeds the {options.MaxPages}-page limit.");
        IReadOnlySet<int> contentObjectIds;
        try { contentObjectIds = ReadContentObjectIds(structure, options.EffectiveRegexTimeout); }
        catch (RegexMatchTimeoutException exception) { throw new PdfExtractionException("PDF content reference matching exceeded its time limit.", exception); }
        // Resolve the font encoding before reading page content.  A Type0 font
        // commonly stores glyph codes in the content stream and puts the actual
        // Unicode mapping in a separate ToUnicode CMap object.  Treating those
        // codes as Latin-1 is what turns Japanese text into punctuation.
        IReadOnlyDictionary<string, PdfToUnicodeMap> fontMaps;
        try { fontMaps = ReadFontMaps(raw, latin, structure, options); }
        catch (RegexMatchTimeoutException exception) { throw new PdfExtractionException("PDF font encoding matching exceeded its time limit.", exception); }
        var streams = ReadStreams(raw, latin, options, contentObjectIds).ToArray();
        var pages = new List<PdfPageText>();
        var streamPage = 1;
        foreach (var stream in streams)
        {
            var regions = ParseOperators(stream, options, fontMaps);
            if (regions.Count == 0) continue;
            pages.Add(new PdfPageText(Math.Min(streamPage++, pageCount), SortReadingOrder(regions)));
        }
        if (pages.Count == 0) pages.Add(new PdfPageText(1, Array.Empty<PdfTextRegion>()));
        while (pages.Count < pageCount) pages.Add(new PdfPageText(pages.Count + 1, Array.Empty<PdfTextRegion>()));
        return new PdfExtractionResult(pageCount, pages);
    }

    private static string StripStreamPayloads(string value)
    {
        var output = new StringBuilder(Math.Min(value.Length, 1_048_576));
        var cursor = 0;
        while (cursor < value.Length)
        {
            var stream = value.IndexOf("stream", cursor, StringComparison.Ordinal);
            if (stream < 0) { output.Append(value, cursor, value.Length - cursor); break; }
            output.Append(value, cursor, stream - cursor + "stream".Length);
            var payload = stream + "stream".Length;
            if (payload < value.Length && value[payload] == '\r') payload++;
            if (payload < value.Length && value[payload] == '\n') payload++;
            var end = value.IndexOf("endstream", payload, StringComparison.Ordinal);
            if (end < 0) { output.Append("\n"); break; }
            output.Append("\nendstream");
            cursor = end + "endstream".Length;
        }
        return output.ToString();
    }

    private static IReadOnlySet<int> ReadContentObjectIds(string structure, TimeSpan timeout)
    {
        var ids = new HashSet<int>();
        foreach (Match match in Regex.Matches(structure, @"/Contents\s+(?<id>\d+)\s+\d+\s+R\b", RegexOptions.None, timeout))
            if (int.TryParse(match.Groups["id"].Value, out var id)) ids.Add(id);
        foreach (Match array in Regex.Matches(structure, @"/Contents\s*\[(?<refs>[^\]]*)\]", RegexOptions.None, timeout))
            foreach (Match reference in Regex.Matches(array.Groups["refs"].Value, @"(?<id>\d+)\s+\d+\s+R\b", RegexOptions.None, timeout))
                if (int.TryParse(reference.Groups["id"].Value, out var id)) ids.Add(id);
        return ids;
    }

    private static IEnumerable<string> ReadStreams(byte[] bytes, string latin, PdfExtractionOptions options, IReadOnlySet<int> contentObjectIds)
    {
        var marker = Encoding.ASCII.GetBytes("stream");
        var endMarker = Encoding.ASCII.GetBytes("endstream");
        var offset = 0;
        while ((offset = IndexOf(bytes, marker, offset)) >= 0)
        {
            var start = offset + marker.Length;
            if (start < bytes.Length && bytes[start] == '\r') start++;
            if (start < bytes.Length && bytes[start] == '\n') start++;
            var end = IndexOf(bytes, endMarker, start);
            if (end < 0) throw new PdfExtractionException("PDF stream is missing endstream.");
            var payload = bytes[start..end];
            while (payload.Length > 0 && (payload[^1] == '\r' || payload[^1] == '\n')) payload = payload[..^1];
            var header = ReadContainingObjectHeader(latin, offset, 2048);
            if (contentObjectIds.Count > 0 && (!TryReadContainingObjectId(header, options.EffectiveRegexTimeout, out var objectId) || !contentObjectIds.Contains(objectId)))
            {
                offset = end + endMarker.Length;
                continue;
            }
            payload = DecodeFilteredStream(payload, header, options.MaxExpandedStreamBytes);
            yield return Encoding.Latin1.GetString(payload);
            offset = end + endMarker.Length;
        }
    }

    private static bool TryReadContainingObjectId(string header, TimeSpan timeout, out int objectId)
    {
        objectId = 0;
        var matches = Regex.Matches(header, @"(?<id>\d+)\s+\d+\s+obj\b", RegexOptions.None, timeout);
        return matches.Count > 0 && int.TryParse(matches[^1].Groups["id"].Value, out objectId);
    }

    private static List<PdfTextRegion> ParseOperators(string content, PdfExtractionOptions options,
        IReadOnlyDictionary<string, PdfToUnicodeMap> fontMaps)
    {
        var regions = new List<PdfTextRegion>();
        var x = 0d;
        var y = 0d;
        var operatorCursor = 0;
        var actualTextCursor = 0;
        var transform = PdfMatrix.Identity;
        var graphicsStack = new Stack<PdfMatrix>();
        string? currentFont = null;
        Regex textString;
        try { textString = new Regex(TextStringPattern, RegexOptions.Compiled | RegexOptions.NonBacktracking, options.EffectiveRegexTimeout); }
        catch (ArgumentOutOfRangeException exception) { throw new PdfExtractionException("Invalid PDF regex timeout.", exception); }
        try
        {
            foreach (Match match in textString.Matches(content))
            {
                var operatorContext = content[operatorCursor..match.Index];
                var actualTextContext = content[actualTextCursor..match.Index];
                operatorCursor = match.Index + match.Length;
                UpdateGraphicsTransform(operatorContext, options.EffectiveRegexTimeout, graphicsStack, ref transform);
                var fonts = Regex.Matches(operatorContext, @"/(?<font>[A-Za-z][A-Za-z0-9_.+-]*)\s+(?:[-+]?\d+(?:\.\d*)?|\.\d+)\s+Tf\b", RegexOptions.None, options.EffectiveRegexTimeout);
                if (fonts.Count > 0) currentFont = fonts[^1].Groups["font"].Value;
                if (Regex.IsMatch(operatorContext, @"\bBT\b", RegexOptions.NonBacktracking, options.EffectiveRegexTimeout)) (x, y) = (0, 0);
                foreach (Match td in Regex.Matches(operatorContext, $@"({NumberPattern})\s+({NumberPattern})\s+Td", RegexOptions.NonBacktracking, options.EffectiveRegexTimeout))
                    if (double.TryParse(td.Groups[1].Value, out var dx) && double.TryParse(td.Groups[2].Value, out var dy)) (x, y) = (x + dx, y + dy);
                foreach (Match tm in Regex.Matches(operatorContext, $@"({NumberPattern})\s+({NumberPattern})\s+({NumberPattern})\s+({NumberPattern})\s+({NumberPattern})\s+({NumberPattern})\s+Tm", RegexOptions.NonBacktracking, options.EffectiveRegexTimeout))
                    if (double.TryParse(tm.Groups[5].Value, out var tx) && double.TryParse(tm.Groups[6].Value, out var ty)) (x, y) = (tx, ty);
                var after = content[(match.Index + match.Length)..];
                var operatorMatch = Regex.Match(after, @"^\s*(?<op>Tj|TJ)\b", RegexOptions.None, options.EffectiveRegexTimeout);
                // Dictionary strings (notably /ActualText) may occur between two
                // shown strings. Keep them in the context for the next Tj/TJ.
                if (!operatorMatch.Success) continue;
                var actualText = FindActiveActualText(actualTextContext, options.EffectiveRegexTimeout);
                var text = actualText ?? (match.Value[0] == '['
                    ? string.Join("", Regex.Matches(match.Value, LiteralPattern + @"|<[0-9A-Fa-f\s]+>", RegexOptions.None, options.EffectiveRegexTimeout)
                        .Select(item => DecodeString(item.Value, FindFontMap(fontMaps, currentFont))))
                    : DecodeString(match.Value, FindFontMap(fontMaps, currentFont)));
                if (text.Length > 0)
                {
                    var point = transform.Apply(x, y);
                    var right = transform.Apply(x + text.Length * 6, y);
                    var top = transform.Apply(x, y + 12);
                    var width = Math.Max(1, Math.Sqrt(Math.Pow(right.X - point.X, 2) + Math.Pow(right.Y - point.Y, 2)));
                    var height = Math.Max(1, Math.Sqrt(Math.Pow(top.X - point.X, 2) + Math.Pow(top.Y - point.Y, 2)));
                    regions.Add(new PdfTextRegion(text,
                        new Geometry("pdf-user-space", point.X, point.Y, width, height), regions.Count));
                }
                x += text.Length * 6;
                actualTextCursor = match.Index + match.Length;
            }
        }
        catch (RegexMatchTimeoutException exception)
        {
            throw new PdfExtractionException("PDF text operator matching exceeded its time limit.", exception);
        }
        return regions;
    }

    private static void UpdateGraphicsTransform(
        string content,
        TimeSpan timeout,
        Stack<PdfMatrix> stack,
        ref PdfMatrix transform)
    {
        var pattern = $@"(?<!\S)(?<state>q|Q)(?!\S)|(?<a>{NumberPattern})\s+(?<b>{NumberPattern})\s+(?<c>{NumberPattern})\s+(?<d>{NumberPattern})\s+(?<e>{NumberPattern})\s+(?<f>{NumberPattern})\s+cm\b";
        foreach (Match match in Regex.Matches(content, pattern, RegexOptions.None, timeout))
        {
            var state = match.Groups["state"].Value;
            if (state == "q")
            {
                stack.Push(transform);
                continue;
            }
            if (state == "Q")
            {
                if (stack.TryPop(out var restored)) transform = restored;
                continue;
            }
            if (!TryNumber(match.Groups["a"], out var a) ||
                !TryNumber(match.Groups["b"], out var b) ||
                !TryNumber(match.Groups["c"], out var c) ||
                !TryNumber(match.Groups["d"], out var d) ||
                !TryNumber(match.Groups["e"], out var e) ||
                !TryNumber(match.Groups["f"], out var f))
                continue;
            transform = transform.Concat(new PdfMatrix(a, b, c, d, e, f));
        }
    }

    private static bool TryNumber(Group group, out double value) =>
        double.TryParse(group.Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);

    private readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
    {
        public static PdfMatrix Identity { get; } = new(1, 0, 0, 1, 0, 0);

        public (double X, double Y) Apply(double x, double y) =>
            (A * x + C * y + E, B * x + D * y + F);

        public PdfMatrix Concat(PdfMatrix next) => new(
            A * next.A + C * next.B,
            B * next.A + D * next.B,
            A * next.C + C * next.D,
            B * next.C + D * next.D,
            A * next.E + C * next.F + E,
            B * next.E + D * next.F + F);
    }

    private static string? FindActiveActualText(string preceding, TimeSpan timeout)
    {
        var matches = Regex.Matches(preceding,
            @"/ActualText\s*(?<value><[0-9A-Fa-f\s]+>|\((?:\\.|[^\\)])*\))",
            RegexOptions.NonBacktracking, timeout);
        if (matches.Count == 0) return null;
        var actual = matches[^1];
        if (preceding.LastIndexOf("EMC", StringComparison.Ordinal) > actual.Index) return null;
        var token = actual.Groups["value"].Value;
        if (!token.StartsWith('<')) return DecodeString(token);
        var hex = Regex.Replace(token[1..^1], @"\s+", string.Empty, RegexOptions.CultureInvariant);
        if (hex.Length % 2 != 0) hex += "0";
        var bytes = Convert.FromHexString(hex);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        return bytes.Length % 2 == 0 ? Encoding.BigEndianUnicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
    }

    private static IReadOnlyList<PdfTextRegion> SortReadingOrder(IEnumerable<PdfTextRegion> regions)
    {
        var ordered = regions.OrderByDescending(region => region.BoundingBox.Y)
            .ThenBy(region => region.BoundingBox.X).ThenBy(region => region.ReadingOrder).ToArray();
        var lines = new List<PdfTextRegion>();
        foreach (var region in ordered)
        {
            if (lines.Count == 0 || Math.Abs(lines[^1].BoundingBox.Y - region.BoundingBox.Y) > Math.Max(1, region.BoundingBox.Height * 0.35))
            {
                lines.Add(region);
                continue;
            }
            var previous = lines[^1];
            var previousRight = previous.BoundingBox.X + previous.BoundingBox.Width;
            var gap = region.BoundingBox.X - previousRight;
            var overlapTolerance = Math.Max(previous.BoundingBox.Height, region.BoundingBox.Height) * 0.5;
            if (gap < -overlapTolerance)
            {
                // Text drawn in nested coordinate frames (for example table cells)
                // can share a local Y while their estimated horizontal bounds overlap.
                // Keep those runs as separate regions instead of collapsing a page
                // into one unreadable line.
                lines.Add(region);
                continue;
            }
            var separator = gap > Math.Max(previous.BoundingBox.Height, region.BoundingBox.Height) * 1.5 ? " " : string.Empty;
            var right = Math.Max(previousRight, region.BoundingBox.X + region.BoundingBox.Width);
            lines[^1] = new PdfTextRegion(previous.Text + separator + region.Text,
                new Geometry(previous.BoundingBox.CoordinateSpace, previous.BoundingBox.X,
                    Math.Max(previous.BoundingBox.Y, region.BoundingBox.Y), right - previous.BoundingBox.X,
                    Math.Max(previous.BoundingBox.Height, region.BoundingBox.Height)), previous.ReadingOrder);
        }
        return lines;
    }

    private static PdfToUnicodeMap? FindFontMap(IReadOnlyDictionary<string, PdfToUnicodeMap> maps, string? font) =>
        font is not null && maps.TryGetValue(font, out var map) ? map : null;

    private static string DecodeString(string value, PdfToUnicodeMap? map = null)
    {
        if (value.StartsWith("<", StringComparison.Ordinal))
        {
            var hex = Regex.Replace(value[1..^1], @"\s+", string.Empty, RegexOptions.CultureInvariant);
            if (hex.Length % 2 != 0) hex += "0";
            var hexBytes = new byte[hex.Length / 2];
            for (var i = 0; i < hexBytes.Length; i++) if (byte.TryParse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var item)) hexBytes[i] = item;
            return map?.Decode(hexBytes) ?? Encoding.Latin1.GetString(hexBytes);
        }
        var inner = value.Length >= 2 ? value[1..^1] : value;
        var raw = new List<byte>(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != (char)92 || i + 1 >= inner.Length) { raw.Add((byte)inner[i]); continue; }
            var escaped = inner[++i];
            if (escaped is >= '0' and <= '7')
            {
                var octal = new StringBuilder(3).Append(escaped);
                while (octal.Length < 3 && i + 1 < inner.Length && inner[i + 1] is >= '0' and <= '7')
                    octal.Append(inner[++i]);
                raw.Add(Convert.ToByte(octal.ToString(), 8));
                continue;
            }
            raw.Add((byte)(escaped switch { 'n' => (char)10, 'r' => (char)13, 't' => (char)9, 'b' => (char)8, 'f' => (char)12, '(' => (char)40, ')' => (char)41, _ => escaped }));
        }
        var bytes = raw.ToArray();
        return map?.Decode(bytes) ?? Encoding.Latin1.GetString(bytes);
    }

    private static IReadOnlyDictionary<string, PdfToUnicodeMap> ReadFontMaps(byte[] bytes, string latin, string structure,
        PdfExtractionOptions options)
    {
        var maps = new Dictionary<string, PdfToUnicodeMap>(StringComparer.Ordinal);
        // Object dictionaries are deliberately parsed from the already bounded
        // Latin-1 view.  The CMap payload itself is read from the matching stream
        // object below, so arbitrary binary font data never enters this regex.
        var objectBodies = new Dictionary<int, string>();
        try
        {
            foreach (Match match in Regex.Matches(structure, @"(?m)(?<id>\d+)\s+\d+\s+obj\b(?<body>.*?)endobj\b", RegexOptions.Singleline, options.EffectiveRegexTimeout))
                if (int.TryParse(match.Groups["id"].Value, out var id)) objectBodies[id] = match.Groups["body"].Value;
        }
        catch (RegexMatchTimeoutException exception)
        {
            throw new PdfExtractionException("PDF font dictionary matching exceeded its time limit.", exception);
        }
        if (objectBodies.Count == 0) return maps;

        var cmapByObject = new Dictionary<int, PdfToUnicodeMap>();
        foreach (var stream in ReadObjectStreams(bytes, latin, options))
        {
            if (stream.ObjectId is not { } objectId) continue;
            var cmap = PdfToUnicodeMap.Parse(stream.Payload, options.EffectiveRegexTimeout);
            if (cmap.Count > 0) cmapByObject[objectId] = cmap;
        }
        foreach (var font in objectBodies)
        {
            var toUnicode = Regex.Match(font.Value, @"/ToUnicode\s+(?<id>\d+)\s+\d+\s+R\b", RegexOptions.None, options.EffectiveRegexTimeout);
            if (!toUnicode.Success || !int.TryParse(toUnicode.Groups["id"].Value, out var cmapObject) || !cmapByObject.TryGetValue(cmapObject, out var map)) continue;
            foreach (Match alias in Regex.Matches(structure, $@"(?<alias>/[A-Za-z][A-Za-z0-9_.+-]*)\s+{font.Key}\s+\d+\s+R\b", RegexOptions.None, options.EffectiveRegexTimeout))
                maps[alias.Groups["alias"].Value[1..]] = map;
            var fontName = Regex.Match(font.Value, @"/Name\s+/(?<alias>[A-Za-z][A-Za-z0-9_.+-]*)", RegexOptions.None, options.EffectiveRegexTimeout);
            if (fontName.Success) maps[fontName.Groups["alias"].Value] = map;
        }
        return maps;
    }

    private static IEnumerable<PdfObjectStream> ReadObjectStreams(byte[] bytes, string latin, PdfExtractionOptions options)
    {
        var marker = Encoding.ASCII.GetBytes("stream");
        var endMarker = Encoding.ASCII.GetBytes("endstream");
        var offset = 0;
        while ((offset = IndexOf(bytes, marker, offset)) >= 0)
        {
            var start = offset + marker.Length;
            if (start < bytes.Length && bytes[start] == '\r') start++;
            if (start < bytes.Length && bytes[start] == '\n') start++;
            var end = IndexOf(bytes, endMarker, start);
            if (end < 0) throw new PdfExtractionException("PDF stream is missing endstream.");
            var payload = bytes[start..end];
            while (payload.Length > 0 && (payload[^1] == '\r' || payload[^1] == '\n')) payload = payload[..^1];
            var header = ReadContainingObjectHeader(latin, offset, 4096);
            var objectId = TryReadContainingObjectId(header, options.EffectiveRegexTimeout, out var id) ? id : (int?)null;
            payload = DecodeFilteredStream(payload, header, options.MaxExpandedStreamBytes);
            yield return new PdfObjectStream(objectId, header, Encoding.Latin1.GetString(payload));
            offset = end + endMarker.Length;
        }
    }

    private sealed record PdfObjectStream(int? ObjectId, string Header, string Payload);

    private static string ReadContainingObjectHeader(string latin, int streamOffset, int maxLookback)
    {
        var start = Math.Max(0, streamOffset - maxLookback);
        var window = latin[start..streamOffset];
        var objectMarker = window.LastIndexOf(" obj", StringComparison.Ordinal);
        if (objectMarker >= 0)
        {
            // Retain the object number immediately before "obj", but exclude
            // filters belonging to preceding objects in a compact PDF.
            start += Math.Max(0, objectMarker - 32);
        }
        return latin[start..streamOffset];
    }

    private sealed class PdfToUnicodeMap
    {
        private readonly Dictionary<string, string> _entries;
        private readonly int _maxSourceBytes;
        private PdfToUnicodeMap(Dictionary<string, string> entries)
        {
            _entries = entries;
            _maxSourceBytes = entries.Keys.Select(key => key.Length / 2).DefaultIfEmpty(1).Max();
        }
        public int Count => _entries.Count;

        public string Decode(ReadOnlySpan<byte> bytes)
        {
            var output = new StringBuilder();
            for (var index = 0; index < bytes.Length;)
            {
                string? mapped = null;
                var consumed = 0;
                for (var width = Math.Min(_maxSourceBytes, bytes.Length - index); width >= 1; width--)
                {
                    var key = Convert.ToHexString(bytes.Slice(index, width));
                    if (_entries.TryGetValue(key, out mapped)) { consumed = width; break; }
                }
                if (consumed == 0) { output.Append((char)bytes[index++]); continue; }
                output.Append(mapped);
                index += consumed;
            }
            return output.ToString();
        }

        public static PdfToUnicodeMap Parse(string cmap, TimeSpan timeout)
        {
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (Match block in Regex.Matches(cmap, @"beginbfchar(?<body>.*?)endbfchar", RegexOptions.Singleline, timeout))
                    ParsePairs(block.Groups["body"].Value, entries, timeout);
                foreach (Match block in Regex.Matches(cmap, @"beginbfrange(?<body>.*?)endbfrange", RegexOptions.Singleline, timeout))
                    ParseRanges(block.Groups["body"].Value, entries, timeout);
            }
            catch (RegexMatchTimeoutException exception)
            {
                throw new PdfExtractionException("PDF ToUnicode CMap matching exceeded its time limit.", exception);
            }
            return new PdfToUnicodeMap(entries);
        }

        private static void ParsePairs(string body, IDictionary<string, string> entries, TimeSpan timeout)
        {
            var tokens = Regex.Matches(body, @"<(?<hex>[0-9A-Fa-f\s]+)>", RegexOptions.None, timeout).Select(match => NormalizeHex(match.Groups["hex"].Value)).ToArray();
            for (var index = 0; index + 1 < tokens.Length; index += 2) entries[tokens[index]] = DecodeDestination(tokens[index + 1]);
        }

        private static void ParseRanges(string body, IDictionary<string, string> entries, TimeSpan timeout)
        {
            var lines = body.Split('\n');
            foreach (var line in lines)
            {
                var range = Regex.Match(line, @"<(?<start>[0-9A-Fa-f\s]+)>\s+<(?<end>[0-9A-Fa-f\s]+)>\s+(?<dest>\[.*\]|<[0-9A-Fa-f\s]+>)", RegexOptions.None, timeout);
                if (!range.Success) continue;
                var sourceStart = NormalizeHex(range.Groups["start"].Value); var sourceEnd = NormalizeHex(range.Groups["end"].Value);
                var start = Convert.ToUInt32(sourceStart, 16); var end = Convert.ToUInt32(sourceEnd, 16); var destinationToken = range.Groups["dest"].Value.Trim();
                if (destinationToken.StartsWith("[", StringComparison.Ordinal))
                {
                    var destinations = Regex.Matches(destinationToken, @"<(?<hex>[0-9A-Fa-f\s]+)>", RegexOptions.None, timeout)
                        .Select(match => NormalizeHex(match.Groups["hex"].Value)).ToArray();
                    for (var index = 0; index < destinations.Length && start + (uint)index <= end; index++)
                        entries[(start + (uint)index).ToString("X" + sourceStart.Length, System.Globalization.CultureInfo.InvariantCulture)] = DecodeDestination(destinations[index]);
                    continue;
                }
                var destination = NormalizeHex(destinationToken[1..^1]);
                for (var value = start; value <= end; value++)
                {
                    var source = value.ToString("X" + sourceStart.Length, System.Globalization.CultureInfo.InvariantCulture);
                    var codePoint = Convert.ToUInt32(destination, 16) + (value - start);
                    entries[source] = codePoint <= 0xFFFF ? ((char)codePoint).ToString() : char.ConvertFromUtf32((int)codePoint);
                }
            }
        }

        private static string NormalizeHex(string value) => Regex.Replace(value, @"\s+", string.Empty, RegexOptions.CultureInvariant).ToUpperInvariant();
        private static string DecodeDestination(string hex)
        {
            if (hex.Length == 0) return string.Empty;
            var value = Convert.ToUInt32(hex, 16);
            // ToUnicode destinations are UTF-16BE strings.  Four-byte values are
            // commonly used for supplementary-plane characters.
            if (hex.Length > 4) return Encoding.BigEndianUnicode.GetString(Convert.FromHexString(hex));
            return value <= 0xFFFF ? ((char)value).ToString() : char.ConvertFromUtf32((int)value);
        }
    }

    private static byte[] DecodeFilteredStream(byte[] payload, string header, long maxExpandedBytes)
    {
        // ReportLab and many production PDFs encode streams as ASCII85 followed
        // by Flate. Decode filters in the order advertised by the PDF dictionary.
        if (header.Contains("/ASCII85Decode", StringComparison.Ordinal))
            payload = DecodeAscii85(payload, maxExpandedBytes);
        if (header.Contains("/FlateDecode", StringComparison.Ordinal))
            payload = Inflate(payload, maxExpandedBytes);
        return payload;
    }

    private static byte[] DecodeAscii85(byte[] payload, long maxDecodedBytes)
    {
        using var output = new MemoryStream();
        ulong tuple = 0;
        var digits = 0;
        void Write(byte value)
        {
            if (output.Length >= maxDecodedBytes)
                throw new PdfExtractionException($"PDF ASCII85 stream exceeds the {maxDecodedBytes}-byte limit.");
            output.WriteByte(value);
        }

        foreach (var value in payload)
        {
            if (value is 32 or 9 or 13 or 10 or 12 or 0)
                continue;
            if (value == (byte)'~') break;
            if (value == (byte)'z')
            {
                if (digits != 0) throw new PdfExtractionException("PDF ASCII85 zero group appeared mid-tuple.");
                Write(0); Write(0); Write(0); Write(0);
                continue;
            }
            if (value < (byte)'!' || value > (byte)'u')
                throw new PdfExtractionException("PDF ASCII85 stream contains an invalid character.");
            tuple = checked(tuple * 85 + (uint)(value - (byte)'!'));
            digits++;
            if (digits != 5) continue;
            if (tuple > uint.MaxValue) throw new PdfExtractionException("PDF ASCII85 tuple exceeds 32 bits.");
            Write((byte)(tuple >> 24)); Write((byte)(tuple >> 16));
            Write((byte)(tuple >> 8)); Write((byte)tuple);
            tuple = 0;
            digits = 0;
        }

        if (digits > 0)
        {
            var count = digits;
            for (var i = digits; i < 5; i++) tuple = checked(tuple * 85 + 84);
            // A partial group is padded to five digits and may exceed 32 bits;
            // only the low 32 bits form the emitted 1-3 bytes.
            var bytes = new[] { (byte)(tuple >> 24), (byte)(tuple >> 16), (byte)(tuple >> 8), (byte)tuple };
            for (var i = 0; i < count - 1; i++) Write(bytes[i]);
        }
        return output.ToArray();
    }

    private static byte[] Inflate(byte[] payload, long maxExpandedBytes)
    {
        try
        {
            using var input = new MemoryStream(payload);
            using var stream = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            long total = 0;
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                total = checked(total + read);
                if (total > maxExpandedBytes) throw new PdfExtractionException($"PDF expanded stream exceeds the {maxExpandedBytes}-byte limit.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (PdfExtractionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or OverflowException)
        {
            throw new PdfExtractionException("PDF Flate stream is malformed.", exception);
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++) if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }
}

public sealed record PdfRestoreDecision(FidelityLevel Fidelity, bool ByteIdentical, string Message);

public static class PdfRestorePolicy
{
    public static PdfRestoreDecision For(bool projectionChanged) => projectionChanged
        ? new(FidelityLevel.F3, false, "Edited PDF content must be rendered as a new PDF; F1 package restore is not supported.")
        : new(FidelityLevel.F0, true, "Unedited PDF can be returned byte-identically from the preserved original.");
}
