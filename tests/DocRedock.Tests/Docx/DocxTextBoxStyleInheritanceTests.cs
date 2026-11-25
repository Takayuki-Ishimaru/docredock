using System.IO.Compression;
using System.Text;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Render;

namespace DocRedock.Tests.Docx;

/// <summary>The formatting a Word text box's paragraphs and runs inherit (document defaults, paragraph and character
/// styles with their basedOn chains) in the source drawing scene, and the line pitch rules exact and atLeast.</summary>
public sealed class DocxTextBoxStyleInheritanceTests : IDisposable
{
    private const string ShapeUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    private const string WordNamespace = "xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'";
    private readonly string root = Directory.CreateTempSubdirectory("docredock-docx-textbox-styles-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);
    public void Dispose() => Directory.Delete(root, true);

    [Fact(Timeout = 30000)]
    public async Task A_text_box_paragraph_takes_size_bold_colour_alignment_and_spacing_from_its_paragraph_style()
    {
        var paragraph = await OnlyParagraph("paragraph-style", Styles(
            "<w:style w:type='paragraph' w:styleId='Callout'><w:pPr><w:jc w:val='center'/><w:spacing w:before='120' w:after='240' w:line='360' w:lineRule='auto'/>" +
            "<w:ind w:left='720'/></w:pPr><w:rPr><w:b/><w:i/><w:u w:val='single'/><w:strike/><w:color w:val='C00000'/><w:sz w:val='40'/></w:rPr></w:style>"),
            "<w:p><w:pPr><w:pStyle w:val='Callout'/></w:pPr><w:r><w:t>Styled</w:t></w:r></w:p>");
        var run = Assert.Single(paragraph.Runs);
        // 40 half-points are 20 pt, 26.6667 px.
        Assert.Equal(26.6667, run.FontSize, 3);
        Assert.Equal((true, true, true, true, "#C00000"), (run.Bold, run.Italic, run.Underline, run.Strike, run.Color));
        Assert.Equal("ctr", paragraph.Alignment);
        Assert.Equal((8d, 16d, 150d, 48d), (Math.Round(paragraph.SpaceBefore!.Value, 3), Math.Round(paragraph.SpaceAfter!.Value, 3),
            Math.Round(paragraph.LineSpacingPercent!.Value, 3), Math.Round(paragraph.MarginLeft!.Value, 3)));
    }

    [Fact(Timeout = 30000)]
    public async Task A_style_inherits_what_it_does_not_state_from_the_styles_it_is_based_on()
    {
        var paragraph = await OnlyParagraph("based-on", Styles(
            "<w:style w:type='paragraph' w:styleId='Base'><w:pPr><w:jc w:val='right'/><w:spacing w:before='200' w:line='300' w:lineRule='exact'/><w:ind w:left='300' w:firstLine='150'/></w:pPr>" +
            "<w:rPr><w:b/><w:color w:val='0000FF'/><w:sz w:val='48'/></w:rPr></w:style>" +
            "<w:style w:type='paragraph' w:styleId='Middle'><w:basedOn w:val='Base'/><w:pPr><w:spacing w:after='100'/><w:ind w:hanging='150'/></w:pPr><w:rPr><w:sz w:val='32'/></w:rPr></w:style>" +
            "<w:style w:type='paragraph' w:styleId='Leaf'><w:basedOn w:val='Middle'/><w:rPr><w:i/></w:rPr></w:style>"),
            "<w:p><w:pPr><w:pStyle w:val='Leaf'/></w:pPr><w:r><w:t>Chain</w:t></w:r></w:p>");
        var run = Assert.Single(paragraph.Runs);
        Assert.Equal(21.3333, run.FontSize, 3);
        Assert.Equal((true, true, "#0000FF"), (run.Bold, run.Italic, run.Color));
        Assert.Equal("r", paragraph.Alignment);
        // w:spacing and w:ind are inherited attribute by attribute; a nearer hanging replaces the inherited firstLine.
        Assert.Equal((13.333, 6.667, 20d, 20d, -10d), (Math.Round(paragraph.SpaceBefore!.Value, 3), Math.Round(paragraph.SpaceAfter!.Value, 3),
            Math.Round(paragraph.LineSpacingExact!.Value, 3), Math.Round(paragraph.MarginLeft!.Value, 3), Math.Round(paragraph.Indent!.Value, 3)));
    }

    [Fact(Timeout = 30000)]
    public async Task A_cyclic_basedOn_chain_ends_instead_of_looping()
    {
        var paragraph = await OnlyParagraph("cycle", Styles(
            "<w:style w:type='paragraph' w:styleId='A'><w:basedOn w:val='B'/><w:rPr><w:sz w:val='40'/></w:rPr></w:style>" +
            "<w:style w:type='paragraph' w:styleId='B'><w:basedOn w:val='A'/><w:rPr><w:b/></w:rPr></w:style>"),
            "<w:p><w:pPr><w:pStyle w:val='A'/></w:pPr><w:r><w:t>Cycle</w:t></w:r></w:p>");
        var run = Assert.Single(paragraph.Runs);
        Assert.Equal(26.6667, run.FontSize, 3);
        Assert.True(run.Bold);
    }

    [Fact(Timeout = 30000)]
    public async Task Direct_formatting_overrides_the_style_and_a_direct_off_turns_style_bold_off()
    {
        var paragraph = await OnlyParagraph("direct", Styles(
            "<w:style w:type='paragraph' w:styleId='Loud'><w:pPr><w:jc w:val='center'/><w:spacing w:before='240' w:after='240'/></w:pPr>" +
            "<w:rPr><w:b/><w:i/><w:color w:val='FF0000'/><w:sz w:val='40'/></w:rPr></w:style>"),
            "<w:p><w:pPr><w:pStyle w:val='Loud'/><w:spacing w:before='60'/><w:jc w:val='left'/></w:pPr>" +
            "<w:r><w:rPr><w:b w:val='0'/><w:color w:val='00AA00'/><w:sz w:val='24'/></w:rPr><w:t>Direct</w:t></w:r><w:r><w:t>Style</w:t></w:r></w:p>");
        Assert.Equal(2, paragraph.Runs.Count);
        var direct = paragraph.Runs[0]; var styled = paragraph.Runs[1];
        Assert.Equal((false, true, "#00AA00"), (direct.Bold, direct.Italic, direct.Color));
        Assert.Equal(16, direct.FontSize, 3);
        Assert.Equal((true, true, "#FF0000"), (styled.Bold, styled.Italic, styled.Color));
        Assert.Equal(26.6667, styled.FontSize, 3);
        Assert.Equal("l", paragraph.Alignment);
        Assert.Equal((4d, 16d), (Math.Round(paragraph.SpaceBefore!.Value, 3), Math.Round(paragraph.SpaceAfter!.Value, 3)));
    }

    [Fact(Timeout = 30000)]
    public async Task A_character_style_applies_over_the_paragraph_style_and_toggles_its_bold()
    {
        var paragraph = await OnlyParagraph("character-style", Styles(
            "<w:style w:type='paragraph' w:styleId='Strong'><w:rPr><w:b/><w:sz w:val='40'/><w:color w:val='111111'/></w:rPr></w:style>" +
            "<w:style w:type='character' w:styleId='MarkBase'><w:rPr><w:b/><w:i/><w:color w:val='123456'/></w:rPr></w:style>" +
            "<w:style w:type='character' w:styleId='Mark'><w:basedOn w:val='MarkBase'/><w:rPr><w:sz w:val='24'/></w:rPr></w:style>"),
            "<w:p><w:pPr><w:pStyle w:val='Strong'/></w:pPr><w:r><w:rPr><w:rStyle w:val='Mark'/></w:rPr><w:t>Toggled</w:t></w:r>" +
            "<w:r><w:rPr><w:rStyle w:val='Mark'/><w:b/></w:rPr><w:t>Set</w:t></w:r></w:p>");
        var toggled = paragraph.Runs[0]; var set = paragraph.Runs[1];
        // ECMA-376 17.7.3: bold in the paragraph style and in the character style cancel; italic is stated once.
        Assert.Equal((false, true, "#123456"), (toggled.Bold, toggled.Italic, toggled.Color));
        Assert.Equal(16, toggled.FontSize, 3);
        Assert.True(set.Bold);
    }

    [Fact(Timeout = 30000)]
    public async Task Document_defaults_and_the_default_paragraph_style_reach_a_text_box_without_a_style_of_its_own()
    {
        var styles = "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:asciiTheme='minorHAnsi' w:hAnsiTheme='minorHAnsi' w:eastAsia='Yu Mincho'/><w:i/><w:sz w:val='18'/><w:color w:val='336699'/></w:rPr></w:rPrDefault>" +
            "<w:pPrDefault><w:pPr><w:spacing w:after='160' w:line='259' w:lineRule='auto'/><w:jc w:val='both'/></w:pPr></w:pPrDefault></w:docDefaults>";
        var defaults = await OnlyParagraph("doc-defaults", Styles(styles), "<w:p><w:r><w:t>Defaults</w:t></w:r></w:p>", Theme);
        var run = Assert.Single(defaults.Runs);
        Assert.Equal(12, run.FontSize, 3);
        Assert.Equal((false, true, "#336699", "Georgia"), (run.Bold, run.Italic, run.Color, run.FontFamily));
        Assert.Equal(("just", 10.667, 107.917), (defaults.Alignment, Math.Round(defaults.SpaceAfter!.Value, 3), Math.Round(defaults.LineSpacingPercent!.Value, 3)));

        var normal = await OnlyParagraph("default-style", Styles(styles +
            "<w:style w:type='paragraph' w:default='1' w:styleId='Normal'><w:pPr><w:jc w:val='center'/></w:pPr><w:rPr><w:rFonts w:ascii='Verdana' w:hAnsi='Verdana'/><w:b/><w:sz w:val='28'/></w:rPr></w:style>"),
            "<w:p><w:r><w:t>Normal</w:t></w:r></w:p>", Theme);
        run = Assert.Single(normal.Runs);
        Assert.Equal(18.6667, run.FontSize, 3);
        // The style's own font replaces the default's theme font; what it does not state still comes from the defaults.
        Assert.Equal((true, true, "#336699", "Verdana"), (run.Bold, run.Italic, run.Color, run.FontFamily));
        Assert.Equal(("ctr", 10.667), (normal.Alignment, Math.Round(normal.SpaceAfter!.Value, 3)));
    }

    [Fact(Timeout = 30000)]
    public async Task Tab_stops_of_the_style_chain_merge_by_position_and_a_nearer_clear_removes_a_stop()
    {
        // The stops are stated by styles only: a paragraph's own w:tabs are counted as tab characters by the text of the
        // text-box node, and a caption that differs from that text is drawn from the node's plain text instead.
        var paragraph = await OnlyParagraph("tabs", Styles(
            "<w:style w:type='paragraph' w:styleId='TabBase'><w:pPr><w:tabs><w:tab w:val='left' w:pos='720'/><w:tab w:val='center' w:pos='1440'/></w:tabs></w:pPr></w:style>" +
            "<w:style w:type='paragraph' w:styleId='Tabbed'><w:basedOn w:val='TabBase'/><w:pPr><w:tabs><w:tab w:val='clear' w:pos='720'/><w:tab w:val='right' w:pos='3000'/></w:tabs></w:pPr></w:style>"),
            "<w:p><w:pPr><w:pStyle w:val='Tabbed'/></w:pPr><w:r><w:t>Tabbed</w:t></w:r></w:p>");
        Assert.Equal([new DrawingTabStop(96, "ctr"), new DrawingTabStop(200, "r")], paragraph.TabStops);
    }

    [Fact(Timeout = 30000)]
    public async Task A_text_box_in_a_document_without_a_styles_part_keeps_its_direct_formatting_only()
    {
        var paragraph = await OnlyParagraph("no-styles", null,
            "<w:p><w:pPr><w:pStyle w:val='Missing'/><w:jc w:val='center'/></w:pPr><w:r><w:rPr><w:b/><w:sz w:val='40'/></w:rPr><w:t>Direct</w:t></w:r><w:r><w:t>Plain</w:t></w:r></w:p>");
        Assert.Equal("ctr", paragraph.Alignment);
        Assert.Equal((true, 26.6667), (paragraph.Runs[0].Bold, Math.Round(paragraph.Runs[0].FontSize, 4)));
        Assert.Equal(new DrawingTextRun { Text = "Plain" }, paragraph.Runs[1]);
        Assert.Null(paragraph.LineSpacingAtLeast);
    }

    [Theory(Timeout = 30000)]
    // 12 pt text has a natural line of about 18.4 px (1.15 times 16 px). A lower minimum changes nothing, a higher one is the line.
    [InlineData("atLeast", 200, null, 13.333, null)]
    [InlineData("atLeast", 600, null, 40d, "line-height:40px")]
    // An exact pitch is the line whatever the text needs.
    [InlineData("exact", 200, 13.333, null, "line-height:13.3333px")]
    [InlineData("exact", 600, 40d, null, "line-height:40px")]
    public async Task A_minimum_line_pitch_yields_to_the_natural_line_and_an_exact_one_does_not(string rule, int line, double? exact, double? least, string? css)
    {
        var name = rule + line.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var output = await Export(WriteDocx(name, Styles(""), Box(
            $"<w:p><w:pPr><w:spacing w:line='{line}' w:lineRule='{rule}'/></w:pPr><w:r><w:rPr><w:sz w:val='24'/></w:rPr><w:t>Pitch</w:t></w:r></w:p>")));
        var paragraph = Assert.Single(Assert.Single(Scenes(output.Graph).SelectMany(scene => scene.Objects)).Paragraphs);
        Assert.Equal(exact, paragraph.LineSpacingExact is { } e ? Math.Round(e, 3) : null);
        Assert.Equal(least, paragraph.LineSpacingAtLeast is { } l ? Math.Round(l, 3) : null);
        Assert.Null(paragraph.LineSpacingPercent);
        var start = output.Html.IndexOf("<div class=\"source-caption-paragraph\"", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var style = output.Html[start..output.Html.IndexOf('>', start)].TrimEnd('"');
        if (css is null) Assert.DoesNotContain("line-height", style, StringComparison.Ordinal);
        else Assert.Contains(css + ";", style + ";", StringComparison.Ordinal);
    }

    [Fact(Timeout = 30000)]
    public async Task A_minimum_line_pitch_between_the_natural_lines_of_mixed_sizes_and_one_from_a_style()
    {
        // 300 twentieths are 20 px: above the natural line of the 12 pt run (18.4 px), below that of the 24 pt run (36.8 px).
        var output = await Export(WriteDocx("mixed", Styles(
            "<w:style w:type='paragraph' w:styleId='Loose'><w:pPr><w:spacing w:line='300' w:lineRule='atLeast'/></w:pPr></w:style>"), Box(
            "<w:p><w:pPr><w:pStyle w:val='Loose'/></w:pPr><w:r><w:rPr><w:sz w:val='24'/></w:rPr><w:t>Small</w:t></w:r><w:r><w:rPr><w:sz w:val='48'/></w:rPr><w:t>Large</w:t></w:r></w:p>")));
        var paragraph = Assert.Single(Assert.Single(Scenes(output.Graph).SelectMany(scene => scene.Objects)).Paragraphs);
        Assert.Equal(20, paragraph.LineSpacingAtLeast!.Value, 3);
        Assert.Null(paragraph.LineSpacingExact);
        Assert.Contains("line-height:36.8px", output.Html, StringComparison.Ordinal);
    }

    private const string Theme = "<a:theme xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main' name='T'><a:themeElements>" +
        "<a:clrScheme name='T'><a:dk1><a:srgbClr val='000000'/></a:dk1><a:lt1><a:srgbClr val='FFFFFF'/></a:lt1><a:dk2><a:srgbClr val='1F1F1F'/></a:dk2><a:lt2><a:srgbClr val='EEEEEE'/></a:lt2>" +
        "<a:accent1><a:srgbClr val='4472C4'/></a:accent1><a:accent2><a:srgbClr val='ED7D31'/></a:accent2><a:accent3><a:srgbClr val='A5A5A5'/></a:accent3><a:accent4><a:srgbClr val='FFC000'/></a:accent4>" +
        "<a:accent5><a:srgbClr val='5B9BD5'/></a:accent5><a:accent6><a:srgbClr val='70AD47'/></a:accent6><a:hlink><a:srgbClr val='0563C1'/></a:hlink><a:folHlink><a:srgbClr val='954F72'/></a:folHlink></a:clrScheme>" +
        "<a:fontScheme name='T'><a:majorFont><a:latin typeface='Impact'/><a:ea typeface=''/><a:cs typeface=''/></a:majorFont>" +
        "<a:minorFont><a:latin typeface='Georgia'/><a:ea typeface=''/><a:cs typeface=''/></a:minorFont></a:fontScheme>" +
        "<a:fmtScheme name='T'><a:fillStyleLst/><a:lnStyleLst/><a:effectStyleLst/><a:bgFillStyleLst/></a:fmtScheme></a:themeElements></a:theme>";

    [Fact(Timeout = 30000)]
    public async Task Direct_tab_stops_of_a_text_box_paragraph_reach_the_scene()
    {
        var paragraph = await OnlyParagraph("direct-tabs", null,
            "<w:p><w:pPr><w:tabs><w:tab w:val='right' w:pos='3000'/></w:tabs></w:pPr><w:r><w:t>A</w:t></w:r><w:r><w:tab/><w:t>B</w:t></w:r></w:p>");
        Assert.Equal([new DrawingTabStop(200, "r")], paragraph.TabStops);
        Assert.Equal("A	B", string.Concat(paragraph.Runs.Select(run => run.Text)));
    }

    [Fact(Timeout = 30000)]
    public async Task Tab_stop_definitions_of_a_body_paragraph_are_no_tab_characters_of_its_text()
    {
        var output = await Export(WriteDocx("body-tabs", null,
            "<w:p><w:pPr><w:tabs><w:tab w:val='left' w:pos='720'/><w:tab w:val='right' w:pos='3000'/></w:tabs></w:pPr><w:r><w:t>Name</w:t></w:r><w:r><w:tab/><w:t>Value</w:t></w:r></w:p>"));
        var node = Assert.Single(output.Graph.Nodes, n => n.Kind == NodeKind.Paragraph);
        var text = node.Content switch
        {
            TextNodeContent plain => plain.Text,
            RichTextNodeContent rich => string.Concat(rich.Runs.Select(run => run.Text)),
            _ => null,
        };
        Assert.Equal("Name	Value", text);
    }

    private static string Styles(string content) => $"<w:styles {WordNamespace}>{content}</w:styles>";

    private static string Box(string paragraphs) =>
        "<w:p><w:r><w:drawing><wp:inline><wp:extent cx='2857500' cy='952500'/><wp:docPr id='7' name='Text Box 7'/><wp:cNvGraphicFramePr/>" +
        $"<a:graphic><a:graphicData uri='{ShapeUri}'><wps:wsp><wps:cNvSpPr txBox='1'/><wps:spPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='2857500' cy='952500'/></a:xfrm>" +
        "<a:prstGeom prst='rect'><a:avLst/></a:prstGeom></wps:spPr><wps:txbx><w:txbxContent>" + paragraphs +
        "</w:txbxContent></wps:txbx><wps:bodyPr/></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>";

    private async Task<DrawingTextParagraph> OnlyParagraph(string name, string? styles, string paragraph, string? theme = null)
    {
        var output = await Export(WriteDocx(name, styles, Box(paragraph), theme));
        return Assert.Single(Assert.Single(Scenes(output.Graph).SelectMany(scene => scene.Objects)).Paragraphs);
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
        var htmlPath = Path.ChangeExtension(source, ".html");
        var html = await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, htmlPath,
            ContentPolicy: "visible", Language: "en", OutputFormat: ReadableOutputFormat.Html));
        return new(html.Graph, await File.ReadAllTextAsync(htmlPath));
    }

    private string WriteDocx(string name, string? styles, string body, string? theme = null)
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
        Write("word/document.xml", $"<w:document {WordNamespace} xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships' xmlns:wp='http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main' xmlns:wps='http://schemas.microsoft.com/office/word/2010/wordprocessingShape'><w:body>" + body + "<w:sectPr/></w:body></w:document>");
        if (styles is not null) Write("word/styles.xml", styles);
        if (theme is not null) Write("word/theme/theme1.xml", theme);
        return path;
    }

    private sealed record Exported(DocumentGraph Graph, string Html);
}
