using System.Text.RegularExpressions;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Pptx;
using DocRedock.Markdown;
using DocRedock.Render;

namespace DocRedock.Tests.Pptx;

/// <summary>Charts, tables and SmartArt of a master or layout, pattern, theme picture, tiled and path-gradient
/// backgrounds, and the text, fills and edges of table cells.</summary>
public sealed partial class PptxSlideDecorationTests
{
    private const string ChartPart = "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
        "<c:chart><c:title><c:tx><c:rich><a:p><a:r><a:t>Adoption by quarter</a:t></a:r></a:p></c:rich></c:tx></c:title>" +
        "<c:plotArea><c:barChart><c:ser><c:idx val=\"0\"/><c:tx><c:strRef><c:strCache><c:pt idx=\"0\"><c:v>Adopters</c:v></c:pt></c:strCache></c:strRef></c:tx>" +
        "<c:cat><c:strRef><c:strCache><c:pt idx=\"0\"><c:v>Q1</c:v></c:pt><c:pt idx=\"1\"><c:v>Q2</c:v></c:pt></c:strCache></c:strRef></c:cat>" +
        "<c:val><c:numRef><c:numCache><c:pt idx=\"0\"><c:v>12</c:v></c:pt><c:pt idx=\"1\"><c:v>30</c:v></c:pt></c:numCache></c:numRef></c:val></c:ser></c:barChart></c:plotArea></c:chart></c:chartSpace>";

    private static string GraphicFrame(int id, string frame, string uri, string content) =>
        $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{id}\" name=\"Frame {id}\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>" +
        frame.Replace("a:xfrm", "p:xfrm", StringComparison.Ordinal) + "<a:graphic><a:graphicData uri=\"" + uri + "\">" + content + "</a:graphicData></a:graphic></p:graphicFrame>";

    private static string Markdown(byte[] package) => new ReadableMarkdownSerializer().Serialize(new PptxAdapter().Extract(new MemoryStream(package)).Graph);

