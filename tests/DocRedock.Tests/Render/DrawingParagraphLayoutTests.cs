using System.Text.Json;
using System.Xml;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Drawing;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

/// <summary>Paragraph layout (a:pPr and list-style levels) and bullet appearance of a drawing shape, read and drawn.</summary>
public sealed class DrawingParagraphLayoutTests
{
    [Fact]
    public void An_Excel_shape_keeps_explicit_margin_indent_line_spacing_and_spacing_before_and_after()
    {
        var shape = Sheet("<a:bodyPr/><a:lstStyle/><a:p><a:pPr marL='342900' indent='-342900'><a:lnSpc><a:spcPct val='90000'/></a:lnSpc>" +
            "<a:spcBef><a:spcPct val='50000'/></a:spcBef><a:spcAft><a:spcPts val='600'/></a:spcAft><a:buChar char='•'/></a:pPr>" +
            "<a:r><a:rPr sz='1800'/><a:t>Spaced</a:t></a:r></a:p>" +
            "<a:p><a:pPr marL='95250' indent='190500'><a:lnSpc><a:spcPts val='1500'/></a:lnSpc></a:pPr><a:r><a:rPr sz='1800'/><a:t>Exact</a:t></a:r></a:p>");
        var first = shape.Paragraphs[0];
        Assert.Equal(36, first.MarginLeft!.Value, 3);
        Assert.Equal(-36, first.Indent!.Value, 3);
        Assert.Equal(90, first.LineSpacingPercent!.Value, 3);
        Assert.Null(first.LineSpacingExact);
        // Half a line (1.2 times the 18 pt, 24 px, text); 6 pt.
        Assert.Equal(14.4, first.SpaceBefore!.Value, 3);
        Assert.Equal(8, first.SpaceAfter!.Value, 3);
        var second = shape.Paragraphs[1];
        Assert.Equal((10d, 20d), (Math.Round(second.MarginLeft!.Value, 3), Math.Round(second.Indent!.Value, 3)));
        Assert.Equal(20, second.LineSpacingExact!.Value, 3);
        Assert.Null(second.LineSpacingPercent);
        Assert.Null(second.SpaceBefore);

        var html = Render(shape);
        Assert.Contains("text-align:left;padding-left:36px;text-indent:-36px;line-height:1.035;margin-top:14.4px;margin-bottom:8px\">", html);
        // The hanging bullet fills the room up to the text and is not itself indented.
        Assert.Contains("<span class=\"source-caption-marker\" style=\"display:inline-block;text-indent:0;min-width:36px\">•</span>", html);
        Assert.Contains("text-align:left;padding-left:10px;text-indent:20px;line-height:20px\">", html);
    }

    [Fact]
    public void A_saved_line_spacing_reduction_shrinks_proportional_line_spacing_as_the_font_scale_shrinks_the_runs()
    {
        var shape = Sheet("<a:bodyPr><a:normAutofit fontScale='90000' lnSpcReduction='20000'/></a:bodyPr><a:lstStyle/>" +
            "<a:p><a:r><a:rPr sz='2000'/><a:t>Single</a:t></a:r></a:p>" +
            "<a:p><a:pPr><a:lnSpc><a:spcPct val='150000'/></a:lnSpc></a:pPr><a:r><a:rPr sz='2000'/><a:t>Wide</a:t></a:r></a:p>" +
            "<a:p><a:pPr><a:lnSpc><a:spcPts val='1500'/></a:lnSpc></a:pPr><a:r><a:rPr sz='2000'/><a:t>Exact</a:t></a:r></a:p>");
        Assert.Equal(2000 / 100d * 96 / 72 * 0.9, shape.Paragraphs[0].Runs[0].FontSize, 3);
        Assert.Equal(80, shape.Paragraphs[0].LineSpacingPercent!.Value, 3);
        Assert.Equal(120, shape.Paragraphs[1].LineSpacingPercent!.Value, 3);
        Assert.Null(shape.Paragraphs[2].LineSpacingPercent);
        Assert.Equal(20, shape.Paragraphs[2].LineSpacingExact!.Value, 3);
        var html = Render(shape);
        Assert.Contains("line-height:0.92\">", html);
        Assert.Contains("line-height:1.38\">", html);

        var unreduced = Sheet("<a:bodyPr><a:normAutofit fontScale='90000'/></a:bodyPr><a:lstStyle/><a:p><a:r><a:rPr sz='2000'/><a:t>Single</a:t></a:r></a:p>");
        Assert.Null(unreduced.Paragraphs[0].LineSpacingPercent);
    }

