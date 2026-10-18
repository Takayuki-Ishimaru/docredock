using System.Security.Cryptography;
using DocRedock.Core.Diff;
using DocRedock.Core.Documents;
using DocRedock.RoundTrip;

namespace DocRedock.Tests.RoundTrip;

public sealed class OutputConcurrencyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "docredock-output-concurrency", Guid.NewGuid().ToString("N"));

    public OutputConcurrencyTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concurrent_creates_preserve_the_winners_markdown_and_workspace(bool sameWorkspace)
    {
        var source = await WriteSourceAsync("source");
        var markdown = Path.Combine(root, "shared.md");
        var paths = new[] { Path.Combine(root, "first.drmd"), Path.Combine(root, sameWorkspace ? "first.drmd" : "second.drmd") };
        var errors = await RunTogetherAsync(async index =>
        {
            await RoundTripWorkspace.CreateAsync(paths[index], source,
                new() { MarkdownPath = markdown, MarkdownContent = $"# Writer {index}\n" });
        });

        var winner = AssertOneWinner(errors);
        Assert.Equal($"# Writer {winner}\n", await File.ReadAllTextAsync(markdown));
        var workspace = await RoundTripWorkspace.OpenAsync(paths[winner]);
        await workspace.VerifyStrictAsync(markdown);
        if (!sameWorkspace) Assert.False(Directory.Exists(paths[1 - winner]));
        AssertNoStaging();
    }

    [Fact]
    public async Task Concurrent_pack_to_keeps_one_complete_verified_zip()
    {
        var (workspace, markdown) = await CreateWorkspaceAsync("source");
        var output = Path.Combine(root, "packed.drmd");
        var errors = await RunTogetherAsync(async _ =>
        {
            await SidecarContainer.PackToAsync(workspace.RootPath, markdown, output);
        });

        AssertOneWinner(errors);
        await using var lease = await SidecarContainer.OpenAsync(output);
        var packedWorkspace = await RoundTripWorkspace.OpenAsync(lease.RootPath);
        await packedWorkspace.VerifyStrictAsync(markdown);
        await workspace.VerifyStrictAsync(markdown);
        AssertNoStaging();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_restores_never_replace_the_winners_bytes(bool forDiff)
    {
        var first = await CreateWorkspaceAsync("first");
        var second = await CreateWorkspaceAsync("second");
        var workspaces = new[] { first, second };
        var output = Path.Combine(root, "restored.docx");
        var graph = new DocumentGraph("1.1", "doc-test", DocumentFormatKind.Docx, []);
        var diff = new DocumentGraphDiffEngine().Compare(graph, graph);
        var errors = await RunTogetherAsync(async index =>
        {
            var (workspace, markdown) = workspaces[index];
            if (forDiff) await workspace.RestoreOriginalForDiffAsync(output, diff, markdown);
            else await workspace.RestoreOriginalAsync(output, markdown);
        });

        var winner = AssertOneWinner(errors);
        Assert.Equal(await File.ReadAllBytesAsync(workspaces[winner].Workspace.OriginalSourcePath), await File.ReadAllBytesAsync(output));
        foreach (var (workspace, markdown) in workspaces) await workspace.VerifyStrictAsync(markdown);
        AssertNoStaging();
    }

    [Fact]
    public async Task Cancelled_create_leaves_no_projection_or_workspace()
    {
        var source = await WriteSourceAsync("source");
        var markdown = Path.Combine(root, "cancelled.md");
        var workspace = Path.Combine(root, "cancelled.drmd");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RoundTripWorkspace.CreateAsync(workspace, source,
            new() { MarkdownPath = markdown, MarkdownContent = "# Cancelled\n" }, cancellation.Token));

        Assert.False(File.Exists(markdown));
        Assert.False(Directory.Exists(workspace));
        AssertNoStaging();
    }

    [Fact]
    public async Task Cancelled_pack_leaves_no_output_and_keeps_the_source_workspace_valid()
    {
        var (workspace, markdown) = await CreateWorkspaceAsync("source");
        var output = Path.Combine(root, "cancelled.drmd");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SidecarContainer.PackToAsync(workspace.RootPath, markdown, output, cancellation.Token));

        Assert.False(File.Exists(output));
        await workspace.VerifyStrictAsync(markdown);
        AssertNoStaging();
    }

    private async Task<string> WriteSourceAsync(string name)
    {
        var source = Path.Combine(root, name + ".docx");
        // Incompressible bytes keep competing copies/ZIP writers in flight together.
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(4 * 1024 * 1024));
        return source;
    }

    private async Task<(RoundTripWorkspace Workspace, string Markdown)> CreateWorkspaceAsync(string name)
    {
        var source = await WriteSourceAsync(name);
        var markdown = Path.Combine(root, name + ".md");
        var workspace = await RoundTripWorkspace.CreateAsync(Path.Combine(root, name + ".drmd"), source,
            new() { MarkdownPath = markdown, MarkdownContent = "# " + name + "\n" });
        return (workspace, markdown);
    }

    private static async Task<Exception?[]> RunTogetherAsync(Func<int, Task> operation)
    {
        using var start = new Barrier(2);
        return await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(async () =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)), "Concurrent writers did not reach the start barrier.");
            return await Record.ExceptionAsync(() => operation(index));
        })));
    }

    private static int AssertOneWinner(Exception?[] errors)
    {
        Assert.Single(errors, error => error is null);
        Assert.IsAssignableFrom<IOException>(Assert.Single(errors, error => error is not null));
        return Array.FindIndex(errors, error => error is null);
    }

    private void AssertNoStaging() => Assert.Empty(Directory.EnumerateFileSystemEntries(root, ".*.tmp"));

    public void Dispose() => Directory.Delete(root, recursive: true);
}
