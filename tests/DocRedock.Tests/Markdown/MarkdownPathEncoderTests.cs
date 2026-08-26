using DocRedock.Markdown;

namespace DocRedock.Tests.Markdown;

public sealed class MarkdownPathEncoderTests
{
    [Theory]
    [InlineData("document.drmd/assets/diagram#1?.png", "document.drmd/assets/diagram%231%3F.png")]
    [InlineData("日本語 [最終]\".png", "日本語%20%5B最終%5D%22.png")]
    [InlineData("folder/image (100%).png", "folder/image%20%28100%25%29.png")]
    public void Encodes_unsafe_ascii_in_each_path_segment(string input, string expected)
    {
        Assert.Equal(expected, MarkdownPathEncoder.Encode(input));
    }
}
