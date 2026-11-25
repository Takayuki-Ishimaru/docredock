using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Markdown;

namespace DocRedock.Render;

/// <summary>An image the HTML writer may embed, keyed in <see cref="ReadableHtmlOptions.Assets"/> by the exact
/// relative reference the document uses for it (for example "report.assets/img-0001.png").</summary>
public sealed record ReadableHtmlAsset(string MediaType, ReadOnlyMemory<byte> Content);

/// <summary>Options for the native document writer and the Markdown compatibility writer.</summary>
public sealed record ReadableHtmlOptions(
    string? Title = null,
    string? Language = null,
    IReadOnlyDictionary<string, ReadableHtmlAsset>? Assets = null,
    long MaxImageBytes = ReadableHtmlOptions.DefaultMaxImageBytes,
    long MaxTotalImageBytes = ReadableHtmlOptions.DefaultMaxTotalImageBytes,
    string? Generator = null,
    bool ShowFormulas = false)
{
    public const long DefaultMaxImageBytes = 25L * 1024 * 1024;
    public const long DefaultMaxTotalImageBytes = 200L * 1024 * 1024;
}

/// <summary>Something the writer could not carry into the HTML (Code is stable; Message is for people).</summary>
public sealed record ReadableHtmlDiagnostic(string Code, string Message);

public sealed record ReadableHtmlResult(string Html, IReadOnlyList<ReadableHtmlDiagnostic> Diagnostics)
{
    public int EmbeddedImageCount { get; init; }
    public long EmbeddedImageBytes { get; init; }
}

/// <summary>
/// Writes a typed readable document directly as one self-contained HTML5 document:
/// images become data URIs, nothing is loaded from the network and no script runs.
/// <see cref="WriteDocument"/> consumes native blocks and runs without Markdown serialization or parsing.
/// <see cref="Write"/> retains compatibility for callers that already have Markdown text.
/// </summary>
/// <remarks>
/// The Markdown compatibility entry point supports ATX headings, paragraphs with hard breaks, backtick fenced code
/// (Mermaid fences become inline SVG), thematic breaks, block quotes, bullet and ordered lists (nested, loose or tight),
/// GFM pipe tables (with the merged-cell hint comment), HTML comments, the OCR / speaker-notes <c>&lt;details&gt;</c>
/// blocks and raw <c>&lt;img&gt;</c> lines, and the inline forms bold, italic, strikethrough, code spans, links
/// (including styled link text and angle-bracket destinations), images, <c>&lt;br&gt;</c>, <c>&lt;u&gt;</c>, <c>&lt;ins&gt;</c>,
/// <c>&lt;del&gt;</c>, <c>&lt;mark&gt;</c> and colored spans. Other CommonMark constructs -- setext headings, indented
/// code, <c>~~~</c> fences, reference links, autolinks and intraword underscores -- are not interpreted and are rendered as text.
/// </remarks>
public static partial class ReadableHtmlWriter
{
    public const string ImageNotEmbeddable = "HtmlImageNotEmbeddable";
    public const string ImageEmbedSkipped = "HtmlImageEmbedSkipped";
    public const string ImageMissing = "HtmlImageMissing";

    public static ReadableHtmlResult Write(string markdown, ReadableHtmlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return new HtmlDocumentWriter(options ?? new ReadableHtmlOptions()).Run(markdown);
    }

    private sealed record Heading(int Level, string Id, string Text);

    private sealed partial class HtmlDocumentWriter
    {
        private const int MaxDepth = 24;
        private readonly ReadableHtmlOptions _options;
        private readonly IReadOnlyDictionary<string, ReadableHtmlAsset> _assets;
        private readonly StringBuilder _body = new(4096);
        private readonly List<ReadableHtmlDiagnostic> _diagnostics = [];
        private readonly List<Heading> _headings = [];
        private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _assetIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _shown = new(StringComparer.Ordinal);
        private readonly List<string> _linked = [];
        private readonly HashSet<string> _linkedSet = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _dataUris = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _nextSuffix = new(StringComparer.Ordinal);
        private int _diagramCount;
        private bool _sawBlock;
        private bool _inLink;
        private bool _ja;
        private string? _firstH1;
        private int _tocInsertAt = -1;
        private int _embeddedCount;
        private long _embeddedBytes;

        public HtmlDocumentWriter(ReadableHtmlOptions options)
        {
            _options = options;
            _assets = options.Assets ?? new Dictionary<string, ReadableHtmlAsset>();
        }

        // ------------------------------------------------------------------ document

        public ReadableHtmlResult Run(string markdown)
        {
            _ja = DetectJapanese(markdown);
            var language = !string.IsNullOrWhiteSpace(_options.Language) && IsLanguageTag(_options.Language.Trim())
                ? _options.Language.Trim() : _ja ? "ja" : "en";
            // Labels follow the page language, not just the detected text.
            _ja = language.StartsWith("ja", StringComparison.OrdinalIgnoreCase);

            var text = markdown.Length > 0 && markdown[0] == '﻿' ? markdown[1..] : markdown;
            var lines = SplitLines(text);
            if (lines.All(IsBlank))
                _body.Append("<p class=\"empty\">").Append(_ja ? "（内容がありません）" : "(This document has no content.)").Append("</p>\n");
            else
            {
                Blocks(lines, 0, false);
                AppendLinkedImages();
            }

            var page = new StringBuilder(_body.Length + 4096);
            page.Append("<!doctype html>\n<html lang=\"").Append(language).Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
                .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\">\n")
                .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            if (!string.IsNullOrWhiteSpace(_options.Generator))
                page.Append("<meta name=\"generator\" content=\"").Append(Enc(_options.Generator.Trim())).Append("\">\n");
            var title = !string.IsNullOrWhiteSpace(_options.Title) ? _options.Title.Trim()
                : !string.IsNullOrWhiteSpace(_firstH1) ? _firstH1.Trim() : "DocRedock";
            page.Append("<title>").Append(Enc(title)).Append("</title>\n<style>").Append(Css).Append("</style>\n</head>\n<body>\n<main>\n");

            var toc = BuildToc();
            if (toc.Length > 0)
            {
                var at = _tocInsertAt >= 0 ? _tocInsertAt : 0;
                _body.Insert(at, toc);
            }
            page.Append(_body).Append("</main>\n</body>\n</html>\n");
            return new ReadableHtmlResult(page.ToString(), _diagnostics)
            {
                EmbeddedImageCount = _embeddedCount,
                EmbeddedImageBytes = _embeddedBytes,
            };
        }

        private static bool DetectJapanese(string text)
        {
            foreach (var c in text)
                if (c is >= '぀' and <= 'ヿ' or >= '㐀' and <= '䶿' or >= '一' and <= '鿿') return true;
            return false;
        }

        private string BuildToc()
        {
            var entries = _headings.Where(h => h.Level is 2 or 3 && h.Text.Length > 0).ToList();
            if (entries.Count < 3) return string.Empty;
            var sb = new StringBuilder();
            var label = _ja ? "目次" : "Contents";
            // A long contents list would push the start of the document below the first screen, so it
            // is collapsed behind its title; a short one stays open.
            var collapsed = entries.Count > TocCollapseThreshold;
            sb.Append("<nav class=\"toc\" aria-label=\"").Append(label).Append("\">\n");
            if (collapsed)
            {
                var count = entries.Count.ToString(CultureInfo.InvariantCulture);
                sb.Append("<details>\n<summary class=\"toc-title\">")
                    .Append(_ja ? "目次（" + count + "項目）" : "Contents (" + count + " items)").Append("</summary>\n<ul>\n");
            }
            else
            {
                sb.Append("<p class=\"toc-title\">").Append(label).Append("</p>\n<ul>\n");
            }
            var inSub = false;
            var openItem = false;
            foreach (var h in entries)
            {
                if (h.Level == 3 && openItem)
                {
                    if (!inSub) { sb.Append("\n<ul>\n"); inSub = true; }
                    sb.Append("<li><a href=\"#").Append(h.Id).Append("\">").Append(Enc(h.Text)).Append("</a></li>\n");
                    continue;
                }
                if (inSub) { sb.Append("</ul>\n"); inSub = false; }
                if (openItem) sb.Append("</li>\n");
                sb.Append("<li><a href=\"#").Append(h.Id).Append("\">").Append(Enc(h.Text)).Append("</a>");
                openItem = true;
            }
            if (inSub) sb.Append("</ul>\n");
            if (openItem) sb.Append("</li>\n");
            sb.Append("</ul>\n");
            if (collapsed) sb.Append("</details>\n");
            sb.Append("</nav>\n");
            return sb.ToString();
        }

