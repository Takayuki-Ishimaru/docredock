using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Pptx;

namespace DocRedock.Tests.Pptx;

/// <summary>The source drawing scene of a slide resolves what its shapes inherit from the slide
/// layout, the slide master, the master's text styles and the presentation defaults.</summary>
public sealed class PptxPlaceholderInheritanceTests
{
    [Fact]
    public void A_placeholder_without_a_frame_takes_the_layout_placeholder_frame()
    {
        var title = Shape(Objects(Deck(
            Placeholder(2, "<p:ph type=\"title\"/>", Text("Quarterly report")),
            layout: Placeholder(2, "<p:ph type=\"title\"/>", Text("Click to add title"), Frame(96, 48, 768, 120)),
            master: Placeholder(2, "<p:ph type=\"title\"/>", Text("Master title"), Frame(10, 10, 100, 50)))), 2);
        Assert.Equal((96d, 48d, 768d, 120d), Bounds(title));
    }

    [Fact]
    public void A_placeholder_without_a_frame_takes_the_master_frame_when_the_layout_has_none()
    {
        var body = Shape(Objects(Deck(
            Placeholder(3, "<p:ph idx=\"1\"/>", Text("Agenda")),
            layout: Placeholder(3, "<p:ph idx=\"1\"/>", Text("Click to add text")),
            master: Placeholder(3, "<p:ph type=\"body\" idx=\"1\"/>", Text("Master text"), Frame(48, 168, 864, 480)))), 3);
        Assert.Equal((48d, 168d, 864d, 480d), Bounds(body));
    }

    [Fact]
    public void A_placeholder_matches_the_layout_by_idx_then_by_type_and_the_master_by_type()
    {
        var objects = Objects(Deck(
            Placeholder(2, "<p:ph idx=\"13\"/>", Text("By idx")) +
            Placeholder(3, "<p:ph type=\"body\" idx=\"99\"/>", Text("By type")) +
            Placeholder(4, "<p:ph idx=\"2\"/>", Text("Master by type")),
            layout: Placeholder(2, "<p:ph type=\"body\" idx=\"1\"/>", Text("first body"), Frame(100, 0, 50, 50)) +
                Placeholder(3, "<p:ph type=\"body\" idx=\"13\"/>", Text("second body"), Frame(500, 0, 50, 50)) +
                Placeholder(4, "<p:ph idx=\"2\"/>", Text("content")),
            // A master numbers its placeholders independently: its date has the idx of the layout's content placeholder.
            master: Placeholder(2, "<p:ph type=\"dt\" idx=\"2\"/>", Text("date"), Frame(10, 600, 50, 20)) +
                Placeholder(3, "<p:ph type=\"body\" idx=\"1\"/>", Text("master body"), Frame(300, 100, 400, 300))));
        Assert.Equal((500d, 0d, 50d, 50d), Bounds(Shape(objects, 2)));
        Assert.Equal((100d, 0d, 50d, 50d), Bounds(Shape(objects, 3)));
        Assert.Equal((300d, 100d, 400d, 300d), Bounds(Shape(objects, 4)));
    }

    [Fact]
    public void A_title_takes_size_color_weight_and_alignment_from_the_master_title_style()
    {
        var title = Shape(Objects(Deck(
            Placeholder(2, "<p:ph type=\"title\"/>", Text("Quarterly report")),
            layout: Placeholder(2, "<p:ph type=\"title\"/>", Text("Click to add title"), Frame(96, 48, 768, 120)),
            master: Placeholder(2, "<p:ph type=\"title\"/>", Text("Master title"), Frame(10, 10, 100, 50), body: "<a:bodyPr lIns=\"182880\" anchor=\"ctr\"/>"),
            textStyles: "<p:titleStyle><a:lvl1pPr algn=\"ctr\"><a:defRPr sz=\"4400\" b=\"1\"><a:solidFill><a:schemeClr val=\"tx2\"/></a:solidFill></a:defRPr></a:lvl1pPr></p:titleStyle>" +
                "<p:bodyStyle><a:lvl1pPr><a:defRPr sz=\"3200\"/></a:lvl1pPr></p:bodyStyle>")), 2);
        var run = Assert.Single(Assert.Single(title.Paragraphs).Runs);
        Assert.Equal(4400 / 100d * 96 / 72, run.FontSize, 3);
        Assert.True(run.Bold);
        Assert.Equal("#112233", run.Color);
        Assert.Equal("center", title.TextAlign);
        Assert.Equal("ctr", title.Paragraphs[0].Alignment);
        Assert.Equal("middle", title.VerticalAlign);
        Assert.Equal(19.2, title.TextInsetLeft, 3);
    }

    [Fact]
    public void Body_paragraphs_take_bullet_and_size_of_their_level_from_the_master_body_style()
    {
        var body = Shape(Objects(Deck(
            Placeholder(3, "<p:ph idx=\"1\"/>", "<a:p><a:r><a:t>First</a:t></a:r></a:p><a:p><a:pPr lvl=\"1\"/><a:r><a:t>Second</a:t></a:r></a:p>" +
                "<a:p><a:pPr><a:buNone/></a:pPr><a:r><a:t>Third</a:t></a:r></a:p><a:p><a:endParaRPr/></a:p>"),
            layout: Placeholder(3, "<p:ph idx=\"1\"/>", Text("Click to add text"), Frame(48, 168, 864, 480)),
            master: Placeholder(3, "<p:ph type=\"body\" idx=\"1\"/>", Text("Master text"), Frame(48, 168, 864, 480)),
            textStyles: "<p:titleStyle><a:lvl1pPr><a:buNone/><a:defRPr sz=\"4400\"/></a:lvl1pPr></p:titleStyle>" +
                "<p:bodyStyle><a:lvl1pPr><a:buChar char=\"●\"/><a:defRPr sz=\"3200\"/></a:lvl1pPr>" +
                "<a:lvl2pPr><a:buChar char=\"–\"/><a:defRPr sz=\"2800\" i=\"1\"/></a:lvl2pPr></p:bodyStyle>")), 3);
        Assert.Equal(4, body.Paragraphs.Count);
        Assert.Equal("●", body.Paragraphs[0].BulletText);
        Assert.Equal(3200 / 100d * 96 / 72, body.Paragraphs[0].Runs[0].FontSize, 3);
        Assert.False(body.Paragraphs[0].Runs[0].Italic);
        Assert.Equal("–", body.Paragraphs[1].BulletText);
        Assert.Equal(1, body.Paragraphs[1].Level);
        Assert.Equal(2800 / 100d * 96 / 72, body.Paragraphs[1].Runs[0].FontSize, 3);
        Assert.True(body.Paragraphs[1].Runs[0].Italic);
        Assert.Null(body.Paragraphs[2].BulletText);
        Assert.Null(body.Paragraphs[3].BulletText);
    }

