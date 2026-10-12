using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocRedock.Cli;
using DocRedock.Api;
using System.Security.Cryptography;
using System.Text;

namespace DocRedock.Tests.Cli;

public sealed class CliPackageSearchDisplayTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-search-display-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    private static void Relist(string directory, string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(Path.Combine(directory, path), bytes);
        var manifestPath = Path.Combine(directory, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        var entry = manifest["files"]!.AsArray().Single(file => file!["path"]!.GetValue<string>() == path)!;
        entry["bytes"] = bytes.Length;
        entry["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        File.WriteAllText(manifestPath, manifest.ToJsonString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Search_escapes_terminal_controls_while_json_and_read_preserve_text(bool zip, bool json)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "DocRedock.sln")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        var sample = Path.Combine(repository.FullName, "tests", "DocRedock.Tests", "Fixtures", "AiPackage", "schema-2.0");
        var directory = Path.Combine(root, "package");
        foreach (var file in Directory.EnumerateFiles(sample, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(directory, Path.GetRelativePath(sample, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        const string partId = "part-\u001b[31m\u202e";
        const string label = "Sheet\nFORGED\r\u202eLABEL\u2066";
        const string line = "MARKER\t\u0002\u001b[2J\u007f\u0085\u061c\u200e\u200f\u2028\u2029\u202a\u202e\u2066\u2069\rFORGED 😀";
        var markdown = "# Text\n" + line + "\n";
        var manifestPath = Path.Combine(directory, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        var part = manifest["parts"]![0]!;
        var oldId = part["id"]!.GetValue<string>();
        var partPath = part["path"]!.GetValue<string>();
        part["id"] = partId;
        foreach (var location in manifest["locations"]!.AsArray()) location!["label"] = label;
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var indexPath = manifest["source_index"]!.GetValue<string>();
        var index = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, indexPath)))!;
        index["parts"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == oldId)!["id"] = partId;
        Relist(directory, indexPath, index.ToJsonString());
        Relist(directory, partPath, markdown);
        var package = directory;
        if (zip)
        {
            package = Path.Combine(root, "package.zip");
            ZipFile.CreateFromDirectory(directory, package);
        }
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter();
        var cli = new CliApplication(output, error, new DocumentService(null, null, discoverPdfRasterizer: false));
        var arguments = new List<string> { "package", "search", package, "MARKER" };
        if (json) arguments.Add("--json");
        Assert.Equal(0, await cli.RunAsync(arguments.ToArray()));
        Assert.Empty(error.ToString());
        if (json)
        {
            using var result = JsonDocument.Parse(output.ToString());
            var match = Assert.Single(result.RootElement.GetProperty("matches").EnumerateArray());
            Assert.Equal(partId, match.GetProperty("part_id").GetString());
            Assert.Equal(label, match.GetProperty("locations")[0].GetProperty("label").GetString());
            Assert.Equal(line, match.GetProperty("lines")[0].GetProperty("text").GetString());
        }
        else
        {
            Assert.DoesNotContain('\u001b', output.ToString());
            Assert.DoesNotContain('\u202e', output.ToString());
            Assert.DoesNotContain('\r', output.ToString());
            Assert.Equal(2, output.ToString().TrimEnd('\n').Split('\n').Length);
            Assert.Contains(@"part-\x1b[31m\u202e", output.ToString());
            Assert.Contains(@"Sheet\x0aFORGED\x0d\u202eLABEL\u2066", output.ToString());
            Assert.Contains(@"MARKER\x09\x02\x1b[2J\x7f\x85\u061c\u200e\u200f\u2028\u2029\u202a\u202e\u2066\u2069\x0dFORGED 😀", output.ToString());
        }

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await cli.RunAsync(["package", "read", package, "--part", partId]));
        Assert.Equal(markdown, output.ToString());
    }
}
