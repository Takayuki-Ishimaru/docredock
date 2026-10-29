using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using Markdig.Extensions.Tables;

namespace DocRedock.Tests.Markdown;

public sealed class V039EvaluationRegressionTests
{
    [Fact]
    public void Merge_expansion_repeats_the_original_value_without_mutating_the_graph()
    {
        var graph = MergedGraph();
        var plain = new ReadableMarkdownSerializer().Serialize(graph);
        var expanded = new ReadableMarkdownSerializer(new(ExpandMergedCells: true)).Serialize(graph);
        Assert.Contains("| 50万円以上 |  |", plain);
        Assert.Contains("| 50万円以上 | 事前申請が必要 |", expanded);
        Assert.Equal("", ((TableNodeContent)graph.Nodes.Single().Content).Rows[2][1].Text);
        var parts = AiPackageContentBuilder.Build(graph, new(), "expenses.docx");
        Assert.Contains("| 50万円以上 | 事前申請が必要 |", Assert.Single(parts).Markdown);
    }

    [Fact]
    public void Worksheet_units_remain_local_and_are_not_the_workbook_title()
    {
        DocumentNode Cell(string id, int row, string text) => new(id, NodeKind.Cell, null, row, ContentLayer.Body,
            new TextNodeContent(text), Extensions: new Dictionary<string, JsonElement>
            {
                ["row"] = JsonSerializer.SerializeToElement(row), ["column"] = JsonSerializer.SerializeToElement(1)
            });
        var graph = new DocumentGraph("1.1", "book", DocumentFormatKind.Xlsx,
            [new("sheet:Summary", 0, [Cell("title", 1, "予算（単位：千円）"), Cell("budget", 2, "123")]),
             new("sheet:Details", 1, [Cell("detail", 1, "明細"), Cell("amount", 2, "456")])]);
        var serializer = new ReadableMarkdownSerializer(new(Title: "Budget workbook"));
        var text = serializer.Serialize(graph);
        Assert.StartsWith("# Budget workbook", text);
        Assert.Contains("予算（単位：千円）", text);
        var parts = AiPackageContentBuilder.Build(graph, new(), "budget.xlsx");
        Assert.All(parts, part => Assert.StartsWith("# budget", part.Markdown));
        var details = parts.Last().Markdown;
        Assert.DoesNotContain("単位", details);
        Assert.Contains("456", details);
    }

    [Theory]
    [InlineData("2025", "2026", true)]
    [InlineData("東京", "大阪", false)]
    public void Chart_is_one_real_Gfm_table_and_only_time_categories_have_trends(string first, string last, bool trend)
    {
        var series = new[]
        {
            new { Name = "導入費", Categories = new[] { first, last }, Values = new[] { "10", "20" } },
            new { Name = "運用費", Categories = new[] { first, last }, Values = new[] { "30", "25" } }
        };
        var node = new DocumentNode("chart", NodeKind.Chart, null, 0, ContentLayer.Body, new TextNodeContent("chart"),
            Extensions: new Dictionary<string, JsonElement> { ["chart_series"] = JsonSerializer.SerializeToElement(series) });
        var text = new ReadableMarkdownSerializer().Serialize(Graph(node));
        Assert.Single(MarkdownStructure.Parse(text).OfType<Table>());
        Assert.Contains("| カテゴリ | 導入費 | 運用費 |", text);
        Assert.Contains($"| {first} | 10 | 30 |", text);
        Assert.Equal(trend, text.Contains("増加", StringComparison.Ordinal));
    }

    [Fact]
    public void Chart_aligns_different_categories_by_label_and_retains_repeated_categories()
    {
        var series = new[]
        {
            new { Name = "A", Categories = new[] { "West", "East", "West" }, Values = new[] { "1", "2", "3" } },
            new { Name = "B", Categories = new[] { "East", "West" }, Values = new[] { "4", "5" } }
        };
        var node = new DocumentNode("chart", NodeKind.Chart, null, 0, ContentLayer.Body, new TextNodeContent("chart"),
            Extensions: new Dictionary<string, JsonElement> { ["chart_series"] = JsonSerializer.SerializeToElement(series) });
        var text = new ReadableMarkdownSerializer(new(Language: "en")).Serialize(Graph(node));
        Assert.Contains("| West | 1 | 5 |", text);
        Assert.Contains("| East | 2 | 4 |", text);
        Assert.Contains("| West | 3 |  |", text);
    }

