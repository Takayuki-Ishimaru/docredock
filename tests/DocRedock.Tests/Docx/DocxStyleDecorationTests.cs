using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Formats.OpenXml.Docx;
using DocRedock.Markdown;

namespace DocRedock.Tests.Docx;

public sealed class DocxStyleDecorationTests
{
    [Theory]
    [InlineData("<w:strike/>", "~~sample~~")]
    [InlineData("<w:dstrike/>", "~~sample~~")]
    [InlineData("<w:u w:val=\"double\"/>", "<u>sample</u>")]
    [InlineData("<w:strike/><w:u/>", "<u>~~sample~~</u>")]
    public async Task Character_style_marks_reach_readable_and_ai_exports_without_changing_direct_runs(string properties, string expected)
    {
        var source = Document(P("sample", rPr: "<w:rStyle w:val=\"Mark\"/>"), Style("Mark", properties));
        try
        {
            var original = await File.ReadAllBytesAsync(source);
            var adapter = new DocxAdapter();
            var extraction = await adapter.ExtractAsync(source);
            var node = Assert.Single(extraction.Graph.Nodes, n => n.Kind == NodeKind.Paragraph);
            var run = Assert.Single(Assert.IsType<RichTextNodeContent>(node.Content).Runs);
            Assert.False(run.Strike);
            Assert.False(run.Underline);
            Assert.Equal("Mark", run.StyleId);
            Assert.Contains(expected, new ReadableMarkdownSerializer().Serialize(extraction.Graph));
            var restored = Path.Combine(Root(source), "f0.docx");
            Assert.True((await adapter.RestoreAsync(extraction, extraction.Graph, restored)).Succeeded);
            Assert.Equal(original, await File.ReadAllBytesAsync(restored));

            var service = new DocumentService(null, null, discoverPdfRasterizer: false);
            var readable = await service.ExportReadableAsync(new(source, Path.Combine(Root(source), "readable.md")));
            Assert.Contains(expected, await File.ReadAllTextAsync(readable.MarkdownPath));
            var package = await service.ExportAiPackageAsync(new(new(source, "unused.md"), Path.Combine(Root(source), "package")));
            Assert.Contains(expected, string.Join("\n", package.Parts.Select(part => part.Markdown)));
            Assert.Equal(original, await File.ReadAllBytesAsync(source));
        }
        finally { Cleanup(source); }
    }

