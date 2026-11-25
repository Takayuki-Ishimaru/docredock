using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Xlsx;
using DocRedock.Markdown;
using DocRedock.Render;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DocRedock.Tests.Xlsx;

/// <summary>
/// A worksheet hyperlink's target is its relationship: the export must carry a resolved External URL into
/// the typed cell record and the graph, so the native HTML writer emits a real safe anchor and the readable
/// Markdown a real link. Text that merely looks like a URL stays text, an unsafe target never becomes a
/// live link, and a missing relationship keeps the cell's text with a precise warning.
/// </summary>
public sealed class WorkbookHyperlinkFeedbackTests
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string QueryUrl = "https://example.com/search?q=a+b&lang=en#sec";
    private const string Label = "Docs & \"notes\" [v1]";

    private static string Cell(string reference, string text, string style = "0") =>
        $"<c r=\"{reference}\" t=\"inlineStr\" s=\"{style}\"><is><t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(text)}</t></is></c>";

    private static string Rels(string id, string target) =>
        "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        $"<Relationship Id=\"{id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"{target}\" TargetMode=\"External\"/></Relationships>";

    private static XlsxExtractionResult Extract(string cells, string? hyperlinks, string? sheetRels, bool cellsAreRows = false)
    {
        var sheet = Encoding.UTF8.GetBytes(
            $"<?xml version=\"1.0\"?><worksheet xmlns=\"{Main}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheetData>" +
            (cellsAreRows ? cells : "<row r=\"1\">" + cells + "</row>") + "</sheetData>" +
            (hyperlinks is null ? "" : "<hyperlinks>" + hyperlinks + "</hyperlinks>") + "</worksheet>");
        using var package = new MemoryStream();
        using (var zip = new ZipArchive(package, ZipArchiveMode.Create, true))
        {
            void Add(string name, string xml) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(xml); }
            Add("[Content_Types].xml",
                """<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
            Add("_rels/.rels",
                """<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Add("xl/workbook.xml",
                $"""<?xml version="1.0"?><workbook xmlns="{Main}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="S" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Add("xl/_rels/workbook.xml.rels",
                """<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
            Add("xl/styles.xml",
                $"""<?xml version="1.0"?><styleSheet xmlns="{Main}"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><strike/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="2"><xf xfId="0"/><xf fontId="1" xfId="0" applyFont="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>""");
            Add("xl/worksheets/sheet1.xml", Encoding.UTF8.GetString(sheet));
            if (sheetRels is not null) Add("xl/worksheets/_rels/sheet1.xml.rels", sheetRels);
        }
        package.Position = 0;
        return new XlsxAdapter().Extract(package);
    }

    private static string MarkdownOf(XlsxExtractionResult result) => new ReadableMarkdownSerializer().Serialize(result.Graph);

    private static string HtmlOf(XlsxExtractionResult result) =>
        ReadableHtmlWriter.WriteDocument(ReadableProjectionBuilder.Build(result.Graph).Document).Html;

    [Fact]
    public void An_external_relation_target_reaches_the_typed_run_and_a_native_safe_anchor()
    {
        var result = Extract(Cell("A1", Label), "<hyperlink ref=\"A1\" r:id=\"rId1\"/>", Rels("rId1", QueryUrl.Replace("&", "&amp;")));
        var record = Assert.Single(result.Worksheets.Single().Cells, cell => cell.CellReference == "A1");
        Assert.Equal(Label, record.Value);
        Assert.Equal(QueryUrl, record.HyperlinkTarget);
        var node = Assert.Single(result.Graph.Nodes, candidate =>
            candidate.Extensions is not null && candidate.Extensions.TryGetValue("hyperlink_target", out var target) &&
            target.ValueKind == JsonValueKind.String && target.GetString() == QueryUrl);
        Assert.Equal(Label, Assert.IsType<TextNodeContent>(node.Content).Text);
        var markdown = MarkdownOf(result);
        Assert.Equal(QueryUrl, Assert.Single(((MarkdownObject)Markdig.Markdown.Parse(markdown)).Descendants<LinkInline>()).Url);
        Assert.Contains("Docs", markdown);
        var html = HtmlOf(result);
        Assert.Contains("<a href=\"https://example.com/search?q=a+b&amp;lang=en#sec\">", html);
        Assert.DoesNotContain("<a href=\"https://example.com/search?q=a+b&lang", html);
    }

    [Fact]
    public void Url_text_without_a_worksheet_relation_stays_plain_text()
    {
        var result = Extract(Cell("A1", "https://example.com/plain"), null, null);
        var record = Assert.Single(result.Worksheets.Single().Cells);
        Assert.Null(record.HyperlinkTarget);
        Assert.DoesNotContain("](", MarkdownOf(result));
        Assert.DoesNotContain("<a href=\"https://example.com/plain", HtmlOf(result));
    }

    [Fact]
    public void A_location_only_link_retains_the_original_workbook_destination()
    {
        var result = Extract(Cell("A1", "go"), "<hyperlink ref=\"A1\" location=\"Sheet1!A5\"/>", null);
        Assert.Equal("#Sheet1!A5", Assert.Single(result.Worksheets.Single().Cells).HyperlinkTarget);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("Hyperlink", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unsafe_target_never_becomes_a_live_link()
    {
        var result = Extract(Cell("A1", "click"), "<hyperlink ref=\"A1\" r:id=\"rId1\"/>", Rels("rId1", "javascript:alert(1)"));
        Assert.Equal("javascript:alert(1)", Assert.Single(result.Worksheets.Single().Cells).HyperlinkTarget);
        var markdown = MarkdownOf(result);
        Assert.DoesNotContain("](", markdown);
        Assert.DoesNotContain("<a href=\"javascript:", HtmlOf(result));
    }

    [Fact]
    public void A_missing_relationship_keeps_the_label_and_warns_precisely()
    {
        var result = Extract(Cell("A1", Label), "<hyperlink ref=\"A1\" r:id=\"rId9\"/>", Rels("rId1", QueryUrl.Replace("&", "&amp;")));
        var record = Assert.Single(result.Worksheets.Single().Cells);
        Assert.Equal(Label, record.Value);
        Assert.Null(record.HyperlinkTarget);
        Assert.Contains(result.Warnings, warning => warning.Contains("XlsxHyperlinkRelationshipMissing", StringComparison.Ordinal)
            && warning.Contains("rId9", StringComparison.Ordinal));
        Assert.DoesNotContain("](", MarkdownOf(result));
        Assert.DoesNotContain("<a href=\"https://example.com/search", HtmlOf(result));
    }

    [Fact]
    public void A_whole_row_link_maps_its_cells_without_densifying_the_row()
    {
        var stopwatch = Stopwatch.StartNew();
        var result = Extract(Cell("A1", "a1") + Cell("C1", "c1"), "<hyperlink ref=\"A1:XFD1\" r:id=\"rId1\"/>", Rels("rId1", QueryUrl.Replace("&", "&amp;")));
        foreach (var reference in new[] { "A1", "C1" })
            Assert.Equal(QueryUrl, Assert.Single(result.Worksheets.Single().Cells, cell => cell.CellReference == reference).HyperlinkTarget);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public void Range_targets_expire_at_the_right_row_and_last_declarations_win_even_for_unordered_cells()
    {
        var cells = "<row r=\"2\">" + Cell("A2", "a2") + Cell("B2", "b2") + "</row><row r=\"1\">" + Cell("A1", "a1") + Cell("B1", "b1") +
            "</row><row r=\"4\">" + Cell("B4", "b4") + "</row><row r=\"3\">" + Cell("A3", "a3") + Cell("B3", "b3") + "</row>";
        var rels = Rels("rId1", "https://example.com/1").Replace("</Relationships>",
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"https://example.com/2\" TargetMode=\"External\"/>" +
            "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"https://example.com/3\" TargetMode=\"External\"/></Relationships>", StringComparison.Ordinal);
        var result = Extract(cells, "<hyperlink ref=\"A1:B2\" r:id=\"rId1\"/><hyperlink ref=\"B2:B3\" r:id=\"rId2\"/><hyperlink ref=\"A1\" r:id=\"rId3\"/>", rels, cellsAreRows: true);
        var targets = result.Worksheets.Single().Cells.ToDictionary(c => c.CellReference, c => c.HyperlinkTarget);
        Assert.Equal("https://example.com/3", targets["A1"]);
        Assert.Equal("https://example.com/1", targets["A2"]);
        Assert.Equal("https://example.com/1", targets["B1"]);
        Assert.Equal("https://example.com/2", targets["B2"]);
        Assert.Equal("https://example.com/2", targets["B3"]);
        Assert.Null(targets["A3"]);
        Assert.Null(targets["B4"]);
    }

    [Fact]
    public void A_whole_sheet_link_materializes_only_populated_source_cells()
    {
        var cells = "<row r=\"1\">" + Cell("A1", "first") + "</row><row r=\"1048576\">" + Cell("XFD1048576", "last") + "</row>";
        var result = Extract(cells, "<hyperlink ref=\"A1:XFD1048576\" r:id=\"rId1\"/>", Rels("rId1", "https://example.com/all"), cellsAreRows: true);
        Assert.Equal(2, result.Worksheets.Single().Cells.Count);
        Assert.All(result.Worksheets.Single().Cells, c => Assert.Equal("https://example.com/all", c.HyperlinkTarget));
    }

    [Fact]
    public void Shared_literal_hyperlink_formulas_keep_each_saved_label_and_the_same_target()
    {
        var result = Extract("""
            <c r="A1" t="str"><f t="shared" si="0" ref="A1:B1">HYPERLINK("https://example.com/shared","Master")</f><v>Master</v></c>
            <c r="B1" t="str"><f t="shared" si="0"/><v>Copy</v></c>
            """, null, null);
        Assert.Equal(new[] { "Master", "Copy" }, result.Worksheets.Single().Cells.Select(c => c.Value));
        Assert.All(result.Worksheets.Single().Cells, c => Assert.Equal("https://example.com/shared", c.HyperlinkTarget));
        Assert.DoesNotContain(result.Warnings, w => w.StartsWith("XlsxHyperlinkFormulaTargetNotProjected:", StringComparison.Ordinal));
    }

    [Fact]
    public void Source_strike_and_bold_on_a_native_link_are_retained()
    {
        var result = Extract(Cell("A1", "Docs", style: "1"), "<hyperlink ref=\"A1\" r:id=\"rId1\"/>", Rels("rId1", QueryUrl.Replace("&", "&amp;")));
        var record = Assert.Single(result.Worksheets.Single().Cells);
        Assert.NotNull(record.StruckRanges);
        Assert.Null(record.UnderlinedRanges);
        var markdown = MarkdownOf(result);
        Assert.Contains("~~", markdown);
        Assert.Equal(QueryUrl, Assert.Single(((MarkdownObject)Markdig.Markdown.Parse(markdown)).Descendants<LinkInline>()).Url);
        Assert.Contains("<a href=\"https://example.com/search?q=a+b&amp;lang=en#sec\">", HtmlOf(result));
    }

    [Theory]
    [InlineData("HYPERLINK(\"https://example.com/f\",\"text\")", true)]
    [InlineData("_xlfn.HYPERLINK(\"https://example.com/f\")", true)]
    [InlineData("HYPERLINK(A2,\"text\")", false)]
    [InlineData("HYPERLINK(\"https://example.com/\"&A2,\"text\")", false)]
    [InlineData("HYPERLINK(\"https://example.com/f\")&\"suffix\"", false)]
    [InlineData("IF(A2,HYPERLINK(\"https://example.com/f\"),\"text\")", false)]
    public void Hyperlink_formulas_keep_only_a_definite_literal_target_without_calculation(string formula, bool linked)
    {
        var result = Extract("<c r=\"A1\" t=\"str\"><f>" + System.Security.SecurityElement.Escape(formula) + "</f><v>text</v></c>", null, null);
        var record = Assert.Single(result.Worksheets.Single().Cells);
        Assert.Equal("text", record.Value);
        Assert.Equal(linked ? "https://example.com/f" : null, record.HyperlinkTarget);
        Assert.Null(record.UnderlinedRanges);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("XlsxHyperlinkRelationshipMissing", StringComparison.Ordinal));
        Assert.Equal(!linked, result.Warnings.Any(warning => warning.StartsWith("XlsxHyperlinkFormulaTargetNotProjected:", StringComparison.Ordinal)));
        Assert.Equal(linked, HtmlOf(result).Contains("<a href=\"https://example.com/f\"", StringComparison.Ordinal));
    }
}
