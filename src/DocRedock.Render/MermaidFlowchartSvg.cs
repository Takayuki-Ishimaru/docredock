using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocRedock.Render;

// Flowchart half of the Mermaid renderer: node shapes, link kinds, subgraphs (lanes and boxes) and the layered layout.
internal static partial class MermaidDiagramSvg
{
    private const int MaxGroups = 60;
    private const double GroupPad = 14;
    private const double GroupGap = 12;
    private const double BandPad = 12;
    private const double TitleFont = 13;
    private const double TitleLine = 16;

    private enum Shape { Process, Rounded, Terminator, Decision, Data, DataAlt, Trapezoid, TrapezoidAlt, Subroutine, Circle, Cylinder, Hexagon, Flag }

    private static readonly (string Open, string Close, Shape Shape)[] ShapeForms =
    [
        ("([", "])", Shape.Terminator),
        ("[[", "]]", Shape.Subroutine),
        ("[(", ")]", Shape.Cylinder),
        ("(((", ")))", Shape.Circle),
        ("((", "))", Shape.Circle),
        ("{{", "}}", Shape.Hexagon),
        ("[/", "/]", Shape.Data),
        ("[\\", "\\]", Shape.DataAlt),
        ("[/", "\\]", Shape.Trapezoid),
        ("[\\", "/]", Shape.TrapezoidAlt),
        (">", "]", Shape.Flag),
        ("{", "}", Shape.Decision),
        ("(", ")", Shape.Rounded),
        ("[", "]", Shape.Process),
    ];

    private sealed class FNode
    {
        public string Id = "";
        public string Label = "";
        public Shape Shape;
        public bool Dummy;
        public int Rank;
        public double W, H;      // box size on the page
        public double Along, Cross;
        public double CrossSize; // extent on the cross axis
        public List<string> Lines = [];
        public double X, Y;
        public int Group;        // index into the group list; 0 is the root
        public int Band;
    }

    private sealed class FEdge
    {
        public int From, To;
        public string? Label;
        public bool Dotted, Arrow, Thick;
        public bool Back;
        public List<int> Chain = [];
    }

    private sealed class FGroup
    {
        public string Id = "";
        public string? Label;
        public int Parent = -1;
        public int Depth;
        public List<int> Children = [];
        public List<int> Members = [];
        public bool Live;
        public int MinNode = int.MaxValue;
        public List<string> TitleLines = [];
        public double TitleW, TitleH;
        public List<(bool IsBand, int Id)> Items = [];
        public double Lo, Hi;                // cross interval (layout coordinates)
        public double AlongLo, AlongHi;      // page coordinates on the rank axis
    }

    private sealed class FBand
    {
        public List<int> Nodes = [];
        public double Natural;
        public double Lo, Hi;
    }

    private static string CleanLabel(string raw)
    {
        var t = raw.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"') t = t[1..^1];
        return Decode(BrRx.Replace(t, "\n"));
    }

    private static List<string> WrapLabel(string label, double maxEm, double fontSize)
    {
        if (!label.Contains('\n')) return Wrap(label, maxEm, fontSize);
        var all = new List<string>();
        foreach (var part in label.Split('\n')) all.AddRange(Wrap(part.Trim(), maxEm, fontSize));
        return all;
    }

    private static bool ReadNodeRef(string s, ref int pos, out string id, out Shape? shape, out string? label)
    {
        id = "";
        shape = null;
        label = null;
        var m = IdRx.Match(s, pos);
        if (!m.Success) return false;
        id = m.Value;
        pos = m.Index + m.Length;
        string? lastOpen = null;
        foreach (var form in ShapeForms)
        {
            if (lastOpen == form.Open) continue; // the earlier forms of this opener already failed
            if (string.CompareOrdinal(s, pos, form.Open, 0, form.Open.Length) != 0) continue;
            lastOpen = form.Open;
            var bodyStart = pos + form.Open.Length;
            var best = -1;
            var bestForm = form;
            foreach (var cand in ShapeForms.Where(f => f.Open == form.Open))
            {
                var at = FindCloser(s, bodyStart, cand.Close);
                if (at >= 0 && (best < 0 || at < best)) { best = at; bestForm = cand; }
            }
            if (best < 0) continue;
            shape = bestForm.Shape;
            label = CleanLabel(s.Substring(bodyStart, best - bodyStart));
            pos = best + bestForm.Close.Length;
            return true;
        }
        return true;
    }

    private static int FindCloser(string s, int bodyStart, string close)
    {
        if (bodyStart < s.Length && s[bodyStart] == '"')
        {
            var q = s.IndexOf('"', bodyStart + 1);
            if (q > 0 && string.CompareOrdinal(s, q + 1, close, 0, close.Length) == 0) return q + 1;
        }
        return s.IndexOf(close, bodyStart, StringComparison.Ordinal);
    }

