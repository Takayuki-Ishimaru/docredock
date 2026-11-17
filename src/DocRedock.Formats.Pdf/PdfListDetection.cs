using System.Text;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.Pdf;

/// <summary>Where a line sits in a list: its nesting level (0 for the outermost), whether the item is numbered and
/// which number it carries.</summary>
public sealed record PdfListInfo(int Level, bool Ordered, int Number);

/// <summary>One block of a page's flow: a region as it is, or - for an item of a list - the region with its marker taken
/// off and the lines that wrap under it joined on. <paramref name="FirstIndex"/> and <paramref name="Count"/> are the
/// positions of the flow regions the block stands for.</summary>
public sealed record PdfFlowBlock(PdfTextRegion Region, PdfListInfo? List, int FirstIndex, int Count);

/// <summary>Finds the lists of a page from its lines alone: a marker that is text (a bullet glyph, a number, a letter), or a
/// small filled or stroked shape drawn just left of the first glyph of the line (a browser draws its bullets so), in a run
/// of lines that follow each other. The nesting comes from how far the marker is indented, and a line without a marker that
/// is indented to the text of the item above is the rest of that item.</summary>
public static class PdfListDetection
{
    private enum MarkerKind { None, Number, Alpha, Bullet, Dash, Vector }

    private sealed class Candidate
    {
        public MarkerKind Kind;
        public string Style = "";
        public int Value;
        public int Strip;
        public double MarkerX;
        public double TextX;
        public bool IsOrdered => Kind is MarkerKind.Number or MarkerKind.Alpha;
    }

    private sealed class Run
    {
        public readonly List<int> Members = [];
    }

    // The bullets a line may start with. A space has to follow them, except after the middle dots of Japanese text.
    private const string BulletGlyphs = "•‣◦⁃▪▫●○■□◆◇・･∙";
    private const string TightGlyphs = "・･";
    // A mark that is a bullet only when two lines in a row start with it.
    private const string DashMarks = "-–—−*+";

    public static IReadOnlyList<PdfFlowBlock> Group(PdfPageText page, IReadOnlyList<PdfTextRegion> flow,
        IReadOnlyList<VisualPath>? shapes = null, Func<PdfTextRegion, bool>? exclude = null, IReadOnlyList<PdfTable>? tables = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(flow);
        var candidates = new Candidate?[flow.Count];
        var any = false;
        var small = shapes is { Count: > 0 } ? SmallShapes(shapes) : [];
        for (var i = 0; i < flow.Count; i++)
        {
            var region = flow[i];
            if (region.IsPageFurniture || region.FontSize is not > 0 || region.Text.Length == 0 || exclude?.Invoke(region) == true) continue;
            var candidate = ParseText(region) ?? FindVectorBullet(region, flow, i, small);
            if (candidate is null || PdfPageProjection.HeadingLevel(page, region, tables) > 0) continue;
            candidates[i] = candidate;
            any = true;
        }
        if (!any) return Plain(flow);

        List<Run> runs = [];
        for (var pass = 0; pass < 4; pass++)
        {
            runs = Segment(flow, candidates);
            if (!Validate(flow, candidates, runs)) break;
        }
        var blocks = new List<PdfFlowBlock>(flow.Count);
        var covered = new bool[flow.Count];
        foreach (var run in runs.Where(run => run.Members.Any(member => candidates[member] is not null)))
        {
            blocks.AddRange(Build(flow, candidates, run));
            foreach (var member in run.Members) covered[member] = true;
        }
        for (var i = 0; i < flow.Count; i++)
            if (!covered[i]) blocks.Add(new PdfFlowBlock(flow[i], null, i, 1));
        return blocks.OrderBy(block => block.FirstIndex).ToArray();
    }

    private static IReadOnlyList<PdfFlowBlock> Plain(IReadOnlyList<PdfTextRegion> flow) =>
        flow.Select((region, index) => new PdfFlowBlock(region, null, index, 1)).ToArray();

    // ---- markers -------------------------------------------------------------------------------------------------

