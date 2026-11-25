using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Xlsx;
using DocRedock.Markdown;
using DocRedock.Render;

namespace DocRedock.Tests.Xlsx;

/// <summary>Regression: Excel print headers/footers (page numbers, confidentiality labels) were
/// dropped from Markdown and HTML output without any warning.</summary>
public sealed class WorkbookFooterFeedbackTests
{
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Secret = "CONFIDENTIAL-920A";
    private const string Data = """<sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>V</t></is></c></row></sheetData>""";

    private sealed record SheetSpec(string Name, string Body, string? State = null);

    [Theory]
    [InlineData("oddHeader")]
    [InlineData("oddFooter")]
    [InlineData("firstHeader")]
    [InlineData("firstFooter")]
    [InlineData("evenHeader")]
    [InlineData("evenFooter")]
    public void Every_header_and_footer_kind_sets_the_presence_flag(string kind)
    {
        var graph = Extract(new SheetSpec("Sheet1", $"{Data}<headerFooter><{kind}>&amp;A&amp;C</{kind}></headerFooter>"));
        Assert.True(PrintOf(graph, "Sheet1").GetProperty("headerFooterPresent").GetBoolean());
    }

    [Fact]
    public void Nonempty_footer_after_an_empty_header_is_seen_and_the_scan_continues()
    {
        var graph = Extract(new SheetSpec("Sheet1", Data + "<headerFooter><oddHeader/><oddFooter>&amp;P</oddFooter></headerFooter><pageSetup paperSize=\"9\" orientation=\"landscape\"/>"));
        var print = PrintOf(graph, "Sheet1");
        Assert.True(print.GetProperty("headerFooterPresent").GetBoolean());
        Assert.Equal("landscape", print.GetProperty("orientation").GetString());
        Assert.DoesNotContain(Native(graph), d => d.Code == "XlsxPrintHeaderFooterNotRendered");
        Assert.Contains("data-drr-print-field=\"page\"", Html(graph), StringComparison.Ordinal);
    }

    [Fact]
    public void Page_number_codes_and_confidentiality_literal_are_retained_for_native_printing()
    {
        var graph = Extract(new SheetSpec("Sheet1",
            $"{Data}<headerFooter><oddFooter>&amp;L&amp;P / &amp;N&amp;R{Secret}</oddFooter></headerFooter>"));
        Assert.True(PrintOf(graph, "Sheet1").GetProperty("headerFooterPresent").GetBoolean());
        Assert.Contains(Secret, graph.FormatExtensions!["xlsx_layout"].GetRawText(), StringComparison.Ordinal);
        Assert.Contains(Secret, Html(graph), StringComparison.Ordinal);
    }

    [Fact]
    public void Picture_header_code_is_detected()
    {
        var graph = Extract(new SheetSpec("Sheet1", $"{Data}<headerFooter><oddHeader>&amp;Picture&amp;G</oddHeader></headerFooter>"));
        Assert.True(PrintOf(graph, "Sheet1").GetProperty("headerFooterPresent").GetBoolean());
    }

    [Fact]
    public void Empty_or_whitespace_only_header_and_footer_is_not_present()
    {
        var graph = Extract(new SheetSpec("Sheet1",
            $"{Data}<headerFooter><oddHeader></oddHeader><oddFooter>   </oddFooter><firstHeader>&#160;</firstHeader></headerFooter>"));
        Assert.False(PrintOf(graph, "Sheet1").TryGetProperty("headerFooterPresent", out _));
        Assert.DoesNotContain(Native(graph), d => d.Code == "XlsxPrintHeaderFooterNotRendered");
        Assert.DoesNotContain(Markdown(graph).Diagnostics, d => d.Code == "XlsxPrintHeaderFooterNotRendered");
    }

    [Fact]
    public void Margin_settings_without_header_or_footer_content_do_not_warn()
    {
        var graph = Extract(new SheetSpec("Sheet1",
            $"{Data}<pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/><pageSetup paperSize=\"9\"/>"));
        Assert.False(PrintOf(graph, "Sheet1").TryGetProperty("headerFooterPresent", out _));
        Assert.DoesNotContain(Native(graph), d => d.Code == "XlsxPrintHeaderFooterNotRendered");
    }

