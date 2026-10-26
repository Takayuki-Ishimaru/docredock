using System.IO.Compression;
using System.Text;
using DocRedock.Cli;

namespace DocRedock.Tests.Cli;

/// <summary>Finding B: --fail-on-review returns ReviewRequired (11) for included tracked revision
/// markup, so the summary the same run prints must report that review item too.</summary>
public sealed class RevisionSummaryRegressionTests : IDisposable
{
    public void Dispose() => Directory.Delete(root, recursive: true);
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Revised = "<w:p><w:r><w:t>Before </w:t></w:r><w:del><w:r><w:delText>old</w:delText></w:r></w:del>" +
        "<w:ins><w:r><w:t>new</w:t></w:r></w:ins></w:p>";

    private readonly string root = Directory.CreateTempSubdirectory("docredock-cli-revision-").FullName;
    private string SourcePath => Path.Combine(root, "source.docx");
    private string MarkdownPath => Path.Combine(root, "projection.md");

    private void CreateDocx(string body)
    {
        using var file = File.Create(SourcePath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        Write(archive, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Write(archive, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Write(archive, "word/document.xml", $"<w:document xmlns:w=\"{W}\"><w:body>{body}</w:body></w:document>");
    }

    private static void Write(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private async Task<(int Exit, string Output)> RunAsync(params string[] options)
    {
        var stdout = new StringWriter();
        var app = new CliApplication(stdout, new StringWriter(), new DocRedock.Api.DocumentService(null, null, discoverPdfRasterizer: false));
        var args = new List<string> { "export", SourcePath, "--output", MarkdownPath, "--ocr", "off" };
        args.AddRange(options);
        return (await app.RunAsync(args.ToArray()), stdout.ToString());
    }

    [Fact]
    public async Task Visible_markup_fail_on_review_exit_and_summary_agree()
    {
        CreateDocx(Revised);
        var (exit, output) = await RunAsync("--revisions", "markup", "--fail-on-review");
        Assert.Equal(11, exit);
        Assert.Contains("revision sharing required", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Detected review items: none", output, StringComparison.Ordinal);
        Assert.Contains("~~old~~", await File.ReadAllTextAsync(MarkdownPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--revisions", "final")]
    [InlineData("--content-policy", "sanitized")]
    public async Task Readable_final_or_sanitized_output_asks_for_no_sharing_review(params string[] options)
    {
        CreateDocx(Revised);
        var (exit, output) = await RunAsync([.. options, "--fail-on-review"]);
        Assert.NotEqual(11, exit);
        Assert.DoesNotContain("revision sharing required", output, StringComparison.Ordinal);
        Assert.DoesNotContain("変更履歴", output, StringComparison.Ordinal);
        Assert.Contains("Detected review items: none", output, StringComparison.Ordinal);
        Assert.True(File.Exists(MarkdownPath));
        Assert.DoesNotContain("~~old~~", await File.ReadAllTextAsync(MarkdownPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Document_without_revisions_never_reports_sharing_review()
    {
        CreateDocx("<w:p><w:r><w:t>Plain text</w:t></w:r></w:p>");
        var (exit, output) = await RunAsync("--revisions", "markup", "--fail-on-review");
        Assert.NotEqual(11, exit);
        Assert.Contains("Detected review items: none", output, StringComparison.Ordinal);
    }
}
