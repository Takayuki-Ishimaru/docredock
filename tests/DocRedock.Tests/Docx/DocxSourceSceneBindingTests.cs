using System.IO.Compression;
using System.Net;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Api;
using DocRedock.Core.Documents;

namespace DocRedock.Tests.Docx;

/// <summary>Source instance binding, captions and content policy through the public export API.</summary>
public sealed class DocxSourceSceneBindingTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-docx-source-binding-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);
    public void Dispose() => Directory.Delete(root, true);

    [Fact(Timeout = 30000)]
    public async Task Repeated_caption_boxes_bind_by_source_instance_and_keep_their_own_style_geometry_and_link()
    {
        // The boxes are siblings in one paragraph and intentionally have identical captions.
        // Neither WPS has an id/cNvPr: the source identity is its ancestor wp:docPr.
        var source = WriteDocx("repeated-caption", "<w:p>" +
            Box(101, "SAME_CAPTION", 12, 80, "CC0000", "rFirst") +
            Box(102, "SAME_CAPTION", 140, 100, "0000CC", "rSecond") + "</w:p>");
        var output = await Export(source, "visible");
        var scenes = Scenes(output.Graph);
        var boxes = scenes.SelectMany(s => s.Objects).Where(o => o.Kind == "shape").ToArray();
        Assert.Equal(2, boxes.Length);
        Assert.Equal(2, boxes.Select(o => o.SourceNodeId).Distinct().Count());
        foreach (var (sourceId, index, width, color, target) in new[]
        {
            ("101", "0", 80d, "#CC0000", "https://example.invalid/first"),
            ("102", "1", 100d, "#0000CC", "https://example.invalid/second"),
        })
        {
            var box = Assert.Single(boxes, item => ShapeId(item) == sourceId);
            Assert.Equal("SAME_CAPTION", box.Text);
            Assert.Equal(width, box.Width, precision: 5);
            Assert.Equal(color, box.FillPaint?.Color);
            Assert.False(box.Hidden);
            Assert.False(string.IsNullOrWhiteSpace(box.SourceNodeId));
            var owner = output.Graph.FindNode(box.SourceNodeId!);
            Assert.NotNull(owner);
            Assert.Equal(NodeKind.TextBox, owner.Kind);
            Assert.Contains(owner.Source!.Locators, locator => locator.Kind == "textbox" && locator.Value == index);
            Assert.Contains(owner.Source.Locators, locator => locator.Kind == "shape_id" && locator.Value == sourceId);
            var run = Assert.Single(box.Paragraphs.SelectMany(p => p.Runs));
            Assert.Equal("SAME_CAPTION", run.Text);
            Assert.Equal(target, run.LinkTarget);
            Assert.Contains(target, WebUtility.HtmlDecode(output.Html), StringComparison.Ordinal);
            Assert.Contains(target, output.Markdown, StringComparison.Ordinal);
        }
        // Preserve the horizontal relation rather than binding both frames to the first caption.
        var left = Assert.Single(boxes, box => ShapeId(box) == "101");
        var right = Assert.Single(boxes, box => ShapeId(box) == "102");
        Assert.True(left.Transform.E < right.Transform.E);
        Assert.All(scenes.Where(scene => scene.AnchorNodeId is not null), scene =>
            Assert.NotNull(output.Graph.FindNode(scene.AnchorNodeId!)));
    }

    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hidden_outer_run_or_nonvisual_property_is_filtered_from_visible_and_sanitized_exports(bool nonvisual)
    {
        var source = WriteDocx("hidden-caption", "<w:p>" +
            Box(101, "VISIBLE_LABEL", 12, 80, "CC0000", "rFirst") +
            Box(103, "PRIVATE-HIDDEN-SOURCE-SHAPE", 140, 100, "0000CC", null, hidden: !nonvisual, pageAnchor: true, nonvisualHidden: nonvisual) + "</w:p>");
        foreach (var policy in new[] { "visible", "sanitized", "complete" })
        {
            var output = await Export(source, policy);
            var privateBox = Assert.Single(Scenes(output.Graph).SelectMany(s => s.Objects), box => ShapeId(box) == "103");
            Assert.True(privateBox.Hidden);
            Assert.Equal("PRIVATE-HIDDEN-SOURCE-SHAPE", privateBox.Text);
            Assert.False(string.IsNullOrWhiteSpace(privateBox.SourceNodeId));
            Assert.Equal(ContentLayer.Hidden, output.Graph.FindNode(privateBox.SourceNodeId!)?.Layer);
            Assert.Contains("VISIBLE_LABEL", VisibleText(output.Html), StringComparison.Ordinal);
            Assert.Contains("VISIBLE_LABEL", Markdig.Markdown.ToPlainText(output.Markdown), StringComparison.Ordinal);
            if (policy == "complete")
            {
                Assert.Contains("PRIVATE-HIDDEN-SOURCE-SHAPE", VisibleText(output.Html), StringComparison.Ordinal);
                Assert.Contains("PRIVATE-HIDDEN-SOURCE-SHAPE", output.Markdown, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain("PRIVATE-HIDDEN-SOURCE-SHAPE", output.Html, StringComparison.Ordinal);
                Assert.DoesNotContain("PRIVATE-HIDDEN-SOURCE-SHAPE", output.Markdown, StringComparison.Ordinal);
            }
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Hidden_picture_nonvisual_property_is_filtered_even_without_a_shape_scene()
    {
        var source = WriteDocx("hidden-picture", """
            <w:p><w:r><w:drawing><wp:inline><wp:extent cx="952500" cy="476250"/>
            <wp:docPr id="301" name="picture" descr="PRIVATE-HIDDEN-PICTURE"/>
            <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
            <pic:pic xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">
            <pic:nvPicPr><pic:cNvPr id="1" name="picture" hidden="1"/><pic:cNvPicPr/></pic:nvPicPr>
            <pic:blipFill><a:blip r:embed="rPicture"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
            <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="952500" cy="476250"/></a:xfrm><a:prstGeom prst="rect"/></pic:spPr>
            </pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
            """);
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Update))
        {
            zip.GetEntry("word/_rels/document.xml.rels")!.Delete();
            using (var writer = new StreamWriter(zip.CreateEntry("word/_rels/document.xml.rels").Open()))
                writer.Write("<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rPicture' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/image' Target='media/picture.png'/></Relationships>");
            using var image = zip.CreateEntry("word/media/picture.png").Open();
            image.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lRkAAAAASUVORK5CYII="));
        }
        foreach (var policy in new[] { "visible", "sanitized", "complete" })
        {
            var output = await Export(source, policy);
            var image = Assert.Single(output.Graph.Nodes, n => n.Kind == NodeKind.Image);
            Assert.Equal(ContentLayer.Hidden, image.Layer);
            Assert.Equal(policy == "complete", output.Html.Contains("data:image/png;base64,", StringComparison.Ordinal));
            Assert.Equal(policy == "complete", output.Markdown.Contains(Assert.IsType<ReferenceNodeContent>(image.Content).Reference, StringComparison.Ordinal));
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Canvas_child_WPS_id_binds_to_its_own_textbox_instead_of_the_outer_frame_id()
    {
        string Child(int id, string text, int x)
        {
            var outer = Box(id, text, x, 80, "EEEEEE", null);
            var start = outer.IndexOf("<wps:wsp>", StringComparison.Ordinal) + "<wps:wsp>".Length;
            var end = outer.IndexOf("</wps:wsp>", StringComparison.Ordinal);
            return $"<wps:wsp><wps:cNvPr id='{id}' name='Canvas{id}'/>" + outer[start..end] + "</wps:wsp>";
        }
        var source = WriteDocx("canvas-ids", "<w:p><w:r><w:drawing><wp:inline><wp:extent cx='2286000' cy='952500'/>" +
            "<wp:docPr id='900' name='OuterCanvas'/><a:graphic><a:graphicData uri='http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas'>" +
            "<wpc:wpc xmlns:wpc='http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas'>" +
            Child(101, "CANVAS_FIRST", 0) + Child(102, "CANVAS_SECOND", 120) +
            "</wpc:wpc></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>");
        var output = await Export(source, "visible");
        var shapes = Scenes(output.Graph).SelectMany(scene => scene.Objects).Where(o => o.Kind == "shape").ToArray();
        Assert.Equal(2, shapes.Length);
        Assert.All(shapes, shape =>
        {
            Assert.NotNull(shape.SourceNodeId);
            var node = output.Graph.FindNode(shape.SourceNodeId!);
            Assert.NotNull(node);
            Assert.Equal(NodeKind.TextBox, node.Kind);
            Assert.Contains(node.Source!.Locators, locator => locator.Kind == "shape_id" && locator.Value == ShapeId(shape));
        });
        var projection = DocRedock.Markdown.ReadableProjectionBuilder.Build(output.Graph).Document;
        Assert.DoesNotContain(projection.Blocks.OfType<ReadableParagraph>(), p => p.Content.PlainText.Contains("CANVAS_", StringComparison.Ordinal));
    }

    private static IReadOnlyList<SourceDrawingScene> Scenes(DocumentGraph graph)
    {
        Assert.NotNull(graph.FormatExtensions);
        Assert.True(graph.FormatExtensions.TryGetValue(SourceDrawingSceneJson.ExtensionKey, out var value),
            "An ordinary source WPS textbox must produce source_drawing_scenes after adapter wiring.");
        var warnings = new List<string>();
        var byPartition = SourceDrawingSceneJson.Read(value, warnings);
        Assert.Empty(warnings);
        Assert.NotEmpty(byPartition);
        Assert.All(byPartition.Keys, key => Assert.Contains(graph.Partitions, partition => partition.Id == key));
        return byPartition.Values.SelectMany(scenes => scenes).ToArray();
    }
    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_standalone_picture_keeps_crop_rotation_and_flip_without_needing_a_sibling_shape(bool hidden)
    {
        var source = WriteDocx("transformed-picture-" + hidden, """
            <w:p><w:r><w:drawing><wp:inline><wp:extent cx='952500' cy='476250'/><wp:docPr id='301' name='CropRotate'/>
            <a:graphic><a:graphicData uri='http://schemas.openxmlformats.org/drawingml/2006/picture'>
            <pic:pic xmlns:pic='http://schemas.openxmlformats.org/drawingml/2006/picture'>
            <pic:nvPicPr><pic:cNvPr id='1' name='CropRotate' HIDDEN/><pic:cNvPicPr/></pic:nvPicPr>
            <pic:blipFill><a:blip r:embed='rPicture'/><a:srcRect l='25000' r='12500' t='10000' b='20000'/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
            <pic:spPr><a:xfrm rot='5400000' flipH='1'><a:off x='0' y='0'/><a:ext cx='952500' cy='476250'/></a:xfrm><a:prstGeom prst='rect'/></pic:spPr>
            </pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
            """.Replace("HIDDEN", hidden ? "hidden='1'" : "", StringComparison.Ordinal));
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Update))
        {
            zip.GetEntry("word/_rels/document.xml.rels")!.Delete();
            using (var writer = new StreamWriter(zip.CreateEntry("word/_rels/document.xml.rels").Open()))
                writer.Write("<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rPicture' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/image' Target='media/picture.png'/></Relationships>");
            using var image = zip.CreateEntry("word/media/picture.png").Open();
            image.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lRkAAAAASUVORK5CYII="));
        }
        foreach (var policy in new[] { "visible", "sanitized", "complete" })
        {
            var output = await Export(source, policy);
            var scene = Assert.Single(Scenes(output.Graph));
            var picture = Assert.Single(scene.Objects, o => o.Kind == "picture");
            Assert.Equal(hidden, picture.Hidden);
            Assert.Equal(.25, picture.CropLeft, 6);
            Assert.Equal(.125, picture.CropRight, 6);
            Assert.Equal(.1, picture.CropTop, 6);
            Assert.Equal(.2, picture.CropBottom, 6);
            Assert.Equal(-1, picture.Transform.B, 6);
            Assert.Equal(-1, picture.Transform.C, 6);
            Assert.Equal(-25, scene.OriginY, 6);
            Assert.Equal(100, scene.Height, 6);
            Assert.Equal(-25, new[] { picture.Transform.Point(0, 0).Y, picture.Transform.Point(picture.Width, picture.Height).Y }.Min(), 6);
            Assert.False(string.IsNullOrWhiteSpace(picture.SourceNodeId));
            var present = !hidden || policy == "complete";
            Assert.Equal(present, output.Html.Contains("class=\"source-picture-clip\"", StringComparison.Ordinal));
            Assert.Equal(present, output.Html.Contains("data:image/png;base64,", StringComparison.Ordinal));
            Assert.Equal(present, output.Html.Contains("data-source-origin-y=\"-25\"", StringComparison.Ordinal));
            Assert.DoesNotContain("source-caption-transform", output.Html);
        }
    }

    private static string? ShapeId(WorkbookDrawingObject box) =>
        box.Source?.Locators.FirstOrDefault(locator => locator.Kind == "shape_id")?.Value;

    private async Task<Exported> Export(string source, string policy)
    {
        var basename = Path.GetFileNameWithoutExtension(source) + "-" + policy;
        var mdPath = Path.Combine(root, basename + ".md");
        var htmlPath = Path.Combine(root, basename + ".html");
        var md = await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, mdPath,
            ContentPolicy: policy, Language: "en"));
        await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, htmlPath,
            ContentPolicy: policy, Language: "en", OutputFormat: ReadableOutputFormat.Html));
        return new(md.Graph, await File.ReadAllTextAsync(mdPath), await File.ReadAllTextAsync(htmlPath));
    }

    private static string Box(int id, string text, int x, int width, string fill, string? link,
        bool hidden = false, bool pageAnchor = false, bool nonvisualHidden = false)
    {
        var frame = pageAnchor
            ? "<wp:anchor distT='0' distB='0' distL='0' distR='0' simplePos='0' relativeHeight='0' behindDoc='0' locked='0' layoutInCell='1' allowOverlap='1'>" +
              "<wp:simplePos x='0' y='0'/><wp:positionH relativeFrom='page'><wp:posOffset>0</wp:posOffset></wp:positionH>" +
              "<wp:positionV relativeFrom='page'><wp:posOffset>0</wp:posOffset></wp:positionV>" +
              $"<wp:extent cx='{width * 9525}' cy='381000'/><wp:wrapNone/>"
            : $"<wp:inline><wp:extent cx='{width * 9525}' cy='381000'/>";
        var caption = "<w:r><w:rPr><w:vanish w:val='0'/></w:rPr><w:t>" + SecurityElement.Escape(text) + "</w:t></w:r>";
        if (link is not null) caption = "<w:hyperlink r:id='" + link + "'>" + caption + "</w:hyperlink>";
        return "<w:r>" + (hidden ? "<w:rPr><w:vanish/></w:rPr>" : "") + "<w:drawing>" + frame +
            $"<wp:docPr id='{id}' name='SourceBox{id}' hidden='{(nonvisualHidden ? "1" : "0")}'/><wp:cNvGraphicFramePr/>" +
            "<a:graphic><a:graphicData uri='http://schemas.microsoft.com/office/word/2010/wordprocessingShape'><wps:wsp>" +
            $"<wps:cNvSpPr txBox='1'/><wps:spPr><a:xfrm><a:off x='{x * 9525}' y='0'/><a:ext cx='{width * 9525}' cy='381000'/></a:xfrm>" +
            "<a:prstGeom prst='rect'><a:avLst/></a:prstGeom><a:solidFill><a:srgbClr val='" + fill + "'/></a:solidFill></wps:spPr>" +
            "<wps:txbx><w:txbxContent><w:p>" + caption + "</w:p></w:txbxContent></wps:txbx><wps:bodyPr/>" +
            "</wps:wsp></a:graphicData></a:graphic>" + (pageAnchor ? "</wp:anchor>" : "</wp:inline>") + "</w:drawing></w:r>";
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
        Write("[Content_Types].xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='xml' ContentType='application/xml'/><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
        Write("_rels/.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rDocument' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>");
        Write("word/document.xml", "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships' xmlns:wp='http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main' xmlns:wps='http://schemas.microsoft.com/office/word/2010/wordprocessingShape'><w:body>" + body + "<w:sectPr/></w:body></w:document>");
        Write("word/_rels/document.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rFirst' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink' Target='https://example.invalid/first' TargetMode='External'/><Relationship Id='rSecond' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink' Target='https://example.invalid/second' TargetMode='External'/></Relationships>");
        return path;
    }
    private static string VisibleText(string html)
    {
        var body = Regex.Match(html, "<body\\b[^>]*>(.*)</body>", RegexOptions.Singleline).Groups[1].Value;
        body = Regex.Replace(body, "<(?:script|style)\\b[^>]*>.*?</(?:script|style)>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return WebUtility.HtmlDecode(Regex.Replace(body, "<[^>]+>", " "));
    }
    private sealed record Exported(DocumentGraph Graph, string Markdown, string Html);
}
