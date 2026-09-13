using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;
using DocRedock.Markdown;

namespace DocRedock.Render;

/// <summary>
/// One node of the inline syntax tree <see cref="MarkdownInlineParser"/> produces.
///
/// The tree is the renderer's single reading of inline Markdown: every output format is written
/// from it, so "what the text says" is decided exactly once instead of once per writer.  Text
/// carried by a node is SEMANTIC text -- backslash escapes and character references are already
/// resolved -- and each serializer re-encodes it for its own format (HTML escapes it again; OOXML
/// and PDF take it as-is).  <see cref="MarkdownInlineCode"/> is the deliberate exception: CommonMark
/// does not resolve references inside a code span, so its body stays exactly as written.
/// </summary>
public abstract record MarkdownInline;

/// <summary>Ordinary text.  Embedded '\n'/'\t' are real line breaks and tabs.</summary>
public sealed record MarkdownInlineText(string Text) : MarkdownInline;

/// <summary>A code span.  <paramref name="Text"/> is verbatim: no reference is resolved inside it.</summary>
public sealed record MarkdownInlineCode(string Text) : MarkdownInline;

public sealed record MarkdownInlineEmphasis(string Text) : MarkdownInline;
public sealed record MarkdownInlineStrong(string Text) : MarkdownInline;
public sealed record MarkdownInlineStrike(string Text) : MarkdownInline;

/// <summary>
/// A hyperlink.  <paramref name="Literal"/> is the source text of the whole construct, which the
/// HTML writer emits as plain text when <paramref name="Destination"/> fails its URL allowlist.
/// </summary>
public sealed record MarkdownInlineLink(string Destination, string Text, string Literal) : MarkdownInline;

/// <summary>An image.  <paramref name="Literal"/> is used exactly as in <see cref="MarkdownInlineLink"/>.</summary>
public sealed record MarkdownInlineImage(string Destination, string AltText, string Literal) : MarkdownInline;

/// <summary>An allowlisted raw HTML tag (<c>&lt;br&gt;</c>, <c>&lt;u&gt;</c>, <c>&lt;mark&gt;</c>, ...).</summary>
public sealed record MarkdownInlineRawHtml(string Markup) : MarkdownInline;

/// <summary>
/// The renderer's one inline Markdown reader, plus the per-format serializers written from its
/// output: HTML, plain text (PPTX / XLSX / PDF), and Word runs (DOCX).
///
/// Character references are resolved here, once, through the shared
/// <see cref="MarkdownCharacterReferences"/> policy, so every format agrees on the text a document
/// contains.  The order matters and is fixed: backslash escapes are stood in for first
/// (<see cref="ProtectEscapes"/>), then the tokenizer runs, then references are decoded on the
/// still-protected text, and only then are the escape placeholders restored.  A backslash-escaped
/// '&amp;' therefore can never start a reference.
/// </summary>
public static class MarkdownInlineParser
{
    // The 32 ASCII punctuation characters CommonMark allows a backslash to escape, in code-point
    // order. While inline text is tokenized, an escaped one is stood in for by the Unicode
    // noncharacter U+FDD0 + its index here: the U+FDD0..U+FDEF block is reserved by Unicode for
    // exactly this kind of internal, never-interchanged use, so unlike a private-use character it
    // can never collide with a symbol-font glyph that a source document legitimately contains.
    // '&' sits at index 5, so an escaped ampersand becomes U+FDD5 --
    // MarkdownCharacterReferences.EscapedAmpersandPlaceholder, which the shared decoder is
    // documented never to read as the start of a character reference.
    private const string AsciiPunctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
    private const char PlaceholderBase = '\uFDD0';
    private const char PlaceholderLast = '\uFDEF';

