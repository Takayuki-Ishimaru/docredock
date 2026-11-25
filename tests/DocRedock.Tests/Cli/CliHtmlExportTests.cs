using System.Text.Json;
using DocRedock.Api;
using DocRedock.Cli;
using DocRedock.Render;
using DocRedock.Tests.Api;

namespace DocRedock.Tests.Cli;

public sealed class CliHtmlExportTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-cli-html-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    private async Task<string> Source(string name)
    {
        var path = Path.Combine(root, name + ".docx");
        await new MarkdownRenderer().RenderAsync("Hello world", RenderFormat.Docx, path);
        return path;
    }

    private static CliApplication App(StringWriter output, StringWriter error) => new(output, error, HtmlFixtures.Service());

    private static string[] Files(string folder) =>
        Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).OfType<string>().Order().ToArray();

    [Fact]
    public async Task Format_html_writes_a_html_named_file_with_no_assets()
    {
        var source = await Source("report");
        var output = new StringWriter();
        var exit = await App(output, new StringWriter()).RunAsync(["export", source, "--format", "html", "--ocr", "off", "--verbose"]);
        Assert.Equal(0, exit);
        Assert.Equal(["report.docx", "report.html"], Files(root));
        Assert.StartsWith("<!doctype html>", await File.ReadAllTextAsync(Path.Combine(root, "report.html")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Readable HTML (one-way; images embedded; no sidecar)", output.ToString());
    }

    [Fact]
    public async Task Keep_source_extension_names_the_html_after_the_whole_file_name()
    {
        var source = await Source("report");
        Assert.Equal(0, await App(new(), new()).RunAsync(["export", source, "--format", "HTML", "--keep-source-extension", "--ocr", "off"]));
        Assert.Contains("report.docx.html", Files(root));
    }

    [Theory]
    [InlineData("x.html")]
    [InlineData("x.HTM")]
    public async Task An_html_output_name_selects_html_when_format_is_omitted(string name)
    {
        var source = await Source("report");
        var target = Path.Combine(root, name);
        Assert.Equal(0, await App(new(), new()).RunAsync(["export", source, "-o", target, "--ocr", "off"]));
        Assert.StartsWith("<!doctype html>", await File.ReadAllTextAsync(target), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_explicit_markdown_format_wins_over_an_html_name_and_md_is_markdown()
    {
        var source = await Source("report");
        var target = Path.Combine(root, "x.html");
        Assert.Equal(0, await App(new(), new()).RunAsync(["export", source, "-o", target, "--format", "md", "--ocr", "off"]));
        Assert.DoesNotContain("<!doctype", await File.ReadAllTextAsync(target), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Office_layout_is_accepted_for_html_by_format_or_output_name_and_refused_for_markdown()
    {
        var source = await Source("report");
        Assert.Equal(0, await App(new(), new()).RunAsync(["export", source, "--format", "html", "--office-layout", "structured", "--ocr", "off"]));
        Assert.Equal(0, await App(new(), new()).RunAsync(["export", source, "-o", Path.Combine(root, "named.html"), "--office-layout", "structured", "--ocr", "off"]));
        var error = new StringWriter();
        Assert.NotEqual(0, await App(new(), error).RunAsync(["export", source, "-o", Path.Combine(root, "plain.md"), "--office-layout", "structured", "--ocr", "off"]));
        // The message names an option that exists.
        Assert.Contains("--format html", error.ToString());
        Assert.False(File.Exists(Path.Combine(root, "plain.md")));
    }

    [Theory]
    [InlineData("--format", "pdf")]
    [InlineData("--format", "html", "--ai-package", "dir")]
    [InlineData("--format", "html", "--profile", "roundtrip")]
    [InlineData("--format", "html", "--profile", "audit")]
    [InlineData("--format", "html", "--images", "files")]
    public async Task Invalid_combinations_exit_2_and_write_nothing(params string[] extra)
    {
        var source = await Source("report");
        var error = new StringWriter();
        var exit = await App(new(), error).RunAsync(["export", source, .. extra, "--ocr", "off"]);
        Assert.Equal(2, exit);
        Assert.NotEqual("", error.ToString());
        Assert.Equal(["report.docx"], Files(root));
    }

    [Fact]
    public async Task Images_files_message_explains_the_alternatives()
    {
        var source = await Source("report");
        var error = new StringWriter();
        await App(new(), error).RunAsync(["export", source, "--format", "html", "--images", "files"]);
        Assert.Contains("HTML keeps images inside the file", error.ToString());
    }

    [Fact]
    public async Task Html_accepts_embed_images_and_omit()
    {
        var source = await Source("report");
        Assert.Equal(0, await App(new(), new()).RunAsync(["export", source, "-o", Path.Combine(root, "a.html"), "--embed-images", "--ocr", "off"]));
        Assert.Equal(0, await App(new(), new()).RunAsync(["export", source, "-o", Path.Combine(root, "b.html"), "--images", "omit", "--ocr", "off"]));
    }

    [Fact]
    public async Task Batch_with_an_output_directory_writes_html_files_and_no_assets()
    {
        var first = await Source("first"); var second = await Source("second");
        var target = Path.Combine(root, "out");
        var output = new StringWriter();
        Assert.Equal(0, await App(output, new()).RunAsync(["export", first, second, "--format", "html", "--output-dir", target, "--json", "--ocr", "off"]));
        Assert.Equal(["first.html", "second.html"], Files(target));
        using var json = JsonDocument.Parse(output.ToString());
        foreach (var result in json.RootElement.GetProperty("results").EnumerateArray())
            Assert.Equal("html", result.GetProperty("output_format").GetString());
    }

    [Fact]
    public async Task Standard_output_receives_the_html_document_with_images_embedded()
    {
        var output = new StringWriter(); var error = new StringWriter();
        var exit = await App(output, error).RunAsync(["export", HtmlFixtures.Path_("Pptx", "complex-design-doc.pptx"), "-o", "-", "--format", "html", "--ocr", "off"]);
        Assert.True(exit is 0 or 1, error.ToString());
        Assert.StartsWith("<!doctype html>", output.ToString().TrimStart(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data:image/png;base64,", output.ToString());
        Assert.DoesNotContain("Exported:", output.ToString());
    }

    [Theory]
    [InlineData(new[] { "--format", "html" }, "html")]
    [InlineData(new string[0], "markdown")]
    [InlineData(new[] { "--profile", "roundtrip" }, "markdown")]
    [InlineData(new[] { "--ai-package", "dir" }, "ai-package")]
    public async Task Json_results_name_the_output_format(string[] extra, string expected)
    {
        var source = await Source("report");
        var output = new StringWriter();
        var args = new List<string> { "export", source, "--json", "--ocr", "off", "-o", Path.Combine(root, expected + (expected == "ai-package" ? "-pkg" : expected == "html" ? ".html" : ".md")) };
        args.AddRange(extra);
        await App(output, new()).RunAsync([.. args]); // roundtrip is refused without the experimental switch; its failure result still names markdown
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(expected, json.RootElement.GetProperty("results")[0].GetProperty("output_format").GetString());
    }

    [Fact]
    public async Task Failure_results_also_name_the_output_format()
    {
        var output = new StringWriter();
        await App(output, new()).RunAsync(["export", Path.Combine(root, "missing.docx"), "--format", "html", "--json"]);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("html", json.RootElement.GetProperty("results")[0].GetProperty("output_format").GetString());
    }

    [Fact]
    public async Task Export_help_documents_the_format_option()
    {
        var output = new StringWriter();
        Assert.Equal(0, await App(output, new()).RunAsync(["help", "export"]));
        Assert.Contains("[--format markdown|html]", output.ToString());
        Assert.Contains("--format markdown|html   Markdown (default)", output.ToString());
    }

    private static async Task<JsonElement> McpAsync(params string[] lines)
    {
        var output = new StringWriter();
        await new McpServer(new StringReader(string.Join("\n", lines) + "\n"), output, new StringWriter(), HtmlFixtures.Service()).RunAsync();
        return JsonDocument.Parse(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Last()).RootElement.Clone();
    }

    [Fact]
    public async Task Mcp_lists_export_html_and_it_writes_an_html_file()
    {
        var list = await McpAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        Assert.Contains(list.GetProperty("result").GetProperty("tools").EnumerateArray(), tool => tool.GetProperty("name").GetString() == "export_html");

        var outputDir = Path.Combine(root, "mcp");
        var call = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call",
            ["params"] = new Dictionary<string, object>
            {
                ["name"] = "export_html",
                ["arguments"] = new { path = HtmlFixtures.Path_("Pptx", "complex-design-doc.pptx"), output_dir = outputDir, ocr = "off" },
            },
        });
        var result = (await McpAsync(call)).GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), result.ToString());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        using var json = JsonDocument.Parse(text);
        var item = json.RootElement.GetProperty("results")[0];
        var path = item.GetProperty("output").GetString()!;
        Assert.EndsWith(".html", path);
        Assert.True(File.Exists(path));
        Assert.Equal("html", item.GetProperty("output_format").GetString());
        Assert.False(Directory.Exists(Path.Combine(outputDir, "complex-design-doc.assets")));
    }
}