    [Fact]
    public void A_chart_of_the_master_is_drawn_behind_the_slide_shapes_and_the_Markdown_stays_as_it_was()
    {
        var slide = Box(2, "Body", Frame(100, 100, 300, 60));
        var chart = GraphicFrame(9, Frame(500, 300, 400, 300), "http://schemas.openxmlformats.org/drawingml/2006/chart",
            "<c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"rId2\"/>");
        var package = Deck(slide, master: chart,
            masterRelationships: "<Relationship Id=\"rId2\" Type=\"" + RelationshipType + "chart\" Target=\"../charts/chart1.xml\"/>",
            parts: new() { ["ppt/charts/chart1.xml"] = ChartPart });
        var extraction = new PptxAdapter().Extract(new MemoryStream(package));
        var html = ReadableHtmlWriter.WriteDocument(ReadableProjectionBuilder.Build(extraction.Graph).Document).Html;
        var drawn = html.IndexOf("data-drawing-kind=\"chart\" data-source-part=\"/ppt/slideMasters/slideMaster1.xml\"", StringComparison.Ordinal);
        Assert.True(drawn >= 0, "the master's chart is not drawn");
        var own = html.IndexOf("data-drawing-kind=\"shape\" data-source-part=\"/ppt/slides/slide1.xml\"", StringComparison.Ordinal);
        Assert.True(drawn < own, "the master's chart is not behind the slide's shape");
        // The chart itself, read through the master's relationships: its title and categories, no fallback box.
        Assert.Contains("Adoption by quarter", html[drawn..own], StringComparison.Ordinal);
        Assert.Contains("Q2", html[drawn..own], StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"source-chart-fallback\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(extraction.Warnings, warning => warning.Contains("chart has no resolved source node", StringComparison.Ordinal));
        // No node, no text: the Markdown is that of the deck without the chart.
        Assert.Equal(Markdown(Deck(slide)), Markdown(package));
    }

    [Fact]
    public void A_table_of_the_layout_is_drawn_with_its_table_style_and_the_Markdown_stays_as_it_was()
    {
        var slide = Box(2, "Body", Frame(100, 500, 300, 60));
        var table = Table(5, "<a:tblPr firstRow=\"1\" bandRow=\"1\"><a:tableStyleId>" + Medium2Accent1 + "</a:tableStyleId></a:tblPr>",
            [Cell("Layout head"), Cell("Layout value")], [Cell("Layout one"), Cell("Layout 1")]);
        var package = Deck(slide, layout: table);
        var html = Html(package);
        var drawn = html.IndexOf("<div class=\"source-native-table\"", StringComparison.Ordinal);
        Assert.True(drawn >= 0, "the layout's table is not drawn");
        Assert.True(drawn < html.IndexOf("data-drawing-kind=\"shape\" data-source-part=\"/ppt/slides/slide1.xml\"", StringComparison.Ordinal));
        // At its source frame (100, 100), 500 px wide.
        Assert.Matches("<div class=\"source-native-table\" style=\"[^\"]*width:500px;height:80px;transform:matrix\\(1,0,0,1,100,100\\)\"", html);
        Assert.Single(Regex.Matches(html, "Layout head"));
        var header = CellStyle(html, "Layout head");
        Assert.Equal("#C00000", header["background-color"]);
        Assert.Equal("#FFFFFF", header["color"]);
        Assert.Equal("bold", header["font-weight"]);
        Assert.Equal("normal", CellStyle(html, "Layout one")["font-weight"]);
        // A layout's table with text was a table of the slide's Markdown before: it still is, once.
        Assert.Single(Regex.Matches(Markdown(package), "Layout head"));
    }

    [Fact]
    public void A_table_of_the_master_without_text_is_drawn_from_its_cells_and_the_Markdown_stays_as_it_was()
    {
        var slide = Box(2, "Body", Frame(100, 500, 300, 60));
        var table = Table(5, "<a:tblPr/>", [Cell("", properties: "<a:tcPr>" + Solid("FFC000") + "</a:tcPr>"), Cell("")],
            [Cell("", properties: "<a:tcPr><a:lnB w=\"19050\">" + Solid("0070C0") + "</a:lnB></a:tcPr>").Replace("<a:tc>", "<a:tc gridSpan=\"2\">", StringComparison.Ordinal),
                "<a:tc hMerge=\"1\"><a:txBody><a:bodyPr/><a:lstStyle/><a:p/></a:txBody><a:tcPr/></a:tc>"]);
        var package = Deck(slide, master: table);
        var extraction = new PptxAdapter().Extract(new MemoryStream(package));
        // No text, no node: the scene alone holds the table.
        var scene = Assert.Single(Assert.Single(SourceDrawingSceneJson.Read(extraction.Graph.FormatExtensions![SourceDrawingSceneJson.ExtensionKey])).Value);
        var drawn = Assert.Single(scene.Objects, item => item.Kind == "table");
        Assert.Null(drawn.SourceNodeId);
        var html = ReadableHtmlWriter.WriteDocument(ReadableProjectionBuilder.Build(extraction.Graph).Document).Html;
        var frame = Regex.Match(html, "<div class=\"source-native-table\" style=\"[^\"]*width:500px;height:80px;transform:matrix\\(1,0,0,1,100,100\\)\">(.*?)</table>", RegexOptions.Singleline);
        Assert.True(frame.Success, "the master's table is not drawn");
        Assert.True(frame.Index < html.IndexOf("data-drawing-kind=\"shape\" data-source-part=\"/ppt/slides/slide1.xml\"", StringComparison.Ordinal));
        Assert.Contains("<col style=\"width:40%\"><col style=\"width:60%\">", frame.Value, StringComparison.Ordinal);
        var cells = Regex.Matches(frame.Groups[1].Value, "<td(?: colspan=\"(\\d)\")?[^>]* class=\"[^\"]*(source-style-\\d+)[^\"]*\"");
        Assert.Equal(3, cells.Count);
        Assert.Equal("2", cells[2].Groups[1].Value);
        string Declaration(Match cell, string property) => Regex.Match(html, "\\.direct-document \\.source-native-table tr>\\." + cell.Groups[2].Value + ",[^{]*\\{[^}]*" + property + ":([^;!]*)").Groups[1].Value;
        Assert.Equal("#FFC000", Declaration(cells[0], "background-color"));
        Assert.Equal("transparent", Declaration(cells[1], "background-color"));
        Assert.Equal("2px solid #0070C0", Declaration(cells[2], "border-bottom"));
        Assert.Equal(Markdown(Deck(slide)), Markdown(package));
    }

    [Fact]
    public void SmartArt_of_the_master_is_drawn_from_its_saved_drawing_and_the_Markdown_stays_as_it_was()
    {
        const string diagram = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
        const string drawing = "http://schemas.microsoft.com/office/drawing/2008/diagram";
        var slide = Box(2, "Body", Frame(100, 100, 300, 60));
        var frame = GraphicFrame(9, Frame(500, 300, 400, 200), diagram, "<dgm:relIds xmlns:dgm=\"" + diagram + "\" r:dm=\"rId2\"/>");
        var package = Deck(slide, master: frame,
            masterRelationships: "<Relationship Id=\"rId2\" Type=\"" + RelationshipType + "diagramData\" Target=\"../diagrams/data1.xml\"/>",
            parts: new()
            {
                ["ppt/diagrams/data1.xml"] = "<dgm:dataModel xmlns:dgm=\"" + diagram + "\" xmlns:dsp=\"" + drawing + "\"><dgm:extLst><dsp:dataModelExt relId=\"rDraw\"/></dgm:extLst></dgm:dataModel>",
                ["ppt/diagrams/_rels/data1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rDraw\" Type=\"http://schemas.microsoft.com/office/2007/relationships/diagramDrawing\" Target=\"drawing1.xml\"/></Relationships>",
                ["ppt/diagrams/drawing1.xml"] = "<dsp:drawing xmlns:dsp=\"" + drawing + "\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><dsp:spTree>" +
                    "<dsp:nvGrpSpPr><dsp:cNvPr id=\"0\" name=\"\"/><dsp:cNvGrpSpPr/></dsp:nvGrpSpPr><dsp:grpSpPr/>" +
                    "<dsp:sp><dsp:nvSpPr><dsp:cNvPr id=\"3\" name=\"s\"/><dsp:cNvSpPr/></dsp:nvSpPr><dsp:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"1905000\" cy=\"952500\"/></a:xfrm>" +
                    "<a:prstGeom prst=\"roundRect\"><a:avLst/></a:prstGeom>" + Solid("7030A0") + "</dsp:spPr>" +
                    "<dsp:txBody><a:bodyPr/><a:p><a:r><a:rPr sz=\"1800\"/><a:t>Master step</a:t></a:r></a:p></dsp:txBody></dsp:sp></dsp:spTree></dsp:drawing>",
            });
        var html = Html(package);
        var drawn = html.IndexOf("data-source-part=\"/ppt/diagrams/drawing1.xml\"", StringComparison.Ordinal);
        Assert.True(drawn >= 0, "the master's SmartArt is not drawn");
        var own = html.IndexOf("data-drawing-kind=\"shape\" data-source-part=\"/ppt/slides/slide1.xml\"", StringComparison.Ordinal);
        Assert.True(drawn < own, "the master's SmartArt is not behind the slide's shape");
        Assert.Contains("fill=\"#7030A0\"", html[drawn..own], StringComparison.Ordinal);
        Assert.Single(Regex.Matches(html, "Master step"));
        Assert.Equal(Markdown(Deck(slide)), Markdown(package));
    }

    [Fact]
    public void A_pattern_background_is_drawn_as_its_pattern()
    {
        var html = Html(Deck(Box(2, "Body", Frame(100, 100, 300, 60)),
            slideBackground: "<p:bg><p:bgPr><a:pattFill prst=\"dkVert\"><a:fgClr><a:srgbClr val=\"1F4E79\"/></a:fgClr><a:bgClr><a:srgbClr val=\"FFF2CC\"/></a:bgClr></a:pattFill><a:effectLst/></p:bgPr></p:bg>"));
        var background = Regex.Match(html, "<svg class=\"source-scene-background\"[^>]*>.*?</svg>", RegexOptions.Singleline);
        Assert.True(background.Success, "the slide scene has no background");
        var pattern = Regex.Match(background.Value, "<pattern id=\"([^\"]+)\"[^>]*width=\"8\" height=\"8\"");
        Assert.True(pattern.Success, "the background is no pattern");
        Assert.Contains("fill=\"#FFF2CC\"", background.Value, StringComparison.Ordinal);
        Assert.Contains("fill=\"#1F4E79\"", background.Value, StringComparison.Ordinal);
        Assert.Contains("<rect width=\"960\" height=\"720\" fill=\"url(#" + pattern.Groups[1].Value + ")\"", background.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_gradient_background_is_drawn_from_its_focus_outwards()
    {
        var html = Html(Deck(Box(2, "Body", Frame(100, 100, 300, 60)),
            masterBackground: "<p:bg><p:bgPr><a:gradFill><a:gsLst><a:gs pos=\"0\"><a:srgbClr val=\"FFFFFF\"/></a:gs><a:gs pos=\"100000\"><a:srgbClr val=\"1F4E79\"/></a:gs></a:gsLst>" +
                "<a:path path=\"circle\"><a:fillToRect l=\"50000\" t=\"50000\" r=\"50000\" b=\"50000\"/></a:path></a:gradFill><a:effectLst/></p:bgPr></p:bg>"));
        var background = Regex.Match(html, "<svg class=\"source-scene-background\"[^>]*>.*?</svg>", RegexOptions.Singleline);
        Assert.True(background.Success, "the slide scene has no background");
        Assert.Contains("<radialGradient", background.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("<linearGradient", background.Value, StringComparison.Ordinal);
        // The focus is the centre of the slide.
        Assert.Matches("gradientTransform=\"matrix\\(960,0,0,720,480,360\\)\"", background.Value);
    }

    [Fact]
    public async Task A_background_reference_to_a_picture_fill_of_the_theme_takes_the_picture_through_the_themes_relationships()
    {
        var html = await ExportHtml(Deck(Box(2, "Body", Frame(100, 100, 300, 60)),
            masterBackground: "<p:bg><p:bgRef idx=\"1003\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg>",
            themeBackgroundFill: "<a:blipFill rotWithShape=\"1\"><a:blip r:embed=\"rId7\"/><a:stretch><a:fillRect/></a:stretch></a:blipFill>",
            media: new() { ["ppt/media/theme-backdrop.png"] = Png },
            parts: new()
            {
                ["ppt/theme/_rels/theme1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    ImageRelationship("rId7", "../media/theme-backdrop.png") + "</Relationships>",
            }));
        var background = Regex.Match(html, "<div class=\"source-scene-background source-shared-image (source-shared-image-\\d+)\"></div>");
        Assert.True(background.Success, "the slide scene has no picture background");
        Assert.Contains("." + background.Groups[1].Value + "{background-image:url(\"data:image/png;base64," + Convert.ToBase64String(Png) + "\")}", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ImageNotWritten", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tiled_picture_background_repeats_at_the_size_and_offset_of_its_tile()
    {
        // A 1 x 1 pixel picture at 5000 %: tiles of 50 px, the first one 10 px right of and 20 px below the top left corner.
        var html = await ExportHtml(Deck(Box(2, "Body", Frame(100, 100, 300, 60)),
            layoutBackground: "<p:bg><p:bgPr><a:blipFill><a:blip r:embed=\"rId2\"/><a:tile tx=\"95250\" ty=\"190500\" sx=\"5000000\" sy=\"5000000\" flip=\"none\" algn=\"tl\"/></a:blipFill></p:bgPr></p:bg>",
            layoutRelationships: ImageRelationship("rId2", "../media/tile.png"),
            media: new() { ["ppt/media/tile.png"] = Png }));
        var background = Regex.Match(html, "<div class=\"source-scene-background source-shared-image (source-shared-image-\\d+)\" style=\"([^\"]*)\"></div>");
        Assert.True(background.Success, "the tiled background is not drawn as tiles");
        Assert.Equal("background-size:50px 50px;background-repeat:repeat;background-position:calc(0% + 10px) calc(0% + 20px)", background.Groups[2].Value);
        Assert.Single(Regex.Matches(html, Regex.Escape("data:image/png;base64," + Convert.ToBase64String(Png))));

        // Anchored at the centre without an offset.
        var centred = await ExportHtml(Deck(Box(2, "Body", Frame(100, 100, 300, 60)),
            layoutBackground: "<p:bg><p:bgPr><a:blipFill><a:blip r:embed=\"rId2\"/><a:tile sx=\"2000000\" sy=\"1000000\" algn=\"ctr\"/></a:blipFill></p:bgPr></p:bg>",
            layoutRelationships: ImageRelationship("rId2", "../media/tile.png"),
            media: new() { ["ppt/media/tile.png"] = Png }));
        Assert.Contains("style=\"background-size:20px 10px;background-repeat:repeat;background-position:calc(50% + 0px) calc(50% + 0px)\"", centred, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_cell_takes_its_font_from_the_run_the_table_style_or_the_theme()
    {
        const string styleId = "{11111111-2222-3333-4444-555555555555}";
        var styles = "<a:tblStyleLst xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" def=\"" + styleId + "\"><a:tblStyle styleId=\"" + styleId + "\" styleName=\"Fonts\">" +
            "<a:wholeTbl><a:tcTxStyle><a:fontRef idx=\"major\"/><a:schemeClr val=\"dk1\"/></a:tcTxStyle></a:wholeTbl>" +
            "<a:firstRow><a:tcTxStyle><a:font><a:latin typeface=\"Georgia\"/></a:font><a:schemeClr val=\"dk1\"/></a:tcTxStyle></a:firstRow></a:tblStyle></a:tblStyleLst>";
        var table = Table(5, "<a:tblPr firstRow=\"1\"><a:tableStyleId>" + styleId + "</a:tableStyleId></a:tblPr>",
            [Cell("Style font"), Cell("Run font", run: "<a:rPr><a:latin typeface=\"Courier New\"/><a:ea typeface=\"Meiryo\"/></a:rPr>")],
            [Cell("Theme major"), Cell("Theme minor run", run: "<a:rPr><a:latin typeface=\"+mn-lt\"/></a:rPr>")]);
        var html = Html(Deck(table, parts: new() { ["ppt/tableStyles.xml"] = styles, ["ppt/theme/theme1.xml"] = FontTheme }));
        Assert.Equal("\"Georgia\",sans-serif", CellStyle(html, "Style font")["font-family"]);
        Assert.Equal("\"Courier New\",\"Meiryo\",sans-serif", CellStyle(html, "Run font")["font-family"]);
        Assert.Equal("\"Cambria\",sans-serif", CellStyle(html, "Theme major")["font-family"]);
        Assert.Equal("\"Verdana\",sans-serif", CellStyle(html, "Theme minor run")["font-family"]);

        // Without a table style the text is in the theme's minor font.
        var plain = Html(Deck(Table(5, "<a:tblPr/>", [Cell("Plain"), Cell("Other")]), parts: new() { ["ppt/theme/theme1.xml"] = FontTheme }));
        Assert.Equal("\"Verdana\",sans-serif", CellStyle(plain, "Plain")["font-family"]);
    }

    [Fact]
    public void Each_run_of_a_table_cell_is_drawn_with_its_own_formatting_and_the_Markdown_text_stays_as_it_was()
    {
        const string runs = "<a:r><a:rPr sz=\"1200\"/><a:t>Plain </a:t></a:r>" +
            "<a:r><a:rPr sz=\"2400\" b=\"1\" i=\"1\" u=\"sng\"><a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill><a:latin typeface=\"Georgia\"/></a:rPr><a:t>loud</a:t></a:r>" +
            "<a:r><a:rPr sz=\"1200\"/><a:t> end</a:t></a:r>";
        static string Mixed(string content) => "<a:tc><a:txBody><a:bodyPr/><a:lstStyle/><a:p>" + content + "</a:p></a:txBody><a:tcPr/></a:tc>";
        var package = Deck(Table(5, "<a:tblPr/>", [Mixed(runs), Cell("Other")]));
        var html = Html(package);
        var cell = Regex.Match(html, "<t[hd][^>]*>((?:(?!</t[hd]>).)*loud(?:(?!</t[hd]>).)*)</t[hd]>");
        Assert.True(cell.Success, "the cell is not drawn");
        var loud = Regex.Match(cell.Groups[1].Value, "<span class=\"(source-style-\\d+)\">(?:<[a-z]+>)*loud");
        Assert.True(loud.Success, "the second run has no style of its own: " + cell.Groups[1].Value);
        var rule = Regex.Match(html, "\\.direct-document \\.source-native-table \\." + loud.Groups[1].Value + "\\{([^}]*)\\}");
        Assert.True(rule.Success, "the run's style has no rule");
        var declarations = rule.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(d => d.Split(':', 2))
            .ToDictionary(d => d[0], d => d[1].Replace("!important", "", StringComparison.Ordinal), StringComparer.Ordinal);
        Assert.Equal("24pt", declarations["font-size"]);
        Assert.Equal("bold", declarations["font-weight"]);
        Assert.Equal("italic", declarations["font-style"]);
        // The underline is the run's own mark, as in any other table.
        Assert.Contains("<u>loud</u>", cell.Groups[1].Value, StringComparison.Ordinal);
        Assert.Equal("#FF0000", declarations["color"]);
        Assert.Equal("\"Georgia\",sans-serif", declarations["font-family"]);
        // The cell keeps the first run's formatting; the runs around the loud one are not in its style.
        Assert.Matches("class=\"[^\"]*source-style-\\d+[^\"]*\"[^>]*>Plain <span", cell.Value);
        Assert.DoesNotMatch("<span class=\"" + loud.Groups[1].Value + "\">(?:<[a-z]+>)* end", cell.Groups[1].Value);
        Assert.Equal("Plain loud end", Regex.Replace(cell.Groups[1].Value, "<[^>]+>", ""));

        var markdown = Markdown(package);
        Assert.Contains("| Plain <u>loud</u> end | Other |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("FF0000", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_cell_draws_a_gradient_fill_its_diagonals_and_the_dash_and_width_of_its_edges()
    {
        const string gradient = "<a:gradFill><a:gsLst><a:gs pos=\"0\"><a:srgbClr val=\"FF0000\"/></a:gs><a:gs pos=\"100000\"><a:srgbClr val=\"0000FF\"/></a:gs></a:gsLst><a:lin ang=\"5400000\" scaled=\"0\"/></a:gradFill>";
        var html = Html(Deck(Table(5, "<a:tblPr/>",
            [Cell("Gradient", properties: "<a:tcPr>" + gradient + "</a:tcPr>"),
                Cell("Diagonals", properties: "<a:tcPr><a:lnTlToBr w=\"19050\">" + Solid("00B050") + "</a:lnTlToBr><a:lnBlToTr w=\"9525\">" + Solid("7030A0") + "</a:lnBlToTr></a:tcPr>")],
            [Cell("Dashed", properties: "<a:tcPr><a:lnB w=\"28575\">" + Solid("0070C0") + "<a:prstDash val=\"dash\"/></a:lnB></a:tcPr>"),
                Cell("Dotted", properties: "<a:tcPr><a:lnR w=\"12700\">" + Solid("C00000") + "<a:prstDash val=\"sysDot\"/></a:lnR><a:lnB w=\"57150\" cmpd=\"dbl\">" + Solid("000000") + "</a:lnB></a:tcPr>")])));
        var filled = CellStyle(html, "Gradient");
        // 90 degrees in DrawingML runs from the top to the bottom.
        Assert.Equal("linear-gradient(180deg,#FF0000 0%,#0000FF 100%)", filled["background-image"]);
        Assert.Equal("#FF0000", filled["background-color"]);
        Assert.Equal("linear-gradient(to top right,transparent calc(50% - 1px),#00B050 calc(50% - 1px),#00B050 calc(50% + 1px),transparent calc(50% + 1px))," +
            "linear-gradient(to bottom right,transparent calc(50% - 0.5px),#7030A0 calc(50% - 0.5px),#7030A0 calc(50% + 0.5px),transparent calc(50% + 0.5px))",
            CellStyle(html, "Diagonals")["background-image"]);
        // The real width of the edge (28575 EMU are 3 px, 12700 are 1.33) and its dash.
        Assert.Equal("3px dashed #0070C0", CellStyle(html, "Dashed")["border-bottom"]);
        Assert.Equal("1.33px dotted #C00000", CellStyle(html, "Dotted")["border-right"]);
        Assert.Equal("6px double #000000", CellStyle(html, "Dotted")["border-bottom"]);
    }

    /// <summary>The test theme with distinct fonts: Cambria for headings, Verdana for body text.</summary>
    private const string FontTheme = "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Fonts\"><a:themeElements><a:clrScheme name=\"Fonts\">" +
        "<a:dk1><a:srgbClr val=\"000000\"/></a:dk1><a:lt1><a:srgbClr val=\"FFFFFF\"/></a:lt1><a:dk2><a:srgbClr val=\"112233\"/></a:dk2><a:lt2><a:srgbClr val=\"EEEEEE\"/></a:lt2>" +
        "<a:accent1><a:srgbClr val=\"C00000\"/></a:accent1><a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2><a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3>" +
        "<a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4><a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6>" +
        "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
        "<a:fontScheme name=\"Fonts\"><a:majorFont><a:latin typeface=\"Cambria\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
        "<a:minorFont><a:latin typeface=\"Verdana\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
        "<a:fmtScheme name=\"Fonts\"><a:fillStyleLst/><a:lnStyleLst/><a:effectStyleLst/><a:bgFillStyleLst/></a:fmtScheme></a:themeElements></a:theme>";
}