    [Fact]
    public void An_explicit_bullet_keeps_its_color_size_and_font_and_a_symbol_font_bullet_is_drawn_as_its_character()
    {
        var shape = Sheet("<a:bodyPr/><a:lstStyle/>" +
            "<a:p><a:pPr><a:buClr><a:srgbClr val='FF0000'/></a:buClr><a:buSzPct val='50000'/><a:buFont typeface='Wingdings'/><a:buChar char='§'/></a:pPr>" +
            "<a:r><a:rPr sz='1800'/><a:t>Symbol</a:t></a:r></a:p>" +
            "<a:p><a:pPr><a:buSzPts val='1200'/><a:buFont typeface='Georgia'/><a:buChar char='–'/></a:pPr><a:r><a:rPr sz='1800'/><a:t>Dash</a:t></a:r></a:p>" +
            "<a:p><a:pPr><a:buClrTx/><a:buSzTx/><a:buFontTx/><a:buChar char='*'/></a:pPr><a:r><a:rPr sz='1800'/><a:t>Follows the text</a:t></a:r></a:p>");
        Assert.Equal(("§", "Wingdings", "#FF0000"), (shape.Paragraphs[0].BulletText, shape.Paragraphs[0].BulletFontFamily, shape.Paragraphs[0].BulletColor));
        Assert.Equal(12, shape.Paragraphs[0].BulletSize!.Value, 3);
        Assert.Equal(("–", "Georgia", null), (shape.Paragraphs[1].BulletText, shape.Paragraphs[1].BulletFontFamily, shape.Paragraphs[1].BulletColor));
        Assert.Equal(16, shape.Paragraphs[1].BulletSize!.Value, 3);
        Assert.Equal(("*", null, null, null), (shape.Paragraphs[2].BulletText, shape.Paragraphs[2].BulletFontFamily, shape.Paragraphs[2].BulletColor, shape.Paragraphs[2].BulletSize));

        var html = Render(shape);
        Assert.Contains("<span class=\"source-caption-marker\" style=\"display:inline-block;margin-right:.4em;font-size:12px;color:#FF0000\">▪</span>", html);
        Assert.DoesNotContain("Wingdings", html);
        Assert.Contains("<span class=\"source-caption-marker\" style=\"display:inline-block;margin-right:.4em;font-family:&#39;Georgia&#39;,sans-serif;font-size:16px\">–</span>", html);
        Assert.Contains("<span class=\"source-caption-marker\" style=\"display:inline-block;margin-right:.4em\">*</span>", html);
    }

    [Fact]
    public void An_Excel_shape_takes_alignment_layout_and_bullet_of_a_level_from_its_own_list_style()
    {
        var shape = Sheet("<a:bodyPr/><a:lstStyle><a:lvl1pPr algn='r' marL='190500'><a:spcBef><a:spcPts val='300'/></a:spcBef></a:lvl1pPr>" +
            "<a:lvl2pPr algn='ctr' marL='381000' indent='-190500'><a:buClr><a:srgbClr val='0000FF'/></a:buClr><a:buChar char='o'/></a:lvl2pPr></a:lstStyle>" +
            "<a:p><a:r><a:t>Right</a:t></a:r></a:p><a:p><a:pPr lvl='1'/><a:r><a:t>Centered</a:t></a:r></a:p>" +
            "<a:p><a:pPr algn='l' marL='0'/><a:r><a:t>Own</a:t></a:r></a:p>");
        Assert.Equal("right", shape.TextAlign);
        Assert.Equal(("r", 20d, 4d), (shape.Paragraphs[0].Alignment, Math.Round(shape.Paragraphs[0].MarginLeft!.Value, 3), Math.Round(shape.Paragraphs[0].SpaceBefore!.Value, 3)));
        Assert.Equal(("ctr", 40d, -20d, "o", "#0000FF"), (shape.Paragraphs[1].Alignment, Math.Round(shape.Paragraphs[1].MarginLeft!.Value, 3),
            Math.Round(shape.Paragraphs[1].Indent!.Value, 3), shape.Paragraphs[1].BulletText, shape.Paragraphs[1].BulletColor));
        // The paragraph's own values win; what it leaves open still comes from the level.
        Assert.Equal(("l", 0d, 4d), (shape.Paragraphs[2].Alignment, Math.Round(shape.Paragraphs[2].MarginLeft!.Value, 3), Math.Round(shape.Paragraphs[2].SpaceBefore!.Value, 3)));
        Assert.Contains("text-align:right;padding-left:20px;text-indent:0px;margin-top:4px\">", Render(shape));
    }

