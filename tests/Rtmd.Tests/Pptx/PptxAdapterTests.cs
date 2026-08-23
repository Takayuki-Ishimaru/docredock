using System.IO.Compression;
using System.Text;
using Rtmd.Api;
using Rtmd.Formats.OpenXml.Pptx;
using Rtmd.Markdown;

namespace Rtmd.Tests.Pptx;

public sealed class PptxAdapterTests
{
    [Fact]
    public void ExtractsShapeTableImageAndNotes()
    {
        var result = new PptxAdapter().Extract(new MemoryStream(CreatePackage()));
        var slide = Assert.Single(result.Slides);
        Assert.Contains(slide.Shapes, shape => shape.ShapeId == "2" && shape.Text == "Hello");
        Assert.Equal("title", slide.Shapes.Single(shape => shape.ShapeId == "2").Role);
        Assert.Contains(slide.Shapes, shape => shape.IsTable);
        Assert.Contains(slide.Shapes, shape => shape.ImageRelationshipIds.Contains("rIdImage"));
        var image = Assert.Single(result.Graph.Nodes, node => node.Kind == Rtmd.Core.Documents.NodeKind.Image);
        Assert.Equal("ppt/media/image1.png", Assert.IsType<Rtmd.Core.Documents.ReferenceNodeContent>(image.Content).Reference);
        Assert.Equal("Speaker note", slide.NotesText);
    }

    [Fact]
    public void UnchangedRestoreIsByteIdenticalAndTextPatchLeavesUnknownPart()
    {
        var original = CreatePackage(); var adapter = new PptxAdapter();
        var empty = adapter.CreatePatchPlan(Array.Empty<PptxShapeTextEdit>());
        Assert.Equal(original, adapter.Restore(new MemoryStream(original), empty).Bytes);
        var plan = adapter.CreatePatchPlan([new PptxShapeTextEdit("slide1", "2", "Changed")]);
        var restored = adapter.Restore(new MemoryStream(original), plan).Bytes;
        var before = Entries(original); var after = Entries(restored);
        Assert.NotEqual(Convert.ToBase64String(before["ppt/slides/slide1.xml"]), Convert.ToBase64String(after["ppt/slides/slide1.xml"]));
        Assert.Equal(before["custom/unknown.bin"], after["custom/unknown.bin"]);
        Assert.Equal(before["ppt/theme/theme1.xml"], after["ppt/theme/theme1.xml"]);
        Assert.Contains("ppt/slides/slide1.xml", plan.DirtyParts);
    }

    [Fact]
    public void TextPatchPreservesRunFontsAndShapeLayout()
    {
        var original = CreatePackage();
        var adapter = new PptxAdapter();
        var restored = adapter.Restore(new MemoryStream(original),
            adapter.CreatePatchPlan([new PptxShapeTextEdit("slide1", "2", "変更後の表題")])).Bytes;
        var xml = Encoding.UTF8.GetString(Entries(restored)["ppt/slides/slide1.xml"]);

        Assert.Contains("typeface=\"Yu Mincho\"", xml);
        Assert.Contains("typeface=\"游明朝\"", xml);
        Assert.Contains("typeface=\"BIZ UDPGothic\"", xml);
        Assert.Contains("sz=\"2800\"", xml);
        Assert.Contains("<a:off x=\"640000\" y=\"320000\"", xml);
        Assert.Contains("<a:ext cx=\"10800000\" cy=\"1000000\"", xml);
        Assert.Equal(Entries(original)["ppt/theme/theme1.xml"], Entries(restored)["ppt/theme/theme1.xml"]);
    }

