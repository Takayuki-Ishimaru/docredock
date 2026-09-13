using DocRedock.Markdown;

namespace DocRedock.Tests.Markdown;

/// <summary>
/// The one character-reference policy every Markdown reader in the product shares
/// (CommonMark "Entity and numeric character references").
/// </summary>
public sealed class MarkdownCharacterReferencesTests
{
    [Theory]
    [InlineData("&amp;", "&")]
    [InlineData("&lt;", "<")]
    [InlineData("&gt;", ">")]
    [InlineData("&quot;", "\"")]
    [InlineData("&copy;", "\u00A9")]
    [InlineData("&nbsp;", "\u00A0")]
    [InlineData("A &amp; B", "A & B")]
    [InlineData("&lt;tag&gt;", "<tag>")]
    [InlineData("&varsubsetneqq;", "\u2ACB\uFE00")]
    [InlineData("&NotEqualTilde;", "\u2242\u0338")]
    [InlineData("&ngE;", "\u2267\u0338")]
    [InlineData("&AMP;", "&")]
    [InlineData("&Afr;", "\U0001D504")]
    [InlineData("&CounterClockwiseContourIntegral;", "\u2233")]
    public void Decodes_named_references(string value, string expected) =>
        Assert.Equal(expected, MarkdownCharacterReferences.Decode(value));

    [Theory]
    [InlineData("&#65;", "A")]
    [InlineData("&#0065;", "A")]
    [InlineData("&#35;", "#")]
    [InlineData("x&#65;y", "xAy")]
    public void Decodes_decimal_references(string value, string expected) =>
        Assert.Equal(expected, MarkdownCharacterReferences.Decode(value));

    [Theory]
    [InlineData("&#x41;", "A")]
    [InlineData("&#X41;", "A")]
    [InlineData("&#x1F600;", "\U0001F600")]
    public void Decodes_hexadecimal_references(string value, string expected) =>
        Assert.Equal(expected, MarkdownCharacterReferences.Decode(value));

    [Theory]
    [InlineData("&#0;")]
    [InlineData("&#xD800;")]
    [InlineData("&#1234567;")]
    [InlineData("&#x110000;")]
    public void Replaces_out_of_range_code_points(string value) =>
        Assert.Equal("\uFFFD", MarkdownCharacterReferences.Decode(value));

    [Theory]
    [InlineData("&foo;")]
    [InlineData("&Amp;")]
    [InlineData("&notequaltilde;")]
    [InlineData("&MadeUpName;")]
    [InlineData("&;")]
    [InlineData("&#;")]
    [InlineData("&#x;")]
    public void Keeps_an_unknown_reference_literal(string value) =>
        Assert.Equal(value, MarkdownCharacterReferences.Decode(value));

    [Theory]
    [InlineData("&amp")]
    [InlineData("&NotEqualTilde")]
    [InlineData("&CounterClockwiseContourIntegralXXX;")]
    [InlineData("&copy")]
    [InlineData("&#65")]
    [InlineData("&#x41")]
    [InlineData("&#12345678;")]
    [InlineData("&#x1234567;")]
    public void Requires_a_trailing_semicolon_and_a_digit_count_in_range(string value) =>
        Assert.Equal(value, MarkdownCharacterReferences.Decode(value));

    [Fact]
    public void A_backslash_escaped_ampersand_never_starts_a_reference()
    {
        // The caller stands "\&" in with the shared placeholder before decoding, so "\&amp;" is the
        // literal text "&amp;" rather than a decoded ampersand.
        var protectedValue = MarkdownCharacterReferences.EscapedAmpersandPlaceholder + "amp;";

        var decoded = MarkdownCharacterReferences.Decode(protectedValue);

        Assert.Equal(protectedValue, decoded);
        Assert.Equal("&amp;", MarkdownCharacterReferences.RestoreEscapedAmpersands(decoded));
    }

    [Fact]
    public void Decodes_every_reference_in_one_left_to_right_pass()
    {
        Assert.Equal("& < A B \u00A9 &foo; &#65", MarkdownCharacterReferences.Decode(
            "&amp; &lt; &#65; &#x42; &copy; &foo; &#65"));
    }

    [Fact]
    public void An_encoded_reference_decodes_only_once()
    {
        // "&amp;lt;" is how a writer escapes the literal text "&lt;"; decoding must stop there
        // instead of going on to produce '<'.
        Assert.Equal("&lt;", MarkdownCharacterReferences.Decode("&amp;lt;"));
    }
}
