using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.RoundTrip;

namespace DocRedock.Tests.RoundTrip;

public sealed class RoundTripWorkspaceTests
{
    [Fact]
    public async Task Create_writes_manifest_sidecar_and_original_hash()
    {
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(fixture.SourcePath, Encoding.UTF8.GetBytes("source bytes\0\u3042"));
        await File.WriteAllTextAsync(fixture.MarkdownPath, "# Hello\n", new UTF8Encoding(false));

        var workspace = await RoundTripWorkspace.CreateAsync(fixture.WorkspacePath, fixture.SourcePath,
            new RoundTripWorkspaceOptions { MarkdownPath = fixture.MarkdownPath });

        Assert.True(File.Exists(Path.Combine(fixture.WorkspacePath, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(fixture.WorkspacePath, "source", "original.docx")));
        Assert.True(File.Exists(Path.Combine(fixture.WorkspacePath, "checksums.json")));
        Assert.True(File.Exists(Path.Combine(fixture.WorkspacePath, "reports", "export-report.md")));
        Assert.Equal("1.1", workspace.Manifest.SchemaVersion);
        Assert.Equal(Hash(fixture.SourcePath), workspace.Manifest.Source.Sha256);
        Assert.True((await workspace.VerifyAsync(fixture.MarkdownPath)).IsValid);
    }

    [Fact]
    public async Task Verify_detects_source_and_markdown_tampering()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "original");
        await File.WriteAllTextAsync(fixture.MarkdownPath, "# Original\n");
        var workspace = await RoundTripWorkspace.CreateAsync(fixture.WorkspacePath, fixture.SourcePath,
            new RoundTripWorkspaceOptions { MarkdownPath = fixture.MarkdownPath });

        await File.AppendAllTextAsync(Path.Combine(fixture.WorkspacePath, "source", "original.docx"), "tampered");
        await File.AppendAllTextAsync(fixture.MarkdownPath, "changed\n");