    [Fact]
    public void ExtractsPlaceholderRolesAndRestoresMultipleBodyParagraphs()
    {
        var original = CreatePackage();
        var adapter = new PptxAdapter();
        var slide = Assert.Single(adapter.Extract(new MemoryStream(original)).Slides);
        var body = Assert.Single(slide.Shapes, shape => shape.ShapeId == "5");

        Assert.Equal("body", body.Role);
        Assert.Equal("One\nTwo\nThree", body.Text);
        Assert.Equal(["One", "Two", "Three"], body.Paragraphs);

        var plan = adapter.CreatePatchPlan([new PptxShapeTextEdit("slide1", "5", "Alpha\nBeta\nGamma\nDelta")]);
        var restored = adapter.Restore(new MemoryStream(original), plan).Bytes;
        var restoredBody = Assert.Single(Assert.Single(adapter.Extract(new MemoryStream(restored)).Slides).Shapes, shape => shape.ShapeId == "5");
        var xml = Encoding.UTF8.GetString(Entries(restored)["ppt/slides/slide1.xml"]);
        Assert.Equal(["Alpha", "Beta", "Gamma", "Delta"], restoredBody.Paragraphs);
        Assert.Contains("<a:t>Alpha</a:t>", xml);
        Assert.Contains("<a:t>Delta</a:t>", xml);
    }

