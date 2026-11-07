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
        // The note DocRedock puts on a page without text is no text of the document. A PDF with none (a scan) is
        // described in the language of its file name, or else of the system.
        var sample = string.Concat(graph.Nodes.Where(node => node.Kind is NodeKind.Paragraph or NodeKind.Heading or NodeKind.Cell &&
                node.Extensions?.ContainsKey("pdf_textless_placeholder") != true)
            .Take(200).Select(NodeText));
        if (sample.Trim().Length == 0) sample = options.FallbackTitle ?? string.Empty;
        EnglishOutput = options.Language == "en" || options.Language is null && graph.Format == DocumentFormatKind.Pdf &&
            (sample.Any(char.IsAsciiLetter) && !sample.Any(IsJapanese) ||
             !sample.Any(char.IsLetter) && System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en");
    }

    private static bool IsJapanese(char c) => c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff';

    private static bool HasResolvedVisualEdges(DocumentNode node)
    {
        if (node.Extensions?.TryGetValue("visual_graph", out var value) != true) return false;
        try { return value.Deserialize<VisualGraph>()?.Edges?.Any(edge => edge.SourceId is not null && edge.TargetId is not null) == true; }
        catch (JsonException) { return false; }
    }
}