    [Fact]
    public void A_list_style_on_the_layout_placeholder_beats_the_master_placeholder_and_the_master_text_style()
    {
        var title = Shape(Objects(Deck(
            Placeholder(2, "<p:ph type=\"title\"/>", "<a:p><a:r><a:t>Inherited</a:t></a:r><a:r><a:rPr sz=\"1200\"/><a:t>Own</a:t></a:r></a:p>"),
            layout: Placeholder(2, "<p:ph type=\"title\"/>", Text("Click to add title"), Frame(96, 48, 768, 120),
                list: "<a:lvl1pPr><a:defRPr sz=\"6000\"/></a:lvl1pPr>"),
            master: Placeholder(2, "<p:ph type=\"title\"/>", Text("Master title"), Frame(10, 10, 100, 50),
                list: "<a:lvl1pPr><a:defRPr sz=\"5000\" i=\"1\"/></a:lvl1pPr>"),
            textStyles: "<p:titleStyle><a:lvl1pPr><a:defRPr sz=\"4400\" i=\"0\"><a:solidFill><a:schemeClr val=\"tx2\"/></a:solidFill></a:defRPr></a:lvl1pPr></p:titleStyle>")), 2);
        var runs = Assert.Single(title.Paragraphs).Runs;
        Assert.Equal(80, runs[0].FontSize, 3);
        Assert.True(runs[0].Italic);
        Assert.Equal("#112233", runs[0].Color);
        Assert.Equal(16, runs[1].FontSize, 3);
    }

    [Theory]
    [InlineData(false, "#112233")]
    [InlineData(true, "#EEEEEE")]
    public void An_inherited_scheme_color_goes_through_the_master_color_map_and_the_slide_override(bool slideOverride, string expected)
    {
        var title = Shape(Objects(Deck(
            Placeholder(2, "<p:ph type=\"title\"/>", Text("Mapped")),
            layout: Placeholder(2, "<p:ph type=\"title\"/>", Text("Click to add title"), Frame(96, 48, 768, 120)),
            textStyles: "<p:titleStyle><a:lvl1pPr><a:defRPr sz=\"4400\"><a:solidFill><a:schemeClr val=\"tx2\"/></a:solidFill></a:defRPr></a:lvl1pPr></p:titleStyle>",
            slideColorMap: slideOverride ? "<p:clrMapOvr><a:overrideClrMapping bg1=\"dk1\" tx1=\"lt1\" bg2=\"dk2\" tx2=\"lt2\" accent1=\"accent1\" accent2=\"accent2\" " +
                "accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/></p:clrMapOvr>" : "")), 2);
        Assert.Equal(expected, Assert.Single(Assert.Single(title.Paragraphs).Runs).Color);
    }

    [Fact]
    public void Inherited_theme_fonts_resolve_for_Latin_and_Japanese_text()
    {
        var objects = Objects(Deck(
            Placeholder(2, "<p:ph type=\"title\"/>", Text("日本語Title")) + Placeholder(3, "<p:ph idx=\"1\"/>", Text("本文Body")),
            layout: Placeholder(2, "<p:ph type=\"title\"/>", Text("Click to add title"), Frame(96, 48, 768, 120)) +
                Placeholder(3, "<p:ph idx=\"1\"/>", Text("Click to add text"), Frame(48, 168, 864, 480)),
            textStyles: "<p:titleStyle><a:lvl1pPr><a:defRPr sz=\"4400\"><a:latin typeface=\"+mj-lt\"/><a:ea typeface=\"+mj-ea\"/><a:cs typeface=\"+mj-cs\"/></a:defRPr></a:lvl1pPr></p:titleStyle>" +
                "<p:bodyStyle><a:lvl1pPr><a:defRPr sz=\"3200\"><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl1pPr></p:bodyStyle>"));
        Assert.Equal([("日本語", "游ゴシック Light"), ("Title", "Arial Black")],
            Assert.Single(Shape(objects, 2).Paragraphs).Runs.Select(run => (run.Text, run.FontFamily)));
        Assert.Equal([("本文", "游明朝"), ("Body", "Verdana")],
            Assert.Single(Shape(objects, 3).Paragraphs).Runs.Select(run => (run.Text, run.FontFamily)));
    }

    [Fact]
    public void An_empty_placeholder_draws_neither_the_layout_prompt_nor_the_layout_fill()
    {
        var objects = Objects(Deck(
            Placeholder(2, "<p:ph type=\"title\"/>", "<a:p><a:endParaRPr lang=\"en-US\"/></a:p>") + Placeholder(3, "<p:ph idx=\"1\"/>", Text("Agenda")),
            layout: Placeholder(2, "<p:ph type=\"title\"/>", Text("Click to add title"), Frame(96, 48, 768, 120) + Solid("FFC000")) +
                Placeholder(3, "<p:ph idx=\"1\"/>", Text("Click to add text"), Frame(48, 168, 864, 480)) +
                Placeholder(4, "<p:ph type=\"ftr\" idx=\"11\"/>", Text("Layout footer"), Frame(300, 660, 300, 40)),
            master: Placeholder(2, "<p:ph type=\"title\"/>", Text("Click to edit Master title style"), Frame(10, 10, 100, 50)),
            textStyles: "<p:titleStyle><a:lvl1pPr><a:defRPr sz=\"4400\"/></a:lvl1pPr></p:titleStyle>"));
        Assert.DoesNotContain(objects, item => item.Text.Contains("Click", StringComparison.Ordinal) || item.Text.Contains("footer", StringComparison.Ordinal));
        var title = Shape(objects, 2);
        Assert.Equal("", title.Text);
        Assert.Equal("none", title.FillPaint?.Kind);
        Assert.Equal((96d, 48d, 768d, 120d), Bounds(title));
        Assert.Equal(2, objects.Count);
    }