        internal const int TocCollapseThreshold = 12;

        private void AppendLinkedImages()
        {
            var pending = _linked.Where(key => !_shown.Contains(key)).ToList();
            if (pending.Count == 0) return;
            _body.Append("<section class=\"linked-images\">\n");
            foreach (var key in pending)
            {
                _body.Append("<figure>");
                EmitImage(_body, key, FileNameOf(key), null, null);
                _body.Append("</figure>\n");
            }
            _body.Append("</section>\n");
        }

        // ------------------------------------------------------------------ line helpers

        private static List<string> SplitLines(string text)
        {
            var lines = new List<string>(Math.Max(16, text.Length / 40));
            var start = 0;
            for (var i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] != '\n' && text[i] != '\r') continue;
                lines.Add(ExpandLeadingTabs(text.Substring(start, i - start)));
                if (i < text.Length && text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
            return lines;
        }

        private static string ExpandLeadingTabs(string line)
        {
            if (line.IndexOf('\t') < 0) return line;
            var k = 0;
            var width = 0;
            while (k < line.Length && (line[k] == ' ' || line[k] == '\t')) { width += line[k] == '\t' ? 4 - width % 4 : 1; k++; }
            return new string(' ', width) + line[k..];
        }

        private static bool IsBlank(string line)
        {
            foreach (var c in line) if (c != ' ' && c != '\t') return false;
            return true;
        }

        private static int Indent(string line)
        {
            var k = 0;
            while (k < line.Length && line[k] == ' ') k++;
            return k;
        }

        private static string StripIndent(string line, int max)
        {
            var k = 0;
            while (k < max && k < line.Length && line[k] == ' ') k++;
            return line[k..];
        }

        // ------------------------------------------------------------------ block recognisers

        private static bool TryFenceOpen(string line, out int length, out string info)
        {
            length = 0; info = string.Empty;
            var indent = Indent(line);
            if (indent > 3) return false;
            var k = indent;
            while (k < line.Length && line[k] == '`') k++;
            if (k - indent < 3) return false;
            var rest = line[k..].Trim();
            if (rest.Contains('`')) return false;
            length = k - indent;
            info = rest;
            return true;
        }

        private static bool IsFenceClose(string line, int length)
        {
            var indent = Indent(line);
            if (indent > 3) return false;
            var k = indent;
            while (k < line.Length && line[k] == '`') k++;
            return k - indent >= length && IsBlank(line[k..]);
        }

        private static bool TryHeading(string line, out int level, out string text)
        {
            level = 0; text = string.Empty;
            var indent = Indent(line);
            if (indent > 3) return false;
            var k = indent;
            while (k < line.Length && line[k] == '#') k++;
            level = k - indent;
            if (level is < 1 or > 6) return false;
            if (k < line.Length && line[k] != ' ' && line[k] != '\t') return false;
            var content = line[k..].Trim();
            var end = content.Length;
            while (end > 0 && content[end - 1] == '#') end--;
            if (end == 0) content = string.Empty;
            else if (end < content.Length && (content[end - 1] == ' ' || content[end - 1] == '\t')) content = content[..end].TrimEnd();
            text = content;
            return true;
        }

        private static bool IsThematicBreak(string line)
        {
            if (Indent(line) > 3) return false;
            var marker = '\0';
            var count = 0;
            foreach (var c in line)
            {
                if (c is ' ' or '\t') continue;
                if (c is not ('-' or '*' or '_')) return false;
                if (marker == '\0') marker = c; else if (c != marker) return false;
                count++;
            }
            return count >= 3;
        }

        private static bool IsQuote(string line) => Indent(line) <= 3 && Indent(line) < line.Length && line[Indent(line)] == '>';

        private static string StripQuote(string line)
        {
            var k = Indent(line) + 1;
            if (k < line.Length && line[k] == ' ') k++;
            return line[k..];
        }

        private readonly record struct ListMarker(int Indent, bool Ordered, int Number, int ContentOffset, string Rest);

        private static bool TryListMarker(string line, out ListMarker marker)
        {
            marker = default;
            var indent = Indent(line);
            if (indent >= line.Length) return false;
            var k = indent;
            var ordered = false;
            var number = 0;
            var c = line[k];
            if (c is '-' or '*' or '+') k++;
            else if (char.IsAsciiDigit(c))
            {
                var d = k;
                while (d < line.Length && char.IsAsciiDigit(line[d])) d++;
                if (d - k > 9 || d >= line.Length || line[d] is not ('.' or ')')) return false;
                number = int.Parse(line.AsSpan(k, d - k), CultureInfo.InvariantCulture);
                ordered = true;
                k = d + 1;
            }
            else return false;
            if (k < line.Length && line[k] != ' ' && line[k] != '\t') return false;
            var spaces = 0;
            while (k + spaces < line.Length && line[k + spaces] is ' ' or '\t') spaces++;
            var restStart = k + spaces;
            if (restStart >= line.Length) marker = new ListMarker(indent, ordered, number, k + 1, string.Empty);
            else
            {
                if (spaces > 4) spaces = 1;
                marker = new ListMarker(indent, ordered, number, k + spaces, line[(k + spaces)..]);
            }
            return true;
        }

        // A list item may interrupt a paragraph only when it is a non-empty bullet or an ordered item numbered 1.
        private static bool InterruptsParagraph(string line) =>
            TryListMarker(line, out var m) && m.Rest.Length > 0 && (!m.Ordered || m.Number == 1);

