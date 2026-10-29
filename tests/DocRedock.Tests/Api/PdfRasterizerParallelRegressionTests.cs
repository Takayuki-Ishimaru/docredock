using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocRedock.Api;
using DocRedock.Providers.Abstractions.Providers;

namespace DocRedock.Tests.Api;

/// <summary>Bounded page parallelism must keep ordering, budgets, cleanup and child-process lifetime intact.</summary>
public sealed class PdfRasterizerParallelRegressionTests
{
    [Fact]
    public async Task Six_pages_overlap_within_the_process_bound_and_finish_without_hanging()
    {
        using var fixture = new FakeRasterizer("valid");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var pages = await fixture.Provider.RasterizeAsync(fixture.Input, [6, 5, 4, 3, 2, 1], new(), deadline.Token);

        Assert.Equal([1, 2, 3, 4, 5, 6], pages.Select(page => page.PageNumber));
        Assert.All(pages, page => Assert.True(page.Content.Length >= 45));
        var calls = fixture.Calls();
        var starts = calls.Where(call => call.GetProperty("event").GetString() == "start").ToArray();
        Assert.Equal(6, starts.Length);
        Assert.Equal([1, 2, 3, 4, 5, 6], starts.Select(call => call.GetProperty("page").GetInt32()).Order());
        Assert.InRange(fixture.PeakConcurrency, 2, 3);
        Assert.All(calls, call => Assert.False(Directory.Exists(call.GetProperty("output_directory").GetString())));
    }

    [Fact]
    public async Task Duplicate_and_unordered_pages_run_once_each_in_ascending_order()
    {
        using var fixture = new FakeRasterizer("valid");
        var pages = await fixture.Provider.RasterizeAsync(fixture.Input, [7, 3, 7, 3, 5, 5], new());
        Assert.Equal([3, 5, 7], pages.Select(page => page.PageNumber));
        Assert.Equal([3, 5, 7], fixture.Calls().Where(call => call.GetProperty("event").GetString() == "start").Select(call => call.GetProperty("page").GetInt32()).Order());
    }