    [Fact]
    public void A_placeholder_takes_geometry_fill_and_line_from_the_layout_then_the_master()
    {
        var body = Shape(Objects(Deck(
            Placeholder(3, "<p:ph idx=\"1\"/>", Text("Agenda")),
            layout: Placeholder(3, "<p:ph idx=\"1\"/>", Text("Click to add text"), Frame(48, 168, 864, 480) + Solid("FFC000")),
            master: Placeholder(3, "<p:ph type=\"body\" idx=\"1\"/>", Text("Master text"), Frame(48, 168, 864, 480) +
                "<a:prstGeom prst=\"roundRect\"><a:avLst/></a:prstGeom>" + Solid("0000FF") + "<a:ln w=\"25400\">" + Solid("00B050") + "</a:ln>"))), 3);
        Assert.Equal("roundRect", body.Preset);
        Assert.Equal("#FFC000", body.FillPaint?.Color);
        Assert.Equal("#00B050", body.StrokePaint?.Color);
        Assert.Equal(25400 / 9525d, body.StrokeWidth, 3);
    }

    [Fact]
    public void A_placeholder_takes_text_body_settings_from_the_layout_then_the_master()
    {
        var body = Shape(Objects(Deck(
            Placeholder(3, "<p:ph idx=\"1\"/>", Text("Agenda")),
            layout: Placeholder(3, "<p:ph idx=\"1\"/>", Text("Click to add text"), Frame(48, 168, 864, 480),
                body: "<a:bodyPr vert=\"vert270\" wrap=\"none\" anchor=\"b\"><a:normAutofit fontScale=\"50000\"/></a:bodyPr>"),
            master: Placeholder(3, "<p:ph type=\"body\" idx=\"1\"/>", Text("Master text"), Frame(48, 168, 864, 480),
                body: "<a:bodyPr lIns=\"0\" tIns=\"95250\" anchor=\"ctr\"><a:noAutofit/></a:bodyPr>"),
            textStyles: "<p:bodyStyle><a:lvl1pPr><a:defRPr sz=\"3200\"/></a:lvl1pPr></p:bodyStyle>")), 3);
        Assert.Equal("bottom", body.VerticalAlign);
        Assert.False(body.WrapText);
        Assert.Equal(0, body.TextInsetLeft, 3);
        Assert.Equal(10, body.TextInsetTop, 3);
        Assert.Equal(9.6, body.TextInsetRight, 3);
        Assert.Equal("vert270", body.TextLayout?.Direction);
        Assert.Equal(3200 / 100d * 96 / 72 / 2, Assert.Single(Assert.Single(body.Paragraphs).Runs).FontSize, 3);
    }

    [Theory]
    [InlineData(true, 2000)]
    [InlineData(false, 1800)]
    public void A_shape_that_is_no_placeholder_takes_the_master_other_style_then_the_presentation_defaults(bool otherStyle, int size)
    {
        const string styled = "<p:style><a:lnRef idx=\"0\"><a:schemeClr val=\"accent1\"/></a:lnRef><a:fillRef idx=\"0\"><a:schemeClr val=\"accent1\"/></a:fillRef>" +
            "<a:effectRef idx=\"0\"><a:schemeClr val=\"accent1\"/></a:effectRef><a:fontRef idx=\"minor\"><a:schemeClr val=\"lt1\"/></a:fontRef></p:style>";
        var objects = Objects(Deck(
            Box(5, Text("Text box")) + Box(6, Text("Styled"), styled),
            textStyles: otherStyle ? "<p:otherStyle><a:lvl1pPr><a:defRPr sz=\"2000\"/></a:lvl1pPr></p:otherStyle>" : "",
            defaults: "<p:defaultTextStyle><a:lvl1pPr><a:defRPr sz=\"1800\" b=\"1\"><a:solidFill><a:schemeClr val=\"tx2\"/></a:solidFill>" +
                "<a:latin typeface=\"+mn-lt\"/></a:defRPr></a:lvl1pPr></p:defaultTextStyle>"));
        var plain = Assert.Single(Assert.Single(Shape(objects, 5).Paragraphs).Runs);
        Assert.Equal(size / 100d * 96 / 72, plain.FontSize, 3);
        Assert.True(plain.Bold);
        Assert.Equal("#112233", plain.Color);
        Assert.Equal("Verdana", plain.FontFamily);
        // The shape's own style font reference names the color; size and weight remain inherited.
        var withStyle = Assert.Single(Assert.Single(Shape(objects, 6).Paragraphs).Runs);
        Assert.Equal("#FFFFFF", withStyle.Color);
        Assert.Equal(size / 100d * 96 / 72, withStyle.FontSize, 3);
        Assert.True(withStyle.Bold);
    }

