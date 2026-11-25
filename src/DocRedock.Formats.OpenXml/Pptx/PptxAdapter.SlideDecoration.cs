using System.Xml;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Drawing;

namespace DocRedock.Formats.OpenXml.Pptx;

public sealed partial class PptxAdapter
{
    private const string NoPictureNodeWarning = "picture has no resolved source image node; its source reference and frame remain";
    private const string NoChartNodeWarning = "chart has no resolved source node; the adapter must attach its typed Chart or cached-data fallback";
    private static readonly string[] BackgroundFillNames = ["noFill", "solidFill", "gradFill", "blipFill", "pattFill", "grpFill"];

    private static Dictionary<string, string> SceneLinks(Dictionary<string, Relationship> relationships) =>
        relationships.ToDictionary(k => k.Key, k => Uri.TryCreate(k.Value.Target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto"
            ? k.Value.Target : "/" + k.Value.Target.TrimStart('/'), StringComparer.Ordinal);

    private static SceneLayer? SceneLayerOf(Dictionary<string, byte[]> package, string? part, Dictionary<string, SceneLayer?> cache)
    {
        if (part is null) return null;
        if (cache.TryGetValue(part, out var cached)) return cached;
        if (!package.TryGetValue(part, out var bytes)) return cache[part] = null;
        var document = SourceDrawingPackage.Load(bytes);
        return cache[part] = new SceneLayer(part, document, DrawingRelationships(package, part), DrawingTheme(package, part, document))
        { ShowsMasterShapes = ReadShowMasterSp(bytes, "sldLayout") };
    }

    /// <summary>
    /// One part of the chain slide, slide layout, slide master as a slide's scene reads it, with the part's
    /// own relationships and its own theme and color map. A layout and a master contribute their background
    /// and every shape, picture, chart, table and SmartArt that is not a placeholder: PowerPoint draws those
    /// behind the slide's shapes, the master's first, unless the slide (both) or the layout (the master's) has
    /// showMasterSp="0". A chart is read through the part's own relationships, SmartArt from its saved drawing.
    /// A placeholder of a layout or master is never drawn: it only lends its properties to the slide's.
    /// </summary>
    private sealed class SceneLayer(string part, XmlDocument document, Dictionary<string, Relationship> relationships, XmlDocument? theme)
    {
        private XmlElement? _tree;
        private Dictionary<string, string>? _links;
        private bool _treeRead, _warningsReported;
        private readonly Dictionary<string, DrawingChart?> _charts = new(StringComparer.Ordinal);
        private Dictionary<string, SourceDrawingTableStyle>? _tableStyles;

        /// <summary>The cell appearance and text of the part's tables, by the id of the table's object; filled by <see cref="Decoration"/>.</summary>
        public IReadOnlyDictionary<string, SourceDrawingTableStyle> TableStyles => _tableStyles ?? [];

        public XmlDocument Document => document;
        public Dictionary<string, Relationship> Relationships => relationships;
        public XmlDocument? Theme => theme;
        /// <summary>False for a layout with showMasterSp="0": its slides do not show the master's shapes.</summary>
        public bool ShowsMasterShapes { get; init; } = true;

        /// <summary>
        /// The part's shapes that are no placeholder, as scene objects of one slide. <paramref name="node"/> resolves
        /// the slide's semantic node of an inherited shape; its locator and the object's are "<paramref name="from"/>:id",
        /// so that an id of this part never meets the same id among the slide's own shapes.
        /// </summary>
        /// <param name="tableStyles">Reads the cells of the part's tables, which have no semantic node, with their text.</param>
        public IReadOnlyList<WorkbookDrawingObject> Decoration(Func<XmlElement, DrawingMlInheritedShape?> inheritance,
            Func<string, DocumentNode?> node, string from, Func<string, string?, string?> jumps, ICollection<string> warnings, CancellationToken cancellationToken,
            DrawingMlPictureFills? pictureFills = null, Func<string, byte[]?>? readPart = null,
            Func<XmlElement, IReadOnlyList<WorkbookDrawingObject>, XmlDocument?, Dictionary<string, SourceDrawingTableStyle>>? tableStyles = null)
        {
            if (!_treeRead) { _treeRead = true; _tree = DecorationTree(document); }
            if (_tree is null) return [];
            var found = new List<string>();
            var objects = DrawingMlSceneReader.ReadInherited(_tree, inheritance, "pptx", "/" + part, theme, _links ??= SceneLinks(relationships),
                node, found, cancellationToken, actionLinkResolver: jumps, pictureFills: pictureFills,
                diagramResolver: readPart is null ? null : (relIds, frame) => CachedSmartArtScene.Expand(relIds, frame, "/" + part, "pptx", readPart, theme, found, cancellationToken, pictureFills));
            objects = objects.Select(item =>
            {
                if (item.Kind != "chart" || item.Source?.Locators.FirstOrDefault(l => l.Kind == "chart_part")?.Value.TrimStart('/') is not { Length: > 0 } chartPart) return item;
                if (!_charts.TryGetValue(chartPart, out var chart))
                    _charts[chartPart] = chart = readPart?.Invoke(chartPart) is { } bytes ? SourceDrawingPackage.Chart(bytes, theme, found, part + ":" + item.Id, chartPart, readPart, pictureFills) : null;
                return item with { Chart = chart };
            }).ToArray();
            _tableStyles ??= tableStyles?.Invoke(_tree, objects, theme);
            // Every slide of the layout reads the same shapes: their findings are reported once. A picture or a chart
            // of a layout or master has no node by design, the scene holds its package part or the chart itself.
            if (!_warningsReported)
            {
                _warningsReported = true;
                foreach (var warning in found.Where(warning => !warning.EndsWith(NoPictureNodeWarning, StringComparison.Ordinal) &&
                    !warning.EndsWith(NoChartNodeWarning, StringComparison.Ordinal))) warnings.Add(warning);
            }
            return objects.Where(item => item.Kind is "shape" or "picture" or "chart" or "table").Select(item => item.Source is not { } source ? item : item with
            {
                Source = source with { Locators = source.Locators.Select(l => l.Kind == "shape_id" ? l with { Value = from + ":" + l.Value } : l).ToArray() },
            }).ToArray();
        }

        /// <summary>The relationships of the theme part this part's chain (slide, layout, master) leads to: what a picture fill of the theme refers to.</summary>
        public Dictionary<string, Relationship> ThemeRelationships(Dictionary<string, byte[]> package)
        {
            var current = part; var rels = relationships.Values.ToArray();
            for (var depth = 0; depth < 5; depth++)
            {
                if (rels.FirstOrDefault(r => r.Type.EndsWith("/theme", StringComparison.Ordinal)) is { } themeRel && package.ContainsKey(themeRel.Target.TrimStart('/')))
                    return DrawingRelationships(package, themeRel.Target.TrimStart('/'));
                var next = rels.FirstOrDefault(r => r.Type.EndsWith("/slideLayout", StringComparison.Ordinal) || r.Type.EndsWith("/slideMaster", StringComparison.Ordinal));
                if (next is null || !package.ContainsKey(next.Target)) break;
                current = next.Target; rels = DrawingRelationships(package, current).Values.ToArray();
            }
            return new(StringComparer.Ordinal);
        }

        private static XmlElement? DecorationTree(XmlDocument document)
        {
            if (document.SelectSingleNode("/*/*[local-name()='cSld']/*[local-name()='spTree']") is not XmlElement tree) return null;
            var copy = (XmlElement)tree.CloneNode(true);
            foreach (var placeholder in copy.SelectNodes(".//*[local-name()='nvPr']/*[local-name()='ph']")!.OfType<XmlElement>().ToArray())
                if (placeholder.ParentNode?.ParentNode?.ParentNode is XmlElement shape && shape != copy) shape.ParentNode?.RemoveChild(shape);
            return copy;
        }
    }

    /// <summary>
    /// The background of a slide: p:cSld/p:bg of the slide, else of its layout, else of its master
    /// (<paramref name="nearestFirst"/>). p:bgPr holds the fill itself; p:bgRef names a fill of the theme
    /// (idx 1..999 of a:fillStyleLst, from 1001 of a:bgFillStyleLst) and the color that fill's phClr stands for.
    /// Colors resolve against the theme and color map of the part that holds the background. A picture fill
    /// is returned as the package part of its image, stretched over the slide (inside its a:fillRect) or repeated
    /// as its a:tile says, with the part of the picture its a:srcRect keeps; its relationship id is that of the part that holds the fill: the slide, layout or master for p:bgPr, the
    /// theme for a fill p:bgRef names.
    /// </summary>
    private static (DrawingPaint? Paint, string? Image, DrawingPictureTile? Tile, DrawingInsets? Crop, DrawingInsets? Stretch) SlideBackground(Dictionary<string, byte[]> package, IReadOnlyList<SceneLayer> nearestFirst)
    {
        foreach (var layer in nearestFirst)
        {
            if (layer.Document.SelectSingleNode("/*/*[local-name()='cSld']/*[local-name()='bg']") is not XmlElement background) continue;
            XmlElement? Child(XmlElement? parent, string name) => parent?.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == name);
            var work = new XmlDocument { XmlResolver = null };
            var shape = work.CreateElement("sp"); work.AppendChild(shape);
            if (Child(background, "bgPr") is { } properties)
            {
                var fill = properties.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => Array.IndexOf(BackgroundFillNames, e.LocalName) >= 0);
                if (fill is null) return default;
                var shapeProperties = work.CreateElement("spPr"); shape.AppendChild(shapeProperties);
                shapeProperties.AppendChild(work.ImportNode(fill, true));
            }
            else if (Child(background, "bgRef") is { } reference)
            {
                var fillReference = work.CreateElement("fillRef");
                fillReference.SetAttribute("idx", reference.GetAttribute("idx"));
                foreach (var color in reference.ChildNodes.OfType<XmlElement>()) fillReference.AppendChild(work.ImportNode(color, true));
                var style = work.CreateElement("style"); shape.AppendChild(style);
                style.AppendChild(fillReference);
            }
            else return default;
            var pictures = shape.FirstChild?.LocalName == "style" ? layer.ThemeRelationships(package) : layer.Relationships;
            string? Picture(string id, out string? reason)
            {
                reason = "its relationship is missing or is no image of the package";
                return pictures.TryGetValue(id, out var relationship) && relationship.Type.EndsWith("/image", StringComparison.Ordinal) &&
                    package.ContainsKey(relationship.Target) ? "/" + relationship.Target : null;
            }
            var paint = DrawingMlStyleReader.Read(shape, layer.Theme, imageResolver: Picture).FillPaint;
            // The picture travels as its package part: the page holds its bytes once for every slide that shows it.
            static DrawingInsets? Set(DrawingInsets? insets) => insets is null or { Left: 0, Top: 0, Right: 0, Bottom: 0 } ? null : insets;
            return paint.Kind == "picture" ? (null, paint.ImageReference, paint.Tile, Set(paint.Crop), Set(paint.Stretch)) : (paint.Kind == "none" ? null : paint, null, null, null, null);
        }
        return default;
    }
}
