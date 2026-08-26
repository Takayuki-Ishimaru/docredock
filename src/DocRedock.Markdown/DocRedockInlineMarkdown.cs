using System.Text;
using DocRedock.Core.Documents;

namespace DocRedock.Markdown;

/// <summary>
/// Deterministic inline Markdown used by DRMD rich-text blocks.  The supported
/// subset is deliberately small and reversible: bold, italic, underline,
/// strike-through, inline code, hyperlinks, line breaks, and tabs.
/// </summary>
public static class DocRedockInlineMarkdown
{
    public static string Serialize(IReadOnlyList<TextRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var output = new StringBuilder();
        for (var index = 0; index < runs.Count; index++)
        {
            var run = runs[index];
            if (run.Kind == TextRunKind.LineBreak)
            {
                output.Append("<br>");
                continue;
            }
            if (run.Kind == TextRunKind.Tab)
            {
                output.Append("&#9;");
                continue;
            }

            // OpenXML frequently splits one visually continuous span into
            // several runs (font, language, or proofing boundaries). Merge
            // adjacent runs with the same semantic style before adding
            // Markdown delimiters; otherwise output such as
            // **Label: ****VALUE** becomes hard to read.
            var text = new StringBuilder(run.Text);
            while (index + 1 < runs.Count && runs[index + 1].Kind == TextRunKind.Text &&
                   SameStyle(run, runs[index + 1]))
                text.Append(runs[++index].Text);
            AppendStyledText(output, text.ToString(), run);
        }
        return output.ToString();
    }

    private static bool SameStyle(TextRun left, TextRun right) =>
        left.Bold == right.Bold && left.Italic == right.Italic &&
        left.Underline == right.Underline && left.Strike == right.Strike &&
        left.Code == right.Code && left.LinkTarget == right.LinkTarget &&
        left.Color == right.Color && left.HighlightColor == right.HighlightColor;

    private static void AppendStyledText(StringBuilder output, string text, TextRun style)
    {
        if (text.Length == 0) return;

        // Keep whitespace outside emphasis and hyperlink delimiters. This avoids a run
        // boundary producing '**Recommendation. **Proceed' and keeps the
        // following word visibly separated in every Markdown renderer.
        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        var end = text.Length;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        output.Append(Escape(text[..start]));
        if (end == start)
        {
            output.Append(Escape(text[start..]));
            return;
        }

        var value = style.Code ? CodeSpan(text[start..end]) : Escape(text[start..end]);
        if (style.Italic) value = "_" + value + "_";
        if (style.Bold) value = "**" + value + "**";
        if (style.Strike) value = "~~" + value + "~~";
        if (style.Underline) value = "<u>" + value + "</u>";
        if (style.LinkTarget is not null) value = "[" + value + "](" + LinkDestination(style.LinkTarget) + ")";
        output.Append(value).Append(Escape(text[end..]));
    }

    public static RichTextNodeContent Parse(string markdown, IReadOnlyList<TextRun>? baselineRuns = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var runs = new List<TextRun>();
        var text = new StringBuilder();
        var bold = false;
        var italic = false;
        var underline = false;
        var strike = false;

        void Flush()
        {
            if (text.Length == 0) return;
            AddRun(runs, new TextRun(DecodeEntities(text.ToString()), MatchingStyleId(baselineRuns, bold, italic, underline, strike, false),
                bold, italic, underline, strike));
            text.Clear();
        }

        for (var index = 0; index < markdown.Length;)
        {
            if (markdown[index] == '\\' && index + 1 < markdown.Length)
            {
                text.Append(markdown[index + 1]);
                index += 2;
                continue;
            }
            if (TryReadLink(markdown, index, out var label, out var target, out var nextIndex))
            {
                Flush();
                var linkBaseline = baselineRuns?.Where(run => StringComparer.Ordinal.Equals(run.LinkTarget, target)).ToArray();
                foreach (var linkRun in Parse(label, linkBaseline).Runs)
                    AddRun(runs, linkRun with { LinkTarget = target });
                index = nextIndex;
                continue;
            }
            if (StartsWith(markdown, index, "<br>"))
            {
                Flush();
                AddRun(runs, new TextRun("\n", Kind: TextRunKind.LineBreak));
                index += 4;
                continue;
            }
            if (StartsWith(markdown, index, "&#9;"))
            {
                Flush();
                AddRun(runs, new TextRun("\t", Kind: TextRunKind.Tab));
                index += 4;
                continue;
            }
            if (StartsWith(markdown, index, "<u>"))
            {
                Flush();
                underline = true;
                index += 3;
                continue;
            }
            if (StartsWith(markdown, index, "</u>"))
            {
                Flush();
                underline = false;
                index += 4;
                continue;
            }
            if (StartsWith(markdown, index, "**"))
            {
                Flush();
                bold = !bold;
                index += 2;
                continue;
            }
            if (StartsWith(markdown, index, "~~"))
            {
                Flush();
                strike = !strike;
                index += 2;
                continue;
            }
            if (markdown[index] == '_')
            {
                Flush();
                italic = !italic;
                index++;
                continue;
            }
            if (markdown[index] == '`')
            {
                Flush();
                var delimiterLength = 1;
                while (index + delimiterLength < markdown.Length && markdown[index + delimiterLength] == '`') delimiterLength++;
                var delimiter = new string('`', delimiterLength);
                var close = markdown.IndexOf(delimiter, index + delimiterLength, StringComparison.Ordinal);
                if (close < 0)
                {
                    text.Append(delimiter);
                    index += delimiterLength;
                    continue;
                }
                var code = markdown.Substring(index + delimiterLength, close - index - delimiterLength);
                AddRun(runs, new TextRun(code, MatchingStyleId(baselineRuns, bold, italic, underline, strike, true),
                    bold, italic, underline, strike, true));
                index = close + delimiterLength;
                continue;
            }
            if (markdown[index] == '\n')
            {
                Flush();
                AddRun(runs, new TextRun("\n", Kind: TextRunKind.LineBreak));
                index++;
                continue;
            }
            if (markdown[index] == '\t')
            {
                Flush();
                AddRun(runs, new TextRun("\t", Kind: TextRunKind.Tab));
                index++;
                continue;
            }
            text.Append(markdown[index]);
            index++;
        }
        Flush();
        return new RichTextNodeContent(runs);
    }