    private static string? RenderFlowchart(List<string> lines, bool leftToRight, string id)
    {
        var nodes = new List<FNode>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var edges = new List<FEdge>();
        var groups = new List<FGroup> { new() { Id = "", Live = true } };
        var stack = new List<int> { 0 };
        string? title = null;

        int Mention(string nodeId)
        {
            if (!index.TryGetValue(nodeId, out var i))
            {
                if (nodes.Count >= MaxNodes) return -1;
                nodes.Add(new FNode { Id = nodeId, Label = nodeId, Shape = Shape.Process, Group = -1 });
                i = nodes.Count - 1;
                index[nodeId] = i;
            }
            // A node belongs to the first subgraph that mentions it; mentions at the top level claim nothing.
            if (nodes[i].Group < 0 && stack.Count > 1)
            {
                nodes[i].Group = stack[^1];
                groups[stack[^1]].Members.Add(i);
            }
            return i;
        }

        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("%%", StringComparison.Ordinal))
            {
                var tm = TitleCommentRx.Match(line);
                if (tm.Success && tm.Groups[1].Value.Trim().Length > 0) title = Decode(tm.Groups[1].Value.Trim());
                continue;
            }
            if (line == "end")
            {
                if (stack.Count <= 1) return null;
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            Match m;
            if ((m = SubgraphRx.Match(line)).Success)
            {
                if (groups.Count > MaxGroups || stack.Count > 4) return null;
                var rest = m.Groups[1].Value.Trim();
                string gid, glabel;
                var im = SubgraphIdRx.Match(rest);
                if (im.Success) { gid = im.Groups[1].Value; glabel = CleanLabel(im.Groups[2].Value); }
                else { glabel = CleanLabel(rest); gid = "sg" + groups.Count.ToString(CultureInfo.InvariantCulture); }
                var g = new FGroup { Id = gid, Label = glabel, Parent = stack[^1], Depth = stack.Count };
                groups.Add(g);
                groups[stack[^1]].Children.Add(groups.Count - 1);
                stack.Add(groups.Count - 1);
                continue;
            }
            if (DirectionRx.IsMatch(line)) continue;

            var cur = 0;
            if (!ReadNodeRef(line, ref cur, out var aId, out var aShape, out var aLabel)) return null;
            var a = Mention(aId);
            if (a < 0) return null;
            if (aShape is not null) { nodes[a].Shape = aShape.Value; nodes[a].Label = aLabel!; }
            while (true)
            {
                while (cur < line.Length && char.IsWhiteSpace(line[cur])) cur++;
                if (cur >= line.Length) break;
                var lm = LinkRx.Match(line, cur);
                if (!lm.Success) return null;
                cur = lm.Index + lm.Length;
                string op;
                string? elabel = null;
                if (lm.Groups["op"].Success)
                {
                    op = lm.Groups["op"].Value;
                    if (lm.Groups["l"].Success) elabel = CleanLabel(lm.Groups["l"].Value);
                }
                else if (lm.Groups["op1"].Success) { op = lm.Groups["op1"].Value; elabel = CleanLabel(lm.Groups["t1"].Value); }
                else if (lm.Groups["op2"].Success) { op = lm.Groups["op2"].Value; elabel = CleanLabel(lm.Groups["t2"].Value); }
                else { op = lm.Groups["op3"].Value; elabel = CleanLabel(lm.Groups["t3"].Value); }
                while (cur < line.Length && char.IsWhiteSpace(line[cur])) cur++;
                if (!ReadNodeRef(line, ref cur, out var bId, out var bShape, out var bLabel)) return null;
                var b = Mention(bId);
                if (b < 0 || edges.Count >= MaxEdges) return null;
                if (bShape is not null) { nodes[b].Shape = bShape.Value; nodes[b].Label = bLabel!; }
                edges.Add(new FEdge
                {
                    From = a,
                    To = b,
                    Label = string.IsNullOrEmpty(elabel) ? null : elabel,
                    Dotted = op.Contains('.'),
                    Thick = op.Contains('='),
                    Arrow = op.EndsWith('>'),
                });
                a = b;
            }
        }
        if (stack.Count != 1 || nodes.Count == 0) return null;
        foreach (var orphan in nodes.Where(o => o.Group < 0)) orphan.Group = 0;
        for (var i = 0; i < nodes.Count; i++) if (nodes[i].Group == 0) groups[0].Members.Add(i);

        // Node boxes.
        foreach (var node in nodes)
        {
            node.Lines = WrapLabel(node.Label, 18, NodeFont);
            var tw = node.Lines.Max(l => TextWidth(l, NodeFont));
            var th = node.Lines.Count * NodeLine;
            switch (node.Shape)
            {
                case Shape.Decision:
                    node.W = Math.Max(96, tw * 1.6 + 30);
                    node.H = Math.Max(60, th * 1.7 + 22);
                    break;
                case Shape.Terminator:
                    node.W = Math.Max(70, tw + 40);
                    node.H = Math.Max(38, th + 20);
                    break;
                case Shape.Data:
                case Shape.DataAlt:
                case Shape.Trapezoid:
                case Shape.TrapezoidAlt:
                    node.W = Math.Max(70, tw + 46);
                    node.H = Math.Max(38, th + 20);
                    break;
                case Shape.Circle:
                    node.W = node.H = Math.Max(56, 2 * (Math.Sqrt(tw * tw / 4 + th * th / 4) + 8));
                    break;
                case Shape.Cylinder:
                    node.W = Math.Max(64, tw + 28);
                    node.H = Math.Max(52, th + 36);
                    break;
                case Shape.Hexagon:
                    node.W = Math.Max(80, tw + 48);
                    node.H = Math.Max(42, th + 22);
                    break;
                case Shape.Flag:
                    node.W = Math.Max(70, tw + 44);
                    node.H = Math.Max(38, th + 20);
                    break;
                case Shape.Subroutine:
                    node.W = Math.Max(76, tw + 44);
                    node.H = Math.Max(38, th + 20);
                    break;
                default:
                    node.W = Math.Max(64, tw + 28);
                    node.H = Math.Max(38, th + 20);
                    break;
            }
        }
        var realCount = nodes.Count;

