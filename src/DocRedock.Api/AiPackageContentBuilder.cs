using System.Globalization;
using System.Text;
using DocRedock.Core.Documents;
using DocRedock.Markdown;

namespace DocRedock.Api;

/// <summary>
/// Builds deterministic, AI-consumable Markdown parts from a DocumentGraph by reusing
/// ReadableMarkdownSerializer. File output and transactions are handled by DocumentService.
/// </summary>
public static class AiPackageContentBuilder
{
    private const int MinimumTargetCharacters = 128;

    // Rows held by one table block at least, when the target leaves less room than this.
    private const int MinimumBlockCharacters = 512;

    public static IReadOnlyList<AiPackagePart> Build(
        DocumentGraph graph,
        ReadableMarkdownOptions options,
        string sourceFileName,
        int targetCharacters = 12000,
        CancellationToken cancellationToken = default) =>
        Build(graph, options, sourceFileName, new AiPackagePartOptions(targetCharacters), cancellationToken);

    public static IReadOnlyList<AiPackagePart> Build(
        DocumentGraph graph,
        ReadableMarkdownOptions options,
        string sourceFileName,
        AiPackagePartOptions partOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(partOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFileName);
        var targetCharacters = partOptions.TargetCharacters;
        if (targetCharacters < MinimumTargetCharacters)
            throw new ArgumentOutOfRangeException(nameof(targetCharacters), targetCharacters,
                $"Target characters must be at least {MinimumTargetCharacters}.");
        cancellationToken.ThrowIfCancellationRequested();

        var policy = DocumentContentPolicyRules.Parse(options.ContentPolicy);
        var isWorkbook = graph.Format == DocumentFormatKind.Xlsx;
        graph = graph with { Partitions = graph.Partitions.Select(p => p with
        {
            Nodes = p.Nodes.Where(n => DocumentContentPolicyRules.Includes(n, policy) ||
                isWorkbook && IsVisibleSheetOverlay(n)).ToArray()
        }).ToArray() };
        var isPageOriented = graph.Format is DocumentFormatKind.Pdf or DocumentFormatKind.Pptx or DocumentFormatKind.Xlsx;
        var ordered = graph.Partitions
            .OrderBy(partition => partition.Order)
            .ThenBy(partition => partition.Id, StringComparer.Ordinal)
            .ToArray();
        var serializer = new ReadableMarkdownSerializer(options);
        var headingPaths = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (graph.Format == DocumentFormatKind.Docx)
            foreach (var sourcePartition in ordered)
                foreach (var entry in WordHeadingPaths(sourcePartition))
                    headingPaths[entry.Key] = entry.Value;
        var parts = new List<AiPackagePart>();
        var partNumber = 0;
        var tableNumber = 0;

        for (var partitionIndex = 0; partitionIndex < ordered.Length; partitionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partition = ordered[partitionIndex];
            if (partition.Nodes.Count == 0) continue;
            if (isWorkbook && !IsSheetSelected(partition.Id, options.IncludedSheets)) continue;
            if (isWorkbook && partOptions.TableRowBlocks &&
                BuildRowBlockParts(graph, ordered, partitionIndex, partition, options, sourceFileName,
                    targetCharacters, ref partNumber, ref tableNumber, cancellationToken) is { } blocks)
            {
                parts.AddRange(blocks);
                continue;
            }

            var groups = isPageOriented
                ? [WholePartition(partition)]
                : BuildDocxGroups(partition, targetCharacters, cancellationToken) ?? [WholePartition(partition)];

            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var groupNodes = group.Nodes;
                if (groupNodes.Length == 0) continue;

                var markdown = serializer.Serialize(BuildGroupGraph(graph, ordered, partitionIndex, partition, group));
                if (IsHeaderOnly(markdown)) continue;
                var sources = groupNodes.Select(node => DescribeLocation(node, partition, graph.Format,
                    partitionIndex + 1, headingPaths)).ToArray();
                markdown = InsertSourceLine(markdown, sourceFileName, sources);

                partNumber++;
                var number = partNumber.ToString("D4", CultureInfo.InvariantCulture);
                parts.Add(new AiPackagePart(
                    "part-" + number,
                    "parts/" + number + ".md",
                    markdown,
                    groupNodes.Select(node => node.Id).ToArray(),
                    sources,
                    markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Trim().Length > targetCharacters));
            }
        }

