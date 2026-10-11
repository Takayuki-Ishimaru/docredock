using DocRedock.Render;

namespace DocRedock.Tests.Render;

public sealed class MarkdownInlineNestingTests
{
    [Theory]
    [InlineData("~~本日の**会議**は中止~~", "<del>本日の<strong>会議</strong>は中止</del>", "本日の会議は中止")]
    [InlineData("~~*italic* `code` <u>under</u>~~", "<del><em>italic</em> <code>code</code> <u>under</u></del>", "italic code under")]
    [InlineData(@"~~\*\*literal\*\* &lt;u&gt;x&lt;/u&gt;~~", "<del>**literal** &lt;u&gt;x&lt;/u&gt;</del>", "**literal** <u>x</u>")]
    [InlineData(@"~~`**literal** &amp; \*`~~", @"<del><code>**literal** &amp;amp; \*</code></del>", @"**literal** &amp; \*")]
    [InlineData("**~~*nested*~~**", "<strong><del><em>nested</em></del></strong>", "nested")]
    [InlineData("~~`a~~b`~~", "<del><code>a~~b</code></del>", "a~~b")]
    [InlineData("~~**bold and *nested italic***~~", "<del><strong>bold and <em>nested italic</em></strong></del>", "bold and nested italic")]
    [InlineData("~~*italic **bold** more*~~", "<del><em>italic <strong>bold</strong> more</em></del>", "italic bold more")]
    [InlineData("~~*`a*b`*~~", "<del><em><code>a*b</code></em></del>", "a*b")]
    [InlineData("~~_`a_b`_~~", "<del><em><code>a_b</code></em></del>", "a_b")]
    public void Nested_styles_are_read_once_without_reinterpreting_escaped_text(string markdown, string html, string plain)
    {
        Assert.Equal(html, MarkdownInlineParser.Html(markdown));
        Assert.Equal(plain, MarkdownInlineParser.PlainText(markdown));
        var runs = MarkdownInlineParser.Runs(markdown);
        Assert.Equal(plain, string.Concat(runs.Select(run => run.Text)));
        Assert.All(runs, run => Assert.True(run.Strike));
    }

    [Fact]
    public void Runs_inherit_strike_and_keep_nested_bold_italic_code_and_underline()
    {
        var runs = MarkdownInlineParser.Runs("~~**bold** *italic* `code` <u>under <u>inner</u> after</u> plain~~");
        Assert.True(Assert.Single(runs, run => run.Text == "bold").Bold);
        Assert.True(Assert.Single(runs, run => run.Text == "italic").Italic);
        Assert.True(Assert.Single(runs, run => run.Text == "code").Code);
        Assert.All(runs.Where(run => run.Text is "under " or "inner" or " after"), run => Assert.True(run.Underline));
        Assert.False(Assert.Single(runs, run => run.Text == " plain").Underline);
        Assert.All(runs, run => Assert.True(run.Strike));
    }

    [Fact]
    public void Italic_children_keep_bold_and_literal_code_delimiters()
    {
        var runs = MarkdownInlineParser.Runs("~~*italic **bold** `a*b` more* _`a_b`_~~");
        Assert.True(Assert.Single(runs, run => run.Text == "bold").Bold);
        Assert.All(runs.Where(run => run.Text is "a*b" or "a_b"), run => Assert.True(run.Code));
        Assert.All(runs.Where(run => run.Text != " "), run => Assert.True(run.Italic));
        Assert.All(runs, run => Assert.True(run.Strike));
    }

    [Theory]
    [InlineData("~~")]
    [InlineData("*")]
    [InlineData("_")]
    public async Task An_unclosed_style_with_many_code_spans_does_not_backtrack_through_every_token_choice(string delimiter)
    {
        var markdown = delimiter + string.Concat(Enumerable.Repeat("`code` ", 2000));
        var plain = await Task.Run(() => MarkdownInlineParser.PlainText(markdown)).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(delimiter + string.Concat(Enumerable.Repeat("code ", 2000)), plain);
    }
}