    [Fact]
    public void Shapes_that_specify_everything_themselves_are_unchanged_by_layout_master_and_defaults()
    {
        const string properties = "<a:prstGeom prst=\"ellipse\"><a:avLst/></a:prstGeom><a:solidFill><a:srgbClr val=\"123456\"/></a:solidFill>" +
            "<a:ln w=\"19050\"><a:solidFill><a:srgbClr val=\"654321\"/></a:solidFill><a:prstDash val=\"dash\"/></a:ln>";
        const string body = "<a:bodyPr vert=\"horz\" wrap=\"square\" lIns=\"45720\" tIns=\"22860\" rIns=\"45720\" bIns=\"22860\" anchor=\"b\" rot=\"0\"><a:noAutofit/></a:bodyPr>";
        const string text = "<a:p><a:pPr algn=\"r\" lvl=\"1\"><a:buNone/></a:pPr><a:r><a:rPr lang=\"ja-JP\" sz=\"1400\" b=\"0\" i=\"1\" u=\"sng\" strike=\"noStrike\">" +
            "<a:solidFill><a:srgbClr val=\"ABCDEF\"/></a:solidFill><a:latin typeface=\"Georgia\"/><a:ea typeface=\"メイリオ\"/><a:cs typeface=\"Arial\"/></a:rPr><a:t>自前Own</a:t></a:r></a:p>";
        var slide = Placeholder(2, "<p:ph type=\"title\"/>", text, Frame(20, 30, 400, 100) + properties, body) +
            Placeholder(3, "<p:ph idx=\"1\"/>", text, Frame(20, 150, 400, 100) + properties, body) +
            Box(5, text, body: body, properties: Frame(20, 300, 400, 100) + properties);
        var bare = Objects(Deck(slide));
        var inherited = Objects(Deck(slide,
            layout: Placeholder(2, "<p:ph type=\"title\"/>", Text("Click to add title"), Frame(96, 48, 768, 120) + Solid("FFC000"),
                    "<a:bodyPr anchor=\"ctr\" wrap=\"none\" lIns=\"0\"><a:normAutofit fontScale=\"50000\"/></a:bodyPr>", "<a:lvl2pPr algn=\"ctr\"><a:buChar char=\"x\"/><a:defRPr sz=\"6000\" b=\"1\"/></a:lvl2pPr>") +
                Placeholder(3, "<p:ph idx=\"1\"/>", Text("Click to add text"), Frame(48, 168, 864, 480)),
            master: Placeholder(2, "<p:ph type=\"title\"/>", Text("Master title"), Frame(10, 10, 100, 50)) +
                Placeholder(3, "<p:ph type=\"body\" idx=\"1\"/>", Text("Master text"), Frame(48, 168, 864, 480) + Solid("0000FF")),
            textStyles: "<p:titleStyle><a:lvl2pPr algn=\"ctr\"><a:defRPr sz=\"4400\" b=\"1\" strike=\"sngStrike\"/></a:lvl2pPr></p:titleStyle>" +
                "<p:bodyStyle><a:lvl2pPr algn=\"ctr\"><a:buChar char=\"●\"/><a:defRPr sz=\"3200\" b=\"1\"/></a:lvl2pPr></p:bodyStyle>" +
                "<p:otherStyle><a:lvl2pPr algn=\"ctr\"><a:buChar char=\"●\"/><a:defRPr sz=\"2000\" b=\"1\"/></a:lvl2pPr></p:otherStyle>",
            defaults: "<p:defaultTextStyle><a:lvl2pPr algn=\"ctr\"><a:defRPr sz=\"1800\" b=\"1\"/></a:lvl2pPr></p:defaultTextStyle>"));
        Assert.Equal(3, bare.Count);
        Assert.Equal(JsonSerializer.Serialize(bare), JsonSerializer.Serialize(inherited));
        Assert.Equal("#123456", Shape(inherited, 2).FillPaint?.Color);
        Assert.Equal(1400 / 100d * 96 / 72, Shape(inherited, 3).Paragraphs[0].Runs[0].FontSize, 3);
    }

    [Fact]
    public void A_slide_shape_keeps_explicit_margin_indent_line_spacing_and_spacing_before_and_after()
    {
        var box = Shape(Objects(Deck(Box(5, "<a:p><a:pPr marL=\"342900\" indent=\"-342900\"><a:lnSpc><a:spcPct val=\"90000\"/></a:lnSpc>" +
            "<a:spcBef><a:spcPts val=\"600\"/></a:spcBef><a:spcAft><a:spcPct val=\"25000\"/></a:spcAft><a:buChar char=\"•\"/></a:pPr>" +
            "<a:r><a:rPr lang=\"en-US\" sz=\"2400\"/><a:t>Spaced</a:t></a:r></a:p>" +
            "<a:p><a:pPr marL=\"95250\" indent=\"190500\"><a:lnSpc><a:spcPts val=\"1500\"/></a:lnSpc></a:pPr><a:r><a:rPr lang=\"en-US\" sz=\"2400\"/><a:t>Exact</a:t></a:r></a:p>"))), 5);
        var first = box.Paragraphs[0];
        Assert.Equal((36d, -36d, 90d), (Math.Round(first.MarginLeft!.Value, 3), Math.Round(first.Indent!.Value, 3), Math.Round(first.LineSpacingPercent!.Value, 3)));
        Assert.Null(first.LineSpacingExact);
        // 6 pt; a quarter of a line (1.2 times the 24 pt, 32 px, text).
        Assert.Equal((8d, 9.6), (Math.Round(first.SpaceBefore!.Value, 3), Math.Round(first.SpaceAfter!.Value, 3)));
        Assert.Equal("•", first.BulletText);
        var second = box.Paragraphs[1];
        Assert.Equal((10d, 20d, 20d), (Math.Round(second.MarginLeft!.Value, 3), Math.Round(second.Indent!.Value, 3), Math.Round(second.LineSpacingExact!.Value, 3)));
        Assert.Null(second.LineSpacingPercent);
        Assert.Null(second.SpaceAfter);
    }

    [Fact]
    public void A_level_the_placeholders_own_list_style_leaves_without_a_bullet_takes_none_from_the_master()
    {
        var body = Shape(Objects(Deck(
            Placeholder(3, "<p:ph idx=\"1\"/>", "<a:p><a:r><a:t>First</a:t></a:r></a:p><a:p><a:pPr lvl=\"1\"/><a:r><a:t>Second</a:t></a:r></a:p>" +
                "<a:p><a:pPr><a:buChar char=\"*\"/></a:pPr><a:r><a:t>Third</a:t></a:r></a:p>", list: "<a:lvl1pPr><a:buNone/></a:lvl1pPr>"),
            layout: Placeholder(3, "<p:ph idx=\"1\"/>", Text("Click to add text"), Frame(48, 168, 864, 480)),
            master: Placeholder(3, "<p:ph type=\"body\" idx=\"1\"/>", Text("Master text"), Frame(48, 168, 864, 480)),
            textStyles: "<p:bodyStyle><a:lvl1pPr><a:buChar char=\"●\"/><a:defRPr sz=\"3200\"/></a:lvl1pPr>" +
                "<a:lvl2pPr><a:buChar char=\"–\"/><a:defRPr sz=\"2800\"/></a:lvl2pPr></p:bodyStyle>")), 3);
        // Level 1 has none; level 2 still inherits, and a paragraph's own bullet stands.
        Assert.Equal([null, "–", "*"], body.Paragraphs.Select(paragraph => paragraph.BulletText));
    }