    [Fact]
    public void TitleAndBodyCompletePptxToMarkdownEditToPptxRoundTrip()
    {
        var original = CreatePackage();
        var adapter = new PptxAdapter();
        var extraction = adapter.Extract(new MemoryStream(original));
        var markdown = new RtmdMarkdownSerializer().Serialize(extraction.Graph).Markdown;

        Assert.Contains("role=title", markdown);
        Assert.Contains("### Hello", markdown);
        Assert.Contains("role=body", markdown);
        Assert.Contains("- One\n- Two\n- Three", markdown);
        var edit = new MarkdownGraphEditor().Apply(extraction.Graph, markdown
            .Replace("### Hello", "### 実行計画", StringComparison.Ordinal)
            .Replace("- Two", "- 第二項", StringComparison.Ordinal));
        var plan = adapter.CreatePatchPlan(extraction.Graph, edit.EditedGraph);
        var restored = adapter.Restore(new MemoryStream(original), plan).Bytes;
        var reexport = adapter.Extract(new MemoryStream(restored));
        var slide = Assert.Single(reexport.Slides);

        Assert.True(edit.IsValid, string.Join(" | ", edit.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        Assert.Equal("実行計画", slide.Shapes.Single(shape => shape.Role == "title").Text);
        Assert.Equal(["One", "第二項", "Three"], slide.Shapes.Single(shape => shape.Role == "body").Paragraphs);
        Assert.Equal(Entries(original)["custom/unknown.bin"], Entries(restored)["custom/unknown.bin"]);
    }

    [Fact]
    public void Extracts_bullet_level_and_run_emphasis_metadata()
    {
        var slide = Assert.Single(new PptxAdapter().Extract(new MemoryStream(CreatePackage(includeRichShape: true))).Slides);
        var shape = Assert.Single(slide.Shapes, item => item.ShapeId == "7");

        var paragraph = Assert.Single(shape.ParagraphDetails!);
        Assert.True(paragraph.IsBullet);
        Assert.Equal(1, paragraph.Level);
        Assert.True(Assert.Single(paragraph.Runs!).Bold);
        var node = Assert.Single(new PptxAdapter().Extract(new MemoryStream(CreatePackage(includeRichShape: true))).Graph.Nodes,
            item => item.Source?.Locators.Any(locator => locator.Value == "7") == true);
        Assert.IsType<Rtmd.Core.Documents.RichTextNodeContent>(node.Content);
    }

    private static byte[] CreatePackage(bool includeRichShape = false)
    {
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
            ["ppt/presentation.xml"] = "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\" /></p:sldIdLst></p:presentation>",
            ["ppt/_rels/presentation.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"slide\" Target=\"slides/slide1.xml\" /></Relationships>",
            ["ppt/slides/slide1.xml"] = "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:cSld><p:spTree><p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title\" /><p:nvPr><p:ph type=\"title\" /></p:nvPr></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"640000\" y=\"320000\" /><a:ext cx=\"10800000\" cy=\"1000000\" /></a:xfrm></p:spPr><p:txBody><a:bodyPr lIns=\"91440\" rIns=\"91440\" /><a:p><a:r><a:rPr lang=\"ja-JP\" sz=\"2800\"><a:latin typeface=\"Yu Mincho\" /><a:ea typeface=\"游明朝\" /></a:rPr><a:t>He</a:t></a:r><a:r><a:rPr lang=\"ja-JP\" sz=\"2600\"><a:latin typeface=\"BIZ UDPGothic\" /><a:ea typeface=\"BIZ UDPゴシック\" /></a:rPr><a:t>llo</a:t></a:r></a:p></p:txBody></p:sp><p:sp><p:nvSpPr><p:cNvPr id=\"5\" name=\"Body\" /><p:nvPr><p:ph type=\"body\" /></p:nvPr></p:nvSpPr><p:txBody><a:bodyPr /><a:lstStyle /><a:p><a:r><a:t>One</a:t></a:r></a:p><a:p><a:r><a:t>Two</a:t></a:r></a:p><a:p><a:r><a:t>Three</a:t></a:r></a:p></p:txBody></p:sp><p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"Table\" /></p:nvGraphicFramePr><a:graphic><a:graphicData><a:tbl><a:tr><a:tc><a:txBody><a:p><a:r><a:t>Cell</a:t></a:r></a:p></a:txBody></a:tc></a:tr></a:tbl></a:graphicData></a:graphic></p:graphicFrame><p:pic><p:nvPicPr><p:cNvPr id=\"4\" name=\"Image\" /></p:nvPicPr><p:blipFill><a:blip r:embed=\"rIdImage\" /></p:blipFill></p:pic></p:spTree></p:cSld></p:sld>",
            ["ppt/slides/_rels/slide1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdImage\" Type=\"image\" Target=\"../media/image1.png\" /><Relationship Id=\"rIdNotes\" Type=\"notesSlide\" Target=\"../notesSlides/notesSlide1.xml\" /></Relationships>",
            ["ppt/notesSlides/notesSlide1.xml"] = "<p:notes xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:t>Speaker note</a:t></p:notes>",
            ["ppt/theme/theme1.xml"] = "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:themeElements><a:fontScheme name=\"Corporate\"><a:majorFont><a:latin typeface=\"Aptos Display\" /><a:ea typeface=\"Yu Gothic\" /></a:majorFont></a:fontScheme></a:themeElements></a:theme>",
            ["ppt/media/image1.png"] = "image",
            ["custom/unknown.bin"] = "untouched"
        };
        if (includeRichShape)
        {
            const string richShape = "<p:sp><p:nvSpPr><p:cNvPr id=\"7\" name=\"Bullets\" /><p:nvPr><p:ph type=\"body\" /></p:nvPr></p:nvSpPr><p:txBody><a:bodyPr /><a:p><a:pPr lvl=\"1\"><a:buChar char=\"•\" /></a:pPr><a:r><a:rPr b=\"1\" sz=\"2400\" /><a:t>Emphasized</a:t></a:r></a:p></p:txBody></p:sp>";
            parts["ppt/slides/slide1.xml"] = parts["ppt/slides/slide1.xml"].Replace("</p:spTree>", richShape + "</p:spTree>", StringComparison.Ordinal);
        }
        using var output = new MemoryStream(); using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) foreach (var part in parts) { using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), Encoding.UTF8); writer.Write(part.Value); }
        return output.ToArray();
    }
    private static Dictionary<string, byte[]> Entries(byte[] bytes)
    {
        using var input = new MemoryStream(bytes); using var zip = new ZipArchive(input); var result = new Dictionary<string, byte[]>(); foreach (var entry in zip.Entries) using (var source = entry.Open()) using (var output = new MemoryStream()) { source.CopyTo(output); result[entry.FullName] = output.ToArray(); }
        return result;
    }

    private static int Count(string value, string needle)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0; index += needle.Length) count++;
        return count;
    }
}
