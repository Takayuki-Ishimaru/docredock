using System.Globalization;
using System.Text;
using DocRedock.Core.Documents;

namespace DocRedock.Markdown;

// A sequence diagram an adapter has already recognized (see SequenceVisualConventions): its participants, messages in vertical
// order, the arrow each message was drawn with, and the alt / opt / loop frames that group them.
public sealed partial class ReadableMarkdownSerializer
{
    private static bool HasExplicitSequence(VisualGraph graph) => (graph.Edges ?? []).Any(edge => SequenceVisualConventions.IsMessage(edge));

    private static bool TryProjectExplicitSequence(IReadOnlyList<VisualNode> nodes, IReadOnlyList<VisualEdge> edges, out string mermaid)
    {
        mermaid = string.Empty;
        var messages = edges.Where(edge => SequenceVisualConventions.IsMessage(edge) && edge.SourceId is not null && edge.TargetId is not null).ToArray();
        if (messages.Length == 0) return false;
        var participants = nodes.Where(node => !SequenceVisualConventions.IsFrame(node) && !SequenceVisualConventions.IsBranch(node))
            .Select((node, order) => (Node: node, Order: order))
            .OrderBy(item => item.Node.Geometry is { } geometry ? geometry.X + geometry.Width / 2 : double.MaxValue).ThenBy(item => item.Order)
            .Select(item => item.Node).ToArray();
        var aliases = participants.Select((node, index) => (node.Id, Alias: "P" + (index + 1).ToString(CultureInfo.InvariantCulture)))
            .ToDictionary(item => item.Id, item => item.Alias, StringComparer.Ordinal);
        var known = messages.Where(edge => aliases.ContainsKey(edge.SourceId!) && aliases.ContainsKey(edge.TargetId!)).ToArray();
        if (participants.Length < 2 || known.Length == 0) return false;

        static double MessageY(VisualEdge edge) => edge.Geometry is { } geometry ? geometry.Y + geometry.Height / 2
            : edge.Path is { Count: > 0 } path ? (path[0].Y + path[^1].Y) / 2 : 0;
        var ordered = known.Select((edge, order) => (Edge: edge, Order: order, Y: MessageY(edge)))
            .OrderBy(item => item.Y).ThenBy(item => item.Order).ToArray();

        var frames = nodes.Where(node => SequenceVisualConventions.IsFrame(node) && node.Geometry is not null)
            .OrderBy(node => node.Geometry!.Y).ThenByDescending(node => node.Geometry!.Height).ToList();
        var branchesByFrame = new Dictionary<string, List<VisualNode>>(StringComparer.Ordinal);
        foreach (var branch in nodes.Where(node => SequenceVisualConventions.IsBranch(node) && node.Geometry is not null))
        {
            var y = branch.Geometry!.Y + branch.Geometry.Height / 2;
            var owner = frames.Where(frame => y > frame.Geometry!.Y && y < frame.Geometry.Y + frame.Geometry.Height &&
                    branch.Geometry.X + branch.Geometry.Width / 2 >= frame.Geometry.X && branch.Geometry.X <= frame.Geometry.X + frame.Geometry.Width)
                .OrderBy(frame => frame.Geometry!.Width * frame.Geometry.Height).FirstOrDefault();
            if (owner is null) continue;
            if (!branchesByFrame.TryGetValue(owner.Id, out var list)) branchesByFrame[owner.Id] = list = [];
            list.Add(branch);
        }

        var output = new StringBuilder("sequenceDiagram\n");
        foreach (var participant in participants)
            output.Append("    participant ").Append(aliases[participant.Id]).Append(" as ").AppendLine(SequenceText(participant.Label));

        string FrameHeader(VisualNode frame) => frame.Lane is { Length: > 0 } kind ? kind : "opt";
        string Condition(VisualNode node, string keyword) =>
            string.Equals(node.Label, keyword, StringComparison.OrdinalIgnoreCase) ? string.Empty : " " + SequenceText(node.Label);
        var open = new List<(VisualNode Frame, List<VisualNode> Pending)>();
        var pendingFrames = new Queue<VisualNode>(frames);
        void Line(string text, int depth) => output.Append(' ', 4 + depth * 4).AppendLine(text);
        void Close()
        {
            var (frame, pending) = open[^1];
            open.RemoveAt(open.Count - 1);
            foreach (var branch in pending)
                Line(BranchKeyword(branch) + Condition(branch, BranchKeyword(branch)), open.Count);
            Line("end", open.Count);
        }
        void CloseBefore(double y)
        {
            while (open.Count > 0 && open[^1].Frame.Geometry!.Y + open[^1].Frame.Geometry!.Height < y) Close();
        }
        void OpenBy(double y)
        {
            while (pendingFrames.Count > 0 && pendingFrames.Peek().Geometry!.Y <= y)
            {
                var frame = pendingFrames.Dequeue();
                var geometry = frame.Geometry!;
                var bottom = geometry.Y + geometry.Height;
                var keyword = FrameHeader(frame);
                // A frame no message falls in, or one that crosses its enclosing frame, cannot be written as a block;
                // its words stay as a note over the participants.
                var crosses = open.Count > 0 && bottom > open[^1].Frame.Geometry!.Y + open[^1].Frame.Geometry!.Height + 1;
                if (bottom < y || crosses)
                {
                    Line("Note over " + aliases[participants[0].Id] + "," + aliases[participants[^1].Id] + ": " +
                        (keyword + Condition(frame, keyword)).Trim(), open.Count);
                    continue;
                }
                Line(keyword + Condition(frame, keyword), open.Count);
                open.Add((frame, branchesByFrame.TryGetValue(frame.Id, out var branches)
                    ? branches.OrderBy(branch => branch.Geometry!.Y).ToList() : []));
            }
        }

        foreach (var (edge, _, y) in ordered)
        {
            CloseBefore(y);
            OpenBy(y);
            for (var level = 0; level < open.Count; level++)
            {
                var pending = open[level].Pending;
                while (pending.Count > 0 && pending[0].Geometry!.Y + pending[0].Geometry!.Height / 2 <= y)
                {
                    var branch = pending[0];
                    pending.RemoveAt(0);
                    Line(BranchKeyword(branch) + Condition(branch, BranchKeyword(branch)), level);
                }
            }
            Line(aliases[edge.SourceId!] + SequenceMessageArrow(edge) + aliases[edge.TargetId!] + ": " + SequenceText(edge.Label ?? string.Empty), open.Count);
        }
        while (open.Count > 0) Close();
        mermaid = output.ToString().TrimEnd();
        return true;
    }

    private static string BranchKeyword(VisualNode branch) => branch.Lane is { Length: > 0 } keyword ? keyword : "else";

    // Solid and filled: a call. Dashed: a reply. Open arrowhead: an asynchronous message (Mermaid's -) arrows).
    private static string SequenceMessageArrow(VisualEdge edge)
    {
        var codes = edge.Evidence?.EvidenceCodes ?? [];
        var dashed = VisualLineStyles.IsKnown(edge.LineStyle) || codes.Contains(SequenceVisualConventions.ReturnEvidence, StringComparer.Ordinal);
        if (codes.Contains(SequenceVisualConventions.AsyncEvidence, StringComparer.Ordinal)) return dashed ? "--)" : "-)";
        return dashed ? "-->>" : "->>";
    }

    // ';' ends a statement in Mermaid's sequence syntax and has to be written as its entity.
    private static string SequenceText(string value) => MermaidText(value).Replace(";", "#59;", StringComparison.Ordinal);
}
