using System.Security;
using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using DocRedock.Render;
using DocRedock.Tests.Xlsx;

namespace DocRedock.Tests.Render;

public sealed class WorksheetFeedbackStructureTests
{
    private static ReadableTable Table(DocumentGraph graph, string policy = "visible") =>
        Assert.Single(Assert.Single(ReadableProjectionBuilder.Build(graph, new(ContentPolicy: policy)).Document.Blocks.OfType<ReadableSection>()).Blocks.OfType<ReadableTable>());

    [Fact]
    public void Blank_columns_rows_and_merged_condition_scope_remain_in_original_coordinates()
    {
        var graph = WorkbookPresentationTests.Extract("""
            <sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Condition</t></is></c><c r="C1" t="inlineStr"><is><t>Field</t></is></c></row>
            <row r="2"><c r="A2" t="inlineStr"><is><t>Applies to three rows</t></is></c><c r="C2" t="inlineStr"><is><t>Item A</t></is></c></row>
            <row r="4"><c r="C4" t="inlineStr"><is><t>Item C</t></is></c></row></sheetData>
            <mergeCells count="1"><mergeCell ref="A2:A4"/></mergeCells>
            """).Graph;
        var table = Table(graph);
        Assert.Equal(new[] { 1, 2, 3 }, table.Columns.Select(c => c.SourceIndex));
        Assert.Equal(new int?[] { 1, 2, 3, 4 }, table.Rows.Select(r => r.SourceRow));
        var condition = Assert.Single(table.Rows[1].Cells, c => c.SourceColumn == 1);
        Assert.Equal(3, condition.RowSpan);
        Assert.Equal("A2:A4", condition.Origin!.Range);
        Assert.All(table.Rows, r => Assert.Equal("", Assert.Single(r.Cells, c => c.SourceColumn == 2).Content.PlainText));
        Assert.Equal("Item C", Assert.Single(table.Rows[3].Cells, c => c.SourceColumn == 3).Content.PlainText);
    }

    [Theory]
    [InlineData("visible", 2)]
    [InlineData("complete", 3)]
    public void Preserving_blank_rows_does_not_restore_hidden_rows_in_visible_output(string policy, int count)
    {
        var graph = WorkbookPresentationTests.Extract("""
            <sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>First</t></is></c></row>
            <row r="2" hidden="1"><c r="A2" t="inlineStr"><is><t>Private row</t></is></c></row>
            <row r="3"><c r="A3" t="inlineStr"><is><t>Last</t></is></c></row></sheetData>
            """).Graph;
        Assert.Equal(count, Table(graph, policy).Rows.Count);
    }

    [Fact]
    public void Structured_json_keeps_source_whitespace_in_html_and_a_markdown_fence()
    {
        const string example = "{\n  \"request\": {\n    \"ids\": [1, 2]\n  }\n}";
        var graph = WorkbookPresentationTests.Extract($"""
            <sheetData><row r="1"><c r="A1" s="1" t="inlineStr"><is><t>Field</t></is></c><c r="B1" s="1" t="inlineStr"><is><t>Example</t></is></c></row>
            <row r="2"><c r="A2" t="inlineStr"><is><t>request</t></is></c><c r="B2" t="inlineStr"><is><t xml:space="preserve">{SecurityElement.Escape(example)}</t></is></c></row></sheetData>
            """).Graph;
        var table = Table(graph);
        var code = Assert.Single(table.Rows[1].Cells[1].Children.OfType<ReadableCode>());
        Assert.Equal(example, code.Text);
        Assert.Equal("json", code.Language);
        var html = ReadableHtmlWriter.WriteDocument(ReadableProjectionBuilder.Build(graph).Document).Html;
        Assert.Contains("<pre><code class=\"language-json\">", html, StringComparison.Ordinal);
        var markdown = new ReadableMarkdownSerializer(new(TableLayout: MarkdownTableLayout.Records)).Serialize(graph);
        Assert.Contains("```json", markdown, StringComparison.Ordinal);
        Assert.Contains("      \"ids\": [1, 2]", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Sparse_row_compaction_keeps_source_addresses_and_reports_the_layout_change()
    {
        var graph = WorkbookPresentationTests.Extract("""
            <sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>First</t></is></c></row>
            <row r="1048576"><c r="A1048576" t="inlineStr"><is><t>Last</t></is></c></row></sheetData>
            """).Graph;
        var projected = ReadableProjectionBuilder.Build(graph);
        var table = Assert.Single(Assert.Single(projected.Document.Blocks.OfType<ReadableSection>()).Blocks.OfType<ReadableTable>());
        Assert.Equal(new int?[] { 1, 1048576 }, table.Rows.Select(r => r.SourceRow));
        Assert.Contains(projected.Diagnostics, d => d.Code == "XlsxSparseRowsCompacted");
    }

    [Theory]
    [InlineData("visible", false)]
    [InlineData("complete", true)]
    [InlineData("sanitized", false)]
    public void Notes_and_authors_use_the_same_content_policy_in_both_formats(string policy, bool included)
    {
        var graph = WorkbookPresentationTests.Extract("""<sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Value</t></is></c></row></sheetData>""").Graph;
        var note = new DocumentNode("note", NodeKind.Comment, null, 9, ContentLayer.Metadata, new TextNodeContent("PRIVATE_NOTE"),
            Extensions: new Dictionary<string, JsonElement> { ["cell_ref"] = JsonSerializer.SerializeToElement("A1"), ["comment_author"] = JsonSerializer.SerializeToElement("NOTE_AUTHOR") });
        graph = graph with { Partitions = [graph.Partitions[0] with { Nodes = graph.Partitions[0].Nodes.Append(note).ToArray() }] };
        var serializer = new ReadableMarkdownSerializer(new(ContentPolicy: policy));
        var markdown = Markdig.Markdown.ToHtml(serializer.Serialize(graph));
        var native = ReadableProjectionBuilder.Build(graph, new(ContentPolicy: policy));
        var html = ReadableHtmlWriter.WriteDocument(native.Document).Html;
        foreach (var expected in new[] { "PRIVATE_NOTE", "NOTE_AUTHOR" })
        {
            Assert.Equal(included, markdown.Contains(expected, StringComparison.Ordinal));
            Assert.Equal(included, html.Contains(expected, StringComparison.Ordinal));
        }
        if (included) Assert.Contains("data-source-range=\"A1\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_styles_keep_images_legible_and_do_not_invent_alternating_row_colors()
    {
        var html = ReadableHtmlWriter.WriteDocument(new ReadableDocument("Test", "en", DocumentFormatKind.Xlsx, [])).Html;
        Assert.Contains(".direct-document img{background:#fff", html, StringComparison.Ordinal);
        Assert.Contains(".direct-document tbody tr:nth-child(even){background:transparent}", html, StringComparison.Ordinal);
    }
}
