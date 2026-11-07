using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Markdown;
using DocRedock.Render;
using Xunit;

namespace DocRedock.Tests.Api;

/// <summary>
/// A Word paragraph that holds only a picture, a page break or a floating box has no text of its
/// own. The content policy removes that empty parent, while the picture, break or box stays. The
/// AI package must still divide the document at its headings (the kept child is a root), without
/// bringing back anything the policy excludes, and must say so when a document really cannot be divided.
/// </summary>
public sealed class WordSplitStructuralParentTests : IDisposable
{
    private const string HiddenSecret = "HIDDENSECRETTEXT";
    private const string PictureAlt = "Timing diagram";
    private const string BoxText = "Floating box text";

    private readonly string root = Directory.CreateTempSubdirectory("docredock-word-split-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);
    private int exports;

    public void Dispose() => Directory.Delete(root, true);

    private static string Heading(int number) =>
        $"<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>Section {number}</w:t></w:r></w:p>";

    // About 290 characters, so that every section stays well under the 400 character target on its own
    // and any two of them together exceed it.
    private static string Paragraph(int number) =>
        "<w:p><w:r><w:t>" + string.Join(' ', Enumerable.Repeat($"Item {number} must preserve its identifier and measured timeout.", 5)) + "</w:t></w:r></w:p>";

    private const string PageBreakParagraph = "<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>";

    private static string DrawingRun(string alt) =>
        "<w:r><w:drawing><wp:inline><wp:extent cx=\"2743200\" cy=\"533400\"/>" +
        $"<wp:docPr id=\"1\" name=\"Picture 1\" descr=\"{alt}\"/>" +
        "<a:graphic><a:graphicData><a:blip r:embed=\"rIdImage\"/></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

    private static string ImageOnlyParagraph => "<w:p>" + DrawingRun(PictureAlt) + "</w:p>";

    private static string HiddenRun(string text) => $"<w:r><w:rPr><w:vanish/></w:rPr><w:t>{text}</w:t></w:r>";

    private static string BoxOnlyParagraph(string shapeId) =>
        "<w:p><w:r><w:pict><v:shape id=\"" + shapeId + "\" type=\"#_x0000_t202\"><v:textbox><w:txbxContent>" +
        $"<w:p><w:r><w:t>{BoxText}</w:t></w:r></w:p></w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>";

    /// <summary>A title and six headings with one body paragraph each; <paramref name="extras"/> maps a
    /// section number to the XML that follows that section.</summary>
    private string WriteDocx(string name, IReadOnlyDictionary<int, string>? extras = null)
    {
        var body = new StringBuilder("<w:p><w:pPr><w:pStyle w:val=\"Title\"/></w:pPr><w:r><w:t>Chunking test</w:t></w:r></w:p>");
        for (var number = 1; number <= 6; number++)
        {
            body.Append(Heading(number)).Append(Paragraph(number));
            if (extras is not null && extras.TryGetValue(number, out var extra)) body.Append(extra);
        }
        var parts = new Dictionary<string, byte[]>
        {
            ["[Content_Types].xml"] = Utf8("""
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="png" ContentType="image/png"/>
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                </Types>
                """),
            ["_rels/.rels"] = Utf8("""
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """),
            ["word/document.xml"] = Utf8("""
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                            xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
                            xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
                            xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                            xmlns:v="urn:schemas-microsoft-com:vml">
                  <w:body>
                """ + body + "</w:body></w:document>"),
            ["word/_rels/document.xml.rels"] = Utf8("""
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rIdImage" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/image1.png"/>
                </Relationships>
                """),
            ["word/media/image1.png"] = PngRasterImage.Encode(40, 20, new byte[40 * 20 * 3]),
        };
        var path = Path.Combine(root, name + ".docx");
        using var output = File.Create(path);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var part in parts.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            using var entry = archive.CreateEntry(part.Key, CompressionLevel.NoCompression).Open();
            entry.Write(part.Value);
        }
        return path;

        static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
    }

