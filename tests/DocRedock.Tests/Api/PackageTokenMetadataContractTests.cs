using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocRedock.Api;
using DocRedock.Cli;

namespace DocRedock.Tests.Api;

public sealed class PackageTokenMetadataContractTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-token-metadata-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData(AiPackageForm.Directory, 1)]
    [InlineData(AiPackageForm.Directory, 1.5)]
    [InlineData(AiPackageForm.Zip, 2)]
    [InlineData(AiPackageForm.Zip, 100)]
    public async Task Read_and_search_expose_stored_adjusted_estimates_without_scaling_twice(AiPackageForm form, double multiplier)
    {
        var source = TokenEstimateMultiplierTests.WriteSource(root);
        var path = Path.Combine(root, form == AiPackageForm.Zip ? "package.zip" : "package");
        var exported = await service.ExportAiPackageAsync(new AiPackageExportOptions(new(source, "unused.md"),
            path, form, TargetCharacters: 512) { TokenEstimateMultiplier = multiplier });
        var snapshot = await AiPackageManifestReader.ReadVerifiedPackageAsync(path);
        Assert.Equal(multiplier, Multiplier(snapshot.Manifest));
        Assert.True(snapshot.Parts.Count > 1);
        var matches = AiPackageSearch.Find(snapshot, "Paragraph");
        Assert.Equal(snapshot.Parts.Count, matches.Count);
        foreach (var match in matches)
        {
            var original = exported.Parts.Single(part => part.Id == match.PartId);
            Assert.Equal(original.EstimatedTokens, Estimate(match));
            Assert.Equal(multiplier, Multiplier(match));
            Assert.NotEmpty(match.Lines);
            Assert.NotEmpty(match.Locations);
        }
        var selected = snapshot.Parts[0];
        foreach (var withSources in new[] { false, true })
        {
            var (exit, stdout, stderr) = await Run(["package", "read", path, "--part", selected.Part.Id,
                "--json", .. (withSources ? new[] { "--with-sources" } : Array.Empty<string>())]);
            Assert.Equal(0, exit);
            Assert.Empty(stderr);
            using var json = JsonDocument.Parse(stdout);
            Assert.Equal(selected.Part.EstimatedTokens, json.RootElement.GetProperty("estimated_tokens").GetInt32());
            Assert.Equal(multiplier, json.RootElement.GetProperty("token_estimate_multiplier").GetDouble());
            Assert.Equal(selected.Markdown, json.RootElement.GetProperty("markdown").GetString());
            Assert.Equal(withSources ? JsonValueKind.Array : JsonValueKind.Null,
                json.RootElement.GetProperty("sources").ValueKind);
        }
        var search = await Run("package", "search", path, "paragraph", "--json");
        Assert.Equal(0, search.Exit);
        using var result = JsonDocument.Parse(search.Stdout);
        var jsonMatches = result.RootElement.GetProperty("matches").EnumerateArray().ToArray();
        Assert.Equal(matches.Count, jsonMatches.Length);
        foreach (var match in jsonMatches)
        {
            var expected = exported.Parts.Single(part => part.Id == match.GetProperty("part_id").GetString());
            Assert.Equal(expected.EstimatedTokens, match.GetProperty("estimated_tokens").GetInt32());
            Assert.Equal(multiplier, match.GetProperty("token_estimate_multiplier").GetDouble());
            Assert.NotEmpty(match.GetProperty("lines").EnumerateArray());
        }
        var plain = await Run("package", "read", path, "--part", selected.Part.Id);
        Assert.Equal((0, selected.Markdown, ""), plain);
        var empty = await Run("package", "search", path, "unmatched-token-zzz", "--json");
        using var emptyJson = JsonDocument.Parse(empty.Stdout);
        Assert.Empty(emptyJson.RootElement.GetProperty("matches").EnumerateArray());

        await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            var read = await AiPackageManifestReader.ReadVerifiedPartAsync(path, selected.Part.Id);
            Assert.Equal(multiplier, Multiplier(read.Manifest));
            Assert.Equal(selected.Markdown, read.Markdown);
            Assert.Equal(matches.Count, AiPackageSearch.Find(snapshot, "Paragraph").Count);
        }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => AiPackageSearch.Find(snapshot, "Paragraph", cancellation.Token));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("2.0")]
    [InlineData("2.1")]
    [InlineData("2.2")]
    public async Task Older_packages_preserve_absent_metadata_as_null(string schema)
    {
        var path = Sample(schema);
        var snapshot = await AiPackageManifestReader.ReadVerifiedPackageAsync(path);
        Assert.Null(Multiplier(snapshot.Manifest));
        var selected = snapshot.Parts[0];
        var read = await Run("package", "read", path, "--part", selected.Part.Id, "--json", "--allow-review");
        Assert.Equal(0, read.Exit);
        using var json = JsonDocument.Parse(read.Stdout);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("token_estimate_multiplier").ValueKind);
        if (selected.Part.EstimatedTokens is { } expected)
            Assert.Equal(expected, json.RootElement.GetProperty("estimated_tokens").GetInt32());
        else Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("estimated_tokens").ValueKind);
        var query = selected.Markdown.Split('\n').First(line => !string.IsNullOrWhiteSpace(line)).Trim();
        var matches = AiPackageSearch.Find(snapshot, query);
        Assert.NotEmpty(matches);
        Assert.All(matches, match =>
        {
            Assert.Null(Multiplier(match));
            Assert.Equal(snapshot.Parts.Single(part => part.Part.Id == match.PartId).Part.EstimatedTokens, Estimate(match));
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("1.5")]
    [InlineData("100")]
    public void Optional_manifest_multiplier_is_nullable_and_typed(string? value)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(Sample("2.2"), "manifest.json")))!.AsObject();
        if (value is null) json.Remove("token_estimate_multiplier");
        else json["token_estimate_multiplier"] = JsonNode.Parse(value);
        Assert.Equal(value is null or "null" ? null : (double?)double.Parse(value,
            System.Globalization.CultureInfo.InvariantCulture), Multiplier(AiPackageManifestReader.Parse(json.ToJsonString())));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.5")]
    [InlineData("-1")]
    [InlineData("100.01")]
    [InlineData("1e309")]
    [InlineData("\"2\"")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Invalid_present_multiplier_is_refused_by_field_name(string value)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(Sample("2.2"), "manifest.json")))!.AsObject();
        json["token_estimate_multiplier"] = JsonNode.Parse(value);
        var error = Assert.Throws<InvalidDataException>(() => AiPackageManifestReader.Parse(json.ToJsonString()));
        Assert.Contains("token_estimate_multiplier", error.Message);
    }

    [Fact]
    public async Task Verification_failure_still_prevents_json_body_and_metadata_output()
    {
        var source = TokenEstimateMultiplierTests.WriteSource(root);
        var path = Path.Combine(root, "package");
        var exported = await service.ExportAiPackageAsync(new AiPackageExportOptions(new(source, "unused.md"), path)
            { TokenEstimateMultiplier = 2 });
        await File.AppendAllTextAsync(Path.Combine(path, exported.Parts[0].Path), "tampered");
        foreach (var action in new[] { "read", "search" })
        {
            var args = action == "read" ? new[] { "--part", exported.Parts[0].Id } : new[] { "Paragraph" };
            var result = await Run(["package", action, path, .. args, "--json", "--allow-review"]);
            Assert.Equal(7, result.Exit);
            Assert.Empty(result.Stdout);
        }
    }

    private static double? Multiplier(object value)
    {
        var property = value.GetType().GetProperty("TokenEstimateMultiplier");
        Assert.NotNull(property);
        Assert.Equal(typeof(double?), property.PropertyType);
        return (double?)property.GetValue(value);
    }

    private static int? Estimate(object value)
    {
        var property = value.GetType().GetProperty("EstimatedTokens");
        Assert.NotNull(property);
        Assert.Equal(typeof(int?), property.PropertyType);
        return (int?)property.GetValue(value);
    }

    private async Task<(int Exit, string Stdout, string Stderr)> Run(params string[] args)
    {
        var output = new StringWriter(); var error = new StringWriter();
        var exit = await new CliApplication(output, error, service).RunAsync(args);
        return (exit, output.ToString(), error.ToString());
    }

    private static string Sample(string schema)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var path = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "AiPackage", "schema-" + schema);
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException(schema);
    }
}
