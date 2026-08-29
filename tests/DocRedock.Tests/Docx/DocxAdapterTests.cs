using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Diff;
using DocRedock.Core.Documents;
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
        Assert.True(labelNode.Extensions?.TryGetValue("visual_graph_member", out var marker) == true && marker.GetBoolean());
        Assert.Contains(" -->|YES| ", markdown, StringComparison.Ordinal);
        Assert.Contains("Explanation remains", markdown, StringComparison.Ordinal);
        Assert.True(visual.Accounting.IsConsistent);
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

        Assert.Equal(VisualEdgeResolution.Unresolved, edge.Resolution);
        Assert.Null(edge.SourceId);
        Assert.NotNull(edge.TargetId);
        Assert.Contains(visual.Diagnostics!, diagnostic => diagnostic.Code == "VisualConnectorAmbiguous");
        Assert.Contains(visual.Diagnostics!, diagnostic => diagnostic.Code == "VisualConnectorUnresolved");
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

    private static async Task<string> CreateVisualTopologyDocxAsync(string visualContent)
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-docx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "visual-topology.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await Write(zip, "word/document.xml", $"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:w14=\"http://schemas.microsoft.com/office/word/2010/wordml\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" xmlns:v=\"urn:schemas-microsoft-com:vml\"><w:body><w:p w14:paraId=\"AA\"><w:r><w:drawing>{visualContent}</w:drawing></w:r></w:p></w:body></w:document>");
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

    private static string Text(DocumentNode node) => node.Content is TextNodeContent text ? text.Text : string.Empty;
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
