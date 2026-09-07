using System.IO.Compression;
using System.Text;
using DocRedock.Api;
using DocRedock.Cli;
using DocRedock.Gui;
using DocRedock.Tests.Markdown;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DocRedock.Tests.Api;

/// <summary>
/// End-to-end regression coverage for D07 (literal source text that merely looks like Markdown --
/// "[LABEL](url)", "![ALT](path)", "[REF][id]", and a "[id]: url" reference-definition line --
/// must never become a live link/image/reference-definition). Unlike MarkdownLiteralSyntaxGateTests,
/// which serializes hand-built DocumentGraphs, these tests build minimal but REAL DOCX/PPTX/XLSX
/// packages (the same way the adapter tests do) and drive them through the real adapters via
/// DocumentService, GuiWorkflowService and CliApplication, then parse the resulting Markdown with
/// the same Markdig-backed MarkdownStructure helper the gate uses.
/// </summary>
public sealed class MarkdownLiteralSyntaxEndToEndTests
{
    private const string InlineLink = "[LABEL](https://example.com/x)";
    private const string InlineImageProbe = "![ALT](image.png)";
    private const string ReferenceLink = "[REF][id]";
    private const string ReferenceDefinition = "[id]: https://example.com/ref";
    private const string ShortLink = "[x](y)";
    private const string RealLinkLabel = "Real link";
    private const string RealLinkUrl = "https://example.com/real";

    // 1x1, 8-bit grayscale+alpha PNG, reused verbatim from
    // MarkdownRendererTests.StubMermaidRenderer.Png so the embedded image in these fixtures is a
    // real, valid image rather than arbitrary bytes.
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task Docx_readable_export_escapes_every_literal_probe_and_keeps_real_features_live()
    {
        var source = await CreateLiteralSyntaxDocxAsync();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "docx-literal.md");

        var exported = await new DocumentService().ExportReadableAsync(
            new ReadableDocumentExportOptions(source, output, EnableOcr: false));
        var markdown = await File.ReadAllTextAsync(exported.MarkdownPath);
        var document = MarkdownStructure.Parse(markdown);

        AssertOnlyRealLink(document, RealLinkUrl, RealLinkLabel);
        AssertOnlyRealImage(document, markdown);
        AssertNoReferenceDefinitionsSurvive(document, markdown);
        AssertRawEscaping(markdown);
        MarkdownStructure.AssertInOrder(MarkdownStructure.DisplayedText(document),
            ReferenceLink, InlineLink, InlineImageProbe, ReferenceDefinition, RealLinkLabel,
            "Bold text", "Italic text", "List item one", "List item two", ShortLink);

