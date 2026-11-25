using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Api;
using DocRedock.Providers.Abstractions.Providers;
using DocRedock.Render;

namespace DocRedock.Tests.Api;

/// <summary>Lifecycle regressions for the local LibreOffice page renderer: private profile isolation,
/// package sanitization of the private copy, physical page geometry, cleanup, and cancellation.</summary>
public sealed class OfficePageRendererLifecycleTests : IDisposable
{
    private readonly List<string> ownedDirectories=[];
    public void Dispose(){foreach(var path in ownedDirectories)Directory.Delete(path,true);}
    private const string SpreadsheetMl = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelPrefix = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    [Fact]
    public async Task RenderAsync_UsesPrivateProfile_SanitizesPrivateCopy_KeepsOriginalAndCleansUp()
    {
        var workspace = CreateWorkspace();
        var source = Path.Combine(workspace, "fixture.xlsx");
        WriteSpreadsheetFixture(source);
        var originalSha = Sha256(source);
        using var renderer = new ControllableRenderer("/usr/bin/false", new FakeRasterizer());

        var result = await renderer.RenderAsync(source);

        var run = Assert.Single(renderer.Runs);
        Assert.True(run.Info.CreateNoWindow);
        Assert.False(run.Info.UseShellExecute);
        Assert.Contains("--headless", run.Info.ArgumentList);
        Assert.Contains("pdf:calc_pdf_Export", run.Info.ArgumentList);
        Assert.Equal(Path.Combine(run.Root, "input.xlsx"), run.Info.ArgumentList[^1]);

        // A fresh private profile, isolated from the user's LibreOffice installation.
        var profile = run.ProfileDirectory;
        Assert.StartsWith(run.Root + Path.DirectorySeparatorChar, profile + Path.DirectorySeparatorChar);
        Assert.Contains("profile", profile);
        var registry = run.RegistryXml ?? throw new InvalidOperationException("Private profile registry was not created.");
        Assert.Contains("MacroSecurityLevel", registry);
        Assert.Contains("<value>3</value>", registry);

        // The original file is never rewritten.
        Assert.Equal(originalSha, Sha256(source));

        // The private copy keeps saved values, drops recalculable formulas, macros, and
        // external non-hyperlink relationships, and keeps external hyperlinks.
        using var copy = ZipFile.OpenRead(run.CapturedInputZip!);
        var sheet = ReadEntry(copy, "xl/worksheets/sheet1.xml");
        Assert.Contains("<v>3</v>", sheet);
        Assert.DoesNotContain("<f>", sheet);
        Assert.DoesNotContain("SUM(A1:A2)", sheet);
        Assert.Null(copy.GetEntry("xl/vbaProject.bin"));
        var workbookRels = ReadEntry(copy, "xl/_rels/workbook.xml.rels");
        Assert.DoesNotContain("evil-printer", workbookRels);
        Assert.Contains("https://example.com/report", workbookRels);
        Assert.Contains("/worksheet\"", workbookRels);
        Assert.NotNull(copy.GetEntry("[Content_Types].xml"));

        // Physical PDF MediaBox drives the page geometry; the rasterizer ran at the fixed preview DPI.
        var page = Assert.Single(result.Pages);
        Assert.Equal("LibreOffice", result.Engine);
        Assert.Equal(1, page.PageNumber);
        Assert.Equal(612, page.WidthPoints);
        Assert.Equal(792, page.HeightPoints);
        Assert.Equal("image/png", page.MediaType);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, page.Content.Span[..8].ToArray());
        var raster = Assert.Single(renderer.FakeRasterizer.Calls);
        Assert.Equal(144, raster.Options.Dpi);
        Assert.Equal(new[] { 1 }, raster.PageNumbers.ToArray());

