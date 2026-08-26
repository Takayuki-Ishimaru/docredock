using System.IO.Compression;
using System.Text;
using DocRedock.Cli;

namespace DocRedock.Tests.Cli;

[Collection("Environment variables")]
public sealed class CliApplicationTests : IDisposable
{
    private readonly string? previousExperimental = Environment.GetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL");

    public CliApplicationTests() => Environment.SetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL", "1");

    public void Dispose() => Environment.SetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL", previousExperimental);
    [Fact]
    public async Task Readable_profile_writes_markdown_without_a_sidecar()
    {
        using var fixture = new Fixture();
        fixture.CreateDocx();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var app = new CliApplication(stdout, stderr);

        var result = await app.RunAsync(["export", fixture.SourcePath, "--output", fixture.MarkdownPath, "--profile", "readable"]);

        Assert.Equal((int)ExitCode.Success, result);
        Assert.True(File.Exists(fixture.MarkdownPath));
        Assert.False(Directory.Exists(Path.ChangeExtension(fixture.MarkdownPath, ".drmd")));
        Assert.Contains("Readable Markdown", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Sidecar:", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("drmd_schema", await File.ReadAllTextAsync(fixture.MarkdownPath), StringComparison.Ordinal);
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task Readable_profile_can_embed_images_without_creating_an_asset_directory()
    {
        using var fixture = new Fixture();
        fixture.CreateDocx(withImage: true);
        using (var archive = ZipFile.Open(fixture.SourcePath, ZipArchiveMode.Update))
        await using (var media = archive.CreateEntry("word/media/image1.png").Open())
            await media.WriteAsync(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var app = new CliApplication(stdout, stderr);

        var result = await app.RunAsync(["export", fixture.SourcePath, "--output", fixture.MarkdownPath, "--profile", "readable", "--ocr", "off", "--embed-images"]);

        Assert.Equal((int)ExitCode.Success, result);
        var text = await File.ReadAllTextAsync(fixture.MarkdownPath);
        Assert.Contains("data:image/png;base64,iVBORw0KGgo=", text, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "projection.assets")));
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task Rules_prints_the_embedded_ai_editing_contract()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var app = new CliApplication(stdout, stderr);

        var result = await app.RunAsync(["rules"]);

        Assert.Equal((int)ExitCode.Success, result);
        Assert.Contains("# DRMD Markdown AI編集ルール", stdout.ToString());
        Assert.Contains("verify", stdout.ToString());
        Assert.Contains("diff", stdout.ToString());
        Assert.Contains(".drmd", stdout.ToString(), StringComparison.Ordinal);
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task Help_uses_docredock_sidecar_extensions()
    {
        var stdout = new StringWriter();
        var app = new CliApplication(stdout, new StringWriter());

        Assert.Equal((int)ExitCode.Success, await app.RunAsync(["help"]));

        Assert.Contains("DocRedock 0.1.4", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("--content-policy visible|complete|sanitized", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("file.drmd", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("file.drmdpkg", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("licenses", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("--strict", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_is_derived_from_the_release_assembly()
    {
        var stdout = new StringWriter();
        var app = new CliApplication(stdout, new StringWriter());

        Assert.Equal((int)ExitCode.Success, await app.RunAsync(["--version"]));
        Assert.StartsWith("DocRedock 0.1.4", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Experimental_commands_require_explicit_environment_opt_in()
    {
        Environment.SetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL", null);
        try
        {
            var stderr = new StringWriter();
            var result = await new CliApplication(new StringWriter(), stderr).RunAsync(["restore", "missing.md"]);

            Assert.Equal((int)ExitCode.Unsupported, result);
            Assert.Contains("DOCREDOCK_ENABLE_EXPERIMENTAL=1", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL", "1");
        }
    }

    [Fact]
    public async Task Export_requires_force_before_replacing_existing_outputs()
    {
        using var fixture = new Fixture();
        fixture.CreateDocx();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var app = new CliApplication(stdout, stderr);

        Assert.Equal((int)ExitCode.Success,
            await app.RunAsync(["export", fixture.SourcePath, "--output", fixture.MarkdownPath, "--profile", "readable"]));
        var refused = await app.RunAsync(["export", fixture.SourcePath, "--output", fixture.MarkdownPath, "--profile", "readable"]);
        var forced = await app.RunAsync(["export", fixture.SourcePath, "--output", fixture.MarkdownPath, "--profile", "readable", "--force", "--quiet"]);

        Assert.Equal((int)ExitCode.InvalidInput, refused);
        Assert.Equal((int)ExitCode.Success, forced);
        Assert.Contains("--force", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Force_preserves_the_previous_output_when_conversion_fails()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "not-an-openxml-package");
        await File.WriteAllTextAsync(fixture.MarkdownPath, "previous-good-output");
        var app = new CliApplication(new StringWriter(), new StringWriter());

        var result = await app.RunAsync(["export", fixture.SourcePath, "--output", fixture.MarkdownPath, "--profile", "readable", "--force"]);

        Assert.NotEqual((int)ExitCode.Success, result);
        Assert.Equal("previous-good-output", await File.ReadAllTextAsync(fixture.MarkdownPath));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root, ".docredock-stage-*"));
    }

    [Fact]
    public async Task Export_verify_and_F0_restore_are_a_byte_identical_vertical_slice()
    {
        using var fixture = new Fixture();
        fixture.CreateDocx();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var app = new CliApplication(stdout, stderr);

        var export = await app.RunAsync(["export", fixture.SourcePath, "--output", fixture.MarkdownPath]);
        var verify = await app.RunAsync(["verify", fixture.MarkdownPath]);
        var restore = await app.RunAsync(["restore", fixture.MarkdownPath, "--output", fixture.RestoredPath]);

        Assert.Equal((int)ExitCode.Success, export);
        Assert.Equal((int)ExitCode.Success, verify);
        Assert.Equal((int)ExitCode.Success, restore);
        Assert.Equal(await File.ReadAllBytesAsync(fixture.SourcePath), await File.ReadAllBytesAsync(fixture.RestoredPath));
        Assert.Contains("Workspace integrity: OK", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("Restore readiness: F0 eligible.", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("Fidelity: F0", stdout.ToString());
        Assert.True(Directory.Exists(Path.ChangeExtension(fixture.MarkdownPath, ".drmd")));
        Assert.True(string.IsNullOrEmpty(stderr.ToString()), stderr.ToString());
    }

    [Fact]
    public async Task Edited_projection_is_restored_with_F1()
    {
        using var fixture = new Fixture();
        fixture.CreateDocx();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var app = new CliApplication(stdout, stderr);
        Assert.Equal((int)ExitCode.Success,
            await app.RunAsync(["export", fixture.SourcePath, "--output", fixture.MarkdownPath]));
        var markdown = await File.ReadAllTextAsync(fixture.MarkdownPath, Encoding.UTF8);
        await File.WriteAllTextAsync(
            fixture.MarkdownPath,
            markdown.Replace("Before", "After", StringComparison.Ordinal),
            new UTF8Encoding(false));

        var verify = await app.RunAsync(["verify", fixture.MarkdownPath]);
        var restore = await app.RunAsync(["restore", fixture.MarkdownPath, "--output", fixture.RestoredPath]);

        Assert.Equal((int)ExitCode.SuccessWithWarnings, verify);
        Assert.Contains("Edit applicability: NOT CHECKED", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("Restore readiness: NOT CHECKED.", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal((int)ExitCode.Success, restore);
        Assert.True(File.Exists(fixture.RestoredPath));
        using var archive = ZipFile.OpenRead(fixture.RestoredPath);
        using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open(), Encoding.UTF8);
        Assert.Contains("After", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Sidecar_can_be_packed_verified_restored_and_unpacked_in_place()
    {
        using var fixture = new Fixture();
        fixture.CreateDocx();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var app = new CliApplication(stdout, stderr);
        var sidecar = Path.ChangeExtension(fixture.MarkdownPath, ".drmd");

        Assert.Equal((int)ExitCode.Success, await app.RunAsync(["export", fixture.SourcePath, "--output", fixture.MarkdownPath]));
        var markdown = await File.ReadAllBytesAsync(fixture.MarkdownPath);
        Assert.True(Directory.Exists(sidecar));

        Assert.Equal((int)ExitCode.Success, await app.RunAsync(["pack", fixture.MarkdownPath, "--sidecar", "--in-place"]));
        Assert.True(File.Exists(sidecar));
        Assert.Equal((int)ExitCode.Success, await app.RunAsync(["verify", fixture.MarkdownPath]));
        Assert.Equal((int)ExitCode.Success, await app.RunAsync(["verify", sidecar]));
        Assert.Equal((int)ExitCode.Success, await app.RunAsync(["restore", fixture.MarkdownPath, "--output", fixture.RestoredPath]));
        Assert.Contains("SidecarZipFormReadOnly", stdout.ToString(), StringComparison.Ordinal);

        Assert.Equal((int)ExitCode.Success, await app.RunAsync(["unpack", sidecar, "--in-place"]));
        Assert.True(Directory.Exists(sidecar));
        Assert.Equal(markdown, await File.ReadAllBytesAsync(fixture.MarkdownPath));
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task Export_can_write_a_zip_form_sidecar()
    {
        using var fixture = new Fixture();
        fixture.CreateDocx();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var app = new CliApplication(stdout, stderr);
        var sidecar = Path.ChangeExtension(fixture.MarkdownPath, ".drmd");

        Assert.Equal((int)ExitCode.Success,
            await app.RunAsync(["export", fixture.SourcePath, "--output", fixture.MarkdownPath, "--sidecar", "zip"]));

        Assert.True(File.Exists(sidecar));
        Assert.False(Directory.Exists(sidecar));
        Assert.Contains("(zip)", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal((int)ExitCode.Success, await app.RunAsync(["verify", fixture.MarkdownPath]));
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task Sidecar_pack_requires_an_explicit_destination_mode()
    {
        using var fixture = new Fixture();
        fixture.CreateDocx();
        var app = new CliApplication(new StringWriter(), new StringWriter());
        Assert.Equal((int)ExitCode.Success, await app.RunAsync(["export", fixture.SourcePath, "--output", fixture.MarkdownPath]));

        Assert.Equal((int)ExitCode.InvalidInput, await app.RunAsync(["pack", fixture.MarkdownPath, "--sidecar"]));
    }

    [Fact]
    public async Task Staged_commit_requires_every_output_before_replacing_existing_targets()
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Root, "projection.md");
        await File.WriteAllTextAsync(target, "previous-good-output");
        string stagingRoot;

        using (var transaction = new StagedOutputTransaction([target], force: true))
        {
            stagingRoot = transaction.StagingRoot;
            var exception = Assert.Throws<InvalidOperationException>(() => transaction.Commit());

            Assert.Contains("projection.md", exception.Message, StringComparison.Ordinal);
            Assert.Equal("previous-good-output", await File.ReadAllTextAsync(target));
            Assert.True(Directory.Exists(stagingRoot));
        }

        Assert.False(Directory.Exists(stagingRoot));
        Assert.Equal("previous-good-output", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task Staged_commit_allows_a_missing_optional_output_and_removes_a_stale_target()
    {
        using var fixture = new Fixture();
        var markdown = Path.Combine(fixture.Root, "projection.md");
        var assets = Path.Combine(fixture.Root, "projection.assets");
        await File.WriteAllTextAsync(markdown, "previous-markdown");
        Directory.CreateDirectory(assets);
        await File.WriteAllTextAsync(Path.Combine(assets, "stale.png"), "stale");

        using (var transaction = new StagedOutputTransaction([markdown], force: true, optionalDestinations: [assets]))
        {
            await File.WriteAllTextAsync(transaction.PathFor(markdown), "new-markdown");
            transaction.Commit();
        }

        Assert.Equal("new-markdown", await File.ReadAllTextAsync(markdown));
        Assert.False(Directory.Exists(assets));
    }

    [Fact]
    public async Task Staged_commit_installs_all_outputs_and_removes_staging_artifacts()
    {
        using var fixture = new Fixture();
        var markdown = Path.Combine(fixture.Root, "projection.md");
        var sidecar = Path.Combine(fixture.Root, "projection.drmd");
        await File.WriteAllTextAsync(markdown, "previous-good-output");
        string stagingRoot;

        using (var transaction = new StagedOutputTransaction([markdown, sidecar], force: true))
        {
            stagingRoot = transaction.StagingRoot;
            await File.WriteAllTextAsync(transaction.PathFor(markdown), "new-markdown");
            await File.WriteAllTextAsync(transaction.PathFor(sidecar), "new-sidecar");
            transaction.Commit();

            Assert.Equal("new-markdown", await File.ReadAllTextAsync(markdown));
            Assert.Equal("new-sidecar", await File.ReadAllTextAsync(sidecar));
            Assert.False(Directory.Exists(stagingRoot));
        }

        Assert.False(Directory.Exists(stagingRoot));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, ".*.backup"));
    }

    [Fact]
    public async Task Staged_commit_keeps_all_new_outputs_when_backup_cleanup_fails()
    {
        using var fixture = new Fixture();
        var markdown = Path.Combine(fixture.Root, "projection.md");
        var sidecar = Path.Combine(fixture.Root, "projection.drmd");
        await File.WriteAllTextAsync(markdown, "previous-markdown");
        await File.WriteAllTextAsync(sidecar, "previous-sidecar");

        using (var transaction = new StagedOutputTransaction(
            [markdown, sidecar], force: true, fileSystem: new FailingBackupCleanupFileSystem()))
        {
            await File.WriteAllTextAsync(transaction.PathFor(markdown), "new-markdown");
            await File.WriteAllTextAsync(transaction.PathFor(sidecar), "new-sidecar");
            transaction.Commit();
        }

        Assert.Equal("new-markdown", await File.ReadAllTextAsync(markdown));
        Assert.Equal("new-sidecar", await File.ReadAllTextAsync(sidecar));
    }

    private sealed class FailingBackupCleanupFileSystem : IStagedOutputFileSystem
    {
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public void MoveFile(string source, string destination) => File.Move(source, destination);
        public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);
        public void DeleteFile(string path)
        {
            if (path.EndsWith(".backup", StringComparison.Ordinal))
                throw new IOException("Simulated backup cleanup failure.");
            File.Delete(path);
        }
        public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "docredock-cli-tests", Guid.NewGuid().ToString("N"));
        public string SourcePath => Path.Combine(Root, "source.docx");
        public string MarkdownPath => Path.Combine(Root, "projection.md");
        public string RestoredPath => Path.Combine(Root, "restored.docx");

        public Fixture() => Directory.CreateDirectory(Root);

        public void CreateDocx(bool withImage = false)
        {
            using var file = File.Create(SourcePath);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            Write(archive, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            var document = withImage
                ? "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><w:body><w:p><w:r><w:t>Before</w:t></w:r><w:r><w:drawing><a:blip r:embed=\"rIdImage\"/></w:drawing></w:r></w:p></w:body></w:document>"
                : "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Before</w:t></w:r></w:p></w:body></w:document>";
            Write(archive, "word/document.xml", document);
            if (withImage)
                Write(archive, "word/_rels/document.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdImage\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/image1.png\"/></Relationships>");
        }

        private static void Write(ZipArchive archive, string path, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
