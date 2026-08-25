using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.OpenXml.Xlsx;

/// <summary>
/// Projects Japanese Excel-grid diagrams into a semantic Mermaid view while retaining
/// the original cells as the authoritative round-trip representation.
/// </summary>
internal static class XlsxMermaidProjection
{
    private static readonly char[] ArrowCharacters = ['←', '→', '↑', '↓', '▶', '◀'];
    private static readonly Regex SectionHeadingPattern = new(@"^\d+(?:\.\d+)+\s+\S", RegexOptions.CultureInvariant);
    private static readonly Regex NumberedMessagePattern = new(@"^\d+[A-Za-z]?[.)]\s*\S", RegexOptions.CultureInvariant);
    private static readonly Regex StateLabelPattern = new(@"^(?<name>.+)\n(?<code>[A-Z][A-Z0-9_]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex ConnectorPattern = new(@"^[A-Z]$", RegexOptions.CultureInvariant);
    private static readonly Regex FragmentPattern = new(@"^(?<kind>alt|break|opt|loop)\b\s*(?<guard>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static DocumentNode? TryCreate(XlsxWorksheetRecord worksheet, int order)
    {
        // Content and DrawingML topology are authoritative.  The sheet name is only a
        // tie-breaker so ordinary workbooks do not need project-specific naming rules.
        var hasSequenceEvidence = HasSequenceEvidence(worksheet);
        var prefersLanes = ContainsAny(worksheet.Name, "フロー", "flow", "swimlane", "スイムレーン");
        DiagramProjection? projection = TryCreateStateDiagram(worksheet);
        if (projection is null && hasSequenceEvidence) projection = TryCreateSequence(worksheet);
        if (projection is null && prefersLanes) projection = TryCreateDrawingFlowchart(worksheet, useLanes: true);
        projection ??= TryCreateDrawingFlowchart(worksheet, useLanes: false);
        if (projection is null && HasGridFlowEvidence(worksheet))
            projection = TryCreateFlowchart(worksheet);

        if (projection is null || string.IsNullOrWhiteSpace(projection.Mermaid)) return null;
        var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["diagram_language"] = JsonSerializer.SerializeToElement("mermaid"),
            ["diagram_type"] = JsonSerializer.SerializeToElement(projection.Type),
            ["diagram_source"] = JsonSerializer.SerializeToElement(projection.Source),
            ["diagram_min_row"] = JsonSerializer.SerializeToElement(projection.MinRow),
            ["diagram_max_row"] = JsonSerializer.SerializeToElement(projection.MaxRow)
        };
        return new DocumentNode(
            "n_" + Hash(worksheet.Name + "!mermaid")[..16],
            NodeKind.Diagram,
            null,
            order,
            ContentLayer.Derived,
            new TextNodeContent(projection.Mermaid),
            new SourceAnchor("xlsx", worksheet.PartUri, [new AnchorLocator("diagram_projection", "mermaid")]),
            Editability: NodeEditability.Protected,
            Provenance: [new ProvenanceItem(EvidenceKind.LayoutInferred)],
            Extensions: extensions);
    }

    private static DiagramProjection? TryCreateStateDiagram(XlsxWorksheetRecord worksheet)
    {
        var cells = worksheet.Cells.Where(cell => !string.IsNullOrWhiteSpace(cell.Value)).ToArray();
        var rows = cells.GroupBy(cell => cell.RowIndex).OrderBy(group => group.Key).ToArray();
        var header = rows.FirstOrDefault(group =>
            group.Any(cell => ContainsAny(cell.Value ?? string.Empty, "遷移元", "from state")) &&
            group.Any(cell => ContainsAny(cell.Value ?? string.Empty, "イベント", "event")) &&
            group.Any(cell => ContainsAny(cell.Value ?? string.Empty, "遷移先", "to state")));
        if (header is null) return null;

        int Column(params string[] names) => header.FirstOrDefault(cell => ContainsAny(cell.Value ?? string.Empty, names))?.ColumnIndex ?? 0;
        var sourceColumn = Column("遷移元", "from state");
        var eventColumn = Column("イベント", "event");
        var guardColumn = Column("ガード", "条件", "guard");
        var targetColumn = Column("遷移先", "to state");
        if (sourceColumn == 0 || eventColumn == 0 || targetColumn == 0) return null;

        var transitions = new List<StateTransition>();
        foreach (var row in rows.Where(group => group.Key > header.Key))
        {
            string Value(int column) => row.FirstOrDefault(cell => cell.ColumnIndex == column)?.Value?.Trim() ?? string.Empty;
            var source = Value(sourceColumn);
            var target = Value(targetColumn);
            var @event = Value(eventColumn);
            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(@event))
            {
                if (transitions.Count > 0) break;
                continue;
            }
            transitions.Add(new StateTransition(source, target, @event, guardColumn == 0 ? string.Empty : Value(guardColumn)));
        }
        if (transitions.Count < 2) return null;

        var stateLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in cells.Where(cell => cell.RowIndex < header.Key))
        {
            var value = (cell.Value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
            var match = StateLabelRegex().Match(value);
            if (match.Success)
                stateLabels[match.Groups["code"].Value] = match.Groups["name"].Value.Trim();
        }

        var stateCodes = transitions.SelectMany(transition => new[] { transition.Source, transition.Target })
            .Where(value => !string.IsNullOrWhiteSpace(value) && value is not "—" and not "-")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var output = new StringBuilder("stateDiagram-v2\n");
        foreach (var code in stateCodes)
        {
            var id = StateId(code);
            var label = stateLabels.TryGetValue(code, out var name) ? $"{name}<br/>{code}" : code;
            output.Append("    state \"").Append(Label(label)).Append("\" as ").AppendLine(id);
        }
        foreach (var transition in transitions)
        {
            var source = transition.Source is "" or "—" or "-" ? "[*]" : StateId(transition.Source);
            var target = StateId(transition.Target);
            var label = Label(transition.Event);
            if (!string.IsNullOrWhiteSpace(transition.Guard) && transition.Guard is not "—" and not "-")
                label += "<br/>[" + Label(transition.Guard).Trim('[', ']') + "]";
            output.Append("    ").Append(source).Append(" --> ").Append(target).Append(": ").AppendLine(label);
        }

        var headingRow = cells.Where(cell => cell.RowIndex < header.Key && ContainsAny(cell.Value ?? string.Empty, "状態遷移図", "state diagram"))
            .Select(cell => cell.RowIndex).DefaultIfEmpty(1).Max();
        var visualCells = cells.Where(cell => cell.RowIndex > headingRow && cell.RowIndex < header.Key &&
                                              !ContainsAny(cell.Value ?? string.Empty, "重要な不変条件", "invariant") &&
                                              !SectionHeadingRegex().IsMatch((cell.Value ?? string.Empty).Trim()))
            .ToArray();
        var minRow = visualCells.Select(cell => cell.RowIndex).DefaultIfEmpty(headingRow + 1).Min();
        var maxRow = visualCells.Select(cell => cell.RowIndex).DefaultIfEmpty(header.Key - 1).Max();
        return new DiagramProjection("state", output.ToString().TrimEnd(), minRow, maxRow, "xlsx-state-table+cell-layout");
    }

