using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Docx;
using DocRedock.Markdown;

namespace DocRedock.Tests.Docx;

public sealed class DocxReadabilityTests
{
    [Theory]
    [InlineData("21", "", 2)]
    [InlineData("Derived", "", 2)]
    [InlineData("21", "<w:outlineLvl w:val=\"0\"/>", 1)]
    [InlineData("21", "<w:outlineLvl w:val=\"9\"/>", 0)]
    [InlineData("21", "<w:outlineLvl/>", 0)]
    [InlineData("CycleA", "", 3)]
    public async Task Style_names_inheritance_and_direct_outline_have_stable_precedence(string style, string direct, int level)
    {
        var source = Document(P("Section", $"<w:pStyle w:val=\"{style}\"/>{direct}"),
            "<w:style w:type=\"paragraph\" w:styleId=\"21\"><w:name w:val=\"heading 2\"/><w:pPr><w:outlineLvl w:val=\"1\"/></w:pPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Derived\"><w:name w:val=\"Custom\"/><w:basedOn w:val=\"21\"/></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"CycleA\"><w:name w:val=\"heading 3\"/><w:basedOn w:val=\"CycleB\"/></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"CycleB\"><w:basedOn w:val=\"CycleA\"/></w:style>");
        try
        {
            var adapter = new DocxAdapter(); var extraction = await adapter.ExtractAsync(source);
            var node = Assert.Single(extraction.Graph.Nodes, n => n.Content is TextNodeContent { Text: "Section" });
            Assert.Equal(level == 0 ? NodeKind.Paragraph : NodeKind.Heading, node.Kind);
            if (level > 0) Assert.Equal(level, node.Extensions!["heading_level"].GetInt32());
            var restored = Path.Combine(Path.GetDirectoryName(source)!, "restored.docx");
            Assert.True((await adapter.RestoreAsync(extraction, extraction.Graph, restored)).Succeeded);
            Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(restored));
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task Directly_formatted_title_and_repeated_chapters_keep_hierarchy_and_f0()
    {
        var bodyText = new string('文', 95);
        var source = Document(P("秘密", "", "<w:vanish/><w:sz w:val=\"90\"/>") +
            P("技術報告", "<w:jc w:val=\"center\"/>", "<w:sz w:val=\"32\"/>") +
            P("１．はじめに") + P(bodyText, "<w:ind w:firstLine=\"240\"/>") +
            P("２．１　方法") + P(bodyText, "<w:ind w:firstLine=\"240\"/>") +
            P("強調だけ", "", "<w:b/>") + P("1. ordinary step", "<w:numPr><w:numId w:val=\"1\"/></w:numPr>"));
        try
        {
            var adapter = new DocxAdapter(); var extraction = await adapter.ExtractAsync(source);
            var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);
            Assert.StartsWith("# 技術報告\n", markdown, StringComparison.Ordinal);
            Assert.Contains("## １．はじめに", markdown, StringComparison.Ordinal);
            Assert.Contains("### ２．１　方法", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("秘密", markdown, StringComparison.Ordinal);
            Assert.Contains(extraction.Graph.Nodes, n => n.Kind == NodeKind.Paragraph && n.Content is RichTextNodeContent);
            Assert.Contains(extraction.Graph.Nodes, n => n.Kind == NodeKind.ListItem);
            var restored = Path.Combine(Path.GetDirectoryName(source)!, "restored.docx");
            Assert.True((await adapter.RestoreAsync(extraction, extraction.Graph, restored)).Succeeded);
            Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(restored));
            var package = await new DocumentService().ExportAiPackageAsync(new(new(source, "unused.md"), Path.Combine(Path.GetDirectoryName(source)!, "package")));
            Assert.Contains(package.Parts, p => p.Markdown.Contains("１．はじめに", StringComparison.Ordinal));
            var edited = extraction.Graph with { Partitions = extraction.Graph.Partitions.Select(part => part with
                { Nodes = part.Nodes.Select(n => n.Extensions?.ContainsKey("document_title") == true
                    ? n with { Content = new TextNodeContent("技術報告（修正版）") } : n).ToArray() }).ToArray() };
            var f1 = Path.Combine(Path.GetDirectoryName(source)!, "edited.docx");
            Assert.True((await adapter.RestoreAsync(extraction, edited, f1)).Succeeded);
            using var restoredZip = ZipFile.OpenRead(f1);
            using var reader = new StreamReader(restoredZip.GetEntry("word/document.xml")!.Open());
            var xml = await reader.ReadToEndAsync();
            Assert.Contains("技術報告（修正版）", xml, StringComparison.Ordinal);
            Assert.Contains("w:sz w:val=\"32\"", xml, StringComparison.Ordinal);
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task Ordinary_numbered_steps_bold_emphasis_and_style_toggle_are_not_headings()
    {
        var source = Document(P("1. First step") + P("2. Second step") + P(new string('x', 90)) +
            P("Small centered", "<w:jc w:val=\"center\"/>") + P("Important", "", "<w:b/>") +
            P("Toggle off", "<w:pStyle w:val=\"Child\"/>", "<w:sz w:val=\"32\"/>"),
            "<w:style w:type=\"paragraph\" w:styleId=\"Base\"><w:rPr><w:b/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Child\"><w:basedOn w:val=\"Base\"/><w:rPr><w:b/></w:rPr></w:style>");
        try { Assert.DoesNotContain((await new DocxAdapter().ExtractAsync(source)).Graph.Nodes, n => n.Kind == NodeKind.Heading); }
        finally { Cleanup(source); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_images_keep_each_source_display_size_and_work_in_exports(bool vml)
    {
        string Image(string size, string alt, bool floating = false) => vml
            ? $"<w:p><w:r><w:pict><v:shape style=\"{size}\" alt=\"{alt}\"><v:imagedata r:id=\"rIdImage\"/></v:shape></w:pict></w:r></w:p>"
            : $"<w:p><w:r><w:drawing><wp:{(floating ? "anchor" : "inline")}>{size}<wp:docPr id=\"1\" name=\"image\" descr=\"{alt}\"/><a:graphic><a:graphicData><a:blip r:embed=\"rIdImage\"/></a:graphicData></a:graphic></wp:{(floating ? "anchor" : "inline")}></w:drawing></w:r></w:p>";
        var source = Document(Image(vml ? "width:72pt;height:36pt" : "<wp:extent cx=\"914400\" cy=\"457200\"/>", "Logo &quot; &lt; &amp;") +
            Image(vml ? "width:2in;height:1in" : "<wp:extent cx=\"1828800\" cy=\"914400\"/>", "Second", true) +
            Image(vml ? "width:0pt;height:NaNpt" : "<wp:extent cx=\"-1\" cy=\"NaN\"/>", "No size"));
        try
        {
            var dir = Path.GetDirectoryName(source)!; var service = new DocumentService();
            var export = await service.ExportReadableAsync(new(source, Path.Combine(dir, "readable.md")));
            var markdown = await File.ReadAllTextAsync(export.MarkdownPath);
            Assert.Contains("width=\"96\" height=\"48\"", markdown, StringComparison.Ordinal);
            Assert.Contains("width=\"192\" height=\"96\"", markdown, StringComparison.Ordinal);
            Assert.Contains("alt=\"Logo &quot; &lt; &amp;\"", markdown, StringComparison.Ordinal);
            Assert.Contains("![No size]", markdown, StringComparison.Ordinal);
            foreach (Match match in Regex.Matches(markdown, "src=\"([^\"]+)\""))
                Assert.True(File.Exists(Path.Combine(dir, Uri.UnescapeDataString(match.Groups[1].Value))));
            var embedded = await service.ExportReadableAsync(new(source, Path.Combine(dir, "embedded.md"), EmbedImages: true));
            Assert.Contains("src=\"data:image/png;base64,", await File.ReadAllTextAsync(embedded.MarkdownPath), StringComparison.Ordinal);
            var package = await service.ExportAiPackageAsync(new(new(source, "unused.md"), Path.Combine(dir, "package")));
            foreach (var part in package.Parts)
                foreach (Match match in Regex.Matches(part.Markdown, "src=\"([^\"]+)\""))
                    Assert.True(File.Exists(Path.GetFullPath(Path.Combine(dir, "package", Path.GetDirectoryName(part.Path)!, Uri.UnescapeDataString(match.Groups[1].Value)))));
        }
        finally { Cleanup(source); }
    }

    private static string P(string text, string pPr = "", string rPr = "") =>
        $"<w:p><w:pPr>{pPr}</w:pPr><w:r><w:rPr>{rPr}</w:rPr><w:t>{text}</w:t></w:r></w:p>";
    private static void Cleanup(string source) => Directory.Delete(Path.GetDirectoryName(source)!, true);
    private static string Document(string body, string styles = "")
    {
        var dir = Path.Combine(Path.GetTempPath(), "docredock-readability-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "source.docx");
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"png\" ContentType=\"image/png\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>",
            ["_rels/.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>",
            ["word/document.xml"] = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:v=\"urn:schemas-microsoft-com:vml\"><w:body>" + body + "</w:body></w:document>",
            ["word/styles.xml"] = "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val=\"22\"/></w:rPr></w:rPrDefault></w:docDefaults>" + styles + "</w:styles>",
            ["word/_rels/document.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdImage\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/logo.png\"/></Relationships>"
        };
        using var zip = ZipFile.Open(source, ZipArchiveMode.Create);
        foreach (var (name, content) in parts) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(content); }
        using var image = zip.CreateEntry("word/media/logo.png").Open();
        image.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jB1cAAAAASUVORK5CYII="));
        return source;
    }
}