    private static Candidate? ParseText(PdfTextRegion region)
    {
        var text = region.Text;
        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        if (start >= text.Length) return null;
        var c = text[start];
        MarkerKind kind;
        string style;
        var value = 0;
        int end;
        bool needSpace = true;
        if (BulletGlyphs.Contains(c) || c is >= '' and <= '')
        {
            kind = MarkerKind.Bullet; style = "bullet"; end = start + 1;
            needSpace = !TightGlyphs.Contains(c);
        }
        else if (DashMarks.Contains(c))
        {
            kind = MarkerKind.Dash; style = "dash" + c; end = start + 1;
        }
        else if (c is >= '①' and <= '⑳')
        {
            kind = MarkerKind.Number; style = "circled"; value = c - '①' + 1; end = start + 1; needSpace = false;
        }
        else if (char.IsAsciiDigit(c))
        {
            var length = 1;
            while (start + length < text.Length && length < 3 && char.IsAsciiDigit(text[start + length])) length++;
            if (start + length >= text.Length || text[start + length] is not ('.' or ')' or '．' or '）')) return null;
            kind = MarkerKind.Number; style = text[start + length] is '.' or '．' ? "n." : "n)";
            value = int.Parse(text.AsSpan(start, length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
            end = start + length + 1;
        }
        else if (c is '(' or '（')
        {
            var length = 1;
            while (start + length < text.Length && length <= 3 && char.IsAsciiDigit(text[start + length])) length++;
            if (length == 1 || start + length >= text.Length || text[start + length] is not (')' or '）')) return null;
            kind = MarkerKind.Number; style = "(n)";
            value = int.Parse(text.AsSpan(start + 1, length - 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
            end = start + length + 1;
        }
        else if (char.IsAsciiLetter(c) && start + 1 < text.Length && text[start + 1] is '.' or ')')
        {
            kind = MarkerKind.Alpha; style = (char.IsAsciiLetterUpper(c) ? "A" : "a") + text[start + 1];
            value = char.ToLowerInvariant(c) - 'a' + 1; end = start + 2;
        }
        else return null;

        var afterMarker = end;
        while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
        if (end >= text.Length || needSpace && end == afterMarker) return null;
        // The text of the item has to read as text, not as more marker (a row of dots, "1.2", "3.-").
        if (kind is MarkerKind.Number or MarkerKind.Alpha && char.IsAsciiDigit(text[end]) && text[afterMarker - 1] is '.' or '．') return null;
        var size = region.FontSize!.Value;
        var left = region.BoundingBox.X;
        var textX = TextStart(region, end) ?? left + (end - start) * 0.5 * size;
        return new Candidate { Kind = kind, Style = style, Value = value, Strip = end, MarkerX = left, TextX = textX };
    }

    /// <summary>Where the character at <paramref name="index"/> is drawn, when the font's glyph widths place it.</summary>
    private static double? TextStart(PdfTextRegion region, int index) =>
        region.Glyphs is { } glyphs && glyphs.Count == region.Text.Length && index < glyphs.Count ? glyphs[index].Left : null;

    private static (VisualPath Path, Geometry Box)[] SmallShapes(IReadOnlyList<VisualPath> shapes)
    {
        var found = new List<(VisualPath, Geometry)>();
        foreach (var path in shapes)
        {
            if (path.Geometry is not { } box || path.IsFilled != true && path.IsStroked != true) continue;
            if (box.Width < 1.2 || box.Height < 1.2 || box.Width > 14 || box.Height > 14) continue;
            var aspect = box.Width / box.Height;
            if (aspect < 0.6 || aspect > 1.6) continue;
            found.Add((path, box));
        }
        return found.ToArray();
    }

    /// <summary>The small shape just left of the first glyph of a line and level with the middle of its lower-case letters,
    /// which is how a browser draws the disc, circle or square of a bulleted list.</summary>
    private static Candidate? FindVectorBullet(PdfTextRegion region, IReadOnlyList<PdfTextRegion> flow, int index,
        (VisualPath Path, Geometry Box)[] small)
    {
        if (small.Length == 0) return null;
        var size = region.FontSize!.Value;
        Geometry? best = null;
        foreach (var (_, box) in small)
        {
            if (box.Width > size * 0.55 || box.Height > size * 0.55 || box.Width < size * 0.12) continue;
            var gap = region.BoundingBox.X - (box.X + box.Width);
            if (gap < size * 0.1 || gap > size * 2.5) continue;
            var middle = box.Y + box.Height / 2 - region.BoundingBox.Y;
            if (middle < size * 0.15 || middle > size * 0.8) continue;
            if (best is null || box.X > best.X) best = box;
        }
        if (best is null) return null;
        // Nothing else may be drawn between the shape and the line: then it is a leader or part of a drawing.
        var right = region.BoundingBox.X;
        for (var i = 0; i < flow.Count; i++)
        {
            if (i == index) continue;
            var other = flow[i];
            if (Math.Abs(other.BoundingBox.Y - region.BoundingBox.Y) > size * 0.5) continue;
            if (other.BoundingBox.X < right && other.BoundingBox.X + other.BoundingBox.Width > best.X) return null;
        }
        return new Candidate { Kind = MarkerKind.Vector, Style = "vector", Strip = 0, MarkerX = best.X, TextX = region.BoundingBox.X };
    }

    // ---- runs ----------------------------------------------------------------------------------------------------

    private static List<Run> Segment(IReadOnlyList<PdfTextRegion> flow, Candidate?[] candidates)
    {
        var runs = new List<Run>();
        Run? run = null;
        PdfTextRegion? last = null;
        Candidate? item = null;
        for (var i = 0; i < flow.Count; i++)
        {
            var region = flow[i];
            if (region.IsPageFurniture) continue;
            var size = region.FontSize ?? region.BoundingBox.Height;
            if (candidates[i] is { } candidate)
            {
                if (run is not null && last is not null)
                {
                    var drop = last.BoundingBox.Y - region.BoundingBox.Y;
                    if (drop < Math.Min(size, last.FontSize ?? size) * 0.3 || drop > Math.Max(size, last.FontSize ?? size) * 3.2) run = null;
                }
                if (run is null) { run = new Run(); runs.Add(run); }
                run.Members.Add(i);
                item = candidate;
                last = region;
                continue;
            }
            if (run is not null && last is not null && item is not null && IsContinuation(region, last, item))
            {
                run.Members.Add(i);
                last = region;
                continue;
            }
            run = null; last = null; item = null;
        }
        return runs;
    }

    private static bool IsContinuation(PdfTextRegion region, PdfTextRegion previous, Candidate item)
    {
        if (region.FontSize is not > 0 || previous.FontSize is not > 0 || region.Text.Length == 0) return false;
        var size = region.FontSize.Value;
        if (Math.Abs(size - previous.FontSize.Value) > 0.12 * size) return false;
        var drop = previous.BoundingBox.Y - region.BoundingBox.Y;
        if (drop < size * 0.6 || drop > size * 1.9) return false;
        return Math.Abs(region.BoundingBox.X - item.TextX) <= size * 0.6;
    }

    /// <summary>Takes off the candidates that no list backs: a number with no neighbour in the sequence, a dash or a shape
    /// with no second one. Returns whether any went (the runs are then cut again).</summary>
    private static bool Validate(IReadOnlyList<PdfTextRegion> flow, Candidate?[] candidates, List<Run> runs)
    {
        var changed = false;
        foreach (var run in runs)
        {
            var items = run.Members.Where(member => candidates[member] is not null).ToArray();
            if (items.Length == 0) continue;
            var vectors = items.Count(member => candidates[member]!.Kind == MarkerKind.Vector);
            foreach (var member in items)
            {
                var candidate = candidates[member]!;
                var tolerance = Math.Max(4, 0.9 * flow[member].FontSize!.Value);
                bool Peer(int other) => candidates[other] is { } peer && other != member && peer.Style == candidate.Style &&
                    Math.Abs(peer.MarkerX - candidate.MarkerX) <= tolerance;
                var valid = candidate.Kind switch
                {
                    MarkerKind.Bullet => true,
                    MarkerKind.Vector => vectors >= 2,
                    MarkerKind.Dash => items.Count(Peer) >= 1,
                    _ => items.Any(other => Peer(other) && (other < member && candidates[other]!.Value == candidate.Value - 1 ||
                        other > member && candidates[other]!.Value == candidate.Value + 1)),
                };
                if (valid) continue;
                candidates[member] = null;
                changed = true;
            }
        }
        return changed;
    }

    // ---- blocks --------------------------------------------------------------------------------------------------

    private static IEnumerable<PdfFlowBlock> Build(IReadOnlyList<PdfTextRegion> flow, Candidate?[] candidates, Run run)
    {
        var blocks = new List<PdfFlowBlock>();
        var levels = new List<double>();
        PdfTextRegion? openRegion = null;
        PdfListInfo? openInfo = null;
        var openFirst = 0;
        var openCount = 0;
        void Close()
        {
            if (openRegion is not null && openInfo is not null) blocks.Add(new PdfFlowBlock(openRegion, openInfo, openFirst, openCount));
            openRegion = null; openInfo = null;
        }
        foreach (var member in run.Members)
        {
            var region = flow[member];
            if (candidates[member] is not { } candidate)
            {
                if (openRegion is null) { blocks.Add(new PdfFlowBlock(region, null, member, 1)); continue; }
                openRegion = Append(openRegion, region);
                openCount = member - openFirst + 1;
                continue;
            }
            Close();
            var size = region.FontSize!.Value;
            var level = LevelOf(levels, candidate.MarkerX, Math.Max(4, 0.9 * size));
            openRegion = Strip(region, candidate.Strip);
            openInfo = new PdfListInfo(level, candidate.IsOrdered, candidate.IsOrdered ? candidate.Value : 0);
            openFirst = member;
            openCount = 1;
        }
        Close();
        return blocks;
    }

    private static int LevelOf(List<double> levels, double x, double tolerance)
    {
        for (var k = levels.Count - 1; k >= 0; k--)
        {
            if (Math.Abs(levels[k] - x) > tolerance) continue;
            levels.RemoveRange(k + 1, levels.Count - k - 1);
            return k;
        }
        var parent = levels.FindLastIndex(level => level < x - tolerance);
        levels.RemoveRange(parent + 1, levels.Count - parent - 1);
        levels.Add(x);
        return parent + 1;
    }

    private static PdfTextRegion Strip(PdfTextRegion region, int strip)
    {
        var text = region.Text.Replace('\n', ' ');
        var tail = text.TrimEnd();
        return region with
        {
            Text = tail[strip..],
            Struck = Cut(region.Struck, strip, tail.Length),
            Underlined = Cut(region.Underlined, strip, tail.Length),
            Glyphs = null,
        };
    }

    private static PdfTextRegion Append(PdfTextRegion item, PdfTextRegion rest)
    {
        var before = item.Text;
        var restText = rest.Text.Replace('\n', ' ');
        var after = restText.TrimStart();
        var leading = restText.Length - after.Length;
        after = after.TrimEnd();
        var separator = Separator(before, after);
        var start = before.Length + separator.Length;
        var offset = start - leading;
        var left = Math.Min(item.BoundingBox.X, rest.BoundingBox.X);
        var right = Math.Max(item.BoundingBox.X + item.BoundingBox.Width, rest.BoundingBox.X + rest.BoundingBox.Width);
        var bottom = Math.Min(item.BoundingBox.Y, rest.BoundingBox.Y);
        var top = Math.Max(item.BoundingBox.Y + item.BoundingBox.Height, rest.BoundingBox.Y + rest.BoundingBox.Height);
        return item with
        {
            Text = before + separator + after,
            BoundingBox = item.BoundingBox with { X = left, Y = item.BoundingBox.Y, Width = right - left, Height = Math.Max(item.BoundingBox.Height, top - item.BoundingBox.Y) },
            SourceIds = [.. item.SourceTextIds, .. rest.SourceTextIds],
            ContinuesBelow = rest.ContinuesBelow,
            ContinuesOnNextPage = rest.ContinuesOnNextPage,
            HasUncertainHyphenation = item.HasUncertainHyphenation || rest.HasUncertainHyphenation,
            Struck = PdfTextDecorations.Join(item.Struck, Clip(PdfTextDecorations.Shift(rest.Struck, offset), start, start + after.Length)),
            Underlined = PdfTextDecorations.Join(item.Underlined, Clip(PdfTextDecorations.Shift(rest.Underlined, offset), start, start + after.Length)),
            Glyphs = null,
        };
    }

    /// <summary>The ranges that remain of <paramref name="ranges"/> once the first <paramref name="strip"/> characters are
    /// taken off a text that ends at <paramref name="length"/>.</summary>
    private static IReadOnlyList<TextRange>? Cut(IReadOnlyList<TextRange>? ranges, int strip, int length)
    {
        if (ranges is not { Count: > 0 }) return null;
        var cut = ranges.Where(range => range.End > strip && range.Start < length)
            .Select(range => new TextRange(Math.Max(range.Start, strip) - strip, Math.Min(range.End, length) - strip)).ToArray();
        return cut.Length == 0 ? null : cut;
    }

    private static IReadOnlyList<TextRange>? Clip(IReadOnlyList<TextRange>? ranges, int from, int to)
    {
        if (ranges is not { Count: > 0 }) return null;
        var cut = ranges.Where(range => range.End > from && range.Start < to)
            .Select(range => new TextRange(Math.Max(range.Start, from), Math.Min(range.End, to))).ToArray();
        return cut.Length == 0 ? null : cut;
    }

    private static string Separator(string before, string after)
    {
        if (before.Length == 0 || after.Length == 0) return string.Empty;
        char last = before[^1], first = after[0];
        if (char.IsWhiteSpace(last) || char.IsWhiteSpace(first)) return string.Empty;
        if (Rune.DecodeLastFromUtf16(before, out var lastRune, out _) != System.Buffers.OperationStatus.Done ||
            Rune.DecodeFromUtf16(after, out var firstRune, out _) != System.Buffers.OperationStatus.Done ||
            PdfTextRuns.IsAsian(lastRune) || PdfTextRuns.IsAsian(firstRune)) return string.Empty;
        if (".,;:!?)]}%…”’".Contains(first) || "([{/\\-–—_~#@$“‘".Contains(last)) return string.Empty;
        return " ";
    }
}