    [Fact]
    public void A_paragraph_without_the_new_properties_is_read_and_drawn_as_before()
    {
        var shape = Sheet("<a:bodyPr/><a:lstStyle/><a:p><a:pPr algn='ctr' lvl='1'><a:buChar char='•'/></a:pPr><a:r><a:rPr sz='1400' b='1'/><a:t>Plain</a:t></a:r></a:p>");
        var paragraph = Assert.Single(shape.Paragraphs);
        Assert.Equal(new DrawingTextParagraph(paragraph.Runs, "ctr") { BulletText = "•", Level = 1 }, paragraph);
        Assert.Contains("<div class=\"source-caption-paragraph\" style=\"min-height:1em;text-align:center;padding-left:20px\">" +
            "<span class=\"source-caption-marker\" style=\"display:inline-block;margin-right:.4em\">•</span><span style=\"font-family:&#39;Calibri&#39;,sans-serif;font-size:18.6667px;", Render(shape));
    }

    [Fact]
    public void Paragraph_layout_outside_its_source_range_does_not_pass_the_scene_validation()
    {
        var shape = Sheet("<a:bodyPr/><a:lstStyle/><a:p><a:pPr marL='342900' indent='-342900'/><a:r><a:t>Text</a:t></a:r></a:p>");
        IReadOnlyDictionary<string, WorkbookDrawingScene> Reread(Func<DrawingTextParagraph, DrawingTextParagraph> change) => WorkbookDrawingSceneJson.Read(
            WorkbookDrawingSceneJson.Write(new Dictionary<string, WorkbookDrawingScene>
            { ["s"] = new("Sheet1", [shape with { Paragraphs = [change(shape.Paragraphs[0])] }]) }));
        Assert.Equal(-36, Assert.Single(Reread(p => p)["s"].Objects).Paragraphs[0].Indent!.Value, 3);
        Assert.Empty(Reread(p => p with { MarginLeft = -1 })["s"].Objects);
        Assert.Empty(Reread(p => p with { Indent = 1e9 })["s"].Objects);
        Assert.Empty(Reread(p => p with { LineSpacingPercent = 20000 })["s"].Objects);
        Assert.Empty(Reread(p => p with { SpaceAfter = -5 })["s"].Objects);
        Assert.Empty(Reread(p => p with { BulletSize = 0 })["s"].Objects);
        Assert.Empty(Reread(p => p with { BulletColor = new string('F', 65) })["s"].Objects);
    }

    [Fact]
    public void Percent_spacing_before_and_after_is_a_share_of_one_line_of_the_largest_text()
    {
        var shape = Sheet("<a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:spcBef><a:spcPct val='100000'/></a:spcBef><a:spcAft><a:spcPct val='50000'/></a:spcAft></a:pPr>" +
            "<a:r><a:rPr sz='1000'/><a:t>small </a:t></a:r><a:r><a:rPr sz='2000'/><a:t>large</a:t></a:r></a:p>");
        // One line of the 20 pt (26.667 px) run is 1.2 times its size: 32 px; half a line after.
        Assert.Equal(32, shape.Paragraphs[0].SpaceBefore!.Value, 3);
        Assert.Equal(16, shape.Paragraphs[0].SpaceAfter!.Value, 3);
        Assert.Contains("margin-top:32px;margin-bottom:16px\">", Render(shape));
    }

