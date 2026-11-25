using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Pptx;
using DocRedock.Markdown;
using DocRedock.Render;

namespace DocRedock.Tests.Pptx;

/// <summary>Links between the slides of a presentation: a:hlinkClick with the action ppaction://hlinksldjump (the
/// relationship names the slide part) or ppaction://hlinkshowjump?jump=... (first, last, next, previous slide).
/// The deck has four slides, the third hidden. The page reaches a slide by the id of its section and the Markdown
/// by an anchor above the slide's heading; a jump to a slide the output leaves out keeps its label only.</summary>
public sealed class PptxSlideJumpLinkTests : IDisposable
{
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private readonly string root = Directory.CreateTempSubdirectory("docredock-slide-jump-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public async Task A_jump_by_relationship_on_a_shape_and_on_a_run_links_to_the_slide_section_of_the_page()
    {
        var html = await Export(ReadableOutputFormat.Html);
        // The shape's own link covers its text; the run's link covers the run. Slide 2 is section 1, slide 4 section 3.
        Assert.Equal("#source-part-1", Href(html, "SHAPEJUMP"));
        Assert.Equal("#source-part-3", Href(html, "RUNJUMP"));
        Assert.Matches("<section class=\"source-section[^\"]*\" id=\"source-part-1\"", html);
        Assert.Contains(" id=\"source-part-3\"", html, StringComparison.Ordinal);
        // The drawn slide carries the link on the run's own span.
        Assert.Matches("<a href=\"#source-part-3\"><span style=\"[^\"]*\">RUNJUMP</span></a>", html);
    }

    [Fact]
    public void A_text_block_of_the_page_links_a_jump_to_the_slide_section_and_keeps_the_label_of_one_that_is_not_there()
    {
        ReadableSection Slide(int order, params TextRun[] runs) => new("source-part-" + order, ReadableContent.Text("Slide " + (order + 1)), [new ReadableParagraph(new(runs))]);
        // A heading that would take the second slide's id if the slides had not reserved theirs first.
        var html = ReadableHtmlWriter.WriteDocument(new("deck", "en", DocumentFormatKind.Pptx,
        [
            new ReadableHeading(2, ReadableContent.Text("source part 1")),
            Slide(0, new TextRun("FORWARD", LinkTarget: "#docredock-slide-2"), new TextRun("GONE", LinkTarget: "#docredock-slide-7"), new TextRun("WEB", LinkTarget: "https://example.invalid/")),
            Slide(1, new TextRun("BACK", LinkTarget: "#docredock-slide-1")),
        ])).Html;
        Assert.Contains("<a href=\"#source-part-1\">FORWARD</a>", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"#source-part-0\">BACK</a>", html, StringComparison.Ordinal);
        Assert.Matches("<section class=\"source-section[^\"]*\" id=\"source-part-1\"", html);
        Assert.Contains("<a href=\"https://example.invalid/\">WEB</a>", html, StringComparison.Ordinal);
        Assert.Contains("GONE", html, StringComparison.Ordinal);
        Assert.DoesNotContain("docredock-slide", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">GONE</a>", html, StringComparison.Ordinal);
        // Outside a presentation the fragment is an ordinary one.
        var word = ReadableHtmlWriter.WriteDocument(new("doc", "en", DocumentFormatKind.Docx,
            [new ReadableParagraph(new([new TextRun("BOOKMARK", LinkTarget: "#docredock-slide-2")]))])).Html;
        Assert.Contains("<a href=\"#docredock-slide-2\">BOOKMARK</a>", word, StringComparison.Ordinal);
    }

    [Fact]
    public async Task First_last_next_and_previous_jumps_follow_the_show_past_the_hidden_slide()
    {
        var html = await Export(ReadableOutputFormat.Html);
        Assert.Equal("#source-part-1", Href(html, "NEXTFROM1"));
        Assert.Equal("#source-part-3", Href(html, "LASTFROM1"));
        Assert.Equal("#source-part-0", Href(html, "PREVFROM2"));
        Assert.Equal("#source-part-0", Href(html, "FIRSTFROM2"));
        // Slide 3 is hidden: the show goes from slide 2 to slide 4 and back.
        Assert.Equal("#source-part-3", Href(html, "NEXTFROM2"));
        Assert.Equal("#source-part-1", Href(html, "PREVFROM4"));
        // There is no slide after the last one.
        Assert.Null(Href(html, "NEXTFROM4"));
        Assert.Contains("NEXTFROM4", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_jump_to_a_hidden_or_missing_slide_keeps_its_label_without_an_anchor()
    {
        var html = await Export(ReadableOutputFormat.Html);
        Assert.Null(Href(html, "TOHIDDEN"));
        Assert.Null(Href(html, "TOMISSING"));
        Assert.Contains("TOHIDDEN", html, StringComparison.Ordinal);
        Assert.Contains("TOMISSING", html, StringComparison.Ordinal);
        // No link names a section the page does not have, and the internal target never reaches the page.
        Assert.DoesNotContain("source-part-2", html, StringComparison.Ordinal);
        Assert.DoesNotContain("docredock-slide", html, StringComparison.Ordinal);
        Assert.All(Regex.Matches(html, "href=\"#([^\"]+)\"").Select(match => match.Groups[1].Value),
            id => Assert.Contains(" id=\"" + id + "\"", html, StringComparison.Ordinal));

        // The complete policy writes the hidden slide, and the link then leads to it.
        var complete = await Export(ReadableOutputFormat.Html, "complete");
        Assert.Equal("#source-part-2", Href(complete, "TOHIDDEN"));
        Assert.Contains(" id=\"source-part-2\"", complete, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_actions_stay_plain_text_and_external_links_keep_their_safety_rules()
    {
        var html = await Export(ReadableOutputFormat.Html);
        foreach (var label in new[] { "MACRO", "PROGRAM", "OLEVERB", "CUSTOMSHOW", "ENDSHOW", "SCRIPT", "RELATIVE", "OTHERDECK" })
        {
            Assert.Null(Href(html, label));
            Assert.Contains(label, html, StringComparison.Ordinal);
        }
        Assert.Equal("https://example.invalid/page", Href(html, "WEB"));
        Assert.Equal("mailto:someone@example.invalid", Href(html, "MAIL"));
        Assert.DoesNotContain("javascript:", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"other", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ppaction", html, StringComparison.Ordinal);

        var markdown = await Export(ReadableOutputFormat.Markdown);
        Assert.Contains("[WEB](<https://example.invalid/page>)", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", markdown, StringComparison.Ordinal);
        foreach (var label in new[] { "MACRO", "PROGRAM", "OLEVERB", "CUSTOMSHOW", "ENDSHOW", "SCRIPT" })
            Assert.DoesNotContain("[" + label + "]", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Markdown_links_a_jump_to_an_anchor_above_the_heading_of_the_slide()
    {
        var markdown = await Export(ReadableOutputFormat.Markdown);
        Assert.Contains("[SHAPEJUMP](<#docredock-slide-2>)", markdown, StringComparison.Ordinal);
        Assert.Contains("[RUNJUMP](<#docredock-slide-4>)", markdown, StringComparison.Ordinal);
        Assert.Contains("[PREVFROM2](<#docredock-slide-1>)", markdown, StringComparison.Ordinal);
        Assert.Contains("[NEXTFROM2](<#docredock-slide-4>)", markdown, StringComparison.Ordinal);
        foreach (var slide in new[] { 1, 2, 4 })
            Assert.Matches("(?m)^<a id=\"docredock-slide-" + slide + "\"></a>\n## (Slide|スライド) " + slide + "\\b", markdown);
        // The hidden slide is not written: its jump keeps the label, and nothing points at an anchor that is not there.
        Assert.DoesNotContain("docredock-slide-3", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("[TOHIDDEN]", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("[TOMISSING]", markdown, StringComparison.Ordinal);
        Assert.Contains("TOHIDDEN", markdown, StringComparison.Ordinal);
        Assert.All(Regex.Matches(markdown, @"\]\(<#([^>]+)>\)").Select(match => match.Groups[1].Value),
            id => Assert.Contains("<a id=\"" + id + "\"></a>", markdown, StringComparison.Ordinal));
    }

    [Fact]
    public void Portable_Markdown_has_no_anchors_and_names_the_slide_after_the_label()
    {
        using var stream = File.OpenRead(Deck());
        var graph = new PptxAdapter().Extract(stream).Graph;
        var markdown = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(PortableMarkdown: true, Language: "en")).Serialize(graph);
        Assert.Contains("RUNJUMP (slide 4)", markdown, StringComparison.Ordinal);
        Assert.Contains("SHAPEJUMP (slide 2)", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("docredock-slide", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<a id=", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deck_without_slide_jumps_gets_no_anchors_in_its_Markdown()
    {
        var markdown = await Export(ReadableOutputFormat.Markdown, source: Deck(jumps: false));
        Assert.DoesNotContain("<a id=", markdown, StringComparison.Ordinal);
        Assert.Matches("(?m)^## (Slide|スライド) 2\\b", markdown);
    }

    // The href of the innermost link around the first place the label is written, or null when it is plain there.
    private static string? Href(string text, string label)
    {
        var match = Regex.Match(text, "<a href=\"([^\"]*)\"[^>]*>(?:<(?!/a>)[^>]+>)*" + Regex.Escape(label) + "(?![A-Z0-9])");
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private async Task<string> Export(ReadableOutputFormat format, string policy = "visible", string? source = null)
    {
        var output = Path.Combine(root, Guid.NewGuid().ToString("N") + (format == ReadableOutputFormat.Html ? ".html" : ".md"));
        await service.ExportReadableAsync(new ReadableDocumentExportOptions(source ?? Deck(), output, EnableOcr: false, ContentPolicy: policy, OutputFormat: format));
        return (await File.ReadAllTextAsync(output)).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string Run(string text, string link = "") =>
        $"<a:r><a:rPr lang=\"en-US\" sz=\"1800\">{link}</a:rPr><a:t>{text}</a:t></a:r><a:r><a:rPr lang=\"en-US\" sz=\"1800\"/><a:t> / </a:t></a:r>";

    private static string Jump(string relationship) => $"<a:hlinkClick r:id=\"{relationship}\" action=\"ppaction://hlinksldjump\"/>";

    private static string Show(string jump) => $"<a:hlinkClick r:id=\"\" action=\"ppaction://hlinkshowjump?jump={jump}\"/>";

    private static string Box(int id, int y, string runs, string shapeLink = "") =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Box {id}\">{shapeLink}</p:cNvPr><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr>" +
        $"<p:spPr><a:xfrm><a:off x=\"914400\" y=\"{y * 12700}\"/><a:ext cx=\"7315200\" cy=\"457200\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>" +
        $"<p:txBody><a:bodyPr wrap=\"square\"/><a:lstStyle/><a:p>{runs}</a:p></p:txBody></p:sp>";

    private string Deck(bool jumps = true)
    {
        string Slide(string shapes, bool hidden = false) =>
            $"<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            $"xmlns:r=\"{R}\"{(hidden ? " show=\"0\"" : "")}><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>{shapes}</p:spTree></p:cSld></p:sld>";
        string Rels(string body) => "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + body + "</Relationships>";
        string SlideRel(string id, int slide) => $"<Relationship Id=\"{id}\" Type=\"{R}/slide\" Target=\"slide{slide}.xml\"/>";
        string Link(string id, string target) => $"<Relationship Id=\"{id}\" Type=\"{R}/hyperlink\" Target=\"{target}\" TargetMode=\"External\"/>";
        string Action(string action, string relationship = "") => $"<a:hlinkClick r:id=\"{relationship}\" action=\"ppaction://{action}\"/>";
        var first = jumps
            ? Box(2, 60, Run("SHAPEJUMP"), Jump("rSlide2")) +
              Box(3, 120, Run("RUNJUMP", Jump("rSlide4")) + Run("NEXTFROM1", Show("nextslide")) + Run("LASTFROM1", Show("lastslide"))) +
              Box(4, 180, Run("TOHIDDEN", Jump("rSlide3")) + Run("TOMISSING", Jump("rNone")) + Run("PLAIN")) +
              Box(5, 240, Run("MACRO", Action("macro?name=Run")) + Run("PROGRAM", Action("program", "rProgram")) + Run("OLEVERB", Action("ole?verb=0")) +
                  Run("CUSTOMSHOW", Action("customshow?id=0")) + Run("ENDSHOW", Show("endshow"))) +
              Box(6, 300, Run("WEB", "<a:hlinkClick r:id=\"rWeb\"/>") + Run("MAIL", "<a:hlinkClick r:id=\"rMail\"/>") + Run("SCRIPT", "<a:hlinkClick r:id=\"rScript\"/>") +
                  Run("RELATIVE", "<a:hlinkClick r:id=\"rRelative\"/>") + Run("OTHERDECK", Action("hlinkpres?slideindex=2&amp;slidetitle=", "rDeck")))
            : Box(2, 60, Run("FIRST SLIDE TEXT"));
        var second = jumps
            ? Box(2, 60, Run("PREVFROM2", Show("previousslide")) + Run("FIRSTFROM2", Show("firstslide")) + Run("NEXTFROM2", Show("nextslide")))
            : Box(2, 60, Run("SECOND SLIDE TEXT"));
        var fourth = jumps ? Box(2, 60, Run("PREVFROM4", Show("previousslide")) + Run("NEXTFROM4", Show("nextslide"))) : Box(2, 60, Run("FOURTH SLIDE TEXT"));
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/></Types>",
            ["_rels/.rels"] = Rels($"<Relationship Id=\"rId1\" Type=\"{R}/officeDocument\" Target=\"ppt/presentation.xml\"/>"),
            ["ppt/presentation.xml"] = "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" " +
                $"xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"{R}\"><p:sldIdLst>" +
                "<p:sldId id=\"256\" r:id=\"rId1\"/><p:sldId id=\"257\" r:id=\"rId2\"/><p:sldId id=\"258\" r:id=\"rId3\"/><p:sldId id=\"259\" r:id=\"rId4\"/>" +
                "</p:sldIdLst><p:sldSz cx=\"9144000\" cy=\"5143500\"/></p:presentation>",
            ["ppt/_rels/presentation.xml.rels"] = Rels(string.Concat(Enumerable.Range(1, 4).Select(n => $"<Relationship Id=\"rId{n}\" Type=\"{R}/slide\" Target=\"slides/slide{n}.xml\"/>"))),
            ["ppt/slides/slide1.xml"] = Slide(first),
            ["ppt/slides/_rels/slide1.xml.rels"] = Rels(SlideRel("rSlide2", 2) + SlideRel("rSlide3", 3) + SlideRel("rSlide4", 4) +
                Link("rWeb", "https://example.invalid/page") + Link("rMail", "mailto:someone@example.invalid") + Link("rScript", "javascript:alert(1)") +
                Link("rRelative", "other.html") + Link("rDeck", "other.pptx") + Link("rProgram", "file:///c:/tool.exe")),
            ["ppt/slides/slide2.xml"] = Slide(second),
            ["ppt/slides/slide3.xml"] = Slide(Box(2, 60, Run("HIDDEN SLIDE TEXT")), hidden: true),
            ["ppt/slides/slide4.xml"] = Slide(fourth),
        };
        var path = Path.Combine(root, jumps ? "jumps.pptx" : "plain.pptx");
        if (File.Exists(path)) return path;
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
        return path;
    }
}
