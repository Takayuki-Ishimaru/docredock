using System.Globalization;
using System.Xml;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.OpenXml.Drawing;

/// <summary>Resolves source DrawingML fill, line and text styles for faithful diagrams.</summary>
public static partial class DrawingMlStyleReader
{
    private const int MaxRuns = 8192;
    private const int MaxParagraphs = 4096;
    private const int MaxStops = 256;
    private const int MaxTextLength = 262144;
    private const double MaxStrokeWidth = 1_000_000;

    /// <summary>Reads one shape element (spPr/style/txBody) against an optional theme part.</summary>
    /// <param name="linkResolver">Maps an a:hlinkClick r:id to a target the caller has vetted, or null to leave the label plain.</param>
    /// <param name="inheritedListStyle">An a:lstStyle-shaped element of level defaults from outside the shape (layout and master
    /// placeholder, master text styles, presentation defaults). They rank below the shape's style font reference and own lstStyle.</param>
    /// <param name="groupFill">The fill element an a:grpFill of this shape stands for, when the caller tracks the enclosing groups;
    /// without it the groups around <paramref name="shape"/> in its own document are searched.</param>
    /// <param name="imageResolver">Maps the r:embed of an a:blipFill to a picture reference (a data: URI), or to null with a
    /// reason for the warning. Without it a picture fill is reported as unsupported.</param>
    public static DrawingAppearance Read(XmlElement shape, XmlDocument? theme = null, Func<string, string?>? linkResolver = null,
        XmlElement? inheritedListStyle = null, XmlElement? groupFill = null, DrawingImageResolver? imageResolver = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var warnings = new List<string>();
        var ctx = new StyleContext(theme, warnings) { LinkResolver = linkResolver, ImageResolver = imageResolver };
        var properties = Child(shape, "spPr");
        var style = Child(shape, "style");
        var fillElement = FillElement(properties);
        // a:grpFill: the shape is painted with the fill of the group it belongs to; a group without one leaves it unfilled.
        var grouped = fillElement?.LocalName == "grpFill";
        if (grouped) fillElement = groupFill ?? GroupFill(shape);
        var fill = fillElement is null ? grouped ? new DrawingPaint() : ThemeFill(style, ctx) : Paint(fillElement, ctx);
        var line = Child(properties, "ln");
        var lineFill = FillElement(line);
        var stroke = lineFill is null ? ThemeStroke(style, ctx) : Paint(lineFill, ctx);
        var themeLine = ThemeLine(style, ctx);
        var body = Child(Child(shape, "txBody"), "bodyPr");
        var textLayout = DrawingTextLayoutReader.Read(body, warnings);
        if (Child(body, "prstTxWarp")?.GetAttribute("prst") is { Length: > 0 } warp && warp != "textNoShape")
            ctx.Warn("Unsupported text body warp setting; use source page rendering for its appearance.");
        var width = Number(line, "w") ?? ThemeLineWidth(style, ctx) ?? (stroke.Kind == "none" ? 0 : 9525);
        foreach (var effect in Elements(properties).Where(e => e.LocalName is "effectLst" or "effectDag" or "scene3d" or "sp3d"))
            if (effect.HasChildNodes || effect.HasAttributes) ctx.Warn("Unsupported shape effect " + effect.LocalName + "; source geometry and text retained.");
        return new DrawingAppearance
        {
            Dash = (Child(line, "prstDash") ?? Child(themeLine, "prstDash"))?.GetAttribute("val"),
            HeadArrow = (Child(line, "headEnd") ?? Child(themeLine, "headEnd"))?.GetAttribute("type"),
            TailArrow = (Child(line, "tailEnd") ?? Child(themeLine, "tailEnd"))?.GetAttribute("type"),
            HeadArrowWidth = EndSize(Child(line, "headEnd") ?? Child(themeLine, "headEnd"), "w"),
            HeadArrowLength = EndSize(Child(line, "headEnd") ?? Child(themeLine, "headEnd"), "len"),
            TailArrowWidth = EndSize(Child(line, "tailEnd") ?? Child(themeLine, "tailEnd"), "w"),
            TailArrowLength = EndSize(Child(line, "tailEnd") ?? Child(themeLine, "tailEnd"), "len"),
            FillPaint = fill,
            StrokePaint = stroke,
            StrokeWidth = width > 0 ? Math.Min(width / 9525d, MaxStrokeWidth) : 0,
            Paragraphs = ReadParagraphs(Child(shape, "txBody"), style, ctx,
                ctx.Link(Child(Elements(shape).FirstOrDefault(e => e.LocalName.StartsWith("nv", StringComparison.Ordinal)), "cNvPr")), inheritedListStyle),
            TextLayout = textLayout,
            Warnings = warnings,
        };
    }

    /// <summary>Resolves the relationship id of a picture fill; a null result names in <paramref name="reason"/> why it is not drawn.</summary>
    public delegate string? DrawingImageResolver(string relationshipId, out string? reason);

    private static string? EndSize(XmlElement? end, string name) => end?.GetAttribute(name) is "sm" or "med" or "lg" ? end.GetAttribute(name) : null;

    /// <summary>The fill of the nearest enclosing group that has one of its own; a group that again says a:grpFill passes the question up.</summary>
    private static XmlElement? GroupFill(XmlElement shape)
    {
        var depth = 0;
        for (var node = shape.ParentNode as XmlElement; node is not null && depth < 256; node = node.ParentNode as XmlElement, depth++)
        {
            if (node.LocalName is not ("grpSp" or "wgp")) continue;
            var fill = FillElement(Child(node, "grpSpPr"));
            if (fill?.LocalName != "grpFill") return fill;
        }
        return null;
    }

    private sealed class StyleContext(XmlDocument? theme, List<string> warnings)
    {
        public XmlDocument? Theme { get; } = theme;
        public List<string> Warnings { get; } = warnings;
        public Func<string, string?>? LinkResolver { get; init; }
        public DrawingImageResolver? ImageResolver { get; init; }
        private Dictionary<string, string>? _colorMap;
        private bool _colorMapRead;

        /// <summary>Target of the a:hlinkClick directly under a run or shape property element, when the caller resolves links.</summary>
        public string? Link(XmlElement? owner) => LinkResolver is null ? null
            : Child(owner, "hlinkClick")?.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships") is { Length: > 0 } id
                ? LinkResolver(id) : null;

