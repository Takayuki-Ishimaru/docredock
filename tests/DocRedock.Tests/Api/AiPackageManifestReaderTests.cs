using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocRedock.Api;

namespace DocRedock.Tests.Api;

/// <summary>
/// Manifests written by every released layout read through one API: schema 1.0 (v0.3.0, node
/// identities inline), 2.0 (v0.3.1, shared locations and a source index) and 2.1 (row blocks and
/// token estimates). The samples are real packages; an unsupported schema is refused by name.
/// </summary>
public sealed class AiPackageManifestReaderTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-ai-reader-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    private static string Sample(string schema)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var path = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "AiPackage", "schema-" + schema);
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("AI package sample not found: " + schema);
    }

    private string Zip(string directory)
    {
        var zip = Path.Combine(root, Path.GetFileName(directory) + ".zip");
        ZipFile.CreateFromDirectory(directory, zip);
        return zip;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Schema_1_0_packages_from_v0_3_0_keep_their_inline_nodes_and_gain_shared_locations(bool zip)
    {
        var path = zip ? Zip(Sample("1.0")) : Sample("1.0");
        var manifest = await AiPackageManifestReader.ReadAsync(path);
        Assert.Equal(("1.0", "0.3.0", "example.docx", "docx"), (manifest.SchemaVersion, manifest.GeneratorVersion, manifest.SourceFileName, manifest.Format));
        Assert.Null(manifest.SourceIndex);
        Assert.False(manifest.TableRowBlocks);
        Assert.Equal([3, 2, 4], manifest.Parts.Select(part => part.NodeCount));
        Assert.All(manifest.Parts, part => Assert.Equal(part.NodeCount, part.NodeIds!.Count));
        Assert.Equal(["source-0001", "source-0002", "source-0003", "source-0004"], manifest.Locations.Select(location => location.Id));
        Assert.Equal(["source-0001", "source-0002"], manifest.Parts[0].Locations.Select(location => location.Id));
        Assert.Equal(["AIパッケージのサンプル", "1. 概要"], manifest.Locations[1].HeadingPath);
        Assert.All(manifest.Parts, part => Assert.Null(part.EstimatedTokens));
        var index = await AiPackageManifestReader.ReadSourceIndexAsync(path, manifest);
        Assert.All(manifest.Parts, part => Assert.Equal(part.NodeIds, index[part.Id].SelectMany(group => group.NodeIds)));
        Assert.Empty(await AiPackageManifestReader.VerifyFilesAsync(path, manifest));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Schema_2_0_packages_from_v0_3_1_resolve_locations_and_their_source_index(bool zip)
    {
        var path = zip ? Zip(Sample("2.0")) : Sample("2.0");
        var manifest = await AiPackageManifestReader.ReadAsync(path);
        Assert.Equal(("2.0", "0.3.1", "source-index.json"), (manifest.SchemaVersion, manifest.GeneratorVersion, manifest.SourceIndex));
        Assert.Equal([3, 4, 11, 3], manifest.Parts.Select(part => part.NodeCount));
        Assert.Equal(["headings: Section 1", "headings: Section 2", "headings: Section 3", "headings: Section 4"],
            manifest.Parts.Select(part => Assert.Single(part.Locations).Label));
        Assert.All(manifest.Parts, part => Assert.Null(part.NodeIds));
        Assert.All(manifest.Parts, part => Assert.Null(part.TableBlock));
        var index = await AiPackageManifestReader.ReadSourceIndexAsync(path, manifest);
        Assert.All(manifest.Parts, part => Assert.Equal(part.NodeCount, index[part.Id].Sum(group => group.NodeIds.Count)));
        Assert.Empty(await AiPackageManifestReader.VerifyFilesAsync(path, manifest));
    }

    [Fact]
    public async Task Schema_2_1_row_blocks_chain_and_name_their_cells()
    {
        var path = Sample("2.1");
        var manifest = await AiPackageManifestReader.ReadAsync(path);
        Assert.Equal("2.1", manifest.SchemaVersion);
        Assert.True(manifest.TableRowBlocks);
        Assert.All(manifest.Parts, part => Assert.NotNull(part.EstimatedTokens));
        var blocks = manifest.Parts.Where(part => part.TableBlock is not null).Select(part => (part.Id, part.TableBlock!)).ToArray();
        Assert.Equal(["table-0001", "table-0001", "table-0001", "table-0002", "table-0002"], blocks.Select(block => block.Item2.TableId));
        foreach (var group in blocks.GroupBy(block => block.Item2.TableId))
        {
            var chain = group.ToArray();
            for (var index = 0; index < chain.Length; index++)
            {
                Assert.Equal((index + 1, chain.Length), (chain[index].Item2.Index, chain[index].Item2.Count));
                Assert.Equal(index == 0 ? null : chain[index - 1].Id, chain[index].Item2.PreviousPart);
                Assert.Equal(index == chain.Length - 1 ? null : chain[index + 1].Id, chain[index].Item2.NextPart);
            }
        }
        var sourceIndex = await AiPackageManifestReader.ReadSourceIndexAsync(path, manifest);
        var cells = sourceIndex["part-0002"].SelectMany(group => group.CellAddresses ?? []).ToArray();
        Assert.Contains("A2", cells);
        Assert.Contains("A3", cells);
        Assert.DoesNotContain("A4", cells);
        foreach (var part in manifest.Parts)
            Assert.Equal(AiTokenEstimate.Estimate(await File.ReadAllTextAsync(Path.Combine(path, part.Path))), part.EstimatedTokens);
    }

    [Fact]
    public async Task A_new_export_reads_back_exactly_as_the_builder_produced_it()
    {
        var service = new DocumentService(null, null, discoverPdfRasterizer: false);
        var source = V031EvaluationRegressionTests.Fixture("scale500.xlsx", "V030");
        var result = await service.ExportAiPackageAsync(new(new(source, "unused.md"), Path.Combine(root, "scale.zip"),
            AiPackageForm.Zip, TargetCharacters: 4000, TableRowBlocks: true));
        var manifest = await AiPackageManifestReader.ReadAsync(result.OutputPath);
        Assert.Equal(result.Parts.Select(part => (part.Id, part.Path, part.NodeIds.Count, (int?)part.EstimatedTokens, part.TableBlock)),
            manifest.Parts.Select(part => (part.Id, part.Path, part.NodeCount, part.EstimatedTokens, part.TableBlock)));
        var index = await AiPackageManifestReader.ReadSourceIndexAsync(result.OutputPath, manifest);
        Assert.All(result.Parts, part => Assert.Equal(part.NodeIds, index[part.Id].SelectMany(group => group.NodeIds)));
        Assert.Empty(await AiPackageManifestReader.VerifyFilesAsync(result.OutputPath, manifest));
        // Labels and sheet names stay readable in the JSON instead of \u escapes.
        using (var zip = ZipFile.OpenRead(result.OutputPath))
        using (var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open()))
            Assert.DoesNotContain("\\u", await reader.ReadToEndAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"3.0\"", "'3.0'")]
    [InlineData("\"0.9\"", "'0.9'")]
    [InlineData("\"2\"", "'2'")]
    [InlineData("\"2.x\"", "'2.x'")]
    [InlineData("\"abc\"", "'abc'")]
    [InlineData("2", "2")]
    [InlineData("\"02.1\"", "'02.1'")]
    [InlineData(null, "no schema_version")]
    public void Unsupported_or_missing_schema_versions_are_refused_by_name(string? version, string named)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Sample("2.1"), "manifest.json")))!.AsObject();
        if (version is null) manifest.Remove("schema_version");
        else manifest["schema_version"] = JsonNode.Parse(version);
        var error = Assert.Throws<AiPackageSchemaException>(() => AiPackageManifestReader.Parse(manifest.ToJsonString()));
        Assert.Contains(named, error.Message, StringComparison.Ordinal);
        Assert.Contains("supported: 1.x, 2.x", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2.0", "2.7")]
    [InlineData("1.0", "1.3")]
    public void Later_minor_versions_with_unknown_fields_are_read(string sample, string version)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Sample(sample), "manifest.json")))!.AsObject();
        manifest["schema_version"] = version;
        manifest["future_field"] = new JsonObject { ["anything"] = 1 };
        Assert.Equal(version, AiPackageManifestReader.Parse(manifest.ToJsonString()).SchemaVersion);
    }

    [Fact]
    public async Task Schema_1_cell_addresses_and_missing_fields_are_handled_explicitly()
    {
        var legacy = JsonNode.Parse(File.ReadAllText(Path.Combine(Sample("1.0"), "manifest.json")))!.AsObject();
        var sources = legacy["parts"]![0]!["sources"]!.AsArray();
        sources[0]!["cell_address"] = "B2";
        var manifest = AiPackageManifestReader.Parse(legacy.ToJsonString());
        var index = await AiPackageManifestReader.ReadSourceIndexAsync(Sample("1.0"), manifest);
        // Part 1 holds one node of its first location and two of its second.
        Assert.Equal(["B2"], index["part-0001"][0].CellAddresses!);
        Assert.Null(index["part-0001"][1].CellAddresses);
        Assert.Null(index["part-0002"][0].CellAddresses);

        var current = JsonNode.Parse(File.ReadAllText(Path.Combine(Sample("2.1"), "manifest.json")))!.AsObject();
        var noSource = current.DeepClone().AsObject();
        noSource.Remove("source");
        Assert.Throws<InvalidDataException>(() => AiPackageManifestReader.Parse(noSource.ToJsonString()));
        var noBytes = current.DeepClone().AsObject();
        noBytes["files"]![0]!.AsObject().Remove("bytes");
        Assert.Throws<InvalidDataException>(() => AiPackageManifestReader.Parse(noBytes.ToJsonString()));
        var noIndex = current.DeepClone().AsObject();
        noIndex.Remove("source_index");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AiPackageManifestReader.ReadSourceIndexAsync(Sample("2.1"), AiPackageManifestReader.Parse(noIndex.ToJsonString())));
    }

    [Fact]
    public async Task A_link_inside_a_package_folder_is_refused()
    {
        var copy = Path.Combine(root, "linked");
        foreach (var file in Directory.EnumerateFiles(Sample("2.1"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(Sample("2.1"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var outside = Path.Combine(root, "outside.md");
        await File.WriteAllTextAsync(outside, "# outside the package\n");
        File.Delete(Path.Combine(copy, "parts", "0002.md"));
        File.CreateSymbolicLink(Path.Combine(copy, "parts", "0002.md"), outside);
        var manifest = await AiPackageManifestReader.ReadAsync(copy);
        await Assert.ThrowsAsync<InvalidDataException>(() => AiPackageManifestReader.VerifyFilesAsync(copy, manifest));
    }

    [Fact]
    public async Task Changed_missing_and_escaping_files_are_reported_not_followed()
    {
        var copy = Path.Combine(root, "copy");
        foreach (var file in Directory.EnumerateFiles(Sample("2.1"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(Sample("2.1"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var changed = Path.Combine(copy, "parts", "0002.md");
        await File.WriteAllTextAsync(changed, (await File.ReadAllTextAsync(changed)).Replace("Revenue", "Revenuf", StringComparison.Ordinal));
        File.Delete(Path.Combine(copy, "parts", "0003.md"));
        var manifest = await AiPackageManifestReader.ReadAsync(copy);
        var problems = await AiPackageManifestReader.VerifyFilesAsync(copy, manifest);
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, problem => problem.StartsWith("parts/0002.md:", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem == "parts/0003.md: missing");
        var escaping = manifest with { Files = [new AiPackageManifestFile("../outside.txt", 1, new string('0', 64))] };
        await Assert.ThrowsAsync<InvalidDataException>(() => AiPackageManifestReader.VerifyFilesAsync(copy, escaping));
        Assert.Throws<InvalidDataException>(() => AiPackageManifestReader.Parse("{ not json"));
    }
}
