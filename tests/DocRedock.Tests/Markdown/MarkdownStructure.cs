using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DocRedock.Tests.Markdown;

/// <summary>
/// Parser-backed view of produced Markdown, used by the literal-syntax release gate.
/// A string assertion can only say "a backslash is present"; these helpers say what a
/// CommonMark/GFM renderer actually DOES with the output -- whether a link, an image or a
/// link reference definition exists at all, and what text a reader is finally left with.
/// </summary>
internal static class MarkdownStructure
{
    // Pipe tables are the only Markdown extension DocRedock's writers emit. The autolink
    // extension is deliberately NOT enabled: bare URLs in source text are left untouched by
    // design (see EscapeLiteral), so autolinking would report "links" this gate is not about.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();

    internal static MarkdownDocument Parse(string markdown) =>
        global::Markdig.Markdown.Parse(markdown ?? string.Empty, Pipeline);

    /// <summary>Every inline hyperlink a renderer would emit (images excluded).</summary>
    internal static IReadOnlyList<LinkInline> Links(MarkdownDocument document) =>
        AllLinks(document).Where(link => !link.IsImage).ToArray();

    /// <summary>Every inline image a renderer would emit (and try to fetch).</summary>
    internal static IReadOnlyList<LinkInline> Images(MarkdownDocument document) =>
        AllLinks(document).Where(link => link.IsImage).ToArray();

    /// <summary>
    /// Every link reference definition the parser consumed. These are the dangerous ones: a
    /// "[id]: url" line is swallowed by the parser and rendered as nothing at all, so a source
    /// document containing that text would silently lose the line.
    /// </summary>
    internal static IReadOnlyList<LinkReferenceDefinition> ReferenceDefinitions(MarkdownDocument document)
    {
        var found = ((MarkdownObject)document).Descendants<LinkReferenceDefinition>().ToList();
        // Markdig also keeps a per-document lookup group; a definition reachable only through it
        // would still resolve a "[ref][id]", so both views have to be empty for the gate to pass.
        // addGroup: false -- read the parsed document, never attach a group to it.
        if (document.GetLinkReferenceDefinitions(false) is { } group)
            foreach (var block in group)
                if (block is LinkReferenceDefinition definition && !found.Contains(definition)) found.Add(definition);
        return found;
    }

    /// <summary>
    /// The literal text a reader sees, in document order, joined with "\n". Fenced/indented code
    /// blocks and raw HTML blocks are excluded (they are verbatim regions -- DRMD markers, Mermaid
    /// fences, OCR &lt;details&gt; wrappers -- not prose), and so are link reference definitions,
    /// because a renderer displays nothing for them. That omission is exactly what makes this
    /// helper able to prove the "[id]: url" line did not disappear.
    /// </summary>
    internal static string DisplayedText(MarkdownDocument document)
    {
        var blocks = new List<string>();
        foreach (var block in document) AppendBlock(blocks, block);
        return string.Join("\n", blocks);
    }

    /// <summary>Asserts each probe occurs in <paramref name="displayed"/> and that their first
    /// occurrences are strictly increasing, i.e. source order survived.</summary>
    internal static void AssertInOrder(string displayed, params string[] probes)
    {
        var previous = -1;
        var previousProbe = string.Empty;
        foreach (var probe in probes)
        {
            var index = displayed.IndexOf(probe, StringComparison.Ordinal);
            Assert.True(index >= 0,
                $"Literal text '{probe}' did not survive as displayed text.\n--- displayed ---\n{displayed}\n---");
            Assert.True(index > previous,
                $"Literal text '{probe}' (first seen at {index}) must follow '{previousProbe}' (at {previous}).\n--- displayed ---\n{displayed}\n---");
            previous = index;
            previousProbe = probe;
        }
    }

    private static IEnumerable<LinkInline> AllLinks(MarkdownDocument document) =>
        ((MarkdownObject)document).Descendants<LinkInline>();

    private static void AppendBlock(List<string> blocks, Block block)
    {
        switch (block)
        {
            case CodeBlock:
            case HtmlBlock:
            case LinkReferenceDefinition:
                return;
            case LeafBlock leaf:
                var text = new StringBuilder();
                AppendInline(text, leaf.Inline?.FirstChild);
                if (text.Length > 0) blocks.Add(text.ToString());
                return;
            case ContainerBlock container:
                foreach (var child in container) AppendBlock(blocks, child);
                return;
        }
    }

    private static void AppendInline(StringBuilder text, Inline? first)
    {
        for (var inline = first; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal: text.Append(literal.Content.ToString()); break;
                case CodeInline code: text.Append(code.Content); break;
                case HtmlEntityInline entity: text.Append(entity.Transcoded.ToString()); break;
                case AutolinkInline autolink: text.Append(autolink.Url); break;
                case LineBreakInline: text.Append('\n'); break;
                // Raw inline HTML (<u>, <mark>, <span style=...>) is markup, not reader-visible text.
                case HtmlInline: break;
                // Emphasis, links and images all carry their visible label as children.
                case ContainerInline container: AppendInline(text, container.FirstChild); break;
            }
        }
    }
}
