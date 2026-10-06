using DocRedock.Cli;
using DocRedock.Tests.Pdf;

namespace DocRedock.Tests.Cli;

/// <summary>v0.2.11 evaluation, sections 6 and 7, as the CLI reports them: the reused font name is
/// exported with its real characters, and text a form draws outside its /BBox stays out of the
/// default (visible) output with an information line saying so - not with an exit-code change.</summary>
[Collection("Environment variables")]
public sealed class CliFontScopeAndVisibilityTests : IDisposable
{
    private readonly string? previousExperimental = Environment.GetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL");
    private readonly string root = Directory.CreateTempSubdirectory("docredock-cli-font-").FullName;

    public CliFontScopeAndVisibilityTests() => Environment.SetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL", "1");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL", previousExperimental);
        Directory.Delete(root, true);
    }

    private async Task<(int Exit, string Output, string Markdown)> ExportAsync(string fixture, params string[] options)
    {
        var stdout = new StringWriter();
        var app = new CliApplication(stdout, new StringWriter(), new DocRedock.Api.DocumentService(null, null, discoverPdfRasterizer: false));
        var markdown = Path.Combine(root, Path.GetFileNameWithoutExtension(fixture) + options.Length + ".md");
        var exit = await app.RunAsync(["export", PdfFormXObjectFixtureTests.FixturePath(fixture), "--output", markdown, "--ocr", "off", .. options]);
        return (exit, stdout.ToString(), await File.ReadAllTextAsync(markdown));
    }

    [Fact]
    public async Task A_font_name_reused_by_the_page_and_its_forms_exports_its_real_characters()
    {
        var (exit, output, markdown) = await ExportAsync("font-scope-forms.pdf");

        Assert.Equal(0, exit);
        Assert.Matches("XXX[\\s\\S]*YYY[\\s\\S]*ZZZ", markdown);
        Assert.Contains("Warnings: 0", output, StringComparison.Ordinal);
        Assert.Contains("Detected review items: none", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_outside_a_forms_bbox_is_left_out_and_reported_as_information()
    {
        var (exit, output, markdown) = await ExportAsync("form-bbox-clipped.pdf", "--content-policy", "visible");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("CLIPPED", markdown, StringComparison.Ordinal);
        Assert.Contains("INFORMATION PdfClippedTextExcluded:", output, StringComparison.Ordinal);
        Assert.Contains("Warnings: 0", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("form-bbox-clipped.pdf", "CLIPPED\\_SENTINEL")]
    [InlineData("layer-off.pdf", "HIDDEN\\_LAYER")]
    public async Task Complete_output_names_the_sharing_review_without_requesting_source_images(string fixture, string hiddenText)
    {
        var (exit, output, markdown) = await ExportAsync(fixture, "--content-policy", "complete");

        Assert.Equal(1, exit);
        Assert.Contains(hiddenText, markdown, StringComparison.Ordinal);
        Assert.Contains("Detected review items: hidden content included - review before sharing", output, StringComparison.Ordinal);
        Assert.Contains("Pages requiring review: 0", output, StringComparison.Ordinal);
        Assert.Contains("Review image pages: 0", output, StringComparison.Ordinal);
        Assert.Contains("WARNING HiddenContentIncluded:", output, StringComparison.Ordinal);
    }
}
