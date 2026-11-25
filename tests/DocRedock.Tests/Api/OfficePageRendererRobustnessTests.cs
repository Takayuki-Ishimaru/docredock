using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using DocRedock.Api;
using DocRedock.Formats.OpenXml.Xlsx;
using DocRedock.Formats.Pdf;
using DocRedock.Providers.Abstractions.Providers;
using DocRedock.Render;

namespace DocRedock.Tests.Api;

/// <summary>Regressions for the local Office page renderer where a fake engine rarely looks: the real process
/// runner (time limit, process tree, pipes), engine output that exceeds a limit, SmartArt outside PowerPoint,
/// and limits that drop drawing objects.</summary>
public sealed class OfficePageRendererRobustnessTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-office-robust-").FullName;
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private const string Dgm = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string DiagramData = $"""<dgm:dataModel xmlns:dgm="{Dgm}" xmlns:a="{A}"><dgm:ptLst><dgm:pt modelId="1" type="node"><dgm:t><a:p><a:r><a:t>EXPECTED_DIAGRAM_LABEL</a:t></a:r></a:p></dgm:t></dgm:pt></dgm:ptLst></dgm:dataModel>""";

    [Fact]
    public async Task Auto_layout_keeps_the_structured_document_when_the_engine_pdf_exceeds_the_page_limit()
    {
        var renderer = new PdfWritingRenderer(BuildPdf(129, "Hello"));
        var service = new DocumentService(null, null, false, renderer);
        var result = await service.ExportReadableAsync(new(Docx("long.docx", ""), Path.Combine(root, "long.html"),
            OutputFormat: ReadableOutputFormat.Html, Language: "en", OfficeLayout: OfficeRenderingMode.Auto));
        var failed = Assert.Single(result.Diagnostics, d => d.Code == "OfficeSourcePagesFailed");
        Assert.Contains("128", failed.Message);
        var html = File.ReadAllText(result.MarkdownPath);
        Assert.Contains("OFFICE_BODY_TEXT", html);
        Assert.DoesNotContain("data-office-page=", html);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    public async Task Engine_cannot_silently_omit_SmartArt_text_in_Word_or_Excel(string kind)
    {
        var source = kind == "docx"
            ? Docx("diagram.docx", $"""<w:p><w:r><w:drawing><wp:inline xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"><a:graphic xmlns:a="{A}"><a:graphicData uri="{Dgm}"><dgm:relIds xmlns:dgm="{Dgm}" xmlns:r="{R}" r:dm="rData"/></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>""",
                ("word/_rels/document.xml.rels", $"""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rData" Type="{R}/diagramData" Target="diagrams/data1.xml"/></Relationships>"""),
                ("word/diagrams/data1.xml", DiagramData))
            : Xlsx("diagram.xlsx", $"""<xdr:twoCellAnchor><xdr:from><xdr:col>0</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>0</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:to><xdr:col>4</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>8</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to><xdr:graphicFrame><xdr:nvGraphicFramePr><xdr:cNvPr id="2" name="Diagram"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr><xdr:xfrm><a:off x="0" y="0"/><a:ext cx="100" cy="100"/></xdr:xfrm><a:graphic><a:graphicData uri="{Dgm}"><dgm:relIds xmlns:dgm="{Dgm}" r:dm="rData"/></a:graphicData></a:graphic></xdr:graphicFrame><xdr:clientData/></xdr:twoCellAnchor>""",
                ("xl/drawings/_rels/drawing1.xml.rels", $"""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rData" Type="{R}/diagramData" Target="../diagrams/data1.xml"/></Relationships>"""),
                ("xl/diagrams/data1.xml", DiagramData));

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => new PdfWritingRenderer(BuildPdf(1, "Hello")).RenderAsync(source));
        Assert.Contains("omitted SmartArt text", failure.Message);
        Assert.Contains("EXPECTED_DIAGRAM_LABEL", failure.Message);
        // The same package renders when the engine output does contain the label.
        var rendered = await new PdfWritingRenderer(BuildPdf(1, "EXPECTED_DIAGRAM_LABEL")).RenderAsync(source);
        Assert.Single(rendered.Pages);
    }

    [Fact]
    public async Task A_sheet_selection_never_receives_whole_workbook_page_pixels()
    {
        var renderer = new PdfWritingRenderer(BuildPdf(1, "Hello"));
        var service = new DocumentService(null, null, false, renderer);
        var options = new ReadableDocumentExportOptions(Xlsx("selected.xlsx", ""), Path.Combine(root, "selected.html"),
            Sheets: ["Sheet1"], OutputFormat: ReadableOutputFormat.Html, Language: "en", OfficeLayout: OfficeRenderingMode.Source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportReadableAsync(options));
        Assert.Null(renderer.Input);
        Assert.False(File.Exists(options.MarkdownPath));
    }

    [Fact]
    public async Task Real_process_time_limit_stops_the_whole_process_tree_and_removes_the_private_directory()
    {
        var marker = Path.Combine(root, "cwd.txt");
        var script = Script("slow", OperatingSystem.IsWindows()
            ? $"@echo off\r\necho %CD%> \"{marker}\"\r\nping -n 60 127.0.0.1\r\n"
            : $"#!/bin/sh\npwd > '{marker}'\nsleep 60 &\nwait\n");
        var renderer = new LibreOfficePageRenderer(script, new FakeRasterizer(), TimeSpan.FromSeconds(3));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => renderer.RenderAsync(Docx("slow.docx", "")));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "The time limit did not stop the engine: " + watch.Elapsed);
        var privateRoot = File.ReadAllText(marker).Trim();
        Assert.Contains("docredock-office-pages-", privateRoot);
        // A surviving child would keep the directory (its working directory) alive on Windows.
        Assert.False(Directory.Exists(privateRoot));
    }

    [Fact]
    public async Task Real_process_with_large_stdout_and_stderr_completes_without_blocking_on_its_pipes()
    {
        var pdf = Path.Combine(root, "engine.pdf");
        File.WriteAllBytes(pdf, BuildPdf(2, "Hello"));
        // Each stream carries more than a pipe buffer holds, one after the other: an engine blocks unless both are read.
        var noise = Path.Combine(root, "noise.txt");
        File.WriteAllText(noise, string.Concat(Enumerable.Repeat(new string('x', 99) + "\n", 4000)));
        var script = Script("noisy", OperatingSystem.IsWindows()
            ? $"@echo off\r\ntype \"{noise}\" 1>&2\r\ntype \"{noise}\"\r\ntype \"{noise}\" 1>&2\r\ncopy /Y \"{pdf}\" input.pdf > nul\r\nexit /b 0\r\n"
            : $"#!/bin/sh\ncat '{noise}' 1>&2\ncat '{noise}'\ncat '{noise}' 1>&2\ncp '{pdf}' input.pdf\n");
        var renderer = new LibreOfficePageRenderer(script, new FakeRasterizer(), TimeSpan.FromSeconds(60));
        var result = await renderer.RenderAsync(Docx("noisy.docx", ""));
        Assert.Equal(new[] { 1, 2 }, result.Pages.Select(p => p.PageNumber).ToArray());
    }

    [Fact]
    public async Task Private_copy_keeps_a_run_that_holds_only_a_space()
    {
        const string p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        var source = Path.Combine(root, "space.pptx");
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
            Add(archive, "ppt/slides/slide1.xml", $"""<p:sld xmlns:p="{p}" xmlns:a="{A}"><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>Hello</a:t></a:r><a:r><a:rPr b="1"/><a:t> </a:t></a:r><a:r><a:t>World</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>""");
        var renderer = new PdfWritingRenderer(BuildPdf(1, "Hello"));
        await renderer.RenderAsync(source);
        using var copy = new ZipArchive(new MemoryStream(renderer.Input!), ZipArchiveMode.Read);
        using var reader = new StreamReader(copy.GetEntry("ppt/slides/slide1.xml")!.Open());
        Assert.Contains("<a:t> </a:t>", reader.ReadToEnd());
    }

    [Fact]
    public void Worksheet_drawing_objects_beyond_the_object_limit_are_reported()
    {
        var anchors = new StringBuilder();
        for (var i = 0; i < 16390; i++)
            anchors.Append("<xdr:absoluteAnchor><xdr:pos x=\"0\" y=\"").Append(i * 1000).Append("\"/><xdr:ext cx=\"9525\" cy=\"9525\"/><xdr:sp><xdr:nvSpPr><xdr:cNvPr id=\"")
                .Append(i + 2).Append("\" name=\"S\"/><xdr:cNvSpPr/></xdr:nvSpPr><xdr:spPr><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></xdr:spPr></xdr:sp><xdr:clientData/></xdr:absoluteAnchor>");
        using var stream = File.OpenRead(Xlsx("many.xlsx", anchors.ToString()));
        var extraction = new XlsxAdapter().Extract(stream);
        Assert.True(extraction.Warnings.Any(warning => warning.Contains("16384", StringComparison.Ordinal) &&
            warning.Contains("Sheet1", StringComparison.Ordinal)), string.Join(" || ", extraction.Warnings.Select(w => w[..Math.Min(300, w.Length)]).Take(8)));
    }

    private string Script(string name, string content)
    {
        var path = Path.Combine(root, name + (OperatingSystem.IsWindows() ? ".cmd" : ".sh"));
        File.WriteAllText(path, content, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private string Docx(string name, string bodyXml, params (string Name, string Content)[] parts)
    {
        var path = Path.Combine(root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(archive, "[Content_Types].xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='xml' ContentType='application/xml'/><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
        Add(archive, "_rels/.rels", $"<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='r1' Type='{R}/officeDocument' Target='word/document.xml'/></Relationships>");
        Add(archive, "word/document.xml", "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>OFFICE_BODY_TEXT</w:t></w:r></w:p>" +
            bodyXml + "<w:sectPr><w:pgSz w:w='12240' w:h='15840'/></w:sectPr></w:body></w:document>");
        foreach (var part in parts) Add(archive, part.Name, part.Content);
        return path;
    }

    private string Xlsx(string name, string anchorsXml, params (string Name, string Content)[] parts)
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var path = Path.Combine(root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(archive, "[Content_Types].xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Default Extension='xml' ContentType='application/xml'/><Override PartName='/xl/workbook.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml'/><Override PartName='/xl/worksheets/sheet1.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'/><Override PartName='/xl/drawings/drawing1.xml' ContentType='application/vnd.openxmlformats-officedocument.drawing+xml'/></Types>");
        Add(archive, "_rels/.rels", $"<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Type='{R}/officeDocument' Target='xl/workbook.xml'/></Relationships>");
        Add(archive, "xl/workbook.xml", $"<workbook xmlns='{main}' xmlns:r='{R}'><sheets><sheet name='Sheet1' sheetId='1' r:id='rId1'/></sheets></workbook>");
        Add(archive, "xl/_rels/workbook.xml.rels", $"<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Type='{R}/worksheet' Target='worksheets/sheet1.xml'/></Relationships>");
        Add(archive, "xl/worksheets/sheet1.xml", $"<worksheet xmlns='{main}' xmlns:r='{R}'><sheetData><row r='1'><c r='A1'><v>1</v></c></row></sheetData><drawing r:id='rId1'/></worksheet>");
        Add(archive, "xl/worksheets/_rels/sheet1.xml.rels", $"<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Type='{R}/drawing' Target='../drawings/drawing1.xml'/></Relationships>");
        Add(archive, "xl/drawings/drawing1.xml", $"<xdr:wsDr xmlns:xdr='http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing' xmlns:a='{A}' xmlns:r='{R}'>{anchorsXml}</xdr:wsDr>");
        foreach (var part in parts) Add(archive, part.Name, part.Content);
        return path;
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    // A structurally complete PDF with the given number of 612x792 pages, each showing the same text.
    private static byte[] BuildPdf(int pageCount, string text)
    {
        var stream = $"BT /F1 12 Tf 72 720 Td ({text}) Tj ET";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{i + 5} 0 R"))}] /Count {pageCount} >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        for (var i = 0; i < pageCount; i++)
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 3 0 R >>");
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(builder.Length);
            builder.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = builder.Length;
        builder.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) builder.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private sealed class PdfWritingRenderer(byte[] pdf) : LibreOfficePageRenderer("ignored", new FakeRasterizer())
    {
        protected override async Task<int> RunProcessAsync(ProcessStartInfo info, CancellationToken cancellationToken)
        {
            Input = await File.ReadAllBytesAsync(info.ArgumentList[^1], cancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(info.WorkingDirectory, "input.pdf"), pdf, cancellationToken); return 0;
        }
        public byte[]? Input { get; private set; }
    }

    private sealed class FakeRasterizer : IPdfRasterizer
    {
        public ProviderDescriptor Descriptor { get; } = new("fake-rasterizer", new Version(1, 0), 1,
            new HashSet<string>(StringComparer.Ordinal) { "pdf-rasterize" }, "MIT", string.Empty, false);

        public ValueTask<IReadOnlyList<RasterizedPdfPage>> RasterizeAsync(string pdfPath,
            IReadOnlyList<int> pageNumbers, PdfRasterizationOptions options, CancellationToken cancellationToken = default)
        {
            var png = PngRasterImage.Encode(8, 8, new byte[8 * 8 * 3]);
            return ValueTask.FromResult<IReadOnlyList<RasterizedPdfPage>>(
                pageNumbers.Select(n => new RasterizedPdfPage(n, "image/png", png, 8, 8)).ToList());
        }
    }
}
