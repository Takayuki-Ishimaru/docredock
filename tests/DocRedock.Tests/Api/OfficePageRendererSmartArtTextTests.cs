using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using DocRedock.Api;
using DocRedock.Providers.Abstractions.Providers;
using DocRedock.Render;

namespace DocRedock.Tests.Api;

/// <summary>The SmartArt text the local Office engine must put into its PDF is checked label by label: a short label is not
/// satisfied by a longer word, a label split across lines is found, a PowerPoint label is looked for on its own slide's
/// page, and every missing label is reported.</summary>
public sealed class OfficePageRendererSmartArtTextTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-office-smartart-").FullName;
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private const string Dgm = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";

    [Fact]
    public async Task A_missing_short_label_is_reported_although_a_longer_word_on_the_page_contains_it()
    {
        var source = Docx("short.docx", "A", "Banana");
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => new PdfWritingRenderer(Pdf(["Apple", "Banana"])).RenderAsync(source));
        Assert.Contains("omitted SmartArt text", failure.Message);
        Assert.Matches("text: A;|text: A\\.", failure.Message);
        Assert.DoesNotContain("Banana", failure.Message);
    }

    [Fact]
    public async Task Short_labels_that_are_on_the_page_pass_and_every_missing_label_is_reported()
    {
        var present = await new PdfWritingRenderer(Pdf(["A", "1", "OK", "Banana"])).RenderAsync(Docx("present.docx", "A", "1", "OK", "Banana"));
        Assert.Single(present.Pages);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => new PdfWritingRenderer(Pdf(["Plan 10", "OKAY"])).RenderAsync(Docx("absent.docx", "1", "OK", "Plan 10")));
        Assert.Contains("1;", failure.Message);
        Assert.Contains("OK.", failure.Message);
        Assert.DoesNotContain("Plan 10", failure.Message);
    }

    [Fact]
    public async Task A_label_the_engine_split_across_two_lines_passes()
    {
        var rendered = await new PdfWritingRenderer(Pdf(["Quarterly", "Report", "Other text"])).RenderAsync(Docx("split.docx", "Quarterly Report"));
        Assert.Single(rendered.Pages);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PdfWritingRenderer(Pdf(["Report", "Quarterly"])).RenderAsync(Docx("order.docx", "Quarterly Report")));
    }

    [Fact]
    public async Task A_PowerPoint_label_is_looked_for_on_the_page_of_its_own_slide()
    {
        // The slide list puts slide2.xml first, so Beta is on page 1 and Alpha on page 2.
        var source = Pptx("slides.pptx", [("slide2.xml", "Beta"), ("slide1.xml", "Alpha")]);
        var rendered = await new PdfWritingRenderer(Pdf(["Beta"], ["Alpha"])).RenderAsync(source);
        Assert.Equal(1, rendered.Pages[0].PageNumber);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => new PdfWritingRenderer(Pdf(["Alpha"], ["Beta"])).RenderAsync(source));
        Assert.Contains("Beta", failure.Message);
        Assert.Contains("Alpha", failure.Message);
    }

    [Fact]
    public async Task A_PowerPoint_label_may_be_on_any_page_when_the_engine_made_a_different_number_of_pages()
    {
        var source = Pptx("pages.pptx", [("slide1.xml", "Alpha"), ("slide2.xml", "Beta")]);
        var rendered = await new PdfWritingRenderer(Pdf(["Beta"], ["Notes"], ["Alpha"])).RenderAsync(source);
        Assert.Equal(3, rendered.Pages.Count);
    }

    [Fact]
    public void Labels_are_matched_as_whole_words_for_Latin_and_digits_and_as_substrings_for_CJK()
    {
        static IReadOnlyList<string> Missing(string[] labels, string[] pages, int slides = 0) =>
            LibreOfficePageRenderer.MissingSmartArtText([.. labels.Select(label => new LibreOfficePageRenderer.SmartArtLabel(label))], [.. pages.Select(page => (page, (string?)null))], slides);
        Assert.Equal(["A"], Missing(["A"], ["Apple pie"]));
        Assert.Empty(Missing(["A"], ["Plan A, then B"]));
        Assert.Empty(Missing(["OK", "1"], ["ok 1"]));
        Assert.Equal(["OK"], Missing(["OK"], ["Okay"]));
        // NFKC: full-width letters and digits, the ligature, and case.
        Assert.Empty(Missing(["ＡＢＣ １２", "Office"], ["abc 12", "oﬃce"]));
        // Spacing, line breaks and punctuation between the words do not matter; their order does.
        Assert.Empty(Missing(["Step 1: Start"], ["Step\n1 -\nStart"]));
        Assert.Equal(["Step 1: Start"], Missing(["Step 1: Start"], ["Start Step 1"]));
        // CJK: a substring, whatever the engine put between the characters.
        Assert.Empty(Missing(["売上高"], ["今期の売上高は増加"]));
        Assert.Empty(Missing(["売上高"], ["今期の売\n上高は増加"]));
        Assert.Equal(["利益率"], Missing(["売上高", "利益率"], ["今期の売上高は増加"]));
        // A label without a letter or digit cannot be checked and is not reported.
        Assert.Empty(Missing(["-", "  "], ["x"]));
    }

    [Fact]
    public void A_slide_label_is_matched_on_its_own_page_only_when_there_is_one_page_per_slide()
    {
        static IReadOnlyList<string> Missing(int slide, string[] pages, int slides) =>
            LibreOfficePageRenderer.MissingSmartArtText([new("Beta", slide)], [.. pages.Select(page => (page, (string?)null))], slides);
        Assert.Empty(Missing(2, ["Alpha", "Beta"], 2));
        Assert.Equal(["Beta"], Missing(1, ["Alpha", "Beta"], 2));
        Assert.Empty(Missing(1, ["Alpha", "Beta", "Gamma"], 2));
        Assert.Empty(Missing(0, ["Alpha", "Beta"], 2));
    }

    private string Docx(string name, params string[] labels)
    {
        var path = Path.Combine(root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(archive, "[Content_Types].xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='xml' ContentType='application/xml'/><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
        Add(archive, "_rels/.rels", $"<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='r1' Type='{R}/officeDocument' Target='word/document.xml'/></Relationships>");
        Add(archive, "word/document.xml", $"<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:drawing><wp:inline xmlns:wp='http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing'><a:graphic xmlns:a='{A}'><a:graphicData uri='{Dgm}'><dgm:relIds xmlns:dgm='{Dgm}' xmlns:r='{R}' r:dm='rData'/></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p><w:sectPr><w:pgSz w:w='12240' w:h='15840'/></w:sectPr></w:body></w:document>");
        Add(archive, "word/_rels/document.xml.rels", $"<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rData' Type='{R}/diagramData' Target='diagrams/data1.xml'/></Relationships>");
        Add(archive, "word/diagrams/data1.xml", DataModel(labels));
        return path;
    }

    // The slides are listed in the given order; each is named by its part and has its own diagram.
    private string Pptx(string name, (string Part, string Label)[] slides)
    {
        var path = Path.Combine(root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(archive, "[Content_Types].xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='xml' ContentType='application/xml'/><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/></Types>");
        Add(archive, "_rels/.rels", $"<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='r1' Type='{R}/officeDocument' Target='ppt/presentation.xml'/></Relationships>");
        Add(archive, "ppt/presentation.xml", $"<p:presentation xmlns:p='{P}' xmlns:r='{R}'><p:sldIdLst>" +
            string.Concat(slides.Select((slide, index) => $"<p:sldId id='{256 + index}' r:id='rS{index}'/>")) + "</p:sldIdLst></p:presentation>");
        Add(archive, "ppt/_rels/presentation.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'>" +
            string.Concat(slides.Select((slide, index) => $"<Relationship Id='rS{index}' Type='{R}/slide' Target='slides/{slide.Part}'/>")) + "</Relationships>");
        foreach (var (part, label) in slides)
        {
            var id = Path.GetFileNameWithoutExtension(part);
            Add(archive, "ppt/slides/" + part, $"<p:sld xmlns:p='{P}' xmlns:a='{A}' xmlns:dgm='{Dgm}' xmlns:r='{R}'><p:cSld><p:spTree><p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id='1' name='Diagram'/></p:nvGraphicFramePr><p:xfrm><a:off x='0' y='0'/><a:ext cx='5000000' cy='3000000'/></p:xfrm><a:graphic><a:graphicData><dgm:relIds r:dm='rData'/></a:graphicData></a:graphic></p:graphicFrame></p:spTree></p:cSld></p:sld>");
            Add(archive, $"ppt/slides/_rels/{part}.rels", $"<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rData' Type='{R}/diagramData' Target='../diagrams/{id}-data.xml'/></Relationships>");
            Add(archive, $"ppt/diagrams/{id}-data.xml", DataModel(label));
        }
        return path;
    }

    private static string DataModel(params string[] labels) => $"<dgm:dataModel xmlns:dgm='{Dgm}' xmlns:a='{A}'><dgm:ptLst>" +
        string.Concat(labels.Select((label, index) => $"<dgm:pt modelId='{index + 1}' type='node'><dgm:t><a:p><a:r><a:t>{label}</a:t></a:r></a:p></dgm:t></dgm:pt>")) + "</dgm:ptLst></dgm:dataModel>";

    private static void Add(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    // One page per argument, each a list of text lines.
    private static byte[] Pdf(params string[][] pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", pages.Select((_, i) => $"{i + 4 + pages.Length} 0 R"))}] /Count {pages.Length} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        foreach (var lines in pages)
        {
            var stream = "BT /F1 12 Tf 72 720 Td " + string.Join(" 0 -24 Td ", lines.Select(line => $"({line}) Tj")) + " ET";
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream");
        }
        for (var i = 0; i < pages.Length; i++)
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {i + 4} 0 R >>");
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
            await File.WriteAllBytesAsync(Path.Combine(info.WorkingDirectory!, "input.pdf"), pdf, cancellationToken);
            return 0;
        }
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
