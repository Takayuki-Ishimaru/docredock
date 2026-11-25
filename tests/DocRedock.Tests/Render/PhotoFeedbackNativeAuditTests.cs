using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using DocRedock.Render;
using DocRedock.Tests.Xlsx;

namespace DocRedock.Tests.Render;

/// <summary>Audit regressions for photo-feedback expectations on the native HTML route.</summary>
public sealed class PhotoFeedbackNativeAuditTests
{
    [Fact]
    public void Diagram_node_and_edge_labels_stay_searchable_text_outside_the_svg_and_mermaid_source()
    {
        var diagram = new ReadableDiagram("flowchart LR\nA[LabelA] <-->|Exchange| B[LabelB]", "Diagram", []);
        var html = ReadableHtmlWriter.WriteDocument(
            new ReadableDocument("Title", "en", DocumentFormatKind.Xlsx, [diagram])).Html;
        var text = VisibleText(html);
        Assert.Contains("LabelA", text, StringComparison.Ordinal);
        Assert.Contains("LabelB", text, StringComparison.Ordinal);
        Assert.Contains("Exchange", text, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"↔|<->|bidirectional", RegexOptions.CultureInvariant), text);
    }

    [Fact]
    public void Invalid_visual_graph_warns_and_keeps_node_and_bidirectional_edge_labels_as_visible_text()
    {
        var visual = new VisualGraph(
            "audit-flow",
            [new VisualNode("a", "AUDIT_NODE_A"), new VisualNode("b", "AUDIT_NODE_B")],
            [new VisualEdge("e1", "a", "b", "AUDIT_EDGE_LABEL", EdgeDirection: VisualEdgeDirection.Bidirectional),
             new VisualEdge("e2", "b", "missing-node")]);
        var diagram = new DocumentNode("audit-diagram", NodeKind.Diagram, null, 0, ContentLayer.Derived,
            new TextNodeContent("audit diagram"), Extensions: Ext(("visual_graph", visual)));
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "photo-audit", DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet1", 0, [diagram])]);
        var projected = ReadableProjectionBuilder.Build(graph, new(Language: "en"));
        Assert.Contains(projected.Diagnostics, d => d.Code == "VisualSemanticProjectionFallback");
        var html = ReadableHtmlWriter.WriteDocument(projected.Document).Html;
        var text = VisibleText(html);
        Assert.Contains("AUDIT_NODE_A", text, StringComparison.Ordinal);
        Assert.Contains("AUDIT_NODE_B", text, StringComparison.Ordinal);
        Assert.Contains("AUDIT_EDGE_LABEL", text, StringComparison.Ordinal);
        Assert.Contains("↔", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Print_area_excludes_images_and_diagrams_outside_its_source_coordinates()
    {
        var origin = new ReadableSourceOrigin("sheet-Print", "Print", "A50:C60", "outside-image");
        var document = new ReadableDocument("Print", "en", DocumentFormatKind.Xlsx,
            [new ReadableSection("Print", ReadableContent.Text("Print"),
                [new ReadableImage("outside.png", "OUTSIDE_IMAGE") { Origin = origin },
                 new ReadableDiagram("flowchart LR\nA[OutsideA] --> B[OutsideB]", "OUTSIDE_DIAGRAM", []) { Origin = origin }])
                { Origin = new("sheet-Print", "Print") }])
        {
            WorkbookLayout = WorkbookPresentationLayoutJson.Write(new WorkbookPresentationLayout(1,
                new Dictionary<string, WorkbookSheetPresentation>
                { ["sheet-Print"] = new(Name: "Print", Print: new(Areas: ["A1:C5"])) }))
        };
        var html = ReadableHtmlWriter.WriteDocument(document).Html;
        Assert.Equal(2, Regex.Matches(html, "<div class=\"outside-print-area\">").Count);
        Assert.Contains(".direct-document .outside-print-area{display:none!important}", html, StringComparison.Ordinal);
        Assert.Contains("OUTSIDE_IMAGE", html, StringComparison.Ordinal);
        Assert.Contains("OUTSIDE_DIAGRAM", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_foreground_and_highlight_colors_follow_the_selected_theme_contract()
    {
        var document = new ReadableDocument("Colors", "en", DocumentFormatKind.Docx,
            [new ReadableParagraph(new ReadableContent([
                new TextRun("black", Color: "000000"),
                new TextRun("required", HighlightColor: "red"),
                new TextRun("optional", HighlightColor: "green")]))]);
        var html = ReadableHtmlWriter.WriteDocument(document).Html;
        Assert.Contains("class=\"source-low-dark\" style=\"color:#000000\"", html, StringComparison.Ordinal);
        Assert.Contains("<mark style=\"background-color:#FF0000\">required</mark>", html, StringComparison.Ordinal);
        Assert.Contains("<mark style=\"background-color:#00FF00\">optional</mark>", html, StringComparison.Ordinal);
        Assert.Contains("#docredock-theme-dark:checked~.reader-shell .source-low-dark", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Legend_row_without_source_freeze_is_not_pinned_and_no_nested_scroll_max_height_traps_the_table()
    {
        var graph = WorkbookPresentationTests.Extract("""
            <sheetData><row r="1"><c r="A1" s="1" t="inlineStr"><is><t>LEGEND</t></is></c></row>
            <row r="3"><c r="A3" s="1" t="inlineStr"><is><t>Item</t></is></c><c r="B3" s="1" t="inlineStr"><is><t>Value</t></is></c></row>
            <row r="4"><c r="A4" t="inlineStr"><is><t>Alpha</t></is></c><c r="B4"><v>42</v></c></row></sheetData>
            """).Graph;
        var document = ReadableProjectionBuilder.Build(graph, new(Language: "en")).Document;
        var table = Assert.Single(Assert.Single(document.Blocks.OfType<ReadableSection>()).Blocks.OfType<ReadableTable>());
        Assert.Equal(0, table.HeaderRowCount);
        var html = ReadableHtmlWriter.WriteDocument(document).Html;
        foreach (Match head in Regex.Matches(html, "<thead\\b[^>]*>(.*?)</thead>", RegexOptions.Singleline))
            Assert.DoesNotContain("LEGEND", head.Groups[1].Value, StringComparison.Ordinal);
        Assert.Contains("LEGEND", html, StringComparison.Ordinal);
        Assert.Contains(".direct-document .table-scroll{overflow:visible;max-height:none", html, StringComparison.Ordinal);
    }

    private static string VisibleText(string html)
    {
        var body = Regex.Replace(html, "<style\\b[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        body = Regex.Replace(body, "<svg\\b[^>]*/>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        body = Regex.Replace(body, "<svg\\b[^>]*>.*?</svg>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        body = Regex.Replace(body, "<img\\b[^>]*>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        body = Regex.Replace(body, "<details\\b[^>]*>\\s*<summary>Mermaid</summary>.*?</details>", "", RegexOptions.Singleline);
        return WebUtility.HtmlDecode(Regex.Replace(body, "<[^>]+>", " "));
    }

    private static Dictionary<string, JsonElement> Ext(params (string Key, object Value)[] values) =>
        values.ToDictionary(value => value.Key, value => JsonSerializer.SerializeToElement(value.Value), StringComparer.Ordinal);
}
