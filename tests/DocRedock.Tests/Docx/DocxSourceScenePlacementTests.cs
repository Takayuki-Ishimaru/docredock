using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Render;

namespace DocRedock.Tests.Docx;

/// <summary>Per-instance alternative text, floating-frame placement and frame order of Word source scenes.</summary>
public sealed class DocxSourceScenePlacementTests : IDisposable
{
    private const string PictureUri = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private const string ShapeUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    private const string GroupUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup";
    private readonly string root = Directory.CreateTempSubdirectory("docredock-docx-scene-placement-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);
    public void Dispose() => Directory.Delete(root, true);

    [Fact(Timeout = 30000)]
    public async Task A_cropped_picture_keeps_the_alt_text_of_its_own_frame_when_two_frames_share_one_image_part()
    {
        // Word writes the alternative text on wp:docPr; pic:cNvPr keeps a generated name or a stale file name.
        var source = WriteDocx("shared-image-alt", "<w:p>" +
            Frame(false, 23, "INLINE CROP ALT", PictureUri, Picture(3, "", "<a:srcRect l='33333' r='33333'/>")) +
            Frame(true, 24, "ANCHOR CROP ALT", PictureUri, Picture(4, "file.png", "<a:srcRect l='-20000' t='-10000'/>"), x: 20) + "</w:p>");
        var output = await Export(source);
        var pictures = Scenes(output.Graph).SelectMany(scene => scene.Objects).Where(o => o.Kind == "picture").ToArray();
        Assert.Equal(["INLINE CROP ALT", "ANCHOR CROP ALT"], pictures.Select(p => p.Text).ToArray());
        var alts = Regex.Matches(output.Html, "<img[^>]*?alt=\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(["INLINE CROP ALT", "ANCHOR CROP ALT"], alts);
        Assert.Contains("alt=\"INLINE CROP ALT\"", output.Markdown, StringComparison.Ordinal);
        Assert.Contains("alt=\"ANCHOR CROP ALT\"", output.Markdown, StringComparison.Ordinal);
    }

    [Fact(Timeout = 30000)]
    public async Task Grouped_pictures_keep_their_own_alt_text_instead_of_sharing_the_group_frame_text()
    {
        var source = WriteDocx("grouped-picture-alt", "<w:p>" + Frame(true, 31, "GROUP ALT", GroupUri,
            "<wpg:wgp><wpg:cNvGrpSpPr/><wpg:grpSpPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='1905000' cy='476250'/><a:chOff x='0' y='0'/><a:chExt cx='1905000' cy='476250'/></a:xfrm></wpg:grpSpPr>" +
            Picture(1, "LEFT ALT") + Picture(2, "RIGHT ALT", x: 100) + "</wpg:wgp>", 200) + "</w:p>");
        var output = await Export(source);
        var images = output.Graph.Nodes.Where(n => n.Kind == NodeKind.Image).Select(n => ((ReferenceNodeContent)n.Content).AltText ?? "").ToArray();
        Assert.Equal(["LEFT ALT", "RIGHT ALT"], images.Order().ToArray());
        var pictures = Scenes(output.Graph).SelectMany(scene => scene.Objects).Where(o => o.Kind == "picture").ToArray();
        Assert.Equal(["LEFT ALT", "RIGHT ALT"], pictures.Select(p => p.Text).ToArray());
        Assert.Contains("alt=\"LEFT ALT\"", output.Markdown, StringComparison.Ordinal);
        Assert.Contains("alt=\"RIGHT ALT\"", output.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("GROUP ALT", output.Markdown, StringComparison.Ordinal);
    }

    [Fact(Timeout = 30000)]
    public async Task A_floating_frame_far_down_its_page_does_not_add_an_empty_band_above_the_shape()
    {
        var source = WriteDocx("page-anchored", "<w:p><w:r><w:t>Middle</w:t></w:r>" +
            Frame(true, 42, null, ShapeUri, TextBox("FAR BOX", 100, 40), 100, 40, 500, 800, "page") + "</w:p>" +
            "<w:p>" + Frame(true, 43, null, ShapeUri, TextBox("ABOVE BOX", 100, 40), 100, 40, -60, -30) + "</w:p>");
        var output = await Export(source);
        var scenes = Scenes(output.Graph);
        var far = Assert.Single(scenes, scene => scene.Objects.Any(o => o.Text == "FAR BOX"));
        Assert.Equal("anchor:page/page", far.CoordinateSpace);
        Assert.Equal(40, far.Height, 6);
        Assert.Equal(800, far.OriginY, 6);
        // The object keeps its source offset; only the viewport follows it.
        Assert.Equal(800, Assert.Single(far.Objects).Transform.F, 6);
        Assert.Equal(600, far.Width, 6);
        var above = Assert.Single(scenes, scene => scene.Objects.Any(o => o.Text == "ABOVE BOX"));
        Assert.Equal(-30, above.OriginY, 6);
        Assert.Equal(-60, above.OriginX, 6);
        Assert.Equal(40, above.Height, 6);
        // Horizontally the frame still spans from the shape to its column origin.
        Assert.Equal(160, above.Width, 6);
        Assert.Contains("data-source-origin-y=\"800\"", output.Html, StringComparison.Ordinal);
        Assert.Contains("translate(-0px,-800px)", output.Html.Replace("translate(0px", "translate(-0px", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact(Timeout = 30000)]
    public async Task Several_frames_of_one_paragraph_keep_their_source_order_after_the_paragraph_text()
    {
        var source = WriteDocx("frame-order", "<w:p><w:r><w:t>Caption text</w:t></w:r>" +
            Frame(false, 51, "FIRST", PictureUri, Picture(1, "", "<a:srcRect l='10000'/>")) +
            Frame(false, 52, "SECOND", PictureUri, Picture(2, "", "<a:srcRect r='10000'/>")) +
            Frame(false, 53, "THIRD", PictureUri, Picture(3, "", "<a:srcRect t='10000'/>")) + "</w:p>");
        var output = await Export(source);
        var frames = Regex.Matches(output.Html, "data-source-scene=\"[^\"]*:drawing:(\\d+)\"").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(["0", "1", "2"], frames);
        Assert.True(output.Html.IndexOf("Caption text", StringComparison.Ordinal) < output.Html.IndexOf("data-source-scene=", StringComparison.Ordinal));
    }

    private static string Picture(int id, string ownDescription, string crop = "", int x = 0) =>
        $"<pic:pic><pic:nvPicPr><pic:cNvPr id='{id}' name='Picture {id}'{(ownDescription.Length > 0 ? $" descr='{ownDescription}'" : "")}/><pic:cNvPicPr/></pic:nvPicPr>" +
        $"<pic:blipFill><a:blip r:embed='rImage'/>{crop}<a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
        $"<pic:spPr><a:xfrm><a:off x='{x * 9525}' y='0'/><a:ext cx='952500' cy='476250'/></a:xfrm><a:prstGeom prst='rect'/></pic:spPr></pic:pic>";

    private static string TextBox(string text, int width, int height) =>
        $"<wps:wsp><wps:cNvSpPr txBox='1'/><wps:spPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='{width * 9525}' cy='{height * 9525}'/></a:xfrm><a:prstGeom prst='rect'><a:avLst/></a:prstGeom></wps:spPr>" +
        $"<wps:txbx><w:txbxContent><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:txbxContent></wps:txbx><wps:bodyPr/></wps:wsp>";

    private static string Frame(bool anchor, int id, string? description, string uri, string content,
        int width = 100, int height = 50, int x = 0, int y = 0, string relativeFrom = "column")
    {
        var properties = $"<wp:docPr id='{id}' name='Frame {id}'{(description is null ? "" : $" descr='{description}'")}/><wp:cNvGraphicFramePr/>" +
            $"<a:graphic><a:graphicData uri='{uri}'>{content}</a:graphicData></a:graphic>";
        return "<w:r><w:drawing>" + (anchor
            ? "<wp:anchor distT='0' distB='0' distL='0' distR='0' simplePos='0' relativeHeight='0' behindDoc='0' locked='0' layoutInCell='1' allowOverlap='1'><wp:simplePos x='0' y='0'/>" +
              $"<wp:positionH relativeFrom='{relativeFrom}'><wp:posOffset>{x * 9525}</wp:posOffset></wp:positionH>" +
              $"<wp:positionV relativeFrom='{(relativeFrom == "page" ? "page" : "paragraph")}'><wp:posOffset>{y * 9525}</wp:posOffset></wp:positionV>" +
              $"<wp:extent cx='{width * 9525}' cy='{height * 9525}'/><wp:wrapNone/>{properties}</wp:anchor>"
            : $"<wp:inline><wp:extent cx='{width * 9525}' cy='{height * 9525}'/>{properties}</wp:inline>") + "</w:drawing></w:r>";
    }

    private static IReadOnlyList<SourceDrawingScene> Scenes(DocumentGraph graph)
    {
        Assert.NotNull(graph.FormatExtensions);
        var warnings = new List<string>();
        var scenes = SourceDrawingSceneJson.Read(graph.FormatExtensions[SourceDrawingSceneJson.ExtensionKey], warnings);
        Assert.Empty(warnings);
        return scenes.Values.SelectMany(items => items).ToArray();
    }

    private async Task<Exported> Export(string source)
    {
        var mdPath = Path.ChangeExtension(source, ".md");
        var htmlPath = Path.ChangeExtension(source, ".html");
        var md = await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, mdPath, ContentPolicy: "visible", Language: "en"));
        await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, htmlPath,
            ContentPolicy: "visible", Language: "en", OutputFormat: ReadableOutputFormat.Html));
        return new(md.Graph, await File.ReadAllTextAsync(mdPath), await File.ReadAllTextAsync(htmlPath));
    }

    private string WriteDocx(string name, string body)
    {
        var path = Path.Combine(root, name + ".docx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Write(string part, string xml)
        {
            using var writer = new StreamWriter(zip.CreateEntry(part).Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }
        Write("[Content_Types].xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='xml' ContentType='application/xml'/><Default Extension='png' ContentType='image/png'/><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
        Write("_rels/.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rDocument' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>");
        Write("word/document.xml", "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships' xmlns:wp='http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main' xmlns:wps='http://schemas.microsoft.com/office/word/2010/wordprocessingShape' xmlns:wpg='http://schemas.microsoft.com/office/word/2010/wordprocessingGroup' xmlns:pic='http://schemas.openxmlformats.org/drawingml/2006/picture'><w:body>" + body + "<w:sectPr/></w:body></w:document>");
        Write("word/_rels/document.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rImage' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/image' Target='media/image1.png'/></Relationships>");
        using var image = zip.CreateEntry("word/media/image1.png").Open();
        image.Write(PngRasterImage.Encode(4, 2, [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0]));
        return path;
    }

    private sealed record Exported(DocumentGraph Graph, string Markdown, string Html);
}