        public void Warn(string message)
        {
            if (Warnings.Count < 64 && !Warnings.Contains(message, StringComparer.Ordinal))
            {
                Warnings.Add(message);
            }
        }

        /// <summary>oldClr/newClr pairs of an optional a:clrMap part; unknown names are ignored.</summary>
        public Dictionary<string, string> ColorMap()
        {
            if (!_colorMapRead)
            {
                _colorMapRead = true;
                var map = Descendant(Theme?.DocumentElement, "clrMap");
                if (map is not null)
                {
                    var result = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (XmlAttribute attribute in map.Attributes)
                        if (attribute.NamespaceURI.Length == 0 && attribute.LocalName.Length <= 32 && attribute.Value.Length is > 0 and <= 32)
                            result[attribute.LocalName] = attribute.Value;
                    foreach (var entry in map.ChildNodes.OfType<XmlElement>())
                    {
                        var baseName = entry.GetAttribute("oldClr");
                        var newName = entry.GetAttribute("newClr");
                        if (baseName.Length is > 0 and <= 32 && newName.Length is > 0 and <= 32)
                        {
                            result[baseName] = newName;
                        }
                    }
                    _colorMap = result;
                }
            }
            return _colorMap ?? EmptyMap;
        }

        private static readonly Dictionary<string, string> EmptyMap = new(StringComparer.Ordinal);
    }

    private static XmlElement? Child(XmlElement? parent, string name) => parent?.ChildNodes.OfType<XmlElement>()
        .FirstOrDefault(e => string.Equals(e.LocalName, name, StringComparison.Ordinal));

    private static IEnumerable<XmlElement> Children(XmlElement? parent, string name) => parent?.ChildNodes.OfType<XmlElement>()
        .Where(e => string.Equals(e.LocalName, name, StringComparison.Ordinal)) ?? [];

    private static IEnumerable<XmlElement> Elements(XmlElement? parent) => parent?.ChildNodes.OfType<XmlElement>() ?? [];

    /// <summary>Namespace-agnostic descendant lookup, relative to the given element.</summary>
    private static XmlElement? Descendant(XmlElement? parent, string name) =>
        parent?.SelectSingleNode(".//*[local-name()='" + name + "']") as XmlElement;

    private static double? Number(XmlElement? element, string name) =>
        element is not null && double.TryParse(element.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value) ? value : null;

    private static int? Integer(XmlElement? element, string name) =>
        element is not null && int.TryParse(element.GetAttribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : null;

    private static bool Flag(XmlElement? element, string name, bool fallback) => element?.GetAttribute(name) switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => fallback,
    };