    private static readonly Regex HtmlInlineToken = new(
        @"(?<safeTag><br\s*/?>|</?(?:u|mark|summary|details)>|<details\s+class=""(?:speaker-notes|ocr-extraction)"">|<span\s+style=""color:#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?"">|</span>)|!\[(?<imageAlt>[^\]]*)\]\((?<imageUrl>[^)\s]+)(?:\s+""[^""]*"")?\)|\[(?<linkText>[^\]]+)\]\((?<linkUrl>[^)\s]+)\)|`(?<code>[^`\r\n]+)`|~~(?<strike>.+?)~~|\*\*(?<strong>.+?)\*\*|\*(?<em>[^*]+)\*|_(?<emUnderscore>[^_\r\n]+)_",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Reads one run of inline Markdown into the shared syntax tree.</summary>
    public static IReadOnlyList<MarkdownInline> Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var protectedValue = ProtectEscapes(value);
        var nodes = new List<MarkdownInline>();
        var cursor = 0;
        foreach (Match match in HtmlInlineToken.Matches(protectedValue))
        {
            AddText(nodes, protectedValue[cursor..match.Index]);
            if (match.Groups["safeTag"].Success) nodes.Add(new MarkdownInlineRawHtml(match.Value));
            else if (match.Groups["imageUrl"].Success)
                nodes.Add(new MarkdownInlineImage(Destination(match.Groups["imageUrl"].Value),
                    DecodeText(match.Groups["imageAlt"].Value), DecodeText(match.Value)));
            else if (match.Groups["linkUrl"].Success)
                nodes.Add(new MarkdownInlineLink(Destination(match.Groups["linkUrl"].Value),
                    DecodeText(match.Groups["linkText"].Value), DecodeText(match.Value)));
            // A code span is verbatim; only the escape placeholders are restored, because
            // ProtectEscapes copies a span through untouched and never creates any inside one.
            else if (match.Groups["code"].Success) nodes.Add(new MarkdownInlineCode(DecodePlaceholders(match.Groups["code"].Value)));
            else if (match.Groups["strike"].Success) nodes.Add(new MarkdownInlineStrike(DecodeText(match.Groups["strike"].Value)));
            else if (match.Groups["strong"].Success) nodes.Add(new MarkdownInlineStrong(DecodeText(match.Groups["strong"].Value)));
            else if (match.Groups["em"].Success) nodes.Add(new MarkdownInlineEmphasis(DecodeText(match.Groups["em"].Value)));
            else if (match.Groups["emUnderscore"].Success) nodes.Add(new MarkdownInlineEmphasis(DecodeText(match.Groups["emUnderscore"].Value)));
            else AddText(nodes, match.Value);
            cursor = match.Index + match.Length;
        }
        AddText(nodes, protectedValue[cursor..]);
        return nodes;
    }

    /// <summary>Serializes inline Markdown as an HTML fragment.</summary>
    public static string Html(string value, string? sourceDirectory = null, string? outputPath = null) =>
        Html(Parse(value), sourceDirectory, outputPath);

