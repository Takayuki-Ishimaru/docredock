using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Formats.OpenXml.Xlsx;

namespace DocRedock.Tests.Xlsx;

public sealed class WorkbookPresentationTests
{
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [Fact]
    public void Shared_formula_without_a_cache_and_styled_empty_input_are_retained()
    {
        var graph = Extract("""
            <sheetData><row r="1"><c r="A1"><f t="shared" si="0" ref="A1:A2">ROW()</f><v>1</v></c></row>
            <row r="2"><c r="A2"><f t="shared" si="0"/></c><c r="B2" s="1"/></row>
            <row r="3"><c r="C3" t="str"><f>IF(TRUE,"","")</f><v/></c></row></sheetData>
            """).Graph;
        var shared = Assert.Single(graph.Nodes, n => n.Extensions?["address"].GetString() == "A2");
        Assert.True(shared.Extensions!["is_formula"].GetBoolean());
        Assert.False(shared.Extensions["has_cached_value"].GetBoolean());
        var input = Assert.Single(graph.Nodes, n => n.Extensions?["address"].GetString() == "B2");
        Assert.NotNull(input.StyleId);
        Assert.True(graph.Styles!.ContainsKey(input.StyleId!));
        var emptyResult = Assert.Single(graph.Nodes, n => n.Extensions?["address"].GetString() == "C3");
        Assert.True(emptyResult.Extensions!["has_cached_value"].GetBoolean());
    }

