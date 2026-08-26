using DocRedock.Core.Documents;

namespace DocRedock.Api;

public sealed record GraphChunk(
    string ChunkId,
    IReadOnlyList<string> NodeIds,
    ContentLayer ContentLayer,
    IReadOnlyList<string> ContextHeadingPath,
    string? PartitionId,
    string Text,
    int EstimatedTokens,
    bool CompleteNodes);

public sealed record GraphChunkOptions(int TargetCharacters = 4_000, bool IncludeFurniture = false);

/// <summary>Creates stable, node-complete chunks without splitting tables, shapes, or paragraphs.</summary>
public sealed class GraphChunker
{
    public IReadOnlyList<GraphChunk> Chunk(DocumentGraph graph, GraphChunkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        options ??= new GraphChunkOptions();
        if (options.TargetCharacters < 128)
            throw new ArgumentOutOfRangeException(nameof(options), "TargetCharacters must be at least 128.");

        var result = new List<GraphChunk>();
        var headingPath = new List<string>();
        foreach (var partition in graph.Partitions.OrderBy(partition => partition.Order))
        {
            var selected = partition.Nodes.OrderBy(node => node.Order)
                .Where(node => node.Layer == ContentLayer.Body ||
                    options.IncludeFurniture && node.Layer == ContentLayer.Furniture)
                .ToArray();
            var pending = new List<(DocumentNode Node, string Text, IReadOnlyList<string> Context)>();
            var pendingLength = 0;
            foreach (var node in selected)
            {
                var text = TextOf(node);
                if (node.Kind == NodeKind.Heading)
                {
                    headingPath.Clear();
                    if (text.Length > 0) headingPath.Add(text);
                }
                if (text.Length == 0) continue;
                if (pending.Count > 0 && pendingLength + text.Length + 2 > options.TargetCharacters)
                {
                    AddChunk(partition.Id, pending, result.Count);
                    pending.Clear();
                    pendingLength = 0;
                }
                pending.Add((node, text, headingPath.ToArray()));
                pendingLength += text.Length + 2;
            }
            if (pending.Count > 0) AddChunk(partition.Id, pending, result.Count);
        }
        return result;

        void AddChunk(string partitionId, IReadOnlyList<(DocumentNode Node, string Text, IReadOnlyList<string> Context)> items, int order)
        {
            var text = string.Join("\n\n", items.Select(item => item.Text));
            var layer = items.Select(item => item.Node.Layer).Distinct().Count() == 1
                ? items[0].Node.Layer
                : ContentLayer.Body;
            var context = items[0].Context;
            result.Add(new GraphChunk(
                $"chk_{partitionId}_{order + 1:D4}",
                items.Select(item => item.Node.Id).ToArray(),
                layer,
                context,
                partitionId,
                text,
                Math.Max(1, (int)Math.Ceiling(text.Length / 4d)),
                true));
        }
    }

    private static string TextOf(DocumentNode node) => node.Content switch
    {
        TextNodeContent text => text.Text,
        RichTextNodeContent rich => string.Concat(rich.Runs.Select(run => run.Text)),
        TableNodeContent table => string.Join("\n", table.Rows.Select(row => string.Join("\t", row.Select(cell => cell.Text)))),
        ReferenceNodeContent reference => reference.AltText ?? reference.Reference,
        _ => string.Empty,
    };
}
