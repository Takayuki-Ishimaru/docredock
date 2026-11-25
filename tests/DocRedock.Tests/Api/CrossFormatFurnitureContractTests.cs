using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Api;

namespace DocRedock.Tests.Api;

/// <summary>Anonymous real sources: confidentiality is source content even when it is page furniture.</summary>
public sealed class CrossFormatFurnitureContractTests : IDisposable
{
    private const string Header = "CONFIDENTIAL-HEADER";
    private const string Footer = "CONFIDENTIAL-FOOTER";
    private readonly string root = Directory.CreateTempSubdirectory("docredock-furniture-contract-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);

    public void Dispose() => Directory.Delete(root, true);

    [Theory(Timeout = 30000)]
    [InlineData("docx", "visible")]
    [InlineData("docx", "complete")]
    [InlineData("docx", "sanitized")]
    [InlineData("pptx", "visible")]
    [InlineData("pptx", "complete")]
    [InlineData("pptx", "sanitized")]
    [InlineData("pdf", "visible")]
    [InlineData("pdf", "complete")]
    [InlineData("pdf", "sanitized")]
    public async Task Explicit_source_confidentiality_is_visible_in_both_readable_formats_and_removed_only_by_policy(
        string extension, string policy)
    {
        var source = Path.Combine(root, "source." + extension);
        if (extension == "pdf") await File.WriteAllBytesAsync(source, Pdf());
        else Package(source, extension == "docx" ? Docx() : Pptx());

        var markdownPath = Path.Combine(root, extension + "-" + policy + ".md");
        var htmlPath = Path.Combine(root, extension + "-" + policy + ".html");
        await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, markdownPath,
            ContentPolicy: policy, IncludePdfFallbackImages: false, Language: "en"));
        await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, htmlPath,
            ContentPolicy: policy, IncludePdfFallbackImages: false, Language: "en",
            OutputFormat: ReadableOutputFormat.Html));
        var markdown = await File.ReadAllTextAsync(markdownPath);
        var html = await File.ReadAllTextAsync(htmlPath);

        Assert.Contains("VISIBLE-BODY", markdown, StringComparison.Ordinal);
        Assert.Contains("VISIBLE-BODY", html, StringComparison.Ordinal);
        if (policy == "sanitized")
        {
            Assert.DoesNotContain(Footer, markdown, StringComparison.Ordinal);
            Assert.DoesNotContain(Footer, html, StringComparison.Ordinal);
            // PowerPoint has no slide-header placeholder: its manually drawn top banner is body text.
            if (extension != "pptx")
            {
                Assert.DoesNotContain(Header, markdown, StringComparison.Ordinal);
                Assert.DoesNotContain(Header, html, StringComparison.Ordinal);
            }
            return;
        }

        Assert.Contains(Header, markdown, StringComparison.Ordinal);
        Assert.Contains(Header, html, StringComparison.Ordinal);
        Assert.Contains(Footer, markdown, StringComparison.Ordinal);
        Assert.Contains(Footer, html, StringComparison.Ordinal);
        if (extension == "docx")
        {
            // These are cached source field results, not page numbers recalculated for the reflowed HTML.
            Assert.Contains("Page 7 of 9", markdown, StringComparison.Ordinal);
            Assert.Contains("Page 7 of 9", WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        }
        if (extension == "pdf")
        {
            // Aggregating a running footer may abbreviate identical middle forms, but not its range.
            foreach (var page in new[] { 1, 3 })
            {
                Assert.Contains($"CONFIDENTIAL-FOOTER - {page} of 3", markdown, StringComparison.Ordinal);
                Assert.Contains($"CONFIDENTIAL-FOOTER - {page} of 3", html, StringComparison.Ordinal);
                Assert.Single(Regex.Matches(markdown, $"CONFIDENTIAL-FOOTER - {page} of 3"));
            }
        }

        // A reference appendix is acceptable. Requiring a click to reveal the only confidentiality
        // text is not: closed details are also commonly absent from a browser's default printed output.
        // This deliberately permits either an ordinary visible block or initially-open details.
        foreach (Match details in Regex.Matches(html,
            "<details\\b(?<attributes>[^>]*)>(?<body>.*?)</details>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (!details.Groups["body"].Value.Contains(Header, StringComparison.Ordinal) &&
                !details.Groups["body"].Value.Contains(Footer, StringComparison.Ordinal)) continue;
            Assert.Matches("(?i)(?:^|\\s)open(?:\\s|=|$)", details.Groups["attributes"].Value);
        }
    }

    [Theory(Timeout = 30000)]
    [InlineData("visible")]
    [InlineData("complete")]
    [InlineData("sanitized")]
    public async Task Pdf_page_number_only_footers_retain_saved_source_values_once(string policy)
    {
        var source = Path.Combine(root, "source-page-numbers.pdf");
        await File.WriteAllBytesAsync(source, Pdf(pageNumbersOnly: true));
        foreach (var format in new[] { ReadableOutputFormat.Markdown, ReadableOutputFormat.Html })
        {
            var output = Path.Combine(root, "page-numbers-" + policy + (format == ReadableOutputFormat.Html ? ".html" : ".md"));
            await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, output,
                ContentPolicy: policy, IncludePdfFallbackImages: false, Language: "en", OutputFormat: format));
            var text = WebUtility.HtmlDecode(await File.ReadAllTextAsync(output));
            Assert.Contains("VISIBLE-BODY", text, StringComparison.Ordinal);
            foreach (var page in new[] { 1, 3 })
                if (policy == "sanitized") Assert.DoesNotContain($"Page {page} of 3", text, StringComparison.Ordinal);
                else Assert.Contains($"Page {page} of 3", text, StringComparison.Ordinal);
        }
    }

    private const string RelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private static string Relationships(params (string Id, string Type, string Target)[] items) =>
        $"<Relationships xmlns=\"{RelationshipNamespace}\">" +
        string.Concat(items.Select(item =>
            $"<Relationship Id=\"{item.Id}\" Type=\"{OfficeRelationships}/{item.Type}\" Target=\"{item.Target}\"/>")) +
        "</Relationships>";

    private static Dictionary<string, string> BaseParts(string mainPart, string contentType) => new()
    {
        ["[Content_Types].xml"] = """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
            <Default Extension="xml" ContentType="application/xml"/>
            <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
            """ + $"<Override PartName=\"/{mainPart}\" ContentType=\"{contentType}\"/></Types>",
        ["_rels/.rels"] = Relationships(("rDocument", "officeDocument", mainPart))
    };

    private static Dictionary<string, string> Docx()
    {
        var parts = BaseParts("word/document.xml",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");
        parts["word/document.xml"] = $"""
            <w:document xmlns:w="{W}" xmlns:r="{OfficeRelationships}"><w:body>
            <w:p><w:r><w:t>VISIBLE-BODY docx alpha beta gamma.</w:t></w:r></w:p>
            <w:sectPr><w:headerReference w:type="default" r:id="rHeader"/>
            <w:footerReference w:type="default" r:id="rFooter"/>
            <w:pgSz w:w="11906" w:h="16838"/>
            <w:pgMar w:top="1440" w:bottom="1440" w:left="1440" w:right="1440" w:header="720" w:footer="720"/>
            </w:sectPr></w:body></w:document>
            """;
        parts["word/_rels/document.xml.rels"] = Relationships(
            ("rHeader", "header", "header1.xml"), ("rFooter", "footer", "footer1.xml"));
        parts["word/header1.xml"] = $"<w:hdr xmlns:w=\"{W}\"><w:p><w:r><w:t>{Header}</w:t></w:r></w:p></w:hdr>";
        parts["word/footer1.xml"] = $"""
            <w:ftr xmlns:w="{W}"><w:p>
            <w:r><w:t xml:space="preserve">CONFIDENTIAL-FOOTER Page </w:t></w:r>
            <w:fldSimple w:instr=" PAGE "><w:r><w:t>7</w:t></w:r></w:fldSimple>
            <w:r><w:t xml:space="preserve"> of </w:t></w:r>
            <w:r><w:fldChar w:fldCharType="begin"/></w:r>
            <w:r><w:instrText xml:space="preserve"> NUMPAGES </w:instrText></w:r>
            <w:r><w:fldChar w:fldCharType="separate"/></w:r>
            <w:r><w:t>9</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r>
            </w:p></w:ftr>
            """;
        return parts;
    }

    private static Dictionary<string, string> Pptx()
    {
        var parts = BaseParts("ppt/presentation.xml",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml");
        parts["ppt/presentation.xml"] = $"""
            <p:presentation xmlns:p="{P}" xmlns:r="{OfficeRelationships}">
            <p:sldIdLst><p:sldId id="256" r:id="rSlide"/></p:sldIdLst>
            <p:sldSz cx="12192000" cy="6858000"/></p:presentation>
            """;
        parts["ppt/_rels/presentation.xml.rels"] = Relationships(("rSlide", "slide", "slides/slide1.xml"));
        parts["ppt/slides/slide1.xml"] = $"""
            <p:sld xmlns:p="{P}" xmlns:a="{A}" xmlns:r="{OfficeRelationships}">
            <p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>
            """ + Shape(2, "Header", null, Header, 100000) +
            Shape(3, "Body", "body", "VISIBLE-BODY pptx alpha beta gamma.", 2500000) +
            Shape(4, "Footer", "ftr", Footer, 6200000) +
            Shape(5, "Slide number", "sldNum", "3", 6600000, field: true) +
            "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>";
        return parts;
    }

    private static string Shape(int id, string name, string? role, string text, int y, bool field = false)
    {
        var placeholder = role is null ? "" : $"<p:ph type=\"{role}\"/>";
        var run = field
            ? $"<a:fld id=\"{{11111111-1111-1111-1111-111111111111}}\" type=\"slidenum\"><a:t>{text}</a:t></a:fld>"
            : $"<a:r><a:t>{text}</a:t></a:r>";
        return $"""
            <p:sp><p:nvSpPr><p:cNvPr id="{id}" name="{name}"/><p:cNvSpPr/><p:nvPr>{placeholder}</p:nvPr></p:nvSpPr>
            <p:spPr><a:xfrm><a:off x="640000" y="{y}"/><a:ext cx="10000000" cy="500000"/></a:xfrm></p:spPr>
            <p:txBody><a:bodyPr/><a:lstStyle/><a:p>{run}</a:p></p:txBody></p:sp>
            """;
    }

    private static void Package(string path, IReadOnlyDictionary<string, string> parts)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in parts)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    private static byte[] Pdf(bool pageNumbersOnly = false)
    {
        static string Text(int y, string value, int size = 10) =>
            $"BT /F1 {size} Tf 1 0 0 1 48 {y} Tm ({value}) Tj ET\n";
        var objects = new SortedDictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R >>",
            [2] = "<< /Type /Pages /Kids [4 0 R 6 0 R 8 0 R] /Count 3 >>",
            [3] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        for (var index = 0; index < 3; index++)
        {
            var page = index + 1;
            var content = Text(812, Header, 9) +
                Text(500, $"VISIBLE-BODY pdf page {page} alpha beta gamma delta epsilon zeta.") +
                Text(480, "This is an anonymous semantic footer verification document.") +
                Text(460, "A third long body line sets out the review conditions.") +
                Text(28, pageNumbersOnly ? $"Page {page} of 3" : $"{Footer} - {page} of 3", 9);
            objects[4 + index * 2] =
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents " +
                (5 + index * 2) + " 0 R >>";
            objects[5 + index * 2] = $"<< /Length {content.Length} >>\nstream\n{content}endstream";
        }
        var output = new StringBuilder("%PDF-1.4\n");
        var offsets = new Dictionary<int, int>();
        foreach (var (id, content) in objects)
        {
            offsets[id] = output.Length;
            output.Append($"{id} 0 obj\n{content}\nendobj\n");
        }
        var xref = output.Length;
        output.Append("xref\n0 10\n0000000000 65535 f \n");
        for (var id = 1; id <= 9; id++) output.Append(offsets[id].ToString("D10")).Append(" 00000 n \n");
        output.Append($"trailer\n<< /Size 10 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(output.ToString());
    }
}