    private static DiagramProjection? TryCreateSequence(XlsxWorksheetRecord worksheet)
    {
        var cells = worksheet.Cells.Where(cell => !string.IsNullOrWhiteSpace(cell.Value)).ToArray();
        var regions = ReadRegions(worksheet, cells);
        var actorRow = regions
            .Where(region => region.MinRow <= 12 && !IsHorizontalArrow(region.Value) && !IsLifeline(region.Value) && !IsNote(region.Value))
            .GroupBy(region => region.MinRow)
            .Select(group => new { Row = group.Key, Actors = group.OrderBy(region => region.MinColumn).ToArray() })
            .Where(group => group.Actors.Length >= 2)
            .OrderByDescending(group => group.Actors.Length)
            .ThenBy(group => group.Row)
            .FirstOrDefault();
        if (actorRow is null) return null;

        var actors = actorRow.Actors.OrderBy(actor => actor.MinColumn).ToArray();
        if (actors.Length < 2) return null;

        var messageCells = cells
            .Where(cell => cell.RowIndex > actorRow.Row &&
                           (IsHorizontalArrow(cell.Value ?? string.Empty) || NumberedMessageRegex().IsMatch((cell.Value ?? string.Empty).Trim())))
            .OrderBy(cell => cell.RowIndex)
            .ThenBy(cell => cell.ColumnIndex)
            .ToArray();
        var arrowAssignments = MatchSequenceArrows(messageCells, worksheet.DrawingShapes ?? []);
        var messages = messageCells
            .Select(cell => new SequenceLine(
                cell.RowIndex,
                cell.ColumnIndex,
                SequenceMessage(cell, actors, arrowAssignments.GetValueOrDefault(cell), worksheet.DrawingShapes ?? [])))
            .Where(message => message.Text is not null)
            .ToArray();
        if (messages.Length == 0) return null;

        var output = new StringBuilder("sequenceDiagram\n");
        for (var index = 0; index < actors.Length; index++)
            output.Append("    participant P").Append(index + 1).Append(" as ").AppendLine(Label(actors[index].Value));
        var notes = regions.Where(region => region.MinRow > actorRow.Row && IsNote(region.Value))
            .OrderBy(region => region.MinRow).ThenBy(region => region.MinColumn).ToArray();
        AppendSequenceTimeline(output, actors, regions, messages, notes, worksheet.DrawingShapes ?? []);
        var maxRow = messageCells.Select(cell => cell.RowIndex)
            .Concat(notes.Select(note => note.MaxRow))
            .Concat(ReadSequenceFragments(regions, worksheet.DrawingShapes ?? []).Select(fragment => fragment.EndRow))
            .Concat((worksheet.DrawingShapes ?? []).Where(IsActivationShape).Select(shape => (int)Math.Ceiling(ShapeBounds(shape).MaxRow)))
            .DefaultIfEmpty(actorRow.Row).Max();
        return new DiagramProjection("sequence", output.ToString().TrimEnd(), Math.Max(1, actorRow.Row - 2), maxRow,
            worksheet.DrawingShapes is { Count: > 0 } ? "xlsx-drawingml+cell-layout" : "xlsx-cell-layout");
    }

    private static string? SequenceMessage(
        XlsxCellRecord cell,
        IReadOnlyList<Region> actors,
        DrawingArrow? drawingArrow,
        IReadOnlyList<XlsxDrawingShapeRecord> shapes)
    {
        var value = cell.Value ?? string.Empty;
        if (drawingArrow is not null)
        {
            var sourcePosition = drawingArrow.Direction is Direction.Right or Direction.Down
                ? drawingArrow.MinColumn
                : drawingArrow.MaxColumn;
            var targetPosition = drawingArrow.Direction is Direction.Right or Direction.Down
                ? drawingArrow.MaxColumn
                : drawingArrow.MinColumn;
            var drawingFrom = NearestActor(actors, sourcePosition) + 1;
            var drawingTo = NearestActor(actors, targetPosition) + 1;
            if (drawingFrom != drawingTo)
            {
                var drawingLabel = MessageLabel(value);
                if (drawingLabel.Length == 0) return null;
                var drawingSyntax = drawingArrow.Direction is Direction.Left or Direction.Up ? "-->>" : "->>";
                return $"P{drawingFrom}{drawingSyntax}P{drawingTo}: {Label(drawingLabel)}";
            }
        }

        var nearbyNoteShape = shapes.Where(shape => StringComparer.OrdinalIgnoreCase.Equals(shape.Geometry, "foldedCorner"))
            .OrderBy(shape => Math.Abs(shape.Row - cell.RowIndex) * 100 + Math.Abs(shape.Column - cell.ColumnIndex))
            .FirstOrDefault();
        if (nearbyNoteShape is not null && Math.Abs(nearbyNoteShape.Row - cell.RowIndex) <= 3)
        {
            var actor = NearestActor(actors, cell.ColumnIndex) + 1;
            return $"Note over P{actor}: {Label(MessageLabel(value))}";
        }

        var left = actors.Select((actor, index) => (actor, index))
            .Where(item => item.actor.MinColumn <= cell.ColumnIndex)
            .OrderByDescending(item => item.actor.MinColumn).FirstOrDefault();
        var right = actors.Select((actor, index) => (actor, index))
            .Where(item => item.actor.MinColumn > cell.ColumnIndex)
            .OrderBy(item => item.actor.MinColumn).FirstOrDefault();
        if (left.actor is null || right.actor is null) return null;

        var reverse = value.Contains('◀') || value.Contains('←');
        var label = MessageLabel(value);
        if (label.Length == 0) return null;
        var from = reverse ? right.index + 1 : left.index + 1;
        var to = reverse ? left.index + 1 : right.index + 1;
        var arrow = reverse ? "-->>" : "->>";
        return $"P{from}{arrow}P{to}: {Label(label)}";
    }

    private static string MessageLabel(string value)
    {
        // Keep source numbering (including branches such as 4a/4b) because other
        // sections in a design document often refer to those exact identifiers.
        return Regex.Replace(value, @"\s*[─━—\-<>▶◀←→]+\s*$", string.Empty).Trim();
    }

