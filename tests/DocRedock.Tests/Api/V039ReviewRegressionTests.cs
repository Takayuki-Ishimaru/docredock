using System.IO.Compression;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Providers.Abstractions.Providers;
using DocRedock.Render;

namespace DocRedock.Tests.Api;

public sealed class V039ReviewRegressionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-v039-review-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public async Task Cli_fail_on_review_stops_for_an_image_only_page_without_ocr()
    {
        var source = Path.Combine(root, "review-required.pdf");
        await File.WriteAllTextAsync(source, "%PDF-1.4\n1 0 obj << /Type /Page /MediaBox [0 0 100 100] >> endobj\n%%EOF");
        var output = new StringWriter();
        var error = new StringWriter();
        var cli = new DocRedock.Cli.CliApplication(output, error, new DocumentService(null, null, discoverPdfRasterizer: false));
        var code = await cli.RunAsync(["export", source, "--output", Path.Combine(root, "review-required.md"),
            "--allow-pdf", "--ocr", "off", "--fail-on-review", "--json"]);
        Assert.Equal(11, code);
        using var json = System.Text.Json.JsonDocument.Parse(output.ToString());
        Assert.True(json.RootElement.GetProperty("review_required").GetBoolean());
    }

    [Fact]
    public async Task Native_columns_get_a_comparison_image_without_running_ocr()
    {
        var commands = new List<string>
        {
            "BT /F1 14 Tf 50 760 Td (Left heading) Tj ET",
            "BT /F1 14 Tf 320 760 Td (Right heading) Tj ET"
        };
        for (var index = 0; index < 9; index++)
            commands.Add($"BT /F1 11 Tf 50 {733 - index * 16} Td (Left paragraph has several words {index}) Tj ET");
        commands.Add("BT /F1 11 Tf 320 733 Td (Right paragraph) Tj ET");
        var source = Path.Combine(root, "columns.pdf");
        await File.WriteAllBytesAsync(source, Pdf.MeasuredPdf.Page(string.Join('\n', commands)));
        var engine = new SuccessfulOcr();
        var rasterizer = new Rasterizer();
        var exported = await new DocumentService(engine, rasterizer, discoverPdfRasterizer: false)
            .ExportReadableAsync(new(source, Path.Combine(root, "columns.md"), EnableOcr: true));
        Assert.Contains(exported.Diagnostics, d => d.Code == "PdfColumnLayoutDetected");
        Assert.Equal(0, engine.Calls);
        Assert.NotNull(rasterizer.LastOptions);
        Assert.True(rasterizer.LastOptions.MaxPixelsPerPage < new PdfRasterizationOptions().MaxPixelsPerPage);
        var review = ExportReviewBuilder.Build(exported.Graph, exported.Diagnostics, exported.Projection);
        Assert.True(review.Required);
        Assert.Equal(1, review.ReviewImagePages);
        Assert.Contains(review.Pages.SelectMany(page => page.Elements), element => element.Kind == ReviewElementKind.ReadingOrderUncertain);
        Assert.DoesNotContain(review.Pages.SelectMany(page => page.Elements), element => element.Kind == ReviewElementKind.UnanalyzedContent);
        Assert.Contains("source comparison image", await File.ReadAllTextAsync(exported.MarkdownPath));
    }

    [Fact]
    public async Task Successful_scan_ocr_clears_the_native_text_unavailable_warning()
    {
        var source = Path.Combine(root, "recognized.pdf");
        await File.WriteAllTextAsync(source, "%PDF-1.4\n1 0 obj << /Type /Page /MediaBox [0 0 100 100] >> endobj\n%%EOF");
        var engine = new SuccessfulOcr();
        var rasterizer = new Rasterizer();
        var exported = await new DocumentService(engine, rasterizer, discoverPdfRasterizer: false)
            .ExportReadableAsync(new(source, Path.Combine(root, "recognized.md"), EnableOcr: true));
        Assert.Equal(1, engine.Calls);
        Assert.Equal(new PdfRasterizationOptions().MaxPixelsPerPage, rasterizer.LastOptions!.MaxPixelsPerPage);
        Assert.Contains("Recovered text", await File.ReadAllTextAsync(exported.MarkdownPath));
        Assert.DoesNotContain(exported.Diagnostics, d => d.Code == "PdfNativeTextUnavailable");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Textless_pdf_requires_review_even_when_ocr_is_off(bool rasterizerAvailable)
    {
        var source = Path.Combine(root, "scan.pdf");
        await File.WriteAllTextAsync(source, "%PDF-1.4\n1 0 obj << /Type /Page /MediaBox [0 0 100 100] >> endobj\n%%EOF");
        var service = new DocumentService(null, rasterizerAvailable ? new Rasterizer() : null, discoverPdfRasterizer: false);
        var exported = await service.ExportReadableAsync(new(source, Path.Combine(root, "scan.md"), EnableOcr: false));
        var review = ExportReviewBuilder.Build(exported.Graph, exported.Diagnostics, exported.Projection);
        var summary = ExportSummaryBuilder.BuildReadable(exported.Graph, exported.Diagnostics, exported.Projection);
        Assert.True(review.Required);
        Assert.True(summary.ReviewItemsDetected);
        Assert.Single(review.Pages);
        Assert.Equal(rasterizerAvailable ? 1 : 0, review.ReviewImagePages);
        Assert.Contains(exported.Diagnostics, d => d.Code == "OcrDisabled");
        Assert.DoesNotContain(exported.Diagnostics, d => d.Code == "PdfRasterizerUnavailable");
    }

    [Theory]
    [InlineData(true, 40, 20, false)]
    [InlineData(false, 40, 20, true)]
    [InlineData(true, 800, 1000, true)]
    public async Task Automatic_ocr_absence_is_information_for_illustrations_and_warning_for_scans(bool automatic, int width, int height, bool warning)
    {
        var source = Path.Combine(root, "images.xlsx");
        await new MarkdownRenderer().RenderAsync("# Images\n\nBody", RenderFormat.Xlsx, source);
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
        {
            await using var stream = archive.CreateEntry("xl/media/image.png").Open();
            await stream.WriteAsync(PngRasterImage.Encode(width, height, new byte[width * height * 3]));
        }
        var exported = await new DocumentService(new UnavailableOcr()).ExportReadableAsync(new(source,
            Path.Combine(root, "images.md"), EnableOcr: true, OcrAuto: automatic));
        var diagnostic = Assert.Single(exported.Diagnostics, d => d.Code is "ExecutableUnavailable" or "OcrUnavailableAuto");
        Assert.Equal(warning ? DiagnosticSeverity.Warning : DiagnosticSeverity.Information, diagnostic.Severity);
    }

    private sealed class Rasterizer : IPdfRasterizer
    {
        public PdfRasterizationOptions? LastOptions { get; private set; }
        public ProviderDescriptor Descriptor { get; } = new("test.rasterizer", new(1, 0), 1, new HashSet<string> { "rasterize.pdf" }, "MIT", "test", true);
        public ValueTask<IReadOnlyList<RasterizedPdfPage>> RasterizeAsync(string path, IReadOnlyList<int> pages, PdfRasterizationOptions options,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return ValueTask.FromResult<IReadOnlyList<RasterizedPdfPage>>(
                pages.Select(page => new RasterizedPdfPage(page, "image/png", PngRasterImage.Encode(10, 10, new byte[300]), 10, 10)).ToArray());
        }
    }

    private sealed class UnavailableOcr : IOcrEngine
    {
        public ProviderDescriptor Descriptor { get; } = new("test.ocr", new(1, 0), 1, new HashSet<string> { "ocr.text" }, "MIT", "test", true);
        public ValueTask<OcrAttemptResult> RecognizeAsync(OcrInput input, OcrOptions options, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OcrAttemptResult(OcrProcessingStatus.Unavailable, null,
                [new("ExecutableUnavailable", "OCR engine is not installed.", DiagnosticSeverity.Warning)]));
    }

    private sealed class SuccessfulOcr : IOcrEngine
    {
        public int Calls { get; private set; }
        public ProviderDescriptor Descriptor { get; } = new("test.ocr", new(1, 0), 1, new HashSet<string> { "ocr.text" }, "MIT", "test", true);
        public ValueTask<OcrAttemptResult> RecognizeAsync(OcrInput input, OcrOptions options, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new OcrAttemptResult(OcrProcessingStatus.Completed,
                new("Recovered text", [new("Recovered text", new Geometry("image-pixels", 0, 0, 10, 10), 0.99)]), []));
        }
    }
}
