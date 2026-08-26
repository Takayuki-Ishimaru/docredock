using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DocRedock.Markdown;

/// <summary>
/// Renders the small Mermaid subset emitted by the XLSX readable projection as
/// static inline SVG. This is a viewer-independent preview, not a general
/// Mermaid implementation.
/// </summary>
internal static class MermaidSvgPreviewRenderer
{
    private const string FontFamily = "-apple-system, BlinkMacSystemFont, 'Segoe UI', 'Noto Sans JP', sans-serif";

    public static string? Render(string mermaid)
    {
        if (string.IsNullOrWhiteSpace(mermaid)) return null;
        var first = Lines(mermaid).FirstOrDefault(line => line.Length > 0) ?? string.Empty;
        return first switch
        {
            "stateDiagram-v2" => RenderState(mermaid),
            "sequenceDiagram" => RenderSequence(mermaid),
            _ when first.StartsWith("flowchart ", StringComparison.Ordinal) => RenderFlowchart(mermaid),
            _ => null,
        };
    }

    private static string? RenderFlowchart(string mermaid)
    {
        var lines = Lines(mermaid);
        var nodes = new Dictionary<string, SvgNode>(StringComparer.Ordinal);
        var edges = new List<SvgEdge>();
        var laneNames = new Dictionary<string, string>(StringComparer.Ordinal);
        string? lane = null;
        foreach (var line in lines.Skip(1))
        {
            var trimmed = line.Trim();
            var subgraph = Regex.Match(trimmed, "^subgraph\\s+(?<id>L\\d+)\\[\\\"(?<label>.*)\\\"\\]$", RegexOptions.CultureInvariant);
            if (subgraph.Success)
            {
                lane = subgraph.Groups["id"].Value;
                laneNames[lane] = subgraph.Groups["label"].Value;
                continue;
            }
            if (trimmed == "end") { lane = null; continue; }
            if (trimmed.StartsWith("direction ", StringComparison.Ordinal)) continue;

            var edge = Regex.Match(trimmed,
                "^(?<from>N_[A-Za-z0-9_]+)\\s+-->(?:\\|(?<label>[^|]*)\\|)?\\s+(?<to>N_[A-Za-z0-9_]+)$",
                RegexOptions.CultureInvariant);
            if (edge.Success)
            {
                edges.Add(new SvgEdge(edge.Groups["from"].Value, edge.Groups["to"].Value, edge.Groups["label"].Value, false));
                continue;
            }

            var node = Regex.Match(trimmed, "^(?<id>N_([A-Z]+)([0-9]+))(?<shape>.+)$", RegexOptions.CultureInvariant);
            if (!node.Success) continue;
            var id = node.Groups["id"].Value;
            var coordinate = Regex.Match(id, "^N_(?<column>[A-Z]+)(?<row>[0-9]+)$", RegexOptions.CultureInvariant);
            var shape = node.Groups["shape"].Value;
            var quoted = Regex.Match(shape, "\\\"(?<label>.*)\\\"", RegexOptions.CultureInvariant);
            if (!quoted.Success) continue;
            nodes[id] = new SvgNode(
                id,
                quoted.Groups["label"].Value,
                ColumnNumber(coordinate.Groups["column"].Value),
                int.Parse(coordinate.Groups["row"].Value, CultureInfo.InvariantCulture),
                ShapeKind(shape),
                lane);
        }
        if (nodes.Count < 2) return null;

        var minColumn = nodes.Values.Min(node => node.SourceX);
        var minRow = nodes.Values.Min(node => node.SourceY);
        const double columnScale = 17;
        const double rowScale = 10;
        foreach (var node in nodes.Values)
        {
            node.X = 120 + (node.SourceX - minColumn) * columnScale;
            node.Y = 100 + (node.SourceY - minRow) * rowScale;
            var labelLines = WrapLabel(node.Label, 23);
            node.Width = Math.Clamp(labelLines.Max(line => DisplayLength(line)) * 8.2 + 34, 150, 250);
            node.Height = Math.Max(54, labelLines.Count * 18 + 24);
        }
        var width = Math.Ceiling(nodes.Values.Max(node => node.X + node.Width / 2) + 70);
        var height = Math.Ceiling(nodes.Values.Max(node => node.Y + node.Height / 2) + 70);
        var prefix = Prefix(mermaid);
        var output = BeginSvg(width, height, "Excelから再構築したフローチャート", prefix);

        foreach (var group in nodes.Values.Where(node => node.Lane is not null).GroupBy(node => node.Lane!, StringComparer.Ordinal))
        {
            var laneX = group.Average(node => node.X);
            output.Append("  <rect x=\"").Append(F(laneX - group.Max(node => node.Width) / 2 - 18)).Append("\" y=\"48\" width=\"")
                .Append(F(group.Max(node => node.Width) + 36)).Append("\" height=\"").Append(F(height - 70))
                .AppendLine("\" rx=\"12\" fill=\"#f8fafc\" stroke=\"#cbd5e1\" />");
            AppendText(output, laneX, 31, WrapLabel(laneNames.GetValueOrDefault(group.Key, group.Key), 24), 15, true, "#334155");
        }
        foreach (var edge in edges) AppendEdge(output, nodes, edge, prefix);
        foreach (var node in nodes.Values.OrderBy(node => node.SourceY).ThenBy(node => node.SourceX)) AppendNode(output, node);
        return EndSvg(output);
    }