    private static void AppendSequenceTimeline(
        StringBuilder output,
        IReadOnlyList<Region> actors,
        IReadOnlyList<Region> regions,
        IReadOnlyList<SequenceLine> messages,
        IReadOnlyList<Region> notes,
        IReadOnlyList<XlsxDrawingShapeRecord> shapes)
    {
        var events = new List<SequenceEvent>();
        var fragments = ReadSequenceFragments(regions, shapes);
        foreach (var fragment in fragments)
        {
            events.Add(new SequenceEvent(fragment.EndRow, 0, "end"));
            events.Add(new SequenceEvent(fragment.StartRow, 1, fragment.Kind +
                (fragment.Guard.Length == 0 ? string.Empty : " " + Label(fragment.Guard))));
            foreach (var branch in fragment.Branches.Skip(1))
                events.Add(new SequenceEvent(branch.Row, 2, "else " + Label(branch.Guard)));

            var fragmentAnnotations = regions.Where(region =>
                    region.MinRow > fragment.StartRow && region.MinRow < fragment.EndRow &&
                    region.CenterColumn >= fragment.MinColumn && region.CenterColumn <= fragment.MaxColumn &&
                    !NumberedMessageRegex().IsMatch(region.Value.Trim()) &&
                    !IsArrow(region.Value) && !IsLifeline(region.Value) &&
                    !FragmentRegex().IsMatch(region.Value.Trim()) && !IsGuard(region.Value) &&
                    !SectionHeadingRegex().IsMatch(region.Value.Trim()) && region.Value.Trim().Length > 1)
                .Where(region => !notes.Contains(region))
                .ToArray();
            foreach (var annotation in fragmentAnnotations)
                events.Add(new SequenceEvent(annotation.MinRow, 5, SequenceNote(actors, annotation)));
        }

        foreach (var shape in shapes.Where(IsActivationShape))
        {
            var bounds = ShapeBounds(shape);
            var actor = NearestActor(actors, bounds.CenterColumn) + 1;
            events.Add(new SequenceEvent((int)Math.Floor(bounds.MinRow), 3, $"activate P{actor}"));
            events.Add(new SequenceEvent((int)Math.Ceiling(bounds.MaxRow), 8, $"deactivate P{actor}"));
        }
        events.AddRange(messages.Select(message => new SequenceEvent(message.Row, 4, message.Text!)));
        events.AddRange(notes.Select(note => new SequenceEvent(note.MinRow, 6, SequenceNote(actors, note))));

        foreach (var item in events
                     .Where(item => !string.IsNullOrWhiteSpace(item.Text))
                     .OrderBy(item => item.Row).ThenBy(item => item.Priority).ThenBy(item => item.Text, StringComparer.Ordinal))
            output.Append("    ").AppendLine(item.Text);
    }

    private static IReadOnlyList<SequenceFragment> ReadSequenceFragments(
        IReadOnlyList<Region> regions,
        IReadOnlyList<XlsxDrawingShapeRecord> shapes)
    {
        var markers = regions
            .Select(region => (Region: region, Match: FragmentRegex().Match(region.Value.Trim())))
            .Where(item => item.Match.Success)
            .OrderBy(item => item.Region.MinRow).ThenBy(item => item.Region.MinColumn)
            .ToArray();
        var result = new List<SequenceFragment>();
        foreach (var marker in markers)
        {
            var frame = shapes.Where(shape => !shape.IsConnector &&
                                              StringComparer.OrdinalIgnoreCase.Equals(shape.Geometry, "rect"))
                .Select(shape => ShapeBounds(shape))
                .Where(bounds => bounds.MinRow <= marker.Region.MinRow + 0.5 &&
                                 bounds.MaxRow >= marker.Region.MaxRow &&
                                 bounds.MinColumn <= marker.Region.MinColumn + 0.5 &&
                                 bounds.MaxColumn >= marker.Region.MaxColumn &&
                                 bounds.MaxRow - bounds.MinRow >= 2 &&
                                 bounds.MaxColumn - bounds.MinColumn >= 4)
                .OrderBy(bounds => (bounds.MaxRow - bounds.MinRow) * (bounds.MaxColumn - bounds.MinColumn))
                .FirstOrDefault();
            var minColumn = frame is null ? marker.Region.MinColumn : (int)Math.Floor(frame.MinColumn);
            var maxColumn = frame is null ? marker.Region.MaxColumn + 20 : (int)Math.Ceiling(frame.MaxColumn);
            var endRow = frame is null ? marker.Region.MaxRow + 4 : (int)Math.Ceiling(frame.MaxRow);
            var guards = regions.Where(region => region.MinRow >= marker.Region.MinRow && region.MinRow < endRow &&
                                                 region.CenterColumn >= minColumn && region.CenterColumn <= maxColumn &&
                                                 IsGuard(region.Value))
                .OrderBy(region => region.MinRow).ThenBy(region => region.MinColumn)
                .Select(region => new FragmentBranch(region.MinRow, TrimGuard(region.Value)))
                .ToArray();
            var inlineGuard = marker.Match.Groups["guard"].Value.Trim();
            var guard = inlineGuard.Length > 0 ? TrimGuard(inlineGuard) : guards.FirstOrDefault()?.Guard ?? string.Empty;
            result.Add(new SequenceFragment(
                marker.Match.Groups["kind"].Value.ToLowerInvariant(),
                guard,
                marker.Region.MinRow,
                endRow,
                minColumn,
                maxColumn,
                guards));
        }
        return result;
    }

    private static string SequenceNote(IReadOnlyList<Region> actors, Region note)
    {
        var covered = actors.Select((actor, index) => (actor, index))
            .Where(item => item.actor.CenterColumn >= note.MinColumn && item.actor.CenterColumn <= note.MaxColumn)
            .OrderBy(item => item.index).ToArray();
        if (covered.Length == 0)
            covered = actors.Select((actor, index) => (actor, index))
                .OrderBy(item => Math.Abs(item.actor.CenterColumn - note.CenterColumn)).Take(2)
                .OrderBy(item => item.index).ToArray();
        var first = covered[0].index + 1;
        var last = covered[^1].index + 1;
        return $"Note over P{first}" + (last == first ? string.Empty : $",P{last}") + ": " + Label(note.Value);
    }

    private static bool IsActivationShape(XlsxDrawingShapeRecord shape) =>
        !shape.IsConnector && StringComparer.OrdinalIgnoreCase.Equals(shape.Geometry, "rect") &&
        shape.WidthEmu is > 0 and <= 300_000 && shape.HeightEmu >= 400_000;

    private static bool IsGuard(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 2 && trimmed[0] == '[' && trimmed[^1] == ']';
    }

    private static string TrimGuard(string value) => value.Trim().TrimStart('[').TrimEnd(']').Trim();

    private static int NearestActor(IReadOnlyList<Region> actors, double column) => actors
        .Select((actor, index) => (Distance: Math.Abs(actor.CenterColumn - column), Index: index))
        .OrderBy(item => item.Distance).ThenBy(item => item.Index).First().Index;

    private static IReadOnlyDictionary<XlsxCellRecord, DrawingArrow> MatchSequenceArrows(
        IReadOnlyList<XlsxCellRecord> messages,
        IReadOnlyList<XlsxDrawingShapeRecord> shapes)
    {
        var available = ReadDrawingArrows(shapes)
            .Where(arrow => arrow.Direction is Direction.Left or Direction.Right)
            .OrderBy(arrow => arrow.CenterRow).ThenBy(arrow => arrow.MinColumn).ToList();
        var result = new Dictionary<XlsxCellRecord, DrawingArrow>();
        foreach (var message in messages.OrderBy(cell => cell.RowIndex).ThenBy(cell => cell.ColumnIndex))
        {
            if (IsNearFoldedCorner(message, shapes)) continue;
            var arrow = available
                .Where(candidate => candidate.CenterRow >= message.RowIndex - 0.5 && candidate.CenterRow <= message.RowIndex + 4.5)
                .OrderBy(candidate => Math.Abs(candidate.CenterRow - (message.RowIndex + 2)) * 10 +
                                      Math.Abs(candidate.MinColumn - message.ColumnIndex))
                .FirstOrDefault();
            if (arrow is null) continue;
            result[message] = arrow;
            available.Remove(arrow);
        }
        return result;
    }

