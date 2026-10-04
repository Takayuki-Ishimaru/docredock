using System.IO.Compression;
using System.Text;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Docx;
using DocRedock.Markdown;

namespace DocRedock.Tests.Docx;

public sealed class DocxChartExportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cached_chart_data_is_readable_in_body_and_table(bool inTable)
    {
        var source = CreateDocument(inTable: inTable, repeated: true);
        try
        {
            var adapter = new DocxAdapter();
            var extraction = await adapter.ExtractAsync(source);
            var charts = extraction.Graph.Nodes.Where(n => n.Kind == NodeKind.Chart).ToArray();
            Assert.Equal(2, charts.Length);
            Assert.Equal(2, charts.Select(n => n.Id).Distinct().Count());
            Assert.All(charts, n => Assert.NotNull(n.ParentId));
            var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);
            Assert.Contains("**Quarterly totals**（棒グラフ）", markdown, StringComparison.Ordinal);
            Assert.Contains("| Q1 | 35 |", markdown, StringComparison.Ordinal);
            Assert.Contains("| Q2 | 27 |", markdown, StringComparison.Ordinal);
            var restored = Path.Combine(Path.GetDirectoryName(source)!, "restored.docx");
            Assert.True((await adapter.RestoreAsync(extraction, extraction.Graph, restored)).Succeeded);
            Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(restored));
            var package = await new DocumentService().ExportAiPackageAsync(new(
                new(source, Path.Combine(Path.GetDirectoryName(source)!, "unused.md"), EnableOcr: false),
                Path.Combine(Path.GetDirectoryName(source)!, "package")));
            Assert.Contains(package.Parts, part => part.Markdown.Contains("| Q1 | 35 |", StringComparison.Ordinal));
        }
        finally { Directory.Delete(Path.GetDirectoryName(source)!, true); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("external")]
    public async Task Unavailable_chart_is_reported_and_retained_as_a_placeholder(string kind)
    {
        var source = CreateDocument(kind: kind);
        try
        {
            var extraction = await new DocxAdapter().ExtractAsync(source);
            Assert.Contains(extraction.Diagnostics, d => d.Code == "DocxChartDataUnavailable");
            Assert.Contains("原本を確認してください", new ReadableMarkdownSerializer().Serialize(extraction.Graph), StringComparison.Ordinal);
        }
        finally { Directory.Delete(Path.GetDirectoryName(source)!, true); }
    }

    [Fact]
    public async Task Hidden_chart_cache_does_not_leak_into_visible_export()
    {
        var source = CreateDocument(hidden: true);
        try
        {
            var extraction = await new DocxAdapter().ExtractAsync(source);
            Assert.Equal(ContentLayer.Hidden, Assert.Single(extraction.Graph.Nodes, n => n.Kind == NodeKind.Chart).Layer);
            Assert.DoesNotContain("Quarterly totals", new ReadableMarkdownSerializer().Serialize(extraction.Graph), StringComparison.Ordinal);
        }
        finally { Directory.Delete(Path.GetDirectoryName(source)!, true); }
    }

    private static string CreateDocument(bool inTable = false, bool repeated = false, string kind = "cached", bool hidden = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), "docredock-charts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "charts.docx");
        var paragraph = "<w:p><w:r>" + (hidden ? "<w:rPr><w:vanish/></w:rPr>" : "") +
            "<w:drawing><a:graphic><a:graphicData><c:chart r:id=\"rIdChart\"/></a:graphicData></a:graphic></w:drawing></w:r></w:p>";
        var body = repeated ? paragraph + paragraph : paragraph;
        if (inTable) body = "<w:tbl><w:tblGrid><w:gridCol w:w=\"2000\"/></w:tblGrid><w:tr><w:tc>" + body + "</w:tc></w:tr></w:tbl>";
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>",
            ["_rels/.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>",
            ["word/document.xml"] = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"><w:body>" + body + "</w:body></w:document>",
            ["word/_rels/document.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdChart\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/chart\" Target=\"" + (kind == "external" ? "https://example.invalid/never-fetch.xml\" TargetMode=\"External" : "charts/chart1.xml") + "\"/></Relationships>",
        };
        if (kind is not ("missing" or "external")) parts["word/charts/chart1.xml"] = kind == "malformed" ? "<broken" :
            "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><c:chart><c:title><a:t>Quarterly totals</a:t></c:title><c:plotArea><c:barChart><c:ser><c:tx><c:v>Revenue</c:v></c:tx><c:cat><c:strCache><c:pt idx=\"0\"><c:v>Q1</c:v></c:pt><c:pt idx=\"1\"><c:v>Q2</c:v></c:pt></c:strCache></c:cat><c:val><c:numCache><c:pt idx=\"0\"><c:v>35</c:v></c:pt><c:pt idx=\"1\"><c:v>27</c:v></c:pt></c:numCache></c:val></c:ser></c:barChart></c:plotArea></c:chart></c:chartSpace>";
        using var zip = ZipFile.Open(source, ZipArchiveMode.Create);
        foreach (var (name, content) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        return source;
    }
}
