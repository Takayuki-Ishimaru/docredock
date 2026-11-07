using System.IO.Compression;
using System.Text;
using DocRedock.Api;
using DocRedock.Core.Reporting;
using DocRedock.Formats.OpenXml.Xlsx;

namespace DocRedock.Tests.Xlsx;

/// <summary>Restoring edited cells warns of a recalculation only where a formula may read an edited cell (the
/// 2026-11-07 evaluation: every edit of a workbook ended with exit 1, an edit of a remark as well as of a price that a
/// total uses). DocRedock evaluates no formula; a formula it cannot trace counts as reading every cell.</summary>
public sealed class XlsxRecalculationTests
{
    private const int Rows = XlsxFormulaReferences.MaxRow, Columns = XlsxFormulaReferences.MaxColumn;

    public static TheoryData<string, (string? Sheet, int FirstColumn, int FirstRow, int LastColumn, int LastRow)[]> Traceable => new()
    {
        { "SUM(B6:C6)", [(null, 2, 6, 3, 6)] },
        { "Sheet2!A1+'My Sheet'!$B$2*2", [("Sheet2", 1, 1, 1, 1), ("My Sheet", 2, 2, 2, 2)] },
        { "'It''s'!C3", [("It's", 3, 3, 3, 3)] },
        { "売上!B2*1.08", [("売上", 2, 2, 2, 2)] },
        { "SUM(A:A)", [(null, 1, 1, 1, Rows)] },
        { "SUM($C:$D)", [(null, 3, 1, 4, Rows)] },
        { "SUM(3:5)", [(null, 1, 3, Columns, 5)] },
        { "\"A1 and B1\"&C1", [(null, 3, 1, 3, 1)] },
        { "LOG10(C3)+_xlfn.XLOOKUP(D1,E:E,F:F)", [(null, 3, 3, 3, 3), (null, 4, 1, 4, 1), (null, 5, 1, 5, Rows), (null, 6, 1, 6, Rows)] },
        { "1E+10*A1+.5+50%", [(null, 1, 1, 1, 1)] },
        { "IF(TRUE,#N/A,A2)+#REF!", [(null, 1, 2, 1, 2)] },
        { "Sheet1!#REF!*2", [] },
        { "NOW()-TODAY()", [] },
        { "SUM({1,2;3,4})", [] },
    };

    [Theory]
    [MemberData(nameof(Traceable))]
    public void A_formula_reads_the_cells_its_references_cover(string formula, (string? Sheet, int FirstColumn, int FirstRow, int LastColumn, int LastRow)[] expected)
    {
        var areas = new List<XlsxFormulaReferences.Area>();

        Assert.True(XlsxFormulaReferences.TryRead(formula, 0, 0, areas));
        Assert.Equal(expected, areas.Select(area => (area.Sheet, area.FirstColumn, area.FirstRow, area.LastColumn, area.LastRow)));
    }

    [Theory]
    [InlineData("TaxRate*A1")]
    [InlineData("INDIRECT(\"A\"&B1)")]
    [InlineData("_xlfn.OFFSET(A1,2,0)")]
    [InlineData("Table1[Price]*2")]
    [InlineData("SUM(Table1[@Price])")]
    [InlineData("Sheet1:Sheet3!A1")]
    [InlineData("'Sheet1:Sheet3'!A1")]
    [InlineData("SUM(A1#)")]
    [InlineData("LET(x,A1,x*2)")]
    public void A_formula_whose_reach_its_text_does_not_settle_is_untraceable(string formula) =>
        Assert.False(XlsxFormulaReferences.TryRead(formula, 0, 0, []));

    [Fact]
    public void A_shared_formula_covers_where_its_relative_references_move_and_keeps_its_fixed_ones()
    {
        var areas = new List<XlsxFormulaReferences.Area>();

        // Shared over D6:E9 (one more column, three more rows): B6 reads B6:C9 and C6 reads C6:D9; $B$1 stays; A$2
        // moves across only.
        Assert.True(XlsxFormulaReferences.TryRead("B6+C6*$B$1+A$2", 1, 3, areas));

        Assert.Equal([(2, 6, 3, 9), (3, 6, 4, 9), (2, 1, 2, 1), (1, 2, 2, 2)],
            areas.Select(area => (area.FirstColumn, area.FirstRow, area.LastColumn, area.LastRow)));
    }

    [Theory]
    [InlineData("A1", 1, 1)]
    [InlineData("$AB$12", 28, 12)]
    [InlineData("XFD1048576", Columns, Rows)]
    public void A_cell_reference_parses_to_its_column_and_row(string reference, int column, int row)
    {
        Assert.True(XlsxFormulaReferences.TryParseCell(reference, out var actualColumn, out var actualRow));
        Assert.Equal((column, row), (actualColumn, actualRow));
    }

    [Theory]
    [InlineData("XFE1")]
    [InlineData("A0")]
    [InlineData("A1048577")]
    [InlineData("A1B")]
    public void Out_of_range_or_malformed_references_do_not_parse(string reference) =>
        Assert.False(XlsxFormulaReferences.TryParseCell(reference, out _, out _));

    // Data!B6:C6 are hours, D6 their sum, F6 a remark; Summary!B1 adds up Data!D6.
    private const string DataSheet = """
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
        <row r="6"><c r="B6"><v>8</v></c><c r="C6"><v>4</v></c><c r="D6"><f>SUM(B6:C6)</f><v>12</v></c><c r="F6" t="inlineStr"><is><t>ok</t></is></c></row>
        </sheetData></worksheet>
        """;

