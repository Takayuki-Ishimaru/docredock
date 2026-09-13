using System.Text;
using DocRedock.Core.Reporting;
using DocRedock.Ocr.Tesseract;
using DocRedock.Providers.Abstractions.Providers;

namespace DocRedock.Tests.Ocr;

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
    public void Tsv_parser_joins_adjacent_high_confidence_cjk_words_without_a_space()
    {
        const string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
                           "5\t1\t1\t1\t1\t1\t100\t20\t20\t20\t95.0\t日\n" +
                           "5\t1\t1\t1\t1\t2\t122\t20\t20\t20\t92.0\t本\n" +
                           "5\t1\t1\t1\t1\t3\t144\t20\t20\t20\t90.0\t語\n";

        var result = TsvParser.Parse(tsv);

        Assert.Equal("日本語", result.Text);
        Assert.Equal(3, result.Regions.Count);
        Assert.Equal("日", result.Regions[0].Text);
        Assert.Equal("本", result.Regions[1].Text);
        Assert.Equal("語", result.Regions[2].Text);
    }

    [Fact]
    public void Tsv_parser_keeps_the_space_next_to_a_low_confidence_cjk_word()
    {
        const string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
                           "5\t1\t1\t1\t1\t1\t100\t20\t20\t20\t95.0\t日\n" +
                           "5\t1\t1\t1\t1\t2\t122\t20\t20\t20\t40.0\t本\n" +
                           "5\t1\t1\t1\t1\t3\t144\t20\t20\t20\t90.0\t語\n";

        var result = TsvParser.Parse(tsv);

        Assert.Equal("日 本 語", result.Text);
    }

    [Fact]
    public void Tsv_parser_keeps_the_space_across_a_wide_gap_between_cjk_words()
    {
        const string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
                           "5\t1\t1\t1\t1\t1\t100\t20\t20\t20\t95.0\t日\n" +
                           "5\t1\t1\t1\t1\t2\t160\t20\t20\t20\t92.0\t本\n";

        var result = TsvParser.Parse(tsv);

        Assert.Equal("日 本", result.Text);
    }

    [Fact]
    public void Tsv_parser_keeps_spaces_around_latin_and_digit_tokens_while_merging_adjacent_cjk_words()
    {
        const string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
                           "5\t1\t1\t1\t1\t1\t100\t20\t20\t20\t95.0\t日\n" +
                           "5\t1\t1\t1\t1\t2\t122\t20\t20\t20\t92.0\t本\n" +
                           "5\t1\t1\t1\t1\t3\t144\t20\t20\t20\t90.0\t語\n" +
                           "5\t1\t1\t1\t1\t4\t170\t20\t40\t20\t93.0\tABC\n" +
                           "5\t1\t1\t1\t1\t5\t220\t20\t30\t20\t93.0\t123\n" +
                           "5\t1\t1\t1\t1\t6\t260\t20\t40\t20\t93.0\tです\n";

        var result = TsvParser.Parse(tsv);

        Assert.Equal("日本語 ABC 123 です", result.Text);
    }

    [Fact]
    public void Tsv_parser_leaves_english_lines_unchanged()
    {
        const string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
                           "5\t1\t1\t1\t1\t1\t100\t20\t40\t12\t95.0\tHello\n" +
                           "5\t1\t1\t1\t1\t2\t145\t20\t40\t12\t95.0\tworld\n";

        var result = TsvParser.Parse(tsv);

        Assert.Equal("Hello world", result.Text);
    }

    [Fact]
    public async Task Missing_tesseract_is_unavailable_and_does_not_modify_image_stream()
    {
        var bytes = Encoding.ASCII.GetBytes("not really an image");
        await using var image = new MemoryStream(bytes);
        var engine = new TesseractOcrEngine("docredock-test-tesseract-that-is-not-installed");

        var result = await engine.RecognizeAsync(new OcrInput("img-1", image, "image/png"), new OcrOptions(["jpn", "eng"]), CancellationToken.None);

        Assert.Equal(OcrProcessingStatus.Unavailable, result.Status);
        Assert.Null(result.Result);
        Assert.Equal(0, image.Position);
    }

    [Fact]
    public async Task Pixel_budget_is_reported_as_skipped_without_running_provider()
    {
        await using var image = new MemoryStream([1, 2, 3, 4]);
        var result = await new TesseractOcrEngine(Environment.ProcessPath!).RecognizeAsync(
            new OcrInput("img-budget", image, "image/png"),
            new OcrOptions(["jpn", "eng"], PixelBudget: 1), CancellationToken.None);

        Assert.Equal(OcrProcessingStatus.SkippedByBudget, result.Status);
        Assert.Null(result.Result);
        Assert.Equal(0, image.Position);
    }

    [Fact]
    public void Windows_json_parser_returns_line_regions_in_image_pixels()
    {
        const string json = "[{\"text\":\"Windows OCR\",\"x\":12.5,\"y\":23,\"width\":80,\"height\":16}]";

        var result = WindowsOcrJsonParser.Parse(json);

        Assert.Equal("Windows OCR", result.Text);
        var region = Assert.Single(result.Regions);
        Assert.Equal("image-pixels", region.BoundingBox!.CoordinateSpace);
        Assert.Equal(12.5, region.BoundingBox.X);
        Assert.Equal(23, region.BoundingBox.Y);
        Assert.Null(region.Confidence);
    }

    [Fact]
    public void Windows_json_parser_normalizes_japanese_word_spacing_without_collapsing_english_words()
    {
        const string json = """
            [
              {"text":"領 収 書","x":0,"y":0,"width":1,"height":1},
              {"text":"取 引 日 : 2026 ー 08 ー 23","x":0,"y":0,"width":1,"height":1},
              {"text":"合 計：1 2,800 円","x":0,"y":0,"width":1,"height":1},
              {"text":"サ ー バ ー","x":0,"y":0,"width":1,"height":1},
              {"text":"HELLO WORLD","x":0,"y":0,"width":1,"height":1}
            ]
            """;

        var result = WindowsOcrJsonParser.Parse(json);

        Assert.Equal("領収書\n取引日:2026-08-23\n合計：12,800円\nサーバー\nHELLO WORLD", result.Text);
    }

    [Fact]
    public async Task Windows_ocr_is_unavailable_off_windows_without_modifying_image_stream()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var image = new MemoryStream([1, 2, 3]);

        var result = await new WindowsOcrEngine().RecognizeAsync(
            new OcrInput("img-windows", image, "image/png"), new OcrOptions(["jpn", "eng"]), CancellationToken.None);

        Assert.Equal(OcrProcessingStatus.Unavailable, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "WindowsOcrUnavailable");
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
    public void Fallback_engine_descriptor_reflects_the_selected_providers()
    {
        var primary = new StubOcrEngine("primary", OcrProcessingStatus.Unavailable);
        var fallback = new StubOcrEngine("fallback", OcrProcessingStatus.Completed);

        var engine = new FallbackOcrEngine(primary, fallback);

        Assert.Equal("MIT AND MIT", engine.Descriptor.LicenseExpression);
        Assert.Contains("ocr.text", engine.Descriptor.Capabilities);
    }

    [Fact]
    public async Task Fallback_engine_labels_each_providers_reason_when_both_are_unavailable()
    {
        // AdapterWarningDiagnostics.SummarizeForDisplay (used by the CLI/GUI) groups diagnostics by
        // Code, so two engines reporting the same generic code (as Tesseract and Windows OCR both
        // can for "the executable was not found") must not collapse into a single message that
        // hides one provider's reason from the user.
        var primary = new StubOcrEngine("docredock.ocr.windows-media", OcrProcessingStatus.Unavailable,
            diagnosticCode: "ExecutableUnavailable", diagnosticMessage: "Windows PowerShell executable was not found.");
        var fallback = new StubOcrEngine("docredock.ocr.tesseract", OcrProcessingStatus.Unavailable,
            diagnosticCode: "ExecutableUnavailable", diagnosticMessage: "Tesseract executable 'tesseract' was not found.");
        await using var image = new MemoryStream([1, 2, 3]);

        var result = await new FallbackOcrEngine(primary, fallback).RecognizeAsync(
            new OcrInput("img-1", image, "image/png"), new OcrOptions(["jpn", "eng"]), CancellationToken.None);

        Assert.Equal(OcrProcessingStatus.Unavailable, result.Status);
        Assert.Equal(2, result.Diagnostics.Select(diagnostic => diagnostic.Code).Distinct().Count());
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "PrimaryExecutableUnavailable" &&
            diagnostic.Message.StartsWith("Windows OCR:", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("PowerShell", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "FallbackExecutableUnavailable" &&
            diagnostic.Message.StartsWith("Tesseract:", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("'tesseract'", StringComparison.Ordinal));
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

    private sealed class StubOcrEngine(
        string providerId,
        OcrProcessingStatus status,
        string? text = null,
        string diagnosticCode = "StubStatus",
        string? diagnosticMessage = null) : IOcrEngine
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
                    : [new OcrDiagnostic(diagnosticCode, diagnosticMessage ?? status.ToString(), DiagnosticSeverity.Warning)]));
        }
    }
}
