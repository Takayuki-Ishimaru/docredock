using DocRedock.Api;
using DocRedock.Formats.Pdf;

namespace DocRedock.Tests.Pdf;

public sealed class ComplexPdfFixtureTests
{
    [Fact]
    public void Complex_fixture_preserves_pages_columns_tables_and_japanese_text()
    {
        var path = FixturePath();

        var extraction = PdfTextExtractor.Extract(path);

        Assert.Equal(3, extraction.PageCount);
        Assert.Equal(3, extraction.Pages.Count);
        Assert.Contains("経費精算プラットフォーム", extraction.Text, StringComparison.Ordinal);
        Assert.Contains("KPI-01", extraction.Text, StringComparison.Ordinal);
        Assert.Contains("PDF-COMPLEX-001 / END", extraction.Text, StringComparison.Ordinal);
        Assert.Contains("検証観点", extraction.Text, StringComparison.Ordinal);
        Assert.Contains("業務仕様", extraction.Text, StringComparison.Ordinal);
        Assert.All(extraction.Pages, page => Assert.NotNull(page.Regions));
        Assert.True(extraction.Pages[0].Regions.Count >= 12);
        Assert.True(extraction.Pages[1].Regions.Count >= 8);
        Assert.True(extraction.Pages[2].Regions.Count >= 6);
    }

    [Fact]
    public async Task Readable_markdown_export_keeps_pdf_page_partitions_and_content()
    {
        var source = FixturePath();
        var output = Path.Combine(Path.GetTempPath(), "docredock-pdf-complex-" + Guid.NewGuid().ToString("N") + ".md");

        var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(
            source, output, Title: "PDF complex fixture"));

        var markdown = await File.ReadAllTextAsync(result.MarkdownPath);
        Assert.Contains("経費精算プラットフォーム", markdown, StringComparison.Ordinal);
        Assert.Contains("KPI-01", markdown, StringComparison.Ordinal);
        Assert.Contains("PDF-COMPLEX-001 / END", markdown, StringComparison.Ordinal);
        Assert.Contains("検証観点", markdown, StringComparison.Ordinal);
        Assert.Contains("業務仕様", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadableImageEmbedSkipped", string.Join("\n", result.Diagnostics.Select(item => item.Message)), StringComparison.Ordinal);
        File.Delete(output);
    }

    [Fact]
    public async Task Drmd_export_restores_complex_pdf_byte_identically()
    {
        var source = FixturePath();
        var root = Path.Combine(Path.GetTempPath(), "docredock-pdf-drmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var markdown = Path.Combine(root, "complex.md");
            var sidecar = Path.Combine(root, "complex.drmd");
            var restored = Path.Combine(root, "restored.pdf");
            var service = new DocumentService();

            await service.ExportAsync(new DocumentExportOptions(source, sidecar, markdown));

            Assert.True(File.Exists(markdown));
            Assert.True(File.Exists(Path.Combine(sidecar, "graph", "index.json")));
            Assert.Contains("roundtrip_store:", await File.ReadAllTextAsync(markdown), StringComparison.Ordinal);

            await service.RestoreAsync(new DocumentRestoreOptions(sidecar, restored, markdown));

            Assert.True(File.Exists(restored));
            Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(restored));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string FixturePath()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Pdf", "complex-layout.pdf");
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException("Complex PDF fixture was not found.");
    }
}
