namespace DocRedock.Markdown;

/// <summary>Encodes relative Markdown link paths without escaping non-ASCII characters.</summary>
public static class MarkdownPathEncoder
{
    private const string SafeAsciiPunctuation = "-._~!$&'*+,;=:@";

    public static string Encode(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return string.Join('/', relativePath.Split('/', StringSplitOptions.None).Select(EncodeSegment));
    }

    private static string EncodeSegment(string segment)
    {
        var output = new System.Text.StringBuilder(segment.Length);
        foreach (var character in segment)
        {
            if (character <= 0x7f &&
                !char.IsAsciiLetterOrDigit(character) &&
                !SafeAsciiPunctuation.Contains(character))
            {
                output.Append('%').Append(((int)character).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                continue;
            }

            output.Append(character);
        }
        return output.ToString();
    }
}