        // Positive path: real DOCX features around the literal probes must still work.
        var emphasis = ((MarkdownObject)document).Descendants<EmphasisInline>().ToArray();
        Assert.Contains(emphasis, item => item.DelimiterCount == 2 && PlainText(item.FirstChild) == "Bold text");
        Assert.Contains(emphasis, item => item.DelimiterCount == 1 && PlainText(item.FirstChild) == "Italic text");
        var list = ((MarkdownObject)document).Descendants<ListBlock>().Single();
        Assert.Equal(2, list.Count);
        var table = ((MarkdownObject)document).Descendants<Table>().Single();
        Assert.Contains(((MarkdownObject)table).Descendants<TableCell>(), cell => CellText(cell).Contains(ShortLink, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pptx_readable_export_escapes_every_literal_probe()
    {
        var source = CreateLiteralSyntaxPptxPackage();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "pptx-literal.md");

        var exported = await new DocumentService().ExportReadableAsync(
            new ReadableDocumentExportOptions(source, output, EnableOcr: false));
        var markdown = await File.ReadAllTextAsync(exported.MarkdownPath);
        var document = MarkdownStructure.Parse(markdown);

        // PptxAdapter has no a:hlinkClick support (verified against PptxAdapter.cs: zero matches
        // for "hlinkClick"), so no shape in this fixture carries a real hyperlink -- every link
        // Markdig could possibly find would have to come from one of the literal probes.
        Assert.Empty(MarkdownStructure.Links(document));
        Assert.Empty(MarkdownStructure.Images(document));
        AssertNoReferenceDefinitionsSurvive(document, markdown);
        AssertRawEscaping(markdown);
        MarkdownStructure.AssertInOrder(MarkdownStructure.DisplayedText(document),
            InlineLink, InlineImageProbe, ReferenceLink, ReferenceDefinition);
    }

    [Fact]
    public async Task Xlsx_readable_export_escapes_every_literal_probe()
    {
        var source = CreateLiteralSyntaxXlsxPackage();
        var output = Path.Combine(Path.GetDirectoryName(source)!, "xlsx-literal.md");

        var exported = await new DocumentService().ExportReadableAsync(
            new ReadableDocumentExportOptions(source, output, EnableOcr: false));
        var markdown = await File.ReadAllTextAsync(exported.MarkdownPath);
        var document = MarkdownStructure.Parse(markdown);

        // XlsxAdapter never reads a worksheet's <hyperlinks> element (verified against
        // XlsxAdapter.cs: its only "HYPERLINK" match is the dangerous-formula-name check), so a
        // real hyperlink cell is not possible here either.
        Assert.Empty(MarkdownStructure.Links(document));
        Assert.Empty(MarkdownStructure.Images(document));
        AssertNoReferenceDefinitionsSurvive(document, markdown);
        AssertRawEscaping(markdown);
        MarkdownStructure.AssertInOrder(MarkdownStructure.DisplayedText(document),
            ShortLink, InlineLink, InlineImageProbe, ReferenceDefinition);
    }

    [Fact]
    public async Task Gui_and_cli_readable_exports_of_the_same_docx_are_byte_identical()
    {
        var source = await CreateLiteralSyntaxDocxAsync();
        var root = Path.GetDirectoryName(source)!;
        var guiDirectory = Path.Combine(root, "gui-out");
        var cliDirectory = Path.Combine(root, "cli-out");
        Directory.CreateDirectory(cliDirectory);

        // Default visual-inference mode and default content policy on both sides -- neither call
        // below passes InferenceMode/ContentPolicy, so both fall back to the same Safe/"visible"
        // defaults declared on ReadableDocumentExportOptions.
        var guiExport = await new GuiWorkflowService().ExportAsync(source, guiDirectory, enableOcr: false, readable: true);

        // Same basename as the GUI's auto-derived name: both exports land in different
        // directories, but ReadableDocumentExportOptions/DocumentService derive the sibling
        // ".assets" folder name from the markdown file's OWN basename, so this is required for the
        // two Markdown files to be byte-identical rather than merely equivalent.
        var cliMarkdownPath = Path.Combine(cliDirectory, Path.GetFileNameWithoutExtension(source) + ".md");
        var cliResult = await new CliApplication(new StringWriter(), new StringWriter()).RunAsync(
            ["export", source, "--output", cliMarkdownPath, "--profile", "readable", "--ocr", "off", "--quiet"]);

        Assert.Equal((int)ExitCode.Success, cliResult);
        var guiBytes = await File.ReadAllBytesAsync(guiExport.MarkdownPath);
        var cliBytes = await File.ReadAllBytesAsync(cliMarkdownPath);
        Assert.Equal(guiBytes, cliBytes);

        var markdown = Encoding.UTF8.GetString(guiBytes);
        var document = MarkdownStructure.Parse(markdown);
        AssertOnlyRealLink(document, RealLinkUrl, RealLinkLabel);
        AssertOnlyRealImage(document, markdown);
        AssertNoReferenceDefinitionsSurvive(document, markdown);
        AssertRawEscaping(markdown);
    }

    private static void AssertOnlyRealLink(MarkdownDocument document, string url, string label)
    {
        var links = MarkdownStructure.Links(document);
        var real = Assert.Single(links);
        Assert.Equal(url, real.Url);
        Assert.Equal(label, PlainText(real.FirstChild));
    }

    private static void AssertOnlyRealImage(MarkdownDocument document, string markdown)
    {
        var images = MarkdownStructure.Images(document);
        var real = Assert.Single(images);
        Assert.Contains(".assets/", real.Url, StringComparison.Ordinal);
        Assert.Contains(InlineImageProbe, MarkdownStructure.DisplayedText(document), StringComparison.Ordinal);
    }

    private static void AssertNoReferenceDefinitionsSurvive(MarkdownDocument document, string markdown)
    {
        Assert.Empty(MarkdownStructure.ReferenceDefinitions(document));
        Assert.Contains(ReferenceDefinition, MarkdownStructure.DisplayedText(document), StringComparison.Ordinal);
    }

    private static void AssertRawEscaping(string markdown)
    {
        Assert.DoesNotContain(InlineLink, markdown, StringComparison.Ordinal);
        Assert.Contains(@"\[LABEL\](https://example.com/x)", markdown, StringComparison.Ordinal);
    }

    private static string PlainText(Inline? first)
    {
        var text = new StringBuilder();
        for (var inline = first; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal: text.Append(literal.Content.ToString()); break;
                case ContainerInline container: text.Append(PlainText(container.FirstChild)); break;
            }
        }
        return text.ToString();
    }

