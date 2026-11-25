using System.Xml;
using DocRedock.Formats.OpenXml.Drawing;

namespace DocRedock.Formats.OpenXml.Pptx;

public sealed partial class PptxAdapter
{
    private static PlaceholderInheritance SlideInheritance(Dictionary<string, byte[]> package, string slidePart,
        XmlDocument? presentation, Dictionary<string, PlaceholderInheritance> cache)
    {
        var (layoutTarget, masterTarget) = ResolveLayoutAndMaster(package, slidePart);
        var key = layoutTarget ?? "";
        if (cache.TryGetValue(key, out var cached)) return cached;
        XmlDocument? Part(string? target) => target is not null && package.TryGetValue(target, out var bytes) ? SourceDrawingPackage.Load(bytes) : null;
        return cache[key] = new PlaceholderInheritance(Part(layoutTarget), Part(masterTarget),
            presentation?.DocumentElement?.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "defaultTextStyle"));
    }

    /// <summary>
    /// ECMA-376 inheritance for the shapes of the slides that use one layout. A slide placeholder
    /// (p:nvPr/p:ph) inherits from the layout placeholder and that from the master placeholder:
    /// <list type="bullet">
    /// <item>slide to layout: the layout placeholder with the same idx when the slide's p:ph has an idx;
    /// when it has none or no layout placeholder has that idx, the first layout placeholder of the same
    /// type, else the first of the same family;</item>
    /// <item>to the master: by family only, because a master numbers its placeholders independently of
    /// its layouts (a layout's second content placeholder and the master's date both have idx 2);</item>
    /// <item>families: title (title, ctrTitle); body (body, subTitle, obj, an absent type and the content
    /// types chart, tbl, clipArt, dgm, media, pic); every other type (dt, ftr, sldNum, hdr, sldImg) only itself.</item>
    /// </list>
    /// Text levels come from layout placeholder a:lstStyle, master placeholder a:lstStyle, the master's
    /// p:txStyles (titleStyle for the title family, bodyStyle for the body family, otherStyle for the rest
    /// and for shapes that are no placeholder) and the presentation's p:defaultTextStyle, nearest first.
    /// </summary>
    private sealed class PlaceholderInheritance
    {
        private readonly List<(XmlElement Shape, string? Type, string? Idx)> _layout, _master;
        private readonly XmlElement? _textStyles, _defaultTextStyle;
        private readonly Dictionary<(XmlElement? Layout, XmlElement? Master, string Category), XmlElement?> _listStyles = [];

        public PlaceholderInheritance(XmlDocument? layout, XmlDocument? master, XmlElement? defaultTextStyle)
        {
            _layout = Placeholders(layout);
            _master = Placeholders(master);
            _textStyles = master?.DocumentElement?.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "txStyles");
            _defaultTextStyle = defaultTextStyle;
        }

        public DrawingMlInheritedShape? Resolve(XmlElement shape)
        {
            if (shape.LocalName is not ("sp" or "pic")) return null;
            var hasBody = Child(shape, "txBody") is not null;
            if (Placeholder(shape) is not { } placeholder)
                return hasBody && ListStyle(null, null, "otherStyle") is { } other ? new DrawingMlInheritedShape { ListStyle = other } : null;
            var type = Optional(placeholder, "type");
            var idx = Optional(placeholder, "idx");
            var layout = (idx is null ? default : _layout.FirstOrDefault(p => p.Idx == idx)) is { Shape: not null } sameIdx ? sameIdx : ByType(_layout, type);
            var family = Family(type ?? layout.Type);
            var master = ByType(_master, type ?? layout.Type);
            var shapes = new[] { layout.Shape, master.Shape }.Where(s => s is not null).Select(s => s!).ToArray();
            var listStyle = hasBody ? ListStyle(layout.Shape, master.Shape, family == "title" ? "titleStyle" : family == "body" ? "bodyStyle" : "otherStyle") : null;
            if (shapes.Length == 0 && listStyle is null) return null;
            // PowerPoint shows an empty placeholder's prompt and frame while editing only: nothing of it is painted.
            var empty = shape.LocalName == "sp" && !shape.SelectNodes(".//*[local-name()='t']")!.OfType<XmlElement>().Any(t => t.InnerText.Length > 0);
            return new DrawingMlInheritedShape { Shapes = shapes, ListStyle = listStyle, Paint = !empty };
        }

        private XmlElement? ListStyle(XmlElement? layout, XmlElement? master, string category)
        {
            if (_listStyles.TryGetValue((layout, master, category), out var cached)) return cached;
            XmlElement? Own(XmlElement? shape) => Child(Child(shape, "txBody"), "lstStyle");
            return _listStyles[(layout, master, category)] = DrawingMlInheritedShape.MergeListStyles(
                [Own(layout), Own(master), Child(_textStyles, category), _defaultTextStyle]);
        }

        private static (XmlElement? Shape, string? Type, string? Idx) ByType(List<(XmlElement Shape, string? Type, string? Idx)> placeholders, string? type)
        {
            var exact = placeholders.FirstOrDefault(p => (p.Type ?? "obj") == (type ?? "obj"));
            return exact.Shape is not null ? exact : placeholders.FirstOrDefault(p => Family(p.Type) == Family(type));
        }

        private static string Family(string? type) => type switch
        {
            "title" or "ctrTitle" => "title",
            null or "body" or "subTitle" or "obj" or "chart" or "tbl" or "clipArt" or "dgm" or "media" or "pic" => "body",
            _ => type,
        };

        private static List<(XmlElement Shape, string? Type, string? Idx)> Placeholders(XmlDocument? part)
        {
            var result = new List<(XmlElement, string?, string?)>();
            if (part?.SelectNodes("//*[local-name()='spTree']//*[local-name()='sp' or local-name()='pic']") is { } shapes)
                foreach (XmlElement shape in shapes)
                    if (Placeholder(shape) is { } placeholder) result.Add((shape, Optional(placeholder, "type"), Optional(placeholder, "idx")));
            return result;
        }

        private static XmlElement? Placeholder(XmlElement shape) => Child(Child(
            shape.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName is "nvSpPr" or "nvPicPr"), "nvPr"), "ph");
        private static string? Optional(XmlElement element, string name) => element.HasAttribute(name) ? element.GetAttribute(name) : null;
        private static XmlElement? Child(XmlElement? parent, string name) =>
            parent?.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == name);
    }
}
