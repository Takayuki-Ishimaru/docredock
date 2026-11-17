using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

/// <summary>
/// Regression tests for the visual comparison of the readable HTML with other converters (2026-11-13):
/// source colors that become unreadable in one theme, numeric column alignment, the long contents
/// list, and stylesheet details.
/// </summary>
public sealed class ReadableHtmlWriterAppearanceTests
{
    private static string Html(string markdown) => ReadableHtmlWriter.Write(markdown, null).Html;

    private static string Style(string html) => Regex.Match(html, "<style>(.*?)</style>", RegexOptions.Singleline).Groups[1].Value;

    [Theory]
    [InlineData("333333", " class=\"fg-dark\"")] // dark gray body text: unreadable on the dark background
    [InlineData("000000", " class=\"fg-dark\"")]
    [InlineData("1F3864", " class=\"fg-dark\"")] // Word's dark blue heading color
    [InlineData("FFFFFF", " class=\"fg-light\"")] // white text from a shaded cell: unreadable on white
    [InlineData("FFFF00", " class=\"fg-light\"")]
    [InlineData("FFFFFF80", " class=\"fg-light\"")] // the alpha byte is ignored
    [InlineData("2F75B5", "")] // readable on both backgrounds: kept as is
    [InlineData("23865F", "")]
    [InlineData("FF0000", "")]
    [InlineData("0563C1", "")]
    public void Source_colors_with_low_contrast_get_a_theme_fallback_class(string hex, string expectedClass)
    {
        var html = Html($"a <span style=\"color:#{hex}\">x</span> b");
        Assert.Contains($"<span style=\"color:#{hex}\"{expectedClass}>x</span>", html);
    }

    [Fact]
    public void Fallback_classes_apply_only_in_the_theme_where_the_color_is_unreadable()
    {
        var css = Style(Html("x"));
        Assert.Contains("@media(prefers-color-scheme:light){.fg-light{color:var(--fg)!important}}", css);
        Assert.Contains("@media(prefers-color-scheme:dark){.fg-dark{color:var(--fg)!important}}", css);
        // Paper is white whatever the screen theme is.
        var print = css[css.IndexOf("@media print", StringComparison.Ordinal)..];
        Assert.Contains(".fg-light{color:var(--fg)!important}", print);
    }

    [Fact]
    public void Numeric_columns_are_right_aligned_including_the_header()
    {
        var html = Html(
            "| 商品 | 売上 | 率 | 差 | 備考 |\n| --- | --- | --- | --- | --- |\n" +
            "| A | 1,234 | 12.5% | (300) | 1 |\n" +
            "| B | 56 | 3% | -2.5 | なし |\n" +
            "| C | — | 0.5% | ¥1,000 | 2 |\n");
        Assert.Contains("<th scope=\"col\">商品</th><th scope=\"col\" class=\"align-right\">売上</th><th scope=\"col\" class=\"align-right\">率</th><th scope=\"col\" class=\"align-right\">差</th><th scope=\"col\">備考</th>", html);
        Assert.Contains("<tr><td>A</td><td class=\"align-right\">1,234</td><td class=\"align-right\">12.5%</td><td class=\"align-right\">(300)</td><td>1</td></tr>", html);
        Assert.Contains("<td class=\"align-right\">—</td>", html);
    }

    [Fact]
    public void Numeric_detection_keeps_explicit_alignment_and_ignores_ids_dates_and_markup()
    {
        var html = Html(
            "| L | ID | 日付 | 太字 | 空 |\n| :--- | --- | --- | --- | --- |\n" +
            "| 1 | TR-01 | 2026-08-23 | **5** |  |\n" +
            "| 2 | TR-02 | 2026-08-24 | 6 |  |\n");
        Assert.Contains("<th scope=\"col\" class=\"align-left\">L</th><th scope=\"col\">ID</th><th scope=\"col\">日付</th><th scope=\"col\">太字</th><th scope=\"col\">空</th>", html);
        Assert.Contains("<tr><td class=\"align-left\">1</td><td>TR-01</td><td>2026-08-23</td><td><strong>5</strong></td><td></td></tr>", html);
    }

    [Fact]
    public void A_column_of_only_placeholders_is_not_numeric()
    {
        var html = Html("| a | b |\n| --- | --- |\n| x | — |\n| y | - |\n");
        Assert.Contains("<th scope=\"col\">b</th>", html);
        Assert.Contains("<td>—</td>", html);
    }

    [Theory]
    [InlineData(12, false)]
    [InlineData(13, true)]
    public void A_long_contents_list_is_collapsed(int headings, bool collapsed)
    {
        var markdown = new StringBuilder("# 文書\n\n");
        for (var k = 1; k <= headings; k++) markdown.Append("## 節").Append(k).Append("\n\n本文\n\n");
        var html = Html(markdown.ToString());
        var nav = Regex.Match(html, "<nav class=\"toc\" aria-label=\"目次\">.*?</nav>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(nav);
        Assert.Equal(headings, Regex.Matches(nav, "<li>").Count);
        if (collapsed)
        {
            Assert.Contains($"<details>\n<summary class=\"toc-title\">目次（{headings}項目）</summary>\n<ul>", nav);
            Assert.EndsWith("</ul>\n</details>\n</nav>", nav);
            Assert.DoesNotContain("<p class=\"toc-title\">", nav);
        }
        else
        {
            Assert.Contains("<p class=\"toc-title\">目次</p>", nav);
            Assert.DoesNotContain("<details>", nav);
        }
    }

    [Fact]
    public void Collapsed_contents_list_is_labelled_in_english_for_english_documents()
    {
        var markdown = new StringBuilder("# Doc\n\n");
        for (var k = 1; k <= 13; k++) markdown.Append("## Section ").Append(k).Append("\n\ntext\n\n");
        Assert.Contains("<summary class=\"toc-title\">Contents (13 items)</summary>", Html(markdown.ToString()));
    }

    [Fact]
    public void Stylesheet_uses_document_fonts_tight_code_lines_and_a_minimum_cell_width()
    {
        var css = Style(Html("x"));
        Assert.DoesNotContain("system-ui", css);
        Assert.DoesNotContain("Yu Gothic UI", css);
        Assert.Contains("Meiryo", css);
        Assert.Matches(@"pre\{[^}]*line-height:1\.5", css);
        Assert.Matches(@"th,td\{[^}]*min-width:4\.5em", css);
    }
}