        return parts;
    }

    // One piece of a worksheet's projection in output order: an ordinary segment, or one data row
    // of a table that is cut between rows (Table >= 0, the first row also carrying the header).
    // Text without nodes between segments (an inferred note, a heading) goes with the next piece.
    private sealed record SheetPiece(int Start, int End, IReadOnlyList<string> NodeIds, int Table = -1, int Row = -1);

    private sealed class RowBlockDraft
    {
        public List<SheetPiece> Pieces { get; } = [];
        public int Length;
        public int Table = -1;
    }

    /// <summary>
    /// Opt-in row blocks for a worksheet whose projection exceeds the target. Every table too large
    /// for one part is cut between data rows; each block after the first repeats the table header.
    /// Blocks are slices of the whole sheet's projection, so a row reads exactly as in document.md,
    /// and a part never holds rows of two different cut tables. Null keeps the sheet as one part:
    /// it fits, or no table in it is large enough to need cutting.
    /// </summary>
    private static List<AiPackagePart>? BuildRowBlockParts(DocumentGraph graph, DocumentPartition[] ordered, int partitionIndex,
        DocumentPartition partition, ReadableMarkdownOptions options, string sourceFileName, int target,
        ref int partNumber, ref int tableNumber, CancellationToken token)
    {
        var serializer = new ReadableMarkdownSerializer(options) { RecordWorkbookLayout = true };
        var markdown = serializer.Serialize(BuildGroupGraph(graph, ordered, partitionIndex, partition, WholePartition(partition)));
        var sourceLine = ("ソース: " + EscapeLiteral(sourceFileName.Trim())).Length + 1;
        if (markdown.Trim().Length + sourceLine <= target || serializer.WorkbookLayout?.Sheets is not [var sheet]) return null;
        // Every part repeats the title, sheet heading and source lines; a cut table also gets a
        // table_block line and, after its first block, its header again.
        var overhead = sheet.ContentStart + sourceLine + 120;
        var tables = sheet.Segments.Where(segment => segment.Table is { Rows.Count: >= 2 } &&
            segment.End - segment.Start + overhead > target).ToList();
        if (tables.Count == 0) return null;

        var pieces = new List<SheetPiece>();
        var position = sheet.ContentStart;
        foreach (var segment in sheet.Segments.OrderBy(segment => segment.Start))
        {
            token.ThrowIfCancellationRequested();
            if (segment.Start < position)
            {
                // A record inside the previous one stays with it; it is never cut on its own.
                if (pieces.Count == 0) return null;
                pieces[^1] = pieces[^1] with { End = Math.Max(pieces[^1].End, segment.End), NodeIds = [.. pieces[^1].NodeIds, .. segment.NodeIds] };
                position = Math.Max(position, segment.End);
                continue;
            }
            var table = tables.IndexOf(segment);
            if (table < 0)
            {
                pieces.Add(new(position, segment.End, segment.NodeIds));
                position = segment.End;
                continue;
            }
            var rows = segment.Table!.Rows;
            for (var row = 0; row < rows.Count; row++)
                pieces.Add(new(row == 0 ? position : rows[row].Start, row == rows.Count - 1 ? segment.End : rows[row].End,
                    row == 0 ? [.. segment.Table.HeaderNodeIds, .. rows[0].NodeIds] : rows[row].NodeIds, table, row));
            position = segment.End;
        }
        if (pieces.Count == 0) return null;
        pieces[^1] = pieces[^1] with { End = markdown.Length };

        var headers = tables.Select(segment => markdown[segment.Start..segment.Table!.HeaderEnd]).ToArray();
        // A target smaller than a part's fixed lines cannot be met; blocks still hold a useful number
        // of rows instead of one row each.
        var available = Math.Max(target - overhead, MinimumBlockCharacters);
        var rowOf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in partition.Nodes)
            if (node.Extensions?.TryGetValue("row", out var row) == true && row.ValueKind == System.Text.Json.JsonValueKind.Number &&
                row.TryGetInt32(out var number)) rowOf[node.Id] = number;
        int? PieceRow(SheetPiece piece) => piece.NodeIds.Where(rowOf.ContainsKey).Select(id => (int?)rowOf[id]).Min();
        var drafts = new List<RowBlockDraft>();
        var current = new RowBlockDraft();
        foreach (var piece in pieces)
        {
            token.ThrowIfCancellationRequested();
            var length = piece.End - piece.Start;
            if (piece.Row == 0)
            {
                // A cut table starts. Short text just before it (its title, a heading, a unit note)
                // goes with its first block. After another cut table, only text nearer to this
                // table's rows than to that table's last row moves; the rest stays with that table.
                var text = current.Pieces.Count - (current.Pieces.FindLastIndex(item => item.Table >= 0) + 1);
                var eligible = current.Pieces.Skip(current.Pieces.Count - text).ToList();
                if (current.Table >= 0)
                {
                    var previousLast = tables[current.Table].Table!.Rows[^1].Row;
                    var table = tables[piece.Table].Table!;
                    var nextFirst = table.HeaderRows.Count > 0 ? table.HeaderRows.Min() : table.Rows[0].Row;
                    eligible = eligible.SkipWhile(item => PieceRow(item) is not { } at || at - previousLast <= nextFirst - at).ToList();
                }
                var moving = new List<SheetPiece>();
                var budget = Math.Max(available / 4, 1);
                for (var index = eligible.Count - 1; index >= 0 && budget - (eligible[index].End - eligible[index].Start) >= 0; index--)
                {
                    budget -= eligible[index].End - eligible[index].Start;
                    moving.Insert(0, eligible[index]);
                }
                if (current.Table >= 0 || current.Pieces.Count > 0 && current.Length + length > available)
                {
                    current.Pieces.RemoveRange(current.Pieces.Count - moving.Count, moving.Count);
                    current.Length -= moving.Sum(item => item.End - item.Start);
                    if (current.Pieces.Count > 0) drafts.Add(current);
                    current = new RowBlockDraft { Length = moving.Sum(item => item.End - item.Start) };
                    current.Pieces.AddRange(moving);
                }
            }
            else if (current.Pieces.Count > 0 && current.Length + length > available)
            {
                drafts.Add(current);
                current = new RowBlockDraft();
            }
            if (current.Pieces.Count == 0 && piece.Row > 0) current.Length += headers[piece.Table].Length;
            current.Pieces.Add(piece);
            current.Length += length;
            if (piece.Table >= 0) current.Table = piece.Table;
        }
        drafts.Add(current);

        // A table that still fits one part after packing is not reported as cut.
        var blocks = Enumerable.Range(0, tables.Count)
            .Select(table => drafts.Select((draft, index) => (draft, index)).Where(item => item.draft.Table == table).Select(item => item.index).ToArray())
            .ToArray();
        var tableIds = new string?[blocks.Length];
        for (var table = 0; table < blocks.Length; table++)
            if (blocks[table].Length > 1) tableIds[table] = "table-" + (++tableNumber).ToString("D4", CultureInfo.InvariantCulture);
        var firstPart = partNumber + 1;
        string PartId(int draft) => "part-" + (firstPart + draft).ToString("D4", CultureInfo.InvariantCulture);

        var prefix = markdown[..sheet.ContentStart];
        var assigned = pieces.SelectMany(piece => piece.NodeIds).ToHashSet(StringComparer.Ordinal);
        var noHeadings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var parts = new List<AiPackagePart>();
        for (var index = 0; index < drafts.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var draft = drafts[index];
            var first = draft.Pieces[0];
            var repeatsHeader = first.Row > 0;
            var body = new StringBuilder(prefix);
            if (repeatsHeader) body.Append(headers[first.Table]);
            foreach (var piece in draft.Pieces) body.Append(markdown, piece.Start, piece.End - piece.Start);
            var ids = draft.Pieces.SelectMany(piece => piece.NodeIds)
                .Concat(repeatsHeader ? tables[first.Table].Table!.HeaderNodeIds : []).ToHashSet(StringComparer.Ordinal);
            // Nodes no segment wrote out on its own (a title cell, a covered overlay) stay with the
            // sheet's first part, so every node of the sheet is still in some part.
            if (index == 0) ids.UnionWith(partition.Nodes.Select(node => node.Id).Where(id => !assigned.Contains(id)));
            var groupNodes = partition.Nodes.Where(node => ids.Contains(node.Id))
                .OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal).ToArray();
            AiPackageTableBlock? block = null;
            if (draft.Table >= 0 && tableIds[draft.Table] is { } tableId)
            {
                var table = tables[draft.Table].Table!;
                var rows = draft.Pieces.Where(piece => piece.Table == draft.Table).Select(piece => table.Rows[piece.Row].Row).ToArray();
                var blockIndex = Array.IndexOf(blocks[draft.Table], index);
                block = new AiPackageTableBlock(tableId, blockIndex + 1, blocks[draft.Table].Length,
                    table.HeaderRows.Count == 0 ? null : CellRange(table.MinColumn, table.HeaderRows.Min(), table.MaxColumn, table.HeaderRows.Max()),
                    CellRange(table.MinColumn, rows.Min(), table.MaxColumn, rows.Max()),
                    blockIndex > 0 ? PartId(blocks[draft.Table][blockIndex - 1]) : null,
                    blockIndex + 1 < blocks[draft.Table].Length ? PartId(blocks[draft.Table][blockIndex + 1]) : null);
            }
            var sources = groupNodes.Select(node => DescribeLocation(node, partition, graph.Format, partitionIndex + 1, noHeadings)).ToArray();
            var text = InsertSourceLine(body.ToString(), sourceFileName, sources, block);
            var number = (firstPart + index).ToString("D4", CultureInfo.InvariantCulture);
            parts.Add(new AiPackagePart("part-" + number, "parts/" + number + ".md", text, groupNodes.Select(node => node.Id).ToArray(),
                sources, text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim().Length > target, block));
        }
        partNumber += drafts.Count;
        return parts;
    }

    private static string CellRange(int startColumn, int startRow, int endColumn, int endRow) =>
        $"{ColumnLetters(startColumn)}{startRow.ToString(CultureInfo.InvariantCulture)}:{ColumnLetters(endColumn)}{endRow.ToString(CultureInfo.InvariantCulture)}";

    private static string ColumnLetters(int column)
    {
        var letters = new StringBuilder();
        for (; column > 0; column = (column - 1) / 26) letters.Insert(0, (char)('A' + (column - 1) % 26));
        return letters.ToString();
    }

    private sealed record NodeGroup(DocumentNode[] Nodes, bool WholePartition)
    {
        public HashSet<string>? IdSet { get; init; }
    }

    private static NodeGroup WholePartition(DocumentPartition partition) => new(
        [.. partition.Nodes.OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal)],
        WholePartition: true);

    /// <summary>
    /// Splits a Word partition at heading starts and at the soft target, only between intact
    /// semantic groups (parent/child trees, diagram+member groups, list sequences stay whole).
    /// Null means relationships are unsafe and the caller must keep the whole partition.
    /// </summary>
    private static List<NodeGroup>? BuildDocxGroups(DocumentPartition partition,
        int targetCharacters, CancellationToken token)
    {
        var nodes = partition.Nodes.OrderBy(n => n.Order).ThenBy(n => n.Id, StringComparer.Ordinal).ToArray();
        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var indexById = nodes.Select((n, i) => (n.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        var byShapeId = new Dictionary<string, string>(StringComparer.Ordinal);
        var children = new Dictionary<string, List<DocumentNode>>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            token.ThrowIfCancellationRequested();
            if (node.Extensions?.TryGetValue("shape_id", out var shape) == true &&
                shape.ValueKind == System.Text.Json.JsonValueKind.String && shape.GetString() is { Length: > 0 } shapeId &&
                !byShapeId.TryAdd(shapeId, node.Id)) return null; // Ambiguous visual identity.
            if (node.ParentId is not { Length: > 0 } parentId) continue;
            if (!byId.ContainsKey(parentId)) return null;
            if (!children.TryGetValue(parentId, out var list)) children[parentId] = list = [];
            list.Add(node);
            var visited = new HashSet<string>(StringComparer.Ordinal) { node.Id };
            for (var ancestor = parentId; ancestor is not null; ancestor = byId[ancestor].ParentId)
            {
                token.ThrowIfCancellationRequested();
                if (!byId.ContainsKey(ancestor) || !visited.Add(ancestor)) return null;
            }
        }
        var union = new UnionFind(nodes.Length);
        for (var i = 0; i < nodes.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var node = nodes[i];
            if (node.ParentId is { Length: > 0 } parentId) union.Union(i, indexById[parentId]);
            foreach (var id in LinkedVisualMemberIds(node, byShapeId))
                if (indexById.TryGetValue(id, out var linked)) union.Union(i, linked);
            if (i > 0 && node.Kind == NodeKind.ListItem && nodes[i - 1].Kind == NodeKind.ListItem &&
                node.ParentId == nodes[i - 1].ParentId) union.Union(i - 1, i);
        }
        // A semantic group's members may be interleaved with ordinary paragraphs. Protect
        // its whole interval, so splitting never reorders or severs any member.
        var boundaries = Enumerable.Repeat(true, nodes.Length).ToArray();
        foreach (var members in Enumerable.Range(0, nodes.Length).GroupBy(union.Find))
        {
            var end = members.Max();
            for (var i = members.Min() + 1; i <= end; i++) boundaries[i] = false;
        }
        var groups = new List<NodeGroup>();
        var start = 0;
        long length = 0;
        for (var i = 0; i < nodes.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if (i > start && boundaries[i] && nodes[i - 1].Kind != NodeKind.Heading &&
                (nodes[i].Kind == NodeKind.Heading || length >= targetCharacters))
            {
                groups.Add(new NodeGroup(nodes[start..i], false));
                start = i;
                length = 0;
            }
            length += NodeLength(nodes[i]);
        }
        if (start < nodes.Length) groups.Add(new NodeGroup(nodes[start..], false));
        foreach (var group in groups)
            if (!IsSliceClosed(group.Nodes, group.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal),
                    byId, children, byShapeId)) return null;
        return groups;
    }

    private static bool IsVisibleSheetOverlay(DocumentNode node) => node.Kind == NodeKind.Shape &&
        node.Extensions?.ContainsKey("sheet_overlay") == true &&
        (!node.Extensions.TryGetValue("sheet_state", out var state) || state.ValueKind == System.Text.Json.JsonValueKind.Null ||
         state.ValueKind == System.Text.Json.JsonValueKind.String && state.GetString() == "visible");

    private static bool IsSliceClosed(IReadOnlyList<DocumentNode> slice, HashSet<string> sliceIds,
        Dictionary<string, DocumentNode> byId, Dictionary<string, List<DocumentNode>> childrenByParent,
        Dictionary<string, string> byShapeId)
    {
        foreach (var node in slice)
        {
            if (node.ParentId is { Length: > 0 } parentId &&
                (!byId.TryGetValue(parentId, out var parent) || !sliceIds.Contains(parent.Id)))
                return false;
            foreach (var linkedId in LinkedVisualMemberIds(node, byShapeId))
                if (byId.ContainsKey(linkedId) && !sliceIds.Contains(linkedId))
                    return false;
            if (childrenByParent.TryGetValue(node.Id, out var children) &&
                children.Any(child => !sliceIds.Contains(child.Id)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Ids on the opposite side of a diagram/visual-member relationship: a diagram lists its
    /// members; a member names its host through a host/shape identifier extension.
    /// </summary>
    private static IEnumerable<string> LinkedVisualMemberIds(DocumentNode node, Dictionary<string, string> byShapeId)
    {
        if (node.Extensions is null) return [];
        List<string>? ids = null;
        if (node.Kind == NodeKind.Diagram &&
            node.Extensions.TryGetValue("visual_graph_member_shape_ids", out var memberShapes) &&
            memberShapes.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var item in memberShapes.EnumerateArray())
                if (item.ValueKind == System.Text.Json.JsonValueKind.String &&
                    item.GetString() is { Length: > 0 } shapeId &&
                    byShapeId.TryGetValue(shapeId, out var memberId))
                    (ids ??= []).Add(memberId);
        if (node.Kind == NodeKind.Diagram &&
            node.Extensions.TryGetValue("visual_graph", out var raw) &&
            raw.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in new[] { "members", "nodes" })
                if (raw.TryGetProperty(property, out var array) &&
                    array.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var item in array.EnumerateArray())
                        if (item.ValueKind == System.Text.Json.JsonValueKind.String &&
                            item.GetString() is { Length: > 0 } linkedId)
                            (ids ??= []).Add(linkedId);
        }
        foreach (var key in new[] { "visual_graph_member_of", "visual_graph_host", "diagram_host", "table_overlay_host" })
            if (node.Extensions.TryGetValue(key, out var value) &&
                value.ValueKind == System.Text.Json.JsonValueKind.String &&
                value.GetString() is { Length: > 0 } hostId)
                (ids ??= []).Add(byShapeId.TryGetValue(hostId, out var nodeId) ? nodeId : hostId);
        return ids ?? (IEnumerable<string>)[];
    }

    private static int NodeLength(DocumentNode node) => node.Content switch
    {
        TextNodeContent text => text.Text.Length,
        RichTextNodeContent rich => rich.Runs.Sum(run => run.Text.Length),
        TableNodeContent table => table.Rows.Sum(row => row.Sum(cell => cell.Text.Length)),
        ReferenceNodeContent reference => reference.AltText?.Length ?? reference.Reference.Length,
        _ => 0,
    };

    private static bool IsHeaderOnly(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        var firstBreak = text.IndexOf('\n');
        var body = firstBreak < 0 ? string.Empty : text[(firstBreak + 1)..];
        return body.Trim().Length == 0;
    }

    private static string InsertSourceLine(string markdown, string sourceFileName,
        IReadOnlyList<AiPackageSourceLocation> sources, AiPackageTableBlock? tableBlock = null)
    {
        var line = "ソース: " + EscapeLiteral(sourceFileName.Trim());
        var paths = sources.Where(source => source.HeadingPath is { Count: > 0 })
            .Select(source => string.Join(" > ", source.HeadingPath!)).Distinct(StringComparer.Ordinal);
        foreach (var path in paths) line += "\nsection_path: " + EscapeLiteral(path);
        // A block read alone still says which table it continues, which rows it holds, and that
        // its first lines repeat the header rather than add rows.
        if (tableBlock is { } block)
            line += $"\ntable_block: {block.TableId} {block.Index}/{block.Count}; rows {block.RowRange}" +
                (block.HeaderRange is null ? "; no header row" : $"; header {block.HeaderRange}{(block.Index > 1 ? " (repeated)" : string.Empty)}");
        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        var firstBreak = text.IndexOf('\n');
        return firstBreak < 0
            ? line + "\n"
            : string.Concat(text[..firstBreak], "\n", line, "\n", text[firstBreak..]);
    }

    internal static string EscapeLiteral(string value)
    {
        var output = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsControl(character)) { output.Append(' '); continue; }
            if ("\\*_`[]<>#|~&".IndexOf(character) >= 0) output.Append('\\');
            output.Append(character);
        }
        return output.ToString();
    }

    private static DocumentGraph BuildGroupGraph(DocumentGraph graph, DocumentPartition[] ordered,
        int partitionIndex, DocumentPartition partition, NodeGroup group)
    {
        var kept = partition;
        if (!group.WholePartition)
        {
            var groupIds = group.IdSet ?? group.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
            kept = partition with
            {
                Nodes = [.. partition.Nodes.Where(node => groupIds.Contains(node.Id) &&
                    (node.ParentId is not { Length: > 0 } parentId || groupIds.Contains(parentId)))],
            };
        }
        if (graph.Format is not (DocumentFormatKind.Pdf or DocumentFormatKind.Pptx))
            return graph with { Partitions = [kept] };
        // Preserve original page/slide numbering: the serializer labels PPTX slides by their
        // index within the graph, so pad with emptied copies of the preceding partitions.
        return graph with
        {
            Partitions =
            [
                .. ordered[..partitionIndex].Select(p => p with { Nodes = Array.Empty<DocumentNode>() }),
                kept,
            ],
        };
    }

    private static AiPackageSourceLocation DescribeLocation(DocumentNode node, DocumentPartition partition,
        DocumentFormatKind format, int originalNumber, Dictionary<string, IReadOnlyList<string>> headingPaths)
    {
        var headings = format == DocumentFormatKind.Docx
            ? (headingPaths.TryGetValue(node.Id, out var path) ? path : [])
            : [];
        var label = format switch
        {
            DocumentFormatKind.Pdf => "page " + originalNumber.ToString(CultureInfo.InvariantCulture),
            DocumentFormatKind.Pptx => "slide " + originalNumber.ToString(CultureInfo.InvariantCulture),
            DocumentFormatKind.Xlsx => "sheet " + SheetName(partition.Id),
            _ => headings.Count > 0 ? "headings: " + string.Join(" > ", headings) : "document",
        };
        return new AiPackageSourceLocation(
            node.Id,
            partition.Id,
            label,
            PageNumber: format == DocumentFormatKind.Pdf ? originalNumber : null,
            SlideNumber: format == DocumentFormatKind.Pptx ? originalNumber : null,
            SheetName: format == DocumentFormatKind.Xlsx ? SheetName(partition.Id) : null,
            HeadingPath: headings.Count > 0 ? headings : null,
            CellAddress: format == DocumentFormatKind.Xlsx && node.Kind == NodeKind.Cell
                ? node.Source?.Locators.FirstOrDefault(locator => locator.Kind == "cell_address")?.Value
                : null);
    }

    private static Dictionary<string, IReadOnlyList<string>> WordHeadingPaths(DocumentPartition partition)
    {
        var paths = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var stack = new List<(int Level, string Text)>();
        foreach (var node in partition.Nodes
                     .OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal))
        {
            if (node.Kind == NodeKind.Heading)
            {
                var text = NodeTextOf(node).Trim();
                if (text.Length > 0)
                {
                    var level = 1;
                    if (node.Extensions is not null &&
                        node.Extensions.TryGetValue("heading_level", out var raw) &&
                        raw.ValueKind == System.Text.Json.JsonValueKind.Number &&
                        raw.TryGetInt32(out var parsed) && parsed > 0) level = parsed;
                    while (stack.Count > 0 && stack[^1].Level >= level) stack.RemoveAt(stack.Count - 1);
                    stack.Add((level, text));
                }
            }
            paths[node.Id] = node.Layer == ContentLayer.Furniture ? [] : [.. stack.Select(entry => entry.Text)];
        }
        return paths;
    }

    private static string NodeTextOf(DocumentNode node) => node.Content switch
    {
        TextNodeContent text => text.Text,
        RichTextNodeContent rich => string.Concat(rich.Runs.Select(run => run.Text)),
        _ => string.Empty,
    };

    private static string SheetName(string partitionId)
    {
        var value = partitionId.Trim();
        foreach (var prefix in new[] { "sheet-", "worksheet-", "partition-" })
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[prefix.Length..];
                break;
            }
        value = value.Trim();
        return value.Length > 0 ? value : "シート";
    }

    private static bool IsSheetSelected(string partitionId, IReadOnlyList<string>? includedSheets)
    {
        if (includedSheets is not { Count: > 0 }) return true;
        var sheetName = partitionId.Trim();
        foreach (var prefix in new[] { "sheet-", "worksheet-", "partition-" })
            if (sheetName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                sheetName = sheetName[prefix.Length..];
                break;
            }
        return includedSheets.Any(sheet => StringComparer.OrdinalIgnoreCase.Equals(sheet.Trim(), sheetName));
    }

    private sealed class UnionFind(int count)
    {
        private readonly int[] parent = Enumerable.Range(0, count).ToArray();

        public int Find(int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }
            return index;
        }

        public void Union(int left, int right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot != rightRoot) parent[Math.Max(leftRoot, rightRoot)] = Math.Min(leftRoot, rightRoot);
        }
    }
}
