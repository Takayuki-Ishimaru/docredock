using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Markdown;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DocRedock.Tests.Markdown;

/// <summary>
/// Positive-path companion to MarkdownLiteralSyntaxGateTests: proves the D07 escaping fix
/// (EscapeLiteral / DocRedockInlineMarkdown.Escape backslash-escaping '[' and ']') does not
/// collateral-damage real, app-generated syntax that legitimately uses brackets or underscores --
/// Mermaid diagrams (which use their own HTML-entity quoting, not backslash escapes), OCR detail
/// blocks, real embedded images with bracketed alt text, and rich-text runs with a real hyperlink
/// whose label itself contains brackets.
/// </summary>
public sealed class MarkdownGeneratedSyntaxRegressionTests
{
    [Fact]
    public void Mermaid_node_labels_are_html_entity_escaped_not_backslash_escaped()
    {
        var visual = new VisualGraph("flow",
            [new VisualNode("n1", "Step_1 [a]"), new VisualNode("n2", "Step_2 [b]")],
            [new VisualEdge("e1", "n1", "n2")]);
        var diagram = new DocumentNode("diagram", NodeKind.Diagram, null, 0, ContentLayer.Derived,
            new TextNodeContent("derived visual"), Extensions: new Dictionary<string, JsonElement>
            {
                ["visual_graph"] = JsonSerializer.SerializeToElement(visual)
            });
        var literalOutsideFence = new DocumentNode("literal", NodeKind.Paragraph, null, 1, ContentLayer.Body,
            new TextNodeContent("Step_1 [a] appears again as literal text"));
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "mermaid-escaping", DocumentFormatKind.Pptx,
            [new DocumentPartition("slide1", 0, [diagram, literalOutsideFence])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);
        var document = MarkdownStructure.Parse(markdown);

        var fences = ((MarkdownObject)document).Descendants<FencedCodeBlock>().ToArray();
        var fence = Assert.Single(fences);
        Assert.Equal("mermaid", fence.Info);

        var fenceStart = markdown.IndexOf("```mermaid", StringComparison.Ordinal);
        Assert.True(fenceStart >= 0, "Expected a ```mermaid fence in the output.");
        var bodyStart = markdown.IndexOf('\n', fenceStart) + 1;
        var fenceEnd = markdown.IndexOf("```", bodyStart, StringComparison.Ordinal);
        Assert.True(fenceEnd > bodyStart);
        var fenceBody = markdown[bodyStart..fenceEnd];

        // Mermaid's own quoting (MermaidText): '[' / ']' become HTML entities, '_' is untouched.
        Assert.Contains("Step_1 &#91;a&#93;", fenceBody, StringComparison.Ordinal);
        Assert.Contains("Step_2 &#91;b&#93;", fenceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\\[", fenceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\\]", fenceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\\_", fenceBody, StringComparison.Ordinal);

        // Outside the fence, the same literal text is unaffected: EscapeLiteral (D07) still
        // backslash-escapes '_' as well as the brackets.
        Assert.Contains(@"Step\_1 \[a\] appears again as literal text", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Ocr_extracted_text_is_escaped_inside_the_details_wrapper_and_forms_no_link()
    {
        var image = new DocumentNode("image", NodeKind.Image, null, 0, ContentLayer.Body,
            new ReferenceNodeContent("media/scan.png", "Scan"));
        var ocr = new DocumentNode("ocr", NodeKind.ImageText, "image", 1, ContentLayer.Derived,
            new TextNodeContent("OCR [x](y) text"));
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "ocr-literal", DocumentFormatKind.Docx,
            [new DocumentPartition("document", 0, [image, ocr])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);
        var document = MarkdownStructure.Parse(markdown);

        Assert.Contains("<details class=\"ocr-extraction\">", markdown, StringComparison.Ordinal);
        Assert.Contains("<summary>OCR\u62bd\u51fa\u30c6\u30ad\u30b9\u30c8\uff08\u30af\u30ea\u30c3\u30af\u3067\u5c55\u958b\uff09</summary>", markdown, StringComparison.Ordinal);
        Assert.Contains("</details>", markdown, StringComparison.Ordinal);
        Assert.Contains("> OCR \\[x\\](y) text  ", markdown, StringComparison.Ordinal);
        Assert.Empty(MarkdownStructure.Links(document));
    }

    [Fact]
    public void Real_image_alt_text_with_brackets_displays_unescaped_and_forms_no_extra_link()
    {
        var image = new DocumentNode("image", NodeKind.Image, null, 0, ContentLayer.Body,
            new ReferenceNodeContent("media/diagram.png", "Diagram [v2]"));
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "image-alt", DocumentFormatKind.Docx,
            [new DocumentPartition("document", 0, [image])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);
        var document = MarkdownStructure.Parse(markdown);

        var real = Assert.Single(MarkdownStructure.Images(document));
        Assert.Equal("media/diagram.png", real.Url);
        Assert.Empty(MarkdownStructure.Links(document));
        var displayed = MarkdownStructure.DisplayedText(document);
        Assert.Contains("Diagram [v2]", displayed, StringComparison.Ordinal);
        Assert.DoesNotContain(@"Diagram \[v2\]", displayed, StringComparison.Ordinal);
    }

    private static DocumentNode RichLinkParagraph() => new("rich", NodeKind.Paragraph, null, 0, ContentLayer.Body,
        new RichTextNodeContent(
        [
            new TextRun("Bold", Bold: true),
            new TextRun(" "),
            new TextRun("Italic", Italic: true),
            new TextRun(" "),
            new TextRun("Struck", Strike: true),
            new TextRun(" "),
            new TextRun("code", Code: true),
            new TextRun(" "),
            new TextRun("see [spec]", LinkTarget: "https://example.com/spec"),
        ]));

    [Fact]
    public void Rich_text_paragraph_preserves_emphasis_and_a_bracketed_link_label()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "rich-link", DocumentFormatKind.Docx,
            [new DocumentPartition("document", 0, [RichLinkParagraph()])]);

        var markdown = new ReadableMarkdownSerializer().Serialize(graph);
        var document = MarkdownStructure.Parse(markdown);

        var link = Assert.Single(MarkdownStructure.Links(document));
        Assert.Equal("https://example.com/spec", link.Url);
        Assert.Equal("see [spec]", PlainText(link.FirstChild));
        Assert.Contains("see [spec]", MarkdownStructure.DisplayedText(document), StringComparison.Ordinal);

        var emphasis = ((MarkdownObject)document).Descendants<EmphasisInline>().ToArray();
        Assert.Contains(emphasis, item => item.DelimiterCount == 2 && PlainText(item.FirstChild) == "Bold");
        Assert.Contains(emphasis, item => item.DelimiterCount == 1 && PlainText(item.FirstChild) == "Italic");
        Assert.Contains(((MarkdownObject)document).Descendants<CodeInline>(), item => item.Content.ToString() == "code");
        Assert.Contains("~~Struck~~", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Rich_text_paragraph_round_trips_through_drmd_projection_without_a_diff()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "rich-roundtrip", DocumentFormatKind.Docx,
            [new DocumentPartition("document", 0, [RichLinkParagraph()])]);

        var markdown = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;
        var document = MarkdownStructure.Parse(markdown);
        var link = Assert.Single(MarkdownStructure.Links(document));
        Assert.Equal("https://example.com/spec", link.Url);

        var result = new MarkdownGraphEditor().Apply(graph, markdown);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Empty(result.Diff.PatchSet.Operations);
    }

    private static string PlainText(Inline? first)
    {
        var text = new StringBuilder();
        for (var inline = first; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal: text.Append(literal.Content.ToString()); break;
                case ContainerInline container: text.Append(PlainText(container.FirstChild)); break;
            }
        }
        return text.ToString();
    }
}
