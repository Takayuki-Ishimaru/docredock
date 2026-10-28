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
        // The released package retains the estimates written by its original heuristic.
        Assert.Equal([22, 52, 54, 54, 46, 48], manifest.Parts.Select(part => part.EstimatedTokens));
        Assert.Empty(await AiPackageManifestReader.VerifyFilesAsync(path, manifest));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Schema_2_2_blocks_repeat_the_text_above_the_table_and_name_its_cells(bool zip)
    {
        var path = zip ? Zip(Sample("2.2")) : Sample("2.2");
        var manifest = await AiPackageManifestReader.ReadAsync(path);
        Assert.Equal(("2.2", "captioned_sales.xlsx"), (manifest.SchemaVersion, manifest.SourceFileName));
        Assert.Equal([120, 119, 17], manifest.Parts.Select(part => part.NodeCount));
        Assert.All(manifest.Parts, part => Assert.Equal(("table-0001", "A5:C5", "A3:A4"),
            (part.TableBlock!.TableId, part.TableBlock.HeaderRange, part.TableBlock.ContextRange)));
        var index = await AiPackageManifestReader.ReadSourceIndexAsync(path, manifest);
        Assert.All(manifest.Parts, part => Assert.Superset(new HashSet<string?> { "A3", "A4", "A5", "B5", "C5" },
            index[part.Id].SelectMany(group => group.CellAddresses ?? []).ToHashSet()));
        Assert.Empty(await AiPackageManifestReader.VerifyFilesAsync(path, manifest));
        Assert.False(await AiPackageManifestReader.ReadReviewRequiredAsync(path, manifest));
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
    [InlineData("\"2.1\\n\"", "'2.1\n'")]
    [InlineData("\" 2.1\"", "' 2.1'")]
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
        // Refused, not followed, and reported like any other problem, in the Python reader's words.
        Assert.Equal(["parts/0002.md: refused (package path is a link: 'parts/0002.md')"],
            await AiPackageManifestReader.VerifyFilesAsync(copy, manifest));
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
        Assert.Equal($"parts/0002.md: sha256 mismatch (expected {manifest.Files.Single(file => file.Path == "parts/0002.md").Sha256}, got " +
            $"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(changed)))})", problems[0]);
        var escaping = manifest with { Files = [.. manifest.Files, new AiPackageManifestFile("../outside.txt", 1, new string('0', 64))] };
        Assert.Equal([.. problems, "../outside.txt: refused (unsafe package path: '../outside.txt')"],
            await AiPackageManifestReader.VerifyFilesAsync(copy, escaping));
        Assert.Throws<InvalidDataException>(() => AiPackageManifestReader.Parse("{ not json"));
    }

    // v0.3.2 evaluation R2/R3: a manifest missing what verification needs, or with fields of the
    // wrong shape, is refused by name when it is read instead of verifying less or failing later.
    [Theory]
    [InlineData("source is missing", "remove", "source")]
    [InlineData("source must be an object", "set", "source", "123")]
    [InlineData("source.sha256 must be 64 hexadecimal digits", "set", "source.sha256", "\"abc\"")]
    [InlineData("content_policy is missing", "remove", "content_policy")]
    [InlineData("files is missing", "remove", "files")]
    [InlineData("files must be an array", "set", "files", "\"bad\"")]
    [InlineData("files[0] must be an object", "set", "files.0", "\"document.md\"")]
    [InlineData("files[0].bytes is missing", "remove", "files.0.bytes")]
    [InlineData("files[0].bytes must be a whole number", "set", "files.0.bytes", "true")]
    [InlineData("files[1].bytes must be a whole number", "set", "files.1.bytes", "-1")]
    [InlineData("files[0].sha256 is missing", "remove", "files.0.sha256")]
    [InlineData("files[0].sha256 must be 64 hexadecimal digits", "set", "files.0.sha256", "\"zzzz\"")]
    [InlineData("files[0].path is null", "set", "files.0.path", "null")]
    [InlineData("files[0].path '../document.md' is not a relative path", "set", "files.0.path", "\"../document.md\"")]
    [InlineData("files[0].path 'C:document.md' is not a relative path", "set", "files.0.path", "\"C:document.md\"")]
    [InlineData("parts is missing", "remove", "parts")]
    [InlineData("parts[0] must be an object", "set", "parts.0", "null")]
    [InlineData("parts[1].node_count is missing", "remove", "parts.1.node_count")]
    [InlineData("parts[1].node_count must be a whole number", "set", "parts.1.node_count", "\"8\"")]
    [InlineData("parts[1].source_ids must hold only strings", "set", "parts.1.source_ids", "[1]")]
    [InlineData("parts[1].exceeds_target must be true or false", "set", "parts.1.exceeds_target", "\"no\"")]
    [InlineData("parts[1].estimated_tokens must be a whole number", "set", "parts.1.estimated_tokens", "1.5")]
    [InlineData("parts[1].table_block must be an object", "set", "parts.1.table_block", "[]")]
    [InlineData("parts[1].table_block.index must be from 1 to count (3), got 4", "set", "parts.1.table_block.index", "4")]
    [InlineData("parts[1].table_block.row_range is missing", "remove", "parts.1.table_block.row_range")]
    [InlineData("part part-0002 next_part names an unknown part 'part-0099'", "set", "parts.1.table_block.next_part", "\"part-0099\"")]
    [InlineData("part part-0002 names an unknown location 'source-0099'", "set", "parts.1.source_ids", "[\"source-0099\"]")]
    [InlineData("part part-0002 names a location twice", "set", "parts.1.source_ids", "[\"source-0001\", \"source-0001\"]")]
    [InlineData("locations must be an array", "set", "locations", "{}")]
    [InlineData("locations[0].label is missing", "remove", "locations.0.label")]
    [InlineData("locations[0].heading_path must hold only strings", "set", "locations.0.heading_path", "[null]")]
    [InlineData("source_index '/etc/passwd' is not a relative path", "set", "source_index", "\"/etc/passwd\"")]
    public void Missing_or_mistyped_manifest_fields_are_refused_by_name(string message, string operation, string path, string? value = null)
    {
        var manifest = SampleManifest("2.1");
        var steps = path.Split('.');
        JsonNode parent = manifest;
        foreach (var step in steps[..^1]) parent = int.TryParse(step, out var index) ? parent[index]! : parent[step]!;
        var last = steps[^1];
        if (operation == "remove") parent.AsObject().Remove(last);
        else if (int.TryParse(last, out var position)) parent.AsArray()[position] = value is null ? null : JsonNode.Parse(value);
        else parent.AsObject()[last] = JsonNode.Parse(value!);
        var error = Assert.Throws<InvalidDataException>(() => AiPackageManifestReader.Parse(manifest.ToJsonString()));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.StartsWith("AI package manifest.json: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_with_only_a_version_is_refused() =>
        Assert.Contains("source is missing", Assert.Throws<InvalidDataException>(() =>
            AiPackageManifestReader.Parse("{\"schema_version\":\"2.1\"}")).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("2.1", "document.md")]
    [InlineData("2.1", "review.md")]
    [InlineData("2.1", "report.json")]
    [InlineData("2.1", "source-index.json")]
    [InlineData("2.1", "parts/0004.md")]
    [InlineData("1.0", "parts/0002.md")]
    public void Every_file_the_manifest_names_must_be_listed_with_its_size_and_hash(string schema, string unlisted)
    {
        var manifest = SampleManifest(schema);
        var files = manifest["files"]!.AsArray();
        files.Remove(files.Single(file => (string)file!["path"]! == unlisted));
        var error = Assert.Throws<InvalidDataException>(() => AiPackageManifestReader.Parse(manifest.ToJsonString()));
        Assert.Contains($"files does not list '{unlisted}'", error.Message, StringComparison.Ordinal);
        manifest["files"] = new JsonArray();
        Assert.Contains("and ", Assert.Throws<InvalidDataException>(() => AiPackageManifestReader.Parse(manifest.ToJsonString())).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ids_paths_keys_and_zip_entries_listed_twice_are_refused()
    {
        void Refused(string message, Action<JsonObject> change)
        {
            var manifest = SampleManifest("2.1");
            change(manifest);
            Assert.Contains(message, Assert.Throws<InvalidDataException>(() => AiPackageManifestReader.Parse(manifest.ToJsonString())).Message,
                StringComparison.Ordinal);
        }
        Refused("locations list 'source-0001' twice", manifest =>
        {
            var copy = manifest["locations"]![0]!.DeepClone();
            copy["sheet_name"] = "Other";
            manifest["locations"]!.AsArray().Add(copy);
        });
        Refused("parts list the id 'part-0001' twice", manifest =>
        {
            var copy = manifest["parts"]![0]!.DeepClone();
            copy["path"] = "parts/0009.md";
            manifest["parts"]!.AsArray().Add(copy);
            var file = manifest["files"]![0]!.DeepClone();
            file["path"] = "parts/0009.md";
            manifest["files"]!.AsArray().Add(file);
        });
        Refused("parts list the path 'parts/0001.md' twice", manifest => manifest["parts"]![1]!["path"] = "parts/0001.md");
        Refused("files list 'document.md' twice", manifest => manifest["files"]!.AsArray().Add(manifest["files"]![0]!.DeepClone()));
        var json = File.ReadAllText(Path.Combine(Sample("2.1"), "manifest.json"));
        var twice = json.Replace("\"schema_version\": \"2.1\",", "\"schema_version\": \"2.1\", \"schema_version\": \"3.0\",", StringComparison.Ordinal);
        Assert.NotEqual(json, twice);
        Assert.Contains("schema_version", Assert.Throws<InvalidDataException>(() => AiPackageManifestReader.Parse(twice)).Message, StringComparison.Ordinal);

        var zip = Zip(Sample("2.1"));
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(archive.CreateEntry("parts/0002.md").Open()))
            await writer.WriteAsync("# a second part 2\n");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => AiPackageManifestReader.ReadAsync(zip));
        Assert.Contains("more than one entry named 'parts/0002.md'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Schema_1_nodes_and_sources_must_pair_one_to_one()
    {
        var fewer = SampleManifest("1.0");
        fewer["parts"]![0]!["sources"]!.AsArray().RemoveAt(2);
        Assert.Contains("parts[0] lists 3 node_ids but 2 sources", Assert.Throws<InvalidDataException>(() =>
            AiPackageManifestReader.Parse(fewer.ToJsonString())).Message, StringComparison.Ordinal);
        var other = SampleManifest("1.0");
        other["parts"]![0]!["sources"]![1]!["node_id"] = "not-the-node";
        Assert.Contains("parts[0].sources[1].node_id", Assert.Throws<InvalidDataException>(() =>
            AiPackageManifestReader.Parse(other.ToJsonString())).Message, StringComparison.Ordinal);
        // Equal lengths still group every node.
        var manifest = await AiPackageManifestReader.ReadAsync(Sample("1.0"));
        var index = await AiPackageManifestReader.ReadSourceIndexAsync(Sample("1.0"), manifest);
        Assert.All(manifest.Parts, part => Assert.Equal(part.NodeCount, index[part.Id].Sum(group => group.NodeIds.Count)));
    }

    [Theory]
    [InlineData("schema_version '99.0' is unsupported", "version")]
    [InlineData("schema_version is missing", "no-version")]
    [InlineData("no entry for part-0006", "drop-part")]
    [InlineData("names a part the manifest does not list: 'part-0099'", "unknown-part")]
    [InlineData("parts list 'part-0001' twice", "part-twice")]
    [InlineData("must hold one address or null for each of its", "drop-cell")]
    [InlineData("lists 7 node ids, but the manifest part counts 8", "drop-node")]
    [InlineData("groups name the locations [source-0099]", "other-location")]
    [InlineData("parts[1].sources[0].node_ids must hold only strings", "number-node")]
    public async Task The_source_index_must_agree_with_the_manifest(string message, string change)
    {
        var copy = Copy(Sample("2.1"), "index-" + change);
        var path = Path.Combine(copy, "source-index.json");
        var index = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var group = index["parts"]![1]!["sources"]![0]!;
        switch (change)
        {
            case "version": index["schema_version"] = "99.0"; break;
            case "no-version": index.Remove("schema_version"); break;
            case "drop-part": index["parts"]!.AsArray().RemoveAt(5); break;
            case "unknown-part": index["parts"]![0]!["id"] = "part-0099"; break;
            case "part-twice": index["parts"]!.AsArray().Add(index["parts"]![0]!.DeepClone()); break;
            case "drop-cell": group["cell_addresses"]!.AsArray().RemoveAt(0); break;
            case "drop-node": group["node_ids"]!.AsArray().RemoveAt(0); group["cell_addresses"]!.AsArray().RemoveAt(0); break;
            case "other-location": group["source_id"] = "source-0099"; break;
            case "number-node": group["node_ids"]!.AsArray().Add(5); break;
        }
        await File.WriteAllTextAsync(path, index.ToJsonString());
        var manifest = await AiPackageManifestReader.ReadAsync(copy);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => AiPackageManifestReader.ReadSourceIndexAsync(copy, manifest));
        Assert.True(error is (InvalidDataException or AiPackageSchemaException), error.ToString());
        Assert.Equal(change == "version", error is AiPackageSchemaException);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_required_comes_from_the_report()
    {
        var copy = Copy(Sample("2.1"), "review");
        var manifest = await AiPackageManifestReader.ReadAsync(copy);
        Assert.False(await AiPackageManifestReader.ReadReviewRequiredAsync(copy, manifest));
        var reportPath = Path.Combine(copy, "report.json");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath))!.AsObject();
        report["review"]!["required"] = true;
        await File.WriteAllTextAsync(reportPath, report.ToJsonString());
        Assert.True(await AiPackageManifestReader.ReadReviewRequiredAsync(copy, manifest));
        report["review"]!["required"] = "yes";
        await File.WriteAllTextAsync(reportPath, report.ToJsonString());
        Assert.Contains("review.required must be true or false", (await Assert.ThrowsAsync<InvalidDataException>(() =>
            AiPackageManifestReader.ReadReviewRequiredAsync(copy, manifest))).Message, StringComparison.Ordinal);
        report["schema_version"] = "2.0";
        await File.WriteAllTextAsync(reportPath, report.ToJsonString());
        await Assert.ThrowsAsync<AiPackageSchemaException>(() => AiPackageManifestReader.ReadReviewRequiredAsync(copy, manifest));
    }

    [Fact]
    public async Task Archives_that_are_not_packages_are_explained()
    {
        var notZip = Path.Combine(root, "not-a-package.zip");
        await File.WriteAllTextAsync(notZip, "this is not a zip\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => AiPackageManifestReader.ReadAsync(notZip));
        // Zipping the package folder itself puts every file one level down.
        var nested = Path.Combine(root, "nested.zip");
        ZipFile.CreateFromDirectory(Sample("2.0"), nested, CompressionLevel.Optimal, includeBaseDirectory: true);
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => AiPackageManifestReader.ReadAsync(nested));
        Assert.Contains("found 'schema-2.0/manifest.json'", error.Message, StringComparison.Ordinal);
        var parent = Path.Combine(root, "parent");
        Copy(Sample("2.0"), Path.Combine("parent", "inner"));
        Assert.Contains("found 'inner/manifest.json'", (await Assert.ThrowsAsync<FileNotFoundException>(() =>
            AiPackageManifestReader.ReadAsync(parent))).Message, StringComparison.Ordinal);
    }

    // Inputs from the independent review of the hardened readers (2026-10-07). Each is refused (or read)
    // the same way as by docs/examples/ai_package_reader.py.
    [Theory]
    [InlineData("label")]
    [InlineData("unknown key")]
    [InlineData("unknown value")]
    public void Unpaired_surrogate_escapes_are_refused_as_invalid_data(string where)
    {
        var json = File.ReadAllText(Path.Combine(Sample("2.2"), "manifest.json"));
        var changed = where switch
        {
            "label" => json.Replace("\"label\": \"sheet Data\"", "\"label\": \"\\ud800\"", StringComparison.Ordinal),
            "unknown key" => json.Replace("\"schema_version\": \"2.2\",", "\"schema_version\": \"2.2\", \"future\\ud800\": 1,", StringComparison.Ordinal),
            _ => json.Replace("\"schema_version\": \"2.2\",", "\"schema_version\": \"2.2\", \"future\": \"\\udfff\",", StringComparison.Ordinal),
        };
        Assert.NotEqual(json, changed);
        Assert.Contains("unpaired surrogate", Assert.Throws<InvalidDataException>(() => AiPackageManifestReader.Parse(changed)).Message,
            StringComparison.Ordinal);
        Assert.Equal("😀", AiPackageManifestReader.Parse(json.Replace("\"label\": \"sheet Data\"", "\"label\": \"\\ud83d\\ude00\"",
            StringComparison.Ordinal)).Locations[0].Label);
    }

    [Theory]
    [InlineData("parts\\0009.md")]
    [InlineData("manifest.json\0")]
    public async Task Zip_entry_names_with_a_backslash_or_nul_are_refused(string name)
    {
        var zip = Zip(Sample("2.2"));
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(archive.CreateEntry(name).Open()))
            await writer.WriteAsync("odd");
        await Assert.ThrowsAsync<InvalidDataException>(() => AiPackageManifestReader.ReadAsync(zip));
    }

    [Theory]
    [InlineData("encrypted")]
    [InlineData("crc")]
    public async Task Zip_entries_marked_encrypted_or_with_a_wrong_crc_are_refused(string change)
    {
        var zip = Zip(Sample("2.2"));
        var bytes = await File.ReadAllBytesAsync(zip);
        // The first entry the archive lists, in its central directory and its local header.
        var central = bytes.AsSpan().IndexOf("PK\u0001\u0002"u8);
        var local = BitConverter.ToInt32(bytes, central + 42);
        var name = System.Text.Encoding.UTF8.GetString(bytes, central + 46, BitConverter.ToUInt16(bytes, central + 28));
        if (change == "encrypted")
            foreach (var offset in new[] { local + 6, central + 8 }) bytes[offset] |= 1;
        else
            foreach (var offset in new[] { local + 14, central + 16 }) bytes[offset] ^= 0xFF;
        await File.WriteAllBytesAsync(zip, bytes);
        var manifest = name == "manifest.json" ? null : await AiPackageManifestReader.ReadAsync(zip);
        if (manifest is null)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => AiPackageManifestReader.ReadAsync(zip));
            return;
        }
        var problem = Assert.Single(await AiPackageManifestReader.VerifyFilesAsync(zip, manifest));
        Assert.Equal(change == "encrypted"
            ? $"{name}: refused ('{name}' is encrypted in the ZIP)"
            : $"{name}: refused (cannot read '{name}' from the ZIP: Bad CRC-32 for file '{name}')", problem);
    }

    [Fact]
    public async Task A_pipe_in_a_package_folder_is_refused_without_being_opened()
    {
        if (OperatingSystem.IsWindows()) return;
        var copy = Copy(Sample("2.2"), "pipe");
        var part = Path.Combine(copy, "parts", "0003.md");
        File.Delete(part);
        using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", part)) await mkfifo!.WaitForExitAsync();
        var manifest = await AiPackageManifestReader.ReadAsync(copy);
        // Never opened: refused as the Python reader refuses it, whatever size the manifest lists.
        const string refused = "parts/0003.md: refused (package path is not a regular file: 'parts/0003.md')";
        Assert.Equal([refused], await AiPackageManifestReader.VerifyFilesAsync(copy, manifest).WaitAsync(TimeSpan.FromSeconds(10)));
        var empty = manifest with { Files = [.. manifest.Files.Select(file => file.Path == "parts/0003.md"
            ? file with { Bytes = 0, Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([])) } : file)] };
        Assert.Equal([refused], await AiPackageManifestReader.VerifyFilesAsync(copy, empty).WaitAsync(TimeSpan.FromSeconds(10)));
        File.Delete(Path.Combine(copy, "manifest.json"));
        using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", Path.Combine(copy, "manifest.json"))) await mkfifo!.WaitForExitAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => AiPackageManifestReader.ReadAsync(copy).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static JsonObject SampleManifest(string schema) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Sample(schema), "manifest.json")))!.AsObject();

    private string Copy(string directory, string name)
    {
        var copy = Path.Combine(root, name);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(directory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return copy;
    }
}