    /// <summary>Serializes the syntax tree as an HTML fragment.</summary>
    public static string Html(IReadOnlyList<MarkdownInline> nodes, string? sourceDirectory = null, string? outputPath = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var output = new StringBuilder(128);
        foreach (var node in nodes)
        {
            switch (node)
            {
                // Every text-bearing node is HTML-encoded here and only here. A '<u>' that the
                // source wrote as "&lt;u&gt;" is decoded to text by Parse and encoded back to
                // "&lt;u&gt;" by Encode, so a character reference can never become a live tag --
                // only the allowlisted MarkdownInlineRawHtml markup below reaches the page as HTML.
                case MarkdownInlineText text: output.Append(HtmlText(text.Text)); break;
                case MarkdownInlineRawHtml raw: output.Append(raw.Markup); break;
                case MarkdownInlineImage image:
                    if (IsSafeHtmlUrl(image.Destination, image: true))
                        output.Append("<img class=\"inline-image\" loading=\"lazy\" src=\"")
                            .Append(Encode(ResolveHtmlImageUrl(image.Destination, sourceDirectory, outputPath)))
                            .Append("\" alt=\"").Append(Encode(image.AltText)).Append("\">");
                    else output.Append(Encode(image.Literal));
                    break;
                case MarkdownInlineLink link:
                    if (IsSafeHtmlUrl(link.Destination, image: false))
                        output.Append("<a href=\"").Append(Encode(link.Destination)).Append("\">")
                            .Append(Encode(link.Text)).Append("</a>");
                    else output.Append(Encode(link.Literal));
                    break;
                case MarkdownInlineCode code: output.Append("<code>").Append(Encode(code.Text)).Append("</code>"); break;
                case MarkdownInlineStrike strike: output.Append("<del>").Append(Encode(strike.Text)).Append("</del>"); break;
                case MarkdownInlineStrong strong: output.Append("<strong>").Append(Encode(strong.Text)).Append("</strong>"); break;
                case MarkdownInlineEmphasis emphasis: output.Append("<em>").Append(Encode(emphasis.Text)).Append("</em>"); break;
            }
        }
        return output.ToString();
    }

    /// <summary>Projects inline Markdown to the plain text PPTX, XLSX, and PDF write.</summary>
    public static string PlainText(string value) => PlainText(Parse(value));

    /// <summary>Projects the syntax tree to plain text.</summary>
    public static string PlainText(IReadOnlyList<MarkdownInline> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var output = new StringBuilder(64);
        foreach (var node in nodes)
            output.Append(node switch
            {
                MarkdownInlineText text => text.Text,
                MarkdownInlineCode code => code.Text,
                MarkdownInlineEmphasis emphasis => emphasis.Text,
                MarkdownInlineStrong strong => strong.Text,
                MarkdownInlineStrike strike => strike.Text,
                MarkdownInlineLink link => link.Text,
                MarkdownInlineImage image => image.AltText,
                MarkdownInlineRawHtml raw when IsLineBreakTag(raw.Markup) => "\n",
                _ => string.Empty,
            });
        return output.ToString();
    }

    /// <summary>Maps inline Markdown to the Word runs the DOCX write emits.</summary>
    public static IReadOnlyList<TextRun> Runs(string value) => Runs(Parse(value));

    /// <summary>Maps the syntax tree to Word runs.</summary>
    public static IReadOnlyList<TextRun> Runs(IReadOnlyList<MarkdownInline> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var runs = new List<TextRun>();
        // '<u>' has no node of its own: it is an allowlisted raw tag, so underline is carried as
        // reader state exactly the way the HTML writer lets the tag pair wrap what follows.
        var underline = false;
        foreach (var node in nodes)
        {
            switch (node)
            {
                case MarkdownInlineText text: AddRuns(runs, text.Text, underline: underline); break;
                case MarkdownInlineCode code: AddRuns(runs, code.Text, underline: underline, code: true); break;
                case MarkdownInlineEmphasis emphasis: AddRuns(runs, emphasis.Text, italic: true, underline: underline); break;
                case MarkdownInlineStrong strong: AddRuns(runs, strong.Text, bold: true, underline: underline); break;
                case MarkdownInlineStrike strike: AddRuns(runs, strike.Text, underline: underline, strike: true); break;
                case MarkdownInlineLink link: AddRuns(runs, link.Text, underline: underline, linkTarget: link.Destination); break;
                case MarkdownInlineImage image: AddRuns(runs, image.AltText, underline: underline); break;
                case MarkdownInlineRawHtml raw when IsLineBreakTag(raw.Markup):
                    runs.Add(new TextRun("\n", Kind: TextRunKind.LineBreak));
                    break;
                case MarkdownInlineRawHtml raw when raw.Markup.Equals("<u>", StringComparison.OrdinalIgnoreCase):
                    underline = true;
                    break;
                case MarkdownInlineRawHtml raw when raw.Markup.Equals("</u>", StringComparison.OrdinalIgnoreCase):
                    underline = false;
                    break;
            }
        }
        return runs;
    }

