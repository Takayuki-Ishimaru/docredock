using DocRedock.Core.Documents;

namespace DocRedock.Render;

public static partial class ReadableHtmlWriter
{
    private sealed partial class HtmlDocumentWriter
    {
        private void OfficePages(ReadableOfficePages document)
        {
            foreach(var page in document.Pages.Take(128))
            {
                _typedCancellation.ThrowIfCancellationRequested();
                if(!double.IsFinite(page.WidthPoints)||!double.IsFinite(page.HeightPoints)||
                    page.WidthPoints is <=0 or >14400||page.HeightPoints is <=0 or >14400)continue;
                var name="office-page-"+(++_sourcePrintPageCount);
                _pageStyles.Append("@page ").Append(name).Append("{size:").Append(Px(page.WidthPoints)).Append("pt ")
                    .Append(Px(page.HeightPoints)).Append("pt;margin:0;}\n");
                _body.Append("<figure class=\"office-source-page\" data-office-page=\"").Append(page.Number)
                    .Append("\" data-office-engine=\"").Append(Enc(document.Engine)).Append("\" style=\"page:").Append(name)
                    .Append(";width:").Append(Px(page.WidthPoints)).Append("pt;height:").Append(Px(page.HeightPoints-.05)).Append("pt\">");
                EmitImage(_body,page.ImageReference,(_ja?"原本のページ ":"Source page ")+page.Number,null,null);
                _body.Append("</figure>\n");
                if(page.Text is {Length:>0} text)
                    _body.Append("<details data-kind=\"office-page-text\" data-office-text-page=\"").Append(page.Number).Append("\"><summary>")
                        .Append(_ja?"このページのテキスト":"Text on this page").Append("</summary><pre>").Append(Enc(text)).Append("</pre></details>");
            }
        }
    }
    private const string OfficePageCss="""
        .office-source-page{position:relative;margin:1em 0;padding:0;max-width:none;overflow:hidden;background:white;color:black;box-shadow:0 1px 6px #777;break-inside:avoid}
        .office-source-page>img{display:block;width:100%;height:100%;max-width:none;object-fit:fill;background:white}
        .office-fixed-pages .reader-page{overflow-x:auto}
        @media print{
          .office-fixed-pages .reader-title,.office-fixed-pages .reader-source,.office-fixed-pages details[data-kind="office-structured-text"],.office-fixed-pages details[data-kind="office-page-text"]{display:none!important}
          .office-fixed-pages .reader-page{overflow:visible!important;margin:0!important;padding:0!important;border:0!important;box-shadow:none!important}
          .office-source-page{margin:0!important;padding:0!important;border:0!important;box-shadow:none!important;break-after:page;break-inside:avoid}
          .office-source-page:last-of-type{break-after:auto}
        }
        """;
}
