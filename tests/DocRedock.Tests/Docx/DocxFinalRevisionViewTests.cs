using System.IO.Compression;
using DocRedock.Api;

namespace DocRedock.Tests.Docx;

public sealed partial class DocxReadabilityTests
{
    [Theory]
    [InlineData("visible")]
    [InlineData("complete")]
    [InlineData("sanitized")]
    public async Task Final_revision_view_excludes_deleted_structures_objects_and_related_part_history(string policy)
    {
        static string Cell(string text, string properties = "") => $"<w:tc><w:tcPr>{properties}</w:tcPr>{P(text)}</w:tc>";
        static string Revision(string label) => $"<w:p><w:moveFrom><w:r><w:delText>SECRET {label}</w:delText></w:r></w:moveFrom>" +
            $"<w:moveTo><w:r><w:t>current {label}</w:t></w:r></w:moveTo></w:p>";
        var source = Document(Revision("body") +
            "<w:p><w:r><w:rPr><w:b/><w:rPrChange w:author=\"SECRET author\"><w:rPr><w:color w:val=\"ABCDEF\"/></w:rPr></w:rPrChange></w:rPr><w:t>current format</w:t></w:r></w:p>" +
            "<w:p><w:del><w:r><w:pict><v:shape><v:imagedata r:id=\"rIdImage\"/></v:shape></w:pict></w:r></w:del></w:p>" +
            "<w:tbl><w:tr><w:trPr><w:del/></w:trPr>" + Cell("SECRET row") + "</w:tr><w:tr>" +
            Cell("SECRET cell", "<w:cellDel/>") + Cell("current cell", "<w:cellIns/>") + "</w:tr></w:tbl>");
        try
        {
            const string ns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
            {
                foreach (var (name, content) in new Dictionary<string, string>
                {
                    ["header1.xml"] = $"<w:hdr xmlns:w=\"{ns}\">{Revision("header")}</w:hdr>",
                    ["footer1.xml"] = $"<w:ftr xmlns:w=\"{ns}\">{Revision("footer")}</w:ftr>",
                    ["footnotes.xml"] = $"<w:footnotes xmlns:w=\"{ns}\"><w:footnote w:id=\"1\">{Revision("footnote")}</w:footnote></w:footnotes>",
                    ["endnotes.xml"] = $"<w:endnotes xmlns:w=\"{ns}\"><w:endnote w:id=\"1\">{Revision("endnote")}</w:endnote></w:endnotes>",
                    ["comments.xml"] = $"<w:comments xmlns:w=\"{ns}\"><w:comment w:id=\"1\">{Revision("comment")}</w:comment></w:comments>"
                })
                {
                    using var writer = new StreamWriter(archive.CreateEntry("word/" + name).Open()); writer.Write(content);
                }
                var entry = archive.GetEntry("word/_rels/document.xml.rels")!;
                string relationships;
                using (var reader = new StreamReader(entry.Open())) relationships = reader.ReadToEnd();
                entry.Delete();
                using var relations = new StreamWriter(archive.CreateEntry("word/_rels/document.xml.rels").Open());
                relations.Write(relationships.Replace("</Relationships>",
                    "<Relationship Id=\"header\" Type=\"header\" Target=\"header1.xml\"/>" +
                    "<Relationship Id=\"footer\" Type=\"footer\" Target=\"footer1.xml\"/></Relationships>"));
            }
            var original = File.ReadAllBytes(source);
            var target = Path.Combine(Path.GetDirectoryName(source)!, "final-package");
            var result = await new DocumentService(null, null, discoverPdfRasterizer: false).ExportAiPackageAsync(
                new(new(source, "unused.md", ContentPolicy: policy, Revisions: "final"), target));
            var allText = string.Join("\n", Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
            Assert.DoesNotContain("SECRET", allText); Assert.DoesNotContain("ABCDEF", allText);
            Assert.DoesNotContain("<ins>", allText); Assert.DoesNotContain("変更履歴（", allText);
            Assert.Contains("current body", allText); Assert.Contains("current cell", allText);
            if (policy != "sanitized")
            {
                Assert.Contains("current header", allText); Assert.Contains("current footer", allText);
            }
            if (policy == "complete")
            {
                Assert.Contains("current footnote", allText); Assert.Contains("current endnote", allText); Assert.Contains("current comment", allText);
            }
            Assert.False(Directory.Exists(Path.Combine(target, "assets")));
            Assert.True(result.Review.SharingReview!.RevisionsPresent);
            Assert.False(result.Review.SharingReview.Required); Assert.False(result.Review.SharingReview.DeletedTextIncluded);
            Assert.Equal(original, File.ReadAllBytes(source));
        }
        finally { Cleanup(source); }
    }
}
