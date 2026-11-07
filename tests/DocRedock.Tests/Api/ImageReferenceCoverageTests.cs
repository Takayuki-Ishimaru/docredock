using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Markdown;

namespace DocRedock.Tests.Api;

/// <summary>Regressions for the 2026-11-07 evaluation of v0.3.10: a slide picture whose name and description are both
/// empty lost its Markdown reference while its file was still written, with no warning, no review item and "all visual
/// elements converted". Every image the content policy keeps is now written or left out by a named rule, and any other
/// gap is reported.</summary>
public sealed class ImageReferenceCoverageTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-image-coverage-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [Theory]
    [InlineData("", null, "図")]
    [InlineData("  ", "", "図")]
    [InlineData("", "Login screen", "Login screen")]
    [InlineData("Picture 3", null, "Picture 3")]
    public async Task A_slide_picture_is_written_whatever_its_name_and_description(string name, string? description, string alt)
    {
        var source = Path.Combine(root, "slides.pptx");
        WritePresentation(source, name, description);
        var markdownPath = Path.Combine(root, "slides.md");

        var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, markdownPath));

        var markdown = await File.ReadAllTextAsync(markdownPath);
        var asset = Assert.Single(Directory.GetFiles(Path.Combine(root, "slides.assets")));
        Assert.Contains($"![{alt}](slides.assets/{Path.GetFileName(asset)})", markdown, StringComparison.Ordinal);
        Assert.Contains("Body text", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "ImageNotWritten");
        var summary = ExportSummaryBuilder.BuildReadable(result.Graph, result.Diagnostics, result.Projection!);
        Assert.True(summary.AllVisualElementsConverted);
        Assert.Equal(0, summary.ImagesNotWritten);
    }

    [Fact]
    public async Task A_package_part_references_a_slide_picture_without_a_name()
    {
        var source = Path.Combine(root, "slides.pptx");
        WritePresentation(source, "", null);
        var target = Path.Combine(root, "package");

        var result = await new DocumentService().ExportAiPackageAsync(new AiPackageExportOptions(
            new ReadableDocumentExportOptions(source, "unused.md"), target, AiPackageForm.Directory));

        var parts = string.Join("\n", Directory.GetFiles(Path.Combine(target, "parts")).Select(File.ReadAllText));
        Assert.Matches(@"!\[図\]\(\.\./assets/[^)]+\.png\)", parts);
        Assert.Contains("![図](", await File.ReadAllTextAsync(Path.Combine(target, "document.md")), StringComparison.Ordinal);
        Assert.True(result.Summary.AllVisualElementsConverted);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(target, "report.json")));
        Assert.False(report.RootElement.GetProperty("review").GetProperty("required").GetBoolean());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void An_image_without_a_description_is_written_with_the_default_label(string? description)
    {
        var image = new DocumentNode("image", NodeKind.Image, null, 1, ContentLayer.Body,
            new ReferenceNodeContent("media/figure.png", description));
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "images", DocumentFormatKind.Docx,
            [new DocumentPartition("document", 0, [Paragraph("before", 0), image, Paragraph("after", 2)])]);
        var serializer = new ReadableMarkdownSerializer();

        var markdown = serializer.Serialize(graph);

        Assert.Contains("![図](media/figure.png)", markdown, StringComparison.Ordinal);
        Assert.Empty(serializer.Report.UnwrittenImageIds);
    }

    [Fact]
    public void An_image_left_out_without_a_rule_is_reported_and_needs_review()
    {
        // No rule of the serializer drops an image any more; a node it skips by another path (here a table-of-contents
        // leader mark, which only applies to text) stands for any future gap of that kind.
        var image = new DocumentNode("image", NodeKind.Image, null, 1, ContentLayer.Body,
            new ReferenceNodeContent("media/figure.png", "Figure"),
            Extensions: new Dictionary<string, JsonElement> { ["decorative_toc_leader"] = JsonSerializer.SerializeToElement(true) });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "images", DocumentFormatKind.Docx,
            [new DocumentPartition("document", 0, [Paragraph("before", 0), image])]);
        var serializer = new ReadableMarkdownSerializer();

        var markdown = serializer.Serialize(graph);

        Assert.DoesNotContain("media/figure.png", markdown, StringComparison.Ordinal);
        Assert.Equal(["image"], serializer.Report.UnwrittenImageIds);
        var diagnostic = Assert.Single(serializer.Diagnostics, item => item.Code == "ImageNotWritten");
        Assert.Equal(MarkdownDiagnosticSeverity.Warning, diagnostic.Severity);
        var summary = ExportSummaryBuilder.BuildReadable(graph, [], serializer.Report);
        Assert.False(summary.AllVisualElementsConverted);
        Assert.True(summary.ReviewItemsDetected);
        Assert.Equal(1, summary.ImagesNotWritten);
        var review = ExportReviewBuilder.Build(graph, [], serializer.Report);
        Assert.True(review.Required);
        Assert.Contains("本文に出力されなかった画像 1件", ExportReviewText.CountsJapanese(review), StringComparison.Ordinal);
    }

    [Fact]
    public void A_slide_logo_left_out_as_furniture_is_no_gap()
    {
        // The same picture on every slide is presentation furniture: left out on purpose, so nothing is reported.
        DocumentPartition Slide(int number) => new($"slide-{number}", number - 1,
        [
            new DocumentNode($"t{number}", NodeKind.Shape, null, 0, ContentLayer.Body, new TextNodeContent($"Slide text {number}"),
                Extensions: new Dictionary<string, JsonElement> { ["shape_role"] = JsonSerializer.SerializeToElement("body") }),
            new DocumentNode($"logo{number}", NodeKind.Image, null, 1, ContentLayer.Furniture, new ReferenceNodeContent("media/logo.png", "Logo"),
                Extensions: new Dictionary<string, JsonElement> { ["shape_role"] = JsonSerializer.SerializeToElement("footer") }),
        ]);
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "deck", DocumentFormatKind.Pptx, [Slide(1), Slide(2), Slide(3)]);
        var serializer = new ReadableMarkdownSerializer();

        var markdown = serializer.Serialize(graph);

        if (!markdown.Contains("media/logo.png", StringComparison.Ordinal))
            Assert.Empty(serializer.Report.UnwrittenImageIds);
        Assert.DoesNotContain(serializer.Diagnostics, item => item.Code == "ImageNotWritten");
    }

    private static readonly byte[] RedPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGM4IScHAAK2AQU0pnWqAAAAAElFTkSuQmCC");

    [Theory]
    [InlineData("", "")]
    [InlineData("Picture 2", "Picture 3")]
    public async Task Different_pictures_low_on_their_slides_are_no_repeated_footer(string firstName, string secondName)
    {
        // Two pictures near the foot of their slides, each a different image: their file names (img-0001.png,
        // img-0002.png) differ only in a number, as a footer's page number does, but they are not the same picture.
        var source = Path.Combine(root, "low.pptx");
        WriteDeck(source, [(firstName, Png, 3000000), (secondName, RedPng, 3000000)]);
        var markdownPath = Path.Combine(root, "low.md");

        var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, markdownPath));

        var markdown = await File.ReadAllTextAsync(markdownPath);
        foreach (var asset in Directory.GetFiles(Path.Combine(root, "low.assets")))
            Assert.Contains("low.assets/" + Path.GetFileName(asset), markdown, StringComparison.Ordinal);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "low.assets")).Length);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "ImageNotWritten");
    }

    [Fact]
    public async Task The_same_logo_at_the_foot_of_every_slide_stays_furniture()
    {
        var source = Path.Combine(root, "logo.pptx");
        WriteDeck(source, [("Logo", Png, 3000000), ("Logo", Png, 3000000)]);
        var markdownPath = Path.Combine(root, "logo.md");

        var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, markdownPath));

        Assert.DoesNotContain(".png", await File.ReadAllTextAsync(markdownPath), StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "ImageNotWritten");
    }

    private static DocumentNode Paragraph(string text, int order) =>
        new(text, NodeKind.Paragraph, null, order, ContentLayer.Body, new TextNodeContent(text));

    // One slide per picture: a title, a line of text, and the picture at the given offset from the top (EMU), 2,000,000 high.
    private static void WriteDeck(string path, IReadOnlyList<(string Name, byte[] Png, long Y)> pictures)
    {
        var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["[Content_Types].xml"] = Utf8("""
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="png" ContentType="image/png"/>
                  <Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
                </Types>
                """),
            ["_rels/.rels"] = Utf8("""
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>
                </Relationships>
                """),
            ["ppt/presentation.xml"] = Utf8($$"""
                <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <p:sldIdLst>{{string.Concat(pictures.Select((_, index) => $"<p:sldId id=\"{256 + index}\" r:id=\"rId{index + 1}\"/>"))}}</p:sldIdLst>
                </p:presentation>
                """),
            ["ppt/_rels/presentation.xml.rels"] = Utf8($$"""
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  {{string.Concat(pictures.Select((_, index) => $"<Relationship Id=\"rId{index + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide{index + 1}.xml\"/>"))}}
                </Relationships>
                """),
        };
        for (var index = 0; index < pictures.Count; index++)
        {
            var (name, png, y) = pictures[index];
            var number = index + 1;
            parts[$"ppt/slides/slide{number}.xml"] = Utf8($$"""
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                       xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                       xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <p:cSld><p:spTree>
                  <p:sp><p:nvSpPr><p:cNvPr id="2" name="Title"/><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
                  <p:spPr><a:xfrm><a:off x="457200" y="274638"/><a:ext cx="8229600" cy="1143000"/></a:xfrm></p:spPr>
                  <p:txBody><a:bodyPr/><a:p><a:r><a:t>Slide {{number}}</a:t></a:r></a:p></p:txBody></p:sp>
                  <p:sp><p:nvSpPr><p:cNvPr id="3" name="Body"/><p:nvPr><p:ph type="body" idx="1"/></p:nvPr></p:nvSpPr>
                  <p:spPr><a:xfrm><a:off x="457200" y="1600200"/><a:ext cx="4000000" cy="1000000"/></a:xfrm></p:spPr>
                  <p:txBody><a:bodyPr/><a:p><a:r><a:t>Text of slide {{number}}</a:t></a:r></a:p></p:txBody></p:sp>
                  <p:pic><p:nvPicPr><p:cNvPr id="4" name="{{name}}"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr>
                  <p:blipFill><a:blip r:embed="rIdImage"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>
                  <p:spPr><a:xfrm><a:off x="4800000" y="{{y}}"/><a:ext cx="3000000" cy="2000000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr></p:pic>
                  </p:spTree></p:cSld>
                </p:sld>
                """);
            parts[$"ppt/slides/_rels/slide{number}.xml.rels"] = Utf8($$"""
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rIdImage" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="../media/image{{number}}.png"/>
                </Relationships>
                """);
            parts[$"ppt/media/image{number}.png"] = png;
        }
        using var output = File.Create(path);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var part in parts)
        {
            using var stream = archive.CreateEntry(part.Key).Open();
            stream.Write(part.Value);
        }
    }

    private static void WritePresentation(string path, string name, string? description)
    {
        var descr = description is null ? string.Empty : $" descr=\"{description}\"";
        var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["[Content_Types].xml"] = Utf8("""
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="png" ContentType="image/png"/>
                  <Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
                </Types>
                """),
            ["_rels/.rels"] = Utf8("""
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>
                </Relationships>
                """),
            ["ppt/presentation.xml"] = Utf8("""
                <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <p:sldIdLst><p:sldId id="256" r:id="rId1"/></p:sldIdLst>
                </p:presentation>
                """),
            ["ppt/_rels/presentation.xml.rels"] = Utf8("""
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/>
                </Relationships>
                """),
            ["ppt/slides/slide1.xml"] = Utf8($$"""
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                       xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                       xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <p:cSld><p:spTree>
                  <p:sp><p:nvSpPr><p:cNvPr id="2" name="Title"/><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
                  <p:spPr><a:xfrm><a:off x="457200" y="274638"/><a:ext cx="8229600" cy="1143000"/></a:xfrm></p:spPr>
                  <p:txBody><a:bodyPr/><a:p><a:r><a:t>Approval flow</a:t></a:r></a:p></p:txBody></p:sp>
                  <p:sp><p:nvSpPr><p:cNvPr id="3" name="Body"/><p:nvPr><p:ph type="body" idx="1"/></p:nvPr></p:nvSpPr>
                  <p:spPr><a:xfrm><a:off x="457200" y="1600200"/><a:ext cx="4000000" cy="2000000"/></a:xfrm></p:spPr>
                  <p:txBody><a:bodyPr/><a:p><a:r><a:t>Body text</a:t></a:r></a:p></p:txBody></p:sp>
                  <p:pic><p:nvPicPr><p:cNvPr id="4" name="{{name}}"{{descr}}/><p:cNvPicPr/><p:nvPr/></p:nvPicPr>
                  <p:blipFill><a:blip r:embed="rIdImage"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>
                  <p:spPr><a:xfrm><a:off x="4800000" y="1600200"/><a:ext cx="3000000" cy="2000000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr></p:pic>
                  </p:spTree></p:cSld>
                </p:sld>
                """),
            ["ppt/slides/_rels/slide1.xml.rels"] = Utf8("""
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rIdImage" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="../media/image1.png"/>
                </Relationships>
                """),
            ["ppt/media/image1.png"] = Png,
        };
        using var output = File.Create(path);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var part in parts)
        {
            using var stream = archive.CreateEntry(part.Key, CompressionLevel.NoCompression).Open();
            stream.Write(part.Value);
        }
    }

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
}
