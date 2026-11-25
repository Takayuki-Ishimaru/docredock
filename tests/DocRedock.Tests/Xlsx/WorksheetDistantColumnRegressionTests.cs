using System.Text.RegularExpressions;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using DocRedock.Render;
using DocRedock.Tests.Markdown;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using GfmTable = Markdig.Extensions.Tables.Table;
using GfmRow = Markdig.Extensions.Tables.TableRow;

namespace DocRedock.Tests.Xlsx;

public sealed class WorksheetDistantColumnRegressionTests
{
    private const string Left = "Process description";
    private const string Right = "Unrelated note";

    private static string Cell(string reference, string text, bool bold = false) =>
        $"<c r=\"{reference}\"{(bold ? " s=\"1\"" : "")} t=\"inlineStr\"><is><t>{text}</t></is></c>";

    private static string Row(int number, params (string Reference, string Text, bool Bold)[] cells) =>
        $"<row r=\"{number}\">{string.Concat(cells.Select(cell => Cell(cell.Reference, cell.Text, cell.Bold)))}</row>";

    private static string Sheet(params string[] rows) =>
        $"<sheetData>{string.Concat(rows)}</sheetData>";

    private static string Markdown(DocumentGraph graph) => new ReadableMarkdownSerializer(null).Serialize(graph);

    private static string Text(Block block) => block is LeafBlock leaf ? InlineText(leaf.Inline?.FirstChild) :
        block is ContainerBlock container ? string.Concat(container.Select(Text)) : string.Empty;

    private static string InlineText(Inline? first)
    {
        var text = new System.Text.StringBuilder();
        for (var inline = first; inline is not null; inline = inline.NextSibling)
            text.Append(inline switch
            {
                LiteralInline literal => literal.Content.ToString(),
                HtmlEntityInline entity => entity.Transcoded.ToString(),
                CodeInline code => code.Content,
                ContainerInline nested => InlineText(nested.FirstChild),
                _ => string.Empty,
            });
        return text.ToString();
    }

    [Fact]
    public void Distant_single_row_note_never_joins_the_left_prose_block()
    {
        var graph = WorkbookPresentationTests.Extract(Sheet(
            Row(3, ("A3", Left, false), ("X3", Right, false)))).Graph;
        var markdown = Markdown(graph);
        Assert.Contains(Left, markdown, StringComparison.Ordinal);
        Assert.Contains(Right, markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkdownStructure.Parse(markdown).Descendants<GfmTable>(), table =>
            Text(table).Contains(Left, StringComparison.Ordinal) && Text(table).Contains(Right, StringComparison.Ordinal));
        foreach (var block in MarkdownStructure.Parse(markdown).Descendants<Block>())
            if (block is ParagraphBlock or ListItemBlock)
                Assert.False(Text(block).Contains(Left, StringComparison.Ordinal) &&
                    Text(block).Contains(Right, StringComparison.Ordinal));
    }

    [Fact]
    public void Native_html_keeps_distant_note_in_its_own_typed_cell()
    {
        var graph = WorkbookPresentationTests.Extract(Sheet(
            Row(3, ("A3", Left, false), ("X3", Right, false)))).Graph;
        var html = ReadableHtmlWriter.WriteDocument(ReadableProjectionBuilder.Build(graph).Document).Html;
        Assert.Contains(Left, html, StringComparison.Ordinal);
        Assert.Contains(Right, html, StringComparison.Ordinal);
        Assert.Contains("data-source-column=\"24\"", html, StringComparison.Ordinal);
        foreach (Match cell in Regex.Matches(html, "<t[dh]\\b[^>]*>(?:(?!</t[dh]>).)*</t[dh]>",
                     RegexOptions.Singleline))
            Assert.False(cell.Value.Contains(Left, StringComparison.Ordinal) &&
                cell.Value.Contains(Right, StringComparison.Ordinal));
    }

    [Fact]
    public void Merged_bold_heading_on_same_row_precedes_merged_body()
    {
        var graph = WorkbookPresentationTests.Extract(Sheet(
            Row(3, ("A3", "1. Search", true), ("D3", "Validate search request.", false))) +
            "<mergeCells count=\"2\"><mergeCell ref=\"A3:C3\"/><mergeCell ref=\"D3:H3\"/></mergeCells>").Graph;
        var markdown = Markdown(graph);
        var document = MarkdownStructure.Parse(markdown);
        var heading = Assert.Single(document.Descendants<HeadingBlock>(), block => Text(block).Contains("Search", StringComparison.Ordinal));
        var body = Assert.Single(document.Descendants<ParagraphBlock>());
        Assert.Contains("Search", Text(heading), StringComparison.Ordinal);
        Assert.Contains("Validate search request.", Text(body), StringComparison.Ordinal);
        Assert.True(heading.Span.Start < body.Span.Start);
    }

    [Fact]
    public void Native_html_does_not_promote_body_beside_a_heading_to_a_column_header()
    {
        var graph = WorkbookPresentationTests.Extract(Sheet(
            Row(3, ("A3", "1. Search", true), ("D3", "Validate search request.", false))) +
            "<mergeCells count=\"2\"><mergeCell ref=\"A3:C3\"/><mergeCell ref=\"D3:H3\"/></mergeCells>").Graph;
        var html = ReadableHtmlWriter.WriteDocument(ReadableProjectionBuilder.Build(graph).Document).Html;

        var bodyCell = Regex.Match(html, "<(?<tag>td|th)\\b[^>]*data-source-column=\"4\"[^>]*>");
        Assert.True(bodyCell.Success, html);
        Assert.Equal("td", bodyCell.Groups["tag"].Value);
        Assert.Contains("Validate search request.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Widely_spaced_revision_header_stays_one_table_with_ordered_tuples()
    {
        var graph = WorkbookPresentationTests.Extract(Sheet(
            Row(1, ("A1", "Date", true), ("E1", "Version", true), ("I1", "Author", true), ("M1", "Change", true)),
            Row(2, ("A2", "2026-01-04", false), ("E2", "1.2", false), ("I2", "Tanaka", false), ("M2", "Initial", false)),
            Row(3, ("A3", "2026-02-09", false), ("E3", "1.3", false), ("I3", "Sato", false), ("M3", "Fix", false)))).Graph;
        var table = Assert.Single(MarkdownStructure.Parse(Markdown(graph)).Descendants<GfmTable>());
        string[][] rows =
        [
            ["Date", "Version", "Author", "Change"],
            ["2026-01-04", "1.2", "Tanaka", "Initial"],
            ["2026-02-09", "1.3", "Sato", "Fix"],
        ];
        Assert.Equal(rows.Length, table.Count);
        for (var row = 0; row < rows.Length; row++)
        {
            var cells = ((GfmRow)table[row]).Select(cell => Text(cell)).ToArray();
            Assert.Equal(rows[row], cells);
        }
    }

    [Fact]
    public void Adjacent_label_value_pair_stays_one_row()
    {
        var graph = WorkbookPresentationTests.Extract(Sheet(
            Row(1, ("A1", "Name", true), ("B1", "Value", true)),
            Row(2, ("A2", "Owner", false), ("B2", "Ops", false)))).Graph;
        var table = Assert.Single(MarkdownStructure.Parse(Markdown(graph)).Descendants<GfmTable>());
        Assert.Equal(["Name", "Value"], ((GfmRow)table[0]).Select(Text).ToArray());
        Assert.Equal(["Owner", "Ops"], ((GfmRow)table[1]).Select(Text).ToArray());
    }

    [Fact]
    public void Widely_spaced_revision_table_keeps_a_blank_author_in_its_original_column()
    {
        var graph = WorkbookPresentationTests.Extract(Sheet(
            Row(1, ("A1", "Date", true), ("E1", "Version", true), ("I1", "Author", true), ("M1", "Change", true)),
            Row(2, ("A2", "2026-01-04", false), ("E2", "1.2", false), ("M2", "Initial", false)),
            Row(3, ("A3", "2026-02-09", false), ("E3", "1.3", false), ("I3", "Sato", false), ("M3", "Fix", false)))).Graph;
        var table = Assert.Single(MarkdownStructure.Parse(Markdown(graph)).Descendants<GfmTable>());

        Assert.Equal(3, table.Count);
        Assert.Equal(["Date", "Version", "Author", "Change"], ((GfmRow)table[0]).Select(Text).ToArray());
        Assert.Equal(["2026-01-04", "1.2", "", "Initial"], ((GfmRow)table[1]).Select(Text).ToArray());
        Assert.Equal(["2026-02-09", "1.3", "Sato", "Fix"], ((GfmRow)table[2]).Select(Text).ToArray());
    }

    [Fact]
    public void Single_blank_spacer_column_inside_a_table_stays_one_table()
    {
        var graph = WorkbookPresentationTests.Extract(Sheet(
            Row(1, ("A1", "Item", true), ("C1", "Quantity", true)),
            Row(2, ("A2", "Bolt", false), ("C2", "12", false)),
            Row(3, ("A3", "Nut", false), ("C3", "48", false)))).Graph;
        var table = Assert.Single(MarkdownStructure.Parse(Markdown(graph)).Descendants<GfmTable>());
        Assert.Equal(3, table.Count);
        Assert.Equal(["Item", "Quantity"], ((GfmRow)table[0]).Select(Text).ToArray());
        Assert.Equal(["Bolt", "12"], ((GfmRow)table[1]).Select(Text).ToArray());
    }

    [Fact]
    public void Repeated_headings_keep_heading_body_order_beside_right_table()
    {
        var graph = WorkbookPresentationTests.Extract(Sheet(
            Row(1, ("A1", "1. Section One", true), ("H1", "Key", true), ("I1", "Amount", true)),
            Row(2, ("A2", "First body text.", false), ("H2", "Fee", false), ("I2", "100", false)),
            Row(3, ("A3", "2. Section Two", true), ("H3", "Tax", false), ("I3", "15", false)),
            Row(4, ("A4", "Second body text.", false), ("H4", "Total", false), ("I4", "115", false)))).Graph;
        var markdown = Markdown(graph);
        MarkdownStructure.AssertInOrder(MarkdownStructure.DisplayedText(MarkdownStructure.Parse(markdown)),
            "Section One", "First body text.", "Section Two", "Second body text.");
        var table = Assert.Single(MarkdownStructure.Parse(markdown).Descendants<GfmTable>());
        Assert.Equal(4, table.Count);
        Assert.Equal(["Key", "Amount"], ((GfmRow)table[0]).Select(Text).ToArray());
        Assert.Equal(["Fee", "100"], ((GfmRow)table[1]).Select(Text).ToArray());
        Assert.Equal(["Tax", "15"], ((GfmRow)table[2]).Select(Text).ToArray());
        Assert.Equal(["Total", "115"], ((GfmRow)table[3]).Select(Text).ToArray());
    }

    [Fact]
    public void Distant_note_does_not_inherit_a_sparse_table_schema_from_an_earlier_section()
    {
        var graph = WorkbookPresentationTests.Extract(Sheet(
            Row(2, ("A2", "項目", true), ("J2", "内容", true)),
            Row(3, ("A3", "モード", false), ("J3", "検証", false)),
            Row(30, ("A30", Left, false), ("J30", Right, false)))).Graph;
        var document = MarkdownStructure.Parse(Markdown(graph));

        foreach (var block in document.Descendants<Block>())
            if (block is ParagraphBlock or ListItemBlock or GfmTable)
                Assert.False(Text(block).Contains(Left, StringComparison.Ordinal) &&
                    Text(block).Contains(Right, StringComparison.Ordinal));
        var table = Assert.Single(document.Descendants<GfmTable>());
        Assert.Equal(2, table.Count);
        Assert.Equal(["モード", "検証"], ((GfmRow)table[1]).Select(Text).ToArray());
    }

    [Fact]
    public void Heading_closes_the_old_table_before_indented_body_rows()
    {
        var graph = WorkbookPresentationTests.Extract(Sheet(
            Row(2, ("B2", "項目", true), ("C2", "内容", true)),
            Row(3, ("B3", "旧仕様", false), ("C3", "旧本文", false)),
            Row(4, ("A4", "1. 新しい節", false)),
            Row(5, ("B5", "新仕様", false), ("C5", "新本文", false)))).Graph;
        var document = MarkdownStructure.Parse(Markdown(graph));

        MarkdownStructure.AssertInOrder(MarkdownStructure.DisplayedText(document), "旧本文", "新しい節", "新仕様", "新本文");
        var table = Assert.Single(document.Descendants<GfmTable>());
        Assert.Equal(2, table.Count);
        Assert.DoesNotContain("新本文", Text(table), StringComparison.Ordinal);
    }
}