    private static bool IsNearFoldedCorner(XlsxCellRecord cell, IReadOnlyList<XlsxDrawingShapeRecord> shapes) =>
        shapes.Any(shape => StringComparer.OrdinalIgnoreCase.Equals(shape.Geometry, "foldedCorner") &&
                            Math.Abs(shape.Row - cell.RowIndex) <= 3 && Math.Abs(shape.Column - cell.ColumnIndex) <= 4);

    private static IReadOnlyList<DrawingArrow> ReadDrawingArrows(IReadOnlyList<XlsxDrawingShapeRecord> shapes)
    {
        var result = new List<DrawingArrow>();
        foreach (var shape in shapes)
        {
            if (!TryArrowDirection(shape, out var direction)) continue;
            var bounds = ShapeBounds(shape);
            var horizontal = direction is Direction.Left or Direction.Right;
            var primarySpan = horizontal ? bounds.MaxColumn - bounds.MinColumn : bounds.MaxRow - bounds.MinRow;
            if (primarySpan < 2)
            {
                var line = shapes.Where(candidate => StringComparer.OrdinalIgnoreCase.Equals(candidate.Geometry, "line"))
                    .Select(candidate => (Shape: candidate, Bounds: ShapeBounds(candidate)))
                    .Where(item => horizontal
                        ? item.Bounds.MaxColumn - item.Bounds.MinColumn >= 2 && Math.Abs(item.Bounds.CenterRow - bounds.CenterRow) <= 1.5 &&
                          Math.Min(Math.Abs(item.Bounds.MinColumn - bounds.CenterColumn), Math.Abs(item.Bounds.MaxColumn - bounds.CenterColumn)) <= 2
                        : item.Bounds.MaxRow - item.Bounds.MinRow >= 2 && Math.Abs(item.Bounds.CenterColumn - bounds.CenterColumn) <= 1.5 &&
                          Math.Min(Math.Abs(item.Bounds.MinRow - bounds.CenterRow), Math.Abs(item.Bounds.MaxRow - bounds.CenterRow)) <= 2)
                    .OrderBy(item => horizontal
                        ? Math.Min(Math.Abs(item.Bounds.MinColumn - bounds.CenterColumn), Math.Abs(item.Bounds.MaxColumn - bounds.CenterColumn))
                        : Math.Min(Math.Abs(item.Bounds.MinRow - bounds.CenterRow), Math.Abs(item.Bounds.MaxRow - bounds.CenterRow)))
                    .FirstOrDefault();
                if (line.Shape is not null)
                    bounds = new DrawingBounds(
                        Math.Min(bounds.MinColumn, line.Bounds.MinColumn),
                        Math.Min(bounds.MinRow, line.Bounds.MinRow),
                        Math.Max(bounds.MaxColumn, line.Bounds.MaxColumn),
                        Math.Max(bounds.MaxRow, line.Bounds.MaxRow));
            }
            result.Add(new DrawingArrow(bounds.MinColumn, bounds.MinRow, bounds.MaxColumn, bounds.MaxRow, direction));
        }
        return result;
    }

    private static DrawingBounds ShapeBounds(XlsxDrawingShapeRecord shape)
    {
        const double defaultColumnEmu = 171_450d;
        const double defaultRowEmu = 190_500d;
        var minColumn = shape.Column + shape.ColumnOffset / defaultColumnEmu;
        var minRow = shape.Row + shape.RowOffset / defaultRowEmu;
        var maxColumn = shape.ToColumn is { } toColumn && toColumn > shape.Column && shape.ColumnOffset == 0
            ? toColumn
            : minColumn + Math.Max(0.25, shape.WidthEmu / defaultColumnEmu);
        var maxRow = shape.ToRow is { } toRow && toRow > shape.Row && shape.RowOffset == 0
            ? toRow
            : minRow + Math.Max(0.25, shape.HeightEmu / defaultRowEmu);
        return new DrawingBounds(minColumn, minRow, maxColumn, maxRow);
    }

    private static bool TryArrowDirection(XlsxDrawingShapeRecord shape, out Direction direction)
    {
        direction = Direction.Right;
        switch (shape.Geometry.ToLowerInvariant())
        {
            case "rightarrow": direction = shape.FlipHorizontal ? Direction.Left : Direction.Right; return true;
            case "leftarrow": direction = shape.FlipHorizontal ? Direction.Right : Direction.Left; return true;
            case "downarrow": direction = shape.FlipVertical ? Direction.Up : Direction.Down; return true;
            case "uparrow": direction = shape.FlipVertical ? Direction.Down : Direction.Up; return true;
            default: return false;
        }
    }

    private static DiagramProjection? TryCreateFlowchart(XlsxWorksheetRecord worksheet)
    {
        var cells = worksheet.Cells.Where(cell => !string.IsNullOrWhiteSpace(cell.Value)).ToArray();
        var regions = ReadRegions(worksheet, cells);
        var laneRow = regions
            .Where(region => region.MinRow <= 12 && !IsArrow(region.Value) && !IsNote(region.Value))
            .GroupBy(region => region.MinRow)
            .Select(group => new { Row = group.Key, Lanes = group.OrderBy(region => region.MinColumn).ToArray() })
            .Where(group => group.Lanes.Length >= 2)
            .OrderByDescending(group => group.Lanes.Length)
            .ThenBy(group => group.Row)
            .FirstOrDefault();
        if (laneRow is null) return null;

        var lanes = laneRow.Lanes.OrderBy(lane => lane.MinColumn).ToArray();
        var nodes = regions
            .Where(region => region.MinRow > laneRow.Row && !IsArrow(region.Value) && !IsNote(region.Value))
            .OrderBy(region => region.MinRow).ThenBy(region => region.MinColumn)
            .Select(region => new FlowNode("N_" + ColumnName(region.MinColumn) + region.MinRow, region))
            .ToArray();
        if (nodes.Length < 2) return null;

        var output = new StringBuilder("flowchart TD\n");
        for (var laneIndex = 0; laneIndex < lanes.Length; laneIndex++)
        {
            var lane = lanes[laneIndex];
            output.Append("    subgraph L").Append(laneIndex + 1).Append("[\"").Append(Label(lane.Value)).AppendLine("\"]");
            output.AppendLine("        direction TB");
            foreach (var node in nodes.Where(node => ContainsColumn(lane, node.Region.CenterColumn)))
                output.Append("        ").Append(node.Id).AppendLine(FlowShape(node.Region.Value));
            output.AppendLine("    end");
        }

        var arrows = regions.Where(region => region.MinRow > laneRow.Row && IsArrow(region.Value))
            .OrderBy(region => region.MinRow).ThenBy(region => region.MinColumn).ToArray();
        var edgeCount = 0;
        foreach (var arrow in arrows)
        {
            var direction = ArrowDirection(arrow.Value);
            var source = FindDirectionalNode(nodes, arrow, direction, source: true);
            var target = FindDirectionalNode(nodes, arrow, direction, source: false);
            if (source is null || target is null || source.Id == target.Id) continue;
            var edgeLabel = Regex.Replace(arrow.Value, "[←→↑↓▶◀]", string.Empty).Trim();
            output.Append("    ").Append(source.Id).Append(" -->");
            if (edgeLabel.Length > 0) output.Append('|').Append(Label(edgeLabel)).Append('|');
            output.Append(' ').AppendLine(target.Id);
            edgeCount++;
        }

        foreach (var note in regions.Where(region => region.MinRow > laneRow.Row && IsNote(region.Value)))
            output.Append("    %% ").AppendLine(Label(note.Value));
        if (edgeCount == 0) return null;
        var minRow = laneRow.Row;
        var maxRow = nodes.Select(node => node.Region.MaxRow).Concat(arrows.Select(arrow => arrow.MaxRow)).DefaultIfEmpty(minRow).Max();
        return new DiagramProjection("flowchart", output.ToString().TrimEnd(), minRow, maxRow, "xlsx-cell-layout");
    }

