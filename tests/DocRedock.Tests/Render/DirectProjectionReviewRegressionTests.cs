using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

public sealed class DirectProjectionReviewRegressionTests
{
    [Fact]
    public void Raw_and_hash_prefixed_run_colors_render_without_throwing_or_unsafe_styles()
    {
        var graph = Graph(DocumentFormatKind.Docx, new DocumentNode("colors", NodeKind.Paragraph, null, 0, ContentLayer.Body,
            new RichTextNodeContent([
                new TextRun("RAW_HEX_COLOR", Color: "b42318"),
                new TextRun("HASH_HEX_COLOR", Color: "#B42318"),
                new TextRun("UNSAFE_COLOR_TEXT", Color: "red;display:none")])));
        var html = Html(graph);
        Assert.Matches(new Regex("<span\\b[^>]*style=\"color:#B42318\"[^>]*>RAW_HEX_COLOR</span>", RegexOptions.IgnoreCase), html);
        Assert.Matches(new Regex("<span\\b[^>]*style=\"color:#B42318\"[^>]*>HASH_HEX_COLOR</span>", RegexOptions.IgnoreCase), html);
        Assert.Contains("UNSAFE_COLOR_TEXT", BodyText(html), StringComparison.Ordinal);
        Assert.DoesNotContain("red;display:none", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pptx_paragraph_details_keep_node_level_drawn_marks_at_paragraph_offsets()
    {
        const string first = "strike this tail";
        const string second = "keep under tail";
        var full = first + "\n" + second;
        var underStart = full.IndexOf("under", StringComparison.Ordinal);
        var details = new[]
        {
            new { Text = first, Level = 0, IsBullet = false, Runs = new[] { new { Text = first, Bold = false, Italic = false, Underline = false } } },
            new { Text = second, Level = 0, IsBullet = false, Runs = new[] { new { Text = second, Bold = false, Italic = false, Underline = false } } }
        };
        var shape = new DocumentNode("shape", NodeKind.Shape, null, 0, ContentLayer.Body, new TextNodeContent(full),
            Extensions: Ext(("paragraph_details", details), ("strike_ranges", new[] { new[] { 0, 6 } }),
                ("underline_ranges", new[] { new[] { underStart, underStart + 5 } })));
        var graph = Graph(DocumentFormatKind.Pptx, shape);
        var baseline = new ReadableMarkdownSerializer(new(Language: "en")).Serialize(graph);
        Assert.Contains("~~strike~~", baseline, StringComparison.Ordinal);
        Assert.Contains("<u>under</u>", baseline, StringComparison.Ordinal);

        var html = Html(graph);
        Assert.Matches(new Regex("<(?:s|del)>strike</(?:s|del)>", RegexOptions.CultureInvariant), html);
        Assert.Contains("<u>under</u>", html, StringComparison.Ordinal);
        Assert.Equal(1, Count(BodyText(html), first));
        Assert.Equal(1, Count(BodyText(html), second));
    }

    [Fact]
    public void A_valid_diagram_does_not_suppress_source_members_of_an_invalid_neighbor()
    {
        var valid = new VisualGraph("good",
            [new VisualNode("a", "Valid A", SourceNodeId: "valid-member"), new VisualNode("b", "Valid B")],
            [new VisualEdge("good-edge", "a", "b")]);
        var invalid = new VisualGraph("bad",
            [new VisualNode("x", "Invalid X", SourceNodeId: "invalid-member"), new VisualNode("y", "Invalid Y")],
            [new VisualEdge("bad-edge", "x", "missing", SourceNodeId: "invalid-connector")]);
        var graph = Graph(DocumentFormatKind.Docx,
            Diagram("good", valid, 0), Diagram("bad", invalid, 1),
            new DocumentNode("valid-member", NodeKind.TextBox, null, 2, ContentLayer.Body, new TextNodeContent("Valid A"),
                Extensions: Ext(("visual_graph_member", true))),
            new DocumentNode("invalid-member", NodeKind.TextBox, null, 3, ContentLayer.Body, new TextNodeContent("INVALID_SOURCE_MEMBER"),
                Extensions: Ext(("visual_graph_member", true))),
            new DocumentNode("invalid-connector", NodeKind.Connector, null, 4, ContentLayer.Body, new TextNodeContent("INVALID_SOURCE_CONNECTOR"),
                Extensions: Ext(("visual_graph_edge", true))));
        var text = BodyText(Html(graph));
        Assert.Equal(1, Count(text, "INVALID_SOURCE_MEMBER"));
        Assert.Equal(1, Count(text, "INVALID_SOURCE_CONNECTOR"));
    }

    [Fact]
    public void A_vertical_merge_never_crosses_the_html_header_body_boundary()
    {
        var table = new DocumentNode("table", NodeKind.Table, null, 0, ContentLayer.Body,
            new TableNodeContent([
                [new TableCell("MergedHeaderText", RowSpan: 2), new TableCell("OtherHeaderText")],
                [new TableCell("", RowSpan: 0), new TableCell("SecondRowData")]
            ]));
        var html = Html(Graph(DocumentFormatKind.Docx, table));
        Assert.Contains("rowspan=\"2\"", html, StringComparison.Ordinal);
        foreach (Match group in Regex.Matches(html, "<thead\\b[^>]*>(.*?)</thead>", RegexOptions.Singleline))
        {
            var rows = Regex.Matches(group.Groups[1].Value, "<tr\\b[^>]*>.*?</tr>", RegexOptions.Singleline);
            for (var row = 0; row < rows.Count; row++)
                foreach (Match span in Regex.Matches(rows[row].Value, "rowspan=\"([0-9]+)\""))
                    Assert.True(row + int.Parse(span.Groups[1].Value) <= rows.Count,
                        "A rowspan cannot continue from thead into tbody: " + group.Value);
        }
        var text = BodyText(html);
        Assert.Equal(1, Count(text, "MergedHeaderText"));
        Assert.Equal(1, Count(text, "OtherHeaderText"));
        Assert.Equal(1, Count(text, "SecondRowData"));
    }

    [Fact]
    public void Table_overlay_text_survives_once_with_its_source_cell_position()
    {
        var overlays = new[]
        {
            new { ShapeId = "arrow1", Kind = "arrow", Direction = "right", Axis = "horizontal",
                StartRow = 1, EndRow = 1, StartColumn = 1, EndColumn = 2, Text = "OverlayUnique" }
        };
        var table = new DocumentNode("overlay-table", NodeKind.Table, null, 0, ContentLayer.Body,
            new TableNodeContent([
                [new TableCell("Stage"), new TableCell("D1"), new TableCell("D2")],
                [new TableCell("Design"), new TableCell(""), new TableCell("")]
            ]), Extensions: Ext(("shape_id", "overlay-table"), ("table_overlays", overlays)));
        var host = new DocumentNode("arrow1", NodeKind.Shape, null, 1, ContentLayer.Body,
            new TextNodeContent("OverlayUnique"), Extensions: Ext(("table_overlay_host", "overlay-table"), ("table_overlay", true)));
        var graph = Graph(DocumentFormatKind.Docx, table, host);
        var baseline = new ReadableMarkdownSerializer(new(Language: "en")).Serialize(graph);
        Assert.Equal(1, Count(baseline, "OverlayUnique"));

        var html = Html(graph);
        Assert.Equal(1, Count(BodyText(html), "OverlayUnique"));
        // Native cell rendering or a typed source note are both valid. A source note carries B2:C2.
        var atCell = Regex.Matches(html, "<tr\\b[^>]*data-source-row=\"2\"[^>]*>(.*?)</tr>", RegexOptions.Singleline)
            .Any(row => Regex.Matches(row.Groups[1].Value,
                "<t[dh]\\b[^>]*data-source-column=\"2\"[^>]*>(.*?)</t[dh]>", RegexOptions.Singleline)
                .Any(cell => BodyText(cell.Groups[1].Value).Contains("OverlayUnique", StringComparison.Ordinal)));
        Assert.True(atCell || html.Contains("B2:C2", StringComparison.Ordinal),
            "The overlay must remain associated with its source row/columns, either in the cells or as a source-labelled typed note.");
    }

    [Fact]
    public void Smartart_diagram_items_are_retained_once_as_searchable_source_text()
    {
        var diagram = new DocumentNode("smartart", NodeKind.Diagram, null, 0, ContentLayer.Derived,
            new TextNodeContent("Process"), Extensions: Ext(("diagram_items", new[] { "SMARTART_ITEM_A", "SMARTART_ITEM_B" })));
        var graph = Graph(DocumentFormatKind.Docx, diagram);
        var baseline = new ReadableMarkdownSerializer(new(Language: "en")).Serialize(graph);
        Assert.Contains("SMARTART\\_ITEM\\_A", baseline, StringComparison.Ordinal);
        Assert.Contains("SMARTART\\_ITEM\\_B", baseline, StringComparison.Ordinal);
        var text = BodyText(Html(graph));
        Assert.Equal(1, Count(text, "SMARTART_ITEM_A"));
        Assert.Equal(1, Count(text, "SMARTART_ITEM_B"));
    }

    private static DocumentNode Diagram(string id, VisualGraph graph, int order) =>
        new(id, NodeKind.Diagram, null, order, ContentLayer.Derived, new TextNodeContent(id), Extensions: Ext(("visual_graph", graph)));

    private static DocumentGraph Graph(DocumentFormatKind format, params DocumentNode[] nodes) =>
        new(DocumentGraph.CurrentSchemaVersion, "direct-review", format, [new DocumentPartition("document", 0, nodes)]);

    private static Dictionary<string, JsonElement> Ext(params (string Key, object Value)[] values) =>
        values.ToDictionary(value => value.Key, value => JsonSerializer.SerializeToElement(value.Value), StringComparer.Ordinal);

    private static string Html(DocumentGraph graph) => ReadableHtmlWriter.WriteDocument(
        ReadableProjectionBuilder.Build(graph, new(Language: "en")).Document).Html;

    private static string BodyText(string html)
    {
        var body = Regex.Replace(html, "<style\\b[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        // Showing a Mermaid source in an explicitly labelled disclosure is not duplicate figure rendering.
        body = Regex.Replace(body, "<details\\b[^>]*>\\s*<summary>Mermaid</summary>.*?</details>", "", RegexOptions.Singleline);
        return WebUtility.HtmlDecode(Regex.Replace(body, "<[^>]+>", ""));
    }

    private static int Count(string text, string value) => Regex.Matches(text, Regex.Escape(value)).Count;
}
