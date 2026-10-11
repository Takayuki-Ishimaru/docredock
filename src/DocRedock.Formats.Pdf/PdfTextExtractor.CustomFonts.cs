using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocRedock.Formats.Pdf;

public static partial class PdfTextExtractor
{
    private sealed record CidEncoding(Dictionary<string, int> Codes, bool Identity = false,
        IReadOnlyList<(int Bytes, uint First, uint Last)>? Spaces = null);

    // A custom character code can be read only through the font's actual encoding. A
    // font name, or a glyph's shape, is not evidence of a Unicode character.
    private static Dictionary<string, PdfToUnicodeMap> ReadCustomFontMaps(byte[] bytes, string latin,
        PdfExtractionOptions options, IReadOnlyDictionary<int, string> objects, PdfFontCatalog catalog)
    {
        var result = new Dictionary<string, PdfToUnicodeMap>(StringComparer.Ordinal);
        var fonts = catalog.Fonts.Where(font => font.ToUnicodeObject is null && font.Codes == PdfFontCodes.Glyphs).ToArray();
        var wanted = new HashSet<int>();
        foreach (var font in fonts)
        {
            if (font.Japan1Identity) { result[font.Identity] = Japan1Unicode.Value; continue; }
            var entries = ReadDictionaryEntries(font.Dictionary);
            AddReference(EntryValue(entries, "Encoding"));
            if (DescendantFont(EntryValue(entries, "DescendantFonts"), objects) is not { } descendant) continue;
            var cid = ReadDictionaryEntries(descendant);
            AddReference(EntryValue(cid, "CIDToGIDMap"));
            if (ResolveDictionary(EntryValue(cid, "FontDescriptor"), objects) is { } descriptor)
                AddReference(EntryValue(ReadDictionaryEntries(descriptor), "FontFile2"));
        }
        // UseCMap references live in stream dictionaries; follow only this job's font
        // dependencies and bound cycles before inflating any streams.
        for (var depth = 0; depth < 8; depth++)
        {
            var before = wanted.Count;
            foreach (var id in wanted.ToArray())
                if (objects.TryGetValue(id, out var body)) AddReference(EntryValue(ReadDictionaryEntries(body), "UseCMap"));
            if (wanted.Count == before) break;
        }
        if (wanted.Count == 0) return result;
        var streams = ReadObjectStreams(bytes, latin, options, wanted, objects).Where(stream => stream.ObjectId.HasValue)
            .ToDictionary(stream => stream.ObjectId!.Value, stream => stream);
        foreach (var font in fonts)
        {
            if (font.Japan1Identity) { result[font.Identity] = Japan1Unicode.Value; continue; }
            var entries = ReadDictionaryEntries(font.Dictionary);
            if (ReadName(EntryValue(entries, "Subtype")) != "Type0" ||
                DescendantFont(EntryValue(entries, "DescendantFonts"), objects) is not { } descendant) continue;
            var cid = ReadDictionaryEntries(descendant);
            var encoding = ReadCidEncoding(EntryValue(entries, "Encoding"), streams, objects, [], options.EffectiveRegexTimeout);
            if (encoding is null) continue;
            var unicode = new Dictionary<int, string>();
            var system = ResolveDictionary(EntryValue(cid, "CIDSystemInfo"), objects);
            if (system is not null)
            {
                var info = ReadDictionaryEntries(system);
                var supplement = ReadNumber(ResolveValue(EntryValue(info, "Supplement"), objects));
                if (ResolveValue(EntryValue(info, "Registry"), objects) == "(Adobe)" &&
                    ResolveValue(EntryValue(info, "Ordering"), objects) == "(Japan1)" &&
                    supplement is >= 0 and <= PdfJapan1UnicodeData.Supplement && supplement == Math.Truncate(supplement.Value))
                    foreach (var pair in Japan1Unicode.Value.Entries)
                        unicode[int.Parse(pair.Key, NumberStyles.HexNumber, CultureInfo.InvariantCulture)] = pair.Value;
            }
            if (unicode.Count == 0 && ReadName(EntryValue(cid, "Subtype")) == "CIDFontType2" &&
                ResolveDictionary(EntryValue(cid, "FontDescriptor"), objects) is { } descriptor &&
                EntryValue(ReadDictionaryEntries(descriptor), "FontFile2") is { } fontReference &&
                TryReadReference(fontReference, out var fontId) && streams.TryGetValue(fontId, out var program))
            {
                var glyphs = InvertUnicodeCmap(Encoding.Latin1.GetBytes(program.Payload));
                var gidValue = EntryValue(cid, "CIDToGIDMap");
                if (gidValue is null || ReadName(gidValue) == "Identity")
                    foreach (var pair in glyphs) unicode[pair.Key] = pair.Value;
                else if (TryReadReference(gidValue, out var gidId) && streams.TryGetValue(gidId, out var gids))
                {
                    var data = Encoding.Latin1.GetBytes(gids.Payload);
                    if (data.Length % 2 == 0 && data.Length <= 131072)
                        for (var index = 0; index < data.Length / 2; index++)
                            if (glyphs.TryGetValue(BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(index * 2, 2)), out var text)) unicode[index] = text;
                }
            }
            var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (encoding.Identity)
                foreach (var pair in unicode) mapped[pair.Key.ToString("X4", CultureInfo.InvariantCulture)] = pair.Value;
            foreach (var pair in encoding.Codes)
                if (unicode.TryGetValue(pair.Value, out var text)) mapped[pair.Key] = text;
                else mapped.Remove(pair.Key); // An explicit entry also overrides an inherited identity.
            if (mapped.Count > 0) result[font.Identity] = PdfToUnicodeMap.FromEntries(mapped, encoding.Spaces);
        }
        return result;

