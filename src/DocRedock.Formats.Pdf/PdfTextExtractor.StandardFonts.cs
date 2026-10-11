namespace DocRedock.Formats.Pdf;

public static partial class PdfTextExtractor
{
    private static PdfGlyphWidths? ReadStandardWidths(IReadOnlyList<(string Key, string Value)> font, IReadOnlyDictionary<int, string> objects)
    {
        // Only the exact standard Type1 names identify built-in metrics. A subset or an embedded
        // lookalike may have different advances; explicit widths, even malformed ones, take precedence.
        if (ReadName(EntryValue(font, "Subtype")) != "Type1" || EntryValue(font, "Widths") is not null ||
            ReadName(EntryValue(font, "BaseFont")) is not { } name || !PdfStandardFontMetrics.Contains(name)) return null;
        var builtIn = name switch
        {
            "Symbol" => PdfStandardFontMetrics.SymbolNames,
            "ZapfDingbats" => PdfStandardFontMetrics.DingbatNames,
            _ => Names(StandardNames),
        };
        var encoding = ResolveValue(EntryValue(font, "Encoding"), objects)?.TrimStart();
        string? differences = null;
        if (encoding is { Length: > 0 })
        {
            string? baseName;
            if (encoding[0] == '/') baseName = ReadName(encoding);
            else if (encoding.StartsWith("<<", StringComparison.Ordinal))
            {
                var entries = ReadDictionaryEntries(encoding);
                baseName = ReadName(ResolveValue(EntryValue(entries, "BaseEncoding"), objects));
                differences = ResolveValue(EntryValue(entries, "Differences"), objects);
            }
            else return null;
            builtIn = baseName switch
            {
                null => builtIn,
                "StandardEncoding" => Names(StandardNames),
                "WinAnsiEncoding" => Names(WinAnsiNames),
                "MacRomanEncoding" => Names(MacRomanNames),
                "MacExpertEncoding" => Names(MacExpertNames),
                _ => null,
            };
            if (builtIn is null) return null;
        }
        var glyphs = (string[])builtIn.Clone();
        if (differences is not null)
        {
            var index = SkipSpace(differences, 0);
            if (index >= differences.Length || differences[index] != '[') return null;
            var code = 0;
            for (index++; ;)
            {
                index = SkipSpace(differences, index);
                if (index >= differences.Length) return null;
                if (differences[index] == ']') break;
                var end = differences[index] == '/' ? NameEnd(differences, index + 1) : NameEnd(differences, index);
                if (end == index) return null;
                if (differences[index] == '/')
                {
                    if (code is >= 0 and < 256) glyphs[code] = DecodeName(differences[(index + 1)..end]);
                    code++;
                }
                else if (ReadNumber(differences[index..end]) is { } number && number >= 0 && number < 65_536 && number == Math.Floor(number))
                    code = (int)number;
                else return null;
                index = end;
            }
        }
        return PdfGlyphWidths.Simple(glyphs.Select(glyph => PdfStandardFontMetrics.Width(name, glyph)).ToArray(), 0, 0.001);

        static string[] Names(string data)
        {
            var names = new string[256];
            Array.Fill(names, ".notdef");
            data.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries).CopyTo(names, 0x20);
            return names;
        }
    }
}
