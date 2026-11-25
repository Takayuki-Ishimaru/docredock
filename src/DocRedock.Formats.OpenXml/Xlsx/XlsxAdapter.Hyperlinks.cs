using System.Xml;
using System.Text;

namespace DocRedock.Formats.OpenXml.Xlsx;

public sealed partial class XlsxAdapter
{
    // Only a literal first argument of a complete root HYPERLINK call is a known target.
    // Cell references, concatenations and conditional expressions are never evaluated here.
    private static string? LiteralHyperlinkTarget(string formula)
    {
        if (formula.Length > 131072) return null;
        var text = formula.AsSpan().Trim();
        if (!text.IsEmpty && text[0] == '=') text = text[1..].TrimStart();
        if (text.StartsWith("_xlfn.", StringComparison.OrdinalIgnoreCase)) text = text[6..];
        const string name = "HYPERLINK";
        if (!text.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return null;
        text = text[name.Length..].TrimStart();
        if (text.IsEmpty || text[0] != '(') return null;
        text = text[1..].TrimStart();
        if (text.IsEmpty || text[0] != '"') return null;
        var value = new StringBuilder(); var end = -1;
        for (var i = 1; i < text.Length; i++)
        {
            if (text[i] != '"') { value.Append(text[i]); continue; }
            if (i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; continue; }
            end = i + 1; break;
        }
        if (end < 0 || value.Length == 0) return null;
        var rest = text[end..].TrimStart();
        if (rest.IsEmpty || rest[0] is not (',' or ';' or ')')) return null;
        var depth = 1; var quoted = false;
        for (var i = 0; i < rest.Length; i++)
        {
            var c = rest[i];
            if (quoted)
            {
                if (c != '"') continue;
                if (i + 1 < rest.Length && rest[i + 1] == '"') { i++; continue; }
                quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return rest[(i + 1)..].Trim().IsEmpty ? value.ToString() : null;
        }
        return null;
    }

    /// <summary>The worksheet's hyperlink relationships by Id. An External target is kept exactly as the
    /// source holds it: ReadRelationships normalizes ZIP part paths, which would mangle an absolute URL.</summary>
    private static Dictionary<string, string> ReadHyperlinkRelationships(IReadOnlyDictionary<string, byte[]> package, string worksheetPartUri)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var directory = worksheetPartUri[..worksheetPartUri.LastIndexOf('/')];
        var relsPath = directory + "/_rels/" + Path.GetFileName(worksheetPartUri) + ".rels";
        if (!package.TryGetValue(relsPath, out var bytes)) return result;
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship") continue;
            var id = reader.GetAttribute("Id");
            var type = reader.GetAttribute("Type");
            var target = reader.GetAttribute("Target");
            if (id is not { Length: > 0 } || target is not { Length: > 0 } || type?.EndsWith("/hyperlink", StringComparison.Ordinal) != true) continue;
            result[id] = reader.GetAttribute("TargetMode") is { Length: > 0 } mode && mode.Equals("External", StringComparison.OrdinalIgnoreCase)
                ? target
                : target.StartsWith('/')
                    ? NormalizePartPath(target)
                    : NormalizePartPath(directory + "/" + target);
        }
        return result;
    }

    // Sweep only populated source rows. Active column intervals live in a fixed-depth segment tree;
    // neither whole-sheet ranges nor many unrelated hyperlinks cause a range scan for every cell.
    private static Dictionary<(int Row, int Column), string> ResolveHyperlinkTargets(
        IReadOnlyList<ParsedWorksheetCell> cells,
        IReadOnlyList<(int MinRow, int MaxRow, int MinColumn, int MaxColumn, string Target)> sourceAreas)
    {
        var targets = new Dictionary<(int Row, int Column), string>();
        if (sourceAreas.Count == 0 || cells.Count == 0) return targets;
        var areas = sourceAreas.Where(a => a.MinRow is >= 1 and <= MaxExcelRows && a.MinColumn is >= 1 and <= MaxExcelColumns &&
            a.MaxRow >= a.MinRow && a.MaxColumn >= a.MinColumn).ToArray();
        var events = areas.SelectMany((a, id) => new[] { (Row: a.MinRow, Id: id, Add: true),
                (Row: Math.Min(a.MaxRow, MaxExcelRows) + 1, Id: id, Add: false) })
            .OrderBy(e => e.Row).ThenBy(e => e.Add).ToArray();
        var tree = new SortedSet<int>?[MaxExcelColumns * 4];
        var next = 0;
        foreach (var row in cells.GroupBy(c => c.Row).OrderBy(g => g.Key))
        {
            while (next < events.Length && events[next].Row <= row.Key)
            {
                var item = events[next++]; var area = areas[item.Id];
                Change(1, 1, MaxExcelColumns, area.MinColumn, Math.Min(area.MaxColumn, MaxExcelColumns), item.Id, item.Add);
            }
            foreach (var cell in row)
            {
                if (cell.Column is < 1 or > MaxExcelColumns) continue;
                var id = Find(1, 1, MaxExcelColumns, cell.Column, -1);
                if (id >= 0) targets[(cell.Row, cell.Column)] = areas[id].Target;
            }
        }
        return targets;

        void Change(int node, int low, int high, int from, int to, int id, bool add)
        {
            if (from <= low && high <= to)
            {
                if (add) (tree[node] ??= []).Add(id);
                else if (tree[node] is { } active) { active.Remove(id); if (active.Count == 0) tree[node] = null; }
                return;
            }
            var middle = (low + high) / 2;
            if (from <= middle) Change(node * 2, low, middle, from, to, id, add);
            if (to > middle) Change(node * 2 + 1, middle + 1, high, from, to, id, add);
        }

        int Find(int node, int low, int high, int column, int best)
        {
            if (tree[node] is { Count: > 0 } active) best = Math.Max(best, active.Max);
            if (low == high) return best;
            var middle = (low + high) / 2;
            return column <= middle ? Find(node * 2, low, middle, column, best) : Find(node * 2 + 1, middle + 1, high, column, best);
        }
    }
}
