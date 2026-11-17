using System.Text.RegularExpressions;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

public sealed class ReadableHtmlTableSpanTests
{
    private const string Hint = "<!-- docredock:table-spans ";

    private static string Html(string markdown) => ReadableHtmlWriter.Write(markdown, null).Html;

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static DocumentGraph MergedTableGraph() => new(DocumentGraph.CurrentSchemaVersion, "doc-spans", DocumentFormatKind.Docx,
    [
        new DocumentPartition("document", 0,
        [
            new DocumentNode("table", NodeKind.Table, null, 0, ContentLayer.Body, new TableNodeContent(
            [
                [new TableCell("Group", ColSpan: 2), new TableCell("C"), new TableCell("D")],
                [new TableCell("V", RowSpan: 2), new TableCell("b1"), new TableCell("P", RowSpan: 2, ColSpan: 2)],
                [new TableCell(string.Empty, RowSpan: 0), new TableCell("b2"), new TableCell(string.Empty, RowSpan: 0, ColSpan: 2)],
                [new TableCell("Wide", ColSpan: 2), new TableCell("e"), new TableCell("f")],
            ])),
        ]),
    ]);

    [Fact]
    public void Hint_is_off_by_default_and_markdown_is_unchanged()
    {
        var markdown = new ReadableMarkdownSerializer().Serialize(MergedTableGraph());
        Assert.DoesNotContain("docredock:table-spans", markdown, StringComparison.Ordinal);
        var explicitOff = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(EmitTableSpanHints: false)).Serialize(MergedTableGraph());
        Assert.Equal(markdown, explicitOff);
    }

    [Fact]
    public void Hint_lists_vertical_horizontal_and_2x2_merges_directly_above_the_header()
    {
        var on = Lf(new ReadableMarkdownSerializer(new ReadableMarkdownOptions(EmitTableSpanHints: true)).Serialize(MergedTableGraph()));
        var off = Lf(new ReadableMarkdownSerializer().Serialize(MergedTableGraph()));
        Assert.Contains("<!-- docredock:table-spans 0,0,1,2;1,0,2,1;1,2,2,2;3,0,1,2 -->\n| Group |", on, StringComparison.Ordinal);
        Assert.Equal(off, Regex.Replace(on, "<!-- docredock:table-spans [^\n]*-->\n", string.Empty));
    }

    [Fact]
    public void Table_without_merges_gets_no_hint()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc-plain", DocumentFormatKind.Docx,
        [
            new DocumentPartition("document", 0,
            [
                new DocumentNode("table", NodeKind.Table, null, 0, ContentLayer.Body, new TableNodeContent(
                [
                    [new TableCell("A"), new TableCell("B")],
                    [new TableCell("1"), new TableCell("2")],
                ])),
            ]),
        ]);
        var markdown = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(EmitTableSpanHints: true)).Serialize(graph);
        Assert.DoesNotContain("docredock", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Serializer_to_writer_carries_the_real_merge_structure()
    {
        foreach (var expand in new[] { false, true })
        {
            var markdown = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(ExpandMergedCells: expand, EmitTableSpanHints: true)).Serialize(MergedTableGraph());
            var html = Html(markdown);
            Assert.DoesNotContain("docredock:table-spans", html, StringComparison.Ordinal);
            Assert.Contains("<th scope=\"colgroup\" colspan=\"2\">Group</th><th scope=\"col\">C</th><th scope=\"col\">D</th></tr></thead>", html, StringComparison.Ordinal);
            Assert.Contains("<tr><td rowspan=\"2\">V</td><td>b1</td><td rowspan=\"2\" colspan=\"2\">P</td></tr>", html, StringComparison.Ordinal);
            Assert.Contains("<tr><td>b2</td></tr>", html, StringComparison.Ordinal);
            Assert.Contains("<tr><td colspan=\"2\">Wide</td><td>e</td><td>f</td></tr>", html, StringComparison.Ordinal);
        }
    }

    private const string Table = "| H1 | H2 | H3 |\n| --- | --- | --- |\n| a | b | c |\n| d | e | f |\n";

    [Fact]
    public void Valid_hint_sets_spans_and_drops_covered_cells_whatever_they_hold()
    {
        var html = Html(Hint + "0,0,1,2;1,2,2,1 -->\n| H1 | H2 | H3 |\n| --- | --- | --- |\n| a | b | c |\n| d | e | copy |\n");
        Assert.Contains("<th scope=\"colgroup\" colspan=\"2\">H1</th><th scope=\"col\">H3</th>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">H2<", html, StringComparison.Ordinal);
        Assert.Contains("<tr><td>a</td><td>b</td><td rowspan=\"2\">c</td></tr>", html, StringComparison.Ordinal);
        Assert.Contains("<tr><td>d</td><td>e</td></tr>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("copy", html, StringComparison.Ordinal);
        Assert.DoesNotContain("docredock", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0,0,1,2;0,1,1,2")]
    [InlineData("1,0,2,1;2,0,1,2")]
    [InlineData("1,2,1,2")]
    [InlineData("2,0,2,1")]
    [InlineData("5,0,1,2")]
    [InlineData("a,0,1,2")]
    [InlineData("1,0,1.5,2")]
    [InlineData("-1,0,1,2")]
    [InlineData("0,0,2,1")]
    [InlineData("1,0,0,2")]
    [InlineData("1,0,1,2;")]
    [InlineData("0,0,1,2;9,9,1,2")]
    [InlineData("")]
    public void Invalid_hint_leaves_the_whole_table_unspanned(string spans)
    {
        var hinted = Html(Hint + spans + " -->\n" + Table);
        Assert.DoesNotContain("rowspan", hinted, StringComparison.Ordinal);
        Assert.DoesNotContain("colspan", hinted, StringComparison.Ordinal);
        Assert.DoesNotContain("docredock", hinted, StringComparison.Ordinal);
        Assert.Contains("<th scope=\"col\">H2</th>", hinted, StringComparison.Ordinal);
        Assert.Contains("<td>e</td>", hinted, StringComparison.Ordinal);
        Assert.Equal(Html(Table), hinted);
    }

    [Fact]
    public void Hint_not_followed_by_a_table_is_dropped_silently()
    {
        var html = Html(Hint + "0,0,1,2 -->\n\n| H1 | H2 | H3 |\n| --- | --- | --- |\n| a | b | c |\n");
        Assert.DoesNotContain("docredock", html, StringComparison.Ordinal);
        Assert.DoesNotContain("colspan", html, StringComparison.Ordinal);
        var text = Html("before\n\n" + Hint + "0,0,1,2 -->\nafter\n");
        Assert.DoesNotContain("docredock", text, StringComparison.Ordinal);
        Assert.Contains("after", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Hint_like_comment_inside_a_code_block_stays_code()
    {
        var html = Html("```\n" + Hint + "0,0,1,2 -->\n" + Table + "```\n");
        Assert.Contains("<pre", html, StringComparison.Ordinal);
        Assert.Contains("docredock:table-spans 0,0,1,2", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", html, StringComparison.Ordinal);
        Assert.DoesNotContain("colspan", html, StringComparison.Ordinal);
    }
}
