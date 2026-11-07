using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DocRedock.Api;

/// <summary>A matching line: its number in the part, its text, and the heading it lies under (null before any).</summary>
public sealed record AiPackageSearchLine(int Number, string Text)
{
    public string? Heading { get; init; }
}
public sealed record AiPackageSearchMatch(string PartId, IReadOnlyList<AiPackageManifestLocation> Locations,
    IReadOnlyList<AiPackageSearchLine> Lines)
{
    /// <summary>The part's stored heuristic token estimate, including any export margin;
    /// null when the part records no estimate.</summary>
    public int? EstimatedTokens { get; init; }
    /// <summary>The manifest's recorded multiplier, or null when absent. Independent of whether
    /// the part records an estimate; never applied again by the search.</summary>
    public double? TokenEstimateMultiplier { get; init; }
}

public static class AiPackageSearch
{
    private static readonly Regex Escapes = new(@"\\([!""#$%&'()*+,\-./:;<=>?@\[\]\\^_`{|}~])", RegexOptions.CultureInvariant);
    private static readonly Regex Markup = new(@"(?<!\\)(?:</?(?:u|ins|del|sub|sup)>|\*\*|~~)", RegexOptions.CultureInvariant);
    private static readonly Regex Breaks = new(@"<br\s*/?>|\s+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex HeadingLine = new(@"^#{1,6}\s+(.+?)\s*#*\s*$", RegexOptions.CultureInvariant);
    private static string Normalize(string text) => WebUtility.HtmlDecode(text).Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    // A line as it reads without its white space and line breaks in a table cell: the text a PDF spaced out ("11 月"),
    // or that a line break cut ("見" / "直し"), is still found.
    private static string Compact(string text) => Breaks.Replace(text, "");
    public static IReadOnlyList<AiPackageSearchMatch> Find(AiPackageVerifiedPackage package, string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var needle = Normalize(Escapes.Replace(query, "$1"));
        // A query with a space in it means the space; one without matches text that white space or a line break cuts.
        var compactNeedle = needle.Any(char.IsWhiteSpace) ? string.Empty : needle;
        var matches = new List<AiPackageSearchMatch>();
        foreach (var part in package.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lines = part.Markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var found = new List<AiPackageSearchLine>();
            var metadataEnd = 0;
            if (lines.Length > 1 && lines[0].StartsWith("# ", StringComparison.Ordinal) &&
                lines[1].StartsWith("ソース: ", StringComparison.Ordinal))
            {
                metadataEnd = 1;
                while (metadataEnd < lines.Length && lines[metadataEnd].Length > 0) metadataEnd++;
            }
            string? heading = null;
            string? previous = null;
            var previousMatched = false;
            for (var index = 0; index < lines.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index > 0 && index < metadataEnd) { previous = null; continue; }
                var plain = Escapes.Replace(lines[index], "$1");
                var searchable = Normalize(Escapes.Replace(Markup.Replace(lines[index], ""), "$1"));
                var matched = Normalize(plain).Contains(needle, StringComparison.Ordinal) || searchable.Contains(needle, StringComparison.Ordinal) ||
                    compactNeedle.Length > 0 && Compact(searchable).Contains(compactNeedle, StringComparison.Ordinal);
                if (matched) found.Add(new(index + 1, lines[index]) { Heading = heading });
                // Text a line break cuts: the end of the line above and the start of this one together, reported at the line above.
                else if (!previousMatched && previous is { Length: > 0 } && searchable.Length > 0 && compactNeedle.Length > 0 &&
                         Compact(previous + searchable).Contains(compactNeedle, StringComparison.Ordinal))
                    found.Add(new(index, lines[index - 1]) { Heading = heading });
                if (HeadingLine.Match(lines[index]) is { Success: true } title && index > 0) heading = Escapes.Replace(title.Groups[1].Value, "$1");
                previous = searchable;
                previousMatched = matched;
            }
            if (found.Count > 0) matches.Add(new(part.Part.Id, part.Part.Locations, found)
            {
                EstimatedTokens = part.Part.EstimatedTokens,
                TokenEstimateMultiplier = package.Manifest.TokenEstimateMultiplier,
            });
        }
        return matches;
    }
}
