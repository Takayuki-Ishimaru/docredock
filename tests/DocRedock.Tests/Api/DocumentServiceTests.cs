using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Providers.Abstractions.Providers;
using DocRedock.Render;
using DocRedock.VisualInference;

namespace DocRedock.Tests.Api;

public sealed class DocumentServiceTests
{
    [Fact]
    public async Task Pdf_vector_visual_graph_is_available_to_readable_markdown()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "vector.pdf");
        var markdown = Path.Combine(root, "vector.md");
        await File.WriteAllBytesAsync(source, Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length 145 >> stream\nBT 1 0 0 1 0 0 Tm (Start) Tj 100 100 Td (End) Tj ET\n0 0 20 20 re 100 100 20 20 re 0 0 m 100 100 l S\nendstream\n%%EOF"));

        var exported = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, markdown));

        var diagram = Assert.Single(exported.Graph.Nodes, node => node.Kind == NodeKind.Diagram && node.Extensions?.ContainsKey("visual_graph") == true);
        var visualGraph = diagram.Extensions!["visual_graph"].Deserialize<VisualGraph>()!;
        var validation = VisualGraphValidator.Validate(visualGraph);
        Assert.True(validation.IsValidForSemanticProjection,
            string.Join("; ", validation.Errors.Select(error => error.Code + ": " + error.Message)));
        Assert.Contains("```mermaid", await File.ReadAllTextAsync(markdown), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pdf_rectangle_table_cells_are_not_duplicated_as_mermaid_nodes_while_adjacent_flow_survives()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "table-flow.pdf");
        var markdown = Path.Combine(root, "table-flow.md");
        var content = """
            BT 1 0 0 1 10 10 Tm (CELL_A) Tj ET BT 1 0 0 1 110 10 Tm (CELL_B) Tj ET
            BT 1 0 0 1 10 40 Tm (CELL_C) Tj ET BT 1 0 0 1 110 40 Tm (CELL_D) Tj ET
            0 0 100 30 re S 100 0 100 30 re S 0 30 100 30 re S 100 30 100 30 re S
            BT 1 0 0 1 410 10 Tm (FLOW_START) Tj ET BT 1 0 0 1 610 10 Tm (FLOW_END) Tj ET
            400 0 100 30 re S 600 0 100 30 re S 500 15 m 600 15 l S 600 15 m 588 23 l 588 7 l h f
            """;
        await File.WriteAllBytesAsync(source, Encoding.Latin1.GetBytes($"%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length {content.Length} >> stream\n{content}\nendstream\n%%EOF"));

        var exported = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, markdown));
        var readable = await File.ReadAllTextAsync(markdown);
        var diagram = Assert.Single(exported.Graph.Nodes, node => node.Kind == NodeKind.Diagram);
        var visual = diagram.Extensions!["visual_graph"].Deserialize<VisualGraph>()!;

        // F-Issue7: readable Markdown now backslash-escapes the literal "_" in this plain cell
        // text ("CELL_A" -> "CELL\_A"); de-escape before counting occurrences.
        var readableDeEscaped = readable.Replace("\\", string.Empty, StringComparison.Ordinal);
        Assert.Equal(1, readableDeEscaped.Split("CELL_A", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("CELL_A[", readableDeEscaped, StringComparison.Ordinal);
        Assert.DoesNotContain(visual.Nodes, node => node.Label.StartsWith("CELL_", StringComparison.Ordinal));
        Assert.Contains(visual.Nodes, node => node.Label == "FLOW_START");
    }

    [Fact]
    public async Task Native_only_mode_reaches_pdf_adapter_and_keeps_geometry_relation_unresolved()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "vector-native-only.pdf");
        var markdown = Path.Combine(root, "vector-native-only.md");
        await File.WriteAllBytesAsync(source, Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length 145 >> stream\nBT 1 0 0 1 0 0 Tm (Start) Tj 100 100 Td (End) Tj ET\n0 0 20 20 re 100 100 20 20 re 0 0 m 100 100 l S\nendstream\n%%EOF"));

        var exported = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(
            source, markdown, InferenceMode: VisualInferenceMode.NativeOnly));
        var diagram = Assert.Single(exported.Graph.Nodes,
            node => node.Kind == NodeKind.Diagram && node.Extensions?.ContainsKey("visual_graph") == true);
        var visualGraph = diagram.Extensions!["visual_graph"].Deserialize<VisualGraph>()!;

        Assert.All(visualGraph.Edges, edge => Assert.Equal(VisualEdgeResolution.Unresolved, edge.Resolution));
        Assert.Equal(VisualGraphQuality.FallbackOnly, visualGraph.Quality);
        var readable = await File.ReadAllTextAsync(markdown);
        Assert.Contains("```mermaid\nflowchart", readable, StringComparison.Ordinal);
        Assert.Contains("[Start]", readable, StringComparison.Ordinal);
        Assert.Contains("[End]", readable, StringComparison.Ordinal);
        Assert.DoesNotContain(" --> ", readable, StringComparison.Ordinal);
        Assert.Contains("接続先未確定", readable, StringComparison.Ordinal);
    }

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
    public async Task Markdown_edits_reach_docx_content_control_and_equation_paragraphs_at_f1()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "control.docx");
        await WriteControlAndEquationDocxAsync(source);
        var markdown = Path.Combine(root, "control.md");
        var workspace = Path.Combine(root, "control.drmd");
        var output = Path.Combine(root, "restored.docx");
        var service = new DocumentService();

        await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown));
        var projection = await File.ReadAllTextAsync(markdown);
        // The equation projects as inline code, which parses back into the same Code run.
        Assert.Contains("Controlled body", projection, StringComparison.Ordinal);
        Assert.Contains("`x^2`", projection, StringComparison.Ordinal);
        await File.WriteAllTextAsync(markdown, projection
            .Replace("Controlled body", "Controlled edit", StringComparison.Ordinal)
            .Replace("Given ", "Because ", StringComparison.Ordinal));

        var restored = await service.RestoreAsync(new DocumentRestoreOptions(workspace, output, markdown));
        var patched = ReadDocumentXml(output);

        Assert.Equal(FidelityLevel.F1, restored.Fidelity);
        Assert.Contains("Controlled edit", patched, StringComparison.Ordinal);
        // The control's wrapping is outside the patched slice, and the equation stayed OMML rather
        // than being flattened into the linear text the editor saw.
        Assert.Contains("<w:sdtPr><w:alias w:val=\"Scope\" /></w:sdtPr>", patched, StringComparison.Ordinal);
        Assert.Contains("<m:oMath", patched, StringComparison.Ordinal);
        Assert.DoesNotContain("x^2", patched, StringComparison.Ordinal);
    }

    // A DOCX text box now advertises a replace-text policy in its DRMD marker, so its sentence is
    // an ordinary Markdown edit: the round trip carries it back to the w:txbxContent slice and the
    // shape it hangs off is copied byte for byte.
    [Fact]
    public async Task Markdown_edit_reaches_a_docx_text_box_at_f1()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "textbox.docx");
        await WriteTextBoxDocxAsync(source);
        var markdown = Path.Combine(root, "textbox.md");
        var workspace = Path.Combine(root, "textbox.drmd");
        var output = Path.Combine(root, "restored.docx");
        var service = new DocumentService();

        await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown));
        var projection = await File.ReadAllTextAsync(markdown);
        Assert.Contains("kind=text-box editability=text operations=replace-text", projection, StringComparison.Ordinal);
        Assert.Contains("Box text", projection, StringComparison.Ordinal);
        await File.WriteAllTextAsync(markdown, projection.Replace("Box text", "Box edited", StringComparison.Ordinal));

        var restored = await service.RestoreAsync(new DocumentRestoreOptions(workspace, output, markdown));
        var patched = ReadDocumentXml(output);

        Assert.Equal(FidelityLevel.F1, restored.Fidelity);
        Assert.Contains("<w:t>Box edited</w:t>", patched, StringComparison.Ordinal);
        Assert.DoesNotContain("Box text", patched, StringComparison.Ordinal);
        // The shape, the drawing anchor and the paragraph that holds them sit outside the box's
        // slice, so they come through untouched.
        Assert.Contains("<wps:cNvPr id=\"7\" name=\"Box\" />", patched, StringComparison.Ordinal);
        Assert.Contains("<w:t>Intro</w:t>", patched, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Markdown_edit_around_a_docx_inline_content_control_keeps_the_control_at_f1()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "inline-control.docx");
        await WriteInlineControlDocxAsync(source);
        var markdown = Path.Combine(root, "inline-control.md");
        var workspace = Path.Combine(root, "inline-control.drmd");
        var output = Path.Combine(root, "restored.docx");
        var service = new DocumentService();

        await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown));
        var projection = await File.ReadAllTextAsync(markdown);
        // The control's text is projected inline, exactly as if it were an ordinary run.
        Assert.Contains("Pick Choice here", projection, StringComparison.Ordinal);
        await File.WriteAllTextAsync(markdown, projection.Replace("Pick Choice here", "Pick Choice now", StringComparison.Ordinal));

        var restored = await service.RestoreAsync(new DocumentRestoreOptions(workspace, output, markdown));
        var patched = ReadDocumentXml(output);

        Assert.Equal(FidelityLevel.F1, restored.Fidelity);
        // The edit landed around the control, so the wrapper and the alias only it carries survive.
        Assert.Contains("<w:sdtPr><w:alias w:val=\"Option\" /></w:sdtPr><w:sdtContent><w:r><w:t>Choice</w:t></w:r></w:sdtContent>", patched, StringComparison.Ordinal);
        Assert.Contains("<w:t xml:space=\"preserve\"> now</w:t>", patched, StringComparison.Ordinal);
        Assert.DoesNotContain(restored.Diagnostics, item => item.Code == "DocxInlineContainerUnwrapped");
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
        Assert.DoesNotContain(exported.Diagnostics, item => item.Code == "PdfRasterizerUnavailable");
        var readable = await service.ExportReadableAsync(new ReadableDocumentExportOptions(
            source, Path.Combine(root, "readable.md"), EnableOcr: true));
        var readableText = await File.ReadAllTextAsync(readable.MarkdownPath);
        Assert.Contains("recognized text", readableText);
        Assert.DoesNotContain("rasterizer/OCR unavailable", readableText, StringComparison.Ordinal);
    }

    /// <summary>A one-page PDF whose content stream draws an Image XObject next to native text -
    /// the shape that used to lose the image with no link, no asset and no diagnostic.</summary>
    private static async Task WriteMixedTextAndImagePdfAsync(string path)
    {
        const string content = "BT 100 700 Td (Native body line) Tj ET\nq 200 0 0 100 50 400 cm /Im1 Do Q";
        await File.WriteAllBytesAsync(path, Encoding.Latin1.GetBytes(
            "%PDF-1.4\n1 0 obj << /Type /Page /Contents 2 0 R /Resources << /XObject << /Im1 5 0 R >> >> >> endobj\n" +
            "2 0 obj << /Length " + content.Length + " >> stream\n" + content + "\nendstream endobj\n" +
            "5 0 obj << /Type /XObject /Subtype /Image /Width 8 /Height 8 >> endobj\n%%EOF"));
    }

    [Fact]
    public async Task Pdf_page_mixing_text_and_image_rasterizes_and_keeps_only_non_native_ocr_text()
    {
        var root = TempDirectory();
        try
        {
            var source = Path.Combine(root, "mixed.pdf");
            await WriteMixedTextAndImagePdfAsync(source);
            // The rasterized page carries the body line too, so OCR reports it alongside the text
            // that only exists inside the picture.
            var service = new DocumentService(new FakeOcrEngine("Native body line", "Text inside the picture"), new FakePdfRasterizer());

            var result = await service.ExportReadableAsync(new ReadableDocumentExportOptions(
                source, Path.Combine(root, "mixed.md"), EnableOcr: true));

            var partition = Assert.Single(result.Graph.Partitions);
            Assert.Contains(partition.Nodes, node => node.Content is TextNodeContent text &&
                text.Text.Contains("Native body line", StringComparison.Ordinal));
            Assert.DoesNotContain(partition.Nodes, node => node.Extensions?.ContainsKey("pdf_embedded_image_placeholder") == true);
            var image = Assert.Single(partition.Nodes, node => node.Kind == NodeKind.Image);
            var reference = Assert.IsType<ReferenceNodeContent>(image.Content);
            Assert.Equal("PDF page 1 image", reference.AltText);
            Assert.True(image.Extensions!["pdf_page_raster"].GetBoolean());
            Assert.Equal("page-0001", Assert.Single(result.Graph.Assets!).Key);
            // The readable export materializes the page raster and rewrites the node to point at it.
            Assert.Contains("page-0001", reference.Reference, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(root, reference.Reference)));

            var imageText = Assert.Single(partition.Nodes, node => node.Kind == NodeKind.ImageText);
            Assert.Equal(image.Id, imageText.ParentId);
            Assert.Equal("Text inside the picture", Assert.IsType<TextNodeContent>(imageText.Content).Text);
            Assert.DoesNotContain(result.Diagnostics, item => item.Code == "PdfEmbeddedImageOmitted");
            Assert.Contains(result.Diagnostics, item => item.Code == "PdfEmbeddedImageRasterized" &&
                item.Severity == DiagnosticSeverity.Information);

            var markdown = await File.ReadAllTextAsync(result.MarkdownPath);
            Assert.Contains("Native body line", markdown, StringComparison.Ordinal);
            Assert.Contains("![PDF page 1 image](", markdown, StringComparison.Ordinal);
            Assert.Contains("page-0001", markdown, StringComparison.Ordinal);
            Assert.Contains("Text inside the picture", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("not extracted", markdown, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Pdf_page_raster_whose_ocr_only_repeats_native_text_gets_no_image_text_node()
    {
        var root = TempDirectory();
        try
        {
            var source = Path.Combine(root, "mixed.pdf");
            await WriteMixedTextAndImagePdfAsync(source);
            // A decorative image: everything OCR reads back is already native text (whitespace and
            // line breaks differ, which is why the comparison ignores them).
            var service = new DocumentService(new FakeOcrEngine("Native  body\nline"), new FakePdfRasterizer());

            var result = await service.ExportReadableAsync(new ReadableDocumentExportOptions(
                source, Path.Combine(root, "mixed.md"), EnableOcr: true));

            var partition = Assert.Single(result.Graph.Partitions);
            Assert.Single(partition.Nodes, node => node.Kind == NodeKind.Image);
            Assert.DoesNotContain(partition.Nodes, node => node.Kind == NodeKind.ImageText);
            Assert.DoesNotContain("ocr-extraction", await File.ReadAllTextAsync(result.MarkdownPath), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Pdf_page_mixing_text_and_image_keeps_the_placeholder_when_ocr_is_off()
    {
        var root = TempDirectory();
        try
        {
            var source = Path.Combine(root, "mixed.pdf");
            await WriteMixedTextAndImagePdfAsync(source);
            var rasterizer = new FakePdfRasterizer();

            var result = await new DocumentService(new FakeOcrEngine(), rasterizer).ExportReadableAsync(
                new ReadableDocumentExportOptions(source, Path.Combine(root, "mixed.md"), EnableOcr: false));

            Assert.Equal(0, rasterizer.Calls);
            Assert.Empty(result.Graph.Assets!);
            var partition = Assert.Single(result.Graph.Partitions);
            Assert.DoesNotContain(partition.Nodes, node => node.Kind is NodeKind.Image or NodeKind.ImageText);
            var placeholder = Assert.Single(partition.Nodes, node => node.Kind == NodeKind.Annotation);
            Assert.True(placeholder.Extensions!["pdf_embedded_image_placeholder"].GetBoolean());
            Assert.Contains(result.Diagnostics, item => item.Code == "PdfEmbeddedImageOmitted" &&
                item.Severity == DiagnosticSeverity.Warning);

            var markdown = await File.ReadAllTextAsync(result.MarkdownPath);
            Assert.Contains("Native body line", markdown, StringComparison.Ordinal);
            // The placeholder is projected as ordinary node text, so its leading bracket is
            // backslash-escaped like any other literal '[' (D07); it still reads as "[PDF page 1: ...".
            Assert.Contains("> \\[PDF page 1: 1 embedded image(s) not extracted", markdown, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A one-page PDF with a /MediaBox, native text, and one Image XObject per rectangle.
    /// The box is what lets the crop path map user space onto the rendered page, so a PDF without
    /// one (like <see cref="WriteMixedTextAndImagePdfAsync"/>'s) can only ever fall back.</summary>
    private static async Task WriteBoxedTextAndImagePdfAsync(string path, int rotation,
        params (int X, int Y, int Width, int Height)[] images)
    {
        var draws = string.Concat(images.Select((rect, index) =>
            $"\nq {rect.Width} 0 0 {rect.Height} {rect.X} {rect.Y} cm /Im{index + 1} Do Q"));
        var content = "BT 10 80 Td (Native body line) Tj ET" + draws;
        await File.WriteAllBytesAsync(path, Encoding.Latin1.GetBytes(
            "%PDF-1.4\n1 0 obj << /Type /Page /Contents 2 0 R /MediaBox [0 0 200 100]" +
            (rotation == 0 ? string.Empty : " /Rotate " + rotation) +
            " /Resources << /XObject << " +
            string.Join(" ", images.Select((_, index) => $"/Im{index + 1} {index + 5} 0 R")) +
            " >> >> >> endobj\n" +
            "2 0 obj << /Length " + content.Length + " >> stream\n" + content + "\nendstream endobj\n" +
            string.Concat(images.Select((_, index) =>
                $"{index + 5} 0 obj << /Type /XObject /Subtype /Image /Width 8 /Height 8 >> endobj\n")) +
            "%%EOF"));
    }

    [Fact]
    public async Task Pdf_embedded_image_is_cut_out_of_the_page_raster_so_ocr_reads_only_the_picture()
    {
        var root = TempDirectory();
        try
        {
            var source = Path.Combine(root, "mixed.pdf");
            await WriteBoxedTextAndImagePdfAsync(source, 0, (20, 30, 60, 40));
            // The MediaBox is 200x100 and the raster 400x200, so the image at (20,30)-(80,70) in
            // user space lands at (40,60)-(160,140) in pixels - user space grows upward, the
            // raster downward.
            var rasterizer = new PngPdfRasterizer(400, 200, (40, 60, 120, 80));
            // OCR answers with text the page already carries natively. The whole-page fallback
            // deletes exactly that as a duplicate, so its survival is what proves the cut-out
            // picture - not the page - was read.
            var ocr = new ImageRecordingOcrEngine("Native body line");

            var result = await new DocumentService(ocr, rasterizer).ExportReadableAsync(
                new ReadableDocumentExportOptions(source, Path.Combine(root, "mixed.md"), EnableOcr: true));

            var seen = Assert.Single(ocr.Seen);
            Assert.Equal("page-0001-img-01", seen.AssetId);
            Assert.Equal(120, seen.Width);
            Assert.Equal(80, seen.Height);
            Assert.True(seen.AllRed, "OCR was handed pixels from outside the image rectangle");

            // The whole-page raster was only an intermediate: the workspace keeps the crop alone.
            Assert.Equal("page-0001-img-01", Assert.Single(result.Graph.Assets!).Key);
            var partition = Assert.Single(result.Graph.Partitions);
            Assert.Contains(partition.Nodes, node => node.Content is TextNodeContent text &&
                text.Text.Contains("Native body line", StringComparison.Ordinal));
            Assert.DoesNotContain(partition.Nodes, node => node.Extensions?.ContainsKey("pdf_embedded_image_placeholder") == true);
            var image = Assert.Single(partition.Nodes, node => node.Kind == NodeKind.Image);
            Assert.True(image.Extensions!["pdf_embedded_image"].GetBoolean());
            Assert.Equal(1, image.Extensions["pdf_image_index"].GetInt32());
            Assert.False(image.Extensions.ContainsKey("pdf_page_raster"));
            Assert.Equal(20d, image.Geometry!.X, 6);
            Assert.Equal(30d, image.Geometry.Y, 6);
            Assert.Equal(60d, image.Geometry.Width, 6);
            Assert.Equal(40d, image.Geometry.Height, 6);
            var reference = Assert.IsType<ReferenceNodeContent>(image.Content);
            Assert.Equal("PDF page 1 image 1", reference.AltText);
            Assert.True(File.Exists(Path.Combine(root, reference.Reference)));

            var imageText = Assert.Single(partition.Nodes, node => node.Kind == NodeKind.ImageText);
            Assert.Equal(image.Id, imageText.ParentId);
            Assert.Equal("Native body line", Assert.IsType<TextNodeContent>(imageText.Content).Text);
            Assert.Contains(result.Diagnostics, item => item.Code == "PdfEmbeddedImageRasterized" &&
                item.Severity == DiagnosticSeverity.Information &&
                item.Message.Contains("PDF page 1: 1 embedded image(s) rasterized for OCR", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Diagnostics,
                item => item.Code is "PdfEmbeddedImageOmitted" or "PdfEmbeddedImageCropUnavailable");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Pdf_page_rotation_sends_the_embedded_image_back_to_whole_page_ocr()
    {
        var root = TempDirectory();
        try
        {
            var source = Path.Combine(root, "rotated.pdf");
            await WriteBoxedTextAndImagePdfAsync(source, 90, (20, 30, 60, 40));
            var ocr = new ImageRecordingOcrEngine("Native body line");

            var result = await new DocumentService(ocr, new PngPdfRasterizer(400, 200, (40, 60, 120, 80)))
                .ExportReadableAsync(new ReadableDocumentExportOptions(
                    source, Path.Combine(root, "rotated.md"), EnableOcr: true));

            // Rotation makes user space and the raster disagree on which axis is which, so the
            // whole page is read instead of a rectangle that would have been cut from the wrong place.
            var seen = Assert.Single(ocr.Seen);
            Assert.Equal("page-0001", seen.AssetId);
            Assert.Equal(400, seen.Width);
            Assert.Equal(200, seen.Height);
            Assert.Equal("page-0001", Assert.Single(result.Graph.Assets!).Key);
            var partition = Assert.Single(result.Graph.Partitions);
            var image = Assert.Single(partition.Nodes, node => node.Kind == NodeKind.Image);
            Assert.True(image.Extensions!["pdf_page_raster"].GetBoolean());
            // The page raster re-reads the native body line, and de-duplication is back on for it.
            Assert.DoesNotContain(partition.Nodes, node => node.Kind == NodeKind.ImageText);
            var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "PdfEmbeddedImageCropUnavailable");
            Assert.Equal(DiagnosticSeverity.Information, diagnostic.Severity);
            Assert.Contains("rotated 90", diagnostic.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Two_embedded_images_become_two_assets_ordered_from_the_top_of_the_page()
    {
        var root = TempDirectory();
        try
        {
            var source = Path.Combine(root, "two.pdf");
            // Drawn bottom-first, so content-stream order and reading order disagree.
            await WriteBoxedTextAndImagePdfAsync(source, 0, (10, 10, 40, 20), (10, 70, 40, 20));
            // Scale 2 again: the top image lands at (20,20)-(100,60), the bottom at (20,140)-(100,180).
            var rasterizer = new PngPdfRasterizer(400, 200, (20, 20, 80, 40), (20, 140, 80, 40));
            var ocr = new ImageRecordingOcrEngine("top picture", "bottom picture");

            var result = await new DocumentService(ocr, rasterizer).ExportReadableAsync(
                new ReadableDocumentExportOptions(source, Path.Combine(root, "two.md"), EnableOcr: true));

            Assert.Equal(["page-0001-img-01", "page-0001-img-02"], ocr.Seen.Select(item => item.AssetId));
            Assert.All(ocr.Seen, item =>
            {
                Assert.Equal(80, item.Width);
                Assert.Equal(40, item.Height);
                Assert.True(item.AllRed, "OCR was handed pixels from outside the image rectangle");
            });
            Assert.Equal(["page-0001-img-01", "page-0001-img-02"], result.Graph.Assets!.Keys.Order());

            var partition = Assert.Single(result.Graph.Partitions);
            var images = partition.Nodes.Where(node => node.Kind == NodeKind.Image).ToArray();
            Assert.Equal(2, images.Length);
            Assert.Equal(70d, images[0].Geometry!.Y, 6);
            Assert.Equal(10d, images[1].Geometry!.Y, 6);
            Assert.True(images[0].Order < images[1].Order);
            Assert.Equal("PDF page 1 image 1", Assert.IsType<ReferenceNodeContent>(images[0].Content).AltText);
            Assert.Equal("PDF page 1 image 2", Assert.IsType<ReferenceNodeContent>(images[1].Content).AltText);

            var texts = partition.Nodes.Where(node => node.Kind == NodeKind.ImageText).ToArray();
            Assert.Equal(2, texts.Length);
            Assert.Equal(images[0].Id, texts[0].ParentId);
            Assert.Equal("top picture", Assert.IsType<TextNodeContent>(texts[0].Content).Text);
            Assert.Equal(images[1].Id, texts[1].ParentId);
            Assert.Equal("bottom picture", Assert.IsType<TextNodeContent>(texts[1].Content).Text);
            Assert.Contains(result.Diagnostics, item => item.Code == "PdfEmbeddedImageRasterized" &&
                item.Message.Contains("PDF page 1: 2 embedded image(s) rasterized for OCR", StringComparison.Ordinal));

            var markdown = await File.ReadAllTextAsync(result.MarkdownPath);
            Assert.Contains("top picture", markdown, StringComparison.Ordinal);
            Assert.Contains("bottom picture", markdown, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Textless_pdf_without_rasterizer_reports_explicit_unavailability()
    {
        var root = TempDirectory();
        try
        {
            var source = Path.Combine(root, "scan.pdf");
            await new MarkdownRenderer().RenderAsync(string.Empty, RenderFormat.Pdf, source);
            var exported = await new DocumentService(new FakeOcrEngine(), null, discoverPdfRasterizer: false).ExportAsync(
                new DocumentExportOptions(
                    source,
                    Path.Combine(root, "scan.drmd"),
                    Path.Combine(root, "scan.md"),
                    EnableOcr: true,
                    OcrLanguages: ["jpn"]));

            var diagnostic = Assert.Single(exported.Diagnostics, item =>
                item.Code == "PdfRasterizerUnavailable");
            Assert.Contains("docredock doctor", diagnostic.Message, StringComparison.Ordinal);
            Assert.Empty(exported.Graph.Nodes);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public async Task Invalid_rasterizer_page_mapping_falls_back_without_attaching_wrong_ocr(int returnedPage, bool duplicate)
    {
        var root = TempDirectory();
        try
        {
            var source = Path.Combine(root, "scan.pdf");
            await new MarkdownRenderer().RenderAsync(string.Empty, RenderFormat.Pdf, source);
            var result = await new DocumentService(new FakeOcrEngine(), new FakePdfRasterizer(returnedPage, duplicate))
                .ExportReadableAsync(new ReadableDocumentExportOptions(source, Path.Combine(root, "scan.md"), EnableOcr: true));
            Assert.Contains(result.Diagnostics, item => item.Code == "PdfRasterizationFailed");
            Assert.DoesNotContain("recognized text", await File.ReadAllTextAsync(result.MarkdownPath), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Ocr_disabled_does_not_invoke_an_available_rasterizer()
    {
        var root = TempDirectory();
        try
        {
            var source = Path.Combine(root, "scan.pdf");
            await new MarkdownRenderer().RenderAsync(string.Empty, RenderFormat.Pdf, source);
            var provider = new FakePdfRasterizer();
            await new DocumentService(new FakeOcrEngine(), provider).ExportReadableAsync(
                new ReadableDocumentExportOptions(source, Path.Combine(root, "scan.md"), EnableOcr: false));
            Assert.Equal(0, provider.Calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Xlsx_readable_export_with_unknown_sheet_name_throws_and_writes_nothing()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "source.xlsx");
        WriteXlsxWorkbook(source, ("Overview", "visible", "Overview text"), ("Secret", "hidden", "Secret text"));
        var markdown = Path.Combine(root, "source.md");

        var exception = await Assert.ThrowsAsync<SheetSelectionException>(() =>
            new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, markdown, Sheets: ["DoesNotExist"])));

        Assert.Equal(["DoesNotExist"], exception.UnknownSheets);
        Assert.Contains("DoesNotExist", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Overview", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Secret (hidden)", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(markdown));
    }

    [Fact]
    public async Task Xlsx_readable_export_with_a_partial_sheet_name_mismatch_throws_instead_of_ignoring_the_unknown_name()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "source.xlsx");
        WriteXlsxWorkbook(source, ("Overview", "visible", "Overview text"), ("Secret", "hidden", "Secret text"));
        var markdown = Path.Combine(root, "source.md");

        var exception = await Assert.ThrowsAsync<SheetSelectionException>(() =>
            new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(
                source, markdown, Sheets: ["Overview", "DoesNotExist"])));

        Assert.Equal(["DoesNotExist"], exception.UnknownSheets);
        Assert.False(File.Exists(markdown));
    }

    [Fact]
    public async Task Xlsx_hidden_sheet_requested_under_visible_policy_warns_instead_of_erroring()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "source.xlsx");
        WriteXlsxWorkbook(source, ("Overview", "visible", "Overview text"), ("Secret", "hidden", "Secret text"));
        var markdown = Path.Combine(root, "source.md");

        var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(
            source, markdown, ContentPolicy: "visible", Sheets: ["Secret"]));

        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "XlsxSheetExcludedByPolicy");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Secret", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("visible", diagnostic.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(markdown));
        Assert.DoesNotContain("Secret text", await File.ReadAllTextAsync(markdown), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Xlsx_hidden_sheet_requested_under_complete_policy_is_exported_without_the_policy_warning()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "source.xlsx");
        WriteXlsxWorkbook(source, ("Overview", "visible", "Overview text"), ("Secret", "hidden", "Secret text"));
        var markdown = Path.Combine(root, "source.md");

        var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(
            source, markdown, ContentPolicy: "complete", Sheets: ["Secret"]));

        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "XlsxSheetExcludedByPolicy");
        Assert.Contains(result.Diagnostics, item => item.Code == "HiddenContentIncluded");
        Assert.Contains("Secret text", await File.ReadAllTextAsync(markdown), StringComparison.Ordinal);
    }

    private static void WriteXlsxWorkbook(string path, params (string Name, string State, string Text)[] sheets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        void Write(string entryPath, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entryPath).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        Write("[Content_Types].xml", """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
            </Types>
            """);
        Write("_rels/.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        var sheetElements = string.Join(string.Empty, sheets.Select((sheet, index) =>
            $"<sheet name=\"{sheet.Name}\" sheetId=\"{index + 1}\"" +
            (StringComparer.OrdinalIgnoreCase.Equals(sheet.State, "visible") ? string.Empty : $" state=\"{sheet.State}\"") +
            $" r:id=\"rId{index + 1}\"/>"));
        Write("xl/workbook.xml", $"""
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>{sheetElements}</sheets>
            </workbook>
            """);
        var relationshipElements = string.Join(string.Empty, sheets.Select((_, index) =>
            $"<Relationship Id=\"rId{index + 1}\" Type=\"worksheet\" Target=\"worksheets/sheet{index + 1}.xml\"/>"));
        Write("xl/_rels/workbook.xml.rels", $"""
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{relationshipElements}</Relationships>
            """);
        for (var index = 0; index < sheets.Length; index++)
            Write($"xl/worksheets/sheet{index + 1}.xml", $"""
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                  <row r="1"><c r="A1" t="inlineStr"><is><t>{sheets[index].Text}</t></is></c></row>
                </sheetData></worksheet>
                """);
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "docredock-service-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string ReadDocumentXml(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>A DOCX whose body holds a block content control and an inline equation - the two
    /// shapes that used to project read-only and so never reached the F1 patcher at all.</summary>
    private static async Task WriteControlAndEquationDocxAsync(string path)
    {
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await WriteEntry(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await WriteEntry(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await WriteEntry(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:m="http://schemas.openxmlformats.org/officeDocument/2006/math"><w:body>
            <w:sdt><w:sdtPr><w:alias w:val="Scope" /></w:sdtPr><w:sdtContent>
              <w:p><w:r><w:t>Controlled body</w:t></w:r></w:p>
            </w:sdtContent></w:sdt>
            <w:p><w:r><w:t xml:space="preserve">Given </w:t></w:r><m:oMath><m:sSup><m:e><m:r><m:t>x</m:t></m:r></m:e><m:sup><m:r><m:t>2</m:t></m:r></m:sup></m:sSup></m:oMath><w:r><w:t xml:space="preserve"> holds.</w:t></w:r></w:p>
            </w:body></w:document>
            """);
    }

    /// <summary>A DOCX holding a DrawingML text box, whose w:txbxContent is now a slice of its own
    /// and so an ordinary Markdown edit target.</summary>
    private static async Task WriteTextBoxDocxAsync(string path)
    {
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await WriteEntry(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await WriteEntry(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await WriteEntry(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
              xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
              xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
              xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape"><w:body>
            <w:p><w:r><w:t>Intro</w:t></w:r></w:p>
            <w:p><w:r><w:drawing><wp:inline><a:graphic><a:graphicData><wps:wsp><wps:cNvPr id="7" name="Box" /><wps:txbx><w:txbxContent><w:p><w:r><w:t>Box text</w:t></w:r></w:p></w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
            <w:p><w:r><w:t>Tail</w:t></w:r></w:p><w:sectPr />
            </w:body></w:document>
            """);
    }

    /// <summary>A DOCX whose only paragraph wraps part of its text in an inline content control -
    /// the shape whose whole paragraph used to be refused by the F1 patcher.</summary>
    private static async Task WriteInlineControlDocxAsync(string path)
    {
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await WriteEntry(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await WriteEntry(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await WriteEntry(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
            <w:p><w:r><w:t xml:space="preserve">Pick </w:t></w:r><w:sdt><w:sdtPr><w:alias w:val="Option" /></w:sdtPr><w:sdtContent><w:r><w:t>Choice</w:t></w:r></w:sdtContent></w:sdt><w:r><w:t xml:space="preserve"> here</w:t></w:r></w:p>
            <w:p><w:r><w:t>Tail</w:t></w:r></w:p><w:sectPr />
            </w:body></w:document>
            """);
    }

    private static async Task WriteEntry(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name);
        await using var stream = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        await stream.WriteAsync(text);
    }

    private sealed class FakeOcrEngine(params string[] regionTexts) : IOcrEngine
    {
        private readonly string[] regions = regionTexts.Length > 0 ? regionTexts : ["recognized text"];

        public ProviderDescriptor Descriptor { get; } = new("test.ocr", new Version(1, 0), 1,
            new HashSet<string> { "ocr.text" }, "MIT", "built-in", true);

        public ValueTask<OcrAttemptResult> RecognizeAsync(OcrInput input, OcrOptions options, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OcrAttemptResult(OcrProcessingStatus.Completed,
                new OcrResult(string.Join("\n", regions), regions
                    .Select((text, index) => new OcrTextRegion(text, new Geometry("image-pixels", 0, index * 10, 10, 10), 0.92))
                    .ToArray()), []));
    }

    /// <summary>Answers with a real PNG page - white with one red block per rectangle - so the crop
    /// path has something to decode and a test can tell exactly which pixels were cut out.</summary>
    private sealed class PngPdfRasterizer(int width, int height, params (int X, int Y, int Width, int Height)[] redBlocks)
        : IPdfRasterizer
    {
        public ProviderDescriptor Descriptor { get; } = new("test.pdf.rasterizer", new Version(1, 0), 1,
            new HashSet<string> { "rasterize.pdf" }, "MIT", "built-in", true);

        public ValueTask<IReadOnlyList<RasterizedPdfPage>> RasterizeAsync(string pdfPath, IReadOnlyList<int> pageNumbers,
            PdfRasterizationOptions options, CancellationToken cancellationToken = default)
        {
            var rgb = new byte[width * height * 3];
            Array.Fill(rgb, (byte)255);
            foreach (var block in redBlocks)
                for (var y = block.Y; y < block.Y + block.Height; y++)
                    for (var x = block.X; x < block.X + block.Width; x++)
                    {
                        var at = (y * width + x) * 3;
                        rgb[at] = 255; rgb[at + 1] = 0; rgb[at + 2] = 0;
                    }
            var png = PngRasterImage.Encode(width, height, rgb);
            return ValueTask.FromResult<IReadOnlyList<RasterizedPdfPage>>(pageNumbers
                .Select(page => new RasterizedPdfPage(page, "image/png", png, width, height)).ToList());
        }
    }

    /// <summary>Records the size and colour of every image handed to OCR, so a test can prove the
    /// engine saw the cut-out picture rather than the whole page. Answers with one text per call in
    /// call order, repeating the last.</summary>
    private sealed class ImageRecordingOcrEngine(params string[] texts) : IOcrEngine
    {
        public List<(string AssetId, int Width, int Height, bool AllRed)> Seen { get; } = [];

        public ProviderDescriptor Descriptor { get; } = new("test.ocr", new Version(1, 0), 1,
            new HashSet<string> { "ocr.text" }, "MIT", "built-in", true);

        public ValueTask<OcrAttemptResult> RecognizeAsync(OcrInput input, OcrOptions options, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            input.Image.CopyTo(buffer);
            var image = PngRasterImage.Decode(buffer.ToArray());
            var allRed = Enumerable.Range(0, image.Width * image.Height).All(pixel =>
                image.RgbBytes[pixel * 3] == 255 && image.RgbBytes[pixel * 3 + 1] == 0 && image.RgbBytes[pixel * 3 + 2] == 0);
            Seen.Add((input.AssetId, image.Width, image.Height, allRed));
            var text = texts.Length == 0 ? "recognized text" : texts[Math.Min(Seen.Count - 1, texts.Length - 1)];
            return ValueTask.FromResult(new OcrAttemptResult(OcrProcessingStatus.Completed,
                new OcrResult(text, [new OcrTextRegion(text, new Geometry("image-pixels", 0, 0, image.Width, image.Height), 0.9)]), []));
        }
    }

    private sealed class FakePdfRasterizer(int? forcedPage = null, bool duplicate = false) : IPdfRasterizer
    {
        public int Calls { get; private set; }
        public ProviderDescriptor Descriptor { get; } = new("test.pdf.rasterizer", new Version(1, 0), 1,
            new HashSet<string> { "rasterize.pdf" }, "MIT", "built-in", true);

        public ValueTask<IReadOnlyList<RasterizedPdfPage>> RasterizeAsync(string pdfPath, IReadOnlyList<int> pageNumbers,
            PdfRasterizationOptions options, CancellationToken cancellationToken = default)
        {
            Calls++;
            var pages = pageNumbers.Select(page => new RasterizedPdfPage(forcedPage ?? page, "image/png", new byte[] { 1, 2, 3 }, 10, 10)).ToList();
            if (duplicate && pages.Count > 0) pages.Add(pages[0]);
            return ValueTask.FromResult<IReadOnlyList<RasterizedPdfPage>>(pages);
        }
    }
}
