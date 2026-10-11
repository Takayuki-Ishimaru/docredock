using System.IO.Compression;
using System.Security;
using System.Text;
using DocRedock.Formats.OpenXml.Xlsx;

namespace DocRedock.Tests.Xlsx;

public sealed partial class XlsxAdapterTests
{
    private const string ConditionalValues = "<row r=\"1\"><c r=\"A1\"><f>1000</f><v>3</v></c><c r=\"B1\" t=\"inlineStr\"><is><t>done</t></is></c></row>" +
        "<row r=\"2\"><c r=\"A2\"><v>5</v></c><c r=\"B2\" t=\"inlineStr\"><is><t>pending</t></is></c></row>" +
        "<row r=\"3\"><c r=\"A3\"><v>7</v></c><c r=\"B3\" t=\"inlineStr\"><is><t>done</t></is></c></row>";

    [Theory]
    [InlineData("AND(A1>2,OR(B1=\"done\",FALSE()),NOT(A1=0))")]
    [InlineData("IF(A1>2,TRUE(),UNSUPPORTED())")]
    [InlineData("IFERROR(1/0,3)=A1")]
    [InlineData("SUM($A$1:$A$3)=15")]
    [InlineData("AVERAGE($A$1:$A$3)=5")]
    [InlineData("MIN($A$1:$A$3)=3")]
    [InlineData("MAX($A$1:$A$3)=7")]
    [InlineData("COUNT($A$1:$A$3)=3")]
    [InlineData("COUNTIF($B$1:$B$3,\"d*\")=2")]
    [InlineData("COUNTIFS($A$1:$A$3,\">3\",$B$1:$B$3,\"done\")=1")]
    [InlineData("SUMIF($B$1:$B$3,\"done\",$A$1:$A$3)=10")]
    [InlineData("SUMIFS($A$1:$A$3,$B$1:$B$3,\"done\",$A$1:$A$3,\">3\")=7")]
    [InlineData("ISNUMBER(SEARCH(\"d*e\",B1))")]
    [InlineData("ISERROR(FIND(\"D\",B1))")]
    [InlineData("LEN(TRIM(\"  hello   world \"))=11")]
    [InlineData("LOWER(LEFT(\"DONE\",4))=B1")]
    [InlineData("MID(B1,2,2)=\"on\"")]
    [InlineData("MOD(-3,2)=1")]
    [InlineData("ROUND(2.55,1)=2.6")]
    [InlineData("ROW()+COLUMN()=2")]
    [InlineData("-2^2=4")]
    [InlineData("2^3^2=64")]
    [InlineData("50%*4=2")]
    [InlineData("ISBLANK(Z1)")]
    [InlineData("IF(FALSE(),UNSUPPORTED(),TRUE())")]
    [InlineData("IF(FALSE(),1,2)=2")]
    [InlineData("IF(FALSE(),1)=FALSE()")]
    [InlineData("SUM(B1)=0")]
    [InlineData("COUNT(B1)=0")]
    [InlineData("COUNTA(\"\")=1")]
    [InlineData("COUNTBLANK(\"\")=1")]
    [InlineData("COUNTIF($B$1:$B$3,\">1\")=0")]
    [InlineData("SEARCH(\"o\",B1)=2")]
    [InlineData("ROUND(1234,-2)=1200")]
    [InlineData("ROUNDUP(12.34,1)=12.4")]
    [InlineData("ROUNDDOWN(12.34,1)=12.3")]
    [InlineData("ROUNDUP(1234,-2)=1300")]
    [InlineData("ROUNDDOWN(-1234,-2)=-1200")]
    [InlineData("TRUNC(-1.234,2)=-1.23")]
    [InlineData("3<\"a\"")]
    [InlineData("\"a\"<TRUE()")]
    [InlineData("Z1=FALSE()")]
    [InlineData("ROW(A2)=2")]
    [InlineData("COLUMN($B$2)=2")]
    [InlineData("-(1+2)^2=9")]
    [InlineData("DATE(2024,3,1)>DATE(2024,2,29)")]
    [InlineData("DATE(1900,2,29)=60")]
    [InlineData("DAY(60)=29")]
    [InlineData("YEAR(DATE(2024,2,29))=2024")]
    [InlineData("MONTH(DATE(2024,2,29))=2")]
    [InlineData("WEEKDAY(DATE(2024,2,29),2)=4")]
    [InlineData("NOW()>=TODAY()")]
    public void Complex_conditions_evaluate_cached_values_and_functions(string formula)
    {
        var bytes = ConditionalPackage(ConditionalValues, ConditionalRuleXml(SecurityElement.Escape(formula)!, range: "A1"));
        var result = new XlsxAdapter().Extract(new MemoryStream(bytes));
        Assert.True(result.Worksheets[0].Cells.Single(cell => cell.CellReference == "A1").DisplayStyle!.IsStrike);
        Assert.DoesNotContain(result.Warnings, warning => warning.StartsWith("XlsxConditionalStrikeNotEvaluated:"));
        Assert.Equal(bytes, new XlsxAdapter().Restore(new MemoryStream(bytes), new XlsxAdapter().CreatePatchPlan(result.Graph, result.Graph)).Bytes);
    }

