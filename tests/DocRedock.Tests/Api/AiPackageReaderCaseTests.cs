using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocRedock.Api;

namespace DocRedock.Tests.Api;

/// <summary>
/// The shared reader cases (Fixtures/AiPackage/reader-cases.json) against the .NET
/// <see cref="AiPackageManifestReader"/>: every case gives the result the file states, as
/// tools/test_ai_package_reader_cases.py requires of docs/examples/ai_package_reader.py. Where Python is
/// installed, the Python reader also runs on the same packages and must give the same exit code and lines.
/// </summary>
public sealed class AiPackageReaderCaseTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-ai-cases-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public async Task Every_case_is_accepted_or_refused_as_the_case_file_says()
    {
        var samples = Samples();
        var cases = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(samples, "reader-cases.json")))!["cases"]!.AsArray();
        Assert.True(cases.Count >= 40, $"{cases.Count} cases");
        var python = AiPackageReaderParityTests.Python();
        var compared = 0;
        foreach (var (item, number) in cases.Select((item, number) => (item!.AsObject(), number)))
        {
            var name = (string)item["name"]!;
            var package = Path.Combine(root, number.ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
            CopyDirectory(Path.Combine(samples, (string)item["from"]!), package);
            foreach (var change in item["changes"]!.AsArray()) Apply(package, change!.AsObject());
            var expected = item["verify"]!.AsObject();
            foreach (var form in item["forms"]?.AsArray().Select(form => (string)form!).ToArray() ?? ["dir", "zip"])
            {
                var path = form == "dir" ? package : Zip(package, package + ".zip");
                var (code, lines, message) = await Verify(path);
                Assert.True(code == (int)expected["exit"]!, $"{name} ({form}): exit {code}: {message} {string.Join(" | ", lines)}");
                if (code == 2)
                    foreach (var text in expected["says"]!.AsArray().Select(text => (string)text!))
                        Assert.True(message!.Contains(text, StringComparison.Ordinal), $"{name} ({form}): '{message}' does not say '{text}'");
                else
                    Assert.Equal(expected["lines"]!.AsArray().Select(line => (string)line!), lines);
                if (python is not null)
                {
                    var (pythonCode, output) = AiPackageReaderParityTests.RunPython(python, path);
                    Assert.True(pythonCode == code, $"{name} ({form}): Python exit {pythonCode} ({output}), .NET {code} ({message})");
                    if (code != 2) Assert.Equal(string.Join("\n", lines), output);
                }
                compared++;
            }
        }
        Assert.Equal(cases.Sum(item => item!["forms"]?.AsArray().Count ?? 2), compared);
    }

    [Fact]
    public void Pointers_keep_slashes_inside_brackets()
    {
        Assert.Equal(["files", "[path=parts/0003.md]", "path"], Segments("/files/[path=parts/0003.md]/path"));
        Assert.Equal(["parts", "-"], Segments("/parts/-"));
        Assert.Equal(["a/b", "c~d"], Segments("/a~1b/c~0d"));
    }

    // What `ai_package_reader.py PACKAGE --verify` does, through the .NET reader.
    private static async Task<(int Code, IReadOnlyList<string> Lines, string? Message)> Verify(string path)
    {
        try
        {
            var manifest = await AiPackageManifestReader.ReadAsync(path).WaitAsync(TimeSpan.FromSeconds(30));
            var problems = await AiPackageManifestReader.VerifyFilesAsync(path, manifest).WaitAsync(TimeSpan.FromSeconds(30));
            if (problems.Count > 0) return (1, problems, null);
            await AiPackageManifestReader.ReadSourceIndexAsync(path, manifest).WaitAsync(TimeSpan.FromSeconds(30));
            await AiPackageManifestReader.ReadReviewAsync(path, manifest).WaitAsync(TimeSpan.FromSeconds(30));
            return (0, ["files ok"], null);
        }
        catch (Exception exception) when (exception is InvalidDataException or AiPackageSchemaException or FileNotFoundException)
        {
            return (2, [], exception.Message);
        }
    }

    private static void Apply(string package, JsonObject change)
    {
        string PathOf(string relative) => Path.Combine([package, .. relative.Split('/')]);
        if (change.ContainsKey("set") || change.ContainsKey("remove"))
        {
            var file = PathOf((string)(change["set"] ?? change["remove"])!);
            var document = JsonNode.Parse(File.ReadAllText(file))!;
            var segments = Segments((string)change["at"]!);
            var container = document;
            foreach (var segment in segments[..^1])
                container = container is JsonArray array ? array[Index(array, segment)]! : container[segment]!;
            var last = segments[^1];
            var value = change["to"]?.DeepClone();
            if (container is JsonArray list)
            {
                var index = Index(list, last);
                if (change.ContainsKey("remove")) list.RemoveAt(index);
                else if (index == list.Count) list.Add(value);
                else list[index] = value;
            }
            else if (change.ContainsKey("remove")) Assert.True(container.AsObject().Remove(last), last);
            else container.AsObject()[last] = value;
            File.WriteAllText(file, document.ToJsonString(Compact), new UTF8Encoding(false));
        }
        else if (change["replace"] is { } replaced)
        {
            var file = PathOf((string)replaced!);
            var text = File.ReadAllText(file);
            var old = (string)change["text"]!;
            Assert.Equal(1, (text.Length - text.Replace(old, string.Empty, StringComparison.Ordinal).Length) / old.Length);
            File.WriteAllText(file, text.Replace(old, (string)change["with"]!, StringComparison.Ordinal), new UTF8Encoding(false));
        }
        else if (change["write"] is { } written)
        {
            var file = PathOf((string)written!);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, change["text"] is { } text ? (string)text! : change["json"]!.ToJsonString(Compact), new UTF8Encoding(false));
        }
        else if (change["rename"] is { } renamed) File.Move(PathOf((string)renamed!), PathOf((string)change["to"]!));
        else if (change["delete"] is { } deleted) File.Delete(PathOf((string)deleted!));
        else if (change["reseal"] is { } resealed)
        {
            var bytes = File.ReadAllBytes(PathOf((string)resealed!));
            var manifestPath = Path.Combine(package, "manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            var entry = manifest["files"]!.AsArray().Single(file => (string)file!["path"]! == (string)resealed!)!;
            entry["bytes"] = bytes.Length;
            entry["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
            File.WriteAllText(manifestPath, manifest.ToJsonString(Compact), new UTF8Encoding(false));
        }
        else Assert.Fail($"unknown change {change.ToJsonString()}");
    }

    // A JSON pointer's segments; a segment [key=value] runs to its closing bracket, so the value may hold "/".
    private static List<string> Segments(string pointer)
    {
        Assert.StartsWith("/", pointer, StringComparison.Ordinal);
        var segments = new List<string>();
        var rest = pointer[1..];
        while (true)
        {
            var end = rest.StartsWith('[') ? rest.IndexOf(']', StringComparison.Ordinal) + 1 : rest.IndexOf('/') is var slash and >= 0 ? slash : rest.Length;
            segments.Add(rest[..end].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
            if (end >= rest.Length) return segments;
            Assert.Equal('/', rest[end]);
            rest = rest[(end + 1)..];
        }
    }

    private static int Index(JsonArray array, string segment)
    {
        if (segment == "-") return array.Count;
        if (!segment.StartsWith('[')) return int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture);
        var (key, value) = (segment[1..segment.IndexOf('=', StringComparison.Ordinal)], segment[(segment.IndexOf('=', StringComparison.Ordinal) + 1)..^1]);
        var matches = array.Select((item, index) => (item, index))
            .Where(pair => pair.item is JsonObject item && item[key] is JsonValue field && field.TryGetValue<string>(out var text) && text == value).ToArray();
        return Assert.Single(matches).index;
    }

    // Entries in the order of their names' UTF-8 bytes, as the Python test writes them.
    private static string Zip(string directory, string target)
    {
        using var archive = ZipFile.Open(target, ZipArchiveMode.Create);
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))
            .Order(Comparer<string>.Create((left, right) => Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right))));
        foreach (var name in files) archive.CreateEntryFromFile(Path.Combine([directory, .. name.Split('/')]), name, CompressionLevel.Optimal);
        return target;
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
    }

    private static string Samples()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "AiPackage") is var path && Directory.Exists(path)) return path;
        throw new DirectoryNotFoundException("AI package samples not found.");
    }
}
