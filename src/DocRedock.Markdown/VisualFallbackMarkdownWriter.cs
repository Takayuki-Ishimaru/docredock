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

    /// <summary>More unresolved connections and diagnostics than this are summarized instead of listed one by one.</summary>
    internal const int InlineDetailLimit = 5;

    /// <summary>True when a projected diagram has so many unresolved parts that listing them would bury the body.</summary>
    internal static bool ShouldSummarize(VisualGraph graph) =>
        UnresolvedCount(graph) + (graph.Diagnostics ?? []).Count(item => item is not null) > InlineDetailLimit;

    private static int UnresolvedCount(VisualGraph graph) =>
        (graph.Edges ?? []).Count(edge => edge is not null && (edge.SourceId is null || edge.TargetId is null));

    /// <summary>One short sentence naming how many connections could not be resolved and where the details are, followed by
    /// the diagnostics grouped by code in a collapsed block (the full list stays in the diagnostics report).</summary>
    internal static void WriteSummary(StringBuilder output, VisualGraph graph, bool english)
    {
        var unresolved = UnresolvedCount(graph);
        var diagnostics = (graph.Diagnostics ?? []).Where(item => item is not null).ToArray();
        if (english)
            output.Append("> ").Append(unresolved > 0
                    ? unresolved.ToString(CultureInfo.InvariantCulture) + " connections in this figure could not be resolved."
                    : "This figure was extracted with " + diagnostics.Length.ToString(CultureInfo.InvariantCulture) + " diagnostics.")
                .AppendLine(" Details are in the diagnostics of report.json / review.md.").AppendLine();
        else
            output.Append("> ").Append(unresolved > 0
                    ? "この図の接続のうち " + unresolved.ToString(CultureInfo.InvariantCulture) + " 件は接続元・接続先を特定できませんでした。"
                    : "この図の抽出で " + diagnostics.Length.ToString(CultureInfo.InvariantCulture) + " 件の診断が出ています。")
                .AppendLine("詳細は report.json / review.md の診断を参照してください。").AppendLine();
        if (diagnostics.Length == 0) return;
        output.AppendLine("<details class=\"visual-diagnostics\">")
            .Append("<summary>").Append(english ? "Diagnostics of this figure" : "この図の診断の内訳").AppendLine("</summary>").AppendLine();
        foreach (var group in diagnostics.GroupBy(item => item.Code, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var message = group.First().Message.Replace('\r', ' ').Replace('\n', ' ');
            if (message.Length > 240) message = message[..240] + "…";
            output.Append("- ").Append(group.Key).Append(" ×").Append(group.Sum(item => Math.Max(1, item.Count)).ToString(CultureInfo.InvariantCulture))
                .Append(" — ").AppendLine(message);
        }
        output.AppendLine().AppendLine("</details>").AppendLine();
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
                yield return ["パス: ", path.Id, LineStyleSuffix(path.LineStyle)];
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
                yield return ["接続先未確定: ", string.IsNullOrWhiteSpace(edge.Label) ? "接続先を一意に判定できないコネクター" : edge.Label,
                    LineStyleSuffix(edge.LineStyle)];
    }

    // Unresolved strokes keep their source line style, so a dashed line under review is not
    // described as an ordinary one.
    private static string? LineStyleSuffix(string? lineStyle) =>
        VisualLineStyles.IsKnown(lineStyle) ? "（" + ReadableMarkdownSerializer.LineStyleName(lineStyle) + "）" : null;
}