        private static readonly Regex DetailsOpen = new(@"^\s*<details\s+class=""(?<c>ocr-extraction|speaker-notes|visual-diagnostics)""\s*>\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex DetailsCloseLine = new(@"^\s*</details>\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex SummaryLine = new(@"^\s*<summary>(?<t>.*)</summary>\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex RawImg = new(@"^\s*<img\s+(?<a>(?:[A-Za-z-]+\s*=\s*""[^""]*""\s*)+)/?>\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex Attr = new(@"(?<n>[A-Za-z-]+)\s*=\s*""(?<v>[^""]*)""", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex SpanTag = new(@"^<span\s+style=""color:#(?<h>[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?)"">$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex CodeSpan = new(@"`[^`\r\n]+`", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex Xywh = new(@"xywh=pixel:(\d+),(\d+),(\d+),(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        private static readonly Regex TableSpanHint = new(@"^\s*<!--\s*docredock:table-spans(?<v>(?:\s[^>]*?)?)\s*-->\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        private static bool IsCommentStart(string line) => line.TrimStart().StartsWith("<!--", StringComparison.Ordinal);

        private static bool IsBlockStart(IReadOnlyList<string> lines, int i)
        {
            var line = lines[i];
            if (TryFenceOpen(line, out _, out _) || TryHeading(line, out _, out _) || IsThematicBreak(line) || IsQuote(line) ||
                InterruptsParagraph(line) || IsCommentStart(line)) return true;
            if (line.Contains('<'))
            {
                if (DetailsOpen.IsMatch(line) || DetailsCloseLine.IsMatch(line) || RawImg.IsMatch(line)) return true;
            }
            return IsTableStart(lines, i);
        }

        // ------------------------------------------------------------------ blocks

        private void Blocks(IReadOnlyList<string> lines, int depth, bool tight)
        {
            if (depth > MaxDepth)
            {
                _body.Append("<pre>").Append(Enc(string.Join('\n', lines))).Append("</pre>\n");
                return;
            }
            var i = 0;
            var n = lines.Count;
            while (i < n)
            {
                if (IsBlank(lines[i])) { i++; continue; }
                var before = _body.Length;
                try { i = Block(lines, i, depth, tight); }
                catch (RegexMatchTimeoutException)
                {
                    // A pathological line: show that one line as encoded text and carry on.
                    _body.Length = before;
                    _body.Append("<p>").Append(Enc(lines[i].Trim())).Append("</p>\n");
                    i++;
                }
            }
        }

        // Reads one block starting at lines[i] and returns the index of the next unread line.
        private int Block(IReadOnlyList<string> lines, int i, int depth, bool tight)
        {
            var n = lines.Count;
            var line = lines[i];
            var startOfBody = depth == 0 && !_sawBlock;
            var sawBefore = _sawBlock;
            _sawBlock = true;

            if (TryFenceOpen(line, out var fenceLength, out var info)) return Fence(lines, i, fenceLength, info);
            if (TryHeading(line, out var level, out var headingText)) { Heading(level, headingText, startOfBody); return i + 1; }
            if (IsThematicBreak(line)) { _body.Append("<hr>\n"); return i + 1; }
            if (IsQuote(line))
            {
                var inner = new List<string>();
                while (i < n && IsQuote(lines[i])) inner.Add(StripQuote(lines[i++]));
                _body.Append("<blockquote>\n");
                Blocks(inner, depth + 1, false);
                _body.Append("</blockquote>\n");
                return i;
            }
            if (TryListMarker(line, out _)) return List(lines, i, depth, tight);
            if (IsTableStart(lines, i)) return Table(lines, i, null);
            if (line.Contains('<') && TableSpanHint.Match(line) is { Success: true } hint)
            {
                // The serializer's merged-cell hint: used when a table starts on the next line, dropped otherwise.
                return IsTableStart(lines, i + 1) ? Table(lines, i + 1, hint.Groups["v"].Value) : i + 1;
            }
            if (line.Contains('<'))
            {
                var open = DetailsOpen.Match(line);
                if (open.Success) return Details(lines, i, open.Groups["c"].Value, depth);
                if (DetailsCloseLine.IsMatch(line)) return i + 1;
                var img = RawImg.Match(line);
                if (img.Success) { RawImage(img.Groups["a"].Value); return i + 1; }
            }
            if (IsCommentStart(line))
            {
                _sawBlock = sawBefore; // a leading comment does not count as content (the TOC still follows the first H1)
                return Comment(lines, i);
            }
            return Paragraph(lines, i, tight);
        }

        private int Paragraph(IReadOnlyList<string> lines, int i, bool tight)
        {
            var n = lines.Count;
            var text = new StringBuilder();
            var first = true;
            while (i < n && !IsBlank(lines[i]))
            {
                if (!first && IsBlockStart(lines, i)) break;
                if (!first) text.Append('\n');
                text.Append(lines[i].TrimStart());
                first = false;
                i++;
            }
            var value = text.ToString().TrimEnd();
            if (!tight) _body.Append("<p>");
            Inline(_body, value, false);
            _body.Append(tight ? "\n" : "</p>\n");
            return i;
        }

        private void Heading(int level, string text, bool firstBlock)
        {
            var plain = PlainOf(text);
            var id = UniqueHeadingId(plain);
            if (level == 1 && _firstH1 is null) _firstH1 = plain;
            _headings.Add(new Heading(level, id, plain));
            _body.Append("<h").Append(level).Append(" id=\"").Append(id).Append("\">");
            Inline(_body, text, false);
            _body.Append("</h").Append(level).Append(">\n");
            if (level == 1 && firstBlock && _tocInsertAt < 0) _tocInsertAt = _body.Length;
        }

        private string UniqueHeadingId(string plain)
        {
            var slug = new StringBuilder();
            var pendingDash = false;
            foreach (var c in plain)
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (pendingDash && slug.Length > 0) slug.Append('-');
                    pendingDash = false;
                    slug.Append(char.ToLowerInvariant(c));
                }
                else pendingDash = true;
            }
            var baseId = slug.Length == 0 ? "section-" + (_headings.Count + 1).ToString(CultureInfo.InvariantCulture) : slug.ToString();
            if (baseId.StartsWith("asset-", StringComparison.Ordinal)) baseId = "h-" + baseId;
            return ReserveId(baseId);
        }

        // Claims baseId, or baseId-N with the lowest N not yet tried for that base (kept per base so that
        // many identical headings stay linear instead of re-probing from 2 each time).
        private string ReserveId(string baseId)
        {
            if (_usedIds.Add(baseId)) return baseId;
            var n = _nextSuffix.GetValueOrDefault(baseId, 2);
            string id;
            while (!_usedIds.Add(id = baseId + "-" + n.ToString(CultureInfo.InvariantCulture))) n++;
            _nextSuffix[baseId] = n + 1;
            return id;
        }

        private int Fence(IReadOnlyList<string> lines, int i, int length, string info)
        {
            var n = lines.Count;
            var indent = Indent(lines[i]);
            i++;
            var code = new StringBuilder();
            var firstLine = true;
            while (i < n && !IsFenceClose(lines[i], length))
            {
                if (!firstLine) code.Append('\n');
                code.Append(StripIndent(lines[i], indent));
                firstLine = false;
                i++;
            }
            if (i < n) i++;
            var lang = info.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            lang = new string(lang.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '+' or '#' or '.' or '-').Take(40).ToArray());
            var source = code.ToString();
            var codeClass = lang.Length > 0 ? " class=\"language-" + lang + "\"" : string.Empty;
            if (lang.Equals("mermaid", StringComparison.OrdinalIgnoreCase))
            {
                var svg = SafeMermaidSvg(source);
                if (svg is not null)
                {
                    _body.Append("<figure class=\"diagram\">").Append(svg.Trim()).Append("</figure>\n<details><summary>Mermaid</summary><pre><code class=\"language-mermaid\">")
                        .Append(Enc(source)).Append("</code></pre></details>\n");
                    return i;
                }
            }
            _body.Append("<pre><code").Append(codeClass).Append('>').Append(Enc(source)).Append("</code></pre>\n");
            return i;
        }

        private const int MaxMermaidSourceChars = 100_000;
        private const int MaxMermaidLabelChars = 2_000;

        private static readonly Regex ForbiddenSvg = new(
            @"<\s*(script|foreignObject|iframe|object|embed|image|use|a)\b|\son[a-z]+\s*=|javascript:|(?:href|src)\s*=|url\(\s*[""']?(?!#)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
        private static readonly Regex SvgId = new(@"(?<=\sid="")[^""]*(?="")", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
        private static readonly Regex SvgUrlRef = new(@"url\(\s*#(?<i>[^)\s'""]+)\s*\)", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
        private static readonly Regex SvgHashAttr = new(@"=""#(?<i>[^""]*)""", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(2));

        // The renderers cap what they draw: a source or a label (approximated by a source line) beyond
        // these sizes stays a code block instead of costing seconds and megabytes of SVG.
        private static bool MermaidTooLarge(string source)
        {
            if (source.Length > MaxMermaidSourceChars) return true;
            var start = 0;
            while (start <= source.Length)
            {
                var end = source.IndexOf('\n', start);
                if (end < 0) end = source.Length;
                if (end - start > MaxMermaidLabelChars) return true;
                start = end + 1;
            }
            return false;
        }

        private string? SafeMermaidSvg(string source)
        {
            if (MermaidTooLarge(source)) return null;
            string? svg;
            try { svg = MermaidSvgPreviewRenderer.Render(source) ?? MermaidDiagramSvg.Render(source); }
            catch (Exception) { return null; }
            if (string.IsNullOrWhiteSpace(svg)) return null;
            var trimmed = svg.TrimStart();
            if (!trimmed.StartsWith("<svg", StringComparison.Ordinal)) return null;
            try
            {
                // Only markup is checked: escaped label text ("set once=1", "GET url(/api)") is harmless.
                if (ForbiddenSvg.IsMatch(WithoutLabelText(svg))) return null;
                return PrefixSvgIds(svg);
            }
            catch (RegexMatchTimeoutException) { return null; }
        }

        // Drops the character data inside <text>, <tspan>, <title> and <desc>; every tag, attribute and any
        // other text (for example a <style> body) is kept for the safety check.
        private static string WithoutLabelText(string svg)
        {
            var sb = new StringBuilder(svg.Length);
            var depth = 0;
            var k = 0;
            while (k < svg.Length)
            {
                if (svg[k] != '<')
                {
                    var next = svg.IndexOf('<', k);
                    if (next < 0) next = svg.Length;
                    if (depth == 0) sb.Append(svg, k, next - k);
                    k = next;
                    continue;
                }
                var close = svg.IndexOf('>', k);
                if (close < 0) close = svg.Length - 1;
                var tag = svg.AsSpan(k, close - k + 1);
                sb.Append(tag);
                var name = tag[1..];
                var closing = name.Length > 0 && name[0] == '/';
                if (closing) name = name[1..];
                var e = 0;
                while (e < name.Length && (char.IsAsciiLetterOrDigit(name[e]) || name[e] == ':')) e++;
                var element = name[..e];
                if (element.Equals("text", StringComparison.OrdinalIgnoreCase) || element.Equals("tspan", StringComparison.OrdinalIgnoreCase) ||
                    element.Equals("title", StringComparison.OrdinalIgnoreCase) || element.Equals("desc", StringComparison.OrdinalIgnoreCase))
                {
                    if (closing) depth = Math.Max(0, depth - 1);
                    else if (!tag.EndsWith("/>")) depth++;
                }
                k = close + 1;
            }
            return sb.ToString();
        }

        // Every id in a diagram becomes "dN-id" (reserved in the same set as headings and images), and the
        // url(#id) / ="#id" references inside that SVG follow, so two diagrams or a heading never share an id.
        private string PrefixSvgIds(string svg)
        {
            var ids = SvgId.Matches(svg).Select(m => m.Value).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (ids.Count == 0) return svg;
            var prefix = "d" + (++_diagramCount).ToString(CultureInfo.InvariantCulture) + "-";
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in ids) map[id] = ReserveId(prefix + id);
            svg = SvgId.Replace(svg, m => map.TryGetValue(m.Value, out var v) ? v : m.Value);
            svg = SvgUrlRef.Replace(svg, m => map.TryGetValue(m.Groups["i"].Value, out var v) ? "url(#" + v + ")" : m.Value);
            return SvgHashAttr.Replace(svg, m => map.TryGetValue(m.Groups["i"].Value, out var v) ? "=\"#" + v + "\"" : m.Value);
        }

        private int Comment(IReadOnlyList<string> lines, int i)
        {
            var n = lines.Count;
            var first = lines[i].TrimStart();
            i++;
            string inner;
            var trailing = string.Empty;
            // "<!-->" and "<!--->" are complete (empty) comments in HTML; the close of any other comment is
            // searched line by line so that a long unterminated comment is scanned once, not once per line.
            var emptyLength = first.StartsWith("<!-->", StringComparison.Ordinal) ? 5 : first.StartsWith("<!--->", StringComparison.Ordinal) ? 6 : 0;
            if (emptyLength > 0)
            {
                inner = string.Empty;
                trailing = first[emptyLength..].Trim();
            }
            else
            {
                var parts = new List<string>();
                var end = first.IndexOf("-->", 4, StringComparison.Ordinal);
                if (end >= 0)
                {
                    parts.Add(first[4..end]);
                    trailing = first[(end + 3)..].Trim();
                }
                else
                {
                    parts.Add(first[4..]);
                    while (i < n)
                    {
                        var line = lines[i++];
                        var close = line.IndexOf("-->", StringComparison.Ordinal);
                        if (close < 0) { parts.Add(line); continue; }
                        parts.Add(line[..close]);
                        trailing = line[(close + 3)..].Trim();
                        break;
                    }
                }
                inner = string.Join('\n', parts);
            }
            _body.Append("<!--").Append(NeutralizeComment(inner)).Append("-->\n");
            if (trailing.Length > 0) { _body.Append("<p>"); Inline(_body, trailing, false); _body.Append("</p>\n"); }
            return i;
        }

        private static string NeutralizeComment(string inner)
        {
            var sb = new StringBuilder(inner.Length + 4);
            foreach (var c in inner)
            {
                if (c == '<') sb.Append("&lt;");
                else if (c == '>') sb.Append("&gt;");
                else sb.Append(c);
            }
            var s = sb.ToString();
            if (s.Contains("--", StringComparison.Ordinal))
            {
                var spaced = new StringBuilder(s.Length + 8);
                for (var k = 0; k < s.Length; k++)
                {
                    spaced.Append(s[k]);
                    if (s[k] == '-' && k + 1 < s.Length && s[k + 1] == '-') spaced.Append(' ');
                }
                s = spaced.ToString();
            }
            if (s.EndsWith('-')) s += " ";
            return s;
        }

        private int Details(IReadOnlyList<string> lines, int i, string cls, int depth)
        {
            var n = lines.Count;
            var end = n;
            var nesting = 1;
            var fence = 0;
            for (var j = i + 1; j < n; j++)
            {
                var l = lines[j];
                if (fence > 0) { if (IsFenceClose(l, fence)) fence = 0; continue; }
                if (TryFenceOpen(l, out var len, out _)) { fence = len; continue; }
                if (DetailsOpen.IsMatch(l)) nesting++;
                else if (DetailsCloseLine.IsMatch(l) && --nesting == 0) { end = j; break; }
            }
            var inner = new List<string>();
            for (var j = i + 1; j < end; j++) inner.Add(lines[j]);
            _body.Append("<details class=\"").Append(cls).Append("\">\n");
            var s = 0;
            while (s < inner.Count && IsBlank(inner[s])) s++;
            if (s < inner.Count)
            {
                var m = SummaryLine.Match(inner[s]);
                if (m.Success)
                {
                    _body.Append("<summary>");
                    Inline(_body, m.Groups["t"].Value.Trim(), false);
                    _body.Append("</summary>\n");
                    inner.RemoveRange(0, s + 1);
                }
            }
            Blocks(inner, depth + 1, false);
            _body.Append("</details>\n");
            return end < n ? end + 1 : n;
        }

        // ------------------------------------------------------------------ lists

        private int List(IReadOnlyList<string> lines, int i, int depth, bool tightOuter)
        {
            var n = lines.Count;
            TryListMarker(lines[i], out var head);
            var b = head.Indent;
            var ordered = head.Ordered;
            var start = head.Number;
            var items = new List<List<string>>();
            var loose = false;

            while (i < n)
            {
                if (!TryListMarker(lines[i], out var m) || m.Indent != b || m.Ordered != ordered) break;
                var item = new List<string> { m.Rest };
                var width = m.ContentOffset - b;
                i++;
                while (i < n)
                {
                    var l = lines[i];
                    if (IsBlank(l))
                    {
                        var j = i;
                        while (j < n && IsBlank(lines[j])) j++;
                        if (j < n && Indent(lines[j]) > b)
                        {
                            for (var k = i; k < j; k++) item.Add(string.Empty);
                            i = j;
                            loose = true;
                            continue;
                        }
                        break;
                    }
                    var ind = Indent(l);
                    if (ind > b) { item.Add(StripIndent(l, b + width)); i++; continue; }
                    if (TryListMarker(l, out var next) && next.Indent <= b) break;
                    if (IsBlockStart(lines, i)) break;
                    item.Add(l.TrimStart()); // lazy continuation of the item's paragraph
                    i++;
                }
                items.Add(item);
                // A blank line between this item and the next sibling makes the list loose.
                if (i < n && IsBlank(lines[i]))
                {
                    var j = i;
                    while (j < n && IsBlank(lines[j])) j++;
                    if (j < n && TryListMarker(lines[j], out var sibling) && sibling.Indent == b && sibling.Ordered == ordered)
                    {
                        loose = true;
                        i = j;
                    }
                    else break;
                }
            }

            _body.Append(ordered ? start != 1 ? "<ol start=\"" + start.ToString(CultureInfo.InvariantCulture) + "\">\n" : "<ol>\n" : "<ul>\n");
            foreach (var item in items)
            {
                _body.Append("<li>");
                Blocks(item, depth + 1, !loose);
                _body.Append("</li>\n");
            }
            _body.Append(ordered ? "</ol>\n" : "</ul>\n");
            return i;
        }

        // ------------------------------------------------------------------ tables

        private static bool IsTableStart(IReadOnlyList<string> lines, int i)
        {
            if (i + 1 >= lines.Count) return false;
            var header = lines[i];
            var delimiter = lines[i + 1];
            if (header.IndexOf('|') < 0 || delimiter.IndexOf('|') < 0 || delimiter.IndexOf('-') < 0) return false;
            var aligns = ParseDelimiter(delimiter);
            return aligns is not null && aligns.Length == SplitRow(header).Count;
        }

        private static string[]? ParseDelimiter(string line)
        {
            var cells = SplitRow(line);
            var result = new string[cells.Count];
            for (var k = 0; k < cells.Count; k++)
            {
                var c = cells[k];
                if (c.Length == 0) return null;
                var left = c[0] == ':';
                var right = c[^1] == ':';
                var core = c.Trim(':');
                if (core.Length == 0 || core.Any(ch => ch != '-')) return null;
                result[k] = left && right ? "center" : right ? "right" : left ? "left" : string.Empty;
            }
            return result;
        }

        private static List<string> SplitRow(string line)
        {
            var s = line.Trim();
            var cells = new List<string>();
            var current = new StringBuilder();
            var k = 0;
            var endedOnSeparator = false;
            if (s.Length > 0 && s[0] == '|') k = 1;
            for (; k < s.Length; k++)
            {
                var c = s[k];
                endedOnSeparator = false;
                if (c == '\\' && k + 1 < s.Length) { current.Append(c).Append(s[++k]); continue; }
                if (c == '|') { cells.Add(current.ToString().Trim()); current.Clear(); endedOnSeparator = true; continue; }
                current.Append(c);
            }
            if (!endedOnSeparator) cells.Add(current.ToString().Trim());
            return cells;
        }

        private int Table(IReadOnlyList<string> lines, int i, string? spanHint)
        {
            var n = lines.Count;
            var header = SplitRow(lines[i]);
            var aligns = ParseDelimiter(lines[i + 1])!;
            i += 2;
            var bodyRows = new List<List<string>>();
            while (i < n)
            {
                var l = lines[i];
                if (IsBlank(l) || l.IndexOf('|') < 0) break;
                var t = l.TrimStart();
                if (t[0] is '#' or '>' || t.StartsWith("```", StringComparison.Ordinal)) break;
                bodyRows.Add(SplitRow(l));
                i++;
            }
            var spans = spanHint is null ? null : ParseSpans(spanHint, bodyRows.Count + 1, aligns.Length);
            // A column without an explicit alignment whose body cells are all numbers is right-aligned
            // (header included), so digits line up as they do in a spreadsheet.
            aligns = (string[])aligns.Clone();
            for (var c = 0; c < aligns.Length; c++)
                if (aligns[c].Length == 0 && IsNumericColumn(bodyRows, c)) aligns[c] = "right";

            _body.Append("<div class=\"table-scroll\"><table>\n<thead><tr>");
            for (var c = 0; c < aligns.Length; c++)
            {
                if (spans is not null && spans.Covered.Contains((0, c))) continue;
                var cs = spans is not null && spans.Origins.TryGetValue((0, c), out var span) ? span.ColSpan : 1;
                _body.Append("<th scope=\"").Append(cs > 1 ? "colgroup" : "col").Append('"');
                AppendSpanAttributes(0, c, spans);
                _body.Append(AlignClass(aligns[c])).Append('>');
                Inline(_body, CellText(c < header.Count ? header[c] : string.Empty), true);
                _body.Append("</th>");
            }
            _body.Append("</tr></thead>\n<tbody>\n");
            for (var r = 0; r < bodyRows.Count; r++)
            {
                var cells = bodyRows[r];
                _body.Append("<tr>");
                for (var c = 0; c < aligns.Length; c++)
                {
                    if (spans is not null && spans.Covered.Contains((r + 1, c))) continue;
                    _body.Append("<td");
                    AppendSpanAttributes(r + 1, c, spans);
                    _body.Append(AlignClass(aligns[c])).Append('>');
                    Inline(_body, CellText(c < cells.Count ? cells[c] : string.Empty), true);
                    _body.Append("</td>");
                }
                _body.Append("</tr>\n");
            }
            _body.Append("</tbody>\n</table></div>\n");
            return i;
        }

        private sealed record TableSpans(Dictionary<(int Row, int Column), (int RowSpan, int ColSpan)> Origins, HashSet<(int Row, int Column)> Covered);

        private void AppendSpanAttributes(int row, int column, TableSpans? spans)
        {
            if (spans is null || !spans.Origins.TryGetValue((row, column), out var span)) return;
            if (span.RowSpan > 1) _body.Append(" rowspan=\"").Append(span.RowSpan.ToString(CultureInfo.InvariantCulture)).Append('"');
            if (span.ColSpan > 1) _body.Append(" colspan=\"").Append(span.ColSpan.ToString(CultureInfo.InvariantCulture)).Append('"');
        }

        // "r,c,rs,cs;..." -> merge origins and the cells they cover. Any malformed or inconsistent
        // entry (non-integer, out of the table, overlapping, header row reaching into the body)
        // rejects the whole hint: the table is then rendered without spans.
        private static TableSpans? ParseSpans(string hint, int rowCount, int columnCount)
        {
            var origins = new Dictionary<(int, int), (int, int)>();
            var covered = new HashSet<(int, int)>();
            var occupied = new HashSet<(int, int)>();
            var entries = hint.Trim().Split(';');
            foreach (var entry in entries)
            {
                var parts = entry.Trim().Split(',');
                if (parts.Length != 4) return null;
                var v = new int[4];
                for (var k = 0; k < 4; k++)
                    if (!int.TryParse(parts[k].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out v[k])) return null;
                var (r, c, rs, cs) = (v[0], v[1], v[2], v[3]);
                if (rs < 1 || cs < 1 || (rs == 1 && cs == 1)) return null;
                if (r >= rowCount || c >= columnCount || rs > rowCount - r || cs > columnCount - c) return null;
                if (r == 0 && rs > 1) return null;
                for (var rr = r; rr < r + rs; rr++)
                for (var cc = c; cc < c + cs; cc++)
                {
                    if (!occupied.Add((rr, cc))) return null;
                    if (rr != r || cc != c) covered.Add((rr, cc));
                }
                origins[(r, c)] = (rs, cs);
            }
            return new TableSpans(origins, covered);
        }

        private static string AlignClass(string align) => align.Length == 0 ? string.Empty : " class=\"align-" + align + "\"";

        private static readonly Regex NumericCell = new(
            @"^[(（]?[+\-−]?[¥￥$€£]?(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?(?:[eE][+\-]?\d+)?[)）]?%?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        // True when the column has at least one number and every other non-empty cell is a number or a
        // bare dash placeholder. Cells with any markup (bold, links, code) do not count as numbers.
        private static bool IsNumericColumn(List<List<string>> rows, int column)
        {
            var numbers = 0;
            foreach (var row in rows)
            {
                if (column >= row.Count) continue;
                var cell = row[column].Replace("\\", string.Empty, StringComparison.Ordinal).Trim();
                if (cell.Length == 0 || cell is "-" or "—" or "–" or "－" or "―") continue;
                if (!NumericCell.IsMatch(cell)) return false;
                numbers++;
            }
            return numbers > 0;
        }

        // GFM: an escaped pipe inside a code span is a literal pipe.
        private static string CellText(string cell) =>
            cell.Contains("\\|", StringComparison.Ordinal)
                ? CodeSpan.Replace(cell, m => m.Value.Replace("\\|", "|", StringComparison.Ordinal))
                : cell;

        // ------------------------------------------------------------------ inline

        private static bool NeedsParse(string text)
        {
            foreach (var c in text)
                if (c is '\\' or '&' or '<' or '*' or '_' or '`' or '~' or '[') return true;
            return false;
        }

        private static string PlainOf(string text)
        {
            if (!NeedsParse(text)) return text.Trim();
            try { return MarkdownInlineParser.PlainText(MarkdownInlineParser.Parse(text)).Replace('\n', ' ').Trim(); }
            catch (RegexMatchTimeoutException) { return text.Trim(); }
        }

        private void Inline(StringBuilder sb, string text, bool cell)
        {
            if (text.Length == 0) return;
            if (!NeedsParse(text)) { TextOut(sb, text, cell); return; }
            IReadOnlyList<MarkdownInline> nodes;
            try { nodes = MarkdownInlineParser.Parse(text); }
            catch (RegexMatchTimeoutException) { TextOut(sb, text, cell); return; }
            InlineNodes(sb, nodes, cell);
        }

        private void InlineNodes(StringBuilder sb, IReadOnlyList<MarkdownInline> nodes, bool cell)
        {
            var open = new List<string>();
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case MarkdownInlineText t: TextOut(sb, t.Text, cell); break;
                    case MarkdownInlineCode code: sb.Append("<code>").Append(Enc(code.Text)).Append("</code>"); break;
                    case MarkdownInlineStrong s: Wrapped(sb, "strong", s.Children, s.Text, cell); break;
                    case MarkdownInlineEmphasis e: Wrapped(sb, "em", e.Children, e.Text, cell); break;
                    case MarkdownInlineStrike k: Wrapped(sb, "del", k.Children, k.Text, cell); break;
                    case MarkdownInlineRawHtml raw: RawTag(sb, raw.Markup, open); break;
                    case MarkdownInlineImage image: ImageNode(sb, image); break;
                    case MarkdownInlineLink link: LinkNode(sb, link, cell); break;
                }
            }
            for (var k = open.Count - 1; k >= 0; k--) sb.Append("</").Append(open[k]).Append('>');
        }

