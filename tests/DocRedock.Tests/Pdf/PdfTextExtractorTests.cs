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
    public void Standalone_unlabelled_triangle_is_a_node_not_an_arrowhead()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length 45 >> stream\n0 0 m 100 0 l 50 100 l h S\nendstream\n%%EOF");

        var graph = PdfTextExtractor.Extract(pdf).VisualGraphs![1];

        // With no other semantic element anywhere on the page, this closed path is the page's
        // only visual content, so it is retained as a node on the graph rather than dropped (a
        // validator would not normally promote an isolated synthetic shape into Mermaid, but
        // there is nothing else on this page for it to compete against). Suppression of an
        // off-flow unlabelled triangle when a real flow coexists elsewhere on the page is
        // covered separately by R3_S0_6_off_flow_unlabelled_triangle_is_not_promoted_when_a_flow_exists.
        Assert.Single(graph.Nodes);
        Assert.DoesNotContain(graph.SourceItems!, item => item.Reason?.Contains("arrowhead", StringComparison.OrdinalIgnoreCase) == true);
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
        Assert.Contains(graph.Diagnostics!, diagnostic => diagnostic.Code == "VisualConnectorUnresolved" &&
            diagnostic.Format == "pdf" && diagnostic.PartUri == "pdf:page:1" && diagnostic.PartitionId == "page-1");
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

    [Fact]
    public void Visual_inference_timeout_returns_vector_fallback_without_partial_topology()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length 110 >> stream\n0 0 m 100 0 l S 0 0 20 20 re S 80 -10 20 20 re S\nendstream\n%%EOF");

        var result = PdfTextExtractor.Extract(pdf,
            new PdfExtractionOptions(VisualInferenceTimeout: TimeSpan.Zero));
        var graph = result.VisualGraphs![1];

        Assert.DoesNotContain(graph.Edges, edge => edge.SourceId is not null || edge.TargetId is not null);
        var diagnostic = Assert.Single(graph.Diagnostics!, item => item.Code == "VisualInferenceTimeout");
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Fallback));
        Assert.Contains(result.Diagnostics!, item => item.StartsWith("VisualInferenceTimeout", StringComparison.Ordinal));
        Assert.True(graph.Accounting.IsConsistent);
    }

    [Theory]
    [InlineData(0d, 1d)]
    [InlineData(500d, 1d)]
    [InlineData(0d, .01d)]
    public void Vector_connection_inference_is_translation_and_uniform_scale_invariant(double translate, double scale)
    {
        var matrix = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{scale} 0 0 {scale} {translate} {translate} cm");
        var pdf = Encoding.Latin1.GetBytes($"%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length 140 >> stream\nq {matrix} 0 0 20 20 re S 100 0 20 20 re S 20 10 m 100 10 l S Q\nendstream\n%%EOF");

        var edge = Assert.Single(PdfTextExtractor.Extract(pdf).VisualGraphs![1].Edges);

        Assert.Equal(VisualEdgeResolution.GeometryInferred, edge.Resolution);
        Assert.NotNull(edge.SourceId);
        Assert.NotNull(edge.TargetId);
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

    [Fact]
    public void Closed_path_boxes_are_canonicalized_and_triangle_arrowhead_promotes_shaft_to_directed()
    {
        var pdf = Encoding.Latin1.GetBytes("""
            %PDF-1.4
            1 0 obj << /Type /Page >> endobj
            2 0 obj << /Length 300 >> stream
            BT 1 0 0 1 10 20 Tm (PDF_FLOW_START) Tj ET
            0 0 100 50 re S 0 0 100 50 re S
            BT 1 0 0 1 210 20 Tm (PDF_FLOW_DONE) Tj ET
            200 0 100 50 re S
            100 25 m 200 25 l S
            200 25 m 190 32 l 190 18 l h f
            endstream
            %%EOF
            """);

        var graph = Assert.Single(PdfTextExtractor.Extract(pdf).VisualGraphs!).Value;

        Assert.Equal(2, graph.Nodes.Count);
        Assert.Single(graph.Nodes, node => node.Label == "PDF_FLOW_START");
        Assert.Single(graph.Nodes, node => node.Label == "PDF_FLOW_DONE");
        var edge = Assert.Single(graph.Edges);
        Assert.Equal(VisualEdgeDirection.Directed, edge.EdgeDirection);
        Assert.Equal("directed", edge.Direction);
        Assert.Equal("end", edge.Evidence?.ArrowheadEvidence);
        Assert.Equal(VisualGraphQuality.HighConfidenceInferred, graph.Quality);
        Assert.Contains(graph.SourceItems!, item => item.Disposition == VisualDisposition.SuppressedDuplicate);
        var validation = VisualGraphValidator.Validate(graph);
        Assert.True(graph.HasTopology, string.Join("; ", validation.Errors.Select(error => error.Code + ": " + error.Message)));
    }

    [Fact]
    public void R0_FIX07_decorative_unlabelled_rectangles_are_accounted_without_becoming_semantic_nodes()
    {
        var pdf = Encoding.Latin1.GetBytes("""
            %PDF-1.4
            1 0 obj << /Type /Page >> endobj
            2 0 obj << /Length 420 >> stream
            BT 1 0 0 1 10 20 Tm (RL_START) Tj ET
            0 0 100 50 re S
            BT 1 0 0 1 210 20 Tm (RL_CHECK) Tj ET
            200 0 100 50 re S
            100 25 m 200 25 l S
            200 25 m 190 32 l 190 18 l h f
            400 400 8 8 re f
            420 400 8 8 re f
            440 400 8 8 re f
            endstream
            %%EOF
            """);

        var graph = Assert.Single(PdfTextExtractor.Extract(pdf).VisualGraphs!).Value;

        Assert.Collection(graph.Nodes,
            node => Assert.Equal("RL_START", node.Label),
            node => Assert.Equal("RL_CHECK", node.Label));
        var edge = Assert.Single(graph.Edges);
        Assert.Equal(VisualEdgeDirection.Directed, edge.EdgeDirection);
        Assert.Equal("RL_START", graph.Nodes.Single(node => node.Id == edge.SourceId).Label);
        Assert.Equal("RL_CHECK", graph.Nodes.Single(node => node.Id == edge.TargetId).Label);
        Assert.DoesNotContain(graph.Nodes, node => node.Label.StartsWith("Vector node", StringComparison.Ordinal));
        Assert.True(graph.SourceItems!.Count(item => item.Disposition == VisualDisposition.IgnoredDecorative ||
            item.Disposition == VisualDisposition.VisualFallback) >= 3);
    }

    [Fact]
    public void R3_S0_7_stroke_and_filled_arrowhead_before_text_does_not_misassign_a_node_label_as_an_edge_label()
    {
        // Content-stream order deliberately inverted from the fixtures above: the shaft and its
        // filled triangle arrowhead are painted FIRST, both rectangles second, and the
        // RL_START/RL_CHECK text runs (BT/Tj) LAST -- after the shapes they label. This guards
        // against mis-assigning a node's own label text as a nearby EDGE label when paint order
        // doesn't match reading order.
        var pdf = Encoding.Latin1.GetBytes("""
            %PDF-1.4
            1 0 obj << /Type /Page >> endobj
            2 0 obj << /Length 260 >> stream
            100 25 m 200 25 l S
            200 25 m 190 32 l 190 18 l h f
            0 0 100 50 re S
            200 0 100 50 re S
            BT 1 0 0 1 10 20 Tm (RL_START) Tj ET
            BT 1 0 0 1 210 20 Tm (RL_CHECK) Tj ET
            endstream
            %%EOF
            """);

        var graph = Assert.Single(PdfTextExtractor.Extract(pdf).VisualGraphs!).Value;

        Assert.Collection(graph.Nodes,
            node => Assert.Equal("RL_START", node.Label),
            node => Assert.Equal("RL_CHECK", node.Label));
        var edge = Assert.Single(graph.Edges);
        Assert.NotNull(edge.SourceId);
        Assert.NotNull(edge.TargetId);
        // Negative case: neither node label was misassigned as the connector's own edge label.
        Assert.False(edge.Label != null && edge.Label.StartsWith("RL_", StringComparison.Ordinal));
    }

    [Fact]
    public void R3_S0_6_off_flow_unlabelled_triangle_is_not_promoted_when_a_flow_exists()
    {
        // A real two-node flow (RL_START -> RL_CHECK, same shape as R0_FIX07 above) plus one
        // extra unlabelled filled triangle far off to the side (400,400), unconnected to
        // anything. Unlike Standalone_unlabelled_triangle_is_a_node_not_an_arrowhead (where an
        // isolated triangle is the page's only content and is kept as a node), the presence of a
        // real flow here means the off-flow triangle must not be promoted to a semantic
        // "Vector node" -- it is still accounted for, just not as graph topology.
        var pdf = Encoding.Latin1.GetBytes("""
            %PDF-1.4
            1 0 obj << /Type /Page >> endobj
            2 0 obj << /Length 320 >> stream
            BT 1 0 0 1 10 20 Tm (RL_START) Tj ET
            0 0 100 50 re S
            BT 1 0 0 1 210 20 Tm (RL_CHECK) Tj ET
            200 0 100 50 re S
            100 25 m 200 25 l S
            200 25 m 190 32 l 190 18 l h f
            400 400 m 440 400 l 420 440 l h f
            endstream
            %%EOF
            """);

        var graph = Assert.Single(PdfTextExtractor.Extract(pdf).VisualGraphs!).Value;

        Assert.DoesNotContain(graph.Nodes, node => node.Label.StartsWith("Vector node", StringComparison.Ordinal));
        Assert.Single(graph.Edges);
        // The off-flow triangle isn't a repeated decorative pattern (unlike R0_FIX07's three
        // small squares), so it is retained as a generic fallback path rather than dropped or
        // promoted to a node -- this is the "some disposition" accounting entry for it.
        Assert.Contains(graph.SourceItems!, item => item.Disposition == VisualDisposition.VisualFallback &&
            item.Reason == "vector path retained as fallback");
    }

    [Fact]
    public async Task R3_far_unclaimed_text_is_not_absorbed_as_a_node_or_arrowhead_label()
    {
        // Same two-node-plus-arrowhead flow as Closed_path_boxes_are_canonicalized_and_
        // triangle_arrowhead_promotes_shaft_to_directed above, plus one more text run
        // ("REMOTE") placed at (5000, 5000) -- far past 3x either rectangle's ~112pt
        // diagonal from anything in the flow. Before LabelScore gained a proximity floor,
        // its distance term 1/(1+centerDistance) stayed positive for any finite distance,
        // so once RL_START and RL_CHECK were claimed by their own rectangles, REMOTE was
        // the *only* unclaimed text region left when the triangle ran through that same
        // labelCandidate selection -- and a positive score, however tiny, was enough to
        // "win" by default. That routed the triangle away from IsTriangle's arrowhead
        // branch and turned a legitimate arrowhead into a spurious "REMOTE" node instead
        // of directed-edge evidence.
        var pdf = Encoding.Latin1.GetBytes("""
            %PDF-1.4
            1 0 obj << /Type /Page >> endobj
            2 0 obj << /Length 360 >> stream
            BT 1 0 0 1 10 20 Tm (RL_START) Tj ET
            0 0 100 50 re S
            BT 1 0 0 1 210 20 Tm (RL_CHECK) Tj ET
            200 0 100 50 re S
            100 25 m 200 25 l S
            200 25 m 190 32 l 190 18 l h f
            BT 1 0 0 1 5000 5000 Tm (REMOTE) Tj ET
            endstream
            %%EOF
            """);

        var graph = Assert.Single(PdfTextExtractor.Extract(pdf).VisualGraphs!).Value;

        // The flow resolves exactly as it does without the far-away text: two labelled
        // nodes and one directed edge, with the triangle correctly read as arrowhead
        // evidence rather than being promoted into a third "REMOTE" node.
        Assert.Equal(2, graph.Nodes.Count);
        Assert.DoesNotContain(graph.Nodes, node => node.Label.Contains("REMOTE", StringComparison.Ordinal));
        var edge = Assert.Single(graph.Edges);
        Assert.Equal(VisualEdgeDirection.Directed, edge.EdgeDirection);
        Assert.True(edge.Label is null || !edge.Label.Contains("REMOTE", StringComparison.Ordinal));

        var root = Path.Combine(Path.GetTempPath(), "docredock-pdf-far-label-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "far-label.pdf");
            var output = Path.Combine(root, "far-label.md");
            await File.WriteAllBytesAsync(source, pdf);
            await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, output));
            var markdown = await File.ReadAllTextAsync(output);

            var mermaidStart = markdown.IndexOf("```mermaid", StringComparison.Ordinal);
            Assert.True(mermaidStart >= 0, "Expected the resolved flow to render as a mermaid diagram.");
            var mermaidEnd = markdown.IndexOf("```", mermaidStart + "```mermaid".Length, StringComparison.Ordinal);
            var mermaid = markdown[mermaidStart..(mermaidEnd < 0 ? markdown.Length : mermaidEnd)];

            // REMOTE must never appear inside the mermaid diagram itself -- as a node or edge
            // label -- even though the underlying extracted text may still be present
            // elsewhere in the readable export as ordinary body text.
            Assert.DoesNotContain("REMOTE", mermaid, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void R5_v_head_promotes_horizontal_shaft_to_directed_edge()
    {
        var pdf = Encoding.Latin1.GetBytes("""
            %PDF-1.4
            1 0 obj << /Type /Page >> endobj
            2 0 obj << /Length 280 >> stream
            BT 1 0 0 1 10 20 Tm (V_START) Tj ET
            0 0 100 50 re S
            BT 1 0 0 1 210 20 Tm (V_DONE) Tj ET
            200 0 100 50 re S
            100 25 m 200 25 l S
            190 32 m 200 25 l 190 18 l S
            endstream
            %%EOF
            """);

        var graph = Assert.Single(PdfTextExtractor.Extract(pdf).VisualGraphs!).Value;
        Assert.Collection(graph.Nodes, node => Assert.Equal("V_START", node.Label), node => Assert.Equal("V_DONE", node.Label));
        var edge = Assert.Single(graph.Edges);
        Assert.Equal(VisualEdgeDirection.Directed, edge.EdgeDirection);
        Assert.Equal("V_START", graph.Nodes.Single(node => node.Id == edge.SourceId).Label);
        Assert.Equal("V_DONE", graph.Nodes.Single(node => node.Id == edge.TargetId).Label);
        Assert.True(VisualGraphValidator.Validate(graph).Accounting.IsConsistent);
    }

    [Fact]
    public void R5_forward_opening_v_is_not_promoted_to_an_arrowhead()
    {
        var pdf = Encoding.Latin1.GetBytes("""
            %PDF-1.4
            1 0 obj << /Type /Page >> endobj
            2 0 obj << /Length 280 >> stream
            BT 1 0 0 1 10 20 Tm (V_NEGATIVE_START) Tj ET
            0 0 100 50 re S
            BT 1 0 0 1 210 20 Tm (V_NEGATIVE_DONE) Tj ET
            200 0 100 50 re S
            100 25 m 200 25 l S
            210 32 m 200 25 l 210 18 l S
            endstream
            %%EOF
            """);

        var graph = Assert.Single(PdfTextExtractor.Extract(pdf).VisualGraphs!).Value;
        Assert.DoesNotContain(graph.Edges, edge => edge.EdgeDirection == VisualEdgeDirection.Directed);
        Assert.Contains(graph.Edges, edge => edge.Resolution == VisualEdgeResolution.Unresolved);
    }

    [Fact]
    public void R3_intermediate_node_box_after_connector_stays_visible_to_inference_and_blocks_skip_edge()
    {
        // START and END sit at the two ends of a single straight connector; MIDDLE's box is
        // drawn on that same line, between them, but the shaft only ever touches START's and
        // END's boundaries -- never MIDDLE's. DiagramClusterer only unions a node into a
        // connector's cluster when the node touches one of the connector's two path endpoints,
        // or sits within a generic center-distance radius of another already-unioned shape; a
        // perfectly horizontal (or vertical) connector's own bounding box has a zero-length
        // minor axis, which collapses that generic radius to almost nothing. MIDDLE satisfies
        // neither test, so without an explicit whole-canvas cluster it would be split into its
        // own single-node cluster -- invisible to the corridor check that exists specifically to
        // stop the flanking nodes from resolving a "skip" edge across a node drawn in between.
        // MIDDLE must both stay a first-class labelled node and remain visible to that check, so
        // the shaft is correctly left unresolved rather than reporting a false START --> END.
        var pdf = Encoding.Latin1.GetBytes("""
            %PDF-1.4
            1 0 obj << /Type /Page >> endobj
            2 0 obj << /Length 400 >> stream
            BT /F1 12 Tf 100 330 Td (START) Tj ET
            BT /F1 12 Tf 500 330 Td (END) Tj ET
            80 300 100 60 re S
            500 300 100 60 re S
            180.00 330 m 500.00 330 l S
            500.00 330 m 488.00 338 l 488.00 322 l h f
            300 300 100 60 re S
            BT /F1 12 Tf 315 330 Td (MIDDLE) Tj ET
            endstream
            %%EOF
            """);

        var graph = Assert.Single(PdfTextExtractor.Extract(pdf).VisualGraphs!).Value;

        Assert.Collection(graph.Nodes,
            node => Assert.Equal("START", node.Label),
            node => Assert.Equal("END", node.Label),
            node => Assert.Equal("MIDDLE", node.Label));

        // No topology: the connector stays unresolved with a diagnostic rather than silently
        // connecting START to END across the node drawn in between.
        var edge = Assert.Single(graph.Edges);
        Assert.Equal(VisualEdgeResolution.Unresolved, edge.Resolution);
        Assert.Null(edge.SourceId);
        Assert.Null(edge.TargetId);
        Assert.Contains(graph.Diagnostics!, diag => diag.Code == "VisualConnectorUnresolved");

        // Negative case: START --> END must never appear, resolved or otherwise.
        var startId = graph.Nodes.Single(node => node.Label == "START").Id;
        var endId = graph.Nodes.Single(node => node.Label == "END").Id;
        Assert.DoesNotContain(graph.Edges, candidate => candidate.SourceId == startId && candidate.TargetId == endId);
    }
}
