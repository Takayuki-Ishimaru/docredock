using System.Xml;

namespace DocRedock.Formats.OpenXml.Drawing;

/// <summary>
/// What one shape takes from outside its own element. A PowerPoint placeholder inherits its frame,
/// shape properties and text body settings from the layout and master placeholder, and every text
/// body inherits list-level defaults. The source elements are never changed: the scene reader
/// applies the inherited values to its working copy of the shape.
/// </summary>
public sealed class DrawingMlInheritedShape
{
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const int MaxLevels = 9;

    /// <summary>Shapes this one inherits from, nearest first (layout placeholder, then master placeholder).</summary>
    public IReadOnlyList<XmlElement> Shapes { get; init; } = [];

    /// <summary>
    /// An a:lstStyle-shaped element of level defaults (see <see cref="MergeListStyles"/>). They rank
    /// below the shape's own style font reference, lstStyle, paragraph and run properties.
    /// </summary>
    public XmlElement? ListStyle { get; init; }

    /// <summary>False when fill, line and style reference stay where they are: PowerPoint does not paint an empty placeholder.</summary>
    public bool Paint { get; init; } = true;

    /// <summary>The nearest inherited a:xfrm, for a shape that has none of its own.</summary>
    public XmlElement? Frame => Shapes.Select(shape => Child(Child(shape, "spPr"), "xfrm")).FirstOrDefault(xfrm => xfrm is not null);

    /// <summary>
    /// Completes a working copy of the shape: geometry, fill, line, style reference, a:bodyPr attributes
    /// and autofit are taken from the nearest inherited shape that has them, when the copy has none.
    /// </summary>
    public void ApplyTo(XmlElement shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var document = shape.OwnerDocument!;
        var properties = Child(shape, "spPr");
        var body = Child(Child(shape, "txBody"), "bodyPr");
        foreach (var source in Shapes)
        {
            var inherited = Child(source, "spPr");
            XmlElement? Missing(Func<XmlElement?, XmlElement?> select) => select(properties) is null ? select(inherited) : null;
            foreach (var part in new[]
            {
                Missing(p => Child(p, "custGeom") ?? Child(p, "prstGeom")),
                Paint ? Missing(p => Elements(p).FirstOrDefault(e => RunGroup(e.LocalName) == "fill")) : null,
                Paint ? Missing(p => Child(p, "ln")) : null,
            })
            {
                if (part is null) continue;
                if (properties is null)
                {
                    properties = document.CreateElement(shape.Prefix, "spPr", shape.NamespaceURI);
                    shape.InsertAfter(properties, Elements(shape).FirstOrDefault(e => e.LocalName.StartsWith("nv", StringComparison.Ordinal)));
                }
                properties.AppendChild(document.ImportNode(part, true));
            }
            // An own a:ln that sets only some properties (a width, a dash) keeps the others of the inherited line.
            if (Paint && Child(properties, "ln") is { } line && Child(inherited, "ln") is { } inheritedLine)
            {
                foreach (XmlAttribute attribute in inheritedLine.Attributes)
                    if (attribute.NamespaceURI.Length == 0 && !line.HasAttribute(attribute.LocalName))
                        line.SetAttribute(attribute.LocalName, attribute.Value);
                foreach (var part in Elements(inheritedLine).Where(e => e.LocalName != "extLst").ToArray())
                    if (!Elements(line).Any(e => LineGroup(e.LocalName) == LineGroup(part.LocalName)))
                        line.InsertBefore(document.ImportNode(part, true), Child(line, "extLst"));
            }
            if (Paint && Child(shape, "style") is null && Child(source, "style") is { } style)
                shape.InsertAfter(document.ImportNode(style, true), properties);
            if (body is null || Child(Child(source, "txBody"), "bodyPr") is not { } inheritedBody) continue;
            foreach (XmlAttribute attribute in inheritedBody.Attributes)
                if (attribute.NamespaceURI.Length == 0 && !body.HasAttribute(attribute.LocalName))
                    body.SetAttribute(attribute.LocalName, attribute.Value);
            if (!Elements(body).Any(e => IsAutofit(e.LocalName)) && Elements(inheritedBody).FirstOrDefault(e => IsAutofit(e.LocalName)) is { } autofit)
                body.AppendChild(document.ImportNode(autofit, true));
        }
    }