    private static DiagramProjection? TryCreateDrawingFlowchart(XlsxWorksheetRecord worksheet, bool useLanes)
    {
        var shapes = worksheet.DrawingShapes ?? [];
        if (shapes.Count == 0) return null;
        var connectedTopology = shapes.Any(shape => shape.IsConnector &&
                                                     shape.StartConnectionId is not null &&
                                                     shape.EndConnectionId is not null);
        var textNodeCount = shapes.Count(shape => IsSemanticShape(shape) && !string.IsNullOrWhiteSpace(shape.Text));
        var nameHint = ContainsAny(worksheet.Name, "フロー", "flow", "システム概要", "システム構成", "構成図", "architecture");
        if (!nameHint && !connectedTopology && textNodeCount < 2) return null;
        var cells = worksheet.Cells.Where(cell => !string.IsNullOrWhiteSpace(cell.Value)).ToArray();
        var regions = ReadRegions(worksheet, cells);
        Region[] lanes = [];
        if (useLanes)
        {
            var laneRow = regions
                .Where(region => region.MinRow <= 12 && region.Value.Length <= 40 && !region.Value.Contains(':') &&
                                 !IsArrow(region.Value) && !IsNote(region.Value) && !SectionHeadingRegex().IsMatch(region.Value.Trim()))
                .GroupBy(region => region.MinRow)
                .Select(group => group.OrderBy(region => region.MinColumn).ToArray())
                .Where(group => group.Length >= 2)
                .OrderByDescending(group => group.Length)
                .ThenBy(group => group[0].MinRow)
                .FirstOrDefault() ?? [];
            lanes = laneRow;
            if (lanes.Length < 2) return null;
        }

        var matched = new List<(XlsxDrawingShapeRecord Shape, Region Region)>();
        foreach (var shape in shapes.Where(IsSemanticShape))
        {
            var bounds = ShapeBounds(shape);
            var shapeText = shape.Text?.Trim();
            if (string.IsNullOrWhiteSpace(shapeText) &&
                (bounds.MaxColumn - bounds.MinColumn > 20 || bounds.MaxRow - bounds.MinRow > 20)) continue;
            var region = !string.IsNullOrWhiteSpace(shapeText)
                ? new Region((int)Math.Floor(bounds.MinColumn), (int)Math.Floor(bounds.MinRow),
                    (int)Math.Ceiling(bounds.MaxColumn), (int)Math.Ceiling(bounds.MaxRow), shapeText)
                : regions.FirstOrDefault(candidate => candidate.MinColumn == shape.Column && candidate.MinRow == shape.Row) ??
                  (bounds.MaxColumn - bounds.MinColumn <= 20 && bounds.MaxRow - bounds.MinRow <= 20
                    ? regions.Where(candidate => candidate.CenterColumn >= bounds.MinColumn - 0.5 &&
                                             candidate.CenterColumn <= bounds.MaxColumn + 0.5 &&
                                             candidate.CenterRow >= bounds.MinRow - 0.5 &&
                                             candidate.CenterRow <= bounds.MaxRow + 0.5 &&
                                             !IsArrow(candidate.Value) && !SectionHeadingRegex().IsMatch(candidate.Value.Trim()))
                    .OrderBy(candidate => Math.Abs(candidate.CenterColumn - bounds.CenterColumn) +
                                          Math.Abs(candidate.CenterRow - bounds.CenterRow))
                    .FirstOrDefault()
                    : null);
            if (region is null || string.IsNullOrWhiteSpace(region.Value)) continue;
            matched.Add((shape, region));
        }
        if (matched.Count < 2) return null;

        var nodes = matched.Select(item => new FlowNode(
                ShapeNodeId(item.Shape),
                item.Region,
                item.Shape.Id))
            .OrderBy(node => node.Region.MinRow).ThenBy(node => node.Region.MinColumn).ToArray();
        var nodeGeometry = matched.ToDictionary(
            item => ShapeNodeId(item.Shape),
            item => item.Shape.Geometry,
            StringComparer.Ordinal);
        var output = new StringBuilder(useLanes ? "flowchart TD\n" : "flowchart LR\n");
        if (useLanes)
        {
            for (var laneIndex = 0; laneIndex < lanes.Length; laneIndex++)
            {
                var lane = lanes[laneIndex];
                output.Append("    subgraph L").Append(laneIndex + 1).Append("[\"").Append(Label(lane.Value)).AppendLine("\"]");
                output.AppendLine("        direction TB");
                foreach (var node in nodes.Where(node => ContainsColumn(lane, node.Region.CenterColumn)))
                    output.Append("        ").Append(node.Id).AppendLine(DrawingFlowShape(node.Region.Value, nodeGeometry[node.Id]));
                output.AppendLine("    end");
            }
        }
        else
        {
            foreach (var node in nodes)
                output.Append("    ").Append(node.Id).AppendLine(DrawingFlowShape(node.Region.Value, nodeGeometry[node.Id]));
        }

        var edges = new HashSet<string>(StringComparer.Ordinal);
        var nodesByShapeId = nodes.Where(node => node.ShapeId is not null)
            .ToDictionary(node => node.ShapeId!, StringComparer.Ordinal);
        foreach (var connector in shapes.Where(shape => shape.IsConnector &&
                                                        shape.StartConnectionId is not null &&
                                                        shape.EndConnectionId is not null))
        {
            if (nodesByShapeId.TryGetValue(connector.StartConnectionId!, out var source) &&
                nodesByShapeId.TryGetValue(connector.EndConnectionId!, out var target) && source.Id != target.Id)
                AppendEdge(source, target, connector.Text?.Trim() ?? string.Empty);
        }
        foreach (var edge in ReadInterfaceEdges(cells, nodes))
            AppendEdge(edge.Source, edge.Target, edge.Label);

        var drawingArrows = ReadDrawingArrows(shapes).OrderBy(arrow => arrow.MinRow).ThenBy(arrow => arrow.MinColumn).ToArray();
        foreach (var arrow in drawingArrows)
        {
            var region = new Region(
                (int)Math.Floor(arrow.MinColumn),
                (int)Math.Floor(arrow.MinRow),
                (int)Math.Ceiling(arrow.MaxColumn),
                (int)Math.Ceiling(arrow.MaxRow),
                string.Empty);
            var source = FindDirectionalNode(nodes, region, arrow.Direction, source: true);
            var target = FindDirectionalNode(nodes, region, arrow.Direction, source: false);
            if (source is null || target is null || source.Id == target.Id) continue;
            var edgeLabel = FindEdgeLabel(regions, nodes, lanes, source, target, region, arrow.Direction);
            AppendEdge(source, target, edgeLabel);
        }
        if (useLanes)
        {
            foreach (var connectorGroup in regions.Where(region => ConnectorRegex().IsMatch(region.Value.Trim()))
                         .GroupBy(region => region.Value.Trim(), StringComparer.Ordinal)
                         .Where(group => group.Count() == 2))
            {
                var ordered = connectorGroup.OrderBy(region => region.CenterRow).ToArray();
                var target = NearestNode(nodes, ordered[0]);
                var source = NearestNode(nodes, ordered[1]);
                if (source is null || target is null || source.Id == target.Id) continue;
                var label = regions.Where(region => region.Value.Trim().Length > 1 && region.Value.Trim().Length <= 30 &&
                                                    Math.Abs(region.CenterRow - ordered[1].CenterRow) <= 4 &&
                                                    Math.Abs(region.CenterColumn - ordered[1].CenterColumn) <= 8)
                    .OrderBy(region => Math.Abs(region.CenterRow - ordered[1].CenterRow) + Math.Abs(region.CenterColumn - ordered[1].CenterColumn))
                    .Select(region => region.Value.Trim()).FirstOrDefault() ?? connectorGroup.Key;
                AppendEdge(source, target, label);
            }
        }
        if (edges.Count == 0) return null;

        var minRow = useLanes ? lanes.Min(lane => lane.MinRow) : nodes.Min(node => node.Region.MinRow);
        var maxRow = nodes.Max(node => node.Region.MaxRow);
        var type = useLanes ? "flowchart" : "architecture";
        return new DiagramProjection(type, output.ToString().TrimEnd(), minRow, maxRow, "xlsx-drawingml+cell-layout");

        void AppendEdge(FlowNode source, FlowNode target, string edgeLabel)
        {
            var edgeKey = source.Id + "\0" + target.Id + "\0" + edgeLabel;
            if (!edges.Add(edgeKey)) return;
            output.Append("    ").Append(source.Id).Append(" -->");
            if (edgeLabel.Length > 0) output.Append('|').Append(Label(edgeLabel)).Append('|');
            output.Append(' ').AppendLine(target.Id);
        }
    }

