using DocRedock.Core.Reporting;
using DocRedock.Ocr.Tesseract;
using DocRedock.Providers.Abstractions.Providers;

namespace DocRedock.Tests.Ocr;

/// <summary>Runs the engine against a stand-in for <c>swift</c> that fails the way the interpreter
/// does, so the warning a person reads is checked without depending on Vision itself.</summary>
public sealed class VisionOcrEngineTests
{
    private const string CrashOutput = """
        Swift/ErrorType.swift:254: Fatal error: Error raised at top level: Error Domain=com.apple.Vision Code=13 "The image is too small in at least one dimension 2 x 2"
        Stack dump:
        0.	Program arguments: /usr/bin/swift-frontend -frontend -interpret /tmp/vision-ocr.swift -- /tmp/input.png jpn eng
        1.	Apple Swift version 6.4
        0  swift-frontend           0x0000000106913bd0 llvm::sys::PrintStackTrace(llvm::raw_ostream&, int) + 56
        1  swift-frontend           0x0000000106911358 llvm::sys::RunSignalHandlers() + 172
        """;

    [Theory]
    [InlineData(CrashOutput, "Swift/ErrorType.swift:254: Fatal error: Error raised at top level: Error Domain=com.apple.Vision Code=13 \"The image is too small in at least one dimension 2 x 2\"")]
    [InlineData("text recognition failed: The image is too small\n", "text recognition failed: The image is too small")]
    [InlineData("image could not be opened\nfrom the helper\n", "image could not be opened from the helper")]
    [InlineData("", "Vision OCR exited with code 134.")]
    [InlineData("Stack dump:\n0  swift-frontend 0x1\n", "Vision OCR exited with code 134.")]
    public async Task Helper_failures_are_reported_without_the_interpreter_stack_dump(string stderr, string expected)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = Path.Combine(Path.GetTempPath(), "docredock-vision-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "stderr.txt"), stderr);
            var swift = Path.Combine(root, "swift");
            await File.WriteAllTextAsync(swift, "#!/bin/sh\ncat \"$(dirname \"$0\")/stderr.txt\" >&2\nexit 134\n");
            File.SetUnixFileMode(swift, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var helper = Path.Combine(root, "vision-ocr.swift");
            await File.WriteAllTextAsync(helper, "// stand-in\n");
            await using var image = new MemoryStream([1, 2, 3]);

            var result = await new VisionOcrEngine(helper, swift).RecognizeAsync(
                new OcrInput("img-1", image, "image/png"), new OcrOptions(["jpn", "eng"]), CancellationToken.None);

            Assert.Equal(OcrProcessingStatus.Failed, result.Status);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(("ProcessFailed", DiagnosticSeverity.Warning), (diagnostic.Code, diagnostic.Severity));
            Assert.Equal(expected, diagnostic.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_long_helper_message_is_kept_to_one_short_line()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = Path.Combine(Path.GetTempPath(), "docredock-vision-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var swift = Path.Combine(root, "swift");
            await File.WriteAllTextAsync(swift, "#!/bin/sh\nprintf 'x%.0s' $(seq 1 2000) >&2\nexit 5\n");
            File.SetUnixFileMode(swift, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var helper = Path.Combine(root, "vision-ocr.swift");
            await File.WriteAllTextAsync(helper, "// stand-in\n");
            await using var image = new MemoryStream([1, 2, 3]);

            var result = await new VisionOcrEngine(helper, swift).RecognizeAsync(
                new OcrInput("img-1", image, "image/png"), new OcrOptions(["jpn", "eng"]), CancellationToken.None);

            Assert.Equal(new string('x', 500) + "...", Assert.Single(result.Diagnostics).Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
