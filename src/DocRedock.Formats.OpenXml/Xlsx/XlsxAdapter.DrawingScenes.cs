using System.Globalization;
using System.Text.Json;
using System.Xml;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Drawing;

namespace DocRedock.Formats.OpenXml.Xlsx;

public sealed partial class XlsxAdapter
{
    private static IReadOnlyDictionary<string, WorkbookDrawingScene> ReadDrawingScenes(
        IReadOnlyDictionary<string, byte[]> package, IReadOnlyList<XlsxWorksheetRecord> worksheets,
        IReadOnlyDictionary<string, WorksheetScan> scans, CancellationToken cancellationToken, ICollection<string> warnings)
    {
        var result = new Dictionary<string, WorkbookDrawingScene>(StringComparer.Ordinal);
        var theme = ReadThemeColors(package);
        var pictureFills = new DrawingMlPictureFills(uri => package.GetValueOrDefault(uri));
        var drawingTheme = package.FirstOrDefault(kv => kv.Key.StartsWith("xl/theme/", StringComparison.Ordinal) && kv.Key.EndsWith(".xml", StringComparison.Ordinal)).Value is { } themeBytes
            ? Load(themeBytes) : null;
        foreach (var sheet in worksheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scan = scans[sheet.Name];
            if (!scan.Layout.HasWorksheetDrawing) continue;
            // Full, source dimensions. Visibility compression is a projection decision.
            var metrics = new XlsxWorksheetMetrics(scan.Metrics.DefaultColumnWidth, scan.Metrics.DefaultRowHeight,
                scan.Layout.ColumnWidths, scan.Layout.RowHeights);
            if (!package.TryGetValue(sheet.PartUri, out var sheetBytes)) continue;
            var sheetXml = Load(sheetBytes);
            var relationships = ReadPictureRelationships(package,
                sheet.PartUri[..sheet.PartUri.LastIndexOf('/')] + "/_rels/" + Path.GetFileName(sheet.PartUri) + ".rels");
            var objects = new List<WorkbookDrawingObject>();
            var limitReported = false;
            // A limit that leaves drawing objects out of the sheet's drawing layer is reported once per sheet.
            void ReportLimit(string limit)
            {
                if (limitReported) return;
                limitReported = true;
                warnings.Add($"XlsxDrawingPresentationUnsupported: {sheet.Name}: {limit}; the objects beyond it are not drawn in the sheet's drawing layer. Compare with the source.");
            }
            // Only worksheet drawings: header/footer and comment/VML drawings have different ownership.
            foreach (var drawing in sheetXml.DocumentElement?.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "drawing") ?? [])
            {
                var id = drawing.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                if (!relationships.TryGetValue(id, out var rel) || rel.IsExternal) continue;
                var part = NormalizePartPath(rel.Target);
                if (!package.TryGetValue(part, out var bytes)) continue;
                var root = Load(bytes).DocumentElement;
                if (root is null) continue;
                var drawingRels = ReadPictureRelationships(package, part[..part.LastIndexOf('/')] + "/_rels/" + Path.GetFileName(part) + ".rels");
                string? FillPicture(string relationshipId, out string? reason)
                {
                    reason = "its relationship is missing or leaves the package";
                    return drawingRels.TryGetValue(relationshipId, out var picture) && !picture.IsExternal
                        ? pictureFills.Reference(NormalizePartPath(picture.Target), out reason) : null;
                }
                var anchorIndex = 0;
                foreach (var anchor in EnumerateDrawingAnchors(root))
                {
                    cancellationToken.ThrowIfCancellationRequested(); anchorIndex++;
                    var from = DirectChild(anchor, "from"); var to = DirectChild(anchor, "to");
                    var pos = DirectChild(anchor, "pos"); var ext = DirectChild(anchor, "ext");
                    if (anchor.LocalName != "absoluteAnchor" && from is null) continue;
                    double x = anchor.LocalName == "absoluteAnchor" ? Number(pos,"x") : metrics.ColumnStartEmu(Math.Clamp(ChildInt(from!,"col")+1,1,16384))+ChildLong(from!,"colOff");
                    double y = anchor.LocalName == "absoluteAnchor" ? Number(pos,"y") : metrics.RowStartEmu(Math.Clamp(ChildInt(from!,"row")+1,1,1048576))+ChildLong(from!,"rowOff");
                    var w = anchor.LocalName == "twoCellAnchor" && to is not null ? metrics.ColumnStartEmu(Math.Clamp(ChildInt(to,"col")+1,1,16384))+ChildLong(to,"colOff")-x : Number(ext,"cx");
                    var h = anchor.LocalName == "twoCellAnchor" && to is not null ? metrics.RowStartEmu(Math.Clamp(ChildInt(to,"row")+1,1,1048576))+ChildLong(to,"rowOff")-y : Number(ext,"cy");
                    var client = DirectChild(anchor,"clientData");
                    var printable = client?.GetAttribute("fPrintsWithSheet") is not ("0" or "false");
                    foreach (var child in CompatibleDrawingChildren(anchor))
                        Visit(child, WorkbookDrawingTransform.Identity, (x/9525,y/9525,Math.Max(0,w/9525),Math.Max(0,h/9525)), 0, false);

                    void Visit(XmlElement element, WorkbookDrawingTransform parent, (double X,double Y,double W,double H)? frame, int depth, bool hiddenParent,
                        (double Sx,double Sy,double Ox,double Oy)? space = null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (depth > 32 || objects.Count >= WorkbookDrawingSceneJson.MaxObjects)
                        {
                            ReportLimit(depth > 32 ? "drawing nesting exceeds the supported depth" : "drawing object count exceeds " + WorkbookDrawingSceneJson.MaxObjects);
                            return;
                        }
                        if (element.LocalName is not ("grpSp" or "sp" or "cxnSp" or "pic" or "graphicFrame")) return;
                        var properties = element.LocalName == "grpSp" ? DirectChild(element,"grpSpPr") : DirectChild(element,"spPr");
                        var transform = properties is null ? DirectChild(element,"xfrm") : DirectChild(properties,"xfrm");
                        var off = transform is null ? null : DirectChild(transform,"off"); var size = transform is null ? null : DirectChild(transform,"ext");
                        var bx = frame?.X ?? Number(off,"x")/9525; var by = frame?.Y ?? Number(off,"y")/9525;
                        var bw = frame is { W: > 0 } f ? f.W : Number(size,"cx")/9525; var bh = frame is { H: > 0 } f2 ? f2.H : Number(size,"cy")/9525;
                        var rotation = Number(transform,"rot")/60000;
                        var sideways = Math.IEEERemainder(rotation,360) is var turn && (turn<0?turn+360:turn) is >=45 and <135 or >=225 and <315;
                        if (space is {} s)
                        {
                            // Office resizes a grouped child's frame with the group; captions, line weights and
                            // rotated outlines are not stretched. A child on its side scales on its swapped axes.
                            var cx=(bx+bw/2-s.Ox)*s.Sx; var cy=(by+bh/2-s.Oy)*s.Sy;
                            bw*=sideways?s.Sy:s.Sx; bh*=sideways?s.Sx:s.Sy; bx=cx-bw/2; by=cy-bh/2;
                        }
                        else if (sideways && frame is {} cells && size is not null && Number(size,"cx")/9525 is var ew && Number(size,"cy")/9525 is var eh &&
                            Math.Abs(ew-bh)+Math.Abs(eh-bw) < Math.Abs(ew-bw)+Math.Abs(eh-bh))
                        {
                            // Excel anchors a shape turned onto its side by the cells of its quarter-turned box,
                            // while a:ext keeps the unrotated size: the frame is that box turned back about its centre.
                            (bx,by,bw,bh)=(bx+(bw-bh)/2,by+(bh-bw)/2,bh,bw);
                        }
                        var flipH = transform?.GetAttribute("flipH") is "1" or "true";
                        var flipV = transform?.GetAttribute("flipV") is "1" or "true";
                        var orientation = WorkbookDrawingTransform.Translate(bw/2,bh/2) * WorkbookDrawingTransform.Rotate(rotation) *
                            WorkbookDrawingTransform.Scale(flipH?-1:1,flipV?-1:1) * WorkbookDrawingTransform.Translate(-bw/2,-bh/2);
                        var matrix = parent * WorkbookDrawingTransform.Translate(bx,by) * orientation;
                        var identity = Descendant(element,"cNvPr");
                        var hidden = hiddenParent || identity?.GetAttribute("hidden") is "1" or "true";
                        if (element.LocalName == "grpSp")
                        {
                            var childOff = transform is null ? null : DirectChild(transform,"chOff");
                            var childSize = transform is null ? null : DirectChild(transform,"chExt");
                            var cw = Number(childSize,"cx")/9525; var ch = Number(childSize,"cy")/9525;
                            foreach (var childElement in CompatibleDrawingChildren(element)) Visit(childElement,matrix,null,depth+1,hidden,
                                (cw>0?bw/cw:1,ch>0?bh/ch:1,Number(childOff,"x")/9525,Number(childOff,"y")/9525));
                            return;
                        }
                        var objectId = identity?.GetAttribute("id") ?? objects.Count.ToString(CultureInfo.InvariantCulture);
                        var name = identity?.GetAttribute("name") ?? objectId;
                        var source = new SourceAnchor("xlsx",sheet.PartUri,[new("drawing_part","/"+part),new("shape_id",objectId)]);
                        var kind = element.LocalName == "pic" ? "picture" : element.LocalName == "graphicFrame" ? "chart" : "shape";
                        var preset = properties is null ? null : Descendant(properties,"prstGeom")?.GetAttribute("prst");
                        var line = properties is null ? null : DirectChild(properties,"ln");
                        var textBody = DirectChild(element,"txBody");
                        var text = textBody is null ? "" : string.Join("\n",textBody.ChildNodes.OfType<XmlElement>().Where(e=>e.LocalName=="p")
                            .Select(p=>string.Concat(p.SelectNodes(".//*[local-name()='t' or local-name()='br']")!.OfType<XmlElement>().Select(t=>t.LocalName=="br"?"\n":t.InnerText))));
                        var runStyle = textBody is null ? null : Descendant(textBody,"rPr") ?? Descendant(textBody,"defRPr");
                        var paragraph = textBody is null ? null : Descendant(textBody,"pPr"); var bodyPr = textBody is null ? null : DirectChild(textBody,"bodyPr");
                        double Inset(string name,double fallback)=>bodyPr?.HasAttribute(name)==true?Number(bodyPr,name)/9525:fallback;
                        var geometry = properties is null ? null : DirectChild(properties, "custGeom") ?? DirectChild(properties, "prstGeom");
                        var resolvedGeometry = geometry is null ? null : DrawingMlGeometryReader.Read(geometry, bw, bh);
                        var appearance = DrawingMlStyleReader.Read(element, drawingTheme,
                            linkId => drawingRels.TryGetValue(linkId, out var link) ? DrawingLinkTarget(link) : null, imageResolver: FillPicture);
                        var item = new WorkbookDrawingObject(part+":"+anchorIndex+":"+objectId,kind,bw,bh,matrix)
                        {
                            Preset = preset ?? (properties is not null && DirectChild(properties,"custGeom") is not null ? "custom" : element.LocalName=="cxnSp"?"straightConnector1":"rect"), Source = source, Hidden = hidden, Printable=printable,
                            Text=text, Fill=properties is null || DirectChild(properties,"noFill") is not null ? "none" : DrawingColor(DirectChild(properties,"solidFill"),theme) ?? (element.LocalName=="cxnSp"?"none":"#FFFFFF"),
                            Stroke=line is not null && DirectChild(line,"noFill") is not null ? "none" : DrawingColor(line is null?null:DirectChild(line,"solidFill"),theme) ?? "#000000",
                            StrokeWidth=Number(line,"w") is >0 and var lw?lw/9525:1,
                            Dash=line is null?null:Descendant(line,"prstDash")?.GetAttribute("val"),
                            HeadArrow=line is null?null:Descendant(line,"headEnd")?.GetAttribute("type"), TailArrow=line is null?null:Descendant(line,"tailEnd")?.GetAttribute("type"),
                            FontFamily=runStyle is null?"Calibri":Descendant(runStyle,"latin")?.GetAttribute("typeface") ?? "Calibri",
                            FontSize=(Number(runStyle,"sz") is >0 and var fs?fs*96/7200:14.6667)*
                                (bodyPr is not null && Descendant(bodyPr,"normAutofit") is {} fit && Number(fit,"fontScale") is >0 and var fontScale ? fontScale/100000:1),
                            FontColor=DrawingColor(runStyle is null?null:DirectChild(runStyle,"solidFill"),theme) ?? "#000000",
                            Bold=runStyle?.GetAttribute("b") is "1" or "true", Italic=runStyle?.GetAttribute("i") is "1" or "true",
                            TextAlign=paragraph?.GetAttribute("algn") switch { "l"=>"left","r"=>"right",_=>"center" },
                            VerticalAlign=bodyPr?.GetAttribute("anchor") switch { "t"=>"top","b"=>"bottom",_=>"middle" },
                            TextInsetLeft=Inset("lIns",9.6),TextInsetRight=Inset("rIns",9.6),TextInsetTop=Inset("tIns",4.8),TextInsetBottom=Inset("bIns",4.8),
                            WrapText=bodyPr?.GetAttribute("wrap")!="none",ClipText=bodyPr?.GetAttribute("vertOverflow")=="clip"||bodyPr?.GetAttribute("horzOverflow")=="clip",
                            MoveWithCells=anchor.LocalName!="absoluteAnchor" && anchor.GetAttribute("editAs")!="absolute",
                            SizeWithCells=anchor.LocalName=="twoCellAnchor" && anchor.GetAttribute("editAs") is not ("absolute" or "oneCell"),
                            Adjustments=properties is null?new Dictionary<string,double>():Adjustments(properties),
                            Paths=resolvedGeometry?.Paths ?? [], TextBounds=resolvedGeometry?.TextBounds,
                            FillPaint=appearance.FillPaint, StrokePaint=appearance.StrokePaint, Paragraphs=appearance.Paragraphs, TextLayout=appearance.TextLayout,
                        };
                        if(element.SelectSingleNode(".//*[local-name()='relIds' and namespace-uri()='http://schemas.openxmlformats.org/drawingml/2006/diagram']") is XmlElement diagramIds)
                        {
                            var expanded=CachedSmartArtScene.Expand(diagramIds,item,"/"+part,"xlsx",uri=>package.GetValueOrDefault(uri.TrimStart('/')),drawingTheme,warnings,cancellationToken,pictureFills);
                            if(expanded is not null)
                            {
                                if(expanded.Count>WorkbookDrawingSceneJson.MaxObjects-objects.Count)ReportLimit("drawing object count exceeds "+WorkbookDrawingSceneJson.MaxObjects);
                                objects.AddRange(expanded.Take(WorkbookDrawingSceneJson.MaxObjects-objects.Count));
                            }
                            else Warn("SmartArt needs local Office page rendering: "+objectId);
                            return;
                        }
                        void Warn(string feature) => warnings.Add($"XlsxDrawingPresentationUnsupported: {sheet.Name}: {item.Id}: {feature}; source text, frame and position are retained. Compare with the source.");
                        foreach (var notice in appearance.Warnings) Warn("style: " + notice);
                        item = item with { Dash = appearance.Dash, HeadArrow = appearance.HeadArrow, TailArrow = appearance.TailArrow,
                            HeadArrowWidth = appearance.HeadArrowWidth, HeadArrowLength = appearance.HeadArrowLength,
                            TailArrowWidth = appearance.TailArrowWidth, TailArrowLength = appearance.TailArrowLength };
                        if (appearance.Paragraphs.SelectMany(p => p.Runs).FirstOrDefault() is {} firstRun)
                            item = item with { FontFamily = firstRun.FontFamily, FontSize = firstRun.FontSize, FontColor = firstRun.Color,
                                Bold = firstRun.Bold, Italic = firstRun.Italic };
                        if (appearance.StrokePaint.Kind != "none") item = item with { StrokeWidth = appearance.StrokeWidth };
                        if (appearance.FillPaint.Color is {} fillColor) item = item with { Fill = fillColor };
                        if (appearance.StrokePaint.Color is {} strokeColor) item = item with { Stroke = strokeColor };
                        foreach (var notice in resolvedGeometry?.Warnings ?? []) Warn("geometry: " + notice);
                        if (kind=="picture")
                        {
                            var blip=Descendant(element,"blip"); var refId=blip?.GetAttribute("embed","http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                            var crop=Descendant(element,"srcRect");
                            var image=drawingRels.TryGetValue(refId??"",out var ir) && !ir.IsExternal ? "/"+NormalizePartPath(ir.Target) : null;
                            item=item with { ImageReference=image,SourceNodeId="n_"+Hash($"{sheet.Name}!picture:{part}:{objectId}:{refId}")[..16],
                                Text=identity?.GetAttribute("descr") is {Length:>0} description?description:name,
                                CropLeft=Number(crop,"l")/100000,CropTop=Number(crop,"t")/100000,CropRight=Number(crop,"r")/100000,CropBottom=Number(crop,"b")/100000 };
                        }
                        if (kind=="chart")
                        {
                            var chart=Descendant(element,"chart");
                            var refId=chart?.GetAttribute("id","http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                            item=item with { SourceNodeId="n_"+Hash($"{sheet.Name}!chart:{part}:{objectId}:{refId}")[..16], Text=name };
                            if (drawingRels.TryGetValue(refId ?? "", out var chartRel) && !chartRel.IsExternal &&
                                package.TryGetValue(NormalizePartPath(chartRel.Target), out var chartBytes))
                            {
                                item = item with { Chart = SourceDrawingPackage.Chart(chartBytes, drawingTheme, warnings, item.Id, NormalizePartPath(chartRel.Target),
                                    uri => package.GetValueOrDefault(uri.TrimStart('/')), pictureFills) };
                            }
                        }
                        objects.Add(item);
                    }
                }
            }
            if(objects.Count>0) result["sheet-"+sheet.Name]=new(sheet.Name,objects);
        }
        return result;
    }
    // A shape hyperlink is kept only as an external web or mail address, or as a place in this workbook ("#Sheet!A1").
    private static string? DrawingLinkTarget(PictureRelationship link)
    {
        var target = link.IsExternal ? link.Target : link.Target[(link.Target.LastIndexOf('/') + 1)..];
        if (target.Length is 0 or > 2048 || target.Any(char.IsControl)) return null;
        if (!link.IsExternal) return target.Length > 1 && target[0] == '#' ? target : null;
        return Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" ? target : null;
    }
    private static XmlDocument Load(byte[] bytes)
    { var doc=new XmlDocument{PreserveWhitespace=true}; using var reader=XmlReader.Create(new MemoryStream(bytes),SafeXml);doc.Load(reader);return doc; }
    private static double Number(XmlElement? element,string name)=>double.TryParse(element?.GetAttribute(name),NumberStyles.Float,CultureInfo.InvariantCulture,out var n)&&double.IsFinite(n)?n:0;
    private static string? DrawingColor(XmlElement? fill,string?[] theme)
    {
        var color=fill?.ChildNodes.OfType<XmlElement>().FirstOrDefault();
        if(color is null)return null;
        var value=color.LocalName=="srgbClr"?HexColor(color.GetAttribute("val")):color.LocalName=="sysClr"?HexColor(color.GetAttribute("lastClr")):null;
        if(color.LocalName=="schemeClr")
        {var names=new[]{"lt1","dk1","lt2","dk2","accent1","accent2","accent3","accent4","accent5","accent6","hlink","folHlink"};var ix=Array.IndexOf(names,color.GetAttribute("val"));if(ix>=0)value=theme[ix];}
        return value;
    }
    private static IReadOnlyDictionary<string,double> Adjustments(XmlElement properties)
    {
        var result=new Dictionary<string,double>(StringComparer.Ordinal);
        foreach(var gd in Descendant(properties,"avLst")?.ChildNodes.OfType<XmlElement>()??[])
        {var f=gd.GetAttribute("fmla").Split(' ',StringSplitOptions.RemoveEmptyEntries);if(f.Length==2&&f[0]=="val"&&double.TryParse(f[1],NumberStyles.Float,CultureInfo.InvariantCulture,out var n)&&double.IsFinite(n))result[gd.GetAttribute("name")]=n;}
        return result;
    }
}
