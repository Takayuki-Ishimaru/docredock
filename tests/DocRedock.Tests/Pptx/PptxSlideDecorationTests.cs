using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Pptx;
using DocRedock.Markdown;
using DocRedock.Render;

namespace DocRedock.Tests.Pptx;

/// <summary>What a slide shows without holding it: the background of the slide, its layout or its master,
/// the shapes of the master and the layout that are no placeholder, and the style of a table's cells.</summary>
public sealed partial class PptxSlideDecorationTests : IDisposable
{
    private const string Medium2Accent1 = "{5C22544A-7EE6-4342-B048-85BB5CCE0C2F}";
    private readonly string root = Path.Combine(Path.GetTempPath(), "docredock-slide-decoration-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void A_slide_takes_the_solid_background_of_its_master()
    {
        var html = Html(Deck(Box(2, "Body", Frame(100, 100, 300, 60)),
            masterBackground: "<p:bg><p:bgPr>" + Solid("1F4E79") + "<a:effectLst/></p:bgPr></p:bg>"));
        var background = Regex.Match(html, "<svg class=\"source-scene-background\"[^>]*>.*?</svg>", RegexOptions.Singleline);
        Assert.True(background.Success, "the slide scene has no background");
        Assert.Contains("fill=\"#1F4E79\"", background.Value, StringComparison.Ordinal);
        Assert.Contains("width=\"960\" height=\"720\"", background.Value, StringComparison.Ordinal);
        // Behind every shape of the scene.
        Assert.True(background.Index < html.IndexOf("class=\"source-drawing\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A_background_reference_takes_the_theme_background_fill_with_its_color()
    {
        var html = Html(Deck(Box(2, "Body", Frame(100, 100, 300, 60)),
            slideBackground: "<p:bg><p:bgRef idx=\"1002\"><a:schemeClr val=\"accent1\"/></p:bgRef></p:bg>",
            masterBackground: "<p:bg><p:bgPr>" + Solid("1F4E79") + "</p:bgPr></p:bg>"));
        var background = Regex.Match(html, "<svg class=\"source-scene-background\"[^>]*>.*?</svg>", RegexOptions.Singleline);
        Assert.True(background.Success, "the slide scene has no background");
        // The second fill of a:bgFillStyleLst is a gradient from the referenced color (accent1) to white; the slide's own background wins over the master's.
        Assert.Contains("<linearGradient", background.Value, StringComparison.Ordinal);
        Assert.Contains("stop-color=\"#C00000\"", background.Value, StringComparison.Ordinal);
        Assert.Contains("stop-color=\"#FFFFFF\"", background.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("#1F4E79", background.Value, StringComparison.Ordinal);

        var solid = Html(Deck(Box(2, "Body", Frame(100, 100, 300, 60)),
            layoutBackground: "<p:bg><p:bgRef idx=\"1001\"><a:srgbClr val=\"00B050\"/></p:bgRef></p:bg>"));
        Assert.Matches("<svg class=\"source-scene-background\"[^>]*>.*?<rect[^>]* fill=\"#00B050\"", solid);
    }

    [Fact]
    public async Task A_picture_background_is_embedded_once_and_stretched_over_the_slide()
    {
        var html = await ExportHtml(Deck(Box(2, "Body", Frame(100, 100, 300, 60)),
            layoutBackground: "<p:bg><p:bgPr><a:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></a:blipFill></p:bgPr></p:bg>",
            layoutRelationships: ImageRelationship("rId2", "../media/backdrop.png"),
            media: new() { ["ppt/media/backdrop.png"] = Png }));
        var background = Regex.Match(html, "<div class=\"source-scene-background source-shared-image (source-shared-image-\\d+)\"></div>");
        Assert.True(background.Success, "the slide scene has no picture background");
        Assert.Contains("." + background.Groups[1].Value + "{background-image:url(\"data:image/png;base64," + Convert.ToBase64String(Png) + "\")}", html, StringComparison.Ordinal);
        Assert.True(background.Index < html.IndexOf("class=\"source-drawing\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_picture_fill_of_a_slide_shape_and_of_a_layout_shape_is_drawn_inside_the_outline()
    {
        const string fill = "<a:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></a:blipFill>";
        const string slideRelationships = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"" + RelationshipType + "slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"" + RelationshipType + "image\" Target=\"../media/fill.png\"/></Relationships>";
        var (html, diagnostics) = await Export(Deck(Box(2, "Slide fill", Frame(100, 200, 300, 60), fill),
            layout: Box(8, "", Frame(0, 680, 960, 40), fill),
            layoutRelationships: ImageRelationship("rId2", "../media/fill.png"),
            media: new() { ["ppt/media/fill.png"] = Png },
            parts: new() { ["ppt/slides/_rels/slide1.xml.rels"] = slideRelationships }));
        var patterns = Regex.Matches(html, "<pattern id=\"([^\"]+)\"[^>]*>(?:(?!</pattern>).)*href=\"data:image/png;base64,", RegexOptions.Singleline);
        Assert.Equal(2, patterns.Count);
        foreach (Match pattern in patterns) Assert.Contains("fill=\"url(#" + pattern.Groups[1].Value + ")\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, item => item.Message.Contains("blipFill", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_master_logo_and_a_layout_rectangle_are_drawn_behind_the_slide_shapes()
    {
        var html = await ExportHtml(DecoratedDeck());
        var logo = Regex.Match(html, "<div class=\"source-drawing\" data-drawing-id=\"[^\"]*\" data-drawing-kind=\"picture\" data-source-part=\"/ppt/slideMasters/slideMaster1.xml\"[^>]*>" +
            "<div class=\"source-picture-clip\"><div class=\"source-shared-image (source-shared-image-\\d+)\" role=\"img\" aria-label=\"Company logo\"");
        Assert.True(logo.Success, "the master's logo is not drawn");
        Assert.Contains("." + logo.Groups[1].Value + "{background-image:url(\"data:image/png;base64,", html, StringComparison.Ordinal);
        var band = html.IndexOf("data-drawing-kind=\"shape\" data-source-part=\"/ppt/slideLayouts/slideLayout1.xml\"", StringComparison.Ordinal);
        var own = html.IndexOf("data-drawing-kind=\"shape\" data-source-part=\"/ppt/slides/slide1.xml\"", StringComparison.Ordinal);
        Assert.True(band >= 0, "the layout's rectangle is not drawn");
        // Master first, then layout, then the slide's own shapes: the later is drawn over the earlier.
        Assert.True(logo.Index < band && band < own, $"order: master {logo.Index}, layout {band}, slide {own}");
        Assert.Contains("fill=\"#00B050\"", html[band..own], StringComparison.Ordinal);
        // A placeholder of the layout or master is never drawn.
        Assert.DoesNotContain("Master title prompt", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Layout title prompt", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowMasterSp_off_hides_the_shapes_of_the_layout_and_the_master()
    {
        var onSlide = await ExportHtml(DecoratedDeck(slideAttributes: " showMasterSp=\"0\""));
        Assert.DoesNotContain("data-source-part=\"/ppt/slideMasters/slideMaster1.xml\"", onSlide, StringComparison.Ordinal);
        Assert.DoesNotContain("data-source-part=\"/ppt/slideLayouts/slideLayout1.xml\"", onSlide, StringComparison.Ordinal);
        Assert.Contains("data-drawing-kind=\"shape\" data-source-part=\"/ppt/slides/slide1.xml\"", onSlide, StringComparison.Ordinal);

        // On the layout it hides the master's shapes only.
        var onLayout = await ExportHtml(DecoratedDeck(layoutAttributes: " showMasterSp=\"0\""));
        Assert.DoesNotContain("data-source-part=\"/ppt/slideMasters/slideMaster1.xml\"", onLayout, StringComparison.Ordinal);
        Assert.Contains("data-drawing-kind=\"shape\" data-source-part=\"/ppt/slideLayouts/slideLayout1.xml\"", onLayout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_picture_the_scene_draws_is_not_also_listed_as_an_image_nothing_refers_to()
    {
        var data = "data:image/png;base64," + Convert.ToBase64String(Png);
        var drawn = await ExportHtml(DecoratedDeck());
        Assert.Single(Regex.Matches(drawn, Regex.Escape(data)));
        Assert.Contains("{background-image:url(\"" + data + "\")}", drawn, StringComparison.Ordinal);
        // The reader lists an extracted image that nothing refers to as a finding of the conversion.
        Assert.DoesNotContain("ImageNotWritten", drawn, StringComparison.Ordinal);
        // A picture no scene draws (the slide hides its master's shapes) is listed as before.
        var hidden = await ExportHtml(DecoratedDeck(slideAttributes: " showMasterSp=\"0\""));
        Assert.Contains("<strong>ImageNotWritten</strong>", hidden, StringComparison.Ordinal);
        Assert.DoesNotContain(data, hidden, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scene_pictures_without_an_extracted_image_are_reported_once_for_the_document()
    {
        // The parts exist, but outside the package's media folder: no image is extracted for them.
        var package = Deck(Box(2, "Body", Frame(100, 100, 300, 60)),
            master: "<p:pic><p:nvPicPr><p:cNvPr id=\"9\" name=\"Logo\"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>" +
                "<p:spPr>" + Frame(840, 20, 96, 48) + "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>",
            layoutBackground: "<p:bg><p:bgPr><a:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></a:blipFill></p:bgPr></p:bg>",
            layoutRelationships: ImageRelationship("rId2", "../embedded/backdrop.png"), masterRelationships: ImageRelationship("rId2", "../embedded/logo.png"),
            media: new() { ["ppt/embedded/backdrop.png"] = Png, ["ppt/embedded/logo.png"] = Png });
        var (html, diagnostics) = await Export(package);
        var diagnostic = Assert.Single(diagnostics, item => item.Code == "SourceScenePictureUnavailable");
        Assert.StartsWith("2 picture(s)", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("/ppt/embedded/backdrop.png", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("/ppt/embedded/logo.png", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("source-shared-image-", html, StringComparison.Ordinal);
        // Images the user left out are left out of the scene without a finding.
        Assert.DoesNotContain((await Export(package, omitImages: true)).Diagnostics, item => item.Code == "SourceScenePictureUnavailable");
        Assert.DoesNotContain((await Export(DecoratedDeck())).Diagnostics, item => item.Code == "SourceScenePictureUnavailable");
    }

    [Fact]
    public void Master_text_is_drawn_in_the_scene_once_and_the_Markdown_keeps_it_as_before()
    {
        var extraction = new PptxAdapter().Extract(new MemoryStream(Deck(Box(2, "Body", Frame(100, 100, 300, 60)),
            master: Box(7, "Confidential", Frame(700, 660, 200, 40)) + Placeholder(2, "<p:ph type=\"title\"/>", "Master title prompt", Frame(10, 10, 100, 50)))));
        var node = Assert.Single(extraction.Graph.Partitions.Single().Nodes, n => n.Source?.Locators.Any(l => l.Kind == "shape_id" && l.Value == "master:7") == true);
        var scene = Assert.Single(Assert.Single(SourceDrawingSceneJson.Read(extraction.Graph.FormatExtensions![SourceDrawingSceneJson.ExtensionKey])).Value);
        var drawn = Assert.Single(scene.Objects, item => item.Text == "Confidential");
        // The scene object is the semantic node the adapter already had for the master's text: no second node, no second text.
        Assert.Equal(node.Id, drawn.SourceNodeId);
        Assert.Equal("/ppt/slideMasters/slideMaster1.xml", drawn.Source?.PartUri);
        Assert.Equal((700d, 660d), (Math.Round(drawn.Transform.Point(0, 0).X, 3), Math.Round(drawn.Transform.Point(0, 0).Y, 3)));
        Assert.True(scene.Objects.ToList().IndexOf(drawn) < scene.Objects.ToList().FindIndex(item => item.Text == "Body"));

        var html = ReadableHtmlWriter.WriteDocument(ReadableProjectionBuilder.Build(extraction.Graph).Document).Html;
        Assert.Single(Regex.Matches(html, "Confidential"));
        Assert.Matches("<div class=\"source-drawing-text\"[^>]*>(?:<[^>]*>)*Confidential", html);
        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);
        Assert.Single(Regex.Matches(markdown, "Confidential"));
        Assert.DoesNotContain("Master title prompt", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_takes_fill_text_and_edges_from_its_table_style_and_its_cells()
    {
        const string styleId = "{11111111-2222-3333-4444-555555555555}";
        static string Edge(string name, int width, string rgb) => "<a:" + name + "><a:ln w=\"" + width + "\">" + Solid(rgb) + "</a:ln></a:" + name + ">";
        var styles = "<a:tblStyleLst xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" def=\"" + styleId + "\"><a:tblStyle styleId=\"" + styleId + "\" styleName=\"Banded\">" +
            "<a:wholeTbl><a:tcTxStyle><a:schemeClr val=\"dk2\"/></a:tcTxStyle><a:tcStyle><a:tcBdr>" + Edge("left", 12700, "808080") + Edge("right", 12700, "808080") +
            Edge("top", 12700, "808080") + Edge("bottom", 12700, "808080") + Edge("insideH", 12700, "CCCCCC") + Edge("insideV", 12700, "CCCCCC") +
            "</a:tcBdr><a:fill>" + Solid("F2F2F2") + "</a:fill></a:tcStyle></a:wholeTbl>" +
            "<a:band1H><a:tcStyle><a:tcBdr/><a:fill><a:solidFill><a:schemeClr val=\"accent5\"/></a:solidFill></a:fill></a:tcStyle></a:band1H>" +
            "<a:band2H><a:tcStyle><a:tcBdr/></a:tcStyle></a:band2H>" +
            "<a:firstRow><a:tcTxStyle b=\"on\"><a:srgbClr val=\"FFFFFF\"/></a:tcTxStyle><a:tcStyle><a:tcBdr>" + Edge("bottom", 38100, "000000") +
            "</a:tcBdr><a:fill>" + Solid("1F4E79") + "</a:fill></a:tcStyle></a:firstRow></a:tblStyle></a:tblStyleLst>";
        var table = Table(5, "<a:tblPr firstRow=\"1\" bandRow=\"1\"><a:tableStyleId>" + styleId + "</a:tableStyleId></a:tblPr>",
            [Cell("Name"), Cell("Value")],
            [Cell("First band"), Cell("Own fill", properties: "<a:tcPr anchor=\"ctr\">" + Solid("FFFF00") + "</a:tcPr>")],
            [Cell("Second band", run: "<a:rPr sz=\"1400\" i=\"1\">" + Solid("FF0000") + "</a:rPr>"), Cell("Plain")],
            [Cell("Third row"), Cell("Last")]);
        var extraction = new PptxAdapter().Extract(new MemoryStream(Deck(table, parts: new() { ["ppt/tableStyles.xml"] = styles })));
        var html = ReadableHtmlWriter.WriteDocument(ReadableProjectionBuilder.Build(extraction.Graph).Document).Html;

        // First row: the style's bold white text on its fill, and its heavy bottom edge. Edges keep their source width: 12700 EMU are 1.33 px, 38100 are 4.
        var header = CellStyle(html, "Name");
        Assert.Equal("#1F4E79", header["background-color"]);
        Assert.Equal("#FFFFFF", header["color"]);
        Assert.Equal("bold", header["font-weight"]);
        Assert.Equal("4px solid #000000", header["border-bottom"]);
        Assert.Equal("1.33px solid #808080", header["border-left"]);
        Assert.Equal("1.33px solid #808080", header["border-top"]);
        Assert.Equal("1.33px solid #CCCCCC", header["border-right"]);
        Assert.Equal("18pt", header["font-size"]);
        // Bands count from the row after the first row: band1H (the theme's accent5), then band2H, which leaves the whole-table fill.
        var firstBand = CellStyle(html, "First band");
        Assert.Equal("#5B9BD5", firstBand["background-color"]);
        Assert.Equal("#112233", firstBand["color"]);
        Assert.Equal("normal", firstBand["font-weight"]);
        Assert.Equal("1.33px solid #CCCCCC", firstBand["border-bottom"]);
        Assert.Equal("#F2F2F2", CellStyle(html, "Plain")["background-color"]);
        Assert.Equal("#5B9BD5", CellStyle(html, "Third row")["background-color"]);
        Assert.Equal("1.33px solid #808080", CellStyle(html, "Last")["border-bottom"]);
        Assert.Equal("1.33px solid #808080", CellStyle(html, "Last")["border-right"]);
        // The cell's own fill and anchor, and the run's color, size and slant, win over the style.
        var ownFill = CellStyle(html, "Own fill");
        Assert.Equal("#FFFF00", ownFill["background-color"]);
        Assert.Equal("middle", ownFill["vertical-align"]);
        var ownText = CellStyle(html, "Second band");
        Assert.Equal("#FF0000", ownText["color"]);
        Assert.Equal("14pt", ownText["font-size"]);
        Assert.Equal("italic", ownText["font-style"]);
        Assert.Equal("#F2F2F2", ownText["background-color"]);
        // The source column widths (200 and 300 px).
        Assert.Contains("<col style=\"width:40%\"><col style=\"width:60%\">", html, StringComparison.Ordinal);

        // The Markdown table is the text only, as before.
        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);
        Assert.Contains("| Name | Value |", markdown, StringComparison.Ordinal);
        Assert.Contains("| First band | Own fill |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Second band | Plain |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("1F4E79", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_without_a_stored_definition_of_the_default_style_takes_the_built_in_one_and_no_style_draws_no_fill()
    {
        var styled = Html(Deck(Table(5, "<a:tblPr firstRow=\"1\" bandRow=\"1\"><a:tableStyleId>" + Medium2Accent1 + "</a:tableStyleId></a:tblPr>",
            [Cell("Name"), Cell("Value")], [Cell("One"), Cell("1")])));
        var header = CellStyle(styled, "Name");
        Assert.Equal("#C00000", header["background-color"]);
        Assert.Equal("#FFFFFF", header["color"]);
        Assert.Equal("bold", header["font-weight"]);
        Assert.Equal("4px solid #FFFFFF", header["border-bottom"]);
        Assert.Equal("normal", CellStyle(styled, "One")["font-weight"]);
        Assert.Equal("#000000", CellStyle(styled, "One")["color"]);

        var plain = Html(Deck(Table(5, "<a:tblPr/>", [Cell("Name"), Cell("Value")], [Cell("One"), Cell("1", properties: "<a:tcPr><a:lnB w=\"19050\">" + Solid("0070C0") + "</a:lnB></a:tcPr>")])));
        Assert.Equal("transparent", CellStyle(plain, "Name")["background-color"]);
        Assert.Equal("none", CellStyle(plain, "Name")["border-bottom"]);
        Assert.Equal("normal", CellStyle(plain, "Name")["font-weight"]);
        Assert.Equal("2px solid #0070C0", CellStyle(plain, "1")["border-bottom"]);
    }

    /// <summary>The declarations of the style class of the table cell that holds exactly <paramref name="text"/>.</summary>
    private static Dictionary<string, string> CellStyle(string html, string text)
    {
        var cell = Regex.Match(html, "<t[hd][^>]* class=\"[^\"]*(source-style-\\d+)[^\"]*\"[^>]*>" + Regex.Escape(text) + "</t[hd]>");
        Assert.True(cell.Success, "no styled table cell holds '" + text + "'");
        // The rule outranks the reader's own cell edges and its light and dark themes: a slide keeps its source colors.
        var rule = Regex.Match(html, "\\.direct-document \\.source-native-table tr>\\." + cell.Groups[1].Value + ",[^{]*#docredock-theme-dark:checked[^{]*\\{([^}]*)\\}");
        Assert.True(rule.Success, "the cell's style has no rule for a table inside a scene");
        return rule.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(d => d.Split(':', 2))
            .ToDictionary(d => d[0], d => d[1].Replace("!important", "", StringComparison.Ordinal), StringComparer.Ordinal);
    }

    private static string Html(byte[] package) =>
        ReadableHtmlWriter.WriteDocument(ReadableProjectionBuilder.Build(new PptxAdapter().Extract(new MemoryStream(package)).Graph).Document).Html;

    private async Task<string> ExportHtml(byte[] package) => (await Export(package)).Html;

    private async Task<(string Html, IReadOnlyList<DocRedock.Core.Reporting.Diagnostic> Diagnostics)> Export(byte[] package, bool omitImages = false)
    {
        Directory.CreateDirectory(root);
        var name = Guid.NewGuid().ToString("N");
        var source = Path.Combine(root, name + ".pptx"); var output = Path.Combine(root, name + ".html");
        await File.WriteAllBytesAsync(source, package);
        var result = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, output,
            OmitImages: omitImages, OutputFormat: ReadableOutputFormat.Html, OfficeLayout: OfficeRenderingMode.Structured));
        return (await File.ReadAllTextAsync(output), result.Diagnostics);
    }

    /// <summary>A master with a logo picture, a title placeholder and footer text, a layout with a green band, a slide with one box.</summary>
    private static byte[] DecoratedDeck(string slideAttributes = "", string layoutAttributes = "") => Deck(
        Box(2, "Body", Frame(100, 200, 300, 60), Solid("FFC000")),
        layout: Placeholder(2, "<p:ph type=\"title\"/>", "Layout title prompt", Frame(96, 48, 768, 120)) + Box(8, "", Frame(0, 680, 960, 40), Solid("00B050")),
        master: Placeholder(2, "<p:ph type=\"title\"/>", "Master title prompt", Frame(10, 10, 100, 50)) +
            "<p:pic><p:nvPicPr><p:cNvPr id=\"9\" name=\"Logo\" descr=\"Company logo\"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr>" +
            "<p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr>" + Frame(840, 20, 96, 48) +
            "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>",
        masterRelationships: ImageRelationship("rId2", "../media/logo.png"), media: new() { ["ppt/media/logo.png"] = Png },
        slideAttributes: slideAttributes, layoutAttributes: layoutAttributes);

    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private const string Namespaces = " xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"" +
        " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
    private const string RelationshipType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

    private static string Solid(string rgb) => "<a:solidFill><a:srgbClr val=\"" + rgb + "\"/></a:solidFill>";

    /// <summary>An a:xfrm in CSS pixels.</summary>
    private static string Frame(int x, int y, int width, int height) =>
        $"<a:xfrm><a:off x=\"{x * 9525}\" y=\"{y * 9525}\"/><a:ext cx=\"{width * 9525}\" cy=\"{height * 9525}\"/></a:xfrm>";

    private static string Paragraph(string text) => text.Length == 0 ? "<a:p/>" : "<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>" + text + "</a:t></a:r></a:p>";

    private static string Placeholder(int id, string placeholder, string text, string frame) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Placeholder {id}\"/><p:cNvSpPr/><p:nvPr>{placeholder}</p:nvPr></p:nvSpPr>" +
        $"<p:spPr>{frame}</p:spPr><p:txBody><a:bodyPr/><a:lstStyle/>{Paragraph(text)}</p:txBody></p:sp>";

    private static string Box(int id, string text, string frame, string fill = "") =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Box {id}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr>{frame}" +
        $"<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>{fill}</p:spPr><p:txBody><a:bodyPr/><a:lstStyle/>{Paragraph(text)}</p:txBody></p:sp>";

    private static string Cell(string text, string properties = "<a:tcPr/>", string run = "") =>
        "<a:tc><a:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r>" + run + "<a:t>" + text + "</a:t></a:r></a:p></a:txBody>" + properties + "</a:tc>";

    /// <summary>A two-column table (200 and 300 px wide) at (100, 100).</summary>
    private static string Table(int id, string properties, params string[][] rows) =>
        $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{id}\" name=\"Table {id}\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>" +
        $"<p:xfrm><a:off x=\"952500\" y=\"952500\"/><a:ext cx=\"4762500\" cy=\"{rows.Length * 381000}\"/></p:xfrm>" +
        "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\"><a:tbl>" + properties +
        "<a:tblGrid><a:gridCol w=\"1905000\"/><a:gridCol w=\"2857500\"/></a:tblGrid>" +
        string.Concat(rows.Select(row => "<a:tr h=\"381000\">" + string.Concat(row) + "</a:tr>")) + "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";

    private static string ImageRelationship(string id, string target) =>
        "<Relationship Id=\"" + id + "\" Type=\"" + RelationshipType + "image\" Target=\"" + target + "\"/>";

    private static byte[] Deck(string slide, string layout = "", string master = "", string slideBackground = "", string layoutBackground = "",
        string masterBackground = "", string slideAttributes = "", string layoutAttributes = "", string layoutRelationships = "", string masterRelationships = "",
        Dictionary<string, byte[]>? media = null, Dictionary<string, string>? parts = null, string? themeBackgroundFill = null)
    {
        const string relNs = "http://schemas.openxmlformats.org/package/2006/relationships";
        static string Tree(string background, string shapes) => "<p:cSld>" + background +
            "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>" + shapes + "</p:spTree></p:cSld>";
        static string Relationships(string kind, string target, string more = "") =>
            "<Relationships xmlns=\"" + relNs + "\"><Relationship Id=\"rId1\" Type=\"" + RelationshipType + kind + "\" Target=\"" + target + "\"/>" + more + "</Relationships>";
        var colors = string.Concat(new[] { ("dk1", "000000"), ("lt1", "FFFFFF"), ("dk2", "112233"), ("lt2", "EEEEEE"), ("accent1", "C00000"), ("accent2", "ED7D31"),
            ("accent3", "A5A5A5"), ("accent4", "FFC000"), ("accent5", "5B9BD5"), ("accent6", "70AD47"), ("hlink", "0563C1"), ("folHlink", "954F72") }
            .Select(color => "<a:" + color.Item1 + "><a:srgbClr val=\"" + color.Item2 + "\"/></a:" + color.Item1 + ">"));
        const string placeholderColor = "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>";
        var text = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"png\" ContentType=\"image/png\"/>" +
                "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/></Types>",
            ["_rels/.rels"] = Relationships("officeDocument", "ppt/presentation.xml"),
            ["ppt/presentation.xml"] = "<p:presentation" + Namespaces + "><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\"/></p:sldIdLst><p:sldSz cx=\"9144000\" cy=\"6858000\"/></p:presentation>",
            ["ppt/_rels/presentation.xml.rels"] = Relationships("slide", "slides/slide1.xml"),
            ["ppt/slides/slide1.xml"] = "<p:sld" + Namespaces + slideAttributes + ">" + Tree(slideBackground, slide) + "</p:sld>",
            ["ppt/slides/_rels/slide1.xml.rels"] = Relationships("slideLayout", "../slideLayouts/slideLayout1.xml"),
            ["ppt/slideLayouts/slideLayout1.xml"] = "<p:sldLayout" + Namespaces + layoutAttributes + ">" + Tree(layoutBackground, layout) + "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>",
            ["ppt/slideLayouts/_rels/slideLayout1.xml.rels"] = Relationships("slideMaster", "../slideMasters/slideMaster1.xml", layoutRelationships),
            ["ppt/slideMasters/slideMaster1.xml"] = "<p:sldMaster" + Namespaces + ">" + Tree(masterBackground, master) +
                "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
                "<p:txStyles/></p:sldMaster>",
            ["ppt/slideMasters/_rels/slideMaster1.xml.rels"] = Relationships("theme", "../theme/theme1.xml", masterRelationships),
            ["ppt/theme/theme1.xml"] = "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" name=\"Test\"><a:themeElements><a:clrScheme name=\"Test\">" + colors +
                "</a:clrScheme><a:fontScheme name=\"Test\"><a:majorFont><a:latin typeface=\"Arial\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
                "<a:minorFont><a:latin typeface=\"Arial\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
                "<a:fmtScheme name=\"Test\"><a:fillStyleLst>" + placeholderColor + placeholderColor + placeholderColor + "</a:fillStyleLst>" +
                "<a:lnStyleLst><a:ln w=\"9525\">" + placeholderColor + "</a:ln><a:ln w=\"9525\">" + placeholderColor + "</a:ln><a:ln w=\"9525\">" + placeholderColor + "</a:ln></a:lnStyleLst>" +
                "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
                "<a:bgFillStyleLst>" + placeholderColor + "<a:gradFill rotWithShape=\"1\"><a:gsLst><a:gs pos=\"0\"><a:schemeClr val=\"phClr\"/></a:gs>" +
                "<a:gs pos=\"100000\"><a:srgbClr val=\"FFFFFF\"/></a:gs></a:gsLst><a:lin ang=\"5400000\" scaled=\"0\"/></a:gradFill>" + (themeBackgroundFill ?? placeholderColor) +
                "</a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>",
        };
        foreach (var part in parts ?? new Dictionary<string, string>()) text[part.Key] = part.Value;
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var part in text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), new UTF8Encoding(false));
                writer.Write(part.Value);
            }
            foreach (var part in media ?? new Dictionary<string, byte[]>())
            {
                using var stream = zip.CreateEntry(part.Key).Open();
                stream.Write(part.Value);
            }
        }
        return output.ToArray();
    }
}
