using System.Text.Json;
using DocRedock.Api;
using DocRedock.Cli;
using DocRedock.Render;
using DocRedock.Tests.Api;

namespace DocRedock.Tests.Cli;

[Collection("Environment variables")]
public sealed class CliAiPackageTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-ai-cli-").FullName;
    public void Dispose() => Directory.Delete(root, true);
    private static CliApplication App(StringWriter output, StringWriter error) =>
        new(output, error, new DocumentService(null, null, discoverPdfRasterizer: false));

    [Theory]
    [InlineData("dir", AiPackageForm.Directory)]
    [InlineData("zip", AiPackageForm.Zip)]
    public async Task Office_package_needs_no_experimental_flag_and_supports_selected_sheets(string form, AiPackageForm packageForm)
    {
        var previous = Environment.GetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL");
        Environment.SetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL", null);
        try
        {
            var source = AiPackageExportTests.WriteSource(root, "xlsx");
            var target = Path.Combine(root, "selected" + (form == "zip" ? ".zip" : ""));
            var output = new StringWriter(); var error = new StringWriter();
            var exit = await App(output, error).RunAsync(["export", source, "--ai-package", form, "--output", target,
                "--sheets", "Visible", "--content-policy", "sanitized", "--chunk-chars", "128", "--ocr", "off"]);
            Assert.True(exit is 0 or 1, error.ToString());
            Assert.Contains("AI package:", output.ToString());
            var files = AiPackageExportTests.ReadPackage(target, packageForm);
            using var manifest = JsonDocument.Parse(files["manifest.json"]);
            Assert.Equal(128, manifest.RootElement.GetProperty("target_characters").GetInt32());
            Assert.Equal("sanitized", manifest.RootElement.GetProperty("content_policy").GetString());
            var locations = manifest.RootElement.GetProperty("locations").EnumerateArray()
                .ToDictionary(location => location.GetProperty("id").GetString()!);
            Assert.All(manifest.RootElement.GetProperty("parts").EnumerateArray(), part =>
                Assert.All(part.GetProperty("source_ids").EnumerateArray(), id =>
                    Assert.Equal("Visible", locations[id.GetString()!].GetProperty("sheet_name").GetString())));
        }
        finally { Environment.SetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL", previous); }
    }

    [Theory]
    [InlineData("--ai-package", "bad")]
    [InlineData("--chunk-chars", "128")]
    [InlineData("--ai-package", "dir", "--chunk-chars", "127")]
    [InlineData("--ai-package", "zip", "--chunk-chars", "1000001")]
    [InlineData("--ai-package", "zip", "--embed-images")]
    [InlineData("--ai-package", "dir", "--profile", "roundtrip")]
    public async Task Invalid_package_options_do_not_write_outputs(params string[] options)
    {
        var source = AiPackageExportTests.WriteSource(root, "docx");
        var target = Path.Combine(root, "out");
        var exit = await App(new(), new()).RunAsync(["export", source, "--output", target, .. options]);
        Assert.True(exit is 2 or 4);
        Assert.False(File.Exists(target)); Assert.False(Directory.Exists(target));
        Assert.Empty(Directory.GetFileSystemEntries(root, ".docredock-*"));
    }

    [Theory]
    [InlineData("dir")]
    [InlineData("zip")]
    public async Task Force_replaces_only_after_success_and_refuses_source_ancestors(string form)
    {
        var source = Path.Combine(root, "source.docx");
        await new MarkdownRenderer().RenderAsync("# Before\n\nOriginal body", RenderFormat.Docx, source);
        var target = Path.Combine(root, "out" + (form == "zip" ? ".zip" : ""));
        var app = App(new(), new());
        Assert.Equal(0, await app.RunAsync(["export", source, "--ai-package", form, "--output", target, "--ocr", "off"]));
        Assert.Equal(2, await app.RunAsync(["export", source, "--ai-package", form, "--output", target, "--ocr", "off"]));
        var packageForm = form == "zip" ? AiPackageForm.Zip : AiPackageForm.Directory;
        var original = AiPackageExportTests.ReadPackage(target, packageForm);
        var bad = Path.Combine(root, "bad.docx"); await File.WriteAllTextAsync(bad, "invalid archive");
        var failed = await app.RunAsync(["export", bad, "--ai-package", form, "--output", target, "--force", "--ocr", "off"]);
        Assert.True(failed > 1);
        Assert.Equal(original["manifest.json"], AiPackageExportTests.ReadPackage(target, packageForm)["manifest.json"]);
        File.Delete(source);
        await new MarkdownRenderer().RenderAsync("# After\n\nUpdated body", RenderFormat.Docx, source);
        Assert.Equal(0, await app.RunAsync(["export", source, "--ai-package", form, "--output", target, "--force", "--ocr", "off"]));
        Assert.Contains("Updated body", System.Text.Encoding.UTF8.GetString(AiPackageExportTests.ReadPackage(target, packageForm)["document.md"]));
        var before = await File.ReadAllBytesAsync(source);
        Assert.Equal(2, await app.RunAsync(["export", source, "--ai-package", form, "--output", root, "--force", "--ocr", "off"]));
        Assert.Equal(before, await File.ReadAllBytesAsync(source));
        Assert.Empty(Directory.GetFileSystemEntries(root, ".docredock-*"));
    }

    [Fact]
    public async Task Default_output_name_and_help_describe_packages()
    {
        var source = Path.Combine(root, "guide.docx");
        await new MarkdownRenderer().RenderAsync("Guide body", RenderFormat.Docx, source);
        var output = new StringWriter(); var app = App(output, new());
        Assert.Equal(0, await app.RunAsync(["export", source, "--ai-package", "dir", "--ocr", "off"]));
        Assert.True(File.Exists(Path.Combine(root, "guide.ai-package", "document.md")));
        Assert.Equal(0, await app.RunAsync(["export", "--help"]));
        Assert.Contains("--ai-package dir|zip", output.ToString());
    }

    [Theory]
    [InlineData("dir")]
    [InlineData("zip")]
    public async Task Force_protects_real_ancestors_of_a_source_reached_through_a_directory_link(string form)
    {
        var real = Path.Combine(root, "real");
        var nested = Path.Combine(real, "project", "nested");
        Directory.CreateDirectory(nested);
        var source = Path.Combine(nested, "source.docx");
        await new MarkdownRenderer().RenderAsync("Original body", RenderFormat.Docx, source);
        var before = await File.ReadAllBytesAsync(source);
        var link = Path.Combine(root, "link");
        try { Directory.CreateSymbolicLink(link, nested); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        { return; } // Windows hosts without link privileges still run literal-ancestor cases.
        try
        {
            var error = new StringWriter();
            var exit = await App(new(), error).RunAsync(["export", Path.Combine(link, "source.docx"),
                "--ai-package", form, "--output", real, "--force", "--ocr", "off"]);
            Assert.Equal(2, exit);
            Assert.Contains("refusing to overwrite the source", error.ToString());
            Assert.Equal(before, await File.ReadAllBytesAsync(source));
            Assert.Empty(Directory.GetFileSystemEntries(root, ".docredock-*"));
        }
        finally { Directory.Delete(link); }
    }
}