    [Fact]
    public void Later_sheet_references_global_and_local_names_are_evaluated_without_sheet_order_dependence()
    {
        var parts = Entries(ConditionalPackage(ConditionalValues,
            ConditionalRuleXml("AND(A1&gt;LocalLimit,A1&gt;Threshold,'Lookup Data'!$A$1=2)", range: "A1")));
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        parts["xl/workbook.xml"] = Encoding.UTF8.GetBytes($"<workbook xmlns=\"{ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
            "<sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Lookup Data\" sheetId=\"2\" r:id=\"rId2\"/></sheets>" +
            "<definedNames><definedName name=\"Threshold\">'Lookup Data'!$A$1</definedName><definedName name=\"LocalLimit\">9</definedName>" +
            "<definedName name=\"LocalLimit\" localSheetId=\"0\">2</definedName></definedNames></workbook>");
        parts["xl/_rels/workbook.xml.rels"] = Encoding.UTF8.GetBytes("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Target=\"worksheets/sheet2.xml\"/></Relationships>");
        parts["xl/worksheets/sheet2.xml"] = Encoding.UTF8.GetBytes($"<worksheet xmlns=\"{ns}\"><sheetData><row r=\"1\"><c r=\"A1\"><v>2</v></c></row></sheetData></worksheet>");
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var part in parts) { using var entry = zip.CreateEntry(part.Key).Open(); entry.Write(part.Value); }
        var result = new XlsxAdapter().Extract(new MemoryStream(stream.ToArray()));
        Assert.True(result.Worksheets[0].Cells.Single(cell => cell.CellReference == "A1").DisplayStyle!.IsStrike);
        Assert.DoesNotContain(result.Warnings, warning => warning.StartsWith("XlsxConditionalStrikeNotEvaluated:"));
    }

    [Theory]
    [InlineData("AND(DATE(1904,1,1)=0,YEAR(0)=1904,MONTH(0)=1,DAY(0)=1,WEEKDAY(0)=6)", true)]
    [InlineData("LoopName>0", false)]
    public void Date_system_and_cyclic_names_follow_workbook_metadata(string formula, bool resolved)
    {
        var parts = Entries(ConditionalPackage(ConditionalValues, ConditionalRuleXml(SecurityElement.Escape(formula)!, range: "A1")));
        var workbook = Encoding.UTF8.GetString(parts["xl/workbook.xml"]);
        workbook = workbook.Replace("<sheets>", "<workbookPr date1904=\"1\"/><sheets>", StringComparison.Ordinal)
            .Replace("</workbook>", "<definedNames><definedName name=\"LoopName\">LoopName</definedName></definedNames></workbook>", StringComparison.Ordinal);
        parts["xl/workbook.xml"] = Encoding.UTF8.GetBytes(workbook);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var part in parts) { using var entry = zip.CreateEntry(part.Key).Open(); entry.Write(part.Value); }
        var result = new XlsxAdapter().Extract(new MemoryStream(stream.ToArray()));
        Assert.Equal(resolved, result.Worksheets[0].Cells.Single(cell => cell.CellReference == "A1").DisplayStyle!.IsStrike);
        Assert.Equal(!resolved, result.Warnings.Any(warning => warning.StartsWith("XlsxConditionalStrikeNotEvaluated:")));
    }

    [Theory]
    [InlineData("IFERROR(UNSUPPORTED(),TRUE())")]
    [InlineData("SUM(A1:A10000)>0")]
    [InlineData("'[Other.xlsx]Sheet1'!A1=3")]
    [InlineData("INDIRECT(\"A1\")=3")]
    [InlineData("ISNUMBER(UNSUPPORTED())")]
    [InlineData("IFERROR(1@invalid,TRUE())")]
    [InlineData("COUNTIFS(A1:A2,\">0\",B1:B3,\"done\")=2")]
    public void Unsupported_or_unbounded_dependencies_cannot_clear_ordinary_strike(string formula)
    {
        var bytes = ConditionalPackage(ConditionalValues, ConditionalRuleXml(SecurityElement.Escape(formula)!,
            attributes: "dxfId=\"1\" priority=\"1\"", range: "A1"), true);
        var result = new XlsxAdapter().Extract(new MemoryStream(bytes));
        Assert.True(result.Worksheets[0].Cells[0].DisplayStyle!.IsStrike);
        Assert.Single(result.Warnings, warning => warning.StartsWith("XlsxConditionalStrikeNotEvaluated:"));
    }

    [Theory]
    [InlineData("d*e", true)]
    [InlineData("d?ne", true)]
    [InlineData("d~*e", false)]
    public void Conditional_text_rules_handle_excel_wildcards_and_escapes(string text, bool expected)
    {
        var bytes = ConditionalPackage(ConditionalValues, ConditionalRuleXml("", "containsText",
            $"dxfId=\"0\" priority=\"1\" text=\"{text}\"", range: "B1"));
        var result = new XlsxAdapter().Extract(new MemoryStream(bytes));
        Assert.Equal(expected, result.Worksheets[0].Cells.Single(cell => cell.CellReference == "B1").DisplayStyle!.IsStrike);
        Assert.DoesNotContain(result.Warnings, warning => warning.StartsWith("XlsxConditionalStrikeNotEvaluated:"));
    }
}