    private static string? RenderState(string mermaid)
    {
        var nodes = new Dictionary<string, SvgNode>(StringComparer.Ordinal);
        var edges = new List<SvgEdge>();
        foreach (var line in Lines(mermaid).Skip(1))
        {
            var declaration = Regex.Match(line.Trim(), "^state\\s+\\\"(?<label>.*)\\\"\\s+as\\s+(?<id>S_[A-Za-z0-9_]+)$", RegexOptions.CultureInvariant);
            if (declaration.Success)
            {
                var id = declaration.Groups["id"].Value;
                nodes[id] = new SvgNode(id, declaration.Groups["label"].Value, 0, 0, "round", null);
                continue;
            }
            var transition = Regex.Match(line.Trim(), "^(?<from>\\[\\*\\]|S_[A-Za-z0-9_]+)\\s+-->\\s+(?<to>S_[A-Za-z0-9_]+)(?::\\s*(?<label>.*))?$", RegexOptions.CultureInvariant);
            if (transition.Success)
                edges.Add(new SvgEdge(transition.Groups["from"].Value, transition.Groups["to"].Value, transition.Groups["label"].Value, false));
        }
        if (nodes.Count == 0 || edges.Count == 0) return null;
        nodes["[*]"] = new SvgNode("[*]", string.Empty, 0, 0, "start", null);

        var layers = new Dictionary<string, int>(StringComparer.Ordinal) { ["[*]"] = 0 };
        var queue = new Queue<string>();
        queue.Enqueue("[*]");
        while (queue.Count > 0)
        {
            var source = queue.Dequeue();
            foreach (var edge in edges.Where(edge => edge.From == source))
            {
                if (layers.ContainsKey(edge.To)) continue;
                layers[edge.To] = layers[source] + 1;
                queue.Enqueue(edge.To);
            }
        }
        var fallbackLayer = layers.Values.DefaultIfEmpty(0).Max() + 1;
        foreach (var node in nodes.Keys.Where(id => !layers.ContainsKey(id))) layers[node] = fallbackLayer++;
        var layerGroups = nodes.Values.GroupBy(node => layers[node.Id]).OrderBy(group => group.Key).ToArray();
        const int maxColumns = 5;
        const double columnSpacing = 240;
        const double rowSpacing = 230;
        foreach (var group in layerGroups)
        {
            var gridRow = group.Key / maxColumns;
            var offset = group.Key % maxColumns;
            var gridColumn = gridRow % 2 == 0 ? offset : maxColumns - 1 - offset;
            var values = group.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
            for (var index = 0; index < values.Length; index++)
            {
                var node = values[index];
                node.X = 55 + gridColumn * columnSpacing;
                node.Y = 85 + gridRow * rowSpacing + (index - (values.Length - 1) / 2d) * 88;
                node.Width = node.Id == "[*]" ? 22 : 184;
                node.Height = node.Id == "[*]" ? 22 : Math.Max(58, WrapLabel(node.Label, 22).Count * 18 + 24);
            }
        }
        var width = Math.Ceiling(nodes.Values.Max(node => node.X + node.Width / 2) + 70);
        var height = Math.Ceiling(nodes.Values.Max(node => node.Y + node.Height / 2) + 80);
        var prefix = Prefix(mermaid);
        var output = BeginSvg(width, height, "Excelから再構築した状態遷移図", prefix);
        foreach (var edge in edges) AppendEdge(output, nodes, edge, prefix);
        foreach (var node in nodes.Values) AppendNode(output, node);
        return EndSvg(output);
    }