        private void Wrapped(StringBuilder sb, string tag, IReadOnlyList<MarkdownInline>? children, string text, bool cell)
        {
            sb.Append('<').Append(tag).Append('>');
            if (children is not null) InlineNodes(sb, children, cell); else sb.Append(Enc(text));
            sb.Append("</").Append(tag).Append('>');
        }

        private static void RawTag(StringBuilder sb, string markup, List<string> open)
        {
            var m = markup.Trim();
            if (m.StartsWith("<br", StringComparison.OrdinalIgnoreCase)) { sb.Append("<br>"); return; }
            var span = SpanTag.Match(m);
            if (span.Success)
            {
                var hex = span.Groups["h"].Value;
                sb.Append("<span style=\"color:#").Append(hex).Append('"');
                var contrast = LowContrastClass(hex);
                if (contrast is not null) sb.Append(" class=\"").Append(contrast).Append('"');
                sb.Append('>');
                open.Add("span");
                return;
            }
            var lower = m.ToLowerInvariant();
            if (lower is "<u>" or "<ins>" or "<del>" or "<mark>")
            {
                sb.Append(lower);
                open.Add(lower[1..^1]);
                return;
            }
            if (lower is "</u>" or "</ins>" or "</del>" or "</mark>" or "</span>")
            {
                var name = lower[2..^1];
                var idx = open.LastIndexOf(name);
                if (idx < 0) return;
                for (var k = open.Count - 1; k >= idx; k--) sb.Append("</").Append(open[k]).Append('>');
                open.RemoveRange(idx, open.Count - idx);
            }
            // details / summary tags inside running text are dropped.
        }

