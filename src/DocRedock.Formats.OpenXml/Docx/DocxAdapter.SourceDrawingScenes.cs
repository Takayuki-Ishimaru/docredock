using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Formats.OpenXml.Common;
using DocRedock.Formats.OpenXml.Drawing;

namespace DocRedock.Formats.OpenXml.Docx;

public sealed partial class DocxAdapter
{
    private static XmlDocument? SourceDrawingTheme(ZipArchive archive, CancellationToken cancellationToken)
    {
        var entry = archive.Entries.FirstOrDefault(e => e.FullName.StartsWith("word/theme/", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal));
        if (entry is null) return null;
        var theme = SourceDrawingPackage.Load(ReadEntryAsync(entry, cancellationToken).GetAwaiter().GetResult());
        // Word maps the theme's color names for the whole document in w:clrSchemeMapping of its settings, as a slide
        // master does in p:clrMap: a shape's bg1 or tx1 is the theme color that mapping names. It travels as the
        // a:clrMap the style reader looks for; first, so that a clrMap of an extra color scheme does not hide it.
        if (theme.DocumentElement is { } themeRoot && archive.GetEntry("word/settings.xml") is { } settings &&
            SafeXml.LoadDocument(ReadEntryAsync(settings, cancellationToken).GetAwaiter().GetResult())
                .Descendants(W + "clrSchemeMapping").FirstOrDefault() is { } mapping)
        {
            var map = theme.CreateElement("a", "clrMap", A.NamespaceName);
            foreach (var (word, drawing) in new[] { ("bg1", "bg1"), ("t1", "tx1"), ("bg2", "bg2"), ("t2", "tx2"), ("accent1", "accent1"),
                ("accent2", "accent2"), ("accent3", "accent3"), ("accent4", "accent4"), ("accent5", "accent5"), ("accent6", "accent6"),
                ("hyperlink", "hlink"), ("followedHyperlink", "folHlink") })
            {
                var color = (string?)mapping.Attribute(W + word) switch
                {
                    "light1" => "lt1", "dark1" => "dk1", "light2" => "lt2", "dark2" => "dk2", "hyperlink" => "hlink", "followedHyperlink" => "folHlink",
                    "accent1" or "accent2" or "accent3" or "accent4" or "accent5" or "accent6" => (string?)mapping.Attribute(W + word),
                    _ => null,
                };
                if (color is not null) map.SetAttribute(drawing, color);
            }
            if (map.HasAttributes) themeRoot.PrependChild(map);
        }
        return theme;
    }

    // One bound for the fill pictures of a document: the archive is the document being read.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ZipArchive, DrawingMlPictureFills> SourcePictureFills = new();