    private static IReadOnlyList<InterfaceEdge> ReadInterfaceEdges(
        IReadOnlyList<XlsxCellRecord> cells,
        IReadOnlyList<FlowNode> nodes)
    {
        var rows = cells.GroupBy(cell => cell.RowIndex).OrderBy(group => group.Key).ToArray();
        var header = rows.FirstOrDefault(group => group.Any(cell => ContainsAny(cell.Value ?? string.Empty, "I/F ID", "IF ID")) &&
                                                  group.Any(cell => ContainsAny(cell.Value ?? string.Empty, "送信元", "source")) &&
                                                  group.Any(cell => ContainsAny(cell.Value ?? string.Empty, "送信先", "target")));
        if (header is null) return [];
        int Column(params string[] names) => header.FirstOrDefault(cell => ContainsAny(cell.Value ?? string.Empty, names))?.ColumnIndex ?? 0;
        var idColumn = Column("I/F ID", "IF ID");
        var sourceColumn = Column("送信元", "source");
        var targetColumn = Column("送信先", "target");
        var modeColumn = Column("方式", "method", "protocol");
        var result = new List<InterfaceEdge>();
        foreach (var row in rows.Where(group => group.Key > header.Key))
        {
            string Value(int column) => row.FirstOrDefault(cell => cell.ColumnIndex == column)?.Value?.Trim() ?? string.Empty;
            var id = Value(idColumn);
            if (!id.StartsWith("IF-", StringComparison.OrdinalIgnoreCase))
            {
                if (result.Count > 0) break;
                continue;
            }
            var source = MatchEndpointNode(Value(sourceColumn), nodes);
            var target = MatchEndpointNode(Value(targetColumn), nodes);
            if (source is null || target is null || source.Id == target.Id) continue;
            var mode = modeColumn == 0 ? string.Empty : Value(modeColumn);
            result.Add(new InterfaceEdge(source, target, string.IsNullOrWhiteSpace(mode) ? id : id + " " + mode));
        }
        return result;
    }

    private static FlowNode? MatchEndpointNode(string endpoint, IReadOnlyList<FlowNode> nodes) => nodes
        .Select(node => (Node: node, Score: EndpointScore(endpoint, node.Region.Value)))
        .Where(item => item.Score > 0)
        .OrderByDescending(item => item.Score)
        .ThenBy(item => item.Node.Region.MinRow)
        .Select(item => item.Node)
        .FirstOrDefault();

    private static int EndpointScore(string endpoint, string nodeLabel)
    {
        var value = NormalizeEndpoint(endpoint);
        var label = NormalizeEndpoint(nodeLabel);
        if (value.Length == 0) return 0;
        if (StringComparer.OrdinalIgnoreCase.Equals(label, value)) return 120;
        if (label.Contains(value, StringComparison.OrdinalIgnoreCase) || value.Contains(label, StringComparison.OrdinalIgnoreCase)) return 100;

        var endpointTokens = EndpointTokens(endpoint);
        var labelTokens = EndpointTokens(nodeLabel);
        var overlap = endpointTokens.Intersect(labelTokens, StringComparer.OrdinalIgnoreCase).Count();
        if (overlap == 0) return 0;
        return 40 + overlap * 10 - Math.Abs(endpointTokens.Count - labelTokens.Count);
    }

