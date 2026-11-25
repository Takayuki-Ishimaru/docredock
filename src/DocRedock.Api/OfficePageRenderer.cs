using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using DocRedock.Formats.Pdf;
using DocRedock.Providers.Abstractions.Providers;

namespace DocRedock.Api;

/// <summary>A local engine's fixed source page; dimensions are physical PDF points.</summary>
public sealed record OfficeRenderedPage(int PageNumber, double WidthPoints, double HeightPoints,
    string MediaType, ReadOnlyMemory<byte> Content)
{
    public string? Text {get;init;}
}
public sealed record OfficePageRenderResult(string Engine, IReadOnlyList<OfficeRenderedPage> Pages);
public interface IOfficePageRenderer
{
    Task<OfficePageRenderResult> RenderAsync(string sourcePath, CancellationToken cancellationToken = default);
}

/// <summary>Runs an installed local LibreOffice in a fresh, private profile; never installs or downloads software.</summary>
public partial class LibreOfficePageRenderer(string executable, IPdfRasterizer rasterizer, TimeSpan? timeout = null) : IOfficePageRenderer
{
    private static readonly SemaphoreSlim Slots = new(2);
    private const long MaxBytes = 128L * 1024 * 1024;
    private const int MaxPages = 128;

    public async Task<OfficePageRenderResult> RenderAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = new FileInfo(Path.GetFullPath(sourcePath));
        var extension = source.Extension.ToLowerInvariant() switch { ".docm"=>".docx",".pptm"=>".pptx",".xlsm"=>".xlsx",var value=>value };
        if (extension is not (".docx" or ".pptx" or ".xlsx")) throw new NotSupportedException("The local Office page renderer accepts Word, PowerPoint and Excel Open XML files.");
        if (!source.Exists) throw new FileNotFoundException("Office input was not found.", sourcePath);
        if (source.Length > MaxBytes) throw new InvalidDataException("Office page rendering input exceeds 128 MiB.");
        var duration = timeout ?? TimeSpan.FromSeconds(120);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(10)) throw new InvalidOperationException("Office renderer timeout must be positive and at most ten minutes.");
        await Slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? root = null;
        try
        {
            root = Directory.CreateTempSubdirectory("docredock-office-pages-").FullName;
            var input = Path.Combine(root, "input" + extension);
            // A private copy also removes package instructions for macros, external connections and linked content.
            // The original source, relationships and media remain untouched for restoration.
            var (requiredDiagramText,slideCount)=CopyForLocalRendering(source.FullName, input, cancellationToken);
            var profile = Path.Combine(root, "profile");
            Directory.CreateDirectory(Path.Combine(profile, "user"));
            // Every key is declared in LibreOffice core, officecfg/registry/schema/org/openoffice/Office/<component>.xcs
            // (https://github.com/LibreOffice/core, master read on 2026-11-20):
            //   Common.xcs  Security/Scripting: MacroSecurityLevel (xs:int 0..3, 3 = highest), DisableMacrosExecution
            //               ("macro execution is disabled in general"), BlockUntrustedRefererLinks ("block any links
            //               originating from documents that are not among the trusted locations"), DisableActiveContent
            //               ("disables active embedded content (OLE) and DDE").
            //   Common.xcs  Misc: FirstRun, CrashReport ("sending crash reports to The Document Foundation").
            //   Writer.xcs  Content/Update: Link ("links in the document are updated during loading": 0 always, 1 on request, 2 never).
            //   Calc.xcs    Content/Update: Link ("if and how links are updated when loading": 0 always, 1 never, 2 on request).
            // The online update check has no key in these schemas (Jobs.xcs only declares an extensible Arguments group), so none is set.
            await File.WriteAllTextAsync(Path.Combine(profile, "user", "registrymodifications.xcu"), """
                <?xml version="1.0" encoding="UTF-8"?>
                <oor:items xmlns:oor="http://openoffice.org/2001/registry">
                <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop></item>
                <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="DisableMacrosExecution" oor:op="fuse"><value>true</value></prop></item>
                <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="BlockUntrustedRefererLinks" oor:op="fuse"><value>true</value></prop></item>
                <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="DisableActiveContent" oor:op="fuse"><value>true</value></prop></item>
                <item oor:path="/org.openoffice.Office.Common/Misc"><prop oor:name="FirstRun" oor:op="fuse"><value>false</value></prop></item>
                <item oor:path="/org.openoffice.Office.Common/Misc"><prop oor:name="CrashReport" oor:op="fuse"><value>false</value></prop></item>
                <item oor:path="/org.openoffice.Office.Writer/Content/Update"><prop oor:name="Link" oor:op="fuse"><value>2</value></prop></item>
                <item oor:path="/org.openoffice.Office.Calc/Content/Update"><prop oor:name="Link" oor:op="fuse"><value>1</value></prop></item>
                </oor:items>
                """, cancellationToken).ConfigureAwait(false);
            var info = new ProcessStartInfo(Path.GetFullPath(executable))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = root };
            foreach (var arg in new[] { "-env:UserInstallation=" + new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri,
                         "--headless", "--nologo", "--nodefault", "--norestore", "--convert-to",
                         extension switch { ".docx" => "pdf:writer_pdf_Export", ".xlsx" => "pdf:calc_pdf_Export", _ => "pdf:impress_pdf_Export" },
                         "--outdir", root, input }) info.ArgumentList.Add(arg);
            var output = Path.Combine(root, "input.pdf");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(duration);
            try
            {
                var exit = await RunProcessAsync(info, deadline.Token).ConfigureAwait(false);
                if (exit != 0) throw new InvalidDataException("Local Office rendering failed (exit " + exit + ").");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("Local Office page rendering exceeded its time limit."); }
            if (!File.Exists(output) || new FileInfo(output).Length is <= 0 or > MaxBytes)
                throw new InvalidDataException("The local Office engine did not produce a bounded PDF.");
            cancellationToken.ThrowIfCancellationRequested();
            PdfExtractionResult pdf;
            try { pdf = PdfTextExtractor.Extract(output, new PdfExtractionOptions(MaxInputBytes: MaxBytes, MaxPages: MaxPages), cancellationToken); }
            // More pages than the budget, or an engine PDF that cannot be read, is a rendering failure the caller
            // reports while keeping the structured layout; it must not abort the whole conversion.
            catch (PdfExtractionException exception) { throw new InvalidDataException("The local Office engine's page output could not be used: " + exception.Message, exception); }
            // Geometric reading order can interleave the glyphs of a rotated label; the paint-order text is its second source.
            var absent=MissingSmartArtText(requiredDiagramText,pdf.Pages.OrderBy(p=>p.PageNumber).Select(p=>(p.Text,p.PaintOrderText)).ToArray(),slideCount);
            if(absent.Count>0)throw new InvalidDataException("The local Office renderer omitted SmartArt text: "+string.Join("; ",absent.Take(10).Select(text=>text[..Math.Min(120,text.Length)]))+(absent.Count>10?"; and "+(absent.Count-10)+" more":"")+". Structured source layout is retained instead of silently publishing incomplete pages.");
            if (pdf.Pages.Count is <= 0 or > MaxPages) throw new InvalidDataException("Office page count exceeds the rendering budget.");
            var pages = await rasterizer.RasterizeAsync(output, pdf.Pages.Select(p => p.PageNumber).ToArray(),
                new PdfRasterizationOptions(Dpi: 144, MaxPixelsPerPage: 20_000_000, MaxTotalPixels: 200_000_000,
                    Timeout: TimeSpan.FromSeconds(15)), cancellationToken).ConfigureAwait(false);
            if (pages.Count != pdf.Pages.Count || pages.Select(p => p.PageNumber).Distinct().Count() != pages.Count)
                throw new InvalidDataException("The local Office renderer returned an incomplete page set.");
            var rendered = new List<OfficeRenderedPage>();
            long total = 0;
            foreach (var page in pages.OrderBy(p => p.PageNumber))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var original = pdf.Pages.Single(p => p.PageNumber == page.PageNumber);
                var box = original.MediaBox ?? throw new InvalidDataException("An Office PDF page has no physical size.");
                if (!double.IsFinite(box.Width) || !double.IsFinite(box.Height) || box.Width is <= 0 or > 14400 || box.Height is <= 0 or > 14400 ||
                    page.MediaType != "image/png" || page.Content.Length is <= 0 or > 25 * 1024 * 1024)
                    throw new InvalidDataException("Invalid local Office page data.");
                total += page.Content.Length;
                if (total > 200L * 1024 * 1024) throw new InvalidDataException("Office page image byte budget exceeded.");
                rendered.Add(new(page.PageNumber, box.Width, box.Height, page.MediaType, page.Content.ToArray())
                    {Text=string.Join("\n",original.Regions.Select(r=>r.Text))});
            }
            return new("LibreOffice", rendered);
        }
        finally
        {
            // A stopped engine releases its working directory a moment after it is killed; the private copy
            // of the document must not stay behind because the first attempt came too early.
            for (var attempt = 0; root is not null; attempt++)
            {
                try { Directory.Delete(root, recursive: true); break; }
                catch (DirectoryNotFoundException) { break; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { if (attempt >= 30) break; await Task.Delay(100, CancellationToken.None).ConfigureAwait(false); }
            }
            Slots.Release();
        }
    }

    protected virtual async Task<int> RunProcessAsync(ProcessStartInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        info.RedirectStandardError = true; info.RedirectStandardOutput = true;
        using var process = Process.Start(info) ?? throw new IOException("The local Office engine could not start.");
        static async Task DrainAsync(StreamReader reader, CancellationToken token)
        { var buffer = new char[4096]; while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) != 0) { } }
        var stdout = DrainAsync(process.StandardOutput, cancellationToken);
        var stderr = DrainAsync(process.StandardError, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception) { }
                // Termination is asynchronous: wait (bounded) so that the caller can remove the private directory.
                using var exited = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(exited.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            // Observe both drain tasks on cancellation before disposing their streams.
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private static (IReadOnlyList<SmartArtLabel> Labels,int SlideCount) CopyForLocalRendering(string source, string output, CancellationToken cancellationToken)
    {
        using var input = ZipFile.OpenRead(source);
        using var target = ZipFile.Open(output, ZipArchiveMode.Create);
        if(input.Entries.Count>20_000)throw new InvalidDataException("Office rendering package has too many entries.");
        // Entry names are checked, and the parts and relationships to leave out are known, before anything is written.
        var plan=PlanPrivateCopy(input,cancellationToken);
        var budget=new CopyBudget();
        long total=0;
        var requiredText=new List<SmartArtLabel>();
        var slides=SlideNumbers(input);
        foreach (var entry in input.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total+=entry.Length;
            if(entry.Length>32L*1024*1024||total>256L*1024*1024||entry.Length>100*Math.Max(1,entry.CompressedLength))
                throw new InvalidDataException("Office rendering package exceeds the decompression budget.");
            if (plan.Removes(entry.FullName)) continue;
            using var destination = target.CreateEntry(entry.FullName, CompressionLevel.Fastest).Open();
            var owner=RelationshipOwner(entry.FullName);
            plan.RemovedRelationshipIds.TryGetValue(entry.FullName,out var removedIds);
            if (entry.FullName.EndsWith(".rels", StringComparison.Ordinal)||
                entry.FullName is "[Content_Types].xml" or "xl/workbook.xml"||removedIds is not null&&entry.FullName.EndsWith(".xml",StringComparison.Ordinal)||
                entry.FullName.StartsWith("xl/tables/",StringComparison.Ordinal)&&entry.FullName.EndsWith(".xml",StringComparison.Ordinal)||
                entry.FullName.StartsWith("xl/pivotCache/pivotCacheDefinition",StringComparison.Ordinal)&&entry.FullName.EndsWith(".xml",StringComparison.Ordinal)||
                entry.FullName.StartsWith("ppt/slides/slide",StringComparison.Ordinal)&&entry.FullName.EndsWith(".xml",StringComparison.Ordinal)||
                entry.FullName.StartsWith("xl/worksheets/",StringComparison.Ordinal)&&entry.FullName.EndsWith(".xml",StringComparison.Ordinal)||
                entry.FullName.StartsWith("xl/drawings/",StringComparison.Ordinal)&&entry.FullName.EndsWith(".xml",StringComparison.Ordinal)||
                entry.FullName.StartsWith("word/",StringComparison.Ordinal)&&entry.FullName.EndsWith(".xml",StringComparison.Ordinal))
            {
                // Whitespace is content here: a run that holds only a space (<a:t> </a:t>) separates two words.
                var xml = LoadPackageXml(entry, budget, preserveWhitespace: true);
                if(entry.FullName.StartsWith("ppt/slides/slide",StringComparison.Ordinal))ExpandSavedSmartArt(xml,entry.FullName,input,requiredText,slides.TryGetValue(entry.FullName,out var slideNumber)?slideNumber:null,cancellationToken);
                else if(!entry.FullName.EndsWith(".rels",StringComparison.Ordinal))RequireSmartArtText(xml,entry.FullName,input,requiredText,cancellationToken);
                // External targets other than hyperlinks, macro and data connection parts, and whatever names a part the copy leaves out.
                foreach (var rel in xml.DocumentElement!.ChildNodes.OfType<System.Xml.XmlElement>().Where(e =>
                             owner is not null ? RemovesRelationship(e, owner, plan.RemovedParts) :
                             e.GetAttribute("TargetMode").Equals("External", StringComparison.OrdinalIgnoreCase)&&!e.GetAttribute("Type").EndsWith("/hyperlink",StringComparison.Ordinal) ||
                             e.GetAttribute("Type").Contains("vbaProject", StringComparison.OrdinalIgnoreCase)).ToArray())
                    rel.ParentNode!.RemoveChild(rel);
                if(entry.FullName=="[Content_Types].xml")MakeContentTypesConsistent(xml,plan);
                if(removedIds is not null)RemoveDanglingReferences(xml,removedIds);
                if(xml.DocumentElement.LocalName=="workbook")RemoveExternalWorkbookReferences(xml);
                if(entry.FullName.StartsWith("xl/",StringComparison.Ordinal))DetachSpreadsheetQueries(xml);
                // A page preview must show the source's saved values, not results recalculated by another engine.
                if(entry.FullName.StartsWith("xl/worksheets/",StringComparison.Ordinal))
                    foreach(var formula in xml.GetElementsByTagName("f","http://schemas.openxmlformats.org/spreadsheetml/2006/main").OfType<System.Xml.XmlElement>().ToArray())
                        formula.ParentNode!.RemoveChild(formula);
                if(entry.FullName.StartsWith("word/",StringComparison.Ordinal))
                {
                    const string word="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                    foreach(var deleted in xml.GetElementsByTagName("*",word).OfType<System.Xml.XmlElement>().Where(e=>e.LocalName is "del" or "moveFrom").ToArray())
                        deleted.ParentNode?.RemoveChild(deleted);
                    foreach(var inserted in xml.GetElementsByTagName("*",word).OfType<System.Xml.XmlElement>().Where(e=>e.LocalName is "ins" or "moveTo").ToArray())
                    {
                        if(inserted.ParentNode is not {} parent)continue;
                        while(inserted.FirstChild is {} child)parent.InsertBefore(child,inserted);
                        parent.RemoveChild(inserted);
                    }
                    ReplaceExternalFieldsByCachedResult(xml,word);
                    // The page shows the saved state: no mail merge data source and no field refresh on opening.
                    if(xml.DocumentElement.LocalName=="settings")
                        foreach(var setting in xml.DocumentElement.ChildNodes.OfType<System.Xml.XmlElement>().Where(e=>e.LocalName is "mailMerge" or "updateFields").ToArray())
                            xml.DocumentElement.RemoveChild(setting);
                }
                xml.Save(destination);
            }
            else { using var stream = new BoundedEntryStream(entry.Open(), budget); stream.CopyTo(destination); }
        }
        return (requiredText,slides.Count);
    }
}

public static class OfficePageRendererFactory
{
    public static IOfficePageRenderer? Discover(IPdfRasterizer? rasterizer, string? configuredPath = null, bool disabled = false)
    {
        if (disabled || rasterizer is null) return null;
        var path = configuredPath ?? Environment.GetEnvironmentVariable("DOCREDOCK_OFFICE_RENDERER");
        if (!string.IsNullOrWhiteSpace(path))
            return File.Exists(path) ? new LibreOfficePageRenderer(Path.GetFullPath(path), rasterizer) : null;
        var names = OperatingSystem.IsWindows() ? new[] { "soffice.com", "soffice.exe" } : new[] { "soffice", "libreoffice" };
        var roots = new[] { Path.Combine(AppContext.BaseDirectory, "office", "program"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocRedock", "office", "program") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (var folder in roots.Where(p => !string.IsNullOrWhiteSpace(p)&&Path.IsPathFullyQualified(p)))
            foreach (var name in names)
                if (File.Exists(Path.Combine(folder, name))) return new LibreOfficePageRenderer(Path.GetFullPath(Path.Combine(folder, name)), rasterizer);
        return null;
    }
}