    private static void AddSourceDrawingScenes(XElement paragraph, DocumentNode host, IReadOnlyList<DocumentNode> blockNodes,
        Dictionary<string, string> relationships, ZipArchive archive, XmlDocument? theme, DocxHiddenStyles hiddenStyles,
        DocxDrawnStyles? styles, List<SourceDrawingScene> scenes, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        // The map is an address map: a source textbox/image/chart occurrence binds to its own
        // extracted node. Repeated captions and repeated image relationship IDs are not identities.
        var boxes = paragraph.Descendants(W + "txbxContent").ToArray();
        var blips = paragraph.Descendants(A + "blip").ToArray();
        var charts = paragraph.Descendants(C + "chart").ToArray();
        var frames = paragraph.Descendants().Where(e => e.Name == WP + "inline" || e.Name == WP + "anchor")
            .Where(e => !e.Ancestors().Any(a => a.Name == WP + "inline" || a.Name == WP + "anchor")).ToArray();
        foreach (var (frame, frameIndex) in frames.Select((f, i) => (f, i)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var originalObjects = frame.Descendants().Where(IsSourceDrawingObject).ToArray();
            // Ordinary inline pictures use the existing image flow. Cropped, reflected or
            // rotated pictures need their source frame even when no shape shares the drawing.
            if (originalObjects.Length == 0 || originalObjects.All(IsPlainSourcePicture)) continue;
            var bindings = new Dictionary<string, Queue<DocumentNode?>>(StringComparer.Ordinal);
            for (var i = 0; i < originalObjects.Length; i++)
            {
                var element = originalObjects[i];
                var id = SourceShapeIdentity(element) ?? "source-" + (i + 1).ToString(CultureInfo.InvariantCulture);
                var box = element.Descendants(W + "txbxContent").FirstOrDefault(b => b.Ancestors().FirstOrDefault(IsSourceDrawingObject) == element);
                var blip = element.Descendants(A + "blip").FirstOrDefault();
                var chart = element.Name == C + "chart" ? element : element.Descendants(C + "chart").FirstOrDefault();
                var kind = box is not null ? NodeKind.TextBox : blip is not null ? NodeKind.Image : chart is not null ? NodeKind.Chart : NodeKind.Unknown;
                var locator = box is not null ? "textbox" : blip is not null ? "visual_index" : "chart_index";
                var index = box is not null ? Array.IndexOf(boxes, box) : blip is not null ? Array.IndexOf(blips, blip) : chart is not null ? Array.IndexOf(charts, chart) : -1;
                var node = blockNodes.FirstOrDefault(n => n.Kind == kind && n.Source?.Locators.Any(l => l.Kind == locator && l.Value == index.ToString(CultureInfo.InvariantCulture)) == true);
                if (!bindings.TryGetValue(id, out var queue)) bindings[id] = queue = new();
                queue.Enqueue(node);
            }
            DocumentNode? Resolve(string id) => bindings.TryGetValue(id, out var queue) && queue.Count > 0 ? queue.Dequeue() : null;
            var document = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
            using (var source = WithInheritedTextBoxFormatting(frame, styles).CreateReader()) document.Load(source);
            var width = SourcePixels(frame.Element(WP + "extent")?.Attribute("cx")?.Value);
            var height = SourcePixels(frame.Element(WP + "extent")?.Attribute("cy")?.Value);
            var notices = new List<string>();
            var links = relationships.ToDictionary(k => k.Key, k => Uri.TryCreate(k.Value, UriKind.Absolute, out _) ? k.Value : "/" + k.Value.TrimStart('/'), StringComparer.Ordinal);
            var pictureFills = SourcePictureFills.GetValue(archive, package => new(uri => package.GetEntry(uri.TrimStart('/')) is { } pictureEntry &&
                pictureEntry.Length <= DrawingMlPictureFills.MaxImageBytes ? ReadEntryAsync(pictureEntry, cancellationToken).GetAwaiter().GetResult() : null));
            var items = DrawingMlSceneReader.Read(document.DocumentElement!, "docx", "/word/document.xml", theme, links, Resolve,
                width, height, notices, cancellationToken, pictureFills: pictureFills).Select(item =>
            {
                var hidden = item.Hidden || IsHiddenContentElement(frame, hiddenStyles);
                // Word keeps a picture's alternative text on its frame (wp:docPr); the picture's own
                // cNvPr usually holds a generated name. The bound image node already carries the right one.
                if (item.Kind == "picture" && item.SourceNodeId is {} pictureNode &&
                    blockNodes.FirstOrDefault(n => n.Id == pictureNode)?.Content is ReferenceNodeContent { AltText: { Length: > 0 } alt })
                    item = item with { Text = alt };
                if (item.Kind != "chart") return item with { Id = host.Id + ":" + frameIndex + ":" + item.Id, Hidden = hidden };
                var chartPart = item.Source?.Locators.FirstOrDefault(l => l.Kind == "chart_part")?.Value.TrimStart('/');
                var chartEntry = chartPart is null ? null : archive.GetEntry(chartPart);
                return item with { Id = host.Id + ":" + frameIndex + ":" + item.Id, Hidden = hidden, Chart = chartEntry is null ? null : SourceDrawingPackage.Chart(
                    ReadEntryAsync(chartEntry, cancellationToken).GetAwaiter().GetResult(), theme, notices, item.Id, chartPart,
                    uri => archive.GetEntry(uri.TrimStart('/')) is { } partEntry && partEntry.Length <= DrawingMlPictureFills.MaxImageBytes
                        ? ReadEntryAsync(partEntry, cancellationToken).GetAwaiter().GetResult() : null, pictureFills) };
            }).ToArray();
            if (items.Length == 0) continue;
            var horizontal = frame.Element(WP + "positionH"); var vertical = frame.Element(WP + "positionV");
            var x = SourcePixels(horizontal?.Element(WP + "posOffset")?.Value);
            var y = SourcePixels(vertical?.Element(WP + "posOffset")?.Value);
            var inline = frame.Name == WP + "inline";
            // Keep the configured anchor reference explicit. The independent frame does not
            // pretend to paginate Word paragraphs using browser line metrics.
            var coordinate = inline ? "inline" : "anchor:" + ((string?)horizontal?.Attribute("relativeFrom") ?? "column") + "/" + ((string?)vertical?.Attribute("relativeFrom") ?? "paragraph");
            if (!inline)
            {
                items = items.Select(item => item with { Transform = WorkbookDrawingTransform.Translate(x, y) * item.Transform }).ToArray();
                if (horizontal?.Element(WP + "align") is not null || vertical?.Element(WP + "align") is not null)
                    notices.Add("SourceDrawingScenePartial: Word alignment anchor needs page text layout; drawing-local geometry is retained: " + host.Id);
            }
            var points = items.SelectMany(o => new[] { o.Transform.Point(0, 0), o.Transform.Point(o.Width, 0), o.Transform.Point(0, o.Height), o.Transform.Point(o.Width, o.Height) }).ToArray();
            var originX = Math.Min(0, points.Min(p => p.X));
            // The scene is placed after its anchor paragraph, so a floating frame's vertical offset
            // (often measured from the page top) would only add an empty band above it.
            var originY = inline ? Math.Min(0, points.Min(p => p.Y)) : points.Min(p => p.Y);
            width = Math.Max(width + Math.Max(0, x), points.Max(p => p.X)) - originX;
            height = (inline ? Math.Max(height + Math.Max(0, y), points.Max(p => p.Y)) : points.Max(p => p.Y)) - originY;
            if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 || width > 1e8 || height > 1e8) continue;
            scenes.Add(new SourceDrawingScene(host.Id + ":drawing:" + frameIndex, width, height, items)
            { AnchorNodeId = host.Id, AnchorOrder = host.Order, CoordinateSpace = coordinate, OriginX = originX, OriginY = originY });
            foreach (var notice in notices.Distinct(StringComparer.Ordinal))
                diagnostics.Add(new("SourceDrawingScenePartial", notice, DiagnosticSeverity.Warning, host.Id, "/word/document.xml"));
        }
    }

