using DocRedock.Core.Documents;

namespace DocRedock.Formats.OpenXml.Common;

/// <summary>
/// Byte-level XML scanner used only for slice boundaries. Semantic processing uses SafeXml;
/// this scanner never uses a regex and respects comments, CDATA, processing instructions and quotes.
/// </summary>
internal static class XmlSliceScanner
{
    /// <summary>One body-position block. <paramref name="AlternateContent"/> is the zero-based
    /// index of the *body-level* mc:AlternateContent this block lives in (-1 when it lives directly
    /// in the body), and <paramref name="Branch"/> names the path of branches inside it -
    /// "Choice#0", "Fallback", and for a fork nested in a branch "Choice#0/Fallback",
    /// "Fallback/Choice#1", ... - so the adapter can hand the slices of the branch it selected to
    /// the blocks it projected and keep the other branches' slices for a mirrored rewrite. A nested
    /// fork keeps the outer ordinal and is told apart by the deeper path alone.</summary>
    internal sealed record BlockSlice(string LocalName, int Start, int End, RawSliceRef Reference,
        int AlternateContent = -1, string? Branch = null);

    /// <summary>One w:txbxContent - the editable body of a text box - wherever under w:body it
    /// sits. <paramref name="HostBlockStart"/> is the Start of the nearest enclosing recorded
    /// block (its <see cref="BlockSlice"/>, so a splice of the box and a splice of its host are
    /// recognisably the same region), <paramref name="OrdinalInHost"/> its zero-based position
    /// among that host's text boxes in document order, and <paramref name="InlineAlternatePath"/>
    /// the paragraph-level fork it came out of - "&lt;ordinal&gt;:Choice#0", "&lt;ordinal&gt;:Fallback",
    /// where the ordinal counts mc:AlternateContent elements inside the host in document order -
    /// or null when the box sits outside every such fork. A text box inside a text box is not
    /// recorded: only the outer one is addressable.</summary>
    internal sealed record TextBoxSlice(int Start, int End, RawSliceRef Reference,
        int HostBlockStart, int OrdinalInHost, string? InlineAlternatePath);

    internal sealed record BodySlices(IReadOnlyList<BlockSlice> Blocks, int BodyEndTagStart,
        IReadOnlyList<TextBoxSlice> TextBoxes);

    /// <summary>What a body position can hold: a paragraph, a table, or a block-level equation.
    /// Anything else there (a sectPr, a bookmark) projects no editable node and needs no slice.</summary>
    private static bool IsBlockName(string localName) => localName is "p" or "tbl" or "oMathPara" or "oMath";

    /// <summary>Elements that wrap body content without being content: a block content control and
    /// its content holder. The blocks they hold still occupy body positions, so they are sliced at
    /// their own boundaries, and a control nested inside a control keeps that property. Every other
    /// container stays opaque, so nothing inside it is ever addressed by a splice - except a
    /// body-level mc:AlternateContent, whose mc:Choice / mc:Fallback branches are transparent in
    /// the same way but tag what they hold with the branch it came from (see BlockSlice).</summary>
    private static bool IsTransparentWrapper(string localName) => localName is "sdt" or "sdtContent";

