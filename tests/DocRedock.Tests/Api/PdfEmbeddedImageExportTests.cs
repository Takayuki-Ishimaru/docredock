using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Formats.Pdf;
using DocRedock.Providers.Abstractions.Providers;
using static DocRedock.Tests.Pdf.PdfImageFixtures;

namespace DocRedock.Tests.Api;

/// <summary>The pictures of a PDF reach the exports the way the pictures of a Word document do: an image node, an asset, a
/// reference in the Markdown, and no placeholder or warning for what was extracted.</summary>
public sealed class PdfEmbeddedImageExportTests
{
    private static readonly byte[] Pixels = Gradient(4, 3, 3);

    private static byte[] FigurePdf() => Build(
        [new PdfPage("q 60 0 0 40 20 100 cm /Im1 Do Q\nBT 10 80 Td (Figure 1. A caption) Tj ET")],
        [Image(5, 4, 3, "/DeviceRGB", 8, Pixels)]);

    private static async Task<(string Root, string Source)> Write(byte[] pdf)
    {
        var root = Path.Combine(Path.GetTempPath(), "docredock-pdf-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "figure.pdf");
        await File.WriteAllBytesAsync(source, pdf);
        return (root, source);
    }

    [Fact]
    public async Task Readable_markdown_writes_the_image_as_a_file_beside_its_caption_without_a_placeholder()
    {
        var (root, source) = await Write(FigurePdf());
        try
        {
            var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, Path.Combine(root, "figure.md")));

            var markdown = await File.ReadAllTextAsync(result.MarkdownPath);
            var file = Path.Combine(root, "figure.assets", "pdf-img-0005.png");
            Assert.True(File.Exists(file));
            Assert.Equal(Pixels, ReadPng(await File.ReadAllBytesAsync(file)).Samples);
            // Drawn at 60x40 points = 80x53.33 CSS pixels; the markdown says so, as it does for a Word picture.
            Assert.Contains("<img src=\"figure.assets/pdf-img-0005.png\" alt=\"Image 1 on page 1\" width=\"80\" height=\"53\">", markdown, StringComparison.Ordinal);
            Assert.True(markdown.IndexOf("<img", StringComparison.Ordinal) < markdown.IndexOf("Figure 1. A caption", StringComparison.Ordinal));
            Assert.True(markdown.IndexOf("Body text", StringComparison.Ordinal) < markdown.IndexOf("<img", StringComparison.Ordinal));
            Assert.DoesNotContain("not extracted", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain(result.Diagnostics, item => item.Code is "PdfEmbeddedImageOmitted" or "ImageNotWritten");
            Assert.Empty(result.Projection.UnwrittenImageIds);
            Assert.False(ExportReviewBuilder.Build(result.Graph, result.Diagnostics, result.Projection).Required);
            Assert.Equal("image/png", result.Graph.Assets!["pdf-img-0005"].MediaType);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("visible")]
    [InlineData("sanitized")]
    [InlineData("complete")]
    public async Task Every_content_policy_keeps_a_body_image(string policy)
    {
        var (root, source) = await Write(FigurePdf());
        try
        {
            var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, Path.Combine(root, "figure.md"), ContentPolicy: policy));

            Assert.Contains("pdf-img-0005.png", await File.ReadAllTextAsync(result.MarkdownPath), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Images_omit_names_the_picture_by_its_description_and_writes_no_file()
    {
        var (root, source) = await Write(FigurePdf());
        try
        {
            var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, Path.Combine(root, "figure.md"), OmitImages: true));

            var markdown = await File.ReadAllTextAsync(result.MarkdownPath);
            Assert.False(Directory.Exists(Path.Combine(root, "figure.assets")));
            Assert.DoesNotContain("<img", markdown, StringComparison.Ordinal);
            Assert.Contains("Image: Image 1 on page 1 (pdf-img-0005, not included in this text-only output)", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain(result.Diagnostics, item => item.Code is "PdfEmbeddedImageOmitted" or "ImageNotWritten");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Embedded_images_become_a_data_uri_of_the_same_png()
    {
        var (root, source) = await Write(FigurePdf());
        try
        {
            var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, Path.Combine(root, "figure.md"), EmbedImages: true));

            var markdown = await File.ReadAllTextAsync(result.MarkdownPath);
            Assert.False(Directory.Exists(Path.Combine(root, "figure.assets")));
            var encoded = Regex.Match(markdown, "src=\"data:image/png;base64,([^\"]+)\"").Groups[1].Value;
            var png = Convert.FromBase64String(encoded);
            Assert.Equal(PdfTextExtractor.Extract(await File.ReadAllBytesAsync(source)).Pages[0].ExtractedImages![0].Content, png);
            Assert.Equal(Pixels, ReadPng(png).Samples);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task An_image_that_stayed_a_placeholder_is_still_reported_beside_one_that_was_extracted()
    {
        var content = "q 60 0 0 40 20 140 cm /Im1 Do Q\nq 60 0 0 40 20 60 cm /Im2 Do Q";
        var (root, source) = await Write(Build([new PdfPage(content, "/Im1 5 0 R /Im2 6 0 R")],
        [
            Image(5, 4, 3, "/DeviceRGB", 8, Pixels),
            new PdfObject(6, "/Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /JPXDecode", [1, 2, 3]),
        ]));
        try
        {
            var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, Path.Combine(root, "figure.md"), IncludePdfFallbackImages: false));

            var markdown = await File.ReadAllTextAsync(result.MarkdownPath);
            Assert.Contains("pdf-img-0005.png", markdown, StringComparison.Ordinal);
            Assert.Contains("1 embedded image(s) not extracted", markdown, StringComparison.Ordinal);
            var omitted = Assert.Single(result.Diagnostics, item => item.Code == "PdfEmbeddedImageOmitted");
            Assert.Contains("PDF page 1: 1 embedded image(s)", omitted.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task A_picture_repeated_on_every_page_is_not_written_into_the_body()
    {
        var logo = Image(5, 2, 2, "/DeviceGray", 8, [1, 2, 3, 4]);
        var pages = new[]
        {
            new PdfPage("q 20 0 0 20 10 170 cm /Im1 Do Q", Text: "First"),
            new PdfPage("q 20 0 0 20 10 170 cm /Im1 Do Q", Text: "Second"),
            new PdfPage("q 20 0 0 20 10 170 cm /Im1 Do Q", Text: "Third"),
        };
        var (root, source) = await Write(Build(pages, [logo]));
        try
        {
            var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, Path.Combine(root, "figure.md")));

            Assert.DoesNotContain("<img", await File.ReadAllTextAsync(result.MarkdownPath), StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(root, "figure.assets")));
            Assert.DoesNotContain(result.Diagnostics, item => item.Code == "PdfEmbeddedImageOmitted");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task The_audit_profile_keeps_the_image_asset_in_its_workspace_and_the_markdown_refers_to_it()
    {
        var (root, source) = await Write(FigurePdf());
        try
        {
            var result = await new DocumentService().ExportAsync(new DocumentExportOptions(
                source, Path.Combine(root, "figure.drmd"), Path.Combine(root, "figure.md"), Profile: "audit"));

            Assert.True(result.Graph.Assets!.ContainsKey("pdf-img-0005"));
            Assert.Contains(Directory.EnumerateFiles(Path.Combine(root, "figure.drmd"), "pdf-img-0005.png", SearchOption.AllDirectories), _ => true);
            var markdown = await File.ReadAllTextAsync(result.MarkdownPath);
            Assert.Contains("pdf-img-0005", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("not extracted", markdown, StringComparison.Ordinal);
            Assert.False(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Required);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task An_ai_package_carries_the_image_in_assets_and_needs_no_review()
    {
        var (root, source) = await Write(FigurePdf());
        try
        {
            var output = Path.Combine(root, "figure.ai-package");
            var result = await new DocumentService().ExportAiPackageAsync(new AiPackageExportOptions(
                new ReadableDocumentExportOptions(source, "document.md"), output, AiPackageForm.Directory, 12_000));

            Assert.True(File.Exists(Path.Combine(output, "assets", "pdf-img-0005.png")));
            Assert.Contains("assets/pdf-img-0005.png", await File.ReadAllTextAsync(Path.Combine(output, "document.md")), StringComparison.Ordinal);
            Assert.False(result.Review.Required);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Ocr_that_is_only_automatic_leaves_a_pictures_text_alone_but_an_explicit_request_reads_it()
    {
        var (root, source) = await Write(FigurePdf());
        try
        {
            var service = new DocumentService(new RecordingOcr(), null, discoverPdfRasterizer: false);
            var automatic = await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, Path.Combine(root, "auto.md"), EnableOcr: true, OcrAuto: true));
            var requested = await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, Path.Combine(root, "on.md"), EnableOcr: true));

            Assert.DoesNotContain(automatic.Graph.Nodes, node => node.Kind == NodeKind.ImageText);
            var text = Assert.Single(requested.Graph.Nodes, node => node.Kind == NodeKind.ImageText);
            Assert.Equal("text in the picture", Assert.IsType<TextNodeContent>(text.Content).Text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class RecordingOcr : IOcrEngine
    {
        public ProviderDescriptor Descriptor { get; } = new("test.ocr", new Version(1, 0), 1,
            new HashSet<string> { "ocr.text" }, "MIT", "built-in", true);

        public ValueTask<OcrAttemptResult> RecognizeAsync(OcrInput input, OcrOptions options, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OcrAttemptResult(OcrProcessingStatus.Completed,
                new OcrResult("text in the picture", [new OcrTextRegion("text in the picture", new Geometry("image-pixels", 0, 0, 10, 10), 0.92)]), []));
    }

    private static string FixturePath(string name)
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Pdf", name);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(name);
    }

    [Fact]
    public async Task The_cli_benchmark_style_report_writes_the_figure_next_to_its_caption()
    {
        var fixture = FixturePath("list-benchmark-report.pdf");
        var root = Path.Combine(Path.GetTempPath(), "docredock-pdf-figure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(fixture, Path.Combine(root, "report.md")));

            var markdown = await File.ReadAllTextAsync(result.MarkdownPath);
            var caption = markdown.IndexOf("Figure 1.", StringComparison.Ordinal);
            Assert.True(caption > 0);
            var reference = markdown.LastIndexOf("<img", caption, StringComparison.Ordinal);
            Assert.True(reference >= 0 && caption - reference < 300, "the image reference is not within 300 characters of the caption");
            var file = Regex.Match(markdown[reference..caption], "src=\"([^\"]+)\"").Groups[1].Value;
            Assert.StartsWith("report.assets/", file, StringComparison.Ordinal);
            var png = ReadPng(await File.ReadAllBytesAsync(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal((640, 320), (png.Width, png.Height));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
