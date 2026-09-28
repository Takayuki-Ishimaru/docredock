using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocRedock.Formats.Pdf;

// Resource dictionaries resolved scope by scope: fonts, optional content, and the dictionary
// reading both need. A resource name means nothing outside the dictionary that declares it - a page
// and each of its forms may all call a different font /F1 - so every name is resolved where it is
// used, never through one table for the whole document.
public static partial class PdfTextExtractor
{
    /// <summary>The entries of the first dictionary written in <paramref name="text"/>, at that
    /// dictionary's own level only: each key with its <c>#xx</c> escapes decoded, and the raw text of
    /// its value. A key written inside a value is never offered as an entry, so the <c>/Font</c> of
    /// an ExtGState or the <c>/Type /Font</c> of an inline font is not taken for the resource
    /// dictionary's own <c>/Font</c>.</summary>
    private static IReadOnlyList<(string Key, string Value)> ReadDictionaryEntries(string text)
    {
        var entries = new List<(string Key, string Value)>();
        var open = text.IndexOf("<<", StringComparison.Ordinal);
        if (open < 0) return entries;
        var index = open + 2;
        while (true)
        {
            index = SkipSpace(text, index);
            if (index >= text.Length || text[index] == '>') break;
            if (text[index] != '/')
            {
                // Malformed: a value without a key. Step over it rather than stall.
                index = SkipValue(text, index);
                continue;
            }
            var keyEnd = NameEnd(text, index + 1);
            var key = DecodeName(text[(index + 1)..keyEnd]);
            var valueStart = SkipSpace(text, keyEnd);
            var valueEnd = SkipValue(text, valueStart);
            entries.Add((key, text[valueStart..valueEnd]));
            index = valueEnd;
        }
        return entries;
    }

    private static string? EntryValue(IReadOnlyList<(string Key, string Value)> entries, string key)
    {
        foreach (var entry in entries)
            if (StringComparer.Ordinal.Equals(entry.Key, key)) return entry.Value;
        return null;
    }

    /// <summary>The dictionary a value stands for: the value itself when it is written inline, the
    /// referenced object's body when it is an indirect reference, or null.</summary>
    private static string? ResolveDictionary(string? value, IReadOnlyDictionary<int, string> objects)
    {
        if (value is null) return null;
        if (TryReadReference(value, out var id)) return objects.TryGetValue(id, out var body) && body.Contains("<<", StringComparison.Ordinal) ? body : null;
        return value.StartsWith("<<", StringComparison.Ordinal) ? value : null;
    }

    /// <summary>Whether <paramref name="value"/> is exactly one indirect reference <c>N G R</c>.</summary>
    private static bool TryReadReference(string value, out int id)
    {
        id = 0;
        var index = SkipSpace(value, 0);
        var numberEnd = index;
        while (numberEnd < value.Length && char.IsAsciiDigit(value[numberEnd])) numberEnd++;
        if (numberEnd == index || !int.TryParse(value.AsSpan(index, numberEnd - index), NumberStyles.None, CultureInfo.InvariantCulture, out id)) return false;
        var generation = SkipSpace(value, numberEnd);
        var generationEnd = generation;
        while (generationEnd < value.Length && char.IsAsciiDigit(value[generationEnd])) generationEnd++;
        if (generationEnd == generation) return false;
        var keyword = SkipSpace(value, generationEnd);
        return keyword < value.Length && value[keyword] == 'R' && SkipSpace(value, keyword + 1) == value.Length;
    }