    [Fact]
    public void Tab_stops_right_margin_direction_and_font_alignment_of_a_level_are_inherited_from_the_master_body_style()
    {
        var body = Shape(Objects(Deck(
            Placeholder(3, "<p:ph idx=\"1\"/>", "<a:p><a:r><a:t>A\tB</a:t></a:r></a:p><a:p><a:pPr marR=\"0\" rtl=\"0\" defTabSz=\"457200\"><a:tabLst/></a:pPr><a:r><a:t>Own</a:t></a:r></a:p>"),
            layout: Placeholder(3, "<p:ph idx=\"1\"/>", Text("Click to add text"), Frame(48, 168, 864, 480), list: "<a:lvl1pPr fontAlgn=\"ctr\"/>"),
            master: Placeholder(3, "<p:ph type=\"body\" idx=\"1\"/>", Text("Master text"), Frame(48, 168, 864, 480)),
            textStyles: "<p:bodyStyle><a:lvl1pPr marR=\"190500\" rtl=\"1\" defTabSz=\"914400\" fontAlgn=\"b\"><a:tabLst><a:tab pos=\"1905000\" algn=\"r\"/></a:tabLst>" +
                "<a:buNone/><a:defRPr sz=\"3200\"/></a:lvl1pPr></p:bodyStyle>")), 3);
        var inherited = body.Paragraphs[0];
        Assert.Equal((20d, true, 96d, "ctr"), (inherited.MarginRight, inherited.RightToLeft, inherited.DefaultTabSize, inherited.FontAlignment));
        Assert.Equal(new DrawingTabStop(200, "r"), Assert.Single(inherited.TabStops!));
        var own = body.Paragraphs[1];
        Assert.Equal((0d, false, 48d, "ctr"), (own.MarginRight, own.RightToLeft, own.DefaultTabSize, own.FontAlignment));
        // The paragraph's own empty list clears the inherited stops.
        Assert.Null(own.TabStops);
    }

    [Fact]
    public void Body_paragraphs_take_margin_indent_and_spacing_of_their_level_from_the_master_body_style()
    {
        var body = Shape(Objects(Deck(
            Placeholder(3, "<p:ph idx=\"1\"/>", "<a:p><a:r><a:t>First</a:t></a:r></a:p><a:p><a:pPr lvl=\"1\"/><a:r><a:t>Second</a:t></a:r></a:p>" +
                "<a:p><a:pPr lvl=\"1\" indent=\"0\"><a:spcBef><a:spcPts val=\"0\"/></a:spcBef></a:pPr><a:r><a:t>Own</a:t></a:r></a:p>"),
            layout: Placeholder(3, "<p:ph idx=\"1\"/>", Text("Click to add text"), Frame(48, 168, 864, 480),
                list: "<a:lvl2pPr><a:spcAft><a:spcPts val=\"300\"/></a:spcAft></a:lvl2pPr>"),
            master: Placeholder(3, "<p:ph type=\"body\" idx=\"1\"/>", Text("Master text"), Frame(48, 168, 864, 480)),
            textStyles: "<p:bodyStyle><a:lvl1pPr marL=\"228600\" indent=\"-228600\"><a:lnSpc><a:spcPct val=\"90000\"/></a:lnSpc><a:spcBef><a:spcPts val=\"1000\"/></a:spcBef>" +
                "<a:buChar char=\"●\"/><a:defRPr sz=\"2800\"/></a:lvl1pPr>" +
                "<a:lvl2pPr marL=\"685800\" indent=\"-228600\"><a:lnSpc><a:spcPct val=\"80000\"/></a:lnSpc><a:spcBef><a:spcPct val=\"50000\"/></a:spcBef>" +
                "<a:spcAft><a:spcPts val=\"900\"/></a:spcAft><a:buChar char=\"–\"/><a:defRPr sz=\"2400\"/></a:lvl2pPr></p:bodyStyle>")), 3);
        var first = body.Paragraphs[0];
        Assert.Equal((24d, -24d, 90d, 13.333), (Math.Round(first.MarginLeft!.Value, 3), Math.Round(first.Indent!.Value, 3),
            Math.Round(first.LineSpacingPercent!.Value, 3), Math.Round(first.SpaceBefore!.Value, 3)));
        Assert.Null(first.SpaceAfter);
        var second = body.Paragraphs[1];
        // Level 2 of the master body style; half a line (1.2 times its 24 pt, 32 px, text) before, and the layout placeholder's 3 pt after.
        Assert.Equal((72d, -24d, 80d, 19.2, 4d), (Math.Round(second.MarginLeft!.Value, 3), Math.Round(second.Indent!.Value, 3),
            Math.Round(second.LineSpacingPercent!.Value, 3), Math.Round(second.SpaceBefore!.Value, 3), Math.Round(second.SpaceAfter!.Value, 3)));
        var own = body.Paragraphs[2];
        Assert.Equal((72d, 0d, 80d, 0d, 4d), (Math.Round(own.MarginLeft!.Value, 3), Math.Round(own.Indent!.Value, 3),
            Math.Round(own.LineSpacingPercent!.Value, 3), Math.Round(own.SpaceBefore!.Value, 3), Math.Round(own.SpaceAfter!.Value, 3)));
    }

    [Fact]
    public void A_bullet_takes_color_size_and_font_from_the_master_body_style_unless_the_paragraph_sets_its_own()
    {
        var body = Shape(Objects(Deck(
            Placeholder(3, "<p:ph idx=\"1\"/>", "<a:p><a:r><a:t>Inherited</a:t></a:r></a:p>" +
                "<a:p><a:pPr><a:buClrTx/><a:buSzTx/><a:buFontTx/></a:pPr><a:r><a:t>As the text</a:t></a:r></a:p>" +
                "<a:p><a:pPr><a:buClr><a:srgbClr val=\"00B050\"/></a:buClr><a:buSzPts val=\"1200\"/><a:buFont typeface=\"Wingdings\"/><a:buChar char=\"§\"/></a:pPr><a:r><a:t>Own</a:t></a:r></a:p>"),
            layout: Placeholder(3, "<p:ph idx=\"1\"/>", Text("Click to add text"), Frame(48, 168, 864, 480)),
            master: Placeholder(3, "<p:ph type=\"body\" idx=\"1\"/>", Text("Master text"), Frame(48, 168, 864, 480)),
            textStyles: "<p:bodyStyle><a:lvl1pPr><a:buClr><a:schemeClr val=\"tx2\"/></a:buClr><a:buSzPct val=\"75000\"/><a:buFont typeface=\"+mj-lt\"/>" +
                "<a:buChar char=\"●\"/><a:defRPr sz=\"2400\"/></a:lvl1pPr></p:bodyStyle>")), 3);
        var inherited = body.Paragraphs[0];
        Assert.Equal(("●", "#112233", "Arial Black", 24d), (inherited.BulletText, inherited.BulletColor, inherited.BulletFontFamily, Math.Round(inherited.BulletSize!.Value, 3)));
        var text = body.Paragraphs[1];
        Assert.Equal(("●", null, null, null), (text.BulletText, text.BulletColor, text.BulletFontFamily, text.BulletSize));
        var own = body.Paragraphs[2];
        Assert.Equal(("§", "#00B050", "Wingdings", 16d), (own.BulletText, own.BulletColor, own.BulletFontFamily, Math.Round(own.BulletSize!.Value, 3)));
    }

