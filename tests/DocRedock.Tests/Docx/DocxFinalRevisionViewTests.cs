using System.IO.Compression;
using DocRedock.Api;

namespace DocRedock.Tests.Docx;

public sealed partial class DocxReadabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uniform_column_deletion_is_identical_for_merged_and_adjacent_deleted_cells(bool reverseRows)
    {
        static string Cell(string text, string properties = "") => $"<w:tc><w:tcPr>{properties}</w:tcPr>{P(text)}</w:tc>";
        var merged = Cell("SECRET_MERGED", "<w:cellDel/><w:gridSpan w:val=\"2\"/>");
        var singles = Cell("SECRET_LEFT", "<w:cellDel/>") + Cell("SECRET_MIDDLE", "<w:cellDel/>");
        var row1 = (reverseRows ? singles : merged) + Cell("HEADER_RIGHT");
        var row2 = (reverseRows ? merged : singles) + Cell("KEPT_RIGHT");
        var source = Document("<w:tbl><w:tblGrid><w:gridCol w:w=\"1000\"/><w:gridCol w:w=\"1000\"/><w:gridCol w:w=\"1000\"/></w:tblGrid>" +
            "<w:tr>" + row1 + "</w:tr><w:tr>" + row2 + "</w:tr></w:tbl>");
        try
        {
            var result = await new DocumentService(null, null, discoverPdfRasterizer: false).ExportAiPackageAsync(
                new(new(source, "unused.md", Revisions: "final", EnableOcr: false), Path.Combine(Path.GetDirectoryName(source)!, "final")));
            var text = string.Join("\n", result.Parts.Select(part => part.Markdown)).Replace("\\", "");
            Assert.Contains("| HEADER_RIGHT |", text); Assert.Contains("| KEPT_RIGHT |", text); Assert.DoesNotContain("SECRET", text);
            Assert.False(result.Review.Required); Assert.Equal(StructureBasis.Source, result.Summary.TableStructure);
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "DocxFinalTableGridUnresolved");
        }
        finally { Cleanup(source); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Final_table_fallback_preserves_spanned_and_nested_cells_without_guessing_columns(bool spanned, bool nested)
    {
        static string Cell(string text, string properties = "") => $"<w:tc><w:tcPr>{properties}</w:tcPr>{P(text)}</w:tc>";
        var kept = nested ? "<w:tc><w:p/><w:tbl><w:tr>" + Cell("NESTED_KEPT") + "</w:tr></w:tbl>" + P("KEPT") + "</w:tc>" : Cell("KEPT");
        var source = Document("<w:tbl><w:tr>" + Cell("HEADER_LEFT", spanned ? "<w:gridSpan w:val=\"2\"/>" : "") +
            Cell("HEADER_RIGHT") + "</w:tr><w:tr>" + Cell("SECRET", "<w:cellDel/>" + (spanned ? "<w:gridSpan w:val=\"2\"/>" : "")) + kept + "</w:tr></w:tbl>");
        try
        {
            var result = await new DocumentService(null, null, discoverPdfRasterizer: false).ExportAiPackageAsync(
                new(new(source, "unused.md", Revisions: "final", EnableOcr: false), Path.Combine(Path.GetDirectoryName(source)!, "final")));
            var text = string.Join("\n", result.Parts.Select(part => part.Markdown)).Replace("\\", "");
            Assert.Contains("KEPT", text); Assert.DoesNotContain("SECRET", text);
            if (nested) Assert.Contains("NESTED_KEPT", text);
            Assert.True(result.Review.Required); Assert.Equal(StructureBasis.Unresolved, result.Summary.TableStructure);
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task Equal_row_widths_after_deleting_different_columns_still_require_review()
    {
        static string Cell(string text, bool deleted) => $"<w:tc><w:tcPr>{(deleted ? "<w:cellDel/>" : "")}</w:tcPr>{P(text)}</w:tc>";
        var source = Document("<w:tbl><w:tr>" + Cell("SECRET_HEADER", true) + Cell("HEADER_RIGHT", false) +
            "</w:tr><w:tr>" + Cell("VALUE_LEFT", false) + Cell("SECRET_VALUE", true) + "</w:tr></w:tbl>");
        try
        {
            var result = await new DocumentService(null, null, discoverPdfRasterizer: false).ExportAiPackageAsync(
                new(new(source, "unused.md", Revisions: "final", EnableOcr: false), Path.Combine(Path.GetDirectoryName(source)!, "final")));
            var text = string.Join("\n", result.Parts.Select(part => part.Markdown)).Replace("\\", "");
            Assert.Contains("HEADER_RIGHT", text); Assert.Contains("VALUE_LEFT", text); Assert.DoesNotContain("SECRET", text);
            Assert.DoesNotContain("| HEADER_RIGHT |", text); Assert.True(result.Review.Required);
        }
        finally { Cleanup(source); }
    }

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
