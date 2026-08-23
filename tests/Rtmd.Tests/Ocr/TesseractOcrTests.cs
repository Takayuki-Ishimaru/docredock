using System.Text;
using Rtmd.Core.Reporting;
using Rtmd.Ocr.Tesseract;
using Rtmd.Providers.Abstractions.Providers;

namespace Rtmd.Tests.Ocr;

public sealed class TesseractOcrTests
{
    [Fact]
    public void Tsv_parser_returns_reading_order_regions_and_confidence()
    {
        const string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
                           "5\t1\t1\t1\t1\t1\t100\t20\t40\t12\t95.0\tHello\n" +
                           "5\t1\t1\t1\t1\t2\t145\t20\t40\t12\t75.0\tworld\n" +
                           "5\t1\t1\t1\t2\t1\t100\t40\t40\t12\t-1\tignored\n";

        var result = TsvParser.Parse(tsv);

        Assert.Equal("Hello world", result.Text);
        Assert.Equal(2, result.Regions.Count);
        Assert.NotNull(result.Regions[0].Confidence);
        Assert.InRange(result.Regions[0].Confidence!.Value, 0.949, 0.951);
        Assert.Equal(100d, result.Regions[0].BoundingBox!.X);
        Assert.Equal(145d, result.Regions[1].BoundingBox!.X);
    }

    [Fact]
    public async Task Missing_tesseract_is_unavailable_and_does_not_modify_image_stream()
    {
        var bytes = Encoding.ASCII.GetBytes("not really an image");
        await using var image = new MemoryStream(bytes);
        var engine = new TesseractOcrEngine("rtmd-test-tesseract-that-is-not-installed");

        var result = await engine.RecognizeAsync(new OcrInput("img-1", image, "image/png"), new OcrOptions(["jpn", "eng"]), CancellationToken.None);

        Assert.Equal(OcrProcessingStatus.Unavailable, result.Status);
        Assert.Null(result.Result);
        Assert.Equal(0, image.Position);
    }

    [Fact]
    public async Task Pixel_budget_is_reported_as_skipped_without_running_provider()
    {
        await using var image = new MemoryStream([1, 2, 3, 4]);
        var result = await new TesseractOcrEngine("/bin/echo").RecognizeAsync(
            new OcrInput("img-budget", image, "image/png"),
            new OcrOptions(["jpn", "eng"], PixelBudget: 1), CancellationToken.None);

        Assert.Equal(OcrProcessingStatus.SkippedByBudget, result.Status);
        Assert.Null(result.Result);
        Assert.Equal(0, image.Position);
    }

    [Fact]
    public async Task Fallback_engine_uses_secondary_provider_only_when_primary_is_unavailable()
    {
        var primary = new StubOcrEngine("test.primary", OcrProcessingStatus.Unavailable);
        var fallback = new StubOcrEngine("test.fallback", OcrProcessingStatus.Completed, "fallback text");
        await using var image = new MemoryStream([1, 2, 3]);

        var result = await new FallbackOcrEngine(primary, fallback).RecognizeAsync(
            new OcrInput("img-1", image, "image/png"), new OcrOptions(["jpn", "eng"]), CancellationToken.None);

        Assert.Equal(OcrProcessingStatus.Completed, result.Status);
        Assert.Equal("fallback text", result.Result!.Text);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(1, fallback.CallCount);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "OcrFallbackUsed");
    }

    [Fact]
    public async Task Fallback_engine_does_not_mask_a_failed_primary_provider()
    {
        var primary = new StubOcrEngine("test.primary", OcrProcessingStatus.Failed);
        var fallback = new StubOcrEngine("test.fallback", OcrProcessingStatus.Completed, "must not be used");
        await using var image = new MemoryStream([1, 2, 3]);

        var result = await new FallbackOcrEngine(primary, fallback).RecognizeAsync(
            new OcrInput("img-1", image, "image/png"), new OcrOptions(["eng"]), CancellationToken.None);

        Assert.Equal(OcrProcessingStatus.Failed, result.Status);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(0, fallback.CallCount);
    }

    private sealed class StubOcrEngine(string providerId, OcrProcessingStatus status, string? text = null) : IOcrEngine
    {
        public int CallCount { get; private set; }

        public ProviderDescriptor Descriptor { get; } = new(
            providerId, new Version(1, 0), 1, new HashSet<string> { "ocr.text" }, "MIT", "test", true);

        public ValueTask<OcrAttemptResult> RecognizeAsync(
            OcrInput input,
            OcrOptions options,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var result = text is null
                ? null
                : new OcrResult(text, [new OcrTextRegion(text, null, 0.9)]);
            return ValueTask.FromResult(new OcrAttemptResult(status, result,
                status == OcrProcessingStatus.Completed
                    ? []
                    : [new OcrDiagnostic("StubStatus", status.ToString(), DiagnosticSeverity.Warning)]));
        }
    }
}
