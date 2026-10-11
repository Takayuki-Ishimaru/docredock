using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DocRedock.Api;

namespace DocRedock.Tests.Api;

/// <summary>
/// The .NET <see cref="AiPackageManifestReader"/> and docs/examples/ai_package_reader.py read packages
/// the same way: for every crafted package below both accept it or both refuse it, and both report
/// the same problems in the same words (`--verify`). The Python reader runs with python3 (or python);
/// the test is skipped where neither is installed, and the link and pipe cases where the system
/// cannot make them.
/// </summary>
public sealed class AiPackageReaderParityTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-ai-parity-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public async Task Both_readers_accept_refuse_and_report_every_crafted_package_alike()
    {
        if (Python() is not { } python) return;
        var compared = 0;
        foreach (var (name, path) in Cases(python))
        {
            var (code, output) = RunPython(python, path);
            int expectedCode;
            string expected;
            try
            {
                var manifest = await AiPackageManifestReader.ReadAsync(path).WaitAsync(TimeSpan.FromSeconds(30));
                var problems = await AiPackageManifestReader.VerifyFilesAsync(path, manifest).WaitAsync(TimeSpan.FromSeconds(30));
                if (problems.Count == 0)
                {
                    // As --verify does: intact files must also hold a readable source index and report.
                    await AiPackageManifestReader.ReadSourceIndexAsync(path, manifest).WaitAsync(TimeSpan.FromSeconds(30));
                    await AiPackageManifestReader.ReadReviewAsync(path, manifest).WaitAsync(TimeSpan.FromSeconds(30));
                }
                (expectedCode, expected) = problems.Count == 0 ? (0, "files ok") : (1, string.Join("\n", problems));
            }
            catch (Exception exception) when (exception is InvalidDataException or AiPackageSchemaException or FileNotFoundException)
            {
                (expectedCode, expected) = (2, string.Empty);
            }
            Assert.True(code == expectedCode, $"{name}: Python exit {code} ({output}), .NET {expectedCode} ({expected})");
            if (code != 2) Assert.Equal(expected, output);
            // And both say what the contract says.
            var (contractCode, problem) = Contract[name];
            Assert.True(code == contractCode, $"{name}: exit {code}, the contract says {contractCode}");
            if (problem is not null) Assert.Contains(output.Split('\n'), line => line.StartsWith(problem, StringComparison.Ordinal));
            compared++;
        }
        // Links and pipes cannot be made on Windows; every other case runs everywhere.
        Assert.Equal(OperatingSystem.IsWindows() ? 24 : 28, compared);
    }

    // 0 intact, 1 reported with a line starting this way, 2 refused as a whole.
    private static readonly Dictionary<string, (int Code, string? Problem)> Contract = new()
    {
        ["folder"] = (0, null),
        ["changed part"] = (1, "parts/0002.md: sha256 mismatch (expected "),
        ["missing part"] = (1, "parts/0003.md: missing"),
        ["folder where a part should be"] = (1, "parts/0001.md: missing"),
        ["linked part"] = (1, "parts/0002.md: refused (package path is a link: 'parts/0002.md')"),
        ["dangling link"] = (1, "parts/0002.md: refused (package path is a link: 'parts/0002.md')"),
        ["pipe as a part"] = (1, "parts/0003.md: refused (package path is not a regular file: 'parts/0003.md')"),
        ["pipe as the manifest"] = (2, null),
        ["zip"] = (0, null),
        ["version needed 10.0"] = (2, null),
        ["encrypted"] = (1, "parts/0001.md: refused ('parts/0001.md' is encrypted in the ZIP)"),
        ["patched data"] = (1, "parts/0001.md: refused (cannot read 'parts/0001.md' from the ZIP: compressed patched data (flag bit 5))"),
        ["strong encryption"] = (1, "parts/0001.md: refused (cannot read 'parts/0001.md' from the ZIP: strong encryption (flag bit 6))"),
        ["deflate64"] = (1, "parts/0001.md: refused ('parts/0001.md' uses ZIP compression method 9; packages use deflate)"),
        ["bzip2"] = (1, "parts/0001.md: refused ('parts/0001.md' uses ZIP compression method 12; packages use deflate)"),
        ["bad CRC-32"] = (1, "parts/0001.md: refused (cannot read 'parts/0001.md' from the ZIP: Bad CRC-32 for file 'parts/0001.md')"),
        ["other local name"] = (1, "parts/0001.md: refused (cannot read 'parts/0001.md' from the ZIP: File name in directory 'parts/0001.md' and header b'parts/X001.md' differ.)"),
        ["overlapping entries"] = (1, "parts/0001.md: refused (cannot read 'parts/0001.md' from the ZIP: Overlapped entries: 'parts/0001.md' (possible zip bomb))"),
        ["larger size"] = (1, "parts/0001.md: size mismatch (expected "),
        ["end record count"] = (2, null),
        ["several disks"] = (2, null),
        ["short directory size"] = (2, null),
        ["NUL in a name"] = (2, null),
        ["backslash in a name"] = (2, null),
        ["duplicate in a name"] = (2, null),
        ["Unicode Path extra field"] = (2, null),
        ["names without the UTF-8 flag"] = (0, null),
        ["larger than the limit"] = (1, "parts/0001.md: refused (cannot read 'parts/0001.md': it is larger than 2147483520 bytes)"),
    };

    private IEnumerable<(string Name, string Path)> Cases(string python)
    {
        var sample = Sample();
        // Folders.
        yield return ("folder", Copy(sample, "folder"));
        var changed = Copy(sample, "changed");
        File.WriteAllText(Path.Combine(changed, "parts", "0002.md"),
            File.ReadAllText(Path.Combine(changed, "parts", "0002.md")).Replace("商品039", "商品X39", StringComparison.Ordinal));
        yield return ("changed part", changed);
        var missing = Copy(sample, "missing");
        File.Delete(Path.Combine(missing, "parts", "0003.md"));
        yield return ("missing part", missing);
        var folderInstead = Copy(sample, "folder-instead");
        File.Delete(Path.Combine(folderInstead, "parts", "0001.md"));
        Directory.CreateDirectory(Path.Combine(folderInstead, "parts", "0001.md"));
        yield return ("folder where a part should be", folderInstead);
        if (!OperatingSystem.IsWindows())
        {
            var linked = Copy(sample, "linked");
            File.Move(Path.Combine(linked, "parts", "0002.md"), Path.Combine(root, "outside.md"));
            File.CreateSymbolicLink(Path.Combine(linked, "parts", "0002.md"), Path.Combine(root, "outside.md"));
            yield return ("linked part", linked);
            var dangling = Copy(sample, "dangling");
            File.Delete(Path.Combine(dangling, "parts", "0002.md"));
            File.CreateSymbolicLink(Path.Combine(dangling, "parts", "0002.md"), Path.Combine(root, "nowhere.md"));
            yield return ("dangling link", dangling);
            var pipe = Copy(sample, "pipe");
            File.Delete(Path.Combine(pipe, "parts", "0003.md"));
            using (var mkfifo = Process.Start("mkfifo", Path.Combine(pipe, "parts", "0003.md"))!) mkfifo.WaitForExit();
            yield return ("pipe as a part", pipe);
            var pipeManifest = Copy(sample, "pipe-manifest");
            File.Delete(Path.Combine(pipeManifest, "manifest.json"));
            using (var mkfifo = Process.Start("mkfifo", Path.Combine(pipeManifest, "manifest.json"))!) mkfifo.WaitForExit();
            yield return ("pipe as the manifest", pipeManifest);
        }

        // ZIP archives, written by .NET and changed in place.
        var plain = Path.Combine(root, "plain.zip");
        ZipFile.CreateFromDirectory(sample, plain);
        yield return ("zip", plain);
        var part = "parts/0001.md";
        yield return ("version needed 10.0", Patched(plain, "version", bytes => SetCentral(bytes, part, 6, 100)));
        yield return ("encrypted", Patched(plain, "encrypted", bytes => { SetCentral(bytes, part, 8, Central(bytes, part, 8) | 1); SetLocal(bytes, part, 6, Local(bytes, part, 6) | 1); }));
        yield return ("patched data", Patched(plain, "patched", bytes => SetCentral(bytes, part, 8, Central(bytes, part, 8) | 0x20)));
        yield return ("strong encryption", Patched(plain, "strong", bytes => SetCentral(bytes, part, 8, Central(bytes, part, 8) | 0x40)));
        yield return ("deflate64", Patched(plain, "deflate64", bytes => { SetCentral(bytes, part, 10, 9); SetLocal(bytes, part, 8, 9); }));
        yield return ("bzip2", Patched(plain, "bzip2", bytes => { SetCentral(bytes, part, 10, 12); SetLocal(bytes, part, 8, 12); }));
        yield return ("bad CRC-32", Patched(plain, "crc", bytes => { SetCentral32(bytes, part, 16, Central32(bytes, part, 16) ^ 1); SetLocal32(bytes, part, 14, Local32(bytes, part, 14) ^ 1); }));
        yield return ("other local name", Patched(plain, "local-name", bytes => bytes[LocalHeader(bytes, part) + 30 + 6] = (byte)'X'));
        yield return ("overlapping entries", Patched(plain, "overlap", bytes => SetCentral32(bytes, part, 20, Central32(bytes, part, 20) + 4096)));
        yield return ("larger size", Patched(plain, "size", bytes => SetCentral32(bytes, part, 24, Central32(bytes, part, 24) + 1)));
        yield return ("end record count", Patched(plain, "count", bytes => { var end = EndRecord(bytes); Write16(bytes, end + 8, Read16(bytes, end + 8) + 1); Write16(bytes, end + 10, Read16(bytes, end + 10) + 1); }));
        yield return ("several disks", Patched(plain, "disks", bytes => Write16(bytes, EndRecord(bytes) + 4, 1)));
        yield return ("short directory size", Patched(plain, "directory-size", bytes => { var end = EndRecord(bytes); Write32(bytes, end + 12, Read32(bytes, end + 12) - 1); }));
        foreach (var (label, entry) in new[] { ("NUL", "parts/0009.md\0"), ("backslash", "parts\\0009.md"), ("duplicate", "parts/0002.md") })
        {
            var odd = Path.Combine(root, $"name-{label}.zip");
            File.Copy(plain, odd);
            using (var archive = ZipFile.Open(odd, ZipArchiveMode.Update))
            using (var writer = new StreamWriter(archive.CreateEntry(entry).Open()))
                writer.Write("odd");
            yield return ($"{label} in a name", odd);
        }
        // Python writes the archives .NET cannot: a Unicode Path extra field, and names without the UTF-8
        // flag (as macOS ditto writes them).
        var unicodePath = Path.Combine(root, "unicode-path.zip");
        RunScript(python, """
            import struct, sys, zipfile, zlib
            from pathlib import Path
            source, target = Path(sys.argv[1]), sys.argv[2]
            with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED) as archive:
                for path in sorted(p for p in source.rglob('*') if p.is_file()):
                    name = path.relative_to(source).as_posix()
                    info = zipfile.ZipInfo(name)
                    if name == 'review.md':
                        renamed = 'review-renamed.md'.encode()
                        info.extra = struct.pack('<HHBL', 0x7075, 5 + len(renamed), 1, zlib.crc32(name.encode())) + renamed
                    archive.writestr(info, path.read_bytes())
            """, sample, unicodePath);
        yield return ("Unicode Path extra field", unicodePath);
        var named = Copy(sample, "named");
        Directory.CreateDirectory(Path.Combine(named, "assets"));
        var image = Encoding.UTF8.GetBytes("# 画像\n");
        File.WriteAllBytes(Path.Combine(named, "assets", "画像.md"), image);
        var manifestJson = JsonNode.Parse(File.ReadAllText(Path.Combine(named, "manifest.json")))!.AsObject();
        manifestJson["files"]!.AsArray().Add(new JsonObject
        {
            ["path"] = "assets/画像.md", ["bytes"] = image.Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(image)),
        });
        File.WriteAllText(Path.Combine(named, "manifest.json"), manifestJson.ToJsonString());
        var noFlag = Path.Combine(root, "no-utf8-flag.zip");
        ZipFile.CreateFromDirectory(named, noFlag);
        var bytes = File.ReadAllBytes(noFlag);
        foreach (var (central, local) in Headers(bytes))
        {
            Write16(bytes, central + 8, Read16(bytes, central + 8) & ~0x800);
            Write16(bytes, local + 6, Read16(bytes, local + 6) & ~0x800);
        }
        File.WriteAllBytes(noFlag, bytes);
        yield return ("names without the UTF-8 flag", noFlag);
        // An entry that says it is larger than either reader reads, listed with that size in the manifest.
        var huge = Copy(sample, "huge");
        var hugeManifest = JsonNode.Parse(File.ReadAllText(Path.Combine(huge, "manifest.json")))!.AsObject();
        hugeManifest["files"]!.AsArray().Single(file => (string)file!["path"]! == part)!["bytes"] = 2_147_483_521L;
        File.WriteAllText(Path.Combine(huge, "manifest.json"), hugeManifest.ToJsonString());
        var hugeZip = Path.Combine(root, "huge.zip");
        ZipFile.CreateFromDirectory(huge, hugeZip);
        bytes = File.ReadAllBytes(hugeZip);
        SetCentral32(bytes, part, 24, 2_147_483_521u);
        File.WriteAllBytes(hugeZip, bytes);
        yield return ("larger than the limit", hugeZip);
    }

    internal static string? Python()
    {
        foreach (var name in new[] { "python3", "python" })
            try
            {
                using var process = Process.Start(new ProcessStartInfo(name, "--version") { RedirectStandardOutput = true, RedirectStandardError = true })!;
                if (process.WaitForExit(10_000) && process.ExitCode == 0) return name;
            }
            catch (System.ComponentModel.Win32Exception) { }
        return null;
    }

    internal static (int Code, string Output) RunPython(string python, string package)
    {
        var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add(Reader());
        start.ArgumentList.Add(package);
        start.ArgumentList.Add("--verify");
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000), "the Python reader did not finish");
        Assert.DoesNotContain("Traceback", error.Result, StringComparison.Ordinal);
        return (process.ExitCode, output.Result.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n'));
    }

    private static void RunScript(string python, string script, params string[] arguments)
    {
        var start = new ProcessStartInfo(python) { RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000) && process.ExitCode == 0, error);
    }

    private static string Sample() => Find(Path.Combine("tests", "DocRedock.Tests", "Fixtures", "AiPackage", "schema-2.2"));

    private static string Reader() => Find(Path.Combine("docs", "examples", "ai_package_reader.py"));

    private static string Find(string relative)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (Path.Combine(current.FullName, relative) is var path && (File.Exists(path) || Directory.Exists(path))) return path;
        throw new FileNotFoundException("Not found from the test directory.", relative);
    }

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

    private string Patched(string zip, string name, Action<byte[]> change)
    {
        var bytes = File.ReadAllBytes(zip);
        change(bytes);
        var target = Path.Combine(root, name + ".zip");
        File.WriteAllBytes(target, bytes);
        return target;
    }

    // The end record (no comment), then each central directory record and the local header it points to.
    private static int EndRecord(byte[] bytes) => bytes.Length - 22;

    private static IEnumerable<(int Central, int Local)> Headers(byte[] bytes)
    {
        var end = EndRecord(bytes);
        for (int position = (int)Read32(bytes, end + 16), stop = position + (int)Read32(bytes, end + 12); position < stop;)
        {
            yield return (position, (int)Read32(bytes, position + 42));
            position += 46 + Read16(bytes, position + 28) + Read16(bytes, position + 30) + Read16(bytes, position + 32);
        }
    }

    private static int CentralHeader(byte[] bytes, string name) => Headers(bytes)
        .First(header => Encoding.UTF8.GetString(bytes, header.Central + 46, Read16(bytes, header.Central + 28)) == name).Central;

    private static int LocalHeader(byte[] bytes, string name) => (int)Read32(bytes, CentralHeader(bytes, name) + 42);

    private static int Central(byte[] bytes, string name, int offset) => Read16(bytes, CentralHeader(bytes, name) + offset);
    private static void SetCentral(byte[] bytes, string name, int offset, int value) => Write16(bytes, CentralHeader(bytes, name) + offset, value);
    private static uint Central32(byte[] bytes, string name, int offset) => Read32(bytes, CentralHeader(bytes, name) + offset);
    private static void SetCentral32(byte[] bytes, string name, int offset, uint value) => Write32(bytes, CentralHeader(bytes, name) + offset, value);
    private static int Local(byte[] bytes, string name, int offset) => Read16(bytes, LocalHeader(bytes, name) + offset);
    private static void SetLocal(byte[] bytes, string name, int offset, int value) => Write16(bytes, LocalHeader(bytes, name) + offset, value);
    private static uint Local32(byte[] bytes, string name, int offset) => Read32(bytes, LocalHeader(bytes, name) + offset);
    private static void SetLocal32(byte[] bytes, string name, int offset, uint value) => Write32(bytes, LocalHeader(bytes, name) + offset, value);
    private static int Read16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static void Write16(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), (ushort)value);
    private static uint Read32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void Write32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