    private static string NormalizeEndpoint(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9一-龠ぁ-んァ-ヶ]", string.Empty);
    private static IReadOnlyList<string> EndpointTokens(string value) => Regex
        .Split(value.ToLowerInvariant().Replace("<<", " ", StringComparison.Ordinal).Replace(">>", " ", StringComparison.Ordinal),
            @"[^a-z0-9一-龠ぁ-んァ-ヶ]+")
        .Where(token => token.Length > 1 && token is not "service" and not "system" and not "システム")
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string FindEdgeLabel(
        IReadOnlyList<Region> regions,
        IReadOnlyList<FlowNode> nodes,
        IReadOnlyList<Region> lanes,
        FlowNode source,
        FlowNode target,
        Region arrow,
        Direction direction)
    {
        var nodeRegions = nodes.Select(node => node.Region).ToHashSet();
        var laneRegions = lanes.ToHashSet();
        var minColumn = Math.Min(source.Region.CenterColumn, target.Region.CenterColumn) - 2;
        var maxColumn = Math.Max(source.Region.CenterColumn, target.Region.CenterColumn) + 2;
        var minRow = Math.Min(source.Region.CenterRow, target.Region.CenterRow) - 2;
        var maxRow = Math.Max(source.Region.CenterRow, target.Region.CenterRow) + 2;
        var vertical = direction is Direction.Up or Direction.Down;
        return regions
            .Where(region => !nodeRegions.Contains(region) && !laneRegions.Contains(region) &&
                             region.Value.Trim().Length is > 1 and <= 60 &&
                             !region.Value.Contains(':') && !SectionHeadingRegex().IsMatch(region.Value.Trim()) &&
                             !nodes.Any(node => ReferenceEquals(node.Region, region)) && !region.Value.Contains("<<", StringComparison.Ordinal) &&
                             region.CenterColumn >= minColumn && region.CenterColumn <= maxColumn &&
                             region.CenterRow >= minRow && region.CenterRow <= maxRow &&
                             (vertical
                                 ? Math.Abs(region.CenterColumn - arrow.CenterColumn) <= 7
                                 : Math.Abs(region.CenterRow - arrow.CenterRow) <= 3))
            .OrderBy(region => Math.Abs(region.CenterColumn - arrow.CenterColumn) + Math.Abs(region.CenterRow - arrow.CenterRow))
            .Select(region => region.Value.Trim())
            .FirstOrDefault() ?? string.Empty;
    }

    private static FlowNode? NearestNode(IReadOnlyList<FlowNode> nodes, Region region) => nodes
        .OrderBy(node => Math.Abs(node.Region.CenterColumn - region.CenterColumn) + Math.Abs(node.Region.CenterRow - region.CenterRow))
        .FirstOrDefault();

    private static bool IsSemanticShape(XlsxDrawingShapeRecord shape) =>
        !shape.IsConnector && shape.WidthEmu >= 250_000 && shape.HeightEmu >= 200_000 && shape.Geometry.ToLowerInvariant() is
            "rect" or "roundrect" or "parallelogram" or "diamond" or "can" or "ellipse";

    private static string ShapeNodeId(XlsxDrawingShapeRecord shape)
    {
        var normalized = Regex.Replace(shape.Id, "[^A-Za-z0-9_]", "_");
        if (normalized.Length == 0) normalized = "shape";
        if (char.IsDigit(normalized[0])) normalized = "S_" + normalized;
        return "N_" + normalized;
    }

    private static string DrawingFlowShape(string value, string geometry)
    {
        var quoted = "\"" + Label(value) + "\"";
        return geometry.ToLowerInvariant() switch
        {
            "diamond" => "{" + quoted + "}",
            "roundrect" => "([" + quoted + "])",
            "ellipse" => "((" + quoted + "))",
            "can" => "[(" + quoted + ")]",
            "parallelogram" => "[/" + quoted + "/]",
            _ => "[" + quoted + "]",
        };
    }

    private static FlowNode? FindDirectionalNode(IReadOnlyList<FlowNode> nodes, Region arrow, Direction direction, bool source)
    {
        IEnumerable<FlowNode> candidates = direction switch
        {
            Direction.Right when source => nodes.Where(node => node.Region.MaxColumn <= arrow.MinColumn + 2),
            Direction.Right => nodes.Where(node => node.Region.MinColumn >= arrow.MaxColumn - 2),
            Direction.Left when source => nodes.Where(node => node.Region.MinColumn >= arrow.MaxColumn - 2),
            Direction.Left => nodes.Where(node => node.Region.MaxColumn <= arrow.MinColumn + 2),
            Direction.Up when source => nodes.Where(node => node.Region.MinRow >= arrow.MaxRow - 3),
            Direction.Up => nodes.Where(node => node.Region.MaxRow <= arrow.MinRow + 2),
            Direction.Down when source => nodes.Where(node => node.Region.MaxRow <= arrow.MinRow + 2),
            _ => nodes.Where(node => node.Region.MinRow >= arrow.MaxRow - 3),
        };
        return candidates.OrderBy(node => DirectionalScore(node.Region, arrow, direction, source)).FirstOrDefault();
    }

    private static double DirectionalScore(Region node, Region arrow, Direction direction, bool source)
    {
        var vertical = direction is Direction.Up or Direction.Down;
        var primaryGap = direction switch
        {
            Direction.Right when source => arrow.MinColumn - node.MaxColumn,
            Direction.Right => node.MinColumn - arrow.MaxColumn,
            Direction.Left when source => node.MinColumn - arrow.MaxColumn,
            Direction.Left => arrow.MinColumn - node.MaxColumn,
            Direction.Up when source => node.MinRow - arrow.MaxRow,
            Direction.Up => arrow.MinRow - node.MaxRow,
            Direction.Down when source => arrow.MinRow - node.MaxRow,
            _ => node.MinRow - arrow.MaxRow,
        };
        var perpendicular = vertical
            ? Math.Abs(node.CenterColumn - arrow.CenterColumn)
            : Math.Abs(node.CenterRow - arrow.CenterRow);
        return perpendicular * 100 + primaryGap;
    }

    private static IReadOnlyList<Region> ReadRegions(XlsxWorksheetRecord worksheet, IReadOnlyList<XlsxCellRecord> cells)
    {
        var byCoordinate = cells.ToDictionary(cell => (cell.RowIndex, cell.ColumnIndex));
        var merged = new List<Region>();
        foreach (var reference in worksheet.MergedRanges ?? [])
        {
            if (!TryParseRange(reference, out var minColumn, out var minRow, out var maxColumn, out var maxRow)) continue;
            if (!byCoordinate.TryGetValue((minRow, minColumn), out var cell) || string.IsNullOrWhiteSpace(cell.Value)) continue;
            merged.Add(new(minColumn, minRow, maxColumn, maxRow, cell.Value!));
        }

        var result = new List<Region>(merged);
        foreach (var cell in cells)
        {
            if (merged.Any(region => region.Contains(cell.ColumnIndex, cell.RowIndex))) continue;
            result.Add(new(cell.ColumnIndex, cell.RowIndex, cell.ColumnIndex, cell.RowIndex, cell.Value!));
        }
        return result;
    }

    private static bool TryParseRange(string reference, out int minColumn, out int minRow, out int maxColumn, out int maxRow)
    {
        minColumn = minRow = maxColumn = maxRow = 0;
        var match = Regex.Match(reference, @"^\$?(?<c1>[A-Za-z]+)\$?(?<r1>[1-9][0-9]*):\$?(?<c2>[A-Za-z]+)\$?(?<r2>[1-9][0-9]*)$");
        if (!match.Success || !int.TryParse(match.Groups["r1"].Value, out minRow) || !int.TryParse(match.Groups["r2"].Value, out maxRow)) return false;
        minColumn = ColumnNumber(match.Groups["c1"].Value);
        maxColumn = ColumnNumber(match.Groups["c2"].Value);
        return minColumn > 0 && maxColumn >= minColumn && maxRow >= minRow;
    }

