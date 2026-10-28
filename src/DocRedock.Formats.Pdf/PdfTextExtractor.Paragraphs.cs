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
        void Flush()
        {
            if (run.Count > 1 && !IsFixedPitch(run.SelectMany(line => line.SourceTextIds).Select(id => byId[id])))
                output.AddRange(JoinWrappedLines(new BoxColumn(default, []) { Lines = [.. run] }, byId).Lines);
            else output.AddRange(run);
            run.Clear();
        }
        foreach (var line in lines)
        {
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
                if (gap < size * 0.6 || gap > size * MaxLeadingEm ||
                    Math.Abs(previous.BoundingBox.X - line.BoundingBox.X) > size * 2)
                    Flush();
            }
            run.Add(line);
        }
        Flush();
        return output;
    }
}
