using DocRedock.Markdown;
using DocRedock.Tests.Xlsx;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DocRedock.Tests.Markdown;

public sealed class PortableWorkbookMarkdownTests
{
    [Fact]
    public void Portable_contents_and_explicit_hidden_row_markers_are_retained()
    {
        var graph = WorkbookPresentationTests.Extract("""
            <sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Label</t></is></c><c r="B1" t="inlineStr"><is><t>Value</t></is></c></row>
            <row r="2" hidden="1"><c r="A2" t="inlineStr"><is><t>HiddenSourceValue</t></is></c><c r="B2"><v>7</v></c></row></sheetData>
            """).Graph;
        var markdown = new ReadableMarkdownSerializer(new(PortableMarkdown: true, ContentPolicy: "complete", Language: "en", IncludeTableOfContents: true)).Serialize(graph);
        Assert.Contains("## Contents", markdown, StringComparison.Ordinal);
        Assert.Contains("- Sheet, O'Brien", markdown, StringComparison.Ordinal);
        Assert.Contains("(hidden) HiddenSourceValue", MarkdownStructure.DisplayedText(MarkdownStructure.Parse(markdown)), StringComparison.Ordinal);
    }

    [Fact]
    public void Portable_records_distinguish_a_missing_formula_cache_from_a_saved_empty_result()
    {
        var graph = WorkbookPresentationTests.Extract("""
            <sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Field</t></is></c><c r="B1" t="inlineStr"><is><t>Value</t></is></c></row>
            <row r="2"><c r="A2" t="inlineStr"><is><t>Missing</t></is></c><c r="B2"><f>1+2</f></c></row>
            <row r="3"><c r="A3" t="inlineStr"><is><t>Empty</t></is></c><c r="B3" t="str"><f>IF(1,"","")</f><v/></c></row></sheetData>
            """).Graph;
        var markdown = new ReadableMarkdownSerializer(new(PortableMarkdown: true)).Serialize(graph);
        Assert.Equal(1, markdown.Split("保存済み計算値なし", StringSplitOptions.None).Length - 1);
        Assert.Contains("Missing", markdown, StringComparison.Ordinal);
        Assert.Contains("Empty", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Portable_records_keep_literal_text_and_multiline_cells_without_raw_html()
    {
        const string value = "line one\n    <tag> | $cash$ `literal`\n```\nlast line";
        var graph = WorkbookPresentationTests.Extract("""
            <sheetData><row r="1"><c r="A1" s="1" t="inlineStr"><is><t>Field</t></is></c><c r="B1" s="1" t="inlineStr"><is><t>Example</t></is></c></row>
            <row r="2"><c r="A2" t="inlineStr"><is><t>sample</t></is></c><c r="B2" t="inlineStr"><is><t xml:space="preserve">line one
                &lt;tag&gt; | $cash$ `literal`
            ```
            last line</t></is></c></row></sheetData>
            """).Graph;
        var markdown = new ReadableMarkdownSerializer(new(PortableMarkdown: true, IncludeTableOfContents: true,
            IncludeSourceReferences: true, SourceName: "Book.xlsx")).Serialize(graph);
        var parsed = MarkdownStructure.Parse(markdown);
        Assert.Empty(((MarkdownObject)parsed).Descendants<HtmlBlock>());
        Assert.Empty(((MarkdownObject)parsed).Descendants<HtmlInline>());
        var code = Assert.Single(((MarkdownObject)parsed).Descendants<FencedCodeBlock>());
        Assert.Equal(value, code.Lines.ToString().TrimEnd('\r', '\n'));
        Assert.Contains("Book.xlsx", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Commented_json_and_CR_only_code_preserve_indentation_in_default_markdown()
    {
        foreach (var value in new[] { "{\n  // note\n  \"value\": ...\n}", "{\r  \"value\": 1\r}" })
        {
            var xmlValue = System.Security.SecurityElement.Escape(value)!.Replace("\r", "&#13;", StringComparison.Ordinal);
            var graph = WorkbookPresentationTests.Extract($"""
                <sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>項目</t></is></c><c r="B1" t="inlineStr"><is><t>説明</t></is></c></row>
                <row r="2"><c r="A2" t="inlineStr"><is><t>Example</t></is></c><c r="B2" t="inlineStr"><is><t xml:space="preserve">{xmlValue}</t></is></c></row></sheetData>
                """).Graph;
            var document = MarkdownStructure.Parse(new ReadableMarkdownSerializer().Serialize(graph));
            var code = Assert.Single(((MarkdownObject)document).Descendants<FencedCodeBlock>());
            Assert.Equal(value.Replace('\r', '\n'), code.Lines.ToString().TrimEnd('\r', '\n'));
        }
    }

    [Fact]
    public void Literal_backticks_in_a_source_heading_are_not_discarded()
    {
        var graph = WorkbookPresentationTests.Extract("""
            <sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>1. `Literal` section</t></is></c></row></sheetData>
            """).Graph;
        var markdown = new ReadableMarkdownSerializer().Serialize(graph);
        Assert.Contains("`Literal`", MarkdownStructure.DisplayedText(MarkdownStructure.Parse(markdown)), StringComparison.Ordinal);
    }
}
