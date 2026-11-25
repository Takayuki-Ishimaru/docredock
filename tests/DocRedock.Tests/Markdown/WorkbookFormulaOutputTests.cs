using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DocRedock.Tests.Markdown;

public sealed class WorkbookFormulaOutputTests
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseMathematics().Build();

    [Theory]
    [InlineData("ROW()-$BH$18")]
    [InlineData("IF(A1=\"$x$\",1,0)")]
    [InlineData("IF(A1=\"a`b|c\\d\",1,0)")]
    [InlineData("IF(A1=\"a\\|b\",1,0)")]
    [InlineData("IF(A1=\"``\",1,0)")]
    public void Generated_formula_code_is_not_escaped_as_literal_text(string expression)
    {
        foreach (var record in new[] { false, true })
        {
            var serializer = new ReadableMarkdownSerializer(new(ShowFormulas: true)) { RecordWorkbookLayout = record };
            var markdown = serializer.Serialize(Graph(expression, "7", true));
            var document = global::Markdig.Markdown.Parse(markdown, Pipeline);
            var code = Assert.Single(document.Descendants<CodeInline>());
            Assert.Equal("=" + expression, code.Content);
            Assert.Empty(document.Descendants<MathInline>());
            Assert.Empty(document.Descendants<MathBlock>());
            Assert.Contains("→ 7", MarkdownStructure.DisplayedText(document), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("ROW()")]
    [InlineData("")]
    [InlineData(null)]
    public void Missing_result_is_visible_for_master_and_shared_formulas(string? expression)
    {
        foreach (var show in new[] { false, true })
        {
            var markdown = new ReadableMarkdownSerializer(new(ShowFormulas: show)).Serialize(Graph(expression, "", false));
            Assert.Contains("保存済み計算値なし", markdown, StringComparison.Ordinal);
            Assert.Contains("entry", markdown, StringComparison.Ordinal);
            if (!show) Assert.DoesNotContain("ROW()", markdown, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("IF(TRUE,\"\",\"\")")]
    [InlineData("")]
    [InlineData(null)]
    public void Cached_empty_string_is_a_saved_result(string? expression)
    {
        var markdown = new ReadableMarkdownSerializer().Serialize(Graph(expression, "", true));
        Assert.DoesNotContain("保存済み計算値なし", markdown, StringComparison.Ordinal);
        Assert.Contains("| entry |  |", markdown, StringComparison.Ordinal);
    }

    private static DocumentGraph Graph(string? expression, string value, bool cache)
    {
        DocumentNode Cell(string id, int row, int column, string text, bool bold = false, bool formula = false)
        {
            var ext = new Dictionary<string, JsonElement>
            {
                ["row"] = JsonSerializer.SerializeToElement(row), ["column"] = JsonSerializer.SerializeToElement(column),
                ["is_bold"] = JsonSerializer.SerializeToElement(bold)
            };
            if (formula)
            {
                ext["is_formula"] = JsonSerializer.SerializeToElement(true);
                ext["has_cached_value"] = JsonSerializer.SerializeToElement(cache);
                if (expression is not null) ext["formula"] = JsonSerializer.SerializeToElement(expression);
            }
            return new(id, NodeKind.Cell, null, row * 10 + column, ContentLayer.Body, new TextNodeContent(text), Extensions: ext);
        }
        return new(DocumentGraph.CurrentSchemaVersion, "formula", DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-Data", 0, [Cell("A1", 1, 1, "項目", true), Cell("B1", 1, 2, "値", true),
                Cell("A2", 2, 1, "entry"), Cell("B2", 2, 2, value, formula: true)])]);
    }
}