    public static BodySlices FindWordBodyBlocks(byte[] xml, string partUri)
    {
        var stack = new Stack<(string LocalName, int Start, bool Transparent, int Alternate, string? Branch,
            bool AlternateHost, int InlineAlternate, string? InlinePath)>();
        var blocks = new List<BlockSlice>();
        var textBoxes = new List<TextBoxSlice>();
        var bodyDepth = -1;
        var activeBlocks = new Stack<(string Name, int Start)>();
        var bodyEnd = -1;
        var alternateOrdinal = 0;
        var branchCounts = new Dictionary<int, int>();
        var inlineAlternateCounts = new Dictionary<int, int>();
        var textBoxCounts = new Dictionary<int, int>();
        // Only the outermost w:txbxContent is addressable, so a box opened inside one is counted
        // for nesting depth alone and never recorded.
        var textBoxDepth = 0;
        (int Start, int HostStart, int Ordinal, string? Path)? pendingTextBox = null;
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
                    activeBlocks.Pop();
                    Record(local, opened.Start, tagEnd + 1, opened.Alternate, opened.Branch);
                }
                if (local == "txbxContent") CloseTextBox(opened.Start, tagEnd + 1);
                if (local == "body" && stack.Count == bodyDepth - 1) bodyEnd = cursor;
            }
            else
            {
                // A body position is not only a direct child of w:body: a block content control
                // wraps body content without being content itself, so the blocks under its
                // w:sdtContent are recorded at their own boundaries and an edit inside a control
                // splices only that block, leaving the w:sdt/w:sdtPr wrapping byte-identical.
                var parent = stack.Count > 0 ? stack.Peek() : default;
                var parentTransparent = stack.Count > 0 && parent.Transparent;
                var isBlock = parentTransparent && IsBlockName(local);
                var transparent = false;
                var alternate = parentTransparent ? parent.Alternate : -1;
                var branch = parentTransparent ? parent.Branch : null;
                var alternateHost = false;
                var inlineAlternate = -1;
                // The paragraph-level fork a descendant sits in is inherited from wherever it was
                // last set, so the innermost mc:Choice / mc:Fallback wins.
                var inlinePath = stack.Count > 0 ? parent.InlinePath : null;
                if (local == "body") { transparent = true; alternate = -1; branch = null; }
                else if (parentTransparent && IsTransparentWrapper(local)) transparent = true;
                // A body-level mc:AlternateContent is a fork in the body, not content: each branch
                // holds blocks that would sit at this body position had that branch been chosen.
                // Slicing every branch lets an edit to the selected one be mirrored into the
                // others. A fork nested inside a branch is the same shape one level down: it keeps
                // the outer ordinal - the ordinal counts body positions, not forks - and its own
                // branches extend the path, so "Choice#0/Fallback" is the fallback of the fork that
                // stands in the first choice of body fork #N.
                else if (parentTransparent && local == "AlternateContent")
                {
                    alternateHost = true;
                    if (parent.Alternate < 0) { alternate = alternateOrdinal++; branch = null; }
                    else { alternate = parent.Alternate; branch = parent.Branch; }
                }
                else if (stack.Count > 0 && parent.AlternateHost && local is "Choice" or "Fallback")
                {
                    transparent = true;
                    alternate = parent.Alternate;
                    var label = local == "Fallback" ? "Fallback" : "Choice#" + NextChoice(parent.Start);
                    branch = parent.Branch is null ? label : parent.Branch + "/" + label;
                }
                // A fork inside a block (the usual DrawingML-or-VML shape alternative) forks
                // inline content, not body positions: it claims no block slice, but a text box in
                // one of its branches is still addressable, so the branch it came from is recorded
                // on the box instead.
                else if (local == "AlternateContent")
                    inlineAlternate = NextInlineAlternate(activeBlocks.Count > 0 ? activeBlocks.Peek().Start : -1);
                else if (stack.Count > 0 && parent.InlineAlternate >= 0 && local is "Choice" or "Fallback")
                    inlinePath = parent.InlineAlternate.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                        (local == "Fallback" ? "Fallback" : "Choice#" + NextChoice(parent.Start));
                if (local == "txbxContent") OpenTextBox(cursor, inlinePath);
                stack.Push((local, cursor, transparent, alternate, branch, alternateHost, inlineAlternate, inlinePath));
                if (local == "body") bodyDepth = stack.Count;
                if (isBlock) activeBlocks.Push((local, cursor));
                if (selfClosing)
                {
                    stack.Pop();
                    // An empty block serialized as <w:p/> closes at its own tag. Recording it here
                    // keeps the ledger in step with the elements the XML projection walks; leaving
                    // it open used to swallow the *next* block's slice as well.
                    if (isBlock) { activeBlocks.Pop(); Record(local, cursor, tagEnd + 1, alternate, branch); }
                    if (local == "txbxContent") CloseTextBox(cursor, tagEnd + 1);
                    if (local == "body") bodyEnd = cursor;
                }
            }
            cursor = tagEnd + 1;
        }
        if (stack.Count != 0 || bodyEnd < 0) throw new InvalidDataException("Word document has no balanced body element.");
        return new(blocks.OrderBy(item => item.Start).ToArray(), bodyEnd,
            textBoxes.OrderBy(item => item.Start).ToArray());

        void Record(string blockName, int blockStart, int blockEnd, int alternate, string? branch) =>
            blocks.Add(new(blockName, blockStart, blockEnd,
                new RawSliceRef(partUri, blockStart, blockEnd, SafeXml.Sha256(xml.AsSpan(blockStart, blockEnd - blockStart)), RawSliceKind.XmlElement),
                alternate, branch));

        void OpenTextBox(int boxStart, string? path)
        {
            if (textBoxDepth == 0)
            {
                var hostStart = activeBlocks.Count > 0 ? activeBlocks.Peek().Start : -1;
                pendingTextBox = (boxStart, hostStart, NextTextBox(hostStart), path);
            }
            textBoxDepth++;
        }

        void CloseTextBox(int boxStart, int boxEnd)
        {
            textBoxDepth--;
            if (textBoxDepth != 0 || pendingTextBox is not { } pending || pending.Start != boxStart) return;
            textBoxes.Add(new(boxStart, boxEnd,
                new RawSliceRef(partUri, boxStart, boxEnd, SafeXml.Sha256(xml.AsSpan(boxStart, boxEnd - boxStart)), RawSliceKind.XmlElement),
                pending.HostStart, pending.Ordinal, pending.Path));
            pendingTextBox = null;
        }

        int NextChoice(int hostStart)
        {
            var next = branchCounts.TryGetValue(hostStart, out var current) ? current : 0;
            branchCounts[hostStart] = next + 1;
            return next;
        }

        int NextInlineAlternate(int hostStart)
        {
            var next = inlineAlternateCounts.TryGetValue(hostStart, out var current) ? current : 0;
            inlineAlternateCounts[hostStart] = next + 1;
            return next;
        }

        int NextTextBox(int hostStart)
        {
            var next = textBoxCounts.TryGetValue(hostStart, out var current) ? current : 0;
            textBoxCounts[hostStart] = next + 1;
            return next;
        }
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
