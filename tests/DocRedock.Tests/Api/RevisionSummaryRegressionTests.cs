using System.IO.Compression;
using System.Text;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;

namespace DocRedock.Tests.Api;

/// <summary>Finding B: an export that includes tracked revision markup needs a sharing decision
/// (the CLI returns ReviewRequired for it), so the human review summary must not say "none".</summary>
public sealed class RevisionSummaryRegressionTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static string WriteDocx(string body)
    {
        var dir = Directory.CreateTempSubdirectory("docredock-revision-summary-").FullName;
        var source = Path.Combine(dir, "source.docx");
        using var archive = ZipFile.Open(source, ZipArchiveMode.Create);
        Write(archive, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Write(archive, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Write(archive, "word/document.xml", $"<w:document xmlns:w=\"{W}\"><w:body>{body}</w:body></w:document>");
        return source;
    }

    private static void Write(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private const string Revised = "<w:p><w:r><w:t>Before </w:t></w:r><w:ins><w:r><w:t>new</w:t></w:r></w:ins></w:p>";
    private const string Plain = "<w:p><w:r><w:t>Plain text</w:t></w:r></w:p>";

    [Theory]
    [InlineData(Revised, true)]
    [InlineData(Plain, false)]
    public async Task Visible_markup_export_summarizes_revision_sharing_as_a_review_item(string body, bool expected)
    {
        var source = WriteDocx(body);
        try
        {
            var result = await new DocumentService(null, null, discoverPdfRasterizer: false)
                .ExportReadableAsync(new ReadableDocumentExportOptions(source, Path.ChangeExtension(source, ".md"), EnableOcr: false));
            var summary = ExportSummaryBuilder.BuildReadable(result.Graph, result.Diagnostics, result.Projection);
            var review = ExportReviewBuilder.Build(result.Graph, result.Diagnostics, result.Projection);

            Assert.Equal(expected, summary.RevisionSharingRequired);
            Assert.Equal(review.SharingReview!.Required, summary.RevisionSharingRequired);
            Assert.False(summary.ReviewItemsDetected);
            Assert.Equal(expected, review.Required);
            var text = summary.ToString();
            Assert.Contains(expected ? "revision sharing required" : "Detected review items: none", text, StringComparison.Ordinal);
            Assert.Equal(expected, ExportSummaryText.EvaluationJapanese(summary).Contains("変更履歴の共有", StringComparison.Ordinal));
        }
        finally { Directory.Delete(Path.GetDirectoryName(source)!, true); }
    }

    [Fact]
    public async Task Cli_default_policy_excludes_hidden_text_without_a_warning_exit()
    {
        const string body = "<w:p><w:r><w:t>Visible</w:t></w:r><w:r><w:rPr><w:vanish/></w:rPr><w:t>Secret</w:t></w:r></w:p>";
        var source = WriteDocx(body);
        try
        {
            var app = new DocRedock.Cli.CliApplication(new StringWriter(), new StringWriter(), new DocumentService(null, null, discoverPdfRasterizer: false));
            var target = Path.ChangeExtension(source, ".md");
            Assert.Equal(0, await app.RunAsync(["export", source, "--output", target, "--ocr", "off"]));
            var markdown = await File.ReadAllTextAsync(target);
            Assert.Contains("Visible", markdown);
            Assert.DoesNotContain("Secret", markdown);
        }
        finally { Directory.Delete(Path.GetDirectoryName(source)!, true); }
    }

    [Fact]
    public async Task Cli_successfully_excludes_hidden_text_and_final_revision_history()
    {
        const string body = "<w:p><w:r><w:t>Visible</w:t></w:r><w:r><w:rPr><w:vanish/></w:rPr><w:t>Secret</w:t></w:r>" +
            "<w:del><w:r><w:delText>Old</w:delText></w:r></w:del></w:p>";
        var source = WriteDocx(body);
        try
        {
            var output = new StringWriter();
            var app = new DocRedock.Cli.CliApplication(output, new StringWriter(), new DocumentService(null, null, discoverPdfRasterizer: false));
            var target = Path.ChangeExtension(source, ".md");
            Assert.Equal(0, await app.RunAsync(["export", source, "--output", target, "--ocr", "off", "--revisions", "final", "--verbose"]));
            var markdown = await File.ReadAllTextAsync(target);
            Assert.Contains("Visible", markdown);
            Assert.DoesNotContain("Secret", markdown);
            Assert.DoesNotContain("Old", markdown);
            Assert.Contains("TrackedRevisionsPresent", output.ToString());
        }
        finally { Directory.Delete(Path.GetDirectoryName(source)!, true); }
    }

    [Theory]
    [InlineData("visible", "final")]
    [InlineData("sanitized", null)]
    [InlineData("sanitized", "final")]
    public async Task Final_view_and_sanitized_output_do_not_ask_for_sharing_review(string policy, string? revisions)
    {
        var source = WriteDocx(Revised);
        try
        {
            var result = await new DocumentService(null, null, discoverPdfRasterizer: false)
                .ExportReadableAsync(new ReadableDocumentExportOptions(source, Path.ChangeExtension(source, ".md"),
                    EnableOcr: false, Revisions: revisions, ContentPolicy: policy));
            var summary = ExportSummaryBuilder.BuildReadable(result.Graph, result.Diagnostics, result.Projection);
            Assert.False(summary.RevisionSharingRequired);
            Assert.DoesNotContain("revision sharing required", summary.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("変更履歴の共有", ExportSummaryText.EvaluationJapanese(summary), StringComparison.Ordinal);
        }
        finally { Directory.Delete(Path.GetDirectoryName(source)!, true); }
    }

    [Fact]
    public void Revision_sharing_stays_visible_alongside_fidelity_and_hidden_reasons()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "sharing", DocumentFormatKind.Docx, []);
        var diagnostics = new[]
        {
            new Diagnostic("TrackedRevisionsPresent", "Tracked revisions are present.", DiagnosticSeverity.Warning),
            new Diagnostic("RevisionPolicy", "markup", DiagnosticSeverity.Information),
            new Diagnostic("HiddenContentIncluded", "Hidden content is included.", DiagnosticSeverity.Warning),
        };
        var summary = ExportSummaryBuilder.Build(graph, diagnostics) with { VisualReviewPages = 1, ReviewPages = 1, UnresolvedElements = 1 };

        Assert.True(summary.RevisionSharingRequired);
        Assert.True(summary.HiddenContentIncluded);
        var text = summary.ToString();
        Assert.Contains("Detected review items: visual 1 page(s), revision sharing required; hidden content included - review before sharing", text, StringComparison.Ordinal);
        Assert.Contains("変更履歴の共有", ExportSummaryText.EvaluationJapanese(summary), StringComparison.Ordinal);
    }
}