    // The properties the scene reader takes from a text-box paragraph and run (DrawingMlSceneReader.ConvertWordParagraphLayout
    // and ConvertWordRunProperties). Those whose attributes are inherited one by one, with the attributes that replace
    // each other, and the run properties that toggle between style levels.
    private static readonly string[] SceneParagraphProperties = ["jc", "bidi", "ind", "spacing", "tabs"];
    private static readonly string[] SceneRunProperties = ["b", "i", "strike", "dstrike", "sz", "color", "u", "rFonts", "lang"];
    private static readonly string[] SceneToggleProperties = ["b", "i", "strike", "dstrike"];
    private static readonly Dictionary<string, string[][]> SceneAttributeGroups = new(StringComparer.Ordinal)
    {
        ["ind"] = [["left", "start", "leftChars", "startChars"], ["right", "end", "rightChars", "endChars"], ["hanging", "firstLine", "hangingChars", "firstLineChars"]],
        ["spacing"] = [],
        ["rFonts"] = [["ascii", "asciiTheme"], ["hAnsi", "hAnsiTheme"], ["eastAsia", "eastAsiaTheme"], ["cs", "cstheme"]],
        ["lang"] = [],
    };

    /// <summary>
    /// The frame with the formatting its text-box paragraphs and runs inherit written out as direct formatting, for the
    /// scene reader, which reads direct w:pPr and w:rPr only. The frame itself when the document has no styles part or
    /// the frame no text box; otherwise a copy. A property is taken from the paragraph or run itself, then the character
    /// style and the styles it is based on, the paragraph style (w:pStyle, or the default paragraph style) and the styles
    /// that is based on, and the document defaults (<see cref="DocxDrawnStyles"/>). Only the properties the scene reader
    /// converts are resolved. w:ind, w:spacing, w:rFonts and w:lang are inherited attribute by attribute; the tab stops
    /// of all levels are merged by position, a nearer val="clear" removing a stop. Bold, italic and the strikes are toggle
    /// properties (ECMA-376 17.7.3): direct formatting sets them; without it the character and the paragraph style level
    /// are combined by exclusive or, and the document default counts when neither style level states the property.
    /// Table styles and numbering levels are not applied.
    /// </summary>
    private static XElement WithInheritedTextBoxFormatting(XElement frame, DocxDrawnStyles? styles)
    {
        if (styles is null || !frame.Descendants(W + "txbxContent").Any()) return frame;
        var copy = new XElement(frame);
        foreach (var paragraph in copy.Descendants(W + "p").Where(p => p.Ancestors(W + "txbxContent").Any()).ToArray())
        {
            foreach (var run in paragraph.Descendants(W + "r").Where(r => r.Ancestors(W + "p").FirstOrDefault() == paragraph).ToArray())
            {
                var (own, character, paragraphLevel, defaults) = styles.RunPropertyLevels(run, paragraph);
                XElement?[] nearestFirst = [own, .. character, .. paragraphLevel, defaults];
                var resolved = new XElement(W + "rPr", own?.Elements().Where(e => e.Name.Namespace != W || Array.IndexOf(SceneRunProperties, e.Name.LocalName) < 0));
                foreach (var name in SceneRunProperties)
                {
                    if (Array.IndexOf(SceneToggleProperties, name) >= 0)
                    {
                        var characterState = SceneToggle(character, name); var paragraphState = SceneToggle(paragraphLevel, name);
                        var state = SceneToggle([own], name) ?? (characterState is null && paragraphState is null ? SceneToggle([defaults], name)
                            : (characterState ?? false) ^ (paragraphState ?? false));
                        if (state is { } on) resolved.Add(new XElement(W + name, new XAttribute(W + "val", on ? "1" : "0")));
                    }
                    else if (InheritedSceneProperty(nearestFirst, name) is { } property) resolved.Add(property);
                }
                own?.Remove();
                if (resolved.HasElements) run.AddFirst(resolved);
            }
            var ownParagraph = paragraph.Element(W + "pPr");
            var levels = styles.ParagraphProperties(paragraph, null);
            var resolvedParagraph = new XElement(W + "pPr", ownParagraph?.Elements().Where(e => e.Name.Namespace != W || Array.IndexOf(SceneParagraphProperties, e.Name.LocalName) < 0));
            foreach (var name in SceneParagraphProperties)
                if ((name == "tabs" ? InheritedSceneTabs(levels) : InheritedSceneProperty(levels, name)) is { } property) resolvedParagraph.Add(property);
            ownParagraph?.Remove();
            if (resolvedParagraph.HasElements) paragraph.AddFirst(resolvedParagraph);
        }
        return copy;
    }

