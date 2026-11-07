using System.Text;
using System.Text.RegularExpressions;

namespace DocRedock.Formats.Pdf;

public static partial class PdfTextExtractor
{
    // A description longer than this is cut: it describes an image, it is no text of the page.
    private const int MaxImageDescriptionLength = 2_000;

    /// <summary>The descriptions a tagged PDF gives its figures (a structure element /S /Figure with /Alt), by page, in the
    /// order the structure lists them: what an image shows, often the figures of a chart, which the page's own text does
    /// not hold. A figure is on the page its /Pg names, or on the only page.</summary>
    private static Dictionary<int, List<string>> ReadFigureDescriptions(string structure, TimeSpan timeout)
    {
        var result = new Dictionary<int, List<string>>();
        if (!structure.Contains("/Figure", StringComparison.Ordinal) || !structure.Contains("/Alt", StringComparison.Ordinal)) return result;
        var pages = new Dictionary<int, int>();
        var figures = new List<(int? Page, string Alt)>();
        foreach (Match item in Regex.Matches(structure, @"(?<id>\d+)\s+\d+\s+obj\b(?<body>.*?)(?=\bendobj\b)", RegexOptions.Singleline, timeout))
        {
            var body = item.Groups["body"].Value;
            if (Regex.IsMatch(body, @"/Type\s*/Page\b", RegexOptions.None, timeout))
            {
                pages[int.Parse(item.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture)] = pages.Count + 1;
                continue;
            }
            if (!Regex.IsMatch(body, @"/S\s*/Figure\b", RegexOptions.None, timeout) || AltText(body) is not { } alt) continue;
            var page = Regex.Match(body, @"/Pg\s+(?<page>\d+)\s+\d+\s+R\b", RegexOptions.None, timeout) is { Success: true } reference
                ? int.Parse(reference.Groups["page"].Value, System.Globalization.CultureInfo.InvariantCulture)
                : (int?)null;
            figures.Add((page, alt));
        }
        foreach (var (page, alt) in figures)
        {
            var number = page is { } id && pages.TryGetValue(id, out var found) ? found : pages.Count == 1 ? 1 : 0;
            if (number == 0) continue;
            if (!result.TryGetValue(number, out var list)) result[number] = list = [];
            list.Add(alt);
        }
        return result;
    }

    /// <summary>The /Alt string of a dictionary, decoded (UTF-16 with its byte order mark, or PDFDocEncoding read as
    /// Latin-1), with its white space folded; null when it has none or it is empty.</summary>
    private static string? AltText(string dictionary)
    {
        var at = dictionary.IndexOf("/Alt", StringComparison.Ordinal);
        while (at >= 0 && at + 4 < dictionary.Length && char.IsLetterOrDigit(dictionary[at + 4]))
            at = dictionary.IndexOf("/Alt", at + 4, StringComparison.Ordinal);
        if (at < 0) return null;
        var start = at + 4;
        while (start < dictionary.Length && char.IsWhiteSpace(dictionary[start])) start++;
        if (start >= dictionary.Length) return null;
        string token;
        if (dictionary[start] == '(')
        {
            var depth = 0;
            var end = start;
            for (; end < dictionary.Length; end++)
            {
                var character = dictionary[end];
                if (character == '\\') { end++; continue; }
                if (character == '(') depth++;
                else if (character == ')' && --depth == 0) break;
            }
            if (end >= dictionary.Length) return null;
            token = dictionary[start..(end + 1)];
        }
        else if (dictionary[start] == '<' && dictionary.IndexOf('>', start) is var close and > 0)
            token = dictionary[start..(close + 1)];
        else return null;
        var bytes = StringBytes(token);
        var text = bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2)
            : bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
            : Encoding.Latin1.GetString(bytes);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length == 0) return null;
        return text.Length > MaxImageDescriptionLength ? text[..MaxImageDescriptionLength] + "…" : text;
    }
}
