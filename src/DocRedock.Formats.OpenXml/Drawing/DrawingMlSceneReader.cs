using System.Globalization;
using System.Xml;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.OpenXml.Drawing;

/// <summary>
/// Reads drawing-local source objects. Worksheet cell anchors and Word page/paragraph placement
/// belong to the calling adapter; a:xfrm is retained as a composed affine transform, never a bbox.
/// </summary>
public static class DrawingMlSceneReader
{
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const double EmuPerPixel = 9525d;
    private const int MaxObjects = 16384, MaxGroupDepth = 32, MaxTreeDepth = 128, MaxWarnings = 128;
    private const double MaxCoordinate = 100_000_000;

    public static IReadOnlyList<WorkbookDrawingObject> Read(XmlElement root, string format, string part,
        XmlDocument? theme, IReadOnlyDictionary<string, string>? relationships = null,
        Func<string, DocumentNode?>? nodeResolver = null, double fallbackWidth = 0, double fallbackHeight = 0,
        ICollection<string>? warnings = null, CancellationToken cancellationToken = default,
        Func<XmlElement,WorkbookDrawingObject,IReadOnlyList<WorkbookDrawingObject>?>? diagramResolver = null,
        (double Sx, double Sy, double Ox, double Oy)? initialChildSpace = null, DrawingMlPictureFills? pictureFills = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentException.ThrowIfNullOrWhiteSpace(part);
        cancellationToken.ThrowIfCancellationRequested();
        var state = new Reader(format, part, theme, relationships, nodeResolver, warnings, cancellationToken,diagramResolver,initialChildSpace) { PictureFills = pictureFills };
        state.Visit(root, WorkbookDrawingTransform.Identity, 0, 0, HiddenInAncestors(root),
            Dimension(fallbackWidth), Dimension(fallbackHeight));
        return state.Objects;
    }

    private sealed class Reader(string format, string part, XmlDocument? theme,
        IReadOnlyDictionary<string, string>? relationships, Func<string, DocumentNode?>? nodeResolver,
        ICollection<string>? warnings, CancellationToken cancellationToken,
        Func<XmlElement,WorkbookDrawingObject,IReadOnlyList<WorkbookDrawingObject>?>? diagramResolver,
        (double Sx, double Sy, double Ox, double Oy)? initialChildSpace)
    {
        public List<WorkbookDrawingObject> Objects { get; } = [];
        private int ordinal, warningCount;
        private bool objectLimitReported, depthLimitReported;
        // Child coordinate space of the enclosing group: x' = (x - Ox) * Sx. Office resizes a grouped
        // child's frame with it; captions, line weights and rotated outlines are never stretched.
        private (double Sx, double Sy, double Ox, double Oy)? childSpace = initialChildSpace, rootSpace = initialChildSpace;
        // The fill an a:grpFill of a shape in the group being read stands for: the nearest group fill that is not itself a:grpFill.
        private XmlElement? groupFill;

        public void Visit(XmlElement element, WorkbookDrawingTransform parent, int groupDepth, int treeDepth,
            bool inheritedHidden, double fallbackWidth, double fallbackHeight)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Objects.Count >= MaxObjects)
            {
                if (!objectLimitReported) { objectLimitReported = true; Warn("limit", "object count exceeds 16384"); }
                return;
            }
            if (treeDepth > MaxTreeDepth || groupDepth > MaxGroupDepth)
            {
                if (!depthLimitReported) { depthLimitReported = true; Warn("limit", "drawing nesting exceeds the supported depth"); }
                return;
            }
            if (element.LocalName == "AlternateContent")
            {
                var branch = Children(element).FirstOrDefault(e => e.LocalName == "Choice") ??
                    Children(element).FirstOrDefault(e => e.LocalName == "Fallback");
                if (branch is not null) Visit(branch, parent, groupDepth, treeDepth + 1, inheritedHidden, fallbackWidth, fallbackHeight);
                return;
            }
            var isGroup = element.LocalName is "grpSp" or "spTree" or "wgp";
            var isObject = element.LocalName is "sp" or "cxnSp" or "wsp" or "pic" or "graphicFrame" ||
                element.LocalName == "chart" && element.NamespaceURI == "http://schemas.openxmlformats.org/drawingml/2006/chart";
            if (!isGroup && !isObject)
            {
                // A Word drawing wrapper can supply its frame when the inner shape has no ext.
                var extent = Child(element, "extent");
                var fw = extent is null ? fallbackWidth : Number(extent, "cx", fallbackWidth * EmuPerPixel, "frame") / EmuPerPixel;
                var fh = extent is null ? fallbackHeight : Number(extent, "cy", fallbackHeight * EmuPerPixel, "frame") / EmuPerPixel;
                foreach (var child in Children(element))
                    Visit(child, parent, groupDepth, treeDepth + 1, inheritedHidden || HiddenInAncestors(element), fw, fh);
                return;
            }
            // Only the frame travels through this reader. The existing table adapter still
            // owns merged cells, rich content and policy selection.
            var isTable = element.LocalName == "graphicFrame" && Descendant(element, "tbl") is not null;
            var diagram = Descendant(element,"relIds","http://schemas.openxmlformats.org/drawingml/2006/diagram");

