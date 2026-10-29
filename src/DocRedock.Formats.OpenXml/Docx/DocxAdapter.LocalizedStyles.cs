using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using DocRedock.Formats.OpenXml.Common;

namespace DocRedock.Formats.OpenXml.Docx;

/// <summary>
/// The parts of <c>word/styles.xml</c> a paragraph needs to be classified correctly: its display
/// name (Japanese Word writes <c>List Number</c> or <c>コード</c> there while the applied style id is
/// opaque, e.g. <c>a4</c> or <c>21</c>), its <c>w:basedOn</c> chain, the numbering a style supplies to
/// paragraphs without a direct <c>w:pPr/w:numPr</c>, and its font.
/// </summary>
internal sealed class DocxStyleTable
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    // A hand-written or corrupt styles.xml can make w:basedOn cyclic; a chain longer than this
    // simply stops inheriting, matching the guard the hidden-style and decoration readers use.
    private const int MaxChainDepth = 32;

    private readonly Dictionary<string, Definition> definitions;
    private readonly Dictionary<string, IReadOnlyList<string>> chains = new(StringComparer.Ordinal);

    private DocxStyleTable(Dictionary<string, Definition> definitions, string? defaultParagraphStyleId = null)
    {
        this.definitions = definitions;
        DefaultParagraphStyleId = defaultParagraphStyleId;
    }

    public string? DefaultParagraphStyleId { get; }

    public static DocxStyleTable Empty => new(new Dictionary<string, Definition>(StringComparer.Ordinal));

    private sealed record Definition(string? Name, string? BasedOn, int? NumId, int? Ilvl, bool HasNumPr, IReadOnlyDictionary<string, string> Fonts);
    public sealed record Numbering(int? NumId, int? Ilvl);

    public static DocxStyleTable Read(ZipArchive archive, CancellationToken cancellationToken)
    {
        if (archive.GetEntry("word/styles.xml") is not { } entry) return Empty;
        var root = SafeXml.LoadDocument(DocxAdapter.ReadEntryAsync(entry, cancellationToken).GetAwaiter().GetResult()).Root;
        var definitions = new Dictionary<string, Definition>(StringComparer.Ordinal);
        foreach (var style in root?.Elements(W + "style") ?? Enumerable.Empty<XElement>())
        {
            if ((string?)style.Attribute(W + "styleId") is not { } styleId) continue;
            var numbering = style.Element(W + "pPr")?.Element(W + "numPr");
            definitions[styleId] = new Definition(
                (string?)style.Element(W + "name")?.Attribute(W + "val"),
                (string?)style.Element(W + "basedOn")?.Attribute(W + "val"),
                ParseNonNegative((string?)numbering?.Element(W + "numId")?.Attribute(W + "val")),
                ParseLevel((string?)numbering?.Element(W + "ilvl")?.Attribute(W + "val")),
                numbering is not null,
                ReadFonts(style.Element(W + "rPr")?.Element(W + "rFonts")));
        }
        var defaultStyle = root?.Elements(W + "style").FirstOrDefault(style =>
            (string?)style.Attribute(W + "type") == "paragraph" &&
            (string?)style.Attribute(W + "default") is "1" or "true" or "on");
        return new DocxStyleTable(definitions, (string?)defaultStyle?.Attribute(W + "styleId"));
    }

    /// <summary>Style ids from the applied style up its <c>w:basedOn</c> chain. A repeated id ends
    /// the walk, so a cycle cannot loop forever.</summary>
    public IReadOnlyList<string> Chain(string? styleId)
    {
        if (styleId is null) return Array.Empty<string>();
        if (chains.TryGetValue(styleId, out var cached)) return cached;
        var chain = new List<string> { styleId };
        var visiting = new HashSet<string>(StringComparer.Ordinal) { styleId };
        var current = definitions.TryGetValue(styleId, out var first) ? first.BasedOn : null;
        while (current is not null && chain.Count < MaxChainDepth && visiting.Add(current) &&
               definitions.TryGetValue(current, out var definition))
        {
            chain.Add(current);
            current = definition.BasedOn;
        }
        chains[styleId] = chain;
        return chain;
    }

    /// <summary>The nearest declared <c>w:name</c>; an id Word generated (a4, 21) says nothing itself.</summary>
    public string? NameOf(string? styleId) => styleId is null ? null :
        Chain(styleId).Select(id => Def(id)?.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

    public bool IsListStyle(string? styleId) => styleId is not null &&
        Chain(styleId).Any(id => IsListName(Def(id)?.Name));

    public bool IsCodeStyle(string? styleId) => styleId is not null &&
        Chain(styleId).Any(id => DocxAdapter.IsCodeStyleName(Def(id)?.Name));

    public bool IsOrderedStyle(string? styleId) => Chain(styleId).Any(id =>
        (Def(id)?.Name ?? id).Replace(" ", string.Empty, StringComparison.Ordinal).Contains("listnumber", StringComparison.OrdinalIgnoreCase));

    /// <summary>The numbering the style chain supplies (numId and ilvl inherit independently), or
    /// null when no style in the chain numbers its paragraphs.</summary>
    public Numbering? NumberingOf(string? styleId)
    {
        int? numId = null, level = null;
        foreach (var id in Chain(styleId))
        {
            numId ??= Def(id)?.NumId;
            level ??= Def(id)?.Ilvl;
        }
        return numId is null && level is null ? null : new Numbering(numId, level);
    }

    /// <summary>Nesting level the style chain states: the nearest <c>w:numPr/w:ilvl</c>, else the
    /// trailing number of the name (<c>List Bullet 2</c> is the second level), else the id's.</summary>
    public int LevelOf(string? styleId)
    {
        if (styleId is null) return 0;
        foreach (var id in Chain(styleId))
            if (Def(id) is { HasNumPr: true, Ilvl: { } ilvl }) return Math.Max(0, ilvl);
        return Chain(styleId).Select(id => DocxAdapter.StyleListLevel(Def(id)?.Name) ?? DocxAdapter.StyleListLevel(id)).FirstOrDefault(level => level is not null) ?? 0;
    }

    public IReadOnlyDictionary<string, string> FontsOf(string? styleId)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in Chain(styleId))
            foreach (var (slot, font) in Def(id)?.Fonts ?? new Dictionary<string, string>()) result.TryAdd(slot, font);
        return result;
    }

    public static IReadOnlyDictionary<string, string> ReadFonts(XElement? fonts) =>
        (fonts?.Attributes() ?? []).Where(attribute => attribute.Name.Namespace == W &&
            attribute.Name.LocalName is "ascii" or "hAnsi" or "eastAsia" or "cs" && !string.IsNullOrWhiteSpace(attribute.Value))
            .ToDictionary(attribute => attribute.Name.LocalName, attribute => attribute.Value, StringComparer.Ordinal);

    private Definition? Def(string id) => definitions.TryGetValue(id, out var definition) ? definition : null;

    private static bool IsListName(string? name) => name is not null &&
        (name.Contains("list bullet", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("list number", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("list paragraph", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("listbullet", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("listnumber", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("listparagraph", StringComparison.OrdinalIgnoreCase));

    internal static int? ParseNonNegative(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0 ? parsed : null;

    private static int? ParseLevel(string? value) => ParseNonNegative(value);

}

/// <summary>
/// Recognizes code typed in a monospace face rather than marked by a style: a run of at least two
/// consecutive normal paragraphs whose visible text runs all use a monospace font projects as code
/// lines. A single such paragraph stays a paragraph, because inline monospace inside prose is not a
/// code block. Only the node kind and its extensions change; paragraph text and slices are untouched.
/// </summary>
internal static class DocxMonospaceCode
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public sealed record Line(int Group, int Index);

    public static IReadOnlyDictionary<XElement, Line> Find(IReadOnlyList<XElement> blocks, DocxStyleTable styles,
        Func<XElement, bool> isHiddenRun, Func<XElement, bool> isHiddenElement)
    {
        var result = new Dictionary<XElement, Line>();
        var run = new List<(XElement Paragraph, bool HasText)>();
        var group = 0;
        for (var index = 0; index <= blocks.Count; index++)
        {
            var element = index < blocks.Count ? blocks[index] : null;
            var state = element is not null && element.Name == W + "p" && IsNormalParagraph(element, styles)
                ? Classify(element, styles, isHiddenRun, isHiddenElement)
                : null;
            if (element is not null && state is { } hasText)
            {
                run.Add((element, hasText));
                continue;
            }
            Flush(run, group, result);
            // Only a paragraph that cannot belong to a block breaks one; a blank paragraph never
            // does, so a blank line inside a code block stays inside it.
            if (element is not null) group++;
            run.Clear();
        }
        return result;
    }

    /// <summary>Blank paragraphs join a block only between two monospace ones, so the empty
    /// paragraphs a document uses as spacers outside a block stay paragraphs.</summary>
    private static void Flush(List<(XElement Paragraph, bool HasText)> run, int group, Dictionary<XElement, Line> result)
    {
        var first = 0;
        var last = run.Count - 1;
        while (first <= last && !run[first].HasText) first++;
        while (last >= first && !run[last].HasText) last--;
        if (run.Count(item => item.HasText) < 2) return;
        var line = 0;
        for (var index = first; index <= last; index++, line++) result[run[index].Paragraph] = new Line(group, line);
    }

    private static bool LooksLikeCode(string text) => text.StartsWith("  ", StringComparison.Ordinal) || text.StartsWith('\t') ||
        text.Contains('=') || text.Contains('{') || text.Contains('}') || text.Contains('(') || text.Contains(';') ||
        text.TrimStart().StartsWith('#') || text.TrimEnd().EndsWith(':');

    /// <summary>True when every visible text run is monospace, false for a blank paragraph, null when
    /// the paragraph is not part of a monospace run at all.</summary>
    private static bool? Classify(XElement paragraph, DocxStyleTable styles, Func<XElement, bool> isHiddenRun, Func<XElement, bool> isHiddenElement)
    {
        // Hidden/revision-only paragraphs are barriers, not blank lines inside visible code.
        if (paragraph.Descendants().Any(DocxAdapter.IsRevisionContainer) ||
            DocxAdapter.RelevantDescendants(paragraph).Any(element => element.Name == W + "r" && isHiddenRun(element))) return null;
        var visibleRuns = VisibleTextRuns(paragraph, isHiddenRun);
        if (visibleRuns.Count > 0)
        {
            if (MonospaceFont(paragraph, styles, isHiddenRun) is not { } font) return null;
            // Japanese fixed-pitch faces and inherited body fonts are weak evidence. Prose is
            // a barrier even beside code, so a code line cannot absorb an entire Normal-style document.
            var needsSyntax = visibleRuns.Any(run => run.Element(W + "rPr")?.Element(W + "rFonts") is null) ||
                font.Any(character => character > 127) || font.StartsWith("MS ", StringComparison.OrdinalIgnoreCase);
            return !needsSyntax || LooksLikeCode(VisibleText(paragraph, isHiddenElement)) ? true : null;
        }
        return paragraph.Descendants().Any(element => element.Name == W + "drawing" || element.Name == W + "pict" || element.Name == W + "object")
            ? null : string.IsNullOrEmpty(VisibleText(paragraph, isHiddenElement)) ? false : null;
    }

    /// <summary>The monospace face every visible text run uses, or null when the paragraph mixes
    /// fonts, has no text, or carries markup the code projection cannot keep.</summary>
    public static string? MonospaceFont(XElement paragraph, DocxStyleTable styles, Func<XElement, bool> isHiddenRun)
    {
        if (paragraph.Descendants().Any(DocxAdapter.IsRevisionContainer) || paragraph.Descendants().Any(element =>
                element.Name == W + "drawing" || element.Name == W + "pict" || element.Name == W + "object" ||
                element.Name == W + "hyperlink" || element.Name == W + "fldChar" || element.Name == W + "instrText"))
            return null;
        var runs = VisibleTextRuns(paragraph, isHiddenRun);
        if (runs.Count == 0) return null;
        string? shared = null;
        foreach (var run in runs)
        {
            var font = FontOf(run, paragraph, styles);
            if (font is null || !DocxAdapter.IsMonospaceFont(font)) return null;
            shared ??= font;
        }
        return shared;
    }

    /// <summary>Only a plain paragraph can be code typed in a monospace face: a list, heading, title
    /// or styled code paragraph already has its own kind.</summary>
    private static bool IsNormalParagraph(XElement paragraph, DocxStyleTable styles)
    {
        var properties = paragraph.Element(W + "pPr");
        if (properties?.Element(W + "numPr") is not null || properties?.Element(W + "outlineLvl") is not null) return false;
        var style = (string?)properties?.Element(W + "pStyle")?.Attribute(W + "val");
        return styles.NumberingOf(style)?.NumId is not > 0 && !styles.IsListStyle(style) && !styles.IsCodeStyle(style) &&
               !DocxAdapter.IsListStyle(style) && !DocxAdapter.IsCodeStyle(style) &&
               DocxAdapter.HeadingLevel(style) == 0 && !DocxAdapter.IsTitleStyle(style);
    }

    private static IReadOnlyList<XElement> VisibleTextRuns(XElement paragraph, Func<XElement, bool> isHiddenRun) =>
        DocxAdapter.RelevantDescendants(paragraph)
            .Where(element => element.Name == W + "r" && !isHiddenRun(element))
            .Where(element => element.Elements().Any(child => child.Name == W + "t" && child.Value.Length > 0))
            .ToArray();

    private static string VisibleText(XElement paragraph, Func<XElement, bool> isHiddenElement) =>
        string.Concat(DocxAdapter.RelevantDescendants(paragraph)
            .Where(element => element.Name == W + "t" && !isHiddenElement(element))
            .Select(element => element.Value));

    private static string? FontOf(XElement run, XElement paragraph, DocxStyleTable styles)
    {
        var declared = new Dictionary<string, string>(DocxStyleTable.ReadFonts(run.Element(W + "rPr")?.Element(W + "rFonts")), StringComparer.Ordinal);
        var runStyle = (string?)run.Element(W + "rPr")?.Element(W + "rStyle")?.Attribute(W + "val");
        var paragraphStyle = (string?)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? styles.DefaultParagraphStyleId;
        foreach (var (slot, font) in styles.FontsOf(runStyle)) declared.TryAdd(slot, font);
        foreach (var (slot, font) in styles.FontsOf(paragraphStyle)) declared.TryAdd(slot, font);
        // Script-specific declarations inherit independently. A proportional face alongside a
        // monospace face is mixed evidence and never sufficient for block inference.
        return declared.Count > 0 && declared.Values.All(DocxAdapter.IsMonospaceFont) ? declared.Values.First() : null;
    }
}
