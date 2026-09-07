using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Markdown;

namespace DocRedock.Tests.Markdown;

/// <summary>
/// Release gate for D07: ordinary source text that merely LOOKS like Markdown must never become
/// live Markdown in the produced output. Every check here parses the generated Markdown with a
/// real CommonMark/GFM parser instead of matching strings, so it fails whenever a renderer would
/// build a link, fetch an image, or silently swallow a "[id]: url" line.
/// "D07" here is the external v0.2.5 review's item number for this literal-Markdown-syntax issue;
/// it is unrelated to the fixture spec's D07 (merged table cells,
/// tests/DocRedock.Tests/Fixtures/COMPLEX_DESIGN_DOC_SPEC.md).
/// </summary>
public sealed class MarkdownLiteralSyntaxGateTests
{
    private const string InlineLink = "[LABEL](https://example.com/x)";
    private const string InlineImage = "![ALT](image.png)";
    private const string ReferenceLink = "[REF][id]";
    private const string ReferenceDefinition = "[id]: https://example.com/ref";
    private const string ShortLink = "[x](y)";

    private static readonly string[] ProbesInSourceOrder =
        [InlineLink, InlineImage, ReferenceLink, ReferenceDefinition, ShortLink];

    [Fact]
    public void Gate1_readable_output_of_a_docx_graph_produces_no_hyperlink()
    {
        var document = MarkdownStructure.Parse(new ReadableMarkdownSerializer().Serialize(DocxGraph()));

        Assert.Empty(MarkdownStructure.Links(document));
    }

    [Fact]
    public void Gate2_readable_output_never_turns_literal_image_notation_into_an_image()
    {
        var markdown = new ReadableMarkdownSerializer().Serialize(DocxGraph());
        var document = MarkdownStructure.Parse(markdown);

        Assert.Empty(MarkdownStructure.Images(document));
        Assert.Contains(InlineImage, MarkdownStructure.DisplayedText(document), StringComparison.Ordinal);
    }

    [Fact]
    public void Gate3_literal_reference_definition_line_is_never_consumed()
    {
        var document = MarkdownStructure.Parse(new ReadableMarkdownSerializer().Serialize(DocxGraph()));

        Assert.Empty(MarkdownStructure.ReferenceDefinitions(document));
        // Both halves have to remain visible: an unescaped definition line is not merely rendered
        // as a link target, it is removed from the output entirely.
        MarkdownStructure.AssertInOrder(MarkdownStructure.DisplayedText(document), ReferenceLink, ReferenceDefinition);
    }

    [Fact]
    public void Gate4a_readable_docx_output_preserves_every_probe_in_source_order()
    {
        var displayed = MarkdownStructure.DisplayedText(
            MarkdownStructure.Parse(new ReadableMarkdownSerializer().Serialize(DocxGraph())));

        MarkdownStructure.AssertInOrder(displayed, ProbesInSourceOrder);
        // The table cell repeats the first probe, so its content survived too.
        Assert.Equal(2, Occurrences(displayed, InlineLink));
    }

    [Fact]
    public void Gate4b_readable_xlsx_output_preserves_every_probe_in_source_order()
    {
        var displayed = MarkdownStructure.DisplayedText(
            MarkdownStructure.Parse(new ReadableMarkdownSerializer().Serialize(XlsxGraph())));

        MarkdownStructure.AssertInOrder(displayed, ShortLink, InlineLink, ReferenceDefinition);
    }

    [Fact]
    public void Gate4c_readable_pptx_output_preserves_every_probe_in_source_order()
    {
        var displayed = MarkdownStructure.DisplayedText(
            MarkdownStructure.Parse(new ReadableMarkdownSerializer().Serialize(PptxGraph())));

        MarkdownStructure.AssertInOrder(displayed, ProbesInSourceOrder);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    [InlineData("pptx")]
    public void Gate5_drmd_projection_stays_literal_and_round_trips_without_a_diff(string format)
    {
        var graph = format switch { "xlsx" => XlsxGraph(), "pptx" => PptxGraph(), _ => DocxGraph() };
        var markdown = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;
        var document = MarkdownStructure.Parse(markdown);

        Assert.Empty(MarkdownStructure.Links(document));
        Assert.Empty(MarkdownStructure.Images(document));
        Assert.Empty(MarkdownStructure.ReferenceDefinitions(document));

        var result = new MarkdownGraphEditor().Apply(graph, markdown);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Empty(result.Diff.PatchSet.Operations);
    }

    private static int Occurrences(string value, string probe)
    {
        var count = 0;
        for (var index = value.IndexOf(probe, StringComparison.Ordinal); index >= 0;
             index = value.IndexOf(probe, index + probe.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static DocumentGraph DocxGraph() => new(
        DocumentGraph.CurrentSchemaVersion, "doc_literal_docx", DocumentFormatKind.Docx,
        [new DocumentPartition("part-0001", 0,
        [
            Text("p_link", NodeKind.Paragraph, 0, InlineLink),
            Text("p_image", NodeKind.Paragraph, 1, InlineImage),
            Heading("h_ref", 2, ReferenceLink, level: 2),
            Text("li_def", NodeKind.ListItem, 3, ReferenceDefinition),
            Text("q_short", NodeKind.Quote, 4, ShortLink),
            new DocumentNode("table_1", NodeKind.Table, null, 5, ContentLayer.Body,
                new TableNodeContent([new TableCell[] { "見出し" }, new TableCell[] { InlineLink }])),
        ])]);

    private static DocumentGraph XlsxGraph() => new(
        DocumentGraph.CurrentSchemaVersion, "doc_literal_xlsx", DocumentFormatKind.Xlsx,
        [new DocumentPartition("sheet-Sheet1", 0,
        [
            Cell("A1", 1, ShortLink),
            Cell("A2", 2, InlineLink),
            Cell("A3", 3, ReferenceDefinition),
        ])]);

    private static DocumentGraph PptxGraph() => new(
        DocumentGraph.CurrentSchemaVersion, "doc_literal_pptx", DocumentFormatKind.Pptx,
        [new DocumentPartition("slide-1", 0,
        [
            Shape("shape_1", 0, InlineLink),
            Shape("shape_2", 1, InlineImage),
            Shape("shape_3", 2, ReferenceLink),
            Shape("shape_4", 3, ReferenceDefinition),
            Shape("shape_5", 4, ShortLink),
        ])]);

    private static DocumentNode Text(string id, NodeKind kind, int order, string text) =>
        new(id, kind, null, order, ContentLayer.Body, new TextNodeContent(text));

    private static DocumentNode Heading(string id, int order, string text, int level) =>
        new(id, NodeKind.Heading, null, order, ContentLayer.Body, new TextNodeContent(text),
            Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["heading_level"] = JsonSerializer.SerializeToElement(level),
            });

    private static DocumentNode Cell(string address, int row, string value) => new(
        "cell-" + address, NodeKind.Cell, null, row * 1000 + 1, ContentLayer.Body, new TextNodeContent(value),
        new SourceAnchor("xlsx", "/xl/worksheets/sheet1.xml", [new AnchorLocator("cell_address", address)]),
        Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["row"] = JsonSerializer.SerializeToElement(row),
            ["column"] = JsonSerializer.SerializeToElement(1),
        });

    private static DocumentNode Shape(string id, int order, string text) => new(
        id, NodeKind.Shape, null, order, ContentLayer.Body, new TextNodeContent(text),
        Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["shape_role"] = JsonSerializer.SerializeToElement("body"),
        });
}