    [Fact]
    public void A_line_that_sets_only_its_width_keeps_color_dash_and_arrow_of_the_layout_line()
    {
        var objects = Objects(Deck(
            Placeholder(3, "<p:ph idx=\"1\"/>", Text("Width only"), "<a:ln w=\"38100\"/>") +
            Placeholder(4, "<p:ph idx=\"2\"/>", Text("Dash only"), "<a:ln><a:prstDash val=\"dot\"/><a:headEnd type=\"oval\"/></a:ln>"),
            layout: Placeholder(3, "<p:ph idx=\"1\"/>", Text("Click to add text"), Frame(48, 168, 400, 200) +
                    "<a:ln w=\"12700\">" + Solid("00B050") + "<a:prstDash val=\"dash\"/><a:tailEnd type=\"triangle\"/></a:ln>") +
                Placeholder(4, "<p:ph idx=\"2\"/>", Text("Click to add text"), Frame(48, 400, 400, 200) + "<a:ln>" + Solid("FF0000") + "</a:ln>"),
            master: Placeholder(3, "<p:ph type=\"body\" idx=\"1\"/>", Text("Master text"), Frame(48, 168, 864, 480) +
                "<a:ln w=\"57150\">" + Solid("0000FF") + "<a:headEnd type=\"diamond\"/></a:ln>")));
        var width = Shape(objects, 3);
        Assert.Equal(4, width.StrokeWidth, 3);
        Assert.Equal(("#00B050", "dash", "triangle", "diamond"), (width.StrokePaint?.Color, width.Dash, width.TailArrow, width.HeadArrow));
        // The layout line names the color only; the width comes from the master line behind it.
        var dash = Shape(objects, 4);
        Assert.Equal(6, dash.StrokeWidth, 3);
        Assert.Equal(("#FF0000", "dot", "oval"), (dash.StrokePaint?.Color, dash.Dash, dash.HeadArrow));
    }

    [Fact]
    public void Theme_object_defaults_are_insertion_defaults_and_change_no_saved_shape_connector_or_text_box()
    {
        // ECMA-376 20.1.6.7: a:objectDefaults format an object when it is inserted. PowerPoint draws a saved shape
        // without p:style, fill and line with no fill and no line, whatever a:spDef says.
        const string connector = "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"7\" name=\"Connector 7\"/><p:cNvCxnSpPr/><p:nvPr/></p:nvCxnSpPr><p:spPr>" +
            "<a:xfrm><a:off x=\"952500\" y=\"952500\"/><a:ext cx=\"952500\" cy=\"0\"/></a:xfrm><a:prstGeom prst=\"line\"><a:avLst/></a:prstGeom></p:spPr></p:cxnSp>";
        var slide = Box(5, Text("Text box")).Replace("<p:cNvSpPr/>", "<p:cNvSpPr txBox=\"1\"/>", StringComparison.Ordinal) + Box(6, Text("Shape")) + connector;
        const string defaults = "<p:defaultTextStyle><a:lvl1pPr><a:defRPr sz=\"1800\" b=\"1\"/></a:lvl1pPr></p:defaultTextStyle>";
        const string style = "<a:style><a:lnRef idx=\"0\"><a:schemeClr val=\"accent2\"/></a:lnRef><a:fillRef idx=\"0\"><a:schemeClr val=\"accent2\"/></a:fillRef>" +
            "<a:effectRef idx=\"0\"><a:schemeClr val=\"accent2\"/></a:effectRef><a:fontRef idx=\"major\"><a:schemeClr val=\"lt1\"/></a:fontRef></a:style>";
        var bare = Objects(Deck(slide, defaults: defaults));
        var withDefaults = Objects(Deck(slide, defaults: defaults,
            objectDefaults: "<a:spDef><a:spPr>" + Solid("FFC000") + "<a:ln w=\"28575\">" + Solid("FF00FF") + "</a:ln></a:spPr><a:bodyPr anchor=\"ctr\" wrap=\"none\"/>" +
                "<a:lstStyle><a:lvl1pPr algn=\"r\" marL=\"342900\"><a:defRPr sz=\"1000\" i=\"1\"/></a:lvl1pPr></a:lstStyle>" + style + "</a:spDef>" +
                "<a:lnDef><a:spPr><a:ln w=\"28575\">" + Solid("FF00FF") + "<a:tailEnd type=\"arrow\"/></a:ln></a:spPr><a:bodyPr/><a:lstStyle/>" + style + "</a:lnDef>" +
                "<a:txDef><a:spPr>" + Solid("FFC000") + "</a:spPr><a:bodyPr wrap=\"none\" anchor=\"b\"/><a:lstStyle><a:lvl1pPr algn=\"r\"><a:defRPr sz=\"2400\"/></a:lvl1pPr></a:lstStyle>" + style + "</a:txDef>"));
        Assert.Equal(3, bare.Count);
        Assert.Equal(JsonSerializer.Serialize(bare), JsonSerializer.Serialize(withDefaults));
        var shape = Shape(withDefaults, 6);
        Assert.Equal(("none", "none", "top", true), (shape.FillPaint?.Kind, shape.StrokePaint?.Kind, shape.VerticalAlign, shape.WrapText));
        Assert.Equal(1800 / 100d * 96 / 72, shape.Paragraphs[0].Runs[0].FontSize, 3);
        Assert.Null(shape.Paragraphs[0].MarginLeft);
        Assert.Equal(1800 / 100d * 96 / 72, Shape(withDefaults, 5).Paragraphs[0].Runs[0].FontSize, 3);
        Assert.Equal(("none", null), (Shape(withDefaults, 7).StrokePaint?.Kind, Shape(withDefaults, 7).TailArrow));
    }

