using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

public sealed class ReadableHtmlWriterTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static string Html(string markdown, ReadableHtmlOptions? options = null) => ReadableHtmlWriter.Write(markdown, options).Html;

    private static ReadableHtmlOptions WithAssets(params (string Key, string Type, byte[] Bytes)[] assets) =>
        new(Assets: assets.ToDictionary(a => a.Key, a => new ReadableHtmlAsset(a.Type, a.Bytes)));

    private static void AssertNoLiveMarkup(string html)
    {
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex(@"<[a-z][^>]*\son[a-z]+\s*=", RegexOptions.IgnoreCase), html);
    }

    [Fact]
    public void Document_shell_has_csp_lang_title_and_generator()
    {
        var html = Html("# 設計書\n\n本文です。", new ReadableHtmlOptions(Generator: "DocRedock 1.0"));
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<html lang=\"ja\">", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Contains("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\">", html);
        Assert.Contains("<meta name=\"viewport\"", html);
        Assert.Contains("<meta name=\"generator\" content=\"DocRedock 1.0\">", html);
        Assert.Contains("<title>設計書</title>", html);
        Assert.True(html.IndexOf("charset", StringComparison.Ordinal) < html.IndexOf("Content-Security-Policy", StringComparison.Ordinal));
        AssertNoLiveMarkup(html);
    }

    [Fact]
    public void Language_is_detected_or_overridden_and_title_falls_back()
    {
        Assert.Contains("<html lang=\"en\">", Html("# Hello\n\nworld"));
        Assert.Contains("<html lang=\"ja\">", Html("カタカナ"));
        Assert.Contains("<html lang=\"fr\">", Html("# Hello", new ReadableHtmlOptions(Language: "fr")));
        Assert.Contains("<title>Given</title>", Html("# Hello", new ReadableHtmlOptions(Title: "Given")));
        Assert.Contains("<title>DocRedock</title>", Html("just text"));
    }

    [Fact]
    public void Headings_get_unique_ids_and_a_toc_for_levels_two_and_three()
    {
        var html = Html("# Title\n\n## 概要\n\n### 詳細\n\n## 概要\n\n##\n\n#### deep");
        Assert.Contains("<h2 id=\"概要\">概要</h2>", html);
        Assert.Contains("<h2 id=\"概要-2\">", html);
        Assert.Contains("<h3 id=\"詳細\">", html);
        Assert.Contains("<h2 id=\"section-5\"></h2>", html);
        Assert.Contains("<nav class=\"toc\" aria-label=\"目次\">", html);
        Assert.Contains("href=\"#概要-2\"", html);
        var toc = Regex.Match(html, "<nav.*?</nav>", RegexOptions.Singleline).Value;
        Assert.Matches(@"概要</a>\s*<ul>\s*<li><a href=""#詳細"">", toc);
        Assert.True(html.IndexOf("<nav", StringComparison.Ordinal) > html.IndexOf("</h1>", StringComparison.Ordinal));
        Assert.DoesNotContain("<nav", Html("# T\n\n## a\n\n## b"));
    }

    [Fact]
    public void Tables_use_th_scope_alignment_br_escaped_pipes_and_ragged_rows()
    {
        var html = Html("| 名前 | 値 | 備考 |\n| :--- | :---: | ---: |\n| a\\|b | 1<br>2 | **x** |\n| only |\n| 1 | 2 | 3 | 4 |\n\nafter");
        Assert.Contains("<div class=\"table-scroll\"><table>", html);
        Assert.Contains("<th scope=\"col\" class=\"align-left\">名前</th>", html);
        Assert.Contains("<th scope=\"col\" class=\"align-center\">値</th>", html);
        Assert.Contains("<th scope=\"col\" class=\"align-right\">備考</th>", html);
        Assert.Contains("<td class=\"align-left\">a|b</td>", html);
        Assert.Contains("<td class=\"align-center\">1<br>2</td>", html);
        Assert.Contains("<td class=\"align-right\"><strong>x</strong></td>", html);
        Assert.Contains("<td class=\"align-left\">only</td><td class=\"align-center\"></td><td class=\"align-right\"></td>", html);
        Assert.DoesNotContain(">4<", html);
        Assert.Contains("<p>after</p>", html);
    }

    [Fact]
    public void Lists_nest_and_ordered_lists_keep_their_start()
    {
        var html = Html("- a\n  - b\n    - c\n- d\n\n3. x\n4. y\n   continued\n\nbreak\n\n1. one\n    - four-space child");
        Assert.Matches(@"<ul>\s*<li>a\s*<ul>\s*<li>b\s*<ul>\s*<li>c\s*</li>", html);
        Assert.Contains("<li>d", html);
        Assert.Contains("<ol start=\"3\">", html);
        Assert.Matches(@"<li>y\s+continued", html);
        Assert.Contains("<ol>", html);
        Assert.Matches(@"<li>one\s*<ul>\s*<li>four-space child", html);
    }

    [Fact]
    public void Blockquote_holds_paragraphs_lists_and_hard_breaks()
    {
        var html = Html("> 一行目  \n> 二行目\n>\n> - item1\n> - item2");
        Assert.Matches(@"<blockquote>\s*<p>一行目<br>\s*二行目</p>\s*<ul>\s*<li>item1", html);
        Assert.Contains("item2", html);
    }

    [Fact]
    public void Code_fences_are_escaped_and_honour_run_length()
    {
        var html = Html("````cs\nvar a = \"<b>\";\n```\nstill code\n````\n\nnext");
        Assert.Contains("<pre><code class=\"language-cs\">var a = &quot;&lt;b&gt;&quot;;\n```\nstill code</code></pre>", html);
        Assert.Contains("<p>next</p>", html);
    }

    [Fact]
    public void Mermaid_becomes_inline_svg_with_source_details()
    {
        var html = Html("```mermaid\nflowchart TD\n    N_A1[\"開始\"]\n    N_B2[\"完了\"]\n    N_A1 --> N_B2\n```");
        Assert.Contains("<figure class=\"diagram\"><svg", html);
        Assert.Contains("<details><summary>Mermaid</summary><pre><code class=\"language-mermaid\">flowchart TD", html);
        var svg = Regex.Match(html, "<svg.*?</svg>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(svg);
        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"\son[a-z]+\s*=", svg);
        var unsupported = Html("```mermaid\npie title x\n```");
        Assert.DoesNotContain("<figure", unsupported);
        Assert.Contains("<pre><code class=\"language-mermaid\">pie title x</code></pre>", unsupported);
    }

    [Fact]
    public void Ocr_details_block_is_recursive_and_unbalanced_details_close()
    {
        var html = Html("<details class=\"ocr-extraction\">\n<summary>OCR **抽出**</summary>\n\n> 領収書  \n> 合計\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n\n</details>\n\nafter");
        Assert.Contains("<details class=\"ocr-extraction\">", html);
        Assert.Contains("<summary>OCR <strong>抽出</strong></summary>", html);
        Assert.Contains("<blockquote>", html);
        Assert.Contains("<td class=\"align-right\">2</td>", html);
        Assert.Matches(@"</details>\s*<p>after</p>", html);
        var open = Html("<details class=\"speaker-notes\">\n<summary>Notes</summary>\n\nbody");
        Assert.Matches(@"<p>body</p>\s*</details>", open);
    }

    [Fact]
    public void Raw_img_and_markdown_images_embed_with_ids_and_sizes()
    {
        var options = WithAssets(("r.assets/img-0001.png", "image/png", Png), ("r.assets/日本 語.png", "image/png", Png));
        var raw = Html("<img src=\"r.assets/img-0001.png\" alt=\"図1\" width=\"120\" height=\"80\">", options);
        Assert.Matches(@"<img id=""asset-img-0001\.png"" src=""data:image/png;base64,[A-Za-z0-9+/=]+"" alt=""図1"" width=""120"" height=""80"">", raw);
        var md = ReadableHtmlWriter.Write("![alt](r.assets/img-0001.png)\n\n![again](r.assets/img-0001.png)\n\n![enc](r.assets/%E6%97%A5%E6%9C%AC%20%E8%AA%9E.png)", options);
        Assert.Equal(3, md.EmbeddedImageCount);
        Assert.Equal(Png.Length * 3, md.EmbeddedImageBytes);
        Assert.Single(Regex.Matches(md.Html, "id=\"asset-img-0001\\.png\""));
        Assert.Contains("alt=\"enc\"", md.Html);
        Assert.Empty(md.Diagnostics);
    }

    [Fact]
    public void Asset_links_with_fragments_point_to_the_embedded_image()
    {
        var options = WithAssets(("d.assets/img-0002.png", "image/png", Png));
        var html = Html("![x](d.assets/img-0002.png)\n\n| 位置 |\n| --- |\n| [image-pixels: x=493, y=74, w=212, h=68](d.assets/img-0002.png#xywh=pixel:493,74,212,68) |", options);
        Assert.Contains("<a href=\"#asset-img-0002.png\" title=\"x=493, y=74, w=212, h=68\">", html);
        Assert.Contains("id=\"asset-img-0002.png\"", html);
        Assert.DoesNotContain("d.assets/img-0002.png#", html);
        // A link to an image that is not shown elsewhere still gets a target.
        var onlyLink = Html("[see](d.assets/img-0002.png)", options);
        Assert.Contains("href=\"#asset-img-0002.png\"", onlyLink);
        Assert.Contains("id=\"asset-img-0002.png\"", onlyLink);
        // Relative links that are not assets render as plain text.
        var relative = Html("[doc](other/file.docx) and [web](https://example.com/a)");
        Assert.DoesNotContain("other/file.docx", relative);
        Assert.Contains("<p>doc and <a href=\"https://example.com/a\">web</a></p>", relative);
    }

    [Theory]
    [InlineData("image/tiff", new byte[] { 0x49, 0x49, 0x2A, 0 }, "HtmlImageNotEmbeddable")]
    [InlineData("image/png", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, "HtmlImageNotEmbeddable")]
    [InlineData("image/svg+xml", new byte[] { (byte)'<', (byte)'p', (byte)'/', (byte)'>' }, "HtmlImageNotEmbeddable")]
    public void Unembeddable_assets_become_placeholders(string type, byte[] bytes, string code)
    {
        var result = ReadableHtmlWriter.Write("![図の説明](a/x.bin)", WithAssets(("a/x.bin", type, bytes)));
        Assert.Contains("[図: 図の説明]", result.Html);
        Assert.DoesNotContain("data:image", result.Html);
        Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0, result.EmbeddedImageCount);
    }

    [Fact]
    public void Svg_with_prolog_embeds_and_other_signatures_verify()
    {
        var svg = Encoding.UTF8.GetBytes("﻿ <?xml version=\"1.0\"?>\n<!-- c --><svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        byte[] gif = [.. "GIF89a"u8, 0, 0];
        byte[] webp = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8];
        byte[] bmp = [(byte)'B', (byte)'M', .. new byte[30]];
        byte[] jpg = [0xFF, 0xD8, 0xFF, 0xE0];
        var options = WithAssets(("s.svg", "image/svg+xml", svg), ("g.gif", "image/gif", gif), ("w.webp", "image/webp", webp),
            ("b.bmp", "image/bmp", bmp), ("j.jpg", "image/jpeg", jpg));
        var result = ReadableHtmlWriter.Write("![](s.svg) ![](g.gif) ![](w.webp) ![](b.bmp) ![](j.jpg)", options);
        Assert.Equal(5, result.EmbeddedImageCount);
        Assert.Empty(result.Diagnostics);
        Assert.Contains("data:image/svg+xml;base64,", result.Html);
    }

    [Fact]
    public void Missing_images_and_size_limits_report_diagnostics()
    {
        var options = new ReadableHtmlOptions(Assets: new Dictionary<string, ReadableHtmlAsset>
        {
            ["big.png"] = new("image/png", Png.Concat(new byte[100]).ToArray()),
            ["a.png"] = new("image/png", Png),
            ["b.png"] = new("image/png", Png),
        }, MaxImageBytes: 50, MaxTotalImageBytes: Png.Length + 5);
        var result = ReadableHtmlWriter.Write("![m](nope.png)\n\n![b](big.png)\n\n![a](a.png)\n\n![c](b.png)", options);
        Assert.Equal(["HtmlImageMissing", "HtmlImageEmbedSkipped", "HtmlImageEmbedSkipped"], result.Diagnostics.Select(d => d.Code).ToArray());
        Assert.Equal(1, result.EmbeddedImageCount);
        Assert.Equal(Png.Length, result.EmbeddedImageBytes);
        Assert.Contains("[Image: m]", result.Html);
    }

    [Fact]
    public void Existing_data_uri_images_pass_through_and_external_images_do_not()
    {
        var uri = "data:image/png;base64," + Convert.ToBase64String(Png);
        var html = Html("![p](" + uri + ")");
        Assert.Contains("src=\"" + uri + "\"", html);
        var bad = ReadableHtmlWriter.Write("![x](data:image/svg+xml;base64,AAAA)\n\n![y](http://evil/x.png)", null);
        Assert.DoesNotContain("<img", bad.Html);
        Assert.All(bad.Diagnostics, d => Assert.Equal("HtmlImageNotEmbeddable", d.Code));
        Assert.Equal(2, bad.Diagnostics.Count);
    }

    [Fact]
    public void Untrusted_input_never_becomes_live_markup()
    {
        const string hostile = "<script>alert(1)</script>\n\n<img src=x onerror=alert(1)>\n\n[x](javascript:alert(1))\n\n![x](http://evil/x.png)\n\n" +
            "<svg onload=alert(1)>\n<circle/>\n</svg>\n\n<details class=\"x\" onclick=alert(1)>\n\n<!-- a --><script>alert(2)</script> -->\n\n" +
            "<!-- x --><img src=\"y\" onerror=\"z\"> -->\n\n<img src=\"http://evil/p.png\" onerror=\"alert(1)\">\n\n<a href=\"javascript:x\">k</a>";
        var html = Html(hostile);
        AssertNoLiveMarkup(html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<svg", html);
        Assert.DoesNotContain("<details", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("[Image: x]", html);
        // Every comment stays one well-formed comment.
        foreach (Match comment in Regex.Matches(html, "<!--.*?-->", RegexOptions.Singleline))
            Assert.DoesNotContain("--", comment.Value[4..^3]);
    }

    [Fact]
    public void Comments_are_kept_but_cannot_end_early()
    {
        var html = Html("<!-- 回転45° -->\n\n<!-- multi\nline -- x --->");
        Assert.Contains("<!-- 回転45° -->", html);
        Assert.Matches(@"<!-- multi\nline - - x - -->", html);
    }

    [Fact]
    public void Inline_formatting_uses_the_shared_parser_and_balances_tags()
    {
        var html = Html("**太字** と `code` と ~~消去~~ と <u>下線<mark>強調</u> と <span style=\"color:#FF0000\">赤</span> と </mark> \\_literal\\_ &amp; <i>x</i>");
        Assert.Contains("<strong>太字</strong>", html);
        Assert.Contains("<code>code</code>", html);
        Assert.Contains("<del>消去</del>", html);
        Assert.Contains("<u>下線<mark>強調</mark></u>", html);
        Assert.Contains("<span style=\"color:#FF0000\">赤</span>", html);
        Assert.Contains("_literal_ &amp; &lt;i&gt;x&lt;/i&gt;", html);
        AssertNoLiveMarkup(html);
    }

    [Fact]
    public void Paragraph_line_joins_hard_breaks_and_thematic_breaks()
    {
        var html = Html("one  \ntwo\nthree\n\n---\n\n日本語の\n途中改行");
        Assert.Matches(@"<p>one<br>\s*two\nthree</p>", html);
        Assert.Contains("<hr>", html);
        Assert.Contains("<p>日本語の途中改行</p>", html);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n\n ")]
    public void Empty_input_gives_a_valid_document(string markdown)
    {
        var html = Html(markdown);
        Assert.Contains("<main>", html);
        Assert.Contains("class=\"empty\"", html);
        Assert.EndsWith("</html>\n", html);
    }

    [Fact]
    public void Output_is_deterministic_and_style_is_compact()
    {
        const string md = "# T\n\n## a\n\n## b\n\n### c\n\n| a | b |\n| - | - |\n| 1 | 2 |\n\n- x\n  - y\n";
        Assert.Equal(Html(md), Html(md));
        var css = Regex.Match(Html(md), "<style>(.*?)</style>", RegexOptions.Singleline).Groups[1].Value;
        Assert.True(css.Length < 4500, $"style is {css.Length} chars");
        Assert.Contains("prefers-color-scheme:dark", css);
        Assert.Contains("@media print", css);
        Assert.DoesNotContain("url(", css);
        Assert.DoesNotContain("@import", css);
    }

    [Fact]
    public void Twenty_thousand_row_table_converts_quickly()
    {
        var sb = new StringBuilder("| id | name | note |\n| --- | --- | --- |\n");
        for (var i = 0; i < 20000; i++) sb.Append("| ").Append(i).Append(" | 名前").Append(i).Append(" | plain *text* ").Append(i).Append(" |\n");
        var watch = Stopwatch.StartNew();
        var html = Html(sb.ToString());
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.Equal(20001, Regex.Matches(html, "<tr>").Count);
    }
}
