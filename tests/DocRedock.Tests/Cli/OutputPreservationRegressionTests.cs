using DocRedock.Api;
using DocRedock.Cli;
using DocRedock.Render;

namespace DocRedock.Tests.Cli;

public sealed class OutputPreservationRegressionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-output-preservation-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Force_export_protects_source_at_every_depth_inside_assets(int depth)
    {
        var markdown = Path.Combine(root, "projection.md");
        var directory = Path.Combine(root, "projection.assets");
        for (var index = 0; index < depth; index++) directory = Path.Combine(directory, "nested");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.docx");
        await new MarkdownRenderer().RenderAsync("Original document", RenderFormat.Docx, source);
        var original = await File.ReadAllBytesAsync(source);
        await File.WriteAllTextAsync(markdown, "previous output");
        var error = new StringWriter();
        var cli = new CliApplication(new StringWriter(), error, new DocumentService(null, null, discoverPdfRasterizer: false));

        var code = await cli.RunAsync(["export", source, "--output", markdown, "--force", "--ocr", "off"]);

        Assert.Equal((int)ExitCode.InvalidInput, code);
        Assert.Contains("refusing to overwrite the source", error.ToString());
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        Assert.Equal("previous output", await File.ReadAllTextAsync(markdown));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Force_export_protects_nested_source_reached_through_a_symlink(bool directoryLink)
    {
        if (OperatingSystem.IsWindows()) return;
        var markdown = Path.Combine(root, "projection.md");
        var directory = Directory.CreateDirectory(Path.Combine(root, "projection.assets", "nested", "deep")).FullName;
        var source = Path.Combine(directory, "source.docx");
        await new MarkdownRenderer().RenderAsync("Original document", RenderFormat.Docx, source);
        var original = await File.ReadAllBytesAsync(source);
        var alias = Path.Combine(root, directoryLink ? "alias" : "alias.docx");
        if (directoryLink) Directory.CreateSymbolicLink(alias, directory);
        else File.CreateSymbolicLink(alias, source);
        var input = directoryLink ? Path.Combine(alias, "source.docx") : alias;
        var cli = new CliApplication(new StringWriter(), new StringWriter(), new DocumentService(null, null, discoverPdfRasterizer: false));

        var code = await cli.RunAsync(["export", input, "--output", markdown, "--force", "--ocr", "off"]);

        Assert.Equal((int)ExitCode.InvalidInput, code);
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        Assert.False(File.Exists(markdown));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Non_force_commit_preserves_output_from_another_transaction(bool withAssets)
    {
        var markdown = Path.Combine(root, "projection.md");
        var assets = Path.Combine(root, "projection.assets");
        string[] optional = withAssets ? [assets] : [];
        using var first = new StagedOutputTransaction([markdown], force: false, optionalDestinations: optional);
        using var second = new StagedOutputTransaction([markdown], force: false, optionalDestinations: optional);
        File.WriteAllText(first.PathFor(markdown), "first result");
        File.WriteAllText(second.PathFor(markdown), "second result");
        if (withAssets)
        {
            Directory.CreateDirectory(first.PathFor(assets));
            Directory.CreateDirectory(second.PathFor(assets));
            File.WriteAllText(Path.Combine(first.PathFor(assets), "image.png"), "first image");
            File.WriteAllText(Path.Combine(second.PathFor(assets), "image.png"), "second image");
        }
        first.Commit();

        Assert.Throws<IOException>(() => second.Commit());

        Assert.Equal("first result", File.ReadAllText(markdown));
        if (withAssets) Assert.Equal("first image", File.ReadAllText(Path.Combine(assets, "image.png")));
    }

    [Fact]
    public void Non_force_commit_preserves_late_optional_output_without_installing_markdown()
    {
        var markdown = Path.Combine(root, "projection.md");
        var assets = Path.Combine(root, "projection.assets");
        using var transaction = new StagedOutputTransaction([markdown], force: false, optionalDestinations: [assets]);
        File.WriteAllText(transaction.PathFor(markdown), "new result");
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "keep.txt"), "another process");

        Assert.Throws<IOException>(() => transaction.Commit());

        Assert.False(File.Exists(markdown));
        Assert.Equal("another process", File.ReadAllText(Path.Combine(assets, "keep.txt")));
    }
}
