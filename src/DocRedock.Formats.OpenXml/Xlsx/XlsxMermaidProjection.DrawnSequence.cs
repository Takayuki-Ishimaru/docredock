using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocRedock.Formats.OpenXml.Xlsx;

// A sequence diagram drawn as DrawingML shapes: participant boxes in a row, a (usually dashed) vertical
// line under each of them, horizontal arrows between the lines with the message text in a nearby text box,
// narrow rectangles for activations and labelled rectangles for alt/opt/loop frames. Everything here is
// geometry in worksheet EMU; no shape name, colour or text is consulted except the fragment keyword on a
// frame's tab. A sheet that is not clearly such a diagram is left to the other projections.
internal static partial class XlsxMermaidProjection
{
    private static readonly Regex DrawnFragmentPattern = new(
        @"^(?<kind>alt|opt|loop|par|break|critical)\b\s*(?<guard>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private readonly record struct Box(double Left, double Top, double Right, double Bottom)
    {
        public double Width => Right - Left;
        public double Height => Bottom - Top;
        public double CenterX => (Left + Right) / 2d;
        public double CenterY => (Top + Bottom) / 2d;
    }

    private sealed record Shp(XlsxDrawingShapeRecord Shape, Box Box)
    {
        public string Id => Shape.Id;
        public string Text => Shape.Text?.Trim() ?? string.Empty;
    }

    private sealed record DrawnParticipant(Shp Box, Shp Lifeline, double X, int Index)
    {
        public string Alias => "P" + (Index + 1).ToString(CultureInfo.InvariantCulture);
    }

    private sealed record DrawnEvent(double Key, int Priority, double Tie, string Text);

    // A shape's position comes from its anchor cell, resolved with the sheet's own column and row sizes, while the
    // width and height recorded for it can be the exact ones of the drawing. The two differ when the sheet's real
    // grid is not the default one the metrics assume, and the far edge then lands off the lifeline or box it
    // belongs to. For a two-cell anchor the far edge is therefore resolved from its own anchor cell the same way.
    private static Box? BoxOf(XlsxDrawingShapeRecord shape, XlsxWorksheetMetrics? metrics)
    {
        if (shape.AbsoluteBounds is not { } b) return null;
        var right = (double)b.RightEmu;
        var bottom = (double)b.BottomEmu;
        if (metrics is not null && shape.ToColumn is { } toColumn && shape.ToRow is { } toRow &&
            StringComparer.Ordinal.Equals(shape.AnchorKind, "twoCellAnchor"))
        {
            right = metrics.ColumnStartEmu(toColumn) + shape.ToColumnOffset;
            bottom = metrics.RowStartEmu(toRow) + shape.ToRowOffset;
        }
        return new Box(b.XEmu, b.YEmu, Math.Max(b.XEmu, right), Math.Max(b.YEmu, bottom));
    }

    private static bool IsLineLike(XlsxDrawingShapeRecord shape)
    {
        if (shape.IsConnector) return true;
        var geometry = shape.Geometry;
        return geometry.Equals("line", StringComparison.OrdinalIgnoreCase) ||
               geometry.StartsWith("straightConnector", StringComparison.OrdinalIgnoreCase) ||
               geometry.StartsWith("bentConnector", StringComparison.OrdinalIgnoreCase) ||
               geometry.StartsWith("curvedConnector", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBentLine(XlsxDrawingShapeRecord shape) =>
        shape.Geometry.StartsWith("bentConnector", StringComparison.OrdinalIgnoreCase) ||
        shape.Geometry.StartsWith("curvedConnector", StringComparison.OrdinalIgnoreCase);

    private static bool HasArrowhead(string? kind) =>
        !string.IsNullOrWhiteSpace(kind) && !StringComparer.OrdinalIgnoreCase.Equals(kind, "none");

    private static bool IsDashed(string? dash) =>
        !string.IsNullOrWhiteSpace(dash) && !StringComparer.OrdinalIgnoreCase.Equals(dash, "solid");

    private static DiagramProjection? TryCreateDrawnSequence(XlsxWorksheetRecord worksheet)
    {
        var all = (worksheet.DrawingShapes ?? [])
            .Where(shape => !shape.IsHidden && BoxOf(shape, worksheet.Metrics) is not null)
            .Select(shape => new Shp(shape, BoxOf(shape, worksheet.Metrics)!.Value))
            .ToList();
        if (all.Count < 6) return null;
        var lines = all.Where(item => IsLineLike(item.Shape)).ToList();
        var texts = all.Where(item => !IsLineLike(item.Shape) && item.Text.Length > 0).ToList();
        var plain = all.Where(item => !IsLineLike(item.Shape) && item.Text.Length == 0).ToList();
        if (lines.Count < 4 || texts.Count < 3) return null;

        // 1. Lifelines: long, vertical, straight, starting directly under a text box and ending in free space.
        var candidates = new List<DrawnParticipant>();
        var duplicateBottoms = new List<Shp>();
        foreach (var line in lines.Where(item => !IsBentLine(item.Shape) &&
                                                 item.Box.Height >= 900_000 && item.Box.Width <= item.Box.Height * 0.05))
        {
            var x = line.Box.CenterX;
            var owner = texts
                .Where(text => text.Box.Left <= x && text.Box.Right >= x &&
                               line.Box.Height >= text.Box.Height * 2.5 &&
                               line.Box.Top >= text.Box.Top + text.Box.Height * 0.5 &&
                               line.Box.Top <= text.Box.Bottom + Math.Max(60_000, text.Box.Height * 0.3))
                .OrderBy(text => Math.Abs(text.Box.CenterX - x)).FirstOrDefault();
            if (owner is null) continue;
            var endTolerance = Math.Max(60_000, owner.Box.Height * 0.3);
            var atEnd = texts.Where(text => !ReferenceEquals(text, owner) &&
                                            text.Box.Left <= x && text.Box.Right >= x &&
                                            text.Box.Top <= line.Box.Bottom + endTolerance &&
                                            text.Box.Bottom >= line.Box.Bottom - endTolerance).ToArray();
            // A closing copy of the participant box is part of the participant; any other shape at the
            // far end means this line connects two things and is not a lifeline.
            if (atEnd.Any(text => !SameText(text.Text, owner.Text))) continue;
            duplicateBottoms.AddRange(atEnd);
            candidates.Add(new DrawnParticipant(owner, line, x, 0));
        }
        // One participant box per lifeline, boxes aligned in a row.
        candidates = candidates.GroupBy(item => item.Box.Id).Select(group => group.First()).ToList();
        var row = candidates
            .GroupBy(item => Math.Round(item.Box.Box.Top / Math.Max(100_000, item.Box.Box.Height * 0.5)))
            .OrderByDescending(group => group.Count()).FirstOrDefault()?.ToList();
        if (row is null || row.Count < 2) return null;
        var participants = row.OrderBy(item => item.X).Select((item, index) => item with { Index = index }).ToList();
        var spacing = participants.Zip(participants.Skip(1), (left, right) => right.X - left.X).Min();
        if (spacing < 300_000) return null;
        var endpointTolerance = Math.Clamp(spacing * 0.35, 180_000, 600_000);
        var lifelineIds = participants.Select(item => item.Lifeline.Id).ToHashSet(StringComparer.Ordinal);
        var participantTop = participants.Min(item => item.Box.Box.Top);
        var participantBottom = participants.Max(item => item.Box.Box.Bottom);
        var bandTop = participants.Min(item => item.Lifeline.Box.Top);
        var bandBottom = participants.Max(item => item.Lifeline.Box.Bottom);

        var consumed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in participants) { consumed.Add(item.Box.Id); consumed.Add(item.Lifeline.Id); }
        foreach (var item in duplicateBottoms)
            if (participants.Any(p => SameText(p.Box.Text, item.Text))) consumed.Add(item.Id);

        int? Resolve(double x)
        {
            var best = participants.OrderBy(item => Math.Abs(item.X - x)).First();
            return Math.Abs(best.X - x) <= endpointTolerance ? best.Index : null;
        }

        // 2. Frames: a rectangle without text, wide enough to span two lifelines, with a keyword tab at its corner.
        var frameRects = plain.Where(item => !IsLineLike(item.Shape) &&
                                             item.Box.Width >= spacing * 0.8 && item.Box.Height >= 250_000 &&
                                             item.Box.Top >= bandTop - 60_000 && item.Box.Bottom <= bandBottom + 60_000 &&
                                             (item.Shape.Geometry.Equals("rect", StringComparison.OrdinalIgnoreCase) ||
                                              item.Shape.Geometry.Equals("roundRect", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var usedText = new HashSet<string>(StringComparer.Ordinal);
        var claimedLines = new HashSet<string>(StringComparer.Ordinal);
        var events = new List<DrawnEvent>();
        var frameCount = 0;
        foreach (var tab in texts.Where(item => !consumed.Contains(item.Id) &&
                                                DrawnFragmentPattern.IsMatch(item.Text) && item.Box.Top >= participantBottom - 1))
        {
            var tolerance = Math.Max(120_000, tab.Box.Height * 0.5);
            var frame = frameRects
                .Where(rect => tab.Box.Left >= rect.Box.Left - tolerance && tab.Box.Left <= rect.Box.Left + tolerance * 3 &&
                               tab.Box.Top >= rect.Box.Top - tolerance && tab.Box.Top <= rect.Box.Top + tolerance * 3 &&
                               tab.Box.Right <= rect.Box.Right && tab.Box.Bottom <= rect.Box.Bottom)
                .OrderBy(rect => rect.Box.Width * rect.Box.Height).FirstOrDefault();
            if (frame is null) continue;
            var match = DrawnFragmentPattern.Match(tab.Text);
            var kind = match.Groups["kind"].Value.ToLowerInvariant();
            var guard = TrimGuard(match.Groups["guard"].Value);
            usedText.Add(tab.Id);
            if (guard.Length == 0)
            {
                // The guard shares the tab's row: its middle lies within the tab's height.
                var guardText = texts.Where(item => !consumed.Contains(item.Id) && !usedText.Contains(item.Id) &&
                                                    item.Box.CenterY >= tab.Box.Top - tab.Box.Height * 0.25 &&
                                                    item.Box.CenterY <= tab.Box.Bottom + tab.Box.Height * 0.25 &&
                                                    item.Box.Left >= tab.Box.Right - tolerance &&
                                                    item.Box.Left <= frame.Box.Left + frame.Box.Width * 0.7 &&
                                                    item.Box.Right <= frame.Box.Right + tolerance)
                    .OrderBy(item => IsGuard(item.Text) ? 0 : 1).ThenBy(item => item.Box.Left).FirstOrDefault();
                if (guardText is not null)
                {
                    guard = TrimGuard(guardText.Text);
                    usedText.Add(guardText.Id);
                }
            }
            var elseWord = kind switch { "alt" => "else", "par" => "and", "critical" => "option", _ => null };
            var edgeTolerance = Math.Max(120_000, frame.Box.Width * 0.05);
            var area = frame.Box.Width * frame.Box.Height;
            events.Add(new DrawnEvent(frame.Box.Top, 0, -area, kind + (guard.Length == 0 ? string.Empty : " " + Label(guard))));
            if (elseWord is not null)
            {
                foreach (var divider in lines.Where(item => !claimedLines.Contains(item.Id) && !IsBentLine(item.Shape) &&
                                                            item.Box.Height <= Math.Max(15_000, item.Box.Width * 0.02) &&
                                                            Math.Abs(item.Box.Left - frame.Box.Left) <= edgeTolerance &&
                                                            Math.Abs(item.Box.Right - frame.Box.Right) <= edgeTolerance &&
                                                            item.Box.Top >= frame.Box.Top + tab.Box.Height &&
                                                            item.Box.Top <= frame.Box.Bottom - tolerance)
                             .OrderBy(item => item.Box.Top).ToList())
                {
                    claimedLines.Add(divider.Id);
                    consumed.Add(divider.Id);
                    var branch = texts.Where(item => !consumed.Contains(item.Id) && !usedText.Contains(item.Id) &&
                                                     item.Box.Top >= divider.Box.Top - tab.Box.Height * 0.5 &&
                                                     item.Box.Top <= divider.Box.Top + tab.Box.Height * 1.5 &&
                                                     item.Box.Left >= frame.Box.Left - tolerance &&
                                                     item.Box.Left <= frame.Box.Left + frame.Box.Width * 0.5)
                        .OrderBy(item => IsGuard(item.Text) ? 0 : 1).ThenBy(item => Math.Abs(item.Box.Top - divider.Box.Top)).FirstOrDefault();
                    if (branch is not null) usedText.Add(branch.Id);
                    var branchGuard = branch is null ? string.Empty : TrimGuard(branch.Text);
                    events.Add(new DrawnEvent(divider.Box.Top, 2, 0, elseWord + (branchGuard.Length == 0 ? string.Empty : " " + Label(branchGuard))));
                }
            }
            events.Add(new DrawnEvent(frame.Box.Bottom, 9, area, "end"));
            consumed.Add(frame.Id);
            consumed.Add(tab.Id);
            frameRects.Remove(frame);
            frameCount++;
        }
        foreach (var id in usedText) consumed.Add(id);

        // 3. Messages: near-horizontal lines whose two ends sit on two different lifelines; self messages are a
        //    small bent connector whose left edge sits on one lifeline.
        var messageLines = new List<(Shp Line, int From, int To, string Arrow, double Y, bool Self)>();
        foreach (var line in lines.Where(item => !lifelineIds.Contains(item.Id) && !claimedLines.Contains(item.Id)))
        {
            var shape = line.Shape;
            var rotated180 = Math.Abs(Math.Abs(shape.RotationDegrees) % 360 - 180) < 1;
            var flipped = shape.FlipHorizontal ^ rotated180;
            var startX = flipped ? line.Box.Right : line.Box.Left;
            var endX = flipped ? line.Box.Left : line.Box.Right;
            var head = HasArrowhead(shape.HeadArrow);
            var tail = HasArrowhead(shape.TailArrow);
            // This sequence subset cannot express simultaneous arrows at both ends.
            // Let the visual fallback retain the source and report review rather than invent one direction.
            if (head && tail) return null;
            var arrowKind = tail ? shape.TailArrow : shape.HeadArrow;
            var open = StringComparer.OrdinalIgnoreCase.Equals(arrowKind, "arrow") ||
                       StringComparer.OrdinalIgnoreCase.Equals(arrowKind, "open");
            var dashed = IsDashed(shape.LineDash);
            if (IsBentLine(shape) && line.Box.Width <= spacing * 0.6 && line.Box.Height <= 1_000_000 &&
                Resolve(line.Box.Left) is { } selfIndex && line.Box.Top >= bandTop && line.Box.Bottom <= bandBottom + 60_000)
            {
                messageLines.Add((line, selfIndex, selfIndex, ArrowSyntax(dashed, open, head || tail), line.Box.Top, true));
                continue;
            }
            if (IsBentLine(shape) && line.Box.Height > line.Box.Width * 0.35) continue;
            if (line.Box.Height > Math.Max(15_000, line.Box.Width * 0.1) || line.Box.Width < spacing * 0.3) continue;
            if (line.Box.Top < bandTop || line.Box.Top > bandBottom) continue;
            int? fromIndex, toIndex;
            if (head && !tail) { fromIndex = Resolve(endX); toIndex = Resolve(startX); }
            else { fromIndex = Resolve(startX); toIndex = Resolve(endX); }
            if (fromIndex is null || toIndex is null || fromIndex == toIndex) continue;
            messageLines.Add((line, fromIndex.Value, toIndex.Value, ArrowSyntax(dashed, open, head || tail), line.Box.CenterY, false));
        }
        if (messageLines.Count < 2) return null;

        // 4. Labels: each message takes the nearest free text box that overlaps its horizontal span.
        var labelPairs = new List<(double Cost, int Message, Shp Label)>();
        var labelCandidates = texts.Where(item => !consumed.Contains(item.Id)).ToList();
        for (var m = 0; m < messageLines.Count; m++)
        {
            var (line, _, _, _, y, self) = messageLines[m];
            foreach (var label in labelCandidates)
            {
                var reach = Math.Max(label.Box.Height, 250_000);
                var spanLeft = self ? line.Box.Left - endpointTolerance : line.Box.Left - endpointTolerance;
                var spanRight = self ? line.Box.Right + spacing : line.Box.Right + endpointTolerance;
                if (label.Box.Right < spanLeft || label.Box.Left > spanRight) continue;
                if (!self && label.Box.CenterX < line.Box.Left - endpointTolerance || !self && label.Box.CenterX > line.Box.Right + endpointTolerance) continue;
                var dy = label.Box.CenterY - (self ? line.Box.CenterY : y);
                if (Math.Abs(dy) > reach * 1.4) continue;
                // A label above the arrow is the usual place; below is accepted at a small penalty.
                var cost = Math.Abs(dy) + (dy > 0 && !self ? reach * 0.15 : 0) + Math.Abs(label.Box.CenterX - line.Box.CenterX) * 0.01;
                labelPairs.Add((cost, m, label));
            }
        }
        var labelOf = new Dictionary<int, Shp>();
        var labelTaken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in labelPairs.OrderBy(item => item.Cost))
        {
            if (labelOf.ContainsKey(pair.Message) || !labelTaken.Add(pair.Label.Id)) continue;
            labelOf[pair.Message] = pair.Label;
        }
        foreach (var label in labelOf.Values) consumed.Add(label.Id);

        foreach (var (line, from, to, arrow, y, _) in messageLines)
        {
            consumed.Add(line.Id);
            var index = messageLines.FindIndex(item => ReferenceEquals(item.Line, line));
            var text = labelOf.TryGetValue(index, out var label) ? Label(label.Text) : string.Empty;
            var syntax = $"P{from + 1}{arrow}P{to + 1}:" + (text.Length == 0 ? string.Empty : " " + text);
            events.Add(new DrawnEvent(y, 4, line.Box.CenterX, syntax));
        }

        // 5. Activations: a narrow text-less rectangle centred on a lifeline.
        foreach (var bar in plain.Where(item => !consumed.Contains(item.Id) &&
                                                 item.Box.Width <= Math.Max(300_000, spacing * 0.25) && item.Box.Height >= 200_000 &&
                                                 item.Box.Top >= bandTop - 60_000 && item.Box.Bottom <= bandBottom + 60_000))
        {
            var owner = participants.OrderBy(item => Math.Abs(item.X - bar.Box.CenterX)).First();
            if (Math.Abs(owner.X - bar.Box.CenterX) > Math.Max(bar.Box.Width, 200_000)) continue;
            events.Add(new DrawnEvent(bar.Box.Top + 30_000, 5, bar.Box.Left, "activate " + owner.Alias));
            events.Add(new DrawnEvent(bar.Box.Bottom + 30_000, 8, -bar.Box.Left, "deactivate " + owner.Alias));
            consumed.Add(bar.Id);
        }

        // 6. Notes: a painted or folded-corner text shape between the lifelines that no message claimed.
        string? title = null;
        var remaining = texts.Where(item => !consumed.Contains(item.Id)).ToList();
        foreach (var note in remaining.Where(item => item.Box.Top >= participantBottom &&
                                                     item.Box.Bottom <= bandBottom + 60_000 &&
                                                     (!item.Shape.IsUnpainted ||
                                                      item.Shape.Geometry.Equals("foldedCorner", StringComparison.OrdinalIgnoreCase))))
        {
            var covered = participants.Where(item => item.X >= note.Box.Left - endpointTolerance &&
                                                     item.X <= note.Box.Right + endpointTolerance).ToList();
            if (covered.Count == 0) covered = [participants.OrderBy(item => Math.Abs(item.X - note.Box.CenterX)).First()];
            var span = covered.Count == 1 ? covered[0].Alias : covered[0].Alias + "," + covered[^1].Alias;
            events.Add(new DrawnEvent(note.Box.Top, 6, note.Box.Left, $"Note over {span}: {Label(note.Text)}"));
            consumed.Add(note.Id);
        }
        // 7. A single text box above the participant row is the diagram's title.
        var above = texts.Where(item => !consumed.Contains(item.Id) && item.Box.Bottom <= participantTop + 1).ToList();
        if (above.Count == 1)
        {
            title = above[0].Text;
            consumed.Add(above[0].Id);
        }

        var output = new StringBuilder("sequenceDiagram\n");
        if (title is not null) output.Append("    title ").AppendLine(Label(title));
        foreach (var item in participants)
            output.Append("    participant ").Append(item.Alias).Append(" as ").AppendLine(Label(item.Box.Text));
        foreach (var item in events.OrderBy(item => item.Key).ThenBy(item => item.Priority).ThenBy(item => item.Tie)
                     .ThenBy(item => item.Text, StringComparer.Ordinal))
            output.Append("    ").AppendLine(item.Text);

        var topBound = Math.Min(participantTop, above.Count == 1 ? above[0].Box.Top : participantTop);
        var firstRow = (worksheet.Metrics is { } metrics ? (int)Math.Floor(metrics.RowFromEmu((long)topBound)) : 0) + 1;
        // The projection stands beside the cells of the sheet, never in place of them: no cell row is covered.
        return new DiagramProjection("sequence", output.ToString().TrimEnd(), Math.Max(1, firstRow), Math.Max(1, firstRow) - 1,
            "xlsx-drawingml", ConsumedShapeIds: consumed);
    }

    private static string ArrowSyntax(bool dashed, bool open, bool hasHead) =>
        !hasHead ? dashed ? "-->" : "->" : dashed ? open ? "--)" : "-->>" : open ? "-)" : "->>";

    private static bool SameText(string left, string right) =>
        StringComparer.Ordinal.Equals(Regex.Replace(left, @"\s+", string.Empty), Regex.Replace(right, @"\s+", string.Empty));
}