    private static string CellText(TableCell cell)
    {
        var text = new StringBuilder();
        foreach (var block in cell)
            if (block is LeafBlock { Inline: { } inline })
                text.Append(PlainText(inline.FirstChild));
        return text.ToString();
    }

    private static async Task<string> CreateLiteralSyntaxDocxAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-e2e-literal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "literal-syntax.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        WriteEntry(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        WriteEntry(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        WriteEntry(zip, "word/_rels/document.xml.rels",
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rIdLink\" Target=\"https://example.com/real\" Type=\"hyperlink\" />" +
            "<Relationship Id=\"rIdImg\" Target=\"media/image1.png\" Type=\"image\" />" +
            "</Relationships>");
        WriteEntry(zip, "word/document.xml", $"""
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><w:body>
            <w:p><w:pPr><w:pStyle w:val="Heading1" /></w:pPr><w:r><w:t>{ReferenceLink}</w:t></w:r></w:p>
            <w:p><w:r><w:t>{InlineLink}</w:t></w:r></w:p>
            <w:p><w:r><w:t>{InlineImageProbe}</w:t></w:r></w:p>
            <w:p><w:r><w:t>{ReferenceDefinition}</w:t></w:r></w:p>
            <w:p><w:hyperlink r:id="rIdLink"><w:r><w:t>{RealLinkLabel}</w:t></w:r></w:hyperlink></w:p>
            <w:p><w:r><w:rPr><w:b /></w:rPr><w:t>Bold text</w:t></w:r><w:r><w:rPr><w:i /></w:rPr><w:t xml:space="preserve"> Italic text</w:t></w:r></w:p>
            <w:p><w:pPr><w:numPr /></w:pPr><w:r><w:t>List item one</w:t></w:r></w:p>
            <w:p><w:pPr><w:numPr /></w:pPr><w:r><w:t>List item two</w:t></w:r></w:p>
            <w:tbl>
              <w:tr><w:tc><w:p><w:r><w:t>{ShortLink}</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>B1</w:t></w:r></w:p></w:tc></w:tr>
              <w:tr><w:tc><w:p><w:r><w:t>A2</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>B2</w:t></w:r></w:p></w:tc></w:tr>
            </w:tbl>
            <w:p><w:r><w:drawing><a:blip r:embed="rIdImg" /></w:drawing></w:r></w:p><w:sectPr />
            </w:body></w:document>
            """);
        var image = zip.CreateEntry("word/media/image1.png");
        await using (var imageStream = image.Open()) await imageStream.WriteAsync(TinyPng);
        return path;
    }

    private static string CreateLiteralSyntaxPptxPackage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-e2e-literal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "literal-syntax.pptx");
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
            ["ppt/presentation.xml"] = "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\" /></p:sldIdLst></p:presentation>",
            ["ppt/_rels/presentation.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"slide\" Target=\"slides/slide1.xml\" /></Relationships>",
            ["ppt/slides/slide1.xml"] =
                "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree>" +
                PptxShape(2, InlineLink) + PptxShape(3, InlineImageProbe) + PptxShape(4, ReferenceLink) + PptxShape(5, ReferenceDefinition) +
                "</p:spTree></p:cSld></p:sld>",
        };
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            foreach (var part in parts) WriteEntry(zip, part.Key, part.Value);
        return path;
    }

    private static string PptxShape(int id, string text) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Shape{id}\" /></p:nvSpPr><p:txBody><a:bodyPr /><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:txBody></p:sp>";

    private static string CreateLiteralSyntaxXlsxPackage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-e2e-literal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "literal-syntax.xlsx");
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
            ["xl/workbook.xml"] = "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\" /></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"worksheet\" Target=\"worksheets/sheet1.xml\" /></Relationships>",
            ["xl/worksheets/sheet1.xml"] =
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
                XlsxRow(1, ShortLink) + XlsxRow(2, InlineLink) + XlsxRow(3, InlineImageProbe) + XlsxRow(4, ReferenceDefinition) +
                "</sheetData></worksheet>",
        };
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            foreach (var part in parts) WriteEntry(zip, part.Key, part.Value);
        return path;
    }

    private static string XlsxRow(int row, string text) =>
        $"<row r=\"{row}\"><c r=\"A{row}\" t=\"inlineStr\"><is><t>{text}</t></is></c></row>";

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
