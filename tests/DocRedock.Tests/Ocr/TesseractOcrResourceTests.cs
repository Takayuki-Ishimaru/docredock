using System.Diagnostics;
using DocRedock.Core.Reporting;
using DocRedock.Ocr.Tesseract;
using DocRedock.Providers.Abstractions.Providers;

namespace DocRedock.Tests.Ocr;

public sealed class TesseractOcrResourceTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Image_budget_bounds_reads_from_nonseekable_inputs(long budget)
    {
        using var image = new CountingInput();
        var result = await new TesseractOcrEngine(Environment.ProcessPath!).RecognizeAsync(
            new OcrInput("budget", image, "image/png"), new OcrOptions(["eng"], PixelBudget: budget), CancellationToken.None);
        Assert.Equal(OcrProcessingStatus.SkippedByBudget, result.Status);
        Assert.Contains(result.Diagnostics, item => item.Code == "PixelBudgetExceeded");
        Assert.InRange(image.BytesRead, 0, Math.Max(0, budget) + 1);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, 4L)]
    [InlineData(false, long.MaxValue)]
    [InlineData(false, long.MaxValue - 1)]
    [InlineData(true, null)]
    [InlineData(true, 4L)]
    public async Task Successful_inputs_preserve_the_callers_stream_position(bool vision, long? budget)
    {
        if (OperatingSystem.IsWindows() || vision && !OperatingSystem.IsMacOS()) return;
        using var executable = new FakeCli(success: true);
        using var image = new MemoryStream([9, 1, 2, 3, 4]);
        image.Position = 1;
        IOcrEngine engine = vision ? new VisionOcrEngine(executable.Path, executable.Path) : new TesseractOcrEngine(executable.Path);
        var result = await engine.RecognizeAsync(new OcrInput("boundary", image, "image/png"),
            new OcrOptions(["eng"], Timeout: TimeSpan.FromSeconds(5), PixelBudget: budget), CancellationToken.None);
        Assert.Equal(OcrProcessingStatus.Completed, result.Status);
        Assert.Equal(1, image.Position);
        Assert.True(image.CanRead);
    }

    [Fact]
    public async Task Over_budget_seekable_input_restores_a_nonzero_position()
    {
        using var image = new MemoryStream([9, 1, 2, 3, 4]);
        image.Position = 1;
        var result = await new TesseractOcrEngine(Environment.ProcessPath!).RecognizeAsync(
            new OcrInput("overflow", image, "image/png"), new OcrOptions(["eng"], PixelBudget: 2), CancellationToken.None);
        Assert.Equal(OcrProcessingStatus.SkippedByBudget, result.Status);
        Assert.Equal(1, image.Position);
        Assert.True(image.CanRead);
    }

    [Fact]
    public async Task A_cancelled_image_read_restores_the_callers_stream_position()
    {
        using var cancellation = new CancellationTokenSource();
        using var image = new InterruptedImage(cancellation);
        image.Position = 1;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TesseractOcrEngine(Environment.ProcessPath!).RecognizeAsync(
            new OcrInput("cancel-read", image, "image/png"), new OcrOptions(["eng"], PixelBudget: 100), cancellation.Token).AsTask());
        Assert.Equal(1, image.Position);
        Assert.True(image.CanRead);
    }

    private sealed class InterruptedImage(CancellationTokenSource cancellation) : MemoryStream([9, 1, 2, 3, 4])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellation.IsCancellationRequested)
                return ValueTask.FromCanceled<int>(cancellationToken);
            buffer.Span[0] = (byte)ReadByte();
            cancellation.Cancel();
            return ValueTask.FromResult(1);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_covers_diagnostic_pipes_after_tesseract_exits(bool vision)
    {
        if (OperatingSystem.IsWindows() || vision && !OperatingSystem.IsMacOS()) return;
        using var executable = new FakeCli();
        using var image = new MemoryStream([1]);
        IOcrEngine engine = vision ? new VisionOcrEngine(executable.Path, executable.Path) : new TesseractOcrEngine(executable.Path);
        var operation = engine.RecognizeAsync(
            new OcrInput("timeout", image, "image/png"),
            new OcrOptions(["eng"], Timeout: TimeSpan.FromMilliseconds(350)), CancellationToken.None).AsTask();
        try
        {
            var result = await operation.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(OcrProcessingStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, item => item.Code == "Timeout");
        }
        finally
        {
            executable.StopChild();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_covers_diagnostic_pipes_after_tesseract_exits(bool vision)
    {
        if (OperatingSystem.IsWindows() || vision && !OperatingSystem.IsMacOS()) return;
        using var executable = new FakeCli();
        using var image = new MemoryStream([1]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        IOcrEngine engine = vision ? new VisionOcrEngine(executable.Path, executable.Path) : new TesseractOcrEngine(executable.Path);
        var operation = engine.RecognizeAsync(
            new OcrInput("cancel", image, "image/png"), new OcrOptions(["eng"], Timeout: TimeSpan.FromSeconds(10)), cancellation.Token).AsTask();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(4)));
        }
        finally
        {
            executable.StopChild();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    private sealed class CountingInput : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, 1_048_576 - BytesRead);
            Array.Clear(buffer, offset, read);
            BytesRead += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, 1_048_576 - BytesRead);
            buffer.Span[..read].Clear();
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeCli : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "docredock-ocr-resource-test-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public FakeCli(bool success = false)
        {
            Directory.CreateDirectory(root);
            Path = System.IO.Path.Combine(root, "tesseract");
            File.WriteAllText(Path, success
                ? "#!/bin/sh\nprintf '' > \"$2.tsv\"\nprintf '[]'\n"
                : "#!/bin/sh\nsleep 30 &\nprintf '%s' \"$!\" > \"$0.pid\"\nexit 0\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public void StopChild()
        {
            if (!File.Exists(Path + ".pid") || !int.TryParse(File.ReadAllText(Path + ".pid"), out var pid)) return;
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        public void Dispose()
        {
            StopChild();
            Directory.Delete(root, recursive: true);
        }
    }
}
