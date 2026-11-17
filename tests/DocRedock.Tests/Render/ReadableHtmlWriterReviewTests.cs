using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

/// <summary>Regression tests for the independent review of <see cref="ReadableHtmlWriter"/>.</summary>
public sealed class ReadableHtmlWriterReviewTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static string Html(string markdown, ReadableHtmlOptions? options = null) => ReadableHtmlWriter.Write(markdown, options).Html;

    private const string Seq = "```mermaid\nsequenceDiagram\n    participant P1 as A\n    participant P2 as B\n    P1->>P2: plain\n```";

    // ---- H1

    [Fact]
    public void Styled_link_text_is_rendered_as_markup_inside_the_anchor()
    {
        var html = Html("[<span style=\"color:#0563C1\"><u>Example</u></span>](https://example.com) and [**bold**](https://a/)");
        Assert.Contains("<a href=\"https://example.com\"><span style=\"color:#0563C1\"><u>Example</u></span></a>", html);
        Assert.Contains("<a href=\"https://a/\"><strong>bold</strong></a>", html);
        Assert.DoesNotContain("&lt;span", html);
    }

    [Fact]
    public void Angle_bracket_destinations_are_links()
    {
        var html = Html("[sp](<https://a/b c>) [par](<https://a/(b)>)");
        Assert.Contains("<a href=\"https://a/b c\">sp</a>", html);
        Assert.Contains("<a href=\"https://a/(b)\">par</a>", html);
        Assert.DoesNotContain("&lt;https", html);
    }

    [Fact]
    public void Link_inside_link_text_never_nests_anchors()
    {
        var html = Html("[outer **[inner](https://b/)** text](https://a/)");
        Assert.Single(Regex.Matches(html, "<a "));
    }

    [Fact]
    public void Link_node_exposes_children_and_plain_text()
    {
        var link = Assert.IsType<MarkdownInlineLink>(Assert.Single(MarkdownInlineParser.Parse("[**b** x](<https://a/b c>)")));
        Assert.Equal("https://a/b c", link.Destination);
        Assert.Equal("b x", link.Text);
        Assert.NotNull(link.Children);
        Assert.IsType<MarkdownInlineStrong>(link.Children![0]);
    }

    // ---- M1 / M2

    [Fact]
    public void Forty_thousand_identical_headings_get_unique_ids_quickly()
    {
        var md = string.Concat(Enumerable.Repeat("## Same\n\n", 40_000));
        var sw = Stopwatch.StartNew();
        var html = Html(md);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms");
        var ids = Regex.Matches(html, "<h2 id=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(40_000, ids.Count);
        Assert.Equal(40_000, ids.Distinct().Count());
        Assert.Equal("same", ids[0]);
        Assert.Equal("same-2", ids[1]);
    }

    [Fact]
    public void Unterminated_comment_over_forty_thousand_lines_is_linear()
    {
        var md = "<!--\n" + string.Concat(Enumerable.Repeat("some line of text here that is fairly ordinary\n", 40_000));
        var sw = Stopwatch.StartNew();
        var html = Html(md);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Contains("<!--", html);
    }

    // ---- M3

    private static string? PreviewSvg(string mermaid)
    {
        var type = typeof(DocRedock.Markdown.ReadableMarkdownSerializer).Assembly.GetType("DocRedock.Markdown.MermaidSvgPreviewRenderer")!;
        return (string?)type.GetMethod("Render", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [mermaid]);
    }

    private static string Sha(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [Fact]
    public void Mermaid_preview_output_is_unchanged_by_the_linear_label_wrapping()
    {
        // Baseline captured from the previous (quadratic) WrapLabel implementation, with LF line endings.
        var cases = new (string Source, string Hash)[]
        {
            ("flowchart LR\n    N_A1[\"Short\"]\n    N_B2[\"A rather long label that needs wrapping across several lines indeed\"]\n    N_A1 -->|edge label that is also long enough to wrap| N_B2",
                "AA14060637C32761C40810F907F17EA08781DD7D5348EE2C2B520D691BD5D0C6"),
            ("flowchart LR\n    N_A1[\"日本語のとても長いラベルがここに入ります。折り返しが必要になるはずです。\"]\n    N_B2[\"line1<br/>line two is longer than twenty-three chars<br>x\"]\n    N_A1 --> N_B2",
                "4B9982E2E8ABDE1428C94E48398B8A5B680DF9C44D261F3724123C37BBE65D7C"),
            ("sequenceDiagram\n    participant P1 as Alice the very long participant name\n    participant P2 as B\n    P1->>P2: a message with a quite long label to wrap over lines\n    Note over P1,P2: note text that is long long long long long long long long long long long long long long long long long long long long long long long long long long long long long long",
                "F5305C41BEFDA30EAA8C458BE7E7368BB9A4C1FAF4F798E42194DB00427F67DD"),
            ("stateDiagram-v2\n    state \"A state with a long description here\" as S_1\n    [*] --> S_1\n    S_1 --> [*]",
                "3ED3FA7DAAC36C0F9543E6F72B6C39CCDB3FC6664B583393DCC2E336AD9B1785"),
            ("flowchart LR\n    N_A1[\"" + new string('x', 23) + "\"]\n    N_B2[\"" + new string('y', 38) + "\"]\n    N_C3[\"" + new string('z', 39) + "\"]\n    N_A1 --> N_B2\n    N_B2 --> N_C3",
                "95D8CB9A9B7FE862B01D9747F23443F94A7ABC75B74248A3E5C6DC277100130C"),
        };
        foreach (var (source, hash) in cases)
            Assert.Equal(hash, Sha(PreviewSvg(source)?.ReplaceLineEndings("\n") ?? "null"));
    }

    [Fact]
    public void Wrapping_a_very_long_label_is_linear()
    {
        var sw = Stopwatch.StartNew();
        var svg = PreviewSvg("flowchart LR\n    N_A1[\"" + new string('a', 200_000) + "\"]\n    N_B2[\"b\"]\n    N_A1 --> N_B2");
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.NotNull(svg);
    }

    [Fact]
    public void Oversized_mermaid_sources_and_labels_stay_code_blocks()
    {
        var longLabel = Html("```mermaid\nflowchart LR\n    N_A1[\"" + new string('a', 2_500) + "\"]\n    N_B2[\"b\"]\n    N_A1 --> N_B2\n```");
        Assert.DoesNotContain("<svg", longLabel);
        Assert.Contains("<pre><code class=\"language-mermaid\">", longLabel);
        var bigSource = Html("```mermaid\nflowchart LR\n" + string.Concat(Enumerable.Repeat("    N_A1 --> N_B2\n", 8_000)) + "```");
        Assert.DoesNotContain("<svg", bigSource);
        Assert.Contains("<pre><code", bigSource);
    }

    // ---- L1

    [Theory]
    [InlineData("<!-->")]
    [InlineData("<!--->")]
    public void Empty_comment_forms_do_not_swallow_the_document(string comment)
    {
        var html = Html(comment + "\n\nvisible paragraph\n\n## Heading\n\nmore");
        Assert.Contains("<p>visible paragraph</p>", html);
        Assert.Contains("<h2 id=\"heading\">Heading</h2>", html);
    }

    [Fact]
    public void A_terminated_multiline_comment_leaves_later_headings_intact()
    {
        var html = Html("<!-- note\n\nstill comment\n-->\n\n## Later\n\ntext");
        Assert.Contains("<h2 id=\"later\">Later</h2>", html);
        Assert.Contains("<p>text</p>", html);
    }

    // ---- L2

    [Fact]
    public void Inline_data_images_are_checked_counted_and_limited()
    {
        var good = "data:image/png;base64," + Convert.ToBase64String(Png);
        var r = ReadableHtmlWriter.Write($"![a]({good})", new ReadableHtmlOptions());
        Assert.Contains("<img src=\"" + good + "\"", r.Html);
        Assert.Equal(1, r.EmbeddedImageCount);
        Assert.Equal(Png.Length, r.EmbeddedImageBytes);

        var wrongSignature = ReadableHtmlWriter.Write("![b](data:image/png;base64,PHNjcmlwdD4=)");
        Assert.DoesNotContain("<img", wrongSignature.Html);
        Assert.Contains("image-placeholder", wrongSignature.Html);
        Assert.Contains(wrongSignature.Diagnostics, d => d.Code == ReadableHtmlWriter.ImageNotEmbeddable);
        Assert.Equal(0, wrongSignature.EmbeddedImageCount);

        var malformed = ReadableHtmlWriter.Write("![c](data:image/png;base64,iVBORw0KGgoAA)");
        Assert.DoesNotContain("<img", malformed.Html);
        Assert.Contains(malformed.Diagnostics, d => d.Code == ReadableHtmlWriter.ImageNotEmbeddable);

        var perImage = ReadableHtmlWriter.Write($"![d]({good})", new ReadableHtmlOptions(MaxImageBytes: 4));
        Assert.DoesNotContain("<img", perImage.Html);
        Assert.Contains(perImage.Diagnostics, d => d.Code == ReadableHtmlWriter.ImageEmbedSkipped);

        var total = ReadableHtmlWriter.Write($"![e]({good})\n\n![f]({good})", new ReadableHtmlOptions(MaxTotalImageBytes: Png.Length + 2));
        Assert.Equal(1, total.EmbeddedImageCount);
        Assert.Single(Regex.Matches(total.Html, "<img "));
        Assert.Contains(total.Diagnostics, d => d.Code == ReadableHtmlWriter.ImageEmbedSkipped);
    }

    // ---- L3

    [Theory]
    [InlineData("set once=1")]
    [InlineData("GET url(/api)")]
    public void Label_text_that_looks_like_markup_does_not_drop_the_drawing(string label)
    {
        var html = Html($"```mermaid\nsequenceDiagram\n    participant P1 as A\n    participant P2 as B\n    P1->>P2: {label}\n```");
        Assert.Contains("<figure class=\"diagram\"><svg", html);
    }

    [Fact]
    public void Hostile_label_markup_never_reaches_the_page()
    {
        var html = Html("```mermaid\nflowchart LR\n    N_A1[\"<script>alert(1)</script>\"]\n    N_B2[\"x\\\" onload=\\\"alert(1)\"]\n    N_A1 --> N_B2\n```");
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotMatch(new Regex(@"<[a-z][^>]*\son[a-z]+\s*=", RegexOptions.IgnoreCase), html);
    }

    // ---- L4

    [Fact]
    public void Identical_diagrams_get_distinct_ids_and_matching_references()
    {
        var html = Html(Seq + "\n\n" + Seq);
        var ids = Regex.Matches(html, @"\sid=""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Contains(ids, id => id.StartsWith("d1-", StringComparison.Ordinal));
        Assert.Contains(ids, id => id.StartsWith("d2-", StringComparison.Ordinal));
        foreach (Match m in Regex.Matches(html, @"url\(#([^)]+)\)"))
            Assert.Contains(m.Groups[1].Value, ids);
        Assert.Equal(2, Regex.Matches(html, "<figure class=\"diagram\">").Count);
    }

    [Fact]
    public void A_heading_cannot_collide_with_a_diagram_id()
    {
        var first = Html(Seq);
        var diagramId = Regex.Match(first, @"<marker id=""([^""]+)""").Groups[1].Value;
        Assert.StartsWith("d1-", diagramId);
        var html = Html(Seq + "\n\n## " + diagramId.Replace('-', ' '));
        var ids = Regex.Matches(html, @"\sid=""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    // ---- L5

    [Fact]
    public void Toc_follows_the_first_h1_even_after_a_leading_comment()
    {
        var html = Html("<!-- section:landscape -->\n\n# Title\n\n## a\n\n## b\n\n## c\n");
        var h1 = html.IndexOf("</h1>", StringComparison.Ordinal);
        var toc = html.IndexOf("<nav class=\"toc\"", StringComparison.Ordinal);
        Assert.True(h1 > 0 && toc > h1, "TOC must come after the H1");
    }

    // ---- CSS

    [Fact]
    public void Css_keeps_words_whole_and_diagrams_at_natural_size_on_screen()
    {
        var html = Html("# t");
        Assert.Contains("overflow-wrap:break-word", html);
        Assert.DoesNotContain("overflow-wrap:anywhere", html);
        Assert.Contains("figure.diagram svg{max-width:none}", html);
        Assert.Contains("figure.diagram svg{max-width:100%}}", html);
    }

    // ---- never throws

    [Fact]
    public void Pathological_lines_do_not_throw()
    {
        var md = "<img " + string.Concat(Enumerable.Repeat("a=\"b\" ", 20_000)) + "\n\n<details class=\"ocr-extraction\">\n<summary>" +
                 string.Concat(Enumerable.Repeat("</summary>", 20_000)) + "x\n</details>\n\nend";
        var result = ReadableHtmlWriter.Write(md);
        Assert.Contains("end", result.Html);
    }
}
