using System.Globalization;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;

namespace DocRedock.Render;

internal sealed record WorkbookSourceBand(double SourceStart,double Length,double DestinationStart);
internal sealed record WorkbookPrintPage(double PaperWidth,double PaperHeight,double Left,double Top,
    double ContentWidth,double ContentHeight,double Scale,IReadOnlyList<WorkbookSourceBand> Columns,
    IReadOnlyList<WorkbookSourceBand> Rows);
internal static class WorkbookPagePlan
{
    public static IReadOnlyList<WorkbookPrintPage> Create(ReadableWorksheetScene scene,WorkbookSheetPresentation? sheet)
    {
        var x=new WorkbookSourceAxis(sheet,true,scene.IncludeHiddenDimensions);var y=new WorkbookSourceAxis(sheet,false,scene.IncludeHiddenDimensions);
        var print=sheet?.Print;var (pw,ph)=print?.PaperSize switch{5=>(816d,1344d),8=>(1122.5197,1587.4016),9=>(793.7008,1122.5197),11=>(559.3701,793.7008),_=>(816d,1056d)};
        if(print?.Orientation=="landscape")(pw,ph)=(ph,pw);
        var m=print?.Margins;var left=(m?.Left??.7)*96;var right=(m?.Right??.7)*96;var top=(m?.Top??.75)*96;var bottom=(m?.Bottom??.75)*96;
        var width=Math.Max(1,pw-left-right);var height=Math.Max(1,ph-top-bottom);
        var bounds=Bounds(scene with {Scene=scene.Scene with{Objects=scene.Scene.Objects.Where(o=>o.Printable).ToArray()}},x,y);var areas=(print?.Areas??[]).Select(Range).OfType<(int R1,int R2,int C1,int C2)>()
            .Select(a=>(X1:x.Start(a.C1),X2:x.Start(a.C2+1),Y1:y.Start(a.R1),Y2:y.Start(a.R2+1))).ToArray();
        if(areas.Length==0)areas=[bounds];
        var pages=new List<WorkbookPrintPage>();
        foreach(var area in areas)
        {
            var sourceWidth=Math.Max(1,area.X2-area.X1);var sourceHeight=Math.Max(1,area.Y2-area.Y1);
            var scale=Math.Clamp((print?.Scale??100)/100d,.1,4);
            if(print?.FitToPage==true)
            {
                scale=1;
                if(print.FitToWidth is >0 and var fw)scale=Math.Min(scale,width*fw/sourceWidth);
                if(print.FitToHeight is >0 and var fh)scale=Math.Min(scale,height*fh/sourceHeight);
                scale=Math.Clamp(scale,.001,4);
            }
            var columns=Split(x,area.X1,area.X2,width/scale,print?.ColumnBreaks,Title(print?.TitleColumns,false));
            var rows=Split(y,area.Y1,area.Y2,height/scale,print?.RowBreaks,Title(print?.TitleRows,true));
            var tiles=print?.PageOrder=="overThenDown" ? rows.SelectMany(row=>columns.Select(col=>(Col:col,Row:row))) :
                columns.SelectMany(col=>rows.Select(row=>(Col:col,Row:row)));
            foreach(var (col,row) in tiles)
                {
                    if(pages.Count>=2048)throw new InvalidOperationException("The worksheet exceeds the 2048-page source layout limit.");
                    var usedWidth=col.Sum(b=>b.Length)*scale;var usedHeight=row.Sum(b=>b.Length)*scale;
                    pages.Add(new(pw,ph,left+(print?.HorizontalCentering==true?Math.Max(0,(width-usedWidth)/2):0),
                        top+(print?.VerticalCentering==true?Math.Max(0,(height-usedHeight)/2):0),width,height,scale,col,row));
                }
        }
        return pages;
    }
    public static (double X1,double X2,double Y1,double Y2) Bounds(ReadableWorksheetScene scene,WorkbookSourceAxis x,WorkbookSourceAxis y)
    {
        var table=scene.Grid?.SourceGrid??scene.Grid;var objects=scene.Scene.Objects;
        var corners=objects.SelectMany(o=>new[]{o.Transform.Point(0,0),o.Transform.Point(o.Width,0),o.Transform.Point(0,o.Height),o.Transform.Point(o.Width,o.Height)}).ToArray();
        var x1=table?.Columns.Count>0?x.Start(table.Columns.Min(c=>c.SourceIndex)):corners.Select(p=>p.X).DefaultIfEmpty(0).Min();
        var y1=table?.Rows.Count>0?y.Start(table.Rows.Min(r=>r.SourceRow??1)):corners.Select(p=>p.Y).DefaultIfEmpty(0).Min();
        var x2=table?.Columns.Count>0?x.Start(table.Columns.Max(c=>c.SourceIndex)+1):x1+1;
        var y2=table?.Rows.Count>0?y.Start(table.Rows.Max(r=>r.SourceRow??1)+1):y1+1;
        if(corners.Length>0){x1=Math.Min(x1,corners.Min(p=>p.X));y1=Math.Min(y1,corners.Min(p=>p.Y));x2=Math.Max(x2,corners.Max(p=>p.X));y2=Math.Max(y2,corners.Max(p=>p.Y));}
        return(Math.Max(0,x1),Math.Max(x1+1,x2),Math.Max(0,y1),Math.Max(y1+1,y2));
    }
    private static List<IReadOnlyList<WorkbookSourceBand>> Split(WorkbookSourceAxis axis,double start,double end,double capacity,
        IReadOnlyList<int>? breaks,(int Start,int End)? title)
    {
        var result=new List<IReadOnlyList<WorkbookSourceBand>>();var cursor=start;
        var manual=(breaks??[]).Where(b=>b>0&&b<axis.Maximum).Select(b=>axis.Start(b+1)).Where(p=>p>start&&p<end).Distinct().Order().ToArray();
        var titleStart=title is {} t?axis.Start(t.Start):0;var titleEnd=title is {} t2?axis.Start(t2.End+1):0;
        while(cursor<end-.001)
        {
            var bands=new List<WorkbookSourceBand>();var repeated=title is not null&&cursor>=titleEnd-.001&&titleEnd<=end&&titleEnd>titleStart;
            var prefix=repeated?titleEnd-titleStart:0;
            if(repeated)bands.Add(new(titleStart,prefix,0));
            var target=Math.Min(end,cursor+Math.Max(1,capacity-prefix));
            var nextBreak=manual.FirstOrDefault(p=>p>cursor+.001);
            if(nextBreak>0)target=Math.Min(target,nextBreak);
            var edge=axis.Start(axis.Index(target));
            if(target<end-.001&&edge>cursor+.001)target=edge;
            // A single oversized row/column is clipped over physical pages, rather than dropped.
            if(target<=cursor+.001)target=Math.Min(end,cursor+Math.Max(1,capacity-prefix));
            bands.Add(new(cursor,target-cursor,prefix));result.Add(bands);cursor=target;
            if(result.Count>2048)throw new InvalidOperationException("The worksheet exceeds the source pagination limit.");
        }
        if(result.Count==0)result.Add([new(start,Math.Max(1,end-start),0)]);
        return result;
    }
    private static (int R1,int R2,int C1,int C2)? Range(string text)
    {
        if(text.Length>512)return null;
        var m=Regex.Match(text,@"^\$?([A-Za-z]{1,3})\$?([0-9]{1,7})(?::\$?([A-Za-z]{1,3})\$?([0-9]{1,7}))?$",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));
        if(!m.Success)return null;var r1=int.Parse(m.Groups[2].Value,CultureInfo.InvariantCulture);var r2=m.Groups[4].Success?int.Parse(m.Groups[4].Value,CultureInfo.InvariantCulture):r1;
        var c1=Column(m.Groups[1].Value);var c2=m.Groups[3].Success?Column(m.Groups[3].Value):c1;
        return r1>0&&r2>=r1&&r2<=1048576&&c1>0&&c2>=c1&&c2<=16384?(r1,r2,c1,c2):null;
    }
    private static (int Start,int End)? Title(string? text,bool rows)
    {
        var parts=text?.Replace("$","",StringComparison.Ordinal).Split(':');if(parts?.Length!=2)return null;
        var first=rows&&int.TryParse(parts[0],out var n)?n:rows?0:Column(parts[0]);var last=rows&&int.TryParse(parts[1],out var e)?e:rows?0:Column(parts[1]);
        return first>0&&last>=first&&last<=(rows?1048576:16384)?(first,last):null;
    }
    private static int Column(string s){var n=0;foreach(var c in s.ToUpperInvariant()){if(c is <'A' or >'Z')return 0;n=n*26+c-'A'+1;}return n;}
}