    [Fact]
    public void Missing_chart_category_does_not_align_with_a_named_numeric_category()
    {
        var series = new[]
        {
            new { Name = "A", Categories = new[] { "East" }, Values = new[] { "10", "20" } },
            new { Name = "B", Categories = new[] { "2" }, Values = new[] { "99" } }
        };
        var node = new DocumentNode("chart", NodeKind.Chart, null, 0, ContentLayer.Body, new TextNodeContent("chart"),
            Extensions: new Dictionary<string, JsonElement> { ["chart_series"] = JsonSerializer.SerializeToElement(series) });
        var text = new ReadableMarkdownSerializer(new(Language: "en")).Serialize(Graph(node));
        Assert.Contains("| 2 | 20 |  |", text);
        Assert.Contains("| 2 |  | 99 |", text);
        Assert.DoesNotContain("| 2 | 20 | 99 |", text);
    }

    [Fact]
    public void Explicit_English_localizes_the_generated_chart_summary()
    {
        var node = new DocumentNode("chart", NodeKind.Chart, null, 0, ContentLayer.Body, new TextNodeContent("chart"),
            Extensions: new Dictionary<string, JsonElement>
            {
                ["chart_series"] = JsonSerializer.SerializeToElement(new[] { new { Name = "Sales", Categories = new[] { "2025", "2026" }, Values = new[] { "10", "20" } } })
            });
        var text = new ReadableMarkdownSerializer(new(Language: "en")).Serialize(Graph(node));
        Assert.Contains("increased from 10 (2025) to 20 (2026)", text);
        Assert.DoesNotContain("増加", text);
    }

    [Fact]
    public void Generated_title_language_can_be_inferred_or_overridden()
    {
        var graph = Graph(new("p", NodeKind.Paragraph, null, 0, ContentLayer.Body, new TextNodeContent("English prose")))
            with { Format = DocumentFormatKind.Pdf };
        Assert.StartsWith("# Document", new ReadableMarkdownSerializer().Serialize(graph));
        Assert.StartsWith("# ドキュメント", new ReadableMarkdownSerializer(new(Language: "ja")).Serialize(graph));
        Assert.Throws<ArgumentException>(() => new ReadableMarkdownSerializer(new(Language: "fr")).Serialize(graph));
    }

    [Fact]
    public void Complete_output_labels_hidden_word_text_and_only_actually_hidden_slides()
    {
        var hiddenWord = Graph(new("hidden", NodeKind.Quote, null, 0, ContentLayer.Hidden, new TextNodeContent("internal note")));
        Assert.Contains("**非表示文字**: internal note", new ReadableMarkdownSerializer(new(ContentPolicy: "complete")).Serialize(hiddenWord));
        DocumentNode Slide(string id, bool hiddenSlide) => new(id, NodeKind.Shape, null, 0, ContentLayer.Hidden,
            new TextNodeContent("Text"), Extensions: new Dictionary<string, JsonElement>
            { ["hidden_slide"] = JsonSerializer.SerializeToElement(hiddenSlide) });
        var slides = new DocumentGraph("1.1", "presentation", DocumentFormatKind.Pptx,
            [new("slide1", 0, [Slide("object", false)]), new("slide2", 1, [Slide("slide", true)])]);
        var text = new ReadableMarkdownSerializer(new(ContentPolicy: "complete")).Serialize(slides);
        Assert.Contains("## スライド 1\n", text);
        Assert.DoesNotContain("## スライド 1（非表示）", text);
        Assert.Contains("## スライド 2（非表示）", text);
    }

    private static DocumentGraph MergedGraph() => Graph(new("table", NodeKind.Table, null, 0, ContentLayer.Body,
        new TableNodeContent([
            [new("金額"), new("備考")],
            [new("5万円以上50万円未満"), new("事前申請が必要", RowSpan: 2)],
            [new("50万円以上"), new("", RowSpan: 0)]
        ])));

    private static DocumentGraph Graph(DocumentNode node) => new("1.1", "test", DocumentFormatKind.Docx, [new("document", 0, [node])]);
}
