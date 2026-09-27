using DocRedock.Cli;
using DocRedock.Tests.Pdf;

namespace DocRedock.Tests.Cli;

/// <summary>v0.2.10 evaluation, section 5, as the CLI reports it: the Form XObject page exports
/// like the flat page, and a form DocRedock cannot decode is never reported as a complete page.</summary>
[Collection("Environment variables")]
public sealed class CliFormXObjectReviewTests : IDisposable
{
    private readonly string? previousExperimental = Environment.GetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL");
    private readonly string root = Directory.CreateTempSubdirectory("docredock-cli-form-").FullName;

    public CliFormXObjectReviewTests() => Environment.SetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL", "1");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL", previousExperimental);
        Directory.Delete(root, true);
    }

    private async Task<(int Exit, string Output)> ExportAsync(string fixture)
    {
        var stdout = new StringWriter();
        var app = new CliApplication(stdout, new StringWriter(), new DocRedock.Api.DocumentService(null, null, discoverPdfRasterizer: false));
        var exit = await app.RunAsync(["export", PdfFormXObjectFixtureTests.FixturePath(fixture),
            "--output", Path.Combine(root, Path.GetFileNameWithoutExtension(fixture) + ".md"), "--ocr", "off"]);
        return (exit, stdout.ToString());
    }

    [Fact]
    public async Task Form_page_exports_its_text_and_needs_no_review()
    {
        var (exit, output) = await ExportAsync("form-xobject-form.pdf");

        Assert.Equal(0, exit);
        Assert.Contains("Unanalyzed content: none", output, StringComparison.Ordinal);
        Assert.Contains("Human review: not required", output, StringComparison.Ordinal);
        Assert.Contains("FORM\\_TEXT\\_SENTINEL: approved",
            await File.ReadAllTextAsync(Path.Combine(root, "form-xobject-form.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Undecodable_form_page_warns_and_names_what_to_compare()
    {
        var (exit, output) = await ExportAsync("form-xobject-unreadable.pdf");

        Assert.Equal(1, exit);
        Assert.Contains("Visual elements converted: all", output, StringComparison.Ordinal);
        Assert.Contains("Unanalyzed content: 1 item(s) on 1 page(s)", output, StringComparison.Ordinal);
        Assert.Contains("Human review: required (unanalyzed content 1 page(s))", output, StringComparison.Ordinal);
        Assert.Contains("Pages requiring review: 1", output, StringComparison.Ordinal);
        Assert.Contains("Review page 1: 1 drawing component(s) not analyzed; their text and graphics are missing from the Markdown; review image unavailable",
            output, StringComparison.Ordinal);
        Assert.Contains("WARNING PdfFormXObjectUnparsed: PDF page 1:", output, StringComparison.Ordinal);
        Assert.Contains("WARNING PdfReviewImageUnavailable: PDF page 1:", output, StringComparison.Ordinal);
    }
}
