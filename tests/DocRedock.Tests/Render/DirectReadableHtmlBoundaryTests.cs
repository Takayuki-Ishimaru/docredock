using System.Collections;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

public sealed class DirectReadableHtmlBoundaryTests
{
    [Fact]
    public void Section_ids_are_encoded_in_navigation_links()
    {
        const string id = "part\" data-escape-probe=\"injected";
        var document = new ReadableDocument("safe", "en", DocumentFormatKind.Docx,
            [new ReadableSection(id, ReadableContent.Text("Section"), [])]);
        var html = ReadableHtmlWriter.WriteDocument(document).Html;

        Assert.Contains("href=\"#" + WebUtility.HtmlEncode(id) + "\"", html,
            StringComparison.Ordinal);
        Assert.DoesNotContain(" data-escape-probe=\"injected\"", html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Static_controls_sections_and_repeated_headings_have_unique_ids()
    {
        var document = new ReadableDocument("ids", "en", DocumentFormatKind.Docx,
        [
            new ReadableSection("docredock-theme-light", ReadableContent.Text("Section"), []),
            new ReadableHeading(2, ReadableContent.Text("docredock-theme-dark")),
            new ReadableHeading(2, ReadableContent.Text("docredock-theme-dark")),
            new ReadableSection("repeat", ReadableContent.Text("Repeated"), []),
            new ReadableSection("repeat", ReadableContent.Text("Repeated"), [])
        ]);
        var html = ReadableHtmlWriter.WriteDocument(document).Html;
        var ids = Regex.Matches(html, @"(?<![\w:-])id=""(?<id>[^""]*)""")
            .Select(match => WebUtility.HtmlDecode(match.Groups["id"].Value)).ToArray();
        var targets = Regex.Matches(html, @"href=""#(?<id>[^""]*)""")
            .Select(match => WebUtility.HtmlDecode(match.Groups["id"].Value)).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(targets, target => Assert.Contains(target, ids));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Invalid_native_spans_keep_payload_and_produce_a_diagnostic(
        int rowspan, int colspan)
    {
        var node = NativeTable("broken",
            [new TableCell("INVALID_SPAN_PAYLOAD", RowSpan: rowspan, ColSpan: colspan)]);
        var result = ReadableProjectionBuilder.Build(Graph(DocumentFormatKind.Docx, node));
        var html = ReadableHtmlWriter.WriteDocument(result.Document).Html;

        Assert.NotEmpty(result.Diagnostics);
        Assert.Contains("INVALID_SPAN_PAYLOAD", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlapping_native_rowspans_are_reported_instead_of_claiming_a_regular_grid()
    {
        var node = NativeTable("overlap",
            [new TableCell("ORIGIN_PAYLOAD", RowSpan: 2)],
            [new TableCell("OVERLAPPING_PAYLOAD")]);
        var result = ReadableProjectionBuilder.Build(Graph(DocumentFormatKind.Docx, node));
        var html = ReadableHtmlWriter.WriteDocument(result.Document).Html;

        Assert.NotEmpty(result.Diagnostics);
        Assert.Contains("ORIGIN_PAYLOAD", html, StringComparison.Ordinal);
        Assert.Contains("OVERLAPPING_PAYLOAD", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_width_arithmetic_is_bounded_before_integer_sum_can_overflow()
    {
        var repeated = new TableCell("BOUNDED_PAYLOAD", ColSpan: 16384);
        var row = Enumerable.Repeat(repeated, 131073).ToArray();
        var result = ReadableProjectionBuilder.Build(
            Graph(DocumentFormatKind.Docx, NativeTable("huge-width", row)));

        Assert.NotEmpty(result.Diagnostics);
        Assert.Contains(TextSources(result.Document.Blocks), text => text.Contains("BOUNDED_PAYLOAD", StringComparison.Ordinal));
        Assert.All(Blocks(result.Document.Blocks).OfType<ReadableTable>(),
            table => Assert.InRange(table.Columns.Count, 0, 16384));
    }

    [Fact]
    public void Wide_native_table_does_not_allocate_a_column_list_for_every_cell()
    {
        const int columns = 2048;
        var content = ReadableContent.Text("v");
        var table = new ReadableTable(
            Enumerable.Range(1, columns).Select(c => new ReadableColumn(c, 20)).ToArray(),
            Enumerable.Range(1, 2).Select(row => new ReadableTableRow(
                Enumerable.Range(1, columns).Select(c =>
                    new ReadableTableCell(content, SourceColumn: c)).ToArray(), row)).ToArray());
        var document = new ReadableDocument("wide", "en", DocumentFormatKind.Xlsx, [table]);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var html = ReadableHtmlWriter.WriteDocument(document).Html;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains("<table", html, StringComparison.Ordinal);
        Assert.InRange(allocated, 1, 32L * 1024 * 1024);
    }

    [Fact]
    public void Rendering_observes_cancellation_after_it_has_started_consuming_blocks()
    {
        using var cancellation = new CancellationTokenSource();
        var blocks = new CancelOnSecondRead<ReadableBlock>(
            [new ReadableParagraph(ReadableContent.Text("first")),
             new ReadableParagraph(ReadableContent.Text("second"))], cancellation);
        var document = new ReadableDocument("cancel", "en", DocumentFormatKind.Docx, blocks);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ReadableHtmlWriter.WriteDocument(document, cancellationToken: cancellation.Token));
        Assert.Equal(2, blocks.Reads);
    }

    [Fact]
    public void Unsafe_style_values_and_style_identifiers_do_not_become_active_markup()
    {
        const string styleId = "style\" data-style-probe=\"injected";
        var cell = new ReadableTableCell(ReadableContent.Text("styled")) { StyleId = styleId };
        var document = new ReadableDocument("style", "en", DocumentFormatKind.Xlsx,
            [new ReadableTable([new ReadableColumn(1)],
                [new ReadableTableRow([cell])])])
        {
            Styles = new Dictionary<string, StyleDescriptor>
            {
                [styleId] = new(styleId, Properties: new Dictionary<string, string>
                {
                    ["text-align"] = "center",
                    ["background-color"] = "</style><script>STYLE_PAYLOAD</script>",
                    ["color"] = "url(https://example.invalid/STYLE_PAYLOAD)",
                    ["font-size"] = "NaNpt",
                    ["border-left"] = "1px solid #000000;STYLE_PAYLOAD:x"
                })
            }
        };
        var html = ReadableHtmlWriter.WriteDocument(document).Html;

        Assert.Contains("text-align:center;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("STYLE_PAYLOAD", html, StringComparison.Ordinal);
        Assert.DoesNotContain(" data-style-probe=\"injected\"", html, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(html, "<script>"));
        Assert.Contains("<script>" + ReadableHtmlInteractions.Script + "</script>", html, StringComparison.Ordinal);
        Assert.Contains("script-src '" + ReadableHtmlInteractions.CspHash + "'", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Rendered_table_report_matches_nested_tables_that_are_written()
    {
        var parent = NativeTable("parent",
            [new TableCell("Header")], [new TableCell("Parent value")]);
        var nested = NativeTable("nested",
            [new TableCell("Nested header")], [new TableCell("Nested value")]) with
        {
            ParentId = "parent",
            Extensions = new Dictionary<string, JsonElement>
            {
                ["nested_table_parent"] = JsonSerializer.SerializeToElement("parent"),
                ["nested_table_row"] = JsonSerializer.SerializeToElement(1),
                ["nested_table_column"] = JsonSerializer.SerializeToElement(0)
            }
        };
        var result = ReadableProjectionBuilder.Build(
            Graph(DocumentFormatKind.Docx, parent, nested));
        var html = ReadableHtmlWriter.WriteDocument(result.Document).Html;

        Assert.Equal(2, result.Report.RenderedTables);
        Assert.Equal(result.Report.RenderedTables,
            Regex.Matches(html, @"<table(?:\s|>)").Count);
        Assert.Empty(result.Report.UnwrittenImageIds);
    }

    [Fact]
    public void Unwritten_image_report_only_counts_the_selected_sheet()
    {
        DocumentNode Cell(string id) => new(id, NodeKind.Cell, null, 0,
            ContentLayer.Body, new TextNodeContent("value"),
            Extensions: new Dictionary<string, JsonElement>
            {
                ["row"] = JsonSerializer.SerializeToElement(1),
                ["column"] = JsonSerializer.SerializeToElement(1)
            });
        DocumentNode BrokenImage(string id) => new(id, NodeKind.Image, null, 1,
            ContentLayer.Body, new TextNodeContent("unresolved source image"));
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion,
            "report-selection", DocumentFormatKind.Xlsx,
        [
            new DocumentPartition("sheet-Kept", 0,
                [Cell("kept-cell"), BrokenImage("kept-image")]),
            new DocumentPartition("sheet-Excluded", 1,
                [Cell("excluded-cell"), BrokenImage("excluded-image")])
        ]);
        var result = ReadableProjectionBuilder.Build(graph,
            new ReadableProjectionOptions(IncludedSheets: ["Kept"]));

        Assert.Equal(["kept-image"], result.Report.UnwrittenImageIds);
        var diagnostic = Assert.Single(result.Diagnostics,
            item => item.Code == "ImageNotWritten");
        Assert.Equal("kept-image", diagnostic.BlockId);
        Assert.DoesNotContain(result.Diagnostics,
            item => item.BlockId == "excluded-image");
    }

    [Fact]
    public void Workbook_source_range_includes_the_bounded_merge_extent()
    {
        var cell = new DocumentNode("merged-cell", NodeKind.Cell, null, 0,
            ContentLayer.Body, new TextNodeContent("merged value"),
            Extensions: new Dictionary<string, JsonElement>
            {
                ["row"] = JsonSerializer.SerializeToElement(1),
                ["column"] = JsonSerializer.SerializeToElement(1),
                ["address"] = JsonSerializer.SerializeToElement("A1"),
                ["merged_to_column"] = JsonSerializer.SerializeToElement(3),
                ["merged_to_row"] = JsonSerializer.SerializeToElement(3)
            });
        var result = ReadableProjectionBuilder.Build(Graph(DocumentFormatKind.Xlsx, cell));
        var table = Assert.Single(Blocks(result.Document.Blocks).OfType<ReadableTable>());

        Assert.Equal("A1:C3", table.Origin!.Range);
    }

    private static DocumentGraph Graph(DocumentFormatKind format, params DocumentNode[] nodes) =>
        new(DocumentGraph.CurrentSchemaVersion, "boundary", format,
            [new DocumentPartition("part-1", 0, nodes)]);

    private static DocumentNode NativeTable(string id,
        params IReadOnlyList<TableCell>[] rows) =>
        new(id, NodeKind.Table, null, 0, ContentLayer.Body, new TableNodeContent(rows));

    private static IEnumerable<ReadableBlock> Blocks(IEnumerable<ReadableBlock> blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            IEnumerable<ReadableBlock> children = block switch
            {
                ReadableSection section => section.Blocks,
                ReadableQuote quote => quote.Blocks,
                ReadableDetails details => details.Blocks,
                ReadableTable table => table.Rows.SelectMany(row => row.Cells)
                    .SelectMany(cell => cell.Children),
                _ => []
            };
            foreach (var child in Blocks(children)) yield return child;
        }
    }

    private static IEnumerable<string> TextSources(IEnumerable<ReadableBlock> blocks) =>
        Blocks(blocks).SelectMany(block => block switch
        {
            ReadableParagraph paragraph => new[] { paragraph.Content.PlainText },
            ReadableHeading heading => new[] { heading.Content.PlainText },
            ReadableCode code => new[] { code.Text },
            ReadableList list => list.Items.Select(item => item.Content.PlainText),
            ReadableTable table => table.Rows.SelectMany(row => row.Cells)
                .Select(cell => cell.Content.PlainText),
            _ => []
        });

    private sealed class CancelOnSecondRead<T>(
        IReadOnlyList<T> values, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public int Reads { get; private set; }
        public int Count => values.Count;
        public T this[int index]
        {
            get
            {
                if (++Reads == 2) cancellation.Cancel();
                return values[index];
            }
        }
        public IEnumerator<T> GetEnumerator()
        {
            for (var index = 0; index < values.Count; index++) yield return this[index];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
