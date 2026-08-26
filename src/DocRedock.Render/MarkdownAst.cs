using System.Text.RegularExpressions;

namespace DocRedock.Render;

public abstract record MarkdownBlock;
public sealed record MarkdownHeading(int Level, string Text) : MarkdownBlock;
public sealed record MarkdownParagraph(string Text) : MarkdownBlock;
public sealed record MarkdownList(
    IReadOnlyList<string> Items,
    IReadOnlyList<int>? Levels = null,
    IReadOnlyList<bool>? Ordered = null) : MarkdownBlock;
public sealed record MarkdownCodeBlock(string Language, string Text) : MarkdownBlock;
public sealed record MarkdownTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows) : MarkdownBlock;
public sealed record MarkdownDocument(IReadOnlyList<MarkdownBlock> Blocks);
internal sealed record MarkdownDiagram(string Source, string AltText, PngRasterImage Image) : MarkdownBlock;

public static class MarkdownAstParser
{
    private static readonly Regex Heading = new(@"^(?<marks>#{1,6})\s+(?<text>.*)$", RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"^(?<indent>\s*)(?<marker>[-*+]|\d+[.)])\s+(?<text>.*)$", RegexOptions.Compiled);

    public static MarkdownDocument Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var blocks = new List<MarkdownBlock>();
        for (var i = 0; i < lines.Length;)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) { i++; continue; }
            var heading = Heading.Match(lines[i]);
            if (heading.Success) { blocks.Add(new MarkdownHeading(heading.Groups["marks"].Length, heading.Groups["text"].Value.Trim())); i++; continue; }
            if (lines[i].StartsWith("```", StringComparison.Ordinal))
            {
                var language = lines[i][3..].Trim();
                var start = ++i;
                while (i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal)) i++;
                blocks.Add(new MarkdownCodeBlock(language, string.Join("\n", lines[start..i])));
                if (i < lines.Length) i++;
                continue;
            }
            if (ListItem.IsMatch(lines[i]))
            {
                var items = new List<string>();
                var levels = new List<int>();
                var ordered = new List<bool>();
                while (i < lines.Length && ListItem.IsMatch(lines[i]))
                {
                    var match = ListItem.Match(lines[i]);
                    items.Add(match.Groups["text"].Value.Trim());
                    var indent = match.Groups["indent"].Value.Replace("\t", "  ", StringComparison.Ordinal).Length;
                    levels.Add(indent / 2);
                    ordered.Add(char.IsDigit(match.Groups["marker"].Value[0]));
                    i++;
                }
                blocks.Add(new MarkdownList(items, levels, ordered));
                continue;
            }
            if (IsTableHeader(lines, i))
            {
                var headers = SplitTableRow(lines[i++]);
                i++; // separator row
                var rows = new List<IReadOnlyList<string>>();
                while (i < lines.Length && lines[i].Contains('|', StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(lines[i])) rows.Add(SplitTableRow(lines[i++]));
                blocks.Add(new MarkdownTable(headers, rows));
                continue;
            }
            var paragraph = new List<string> { lines[i++] };
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !Heading.IsMatch(lines[i]) && !ListItem.IsMatch(lines[i]) && !lines[i].StartsWith("```", StringComparison.Ordinal)) paragraph.Add(lines[i++]);
            blocks.Add(new MarkdownParagraph(string.Join("\n", paragraph).Trim()));
        }
        return new MarkdownDocument(blocks);
    }

    private static bool IsTableHeader(string[] lines, int index) => index + 1 < lines.Length && lines[index].Contains('|', StringComparison.Ordinal) && Regex.IsMatch(lines[index + 1], @"^\s*\|?\s*:?-{3,}");

    private static IReadOnlyList<string> SplitTableRow(string line)
    {
        var normalized = line.Trim();
        if (normalized.StartsWith('|')) normalized = normalized[1..];
        if (normalized.EndsWith('|')) normalized = normalized[..^1];
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var escaped = false;
        foreach (var character in normalized)
        {
            if (escaped) { current.Append(character); escaped = false; continue; }
            if (character == '\\') { escaped = true; continue; }
            if (character == '|') { cells.Add(current.ToString().Trim().Replace("<br>", "\n", StringComparison.Ordinal)); current.Clear(); continue; }
            current.Append(character);
        }
        if (escaped) current.Append('\\');
        cells.Add(current.ToString().Trim().Replace("<br>", "\n", StringComparison.Ordinal));
        return cells;
    }
}