    [Fact]
    public void Shapes_that_specify_paragraph_layout_bullets_and_lines_themselves_are_unchanged_by_what_they_could_inherit()
    {
        const string properties = "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:solidFill><a:srgbClr val=\"123456\"/></a:solidFill>" +
            "<a:ln w=\"19050\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:srgbClr val=\"654321\"/></a:solidFill><a:prstDash val=\"dash\"/><a:miter lim=\"800000\"/>" +
            "<a:headEnd type=\"none\"/><a:tailEnd type=\"none\"/></a:ln>";
        const string body = "<a:bodyPr vert=\"horz\" wrap=\"square\" lIns=\"45720\" tIns=\"22860\" rIns=\"45720\" bIns=\"22860\" anchor=\"b\" rot=\"0\"><a:noAutofit/></a:bodyPr>";
        const string text = "<a:p><a:pPr algn=\"r\" lvl=\"1\" marL=\"457200\" indent=\"-228600\"><a:lnSpc><a:spcPct val=\"110000\"/></a:lnSpc><a:spcBef><a:spcPts val=\"200\"/></a:spcBef>" +
            "<a:spcAft><a:spcPts val=\"400\"/></a:spcAft><a:buClr><a:srgbClr val=\"FF0000\"/></a:buClr><a:buSzPct val=\"80000\"/><a:buFont typeface=\"Georgia\"/><a:buChar char=\"–\"/></a:pPr>" +
            "<a:r><a:rPr lang=\"en-US\" sz=\"1400\" b=\"0\" i=\"1\" u=\"sng\" strike=\"noStrike\"><a:solidFill><a:srgbClr val=\"ABCDEF\"/></a:solidFill>" +
            "<a:latin typeface=\"Georgia\"/><a:ea typeface=\"メイリオ\"/><a:cs typeface=\"Arial\"/></a:rPr><a:t>Own</a:t></a:r></a:p>";
        const string style = "<p:style><a:lnRef idx=\"0\"><a:schemeClr val=\"accent1\"/></a:lnRef><a:fillRef idx=\"0\"><a:schemeClr val=\"accent1\"/></a:fillRef>" +
            "<a:effectRef idx=\"0\"><a:schemeClr val=\"accent1\"/></a:effectRef><a:fontRef idx=\"minor\"><a:schemeClr val=\"dk1\"/></a:fontRef></p:style>";
        var slide = Placeholder(3, "<p:ph idx=\"1\"/>", text, Frame(20, 150, 400, 100) + properties, body) +
            Box(5, text, style, body, Frame(20, 300, 400, 100) + properties) +
            Box(6, text, style, body, Frame(20, 450, 400, 100) + properties).Replace("<p:cNvSpPr/>", "<p:cNvSpPr txBox=\"1\"/>", StringComparison.Ordinal);
        const string level = "<a:lvl2pPr algn=\"ctr\" marL=\"914400\" indent=\"-457200\"><a:lnSpc><a:spcPct val=\"70000\"/></a:lnSpc><a:spcBef><a:spcPts val=\"1000\"/></a:spcBef>" +
            "<a:spcAft><a:spcPts val=\"1000\"/></a:spcAft><a:buClr><a:srgbClr val=\"0000FF\"/></a:buClr><a:buSzPts val=\"3000\"/><a:buFont typeface=\"Wingdings\"/><a:buChar char=\"§\"/>" +
            "<a:defRPr sz=\"3200\" b=\"1\"/></a:lvl2pPr>";
        const string inheritedLine = "<a:ln w=\"57150\" cap=\"rnd\" cmpd=\"dbl\" algn=\"in\">" + "<a:solidFill><a:srgbClr val=\"00FF00\"/></a:solidFill>" +
            "<a:prstDash val=\"dot\"/><a:round/><a:headEnd type=\"oval\"/><a:tailEnd type=\"arrow\"/></a:ln>";
        var bare = Objects(Deck(slide));
        var inherited = Objects(Deck(slide,
            layout: Placeholder(3, "<p:ph idx=\"1\"/>", Text("Click to add text"), Frame(48, 168, 864, 480) + inheritedLine,
                "<a:bodyPr anchor=\"ctr\" wrap=\"none\" lIns=\"0\"><a:normAutofit fontScale=\"50000\" lnSpcReduction=\"20000\"/></a:bodyPr>", level),
            master: Placeholder(3, "<p:ph type=\"body\" idx=\"1\"/>", Text("Master text"), Frame(48, 168, 864, 480) + Solid("0000FF") + inheritedLine, list: level),
            textStyles: "<p:bodyStyle>" + level + "</p:bodyStyle><p:otherStyle>" + level + "</p:otherStyle>",
            defaults: "<p:defaultTextStyle>" + level + "</p:defaultTextStyle>",
            objectDefaults: string.Concat(new[] { "spDef", "lnDef", "txDef" }.Select(name => "<a:" + name + "><a:spPr>" + Solid("FFC000") + inheritedLine +
                "</a:spPr><a:bodyPr anchor=\"ctr\" wrap=\"none\" lIns=\"0\"><a:normAutofit fontScale=\"50000\" lnSpcReduction=\"20000\"/></a:bodyPr><a:lstStyle>" + level +
                "</a:lstStyle><a:style><a:lnRef idx=\"0\"><a:schemeClr val=\"accent2\"/></a:lnRef><a:fillRef idx=\"0\"><a:schemeClr val=\"accent2\"/></a:fillRef>" +
                "<a:effectRef idx=\"0\"><a:schemeClr val=\"accent2\"/></a:effectRef><a:fontRef idx=\"major\"><a:schemeClr val=\"lt1\"/></a:fontRef></a:style></a:" + name + ">"))));
        Assert.Equal(3, bare.Count);
        Assert.Equal(JsonSerializer.Serialize(bare), JsonSerializer.Serialize(inherited));
        var paragraph = Shape(inherited, 5).Paragraphs[0];
        Assert.Equal((48d, -24d, 110d, "#FF0000", "Georgia"), (Math.Round(paragraph.MarginLeft!.Value, 3), Math.Round(paragraph.Indent!.Value, 3),
            Math.Round(paragraph.LineSpacingPercent!.Value, 3), paragraph.BulletColor, paragraph.BulletFontFamily));
    }

