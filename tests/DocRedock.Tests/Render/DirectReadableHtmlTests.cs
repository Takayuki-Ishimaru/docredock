using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

public sealed class DirectReadableHtmlTests
{
    private static DocumentNode Text(string id, string text, int order = 0, ContentLayer layer = ContentLayer.Body) =>
        new(id, NodeKind.Paragraph, null, order, layer, new TextNodeContent(text));

    private static DocumentGraph Graph(DocumentFormatKind format, params DocumentNode[] nodes) =>
        new(DocumentGraph.CurrentSchemaVersion, "direct", format, [new DocumentPartition("part-1", 0, nodes)]);

    [Theory]
    [InlineData(DocumentFormatKind.Docx)]
    [InlineData(DocumentFormatKind.Pptx)]
    [InlineData(DocumentFormatKind.Pdf)]
    public void Source_text_is_not_interpreted_as_Markdown(DocumentFormatKind format)
    {
        const string literal = "# heading **literal** `code` [name](https://example.org) <script>alert(1)</script>";
        var projected = ReadableProjectionBuilder.Build(Graph(format, Text("literal", literal)));
        var html = ReadableHtmlWriter.WriteDocument(projected.Document).Html;
        Assert.Contains("**literal**", html, StringComparison.Ordinal);
        Assert.Contains("`code`", html, StringComparison.Ordinal);
        Assert.Contains("[name](https://example.org)", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<strong>literal</strong>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_runs_and_nested_tables_are_kept_structured()
    {
        var rich = new DocumentNode("runs", NodeKind.Paragraph, null, 0, ContentLayer.Body,
            new RichTextNodeContent([new TextRun("strong", Bold: true), new TextRun("insert", Inserted: true),
                new TextRun("gone", Deleted: true), new TextRun("link", LinkTarget: "https://example.org")]));
        var table = new DocumentNode("parent", NodeKind.Table, null, 1, ContentLayer.Body,
            new TableNodeContent([[new TableCell("Group", ColSpan: 2)], [new TableCell("A"), new TableCell("B")]]));
        var nested = new DocumentNode("child", NodeKind.Table, "parent", 2, ContentLayer.Body,
            new TableNodeContent([[new TableCell("Nested heading")], [new TableCell("Nested value")]]),
            Extensions: new Dictionary<string, JsonElement>
            {
                ["nested_table_parent"] = JsonSerializer.SerializeToElement("parent"),
                ["nested_table_row"] = JsonSerializer.SerializeToElement(1),
                ["nested_table_column"] = JsonSerializer.SerializeToElement(0)
            });
        var result = ReadableProjectionBuilder.Build(Graph(DocumentFormatKind.Docx, rich, table, nested));
        var html = ReadableHtmlWriter.WriteDocument(result.Document).Html;
        Assert.Contains("<strong>strong</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<ins>insert</ins>", html, StringComparison.Ordinal);
        Assert.Contains("<del>gone</del>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://example.org\"", html, StringComparison.Ordinal);
        Assert.Contains("colspan=\"2\"", html, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "Nested value"));
        Assert.True(System.Text.RegularExpressions.Regex.Matches(html, "<table(?:>| )").Count >= 2);
        Assert.DoesNotContain("docredock:table-spans", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Formula_has_typed_display_expression_and_missing_cache_state()
    {
        var formula = new ReadableFormula("IF(A1=\"$x$\",1,0)", "1", true);
        var table = new ReadableTable([new ReadableColumn(1)],
            [new ReadableTableRow([new ReadableTableCell(ReadableContent.Text("Value"))]),
             new ReadableTableRow([new ReadableTableCell(ReadableContent.Text("1")) { Formula = formula }]),
             new ReadableTableRow([new ReadableTableCell(ReadableContent.Text("")) { Formula = new(null, "", false, true) }])]);
        var document = new ReadableDocument("Formula", "ja", DocumentFormatKind.Xlsx, [table]);
        var normal = ReadableHtmlWriter.WriteDocument(document).Html;
        Assert.DoesNotContain("IF(A1", normal, StringComparison.Ordinal);
        Assert.Contains("保存済み計算値なし", normal, StringComparison.Ordinal);
        var shown = ReadableHtmlWriter.WriteDocument(document, new ReadableHtmlOptions(ShowFormulas: true)).Html;
        Assert.Contains("<code>", shown, StringComparison.Ordinal);
        Assert.Contains("$x$", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("\\`", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void Policies_and_exact_sheet_names_filter_content_before_rendering()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "book", DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-A_B", 0, [Cell("A1", "visible", ContentLayer.Body), Cell("A2", "hidden", ContentLayer.Hidden),
                new DocumentNode("memo", NodeKind.Comment, null, 3, ContentLayer.Body, new TextNodeContent("private memo"),
                    Extensions: new Dictionary<string, JsonElement> { ["cell_ref"] = JsonSerializer.SerializeToElement("A1") })]),
             new DocumentPartition("sheet-excluded", 1, [Cell("A1", "excluded-content", ContentLayer.Body)])]);
        string Render(string policy) => ReadableHtmlWriter.WriteDocument(ReadableProjectionBuilder.Build(graph,
            new ReadableProjectionOptions(ContentPolicy: policy, IncludedSheets: ["A_B"], SourceName: "book.xlsx")).Document).Html;
        var visible = Render("visible");
        Assert.Contains("A_B", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("private memo", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("excluded-content", visible, StringComparison.Ordinal);
        Assert.DoesNotContain(">hidden<", visible, StringComparison.Ordinal);
        var complete = Render("complete");
        Assert.Contains("private memo", complete, StringComparison.Ordinal);
        Assert.Contains("hidden", complete, StringComparison.Ordinal);
        Assert.DoesNotContain("excluded-content", complete, StringComparison.Ordinal);
    }

    [Fact]
    public void Typed_images_embed_and_unsafe_links_remain_inert()
    {
        var document = new ReadableDocument("Safe", "en", DocumentFormatKind.Docx,
            [new ReadableParagraph(new ReadableContent([new TextRun("unsafe", LinkTarget: "javascript:alert(1)")])),
             new ReadableImage("sample.png", "source image")]);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var result = ReadableHtmlWriter.WriteDocument(document, new ReadableHtmlOptions(Assets:
            new Dictionary<string, ReadableHtmlAsset> { ["sample.png"] = new("image/png", png) }));
        Assert.Contains("data:image/png;base64,", result.Html, StringComparison.Ordinal);
        Assert.Contains("alt=\"source image\"", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"javascript:", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Projection_and_rendering_honor_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ReadableProjectionBuilder.Build(
            Graph(DocumentFormatKind.Docx, Text("p", "value")), cancellationToken: cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => ReadableHtmlWriter.WriteDocument(
            new ReadableDocument("test", "en", DocumentFormatKind.Docx, []), cancellationToken: cancellation.Token));
    }

    [Theory]
    [InlineData(DocumentFormatKind.Docx, false)]
    [InlineData(DocumentFormatKind.Docx, true)]
    [InlineData(DocumentFormatKind.Pptx, false)]
    [InlineData(DocumentFormatKind.Pptx, true)]
    [InlineData(DocumentFormatKind.Pdf, false)]
    [InlineData(DocumentFormatKind.Pdf, true)]
    public void Native_source_image_extents_override_the_bitmap_aspect_in_both_embedding_paths(
        DocumentFormatKind format, bool dataUri)
    {
        // A square bitmap deliberately drawn in a 2:1 source frame (as in the real DOCX repro).
        const string payload = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        var reference = dataUri ? "data:image/png;base64," + payload : "sample.png";
        var document = new ReadableDocument("Image source frame", "en", format,
            [new ReadableImage(reference, "source 2:1 frame", 96, 48)]);
        var result = ReadableHtmlWriter.WriteDocument(document, new ReadableHtmlOptions(Assets:
            new Dictionary<string, ReadableHtmlAsset> { ["sample.png"] = new("image/png", Convert.FromBase64String(payload)) }));
        Assert.Matches("<img\\b[^>]*width=\"96\" height=\"48\"[^>]*class=\"source-sized-image\"[^>]*style=\"aspect-ratio:96/48\"", result.Html);
        Assert.Empty(result.Diagnostics);
    }

    private static DocumentNode Cell(string address, string value, ContentLayer layer) =>
        new("cell-" + address, NodeKind.Cell, null, int.Parse(address[1..]), layer, new TextNodeContent(value),
            Extensions: new Dictionary<string, JsonElement>
            {
                ["row"] = JsonSerializer.SerializeToElement(int.Parse(address[1..])),
                ["column"] = JsonSerializer.SerializeToElement(1),
                ["address"] = JsonSerializer.SerializeToElement(address)
            });
}
