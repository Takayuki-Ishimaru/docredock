using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Render;
using DocRedock.Providers.Abstractions.Providers;

namespace DocRedock.Tests.Api;

public sealed class AiPackageExportTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-ai-tests-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);
    public void Dispose() => Directory.Delete(root, true);

    internal static string WriteSource(string directory, string format)
    {
        var source = Path.Combine(directory, "source." + format);
        ContentPolicyIntegrationTests.WritePackage(source, format switch
        {
            "docx" => ContentPolicyIntegrationTests.DocxParts(),
            "xlsx" => ContentPolicyIntegrationTests.XlsxParts(),
            "pptx" => ContentPolicyIntegrationTests.PptxParts(),
            _ => throw new ArgumentException(format)
        });
        return source;
    }

    public static IEnumerable<object[]> Policies() =>
        from format in new[] { "docx", "xlsx", "pptx" }
        from policy in new[] { "visible", "sanitized", "complete" }
        from form in new[] { AiPackageForm.Directory, AiPackageForm.Zip }
        select new object[] { format, policy, form };

    [Theory]
    [MemberData(nameof(Policies))]
    public async Task Every_package_file_obeys_policy_and_has_a_verifiable_manifest(string format, string policy, AiPackageForm form)
    {
        var source = WriteSource(root, format);
        var before = await File.ReadAllBytesAsync(source);
        var target = Path.Combine(root, "out" + (form == AiPackageForm.Zip ? ".zip" : ""));
        var result = await service.ExportAiPackageAsync(new AiPackageExportOptions(
            new ReadableDocumentExportOptions(source, "unused.md", ContentPolicy: policy), target, form));
        var files = ReadPackage(target, form);
        Assert.Contains("document.md", files.Keys);
        Assert.Contains("review.md", files.Keys);
        Assert.Contains("report.json", files.Keys);
        Assert.NotEmpty(result.Parts);
        Assert.Contains(files.Keys, p => p.StartsWith("parts/", StringComparison.Ordinal));
        Assert.DoesNotContain(files.Keys, p => p.EndsWith("." + format, StringComparison.Ordinal));
        var allText = string.Join("\n", files.Values.Select(Encoding.UTF8.GetString)).Replace("\\", "", StringComparison.Ordinal);
        Assert.Equal(policy == "complete", allText.Contains("DOCREDOCK_SECRET", StringComparison.Ordinal));
        Assert.DoesNotContain(root, allText, StringComparison.Ordinal);
        Assert.Equal(policy == "complete", result.Summary.HiddenContentIncluded);
        using var manifest = JsonDocument.Parse(files["manifest.json"]);
        Assert.Equal("2.0", manifest.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal(policy, manifest.RootElement.GetProperty("content_policy").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(before)),
            manifest.RootElement.GetProperty("source").GetProperty("sha256").GetString());
        Assert.Equal(result.Parts.Count, manifest.RootElement.GetProperty("parts").GetArrayLength());
        var indexed = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(files.Count - 1, indexed.Length);
        foreach (var item in indexed)
        {
            var path = item.GetProperty("path").GetString()!;
            Assert.DoesNotContain("..", path, StringComparison.Ordinal);
            Assert.Equal(files[path].Length, item.GetProperty("bytes").GetInt64());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(files[path])), item.GetProperty("sha256").GetString());
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root, ".docredock-ai-*"));
    }

    [Fact]
    public async Task Images_and_literal_markdown_keep_working_in_document_and_parts()
    {
        var source = await MarkdownLiteralSyntaxEndToEndTests.CreateLiteralSyntaxDocxAsync();
        try
        {
            var target = Path.Combine(root, "images");
            var result = await service.ExportAiPackageAsync(new(new(source, "unused.md"), target));
            var files = ReadPackage(target, AiPackageForm.Directory);
            Assert.Contains(files.Keys, p => p.StartsWith("assets/", StringComparison.Ordinal));
            var document = Encoding.UTF8.GetString(files["document.md"]);
            Assert.Contains("](assets/", document, StringComparison.Ordinal);
            Assert.Contains(result.Parts, p => p.Markdown.Contains("](../assets/", StringComparison.Ordinal));
            Assert.Contains("\\[LABEL\\]", document, StringComparison.Ordinal);
            Assert.Contains(result.Parts, p => p.Markdown.Contains("\\[LABEL\\]", StringComparison.Ordinal));
            Assert.Contains("|", document, StringComparison.Ordinal);
            Assert.Contains(result.Parts, p => p.Markdown.Contains("|", StringComparison.Ordinal));
            Assert.All(files.Keys, p => Assert.DoesNotContain("document.assets", p, StringComparison.Ordinal));
            foreach (var part in result.Parts)
                Assert.Equal(part.Markdown, Encoding.UTF8.GetString(files[part.Path]));
        }
        finally { Directory.Delete(Path.GetDirectoryName(source)!, true); }
    }

    [Fact]
    public async Task Parallel_packages_and_ZIPs_are_byte_deterministic()
    {
        var source = Path.Combine(root, "stable.docx");
        await new MarkdownRenderer().RenderAsync("# Title\n\nBody\n\n| Key | Value |\n|---|---|\n| A | B |", RenderFormat.Docx, source);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(i => service.ExportAiPackageAsync(
            new(new(source, "unused.md"), Path.Combine(root, $"out{i}.zip"), AiPackageForm.Zip))));
        Assert.Equal(await File.ReadAllBytesAsync(results[0].OutputPath), await File.ReadAllBytesAsync(results[1].OutputPath));
        Assert.Equal(await File.ReadAllBytesAsync(results[0].OutputPath), await File.ReadAllBytesAsync(results[2].OutputPath));
    }

    [Fact]
    public async Task Cancellation_failure_existing_output_and_invalid_options_preserve_inputs()
    {
        var source = WriteSource(root, "docx");
        var target = Path.Combine(root, "out");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportAiPackageAsync(new(new(source, "unused.md"), target), cts.Token));
        Assert.False(Directory.Exists(target));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ExportAiPackageAsync(new(new(source, "unused.md"), target, TargetCharacters: 1)));
        await File.WriteAllTextAsync(target, "keep");
        await Assert.ThrowsAsync<IOException>(() => service.ExportAiPackageAsync(new(new(source, "unused.md"), target)));
        Assert.Equal("keep", await File.ReadAllTextAsync(target));
        var badSource = Path.Combine(root, "bad.docx");
        await File.WriteAllTextAsync(badSource, "broken");
        await Assert.ThrowsAnyAsync<Exception>(() => service.ExportAiPackageAsync(new(new(badSource, "unused.md"), Path.Combine(root, "failed"))));
        Assert.False(Directory.Exists(Path.Combine(root, "failed")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root, ".docredock-ai-*"));
    }

    [Fact]
    public async Task Unanalyzed_PDF_content_is_linked_to_the_actual_review_page_and_part()
    {
        var fixture = Pdf.PdfFormXObjectFixtureTests.FixturePath("form-xobject-unreadable.pdf");
        var result = await service.ExportAiPackageAsync(new(new(fixture, "unused.md"), Path.Combine(root, "pdf")));
        var review = await File.ReadAllTextAsync(Path.Combine(result.OutputPath, "review.md"));
        Assert.NotEmpty(result.Review.Pages);
        Assert.Contains("parts/", review, StringComparison.Ordinal);
        Assert.Contains(result.Parts.SelectMany(p => p.Sources), s => s.PageNumber == 1);
        Assert.True(result.Summary.UnanalyzedContent > 0);
    }

    [Theory]
    [InlineData(AiPackageForm.Directory)]
    [InlineData(AiPackageForm.Zip)]
    public async Task Review_image_destinations_resolve_inside_the_package(AiPackageForm form)
    {
        var fixture = Pdf.PdfFormXObjectFixtureTests.FixturePath("form-xobject-unreadable.pdf");
        var rasterizer = new ReviewRasterizer();
        var result = await new DocumentService(null, rasterizer, discoverPdfRasterizer: false)
            .ExportAiPackageAsync(new(new(fixture, "unused.md"), Path.Combine(root, "review"), form));
        Assert.True(rasterizer.Calls > 0);
        var files = ReadPackage(result.OutputPath, form);
        var page = Assert.Single(result.Review.Pages);
        Assert.StartsWith("assets/", page.ReviewImageReference);
        Assert.Contains(page.ReviewImageReference!, files.Keys);
        Assert.Contains(page.ReviewImageReference!, Encoding.UTF8.GetString(files["review.md"]));
        Assert.Contains(result.Parts, p => p.Markdown.Contains("../" + page.ReviewImageReference, StringComparison.Ordinal));
    }

    private sealed class ReviewRasterizer : IPdfRasterizer
    {
        public int Calls { get; private set; }
        public ProviderDescriptor Descriptor { get; } = new("test.ai.rasterizer", new Version(1, 0), 1,
            new HashSet<string> { "rasterize.pdf" }, "MIT", "test", true);
        public ValueTask<IReadOnlyList<RasterizedPdfPage>> RasterizeAsync(string path, IReadOnlyList<int> pages,
            PdfRasterizationOptions options, CancellationToken token = default)
        {
            Calls++;
            var png = PngRasterImage.Encode(40, 20, new byte[40 * 20 * 3]);
            return ValueTask.FromResult<IReadOnlyList<RasterizedPdfPage>>(pages.Select(p => new RasterizedPdfPage(p, "image/png", png, 40, 20)).ToArray());
        }
    }

    [Fact]
    public async Task Cancellation_during_conversion_removes_staging_and_preserves_the_source()
    {
        var source = await MarkdownLiteralSyntaxEndToEndTests.CreateLiteralSyntaxDocxAsync();
        try
        {
            var before = await File.ReadAllBytesAsync(source);
            using var cts = new CancellationTokenSource();
            var engine = new WaitingOcrEngine();
            var target = Path.Combine(root, "cancelled");
            var exporting = new DocumentService(engine, null, discoverPdfRasterizer: false).ExportAiPackageAsync(
                new(new(source, "unused.md", EnableOcr: true), target), cts.Token);
            await engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporting);
            Assert.False(Directory.Exists(target));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root, ".docredock-ai-*"));
            Assert.Equal(before, await File.ReadAllBytesAsync(source));
        }
        finally { Directory.Delete(Path.GetDirectoryName(source)!, true); }
    }

    private sealed class WaitingOcrEngine : IOcrEngine
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderDescriptor Descriptor { get; } = new("test.ai.ocr", new Version(1, 0), 1,
            new HashSet<string> { "ocr.text" }, "MIT", "test", true);
        public async ValueTask<OcrAttemptResult> RecognizeAsync(OcrInput input, OcrOptions options, CancellationToken token)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        }
    }

    internal static Dictionary<string, byte[]> ReadPackage(string path, AiPackageForm form)
    {
        if (form == AiPackageForm.Directory)
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .ToDictionary(p => Path.GetRelativePath(path, p).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var input = e.Open(); using var output = new MemoryStream();
            input.CopyTo(output); return output.ToArray();
        }, StringComparer.Ordinal);
    }
}
