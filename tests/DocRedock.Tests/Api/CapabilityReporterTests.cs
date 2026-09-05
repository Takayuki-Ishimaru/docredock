using DocRedock.Api;
using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;

namespace DocRedock.Tests.Api;

public sealed class CapabilityReporterTests
{
    private static readonly CapabilityStatus Rasterizer = new("pdf-rasterizer", "ready", "pdftoppm", "/tools/pdftoppm");

    [Fact]
    public async Task Missing_tesseract_never_claims_engine_or_languages_ready()
    {
        var report = await new CapabilityReporter(_ => null).ReportAsync(Rasterizer);

        Assert.All(report.Where(item => item.Id is "ocr-engine" or "ocr-jpn" or "ocr-eng"),
            item => Assert.Equal("unavailable", item.Status));
    }

    [Fact]
    public async Task Listed_languages_are_the_only_languages_marked_ready()
    {
        var reporter = new CapabilityReporter(
            name => name == "tesseract" ? "/tools/tesseract" : null,
            (_, _, _) => Task.FromResult(new CapabilityProbeResult(true, "List of available languages in /data (2):\neng\nosd\n")));

        var report = await reporter.ReportAsync(Rasterizer);

        Assert.Equal("ready", report.Single(item => item.Id == "ocr-engine").Status);
        Assert.Equal("ready", report.Single(item => item.Id == "ocr-eng").Status);
        Assert.Equal("unavailable", report.Single(item => item.Id == "ocr-jpn").Status);
    }

    [Fact]
    public async Task Failed_language_probe_is_partial_not_ready()
    {
        var reporter = new CapabilityReporter(
            name => name == "tesseract" ? "/tools/tesseract" : null,
            (_, _, _) => Task.FromResult(new CapabilityProbeResult(false, string.Empty)));

        var report = await reporter.ReportAsync(Rasterizer);

        Assert.All(report.Where(item => item.Id is "ocr-engine" or "ocr-jpn" or "ocr-eng"),
            item => Assert.Equal("partial", item.Status));
    }

    [Fact]
    public void Export_summary_counts_tables_and_actual_fallback_pages_not_diagnostic_words()
    {
        var resolved = new VisualGraph("resolved", [new VisualNode("a", "A"), new VisualNode("b", "B")],
            [new VisualEdge("edge", "a", "b")]);
        var fallbackOnly = new VisualGraph("fallback", [], [], Paths: [new VisualPath("noisy-stroke", IsFallback: true)]);
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc", DocumentFormatKind.Pdf,
        [new DocumentPartition("p", 0,
        [new DocumentNode("table", NodeKind.Table, null, 0, ContentLayer.Body, new TableNodeContent([])),
         VisualNode("diagram", resolved), VisualNode("fallback", fallbackOnly)])]);

        var summary = ExportSummaryBuilder.Build(graph,
            [new Diagnostic("UnrelatedFallbackMessage", "a fallback word is not a page", DiagnosticSeverity.Warning)]);

        Assert.Equal(1, summary.Tables);
        Assert.Equal(1, summary.Diagrams);
        Assert.Equal(1, summary.FallbackPages);
        Assert.Equal(1, summary.Warnings);
    }

    [Fact]
    public void Export_summary_counts_multiple_fallback_graphs_on_one_page_once()
    {
        var fallback = new VisualGraph("fallback", [], [], Paths: [new VisualPath("noisy-stroke", IsFallback: true)]);
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc", DocumentFormatKind.Pdf,
        [new DocumentPartition("page-1", 0, [VisualNode("a", fallback), VisualNode("b", fallback)]),
         new DocumentPartition("page-2", 1, [VisualNode("c", fallback)])]);

        var summary = ExportSummaryBuilder.Build(graph, []);

        Assert.Equal(2, summary.FallbackPages);
    }

    [Fact]
    public async Task Bounded_probe_rejects_oversized_output_without_retaining_it()
    {
        if (OperatingSystem.IsWindows()) return;
        using var script = new ScriptFixture("head -c 1100000 /dev/zero");

        var result = await CapabilityReporter.RunBoundedAsync(script.Path, [], CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(result.StandardOutput);
    }

    [Fact]
    public async Task Timed_out_probe_kills_its_process_tree()
    {
        if (OperatingSystem.IsWindows()) return;
        using var script = new ScriptFixture("sleep 30 & child=$!; echo $child > \"$1\"; wait $child", acceptsArgument: true);
        var childFile = System.IO.Path.Combine(script.Root, "child.pid");
        var probe = CapabilityReporter.RunBoundedAsync(script.Path, [childFile], CancellationToken.None);
        await Task.Delay(100);

        var result = await probe;

        Assert.False(result.Succeeded);
        if (File.Exists(childFile) && int.TryParse(await File.ReadAllTextAsync(childFile), out var child))
            Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(child));
    }

    private static DocumentNode VisualNode(string id, VisualGraph visual) => new(id, NodeKind.Image, null, 0,
        ContentLayer.Body, new EmptyNodeContent(), Extensions: new Dictionary<string, JsonElement>
        {
            ["visual_graph"] = JsonSerializer.SerializeToElement(visual),
        });

    private sealed class ScriptFixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "docredock-capability-tests", Guid.NewGuid().ToString("N"));
        public string Path { get; }

        public ScriptFixture(string command, bool acceptsArgument = false)
        {
            Directory.CreateDirectory(Root);
            Path = System.IO.Path.Combine(Root, "probe.sh");
            File.WriteAllText(Path, "#!/bin/sh\n" + command + "\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
