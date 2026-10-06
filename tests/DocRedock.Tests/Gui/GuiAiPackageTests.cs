using DocRedock.Api;
using DocRedock.Gui;
using DocRedock.Tests.Api;

namespace DocRedock.Tests.Gui;

public sealed class GuiAiPackageTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-ai-gui-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public async Task GUI_results_carry_table_boundaries_the_evaluation_and_optional_row_blocks()
    {
        var workflow = new GuiWorkflowService();
        var readable = await workflow.ExportAsync(DocRedock.Tests.Api.V031EvaluationRegressionTests.Fixture("independent_one_column.xlsx"),
            Path.Combine(root, "readable"), enableOcr: false, readable: true);
        Assert.Equal(StructureBasis.NeedsComparison, readable.Summary!.TableStructure);
        var boundary = Assert.Single(readable.TableBoundaries!);
        Assert.Equal(("Data", "C1:C3"), (boundary.SheetName, boundary.GapRange));
        var package = await workflow.ExportAsync(DocRedock.Tests.Api.V031EvaluationRegressionTests.Fixture("scale500.xlsx", "V030"),
            Path.Combine(root, "package"), enableOcr: false, readable: true, aiPackage: true, tableRowBlocks: true);
        using var manifest = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(package.AiPackagePath!, "manifest.json")));
        Assert.True(manifest.RootElement.GetProperty("table_row_blocks").GetBoolean());
        Assert.True(manifest.RootElement.GetProperty("parts").GetArrayLength() > 1);
        Assert.Equal(StructureBasis.Inferred, package.Summary!.TableStructure);
    }

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
