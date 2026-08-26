using System.IO.Compression;
using System.Text;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Providers.Abstractions.Providers;

namespace DocRedock.Tests.Api;

public sealed class ContentPolicyIntegrationTests : IDisposable
{
    private const string DocxSecret = "DOCREDOCK_SECRET_HIDDEN_TEXT";
    private const string XlsxHiddenRowSecret = "DOCREDOCK_SECRET_HIDDEN_ROW";
    private const string XlsxHiddenColumnSecret = "DOCREDOCK_SECRET_HIDDEN_COLUMN";
    private const string XlsxHiddenSheetSecret = "DOCREDOCK_SECRET_HIDDEN_SHEET";
    private const string XlsxVeryHiddenSheetSecret = "DOCREDOCK_SECRET_VERY_HIDDEN_SHEET";
    private const string PptxHiddenSlideSecret = "DOCREDOCK_SECRET_HIDDEN_SLIDE";
    private const string PptxNotesSecret = "DOCREDOCK_SECRET_HIDDEN_NOTE";
    private const string PptxHiddenImagePayload = "DOCREDOCK_SECRET_HIDDEN_IMAGE_BYTES";
    private const string DocxHiddenVmlImagePayload = "DOCREDOCK_SECRET_HIDDEN_VML_IMAGE_BYTES";
    private const string DocxHiddenTableImagePayload = "DOCREDOCK_SECRET_HIDDEN_TABLE_IMAGE_BYTES";
    private const string DocxHiddenHeaderImagePayload = "DOCREDOCK_SECRET_HIDDEN_HEADER_IMAGE_BYTES";
    private const string PptxHiddenImageOcr = "DOCREDOCK_SECRET_HIDDEN_IMAGE_OCR";

