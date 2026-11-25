using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Tests.Api;

namespace DocRedock.Tests.Docx;

/// <summary>A page break is read where the document has it among its paragraph's text and drawings, and the AI package
/// and the DRMD list a paragraph's text, page breaks and drawings in the same order as the readable outputs.</summary>
public sealed partial class DocxParagraphDrawingOrderTests
{
    private const string Break = "<w:r><w:br w:type='page'/></w:r>";
    private const string Mark = "[BREAK]";
    // Each repetition has other node ids, so an order that only follows the ids is wrong in some of them.
    private const int Repetitions = 8;

    [Fact(Timeout = 30000)]
    public async Task A_page_break_at_the_start_of_a_paragraph_is_read_before_its_text()
    {
        var output = await Export(WriteDocx("break-start", Repeat(index =>
            $"<w:p><w:pPr><w:pStyle w:val='Heading1'/></w:pPr><w:r><w:br w:type='page'/><w:t>Chapter {index}.</w:t></w:r>" +
            Frame(false, 100 + index, $"CHAPTER PICTURE {index}.", PictureUri, Picture(index + 1)) + "</w:p>")));
        var expected = Each(index => [Mark, $"Chapter {index}.", $"CHAPTER PICTURE {index}."]);
        Assert.Equal(expected, Read(output.Markdown, expected));
        Assert.Equal(expected, Read(output.Html, expected));
    }

    [Fact(Timeout = 30000)]
    public async Task A_page_break_alone_in_a_paragraph_is_read_between_its_neighbours_and_before_its_own_paragraph()
    {
        var output = await Export(WriteDocx("break-alone", Repeat(index =>
            $"<w:p><w:r><w:t>Before {index}.</w:t></w:r></w:p><w:p>{Break}</w:p><w:p><w:r><w:t>After {index}.</w:t></w:r></w:p>")));
        var expected = Each(index => [$"Before {index}.", Mark, $"After {index}."]);
        Assert.Equal(expected, Read(output.Markdown, expected));
        Assert.Equal(expected, Read(output.Html, expected));
        var nodes = Nodes(output.Graph).InSourceOrder().ToArray();
        var breaks = nodes.Where(node => node.Kind == NodeKind.PageBreak).ToArray();
        Assert.Equal(Repetitions, breaks.Length);
        Assert.All(breaks, node => Assert.True(Array.IndexOf(nodes, node) < Array.FindIndex(nodes, parent => parent.Id == node.ParentId)));
    }

    [Fact(Timeout = 30000)]
    public async Task A_page_break_between_two_pictures_is_read_between_them()
    {
        var output = await Export(WriteDocx("break-between", BreakBetweenPictures()));
        var expected = Each(index => [$"Host {index}.", $"PICTURE A {index}.", Mark, $"PICTURE B {index}."]);
        Assert.Equal(expected, Read(output.Markdown, expected));
        Assert.Equal(expected, Read(output.Html, expected));
    }

    [Fact(Timeout = 30000)]
    public async Task A_page_break_between_a_picture_and_a_text_box_is_read_between_them()
    {
        var output = await Export(WriteDocx("break-box", Repeat(index =>
            $"<w:p><w:r><w:t>Host {index}.</w:t></w:r>" + Frame(false, 100 + index, $"PICTURE A {index}.", PictureUri, Picture(index + 1)) + Break +
            Frame(true, 200 + index, null, ShapeUri, TextBox($"BOX B {index}.")) + "</w:p>")));
        var expected = Each(index => [$"Host {index}.", $"PICTURE A {index}.", Mark, $"BOX B {index}."]);
        Assert.Equal(expected, Read(output.Markdown, expected));
        Assert.Equal(expected, Read(output.Html, expected));
    }

    [Fact(Timeout = 30000)]
    public async Task A_page_break_after_text_is_read_after_the_whole_text_of_its_paragraph()
    {
        var output = await Export(WriteDocx("break-after-text", Repeat(index =>
            $"<w:p><w:r><w:t>Host {index}, first half</w:t></w:r>{Break}<w:r><w:t> and second half {index}.</w:t></w:r></w:p>")));
        var expected = Each(index => [$"Host {index}, first half and second half {index}.", Mark]);
        Assert.Equal(expected, Read(output.Markdown, expected));
        Assert.Equal(expected, Read(output.Html, expected));
    }

    [Fact(Timeout = 30000)]
    public async Task Two_page_breaks_of_one_paragraph_keep_their_order_before_the_text_and_among_the_drawings()
    {
        var output = await Export(WriteDocx("break-two", Repeat(index =>
            $"<w:p>{Break}{Break}<w:r><w:t>Host {index}.</w:t></w:r>{Break}" + Frame(false, 100 + index, $"PICTURE A {index}.", PictureUri, Picture(index + 1)) +
            Break + "</w:p>")));
        var expected = Each(index => [Mark, Mark, $"Host {index}.", Mark, $"PICTURE A {index}.", Mark]);
        Assert.Equal(expected, Read(output.Markdown, expected));
        Assert.Equal(expected, Read(output.Html, expected));
        foreach (var paragraph in Nodes(output.Graph).Where(node => node.Kind == NodeKind.Paragraph))
        {
            var breaks = Nodes(output.Graph).Where(node => node.Kind == NodeKind.PageBreak && node.ParentId == paragraph.Id).InSourceOrder();
            // The page_break locator counts the breaks of the paragraph in document order.
            Assert.Equal(["0", "1", "2", "3"], breaks.Select(node => node.Source!.Locators.Single(locator => locator.Kind == "page_break").Value));
        }
    }