        private static bool IsWide(char c) => c is >= '⺀' and <= '鿿' or >= '가' and <= '힯' or >= '＀' and <= '￯';

        private static void TextOut(StringBuilder sb, string text, bool cell)
        {
            if (text.IndexOf('\n') < 0) { sb.Append(Enc(text)); return; }
            var segments = text.Split('\n');
            for (var k = 0; k < segments.Length; k++)
            {
                var seg = segments[k];
                if (k == segments.Length - 1) { sb.Append(Enc(seg)); break; }
                var trimmed = seg.TrimEnd(' ');
                var hard = seg.Length - trimmed.Length >= 2;
                sb.Append(Enc(trimmed));
                if (cell || hard) { sb.Append("<br>\n"); continue; }
                var next = segments[k + 1];
                var joinsWide = trimmed.Length > 0 && next.Length > 0 && IsWide(trimmed[^1]) && IsWide(next[0]);
                if (!joinsWide) sb.Append('\n');
            }
        }

        // ------------------------------------------------------------------ links and images

        private void LinkNode(StringBuilder sb, MarkdownInlineLink link, bool cell)
        {
            if (_inLink) { LinkContent(sb, link, cell); return; } // a link inside link text is only its text, never a nested <a>
            var dest = link.Destination;
            var hash = dest.IndexOf('#');
            var path = hash >= 0 ? dest[..hash] : dest;
            var fragment = hash >= 0 ? dest[(hash + 1)..] : string.Empty;
            if (path.Length > 0 && TryFindAsset(path, out var key, out _))
            {
                var id = AssetId(key);
                if (_linkedSet.Add(key)) _linked.Add(key);
                sb.Append("<a href=\"#").Append(id).Append('"');
                var m = Xywh.Match(fragment);
                if (m.Success)
                    sb.Append(" title=\"x=").Append(m.Groups[1].Value).Append(", y=").Append(m.Groups[2].Value)
                        .Append(", w=").Append(m.Groups[3].Value).Append(", h=").Append(m.Groups[4].Value).Append('"');
                sb.Append('>'); LinkContent(sb, link, cell); sb.Append("</a>");
                return;
            }
            if (IsSafeAbsoluteLink(dest))
            {
                sb.Append("<a href=\"").Append(Enc(dest)).Append("\">"); LinkContent(sb, link, cell); sb.Append("</a>");
                return;
            }
            if (dest.Length > 1 && dest[0] == '#' && dest.All(c => char.IsLetterOrDigit(c) || c is '#' or '-' or '_'))
            {
                sb.Append("<a href=\"").Append(Enc(dest)).Append("\">"); LinkContent(sb, link, cell); sb.Append("</a>");
                return;
            }
            // Unsafe schemes and relative file paths: no file exists beside the HTML, so keep the text only.
            LinkContent(sb, link, cell);
        }