    // Whether the nearest set of a level that states the toggle property turns it on; null when none states it.
    private static bool? SceneToggle(IEnumerable<XElement?> level, string name) =>
        level.Select(item => item?.Element(W + name)).FirstOrDefault(item => item is not null) is { } toggle
            ? (string?)toggle.Attribute(W + "val") is not ("0" or "false" or "off") : null;

    // The property as the nearest level states it; for w:ind, w:spacing, w:rFonts and w:lang each attribute from the nearest
    // level that states it or an attribute that replaces it (left and start, hanging and firstLine, ascii and asciiTheme).
    private static XElement? InheritedSceneProperty(IReadOnlyList<XElement?> nearestFirst, string name)
    {
        var stated = nearestFirst.Select(level => level?.Element(W + name)).OfType<XElement>().ToArray();
        if (stated.Length == 0) return null;
        if (!SceneAttributeGroups.TryGetValue(name, out var groups)) return new XElement(stated[0]);
        var result = new XElement(W + name);
        foreach (var level in stated.Reverse())
            foreach (var attribute in level.Attributes().Where(a => !a.IsNamespaceDeclaration))
            {
                if (attribute.Name.Namespace == W && groups.FirstOrDefault(group => Array.IndexOf(group, attribute.Name.LocalName) >= 0) is { } replaced)
                    foreach (var other in replaced) result.Attribute(W + other)?.Remove();
                result.SetAttributeValue(attribute.Name, attribute.Value);
            }
        return result;
    }

