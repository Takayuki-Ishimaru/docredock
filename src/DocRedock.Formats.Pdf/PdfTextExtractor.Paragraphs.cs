using DocRedock.Core.Documents;

namespace DocRedock.Formats.Pdf;

public static partial class PdfTextExtractor
{
    // Reuse the measured wrapping rules for unframed body text after reading order is known.
    // A reversal of baseline, a table, a diagram label, or an already folded paragraph is a barrier.
    private static List<PdfTextRegion> JoinFlowParagraphs(List<PdfTextRegion> lines,
        PdfTextRegion[] fragments, IReadOnlySet<int>? tableSourceIds, IReadOnlySet<int>? isolated)
    {
        var byId = fragments.ToDictionary(fragment => fragment.SourceTextIds[0]);
        var output = new List<PdfTextRegion>();
        var run = new List<PdfTextRegion>();
        var position = 0;
        void Flush()
        {
            if (run.Count > 1 && !IsFixedPitch(run.SelectMany(line => line.SourceTextIds).Select(id => byId[id])))
                output.AddRange(JoinWrappedLines(new BoxColumn(default, []) { Lines = [.. run] }, byId, EndsColumn()).Lines);
            else output.AddRange(run);
            run.Clear();
        }
        // Whether the run is the last text of its column: below it in the column lies at most a short line some way off,
        // a running footer or a page number.
        bool EndsColumn()
        {
            var last = run[^1];
            var size = last.FontSize ?? last.BoundingBox.Height;
            return !lines.Skip(position).Any(line => line.ReadingColumn == last.ReadingColumn && line.BoundingBox.Y < last.BoundingBox.Y &&
                !(line.Text.Trim().Length <= MaxFurnitureCharacters && last.BoundingBox.Y - line.BoundingBox.Y > size * MaxLeadingEm));
        }
        for (; position < lines.Count; position++)
        {
            var line = lines[position];
            var parts = line.SourceTextIds.Select(id => byId.GetValueOrDefault(id)).ToArray();
            if (PdfTextAccounting.IsTableOwned(line, tableSourceIds) ||
                line.SourceTextIds.Any(id => isolated?.Contains(id) == true) ||
                parts.Any(part => part is not { MeasuredWidth: true, FontSize: > 0 }) ||
                parts.Max(part => part!.BoundingBox.Y) - parts.Min(part => part!.BoundingBox.Y) > line.BoundingBox.Height * 0.5)
            {
                Flush(); output.Add(line); continue;
            }
            if (run.Count > 0)
            {
                var previous = run[^1];
                var gap = previous.BoundingBox.Y - line.BoundingBox.Y;
                var size = parts.Max(part => part!.FontSize!.Value);
                if (previous.ReadingColumn != line.ReadingColumn || gap < size * 0.6 || gap > size * MaxLeadingEm ||
                    Math.Abs(previous.BoundingBox.X - line.BoundingBox.X) > size * 2)
                    Flush();
            }
            run.Add(line);
        }
        Flush();
        return output;
    }

    /// <summary>Whether <paramref name="below"/>, at the head of a column or a page, continues the paragraph that
    /// <paramref name="above"/> ends the column or the page before with: that one runs full to the foot of its column
    /// without closing a sentence, and this one is text of the same size that neither starts indented nor with a
    /// bullet or a number.</summary>
    internal static bool ContinuesInto(PdfTextRegion above, PdfTextRegion below)
    {
        if (!above.ContinuesBelow || below.StartsIndented || above.IsPageFurniture || below.IsPageFurniture) return false;
        if (above.FontSize is not > 0 || below.FontSize is not > 0 || !SimilarSize(above.FontSize.Value, below.FontSize.Value)) return false;
        var end = above.Text.TrimEnd();
        var start = below.Text.TrimStart();
        return end.Length > 0 && start.Length > 0 && !SentenceMarks.Contains(end[^1]) && ItemMarkerLength(start) == 0;
    }

    /// <summary>One paragraph of <paramref name="above"/> and the rest of it, <paramref name="below"/>. It keeps the
    /// place of its start.</summary>
    internal static PdfTextRegion JoinContinuation(PdfTextRegion above, PdfTextRegion below)
    {
        var before = above.Text.TrimEnd();
        var after = below.Text.TrimStart();
        var leading = below.Text.Length - after.Length;
        var uncertainHyphen = before.Length >= 2 && before[^1] == '-' && char.IsLetter(before[^2]) && char.IsLower(after[0]);
        var separator = JoinSeparator(before, after);
        var offset = before.Length + separator.Length - leading;
        return above with
        {
            Text = before + separator + after,
            SourceIds = [.. above.SourceTextIds, .. below.SourceTextIds],
            ContinuesBelow = below.ContinuesBelow,
            HasUncertainHyphenation = above.HasUncertainHyphenation || below.HasUncertainHyphenation || uncertainHyphen,
            Struck = PdfTextDecorations.Join(Clip(above.Struck, before.Length), Clip(PdfTextDecorations.Shift(below.Struck, offset), int.MaxValue, offset + leading)),
            Underlined = PdfTextDecorations.Join(Clip(above.Underlined, before.Length), Clip(PdfTextDecorations.Shift(below.Underlined, offset), int.MaxValue, offset + leading)),
        };

        static IReadOnlyList<TextRange>? Clip(IReadOnlyList<TextRange>? ranges, int length, int from = 0) => ranges?
            .Where(range => range.Start < length && range.End > from)
            .Select(range => new TextRange(Math.Max(range.Start, from), Math.Min(range.End, length))).ToArray();
    }
}