    [Fact]
    public async Task Aggregate_pixel_budget_fails_at_the_first_page_over_the_shared_total()
    {
        using var fixture = new FakeRasterizer("valid");
        // Every page is 2x3 pixels, so the shared total is exceeded by the fourth page only.
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Provider.RasterizeAsync(
            fixture.Input, [1, 2, 3, 4, 5, 6], new(MaxTotalPixels: 20)));
        await fixture.WaitForCleanup();
        Assert.All(fixture.Calls(), call => Assert.False(IsRunning(call.GetProperty("pid").GetInt32())));
    }

    [Fact]
    public async Task Page_pixel_budget_rejects_before_the_whole_image_is_allocated()
    {
        using var fixture = new FakeRasterizer("oversized");
        // The header declares 100000x100000 while the file stays small: an implementation that
        // allocated from the pixel count instead of the length would fail here, not pass.
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Provider.RasterizeAsync(
            fixture.Input, [1, 2, 3, 4], new(MaxPixelsPerPage: 100)));
        await fixture.WaitForCleanup();
    }

    [Fact]
    public async Task Truncated_image_is_rejected_by_the_byte_floor_while_siblings_run()
    {
        using var fixture = new FakeRasterizer("truncated");
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Provider.RasterizeAsync(
            fixture.Input, [1, 2, 3, 4, 5, 6], new()));
        await fixture.WaitForCleanup();
        Assert.All(fixture.Calls(), call => Assert.False(Directory.Exists(call.GetProperty("output_directory").GetString())));
    }

    [Fact]
    public async Task Caller_cancellation_while_several_children_run_leaves_no_process_or_directory()
    {
        using var fixture = new FakeRasterizer("wait");
        using var cancellation = new CancellationTokenSource();
        var run = fixture.Provider.RasterizeAsync(fixture.Input, [1, 2, 3, 4, 5, 6], new(), cancellation.Token).AsTask();
        await fixture.WaitForCalls(3);
        var pids = fixture.Calls().Select(call => call.GetProperty("pid").GetInt32()).ToArray();
        Assert.True(pids.Length >= 3);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.All(pids, pid => Assert.False(IsRunning(pid)));
        await fixture.WaitForCleanup();
    }

    [Fact]
    public async Task Timeout_stops_every_active_child_and_cleans_the_private_directory()
    {
        using var fixture = new FakeRasterizer("wait");
        await Assert.ThrowsAsync<TimeoutException>(async () => await fixture.Provider.RasterizeAsync(
            fixture.Input, [1, 2, 3, 4, 5, 6], new(Timeout: TimeSpan.FromMilliseconds(800))));
        await fixture.WaitForCleanup();
        Assert.All(fixture.Calls(), call => Assert.False(IsRunning(call.GetProperty("pid").GetInt32())));
    }

    [Fact]
    public async Task One_failing_page_stops_and_drains_the_other_active_children()
    {
        using var fixture = new FakeRasterizer("mixed");
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Provider.RasterizeAsync(
            fixture.Input, [1, 2, 3, 4, 5, 6], new()));
        await fixture.WaitForCleanup();
        Assert.Contains(fixture.Calls(), call => call.GetProperty("page").GetInt32() == 4);
        Assert.All(fixture.Calls(), call => Assert.False(IsRunning(call.GetProperty("pid").GetInt32())));
    }

    [Fact]
    public async Task Slow_page_work_overlaps_instead_of_waiting_for_each_previous_page()
    {
        using var fixture = new FakeRasterizer("slow");
        var pages = await fixture.Provider.RasterizeAsync(fixture.Input, [1, 2, 3, 4, 5, 6], new());
        Assert.Equal(6, pages.Count);
        Assert.InRange(fixture.PeakConcurrency, 2, 3);
        // Compare measured child lifetimes instead of an absolute wall-clock limit:
        // unrelated tests may occupy the machine during this test.
        var spans = fixture.Calls().GroupBy(call => call.GetProperty("page").GetInt32())
            .Select(group => (Start: group.Min(call => call.GetProperty("t").GetDouble()),
                End: group.Max(call => call.GetProperty("t").GetDouble()))).ToArray();
        var sequentialWork = spans.Sum(span => span.End - span.Start);
        var overlappingWork = spans.Max(span => span.End) - spans.Min(span => span.Start);
        Assert.True(sequentialWork > overlappingWork * 1.2,
            $"process lifetimes did not overlap enough: sum={sequentialWork.ToString(CultureInfo.InvariantCulture)}, span={overlappingWork.ToString(CultureInfo.InvariantCulture)}");
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    /// <summary>Counts overlapping fake processes and simultaneously present image files.</summary>
    private sealed class FakeRasterizer : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(),
            "docredock-parallel-test-" + Guid.NewGuid().ToString("N"));
        private readonly string log;
        private readonly string live;

        public FakeRasterizer(string mode)
        {
            Directory.CreateDirectory(root);
            log = Path.Combine(root, "calls.jsonl");
            live = Path.Combine(root, "live.jsonl");
            Input = Path.Combine(root, "input with spaces and $literal.pdf");
            File.WriteAllText(Input, "%PDF-1.4\n%%EOF");
            var script = Path.Combine(root, "provider.py");
            File.WriteAllText(script, FakeScript);
            Provider = new PythonRasterizer(FindPython(), script, log, live, mode);
        }

        public string Input { get; }
        public PdftoppmPdfRasterizer Provider { get; }

        /// <summary>Highest number of fake processes that were running at the same time.</summary>
        public int PeakConcurrency => Peak(Complete(log).Select(line => JsonSerializer.Deserialize<Line>(line)!));

        /// <summary>Highest number of produced images that existed at the same time.</summary>
        public int PeakLiveImages => Peak(Complete(live).Select(line => JsonSerializer.Deserialize<Line>(line)!));

        public JsonElement[] Calls() => Complete(log).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();

        public async Task WaitForCalls(int count)
        {
            for (var attempt = 0; attempt < 500; attempt++)
            {
                if (Calls().Length >= count) return;
                await Task.Delay(20);
            }
            throw new TimeoutException($"Fake provider did not report {count} calls.");
        }

        /// <summary>Waits until every private output directory the children recorded is gone.</summary>
        public async Task WaitForCleanup()
        {
            for (var attempt = 0; attempt < 500; attempt++)
            {
                if (Calls().All(call => !Directory.Exists(call.GetProperty("output_directory").GetString()))) return;
                await Task.Delay(20);
            }
            throw new TimeoutException("Rasterizer left a private output directory behind.");
        }

        public void Dispose() => Directory.Delete(root, true);

        private static IEnumerable<string> Complete(string path)
        {
            if (!File.Exists(path)) return [];
            // A child may still hold its append handle; share with the writer and keep only
            // newline-terminated records so a half-written line is never parsed.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n').SkipLast(1).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        }

        private static int Peak(IEnumerable<Line> lines)
        {
            var events = lines.Select(line => (At: line.T, Delta: line.Event == "start" ? 1 : -1))
                // Ends are applied before starts at the same instant so the peak is never inflated.
                .OrderBy(e => e.At).ThenBy(e => e.Delta).ToArray();
            var running = 0;
            var peak = 0;
            foreach (var item in events)
            {
                running += item.Delta;
                peak = Math.Max(peak, running);
            }
            return peak;
        }

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

        private sealed class Line
        {
            [JsonPropertyName("event")] public string Event { get; set; } = "";
            [JsonPropertyName("page")] public int Page { get; set; }
            [JsonPropertyName("t")] public double T { get; set; }
            [JsonPropertyName("pid")] public int Pid { get; set; }
            [JsonPropertyName("output_directory")] public string? OutputDirectory { get; set; }
        }

        private const string FakeScript = """
            import json, os, pathlib, struct, sys, time, zlib
            log, live, mode = sys.argv[1:4]
            args = sys.argv[4:]
            output = args[-1]
            page = int(args[args.index("-f") + 1])
            image = output + ".png"

            def record(path, event):
                with open(path, "a", encoding="utf-8") as stream:
                    stream.write(json.dumps({"event": event, "page": page, "pid": os.getpid(), "t": time.monotonic(),
                                             "output_directory": str(pathlib.Path(output).parent)}) + "\n")

            def chunk(kind, data):
                return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

            def write_png(width, height):
                body = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
                        + chunk(b"IDAT", zlib.compress((b"\x00" + b"\x00" * (width * 3)) * min(height, 3))) + chunk(b"IEND", b""))
                pathlib.Path(image).write_bytes(body)

            record(log, "start")
            written = False
            try:
                if mode == "wait":
                    time.sleep(60)
                elif mode == "mixed" and page == 4:
                    sys.exit(9)
                elif mode == "slow":
                    time.sleep(0.4)
                    write_png(2, 3)
                elif mode == "oversized":
                    write_png(100000, 100000)
                elif mode == "truncated":
                    pathlib.Path(image).write_bytes(b"\x89PNG\r\n\x1a\n\x00\x00")
                else:
                    time.sleep(0.15)
                    write_png(2, 3)
                    sys.stdout.write("o" * 100000)
                    sys.stderr.write("e" * 100000)
                written = os.path.exists(image)
                if written:
                    record(live, "start")
            finally:
                if written:
                    record(live, "end")
                record(log, "end")
            """;
    }

    private sealed class PythonRasterizer(string python, string script, string log, string live, string mode)
        : PdftoppmPdfRasterizer(python)
    {
        protected override void ConfigureArguments(ProcessStartInfo start, string pdf, string output,
            IReadOnlyList<int> pages, PdfRasterizationOptions options)
        {
            start.ArgumentList.Add(script);
            start.ArgumentList.Add(log);
            start.ArgumentList.Add(live);
            start.ArgumentList.Add(mode);
            base.ConfigureArguments(start, pdf, output, pages, options);
        }
    }
}