        private void LinkContent(StringBuilder sb, MarkdownInlineLink link, bool cell)
        {
            if (link.Children is null) { sb.Append(Enc(link.Text)); return; }
            var was = _inLink;
            _inLink = true;
            try { InlineNodes(sb, link.Children, cell); }
            finally { _inLink = was; }
        }

        private static bool IsSafeAbsoluteLink(string value)
        {
            if (value.Length == 0 || value.StartsWith("//", StringComparison.Ordinal) || value.Any(char.IsControl)) return false;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
            return uri.Scheme is "http" or "https" or "mailto";
        }

        private void ImageNode(StringBuilder sb, MarkdownInlineImage image) =>
            EmitImage(sb, image.Destination, image.AltText, null, null);

        private void RawImage(string attributes)
        {
            string src = string.Empty, alt = string.Empty;
            string? width = null, height = null;
            foreach (Match m in Attr.Matches(attributes))
            {
                var value = WebUtility.HtmlDecode(m.Groups["v"].Value);
                switch (m.Groups["n"].Value.ToLowerInvariant())
                {
                    case "src": src = value; break;
                    case "alt": alt = value; break;
                    case "width" when IsShortNumber(value): width = value; break;
                    case "height" when IsShortNumber(value): height = value; break;
                }
            }
            _body.Append("<p class=\"image\">");
            EmitImage(_body, src, alt, width, height);
            _body.Append("</p>\n");
        }

        private static readonly string[] EmbeddableTypes =
            ["image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp", "image/svg+xml"];