        var report = await workspace.VerifyAsync(fixture.MarkdownPath, true)
            ?? throw new InvalidOperationException("Integrity verification returned no report.");
        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "source.hash");
        Assert.Contains(report.Issues, issue => issue.Code == "markdown.hash");
    }

    [Fact]
    public async Task RestoreOriginal_is_F0_byte_identical_and_atomic()
    {
        using var fixture = new Fixture();
        var bytes = new byte[] { 0, 1, 2, 3, 255, 4 };
        await File.WriteAllBytesAsync(fixture.SourcePath, bytes);
        await File.WriteAllTextAsync(fixture.MarkdownPath, "# Original\n");
        var workspace = await RoundTripWorkspace.CreateAsync(fixture.WorkspacePath, fixture.SourcePath,
            new RoundTripWorkspaceOptions { MarkdownPath = fixture.MarkdownPath });

        var destination = Path.Combine(fixture.Root, "restored.docx");
        var result = await workspace.RestoreOriginalAsync(destination);

        Assert.Equal("F0", result.FidelityLevel);
        Assert.True(result.ByteIdentical);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.True(File.Exists(Path.Combine(fixture.WorkspacePath, "reports", "restore-report.json")));
        Assert.True(File.Exists(Path.Combine(fixture.WorkspacePath, "reports", "restore-report.md")));
        await Assert.ThrowsAsync<IOException>(() => workspace.RestoreOriginalAsync(destination));
    }

    [Fact]
    public async Task Strict_verify_rejects_missing_markdown_binding()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "original");
        await File.WriteAllTextAsync(fixture.MarkdownPath, "# Original\n");
        var workspace = await RoundTripWorkspace.CreateAsync(fixture.WorkspacePath, fixture.SourcePath,
            new RoundTripWorkspaceOptions { MarkdownPath = fixture.MarkdownPath });

        File.Delete(fixture.MarkdownPath);

        await Assert.ThrowsAsync<WorkspaceIntegrityException>(() => workspace.VerifyStrictAsync(fixture.MarkdownPath));
    }

    [Fact]
    public async Task Verify_rejects_absolute_checksum_paths()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "original");
        await File.WriteAllTextAsync(fixture.MarkdownPath, "# Original\n");
        var workspace = await RoundTripWorkspace.CreateAsync(fixture.WorkspacePath, fixture.SourcePath,
            new RoundTripWorkspaceOptions { MarkdownPath = fixture.MarkdownPath });
        await File.WriteAllTextAsync(
            Path.Combine(fixture.WorkspacePath, "checksums.json"),
            "{\"/outside-workspace\":\"00\"}\n");

        var report = await workspace.VerifyAsync(fixture.MarkdownPath);

        Assert.Contains(report.Issues, issue => issue.Code == "checksums.path");
    }

    [Fact]
    public async Task Visible_projection_does_not_require_map_entries_for_hidden_nodes()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "original");
        await File.WriteAllTextAsync(fixture.MarkdownPath, "# Visible\n");
        var workspace = await RoundTripWorkspace.CreateAsync(fixture.WorkspacePath, fixture.SourcePath,
            new RoundTripWorkspaceOptions { MarkdownPath = fixture.MarkdownPath, ContentPolicy = "visible" });
        var graph = new DocumentGraph("1.1", "doc_test", DocumentFormatKind.Docx,
        [
            new DocumentPartition("part-1", 0,
            [
                new DocumentNode("visible", NodeKind.Paragraph, null, 0, ContentLayer.Body, new TextNodeContent("Visible")),
                new DocumentNode("hidden", NodeKind.Paragraph, null, 1, ContentLayer.Hidden, new TextNodeContent("")),
            ])
        ]);
        await workspace.WriteGraphAsync(graph);
        await workspace.WriteProjectionMapAsync([
            JsonSerializer.Serialize(new { projection_id = workspace.Manifest.Projection.ProjectionId, node_id = "visible" })
        ]);

        Assert.True((await workspace.VerifyAsync(fixture.MarkdownPath)).IsValid);
    }

    [Fact]
    public async Task Pack_and_unpack_preserve_a_verified_workspace()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "original");
        await File.WriteAllTextAsync(fixture.MarkdownPath, "# Original\n");
        await RoundTripWorkspace.CreateAsync(fixture.WorkspacePath, fixture.SourcePath,
            new RoundTripWorkspaceOptions { MarkdownPath = fixture.MarkdownPath });
        var package = Path.Combine(fixture.Root, "source.drmdpkg");
        var unpackedDirectory = Path.Combine(fixture.Root, "unpacked");

        await RoundTripPackage.PackAsync(fixture.MarkdownPath, package);
        var unpacked = await RoundTripPackage.UnpackAsync(package, unpackedDirectory);
        var workspace = await RoundTripWorkspace.OpenAsync(unpacked.WorkspacePath);

        Assert.True((await workspace.VerifyAsync(unpacked.MarkdownPath)).IsValid);
        Assert.Equal(await File.ReadAllBytesAsync(fixture.MarkdownPath), await File.ReadAllBytesAsync(unpacked.MarkdownPath));
    }

    [Fact]
    public async Task Unpack_rejects_path_traversal()
    {
        using var fixture = new Fixture();
        var package = Path.Combine(fixture.Root, "unsafe.drmdpkg");
        await using (var output = File.Create(package))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            archive.CreateEntry("../outside.txt");

        await Assert.ThrowsAsync<InvalidDataException>(() => RoundTripPackage.UnpackAsync(
            package,
            Path.Combine(fixture.Root, "unsafe-output")));
    }

    [Fact]
    public async Task Unpack_rejects_a_tampered_workspace_before_publishing_output()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "original");
        await File.WriteAllTextAsync(fixture.MarkdownPath, "# Original\n");
        await RoundTripWorkspace.CreateAsync(fixture.WorkspacePath, fixture.SourcePath,
            new RoundTripWorkspaceOptions { MarkdownPath = fixture.MarkdownPath });
        var package = Path.Combine(fixture.Root, "tampered.drmdpkg");
        await RoundTripPackage.PackAsync(fixture.MarkdownPath, package);
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            var original = archive.GetEntry("source.drmd/source/original.docx")!;
            original.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("source.drmd/source/original.docx").Open());
            writer.Write("tampered");
        }
        var output = Path.Combine(fixture.Root, "tampered-output");

        await Assert.ThrowsAsync<WorkspaceIntegrityException>(() => RoundTripPackage.UnpackAsync(package, output));

        Assert.False(Directory.Exists(output));
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "docredock-tests", Guid.NewGuid().ToString("N"));
        public string SourcePath => Path.Combine(Root, "source.docx");
        public string MarkdownPath => Path.Combine(Root, "source.md");
        public string WorkspacePath => Path.Combine(Root, "source.drmd");
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