    [Fact]
    public void Explicit_tab_stops_place_the_text_after_a_tab_at_left_right_centre_and_decimal_stops()
    {
        var shape = Sheet("<a:bodyPr/><a:lstStyle/><a:p><a:pPr marL='95250' defTabSz='457200'><a:tabLst><a:tab pos='2857500' algn='dec'/><a:tab pos='952500' algn='l'/>" +
            "<a:tab pos='1905000' algn='r'/><a:tab pos='2381250' algn='ctr'/></a:tabLst></a:pPr>" +
            "<a:r><a:t>Item\tleft\tright\tmid\t12.50\tpast</a:t></a:r></a:p>");
        var paragraph = shape.Paragraphs[0];
        Assert.Equal(new DrawingTabStop[] { new(100), new(200, "r"), new(250, "ctr"), new(300, "dec") }, paragraph.TabStops!.Select(stop => stop with { Position = Math.Round(stop.Position, 3) }).ToArray());
        Assert.Equal(48, paragraph.DefaultTabSize!.Value, 3);
        Assert.Equal("Item\tleft\tright\tmid\t12.50\tpast", shape.Text);

        var html = Render(shape);
        const string run = "<span style=\"font-family:&#39;Calibri&#39;,sans-serif;font-size:14.6667px;color:#000000;opacity:1;font-weight:normal;font-style:normal;text-decoration:none\">";
        static string Stop(double width) => "<span class=\"source-caption-tab\" style=\"display:inline-block;min-width:" + width.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px;text-indent:0;text-align:start;white-space:pre\">";
        static string Shift(string by) => "<span style=\"display:inline-block;width:0;text-indent:0;white-space:pre\"><span style=\"display:inline-block;transform:translateX(" + by + ")\">";
        // The stops are measured from the text rectangle and the line starts at the 10 px margin: 90, 190, 240 and 290 px on.
        Assert.Contains("padding-left:10px;text-indent:0px;tab-size:48px\">" + Stop(290) + Stop(240) + Stop(190) + Stop(90) + run + "Item</span></span>" +
            run + "left</span></span>" + Shift("-100%") + run + "right</span></span></span></span>" +
            Shift("-50%") + run + "mid</span></span></span></span>" +
            // The part before the decimal point ends at the stop; the tab no stop is left for stays a tab character.
            Shift("-100%") + run + "12</span></span></span>" + run + ".50</span>" + run + "\tpast</span></div>", html);
    }

    [Fact]
    public void A_line_after_a_break_counts_its_tabs_anew_and_default_stops_become_the_tab_size()
    {
        var stopped = Sheet("<a:bodyPr/><a:lstStyle/><a:p><a:pPr marL='381000' indent='-381000'><a:tabLst><a:tab pos='190500'/><a:tab pos='952500' algn='r'/></a:tabLst><a:buChar char='•'/></a:pPr>" +
            "<a:r><a:t>A\t1</a:t></a:r><a:br/><a:r><a:t>B\t22</a:t></a:r></a:p>");
        var html = Render(stopped);
        // Both lines start at the 40 px margin (the first after its hanging bullet), beyond the 20 px stop: the tab takes the stop at 100 px.
        Assert.Equal(2, html.Split("<span class=\"source-caption-tab\" style=\"display:inline-block;min-width:60px;").Length - 1);
        Assert.DoesNotContain("min-width:-", html);
        Assert.Contains("translateX(-100%)\"><span style=\"font-family:&#39;Calibri&#39;,sans-serif;font-size:14.6667px;color:#000000;opacity:1;font-weight:normal;font-style:normal;text-decoration:none\">1</span></span></span>", html);
        Assert.Contains(">22</span></span></span></div>", html);

        var plain = Sheet("<a:bodyPr/><a:lstStyle><a:lvl1pPr defTabSz='914400'/></a:lstStyle><a:p><a:r><a:t>A\tB</a:t></a:r></a:p><a:p><a:r><a:t>No tab</a:t></a:r></a:p>");
        Assert.Equal((96d, 96d), (plain.Paragraphs[0].DefaultTabSize!.Value, plain.Paragraphs[1].DefaultTabSize!.Value));
        Assert.Null(plain.Paragraphs[0].TabStops);
        html = Render(plain);
        Assert.Contains("padding-left:0px;tab-size:96px\"><span style=\"font-family:&#39;Calibri&#39;,sans-serif;font-size:14.6667px;color:#000000;opacity:1;font-weight:normal;font-style:normal;text-decoration:none\">A\tB</span></div>", html);
        Assert.Equal(1, html.Split("tab-size:").Length - 1);
        Assert.DoesNotContain("source-caption-tab", html);
    }

