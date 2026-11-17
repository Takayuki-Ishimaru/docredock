using System.IO.Compression;
using System.Text.RegularExpressions;
using DocRedock.Api;
using DocRedock.Render;

namespace DocRedock.Tests.Api;

/// <summary>A picture left out of a text-only export (<c>--images omit</c>, which the MCP server's export_markdown uses) is
/// named by its asset id, the one used for its file under <c>.assets</c> and in the links of OCR tables, so a reader who needs
/// what the picture shows can ask for it with <c>docredock source --image</c>.</summary>
public sealed class ImagePlaceholderIdTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-placeholder-id-").FullName;

    public void Dispose() => Directory.Delete(root, true);

    private async Task<string> OmittedAsync(string source, string? language = null)
    {
        var output = Path.Combine(root, Guid.NewGuid().ToString("N") + ".md");
        await new DocumentService(null, null, discoverPdfRasterizer: false).ExportReadableAsync(
            new ReadableDocumentExportOptions(source, output, EnableOcr: false, OmitImages: true, Language: language));
        return await File.ReadAllTextAsync(output);
    }

    [Fact]
    public async Task A_docx_placeholder_names_the_asset_id_of_its_picture()
    {
        var markdown = await OmittedAsync(HtmlFixtures.Path_("Docx", "complex-design-doc.docx"));

        Assert.Contains("> 画像: Picture 1（img-0001、テキストのみの出力のため省略）", markdown, StringComparison.Ordinal);
        Assert.Contains("> 画像: Picture 2（img-0002、テキストのみの出力のため省略）", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_english_placeholder_names_the_asset_id_too()
    {
        var markdown = await OmittedAsync(HtmlFixtures.Path_("Docx", "complex-design-doc.docx"), "en");

        Assert.Contains("> Image: Picture 1 (img-0001, not included in this text-only output)", markdown, StringComparison.Ordinal);
        Assert.Contains("> Image: Picture 2 (img-0002, not included in this text-only output)", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Docx", "complex-design-doc.docx")]
    [InlineData("Pptx", "complex-design-doc.pptx")]
    [InlineData("Docx", "real-office-roundtrip.original.docx")]
    [InlineData("Pdf", "complex-layout.pdf")]
    public async Task Every_placeholder_of_a_fixture_has_an_id_and_the_id_is_a_file_of_the_files_export(string folder, string name)
    {
        var source = HtmlFixtures.Path_(folder, name);
        var markdown = await OmittedAsync(source);
        var placeholders = Regex.Matches(markdown, @"^> 画像.*$", RegexOptions.Multiline).Select(match => match.Value).ToArray();
        Assert.NotEmpty(placeholders);
        Assert.All(placeholders, line => Assert.Matches(@"（(img|pdf-img)-\d{4}(-o\d)?、テキストのみの出力のため省略）$", line));

        // The ids are the file names of the same document exported with its pictures as files.
        var files = Path.Combine(root, "files", name + ".md");
        await new DocumentService(null, null, discoverPdfRasterizer: false).ExportReadableAsync(
            new ReadableDocumentExportOptions(source, files, EnableOcr: false));
        var written = Directory.GetFiles(Path.Combine(root, "files", name + ".assets")).Select(Path.GetFileNameWithoutExtension).ToHashSet();
        foreach (var id in placeholders.Select(line => Regex.Match(line, @"((?:pdf-)?img-\d{4}(?:-o\d)?)、").Groups[1].Value))
            Assert.Contains(id, written);
    }

    [Fact]
    public async Task An_xlsx_placeholder_names_the_asset_id_of_its_picture()
    {
        var source = Path.Combine(root, "book.xlsx");
        await new MarkdownRenderer().RenderAsync("# Image workbook\n\nBody", RenderFormat.Xlsx, source);
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
        await using (var media = archive.CreateEntry("xl/media/image1.png").Open())
            await media.WriteAsync(PngRasterImage.Encode(4, 4, new byte[4 * 4 * 3]));

        var markdown = await OmittedAsync(source);

        Assert.Matches(@"> 画像.*（img-0001、テキストのみの出力のため省略）", markdown);
    }

    [Fact]
    public async Task Pictures_written_as_files_keep_their_markdown_unchanged()
    {
        var source = HtmlFixtures.Path_("Docx", "complex-design-doc.docx");
        var output = Path.Combine(root, "files.md");

        await new DocumentService(null, null, discoverPdfRasterizer: false).ExportReadableAsync(
            new ReadableDocumentExportOptions(source, output, EnableOcr: false));

        var markdown = await File.ReadAllTextAsync(output);
        Assert.DoesNotContain("テキストのみの出力のため省略", markdown, StringComparison.Ordinal);
        Assert.Contains("files.assets/img-0001.png", markdown, StringComparison.Ordinal);
    }
}
