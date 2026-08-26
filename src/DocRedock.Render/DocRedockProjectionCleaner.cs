using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Markdown;

namespace DocRedock.Render;

/// <summary>
/// Converts an DRMD projection into the ordinary Markdown consumed by the
/// document renderers.  DRMD's front matter and HTML comments are control
/// data, never document content, and must not leak into a customer artifact.
/// </summary>
public static class DocRedockProjectionCleaner
{
    private static readonly Regex FrontMatter = new(@"\A\s*---\r?\n(?<body>.*?)\r?\n---\r?\n", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ControlComment = new(@"<!--drmd:(?:block|delete|new|sheet-table|partition-begin|partition-end|document-end)(?:\s+[^>]*)?-->", RegexOptions.Compiled);
    private static readonly Regex DisplayComment = new(
        @"<!--\s*(?:drmd:(?:block|delete|new|sheet-table|partition-begin|partition-end|document-end)(?:\s+[^>]*)?|inferred:\s*[^>]*)-->", RegexOptions.Compiled);
    private static readonly Regex Fence = new(@"^ {0,3}(?<marker>`{3,}|~{3,})(?<suffix>[^\r\n]*)\r?\n?$", RegexOptions.Compiled);

    public static bool IsDocRedockProjection(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var front = FrontMatter.Match(markdown);
        return front.Success && Regex.IsMatch(front.Groups["body"].Value, @"(?m)^\s*drmd_schema\s*:", RegexOptions.CultureInvariant)
            || ContainsControlCommentOutsideFence(markdown);
    }

    /// <summary>Returns clean generic Markdown, rejecting incomplete or malformed DRMD.</summary>
    public static string Clean(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (!IsDocRedockProjection(markdown)) return RemoveControlCommentsOutsideFences(markdown);

        var parsed = new DocRedockMarkdownParser().Parse(markdown, new MarkdownParseOptions { Strict = true });
        if (!parsed.IsComplete)
        {
            var details = string.Join("; ", parsed.Diagnostics.Where(d => d.Severity == MarkdownDiagnosticSeverity.Error)
                .Select(d => $"{d.Code}: {d.Message}"));
            throw new InvalidDataException($"DRMD projection is invalid and cannot be rendered.{(details.Length == 0 ? "" : " " + details)}");
        }

        var content = markdown;
        var front = FrontMatter.Match(content);
        if (front.Success) content = content.Remove(front.Index, front.Length);
        content = RemoveControlCommentsOutsideFences(content);
        return content.Trim('\r', '\n') + Environment.NewLine;
    }

    private static bool ContainsControlCommentOutsideFence(string markdown)
    {
        string? openFence = null;
        foreach (Match line in Regex.Matches(markdown, ".*?(?:\\r?\\n|$)"))
        {
            if (line.Length == 0) continue;
            if (TryUpdateFence(line.Value, ref openFence)) continue;
            if (openFence is null && ControlComment.IsMatch(line.Value)) return true;
        }
        return false;
    }

    private static string RemoveControlCommentsOutsideFences(string markdown)
    {
        var output = new StringBuilder(markdown.Length);
        string? openFence = null;
        foreach (Match line in Regex.Matches(markdown, ".*?(?:\\r?\\n|$)"))
        {
            if (line.Length == 0) continue;
            if (TryUpdateFence(line.Value, ref openFence))
            {
                output.Append(line.Value);
                continue;
            }
            output.Append(openFence is not null ? line.Value : DisplayComment.Replace(line.Value, string.Empty));
        }
        return output.ToString();
    }

    /// <summary>
    /// Tracks CommonMark-style fenced code blocks. A closing fence must use the
    /// same delimiter and be at least as long as the opening fence; shorter
    /// backtick runs are ordinary code content.
    /// </summary>
    private static bool TryUpdateFence(string line, ref string? openFence)
    {
        var match = Fence.Match(line);
        if (!match.Success) return false;

        var marker = match.Groups["marker"].Value;
        var suffix = match.Groups["suffix"].Value;
        if (openFence is null)
        {
            // CommonMark does not allow a backtick in the info string of a
            // backtick fence because it would make inline code ambiguous.
            if (marker[0] == '`' && suffix.Contains('`')) return false;
            openFence = marker;
            return true;
        }

        // Closing fences may contain only optional trailing whitespace. Lines
        // such as ```text remain code content inside an existing fence.
        if (marker[0] != openFence[0] || marker.Length < openFence.Length ||
            suffix.Any(character => character is not (' ' or '\t'))) return false;
        openFence = null;
        return true;
    }
}
