namespace DocRedock.Render;

public static partial class ReadableHtmlWriter
{
    private const string DirectLayoutCss = """
        body.direct-document{margin:0;padding:0;overflow:hidden;color-scheme:light}
        .direct-document .reader-shell{--paper:#fff;--ink:#18212b;--edge:#b5bfca;--muted:#536170;--panel:#f2f5f8;background:var(--paper);color:var(--ink);height:100vh;display:grid;grid-template-columns:17rem minmax(0,1fr)}
        .direct-document .reader-nav{overflow:auto;padding:1rem;background:var(--panel);border-right:1px solid var(--edge);font-size:.9rem;min-width:0}
        .direct-document .reader-nav h2{font-size:1rem;margin:.3rem 0 1rem}
        .direct-document .reader-nav ol{padding-left:1.4rem;margin:0}
        .direct-document .reader-nav li{margin:.55rem 0;overflow-wrap:anywhere}
        .direct-document .reader-nav a{color:inherit;text-decoration:none}
        .direct-document .reader-nav a:hover{text-decoration:underline}
        .direct-document main.reader-content{min-width:0;width:auto;max-width:none;margin:0;padding:0;height:100%;box-sizing:border-box;overflow:auto;scroll-padding-top:1rem}
        .direct-document .reader-page{min-width:100%;box-sizing:border-box;padding:1.5rem 2rem 4rem}
        .direct-document .reader-toolbar{display:flex;flex-wrap:wrap;align-items:center;gap:.4rem;margin-bottom:1.2rem;font-size:.9rem}
        .direct-document .reader-toolbar label{border:1px solid var(--edge);border-radius:.3rem;padding:.25rem .65rem;cursor:pointer;background:var(--panel)}
        .direct-document>.theme-input{position:absolute;width:1px;height:1px;clip-path:inset(50%);overflow:hidden}
        #docredock-theme-original:checked~.reader-shell label[for="docredock-theme-original"],#docredock-theme-light:checked~.reader-shell label[for="docredock-theme-light"],#docredock-theme-dark:checked~.reader-shell label[for="docredock-theme-dark"]{outline:2px solid #3975ac;outline-offset:1px}
        .theme-input:focus-visible~.reader-shell .reader-toolbar{outline:2px solid #3975ac}
        #docredock-theme-dark:checked~.reader-shell{--paper:#18212b;--ink:#edf2f8;--edge:#536170;--muted:#c2cdd9;--panel:#24313e;color-scheme:dark}
        @media screen{
          .direct-document:not(.workbook-document)>#docredock-theme-dark:checked~.reader-shell td,.direct-document:not(.workbook-document)>#docredock-theme-dark:checked~.reader-shell th{background:var(--paper)!important;color:var(--ink)!important;border-color:var(--edge)!important}
          .direct-document:not(.workbook-document)>#docredock-theme-light:checked~.reader-shell td,.direct-document:not(.workbook-document)>#docredock-theme-light:checked~.reader-shell th{background:var(--paper)!important;color:var(--ink)!important}
          #docredock-theme-dark:checked~.reader-shell .source-low-dark,#docredock-theme-light:checked~.reader-shell .source-low-light,#docredock-theme-original:checked~.reader-shell .source-low-light{color:var(--ink)!important}
        }
        .direct-document .source-reference{font-size:.8rem;color:var(--muted);margin:.3rem 0 .8rem;overflow-wrap:anywhere}
        .direct-document section.source-section{margin:0 0 3rem;scroll-margin-top:1rem}
        .direct-document .table-scroll{overflow:visible;max-height:none;max-width:none;margin:1rem 0}
        .direct-document table{border-collapse:separate;border-spacing:0;table-layout:fixed;max-width:none;color:inherit;background:var(--paper)}
        .direct-document th,.direct-document td{min-width:0;padding:.35rem .5rem;vertical-align:top;overflow-wrap:anywhere;white-space:pre-wrap;border-right:1px solid var(--edge);border-bottom:1px solid var(--edge)}
        .direct-document tbody tr:nth-child(even){background:transparent}
        .direct-document img{background:#fff;padding:.4rem;box-sizing:border-box}
        .direct-document img.source-sized-image{padding:0;height:auto}
        .direct-document td>figure{margin:.3rem 0}
        .direct-document figure.diagram svg{max-width:100%;height:auto}
        .direct-document code{color:var(--ink);background:var(--panel)}
        .direct-document pre{color:var(--ink);background:var(--panel)}
        .direct-document pre code{color:inherit;background:none;white-space:pre}
        .direct-document tr>:first-child{border-left:1px solid var(--edge)}
        .direct-document tr:first-child>th,.direct-document tr:first-child>td{border-top:1px solid var(--edge)}
        .direct-document .narrow-column{padding:.3rem .2rem}
        .direct-document .numeric-cell{text-align:right}
        .direct-document th{background:var(--panel);color:var(--ink);font-weight:600;position:static;top:auto}
        .direct-document thead{position:sticky;top:0;z-index:3}.direct-document thead th{position:static}
        .direct-document .frozen-column{position:sticky;left:var(--frozen-left,0);z-index:2;background:var(--paper)}
        .direct-document thead .frozen-column{z-index:4;background:var(--panel)}
        .workbook-document table{background:#fff;color:#18212b;color-scheme:light}
        .workbook-document th{background:#f2f5f8;color:#18212b}
        .workbook-document .frozen-column{background:#fff}.workbook-document thead .frozen-column{background:#f2f5f8}
        .direct-document td>p:first-child{margin-top:0}.direct-document td>p:last-child{margin-bottom:0}
        .direct-document .conversion-report{padding:1rem;border:1px solid var(--edge);background:var(--panel);margin:1rem 0 2rem}
        .direct-document .conversion-report summary{cursor:pointer;font-weight:600}
        .direct-document .conversion-report ul{margin-bottom:0}
        .direct-document .print-copy{display:none}
        .direct-document .sheet-cover{min-height:18rem;padding:2rem;border:1px solid var(--edge);max-width:55rem;box-sizing:border-box}
        .direct-document .sheet-cover>h2{margin-top:0}
        @media(max-width:760px){
          .direct-document .reader-shell{grid-template-columns:1fr;grid-template-rows:auto minmax(0,1fr)}
          .direct-document .reader-nav{max-height:25vh;border-right:0;border-bottom:1px solid var(--edge);padding:.7rem 1rem}
          .direct-document .reader-nav h2{margin:0 0 .5rem}.direct-document .reader-nav ol{display:flex;flex-wrap:wrap;gap:.2rem 1.5rem;padding-left:1.5rem}
          .direct-document .reader-nav li{margin:.2rem 0}.direct-document .reader-page{padding:1rem}
        }
        @media print{
          html,body.direct-document{height:auto!important;overflow:visible!important;background:white!important;color:black!important;color-scheme:light!important}
          .direct-document .reader-shell{--paper:#fff!important;--ink:#18212b!important;--edge:#b5bfca!important;--muted:#536170!important;--panel:#f2f5f8!important;display:block;height:auto;background:white;color:black}
          .direct-document .reader-nav,.direct-document .reader-toolbar,.direct-document>.theme-input,.direct-document .conversion-report,.direct-document .outside-print-area{display:none!important}
          .workbook-document .reader-title,.workbook-document .reader-source{display:none!important}
          .direct-document main.reader-content{display:block;height:auto;width:auto;max-width:none;overflow:visible!important;padding:0;margin:0}
          .direct-document .reader-page{padding:0}
          .direct-document .table-scroll{overflow:visible!important;max-height:none!important;width:auto;max-width:none}
          .direct-document table{width:100%!important;min-width:0!important;max-width:100%!important;table-layout:fixed;print-color-adjust:exact;-webkit-print-color-adjust:exact}
          .direct-document th,.direct-document td{position:static!important;top:auto!important;left:auto!important;font-size:8pt!important;padding:2pt;overflow-wrap:anywhere;break-inside:avoid}
          .direct-document .narrow-column{font-size:7pt!important;padding:1pt}
          .direct-document thead{display:table-header-group}.direct-document tfoot{display:table-footer-group}
          .direct-document thead{position:static!important}.direct-document .manual-page-break{break-before:page}
          .direct-document tr{break-inside:avoid}.direct-document section.source-section{margin:0 0 1rem;break-before:page}
          .direct-document section.source-section:first-of-type{break-before:auto}
          .direct-document .screen-copy{display:none!important}.direct-document .print-copy{display:block!important}
          .direct-document .print-copy+.print-copy{break-before:page}
          .direct-document details{display:block}.direct-document details>*{display:block}.direct-document details>summary{list-style:none}
          .direct-document .source-reference{color:#444}.direct-document .sheet-cover{min-height:70vh;border:0}
        }
        """;
}