    [Fact(Timeout = 30000)]
    public async Task A_page_break_in_a_hidden_run_is_hidden_content_and_a_deleted_one_follows_the_revision_display()
    {
        // A break in hidden text does not break the page Word shows: the marker is hidden content, kept at its place.
        // A deleted break is treated like a picture in a deleted run: shown while tracked revisions are shown (the default).
        var output = await Export(WriteDocx("break-hidden", Repeat(index =>
            $"<w:p><w:r><w:t>Host {index}.</w:t></w:r>" + Frame(false, 100 + index, $"PICTURE A {index}.", PictureUri, Picture(index + 1)) +
            (index % 2 == 0 ? "<w:r><w:rPr><w:vanish/></w:rPr><w:br w:type='page'/></w:r>" : "<w:del w:id='1' w:author='a'><w:r><w:br w:type='page'/></w:r></w:del>") +
            Frame(true, 200 + index, $"PICTURE B {index}.", PictureUri, Picture(index + 20)) + "</w:p>")));
        var expected = Each(index => index % 2 == 0
            ? [$"Host {index}.", $"PICTURE A {index}.", $"PICTURE B {index}."]
            : [$"Host {index}.", $"PICTURE A {index}.", Mark, $"PICTURE B {index}."]);
        Assert.Equal(expected, Read(output.Markdown, [.. expected, Mark]));
        Assert.Equal(expected, Read(output.Html, [.. expected, Mark]));
        var breaks = Nodes(output.Graph).Where(node => node.Kind == NodeKind.PageBreak).ToArray();
        Assert.Equal(breaks.Length / 2 + breaks.Length % 2, breaks.Count(node => node.Layer == ContentLayer.Hidden));
        Assert.All(breaks, node => Assert.True(DocumentSourceOrder.SiblingSequence(node) >= 0));
    }

    [Fact(Timeout = 30000)]
    public async Task A_page_break_in_a_hidden_run_of_a_table_cell_is_hidden_content()
    {
        var output = await Export(WriteDocx("cell-break-hidden", CellTable(
            "<w:p><w:r><w:t>Cell text</w:t></w:r><w:r><w:rPr><w:vanish/></w:rPr><w:br w:type='page'/></w:r></w:p>")));
        var marker = Assert.Single(Nodes(output.Graph), node => node.Kind == NodeKind.PageBreak);
        Assert.Equal(ContentLayer.Hidden, marker.Layer);
        Assert.DoesNotMatch("(?m)^> Page break in table: ", output.Markdown);
        Assert.DoesNotContain("<hr class=\"page-break in-cell\">", output.Html);
    }

    [Fact(Timeout = 30000)]
    public async Task The_ai_package_lists_text_page_break_and_drawings_of_a_paragraph_in_document_order()
    {
        var source = WriteDocx("package", BreakBetweenPictures());
        var target = Path.Combine(root, "package-out");
        var result = await service.ExportAiPackageAsync(new AiPackageExportOptions(
            new ReadableDocumentExportOptions(source, "unused.md", ContentPolicy: "visible", Language: "en"), target));
        var expected = Each(index => [$"Host {index}.", $"PICTURE A {index}.", Mark, $"PICTURE B {index}."]);
        Assert.Equal(expected, Read(string.Concat(result.Parts.Select(part => part.Markdown)), expected));
        var ids = DocumentOrderIds(result.Graph);
        Assert.Equal(ids, result.Parts.SelectMany(part => part.NodeIds).Where(ids.Contains));
        var manifest = await AiPackageManifestReader.ReadAsync(target);
        var index = await AiPackageManifestReader.ReadSourceIndexAsync(target, manifest);
        Assert.Equal(ids, manifest.Parts.SelectMany(part => index[part.Id]).SelectMany(group => group.NodeIds).Where(ids.Contains));
    }

