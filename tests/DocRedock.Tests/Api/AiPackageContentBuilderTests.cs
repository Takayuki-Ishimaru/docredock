using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using Xunit;
using System.Text.Json;

namespace DocRedock.Tests.Api;

public sealed class AiPackageContentBuilderTests
{
    private const string File = "report.md";

    private static DocumentGraph Graph(DocumentFormatKind format, params DocumentPartition[] partitions) =>
        new("1.1", "doc-1", format, partitions);

    private static DocumentNode Node(string id, NodeKind kind, string text, int order = 0,
        string? parentId = null, ContentLayer layer = ContentLayer.Body,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? extensions = null) =>
        new(id, kind, parentId, order, layer, new TextNodeContent(text),
            Extensions: extensions);

    private static DocumentPartition Partition(string id, int order, params DocumentNode[] nodes) =>
        new(id, order, nodes);

    private static IReadOnlyList<AiPackagePart> Build(DocumentGraph graph,
        ReadableMarkdownOptions? options = null, string file = File, int target = 12000,
        CancellationToken token = default) =>
        AiPackageContentBuilder.Build(graph, options ?? new(), file, target, token);

    [Fact]
    public void RejectsInvalidArguments()
    {
        var graph = Graph(DocumentFormatKind.Docx, Partition("p1", 0, Node("n1", NodeKind.Paragraph, "hello")));
        Assert.Throws<ArgumentNullException>(() => AiPackageContentBuilder.Build(null!, new(), File));
        Assert.Throws<ArgumentNullException>(() => AiPackageContentBuilder.Build(graph, null!, File));
        Assert.Throws<ArgumentException>(() => AiPackageContentBuilder.Build(graph, new(), "   "));
        Assert.Throws<ArgumentOutOfRangeException>(() => AiPackageContentBuilder.Build(graph, new(), File, 127));
        Assert.Throws<ArgumentException>(() => AiPackageContentBuilder.Build(graph, new(ContentPolicy: "bogus"), File));
    }

