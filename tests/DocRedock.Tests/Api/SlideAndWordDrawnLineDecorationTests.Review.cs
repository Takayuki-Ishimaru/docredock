using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Pptx;
using DocRedock.Markdown;
using DocRedock.Render;
using Markdig;

namespace DocRedock.Tests.Api;

// What an independent review of the lines drawn over slide and Word text found in the integrated work: a line is a decoration of text only
// where a viewer sees it over the text, a code-style paragraph keeps the strike of a line through it, a strike over runs of mixed formatting
// is written once, the offsets of a drawn line are those of the text the node holds, and distributed text is spread over all of its line.
public sealed partial class SlideAndWordDrawnLineDecorationTests
{
    // ---- a line is a decoration only where a viewer sees it over the text -------------------------------------------

    private const string Gray = "<a:solidFill><a:srgbClr val=\"DDDDDD\"/></a:solidFill>";

    // A fill from the style of the shape (a:fillRef), as PowerPoint writes the shapes it draws.
    private const string FillStyle = "<p:style><a:lnRef idx=\"0\"><a:schemeClr val=\"accent1\"/></a:lnRef><a:fillRef idx=\"1\"><a:schemeClr val=\"accent1\"/></a:fillRef>" +
                                     "<a:effectRef idx=\"0\"><a:schemeClr val=\"accent1\"/></a:effectRef><a:fontRef idx=\"minor\"><a:schemeClr val=\"tx1\"/></a:fontRef></p:style>";

    // The default text box (it has no fill) painted with a fill of its own.
    private static string FilledText() => DefaultText().Replace("<a:noFill/></p:spPr>", Gray + "</p:spPr>", StringComparison.Ordinal);

    // A box with its text in the middle of it, centered: a line from the middle of one to the middle of the next goes through the text.
    private static string CenteredBox(int id, double x, double y, double width, double height, string text) =>
        Box(id, x, y, width, height, text).Replace("<a:bodyPr/>", "<a:bodyPr anchor=\"ctr\"/>", StringComparison.Ordinal)
            .Replace("<a:p><a:r>", "<a:p><a:pPr algn=\"ctr\"/><a:r>", StringComparison.Ordinal);

    [Fact]
    public async Task A_line_drawn_before_a_filled_shape_is_out_of_sight_there_and_is_no_strikethrough_of_its_text()
    {
        // The line is first in the shape tree, so it lies under the shape, and the shape's fill hides it where its text is: a viewer sees
        // no strikethrough, and the line stays the unresolved line it was.
        var behind = Presentation("behind.pptx", SlideLine(5, 70, 116, 280) + FilledText());
        var hidden = ExtractSlides(behind);
        Assert.Null(StrikeRanges(hidden));
        Assert.True(HasUnresolvedLine(hidden));
        Assert.DoesNotContain("~~", (await Export(behind)).Markdown, StringComparison.Ordinal);

        // After the shape the same line lies over the text, whatever the fill is: it strikes it.
        Assert.Equal("[[0,18]]", StrikeRanges(ExtractSlides(Presentation("above.pptx", FilledText() + SlideLine(5, 70, 116, 280)))));
        // Under a shape with no fill it shows through the text.
        Assert.Equal("[[0,18]]", StrikeRanges(ExtractSlides(Presentation("through.pptx", SlideLine(5, 70, 116, 280) + DefaultText()))));
        // The fill may come from the style of the shape: it hides the line too...
        var styled = DefaultText().Replace("<a:noFill/></p:spPr>", "</p:spPr>" + FillStyle, StringComparison.Ordinal);
        Assert.Null(StrikeRanges(ExtractSlides(Presentation("styled.pptx", SlideLine(5, 70, 116, 280) + styled))));
        // ...unless the shape switches its fill off.
        var switchedOff = DefaultText().Replace("</p:spPr>", "</p:spPr>" + FillStyle, StringComparison.Ordinal);
        Assert.Equal("[[0,18]]", StrikeRanges(ExtractSlides(Presentation("switchedoff.pptx", SlideLine(5, 70, 116, 280) + switchedOff))));
    }

