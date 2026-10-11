using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Docx;
using DocRedock.Markdown;
using DocRedock.Render;

namespace DocRedock.Tests.Docx;

public sealed partial class DocxReadabilityTests
{
    [Theory]
    [InlineData("del", "ins")]
    [InlineData("moveFrom", "moveTo")]
    public async Task Readable_and_ai_packages_show_text_revisions_and_preserve_source(string deleted, string inserted)
    {
        var source = Document($"<w:p><w:r><w:t>Before </w:t></w:r><w:{deleted}><w:r><w:delText>old</w:delText></w:r></w:{deleted}>" +
            $"<w:{inserted}><w:r><w:rPr><w:b/></w:rPr><w:t>new</w:t></w:r></w:{inserted}><w:r><w:t> after</w:t></w:r></w:p>" +
            $"<w:tbl><w:tr><w:tc><w:p><w:{deleted}><w:r><w:delText>removed</w:delText></w:r></w:{deleted}>" +
            $"<w:{inserted}><w:r><w:t>added</w:t></w:r></w:{inserted}></w:p></w:tc></w:tr></w:tbl>");
        try
        {
            var original = await File.ReadAllBytesAsync(source);
            var adapter = new DocxAdapter();
            var roundtrip = await adapter.ExtractAsync(source);
            Assert.True(roundtrip.SourceIndex.HasTrackedRevisions);
            Assert.DoesNotContain(roundtrip.Graph.Nodes.Where(node => node.Layer == ContentLayer.Body),
                node => node.Content is TextNodeContent { Text: var text } && text.Contains("old"));
            var restored = Path.Combine(Path.GetDirectoryName(source)!, "restore.docx");
            Assert.True((await adapter.RestoreAsync(roundtrip, roundtrip.Graph, restored)).Succeeded);
            Assert.Equal(original, await File.ReadAllBytesAsync(restored));
            var visible = await adapter.ExtractAsync(source, new(ShowTrackedRevisions: true));
            var text = Assert.Single(visible.Graph.Nodes, node => node.Content is RichTextNodeContent);
            var runs = ((RichTextNodeContent)text.Content!).Runs;
            Assert.Contains(runs, run => run.Text == "old" && run.Strike);
            Assert.Contains(runs, run => run.Text == "new" && run.Inserted && run.Bold);
            var service = new DocumentService(null, null, discoverPdfRasterizer: false);
            var result = await service.ExportReadableAsync(new(source, Path.Combine(Path.GetDirectoryName(source)!, "readable.md")));
            var markdown = await File.ReadAllTextAsync(result.MarkdownPath);
            Assert.Contains("~~old~~", markdown);
            Assert.Contains("<ins>**new**</ins>", markdown);
            Assert.Contains("~~removed~~<ins>added</ins>", markdown);
            var package = await service.ExportAiPackageAsync(new(new(source, "unused.md"), Path.Combine(Path.GetDirectoryName(source)!, "package")));
            Assert.Contains("~~old~~", string.Join("\n", package.Parts.Select(part => part.Markdown)));
            Assert.Contains("<ins>**new**</ins>", string.Join("\n", package.Parts.Select(part => part.Markdown)));
            Assert.Equal(original, await File.ReadAllBytesAsync(source));
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task Hidden_revision_text_stays_hidden_and_literal_insert_tags_are_escaped()
    {
        var source = Document("<w:p><w:ins><w:r><w:rPr><w:vanish/></w:rPr><w:t>secret</w:t></w:r></w:ins>" +
            "<w:r><w:t>&lt;ins&gt;literal&lt;/ins&gt;</w:t></w:r></w:p>");
        try
        {
            var visible = await new DocxAdapter().ExtractAsync(source, new(ShowTrackedRevisions: true));
            var markdown = new ReadableMarkdownSerializer().Serialize(visible.Graph);
            Assert.DoesNotContain("secret", markdown);
            Assert.DoesNotContain("<ins>literal</ins>", markdown);
            Assert.Contains("literal", markdown);
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task Header_and_footnote_revisions_keep_their_marks_and_revision_diagnostic()
    {
        var source = Document(P("Body"));
        try
        {
            const string ns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            static string Revision(string label) => $"<w:p><w:del><w:r><w:delText>old {label}</w:delText></w:r></w:del>" +
                $"<w:ins><w:r><w:t>new {label}</w:t></w:r></w:ins></w:p>";
            using (var archive = System.IO.Compression.ZipFile.Open(source, System.IO.Compression.ZipArchiveMode.Update))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("word/header1.xml").Open()))
                    writer.Write($"<w:hdr xmlns:w=\"{ns}\">{Revision("header")}</w:hdr>");
                using (var writer = new StreamWriter(archive.CreateEntry("word/footnotes.xml").Open()))
                    writer.Write($"<w:footnotes xmlns:w=\"{ns}\"><w:footnote w:id=\"1\">{Revision("note")}</w:footnote></w:footnotes>");
                var entry = archive.GetEntry("word/_rels/document.xml.rels")!;
                string relationships;
                using (var reader = new StreamReader(entry.Open())) relationships = reader.ReadToEnd();
                entry.Delete();
                using var relations = new StreamWriter(archive.CreateEntry("word/_rels/document.xml.rels").Open());
                relations.Write(relationships.Replace("</Relationships>", "<Relationship Id=\"header\" Type=\"header\" Target=\"header1.xml\"/></Relationships>"));
            }
            var result = await new DocxAdapter().ExtractAsync(source, new(ShowTrackedRevisions: true));
            Assert.True(result.SourceIndex.HasTrackedRevisions);
            Assert.Contains(result.Diagnostics, item => item.Code == "TrackedRevisionsPresent");
            var markdown = new ReadableMarkdownSerializer().Serialize(result.Graph);
            Assert.Contains("~~old header~~<ins>new header</ins>", markdown);
            Assert.Contains("~~old note~~<ins>new note</ins>", markdown);
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public void Insert_markup_keeps_nested_emphasis_and_strike_in_html_and_gui_runs()
    {
        const string text = "<ins>**new** ~~old~~</ins> tail";
        Assert.Contains("<ins><strong>new</strong> <del>old</del></ins>", MarkdownInlineParser.Html(text));
        Assert.Equal("new old tail", MarkdownInlineParser.PlainText(text));
        var runs = MarkdownInlineParser.Runs(text);
        Assert.Contains(runs, run => run.Text == "new" && run.Bold && run.Underline);
        Assert.Contains(runs, run => run.Text == "old" && run.Strike && run.Underline);
        Assert.Contains(runs, run => run.Text == " tail" && !run.Underline);
    }
}
