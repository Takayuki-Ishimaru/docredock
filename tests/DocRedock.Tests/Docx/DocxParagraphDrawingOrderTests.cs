using System.IO.Compression;
using System.Text;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Render;

namespace DocRedock.Tests.Docx;

/// <summary>Several drawings of one Word paragraph are read in document order, whatever their kinds and node ids.</summary>
public sealed partial class DocxParagraphDrawingOrderTests : IDisposable
{
    private const string PictureUri = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private const string ShapeUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    private readonly string root = Directory.CreateTempSubdirectory("docredock-docx-drawing-order-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);
    public void Dispose() => Directory.Delete(root, true);

    [Fact(Timeout = 30000)]
    public async Task Text_boxes_of_one_paragraph_are_read_in_document_order()
    {
        var names = new[] { "ALT TEXTBOX", "SECOND BOX", "THIRD BOX", "FOURTH BOX", "FIFTH BOX" };
        var output = await Export(WriteDocx("text-boxes", "<w:p><w:r><w:t>Host text</w:t></w:r>" +
            string.Concat(names.Select((name, index) => Frame(index % 2 == 1, 11 + index, null, ShapeUri, TextBox(name), x: index * 110))) + "</w:p>"));
        AssertDocumentOrder(output.Markdown, ["Host text", .. names]);
        AssertDocumentOrder(output.Html, ["Host text", .. names]);
    }

    [Fact(Timeout = 30000)]
    public async Task Inline_and_anchored_pictures_of_one_paragraph_are_read_in_document_order()
    {
        var names = new[] { "INLINE ALT", "ANCHOR ALT", "INLINE AGAIN", "ANCHOR AGAIN", "LAST INLINE" };
        var output = await Export(WriteDocx("pictures", "<w:p><w:r><w:t>Host text</w:t></w:r>" +
            string.Concat(names.Select((name, index) => Frame(index % 2 == 1, 21 + index, name, PictureUri, Picture(index + 1)))) + "</w:p>"));
        AssertDocumentOrder(output.Markdown, ["Host text", .. names]);
        AssertDocumentOrder(output.Html, ["Host text", .. names]);
    }

    [Fact(Timeout = 30000)]
    public async Task Pictures_and_text_boxes_of_one_paragraph_interleave_in_document_order()
    {
        string[] names = ["PICTURE ONE", "BOX BETWEEN", "PICTURE TWO", "BOX AFTER", "PICTURE THREE"];
        var output = await Export(WriteDocx("interleaved", "<w:p>" +
            Frame(false, 31, "PICTURE ONE", PictureUri, Picture(1)) +
            Frame(true, 32, null, ShapeUri, TextBox("BOX BETWEEN")) +
            Frame(false, 33, "PICTURE TWO", PictureUri, Picture(2)) +
            Frame(true, 34, null, ShapeUri, TextBox("BOX AFTER")) +
            Frame(false, 35, "PICTURE THREE", PictureUri, Picture(3)) + "</w:p>"));
        AssertDocumentOrder(output.Markdown, names);
        AssertDocumentOrder(output.Html, names);
    }

    [Fact(Timeout = 30000)]
    public async Task A_text_box_before_the_pictures_of_a_paragraph_without_text_is_read_first()
    {
        string[] names = ["BOX FIRST", "PICTURE AFTER", "PICTURE LAST"];
        var output = await Export(WriteDocx("box-first", "<w:p>" +
            Frame(true, 36, null, ShapeUri, TextBox("BOX FIRST")) +
            Frame(false, 37, "PICTURE AFTER", PictureUri, Picture(1)) +
            Frame(true, 38, "PICTURE LAST", PictureUri, Picture(2)) + "</w:p><w:p><w:r><w:t>Next paragraph</w:t></w:r></w:p>"));
        AssertDocumentOrder(output.Markdown, [.. names, "Next paragraph"]);
        AssertDocumentOrder(output.Html, [.. names, "Next paragraph"]);
    }

    [Fact(Timeout = 30000)]
    public async Task The_text_of_a_paragraph_comes_before_its_only_drawing_whatever_the_node_ids()
    {
        // Each paragraph has another id, so some of them sort after their drawing and some before it by id alone.
        var pictures = Enumerable.Range(0, 8).Select(index => ($"Picture host {index}", $"LONE PICTURE {index}")).ToArray();
        var boxes = Enumerable.Range(0, 8).Select(index => ($"Box host {index}", $"LONE BOX {index}")).ToArray();
        var output = await Export(WriteDocx("single-drawing",
            string.Concat(pictures.Select((item, index) => $"<w:p><w:r><w:t>{item.Item1}</w:t></w:r>" + Frame(false, 71 + index, item.Item2, PictureUri, Picture(index + 1)) + "</w:p>")) +
            string.Concat(boxes.Select((item, index) => $"<w:p><w:r><w:t>{item.Item1}</w:t></w:r>" + Frame(true, 81 + index, null, ShapeUri, TextBox(item.Item2)) + "</w:p>")) +
            "<w:tbl><w:tr>" + string.Concat(Enumerable.Range(0, 4).Select(index => $"<w:tc><w:p><w:r><w:t>Cell host {index}</w:t></w:r>" +
                Frame(false, 91 + index, $"CELL PICTURE {index}", PictureUri, Picture(index + 20)) + "</w:p></w:tc>")) + "</w:tr></w:tbl>"));
        string[] expected = [.. pictures.SelectMany(item => new[] { item.Item1, item.Item2 }), .. boxes.SelectMany(item => new[] { item.Item1, item.Item2 })];
        AssertDocumentOrder(output.Markdown, expected);
        AssertDocumentOrder(output.Html, expected);
        for (var index = 0; index < 4; index++)
        {
            AssertDocumentOrder(output.Markdown, [$"Cell host {index}", $"CELL PICTURE {index}"]);
            AssertDocumentOrder(output.Html, [$"Cell host {index}", $"CELL PICTURE {index}"]);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task A_vml_picture_among_drawingml_pictures_keeps_its_document_position()
    {
        string[] names = ["DRAWINGML ONE", "VML MIDDLE", "DRAWINGML TWO", "VML LATER", "DRAWINGML THREE"];
        var output = await Export(WriteDocx("vml", "<w:p>" +
            Frame(false, 41, "DRAWINGML ONE", PictureUri, Picture(1)) + VmlPicture("VML MIDDLE") +
            Frame(true, 42, "DRAWINGML TWO", PictureUri, Picture(2)) + VmlPicture("VML LATER") +
            Frame(false, 43, "DRAWINGML THREE", PictureUri, Picture(3)) + "</w:p>"));
        AssertDocumentOrder(output.Markdown, names);
        AssertDocumentOrder(output.Html, names);
    }

    [Fact(Timeout = 30000)]
    public async Task Pictures_of_one_table_cell_are_read_in_document_order()
    {
        var names = new[] { "CELL ONE", "CELL TWO", "CELL THREE", "CELL FOUR", "CELL FIVE" };
        var output = await Export(WriteDocx("cell", "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Head</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Pictures</w:t></w:r></w:p></w:tc></w:tr>" +
            "<w:tr><w:tc><w:p><w:r><w:t>Row</w:t></w:r></w:p></w:tc><w:tc><w:p>" +
            string.Concat(names.Take(3).Select((name, index) => Frame(false, 51 + index, name, PictureUri, Picture(index + 1)))) + "</w:p><w:p>" +
            string.Concat(names.Skip(3).Select((name, index) => Frame(false, 54 + index, name, PictureUri, Picture(index + 4)))) + "</w:p></w:tc></w:tr></w:tbl>"));
        AssertDocumentOrder(output.Markdown, names);
        AssertDocumentOrder(output.Html, names);
    }

    [Fact(Timeout = 30000)]
    public async Task Recording_the_document_position_changes_no_node_id()
    {
        // Ids are a public contract: they come from the anchor locators, never from the position among siblings.
        var picture = Frame(false, 61, "ONLY", PictureUri, Picture(1));
        var one = await Export(WriteDocx("ids-one", "<w:p><w:r><w:t>Host text</w:t></w:r>" + picture + "</w:p>"));
        var two = await Export(WriteDocx("ids-two", "<w:p><w:r><w:t>Host text</w:t></w:r>" + picture + Frame(true, 62, null, ShapeUri, TextBox("ADDED BOX")) + "</w:p>"));
        var first = Assert.Single(Nodes(one.Graph), node => node.Kind == NodeKind.Image);
        var same = Assert.Single(Nodes(two.Graph), node => node.Kind == NodeKind.Image);
        Assert.Equal(first.Id, same.Id);
        Assert.Equal(first.Order, same.Order);
        Assert.Equal(Nodes(one.Graph).Single(node => node.Kind == NodeKind.Paragraph).Id, Nodes(two.Graph).Single(node => node.Kind == NodeKind.Paragraph).Id);
    }

    private static DocumentNode[] Nodes(DocumentGraph graph) => graph.Partitions.SelectMany(partition => partition.Nodes).ToArray();

    private static void AssertDocumentOrder(string output, string[] expected)
    {
        var found = expected.Select(text => (text, index: output.IndexOf(text, StringComparison.Ordinal))).ToArray();
        Assert.All(found, item => Assert.True(item.index >= 0, item.text + " is missing"));
        Assert.Equal(expected, found.OrderBy(item => item.index).Select(item => item.text).ToArray());
    }

    private static string VmlPicture(string alt) =>
        $"<w:r><w:pict><v:shape id='{alt.Replace(' ', '_')}' alt='{alt}' style='width:50pt;height:25pt'><v:imagedata r:id='rImage'/></v:shape></w:pict></w:r>";

    private static string Picture(int id) =>
        $"<pic:pic><pic:nvPicPr><pic:cNvPr id='{id}' name='Picture {id}'/><pic:cNvPicPr/></pic:nvPicPr>" +
        "<pic:blipFill><a:blip r:embed='rImage'/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
        "<pic:spPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='952500' cy='476250'/></a:xfrm><a:prstGeom prst='rect'/></pic:spPr></pic:pic>";

    private static string TextBox(string text) =>
        "<wps:wsp><wps:cNvSpPr txBox='1'/><wps:spPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='952500' cy='381000'/></a:xfrm><a:prstGeom prst='rect'><a:avLst/></a:prstGeom></wps:spPr>" +
        $"<wps:txbx><w:txbxContent><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:txbxContent></wps:txbx><wps:bodyPr/></wps:wsp>";

    private static string Frame(bool anchor, int id, string? description, string uri, string content, int x = 0)
    {
        var properties = $"<wp:docPr id='{id}' name='Frame {id}'{(description is null ? "" : $" descr='{description}'")}/><wp:cNvGraphicFramePr/>" +
            $"<a:graphic><a:graphicData uri='{uri}'>{content}</a:graphicData></a:graphic>";
        return "<w:r><w:drawing>" + (anchor
            ? "<wp:anchor distT='0' distB='0' distL='0' distR='0' simplePos='0' relativeHeight='0' behindDoc='0' locked='0' layoutInCell='1' allowOverlap='1'><wp:simplePos x='0' y='0'/>" +
              $"<wp:positionH relativeFrom='column'><wp:posOffset>{x * 9525}</wp:posOffset></wp:positionH>" +
              "<wp:positionV relativeFrom='paragraph'><wp:posOffset>0</wp:posOffset></wp:positionV>" +
              $"<wp:extent cx='952500' cy='476250'/><wp:wrapNone/>{properties}</wp:anchor>"
            : $"<wp:inline><wp:extent cx='952500' cy='476250'/>{properties}</wp:inline>") + "</w:drawing></w:r>";
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
        Write("word/document.xml", "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships' xmlns:wp='http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main' xmlns:wps='http://schemas.microsoft.com/office/word/2010/wordprocessingShape' xmlns:pic='http://schemas.openxmlformats.org/drawingml/2006/picture' xmlns:v='urn:schemas-microsoft-com:vml'><w:body>" + body + "<w:sectPr/></w:body></w:document>");
        Write("word/_rels/document.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rImage' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/image' Target='media/image1.png'/></Relationships>");
        using var image = zip.CreateEntry("word/media/image1.png").Open();
        image.Write(PngRasterImage.Encode(4, 2, [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0]));
        return path;
    }

    private sealed record Exported(DocumentGraph Graph, string Markdown, string Html);
}
