using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Tests.Markdown;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using GfmTable = Markdig.Extensions.Tables.Table;
using GfmRow = Markdig.Extensions.Tables.TableRow;
using GfmCell = Markdig.Extensions.Tables.TableCell;

namespace DocRedock.Tests.Docx;

/// <summary>Small anonymous DOCX packages exercising semantic data through real API exports.</summary>
public sealed class DocxSemanticExportContractTests : IDisposable
{
    [Fact(Timeout = 30000)]
    public async Task Repeated_image_relationships_in_separate_cell_paragraphs_keep_distinct_instances()
    {
        var body = Table(2, HeaderRow(Cell("Item"), Cell("Figure")), Row(Cell("ROW-ITEM"),
            CellXml("<w:p>" + PictureRun + "</w:p><w:p>" + PictureRun + "</w:p>")));
        var output = await ExportBothAsync("repeated-cell-picture", body,
            "<Relationship Id=\"rImage\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/image1.png\"/>", image: true);
        var images = output.Graph.Nodes.Where(n => n.Kind == NodeKind.Image).ToArray();
        Assert.Equal(2, images.Length);
        Assert.Equal(2, images.Select(n => n.Id).Distinct().Count());
        Assert.All(images, image => Assert.NotNull(image.ParentId));
        Assert.Equal(2, Regex.Matches(output.Html, "alt=\"CELL-IMAGE\"").Count);
        Assert.Equal(2, Regex.Matches(output.Markdown, "<img\\b").Count + MarkdownStructure.Images(MarkdownStructure.Parse(output.Markdown)).Count);
    }