            var identity = Identity(element);
            var shapeId = identity?.GetAttribute("id");
            if (string.IsNullOrWhiteSpace(shapeId)) shapeId = "source-" + (ordinal + 1).ToString(CultureInfo.InvariantCulture);
            if (element.LocalName == "graphicFrame" && !isTable && Descendant(element, "chart") is null && diagram is null)
            {
                Warn(shapeId, "unsupported graphicFrame content; the source adapter's diagram fallback is retained");
                return;
            }
            var node = isGroup ? null : nodeResolver?.Invoke(shapeId);
            cancellationToken.ThrowIfCancellationRequested();
            var hidden = inheritedHidden || HiddenInAncestors(element) ||
                On(identity, "hidden") || node?.Layer == ContentLayer.Hidden;
            var properties = Child(element, isGroup ? "grpSpPr" : "spPr");
            var inherited = isGroup ? null : Inheritance?.Invoke(element);
            var xfrm = Child(properties, "xfrm") ?? Child(element, "xfrm") ?? inherited?.Frame;
            var off = Child(xfrm, "off"); var ext = Child(xfrm, "ext");
            var x = Number(off, "x", 0, shapeId) / EmuPerPixel;
            var y = Number(off, "y", 0, shapeId) / EmuPerPixel;
            var width = ext is null ? fallbackWidth : Number(ext, "cx", fallbackWidth * EmuPerPixel, shapeId) / EmuPerPixel;
            var height = ext is null ? fallbackHeight : Number(ext, "cy", fallbackHeight * EmuPerPixel, shapeId) / EmuPerPixel;
            if (isGroup && element.LocalName == "spTree" && ext is null) width = height = 0;
            var angle = Math.IEEERemainder(Number(xfrm, "rot", 0, shapeId) / 60000d, 360);
            if (childSpace is { } space)
            {
                // A child turned onto its side takes the group's scale on its swapped axes.
                var sideways = (angle < 0 ? angle + 360 : angle) is >= 45 and < 135 or >= 225 and < 315;
                var centerX = (x + width / 2 - space.Ox) * space.Sx; var centerY = (y + height / 2 - space.Oy) * space.Sy;
                width *= sideways ? space.Sy : space.Sx; height *= sideways ? space.Sx : space.Sy;
                x = centerX - width / 2; y = centerY - height / 2;
            }
            if (!IsDimension(width) || !IsDimension(height) || !IsPosition(x) || !IsPosition(y))
            {
                Warn(shapeId, "invalid source frame; finite bounded fallback frame retained");
                x = IsPosition(x) ? x : 0; y = IsPosition(y) ? y : 0;
                width = IsDimension(width) ? width : Dimension(fallbackWidth);
                height = IsDimension(height) ? height : Dimension(fallbackHeight);
            }
            var orientation = WorkbookDrawingTransform.Translate(width / 2, height / 2) *
                WorkbookDrawingTransform.Rotate(angle) *
                WorkbookDrawingTransform.Scale(On(xfrm, "flipH") ? -1 : 1, On(xfrm, "flipV") ? -1 : 1) *
                WorkbookDrawingTransform.Translate(-width / 2, -height / 2);
            var matrix = parent * WorkbookDrawingTransform.Translate(x, y) * orientation;
            if (!Finite(matrix))
            {
                Warn(shapeId, "non-finite or excessive composed transform; object frame retained at parent origin");
                matrix = parent;
            }
            if (isGroup)
            {
                var childOff = Child(xfrm, "chOff"); var childExt = Child(xfrm, "chExt");
                var cw = Number(childExt, "cx", width * EmuPerPixel, shapeId) / EmuPerPixel;
                var ch = Number(childExt, "cy", height * EmuPerPixel, shapeId) / EmuPerPixel;
                // Without a child extent the group's own frame is its child space, except for a tree resized from outside.
                var sx = childExt is null && treeDepth == 0 && rootSpace is { } initial ? initial.Sx : cw > 0 ? width / cw : 1d;
                var sy = childExt is null && treeDepth == 0 && rootSpace is { } initial2 ? initial2.Sy : ch > 0 ? height / ch : 1d;
                if (childExt is not null && (cw <= 0 && width != 0 || ch <= 0 && height != 0))
                    Warn(shapeId, "invalid group child extent; child coordinates retained without that scale");
                var outerSpace = childSpace; var outerFill = groupFill;
                childSpace = (sx, sy, Number(childOff, "x", 0, shapeId) / EmuPerPixel, Number(childOff, "y", 0, shapeId) / EmuPerPixel);
                var ownFill = Children(properties).FirstOrDefault(e => e.LocalName is "noFill" or "solidFill" or "gradFill" or "blipFill" or "pattFill" or "grpFill");
                if (ownFill?.LocalName != "grpFill") groupFill = ownFill ?? NoFill(element);
                foreach (var child in Children(element))
                    Visit(child, matrix, groupDepth + 1, treeDepth + 1, hidden, 0, 0);
                childSpace = outerSpace; groupFill = outerFill;
                return;
            }

