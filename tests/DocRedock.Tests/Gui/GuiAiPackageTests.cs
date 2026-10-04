using DocRedock.Api;
using DocRedock.Gui;
using DocRedock.Tests.Api;

namespace DocRedock.Tests.Gui;

public sealed class GuiAiPackageTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-ai-gui-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GUI_packages_obey_policy_preserve_previous_output_and_choose_unique_names(bool zip)
    {
        var source = AiPackageExportTests.WriteSource(root, "docx");
        var output = Path.Combine(root, "export");
        var workflow = new GuiWorkflowService();
        var first = await workflow.ExportAsync(source, output, enableOcr: false, readable: true,
            contentPolicy: "sanitized", aiPackage: true, zipAiPackage: zip);
        Assert.True(first.IsReadable); Assert.Equal(string.Empty, first.SidecarPath);
        Assert.NotNull(first.AiPackagePath);
        Assert.Equal(zip ? first.AiPackagePath : Path.Combine(first.AiPackagePath, "document.md"), first.MarkdownPath);
        var files = AiPackageExportTests.ReadPackage(first.AiPackagePath, zip ? AiPackageForm.Zip : AiPackageForm.Directory);
        Assert.DoesNotContain("DOCREDOCK_SECRET", string.Concat(files.Values.Select(System.Text.Encoding.UTF8.GetString)).Replace("\\", ""));
        await Assert.ThrowsAsync<IOException>(() => workflow.ExportAsync(source, output, false, readable: true,
            aiPackage: true, zipAiPackage: zip));
        var second = await workflow.ExportAsync(source, output, false, readable: true,
            useUniqueName: true, aiPackage: true, zipAiPackage: zip);
        Assert.NotEqual(first.AiPackagePath, second.AiPackagePath);
        Assert.Contains(" (2).ai-package", second.AiPackagePath);
        Assert.Equal(files["manifest.json"], AiPackageExportTests.ReadPackage(first.AiPackagePath,
            zip ? AiPackageForm.Zip : AiPackageForm.Directory)["manifest.json"]);
    }
}
