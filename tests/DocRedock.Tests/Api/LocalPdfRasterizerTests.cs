using System.Diagnostics;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Providers.Abstractions.Providers;

namespace DocRedock.Tests.Api;

public sealed class LocalPdfRasterizerTests
{
    [Fact]
    public async Task Sparse_pages_spaces_and_verbose_process_preserve_real_dimensions_and_cleanup()
    {
        using var fixture = new FakeRasterizer("valid");
        var pages = await fixture.Provider.RasterizeAsync(fixture.Input, [1, 9], new());
        Assert.Equal([1, 9], pages.Select(page => page.PageNumber));
        Assert.All(pages, page => { Assert.Equal(2, page.PixelWidth); Assert.Equal(3, page.PixelHeight); });
        var calls = fixture.Calls();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, call => Assert.Equal(call.GetProperty("first").GetString(), call.GetProperty("last").GetString()));
        Assert.All(calls, call => Assert.False(Directory.Exists(call.GetProperty("output_directory").GetString())));
    }

    [Fact]
    public async Task Oversized_png_header_is_rejected_by_pixel_budget()
    {
        using var fixture = new FakeRasterizer("oversized");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Provider.RasterizeAsync(fixture.Input, [1], new(MaxPixelsPerPage: 100)));
        Assert.All(fixture.Calls(), call => Assert.False(Directory.Exists(call.GetProperty("output_directory").GetString())));
    }

    [Theory]
    [InlineData("bad-exit")]
    [InlineData("bad-png")]
    public async Task Unusable_provider_output_is_rejected(string mode)
    {
        using var fixture = new FakeRasterizer(mode);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Provider.RasterizeAsync(fixture.Input, [1], new()));
    }

    [Fact]
    public async Task Caller_cancellation_stops_child_and_cleans_private_directory()
    {
        using var fixture = new FakeRasterizer("wait");
        using var cancellation = new CancellationTokenSource();
        var run = fixture.Provider.RasterizeAsync(fixture.Input, [1], new(), cancellation.Token).AsTask();
        await fixture.WaitForCall();
        var pid = fixture.Calls()[0].GetProperty("pid").GetInt32();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(IsRunning(pid));
        Assert.All(fixture.Calls(), call => Assert.False(Directory.Exists(call.GetProperty("output_directory").GetString())));
    }

    [Fact]
    public async Task Timeout_stops_child_and_cleans_private_directory()
    {
        using var fixture = new FakeRasterizer("wait");
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await fixture.Provider.RasterizeAsync(fixture.Input, [1], new(Timeout: TimeSpan.FromMilliseconds(500))));
        Assert.All(fixture.Calls(), call => Assert.False(Directory.Exists(call.GetProperty("output_directory").GetString())));
    }

    [Fact]
    public async Task Empty_page_request_does_not_start_a_process()
    {
        using var fixture = new FakeRasterizer("valid");
        Assert.Empty(await fixture.Provider.RasterizeAsync(fixture.Input, [], new()));
        Assert.Empty(fixture.Calls());
    }

    [Fact]
    public void Invalid_explicit_path_never_silently_selects_a_different_provider()
    {
        Assert.Null(PdfRasterizerFactory.Discover(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".missing")));
        Assert.Equal("unavailable", PdfRasterizerFactory.Describe(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".missing")).Status);
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private sealed class FakeRasterizer : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "docredock-process-test-" + Guid.NewGuid().ToString("N"));
        private readonly string log;
        public string Input { get; }
        public PdftoppmPdfRasterizer Provider { get; }

        public FakeRasterizer(string mode)
        {
            Directory.CreateDirectory(root);
            log = Path.Combine(root, "calls.jsonl");
            Input = Path.Combine(root, "input with spaces and $literal.pdf");
            File.WriteAllText(Input, "%PDF-1.4\n%%EOF");
            var script = Path.Combine(root, "provider.py");
            File.WriteAllText(script, """
                import json, os, pathlib, struct, sys, time, zlib
                log, mode = sys.argv[1:3]
                args = sys.argv[3:]
                output = args[-1]
                first, last = args[args.index("-f")+1], args[args.index("-l")+1]
                with open(log, "a", encoding="utf-8") as stream:
                    stream.write(json.dumps({"pid":os.getpid(),"first":first,"last":last,"output_directory":str(pathlib.Path(output).parent)})+"\n")
                if mode == "wait":
                    time.sleep(60)
                if mode == "bad-exit":
                    sys.exit(7)
                width, height = (100000,100000) if mode == "oversized" else (2,3)
                def chunk(kind, data):
                    return struct.pack(">I",len(data))+kind+data+struct.pack(">I",zlib.crc32(kind+data))
                data = b"\x89PNG\r\n\x1a\n"+chunk(b"IHDR",struct.pack(">IIBBBBB",width,height,8,2,0,0,0))+chunk(b"IDAT",zlib.compress(b"\x00"+b"\x00"*6)*3)+chunk(b"IEND",b"")
                if mode == "bad-png":
                    data = b"not a png"
                path = output+".png" if "-singlefile" in args else output+"-"+first.zfill(6)+".png"
                pathlib.Path(path).write_bytes(data)
                if mode == "valid":
                    sys.stdout.write("o"*100000)
                    sys.stderr.write("e"*100000)
                """);
            Provider = new PythonRasterizer(FindPython(), script, log, mode);
        }

        public JsonElement[] Calls() => File.Exists(log) ? File.ReadAllLines(log).Where(line => line.Length > 0)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray() : [];

        public async Task WaitForCall()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (Calls().Length > 0) return;
                await Task.Delay(20);
            }
            throw new TimeoutException("Fake provider did not start.");
        }

        public void Dispose() => Directory.Delete(root, true);

        private static string FindPython()
        {
            var names = OperatingSystem.IsWindows() ? new[] { "python.exe", "python3.exe" } : new[] { "python3" };
            foreach (var name in names)
                foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                {
                    var candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            throw new InvalidOperationException("Python 3 is required for the cross-platform external-process tests.");
        }
    }

    private sealed class PythonRasterizer(string python, string script, string log, string mode) : PdftoppmPdfRasterizer(python)
    {
        protected override void ConfigureArguments(ProcessStartInfo start, string pdf, string output,
            IReadOnlyList<int> pages, PdfRasterizationOptions options)
        {
            start.ArgumentList.Add(script);
            start.ArgumentList.Add(log);
            start.ArgumentList.Add(mode);
            base.ConfigureArguments(start, pdf, output, pages, options);
        }
    }
}