    private static void AddRun(List<TextRun> runs, TextRun run)
    {
        if (run.Text.Length == 0) return;
        if (runs.Count > 0 && runs[^1].Kind == TextRunKind.Text && run.Kind == TextRunKind.Text &&
            runs[^1].StyleId == run.StyleId && runs[^1].Bold == run.Bold && runs[^1].Italic == run.Italic &&
            runs[^1].Underline == run.Underline && runs[^1].Strike == run.Strike && runs[^1].Code == run.Code &&
            runs[^1].LinkTarget == run.LinkTarget && runs[^1].Color == run.Color &&
            runs[^1].HighlightColor == run.HighlightColor)
        {
            runs[^1] = runs[^1] with { Text = runs[^1].Text + run.Text };
            return;
        }
        runs.Add(run);
    }

    private static string? MatchingStyleId(IReadOnlyList<TextRun>? baselineRuns, bool bold, bool italic, bool underline, bool strike, bool code) =>
        baselineRuns?.FirstOrDefault(run => run.Kind == TextRunKind.Text && run.Bold == bold && run.Italic == italic &&
            run.Underline == underline && run.Strike == strike && run.Code == code)?.StyleId;

    private static bool TryReadLink(string markdown, int start, out string label, out string target, out int nextIndex)
    {
        label = string.Empty;
        target = string.Empty;
        nextIndex = start;
        if (start >= markdown.Length || markdown[start] != '[') return false;

        var closeLabel = -1;
        for (var index = start + 1; index < markdown.Length; index++)
        {
            if (markdown[index] == ']' && (index == start + 1 || markdown[index - 1] != '\\'))
            {
                closeLabel = index;
                break;
            }
        }
        if (closeLabel < 0 || closeLabel + 1 >= markdown.Length || markdown[closeLabel + 1] != '(') return false;

        var targetStart = closeLabel + 2;
        int closeTarget;
        if (targetStart < markdown.Length && markdown[targetStart] == '<')
        {
            closeTarget = markdown.IndexOf(">)", targetStart + 1, StringComparison.Ordinal);
            if (closeTarget < 0) return false;
            target = markdown[(targetStart + 1)..closeTarget];
            nextIndex = closeTarget + 2;
        }
        else
        {
            closeTarget = markdown.IndexOf(')', targetStart);
            if (closeTarget < 0) return false;
            target = markdown[targetStart..closeTarget];
            nextIndex = closeTarget + 1;
        }

        label = markdown[(start + 1)..closeLabel];
        return target.Length > 0;
    }

    private static string LinkDestination(string target) =>
        target.IndexOfAny([' ', '(', ')']) >= 0 ? "<" + target.Replace(">", "%3E", StringComparison.Ordinal) + ">" : target;

    private static string CodeSpan(string value)
    {
        var longest = 0;
        var current = 0;
        foreach (var character in value)
        {
            if (character == '`') { current++; longest = Math.Max(longest, current); }
            else current = 0;
        }
        var delimiter = new string('`', Math.Max(1, longest + 1));
        return delimiter + value + delimiter;
    }

    private static string Escape(string value)
    {
        var output = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\\' or '*' or '_' or '~' or '`') output.Append('\\');
            output.Append(character switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", _ => character.ToString() });
        }
        return output.ToString();
    }

    private static string DecodeEntities(string value) => value.Replace("&lt;", "<", StringComparison.Ordinal)
        .Replace("&gt;", ">", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);

    private static bool StartsWith(string value, int index, string token) =>
        index + token.Length <= value.Length && value.AsSpan(index, token.Length).SequenceEqual(token.AsSpan());
}
