using System.IO.Compression;
using System.Text;
using DocRedock.RoundTrip;

namespace DocRedock.Tests.RoundTrip;

public sealed class SidecarContainerTests
{
    [Fact]
    public async Task Pack_and_unpack_in_place_preserve_the_workspace_and_markdown_bytes()
    {
        using var fixture = new Fixture();
        await fixture.CreateAsync();
        var markdown = await File.ReadAllBytesAsync(fixture.MarkdownPath);
        var sidecarFiles = Directory.EnumerateFiles(fixture.SidecarPath, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(fixture.SidecarPath, path).Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);

        var packed = await SidecarContainer.PackInPlaceAsync(fixture.SidecarPath, fixture.MarkdownPath);

        Assert.Equal(fixture.SidecarPath, packed);
        Assert.True(File.Exists(packed));
        Assert.Equal(SidecarForm.Zip, SidecarContainer.Detect(packed));
        Assert.Equal(markdown, await File.ReadAllBytesAsync(fixture.MarkdownPath));

        var unpacked = await SidecarContainer.UnpackInPlaceAsync(packed, fixture.MarkdownPath);

        Assert.Equal(fixture.SidecarPath, unpacked);
        Assert.True(Directory.Exists(unpacked));
        var unpackedFiles = Directory.EnumerateFiles(unpacked, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(unpacked, path).Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);
        Assert.Equal(sidecarFiles.Keys.Order(StringComparer.Ordinal), unpackedFiles.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in sidecarFiles)
            Assert.Equal(bytes, unpackedFiles[path]);
        Assert.Equal(markdown, await File.ReadAllBytesAsync(fixture.MarkdownPath));
        var workspace = await RoundTripWorkspace.OpenAsync(unpacked);
        Assert.True((await workspace.VerifyAsync(fixture.MarkdownPath)).IsValid);
    }

    [Fact]
    public async Task Pack_refuses_an_invalid_workspace_without_replacing_the_directory()
    {
        using var fixture = new Fixture();
        await fixture.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(fixture.SidecarPath, "source", "original.docx"), "tampered");

        await Assert.ThrowsAsync<WorkspaceIntegrityException>(() => SidecarContainer.PackInPlaceAsync(fixture.SidecarPath, fixture.MarkdownPath));

        Assert.True(Directory.Exists(fixture.SidecarPath));
        Assert.False(File.Exists(fixture.SidecarPath));
    }

    [Fact]
    public async Task Open_zip_sidecar_extracts_temporarily_and_removes_it_on_dispose()
    {
        using var fixture = new Fixture();
        await fixture.CreateAsync();
        await SidecarContainer.PackInPlaceAsync(fixture.SidecarPath, fixture.MarkdownPath);

        string root;
        await using (var lease = await SidecarContainer.OpenAsync(fixture.SidecarPath))
        {
            root = lease.RootPath;
            Assert.Equal(SidecarForm.Zip, lease.Form);
            Assert.True(lease.IsTemporary);
            Assert.True(File.Exists(Path.Combine(root, "manifest.json")));
        }
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Detect_distinguishes_directory_zip_bundle_and_invalid_containers()
    {
        using var fixture = new Fixture();
        await fixture.CreateAsync();
        Assert.Equal(SidecarForm.Directory, SidecarContainer.Detect(fixture.SidecarPath));

        await SidecarContainer.PackInPlaceAsync(fixture.SidecarPath, fixture.MarkdownPath);
        Assert.Equal(SidecarForm.Zip, SidecarContainer.Detect(fixture.SidecarPath));

        var bundle = Path.Combine(fixture.Root, "source.drmdpkg");
        await RoundTripPackage.PackAsync(fixture.MarkdownPath, fixture.SidecarPath, bundle);
        Assert.True(SidecarContainer.IsBundle(bundle));
        Assert.Throws<InvalidDataException>(() => SidecarContainer.Detect(bundle));

        var invalid = Path.Combine(fixture.Root, "invalid.drmd");
        await File.WriteAllTextAsync(invalid, "not a zip");
        Assert.Throws<InvalidDataException>(() => SidecarContainer.Detect(invalid));
    }

    [Fact]
    public async Task Open_rejects_zip_entries_that_escape_the_sidecar_root()
    {
        using var fixture = new Fixture();
        var zip = Path.Combine(fixture.Root, "unsafe.drmd");
        await using (var stream = File.Create(zip))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            await using (var manifest = new StreamWriter(archive.CreateEntry("manifest.json").Open(), Encoding.UTF8))
                await manifest.WriteAsync("{}");
            await using var writer = new StreamWriter(archive.CreateEntry("../escape.txt").Open(), Encoding.UTF8);
            await writer.WriteAsync("unsafe");
        }

        Assert.Equal(SidecarForm.Zip, SidecarContainer.Detect(zip));
        await Assert.ThrowsAsync<InvalidDataException>(() => SidecarContainer.OpenAsync(zip));
    }

    [Fact]
    public async Task Open_rejects_zip_with_more_than_the_entry_limit()
    {
        using var fixture = new Fixture();
        var zip = Path.Combine(fixture.Root, "too-many.drmd");
        await using (var stream = File.Create(zip))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            archive.CreateEntry("manifest.json");
            for (var index = 0; index < 50_000; index++)
                archive.CreateEntry($"entries/{index:D5}.bin");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => SidecarContainer.OpenAsync(zip));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "docredock-sidecar-tests", Guid.NewGuid().ToString("N"));
        public string SourcePath => Path.Combine(Root, "original.docx");
        public string MarkdownPath => Path.Combine(Root, "source.md");
        public string SidecarPath => Path.Combine(Root, "source.drmd");

        public Fixture() => Directory.CreateDirectory(Root);

        public async Task CreateAsync()
        {
            await File.WriteAllTextAsync(SourcePath, "original");
            await File.WriteAllTextAsync(MarkdownPath, "# Original\n", new UTF8Encoding(false));
            await RoundTripWorkspace.CreateAsync(SidecarPath, SourcePath, new RoundTripWorkspaceOptions { MarkdownPath = MarkdownPath });
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