    [Fact]
    public void A_right_margin_a_right_to_left_paragraph_and_a_font_alignment_are_read_and_drawn()
    {
        var shape = Sheet("<a:bodyPr/><a:lstStyle><a:lvl1pPr marR='190500' fontAlgn='ctr'/><a:lvl2pPr rtl='1' fontAlgn='b'/></a:lstStyle>" +
            "<a:p><a:r><a:rPr sz='1000'/><a:t>small</a:t></a:r><a:r><a:rPr sz='2000' baseline='30000'/><a:t>raised</a:t></a:r></a:p>" +
            "<a:p><a:pPr lvl='1' marL='381000' indent='-190500' marR='95250'><a:tabLst><a:tab pos='952500' algn='r'/></a:tabLst></a:pPr><a:r><a:t>rtl\tstop</a:t></a:r></a:p>" +
            "<a:p><a:pPr lvl='1' algn='r' fontAlgn='t'/><a:r><a:t>trailing</a:t></a:r></a:p>" +
            "<a:p><a:pPr lvl='1' rtl='0' fontAlgn='base' algn='ctr'/><a:r><a:t>ltr</a:t></a:r></a:p>");
        Assert.Equal((20d, false, "ctr"), (shape.Paragraphs[0].MarginRight, shape.Paragraphs[0].RightToLeft, shape.Paragraphs[0].FontAlignment));
        Assert.Equal((10d, true, "b"), (shape.Paragraphs[1].MarginRight, shape.Paragraphs[1].RightToLeft, shape.Paragraphs[1].FontAlignment));
        Assert.Equal((null, true, "t"), (shape.Paragraphs[2].MarginRight, shape.Paragraphs[2].RightToLeft, shape.Paragraphs[2].FontAlignment));
        Assert.Equal((null, false, null), (shape.Paragraphs[3].MarginRight, shape.Paragraphs[3].RightToLeft, shape.Paragraphs[3].FontAlignment));

        var html = Render(shape);
        // A run on the common line takes the font alignment; a superscript keeps its own.
        Assert.Contains("text-align:left;padding-left:0px;padding-right:20px\"><span style=\"font-family:&#39;Calibri&#39;,sans-serif;font-size:13.3333px;vertical-align:middle;color:", html);
        Assert.Contains("font-size:17.7778px;vertical-align:super;color:", html);
        // Right to left: the margins change sides, l is the right edge, and a right stop holds its text back to the left of the pen.
        Assert.Contains("text-align:right;padding-right:40px;text-indent:-20px;padding-left:10px;direction:rtl\">" +
            "<span class=\"source-caption-tab\" style=\"display:inline-block;min-width:80px;text-indent:0;text-align:start;white-space:pre\">", html);
        Assert.Contains("vertical-align:bottom;color:#000000;opacity:1;font-weight:normal;font-style:normal;text-decoration:none\">rtl</span></span>" +
            "<span style=\"display:inline-block;width:0;text-indent:0;white-space:pre\"><span style=\"display:inline-block;transform:translateX(100%)\">", html);
        Assert.Contains("text-align:left;padding-right:20px;direction:rtl\"><span style=\"font-family:&#39;Calibri&#39;,sans-serif;font-size:14.6667px;vertical-align:top;color:", html);
        Assert.Contains("text-align:center;padding-left:20px\"><span style=\"font-family:&#39;Calibri&#39;,sans-serif;font-size:14.6667px;color:", html);
    }

