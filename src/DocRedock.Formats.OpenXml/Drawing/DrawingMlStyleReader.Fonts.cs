using System.Text;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.OpenXml.Drawing;

public static partial class DrawingMlStyleReader
{
    private static string? Nonempty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static IReadOnlyList<DrawingTextRun> BuildScriptRuns(RunStyle style, string text, StyleContext ctx)
    {
        if (text.Length == 0 || style.EastAsianFont is null && style.ComplexScriptFont is null)
            return [Build(style, text)];
        var segments = new List<(int Start, int Length, int Script)>();
        var start = 0; var position = 0; var previous = -1;
        foreach (var rune in text.EnumerateRunes())
        {
            var script = TypefaceScript(rune.Value);
            if (script != previous)
            {
                if (position > start) segments.Add((start, position - start, previous));
                if (segments.Count >= 128)
                {
                    ctx.Warn("Mixed-script font segmentation limit exceeded; complete text retained in its base typeface.");
                    return [Build(style, text)];
                }
                start = position; previous = script;
            }
            position += rune.Utf16SequenceLength;
        }
        if (position > start) segments.Add((start, position - start, previous));
        var runs = segments.Select(segment =>
        {
            var font = segment.Script == 1 ? style.EastAsianFont : segment.Script == 2 ? style.ComplexScriptFont : null;
            var run = Build(style, text.Substring(segment.Start, segment.Length));
            // DrawingML saves the East Asian language in lang or in altLang, depending on the application.
            var language = segment.Script != 1 ? style.Language
                : new[] { style.AlternateLanguage, style.Language }.FirstOrDefault(l => SupplementalScript(l) is "Jpan" or "Hang" or "Hans" or "Hant")
                    ?? style.AlternateLanguage ?? style.Language;
            return font is null ? run : run with { FontFamily = ResolveTypeface(font, ctx, language, scriptOnly: true) ?? run.FontFamily };
        }).ToList();
        // A script the theme names no typeface for stays in the run's own font: no boundary is left there.
        for (var index = runs.Count - 1; index > 0; index--)
            if (runs[index].FontFamily == runs[index - 1].FontFamily)
            {
                runs[index - 1] = runs[index - 1] with { Text = runs[index - 1].Text + runs[index].Text };
                runs.RemoveAt(index);
            }
        return runs;
    }

    private static int TypefaceScript(int value) => value switch
    {
        >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7AF or >= 0xF900 and <= 0xFAFF or
        >= 0xFE10 and <= 0xFE1F or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFFEF or
        >= 0x20000 and <= 0x323AF => 1,
        >= 0x0590 and <= 0x109F or >= 0x1780 and <= 0x17FF or >= 0xFB1D and <= 0xFDFF or
        >= 0xFE70 and <= 0xFEFF => 2,
        _ => 0,
    };

    // Theme supplemental fonts are selected by the source run's declared language,
    // rather than guessing a Han script's language from identical Unicode characters.
    private static string? SupplementalScript(string? language)
    {
        var lang = language?.ToLowerInvariant();
        if (lang?.StartsWith("ja", StringComparison.Ordinal) == true) return "Jpan";
        if (lang?.StartsWith("ko", StringComparison.Ordinal) == true) return "Hang";
        if (lang?.StartsWith("zh", StringComparison.Ordinal) == true)
            return lang.Contains("tw", StringComparison.Ordinal) || lang.Contains("hk", StringComparison.Ordinal) || lang.Contains("hant", StringComparison.Ordinal) ? "Hant" : "Hans";
        return lang?.Split('-')[0] switch
        {
            "ar" or "fa" or "ur" => "Arab", "he" or "yi" => "Hebr", "th" => "Thai",
            "hi" or "mr" or "ne" => "Deva", "bn" => "Beng", "ta" => "Taml", "te" => "Telu",
            "gu" => "Gujr", "pa" => "Guru", "kn" => "Knda", "ml" => "Mlym", "km" => "Khmr",
            _ => null,
        };
    }
}