    /// <summary>Attribute presence decides the override; absent attributes inherit the paragraph default.</summary>
    private static bool? OptionalFlag(XmlElement? element, string name) => element?.GetAttribute(name) switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => null,
    };

    private static double Clamp(double value, double minimum, double maximum) =>
        !double.IsFinite(value) ? minimum : Math.Min(Math.Max(value, minimum), maximum);

    private static int Byte(double channel) => (int)Math.Floor(Clamp(channel, 0, 255) + 0.5);

    private static readonly string[] FillNames = ["noFill", "solidFill", "gradFill", "blipFill", "pattFill", "grpFill"];

    private static XmlElement? FillElement(XmlElement? parent) => Elements(parent)
        .FirstOrDefault(e => Array.IndexOf(FillNames, e.LocalName) >= 0);

    private readonly record struct ColorValue(string Rgb, double Opacity);

    private static DrawingPaint Paint(XmlElement element, StyleContext ctx, XmlElement? placeholder = null)
    {
        if (element.LocalName == "noFill")
        {
            return new DrawingPaint();
        }
        if (element.LocalName == "gradFill")
        {
            return Gradient(element, ctx, placeholder);
        }
        if (element.LocalName == "solidFill")
        {
            var choice = FirstColor(element);
            if (choice is null)
            {
                ctx.Warn("Unsupported color: " + (Elements(element).FirstOrDefault()?.LocalName ?? "empty solidFill"));
                return new DrawingPaint();
            }
            var color = Color(choice, ctx, placeholder);
            return color is null ? new DrawingPaint()
                : new DrawingPaint { Kind = "solid", Color = color.Value.Rgb, Opacity = color.Value.Opacity };
        }
        if (element.LocalName == "pattFill")
        {
            return Pattern(element, ctx, placeholder);
        }
        if (element.LocalName == "blipFill" && ctx.ImageResolver is not null)
        {
            return Picture(element, ctx);
        }
        ctx.Warn("Unsupported fill: " + element.LocalName);
        return new DrawingPaint();
    }

    private static DrawingPaint Pattern(XmlElement element, StyleContext ctx, XmlElement? placeholder)
    {
        // Without a color the foreground is black and the background white, as in the pattern gallery.
        var fore = Color(FirstColor(Child(element, "fgClr")), ctx, placeholder) ?? new ColorValue("#000000", 1);
        var back = Color(FirstColor(Child(element, "bgClr")), ctx, placeholder) ?? new ColorValue("#FFFFFF", 1);
        var preset = element.GetAttribute("prst");
        if (DrawingPatternPresets.IsKnown(preset))
        {
            return new DrawingPaint { Kind = "pattern", Pattern = preset, Color = fore.Rgb, Opacity = fore.Opacity, BackColor = back.Rgb, BackOpacity = back.Opacity };
        }
        ctx.Warn("Unknown pattern fill preset '" + (preset.Length <= 32 ? preset : preset[..32]) + "'; drawn as an even blend of its foreground and background colors.");
        double Channel(int at) => (Convert.ToInt32(fore.Rgb.Substring(at, 2), 16) + Convert.ToInt32(back.Rgb.Substring(at, 2), 16)) / 2d;
        return new DrawingPaint
        {
            Kind = "solid", Color = "#" + string.Concat(Hex2(Byte(Channel(1))), Hex2(Byte(Channel(3))), Hex2(Byte(Channel(5)))),
            Opacity = (fore.Opacity + back.Opacity) / 2,
        };
    }

    private const string RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static DrawingPaint Picture(XmlElement element, StyleContext ctx)
    {
        var blip = Child(element, "blip");
        var id = blip?.GetAttribute("embed", RelationshipNamespace);
        if (string.IsNullOrEmpty(id))
        {
            ctx.Warn("Picture fill without an embedded picture (a linked picture is not followed); the shape is left unfilled.");
            return new DrawingPaint();
        }
        var reference = ctx.ImageResolver!(id, out var reason);
        if (reference is null)
        {
            ctx.Warn("Picture fill is not drawn: " + (reason ?? "its picture could not be read") + ".");
            return new DrawingPaint();
        }
        foreach (var effect in Elements(blip).Where(e => e.LocalName is not ("alphaModFix" or "extLst")))
            ctx.Warn("Picture fill effect " + effect.LocalName + " is not applied; the picture is drawn as saved.");
        static DrawingInsets? Insets(XmlElement? rect) => rect is null ? null : new(
            Clamp((Number(rect, "l") ?? 0) / 100000d, -1000, 1000), Clamp((Number(rect, "t") ?? 0) / 100000d, -1000, 1000),
            Clamp((Number(rect, "r") ?? 0) / 100000d, -1000, 1000), Clamp((Number(rect, "b") ?? 0) / 100000d, -1000, 1000));
        var tile = Child(element, "tile");
        const double maximum = 100_000_000;
        return new DrawingPaint
        {
            Kind = "picture", ImageReference = reference,
            Opacity = Number(Child(blip, "alphaModFix"), "amt") is { } amount ? Clamp(amount / 100000d, 0, 1) : 1,
            RotateWithShape = Flag(element, "rotWithShape", true),
            Crop = Insets(Child(element, "srcRect")),
            Stretch = tile is null ? Insets(Child(Child(element, "stretch"), "fillRect")) : null,
            Tile = tile is null ? null : new DrawingPictureTile(
                Clamp((Number(tile, "tx") ?? 0) / 9525d, -maximum, maximum), Clamp((Number(tile, "ty") ?? 0) / 9525d, -maximum, maximum),
                Clamp((Number(tile, "sx") ?? 100000) / 100000d, -maximum, maximum), Clamp((Number(tile, "sy") ?? 100000) / 100000d, -maximum, maximum))
            {
                Alignment = tile.GetAttribute("algn") is "t" or "tr" or "l" or "ctr" or "r" or "bl" or "b" or "br" ? tile.GetAttribute("algn") : "tl",
                Flip = tile.GetAttribute("flip") is "x" or "y" or "xy" ? tile.GetAttribute("flip") : "none",
            },
        };
    }

    private static XmlElement? FirstColor(XmlElement? parent) => Elements(parent)
        .FirstOrDefault(e => e.LocalName is "srgbClr" or "schemeClr" or "scrgbClr" or "sysClr" or "hslClr" or "prstClr");

    private static DrawingPaint Gradient(XmlElement element, StyleContext ctx, XmlElement? placeholder)
    {
        var stops = new List<DrawingGradientStop>();
        foreach (var stop in Children(Child(element, "gsLst"), "gs"))
        {
            if (stops.Count >= MaxStops)
            {
                ctx.Warn("Gradient stop limit exceeded");
                break;
            }
            var color = Color(FirstColor(stop), ctx, placeholder);
            if (color is null)
            {
                continue;
            }
            stops.Add(new DrawingGradientStop(Clamp((Number(stop, "pos") ?? 0) / 100000d, 0, 1), color.Value.Rgb, color.Value.Opacity));
        }
        if (stops.Count == 0)
        {
            ctx.Warn("Gradient without resolvable stops");
            return new DrawingPaint();
        }
        // A gsLst need not be saved in position order; SVG stop offsets must not decrease. The sort is stable.
        stops = stops.OrderBy(stop => stop.Position).ToList();
        if (Child(element, "path") is { } path)
        {
            // Position 0 lies at a:fillToRect (the focus, usually a point) and 100 % at the outline of the path:
            // the order of an SVG radial gradient. A rectangular or shape-following path has no SVG counterpart.
            if (path.GetAttribute("path") != "circle")
                ctx.Warn("Path gradient '" + (path.GetAttribute("path") is { Length: <= 16 } kind ? kind : "") + "' is drawn as a radial gradient; its bands follow an ellipse instead of the source outline.");
            var focus = Child(path, "fillToRect");
            return new DrawingPaint
            {
                Kind = "radial", Stops = stops, RotateWithShape = Flag(element, "rotWithShape", true),
                Focus = new(Clamp((Number(focus, "l") ?? 0) / 100000d, -10, 10), Clamp((Number(focus, "t") ?? 0) / 100000d, -10, 10),
                    Clamp((Number(focus, "r") ?? 0) / 100000d, -10, 10), Clamp((Number(focus, "b") ?? 0) / 100000d, -10, 10)),
            };
        }
        var direction = Child(element, "lin");
        var angle = Number(direction, "ang") ?? 0;
        return new DrawingPaint
        {
            Kind = "linear",
            Stops = stops,
            AngleDegrees = double.IsFinite(angle) ? angle / 60000d : 0,
            Scaled = Flag(direction, "scaled", false),
            RotateWithShape = Flag(element, "rotWithShape", true),
        };
    }

    private static ColorValue? Color(XmlElement? element, StyleContext ctx, XmlElement? placeholder = null)
    {
        if (element is null)
        {
            return null;
        }
        var scheme = element.LocalName == "schemeClr" ? SchemeColor(element.GetAttribute("val"), ctx, placeholder) : null;
        string? rgb = element.LocalName switch
        {
            "srgbClr" => Hex(element.GetAttribute("val")),
            "sysClr" => Hex(element.GetAttribute("lastClr")) ?? element.GetAttribute("val") switch
            {
                "windowText" => "#000000", "window" => "#FFFFFF", _ => null,
            },
            "scrgbClr" => Scrgb(element),
            "hslClr" => HslColor(element),
            "prstClr" => PresetColor(element.GetAttribute("val")),
            "schemeClr" => scheme?.Rgb,
            _ => null,
        };
        if (rgb is null)
        {
            ctx.Warn(element.LocalName is "srgbClr" or "scrgbClr" or "sysClr" or "schemeClr" or "hslClr" or "prstClr"
                ? "Unresolved color reference" : "Unsupported color: " + element.LocalName);
            return null;
        }
        double opacity = scheme?.Opacity ?? 1;
        foreach (var transform in Elements(element))
        {
            if (transform.LocalName is "comp" or "inv" or "gray")
            {
                // These transforms carry no val attribute.
                rgb = transform.LocalName switch
                {
                    "comp" => Hue(rgb, 180),
                    "inv" => Channels(rgb, channel => 255 - channel),
                    _ => Gray(rgb),
                };
                continue;
            }
            var amount = Number(transform, "val");
            if (amount is null)
            {
                ctx.Warn("Invalid color transform: " + transform.LocalName);
                continue;
            }
            var ratio = amount.Value / 100000d;
            switch (transform.LocalName)
            {
                case "alpha": opacity = Clamp(ratio, 0, 1); break;
                case "alphaMod": opacity = Clamp(opacity * ratio, 0, 1); break;
                case "alphaOff": opacity = Clamp(opacity + ratio, 0, 1); break;
                case "alphaRepl": opacity = Clamp(ratio, 0, 1); break;
                // Office mixes tint (with white) and shade (with black) in linear light, not on the sRGB values:
                // accent1 4F81BD shaded 50 % is the 385D8A outline of an Office 2010 shape and tinted 40 % the D0D8E8
                // band of its table styles. LibreOffice's import does the same (oox/source/drawingml/color.cxx: toCrgb()
                // before both transforms).
                case "tint": rgb = Channels(rgb, channel => 255 * SrgbTransfer(SrgbLinear(channel / 255) * ratio + (1 - ratio))); break;
                case "shade": rgb = Channels(rgb, channel => 255 * SrgbTransfer(SrgbLinear(channel / 255) * ratio)); break;
                case "lumMod": rgb = Lightness(rgb, LightnessOf(rgb) * ratio); break;
                case "lumOff": rgb = Lightness(rgb, LightnessOf(rgb) + ratio); break;
                case "satMod": rgb = Saturation(rgb, SaturationOf(rgb) * ratio); break;
                case "satOff": rgb = Saturation(rgb, SaturationOf(rgb) + ratio); break;
                case "lum": rgb = Lightness(rgb, ratio); break;
                case "sat": rgb = Saturation(rgb, ratio); break;
                case "hue": rgb = Hue(rgb, amount.Value / 60000d - Hsl(rgb).h * 360); break;
                case "hueOff": rgb = Hue(rgb, amount.Value / 60000d); break;
                case "hueMod": rgb = Hue(rgb, Hsl(rgb).h * 360 * (ratio - 1)); break;
                default: ctx.Warn("Unsupported color transform: " + transform.LocalName); break;
            }
        }
        return new ColorValue(rgb, Clamp(opacity, 0, 1));
    }

    private static string? Hex(string value) =>
        value is { Length: 6 } && int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number)
            ? "#" + number.ToString("X6", CultureInfo.InvariantCulture) : null;

    /// <summary>scrgbClr r/g/b are linear-light percentages (0..100000); 50000 maps to #BCBCBC per the sRGB transfer function.</summary>
    private static string? Scrgb(XmlElement element)
    {
        var channels = new List<string>(3);
        foreach (var name in new[] { "r", "g", "b" })
        {
            if (Number(element, name) is not double raw)
            {
                return null;
            }
            channels.Add(Hex2(Byte(SrgbTransfer(Clamp(raw, 0, 100000) / 100000d) * 255d)));
        }
        return "#" + string.Concat(channels);
    }

    private static double SrgbTransfer(double linear) =>
        linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1d / 2.4) - 0.055;

    private static double SrgbLinear(double encoded) =>
        encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);

    /// <summary>hslClr: hue in 1/60000 degree, sat and lum in 1/100000.</summary>
    private static string? HslColor(XmlElement element)
    {
        if (Number(element, "hue") is not double hue || Number(element, "sat") is not double sat
            || Number(element, "lum") is not double lum)
        {
            return null;
        }
        return Rgb((((hue / 60000d) % 360) + 360) % 360 / 360d, Clamp(sat / 100000d, 0, 1), Clamp(lum / 100000d, 0, 1));
    }

    /// <summary>prstClr names: CSS/OOXML known colors plus the valid dk/lt/med abbreviations and grey spelling.</summary>
    private static string? PresetColor(string name)
    {
        if (name.Length is < 2 or > 32)
        {
            return null;
        }
        foreach (var candidate in PresetNameCandidates(name))
        {
            var color = System.Drawing.Color.FromName(candidate);
            if (color.IsKnownColor && !color.IsSystemColor && color.A == 255)
            {
                return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);
            }
        }
        return null;
    }

    private static IEnumerable<string> PresetNameCandidates(string name)
    {
        yield return name;
        var expanded = name switch
        {
            { Length: > 2 } when name.StartsWith("dk", StringComparison.Ordinal) && char.IsUpper(name[2]) => "Dark" + name[2..],
            { Length: > 2 } when name.StartsWith("lt", StringComparison.Ordinal) && char.IsUpper(name[2]) => "Light" + name[2..],
            { Length: > 3 } when name.StartsWith("med", StringComparison.Ordinal) && char.IsUpper(name[3]) => "Medium" + name[3..],
            _ => name,
        };
        if (!string.Equals(expanded, name, StringComparison.Ordinal))
        {
            yield return expanded;
        }
        var greyless = expanded.Replace("Grey", "Gray", StringComparison.Ordinal);
        if (!string.Equals(greyless, expanded, StringComparison.Ordinal))
        {
            yield return greyless;
        }
    }

    private static string Hex2(int value) => value.ToString("X2", CultureInfo.InvariantCulture);

    private static string Channels(string rgb, Func<double, double> transform) =>
        "#" + string.Concat(
            Hex2(Byte(transform(Convert.ToInt32(rgb.Substring(1, 2), 16)))),
            Hex2(Byte(transform(Convert.ToInt32(rgb.Substring(3, 2), 16)))),
            Hex2(Byte(transform(Convert.ToInt32(rgb.Substring(5, 2), 16)))));

    private static (double h, double s, double l) Hsl(string rgb)
    {
        double Channel(int at) => Convert.ToInt32(rgb.Substring(at, 2), 16) / 255d;
        var (r, g, b) = (Channel(1), Channel(3), Channel(5));
        var max = Math.Max(Math.Max(r, g), b);
        var min = Math.Min(Math.Min(r, g), b);
        var lightness = (max + min) / 2;
        if (max == min)
        {
            return (0, 0, lightness);
        }
        var delta = max - min;
        var saturation = lightness > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        var hue = max == r ? (g - b) / delta + (g < b ? 6 : 0) : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
        return (hue / 6, saturation, lightness);
    }

    private static string Rgb(double h, double s, double l)
    {
        double Channel(double offset)
        {
            if (s == 0)
            {
                return l * 255;
            }
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            var t = ((h + offset) % 1 + 1) % 1;
            return 255 * (t < 1d / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2d / 3 ? p + (q - p) * (2d / 3 - t) * 6 : p);
        }
        return "#" + string.Concat(
            Hex2(Byte(Channel(1d / 3))), Hex2(Byte(Channel(0))), Hex2(Byte(Channel(-1d / 3))));
    }

    private static double LightnessOf(string rgb) => Hsl(rgb).l;

    private static double SaturationOf(string rgb) => Hsl(rgb).s;

    private static string Lightness(string rgb, double value)
    {
        var (h, s, l) = Hsl(rgb);
        return Rgb(h, s, Clamp(value, 0, 1));
    }

    private static string Saturation(string rgb, double value)
    {
        var (h, s, l) = Hsl(rgb);
        return Rgb(h, Clamp(value, 0, 1), l);
    }

    private static string Hue(string rgb, double degrees)
    {
        var (h, s, l) = Hsl(rgb);
        return Rgb((((h * 360 + degrees) % 360) + 360) % 360 / 360, s, l);
    }

    /// <summary>gray: one luminance value (Rec. 601 weights) on all three channels.</summary>
    private static string Gray(string rgb)
    {
        double Channel(int at) => Convert.ToInt32(rgb.Substring(at, 2), 16);
        var value = Hex2(Byte(0.299 * Channel(1) + 0.587 * Channel(3) + 0.114 * Channel(5)));
        return "#" + value + value + value;
    }

    private static ColorValue? SchemeColor(string name, StyleContext ctx, XmlElement? placeholder)
    {
        if (string.Equals(name, "phClr", StringComparison.Ordinal))
        {
            return placeholder is null ? null : Color(placeholder, ctx);
        }
        var scheme = Descendant(ctx.Theme?.DocumentElement, "clrScheme");
        var defaultName = name switch { "bg1" => "lt1", "tx1" => "dk1", "bg2" => "lt2", "tx2" => "dk2", _ => name };
        var entry = scheme is null ? null : Children(scheme, ctx.ColorMap().GetValueOrDefault(name, defaultName)).FirstOrDefault();
        if (entry is null)
        {
            return null;
        }
        return Color(Child(entry, "srgbClr") ?? Child(entry, "sysClr"), ctx);
    }

    private static DrawingPaint ThemeFill(XmlElement? style, StyleContext ctx)
    {
        var reference = Child(style, "fillRef");
        if (reference is null)
        {
            return new DrawingPaint();
        }
        var raw = Number(reference, "idx") ?? 0;
        if (raw == 0) return new DrawingPaint();
        var entry = ThemeEntry(Descendant(ctx.Theme?.DocumentElement, raw >= 1001 ? "bgFillStyleLst" : "fillStyleLst"),
            raw >= 1001 ? (int)raw - 1001 : (int)raw - 1);
        if (entry is null)
        {
            ctx.Warn("Unresolved fillRef idx " + raw.ToString(CultureInfo.InvariantCulture));
            return new DrawingPaint();
        }
        return Paint(entry, ctx, FirstColor(reference));
    }

    private static DrawingPaint ThemeStroke(XmlElement? style, StyleContext ctx)
    {
        var reference = Child(style, "lnRef");
        if (reference is null)
        {
            return new DrawingPaint();
        }
        var raw = Number(reference, "idx") ?? 0;
        if (raw == 0) return new DrawingPaint();
        var entry = ThemeEntry(Descendant(ctx.Theme?.DocumentElement, "lnStyleLst"), raw >= 1001 ? (int)raw - 1001 : (int)raw - 1);
        if (entry is null)
        {
            ctx.Warn("Unresolved lnRef idx " + raw.ToString(CultureInfo.InvariantCulture));
            return new DrawingPaint();
        }
        return Paint(FillElement(entry) ?? entry, ctx, FirstColor(reference));
    }

    private static double? ThemeLineWidth(XmlElement? style, StyleContext ctx)
    {
        var reference = Child(style, "lnRef");
        if (reference is null)
        {
            return null;
        }
        var raw = Number(reference, "idx") ?? 0;
        return Number(ThemeEntry(Descendant(ctx.Theme?.DocumentElement, "lnStyleLst"), raw >= 1001 ? (int)raw - 1001 : (int)raw - 1), "w");
    }

    private static XmlElement? ThemeLine(XmlElement? style, StyleContext ctx)
    {
        var raw = Number(Child(style, "lnRef"), "idx") ?? 0;
        return ThemeEntry(Descendant(ctx.Theme?.DocumentElement, "lnStyleLst"), raw >= 1001 ? (int)raw - 1001 : (int)raw - 1);
    }

    private static XmlElement? ThemeEntry(XmlElement? list, int index) =>
        list is null || index < 0 ? null : Elements(list).ElementAtOrDefault(index);

    private sealed record RunStyle(string? FontFamily, double? FontSize, ColorValue? Color,
        bool? Bold, bool? Italic, bool? Underline, bool? Strike)
    {
        public string? EastAsianFont { get; init; }
        public string? ComplexScriptFont { get; init; }
        public string? Language { get; init; }
        public string? AlternateLanguage { get; init; }
        public double? Baseline { get; init; }
    }

    private static IReadOnlyList<DrawingTextParagraph> ReadParagraphs(XmlElement? body, XmlElement? style, StyleContext ctx, string? shapeLink,
        XmlElement? inheritedListStyle)
    {
        var paragraphs = new List<DrawingTextParagraph>();
        var reference = FontReference(style, ctx);
        // Text that no layer gives a font is set in the theme's minor (body) font of its script, as +mn-lt, +mn-ea and +mn-cs name it.
        var themeFont = Descendant(ctx.Theme?.DocumentElement, "minorFont") is { } minor
            ? new RunStyle(Typeface(minor), null, null, null, null, null, null) { EastAsianFont = "+mn-ea", ComplexScriptFont = "+mn-cs" } : null;
        // normAutofit records the shrink the source application applied to every run of the body.
        var fontScale = Number(Child(Child(body, "bodyPr"), "normAutofit"), "fontScale") is > 0 and < 100000 and var saved ? saved / 100000d : 1;
        // ... and the share it took off every paragraph's proportional line spacing.
        var lineReduction = Number(Child(Child(body, "bodyPr"), "normAutofit"), "lnSpcReduction") is > 0 and < 100000 and var reduced ? reduced / 100000d : 0;
        var runCount = 0;
        var textLength = 0;
        foreach (var paragraph in Children(body, "p"))
        {
            if (paragraphs.Count >= MaxParagraphs)
            {
                ctx.Warn("Paragraph limit exceeded");
                break;
            }
            var properties = Child(paragraph, "pPr");
            var level = Math.Clamp(Integer(properties, "lvl") ?? 0, 0, 8);
            var listProperties = Child(Child(body, "lstStyle"), "lvl" + (level + 1).ToString(CultureInfo.InvariantCulture) + "pPr");
            var inheritedProperties = Child(inheritedListStyle, "lvl" + (level + 1).ToString(CultureInfo.InvariantCulture) + "pPr");
            var defaults = Merge(reference, Merge(RunStyleOf(Child(listProperties, "defRPr"), ctx),
                Merge(RunStyleOf(Child(properties, "defRPr"), ctx), RunStyleOf(properties, ctx))));
            if (RunStyleOf(Child(inheritedProperties, "defRPr"), ctx) is { } inheritedRun)
            {
                // A style font reference names a typeface and a color only; weight and slant stay inherited.
                var below = reference is null ? inheritedRun
                    : Merge(inheritedRun, reference with { Bold = null, Italic = null, Underline = null, Strike = null });
                defaults = Merge(below, Layer(RunStyleOf(Child(listProperties, "defRPr"), ctx),
                    Layer(RunStyleOf(Child(properties, "defRPr"), ctx), RunStyleOf(properties, ctx))));
            }
            if (themeFont is not null) defaults = Merge(themeFont, defaults);
            var runs = new List<DrawingTextRun>();
            foreach (var node in Elements(paragraph))
            {
                if (node.LocalName is not ("r" or "fld" or "br"))
                {
                    continue;
                }
                if (runCount >= MaxRuns || textLength >= MaxTextLength)
                {
                    ctx.Warn("Text run limit exceeded");
                    continue;
                }
                var text = node.LocalName == "br" ? "\n" : string.Concat(Children(node, "t").Select(t => t.InnerText));
                runCount++;
                textLength += text.Length;
                var segments = BuildScriptRuns(Merge(defaults, RunStyleOf(Child(node, "rPr"), ctx)), text, ctx);
                if (node.LocalName != "br" && (ctx.Link(Child(node, "rPr")) ?? shapeLink) is { } link)
                    segments = segments.Select(segment => segment with { LinkTarget = link }).ToArray();
                if (runs.Count + segments.Count > MaxRuns)
                {
                    ctx.Warn("Script font segmentation limit exceeded; source text retained in its base typeface.");
                    if (runs.Count >= MaxRuns)
                        runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
                    else runs.Add(Build(Merge(defaults, RunStyleOf(Child(node, "rPr"), ctx)), text));
                }
                else runs.AddRange(segments);
            }
            // The bullet is one choice (buNone, buChar, buAutoNum or buBlip), made by the nearest layer that makes it: the
            // paragraph, then the shape's own list style. An inherited level decides only when neither does and, as in
            // PowerPoint, only for a paragraph that has text.
            var bulletChoice = LayerChild([properties, listProperties], BulletChoices)
                ?? (runs.Any(run => run.Text.Trim('\n').Length > 0) ? LayerChild([inheritedProperties], BulletChoices) : null);
            var marker = bulletChoice?.LocalName == "buChar" ? bulletChoice.GetAttribute("char") : null;
            if (bulletChoice?.LocalName == "buAutoNum")
            {
                marker = (Integer(bulletChoice, "startAt") ?? 1).ToString(CultureInfo.InvariantCulture) + ".";
                ctx.Warn("Automatic list numbering uses saved paragraph metadata when available.");
            }
            string? bulletImage = null;
            if (bulletChoice?.LocalName == "buBlip")
            {
                // The round bullet stands for the picture in plain text and wherever the picture cannot be drawn.
                marker = "\u2022";
                bulletImage = BulletPicture(bulletChoice, ctx);
            }
            if (fontScale != 1)
                for (var index = 0; index < runs.Count; index++) runs[index] = runs[index] with { FontSize = runs[index].FontSize * fontScale };
            // Paragraph properties resolve value by value: the paragraph, the shape's own list style level, the inherited level.
            XmlElement?[] layers = [properties, listProperties, inheritedProperties];
            var size = runs.Count > 0 ? runs[0].FontSize : Build(Merge(defaults, RunStyleOf(Child(paragraph, "endParaRPr"), ctx)), "").FontSize * fontScale;
            // ECMA-376 21.1.2.2.11: percent spacing follows the largest text size of the paragraph.
            var largest = runs.Count > 0 ? runs.Max(run => run.FontSize) : size;
            var bulletColor = LayerChild(layers, "buClr", "buClrTx") is { LocalName: "buClr" } bulletColorElement
                ? Color(FirstColor(bulletColorElement), ctx) : null;
            var lineSpacing = LayerChild(layers, "lnSpc");
            var linePercent = Number(Child(lineSpacing, "spcPct"), "val") is { } percent ? Clamp(percent / 1000d, 0, 13200)
                : Child(lineSpacing, "spcPts") is null && lineReduction > 0 ? 100 : (double?)null;
            // A Word text box's lineRule atLeast arrives as a:spcPts marked atLeast (DrawingMlSceneReader.ConvertWordParagraphLayout).
            var atLeast = Child(lineSpacing, "spcPts")?.GetAttribute("atLeast") == "1";
            paragraphs.Add(new DrawingTextParagraph(runs, layers.Select(layer => Nonempty(layer?.GetAttribute("algn"))).FirstOrDefault(value => value is not null))
            {
                BulletText = marker, Level = level,
                MarginLeft = LayerNumber(layers, "marL") is { } margin ? Clamp(margin / 9525d, 0, MaxTextMargin) : null,
                Indent = LayerNumber(layers, "indent") is { } indent ? Clamp(indent / 9525d, -MaxTextMargin, MaxTextMargin) : null,
                LineSpacingPercent = linePercent * (1 - lineReduction),
                LineSpacingExact = !atLeast && Number(Child(lineSpacing, "spcPts"), "val") is { } exact ? Clamp(exact / 100 * 96 / 72, 0, 2112) : null,
                LineSpacingAtLeast = atLeast && Number(Child(lineSpacing, "spcPts"), "val") is { } minimum ? Clamp(minimum / 100 * 96 / 72, 0, 2112) : null,
                SpaceBefore = Spacing(LayerChild(layers, "spcBef"), largest),
                SpaceAfter = Spacing(LayerChild(layers, "spcAft"), largest),
                MarginRight = LayerNumber(layers, "marR") is { } marginRight ? Clamp(marginRight / 9525d, 0, MaxTextMargin) : null,
                RightToLeft = layers.Select(layer => OptionalFlag(layer, "rtl")).FirstOrDefault(value => value is not null) ?? false,
                FontAlignment = layers.Select(layer => Nonempty(layer?.GetAttribute("fontAlgn"))).FirstOrDefault(value => value is not null)
                    is ("t" or "ctr" or "b") and var fontAlignment ? fontAlignment : null,
                TabStops = TabStops(LayerChild(layers, "tabLst")),
                DefaultTabSize = LayerNumber(layers, "defTabSz") is > 0 and var tabSize ? Clamp(tabSize / 9525d, 0.01, MaxTextMargin) : null,
                // Bullet properties are kept without a marker too: a PowerPoint adapter supplies automatic numbers afterwards.
                BulletFontFamily = ResolveTypeface(LayerChild(layers, "buFont", "buFontTx")?.GetAttribute("typeface"), ctx),
                BulletColor = bulletColor?.Rgb,
                BulletOpacity = bulletColor is { Opacity: < 1 } translucent ? translucent.Opacity : null,
                BulletImage = bulletImage,
                BulletSize = LayerChild(layers, "buSzPct", "buSzPts", "buSzTx") switch
                {
                    { LocalName: "buSzPct" } relative when Number(relative, "val") is { } share => Clamp(share / 100000d * size, 0.01, MaxBulletSize),
                    { LocalName: "buSzPts" } absolute when Number(absolute, "val") is { } points => Clamp(points / 100 * 96 / 72, 0.01, MaxBulletSize),
                    // A picture bullet without a size of its own is as high as the text.
                    _ => bulletImage is null ? null : Clamp(size, 0.01, MaxBulletSize),
                },
            });
        }
        return paragraphs;
    }

    private const double MaxTextMargin = 5376, MaxBulletSize = 5334;

    private static double? LayerNumber(XmlElement?[] layers, string name) =>
        layers.Select(layer => Number(layer, name)).FirstOrDefault(value => value is not null);

    /// <summary>The nearest layer's choice among alternative child elements; a nearer buClrTx hides an inherited buClr.</summary>
    private static XmlElement? LayerChild(XmlElement?[] layers, params string[] names) =>
        layers.Select(layer => Elements(layer).FirstOrDefault(e => Array.IndexOf(names, e.LocalName) >= 0)).FirstOrDefault(e => e is not null);

    /// <summary>
    /// a:spcBef / a:spcAft in CSS pixels: a:spcPts is absolute, a:spcPct a share of one line of the paragraph's text.
    /// PowerPoint sets and reports this value in lines (ParagraphFormat.SpaceBefore and SpaceAfter with LineRuleBefore and
    /// LineRuleAfter: 0.5 lines is spcPct 50000), and one line is its single spacing, 1.2 times the font size, as for lnSpc.
    /// ECMA-376 21.1.2.2.11 names the largest text size of the paragraph as the size the percentage follows.
    /// </summary>
    private static double? Spacing(XmlElement? spacing, double size) =>
        Number(Child(spacing, "spcPts"), "val") is { } points ? Clamp(points / 100 * 96 / 72, 0, 2112)
        : Number(Child(spacing, "spcPct"), "val") is { } percent ? Clamp(percent / 100000d * size * SingleLineHeight, 0, 100_000) : null;

    private const double SingleLineHeight = 1.2;
    private const int MaxTabStops = 32;
    private static readonly string[] BulletChoices = ["buNone", "buAutoNum", "buChar", "buBlip"];

    /// <summary>a:tabLst (ECMA-376 21.1.2.2.14, at most 32 stops) in ascending position; null without a usable stop.</summary>
    private static DrawingTabStop[]? TabStops(XmlElement? list)
    {
        var stops = Children(list, "tab").Take(MaxTabStops).Where(tab => Number(tab, "pos") is >= 0)
            .Select(tab => new DrawingTabStop(Clamp(Number(tab, "pos")!.Value / 9525d, 0, MaxTextMargin),
                tab.GetAttribute("algn") is "ctr" or "r" or "dec" ? tab.GetAttribute("algn") : "l"))
            .OrderBy(stop => stop.Position).ToArray();
        return stops.Length == 0 ? null : stops;
    }

    /// <summary>The picture of an a:buBlip through the resolver of picture fills, under the same limits; null with a warning.</summary>
    private static string? BulletPicture(XmlElement bullet, StyleContext ctx)
    {
        var id = Child(bullet, "blip")?.GetAttribute("embed", RelationshipNamespace);
        var reason = string.IsNullOrEmpty(id) ? "it is not embedded" : ctx.ImageResolver is null ? "this reader has no access to the pictures of the package" : null;
        var reference = reason is null ? ctx.ImageResolver!(id!, out reason) : null;
        if (reference is null) ctx.Warn("Picture bullet is drawn as a round bullet: " + (reason ?? "its picture could not be read") + ".");
        return reference;
    }

    private static RunStyle? RunStyleOf(XmlElement? properties, StyleContext ctx)
    {
        if (properties is null)
        {
            return null;
        }
        double? size = null;
        if (Number(properties, "sz") is double points)
        {
            size = points / 100 * 96 / 72;
        }
        else if (Descendant(properties, "normAutofit") is { } autofit && Number(autofit, "fontScale") is { } scale)
        {
            size = scale / 100000 * 14.6667;
        }
        return new RunStyle(
            ResolveTypeface(Child(properties, "latin")?.GetAttribute("typeface"), ctx),
            size,
            Color(FirstColor(FillElement(properties)), ctx),
            OptionalFlag(properties, "b"),
            OptionalFlag(properties, "i"),
            properties.GetAttribute("u") is { Length: > 0 } underline ? !string.Equals(underline, "none", StringComparison.Ordinal) : null,
            properties.GetAttribute("strike") is { Length: > 0 } strike ? !string.Equals(strike, "noStrike", StringComparison.Ordinal) : null)
        {
            EastAsianFont = Nonempty(Child(properties, "ea")?.GetAttribute("typeface")),
            ComplexScriptFont = Nonempty(Child(properties, "cs")?.GetAttribute("typeface")),
            Language = Nonempty(properties.GetAttribute("lang")) ?? Nonempty(properties.GetAttribute("altLang")),
            AlternateLanguage = Nonempty(properties.GetAttribute("altLang")),
            Baseline = Number(properties, "baseline") is { } baseline ? Clamp(baseline / 1000d, -100, 100) : null,
        };
    }

    private static RunStyle Merge(RunStyle? baseStyle, RunStyle? overrideStyle) =>
        overrideStyle is null ? baseStyle ?? EmptyStyle : baseStyle is null ? overrideStyle : overrideStyle with
        {
            FontFamily = overrideStyle.FontFamily ?? baseStyle.FontFamily,
            FontSize = overrideStyle.FontSize ?? baseStyle.FontSize,
            Color = overrideStyle.Color ?? baseStyle.Color,
            Bold = overrideStyle.Bold ?? baseStyle.Bold,
            Italic = overrideStyle.Italic ?? baseStyle.Italic,
            Underline = overrideStyle.Underline ?? baseStyle.Underline,
            Strike = overrideStyle.Strike ?? baseStyle.Strike,
            EastAsianFont = overrideStyle.EastAsianFont ?? baseStyle.EastAsianFont,
            ComplexScriptFont = overrideStyle.ComplexScriptFont ?? baseStyle.ComplexScriptFont,
            Language = overrideStyle.Language ?? baseStyle.Language,
            AlternateLanguage = overrideStyle.AlternateLanguage ?? baseStyle.AlternateLanguage,
            Baseline = overrideStyle.Baseline ?? baseStyle.Baseline,
        };

    /// <summary>As <see cref="Merge"/>, but an absent layer stays absent, so that unset flags remain open to a lower layer.</summary>
    private static RunStyle? Layer(RunStyle? baseStyle, RunStyle? overrideStyle) =>
        baseStyle is null ? overrideStyle : overrideStyle is null ? baseStyle : Merge(baseStyle, overrideStyle);

    private static readonly RunStyle EmptyStyle = new(null, null, null, false, false, false, false);

    private static DrawingTextRun Build(RunStyle? style, string text)
    {
        var resolved = Merge(EmptyStyle, style);
        return new DrawingTextRun
        {
            Text = text,
            FontFamily = resolved.FontFamily ?? "Calibri",
            FontSize = resolved.FontSize ?? 14.6667,
            Color = resolved.Color?.Rgb ?? "#000000",
            Opacity = resolved.Color?.Opacity ?? 1,
            Bold = resolved.Bold ?? false,
            Italic = resolved.Italic ?? false,
            Underline = resolved.Underline ?? false,
            Strike = resolved.Strike ?? false,
            Baseline = resolved.Baseline ?? 0,
        };
    }

    private static RunStyle? FontReference(XmlElement? style, StyleContext ctx)
    {
        var reference = Child(style, "fontRef");
        if (reference is null)
        {
            return null;
        }
        var kind = reference.GetAttribute("idx") is "major" or "+mj" ? "majorFont" : "minorFont";
        var color = Color(FirstColor(reference), ctx);
        return new RunStyle(Typeface(Descendant(ctx.Theme?.DocumentElement, kind)), null, color, false, false, false, false)
        {
            EastAsianFont = kind == "majorFont" ? "+mj-ea" : "+mn-ea",
            ComplexScriptFont = kind == "majorFont" ? "+mj-cs" : "+mn-cs",
        };
    }

    private static string? Typeface(XmlElement? font) => Child(font, "latin")?.GetAttribute("typeface") is { Length: > 0 } typeface
        ? typeface : null;

    /// <param name="scriptOnly">For an East Asian or complex-script theme reference: null when the theme names no typeface
    /// for that script, instead of the theme's Latin typeface.</param>
    private static string? ResolveTypeface(string? typeface, StyleContext ctx, string? language = null, bool scriptOnly = false)
    {
        if (string.IsNullOrEmpty(typeface)) return null;
        if (!typeface.StartsWith("+mj-", StringComparison.Ordinal) && !typeface.StartsWith("+mn-", StringComparison.Ordinal)) return typeface;
        var font = Descendant(ctx.Theme?.DocumentElement, typeface.StartsWith("+mj-", StringComparison.Ordinal) ? "majorFont" : "minorFont");
        var script = typeface.EndsWith("-ea", StringComparison.Ordinal) ? "ea" : typeface.EndsWith("-cs", StringComparison.Ordinal) ? "cs" : "latin";
        var resolved = Child(font, script)?.GetAttribute("typeface");
        if (string.IsNullOrEmpty(resolved) && script != "latin" && SupplementalScript(language) is {} supplemental)
            resolved = Children(font, "font").FirstOrDefault(e => e.GetAttribute("script") == supplemental)?.GetAttribute("typeface");
        if (string.IsNullOrEmpty(resolved) && scriptOnly && script != "latin") return null;
        if (string.IsNullOrEmpty(resolved)) resolved = Typeface(font);
        if (!string.IsNullOrEmpty(resolved)) return resolved;
        ctx.Warn("Unresolved theme font " + typeface + "; default font used.");
        return null;
    }
}
