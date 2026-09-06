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
    public async Task Tesseract_ready_marks_native_ocr_gap_as_satisfied_not_broken()
    {
        var reporter = new CapabilityReporter(
            name => name == "tesseract" ? "/tools/tesseract" : null,
            (_, _, _) => Task.FromResult(new CapabilityProbeResult(true, "List of available languages in /data (2):\neng\njpn\n")),
            nativeOcr: () => new CapabilityStatus("ocr-native", "unavailable", "system",
                Action: "No native OCR provider is bundled for this platform; install Tesseract."));

        var report = await reporter.ReportAsync(Rasterizer);

        var native = report.Single(item => item.Id == "ocr-native");
        Assert.Equal("unavailable", native.Status);
        Assert.Equal("tesseract", native.SatisfiedBy);
        Assert.DoesNotContain("install tesseract", native.Action, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_tesseract_leaves_native_ocr_gap_unsatisfied()
    {
        var reporter = new CapabilityReporter(
            _ => null,
            nativeOcr: () => new CapabilityStatus("ocr-native", "unavailable", "system",
                Action: "No native OCR provider is bundled for this platform; install Tesseract."));

        var report = await reporter.ReportAsync(Rasterizer);

        var native = report.Single(item => item.Id == "ocr-native");
        Assert.Null(native.SatisfiedBy);
        Assert.Contains("install tesseract", native.Action, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ready_native_ocr_satisfies_missing_tesseract_but_partial_native_does_not()
    {
        var readyNativeReport = await new CapabilityReporter(_ => null,
            nativeOcr: () => new CapabilityStatus("ocr-native", "ready", "apple-vision")).ReportAsync(Rasterizer);
        Assert.Equal("apple-vision", readyNativeReport.Single(item => item.Id == "ocr-engine").SatisfiedBy);

        var partialNativeReport = await new CapabilityReporter(_ => null,
            nativeOcr: () => new CapabilityStatus("ocr-native", "partial", "apple-vision")).ReportAsync(Rasterizer);
        Assert.Null(partialNativeReport.Single(item => item.Id == "ocr-engine").SatisfiedBy);
    }

    [Fact]
    public async Task Every_capability_carries_an_explicit_tier()
    {
        var report = await new CapabilityReporter(_ => null).ReportAsync(Rasterizer);

        Assert.All(report, item => Assert.True(item.Tier is "required" or "optional"));
        var requiredIds = new HashSet<string> { "docx-readable", "xlsx-readable", "pptx-readable", "pdf-text" };
        Assert.All(report.Where(item => requiredIds.Contains(item.Id)), item => Assert.Equal("required", item.Tier));
        Assert.All(report.Where(item => !requiredIds.Contains(item.Id)), item => Assert.Equal("optional", item.Tier));
    }

    [Fact]
    public void Export_summary_counts_tables_and_actual_fallback_pages_not_diagnostic_words()
    {
        var resolved = new VisualGraph("resolved", [new VisualNode("a", "A"), new VisualNode("b", "B")],
            [new VisualEdge("edge", "a", "b")]);
        // Legacy graph (no SourceItems): the IsFallback rule applies directly.
        var fallbackOnly = new VisualGraph("fallback", [], [], Paths: [new VisualPath("noisy-stroke", IsFallback: true)]);
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc", DocumentFormatKind.Pdf,
        [new DocumentPartition("p", 0,
        [new DocumentNode("table", NodeKind.Table, null, 0, ContentLayer.Body, new TableNodeContent([])),
         VisualNode("diagram", resolved), VisualNode("fallback", fallbackOnly)])]);

        var summary = ExportSummaryBuilder.Build(graph,
            [new Diagnostic("UnrelatedFallbackMessage", "a fallback word is not a page", DiagnosticSeverity.Warning)]);

        Assert.Equal(1, summary.Tables);
        Assert.Equal(1, summary.DiagramsReconstructed);
        Assert.Equal(1, summary.VectorPages);
        Assert.Equal(1, summary.FallbackPages);
        Assert.Equal(1, summary.FallbackPaths);
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
        Assert.Equal(2, summary.VectorPages);
    }

    [Fact]
    public void Export_summary_does_not_count_a_resolved_connectors_own_fallback_stroke_as_fallback()
    {
        // A resolved connector's own raw open stroke is recorded IsFallback=true even though the
        // source-item ledger correctly marks it ProjectedEdge, not VisualFallback (F-05). The
        // finalized summary must follow the ledger, not the raw path flag, whenever one is present.
        var resolvedWithFallbackStroke = new VisualGraph("resolved", [new VisualNode("a", "A"), new VisualNode("b", "B")],
            [new VisualEdge("edge", "a", "b")],
            Paths: [new VisualPath("shaft", IsFallback: true)],
            SourceItems:
            [
                new VisualSourceItem("shape-a", VisualSourceItemKind.Shape, VisualDisposition.ProjectedNode, ProjectedNodeId: "a"),
                new VisualSourceItem("shape-b", VisualSourceItemKind.Shape, VisualDisposition.ProjectedNode, ProjectedNodeId: "b"),
                new VisualSourceItem("shaft", VisualSourceItemKind.Connector, VisualDisposition.ProjectedEdge, ProjectedEdgeId: "edge"),
            ]);
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc", DocumentFormatKind.Pdf,
        [new DocumentPartition("p", 0, [VisualNode("diagram", resolvedWithFallbackStroke)])]);

        var summary = ExportSummaryBuilder.Build(graph, []);

        Assert.Equal(0, summary.FallbackPaths);
        Assert.Equal(0, summary.FallbackPages);
        Assert.Equal(1, summary.DiagramsReconstructed);
        Assert.Equal(1, summary.VectorPages);
    }

    [Fact]
    public void Export_summary_counts_a_table_only_graph_as_a_vector_page_without_a_reconstructed_diagram()
    {
        // A table-only page still carries a visual graph (it has vector content) but reconstructs
        // no semantic diagram, so diagrams= and "Diagrams reconstructed:" must both read 0 while
        // vector_pages= still reflects the vector content that was present (F-05 case 1).
        var tableGrid = new VisualGraph("grid", [], []);
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc", DocumentFormatKind.Pdf,
        [new DocumentPartition("p", 0,
        [new DocumentNode("table", NodeKind.Table, null, 0, ContentLayer.Body, new TableNodeContent([])),
         VisualNode("diagram", tableGrid)])]);

        var summary = ExportSummaryBuilder.Build(graph, []);

        Assert.Equal(1, summary.Tables);
        Assert.Equal(0, summary.DiagramsReconstructed);
        Assert.Equal(1, summary.VectorPages);
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

public sealed class CapabilityExitPolicyTests
{
    private static CapabilityStatus Required(string id, string status) => new(id, status, Tier: "required");

    private static CapabilityStatus Optional(string id, string status, string? provider = null, string? satisfiedBy = null) =>
        new(id, status, provider, Tier: "optional", SatisfiedBy: satisfiedBy);

    [Fact]
    public void All_required_ready_with_optional_gaps_exits_zero_by_default_and_one_under_strict()
    {
        var capabilities = new[] { Required("docx-readable", "ready"), Optional("mermaid-render", "unavailable") };

        var defaultVerdict = CapabilityExitPolicy.Evaluate(capabilities, strict: false);
        var strictVerdict = CapabilityExitPolicy.Evaluate(capabilities, strict: true);

        Assert.Equal(0, defaultVerdict.ExitCode);
        Assert.True(defaultVerdict.RequiredReady);
        Assert.Contains("mermaid-render", defaultVerdict.OptionalGaps);
        Assert.Equal(1, strictVerdict.ExitCode);
        Assert.Contains("mermaid-render", strictVerdict.StrictFailures);
    }

    [Fact]
    public void Optional_gap_satisfied_by_a_ready_alternative_never_fails_even_under_strict()
    {
        var capabilities = new[]
        {
            Required("docx-readable", "ready"),
            Optional("ocr-engine", "ready", provider: "tesseract"),
            Optional("ocr-native", "unavailable", provider: "system", satisfiedBy: "tesseract"),
        };

        var verdict = CapabilityExitPolicy.Evaluate(capabilities, strict: true);

        Assert.Equal(0, verdict.ExitCode);
        Assert.Contains("ocr-native", verdict.OptionalGaps);
        Assert.DoesNotContain("ocr-native", verdict.StrictFailures);
    }

    [Fact]
    public void Required_item_not_ready_fails_in_both_default_and_strict_mode()
    {
        var capabilities = new[] { Required("docx-readable", "unavailable") };

        Assert.Equal(1, CapabilityExitPolicy.Evaluate(capabilities, strict: false).ExitCode);
        Assert.Equal(1, CapabilityExitPolicy.Evaluate(capabilities, strict: true).ExitCode);
    }

    [Fact]
    public void A_partial_alternative_does_not_satisfy_an_optional_gap_under_strict()
    {
        var capabilities = new[]
        {
            Required("docx-readable", "ready"),
            Optional("ocr-engine", "partial", provider: "tesseract"),
            Optional("ocr-native", "unavailable", provider: "system", satisfiedBy: "tesseract"),
        };

        var verdict = CapabilityExitPolicy.Evaluate(capabilities, strict: true);

        Assert.Equal(1, verdict.ExitCode);
        Assert.Contains("ocr-native", verdict.StrictFailures);
    }
}
