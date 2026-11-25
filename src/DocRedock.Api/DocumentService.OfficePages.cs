using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Render;

namespace DocRedock.Api;

public sealed partial class DocumentService
{
    private async Task<(ReadableDocument Document,Dictionary<string,ReadableHtmlAsset>? Assets)> ApplyOfficePagesAsync(
        ReadableDocument document,Dictionary<string,ReadableHtmlAsset>? assets,string source,ReadableDocumentExportOptions options,
        List<Diagnostic> diagnostics,CancellationToken cancellationToken)
    {
        if(document.SourceFormat is not (DocumentFormatKind.Docx or DocumentFormatKind.Pptx or DocumentFormatKind.Xlsx) ||
            options.OfficeLayout==OfficeRenderingMode.Structured)return(document,assets);
        var requested=options.OfficeLayout==OfficeRenderingMode.Source;
        var needed=requested||document.SourceFormat==DocumentFormatKind.Docx||diagnostics.Any(d=>
            new[]{"scene3d","sp3d","SmartArt","3-D","3D chart","text body warp"}.Any(s=>d.Message.Contains(s,StringComparison.OrdinalIgnoreCase)));
        if(!needed)return(document,assets);
        var revisions=RevisionSharingReview.Build(diagnostics);
        var incompatible=options.OmitImages||options.Sheets is {Count:>0}||
            DocumentContentPolicyRules.Parse(options.ContentPolicy)!=DocumentContentPolicy.Visible||
            revisions.RevisionsIncluded;
        if(incompatible)
        {
            if(requested)throw new InvalidOperationException("Source pages require visible content, images enabled, no sheet filter and final revisions. Use structured layout for other policies.");
            return(document,assets);
        }
        if(officePageRenderer is null)
        {
            if(requested)throw new InvalidOperationException("Source page rendering requires a local Office renderer. Set DOCREDOCK_OFFICE_RENDERER to the installed soffice executable.");
            diagnostics.Add(new("OfficeSourcePagesUnavailable","Exact page layout, 3D and uncached SmartArt need a local Office renderer. Set DOCREDOCK_OFFICE_RENDERER or use --office-layout structured for typed layout.",DiagnosticSeverity.Information));
            return(document with{Notices=Notices()},assets);
        }
        try
        {
            var result=await officePageRenderer.RenderAsync(source,cancellationToken).ConfigureAwait(false);
            if(result.Pages.Count is <1 or >128 || result.Pages.Select(p=>p.PageNumber).Order().Where((n,i)=>n!=i+1).Any())
                throw new InvalidDataException("The Office renderer returned an incomplete page sequence.");
            var pages=new List<ReadableFixedPage>();
            var pageAssets=new Dictionary<string,ReadableHtmlAsset>(StringComparer.Ordinal);
            long total=assets?.Values.Sum(a=>(long)a.Content.Length)??0;
            foreach(var page in result.Pages.OrderBy(p=>p.PageNumber))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(!double.IsFinite(page.WidthPoints)||!double.IsFinite(page.HeightPoints)||page.WidthPoints is <=0 or >14400||page.HeightPoints is <=0 or >14400||
                    page.MediaType!="image/png"||page.Content.Length is <8 or >25*1024*1024||
                    !page.Content.Span[..8].SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))
                    throw new InvalidDataException("The Office renderer returned invalid page data.");
                total+=page.Content.Length;
                if(total>ReadableHtmlOptions.DefaultMaxTotalImageBytes)throw new InvalidDataException("Office page images exceed the HTML byte budget.");
                var reference="docredock-office-page-"+page.PageNumber+".png";
                while(assets?.ContainsKey(reference)==true)reference="_"+reference;
                pageAssets.Add(reference,new(page.MediaType,page.Content));
                pages.Add(new(page.PageNumber,reference,page.WidthPoints,page.HeightPoints,page.Text));
            }
            assets??=new(StringComparer.Ordinal);
            foreach(var pair in pageAssets)assets.Add(pair.Key,pair.Value);
            diagnostics.Add(new("OfficeSourcePagesRendered",$"Source page layout rendered locally with {result.Engine}: {pages.Count} pages. Structured text remains available below the pages.",DiagnosticSeverity.Information));
            return(document with{Blocks=[new ReadableOfficePages(result.Engine,pages),
                new ReadableDetails("office-structured-text",ReadableContent.Text(document.Language.StartsWith("ja",StringComparison.OrdinalIgnoreCase)?"本文・表・図の構造化テキスト":"Structured text, tables and diagrams"),document.Blocks)],Notices=Notices()},assets);
        }
        catch(Exception ex) when(!requested && ex is IOException or InvalidDataException or InvalidOperationException or TimeoutException or NotSupportedException or System.ComponentModel.Win32Exception or System.Xml.XmlException or UnauthorizedAccessException)
        {
            diagnostics.Add(new("OfficeSourcePagesFailed","The local Office engine could not render source pages; typed layout is retained. "+ex.Message,DiagnosticSeverity.Warning));
            return(document with{Notices=Notices()},assets);
        }
        IReadOnlyList<ReadableNotice> Notices()=>diagnostics.Select(d=>new ReadableNotice(d.Code,d.Message,d.NodeId)).ToArray();
    }
}
