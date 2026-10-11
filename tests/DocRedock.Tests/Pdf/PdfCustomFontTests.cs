using System.Buffers.Binary;
using System.Text;
using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.PdfFontResourceScopeTests;

namespace DocRedock.Tests.Pdf;

public sealed class PdfCustomFontTests
{
    private static byte[] Custom(string content, string encoding, string collection = "Identity", string extras = "", params string[] objects) => Document([
        Object(1, "<< /Type /Page /MediaBox [0 0 600 800] /Contents 2 0 R /Resources << /Font << /F1 3 0 R >> >> >>"),
        Stream(2, "", $"BT /F1 12 Tf 50 700 Td <{content}> Tj ET"),
        Object(3, $"<< /Type /Font /Subtype /Type0 /BaseFont /Custom /Encoding {encoding} /DescendantFonts [4 0 R] >>"),
        Object(4, $"<< /Subtype /CIDFontType2 /CIDSystemInfo << /Registry (Adobe) /Ordering ({collection}) /Supplement 7 >> /DW 1000 {extras} >>"),
        .. objects]);

    private static string FontProgram(bool ambiguous = false, bool truncated = false)
    {
        var points = ambiguous ? new uint[] { 0x41, 0x42 } : new uint[] { 0x65e5, 0x672c, 0x8a9e, 0x1f600 };
        var data = new byte[56 + points.Length * 12];
        void W16(int at, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at, 2), value);
        void W32(int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at, 4), value);
        W32(0, 0x00010000); W16(4, 1); W32(12, 0x636d6170); W32(20, 28); W32(24, (uint)data.Length - 28);
        W16(30, 1); W16(32, 3); W16(34, 10); W32(36, 12);
        W16(40, 12); W32(44, (uint)data.Length - 40); W32(52, (uint)points.Length);
        for (var index = 0; index < points.Length; index++) { W32(56 + index * 12, points[index]); W32(60 + index * 12, points[index]); W32(64 + index * 12, (uint)(ambiguous ? 7 : 7 + index)); }
        return Encoding.Latin1.GetString(truncated ? data[..^5] : data);
    }

    [Fact]
    public void Embedded_encoding_maps_arbitrary_codes_to_standard_cids()
    {
        var pdf = Custom("010203", "5 0 R", "Japan1", "", Stream(5, "", "3 begincidchar <01> 3284 <02> 3722 <03> 1952 endcidchar"));
        var result = PdfTextExtractor.Extract(pdf);
        Assert.Contains("日本語", result.Text);
        Assert.Null(result.Pages[0].UncertainText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Embedded_unicode_cmap_and_cid_to_gid_stream_resolve_custom_subset_ids(bool indirectLength)
    {
        var program = FontProgram();
        var stream = Stream(7, "", program);
        if (indirectLength) stream = stream.Replace($"/Length {program.Length}", "/Length 9 0 R");
        var result = PdfTextExtractor.Extract(Custom("0001000200030004", "/Identity-H", "Identity", "/CIDToGIDMap 8 0 R /FontDescriptor 6 0 R",
            Object(6, "<< /FontFile2 7 0 R >>"), stream, Stream(8, "", "\0\0\0\x07\0\x08\0\x09\0\x0a"), Object(9, program.Length.ToString())));
        Assert.Contains("日本語😀", result.Text);
        Assert.Null(result.Pages[0].UncertainText);
    }

    [Fact]
    public void Encoding_cid_ranges_inheritance_and_identity_gid_maps_compose()
    {
        var result = PdfTextExtractor.Extract(Custom("81828384", "5 0 R", "Identity", "/CIDToGIDMap /Identity /FontDescriptor 6 0 R",
            Stream(5, "/UseCMap 10 0 R", "1 begincidchar <84> 10 endcidchar"),
            Stream(10, "", "1 begincidrange <81><83> 7 endcidrange"), Object(6, "<< /FontFile2 7 0 R >>"), Stream(7, "", FontProgram())));
        Assert.Contains("日本語😀", result.Text);
        Assert.Null(result.Pages[0].UncertainText);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Ambiguous_or_malformed_font_maps_are_not_guessed(bool ambiguous, bool truncated)
    {
        var result = PdfTextExtractor.Extract(Custom("0007", "/Identity-H", "Identity", "/FontDescriptor 6 0 R",
            Object(6, "<< /FontFile2 7 0 R >>"), Stream(7, "", FontProgram(ambiguous, truncated))));
        Assert.NotEmpty(result.Pages[0].UncertainText!);
    }

    [Theory]
    [InlineData("1 begincidrange <0000><FFFFFFFF> 0 endcidrange", "")]
    [InlineData("1 begincidchar <01> 3284 endcidchar", "/UseCMap 5 0 R")]
    [InlineData("2 begincidchar <01> 3284 endcidchar", "")]
    [InlineData("2 begincodespacerange <00><FF> <0100><01FF> endcodespacerange 1 begincidchar <01> 3284 endcidchar", "")]
    public void Hostile_encoding_ranges_cycles_and_inconsistent_counts_fail_closed(string map, string header)
    {
        var result = PdfTextExtractor.Extract(Custom("01", "5 0 R", "Japan1", "", Stream(5, header, map)));
        Assert.NotEmpty(result.Pages[0].UncertainText!);
    }

    [Fact]
    public void Variable_length_codespace_keeps_an_unknown_code_from_consuming_the_next_character()
    {
        var map = "2 begincodespacerange <00><7F> <8100><81FF> endcodespacerange " +
            "2 begincidchar <01> 3284 <8101> 3722 endcidchar";
        var result = PdfTextExtractor.Extract(Custom("0181028101", "5 0 R", "Japan1", "", Stream(5, "", map)));
        Assert.Contains("日", result.Text); Assert.EndsWith("本", result.Text.Trim());
        Assert.NotEmpty(result.Pages[0].UncertainText!);
    }

    [Fact]
    public void Overlapping_unicode_groups_are_rejected_instead_of_using_a_partial_font_map()
    {
        var data = Encoding.Latin1.GetBytes(FontProgram());
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(68, 4), 0x65e5);
        var result = PdfTextExtractor.Extract(Custom("0007", "/Identity-H", "Identity", "/FontDescriptor 6 0 R",
            Object(6, "<< /FontFile2 7 0 R >>"), Stream(7, "", Encoding.Latin1.GetString(data))));
        Assert.NotEmpty(result.Pages[0].UncertainText!);
    }

    [Fact]
    public void True_type_format4_unicode_segments_are_inverted()
    {
        var data = new byte[80];
        void W16(int at, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at, 2), value);
        void W32(int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at, 4), value);
        W32(0, 0x00010000); W16(4, 1); W32(12, 0x636d6170); W32(20, 28); W32(24, 52);
        W16(30, 1); W16(32, 3); W16(34, 1); W32(36, 12); W16(40, 4); W16(42, 40); W16(46, 6);
        W16(54, 0x65e5); W16(56, 0x672c); W16(58, 0xffff);
        W16(62, 0x65e5); W16(64, 0x672c); W16(66, 0xffff);
        W16(68, unchecked((ushort)(7 - 0x65e5))); W16(70, unchecked((ushort)(8 - 0x672c))); W16(72, 1);
        var result = PdfTextExtractor.Extract(Custom("00070008", "/Identity-H", "Identity", "/FontDescriptor 6 0 R",
            Object(6, "<< /FontFile2 7 0 R >>"), Stream(7, "", Encoding.Latin1.GetString(data))));
        Assert.Contains("日本", result.Text); Assert.Null(result.Pages[0].UncertainText);
    }
}
