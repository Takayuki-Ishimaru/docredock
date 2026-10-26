using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Markdown;

namespace DocRedock.Tests.Api;

public sealed class TokenEstimateMultiplierTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-token-margin-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Theory]
    [InlineData("", 0)]
    [InlineData("abcde", 2)]
    [InlineData("日本語", 3)]
    [InlineData("abc日本😀", 4)]
    public void Default_and_fractional_estimates_keep_rune_counts(string text, int expected)
    {
        Assert.Equal(expected, AiTokenEstimate.Estimate(text));
        Assert.Equal(expected, AiTokenEstimate.Estimate(text, 1));
        Assert.Equal((int)Math.Ceiling(expected * 1.25), AiTokenEstimate.Estimate(text, 1.25));
        Assert.Equal(expected * 100, AiTokenEstimate.Estimate(text, 100));
    }

    public static TheoryData<double> InvalidMargins => new()
    {
        double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, -1, .5, 100.01
    };

    [Theory]
    [MemberData(nameof(InvalidMargins))]
    public async Task Invalid_margins_are_rejected_by_all_entry_points_before_output(double margin)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AiTokenEstimate.Estimate("text", margin));
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "empty", DocumentFormatKind.Docx, []);
        Assert.Throws<ArgumentOutOfRangeException>(() => AiPackageContentBuilder.Build(graph, new(), "source.docx",
            new AiPackagePartOptions { TokenEstimateMultiplier = margin }));
        var target = Path.Combine(root, "new-parent", "output");
        var options = new ReadableDocumentExportOptions(Path.Combine(root, "missing.docx"), "unused.md");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ExportAiPackageAsync(
            new AiPackageExportOptions(options, target) { TokenEstimateMultiplier = margin }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.PreviewAiPackageAsync(options,
            new AiPackagePartOptions { TokenEstimateMultiplier = margin }));
        Assert.False(Directory.Exists(Path.GetDirectoryName(target)));
    }

    [Fact]
    public void Large_estimates_saturate_and_original_record_deconstruction_still_compiles()
    {
        Assert.Equal(int.MaxValue, AiTokenEstimate.Estimate(new string('日', int.MaxValue / 100 + 1), 100));
        Assert.Throws<ArgumentNullException>(() => AiTokenEstimate.Estimate(null!));
        var document = new ReadableDocumentExportOptions("source.docx", "unused.md");
        var (originalDocument, path, form, target, blocks) = new AiPackageExportOptions(document, "output");
        Assert.Same(document, originalDocument);
        Assert.Equal(("output", AiPackageForm.Directory, 12000, false), (path, form, target, blocks));
        var (characters, rowBlocks) = new AiPackagePartOptions();
        Assert.Equal((12000, false), (characters, rowBlocks));
    }

    [Theory]
    [InlineData(AiPackageForm.Directory)]
    [InlineData(AiPackageForm.Zip)]
    public async Task Every_part_and_manifest_apply_margin_without_changing_content_or_sources(AiPackageForm form)
    {
        var source = WriteSource(root);
        var document = new ReadableDocumentExportOptions(source, "unused.md");
        var baseline = await service.ExportAiPackageAsync(new(document, Path.Combine(root, "baseline"), TargetCharacters: 512));
        var changed = await service.ExportAiPackageAsync(new AiPackageExportOptions(document,
            Path.Combine(root, form == AiPackageForm.Zip ? "margin.zip" : "margin"), form, TargetCharacters: 512)
            { TokenEstimateMultiplier = 1.5 });
        Assert.True(baseline.Parts.Count > 1);
        Assert.Equal(baseline.Parts.Count, changed.Parts.Count);
        for (var i = 0; i < baseline.Parts.Count; i++)
        {
            var before = baseline.Parts[i];
            var after = changed.Parts[i];
            Assert.Equal(before.Id, after.Id);
            Assert.Equal(before.Path, after.Path);
            Assert.Equal(before.Markdown, after.Markdown);
            Assert.Equal(before.NodeIds, after.NodeIds);
            Assert.Equal(JsonSerializer.Serialize(before.Sources), JsonSerializer.Serialize(after.Sources));
            Assert.Equal(before.ExceedsTarget, after.ExceedsTarget);
            Assert.Equal(1.5, after.TokenEstimateMultiplier);
            Assert.Equal((int)Math.Ceiling(before.EstimatedTokens * 1.5), after.EstimatedTokens);
        }
        var preview = await service.PreviewAiPackageAsync(document,
            new AiPackagePartOptions(512) { TokenEstimateMultiplier = 1.5 });
        Assert.Equal(baseline.Size, preview);
        var built = AiPackageContentBuilder.Build(baseline.Graph, new ReadableMarkdownOptions(), "source.docx",
            new AiPackagePartOptions(512) { TokenEstimateMultiplier = 1.5 });
        Assert.All(built, part => Assert.Equal(AiTokenEstimate.Estimate(part.Markdown, 1.5), part.EstimatedTokens));
        var files = AiPackageExportTests.ReadPackage(changed.OutputPath, form);
        var plain = AiPackageExportTests.ReadPackage(baseline.OutputPath, AiPackageForm.Directory);
        Assert.Equal(plain.Keys.Order(), files.Keys.Order());
        foreach (var file in files.Keys.Where(file => file != "manifest.json"))
            Assert.Equal(plain[file], files[file]);
        using var json = JsonDocument.Parse(files["manifest.json"]);
        Assert.Equal(1.5, json.RootElement.GetProperty("token_estimate_multiplier").GetDouble());
        Assert.Equal(changed.Parts.Select(part => part.EstimatedTokens),
            json.RootElement.GetProperty("parts").EnumerateArray().Select(part => part.GetProperty("estimated_tokens").GetInt32()));
        var manifest = await AiPackageManifestReader.ReadAsync(changed.OutputPath);
        Assert.Equal(changed.Parts.Select(part => (int?)part.EstimatedTokens), manifest.Parts.Select(part => part.EstimatedTokens));
        Assert.Empty(await AiPackageManifestReader.VerifyFilesAsync(changed.OutputPath, manifest));
        Assert.NotNull(await AiPackageManifestReader.ReadVerifiedPackageAsync(changed.OutputPath));
    }

    [Fact]
    public async Task Cancellation_preserves_existing_destination()
    {
        var source = WriteSource(root);
        var target = Path.Combine(root, "existing");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportAiPackageAsync(
            new AiPackageExportOptions(new(source, "unused.md"), target) { TokenEstimateMultiplier = 2 }, cancellation.Token));
        Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
    }

    internal static string WriteSource(string directory)
    {
        var source = Path.Combine(directory, "source.docx");
        using var archive = ZipFile.Open(source, ZipArchiveMode.Create);
        Write("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Write("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        var paragraphs = string.Concat(Enumerable.Range(0, 6).Select(i =>
            $"<w:p><w:r><w:t>Paragraph {i}: {string.Concat(Enumerable.Repeat("日本語 ASCII 0123456789 ", 40))}</w:t></w:r></w:p>"));
        Write("word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + paragraphs + "</w:body></w:document>");
        return source;
        void Write(string path, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }
}
