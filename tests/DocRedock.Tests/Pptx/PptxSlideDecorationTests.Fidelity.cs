using System.Text.RegularExpressions;

namespace DocRedock.Tests.Pptx;

/// <summary>Mirrored and cropped background pictures, pattern fills and paragraph alignment of table cells,
/// and the picture fills of SmartArt shapes.</summary>
public sealed partial class PptxSlideDecorationTests
{
    private static string PictureBackground(string content) =>
        "<p:bg><p:bgPr><a:blipFill><a:blip r:embed=\"rId2\"/>" + content + "</a:blipFill></p:bgPr></p:bg>";

    private Task<string> BackgroundHtml(string content) => ExportHtml(Deck(Box(2, "Body", Frame(100, 100, 300, 60)),
        layoutBackground: PictureBackground(content), layoutRelationships: ImageRelationship("rId2", "../media/tile.png"),
        media: new() { ["ppt/media/tile.png"] = Png }));

    [Fact]
    public async Task A_tiled_picture_background_mirrors_every_second_tile_as_its_flip_says()
    {
        // A 1 x 1 pixel picture at 5000 %: tiles of 50 px. Mirrored on both axes, the pattern repeats after two tiles each way.
        var html = await BackgroundHtml("<a:tile tx=\"95250\" ty=\"190500\" sx=\"5000000\" sy=\"5000000\" flip=\"xy\" algn=\"tl\"/>");
        var background = Regex.Match(html, "<svg class=\"source-scene-background\"[^>]*><defs><pattern id=\"(source-scene-background-\\d+)\" patternUnits=\"userSpaceOnUse\" " +
            "x=\"10\" y=\"20\" width=\"100\" height=\"100\">(.*?)</pattern></defs><rect width=\"960\" height=\"720\" fill=\"url\\(#\\1\\)\"/></svg>");
        Assert.True(background.Success, "the mirrored tiles are not drawn as a pattern of two tiles each way");
        var tiles = background.Groups[2].Value;
        Assert.Equal(4, Regex.Matches(tiles, "<image ").Count);
        Assert.Contains("<g transform=\"translate(150,0) scale(-1,1)\"><svg x=\"50\" y=\"0\" width=\"50\" height=\"50\"", tiles, StringComparison.Ordinal);
        Assert.Contains("<g transform=\"translate(0,150) scale(1,-1)\"><svg x=\"0\" y=\"50\" width=\"50\" height=\"50\"", tiles, StringComparison.Ordinal);
        Assert.Contains("<g transform=\"translate(150,150) scale(-1,-1)\"><svg x=\"50\" y=\"50\" width=\"50\" height=\"50\"", tiles, StringComparison.Ordinal);
        Assert.Contains("href=\"data:image/png;base64," + Convert.ToBase64String(Png) + "\"", tiles, StringComparison.Ordinal);
        Assert.True(background.Index < html.IndexOf("class=\"source-drawing\"", StringComparison.Ordinal));
        Assert.DoesNotContain("source-shared-image ", html, StringComparison.Ordinal);

        // Mirrored on one axis only: two tiles side by side, one row.
        var horizontal = await BackgroundHtml("<a:tile sx=\"5000000\" sy=\"5000000\" flip=\"x\"/>");
        Assert.Matches("<pattern id=\"source-scene-background-\\d+\" patternUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"100\" height=\"50\">", horizontal);
        Assert.Contains("scale(-1,1)", horizontal, StringComparison.Ordinal);
        Assert.DoesNotContain("scale(1,-1)", horizontal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tiled_picture_background_repeats_only_the_part_its_source_rectangle_keeps()
    {
        // The right half of the picture, a quarter cut from its top: a tile of 25 x 37.5 px that shows that part alone.
        var html = await BackgroundHtml("<a:srcRect l=\"50000\" t=\"25000\"/><a:tile sx=\"5000000\" sy=\"5000000\" flip=\"none\" algn=\"tl\"/>");
        Assert.Matches("<svg class=\"source-scene-background\"[^>]*><defs><pattern id=\"source-scene-background-\\d+\" patternUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"25\" height=\"37.5\">" +
            "<svg x=\"0\" y=\"0\" width=\"25\" height=\"37.5\" viewBox=\"0.5 0.25 0.5 0.75\" preserveAspectRatio=\"none\"><image ", html);
    }

    [Fact]
    public async Task A_stretched_picture_background_shows_its_source_rectangle_inside_its_fill_rectangle()
    {
        // The middle half of the picture's width, in the slide (960 x 720) without a tenth at its top and bottom.
        var html = await BackgroundHtml("<a:srcRect l=\"25000\" r=\"25000\"/><a:stretch><a:fillRect t=\"10000\" b=\"10000\"/></a:stretch>");
        var background = Regex.Match(html, "<div class=\"source-scene-background source-shared-image (source-shared-image-\\d+)\" style=\"([^\"]*)\"></div>");
        Assert.True(background.Success, "the cropped background is not drawn");
        Assert.Equal("left:0px;top:72px;width:960px;height:576px;background-size:1920px 576px;background-position:-480px 0px", background.Groups[2].Value);
        // Still the page's one shared picture.
        Assert.Single(Regex.Matches(html, Regex.Escape("data:image/png;base64," + Convert.ToBase64String(Png))));

        // A fill rectangle that reaches outside the slide is cut at the slide's edge; the picture keeps its place.
        var outside = await BackgroundHtml("<a:stretch><a:fillRect l=\"-10000\" r=\"-10000\" t=\"-50000\"/></a:stretch>");
        Assert.Contains("style=\"left:0px;top:0px;width:960px;height:720px;background-size:1152px 1080px;background-position:-96px -360px\"", outside, StringComparison.Ordinal);

        // Without either the picture covers the slide as before.
        var plain = await BackgroundHtml("<a:srcRect/><a:stretch><a:fillRect/></a:stretch>");
        Assert.Matches("<div class=\"source-scene-background source-shared-image source-shared-image-\\d+\"></div>", plain);
    }

    [Fact]
    public void A_table_cell_draws_the_pattern_fill_of_the_cell_and_of_its_table_style()
    {
        const string styleId = "{22222222-3333-4444-5555-666666666666}";
        static string Pattern(string preset, string fore, string back) => "<a:pattFill prst=\"" + preset + "\"><a:fgClr><a:srgbClr val=\"" + fore + "\"/></a:fgClr>" +
            "<a:bgClr><a:srgbClr val=\"" + back + "\"/></a:bgClr></a:pattFill>";
        var styles = "<a:tblStyleLst xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" def=\"" + styleId + "\"><a:tblStyle styleId=\"" + styleId + "\" styleName=\"Hatched\">" +
            "<a:wholeTbl><a:tcStyle><a:tcBdr/><a:fill>" + Pattern("ltUpDiag", "0070C0", "FFFFFF") + "</a:fill></a:tcStyle></a:wholeTbl></a:tblStyle></a:tblStyleLst>";
        var html = Html(Deck(Table(5, "<a:tblPr><a:tableStyleId>" + styleId + "</a:tableStyleId></a:tblPr>",
            [Cell("Styled"), Cell("Own", properties: "<a:tcPr>" + Pattern("horz", "FF0000", "FFFF00") + "</a:tcPr>")]), parts: new() { ["ppt/tableStyles.xml"] = styles }));

        var own = CellStyle(html, "Own");
        // The background color lies under the 8 x 8 bitmap; "horz" is one foreground row of eight pixels.
        Assert.Equal("#FFFF00", own["background-color"]);
        Assert.Equal("url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='8' height='8' shape-rendering='crispEdges'%3E" +
            "%3Crect width='8' height='8' fill='%23FFFF00' fill-opacity='1'/%3E%3Cpath d='M0 0h8v1h-8z' fill='%23FF0000' fill-opacity='1'/%3E%3C/svg%3E\")", own["background-image"]);

        var styled = CellStyle(html, "Styled");
        Assert.Equal("#FFFFFF", styled["background-color"]);
        Assert.StartsWith("url(\"data:image/svg+xml,", styled["background-image"], StringComparison.Ordinal);
        Assert.Contains("fill='%23FFFFFF'", styled["background-image"], StringComparison.Ordinal);
        Assert.Contains("fill='%230070C0'", styled["background-image"], StringComparison.Ordinal);
        Assert.NotEqual(own["background-image"], styled["background-image"]);
    }

    [Fact]
    public void Each_paragraph_of_a_table_cell_keeps_its_own_alignment_and_indent()
    {
        static string Lines(params string[] paragraphs) => "<a:tc><a:txBody><a:bodyPr/><a:lstStyle/>" + string.Concat(paragraphs) + "</a:txBody><a:tcPr/></a:tc>";
        static string Line(string text, string properties) => "<a:p>" + properties + "<a:r><a:rPr lang=\"en-US\"/><a:t>" + text + "</a:t></a:r></a:p>";
        var package = Deck(Table(5, "<a:tblPr/>",
            [Lines(Line("Centre", "<a:pPr algn=\"ctr\"/>"), Line("Right", "<a:pPr algn=\"r\"/>"), Line("Plain", "<a:pPr marL=\"342900\" indent=\"95250\"/>"), Line("Spread", "<a:pPr algn=\"dist\"/>")),
                Lines(Line("Same one", "<a:pPr algn=\"ctr\"/>"), Line("Same two", "<a:pPr algn=\"ctr\"/>"))]));
        var html = Html(package);

        var cell = Regex.Match(html, "<t[hd][^>]* class=\"[^\"]*(source-style-\\d+)[^\"]*\"[^>]*>((?:(?!</t[hd]>).)*Centre(?:(?!</t[hd]>).)*)</t[hd]>");
        Assert.True(cell.Success, "the cell is not drawn");
        var paragraphs = Regex.Matches(cell.Groups[2].Value, "<div class=\"source-cell-paragraph (source-style-\\d+)\">(.*?)</div>");
        Assert.Equal(["Centre", "Right", "Plain", "Spread"], paragraphs.Select(paragraph => Regex.Replace(paragraph.Groups[2].Value, "<[^>]+>", "")));
        // Nothing of the cell's text lies outside its paragraphs, and no line break is left between them.
        Assert.Equal("", Regex.Replace(cell.Groups[2].Value, "<div class=\"source-cell-paragraph source-style-\\d+\">.*?</div>", ""));
        Assert.DoesNotContain("<br>", cell.Groups[2].Value, StringComparison.Ordinal);
        string Rule(int index)
        {
            var rule = Regex.Match(html, "\\.direct-document \\.source-native-table \\." + paragraphs[index].Groups[1].Value + "\\{([^}]*)\\}");
            Assert.True(rule.Success, "paragraph " + index + " has no rule");
            return rule.Groups[1].Value;
        }
        Assert.Equal("text-align:center!important;", Rule(0));
        Assert.Equal("text-align:right!important;", Rule(1));
        // A paragraph without an alignment of its own is not centred with the first one: it is left-aligned, 36 px in, its first line 10 px more.
        Assert.Equal("padding-left:36px!important;text-align:left!important;text-indent:10px!important;", Rule(2));
        Assert.Equal("text-align:justify!important;", Rule(3));

        // A cell whose paragraphs are aligned alike is aligned as a whole, as before.
        var same = Regex.Match(html, "<t[hd][^>]*>((?:(?!</t[hd]>).)*Same one(?:(?!</t[hd]>).)*)</t[hd]>");
        Assert.True(same.Success, "the second cell is not drawn");
        Assert.DoesNotContain("source-cell-paragraph", same.Groups[1].Value, StringComparison.Ordinal);
        Assert.Equal("Same one<br>Same two", same.Groups[1].Value);

        var markdown = Markdown(package);
        Assert.Contains("Centre", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("source-cell-paragraph", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("text-align", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_picture_fill_of_a_SmartArt_shape_is_drawn_inside_the_shape_from_the_saved_drawings_own_relationships()
    {
        const string diagram = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
        const string drawing = "http://schemas.microsoft.com/office/drawing/2008/diagram";
        const string package = "http://schemas.openxmlformats.org/package/2006/relationships";
        var frame = GraphicFrame(9, Frame(500, 300, 400, 200), diagram, "<dgm:relIds xmlns:dgm=\"" + diagram + "\" r:dm=\"rId2\"/>");
        const string data = "<Relationship Id=\"rId2\" Type=\"" + RelationshipType + "diagramData\" Target=\"../diagrams/data1.xml\"/>";
        Dictionary<string, string> Parts(string drawingRelationships) => new()
        {
            ["ppt/diagrams/data1.xml"] = "<dgm:dataModel xmlns:dgm=\"" + diagram + "\" xmlns:dsp=\"" + drawing + "\"><dgm:extLst><dsp:dataModelExt relId=\"rDraw\"/></dgm:extLst></dgm:dataModel>",
            ["ppt/diagrams/_rels/data1.xml.rels"] = "<Relationships xmlns=\"" + package + "\">" +
                "<Relationship Id=\"rDraw\" Type=\"http://schemas.microsoft.com/office/2007/relationships/diagramDrawing\" Target=\"drawing1.xml\"/></Relationships>",
            // The id rId2 is the diagram data in the slide's relationships and the picture in the drawing's own.
            ["ppt/diagrams/_rels/drawing1.xml.rels"] = "<Relationships xmlns=\"" + package + "\">" + drawingRelationships + "</Relationships>",
            ["ppt/diagrams/drawing1.xml"] = "<dsp:drawing xmlns:dsp=\"" + drawing + "\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"" +
                " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><dsp:spTree>" +
                "<dsp:nvGrpSpPr><dsp:cNvPr id=\"0\" name=\"\"/><dsp:cNvGrpSpPr/></dsp:nvGrpSpPr><dsp:grpSpPr/>" +
                "<dsp:sp><dsp:nvSpPr><dsp:cNvPr id=\"3\" name=\"photo\"/><dsp:cNvSpPr/></dsp:nvSpPr><dsp:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"952500\" cy=\"952500\"/></a:xfrm>" +
                "<a:prstGeom prst=\"ellipse\"><a:avLst/></a:prstGeom><a:blipFill><a:blip r:embed=\"rId2\"/><a:srcRect l=\"10000\" r=\"10000\"/><a:stretch><a:fillRect/></a:stretch></a:blipFill></dsp:spPr></dsp:sp>" +
                "<dsp:sp><dsp:nvSpPr><dsp:cNvPr id=\"4\" name=\"caption\"/><dsp:cNvSpPr/></dsp:nvSpPr><dsp:spPr><a:xfrm><a:off x=\"952500\" y=\"0\"/><a:ext cx=\"952500\" cy=\"952500\"/></a:xfrm>" +
                "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" + Solid("7030A0") + "</dsp:spPr>" +
                "<dsp:txBody><a:bodyPr/><a:p><a:r><a:rPr sz=\"1800\"/><a:t>Portrait</a:t></a:r></a:p></dsp:txBody></dsp:sp></dsp:spTree></dsp:drawing>",
        };
        var picture = ImageRelationship("rId2", "../media/photo.png");
        var slideRelationships = "<Relationships xmlns=\"" + package + "\"><Relationship Id=\"rId1\" Type=\"" + RelationshipType + "slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" + data + "</Relationships>";
        Dictionary<string, byte[]> media = new() { ["ppt/media/photo.png"] = Png };

        void Drawn(byte[] deck)
        {
            var extracted = new DocRedock.Formats.OpenXml.Pptx.PptxAdapter().Extract(new MemoryStream(deck));
            Assert.DoesNotContain(extracted.Warnings, warning => warning.Contains("blipFill", StringComparison.Ordinal) ||
                warning.Contains("Picture fill", StringComparison.Ordinal) || warning.Contains("cached media", StringComparison.Ordinal));
            var html = Html(deck);
            // The picture is the fill of the saved ellipse: a pattern that holds the kept part of the picture, which the outline clips.
            var shape = Regex.Match(html, "<div class=\"source-drawing\" data-drawing-id=\"[^\"]*:smartart:0:[^\"]*\"[^>]*data-source-part=\"/ppt/diagrams/drawing1.xml\".*?</svg></div>", RegexOptions.Singleline);
            Assert.True(shape.Success, "the SmartArt shape with the picture is not drawn");
            var pattern = Regex.Match(shape.Value, "<pattern id=\"([^\"]+)\"[^>]*><svg [^>]*viewBox=\"0.1 0 0.8 1\"[^>]*><image [^>]*href=\"data:image/png;base64," +
                Regex.Escape(Convert.ToBase64String(Png)) + "\"/></svg></pattern>");
            Assert.True(pattern.Success, "the picture is not the fill of the shape: " + shape.Value);
            Assert.Matches("<(?:path|ellipse)[^>]* fill=\"url\\(#" + Regex.Escape(pattern.Groups[1].Value) + "\\)\"", shape.Value);
            Assert.Contains("fill=\"#7030A0\"", html, StringComparison.Ordinal);
            Assert.Contains("Portrait", html, StringComparison.Ordinal);
        }
        // On the slide itself and lent by the master.
        Drawn(Deck(frame, media: media, parts: new(Parts(picture)) { ["ppt/slides/_rels/slide1.xml.rels"] = slideRelationships }));
        Drawn(Deck(Box(2, "Body", Frame(100, 100, 300, 60)), master: frame, masterRelationships: data, media: media, parts: Parts(picture)));

        // A picture the drawing's relationships do not hold leaves the shape unfilled, with a notice; the rest is drawn.
        var missing = Deck(frame, media: media, parts: new(Parts("")) { ["ppt/slides/_rels/slide1.xml.rels"] = slideRelationships });
        var report = new DocRedock.Formats.OpenXml.Pptx.PptxAdapter().Extract(new MemoryStream(missing));
        Assert.Contains(report.Warnings, warning => warning.Contains("Picture fill is not drawn", StringComparison.Ordinal));
        var unfilled = Html(missing);
        Assert.DoesNotContain("href=\"data:image/png", unfilled, StringComparison.Ordinal);
        Assert.Contains("fill=\"#7030A0\"", unfilled, StringComparison.Ordinal);
    }
}
