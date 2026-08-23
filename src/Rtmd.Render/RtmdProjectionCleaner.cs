using System.Text;
using System.Text.RegularExpressions;
using Rtmd.Markdown;

namespace Rtmd.Render;

/// <summary>
/// Converts an RTMD projection into the ordinary Markdown consumed by the
/// document renderers.  RTMD's front matter and HTML comments are control
/// data, never document content, and must not leak into a customer artifact.
/// </summary>
public static class RtmdProjectionCleaner
{
    private static readonly Regex FrontMatter = new(@"\A\s*---\r?\n(?<body>.*?)\r?\n---\r?\n", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ControlComment = new(@"<!--rtmd:(?:block|delete|new|partition-begin|partition-end|document-end)(?:\s+[^>]*)?-->", RegexOptions.Compiled);
    private static readonly Regex Fence = new(@"^[ \t]{0,3}(?:`{3,}|~{3,})", RegexOptions.Compiled);

    public static bool IsRtmdProjection(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var front = FrontMatter.Match(markdown);
        return front.Success && Regex.IsMatch(front.Groups["body"].Value, @"(?m)^\s*rtmd_schema\s*:", RegexOptions.CultureInvariant)
            || ContainsControlCommentOutsideFence(markdown);
    }

    /// <summary>Returns clean generic Markdown, rejecting incomplete or malformed RTMD.</summary>
    public static string Clean(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (!IsRtmdProjection(markdown)) return markdown;

        var parsed = new RtmdMarkdownParser().Parse(markdown, new MarkdownParseOptions { Strict = true });
        if (!parsed.IsComplete)
        {
            var details = string.Join("; ", parsed.Diagnostics.Where(d => d.Severity == MarkdownDiagnosticSeverity.Error)
                .Select(d => $"{d.Code}: {d.Message}"));
            throw new InvalidDataException($"RTMD projection is invalid and cannot be rendered.{(details.Length == 0 ? "" : " " + details)}");
        }

        var content = markdown;
        var front = FrontMatter.Match(content);
        if (front.Success) content = content.Remove(front.Index, front.Length);
        content = RemoveControlCommentsOutsideFences(content);
        return content.Trim('\r', '\n') + Environment.NewLine;
    }

    private static bool ContainsControlCommentOutsideFence(string markdown)
    {
        var inFence = false;
        foreach (Match line in Regex.Matches(markdown, ".*?(?:\\r?\\n|$)"))
        {
            if (line.Length == 0) continue;
            if (Fence.IsMatch(line.Value)) { inFence = !inFence; continue; }
            if (!inFence && ControlComment.IsMatch(line.Value)) return true;
        }
        return false;
    }

    private static string RemoveControlCommentsOutsideFences(string markdown)
    {
        var output = new StringBuilder(markdown.Length);
        var inFence = false;
        foreach (Match line in Regex.Matches(markdown, ".*?(?:\\r?\\n|$)"))
        {
            if (line.Length == 0) continue;
            if (Fence.IsMatch(line.Value))
            {
                inFence = !inFence;
                output.Append(line.Value);
                continue;
            }
            output.Append(inFence ? line.Value : ControlComment.Replace(line.Value, string.Empty));
        }
        return output.ToString();
    }
}
