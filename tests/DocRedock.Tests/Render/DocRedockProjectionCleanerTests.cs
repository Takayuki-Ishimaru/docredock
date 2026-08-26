using DocRedock.Render;

namespace DocRedock.Tests.Render;

public sealed class DocRedockProjectionCleanerTests
{
    [Fact]
    public void Keeps_docredock_looking_comment_inside_a_shorter_fence()
    {
        const string markdown = """
            ````markdown
            ```
            <!--drmd:block id=example kind=paragraph-->
            ````
            """;

        Assert.False(DocRedockProjectionCleaner.IsDocRedockProjection(markdown));
        Assert.Equal(markdown, DocRedockProjectionCleaner.Clean(markdown));
    }

    [Fact]
    public void Does_not_treat_a_backtick_in_the_info_string_as_a_fence()
    {
        const string markdown = """
            ```markdown`invalid
            <!--drmd:block id=example kind=paragraph-->
            """;

        Assert.True(DocRedockProjectionCleaner.IsDocRedockProjection(markdown));
    }
}
