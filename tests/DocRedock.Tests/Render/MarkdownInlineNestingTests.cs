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
    public async Task An_unclosed_strike_with_many_code_spans_does_not_backtrack_through_every_token_choice()
    {
        var markdown = "~~" + string.Concat(Enumerable.Repeat("`code` ", 2000));
        var plain = await Task.Run(() => MarkdownInlineParser.PlainText(markdown)).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("~~" + string.Concat(Enumerable.Repeat("code ", 2000)), plain);
    }
}