    [Fact]
    public void HonoursCancellation()
    {
        var graph = Graph(DocumentFormatKind.Pdf, Partition("p1", 0, Node("n1", NodeKind.Paragraph, "hello")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Build(graph, token: cts.Token));
    }

    [Fact]
    public void EmptyGraphProducesNoParts()
    {
        Assert.Empty(Build(Graph(DocumentFormatKind.Docx)));
        Assert.Empty(Build(Graph(DocumentFormatKind.Docx, Partition("p1", 0))));
    }

    [Fact]
    public async Task OutputIsDeterministicAndConcurrentBuildsAreIndependent()
    {
        var graph = Graph(DocumentFormatKind.Pdf,
            Partition("p1", 0, Node("n1", NodeKind.Paragraph, "first page text")),
            Partition("p2", 1, Node("n2", NodeKind.Paragraph, "second page text")));
        var other = Graph(DocumentFormatKind.Pptx,
            Partition("s1", 0, Node("s1n", NodeKind.Paragraph, "slide one")));
        var first = Build(graph);
        var second = Build(graph);
        Assert.Equal(first.Select(p => (p.Id, p.Path, p.Markdown)), second.Select(p => (p.Id, p.Path, p.Markdown)));
        var results = await System.Threading.Tasks.Task.WhenAll(
            System.Threading.Tasks.Task.Run(() => Build(graph)),
            System.Threading.Tasks.Task.Run(() => Build(other)),
            System.Threading.Tasks.Task.Run(() => Build(graph)));
        Assert.Equal(first.Select(p => p.Markdown), results[0].Select(p => p.Markdown));
        Assert.Equal(first.Select(p => p.Markdown), results[2].Select(p => p.Markdown));
        Assert.Single(results[1]);
    }

    [Fact]
    public void PdfKeepsEachPageWholeWithOriginalNumberingAndExceedsTarget()
    {
        var big = new string('あ', 400);
        var graph = Graph(DocumentFormatKind.Pdf,
            Partition("p1", 0, Node("n1", NodeKind.Paragraph, "small page")),
            Partition("p2", 1, Node("n2", NodeKind.Paragraph, big)),
            Partition("p3", 2));
        var parts = Build(graph, target: 200);
        Assert.Equal(2, parts.Count);
        Assert.Equal(["part-0001", "part-0002"], parts.Select(p => p.Id));
        Assert.Equal(["parts/0001.md", "parts/0002.md"], parts.Select(p => p.Path));
        Assert.False(parts[0].ExceedsTarget);
        Assert.True(parts[1].ExceedsTarget); // oversized intact unit kept whole
        Assert.Contains(big, parts[1].Markdown, StringComparison.Ordinal);
        var location = Assert.Single(parts[1].Sources);
        Assert.Equal("n2", location.NodeId);
        Assert.Equal("p2", location.PartitionId);
        Assert.Equal("page 2", location.Label);
        Assert.Equal(2, location.PageNumber);
        Assert.Null(location.SlideNumber);
        Assert.Null(location.SheetName);
    }

    [Fact]
    public void PptxKeepsOriginalSlideNumberingForSubsets()
    {
        var graph = Graph(DocumentFormatKind.Pptx,
            Partition("s1", 0, Node("s1n", NodeKind.Paragraph, "one")),
            Partition("s2", 1),
            Partition("s3", 2, Node("s3n", NodeKind.Paragraph, "three body")));
        var parts = Build(graph);
        Assert.Equal(2, parts.Count);
        Assert.Contains("スライド 1", parts[0].Markdown, StringComparison.Ordinal);
        Assert.Contains("スライド 3", parts[1].Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("three body", parts[1].Markdown[..parts[1].Markdown.IndexOf("スライド 3", StringComparison.Ordinal)], StringComparison.Ordinal);
        var location = Assert.Single(parts[1].Sources);
        Assert.Equal("slide 3", location.Label);
        Assert.Equal(3, location.SlideNumber);
    }

    [Fact]
    public void XlsxKeepsSheetsWholeAndSelected()
    {
        DocumentNode Cell(string id, string address, string text, int order) => new(id, NodeKind.Cell, null, order,
            ContentLayer.Body, new TextNodeContent(text), Source: new SourceAnchor("xlsx", "/xl/worksheets/sheet1.xml",
                [new AnchorLocator("cell_address", address)]));
        var graph = Graph(DocumentFormatKind.Xlsx,
            Partition("sheet-Summary", 0, Cell("c1", "A1", "Key", 0), Cell("c2", "B1", "Value", 1),
                Cell("c3", "A2", "a", 2), Cell("c4", "B2", new string('b', 300), 3)),
            Partition("sheet-Data_2", 1, Cell("d1", "A1", "Key", 0), Cell("d2", "B1", "Value", 1),
                Cell("d3", "A2", "c", 2), Cell("d4", "B2", "d", 3)));
        var all = Build(graph, target: 128);
        Assert.Equal(2, all.Count);
        Assert.True(all[0].ExceedsTarget);
        Assert.Equal(4, all[0].NodeIds.Count);
        Assert.Contains("Data 2", all[1].Markdown, StringComparison.Ordinal);
        Assert.Equal("sheet Data_2", all[1].Sources[0].Label);
        Assert.Equal("Data_2", all[1].Sources[0].SheetName);
        var selected = Build(graph, new ReadableMarkdownOptions(IncludedSheets: ["Data_2"]));
        Assert.Single(selected);
        Assert.Equal("sheet-Data_2", selected[0].Sources[0].PartitionId);
        Assert.DoesNotContain("Summary", selected[0].Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void PoliciesFilterNodesAndNeverLeakHiddenContent()
    {
        var graph = Graph(DocumentFormatKind.Docx, Partition("p1", 0,
            Node("visible", NodeKind.Paragraph, "public body text"),
            Node("hidden", NodeKind.Paragraph, "SECRET hidden text", parentId: null, layer: ContentLayer.Hidden),
            Node("meta", NodeKind.Paragraph, "SECRET metadata text", parentId: null, layer: ContentLayer.Metadata)));
        foreach (var policy in new[] { "visible", "sanitized" })
        {
            var parts = Build(graph, new ReadableMarkdownOptions(ContentPolicy: policy));
            Assert.DoesNotContain("SECRET", string.Concat(parts.Select(p => p.Markdown)), StringComparison.Ordinal);
            Assert.DoesNotContain("hidden", parts.SelectMany(p => p.NodeIds));
            Assert.DoesNotContain("meta", parts.SelectMany(p => p.NodeIds));
        }
        var complete = Build(graph, new ReadableMarkdownOptions(ContentPolicy: "complete"));
        Assert.Contains("SECRET hidden text", string.Concat(complete.Select(p => p.Markdown)), StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizedDropsFurnitureThatVisibleKeeps()
    {
        var graph = Graph(DocumentFormatKind.Docx, Partition("p1", 0,
            Node("body", NodeKind.Paragraph, "body text here"),
            Node("hdr", NodeKind.Header, "FURNITURE header text")));
        var visible = Build(graph, new ReadableMarkdownOptions(ContentPolicy: "visible"));
        Assert.Contains("FURNITURE", string.Concat(visible.Select(p => p.Markdown)), StringComparison.Ordinal);
        var sanitized = Build(graph, new ReadableMarkdownOptions(ContentPolicy: "sanitized"));
        Assert.DoesNotContain("FURNITURE", string.Concat(sanitized.Select(p => p.Markdown)), StringComparison.Ordinal);
    }

    [Fact]
    public void DocxSplitsOnHeadingsWithHeadingPathLabels()
    {
        var graph = Graph(DocumentFormatKind.Docx, Partition("p1", 0,
            Node("h1", NodeKind.Heading, "Alpha Chapter"),
            Node("b1", NodeKind.Paragraph, "alpha body", order: 1),
            Node("h2", NodeKind.Heading, "Beta Chapter", order: 2),
            Node("b2", NodeKind.Paragraph, "beta body", order: 3)));
        var parts = Build(graph);
        Assert.Equal(2, parts.Count);
        Assert.Equal(["h1", "b1"], parts[0].NodeIds);
        Assert.Equal(["h2", "b2"], parts[1].NodeIds);
        Assert.Contains("headings: Alpha Chapter", parts[0].Sources.Single(s => s.NodeId == "b1").Label, StringComparison.Ordinal);
        Assert.Equal("headings: Alpha Chapter", parts[0].Sources.Single(s => s.NodeId == "h1").Label);
        Assert.Null(parts[0].Sources.Single(s => s.NodeId == "b1").PageNumber);
        Assert.Equal(["Alpha Chapter"], parts[0].Sources.Single(s => s.NodeId == "b1").HeadingPath);
    }

    [Fact]
    public void DocxNeverSeversNestedTableTreeOrListSequence()
    {
        var table = new TableNodeContent([[new TableCell("x"), new TableCell("y")]]);
        var graph = Graph(DocumentFormatKind.Docx, Partition("p1", 0,
            Node("h1", NodeKind.Heading, "First"),
            new DocumentNode("t1", NodeKind.Table, null, 1, ContentLayer.Body, table),
            new DocumentNode("t1c", NodeKind.Table, "t1", 2, ContentLayer.Body, table),
            Node("h2", NodeKind.Heading, "Second", order: 3),
            new DocumentNode("l1", NodeKind.List, null, 4, ContentLayer.Body, new TextNodeContent("list")),
            Node("l1i", NodeKind.ListItem, "item one", order: 5, parentId: "l1"),
            Node("l1j", NodeKind.ListItem, "item two", order: 6, parentId: "l1")));
        var parts = Build(graph);
        Assert.Equal(2, parts.Count);
        Assert.Contains("t1", parts[0].NodeIds);
        Assert.Contains("t1c", parts[0].NodeIds);
        Assert.DoesNotContain("t1c", parts[1].NodeIds);
        Assert.Equal(["h2", "l1", "l1i", "l1j"], parts[1].NodeIds);
        Assert.Contains("| x | y |", parts[0].Markdown, StringComparison.Ordinal); // table not flattened
    }

    [Fact]
    public void DocxKeepsDiagramVisualMemberGroupIntact()
    {
        var members = System.Text.Json.JsonDocument.Parse("""{"members":["d1m1","d1m2"]}""").RootElement;
        var graph = Graph(DocumentFormatKind.Docx, Partition("p1", 0,
            Node("h1", NodeKind.Heading, "First"),
            Node("d1p", NodeKind.Paragraph, "before group", order: 1),
            new DocumentNode("d1", NodeKind.Diagram, null, 2, ContentLayer.Body, new TextNodeContent("flow"),
                Extensions: new Dictionary<string, System.Text.Json.JsonElement> { ["visual_graph"] = members }),
            Node("d1m1", NodeKind.Shape, "member one", order: 3),
            Node("d1m2", NodeKind.Shape, "member two", order: 4),
            Node("h2", NodeKind.Heading, "Second", order: 5),
            Node("b2", NodeKind.Paragraph, "after heading", order: 6)));
        var parts = Build(graph);
        var groupPart = parts.Single(p => p.NodeIds.Contains("d1"));
        Assert.Contains("d1m1", groupPart.NodeIds);
        Assert.Contains("d1m2", groupPart.NodeIds);
        Assert.DoesNotContain("d1m1", parts.Single(p => p.NodeIds.Contains("h2")).NodeIds);
    }

    [Fact]
    public void KeepsGraphImageDestinationsUnchanged()
    {
        var graph = Graph(DocumentFormatKind.Docx, Partition("p1", 0,
            new DocumentNode("img", NodeKind.Image, null, 0, ContentLayer.Body,
                new ReferenceNodeContent("assets/img.png", "diagram alt"))));
        var parts = Build(graph);
        Assert.Single(parts);
        Assert.Contains("assets/img.png", parts[0].Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapesLiteralSourceFileNameInSourceLine()
    {
        var graph = Graph(DocumentFormatKind.Docx, Partition("p1", 0, Node("n1", NodeKind.Paragraph, "text")));
        var parts = Build(graph, file: "my*report[2024].md");
        Assert.Contains("my\\*report\\[2024\\].md", parts[0].Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("my*report[2024].md", parts[0].Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void NoEmptyOrHeaderOnlyParts()
    {
        var graph = Graph(DocumentFormatKind.Pdf,
            Partition("p1", 0, Node("h", NodeKind.Header, "only furniture", layer: ContentLayer.Furniture)),
            Partition("p2", 1, Node("n", NodeKind.Paragraph, "real text")));
        var parts = Build(graph, new ReadableMarkdownOptions(ContentPolicy: "sanitized"));
        Assert.Single(parts);
        Assert.Equal("p2", parts[0].Sources[0].PartitionId);
        Assert.All(parts, p => Assert.False(string.IsNullOrWhiteSpace(p.Markdown)));
    }

    [Fact]
    public void SoftTargetKeepsLargeNestedTablesAndRootListsWhole()
    {
        var table = new TableNodeContent([[new TableCell(new string('x', 400)), new TableCell("end")]]);
        var graph = Graph(DocumentFormatKind.Docx, Partition("body", 0,
            Node("h1", NodeKind.Heading, "First"), Node("p1", NodeKind.Paragraph, new string('a', 200), 1),
            new DocumentNode("table", NodeKind.Table, null, 2, ContentLayer.Body, table),
            new DocumentNode("nested", NodeKind.Table, "table", 3, ContentLayer.Body, table),
            Node("l1", NodeKind.ListItem, new string('b', 200), 4),
            Node("l2", NodeKind.ListItem, new string('c', 200), 5),
            Node("l3", NodeKind.ListItem, new string('d', 200), 6),
            Node("h2", NodeKind.Heading, "Last", 7), Node("p2", NodeKind.Paragraph, "tail", 8)));
        var parts = Build(graph, target: 128);
        Assert.Equal(4, parts.Count);
        Assert.Equal(new[] { "table", "nested" }, parts.Single(p => p.NodeIds.Contains("table")).NodeIds);
        Assert.Equal(new[] { "l1", "l2", "l3" }, parts.Single(p => p.NodeIds.Contains("l1")).NodeIds);
        Assert.True(parts.Single(p => p.NodeIds.Contains("table")).ExceedsTarget);
        Assert.Equal(graph.Nodes.Select(n => n.Id), parts.SelectMany(p => p.NodeIds));
    }

    [Fact]
    public void HeadingPathsKeepHierarchyAndExcludeHiddenHeadingLabels()
    {
        var levels = new Dictionary<string, JsonElement> { ["heading_level"] = JsonSerializer.SerializeToElement(2) };
        var graph = Graph(DocumentFormatKind.Docx, Partition("body", 0,
            Node("secret", NodeKind.Heading, "SECRET HEADING", 0, layer: ContentLayer.Hidden),
            Node("before", NodeKind.Paragraph, "public text", 1),
            Node("h1", NodeKind.Heading, "Chapter", 2),
            Node("h2", NodeKind.Heading, "Section", 3, extensions: levels),
            Node("p", NodeKind.Paragraph, "body", 4)));
        var parts = Build(graph);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(parts));
        var source = parts.SelectMany(p => p.Sources).Single(s => s.NodeId == "p");
        Assert.Equal(new[] { "Chapter", "Section" }, source.HeadingPath);
        Assert.Null(source.PageNumber);
        Assert.Null(parts.SelectMany(p => p.Sources).Single(s => s.NodeId == "before").HeadingPath);
    }

    [Fact]
    public void RealVisualMembershipKeepsInterleavedNodesInOriginalOrder()
    {
        Dictionary<string, JsonElement> Shape(string id) => new() { ["shape_id"] = JsonSerializer.SerializeToElement(id) };
        var graph = Graph(DocumentFormatKind.Docx, Partition("body", 0,
            Node("h1", NodeKind.Heading, "First"), Node("a", NodeKind.Shape, "box A", 1, extensions: Shape("10")),
            Node("h2", NodeKind.Heading, "Between", 2), Node("b", NodeKind.Shape, "box B", 3, extensions: Shape("20")),
            Node("diagram", NodeKind.Diagram, "flow", 4, extensions: new Dictionary<string, JsonElement>
                { ["visual_graph_member_shape_ids"] = JsonSerializer.SerializeToElement(new[] { "10", "20" }) }),
            Node("h3", NodeKind.Heading, "After", 5), Node("p", NodeKind.Paragraph, "tail", 6)));
        var parts = Build(graph, target: 128);
        var visual = parts.Single(p => p.NodeIds.Contains("diagram"));
        Assert.Contains("a", visual.NodeIds); Assert.Contains("b", visual.NodeIds); Assert.Contains("h2", visual.NodeIds);
        Assert.DoesNotContain("h3", visual.NodeIds);
        Assert.Equal(graph.Nodes.Select(n => n.Id), parts.SelectMany(p => p.NodeIds));
    }

    [Theory]
    [InlineData("visible")]
    [InlineData("sanitized")]
    public void VisibleSheetOverlayIsRetainedInMarkdownAndSourceMap(string policy)
    {
        var overlay = Node("overlay", NodeKind.Shape, "MARKER", 0, layer: ContentLayer.Hidden,
            extensions: new Dictionary<string, JsonElement>
            {
                ["sheet_state"] = JsonSerializer.SerializeToElement("visible"),
                ["sheet_overlay"] = JsonSerializer.SerializeToElement(new
                { ShapeId = "1", Text = "MARKER", Kind = "arrow", Direction = "right", Axis = "horizontal",
                    StartRow = 1, EndRow = 1, StartColumn = 1, EndColumn = 2 })
            });
        var parts = Build(Graph(DocumentFormatKind.Xlsx, Partition("sheet-Data", 0, overlay)),
            new ReadableMarkdownOptions(ContentPolicy: policy));
        Assert.Single(parts);
        Assert.Contains("MARKER", parts[0].Markdown);
        Assert.Contains("overlay", parts[0].NodeIds);
        Assert.Equal("Data", Assert.Single(parts[0].Sources).SheetName);
    }
}