    [Fact]
    public void A_picture_bullet_is_drawn_as_its_picture_at_the_bullet_size_and_falls_back_to_a_round_bullet()
    {
        var png = PngRasterImage.Encode(2, 1, [255, 0, 0, 0, 0, 255]);
        var pictures = new DrawingMlPictureFills(uri => uri == "/xl/media/image1.png" ? png : null);
        var relationships = new Dictionary<string, string> { ["rId1"] = "../media/image1.png", ["rId2"] = "../media/missing.png" };
        var warnings = new List<string>();
        var shape = Sheet("<a:bodyPr/><a:lstStyle><a:lvl1pPr><a:buSzPct val='50000'/><a:buBlip><a:blip r:embed='rId1'/></a:buBlip></a:lvl1pPr></a:lstStyle>" +
            "<a:p><a:r><a:rPr sz='1800'/><a:t>Half</a:t></a:r></a:p>" +
            "<a:p><a:pPr><a:buSzTx/></a:pPr><a:r><a:rPr sz='1800'/><a:t>Text height</a:t></a:r></a:p>" +
            "<a:p><a:pPr><a:buBlip><a:blip r:embed='rId2'/></a:buBlip></a:pPr><a:r><a:rPr sz='1800'/><a:t>Missing</a:t></a:r></a:p>" +
            "<a:p><a:pPr><a:buNone/></a:pPr><a:r><a:t>None</a:t></a:r></a:p>", relationships, pictures, warnings);
        var reference = "data:image/png;base64," + Convert.ToBase64String(png);
        Assert.Equal(("•", reference, 12d), (shape.Paragraphs[0].BulletText, shape.Paragraphs[0].BulletImage, Math.Round(shape.Paragraphs[0].BulletSize!.Value, 3)));
        Assert.Equal((reference, 24d), (shape.Paragraphs[1].BulletImage, Math.Round(shape.Paragraphs[1].BulletSize!.Value, 3)));
        Assert.Equal(("•", null), (shape.Paragraphs[2].BulletText, shape.Paragraphs[2].BulletImage));
        Assert.Equal((null, null), (shape.Paragraphs[3].BulletText, shape.Paragraphs[3].BulletImage));
        Assert.Contains(warnings, warning => warning.Contains("Picture bullet is drawn as a round bullet: its package part is missing.", StringComparison.Ordinal));

        var html = Render(shape);
        Assert.Contains("<span class=\"source-caption-marker\" style=\"display:inline-block;margin-right:.4em\"><img alt=\"\" src=\"" + reference + "\" style=\"height:12px\"></span>", html);
        Assert.Contains("<img alt=\"\" src=\"" + reference + "\" style=\"height:24px\"></span>", html);
        Assert.Contains("<span class=\"source-caption-marker\" style=\"display:inline-block;margin-right:.4em;font-size:12px\">•</span>", html);

        // A reference that is no embeddable picture is not written: the character stands for it.
        var unsafeImage = Render(shape with { Paragraphs = [shape.Paragraphs[0] with { BulletImage = "data:text/html;base64,PGI+" }] });
        Assert.DoesNotContain("<img", unsafeImage);
        Assert.Contains("font-size:12px\">•</span>", unsafeImage);
    }

    [Fact]
    public void A_translucent_bullet_color_keeps_its_alpha()
    {
        var shape = Sheet("<a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:buClr><a:srgbClr val='FF0000'><a:alpha val='40000'/></a:srgbClr></a:buClr><a:buChar char='•'/></a:pPr><a:r><a:t>Faint</a:t></a:r></a:p>" +
            "<a:p><a:pPr><a:buClr><a:srgbClr val='00FF00'><a:alpha val='100000'/></a:srgbClr></a:buClr><a:buChar char='•'/></a:pPr><a:r><a:t>Solid</a:t></a:r></a:p>");
        Assert.Equal(("#FF0000", 0.4), (shape.Paragraphs[0].BulletColor, shape.Paragraphs[0].BulletOpacity));
        Assert.Equal(("#00FF00", null), (shape.Paragraphs[1].BulletColor, shape.Paragraphs[1].BulletOpacity));
        var html = Render(shape);
        Assert.Contains("margin-right:.4em;color:#FF0000;opacity:0.4\">•</span>", html);
        Assert.Contains("margin-right:.4em;color:#00FF00\">•</span>", html);
    }

    [Theory]
    [InlineData("Wingdings 2", "P", "✓")]
    [InlineData("Wingdings 2", "\uF052", "☑")]
    [InlineData("Wingdings 2", "\uF097", "●")]
    [InlineData("Wingdings 3", "}", "▶")]
    [InlineData("Wingdings 3", "\uF070", "▲")]
    [InlineData("Wingdings 3", "h", "↑")]
    [InlineData("Webdings", "4", "▶")]
    [InlineData("webdings", "\uF061", "✔")]
    [InlineData("Webdings", "n", "●")]
    public void A_bullet_of_Wingdings_2_Wingdings_3_or_Webdings_is_drawn_as_the_character_it_shows(string font, string character, string expected)
    {
        var shape = Sheet("<a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:buFont typeface='" + font + "'/><a:buChar char='" + character + "'/></a:pPr><a:r><a:t>Item</a:t></a:r></a:p>" +
            "<a:p><a:pPr><a:buFont typeface='" + font + "'/><a:buChar char='\uF0FF'/></a:pPr><a:r><a:t>Unmapped</a:t></a:r></a:p>");
        var html = Render(shape);
        Assert.Contains("<span class=\"source-caption-marker\" style=\"display:inline-block;margin-right:.4em\">" + expected + "</span>", html);
        // A character without a counterpart stays in its font.
        Assert.Contains("margin-right:.4em;font-family:&#39;" + font + "&#39;,sans-serif\">\uF0FF</span>", html);
    }