    private static bool IsArrow(string value) => value.IndexOfAny(ArrowCharacters) >= 0 &&
        Regex.Replace(value, "[←→↑↓▶◀─━—\\-<>\\s]", string.Empty).Length <= 8;
    private static bool IsHorizontalArrow(string value) => value.Contains('▶') || value.Contains('◀') ||
        value.Contains('←') || value.Contains('→') || value.Contains("->", StringComparison.Ordinal) || value.Contains("<-", StringComparison.Ordinal);
    private static bool IsLifeline(string value)
    {
        var normalized = Regex.Replace(value, @"\s", string.Empty);
        return normalized.Length is > 0 and <= 3 && normalized.All(character => character is '┆' or '│' or '¦' or '|');
    }
    private static bool IsNote(string value) => ContainsAny(value.TrimStart(), "注記", "備考", "代替：", "代替:", "いいえ：", "いいえ:");
    private static bool HasSequenceEvidence(XlsxWorksheetRecord worksheet)
    {
        var values = worksheet.Cells.Select(cell => cell.Value ?? string.Empty).Where(value => value.Length > 0).ToArray();
        var horizontalArrows = values.Count(IsHorizontalArrow) +
                               (worksheet.DrawingShapes ?? []).Count(shape =>
                                   TryArrowDirection(shape, out var direction) && direction is Direction.Left or Direction.Right);
        var lifelines = values.Count(IsLifeline) + (worksheet.DrawingShapes ?? []).Count(shape =>
        {
            var bounds = ShapeBounds(shape);
            return StringComparer.OrdinalIgnoreCase.Equals(shape.Geometry, "line") &&
                   bounds.MaxRow - bounds.MinRow >= 5 && bounds.MaxRow - bounds.MinRow >= (bounds.MaxColumn - bounds.MinColumn) * 3;
        });
        var nameHint = ContainsAny(worksheet.Name, "シーケンス", "sequence");
        return horizontalArrows > 0 && values.Count(value => NumberedMessageRegex().IsMatch(value.Trim())) >= 2 &&
               (nameHint || lifelines >= 2);
    }

    private static bool HasGridFlowEvidence(XlsxWorksheetRecord worksheet)
    {
        if (ContainsAny(worksheet.Name, "フロー", "flow", "swimlane", "スイムレーン")) return true;
        // Prose such as "approval -> accounting" is not diagram topology.  Only
        // count cells whose contents are effectively connector glyphs here.
        var arrowCount = worksheet.Cells.Count(cell => IsGridConnector(cell.Value ?? string.Empty));
        return arrowCount >= 2 && (worksheet.MergedRanges?.Count ?? 0) >= 4;
    }
    private static bool IsGridConnector(string value) => value.IndexOfAny(ArrowCharacters) >= 0 &&
        Regex.Replace(value, "[←→↑↓▶◀─━—\\-<>\\s]", string.Empty).Length <= 2;
    private static Direction ArrowDirection(string value) => value.Contains('←') || value.Contains('◀') ? Direction.Left :
        value.Contains('→') || value.Contains('▶') ? Direction.Right : value.Contains('↑') ? Direction.Up : Direction.Down;
    private static bool ContainsColumn(Region lane, double column) => column >= lane.MinColumn && column <= lane.MaxColumn;
    private static bool ContainsAny(string value, params string[] candidates) => candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    private static string Label(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "<br/>", StringComparison.Ordinal)
        .Replace("<<", "«", StringComparison.Ordinal).Replace(">>", "»", StringComparison.Ordinal)
        .Replace("  ", " ", StringComparison.Ordinal).Replace("\"", "'", StringComparison.Ordinal).Trim().Replace(" <br/> ", "<br/>", StringComparison.Ordinal);

    private static string FlowShape(string value)
    {
        var label = Label(value).Replace("◇", string.Empty, StringComparison.Ordinal).Trim();
        var quoted = "\"" + label + "\"";
        if (value.Contains('？') || value.Contains('?') || value.Contains('◇')) return "{" + quoted + "}";
        if (value.TrimStart().StartsWith("開始", StringComparison.Ordinal) || value.TrimStart().StartsWith("終了", StringComparison.Ordinal)) return "([" + quoted + "])";
        return "[" + quoted + "]";
    }

    private static int ColumnNumber(string name)
    {
        var result = 0;
        foreach (var character in name.ToUpperInvariant()) result = checked(result * 26 + character - 'A' + 1);
        return result;
    }

    private static string ColumnName(int number)
    {
        var result = new StringBuilder();
        while (number > 0)
        {
            number--;
            result.Insert(0, (char)('A' + number % 26));
            number /= 26;
        }
        return result.ToString();
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static Regex SectionHeadingRegex() => SectionHeadingPattern;
    private static Regex NumberedMessageRegex() => NumberedMessagePattern;
    private static Regex StateLabelRegex() => StateLabelPattern;
    private static Regex ConnectorRegex() => ConnectorPattern;
    private static Regex FragmentRegex() => FragmentPattern;

    private static string StateId(string value)
    {
        var normalized = Regex.Replace(value.Trim(), "[^A-Za-z0-9_]", "_");
        if (normalized.Length == 0) normalized = "UNKNOWN";
        if (char.IsDigit(normalized[0])) normalized = "_" + normalized;
        return "S_" + normalized;
    }

    private enum Direction { Left, Right, Up, Down }
    private sealed record DiagramProjection(string Type, string Mermaid, int MinRow, int MaxRow, string Source);
    private sealed record StateTransition(string Source, string Target, string Event, string Guard);
    private sealed record InterfaceEdge(FlowNode Source, FlowNode Target, string Label);
    private sealed record SequenceLine(int Row, int Column, string? Text);
    private sealed record SequenceEvent(int Row, int Priority, string Text);
    private sealed record FragmentBranch(int Row, string Guard);
    private sealed record SequenceFragment(
        string Kind,
        string Guard,
        int StartRow,
        int EndRow,
        int MinColumn,
        int MaxColumn,
        IReadOnlyList<FragmentBranch> Branches);
    private sealed record DrawingArrow(double MinColumn, double MinRow, double MaxColumn, double MaxRow, Direction Direction)
    {
        public double CenterColumn => (MinColumn + MaxColumn) / 2d;
        public double CenterRow => (MinRow + MaxRow) / 2d;
    }
    private sealed record DrawingBounds(double MinColumn, double MinRow, double MaxColumn, double MaxRow)
    {
        public double CenterColumn => (MinColumn + MaxColumn) / 2d;
        public double CenterRow => (MinRow + MaxRow) / 2d;
    }
    private sealed record FlowNode(string Id, Region Region, string? ShapeId = null);
    private sealed record Region(int MinColumn, int MinRow, int MaxColumn, int MaxRow, string Value)
    {
        public double CenterColumn => (MinColumn + MaxColumn) / 2d;
        public double CenterRow => (MinRow + MaxRow) / 2d;
        public bool Contains(int column, int row) => column >= MinColumn && column <= MaxColumn && row >= MinRow && row <= MaxRow;
    }
}