    // The tab stops of all levels by position, the farthest level first: a nearer stop replaces one at its position and a
    // nearer val="clear" removes it.
    private static XElement? InheritedSceneTabs(IReadOnlyList<XElement> nearestFirst)
    {
        var stops = new SortedDictionary<double, XElement>();
        foreach (var level in nearestFirst.Reverse())
            foreach (var tab in level.Element(W + "tabs")?.Elements(W + "tab") ?? [])
            {
                if (!double.TryParse((string?)tab.Attribute(W + "pos"), NumberStyles.Float, CultureInfo.InvariantCulture, out var position) || !double.IsFinite(position)) continue;
                if ((string?)tab.Attribute(W + "val") == "clear") stops.Remove(position); else stops[position] = tab;
            }
        return stops.Count == 0 ? null : new XElement(W + "tabs", stops.Values.Select(tab => new XElement(tab)));
    }

    private static bool IsSourceDrawingObject(XElement element) => element.Name == WPS + "wsp" || element.Name == A + "sp" ||
        element.Name.LocalName is "pic" or "cxnSp" or "graphicFrame" || element.Name == C + "chart" && !element.Ancestors().Any(a => a.Name.LocalName == "graphicFrame");

    private static bool IsPlainSourcePicture(XElement element)
    {
        if (element.Name != XName.Get("pic", "http://schemas.openxmlformats.org/drawingml/2006/picture")) return false;
        if (element.Ancestors().Any(a => a.Name.LocalName is "grpSp" or "wgp")) return false;
        var crop = element.Descendants(A + "srcRect").FirstOrDefault();
        if (crop?.Attributes().Any(a => a.Value is not ("0" or "")) == true) return false;
        var transform = element.Descendants(A + "xfrm").FirstOrDefault();
        return transform is null ||
            (string?)transform.Attribute("rot") is null or "" or "0" &&
            (string?)transform.Attribute("flipH") is not ("1" or "true" or "on") &&
            (string?)transform.Attribute("flipV") is not ("1" or "true" or "on");
    }

    /// <summary>The alternative text of a picture inside a group or canvas: the frame's wp:docPr describes
    /// the whole group, so each member keeps the text of its own cNvPr. Null for a picture that is the frame.</summary>
    private static string? GroupedPictureAltText(XElement blip)
    {
        var picture = blip.Ancestors().FirstOrDefault(a => a.Name.LocalName == "pic");
        if (picture?.Ancestors().Any(a => a.Name.LocalName is "grpSp" or "wgp" or "wpc") != true) return null;
        return FirstNonEmptyAttribute(picture.Elements().SelectMany(e => e.Elements()).FirstOrDefault(e => e.Name.LocalName == "cNvPr"), "descr", "title");
    }

    private static string? SourceShapeIdentity(XElement element) => (string?)(element.Elements().FirstOrDefault(e => e.Name.LocalName == "cNvPr") ??
        element.Elements().Where(e => e.Name.LocalName.StartsWith("nv", StringComparison.Ordinal)).SelectMany(e => e.Elements()).FirstOrDefault(e => e.Name.LocalName == "cNvPr") ??
        element.Ancestors().FirstOrDefault(e => e.Name == WP + "inline" || e.Name == WP + "anchor")?.Element(WP + "docPr"))?.Attribute("id");

    private static double SourcePixels(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
        double.IsFinite(n) && Math.Abs(n) <= 952_500_000_000 ? n / 9525 : 0;
}
