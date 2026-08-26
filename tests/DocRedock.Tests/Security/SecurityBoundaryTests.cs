using System.IO.Compression;
using System.Text;
using System.Xml;
using DocRedock.Formats.OpenXml.Xlsx;
using DocRedock.Markdown;
using DocRedock.Providers.Abstractions.Providers;

namespace DocRedock.Tests.Security;

public sealed class SecurityBoundaryTests
{
    [Fact]
    public void Xlsx_dtd_and_xxe_are_rejected_without_external_resolution()
    {
        var workbook = "<?xml version=\"1.0\"?><!DOCTYPE workbook [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"&xxe;\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";
        using var input = new MemoryStream(CreateZip(
            ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
            ("xl/workbook.xml", workbook),
            ("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\"/></Relationships>"),
            ("xl/worksheets/sheet1.xml", "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData/></worksheet>")));

        Assert.Throws<XmlException>(() => new XlsxAdapter().Extract(input));
    }

    [Fact]
    public async Task Container_gate_rejects_zip_traversal_and_compression_bomb()
    {
        await using var traversal = new RewindableInput(new MemoryStream(CreateZip(("../outside.xml", "x"))), leaveOpen: false);
        var traversalAssessment = ContainerSecurityGate.Assess(traversal);
        Assert.False(traversalAssessment.IsAllowed);
        Assert.Contains(traversalAssessment.Diagnostics, item => item.Code == "ZipPathTraversal");

        var repeated = new string('A', 100_000);
        await using var bomb = new RewindableInput(new MemoryStream(CreateZip(("payload.bin", repeated))), leaveOpen: false);
        var bombAssessment = ContainerSecurityGate.Assess(bomb, new ContainerSecurityLimits(MaxCompressionRatio: 2));
        Assert.False(bombAssessment.IsAllowed);
        Assert.Contains(bombAssessment.Diagnostics, item => item.Code == "ZipCompressionRatioExceeded");
    }

    [Fact]
    public void Random_markdown_marker_inputs_are_strictly_rejected_without_crashing()
    {
        var random = new Random(0x52544D44);
        var parser = new DocRedockMarkdownParser();
        for (var iteration = 0; iteration < 250; iteration++)
        {
            var chars = new char[random.Next(0, 512)];
            for (var index = 0; index < chars.Length; index++) chars[index] = (char)random.Next(0x20, 0xD7FF);
            var parsed = parser.Parse(new string(chars), new MarkdownParseOptions { Strict = true, RequireFrontMatter = true, RequireDocumentEnd = true });
            Assert.False(parsed.IsComplete);
        }
    }

    private static byte[] CreateZip(params (string Name, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        return output.ToArray();
    }
}