    private static string? RenderSequence(string mermaid)
    {
        var participants = new List<Participant>();
        var events = new List<SequenceEvent>();
        foreach (var line in Lines(mermaid).Skip(1))
        {
            var trimmed = line.Trim();
            var participant = Regex.Match(trimmed, "^participant\\s+(?<id>P[0-9]+)\\s+as\\s+(?<label>.*)$", RegexOptions.CultureInvariant);
            if (participant.Success)
            {
                participants.Add(new Participant(participant.Groups["id"].Value, participant.Groups["label"].Value));
                continue;
            }
            var message = Regex.Match(trimmed, "^(?<from>P[0-9]+)(?<arrow>-->>|->>)(?<to>P[0-9]+):\\s*(?<label>.*)$", RegexOptions.CultureInvariant);
            if (message.Success)
            {
                events.Add(new SequenceEvent("message", message.Groups["from"].Value, message.Groups["to"].Value,
                    message.Groups["label"].Value, message.Groups["arrow"].Value.StartsWith("--", StringComparison.Ordinal)));
                continue;
            }
            var note = Regex.Match(trimmed, "^Note over (?<from>P[0-9]+)(?:,(?<to>P[0-9]+))?:\\s*(?<label>.*)$", RegexOptions.CultureInvariant);
            if (note.Success)
                events.Add(new SequenceEvent("note", note.Groups["from"].Value,
                    note.Groups["to"].Success ? note.Groups["to"].Value : note.Groups["from"].Value, note.Groups["label"].Value, false));
        }
        if (participants.Count < 2 || events.Count == 0) return null;
        const double spacing = 185;
        const double margin = 70;
        var positions = participants.Select((participant, index) => (participant.Id, X: margin + index * spacing))
            .ToDictionary(item => item.Id, item => item.X, StringComparer.Ordinal);
        var eventHeights = events.Select(@event => @event.Kind == "note" ? Math.Max(62, WrapLabel(@event.Label, 92).Count * 18 + 24) : 68).ToArray();
        var width = margin * 2 + (participants.Count - 1) * spacing;
        var height = 115 + eventHeights.Sum() + 45;
        var prefix = Prefix(mermaid);
        var output = BeginSvg(width, height, "Excelから再構築したシーケンス図", prefix);
        foreach (var participant in participants)
        {
            var x = positions[participant.Id];
            output.Append("  <line x1=\"").Append(F(x)).Append("\" y1=\"68\" x2=\"").Append(F(x)).Append("\" y2=\"")
                .Append(F(height - 25)).AppendLine("\" stroke=\"#94a3b8\" stroke-width=\"1.5\" stroke-dasharray=\"6 5\" />");
            output.Append("  <rect x=\"").Append(F(x - 68)).Append("\" y=\"18\" width=\"136\" height=\"50\" rx=\"8\" fill=\"#e0f2fe\" stroke=\"#0284c7\" stroke-width=\"1.5\" />\n");
            AppendText(output, x, 43, WrapLabel(participant.Label, 17), 13, true, "#0c4a6e");
        }
        var y = 112d;
        var messageNumber = 0;
        for (var index = 0; index < events.Count; index++)
        {
            var @event = events[index];
            var eventHeight = eventHeights[index];
            if (@event.Kind == "note")
            {
                var left = Math.Min(positions[@event.From], positions[@event.To]) - 62;
                var right = Math.Max(positions[@event.From], positions[@event.To]) + 62;
                output.Append("  <rect x=\"").Append(F(left)).Append("\" y=\"").Append(F(y - 20)).Append("\" width=\"")
                    .Append(F(right - left)).Append("\" height=\"").Append(F(eventHeight - 12)).AppendLine("\" rx=\"6\" fill=\"#fef3c7\" stroke=\"#d97706\" />");
                AppendText(output, (left + right) / 2, y + (eventHeight - 34) / 2, WrapLabel(@event.Label, Math.Min(92, Math.Max(24, (int)((right - left) / 10.5)))), 12, false, "#78350f");
            }
            else
            {
                messageNumber++;
                var from = positions[@event.From];
                var to = positions[@event.To];
                output.Append("  <line x1=\"").Append(F(from)).Append("\" y1=\"").Append(F(y)).Append("\" x2=\"").Append(F(to)).Append("\" y2=\"").Append(F(y))
                    .Append("\" stroke=\"#334155\" stroke-width=\"1.8\"");
                if (@event.Dashed) output.Append(" stroke-dasharray=\"7 5\"");
                output.Append(" marker-end=\"url(#").Append(prefix).AppendLine("-arrow)\" />");
                AppendText(output, (from + to) / 2, y - 13, WrapLabel($"{messageNumber}. {@event.Label}", 30), 11.5, false, "#0f172a");
            }
            y += eventHeight;
        }
        return EndSvg(output);
    }

