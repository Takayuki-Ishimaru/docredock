using System.Text.Json;
using DocRedock.Core.Documents;

namespace DocRedock.Markdown;

public sealed partial class ReadableMarkdownSerializer
{
    private bool EnglishOutput { get; set; }

    private void SetOutputLanguage(DocumentGraph graph)
    {
        if (options.Language is not null && options.Language is not ("ja" or "en"))
            throw new ArgumentException("Language must be ja or en.");
        var sample = string.Concat(graph.Nodes.Where(node => node.Kind is NodeKind.Paragraph or NodeKind.Heading or NodeKind.Cell)
            .Take(200).Select(NodeText));
        EnglishOutput = options.Language == "en" || options.Language is null && graph.Format == DocumentFormatKind.Pdf &&
            sample.Any(char.IsAsciiLetter) && !sample.Any(c => c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff');
    }

    private static bool HasResolvedVisualEdges(DocumentNode node)
    {
        if (node.Extensions?.TryGetValue("visual_graph", out var value) != true) return false;
        try { return value.Deserialize<VisualGraph>()?.Edges?.Any(edge => edge.SourceId is not null && edge.TargetId is not null) == true; }
        catch (JsonException) { return false; }
    }
}