    private async Task<(AiPackageExportResult Result, string Directory)> ExportAsync(string source, string policy)
    {
        var target = Path.Combine(root, "package-" + (++exports).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var result = await service.ExportAiPackageAsync(new(new(source, "unused.md", ContentPolicy: policy), target, TargetCharacters: 400));
        return (result, target);
    }

    private static readonly IReadOnlyDictionary<string, string> Variants = new Dictionary<string, string>
    {
        ["control"] = string.Empty,
        ["page break only"] = PageBreakParagraph,
        ["image only"] = ImageOnlyParagraph,
        ["floating box only"] = BoxOnlyParagraph("box1"),
        ["image beside hidden text"] = "<w:p>" + HiddenRun(HiddenSecret) + DrawingRun(PictureAlt) + "</w:p>",
        ["hidden text only"] = "<w:p>" + HiddenRun(HiddenSecret) + "</w:p>",
    };

    public static IEnumerable<object[]> VariantsAndPolicies() =>
        from variant in new[] { "control", "page break only", "image only", "floating box only", "image beside hidden text", "hidden text only" }
        from policy in new[] { "visible", "sanitized" }
        select new object[] { variant, policy };

    // The first line of a part after the repeated document title and the source lines.
    private static string FirstBodyLine(AiPackagePart part) => part.Markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Split('\n').Skip(1)
        .First(line => line.Trim().Length > 0 && !line.StartsWith("ソース: ", StringComparison.Ordinal) &&
            !line.StartsWith("section_path: ", StringComparison.Ordinal));

    private static string[] BodyLines(string markdown) => markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
        .Where(line => line.Trim().Length > 0).ToArray();

    [Theory]
    [MemberData(nameof(VariantsAndPolicies))]
    public async Task Document_still_splits_at_every_heading(string variant, string policy)
    {
        var source = WriteDocx("split", new Dictionary<int, string> { [3] = Variants[variant] });
        var (result, directory) = await ExportAsync(source, policy);

        Assert.Equal(6, result.Parts.Count);
        for (var index = 0; index < 6; index++)
        {
            Assert.Equal($"## Section {index + 1}", FirstBodyLine(result.Parts[index]));
            // Each section's body text sits in its own part and nowhere else.
            for (var other = 0; other < 6; other++)
                Assert.Equal(other == index, result.Parts[other].Markdown.Contains($"Item {index + 1} must", StringComparison.Ordinal));
        }
        // Dividing the document was possible, so there is nothing to report about it.
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == AiPackageContentBuilder.SplitUnavailableCode);
        Assert.DoesNotContain("Not divided by section", await File.ReadAllTextAsync(Path.Combine(directory, "review.md")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("page break only", "visible")]
    [InlineData("page break only", "sanitized")]
    public async Task Page_break_stays_at_the_end_of_the_section_it_follows(string variant, string policy)
    {
        var source = WriteDocx("break", new Dictionary<int, string> { [3] = Variants[variant] });
        var (result, _) = await ExportAsync(source, policy);

        Assert.Equal(6, result.Parts.Count);
        for (var index = 0; index < 6; index++)
            Assert.Equal(index == 2, BodyLines(result.Parts[index].Markdown).Contains("---"));
        Assert.Equal("---", BodyLines(result.Parts[2].Markdown)[^1]);
    }

    [Theory]
    [InlineData("image only", "visible")]
    [InlineData("image only", "sanitized")]
    [InlineData("image beside hidden text", "visible")]
    [InlineData("image beside hidden text", "sanitized")]
    public async Task Picture_stays_in_the_section_it_follows(string variant, string policy)
    {
        var source = WriteDocx("picture", new Dictionary<int, string> { [3] = Variants[variant] });
        var (result, directory) = await ExportAsync(source, policy);

        Assert.Equal(6, result.Parts.Count);
        for (var index = 0; index < 6; index++)
        {
            Assert.Equal(index == 2, result.Parts[index].Markdown.Contains("<img src=\"../assets/", StringComparison.Ordinal));
            Assert.Equal(index == 2, result.Parts[index].Markdown.Contains(PictureAlt, StringComparison.Ordinal));
        }
        var line = BodyLines(result.Parts[2].Markdown)[^1];
        Assert.StartsWith("<img src=\"../assets/", line, StringComparison.Ordinal);
        // The part points at the asset document.md points at, and that file is in the package.
        var assetName = line.Split('"')[1]["../assets/".Length..];
        Assert.True(File.Exists(Path.Combine(directory, "assets", assetName)));
        Assert.Contains($"<img src=\"assets/{assetName}\"", await File.ReadAllTextAsync(Path.Combine(directory, "document.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Floating_box_without_host_text_stays_in_the_section_it_follows()
    {
        var source = WriteDocx("box", new Dictionary<int, string> { [3] = Variants["floating box only"] });
        var (result, _) = await ExportAsync(source, "visible");

        Assert.Equal(6, result.Parts.Count);
        for (var index = 0; index < 6; index++)
            Assert.Equal(index == 2, result.Parts[index].Markdown.Contains(BoxText, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(VariantsAndPolicies))]
    public async Task Parts_hold_exactly_the_lines_document_md_holds(string variant, string policy)
    {
        var source = WriteDocx("lines", new Dictionary<int, string> { [3] = Variants[variant] });
        var (result, directory) = await ExportAsync(source, policy);

        var document = BodyLines(await File.ReadAllTextAsync(Path.Combine(directory, "document.md")))
            .Select(line => line.Replace("src=\"assets/", "src=\"../assets/", StringComparison.Ordinal))
            .Skip(1) // the document title; every part repeats it as its first line
            .Order(StringComparer.Ordinal).ToArray();
        var parts = result.Parts.SelectMany(part => BodyLines(part.Markdown).Skip(1)
                .Where(line => !line.StartsWith("ソース: ", StringComparison.Ordinal) && !line.StartsWith("section_path: ", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(document, parts);
    }

    [Theory]
    [InlineData("visible")]
    [InlineData("sanitized")]
    public async Task Hidden_text_does_not_come_back_with_the_empty_parent(string policy)
    {
        foreach (var variant in new[] { "hidden text only", "image beside hidden text" })
        {
            var source = WriteDocx("hidden", new Dictionary<int, string> { [3] = Variants[variant] });
            var (result, directory) = await ExportAsync(source, policy);

            Assert.Equal(6, result.Parts.Count);
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                         .Where(path => path.EndsWith(".md", StringComparison.Ordinal) || path.EndsWith(".json", StringComparison.Ordinal)))
                Assert.DoesNotContain(HiddenSecret, await File.ReadAllTextAsync(path), StringComparison.Ordinal);
            Assert.DoesNotContain(result.Parts, part => part.Markdown.Contains(HiddenSecret, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Complete_policy_still_includes_the_hidden_text_and_splits_as_before()
    {
        var source = WriteDocx("complete", new Dictionary<int, string> { [3] = Variants["image beside hidden text"] });
        var (result, _) = await ExportAsync(source, "complete");

        Assert.Equal(6, result.Parts.Count);
        Assert.Single(result.Parts, part => part.Markdown.Contains(HiddenSecret, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == AiPackageContentBuilder.SplitUnavailableCode);
    }

    [Theory]
    [InlineData("visible")]
    [InlineData("sanitized")]
    public async Task Ambiguous_shape_ids_keep_one_part_and_say_why(string policy)
    {
        // Two floating boxes share one drawing id, so which shape a diagram member names is ambiguous.
        var source = WriteDocx("ambiguous", new Dictionary<int, string>
        {
            [2] = BoxOnlyParagraph("duplicate"),
            [4] = BoxOnlyParagraph("duplicate"),
        });
        var (result, directory) = await ExportAsync(source, policy);

        var part = Assert.Single(result.Parts);
        Assert.True(part.ExceedsTarget);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == AiPackageContentBuilder.SplitUnavailableCode);
        Assert.Equal(DiagnosticSeverity.Information, diagnostic.Severity);
        Assert.Contains("shape id", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("part-0001", diagnostic.Message, StringComparison.Ordinal);

        var review = await File.ReadAllTextAsync(Path.Combine(directory, "review.md"));
        Assert.Contains("Not divided by section", review, StringComparison.Ordinal);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "report.json")));
        var entry = Assert.Single(report.RootElement.GetProperty("diagnostics").EnumerateArray(),
            item => item.GetProperty("code").GetString() == AiPackageContentBuilder.SplitUnavailableCode);
        Assert.Equal("information", entry.GetProperty("severity").GetString());
        Assert.Equal(1, entry.GetProperty("count").GetInt32());
    }

    // ---- The builder on its own, with graphs that no Word file would produce ----

    private static DocumentNode Node(string id, NodeKind kind, string text, int order, string? parentId = null,
        IReadOnlyDictionary<string, JsonElement>? extensions = null) =>
        new(id, kind, parentId, order, ContentLayer.Body, new TextNodeContent(text), Extensions: extensions);

    private static DocumentGraph WordGraph(params DocumentNode[] nodes) =>
        new("1.1", "doc-1", DocumentFormatKind.Docx, [new DocumentPartition("body", 0, nodes)]);

    private static (IReadOnlyList<AiPackagePart> Parts, List<Diagnostic> Diagnostics) Build(DocumentGraph graph, int target = 128)
    {
        var diagnostics = new List<Diagnostic>();
        var parts = AiPackageContentBuilder.Build(graph, new(), "report.docx", new AiPackagePartOptions(target), diagnostics);
        return (parts, diagnostics);
    }

    [Fact]
    public void A_node_whose_parent_is_not_in_the_graph_is_an_ordinary_root()
    {
        var picture = new DocumentNode("picture", NodeKind.Image, "removed-paragraph", 2, ContentLayer.Body,
            new ReferenceNodeContent("assets/figure.png", "Figure"));
        var graph = WordGraph(
            Node("h1", NodeKind.Heading, "First", 0), Node("b1", NodeKind.Paragraph, "first body", 1), picture,
            Node("h2", NodeKind.Heading, "Second", 3), Node("b2", NodeKind.Paragraph, "second body", 4));

        var (parts, diagnostics) = Build(graph);

        Assert.Equal(2, parts.Count);
        Assert.Equal(["h1", "b1", "picture"], parts[0].NodeIds);
        Assert.Equal(["h2", "b2"], parts[1].NodeIds);
        Assert.Contains("assets/figure.png", parts[0].Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("figure.png", parts[1].Markdown, StringComparison.Ordinal);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void A_child_stays_with_its_parent_when_the_parent_is_in_the_graph()
    {
        var graph = WordGraph(
            Node("h1", NodeKind.Heading, "First", 0), Node("host", NodeKind.Paragraph, "host text", 1),
            new DocumentNode("picture", NodeKind.Image, "host", 1, ContentLayer.Body, new ReferenceNodeContent("assets/figure.png", "Figure")),
            Node("h2", NodeKind.Heading, "Second", 2));

        var (parts, _) = Build(graph);

        Assert.Equal(2, parts.Count);
        Assert.Equal(["h1", "host", "picture"], parts[0].NodeIds);
    }

    [Fact]
    public void Parent_cycle_keeps_the_whole_partition_and_reports_it()
    {
        var graph = WordGraph(
            Node("h1", NodeKind.Heading, "First", 0), Node("a", NodeKind.Paragraph, new string('a', 100), 1, parentId: "b"),
            Node("b", NodeKind.Paragraph, new string('b', 100), 2, parentId: "a"),
            Node("h2", NodeKind.Heading, "Second", 3), Node("c", NodeKind.Paragraph, new string('c', 100), 4));

        var (parts, diagnostics) = Build(graph);

        var part = Assert.Single(parts);
        Assert.True(part.ExceedsTarget);
        Assert.Equal(["h1", "a", "b", "h2", "c"], part.NodeIds);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(AiPackageContentBuilder.SplitUnavailableCode, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Information, diagnostic.Severity);
        Assert.Contains("cycle", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_that_fits_one_part_anyway_is_not_reported()
    {
        var graph = WordGraph(
            Node("h1", NodeKind.Heading, "First", 0), Node("a", NodeKind.Paragraph, "short", 1, parentId: "b"),
            Node("b", NodeKind.Paragraph, "short", 2, parentId: "a"));

        var (parts, diagnostics) = Build(graph);

        Assert.False(Assert.Single(parts).ExceedsTarget);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Ambiguous_shape_ids_keep_the_whole_partition_and_report_it()
    {
        JsonElement Shape(string id) => JsonSerializer.SerializeToElement(id);
        var graph = WordGraph(
            Node("h1", NodeKind.Heading, "First", 0),
            Node("a", NodeKind.Shape, new string('a', 100), 1, extensions: new Dictionary<string, JsonElement> { ["shape_id"] = Shape("7") }),
            Node("h2", NodeKind.Heading, "Second", 2),
            Node("b", NodeKind.Shape, new string('b', 100), 3, extensions: new Dictionary<string, JsonElement> { ["shape_id"] = Shape("7") }));

        var (parts, diagnostics) = Build(graph);

        Assert.Single(parts);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(AiPackageContentBuilder.SplitUnavailableCode, diagnostic.Code);
        Assert.Contains("shape id", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostics_are_required_by_the_overload_that_reports_them()
    {
        var graph = WordGraph(Node("h1", NodeKind.Heading, "First", 0));
        Assert.Throws<ArgumentNullException>(() =>
            AiPackageContentBuilder.Build(graph, new(), "report.docx", new AiPackagePartOptions(), null!));
    }
}