    /// <summary>
    /// Word runs for text that is NOT Markdown -- a fenced code block line.  No syntax is read and
    /// no character reference is resolved; only real tabs become tab runs.
    /// </summary>
    public static IReadOnlyList<TextRun> LiteralRuns(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var runs = new List<TextRun>();
        AddRuns(runs, value);
        return runs;
    }

    /// <summary>HTML-encodes already-decoded text.</summary>
    public static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static void AddRuns(List<TextRun> runs, string text, bool bold = false, bool italic = false,
        bool underline = false, bool strike = false, bool code = false, string? linkTarget = null)
    {
        var segment = new StringBuilder(text.Length);
        void Flush()
        {
            if (segment.Length == 0) return;
            runs.Add(new TextRun(segment.ToString(), null, bold, italic, underline, strike, code,
                TextRunKind.Text, linkTarget));
            segment.Clear();
        }

        foreach (var character in text)
        {
            switch (character)
            {
                case '\n': Flush(); runs.Add(new TextRun("\n", Kind: TextRunKind.LineBreak)); break;
                case '\t': Flush(); runs.Add(new TextRun("\t", Kind: TextRunKind.Tab)); break;
                case '\r': break;
                default: segment.Append(character); break;
            }
        }
        Flush();
    }

    private static void AddText(List<MarkdownInline> nodes, string value)
    {
        if (value.Length == 0) return;
        nodes.Add(new MarkdownInlineText(DecodeText(value)));
    }

    private static bool IsLineBreakTag(string markup) => markup.StartsWith("<br", StringComparison.OrdinalIgnoreCase);

    // Resolves character references on the still-protected text and only then restores the escape
    // placeholders, so "\&amp;" stays the literal text "&amp;" (see MarkdownCharacterReferences).
    private static string DecodeText(string value) => DecodePlaceholders(MarkdownCharacterReferences.Decode(value));

    // A destination is decoded the same way, but its escape placeholders are restored verbatim:
    // CommonMark does not process backslash escapes inside a destination, so "\[" there is the two
    // characters the author typed.  Decoding runs first for a second reason: it is what lets
    // IsSafeHtmlUrl see "javascript&#58;..." as the scheme it actually is.
    private static string Destination(string value) =>
        DecodePlaceholdersVerbatim(MarkdownCharacterReferences.Decode(value));