    private readonly string root = Directory.CreateTempSubdirectory("docredock-docx-semantic-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);

    public void Dispose() => Directory.Delete(root, true);

    [Fact(Timeout = 30000)]
    public async Task Table_cell_hyperlink_preserves_its_target_and_label_in_both_readable_formats()
    {
        const string target = "https://example.invalid/docs?mode=a&value=b#section";
        var relationship = "<Relationship Id=\"rLink\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" " +
            "Target=\"https://example.invalid/docs?mode=a&amp;value=b#section\" TargetMode=\"External\"/>";
        var body = "<w:p><w:hyperlink r:id=\"rLink\"><w:r><w:t>BODY-LINK</w:t></w:r></w:hyperlink></w:p>" +
            Table(2,
                HeaderRow(Cell("Field"), Cell("Value")),
                Row(Cell("manual"), CellXml("<w:p><w:hyperlink r:id=\"rLink\"><w:r><w:t>TABLE-LINK</w:t></w:r></w:hyperlink></w:p>")));
        var output = await ExportBothAsync("cell-link", body, relationship);
        var parsed = MarkdownStructure.Parse(output.Markdown);
        var table = Assert.Single(((MarkdownObject)parsed).Descendants<GfmTable>());
        var cell = Assert.IsType<GfmCell>(Assert.IsType<GfmRow>(table[1])[1]);
        var link = Assert.Single(((MarkdownObject)cell).Descendants<LinkInline>(), link => !link.IsImage);

        Assert.Equal(target, link.Url);
        Assert.Equal("TABLE-LINK", string.Concat(link.Descendants<LiteralInline>().Select(literal => literal.Content.ToString())));
        // A working ordinary paragraph link is a control; it must not mask a missing table link.
        Assert.Contains(MarkdownStructure.Links(parsed), candidate => candidate.Url == target &&
            candidate.Descendants<LiteralInline>().Any(literal => literal.Content.ToString() == "BODY-LINK"));
        var nativeLink = Regex.Match(output.Html,
            "<a\\b[^>]*href=\"([^\"]*)\"[^>]*>\\s*TABLE-LINK\\s*</a>",
            RegexOptions.CultureInvariant);
        Assert.True(nativeLink.Success, "The cell label survived, but its native HTML hyperlink did not.");
        Assert.Equal(target, WebUtility.HtmlDecode(nativeLink.Groups[1].Value));
        Assert.Contains(output.Graph.Nodes, node => node.Kind == NodeKind.Table &&
            Assert.IsType<TableNodeContent>(node.Content).Rows.Any(row => row.Any(value => value.Text == "TABLE-LINK")));
    }

    [Fact(Timeout = 30000)]
    public async Task Picture_in_a_table_cell_is_extracted_and_rendered_with_its_description()
    {
        var body = Paragraph("BEFORE-PICTURE-TABLE") + Table(2,
            HeaderRow(Cell("Item"), Cell("Figure")),
            Row(Cell("ROW-ITEM"), CellXml("<w:p>" + PictureRun + "</w:p>"))) +
            Paragraph("AFTER-PICTURE-TABLE");
        var output = await ExportBothAsync("cell-picture", body,
            "<Relationship Id=\"rImage\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/image1.png\"/>",
            image: true);

        var image = Assert.Single(output.Graph.Nodes, node => node.Kind == NodeKind.Image);
        var reference = Assert.IsType<ReferenceNodeContent>(image.Content);
        Assert.Equal("CELL-IMAGE", reference.AltText);
        Assert.NotNull(image.Source);
        Assert.Contains(image.Source.Locators, locator => locator.Kind == "image_relationship" && locator.Value == "rImage");
        var inlineImages = MarkdownStructure.Images(MarkdownStructure.Parse(output.Markdown));
        var htmlImages = Regex.Matches(output.Markdown, "<img\\b[^>]*src=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        Assert.Equal(1, inlineImages.Count + htmlImages.Count);
        var imageUrl = inlineImages.Count == 1 ? inlineImages[0].Url : WebUtility.HtmlDecode(htmlImages[0].Groups[1].Value);
        Assert.False(string.IsNullOrWhiteSpace(imageUrl));
        var asset = Path.GetFullPath(Path.Combine(root,
            Uri.UnescapeDataString(imageUrl!).Replace('/', Path.DirectorySeparatorChar)));
        Assert.True(File.Exists(asset), $"Markdown references a missing image asset: {imageUrl}");
        Assert.Contains("CELL-IMAGE", output.Markdown, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,", output.Html, StringComparison.Ordinal);
        Assert.Contains("CELL-IMAGE", output.Html, StringComparison.Ordinal);
        Assert.Matches(new Regex("<td\\b[^>]*>(?:(?!</td>).)*alt=\"CELL-IMAGE\"", RegexOptions.Singleline), output.Html);
        Assert.Matches("<img\\b[^>]*alt=\"CELL-IMAGE\"[^>]*width=\"96\" height=\"48\"[^>]*style=\"aspect-ratio:96/48\"", output.Html);
        MarkdownStructure.AssertInOrder(VisibleNativeText(output.Html),
            "BEFORE-PICTURE-TABLE", "ROW-ITEM", "AFTER-PICTURE-TABLE");
    }

    [Fact(Timeout = 30000)]
    public async Task Grid_after_retains_all_table_payload_and_the_omitted_right_hand_column()
    {
        var omittedRight = "<w:tr><w:trPr><w:gridAfter w:val=\"1\"/></w:trPr>" +
            Cell("ROW-A") + Cell("ROW-B") + "</w:tr>";
        var body = Paragraph("BEFORE-TABLE") + Table(3,
            HeaderRow(Cell("ID"), Cell("Value"), Cell("Remark")),
            omittedRight,
            Row(Cell("ROW-C"), Cell("ROW-D"), Cell("ROW-E"))) + Paragraph("AFTER-TABLE");
        var output = await ExportBothAsync("grid-after", body);
        var node = Assert.Single(output.Graph.Nodes, node => node.Kind == NodeKind.Table);
        var content = Assert.IsType<TableNodeContent>(node.Content);

        Assert.True(TableGrid.TryCreate(content, out var sourceGrid, out var error),
            $"A valid gridAfter row must remain a rectangular source grid: {error}");
        Assert.Equal(3, sourceGrid!.ColumnCount);
        Assert.Equal(3, content.Rows[1].Sum(cell => cell.ColSpan));
        Assert.Equal("ROW-A", sourceGrid.Rows[1][0].Origin.Text);
        Assert.Equal("ROW-B", sourceGrid.Rows[1][1].Origin.Text);
        Assert.Equal("", sourceGrid.Rows[1][2].Origin.Text);
        var table = Assert.Single(((MarkdownObject)MarkdownStructure.Parse(output.Markdown)).Descendants<GfmTable>());
        Assert.Equal(3, table.Count);
        Assert.All(table, row => Assert.Equal(3, Assert.IsType<GfmRow>(row).Count));
        Assert.Equal("ROW-A", Text(Assert.IsType<GfmCell>(Assert.IsType<GfmRow>(table[1])[0])));
        Assert.Equal("ROW-B", Text(Assert.IsType<GfmCell>(Assert.IsType<GfmRow>(table[1])[1])));
        Assert.Equal("", Text(Assert.IsType<GfmCell>(Assert.IsType<GfmRow>(table[1])[2])));
        foreach (var value in new[] { "ROW-A", "ROW-B", "ROW-C", "ROW-D", "ROW-E" })
        {
            Assert.Contains(value, MarkdownStructure.DisplayedText(MarkdownStructure.Parse(output.Markdown)), StringComparison.Ordinal);
            Assert.Contains(value, VisibleNativeText(output.Html), StringComparison.Ordinal);
        }
        MarkdownStructure.AssertInOrder(MarkdownStructure.DisplayedText(MarkdownStructure.Parse(output.Markdown)),
            "BEFORE-TABLE", "ROW-A", "ROW-B", "ROW-C", "ROW-D", "ROW-E", "AFTER-TABLE");
    }

    [Fact(Timeout = 30000)]
    public async Task Interleaved_full_width_group_rows_keep_their_scope_and_source_order()
    {
        var body = Table(2,
            HeaderRow(Cell("Item"), Cell("Value")),
            Row(Cell("GROUP-A-CONDITION", span: 2)),
            Row(Cell("ITEM-A"), Cell("VALUE-A")),
            Row(Cell("GROUP-B-CONDITION", span: 2)),
            Row(Cell("ITEM-B"), Cell("VALUE-B")),
            Row(Cell("TAIL-NOTE", span: 2)));
        var output = await ExportBothAsync("interleaved-groups", body);
        var expectedOrder = new[]
        {
            "GROUP-A-CONDITION", "ITEM-A", "VALUE-A",
            "GROUP-B-CONDITION", "ITEM-B", "VALUE-B", "TAIL-NOTE"
        };

        MarkdownStructure.AssertInOrder(
            MarkdownStructure.DisplayedText(MarkdownStructure.Parse(output.Markdown)), expectedOrder);
        MarkdownStructure.AssertInOrder(VisibleNativeText(output.Html), expectedOrder);
        // Moving only a trailing note below the final table remains permitted; moving an
        // earlier group below its records changes which condition the records belong to.
        var content = Assert.IsType<TableNodeContent>(
            Assert.Single(output.Graph.Nodes, node => node.Kind == NodeKind.Table).Content);
        Assert.Equal(2, content.Rows[1][0].ColSpan);
        Assert.Equal(2, content.Rows[3][0].ColSpan);
        Assert.Equal(2, content.Rows[5][0].ColSpan);
    }

    [Fact(Timeout = 30000)]
    public async Task Hidden_outer_runs_hide_their_text_boxes_in_both_formats_without_hiding_visible_inline_or_page_boxes()
    {
        const string privateInline = "PRIVATE-HIDDEN-SHAPE-INLINE";
        const string privatePage = "PRIVATE-HIDDEN-SHAPE-PAGE";
        var body = Paragraph("VISIBLE_LABEL") +
            TextBoxParagraph(1, "INLINE_VISIBLE_LABEL", anchored: false) +
            TextBoxParagraph(2, "PAGE_VISIBLE_LABEL", anchored: true) +
            TextBoxParagraph(3, privateInline, anchored: false, hiddenOuterRun: true) +
            TextBoxParagraph(4, privatePage, anchored: true, hiddenOuterRun: true);
        // Exactly one actual DOCX is reused for all six exports. Its inner textbox run
        // explicitly clears vanish; the hidden containing run must still hide the whole box.
        var source = WriteDocx("outer-hidden-textboxes", body, "", image: false);

        foreach (var policy in new[] { "visible", "sanitized", "complete" })
        {
            var markdownPath = Path.Combine(root, "outer-hidden-textboxes-" + policy + ".md");
            var htmlPath = Path.Combine(root, "outer-hidden-textboxes-" + policy + ".html");
            var markdown = await service.ExportReadableAsync(new ReadableDocumentExportOptions(
                source, markdownPath, ContentPolicy: policy, Language: "en"));
            await service.ExportReadableAsync(new ReadableDocumentExportOptions(
                source, htmlPath, ContentPolicy: policy, Language: "en",
                OutputFormat: ReadableOutputFormat.Html));
            var md = await File.ReadAllTextAsync(markdownPath);
            var native = await File.ReadAllTextAsync(htmlPath);
            var displayedMarkdown = MarkdownStructure.DisplayedText(MarkdownStructure.Parse(md));
            var displayedNative = VisibleNativeText(native);

            foreach (var label in new[] { "VISIBLE_LABEL", "INLINE_VISIBLE_LABEL", "PAGE_VISIBLE_LABEL" })
            {
                Assert.Contains(label, native, StringComparison.Ordinal);
                Assert.Contains(label, displayedMarkdown, StringComparison.Ordinal);
                Assert.Contains(label, displayedNative, StringComparison.Ordinal);
            }

            if (policy == "complete")
            {
                foreach (var label in new[] { privateInline, privatePage })
                {
                    Assert.Contains(label, displayedMarkdown, StringComparison.Ordinal);
                    Assert.Contains(label, displayedNative, StringComparison.Ordinal);
                }
            }
            else
            {
                // Check the full payload as well as displayed text: hidden text in HTML
                // metadata, a collapsed disclosure or a diagram source is still a leak.
                Assert.DoesNotContain("PRIVATE-HIDDEN-SHAPE", md, StringComparison.Ordinal);
                Assert.DoesNotContain("PRIVATE-HIDDEN-SHAPE", native, StringComparison.Ordinal);
                Assert.DoesNotContain("PRIVATE-HIDDEN-SHAPE", displayedMarkdown, StringComparison.Ordinal);
                Assert.DoesNotContain("PRIVATE-HIDDEN-SHAPE", displayedNative, StringComparison.Ordinal);
            }

            foreach (var label in new[] { privateInline, privatePage })
            {
                var sourceBox = Assert.Single(markdown.Graph.Nodes, node =>
                    node.Kind == NodeKind.TextBox && node.Content is TextNodeContent text &&
                    text.Text.Contains(label, StringComparison.Ordinal));
                Assert.Equal(ContentLayer.Hidden, sourceBox.Layer);
            }
        }
    }

    private static string TextBoxParagraph(int id, string text, bool anchored, bool hiddenOuterRun = false)
    {
        var frame = anchored
            ? "<wp:anchor distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\" simplePos=\"0\" relativeHeight=\"0\" behindDoc=\"0\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"1\">" +
              "<wp:simplePos x=\"0\" y=\"0\"/><wp:positionH relativeFrom=\"page\"><wp:posOffset>95250</wp:posOffset></wp:positionH>" +
              "<wp:positionV relativeFrom=\"page\"><wp:posOffset>190500</wp:posOffset></wp:positionV>" +
              "<wp:extent cx=\"952500\" cy=\"381000\"/><wp:wrapNone/>"
            : "<wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"952500\" cy=\"381000\"/>";
        var shape = $"""
            <wp:docPr id="{id}" name="SourceBox{id}"/><wp:cNvGraphicFramePr/>
            <a:graphic><a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingShape">
            <wps:wsp xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape">
            <wps:cNvSpPr txBox="1"/><wps:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="952500" cy="381000"/></a:xfrm>
            <a:prstGeom prst="rect"><a:avLst/></a:prstGeom><a:noFill/></wps:spPr>
            <wps:txbx><w:txbxContent><w:p><w:r><w:rPr><w:vanish w:val="0"/></w:rPr>
            <w:t>{System.Security.SecurityElement.Escape(text)}</w:t></w:r></w:p></w:txbxContent></wps:txbx>
            <wps:bodyPr/></wps:wsp></a:graphicData></a:graphic>
            """;
        return "<w:p><w:r>" + (hiddenOuterRun ? "<w:rPr><w:vanish/></w:rPr>" : "") +
            "<w:drawing>" + frame + shape + (anchored ? "</wp:anchor>" : "</wp:inline>") +
            "</w:drawing></w:r></w:p>";
    }

    private async Task<Exported> ExportBothAsync(string name, string body,
        string relationships = "", bool image = false)
    {
        var source = WriteDocx(name, body, relationships, image);
        var markdownPath = Path.Combine(root, name + ".md");
        var markdown = await service.ExportReadableAsync(
            new ReadableDocumentExportOptions(source, markdownPath, Language: "en"));
        var htmlPath = Path.Combine(root, name + ".html");
        await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, htmlPath,
            Language: "en", OutputFormat: ReadableOutputFormat.Html));
        return new(markdown.Graph, await File.ReadAllTextAsync(markdownPath),
            await File.ReadAllTextAsync(htmlPath));
    }

    private string WriteDocx(string name, string body, string relationships, bool image)
    {
        var source = Path.Combine(root, name + ".docx");
        using var archive = ZipFile.Open(source, ZipArchiveMode.Create);
        void Write(string path, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        Write("[Content_Types].xml", """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
            <Default Extension="xml" ContentType="application/xml"/>
            <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
            <Default Extension="png" ContentType="image/png"/>
            <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
            </Types>
            """);
        Write("_rels/.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            <Relationship Id="rDocument" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
            </Relationships>
            """);
        Write("word/document.xml", $"""
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
            xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
            xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
            xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
            xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">
            <w:body>{body}<w:sectPr/></w:body></w:document>
            """);
        Write("word/_rels/document.xml.rels",
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            relationships + "</Relationships>");
        if (image)
        {
            using var stream = archive.CreateEntry("word/media/image1.png").Open();
            stream.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a1u8AAAAASUVORK5CYII="));
        }
        return source;
    }

    private static string Paragraph(string text) =>
        "<w:p><w:r><w:t>" + System.Security.SecurityElement.Escape(text) + "</w:t></w:r></w:p>";

    private static string Cell(string text, int span = 1) => CellXml(Paragraph(text), span);

    private static string CellXml(string paragraphs, int span = 1) =>
        "<w:tc><w:tcPr>" + (span > 1 ? $"<w:gridSpan w:val=\"{span}\"/>" : "") +
        "</w:tcPr>" + paragraphs + "</w:tc>";

    private static string Row(params string[] cells) => "<w:tr>" + string.Concat(cells) + "</w:tr>";

    private static string HeaderRow(params string[] cells) =>
        "<w:tr><w:trPr><w:tblHeader/></w:trPr>" + string.Concat(cells) + "</w:tr>";

    private static string Table(int columns, params string[] rows) =>
        "<w:tbl><w:tblPr/><w:tblGrid>" +
        string.Concat(Enumerable.Repeat("<w:gridCol w:w=\"1800\"/>", columns)) +
        "</w:tblGrid>" + string.Concat(rows) + "</w:tbl>";

    private static string Text(GfmCell cell) =>
        string.Concat(((MarkdownObject)cell).Descendants<LiteralInline>().Select(literal => literal.Content.ToString()));

    private static string VisibleNativeText(string html)
    {
        var body = Regex.Match(html, "<body\\b[^>]*>(.*)</body>", RegexOptions.Singleline).Groups[1].Value;
        body = Regex.Replace(body, "<(?:script|style)\\b[^>]*>.*?</(?:script|style)>", "",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return WebUtility.HtmlDecode(Regex.Replace(body, "<[^>]+>", " "));
    }

    private sealed record Exported(DocumentGraph Graph, string Markdown, string Html);

    private const string PictureRun = """
        <w:r><w:drawing><wp:inline><wp:extent cx="914400" cy="457200"/>
        <wp:docPr id="1" name="CellPicture" descr="CELL-IMAGE"/>
        <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
        <pic:pic><pic:nvPicPr><pic:cNvPr id="1" name="CellPicture"/><pic:cNvPicPr/></pic:nvPicPr>
        <pic:blipFill><a:blip r:embed="rImage"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
        <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="457200"/></a:xfrm>
        <a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic>
        </a:graphicData></a:graphic></wp:inline></w:drawing></w:r>
        """;
}
