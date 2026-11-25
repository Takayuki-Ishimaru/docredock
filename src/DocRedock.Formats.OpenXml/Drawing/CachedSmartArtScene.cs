using System.Globalization;
using System.Xml;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.OpenXml.Drawing;

internal static class CachedSmartArtScene
{
    internal static IReadOnlyList<WorkbookDrawingObject>? Expand(XmlElement relIds,WorkbookDrawingObject frame,
        string ownerPart,string format,Func<string,byte[]?> readPart,XmlDocument? theme,ICollection<string> warnings,CancellationToken cancellationToken,
        DrawingMlPictureFills? pictureFills=null)
    {
        var notices=new List<string>();
        var drawing=SmartArtDrawingResolver.TryResolve(relIds,ownerPart,readPart,notices,cancellationToken);
        foreach(var message in notices)warnings.Add("SourceDrawingScenePartial: SmartArt "+frame.Id+": "+message);
        if(drawing is null)return null;
        var transform=drawing.ShapeTree.SelectSingleNode("./*[local-name()='grpSpPr']/*[local-name()='xfrm']") as XmlElement;
        var extent=transform?.SelectSingleNode("./*[local-name()='ext']") as XmlElement;
        double Number(string name)=>double.TryParse(extent?.GetAttribute(name),NumberStyles.Float,CultureInfo.InvariantCulture,out var value)&&double.IsFinite(value)?value/9525:0;
        var width=Number("cx");var height=Number("cy");double x=0,y=0;
        if(width<=0||height<=0)
        {
            var measured=DrawingMlSceneReader.Read(drawing.ShapeTree,format,drawing.PartUri,theme,cancellationToken:cancellationToken);
            if(measured.Count==0)return null;
            var points=measured.SelectMany(i=>new[]{i.Transform.Point(0,0),i.Transform.Point(i.Width,0),i.Transform.Point(0,i.Height),i.Transform.Point(i.Width,i.Height)}).ToArray();
            x=points.Min(p=>p.X);y=points.Min(p=>p.Y);width=points.Max(p=>p.X)-x;height=points.Max(p=>p.Y)-y;
        }
        if(!double.IsFinite(width)||!double.IsFinite(height)||width<=0||height<=0||width>1e8||height>1e8)return null;
        // Office resizes the saved shapes' frames with the diagram; captions, line weights and rotated outlines are not stretched.
        // A picture fill of a saved shape (a:blipFill, the picture of a picture layout) names its picture in the drawing part's own relationships.
        var items=DrawingMlSceneReader.Read(drawing.ShapeTree,format,drawing.PartUri,theme,pictureFills is null?null:ImageRelationships(drawing.PartUri,readPart),
            warnings:warnings,cancellationToken:cancellationToken,initialChildSpace:(frame.Width/width,frame.Height/height,x,y),pictureFills:pictureFills);
        if(items.Count==0)return null;
        // A cached picture object, chart or table requires its own asset identity; retain the ordinary fallback
        // until the local source-page engine can render the entire composition in one coordinate space.
        if(items.Any(i=>i.Kind is "picture" or "chart" or "table"))
        {warnings.Add("SourceDrawingScenePartial: SmartArt "+frame.Id+" contains cached media requiring local Office page rendering.");return null;}
        return items.Select((item,index)=>item with{
            Id=frame.Id[..Math.Min(frame.Id.Length,400)]+":smartart:"+index+":"+item.Id[..Math.Min(item.Id.Length,400)],
            SourceNodeId=frame.SourceNodeId,Hidden=frame.Hidden||item.Hidden,Printable=frame.Printable&&item.Printable,
            Transform=frame.Transform*item.Transform,
            Source=item.Source is {} source?source with{Locators=source.Locators.Concat([new AnchorLocator("smartart_owner",frame.Id)]).ToArray()}:frame.Source,
        }).ToArray();
    }
    /// <summary>The internal image relationships of the saved drawing part, by id; empty when the part has none.</summary>
    private static Dictionary<string,string> ImageRelationships(string partUri,Func<string,byte[]?> readPart)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        var slash=partUri.LastIndexOf('/');
        if(readPart(partUri[..(slash+1)]+"_rels/"+partUri[(slash+1)..]+".rels") is not {Length:>0 and <=4*1024*1024} bytes)return result;
        XmlDocument document;
        try{document=SourceDrawingPackage.Load(bytes);}
        catch(XmlException){return result;}
        foreach(var relationship in document.DocumentElement?.ChildNodes.OfType<XmlElement>().Where(e=>e.LocalName=="Relationship")??[])
            if(relationship.GetAttribute("Type").Trim().EndsWith("/image",StringComparison.Ordinal)&&relationship.GetAttribute("TargetMode").Trim()!="External"&&
                relationship.GetAttribute("Id").Trim() is {Length:>0} id&&relationship.GetAttribute("Target").Trim() is {Length:>0} target)
                result.TryAdd(id,target);
        return result;
    }
}