    [Fact(Timeout = 30000)]
    public async Task The_drmd_lists_text_page_break_and_drawings_in_document_order_and_restores_byte_identically()
    {
        var source = WriteDocx("drmd", BreakBetweenPictures());
        var markdown = Path.Combine(root, "drmd.md");
        var workspace = Path.Combine(root, "drmd.drmd");
        var restoredPath = Path.Combine(root, "drmd-restored.docx");
        var exported = await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown));
        var ids = DocumentOrderIds(exported.Graph);
        var blocks = Regex.Matches(await File.ReadAllTextAsync(markdown), "<!--drmd:block id=(\\S+) ").Select(match => match.Groups[1].Value);
        Assert.Equal(ids, blocks.Where(ids.Contains));
        var restored = await service.RestoreAsync(new DocumentRestoreOptions(workspace, restoredPath, markdown));
        Assert.True(restored.Succeeded);
        Assert.Equal(FidelityLevel.F0, restored.Fidelity);
        Assert.Equal(SHA256.HashData(await File.ReadAllBytesAsync(source)), SHA256.HashData(await File.ReadAllBytesAsync(restoredPath)));
    }

    [Theory(Timeout = 30000)]
    [InlineData("xlsx")]
    [InlineData("pptx")]
    public async Task Nodes_of_the_other_formats_record_no_place_and_are_listed_by_order_and_id_as_before(string format)
    {
        var source = AiPackageExportTests.WriteSource(root, format);
        var exported = await service.ExportAsync(new DocumentExportOptions(source, Path.Combine(root, format + ".drmd"), Path.Combine(root, format + ".md"), ContentPolicy: "complete"));
        Assert.NotEmpty(Nodes(exported.Graph));
        foreach (var partition in exported.Graph.Partitions)
        {
            Assert.DoesNotContain(partition.Nodes, node => node.Extensions?.ContainsKey(DocumentSourceOrder.SiblingSequenceKey) == true);
            Assert.Equal(partition.Nodes.OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal).Select(node => node.Id),
                partition.Nodes.InSourceOrder().Select(node => node.Id));
        }
    }

    [Fact]
    public void A_node_without_a_recorded_place_sorts_by_order_and_id_between_leading_breaks_and_drawings()
    {
        static DocumentNode Node(string id, int order, object? place = null) => new(id, NodeKind.Paragraph, null, order, ContentLayer.Body, new TextNodeContent(id),
            Extensions: place is null ? null : new Dictionary<string, JsonElement> { [DocumentSourceOrder.SiblingSequenceKey] = JsonSerializer.SerializeToElement(place) });
        // A graph saved before the key existed has none: order, then id.
        Assert.Equal(["a", "c", "b"], new[] { Node("b", 1), Node("c", 0), Node("a", 0) }.InSourceOrder().Select(node => node.Id));
        Assert.Equal(["z-leading-1", "y-leading-2", "x-paragraph", "w-first", "a-second", "next"],
            new[] { Node("next", 2), Node("a-second", 1, 1), Node("w-first", 1, "0"), Node("x-paragraph", 1), Node("y-leading-2", 1, -2), Node("z-leading-1", 1, -3) }
                .InSourceOrder().Select(node => node.Id));
        Assert.Equal(DocumentSourceOrder.NoSequence, DocumentSourceOrder.SiblingSequence(Node("text", 0, "not a number")));
    }

    private static string BreakBetweenPictures() => Repeat(index =>
        $"<w:p><w:r><w:t>Host {index}.</w:t></w:r>" + Frame(false, 100 + index, $"PICTURE A {index}.", PictureUri, Picture(index + 1)) + Break +
        Frame(true, 200 + index, $"PICTURE B {index}.", PictureUri, Picture(index + 20)) + "</w:p>");

    /// <summary>The ids of each paragraph, its first picture, its page break and its second picture, as the document has them.</summary>
    private static string[] DocumentOrderIds(DocumentGraph graph)
    {
        var nodes = Nodes(graph);
        return [.. nodes.Where(node => node.Kind == NodeKind.Paragraph).OrderBy(node => node.Order).SelectMany(paragraph =>
        {
            var pictures = nodes.Where(node => node.Kind == NodeKind.Image && node.ParentId == paragraph.Id)
                .OrderBy(node => ((ReferenceNodeContent)node.Content).AltText, StringComparer.Ordinal).ToArray();
            Assert.Equal(2, pictures.Length);
            return new[] { paragraph.Id, pictures[0].Id, nodes.Single(node => node.Kind == NodeKind.PageBreak && node.ParentId == paragraph.Id).Id, pictures[1].Id };
        })];
    }

    private static string Repeat(Func<int, string> paragraph) => string.Concat(Enumerable.Range(0, Repetitions).Select(paragraph));

    private static string[] Each(Func<int, string[]> expected) => [.. Enumerable.Range(0, Repetitions).SelectMany(expected)];

    /// <summary>The expected texts and the page-break separators of an output, in the order they occur.</summary>
    private static string[] Read(string output, string[] expected)
    {
        var marked = Regex.Replace(output.ReplaceLineEndings("\n"), "(?m)^---$|<hr class=\"page-break\">", Mark);
        var found = Regex.Matches(marked, string.Join("|", expected.Distinct().OrderByDescending(text => text.Length).Select(Regex.Escape))).Select(match => match.Value).ToArray();
        // A picture's description is written more than once (its alternative text and its caption): once is enough here.
        return [.. found.Where((text, index) => text == Mark || index == 0 || found[index - 1] != text)];
    }
}
