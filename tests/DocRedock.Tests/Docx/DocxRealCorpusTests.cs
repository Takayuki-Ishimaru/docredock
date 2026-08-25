using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Docx;

namespace DocRedock.Tests.Docx;

/// <summary>
/// Regression coverage against the real DOCX used by the office round-trip check,
/// plus compact OOXML fixtures for boundaries that must remain safe to reject.
/// The real corpus is checked into the test fixtures so the regression suite has a
/// deterministic document even when ignored build artifacts are absent. The original
/// office-roundtrip artifact is the provenance/reference document for visual review.
/// </summary>
public sealed class DocxRealCorpusTests
{
    [Fact]
    public async Task Real_office_corpus_extracts_lists_tables_images_and_furniture_and_keeps_f0()
    {
        var source = FindRealCorpus();
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var nodes = export.Graph.Nodes.ToArray();

        Assert.Contains(nodes, node => node.Kind == NodeKind.Heading);
        Assert.True(nodes.Count(node => node.Kind == NodeKind.ListItem) >= 8,
            "ListBullet/ListNumber paragraph styles must be projected as ListItem nodes.");
        Assert.Equal(2, nodes.Count(node => node.Kind == NodeKind.Table));
        Assert.Equal(2, nodes.Count(node => node.Kind == NodeKind.Image));
        Assert.Contains(nodes, node => node.Kind == NodeKind.Header && node.Layer == ContentLayer.Furniture);
        Assert.Contains(nodes, node => node.Kind == NodeKind.Footer && node.Layer == ContentLayer.Furniture);
        Assert.DoesNotContain(export.Diagnostics, diagnostic => diagnostic.Severity == DocRedock.Core.Reporting.DiagnosticSeverity.Error);

        var output = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"), "f0.docx");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var restored = await adapter.RestoreAsync(export, export.Graph, output);

        Assert.True(restored.Succeeded);
        Assert.Equal(Hash(source), Hash(output));
    }

    [Fact]
    public async Task External_hyperlink_is_extracted_without_dereferencing_and_edit_is_rejected()
    {
        var source = await CreateEdgeDocxAsync("hyperlink");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var link = Assert.Single(export.Graph.Nodes, node => node.Kind == NodeKind.Link);
        var content = Assert.IsType<ReferenceNodeContent>(link.Content);

        Assert.Equal("https://example.invalid/docredock", content.Reference);
        Assert.Equal("DocRedock docs", content.AltText);

        var paragraph = export.Graph.Nodes.Single(node => node.Kind == NodeKind.Paragraph);
        var edited = export.Graph with
        {
            Partitions = [new DocumentPartition("part-0001", 0,
                export.Graph.Nodes.Select(node => node.Id == paragraph.Id
                    ? node with { Content = new TextNodeContent("changed") }
                    : node).ToArray(), "/word/document.xml")]
        };
        var output = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"), "hyperlink.docx");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.RestoreAsync(export, edited, output).AsTask());
    }

    [Fact]
    public async Task Fields_and_tracked_revisions_are_detected_and_plain_text_edits_are_rejected()
    {
        var adapter = new DocxAdapter();
        foreach (var kind in new[] { "field", "revision" })
        {
            var source = await CreateEdgeDocxAsync(kind);
            var export = await adapter.ExtractAsync(source);
            var paragraph = export.Graph.Nodes.Single(node => node.Kind == NodeKind.Paragraph);
            var edited = export.Graph with
            {
                Partitions = [new DocumentPartition("part-0001", 0,
                    export.Graph.Nodes.Select(node => node.Id == paragraph.Id
                        ? node with { Content = new TextNodeContent("changed") }
                        : node).ToArray(), "/word/document.xml")]
            };
            var output = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"), kind + ".docx");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);

            if (kind == "revision")
            {
                Assert.True(export.SourceIndex.HasTrackedRevisions);
                Assert.Contains(export.Diagnostics, diagnostic => diagnostic.Code == "TrackedRevisionsPresent");
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => adapter.RestoreAsync(export, edited, output).AsTask());
        }
    }

    [Fact]
    public async Task Settings_document_protection_is_detected_and_strict_restore_refuses_edits()
    {
        var source = await CreateEdgeDocxAsync("protection");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        Assert.True(export.SourceIndex.HasDocumentProtection);
        Assert.Contains(export.Diagnostics, diagnostic => diagnostic.Code == "DocumentProtected");

        var paragraph = export.Graph.Nodes.Single(node => node.Kind == NodeKind.Paragraph);
        var edited = export.Graph with
        {
            Partitions = [new DocumentPartition("part-0001", 0,
                export.Graph.Nodes.Select(node => node.Id == paragraph.Id
                    ? node with { Content = new TextNodeContent("changed") }
                    : node).ToArray(), "/word/document.xml")]
        };
        var output = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"), "protected.docx");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var result = await adapter.RestoreAsync(export, edited, output);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ProtectedPackage");
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Footnotes_are_projected_as_protected_body_evidence()
    {
        var source = await CreateEdgeDocxAsync("footnote");
        var export = await new DocxAdapter().ExtractAsync(source);
        var footnote = Assert.Single(export.Graph.Nodes, node => node.Kind == NodeKind.Footnote);

        Assert.Equal(ContentLayer.Body, footnote.Layer);
        Assert.Equal(NodeEditability.Protected, footnote.Editability);
        Assert.Equal("Footnote evidence", Assert.IsType<TextNodeContent>(footnote.Content).Text);
    }