        // The private working root is deleted after a successful render.
        Assert.False(Directory.Exists(run.Root));
    }

    [Fact]
    public async Task RenderAsync_ProcessExitOne_ThrowsInvalidDataAndCleansUp()
    {
        var workspace = CreateWorkspace();
        var source = Path.Combine(workspace, "fixture.xlsx");
        WriteSpreadsheetFixture(source);
        using var renderer = new ControllableRenderer("/usr/bin/false", new FakeRasterizer(),
            behavior: run => { run.WritePdf = false; return Task.FromResult(1); });

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => renderer.RenderAsync(source));

        Assert.Contains("exit 1", failure.Message);
        Assert.False(Directory.Exists(Assert.Single(renderer.Runs).Root));
        Assert.Empty(renderer.FakeRasterizer.Calls);
    }

    [Fact]
    public async Task RenderAsync_CancellationDuringProcess_ThrowsOperationCanceledAndCleansUp()
    {
        var workspace = CreateWorkspace();
        var source = Path.Combine(workspace, "fixture.xlsx");
        WriteSpreadsheetFixture(source);
        using var cts = new CancellationTokenSource();
        using var renderer = new ControllableRenderer("/usr/bin/false", new FakeRasterizer(),
            behavior: async run =>
            {
                await cts.CancelAsync();
                await Task.Delay(Timeout.Infinite, run.Token);
                return 0;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.RenderAsync(source, cts.Token));

        Assert.False(Directory.Exists(Assert.Single(renderer.Runs).Root));
    }

    [Fact]
    public async Task RenderAsync_PreCancelledToken_StartsNoProcess()
    {
        var workspace = CreateWorkspace();
        var source = Path.Combine(workspace, "fixture.xlsx");
        WriteSpreadsheetFixture(source);
        using var renderer = new ControllableRenderer("/usr/bin/false", new FakeRasterizer());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.RenderAsync(source, cts.Token));

        Assert.Empty(renderer.Runs);
        Assert.Equal(0, renderer.ProcessStarts);
    }

    [Fact]
    public async Task Native_engine_cannot_silently_omit_uncached_SmartArt_text()
    {
        var source=Path.Combine(CreateWorkspace(),"missing-diagram.pptx");
        using(var zip=ZipFile.Open(source,ZipArchiveMode.Create))
        {
            AddEntry(zip,"ppt/slides/slide1.xml","""
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:dgm="http://schemas.openxmlformats.org/drawingml/2006/diagram" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><p:cSld><p:spTree><p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id="1" name="Diagram"/></p:nvGraphicFramePr><p:xfrm><a:off x="0" y="0"/><a:ext cx="5000000" cy="3000000"/></p:xfrm><a:graphic><a:graphicData><dgm:relIds r:dm="rData"/></a:graphicData></a:graphic></p:graphicFrame></p:spTree></p:cSld></p:sld>
                """);
            AddEntry(zip,"ppt/slides/_rels/slide1.xml.rels","""
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rData" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/diagramData" Target="../diagrams/data1.xml"/></Relationships>
                """);
            AddEntry(zip,"ppt/diagrams/data1.xml","""
                <dgm:dataModel xmlns:dgm="http://schemas.openxmlformats.org/drawingml/2006/diagram" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><dgm:ptLst><dgm:pt modelId="1" type="node"><dgm:t><a:p><a:r><a:t>EXPECTED_DIAGRAM_LABEL</a:t></a:r></a:p></dgm:t></dgm:pt></dgm:ptLst></dgm:dataModel>
                """);
        }
        var failure=await Assert.ThrowsAsync<InvalidDataException>(()=>new OmittedDiagramRenderer().RenderAsync(source));
        Assert.Contains("omitted SmartArt text",failure.Message);
        Assert.Contains("EXPECTED_DIAGRAM_LABEL",failure.Message);
    }

    private sealed class OmittedDiagramRenderer():LibreOfficePageRenderer("ignored",new FakeRasterizer())
    {
        protected override async Task<int> RunProcessAsync(ProcessStartInfo info,CancellationToken cancellationToken)
        {await File.WriteAllBytesAsync(Path.Combine(info.WorkingDirectory,"input.pdf"),BuildMinimalPdf(),cancellationToken);return 0;}
    }

    [Fact]
    public async Task RenderAsync_ConcurrentCalls_UseUniqueProfilesWithoutCrossContamination()
    {
        var workspace = CreateWorkspace();
        var sources = Enumerable.Range(1, 4).Select(i =>
        {
            var path = Path.Combine(workspace, $"fixture{i}.xlsx");
            WriteSpreadsheetFixture(path, marker: i);
            return path;
        }).ToArray();
        var hashes = sources.Select(Sha256).ToArray();
        using var renderer = new ControllableRenderer("/usr/bin/false", new FakeRasterizer());

        var results = await Task.WhenAll(sources.Select(s => renderer.RenderAsync(s)));

        Assert.All(results, r => Assert.Equal("LibreOffice", r.Engine));
        Assert.Equal(4, renderer.Runs.Count);
        var profiles = renderer.Runs.Select(r => r.ProfileDirectory).ToList();
        Assert.Equal(4, profiles.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, renderer.Runs.Select(r => r.Root).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new[]{1,2,3,4},renderer.Runs.Select(r=>r.Marker).Order().ToArray());
        // Each run saw exactly its own package: the per-file marker survived in its private copy.
        foreach (var run in renderer.Runs)
        {
            using var zip = ZipFile.OpenRead(run.CapturedInputZip!);
            Assert.Contains($"<v>{run.Marker}</v>", ReadEntry(zip, "xl/worksheets/sheet1.xml"));
        }
        Assert.All(renderer.Runs, run => Assert.False(Directory.Exists(run.Root)));
        for (var i = 0; i < sources.Length; i++)
            Assert.Equal(hashes[i], Sha256(sources[i]));
    }

    private sealed record RasterCall(string PdfPath, IReadOnlyList<int> PageNumbers, PdfRasterizationOptions Options);

    private sealed class FakeRasterizer : IPdfRasterizer
    {
        public List<RasterCall> Calls { get; } = [];
        public ProviderDescriptor Descriptor { get; } = new("fake-rasterizer", new Version(1, 0), 1,
            new HashSet<string>(StringComparer.Ordinal) { "pdf-rasterize" }, "MIT", string.Empty, false);

        public ValueTask<IReadOnlyList<RasterizedPdfPage>> RasterizeAsync(string pdfPath,
            IReadOnlyList<int> pageNumbers, PdfRasterizationOptions options, CancellationToken cancellationToken = default)
        {
            lock (Calls) Calls.Add(new RasterCall(pdfPath, pageNumbers, options));
            var png = PngRasterImage.Encode(8, 8, new byte[8 * 8 * 3]);
            var pages = pageNumbers.Select(n => new RasterizedPdfPage(n, "image/png", png, 8, 8)).ToList();
            return ValueTask.FromResult<IReadOnlyList<RasterizedPdfPage>>(pages);
        }
    }

    private sealed class ProcessRun
    {
        public required ProcessStartInfo Info { get; init; }
        public required string Root { get; init; }
        public required CancellationToken Token { get; init; }
        public int Marker { get; init; }
        public bool WritePdf { get; set; } = true;
        public string ProfileDirectory =>
            new Uri(Info.ArgumentList.First(a => a.StartsWith("-env:UserInstallation=", StringComparison.Ordinal))
                ["-env:UserInstallation=".Length..]).LocalPath.TrimEnd('/');
        public string? CapturedInputZip { get; set; }
        public string? RegistryXml { get; set; }
    }

    private sealed class ControllableRenderer(string executable, FakeRasterizer rasterizer,
        Func<ProcessRun, Task<int>>? behavior = null) : LibreOfficePageRenderer(executable, rasterizer),IDisposable
    {
        private readonly List<string> captures=[];
        public void Dispose(){foreach(var file in captures)File.Delete(file);}
        public FakeRasterizer FakeRasterizer => rasterizer;
        public List<ProcessRun> Runs { get; } = [];
        public int ProcessStarts { get; private set; }

        protected override async Task<int> RunProcessAsync(ProcessStartInfo info, CancellationToken cancellationToken)
        {
            ProcessStarts++;
            var root = info.WorkingDirectory!;
            var input = info.ArgumentList[^1];
            var bytes = await File.ReadAllBytesAsync(input, cancellationToken);
            var captured = Path.Combine(Path.GetTempPath(),"docredock-lifecycle-capture-"+Guid.NewGuid().ToString("N")+".xlsx");
            lock(captures)captures.Add(captured);
            await File.WriteAllBytesAsync(captured, bytes, cancellationToken);
            var run = new ProcessRun { Info = info, Root = root, Token = cancellationToken, Marker = ReadMarker(bytes) };
            run.CapturedInputZip = captured;
            var registryPath = Path.Combine(run.ProfileDirectory, "user", "registrymodifications.xcu");
            run.RegistryXml = File.Exists(registryPath) ? File.ReadAllText(registryPath) : null;
            lock (Runs) Runs.Add(run);
            var exit = behavior is null ? 0 : await behavior(run);
            if (run.WritePdf) await File.WriteAllBytesAsync(Path.Combine(root, "input.pdf"), BuildMinimalPdf(), cancellationToken);
            return exit;
        }
    }

    // The fixture's A1 cached value identifies which source a private copy came from.
    private static int ReadMarker(byte[] packageBytes)
    {
        using var archive = new ZipArchive(new MemoryStream(packageBytes), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        return int.Parse(Regex.Match(reader.ReadToEnd(), @"<c r=""A1"">\s*<v>(\d+)</v>").Groups[1].Value);
    }

    private string CreateWorkspace()
    {var path=Directory.CreateTempSubdirectory("docredock-lifecycle-").FullName;ownedDirectories.Add(path);return path;}

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"Missing entry {name}.");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static void WriteSpreadsheetFixture(string path, int marker = 3)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddEntry(archive, "[Content_Types].xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""");
        AddEntry(archive, "_rels/.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
        AddEntry(archive, "xl/workbook.xml",
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="{SpreadsheetMl}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet{marker}" sheetId="1" r:id="rId1"/></sheets></workbook>""");
        AddEntry(archive, "xl/_rels/workbook.xml.rels",
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{RelPrefix}/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="{RelPrefix}/printersSettings" Target="file:///tmp/evil-printer.bin" TargetMode="External"/><Relationship Id="rId3" Type="{RelPrefix}/hyperlink" Target="https://example.com/report" TargetMode="External"/></Relationships>""");
        AddEntry(archive, "xl/worksheets/sheet1.xml",
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="{SpreadsheetMl}"><sheetData><row r="1"><c r="A1"><v>{marker}</v></c><c r="B1"><f>SUM(A1:A2)</f><v>{marker}</v></c></row></sheetData></worksheet>""");
        AddEntry(archive, "xl/vbaProject.bin", "BIN");
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    // A structurally complete one-page PDF (612x792 MediaBox) with real xref offsets; the
    // production parser must accept it without stubbing.
    private static byte[] BuildMinimalPdf()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            null!,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        const string stream = "BT /F1 24 Tf 72 720 Td (Hello) Tj ET";
        objects[3] = $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream";

        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Count + 1];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = Encoding.ASCII.GetByteCount(builder.ToString());
            builder.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++)
            builder.Append($"{offsets[i]:D10} 00000 n \n");
        builder.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
