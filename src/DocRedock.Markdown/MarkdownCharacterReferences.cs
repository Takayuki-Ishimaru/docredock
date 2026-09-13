using System.Globalization;
using System.Text;

namespace DocRedock.Markdown;

/// <summary>
/// The single character-reference policy shared by every Markdown reader in the product.
///
/// It implements CommonMark "Entity and numeric character references"
/// (https://spec.commonmark.org/current/#entity-and-numeric-character-references):
/// a named reference <c>&amp;name;</c>, a decimal reference <c>&amp;#N;</c> (1-7 digits), and a
/// hexadecimal reference <c>&amp;#xH;</c> / <c>&amp;#XH;</c> (1-6 digits) resolve to the character
/// they denote; anything else -- an unknown name, a missing trailing semicolon, too many digits --
/// is ordinary text and is returned unchanged.
///
/// Callers own the "where": references are resolved in ordinary text and in link/image
/// destinations and titles, and are NOT resolved inside code spans, fenced/indented code blocks, or
/// autolinks.  Callers also own backslash escapes: <c>\&amp;</c> is a literal ampersand that must
/// never start a reference, so a caller resolves (or stands in for) backslash escapes BEFORE
/// calling <see cref="Decode"/> -- see <see cref="EscapedAmpersandPlaceholder"/>.
/// </summary>
public static class MarkdownCharacterReferences
{
    /// <summary>
    /// Stands in for a backslash-escaped ampersand (<c>\&amp;</c>) while references are decoded.
    /// <see cref="Decode"/> never treats it as the start of a reference, so a caller that replaces
    /// <c>\&amp;</c> with this character first, decodes, and then puts a literal '&amp;' back --
    /// <see cref="RestoreEscapedAmpersands"/> -- gets CommonMark's behaviour, where <c>\&amp;amp;</c>
    /// is the literal text "&amp;amp;" rather than a decoded ampersand.
    ///
    /// U+FDD0..U+FDEF are Unicode noncharacters, reserved by Unicode for exactly this kind of
    /// internal, never-interchanged use; unlike a private-use character they can never collide with
    /// a symbol-font glyph a converted source document legitimately contains.  MarkdownRenderer's
    /// own escape placeholders live in the same block and map a backslash-escaped '&amp;' to this
    /// very code point, which is deliberate: both readers stand in for <c>\&amp;</c> identically.
    /// </summary>
    public const char EscapedAmpersandPlaceholder = '\uFDD5';

    // The longest HTML5 entity name ("CounterClockwiseContourIntegral") is 30 characters; 32 leaves
    // headroom without letting a stray '&' scan an unbounded distance for a semicolon.
    private const int MaxNameLength = 32;
    private const int MaxDecimalDigits = 7;
    private const int MaxHexDigits = 6;
    private const string ReplacementCharacter = "\uFFFD";

    /// <summary>Resolves every character reference in <paramref name="value"/>.</summary>
    public static string Decode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var first = value.IndexOf('&', StringComparison.Ordinal);
        if (first < 0) return value;
        var output = new StringBuilder(value.Length);
        output.Append(value, 0, first);
        for (var index = first; index < value.Length;)
        {
            if (value[index] == '&' && TryDecodeReference(value, index, out var decoded, out var length))
            {
                output.Append(decoded);
                index += length;
                continue;
            }
            output.Append(value[index]);
            index++;
        }
        return output.ToString();
    }

    /// <summary>Turns every <see cref="EscapedAmpersandPlaceholder"/> back into a literal '&amp;'.</summary>
    public static string RestoreEscapedAmpersands(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace(EscapedAmpersandPlaceholder, '&');
    }

    private static bool TryDecodeReference(string value, int start, out string decoded, out int length)
    {
        decoded = string.Empty;
        length = 0;
        if (start + 1 >= value.Length) return false;
        return value[start + 1] == '#'
            ? TryDecodeNumeric(value, start, out decoded, out length)
            : TryDecodeNamed(value, start, out decoded, out length);
    }

    // The generated WHATWG HTML5 table includes every semicolon-terminated name,
    // with ordinal case sensitivity and one- or two-code-point expansions.
    private static bool TryDecodeNamed(string value, int start, out string decoded, out int length)
    {
        decoded = string.Empty;
        length = 0;
        var index = start + 1;
        var limit = Math.Min(value.Length, index + MaxNameLength);
        while (index < limit && char.IsAsciiLetterOrDigit(value[index])) index++;
        if (index == start + 1 || index >= value.Length || value[index] != ';') return false;
        if (!HtmlEntityTable.Names.TryGetValue(value[(start + 1)..index], out var resolved)) return false;
        decoded = resolved;
        length = index + 1 - start;
        return true;
    }

    private static bool TryDecodeNumeric(string value, int start, out string decoded, out int length)
    {
        decoded = string.Empty;
        length = 0;
        var index = start + 2;
        if (index >= value.Length) return false;
        var hexadecimal = value[index] is 'x' or 'X';
        if (hexadecimal) index++;
        var digitsStart = index;
        var maximumDigits = hexadecimal ? MaxHexDigits : MaxDecimalDigits;
        while (index < value.Length && index - digitsStart < maximumDigits &&
               (hexadecimal ? char.IsAsciiHexDigit(value[index]) : char.IsAsciiDigit(value[index])))
            index++;
        if (index == digitsStart || index >= value.Length || value[index] != ';') return false;
        var codePoint = int.Parse(value[digitsStart..index],
            hexadecimal ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture);
        // CommonMark: NUL, a lone surrogate, and anything past the last Unicode code point are
        // replaced with U+FFFD rather than rejected.
        decoded = codePoint == 0 || codePoint > 0x10FFFF || codePoint is >= 0xD800 and <= 0xDFFF
            ? ReplacementCharacter
            : char.ConvertFromUtf32(codePoint);
        length = index + 1 - start;
        return true;
    }
}