    private static void AppendEdge(StringBuilder output, IReadOnlyDictionary<string, SvgNode> nodes, SvgEdge edge, string prefix)
    {
        if (!nodes.TryGetValue(edge.From, out var source) || !nodes.TryGetValue(edge.To, out var target)) return;
        var dx = target.X - source.X;
        var dy = target.Y - source.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return;
        var startScale = 1 / Math.Max(Math.Abs(dx) / Math.Max(1, source.Width / 2), Math.Abs(dy) / Math.Max(1, source.Height / 2));
        var endScale = 1 / Math.Max(Math.Abs(dx) / Math.Max(1, target.Width / 2), Math.Abs(dy) / Math.Max(1, target.Height / 2));
        var x1 = source.X + dx * startScale;
        var y1 = source.Y + dy * startScale;
        var x2 = target.X - dx * endScale;
        var y2 = target.Y - dy * endScale;
        output.Append("  <line x1=\"").Append(F(x1)).Append("\" y1=\"").Append(F(y1)).Append("\" x2=\"").Append(F(x2)).Append("\" y2=\"").Append(F(y2))
            .Append("\" stroke=\"#475569\" stroke-width=\"1.8\"");
        if (edge.Dashed) output.Append(" stroke-dasharray=\"7 5\"");
        output.Append(" marker-end=\"url(#").Append(prefix).AppendLine("-arrow)\" />");
        if (string.IsNullOrWhiteSpace(edge.Label)) return;
        var labelLines = WrapLabel(edge.Label, 25);
        var labelWidth = Math.Min(230, Math.Max(62, labelLines.Max(line => DisplayLength(line)) * 7.2 + 16));
        var labelHeight = labelLines.Count * 15 + 8;
        var midX = (x1 + x2) / 2;
        var midY = (y1 + y2) / 2 - 7;
        output.Append("  <rect x=\"").Append(F(midX - labelWidth / 2)).Append("\" y=\"").Append(F(midY - labelHeight / 2))
            .Append("\" width=\"").Append(F(labelWidth)).Append("\" height=\"").Append(F(labelHeight)).AppendLine("\" rx=\"4\" fill=\"#ffffff\" fill-opacity=\"0.94\" />");
        AppendText(output, midX, midY, labelLines, 10.5, false, "#334155");
    }