    [Theory]
    [InlineData("inherit", true, true)]
    [InlineData("two-strike-toggles", false, true)]
    [InlineData("style-strike-off", true, true)]
    [InlineData("direct-off", false, false)]
    [InlineData("direct-on", true, true)]
    [InlineData("underline-off", true, false)]
    [InlineData("double-strike", true, true)]
    [InlineData("double-strike-off", false, true)]
    [InlineData("paragraph-and-character-toggle", false, true)]
    [InlineData("paragraph-and-character-off", true, false)]
    [InlineData("default-paragraph", true, true)]
    [InlineData("default-character", false, false)]
    [InlineData("document-default", true, true)]
    [InlineData("paragraph-mark", false, false)]
    [InlineData("missing-style", true, true)]
    [InlineData("wrong-base-type", false, false)]
    [InlineData("missing-base", true, true)]
    public async Task Style_inheritance_obeys_toggle_and_direct_override_rules(string scenario, bool struck, bool underlined)
    {
        var styles = Style("Base", "<w:strike/><w:u/>");
        var rPr = "<w:rStyle w:val=\"Mark\"/>";
        var pPr = "";
        switch (scenario)
        {
            case "inherit": styles += Style("Mark", "", basedOn: "Base"); break;
            case "two-strike-toggles": styles += Style("Mark", "<w:strike/>", basedOn: "Base"); break;
            case "style-strike-off": styles += Style("Mark", "<w:strike w:val=\"off\"/>", basedOn: "Base"); break;
            case "direct-off": styles += Style("Mark", "", basedOn: "Base"); rPr += "<w:strike w:val=\"false\"/><w:u w:val=\"none\"/>"; break;
            case "direct-on": styles += Style("Mark", "<w:strike/><w:u w:val=\"none\"/>", basedOn: "Base"); rPr += "<w:strike/><w:u w:val=\"single\"/>"; break;
            case "underline-off": styles += Style("Mark", "<w:u w:val=\"none\"/>", basedOn: "Base"); break;
            case "double-strike": styles = Style("Base", "<w:dstrike/><w:u/>") + Style("Mark", "<w:dstrike/>", basedOn: "Base"); break;
            case "double-strike-off": styles = Style("Base", "<w:dstrike/><w:u/>") + Style("Mark", "<w:dstrike w:val=\"0\"/>", basedOn: "Base"); break;
            case "paragraph-and-character-toggle": styles = Style("Para", "<w:strike/><w:u/>", "paragraph") + Style("Mark", "<w:strike/>"); pPr = "<w:pStyle w:val=\"Para\"/>"; break;
            case "paragraph-and-character-off": styles = Style("Para", "<w:strike/><w:u/>", "paragraph") + Style("Mark", "<w:strike w:val=\"false\"/><w:u w:val=\"none\"/>"); pPr = "<w:pStyle w:val=\"Para\"/>"; break;
            case "default-paragraph": styles = Style("Para", "<w:strike/><w:u/>", "paragraph", isDefault: true); rPr = ""; break;
            case "default-character": styles = Style("Mark", "<w:strike/><w:u/>", isDefault: true); rPr = ""; break;
            case "document-default": styles = Defaults("<w:strike/><w:u/>"); rPr = ""; break;
            case "paragraph-mark": styles = ""; pPr = "<w:rPr><w:strike/><w:u/></w:rPr>"; rPr = ""; break;
            case "missing-style": styles = Defaults("<w:strike/><w:u/>"); break;
            case "wrong-base-type": styles = Style("Base", "<w:strike/><w:u/>", "paragraph") + Style("Mark", "", basedOn: "Base"); break;
            case "missing-base": styles = Style("Mark", "<w:strike/><w:u/>", basedOn: "Missing"); break;
        }
        var source = Document(P("sample", pPr, rPr), styles);
        try
        {
            var result = await new DocxAdapter().ExtractAsync(source);
            var markdown = new ReadableMarkdownSerializer().Serialize(result.Graph);
            Assert.Equal(struck, markdown.Contains("~~sample~~", StringComparison.Ordinal));
            Assert.Equal(underlined, markdown.Contains("<u>", StringComparison.Ordinal));
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task Styled_marks_cover_tables_textboxes_furniture_and_notes_but_do_not_reveal_hidden_text()
    {
        static string Marked(string text) => P(text, rPr: "<w:rStyle w:val=\"Mark\"/>");
        var source = Document(Marked("body") + "<w:tbl><w:tr><w:tc>" + P("heading") + "</w:tc></w:tr><w:tr><w:tc>" + Marked("cell") + "</w:tc></w:tr></w:tbl>" +
            "<w:p><w:r><w:pict><v:shape><v:textbox><w:txbxContent>" + Marked("box") + "</w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>" +
            P("secret", rPr: "<w:rStyle w:val=\"Hidden\"/>"),
            Style("Mark", "<w:strike/><w:u/>") + Style("Hidden", "<w:vanish/>", basedOn: "Mark"),
            new Dictionary<string, string>
            {
                ["word/header1.xml"] = $"<w:hdr xmlns:w=\"{Ns}\">{Marked("header")}</w:hdr>",
                ["word/footer1.xml"] = $"<w:ftr xmlns:w=\"{Ns}\">{Marked("footer")}</w:ftr>",
                ["word/footnotes.xml"] = $"<w:footnotes xmlns:w=\"{Ns}\"><w:footnote w:id=\"1\">{Marked("footnote")}</w:footnote></w:footnotes>",
                ["word/endnotes.xml"] = $"<w:endnotes xmlns:w=\"{Ns}\"><w:endnote w:id=\"1\">{Marked("endnote")}</w:endnote></w:endnotes>"
            });
        try
        {
            var extraction = await new DocxAdapter().ExtractAsync(source);
            var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);
            foreach (var text in new[] { "body", "cell", "box", "header", "footer", "footnote", "endnote" })
                Assert.Contains($"<u>~~{text}~~</u>", markdown);
            Assert.DoesNotContain("secret", markdown);
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task F1_text_edit_preserves_style_xml_and_does_not_materialize_inherited_marks()
    {
        var source = Document(P("sample", rPr: "<w:rStyle w:val=\"Mark\"/>"), Style("Mark", "<w:strike/><w:u/>"));
        try
        {
            var service = new DocumentService(null, null, discoverPdfRasterizer: false);
            var markdown = Path.Combine(Root(source), "roundtrip.md");
            var workspace = Path.Combine(Root(source), "roundtrip.drmd");
            await service.ExportAsync(new(source, workspace, markdown));
            var projection = await File.ReadAllTextAsync(markdown);
            Assert.DoesNotContain("~~sample~~", projection);
            Assert.DoesNotContain("<u>sample</u>", projection);
            await File.WriteAllTextAsync(markdown, projection.Replace("sample", "edited", StringComparison.Ordinal));
            var output = Path.Combine(Root(source), "edited.docx");
            var restore = await service.RestoreAsync(new(workspace, output, markdown));
            Assert.True(restore.Succeeded);
            Assert.Equal(FidelityLevel.F1, restore.Fidelity);
            Assert.Equal(Entry(source, "word/styles.xml"), Entry(output, "word/styles.xml"));
            var xml = Entry(output, "word/document.xml");
            Assert.Contains("w:rStyle", xml);
            Assert.DoesNotContain("w:strike", xml);
            Assert.DoesNotContain("w:u ", xml);
            var reextracted = await new DocxAdapter().ExtractAsync(output);
            Assert.Contains("<u>~~edited~~</u>", new ReadableMarkdownSerializer().Serialize(reextracted.Graph));
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task Style_underlines_do_not_emphasize_links_or_headings_and_code_paragraphs_keep_marks()
    {
        var source = Document(P("Heading", "<w:pStyle w:val=\"Heading1\"/>", "<w:rStyle w:val=\"Mark\"/>") +
            P("code", "<w:pStyle w:val=\"Code\"/>", "<w:rStyle w:val=\"Mark\"/>") +
            "<w:p><w:hyperlink w:anchor=\"bookmark\"><w:r><w:rPr><w:rStyle w:val=\"Mark\"/></w:rPr><w:t>link</w:t></w:r></w:hyperlink></w:p>",
            Style("Mark", "<w:u/>"));
        try
        {
            var result = await new DocxAdapter().ExtractAsync(source);
            var markdown = new ReadableMarkdownSerializer().Serialize(result.Graph);
            Assert.Contains("<u>code</u>", markdown);
            Assert.DoesNotContain("<u>Heading</u>", markdown);
            Assert.DoesNotContain("<u>link</u>", markdown);
        }
        finally { Cleanup(source); }
    }

    [Theory]
    [InlineData("<w:strike/>", "<w:strike w:val=\"false\"/>")]
    [InlineData("<w:dstrike/>", "<w:dstrike w:val=\"off\"/>")]
    [InlineData("<w:u/>", "<w:u w:val=\"none\"/>")]
    public async Task F1_text_edit_keeps_explicit_style_overrides(string inherited, string direct)
    {
        var source = Document(P("sample", rPr: "<w:rStyle w:val=\"Mark\"/>" + direct), Style("Mark", inherited));
        try
        {
            var service = new DocumentService(null, null, discoverPdfRasterizer: false);
            var markdown = Path.Combine(Root(source), "roundtrip.md");
            var workspace = Path.Combine(Root(source), "roundtrip.drmd");
            await service.ExportAsync(new(source, workspace, markdown));
            var projection = await File.ReadAllTextAsync(markdown);
            await File.WriteAllTextAsync(markdown, projection.Replace("sample", "edited", StringComparison.Ordinal));
            var output = Path.Combine(Root(source), "edited.docx");
            Assert.True((await service.RestoreAsync(new(workspace, output, markdown))).Succeeded);
            var expected = XElement.Parse($"<w:rPr xmlns:w=\"{Ns}\">{direct}</w:rPr>").Elements().Single();
            var actual = XDocument.Parse(Entry(output, "word/document.xml")).Descendants(expected.Name).SingleOrDefault();
            Assert.NotNull(actual);
            Assert.Equal((string?)expected.Attribute(XName.Get("val", Ns)), (string?)actual.Attribute(XName.Get("val", Ns)));
            var reextracted = await new DocxAdapter().ExtractAsync(output);
            var readable = new ReadableMarkdownSerializer().Serialize(reextracted.Graph);
            Assert.Contains("edited", readable);
            Assert.DoesNotContain("~~edited~~", readable);
            Assert.DoesNotContain("<u>edited</u>", readable);
        }
        finally { Cleanup(source); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cyclic_and_excessively_deep_applied_styles_finish_with_explicit_source_review(bool deep)
    {
        var styles = deep
            ? string.Concat(Enumerable.Range(0, 40).Select(index => Style("S" + index,
                index == 39 ? "<w:strike/><w:u/>" : "", basedOn: index == 39 ? null : "S" + (index + 1))))
            : Style("S0", "<w:strike/><w:u/>", basedOn: "S1") + Style("S1", "", basedOn: "S0");
        var source = Document(P("sample", rPr: "<w:rStyle w:val=\"S0\"/>"), styles);
        try
        {
            var result = await new DocxAdapter().ExtractAsync(source).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(result.Diagnostics, item => item.Code == "DocxStyleDecorationUnresolved");
            var node = Assert.Single(result.Graph.Nodes, n => n.Kind == NodeKind.Paragraph);
            Assert.True(node.Extensions![ReviewExtensionKeys.SourceReviewRequired].GetBoolean());
            Assert.Contains("sample", new ReadableMarkdownSerializer().Serialize(result.Graph));
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task Concurrent_documents_do_not_share_style_state_and_cancelled_extraction_stops()
    {
        var marked = Document(P("marked", rPr: "<w:rStyle w:val=\"Mark\"/>"), Style("Mark", "<w:strike/><w:u/>"));
        var plain = Document(P("plain", rPr: "<w:rStyle w:val=\"Mark\"/>"), Style("Mark", ""));
        try
        {
            var adapter = new DocxAdapter();
            var results = await Task.WhenAll(adapter.ExtractAsync(marked).AsTask(), adapter.ExtractAsync(plain).AsTask())
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("<u>~~marked~~</u>", new ReadableMarkdownSerializer().Serialize(results[0].Graph));
            var unmarked = new ReadableMarkdownSerializer().Serialize(results[1].Graph);
            Assert.DoesNotContain("~~plain~~", unmarked);
            Assert.DoesNotContain("<u>plain</u>", unmarked);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ExtractAsync(marked, cancellationToken: cancellation.Token).AsTask());
        }
        finally { Cleanup(marked); Cleanup(plain); }
    }

    private const string Ns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static string P(string text, string pPr = "", string rPr = "") => $"<w:p><w:pPr>{pPr}</w:pPr><w:r><w:rPr>{rPr}</w:rPr><w:t>{text}</w:t></w:r></w:p>";
    private static string Style(string id, string properties, string type = "character", string? basedOn = null, bool isDefault = false) =>
        $"<w:style w:type=\"{type}\" w:styleId=\"{id}\"{(isDefault ? " w:default=\"1\"" : "")}>{(basedOn is null ? "" : $"<w:basedOn w:val=\"{basedOn}\"/>")}<w:rPr>{properties}</w:rPr></w:style>";
    private static string Defaults(string properties) => $"<w:docDefaults><w:rPrDefault><w:rPr>{properties}</w:rPr></w:rPrDefault></w:docDefaults>";
    private static string Root(string source) => Path.GetDirectoryName(source)!;
    private static void Cleanup(string source) => Directory.Delete(Root(source), true);
    private static string Entry(string source, string name)
    {
        using var archive = ZipFile.OpenRead(source);
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
    private static string Document(string body, string styles, Dictionary<string, string>? extra = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "docredock-style-decorations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.docx");
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>",
            ["_rels/.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"doc\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>",
            ["word/_rels/document.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"styles\" Type=\"styles\" Target=\"styles.xml\"/><Relationship Id=\"header\" Type=\"header\" Target=\"header1.xml\"/><Relationship Id=\"footer\" Type=\"footer\" Target=\"footer1.xml\"/></Relationships>",
            ["word/document.xml"] = $"<w:document xmlns:w=\"{Ns}\" xmlns:v=\"urn:schemas-microsoft-com:vml\"><w:body>{body}</w:body></w:document>",
            ["word/styles.xml"] = $"<w:styles xmlns:w=\"{Ns}\">{styles}</w:styles>"
        };
        using var archive = ZipFile.Open(source, ZipArchiveMode.Create);
        foreach (var (name, content) in parts.Concat(extra ?? []))
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        return source;
    }
}
