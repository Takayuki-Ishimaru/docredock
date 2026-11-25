using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;
using DocRedock.Markdown;
using DocRedock.Providers.Abstractions.Providers;
using DocRedock.Render;

namespace DocRedock.Tests.Api;

/// <summary>Source-page preview routing, without changing embedded-image or legacy Markdown counts.</summary>
public sealed class PdfSourcePagePreviewTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-source-preview-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData("visible")]
    [InlineData("complete")]
    public async Task Original_page_preview_is_open_before_the_extracted_description_and_uses_backend_bytes(string policy)
    {
        var raster = new RecordingRasterizer();
        var result = await Export(raster, policy);
        Assert.Equal([1], Assert.Single(raster.PageRequests));
        Assert.Equal(SHA256.HashData(await File.ReadAllBytesAsync(Source())), raster.SourceHash);
        var html = await File.ReadAllTextAsync(result.MarkdownPath);
        Assert.Contains("data:image/png;base64," + Convert.ToBase64String(raster.Png), html);
        Assert.Matches("""<details\b[^>]*data-kind="pdf-source-preview"[^>]*\bopen(?:\s|>)""", html);
        Assert.True(html.IndexOf("data-kind=\"pdf-source-preview\"", StringComparison.Ordinal) <
                    html.IndexOf("data-kind=\"pdf-source-description\"", StringComparison.Ordinal));
        Assert.Contains("VISIBLE_NATIVE_TEXT", html);
        Assert.Equal(1, html.Split("<img ", StringSplitOptions.None).Length - 1);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PdfSourcePagePreviewShown");

        var projected = ReadableProjectionBuilder.Build(result.Graph, new(ContentPolicy: policy, Language: "en"));
        var section = Assert.Single(projected.Document.Blocks.OfType<ReadableSection>());
        var preview = Assert.IsType<ReadableDetails>(section.Blocks[0]);
        Assert.Equal("pdf-source-preview", preview.Kind);
        Assert.True(preview.InitiallyOpen);
        var image = Assert.Single(preview.Blocks.OfType<ReadableImage>());
        Assert.Equal(400 * 96d / 72, image.WidthPixels!.Value, 6);
        Assert.Equal(320 * 96d / 72, image.HeightPixels!.Value, 6);
        Assert.Empty(projected.Report.UnwrittenImageIds);
    }

    [Theory]
    [InlineData(ReadableOutputFormat.Html)]
    [InlineData(ReadableOutputFormat.Markdown)]
    public async Task Sanitized_output_never_embeds_or_publishes_the_whole_page_raster(ReadableOutputFormat format)
    {
        var raster = new RecordingRasterizer();
        var service = new DocumentService(null, raster, discoverPdfRasterizer: false);
        var result = await service.ExportReadableAsync(new ReadableDocumentExportOptions(Source(),
            Path.Combine(root, format == ReadableOutputFormat.Html ? "result.html" : "result.md"), ContentPolicy: "sanitized",
            EnableOcr: false, OutputFormat: format, Language: "en"));
        var html = await File.ReadAllTextAsync(result.MarkdownPath);
        Assert.Contains("VISIBLE_NATIVE_TEXT", format == ReadableOutputFormat.Html ? html : Markdig.Markdown.ToPlainText(html));
        Assert.DoesNotContain("data:image/png", html);
        Assert.DoesNotContain("data-kind=\"pdf-source-preview\"", html);
        Assert.DoesNotContain(result.Graph.Nodes, node => node.Kind == NodeKind.Image && Flag(node, "pdf_page_raster"));
        Assert.False(Directory.Exists(Path.Combine(root, "result.assets")));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PdfSourcePagePreviewWithheld");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "PdfReviewImageAttached");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unavailable_or_failed_backend_keeps_native_text_and_explicit_review_warning(bool failed)
    {
        var result = await Export(failed ? new RecordingRasterizer(fail: true) : null, "visible");
        var html = await File.ReadAllTextAsync(result.MarkdownPath);
        Assert.Contains("VISIBLE_NATIVE_TEXT", html);
        Assert.DoesNotContain("data-kind=\"pdf-source-preview\"", html);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PdfReviewImageUnavailable");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_image_omission_or_disabled_fallback_does_not_claim_a_source_preview(bool omitImages)
    {
        var raster = new RecordingRasterizer();
        var result = await Export(raster, "visible", omitImages: omitImages, fallback: omitImages);
        var html = await File.ReadAllTextAsync(result.MarkdownPath);
        Assert.Contains("VISIBLE_NATIVE_TEXT", html);
        Assert.DoesNotContain("data-kind=\"pdf-source-preview\"", html);
        Assert.DoesNotContain("data:image/png", html);
        if (!omitImages) Assert.Empty(raster.PageRequests);
    }

    [Fact]
    public async Task Cancellation_from_the_page_renderer_does_not_publish_a_partial_output()
    {
        using var cancellation = new CancellationTokenSource();
        var output = Path.Combine(root, "cancelled.html");
        var service = new DocumentService(null, new CancellingRasterizer(cancellation), discoverPdfRasterizer: false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportReadableAsync(new(
            Source(), output, EnableOcr: false, OutputFormat: ReadableOutputFormat.Html), cancellation.Token));
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(Path.Combine(root, "cancelled.assets")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Direct_sanitized_projection_withholds_full_page_images_but_keeps_embedded_pictures(bool wholePage)
    {
        var image = new DocumentNode("image", NodeKind.Image, null, 1, ContentLayer.Body,
            new ReferenceNodeContent("source.png", "SOURCE_IMAGE"), Extensions: new Dictionary<string, JsonElement>
            {
                ["pdf_review_image"] = JsonSerializer.SerializeToElement(true),
                ["pdf_page_raster"] = JsonSerializer.SerializeToElement(wholePage),
            });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "pdf-source", DocumentFormatKind.Pdf,
            [new DocumentPartition("page-0001", 0, [image])]);
        var projection = ReadableProjectionBuilder.Build(graph, new(ContentPolicy: "sanitized", Language: "en"));
        var images = Flatten(projection.Document.Blocks).OfType<ReadableImage>().ToArray();
        Assert.Equal(wholePage ? 0 : 1, images.Length);
        Assert.Empty(projection.Report.UnwrittenImageIds);
        Assert.DoesNotContain(projection.Diagnostics, diagnostic => diagnostic.Code == "ImageNotWritten");
    }

    [Fact]
    public async Task Markdown_remains_a_separate_legacy_presentation_with_the_same_one_review_image()
    {
        var raster = new RecordingRasterizer();
        var result = await new DocumentService(null, raster, discoverPdfRasterizer: false)
            .ExportReadableAsync(new(Source(), Path.Combine(root, "legacy.md"), EnableOcr: false, Language: "en"));
        var markdown = await File.ReadAllTextAsync(result.MarkdownPath);
        Assert.Contains("VISIBLE_NATIVE_TEXT", Markdig.Markdown.ToPlainText(markdown));
        Assert.Contains("legacy.assets/page-0001.png", markdown);
        Assert.DoesNotContain("Original PDF page", markdown);
        Assert.Equal(1, result.Graph.Nodes.Count(node => node.Kind == NodeKind.Image && Flag(node, "pdf_page_raster")));
    }

    private Task<ReadableDocumentExportResult> Export(IPdfRasterizer? rasterizer, string policy,
        bool omitImages = false, bool fallback = true) =>
        new DocumentService(null, rasterizer, discoverPdfRasterizer: false).ExportReadableAsync(new(
            Source(), Path.Combine(root, "result.html"), EnableOcr: false, ContentPolicy: policy, Language: "en",
            OmitImages: omitImages, IncludePdfFallbackImages: fallback, OutputFormat: ReadableOutputFormat.Html));

    private string Source()
    {
        var path = Path.Combine(root, "curves.pdf");
        if (File.Exists(path)) return path;
        // Actual source operators: compound even-odd fill, cubic Bezier, clipping, and native text.
        const string content = "BT /F1 12 Tf 20 290 Td (VISIBLE_NATIVE_TEXT) Tj ET\n" +
            "0.2 0.4 0.7 rg 2 w 20 160 m 80 280 180 80 240 180 c 240 120 l 20 120 l h B\n" +
            "270 150 100 100 re 295 175 50 50 re f*\nq 20 20 50 50 re W n 1 0 0 rg 0 0 200 100 re f Q";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 320] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
        };
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var start = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n").Append(start).Append("\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
        return path;
    }

    private static bool Flag(DocumentNode node, string key) =>
        node.Extensions?.TryGetValue(key, out var value) == true && value.ValueKind == JsonValueKind.True;

    private static IEnumerable<ReadableBlock> Flatten(IEnumerable<ReadableBlock> blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            var children = block switch
            {
                ReadableSection section => section.Blocks,
                ReadableDetails details => details.Blocks,
                ReadableQuote quote => quote.Blocks,
                _ => Array.Empty<ReadableBlock>(),
            };
            foreach (var child in Flatten(children)) yield return child;
        }
    }

    private sealed class CancellingRasterizer(CancellationTokenSource cancellation) : IPdfRasterizer
    {
        public ProviderDescriptor Descriptor { get; } = new("test.source-cancel", new Version(1, 0), 1,
            new HashSet<string> { "rasterize.pdf" }, "MIT", "test", true);
        public ValueTask<IReadOnlyList<RasterizedPdfPage>> RasterizeAsync(string path, IReadOnlyList<int> pages,
            PdfRasterizationOptions options, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }
    }

    private sealed class RecordingRasterizer(bool fail = false) : IPdfRasterizer
    {
        public byte[] Png { get; } = PngRasterImage.Encode(400, 320, Enumerable.Repeat((byte)220, 400 * 320 * 3).ToArray());
        public List<int[]> PageRequests { get; } = [];
        public byte[]? SourceHash { get; private set; }
        public ProviderDescriptor Descriptor { get; } = new("test.source-preview", new Version(1, 0), 1,
            new HashSet<string> { "rasterize.pdf" }, "MIT", "test", true);
        public ValueTask<IReadOnlyList<RasterizedPdfPage>> RasterizeAsync(string path, IReadOnlyList<int> pages,
            PdfRasterizationOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceHash = SHA256.HashData(File.ReadAllBytes(path));
            PageRequests.Add(pages.ToArray());
            if (fail) throw new InvalidOperationException("Synthetic page preview failure.");
            return ValueTask.FromResult<IReadOnlyList<RasterizedPdfPage>>(
                pages.Select(page => new RasterizedPdfPage(page, "image/png", Png, 400, 320)).ToArray());
        }
    }
}