    private static void AppendNode(StringBuilder output, SvgNode node)
    {
        if (node.Kind == "start")
        {
            output.Append("  <circle cx=\"").Append(F(node.X)).Append("\" cy=\"").Append(F(node.Y)).AppendLine("\" r=\"10\" fill=\"#0f172a\" />");
            return;
        }
        var x = node.X - node.Width / 2;
        var y = node.Y - node.Height / 2;
        var fill = node.Kind == "diamond" ? "#fff7ed" : node.Kind == "cylinder" ? "#ecfdf5" : "#eff6ff";
        var stroke = node.Kind == "diamond" ? "#ea580c" : node.Kind == "cylinder" ? "#059669" : "#2563eb";
        switch (node.Kind)
        {
            case "diamond":
                output.Append("  <polygon points=\"").Append(F(node.X)).Append(',').Append(F(y)).Append(' ')
                    .Append(F(x + node.Width)).Append(',').Append(F(node.Y)).Append(' ')
                    .Append(F(node.X)).Append(',').Append(F(y + node.Height)).Append(' ')
                    .Append(F(x)).Append(',').Append(F(node.Y)).Append("\" fill=\"").Append(fill).Append("\" stroke=\"").Append(stroke).AppendLine("\" stroke-width=\"1.6\" />");
                break;
            case "parallelogram":
                output.Append("  <polygon points=\"").Append(F(x + 14)).Append(',').Append(F(y)).Append(' ')
                    .Append(F(x + node.Width)).Append(',').Append(F(y)).Append(' ')
                    .Append(F(x + node.Width - 14)).Append(',').Append(F(y + node.Height)).Append(' ')
                    .Append(F(x)).Append(',').Append(F(y + node.Height)).Append("\" fill=\"").Append(fill).Append("\" stroke=\"").Append(stroke).AppendLine("\" stroke-width=\"1.6\" />");
                break;
            case "cylinder":
                output.Append("  <rect x=\"").Append(F(x)).Append("\" y=\"").Append(F(y + 7)).Append("\" width=\"").Append(F(node.Width)).Append("\" height=\"").Append(F(node.Height - 14)).Append("\" fill=\"").Append(fill).Append("\" stroke=\"").Append(stroke).AppendLine("\" stroke-width=\"1.6\" />");
                output.Append("  <ellipse cx=\"").Append(F(node.X)).Append("\" cy=\"").Append(F(y + 7)).Append("\" rx=\"").Append(F(node.Width / 2)).Append("\" ry=\"7\" fill=\"").Append(fill).Append("\" stroke=\"").Append(stroke).AppendLine("\" stroke-width=\"1.6\" />");
                output.Append("  <path d=\"M ").Append(F(x)).Append(' ').Append(F(y + node.Height - 7)).Append(" A ").Append(F(node.Width / 2)).Append(" 7 0 0 0 ").Append(F(x + node.Width)).Append(' ').Append(F(y + node.Height - 7)).Append("\" fill=\"none\" stroke=\"").Append(stroke).AppendLine("\" stroke-width=\"1.6\" />");
                break;
            default:
                output.Append("  <rect x=\"").Append(F(x)).Append("\" y=\"").Append(F(y)).Append("\" width=\"").Append(F(node.Width)).Append("\" height=\"").Append(F(node.Height)).Append("\" rx=\"").Append(node.Kind == "round" ? "14" : "7").Append("\" fill=\"").Append(fill).Append("\" stroke=\"").Append(stroke).AppendLine("\" stroke-width=\"1.6\" />");
                break;
        }
        AppendText(output, node.X, node.Y, WrapLabel(node.Label, 23), 12, true, "#0f172a");
    }

