using DocRedock.Api;
using DocRedock.Cli;
using DocRedock.Render;
using DocRedock.Tests.Markdown;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using GfmTable = Markdig.Extensions.Tables.Table;

namespace DocRedock.Tests.Cli;

public sealed class ReadableWorkbookCliOptionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-workbook-cli-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Theory]
    [InlineData(null, false)]
    [InlineData("auto", false)]
    [InlineData("grid", true)]
    [InlineData("records", false)]
    public async Task Table_layout_option_changes_the_exported_workbook_structure(
        string? mode, bool grid)
    {
        var source = await Source(11);
        var target = Path.Combine(root, "result.md");
        var arguments = new List<string>
        {
            "export", source, "-o", target, "--format", "markdown", "--ocr", "off"
        };
        if (mode is not null) arguments.AddRange(["--table-layout", mode]);
        var error = new StringWriter();
        var exit = await App(error).RunAsync([.. arguments]);

        Assert.Equal(0, exit);
        var markdown = await File.ReadAllTextAsync(target);
        var document = MarkdownStructure.Parse(markdown);
        Assert.Equal(grid ? 1 : 0,
            ((MarkdownObject)document).Descendants<GfmTable>().Count());
        var displayed = MarkdownStructure.DisplayedText(document);
        for (var column = 1; column <= 11; column++)
        {
            Assert.Contains($"FIELD_{column:D2}", displayed, StringComparison.Ordinal);
            Assert.Contains($"VALUE_{column:D2}", displayed, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Source_and_contents_switches_are_independent(
        bool omitSources, bool omitContents)
    {
        var source = await Source(2);
        var target = Path.Combine(root, "result.md");
        var arguments = new List<string>
        {
            "export", source, "-o", target, "--ocr", "off"
        };
        if (omitSources) arguments.Add("--no-source-references");
        if (omitContents) arguments.Add("--no-table-of-contents");

        Assert.Equal(0, await App(new()).RunAsync([.. arguments]));
        var markdown = await File.ReadAllTextAsync(target);
        var document = MarkdownStructure.Parse(markdown);
        var displayed = MarkdownStructure.DisplayedText(document);
        var contentsLinks = ((MarkdownObject)document).Descendants<LinkInline>()
            .Where(link => link.Url?.StartsWith('#') == true).ToArray();

        Assert.Equal(!omitSources,
            displayed.Contains(Path.GetFileName(source), StringComparison.Ordinal));
        Assert.Equal(!omitContents, contentsLinks.Length > 0);
        Assert.Contains("VALUE_01", displayed, StringComparison.Ordinal);
        Assert.Contains("VALUE_02", displayed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invalid_layout_is_rejected_before_output_is_created(bool invalidValue)
    {
        string[] extra = invalidValue ? ["--table-layout", "unsupported"] : ["--table-layout"];
        var source = await Source(2);
        var target = Path.Combine(root, "invalid.md");
        var error = new StringWriter();
        var exit = await App(error).RunAsync(
            ["export", source, "-o", target, "--ocr", "off", .. extra]);

        Assert.Equal(2, exit);
        Assert.NotEqual("", error.ToString());
        Assert.False(File.Exists(target));
        Assert.Equal([Path.GetFileName(source)],
            Directory.GetFileSystemEntries(root).Select(Path.GetFileName).OfType<string>().ToArray());
    }

    [Fact]
    public async Task Split_sheets_publishes_a_review_directory_and_structured_accounting()
    {
        var source = await Source(17);
        var target = Path.Combine(root, "review");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var app = new CliApplication(stdout, stderr, new DocumentService(null, null, discoverPdfRasterizer: false));
        Assert.Equal(0, await app.RunAsync(["export", source, "--split-sheets", "-o", target, "--ocr", "off", "--json"]));
        Assert.True(File.Exists(Path.Combine(target, "README.md")), stderr.ToString());
        Assert.Single(Directory.GetFiles(Path.Combine(target, "sheets"), "*.md"));
        Assert.True(File.Exists(Path.Combine(target, "conversion-report.json")));
        using var json = System.Text.Json.JsonDocument.Parse(stdout.ToString());
        var result = json.RootElement.GetProperty("results")[0];
        Assert.Equal("markdown-review", result.GetProperty("output_format").GetString());
        Assert.Equal("markdown-review", result.GetProperty("conversion").GetProperty("output_format").GetString());
    }

    [Fact]
    public async Task Export_help_documents_all_workbook_options()
    {
        var output = new StringWriter();
        var app = new CliApplication(output, new StringWriter(),
            new DocumentService(null, null, discoverPdfRasterizer: false));

        Assert.Equal(0, await app.RunAsync(["help", "export"]));
        Assert.Contains("--table-layout", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--no-source-references", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--no-table-of-contents", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--split-sheets", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--portable-markdown", output.ToString(), StringComparison.Ordinal);
    }

    private CliApplication App(StringWriter error) => new(new StringWriter(), error,
        new DocumentService(null, null, discoverPdfRasterizer: false));

    private async Task<string> Source(int columns)
    {
        var source = Path.Combine(root, "cli-source.xlsx");
        var headers = Enumerable.Range(1, columns).Select(column => $"FIELD_{column:D2}");
        var values = Enumerable.Range(1, columns).Select(column => $"VALUE_{column:D2}");
        var markdown = "| " + string.Join(" | ", headers) + " |\n" +
            "| " + string.Join(" | ", Enumerable.Repeat("---", columns)) + " |\n" +
            "| " + string.Join(" | ", values) + " |\n";
        await new MarkdownRenderer().RenderAsync(markdown, RenderFormat.Xlsx, source);
        return source;
    }
}