            ordinal++;
            var objectId = part + ":" + ordinal.ToString(CultureInfo.InvariantCulture) + ":" + shapeId;
            var source = new SourceAnchor(format, part, [new AnchorLocator("shape_id", shapeId)], ordinal);
            if(diagram is not null)
            {
                var expanded=diagramResolver?.Invoke(diagram,new(objectId,"shape",width,height,matrix){Source=source,SourceNodeId=node?.Id,Hidden=hidden});
                if(expanded is null||expanded.Count==0)Warn(shapeId,"unsupported graphicFrame SmartArt has no usable saved drawing; a local Office renderer is needed for source page layout.");
                else
                {
                    if(expanded.Count>MaxObjects-Objects.Count&&!objectLimitReported){objectLimitReported=true;Warn("limit","object count exceeds 16384");}
                    foreach(var child in expanded.Take(MaxObjects-Objects.Count))Objects.Add(child);
                }
                return;
            }
            var kind = isTable ? "table" : element.LocalName == "pic" ? "picture" : element.LocalName is "graphicFrame" or "chart" ? "chart" : "shape";
            var pureTextBox = Descendant(element, "txbxContent", W) is not null ||
                On(Child(Child(element, "nvSpPr"), "cNvSpPr"), "txBox") || On(Child(element, "cNvSpPr"), "txBox");
            var styleShape = NormalizeWordText(element, hidden, cancellationToken, out var linearizedTable);
            NormalizeCachedFields(styleShape);
            // Inherited values complete the working copy only; the source element stays as saved.
            if (inherited is not null) { inherited.ApplyTo(styleShape); properties = Child(styleShape, "spPr") ?? properties; }
            if (linearizedTable) Warn(objectId, "Word textbox table text is linearized; the native table remains adapter-owned");
            var geometry = Child(properties, "custGeom") ?? Child(properties, "prstGeom");
            if (geometry is null)
            {
                geometry = RectangleGeometry();
                if (pureTextBox) EnsureDefaultTextBoxNoFill(styleShape);
                else if (kind == "shape") Warn(objectId, "missing geometry; rectangular source frame used as fallback");
            }
            // The working copy has no parents: the group fill and the package behind a picture fill are handed over.
            string? FillPicture(string relationshipId, out string? reason)
            {
                reason = "its relationship is missing or leaves the package";
                return ResolveRelationship(relationshipId, objectId) is { } pictureUri ? PictureFills!.Reference(pictureUri, out reason) : null;
            }
            DrawingAppearance Appearance() => DrawingMlStyleReader.Read(styleShape, theme, inheritedListStyle: inherited?.ListStyle,
                groupFill: groupFill ?? NoFill(element), imageResolver: PictureFills is null ? null : FillPicture);
            var appearance = Appearance();
            cancellationToken.ThrowIfCancellationRequested();
            var paragraphs = appearance.Paragraphs;
            var text = PlainText(paragraphs);
            // The resolved source node is authoritative for Word hidden-style/revision filtering
            // which this drawing-only helper cannot evaluate without the Word styles/policy model.
            if (format.Equals("docx", StringComparison.OrdinalIgnoreCase) && !hidden && node?.Kind == NodeKind.TextBox &&
                NodeText(node) is { } authoritative && authoritative != text)
            {
                ReplaceTextBodyWithPlainText(styleShape, authoritative);
                appearance = Appearance();
                paragraphs = appearance.Paragraphs; text = PlainText(paragraphs);
                Warn(objectId, "Word caption follows the resolved source-node content policy; unmatched rich formatting uses fallback");
            }
            foreach (var warning in appearance.Warnings) Warn(objectId, "style: " + warning);
            paragraphs = DecorateLinks(styleShape, paragraphs, identity, objectId);
            text = PlainText(paragraphs);
            var shapeGeometry = kind == "shape" ? DrawingMlGeometryReader.Read(geometry, width, height) : null;
            cancellationToken.ThrowIfCancellationRequested();
            if (shapeGeometry is not null)
                foreach (var warning in shapeGeometry.Warnings) Warn(objectId, "geometry: " + warning);
            var firstRun = paragraphs.SelectMany(p => p.Runs).FirstOrDefault();
            var body = Child(styleShape, "txBody");
            var bodyPr = Child(body, "bodyPr");
            var pPr = Child(Children(body).FirstOrDefault(e => e.LocalName == "p"), "pPr");
            var line = Child(properties, "ln");
            double Inset(string name, double fallback) => bodyPr?.HasAttribute(name) == true
                ? Math.Clamp(Number(bodyPr, name, fallback * EmuPerPixel, objectId) / EmuPerPixel, 0, MaxCoordinate) : fallback;
            var item = new WorkbookDrawingObject(objectId, kind, width, height, matrix)
            {
                Source = source, SourceNodeId = node?.Id, Hidden = hidden,
                Printable = !AncestorsAndSelf(element).Any(e => Child(e, "clientData") is { } cd && Off(cd, "fPrintsWithSheet")),
                Preset = geometry.GetAttribute("prst") is { Length: > 0 } preset ? preset : "custom",
                Text = text, Paragraphs = paragraphs,
                FillPaint = appearance.FillPaint, StrokePaint = appearance.StrokePaint, StrokeWidth = appearance.StrokeWidth,
                Fill = appearance.FillPaint.Kind == "solid" ? appearance.FillPaint.Color ?? "none" : "none",
                Stroke = appearance.StrokePaint.Kind == "solid" ? appearance.StrokePaint.Color ?? "none" : "none",
                FontFamily = firstRun?.FontFamily ?? "Calibri", FontSize = firstRun?.FontSize ?? 14.6667,
                FontColor = firstRun?.Color ?? "#000000", Bold = firstRun?.Bold == true, Italic = firstRun?.Italic == true,
                TextAlign = (pPr?.GetAttribute("algn") is { Length: > 0 } ownAlignment ? ownAlignment : paragraphs.FirstOrDefault()?.Alignment) switch { "r" => "right", "ctr" => "center", "just" => "justify", _ => "left" },
                VerticalAlign = bodyPr?.GetAttribute("anchor") switch { "b" => "bottom", "ctr" => "middle", _ => "top" },
                TextInsetLeft = Inset("lIns", 9.6), TextInsetRight = Inset("rIns", 9.6),
                TextInsetTop = Inset("tIns", 4.8), TextInsetBottom = Inset("bIns", 4.8),
                WrapText = bodyPr?.GetAttribute("wrap") != "none",
                ClipText = bodyPr?.GetAttribute("vertOverflow") == "clip" || bodyPr?.GetAttribute("horzOverflow") == "clip",
                Paths = shapeGeometry?.Paths ?? [], TextBounds = shapeGeometry?.TextBounds, TextLayout = appearance.TextLayout,
                Dash = appearance.Dash,
                HeadArrow = appearance.HeadArrow,
                TailArrow = appearance.TailArrow,
                HeadArrowWidth = appearance.HeadArrowWidth, HeadArrowLength = appearance.HeadArrowLength,
                TailArrowWidth = appearance.TailArrowWidth, TailArrowLength = appearance.TailArrowLength,
            };
            if (kind == "picture")
            {
                var blip = Descendant(element, "blip");
                var relationshipId = blip?.GetAttribute("embed", R);
                var reference = ResolveRelationship(relationshipId, objectId);
                var crop = Descendant(element, "srcRect");
                item = item with
                {
                    ImageReference = reference,
                    Text = identity?.GetAttribute("descr") is { Length: > 0 } alt ? alt :
                        identity?.GetAttribute("name") is { Length: > 0 } name ? name : "Image",
                    CropLeft = Number(crop, "l", 0, objectId) / 100000,
                    CropTop = Number(crop, "t", 0, objectId) / 100000,
                    CropRight = Number(crop, "r", 0, objectId) / 100000,
                    CropBottom = Number(crop, "b", 0, objectId) / 100000,
                };
                if (node is null) Warn(objectId, "picture has no resolved source image node; its source reference and frame remain");
                if (!string.IsNullOrWhiteSpace(relationshipId))
                    item = item with { Source = source with { Locators = [.. source.Locators, new("image_relationship", relationshipId)] } };
            }
            if (kind == "chart")
            {
                var chartId = Descendant(element, "chart")?.GetAttribute("id", R);
                var chartPart = ResolveRelationship(chartId, objectId);
                item = item with
                {
                    Text = NodeText(node) ?? identity?.GetAttribute("name") ?? "Chart",
                    Source = source with { Locators = [.. source.Locators,
                        new("chart_relationship", chartId ?? ""), new("chart_part", chartPart ?? "")] },
                };
                if (node is null)
                    Warn(objectId, "chart has no resolved source node; the adapter must attach its typed Chart or cached-data fallback");
            }
            cancellationToken.ThrowIfCancellationRequested();
            Objects.Add(item);
        }

        /// <summary>Reads the picture of a picture fill from the package, or null to report picture fills as unsupported.</summary>
        public DrawingMlPictureFills? PictureFills { get; init; }
        /// <summary>What a shape inherits from outside the tree being read (a placeholder's layout and master), or null.</summary>
        public Func<XmlElement, DrawingMlInheritedShape?>? Inheritance { get; init; }
        /// <summary>Maps a PowerPoint action (a:hlinkClick action and r:id) to a link target the caller has vetted, or null to leave the label plain.</summary>
        public Func<string, string?, string?>? ActionLinks { get; init; }

        private string? ResolveRelationship(string? id, string objectId)
        {
            if (string.IsNullOrWhiteSpace(id) || relationships is null || !relationships.TryGetValue(id, out var target))
            { Warn(objectId, "missing internal drawing relationship"); return null; }
            var normalized = NormalizeTarget(part, target);
            if (normalized is null) Warn(objectId, "external or invalid package relationship was not followed");
            return normalized;
        }

        private IReadOnlyList<DrawingTextParagraph> DecorateLinks(XmlElement shape,
            IReadOnlyList<DrawingTextParagraph> paragraphs, XmlElement? identity, string objectId)
        {
            var sourceParagraphs = Children(Child(shape, "txBody")).Where(p => p.LocalName == "p").ToArray();
            var shapeLink = ResolveLink(Child(identity, "hlinkClick"), objectId);
            var result = paragraphs.ToArray();
            for (var p = 0; p < Math.Min(sourceParagraphs.Length, result.Length); p++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var spans = new List<(int Start, int End, string Target)>();
                var sourceText = new System.Text.StringBuilder();
                foreach (var child in Children(sourceParagraphs[p]))
                {
                    var text = child.LocalName == "br" ? "\n" :
                        child.LocalName is "r" or "fld" ? Child(child, "t")?.InnerText ?? "" : "";
                    var start = sourceText.Length; sourceText.Append(text);
                    var explicitLink = Child(Child(child, "rPr"), "hlinkClick");
                    var target = explicitLink is null ? shapeLink : ResolveLink(explicitLink, objectId);
                    if (target is not null && text.Length > 0) spans.Add((start, start + text.Length, target));
                }
                if (spans.Count == 0) continue;
                if (sourceText.ToString() != string.Concat(result[p].Runs.Select(r => r.Text)))
                {
                    Warn(objectId, "hyperlink run offsets could not be aligned to the source paragraph; labels remain plain text");
                    continue;
                }
                // Positional split is linear in source runs plus hyperlink spans. It does not
                // match strings across shapes or infer the owner from repeated caption text.
                var output = new List<DrawingTextRun>(); var position = 0; var spanIndex = 0;
                foreach (var run in result[p].Runs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var local = 0;
                    while (local < run.Text.Length)
                    {
                        while (spanIndex < spans.Count && spans[spanIndex].End <= position) spanIndex++;
                        var end = position + run.Text.Length - local;
                        string? target = null;
                        if (spanIndex < spans.Count)
                        {
                            var span = spans[spanIndex];
                            if (position < span.Start) end = Math.Min(end, span.Start);
                            else { target = span.Target; end = Math.Min(end, span.End); }
                        }
                        var count = end - position;
                        output.Add(run with { Text = run.Text.Substring(local, count), LinkTarget = run.LinkTarget ?? target });
                        local += count; position += count;
                    }
                }
                result[p] = result[p] with { Runs = output };
            }
            return result;
        }

        private string? ResolveLink(XmlElement? hyperlink, string objectId)
        {
            if (hyperlink is null) return null;
            var id = hyperlink.GetAttribute("id", R);
            // A PowerPoint action other than opening the relationship's file has no URL: the caller names the
            // slide a jump leads to, and a macro, program, OLE verb, custom show or end of show stays a label.
            if (hyperlink.GetAttribute("action") is { } action && action.StartsWith("ppaction://", StringComparison.OrdinalIgnoreCase) &&
                !action.StartsWith("ppaction://hlinkfile", StringComparison.OrdinalIgnoreCase) && !action.StartsWith("ppaction://hlinkpres", StringComparison.OrdinalIgnoreCase))
            {
                var jump = ActionLinks?.Invoke(action, id.Length == 0 ? null : id);
                if (jump is null) Warn(objectId, "presentation action has no destination in the document; its label remains");
                return jump;
            }
            if (id.Length == 0 || relationships is null || !relationships.TryGetValue(id, out var target))
            { Warn(objectId, "source hyperlink relationship could not be resolved; its label remains"); return null; }
            if (target.Any(char.IsControl) || !Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https" or "mailto"))
            { Warn(objectId, "unsupported hyperlink target was not activated; its label remains"); return null; }
            return target;
        }

        private double Number(XmlElement? element, string name, double fallback, string objectId)
        {
            if (element is null || !element.HasAttribute(name)) return fallback;
            if (double.TryParse(element.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                double.IsFinite(value) && Math.Abs(value) <= 1e12) return value;
            Warn(objectId, "invalid numeric drawing attribute " + name + "; bounded fallback used");
            return fallback;
        }
        private void Warn(string objectId, string reason)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (warnings is null) return;
            if (warningCount++ < MaxWarnings)
                warnings.Add($"SourceDrawingScenePartial: {format}:{part}:{objectId}: {reason}");
            else if (warningCount == MaxWarnings + 1)
                warnings.Add($"SourceDrawingScenePartial: {format}:{part}: further drawing warnings were bounded.");
        }
    }

    /// <summary>
    /// As <see cref="Read"/>, for a tree whose shapes inherit from other parts: <paramref name="inheritance"/>
    /// returns, for a shape element of the tree, the frame, shape properties and text defaults it takes
    /// from outside its own element (a PowerPoint placeholder's layout and master), or null.
    /// </summary>
    public static IReadOnlyList<WorkbookDrawingObject> ReadInherited(XmlElement root, Func<XmlElement, DrawingMlInheritedShape?> inheritance,
        string format, string part, XmlDocument? theme, IReadOnlyDictionary<string, string>? relationships = null,
        Func<string, DocumentNode?>? nodeResolver = null, ICollection<string>? warnings = null, CancellationToken cancellationToken = default,
        Func<XmlElement,WorkbookDrawingObject,IReadOnlyList<WorkbookDrawingObject>?>? diagramResolver = null,
        Func<string, string?, string?>? actionLinkResolver = null, DrawingMlPictureFills? pictureFills = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(inheritance);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentException.ThrowIfNullOrWhiteSpace(part);
        cancellationToken.ThrowIfCancellationRequested();
        var state = new Reader(format, part, theme, relationships, nodeResolver, warnings, cancellationToken, diagramResolver, initialChildSpace: null) { Inheritance = inheritance, ActionLinks = actionLinkResolver, PictureFills = pictureFills };
        state.Visit(root, WorkbookDrawingTransform.Identity, 0, 0, HiddenInAncestors(root), 0, 0);
        return state.Objects;
    }

    private static IReadOnlyList<XmlElement> Children(XmlElement? element) => element?.ChildNodes.OfType<XmlElement>().ToArray() ?? [];
    private static XmlElement? Child(XmlElement? element, string localName) => Children(element).FirstOrDefault(e => e.LocalName == localName);
    private static XmlElement? Descendant(XmlElement? element, string localName, string? ns = null)
    {
        if (element is null) return null;
        var pending = new Stack<(XmlElement Element, int Depth)>(); pending.Push((element, 0));
        while (pending.TryPop(out var next))
        {
            if (next.Element.LocalName == localName && (ns is null || next.Element.NamespaceURI == ns)) return next.Element;
            if (next.Depth >= MaxTreeDepth) continue;
            foreach (var child in Children(next.Element).Reverse()) pending.Push((child, next.Depth + 1));
        }
        return null;
    }
    private static XmlElement? Identity(XmlElement element)
    {
        var direct = Child(element, "cNvPr") ?? Children(element).Where(e => e.LocalName.StartsWith("nv", StringComparison.Ordinal))
            .Select(e => Child(e, "cNvPr")).FirstOrDefault(e => e is not null);
        if (direct is not null) return direct;
        var wordFrame = AncestorsAndSelf(element).FirstOrDefault(e => e.LocalName is "inline" or "anchor" &&
            e.NamespaceURI == "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing");
        return Child(wordFrame, "docPr");
    }
    private static IEnumerable<XmlElement> AncestorsAndSelf(XmlElement element)
    {
        for (XmlNode? node = element; node is XmlElement current; node = node.ParentNode) yield return current;
    }
    private static bool HiddenInAncestors(XmlElement element) => AncestorsAndSelf(element).Any(e =>
        e.LocalName == "r" && e.NamespaceURI == W && Child(e, "rPr") is { } pr &&
        (WordOn(Child(pr, "vanish")) || WordOn(Child(pr, "webHidden"))));
    private static bool WordOn(XmlElement? element) => element is not null && element.GetAttribute("val", W) is not ("0" or "false" or "off");
    private static bool On(XmlElement? element, string name) => element?.GetAttribute(name) is "1" or "true";
    private static bool Off(XmlElement? element, string name) => element?.GetAttribute(name) is "0" or "false";
    private static bool IsPosition(double value) => double.IsFinite(value) && Math.Abs(value) <= MaxCoordinate;
    private static bool IsDimension(double value) => double.IsFinite(value) && value is >= 0 and <= MaxCoordinate;
    private static double Dimension(double value) => IsDimension(value) ? value : 0;
    private static bool Finite(WorkbookDrawingTransform t) =>
        new[] { t.A, t.B, t.C, t.D, t.E, t.F }.All(v => double.IsFinite(v) && Math.Abs(v) <= MaxCoordinate);
    private static string PlainText(IReadOnlyList<DrawingTextParagraph> paragraphs) =>
        string.Join("\n", paragraphs.Select(p => string.Concat(p.Runs.Select(r => r.Text))));
    private static string? NodeText(DocumentNode? node) => node?.Content switch
    {
        TextNodeContent text => text.Text,
        RichTextNodeContent rich => string.Concat(rich.Runs.Select(r => r.Text)),
        ReferenceNodeContent reference => reference.AltText,
        _ => null,
    };
    /// <summary>An a:noFill for a shape whose a:grpFill finds no group fill: Office leaves such a shape unfilled.</summary>
    private static XmlElement NoFill(XmlElement owner) => owner.OwnerDocument!.CreateElement("a", "noFill", A);
    private static XmlElement RectangleGeometry()
    {
        var document = new XmlDocument { XmlResolver = null };
        document.LoadXml($"<a:prstGeom xmlns:a=\"{A}\" prst=\"rect\"><a:avLst/></a:prstGeom>");
        return document.DocumentElement!;
    }
    private static string? NormalizeTarget(string sourcePart, string target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.IndexOfAny(['?', '#', '\0']) >= 0 ||
            Uri.TryCreate(target, UriKind.Absolute, out var absolute) && absolute.IsAbsoluteUri && !target.StartsWith('/')) return null;
        string decoded;
        try { decoded = Uri.UnescapeDataString(target).Replace('\\', '/'); }
        catch (UriFormatException) { return null; }
        if (decoded.Any(char.IsControl) || decoded.Contains(':', StringComparison.Ordinal) ||
            decoded.StartsWith("//", StringComparison.Ordinal)) return null;
        var basePart = sourcePart.Replace('\\', '/').TrimStart('/');
        var slash = basePart.LastIndexOf('/');
        var path = decoded.StartsWith('/') ? decoded : (slash >= 0 ? basePart[..(slash + 1)] : "") + decoded;
        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        return parts.Count == 0 ? null : "/" + string.Join("/", parts);
    }

    private static XmlElement NormalizeWordText(XmlElement original, bool wholeObjectHidden,
        CancellationToken cancellationToken, out bool hasTable)
    {
        var clone = (XmlElement)original.CloneNode(true);
        hasTable = false;
        var box = Descendant(original, "txbxContent", W);
        if (box is null || Child(clone, "txBody") is not null) return clone;
        hasTable = Descendant(box, "tbl", W) is not null;
        var document = clone.OwnerDocument!;
        var body = document.CreateElement("a", "txBody", A);
        var bodyProperties = document.CreateElement("a", "bodyPr", A);
        if (Child(original, "bodyPr") is {} sourceBody)
        {
            foreach (XmlAttribute attribute in sourceBody.Attributes)
                if (attribute.NamespaceURI.Length == 0) bodyProperties.SetAttribute(attribute.LocalName, attribute.Value);
            foreach (XmlNode child in sourceBody.ChildNodes) bodyProperties.AppendChild(child.CloneNode(true));
        }
        body.AppendChild(bodyProperties);
        body.AppendChild(document.CreateElement("a", "lstStyle", A));
        foreach (var paragraph in WordParagraphs(box))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = document.CreateElement("a", "p", A);
            var sourcePPr = Child(paragraph, "pPr");
            var pPr = document.CreateElement("a", "pPr", A);
            if (Child(sourcePPr, "jc") is { } jc)
                pPr.SetAttribute("algn", jc.GetAttribute("val", W) switch { "center" => "ctr", "right" or "end" => "r", "both" or "distribute" => "just", _ => "l" });
            ConvertWordParagraphLayout(document, sourcePPr, pPr);
            if (Child(sourcePPr, "rPr") is { } defaultRun)
                pPr.AppendChild(ConvertWordRunProperties(document, defaultRun, "defRPr"));
            p.AppendChild(pPr);
            foreach (var run in WordRuns(paragraph))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!wholeObjectHidden && HiddenInAncestors(run)) continue;
                var properties = Child(run, "rPr");
                foreach (var token in Children(run))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (token.NamespaceURI != W) continue;
                    if (token.LocalName is "br" or "cr")
                    { p.AppendChild(document.CreateElement("a", "br", A)); continue; }
                    if (token.LocalName is not ("t" or "tab")) continue;
                    var r = document.CreateElement("a", "r", A);
                    var runProperties = properties is null ? document.CreateElement("a", "rPr", A) :
                        ConvertWordRunProperties(document, properties, "rPr");
                    var link = AncestorsAndSelf(run).Skip(1).TakeWhile(e => e != paragraph)
                        .FirstOrDefault(e => e.LocalName == "hyperlink" && e.NamespaceURI == W)?.GetAttribute("id", R);
                    if (!string.IsNullOrEmpty(link))
                    {
                        var click = document.CreateElement("a", "hlinkClick", A);
                        var id = document.CreateAttribute("r", "id", R); id.Value = link; click.Attributes.Append(id);
                        runProperties.AppendChild(click);
                    }
                    if (runProperties.HasAttributes || runProperties.HasChildNodes) r.AppendChild(runProperties);
                    var t = document.CreateElement("a", "t", A);
                    t.SetAttribute("space", "http://www.w3.org/XML/1998/namespace", "preserve");
                    t.InnerText = token.LocalName == "tab" ? "\t" : token.InnerText;
                    r.AppendChild(t); p.AppendChild(r);
                }
            }
            body.AppendChild(p);
        }
        clone.AppendChild(body);
        return clone;
    }
    private static void NormalizeCachedFields(XmlElement shape)
    {
        var body = Child(shape, "txBody");
        if (body is null) return;
        var document = shape.OwnerDocument!;
        foreach (var p in Children(body).Where(e => e.LocalName == "p"))
            foreach (var field in Children(p).Where(e => e.LocalName == "fld"))
            {
                var run = document.CreateElement("a", "r", A);
                if (Child(field, "rPr") is { } pr) run.AppendChild(pr.CloneNode(true));
                if (Child(field, "t") is { } text) run.AppendChild(text.CloneNode(true));
                p.ReplaceChild(run, field);
            }
    }
    private static IEnumerable<XmlElement> WordParagraphs(XmlElement box)
    {
        foreach (XmlElement p in box.GetElementsByTagName("p", W))
            if (AncestorsAndSelf(p).Skip(1).FirstOrDefault(e => e.LocalName == "txbxContent" && e.NamespaceURI == W) == box) yield return p;
    }
    private static IEnumerable<XmlElement> WordRuns(XmlElement paragraph)
    {
        foreach (XmlElement run in paragraph.GetElementsByTagName("r", W))
            if (AncestorsAndSelf(run).Skip(1).FirstOrDefault(e => e.LocalName == "p" && e.NamespaceURI == W) == paragraph) yield return run;
    }
    /// <summary>
    /// The direct w:ind, w:spacing, w:bidi and w:tabs of a Word text-box paragraph as the a:pPr values the drawing reader
    /// resolves. Word measures in twentieths of a point (635 EMU): w:ind left and right (or start and end) are marL and
    /// marR, hanging and firstLine the negative and positive indent; w:spacing before and after are spcPts, and line is
    /// 240ths of a line under lineRule auto and otherwise a pitch: exact as it is, and for atLeast marked with the attribute
    /// atLeast="1" on a:spcPts (DrawingML has no such rule), which the style reader turns into LineSpacingAtLeast.
    /// </summary>
    private static void ConvertWordParagraphLayout(XmlDocument document, XmlElement? source, XmlElement target)
    {
        static double? Twips(XmlElement? element, params string[] names) => names.Select(name => element?.GetAttribute(name, W))
            .Select(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) &&
                Math.Abs(number) <= 31680 ? number : (double?)null).FirstOrDefault(number => number is not null);
        static string Text(double value) => Math.Round(value).ToString(CultureInfo.InvariantCulture);
        XmlElement Spacing(string name, string unit, double value)
        {
            var spacing = document.CreateElement("a", name, A); var amount = document.CreateElement("a", unit, A);
            amount.SetAttribute("val", Text(value)); spacing.AppendChild(amount);
            return spacing;
        }
        var indent = Child(source, "ind");
        if (Twips(indent, "left", "start") is { } left) target.SetAttribute("marL", Text(Math.Max(0, left) * 635));
        if (Twips(indent, "right", "end") is { } right) target.SetAttribute("marR", Text(Math.Max(0, right) * 635));
        if (Twips(indent, "hanging") is { } hanging) target.SetAttribute("indent", Text(-hanging * 635));
        else if (Twips(indent, "firstLine") is { } firstLine) target.SetAttribute("indent", Text(firstLine * 635));
        if (Child(source, "bidi") is { } bidi) target.SetAttribute("rtl", WordOn(bidi) ? "1" : "0");
        var spacing = Child(source, "spacing");
        if (Twips(spacing, "line") is > 0 and var line)
        {
            var rule = spacing!.GetAttribute("lineRule", W);
            var lineSpacing = rule is "exact" or "atLeast" ? Spacing("lnSpc", "spcPts", line * 5) : Spacing("lnSpc", "spcPct", line / 240 * 100000);
            if (rule == "atLeast") ((XmlElement)lineSpacing.FirstChild!).SetAttribute("atLeast", "1");
            target.AppendChild(lineSpacing);
        }
        if (Twips(spacing, "before") is >= 0 and var before) target.AppendChild(Spacing("spcBef", "spcPts", before * 5));
        if (Twips(spacing, "after") is >= 0 and var after) target.AppendChild(Spacing("spcAft", "spcPts", after * 5));
        var stops = Children(Child(source, "tabs")).Where(tab => tab.LocalName == "tab" && Twips(tab, "pos") is >= 0).Take(32).ToArray();
        if (stops.Length == 0) return;
        var list = document.CreateElement("a", "tabLst", A);
        foreach (var stop in stops)
        {
            var alignment = stop.GetAttribute("val", W) switch { "left" or "start" => "l", "center" => "ctr", "right" or "end" => "r", "decimal" => "dec", _ => null };
            if (alignment is null) continue;
            var tab = document.CreateElement("a", "tab", A);
            tab.SetAttribute("pos", Text(Twips(stop, "pos")!.Value * 635)); tab.SetAttribute("algn", alignment);
            list.AppendChild(tab);
        }
        if (list.HasChildNodes) target.AppendChild(list);
    }
    private static XmlElement ConvertWordRunProperties(XmlDocument document, XmlElement source, string name)
    {
        var result = document.CreateElement("a", name, A);
        foreach (var (word, drawing) in new[] { ("b", "b"), ("i", "i") })
            if (Child(source, word) is { } property) result.SetAttribute(drawing, WordOn(property) ? "1" : "0");
        if (Child(source, "sz") is { } size && double.TryParse(size.GetAttribute("val", W), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var halfPoints) && double.IsFinite(halfPoints) && halfPoints is > 0 and <= 8192)
            result.SetAttribute("sz", (halfPoints * 50).ToString("0.####", CultureInfo.InvariantCulture));
        if (Child(source, "color")?.GetAttribute("val", W) is { Length: 6 } color && color.All(char.IsAsciiHexDigit))
        {
            var fill = document.CreateElement("a", "solidFill", A); var rgb = document.CreateElement("a", "srgbClr", A);
            rgb.SetAttribute("val", color); fill.AppendChild(rgb); result.AppendChild(fill);
        }
        if (Child(source, "rFonts") is { } fonts)
        {
            foreach (var (word, drawing) in new[] { ("ascii", "latin"), ("eastAsia", "ea"), ("cs", "cs") })
            {
                var face = fonts.GetAttribute(word, W);
                if (word == "ascii" && face.Length == 0) face = fonts.GetAttribute("hAnsi", W);
                if (face.Length == 0)
                {
                    var themeFace = fonts.GetAttribute(word + "Theme", W);
                    if (word == "ascii" && themeFace.Length == 0) themeFace = fonts.GetAttribute("hAnsiTheme", W);
                    face = themeFace switch
                    {
                        "majorAscii" or "majorHAnsi" => "+mj-lt", "minorAscii" or "minorHAnsi" => "+mn-lt",
                        "majorEastAsia" => "+mj-ea", "minorEastAsia" => "+mn-ea",
                        "majorBidi" => "+mj-cs", "minorBidi" => "+mn-cs", _ => "",
                    };
                }
                if (face.Length == 0) continue;
                var font = document.CreateElement("a", drawing, A); font.SetAttribute("typeface", face); result.AppendChild(font);
            }
        }
        if (Child(source, "lang") is {} language)
        {
            if (language.GetAttribute("val", W) is { Length: > 0 } lang) result.SetAttribute("lang", lang);
            if (language.GetAttribute("eastAsia", W) is { Length: > 0 } eastAsian) result.SetAttribute("altLang", eastAsian);
        }
        if (Child(source, "u") is { } underline)
            result.SetAttribute("u", underline.GetAttribute("val", W) switch { "none" => "none", "double" => "dbl", _ => "sng" });
        if (Child(source, "dstrike") is { } doubleStrike && WordOn(doubleStrike)) result.SetAttribute("strike", "dblStrike");
        else if (Child(source, "strike") is { } strike) result.SetAttribute("strike", WordOn(strike) ? "sngStrike" : "noStrike");
        return result;
    }
    private static void EnsureDefaultTextBoxNoFill(XmlElement shape)
    {
        var document = shape.OwnerDocument!; var properties = Child(shape, "spPr");
        if (properties is null) { properties = document.CreateElement("a", "spPr", A); shape.PrependChild(properties); }
        if (!Children(properties).Any(e => e.LocalName is "solidFill" or "gradFill" or "noFill" or "blipFill" or "pattFill") &&
            Child(Child(shape, "style"), "fillRef") is null) properties.AppendChild(document.CreateElement("a", "noFill", A));
    }
    private static void ReplaceTextBodyWithPlainText(XmlElement shape, string source)
    {
        var document = shape.OwnerDocument!;
        if (Child(shape, "txBody") is { } existing) shape.RemoveChild(existing);
        var body = document.CreateElement("a", "txBody", A); body.AppendChild(document.CreateElement("a", "bodyPr", A));
        foreach (var line in source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var p = document.CreateElement("a", "p", A); var r = document.CreateElement("a", "r", A); var t = document.CreateElement("a", "t", A);
            t.InnerText = line; r.AppendChild(t); p.AppendChild(r); body.AppendChild(p);
        }
        shape.AppendChild(body);
    }
}
