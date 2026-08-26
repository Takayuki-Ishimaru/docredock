using System.IO.Compression;
using System.Xml.Linq;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

public sealed class MarkdownRendererTests
{
    private const string MermaidMarkdown = """
        # Request flow

        ```mermaid
        flowchart TD
            A[Client] --> B[API]
        ```
        """;

    [Theory]
    [InlineData(RenderFormat.Docx, ".docx", "word/document.xml")]
    [InlineData(RenderFormat.Pptx, ".pptx", "ppt/slides/slide1.xml")]
    [InlineData(RenderFormat.Xlsx, ".xlsx", "xl/worksheets/sheet1.xml")]
    public async Task Renders_minimal_valid_ooxml_package(RenderFormat format, string extension, string requiredEntry)
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Root, "rendered" + extension);
        var result = await new MarkdownRenderer().RenderAsync("# Title\n\nHello world\n", format, output);

        Assert.Equal("F3", result.FidelityLevel);
        Assert.False(result.IsRestore);
        Assert.True(File.Exists(output));
        using var archive = ZipFile.OpenRead(output);
        Assert.Contains(archive.Entries, entry => entry.FullName == "[Content_Types].xml");
        Assert.Contains(archive.Entries, entry => entry.FullName == requiredEntry);
    }

    [Fact]
    public async Task Renders_pdf_with_header_and_text()
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Root, "rendered.pdf");
        var result = await new MarkdownRenderer().RenderAsync("# Title\n\nHello world\n", RenderFormat.Pdf, output);

        Assert.Equal(RenderFormat.Pdf, result.Format);
        var bytes = await File.ReadAllBytesAsync(output);
        Assert.StartsWith("%PDF-1.4", System.Text.Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.True(bytes.Length > 100_000);
        Assert.Contains("/Subtype /Type0", System.Text.Encoding.Latin1.GetString(bytes));
    }

    [Fact]
    public async Task Pdf_render_embeds_japanese_text_in_a_unicode_cid_font()
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Root, "rendered.pdf");

        var result = await new MarkdownRenderer().RenderAsync("# 日本語\n\n本文です。Hello world", RenderFormat.Pdf, output);

        Assert.Equal(RenderFormat.Pdf, result.Format);
        var bytes = await File.ReadAllBytesAsync(output);
        var latin = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.True(bytes.Length > 9_000_000);
        Assert.Contains("/CIDToGIDMap", latin);
        Assert.Contains("/ToUnicode", latin);
    }

    [Fact]
    public async Task DocxRenderCreatesRichRunsRealListsAndRealTables()
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Root, "structured.docx");

        await new MarkdownRenderer().RenderAsync("# 計画\n\n通常 **太字** <u>下線</u>\n\n- 項目1\n- 項目2\n\n| 項目 | 金額 |\n| --- | --- |\n| 売上 | 120 |", RenderFormat.Docx, output);

        using var archive = ZipFile.OpenRead(output);
        var xml = await ReadEntryAsync(archive, "word/document.xml");
        Assert.Contains("pStyle", xml);
        Assert.Contains("<w:b", xml);
        Assert.Contains("<w:u", xml);
        Assert.Contains("numPr", xml);
        Assert.Contains("<w:tbl", xml);
        Assert.NotNull(archive.GetEntry("word/styles.xml"));
        Assert.NotNull(archive.GetEntry("word/numbering.xml"));
    }

    [Fact]
    public async Task PptxRenderCreatesSeparateTitleAndBodyPlaceholderShapes()
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Root, "structured.pptx");

        await new MarkdownRenderer().RenderAsync("# 実行計画\n\n- 要点1\n- 要点2", RenderFormat.Pptx, output);

        using var archive = ZipFile.OpenRead(output);
        var xml = await ReadEntryAsync(archive, "ppt/slides/slide1.xml");
        Assert.Contains("name=\"Title\"", xml);
        Assert.Contains("type=\"title\"", xml);
        Assert.Contains("name=\"Body\"", xml);
        Assert.Contains("type=\"body\"", xml);
        Assert.Contains("実行計画", xml);
        Assert.Contains("要点2", xml);
    }

    [Fact]
    public async Task Mermaid_block_is_rendered_as_a_native_docx_image()
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Root, "mermaid.docx");
        var renderer = new MarkdownRenderer(new StubMermaidRenderer());

        var result = await renderer.RenderAsync(MermaidMarkdown, RenderFormat.Docx, output);

        Assert.Contains(result.Warnings, warning => warning.Contains("1 Mermaid diagram", StringComparison.Ordinal));
        using var archive = ZipFile.OpenRead(output);
        Assert.NotNull(archive.GetEntry("word/media/docredock-mermaid-1.png"));
        Assert.Contains("rIdDocRedockMermaid1", await ReadEntryAsync(archive, "word/_rels/document.xml.rels"));
        var document = await ReadEntryAsync(archive, "word/document.xml");
        Assert.Contains("<w:drawing", document);
        Assert.Contains("Mermaid diagram", document);
        Assert.DoesNotContain("flowchart TD", document);
    }

    [Fact]
    public async Task Mermaid_block_is_rendered_as_a_native_pptx_picture()
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Root, "mermaid.pptx");
        var renderer = new MarkdownRenderer(new StubMermaidRenderer());

        await renderer.RenderAsync(MermaidMarkdown, RenderFormat.Pptx, output);

        using var archive = ZipFile.OpenRead(output);
        Assert.NotNull(archive.GetEntry("ppt/media/docredock-mermaid-1.png"));
        Assert.Contains("rIdDocRedockMermaid1", await ReadEntryAsync(archive, "ppt/slides/_rels/slide1.xml.rels"));
        var slide = await ReadEntryAsync(archive, "ppt/slides/slide1.xml");
        Assert.Contains("<p:pic>", slide);
        Assert.Contains("Mermaid diagram", slide);
        Assert.DoesNotContain("flowchart TD", slide);
    }

    [Fact]
    public async Task Mermaid_block_is_embedded_as_a_pdf_image_xobject()
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Root, "mermaid.pdf");
        var renderer = new MarkdownRenderer(new StubMermaidRenderer());

        await renderer.RenderAsync(MermaidMarkdown, RenderFormat.Pdf, output);

        var pdf = System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(output));
        Assert.Contains("/Subtype /Image", pdf);
        Assert.Contains("/Im1 Do", pdf);
        Assert.DoesNotContain("flowchart TD", pdf);
    }

    [Fact]
    public async Task Mermaid_block_is_rendered_as_a_native_xlsx_drawing()
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Root, "mermaid.xlsx");
        var renderer = new MarkdownRenderer(new StubMermaidRenderer());
        const string markdown = """
            | Step | Owner |
            | --- | --- |
            | Validate | API |
            | Persist | Database |

            ```mermaid
            flowchart TD
                A[Client] --> B[API]
            ```
            """;

        await renderer.RenderAsync(markdown, RenderFormat.Xlsx, output);

        using var archive = ZipFile.OpenRead(output);
        Assert.NotNull(archive.GetEntry("xl/media/docredock-mermaid-1.png"));
        Assert.NotNull(archive.GetEntry("xl/drawings/drawing1.xml"));
        Assert.Contains("rIdDocRedockDrawing1", await ReadEntryAsync(archive, "xl/worksheets/_rels/sheet1.xml.rels"));
        Assert.Contains("rIdDocRedockMermaid1", await ReadEntryAsync(archive, "xl/drawings/_rels/drawing1.xml.rels"));
        var sheet = await ReadEntryAsync(archive, "xl/worksheets/sheet1.xml");
        Assert.Contains("<drawing", sheet);
        Assert.Contains("customHeight=\"1\"", sheet);
        Assert.Contains("r=\"A1\"", sheet);
        Assert.Contains("r=\"B3\"", sheet);
        var drawing = await ReadEntryAsync(archive, "xl/drawings/drawing1.xml");
        Assert.Contains("<xdr:oneCellAnchor>", drawing);
        Assert.Contains("<xdr:row>4</xdr:row>", drawing);
        Assert.Contains("Mermaid diagram", drawing);
        Assert.DoesNotContain("flowchart TD", sheet);
    }

    [Fact]
    public async Task Mermaid_cli_rejects_remote_references_before_starting_a_process()
    {
        var renderer = new MermaidCliRenderer();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => renderer.RenderPngAsync(
            "flowchart TD\nA[https://example.com] --> B",
            new MermaidRenderRequest("definitely-not-installed-mmdc")));

        Assert.Contains("cannot reference URLs", exception.Message);
    }

    [Fact]
    public async Task XlsxRenderMapsMarkdownTableToWorksheetCells()
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Root, "table.xlsx");

        await new MarkdownRenderer().RenderAsync("| 項目 | 金額 |\n| --- | --- |\n| 売上 | 120 |", RenderFormat.Xlsx, output);

        using var archive = ZipFile.OpenRead(output);
        var xml = await ReadEntryAsync(archive, "xl/worksheets/sheet1.xml");
        Assert.Contains("r=\"A1\"", xml);
        Assert.Contains("r=\"B2\"", xml);
        Assert.Contains("売上", xml);
        Assert.Contains("120", xml);
    }

    [Fact]
    public async Task DocRedock_projection_control_metadata_is_not_rendered_into_docx()
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Root, "rendered.docx");
        const string projection = """
            ---
            drmd_schema: 1.0
            document_id: doc_1
            source_format: docx
            roundtrip_store: source.drmd
            content_policy: visible
            preserve_drmd_comments: true
            ---
            <!--drmd:partition-begin id=part-0001 baseline_nodes=1-->
            <!--drmd:block id=n_1 kind=paragraph-->
            Customer content
            <!--drmd:partition-end id=part-0001 baseline_nodes=1-->
            <!--drmd:document-end id=doc_1 partitions=1-->
            """;

        await new MarkdownRenderer().RenderAsync(projection, RenderFormat.Docx, output);

        using var archive = ZipFile.OpenRead(output);
        using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
        var xml = await reader.ReadToEndAsync();
        Assert.Contains("Customer content", xml);
        Assert.DoesNotContain("drmd:", xml);
        Assert.DoesNotContain("drmd_schema", xml);
        Assert.DoesNotContain("document_id", xml);
    }

    [Fact]
    public async Task DocRedock_projection_renders_as_sanitized_responsive_html_preview()
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Root, "roundtrip-preview.html");
        const string projection = """
            ---
            drmd_schema: 1.0
            document_id: doc_1
            source_format: xlsx
            roundtrip_store: source.drmd
            content_policy: visible
            preserve_drmd_comments: true
            ---
            <!--drmd:partition-begin id=part-0001 baseline_nodes=4-->
            <!--drmd:block id=n_1 kind=heading-->
            # 月次レビュー

            <!--drmd:sheet-table range=A1:B2 source-columns=A,B source-rows=1,2 baseline_nodes=2 editability=cell-grid operations=replace-cell constraints=preserve-range,preserve-addresses,no-insert-delete,safe-formula-->
            | 月 | 達成率 |
            | --- | --- |
            | 8月 | 42.0% |

            <!--drmd:block id=n_3 kind=paragraph-->
            **判断:** 継続
            <!--drmd:partition-end id=part-0001 baseline_nodes=4-->
            <!--drmd:document-end id=doc_1 partitions=1-->
            """;

        var result = await new MarkdownRenderer().RenderAsync(projection, RenderFormat.Html, output);

        var html = await File.ReadAllTextAsync(output);
        Assert.Equal(RenderFormat.Html, result.Format);
        Assert.Contains("ROUNDTRIP PREVIEW", html, StringComparison.Ordinal);
        Assert.Contains("月次レビュー", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"table-scroll\">", html, StringComparison.Ordinal);
        Assert.Contains("42.0%", html, StringComparison.Ordinal);
        Assert.Contains("<strong>判断:</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("drmd_schema", html, StringComparison.Ordinal);
        Assert.DoesNotContain("drmd:block", html, StringComparison.Ordinal);
        Assert.Contains(result.Warnings, warning => warning.Contains("control metadata", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Html_preview_renders_known_readable_markup_and_hides_inference_comments()
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Root, "readable-preview.html");
        const string markdown = """
            # **変換品質**<br>**構造から測る**

            <!-- inferred: セル配置から文書情報セクションを推定 -->
            <details class="speaker-notes">
            <summary>スピーカーノート（クリックで展開）</summary>

            検証メモ

            </details>

            ---
            """;

        await new MarkdownRenderer().RenderAsync(markdown, RenderFormat.Html, output);

        var html = await File.ReadAllTextAsync(output);
        Assert.Contains("<title>変換品質 構造から測る</title>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>変換品質</strong><br><strong>構造から測る</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<details class=\"speaker-notes\">", html, StringComparison.Ordinal);
        Assert.Contains("<summary>スピーカーノート（クリックで展開）</summary>", html, StringComparison.Ordinal);
        Assert.Contains("<hr>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;details", html, StringComparison.Ordinal);
        Assert.DoesNotContain("inferred:", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Html_preview_preserves_ordered_nested_lists_breaks_tables_code_and_rebased_images()
    {
        using var fixture = new Fixture();
        var sourceDirectory = Path.Combine(fixture.Root, "source");
        var output = Path.Combine(fixture.Root, "preview", "readable.html");
        Directory.CreateDirectory(sourceDirectory);
        var markdown = $$"""
            # 見出し

            **太字** と *斜体*{{"  "}}
            次の行

            1. 最初
              - 入れ子
            2. 次

            | 項目 | 内容 |
            | --- | --- |
            | A | B |

            `inline`

            ```text
            code <safe>
            ```

            ![構成図](assets/diagram%20one.png)
            """;

        await new MarkdownRenderer().RenderAsync(markdown, RenderFormat.Html, output,
            new RenderOptions(SourceDirectory: sourceDirectory));

        var html = await File.ReadAllTextAsync(output);
        Assert.Contains("<h1>見出し</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>太字</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<em>斜体</em><br>", html, StringComparison.Ordinal);
        Assert.Contains("<ol>", html, StringComparison.Ordinal);
        Assert.Contains("<ul>", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"table-scroll\">", html, StringComparison.Ordinal);
        Assert.Contains("<pre><code data-language=\"text\">code &lt;safe&gt;</code></pre>", html, StringComparison.Ordinal);
        Assert.Contains("src=\"../source/assets/diagram%20one.png\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Html_preview_does_not_rebase_images_outside_the_source_directory()
    {
        using var fixture = new Fixture();
        var sourceDirectory = Path.Combine(fixture.Root, "source");
        var output = Path.Combine(fixture.Root, "preview", "readable.html");
        Directory.CreateDirectory(sourceDirectory);

        await new MarkdownRenderer().RenderAsync(
            "![escape](../../private-file.png)\n\n![encoded](..%2F..%2Fprivate-file.png)",
            RenderFormat.Html,
            output,
            new RenderOptions(SourceDirectory: sourceDirectory));

        var html = await File.ReadAllTextAsync(output);
        Assert.Equal(2, html.Split("src=\"about:blank\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("private-file.png", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Incomplete_docredock_projection_is_rejected_by_render()
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Root, "rendered.docx");
        const string projection = """
            ---
            drmd_schema: 1.0
            document_id: doc_1
            source_format: docx
            roundtrip_store: source.drmd
            ---
            <!--drmd:block id=n_1 kind=paragraph-->
            Incomplete
            """;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new MarkdownRenderer().RenderAsync(projection, RenderFormat.Docx, output));
    }

    [Fact]
    public void Generic_markdown_code_examples_with_docredock_markers_are_not_misclassified_or_removed()
    {
        const string generic = """
            # Parser example

            ```md
            <!--drmd:delete id=example-->
            ```
            """;

        Assert.False(DocRedockProjectionCleaner.IsDocRedockProjection(generic));
        Assert.Equal(generic, DocRedockProjectionCleaner.Clean(generic));
    }

    [Fact]
    public async Task Template_render_is_F2_and_preserves_unknown_package_parts()
    {
        using var fixture = new Fixture();
        var template = Path.Combine(fixture.Root, "template.docx");
        await new MarkdownRenderer().RenderAsync("Template", RenderFormat.Docx, template);
        using (var archive = ZipFile.Open(template, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(archive.CreateEntry("custom/company-theme.bin").Open()))
            writer.Write("preserve");
        var output = Path.Combine(fixture.Root, "rendered.docx");

        var result = await new MarkdownRenderer().RenderAsync("# New content", RenderFormat.Docx, output, new RenderOptions(TemplatePath: template));

        Assert.Equal("F2", result.FidelityLevel);
        using var rendered = ZipFile.OpenRead(output);
        Assert.NotNull(rendered.GetEntry("custom/company-theme.bin"));
        using var reader = new StreamReader(rendered.GetEntry("word/document.xml")!.Open());
        Assert.Contains("New content", reader.ReadToEnd());
    }

    [Theory]
    [InlineData(RenderFormat.Docx, ".docx")]
    [InlineData(RenderFormat.Pptx, ".pptx")]
    [InlineData(RenderFormat.Xlsx, ".xlsx")]
    public async Task Mermaid_template_render_merges_relationships_and_parts_without_collisions(RenderFormat format, string extension)
    {
        using var fixture = new Fixture();
        var renderer = new MarkdownRenderer(new StubMermaidRenderer());
        var template = Path.Combine(fixture.Root, "template" + extension);
        await renderer.RenderAsync(MermaidMarkdown, format, template);
        using (var archive = ZipFile.Open(template, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(archive.CreateEntry("custom/company-theme.bin").Open()))
            writer.Write("preserve");
        var output = Path.Combine(fixture.Root, "rendered" + extension);

        var result = await renderer.RenderAsync(
            MermaidMarkdown.Replace("# Request flow", "# Merged request", StringComparison.Ordinal),
            format,
            output,
            new RenderOptions(TemplatePath: template));

        Assert.Equal("F2", result.FidelityLevel);
        Assert.Contains(result.Warnings, warning => warning.Contains("merged generated content dependencies", StringComparison.Ordinal));
        using var rendered = ZipFile.OpenRead(output);
        Assert.NotNull(rendered.GetEntry("custom/company-theme.bin"));

        XNamespace relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace packageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
        switch (format)
        {
            case RenderFormat.Docx:
            {
                var document = XDocument.Parse(await ReadEntryAsync(rendered, "word/document.xml"));
                var imageId = (string?)document.Descendants().Attributes(relationships + "embed").Single();
                Assert.Equal("rIdDocRedockMermaid1_2", imageId);
                var rels = XDocument.Parse(await ReadEntryAsync(rendered, "word/_rels/document.xml.rels"));
                var image = rels.Descendants(packageRelationships + "Relationship").Single(element => (string?)element.Attribute("Id") == imageId);
                Assert.Equal("media/docredock-mermaid-1-2.png", (string?)image.Attribute("Target"));
                Assert.NotNull(rendered.GetEntry("word/media/docredock-mermaid-1.png"));
                Assert.NotNull(rendered.GetEntry("word/media/docredock-mermaid-1-2.png"));
                Assert.Contains("Merged request", document.ToString());
                break;
            }
            case RenderFormat.Pptx:
            {
                var slide = XDocument.Parse(await ReadEntryAsync(rendered, "ppt/slides/slide1.xml"));
                var imageId = (string?)slide.Descendants().Attributes(relationships + "embed").Single();
                Assert.Equal("rIdDocRedockMermaid1_2", imageId);
                var rels = XDocument.Parse(await ReadEntryAsync(rendered, "ppt/slides/_rels/slide1.xml.rels"));
                var image = rels.Descendants(packageRelationships + "Relationship").Single(element => (string?)element.Attribute("Id") == imageId);
                Assert.Equal("../media/docredock-mermaid-1-2.png", (string?)image.Attribute("Target"));
                Assert.NotNull(rendered.GetEntry("ppt/media/docredock-mermaid-1.png"));
                Assert.NotNull(rendered.GetEntry("ppt/media/docredock-mermaid-1-2.png"));
                Assert.Contains("Merged request", slide.ToString());
                break;
            }
            case RenderFormat.Xlsx:
            {
                XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                var sheet = XDocument.Parse(await ReadEntryAsync(rendered, "xl/worksheets/sheet1.xml"));
                var drawingId = (string?)sheet.Root?.Element(spreadsheet + "drawing")?.Attribute(relationships + "id");
                Assert.Equal("rIdDocRedockDrawing1_2", drawingId);
                var sheetRels = XDocument.Parse(await ReadEntryAsync(rendered, "xl/worksheets/_rels/sheet1.xml.rels"));
                var drawingRelationship = sheetRels.Descendants(packageRelationships + "Relationship").Single(element => (string?)element.Attribute("Id") == drawingId);
                Assert.Equal("../drawings/docredock-drawing1.xml", (string?)drawingRelationship.Attribute("Target"));
                Assert.NotNull(rendered.GetEntry("xl/drawings/drawing1.xml"));
                Assert.NotNull(rendered.GetEntry("xl/drawings/docredock-drawing1.xml"));

                var drawing = XDocument.Parse(await ReadEntryAsync(rendered, "xl/drawings/docredock-drawing1.xml"));
                var imageId = (string?)drawing.Descendants().Attributes(relationships + "embed").Single();
                var drawingRels = XDocument.Parse(await ReadEntryAsync(rendered, "xl/drawings/_rels/docredock-drawing1.xml.rels"));
                var image = drawingRels.Descendants(packageRelationships + "Relationship").Single(element => (string?)element.Attribute("Id") == imageId);
                Assert.Equal("../media/docredock-mermaid-1-2.png", (string?)image.Attribute("Target"));
                Assert.NotNull(rendered.GetEntry("xl/media/docredock-mermaid-1.png"));
                Assert.NotNull(rendered.GetEntry("xl/media/docredock-mermaid-1-2.png"));

                XNamespace contentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
                var types = XDocument.Parse(await ReadEntryAsync(rendered, "[Content_Types].xml"));
                Assert.Contains(types.Descendants(contentTypes + "Override"), element =>
                    (string?)element.Attribute("PartName") == "/xl/drawings/docredock-drawing1.xml");
                Assert.Contains("Merged request", sheet.ToString());
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    [Fact]
    public async Task Template_render_rejects_external_relationships()
    {
        using var fixture = new Fixture();
        var template = Path.Combine(fixture.Root, "template.docx");
        await new MarkdownRenderer().RenderAsync("Template", RenderFormat.Docx, template);
        using (var archive = ZipFile.Open(template, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(archive.CreateEntry("word/_rels/document.xml.rels").Open()))
            writer.Write("<Relationships><Relationship TargetMode=\"External\" Target=\"https://example.invalid\"/></Relationships>");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new MarkdownRenderer().RenderAsync("# New content", RenderFormat.Docx,
                Path.Combine(fixture.Root, "rendered.docx"), new RenderOptions(TemplatePath: template)));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "docredock-render-tests", Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return await reader.ReadToEndAsync();
    }

    private sealed class StubMermaidRenderer : IMermaidRenderer
    {
        // 1x1, 8-bit grayscale+alpha PNG. Keeping this fixture tiny makes the
        // package assertions independent of Chromium and the installed mmdc version.
        private static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        public Task<byte[]> RenderPngAsync(string source, MermaidRenderRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Png);
    }
}
