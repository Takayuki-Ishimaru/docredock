using System.Text.RegularExpressions;
using DocRedock.Api;
using DocRedock.Render;

namespace DocRedock.Tests.Api;

/// <summary>Finds fixture files from the repository root (the folder holding DocRedock.sln).</summary>
internal static class HtmlFixtures
{
    public static string Root()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "DocRedock.sln")) || File.Exists(Path.Combine(current.FullName, "DocRedock.slnx")))
                return current.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    public static string Path_(params string[] parts) =>
        Path.Combine([Root(), "tests", "DocRedock.Tests", "Fixtures", .. parts]);

    public static DocumentService Service() => new(null, null, discoverPdfRasterizer: false);
}

public sealed class ReadableHtmlExportTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-html-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    private static ReadableDocumentExportOptions Options(string source, string output, bool omitImages = false) =>
        new(source, output, EnableOcr: false, OmitImages: omitImages, OutputFormat: ReadableOutputFormat.Html);

    [Theory]
    [InlineData("Pptx", "complex-design-doc.pptx", 2)]
    [InlineData("Docx", "complex-design-doc.docx", 1)]
    public async Task Html_export_embeds_images_and_writes_no_assets_directory(string folder, string file, int minimumImages)
    {
        var output = Path.Combine(root, "design.html");
        var result = await HtmlFixtures.Service().ExportReadableAsync(Options(HtmlFixtures.Path_(folder, file), output));

        Assert.Equal(ReadableOutputFormat.Html, result.OutputFormat);
        Assert.Equal(output, result.MarkdownPath);
        var html = await File.ReadAllTextAsync(output);
        Assert.False(Directory.Exists(Path.Combine(root, "design.assets")));
        Assert.Equal(["design.html"], Directory.GetFileSystemEntries(root).Select(Path.GetFileName).OfType<string>().ToArray());
        Assert.True(Regex.Matches(html, "data:image/png;base64,").Count >= minimumImages, "embedded PNG count");
        Assert.DoesNotContain(".assets/", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content-Security-Policy", html);
        Assert.False(File.ReadAllBytes(output).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "no BOM");
    }

    [Fact]
    public async Task Markdown_export_still_reports_markdown_format()
    {
        var output = Path.Combine(root, "design.md");
        var result = await HtmlFixtures.Service().ExportReadableAsync(
            new ReadableDocumentExportOptions(HtmlFixtures.Path_("Docx", "complex-design-doc.docx"), output, EnableOcr: false));
        Assert.Equal(ReadableOutputFormat.Markdown, result.OutputFormat);
    }

    [Fact]
    public async Task Omit_images_leaves_no_embedded_image()
    {
        var output = Path.Combine(root, "omit.html");
        await HtmlFixtures.Service().ExportReadableAsync(Options(HtmlFixtures.Path_("Pptx", "complex-design-doc.pptx"), output, omitImages: true));
        Assert.DoesNotContain("data:image", await File.ReadAllTextAsync(output));
        Assert.False(Directory.Exists(Path.Combine(root, "omit.assets")));
    }

    [Fact]
    public async Task Existing_output_is_refused_and_nothing_new_is_created()
    {
        var output = Path.Combine(root, "exists.html");
        await File.WriteAllTextAsync(output, "keep");
        await Assert.ThrowsAsync<IOException>(() => HtmlFixtures.Service().ExportReadableAsync(
            Options(HtmlFixtures.Path_("Pptx", "complex-design-doc.pptx"), output)));
        Assert.Equal("keep", await File.ReadAllTextAsync(output));
        Assert.Equal(["exists.html"], Directory.GetFileSystemEntries(root).Select(Path.GetFileName).OfType<string>().ToArray());
    }

    [Fact]
    public async Task Cancelled_export_leaves_no_partial_file()
    {
        var output = Path.Combine(root, "cancelled.html");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HtmlFixtures.Service().ExportReadableAsync(
            Options(HtmlFixtures.Path_("Pptx", "complex-design-doc.pptx"), output), cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task Ai_package_refuses_html()
    {
        var options = new AiPackageExportOptions(Options(HtmlFixtures.Path_("Docx", "complex-design-doc.docx"), Path.Combine(root, "x.html")),
            Path.Combine(root, "pkg"), AiPackageForm.Directory, 12_000);
        await Assert.ThrowsAsync<ArgumentException>(() => HtmlFixtures.Service().ExportAiPackageAsync(options));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    // Written against the full writer: the th scope and href rules only hold once it replaces the stub.
    [Fact]
    public async Task Every_fixture_exports_to_html_that_keeps_the_safety_invariants()
    {
        var sources = new[] { "Docx", "Xlsx", "Pptx" }
            .SelectMany(folder => Directory.EnumerateFiles(HtmlFixtures.Path_(folder), "*.*")
                .Where(file => file.EndsWith("." + folder.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)))
            .OrderBy(file => file, StringComparer.Ordinal).Take(40)
            .Concat(new[] { "complex-layout.pdf", "schedule-arrows.pdf", "font-scope-flat.pdf" }.Select(name => HtmlFixtures.Path_("Pdf", name)))
            .ToArray();
        var service = new DocumentService(null, null, discoverPdfRasterizer: false);
        var failures = new List<string>();
        var exported = 0;
        foreach (var source in sources)
        {
            var output = Path.Combine(root, $"inv-{exported}.html");
            try
            {
                await service.ExportReadableAsync(Options(source, output));
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or DocumentSourceException or System.Xml.XmlException)
            {
                continue; // a fixture that fails for a known reason (legacy, damaged, unsupported) is not an HTML defect
            }
            exported++;
            var problem = Check(await File.ReadAllTextAsync(output));
            if (problem is not null) failures.Add($"{Path.GetFileName(source)}: {problem}");
        }
        Assert.True(exported >= 10, $"only {exported} fixtures exported");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string? Check(string html)
    {
        if (!html.StartsWith("<!doctype html>", StringComparison.OrdinalIgnoreCase)) return "doctype";
        if (!Regex.IsMatch(html, "<html lang=\"(ja|en)\"")) return "html lang";
        if (!html.Contains("Content-Security-Policy", StringComparison.Ordinal)) return "CSP meta";
        var scripts = Regex.Matches(html, "<script\\b[^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (scripts.Count > 0 && (!html.Contains("workbook-document", StringComparison.Ordinal) || scripts.Count != 1 ||
            scripts[0].Groups[1].Value != ReadableHtmlInteractions.Script ||
            !html.Contains("script-src '" + ReadableHtmlInteractions.CspHash + "'", StringComparison.Ordinal))) return "untrusted script element";
        var tags = Regex.Matches(html, "<[a-zA-Z][^>]*>").Select(match => match.Value).ToArray();
        if (tags.FirstOrDefault(tag => Regex.IsMatch(tag, @"\son[a-z]+\s*=", RegexOptions.IgnoreCase)) is { } handler) return "event handler: " + Trim(handler);
        foreach (Match src in Regex.Matches(html, "\\ssrc=\"([^\"]*)\""))
            if (!src.Groups[1].Value.StartsWith("data:image/", StringComparison.Ordinal)) return "src not data:image/: " + Trim(src.Value);
        foreach (Match href in Regex.Matches(html, "\\shref=\"([^\"]*)\""))
        {
            var value = href.Groups[1].Value;
            if (!(value.StartsWith('#') || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                  value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)))
                return "href: " + Trim(value);
        }
        if (tags.FirstOrDefault(tag => tag.StartsWith("<img", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(tag, "\\salt=")) is { } img) return "img without alt: " + Trim(img);
        if (tags.FirstOrDefault(tag => Regex.IsMatch(tag, "^<th[\\s>]", RegexOptions.IgnoreCase) && !Regex.IsMatch(tag, "\\sscope=")) is { } th) return "th without scope: " + Trim(th);
        if (!Regex.IsMatch(html, "<title>[^<]+</title>")) return "empty title";
        return null;

        static string Trim(string text) => text.Length > 80 ? text[..80] : text;
    }
}
