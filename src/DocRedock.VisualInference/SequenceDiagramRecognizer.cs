namespace DocRedock.VisualInference;

/// <summary>A drawn shape the sequence recognizer may use: a participant header, an activation bar, a frame, a frame tab or a text box.</summary>
public sealed record SequenceShapeInput(string Id, VisualRect Bounds, string Text, string? Preset = null, bool IsTextBox = false);

/// <summary>A drawn line: <paramref name="Route"/> runs from the drawn start to the drawn end. An arrow type is the format's own
/// name; <c>arrow</c> and <c>open</c> are open arrowheads, every other present type is a filled one.</summary>
public sealed record SequenceLineInput(string Id, IReadOnlyList<VisualPoint> Route, bool Dashed, string? StartArrow, string? EndArrow,
    string? OwnText = null);

public enum SequenceMessageKind { Sync, Return, Async }

public sealed record SequenceParticipant(string ShapeId, string Label, double CenterX, VisualRect Header, string LifelineId);

public sealed record SequenceMessage(string LineId, int From, int To, string Label, string? LabelShapeId, SequenceMessageKind Kind,
    bool Dashed, double Y, VisualRect Bounds)
{
    public bool IsSelf => From == To;
}

/// <summary>The divider inside a frame that starts its next branch (<c>else</c> of an <c>alt</c>, <c>and</c> of a <c>par</c>).</summary>
public sealed record SequenceBranch(string Keyword, string Label, double Y, string LineId, string? LabelShapeId, VisualRect Bounds);

public sealed record SequenceFrame(string ShapeId, string Kind, string Label, VisualRect Bounds, string TabShapeId, string? ConditionShapeId,
    IReadOnlyList<SequenceBranch> Branches);

public sealed record SequenceRecognition(IReadOnlyList<SequenceParticipant> Participants, IReadOnlyList<SequenceMessage> Messages,
    IReadOnlyList<SequenceFrame> Frames, IReadOnlySet<string> ConsumedShapeIds, IReadOnlySet<string> TextShapeIds);

/// <summary>
/// Recognizes a UML-style sequence diagram drawn with shapes: a row of participant boxes, a long arrowless line (the lifeline)
/// under each, horizontal arrows between lifelines (messages), narrow bars on the lifelines (activations) and labelled
/// rectangles over the messages they group (alt / opt / loop frames, with dividers for their other branches). Only geometry is
/// used: no names, colours or file-specific marks. A page that does not look like this returns <c>null</c>, so the caller keeps its
/// flowchart reading.
/// </summary>
public static class SequenceDiagramRecognizer
{
    private static readonly string[] FrameKeywords =
        ["alt", "opt", "loop", "par", "critical", "break", "ref", "neg", "assert", "seq", "strict", "ignore", "consider", "group", "option"];

    public static SequenceRecognition? Recognize(IReadOnlyList<SequenceShapeInput> shapes, IReadOnlyList<SequenceLineInput> lines)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(lines);
        shapes = shapes.Where(shape => shape.Bounds.IsFinite).ToArray();
        lines = lines.Where(line => line.Route.Count >= 2 && line.Route.All(point => double.IsFinite(point.X) && double.IsFinite(point.Y))).ToArray();
        if (shapes.Count < 2 || lines.Count < 5) return null;
        bool HasText(SequenceShapeInput shape) => !string.IsNullOrWhiteSpace(shape.Text);
        static bool Present(string? arrow) => !string.IsNullOrWhiteSpace(arrow) && !arrow.Equals("none", StringComparison.OrdinalIgnoreCase);
        static bool IsOpen(string? arrow) => arrow is not null && (arrow.Equals("arrow", StringComparison.OrdinalIgnoreCase) ||
            arrow.Equals("open", StringComparison.OrdinalIgnoreCase));
        static bool Arrowed(SequenceLineInput line) => Present(line.StartArrow) || Present(line.EndArrow);

