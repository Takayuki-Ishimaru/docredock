using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.PdfFontResourceScopeTests;

namespace DocRedock.Tests.Pdf;

public sealed class PdfJapan1UnicodeTests
{
    private static byte[] Japan1(string text = "00410042003D", string encoding = "Identity-H", string ordering = "Japan1",
        int supplement = 7, string toUnicode = "", bool indirect = false)
    {
        var descendant = $"<< /Subtype /CIDFontType0 /CIDSystemInfo << /Registry (Adobe) /Ordering ({ordering}) /Supplement {supplement} >> /DW 1000 >>";
        return Document([
            Object(1, "<< /Type /Page /MediaBox [0 0 600 800] /Contents 2 0 R /Resources << /Font << /F1 3 0 R >> >> >>"),
            Stream(2, "", $"BT /F1 12 Tf 50 700 Td <{text}> Tj ET"),
            Object(3, $"<< /Type /Font /Subtype /Type0 /BaseFont /Subset /Encoding /{encoding} /DescendantFonts [{(indirect ? "4 0 R" : descendant)}] {toUnicode} >>"),
            Object(4, descendant), Stream(5, "", "1 beginbfchar <0041><65E5> endbfchar")]);
    }

    [Theory]
    [InlineData(false, "Identity-H")]
    [InlineData(true, "Identity-H")]
    [InlineData(false, "Identity-V")]
    public void Identity_encoded_standard_cids_use_the_public_map(bool indirect, string encoding)
    {
        var result = PdfTextExtractor.Extract(Japan1(indirect: indirect, encoding: encoding));
        Assert.Contains("`a¥", result.Text);
        Assert.DoesNotContain(result.Diagnostics ?? [], message => message.StartsWith("PdfTextUndecodable:"));
        Assert.Null(result.Pages[0].UncertainText);
    }

    [Theory]
    [InlineData("0CD40E8A07A0", "日本語")]
    [InlineData("00E6", "0\uFE00")]
    [InlineData("046D", "逢\U000E0100")]
    public void Japanese_and_variation_sequences_preserve_unicode_text(string cids, string expected)
    {
        var result = PdfTextExtractor.Extract(Japan1(cids));
        Assert.Contains(expected, result.Text);
        Assert.Null(result.Pages[0].UncertainText);
    }

    [Theory]
    [InlineData("Identity-H", "Identity", 7)]
    [InlineData("Identity-H", "Japan1", 8)]
    [InlineData("90ms-RKSJ-H", "Japan1", 7)]
    public void Unidentified_or_unsupported_collections_still_require_source_comparison(string encoding, string ordering, int supplement)
    {
        var result = PdfTextExtractor.Extract(Japan1(encoding: encoding, ordering: ordering, supplement: supplement));
        Assert.Contains(result.Diagnostics!, message => message.StartsWith("PdfTextUndecodable:"));
    }

    [Fact]
    public void Explicit_partial_to_unicode_takes_precedence_and_does_not_get_standard_cid_guesses()
    {
        var result = PdfTextExtractor.Extract(Japan1("00410042", toUnicode: "/ToUnicode 5 0 R"));
        Assert.Contains("日", result.Text);
        Assert.DoesNotContain("a", result.Text);
        Assert.NotEmpty(result.Pages[0].UncertainText!);
    }

    [Fact]
    public async Task Cached_public_map_is_safe_for_concurrent_extraction_and_unknown_cids_remain_uncertain()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => PdfTextExtractor.Extract(Japan1()))));
        Assert.All(results, result => Assert.Contains("`a¥", result.Text));
        var unknown = PdfTextExtractor.Extract(Japan1("FFFF"));
        Assert.NotEmpty(unknown.Pages[0].UncertainText!);
    }
}
