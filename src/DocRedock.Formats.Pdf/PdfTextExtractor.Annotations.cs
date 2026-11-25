using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.Pdf;

public sealed record PdfPageLink(Geometry Bounds, string Target);
public sealed record PdfTextLinkSpan(int Start, int End, string Target);

// What a page's annotations say about its text. Acrobat marks text with StrikeOut, Underline and
// Squiggly annotations over the quadrilaterals around it; reviewers also cancel or underline text with
// the drawing tools instead of the comment, with a straight Line or a pencil stroke (Ink). A Link holds
// the area of a link, where an underline of the text is the link's style and no emphasis.
public static partial class PdfTextExtractor
{
    private static PdfTextRegion[] WithSourceLinks(IReadOnlyList<PdfTextRegion> flow,
        IReadOnlyList<PdfTextRegion> fragments, IReadOnlyList<PdfPageLink> links, Action? onBounded = null)
    {
        if (links.Count == 0) return flow.ToArray();
        var byId = fragments.ToDictionary(fragment => fragment.SourceTextIds[0]);
        var work = 0; var bounded = false;
        return flow.Select(region =>
        {
            if (bounded) return region;
            var spans = new List<PdfTextLinkSpan>();
            var cursor = 0;
            foreach (var id in region.SourceTextIds)
            {
                if (!byId.TryGetValue(id, out var fragment)) continue;
                var offset = region.Text.IndexOf(fragment.Text, cursor, StringComparison.Ordinal);
                if (offset < 0) continue;
                cursor = offset + fragment.Text.Length;
                foreach (var link in links)
                {
                    if (++work > 1_000_000) { if (!bounded) onBounded?.Invoke(); bounded = true; break; }
                    if (fragment.BoundingBox.Y > link.Bounds.Y + link.Bounds.Height ||
                        fragment.BoundingBox.Y + fragment.BoundingBox.Height < link.Bounds.Y) continue;
                    if (fragment.Glyphs is { } glyphs && glyphs.Count == fragment.Text.Length)
                    {
                        var start = -1;
                        for (var c = 0; c <= glyphs.Count; c++)
                        {
                            if (++work > 1_000_000) { if (!bounded) onBounded?.Invoke(); bounded = true; break; }
                            var inside = c < glyphs.Count && (glyphs[c].Left + glyphs[c].Right) / 2 >= link.Bounds.X &&
                                (glyphs[c].Left + glyphs[c].Right) / 2 <= link.Bounds.X + link.Bounds.Width;
                            if (inside && start < 0) start = c;
                            if (!inside && start >= 0) { spans.Add(new(offset + start, offset + c, link.Target)); start = -1; }
                        }
                    }
                    else if (link.Bounds.X <= fragment.BoundingBox.X + 1 &&
                        link.Bounds.X + link.Bounds.Width >= fragment.BoundingBox.X + fragment.BoundingBox.Width - 1)
                        spans.Add(new(offset, offset + fragment.Text.Length, link.Target));
                }
            }
            return spans.Count == 0 ? region : region with { LinkSpans = spans };
        }).ToArray();
    }

    // What one page is read for, so that a page of links or a huge selection cannot make reading it endless.
    private const int MaxAnnotationsPerPage = 50_000;
    private const int MaxMarksPerPage = 100_000;
    private const int MaxQuadsPerAnnotation = 10_000;

