using DocRedock.Api;
using DocRedock.Core.Reporting;
using DocRedock.Providers.Abstractions.Providers;
using static DocRedock.Tests.Pdf.PdfFontResourceScopeTests;

namespace DocRedock.Tests.Pdf;

/// <summary>v0.2.12 evaluation: paths used only for clipping must neither create a diagram nor
/// request a page preview. Their text visibility effect and the content policy still apply.</summary>
public sealed class PdfClippingPathExportTests
{
    private static byte[] Pdf(string content) => Document(
        Object(1, "<< /Type /Page /MediaBox [0 0 612 792] /Contents 2 0 R /Resources << /Font << /F1 10 0 R >> >> >>"),
        Stream(2, "", content),
        Object(10, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));

    [Theory]
    [InlineData("W")]
    [InlineData("W*")]
    public async Task A_clipping_only_rectangle_exports_exactly_like_the_text_only_control(string clip)
    {
        var root = Directory.CreateTempSubdirectory("docredock-clip-only-").FullName;
        try
        {
            const string text = "BT /F1 16 Tf 72 540 Td (ONLY_VISIBLE_TEXT) Tj ET";
            var rasterizer = new CountingRasterizer();
            var service = new DocumentService(null, rasterizer, discoverPdfRasterizer: false);
            var control = await ExportAsync(service, root, "plain", Pdf(text), "visible");
            var clipped = await ExportAsync(service, root, "clipped", Pdf($"q 50 500 220 100 re {clip} n\n{text}\nQ"), "visible");

            Assert.Equal(control.Markdown, clipped.Markdown);
            Assert.Contains("ONLY\\_VISIBLE\\_TEXT", clipped.Markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("pdf_p1_path", clipped.Markdown, StringComparison.Ordinal);
            var summary = ExportSummaryBuilder.Build(clipped.Result.Graph, clipped.Result.Diagnostics);
            Assert.Equal((0, 0, 0, 0, 0), (summary.FallbackPaths, summary.FallbackPages,
                summary.ReviewPages, summary.ReviewImagePages, summary.Warnings));
            Assert.Empty(ExportReviewBuilder.Build(clipped.Result.Graph, clipped.Result.Diagnostics).Pages);
            Assert.Equal(0, rasterizer.Calls);
            Assert.True(clipped.Result.Graph.Assets is null or { Count: 0 });
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("visible", false)]
    [InlineData("sanitized", false)]
    [InlineData("complete", true)]
    public async Task Clipping_still_hides_outside_text_and_Q_restores_visibility(string policy, bool includeHidden)
    {
        var root = Directory.CreateTempSubdirectory("docredock-clip-policy-").FullName;
        try
        {
            var rasterizer = new CountingRasterizer();
            var service = new DocumentService(null, rasterizer, discoverPdfRasterizer: false);
            var pdf = Pdf("q 50 500 220 100 re W n\n" +
                "BT /F1 16 Tf 72 540 Td (INSIDE) Tj ET\nBT /F1 16 Tf 400 540 Td (OUTSIDE) Tj ET\nQ\n" +
                "BT /F1 16 Tf 400 500 Td (RESTORED) Tj ET");
            var export = await ExportAsync(service, root, policy, pdf, policy);

            Assert.Contains("INSIDE", export.Markdown, StringComparison.Ordinal);
            Assert.Contains("RESTORED", export.Markdown, StringComparison.Ordinal);
            Assert.Equal(includeHidden, export.Markdown.Contains("OUTSIDE", StringComparison.Ordinal));
            var summary = ExportSummaryBuilder.Build(export.Result.Graph, export.Result.Diagnostics);
            Assert.Equal((0, 0, 0), (summary.FallbackPaths, summary.ReviewPages, summary.ReviewImagePages));
            Assert.Equal(includeHidden ? 1 : 0, summary.Warnings);
            Assert.Equal(includeHidden, summary.HiddenContentIncluded);
            if (!includeHidden)
                Assert.Equal(DiagnosticSeverity.Information,
                    Assert.Single(export.Result.Diagnostics, d => d.Code == "PdfClippedTextExcluded").Severity);
            Assert.Equal(0, rasterizer.Calls);
            Assert.True(export.Result.Graph.Assets is null or { Count: 0 });
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<(ReadableDocumentExportResult Result, string Markdown)> ExportAsync(
        DocumentService service, string root, string name, byte[] pdf, string policy)
    {
        var source = Path.Combine(root, name + ".pdf");
        var markdown = Path.Combine(root, name + ".md");
        await File.WriteAllBytesAsync(source, pdf);
        var result = await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, markdown,
            ContentPolicy: policy, IncludePdfFallbackImages: true));
        return (result, await File.ReadAllTextAsync(markdown));
    }

    private sealed class CountingRasterizer : IPdfRasterizer
    {
        public int Calls { get; private set; }
        public ProviderDescriptor Descriptor { get; } = new("test.clip.rasterizer", new Version(1, 0), 1,
            new HashSet<string> { "rasterize.pdf" }, "MIT", "built-in", true);

        public ValueTask<IReadOnlyList<RasterizedPdfPage>> RasterizeAsync(string pdfPath, IReadOnlyList<int> pageNumbers,
            PdfRasterizationOptions options, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult<IReadOnlyList<RasterizedPdfPage>>([]);
        }
    }
}
