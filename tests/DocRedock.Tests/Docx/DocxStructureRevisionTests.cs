using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Docx;
using DocRedock.Markdown;

namespace DocRedock.Tests.Docx;

public sealed partial class DocxReadabilityTests
{
    [Fact]
    public async Task Formatting_history_shows_previous_and_current_values_without_changing_roundtrip_bytes()
    {
        var source = Document("<w:p><w:pPr><w:jc w:val=\"right\"/><w:pPrChange w:author=\"Editor\"><w:pPr><w:jc w:val=\"left\"/></w:pPr></w:pPrChange></w:pPr>" +
            "<w:r><w:rPr><w:b/><w:color w:val=\"FF0000\"/><w:rPrChange><w:rPr><w:b w:val=\"0\"/><w:color w:val=\"000000\"/></w:rPr></w:rPrChange></w:rPr><w:t>changed</w:t></w:r></w:p>");
        try
        {
            var original = await File.ReadAllBytesAsync(source);
            var adapter = new DocxAdapter();
            var final = await adapter.ExtractAsync(source);
            Assert.True(final.SourceIndex.HasTrackedRevisions);
            Assert.DoesNotContain("変更履歴", new ReadableMarkdownSerializer().Serialize(final.Graph));
            var restored = Path.Combine(Path.GetDirectoryName(source)!, "unchanged.docx");
            Assert.True((await adapter.RestoreAsync(final, final.Graph, restored)).Succeeded);
            Assert.Equal(original, await File.ReadAllBytesAsync(restored));
            var visible = await adapter.ExtractAsync(source, new(ShowTrackedRevisions: true));
            var markdown = new ReadableMarkdownSerializer().Serialize(visible.Graph);
            Assert.Contains("**changed**", markdown);
            Assert.Contains("配置: left → right", markdown);
            Assert.Contains("太字: 0 → on", markdown);
            Assert.Contains("文字色: 000000 → FF0000", markdown);
            Assert.Equal(original, await File.ReadAllBytesAsync(source));
            var service = new DocumentService(null, null, discoverPdfRasterizer: false);
            var package = await service.ExportAiPackageAsync(new(new(source, "unused.md"), Path.Combine(Path.GetDirectoryName(source)!, "package")));
            Assert.Contains("太字: 0 → on", string.Join("\n", package.Parts.Select(part => part.Markdown)));
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task Inserted_deleted_rows_and_cells_are_marked_and_structural_changes_annotated()
    {
        static string Cell(string text, string properties = "") => $"<w:tc><w:tcPr>{properties}</w:tcPr>{P(text)}</w:tc>";
        var source = Document("<w:tbl><w:tblPr><w:tblW w:w=\"3000\" w:type=\"dxa\"/><w:tblPrChange><w:tblPr><w:tblW w:w=\"2000\" w:type=\"dxa\"/></w:tblPr></w:tblPrChange></w:tblPr>" +
            "<w:tblGrid><w:gridCol w:w=\"3000\"/><w:tblGridChange><w:tblGrid><w:gridCol w:w=\"2000\"/></w:tblGrid></w:tblGridChange></w:tblGrid>" +
            "<w:tr><w:trPr><w:del/></w:trPr>" + Cell("removed row") + "</w:tr>" +
            "<w:tr><w:trPr><w:ins/></w:trPr>" + Cell("added row") + "</w:tr>" +
            "<w:tr>" + Cell("removed cell", "<w:cellDel/>") + Cell("added cell", "<w:cellIns/>") +
            Cell("merged", "<w:cellMerge w:vMergeOrig=\"cont\" w:vMerge=\"rest\"/><w:tcPrChange><w:tcPr><w:gridSpan w:val=\"2\"/></w:tcPr></w:tcPrChange>") + "</w:tr></w:tbl>");
        try
        {
            var adapter = new DocxAdapter();
            var final = await adapter.ExtractAsync(source);
            var finalText = new ReadableMarkdownSerializer().Serialize(final.Graph);
            Assert.Contains("added row", string.Join(" ", final.Graph.Nodes.SelectMany(node => node.Content is TableNodeContent table ? table.Rows.SelectMany(row => row).Select(cell => cell.Text) : [])));
            Assert.DoesNotContain("removed row", finalText);
            Assert.DoesNotContain("removed cell", finalText);
            Assert.Contains("added row", finalText);
            var visible = await adapter.ExtractAsync(source, new(ShowTrackedRevisions: true));
            var markdown = new ReadableMarkdownSerializer().Serialize(visible.Graph);
            Assert.Contains("~~removed row~~", markdown);
            Assert.Contains("<ins>added row</ins>", markdown);
            Assert.Contains("~~removed cell~~", markdown);
            Assert.Contains("<ins>added cell</ins>", markdown);
            Assert.Contains("行を削除", markdown); Assert.Contains("セルを追加", markdown);
            Assert.Contains("セル結合: cont → rest", markdown);
            Assert.Contains("列の結合数: 2 → 継承", markdown);
            Assert.Contains("列幅: w=2000 → w=3000", markdown);
            Assert.Contains("表幅:", markdown);
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task Nested_table_revision_notes_are_retained_once_and_hidden_run_notes_do_not_leak()
    {
        var source = Document("<w:tbl><w:tr><w:tc><w:tbl><w:tr><w:trPr><w:ins/></w:trPr><w:tc>" + P("nested addition") +
            "</w:tc></w:tr></w:tbl></w:tc></w:tr></w:tbl>" +
            "<w:p><w:r><w:rPr><w:vanish/><w:b/><w:rPrChange><w:rPr/></w:rPrChange></w:rPr><w:t>secret</w:t></w:r></w:p>");
        try
        {
            var visible = await new DocxAdapter().ExtractAsync(source, new(ShowTrackedRevisions: true));
            var markdown = new ReadableMarkdownSerializer().Serialize(visible.Graph);
            Assert.Contains("<ins>nested addition</ins>", markdown);
            Assert.Equal(1, markdown.Split("行を追加").Length - 1);
            Assert.DoesNotContain("secret", markdown);
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task Section_and_paragraph_mark_changes_survive_empty_blocks()
    {
        var source = Document("<w:p><w:pPr><w:rPr><w:del/></w:rPr></w:pPr></w:p>" +
            "<w:sectPr><w:pgMar w:top=\"1440\"/><w:sectPrChange><w:sectPr><w:pgMar w:top=\"720\"/></w:sectPr></w:sectPrChange></w:sectPr>");
        try
        {
            var visible = await new DocxAdapter().ExtractAsync(source, new(ShowTrackedRevisions: true));
            var markdown = new ReadableMarkdownSerializer().Serialize(visible.Graph);
            Assert.Contains("段落区切りを削除", markdown);
            Assert.Contains("ページ余白: top=720 → top=1440", markdown);
        }
        finally { Cleanup(source); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Formatting_revision_boundaries_reject_edits_and_keep_original_bytes(bool table)
    {
        var paragraph = "<w:p><w:r><w:rPr><w:b/><w:rPrChange><w:rPr/></w:rPrChange></w:rPr><w:t>original</w:t></w:r></w:p>";
        var source = Document(table ? "<w:tbl><w:tr><w:tc>" + paragraph + "</w:tc></w:tr></w:tbl>" : paragraph);
        try
        {
            var bytes = await File.ReadAllBytesAsync(source);
            var adapter = new DocxAdapter();
            var result = await adapter.ExtractAsync(source);
            var node = Assert.Single(result.Graph.Nodes, node => node.Kind == (table ? NodeKind.Table : NodeKind.Paragraph));
            NodeContent editedContent = table ? new TableNodeContent([new[] { new TableCell("edited") }]) : new RichTextNodeContent([new TextRun("edited", Bold: true)]);
            var edited = result.Graph with { Partitions = result.Graph.Partitions.Select(part => part with
                { Nodes = part.Nodes.Select(candidate => candidate.Id == node.Id ? candidate with { Content = editedContent } : candidate).ToArray() }).ToArray() };
            var output = Path.Combine(Path.GetDirectoryName(source)!, "refused.docx");
            await Assert.ThrowsAsync<InvalidDataException>(async () => await adapter.RestoreAsync(result, edited, output));
            Assert.False(File.Exists(output));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        }
        finally { Cleanup(source); }
    }
}
