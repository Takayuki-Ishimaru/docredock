using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Diff;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Formats.OpenXml.Docx;
using DocRedock.Markdown;

namespace DocRedock.Tests.Docx;

public sealed class DocxAdapterTests
{
    [Fact]
    public async Task Extracts_main_structures_and_f0_restore_is_byte_identical()
    {
        var source = await CreateDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "unchanged.docx");
        var adapter = new DocxAdapter();

        var export = await adapter.ExtractAsync(source);
        var result = await adapter.RestoreAsync(export, export.Graph, output);

        Assert.Contains(export.Graph.Nodes, node => node.Kind == NodeKind.Heading && Text(node) == "Title");
        Assert.Contains(export.Graph.Nodes, node => node.Kind == NodeKind.ListItem && Text(node) == "One");
        Assert.Contains(export.Graph.Nodes, node => node.Kind == NodeKind.Table);
        Assert.True(result.Succeeded);
        Assert.Equal(Hash(source), Hash(output));
    }

    [Fact]
    public async Task AlternateContent_extracts_only_supported_choice_and_assigns_unique_visual_ids()
    {
        var source = await CreateAlternateContentDocxAsync();
        var export = await new DocxAdapter().ExtractAsync(source);
        var boxes = export.Graph.Nodes.Where(node => node.Kind == NodeKind.TextBox).ToArray();

        Assert.Equal(4, boxes.Length);
        Assert.Single(boxes, node => Text(node) == "Choice textbox");
        Assert.Single(boxes, node => Text(node) == "Top-level fallback textbox");
        Assert.DoesNotContain(boxes, node => Text(node) == "Fallback textbox");
        Assert.DoesNotContain(boxes, node => Text(node) == "Unsupported top-level choice");
        Assert.Equal(boxes.Length, boxes.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(boxes.Length, boxes.Select(node => node.Source).Distinct().Count());
    }

    [Fact]
    public async Task F1_changes_only_dirty_paragraph_and_preserves_unrelated_payloads()
    {
        var source = await CreateDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "changed.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => Text(node) == "Before");
        var editedNodes = export.Graph.Nodes.Select(node => node.Id == target.Id ? node with { Content = new TextNodeContent("After") } : node).ToArray();
        var edited = export.Graph with { Partitions = [new DocumentPartition("part-0001", 0, editedNodes, "/word/document.xml")] };

        var result = await adapter.RestoreAsync(export, edited, output, new DiffOptions());

        Assert.True(result.Succeeded);
        Assert.Equal("After", Text((await adapter.ExtractAsync(output)).Graph.Nodes.Single(node => node.Id == target.Id)));
        Assert.Equal(ReadEntryHash(source, "word/media/image1.png"), ReadEntryHash(output, "word/media/image1.png"));
        Assert.Equal(ReadUnchangedFirstParagraphSlice(source), ReadUnchangedFirstParagraphSlice(output));
        Assert.Contains("<w:t>A</w:t></w:r><w:r><w:t>fter</w:t>", ReadDocumentXml(output));
    }

    [Fact]
    public async Task Explicit_delete_removes_node_but_missing_node_does_not()
    {
        var source = await CreateDocxAsync();
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var deleted = export.Graph.Nodes.Single(node => Text(node) == "Before");
        var edited = export.Graph with { Partitions = [new DocumentPartition("part-0001", 0, export.Graph.Nodes.Where(node => node.Id != deleted.Id).ToArray(), "/word/document.xml")] };
        var retainedOutput = Path.Combine(Path.GetDirectoryName(source)!, "retained.docx");
        var deleteOutput = Path.Combine(Path.GetDirectoryName(source)!, "deleted.docx");

        var retained = await adapter.RestoreAsync(export, edited, retainedOutput);
        var applied = await adapter.RestoreAsync(export, edited, deleteOutput, new DiffOptions(new HashSet<string> { deleted.Id }));

        Assert.Equal("Before", Text((await adapter.ExtractAsync(retainedOutput)).Graph.Nodes.Single(node => node.Id == deleted.Id)));
        Assert.DoesNotContain((await adapter.ExtractAsync(deleteOutput)).Graph.Nodes, node => node.Id == deleted.Id);
        Assert.True(applied.Succeeded);
        Assert.True(retained.Succeeded);
    }

    [Fact]
    public async Task F1_updates_list_item_and_same_shape_table_cells()
    {
        var source = await CreateDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "table-and-list.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var editedNodes = export.Graph.Nodes.Select(node => node switch
        {
            { Kind: NodeKind.ListItem } => node with { Content = new TextNodeContent("Two") },
            { Kind: NodeKind.Table } => node with { Content = new TableNodeContent([new TableCell[] { "Changed cell" }]) },
            _ => node
        }).ToArray();
        var edited = export.Graph with { Partitions = [new DocumentPartition("part-0001", 0, editedNodes, "/word/document.xml")] };

        var result = await adapter.RestoreAsync(export, edited, output);
        var reexport = await adapter.ExtractAsync(output);

        Assert.True(result.Succeeded);
        Assert.Contains(reexport.Graph.Nodes, node => node.Kind == NodeKind.ListItem && Text(node) == "Two");
        Assert.Equal("Changed cell", Assert.IsType<TableNodeContent>(reexport.Graph.Nodes.Single(node => node.Kind == NodeKind.Table).Content).Rows[0][0].Text);
    }

    [Fact]
    public async Task Rich_text_subset_round_trips_run_properties_breaks_tabs_and_paragraph_properties()
    {
        var source = await CreateRichTextDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "rich-changed.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => node.StyleId == "QuoteStyle");
        var rich = Assert.IsType<RichTextNodeContent>(target.Content);

        Assert.Collection(rich.Runs,
            run => Assert.Equal(new TextRun("Bold", Bold: true, Color: "1F4E79"), run),
            run => Assert.Equal(new TextRun("Italic", Italic: true), run),
            run => Assert.Equal(new TextRun("Under", Underline: true), run),
            run => Assert.Equal(new TextRun("Strike", Strike: true), run),
            run => Assert.Equal(new TextRun("Code", "CodeChar", Code: true), run),
            run => Assert.Equal(new TextRun("\n", Kind: TextRunKind.LineBreak), run),
            run => Assert.Equal(new TextRun("\t", Kind: TextRunKind.Tab), run),
            run => Assert.Equal(new TextRun("Tail"), run));

        var changedRuns = new[]
        {
            // The restored "太字" run reuses the original Bold-run's cloned rPr (see
            // CreateRunProperties), which still carries the original w:color — that field is
            // outside the round-trip contract (TextRun.Color is never written from an edit), so
            // it survives untouched rather than being cleared.
            new TextRun("太字", Bold: true, Color: "1F4E79"),
            new TextRun("斜体", Italic: true),
            new TextRun("下線", Underline: true),
            new TextRun("\n", Kind: TextRunKind.LineBreak),
            new TextRun("コード", "CodeChar", Code: true),
            new TextRun("\t", Kind: TextRunKind.Tab),
            new TextRun("取消", Strike: true)
        };
        var editedNodes = export.Graph.Nodes.Select(node => node.Id == target.Id
            ? node with { Content = new RichTextNodeContent(changedRuns) }
            : node).ToArray();
        var edited = export.Graph with { Partitions = [new DocumentPartition("part-0001", 0, editedNodes, "/word/document.xml")] };

        var result = await adapter.RestoreAsync(export, edited, output);
        var documentXml = ReadDocumentXml(output);
        var reexport = await adapter.ExtractAsync(output);
        var restored = Assert.IsType<RichTextNodeContent>(reexport.Graph.Nodes.Single(node => node.Id == target.Id).Content);

        Assert.True(result.Succeeded);
        Assert.Contains("<w:pStyle w:val=\"QuoteStyle\"", documentXml);
        Assert.Contains("<w:jc w:val=\"center\"", documentXml);
        Assert.Contains("<w:spacing w:before=\"120\" w:after=\"80\"", documentXml);
        Assert.Contains("<w:b", documentXml);
        Assert.Contains("<w:i", documentXml);
        Assert.Contains("<w:u", documentXml);
        Assert.Contains("<w:br", documentXml);
        Assert.Contains("<w:tab", documentXml);
        Assert.Contains("<w:rStyle w:val=\"CodeChar\"", documentXml);
        Assert.Contains("<w:strike", documentXml);
        Assert.Equal(changedRuns, restored.Runs);
    }

    [Fact]
    public async Task RichTextCompletesDocxToMarkdownEditToDocxRoundTrip()
    {
        var source = await CreateRichTextDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "rich-markdown-roundtrip.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var markdown = new DocRedockMarkdownSerializer().Serialize(export.Graph).Markdown;

        Assert.Contains("rich-text=inline-v1", markdown);
        Assert.Contains("**Bold**", markdown);
        var graphEdit = new MarkdownGraphEditor().Apply(export.Graph,
            markdown.Replace("**Bold**", "**重要**", StringComparison.Ordinal));
        var restore = await adapter.RestoreAsync(export, graphEdit.EditedGraph, output);
        var restored = await adapter.ExtractAsync(output);
        var rich = Assert.IsType<RichTextNodeContent>(restored.Graph.Nodes.Single(node => node.StyleId == "QuoteStyle").Content);

        Assert.True(graphEdit.IsValid);
        Assert.True(restore.Succeeded);
        Assert.Contains(rich.Runs, run => run.Text == "重要" && run.Bold);
        Assert.Contains(rich.Runs, run => run.Kind == TextRunKind.LineBreak);
        Assert.Contains(rich.Runs, run => run.Kind == TextRunKind.Tab);
    }

    [Fact]
    public async Task MarkdownRestorePreservesOriginalRunFontsSizesColorsAndPageLayout()
    {
        var source = await CreateRichTextDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "font-layout-roundtrip.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var markdown = new DocRedockMarkdownSerializer().Serialize(export.Graph).Markdown;
        var edit = new MarkdownGraphEditor().Apply(export.Graph,
            markdown.Replace("**Bold**", "**重要**", StringComparison.Ordinal));

        var restore = await adapter.RestoreAsync(export, edit.EditedGraph, output);
        var xml = ReadDocumentXml(output);

        Assert.True(edit.IsValid);
        Assert.True(restore.Succeeded);
        Assert.Contains("w:ascii=\"Yu Mincho\"", xml);
        Assert.Contains("w:eastAsia=\"游明朝\"", xml);
        Assert.Contains("w:val=\"28\"", xml);
        Assert.Contains("w:val=\"1F4E79\"", xml);
        Assert.Contains("w:ascii=\"BIZ UDPGothic\"", xml);
        Assert.Contains("w:spacing w:before=\"120\" w:after=\"80\"", xml);
        Assert.Contains("w:pgSz w:w=\"11906\" w:h=\"16838\"", xml);
        Assert.Contains("w:pgMar w:top=\"1440\" w:right=\"1080\" w:bottom=\"1440\" w:left=\"1080\"", xml);
        using var sourceArchive = ZipFile.OpenRead(source);
        using var outputArchive = ZipFile.OpenRead(output);
        Assert.Equal(await ReadEntryBytesAsync(sourceArchive, "word/styles.xml"), await ReadEntryBytesAsync(outputArchive, "word/styles.xml"));
    }

    [Fact]
    public async Task Strict_export_rejects_zip_path_traversal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "unsafe.docx");
        await using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            await Write(zip, "[Content_Types].xml", "<Types />");
            await Write(zip, "../outside.xml", "not allowed");
            await Write(zip, "word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body /></w:document>");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => new DocxAdapter().ExtractAsync(path).AsTask());
    }

    [Fact]
    public async Task Extracts_grid_span_and_vertical_merge_as_table_cell_spans_without_flattening_the_grid()
    {
        var source = await CreateMergedTableDocxAsync();
        var adapter = new DocxAdapter();

        var export = await adapter.ExtractAsync(source);
        var table = Assert.IsType<TableNodeContent>(export.Graph.Nodes.Single(node => node.Kind == NodeKind.Table).Content);

        // Row shape mirrors the physical tr/tc layout exactly (2, 2, 1 cells) — a merge changes
        // ColSpan/RowSpan metadata, not how many tc elements a row has, so F1 restore's same-shape
        // check still lines up against the original XML.
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(2, table.Rows[0].Count);
        Assert.Equal(2, table.Rows[1].Count);
        Assert.Single(table.Rows[2]);

        Assert.Equal(new TableCell("A1"), table.Rows[0][0]);
        Assert.Equal(new TableCell("B1", RowSpan: 2), table.Rows[0][1]);
        Assert.Equal(new TableCell("A2"), table.Rows[1][0]);
        // The vMerge continuation cell keeps its own (empty) text — the origin cell above carries
        // the real RowSpan count and text; ReadableMarkdownSerializer does the carry-down at
        // render time so this raw model stays a faithful copy of the source XML.
        Assert.Equal(new TableCell(string.Empty, RowSpan: 0), table.Rows[1][1]);
        Assert.Equal(new TableCell("Merged", ColSpan: 2), table.Rows[2][0]);
    }

    [Fact]
    public async Task Extracts_nested_table_as_its_own_sibling_node_excluded_from_the_host_cells_own_text()
    {
        var source = await CreateNestedTableDocxAsync();
        var adapter = new DocxAdapter();

        var export = await adapter.ExtractAsync(source);
        var tables = export.Graph.Nodes.Where(node => node.Kind == NodeKind.Table)
            .Select(node => (Node: node, Content: Assert.IsType<TableNodeContent>(node.Content))).OrderBy(item => item.Node.Order).ToArray();

        Assert.Equal(2, tables.Length);
        var outer = tables[0].Content;
        var inner = tables[1].Content;
        Assert.Equal("Outer label", outer.Rows[0][0].Text);
        // D08: the nested table's own text must not also be embedded in the host cell's text.
        Assert.DoesNotContain("Inner value", outer.Rows[0][0].Text, StringComparison.Ordinal);
        Assert.Equal("Inner label", inner.Rows[0][0].Text);
        Assert.Equal("Inner value", inner.Rows[0][1].Text);
    }

    [Fact]
    public async Task Readable_markdown_folds_a_nested_table_into_its_host_cell_instead_of_a_separate_table()
    {
        var source = await CreateNestedTableDocxAsync();
        var service = new DocumentService();
        var directory = Path.GetDirectoryName(source)!;

        var export = await service.ExportReadableAsync(new ReadableDocumentExportOptions(
            source, Path.Combine(directory, "nested-table-readable.md")));
        var markdown = await File.ReadAllTextAsync(export.MarkdownPath);

        // D08: the nested table is folded into the outer table's host cell by the readability
        // pass instead of appearing as a second, disconnected table with no indication of which
        // cell it came from (see ReadableMarkdownSerializer.FoldNestedTableRows).
        Assert.Contains("Outer label<br>Inner label / Inner value", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Inner label | Inner value", markdown, StringComparison.Ordinal);
        var separatorLines = markdown.Split('\n').Count(line => line.TrimStart().StartsWith('|') && line.Contains("---"));
        Assert.Equal(1, separatorLines);
    }

    [Fact]
    public async Task Wps_drawingml_native_connector_projects_a_visual_graph_and_mermaid_edge()
    {
        var source = await CreateVisualTopologyDocxAsync(WpsShape("start", "START", 0, 0) +
            WpsShape("end", "END", 100, 0) + WpsConnector("native", 0, 0, 100, 0, "start", "end"));
        var extraction = await new DocxAdapter().ExtractAsync(source);
        var visual = VisualGraphOf(extraction);
        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);

        var edge = Assert.Single(visual.Edges);
        Assert.Equal(VisualEdgeResolution.NativeConnection, edge.Resolution);
        Assert.NotNull(edge.SourceId);
        Assert.NotNull(edge.TargetId);
        Assert.True(visual.HasTopology);
        Assert.True(visual.Accounting.IsConsistent);
        Assert.Contains("```mermaid", markdown, StringComparison.Ordinal);
        Assert.Contains(" --> ", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Native_connector_without_geometry_uses_aliases_without_throwing()
    {
        var connector = "<a:cxnSp><a:nvCxnSpPr><a:cNvPr id=\"native-no-geometry\" />" +
            "<a:cNvCxnSpPr><a:stCxn id=\"start\" /><a:endCxn id=\"end\" /></a:cNvCxnSpPr>" +
            "</a:nvCxnSpPr><a:spPr /></a:cxnSp>";
        var source = await CreateVisualTopologyDocxAsync(DrawingShape("start", "START", 0, 0) +
            DrawingShape("end", "END", 100, 0) + connector);

        var extraction = await new DocxAdapter().ExtractAsync(source);
        var edge = Assert.Single(VisualGraphOf(extraction).Edges);

        Assert.Equal(VisualEdgeResolution.NativeConnection, edge.Resolution);
        Assert.NotNull(edge.SourceId);
        Assert.NotNull(edge.TargetId);
    }

    [Fact]
    public async Task Visual_inference_timeout_returns_complete_fallback_without_partial_topology()
    {
        var source = await CreateVisualTopologyDocxAsync(DrawingShape("start", "START", 0, 0) +
            DrawingShape("end", "END", 100, 0) + DrawingConnector("inferred", 10, 10, 100, 10));

        var extraction = await new DocxAdapter { VisualInferenceTimeout = TimeSpan.Zero }.ExtractAsync(source);
        var visual = VisualGraphOf(extraction);

        Assert.DoesNotContain(visual.Edges, edge => edge.SourceId is not null || edge.TargetId is not null);
        var diagnostic = Assert.Single(visual.Diagnostics!, item => item.Code == "VisualInferenceTimeout");
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Fallback));
        Assert.True(visual.Accounting.IsConsistent);
    }

    [Fact]
    public async Task Document_level_visual_graph_connects_shapes_across_paragraph_boundaries()
    {
        var source = await CreateVisualTopologyDocxAsync(
            DrawingShape("start", "START", 0, 0),
            DrawingShape("end", "END", 100, 0),
            DrawingConnector("cross-paragraph", 10, 10, 100, 10));

        var extraction = await new DocxAdapter().ExtractAsync(source);
        var visual = VisualGraphOf(extraction);
        var edge = Assert.Single(visual.Edges);

        Assert.Equal(VisualEdgeResolution.GeometryInferred, edge.Resolution);
        Assert.NotNull(edge.SourceId);
        Assert.NotNull(edge.TargetId);
        Assert.Equal(VisualGraphQuality.HighConfidenceInferred, visual.Quality);
        Assert.True(visual.HasTopology);
    }

    [Fact]
    public async Task Geometry_connector_attaches_unique_label_and_keeps_unadopted_textbox()
    {
        var source = await CreateVisualTopologyDocxAsync(DrawingShape("start", "START", 0, 0) +
            DrawingShape("end", "END", 100, 0) +
            DrawingTextBox("label", "YES", 50, 0) + DrawingTextBox("note", "Explanation remains", 500, 0) +
            DrawingConnector("inferred", 10, 10, 100, 10));
        var extraction = await new DocxAdapter().ExtractAsync(source);
        var visual = VisualGraphOf(extraction);
        var edge = Assert.Single(visual.Edges);
        var labelNode = Assert.Single(extraction.Graph.Nodes, node => node.Kind == NodeKind.TextBox && Text(node) == "YES");
        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);

        Assert.Equal(VisualEdgeResolution.GeometryInferred, edge.Resolution);
        Assert.Equal("YES", edge.Label);
        Assert.Equal("label", labelNode.Extensions!["shape_id"].GetString());
        Assert.True(labelNode.Extensions?.TryGetValue("visual_graph_member", out var marker) == true && marker.GetBoolean());
        Assert.Equal(1, Count(markdown, "YES"));
        Assert.Contains(" -->|YES| ", markdown, StringComparison.Ordinal);
        Assert.Contains("Explanation remains", markdown, StringComparison.Ordinal);
        Assert.True(visual.Accounting.IsConsistent);
    }

    [Fact]
    public async Task Same_text_outside_diagram_is_not_suppressed_by_visual_member_shape_ids()
    {
        var source = await CreateVisualTopologyDocxAsync(DrawingShape("start", "START", 0, 0) +
            DrawingShape("end", "END", 100, 0) + DrawingTextBox("label", "YES", 50, 0) +
            DrawingTextBox("outside", "YES", 600, 600) + DrawingConnector("inferred", 10, 10, 100, 10));
        var extraction = await new DocxAdapter().ExtractAsync(source);
        var visual = VisualGraphOf(extraction);
        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);

        Assert.Equal("YES", Assert.Single(visual.Edges).Label);
        Assert.Equal(2, extraction.Graph.Nodes.Count(node => node.Kind == NodeKind.TextBox && Text(node) == "YES"));
        Assert.Equal(2, Count(markdown, "YES"));
    }

    [Fact]
    public async Task Geometry_connector_label_joins_multiple_textbox_paragraphs_and_is_suppressed_once()
    {
        var source = await CreateVisualTopologyDocxAsync(DrawingShape("start", "START", 0, 0) +
            DrawingShape("end", "END", 100, 0) + DrawingTextBoxParagraphs("label", 50, 0, "YES", "Proceed") +
            DrawingConnector("inferred", 10, 10, 100, 10));
        var extraction = await new DocxAdapter().ExtractAsync(source);
        var visual = VisualGraphOf(extraction);
        var labelNode = Assert.Single(extraction.Graph.Nodes, node => node.Kind == NodeKind.TextBox && node.Extensions?.TryGetValue("shape_id", out var id) == true && id.GetString() == "label");
        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);

        Assert.Equal("YES\nProceed", Assert.IsType<TextNodeContent>(labelNode.Content).Text);
        Assert.Equal("YES\nProceed", Assert.Single(visual.Edges).Label);
        Assert.True(labelNode.Extensions?.TryGetValue("visual_graph_member", out var marker) == true && marker.GetBoolean());
        Assert.Equal(1, Count(markdown, "YES"));

        var edge = Assert.Single(visual.Edges);
        Assert.Equal("label", labelNode.Extensions!["shape_id"].GetString());
        Assert.Equal("YES\nProceed", edge.Label);
    }

    [Fact]
    public async Task Ambiguous_geometry_connector_is_diagnosed_without_inventing_an_edge()
    {
        var source = await CreateVisualTopologyDocxAsync(DrawingShape("first", "FIRST", 0, 0) +
            DrawingShape("competing", "COMPETING", 0, 0) + DrawingShape("end", "END", 100, 0) +
            DrawingConnector("ambiguous", 10, 10, 100, 10));
        var extraction = await new DocxAdapter().ExtractAsync(source);
        var visual = VisualGraphOf(extraction);
        var edge = Assert.Single(visual.Edges);
        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);

        Assert.Equal(new[] { "COMPETING", "END", "FIRST" },
            visual.Nodes.Select(node => node.Label).OrderBy(label => label, StringComparer.Ordinal));
        Assert.Contains("```mermaid\nflowchart", markdown, StringComparison.Ordinal);
        Assert.Contains("[COMPETING]", markdown, StringComparison.Ordinal);
        Assert.Contains("[END]", markdown, StringComparison.Ordinal);
        Assert.Contains("[FIRST]", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(" --> ", markdown, StringComparison.Ordinal);
        Assert.Equal(VisualEdgeResolution.Unresolved, edge.Resolution);
        Assert.Null(edge.SourceId);
        Assert.Null(edge.TargetId);
        var ambiguous = Assert.Single(visual.Diagnostics!, diagnostic => diagnostic.Code == "VisualConnectorAmbiguous");
        var unresolved = Assert.Single(visual.Diagnostics!, diagnostic => diagnostic.Code == "VisualConnectorUnresolved");
        Assert.Equal("docx", ambiguous.Format);
        Assert.Equal("/word/document.xml", ambiguous.PartUri);
        Assert.Equal("part-0001", ambiguous.PartitionId);
        Assert.False(string.IsNullOrWhiteSpace(ambiguous.SourceObjectId));
        Assert.Equal("connector", ambiguous.SourceObjectType);
        Assert.Equal(0d, ambiguous.Confidence);
        Assert.NotNull(ambiguous.LocationSummary);
        Assert.Contains("format=docx", ambiguous.LocationSummary!, StringComparison.Ordinal);
        Assert.Contains("part=/word/document.xml", ambiguous.LocationSummary!, StringComparison.Ordinal);
        Assert.Contains("source_type=connector", ambiguous.LocationSummary!, StringComparison.Ordinal);
        Assert.Equal("docx", unresolved.Format);
        Assert.Equal("part-0001", unresolved.PartitionId);
        Assert.False(string.IsNullOrWhiteSpace(unresolved.SourceObjectId));
        Assert.False(visual.HasTopology);
        Assert.True(visual.Accounting.IsConsistent);
    }

    [Fact]
    public async Task Vml_line_from_to_projects_native_topology()
    {
        var source = await CreateVisualTopologyDocxAsync(VmlShape("left", "LEFT", 0, 0) + VmlShape("right", "RIGHT", 100, 0) +
            "<v:shape id=\"line\" type=\"#line\" from=\"#left\" to=\"#right\" style=\"margin-left:0pt;margin-top:10pt;width:100pt;height:1pt\" />");
        var extraction = await new DocxAdapter().ExtractAsync(source);
        var visual = VisualGraphOf(extraction);
        var edge = Assert.Single(visual.Edges);

        Assert.Equal(VisualEdgeResolution.NativeConnection, edge.Resolution);
        Assert.NotNull(edge.SourceId);
        Assert.NotNull(edge.TargetId);
        Assert.True(visual.HasTopology);
        Assert.True(visual.Accounting.IsConsistent);
    }

    [Fact]
    public async Task Duplicate_visual_ids_do_not_throw_and_leave_invalid_topology_unsuppressed()
    {
        var source = await CreateVisualTopologyDocxAsync(DrawingShape("same", "FIRST", 0, 0) + DrawingShape("same", "SECOND", 100, 0) +
            DrawingConnector("duplicate", 0, 0, 100, 0, "same", "same"));
        var extraction = await new DocxAdapter().ExtractAsync(source);
        var visual = VisualGraphOf(extraction);

        Assert.False(visual.HasTopology);
        Assert.True(visual.Accounting.IsConsistent);
    }

    private static VisualGraph VisualGraphOf(DocxExtractionResult extraction)
    {
        var node = Assert.Single(extraction.Graph.Nodes, candidate => candidate.Kind == NodeKind.Diagram && candidate.Extensions?.ContainsKey("visual_graph") == true);
        return node.Extensions!["visual_graph"].Deserialize<VisualGraph>()!;
    }

    private static string DrawingShape(string id, string text, int x, int y) =>
        $"<a:sp><a:nvSpPr><a:cNvPr id=\"{id}\" /></a:nvSpPr><a:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\" /><a:ext cx=\"20\" cy=\"20\" /></a:xfrm></a:spPr><w:r><w:t>{text}</w:t></w:r></a:sp>";

    private static string DrawingTextBox(string id, string text, int x, int y) =>
        $"<a:sp><a:nvSpPr><a:cNvPr id=\"{id}\" /></a:nvSpPr><a:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\" /><a:ext cx=\"20\" cy=\"20\" /></a:xfrm></a:spPr><w:txbxContent><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:txbxContent></a:sp>";

    private static string DrawingTextBoxParagraphs(string id, int x, int y, params string[] paragraphs) =>
        $"<a:sp><a:nvSpPr><a:cNvPr id=\"{id}\" /></a:nvSpPr><a:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\" /><a:ext cx=\"20\" cy=\"20\" /></a:xfrm></a:spPr><w:txbxContent>{string.Concat(paragraphs.Select(text => $"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>"))}</w:txbxContent></a:sp>";

    private static string WpsShape(string id, string text, int x, int y) =>
        $"<wps:wsp><a:cNvPr id=\"{id}\" /><a:xfrm><a:off x=\"{x}\" y=\"{y}\" /><a:ext cx=\"20\" cy=\"20\" /></a:xfrm><w:txbxContent><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:txbxContent></wps:wsp>";

    private static string WpsConnector(string id, int x, int y, int width, int height, string start, string end) =>
        $"<wps:wsp><a:cNvPr id=\"{id}\" /><a:prstGeom prst=\"line\" /><a:stCxn id=\"{start}\" /><a:endCxn id=\"{end}\" /><a:xfrm><a:off x=\"{x}\" y=\"{y}\" /><a:ext cx=\"{width}\" cy=\"{height}\" /></a:xfrm></wps:wsp>";

    private static string DrawingConnector(string id, int x, int y, int width, int height, string? start = null, string? end = null)
    {
        var connections = (start is null ? string.Empty : $"<a:stCxn id=\"{start}\" />") +
            (end is null ? string.Empty : $"<a:endCxn id=\"{end}\" />");
        return $"<a:cxnSp><a:nvCxnSpPr><a:cNvPr id=\"{id}\" /><a:cNvCxnSpPr>{connections}</a:cNvCxnSpPr></a:nvCxnSpPr><a:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\" /><a:ext cx=\"{width}\" cy=\"{height}\" /></a:xfrm></a:spPr></a:cxnSp>";
    }

    private static string VmlShape(string id, string text, int x, int y) =>
        $"<v:shape id=\"{id}\" style=\"margin-left:{x}pt;margin-top:{y}pt;width:20pt;height:20pt\"><v:textbox><w:txbxContent><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:txbxContent></v:textbox></v:shape>";

    private static async Task<string> CreateVisualTopologyDocxAsync(params string[] visualParagraphs)
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "visual-topology.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        var body = string.Concat(visualParagraphs.Select((visualContent, index) =>
            $"<w:p w14:paraId=\"{index + 1:X8}\"><w:r><w:drawing>{visualContent}</w:drawing></w:r></w:p>"));
        await Write(zip, "word/document.xml", $"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:w14=\"http://schemas.microsoft.com/office/word/2010/wordml\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" xmlns:v=\"urn:schemas-microsoft-com:vml\"><w:body>{body}</w:body></w:document>");
        return path;
    }

    private static async Task<string> CreateMergedTableDocxAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "merged-table.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await Write(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
            <w:tbl>
              <w:tr>
                <w:tc><w:p><w:r><w:t>A1</w:t></w:r></w:p></w:tc>
                <w:tc><w:tcPr><w:vMerge w:val="restart"/></w:tcPr><w:p><w:r><w:t>B1</w:t></w:r></w:p></w:tc>
              </w:tr>
              <w:tr>
                <w:tc><w:p><w:r><w:t>A2</w:t></w:r></w:p></w:tc>
                <w:tc><w:tcPr><w:vMerge/></w:tcPr><w:p /></w:tc>
              </w:tr>
              <w:tr>
                <w:tc><w:tcPr><w:gridSpan w:val="2"/></w:tcPr><w:p><w:r><w:t>Merged</w:t></w:r></w:p></w:tc>
              </w:tr>
            </w:tbl>
            </w:body></w:document>
            """);
        return path;
    }

    [Fact]
    public async Task Nested_table_between_two_cell_paragraphs_keeps_its_position_in_readable_markdown()
    {
        var source = await CreateNestedTableBetweenParagraphsDocxAsync();
        var export = await new DocxAdapter().ExtractAsync(source);
        var tables = export.Graph.Nodes.Where(node => node.Kind == NodeKind.Table).ToArray();
        var outer = tables.Single(node => node.Extensions is null || !node.Extensions.ContainsKey("nested_table_parent"));
        var inner = tables.Single(node => node.Extensions is not null && node.Extensions.TryGetValue("nested_table_paragraph_offset", out var offset) && offset.GetInt32() == 1);
        var trailing = tables.Single(node => node.Extensions is not null && node.Extensions.TryGetValue("nested_table_paragraph_offset", out var offset) && offset.GetInt32() == 2);

        Assert.Equal("Before\nSecond line\nAfter", ((TableNodeContent)outer.Content).Rows[0][0].Text);
        Assert.Equal("Inner label", ((TableNodeContent)inner.Content).Rows[0][0].Text);
        Assert.Equal(1, inner.Extensions!["nested_table_paragraph_offset"].GetInt32());
        // The first paragraph holds a w:br, so it occupies two lines of the cell text.
        Assert.Equal(2, inner.Extensions!["nested_table_line_offset"].GetInt32());
        Assert.Equal("Tail label", ((TableNodeContent)trailing.Content).Rows[0][0].Text);
        Assert.Equal(3, trailing.Extensions!["nested_table_line_offset"].GetInt32());

        var readable = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(
            source, Path.Combine(Path.GetDirectoryName(source)!, "nested-between-readable.md")));
        var markdown = await File.ReadAllTextAsync(readable.MarkdownPath);

        Assert.Contains("Before<br>Second line<br>Inner label / Inner value<br>After<br>Tail label / Tail value", markdown, StringComparison.Ordinal);
    }

    private static async Task<string> CreateNestedTableBetweenParagraphsDocxAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "nested-between.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await Write(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
            <w:tbl>
              <w:tr>
                <w:tc>
                  <w:p><w:r><w:t>Before</w:t><w:br /><w:t>Second line</w:t></w:r></w:p>
                  <w:tbl>
                    <w:tr>
                      <w:tc><w:p><w:r><w:t>Inner label</w:t></w:r></w:p></w:tc>
                      <w:tc><w:p><w:r><w:t>Inner value</w:t></w:r></w:p></w:tc>
                    </w:tr>
                  </w:tbl>
                  <w:p><w:r><w:t>After</w:t></w:r></w:p>
                  <w:tbl>
                    <w:tr>
                      <w:tc><w:p><w:r><w:t>Tail label</w:t></w:r></w:p></w:tc>
                      <w:tc><w:p><w:r><w:t>Tail value</w:t></w:r></w:p></w:tc>
                    </w:tr>
                  </w:tbl>
                  <w:p />
                </w:tc>
                <w:tc><w:p><w:r><w:t>Right</w:t></w:r></w:p></w:tc>
              </w:tr>
            </w:tbl>
            <w:p><w:r><w:t>Body</w:t></w:r></w:p><w:sectPr />
            </w:body></w:document>
            """);
        return path;
    }

    private static async Task<string> CreateNestedTableDocxAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "nested-table.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await Write(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
            <w:tbl>
              <w:tr>
                <w:tc>
                  <w:p><w:r><w:t>Outer label</w:t></w:r></w:p>
                  <w:tbl>
                    <w:tr>
                      <w:tc><w:p><w:r><w:t>Inner label</w:t></w:r></w:p></w:tc>
                      <w:tc><w:p><w:r><w:t>Inner value</w:t></w:r></w:p></w:tc>
                    </w:tr>
                  </w:tbl>
                  <w:p />
                </w:tc>
              </w:tr>
            </w:tbl>
            </w:body></w:document>
            """);
        return path;
    }

    [Fact]
    public async Task Explicit_page_break_is_excluded_from_paragraph_text_while_an_ordinary_break_still_becomes_a_line_break()
    {
        var source = await CreateMixedBreakDocxAsync();
        var adapter = new DocxAdapter();

        var export = await adapter.ExtractAsync(source);

        // D18 (coordinator-adjudicated): the page-break-only paragraph no longer carries the
        // break as a LineBreak run/newline itself — a separate NodeKind.PageBreak marker node is
        // now its sole representation, so the paragraph's own text is empty.
        var pageBreakParagraph = export.Graph.Nodes.Single(node => node.StyleId == "PageBreakOnly");
        Assert.Equal("", Text(pageBreakParagraph));
        Assert.Contains(export.Graph.Nodes, node => node.Kind == NodeKind.PageBreak && node.ParentId == pageBreakParagraph.Id);

        // An ordinary (non-page) w:br is unaffected and still becomes a real LineBreak run.
        var wrappedParagraph = export.Graph.Nodes.Single(node => node.StyleId == "OrdinaryBreak");
        var rich = Assert.IsType<RichTextNodeContent>(wrappedParagraph.Content);
        Assert.Contains(rich.Runs, run => run.Kind == TextRunKind.LineBreak);
        Assert.Contains(rich.Runs, run => run.Text == "Before");
        Assert.Contains(rich.Runs, run => run.Text == "After");
    }

    private static async Task<string> CreateMixedBreakDocxAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "mixed-break.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await Write(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
            <w:p><w:pPr><w:pStyle w:val="PageBreakOnly" /></w:pPr><w:r><w:br w:type="page" /></w:r></w:p>
            <w:p><w:pPr><w:pStyle w:val="OrdinaryBreak" /></w:pPr><w:r><w:t>Before</w:t><w:br /><w:t>After</w:t></w:r></w:p>
            </w:body></w:document>
            """);
        return path;
    }

    [Fact]
    public async Task Preserves_heading_level_and_classifies_code_style()
    {
        var source = await CreateDocxAsync();
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);

        var heading = Assert.Single(export.Graph.Nodes, node => node.StyleId == "Heading 2");
        var code = Assert.Single(export.Graph.Nodes, node => node.StyleId == "Code");
        Assert.Equal(NodeKind.Heading, heading.Kind);
        Assert.Equal(2, heading.Extensions!["heading_level"].GetInt32());
        Assert.Equal(NodeKind.CodeBlock, code.Kind);
    }

    [Fact]
    public async Task Style_resolved_vanish_hides_text_exactly_like_a_direct_vanish()
    {
        var source = await CreateStyleHiddenDocxAsync();
        var adapter = new DocxAdapter();

        var export = await adapter.ExtractAsync(source);

        // (a) w:rStyle -> a character style carrying w:vanish.
        var characterStyled = Assert.Single(export.Graph.Nodes, node => node.StyleId == "VisiblePara");
        Assert.Equal("Visible one", NodeText(characterStyled));
        Assert.DoesNotContain(CharacterStyleSecret, NodeText(characterStyled), StringComparison.Ordinal);
        Assert.Equal(CharacterStyleSecret, HiddenAnnotationText(export.Graph, characterStyled.Id));

        // (b) w:pStyle -> a paragraph style carrying w:vanish. Every run is hidden, so the
        // paragraph itself drops to the Hidden layer and only the annotation carries the text.
        var paragraphStyled = Assert.Single(export.Graph.Nodes, node => node.StyleId == "HiddenPara");
        Assert.Equal(string.Empty, NodeText(paragraphStyled));
        Assert.Equal(ContentLayer.Hidden, paragraphStyled.Layer);
        Assert.Equal(ParagraphStyleSecret, HiddenAnnotationText(export.Graph, paragraphStyled.Id));

        // (c) w:basedOn -> the vanish is inherited from the base style, not declared locally.
        var inherited = Assert.Single(export.Graph.Nodes, node => node.StyleId == "DerivedHiddenPara");
        Assert.Equal(string.Empty, NodeText(inherited));
        Assert.Equal(ContentLayer.Hidden, inherited.Layer);
        Assert.Equal(BasedOnSecret, HiddenAnnotationText(export.Graph, inherited.Id));

        // (d) the nearer level wins: a run-level w:vanish w:val="0" cancels the style's hide.
        var cancelled = Assert.Single(export.Graph.Nodes, node => node.StyleId == "CancelPara");
        Assert.Equal("Cancelled visible", NodeText(cancelled));
        Assert.DoesNotContain(export.Graph.Nodes, node => node.ParentId == cancelled.Id && node.Kind == NodeKind.Annotation);
    }

    [Fact]
    public async Task Style_hidden_text_is_excluded_from_visible_markdown_and_kept_under_complete()
    {
        var source = await CreateStyleHiddenDocxAsync();
        var service = new DocumentService();
        var directory = Path.GetDirectoryName(source)!;

        var visible = await service.ExportReadableAsync(new ReadableDocumentExportOptions(
            source, Path.Combine(directory, "style-hidden-visible.md"), ContentPolicy: "visible"));
        var sanitized = await service.ExportReadableAsync(new ReadableDocumentExportOptions(
            source, Path.Combine(directory, "style-hidden-sanitized.md"), ContentPolicy: "sanitized"));
        var complete = await service.ExportReadableAsync(new ReadableDocumentExportOptions(
            source, Path.Combine(directory, "style-hidden-complete.md"), ContentPolicy: "complete"));

        foreach (var secret in new[] { CharacterStyleSecret, ParagraphStyleSecret, BasedOnSecret })
        {
            Assert.DoesNotContain(secret, await File.ReadAllTextAsync(visible.MarkdownPath), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, await File.ReadAllTextAsync(sanitized.MarkdownPath), StringComparison.Ordinal);
            // F-Issue7: the "complete" policy renders this as a blockquote, and its plain-text "_"
            // is now backslash-escaped (e.g. "DOCREDOCK\_SECRET\_..."), so compare against a
            // de-escaped copy of the file instead of the literal secret string.
            var completeMarkdown = (await File.ReadAllTextAsync(complete.MarkdownPath)).Replace("\\", string.Empty, StringComparison.Ordinal);
            Assert.Contains(secret, completeMarkdown, StringComparison.Ordinal);
        }
        Assert.Contains("Cancelled visible", await File.ReadAllTextAsync(visible.MarkdownPath), StringComparison.Ordinal);
        Assert.Contains(visible.Diagnostics, item => item.Code == "DocxHiddenTextExcluded");
    }

    [Fact]
    public async Task Block_content_control_content_is_extracted_in_order_and_f0_restore_stays_byte_identical()
    {
        var source = await CreateContentControlDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "sdt-unchanged.docx");
        var adapter = new DocxAdapter();

        var export = await adapter.ExtractAsync(source);
        var restore = await adapter.RestoreAsync(export, export.Graph, output);
        var body = export.Graph.Nodes
            .Where(node => node.Kind is NodeKind.Paragraph or NodeKind.Table && node.Layer == ContentLayer.Body)
            .OrderBy(node => node.Order).ToArray();

        Assert.Equal(
            ["Before control", "Inside one", "Inside two", string.Empty, "After control"],
            body.Select(node => node.Kind == NodeKind.Table ? string.Empty : NodeText(node)).ToArray());
        var control = body[1..4];
        Assert.All(control, node => Assert.True(node.Extensions!["content_control"].GetBoolean()));
        Assert.All(control, node => Assert.Equal("Section A", node.Extensions!["sdt_alias"].GetString()));
        Assert.All(control, node => Assert.Equal("sectionA", node.Extensions!["sdt_tag"].GetString()));
        // A w:sdt is a wrapper, not content: the blocks it holds occupy body positions and are
        // sliced at their own boundaries, so they are ordinary F1 targets. (Before, the scanner
        // recorded only body-direct blocks, and a control's content was projected read-only.)
        Assert.Equal(
            [NodeEditability.EditableInPlace, NodeEditability.EditableInPlace, NodeEditability.EditableWithConstraints],
            control.Select(node => node.Editability).ToArray());
        Assert.All(control, node => Assert.NotNull(node.RawSlice));
        // Each slice covers only its own block; the w:sdt/w:sdtPr wrapping is outside all of them.
        Assert.All(control, node => Assert.DoesNotContain("w:sdt", SliceText(source, node.RawSlice!), StringComparison.Ordinal));
        Assert.Equal("Control cell", Assert.IsType<TableNodeContent>(body[3].Content).Rows[0][0].Text);
        Assert.DoesNotContain(body[0].Extensions ?? new Dictionary<string, JsonElement>(), item => item.Key == "content_control");
        Assert.True(restore.Succeeded);
        Assert.Equal(Hash(source), Hash(output));
    }

    [Fact]
    public async Task F1_still_patches_a_paragraph_outside_a_block_content_control()
    {
        var source = await CreateContentControlDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "sdt-changed.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => NodeText(node) == "After control");
        var editedNodes = export.Graph.Nodes
            .Select(node => node.Id == target.Id ? node with { Content = new TextNodeContent("After edit") } : node).ToArray();
        var edited = export.Graph with { Partitions = [new DocumentPartition("part-0001", 0, editedNodes, "/word/document.xml")] };

        var result = await adapter.RestoreAsync(export, edited, output);
        var reexport = await adapter.ExtractAsync(output);

        Assert.True(result.Succeeded);
        Assert.Equal("After edit", NodeText(reexport.Graph.Nodes.Single(node => node.Id == target.Id)));
        Assert.Contains(reexport.Graph.Nodes, node => NodeText(node) == "Inside one");
        Assert.Contains(reexport.Graph.Nodes, node => NodeText(node) == "Inside two");
    }

    [Theory]
    // The control's own paragraph, the nested control's paragraph, and a paragraph outside both:
    // all three are now sliced at their own boundaries, so each patches on its own.
    [InlineData("Inside one", "Inside edited")]
    [InlineData("Inside two", "Nested edited")]
    [InlineData("Before control", "Before edited")]
    public async Task F1_patches_a_paragraph_inside_a_block_content_control_and_leaves_the_wrapping_byte_identical(
        string original, string replacement)
    {
        var source = await CreateContentControlDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, $"sdt-{replacement}.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => NodeText(node) == original);
        var (before, after) = SliceSurroundings(source, target.RawSlice!);

        var result = await EditAsync(adapter, export, source, output, target.Id, new TextNodeContent(replacement));
        var patched = ReadDocumentXml(output);
        var reexport = await adapter.ExtractAsync(output);

        Assert.True(result.Succeeded);
        Assert.Equal(replacement, NodeText(reexport.Graph.Nodes.Single(node => node.Id == target.Id)));
        // The w:sdt / w:sdtPr / w:sdtContent wrapping and every sibling block sit outside the
        // slice, so the patch cannot have touched a byte of them.
        Assert.StartsWith(before, patched, StringComparison.Ordinal);
        Assert.EndsWith(after, patched, StringComparison.Ordinal);
        Assert.Contains("<w:sdtPr><w:alias w:val=\"Section A\" /><w:tag w:val=\"sectionA\" /></w:sdtPr>", patched, StringComparison.Ordinal);
        Assert.Equal(2, Count(patched, "<w:sdtContent>"));
        // Only the edited paragraph changed; the control's other blocks re-extract unchanged.
        Assert.Equal(
            new[] { "Before control", "Inside one", "Inside two", "After control" }.Select(text => text == original ? replacement : text).ToArray(),
            reexport.Graph.Nodes.Where(node => node.Kind == NodeKind.Paragraph && node.Layer == ContentLayer.Body)
                .OrderBy(node => node.Order).Select(NodeText).ToArray());
    }

    [Fact]
    public async Task F1_cell_edit_inside_a_block_content_control_keeps_the_control_and_its_siblings()
    {
        var source = await CreateContentControlDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "sdt-table-changed.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = Assert.Single(export.Graph.Nodes, node => node.Kind == NodeKind.Table);
        var (before, after) = SliceSurroundings(source, target.RawSlice!);

        var result = await EditAsync(adapter, export, source, output, target.Id,
            new TableNodeContent([new TableCell[] { "Control edited" }]));
        var patched = ReadDocumentXml(output);
        var reexport = await adapter.ExtractAsync(output);

        Assert.True(result.Succeeded);
        Assert.Equal("Control edited",
            Assert.IsType<TableNodeContent>(reexport.Graph.Nodes.Single(node => node.Id == target.Id).Content).Rows[0][0].Text);
        Assert.True(reexport.Graph.Nodes.Single(node => node.Id == target.Id).Extensions!["content_control"].GetBoolean());
        Assert.StartsWith(before, patched, StringComparison.Ordinal);
        Assert.EndsWith(after, patched, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Native_omml_equations_are_linearized_into_paragraph_text_and_diagnosed_once()
    {
        var source = await CreateMathDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "math-unchanged.docx");
        var adapter = new DocxAdapter();

        var export = await adapter.ExtractAsync(source);
        var restore = await adapter.RestoreAsync(export, export.Graph, output);

        var inline = Assert.Single(export.Graph.Nodes, node => node.StyleId == "MathPara");
        // m:sSup -> base^sup (single-character sup needs no grouping); m:f -> (num)/(den).
        Assert.Equal("E=mc^2 and (a+b)/2", NodeText(inline));
        Assert.True(inline.Extensions!["math_linear"].GetBoolean());
        // OMML cannot be regenerated from linear text, but it does not have to be: the equation is
        // kept as an anchor and only the runs around it are rewritten, so the host paragraph is an
        // ordinary F1 target again. (Before, an equation made the whole paragraph read-only.)
        Assert.Equal(NodeEditability.EditableInPlace, inline.Editability);
        Assert.NotNull(inline.RawSlice);

        var block = Assert.Single(export.Graph.Nodes, node =>
            node.Kind == NodeKind.Paragraph && NodeText(node) == "√(x)");
        Assert.True(block.Extensions!["math_linear"].GetBoolean());
        // A block-level equation's slice is the m:oMathPara element itself.
        Assert.Equal(NodeEditability.EditableInPlace, block.Editability);
        Assert.StartsWith("<m:oMathPara", SliceText(source, block.RawSlice!), StringComparison.Ordinal);

        // A table cell's equation is linearized too, and the cell rewrite keeps it as an anchor,
        // so the table stays editable rather than being read-only for holding an equation.
        var table = Assert.Single(export.Graph.Nodes, node => node.Kind == NodeKind.Table);
        Assert.Equal("a_n", Assert.IsType<TableNodeContent>(table.Content).Rows[0][0].Text);
        Assert.Equal(NodeEditability.EditableWithConstraints, table.Editability);
        Assert.NotNull(table.RawSlice);

        // One document-level notice covering all four equations, not one per equation.
        var diagnostic = Assert.Single(export.Diagnostics, item => item.Code == "DocxMathLinearized");
        Assert.Equal("4 equation(s) were converted to linear text; layout may differ from the original.", diagnostic.Message);
        Assert.True(restore.Succeeded);
        Assert.Equal(Hash(source), Hash(output));
    }

    [Fact]
    public async Task F1_edit_around_an_equation_keeps_the_omml_in_its_original_position()
    {
        var source = await CreateEditableMathDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "math-around.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => NodeText(node) == "Given x^2 holds.");
        var (before, after) = SliceSurroundings(source, target.RawSlice!);

        // The equation run comes back unchanged; only the text on either side of it was edited.
        var result = await EditAsync(adapter, export, source, output, target.Id, new RichTextNodeContent(
            [new TextRun("Because "), new TextRun("x^2", Code: true), new TextRun(" still holds.")]));
        var patched = ReadDocumentXml(output);
        var reexport = await adapter.ExtractAsync(output);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "DocxMathReplaced");
        Assert.Equal("Because x^2 still holds.", NodeText(reexport.Graph.Nodes.Single(node => node.Id == target.Id)));
        // The equation was kept as markup, not re-emitted: the part holds exactly as many m:oMath
        // elements as the original and none of them was flattened into its own linear text.
        // (The rewritten slice redeclares the m prefix locally, hence the open-tag prefix match.)
        Assert.Equal(Count(ReadDocumentXml(source), "<m:oMath"), Count(patched, "<m:oMath"));
        Assert.DoesNotContain("x^2", patched, StringComparison.Ordinal);
        // The tail text lands *after* the equation. Rewriting all runs from the pPr - what the
        // patcher did before anchors existed - would have put it in front of the m:oMath.
        Assert.DoesNotContain(" still holds.", patched[..patched.IndexOf("<m:oMath", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.StartsWith(before, patched, StringComparison.Ordinal);
        Assert.EndsWith(after, patched, StringComparison.Ordinal);
    }

    [Fact]
    public async Task F1_edit_that_deletes_an_equation_drops_the_omml_and_warns_once()
    {
        var source = await CreateEditableMathDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "math-deleted.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => NodeText(node) == "Given x^2 holds.");

        var result = await EditAsync(adapter, export, source, output, target.Id, new TextNodeContent("Given nothing holds."));
        var patched = ReadDocumentXml(output);
        var reexport = await adapter.ExtractAsync(output);

        Assert.True(result.Succeeded);
        Assert.Equal("Given nothing holds.", NodeText(reexport.Graph.Nodes.Single(node => node.Id == target.Id)));
        // The paragraph's equation is gone; the table cell's is untouched.
        Assert.Equal(Count(ReadDocumentXml(source), "<m:oMath") - 1, Count(patched, "<m:oMath"));
        Assert.DoesNotContain("math_linear", reexport.Graph.Nodes.Single(node => node.Id == target.Id).Extensions!.Keys);
        var warning = Assert.Single(result.Diagnostics, item => item.Code == "DocxMathReplaced");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(target.Id, warning.NodeId);
        Assert.Contains($"1 equation(s) on paragraph {target.Id} were replaced by plain text", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task F1_table_cell_edit_preserves_an_equation_in_a_sibling_cell()
    {
        var source = await CreateEditableMathDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "math-cell.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = Assert.Single(export.Graph.Nodes, node => node.Kind == NodeKind.Table);

        // Only the second cell changes; the first still carries the equation's linear form, so its
        // OMML must survive the rewrite rather than being flattened into a w:t.
        var result = await EditAsync(adapter, export, source, output, target.Id,
            new TableNodeContent([new TableCell[] { "a_n", "Noted" }]));
        var patched = ReadDocumentXml(output);
        var cells = Assert.IsType<TableNodeContent>(
            (await adapter.ExtractAsync(output)).Graph.Nodes.Single(node => node.Id == target.Id).Content).Rows[0];

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "DocxMathReplaced");
        Assert.Equal(["a_n", "Noted"], cells.Select(cell => cell.Text).ToArray());
        Assert.Equal(Count(ReadDocumentXml(source), "<m:oMath"), Count(patched, "<m:oMath"));
        Assert.DoesNotContain("<w:t>a_n</w:t>", patched, StringComparison.Ordinal);
    }

    [Fact]
    public async Task F1_edit_of_a_block_level_equation_replaces_it_with_a_paragraph_and_warns()
    {
        var source = await CreateMathDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "math-block-changed.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => node.Kind == NodeKind.Paragraph && NodeText(node) == "√(x)");
        var (before, after) = SliceSurroundings(source, target.RawSlice!);

        var result = await EditAsync(adapter, export, source, output, target.Id, new TextNodeContent("sqrt of x"));
        var patched = ReadDocumentXml(output);

        Assert.True(result.Succeeded);
        // A block-level equation's slice is the OMML itself: there is no w:p to rewrite, so the
        // whole equation becomes an ordinary paragraph and the loss of markup is reported.
        Assert.Equal("sqrt of x", NodeText((await adapter.ExtractAsync(output)).Graph.Nodes.Single(node => node.Id == target.Id)));
        Assert.DoesNotContain("<m:oMathPara", patched, StringComparison.Ordinal);
        Assert.Single(result.Diagnostics, item => item.Code == "DocxMathReplaced");
        Assert.StartsWith(before, patched, StringComparison.Ordinal);
        Assert.EndsWith(after, patched, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_self_closing_paragraph_takes_its_own_slice_and_does_not_shift_the_next_block()
    {
        var source = await CreateSelfClosingParagraphDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "self-closing-changed.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var body = export.Graph.Nodes.Where(node => node.Kind == NodeKind.Paragraph && node.Layer != ContentLayer.Hidden)
            .OrderBy(node => node.Order).ToArray();
        var target = export.Graph.Nodes.Single(node => NodeText(node) == "Tail");
        var (before, after) = SliceSurroundings(source, target.RawSlice!);

        var result = await EditAsync(adapter, export, source, output, target.Id, new TextNodeContent("Tail edited"));
        var patched = ReadDocumentXml(output);

        // <w:p /> closes at its own tag. It used to stay open in the scanner's ledger, which
        // swallowed the following paragraph's slice along with its own.
        Assert.Equal(["Head", "Tail"], body.Select(NodeText).ToArray());
        Assert.All(body, node => Assert.NotNull(node.RawSlice));
        Assert.Equal("<w:p />", SliceText(source, export.Graph.Nodes
            .Single(node => node.Kind == NodeKind.Paragraph && node.Layer == ContentLayer.Hidden).RawSlice!));
        Assert.True(result.Succeeded);
        Assert.StartsWith(before, patched, StringComparison.Ordinal);
        Assert.EndsWith(after, patched, StringComparison.Ordinal);
        Assert.DoesNotContain(export.Diagnostics, item => item.Code == "DocxSliceLedgerMismatch");
    }

    [Fact]
    public async Task F0_restore_of_the_editable_equation_fixture_is_byte_identical()
    {
        var source = await CreateEditableMathDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "math-editable-unchanged.docx");
        var adapter = new DocxAdapter();

        var export = await adapter.ExtractAsync(source);
        var restore = await adapter.RestoreAsync(export, export.Graph, output);

        Assert.True(restore.Succeeded);
        Assert.Equal(Hash(source), Hash(output));
        Assert.All(export.Graph.Nodes.Where(node => node.Kind is NodeKind.Paragraph or NodeKind.Table && node.Layer == ContentLayer.Body),
            node => Assert.NotNull(node.RawSlice));
    }

    [Fact]
    public async Task Table_cell_keeps_paragraph_boundaries_and_nested_tables_record_their_host_cell()
    {
        var source = await CreateMultiParagraphCellDocxAsync();
        var adapter = new DocxAdapter();

        var export = await adapter.ExtractAsync(source);
        var tables = export.Graph.Nodes.Where(node => node.Kind == NodeKind.Table).OrderBy(node => node.Order).ToArray();
        var outer = Assert.IsType<TableNodeContent>(tables[0].Content);

        // Three paragraphs in one cell keep their boundaries; the readable serializer turns each
        // "\n" into <br> rather than gluing the paragraphs into "ABC".
        Assert.Equal("A\nB\nC", outer.Rows[0][0].Text);
        // A host cell still carries only its own paragraphs, and its trailing empty paragraph
        // (Word always leaves one after a nested table) does not add a blank line.
        Assert.Equal("Host", outer.Rows[0][1].Text);
        Assert.Equal("Wrapped host", outer.Rows[0][2].Text);

        Assert.Equal(3, tables.Length);
        Assert.Equal(tables[0].Id, tables[1].Extensions!["nested_table_parent"].GetString());
        Assert.Equal(0, tables[1].Extensions!["nested_table_row"].GetInt32());
        Assert.Equal(1, tables[1].Extensions!["nested_table_column"].GetInt32());
        Assert.Equal("Nested", Assert.IsType<TableNodeContent>(tables[1].Content).Rows[0][0].Text);
        // A nested table wrapped in a block content control is found too — the old
        // tc.Elements(w:tbl) scan looked straight through neither the w:sdt nor its w:sdtContent.
        Assert.Equal(tables[0].Id, tables[2].Extensions!["nested_table_parent"].GetString());
        Assert.Equal(2, tables[2].Extensions!["nested_table_column"].GetInt32());
        Assert.Equal("Wrapped nested", Assert.IsType<TableNodeContent>(tables[2].Content).Rows[0][0].Text);
    }

    [Fact]
    public async Task F1_table_edit_rewrites_only_the_hosts_own_paragraphs_and_leaves_the_nested_table_intact()
    {
        var source = await CreateMultiParagraphCellDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "nested-host-changed.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var outerNode = export.Graph.Nodes.Where(node => node.Kind == NodeKind.Table).OrderBy(node => node.Order).First();
        var rows = Assert.IsType<TableNodeContent>(outerNode.Content).Rows;
        var editedRows = new[] { new[] { new TableCell("A\nB\nZ"), rows[0][1], rows[0][2] } };
        var editedNodes = export.Graph.Nodes
            .Select(node => node.Id == outerNode.Id ? node with { Content = new TableNodeContent(editedRows) } : node).ToArray();
        var edited = export.Graph with { Partitions = [new DocumentPartition("part-0001", 0, editedNodes, "/word/document.xml")] };

        var result = await adapter.RestoreAsync(export, edited, output);
        var reexport = await adapter.ExtractAsync(output);
        var tables = reexport.Graph.Nodes.Where(node => node.Kind == NodeKind.Table).OrderBy(node => node.Order).ToArray();

        Assert.True(result.Succeeded);
        Assert.Equal("A\nB\nZ", Assert.IsType<TableNodeContent>(tables[0].Content).Rows[0][0].Text);
        // The nested table's own runs live inside the host cell's XML subtree; the cell rewrite
        // must not blank them, and must not copy them into the host cell either.
        Assert.Equal(3, tables.Length);
        Assert.Equal("Nested", Assert.IsType<TableNodeContent>(tables[1].Content).Rows[0][0].Text);
        Assert.Equal("Wrapped nested", Assert.IsType<TableNodeContent>(tables[2].Content).Rows[0][0].Text);
        Assert.Equal("Host", Assert.IsType<TableNodeContent>(tables[0].Content).Rows[0][1].Text);
        Assert.Equal(1, Count(ReadDocumentXml(output), "<w:t>Nested</w:t>"));
    }

    private const string CharacterStyleSecret = "DOCREDOCK_SECRET_CHARACTER_STYLE";
    private const string ParagraphStyleSecret = "DOCREDOCK_SECRET_PARAGRAPH_STYLE";
    private const string BasedOnSecret = "DOCREDOCK_SECRET_BASED_ON_STYLE";

    private static async Task<string> CreateStyleHiddenDocxAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "style-hidden.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await Write(zip, "word/styles.xml", """
            <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:docDefaults><w:rPrDefault><w:rPr /></w:rPrDefault></w:docDefaults>
              <w:style w:type="paragraph" w:default="1" w:styleId="Normal" />
              <w:style w:type="paragraph" w:styleId="VisiblePara" />
              <w:style w:type="character" w:styleId="HiddenChar"><w:rPr><w:vanish /></w:rPr></w:style>
              <w:style w:type="paragraph" w:styleId="HiddenPara"><w:rPr><w:vanish /></w:rPr></w:style>
              <w:style w:type="paragraph" w:styleId="DerivedHiddenPara"><w:basedOn w:val="HiddenPara" /></w:style>
              <w:style w:type="paragraph" w:styleId="CancelPara"><w:rPr><w:vanish /></w:rPr></w:style>
            </w:styles>
            """);
        await Write(zip, "word/document.xml", $$"""
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
            <w:p><w:pPr><w:pStyle w:val="VisiblePara" /></w:pPr><w:r><w:t>Visible one</w:t></w:r><w:r><w:rPr><w:rStyle w:val="HiddenChar" /></w:rPr><w:t>{{CharacterStyleSecret}}</w:t></w:r></w:p>
            <w:p><w:pPr><w:pStyle w:val="HiddenPara" /></w:pPr><w:r><w:t>{{ParagraphStyleSecret}}</w:t></w:r></w:p>
            <w:p><w:pPr><w:pStyle w:val="DerivedHiddenPara" /></w:pPr><w:r><w:t>{{BasedOnSecret}}</w:t></w:r></w:p>
            <w:p><w:pPr><w:pStyle w:val="CancelPara" /></w:pPr><w:r><w:rPr><w:vanish w:val="0" /></w:rPr><w:t>Cancelled visible</w:t></w:r></w:p>
            </w:body></w:document>
            """);
        return path;
    }

    private static async Task<string> CreateContentControlDocxAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "content-control.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await Write(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:w14="http://schemas.microsoft.com/office/word/2010/wordml"><w:body>
            <w:p w14:paraId="0A"><w:r><w:t>Before control</w:t></w:r></w:p>
            <w:sdt>
              <w:sdtPr><w:alias w:val="Section A" /><w:tag w:val="sectionA" /></w:sdtPr>
              <w:sdtContent>
                <w:p w14:paraId="0B"><w:r><w:t>Inside one</w:t></w:r></w:p>
                <w:sdt><w:sdtPr /><w:sdtContent><w:p w14:paraId="0C"><w:r><w:t>Inside two</w:t></w:r></w:p></w:sdtContent></w:sdt>
                <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Control cell</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
              </w:sdtContent>
            </w:sdt>
            <w:p w14:paraId="0D"><w:r><w:t>After control</w:t></w:r></w:p>
            </w:body></w:document>
            """);
        return path;
    }

    private static async Task<string> CreateMathDocxAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "math.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await Write(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:m="http://schemas.openxmlformats.org/officeDocument/2006/math"><w:body>
            <w:p><w:pPr><w:pStyle w:val="MathPara" /></w:pPr>
              <m:oMath><m:r><m:t>E=m</m:t></m:r><m:sSup><m:e><m:r><m:t>c</m:t></m:r></m:e><m:sup><m:r><m:t>2</m:t></m:r></m:sup></m:sSup></m:oMath>
              <w:r><w:t xml:space="preserve"> and </w:t></w:r>
              <m:oMath><m:f><m:num><m:r><m:t>a+b</m:t></m:r></m:num><m:den><m:r><m:t>2</m:t></m:r></m:den></m:f></m:oMath>
            </w:p>
            <m:oMathPara><m:oMath><m:rad><m:radPr><m:degHide m:val="1" /></m:radPr><m:deg /><m:e><m:r><m:t>x</m:t></m:r></m:e></m:rad></m:oMath></m:oMathPara>
            <w:tbl><w:tr><w:tc><w:p><m:oMath><m:sSub><m:e><m:r><m:t>a</m:t></m:r></m:e><m:sub><m:r><m:t>n</m:t></m:r></m:sub></m:sSub></m:oMath></w:p></w:tc></w:tr></w:tbl>
            </w:body></w:document>
            """);
        return path;
    }

    /// <summary>A body whose middle block is an empty self-closing paragraph.</summary>
    private static async Task<string> CreateSelfClosingParagraphDocxAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "self-closing.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await Write(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
            <w:p><w:r><w:t>Head</w:t></w:r></w:p>
            <w:p />
            <w:p><w:r><w:t>Tail</w:t></w:r></w:p>
            </w:body></w:document>
            """);
        return path;
    }

    /// <summary>An equation with text on both sides of it, and an equation alone in a table cell
    /// beside a plain one: the two shapes the anchor-preserving F1 rewrite has to keep intact.</summary>
    private static async Task<string> CreateEditableMathDocxAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "editable-math.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await Write(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:m="http://schemas.openxmlformats.org/officeDocument/2006/math"><w:body>
            <w:p><w:r><w:t xml:space="preserve">Given </w:t></w:r><m:oMath><m:sSup><m:e><m:r><m:t>x</m:t></m:r></m:e><m:sup><m:r><m:t>2</m:t></m:r></m:sup></m:sSup></m:oMath><w:r><w:t xml:space="preserve"> holds.</w:t></w:r></w:p>
            <w:p><w:r><w:t>Plain paragraph</w:t></w:r></w:p>
            <w:tbl><w:tr>
              <w:tc><w:p><m:oMath><m:sSub><m:e><m:r><m:t>a</m:t></m:r></m:e><m:sub><m:r><m:t>n</m:t></m:r></m:sub></m:sSub></m:oMath></w:p></w:tc>
              <w:tc><w:p><w:r><w:t>Note</w:t></w:r></w:p></w:tc>
            </w:tr></w:tbl>
            </w:body></w:document>
            """);
        return path;
    }

    private static async Task<string> CreateMultiParagraphCellDocxAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "multi-paragraph-cell.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await Write(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
            <w:tbl>
              <w:tr>
                <w:tc>
                  <w:p><w:r><w:t>A</w:t></w:r></w:p>
                  <w:p><w:r><w:t>B</w:t></w:r></w:p>
                  <w:p><w:r><w:t>C</w:t></w:r></w:p>
                </w:tc>
                <w:tc>
                  <w:p><w:r><w:t>Host</w:t></w:r></w:p>
                  <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Nested</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
                  <w:p />
                </w:tc>
                <w:tc>
                  <w:p><w:r><w:t>Wrapped host</w:t></w:r></w:p>
                  <w:sdt><w:sdtContent><w:tbl><w:tr><w:tc><w:p><w:r><w:t>Wrapped nested</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:sdtContent></w:sdt>
                </w:tc>
              </w:tr>
            </w:tbl>
            </w:body></w:document>
            """);
        return path;
    }

    private static int Count(string value, string token)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0; index += token.Length) count++;
        return count;
    }

    private static string Text(DocumentNode node) => node.Content is TextNodeContent text ? text.Text : string.Empty;

    /// <summary>Node text regardless of whether the paragraph projected as plain or rich text.</summary>
    private static string NodeText(DocumentNode node) => node.Content switch
    {
        TextNodeContent text => text.Text,
        RichTextNodeContent rich => string.Concat(rich.Runs.Select(run => run.Text)),
        _ => string.Empty,
    };

    private static string HiddenAnnotationText(DocumentGraph graph, string parentId)
    {
        var annotation = Assert.Single(graph.Nodes, node => node.ParentId == parentId && node.Kind == NodeKind.Annotation);
        Assert.Equal(ContentLayer.Hidden, annotation.Layer);
        Assert.Equal("docx-hidden-text", annotation.Extensions!["hidden_content_type"].GetString());
        return NodeText(annotation);
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string ReadEntryHash(string path, string entryName)
    {
        using var archive = ZipFile.OpenRead(path);
        using var stream = archive.GetEntry(entryName)!.Open();
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static string ReadUnchangedFirstParagraphSlice(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open(), Encoding.UTF8);
        var xml = reader.ReadToEnd();
        var start = xml.IndexOf("<w:p", StringComparison.Ordinal);
        var end = xml.IndexOf("</w:p>", start, StringComparison.Ordinal) + "</w:p>".Length;
        return xml[start..end];
    }
    private static string ReadDocumentXml(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static byte[] ReadDocumentBytes(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var stream = archive.GetEntry("word/document.xml")!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>The exact bytes a recorded slice addresses in word/document.xml.</summary>
    private static string SliceText(string path, RawSliceRef slice) =>
        Encoding.UTF8.GetString(ReadDocumentBytes(path), (int)slice.StartOffset, (int)(slice.EndOffset - slice.StartOffset));

    /// <summary>What sits before and after one slice in word/document.xml: the bytes an F1 patch of
    /// that slice must leave exactly as they were, wrapping markup (w:sdt) included.</summary>
    private static (string Before, string After) SliceSurroundings(string path, RawSliceRef slice)
    {
        var bytes = ReadDocumentBytes(path);
        return (Encoding.UTF8.GetString(bytes, 0, (int)slice.StartOffset),
            Encoding.UTF8.GetString(bytes, (int)slice.EndOffset, bytes.Length - (int)slice.EndOffset));
    }

    /// <summary>Replaces one node's content and restores into <paramref name="output"/>.</summary>
    private static async Task<DocxRestoreResult> EditAsync(DocxAdapter adapter, DocxExtractionResult export,
        string source, string output, string nodeId, NodeContent content)
    {
        var editedNodes = export.Graph.Nodes.Select(node => node.Id == nodeId ? node with { Content = content } : node).ToArray();
        var edited = export.Graph with { Partitions = [new DocumentPartition("part-0001", 0, editedNodes, "/word/document.xml")] };
        return await adapter.RestoreAsync(source, export.Graph, edited, output);
    }

    private static async Task<string> CreateAlternateContentDocxAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "alternate-content.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await Write(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
              xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
              xmlns:w14="http://schemas.microsoft.com/office/word/2010/wordml"
              xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
              xmlns:foo="urn:docredock:test:unsupported"><w:body>
              <w:p><mc:AlternateContent><mc:Choice Requires="foo"><w:r><w:drawing><a:sp><w:txbxContent><w:p><w:r><w:t>Unsupported top-level choice</w:t></w:r></w:p></w:txbxContent></a:sp></w:drawing></w:r></mc:Choice><mc:Fallback><w:r><w:drawing><a:sp><w:txbxContent><w:p><w:r><w:t>Top-level fallback textbox</w:t></w:r></w:p></w:txbxContent></a:sp></w:drawing></w:r></mc:Fallback></mc:AlternateContent></w:p>
              <w:p><mc:AlternateContent><mc:Choice Requires="w14" xmlns:w14="http://schemas.microsoft.com/office/word/2010/wordml"><w:r><w:drawing><a:sp><w:txbxContent><w:p><w:r><w:t>Choice textbox</w:t></w:r></w:p></w:txbxContent></a:sp></w:drawing></w:r></mc:Choice><mc:Fallback><w:r><w:drawing><a:sp><w:txbxContent><w:p><w:r><w:t>Fallback textbox</w:t></w:r></w:p></w:txbxContent></a:sp></w:drawing></w:r></mc:Fallback></mc:AlternateContent></w:p>
              <w:p><w:r><w:drawing><a:sp><w:txbxContent><w:p><w:r><w:t>Sibling one</w:t></w:r></w:p></w:txbxContent></a:sp></w:drawing></w:r><w:r><w:drawing><a:sp><w:txbxContent><w:p><w:r><w:t>Sibling two</w:t></w:r></w:p></w:txbxContent></a:sp></w:drawing></w:r></w:p>
              </w:body></w:document>
            """);
        return path;
    }

    // A body-level mc:AlternateContent is a fork in the body: each branch holds the blocks that
    // would sit at that body position had Word resolved it that way. Both branches are now sliced,
    // so the branch this build selects projects editable paragraphs and the edit is mirrored into
    // the counterpart the other branch holds - which is what a Word that resolves the other way
    // will show.
    [Fact]
    public async Task Body_level_alternate_content_projects_the_selected_branch_as_editable_blocks()
    {
        var source = await CreateBodyAlternateContentDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "body-alternate-unchanged.docx");
        var adapter = new DocxAdapter();

        var export = await adapter.ExtractAsync(source);
        var restore = await adapter.RestoreAsync(export, export.Graph, output);
        var body = export.Graph.Nodes.Where(node => node.Kind == NodeKind.Paragraph && node.Layer == ContentLayer.Body)
            .OrderBy(node => node.Order).ToArray();

        // Only the Choice branch projects; the Fallback stays markup nothing stands for.
        Assert.Equal(["Shared line", "Only in choice", "Tail"], body.Select(NodeText).ToArray());
        Assert.All(body, node => Assert.Equal(NodeEditability.EditableInPlace, node.Editability));
        Assert.All(body, node => Assert.NotNull(node.RawSlice));
        Assert.Equal("<w:p><w:r><w:t>Shared line</w:t></w:r></w:p>", SliceText(source, body[0].RawSlice!));
        Assert.DoesNotContain(export.Diagnostics, item => item.Code == "DocxSliceLedgerMismatch");
        Assert.True(restore.Succeeded);
        Assert.Equal(Hash(source), Hash(output));
    }

    [Fact]
    public async Task F1_edit_in_an_alternate_content_choice_is_mirrored_into_the_matching_fallback()
    {
        var source = await CreateBodyAlternateContentDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "body-alternate-shared.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => NodeText(node) == "Shared line");

        var result = await EditAsync(adapter, export, source, output, target.Id, new TextNodeContent("Shared edit"));
        var patched = ReadDocumentXml(output);
        var reexport = await adapter.ExtractAsync(output);

        Assert.True(result.Succeeded);
        Assert.Equal("Shared edit", NodeText(reexport.Graph.Nodes.Single(node => node.Id == target.Id)));
        // Both branches carry the edit, and the fork's own markup is byte-preserved around them.
        Assert.Equal(2, Count(patched, "<w:t>Shared edit</w:t>"));
        Assert.DoesNotContain("Shared line", patched, StringComparison.Ordinal);
        Assert.Equal(2, Count(patched, "<mc:AlternateContent>"));
        Assert.Equal(2, Count(patched, "<mc:Fallback>"));
        Assert.Contains("<mc:Choice Requires=\"w14\">", patched, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "DocxAlternateContentFallbackStale");
    }

    [Fact]
    public async Task F1_edit_in_an_alternate_content_choice_reports_a_fallback_it_could_not_mirror()
    {
        var source = await CreateBodyAlternateContentDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "body-alternate-stale.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => NodeText(node) == "Only in choice");

        var result = await EditAsync(adapter, export, source, output, target.Id, new TextNodeContent("Choice edited"));
        var patched = ReadDocumentXml(output);

        Assert.True(result.Succeeded);
        Assert.Equal("Choice edited", NodeText((await adapter.ExtractAsync(output)).Graph.Nodes.Single(node => node.Id == target.Id)));
        // The fallback said something else, so there is nothing to mirror the edit onto and the
        // reader is told the fork's other branch is now out of step rather than silently rewritten.
        Assert.Contains("<w:t>Different fallback</w:t>", patched, StringComparison.Ordinal);
        var notice = Assert.Single(result.Diagnostics, item => item.Code == "DocxAlternateContentFallbackStale");
        Assert.Equal(DiagnosticSeverity.Information, notice.Severity);
        Assert.Equal(target.Id, notice.NodeId);
        Assert.Contains("the unselected branch of AlternateContent #1 was left unchanged", notice.Message, StringComparison.Ordinal);
    }

    private static async Task<string> CreateBodyAlternateContentDocxAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "body-alternate.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await Write(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
              xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
              xmlns:w14="http://schemas.microsoft.com/office/word/2010/wordml"><w:body>
            <mc:AlternateContent><mc:Choice Requires="w14"><w:p><w:r><w:t>Shared line</w:t></w:r></w:p></mc:Choice><mc:Fallback><w:p><w:r><w:t>Shared line</w:t></w:r></w:p></mc:Fallback></mc:AlternateContent>
            <mc:AlternateContent><mc:Choice Requires="w14"><w:p><w:r><w:t>Only in choice</w:t></w:r></w:p></mc:Choice><mc:Fallback><w:p><w:r><w:t>Different fallback</w:t></w:r></w:p></mc:Fallback></mc:AlternateContent>
            <w:p><w:r><w:t>Tail</w:t></w:r></w:p><w:sectPr />
            </w:body></w:document>
            """);
        return path;
    }

    // An inline w:sdt used to make its whole paragraph unpatchable: only direct w:r children are
    // rewritten, so the control's own runs would have survived the rewrite and been emitted a
    // second time from the edited text. The control is now an anchor instead - the same mechanism
    // that keeps an equation in place - so the paragraph is an ordinary F1 target and the wrapper,
    // with the alias/tag/binding only it carries, comes through the edit untouched.
    [Fact]
    public async Task F1_edit_outside_an_inline_content_control_keeps_the_control_and_its_position()
    {
        var source = await CreateInlineContentControlDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "inline-sdt-outside.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => NodeText(node) == "Pick Choice here");
        var (before, after) = SliceSurroundings(source, target.RawSlice!);

        var result = await EditAsync(adapter, export, source, output, target.Id, new TextNodeContent("Pick Choice now"));
        var patched = ReadDocumentXml(output);
        var reexport = await adapter.ExtractAsync(output);

        Assert.True(result.Succeeded);
        Assert.Equal(NodeEditability.EditableInPlace, target.Editability);
        Assert.Equal("Pick Choice now", NodeText(reexport.Graph.Nodes.Single(node => node.Id == target.Id)));
        // The wrapper, its properties and its own run are all still there, and still between the
        // text that came before it and the text that came after it.
        Assert.Contains("<w:sdtPr><w:alias w:val=\"Option\" /></w:sdtPr><w:sdtContent><w:r><w:t>Choice</w:t></w:r></w:sdtContent>", patched, StringComparison.Ordinal);
        var control = patched.IndexOf("<w:alias w:val=\"Option\"", StringComparison.Ordinal);
        Assert.InRange(patched.IndexOf("Pick ", StringComparison.Ordinal), 0, control);
        Assert.InRange(control, 0, patched.IndexOf(" now", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "DocxInlineContainerUnwrapped");
        Assert.StartsWith(before, patched, StringComparison.Ordinal);
        Assert.EndsWith(after, patched, StringComparison.Ordinal);
    }

    [Fact]
    public async Task F1_edit_inside_an_inline_content_control_rewrites_its_content_and_keeps_the_wrapper()
    {
        var source = await CreateInlineContentControlDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "inline-sdt-inside.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => NodeText(node) == "Pick Choice here");

        var result = await EditAsync(adapter, export, source, output, target.Id, new TextNodeContent("Pick Selection here"));
        var patched = ReadDocumentXml(output);
        var reexport = await adapter.ExtractAsync(output);

        Assert.True(result.Succeeded);
        Assert.Equal("Pick Selection here", NodeText(reexport.Graph.Nodes.Single(node => node.Id == target.Id)));
        // The text around the control came back unchanged, so the difference is the control's own
        // new content and it goes back inside the wrapper rather than dissolving it.
        Assert.Contains("<w:sdtPr><w:alias w:val=\"Option\" /></w:sdtPr><w:sdtContent><w:r><w:t>Selection</w:t></w:r></w:sdtContent>", patched, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "DocxInlineContainerUnwrapped");
    }

    [Fact]
    public async Task F1_edit_across_an_inline_content_control_boundary_unwraps_it_and_warns()
    {
        var source = await CreateInlineContentControlDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "inline-sdt-across.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => NodeText(node) == "Pick Choice here");

        var result = await EditAsync(adapter, export, source, output, target.Id, new TextNodeContent("Choose Option now"));
        var patched = ReadDocumentXml(output);
        var reexport = await adapter.ExtractAsync(output);

        Assert.True(result.Succeeded);
        Assert.Equal("Choose Option now", NodeText(reexport.Graph.Nodes.Single(node => node.Id == target.Id)));
        // Nothing on either side of the control survived the edit, so there is no way to tell what
        // belonged inside it: the wrapper is dropped and the loss is reported rather than guessed.
        Assert.DoesNotContain("<w:alias w:val=\"Option\"", patched, StringComparison.Ordinal);
        var warning = Assert.Single(result.Diagnostics, item => item.Code == "DocxInlineContainerUnwrapped");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(target.Id, warning.NodeId);
        Assert.Contains($"1 inline content control(s) on paragraph {target.Id} were unwrapped", warning.Message, StringComparison.Ordinal);
        // The cell's own control is a different paragraph and is untouched by this patch.
        Assert.Contains("<w:tag w:val=\"owner\" />", patched, StringComparison.Ordinal);
    }

    [Fact]
    public async Task F1_edit_outside_a_smart_tag_keeps_the_smart_tag()
    {
        var source = await CreateInlineContentControlDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "inline-smarttag.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => NodeText(node) == "Meet Alice today");

        var result = await EditAsync(adapter, export, source, output, target.Id, new TextNodeContent("Meet Alice tomorrow"));
        var patched = ReadDocumentXml(output);
        var reexport = await adapter.ExtractAsync(output);

        Assert.True(result.Succeeded);
        Assert.Equal("Meet Alice tomorrow", NodeText(reexport.Graph.Nodes.Single(node => node.Id == target.Id)));
        Assert.Contains("w:element=\"PersonName\"", patched, StringComparison.Ordinal);
        Assert.Contains("<w:r><w:t>Alice</w:t></w:r></w:smartTag>", patched, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "DocxInlineContainerUnwrapped");
    }

    [Fact]
    public async Task F1_cell_edit_keeps_an_inline_content_control_inside_the_cell()
    {
        var source = await CreateInlineContentControlDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "inline-sdt-cell.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = Assert.Single(export.Graph.Nodes, node => node.Kind == NodeKind.Table);
        Assert.Equal("Owner Bob", Assert.IsType<TableNodeContent>(target.Content).Rows[0][0].Text);

        var result = await EditAsync(adapter, export, source, output, target.Id,
            new TableNodeContent([new TableCell[] { "Lead Bob" }]));
        var patched = ReadDocumentXml(output);
        var cells = Assert.IsType<TableNodeContent>(
            (await adapter.ExtractAsync(output)).Graph.Nodes.Single(node => node.Id == target.Id).Content).Rows[0];

        Assert.True(result.Succeeded);
        Assert.Equal("Lead Bob", cells[0].Text);
        Assert.Contains("<w:sdtPr><w:tag w:val=\"owner\" /></w:sdtPr><w:sdtContent><w:r><w:t>Bob</w:t></w:r></w:sdtContent>", patched, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "DocxInlineContainerUnwrapped");
    }

    [Fact]
    public async Task F0_restore_of_the_inline_container_fixture_is_byte_identical()
    {
        var source = await CreateInlineContentControlDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "inline-sdt-unchanged.docx");
        var adapter = new DocxAdapter();

        var export = await adapter.ExtractAsync(source);
        var restore = await adapter.RestoreAsync(export, export.Graph, output);

        Assert.True(restore.Succeeded);
        Assert.DoesNotContain(export.Diagnostics, item => item.Code == "DocxSliceLedgerMismatch");
        Assert.Equal(Hash(source), Hash(output));
    }

    private static async Task<string> CreateInlineContentControlDocxAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "inline-sdt.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await Write(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
            <w:p><w:r><w:t xml:space="preserve">Pick </w:t></w:r><w:sdt><w:sdtPr><w:alias w:val="Option" /></w:sdtPr><w:sdtContent><w:r><w:t>Choice</w:t></w:r></w:sdtContent></w:sdt><w:r><w:t xml:space="preserve"> here</w:t></w:r></w:p>
            <w:p><w:r><w:t xml:space="preserve">Meet </w:t></w:r><w:smartTag w:uri="urn:schemas-microsoft-com:office:smarttags" w:element="PersonName"><w:r><w:t>Alice</w:t></w:r></w:smartTag><w:r><w:t xml:space="preserve"> today</w:t></w:r></w:p>
            <w:tbl><w:tr><w:tc><w:p><w:r><w:t xml:space="preserve">Owner </w:t></w:r><w:sdt><w:sdtPr><w:tag w:val="owner" /></w:sdtPr><w:sdtContent><w:r><w:t>Bob</w:t></w:r></w:sdtContent></w:sdt></w:p></w:tc></w:tr></w:tbl>
            <w:p><w:r><w:t>Plain</w:t></w:r></w:p><w:sectPr />
            </w:body></w:document>
            """);
        return path;
    }

    private static async Task<string> CreateDocxAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sample.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await Write(zip, "word/_rels/document.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdImg\" Target=\"media/image1.png\" Type=\"image\" /></Relationships>");
        await Write(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:w14="http://schemas.microsoft.com/office/word/2010/wordml" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><w:body>
            <w:p w14:paraId="AA"><w:pPr><w:pStyle w:val="Heading1" /></w:pPr><w:r><w:t>Title</w:t></w:r></w:p>
            <w:p w14:paraId="AB"><w:pPr><w:pStyle w:val="Heading 2" /></w:pPr><w:r><w:t>Subheading</w:t></w:r></w:p>
            <w:p w14:paraId="AC"><w:pPr><w:pStyle w:val="Code" /></w:pPr><w:r><w:t>const x = 1;</w:t></w:r></w:p>
            <w:p w14:paraId="BB"><w:r><w:t>Unchanged</w:t></w:r></w:p>
            <w:p w14:paraId="CC"><w:r><w:t>B</w:t></w:r><w:r><w:t>efore</w:t></w:r></w:p>
            <w:p w14:paraId="DD"><w:pPr><w:numPr /></w:pPr><w:r><w:t>One</w:t></w:r></w:p>
            <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Cell</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            <w:p w14:paraId="EE"><w:r><w:drawing><a:blip r:embed="rIdImg" /></w:drawing></w:r></w:p><w:sectPr />
            </w:body></w:document>
            """);
        var image = zip.CreateEntry("word/media/image1.png");
        await using (var imageStream = image.Open()) await imageStream.WriteAsync(new byte[] { 1, 2, 3 });
        return path;
    }

    private static async Task<string> CreateRichTextDocxAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "rich.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await Write(zip, "word/styles.xml", "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:style w:type=\"paragraph\" w:styleId=\"QuoteStyle\"><w:rPr><w:rFonts w:ascii=\"Aptos\" w:eastAsia=\"Yu Gothic\" /></w:rPr></w:style></w:styles>");
        await Write(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:w14="http://schemas.microsoft.com/office/word/2010/wordml"><w:body>
            <w:p w14:paraId="FF"><w:pPr><w:pStyle w:val="QuoteStyle" /><w:jc w:val="center" /><w:spacing w:before="120" w:after="80" /></w:pPr><w:r><w:rPr><w:rFonts w:ascii="Yu Mincho" w:hAnsi="Yu Mincho" w:eastAsia="游明朝" /><w:sz w:val="28" /><w:color w:val="1F4E79" /><w:b /></w:rPr><w:t>Bold</w:t></w:r><w:r><w:rPr><w:rFonts w:ascii="BIZ UDPGothic" w:hAnsi="BIZ UDPGothic" w:eastAsia="BIZ UDPゴシック" /><w:sz w:val="24" /><w:i /></w:rPr><w:t>Italic</w:t></w:r><w:r><w:rPr><w:u w:val="single" /></w:rPr><w:t>Under</w:t></w:r><w:r><w:rPr><w:strike /></w:rPr><w:t>Strike</w:t></w:r><w:r><w:rPr><w:rStyle w:val="CodeChar" /></w:rPr><w:t>Code</w:t></w:r><w:r><w:br /></w:r><w:r><w:tab /></w:r><w:r><w:t>Tail</w:t></w:r></w:p><w:sectPr><w:pgSz w:w="11906" w:h="16838" /><w:pgMar w:top="1440" w:right="1080" w:bottom="1440" w:left="1080" /></w:sectPr>
            </w:body></w:document>
            """);
        return path;
    }

    // A fork nested inside a branch is the same shape one level down: the extractor descends into
    // the branch the inner fork selects, so the paragraph only reachable through two choices still
    // owns its bytes - and an edit to it reaches the counterparts *both* levels passed over.
    [Fact]
    public async Task Nested_alternate_content_projects_the_twice_selected_branch_as_an_editable_block()
    {
        var source = await CreateNestedAlternateContentDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "nested-alternate-unchanged.docx");
        var adapter = new DocxAdapter();

        var export = await adapter.ExtractAsync(source);
        var restore = await adapter.RestoreAsync(export, export.Graph, output);
        var body = export.Graph.Nodes.Where(node => node.Kind == NodeKind.Paragraph && node.Layer == ContentLayer.Body)
            .OrderBy(node => node.Order).ToArray();

        Assert.Equal(["Nested line", "Tail"], body.Select(NodeText).ToArray());
        Assert.All(body, node => Assert.Equal(NodeEditability.EditableInPlace, node.Editability));
        // The slice is the inner Choice's paragraph, not the outer fork and not the fallbacks.
        Assert.Equal("<w:p><w:r><w:t>Nested line</w:t></w:r></w:p>", SliceText(source, body[0].RawSlice!));
        Assert.DoesNotContain(export.Diagnostics, item => item.Code == "DocxSliceLedgerMismatch");
        Assert.True(restore.Succeeded);
        Assert.Equal(Hash(source), Hash(output));
    }

    [Fact]
    public async Task F1_edit_in_a_nested_alternate_content_choice_reaches_the_inner_and_outer_fallbacks()
    {
        var source = await CreateNestedAlternateContentDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "nested-alternate-edit.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => NodeText(node) == "Nested line");

        var result = await EditAsync(adapter, export, source, output, target.Id, new TextNodeContent("Nested edit"));
        var patched = ReadDocumentXml(output);

        Assert.True(result.Succeeded);
        // Three paragraphs said "Nested line": the inner choice, the inner fallback, and the outer
        // fallback. All three carry the edit, so Word shows it however it resolves the two forks.
        Assert.Equal(3, Count(patched, "<w:t>Nested edit</w:t>"));
        Assert.DoesNotContain("Nested line", patched, StringComparison.Ordinal);
        Assert.Equal(2, Count(patched, "<mc:AlternateContent>"));
        Assert.Equal(2, Count(patched, "<mc:Fallback>"));
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "DocxAlternateContentFallbackStale");
        Assert.Equal("Nested edit", NodeText((await adapter.ExtractAsync(output)).Graph.Nodes.Single(node => node.Id == target.Id)));
    }

    private static async Task<string> CreateNestedAlternateContentDocxAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "nested-alternate.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await Write(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
              xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
              xmlns:w14="http://schemas.microsoft.com/office/word/2010/wordml"><w:body>
            <mc:AlternateContent><mc:Choice Requires="w14"><mc:AlternateContent><mc:Choice Requires="w14"><w:p><w:r><w:t>Nested line</w:t></w:r></w:p></mc:Choice><mc:Fallback><w:p><w:r><w:t>Nested line</w:t></w:r></w:p></mc:Fallback></mc:AlternateContent></mc:Choice><mc:Fallback><w:p><w:r><w:t>Nested line</w:t></w:r></w:p></mc:Fallback></mc:AlternateContent>
            <w:p><w:r><w:t>Tail</w:t></w:r></w:p><w:sectPr />
            </w:body></w:document>
            """);
        return path;
    }

    // A body-level fork whose only branch this build cannot resolve projects nothing at that body
    // position. The slices its branches hold are still consumed, so the ledger stays in step and
    // the rest of the document keeps its own slices.
    [Fact]
    public async Task A_body_fork_with_no_resolvable_branch_projects_nothing_and_keeps_the_ledger_aligned()
    {
        var source = await WriteDocxAsync("unresolvable-alternate.docx", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
              xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
              xmlns:foo="urn:docredock:test:unsupported"><w:body>
            <mc:AlternateContent><mc:Choice Requires="foo"><w:p><w:r><w:t>Unsupported only</w:t></w:r></w:p></mc:Choice></mc:AlternateContent>
            <w:p><w:r><w:t>Tail</w:t></w:r></w:p><w:sectPr />
            </w:body></w:document>
            """);
        var output = Path.Combine(Path.GetDirectoryName(source)!, "unresolvable-alternate-unchanged.docx");
        var adapter = new DocxAdapter();

        var export = await adapter.ExtractAsync(source);
        var restore = await adapter.RestoreAsync(export, export.Graph, output);
        var body = export.Graph.Nodes.Where(node => node.Kind == NodeKind.Paragraph && node.Layer == ContentLayer.Body).ToArray();

        Assert.Equal(["Tail"], body.Select(NodeText).ToArray());
        Assert.Equal(NodeEditability.EditableInPlace, Assert.Single(body).Editability);
        Assert.DoesNotContain(export.Diagnostics, item => item.Code == "DocxSliceLedgerMismatch");
        Assert.True(restore.Succeeded);
        Assert.Equal(Hash(source), Hash(output));
    }

    // The branch path is a path, not a label: a fork reached through the *outer fallback* and then
    // the outer fork's second choice is addressed as "Fallback/Choice#1", and the extractor has to
    // walk to exactly that slice.
    [Fact]
    public async Task F1_edit_reaches_a_block_selected_through_a_fallback_and_a_second_choice()
    {
        var source = await CreateDeepAlternateContentDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "deep-alternate-edit.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => NodeText(node) == "Second choice");

        Assert.Equal(NodeEditability.EditableInPlace, target.Editability);
        Assert.Equal("<w:p><w:r><w:t>Second choice</w:t></w:r></w:p>", SliceText(source, target.RawSlice!));
        var result = await EditAsync(adapter, export, source, output, target.Id, new TextNodeContent("Deep edit"));
        var patched = ReadDocumentXml(output);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(export.Diagnostics, item => item.Code == "DocxSliceLedgerMismatch");
        // The inner first choice says the same thing, so it is mirrored; the outer choice - the
        // branch this build cannot resolve - says something else and is left alone.
        Assert.Equal(2, Count(patched, "<w:t>Deep edit</w:t>"));
        Assert.Contains("<w:t>Unsupported branch</w:t>", patched, StringComparison.Ordinal);
    }

    private static async Task<string> CreateDeepAlternateContentDocxAsync() => await WriteDocxAsync("deep-alternate.docx", """
        <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
          xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
          xmlns:w14="http://schemas.microsoft.com/office/word/2010/wordml"
          xmlns:foo="urn:docredock:test:unsupported"><w:body>
        <mc:AlternateContent><mc:Choice Requires="foo"><w:p><w:r><w:t>Unsupported branch</w:t></w:r></w:p></mc:Choice><mc:Fallback><mc:AlternateContent><mc:Choice Requires="foo"><w:p><w:r><w:t>Second choice</w:t></w:r></w:p></mc:Choice><mc:Choice Requires="w14"><w:p><w:r><w:t>Second choice</w:t></w:r></w:p></mc:Choice></mc:AlternateContent></mc:Fallback></mc:AlternateContent>
        <w:p><w:r><w:t>Tail</w:t></w:r></w:p><w:sectPr />
        </w:body></w:document>
        """);

    // A text box's w:txbxContent is now a slice of its own, so its text is an ordinary F1 target:
    // the splice rewrites the box's paragraphs and leaves the shape, the drawing anchor and the
    // host paragraph around it byte-identical.
    [Fact]
    public async Task F1_edit_of_a_drawingml_text_box_rewrites_only_its_own_bytes()
    {
        var source = await CreateTextBoxDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "textbox-edit.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => node.Kind == NodeKind.TextBox && Text(node) == "Box text");

        Assert.Equal(NodeEditability.EditableInPlace, target.Editability);
        Assert.NotNull(target.RawSlice);
        Assert.StartsWith("<w:txbxContent>", SliceText(source, target.RawSlice!), StringComparison.Ordinal);
        var (before, after) = SliceSurroundings(source, target.RawSlice!);

        var result = await EditAsync(adapter, export, source, output, target.Id, new TextNodeContent("Box edited"));
        var patched = ReadDocumentXml(output);
        var reexport = await adapter.ExtractAsync(output);

        Assert.True(result.Succeeded);
        Assert.Equal(FidelityLevel.F1, result.Fidelity.Level);
        Assert.Contains("<w:t>Box edited</w:t>", patched, StringComparison.Ordinal);
        Assert.DoesNotContain("Box text", patched, StringComparison.Ordinal);
        // Everything outside the box - the wsp shape, the drawing, both plain paragraphs - is copied.
        Assert.StartsWith(before, patched, StringComparison.Ordinal);
        Assert.EndsWith(after, patched, StringComparison.Ordinal);
        Assert.Equal("Box edited", Text(reexport.Graph.Nodes.Single(node => node.Id == target.Id)));
        Assert.Equal("Intro", NodeText(reexport.Graph.Nodes.Single(node => NodeText(node) == "Intro")));
    }

    [Fact]
    public async Task F1_text_box_edit_grows_and_shrinks_the_box_paragraph_list()
    {
        var source = await CreateMultiParagraphTextBoxDocxAsync();
        var directory = Path.GetDirectoryName(source)!;
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => node.Kind == NodeKind.TextBox);

        Assert.Equal("First\nSecond", Text(target));

        var grown = Path.Combine(directory, "textbox-grown.docx");
        var growResult = await EditAsync(adapter, export, source, grown, target.Id, new TextNodeContent("First\nSecond\nThird"));
        var grownXml = ReadDocumentXml(grown);
        var shrunk = Path.Combine(directory, "textbox-shrunk.docx");
        var shrinkResult = await EditAsync(adapter, export, source, shrunk, target.Id, new TextNodeContent("Only"));
        var shrunkXml = ReadDocumentXml(shrunk);

        Assert.True(growResult.Succeeded);
        Assert.True(shrinkResult.Succeeded);
        // The added paragraph inherits the last one's w:pPr and first w:rPr, so a new line keeps
        // the box's own formatting instead of falling back to the document default.
        Assert.Equal(3, Count(grownXml, "<w:jc w:val=\"center\" />"));
        Assert.Equal(3, Count(grownXml, "<w:b />"));
        Assert.Contains("<w:t>Third</w:t>", grownXml, StringComparison.Ordinal);
        Assert.Equal("First\nSecond\nThird", Text((await adapter.ExtractAsync(grown)).Graph.Nodes.Single(node => node.Id == target.Id)));
        Assert.Equal("Only", Text((await adapter.ExtractAsync(shrunk)).Graph.Nodes.Single(node => node.Id == target.Id)));
        Assert.DoesNotContain("Second", shrunkXml, StringComparison.Ordinal);
        Assert.Equal(1, Count(shrunkXml, "<w:jc w:val=\"center\" />"));
    }

    // A paragraph-level fork between a DrawingML box and its VML fallback holds the same words
    // twice. The projected box owns the bytes of the branch this build resolves; the counterpart is
    // rewritten alongside it so the fallback does not go stale.
    [Fact]
    public async Task F1_text_box_edit_inside_a_paragraph_fork_is_mirrored_into_the_vml_fallback()
    {
        var source = await CreateForkedTextBoxDocxAsync("Shared box");
        var output = Path.Combine(Path.GetDirectoryName(source)!, "textbox-fork-edit.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => node.Kind == NodeKind.TextBox);

        Assert.Equal(NodeEditability.EditableInPlace, target.Editability);
        var result = await EditAsync(adapter, export, source, output, target.Id, new TextNodeContent("Fork edited"));
        var patched = ReadDocumentXml(output);

        Assert.True(result.Succeeded);
        Assert.Equal(2, Count(patched, "<w:t>Fork edited</w:t>"));
        Assert.DoesNotContain("Shared box", patched, StringComparison.Ordinal);
        // Only the two w:txbxContent runs of bytes changed: the fork, both shapes and the VML
        // wrapper are still there verbatim.
        Assert.Contains("<v:textbox>", patched, StringComparison.Ordinal);
        Assert.Contains("<mc:Fallback>", patched, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "DocxAlternateContentFallbackStale");
    }

    [Fact]
    public async Task F1_text_box_edit_reports_a_paragraph_fork_fallback_it_could_not_mirror()
    {
        var source = await CreateForkedTextBoxDocxAsync("Legacy box");
        var output = Path.Combine(Path.GetDirectoryName(source)!, "textbox-fork-stale.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => node.Kind == NodeKind.TextBox);

        var result = await EditAsync(adapter, export, source, output, target.Id, new TextNodeContent("Fork edited"));
        var patched = ReadDocumentXml(output);

        Assert.True(result.Succeeded);
        Assert.Equal(1, Count(patched, "<w:t>Fork edited</w:t>"));
        // The fallback said something else, so there is nothing to mirror onto and the reader is
        // told the other branch is now out of step rather than having it silently rewritten.
        Assert.Contains("<w:t>Legacy box</w:t>", patched, StringComparison.Ordinal);
        var notice = Assert.Single(result.Diagnostics, item => item.Code == "DocxAlternateContentFallbackStale");
        Assert.Equal(DiagnosticSeverity.Information, notice.Severity);
        Assert.Equal(target.Id, notice.NodeId);
    }

    // A text box's slice sits inside its host block's slice, so one ordered splice cannot rewrite
    // both. The clash is caught before anything is written rather than surfacing as a slice-overlap
    // failure halfway through the patch.
    [Fact]
    public async Task F1_refuses_a_restore_that_edits_a_text_box_and_its_host_paragraph_together()
    {
        var source = await CreateVmlTextBoxDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "textbox-overlap.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var box = export.Graph.Nodes.Single(node => node.Kind == NodeKind.TextBox);
        var host = export.Graph.Nodes.Single(node => node.Id == box.ParentId);

        var editedNodes = export.Graph.Nodes.Select(node =>
            node.Id == box.Id ? node with { Content = new TextNodeContent("Box edited") }
            : node.Id == host.Id ? node with { Content = new TextNodeContent("Host edited") }
            : node).ToArray();
        var edited = export.Graph with { Partitions = [new DocumentPartition("part-0001", 0, editedNodes, "/word/document.xml")] };
        var result = await adapter.RestoreAsync(source, export.Graph, edited, output);

        Assert.False(result.Succeeded);
        var failure = Assert.Single(result.Diagnostics, item => item.Code == "OverlappingEdits");
        Assert.Equal(DiagnosticSeverity.Error, failure.Severity);
        Assert.Equal(box.Id, failure.NodeId);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task F1_refuses_to_delete_a_text_box()
    {
        var source = await CreateTextBoxDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "textbox-delete.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => node.Kind == NodeKind.TextBox);
        var edited = export.Graph with
        {
            Partitions = [new DocumentPartition("part-0001", 0,
                export.Graph.Nodes.Where(node => node.Id != target.Id).ToArray(), "/word/document.xml")]
        };

        var result = await adapter.RestoreAsync(source, export.Graph, edited, output,
            new DiffOptions(new HashSet<string>(StringComparer.Ordinal) { target.Id }));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, item => item.Code == "UnsupportedDelete");
        Assert.False(File.Exists(output));
    }

    // The host paragraph's own text never included the box's characters, so an edit to the
    // paragraph must not write across them either.
    [Fact]
    public async Task F1_edit_of_a_text_box_host_paragraph_leaves_the_box_untouched()
    {
        var source = await CreateVmlTextBoxDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "textbox-host-only.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var box = export.Graph.Nodes.Single(node => node.Kind == NodeKind.TextBox);
        var host = export.Graph.Nodes.Single(node => node.Id == box.ParentId);

        Assert.Equal("Host", NodeText(host));
        var result = await EditAsync(adapter, export, source, output, host.Id, new TextNodeContent("Host edited"));
        var patched = ReadDocumentXml(output);

        Assert.True(result.Succeeded);
        Assert.Contains("<w:t>Legacy body</w:t>", patched, StringComparison.Ordinal);
        Assert.Contains("Host edited", patched, StringComparison.Ordinal);
    }

    // Only the outer w:txbxContent is addressable. The inner box keeps its own node and its own
    // text - which the outer box's projection never included - so an edit to the outer box must
    // leave it exactly where it was.
    [Fact]
    public async Task A_text_box_inside_a_text_box_stays_outside_the_outer_box_slice()
    {
        var source = await CreateNestedTextBoxDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "textbox-nested-edit.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var boxes = export.Graph.Nodes.Where(node => node.Kind == NodeKind.TextBox).ToArray();
        var outerBox = Assert.Single(boxes, node => node.RawSlice is not null);
        var innerBox = Assert.Single(boxes, node => node.RawSlice is null);

        Assert.Equal("Outer body", Text(outerBox));
        Assert.Equal("Inner body", Text(innerBox));
        Assert.Equal(NodeEditability.EditableWithConstraints, innerBox.Editability);
        var result = await EditAsync(adapter, export, source, output, outerBox.Id, new TextNodeContent("Outer edited"));
        var patched = ReadDocumentXml(output);

        Assert.True(result.Succeeded);
        Assert.Contains("<w:t>Outer edited</w:t>", patched, StringComparison.Ordinal);
        Assert.Contains("<w:t>Inner body</w:t>", patched, StringComparison.Ordinal);
    }

    // TextBoxText joins the box's own paragraphs with "\n" and keeps the empty ones at the edges,
    // so restore has to split the same way: an unchanged box is not dirty, and an edit to the
    // middle line lands in the middle paragraph.
    [Fact]
    public async Task F1_text_box_edit_keeps_the_empty_paragraphs_at_the_edges_of_the_box()
    {
        var source = await CreateEdgeEmptyTextBoxDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "textbox-edges-edit.docx");
        var adapter = new DocxAdapter();
        var export = await adapter.ExtractAsync(source);
        var target = export.Graph.Nodes.Single(node => node.Kind == NodeKind.TextBox);

        Assert.Equal("\nMiddle\n", Text(target));
        var result = await EditAsync(adapter, export, source, output, target.Id, new TextNodeContent("\nMiddle edited\n"));
        var reexport = await adapter.ExtractAsync(output);

        Assert.True(result.Succeeded);
        var restored = reexport.Graph.Nodes.Single(node => node.Id == target.Id);
        Assert.Equal("\nMiddle edited\n", Text(restored));
        // Still three paragraphs: the two empty edges were segments of their own, not padding the
        // split could drop.
        Assert.Equal(3, Count(SliceText(output, restored.RawSlice!), "<w:p"));
    }

    [Theory]
    [InlineData("textbox")]
    [InlineData("multi")]
    [InlineData("fork")]
    [InlineData("vml")]
    [InlineData("nested-box")]
    [InlineData("edge-empty")]
    [InlineData("deep-ac")]
    [InlineData("nested-ac")]
    public async Task F0_restore_of_the_text_box_and_nested_fork_fixtures_is_byte_identical(string fixture)
    {
        var source = fixture switch
        {
            "textbox" => await CreateTextBoxDocxAsync(),
            "multi" => await CreateMultiParagraphTextBoxDocxAsync(),
            "fork" => await CreateForkedTextBoxDocxAsync("Shared box"),
            "vml" => await CreateVmlTextBoxDocxAsync(),
            "nested-box" => await CreateNestedTextBoxDocxAsync(),
            "edge-empty" => await CreateEdgeEmptyTextBoxDocxAsync(),
            "deep-ac" => await CreateDeepAlternateContentDocxAsync(),
            _ => await CreateNestedAlternateContentDocxAsync(),
        };
        var output = Path.Combine(Path.GetDirectoryName(source)!, fixture + "-unchanged.docx");
        var adapter = new DocxAdapter();

        var export = await adapter.ExtractAsync(source);
        var restore = await adapter.RestoreAsync(export, export.Graph, output);

        Assert.True(restore.Succeeded);
        Assert.Equal(FidelityLevel.F0, restore.Fidelity.Level);
        Assert.DoesNotContain(export.Diagnostics, item => item.Code == "DocxSliceLedgerMismatch");
        Assert.Equal(Hash(source), Hash(output));
    }

    private static async Task<string> CreateTextBoxDocxAsync() => await WriteDocxAsync("textbox.docx", """
        <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
          xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
          xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
          xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape"><w:body>
        <w:p><w:r><w:t>Intro</w:t></w:r></w:p>
        <w:p><w:r><w:drawing><wp:inline><a:graphic><a:graphicData><wps:wsp><wps:cNvPr id="7" name="Box" /><wps:txbx><w:txbxContent><w:p><w:r><w:t>Box text</w:t></w:r></w:p></w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
        <w:p><w:r><w:t>Tail</w:t></w:r></w:p><w:sectPr />
        </w:body></w:document>
        """);

    private static async Task<string> CreateMultiParagraphTextBoxDocxAsync() => await WriteDocxAsync("textbox-multi.docx", """
        <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
          xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
          xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
          xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape"><w:body>
        <w:p><w:r><w:drawing><wp:inline><a:graphic><a:graphicData><wps:wsp><wps:cNvPr id="9" name="Notes" /><wps:txbx><w:txbxContent><w:p><w:pPr><w:jc w:val="center" /></w:pPr><w:r><w:rPr><w:b /></w:rPr><w:t>First</w:t></w:r></w:p><w:p><w:pPr><w:jc w:val="center" /></w:pPr><w:r><w:rPr><w:b /></w:rPr><w:t>Second</w:t></w:r></w:p></w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
        <w:p><w:r><w:t>Tail</w:t></w:r></w:p><w:sectPr />
        </w:body></w:document>
        """);

    /// <summary>A paragraph-level mc:AlternateContent whose Choice is a DrawingML text box and
    /// whose Fallback is the legacy VML one Word writes beside it.</summary>
    private static async Task<string> CreateForkedTextBoxDocxAsync(string fallbackText) => await WriteDocxAsync("textbox-fork.docx", $"""
        <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
          xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
          xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
          xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
          xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape"
          xmlns:v="urn:schemas-microsoft-com:vml"><w:body>
        <w:p><mc:AlternateContent><mc:Choice Requires="wps"><w:r><w:drawing><wp:inline><a:graphic><a:graphicData><wps:wsp><wps:cNvPr id="11" name="Forked" /><wps:txbx><w:txbxContent><w:p><w:r><w:t>Shared box</w:t></w:r></w:p></w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></mc:Choice><mc:Fallback><w:r><w:pict><v:shape id="s11" style="width:20pt;height:20pt"><v:textbox><w:txbxContent><w:p><w:r><w:t>{fallbackText}</w:t></w:r></w:p></w:txbxContent></v:textbox></v:shape></w:pict></w:r></mc:Fallback></mc:AlternateContent></w:p>
        <w:p><w:r><w:t>Tail</w:t></w:r></w:p><w:sectPr />
        </w:body></w:document>
        """);

    /// <summary>A DrawingML text box holding a VML text box of its own: only the outer
    /// w:txbxContent is addressable.</summary>
    private static async Task<string> CreateNestedTextBoxDocxAsync() => await WriteDocxAsync("textbox-nested.docx", """
        <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
          xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
          xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
          xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape"
          xmlns:v="urn:schemas-microsoft-com:vml"><w:body>
        <w:p><w:r><w:drawing><wp:inline><a:graphic><a:graphicData><wps:wsp><wps:cNvPr id="31" name="Outer" /><wps:txbx><w:txbxContent><w:p><w:r><w:t>Outer body</w:t></w:r><w:r><w:pict><v:shape id="s32" style="width:10pt;height:10pt"><v:textbox><w:txbxContent><w:p><w:r><w:t>Inner body</w:t></w:r></w:p></w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p></w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
        <w:p><w:r><w:t>Tail</w:t></w:r></w:p><w:sectPr />
        </w:body></w:document>
        """);

    /// <summary>A text box whose first and last paragraphs are empty, so the projected text starts
    /// and ends with the "\n" that separates them.</summary>
    private static async Task<string> CreateEdgeEmptyTextBoxDocxAsync() => await WriteDocxAsync("textbox-edges.docx", """
        <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
          xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
          xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
          xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape"><w:body>
        <w:p><w:r><w:drawing><wp:inline><a:graphic><a:graphicData><wps:wsp><wps:cNvPr id="41" name="Edges" /><wps:txbx><w:txbxContent><w:p /><w:p><w:r><w:t>Middle</w:t></w:r></w:p><w:p /></w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
        <w:p><w:r><w:t>Tail</w:t></w:r></w:p><w:sectPr />
        </w:body></w:document>
        """);

    /// <summary>A paragraph that carries both its own text and a legacy VML text box, so host and
    /// box are two editable nodes over overlapping bytes.</summary>
    private static async Task<string> CreateVmlTextBoxDocxAsync() => await WriteDocxAsync("textbox-vml.docx", """
        <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
          xmlns:v="urn:schemas-microsoft-com:vml"><w:body>
        <w:p><w:r><w:t>Host</w:t></w:r><w:r><w:pict><v:shape id="s21" style="width:20pt;height:20pt"><v:textbox><w:txbxContent><w:p><w:r><w:t>Legacy body</w:t></w:r></w:p></w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>
        <w:p><w:r><w:t>Tail</w:t></w:r></w:p><w:sectPr />
        </w:body></w:document>
        """);

    private static async Task<string> WriteDocxAsync(string name, string documentXml)
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await Write(zip, "word/document.xml", documentXml);
        return path;
    }

    private static async Task Write(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name);
        await using var stream = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        await stream.WriteAsync(text);
    }

    private static async Task<byte[]> ReadEntryBytesAsync(ZipArchive archive, string name)
    {
        await using var input = archive.GetEntry(name)!.Open();
        using var output = new MemoryStream();
        await input.CopyToAsync(output);
        return output.ToArray();
    }
}