    /// <summary>Every object an array (or a single reference) names, in order.</summary>
    private static IReadOnlyList<int> ReadReferences(string? value)
    {
        if (value is null) return [];
        var ids = new List<int>();
        foreach (Match reference in Regex.Matches(value, @"(?<id>\d+)\s+\d+\s+R\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            if (int.TryParse(reference.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) ids.Add(id);
        return ids;
    }

    /// <summary>The name a value writes (<c>/Identity-V</c> gives <c>Identity-V</c>), or null.</summary>
    private static string? ReadName(string? value)
    {
        if (value is null) return null;
        var start = SkipSpace(value, 0);
        return start < value.Length && value[start] == '/' ? DecodeName(value[(start + 1)..NameEnd(value, start + 1)]) : null;
    }

    /// <summary>A name as written in a file, with every <c>#xx</c> escape replaced by its character,
    /// so <c>/F#31</c> in a content stream finds the font a dictionary lists as <c>/F1</c>.</summary>
    private static string DecodeName(string name)
    {
        if (!name.Contains('#', StringComparison.Ordinal)) return name;
        var decoded = new StringBuilder(name.Length);
        for (var index = 0; index < name.Length; index++)
        {
            if (name[index] == '#' && index + 2 < name.Length &&
                byte.TryParse(name.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                decoded.Append((char)value);
                index += 2;
                continue;
            }
            decoded.Append(name[index]);
        }
        return decoded.ToString();
    }

    private static bool IsPdfDelimiter(char value) => value is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

    private static int NameEnd(string text, int index)
    {
        while (index < text.Length && !char.IsWhiteSpace(text[index]) && !IsPdfDelimiter(text[index])) index++;
        return index;
    }

    /// <summary>Skips white space and comments.</summary>
    private static int SkipSpace(string text, int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]) || text[index] == '\0') { index++; continue; }
            if (text[index] != '%') break;
            while (index < text.Length && text[index] is not '\r' and not '\n') index++;
        }
        return index;
    }

    /// <summary>The index just past the value starting at <paramref name="index"/>: a dictionary,
    /// array, string, name, number, indirect reference, or bare keyword. Always advances.</summary>
    private static int SkipValue(string text, int index)
    {
        if (index >= text.Length) return text.Length;
        var character = text[index];
        if (character == '<' && index + 1 < text.Length && text[index + 1] == '<') return SkipBracketed(text, index);
        if (character == '<') return SkipHexString(text, index);
        if (character == '[') return SkipBracketed(text, index);
        if (character == '(') return SkipLiteralString(text, index);
        if (character == '/') return NameEnd(text, index + 1);
        var end = NameEnd(text, index);
        if (end == index) return index + 1;
        // "12 0 R" is one value: an indirect reference.
        if (char.IsAsciiDigit(character))
        {
            var generation = SkipSpace(text, end);
            var generationEnd = generation;
            while (generationEnd < text.Length && char.IsAsciiDigit(text[generationEnd])) generationEnd++;
            if (generationEnd > generation && generationEnd < text.Length && char.IsWhiteSpace(text[generationEnd]))
            {
                var keyword = SkipSpace(text, generationEnd);
                if (keyword < text.Length && text[keyword] == 'R' && (keyword + 1 == text.Length || char.IsWhiteSpace(text[keyword + 1]) || IsPdfDelimiter(text[keyword + 1])))
                    return keyword + 1;
            }
        }
        return end;
    }

    /// <summary>Skips a balanced <c>&lt;&lt; ... &gt;&gt;</c> or <c>[ ... ]</c>, stepping over the
    /// strings inside so a bracket in a string cannot end it early.</summary>
    private static int SkipBracketed(string text, int index)
    {
        var depth = 0;
        while (index < text.Length)
        {
            var character = text[index];
            if (character == '(') { index = SkipLiteralString(text, index); continue; }
            if (character == '%') { index = SkipSpace(text, index); continue; }
            if (character == '<' && index + 1 < text.Length && text[index + 1] == '<') { depth++; index += 2; continue; }
            if (character == '>' && index + 1 < text.Length && text[index + 1] == '>') { index += 2; if (--depth <= 0) return index; continue; }
            if (character == '<') { index = SkipHexString(text, index); continue; }
            if (character == '[') { depth++; index++; continue; }
            if (character == ']') { index++; if (--depth <= 0) return index; continue; }
            index++;
        }
        return text.Length;
    }

    private static int SkipHexString(string text, int index)
    {
        var close = text.IndexOf('>', index + 1);
        return close < 0 ? text.Length : close + 1;
    }

    private static int SkipLiteralString(string text, int index)
    {
        var depth = 0;
        while (index < text.Length)
        {
            var character = text[index++];
            if (character == '\\') { index++; continue; }
            if (character == '(') depth++;
            else if (character == ')' && --depth == 0) return index;
        }
        return text.Length;
    }

    /// <summary>What decoding one font needs. <see cref="Identity"/> is shared by every resource name
    /// that points at the same font object; <see cref="ToUnicodeObject"/> is the object of its
    /// <c>/ToUnicode</c> CMap; <see cref="Vertical"/> marks a font whose encoding is a <c>-V</c> CMap
    /// (or declares <c>/WMode 1</c>), so its glyphs advance down the page, not across it.</summary>
    private sealed record PdfFontResource(string Identity, int? ToUnicodeObject, bool Vertical);

    /// <summary>Reads each font a resource dictionary names once, and remembers under which names
    /// the document's resource dictionaries offer it - the evidence a name that cannot be resolved
    /// where it is used falls back on.</summary>
    private sealed class PdfFontCatalog(IReadOnlyDictionary<int, string> objects)
    {
        private readonly Dictionary<int, PdfFontResource> byObject = [];
        private readonly Dictionary<string, PdfFontResource> byInlineDictionary = new(StringComparer.Ordinal);

        public Dictionary<string, HashSet<PdfFontResource>> Named { get; } = new(StringComparer.Ordinal);

        public IEnumerable<PdfFontResource> Fonts => byObject.Values.Concat(byInlineDictionary.Values);

        /// <summary>The fonts a <c>/Font</c> resource value names, or null when the value is not a
        /// dictionary the extractor can follow.</summary>
        public IReadOnlyDictionary<string, PdfFontResource>? ReadFontDictionary(string? value)
        {
            if (ResolveDictionary(value, objects) is not { } dictionary) return null;
            var fonts = new Dictionary<string, PdfFontResource>(StringComparer.Ordinal);
            foreach (var (name, font) in ReadDictionaryEntries(dictionary))
            {
                PdfFontResource? resource = null;
                if (TryReadReference(font, out var id)) resource = FromObject(id);
                else if (font.StartsWith("<<", StringComparison.Ordinal)) resource = FromInline(font);
                if (resource is null) continue;
                fonts[name] = resource;
                if (!Named.TryGetValue(name, out var named)) Named[name] = named = [];
                named.Add(resource);
            }
            return fonts.Count > 0 ? fonts : null;
        }

        private PdfFontResource FromObject(int id)
        {
            if (byObject.TryGetValue(id, out var known)) return known;
            var font = objects.TryGetValue(id, out var body) ? Describe($"object:{id}", body) : new PdfFontResource($"object:{id}", null, false);
            byObject[id] = font;
            return font;
        }

        private PdfFontResource FromInline(string dictionary)
        {
            if (byInlineDictionary.TryGetValue(dictionary, out var known)) return known;
            var font = Describe("inline:" + dictionary, dictionary);
            byInlineDictionary[dictionary] = font;
            return font;
        }

        private PdfFontResource Describe(string identity, string dictionary)
        {
            var entries = ReadDictionaryEntries(dictionary);
            int? toUnicode = EntryValue(entries, "ToUnicode") is { } reference && TryReadReference(reference, out var cmap) ? cmap : null;
            var encoding = EntryValue(entries, "Encoding");
            var vertical = ReadName(encoding) is { } name
                ? name.EndsWith("-V", StringComparison.Ordinal)
                : encoding is not null && TryReadReference(encoding, out var embedded) && objects.TryGetValue(embedded, out var cmapBody) &&
                  Regex.IsMatch(cmapBody, @"/WMode\s+1\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            return new PdfFontResource(identity, toUnicode, vertical);
        }
    }

    /// <summary>The document's ToUnicode CMaps by object, and for each resource name the distinct
    /// maps the fonts offered under that name decode with.</summary>
    private sealed class PdfFontTable(IReadOnlyDictionary<int, PdfToUnicodeMap> maps,
        IReadOnlyDictionary<string, IReadOnlyList<PdfToUnicodeMap>> candidates)
    {
        public PdfToUnicodeMap? MapFor(int? toUnicodeObject) =>
            toUnicodeObject is { } id && maps.TryGetValue(id, out var map) ? map : null;

        public IReadOnlyList<PdfToUnicodeMap> CandidatesFor(string name) =>
            candidates.TryGetValue(name, out var found) ? found : [];
    }

    /// <summary>A font as the rewritten content names it: <see cref="Ambiguous"/> marks a name that
    /// could not be resolved where it is used and that the document gives to fonts decoding
    /// differently, so its text is left undecoded instead of being read with one of them.</summary>
    private sealed record PdfFontBinding(string ResourceName, PdfToUnicodeMap? Map, bool Vertical, bool Ambiguous);

    /// <summary>Gives every font a page or form really uses its own name in the expanded content.
    /// Inlining forms merges several resource scopes into one stream; after the rewrite, a
    /// <c>Tf</c> anywhere in it names exactly the font its own scope meant.</summary>
    private sealed class PdfFontRegistry(PdfFontTable table)
    {
        private const string TokenPrefix = "DocRedock.Font.";
        private readonly Dictionary<string, string> tokens = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PdfFontBinding> bindings = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, PdfFontBinding> Bindings => bindings;

        public string Token(string name, PdfResourceScope? scope)
        {
            string identity;
            PdfFontBinding binding;
            if (scope?.Fonts is { } fonts && fonts.TryGetValue(name, out var font))
            {
                identity = font.Identity;
                binding = new PdfFontBinding(name, table.MapFor(font.ToUnicodeObject), font.Vertical, false);
            }
            else
            {
                // Not declared where it is used - a scope the extractor could not read, or a
                // producer that relied on another dictionary. The document is evidence only when
                // it is unambiguous: one decoding for the name. Otherwise nothing is guessed.
                var candidates = table.CandidatesFor(name);
                identity = (candidates.Count > 1 ? "ambiguous:" : "unscoped:") + name;
                binding = new PdfFontBinding(name, candidates.Count == 1 ? candidates[0] : null, false, candidates.Count > 1);
            }
            if (tokens.TryGetValue(identity, out var token)) return token;
            token = TokenPrefix + tokens.Count.ToString(CultureInfo.InvariantCulture);
            tokens[identity] = token;
            bindings[token] = binding;
            return token;
        }
    }

    /// <summary>Reads the ToUnicode CMaps fonts refer to and collects, per resource name, the maps
    /// the document's fonts offer under it. Only streams some object names as its <c>/ToUnicode</c>
    /// are decoded: inflating every image and font program only to discard it cost memory.</summary>
    private static PdfFontTable ReadFontTable(byte[] bytes, string latin, string structure, PdfExtractionOptions options,
        IReadOnlyDictionary<int, string> objects, PdfFontCatalog catalog)
    {
        var timeout = options.EffectiveRegexTimeout;
        // Every ToUnicode stream any object refers to - a font, or a resource dictionary that
        // writes its fonts inline - and, per object, its first one as the legacy alias scan used.
        var toUnicodeByObject = new Dictionary<int, int>();
        var cmapObjects = new HashSet<int>();
        foreach (var (id, body) in objects)
        {
            if (!body.Contains("/ToUnicode", StringComparison.Ordinal)) continue;
            foreach (Match toUnicode in Regex.Matches(body, @"/ToUnicode\s+(?<id>\d+)\s+\d+\s+R\b", RegexOptions.None, timeout))
            {
                if (!int.TryParse(toUnicode.Groups["id"].Value, out var cmapObject)) continue;
                cmapObjects.Add(cmapObject);
                toUnicodeByObject.TryAdd(id, cmapObject);
            }
        }
        var maps = new Dictionary<int, PdfToUnicodeMap>();
        if (cmapObjects.Count > 0)
            foreach (var stream in ReadObjectStreams(bytes, latin, options, cmapObjects))
            {
                if (stream.ObjectId is not { } objectId) continue;
                var cmap = PdfToUnicodeMap.Parse(stream.Payload, timeout);
                if (cmap.Count > 0) maps[objectId] = cmap;
            }
        var candidates = new Dictionary<string, List<PdfToUnicodeMap>>(StringComparer.Ordinal);
        void Offer(string name, PdfToUnicodeMap map)
        {
            if (!candidates.TryGetValue(name, out var list)) candidates[name] = list = [];
            if (!list.Any(existing => existing.Signature == map.Signature)) list.Add(map);
        }
        // Names resource dictionaries give fonts, inline fonts included.
        foreach (var (name, fonts) in catalog.Named)
            foreach (var font in fonts)
                if (font.ToUnicodeObject is { } cmapObject && maps.TryGetValue(cmapObject, out var map)) Offer(name, map);
        // And, as before scoped resolution existed, every "/Name N 0 R" that points at an object
        // with a map, plus a font's own /Name: dictionaries the scoped reading could not reach
        // (an annotation's resources, an unreadable page tree) still count as evidence.
        foreach (Match alias in Regex.Matches(structure, @"/(?<alias>[A-Za-z][A-Za-z0-9_.+-]*)\s+(?<id>\d+)\s+\d+\s+R\b", RegexOptions.None, timeout))
            if (int.TryParse(alias.Groups["id"].Value, out var fontId) && toUnicodeByObject.TryGetValue(fontId, out var cmapObject) &&
                maps.TryGetValue(cmapObject, out var map))
                Offer(alias.Groups["alias"].Value, map);
        foreach (var (id, cmapObject) in toUnicodeByObject)
        {
            if (!maps.TryGetValue(cmapObject, out var map)) continue;
            var fontName = Regex.Match(objects[id], @"/Name\s+/(?<alias>[A-Za-z][A-Za-z0-9_.+-]*)", RegexOptions.None, timeout);
            if (fontName.Success) Offer(fontName.Groups["alias"].Value, map);
        }
        return new PdfFontTable(maps, candidates.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<PdfToUnicodeMap>)entry.Value, StringComparer.Ordinal));
    }

    /// <summary>Whether content drawn under an optional content group (a layer) is shown.</summary>
    private enum PdfLayerVisibility { Visible, Hidden, Unknown }

    /// <summary>The layers a viewer hides when it opens the document - the default configuration's
    /// <c>/OFF</c> groups, or every group not listed <c>/ON</c> under <c>/BaseState /OFF</c> - and
    /// how to evaluate what a page marks as optional. Null (see <see cref="Read"/>) when the
    /// document hides nothing, so content that merely carries layer marks is never questioned.</summary>
    private sealed class PdfOptionalContent(IReadOnlySet<int> hidden, IReadOnlySet<int> groups, IReadOnlyDictionary<int, string> objects)
    {
        public static PdfOptionalContent? Read(IReadOnlyList<(int Id, string Body)> ordered, IReadOnlyDictionary<int, string> objects, TimeSpan timeout)
        {
            foreach (var (_, body) in ordered)
            {
                if (!body.Contains("/OCProperties", StringComparison.Ordinal) ||
                    !Regex.IsMatch(body, @"/Type\s*/Catalog\b", RegexOptions.None, timeout)) continue;
                if (ResolveDictionary(EntryValue(ReadDictionaryEntries(body), "OCProperties"), objects) is not { } properties) return null;
                var entries = ReadDictionaryEntries(properties);
                var groups = ReadReferences(ResolveArray(EntryValue(entries, "OCGs"), objects)).ToHashSet();
                if (ResolveDictionary(EntryValue(entries, "D"), objects) is not { } configuration) return null;
                var defaults = ReadDictionaryEntries(configuration);
                var on = ReadReferences(ResolveArray(EntryValue(defaults, "ON"), objects)).ToHashSet();
                var off = ReadReferences(ResolveArray(EntryValue(defaults, "OFF"), objects)).ToHashSet();
                var hidden = StringComparer.Ordinal.Equals(ReadName(EntryValue(defaults, "BaseState")), "OFF")
                    ? groups.Where(group => !on.Contains(group)).ToHashSet()
                    : off;
                return hidden.Count > 0 ? new PdfOptionalContent(hidden, groups, objects) : null;
            }
            return null;
        }

        /// <summary>Evaluates the optional content group or membership dictionary an object is.</summary>
        public PdfLayerVisibility Evaluate(int objectId)
        {
            if (!objects.TryGetValue(objectId, out var body)) return PdfLayerVisibility.Unknown;
            var entries = ReadDictionaryEntries(body);
            var type = ReadName(EntryValue(entries, "Type"));
            if (StringComparer.Ordinal.Equals(type, "OCG") || groups.Contains(objectId))
                return hidden.Contains(objectId) ? PdfLayerVisibility.Hidden : PdfLayerVisibility.Visible;
            if (!StringComparer.Ordinal.Equals(type, "OCMD")) return PdfLayerVisibility.Unknown;
            // A visibility expression is not evaluated; the membership policy over plain groups is.
            if (EntryValue(entries, "VE") is not null) return PdfLayerVisibility.Unknown;
            var members = ReadReferences(EntryValue(entries, "OCGs")).Where(id => groups.Contains(id) || objects.ContainsKey(id)).ToArray();
            if (members.Length == 0) return PdfLayerVisibility.Visible;
            var shown = members.Select(member => !hidden.Contains(member)).ToArray();
            var visible = ReadName(EntryValue(entries, "P")) switch
            {
                "AllOn" => shown.All(value => value),
                "AnyOff" => shown.Any(value => !value),
                "AllOff" => shown.All(value => !value),
                _ => shown.Any(value => value),
            };
            return visible ? PdfLayerVisibility.Visible : PdfLayerVisibility.Hidden;
        }

        /// <summary>Evaluates the property list a <c>/OC /Name BDC</c> names in its resources.</summary>
        public PdfLayerVisibility EvaluateName(string name, PdfResourceScope? scope) =>
            scope?.Properties is { } properties && properties.TryGetValue(name, out var id) ? Evaluate(id) : PdfLayerVisibility.Unknown;

        private static string? ResolveArray(string? value, IReadOnlyDictionary<int, string> objects) =>
            value is not null && TryReadReference(value, out var id) ? objects.GetValueOrDefault(id) : value;
    }

    /// <summary>The <c>/Properties</c> a resource dictionary names (marked-content property lists,
    /// among them optional content groups), by name and object.</summary>
    private static IReadOnlyDictionary<string, int>? ReadPropertyResources(string? value, IReadOnlyDictionary<int, string> objects)
    {
        if (ResolveDictionary(value, objects) is not { } dictionary) return null;
        var properties = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, property) in ReadDictionaryEntries(dictionary))
            if (TryReadReference(property, out var id)) properties[name] = id;
        return properties.Count > 0 ? properties : null;
    }
}
