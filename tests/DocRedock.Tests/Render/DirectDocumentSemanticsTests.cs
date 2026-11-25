using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using DocRedock.Providers.Abstractions.Providers;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

/// <summary>Semantic regressions on the native graph -> typed projection -> HTML route.</summary>
public sealed class DirectDocumentSemanticsTests
{
    private const string ImageReference = "review.assets/source.png";
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void Empty_revision_blocks_keep_structural_history_and_exclude_hidden_history()
    {
        var paragraph = Node("empty-paragraph", NodeKind.Paragraph, "", 0,
            Ext(("revision_changes", new[] { new { context = "paragraph", description = "EmptyParagraphRevision: paragraph mark deleted" } })));
        var section = Node("empty-section", NodeKind.Section, "", 1,
            Ext(("revision_changes", new[] { new { context = "section", description = "EmptySectionRevision: margin top=720 to top=1440" } })));
        var hidden = Node("hidden-revision", NodeKind.Paragraph, "hidden-source-secret", 2,
            Ext(("revision_changes", new[] { new { context = "hidden", description = "HiddenRevisionSecret" } })), ContentLayer.Hidden);
        var graph = Graph(DocumentFormatKind.Docx, paragraph, section, hidden);
        var baseline = Markdown(graph);
        Assert.Contains("EmptyParagraphRevision", baseline, StringComparison.Ordinal);
        Assert.Contains("EmptySectionRevision", baseline, StringComparison.Ordinal);
        Assert.DoesNotContain("HiddenRevisionSecret", baseline, StringComparison.Ordinal);

        var text = Text(Render(graph).Html);
        Assert.Equal(1, Count(text, "EmptyParagraphRevision"));
        Assert.Equal(1, Count(text, "EmptySectionRevision"));
        Assert.DoesNotContain("HiddenRevisionSecret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden-source-secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_table_revision_is_retained_once_at_its_recorded_line_position()
    {
        var parent = new DocumentNode("parent", NodeKind.Table, null, 0, ContentLayer.Body,
            new TableNodeContent([[new TableCell("BeforeNested\nAfterNested")]]));
        var child = new DocumentNode("child", NodeKind.Table, "parent", 1, ContentLayer.Body,
            new TableNodeContent([[new TableCell("NestedBody")]]), Extensions: Ext(
                ("nested_table_parent", "parent"), ("nested_table_row", 0), ("nested_table_column", 0),
                ("nested_table_line_offset", 1),
                ("revision_changes", new[] { new { context = "row", description = "NestedRevisionUnique: row inserted" } })));
        var graph = Graph(DocumentFormatKind.Docx, parent, child);
        var baseline = Markdown(graph);
        Assert.Equal(1, Count(baseline, "NestedRevisionUnique"));
        Assert.True(baseline.IndexOf("BeforeNested", StringComparison.Ordinal) < baseline.IndexOf("NestedBody", StringComparison.Ordinal));
        Assert.True(baseline.IndexOf("NestedBody", StringComparison.Ordinal) < baseline.IndexOf("AfterNested", StringComparison.Ordinal));

        var html = Render(graph).Html;
        var text = Text(html);
        Assert.Equal(1, Count(text, "NestedBody"));
        Assert.Equal(1, Count(text, "NestedRevisionUnique"));
        Assert.True(text.IndexOf("BeforeNested", StringComparison.Ordinal) < text.IndexOf("NestedBody", StringComparison.Ordinal), text);
        Assert.True(text.IndexOf("NestedBody", StringComparison.Ordinal) < text.IndexOf("AfterNested", StringComparison.Ordinal), text);
        Assert.True(Regex.Matches(html, "<table(?:>| )").Count >= 2, "The child remains a native nested table.");
    }

    [Fact]
    public void Pptx_paragraph_details_keep_bullet_levels_emphasis_and_complete_only_notes()
    {
        var bodyDetails = new[]
        {
            ParagraphDetail("IntroParagraph", 0, false),
            ParagraphDetail("• ImportantBullet", 0, true, bold: true),
            ParagraphDetail("NestedBullet", 1, true, italic: true)
        };
        var noteDetails = new[]
        {
            ParagraphDetail("• NoteImportant", 0, true, bold: true),
            ParagraphDetail("NoteNested", 1, true, italic: true)
        };
        var body = Node("body", NodeKind.Shape, "IntroParagraph\n• ImportantBullet\nNestedBullet", 0,
            Ext(("shape_role", "body"), ("paragraph_details", bodyDetails)));
        var notes = Node("notes", NodeKind.SpeakerNotes, "• NoteImportant\nNoteNested", 1,
            Ext(("paragraph_details", noteDetails)));
        var graph = Graph(DocumentFormatKind.Pptx, body, notes);
        var baseline = Markdown(graph, policy: "complete");
        Assert.Contains("**ImportantBullet**", baseline, StringComparison.Ordinal);
        Assert.Contains("_NestedBullet_", baseline, StringComparison.Ordinal);
        Assert.Contains("**NoteImportant**", baseline, StringComparison.Ordinal);
        Assert.DoesNotContain("NoteImportant", Markdown(graph), StringComparison.Ordinal);

        var html = Render(graph, policy: "complete").Html;
        Assert.Contains("<strong>ImportantBullet</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<em>NestedBullet</em>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>NoteImportant</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<em>NoteNested</em>", html, StringComparison.Ordinal);
        Assert.True(Regex.Matches(html, "<ul(?:>| )").Count >= 2, "Bullet hierarchy must survive as nested lists.");
        Assert.Matches(new Regex("<li\\b[^>]*>.*?<strong>ImportantBullet</strong>.*?<ul\\b[^>]*>.*?<em>NestedBullet</em>", RegexOptions.Singleline), html);
        Assert.Matches(new Regex("<details\\b[^>]*>.*?NoteImportant.*?</details>", RegexOptions.Singleline), html);
        Assert.DoesNotContain("• ImportantBullet", Text(html), StringComparison.Ordinal);
        Assert.DoesNotContain("NoteImportant", Text(Render(graph).Html), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OcrReviewMode.LowConfidence, 2, 1)]
    [InlineData(OcrReviewMode.All, 2, 2)]
    [InlineData(OcrReviewMode.Summary, 1, 1)]
    public void Ocr_modes_keep_body_and_filter_only_review_rows_with_image_coordinates(OcrReviewMode mode, int lowOccurrences, int highOccurrences)
    {
        var image = new DocumentNode("image", NodeKind.Image, null, 0, ContentLayer.Body,
            new ReferenceNodeContent(ImageReference, "Original image"));
        var regions = new[]
        {
            new OcrTextRegion("AP|", new Geometry("image-pixels", 10, 20, 30, 12), .42, 1),
            new OcrTextRegion("2026", null, null, 1),
            new OcrTextRegion("仕様書", new Geometry("vision-normalized-bottom-left", .1, .2, .3, .1), .9, 2)
        };
        var ocr = new DocumentNode("ocr", NodeKind.ImageText, "image", 1, ContentLayer.Derived,
            new TextNodeContent("AP| 2026\n仕様書"), Extensions: Ext(("ocr_regions", regions)));
        var graph = Graph(DocumentFormatKind.Docx, image, ocr);
        var baseline = Markdown(graph, mode);
        Assert.Contains("> AP| 2026", baseline, StringComparison.Ordinal);
        Assert.Contains("> 仕様書", baseline, StringComparison.Ordinal);
        Assert.Equal(mode != OcrReviewMode.Summary, baseline.Contains("#xywh=pixel:10,20,30,12", StringComparison.Ordinal));
        Assert.Equal(mode == OcrReviewMode.All, baseline.Contains("#xywh=percent:10,70,30,10", StringComparison.Ordinal));

        var result = Render(graph, mode);
        var html = result.Html;
        var text = Text(html);
        Assert.Equal(lowOccurrences, Count(text, "AP|"));
        Assert.Equal(lowOccurrences, Count(text, "2026"));
        Assert.Equal(highOccurrences, Count(text, "仕様書"));
        Assert.DoesNotContain("API", text, StringComparison.Ordinal);
        Assert.Contains("<details", html, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,", html, StringComparison.Ordinal);
        Assert.Equal(0, result.Projection.Report.RenderedTables); // OCR review rows are not document tables.
        if (mode != OcrReviewMode.Summary)
        {
            Assert.True(text.Contains("42%", StringComparison.Ordinal) || text.Contains("0.42", StringComparison.Ordinal), text);
            Assert.True(text.Contains("未提供", StringComparison.Ordinal) || text.Contains("not provided", StringComparison.OrdinalIgnoreCase), text);
            Assert.True(html.Contains("xywh=pixel:10,20,30,12", StringComparison.Ordinal) ||
                (text.Contains("image-pixels", StringComparison.Ordinal) && text.Contains("x=10", StringComparison.Ordinal) &&
                 text.Contains("y=20", StringComparison.Ordinal) && text.Contains("w=30", StringComparison.Ordinal) && text.Contains("h=12", StringComparison.Ordinal)), text);
        }
        if (mode == OcrReviewMode.All)
            Assert.True(html.Contains("xywh=percent:10,70,30,10", StringComparison.Ordinal) ||
                (text.Contains("vision-normalized-bottom-left", StringComparison.Ordinal) && text.Contains("x=0.1", StringComparison.Ordinal) &&
                 text.Contains("y=0.2", StringComparison.Ordinal) && text.Contains("w=0.3", StringComparison.Ordinal) && text.Contains("h=0.1", StringComparison.Ordinal)), text);
    }

    [Fact]
    public void Pdf_review_annotations_keep_image_descriptions_and_meaningful_fallback_messages()
    {
        var annotation = Node("annotation", NodeKind.Annotation, "Raw annotation fallback", 0,
            Ext(("pdf_review_reason", "PdfTableReconstructionFailed"),
                ("pdf_image_descriptions", new[] { "PdfImageDescriptionUnique: a valve connects the two pumps" })));
        var textless = Node("textless", NodeKind.Paragraph, "Raw textless placeholder", 1, Ext(("pdf_textless_placeholder", true)));
        var unparsed = Node("unparsed", NodeKind.Paragraph, "Raw unparsed object placeholder", 2, Ext(("pdf_unparsed_form_xobjects", true)));
        var uncertain = Node("uncertain", NodeKind.Paragraph, "Raw uncertain text placeholder", 3, Ext(("pdf_uncertain_text", true)));
        var graph = Graph(DocumentFormatKind.Pdf, annotation, textless, unparsed, uncertain);
        var baseline = Markdown(graph);
        foreach (var phrase in new[] { "PdfImageDescriptionUnique", "表を組み立てられませんでした", "このページの文字を抽出できませんでした", "解析できなかった描画部品", "文字や表示状態を確定できない" })
            Assert.Contains(phrase, baseline, StringComparison.Ordinal);

        var html = Render(graph).Html;
        var text = Text(html);
        Assert.Contains("PdfImageDescriptionUnique: a valve connects the two pumps", text, StringComparison.Ordinal);
        Assert.Contains("表を組み立てられませんでした", text, StringComparison.Ordinal);
        Assert.Contains("このページの文字を抽出できませんでした", text, StringComparison.Ordinal);
        Assert.Contains("解析できなかった描画部品", text, StringComparison.Ordinal);
        Assert.Contains("文字や表示状態を確定できない", text, StringComparison.Ordinal);
        Assert.Contains("原本", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DocumentFormatKind.Docx)]
    [InlineData(DocumentFormatKind.Pptx)]
    [InlineData(DocumentFormatKind.Pdf)]
    public void Invalid_visual_graph_never_suppresses_its_source_member_or_connector(DocumentFormatKind format)
    {
        var invalid = new VisualGraph("invalid-flow", [new VisualNode("a", "Start"), new VisualNode("b", "End")],
            [new VisualEdge("dangling", "a", "missing")]);
        var diagram = Node("diagram", NodeKind.Diagram, "Diagram source summary", 0,
            Ext(("visual_graph", invalid)), ContentLayer.Derived);
        var member = Node("member", NodeKind.TextBox, "SourceMemberUnique", 1, Ext(("visual_graph_member", true)));
        var connector = Node("connector", NodeKind.Connector, "SourceConnectorUnique: Start -> End", 2, Ext(("visual_graph_edge", true)));
        var graph = Graph(format, diagram, member, connector);
        var serializer = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(Language: "ja"));
        var baseline = serializer.Serialize(graph);
        Assert.Contains("SourceMemberUnique", baseline, StringComparison.Ordinal);
        Assert.Contains("SourceConnectorUnique", baseline, StringComparison.Ordinal);
        Assert.Contains(serializer.Diagnostics, diagnostic => diagnostic.Code == "VisualSemanticProjectionFallback");

        var result = Render(graph);
        var text = Text(result.Html);
        Assert.Equal(1, Count(text, "SourceMemberUnique"));
        Assert.Equal(1, Count(text, "SourceConnectorUnique"));
        Assert.Contains(result.Projection.Diagnostics, diagnostic => diagnostic.Code == "VisualSemanticProjectionFallback");
    }

    private static (ReadableProjectionResult Projection, string Html) Render(DocumentGraph graph,
        OcrReviewMode mode = OcrReviewMode.LowConfidence, string policy = "visible")
    {
        var projected = ReadableProjectionBuilder.Build(graph, new ReadableProjectionOptions(
            ContentPolicy: policy, OcrReview: mode, Language: "ja"));
        var html = ReadableHtmlWriter.WriteDocument(projected.Document, new ReadableHtmlOptions(Assets:
            new Dictionary<string, ReadableHtmlAsset> { [ImageReference] = new("image/png", Png) })).Html;
        return (projected, html);
    }

    private static string Markdown(DocumentGraph graph, OcrReviewMode mode = OcrReviewMode.LowConfidence, string policy = "visible") =>
        new ReadableMarkdownSerializer(new ReadableMarkdownOptions(ContentPolicy: policy, OcrReview: mode, Language: "ja")).Serialize(graph);

    private static DocumentGraph Graph(DocumentFormatKind format, params DocumentNode[] nodes) =>
        new(DocumentGraph.CurrentSchemaVersion, "direct-semantics", format, [new DocumentPartition("part-1", 0, nodes)]);

    private static DocumentNode Node(string id, NodeKind kind, string text, int order,
        Dictionary<string, JsonElement>? extensions = null, ContentLayer layer = ContentLayer.Body) =>
        new(id, kind, null, order, layer, new TextNodeContent(text), Extensions: extensions);

    private static Dictionary<string, JsonElement> Ext(params (string Key, object Value)[] values) =>
        values.ToDictionary(value => value.Key, value => JsonSerializer.SerializeToElement(value.Value), StringComparer.Ordinal);

    private static object ParagraphDetail(string text, int level, bool bullet, bool bold = false, bool italic = false) =>
        new { Text = text, Level = level, IsBullet = bullet, Runs = new[] { new { Text = text, Bold = bold, Italic = italic, Underline = false } } };

    private static string Text(string html)
    {
        var withoutStyle = Regex.Replace(html, "<style\\b[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return WebUtility.HtmlDecode(Regex.Replace(withoutStyle, "<[^>]+>", " "));
    }

    private static int Count(string text, string value) => Regex.Matches(text, Regex.Escape(value)).Count;
}