    private readonly string root = Path.Combine(Path.GetTempPath(), "docredock-content-policy-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Docx_vanished_text_obeys_readable_content_policy()
    {
        var source = Path.Combine(root, "hidden.docx");
        WritePackage(source, DocxParts());

        await AssertPoliciesAsync(
            source,
            [DocxSecret],
            ["DocxHiddenTextExcluded"]);
    }

    [Fact]
    public async Task Docx_hidden_image_assets_and_ocr_do_not_escape_safe_policies()
    {
        var source = Path.Combine(root, "docx-hidden-image.docx");
        WritePackage(source, DocxParts());
        var service = new DocumentService(new HiddenImageOcrEngine());

        var visible = await ExportAsync(service, source, "visible", enableOcr: true);
        var sanitized = await ExportAsync(service, source, "sanitized", enableOcr: true);
        var complete = await ExportAsync(service, source, "complete", enableOcr: true);

        Assert.DoesNotContain(PptxHiddenImageOcr, visible.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(PptxHiddenImageOcr, sanitized.Markdown, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(root, "docx-hidden-image-visible.assets")));
        Assert.False(Directory.Exists(Path.Combine(root, "docx-hidden-image-sanitized.assets")));
        Assert.Contains(PptxHiddenImageOcr, complete.Markdown, StringComparison.Ordinal);
        var completeAssets = Path.Combine(root, "docx-hidden-image-complete.assets");
        Assert.True(Directory.Exists(completeAssets));
        Assert.Contains(Directory.EnumerateFiles(completeAssets), path =>
            File.ReadAllText(path).Contains(PptxHiddenImagePayload, StringComparison.Ordinal));
        Assert.Contains(Directory.EnumerateFiles(completeAssets), path =>
            File.ReadAllText(path).Contains(DocxHiddenVmlImagePayload, StringComparison.Ordinal));
        Assert.Contains(Directory.EnumerateFiles(completeAssets), path =>
            File.ReadAllText(path).Contains(DocxHiddenTableImagePayload, StringComparison.Ordinal));
        Assert.Contains(Directory.EnumerateFiles(completeAssets), path =>
            File.ReadAllText(path).Contains(DocxHiddenHeaderImagePayload, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Xlsx_hidden_sheets_rows_and_columns_obey_readable_content_policy()
    {
        var source = Path.Combine(root, "hidden.xlsx");
        WritePackage(source, XlsxParts());

        await AssertPoliciesAsync(
            source,
            [XlsxHiddenRowSecret, XlsxHiddenColumnSecret, XlsxHiddenSheetSecret, XlsxVeryHiddenSheetSecret],
            ["XlsxHiddenSheetExcluded", "XlsxHiddenRowExcluded", "XlsxHiddenColumnExcluded"]);
    }

    [Fact]
    public async Task Pptx_hidden_slide_and_notes_obey_readable_content_policy()
    {
        var source = Path.Combine(root, "hidden.pptx");
        WritePackage(source, PptxParts());

        await AssertPoliciesAsync(
            source,
            [PptxHiddenSlideSecret, PptxNotesSecret],
            ["PptxHiddenSlideExcluded", "PptxNotesExcluded"]);
    }

    [Theory]
    [InlineData("visible")]
    [InlineData("complete")]
    [InlineData("sanitized")]
    public async Task Xlsx_pristine_roundtrip_is_valid_for_each_content_policy(string contentPolicy)
    {
        var source = Path.Combine(root, $"roundtrip-{contentPolicy}.xlsx");
        var markdown = Path.Combine(root, $"roundtrip-{contentPolicy}.md");
        var workspace = Path.Combine(root, $"roundtrip-{contentPolicy}.drmd");
        WritePackage(source, XlsxParts());
        var service = new DocumentService();

        await service.ExportAsync(new DocumentExportOptions(
            source, workspace, markdown, ContentPolicy: contentPolicy));
        var diff = await service.DiffAsync(workspace, markdown);

        Assert.True(diff.Edit.IsValid, string.Join(Environment.NewLine,
            diff.Edit.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
        Assert.Empty(diff.Edit.Diff.PatchSet.Operations);
    }

    [Fact]
    public async Task Pptx_hidden_image_assets_and_ocr_do_not_escape_visible_policy()
    {
        var source = Path.Combine(root, "hidden-image.pptx");
        WritePackage(source, PptxParts());
        var service = new DocumentService(new HiddenImageOcrEngine());

        var visible = await ExportAsync(service, source, "visible", enableOcr: true);
        var complete = await ExportAsync(service, source, "complete", enableOcr: true);

        Assert.DoesNotContain(PptxHiddenImageOcr, visible.Markdown, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(root, "hidden-image-visible.assets")));
        Assert.Contains(PptxHiddenImageOcr, complete.Markdown, StringComparison.Ordinal);
        var completeAssets = Path.Combine(root, "hidden-image-complete.assets");
        Assert.True(Directory.Exists(completeAssets));
        Assert.Contains(Directory.EnumerateFiles(completeAssets), path =>
            File.ReadAllText(path).Contains(PptxHiddenImagePayload, StringComparison.Ordinal));
    }

    private async Task AssertPoliciesAsync(
        string source,
        IReadOnlyList<string> secrets,
        IReadOnlyList<string> exclusionCodes)
    {
        Directory.CreateDirectory(root);
        var service = new DocumentService();

        var visible = await ExportAsync(service, source, "visible");
        var sanitized = await ExportAsync(service, source, "sanitized");
        var complete = await ExportAsync(service, source, "complete");

        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, visible.Markdown, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, sanitized.Markdown, StringComparison.Ordinal);
            Assert.Contains(secret, complete.Markdown, StringComparison.Ordinal);
        }

        foreach (var code in exclusionCodes)
            Assert.Contains(visible.Diagnostics, item => item.Code == code && item.Severity == DiagnosticSeverity.Information);
        Assert.Contains(complete.Diagnostics, item =>
            item.Code == "HiddenContentIncluded" && item.Severity == DiagnosticSeverity.Warning);
    }

    private async Task<(string Markdown, IReadOnlyList<Diagnostic> Diagnostics)> ExportAsync(
        DocumentService service,
        string source,
        string contentPolicy,
        bool enableOcr = false)
    {
        var markdown = Path.Combine(root, Path.GetFileNameWithoutExtension(source) + "-" + contentPolicy + ".md");
        var result = await service.ExportReadableAsync(new ReadableDocumentExportOptions(
            source,
            markdown,
            EnableOcr: enableOcr,
            ContentPolicy: contentPolicy));
        return (await File.ReadAllTextAsync(markdown), result.Diagnostics);
    }

    private static IReadOnlyDictionary<string, string> DocxParts() => new Dictionary<string, string>
    {
        ["[Content_Types].xml"] = """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="png" ContentType="image/png"/>
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
            </Types>
            """,
        ["_rels/.rels"] = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
            </Relationships>
            """,
        ["word/document.xml"] = $$"""
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                        xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
                        xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
                        xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                        xmlns:v="urn:schemas-microsoft-com:vml">
              <w:body>
                <w:p><w:r><w:t>Visible DOCX text</w:t></w:r><w:r><w:rPr><w:vanish/></w:rPr><w:t>{{DocxSecret}}</w:t></w:r></w:p>
                <w:p><w:r><w:rPr><w:vanish/></w:rPr><w:drawing><wp:inline><wp:docPr id="1" name="Hidden image"/>
                  <a:graphic><a:graphicData><a:blip r:embed="rIdImage"/></a:graphicData></a:graphic>
                </wp:inline></w:drawing></w:r></w:p>
                <w:p><w:r><w:rPr><w:vanish/></w:rPr><w:pict><v:shape><v:imagedata r:id="rIdVml"/></v:shape></w:pict></w:r></w:p>
                <w:tbl><w:tr><w:tc><w:p><w:r><w:rPr><w:vanish/></w:rPr><w:pict><v:shape><v:imagedata r:id="rIdTable"/></v:shape></w:pict></w:r></w:p></w:tc></w:tr></w:tbl>
              </w:body>
            </w:document>
            """,
        ["word/_rels/document.xml.rels"] = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rIdImage" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/hidden.png"/>
              <Relationship Id="rIdVml" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/hidden-vml.png"/>
              <Relationship Id="rIdTable" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/hidden-table.png"/>
              <Relationship Id="rIdHeaderPart" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/header" Target="header1.xml"/>
            </Relationships>
            """,
        ["word/header1.xml"] = """
            <w:hdr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                   xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
                   xmlns:v="urn:schemas-microsoft-com:vml">
              <w:p><w:r><w:rPr><w:vanish/></w:rPr><w:pict><v:shape><v:imagedata r:id="rIdHeader"/></v:shape></w:pict></w:r></w:p>
            </w:hdr>
            """,
        ["word/_rels/header1.xml.rels"] = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rIdHeader" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/hidden-header.png"/>
            </Relationships>
            """,
        ["word/media/hidden.png"] = PptxHiddenImagePayload,
        ["word/media/hidden-vml.png"] = DocxHiddenVmlImagePayload,
        ["word/media/hidden-table.png"] = DocxHiddenTableImagePayload,
        ["word/media/hidden-header.png"] = DocxHiddenHeaderImagePayload,
    };

    private static IReadOnlyDictionary<string, string> XlsxParts() => new Dictionary<string, string>
    {
        ["[Content_Types].xml"] = """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
            </Types>
            """,
        ["_rels/.rels"] = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """,
        ["xl/workbook.xml"] = """
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
                <sheet name="Visible" sheetId="1" r:id="rId1"/>
                <sheet name="Hidden" sheetId="2" state="hidden" r:id="rId2"/>
                <sheet name="VeryHidden" sheetId="3" state="veryHidden" r:id="rId3"/>
              </sheets>
            </workbook>
            """,
        ["xl/_rels/workbook.xml.rels"] = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="worksheet" Target="worksheets/sheet2.xml"/>
              <Relationship Id="rId3" Type="worksheet" Target="worksheets/sheet3.xml"/>
            </Relationships>
            """,
        ["xl/worksheets/sheet1.xml"] = $$"""
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <cols><col min="3" max="3" hidden="1"/></cols>
              <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>Visible XLSX text</t></is></c></row>
                <row r="2" hidden="1"><c r="A2" t="inlineStr"><is><t>{{XlsxHiddenRowSecret}}</t></is></c></row>
                <row r="3"><c r="C3" t="inlineStr"><is><t>{{XlsxHiddenColumnSecret}}</t></is></c></row>
              </sheetData>
            </worksheet>
            """,
        ["xl/worksheets/sheet2.xml"] = $$"""
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
              <row r="1"><c r="A1" t="inlineStr"><is><t>{{XlsxHiddenSheetSecret}}</t></is></c></row>
            </sheetData></worksheet>
            """,
        ["xl/worksheets/sheet3.xml"] = $$"""
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
              <row r="1"><c r="A1" t="inlineStr"><is><t>{{XlsxVeryHiddenSheetSecret}}</t></is></c></row>
            </sheetData></worksheet>
            """,
    };

    private static IReadOnlyDictionary<string, string> PptxParts() => new Dictionary<string, string>
    {
        ["[Content_Types].xml"] = """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
            </Types>
            """,
        ["_rels/.rels"] = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>
            </Relationships>
            """,
        ["ppt/presentation.xml"] = """
            <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                            xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:sldIdLst><p:sldId id="256" r:id="rId1"/><p:sldId id="257" r:id="rId2"/></p:sldIdLst>
            </p:presentation>
            """,
        ["ppt/_rels/presentation.xml.rels"] = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="slide" Target="slides/slide1.xml"/>
              <Relationship Id="rId2" Type="slide" Target="slides/slide2.xml"/>
            </Relationships>
            """,
        ["ppt/slides/slide1.xml"] = SlideXml("Visible slide text", hidden: false),
        ["ppt/slides/slide2.xml"] = SlideXml(PptxHiddenSlideSecret, hidden: true, imageRelationshipId: "rIdImage"),
        ["ppt/slides/_rels/slide1.xml.rels"] = $$"""
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rIdNotes" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesSlide" Target="../notesSlides/notesSlide1.xml"/>
            </Relationships>
            """,
        ["ppt/slides/_rels/slide2.xml.rels"] = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rIdImage" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="../media/hidden.png"/>
            </Relationships>
            """,
        ["ppt/media/hidden.png"] = PptxHiddenImagePayload,
        ["ppt/notesSlides/notesSlide1.xml"] = $$"""
            <p:notes xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                     xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <p:cSld><p:spTree><p:sp><p:nvSpPr><p:cNvPr id="3" name="Notes"/>
              <p:nvPr><p:ph type="body"/></p:nvPr></p:nvSpPr>
              <p:txBody><a:bodyPr/><a:p><a:r><a:t>{{PptxNotesSecret}}</a:t></a:r></a:p></p:txBody>
              </p:sp></p:spTree></p:cSld>
            </p:notes>
            """,
    };

    private static string SlideXml(string text, bool hidden, string? imageRelationshipId = null)
    {
        var visibility = hidden ? " show=\"0\"" : string.Empty;
        var image = imageRelationshipId is null ? string.Empty : $$"""
              <p:pic><p:nvPicPr><p:cNvPr id="3" name="Hidden image"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr>
              <p:blipFill><a:blip r:embed="{{imageRelationshipId}}"/></p:blipFill><p:spPr/></p:pic>
            """;
        return $$"""
            <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                   xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                   xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"{{visibility}}>
              <p:cSld><p:spTree><p:sp><p:nvSpPr><p:cNvPr id="2" name="Title"/>
              <p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
              <p:txBody><a:bodyPr/><a:p><a:r><a:t>{{text}}</a:t></a:r></a:p></p:txBody>
              </p:sp>{{image}}</p:spTree></p:cSld>
            </p:sld>
            """;
    }

    private static void WritePackage(string path, IReadOnlyDictionary<string, string> parts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = File.Create(path);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var part in parts.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var entry = archive.CreateEntry(part.Key, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(part.Value);
        }
    }

    private sealed class HiddenImageOcrEngine : IOcrEngine
    {
        public ProviderDescriptor Descriptor { get; } = new("test.hidden-image-ocr", new Version(1, 0), 1,
            new HashSet<string> { "ocr.text" }, "MIT", "built-in", true);

        public ValueTask<OcrAttemptResult> RecognizeAsync(
            OcrInput input,
            OcrOptions options,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OcrAttemptResult(
                OcrProcessingStatus.Completed,
                new OcrResult(PptxHiddenImageOcr,
                    [new OcrTextRegion(PptxHiddenImageOcr, new Geometry("image-pixels", 0, 0, 1, 1), 1)]),
                []));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