        void AddReference(string? reference)
        {
            if (reference is not null && TryReadReference(reference, out var id)) wanted.Add(id);
        }
    }

    private static CidEncoding? ReadCidEncoding(string? value, IReadOnlyDictionary<int, PdfObjectStream> streams,
        IReadOnlyDictionary<int, string> objects, HashSet<int> visited, TimeSpan timeout)
    {
        if (ReadName(value) is "Identity-H" or "Identity-V") return new([], true, [(2, 0, 65535)]);
        if (value is null || !TryReadReference(value, out var id) || visited.Count >= 8 || !visited.Add(id) ||
            !streams.TryGetValue(id, out var stream)) return null;
        var payload = Regex.Replace(stream.Payload, @"%[^\r\n]*", "", RegexOptions.CultureInvariant, timeout);
        var header = objects.GetValueOrDefault(id) ?? stream.Header;
        var inherited = EntryValue(ReadDictionaryEntries(header), "UseCMap");
        if (inherited is null)
        {
            var use = Regex.Match(payload, @"/(?<name>[A-Za-z0-9_-]+)\s+usecmap\b", RegexOptions.CultureInvariant, timeout);
            if (use.Success) inherited = "/" + use.Groups["name"].Value;
        }
        var basis = inherited is null ? new CidEncoding([]) : ReadCidEncoding(inherited, streams, objects, visited, timeout);
        if (basis is null) return null;
        var codes = new Dictionary<string, int>(basis.Codes, StringComparer.OrdinalIgnoreCase);
        var spaces = (basis.Spaces ?? []).ToList();
        foreach (Match section in Regex.Matches(payload, @"(?<count>\d+)\s+begincodespacerange\b(?<body>.*?)endcodespacerange\b", RegexOptions.Singleline, timeout))
        {
            var ranges = Regex.Matches(section.Groups["body"].Value, @"<(?<first>[0-9A-Fa-f]+)>\s*<(?<last>[0-9A-Fa-f]+)>", RegexOptions.CultureInvariant, timeout);
            if (!int.TryParse(section.Groups["count"].Value, out var count) || count != ranges.Count || spaces.Count + count > 256) return null;
            foreach (Match range in ranges)
            {
                var first = range.Groups["first"].Value; var last = range.Groups["last"].Value;
                if (first.Length is not (2 or 4 or 6 or 8) || last.Length != first.Length ||
                    !uint.TryParse(first, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var low) ||
                    !uint.TryParse(last, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var high) || high < low) return null;
                spaces.Add((first.Length / 2, low, high));
            }
        }
        // A short code must not also be the prefix of a longer code. Otherwise
        // even a known mapping could consume bytes belonging to the next glyph.
        foreach (var shortSpace in spaces)
            foreach (var longSpace in spaces.Where(space => space.Bytes > shortSpace.Bytes))
            {
                var shift = 8 * (longSpace.Bytes - shortSpace.Bytes);
                if (shortSpace.First <= (longSpace.Last >> shift) && shortSpace.Last >= (longSpace.First >> shift)) return null;
            }
        var assigned = 0;
        foreach (Match section in Regex.Matches(payload, @"(?<count>\d+)\s+begin(?<type>cidchar|cidrange)\b(?<body>.*?)end\k<type>\b", RegexOptions.Singleline, timeout))
        {
            var ranges = section.Groups["type"].Value == "cidrange";
            var pattern = ranges ? @"<(?<first>[0-9A-Fa-f]+)>\s*<(?<last>[0-9A-Fa-f]+)>\s*(?<cid>\d+)" : @"<(?<first>[0-9A-Fa-f]+)>\s*(?<cid>\d+)";
            var matches = Regex.Matches(section.Groups["body"].Value, pattern, RegexOptions.CultureInvariant, timeout);
            if (!int.TryParse(section.Groups["count"].Value, out var count) || count != matches.Count) return null;
            foreach (Match match in matches)
            {
                var firstText = match.Groups["first"].Value;
                if (firstText.Length is not (2 or 4 or 6 or 8) ||
                    !uint.TryParse(firstText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var first) ||
                    !int.TryParse(match.Groups["cid"].Value, out var cid)) return null;
                var last = first;
                if (ranges && (match.Groups["last"].Length != firstText.Length ||
                    !uint.TryParse(match.Groups["last"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out last))) return null;
                if (last < first || last - first > 65535 || cid < 0 || (ulong)cid + last - first > 65535 || assigned + (long)last - first + 1 > 262144) return null;
                for (uint offset = 0; offset <= last - first; offset++)
                {
                    codes[(first + offset).ToString("X" + firstText.Length, CultureInfo.InvariantCulture)] = cid + (int)offset;
                    assigned++;
                }
            }
        }
        return codes.Count > 0 || basis.Identity ? new(codes, basis.Identity, spaces) : null;
    }

    private static Dictionary<int, string> InvertUnicodeCmap(byte[] data)
    {
        var result = new Dictionary<int, string>();
        var ambiguous = new HashSet<int>();
        try
        {
            if (data.Length < 12 || U32(0) is not (0x00010000 or 0x74727565)) return result;
            var tables = U16(4);
            var cmap = -1;
            var cmapEnd = 0;
            Check(12, tables * 16);
            for (var index = 0; index < tables; index++)
            {
                var at = 12 + index * 16;
                if (U32(at) != 0x636d6170) continue;
                cmap = checked((int)U32(at + 8));
                cmapEnd = checked(cmap + (int)U32(at + 12));
                Check(cmap, cmapEnd - cmap);
            }
            if (cmap < 0) return result;
            if (cmapEnd - cmap < 4 || U16(cmap) != 0) return [];
            var count = U16(cmap + 2);
            if (4 + count * 8 > cmapEnd - cmap) return [];
            Check(cmap + 4, count * 8);
            var work = 0;
            for (var index = 0; index < count; index++)
            {
                var record = cmap + 4 + index * 8;
                var platform = U16(record);
                var encoding = U16(record + 2);
                if (platform != 0 && !(platform == 3 && encoding is 1 or 10)) continue;
                var at = checked(cmap + (int)U32(record + 4));
                if (at < cmap + 4 + count * 8 || at > cmapEnd - 4) return [];
                var format = U16(at);
                var length = format is 12 or 13 ? checked((int)U32(at + 4)) : U16(at + 2);
                Check(at, length);
                if (at < cmap || (long)at + length > cmapEnd) return [];
                if (format is 12 or 13)
                {
                    if (length < 16) return [];
                    var groups = U32(at + 12);
                    if (groups > (length - 16) / 12) return [];
                    uint? previousLast = null;
                    for (var group = 0; group < groups; group++)
                    {
                        var slot = at + 16 + group * 12;
                        var first = U32(slot); var last = U32(slot + 4); var gid = U32(slot + 8);
                        if (last < first || last > 0x10ffff || previousLast is { } previous && first <= previous ||
                            (work += checked((int)(last - first + 1))) > 2_000_000) return [];
                        previousLast = last;
                        for (var cp = first; cp <= last; cp++) Add(format == 13 ? gid : gid + cp - first, cp);
                    }
                }
                else if (format == 4)
                {
                    if (length < 16 || U16(at + 6) % 2 != 0) return [];
                    var segments = U16(at + 6) / 2;
                    var endAt = at + 14; var startAt = endAt + segments * 2 + 2;
                    var deltaAt = startAt + segments * 2; var rangeAt = deltaAt + segments * 2;
                    if (segments == 0 || rangeAt + segments * 2 > at + length) return [];
                    var previousLast = -1;
                    for (var segment = 0; segment < segments; segment++)
                    {
                        var first = U16(startAt + segment * 2); var last = U16(endAt + segment * 2);
                        var delta = U16(deltaAt + segment * 2); var range = U16(rangeAt + segment * 2);
                        if (last < first || first <= previousLast || range % 2 != 0 || (work += last - first + 1) > 2_000_000) return [];
                        previousLast = last;
                        for (var cp = (int)first; cp <= last && cp < 65535; cp++)
                        {
                            var glyph = (cp + delta) & 65535;
                            if (range != 0)
                            {
                                var glyphAt = rangeAt + segment * 2 + range + (cp - first) * 2;
                                if (glyphAt + 2 > at + length) return [];
                                glyph = U16(glyphAt);
                                if (glyph != 0) glyph = (glyph + delta) & 65535;
                            }
                            Add((uint)glyph, (uint)cp);
                        }
                    }
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException or ArgumentOutOfRangeException) { return []; }
        return result;

        void Check(int offset, int length)
        {
            if (offset < 0 || length < 0 || offset > data.Length - length) throw new InvalidDataException("Invalid embedded font cmap.");
        }
        ushort U16(int at) { Check(at, 2); return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at, 2)); }
        uint U32(int at) { Check(at, 4); return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at, 4)); }
        void Add(uint glyph, uint cp)
        {
            if (glyph is 0 or > 65535 || !Rune.IsValid((int)cp) || ambiguous.Contains((int)glyph)) return;
            var text = char.ConvertFromUtf32((int)cp);
            if (result.TryGetValue((int)glyph, out var previous) && previous != text) { result.Remove((int)glyph); ambiguous.Add((int)glyph); }
            else result[(int)glyph] = text;
        }
    }
}
