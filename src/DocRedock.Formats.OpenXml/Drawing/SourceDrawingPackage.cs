using System.Xml;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.OpenXml.Drawing;

internal static class SourceDrawingPackage
{
    internal static XmlDocument Load(byte[] bytes)
    {
        var result = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
        using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
        result.Load(reader); return result;
    }

    /// <param name="chartPart">The package path of the chart part, and <paramref name="readPart"/> to read its
    /// relationships: together they resolve the pictures of the picture fills in the chart, which
    /// <paramref name="pictureFills"/> embeds under the limits of the document.</param>
    internal static DrawingChart? Chart(byte[] bytes, XmlDocument? theme, ICollection<string> warnings, string source,
        string? chartPart = null, Func<string, byte[]?>? readPart = null, DrawingMlPictureFills? pictureFills = null)
    {
        var parsed = DrawingChartReader.Read(bytes, theme, ChartPictures(chartPart, readPart, pictureFills));
        foreach (var warning in parsed.Warnings) warnings.Add("SourceDrawingScenePartial: " + source + ": " + warning);
        return parsed.Chart is {} chart ? chart with
        { Series = chart.Series.Select(s => s with
        {
            Color = s.Color?.StartsWith("scheme:", StringComparison.Ordinal) == true ? Scheme(s.Color[7..], theme, warnings, source) : s.Color,
            PointColors = s.PointColors.ToDictionary(k => k.Key, k => k.Value.StartsWith("scheme:", StringComparison.Ordinal)
                ? Scheme(k.Value[7..], theme, warnings, source) ?? "" : k.Value),
        }).ToArray() } : null;
    }

    /// <summary>
    /// Resolves the r:embed of a picture fill in a chart part through the relationships of that part
    /// (charts/_rels/chartN.xml.rels) to an embedded picture; null when the package cannot be read.
    /// </summary>
    private static DrawingMlStyleReader.DrawingImageResolver? ChartPictures(string? chartPart, Func<string, byte[]?>? readPart, DrawingMlPictureFills? pictureFills)
    {
        if (chartPart is not { Length: > 0 } || readPart is null) return null;
        chartPart = chartPart.TrimStart('/');
        var slash = chartPart.LastIndexOf('/');
        var folder = slash < 0 ? "" : chartPart[..slash];
        Dictionary<string, string>? targets = null;
        var fills = pictureFills ?? new DrawingMlPictureFills(readPart);
        return (string id, out string? reason) =>
        {
            reason = null;
            if (targets is null)
            {
                targets = new(StringComparer.Ordinal);
                try
                {
                    if (readPart((folder.Length > 0 ? folder + "/" : "") + "_rels/" + chartPart[(slash + 1)..] + ".rels") is { } relationships)
                        foreach (var relationship in Load(relationships).DocumentElement?.ChildNodes.OfType<XmlElement>() ?? [])
                            if (relationship.LocalName == "Relationship" && relationship.GetAttribute("TargetMode") != "External" &&
                                relationship.GetAttribute("Id") is { Length: > 0 } key && ResolveTarget(folder, relationship.GetAttribute("Target")) is { } target)
                                targets[key] = target;
                }
                catch (XmlException) { }
            }
            if (targets.TryGetValue(id, out var part)) return fills.Reference(part, out reason);
            reason = "its relationship is missing or external";
            return null;
        };
    }

    /// <summary>The package path (without a leading slash) of a relationship target of a part in <paramref name="folder"/>.</summary>
    private static string? ResolveTarget(string folder, string target)
    {
        if (target.Length is 0 or > 1024) return null;
        var segments = new List<string>();
        if (!target.StartsWith('/')) segments.AddRange(folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var segment in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment != "..") { segments.Add(segment); continue; }
            if (segments.Count == 0) return null;
            segments.RemoveAt(segments.Count - 1);
        }
        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    private static string? Scheme(string name, XmlDocument? theme, ICollection<string> warnings, string source)
    {
        var doc = new XmlDocument { XmlResolver = null };
        var shape = doc.CreateElement("sp"); doc.AppendChild(shape);
        var props = doc.CreateElement("spPr"); shape.AppendChild(props);
        var fill = doc.CreateElement("solidFill"); props.AppendChild(fill);
        var color = doc.CreateElement("schemeClr"); color.SetAttribute("val", name); fill.AppendChild(color);
        var result = DrawingMlStyleReader.Read(shape, theme);
        foreach (var warning in result.Warnings) warnings.Add("SourceDrawingScenePartial: " + source + ": " + warning);
        return result.FillPaint.Color;
    }
}
