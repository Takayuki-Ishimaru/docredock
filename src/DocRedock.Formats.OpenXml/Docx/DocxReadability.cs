using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.OpenXml.Docx;

/// <summary>Read-only heading evidence and source display sizes; never rewrites OOXML.</summary>
internal sealed class DocxReadability
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    private readonly Dictionary<string, XElement> styles;
    private readonly XElement? defaults;
    private readonly string? defaultStyle;
    private readonly Dictionary<XElement, ParagraphAppearance> appearances;
    private readonly double bodySize;
    private readonly XElement? firstParagraph;
    private readonly bool repeatedNumberedHeadings;

    public DocxReadability(XDocument? styleDocument, IEnumerable<XElement> blocks, Func<XElement, bool> hidden)
    {
        styles = (styleDocument?.Root?.Elements(W + "style") ?? []).Where(s => s.Attribute(W + "styleId") is not null)
            .GroupBy(s => (string)s.Attribute(W + "styleId")!).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        defaults = styleDocument?.Root?.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr");
        defaultStyle = styles.FirstOrDefault(s => (string?)s.Value.Attribute(W + "type") == "paragraph" &&
            On((string?)s.Value.Attribute(W + "default"))).Key;
        appearances = blocks.Where(p => p.Name == W + "p").ToDictionary(p => p, p => Appearance(p, hidden));
        firstParagraph = appearances.FirstOrDefault(p => p.Value.Text.Length > 0).Key;
        var body = appearances.Values.Where(p => p.Text.Length >= 80).Select(p => p.Size).OrderBy(s => s).ToArray();
        bodySize = body.Length > 0 ? body[body.Length / 2] : Size([defaults]) ?? 11;
        var bodyParagraphs = appearances.Where(p => p.Value.Text.Length >= 80).ToArray();
        var indentedBody = bodyParagraphs.Length >= 2 && bodyParagraphs.Count(p => Indented(p.Key)) > bodyParagraphs.Length / 2;
        repeatedNumberedHeadings = appearances.Count(p => ShortHeading(p.Value.Text) &&
            NumberedLevel(p.Value.Text) > 0 && !Indented(p.Key) && p.Value.Size >= bodySize &&
            (p.Value.Bold || p.Value.Size >= bodySize * 1.2 || indentedBody) &&
            p.Key.Element(W + "pPr")?.Element(W + "numPr") is null) >= 2;
    }

    public DocumentNode ApplyHeading(XElement paragraph, DocumentNode node)
    {
        if (node.Layer != ContentLayer.Body || node.Kind is not (NodeKind.Paragraph or NodeKind.Heading) ||
            !appearances.TryGetValue(paragraph, out var appearance) || appearance.Text.Length == 0) return node;
        var chain = StyleChain((string?)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")).ToArray();
        var outline = paragraph.Element(W + "pPr")?.Element(W + "outlineLvl") ??
            chain.Reverse().Select(s => s.Element(W + "pPr")?.Element(W + "outlineLvl")).FirstOrDefault(p => p is not null);
        var hasOutline = outline is not null;
        var validOutline = int.TryParse((string?)outline?.Attribute(W + "val"), out var outlineLevel);
        var title = chain.Any(s => string.Equals((string?)s.Element(W + "name")?.Attribute(W + "val"), "Title", StringComparison.OrdinalIgnoreCase));
        var level = hasOutline ? validOutline && outlineLevel is >= 0 and <= 8 ? outlineLevel + 1 : 0 :
            chain.Reverse().Select(s => NamedHeadingLevel((string?)s.Element(W + "name")?.Attribute(W + "val"))).FirstOrDefault(n => n > 0);
        var inferred = false;
        if (hasOutline && level == 0) title = false;
        var excludedStyle = chain.Any(s => Regex.IsMatch((string?)s.Element(W + "name")?.Attribute(W + "val") ?? "",
            @"(?:^toc|table of|caption|subtitle|quote|list|code|source)", RegexOptions.IgnoreCase));
        if (level == 0 && !title && !hasOutline && !excludedStyle && ShortHeading(appearance.Text) &&
            !paragraph.Descendants(W + "fldChar").Any() && !paragraph.Descendants(W + "instrText").Any() &&
            !paragraph.Descendants(W + "tab").Any() && !Indented(paragraph))
        {
            var centered = (string?)paragraph.Element(W + "pPr")?.Element(W + "jc")?.Attribute(W + "val") == "center";
            title = paragraph == firstParagraph && centered && appearance.Size >= 16 && appearance.Size >= bodySize * 1.35;
            var numbered = NumberedLevel(appearance.Text);
            if (!title && repeatedNumberedHeadings && numbered > 0 && appearance.Size >= bodySize) level = numbered;
            else if (!title && appearance.Bold && appearance.Size >= bodySize * 1.2) level = 1;
            inferred = title || level > 0;
        }
        if (level == 0 && !title && !hasOutline) return node;
        var extensions = node.Extensions?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) ?? [];
        extensions.Remove("heading_level"); extensions.Remove("document_title");
        if (level > 0) extensions["heading_level"] = JsonSerializer.SerializeToElement(level);
        if (title) extensions["document_title"] = JsonSerializer.SerializeToElement(true);
        if (inferred) extensions["heading_inferred"] = JsonSerializer.SerializeToElement(true);
        return node with
        {
            Kind = level > 0 || title ? NodeKind.Heading : NodeKind.Paragraph,
            Extensions = extensions,
            Provenance = inferred ? (node.Provenance ?? []).Concat([new ProvenanceItem(EvidenceKind.LayoutInferred, .85, "docx-readability")]).ToArray() : node.Provenance
        };
    }

    private ParagraphAppearance Appearance(XElement paragraph, Func<XElement, bool> hidden)
    {
        var paragraphStyle = (string?)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val");
        var baseProperties = new[] { defaults }.Concat(StyleChain(defaultStyle).Select(s => s.Element(W + "rPr")))
            .Concat(StyleChain(paragraphStyle).Select(s => s.Element(W + "rPr"))).ToArray();
        var visibleRuns = paragraph.Descendants(W + "r").Where(r => !hidden(r) &&
            !r.Ancestors(W + "txbxContent").Any() && !r.Ancestors(W + "del").Any()).ToArray();
        var sizes = new List<(double Size, int Length)>(); var boldLength = 0; var totalLength = 0; var text = new StringBuilder();
        foreach (var run in visibleRuns)
        {
            var value = string.Concat(run.Descendants(W + "t").Select(t => t.Value));
            var length = value.Count(c => !char.IsWhiteSpace(c));
            if (length == 0) continue;
            var runProperties = run.Element(W + "rPr");
            var properties = baseProperties.Concat(StyleChain((string?)runProperties?.Element(W + "rStyle")?.Attribute(W + "val")).Select(s => s.Element(W + "rPr")))
                .Append(runProperties).ToArray();
            sizes.Add((Size(properties) ?? 11, length));
            if (Toggle(properties, "b")) boldLength += length;
            totalLength += length; text.Append(value);
        }
        var median = sizes.OrderBy(s => s.Size).FirstOrDefault(); var cumulative = 0;
        foreach (var item in sizes.OrderBy(s => s.Size)) { cumulative += item.Length; if (cumulative * 2 >= totalLength) { median = item; break; } }
        return new(text.ToString().Trim(), median.Size > 0 ? median.Size : 11, totalLength > 0 && boldLength >= totalLength * .9);
    }

    private IEnumerable<XElement> StyleChain(string? id)
    {
        var chain = new List<XElement>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        while (id is not null && chain.Count < 32 && seen.Add(id) && styles.TryGetValue(id, out var style))
        { chain.Add(style); id = (string?)style.Element(W + "basedOn")?.Attribute(W + "val"); }
        return chain.AsEnumerable().Reverse();
    }
    private static double? Size(IEnumerable<XElement?> properties)
    {
        var raw = properties.Select(p => (string?)p?.Element(W + "sz")?.Attribute(W + "val")).LastOrDefault(v => v is not null);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) && double.IsFinite(size) && size is > 0 and <= 2000 ? size / 2 : null;
    }
    private static bool Toggle(IEnumerable<XElement?> properties, string name)
    {
        var enabled = false;
        foreach (var property in properties.OfType<XElement>().Distinct())
        {
            if (property.Element(W + name) is not { } value) continue;
            var on = (string?)value.Attribute(W + "val") is null || On((string?)value.Attribute(W + "val"));
            if (property.Parent?.Name == W + "style") { if (on) enabled = !enabled; }
            else enabled = on;
        }
        return enabled;
    }
    private static bool On(string? value) => value is "1" or "true" or "on";
    private static bool Indented(XElement p) => p.Element(W + "pPr")?.Element(W + "ind")?.Attributes().Any(a =>
        a.Name.LocalName is "left" or "start" or "firstLine" or "hanging" or "leftChars" or "firstLineChars" && int.TryParse(a.Value, out var v) && v > 0) == true;
    private static bool ShortHeading(string text) => text.Length is >= 2 and <= 100 &&
        !text.Contains('\n') && !text.Contains('。') && !text.Contains("http", StringComparison.OrdinalIgnoreCase) &&
        !Regex.IsMatch(text, @"[.!?！？]$");
    private static int NumberedLevel(string text)
    {
        var match = Regex.Match(text.Normalize(NormalizationForm.FormKC), @"^(?<n>\d+(?:\.\d+)*)(?:[.．]\s*|\s+)[^\d\s]");
        return match.Success ? Math.Min(6, 1 + match.Groups["n"].Value.Count(c => c == '.')) : 0;
    }
    private static int NamedHeadingLevel(string? name)
    {
        var match = Regex.Match(name ?? "", @"^heading\s*(\d+)$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) && n is > 0 and <= 99 ? n : 0;
    }
    private sealed record ParagraphAppearance(string Text, double Size, bool Bold);

    public static Dictionary<string, JsonElement>? ImageDimensions(XElement image)
    {
        var host = image.Ancestors().FirstOrDefault(p => p.Name == WP + "inline" || p.Name == WP + "anchor");
        var extent = host?.Element(WP + "extent");
        double? width = Emu((string?)extent?.Attribute("cx")), height = Emu((string?)extent?.Attribute("cy"));
        if (host is null && image.Ancestors(V + "shape").FirstOrDefault() is { } shape)
        {
            var style = (string?)shape.Attribute("style") ?? "";
            width = VmlSize(style, "width"); height = VmlSize(style, "height");
        }
        if (width is not > 0 || height is not > 0) return null;
        return new(StringComparer.Ordinal)
        {
            ["display_width_px"] = JsonSerializer.SerializeToElement(width.Value),
            ["display_height_px"] = JsonSerializer.SerializeToElement(height.Value)
        };
    }
    private static double? Emu(string? raw) => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) &&
        double.IsFinite(size) && size is > 0 and <= 78028800 ? size / 9525 : null;
    private static double? VmlSize(string style, string property)
    {
        var match = Regex.Match(style, @"(?:^|;)\s*" + property + @"\s*:\s*(\d+(?:\.\d+)?)\s*(pt|px|in|cm|mm)\s*(?:;|$)", RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
        value *= match.Groups[2].Value.ToLowerInvariant() switch { "pt" => 96d / 72, "in" => 96, "cm" => 96 / 2.54, "mm" => 96 / 25.4, _ => 1 };
        return double.IsFinite(value) && value is > 0 and <= 8192 ? value : null;
    }
}