    // CommonMark backslash escapes (https://spec.commonmark.org/current/#backslash-escapes): a
    // backslash followed by an ASCII punctuation character is that literal character and must not
    // start or end emphasis, code spans, links, or images; escapes are not processed inside code
    // spans or link/image destinations. ReadableMarkdownSerializer.EscapeLiteral and
    // DocRedockInlineMarkdown.Escape (D07) rely on the reader honouring this -- without it this
    // renderer's own regex tokenizer (HtmlInlineToken above) has no notion of escaping and turns an
    // escaped literal such as "\[LABEL\](url)" back into a live link with a stray backslash left
    // over, and in a table cell the backslash was silently dropped upstream while the link stayed
    // live (see MarkdownAstParser.SplitTableRow).
    //
    // Rather than teach the tokenizer's regex about escaping directly, every backslash-escaped
    // punctuation character is replaced with an opaque placeholder (U+FDD0 plus the punctuation's
    // index in AsciiPunctuation) before the regex ever runs. None of the regex's
    // character classes (`[^\]]`, `[^`\r\n]`, a bare `*`, `_`, ...) can match a placeholder, so an
    // escaped delimiter can no longer start or end syntax -- and an escaped closing bracket inside a
    // link/image label (`[see \[spec\]](url)`) is simply skipped over by `[^\]]+` the same way,
    // letting the existing regex find the real, unescaped delimiters with no extra bracket-matching
    // logic. DecodeText decodes the placeholder back to the literal character wherever text is
    // built; link/image URLs instead go through DecodePlaceholdersVerbatim, which restores the
    // original two-character "\X" sequence, since escapes are not processed inside a destination.
    private static string ProtectEscapes(string value)
    {
        if (value.IndexOf('\\') < 0) return value;
        var output = new StringBuilder(value.Length);
        var index = 0;
        while (index < value.Length)
        {
            var character = value[index];
            if (character == '`')
            {
                // Mirrors HtmlInlineToken's own code-span rule exactly (a single backtick, one or
                // more characters that are not a backtick/CR/LF, then a single backtick) so a span
                // recognized here is always the span the tokenizer recognizes later. Escapes are not
                // processed inside code spans, so the whole span is copied through untouched.
                var close = -1;
                for (var probe = index + 1; probe < value.Length; probe++)
                {
                    if (value[probe] == '`') { close = probe; break; }
                    if (value[probe] is '\r' or '\n') break;
                }
                if (close > index + 1)
                {
                    output.Append(value, index, close - index + 1);
                    index = close + 1;
                    continue;
                }
                output.Append('`');
                index++;
                continue;
            }
            if (character == '\\' && index + 1 < value.Length && IsAsciiPunctuation(value[index + 1]))
            {
                output.Append((char)(PlaceholderBase + AsciiPunctuation.IndexOf(value[index + 1])));
                index += 2;
                continue;
            }
            output.Append(character);
            index++;
        }
        return output.ToString();
    }

    private static bool IsAsciiPunctuation(char character) => AsciiPunctuation.IndexOf(character) >= 0;

    private static bool IsPlaceholder(char character) => character is >= PlaceholderBase and <= PlaceholderLast;

    private static string DecodePlaceholders(string value)
    {
        if (value.Length == 0) return value;
        var output = new StringBuilder(value.Length);
        foreach (var character in value)
            output.Append(IsPlaceholder(character) ? AsciiPunctuation[character - PlaceholderBase] : character);
        return output.ToString();
    }

    private static string DecodePlaceholdersVerbatim(string value)
    {
        if (value.Length == 0) return value;
        var output = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (IsPlaceholder(character)) output.Append('\\').Append(AsciiPunctuation[character - PlaceholderBase]);
            else output.Append(character);
        }
        return output.ToString();
    }

    private static string HtmlText(string value) => Encode(value)
        .Replace("  \n", "<br>\n", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static string ResolveHtmlImageUrl(string value, string? sourceDirectory, string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(outputPath) ||
            Uri.TryCreate(value, UriKind.Absolute, out _)) return value;
        var decoded = Uri.UnescapeDataString(value).Replace('/', Path.DirectorySeparatorChar);
        var sourceRoot = Path.GetFullPath(sourceDirectory);
        var sourcePath = Path.GetFullPath(Path.Combine(sourceRoot, decoded));
        var sourceRelative = Path.GetRelativePath(sourceRoot, sourcePath);
        if (Path.IsPathRooted(sourceRelative) || sourceRelative == ".." ||
            sourceRelative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return "about:blank";
        var relative = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, sourcePath).Replace('\\', '/');
        return string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
    }

    private static bool IsSafeHtmlUrl(string value, bool image)
    {
        if (value.Length == 0 || value.StartsWith("//", StringComparison.Ordinal) || value.Contains('\0')) return false;
        if (image && (value.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase) ||
                      value.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase) ||
                      value.StartsWith("data:image/gif;base64,", StringComparison.OrdinalIgnoreCase) ||
                      value.StartsWith("data:image/webp;base64,", StringComparison.OrdinalIgnoreCase))) return true;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return !Regex.IsMatch(value, @"^[A-Za-z][A-Za-z0-9+.-]*:", RegexOptions.CultureInvariant);
        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
               !image && uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase);
    }
}
