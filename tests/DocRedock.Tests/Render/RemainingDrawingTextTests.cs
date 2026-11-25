using System.Xml;
using DocRedock.Formats.OpenXml.Drawing;

namespace DocRedock.Tests.Render;

public sealed class RemainingDrawingTextTests
{
    [Theory]
    [InlineData("ABC123", "Cambria")]
    [InlineData("日本語かな", "Yu Mincho")]
    [InlineData("مرحبا", "Traditional Arabic")]
    public void Source_script_typefaces_are_used_for_the_matching_text(string text, string family)
    {
        var value = DrawingMlStyleReader.Read(Shape(text));
        var run = Assert.Single(Assert.Single(value.Paragraphs).Runs);
        Assert.Equal(text, run.Text);
        Assert.Equal(family, run.FontFamily);
    }

    [Fact]
    public void A_mixed_source_run_keeps_text_order_and_its_distinct_script_fonts()
    {
        var value = DrawingMlStyleReader.Read(Shape("ABC日本語123مرحبا"));
        var runs = Assert.Single(value.Paragraphs).Runs;
        Assert.Equal("ABC日本語123مرحبا", string.Concat(runs.Select(r => r.Text)));
        Assert.Contains(runs, r => r.Text == "ABC" && r.FontFamily == "Cambria");
        Assert.Contains(runs, r => r.Text == "日本語" && r.FontFamily == "Yu Mincho");
        Assert.Contains(runs, r => r.Text == "مرحبا" && r.FontFamily == "Traditional Arabic");
        Assert.All(runs, r => Assert.True(r.Bold));
    }

    [Fact]
    public void Theme_supplemental_Japanese_typeface_is_resolved_when_the_ea_entry_is_empty()
    {
        var shape = Shape("日本語", "<a:latin typeface='+mn-lt'/><a:ea typeface='+mn-ea'/>");
        var theme = new XmlDocument { XmlResolver = null };
        theme.LoadXml("""
            <a:theme xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><a:themeElements><a:fontScheme>
            <a:minorFont><a:latin typeface='Cambria'/><a:ea typeface=''/><a:cs typeface=''/><a:font script='Jpan' typeface='Yu Gothic'/></a:minorFont>
            </a:fontScheme></a:themeElements></a:theme>
            """);
        var run = Assert.Single(Assert.Single(DrawingMlStyleReader.Read(shape, theme).Paragraphs).Runs);
        Assert.Equal("Yu Gothic", run.FontFamily);
    }

    [Fact]
    public void Supplemental_Unicode_characters_and_bounded_mixed_script_fallback_keep_all_source_text()
    {
        foreach (var text in new[] { "A𠀀🙂B", string.Concat(Enumerable.Repeat("A日", 200)) })
        {
            var result = DrawingMlStyleReader.Read(Shape(text));
            var runs = Assert.Single(result.Paragraphs).Runs;
            Assert.Equal(text, string.Concat(runs.Select(r => r.Text)));
            Assert.InRange(runs.Count, 1, 128);
            if (text.Length > 128) Assert.NotEmpty(result.Warnings);
        }
    }

    [Fact]
    public void Word_theme_font_and_East_Asian_language_reach_the_shared_drawing_reader()
    {
        var xml = new XmlDocument { XmlResolver = null };
        xml.LoadXml("""
            <wps:wsp xmlns:wps='http://schemas.microsoft.com/office/word/2010/wordprocessingShape'
              xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'>
            <wps:cNvPr id='1'/><wps:spPr><a:prstGeom prst='rect'/></wps:spPr><wps:txbx><w:txbxContent><w:p><w:r><w:rPr>
            <w:rFonts w:asciiTheme='minorHAnsi' w:eastAsiaTheme='minorEastAsia'/><w:lang w:val='en-US' w:eastAsia='ja-JP'/>
            </w:rPr><w:t>ABC日本語</w:t></w:r></w:p></w:txbxContent></wps:txbx><wps:bodyPr/></wps:wsp>
            """);
        var theme = new XmlDocument { XmlResolver = null };
        theme.LoadXml("""
            <a:theme xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><a:themeElements><a:fontScheme><a:minorFont>
            <a:latin typeface='Cambria'/><a:ea typeface=''/><a:font script='Jpan' typeface='Yu Gothic'/>
            </a:minorFont></a:fontScheme></a:themeElements></a:theme>
            """);
        var shape = Assert.Single(DrawingMlSceneReader.Read(xml.DocumentElement!, "docx", "/word/document.xml", theme,
            fallbackWidth: 100, fallbackHeight: 50));
        var runs = Assert.Single(shape.Paragraphs).Runs;
        Assert.Equal("ABC日本語", string.Concat(runs.Select(r => r.Text)));
        Assert.Contains(runs, r => r.Text == "ABC" && r.FontFamily == "Cambria");
        Assert.Contains(runs, r => r.Text == "日本語" && r.FontFamily == "Yu Gothic");
    }

    [Theory]
    [InlineData("vert='vert270'")]
    [InlineData("rot='5400000'")]
    [InlineData("upright='1'")]
    public void Body_text_layout_is_retained_without_losing_the_caption(string setting)
    {
        var shape = Shape("LAYOUT_REQUIRES_REVIEW");
        var body = shape.GetElementsByTagName("bodyPr", "http://schemas.openxmlformats.org/drawingml/2006/main")[0]!;
        var attribute = setting.Split('=');
        ((XmlElement)body).SetAttribute(attribute[0], attribute[1].Trim('\''));
        var result = DrawingMlStyleReader.Read(shape);
        Assert.Equal("LAYOUT_REQUIRES_REVIEW", string.Concat(result.Paragraphs.SelectMany(p => p.Runs).Select(r => r.Text)));
        Assert.NotNull(result.TextLayout);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("text body", StringComparison.Ordinal));
        if(attribute[0]=="vert")Assert.Equal("vert270",result.TextLayout.Direction);
        if(attribute[0]=="rot")Assert.Equal(90,result.TextLayout.RotationDegrees);
        if(attribute[0]=="upright")Assert.True(result.TextLayout.Upright);
    }

    private static XmlElement Shape(string text, string? fonts = null)
    {
        var xml = new XmlDocument { XmlResolver = null };
        xml.LoadXml("<a:sp xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><a:spPr/>" +
            "<a:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang='ja-JP' b='1'>" +
            (fonts ?? "<a:latin typeface='Cambria'/><a:ea typeface='Yu Mincho'/><a:cs typeface='Traditional Arabic'/>") +
            "</a:rPr><a:t>" + text + "</a:t></a:r></a:p></a:txBody></a:sp>");
        return xml.DocumentElement!;
    }
}
