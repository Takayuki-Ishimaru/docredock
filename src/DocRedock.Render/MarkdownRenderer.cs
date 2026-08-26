using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocRedock.Core.Documents;
using DocRedock.Markdown;

namespace DocRedock.Render;

public enum RenderFormat { Docx, Pptx, Xlsx, Pdf, Html }
public sealed record RenderOptions(
    string? Title = null,
    string? TemplatePath = null,
    string? FontPath = null,
    string MermaidExecutablePath = "mmdc",
    string MermaidBackgroundColor = "white",
    TimeSpan? MermaidTimeout = null,
    string? SourceDirectory = null);
public sealed record RenderResult(string OutputPath, RenderFormat Format, string FidelityLevel, bool IsRestore, IReadOnlyList<string> Warnings)
{
    public RenderReport Report => new("render", Format, FidelityLevel, IsRestore, Warnings);
}
public sealed record RenderReport(string Operation, RenderFormat Format, string FidelityLevel, bool IsRestore, IReadOnlyList<string> Warnings);

/// <summary>Generates new documents from generic Markdown. This path is intentionally separate from Restore.</summary>
public sealed class MarkdownRenderer
{
    private const int MaxMermaidDiagrams = 32;
    private static readonly Regex HtmlInlineToken = new(
        @"(?<safeTag><br\s*/?>|</?(?:u|mark|summary|details)>|<details\s+class=""(?:speaker-notes|ocr-extraction)"">|<span\s+style=""color:#[0-9A-Fa-f]{6}"">|</span>)|!\[(?<imageAlt>[^\]]*)\]\((?<imageUrl>[^)\s]+)(?:\s+""[^""]*"")?\)|\[(?<linkText>[^\]]+)\]\((?<linkUrl>[^)\s]+)\)|`(?<code>[^`\r\n]+)`|~~(?<strike>.+?)~~|\*\*(?<strong>.+?)\*\*|\*(?<em>[^*]+)\*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly IMermaidRenderer mermaidRenderer;

    public MarkdownRenderer(IMermaidRenderer? mermaidRenderer = null) =>
        this.mermaidRenderer = mermaidRenderer ?? new MermaidCliRenderer();

    public Task<RenderResult> RenderAsync(string markdown, RenderFormat format, string outputPath, RenderOptions? options = null, CancellationToken cancellationToken = default)
    {
        var clean = DocRedockProjectionCleaner.Clean(markdown);
        return RenderDocumentAsync(MarkdownAstParser.Parse(clean), format, outputPath, options, cancellationToken,
            DocRedockProjectionCleaner.IsDocRedockProjection(markdown), markdown.Any(c => c > 127));
    }

    public async Task<RenderResult> RenderDocumentAsync(MarkdownDocument document, RenderFormat format, string outputPath, RenderOptions? options = null, CancellationToken cancellationToken = default,
        bool sanitizedDocRedock = false, bool containsNonAscii = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        containsNonAscii |= ContainsNonAscii(document);
        var fullPath = Path.GetFullPath(outputPath);
        if (File.Exists(fullPath)) throw new IOException("Render output already exists.");
        string? templatePath = null;
        if (options?.TemplatePath is not null)
        {
            if (format is RenderFormat.Pdf or RenderFormat.Html)
                throw new NotSupportedException($"{format.ToString().ToUpperInvariant()} templates are not supported by the built-in renderer.");
            templatePath = ValidateTemplate(options.TemplatePath, format);
        }
        document = await MaterializeMermaidAsync(document, options, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var generatedPath = tempPath + ".generated";
        try
        {
            if (templatePath is not null)
            {
                WriteGenerated(document, format, generatedPath, options, sanitizedDocRedock);
                File.Copy(templatePath, tempPath);
                OfficeTemplatePackageMerger.ApplyGeneratedContent(generatedPath, tempPath, format);
            }
            else WriteGenerated(document, format, tempPath, options, sanitizedDocRedock);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, fullPath);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            if (File.Exists(generatedPath)) File.Delete(generatedPath);
        }
        var fidelity = options?.TemplatePath is null ? "F3" : "F2";
        var warnings = new List<string>
        {
            format == RenderFormat.Html
                ? "HTML preview is a review artifact; it is not Restore."
                : options?.TemplatePath is null
                    ? "Render creates a new document; it is not an Office/PDF Restore."
                    : "Template render preserved template package parts and merged generated content dependencies; it is not Restore."
        };
        if (sanitizedDocRedock) warnings.Add("DRMD control metadata was removed before rendering.");
        var diagramCount = document.Blocks.OfType<MarkdownDiagram>().Count();
        if (diagramCount > 0) warnings.Add($"Rendered {diagramCount} Mermaid diagram(s) with a local Mermaid CLI.");
        return new RenderResult(fullPath, format, fidelity, false, warnings);
    }

    private async Task<MarkdownDocument> MaterializeMermaidAsync(MarkdownDocument document, RenderOptions? options, CancellationToken cancellationToken)
    {
        var mermaidBlocks = document.Blocks.OfType<MarkdownCodeBlock>()
            .Where(block => block.Language.Equals("mermaid", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (mermaidBlocks.Length == 0) return document;
        if (mermaidBlocks.Length > MaxMermaidDiagrams)
            throw new InvalidDataException($"Markdown contains {mermaidBlocks.Length} Mermaid diagrams; the limit is {MaxMermaidDiagrams}.");

        var request = new MermaidRenderRequest(
            options?.MermaidExecutablePath ?? "mmdc",
            options?.MermaidBackgroundColor ?? "white",
            Timeout: options?.MermaidTimeout);
        var blocks = new List<MarkdownBlock>(document.Blocks.Count);
        foreach (var block in document.Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block is not MarkdownCodeBlock code || !code.Language.Equals("mermaid", StringComparison.OrdinalIgnoreCase))
            {
                blocks.Add(block);
                continue;
            }
            var bytes = await mermaidRenderer.RenderPngAsync(code.Text, request, cancellationToken).ConfigureAwait(false);
            blocks.Add(new MarkdownDiagram(code.Text, MermaidAltText(code.Text), PngRasterImage.Decode(bytes)));
        }
        return new MarkdownDocument(blocks);
    }

    private static string MermaidAltText(string source)
    {
        foreach (var key in new[] { "accDescr", "accTitle" })
        {
            var match = Regex.Match(source, $@"(?im)^\s*{key}\s*:\s*(?<value>.+?)\s*$", RegexOptions.CultureInvariant);
            if (match.Success) return match.Groups["value"].Value.Trim();
        }
        return "Mermaid diagram";
    }

    private static bool ContainsNonAscii(MarkdownDocument document) => document.Blocks.Any(block => block switch
    {
        MarkdownHeading heading => heading.Text.Any(character => character > 127),
        MarkdownParagraph paragraph => paragraph.Text.Any(character => character > 127),
        MarkdownList list => list.Items.Any(item => item.Any(character => character > 127)),
        MarkdownCodeBlock code => code.Text.Any(character => character > 127),
        MarkdownDiagram diagram => diagram.AltText.Any(character => character > 127),
        MarkdownTable table => table.Headers.Concat(table.Rows.SelectMany(row => row)).Any(cell => cell.Any(character => character > 127)),
        _ => false,
    });

    private static void WriteGenerated(MarkdownDocument document, RenderFormat format, string path, RenderOptions? options, bool sanitizedDocRedock)
    {
        switch (format)
        {
            case RenderFormat.Docx: WriteDocx(document, path, options); break;
            case RenderFormat.Pptx: WritePptx(document, path, options); break;
            case RenderFormat.Xlsx: WriteXlsx(document, path, options); break;
            case RenderFormat.Pdf: WritePdf(document, path, options); break;
            case RenderFormat.Html: WriteHtml(document, path, options, sanitizedDocRedock); break;
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static void WriteHtml(MarkdownDocument document, string path, RenderOptions? options, bool sanitizedDocRedock)
    {
        var sourceTitle = options?.Title ?? document.Blocks.OfType<MarkdownHeading>().FirstOrDefault()?.Text ?? "DocRedock Preview";
        var title = Regex.Replace(Regex.Replace(sourceTitle, @"<[^>]+>", " ", RegexOptions.CultureInvariant), @"[*_`~]+", string.Empty, RegexOptions.CultureInvariant);
        title = Regex.Replace(title, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        var language = ContainsNonAscii(document) ? "ja" : "en";
        var output = new StringBuilder(16_384);
        output.Append("<!doctype html><html lang=\"").Append(language).Append("\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<title>").Append(Html(title)).AppendLine("</title>");
        output.AppendLine("""
            <style>
            :root{color-scheme:light;--ink:#172033;--muted:#5d687b;--line:#dfe4ec;--paper:#fff;--wash:#f5f7fb;--accent:#3157d5;--accent-wash:#edf2ff}
            *{box-sizing:border-box}body{margin:0;background:var(--wash);color:var(--ink);font-family:-apple-system,BlinkMacSystemFont,"Segoe UI","Noto Sans JP",sans-serif;line-height:1.7}
            .shell{width:min(1120px,calc(100% - 32px));margin:28px auto 64px;background:var(--paper);border:1px solid var(--line);border-radius:16px;box-shadow:0 16px 50px rgba(34,49,82,.08);overflow:hidden}
            .preview-bar{display:flex;gap:12px;align-items:center;padding:14px 24px;background:var(--accent-wash);border-bottom:1px solid #cfd9ff;color:#233d91;font-size:.9rem}
            .preview-badge{padding:3px 9px;border-radius:999px;background:var(--accent);color:#fff;font-weight:700;letter-spacing:.03em}.preview-note{color:#44598f}
            main{padding:clamp(24px,5vw,64px)}h1,h2,h3,h4,h5,h6{line-height:1.25;margin:1.7em 0 .55em;letter-spacing:-.02em}h1{font-size:clamp(2rem,5vw,3.4rem);margin-top:0}h2{font-size:1.65rem;border-bottom:1px solid var(--line);padding-bottom:.35em}h3{font-size:1.25rem}
            p,ul,ol{margin:.75em 0 1.15em}li+li{margin-top:.3em}a{color:var(--accent)}code{font-family:"SFMono-Regular",Consolas,monospace;background:#eef1f6;border-radius:4px;padding:.12em .35em;font-size:.92em}
            pre{overflow:auto;padding:18px 20px;background:#111827;color:#e5e7eb;border-radius:10px;line-height:1.5}pre code{padding:0;background:transparent;color:inherit}
            .table-scroll{overflow-x:auto;margin:1.2rem 0 2rem;border:1px solid var(--line);border-radius:10px}table{border-collapse:collapse;width:max-content;min-width:100%;font-variant-numeric:tabular-nums}th,td{padding:10px 13px;border-right:1px solid var(--line);border-bottom:1px solid var(--line);text-align:left;vertical-align:top;white-space:pre-wrap}th{position:sticky;top:0;background:#f2f5fa;font-weight:700}tr:last-child td{border-bottom:0}th:last-child,td:last-child{border-right:0}
            figure{margin:1.5rem 0;text-align:center}figure img,.inline-image{max-width:100%;height:auto;border-radius:8px}figcaption{margin-top:.5rem;color:var(--muted);font-size:.9rem}.empty{color:var(--muted);font-style:italic}
            details{margin:1.2rem 0;padding:12px 16px;border:1px solid var(--line);border-radius:9px;background:#fafbfe;color:var(--muted)}summary{cursor:pointer;color:var(--ink);font-weight:700}details[open] summary{margin-bottom:.65rem}hr{border:0;border-top:2px solid var(--line);margin:2.5rem 0}
            @media(max-width:640px){.shell{width:100%;margin:0;border:0;border-radius:0}.preview-bar{align-items:flex-start;flex-direction:column}main{padding:24px 18px}}
            @media print{body{background:#fff}.shell{width:100%;margin:0;border:0;box-shadow:none}.preview-bar{display:none}main{padding:0}.table-scroll{overflow:visible}}
            </style></head><body><div class="shell">
            """);
        if (sanitizedDocRedock)
            output.AppendLine("<div class=\"preview-bar\"><span class=\"preview-badge\">ROUNDTRIP PREVIEW</span><span class=\"preview-note\">編集用メタデータを隠し、復元対象の内容だけを表示しています。</span></div>");
        output.AppendLine("<main>");
        string Inline(string value) => HtmlInline(value, options?.SourceDirectory, path);
        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case MarkdownHeading heading:
                    var level = Math.Clamp(heading.Level, 1, 6);
                    output.Append('<').Append('h').Append(level).Append('>').Append(Inline(heading.Text))
                        .Append("</h").Append(level).AppendLine(">");
                    break;
                case MarkdownParagraph paragraph:
                    if (paragraph.Text.Trim() is "---" or "***" or "___") output.AppendLine("<hr>");
                    else output.Append("<p>").Append(Inline(paragraph.Text)).AppendLine("</p>");
                    break;
                case MarkdownList list:
                    WriteHtmlList(output, list, Inline);
                    break;
                case MarkdownCodeBlock code:
                    output.Append("<pre><code data-language=\"").Append(Html(code.Language)).Append("\">")
                        .Append(Html(code.Text)).AppendLine("</code></pre>");
                    break;
                case MarkdownDiagram diagram:
                    output.Append("<figure><img src=\"data:image/png;base64,")
                        .Append(Convert.ToBase64String(diagram.Image.PngBytes)).Append("\" alt=\"").Append(Html(diagram.AltText))
                        .Append("\"><figcaption>").Append(Html(diagram.AltText)).AppendLine("</figcaption></figure>");
                    break;
                case MarkdownTable table:
                    output.AppendLine("<div class=\"table-scroll\"><table><thead><tr>");
                    foreach (var header in table.Headers) output.Append("<th scope=\"col\">").Append(Inline(header)).AppendLine("</th>");
                    output.AppendLine("</tr></thead><tbody>");
                    foreach (var row in table.Rows)
                    {
                        output.AppendLine("<tr>");
                        for (var index = 0; index < table.Headers.Count; index++)
                        {
                            var value = index < row.Count ? row[index] : string.Empty;
                            output.Append("<td>").Append(Inline(value).Replace("\n", "<br>", StringComparison.Ordinal)).AppendLine("</td>");
                        }
                        output.AppendLine("</tr>");
                    }
                    output.AppendLine("</tbody></table></div>");
                    break;
            }
        }
        if (document.Blocks.Count == 0) output.AppendLine("<p class=\"empty\">表示できる内容がありません。</p>");
        output.AppendLine("</main></div></body></html>");
        File.WriteAllText(path, output.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteHtmlList(StringBuilder output, MarkdownList list, Func<string, string> inline)
    {
        var levels = list.Levels ?? Enumerable.Repeat(0, list.Items.Count).ToArray();
        var ordered = list.Ordered ?? Enumerable.Repeat(false, list.Items.Count).ToArray();
        var index = 0;
        while (index < list.Items.Count) WriteLevel(Math.Max(0, levels.ElementAtOrDefault(index)));

        void WriteLevel(int level)
        {
            var isOrdered = ordered.ElementAtOrDefault(index);
            var tag = isOrdered ? "ol" : "ul";
            output.Append('<').Append(tag).AppendLine(">");
            while (index < list.Items.Count)
            {
                var itemLevel = Math.Max(0, levels.ElementAtOrDefault(index));
                if (itemLevel < level || ordered.ElementAtOrDefault(index) != isOrdered && itemLevel == level) break;
                if (itemLevel > level) { WriteLevel(itemLevel); continue; }
                output.Append("<li>").Append(inline(list.Items[index]));
                index++;
                while (index < list.Items.Count && Math.Max(0, levels.ElementAtOrDefault(index)) > level)
                    WriteLevel(Math.Max(0, levels.ElementAtOrDefault(index)));
                output.AppendLine("</li>");
            }
            output.Append("</").Append(tag).AppendLine(">");
        }
    }

    private static string HtmlInline(string value, string? sourceDirectory = null, string? outputPath = null)
    {
        var output = new StringBuilder(value.Length + 32);
        var cursor = 0;
        foreach (Match match in HtmlInlineToken.Matches(value))
        {
            output.Append(HtmlText(value[cursor..match.Index]));
            if (match.Groups["safeTag"].Success) output.Append(match.Value);
            else if (match.Groups["imageUrl"].Success && IsSafeHtmlUrl(match.Groups["imageUrl"].Value, image: true))
                output.Append("<img class=\"inline-image\" loading=\"lazy\" src=\"").Append(Html(ResolveHtmlImageUrl(match.Groups["imageUrl"].Value, sourceDirectory, outputPath)))
                    .Append("\" alt=\"").Append(Html(match.Groups["imageAlt"].Value)).Append("\">");
            else if (match.Groups["linkUrl"].Success && IsSafeHtmlUrl(match.Groups["linkUrl"].Value, image: false))
                output.Append("<a href=\"").Append(Html(match.Groups["linkUrl"].Value)).Append("\">").Append(Html(match.Groups["linkText"].Value)).Append("</a>");
            else if (match.Groups["code"].Success) output.Append("<code>").Append(Html(match.Groups["code"].Value)).Append("</code>");
            else if (match.Groups["strike"].Success) output.Append("<del>").Append(Html(match.Groups["strike"].Value)).Append("</del>");
            else if (match.Groups["strong"].Success) output.Append("<strong>").Append(Html(match.Groups["strong"].Value)).Append("</strong>");
            else if (match.Groups["em"].Success) output.Append("<em>").Append(Html(match.Groups["em"].Value)).Append("</em>");
            else output.Append(Html(match.Value));
            cursor = match.Index + match.Length;
        }
        output.Append(HtmlText(value[cursor..]));
        return output.ToString();
    }

    private static string HtmlText(string value) => Html(value)
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

    private static string Html(string value) => System.Net.WebUtility.HtmlEncode(value);

    private static string ValidateTemplate(string path, RenderFormat format)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Render template was not found.", fullPath);
        if (new FileInfo(fullPath).LinkTarget is not null) throw new UnauthorizedAccessException("Symbolic-link templates are not accepted.");
        var expected = "." + format.ToString().ToLowerInvariant();
        if (!Path.GetExtension(fullPath).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Template extension must be '{expected}'.");
        using var archive = ZipFile.OpenRead(fullPath);
        if (archive.Entries.Count > 50_000)
            throw new InvalidDataException("Template failed package entry-count limits.");
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (Path.IsPathRooted(entry.FullName) || (normalized.Length >= 2 && normalized[1] == ':') || normalized.StartsWith("/", StringComparison.Ordinal) ||
                normalized.Split('/').Any(segment => segment is ".." or "."))
                throw new UnauthorizedAccessException("Template contains an unsafe package path.");
            if (entry.Length > 268_435_456 || entry.Length > 0 && (entry.CompressedLength == 0 || (double)entry.Length / entry.CompressedLength > 100))
                throw new InvalidDataException("Template failed package size limits.");
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > 1_073_741_824) throw new InvalidDataException("Template failed total expansion limits.");
            if (entry.FullName.EndsWith("/vbaProject.bin", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Macro-enabled templates are not accepted by the built-in renderer.");
            if (entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                if (entry.Length > 4_194_304) throw new InvalidDataException("Template XML part exceeds the inspection limit.");
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
                var content = reader.ReadToEnd();
                if (content.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || content.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("Template XML contains DTD or entity declarations.");
                if (entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var relationshipDocument = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
                        var hasExternalRelationship = relationshipDocument.Descendants()
                            .Where(node => node.Name.LocalName.Equals("Relationship", StringComparison.Ordinal))
                            .Any(node =>
                            {
                                var target = (string?)node.Attribute("Target") ?? string.Empty;
                                var mode = (string?)node.Attribute("TargetMode") ?? string.Empty;
                                return mode.Equals("External", StringComparison.OrdinalIgnoreCase) ||
                                    target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                    target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                                    target.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                                    target.StartsWith("\\\\", StringComparison.Ordinal);
                            });
                        if (hasExternalRelationship) throw new UnauthorizedAccessException("Template contains an external relationship.");
                    }
                    catch (UnauthorizedAccessException) { throw; }
                    catch (Exception exception) when (exception is System.Xml.XmlException or InvalidOperationException)
                    {
                        throw new InvalidDataException("Template relationship part is malformed.", exception);
                    }
                }
            }
        }
        return fullPath;
    }

    private static void WriteDocx(MarkdownDocument document, string path, RenderOptions? options)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var diagrams = document.Blocks.OfType<MarkdownDiagram>().ToArray();
        var pngContentType = diagrams.Length == 0 ? string.Empty : "<Default Extension=\"png\" ContentType=\"image/png\"/>";
        Add(archive, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" + pngContentType + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/><Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/></Types>");
        Add(archive, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        var imageRelationships = string.Concat(diagrams.Select((_, index) => $"<Relationship Id=\"rIdDocRedockMermaid{index + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/docredock-mermaid-{index + 1}.png\"/>"));
        Add(archive, "word/_rels/document.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/><Relationship Id=\"rIdNumbering\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>" + imageRelationships + "</Relationships>");
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        XNamespace pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
        var body = new XElement(w + "body");
        var diagramIndex = 0;
        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case MarkdownHeading heading: body.Add(WordParagraph(w, heading.Text, "Heading" + heading.Level)); break;
                case MarkdownParagraph paragraph: body.Add(WordParagraph(w, paragraph.Text)); break;
                case MarkdownList list: foreach (var item in list.Items) body.Add(WordParagraph(w, item, numbered: true)); break;
                case MarkdownCodeBlock code:
                    foreach (var line in code.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                        body.Add(WordParagraph(w, EscapeInlineLiteral(line), "Code"));
                    break;
                case MarkdownDiagram diagram:
                    diagramIndex++;
                    body.Add(WordDiagram(w, r, wp, a, pic, diagram, diagramIndex));
                    break;
                case MarkdownTable table:
                    body.Add(WordTable(w, table));
                    break;
            }
        }
        body.Add(new XElement(w + "sectPr", new XElement(w + "pgSz", new XAttribute(w + "w", "11906"), new XAttribute(w + "h", "16838"))));
        Add(archive, "word/document.xml", new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(w + "document",
            new XAttribute(XNamespace.Xmlns + "w", w), new XAttribute(XNamespace.Xmlns + "r", r), new XAttribute(XNamespace.Xmlns + "wp", wp),
            new XAttribute(XNamespace.Xmlns + "a", a), new XAttribute(XNamespace.Xmlns + "pic", pic), body)).ToString(SaveOptions.DisableFormatting));
        Add(archive, "word/styles.xml", WordStyles(w).ToString(SaveOptions.DisableFormatting));
        Add(archive, "word/numbering.xml", WordNumbering(w).ToString(SaveOptions.DisableFormatting));
        for (var index = 0; index < diagrams.Length; index++) Add(archive, $"word/media/docredock-mermaid-{index + 1}.png", diagrams[index].Image.PngBytes);
    }

    private static XElement WordDiagram(XNamespace w, XNamespace r, XNamespace wp, XNamespace a, XNamespace pic, MarkdownDiagram diagram, int index)
    {
        var (width, height) = FitEmu(diagram.Image.Width, diagram.Image.Height, 5_943_600, 5_943_600);
        return new XElement(w + "p",
            new XElement(w + "pPr", new XElement(w + "jc", new XAttribute(w + "val", "center"))),
            new XElement(w + "r", new XElement(w + "drawing",
                new XElement(wp + "inline",
                    new XAttribute("distT", "0"), new XAttribute("distB", "0"), new XAttribute("distL", "0"), new XAttribute("distR", "0"),
                    new XElement(wp + "extent", new XAttribute("cx", width), new XAttribute("cy", height)),
                    new XElement(wp + "effectExtent", new XAttribute("l", "0"), new XAttribute("t", "0"), new XAttribute("r", "0"), new XAttribute("b", "0")),
                    new XElement(wp + "docPr", new XAttribute("id", 1000 + index), new XAttribute("name", $"Mermaid diagram {index}"), new XAttribute("descr", diagram.AltText)),
                    new XElement(wp + "cNvGraphicFramePr", new XElement(a + "graphicFrameLocks", new XAttribute("noChangeAspect", "1"))),
                    new XElement(a + "graphic",
                        new XElement(a + "graphicData", new XAttribute("uri", "http://schemas.openxmlformats.org/drawingml/2006/picture"),
                            new XElement(pic + "pic",
                                new XElement(pic + "nvPicPr", new XElement(pic + "cNvPr", new XAttribute("id", index), new XAttribute("name", $"docredock-mermaid-{index}.png"), new XAttribute("descr", diagram.AltText)), new XElement(pic + "cNvPicPr")),
                                new XElement(pic + "blipFill", new XElement(a + "blip", new XAttribute(r + "embed", $"rIdDocRedockMermaid{index}")), new XElement(a + "stretch", new XElement(a + "fillRect"))),
                                new XElement(pic + "spPr", new XElement(a + "xfrm", new XElement(a + "off", new XAttribute("x", "0"), new XAttribute("y", "0")), new XElement(a + "ext", new XAttribute("cx", width), new XAttribute("cy", height))), new XElement(a + "prstGeom", new XAttribute("prst", "rect"), new XElement(a + "avLst"))))))))));
    }

    private static (long Width, long Height) FitEmu(int pixelWidth, int pixelHeight, long maxWidth, long maxHeight)
    {
        var scale = Math.Min((double)maxWidth / pixelWidth, (double)maxHeight / pixelHeight);
        return (Math.Max(1, (long)Math.Round(pixelWidth * scale)), Math.Max(1, (long)Math.Round(pixelHeight * scale)));
    }

    private static XElement WordParagraph(XNamespace w, string inlineMarkdown, string? styleId = null, bool numbered = false)
    {
        var paragraph = new XElement(w + "p");
        if (styleId is not null || numbered)
        {
            var properties = new XElement(w + "pPr");
            if (styleId is not null) properties.Add(new XElement(w + "pStyle", new XAttribute(w + "val", styleId)));
            if (numbered) properties.Add(new XElement(w + "numPr", new XElement(w + "ilvl", new XAttribute(w + "val", "0")), new XElement(w + "numId", new XAttribute(w + "val", "1"))));
            paragraph.Add(properties);
        }
        foreach (var run in DocRedockInlineMarkdown.Parse(inlineMarkdown).Runs) paragraph.Add(WordRun(w, run));
        if (!paragraph.Elements(w + "r").Any()) paragraph.Add(new XElement(w + "r", new XElement(w + "t", string.Empty)));
        return paragraph;
    }

    private static XElement WordRun(XNamespace w, TextRun run)
    {
        var element = new XElement(w + "r");
        var properties = new XElement(w + "rPr",
            new XElement(w + "rFonts", new XAttribute(w + "ascii", run.Code ? "Consolas" : "Arial"),
                new XAttribute(w + "hAnsi", run.Code ? "Consolas" : "Arial"), new XAttribute(w + "eastAsia", "Noto Sans JP"), new XAttribute(w + "cs", "Arial")),
            new XElement(w + "lang", new XAttribute(w + "val", "en-US"), new XAttribute(w + "eastAsia", "ja-JP")));
        if (run.Bold) properties.Add(new XElement(w + "b"));
        if (run.Italic) properties.Add(new XElement(w + "i"));
        if (run.Underline) properties.Add(new XElement(w + "u", new XAttribute(w + "val", "single")));
        if (run.Strike) properties.Add(new XElement(w + "strike"));
        element.Add(properties);
        if (run.Kind == TextRunKind.LineBreak) element.Add(new XElement(w + "br"));
        else if (run.Kind == TextRunKind.Tab) element.Add(new XElement(w + "tab"));
        else element.Add(new XElement(w + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), run.Text));
        return element;
    }

    private static XElement WordTable(XNamespace w, MarkdownTable table)
    {
        var rows = new[] { table.Headers }.Concat(table.Rows).ToArray();
        var width = Math.Max(1, rows.Max(row => row.Count));
        return new XElement(w + "tbl",
            new XElement(w + "tblPr",
                new XElement(w + "tblW", new XAttribute(w + "w", "0"), new XAttribute(w + "type", "auto")),
                new XElement(w + "tblBorders",
                    new[] { "top", "left", "bottom", "right", "insideH", "insideV" }
                        .Select(name => new XElement(w + name, new XAttribute(w + "val", "single"), new XAttribute(w + "sz", "4"), new XAttribute(w + "color", "B7C9E2"))))),
            new XElement(w + "tblGrid", Enumerable.Range(0, width).Select(_ => new XElement(w + "gridCol", new XAttribute(w + "w", (9000 / width).ToString(System.Globalization.CultureInfo.InvariantCulture))))),
            rows.Select((row, rowIndex) => new XElement(w + "tr",
                Enumerable.Range(0, width).Select(column => new XElement(w + "tc",
                    new XElement(w + "tcPr", new XElement(w + "tcW", new XAttribute(w + "w", (9000 / width).ToString(System.Globalization.CultureInfo.InvariantCulture)), new XAttribute(w + "type", "dxa")),
                        rowIndex == 0 ? new XElement(w + "shd", new XAttribute(w + "fill", "D9EAF7")) : null),
                    WordParagraph(w, column < row.Count ? row[column] : string.Empty))))));
    }

    private static XDocument WordStyles(XNamespace w)
    {
        var styles = new XElement(w + "styles",
            new XElement(w + "style", new XAttribute(w + "type", "paragraph"), new XAttribute(w + "default", "1"), new XAttribute(w + "styleId", "Normal"), new XElement(w + "name", new XAttribute(w + "val", "Normal")), new XElement(w + "rPr", new XElement(w + "rFonts", new XAttribute(w + "ascii", "Arial"), new XAttribute(w + "hAnsi", "Arial"), new XAttribute(w + "eastAsia", "Noto Sans JP")), new XElement(w + "lang", new XAttribute(w + "val", "en-US"), new XAttribute(w + "eastAsia", "ja-JP")), new XElement(w + "sz", new XAttribute(w + "val", "22")))));
        for (var level = 1; level <= 6; level++)
            styles.Add(new XElement(w + "style", new XAttribute(w + "type", "paragraph"), new XAttribute(w + "styleId", "Heading" + level),
                new XElement(w + "name", new XAttribute(w + "val", "heading " + level)), new XElement(w + "basedOn", new XAttribute(w + "val", "Normal")),
                new XElement(w + "pPr", new XElement(w + "keepNext"), new XElement(w + "spacing", new XAttribute(w + "before", "240"), new XAttribute(w + "after", "100"))),
                new XElement(w + "rPr", new XElement(w + "b"), new XElement(w + "sz", new XAttribute(w + "val", Math.Max(24, 38 - level * 2).ToString(System.Globalization.CultureInfo.InvariantCulture))))));
        styles.Add(new XElement(w + "style", new XAttribute(w + "type", "paragraph"), new XAttribute(w + "styleId", "Code"), new XElement(w + "name", new XAttribute(w + "val", "Code")), new XElement(w + "basedOn", new XAttribute(w + "val", "Normal")), new XElement(w + "rPr", new XElement(w + "rFonts", new XAttribute(w + "ascii", "Consolas"), new XAttribute(w + "hAnsi", "Consolas")), new XElement(w + "sz", new XAttribute(w + "val", "20")))));
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), styles);
    }