    /// <summary>
    /// Flattens a:lstStyle-shaped elements (lvl1pPr..lvl9pPr children: a placeholder's a:lstStyle, a master's
    /// p:titleStyle/p:bodyStyle/p:otherStyle, p:defaultTextStyle) into one element, nearest first. A nearer
    /// level overrides attribute by attribute and, for child elements, choice by choice (bullet, bullet
    /// font/color/size, spacing, and inside a:defRPr fill, line and each script font).
    /// </summary>
    public static XmlElement? MergeListStyles(IEnumerable<XmlElement?> nearestFirst)
    {
        ArgumentNullException.ThrowIfNull(nearestFirst);
        var sources = nearestFirst.Where(source => source is not null).Select(source => source!).Reverse().ToArray();
        if (sources.Length == 0) return null;
        var document = new XmlDocument { XmlResolver = null };
        var result = document.CreateElement("a", "lstStyle", A);
        document.AppendChild(result);
        foreach (var source in sources)
            for (var level = 1; level <= MaxLevels; level++)
            {
                var name = "lvl" + level.ToString(System.Globalization.CultureInfo.InvariantCulture) + "pPr";
                if (Child(source, name) is not { } properties) continue;
                var target = Child(result, name);
                if (target is null) { target = document.CreateElement("a", name, A); result.AppendChild(target); }
                Overlay(target, properties, ParagraphGroup);
            }
        return result.HasChildNodes ? result : null;
    }

    private static void Overlay(XmlElement target, XmlElement source, Func<string, string> group)
    {
        var document = target.OwnerDocument!;
        foreach (XmlAttribute attribute in source.Attributes)
            if (attribute.NamespaceURI.Length == 0) target.SetAttribute(attribute.LocalName, attribute.Value);
        foreach (var child in Elements(source).ToArray())
        {
            if (child.LocalName == "extLst") continue;
            var key = group(child.LocalName);
            var existing = Elements(target).Where(e => group(e.LocalName) == key).ToArray();
            if (child.LocalName == "defRPr" && existing.FirstOrDefault(e => e.LocalName == "defRPr") is { } run)
            {
                Overlay(run, child, RunGroup);
                continue;
            }
            foreach (var replaced in existing) target.RemoveChild(replaced);
            target.AppendChild(document.ImportNode(child, true));
        }
    }

    private static string ParagraphGroup(string name) => name switch
    {
        "buNone" or "buAutoNum" or "buChar" or "buBlip" => "bullet",
        "buClrTx" or "buClr" => "bulletColor",
        "buSzTx" or "buSzPct" or "buSzPts" => "bulletSize",
        "buFontTx" or "buFont" => "bulletFont",
        _ => name,
    };

    private static string RunGroup(string name) => name switch
    {
        "noFill" or "solidFill" or "gradFill" or "blipFill" or "pattFill" or "grpFill" => "fill",
        "effectLst" or "effectDag" => "effect",
        "uLnTx" or "uLn" => "underlineLine",
        "uFillTx" or "uFill" => "underlineFill",
        _ => name,
    };

    private static string LineGroup(string name) => name switch
    {
        "prstDash" or "custDash" => "dash",
        "round" or "bevel" or "miter" => "join",
        _ => RunGroup(name),
    };

    private static bool IsAutofit(string name) => name is "noAutofit" or "normAutofit" or "spAutoFit";
    private static IEnumerable<XmlElement> Elements(XmlElement? parent) => parent?.ChildNodes.OfType<XmlElement>() ?? [];
    private static XmlElement? Child(XmlElement? parent, string name) => Elements(parent).FirstOrDefault(e => e.LocalName == name);
}