    private static string FindRealCorpus()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var checkedInCandidate = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Docx", "real-office-roundtrip.original.docx");
            if (File.Exists(checkedInCandidate)) return checkedInCandidate;
            current = current.Parent;
        }

        var workingCandidate = Path.GetFullPath("tests/DocRedock.Tests/Fixtures/Docx/real-office-roundtrip.original.docx");
        Assert.True(File.Exists(workingCandidate), "The checked-in real DOCX corpus is missing: tests/DocRedock.Tests/Fixtures/Docx/real-office-roundtrip.original.docx");
        return workingCandidate;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static async Task<string> CreateEdgeDocxAsync(string kind)
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, kind + ".docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");

        var relationships = kind == "hyperlink"
            ? "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdLink\" Target=\"https://example.invalid/docredock\" TargetMode=\"External\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" /></Relationships>"
            : "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />";
        await Write(zip, "word/_rels/document.xml.rels", relationships);

        var paragraph = kind switch
        {
            "hyperlink" => "<w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>DocRedock docs</w:t></w:r></w:hyperlink></w:p>",
            "field" => "<w:p><w:r><w:fldChar w:fldCharType=\"begin\" /></w:r><w:r><w:instrText> PAGE </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\" /></w:r><w:r><w:t>1</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\" /></w:r></w:p>",
            "revision" => "<w:p><w:ins w:id=\"1\" w:author=\"QA\"><w:r><w:t>inserted</w:t></w:r></w:ins><w:del w:id=\"2\" w:author=\"QA\"><w:r><w:delText>deleted</w:delText></w:r></w:del></w:p>",
            _ => "<w:p><w:r><w:t>protected text</w:t></w:r></w:p>"
        };
        await Write(zip, "word/document.xml", $"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body>{paragraph}<w:sectPr /></w:body></w:document>");
        if (kind == "protection")
            await Write(zip, "word/settings.xml", "<w:settings xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:documentProtection w:edit=\"readOnly\" w:enforcement=\"1\" /></w:settings>");
        if (kind == "footnote")
            await Write(zip, "word/footnotes.xml", "<w:footnotes xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:footnote w:id=\"2\"><w:p><w:r><w:t>Footnote evidence</w:t></w:r></w:p></w:footnote></w:footnotes>");
        return path;
    }

    private static async Task Write(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name);
        await using var stream = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        await stream.WriteAsync(text);
    }

    [Fact]
    public async Task Real_office_corpus_exports_readable_md_and_roundtrip_drmd_with_semantic_blocks()
    {
        var source = FindRealCorpus();
        var root = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var readablePath = Path.Combine(root, "atlas.md");
        var sidecarPath = Path.Combine(root, "atlas.drmd");
        var roundtripMarkdownPath = Path.Combine(root, "atlas-roundtrip.md");
        var service = new DocumentService();

        var readable = await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, readablePath));
        var readableMarkdown = await File.ReadAllTextAsync(readable.MarkdownPath);
        Assert.DoesNotContain("drmd_schema", readableMarkdown, StringComparison.Ordinal);
        Assert.Contains("# ", readableMarkdown, StringComparison.Ordinal);
        Assert.Contains("## ", readableMarkdown, StringComparison.Ordinal);
        Assert.Contains("- ", readableMarkdown, StringComparison.Ordinal);
        Assert.Contains("|", readableMarkdown, StringComparison.Ordinal);
        Assert.Contains("![", readableMarkdown, StringComparison.Ordinal);
        Assert.Contains("v2.4.0", readableMarkdown, StringComparison.Ordinal);

        var exported = await service.ExportAsync(new DocumentExportOptions(source, sidecarPath, roundtripMarkdownPath));
        var roundtripMarkdown = await File.ReadAllTextAsync(exported.MarkdownPath);
        Assert.Contains("drmd_schema", roundtripMarkdown, StringComparison.Ordinal);
        Assert.Contains("roundtrip_store: atlas.drmd", roundtripMarkdown, StringComparison.Ordinal);
        Assert.Contains("# ", roundtripMarkdown, StringComparison.Ordinal);
        Assert.Contains("|", roundtripMarkdown, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(sidecarPath, "graph", "index.json")));
        Assert.True(File.Exists(Path.Combine(sidecarPath, "maps", "projection-map.jsonl")));
        Assert.True(File.Exists(Path.Combine(sidecarPath, "source", "original.docx")));
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(sidecarPath, "assets")));

        var restoredPath = Path.Combine(root, "atlas-restored.docx");
        var restored = await service.RestoreAsync(new DocumentRestoreOptions(
            sidecarPath, restoredPath, roundtripMarkdownPath));
        Assert.True(restored.Succeeded);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(restoredPath))));
    }

}
