using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DocRedock.Api;

public sealed record AiPackageSearchLine(int Number, string Text);
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
    private static string Normalize(string text) => WebUtility.HtmlDecode(text).Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    public static IReadOnlyList<AiPackageSearchMatch> Find(AiPackageVerifiedPackage package, string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var needle = Normalize(Escapes.Replace(query, "$1"));
        var matches = new List<AiPackageSearchMatch>();
        foreach (var part in package.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lines = part.Markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var found = new List<AiPackageSearchLine>();
            for (var index = 0; index < lines.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var plain = Escapes.Replace(lines[index], "$1");
                var searchable = Escapes.Replace(Markup.Replace(lines[index], ""), "$1");
                if (Normalize(plain).Contains(needle, StringComparison.Ordinal) || Normalize(searchable).Contains(needle, StringComparison.Ordinal))
                    found.Add(new(index + 1, lines[index]));
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
