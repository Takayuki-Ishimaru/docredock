using System.Text.Json;
using System.Xml.Linq;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;

namespace DocRedock.Formats.OpenXml.Docx;

public sealed partial class DocxAdapter
{
    // Effective marks belong to the readable projection. TextRun keeps the direct properties
    // that the F1 patcher writes back, otherwise an inherited mark would become direct formatting.
    private sealed class DocxStyleDecorations
    {
        public static readonly DocxStyleDecorations Empty = new(default, null,
            new Dictionary<string, DecorationStyle>(StringComparer.Ordinal),
            new Dictionary<string, DecorationStyle>(StringComparer.Ordinal), false);

        private readonly record struct DecorationStyle(bool StrikeToggle, bool? DoubleStrike, bool? Underline, bool Unresolved = false);
        private readonly record struct Declaration(string Type, string? BasedOn, DecorationStyle Properties);
        private readonly DecorationStyle defaults;
        private readonly string? defaultParagraphStyle;
        private readonly IReadOnlyDictionary<string, DecorationStyle> paragraphStyles;
        private readonly IReadOnlyDictionary<string, DecorationStyle> characterStyles;
        private readonly HashSet<string> unresolvedStyles = new(StringComparer.Ordinal);
        public bool HasDeclarations { get; }

        private DocxStyleDecorations(DecorationStyle defaults, string? defaultParagraphStyle,
            IReadOnlyDictionary<string, DecorationStyle> paragraphStyles,
            IReadOnlyDictionary<string, DecorationStyle> characterStyles, bool hasDeclarations)
        {
            this.defaults = defaults;
            this.defaultParagraphStyle = defaultParagraphStyle;
            this.paragraphStyles = paragraphStyles;
            this.characterStyles = characterStyles;
            HasDeclarations = hasDeclarations;
        }

        public static DocxStyleDecorations Read(XElement root, string? defaultParagraphStyle, CancellationToken cancellationToken)
        {
            var defaults = Properties(root.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr"));
            var declarations = new Dictionary<string, Declaration>(StringComparer.Ordinal);
            foreach (var style in root.Elements(W + "style"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((string?)style.Attribute(W + "styleId") is not { } id) continue;
                declarations[id] = new((string?)style.Attribute(W + "type") ?? "paragraph",
                    (string?)style.Element(W + "basedOn")?.Attribute(W + "val"), Properties(style.Element(W + "rPr")));
            }
            var paragraphs = new Dictionary<string, DecorationStyle>(StringComparer.Ordinal);
            var characters = new Dictionary<string, DecorationStyle>(StringComparer.Ordinal);
            foreach (var (id, declaration) in declarations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (declaration.Type == "paragraph") paragraphs[id] = Resolve(id, declaration.Type);
                else if (declaration.Type == "character") characters[id] = Resolve(id, declaration.Type);
            }
            return new(defaults, defaultParagraphStyle, paragraphs, characters,
                defaults != default || declarations.Values.Any(item => item.Properties != default));

            DecorationStyle Resolve(string id, string type)
            {
                var chain = new List<DecorationStyle>();
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (declarations.TryGetValue(id, out var declaration) && declaration.Type == type)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (chain.Count >= MaxStyleChainDepth || !visited.Add(id)) return new(false, null, null, Unresolved: true);
                    chain.Add(declaration.Properties);
                    if (declaration.BasedOn is not { } parent) break;
                    id = parent; // A missing or differently typed base is ignored, as OOXML requires.
                }
                var result = default(DecorationStyle);
                for (var index = chain.Count - 1; index >= 0; index--)
                {
                    var current = chain[index];
                    // w:strike is a toggle in styles: true reverses the inherited state and
                    // false leaves it alone. w:dstrike and w:u use the nearest declaration.
                    result = new(result.StrikeToggle ^ current.StrikeToggle,
                        current.DoubleStrike ?? result.DoubleStrike, current.Underline ?? result.Underline);
                }
                return result;
            }
        }

        private static DecorationStyle Properties(XElement? properties) => new(
            IsEnabled(properties?.Element(W + "strike")),
            properties?.Element(W + "dstrike") is { } doubleStrike ? IsEnabled(doubleStrike) : null,
            properties?.Element(W + "u") is { } underline ? IsEnabled(underline, "none") : null);

        public (bool Strike, bool Underline, bool Unresolved) Resolve(XElement run)
        {
            var properties = run.Element(W + "rPr");
            var single = defaults.StrikeToggle;
            var doubleStrike = defaults.DoubleStrike ?? false;
            var underline = defaults.Underline ?? false;
            var unresolved = false;
            if (HasDeclarations)
            {
                var paragraphId = (string?)run.Ancestors(W + "p").FirstOrDefault()?.Element(W + "pPr")?
                    .Element(W + "pStyle")?.Attribute(W + "val");
                if (paragraphId is null || !paragraphStyles.ContainsKey(paragraphId)) paragraphId = defaultParagraphStyle;
                Apply(paragraphId, paragraphStyles);
                Apply((string?)properties?.Element(W + "rStyle")?.Attribute(W + "val"), characterStyles);
            }
            // Direct formatting sets an absolute state, including an explicit off/none.
            if (properties?.Element(W + "strike") is { } directStrike) single = IsEnabled(directStrike);
            if (properties?.Element(W + "dstrike") is { } directDouble) doubleStrike = IsEnabled(directDouble);
            if (properties?.Element(W + "u") is { } directUnderline) underline = IsEnabled(directUnderline, "none");
            return (single || doubleStrike, underline, unresolved);

            void Apply(string? id, IReadOnlyDictionary<string, DecorationStyle> styles)
            {
                if (id is null || !styles.TryGetValue(id, out var style)) return;
                if (style.Unresolved)
                {
                    unresolved = true;
                    unresolvedStyles.Add(id);
                    return;
                }
                single ^= style.StrikeToggle;
                doubleStrike = style.DoubleStrike ?? doubleStrike;
                underline = style.Underline ?? underline;
            }
        }

        public void AddDiagnostics(ICollection<Diagnostic> diagnostics)
        {
            if (unresolvedStyles.Count == 0) return;
            diagnostics.Add(new("DocxStyleDecorationUnresolved",
                $"{unresolvedStyles.Count} applied style(s) have cyclic or excessively deep inheritance; inherited underline/strikethrough may be incomplete. Compare the marked content with the source.",
                DiagnosticSeverity.Warning, PartUri: "/word/styles.xml"));
        }
    }

    private static Dictionary<string, JsonElement>? WithStyleDecorationReview(Dictionary<string, JsonElement>? extensions,
        XContainer container, DocxHiddenStyles styles)
    {
        if (!styles.Decorations.HasDeclarations || !RelevantDescendants(container).Any(element =>
                element.Name == W + "r" && !IsHiddenRun(element, styles) && styles.Decorations.Resolve(element).Unresolved)) return extensions;
        extensions ??= new(StringComparer.Ordinal);
        extensions[ReviewExtensionKeys.SourceReviewRequired] = JsonSerializer.SerializeToElement(true);
        return extensions;
    }
}
