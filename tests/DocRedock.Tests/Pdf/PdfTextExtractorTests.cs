using System.Text;
using DocRedock.Core.Reporting;
using DocRedock.Formats.Pdf;

namespace DocRedock.Tests.Pdf;

public sealed class PdfTextExtractorTests
{
    [Fact]
    public void Extracts_literal_text_and_simple_coordinates_in_reading_order()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length 42 >> stream\nBT 1 0 0 1 72 700 Tm (Hello) Tj 0 -20 Td (world) Tj ET\nendstream\n%%EOF");

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal(1, result.PageCount);
        Assert.Equal("Hello\nworld", result.Text);
        Assert.Equal(72d, result.Pages[0].Regions[0].BoundingBox.X);
        Assert.Equal(700d, result.Pages[0].Regions[0].BoundingBox.Y);
        Assert.Equal(FidelityLevel.F0, PdfRestorePolicy.For(false).Fidelity);
        Assert.Equal(FidelityLevel.F3, PdfRestorePolicy.For(true).Fidelity);
    }

    [Fact]
    public void Rejects_input_larger_than_explicit_bound()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n%%EOF");

        var exception = Assert.Throws<PdfExtractionException>(() =>
            PdfTextExtractor.Extract(pdf, new PdfExtractionOptions(MaxInputBytes: 4)));

        Assert.Contains("input exceeds", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_page_and_object_count_overflows_before_parsing_streams()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n" +
            "1 0 obj << /Type /Page >> endobj\n" +
            "2 0 obj << /Type /Page >> endobj\n%%EOF");

        Assert.Throws<PdfExtractionException>(() => PdfTextExtractor.Extract(pdf, new PdfExtractionOptions(MaxPages: 1)));
        Assert.Throws<PdfExtractionException>(() => PdfTextExtractor.Extract(pdf, new PdfExtractionOptions(MaxObjects: 1)));
    }

    [Fact]
    public void Broken_stream_fails_as_bounded_domain_error()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> stream\nBT (unterminated Tj\n%%EOF");

        Assert.Throws<PdfExtractionException>(() => PdfTextExtractor.Extract(pdf));
    }

    [Fact]
    public void Flate_expansion_is_bounded()
    {
        using var compressed = new MemoryStream();
        using (var compressor = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new StreamWriter(compressor, Encoding.Latin1, leaveOpen: true)) writer.Write(new string('A', 1024));
        var payload = compressed.ToArray();
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page /Filter /FlateDecode >> stream\n")
            .Concat(payload).Concat(Encoding.Latin1.GetBytes("\nendstream\n%%EOF")).ToArray();

        Assert.Throws<PdfExtractionException>(() => PdfTextExtractor.Extract(pdf,
            new PdfExtractionOptions(MaxExpandedStreamBytes: 16)));
    }

    [Fact]
    public void Operator_matching_has_a_finite_timeout_on_adversarial_text()
    {
        var adversarial = new string('(', 250_000);
        var pdf = Encoding.Latin1.GetBytes($"%PDF-1.4\n1 0 obj << /Type /Page >> stream\nBT ({adversarial}) Tj ET\nendstream\n%%EOF");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try { _ = PdfTextExtractor.Extract(pdf, new PdfExtractionOptions(RegexTimeout: TimeSpan.FromMilliseconds(1))); }
        catch (PdfExtractionException) { }
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"PDF operator matching exceeded bound: {stopwatch.Elapsed}");
    }

    [Fact]
    public void Decodes_type0_font_using_to_unicode_cmap()
    {
        var pdf = Encoding.Latin1.GetBytes("""
            %PDF-1.4
            1 0 obj << /Type /Page /Contents 2 0 R /Resources 3 0 R >> endobj
            2 0 obj << /Length 36 >> stream
            BT /F1 12 Tf 72 700 Td <0001> Tj ET
            endstream endobj
            3 0 obj << /Font << /F1 4 0 R >> >> endobj
            4 0 obj << /Type /Font /Subtype /Type0 /Encoding /Identity-H /ToUnicode 5 0 R >> endobj
            5 0 obj << /Length 104 >> stream
            /CIDInit /ProcSet findresource begin
            12 dict begin begincmap
            /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def
            /CMapName /Adobe-Identity-UCS def /CMapType 2 def
            1 begincodespacerange <0000> <FFFF> endcodespacerange
            1 beginbfchar <0001> <65E5> endbfchar
            endcmap CMapName currentdict /CMap defineresource pop end end
            endstream endobj
            %%EOF
            """);

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal("日", result.Text);
    }

    [Fact]
    public void Uses_the_last_active_font_before_a_text_operator()
    {
        var pdf = Encoding.Latin1.GetBytes("""
            %PDF-1.4
            1 0 obj << /Type /Page /Contents 2 0 R /Resources 3 0 R >> endobj
            2 0 obj << /Length 52 >> stream
            BT /F1 12 Tf /F2+0 12 Tf <0002> Tj ET
            endstream endobj
            3 0 obj << /Font << /F1 4 0 R /F2+0 6 0 R >> >> endobj
            4 0 obj << /Type /Font /Subtype /Type0 /ToUnicode 5 0 R >> endobj
            5 0 obj << /Length 48 >> stream
            1 beginbfchar <0002> <65E5> endbfchar
            endstream endobj
            6 0 obj << /Type /Font /Subtype /Type0 /ToUnicode 7 0 R >> endobj
            7 0 obj << /Length 48 >> stream
            1 beginbfchar <0002> <672C> endbfchar
            endstream endobj
            %%EOF
            """);

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal("本", result.Text);
    }

    [Fact]
    public void Does_not_apply_a_previous_objects_flate_filter_to_an_uncompressed_stream()
    {
        using var compressed = new MemoryStream();
        using (var compressor = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            compressor.Write(Encoding.Latin1.GetBytes("BT (Hello) Tj ET"));
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page /Contents 2 0 R >> endobj\n2 0 obj << /Filter /FlateDecode >> stream\n")
            .Concat(compressed.ToArray())
            .Concat(Encoding.Latin1.GetBytes("\nendstream endobj\n3 0 obj << /Type /Metadata >> stream\n<metadata>plain</metadata>\nendstream endobj\n%%EOF"))
            .ToArray();

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Contains("Hello", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Actual_text_overrides_reused_cid_glyphs()
    {
        var pdf = Encoding.Latin1.GetBytes("""
            %PDF-1.4
            1 0 obj << /Type /Page /Contents 2 0 R /Resources 3 0 R >> endobj
            2 0 obj << /Length 100 >> stream
            BT /F1 12 Tf <0001> Tj /Span << /ActualText <FEFF672C> >> BDC <0001> Tj EMC ET
            endstream endobj
            3 0 obj << /Font << /F1 4 0 R >> >> endobj
            4 0 obj << /Type /Font /Subtype /Type0 /Encoding /Identity-H /ToUnicode 5 0 R >> endobj
            5 0 obj << /Length 100 >> stream
            1 beginbfchar <0001> <65E5> endbfchar
            endstream endobj
            %%EOF
            """);

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Equal("日本", result.Text);
    }
}