        // Subgraph bookkeeping: which groups hold nodes, and the bands (cross-axis intervals) they split into.
        for (var g = groups.Count - 1; g >= 1; g--)
        {
            var grp = groups[g];
            foreach (var mem in grp.Members) grp.MinNode = Math.Min(grp.MinNode, mem);
            if (grp.MinNode != int.MaxValue) grp.Live = true;
            if (grp.Live)
            {
                groups[grp.Parent].Live = true;
                groups[grp.Parent].MinNode = Math.Min(groups[grp.Parent].MinNode, grp.MinNode);
            }
        }
        var liveGroups = groups.Skip(1).Where(g => g.Live).ToList();
        var banded = liveGroups.Count > 0;
        var bands = new List<FBand>();
        if (banded)
        {
            void Build(int g)
            {
                var grp = groups[g];
                var items = new List<(int Key, bool IsBand, int Id)>();
                if (grp.Members.Count > 0) items.Add((grp.Members.Min(), true, -1));
                foreach (var c in grp.Children.Where(c => groups[c].Live)) items.Add((groups[c].MinNode, false, c));
                foreach (var it in items.OrderBy(i => i.Key))
                {
                    if (it.IsBand)
                    {
                        bands.Add(new FBand { Nodes = grp.Members });
                        foreach (var nIdx in grp.Members) nodes[nIdx].Band = bands.Count - 1;
                        grp.Items.Add((true, bands.Count - 1));
                    }
                    else
                    {
                        grp.Items.Add((false, it.Id));
                        Build(it.Id);
                    }
                }
            }
            Build(0);
            foreach (var g in liveGroups)
            {
                g.TitleLines = WrapLabel(g.Label ?? "", 24, TitleFont);
                g.TitleW = g.TitleLines.Max(l => TextWidth(l, TitleFont)) + 24;
                g.TitleH = g.TitleLines.Count * TitleLine + 10;
            }
        }

        // Cycle breaking: DFS back edges are reversed for ranking only.
        var outs = new List<int>[realCount];
        for (var i = 0; i < realCount; i++) outs[i] = [];
        for (var e = 0; e < edges.Count; e++)
            if (edges[e].From != edges[e].To) outs[edges[e].From].Add(e);
        var state = new int[realCount];
        for (var s = 0; s < realCount; s++)
        {
            if (state[s] != 0) continue;
            var dfs = new Stack<(int Node, int Next)>();
            dfs.Push((s, 0));
            state[s] = 1;
            while (dfs.Count > 0)
            {
                var (u, next) = dfs.Pop();
                if (next < outs[u].Count)
                {
                    dfs.Push((u, next + 1));
                    var e = outs[u][next];
                    var v = edges[e].To;
                    if (state[v] == 1) edges[e].Back = true;
                    else if (state[v] == 0) { state[v] = 1; dfs.Push((v, 0)); }
                }
                else state[u] = 2;
            }
        }

