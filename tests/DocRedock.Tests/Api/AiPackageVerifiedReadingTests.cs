using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DocRedock.Api;

namespace DocRedock.Tests.Api;

/// <summary>
/// One-call verified reading (<see cref="AiPackageManifestReader.ReadVerifiedPartAsync"/>), the review
/// state of a package, and package files the readers refuse or report whatever the checks before them
/// (Zip64 records, names that would print as something else).
/// </summary>
public sealed class AiPackageVerifiedReadingTests : IDisposable
{
    private static readonly string[] CostCells = ["A2", "B2", "D2", "F2", "A4", "B4", "D4", "F4"];
    private readonly string root = Directory.CreateTempSubdirectory("docredock-ai-verified-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_verified_part_comes_with_its_cells_and_the_review_state(bool zip)
    {
        var sample = Sample("2.1");
        var path = zip ? Zip(sample) : sample;
        var verified = await AiPackageManifestReader.ReadVerifiedPartAsync(path, "part-0003");
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(sample, "parts", "0003.md")), verified.Markdown);
        Assert.Equal(("part-0003", "A4:F4", "A2:F2"), (verified.Part.Id, verified.Part.TableBlock!.RowRange, verified.Part.TableBlock.HeaderRange));
        Assert.Equal("Summary", Assert.Single(verified.Part.Locations).SheetName);
        var group = Assert.Single(verified.Sources);
        Assert.Equal(("source-0001", 8), (group.SourceId, group.NodeIds.Count));
        Assert.Equal(CostCells, group.CellAddresses);
        Assert.Equal(new AiPackageReview(false, false, false), verified.Review);
        Assert.Equal("workbook_base.xlsx", verified.Manifest.SourceFileName);
        // Q2 is the third column shown and D4 the third cell of the row, not C4.
        var row = verified.Markdown.Split('\n').Single(line => line.StartsWith("| Cost", StringComparison.Ordinal));
        Assert.Equal("80", row.Split('|')[3].Trim());
        Assert.Equal("D4", group.CellAddresses![4 + 2]);
    }

    [Fact]
    public async Task Schema_1_parts_carry_their_inline_sources_and_an_older_report()
    {
        var verified = await AiPackageManifestReader.ReadVerifiedPartAsync(Sample("1.0"), "part-0001");
        Assert.Equal(["source-0001", "source-0002"], verified.Sources.Select(group => group.SourceId));
        Assert.Equal(new AiPackageReview(false, null, false), verified.Review);
        Assert.Equal(new AiPackageReview(false, null, false), await AiPackageManifestReader.ReadReviewAsync(Sample("1.0"), verified.Manifest));
    }

    [Fact]
    public async Task A_changed_package_gives_its_problems_and_no_text()
    {
        var copy = Copy(Sample("2.1"));
        var part = Path.Combine(copy, "parts", "0003.md");
        await File.WriteAllTextAsync(part, (await File.ReadAllTextAsync(part)).Replace("| 80 |", "| 99 |", StringComparison.Ordinal));
        var refused = await Assert.ThrowsAsync<AiPackageVerificationException>(() => AiPackageManifestReader.ReadVerifiedPartAsync(copy, "part-0003"));
        var problem = Assert.Single(refused.Problems);
        Assert.StartsWith("parts/0003.md: sha256 mismatch (expected de7e86cb", problem, StringComparison.Ordinal);
        Assert.Contains(problem, refused.Message, StringComparison.Ordinal);
        // An unlisted file fails verification as well.
        var planted = Copy(Sample("2.1"));
        await File.WriteAllTextAsync(Path.Combine(planted, "parts", "0007.md"), "# planted\n");
        refused = await Assert.ThrowsAsync<AiPackageVerificationException>(() => AiPackageManifestReader.ReadVerifiedPartAsync(planted, "part-0003"));
        Assert.Equal(["parts/0007.md: not listed in the manifest"], refused.Problems);
    }

    [Fact]
    public async Task Unknown_parts_malformed_indexes_and_review_required_are_told_apart()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => AiPackageManifestReader.ReadVerifiedPartAsync(Sample("2.1"), "part-9999"));
        var broken = Copy(Sample("2.1"));
        Relist(broken, "source-index.json", index => index["parts"]![2]!["sources"]![0]!["cell_addresses"] = new JsonArray("D4"));
        var invalid = await Assert.ThrowsAsync<InvalidDataException>(() => AiPackageManifestReader.ReadVerifiedPartAsync(broken, "part-0003"));
        Assert.Contains("cell_addresses must hold one address or null for each of its 8 node_ids", invalid.Message, StringComparison.Ordinal);
        var review = Copy(Sample("2.1"));
        Relist(review, "report.json", report => report["review"]!["required"] = true);
        var verified = await AiPackageManifestReader.ReadVerifiedPartAsync(review, "part-0003");
        Assert.True(verified.Review.Required);
        Assert.True(await AiPackageManifestReader.ReadReviewRequiredAsync(review, verified.Manifest));
        Relist(review, "report.json", report => report["summary"]!["review_items_detected"] = "yes");
        invalid = await Assert.ThrowsAsync<InvalidDataException>(() => AiPackageManifestReader.ReadReviewAsync(review, verified.Manifest));
        Assert.Equal("AI package report.json: summary.review_items_detected must be true or false, got a string.", invalid.Message);
    }

    [Fact]
    public async Task Zip64_end_records_must_be_where_their_locator_points()
    {
        var plain = Zip(Sample("2.1"));
        var bytes = await File.ReadAllBytesAsync(plain);
        var end = bytes.Length - 22;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(end + 10));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 12));
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16));
        byte[] Archive(ulong pointer, ulong recordSize = 44)
        {
            var record = new byte[56];
            "PK\u0006\u0006"u8.CopyTo(record);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(4), recordSize);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12), 45);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(14), 45);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(24), count);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), count);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(40), size);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(48), offset);
            var locator = new byte[20];
            "PK\u0006\u0007"u8.CopyTo(locator);
            BinaryPrimitives.WriteUInt64LittleEndian(locator.AsSpan(8), pointer);
            BinaryPrimitives.WriteUInt32LittleEndian(locator.AsSpan(16), 1);
            var endRecord = new byte[22];
            "PK\u0005\u0006"u8.CopyTo(endRecord);
            BinaryPrimitives.WriteUInt16LittleEndian(endRecord.AsSpan(8), 0xFFFF);
            BinaryPrimitives.WriteUInt16LittleEndian(endRecord.AsSpan(10), 0xFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(endRecord.AsSpan(12), 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(endRecord.AsSpan(16), 0xFFFFFFFF);
            return [.. bytes.AsSpan(0, end), .. record, .. locator, .. endRecord];
        }
        var valid = Path.Combine(root, "zip64.zip");
        await File.WriteAllBytesAsync(valid, Archive((ulong)end));
        Assert.Empty(await AiPackageManifestReader.VerifyFilesAsync(valid, await AiPackageManifestReader.ReadAsync(valid)));
        foreach (var (pointer, recordSize, message) in new[]
                 {
                     (ulong.MaxValue, 44UL, "the ZIP's Zip64 end record locator points past the record"),
                     ((ulong)long.MaxValue, 44UL, "the ZIP's Zip64 end record locator points past the record"),
                     (0UL, 44UL, "the ZIP's Zip64 end record is not where its locator points"),
                     ((ulong)end, 45UL, "the ZIP's Zip64 end record does not end where its locator begins"),
                 })
        {
            var path = Path.Combine(root, $"zip64-{pointer}-{recordSize}.zip");
            await File.WriteAllBytesAsync(path, Archive(pointer, recordSize));
            var refused = await Assert.ThrowsAsync<InvalidDataException>(() => AiPackageManifestReader.ReadAsync(path));
            Assert.Equal(message, refused.Message);
        }
    }

    [Fact]
    public async Task Names_of_unlisted_files_are_shown_escaped()
    {
        if (OperatingSystem.IsWindows()) return;   // Windows file names cannot hold control characters
        var copy = Copy(Sample("2.1"));
        await File.WriteAllTextAsync(Path.Combine(copy, "parts", "x\u001b[2Jfiles ok\n.md"), "planted");
        var manifest = await AiPackageManifestReader.ReadAsync(copy);
        Assert.Equal(["parts/x\\x1b[2Jfiles ok\\x0a.md: not listed in the manifest"], await AiPackageManifestReader.VerifyFilesAsync(copy, manifest));
    }

    [Fact]
    public async Task A_linked_folder_is_reported_by_its_own_name_once()
    {
        if (OperatingSystem.IsWindows()) return;
        var copy = Copy(Sample("2.1"));
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "b.png"), "pixel");
        Directory.CreateSymbolicLink(Path.Combine(copy, "assets"), outside);
        var manifest = await AiPackageManifestReader.ReadAsync(copy);
        Assert.Equal(["assets: not listed in the manifest"], await AiPackageManifestReader.VerifyFilesAsync(copy, manifest));
        // A link where a folder of listed files belongs refuses those files and is not reported again.
        var linkedParts = Copy(Sample("2.1"));
        var parts = Path.Combine(root, "parts-outside");
        Directory.Move(Path.Combine(linkedParts, "parts"), parts);
        Directory.CreateSymbolicLink(Path.Combine(linkedParts, "parts"), parts);
        var problems = await AiPackageManifestReader.VerifyFilesAsync(linkedParts, await AiPackageManifestReader.ReadAsync(linkedParts));
        Assert.Equal(6, problems.Count);
        Assert.All(problems, problem => Assert.Contains(": refused (package path is a link: 'parts/", problem, StringComparison.Ordinal));
    }

    private static string Sample(string schema)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "AiPackage", "schema-" + schema) is var path && Directory.Exists(path))
                return path;
        throw new DirectoryNotFoundException("AI package sample not found: " + schema);
    }

    private string Copy(string sample)
    {
        var copy = Path.Combine(root, "copy-" + Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(sample, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(sample, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return copy;
    }

    private string Zip(string directory)
    {
        var zip = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(directory, zip);
        return zip;
    }

    // Changes a package JSON file and lists its new size and hash, as a consistent package would.
    private static void Relist(string package, string relative, Action<JsonNode> change)
    {
        var path = Path.Combine(package, relative);
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        change(document);
        var bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        File.WriteAllBytes(path, bytes);
        var manifestPath = Path.Combine(package, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        var entry = manifest["files"]!.AsArray().Single(file => (string)file!["path"]! == relative)!;
        entry["bytes"] = bytes.Length;
        entry["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        File.WriteAllText(manifestPath, manifest.ToJsonString());
    }
}