    [Fact]
    public void Only_selected_sheets_contribute_print_furniture()
    {
        var graph = Extract(
            new SheetSpec("Alpha", $"{Data}<headerFooter><oddFooter>ALPHA-FOOTER &amp;P</oddFooter></headerFooter>"),
            new SheetSpec("Beta", $"{Data}<headerFooter><oddFooter>BETA-FOOTER &amp;N</oddFooter></headerFooter>"));
        var html = Html(graph, select: ["Alpha"]);
        Assert.Contains("ALPHA-FOOTER", html, StringComparison.Ordinal);
        Assert.DoesNotContain("BETA-FOOTER", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Visible_policy_skips_hidden_sheet_furniture_and_complete_policy_includes_it()
    {
        var graph = Extract(
            new SheetSpec("Shown", $"{Data}<headerFooter><oddFooter>SHOWN-FOOTER &amp;P</oddFooter></headerFooter>"),
            new SheetSpec("Gone", $"{Data}<headerFooter><oddFooter>HIDDEN-FOOTER &amp;N</oddFooter></headerFooter>", State: "hidden"));
        var visible = Html(graph);
        Assert.Contains("SHOWN-FOOTER", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDEN-FOOTER", visible, StringComparison.Ordinal);
        Assert.Contains("HIDDEN-FOOTER", Html(graph, policy: "complete"), StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitized_policy_warns_nothing_and_leaks_no_footer_text()
    {
        var graph = Extract(new SheetSpec("Sheet1", $"{Data}<headerFooter><oddFooter>&amp;P&amp;R{Secret}</oddFooter></headerFooter>"));
        var (markdown, diagnostics) = Markdown(graph, policy: "sanitized");
        Assert.DoesNotContain(diagnostics, d => d.Code == "XlsxPrintHeaderFooterNotRendered");
        Assert.DoesNotContain(Secret, markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, Html(graph, policy: "sanitized"), StringComparison.Ordinal);
    }

    [Fact]
    public void Native_projection_and_markdown_serializer_keep_footer_content_without_omission_warning()
    {
        var graph = Extract(new SheetSpec("Sheet1", $"{Data}<headerFooter><oddFooter>&amp;P&amp;R{Secret}</oddFooter></headerFooter>"));
        var (markdown, diagnostics) = Markdown(graph);
        Assert.DoesNotContain(Native(graph), d => d.Code == "XlsxPrintHeaderFooterNotRendered");
        Assert.DoesNotContain(diagnostics, d => d.Code == "XlsxPrintHeaderFooterNotRendered");
        Assert.Contains(Secret, markdown, StringComparison.Ordinal);
        Assert.Contains(Secret, Html(graph), StringComparison.Ordinal);
    }

    [Fact]
    public void Layout_json_round_trips_the_flag_and_malformed_flags_default_to_false()
    {
        var layout = new WorkbookPresentationLayout(1, new Dictionary<string, WorkbookSheetPresentation>
        {
            ["sheet-A"] = new(Name: "A", Print: new WorkbookPrintSettings(HeaderFooterPresent: true)),
        });
        var json = WorkbookPresentationLayoutJson.Write(layout);
        Assert.True(json.GetProperty("sheets").GetProperty("sheet-A").GetProperty("print").GetProperty("headerFooterPresent").GetBoolean());
        Assert.True(WorkbookPresentationLayoutJson.TryRead(json, out var back));
        Assert.True(back!.Sheets["sheet-A"].Print!.HeaderFooterPresent);
        var bad = JsonDocument.Parse("""{"version":1,"sheets":{"sheet-A":{"print":{"headerFooterPresent":"yes"}},"sheet-B":{"print":{"headerFooterPresent":1}},"sheet-C":{"print":{}}}}""").RootElement;
        Assert.True(WorkbookPresentationLayoutJson.TryRead(bad, out var malformed));
        Assert.False(malformed!.Sheets["sheet-A"].Print!.HeaderFooterPresent!.Value);
        Assert.False(malformed.Sheets["sheet-B"].Print!.HeaderFooterPresent!.Value);
        Assert.False(malformed.Sheets["sheet-C"].Print!.HeaderFooterPresent!.Value);
    }

    private static JsonElement PrintOf(DocumentGraph graph, string sheet) =>
        graph.FormatExtensions!["xlsx_layout"].GetProperty("sheets").GetProperty("sheet-" + sheet).GetProperty("print");

    private static string Html(DocumentGraph graph, string policy = "visible", string[]? select = null) =>
        ReadableHtmlWriter.WriteDocument(ReadableProjectionBuilder.Build(graph,
            new ReadableProjectionOptions(ContentPolicy: policy, IncludedSheets: select)).Document).Html;

    private static IReadOnlyList<MarkdownDiagnostic> Native(DocumentGraph graph, string policy = "visible", string[]? select = null) =>
        ReadableProjectionBuilder.Build(graph, new ReadableProjectionOptions(ContentPolicy: policy, IncludedSheets: select)).Diagnostics;

    private static (string Markdown, IReadOnlyList<MarkdownDiagnostic> Diagnostics) Markdown(DocumentGraph graph, string policy = "visible", string[]? select = null)
    {
        var serializer = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(ContentPolicy: policy, IncludedSheets: select, Language: "en"));
        return (serializer.Serialize(graph), serializer.Diagnostics);
    }

    private static DocumentGraph Extract(params SheetSpec[] sheets)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            void Add(string path, string content) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(content); }
            Add("[Content_Types].xml", """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/></Types>""");
            var sheetXml = string.Concat(sheets.Select((s, i) => "<sheet name=\"" + System.Security.SecurityElement.Escape(s.Name) +
                "\" sheetId=\"" + (i + 1) + "\" r:id=\"r" + (i + 1) + "\"" +
                (s.State is null ? "" : " state=\"" + s.State + "\"") + "/>"));
            Add("xl/workbook.xml", $"""<workbook xmlns="{Ns}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>{sheetXml}</sheets></workbook>""");
            Add("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""" +
                string.Concat(sheets.Select((s, i) => $"""<Relationship Id="r{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i + 1}.xml"/>""")) + "</Relationships>");
            Add("xl/styles.xml", $"""<styleSheet xmlns="{Ns}"><fonts count="1"><font/></fonts><fills count="1"><fill><patternFill patternType="none"/></fill></fills><borders count="1"><border/></borders><cellXfs count="1"><xf fontId="0" fillId="0" borderId="0"/></cellXfs></styleSheet>""");
            for (var i = 0; i < sheets.Length; i++)
                Add($"xl/worksheets/sheet{i + 1}.xml", $"""<worksheet xmlns="{Ns}">{sheets[i].Body}</worksheet>""");
        }
        bytes.Position = 0;
        return new XlsxAdapter().Extract(bytes).Graph;
    }
}