    [Fact]
    public async Task An_axis_drawn_behind_filled_boxes_stays_a_line_and_strikes_none_of_their_labels()
    {
        // A timeline: the axis comes first, the milestones are drawn over it. Its height is that of a strikethrough of their 18 pt labels.
        var labels = new[] { "Plan", "Build", "Test", "Ship" };
        var boxes = string.Concat(labels.Select((label, index) => Box(10 + index, 70 + index * 160, 200, 90, 50, label)));
        var behind = Presentation("axisbehind.pptx", SlideLine(5, 40, 215, 680) + boxes);
        var hidden = ExtractSlides(behind);
        foreach (var index in Enumerable.Range(0, labels.Length)) Assert.Null(StrikeRanges(hidden, "Box " + (10 + index)));
        Assert.True(HasUnresolvedLine(hidden));
        var markdown = (await Export(behind)).Markdown;
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);
        foreach (var label in labels) Assert.Contains(label, markdown, StringComparison.Ordinal);

        // Drawn after the boxes the same line is over their labels: it strikes each of them.
        var over = ExtractSlides(Presentation("axisover.pptx", boxes + SlideLine(5, 40, 215, 680)));
        Assert.Equal("[[0,4]]", StrikeRanges(over, "Box 10"));
        Assert.Equal("[[0,4]]", StrikeRanges(over, "Box 12"));
        Assert.False(HasUnresolvedLine(over));
    }

    [Fact]
    public async Task A_line_drawn_between_two_boxes_behind_them_is_still_the_edge_of_their_diagram()
    {
        // An unglued line from the middle of one box to the middle of the next, drawn first: the boxes lie over it, and it joins them.
        var behind = Presentation("edgebehind.pptx", SlideLine(5, 152, 180, 480) + CenteredBox(10, 72, 150, 160, 60, "Start") + CenteredBox(11, 400, 150, 160, 60, "End"));
        var (markdown, result) = await Export(behind);
        Assert.DoesNotContain("~~", markdown, StringComparison.Ordinal);
        Assert.Contains("flowchart", markdown, StringComparison.Ordinal);
        Assert.Contains("[Start]", markdown, StringComparison.Ordinal);
        Assert.Contains("[End]", markdown, StringComparison.Ordinal);
        Assert.Empty(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages);
        // Drawn after them it lies over the text of both, and strikes the half of each that it covers: not a decoration it can place.
        var over = ExtractSlides(Presentation("edgeover.pptx", CenteredBox(10, 72, 150, 160, 60, "Start") + CenteredBox(11, 400, 150, 160, 60, "End") + SlideLine(5, 152, 180, 480)));
        Assert.Equal("true", Unplaced(over, "Box 10"));
    }

    // The table of two rows at (72, 200) with a table style: the default one of PowerPoint, which a file need not define, and the one without.
    private const string DefaultTableStyle = "{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}";
    private const string NoStyleNoGrid = "{2D5ABB26-0587-4C30-8999-92F81FD0307C}";

    private static string StyledTable(string styleId, string cellProperties = "") =>
        SlideTable("Item", "Old plan")
            .Replace("<a:tbl><a:tblGrid>", $"<a:tbl><a:tblPr firstRow=\"1\" bandRow=\"1\"><a:tableStyleId>{styleId}</a:tableStyleId></a:tblPr><a:tblGrid>", StringComparison.Ordinal)
            .Replace("<a:tcPr/>", $"<a:tcPr>{cellProperties}</a:tcPr>", StringComparison.Ordinal);

    private static string WithPart(string path, string entry, string text)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        zip.GetEntry(entry)?.Delete();
        using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
        writer.Write(text);
        return path;
    }

    [Fact]
    public async Task A_line_drawn_before_a_table_is_out_of_sight_where_the_cells_have_a_fill()
    {
        const string strike = "| ~~Old plan~~ | 800 |";
        static string Line() => SlideLine(5, 76, 251.7, 150);
        // The table style of PowerPoint's own that fills its cells (the file need not define it): the line under the table is hidden by it.
        Assert.DoesNotContain("~~", (await Export(Presentation("tablebehind.pptx", Line() + StyledTable(DefaultTableStyle)))).Markdown, StringComparison.Ordinal);
        // Over the table, or under a table with no fill, it strikes the cell.
        Assert.Contains(strike, (await Export(Presentation("tableabove.pptx", StyledTable(DefaultTableStyle) + Line()))).Markdown, StringComparison.Ordinal);
        Assert.Contains(strike, (await Export(Presentation("tablenostyle.pptx", Line() + StyledTable(NoStyleNoGrid)))).Markdown, StringComparison.Ordinal);
        Assert.Contains(strike, (await Export(Presentation("tablenone.pptx", Line() + SlideTable("Item", "Old plan")))).Markdown, StringComparison.Ordinal);
        // A cell that says it has no fill lets the line show through, whatever its table style gives; one with a fill of its own hides it.
        Assert.Contains(strike, (await Export(Presentation("cellnofill.pptx", Line() + StyledTable(DefaultTableStyle, "<a:noFill/>")))).Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("~~", (await Export(Presentation("cellfill.pptx", Line() + StyledTable(NoStyleNoGrid, Gray)))).Markdown, StringComparison.Ordinal);

        // A style the file defines: with a fill in it the table paints, without one it does not.
        const string StyleId = "{11111111-2222-3333-4444-555555555555}";
        static string StyleList(string wholeTable) => "<a:tblStyleLst xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" def=\"" + DefaultTableStyle + "\">" +
            $"<a:tblStyle styleId=\"{StyleId}\" styleName=\"Mine\"><a:wholeTbl><a:tcStyle><a:tcBdr/>{wholeTable}</a:tcStyle></a:wholeTbl></a:tblStyle></a:tblStyleLst>";
        var filled = WithPart(Presentation("styledefined.pptx", Line() + StyledTable(StyleId)), "ppt/tableStyles.xml", StyleList("<a:fill>" + Gray + "</a:fill>"));
        Assert.DoesNotContain("~~", (await Export(filled)).Markdown, StringComparison.Ordinal);
        var plain = WithPart(Presentation("styleplain.pptx", Line() + StyledTable(StyleId)), "ppt/tableStyles.xml", StyleList("<a:fill><a:noFill/></a:fill>"));
        Assert.Contains(strike, (await Export(plain)).Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_placeholder_lies_on_the_fill_its_layout_or_master_gives_it()
    {
        // A title that says nothing of its fill or its frame, on a layout and master whose titles may. The line is first or last in the tree.
        const string Ns = "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
        var frame = $"<a:xfrm><a:off x=\"{Emu(72)}\" y=\"{Emu(100)}\"/><a:ext cx=\"{Emu(400)}\" cy=\"{Emu(60)}\"/></a:xfrm>";
        static string Placeholder(string frame, string fill) =>
            "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr>" +
            $"<p:spPr>{frame}{fill}</p:spPr><p:txBody><a:bodyPr lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"0\" anchor=\"t\" wrap=\"none\"/><a:lstStyle/><a:p><a:endParaRPr lang=\"en-US\"/></a:p></p:txBody></p:sp>";
        const string title = "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title 1\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>" +
                             "<a:p><a:r><a:rPr lang=\"en-US\" sz=\"2400\"/><a:t>Old price 1000 yen</a:t></a:r></a:p></p:txBody></p:sp>";
        string Kind(string name, string layoutFill, string masterFill, bool lineFirst)
        {
            var line = SlideLine(5, 70, 116, 280);
            var parts = new Dictionary<string, string>
            {
                ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>",
                ["_rels/.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>",
                ["ppt/presentation.xml"] = "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\"/></p:sldIdLst></p:presentation>",
                ["ppt/_rels/presentation.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"slide\" Target=\"slides/slide1.xml\"/></Relationships>",
                ["ppt/slides/_rels/slide1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/></Relationships>",
                ["ppt/slideLayouts/_rels/slideLayout1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>",
                ["ppt/slideLayouts/slideLayout1.xml"] = $"<p:sldLayout {Ns}><p:cSld><p:spTree>{Placeholder(frame, layoutFill)}</p:spTree></p:cSld></p:sldLayout>",
                ["ppt/slideMasters/slideMaster1.xml"] = $"<p:sldMaster {Ns}><p:cSld><p:spTree>{Placeholder(frame, masterFill)}</p:spTree></p:cSld>" +
                                                       "<p:txStyles><p:titleStyle/><p:bodyStyle/><p:otherStyle/></p:txStyles></p:sldMaster>",
                ["ppt/slides/slide1.xml"] = $"<p:sld {Ns}><p:cSld><p:spTree>{(lineFirst ? line + title : title + line)}</p:spTree></p:cSld></p:sld>",
            };
            return StrikeRanges(ExtractSlides(Write(name, parts)), "Title 1") is null ? "none" : "strike";
        }
        Assert.Equal("none", Kind("layoutfill.pptx", Gray, "", lineFirst: true));
        Assert.Equal("none", Kind("masterfill.pptx", "", Gray, lineFirst: true));
        Assert.Equal("strike", Kind("nofill.pptx", "", "", lineFirst: true));
        // The layout may switch off the fill of the master; and a line over the title is its strikethrough, filled or not.
        Assert.Equal("strike", Kind("layoutnofill.pptx", "<a:noFill/>", Gray, lineFirst: true));
        Assert.Equal("strike", Kind("lineover.pptx", Gray, Gray, lineFirst: false));
    }

    // ---- a code-style paragraph keeps the strike of the line drawn through it --------------------------------------

    [Fact]
    public async Task A_line_drawn_through_a_code_style_paragraph_strikes_it_as_a_paragraph_and_other_code_stays_a_code_block()
    {
        string Code(string text, string lines = "") => WordParagraph(text, lines, "<w:pStyle w:val=\"SourceCode\"/>");
        const string codeStyle = "<w:style w:type=\"paragraph\" w:styleId=\"SourceCode\"><w:name w:val=\"Source Code\"/><w:basedOn w:val=\"Normal\"/></w:style></w:styles>";
        // "DELETE FROM users" is 103.2 pt wide at 12 pt: the lines cover it.
        var body = Code("DELETE FROM users", WordLine(-2, WordStrike, 106)) + Code("SELECT 1") + Code("DROP TABLE users", WordLine(0, WordUnderline, 106));
        var source = Document("codestrike.docx", body, styles: Styles.Replace("</w:styles>", codeStyle, StringComparison.Ordinal));
        var (markdown, result) = await Export(source);
        Assert.Contains("\n~~DELETE FROM users~~\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\n<u>DROP TABLE users</u>\n", markdown, StringComparison.Ordinal);
        // The paragraph with no line through it is still a code block.
        Assert.Contains("\n```\nSELECT 1\n```\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("```\nDELETE", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("```\nDROP", markdown, StringComparison.Ordinal);
        Assert.Empty(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages);
        // The node is a code block all the same.
        var node = ExtractDocument(source).Graph.Nodes.First(item => item.Kind == NodeKind.CodeBlock);
        Assert.Equal("[[0,17]]", Extension(node, "strike_ranges"));
    }

    private static Dictionary<string, JsonElement> RangesOf(string key, int start, int end) => new() { [key] = JsonSerializer.SerializeToElement(new[] { new[] { start, end } }) };

    private static string SerializeNodes(params DocumentNode[] nodes) =>
        new ReadableMarkdownSerializer().Serialize(new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "review", DocumentFormatKind.Docx,
            [new DocumentPartition("document", 0, nodes)])).Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void A_code_block_with_ranges_is_written_as_a_paragraph_with_its_marks_whatever_holds_its_text()
    {
        var plain = new DocumentNode("a", NodeKind.CodeBlock, null, 0, ContentLayer.Body, new TextNodeContent("rm -rf build"), Extensions: RangesOf("strike_ranges", 0, 6));
        var rich = new DocumentNode("b", NodeKind.CodeBlock, null, 1, ContentLayer.Body,
            new RichTextNodeContent([new TextRun("git "), new TextRun("push", Bold: true)]), Extensions: RangesOf("underline_ranges", 0, 8));
        var untouched = new DocumentNode("c", NodeKind.CodeBlock, null, 2, ContentLayer.Body, new TextNodeContent("ls -l"));
        var markdown = SerializeNodes(plain, rich, untouched);
        Assert.Contains("\n~~rm -rf~~ build\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\n<u>git **push**</u>\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\n```\nls -l\n```\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("```\nrm", markdown, StringComparison.Ordinal);
    }

    // ---- a strike over runs of mixed formatting is written once ------------------------------------------------------

    // What a Markdown renderer (GFM, with strikethrough) makes of the Markdown.
    private static readonly MarkdownPipeline StrikePipeline = new MarkdownPipelineBuilder().UsePipeTables()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough).Build();

    private static string Gfm(string markdown) => Markdig.Markdown.ToHtml(markdown, StrikePipeline).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    private static DocumentNode RichParagraph(params TextRun[] runs) => new("p", NodeKind.Paragraph, null, 0, ContentLayer.Body, new RichTextNodeContent(runs));

    [Fact]
    public void A_stretch_of_struck_text_is_written_inside_one_pair_of_tildes_around_the_formatting_of_its_runs()
    {
        var struck = RichParagraph(new TextRun("本日の", Strike: true), new TextRun("会議", Bold: true, Strike: true), new TextRun("は中止", Strike: true));
        var markdown = SerializeNodes(struck);
        Assert.Contains("\n~~本日の**会議**は中止~~\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("~~~~", markdown, StringComparison.Ordinal);
        // GFM reads one strike with a strong inside it; the adjacent pairs the runs used to get it prints as literal tildes.
        Assert.Equal("<p><del>本日の<strong>会議</strong>は中止</del></p>", Gfm("~~本日の**会議**は中止~~"));
        Assert.Contains("~~", Gfm("~~本日の~~~~**会議**~~~~は中止~~"), StringComparison.Ordinal);
        // The preview (the project's own reader) keeps the whole text struck, in one piece.
        Assert.Single(Regex.Matches(MarkdownInlineParser.Html("~~本日の**会議**は中止~~"), "<del>"));
        Assert.All(MarkdownInlineParser.Runs("~~本日の**会議**は中止~~"), run => Assert.True(run.Strike));
        // The round-trip projection is written by the shared writer, which writes each run on its own, as it always has.
        Assert.Equal("~~本日の~~~~**会議**~~~~は中止~~", DocRedockInlineMarkdown.Serialize(((RichTextNodeContent)struck.Content).Runs));
    }

    [Fact]
    public void Stretches_of_struck_and_underlined_text_nest_the_underline_outside_and_keep_white_space_outside_the_marks()
    {
        var link = "https://example.test/x";
        var cases = new (TextRun[] Runs, string Markdown, string Html)[]
        {
            // An underlined stretch is written once; the strike inside it is a stretch of its own.
            ([new("x ", Underline: true), new("y", Underline: true, Strike: true), new("z", Bold: true, Underline: true, Strike: true), new(" w", Underline: true)],
                "<u>x ~~y**z**~~ w</u>", "<p><u>x <del>y<strong>z</strong></del> w</u></p>"),
            ([new("a", Strike: true), new(" ", Strike: true), new("b", Italic: true, Strike: true)], "~~a _b_~~", "<p><del>a <em>b</em></del></p>"),
            // White space at the ends of a stretch is left outside the delimiters.
            ([new("x"), new(" a ", Strike: true), new("b", Bold: true, Strike: true), new(" y")], "x ~~a **b**~~ y", "<p>x <del>a <strong>b</strong></del> y</p>"),
            ([new("a", Underline: true), new("b", Bold: true, Underline: true)], "<u>a**b**</u>", "<p><u>a<strong>b</strong></u></p>"),
            ([new("a", Strike: true), new("x", Code: true, Strike: true)], "~~a`x`~~", "<p><del>a<code>x</code></del></p>"),
            // A line break ends a stretch, as it has always been written beside the runs.
            ([new("a", Strike: true), new("\n", Strike: true, Kind: TextRunKind.LineBreak), new("b", Strike: true)], "~~a~~<br>~~b~~", "<p><del>a</del><br><del>b</del></p>"),
            // A link is written around the stretch inside it, as ever; so is a colour or a highlight, and what they wrap is one stretch too.
            ([new("a", Strike: true), new("link", Strike: true, LinkTarget: link), new("b", Strike: true)], $"~~a~~[~~link~~]({link})~~b~~",
                $"<p><del>a</del><a href=\"{link}\"><del>link</del></a><del>b</del></p>"),
            ([new("a", Strike: true, LinkTarget: link), new("b", Bold: true, Strike: true, LinkTarget: link)], $"[~~a**b**~~]({link})",
                $"<p><a href=\"{link}\"><del>a<strong>b</strong></del></a></p>"),
            ([new("x", Strike: true, Color: "FF0000"), new("y", Bold: true, Strike: true, Color: "FF0000")], "<span style=\"color:#FF0000\">~~x**y**~~</span>",
                "<p><span style=\"color:#FF0000\"><del>x<strong>y</strong></del></span></p>"),
            ([new("p ", HighlightColor: "FFFF00"), new("q", Underline: true, HighlightColor: "FFFF00"), new("r", Bold: true, Underline: true, HighlightColor: "FFFF00")],
                "<mark>p <u>q**r**</u></mark>", "<p><mark>p <u>q<strong>r</strong></u></mark></p>"),
        };
        foreach (var (runs, expected, html) in cases)
        {
            var markdown = SerializeNodes(RichParagraph(runs));
            Assert.Contains("\n" + expected + "\n", markdown, StringComparison.Ordinal);
            Assert.Equal(html, Gfm(expected));
            Assert.DoesNotContain("~~~~", markdown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_line_through_a_paragraph_of_mixed_runs_strikes_it_once_as_a_strike_of_the_runs_does()
    {
        // 本日の会議は中止 at 12 pt is 84 pt wide: the line covers it. The same paragraph struck by its runs reads the same.
        var drawn = $"<w:p>{WordRun("本日の")}{WordRun("会議", "<w:b/>")}{WordRun("は中止")}{WordLine(-2, WordStrike, 88)}</w:p>";
        var native = $"<w:p>{WordRun("本日の", "<w:strike/>")}{WordRun("会議", "<w:b/><w:strike/>")}{WordRun("は中止", "<w:strike/>")}</w:p>";
        var (markdown, _) = await Export(Document("mixedruns.docx", drawn + native));
        Assert.Equal(2, markdown.Split("\n~~本日の**会議**は中止~~\n").Length - 1);
        Assert.DoesNotContain("~~~~", markdown, StringComparison.Ordinal);
    }

    // ---- the offsets of a drawn line are those of the text the node holds ---------------------------------------------

    private static string EmptyParagraph() => "<a:p><a:endParaRPr lang=\"en-US\" sz=\"1800\"/></a:p>";

    private static string Titled(string textBox) => textBox.Replace("name=\"Text 2\"", "name=\"Title 1\"", StringComparison.Ordinal);

    [Fact]
    public async Task A_line_through_a_title_of_formatted_text_that_begins_with_an_empty_paragraph_strikes_the_text_it_crosses()
    {
        // The title is bold, so its node holds runs; they begin at its first paragraph that has any, with no line break for the empty one above.
        // The text, 15 characters, is on the second line: its baseline is 72 + 21.6 + 17.46 down, and the line is 0.3 em above it.
        var title = Titled(TextBox(2, 72, 72, 400, 80, EmptyParagraph() + SlideParagraph("Cancelled title", 18, " b=\"1\"")));
        var source = Presentation("emptytitle.pptx", title + SlideLine(5, 71, 105.66, 191));
        var node = ShapeNode(ExtractSlides(source), "Title 1");
        Assert.IsType<RichTextNodeContent>(node.Content);
        Assert.Equal("[[0,15]]", Extension(node, "strike_ranges"));
        var (markdown, _) = await Export(source);
        Assert.Contains("## スライド 1 — ~~**Cancelled title**~~\n", markdown, StringComparison.Ordinal);

        // A title with no formatting holds its text as it is, with the line break of the empty paragraph: the ranges are over that text.
        var plain = Presentation("emptytitleplain.pptx", Titled(TextBox(2, 72, 72, 400, 80, EmptyParagraph() + SlideParagraph("Cancelled title", 18))) + SlideLine(5, 71, 105.66, 191));
        var plainNode = ShapeNode(ExtractSlides(plain), "Title 1");
        Assert.IsType<TextNodeContent>(plainNode.Content);
        Assert.Equal("[[1,16]]", Extension(plainNode, "strike_ranges"));
        Assert.Contains("## スライド 1 — ~~Cancelled title~~\n", (await Export(plain)).Markdown, StringComparison.Ordinal);

        // Two empty paragraphs above two lines of the title: the line through the first of them stays inside it.
        var two = Titled(TextBox(2, 72, 72, 400, 120, EmptyParagraph() + EmptyParagraph() + SlideParagraph("Plan A", 18, " b=\"1\"") + SlideParagraph("Plan B", 18, " b=\"1\"")));
        var second = Presentation("twoempty.pptx", two + SlideLine(5, 71, 127.26, 125));
        Assert.Equal("[[0,6]]", Extension(ShapeNode(ExtractSlides(second), "Title 1"), "strike_ranges"));
        Assert.Contains("## スライド 1 — ~~**Plan A**~~<br>**Plan B**\n", (await Export(second)).Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_through_the_text_of_a_body_shape_that_begins_with_an_empty_paragraph_strikes_the_text_it_crosses()
    {
        // The paragraphs of a shape find their own place in the text the node holds, as the title does in the heading.
        var body = TextBox(2, 72, 250, 400, 80, EmptyParagraph() + SlideParagraph("Cancelled body", 18, " b=\"1\"") + SlideParagraph("Kept", 18, " b=\"1\""));
        var source = Presentation("emptybody.pptx", body + SlideLine(5, 71, 283.66, 199));
        Assert.Equal("[[0,14]]", Extension(ShapeNode(ExtractSlides(source), "Text 2"), "strike_ranges"));
        Assert.Contains("\n**~~Cancelled body~~**\n\n**Kept**\n", (await Export(source)).Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void The_paragraphs_of_a_shape_find_their_place_in_the_text_its_node_holds_and_ranges_past_that_text_mark_nothing()
    {
        // A shape as PptxAdapter writes it: the details of its paragraphs, and a node that holds the runs when some are formatted (they begin at the
        // first paragraph with runs, a line break before each paragraph after it) or else the paragraphs' text joined by line breaks.
        string Written(bool formatted, (string Text, bool Bold)[] paragraphs, int start, int end)
        {
            var details = JsonSerializer.SerializeToElement(paragraphs.Select(paragraph => new PptxTextParagraph(paragraph.Text,
                Runs: paragraph.Text.Length == 0 ? [] : [new PptxTextRun(paragraph.Text, Bold: paragraph.Bold)])).ToArray());
            var runs = new List<TextRun>();
            foreach (var paragraph in paragraphs)
            {
                if (runs.Count > 0) runs.Add(new TextRun("\n", Kind: TextRunKind.LineBreak));
                if (paragraph.Text.Length > 0) runs.Add(new TextRun(paragraph.Text, Bold: paragraph.Bold));
            }
            NodeContent content = formatted ? new RichTextNodeContent(runs) : new TextNodeContent(string.Join('\n', paragraphs.Select(paragraph => paragraph.Text)));
            var extensions = RangesOf("strike_ranges", start, end);
            extensions["paragraph_details"] = details;
            var node = new DocumentNode("s", NodeKind.Shape, null, 0, ContentLayer.Body, content, Extensions: extensions);
            return new ReadableMarkdownSerializer().Serialize(new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "review", DocumentFormatKind.Pptx,
                [new DocumentPartition("slide-1", 0, [node])])).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        // Formatted text with an empty paragraph above it: the node's text is "ab\ncd", and characters 3 to 5 of it are "cd".
        (string, bool)[] formatted = [("", false), ("ab", true), ("cd", true)];
        Assert.Contains("\n**~~cd~~**\n", Written(true, formatted, 3, 5), StringComparison.Ordinal);
        Assert.Contains("\n**ab**\n", Written(true, formatted, 3, 5), StringComparison.Ordinal);
        // Plain text keeps the line break of the empty paragraph: "\nab\ncd", and characters 4 to 6 of it are "cd".
        (string, bool)[] plain = [("", false), ("ab", false), ("cd", false)];
        Assert.Contains("\n~~cd~~\n", Written(false, plain, 4, 6), StringComparison.Ordinal);
        // Ranges that reach past the end of the text are over some other text.
        Assert.DoesNotContain("~~", Written(true, formatted, 3, 9), StringComparison.Ordinal);
        Assert.DoesNotContain("~~", Written(false, plain, 4, 9), StringComparison.Ordinal);
    }

    [Fact]
    public void Ranges_that_reach_past_the_text_of_the_runs_mark_nothing()
    {
        var runs = new[] { new TextRun("ab", Bold: true), new TextRun("cd") };
        string With(int start, int end) => SerializeNodes(RichParagraph(runs) with { Extensions = RangesOf("strike_ranges", start, end) });
        Assert.Contains("\n**a**~~**b**c~~d\n", With(1, 3), StringComparison.Ordinal);
        // Over the end of the text: they are over some other text, and no character of this one is struck.
        var reaching = With(1, 9);
        Assert.Contains("\n**ab**cd\n", reaching, StringComparison.Ordinal);
        Assert.DoesNotContain("~~", reaching, StringComparison.Ordinal);
    }

    // ---- distributed text is spread over all of its line ---------------------------------------------------------------

    [Fact]
    public void A_line_over_part_of_distributed_slide_text_is_over_part_of_it_and_asks_for_review()
    {
        // The box is 400 pt wide; spread over it, the text runs from 72 to 472 pt. A line over its first 210 pt covers a part of it (left aligned,
        // the text would end at 273.6 pt and the line would strike all of it).
        var distributed = TextBox(2, 72, 100, 400, 40, SlideParagraph("Old price 1000 yen", properties: "algn=\"dist\""));
        var partial = ExtractSlides(Presentation("distpartial.pptx", distributed + SlideLine(5, 70, 116, 280)));
        Assert.Null(StrikeRanges(partial));
        Assert.Equal("true", Unplaced(partial));
        // A line over all of the line strikes it; so does one over the first part of text that is not spread.
        Assert.Equal("[[0,18]]", StrikeRanges(ExtractSlides(Presentation("distwhole.pptx", distributed + SlideLine(5, 70, 116, 474)))));
        var left = TextBox(2, 72, 100, 400, 40, SlideParagraph("Old price 1000 yen", properties: "algn=\"l\""));
        Assert.Equal("[[0,18]]", StrikeRanges(ExtractSlides(Presentation("leftpartial.pptx", left + SlideLine(5, 70, 116, 280)))));
        // A line under part of it underlines nothing, and asks for nothing.
        var under = ExtractSlides(Presentation("distunder.pptx", distributed + SlideLine(5, 70, 126.5, 280)));
        Assert.Null(UnderlineRanges(under));
        Assert.Null(Unplaced(under));
    }

    [Fact]
    public async Task A_line_over_part_of_distributed_Word_text_is_over_part_of_it_and_asks_for_review()
    {
        // 東京都 is 36 pt wide, but "distribute" spreads it over the 468 pt column: a line over the first 40 pt covers one character of three.
        string Paragraph(string lines, string justification) => WordParagraph("東京都", lines, $"<w:jc w:val=\"{justification}\"/>");
        var partial = Document("distpartial.docx", Paragraph(WordLine(-2, WordStrike, 42), "distribute"));
        var node = ParagraphNodes(ExtractDocument(partial)).Single();
        Assert.Null(Extension(node, "strike_ranges"));
        Assert.Equal("true", Extension(node, ReviewExtensionKeys.StrikeUnplaced));
        var (markdown, result) = await Export(partial);
        Assert.Contains("\n東京都\n", markdown, StringComparison.Ordinal);
        Assert.Equal(ReviewElementKind.StrikethroughUnplaced, Assert.Single(Assert.Single(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages).Elements).Kind);
        // A line along all of the column strikes it; a line as short over text that is not spread strikes all of it.
        Assert.Contains("\n~~東京都~~\n", (await Export(Document("distwhole.docx", Paragraph(WordLine(-2, WordStrike, 472), "distribute")))).Markdown, StringComparison.Ordinal);
        Assert.Contains("\n~~東京都~~\n", (await Export(Document("leftpartial.docx", Paragraph(WordLine(-2, WordStrike, 42), "left")))).Markdown, StringComparison.Ordinal);
        // An underline over part of it underlines nothing.
        Assert.DoesNotContain("<u>", (await Export(Document("distunder.docx", Paragraph(WordLine(0, WordUnderline, 42), "distribute")))).Markdown, StringComparison.Ordinal);
        Assert.Contains("<u>東京都</u>", (await Export(Document("leftunder.docx", Paragraph(WordLine(0, WordUnderline, 42), "left")))).Markdown, StringComparison.Ordinal);
    }
}
