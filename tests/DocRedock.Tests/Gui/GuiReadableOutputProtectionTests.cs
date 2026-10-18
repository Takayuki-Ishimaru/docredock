using System.IO.Compression;
using DocRedock.Gui;
using DocRedock.Render;

namespace DocRedock.Tests.Gui;

public sealed class GuiReadableOutputProtectionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-gui-output-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public async Task Asset_collision_preserves_existing_assets_and_source()
    {
        var output = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
        var assets = Directory.CreateDirectory(Path.Combine(output, "images.assets")).FullName;
        var sentinel = Path.Combine(assets, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "previous export");
        var source = await WriteImageWorkbookAsync(assets);
        var original = await File.ReadAllBytesAsync(source);

        await Assert.ThrowsAsync<IOException>(() => new GuiWorkflowService().ExportAsync(
            source, output, enableOcr: false, readable: true));

        Assert.Equal("previous export", await File.ReadAllTextAsync(sentinel));
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        Assert.False(File.Exists(Path.Combine(output, "images.md")));
    }

    [Fact]
    public async Task Invalid_source_preserves_existing_assets()
    {
        var output = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
        var assets = Directory.CreateDirectory(Path.Combine(output, "broken.assets")).FullName;
        var sentinel = Path.Combine(assets, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "previous export");
        var source = Path.Combine(root, "broken.xlsx");
        await File.WriteAllTextAsync(source, "not an Office package");

        await Assert.ThrowsAnyAsync<Exception>(() => new GuiWorkflowService().ExportAsync(
            source, output, enableOcr: false, readable: true));

        Assert.Equal("previous export", await File.ReadAllTextAsync(sentinel));
        Assert.False(File.Exists(Path.Combine(output, "broken.md")));
    }

    [Fact]
    public async Task Cancelled_export_preserves_existing_assets()
    {
        var source = await WriteImageWorkbookAsync(root);
        var output = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
        var assets = Directory.CreateDirectory(Path.Combine(output, "images.assets")).FullName;
        var sentinel = Path.Combine(assets, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "previous export");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GuiWorkflowService().ExportAsync(
            source, output, enableOcr: false, readable: true, cancellationToken: cancellation.Token));

        Assert.Equal("previous export", await File.ReadAllTextAsync(sentinel));
        Assert.False(File.Exists(Path.Combine(output, "images.md")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unique_names_skip_existing_asset_files_and_directories(bool directory)
    {
        var source = await WriteImageWorkbookAsync(root);
        var output = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
        var collision = Path.Combine(output, "images.assets");
        var sentinel = directory ? Path.Combine(Directory.CreateDirectory(collision).FullName, "keep.txt") : collision;
        await File.WriteAllTextAsync(sentinel, "previous export");

        var result = await new GuiWorkflowService().ExportAsync(
            source, output, enableOcr: false, readable: true, useUniqueName: true);

        Assert.Equal(Path.Combine(output, "images (2).md"), result.MarkdownPath);
        Assert.Equal("previous export", await File.ReadAllTextAsync(sentinel));
        Assert.True(Directory.Exists(Path.Combine(output, "images (2).assets")));
        Assert.Contains("images%20%282%29.assets", await File.ReadAllTextAsync(result.MarkdownPath));
    }

    private static async Task<string> WriteImageWorkbookAsync(string directory)
    {
        var source = Path.Combine(directory, "images.xlsx");
        await new MarkdownRenderer().RenderAsync("# Image workbook", RenderFormat.Xlsx, source);
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
        await using (var media = archive.CreateEntry("xl/media/image1.png").Open())
            await media.WriteAsync(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        return source;
    }
}
