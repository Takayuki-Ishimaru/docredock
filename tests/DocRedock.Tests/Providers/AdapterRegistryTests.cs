using System.Text;
using System.IO.Compression;
using DocRedock.Providers.Abstractions.Providers;

namespace DocRedock.Tests.Providers;

public sealed class AdapterRegistryTests
{
    [Fact]
    public async Task Selects_highest_priority_and_restores_input_position_after_failures()
    {
        await using var input = new RewindableInput(new MemoryStream(Encoding.UTF8.GetBytes("PK sample")), leaveOpen: false);
        var failing = new TestProbe("failing", 1, _ => throw new InvalidDataException("bad probe"));
        var selected = new TestProbe("docx", 2, _ => Result("docx", priority: 20, confidence: .8));
        var registry = new AdapterRegistry([failing, selected]);

        var result = await registry.SelectAsync(input, new AdapterSelectionPolicy());

        Assert.True(result.IsSuccess);
        Assert.Same(selected, result.Selected);
        Assert.Single(result.Failures);
        Assert.Equal(0, input.Stream.Position);
    }

    [Fact]
    public async Task Strict_mode_rejects_equally_confident_adapters()
    {
        await using var input = new RewindableInput(new MemoryStream([1, 2]), leaveOpen: false);
        var registry = new AdapterRegistry([new TestProbe("one", 1, _ => Result("one", 10, .9)), new TestProbe("two", 1, _ => Result("two", 10, .88))]);

        var result = await registry.SelectAsync(input, new AdapterSelectionPolicy(AmbiguityConfidenceDelta: .05));

        Assert.Equal(AdapterSelectionStatus.Ambiguous, result.Status);
        Assert.Contains(result.Warnings, warning => warning.Code == "AmbiguousAdapter");
    }

    [Fact]
    public async Task Rejects_unallowlisted_external_provider()
    {
        await using var input = new RewindableInput(new MemoryStream([1]), leaveOpen: false);
        var external = new TestProbe("external", 1, _ => Result("external", 10, .9), builtIn: false, hash: "abc");
        var registry = new AdapterRegistry([external]);

        var result = await registry.SelectAsync(input, new AdapterSelectionPolicy(Allowlist: new ProviderAllowlist(Array.Empty<AllowedProvider>())));

        Assert.Equal(AdapterSelectionStatus.NoSupportedAdapter, result.Status);
        Assert.Contains(result.Warnings, warning => warning.Code == "AllowlistRejected");
    }

    [Fact]
    public async Task Container_detector_identifies_ooxml_and_reports_extension_mismatch()
    {
        await using var input = new RewindableInput(CreateZip("[Content_Types].xml", "word/document.xml"), leaveOpen: false);
        var detector = new ContainerFormatDetector();

        var result = await detector.ProbeAsync(input, new ProbeContext(FileName: "misnamed.xlsx"), CancellationToken.None);

        Assert.True(result.IsSupported);
        Assert.Contains(result.Evidence, evidence => evidence.Kind == "ooxml_part" && evidence.Detail == "docx");
        Assert.Contains(result.Warnings, warning => warning.Code == "ExtensionMismatch");
    }

    [Fact]
    public async Task Container_detector_reports_macro_without_executing_it()
    {
        await using var input = new RewindableInput(CreateZip(
            "[Content_Types].xml", "word/document.xml", "word/vbaProject.bin"), leaveOpen: false);

        var result = await new ContainerFormatDetector().ProbeAsync(
            input,
            new ProbeContext(FileName: "macro.docm"),
            CancellationToken.None);

        Assert.True(result.IsSupported);
        Assert.Contains(result.Evidence, evidence => evidence.Kind == "macro" && evidence.Detail == "present");
        Assert.Contains(result.Warnings, warning => warning.Code == "MacroEnabled");
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "ExtensionMismatch");
    }

    [Fact]
    public async Task Rewindable_input_enforces_limit_for_seekable_streams()
    {
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await using var _ = await RewindableInput.CreateAsync(new MemoryStream(new byte[5]), maxBytes: 4);
        });
    }

    [Fact]
    public async Task Local_resource_resolver_rejects_network_and_root_escape()
    {
        var root = Path.Combine(Path.GetTempPath(), "docredock-resource-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var allowedPath = Path.Combine(root, "asset.bin");
            await File.WriteAllBytesAsync(allowedPath, [1, 2, 3]);
            var resolver = new LocalResourceResolver();
            await using var resolved = await resolver.ResolveReadOnlyAsync(
                new ResourceReference(allowedPath),
                new ResourcePolicy([root]));

            Assert.Equal(3, resolved.Size);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
                await resolver.ResolveReadOnlyAsync(new ResourceReference("https://example.invalid/a"), new ResourcePolicy([root])));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
                await resolver.ResolveReadOnlyAsync(new ResourceReference(Path.GetTempPath()), new ResourcePolicy([root])));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Security_gate_rejects_path_traversal_before_extraction()
    {
        await using var input = new RewindableInput(CreateZip("../outside.xml"), leaveOpen: false);

        var assessment = ContainerSecurityGate.Assess(input);

        Assert.False(assessment.IsAllowed);
        Assert.Contains(assessment.Diagnostics, diagnostic => diagnostic.Code == "ZipPathTraversal");
        Assert.Equal(0, input.Stream.Position);
    }

    private static ProbeResult Result(string id, int priority, double confidence) => new(id, confidence, priority, Array.Empty<ProbeEvidence>(), Array.Empty<ProbeWarning>(), false, false, true);

    private static MemoryStream CreateZip(params string[] paths)
    {
        var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var path in paths) archive.CreateEntry(path).Open().Dispose();
        output.Position = 0;
        return output;
    }

    private sealed class TestProbe : IFormatProbe
    {
        private readonly Func<RewindableInput, ProbeResult> execute;
        public TestProbe(string id, int version, Func<RewindableInput, ProbeResult> execute, bool builtIn = true, string hash = "built-in")
        {
            this.execute = execute;
            Descriptor = new(id, new Version(version, 0), 1, new HashSet<string>(), "MIT", hash, builtIn);
        }
        public ProviderDescriptor Descriptor { get; }
        public ValueTask<ProbeResult> ProbeAsync(RewindableInput input, ProbeContext context, CancellationToken cancellationToken)
        {
            _ = input.Stream.ReadByte();
            return ValueTask.FromResult(execute(input));
        }
    }
}
