using System.Security.Cryptography;
using System.IO.Compression;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Providers.Abstractions.Providers;
using DocRedock.Render;

namespace DocRedock.Tests.Api;

public sealed class DocumentServiceTests
{
    [Fact]
    public async Task Readable_export_writes_only_a_plain_markdown_file()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "source.docx");
        await new MarkdownRenderer().RenderAsync("# Title\n\nReadable body", RenderFormat.Docx, source);
        var outputDirectory = Path.Combine(root, "readable");
        var markdown = Path.Combine(outputDirectory, "source.md");
        var service = new DocumentService();

        var exported = await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, markdown));

        Assert.Equal(markdown, exported.MarkdownPath);
        Assert.Equal([markdown], Directory.EnumerateFileSystemEntries(outputDirectory));
        var text = await File.ReadAllTextAsync(markdown);
        Assert.Contains("Readable body", text, StringComparison.Ordinal);
        Assert.DoesNotContain("drmd_schema", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<!--drmd:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readable_export_rejects_unrecognized_input_with_the_source_name()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "broken.pptx");
        await File.WriteAllTextAsync(source, "not an Office package");

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, Path.Combine(root, "broken.md"))));

        Assert.Contains("broken.pptx", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not a supported or readable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readable_xlsx_export_preflights_oversized_media_before_adapter_processing()
    {
        var root = TempDirectory();
        try
        {
            var source = Path.Combine(root, "source.xlsx");
            await new MarkdownRenderer().RenderAsync("# Image workbook\n\nBody", RenderFormat.Xlsx, source);
            using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
            await using (var media = archive.CreateEntry("xl/media/oversized.png").Open())
                await media.WriteAsync(RandomNumberGenerator.GetBytes(33 * 1024 * 1024));

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, Path.Combine(root, "source.md"))));

            Assert.Contains("xl/media/oversized.png", exception.Message, StringComparison.Ordinal);
            Assert.Contains("32 MiB limit", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(root, "source.md")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Readable_xlsx_export_preflights_oversized_non_media_before_adapter_processing()
    {
        var root = TempDirectory();
        try
        {
            var source = Path.Combine(root, "source.xlsx");
            await new MarkdownRenderer().RenderAsync("# Workbook\n\nBody", RenderFormat.Xlsx, source);
            using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
            await using (var content = archive.CreateEntry("xl/worksheets/oversized.xml", CompressionLevel.NoCompression).Open())
                await content.WriteAsync(new byte[33 * 1024 * 1024]);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, Path.Combine(root, "source.md"))));

            Assert.Contains("xl/worksheets/oversized.xml", exception.Message, StringComparison.Ordinal);
            Assert.Contains("per-entry limit", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Readable_xlsx_export_rejects_a_highly_compressed_non_media_entry()
    {
        var root = TempDirectory();
        try
        {
            var source = Path.Combine(root, "source.xlsx");
            await new MarkdownRenderer().RenderAsync("# Workbook\n\nBody", RenderFormat.Xlsx, source);
            using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
            await using (var content = archive.CreateEntry("xl/sharedStrings.xml", CompressionLevel.Optimal).Open())
                await content.WriteAsync(new byte[2 * 1024 * 1024]);

            var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, Path.Combine(root, "source.md"))));

            Assert.Contains("xl/sharedStrings.xml", exception.Message, StringComparison.Ordinal);
            Assert.Contains("compression-ratio", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Readable_xlsx_export_writes_embedded_images_and_labels_ocr_as_derived_text()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "source.xlsx");
        await new MarkdownRenderer().RenderAsync("# Image workbook\n\nBody", RenderFormat.Xlsx, source);
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
        await using (var media = archive.CreateEntry("xl/media/image1.png").Open())
            await media.WriteAsync(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var outputDirectory = Path.Combine(root, "readable");
        var markdown = Path.Combine(outputDirectory, "source.md");
        var service = new DocumentService(new FakeOcrEngine());

        await service.ExportReadableAsync(new ReadableDocumentExportOptions(
            source, markdown, EnableOcr: true, OcrLanguages: ["jpn", "eng"]));

        var text = await File.ReadAllTextAsync(markdown);
        Assert.Contains("### 埋め込み画像", text, StringComparison.Ordinal);
        Assert.Contains("![image1](source.assets/img-0001.png)", text, StringComparison.Ordinal);
        Assert.Contains("<details class=\"ocr-extraction\">", text, StringComparison.Ordinal);
        Assert.Contains("<summary>OCR抽出テキスト（クリックで展開）</summary>", text, StringComparison.Ordinal);
        Assert.Contains("> recognized text", text, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(outputDirectory, "source.assets", "img-0001.png")));
    }

    [Fact]
    public async Task Readable_xlsx_export_can_embed_verified_images_without_an_asset_directory()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "source.xlsx");
        await new MarkdownRenderer().RenderAsync("# Image workbook\n\nBody", RenderFormat.Xlsx, source);
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
        await using (var media = archive.CreateEntry("xl/media/image1.png").Open())
            await media.WriteAsync(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var outputDirectory = Path.Combine(root, "readable");
        var markdown = Path.Combine(outputDirectory, "source.md");

        await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(
            source, markdown, EmbedImages: true));

        var text = await File.ReadAllTextAsync(markdown);
        Assert.Contains("![image1](data:image/png;base64,iVBORw0KGgo=)", text, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(outputDirectory, "source.assets")));
        Assert.Equal([markdown], Directory.EnumerateFileSystemEntries(outputDirectory));
    }

    [Fact]
    public async Task Readable_embed_images_omits_unverified_image_data_instead_of_writing_an_external_reference()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "source.xlsx");
        await new MarkdownRenderer().RenderAsync("# Image workbook\n\nBody", RenderFormat.Xlsx, source);
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
        await using (var media = archive.CreateEntry("xl/media/image1.png").Open())
            await media.WriteAsync(new byte[] { 1, 2, 3, 4 });
        var markdown = Path.Combine(root, "source.md");

        var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(
            source, markdown, EmbedImages: true));

        var text = await File.ReadAllTextAsync(markdown);
        Assert.DoesNotContain("![", text, StringComparison.Ordinal);
        Assert.DoesNotContain("img-0001", text, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ReadableImageEmbedSkipped");
        Assert.False(Directory.Exists(Path.Combine(root, "source.assets")));
    }

    [Fact]
    public async Task Readable_export_links_svg_and_uses_a_placeholder_for_emf()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "source.xlsx");
        await new MarkdownRenderer().RenderAsync("Images", RenderFormat.Xlsx, source);
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
        {
            await using (var svg = archive.CreateEntry("xl/media/image1.svg").Open())
                await svg.WriteAsync("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"u8.ToArray());
            await using (var emf = archive.CreateEntry("xl/media/image2.emf").Open())
                await emf.WriteAsync(new byte[] { 1, 2, 3, 4 });
        }
        var markdown = Path.Combine(root, "readable", "source.md");

        var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, markdown));

        var text = await File.ReadAllTextAsync(markdown);
        Assert.Contains("![image1](source.assets/img-0001.svg)", text, StringComparison.Ordinal);
        Assert.Contains(".emf 形式は Markdown で表示できません", text, StringComparison.Ordinal);
        Assert.DoesNotContain("![image2]", text, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ImageFormatNotDisplayable");
        Assert.True(File.Exists(Path.Combine(root, "readable", "source.assets", "img-0002.emf")));
    }

    [Fact]
    public async Task Roundtrip_export_uses_drmd_image_paths_and_records_duplicate_part_aliases()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "source.xlsx");
        await new MarkdownRenderer().RenderAsync("Images", RenderFormat.Xlsx, source);
        var bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
        {
            await using (var first = archive.CreateEntry("xl/media/image1.png").Open())
                await first.WriteAsync(bytes);
            await using (var second = archive.CreateEntry("xl/media/image2.png").Open())
                await second.WriteAsync(bytes);
        }
        var markdown = Path.Combine(root, "source.md");
        var sidecar = Path.Combine(root, "source.drmd");

        await new DocumentService().ExportAsync(new DocumentExportOptions(source, sidecar, markdown));

        Assert.Contains("![image1](source.drmd/assets/img-0001.png)", await File.ReadAllTextAsync(markdown), StringComparison.Ordinal);
        var assetIndex = await File.ReadAllTextAsync(Path.Combine(sidecar, "assets", "index.json"));
        Assert.Contains("alias_part_uris", assetIndex, StringComparison.Ordinal);
        Assert.Contains("/xl/media/image2.png", assetIndex, StringComparison.Ordinal);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(sidecar, "assets"), "img-*.png"));
    }

    [Fact]
    public async Task Export_writes_roundtrip_sidecars_and_f0_restore_is_byte_identical()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "source.docx");
        await new MarkdownRenderer().RenderAsync("# Title\n\nBefore", RenderFormat.Docx, source);
        var markdown = Path.Combine(root, "source.md");
        var workspace = Path.Combine(root, "source.drmd");
        var output = Path.Combine(root, "restored.docx");
        var service = new DocumentService();

        var exported = await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown));
        var restored = await service.RestoreAsync(new DocumentRestoreOptions(workspace, output, markdown));

        Assert.True(File.Exists(Path.Combine(workspace, "graph", "index.json")));
        Assert.True(File.Exists(Path.Combine(workspace, "maps", "projection-map.jsonl")));
        Assert.True(File.Exists(Path.Combine(workspace, "derived", "chunks", "default.jsonl")));
        Assert.Equal(FidelityLevel.F0, restored.Fidelity);
        Assert.Equal(Hash(source), Hash(output));
        Assert.NotEmpty(exported.Graph.Nodes);
    }

    [Fact]
    public async Task Diff_maps_markdown_edit_to_graph_node()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "source.docx");
        await new MarkdownRenderer().RenderAsync("Before", RenderFormat.Docx, source);
        var markdown = Path.Combine(root, "source.md");
        var workspace = Path.Combine(root, "source.drmd");
        var service = new DocumentService();
        await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown));
        await File.WriteAllTextAsync(markdown, (await File.ReadAllTextAsync(markdown)).Replace("Before", "After", StringComparison.Ordinal));

        var diff = await service.DiffAsync(workspace, markdown);

        Assert.True(diff.Edit.Diff.DirtySet.HasOriginalMutations);
        Assert.Contains(diff.Edit.Diff.PatchSet.Operations, operation => operation.MutatesOriginal);
    }

    [Fact]
    public async Task Ocr_is_inline_derived_evidence_and_correction_restores_original_bytes()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "source.xlsx");
        await new MarkdownRenderer().RenderAsync("Image document", RenderFormat.Xlsx, source);
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
        await using (var media = archive.CreateEntry("xl/media/image1.png").Open())
            await media.WriteAsync(new byte[] { 1, 2, 3, 4 });
        var markdown = Path.Combine(root, "source.md");
        var workspace = Path.Combine(root, "source.drmd");
        var restored = Path.Combine(root, "restored.docx");
        var service = new DocumentService(new FakeOcrEngine());

        var exported = await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown, true, ["jpn", "eng"]));
        var projection = await File.ReadAllTextAsync(markdown);
        Assert.Contains("recognized text", projection);
        Assert.Contains(exported.Graph.Nodes, node => node is { Kind: NodeKind.ImageText, Layer: ContentLayer.Derived, Editability: NodeEditability.AnnotationOnly });
        Assert.Equal(1, exported.Workspace.Manifest.Ocr.StatusSummary.Completed);
        await File.WriteAllTextAsync(markdown, projection.Replace("recognized text", "corrected text", StringComparison.Ordinal));

        var result = await service.RestoreAsync(new DocumentRestoreOptions(workspace, restored, markdown));

        Assert.Equal(FidelityLevel.F0, result.Fidelity);
        Assert.Equal(Hash(source), Hash(restored));
    }

    [Fact]
    public async Task Textless_pdf_uses_explicit_rasterizer_and_ocr_providers()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "scan.pdf");
        await new MarkdownRenderer().RenderAsync(string.Empty, RenderFormat.Pdf, source);
        var markdown = Path.Combine(root, "scan.md");
        var workspace = Path.Combine(root, "scan.drmd");
        var service = new DocumentService(new FakeOcrEngine(), new FakePdfRasterizer());

        var exported = await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown, true, ["jpn"]));

        Assert.Contains("recognized text", await File.ReadAllTextAsync(markdown));
        Assert.Equal(1, exported.Workspace.Manifest.Ocr.StatusSummary.Completed);
        Assert.True(File.Exists(Path.Combine(workspace, "assets", "page-0001.png")));
    }

    [Fact]
    public async Task Textless_pdf_without_rasterizer_reports_explicit_unavailability()
    {
        var root = TempDirectory();
        try
        {
            var source = Path.Combine(root, "scan.pdf");
            await new MarkdownRenderer().RenderAsync(string.Empty, RenderFormat.Pdf, source);
            var exported = await new DocumentService(new FakeOcrEngine()).ExportAsync(
                new DocumentExportOptions(
                    source,
                    Path.Combine(root, "scan.drmd"),
                    Path.Combine(root, "scan.md"),
                    EnableOcr: true,
                    OcrLanguages: ["jpn"]));

            var diagnostic = Assert.Single(exported.Diagnostics, item =>
                item.Code == "PdfRasterizerUnavailable");
            Assert.Contains("does not include a PDF rasterizer", diagnostic.Message, StringComparison.Ordinal);
            Assert.Empty(exported.Graph.Nodes);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "docredock-service-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class FakeOcrEngine : IOcrEngine
    {
        public ProviderDescriptor Descriptor { get; } = new("test.ocr", new Version(1, 0), 1,
            new HashSet<string> { "ocr.text" }, "MIT", "built-in", true);

        public ValueTask<OcrAttemptResult> RecognizeAsync(OcrInput input, OcrOptions options, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OcrAttemptResult(OcrProcessingStatus.Completed,
                new OcrResult("recognized text", [new OcrTextRegion("recognized text", new Geometry("image-pixels", 0, 0, 10, 10), 0.92)]), []));
    }

    private sealed class FakePdfRasterizer : IPdfRasterizer
    {
        public ProviderDescriptor Descriptor { get; } = new("test.pdf.rasterizer", new Version(1, 0), 1,
            new HashSet<string> { "rasterize.pdf" }, "MIT", "built-in", true);

        public ValueTask<IReadOnlyList<RasterizedPdfPage>> RasterizeAsync(string pdfPath, IReadOnlyList<int> pageNumbers,
            PdfRasterizationOptions options, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<RasterizedPdfPage>>(pageNumbers
                .Select(page => new RasterizedPdfPage(page, "image/png", new byte[] { 1, 2, 3 }, 10, 10)).ToArray());
    }
}