    private const string Namespaces = " xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"" +
        " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    private static string Text(string value) => "<a:p><a:r><a:rPr lang=\"ja-JP\"/><a:t>" + value + "</a:t></a:r></a:p>";

    private static string Solid(string rgb) => "<a:solidFill><a:srgbClr val=\"" + rgb + "\"/></a:solidFill>";

    /// <summary>An a:xfrm in CSS pixels.</summary>
    private static string Frame(int x, int y, int width, int height) =>
        $"<a:xfrm><a:off x=\"{x * 9525}\" y=\"{y * 9525}\"/><a:ext cx=\"{width * 9525}\" cy=\"{height * 9525}\"/></a:xfrm>";

    private static string Placeholder(int id, string placeholder, string paragraphs, string properties = "", string body = "<a:bodyPr/>", string list = "") =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Placeholder {id}\"/><p:cNvSpPr/><p:nvPr>{placeholder}</p:nvPr></p:nvSpPr>" +
        $"<p:spPr>{properties}</p:spPr><p:txBody>{body}<a:lstStyle>{list}</a:lstStyle>{paragraphs}</p:txBody></p:sp>";

    private static string Box(int id, string paragraphs, string style = "", string body = "<a:bodyPr/>", string? properties = null) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Box {id}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr>" +
        (properties ?? Frame(100, 500 + id * 10, 300, 60) + "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>") +
        $"</p:spPr>{style}<p:txBody>{body}<a:lstStyle/>{paragraphs}</p:txBody></p:sp>";

    private static (double X, double Y, double Width, double Height) Bounds(WorkbookDrawingObject item)
    {
        var origin = item.Transform.Point(0, 0);
        return (Math.Round(origin.X, 3), Math.Round(origin.Y, 3), Math.Round(item.Width, 3), Math.Round(item.Height, 3));
    }

    private static WorkbookDrawingObject Shape(IReadOnlyList<WorkbookDrawingObject> objects, int id) =>
        Assert.Single(objects, item => item.Source?.Locators.Any(l => l.Kind == "shape_id" && l.Value == id.ToString(System.Globalization.CultureInfo.InvariantCulture)) == true);

    private static IReadOnlyList<WorkbookDrawingObject> Objects(byte[] package)
    {
        var extraction = new PptxAdapter().Extract(new MemoryStream(package));
        var scenes = SourceDrawingSceneJson.Read(extraction.Graph.FormatExtensions![SourceDrawingSceneJson.ExtensionKey]);
        return Assert.Single(Assert.Single(scenes).Value).Objects;
    }

    private static byte[] Deck(string slide, string layout = "", string master = "", string textStyles = "", string defaults = "", string slideColorMap = "", string objectDefaults = "")
    {
        const string relNs = "http://schemas.openxmlformats.org/package/2006/relationships";
        const string type = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
        static string Tree(string shapes) => "<p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>" + shapes + "</p:spTree></p:cSld>";
        static string Relationship(string kind, string target) =>
            "<Relationships xmlns=\"" + relNs + "\"><Relationship Id=\"rId1\" Type=\"" + type + kind + "\" Target=\"" + target + "\"/></Relationships>";
        static string Font(string name, string latin, string eastAsian) =>
            "<a:" + name + "><a:latin typeface=\"" + latin + "\"/><a:ea typeface=\"" + eastAsian + "\"/><a:cs typeface=\"\"/></a:" + name + ">";
        var colors = string.Concat(new[] { ("dk1", "000000"), ("lt1", "FFFFFF"), ("dk2", "112233"), ("lt2", "EEEEEE"), ("accent1", "C00000"), ("accent2", "ED7D31"),
            ("accent3", "A5A5A5"), ("accent4", "FFC000"), ("accent5", "5B9BD5"), ("accent6", "70AD47"), ("hlink", "0563C1"), ("folHlink", "954F72") }
            .Select(color => "<a:" + color.Item1 + "><a:srgbClr val=\"" + color.Item2 + "\"/></a:" + color.Item1 + ">"));
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/></Types>",
            ["_rels/.rels"] = Relationship("officeDocument", "ppt/presentation.xml"),
            ["ppt/presentation.xml"] = "<p:presentation" + Namespaces + "><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\"/></p:sldIdLst><p:sldSz cx=\"9144000\" cy=\"6858000\"/>" + defaults + "</p:presentation>",
            ["ppt/_rels/presentation.xml.rels"] = Relationship("slide", "slides/slide1.xml"),
            ["ppt/slides/slide1.xml"] = "<p:sld" + Namespaces + ">" + Tree(slide) + slideColorMap + "</p:sld>",
            ["ppt/slides/_rels/slide1.xml.rels"] = Relationship("slideLayout", "../slideLayouts/slideLayout1.xml"),
            ["ppt/slideLayouts/slideLayout1.xml"] = "<p:sldLayout" + Namespaces + ">" + Tree(layout) + "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>",
            ["ppt/slideLayouts/_rels/slideLayout1.xml.rels"] = Relationship("slideMaster", "../slideMasters/slideMaster1.xml"),
            ["ppt/slideMasters/slideMaster1.xml"] = "<p:sldMaster" + Namespaces + ">" + Tree(master) +
                "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
                "<p:txStyles>" + textStyles + "</p:txStyles></p:sldMaster>",
            ["ppt/slideMasters/_rels/slideMaster1.xml.rels"] = Relationship("theme", "../theme/theme1.xml"),
            ["ppt/theme/theme1.xml"] = "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Test\"><a:themeElements><a:clrScheme name=\"Test\">" + colors +
                "</a:clrScheme><a:fontScheme name=\"Test\">" + Font("majorFont", "Arial Black", "游ゴシック Light") + Font("minorFont", "Verdana", "游明朝") + "</a:fontScheme></a:themeElements>" +
                (objectDefaults.Length == 0 ? "" : "<a:objectDefaults>" + objectDefaults + "</a:objectDefaults>") + "</a:theme>",
        };
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var part in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), new UTF8Encoding(false));
                writer.Write(part.Value);
            }
        return output.ToArray();
    }
}