    private static XDocument WordNumbering(XNamespace w) => new(new XDeclaration("1.0", "UTF-8", "yes"),
        new XElement(w + "numbering",
            new XElement(w + "abstractNum", new XAttribute(w + "abstractNumId", "0"),
                new XElement(w + "lvl", new XAttribute(w + "ilvl", "0"), new XElement(w + "numFmt", new XAttribute(w + "val", "bullet")),
                    new XElement(w + "lvlText", new XAttribute(w + "val", "•")), new XElement(w + "pPr", new XElement(w + "tabs", new XElement(w + "tab", new XAttribute(w + "val", "num"), new XAttribute(w + "pos", "720"))), new XElement(w + "ind", new XAttribute(w + "left", "720"), new XAttribute(w + "hanging", "360"))))),
            new XElement(w + "num", new XAttribute(w + "numId", "1"), new XElement(w + "abstractNumId", new XAttribute(w + "val", "0")))));

    private static void WritePptx(MarkdownDocument document, string path, RenderOptions? options)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var diagrams = document.Blocks.OfType<MarkdownDiagram>().ToArray();
        var pngContentType = diagrams.Length == 0 ? string.Empty : "<Default Extension=\"png\" ContentType=\"image/png\"/>";
        Add(archive, "[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" + pngContentType + "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/><Override PartName=\"/ppt/slides/slide1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/><Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/><Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/><Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/><Override PartName=\"/ppt/presProps.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presProps+xml\"/><Override PartName=\"/ppt/tableStyles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.tableStyles+xml\"/></Types>");
        Add(archive, "_rels/.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>");
        Add(archive, "ppt/_rels/presentation.xml.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"theme/theme1.xml\"/><Relationship Id=\"rId4\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/presProps\" Target=\"presProps.xml\"/><Relationship Id=\"rId5\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/tableStyles\" Target=\"tableStyles.xml\"/></Relationships>");
        var imageRelationships = string.Concat(diagrams.Select((_, index) => $"<Relationship Id=\"rIdDocRedockMermaid{index + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"../media/docredock-mermaid-{index + 1}.png\"/>"));
        Add(archive, "ppt/slides/_rels/slide1.xml.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" + imageRelationships + "</Relationships>");
        Add(archive, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>");
        Add(archive, "ppt/slideMasters/_rels/slideMaster1.xml.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"../theme/theme1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/></Relationships>");
        XNamespace p = "http://schemas.openxmlformats.org/presentationml/2006/main"; XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main"; XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        Add(archive, "ppt/presentation.xml", new XDocument(new XElement(p + "presentation", new XAttribute(XNamespace.Xmlns + "p", p), new XAttribute(XNamespace.Xmlns + "a", a), new XAttribute(XNamespace.Xmlns + "r", r), new XElement(p + "sldMasterIdLst", new XElement(p + "sldMasterId", new XAttribute("id", "2147483648"), new XAttribute(r + "id", "rId2"))), new XElement(p + "sldIdLst", new XElement(p + "sldId", new XAttribute("id", "256"), new XAttribute(r + "id", "rId1"))), new XElement(p + "sldSz", new XAttribute("cx", "12192000"), new XAttribute("cy", "6858000")), new XElement(p + "notesSz", new XAttribute("cx", "6858000"), new XAttribute("cy", "9144000")))).ToString(SaveOptions.DisableFormatting));
        Add(archive, "ppt/theme/theme1.xml", PptxTheme(a).ToString(SaveOptions.DisableFormatting));
        Add(archive, "ppt/slideMasters/slideMaster1.xml", PptxMaster(p, a, r).ToString(SaveOptions.DisableFormatting));
        Add(archive, "ppt/slideLayouts/slideLayout1.xml", PptxLayout(p, a).ToString(SaveOptions.DisableFormatting));
        Add(archive, "ppt/presProps.xml", new XDocument(new XElement(p + "presentationPr", new XAttribute(XNamespace.Xmlns + "p", p))).ToString(SaveOptions.DisableFormatting));
        Add(archive, "ppt/tableStyles.xml", new XDocument(new XElement(a + "tblStyleLst", new XAttribute(XNamespace.Xmlns + "a", a), new XAttribute("def", "{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}"))).ToString(SaveOptions.DisableFormatting));
        var firstHeading = document.Blocks.OfType<MarkdownHeading>().FirstOrDefault();
        var title = PlainInlineText(firstHeading?.Text ?? options?.Title ?? "DocRedock Document");
        var bodyParagraphs = document.Blocks.Where(block => !ReferenceEquals(block, firstHeading)).SelectMany(block => block switch
        {
            MarkdownHeading heading => new[] { new SlideParagraph(PlainInlineText(heading.Text), false, true) },
            MarkdownParagraph paragraph => new[] { new SlideParagraph(PlainInlineText(paragraph.Text), false, false) },
            MarkdownList list => list.Items.Select(item => new SlideParagraph(PlainInlineText(item), true, false)),
            MarkdownCodeBlock code => code.Text.Split('\n').Select(line => new SlideParagraph(line, false, false)),
            MarkdownTable table => table.Rows.Select(row => new SlideParagraph(string.Join(" | ", row.Select(PlainInlineText)), false, false)).Prepend(new SlideParagraph(string.Join(" | ", table.Headers.Select(PlainInlineText)), false, true)),
            _ => Array.Empty<SlideParagraph>()
        }).ToArray();
        var hasBodyText = bodyParagraphs.Length > 0;
        var bodyHeight = diagrams.Length == 0 ? 4_500_000 : hasBodyText ? 1_250_000 : 200_000;
        var diagramTop = hasBodyText ? 3_000_000L : 1_500_000L;
        var diagramBottom = 6_450_000L;
        var diagramSlotHeight = diagrams.Length == 0 ? 0 : Math.Max(1, (diagramBottom - diagramTop) / diagrams.Length);
        var tree = new XElement(p + "spTree",
            new XElement(p + "nvGrpSpPr", new XElement(p + "cNvPr", new XAttribute("id", "1"), new XAttribute("name", string.Empty)), new XElement(p + "cNvGrpSpPr"), new XElement(p + "nvPr")),
            new XElement(p + "grpSpPr", new XElement(a + "xfrm", new XElement(a + "off", new XAttribute("x", "0"), new XAttribute("y", "0")), new XElement(a + "ext", new XAttribute("cx", "0"), new XAttribute("cy", "0")), new XElement(a + "chOff", new XAttribute("x", "0"), new XAttribute("y", "0")), new XElement(a + "chExt", new XAttribute("cx", "0"), new XAttribute("cy", "0")))),
            SlideShape(p, a, "2", "Title", "title", 650000, 300000, 10900000, 1100000, [new SlideParagraph(title, false, true)]),
            SlideShape(p, a, "3", "Body", "body", 800000, 1600000, 10600000, bodyHeight,
                bodyParagraphs.Length == 0 ? [new SlideParagraph(string.Empty, false, false)] : bodyParagraphs),
            diagrams.Select((diagram, index) => SlidePicture(p, a, r, diagram, index + 1, diagramTop + diagramSlotHeight * index, diagramSlotHeight)));
        Add(archive, "ppt/slides/slide1.xml", new XDocument(new XElement(p + "sld", new XAttribute(XNamespace.Xmlns + "p", p), new XAttribute(XNamespace.Xmlns + "a", a), new XAttribute(XNamespace.Xmlns + "r", r), new XElement(p + "cSld", tree), new XElement(p + "clrMapOvr", new XElement(a + "masterClrMapping")))).ToString(SaveOptions.DisableFormatting));
        for (var index = 0; index < diagrams.Length; index++) Add(archive, $"ppt/media/docredock-mermaid-{index + 1}.png", diagrams[index].Image.PngBytes);
    }

    private static XElement SlideShape(XNamespace p, XNamespace a, string id, string name, string role,
        long x, long y, long width, long height, IReadOnlyList<SlideParagraph> paragraphs) =>
        new(p + "sp",
            new XElement(p + "nvSpPr", new XElement(p + "cNvPr", new XAttribute("id", id), new XAttribute("name", name)), new XElement(p + "cNvSpPr"), new XElement(p + "nvPr", new XElement(p + "ph", new XAttribute("type", role)))),
            new XElement(p + "spPr", new XElement(a + "xfrm", new XElement(a + "off", new XAttribute("x", x), new XAttribute("y", y)), new XElement(a + "ext", new XAttribute("cx", width), new XAttribute("cy", height))), new XElement(a + "prstGeom", new XAttribute("prst", "rect"), new XElement(a + "avLst")), new XElement(a + "noFill"), new XElement(a + "ln", new XElement(a + "noFill"))),
            new XElement(p + "txBody", new XElement(a + "bodyPr", new XAttribute("wrap", "square")), new XElement(a + "lstStyle"),
                paragraphs.Select(paragraph => new XElement(a + "p",
                    new XElement(a + "pPr", paragraph.Bullet ? new XElement(a + "buChar", new XAttribute("char", "•")) : null),
                    new XElement(a + "r", new XElement(a + "rPr", new XAttribute("lang", "ja-JP"), new XAttribute("sz", paragraph.Emphasis ? "2600" : "2000"), paragraph.Emphasis ? new XAttribute("b", "1") : null, new XElement(a + "latin", new XAttribute("typeface", "Arial")), new XElement(a + "ea", new XAttribute("typeface", "Noto Sans JP"))), new XElement(a + "t", paragraph.Text)),
                    new XElement(a + "endParaRPr", new XAttribute("lang", "ja-JP"), new XAttribute("sz", "2000"), new XElement(a + "latin", new XAttribute("typeface", "Arial")), new XElement(a + "ea", new XAttribute("typeface", "Noto Sans JP")))))));

    private static XElement SlidePicture(XNamespace p, XNamespace a, XNamespace r, MarkdownDiagram diagram, int index, long slotTop, long slotHeight)
    {
        const long maxWidth = 10_600_000;
        var maxHeight = Math.Max(1, slotHeight - 120_000);
        var (width, height) = FitEmu(diagram.Image.Width, diagram.Image.Height, maxWidth, maxHeight);
        var x = 800_000 + (maxWidth - width) / 2;
        var y = slotTop + (slotHeight - height) / 2;
        return new XElement(p + "pic",
            new XElement(p + "nvPicPr",
                new XElement(p + "cNvPr", new XAttribute("id", 3 + index), new XAttribute("name", $"Mermaid diagram {index}"), new XAttribute("descr", diagram.AltText)),
                new XElement(p + "cNvPicPr", new XElement(a + "picLocks", new XAttribute("noChangeAspect", "1"))), new XElement(p + "nvPr")),
            new XElement(p + "blipFill", new XElement(a + "blip", new XAttribute(r + "embed", $"rIdDocRedockMermaid{index}")), new XElement(a + "stretch", new XElement(a + "fillRect"))),
            new XElement(p + "spPr", new XElement(a + "xfrm", new XElement(a + "off", new XAttribute("x", x), new XAttribute("y", y)), new XElement(a + "ext", new XAttribute("cx", width), new XAttribute("cy", height))), new XElement(a + "prstGeom", new XAttribute("prst", "rect"), new XElement(a + "avLst"))));
    }

    private sealed record SlideParagraph(string Text, bool Bullet, bool Emphasis);

    private static XDocument PptxLayout(XNamespace p, XNamespace a) => new(new XDeclaration("1.0", "UTF-8", "yes"),
        new XElement(p + "sldLayout", new XAttribute(XNamespace.Xmlns + "p", p), new XAttribute(XNamespace.Xmlns + "a", a), new XAttribute("type", "titleAndContent"), new XAttribute("preserve", "1"),
            new XElement(p + "cSld", new XAttribute("name", "Title and Content"), new XElement(p + "spTree",
                new XElement(p + "nvGrpSpPr", new XElement(p + "cNvPr", new XAttribute("id", "1"), new XAttribute("name", string.Empty)), new XElement(p + "cNvGrpSpPr"), new XElement(p + "nvPr")),
                new XElement(p + "grpSpPr", new XElement(a + "xfrm")))),
            new XElement(p + "clrMapOvr", new XElement(a + "masterClrMapping"))));

    private static XDocument PptxMaster(XNamespace p, XNamespace a, XNamespace r)
    {
        XElement TextStyle(string name, int size) => new(p + name,
            new XElement(a + "lvl1pPr", new XAttribute("algn", "l"), new XElement(a + "defRPr", new XAttribute("sz", size),
                new XElement(a + "latin", new XAttribute("typeface", "Arial")), new XElement(a + "ea", new XAttribute("typeface", "Noto Sans JP")))));
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(p + "sldMaster",
            new XAttribute(XNamespace.Xmlns + "p", p), new XAttribute(XNamespace.Xmlns + "a", a), new XAttribute(XNamespace.Xmlns + "r", r),
            new XElement(p + "cSld", new XAttribute("name", "DocRedock Master"), new XElement(p + "spTree",
                new XElement(p + "nvGrpSpPr", new XElement(p + "cNvPr", new XAttribute("id", "1"), new XAttribute("name", string.Empty)), new XElement(p + "cNvGrpSpPr"), new XElement(p + "nvPr")),
                new XElement(p + "grpSpPr", new XElement(a + "xfrm")))),
            new XElement(p + "clrMap", new XAttribute("bg1", "lt1"), new XAttribute("tx1", "dk1"), new XAttribute("bg2", "lt2"), new XAttribute("tx2", "dk2"), new XAttribute("accent1", "accent1"), new XAttribute("accent2", "accent2"), new XAttribute("accent3", "accent3"), new XAttribute("accent4", "accent4"), new XAttribute("accent5", "accent5"), new XAttribute("accent6", "accent6"), new XAttribute("hlink", "hlink"), new XAttribute("folHlink", "folHlink")),
            new XElement(p + "sldLayoutIdLst", new XElement(p + "sldLayoutId", new XAttribute("id", "2147483649"), new XAttribute(r + "id", "rId2"))),
            new XElement(p + "txStyles", TextStyle("titleStyle", 3200), TextStyle("bodyStyle", 2000), TextStyle("otherStyle", 1800))));
    }