        private void EmitImage(StringBuilder sb, string reference, string alt, string? width, string? height,
            bool preserveSourceAspect = false)
        {
            if (reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                EmitDataImage(sb, reference, alt, width, height, preserveSourceAspect);
                return;
            }
            if (!TryFindAsset(reference, out var key, out var asset))
            {
                Placeholder(sb, alt, null);
                var external = reference.StartsWith("//", StringComparison.Ordinal) || HasScheme(reference);
                _diagnostics.Add(external
                    ? new ReadableHtmlDiagnostic(ImageNotEmbeddable, $"External image '{Truncate(reference)}' is not embedded.")
                    : new ReadableHtmlDiagnostic(ImageMissing, $"Image '{Truncate(reference)}' was not found among the exported assets."));
                return;
            }

            var id = _shown.Add(key) ? AssetId(key) : null;
            var type = (asset.MediaType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
            if (type == "image/jpg") type = "image/jpeg";
            if (Array.IndexOf(EmbeddableTypes, type) < 0 || !SignatureMatches(type, asset.Content.Span))
            {
                Placeholder(sb, alt, id);
                _diagnostics.Add(new ReadableHtmlDiagnostic(ImageNotEmbeddable,
                    $"Image '{Truncate(reference)}' ({(type.Length == 0 ? "unknown type" : type)}) is not an embeddable image type or its content does not match."));
                return;
            }
            var length = asset.Content.Length;
            if (length > _options.MaxImageBytes || _embeddedBytes + length > _options.MaxTotalImageBytes)
            {
                Placeholder(sb, alt, id);
                _diagnostics.Add(new ReadableHtmlDiagnostic(ImageEmbedSkipped,
                    length > _options.MaxImageBytes
                        ? $"Image '{Truncate(reference)}' ({length} bytes) exceeds the per-image limit of {_options.MaxImageBytes} bytes."
                        : $"Image '{Truncate(reference)}' ({length} bytes) would exceed the total image limit of {_options.MaxTotalImageBytes} bytes."));
                return;
            }
            if (!_dataUris.TryGetValue(key, out var uri))
                _dataUris[key] = uri = "data:" + type + ";base64," + Convert.ToBase64String(asset.Content.Span);
            _embeddedCount++;
            _embeddedBytes += length;
            sb.Append("<img");
            if (id is not null) sb.Append(" id=\"").Append(id).Append('"');
            sb.Append(" src=\"").Append(uri).Append("\" alt=\"").Append(Enc(alt)).Append('"').Append(Size(width, height, preserveSourceAspect)).Append('>');
        }

        // An inline data: image gets the same signature check, per-image and total limits and counters as an asset.
        private void EmitDataImage(StringBuilder sb, string reference, string alt, string? width, string? height,
            bool preserveSourceAspect)
        {
            if (!IsSafeDataImage(reference))
            {
                Placeholder(sb, alt, null);
                _diagnostics.Add(new ReadableHtmlDiagnostic(ImageNotEmbeddable, "An inline data: image is not an embeddable PNG, JPEG, GIF or WebP."));
                return;
            }
            var comma = reference.IndexOf(',');
            var type = reference[5..reference.IndexOf(';')].ToLowerInvariant();
            var payload = reference.AsSpan(comma + 1);
            var estimate = payload.Length / 4L * 3;
            if (estimate - 2 > _options.MaxImageBytes)
            {
                Placeholder(sb, alt, null);
                _diagnostics.Add(new ReadableHtmlDiagnostic(ImageEmbedSkipped,
                    $"An inline data: image (about {estimate} bytes) exceeds the per-image limit of {_options.MaxImageBytes} bytes."));
                return;
            }
            byte[] bytes;
            try { bytes = Convert.FromBase64String(payload.ToString()); }
            catch (FormatException) { bytes = []; }
            if (bytes.Length == 0 || !SignatureMatches(type, bytes))
            {
                Placeholder(sb, alt, null);
                _diagnostics.Add(new ReadableHtmlDiagnostic(ImageNotEmbeddable, "An inline data: image is malformed or its content does not match its type."));
                return;
            }
            var length = bytes.Length;
            if (length > _options.MaxImageBytes || _embeddedBytes + length > _options.MaxTotalImageBytes)
            {
                Placeholder(sb, alt, null);
                _diagnostics.Add(new ReadableHtmlDiagnostic(ImageEmbedSkipped,
                    length > _options.MaxImageBytes
                        ? $"An inline data: image ({length} bytes) exceeds the per-image limit of {_options.MaxImageBytes} bytes."
                        : $"An inline data: image ({length} bytes) would exceed the total image limit of {_options.MaxTotalImageBytes} bytes."));
                return;
            }
            _embeddedCount++;
            _embeddedBytes += length;
            sb.Append("<img src=\"data:").Append(type).Append(";base64,").Append(payload).Append("\" alt=\"").Append(Enc(alt)).Append('"').Append(Size(width, height, preserveSourceAspect)).Append('>');
        }

        private static string Size(string? width, string? height, bool preserveSourceAspect = false)
        {
            var attributes = (width is null ? string.Empty : " width=\"" + width + "\"") +
                (height is null ? string.Empty : " height=\"" + height + "\"");
            // HTML width/height attributes alone still defer to the bitmap's intrinsic aspect
            // once CSS height:auto is applied. Source drawing extents may intentionally differ.
            if (preserveSourceAspect &&
                double.TryParse(width, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var w) && w is > 0 and <= 8192 &&
                double.TryParse(height, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var h) && h is > 0 and <= 8192)
                attributes += " class=\"source-sized-image\" style=\"aspect-ratio:" +
                    w.ToString("0.##", CultureInfo.InvariantCulture) + "/" + h.ToString("0.##", CultureInfo.InvariantCulture) + "\"";
            return attributes;
        }

        private void Placeholder(StringBuilder sb, string alt, string? id)
        {
            sb.Append("<span class=\"image-placeholder\"");
            if (id is not null) sb.Append(" id=\"").Append(id).Append('"');
            var label = _ja ? "図" : "Image";
            sb.Append(">[").Append(label);
            if (!string.IsNullOrWhiteSpace(alt)) sb.Append(_ja ? ": " : ": ").Append(Enc(alt.Trim()));
            sb.Append("]</span>");
        }

        private static bool IsLanguageTag(string v) => v.Length is >= 1 and <= 35 && v.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

        private static bool IsShortNumber(string v) => v.Length is >= 1 and <= 5 && v.All(char.IsAsciiDigit);

        private static bool HasScheme(string v)
        {
            if (v.Length == 0 || !char.IsAsciiLetter(v[0])) return false;
            for (var k = 1; k < v.Length; k++)
            {
                if (v[k] == ':') return true;
                if (!(char.IsAsciiLetterOrDigit(v[k]) || v[k] is '+' or '.' or '-')) return false;
            }
            return false;
        }

        private static string Truncate(string value) => value.Length <= 120 ? value : value[..120] + "...";

        private static bool IsSafeDataImage(string uri)
        {
            string[] prefixes = ["data:image/png;base64,", "data:image/jpeg;base64,", "data:image/gif;base64,", "data:image/webp;base64,"];
            var prefix = prefixes.FirstOrDefault(p => uri.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (prefix is null) return false;
            for (var k = prefix.Length; k < uri.Length; k++)
            {
                var c = uri[k];
                if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')) return false;
            }
            return uri.Length > prefix.Length;
        }

        private bool TryFindAsset(string reference, out string key, out ReadableHtmlAsset asset)
        {
            key = string.Empty;
            asset = null!;
            if (_assets.Count == 0 || reference.Length == 0) return false;
            var candidates = new List<string> { reference };
            string decoded;
            try { decoded = Uri.UnescapeDataString(reference); } catch (UriFormatException) { decoded = reference; }
            if (decoded != reference) candidates.Add(decoded);
            if (reference.StartsWith("./", StringComparison.Ordinal)) candidates.Add(reference[2..]);
            if (decoded.StartsWith("./", StringComparison.Ordinal)) candidates.Add(decoded[2..]);
            foreach (var candidate in candidates)
                if (_assets.TryGetValue(candidate, out var found) && found is not null) { key = candidate; asset = found; return true; }
            return false;
        }

        private string AssetId(string key)
        {
            if (_assetIds.TryGetValue(key, out var existing)) return existing;
            var name = new StringBuilder();
            foreach (var c in FileNameOf(key))
                name.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
            var baseId = "asset-" + (name.Length == 0 ? "image" : name.ToString());
            var id = ReserveId(baseId);
            _assetIds[key] = id;
            return id;
        }

        private static string FileNameOf(string key)
        {
            var cut = key.LastIndexOf('/');
            return cut >= 0 ? key[(cut + 1)..] : key;
        }

        private static bool SignatureMatches(string type, ReadOnlySpan<byte> b)
        {
            switch (type)
            {
                case "image/png": return b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
                case "image/jpeg": return b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;
                case "image/gif": return b.Length >= 6 && b[..6].SequenceEqual("GIF87a"u8) || b.Length >= 6 && b[..6].SequenceEqual("GIF89a"u8);
                case "image/webp": return b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WEBP"u8);
                case "image/bmp": return b.Length >= 26 && b[0] == (byte)'B' && b[1] == (byte)'M';
                case "image/svg+xml": return LooksLikeSvg(b);
                default: return false;
            }
        }

        private static bool LooksLikeSvg(ReadOnlySpan<byte> bytes)
        {
            var head = bytes.Length > 8192 ? bytes[..8192] : bytes;
            string text;
            try { text = new UTF8Encoding(false, true).GetString(head); }
            catch (ArgumentException) { text = Encoding.UTF8.GetString(head); }
            var k = 0;
            if (text.Length > 0 && text[0] == '﻿') k = 1;
            while (true)
            {
                while (k < text.Length && char.IsWhiteSpace(text[k])) k++;
                if (string.CompareOrdinal(text, k, "<?", 0, 2) == 0)
                {
                    var end = text.IndexOf("?>", k + 2, StringComparison.Ordinal);
                    if (end < 0) return false;
                    k = end + 2;
                }
                else if (string.CompareOrdinal(text, k, "<!--", 0, 4) == 0)
                {
                    var end = text.IndexOf("-->", k + 4, StringComparison.Ordinal);
                    if (end < 0) return false;
                    k = end + 3;
                }
                else break;
            }
            if (string.Compare(text, k, "<svg", 0, 4, StringComparison.OrdinalIgnoreCase) != 0) return false;
            return k + 4 < text.Length && (char.IsWhiteSpace(text[k + 4]) || text[k + 4] is '>' or '/');
        }

        private static string Enc(string value) => WebUtility.HtmlEncode(value);

        // Source text colors are kept, but a color that would be hard to read on the page background of
        // a theme (contrast below 3:1 against the light or the dark background) gets a class that the
        // stylesheet uses to fall back to the body color in that theme only. Dark gray body text from
        // Word would otherwise all but disappear in dark mode, and white text from a shaded cell in
        // light mode. The alpha byte of an eight-digit color is ignored.
        private const double MinimumTextContrast = 3.0;

        internal static string? LowContrastClass(string hex)
        {
            if (hex.Length < 6) return null;
            var luminance = RelativeLuminance(
                int.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            if (ContrastRatio(luminance, LightBackgroundLuminance) < MinimumTextContrast) return "fg-light";
            if (ContrastRatio(luminance, DarkBackgroundLuminance) < MinimumTextContrast) return "fg-dark";
            return null;
        }

        private static readonly double LightBackgroundLuminance = RelativeLuminance(0xff, 0xff, 0xff);
        private static readonly double DarkBackgroundLuminance = RelativeLuminance(0x0d, 0x11, 0x17);

        private static double RelativeLuminance(int r, int g, int b)
        {
            static double Channel(int value)
            {
                var c = value / 255.0;
                return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
        }

        private static double ContrastRatio(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);

        // ------------------------------------------------------------------ style

        private const string Css =
            ":root{--bg:#fff;--fg:#1f2328;--mut:#59636e;--bd:#d1d9e0;--zb:#f6f8fa;--ac:#0969da;--cd:#eff1f3}" +
            "@media(prefers-color-scheme:dark){:root{--bg:#0d1117;--fg:#e6edf3;--mut:#9198a1;--bd:#3d444d;--zb:#151b23;--ac:#4493f8;--cd:#262c36}}" +
            "*{box-sizing:border-box}" +
            // Document fonts rather than UI fonts: "Yu Gothic UI" (what system-ui gives on Japanese Windows)
            // narrows punctuation such as "・" and "（", which reads as cramped in running text.
            "body{margin:0;background:var(--bg);color:var(--fg);font:16px/1.75 -apple-system,BlinkMacSystemFont,\"Segoe UI\",\"Hiragino Sans\",\"Hiragino Kaku Gothic ProN\",\"Noto Sans JP\",\"Noto Sans CJK JP\",Meiryo,\"Yu Gothic\",sans-serif;overflow-wrap:break-word}" +
            "main{max-width:960px;margin:0 auto;padding:24px 16px 64px}" +
            "h1,h2,h3,h4,h5,h6{line-height:1.35;margin:1.8em 0 .6em}h1{font-size:1.8em}h2{font-size:1.45em;border-bottom:1px solid var(--bd);padding-bottom:.2em}h3{font-size:1.2em}" +
            "p,ul,ol,blockquote,pre,figure,details{margin:0 0 1em}ul,ol{padding-left:1.8em}li>ul,li>ol{margin:.2em 0}" +
            "a{color:var(--ac)}img{max-width:100%;height:auto}" +
            "code{font:.9em ui-monospace,SFMono-Regular,Consolas,\"Noto Sans Mono CJK JP\",monospace;background:var(--cd);padding:.1em .35em;border-radius:4px}" +
            "pre{background:var(--cd);padding:12px 14px;border-radius:6px;overflow:auto;line-height:1.5}pre code{background:none;padding:0}" +
            "blockquote{margin-left:0;padding:.1em 1em;border-left:4px solid var(--bd);color:var(--mut)}" +
            "hr{border:0;border-top:1px solid var(--bd);margin:2em 0}" +
            ".table-scroll{overflow:auto;max-height:80vh;margin:0 0 1em;border:1px solid var(--bd);border-radius:6px}" +
            // The minimum cell width makes a many-column table scroll sideways on a narrow screen instead of
            // squeezing Japanese text into one character per line.
            "table{border-collapse:collapse;width:100%;font-size:.95em}th,td{border:1px solid var(--bd);padding:6px 10px;vertical-align:top;text-align:left;min-width:4.5em}" +
            "th{background:var(--zb);position:sticky;top:0}tbody tr:nth-child(even){background:var(--zb)}.align-center{text-align:center}.align-right{text-align:right}" +
            "figure{text-align:center}figure svg{max-width:100%;height:auto}figure.diagram{overflow:auto}figure.diagram svg{max-width:none}" +
            "details{border:1px solid var(--bd);border-radius:6px;padding:.4em .9em}summary{cursor:pointer;font-weight:600}details[open]>summary{margin-bottom:.6em}" +
            ".toc{border:1px solid var(--bd);border-radius:6px;padding:.6em 1em;margin:1em 0;background:var(--zb)}.toc-title{font-weight:700;margin:0 0 .3em}" +
            ".toc ul{margin:0;padding-left:1.2em;list-style:none}.toc>ul,.toc details>ul{padding-left:0}" +
            ".toc details{border:0;padding:0;margin:0}.toc details>summary.toc-title{margin:0}.toc details[open]>summary.toc-title{margin:0 0 .3em}" +
            "@media(prefers-color-scheme:light){.fg-light{color:var(--fg)!important}}@media(prefers-color-scheme:dark){.fg-dark{color:var(--fg)!important}}" +
            ".image-placeholder{color:var(--mut);border:1px dashed var(--bd);border-radius:4px;padding:0 .4em}.empty{color:var(--mut)}" +
            "@media print{:root{--bg:#fff;--fg:#000;--mut:#444;--bd:#bbb;--zb:#f2f2f2;--cd:#f2f2f2}main{max-width:none;padding:0}" +
            ".table-scroll{max-height:none;overflow:visible}th{position:static}thead{display:table-header-group}tr,figure,img,pre{break-inside:avoid}a{color:inherit}.fg-light{color:var(--fg)!important}figure.diagram svg{max-width:100%}}";
    }
}