    [Fact]
    public void The_nearest_layer_that_chooses_a_bullet_decides_it()
    {
        var shape = Sheet("<a:bodyPr/><a:lstStyle><a:lvl1pPr><a:buChar char='x'/></a:lvl1pPr><a:lvl2pPr><a:buNone/></a:lvl2pPr></a:lstStyle>" +
            "<a:p><a:r><a:t>Level</a:t></a:r></a:p>" +
            "<a:p><a:pPr><a:buAutoNum type='arabicPeriod' startAt='3'/></a:pPr><a:r><a:t>Numbered</a:t></a:r></a:p>" +
            "<a:p><a:pPr><a:buNone/></a:pPr><a:r><a:t>None</a:t></a:r></a:p>" +
            "<a:p><a:pPr lvl='1'/><a:r><a:t>Level without</a:t></a:r></a:p>" +
            "<a:p><a:pPr lvl='1'><a:buChar char='o'/></a:pPr><a:r><a:t>Own</a:t></a:r></a:p>", warnings: []);
        // The paragraph's automatic number is not replaced by the character of its level.
        Assert.Equal(["x", "3.", null, null, "o"], shape.Paragraphs.Select(paragraph => paragraph.BulletText));
    }

    [Fact]
    public void Paragraph_properties_this_version_adds_outside_their_range_do_not_pass_the_scene_validation()
    {
        var shape = Sheet("<a:bodyPr/><a:lstStyle/><a:p><a:pPr marR='95250' rtl='1' fontAlgn='t' defTabSz='914400'><a:tabLst><a:tab pos='952500' algn='dec'/></a:tabLst>" +
            "<a:buClr><a:srgbClr val='FF0000'><a:alpha val='50000'/></a:srgbClr></a:buClr><a:buChar char='•'/></a:pPr><a:r><a:t>Text</a:t></a:r></a:p>");
        IReadOnlyDictionary<string, WorkbookDrawingScene> Reread(Func<DrawingTextParagraph, DrawingTextParagraph> change) => WorkbookDrawingSceneJson.Read(
            WorkbookDrawingSceneJson.Write(new Dictionary<string, WorkbookDrawingScene>
            { ["s"] = new("Sheet1", [shape with { Paragraphs = [change(shape.Paragraphs[0])] }]) }));
        var kept = Assert.Single(Reread(p => p)["s"].Objects).Paragraphs[0];
        Assert.Equal((10d, true, "t", 96d, 0.5), (kept.MarginRight, kept.RightToLeft, kept.FontAlignment, kept.DefaultTabSize, kept.BulletOpacity));
        Assert.Equal(new DrawingTabStop(100, "dec"), Assert.Single(kept.TabStops!));
        Assert.Empty(Reread(p => p with { MarginRight = -1 })["s"].Objects);
        Assert.Empty(Reread(p => p with { FontAlignment = "middle" })["s"].Objects);
        Assert.Empty(Reread(p => p with { DefaultTabSize = 0 })["s"].Objects);
        Assert.Empty(Reread(p => p with { BulletOpacity = 1.5 })["s"].Objects);
        Assert.Empty(Reread(p => p with { TabStops = [new(1e9)] })["s"].Objects);
        Assert.Empty(Reread(p => p with { TabStops = [new(10, "bar")] })["s"].Objects);
        Assert.Empty(Reread(p => p with { TabStops = Enumerable.Range(0, 33).Select(i => new DrawingTabStop(i)).ToArray() })["s"].Objects);
        Assert.Empty(Reread(p => p with { BulletImage = new string('A', WorkbookDrawingSceneJson.MaxFillImageReference + 1) })["s"].Objects);
    }

