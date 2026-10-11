using System.IO.Compression;
using System.Text;
using DocRedock.Api;
using DocRedock.Formats.OpenXml.Xlsx;
using DocRedock.Markdown;

namespace DocRedock.Tests.Xlsx;

public sealed partial class XlsxAdapterTests
{
    private static byte[] ConditionalPackage(string cells, string rules, bool ordinaryStrike = false)
    {
        var parts = Entries(CreatePackage());
        parts["xl/styles.xml"] = Encoding.UTF8.GetBytes("<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            $"<fonts><font>{(ordinaryStrike ? "<strike/>" : "")}</font></fonts><cellXfs><xf/><xf fontId=\"0\"/></cellXfs>" +
            "<dxfs><dxf><font><strike/></font></dxf><dxf><font><strike val=\"0\"/></font></dxf><dxf><font><b/></font></dxf></dxfs></styleSheet>");
        parts["xl/worksheets/sheet1.xml"] = Encoding.UTF8.GetBytes("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<sheetData>" + cells.Replace("<c r=", "<c s=\"1\" r=") + "</sheetData>" + rules + "</worksheet>");
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var part in parts) { using var entry = zip.CreateEntry(part.Key).Open(); entry.Write(part.Value); }
        return stream.ToArray();
    }

    private static string ConditionalRuleXml(string formula, string type = "expression", string attributes = "dxfId=\"0\" priority=\"1\"", string range = "A1:C2") =>
        $"<conditionalFormatting sqref=\"{range}\"><cfRule type=\"{type}\" {attributes}><formula>{formula}</formula></cfRule></conditionalFormatting>";

    [Theory]
    [InlineData("equal", "3", true)]
    [InlineData("notEqual", "3", false)]
    [InlineData("lessThan", "4", true)]
    [InlineData("lessThanOrEqual", "3", true)]
    [InlineData("greaterThan", "4", false)]
    [InlineData("greaterThanOrEqual", "3", true)]
    [InlineData("between", "2</formula><formula>4", true)]
    [InlineData("notBetween", "2</formula><formula>4", false)]
    public void Numeric_cell_conditions_are_evaluated(string op, string formula, bool expected)
    {
        var bytes = ConditionalPackage("<row r=\"1\"><c r=\"A1\"><v>3</v></c></row>",
            ConditionalRuleXml(formula, "cellIs", $"dxfId=\"0\" priority=\"1\" operator=\"{op}\""));
        var result = new XlsxAdapter().Extract(new MemoryStream(bytes));
        var cell = Assert.Single(Assert.Single(result.Worksheets).Cells);
        Assert.Equal(expected, cell.DisplayStyle?.IsStrike == true);
        Assert.Equal(expected, cell.StruckRanges is { Count: > 0 });
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Relative_absolute_references_and_disjoint_ranges_use_the_first_range_origin()
    {
        var bytes = ConditionalPackage("<row r=\"1\"><c r=\"A1\"><v>1</v></c><c r=\"B1\"><v>4</v></c><c r=\"C1\"><v>1</v></c></row>" +
            "<row r=\"2\"><c r=\"A2\"><v>4</v></c><c r=\"B2\"><v>1</v></c><c r=\"C2\"><v>4</v></c></row>",
            ConditionalRuleXml("A1&gt;$B$2", range: "A1:A2 C1:C2"));
        var result = new XlsxAdapter().Extract(new MemoryStream(bytes));
        Assert.Equal(["A2", "C2"], result.Worksheets[0].Cells.Where(cell => cell.DisplayStyle?.IsStrike == true).Select(cell => cell.CellReference));
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("dxfId=\"1\" priority=\"1\"", true, false)]
    [InlineData("dxfId=\"2\" priority=\"1\" stopIfTrue=\"1\"", false, false)]
    [InlineData("dxfId=\"2\" priority=\"1\"", false, true)]
    public void Priority_explicit_off_and_stop_if_true_preserve_the_effective_font_property(string first, bool ordinary, bool expected)
    {
        // Document order is reversed deliberately; priorities apply across formatting elements.
        var bytes = ConditionalPackage("<row r=\"1\"><c r=\"A1\"><v>3</v></c></row>",
            ConditionalRuleXml("TRUE()", attributes: "dxfId=\"0\" priority=\"2\"") + ConditionalRuleXml("TRUE()", attributes: first), ordinary);
        var result = new XlsxAdapter().Extract(new MemoryStream(bytes));
        var cell = Assert.Single(result.Worksheets[0].Cells);
        Assert.Equal(expected, cell.DisplayStyle?.IsStrike == true);
        Assert.Equal(expected, cell.StruckRanges is { Count: > 0 });
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("UNSUPPORTED(A1)&gt;0")]
    [InlineData("Other!A1=1")]
    [InlineData("&quot;")]
    [InlineData("==1")]
    public void Unknown_conditions_block_lower_rules_and_keep_ordinary_strike(string formula)
    {
        var bytes = ConditionalPackage("<row r=\"1\"><c r=\"A1\"><v>3</v></c></row>",
            ConditionalRuleXml(formula, attributes: "dxfId=\"1\" priority=\"1\"") +
            ConditionalRuleXml("TRUE()", attributes: "dxfId=\"1\" priority=\"2\""), true);
        var result = new XlsxAdapter().Extract(new MemoryStream(bytes));
        Assert.True(Assert.Single(result.Worksheets[0].Cells).DisplayStyle!.IsStrike);
        Assert.Single(result.Warnings, warning => warning.StartsWith("XlsxConditionalStrikeNotEvaluated:"));
    }

    [Theory]
    [InlineData("containsText", "alp", true)]
    [InlineData("notContainsText", "alp", false)]
    [InlineData("beginsWith", "Al", true)]
    [InlineData("endsWith", "HA", true)]
    public void Text_rules_use_case_insensitive_text(string type, string text, bool expected)
    {
        var bytes = ConditionalPackage("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Alpha</t></is></c></row>",
            ConditionalRuleXml("", type, $"dxfId=\"0\" priority=\"1\" text=\"{text}\""));
        var result = new XlsxAdapter().Extract(new MemoryStream(bytes));
        Assert.Equal(expected, result.Worksheets[0].Cells[0].DisplayStyle?.IsStrike == true);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Cached_formula_values_and_missing_blank_references_are_used_without_recalculating()
    {
        var bytes = ConditionalPackage("<row r=\"1\"><c r=\"A1\"><f>1+2</f><v>3</v></c><c r=\"B1\" t=\"b\"><v>1</v></c></row>",
            ConditionalRuleXml("A1&gt;$Z$1", range: "A1") + ConditionalRuleXml("B1=TRUE()", range: "B1"));
        var result = new XlsxAdapter().Extract(new MemoryStream(bytes));
        Assert.All(result.Worksheets[0].Cells, cell => Assert.True(cell.DisplayStyle!.IsStrike));
        Assert.DoesNotContain(result.Warnings, warning => warning.StartsWith("XlsxConditionalStrikeNotEvaluated:"));
        var adapter = new XlsxAdapter();
        var restored = adapter.Restore(new MemoryStream(bytes), adapter.CreatePatchPlan(result.Graph, result.Graph)).Bytes;
        Assert.Equal(bytes, restored);
    }

    [Theory]
    [InlineData("<f>1+2</f>")]
    [InlineData("<f>1+2</f><v></v>")]
    [InlineData("<v>#N/A</v>")]
    public void Missing_formula_cache_and_errors_leave_the_condition_unknown(string value)
    {
        var bytes = ConditionalPackage($"<row r=\"1\"><c r=\"A1\"{(value.Contains("#") ? " t=\"e\"" : "")}>{value}</c></row>",
            ConditionalRuleXml("A1&gt;0"));
        var result = new XlsxAdapter().Extract(new MemoryStream(bytes));
        Assert.False(result.Worksheets[0].Cells[0].DisplayStyle?.IsStrike == true);
        Assert.Contains(result.Warnings, warning => warning.StartsWith("XlsxConditionalStrikeNotEvaluated:"));
    }

    [Fact]
    public async Task Full_sheet_ranges_are_sparse_concurrent_and_cancellable_and_readable_exports_keep_marks()
    {
        var bytes = ConditionalPackage("<row r=\"1\"><c r=\"A1\"><v>3</v></c><c r=\"B1\"><v>1</v></c></row>",
            ConditionalRuleXml("A1&gt;2", range: "A1:XFD1048576"));
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => new XlsxAdapter().Extract(new MemoryStream(bytes)))));
        foreach (var result in results)
        {
            Assert.Equal(2, result.Worksheets[0].Cells.Count);
            Assert.Equal(["A1"], result.Worksheets[0].Cells.Where(cell => cell.DisplayStyle?.IsStrike == true).Select(cell => cell.CellReference));
            Assert.Empty(result.Warnings);
        }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => new XlsxAdapter().Extract(new MemoryStream(bytes), cancellationToken: cancel.Token));
        var directory = Directory.CreateTempSubdirectory("conditional-strike-").FullName;
        try
        {
            var source = Path.Combine(directory, "source.xlsx"); await File.WriteAllBytesAsync(source, bytes);
            var service = new DocumentService(null, null, discoverPdfRasterizer: false);
            var readable = await service.ExportReadableAsync(new(source, Path.Combine(directory, "readable.md")));
            Assert.Contains("~~3~~", await File.ReadAllTextAsync(readable.MarkdownPath));
            var package = await service.ExportAiPackageAsync(new(new(source, "unused.md"), Path.Combine(directory, "package")));
            Assert.Contains("~~3~~", string.Join("\n", package.Parts.Select(part => part.Markdown)));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        }
        finally { Directory.Delete(directory, true); }
    }
}
