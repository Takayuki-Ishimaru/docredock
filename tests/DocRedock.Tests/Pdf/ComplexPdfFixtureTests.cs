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
    public async Task Complex_fixture_page_two_reports_its_embedded_image_beside_the_native_text()
    {
        // The fixture's page 2 places a real 900x300 Image XObject among ordinary paragraphs, and
        // reaches it through an indirectly referenced /Resources dictionary. Before this was
        // detected the image vanished with no link, no asset and no diagnostic - the page simply
        // looked complete because it still had native text.
        var source = FixturePath();
        var extraction = PdfTextExtractor.Extract(source);

        Assert.Equal([0, 1, 0], extraction.Pages.Select(page => page.EmbeddedImageCount));
        Assert.All(extraction.Pages, page => Assert.False(page.IsImageOnly));
        var bounds = Assert.Single(extraction.Pages[1].EmbeddedImages!);
        Assert.True(bounds.Width > 100 && bounds.Height > 100, $"expected a page-scale image rectangle, got {bounds}");
        var diagnostic = Assert.Single(PdfDocumentGraphProjection.Diagnostics(extraction),
            item => item.Code == "PdfEmbeddedImageOmitted");
        Assert.Contains("PDF page 2: 1 embedded image(s)", diagnostic.Message, StringComparison.Ordinal);

        var output = Path.Combine(Path.GetTempPath(), "docredock-pdf-embedded-" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, output));
            Assert.Contains(result.Diagnostics, item => item.Code == "PdfEmbeddedImageOmitted");
            var markdown = await File.ReadAllTextAsync(result.MarkdownPath);
            Assert.Contains("[PDF page 2: 1 embedded image(s) not extracted", markdown, StringComparison.Ordinal);
            // The native text around the image must be untouched by the placeholder.
            Assert.Contains("注記A", markdown, StringComparison.Ordinal);
        }
        finally { File.Delete(output); }
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

    [Fact]
    public void Page_two_two_column_business_spec_block_is_not_swallowed_by_table_inference_and_reads_column_aware()
    {
        // F-03 regression: page 2's "4. 左右カラムの業務仕様" section is a 2-cell reportlab
        // Table with a grid (see generate_complex_pdf.py's `columns2`), so it might plausibly be
        // consumed by table inference. In this fixture it is not - PdfTableInference rejects the
        // single-row 2-cell grid - so the block is flow text and goes through SortReadingOrder's
        // column detection.
        var extraction = PdfTextExtractor.Extract(FixturePath());
        var page2 = extraction.Pages[1];
        var page2Tables = extraction.Tables?.GetValueOrDefault(2) ?? [];
        var capturedByTable = page2Tables.Any(table => table.Rows.SelectMany(row => row.Cells)
            .Any(cell => cell.Text.Contains("申請者向け", StringComparison.Ordinal)));
        Assert.False(capturedByTable,
            "the two-column business-spec block was unexpectedly captured by table inference; " +
            "update this test to assert the table keeps each column's lines together instead");
        Assert.Contains(page2.Regions, region => region.Text.Contains("4.1 申請者向け", StringComparison.Ordinal));

        // The whole left column ("4.1"/"4.2" and their bullets) still precedes the right column's
        // "4.2 API境界" heading in page text - true regardless of column detection, but a basic
        // regression guard that the block was not scrambled or lost.
        var text = extraction.Text;
        var left41 = text.IndexOf("4.1 申請者向け", StringComparison.Ordinal);
        var left42 = text.IndexOf("4.2 API境界", StringComparison.Ordinal);
        Assert.True(left41 >= 0 && left42 >= 0 && left41 < left42,
            $"expected '4.1 申請者向け' before '4.2 API境界' in: {text}");

        // Page 2 also carries a short-label / long-value two-column list (注記A/B/C) later in the
        // same "6. 付録" section, with clean baseline-aligned rows (unlike the wrapped paragraph
        // block above): SortReadingOrder's gutter detection confirms and reorders it column-major,
        // which is the clearest positive demonstration on this real fixture that detection fires
        // and the whole label column precedes the whole value column.
        Assert.True(page2.ColumnCount >= 2,
            "expected the extractor to detect at least one column block on page 2");
        var noteA = text.IndexOf("注記A", StringComparison.Ordinal);
        var noteB = text.IndexOf("注記B", StringComparison.Ordinal);
        var noteC = text.IndexOf("注記C", StringComparison.Ordinal);
        var noteAValue = text.IndexOf("画像の文字は本文テキストではなく", StringComparison.Ordinal);
        Assert.True(noteA >= 0 && noteB >= 0 && noteC >= 0 && noteAValue >= 0,
            $"expected to find all three note labels and the first note's value in: {text}");
        Assert.True(noteA < noteB && noteB < noteC && noteC < noteAValue,
            $"expected all three note labels (column 1) before their values (column 2) in: {text}");
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
