using DocRedock.Core.Documents;

namespace DocRedock.Formats.OpenXml.Common;

/// <summary>
/// Byte-level XML scanner used only for slice boundaries. Semantic processing uses SafeXml;
/// this scanner never uses a regex and respects comments, CDATA, processing instructions and quotes.
/// </summary>
internal static class XmlSliceScanner
{
    internal sealed record BlockSlice(string LocalName, int Start, int End, RawSliceRef Reference);
    internal sealed record BodySlices(IReadOnlyList<BlockSlice> Blocks, int BodyEndTagStart);

    public static BodySlices FindWordBodyBlocks(byte[] xml, string partUri)
    {
        var stack = new Stack<(string LocalName, int Start)>();
        var blocks = new List<BlockSlice>();
        var bodyDepth = -1;
        var activeBlocks = new Stack<(string Name, int Start)>();
        var bodyEnd = -1;
        for (var cursor = 0; cursor < xml.Length;)
        {
            if (xml[cursor] != (byte)'<') { cursor++; continue; }
            if (Starts(xml, cursor, "<!--"u8)) { cursor = FindTerminator(xml, cursor + 4, "-->"u8); continue; }
            if (Starts(xml, cursor, "<![CDATA["u8)) { cursor = FindTerminator(xml, cursor + 9, "]]>"u8); continue; }
            if (Starts(xml, cursor, "<?"u8)) { cursor = FindTerminator(xml, cursor + 2, "?>"u8); continue; }
            if (Starts(xml, cursor, "<!"u8)) { cursor = FindTagEnd(xml, cursor + 2) + 1; continue; }

            var isEnd = cursor + 1 < xml.Length && xml[cursor + 1] == (byte)'/';
            var nameStart = cursor + (isEnd ? 2 : 1);
            var nameEnd = nameStart;
            while (nameEnd < xml.Length && IsNameByte(xml[nameEnd])) nameEnd++;
            if (nameEnd == nameStart) throw new InvalidDataException("Malformed XML tag while indexing slices.");
            var local = LocalName(xml, nameStart, nameEnd);
            var tagEnd = FindTagEnd(xml, nameEnd);
            var selfClosing = !isEnd && IsSelfClosing(xml, nameEnd, tagEnd);
            if (isEnd)
            {
                if (stack.Count == 0 || !StringComparer.Ordinal.Equals(stack.Peek().LocalName, local))
                    throw new InvalidDataException("Malformed XML nesting while indexing slices.");
                var opened = stack.Pop();
                if (activeBlocks.Count > 0 && activeBlocks.Peek().Name == local && activeBlocks.Peek().Start == opened.Start)
                {
                    var active = activeBlocks.Pop();
                    var end = tagEnd + 1;
                    var span = xml.AsSpan(active.Start, end - active.Start);
                    blocks.Add(new(local, active.Start, end, new RawSliceRef(partUri, active.Start, end, SafeXml.Sha256(span), RawSliceKind.XmlElement)));
                }
                if (local == "body" && stack.Count == bodyDepth - 1) bodyEnd = cursor;
            }
            else
            {
                stack.Push((local, cursor));
                if (local == "body") bodyDepth = stack.Count;
                if (bodyDepth > 0 && stack.Count == bodyDepth + 1 && local is "p" or "tbl") activeBlocks.Push((local, cursor));
                if (selfClosing)
                {
                    stack.Pop();
                    if (local == "body") bodyEnd = cursor;
                }
            }
            cursor = tagEnd + 1;
        }
        if (stack.Count != 0 || bodyEnd < 0) throw new InvalidDataException("Word document has no balanced body element.");
        return new(blocks.OrderBy(item => item.Start).ToArray(), bodyEnd);
    }

    private static bool IsNameByte(byte value) => value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)':' or (byte)'_' or (byte)'-' or (byte)'.';
    private static string LocalName(byte[] xml, int start, int end)
    {
        var colon = Array.LastIndexOf(xml, (byte)':', end - 1, end - start);
        return System.Text.Encoding.UTF8.GetString(xml, colon >= start ? colon + 1 : start, end - (colon >= start ? colon + 1 : start));
    }
    private static bool Starts(byte[] value, int start, ReadOnlySpan<byte> prefix) => start + prefix.Length <= value.Length && value.AsSpan(start, prefix.Length).SequenceEqual(prefix);
    private static int FindTerminator(byte[] value, int start, ReadOnlySpan<byte> terminator)
    {
        for (var i = start; i + terminator.Length <= value.Length; i++) if (Starts(value, i, terminator)) return i + terminator.Length;
        throw new InvalidDataException("Unterminated XML special section.");
    }
    private static int FindTagEnd(byte[] value, int start)
    {
        byte quote = 0;
        for (var i = start; i < value.Length; i++)
        {
            if (quote != 0) { if (value[i] == quote) quote = 0; continue; }
            if (value[i] is (byte)'\'' or (byte)'\"') { quote = value[i]; continue; }
            if (value[i] == (byte)'>') return i;
        }
        throw new InvalidDataException("Unterminated XML tag.");
    }
    private static bool IsSelfClosing(byte[] value, int nameEnd, int tagEnd)
    {
        for (var i = tagEnd - 1; i >= nameEnd; i--) { if (char.IsWhiteSpace((char)value[i])) continue; return value[i] == (byte)'/'; }
        return false;
    }
}