    [Fact]
    public void A_Word_text_box_paragraph_keeps_indent_spacing_alignment_direction_and_tab_stops()
    {
        var xml = new XmlDocument { XmlResolver = null };
        xml.LoadXml("<wps:wsp xmlns:wps='http://schemas.microsoft.com/office/word/2010/wordprocessingShape' " +
            "xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'>" +
            "<wps:cNvPr id='1'/><wps:spPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='2857500' cy='1905000'/></a:xfrm><a:prstGeom prst='rect'/></wps:spPr><wps:txbx><w:txbxContent>" +
            "<w:p><w:pPr><w:tabs><w:tab w:val='clear' w:pos='720'/><w:tab w:val='right' w:pos='3000'/></w:tabs><w:spacing w:before='120' w:after='240' w:line='360' w:lineRule='auto'/>" +
            "<w:ind w:left='720' w:right='300' w:hanging='360'/><w:jc w:val='both'/></w:pPr><w:r><w:t>Hanging</w:t></w:r><w:r><w:tab/><w:t>stop</w:t></w:r></w:p>" +
            "<w:p><w:pPr><w:bidi/><w:spacing w:line='300' w:lineRule='exact'/><w:ind w:start='150' w:firstLine='450'/><w:jc w:val='end'/></w:pPr><w:r><w:t>First line</w:t></w:r></w:p>" +
            "<w:p><w:r><w:t>Plain</w:t></w:r></w:p>" +
            "</w:txbxContent></wps:txbx><wps:bodyPr/></wps:wsp>");
        var shape = Assert.Single(DrawingMlSceneReader.Read(xml.DocumentElement!, "docx", "/word/document.xml", null));
        var first = shape.Paragraphs[0];
        // 720 and 300 twentieths of a point are 48 and 20 px; hanging 360 is an indent of -24 px.
        Assert.Equal(("just", 48d, 20d, -24d), (first.Alignment, Math.Round(first.MarginLeft!.Value, 3), Math.Round(first.MarginRight!.Value, 3), Math.Round(first.Indent!.Value, 3)));
        // 6 pt before and 12 pt after; 360 240ths of a line.
        Assert.Equal((8d, 16d, 150d), (Math.Round(first.SpaceBefore!.Value, 3), Math.Round(first.SpaceAfter!.Value, 3), Math.Round(first.LineSpacingPercent!.Value, 3)));
        Assert.Null(first.LineSpacingExact);
        Assert.Equal(new DrawingTabStop(200, "r"), Assert.Single(first.TabStops!));
        var second = shape.Paragraphs[1];
        Assert.Equal(("r", true, 10d, 30d, 20d), (second.Alignment, second.RightToLeft, Math.Round(second.MarginLeft!.Value, 3), Math.Round(second.Indent!.Value, 3), Math.Round(second.LineSpacingExact!.Value, 3)));
        Assert.Null(second.LineSpacingPercent);
        Assert.Equal(new DrawingTextParagraph(shape.Paragraphs[2].Runs), shape.Paragraphs[2]);

        var html = Render(shape);
        Assert.Contains("text-align:justify;padding-left:48px;text-indent:-24px;line-height:1.725;margin-top:8px;margin-bottom:16px;padding-right:20px\">" +
            "<span class=\"source-caption-tab\" style=\"display:inline-block;min-width:176px;", html);
        Assert.Contains("text-align:left;padding-right:10px;text-indent:30px;line-height:20px;direction:rtl\">", html);
        Assert.Contains("<div class=\"source-caption-paragraph\" style=\"min-height:1em;text-align:left;padding-left:0px\"><span style=\"font-family:&#39;Calibri&#39;,sans-serif;font-size:14.6667px;color:#000000;", html);
    }

    private static WorkbookDrawingObject Sheet(string textBody, IReadOnlyDictionary<string, string>? relationships = null, DrawingMlPictureFills? pictures = null,
        List<string>? warnings = null)
    {
        var xml = new XmlDocument { XmlResolver = null };
        xml.LoadXml("<xdr:wsDr xmlns:xdr='http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main' " +
            "xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'>" +
            "<xdr:sp><xdr:nvSpPr><xdr:cNvPr id='2' name='Shape 1'/><xdr:cNvSpPr/></xdr:nvSpPr><xdr:spPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='2857500' cy='1905000'/></a:xfrm>" +
            "<a:prstGeom prst='rect'><a:avLst/></a:prstGeom></xdr:spPr><xdr:txBody>" + textBody + "</xdr:txBody></xdr:sp></xdr:wsDr>");
        var found = new List<string>();
        var shape = Assert.Single(DrawingMlSceneReader.Read(xml.DocumentElement!, "xlsx", "/xl/drawings/drawing1.xml", null, relationships, warnings: found, pictureFills: pictures));
        if (warnings is null) Assert.Empty(found); else warnings.AddRange(found);
        return shape;
    }

    private static string Render(WorkbookDrawingObject shape) => ReadableHtmlWriter.WriteDocument(
        new("doc", "en", DocumentFormatKind.Xlsx, [new ReadableSourceDrawingScene(new("source", 400, 300, [shape]))])).Html;
}
