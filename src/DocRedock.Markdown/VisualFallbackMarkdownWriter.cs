using System.Globalization;
using System.Text;
using DocRedock.Core.Documents;

namespace DocRedock.Markdown;

/// <summary>Bounds only visual fallback details; native text and semantic projections have separate lifetimes.</summary>
internal static class VisualFallbackMarkdownWriter
{
    internal const int MaxItems = 100;
    internal const int MaxCharacters = 32_768;
    private const int SummaryReserve = 256;
    private const int MaxFieldCharacters = 2_048;

    internal sealed record Result(int Emitted, int Omitted, bool Shortened);

    internal static Result Write(StringBuilder output, VisualGraph graph, bool partial,
        int maxItems = MaxItems, int maxCharacters = MaxCharacters)
    {
        maxItems = Math.Clamp(maxItems, 0, MaxItems);
        maxCharacters = Math.Clamp(maxCharacters, SummaryReserve, MaxCharacters);
        var block = new StringBuilder();
        block.AppendLine(partial ? "#### 未解決の視覚接続" : "### 図の抽出結果（フォールバック）").AppendLine();
        var emitted = 0;
        var omitted = 0;
        var shortened = false;
        foreach (var fields in Details(graph, partial))
        {
            if (emitted >= maxItems) { omitted++; continue; }
            var line = new StringBuilder("- ");
            foreach (var field in fields)
            {
                var text = field ?? string.Empty;
                if (text.Length > MaxFieldCharacters)
                {
                    text = text[..MaxFieldCharacters] + "…";
                    shortened = true;
                }
                line.Append(text.Replace('\r', ' ').Replace('\n', ' '));
            }
            line.AppendLine();
            if (block.Length + line.Length > maxCharacters - SummaryReserve) { omitted++; continue; }
            block.Append(line);
            emitted++;
        }
        if (omitted > 0 || shortened)
            block.Append("> VisualFallbackCompacted: ")
                .Append(emitted.ToString(CultureInfo.InvariantCulture)).Append(" details shown; ")
                .Append(omitted.ToString(CultureInfo.InvariantCulture))
                .AppendLine(" additional details omitted; long fields may be shortened.").AppendLine();
        else block.AppendLine();
        output.Append(block);
        return new(emitted, omitted, shortened);
    }

    private static IEnumerable<string?[]> Details(VisualGraph graph, bool partial)
    {
        if (!partial)
        {
            foreach (var node in (graph.Nodes ?? []).Where(item => item is not null).OrderBy(item => item.Id, StringComparer.Ordinal))
                yield return ["ノード: ", string.IsNullOrWhiteSpace(node.Label) ? node.Id : node.Label];
            var pathIds = (graph.Paths ?? []).Where(item => item is not null).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var item in (graph.SourceItems ?? []).Where(item => item is not null &&
                         item.Disposition is VisualDisposition.VisualFallback or VisualDisposition.DiagnosticOnly &&
                         (item.FallbackPathId is null || !pathIds.Contains(item.FallbackPathId)))
                         .OrderBy(item => item.Id, StringComparer.Ordinal))
                yield return ["ソース項目: ", item.Id, "（", item.Disposition.ToString(), "）"];
            foreach (var path in (graph.Paths ?? []).Where(item => item is not null && item.IsFallback))
                yield return ["パス: ", path.Id];
        }
        foreach (var group in (graph.Diagnostics ?? []).Where(item => item is not null)
                     .GroupBy(item => item.Code, StringComparer.Ordinal))
        {
            var count = 0;
            foreach (var diagnostic in group)
            {
                count++;
                if (count <= 10) yield return ["診断: ", diagnostic.Code, " — ", diagnostic.Message];
            }
            if (count > 10)
                yield return ["診断: ", group.Key, " — ", (count - 10).ToString(CultureInfo.InvariantCulture), " additional diagnostics."];
        }
        if (partial)
            foreach (var edge in (graph.Edges ?? []).Where(edge => edge is not null &&
                         (edge.SourceId is null || edge.TargetId is null)).OrderBy(edge => edge.Id, StringComparer.Ordinal))
                yield return ["接続先未確定: ", string.IsNullOrWhiteSpace(edge.Label) ? "接続先を一意に判定できないコネクター" : edge.Label];
    }
}
