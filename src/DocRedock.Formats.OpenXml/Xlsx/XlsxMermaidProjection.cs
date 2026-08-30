using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;
using DocRedock.VisualInference;

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

    public static IReadOnlyList<DocumentNode> TryCreateAll(XlsxWorksheetRecord worksheet, int order,
        TimeSpan? inferenceTimeout = null, CancellationToken cancellationToken = default)
    {
        var projection = TryCreate(worksheet, order, inferenceTimeout, cancellationToken);
        if (projection is null) return [];
        if (projection.Extensions is null || !projection.Extensions.TryGetValue("visual_graph", out var raw)) return [projection];
        var visual = raw.Deserialize<VisualGraph>();
        if (visual is null || visual.Nodes.Count < 2 || !visual.Edges.Any(edge => edge.SourceId is not null && edge.TargetId is not null)) return [projection];
        var components = ConnectedComponents(visual)
            .Where(ids => visual.Edges.Any(edge => edge.SourceId is not null && edge.TargetId is not null &&
                ids.Contains(edge.SourceId) && ids.Contains(edge.TargetId)))
            .ToArray();
        if (components.Length <= 1) return [projection];
        var result = new List<DocumentNode>();
        var componentSources = components.Select(nodeIds =>
        {
            var edgeIds = visual.Edges.Where(edge => edge.SourceId is not null && edge.TargetId is not null &&
                    nodeIds.Contains(edge.SourceId) && nodeIds.Contains(edge.TargetId))
                .Select(edge => edge.SourceNodeId).Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>();
            return visual.Nodes.Where(node => nodeIds.Contains(node.Id))
                .Select(node => node.SourceNodeId).Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>()
                .Concat(edgeIds).ToHashSet(StringComparer.Ordinal);
        }).ToArray();
        bool OwnedBy(VisualSourceItem item, int componentIndex)
        {
            var nodeIds = components[componentIndex];
            var edgeIds = visual.Edges.Where(edge => edge.SourceId is not null && edge.TargetId is not null &&
                    nodeIds.Contains(edge.SourceId) && nodeIds.Contains(edge.TargetId))
                .Select(edge => edge.Id).ToHashSet(StringComparer.Ordinal);
            return item.ProjectedNodeId is not null && nodeIds.Contains(item.ProjectedNodeId) ||
                   item.ProjectedEdgeId is not null && edgeIds.Contains(item.ProjectedEdgeId) ||
                   item.SourceAnchor?.Locators.Any(locator => locator.Kind == "shape_id" &&
                       componentSources[componentIndex].Contains(locator.Value)) == true;
        }
        var unownedSourceItemIds = (visual.SourceItems ?? [])
            .Where(item => !Enumerable.Range(0, components.Length).Any(index => OwnedBy(item, index)))
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (nodeIds, index) in components.Select((ids, index) => (ids, index)))
        {
            var componentEdges = visual.Edges.Where(edge => edge.SourceId is not null && edge.TargetId is not null &&
                nodeIds.Contains(edge.SourceId) && nodeIds.Contains(edge.TargetId)).ToArray();
            var componentItems = (visual.SourceItems ?? []).Where(item => OwnedBy(item, index) ||
                index == 0 && unownedSourceItemIds.Contains(item.Id)).ToArray();
            var componentPathIds = componentItems.Where(item => item.FallbackPathId is not null)
                .Select(item => item.FallbackPathId!).ToHashSet(StringComparer.Ordinal);
            var componentDiagnostics = (visual.Diagnostics ?? []).Where(diagnostic =>
                diagnostic.SourceObjectId is not null && componentSources[index].Contains(diagnostic.SourceObjectId) ||
                index == 0 && (diagnostic.SourceObjectId is null ||
                    !componentSources.Any(sourceIds => sourceIds.Contains(diagnostic.SourceObjectId)))).ToArray();
            var graph = visual with
            {
                Id = visual.Id + ":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Nodes = visual.Nodes.Where(node => nodeIds.Contains(node.Id)).ToArray(),
                Edges = componentEdges,
                Paths = (visual.Paths ?? []).Where(path => componentPathIds.Contains(path.Id)).ToArray(),
                Diagnostics = componentDiagnostics,
                SourceItems = componentItems,
            };
            graph = graph with { Quality = VisualGraphValidator.ComputeQuality(graph) };
            var mermaid = RenderComponentMermaid(graph);
            var extensions = new Dictionary<string, JsonElement>(projection.Extensions, StringComparer.Ordinal)
            {
                ["visual_graph"] = JsonSerializer.SerializeToElement(graph),
                ["diagram_cluster_id"] = JsonSerializer.SerializeToElement(graph.Id)
            };
            result.Add(projection with
            {
                Id = "n_" + Hash(worksheet.Name + "!mermaid!" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))[..16],
                Order = order + index,
                Content = new TextNodeContent(mermaid),
                Extensions = extensions
            });
        }
        return result;
    }

    private static IReadOnlyList<HashSet<string>> ConnectedComponents(VisualGraph graph)
    {
        var parent = graph.Nodes.ToDictionary(node => node.Id, node => node.Id, StringComparer.Ordinal);
        string Find(string id) { while (parent[id] != id) { parent[id] = parent[parent[id]]; id = parent[id]; } return id; }
        void Union(string left, string right) { if (!parent.ContainsKey(left) || !parent.ContainsKey(right)) return; var a = Find(left); var b = Find(right); if (a != b) parent[b] = a; }
        foreach (var edge in graph.Edges) if (edge.SourceId is not null && edge.TargetId is not null) Union(edge.SourceId, edge.TargetId);
        return parent.Keys.GroupBy(Find, StringComparer.Ordinal).OrderBy(group => group.Min(StringComparer.Ordinal), StringComparer.Ordinal).Select(group => group.ToHashSet(StringComparer.Ordinal)).ToArray();
    }

    private static string RenderComponentMermaid(VisualGraph graph)
    {
        var output = new StringBuilder("flowchart ")
            .Append(graph.Direction is "TD" ? "TD" : "LR").AppendLine();
        foreach (var node in graph.Nodes.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var quoted = "\"" + Label(node.Label) + "\"";
            var shape = node.Kind switch
            {
                VisualNodeKind.Decision => "{" + quoted + "}",
                VisualNodeKind.Terminator => "([" + quoted + "])",
                VisualNodeKind.Data => "[/" + quoted + "/]",
                _ => "[" + quoted + "]",
            };
            output.Append("    ").Append(node.Id).Append(shape).AppendLine();
        }
        foreach (var edge in graph.Edges.Where(item => item.SourceId is not null && item.TargetId is not null)
                     .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var arrow = edge.IsUndirected ? " ---" : " -->";
            output.Append("    ").Append(edge.SourceId).Append(arrow);
            if (!string.IsNullOrWhiteSpace(edge.Label))
                output.Append('|').Append(Label(edge.Label!)).Append('|');
            output.Append(' ').Append(edge.TargetId).AppendLine();
        }
        return output.ToString().TrimEnd();
    }

    public static DocumentNode? TryCreate(XlsxWorksheetRecord worksheet, int order,
        TimeSpan? inferenceTimeout = null, CancellationToken cancellationToken = default)
    {
        // Content and DrawingML topology are authoritative.  The sheet name is only a
        // tie-breaker so ordinary workbooks do not need project-specific naming rules.
        var hasSequenceEvidence = HasSequenceEvidence(worksheet);
        var prefersLanes = ContainsAny(worksheet.Name, "フロー", "flow", "swimlane", "スイムレーン");
        DiagramProjection? projection = TryCreateStateDiagram(worksheet);
        if (projection is null && hasSequenceEvidence) projection = TryCreateSequence(worksheet);
        if (projection is null && prefersLanes) projection = TryCreateDrawingFlowchart(worksheet, useLanes: true, inferenceTimeout, cancellationToken);
        projection ??= TryCreateDrawingFlowchart(worksheet, useLanes: false, inferenceTimeout, cancellationToken);
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
        if (projection.VisualGraph is { } visualGraph)
            extensions["visual_graph"] = JsonSerializer.SerializeToElement(visualGraph);
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
        var arrowAssignments = MatchSequenceArrows(messageCells, worksheet.DrawingShapes ?? [], worksheet.Metrics);
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
        AppendSequenceTimeline(output, actors, regions, messages, notes, worksheet.DrawingShapes ?? [], worksheet.Metrics);
        var maxRow = messageCells.Select(cell => cell.RowIndex)
            .Concat(notes.Select(note => note.MaxRow))
            .Concat(ReadSequenceFragments(regions, worksheet.DrawingShapes ?? [], worksheet.Metrics).Select(fragment => fragment.EndRow))
            .Concat((worksheet.DrawingShapes ?? []).Where(IsActivationShape).Select(shape => (int)Math.Ceiling(ShapeBounds(shape, worksheet.Metrics).MaxRow)))
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
        IReadOnlyList<XlsxDrawingShapeRecord> shapes,
        XlsxWorksheetMetrics? metrics)
    {
        var events = new List<SequenceEvent>();
        var fragments = ReadSequenceFragments(regions, shapes, metrics);
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
            var bounds = ShapeBounds(shape, metrics);
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
        IReadOnlyList<XlsxDrawingShapeRecord> shapes,
        XlsxWorksheetMetrics? metrics)
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
                .Select(shape => ShapeBounds(shape, null))
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
        IReadOnlyList<XlsxDrawingShapeRecord> shapes,
        XlsxWorksheetMetrics? metrics)
    {
        var available = ReadDrawingArrows(shapes, metrics)
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

    private static IReadOnlyList<DrawingArrow> ReadDrawingArrows(IReadOnlyList<XlsxDrawingShapeRecord> shapes, XlsxWorksheetMetrics? metrics)
    {
        var result = new List<DrawingArrow>();
        foreach (var shape in shapes)
        {
            if (!TryArrowDirection(shape, out var direction)) continue;
            var bounds = ShapeBounds(shape, metrics);
            var horizontal = direction is Direction.Left or Direction.Right;
            var primarySpan = horizontal ? bounds.MaxColumn - bounds.MinColumn : bounds.MaxRow - bounds.MinRow;
            if (primarySpan < 2)
            {
                var line = shapes.Where(candidate => StringComparer.OrdinalIgnoreCase.Equals(candidate.Geometry, "line"))
                    .Select(candidate => (Shape: candidate, Bounds: ShapeBounds(candidate, metrics)))
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
            result.Add(new DrawingArrow(bounds.MinColumn, bounds.MinRow, bounds.MaxColumn, bounds.MaxRow, direction, shape.Id));
        }
        return result;
    }

    private static DrawingBounds ShapeBounds(XlsxDrawingShapeRecord shape, XlsxWorksheetMetrics? metrics = null)
    {
        if (shape.AbsoluteBounds is { } absolute && metrics is not null)
        {
            var absoluteMinColumn = metrics.ColumnFromEmu(absolute.XEmu);
            var absoluteMinRow = metrics.RowFromEmu(absolute.YEmu);
            var absoluteMaxColumn = metrics.ColumnFromEmu(absolute.RightEmu);
            var absoluteMaxRow = metrics.RowFromEmu(absolute.BottomEmu);
            return new DrawingBounds(absoluteMinColumn, absoluteMinRow, Math.Max(absoluteMinColumn + 0.01, absoluteMaxColumn), Math.Max(absoluteMinRow + 0.01, absoluteMaxRow));
        }
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

    private static DiagramProjection? TryCreateDrawingFlowchart(XlsxWorksheetRecord worksheet, bool useLanes,
        TimeSpan? inferenceTimeout, CancellationToken cancellationToken)
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
        var regions = ReadRegions(worksheet, cells)
            .Concat(shapes.Where(shape => !string.IsNullOrWhiteSpace(shape.Text) && shape.AbsoluteBounds is not null &&
                string.Equals(shape.Geometry, "rect", StringComparison.OrdinalIgnoreCase))
                .Select(shape => new Region(shape.Column, shape.Row, shape.ToColumn ?? shape.Column + 1,
                    shape.ToRow ?? shape.Row + 1, shape.Text!.Trim())))
            .ToArray();
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
            var bounds = ShapeBounds(shape, worksheet.Metrics);
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
        var visualEdges = new List<VisualEdge>();
        var visualEdgeSourceIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var visualDiagnostics = new List<VisualDiagnostic>();
        var visualLabels = new List<(string Id, string Value, SourceAnchor Anchor)>();
        var nodesByShapeId = nodes.Where(node => node.ShapeId is not null)
            .ToDictionary(node => node.ShapeId!, StringComparer.Ordinal);
        foreach (var connector in shapes.Where(shape => shape.IsConnector &&
                                                        shape.StartConnectionId is not null &&
                                                        shape.EndConnectionId is not null))
        {
            if (nodesByShapeId.TryGetValue(connector.StartConnectionId!, out var source) &&
                nodesByShapeId.TryGetValue(connector.EndConnectionId!, out var target) && source.Id != target.Id)
                AppendEdge(source, target, connector.Text?.Trim() ?? string.Empty, connector, VisualEdgeResolution.NativeConnection);
            else
            {
                var edgeId = "e_" + visualEdges.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                visualEdges.Add(new VisualEdge(edgeId, null, null, connector.Text?.Trim(),
                    VisualEdgeResolution.Unresolved, connector.Id, Direction: "directed", Confidence: 0,
                    SourceAnchor: ShapeAnchor(worksheet, connector), EdgeDirection: VisualEdgeDirection.Directed));
                visualEdgeSourceIds[connector.Id] = edgeId;
                visualDiagnostics.Add(new VisualDiagnostic("VisualConnectorUnresolved",
                    "An XLSX connector could not be uniquely associated with source and target shapes.", connector.Id,
                    Fallback: "connector retained as diagnostic-only source item",
                    Remedy: "connect the connector to two distinct shapes", Format: "xlsx",
                    PartUri: worksheet.PartUri, PartitionId: worksheet.Name,
                    SourceObjectId: connector.Id, SourceObjectType: "connector", Confidence: 0));
            }
        }
        foreach (var edge in ReadInterfaceEdges(cells, nodes))
            AppendEdge(edge.Source, edge.Target, edge.Label, null, VisualEdgeResolution.LayoutInferred);

        var drawingArrows = ReadDrawingArrows(shapes, worksheet.Metrics).OrderBy(arrow => arrow.MinRow).ThenBy(arrow => arrow.MinColumn).ToArray();
        var visualDocument = BuildDrawingPrimitiveDocument(worksheet, nodes, drawingArrows, shapes);
        var visualClusters = new DiagramClusterer().Cluster(visualDocument);
        SoftConnectionResult inference;
        try
        {
            using var inferenceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (inferenceTimeout is { } timeout)
            {
                if (timeout <= TimeSpan.Zero) inferenceCts.Cancel();
                else inferenceCts.CancelAfter(timeout);
            }
            inference = new SoftConnectionEngine().Infer(visualDocument, visualClusters,
                new SoftConnectionOptions(VisualInferenceContext.Current), inferenceCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            inference = new SoftConnectionResult([], [], [], Diagnostics:
                [new VisualExtractionDiagnostic("VisualInferenceTimeout",
                    "XLSX visual inference exceeded its configured time budget; all directional shapes remain fallback geometry.", worksheet.Name)]);
        }
        visualDiagnostics.AddRange((inference.Diagnostics ?? []).Select(item => new VisualDiagnostic(
            item.Code, item.Message, item.PrimitiveId,
            Fallback: item.Code == "VisualInferenceTimeout" ? "directional shapes retained as visual fallback" : null,
            Remedy: item.Code == "VisualInferenceTimeout" ? "increase VisualInferenceTimeout or simplify the diagram" : null,
            Format: "xlsx", PartUri: worksheet.PartUri, PartitionId: worksheet.Name,
            SourceObjectId: item.PrimitiveId, SourceObjectType: "visual-primitive", Confidence: 0)));
        var flowNodesById = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var pair in inference.Resolved)
        {
            if (!flowNodesById.TryGetValue(pair.SourceId ?? string.Empty, out var source) ||
                !flowNodesById.TryGetValue(pair.TargetId ?? string.Empty, out var target)) continue;
            var arrowShape = shapes.FirstOrDefault(shape => StringComparer.Ordinal.Equals(shape.Id, pair.ConnectorId));
            var arrow = drawingArrows.FirstOrDefault(item => item.SourceShapeId == pair.ConnectorId);
            var edgeLabel = arrow is null ? string.Empty : FindEdgeLabel(regions, nodes, lanes, source, target,
                new Region((int)Math.Floor(arrow.MinColumn), (int)Math.Floor(arrow.MinRow),
                    (int)Math.Ceiling(arrow.MaxColumn), (int)Math.Ceiling(arrow.MaxRow), string.Empty), arrow.Direction);
            AppendEdge(source, target, edgeLabel, arrowShape, VisualEdgeResolution.LayoutInferred, pair);
        }
        foreach (var pair in inference.Unresolved)
        {
            var edgeId = "e_" + visualEdges.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            visualEdges.Add(new VisualEdge(edgeId, null, null, null, VisualEdgeResolution.Unresolved, pair.ConnectorId,
                Direction: "directed", Confidence: 0, EdgeDirection: VisualEdgeDirection.Directed,
                Evidence: new VisualConnectionEvidence("xlsx-directional-geometry", "Unresolved", pair.Score,
                    ClusterId: pair.ClusterId, RejectedCandidateIds: pair.RejectedCandidateIds)));
            visualEdgeSourceIds[pair.ConnectorId] = edgeId;
            visualDiagnostics.Add(new VisualDiagnostic("VisualConnectorUnresolved",
                "An XLSX directional shape could not be uniquely associated with source and target shapes.", pair.ConnectorId,
                Fallback: "directional shape retained as visual fallback", Remedy: "separate the arrow from equidistant candidate nodes",
                Format: "xlsx", PartUri: worksheet.PartUri, PartitionId: worksheet.Name,
                SourceObjectId: pair.ConnectorId, SourceObjectType: "directional-shape", Confidence: 0));
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
                AppendEdge(source, target, label, null, VisualEdgeResolution.LayoutInferred);
            }
        }
        if (edges.Count == 0 && drawingArrows.Length == 0) return null;
        if (edges.Count == 0 && drawingArrows.Length > 0)
            output.Append("    %% unresolved directional shapes: ").AppendLine(drawingArrows.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var minRow = useLanes ? lanes.Min(lane => lane.MinRow) : nodes.Min(node => node.Region.MinRow);
        var maxRow = nodes.Max(node => node.Region.MaxRow);
        var type = useLanes ? "flowchart" : "architecture";
        var visualGraph = BuildDrawingVisualGraph(worksheet, shapes, nodes, visualEdges, visualEdgeSourceIds,
            visualDiagnostics, visualLabels);
        return new DiagramProjection(type, output.ToString().TrimEnd(), minRow, maxRow, "xlsx-drawingml+cell-layout", visualGraph);

        void AppendEdge(FlowNode source, FlowNode target, string edgeLabel, XlsxDrawingShapeRecord? sourceShape,
            VisualEdgeResolution resolution, ConnectionPairCandidate? inferredPair = null)
        {
            var edgeKey = source.Id + "\0" + target.Id + "\0" + edgeLabel;
            if (!edges.Add(edgeKey)) return;
            output.Append("    ").Append(source.Id).Append(" -->");
            if (edgeLabel.Length > 0) output.Append('|').Append(Label(edgeLabel)).Append('|');
            output.Append(' ').AppendLine(target.Id);
            var edgeId = "e_" + visualEdges.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            visualEdges.Add(new VisualEdge(edgeId, source.Id, target.Id,
                string.IsNullOrWhiteSpace(edgeLabel) ? null : edgeLabel, resolution,
                sourceShape?.Id, Direction: "directed",
                Confidence: inferredPair is not null ? inferredPair.Score : resolution == VisualEdgeResolution.NativeConnection ? 1d : 0.99d,
                SourceAnchor: sourceShape is null ? null : ShapeAnchor(worksheet, sourceShape),
                EdgeDirection: VisualEdgeDirection.Directed,
                Evidence: inferredPair is not null
                    ? new VisualConnectionEvidence("xlsx-shared-engine", inferredPair.Confidence.ToString(), inferredPair.Score,
                        ClusterId: inferredPair.ClusterId)
                    : resolution == VisualEdgeResolution.NativeConnection
                        ? new VisualConnectionEvidence("native-connection", "Native", 1,
                            ArrowheadEvidence: "end", ClusterId: worksheet.Name)
                        : new VisualConnectionEvidence(sourceShape is null ? "xlsx-cell-layout" : "xlsx-directional-geometry",
                            "High", .99, ArrowheadEvidence: "end", ClusterId: worksheet.Name)));
            if (sourceShape is not null)
                visualEdgeSourceIds[sourceShape.Id] = edgeId;
            if (!string.IsNullOrWhiteSpace(edgeLabel))
                visualLabels.Add(("label:" + visualLabels.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), edgeLabel,
                    sourceShape is null ? new SourceAnchor("xlsx", worksheet.PartUri,
                        [new AnchorLocator("diagram_edge_label", visualLabels.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))])
                        : ShapeAnchor(worksheet, sourceShape)));
        }
    }

    private static VisualPrimitiveDocument BuildDrawingPrimitiveDocument(
        XlsxWorksheetRecord worksheet, IReadOnlyList<FlowNode> nodes, IReadOnlyList<DrawingArrow> arrows,
        IReadOnlyList<XlsxDrawingShapeRecord> shapes)
    {
        var canvasId = worksheet.Name;
        var nodePrimitives = nodes.Where(node =>
        {
            var candidate = shapes.FirstOrDefault(shape => shape.Id == node.ShapeId);
            if (candidate?.AbsoluteBounds is not { } nodeBounds || string.IsNullOrWhiteSpace(candidate.Text)) return true;
            return !arrows.Any(arrow =>
            {
                var arrowShape = shapes.FirstOrDefault(shape => shape.Id == arrow.SourceShapeId);
                if (arrowShape?.AbsoluteBounds is not { } arrowBounds) return false;
                return nodeBounds.XEmu < arrowBounds.RightEmu && arrowBounds.XEmu < nodeBounds.RightEmu &&
                    nodeBounds.YEmu < arrowBounds.BottomEmu && arrowBounds.YEmu < nodeBounds.BottomEmu;
            });
        }).Select(node =>
        {
            var shape = shapes.FirstOrDefault(candidate => candidate.Id == node.ShapeId);
            var bounds = shape?.AbsoluteBounds;
            var rect = bounds is null ? new VisualRect(node.Region.MinColumn, node.Region.MinRow,
                Math.Max(1, node.Region.MaxColumn - node.Region.MinColumn), Math.Max(1, node.Region.MaxRow - node.Region.MinRow))
                : new VisualRect(bounds.XEmu, bounds.YEmu, bounds.WidthEmu, bounds.HeightEmu);
            return (VisualPrimitive)new VisualNodePrimitive(node.Id, canvasId,
                new SourceAnchor("xlsx", worksheet.PartUri, [new AnchorLocator("shape_id", node.ShapeId ?? node.Id)]),
                rect, PrimitiveBoundary(shape?.Geometry), Text: node.Region.Value);
        }).ToList();
        foreach (var arrow in arrows)
        {
            var shape = shapes.FirstOrDefault(candidate => candidate.Id == arrow.SourceShapeId);
            if (shape?.AbsoluteBounds is not { } bounds) continue;
            var start = arrow.Direction switch
            {
                Direction.Left => new VisualPoint(bounds.RightEmu, bounds.YEmu + bounds.HeightEmu / 2),
                Direction.Up => new VisualPoint(bounds.XEmu + bounds.WidthEmu / 2, bounds.BottomEmu),
                Direction.Down => new VisualPoint(bounds.XEmu + bounds.WidthEmu / 2, bounds.YEmu),
                _ => new VisualPoint(bounds.XEmu, bounds.YEmu + bounds.HeightEmu / 2)
            };
            var end = arrow.Direction switch
            {
                Direction.Left => new VisualPoint(bounds.XEmu, bounds.YEmu + bounds.HeightEmu / 2),
                Direction.Up => new VisualPoint(bounds.XEmu + bounds.WidthEmu / 2, bounds.YEmu),
                Direction.Down => new VisualPoint(bounds.XEmu + bounds.WidthEmu / 2, bounds.BottomEmu),
                _ => new VisualPoint(bounds.RightEmu, bounds.YEmu + bounds.HeightEmu / 2)
            };
            var path = new VisualConnectorPath([start, end], EndArrowhead: new ArrowheadEvidence(true, Kind: "preset", Confidence: 1));
            nodePrimitives.Add(new VisualConnectorPrimitive(arrow.SourceShapeId, canvasId,
                new SourceAnchor("xlsx", worksheet.PartUri, [new AnchorLocator("shape_id", arrow.SourceShapeId)]), path));
        }
        var maxX = nodePrimitives.SelectMany(primitive => primitive.Bounds is { } rect ? new[] { rect.Right } : Array.Empty<double>()).DefaultIfEmpty(1).Max();
        var maxY = nodePrimitives.SelectMany(primitive => primitive.Bounds is { } rect ? new[] { rect.Bottom } : Array.Empty<double>()).DefaultIfEmpty(1).Max();
        return new VisualPrimitiveDocument("xlsx:" + worksheet.Name, DocumentFormatKind.Xlsx,
            [new VisualCanvas(canvasId, worksheet.PartUri, worksheet.Name, Math.Max(1, maxX), Math.Max(1, maxY), "emu")], nodePrimitives);
    }

    private static VisualBoundaryKind PrimitiveBoundary(string? geometry) => geometry?.ToLowerInvariant() switch
    {
        "ellipse" => VisualBoundaryKind.Ellipse,
        "diamond" or "flowchartdecision" => VisualBoundaryKind.Diamond,
        "roundrect" or "flowchartterminator" => VisualBoundaryKind.RoundedRectangle,
        "parallelogram" or "flowchartdata" or "flowchartmanualinput" => VisualBoundaryKind.Parallelogram,
        _ => VisualBoundaryKind.Rectangle,
    };

    private static SourceAnchor ShapeAnchor(XlsxWorksheetRecord worksheet, XlsxDrawingShapeRecord shape) =>
        new("xlsx", shape.DrawingPartUri ?? worksheet.PartUri,
            [new AnchorLocator("shape_id", shape.Id), new AnchorLocator("anchor_index", shape.AnchorIndex.ToString(System.Globalization.CultureInfo.InvariantCulture))]);

    private static string SafeVisualId(string value)
    {
        var normalized = Regex.Replace(value, "[^A-Za-z0-9_]", "_");
        return normalized.Length == 0 ? "shape" : normalized;
    }

    private static Geometry? ShapeGeometry(XlsxDrawingShapeRecord? shape)
    {
        if (shape?.AbsoluteBounds is not { } bounds) return null;
        return new Geometry("xlsx-emu", bounds.XEmu, bounds.YEmu, bounds.WidthEmu, bounds.HeightEmu);
    }

    private static IReadOnlyList<VisualPathPoint> RectanglePath(Geometry geometry) =>
        [new(geometry.X, geometry.Y), new(geometry.X + geometry.Width, geometry.Y),
         new(geometry.X + geometry.Width, geometry.Y + geometry.Height), new(geometry.X, geometry.Y + geometry.Height)];

    private static VisualGraph? BuildDrawingVisualGraph(
        XlsxWorksheetRecord worksheet,
        IReadOnlyList<XlsxDrawingShapeRecord> shapes,
        IReadOnlyList<FlowNode> nodes,
        IReadOnlyList<VisualEdge> edges,
        IReadOnlyDictionary<string, string> edgeSourceIds,
        List<VisualDiagnostic> diagnostics,
        IReadOnlyList<(string Id, string Value, SourceAnchor Anchor)> labels)
    {
        var nodeIds = nodes.Select(node => node.Id).ToArray();
        if (nodeIds.Length == 0 || nodeIds.Distinct(StringComparer.Ordinal).Count() != nodeIds.Length)
            return null;
        var visualNodes = nodes.Select(node =>
        {
            var shape = shapes.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Id, node.ShapeId));
            return new VisualNode(node.Id, node.Region.Value, VisualKind(shape?.Geometry), node.ShapeId,
                ShapeGeometry(shape), shape is null ? null : ShapeAnchor(worksheet, shape));
        }).ToArray();
        var visualPaths = new List<VisualPath>();
        var sourceItems = new List<VisualSourceItem>();
        var nodeByShapeId = nodes.Where(node => node.ShapeId is not null)
            .GroupBy(node => node.ShapeId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var edgeById = edges.ToDictionary(edge => edge.Id, StringComparer.Ordinal);
        foreach (var (shape, index) in shapes.Select((shape, index) => (shape, index)))
        {
            var sourceItemId = "shape:" + SafeVisualId(shape.Id) + ":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var anchor = ShapeAnchor(worksheet, shape);
            if (nodeByShapeId.TryGetValue(shape.Id, out var node))
            {
                sourceItems.Add(new VisualSourceItem(sourceItemId,
                    VisualSourceItemKind.Shape, VisualDisposition.ProjectedNode,
                    ProjectedNodeId: node.Id, SourceAnchor: anchor));
                continue;
            }
            if (shape.IsConnector)
            {
                if (edgeSourceIds.TryGetValue(shape.Id, out var connectorEdgeId) && edgeById.TryGetValue(connectorEdgeId, out var connectorEdge) &&
                    connectorEdge.SourceId is not null && connectorEdge.TargetId is not null)
                {
                    sourceItems.Add(new VisualSourceItem(sourceItemId, VisualSourceItemKind.Connector,
                        VisualDisposition.ProjectedEdge, ProjectedEdgeId: connectorEdge.Id, SourceAnchor: anchor));
                }
                else
                {
                    const string code = "VisualConnectorUnresolved";
                    if (!diagnostics.Any(diagnostic => diagnostic.Code == code && diagnostic.SourceObjectId == shape.Id))
                        diagnostics.Add(new VisualDiagnostic(code,
                            "An XLSX connector could not be uniquely associated with source and target shapes.", shape.Id,
                            Fallback: "connector retained as diagnostic-only source item",
                            Remedy: "connect the connector to two distinct shapes", Format: "xlsx",
                            PartUri: worksheet.PartUri, PartitionId: worksheet.Name,
                            SourceObjectId: shape.Id, SourceObjectType: "connector", Confidence: 0));
                    sourceItems.Add(new VisualSourceItem(sourceItemId, VisualSourceItemKind.Connector,
                        VisualDisposition.DiagnosticOnly, DiagnosticCode: code,
                        Reason: "connector endpoints unresolved", SourceAnchor: anchor));
                }
                continue;
            }
            if (TryArrowDirection(shape, out var direction))
            {
                if (edgeSourceIds.TryGetValue(shape.Id, out var arrowEdgeId) && edgeById.TryGetValue(arrowEdgeId, out var arrowEdge) &&
                    arrowEdge.SourceId is not null && arrowEdge.TargetId is not null)
                {
                    sourceItems.Add(new VisualSourceItem(sourceItemId, VisualSourceItemKind.DirectionalShape,
                        VisualDisposition.ProjectedEdge, ProjectedEdgeId: arrowEdge.Id, SourceAnchor: anchor));
                }
                else if (ShapeGeometry(shape) is { } geometry)
                {
                    var pathId = "path_" + SafeVisualId(shape.Id) + "_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    visualPaths.Add(new VisualPath(pathId, RectanglePath(geometry), geometry, anchor,
                        Confidence: 0.4, IsFallback: true, SourceNodeId: shape.Id));
                    sourceItems.Add(new VisualSourceItem(sourceItemId, VisualSourceItemKind.DirectionalShape,
                        VisualDisposition.VisualFallback, FallbackPathId: pathId,
                        Reason: "directional shape endpoints unresolved", SourceAnchor: anchor));
                }
                else
                {
                    sourceItems.Add(new VisualSourceItem(sourceItemId, VisualSourceItemKind.DirectionalShape,
                        VisualDisposition.IgnoredDecorative, Reason: "directional shape has no usable geometry", SourceAnchor: anchor));
                }
                continue;
            }
            sourceItems.Add(new VisualSourceItem(sourceItemId, VisualSourceItemKind.Shape,
                VisualDisposition.IgnoredDecorative, Reason: "non-semantic drawing shape", SourceAnchor: anchor));
        }
        foreach (var label in labels)
            sourceItems.Add(new VisualSourceItem(label.Id, VisualSourceItemKind.TextLabel,
                VisualDisposition.IgnoredDecorative, Reason: "attached to projected edge label", SourceAnchor: label.Anchor));
        var graph = new VisualGraph("xlsx-visual-" + SafeVisualId(worksheet.Name), visualNodes, edges, diagnostics,
            "LR", Paths: visualPaths, SourceItems: sourceItems);
        return graph with { Quality = VisualGraphValidator.ComputeQuality(graph) };

        static VisualNodeKind VisualKind(string? geometry) => geometry?.ToLowerInvariant() switch
        {
            "flowchartterminator" or "roundrect" or "ellipse" => VisualNodeKind.Terminator,
            "flowchartdecision" or "diamond" => VisualNodeKind.Decision,
            "flowchartdata" or "flowchartmanualinput" or "parallelogram" => VisualNodeKind.Data,
            "flowchartprocess" or "flowchartdocument" or "rect" or "can" => VisualNodeKind.Process,
            _ => VisualNodeKind.Generic
        };

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
        !shape.IsConnector && shape.WidthEmu >= 250_000 && shape.HeightEmu >= 200_000 &&
        (shape.Geometry.StartsWith("flowChart", StringComparison.OrdinalIgnoreCase) || shape.Geometry.ToLowerInvariant() is
            "rect" or "roundrect" or "parallelogram" or "diamond" or "can" or "ellipse");

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
        var preset = geometry.ToLowerInvariant();
        // Preserve Excel flowChart presets as semantic Mermaid node shapes;
        // unknown flowChart* values intentionally fall through to generic labels.
        return preset switch
        {
            "flowchartterminator" => "([" + quoted + "])",
            "flowchartprocess" or "flowchartdocument" => "[" + quoted + "]",
            "flowchartdecision" => "{" + quoted + "}",
            "flowchartdata" or "flowchartmanualinput" => "[/" + quoted + "/]",
            "flowchartpredefinedprocess" or "flowchartconnector" or "flowchartoffpageconnector" => "[[" + quoted + "]]",
            "flowchartpreparation" => "{{" + quoted + "}}",
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
        var candidates = (direction switch
        {
            Direction.Right when source => nodes.Where(node => node.Region.MaxColumn <= arrow.MinColumn + 2),
            Direction.Right => nodes.Where(node => node.Region.MinColumn >= arrow.MaxColumn - 2),
            Direction.Left when source => nodes.Where(node => node.Region.MinColumn >= arrow.MaxColumn - 2),
            Direction.Left => nodes.Where(node => node.Region.MaxColumn <= arrow.MinColumn + 2),
            Direction.Up when source => nodes.Where(node => node.Region.MinRow >= arrow.MaxRow - 3),
            Direction.Up => nodes.Where(node => node.Region.MaxRow <= arrow.MinRow + 2),
            Direction.Down when source => nodes.Where(node => node.Region.MaxRow <= arrow.MinRow + 2),
            _ => nodes.Where(node => node.Region.MinRow >= arrow.MaxRow - 3),
        }).Where(node => HasPerpendicularOverlap(node.Region, arrow, direction))
         .Where(node => PrimaryGap(node.Region, arrow, direction, source) <= 12)
         .Where(node => PrimaryGap(node.Region, arrow, direction, source) >= -1.5)
         .Where(node => !SkipsCloserNode(node, nodes, arrow, direction, source))
         .Select(node => new DirectionalCandidate(node, DirectionalScore(node.Region, arrow, direction, source)))
         .OrderBy(candidate => candidate.Score)
         .ToArray();
        if (candidates.Length == 0) return null;
        if (candidates.Length > 1 && candidates[1].Score - candidates[0].Score < 0.75) return null;

        return candidates[0].Node;
    }

    private static bool HasPerpendicularOverlap(Region node, Region arrow, Direction direction)
    {
        var vertical = direction is Direction.Up or Direction.Down;
        var nodeMin = vertical ? node.MinColumn : node.MinRow;
        var nodeMax = vertical ? node.MaxColumn : node.MaxRow;
        var arrowMin = vertical ? arrow.MinColumn : arrow.MinRow;
        var arrowMax = vertical ? arrow.MaxColumn : arrow.MaxRow;
        var overlap = Math.Min(nodeMax, arrowMax) - Math.Max(nodeMin, arrowMin);
        return overlap >= 0 || Math.Min(Math.Abs(nodeMin - arrowMax), Math.Abs(arrowMin - nodeMax)) <= 1.5;
    }

    private static double PrimaryGap(Region node, Region arrow, Direction direction, bool source) => direction switch
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

    private static bool SkipsCloserNode(FlowNode candidate, IReadOnlyList<FlowNode> nodes, Region arrow, Direction direction, bool source)
    {
        var candidateGap = PrimaryGap(candidate.Region, arrow, direction, source);
        return nodes.Any(other => other.Id != candidate.Id &&
            HasPerpendicularOverlap(other.Region, arrow, direction) &&
            PrimaryGap(other.Region, arrow, direction, source) >= -1.5 &&
            PrimaryGap(other.Region, arrow, direction, source) < candidateGap);
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

    private sealed record DirectionalCandidate(FlowNode Node, double Score);

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
            var bounds = ShapeBounds(shape, worksheet.Metrics);
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
    private sealed record DiagramProjection(string Type, string Mermaid, int MinRow, int MaxRow, string Source,
        VisualGraph? VisualGraph = null);
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
    private sealed record DrawingArrow(double MinColumn, double MinRow, double MaxColumn, double MaxRow, Direction Direction,
        string SourceShapeId)
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