    private static XDocument PptxTheme(XNamespace a)
    {
        XElement Color(string name, string value) => new(a + name, new XElement(a + "srgbClr", new XAttribute("val", value)));
        var colors = new XElement(a + "clrScheme", new XAttribute("name", "DocRedock"),
            Color("dk1", "1F2937"), Color("lt1", "FFFFFF"), Color("dk2", "374151"), Color("lt2", "F3F4F6"),
            Color("accent1", "2563EB"), Color("accent2", "0F766E"), Color("accent3", "7C3AED"), Color("accent4", "D97706"),
            Color("accent5", "DB2777"), Color("accent6", "4D7C0F"), Color("hlink", "0563C1"), Color("folHlink", "954F72"));
        XElement FontSet(string name) => new(a + name,
            new XElement(a + "latin", new XAttribute("typeface", name == "majorFont" ? "Arial" : "Arial")),
            new XElement(a + "ea", new XAttribute("typeface", "Noto Sans JP")), new XElement(a + "cs", new XAttribute("typeface", "Arial")));
        var fonts = new XElement(a + "fontScheme", new XAttribute("name", "DocRedock"), FontSet("majorFont"), FontSet("minorFont"));
        var format = new XElement(a + "fmtScheme", new XAttribute("name", "DocRedock"),
            new XElement(a + "fillStyleLst", new XElement(a + "solidFill", new XElement(a + "schemeClr", new XAttribute("val", "phClr")))),
            new XElement(a + "lnStyleLst", new XElement(a + "ln", new XAttribute("w", "12700"), new XElement(a + "solidFill", new XElement(a + "schemeClr", new XAttribute("val", "phClr"))))),
            new XElement(a + "effectStyleLst", new XElement(a + "effectStyle", new XElement(a + "effectLst"))),
            new XElement(a + "bgFillStyleLst", new XElement(a + "solidFill", new XElement(a + "schemeClr", new XAttribute("val", "phClr")))));
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(a + "theme", new XAttribute(XNamespace.Xmlns + "a", a), new XAttribute("name", "DocRedock"), new XElement(a + "themeElements", colors, fonts, format)));
    }

    private static void WriteXlsx(MarkdownDocument document, string path, RenderOptions? options)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var diagrams = document.Blocks.OfType<MarkdownDiagram>().ToArray();
        var pngContentType = diagrams.Length == 0 ? string.Empty : "<Default Extension=\"png\" ContentType=\"image/png\"/>";
        var drawingContentType = diagrams.Length == 0 ? string.Empty : "<Override PartName=\"/xl/drawings/drawing1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawing+xml\"/>";
        Add(archive, "[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" + pngContentType + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" + drawingContentType + "</Types>");
        Add(archive, "_rels/.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Add(archive, "xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
        XNamespace x = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"; XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        Add(archive, "xl/workbook.xml", new XDocument(new XElement(x + "workbook", new XAttribute(XNamespace.Xmlns + "r", r), new XElement(x + "sheets", new XElement(x + "sheet", new XAttribute("name", "Sheet1"), new XAttribute("sheetId", "1"), new XAttribute(r + "id", "rId1"))))).ToString(SaveOptions.DisableFormatting));
        var rows = new List<IReadOnlyList<string>>();
        foreach (var table in document.Blocks.OfType<MarkdownTable>()) { rows.Add(table.Headers.Select(PlainInlineText).ToArray()); rows.AddRange(table.Rows.Select(row => (IReadOnlyList<string>)row.Select(PlainInlineText).ToArray())); }
        if (rows.Count == 0)
        {
            var text = document.Blocks.Select(block => block switch { MarkdownHeading heading => heading.Text, MarkdownParagraph paragraph => paragraph.Text, _ => null })
                .Where(value => value is not null).Cast<string>().ToArray();
            if (text.Length > 0) rows.Add(text);
        }
        var sheetRows = rows.Select((row, index) => new XElement(x + "row", new XAttribute("r", index + 1), row.Select((cell, column) => new XElement(x + "c", new XAttribute("r", CellRef(column, index)), new XAttribute("t", "inlineStr"), new XElement(x + "is", new XElement(x + "t", cell)))))).ToList();
        var placements = XlsxDiagramPlacements(diagrams, rows.Count + 1);
        sheetRows.AddRange(placements.Select(placement => new XElement(x + "row",
            new XAttribute("r", placement.StartRow + 1),
            new XAttribute("ht", placement.RowHeightPoints.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)),
            new XAttribute("customHeight", "1"))));
        var worksheet = new XElement(x + "worksheet", new XAttribute(XNamespace.Xmlns + "r", r),
            diagrams.Length == 0 ? null : new XElement(x + "cols", new XElement(x + "col", new XAttribute("min", "1"), new XAttribute("max", "12"), new XAttribute("width", "12"), new XAttribute("customWidth", "1"))),
            new XElement(x + "sheetData", sheetRows),
            diagrams.Length == 0 ? null : new XElement(x + "drawing", new XAttribute(r + "id", "rIdDocRedockDrawing1")));
        Add(archive, "xl/worksheets/sheet1.xml", new XDocument(worksheet).ToString(SaveOptions.DisableFormatting));
        if (diagrams.Length == 0) return;

        Add(archive, "xl/worksheets/_rels/sheet1.xml.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdDocRedockDrawing1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"../drawings/drawing1.xml\"/></Relationships>");
        var imageRelationships = string.Concat(diagrams.Select((_, index) => $"<Relationship Id=\"rIdDocRedockMermaid{index + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"../media/docredock-mermaid-{index + 1}.png\"/>"));
        Add(archive, "xl/drawings/_rels/drawing1.xml.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + imageRelationships + "</Relationships>");
        Add(archive, "xl/drawings/drawing1.xml", XlsxDrawing(placements, r).ToString(SaveOptions.DisableFormatting));
        for (var index = 0; index < diagrams.Length; index++) Add(archive, $"xl/media/docredock-mermaid-{index + 1}.png", diagrams[index].Image.PngBytes);
    }

    private static IReadOnlyList<XlsxDiagramPlacement> XlsxDiagramPlacements(IReadOnlyList<MarkdownDiagram> diagrams, int firstRow)
    {
        const long emuPerPixel = 9525;
        var result = new List<XlsxDiagramPlacement>(diagrams.Count);
        for (var index = 0; index < diagrams.Count; index++)
        {
            var (width, height) = FitEmu(diagrams[index].Image.Width, diagrams[index].Image.Height, 900 * emuPerPixel, 500 * emuPerPixel);
            var heightPixels = (double)height / emuPerPixel;
            result.Add(new XlsxDiagramPlacement(diagrams[index], index + 1, firstRow + index, width, height, Math.Min(409, heightPixels * 0.75 + 12)));
        }
        return result;
    }

    private static XDocument XlsxDrawing(IReadOnlyList<XlsxDiagramPlacement> placements, XNamespace r)
    {
        XNamespace xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        var anchors = placements.Select(placement => new XElement(xdr + "oneCellAnchor",
                new XElement(xdr + "from", new XElement(xdr + "col", "0"), new XElement(xdr + "colOff", "76200"), new XElement(xdr + "row", placement.StartRow), new XElement(xdr + "rowOff", "76200")),
                new XElement(xdr + "ext", new XAttribute("cx", placement.WidthEmu), new XAttribute("cy", placement.HeightEmu)),
                new XElement(xdr + "pic",
                    new XElement(xdr + "nvPicPr",
                        new XElement(xdr + "cNvPr", new XAttribute("id", placement.Index), new XAttribute("name", $"Mermaid diagram {placement.Index}"), new XAttribute("descr", placement.Diagram.AltText)),
                        new XElement(xdr + "cNvPicPr", new XElement(a + "picLocks", new XAttribute("noChangeAspect", "1"))),
                        new XElement(xdr + "nvPr")),
                    new XElement(xdr + "blipFill", new XElement(a + "blip", new XAttribute(r + "embed", $"rIdDocRedockMermaid{placement.Index}")), new XElement(a + "stretch", new XElement(a + "fillRect"))),
                    new XElement(xdr + "spPr", new XElement(a + "xfrm", new XElement(a + "off", new XAttribute("x", "0"), new XAttribute("y", "0")), new XElement(a + "ext", new XAttribute("cx", placement.WidthEmu), new XAttribute("cy", placement.HeightEmu))), new XElement(a + "prstGeom", new XAttribute("prst", "rect"), new XElement(a + "avLst")))),
                new XElement(xdr + "clientData")));
        var root = new XElement(xdr + "wsDr",
            new XAttribute(XNamespace.Xmlns + "xdr", xdr), new XAttribute(XNamespace.Xmlns + "a", a), new XAttribute(XNamespace.Xmlns + "r", r), anchors);
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root);
    }

    private sealed record XlsxDiagramPlacement(MarkdownDiagram Diagram, int Index, int StartRow, long WidthEmu, long HeightEmu, double RowHeightPoints);

    private static string CellRef(int column, int row)
    {
        var name = ""; for (var value = column + 1; value > 0; value = (value - 1) / 26) name = (char)('A' + (value - 1) % 26) + name; return name + (row + 1);
    }

    private static string PlainInlineText(string value) => string.Concat(DocRedockInlineMarkdown.Parse(value).Runs.Select(run => run.Text));
    private static string EscapeInlineLiteral(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("*", "\\*", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("~", "\\~", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal);

    private static void WritePdf(MarkdownDocument document, string path, RenderOptions? options)
    {
        var lines = document.Blocks.SelectMany(block => block switch
        {
            MarkdownHeading heading => new[] { PlainInlineText(heading.Text) },
            MarkdownParagraph paragraph => new[] { PlainInlineText(paragraph.Text) },
            MarkdownList list => list.Items.Select(item => "- " + PlainInlineText(item)),
            MarkdownCodeBlock code => new[] { code.Text },
            MarkdownTable table => table.Rows.Select(row => string.Join(" | ", row.Select(PlainInlineText))).Prepend(string.Join(" | ", table.Headers.Select(PlainInlineText))),
            _ => Array.Empty<string>()
        }).ToArray();
        PdfBuilder.Write(path, lines, document.Blocks.OfType<MarkdownDiagram>().ToArray(), options?.FontPath);
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression); using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(content);
    }

    private static void Add(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression); using var output = entry.Open(); output.Write(content);
    }

    /// <summary>
    /// Small self-contained PDF writer.  Unlike the old Type1/Helvetica writer this
    /// writes UTF-16BE character codes to a CIDFontType2 and embeds the Japanese
    /// TrueType font.  That keeps generated PDFs portable and, importantly, keeps
    /// the text available to PDF extractors through the generated ToUnicode map.
    /// </summary>
    private sealed class PdfBuilder
    {
        private const int PageWidth = 612;
        private const int PageHeight = 792;
        private const int FontSize = 12;
        private const int Left = 54;
        private const int Top = 738;
        private const int LineHeight = 17;
        private const int MaxLines = 42;
        private const int MaxLineWidth = PageWidth - (Left * 2);

        public static void Write(string path, IReadOnlyList<string> lines, IReadOnlyList<MarkdownDiagram> diagrams, string? fontPath)
        {
            var font = TrueTypeFont.Load(ResolveFont(fontPath));
            var visualLines = WrapLines(lines, font).Take(diagrams.Count == 0 ? MaxLines : Math.Min(MaxLines, 18)).ToArray();
            var encoding = font.BuildEncoding(visualLines.SelectMany(line => line.CodePoints));
            var placements = PlaceDiagrams(diagrams, visualLines.Length);
            var content = BuildContent(visualLines, placements, encoding);
            var toUnicode = encoding.BuildToUnicodeCMap();
            var widths = encoding.BuildWidths(font);
            var cidToGid = encoding.BuildCidToGidMap(font);
            var imageResources = placements.Count == 0 ? string.Empty : " /XObject << " + string.Join(' ', placements.Select((placement, index) => $"/{placement.Name} {11 + index} 0 R")) + " >>";

            var objects = new List<byte[]>
            {
                Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
                Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] /Resources << /Font << /F1 5 0 R >>{imageResources} >> /Contents 4 0 R >>"),
                StreamObject($"<< /Length {content.Length} >>", content),
                Ascii("<< /Type /Font /Subtype /Type0 /BaseFont /NotoSansJP-Regular /Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 7 0 R >>"),
                Ascii($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /NotoSansJP-Regular /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 8 0 R /CIDToGIDMap 10 0 R /DW 1000 /W [0 [ {widths} ]] >>"),
                StreamObject($"<< /Length {toUnicode.Length} >>", toUnicode),
                Ascii("<< /Type /FontDescriptor /FontName /NotoSansJP-Regular /Flags 4 /FontBBox [" + font.FontBBox + "] /ItalicAngle 0 /Ascent " + font.Ascent + " /Descent " + font.Descent + " /CapHeight " + font.CapHeight + " /StemV 80 /FontFile2 9 0 R >>"),
                StreamObject($"<< /Length {font.Data.Length} /Length1 {font.Data.Length} >>", font.Data),
                StreamObject($"<< /Length {cidToGid.Length} >>", cidToGid),
            };
            foreach (var placement in placements)
            {
                var image = placement.Diagram.Image;
                var compressed = image.DeflateRgb();
                objects.Add(StreamObject($"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {compressed.Length} >>", compressed));
            }

            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(Encoding.ASCII.GetBytes("%PDF-1.4\n%\xFF\xFF\xFF\xFF\n"));
            var offsets = new List<long> { 0 };
            for (var i = 0; i < objects.Count; i++)
            {
                offsets.Add(output.Position);
                output.Write(Encoding.ASCII.GetBytes($"{i + 1} 0 obj\n"));
                output.Write(objects[i]);
                output.Write(Encoding.ASCII.GetBytes("\nendobj\n"));
            }

            var xref = output.Position;
            output.Write(Encoding.ASCII.GetBytes($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
            for (var i = 1; i < offsets.Count; i++)
                output.Write(Encoding.ASCII.GetBytes($"{offsets[i]:D10} 00000 n \n"));
            output.Write(Encoding.ASCII.GetBytes($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        }

        private static string ResolveFont(string? requestedPath)
        {
            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                var fullPath = Path.GetFullPath(requestedPath);
                if (!File.Exists(fullPath)) throw new FileNotFoundException("PDF font was not found.", fullPath);
                return fullPath;
            }

            var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "NotoSansJP[wght].ttf");
            if (File.Exists(bundled)) return bundled;
            throw new FileNotFoundException("The bundled Japanese PDF font is missing. Provide RenderOptions.FontPath or restore the application assets.", bundled);
        }

        private static IReadOnlyList<PdfDiagramPlacement> PlaceDiagrams(IReadOnlyList<MarkdownDiagram> diagrams, int textLineCount)
        {
            if (diagrams.Count == 0) return [];
            var top = Top - textLineCount * LineHeight - 18d;
            const double bottom = 54;
            var available = Math.Max(1, top - bottom);
            var slotHeight = available / diagrams.Count;
            var result = new List<PdfDiagramPlacement>(diagrams.Count);
            for (var index = 0; index < diagrams.Count; index++)
            {
                var diagram = diagrams[index];
                var scale = Math.Min((double)MaxLineWidth / diagram.Image.Width, Math.Max(1, slotHeight - 8) / diagram.Image.Height);
                var width = Math.Max(1, diagram.Image.Width * scale);
                var height = Math.Max(1, diagram.Image.Height * scale);
                var slotTop = top - index * slotHeight;
                result.Add(new PdfDiagramPlacement($"Im{index + 1}", diagram, Left + (MaxLineWidth - width) / 2, slotTop - height - 4, width, height));
            }
            return result;
        }

        private static byte[] BuildContent(IEnumerable<GlyphLine> lines, IReadOnlyList<PdfDiagramPlacement> placements, TrueTypeFont.EncodingTable encoding)
        {
            var builder = new StringBuilder("BT\n/F1 12 Tf\n54 738 Td\n");
            foreach (var line in lines)
                builder.Append('<').Append(encoding.Encode(line.CodePoints)).Append("> Tj\n0 -17 Td\n");
            builder.Append("ET\n");
            foreach (var placement in placements)
                builder.Append("q\n")
                    .Append(PdfNumber(placement.Width)).Append(" 0 0 ").Append(PdfNumber(placement.Height)).Append(' ')
                    .Append(PdfNumber(placement.X)).Append(' ').Append(PdfNumber(placement.Y)).Append(" cm\n/")
                    .Append(placement.Name).Append(" Do\nQ\n");
            return Encoding.ASCII.GetBytes(builder.ToString());
        }

        private static string PdfNumber(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        private static IEnumerable<GlyphLine> WrapLines(IEnumerable<string> source, TrueTypeFont font)
        {
            foreach (var sourceLine in source.SelectMany(line => line.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')))
            {
                var current = new List<uint>();
                var width = 0;
                foreach (var rune in sourceLine.EnumerateRunes())
                {
                    var glyph = font.GetGlyphId((uint)rune.Value);
                    var advance = font.GetAdvance(glyph) * FontSize / font.UnitsPerEm;
                    if (current.Count > 0 && width + advance > MaxLineWidth)
                    {
                        yield return new GlyphLine(current.ToArray(), width);
                        current.Clear();
                        width = 0;
                    }
                    current.Add((uint)rune.Value);
                    width += advance;
                }
                yield return new GlyphLine(current.ToArray(), width);
            }
        }

        private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

        private static byte[] StreamObject(string dictionary, byte[] stream)
        {
            using var buffer = new MemoryStream();
            buffer.Write(Encoding.ASCII.GetBytes(dictionary));
            buffer.Write(Encoding.ASCII.GetBytes("\nstream\n"));
            buffer.Write(stream);
            buffer.Write(Encoding.ASCII.GetBytes("\nendstream"));
            return buffer.ToArray();
        }

        private sealed record GlyphLine(IReadOnlyList<uint> CodePoints, int Width);
        private sealed record PdfDiagramPlacement(string Name, MarkdownDiagram Diagram, double X, double Y, double Width, double Height);

        private sealed class TrueTypeFont
        {
            private readonly Dictionary<string, Table> _tables;
            private readonly ushort[] _advances;
            private readonly Func<uint, ushort> _glyphLookup;
            public byte[] Data { get; }
            public int UnitsPerEm { get; }
            public string FontBBox { get; }
            public int Ascent { get; }
            public int Descent { get; }
            public int CapHeight { get; }
            private ushort GlyphCount { get; }

            private TrueTypeFont(byte[] data, Dictionary<string, Table> tables, ushort glyphCount, int unitsPerEm,
                ushort[] advances, Func<uint, ushort> glyphLookup, short xMin, short yMin, short xMax, short yMax,
                short ascent, short descent)
            {
                Data = data;
                _tables = tables;
                GlyphCount = glyphCount;
                UnitsPerEm = unitsPerEm;
                _advances = advances;
                _glyphLookup = glyphLookup;
                FontBBox = $"{xMin} {yMin} {xMax} {yMax}";
                Ascent = ascent * 1000 / unitsPerEm;
                Descent = descent * 1000 / unitsPerEm;
                CapHeight = Ascent;
            }

            public static TrueTypeFont Load(string path)
            {
                var data = File.ReadAllBytes(path);
                if (data.Length < 12) throw new InvalidDataException("PDF font is not a valid TrueType font.");
                var count = ReadUInt16(data, 4);
                var tables = new Dictionary<string, Table>(StringComparer.Ordinal);
                for (var i = 0; i < count; i++)
                {
                    var at = 12 + (i * 16);
                    var tag = Encoding.ASCII.GetString(data, at, 4);
                    tables[tag] = new Table(ReadInt32(data, at + 8), ReadInt32(data, at + 12));
                }
                var head = tables["head"];
                var unitsPerEm = ReadUInt16(data, head.Offset + 18);
                var xMin = ReadInt16(data, head.Offset + 36);
                var yMin = ReadInt16(data, head.Offset + 38);
                var xMax = ReadInt16(data, head.Offset + 40);
                var yMax = ReadInt16(data, head.Offset + 42);
                var maxp = tables["maxp"];
                var glyphCount = ReadUInt16(data, maxp.Offset + 4);
                var hhea = tables["hhea"];
                var ascent = ReadInt16(data, hhea.Offset + 4);
                var descent = ReadInt16(data, hhea.Offset + 6);
                var metricCount = ReadUInt16(data, hhea.Offset + 34);
                var hmtx = tables["hmtx"];
                var advances = new ushort[glyphCount];
                ushort lastAdvance = 0;
                for (var glyph = 0; glyph < glyphCount; glyph++)
                {
                    if (glyph < metricCount) lastAdvance = ReadUInt16(data, hmtx.Offset + glyph * 4);
                    advances[glyph] = lastAdvance;
                }
                var lookup = BuildCmapLookup(data, tables["cmap"]);
                return new TrueTypeFont(data, tables, glyphCount, unitsPerEm, advances, lookup, xMin, yMin, xMax, yMax, ascent, descent);
            }

            public ushort GetGlyphId(uint codePoint)
            {
                var glyph = _glyphLookup(codePoint);
                return glyph < GlyphCount ? glyph : (ushort)0;
            }

            public ushort GetAdvance(ushort glyph) => _advances[glyph < _advances.Length ? glyph : 0];

            public EncodingTable BuildEncoding(IEnumerable<uint> codePoints) => new(
                codePoints.Where(codePoint => codePoint is not (>= 0xD800 and <= 0xDFFF)).Distinct().ToArray(), this);

            public sealed class EncodingTable
            {
                private readonly Dictionary<uint, ushort> _cids;
                private readonly ushort[] _glyphs;

                public EncodingTable(IReadOnlyList<uint> codePoints, TrueTypeFont font)
                {
                    _cids = new Dictionary<uint, ushort>(codePoints.Count);
                    _glyphs = new ushort[codePoints.Count + 1];
                    for (var i = 0; i < codePoints.Count; i++)
                    {
                        var cid = checked((ushort)(i + 1));
                        _cids[codePoints[i]] = cid;
                        _glyphs[cid] = font.GetGlyphId(codePoints[i]);
                    }
                }

                public string Encode(IEnumerable<uint> codePoints)
                {
                    var builder = new StringBuilder();
                    foreach (var codePoint in codePoints)
                    {
                        var cid = _cids.TryGetValue(codePoint, out var value) ? value : (ushort)0;
                        builder.Append(cid.ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    return builder.ToString();
                }

                public string BuildWidths(TrueTypeFont font)
                {
                    var values = new string[_glyphs.Length];
                    for (var i = 0; i < values.Length; i++)
                        values[i] = Math.Clamp((int)Math.Round(font.GetAdvance(_glyphs[i]) * 1000d / font.UnitsPerEm), 0, 2000).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return string.Join(' ', values);
                }

                public byte[] BuildCidToGidMap(TrueTypeFont font)
                {
                    var result = new byte[_glyphs.Length * 2];
                    for (var i = 0; i < _glyphs.Length; i++)
                    {
                        var glyph = _glyphs[i];
                        result[i * 2] = (byte)(glyph >> 8);
                        result[(i * 2) + 1] = (byte)glyph;
                    }
                    return result;
                }

                public byte[] BuildToUnicodeCMap()
                {
                    var builder = new StringBuilder();
                    builder.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
                    builder.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
                    builder.Append("/CMapName /DRMD-UTF16 def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
                    foreach (var batch in _cids.OrderBy(pair => pair.Value).Chunk(100))
                    {
                        builder.Append(batch.Length).Append(" beginbfchar\n");
                        foreach (var mapping in batch)
                            builder.Append('<').Append(mapping.Value.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)).Append("> <")
                                .Append(ToUtf16Hex(mapping.Key)).Append(">\n");
                        builder.Append("endbfchar\n");
                    }
                    builder.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
                    return Encoding.ASCII.GetBytes(builder.ToString());
                }

                private static string ToUtf16Hex(uint value)
                {
                    if (value <= 0xFFFF) return value.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
                    value -= 0x10000;
                    var high = 0xD800 + (value >> 10);
                    var low = 0xDC00 + (value & 0x3FF);
                    return high.ToString("X4", System.Globalization.CultureInfo.InvariantCulture) + low.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            private static Func<uint, ushort> BuildCmapLookup(byte[] data, Table cmap)
            {
                var offset = cmap.Offset;
                var records = ReadUInt16(data, offset + 2);
                var format12 = -1;
                var format4 = -1;
                for (var i = 0; i < records; i++)
                {
                    var at = offset + 4 + i * 8;
                    var platform = ReadUInt16(data, at);
                    var encoding = ReadUInt16(data, at + 2);
                    var subtable = offset + ReadInt32(data, at + 4);
                    var format = ReadUInt16(data, subtable);
                    if (format == 12 && (platform == 3 && encoding == 10 || platform == 0)) format12 = subtable;
                    else if (format == 4 && (platform == 3 && (encoding == 1 || encoding == 0) || platform == 0)) format4 = subtable;
                }
                if (format12 >= 0)
                {
                    var groups = ReadInt32(data, format12 + 12);
                    var starts = new uint[groups]; var ends = new uint[groups]; var glyphs = new uint[groups];
                    for (var i = 0; i < groups; i++) { var at = format12 + 16 + i * 12; starts[i] = ReadUInt32(data, at); ends[i] = ReadUInt32(data, at + 4); glyphs[i] = ReadUInt32(data, at + 8); }
                    return codePoint => { var lo = 0; var hi = starts.Length - 1; while (lo <= hi) { var mid = (lo + hi) / 2; if (codePoint < starts[mid]) hi = mid - 1; else if (codePoint > ends[mid]) lo = mid + 1; else return (ushort)Math.Min(ushort.MaxValue, glyphs[mid] + codePoint - starts[mid]); } return (ushort)0; };
                }
                if (format4 >= 0)
                {
                    var segments = ReadUInt16(data, format4 + 6) / 2;
                    var endAt = format4 + 14;
                    var startAt = endAt + segments * 2 + 2;
                    var deltaAt = startAt + segments * 2;
                    var rangeAt = deltaAt + segments * 2;
                    return codePoint =>
                    {
                        if (codePoint > 0xFFFF) return 0;
                        var code = (ushort)codePoint;
                        for (var i = 0; i < segments; i++)
                        {
                            var end = ReadUInt16(data, endAt + i * 2);
                            if (code > end) continue;
                            var start = ReadUInt16(data, startAt + i * 2);
                            if (code < start) return 0;
                            var delta = ReadInt16(data, deltaAt + i * 2);
                            var range = ReadUInt16(data, rangeAt + i * 2);
                            if (range == 0) return (ushort)((code + delta) & 0xFFFF);
                            var glyphAt = rangeAt + i * 2 + range + (code - start) * 2;
                            var glyph = ReadUInt16(data, glyphAt);
                            return glyph == 0 ? (ushort)0 : (ushort)((glyph + delta) & 0xFFFF);
                        }
                        return (ushort)0;
                    };
                }
                throw new InvalidDataException("PDF font does not contain a Unicode cmap table.");
            }

            private static ushort ReadUInt16(byte[] data, int offset) => (ushort)((data[offset] << 8) | data[offset + 1]);
            private static short ReadInt16(byte[] data, int offset) => unchecked((short)ReadUInt16(data, offset));
            private static int ReadInt32(byte[] data, int offset) => unchecked((int)ReadUInt32(data, offset));
            private static uint ReadUInt32(byte[] data, int offset) => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
            private sealed record Table(int Offset, int Length);
        }
    }
}