    private static IReadOnlyList<string> RestoreWarnings(string data, string? summary, params XlsxCellEdit[] edits) =>
        RestoreWarnings(Workbook(data, summary), edits);

    private static IReadOnlyList<string> RestoreWarnings(byte[] workbook, params XlsxCellEdit[] edits)
    {
        var adapter = new XlsxAdapter();
        return adapter.Restore(new MemoryStream(workbook), adapter.CreatePatchPlan(edits)).Warnings;
    }

    [Fact]
    public void An_edit_that_no_formula_reads_restores_without_a_warning()
    {
        Assert.Empty(RestoreWarnings(DataSheet, null, new XlsxCellEdit("Data", "F6", "checked")));
        Assert.Empty(RestoreWarnings("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1"><v>1</v></c></row></sheetData></worksheet>""",
            null, new XlsxCellEdit("Data", "A1", "2")));
    }

    [Fact]
    public void An_edit_that_a_formula_reads_names_the_formula_cell_on_its_own_and_on_other_sheets()
    {
        const string summary = """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="B1"><f>Data!B6*2</f><v>16</v></c><c r="C1"><f>Data!F6</f><v>0</v></c></row></sheetData></worksheet>""";

        var warning = Assert.Single(RestoreWarnings(DataSheet, summary, new XlsxCellEdit("Data", "B6", "10")));

        Assert.StartsWith("XlsxRecalculationRequired: 2 formula cell(s) may read the edited cells (Data!D6, Summary!B1)", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Summary!C1", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void An_untraceable_formula_counts_as_reading_the_edit()
    {
        const string summary = """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1"><f>Hours*Rate</f><v>0</v></c></row></sheetData></worksheet>""";

        var warning = Assert.Single(RestoreWarnings(DataSheet, summary, new XlsxCellEdit("Data", "F6", "checked")));

        Assert.Contains("(Summary!A1)", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shared_formula_is_named_by_the_range_it_is_shared_over()
    {
        const string data = """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
            <row r="6"><c r="B6"><v>8</v></c><c r="D6"><f t="shared" ref="D6:D9" si="0">B6*2</f><v>16</v></c></row>
            <row r="9"><c r="B9"><v>5</v></c><c r="D9"><f t="shared" si="0"/><v>10</v></c></row>
            </sheetData></worksheet>
            """;

        Assert.Contains("(Data!D6:D9)", Assert.Single(RestoreWarnings(data, null, new XlsxCellEdit("Data", "B9", "7"))), StringComparison.Ordinal);
        Assert.Empty(RestoreWarnings(data, null, new XlsxCellEdit("Data", "B10", "7")));
    }

    [Fact]
    public void An_edited_formula_is_named_as_having_no_saved_result_and_resets_the_calculation_chain()
    {
        var workbook = Workbook(DataSheet, null, calcChain: true);

        var warnings = RestoreWarnings(workbook, new XlsxCellEdit("Data", "E6", Formula: "D6/2"));

        Assert.Contains(warnings, warning => warning.StartsWith("XlsxRecalculationRequired: 1 edited formula(s) (Data!E6) have no saved result", StringComparison.Ordinal));
        var reset = Assert.Single(warnings, warning => warning.StartsWith("XlsxCalculationChainReset:", StringComparison.Ordinal));
        var diagnostic = AdapterWarningDiagnostics.Create("XlsxWarning", reset);
        Assert.Equal(("XlsxCalculationChainReset", DiagnosticSeverity.Information), (diagnostic.Code, diagnostic.Severity));
        Assert.Equal(DiagnosticSeverity.Warning, AdapterWarningDiagnostics.Create("XlsxWarning", warnings.First(warning => warning.StartsWith("XlsxRecalculationRequired:", StringComparison.Ordinal))).Severity);
    }

    [Fact]
    public void The_calculation_setting_is_written_where_the_schema_puts_it_before_an_extension_list()
    {
        var workbook = Workbook(DataSheet, null, workbookTail: "<extLst><ext uri=\"{140A7094-0E35-4892-8432-C4D2E57EDEB5}\"/></extLst>");
        var adapter = new XlsxAdapter();

        var restored = adapter.Restore(new MemoryStream(workbook), adapter.CreatePatchPlan([new XlsxCellEdit("Data", "F6", "checked")])).Bytes;

        using var zip = new ZipArchive(new MemoryStream(restored));
        using var reader = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open());
        var xml = reader.ReadToEnd();
        Assert.True(xml.IndexOf("<calcPr", StringComparison.Ordinal) is var calc and > 0 && calc < xml.IndexOf("<extLst", StringComparison.Ordinal), xml);
        Assert.Contains("fullCalcOnLoad=\"1\"", xml, StringComparison.Ordinal);
    }

    private static byte[] Workbook(string data, string? summary, bool calcChain = false, string workbookTail = "")
    {
        var sheets = "<sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/>" + (summary is null ? "" : "<sheet name=\"Summary\" sheetId=\"2\" r:id=\"rId2\"/>");
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
            ["xl/workbook.xml"] = $"<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>{sheets}</sheets>{workbookTail}</workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"worksheet\" Target=\"worksheets/sheet2.xml\"/></Relationships>",
            ["xl/worksheets/sheet1.xml"] = data,
        };
        if (summary is not null) parts["xl/worksheets/sheet2.xml"] = summary;
        if (calcChain) parts["xl/calcChain.xml"] = "<calcChain xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><c r=\"D6\" i=\"1\"/></calcChain>";
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var (name, content) in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        return output.ToArray();
    }
}