        // 1. Lifelines and the participant boxes they hang from.
        var textShapes = shapes.Where(HasText).ToArray();
        var lifelineCandidates = new List<(SequenceLineInput Line, double X, double Top, double Bottom)>();
        foreach (var line in lines)
        {
            if (Arrowed(line)) continue;
            var first = line.Route[0];
            var last = line.Route[^1];
            var dx = Math.Abs(last.X - first.X);
            var dy = Math.Abs(last.Y - first.Y);
            if (dy <= 0 || dx > dy * .02 || line.Route.Any(point => Math.Abs(point.X - first.X) > dy * .02)) continue;
            lifelineCandidates.Add((line, (first.X + last.X) / 2, Math.Min(first.Y, last.Y), Math.Max(first.Y, last.Y)));
        }
        if (lifelineCandidates.Count < 2) return null;
        var headers = new List<(SequenceShapeInput Header, SequenceLineInput Line, double X)>();
        foreach (var candidate in lifelineCandidates)
        {
            SequenceShapeInput? best = null;
            var bestGap = double.PositiveInfinity;
            foreach (var shape in textShapes)
            {
                var box = shape.Bounds;
                if (box.Width <= 0 || box.Height <= 0 || candidate.X < box.X || candidate.X > box.Right) continue;
                var gap = Math.Abs(candidate.Top - box.Bottom);
                if (candidate.Top < box.Y || gap > box.Height || candidate.Bottom - box.Bottom < box.Height * 2) continue;
                if (gap < bestGap) { best = shape; bestGap = gap; }
            }
            if (best is not null && headers.All(item => item.Header.Id != best.Id)) headers.Add((best, candidate.Line, candidate.X));
        }
        if (headers.Count < 2) return null;
        // The participants share one row: keep the largest group of boxes whose tops agree.
        var medianHeight = Median(headers.Select(item => item.Header.Bounds.Height));
        var rows = headers.GroupBy(item => Math.Round(item.Header.Bounds.Y / Math.Max(1, medianHeight * .5)))
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key).First().ToArray();
        if (rows.Length < 2)
            return null;
        var participantsRaw = rows.OrderBy(item => item.X).ToArray();
        var lifelineXs = participantsRaw.Select(item => item.X).ToArray();
        var minGap = lifelineXs.Zip(lifelineXs.Skip(1), (left, right) => right - left).Where(gap => gap > 0).DefaultIfEmpty(medianHeight).Min();
        var headerHeight = Median(participantsRaw.Select(item => item.Header.Bounds.Height));
        var rowBottom = participantsRaw.Max(item => item.Header.Bounds.Bottom);
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var textConsumed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in participantsRaw) { consumed.Add(item.Header.Id); consumed.Add(item.Line.Id); }
        var lifelineIds = participantsRaw.Select(item => item.Line.Id).ToHashSet(StringComparer.Ordinal);
        var participants = participantsRaw.Select(item => new SequenceParticipant(item.Header.Id, Collapse(item.Header.Text), item.X,
            item.Header.Bounds, item.Line.Id)).ToArray();
        var columnTolerance = minGap * .25;
        int Column(double x)
        {
            var best = -1;
            var bestDistance = columnTolerance;
            for (var index = 0; index < lifelineXs.Length; index++)
            {
                var distance = Math.Abs(lifelineXs[index] - x);
                if (distance <= bestDistance) { best = index; bestDistance = distance; }
            }
            return best;
        }

        // 2. Activation bars: narrow textless boxes resting on a lifeline.
        var barWidth = Math.Min(minGap * .3, Median(participantsRaw.Select(item => item.Header.Bounds.Width)) * .6);
        foreach (var shape in shapes)
        {
            if (HasText(shape) || consumed.Contains(shape.Id)) continue;
            var box = shape.Bounds;
            if (box.Width > barWidth || box.Height < box.Width || box.Y < rowBottom - headerHeight * .25) continue;
            if (Column(box.Center.X) >= 0 || lifelineXs.Any(x => x >= box.X - box.Width && x <= box.Right + box.Width)) consumed.Add(shape.Id);
        }

        // 3. Frames: textless rectangles wide enough to span lifelines, with a keyword tab in their top left corner.
        var frameCandidates = new List<(SequenceShapeInput Frame, SequenceShapeInput Tab, string Kind, string Condition, SequenceShapeInput? ConditionShape)>();
        foreach (var frame in shapes)
        {
            if (HasText(frame) || consumed.Contains(frame.Id)) continue;
            var box = frame.Bounds;
            if (box.Width < minGap * .5 || box.Height < headerHeight || box.Y < rowBottom - headerHeight * .25) continue;
            SequenceShapeInput? tab = null;
            string? kind = null;
            string remainder = string.Empty;
            foreach (var shape in textShapes)
            {
                if (consumed.Contains(shape.Id) || shape.Bounds.Width > box.Width * .6) continue;
                var tabBox = shape.Bounds;
                if (tabBox.X < box.X - minGap * .1 || tabBox.X > box.X + box.Width * .25 ||
                    tabBox.Y < box.Y - tabBox.Height * .5 || tabBox.Y > box.Y + tabBox.Height) continue;
                var text = Collapse(shape.Text);
                var token = FirstToken(text, out var rest);
                var keyword = token.ToLowerInvariant();
                if (!FrameKeywords.Contains(keyword)) continue;
                if (tab is null || Math.Abs(tabBox.Y - box.Y) < Math.Abs(tab.Bounds.Y - box.Y))
                    (tab, kind, remainder) = (shape, keyword, rest);
            }
            if (tab is null || kind is null) continue;
            SequenceShapeInput? conditionShape = null;
            var condition = remainder;
            if (condition.Length == 0)
            {
                foreach (var shape in textShapes)
                {
                    if (ReferenceEquals(shape, tab) || consumed.Contains(shape.Id) || shape.Id == tab.Id) continue;
                    var conditionBox = shape.Bounds;
                    var overlap = Math.Min(conditionBox.Bottom, tab.Bounds.Bottom) - Math.Max(conditionBox.Y, tab.Bounds.Y);
                    var horizontalGap = conditionBox.X - tab.Bounds.Right;
                    if (overlap < Math.Min(conditionBox.Height, tab.Bounds.Height) * .4 ||
                        horizontalGap < -tab.Bounds.Width * .25 || horizontalGap > Math.Max(tab.Bounds.Width * 2, headerHeight * 3)) continue;
                    if (FrameKeywords.Contains(FirstToken(Collapse(shape.Text), out _).ToLowerInvariant()) && shape.Bounds.Width <= box.Width * .6) continue;
                    if (conditionShape is null || conditionBox.X < conditionShape.Bounds.X) conditionShape = shape;
                }
                if (conditionShape is not null) condition = Collapse(conditionShape.Text);
            }
            frameCandidates.Add((frame, tab, kind, StripBrackets(condition), conditionShape));
        }

        // 4. Dividers of those frames: arrowless horizontal lines across a frame.
        var dividerByLine = new Dictionary<string, SequenceShapeInput>(StringComparer.Ordinal);
        var dividerFrame = new Dictionary<string, (SequenceShapeInput Frame, SequenceLineInput Line, double Y)>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (lifelineIds.Contains(line.Id) || Arrowed(line)) continue;
            var first = line.Route[0];
            var last = line.Route[^1];
            if (Math.Abs(last.Y - first.Y) > Math.Abs(last.X - first.X) * .02) continue;
            var left = Math.Min(first.X, last.X);
            var right = Math.Max(first.X, last.X);
            (SequenceShapeInput Frame, double Area)? owner = null;
            foreach (var candidate in frameCandidates)
            {
                var box = candidate.Frame.Bounds;
                var tolerance = Math.Max(box.Width * .03, minGap * .05);
                var y = (first.Y + last.Y) / 2;
                if (left < box.X - tolerance || right > box.Right + tolerance || right - left < box.Width * .9 ||
                    y <= box.Y + tolerance || y >= box.Bottom - tolerance) continue;
                var area = box.Width * box.Height;
                if (owner is null || area < owner.Value.Area) owner = (candidate.Frame, area);
            }
            if (owner is null) continue;
            consumed.Add(line.Id);
            dividerFrame[line.Id] = (owner.Value.Frame, line, (first.Y + last.Y) / 2);
        }

        // 5. Messages.
        var rawMessages = new List<(SequenceLineInput Line, int From, int To, VisualRect Box, double Y, bool Dashed, SequenceMessageKind Kind)>();
        var unmatchedArrows = 0;
        foreach (var line in lines)
        {
            if (consumed.Contains(line.Id) || !Arrowed(line)) continue;
            var first = line.Route[0];
            var last = line.Route[^1];
            var startColumn = Column(first.X);
            var endColumn = Column(last.X);
            var box = BoundsOf(line.Route);
            var dx = Math.Abs(last.X - first.X);
            var dy = Math.Abs(last.Y - first.Y);
            var horizontal = startColumn >= 0 && endColumn >= 0 && startColumn != endColumn && dy <= dx * .15;
            var self = startColumn >= 0 && startColumn == endColumn && box.Height > 0 && box.Height <= headerHeight * 2 &&
                box.Width <= minGap * .8 && last.Y > first.Y - 1;
            if (!horizontal && !self || first.Y < rowBottom - headerHeight * .25 && last.Y < rowBottom - headerHeight * .25) { unmatchedArrows++; continue; }
            var from = startColumn;
            var to = endColumn;
            if (Present(line.StartArrow) && !Present(line.EndArrow) && !self) (from, to) = (endColumn, startColumn);
            var open = IsOpen(Present(line.EndArrow) ? line.EndArrow : line.StartArrow);
            var kind = open ? SequenceMessageKind.Async : line.Dashed ? SequenceMessageKind.Return : SequenceMessageKind.Sync;
            rawMessages.Add((line, from, to, box, (first.Y + last.Y) / 2, line.Dashed, kind));
            consumed.Add(line.Id);
        }
        var arrowedTotal = rawMessages.Count + unmatchedArrows;
        if (rawMessages.Count < 3 || rawMessages.Count < arrowedTotal * .6) return null;

        // 6. Keep the frames that really group messages; the ones that do not stay ordinary shapes.
        var frames = new List<SequenceFrame>();
        var keptFrameShapes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in frameCandidates)
        {
            var box = candidate.Frame.Bounds;
            var tolerance = Math.Max(box.Width * .03, minGap * .05);
            if (!rawMessages.Any(message => message.Y >= box.Y && message.Y <= box.Bottom &&
                    message.Box.X >= box.X - tolerance && message.Box.Right <= box.Right + tolerance)) continue;
            keptFrameShapes.Add(candidate.Frame.Id);
        }

        // A box as big as a participant, sitting on a lifeline and holding text, is a node of a flow drawn over lane
        // centre lines, not a part of a sequence diagram (whose lifelines carry only narrow bars and small labels).
        var headerWidth = Median(participantsRaw.Select(item => item.Header.Bounds.Width));
        var nodesOnLifelines = shapes.Count(shape => HasText(shape) && !shape.IsTextBox && !consumed.Contains(shape.Id) &&
            !frameCandidates.Any(item => keptFrameShapes.Contains(item.Frame.Id) && (item.Tab.Id == shape.Id || item.ConditionShape?.Id == shape.Id)) &&
            shape.Bounds.Y >= rowBottom - headerHeight * .25 && shape.Bounds.Width >= headerWidth * .5 && shape.Bounds.Height >= headerHeight * .6 &&
            lifelineXs.Any(x => Math.Abs(x - shape.Bounds.Center.X) <= shape.Bounds.Width * .25));
        if (nodesOnLifelines >= 2) return null;

        // 7. Text: branch labels first, then message labels from the text left over.
        bool Free(SequenceShapeInput shape) => HasText(shape) && !consumed.Contains(shape.Id) && !textConsumed.Contains(shape.Id) &&
            !frameCandidates.Any(item => keptFrameShapes.Contains(item.Frame.Id) && (item.Tab.Id == shape.Id || item.ConditionShape?.Id == shape.Id));
        foreach (var candidate in frameCandidates.Where(item => keptFrameShapes.Contains(item.Frame.Id)))
        {
            consumed.Add(candidate.Frame.Id);
            consumed.Add(candidate.Tab.Id); textConsumed.Add(candidate.Tab.Id);
            if (candidate.ConditionShape is not null) { consumed.Add(candidate.ConditionShape.Id); textConsumed.Add(candidate.ConditionShape.Id); }
        }
        var branchesByFrame = new Dictionary<string, List<SequenceBranch>>(StringComparer.Ordinal);
        foreach (var (lineId, (frame, line, y)) in dividerFrame.OrderBy(item => item.Value.Y))
        {
            if (!keptFrameShapes.Contains(frame.Id)) { consumed.Remove(lineId); continue; }
            var candidate = frameCandidates.First(item => item.Frame.Id == frame.Id);
            var left = Math.Min(line.Route[0].X, line.Route[^1].X);
            SequenceShapeInput? labelShape = null;
            var bestDistance = double.PositiveInfinity;
            foreach (var shape in textShapes)
            {
                if (!Free(shape)) continue;
                var box = shape.Bounds;
                if (box.X < left - minGap * .1 || box.X > left + frame.Bounds.Width * .3) continue;
                var vertical = box.Y > y ? box.Y - y : y > box.Bottom ? y - box.Bottom : 0;
                if (vertical > box.Height * .75 || y < box.Y - box.Height * .25 && box.Y - y > box.Height * .75) continue;
                var distance = Math.Abs(box.Center.Y - y) + Math.Abs(box.X - left) * .1;
                if (distance < bestDistance) { labelShape = shape; bestDistance = distance; }
            }
            var keyword = candidate.Kind switch { "par" => "and", "critical" => "option", _ => "else" };
            var label = labelShape is null ? string.Empty : StripBrackets(StripLeadingKeyword(Collapse(labelShape.Text), keyword));
            if (labelShape is not null) { consumed.Add(labelShape.Id); textConsumed.Add(labelShape.Id); }
            if (!branchesByFrame.TryGetValue(frame.Id, out var branches)) branchesByFrame[frame.Id] = branches = [];
            branches.Add(new SequenceBranch(keyword, label, y, lineId, labelShape?.Id, BoundsOf(line.Route)));
        }
        foreach (var candidate in frameCandidates.Where(item => keptFrameShapes.Contains(item.Frame.Id))
                     .OrderBy(item => item.Frame.Bounds.Y).ThenByDescending(item => item.Frame.Bounds.Height))
        {
            branchesByFrame.TryGetValue(candidate.Frame.Id, out var branches);
            frames.Add(new SequenceFrame(candidate.Frame.Id, candidate.Kind, candidate.Condition, candidate.Frame.Bounds,
                candidate.Tab.Id, candidate.ConditionShape?.Id, branches?.OrderBy(branch => branch.Y).ToArray() ?? []));
        }

        var choices = new List<EdgeLabelCandidate>();
        var labelOf = new Dictionary<string, SequenceShapeInput>(StringComparer.Ordinal);
        foreach (var shape in textShapes)
        {
            if (!Free(shape) || shape.Bounds.Y < rowBottom - headerHeight * .25) continue;
            var box = shape.Bounds;
            var tolerance = Math.Max(1, box.Height * 1.5);
            foreach (var message in rawMessages)
            {
                var distance = DistanceToRoute(message.Line.Route, box);
                if (distance > tolerance) continue;
                // A message label sits over its arrow, within its horizontal extent, or beside a self-message loop.
                var center = box.Center.X;
                var covered = center >= message.Box.X - tolerance && center <= message.Box.Right + tolerance;
                var self = message.From == message.To;
                if (!covered && !(self && box.X >= message.Box.X - tolerance)) continue;
                var above = box.Center.Y <= message.Y ? 1 : .85;
                choices.Add(new EdgeLabelCandidate(shape.Id, message.Line.Id, (1 - distance / tolerance * .8) * above));
                labelOf[shape.Id] = shape;
            }
        }
        var assigned = EdgeLabelAssigner.Assign(choices);
        var labelByLine = assigned.ToDictionary(item => item.Value, item => labelOf[item.Key], StringComparer.Ordinal);

        var messages = new List<SequenceMessage>();
        foreach (var raw in rawMessages.OrderBy(item => item.Y).ThenBy(item => item.Line.Id, StringComparer.Ordinal))
        {
            labelByLine.TryGetValue(raw.Line.Id, out var labelShape);
            var label = !string.IsNullOrWhiteSpace(raw.Line.OwnText) ? Collapse(raw.Line.OwnText!) : labelShape is null ? string.Empty : Collapse(labelShape.Text);
            if (labelShape is not null && string.IsNullOrWhiteSpace(raw.Line.OwnText)) { consumed.Add(labelShape.Id); textConsumed.Add(labelShape.Id); }
            messages.Add(new SequenceMessage(raw.Line.Id, raw.From, raw.To, label, labelShape?.Id, raw.Kind, raw.Dashed, raw.Y, raw.Box));
        }
        return new SequenceRecognition(participants, messages, frames, consumed, textConsumed);
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        return ordered.Length == 0 ? 1 : ordered[ordered.Length / 2];
    }

    private static VisualRect BoundsOf(IReadOnlyList<VisualPoint> points)
    {
        var left = points.Min(point => point.X);
        var top = points.Min(point => point.Y);
        return new VisualRect(left, top, points.Max(point => point.X) - left, points.Max(point => point.Y) - top);
    }

    private static double DistanceToRoute(IReadOnlyList<VisualPoint> route, VisualRect box)
    {
        var distance = double.PositiveInfinity;
        for (var index = 1; index < route.Count; index++)
            distance = Math.Min(distance, GeometryMath.DistanceToSegmentRect(route[index - 1], route[index], box));
        return distance;
    }

    private static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string FirstToken(string text, out string rest)
    {
        var end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '[' && text[end] != '［' && text[end] != ':') end++;
        rest = text[end..].TrimStart(' ', ':');
        return text[..end];
    }

    private static string StripLeadingKeyword(string text, string keyword)
    {
        var token = FirstToken(text, out var rest);
        return token.Equals(keyword, StringComparison.OrdinalIgnoreCase) ? rest : text;
    }

    /// <summary>A guard is written in brackets ("[logged in]"); the brackets are the notation, not the condition.</summary>
    private static string StripBrackets(string text)
    {
        var value = text.Trim();
        if (value.Length >= 2 && (value[0] == '[' && value[^1] == ']' || value[0] == '［' && value[^1] == '］'))
            value = value[1..^1].Trim();
        return value;
    }
}