    private static StringBuilder BeginSvg(double width, double height, string title, string prefix)
    {
        var output = new StringBuilder();
        output.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(F(width)).Append(' ').Append(F(height))
            .Append("\" width=\"100%\" height=\"").Append(F(height)).Append("\" role=\"img\" aria-label=\"").Append(Xml(title)).AppendLine("\">");
        output.Append("  <title>").Append(Xml(title)).AppendLine("</title>");
        output.Append("  <defs><marker id=\"").Append(prefix).Append("-arrow\" markerWidth=\"8\" markerHeight=\"8\" refX=\"7\" refY=\"4\" orient=\"auto\"><path d=\"M0,0 L8,4 L0,8 z\" fill=\"#475569\" /></marker></defs>\n");
        output.Append("  <rect width=\"100%\" height=\"100%\" rx=\"10\" fill=\"#ffffff\" stroke=\"#e2e8f0\" />\n");
        return output;
    }

    private static string EndSvg(StringBuilder output)
    {
        output.AppendLine("</svg>");
        return output.ToString().TrimEnd();
    }

    private static void AppendText(StringBuilder output, double x, double centerY, IReadOnlyList<string> lines, double fontSize, bool bold, string color)
    {
        if (lines.Count == 0) return;
        var lineHeight = fontSize * 1.35;
        var startY = centerY - (lines.Count - 1) * lineHeight / 2 + fontSize * 0.34;
        output.Append("  <text x=\"").Append(F(x)).Append("\" y=\"").Append(F(startY)).Append("\" text-anchor=\"middle\" font-family=\"").Append(FontFamily)
            .Append("\" font-size=\"").Append(F(fontSize)).Append("\" fill=\"").Append(color).Append('"');
        if (bold) output.Append(" font-weight=\"600\"");
        output.AppendLine(">");
        for (var index = 0; index < lines.Count; index++)
            output.Append("    <tspan x=\"").Append(F(x)).Append("\" dy=\"").Append(index == 0 ? "0" : F(lineHeight)).Append("\">").Append(Xml(lines[index])).AppendLine("</tspan>");
        output.AppendLine("  </text>");
    }

    private static IReadOnlyList<string> WrapLabel(string value, int maxCharacters)
    {
        var result = new List<string>();
        foreach (var rawLine in Regex.Split(value, "<br\\s*/?>|\\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) { result.Add(string.Empty); continue; }
            while (DisplayLength(line) > maxCharacters)
            {
                var take = Math.Min(line.Length, maxCharacters);
                result.Add(line[..take]);
                line = line[take..];
            }
            result.Add(line);
        }
        return result.Count == 0 ? [string.Empty] : result;
    }

    private static double DisplayLength(string value) => value.Sum(character => character <= 0x7f ? 0.6 : 1.0);
    private static string ShapeKind(string shape) => shape.StartsWith("{", StringComparison.Ordinal) ? "diamond" :
        shape.StartsWith("[/", StringComparison.Ordinal) ? "parallelogram" :
        shape.StartsWith("[(", StringComparison.Ordinal) ? "cylinder" :
        shape.StartsWith("([", StringComparison.Ordinal) ? "round" : "rect";
    private static string[] Lines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    private static string Prefix(string value) => "svg-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..10].ToLowerInvariant();
    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Xml(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&apos;", StringComparison.Ordinal);

    private static int ColumnNumber(string name)
    {
        var result = 0;
        foreach (var character in name) result = result * 26 + character - 'A' + 1;
        return result;
    }

    private sealed class SvgNode(string id, string label, int sourceX, int sourceY, string kind, string? lane)
    {
        public string Id { get; } = id;
        public string Label { get; } = label;
        public int SourceX { get; } = sourceX;
        public int SourceY { get; } = sourceY;
        public string Kind { get; } = kind;
        public string? Lane { get; } = lane;
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    private sealed record SvgEdge(string From, string To, string Label, bool Dashed);
    private sealed record Participant(string Id, string Label);
    private sealed record SequenceEvent(string Kind, string From, string To, string Label, bool Dashed);
}
