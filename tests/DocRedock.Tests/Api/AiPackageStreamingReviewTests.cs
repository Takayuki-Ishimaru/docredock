using System.IO.Compression;
using System.Text.Json.Nodes;
using DocRedock.Api;
using DocRedock.Cli;

namespace DocRedock.Tests.Api;

[CollectionDefinition("Package streaming allocation", DisableParallelization = true)]
public sealed class PackageStreamingAllocationCollection { }

// Allocation checks use the process counter across awaits, so unrelated parallel tests must finish first.
[Collection("Package streaming allocation")]
public sealed class AiPackageStreamingReviewTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-streaming-review-").FullName;
    private static string Sample => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(V033ReviewResponseTests.Fixture("spec.docx"))!, "..", "AiPackage", "schema-2.1"));
    public void Dispose() => Directory.Delete(root, true);

    private string Copy()
    {
        var target = Path.Combine(root, Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(Sample, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(Sample, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
        }
        return target;
    }

    private string Zip(string directory)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(directory, path); return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_and_single_part_reads_allocate_less_than_a_quarter_of_an_unselected_16MiB_body(bool zip)
    {
        var target = Copy();
        const int payloadBytes = 16 * 1024 * 1024;
        V033ReviewResponseTests.Relist(target, "parts/0001.md", new string('x', payloadBytes));
        var path = zip ? Zip(target) : target;
        // Warm the same reader forms before measuring provider/JIT initialization.
        var warm = zip ? Zip(Sample) : Sample;
        await AiPackageManifestReader.ReadVerifiedReviewAsync(warm);
        await AiPackageManifestReader.ReadVerifiedPartAsync(warm, "part-0003");
        AiPackageVerifiedReview? review = null;
        AiPackageVerifiedPart? part = null;
        await LowAllocation(async () => review = await AiPackageManifestReader.ReadVerifiedReviewAsync(path));
        await LowAllocation(async () => part = await AiPackageManifestReader.ReadVerifiedPartAsync(path, "part-0003"));
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(target, "review.md")), review!.ReviewMarkdown);
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(target, "parts/0003.md")), part!.Markdown);
        Assert.False(review.Review.Required); Assert.Equal(6, review.Manifest.Parts.Count);

        var stdout = new StringWriter(); var cli = new CliApplication(stdout, new StringWriter());
        await LowAllocation(async () => Assert.Equal(0, await cli.RunAsync(["package", "review", path, "--json"])));
        Assert.DoesNotContain(new string('x', 100), stdout.ToString());
        stdout.GetStringBuilder().Clear();
        await LowAllocation(async () => Assert.Equal(0, await cli.RunAsync(["package", "read", path, "--part", "part-0003"])));
        Assert.Equal(part.Markdown, stdout.ToString());

        static async Task LowAllocation(Func<Task> action)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            await action();
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            Assert.True(allocated < payloadBytes / 4, $"Allocated {allocated:N0} bytes while an unselected part has {payloadBytes:N0} bytes.");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_still_verifies_unselected_parts_and_source_index_before_returning_metadata(bool zip)
    {
        foreach (var brokenIndex in new[] { false, true })
        {
            var target = Copy();
            if (brokenIndex)
            {
                var index = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "source-index.json")))!;
                index["parts"]![2]!["sources"]![0]!["cell_addresses"] = new JsonArray("D4");
                V033ReviewResponseTests.Relist(target, "source-index.json", index.ToJsonString());
            }
            else File.AppendAllText(Path.Combine(target, "parts/0006.md"), "CHANGED");
            var path = zip ? Zip(target) : target;
            if (brokenIndex)
                await Assert.ThrowsAsync<InvalidDataException>(() => AiPackageManifestReader.ReadVerifiedReviewAsync(path));
            else
                await Assert.ThrowsAsync<AiPackageVerificationException>(() => AiPackageManifestReader.ReadVerifiedReviewAsync(path));
            var stdout = new StringWriter(); var cli = new CliApplication(stdout, new StringWriter());
            Assert.Equal(7, await cli.RunAsync(["package", "review", path, "--json", "--allow-review"]));
            Assert.Empty(stdout.ToString());
            Assert.Equal(7, await cli.RunAsync(["package", "read", path, "--part", "part-0003", "--allow-review"]));
            Assert.Empty(stdout.ToString());
        }
    }

    [Fact]
    public async Task Review_snapshot_uses_checked_bytes_and_concurrent_or_cancelled_reads_stay_independent()
    {
        var target = Copy();
        var reads = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => AiPackageManifestReader.ReadVerifiedReviewAsync(target)));
        Assert.All(reads, snapshot => Assert.Equal(reads[0].ReviewMarkdown, snapshot.ReviewMarkdown));
        var old = reads[0].ReviewMarkdown;
        File.AppendAllText(Path.Combine(target, "review.md"), "CHANGED");
        Assert.Equal(old, reads[0].ReviewMarkdown);
        await Assert.ThrowsAsync<AiPackageVerificationException>(() => AiPackageManifestReader.ReadVerifiedReviewAsync(target));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AiPackageManifestReader.ReadVerifiedReviewAsync(target, cancellation.Token));
        var stdout = new StringWriter(); var cli = new CliApplication(stdout, new StringWriter());
        Assert.Equal(2, await cli.RunAsync(["package", "review", target, "--json"], cancellation.Token));
        Assert.Empty(stdout.ToString());
    }
}
