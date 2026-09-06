using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Docx;
using DocRedock.Gui;
using DocRedock.Render;
using DocRedock.RoundTrip;

namespace DocRedock.Tests.Gui;

[Collection("Environment variables")]
public sealed class GuiWorkflowServiceTests : IDisposable
{
    private readonly string? previousExperimental = Environment.GetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL");

    public GuiWorkflowServiceTests() => Environment.SetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL", "1");

    public void Dispose() => Environment.SetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL", previousExperimental);
    [Fact]
    public async Task Readable_export_creates_only_markdown_without_a_restore_package()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "proposal.docx");
        await new MarkdownRenderer().RenderAsync("# Title\n\nReadable body", RenderFormat.Docx, source);
        var exportDirectory = Path.Combine(fixture.Root, "readable");
        var workflow = new GuiWorkflowService();

        var exported = await workflow.ExportAsync(source, exportDirectory, enableOcr: false, readable: true);

        Assert.True(exported.IsReadable);
        Assert.Equal(string.Empty, exported.PackagePath);
        Assert.Equal([exported.MarkdownPath], Directory.EnumerateFileSystemEntries(exportDirectory));
        Assert.DoesNotContain("drmd_schema", await File.ReadAllTextAsync(exported.MarkdownPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readable_export_can_embed_images_into_a_self_contained_markdown_file()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "images.xlsx");
        await new MarkdownRenderer().RenderAsync("# Image workbook", RenderFormat.Xlsx, source);
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
        await using (var media = archive.CreateEntry("xl/media/image1.png").Open())
            await media.WriteAsync(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var exported = await new GuiWorkflowService().ExportAsync(
            source,
            Path.Combine(fixture.Root, "readable"),
            enableOcr: false,
            readable: true,
            embedReadableImages: true);

        var markdown = await File.ReadAllTextAsync(exported.MarkdownPath);
        Assert.Contains("data:image/png;base64,iVBORw0KGgo=", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(".assets/", markdown, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "readable", "images.assets")));
        Assert.Equal([exported.MarkdownPath], Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(exported.MarkdownPath)!));
    }

    [Fact]
    public async Task Office_round_trip_export_and_restore_are_available_without_experimental_opt_in()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "proposal.docx");
        await new MarkdownRenderer().RenderAsync("# Title", RenderFormat.Docx, source);
        var previous = Environment.GetEnvironmentVariable(ExperimentalFeatures.EnvironmentVariable);
        Environment.SetEnvironmentVariable(ExperimentalFeatures.EnvironmentVariable, null);
        try
        {
            var workflow = new GuiWorkflowService();
            var exported = await workflow.ExportAsync(
                source, Path.Combine(fixture.Root, "export"), enableOcr: false);

            Assert.False(exported.IsReadable);
            Assert.EndsWith(".drmd", exported.SidecarPath, StringComparison.Ordinal);

            var restored = await workflow.RestoreAsync(
                exported.MarkdownPath,
                exported.SidecarPath,
                Path.Combine(fixture.Root, "restore"),
                allowPdfRenderFallback: false);

            Assert.True(restored.Succeeded);
            Assert.Equal("F0", restored.Fidelity);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalFeatures.EnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task Export_and_restore_round_trip_an_edited_docx_through_directory_sidecar()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "proposal.docx");
        await new MarkdownRenderer().RenderAsync("# Title\n\nBefore", RenderFormat.Docx, source);
        var exportDirectory = Path.Combine(fixture.Root, "export");
        var restoreDirectory = Path.Combine(fixture.Root, "restore");
        var workflow = new GuiWorkflowService();

        var exported = await workflow.ExportAsync(source, exportDirectory, enableOcr: false);
        await File.WriteAllTextAsync(
            exported.MarkdownPath,
            (await File.ReadAllTextAsync(exported.MarkdownPath)).Replace("Before", "After", StringComparison.Ordinal));
        var restored = await workflow.RestoreAsync(
            exported.MarkdownPath,
            exported.PackagePath,
            restoreDirectory,
            allowPdfRenderFallback: false);

        Assert.True(restored.Succeeded);
        Assert.Equal("F1", restored.Fidelity);
        Assert.True(File.Exists(exported.MarkdownPath));
        Assert.True(Directory.Exists(exported.SidecarPath));
        Assert.EndsWith(".drmd", exported.SidecarPath, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.ChangeExtension(exported.MarkdownPath, ".drmdpkg")));
        Assert.Equal(
            new[] { exported.MarkdownPath, exported.SidecarPath }.Order(StringComparer.Ordinal),
            Directory.EnumerateFileSystemEntries(exportDirectory).Order(StringComparer.Ordinal));
        var extraction = await new DocxAdapter().ExtractAsync(restored.OutputPath);
        Assert.Contains(extraction.Graph.Nodes, node => node.Content switch
        {
            TextNodeContent text => text.Text.Contains("After", StringComparison.Ordinal),
            RichTextNodeContent rich => rich.Runs.Any(run => run.Text.Contains("After", StringComparison.Ordinal)),
            _ => false,
        });
    }

    [Fact]
    public async Task Unedited_pdf_is_restored_byte_identically_without_render_fallback()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "report.pdf");
        await new MarkdownRenderer().RenderAsync("PDF text", RenderFormat.Pdf, source);
        var workflow = new GuiWorkflowService();
        var exported = await workflow.ExportAsync(source, Path.Combine(fixture.Root, "export"), enableOcr: false);

        var restored = await workflow.RestoreAsync(
            exported.MarkdownPath,
            exported.PackagePath,
            Path.Combine(fixture.Root, "restore"),
            allowPdfRenderFallback: false);

        Assert.True(restored.Succeeded);
        Assert.Equal("F0", restored.Fidelity);
        Assert.Equal(Hash(source), Hash(restored.OutputPath));
    }

    [Fact]
    public async Task Export_refuses_to_replace_existing_outputs()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "proposal.docx");
        await new MarkdownRenderer().RenderAsync("Original", RenderFormat.Docx, source);
        var outputDirectory = Path.Combine(fixture.Root, "export");
        var workflow = new GuiWorkflowService();
        var first = await workflow.ExportAsync(source, outputDirectory, enableOcr: false);
        var originalMarkdown = await File.ReadAllTextAsync(first.MarkdownPath);

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            workflow.ExportAsync(source, outputDirectory, enableOcr: false));

        Assert.Contains("既存の出力", exception.Message, StringComparison.Ordinal);
        Assert.Equal(originalMarkdown, await File.ReadAllTextAsync(first.MarkdownPath));
    }

    [Fact]
    public async Task Export_can_choose_a_numbered_name_for_repeated_gui_runs()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "proposal.docx");
        await new MarkdownRenderer().RenderAsync("Original", RenderFormat.Docx, source);
        var outputDirectory = Path.Combine(fixture.Root, "export");
        var workflow = new GuiWorkflowService();

        var first = await workflow.ExportAsync(source, outputDirectory, enableOcr: false);
        var second = await workflow.ExportAsync(source, outputDirectory, enableOcr: false, useUniqueName: true);

        Assert.NotEqual(first.MarkdownPath, second.MarkdownPath);
        Assert.EndsWith(" (2).md", second.MarkdownPath, StringComparison.Ordinal);
        Assert.EndsWith(" (2).drmd", second.SidecarPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_can_pack_the_sidecar_as_a_zip_and_restore_from_it()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "portable.docx");
        await new MarkdownRenderer().RenderAsync("Portable", RenderFormat.Docx, source);
        var workflow = new GuiWorkflowService();

        var exported = await workflow.ExportAsync(
            source,
            Path.Combine(fixture.Root, "export"),
            enableOcr: false,
            zipSidecar: true);

        Assert.Equal(SidecarForm.Zip, exported.SidecarForm);
        Assert.True(File.Exists(exported.SidecarPath));
        Assert.False(Directory.Exists(exported.SidecarPath));
        Assert.Equal(SidecarForm.Zip, SidecarContainer.Detect(exported.SidecarPath));

        var restored = await workflow.RestoreAsync(
            exported.MarkdownPath,
            exported.SidecarPath,
            Path.Combine(fixture.Root, "restore"),
            allowPdfRenderFallback: false);

        Assert.True(restored.Succeeded);
        Assert.Contains(restored.Diagnostics, diagnostic => diagnostic.Code == "SidecarZipFormReadOnly");
    }

    [Fact]
    public async Task Restore_refuses_to_replace_an_existing_document()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "proposal.docx");
        await new MarkdownRenderer().RenderAsync("Original", RenderFormat.Docx, source);
        var workflow = new GuiWorkflowService();
        var exported = await workflow.ExportAsync(source, Path.Combine(fixture.Root, "export"), enableOcr: false);
        var restoreDirectory = Path.Combine(fixture.Root, "restore");
        var first = await workflow.RestoreAsync(
            exported.MarkdownPath,
            exported.PackagePath,
            restoreDirectory,
            allowPdfRenderFallback: false);
        var originalHash = Hash(first.OutputPath);

        var exception = await Assert.ThrowsAsync<IOException>(() => workflow.RestoreAsync(
            exported.MarkdownPath,
            exported.PackagePath,
            restoreDirectory,
            allowPdfRenderFallback: false));

        Assert.Contains("既存の出力", exception.Message, StringComparison.Ordinal);
        Assert.Equal(originalHash, Hash(first.OutputPath));
    }

    [Theory]
    [InlineData("../../report.docx", "report.docx")]
    [InlineData("..\\..\\report.docx", "report.docx")]
    [InlineData("", "document")]
    [InlineData("sales:2026.xlsx", "sales2026.xlsx")]
    [InlineData("CON.pdf", "_CON.pdf")]
    public void Uploaded_file_names_are_reduced_to_safe_local_names(string input, string expected)
    {
        Assert.Equal(expected, GuiWorkflowService.SafeFileName(input, "document"));
    }

    [Fact]
    public async Task Pdf_export_and_restore_work_without_experimental_opt_in()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "scan.pdf");
        await new MarkdownRenderer().RenderAsync("PDF text", RenderFormat.Pdf, source);
        var previous = Environment.GetEnvironmentVariable(ExperimentalFeatures.EnvironmentVariable);
        Environment.SetEnvironmentVariable(ExperimentalFeatures.EnvironmentVariable, null);
        try
        {
            var workflow = new GuiWorkflowService();
            var exported = await workflow.ExportAsync(
                source,
                Path.Combine(fixture.Root, "export"),
                enableOcr: false);

            Assert.Equal("pdf", exported.Format);
            Assert.EndsWith(".drmd", exported.SidecarPath, StringComparison.Ordinal);

            var restored = await workflow.RestoreAsync(
                exported.MarkdownPath,
                exported.SidecarPath,
                Path.Combine(fixture.Root, "restore"),
                allowPdfRenderFallback: false);

            Assert.True(restored.Succeeded);
            Assert.Equal("F0", restored.Fidelity);
            Assert.Equal(Hash(source), Hash(restored.OutputPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalFeatures.EnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task Readable_export_visual_summary_agrees_with_export_summary_for_a_resolved_connector_pdf()
    {
        // Mirrors the CLI's "Resolved_connector_pdf_export_reports_consistent_zero_fallback_and_
        // warning_counts" (F-05): the GUI used to recount visual-graph nodes ("図: {count}件") and
        // raw VisualPath.IsFallback separately from ExportSummary, so the two lines shown together
        // in the GUI could disagree. Both must now render from one ExportSummary instance.
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "diagram.pdf");
        await File.WriteAllBytesAsync(source, Encoding.Latin1.GetBytes("""
            %PDF-1.4
            1 0 obj << /Type /Page >> endobj
            2 0 obj << /Length 300 >> stream
            BT 1 0 0 1 10 20 Tm (PDF_FLOW_START) Tj ET
            0 0 100 50 re S 0 0 100 50 re S
            BT 1 0 0 1 210 20 Tm (PDF_FLOW_DONE) Tj ET
            200 0 100 50 re S
            100 25 m 200 25 l S
            200 25 m 190 32 l 190 18 l h f
            endstream
            %%EOF
            """));
        var previous = Environment.GetEnvironmentVariable(ExperimentalFeatures.EnvironmentVariable);
        Environment.SetEnvironmentVariable(ExperimentalFeatures.EnvironmentVariable, null);
        try
        {
            var workflow = new GuiWorkflowService();

            var exported = await workflow.ExportAsync(
                source, Path.Combine(fixture.Root, "export"), enableOcr: false, readable: true);

            Assert.NotNull(exported.VisualSummary);
            Assert.Contains("図: 1件（vector page 1）", exported.VisualSummary, StringComparison.Ordinal);
            Assert.Contains("unresolved 0", exported.VisualSummary, StringComparison.Ordinal);
            Assert.Contains("fallback 0", exported.VisualSummary, StringComparison.Ordinal);
            Assert.Contains("Diagrams reconstructed: 1", exported.ExportSummary, StringComparison.Ordinal);
            Assert.Contains("Fallback pages: 0", exported.ExportSummary, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalFeatures.EnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task Readable_export_visual_summary_is_absent_without_a_visual_graph()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "proposal.docx");
        await new MarkdownRenderer().RenderAsync("# Title\n\nPlain text, no diagrams", RenderFormat.Docx, source);
        var workflow = new GuiWorkflowService();

        var exported = await workflow.ExportAsync(
            source, Path.Combine(fixture.Root, "export"), enableOcr: false, readable: true);

        Assert.Null(exported.VisualSummary);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "docredock-gui-tests", Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
