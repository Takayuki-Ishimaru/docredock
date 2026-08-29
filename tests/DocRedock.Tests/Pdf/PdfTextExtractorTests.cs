using System.Text;
using DocRedock.Api;
using DocRedock.Core.Documents;
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

    [Fact]
    public void Vector_only_page_is_retained_with_explicit_visual_diagnostic()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length 24 >> stream\n0 0 m 100 100 l S\nendstream\n%%EOF");

        var result = PdfTextExtractor.Extract(pdf);

        Assert.True(result.Pages[0].HasVectorContent);
        Assert.Contains("vector drawing", result.Text, StringComparison.Ordinal);
        Assert.NotNull(result.VisualGraphs);
        Assert.Contains(1, result.VisualGraphs!.Keys);
        Assert.Contains(result.Diagnostics!, diagnostic => diagnostic.Contains("VisualSemanticProjectionUnavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void Vector_and_text_page_reports_partial_visual_projection()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length 42 >> stream\nBT 1 0 0 1 72 700 Tm (Hello) Tj ET 0 0 m 100 100 l S\nendstream\n%%EOF");

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Contains("Hello", result.Text, StringComparison.Ordinal);
        Assert.True(result.Pages[0].HasVectorContent);
        Assert.Contains(result.Diagnostics!, diagnostic => diagnostic.Contains("VisualSemanticProjectionUnavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void Image_xobject_only_page_is_image_only_and_rasterizer_diagnostic()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length 8 >> stream\n/Im1 Do\nendstream\n%%EOF");

        var result = PdfTextExtractor.Extract(pdf);

        Assert.True(result.Pages[0].IsImageOnly);
        Assert.False(result.Pages[0].HasVectorContent);
        Assert.Contains("image-only content", result.Text, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics!, diagnostic => diagnostic.Contains("PdfRasterizerUnavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void Image_xobject_with_native_text_is_not_image_only()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length 37 >> stream\nBT (Hello) Tj ET /Im1 Do\nendstream\n%%EOF");

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Contains("Hello", result.Text, StringComparison.Ordinal);
        Assert.False(result.Pages[0].IsImageOnly);
        Assert.DoesNotContain(result.Diagnostics!, diagnostic => diagnostic.Contains("PdfRasterizerUnavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Image_only_page_keeps_placeholder_and_export_diagnostic()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n%%EOF");
        var extraction = PdfTextExtractor.Extract(pdf);

        Assert.True(extraction.Pages[0].IsImageOnly);
        Assert.Contains("image-only content", extraction.Text, StringComparison.Ordinal);
        Assert.Contains(extraction.Diagnostics!, diagnostic => diagnostic.Contains("PdfRasterizerUnavailable", StringComparison.Ordinal));

        var root = Path.Combine(Path.GetTempPath(), "docredock-pdf-image-only-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "image-only.pdf");
            var output = Path.Combine(root, "image-only.md");
            await File.WriteAllBytesAsync(source, pdf);
            var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, output));
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PdfRasterizerUnavailable");
            Assert.Contains("image-only content", await File.ReadAllTextAsync(output), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Multiple_content_streams_on_one_page_share_page_number_and_diagnostics()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page /Contents [2 0 R 3 0 R] >> endobj\n2 0 obj << /Length 16 >> stream\nBT (Hello) Tj ET\nendstream endobj\n3 0 obj << /Length 16 >> stream\n0 0 m 10 10 l S\nendstream endobj\n%%EOF");

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Single(result.Pages);
        Assert.Equal(1, result.Pages[0].PageNumber);
        Assert.Contains("Hello", result.Text, StringComparison.Ordinal);
        Assert.True(result.Pages[0].HasVectorContent);
        Assert.Contains(result.Diagnostics!, diagnostic => diagnostic.Contains("page 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Operator_words_inside_pdf_strings_do_not_trigger_visual_detection()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length 48 >> stream\nBT (S m l Do) Tj ET\n/S /Do\n% m l S Do\n<0044> Tj\nendstream\n%%EOF");

        var result = PdfTextExtractor.Extract(pdf);

        Assert.Contains("S m l Do", result.Text, StringComparison.Ordinal);
        Assert.False(result.Pages[0].HasVectorContent);
        Assert.False(result.Pages[0].IsImageOnly);
        Assert.Empty(result.Diagnostics!);
    }

    [Fact]
    public void Vector_paths_apply_cm_inside_q_Q_and_project_rectangles()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length 48 >> stream\nq 2 0 0 2 10 20 cm 0 0 20 10 re S Q\nendstream\n%%EOF");
        var result = PdfTextExtractor.Extract(pdf);
        var graph = result.VisualGraphs![1];
        Assert.DoesNotContain("unavailable", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Diagnostics!, diagnostic => diagnostic.Contains("VisualSemanticProjectionUnavailable", StringComparison.Ordinal));
        Assert.Single(graph.Nodes);
        Assert.Equal(10d, graph.Nodes[0].Geometry!.X);
        Assert.Equal(20d, graph.Nodes[0].Geometry!.Y);
        Assert.Equal(40d, graph.Nodes[0].Geometry!.Width);
    }

    [Fact]
    public void Curve_paths_are_retained_as_fallback_visual_paths()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length 56 >> stream\n0 0 m 10 10 20 0 30 10 c S\nendstream\n%%EOF");
        var result = PdfTextExtractor.Extract(pdf);
        var graph = result.VisualGraphs![1];
        Assert.Contains(graph.Paths!, path => path.IsFallback && path.Points!.Count >= 4);
        Assert.Contains(graph.Diagnostics!, diagnostic => diagnostic.Code == "VisualPathPartial");
    }

    [Fact]
    public void Ambiguous_open_path_remains_unresolved()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length 42 >> stream\n0 0 m 100 100 l S\nendstream\n%%EOF");
        var result = PdfTextExtractor.Extract(pdf);
        var graph = result.VisualGraphs![1];
        Assert.Contains(graph.Edges, edge => edge.Resolution == VisualEdgeResolution.Unresolved);
        Assert.Contains("[PDF visual content:", result.Text, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics!, diagnostic => diagnostic.Contains("VisualSemanticProjectionUnavailable", StringComparison.Ordinal));
        Assert.Contains(graph.Diagnostics!, diagnostic => diagnostic.Code == "VisualConnectorUnresolved");
    }

    [Fact]
    public void Implicit_close_paints_closed_path_and_keeps_multiple_subpaths()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length 58 >> stream\n0 0 m 20 0 l 20 20 l b 30 30 m 40 40 l S\nendstream\n%%EOF");
        var graph = PdfTextExtractor.Extract(pdf).VisualGraphs![1];
        Assert.Contains(graph.Nodes, node => node.Geometry!.Width == 20);
        Assert.True(graph.Paths!.Count >= 2);
    }

    [Fact]
    public void Stroke_before_rectangle_is_resolved_in_second_pass()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length 110 >> stream\n0 0 m 100 0 l S 0 0 20 20 re S 80 -10 20 20 re S\nendstream\n%%EOF");
        var graph = PdfTextExtractor.Extract(pdf).VisualGraphs![1];
        Assert.Contains(graph.Edges, edge => edge.Resolution == VisualEdgeResolution.GeometryInferred);
    }

    [Theory]
    [InlineData("s")]
    [InlineData("b")]
    [InlineData("b*")]
    [InlineData("f")]
    [InlineData("F")]
    [InlineData("f*")]
    [InlineData("B")]
    [InlineData("B*")]
    public void Pdf_implicit_close_paint_operators_create_closed_nodes(string paint)
    {
        var pdf = Encoding.Latin1.GetBytes($"%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length 30 >> stream\n0 0 m 20 0 l 20 20 l {paint}\nendstream\n%%EOF");
        var graph = PdfTextExtractor.Extract(pdf).VisualGraphs![1];
        Assert.Contains(graph.Nodes, node => node.Geometry is not null);
    }
}
