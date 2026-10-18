using System.IO.Compression;
using DocRedock.Api;
using DocRedock.Core.Reporting;
using DocRedock.Providers.Abstractions.Providers;
using DocRedock.Render;

namespace DocRedock.Tests.Api;

public sealed class ReadableOutputConcurrencyTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-readable-concurrency-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Late_markdown_survives_export_failure_and_cancellation(bool cancel, bool embed)
    {
        var source = await WriteImageWorkbookAsync();
        var markdown = Path.Combine(root, "output.md");
        var gate = new GatedOcrEngine();
        var service = new DocumentService(gate, null, discoverPdfRasterizer: false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var export = service.ExportReadableAsync(new ReadableDocumentExportOptions(
            source, markdown, EnableOcr: true, EmbedImages: embed), cancellation.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(markdown, "another export won");
            if (cancel) cancellation.Cancel();
            gate.Release.TrySetResult();

            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
            else await Assert.ThrowsAsync<IOException>(() => export);

            Assert.Equal("another export won", await File.ReadAllTextAsync(markdown));
            Assert.False(Directory.Exists(Path.Combine(root, "output.assets")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root, ".*.tmp"));
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_exports_keep_the_winner_and_its_images(bool embed)
    {
        var source = await WriteImageWorkbookAsync();
        var markdown = Path.Combine(root, "output.md");
        var gate = new GatedOcrEngine(expectedCalls: 2);
        var service = new DocumentService(gate, null, discoverPdfRasterizer: false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var options = new ReadableDocumentExportOptions(source, markdown, EnableOcr: true, EmbedImages: embed);
        var first = service.ExportReadableAsync(options, cancellation.Token);
        var second = service.ExportReadableAsync(options, cancellation.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            gate.Release.TrySetResult();
            var outcomes = await Task.WhenAll(OutcomeAsync(first), OutcomeAsync(second));

            Assert.Single(outcomes, exception => exception is null);
            Assert.Single(outcomes, exception => exception is IOException);
            var content = await File.ReadAllTextAsync(markdown);
            Assert.Contains("Image workbook", content);
            if (embed) Assert.Contains("data:image/png;base64,", content);
            else
            {
                Assert.Contains("output.assets/", content);
                var image = Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "output.assets")));
                Assert.Equal(PngHeader, await File.ReadAllBytesAsync(image));
            }
            Assert.Empty(Directory.EnumerateFileSystemEntries(root, ".*.tmp"));
        }
        finally { gate.Release.TrySetResult(); }
    }

    private static async Task<Exception?> OutcomeAsync(Task<ReadableDocumentExportResult> export)
    {
        try { await export; return null; }
        catch (IOException exception) { return exception; }
    }

    // This provider stub needs only an image signature, matching existing asset tests.
    private static readonly byte[] PngHeader = [137, 80, 78, 71, 13, 10, 26, 10];

    private async Task<string> WriteImageWorkbookAsync()
    {
        var source = Path.Combine(root, "images.xlsx");
        await new MarkdownRenderer().RenderAsync("# Image workbook", RenderFormat.Xlsx, source);
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
        await using (var media = archive.CreateEntry("xl/media/image1.png").Open())
            await media.WriteAsync(PngHeader);
        return source;
    }

    private sealed class GatedOcrEngine(int expectedCalls = 1) : IOcrEngine
    {
        private int calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderDescriptor Descriptor { get; } = new("test.gated-ocr", new Version(1, 0), 1,
            new HashSet<string> { "ocr" }, "MIT", new string('0', 64), IsBuiltIn: true);

        public async ValueTask<OcrAttemptResult> RecognizeAsync(OcrInput input, OcrOptions options, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) == expectedCalls) Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return new OcrAttemptResult(OcrProcessingStatus.Completed, new OcrResult(string.Empty, []), []);
        }
    }
}
