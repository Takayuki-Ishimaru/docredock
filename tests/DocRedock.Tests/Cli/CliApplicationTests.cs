using System.IO.Compression;
using System.Text;
using DocRedock.Cli;

namespace DocRedock.Tests.Cli;

public sealed class CliApplicationTests
{
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
        fixture.CreateDocx();
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

        Assert.Contains("DocRedock 0.1.0 Public Beta", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("file.drmd", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("file.drmdpkg", stdout.ToString(), StringComparison.Ordinal);
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

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "docredock-cli-tests", Guid.NewGuid().ToString("N"));
        public string SourcePath => Path.Combine(Root, "source.docx");
        public string MarkdownPath => Path.Combine(Root, "projection.md");
        public string RestoredPath => Path.Combine(Root, "restored.docx");

        public Fixture() => Directory.CreateDirectory(Root);

        public void CreateDocx()
        {
            using var file = File.Create(SourcePath);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            Write(archive, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Write(archive, "word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Before</w:t></w:r></w:p></w:body></w:document>");
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
