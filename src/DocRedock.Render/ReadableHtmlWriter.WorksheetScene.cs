using System.Globalization;
using DocRedock.Core.Documents;

namespace DocRedock.Render;

public static partial class ReadableHtmlWriter
{
    private sealed partial class HtmlDocumentWriter
    {
        private int _sourceDrawingNumber;
        private int _sourcePrintPageCount;
        private readonly record struct SourcePictureKey(string Reference, double Left, double Top, double Right, double Bottom);
        private readonly Dictionary<SourcePictureKey,(string Html,bool Cropped)> _sceneImages=[];
        private long _croppedPictureInputBytes;
        private readonly Dictionary<string,(int Width,int Height)?> _scenePictureSizes=new(StringComparer.Ordinal);
        private readonly HashSet<string> _sceneWarnings=new(StringComparer.Ordinal);
        private readonly HashSet<string> _sourceChartFallbacks = new(StringComparer.Ordinal);
        private static string Px(double value)=>value.ToString("0.####",CultureInfo.InvariantCulture);
        private static string Matrix(WorkbookDrawingTransform t)=>string.Join(',',new[]{t.A,t.B,t.C,t.D,t.E,t.F}.Select(Px));

        private void WorksheetScene(ReadableWorksheetScene scene,int depth)
        {
            var x=new WorkbookSourceAxis(_sheet,true,scene.IncludeHiddenDimensions);var y=new WorkbookSourceAxis(_sheet,false,scene.IncludeHiddenDimensions);
            var rawX=new WorkbookSourceAxis(_sheet,true,true);var rawY=new WorkbookSourceAxis(_sheet,false,true);
            var objects=scene.Scene.Objects.Select(o=>MapDrawing(o,x,y,rawX,rawY)).ToArray();
            scene=scene with{Scene=scene.Scene with{Objects=objects}};
            var bounds=WorkbookPagePlan.Bounds(scene,x,y);
            _body.Append("<div class=\"source-screen-layout\">");
            if(objects.Length>0)
            {
                _body.Append("<div class=\"source-canvas-scroll\"><div class=\"worksheet-source-canvas\" role=\"group\" aria-label=\"").Append(Enc(scene.Scene.SheetName))
                    .Append("\" style=\"width:").Append(Px(bounds.X2)).Append("px;height:").Append(Px(bounds.Y2)).Append("px\">");
                SourceContents(scene,x,y,(0,bounds.X2,0,bounds.Y2));_body.Append("</div></div>");
                if(scene.Grid is {} grid)
                {
                    _body.Append("<details data-kind=\"worksheet-data\"").Append(_options.ShowFormulas?" open":"").Append("><summary>").Append(_ja?"セルの一覧（検索・絞り込み）":"Cell list (search and filter)").Append("</summary>");
                    var previous=_renderingPrintCopy;_renderingPrintCopy=true;TypedTable(grid,depth);_renderingPrintCopy=previous;_body.Append("</details>");
                }
            }
            else if(scene.Grid is {} table)TypedTable(table,depth);
            _body.Append("</div><div class=\"source-print-pages\">");
            IReadOnlyList<WorkbookPrintPage> pages;
            try{pages=WorkbookPagePlan.Create(scene,_sheet);}
            catch(InvalidOperationException)
            {
                _diagnostics.Add(new("HtmlSourcePaginationLimit","The source print layout exceeds the page bound; source cells are printed as address/value records."));
                if (scene.Grid is {} fallback)
                {
                    _body.Append("<div class=\"source-print-fallback\">");
                    var previous=_renderingPrintCopy;_renderingPrintCopy=true;TypedTable(fallback,depth);_renderingPrintCopy=previous;
                    _body.Append("</div>");
                }
                pages=[];
            }
            var wasPrint=_renderingPrintCopy;_renderingPrintCopy=true;
            for(var p=0;p<pages.Count;p++)
            {
                _typedCancellation.ThrowIfCancellationRequested();var page=pages[p];var print=_sheet?.Print;
                var pageName="drawing-page-"+(++_sourcePrintPageCount).ToString(CultureInfo.InvariantCulture);
                _pageStyles.Append("@page ").Append(pageName).Append("{size:").Append(Px(page.PaperWidth)).Append("px ").Append(Px(page.PaperHeight)).Append("px;margin:0;}\n");
                _body.Append("<div class=\"source-print-page\" data-drr-print-page data-local-page=\"").Append(p+1).Append("\" data-first-page-number=\"")
                    .Append(print?.UseFirstPageNumber==true?print.FirstPageNumber??1:0).Append("\" data-footer-first=\"").Append(print?.HeaderFooter?.DifferentFirst==true?"true":"false")
                    .Append("\" data-footer-even=\"").Append(print?.HeaderFooter?.DifferentOddEven==true?"true":"false").Append("\" style=\"page:").Append(pageName).Append(";width:")
                    .Append(Px(page.PaperWidth)).Append("px;height:").Append(Px(page.PaperHeight-.1)).Append("px\">");
                foreach(var column in page.Columns)foreach(var row in page.Rows)
                {
                    _body.Append("<div class=\"source-print-tile\" style=\"left:").Append(Px(page.Left+column.DestinationStart*page.Scale)).Append("px;top:")
                        .Append(Px(page.Top+row.DestinationStart*page.Scale)).Append("px;width:").Append(Px(Math.Min(column.Length*page.Scale,page.ContentWidth)))
                        .Append("px;height:").Append(Px(Math.Min(row.Length*page.Scale,page.ContentHeight))).Append("px\"><div class=\"worksheet-source-canvas\" style=\"width:")
                        .Append(Px(bounds.X2)).Append("px;height:").Append(Px(bounds.Y2)).Append("px;transform:scale(").Append(Px(page.Scale)).Append(") translate(")
                        .Append(Px(-column.SourceStart)).Append("px,").Append(Px(-row.SourceStart)).Append("px)\">");
                    SourceContents(scene,x,y,(column.SourceStart,column.SourceStart+column.Length,row.SourceStart,row.SourceStart+row.Length));
                    _body.Append("</div></div>");
                }
                PrintFurniture(print?.HeaderFooter,page,p+1,pages.Count);
                _body.Append("</div>");
            }
            _renderingPrintCopy=wasPrint;_body.Append("</div>");
        }
        private static WorkbookDrawingObject MapDrawing(WorkbookDrawingObject item,WorkbookSourceAxis x,WorkbookSourceAxis y,WorkbookSourceAxis rawX,WorkbookSourceAxis rawY)
        {
            if(!item.MoveWithCells)return item;var t=item.Transform;var point=t.Point(0,0);
            var xx=x.Map(point.X,rawX);var yy=y.Map(point.Y,rawY);
            if(!item.SizeWithCells)return item with{Transform=t with{E=xx,F=yy}};
            var right=t.Point(Math.Max(1,item.Width),0);var bottom=t.Point(0,Math.Max(1,item.Height));
            return item with{Transform=new((x.Map(right.X,rawX)-xx)/Math.Max(1,item.Width),(y.Map(right.Y,rawY)-yy)/Math.Max(1,item.Width),
                (x.Map(bottom.X,rawX)-xx)/Math.Max(1,item.Height),(y.Map(bottom.Y,rawY)-yy)/Math.Max(1,item.Height),xx,yy)};
        }
        private void SourceContents(ReadableWorksheetScene scene,WorkbookSourceAxis x,WorkbookSourceAxis y,(double X1,double X2,double Y1,double Y2) clip)
        {
            if((scene.Grid?.SourceGrid??scene.Grid) is {} table)
            {
                var columns=table.Columns.Select((c,i)=>(c.SourceIndex,Index:i)).ToDictionary(c=>c.SourceIndex,c=>c.Index);
                for(var r=0;r<table.Rows.Count;r++)
                {
                    _typedCancellation.ThrowIfCancellationRequested();var row=table.Rows[r];var rowIndex=row.SourceRow??r+1;
                    foreach(var cell in row.Cells)
                    {
                        if(cell.RowSpan<=0||cell.SourceColumn is not {} column||!columns.TryGetValue(column,out var index))continue;
                        var lastCol=table.Columns[Math.Min(table.Columns.Count-1,index+Math.Max(1,cell.ColSpan)-1)].SourceIndex;
                        var lastRow=table.Rows[Math.Min(table.Rows.Count-1,r+Math.Max(1,cell.RowSpan)-1)].SourceRow??rowIndex;
                        var left=x.Start(column);var top=y.Start(rowIndex);var width=x.Start(lastCol+1)-left;var height=y.Start(lastRow+1)-top;
                        if(width<=0||height<=0||left>=clip.X2||left+width<=clip.X1||top>=clip.Y2||top+height<=clip.Y1)continue;
                        var properties=cell.StyleId is {} cellStyle?_document.Styles?.GetValueOrDefault(cellStyle)?.Properties:null;
                        var wrap=properties?.GetValueOrDefault("white-space")=="pre-wrap";
                        var textWidth=width;
                        if(!wrap&&!cell.IsNumeric&&properties?.GetValueOrDefault("text-align") is not ("right" or "center")&&
                            (cell.Content.PlainText.Length>0||cell.Children.Count>0))
                        {
                            var next=row.Cells.FirstOrDefault(c=>c.SourceColumn>lastCol&&
                                (c.Content.PlainText.Length>0||c.Children.Count>0||c.Formula is not null));
                            textWidth=x.Start(next?.SourceColumn??table.Columns[^1].SourceIndex+1)-left;
                        }
                        _body.Append("<div class=\"source-grid-cell");
                        if(cell.StyleId is {} style&&_styleClasses.TryGetValue(style,out var name))_body.Append(' ').Append(name);
                        if(cell.IsNumeric)_body.Append(" numeric-cell");
                        _body.Append('"');
                        if(!_renderingPrintCopy&&_sourceSheetName is {} sheet)_body.Append(" id=\"").Append(WorkbookCellLinks.Id(sheet,rowIndex,column)).Append('"');
                        _body.Append(" data-source-row=\"").Append(rowIndex).Append("\" data-source-column=\"").Append(column).Append('"').Append(OriginAttributes(cell.Origin))
                            .Append(" style=\"left:").Append(Px(left)).Append("px;top:").Append(Px(top)).Append("px;width:").Append(Px(width)).Append("px;height:").Append(Px(height))
                            .Append("px;align-items:").Append(properties?.GetValueOrDefault("vertical-align") switch{"top"=>"flex-start","middle"=>"center",_=>"flex-end"})
                            .Append("\"><div class=\"source-cell-text\" style=\"width:").Append(Px(Math.Max(0,textWidth-4))).Append("px;white-space:").Append(wrap?"pre-wrap":"pre").Append("\">");
                        if(cell.Formula is {HasCachedValue:false})_body.Append("<span class=\"formula-missing\">").Append(_ja?"保存済み計算値なし":"No cached value").Append("</span>");
                        else TypedRuns(cell.SourceContent??cell.Content);
                        if(_options.ShowFormulas && cell.Formula?.Expression is {Length:>0} expression)
                            _body.Append(" <code class=\"source-cell-formula\">").Append(Enc("="+expression.TrimStart('='))).Append("</code>");
                        foreach(var child in cell.Children)if(child is ReadableCode code)_body.Append("<pre><code>").Append(Enc(code.Text)).Append("</code></pre>");
                        _body.Append("</div></div>");
                    }
                }
            }
            foreach(var item in scene.Scene.Objects)
            {
                _typedCancellation.ThrowIfCancellationRequested();if(_renderingPrintCopy&&!item.Printable)continue;
                var corners=new[]{item.Transform.Point(0,0),item.Transform.Point(item.Width,0),item.Transform.Point(0,item.Height),item.Transform.Point(item.Width,item.Height)};
                if(corners.Min(p=>p.X)>clip.X2||corners.Max(p=>p.X)<clip.X1||corners.Min(p=>p.Y)>clip.Y2||corners.Max(p=>p.Y)<clip.Y1)continue;
                SourceDrawing(item);
            }
        }
        private void SourceDrawing(WorkbookDrawingObject item)
        {
            var id="source-drawing-"+(++_sourceDrawingNumber).ToString(CultureInfo.InvariantCulture);
            _body.Append("<div class=\"source-drawing\" data-drawing-id=\"").Append(Enc(item.Id)).Append("\" data-drawing-kind=\"").Append(Enc(item.Kind))
                .Append("\" data-source-part=\"").Append(Enc(item.Source?.PartUri??"")).Append("\" style=\"width:").Append(Px(Math.Max(1,item.Width))).Append("px;height:")
                .Append(Px(Math.Max(1,item.Height))).Append("px;transform:matrix(").Append(Matrix(item.Transform)).Append(")\">");
            if(item.Kind=="picture")
            {
                if(item.ImageReference is {} reference)
                {
                    const string sourceAlt = "{docredock-source-image-instance-alt}";
                    var pictureKey = new SourcePictureKey(reference,item.CropLeft,item.CropTop,item.CropRight,item.CropBottom);
                    if(!_sceneImages.TryGetValue(pictureKey,out var cached))
                    {
                        var cropped = CropSourcePicture(reference,item);
                        var image=new System.Text.StringBuilder();
                        EmitImage(image,cropped is null?reference:"data:image/png;base64,"+Convert.ToBase64String(cropped),sourceAlt,null,null);
                        cached=(image.ToString(),cropped is not null);_sceneImages[pictureKey]=cached;
                    }
                    var html = cached.Html.Replace(sourceAlt, Enc(item.Text), StringComparison.Ordinal);
                    // Every instance has its own positioning; the embedded source bytes are cached once.
                    html=System.Text.RegularExpressions.Regex.Replace(html,@" id=\""[^\""]*\""","",System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));
                    var left=cached.Cropped?0:item.CropLeft;var top=cached.Cropped?0:item.CropTop;
                    var cw=cached.Cropped?1:Math.Max(.001,1-item.CropLeft-item.CropRight);var ch=cached.Cropped?1:Math.Max(.001,1-item.CropTop-item.CropBottom);
                    if(!cached.Cropped&&CropWindow(reference,item) is {} window){left=window.Left;top=window.Top;cw=window.Width;ch=window.Height;}
                    _body.Append("<div class=\"source-picture-clip\"><div style=\"position:absolute;width:").Append(Px(100/cw)).Append("%;height:").Append(Px(100/ch))
                        .Append("%;left:").Append(Px(-100*left/cw)).Append("%;top:").Append(Px(-100*top/ch)).Append("%\">").Append(html).Append("</div></div>");
                }
                else _body.Append("<span class=\"image-placeholder\">").Append(Enc(item.Text)).Append("</span>");
            }
            else if(item.Kind=="chart")
            {
                var chartSvg = item.Chart is {} chart ? DrawingChartSvg.Render(chart, item.Width, item.Height, id + "-chart") : null;
                if (chartSvg is not null) _body.Append(chartSvg);
                else
                {
                _sourceChartFallbacks.Add(item.Id);
                _body.Append("<div class=\"source-chart-fallback\">").Append(Enc(item.Text)).Append("<br>")
                    .Append(_ja?"グラフの外観は未再現です。保存済みデータを下に記載します。":"Chart appearance is unavailable. Saved data follows below.").Append("</div>");
                if(_sceneWarnings.Add(item.Id))_diagnostics.Add(new("HtmlSourceChartFallback","Chart appearance is not reconstructed: "+item.Id+". Its source frame is retained; recognized cached series are listed separately."));
                }
            }
            else
            {
                var paths = item.Paths.Count > 0
                    ? item.Paths.Select(p => (Path: WorkbookDrawingPrimitives.Path(item with { Paths = [p] }), p.Fill, p.Stroke, p.FillMode)).ToArray()
                    : [(WorkbookDrawingPrimitives.Path(item), true, true, "norm")];
                if(paths.Any(p => p.Path is null))
                {
                    if(_sceneWarnings.Add(item.Id))_diagnostics.Add(new("HtmlSourceDrawingUnsupported","Source drawing geometry is unsupported: "+item.Id+" ("+item.Preset+"). Its source position and text are retained."));
                }
                else
                {
                    var stroke=HexColorValue(item.Stroke)?item.Stroke:"none";var fill=HexColorValue(item.Fill)?item.Fill:"none";
                    _body.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(Px(Math.Max(1,item.Width))).Append("\" height=\"").Append(Px(Math.Max(1,item.Height)))
                        .Append("\" aria-hidden=\"true\" overflow=\"visible\"><defs>");
                    // A line end takes the color of the line; a gradient or pattern line gives it its first color.
                    var endColor = item.StrokePaint switch
                    {
                        { Kind: "linear" or "radial", Stops: [var first, ..] } => first.Color,
                        { Kind: "solid" or "pattern", Color: { } own } => own,
                        _ => stroke,
                    };
                    stroke = SourcePaint(item.StrokePaint, stroke, id + "-stroke", item);
                    if (!HexColorValue(endColor)) endColor = HexColorValue(stroke) ? stroke : "#000000";
                    var fills = paths.Select((path,index) => path.Fill
                        ? SourcePaint(item.FillPaint, fill, id + "-fill-" + index, item, path.FillMode) : "none").ToArray();
                    // Office draws line ends on open paths only: a closed outline has no end to put them on.
                    var strokeWidth = Math.Clamp(item.StrokeWidth,0,128); var strokeOpacity = SourcePaintOpacity(item.StrokePaint);
                    var ends = new (string? Head, string? Tail)[paths.Length];
                    for (var pathIndex = 0; pathIndex < paths.Length; pathIndex++)
                    {
                        var path = paths[pathIndex];
                        if (!path.Stroke || stroke == "none" || path.Path!.Contains('Z')) continue;
                        var head = LineEnd(item.HeadArrow, item.HeadArrowWidth, item.HeadArrowLength, strokeWidth, strokeOpacity >= 1);
                        var tail = LineEnd(item.TailArrow, item.TailArrowWidth, item.TailArrowLength, strokeWidth, strokeOpacity >= 1);
                        if (head is null && tail is null) continue;
                        var suffix = ends.Any(e => e.Head is not null || e.Tail is not null) ? "-" + pathIndex.ToString(CultureInfo.InvariantCulture) : "";
                        var trimmed = WorkbookDrawingPrimitives.TrimEnds(path.Path!, head?.Trim ?? 0, tail?.Trim ?? 0, out var headTrim, out var tailTrim);
                        paths[pathIndex] = (trimmed, path.Fill, path.Stroke, path.FillMode);
                        if (head is { } start) Marker(ends[pathIndex].Head = id + "-head" + suffix, start, headTrim, endColor, strokeOpacity, strokeWidth);
                        if (tail is { } end) Marker(ends[pathIndex].Tail = id + "-tail" + suffix, end, tailTrim, endColor, strokeOpacity, strokeWidth);
                    }
                    _body.Append("</defs>");
                    for (var pathIndex = 0; pathIndex < paths.Length; pathIndex++)
                    {
                        var path = paths[pathIndex];
                        _body.Append("<path d=\"").Append(Enc(path.Path!)).Append("\" fill=\"")
                            .Append(fills[pathIndex])
                            .Append("\" stroke=\"").Append(path.Stroke ? stroke : "none").Append("\" stroke-width=\"").Append(Px(strokeWidth)).Append('"');
                        _body.Append(" fill-opacity=\"").Append(Px(SourcePaintOpacity(item.FillPaint))).Append("\" stroke-opacity=\"").Append(Px(strokeOpacity)).Append('"');
                        if(item.Dash is "dash" or "lgDash")_body.Append(" stroke-dasharray=\"6 4\"");else if(item.Dash is "dot" or "sysDot")_body.Append(" stroke-dasharray=\"1 3\"");
                        if(ends[pathIndex].Head is {} headId)_body.Append(" marker-start=\"url(#").Append(headId).Append(")\"");
                        if(ends[pathIndex].Tail is {} tailId)_body.Append(" marker-end=\"url(#").Append(tailId).Append(")\"");
                        _body.Append("/>");
                    }
                    _body.Append("</svg>");
                }
                if(item.Text.Length>0)
                {
                    // A reflected DrawingML frame flips its outline, not the glyphs. The
                    // composed determinant includes parent groups; correcting once also
                    // leaves two reflections as the source's ordinary 180 degree rotation.
                    // The text rectangle itself flips with the outline, so the glyphs are
                    // turned back about that rectangle's own axis, not the frame's.
                    var reflected = item.Transform.A * item.Transform.D - item.Transform.B * item.Transform.C < 0;
                    // A text rectangle without width (a vertical line, a custom a:rect with r = l) has no line length to
                    // wrap in: as with wrap="none", each paragraph is one line, set out from the rectangle by its
                    // alignment, instead of one character per line. Without height (a horizontal line) the text still
                    // wraps in the length and the anchor places it about the line. Insets and clipping need an extent.
                    var horizontalText = item.TextLayout is null || item.TextLayout.Direction == "horz";
                    var flatX = horizontalText && (item.TextBounds?.Width ?? item.Width) < 1;
                    var flatY = horizontalText && (item.TextBounds?.Height ?? item.Height) < 1;
                    if (reflected)
                        _body.Append("<div class=\"source-caption-transform\" style=\"position:absolute;inset:0;transform-origin:0 0;transform:matrix(-1,0,0,1,")
                            .Append(Px(item.TextBounds is {} mirrored ? 2 * mirrored.X + mirrored.Width : item.Width)).Append(",0)\">");
                    _body.Append("<div class=\"source-drawing-text\" style=\"font-family:").Append(Enc(SafeFont(item.FontFamily))).Append(";font-size:")
                    .Append(Px(Math.Clamp(item.FontSize,1,4096))).Append("px;color:").Append(HexColorValue(item.FontColor)?item.FontColor:"#000000")
                    .Append(";font-weight:").Append(item.Bold?"bold":"normal").Append(";font-style:").Append(item.Italic?"italic":"normal").Append(";text-align:")
                    .Append(item.TextAlign is "left" or "right"?item.TextAlign:"center").Append(";justify-content:").Append(item.VerticalAlign=="top"?"flex-start":item.VerticalAlign=="bottom"?"flex-end":"center")
                    .Append(";padding:").Append(Px(flatY?0:Math.Clamp(item.TextInsetTop,0,1e6))).Append("px ").Append(Px(flatX?0:Math.Clamp(item.TextInsetRight,0,1e6))).Append("px ")
                    .Append(Px(flatY?0:Math.Clamp(item.TextInsetBottom,0,1e6))).Append("px ").Append(Px(flatX?0:Math.Clamp(item.TextInsetLeft,0,1e6))).Append("px;white-space:").Append(item.WrapText&&!flatX?"pre-wrap":"pre")
                    .Append(";overflow:").Append(item.ClipText&&!flatX&&!flatY?"hidden":"visible");
                    if (flatX) _body.Append(";align-items:").Append(item.TextAlign=="left"?"flex-start":item.TextAlign=="right"?"flex-end":"center");
                    SourceTextLayout(item,reflected);
                    if (item.TextBounds is {} textBounds)
                        _body.Append(";inset:auto;left:").Append(Px(textBounds.X)).Append("px;top:").Append(Px(textBounds.Y))
                            .Append("px;width:").Append(Px(textBounds.Width)).Append("px;height:").Append(Px(textBounds.Height)).Append("px");
                    _body.Append("\">");
                    if (item.Paragraphs.Count > 0) SourceCaption(item.Paragraphs, flatX || flatY);
                    else _body.Append(Enc(item.Text));
                    _body.Append("</div>");
                    if (reflected) _body.Append("</div>");
                }
            }
            _body.Append("</div>");
        }
        private byte[]? CropSourcePicture(string reference,WorkbookDrawingObject item)
        {
            if(item.CropLeft==0&&item.CropTop==0&&item.CropRight==0&&item.CropBottom==0)return null;
            byte[]? bytes=null;
            if(TryFindAsset(reference,out _,out var asset)&&asset.MediaType.Equals("image/png",StringComparison.OrdinalIgnoreCase)&&
                asset.Content.Length<=_options.MaxImageBytes)bytes=asset.Content.ToArray();
            else if(reference.StartsWith("data:image/png;base64,",StringComparison.OrdinalIgnoreCase)&&
                reference.Length/4L*3<=_options.MaxImageBytes+32)
            {try{bytes=Convert.FromBase64String(reference[(reference.IndexOf(',')+1)..]);}catch(FormatException){}}
            if(bytes is null||bytes.Length>_options.MaxImageBytes||_croppedPictureInputBytes+bytes.Length>_options.MaxTotalImageBytes)return null;
            _croppedPictureInputBytes+=bytes.Length;
            // Crop the immutable source pixels before the browser applies its transforms.
            // Fractional CSS overflow alone can interpolate excluded pixels at PDF tile boundaries.
            return PngSourceCropper.TryCrop(bytes,item.CropLeft,item.CropTop,item.CropRight,item.CropBottom,_typedCancellation);
        }
        /// <summary>The crop of a picture the converter cannot decode and so cannot pre-crop: its window on whole
        /// source pixels, drawn in far enough that the browser's resampling cannot reach the excluded ones.
        /// Null (the plain fractional clip) when nothing is cropped or the pixel size cannot be read.</summary>
        private SourcePictureCropWindow.Window? CropWindow(string reference,WorkbookDrawingObject item)
        {
            if(item.CropLeft==0&&item.CropTop==0&&item.CropRight==0&&item.CropBottom==0)return null;
            if(!_scenePictureSizes.TryGetValue(reference,out var size))
            {
                ReadOnlySpan<byte> bytes=default;
                if(TryFindAsset(reference,out _,out var asset))bytes=asset.Content.Span;
                else if(reference.StartsWith("data:image/",StringComparison.OrdinalIgnoreCase)&&reference.IndexOf(";base64,",StringComparison.OrdinalIgnoreCase) is >0 and var marker&&
                    reference.Length/4L*3<=_options.MaxImageBytes+32)
                {try{bytes=Convert.FromBase64String(reference[(marker+8)..]);}catch(FormatException){}}
                size=SourcePictureCropWindow.TryReadPixelSize(bytes,out var pixelWidth,out var pixelHeight)?(pixelWidth,pixelHeight):null;
                _scenePictureSizes[reference]=size;
            }
            if(size is not {} pixels)return null;
            // The frame's size on the page: the transform carries the scale of the groups around it.
            var frameWidth=Math.Max(1,item.Width)*double.Hypot(item.Transform.A,item.Transform.B);
            var frameHeight=Math.Max(1,item.Height)*double.Hypot(item.Transform.C,item.Transform.D);
            return SourcePictureCropWindow.TryFit(pixels.Width,pixels.Height,item.CropLeft,item.CropTop,item.CropRight,item.CropBottom,frameWidth,frameHeight,out var window)?window:null;
        }
        private void SourceTextLayout(WorkbookDrawingObject item,bool reflected)
        {
            if(item.TextLayout is not {} layout)return;
            _body.Append(";writing-mode:").Append(DrawingTextLayoutCss.WritingMode(layout))
                .Append(";text-orientation:").Append(DrawingTextLayoutCss.TextOrientation(layout));
            if(layout.RightToLeftColumns)_body.Append(";flex-direction:column-reverse");
            var correction=WorkbookDrawingTransform.Identity;
            if(layout.Upright)
            {
                var t=item.Transform*WorkbookDrawingTransform.Scale(reflected?-1:1,1);
                var determinant=t.A*t.D-t.B*t.C;var sx=double.Hypot(t.A,t.B);
                if(Math.Abs(determinant)>1e-12&&sx>1e-12)
                {
                    var sy=Math.Abs(determinant)/sx;
                    correction=new(t.D*sx/determinant,-t.B*sx/determinant,-t.C*sy/determinant,t.A*sy/determinant);
                }
            }
            correction*=WorkbookDrawingTransform.Rotate(layout.RotationDegrees);
            if(correction!=WorkbookDrawingTransform.Identity)
                _body.Append(";transform-origin:50% 50%;transform:matrix(").Append(Matrix(correction)).Append(')');
        }
        private static string ShadeSourceColor(string color, string mode)
        {
            if (!HexColorValue(color) || mode == "norm") return color;
            var amount = mode is "lighten" or "darken" ? .4 : .2;
            var lighten = mode is "lighten" or "lightenLess";
            var channels = Enumerable.Range(0, 3).Select(i => int.Parse(color.AsSpan(1 + 2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            return "#" + string.Concat(channels.Select(value => ((int)Math.Round(lighten ? value + (255 - value) * amount : value * (1 - amount)))
                .ToString("X2", CultureInfo.InvariantCulture)));
        }
        private static string SafeFont(string text)=>"'"+new string(text.Where(c=>char.IsLetterOrDigit(c)||c is ' ' or '-' or '_').Take(100).ToArray())+"',sans-serif";

        private void PrintFurniture(WorkbookHeaderFooter? furniture,WorkbookPrintPage page,int localPage,int localPages)
        {
            if(furniture is null)return;
            var first=furniture.DifferentFirst&&localPage==1;var even=furniture.DifferentOddEven&&localPage%2==0;
            var active=first?"first":even?"even":"odd";
            foreach(var variant in new[]{"odd","even","first"})
            {
                _body.Append("<div class=\"source-print-furniture\" data-furniture-variant=\"").Append(variant).Append('"').Append(variant!=active?" hidden":"").Append('>');
                Furniture(variant=="first"?furniture.FirstHeader:variant=="even"?furniture.EvenHeader:furniture.OddHeader,true);
                Furniture(variant=="first"?furniture.FirstFooter:variant=="even"?furniture.EvenFooter:furniture.OddFooter,false);
                _body.Append("</div>");
            }
            void Furniture(string? text,bool isHeader)
            {
                var margin=_sheet?.Print?.Margins;
                var left=furniture.AlignWithMargins?(margin?.Left??.7)*96:.3*96;var right=furniture.AlignWithMargins?(margin?.Right??.7)*96:.3*96;
                var scale=furniture.ScaleWithDocument?page.Scale:1;
                _body.Append("<div class=\"source-print-").Append(isHeader?"header":"footer").Append("\" style=\"left:").Append(Px(left)).Append("px;right:").Append(Px(right)).Append("px;")
                    .Append(isHeader?"top:":"bottom:").Append(Px((isHeader?margin?.Header??.3:margin?.Footer??.3)*96)).Append("px\">");
                foreach(var section in WorkbookHeaderFooterParser.Parse(text,furniture.ThemeColors))
                {
                    _body.Append("<div class=\"source-footer-").Append(section.Alignment).Append("\">");
                    foreach(var run in section.Runs)
                    {
                        if(run.UnresolvedThemeColor && _sceneWarnings.Add("footer-theme:"+_sheet?.Name))
                            _diagnostics.Add(new("HtmlPrintFooterThemeUnavailable","The source footer theme color could not be resolved for "+_sheet?.Name+"; text and placement are retained with the default color."));
                        _body.Append("<span style=\"font-family:").Append(Enc(SafeFont(run.Font))).Append(";font-size:").Append(Px(run.Size*scale)).Append("pt;color:").Append(run.Color)
                            .Append(";font-weight:").Append(run.Bold?"bold":"normal").Append(";font-style:").Append(run.Italic?"italic":"normal").Append(";text-decoration:")
                            .Append(run.Underline?"underline ":"").Append(run.Strike?"line-through":"").Append(";vertical-align:").Append(run.Superscript?"super":run.Subscript?"sub":"baseline").Append('"');
                        if(run.Field is 'P' or 'N')_body.Append(" data-drr-print-field=\"").Append(run.Field=='P'?"page":"pages").Append("\" data-page-offset=\"").Append(run.PageOffset).Append('"');
                        _body.Append('>').Append(Enc(run.Field switch{'P'=>(localPage+run.PageOffset).ToString(CultureInfo.InvariantCulture),'N'=>(localPages+run.PageOffset).ToString(CultureInfo.InvariantCulture),
                            'A'=>_sheet?.Name??"",'F'=>_document.SourceName??_document.Title,'Z'=>"[source path]",'D'=>DateTime.Now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
                            'T'=>DateTime.Now.ToString("HH:mm",CultureInfo.InvariantCulture),'G'=>"[header/footer image]",_=>run.Text})).Append("</span>");
                        if(run.Field is 'Z' or 'G'&&_sceneWarnings.Add("footer-"+run.Field))_diagnostics.Add(new("HtmlPrintFieldUnavailable",run.Field=='G'?"A header/footer image needs its source image relationship; the image field is marked in the print layout.":"The source workbook path was not supplied; the path field is marked without exposing the conversion machine path."));
                    }
                    _body.Append("</div>");
                }
                _body.Append("</div>");
            }
        }
    }
    private const string WorksheetSceneCss="""
        .source-diagram-scroll{overflow:auto;max-width:100%;background:white;color:#000;border:1px solid #b5bfca;margin:1rem 0}
        .source-diagram-frame{position:relative;overflow:visible;background:white;color:#000;isolation:isolate}
        .source-native-table{z-index:2}.source-native-table .table-scroll{max-height:none;overflow:visible;border:0;margin:0}
        .source-native-table table{width:100%;margin:0}.source-native-table th{position:static}
        .direct-document .source-native-table table{background:transparent}
        .source-scene-background{position:absolute;left:0;top:0;width:100%;height:100%}
        .source-shared-image{background-size:100% 100%;background-repeat:no-repeat}
        .source-canvas-scroll{overflow:auto;max-width:100%;border:1px solid #b5bfca}
        .worksheet-source-canvas{position:relative;background:#fff;color:#000;color-scheme:light;transform-origin:0 0;font:11pt Calibri,Arial,sans-serif;line-height:1.15}
        .source-grid-cell{position:absolute;box-sizing:border-box;overflow:visible;display:flex;white-space:pre;padding:0 2px}
        .source-cell-text{position:relative;z-index:1;flex:none;max-height:100%;overflow:hidden}.source-cell-text pre{margin:0;padding:0;background:transparent;color:inherit;line-height:inherit}
        .source-cell-text code{background:transparent;color:inherit;font-size:inherit}
        .source-grid-cell[style*="vertical-align:top"]{align-items:flex-start}
        .source-drawing{position:absolute;z-index:2;left:0;top:0;transform-origin:0 0;overflow:visible}
        .source-drawing>svg{position:absolute;left:0;top:0;overflow:visible;max-width:none!important}
        .source-drawing-text{position:absolute;inset:0;display:flex;flex-direction:column;box-sizing:border-box;padding:3px 5px;white-space:pre-wrap;overflow:hidden;line-height:1.15}
        .source-picture-clip{position:absolute;inset:0;overflow:hidden}
        .source-chart-fallback{box-sizing:border-box;width:100%;height:100%;border:1px dashed #64748b;background:#fff;color:#111;padding:8px;overflow:hidden}
        .direct-document .source-picture-clip img{display:block;width:100%;height:100%;max-width:none;max-height:none;margin:0;padding:0;border:0;background:transparent}
        .source-print-pages{display:none}
        .source-print-page{position:relative;box-sizing:border-box;background:#fff;color:#000;overflow:hidden}
        .source-print-tile{position:absolute;overflow:hidden}
        .source-print-header,.source-print-footer{position:absolute;display:grid;grid-template-columns:1fr 1fr 1fr;white-space:pre-wrap;line-height:1.15;color:#000}
        .source-footer-left{grid-column:1;grid-row:1;text-align:left}.source-footer-center{grid-column:2;grid-row:1;text-align:center}.source-footer-right{grid-column:3;grid-row:1;text-align:right}
        @media print{
          .source-diagram-scroll{overflow:visible;border:0;break-inside:avoid;print-color-adjust:exact;-webkit-print-color-adjust:exact}
          details[data-kind="source-diagram-description"]{display:none!important}
          .direct-document details[data-kind="source-chart-data"][data-chart-fallback="false"]{display:none!important}
          .direct-document section.source-section.scene-section{margin:0!important;padding:0!important;border:0!important;min-height:0!important;max-width:none!important;break-before:auto!important}
          .direct-document .scene-section>h2,.direct-document .scene-section>h3,.direct-document .scene-section>.source-reference,.direct-document .scene-section>details,.direct-document .source-screen-layout{display:none!important}
          .direct-document .source-print-pages{display:block!important}
          .direct-document .scene-section>details[data-kind="worksheet-chart-data"]{display:block!important;break-before:page}
          .source-print-page{break-before:page;break-inside:avoid;print-color-adjust:exact;-webkit-print-color-adjust:exact}
          .source-print-page.drr-print-first{break-before:auto!important}
          .direct-document .source-print-page details{display:none!important}
          .direct-document .source-print-furniture[hidden]{display:none!important}
          body.direct-document.drr-printing section.source-section[data-drr-print-excluded]{display:none!important}
        }
        """;
}
