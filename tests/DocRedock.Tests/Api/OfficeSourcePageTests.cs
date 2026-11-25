using System.IO.Compression;
using System.Security.Cryptography;
using DocRedock.Api;
using DocRedock.Render;

namespace DocRedock.Tests.Api;

public sealed class OfficeSourcePageTests:IDisposable
{
    private readonly string root=Directory.CreateTempSubdirectory("docredock-office-page-tests-").FullName;
    public void Dispose()=>Directory.Delete(root,true);
    private static readonly byte[] Png=PngRasterImage.Encode(2,2,Enumerable.Repeat((byte)230,12).ToArray());
    private static OfficePageRenderResult Pages()=>new("Synthetic",[new(2,792,612,"image/png",Png),new(1,612,792,"image/png",Png)]);
    private sealed class Fake(Func<CancellationToken,Task<OfficePageRenderResult>> render):IOfficePageRenderer
    {
        public int Calls {get;private set;}
        public Task<OfficePageRenderResult> RenderAsync(string sourcePath,CancellationToken cancellationToken=default)
        {Calls++;return render(cancellationToken);}
    }
    private string Source(bool revisions=false)
    {
        var path=Path.Combine(root,"source.docx");
        if(File.Exists(path))return path;
        using var archive=ZipFile.Open(path,ZipArchiveMode.Create);
        void Write(string name,string content){using var writer=new StreamWriter(archive.CreateEntry(name).Open());writer.Write(content);}
        Write("[Content_Types].xml","<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='xml' ContentType='application/xml'/><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
        Write("_rels/.rels","<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='r1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>");
        Write("word/document.xml","<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>OFFICE_SEMANTIC_LABEL</w:t></w:r>"+
            (revisions?"<w:del w:id='1'><w:r><w:delText>DELETED_SECRET</w:delText></w:r></w:del>":"")+"</w:p><w:sectPr><w:pgSz w:w='12240' w:h='15840'/></w:sectPr></w:body></w:document>");
        return path;
    }
    private ReadableDocumentExportOptions Options(string output="result.html",OfficeRenderingMode mode=OfficeRenderingMode.Source)=>
        new(Source(),Path.Combine(root,output),OutputFormat:ReadableOutputFormat.Html,Language:"en",OfficeLayout:mode);

    [Fact]
    public async Task Source_pages_keep_physical_sizes_sequence_and_semantic_text_without_changing_the_original()
    {
        var fake=new Fake(_=>Task.FromResult(Pages()));
        var source=Source();var hash=SHA256.HashData(File.ReadAllBytes(source));
        var result=await new DocumentService(null,null,false,fake).ExportReadableAsync(Options());
        var html=File.ReadAllText(result.MarkdownPath);
        Assert.Equal(1,fake.Calls);
        Assert.Contains("@page office-page-1{size:612pt 792pt;margin:0;}",html);
        Assert.Contains("@page office-page-2{size:792pt 612pt;margin:0;}",html);
        Assert.True(html.IndexOf("data-office-page=\"1\"",StringComparison.Ordinal)<html.IndexOf("data-office-page=\"2\"",StringComparison.Ordinal));
        Assert.Contains("data-kind=\"office-structured-text\"",html);
        Assert.Contains("OFFICE_SEMANTIC_LABEL",html);
        Assert.Contains("details[data-kind=\"office-structured-text\"],.office-fixed-pages details[data-kind=\"office-page-text\"]{display:none!important}",html);
        Assert.Contains(result.Diagnostics,d=>d.Code=="OfficeSourcePagesRendered");
        Assert.Equal(hash,SHA256.HashData(File.ReadAllBytes(source)));
        Assert.False(Directory.Exists(Path.Combine(root,"result.assets")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Structured_HTML_and_Markdown_never_invoke_the_page_engine(bool markdown)
    {
        var fake=new Fake(_=>throw new InvalidOperationException("Must not call"));
        await new DocumentService(null,null,false,fake).ExportReadableAsync(Options(mode:OfficeRenderingMode.Structured) with{OutputFormat=markdown?ReadableOutputFormat.Markdown:ReadableOutputFormat.Html});
        Assert.Equal(0,fake.Calls);
    }
    [Theory]
    [InlineData("sanitized",false)]
    [InlineData("complete",false)]
    [InlineData("visible",true)]
    public async Task Content_filters_are_not_bypassed_by_full_page_pixels(string policy,bool omit)
    {
        var fake=new Fake(_=>Task.FromResult(Pages()));var service=new DocumentService(null,null,false,fake);
        var options=Options(mode:OfficeRenderingMode.Auto) with{ContentPolicy=policy,OmitImages=omit};
        var result=await service.ExportReadableAsync(options);
        Assert.Equal(0,fake.Calls);
        Assert.DoesNotContain("data-office-page=",File.ReadAllText(result.MarkdownPath));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ExportReadableAsync(options with{MarkdownPath=Path.Combine(root,"source.html"),OfficeLayout=OfficeRenderingMode.Source}));
        Assert.False(File.Exists(Path.Combine(root,"source.html")));
    }
    [Fact]
    public async Task Revision_markup_is_not_replaced_by_final_page_pixels()
    {
        var source=Source(revisions:true);var fake=new Fake(_=>Task.FromResult(Pages()));var service=new DocumentService(null,null,false,fake);
        await service.ExportReadableAsync(Options(mode:OfficeRenderingMode.Auto) with{SourcePath=source});
        Assert.Equal(0,fake.Calls);
        await service.ExportReadableAsync(Options("final.html") with{SourcePath=source,Revisions="final"});
        Assert.Equal(1,fake.Calls);
    }
    [Fact]
    public async Task Engine_failure_keeps_structured_text_in_auto_but_source_is_explicit_and_atomic()
    {
        var fake=new Fake(_=>throw new InvalidDataException("Synthetic engine failure"));var service=new DocumentService(null,null,false,fake);
        var result=await service.ExportReadableAsync(Options(mode:OfficeRenderingMode.Auto));
        Assert.Contains(result.Diagnostics,d=>d.Code=="OfficeSourcePagesFailed");
        Assert.Contains("OFFICE_SEMANTIC_LABEL",File.ReadAllText(result.MarkdownPath));
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.ExportReadableAsync(Options("failed.html")));
        Assert.False(File.Exists(Path.Combine(root,"failed.html")));
    }
    [Theory]
    [InlineData("duplicate")]
    [InlineData("gap")]
    [InlineData("size")]
    [InlineData("bytes")]
    public async Task Invalid_page_sets_cannot_publish_partial_images(string problem)
    {
        var result=Pages();var pages=result.Pages.ToArray();
        pages[0]=problem switch{"duplicate"=>pages[0] with{PageNumber=1},"gap"=>pages[0] with{PageNumber=3},"size"=>pages[0] with{WidthPoints=double.NaN},_=>pages[0] with{Content=new byte[10]}};
        var service=new DocumentService(null,null,false,new Fake(_=>Task.FromResult(result with{Pages=pages})));
        var output=await service.ExportReadableAsync(Options(mode:OfficeRenderingMode.Auto));
        Assert.Contains(output.Diagnostics,d=>d.Code=="OfficeSourcePagesFailed");
        Assert.DoesNotContain("data-office-page=",File.ReadAllText(output.MarkdownPath));
    }
    [Fact]
    public async Task Renderer_cancellation_does_not_write_a_partial_document()
    {
        using var cts=new CancellationTokenSource();
        var service=new DocumentService(null,null,false,new Fake(_=>{cts.Cancel();throw new OperationCanceledException(cts.Token);}));
        var options=Options();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.ExportReadableAsync(options,cts.Token));
        Assert.False(File.Exists(options.MarkdownPath));
    }
}