    /// <summary>The annotations of a page (/Annots, never inherited) that put a line on its text, and
    /// the areas of its links. A StrikeOut annotation is the line through the middle of each
    /// quadrilateral of /QuadPoints (or of the /Rect when there are none); an Underline or Squiggly one
    /// the line along its bottom. A Line annotation is its straight /L, and an Ink annotation a single
    /// stroke; both are lines of unknown kind, which the text they lie on decides, unless they have
    /// arrowheads, a caption or an intent (a figure, not a mark on the text) or the stroke is one of
    /// several. An annotation flagged hidden (bit 2 of /F) is one a viewer does not show.</summary>
    private static PdfTextDecorations.PageMarkup ReadPageMarkup(string body, IReadOnlyDictionary<int, string> objects, TimeSpan timeout)
    {
        if (ResolveKeyValue(body, "Annots", objects, timeout) is not { } annotations) return PdfTextDecorations.PageMarkup.None;
        var lines = new List<PdfTextDecorations.TextLine>();
        var links = new List<Geometry>();
        var linkTargets = new List<PdfPageLink>();
        var read = 0;
        foreach (Match reference in Regex.Matches(annotations, @"(?<id>\d+)\s+\d+\s+R\b", RegexOptions.None, timeout))
        {
            if (++read > MaxAnnotationsPerPage || lines.Count + links.Count >= MaxMarksPerPage) break;
            if (!int.TryParse(reference.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
                !objects.TryGetValue(id, out var annotation) ||
                !Regex.IsMatch(annotation, @"/Subtype\s*/(?:StrikeOut|Underline|Squiggly|Line|Ink|Link)\b", RegexOptions.None, timeout)) continue;
            var entries = ReadDictionaryEntries(annotation);
            if (Numbers(EntryValue(entries, "F")) is [var flags, ..] && ((long)flags & 2) != 0) continue;
            switch (ReadName(EntryValue(entries, "Subtype")))
            {
                case "Link":
                    var action = Resolve(EntryValue(entries, "A"));
                    var actionEntries = action is null ? [] : ReadDictionaryEntries(action);
                    var uriValue = ReadName(EntryValue(actionEntries, "S")) == "URI" ? Resolve(EntryValue(actionEntries, "URI")) : null;
                    var uri = uriValue is { Length: > 1 } && (uriValue[0] is '(' or '<')
                        ? Encoding.Latin1.GetString(StringBytes(uriValue)) : null;
                    foreach (var (left, right, bottom, top) in Quads(entries))
                    {
                        links.Add(new Geometry("pdf-user-space", left, bottom, right - left, top - bottom));
                        if (uri is { Length: > 0 }) linkTargets.Add(new(links[^1], uri));
                    }
                    break;
                case "StrikeOut":
                    foreach (var (left, right, bottom, top) in Quads(entries))
                        lines.Add(new PdfTextDecorations.TextLine(PdfTextDecorations.DecorationKind.Strike, left, right, (bottom + top) / 2));
                    break;
                case "Underline" or "Squiggly":
                    // A viewer draws the underline along the bottom of the marked text.
                    foreach (var (left, right, bottom, _) in Quads(entries))
                        lines.Add(new PdfTextDecorations.TextLine(PdfTextDecorations.DecorationKind.Underline, left, right, bottom));
                    break;
                case "Line":
                    if (PlainLine(entries) is { } line) lines.Add(line);
                    break;
                case "Ink":
                    if (HandDrawnLine(entries) is { } stroke) lines.Add(stroke);
                    break;
            }
        }
        return new PdfTextDecorations.PageMarkup(lines, links) { LinkTargets = linkTargets };

        string? Resolve(string? value) => value is not null && TryReadReference(value, out var target) ? objects.GetValueOrDefault(target) : value;

        double[] Numbers(string? declaration) => Resolve(declaration) is not { } text ? [] : Regex.Matches(text, NumberPattern, RegexOptions.None, timeout)
            .Select(item => double.TryParse(item.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN)
            .Where(double.IsFinite).ToArray();

        // The area of each quadrilateral of /QuadPoints (a mark or link over two lines is two), or the
        // /Rect when there are none.
        IEnumerable<(double Left, double Right, double Bottom, double Top)> Quads(IReadOnlyList<(string Key, string Value)> entries)
        {
            var numbers = Numbers(EntryValue(entries, "QuadPoints"));
            if (numbers.Length >= 8)
            {
                for (var start = 0; start + 8 <= numbers.Length && start < 8 * MaxQuadsPerAnnotation; start += 8)
                {
                    var xs = new[] { numbers[start], numbers[start + 2], numbers[start + 4], numbers[start + 6] };
                    var ys = new[] { numbers[start + 1], numbers[start + 3], numbers[start + 5], numbers[start + 7] };
                    if (xs.Max() > xs.Min()) yield return (xs.Min(), xs.Max(), ys.Min(), ys.Max());
                }
            }
            else if (ReadRectangle(Resolve(EntryValue(entries, "Rect")), timeout) is { } rectangle)
                yield return (rectangle.X, rectangle.X + rectangle.Width, rectangle.Y, rectangle.Y + rectangle.Height);
        }

        // A Line annotation that is a plain line: no arrowheads (/LE other than None), no caption, no
        // intent (an arrow, a dimension).
        PdfTextDecorations.TextLine? PlainLine(IReadOnlyList<(string Key, string Value)> entries)
        {
            if (EntryValue(entries, "IT") is not null || EntryValue(entries, "Cap") is "true") return null;
            if (Resolve(EntryValue(entries, "LE")) is { } ends && Regex.Matches(ends, @"/(?<name>[^\s/<>\[\]()]+)", RegexOptions.None, timeout)
                    .Any(name => name.Groups["name"].Value != "None")) return null;
            if (Numbers(EntryValue(entries, "L")) is not [var x1, var y1, var x2, var y2] || x1 == x2) return null;
            return new PdfTextDecorations.TextLine(null, Math.Min(x1, x2), Math.Max(x1, x2), (y1 + y2) / 2, Math.Abs(y1 - y2));
        }

        // An Ink annotation of one stroke, as the line along its span at the middle of its band; the
        // text it lies on says whether it is straight enough.
        PdfTextDecorations.TextLine? HandDrawnLine(IReadOnlyList<(string Key, string Value)> entries)
        {
            if (Resolve(EntryValue(entries, "InkList")) is not { } list || list.IndexOf('[') is var open && open < 0) return null;
            double[]? stroke = null;
            for (var index = open + 1; index < list.Length;)
            {
                index = SkipSpace(list, index);
                if (index >= list.Length || list[index] == ']') break;
                var end = SkipValue(list, index);
                // A second stroke: the annotation is not one line.
                if (stroke is not null) return null;
                stroke = Numbers(list[index..end]);
                index = end;
            }
            if (stroke is null || stroke.Length < 4 || stroke.Length % 2 != 0) return null;
            var xs = stroke.Where((_, at) => at % 2 == 0).ToArray();
            var ys = stroke.Where((_, at) => at % 2 == 1).ToArray();
            return xs.Max() > xs.Min() ? new PdfTextDecorations.TextLine(null, xs.Min(), xs.Max(), (ys.Min() + ys.Max()) / 2, ys.Max() - ys.Min()) : null;
        }
    }
}