    [Fact]
    public void Workbook_graph_carries_sparse_dimensions_merges_freeze_and_print_settings()
    {
        var result = Extract("""
            <sheetViews><sheetView workbookViewId="0"><pane xSplit="2" ySplit="3" topLeftCell="C4" state="frozen"/></sheetView></sheetViews>
            <sheetFormatPr defaultColWidth="9.5" defaultRowHeight="17"/>
            <cols><col min="1" max="1" width="5"/><col min="2" max="2" width="32" hidden="1"/></cols>
            <sheetData><row r="1" ht="22"><c r="A1" t="inlineStr"><is><t>Header</t></is></c></row></sheetData>
            <mergeCells count="2"><mergeCell ref="A1:C1"/><mergeCell ref="E4:F6"/></mergeCells>
            <autoFilter ref="A1:C8"/>
            <printOptions horizontalCentered="1"/>
            <pageMargins left="0.25" right="0.3" top="0.4" bottom="0.5" header="0.1" footer="0.2"/>
            <pageSetup paperSize="9" orientation="landscape" fitToWidth="1" fitToHeight="0"/>
            <rowBreaks count="1" manualBreakCount="1"><brk id="6" min="0" max="16383" man="1"/></rowBreaks>
            """, """
            <definedNames><definedName name="_xlnm.Print_Area" localSheetId="0">'Sheet, O''Brien'!$A$1:$C$8,'Sheet, O''Brien'!$E$4:$F$6</definedName>
            <definedName name="_xlnm.Print_Titles" localSheetId="0">'Sheet, O''Brien'!$1:$3,'Sheet, O''Brien'!$A:$B</definedName></definedNames>
            """);
        var metadata = result.Graph.FormatExtensions!["xlsx_layout"];
        Assert.Equal(1, metadata.GetProperty("version").GetInt32());
        var sheet = metadata.GetProperty("sheets").GetProperty("sheet-Sheet, O'Brien");
        Assert.Equal(9.5, sheet.GetProperty("defaultColumnWidth").GetDouble());
        Assert.Equal(32, sheet.GetProperty("columnWidths").GetProperty("2").GetDouble());
        Assert.Equal(22, sheet.GetProperty("rowHeights").GetProperty("1").GetDouble());
        Assert.Equal(["A1:C1", "E4:F6"], sheet.GetProperty("mergedRanges").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(2, sheet.GetProperty("frozenColumns").GetInt32());
        Assert.Equal(3, sheet.GetProperty("frozenRows").GetInt32());
        Assert.Equal("C4", sheet.GetProperty("topLeftCell").GetString());
        Assert.Equal("A1:C8", sheet.GetProperty("autoFilterRange").GetString());
        var print = sheet.GetProperty("print");
        Assert.Equal(9, print.GetProperty("paperSize").GetInt32());
        Assert.Equal("landscape", print.GetProperty("orientation").GetString());
        Assert.Equal(0.25, print.GetProperty("margins").GetProperty("left").GetDouble());
        Assert.Equal(["$A$1:$C$8", "$E$4:$F$6"], print.GetProperty("areas").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal("$1:$3", print.GetProperty("titleRows").GetString());
        Assert.Equal("$A:$B", print.GetProperty("titleColumns").GetString());
        Assert.Equal(6, Assert.Single(print.GetProperty("rowBreaks").EnumerateArray()).GetInt32());
    }

    [Fact]
    public void Colors_and_border_styles_are_resolved_once_and_not_reduced_to_flags()
    {
        var graph = Extract("""<sheetData><row r="1"><c r="A1" s="1" t="inlineStr"><is><t>Input</t></is></c></row></sheetData>""").Graph;
        var cell = Assert.Single(graph.Nodes, n => n.Extensions?["address"].GetString() == "A1");
        var props = graph.Styles![cell.StyleId!].Properties!;
        Assert.Equal("#FF0000", props["color"]);
        Assert.Equal("#FFFF00", props["background-color"]);
        Assert.Equal("right", props["text-align"]);
        Assert.Equal("middle", props["vertical-align"]);
        Assert.Contains("double", props["border-bottom"], StringComparison.Ordinal);
        Assert.Equal("pre-wrap", props["white-space"]);
    }

    [Fact]
    public void Huge_declared_range_is_not_materialized()
    {
        var graph = Extract("""<dimension ref="A1:XFD1048576"/><sheetData><row r="1"><c r="A1"><v>1</v></c></row></sheetData><mergeCells><mergeCell ref="B2:XFD1048576"/></mergeCells>""").Graph;
        Assert.InRange(graph.Nodes.Count(), 1, 4);
        Assert.True(graph.FormatExtensions!.ContainsKey("xlsx_layout"));
    }

    [Theory]
    [InlineData(0, "#000000")]
    [InlineData(1, "#FFFFFF")]
    [InlineData(24, "#9999FF")]
    [InlineData(26, "#FFFFCC")]
    [InlineData(31, "#CCCCFF")]
    [InlineData(56, "#003366")]
    [InlineData(63, "#333333")]
    [InlineData(64, null)]
    [InlineData(65, null)]
    public void Indexed_colors_use_the_complete_palette_and_leave_system_colors_unresolved(int index, string? expected)
    {
        var graph = Styled($"<color indexed=\"{index}\"/>").Graph;
        var props = graph.Styles!["xlsx-style-0"].Properties!;
        if (expected is null) Assert.False(props.ContainsKey("color"));
        else Assert.Equal(expected, props["color"]);
    }

    [Fact]
    public void Custom_palette_and_theme_relationship_use_the_source_colors()
    {
        var palette = Styled("<color indexed=\"1\"/>", "<colors><indexedColors><rgbColor rgb=\"FF112233\"/><rgbColor rgb=\"FFABCDEF\"/></indexedColors></colors>").Graph;
        Assert.Equal("#ABCDEF", palette.Styles!["xlsx-style-0"].Properties!["color"]);
        const string theme = """<a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:themeElements><a:clrScheme name="custom"><a:dk1><a:srgbClr val="000000"/></a:dk1><a:lt1><a:srgbClr val="FFFFFF"/></a:lt1><a:accent1><a:srgbClr val="123456"/></a:accent1></a:clrScheme></a:themeElements></a:theme>""";
        var graph = Styled("<b val=\"0\"/><i val=\"0\"/><color theme=\"4\" tint=\"0.5\"/>", theme: theme).Graph;
        var props = graph.Styles!["xlsx-style-0"].Properties!;
        Assert.False(props.ContainsKey("font-weight")); Assert.False(props.ContainsKey("font-style"));
        var rgb = props["color"];
        Assert.InRange(Convert.ToInt32(rgb[1..3], 16), 86, 88);
        Assert.InRange(Convert.ToInt32(rgb[3..5], 16), 152, 154);
        Assert.InRange(Convert.ToInt32(rgb[5..7], 16), 219, 221);
    }

    [Fact]
    public void Cells_without_a_style_attribute_use_style_zero_including_filled_empty_cells()
    {
        var result = Extract("""
            <sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Default red</t></is></c><c r="B1"/></row></sheetData>
            """, styles: $"""<styleSheet xmlns="{Ns}"><fonts><font><color rgb="FFFF0000"/></font></fonts><fills><fill><patternFill patternType="solid"><fgColor rgb="FFFFFF00"/></patternFill></fill></fills><cellXfs><xf fontId="0" fillId="0"/></cellXfs></styleSheet>""");
        var text = Assert.Single(result.Graph.Nodes, node => node.Extensions?["address"].GetString() == "A1");
        var empty = Assert.Single(result.Graph.Nodes, node => node.Extensions?["address"].GetString() == "B1");
        Assert.Equal("xlsx-style-0", text.StyleId);
        Assert.Equal("xlsx-style-0", empty.StyleId);
        var properties = result.Graph.Styles![text.StyleId!].Properties!;
        Assert.Equal("#FF0000", properties["color"]);
        Assert.Equal("#FFFF00", properties["background-color"]);
    }

    [Fact]
    public void Apply_alignment_false_uses_the_base_style_alignment()
    {
        var graph = Extract("""<sheetData><row r="1"><c r="A1" s="0"><v>1</v></c></row></sheetData>""",
            styles: $"""<styleSheet xmlns="{Ns}"><fonts><font/></fonts><cellStyleXfs><xf fontId="0"><alignment horizontal="right" vertical="center" wrapText="1"/></xf></cellStyleXfs><cellXfs><xf xfId="0" applyAlignment="0"><alignment horizontal="left" vertical="top" wrapText="0"/></xf></cellXfs></styleSheet>""").Graph;
        var properties = graph.Styles!["xlsx-style-0"].Properties!;
        Assert.Equal("right", properties["text-align"]);
        Assert.Equal("middle", properties["vertical-align"]);
        Assert.Equal("pre-wrap", properties["white-space"]);
    }

    [Fact]
    public void Conditional_strike_with_a_fill_reports_the_unevaluated_fill_and_uses_effective_marks()
    {
        var result = Extract("""
            <sheetData><row r="1"><c r="A1" s="0"><v>1</v></c></row></sheetData>
            <conditionalFormatting sqref="A1"><cfRule type="expression" dxfId="0" priority="1"><formula>1=1</formula></cfRule></conditionalFormatting>
            """, styles: $"""<styleSheet xmlns="{Ns}"><fonts><font><strike/></font></fonts><cellXfs><xf fontId="0"/></cellXfs><dxfs><dxf><font><strike val="0"/></font><fill><patternFill patternType="solid"><fgColor rgb="FFFF0000"/></patternFill></fill></dxf></dxfs></styleSheet>""");
        var sheet = result.Graph.FormatExtensions!["xlsx_layout"].GetProperty("sheets").GetProperty("sheet-Sheet, O'Brien");
        Assert.Equal(1, sheet.GetProperty("conditionalFormatUnsupportedCount").GetInt32());
        var cell = Assert.Single(result.Graph.Nodes, node => node.Extensions?["address"].GetString() == "A1");
        Assert.False(cell.Extensions!.ContainsKey("strike"));
        Assert.False(result.Graph.Styles![cell.StyleId!].Properties!.ContainsKey("text-decoration"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("n", false)]
    [InlineData("str", true)]
    public void Empty_formula_cache_is_saved_only_for_a_string_result(string? cellType, bool saved)
    {
        var type = cellType is null ? string.Empty : $" t=\"{cellType}\"";
        var result = Extract($"""<sheetData><row r="1"><c r="A1"{type}><f>IF(TRUE,"","")</f><v/></c></row></sheetData>""");
        var cell = Assert.Single(result.Graph.Nodes, node => node.Extensions?["address"].GetString() == "A1");
        Assert.Equal(saved, cell.Extensions!["has_cached_value"].GetBoolean());
        Assert.Equal(!saved, result.Warnings.Any(warning => warning.StartsWith("XlsxFormulaCachedValueMissing:", StringComparison.Ordinal)));
    }
    private static XlsxExtractionResult Styled(string font, string extras = "", string? theme = null) => Extract(
        """<sheetData><row r="1"><c r="A1" s="00"><v>1</v></c></row></sheetData>""",
        styles: $"<styleSheet xmlns=\"{Ns}\"><fonts><font>{font}</font></fonts><cellXfs><xf fontId=\"0\"/></cellXfs>{extras}</styleSheet>", theme: theme);

    internal static XlsxExtractionResult Extract(string sheet, string names = "", string? styles = null, string? theme = null)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            void Add(string path, string content) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(content); }
            Add("[Content_Types].xml", """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/></Types>""");
            Add("xl/workbook.xml", $"<workbook xmlns=\"{Ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet, O'Brien\" sheetId=\"1\" r:id=\"r1\"/></sheets>{names}</workbook>");
            Add("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>""" +
                (theme is null ? "" : """<Relationship Id="theme" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme" Target="custom.xml"/>""") + "</Relationships>");
            if (theme is not null) Add("xl/custom.xml", theme);
            Add("xl/styles.xml", styles ?? $"""<styleSheet xmlns="{Ns}"><fonts count="2"><font/><font><b/><color rgb="FFFF0000"/><sz val="12"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFFFFF00"/></patternFill></fill></fills><borders count="2"><border/><border><bottom style="double"><color rgb="FF000000"/></bottom></border></borders><cellXfs count="2"><xf fontId="0" fillId="0" borderId="0"/><xf fontId="1" fillId="2" borderId="1"><alignment horizontal="right" vertical="center" wrapText="1"/></xf></cellXfs></styleSheet>""");
            Add("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{Ns}\">{sheet}</worksheet>");
        }
        bytes.Position = 0;
        return new XlsxAdapter().Extract(bytes);
    }
}