        // Longest-path ranking on the resulting DAG.
        var indeg = new int[realCount];
        var dagOut = new List<int>[realCount];
        for (var i = 0; i < realCount; i++) dagOut[i] = [];
        foreach (var edge in edges)
        {
            if (edge.From == edge.To) continue;
            var (lo, hi) = edge.Back ? (edge.To, edge.From) : (edge.From, edge.To);
            dagOut[lo].Add(hi);
            indeg[hi]++;
        }
        var queue = new Queue<int>();
        for (var i = 0; i < realCount; i++) if (indeg[i] == 0) queue.Enqueue(i);
        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            foreach (var v in dagOut[u])
            {
                if (nodes[v].Rank < nodes[u].Rank + 1) nodes[v].Rank = nodes[u].Rank + 1;
                if (--indeg[v] == 0) queue.Enqueue(v);
            }
        }

        // Dummy nodes for edges that span several ranks.
        var dummies = 0;
        foreach (var edge in edges)
        {
            if (edge.From == edge.To) continue;
            var (lo, hi) = edge.Back ? (edge.To, edge.From) : (edge.From, edge.To);
            edge.Chain.Add(lo);
            var span = nodes[hi].Rank - nodes[lo].Rank - 1;
            for (var r = nodes[lo].Rank + 1; r < nodes[hi].Rank; r++)
            {
                if (++dummies > MaxDummies) return null;
                // An edge that crosses lanes leaves its own band halfway.
                var band = (r - nodes[lo].Rank - 1) * 2 < span ? nodes[lo].Band : nodes[hi].Band;
                nodes.Add(new FNode { Dummy = true, Rank = r, W = 0, H = 0, Band = band });
                edge.Chain.Add(nodes.Count - 1);
            }
            edge.Chain.Add(hi);
        }

        var rankCount = nodes.Max(n => n.Rank) + 1;
        var layers = new List<int>[rankCount];
        for (var r = 0; r < rankCount; r++) layers[r] = [];
        for (var i = 0; i < nodes.Count; i++) layers[nodes[i].Rank].Add(i);
        var up = new List<int>[nodes.Count];
        var down = new List<int>[nodes.Count];
        for (var i = 0; i < nodes.Count; i++) { up[i] = []; down[i] = []; }
        foreach (var edge in edges)
            for (var k = 0; k + 1 < edge.Chain.Count; k++)
            {
                down[edge.Chain[k]].Add(edge.Chain[k + 1]);
                up[edge.Chain[k + 1]].Add(edge.Chain[k]);
            }

        // Crossing reduction: barycenter sweeps (bands keep their order).
        var pos = new int[nodes.Count];
        void Renumber(int r) { for (var k = 0; k < layers[r].Count; k++) pos[layers[r][k]] = k; }
        for (var r = 0; r < rankCount; r++) Renumber(r);
        void Reorder(int r, List<int>[] neighbours)
        {
            var keyed = layers[r].Select((v, k) => (V: v, K: k, B: neighbours[v].Count == 0 ? (double)k : neighbours[v].Average(u => (double)pos[u])));
            layers[r] = keyed.OrderBy(t => nodes[t.V].Band).ThenBy(t => t.B).ThenBy(t => t.K).Select(t => t.V).ToList();
            Renumber(r);
        }
        for (var sweep = 0; sweep < 4; sweep++)
        {
            for (var r = 1; r < rankCount; r++) Reorder(r, up);
            for (var r = rankCount - 2; r >= 0; r--) Reorder(r, down);
        }

        // Geometry along (rank axis) and across.
        foreach (var node in nodes)
        {
            if (node.Dummy) { node.CrossSize = 6; continue; }
            node.CrossSize = leftToRight ? node.H : node.W;
        }
        var thickness = new double[rankCount];
        foreach (var node in nodes.Where(n => !n.Dummy))
            thickness[node.Rank] = Math.Max(thickness[node.Rank], leftToRight ? node.W : node.H);

        // Edge labels, placed in the gap between the ranks where the label segment lies.
        var labelLines = new Dictionary<FEdge, List<string>>();
        var gapExtra = new double[Math.Max(1, rankCount)];
        foreach (var edge in edges)
        {
            if (string.IsNullOrEmpty(edge.Label)) continue;
            var ll = WrapLabel(edge.Label, 22, LabelFont);
            labelLines[edge] = ll;
            if (edge.From == edge.To) continue;
            var span = edge.Chain.Count - 1;
            var gap = nodes[edge.Chain[0]].Rank + (span - 1) / 2;
            var need = leftToRight ? ll.Max(l => TextWidth(l, LabelFont)) + 36 : ll.Count * LabelLine + 24;
            gapExtra[gap] = Math.Max(gapExtra[gap], need);
        }
        var alongPos = new double[rankCount];
        var baseGap = leftToRight ? 56.0 : 46.0;
        alongPos[0] = thickness[0] / 2;
        for (var r = 1; r < rankCount; r++)
            alongPos[r] = alongPos[r - 1] + thickness[r - 1] / 2 + Math.Max(baseGap, gapExtra[r - 1]) + thickness[r] / 2;
        foreach (var node in nodes) node.Along = alongPos[node.Rank];

        // Cross coordinates: pack, then pull towards neighbours.
        const double sep = 26;
        double alongBase;
        double crossBase;
        if (!banded)
        {
            foreach (var layer in layers)
            {
                var c = 0.0;
                foreach (var v in layer)
                {
                    nodes[v].Cross = c + nodes[v].CrossSize / 2;
                    c += nodes[v].CrossSize + sep;
                }
                var shift = -(c - sep) / 2;
                foreach (var v in layer) nodes[v].Cross += shift;
            }
            void Place(int r, List<int>[] neighbours)
            {
                var layer = layers[r];
                var desired = new double[layer.Count];
                for (var k = 0; k < layer.Count; k++)
                {
                    var v = layer[k];
                    desired[k] = neighbours[v].Count == 0 ? nodes[v].Cross : neighbours[v].Average(u => nodes[u].Cross);
                }
                var placed = new double[layer.Count];
                for (var k = 0; k < layer.Count; k++)
                {
                    placed[k] = desired[k];
                    if (k > 0) placed[k] = Math.Max(placed[k], placed[k - 1] + nodes[layer[k - 1]].CrossSize / 2 + nodes[layer[k]].CrossSize / 2 + sep);
                }
                var mean = 0.0;
                for (var k = 0; k < layer.Count; k++) mean += desired[k] - placed[k];
                mean /= Math.Max(1, layer.Count);
                for (var k = 0; k < layer.Count; k++) nodes[layer[k]].Cross = placed[k] + mean;
            }
            for (var it = 0; it < 6; it++)
            {
                for (var r = 1; r < rankCount; r++) Place(r, up);
                for (var r = rankCount - 2; r >= 0; r--) Place(r, down);
            }
            var minCross = nodes.Min(n => n.Cross - n.CrossSize / 2);
            crossBase = Margin + 20 - minCross;
            alongBase = Margin + 20;
        }
        else
        {
            // Natural width of every band: the widest layer it has.
            foreach (var layer in layers)
            {
                var i = 0;
                while (i < layer.Count)
                {
                    var j = i;
                    var b = nodes[layer[i]].Band;
                    var sum = 0.0;
                    while (j < layer.Count && nodes[layer[j]].Band == b) { sum += nodes[layer[j]].CrossSize; j++; }
                    sum += sep * (j - i - 1);
                    bands[b].Natural = Math.Max(bands[b].Natural, sum + 2 * BandPad);
                    i = j;
                }
            }
            foreach (var b in bands) b.Natural = Math.Max(b.Natural, 60);

            double Natural(int g)
            {
                var grp = groups[g];
                var sum = 0.0;
                foreach (var (isBand, bid) in grp.Items) sum += isBand ? bands[bid].Natural : Natural(bid);
                sum += GroupGap * Math.Max(0, grp.Items.Count - 1);
                if (g == 0) return sum;
                sum += 2 * GroupPad + (leftToRight ? grp.TitleH : 0);
                return leftToRight ? sum : Math.Max(sum, grp.TitleW + 2 * 8);
            }
            void LayoutGroup(int g, double start, double width)
            {
                var grp = groups[g];
                var surplus = Math.Max(0, width - Natural(g));
                var share = grp.Items.Count == 0 ? 0 : surplus / grp.Items.Count;
                var x = start + (g == 0 ? 0 : GroupPad + (leftToRight ? grp.TitleH : 0));
                foreach (var (isBand, bid) in grp.Items)
                {
                    var w = (isBand ? bands[bid].Natural : Natural(bid)) + share;
                    if (isBand) { bands[bid].Lo = x; bands[bid].Hi = x + w; }
                    else LayoutGroup(bid, x, w);
                    x += w + GroupGap;
                }
                grp.Lo = start;
                grp.Hi = start + width;
            }
            LayoutGroup(0, 0, Natural(0));

            void PackSegment(List<int> layer, int from, int to)
            {
                var b = bands[nodes[layer[from]].Band];
                var c = 0.0;
                for (var k = from; k < to; k++)
                {
                    nodes[layer[k]].Cross = c + nodes[layer[k]].CrossSize / 2;
                    c += nodes[layer[k]].CrossSize + sep;
                }
                var shift = (b.Lo + b.Hi) / 2 - (c - sep) / 2;
                for (var k = from; k < to; k++) nodes[layer[k]].Cross += shift;
            }
            void PlaceSegment(List<int> layer, int from, int to, List<int>[] neighbours)
            {
                var b = bands[nodes[layer[from]].Band];
                var lo = b.Lo + BandPad;
                var hi = b.Hi - BandPad;
                var cnt = to - from;
                var desired = new double[cnt];
                for (var k = 0; k < cnt; k++)
                {
                    var v = layer[from + k];
                    desired[k] = neighbours[v].Count == 0 ? nodes[v].Cross : neighbours[v].Average(u => nodes[u].Cross);
                }
                var placed = new double[cnt];
                for (var k = 0; k < cnt; k++)
                {
                    placed[k] = desired[k];
                    if (k > 0) placed[k] = Math.Max(placed[k], placed[k - 1] + nodes[layer[from + k - 1]].CrossSize / 2 + nodes[layer[from + k]].CrossSize / 2 + sep);
                }
                var mean = 0.0;
                for (var k = 0; k < cnt; k++) mean += desired[k] - placed[k];
                mean /= Math.Max(1, cnt);
                var xs = new double[cnt];
                for (var k = 0; k < cnt; k++) xs[k] = placed[k] + mean;
                // Clamp into the band: right to left against the upper bound, then left to right against the lower one.
                for (var k = cnt - 1; k >= 0; k--)
                {
                    var sz = nodes[layer[from + k]].CrossSize;
                    var limit = k == cnt - 1 ? hi - sz / 2 : xs[k + 1] - nodes[layer[from + k + 1]].CrossSize / 2 - sep - sz / 2;
                    xs[k] = Math.Min(xs[k], limit);
                }
                for (var k = 0; k < cnt; k++)
                {
                    var sz = nodes[layer[from + k]].CrossSize;
                    var limit = k == 0 ? lo + sz / 2 : xs[k - 1] + nodes[layer[from + k - 1]].CrossSize / 2 + sep + sz / 2;
                    xs[k] = Math.Max(xs[k], limit);
                    nodes[layer[from + k]].Cross = xs[k];
                }
            }
            void ForSegments(int r, Action<List<int>, int, int> action)
            {
                var layer = layers[r];
                var i = 0;
                while (i < layer.Count)
                {
                    var j = i;
                    var b = nodes[layer[i]].Band;
                    while (j < layer.Count && nodes[layer[j]].Band == b) j++;
                    action(layer, i, j);
                    i = j;
                }
            }
            for (var r = 0; r < rankCount; r++) ForSegments(r, PackSegment);
            for (var it = 0; it < 6; it++)
            {
                for (var r = 1; r < rankCount; r++) ForSegments(r, (l, f, t) => PlaceSegment(l, f, t, up));
                for (var r = rankCount - 2; r >= 0; r--) ForSegments(r, (l, f, t) => PlaceSegment(l, f, t, down));
            }
            crossBase = Margin + 20;
            var maxDepth = liveGroups.Max(g => g.Depth);
            alongBase = Margin + 20 + (leftToRight ? 0 : maxDepth * 44);
        }
        foreach (var node in nodes)
        {
            node.Cross += crossBase;
            node.Along += alongBase;
            node.X = leftToRight ? node.Along : node.Cross;
            node.Y = leftToRight ? node.Cross : node.Along;
        }

        // Drawing.
        var bounds = new Bounds();
        var edgeSvg = new StringBuilder();
        var labelSvg = new StringBuilder();
        var nodeSvg = new StringBuilder();
        var groupSvg = new StringBuilder();
        var groupText = new StringBuilder();
        var marker = "url(#" + id + "-arrow)";
        foreach (var node in nodes.Where(n => !n.Dummy)) bounds.AddRect(node.X - node.W / 2, node.Y - node.H / 2, node.W, node.H);

        if (banded)
        {
            // Rank-axis extent of every group: its nodes and child groups, plus padding and the title row.
            for (var g = groups.Count - 1; g >= 1; g--)
            {
                var grp = groups[g];
                if (!grp.Live) continue;
                var lo = double.MaxValue;
                var hi = double.MinValue;
                foreach (var mem in grp.Members)
                {
                    var half = (leftToRight ? nodes[mem].W : nodes[mem].H) / 2;
                    var c = leftToRight ? nodes[mem].X : nodes[mem].Y;
                    lo = Math.Min(lo, c - half);
                    hi = Math.Max(hi, c + half);
                }
                foreach (var c in grp.Children.Where(c => groups[c].Live))
                {
                    lo = Math.Min(lo, groups[c].AlongLo);
                    hi = Math.Max(hi, groups[c].AlongHi);
                }
                grp.AlongLo = lo - GroupPad - (leftToRight ? 0 : grp.TitleH);
                grp.AlongHi = hi + GroupPad;
                if (leftToRight && grp.AlongHi - grp.AlongLo < grp.TitleW + 16) grp.AlongHi = grp.AlongLo + grp.TitleW + 16;
            }
            var laneStyle = groups[0].Children.Count(c => groups[c].Live) >= 2;
            if (laneStyle)
            {
                var tops = groups[0].Children.Where(c => groups[c].Live).ToList();
                var lo = tops.Min(c => groups[c].AlongLo);
                var hi = tops.Max(c => groups[c].AlongHi);
                foreach (var c in tops) { groups[c].AlongLo = lo; groups[c].AlongHi = hi; }
            }
            for (var g = 1; g < groups.Count; g++)
            {
                var grp = groups[g];
                if (!grp.Live) continue;
                var crossLo = grp.Lo + crossBase;
                var crossHi = grp.Hi + crossBase;
                var (rx, ry, rw, rh) = leftToRight
                    ? (grp.AlongLo, crossLo, grp.AlongHi - grp.AlongLo, crossHi - crossLo)
                    : (crossLo, grp.AlongLo, crossHi - crossLo, grp.AlongHi - grp.AlongLo);
                var lane = laneStyle && grp.Depth == 1;
                groupSvg.Append("<g class=\"").Append(lane ? "lane" : "group").Append("\"><rect x=\"").Append(N(rx)).Append("\" y=\"").Append(N(ry)).Append("\" width=\"").Append(N(rw)).Append("\" height=\"").Append(N(rh))
                    .Append("\" rx=\"").Append(lane ? "0" : "6").Append("\" fill=\"currentColor\" fill-opacity=\"").Append(lane ? "0.03" : "0.04").Append("\" stroke=\"currentColor\" stroke-opacity=\"0.45\" stroke-width=\"1\"")
                    .Append(lane ? "" : " stroke-dasharray=\"5 3\"").Append("/>");
                var (hx, hy, hw, hh) = leftToRight ? (rx, ry, rw, grp.TitleH) : (rx, ry, rw, grp.TitleH);
                groupSvg.Append("<rect x=\"").Append(N(hx)).Append("\" y=\"").Append(N(hy)).Append("\" width=\"").Append(N(hw)).Append("\" height=\"").Append(N(hh))
                    .Append("\" fill=\"currentColor\" fill-opacity=\"0.09\" stroke=\"none\"/></g>");
                bounds.AddRect(rx, ry, rw, rh);
                groupText.Append("<g class=\"group-title\">");
                if (lane) AppendText(groupText, grp.TitleLines, hx + hw / 2, hy + hh / 2, TitleFont, TitleLine, "currentColor", "middle", true);
                else AppendText(groupText, grp.TitleLines, hx + 10, hy + hh / 2, TitleFont, TitleLine, "currentColor", "start", true);
                groupText.Append("</g>");
            }
        }

        foreach (var edge in edges)
        {
            var dash = edge.Dotted ? " stroke-dasharray=\"6 4\"" : "";
            var markerAttr = edge.Arrow ? " marker-end=\"" + marker + "\"" : "";
            var attrs = " fill=\"none\" stroke=\"currentColor\" stroke-width=\"" + (edge.Thick ? "3" : "1.5") + "\"" + dash + markerAttr;
            if (edge.From == edge.To)
            {
                var n = nodes[edge.From];
                var x0 = n.X + n.W / 6;
                var y0 = n.Y - n.H / 2;
                var x1 = n.X + n.W / 2;
                var y1 = n.Y - n.H / 6;
                if (n.Shape == Shape.Decision) { x0 = n.X + n.W / 8; y0 = n.Y - n.H * 0.4; x1 = n.X + n.W * 0.4; y1 = n.Y - n.H / 8; }
                edgeSvg.Append("<path d=\"M").Append(N(x0)).Append(',').Append(N(y0)).Append(" C").Append(N(x0)).Append(',').Append(N(y0 - 34)).Append(' ')
                    .Append(N(x1 + 38)).Append(',').Append(N(y0 - 34)).Append(' ').Append(N(x1)).Append(',').Append(N(y1)).Append('"').Append(attrs).Append("/>");
                bounds.Add(x1 + 30, y0 - 26);
                if (labelLines.TryGetValue(edge, out var sl)) AppendEdgeLabel(labelSvg, bounds, sl, x1 + 34, y0 - 30);
                continue;
            }

            var chain = edge.Chain;
            var pts = chain.Select(c => (X: nodes[c].X, Y: nodes[c].Y)).ToList();
            if (pts.Count == 2 && edge.Back)
            {
                var a = nodes[chain[0]];
                var b = nodes[chain[1]];
                var off = Math.Max(a.CrossSize, b.CrossSize) / 2 + 24;
                var mx = (a.X + b.X) / 2;
                var my = (a.Y + b.Y) / 2;
                pts.Insert(1, leftToRight ? (mx, my + off) : (mx + off, my));
            }
            var span = chain.Count - 1;
            // Anchor the ends on the node borders.
            pts[0] = Border(nodes[chain[0]], pts[1]);
            pts[^1] = Border(nodes[chain[^1]], pts[^2]);
            var labelAt = LabelPoint(pts, edge.Back && span == 1 ? 0 : (span - 1) / 2, span);
            if (edge.Back) pts.Reverse();
            // A labelled straight edge carries its label position as the middle vertex, so a reader of the path data
            // finds the label next to the middle of the edge.
            var data = pts.Count == 2 && labelLines.ContainsKey(edge)
                ? "M" + N(pts[0].X) + "," + N(pts[0].Y) + " L" + N(labelAt.X) + "," + N(labelAt.Y) + " L" + N(pts[1].X) + "," + N(pts[1].Y)
                : PathData(pts);
            edgeSvg.Append("<path d=\"").Append(data).Append('"').Append(attrs).Append("/>");
            foreach (var p in pts) bounds.Add(p.X, p.Y);
            if (labelLines.TryGetValue(edge, out var ll)) AppendEdgeLabel(labelSvg, bounds, ll, labelAt.X, labelAt.Y);
        }

        foreach (var node in nodes.Where(n => !n.Dummy))
        {
            nodeSvg.Append("<g>");
            AppendShape(nodeSvg, node);
            AppendText(nodeSvg, node.Lines, node.X, node.Y, NodeFont, NodeLine, BoxText);
            nodeSvg.Append("</g>");
        }

        var titleSvg = "";
        if (title is not null)
        {
            if (bounds.MinX > bounds.MaxX) { bounds.Add(0, 0); bounds.Add(10, 10); }
            var tsb = new StringBuilder();
            AppendText(tsb, [title], bounds.MinX, bounds.MinY - 16, 15, 18, "currentColor", "start", true);
            bounds.Add(bounds.MinX + TextWidth(title, 15), bounds.MinY - 30);
            titleSvg = tsb.ToString();
        }

        var label = (title is null ? "" : title + ". ") + "Flowchart: " + nodes.Count(n => !n.Dummy).ToString(CultureInfo.InvariantCulture) + " nodes, " + edges.Count.ToString(CultureInfo.InvariantCulture) + " connections";
        if (liveGroups.Count > 0) label += ", " + liveGroups.Count.ToString(CultureInfo.InvariantCulture) + " groups";
        return Wrapper("flowchart", label, titleSvg + groupSvg + edgeSvg + nodeSvg + groupText + labelSvg, bounds, id);
    }

    private static void AppendShape(StringBuilder sb, FNode node)
    {
        var hw = node.W / 2;
        var hh = node.H / 2;
        var x = node.X - hw;
        var y = node.Y - hh;
        var common = " fill=\"" + BoxFill + "\" stroke=\"" + BoxStroke + "\" stroke-width=\"1.5\"";
        void Poly(params (double X, double Y)[] p)
        {
            sb.Append("<polygon points=\"").Append(string.Join(' ', p.Select(q => N(q.X) + "," + N(q.Y)))).Append('"').Append(common).Append("/>");
        }
        void Rect(double rx)
        {
            sb.Append("<rect x=\"").Append(N(x)).Append("\" y=\"").Append(N(y)).Append("\" width=\"").Append(N(node.W))
                .Append("\" height=\"").Append(N(node.H)).Append("\" rx=\"").Append(N(rx)).Append('"').Append(common).Append("/>");
        }
        var sk = Math.Min(14, hw / 2);
        switch (node.Shape)
        {
            case Shape.Terminator:
                Rect(hh);
                break;
            case Shape.Rounded:
                Rect(Math.Min(12, hh));
                break;
            case Shape.Decision:
                Poly((node.X, node.Y - hh), (node.X + hw, node.Y), (node.X, node.Y + hh), (node.X - hw, node.Y));
                break;
            case Shape.Data:
                Poly((x + sk, y), (x + node.W, y), (x + node.W - sk, y + node.H), (x, y + node.H));
                break;
            case Shape.DataAlt:
                Poly((x, y), (x + node.W - sk, y), (x + node.W, y + node.H), (x + sk, y + node.H));
                break;
            case Shape.Trapezoid:
                Poly((x + sk, y), (x + node.W - sk, y), (x + node.W, y + node.H), (x, y + node.H));
                break;
            case Shape.TrapezoidAlt:
                Poly((x, y), (x + node.W, y), (x + node.W - sk, y + node.H), (x + sk, y + node.H));
                break;
            case Shape.Hexagon:
                var hk = Math.Min(16, hw / 2);
                Poly((x + hk, y), (x + node.W - hk, y), (x + node.W, node.Y), (x + node.W - hk, y + node.H), (x + hk, y + node.H), (x, node.Y));
                break;
            case Shape.Flag:
                var fk = Math.Min(16, hw / 2);
                Poly((x, y), (x + node.W, y), (x + node.W, y + node.H), (x, y + node.H), (x + fk, node.Y));
                break;
            case Shape.Circle:
                sb.Append("<ellipse cx=\"").Append(N(node.X)).Append("\" cy=\"").Append(N(node.Y)).Append("\" rx=\"").Append(N(hw)).Append("\" ry=\"").Append(N(hh)).Append('"').Append(common).Append("/>");
                break;
            case Shape.Subroutine:
                Rect(3);
                sb.Append("<path d=\"M").Append(N(x + 10)).Append(',').Append(N(y)).Append(" L").Append(N(x + 10)).Append(',').Append(N(y + node.H))
                    .Append(" M").Append(N(x + node.W - 10)).Append(',').Append(N(y)).Append(" L").Append(N(x + node.W - 10)).Append(',').Append(N(y + node.H))
                    .Append("\" fill=\"none\" stroke=\"").Append(BoxStroke).Append("\" stroke-width=\"1.5\"/>");
                break;
            case Shape.Cylinder:
                const double cap = 7;
                sb.Append("<rect x=\"").Append(N(x)).Append("\" y=\"").Append(N(y + cap)).Append("\" width=\"").Append(N(node.W)).Append("\" height=\"").Append(N(node.H - 2 * cap))
                    .Append("\" fill=\"").Append(BoxFill).Append("\" stroke=\"none\"/>");
                sb.Append("<path d=\"M").Append(N(x)).Append(',').Append(N(y + cap)).Append(" L").Append(N(x)).Append(',').Append(N(y + node.H - cap))
                    .Append(" A").Append(N(hw)).Append(',').Append(N(cap)).Append(" 0 0 0 ").Append(N(x + node.W)).Append(',').Append(N(y + node.H - cap))
                    .Append(" L").Append(N(x + node.W)).Append(',').Append(N(y + cap)).Append("\" fill=\"none\" stroke=\"").Append(BoxStroke).Append("\" stroke-width=\"1.5\"/>");
                sb.Append("<ellipse cx=\"").Append(N(node.X)).Append("\" cy=\"").Append(N(y + cap)).Append("\" rx=\"").Append(N(hw)).Append("\" ry=\"").Append(N(cap)).Append('"').Append(common).Append("/>");
                break;
            default:
                Rect(3);
                break;
        }
    }

    private static void AppendEdgeLabel(StringBuilder sb, Bounds bounds, List<string> lines, double cx, double cy)
    {
        var w = lines.Max(l => TextWidth(l, LabelFont)) + 10;
        var h = lines.Count * LabelLine + 6;
        sb.Append("<rect x=\"").Append(N(cx - w / 2)).Append("\" y=\"").Append(N(cy - h / 2)).Append("\" width=\"").Append(N(w)).Append("\" height=\"").Append(N(h))
          .Append("\" rx=\"3\" fill=\"").Append(BoxFill).Append("\" fill-opacity=\"0.92\" stroke=\"").Append(BoxStroke).Append("\" stroke-width=\"0.5\"/>");
        AppendText(sb, lines, cx, cy, LabelFont, LabelLine, BoxText);
        bounds.AddRect(cx - w / 2, cy - h / 2, w, h);
    }

    private static (double X, double Y) Border(FNode node, (double X, double Y) toward)
    {
        var dx = toward.X - node.X;
        var dy = toward.Y - node.Y;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return (node.X, node.Y);
        var hw = Math.Max(1e-6, node.W / 2);
        var hh = Math.Max(1e-6, node.H / 2);
        var t = node.Shape == Shape.Decision
            ? 1 / (Math.Abs(dx) / hw + Math.Abs(dy) / hh)
            : node.Shape == Shape.Circle
                ? 1 / Math.Sqrt(dx * dx / (hw * hw) + dy * dy / (hh * hh))
                : 1 / Math.Max(Math.Abs(dx) / hw, Math.Abs(dy) / hh);
        t = Math.Min(t, 1);
        return (node.X + dx * t, node.Y + dy * t);
    }

    private static (double X, double Y) LabelPoint(List<(double X, double Y)> pts, int segment, int span)
    {
        _ = span;
        if (pts.Count == 3 && segment == 0 && span == 1)
            return ((pts[0].X + pts[2].X) / 8 + 0.75 * pts[1].X, (pts[0].Y + pts[2].Y) / 8 + 0.75 * pts[1].Y);
        var s = Math.Clamp(segment, 0, pts.Count - 2);
        return ((pts[s].X + pts[s + 1].X) / 2, (pts[s].Y + pts[s + 1].Y) / 2);
    }

    private static string PathData(List<(double X, double Y)> p)
    {
        var sb = new StringBuilder();
        sb.Append('M').Append(N(p[0].X)).Append(',').Append(N(p[0].Y));
        if (p.Count == 2)
            return sb.Append(" L").Append(N(p[1].X)).Append(',').Append(N(p[1].Y)).ToString();
        sb.Append(" L").Append(N((p[0].X + p[1].X) / 2)).Append(',').Append(N((p[0].Y + p[1].Y) / 2));
        for (var i = 1; i < p.Count - 1; i++)
            sb.Append(" Q").Append(N(p[i].X)).Append(',').Append(N(p[i].Y)).Append(' ').Append(N((p[i].X + p[i + 1].X) / 2)).Append(',').Append(N((p[i].Y + p[i + 1].Y) / 2));
        sb.Append(" L").Append(N(p[^1].X)).Append(',').Append(N(p[^1].Y));
        return sb.ToString();
    }
}
