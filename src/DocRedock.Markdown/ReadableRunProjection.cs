using System.Text.Json;
using DocRedock.Core.Documents;

namespace DocRedock.Markdown;

internal static class ReadableRunProjection
{
    public static string Clean(string text) => text.Any(c => c is >= '\uFDD0' and <= '\uFDD7')
        ? string.Concat(text.Select(c => c is >= '\uFDD0' and <= '\uFDD7' ? '\uFFFD' : c)) : text;

    public static DocumentNode Clean(DocumentNode node) => node.Content switch
    {
        TextNodeContent text when HasMarker(text.Text) => node with { Content = text with { Text = Clean(text.Text) } },
        RichTextNodeContent rich when rich.Runs.Any(r => HasMarker(r.Text)) => node with { Content = rich with { Runs = rich.Runs.Select(r => r with { Text = Clean(r.Text) }).ToArray() } },
        TableNodeContent table when table.Rows.Any(row => row.Any(c => HasMarker(c.Text))) => node with { Content = table with { Rows = table.Rows.Select(row => (IReadOnlyList<TableCell>)row.Select(c => c with { Text = Clean(c.Text) }).ToArray()).ToArray() } },
        ReferenceNodeContent reference when reference.AltText is { } alt && HasMarker(alt) => node with { Content = reference with { AltText = Clean(alt) } },
        _ => node,
    };

    private static bool HasMarker(string text) => text.Any(c => c is >= '\uFDD0' and <= '\uFDD7');

    public static ReadableContent Content(DocumentNode node, string? display = null)
    {
        IReadOnlyList<TextRun> runs = node.Content is RichTextNodeContent rich && display is null ? rich.Runs :
            [new TextRun(display ?? (node.Content switch { TextNodeContent t => t.Text, ReferenceNodeContent r => r.AltText ?? r.Reference, _ => "" }))];
        return new(Decorate(node, runs));
    }

    public static IReadOnlyList<TextRun> Decorate(DocumentNode node, IReadOnlyList<TextRun> runs)
    {
        // A cell's resolved hyperlink target becomes every run's link; runs that already carry one keep it.
        if (ReadableGraphPreparation.Extension(node, "hyperlink_target").ValueKind == JsonValueKind.String &&
            ReadableGraphPreparation.Extension(node, "hyperlink_target").GetString() is { Length: > 0 } linkTarget)
            runs = runs.Select(run => run.LinkTarget is null ? run with { LinkTarget = linkTarget } : run).ToArray();
        var length = runs.Sum(r => r.Text.Length);
        var keys = new[] { "strike_ranges", "underline_ranges", "insert_ranges", "delete_ranges" };
        var ranges = keys.Select(key => Ranges(ReadableGraphPreparation.Extension(node, key), length)).ToArray();
        var whole = new[] { "strike", "underline", "inserted", "deleted" }.Select(key => ReadableGraphPreparation.ExtensionBool(node, key)).ToArray();
        if (ranges.All(r => r.Count == 0) && whole.All(v => !v)) return runs;
        var output = new List<TextRun>(); var position = 0;
        foreach (var run in runs)
        {
            var cuts = new SortedSet<int> { 0, run.Text.Length };
            foreach (var (start, end) in ranges.SelectMany(r => r))
            {
                if (start > position && start < position + run.Text.Length) cuts.Add(start - position);
                if (end > position && end < position + run.Text.Length) cuts.Add(end - position);
            }
            var points = cuts.ToArray();
            for (var i = 1; i < points.Length; i++)
            {
                var start = position + points[i - 1]; var end = position + points[i];
                bool Marked(int k) => whole[k] || ranges[k].Any(r => r.Start <= start && r.End >= end);
                output.Add(run with { Text = Clean(run.Text[points[i - 1]..points[i]]), Strike = run.Strike || Marked(0),
                    Underline = run.Underline || Marked(1), Inserted = run.Inserted || Marked(2), Deleted = run.Deleted || Marked(3) });
            }
            position += run.Text.Length;
        }
        return output;
    }

    private static List<(int Start, int End)> Ranges(JsonElement value, int length)
    {
        var ranges = new List<(int, int)>();
        if (value.ValueKind != JsonValueKind.Array) return ranges;
        foreach (var pair in value.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2 || pair[0].ValueKind != JsonValueKind.Number || pair[1].ValueKind != JsonValueKind.Number || !pair[0].TryGetInt32(out var start) || !pair[1].TryGetInt32(out var end) || start < 0 || end < start || end > length) return [];
            if (end > start) ranges.Add((start, end));
        }
        return ranges;
    }
}
