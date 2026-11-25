using System.Globalization;
using System.Text.Json;
using System.Xml;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Drawing;

namespace DocRedock.Formats.OpenXml.Pptx;

public sealed partial class PptxAdapter
{
    private static JsonElement SourceScenes(Dictionary<string, byte[]> package, IReadOnlyList<DocumentPartition> partitions,
        ICollection<string> warnings, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<SourceDrawingScene>>(StringComparer.Ordinal);
        var presentation = package.TryGetValue("ppt/presentation.xml", out var presentationBytes) ? SourceDrawingPackage.Load(presentationBytes) : null;
        var size = presentation?.SelectSingleNode("//*[local-name()='sldSz']") as XmlElement;
        double Dimension(string key, double fallback) => double.TryParse(size?.GetAttribute(key), CultureInfo.InvariantCulture, out var n) &&
            double.IsFinite(n) && n is > 0 and <= 952_500_000_000 ? n / 9525 : fallback;
        var inheritance = new Dictionary<string, PlaceholderInheritance>(StringComparer.Ordinal);
        var layers = new Dictionary<string, SceneLayer?>(StringComparer.Ordinal);
        var tableStyles = new SlideTableStyles(package.TryGetValue("ppt/tableStyles.xml", out var tableStyleBytes) ? SourceDrawingPackage.Load(tableStyleBytes) : null);
        // Picture fills (a:blipFill on a shape) of the whole deck share one byte budget.
        var pictureFills = new DrawingMlPictureFills(uri => package.GetValueOrDefault(uri.TrimStart('/')));
        // One partition per slide, in presentation order: what a jump between slides is resolved against.
        var slideOrder = partitions.Select(p => p.SourcePartUri?.TrimStart('/') is { } slidePart && package.TryGetValue(slidePart, out var slideBytes)
            ? (Part: slidePart, Hidden: IsSlideHidden(slideBytes)) : (Part: "", Hidden: true)).ToArray();
        var slideIndex = -1;
        foreach (var partition in partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            slideIndex++;
            var part = partition.SourcePartUri?.TrimStart('/');
            if (part is null || !package.TryGetValue(part, out var bytes)) continue;
            var document = SourceDrawingPackage.Load(bytes);
            var root = document.SelectSingleNode("//*[local-name()='spTree']") as XmlElement;
            if (root is null) continue;
            var relationships = DrawingRelationships(package, part);
            var links = SceneLinks(relationships);
            var theme = DrawingTheme(package, part, document);
            var nodes = partition.Nodes.Where(n => n.Source?.Locators.Any(l => l.Kind == "shape_id") == true)
                .GroupBy(n => n.Source!.Locators.First(l => l.Kind == "shape_id").Value, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            DocumentNode? Node(string id)
            {
                if (!nodes.TryGetValue(id, out var node)) return null;
                // Empty geometry is not Word/PowerPoint hidden content. The semantic adapter uses
                // Hidden for an empty caption, whereas this source scene still draws its outline.
                return node.Kind == NodeKind.Shape && node.Layer == ContentLayer.Hidden &&
                    node.Extensions?.GetValueOrDefault("hidden_object").ValueKind != JsonValueKind.True &&
                    node.Extensions?.GetValueOrDefault("hidden_slide").ValueKind != JsonValueKind.True
                    ? node with { Layer = ContentLayer.Body } : node;
            }
            var slideInheritance = SlideInheritance(package, part, presentation, inheritance);
            var (layoutPart, masterPart) = ResolveLayoutAndMaster(package, part);
            var layout = SceneLayerOf(package, layoutPart, layers); var master = SceneLayerOf(package, masterPart, layers);
            var tableTextSize = DefaultTableTextSize(master?.Document, presentation);
            var tableText = (master?.Document.SelectSingleNode("/*/*[local-name()='txStyles']/*[local-name()='otherStyle']") ??
                presentation?.SelectSingleNode("/*/*[local-name()='defaultTextStyle']")) as XmlElement;
            // PowerPoint draws what is not a placeholder on the master, then on the layout, behind the slide's own shapes.
            IReadOnlyList<WorkbookDrawingObject> Decoration(SceneLayer? layer, string from) => layer is null ? [] :
                layer.Decoration(slideInheritance.Resolve, id => Node(from + ":" + id), from,
                    new SlideJumps(slideOrder, slideIndex, layer.Relationships).Target, warnings, cancellationToken, pictureFills,
                    uri => package.GetValueOrDefault(uri.TrimStart('/')),
                    (tree, tables, layerTheme) => tableStyles.Read(tree, tables, layerTheme, tableTextSize, tableText, withText: true));
            var showsInherited = ReadShowMasterSp(bytes, "sld");
            var slideObjects = DrawingMlSceneReader.ReadInherited(root, slideInheritance.Resolve,
                "pptx", "/" + part, theme, links, Node, warnings: warnings, cancellationToken: cancellationToken,
                actionLinkResolver: new SlideJumps(slideOrder, slideIndex, relationships).Target, pictureFills: pictureFills,
                diagramResolver:(relIds,frame)=>CachedSmartArtScene.Expand(relIds,frame,"/"+part,"pptx",
                    uri=>package.GetValueOrDefault(uri.TrimStart('/')),theme,warnings,cancellationToken,pictureFills));
            var masterObjects = showsInherited && layout?.ShowsMasterShapes != false ? Decoration(master, "master") : [];
            var layoutObjects = showsInherited ? Decoration(layout, "layout") : [];
            var objects = masterObjects.Concat(layoutObjects).Concat(slideObjects).Select(item =>
            {
                if (item.SourceNodeId is {} sourceNodeId && partition.Nodes.FirstOrDefault(n => n.Id == sourceNodeId) is {} sourceNode &&
                    sourceNode.Extensions?.TryGetValue("paragraph_details", out var details) == true && details.ValueKind == JsonValueKind.Array)
                {
                    var saved = details.Deserialize<PptxTextParagraph[]>();
                    if (saved is not null && saved.Length == item.Paragraphs.Count)
                        item = item with { Paragraphs = item.Paragraphs.Select((p, i) => p with
                        {
                            // PowerPoint draws no inherited marker on a paragraph without text; the scene's own reading decides there.
                            BulletText = saved[i].Text.Length == 0 ? p.BulletText : saved[i].IsOrdered ? (saved[i].ListNumber ?? 1).ToString(CultureInfo.InvariantCulture) + "." :
                                saved[i].IsBullet ? saved[i].BulletCharacter ?? "•" : p.BulletText,
                            Level = Math.Clamp(saved[i].Level, 0, 8),
                        }).ToArray() };
                }
                if (item.Kind != "chart" || item.Chart is not null) return item;
                var chartPart = item.Source?.Locators.FirstOrDefault(l => l.Kind == "chart_part")?.Value.TrimStart('/');
                return chartPart is not null && package.TryGetValue(chartPart, out var chart)
                    ? item with { Chart = SourceDrawingPackage.Chart(chart, theme, warnings, part + ":" + item.Id, chartPart,
                        uri => package.GetValueOrDefault(uri.TrimStart('/')), pictureFills) } : item;
            }).ToArray();
            if (objects.Length == 0) continue;
            var width = Dimension("cx", Math.Max(1, objects.Max(o => o.Transform.Point(o.Width, o.Height).X)));
            var height = Dimension("cy", Math.Max(1, objects.Max(o => o.Transform.Point(o.Width, o.Height).Y)));
            var (background, backgroundImage, backgroundTile, backgroundCrop, backgroundStretch) = SlideBackground(package,
                [new(part, document, relationships, theme), .. new[] { layout, master }.OfType<SceneLayer>()]);
            var styledTables = tableStyles.Read(root, slideObjects, theme, tableTextSize, tableText);
            // The tables of the master and the layout that this slide shows: one with text is a node of the slide, one without is drawn from its cells alone.
            foreach (var (layer, shown) in new[] { (master, masterObjects), (layout, layoutObjects) })
                foreach (var table in shown.Where(item => item.Kind == "table"))
                    if (layer is not null && layer.TableStyles.TryGetValue(table.Id, out var lent)) styledTables[table.Id] = lent;
            result[partition.Id] = [new SourceDrawingScene(part, width, height, objects)
            {
                CoordinateSpace = "slide", Background = background, BackgroundImageReference = backgroundImage, BackgroundImageTile = backgroundTile,
                BackgroundImageCrop = backgroundCrop, BackgroundImageStretch = backgroundStretch,
                TableStyles = styledTables.Count > 0 ? styledTables : null,
            }];
        }
        return SourceDrawingSceneJson.Write(result);
    }

    private static Dictionary<string, Relationship> DrawingRelationships(Dictionary<string, byte[]> package, string part)
    {
        var slash = part.LastIndexOf('/');
        return ReadRelationships(package, part[..slash] + "/_rels/" + part[(slash + 1)..] + ".rels");
    }

    private static XmlDocument? DrawingTheme(Dictionary<string, byte[]> package, string part, XmlDocument slide)
    {
        var current = part; XmlElement? map = slide.SelectSingleNode("//*[local-name()='overrideClrMapping' or local-name()='clrMap']") as XmlElement;
        for (var depth = 0; depth < 5; depth++)
        {
            var rels = DrawingRelationships(package, current).Values;
            var themeRel = rels.FirstOrDefault(r => r.Type.EndsWith("/theme", StringComparison.Ordinal));
            if (themeRel is not null && package.TryGetValue(themeRel.Target.TrimStart('/'), out var themeBytes))
            {
                var theme = SourceDrawingPackage.Load(themeBytes);
                if (map is not null)
                {
                    var clone = theme.CreateElement("clrMap");
                    foreach (XmlAttribute a in map.Attributes) if (a.NamespaceURI.Length == 0) clone.SetAttribute(a.LocalName, a.Value);
                    theme.DocumentElement!.AppendChild(clone);
                }
                return theme;
            }
            var next = rels.FirstOrDefault(r => r.Type.EndsWith("/slideLayout", StringComparison.Ordinal) || r.Type.EndsWith("/slideMaster", StringComparison.Ordinal));
            if (next is null || !package.TryGetValue(next.Target, out var parentBytes)) break;
            current = next.Target;
            var parent = SourceDrawingPackage.Load(parentBytes);
            map ??= parent.SelectSingleNode("//*[local-name()='overrideClrMapping' or local-name()='clrMap']") as XmlElement;
        }
        return null;
    }
}
