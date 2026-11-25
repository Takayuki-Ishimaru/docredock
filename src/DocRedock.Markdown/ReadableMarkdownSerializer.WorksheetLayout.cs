namespace DocRedock.Markdown;

public sealed partial class ReadableMarkdownSerializer
{
    private void ExpandWorkbookMergedValues(Dictionary<(int Row, int Column), ReadableCell> cells)
    {
        if (!options.ExpandMergedCells) return;
        var origins = cells.Values.Where(c => c.MaxRow > c.Row && !string.IsNullOrWhiteSpace(c.Text))
            .OrderBy(c => c.Row).ThenBy(c => c.Column).ToArray();
        if (origins.Length == 0) return;
        var rows = cells.Keys.Select(c => c.Row).Distinct().Order().ToArray();
        const int maximumCopies = 100_000;
        var starts = 0; var copies = 0;
        var active = new SortedDictionary<int, ReadableCell>();
        var ends = new PriorityQueue<ReadableCell, int>();
        foreach (var row in rows)
        {
            while (ends.TryPeek(out var ended, out var lastRow) && lastRow < row)
            {
                ends.Dequeue();
                if (active.GetValueOrDefault(ended.Column) == ended) active.Remove(ended.Column);
            }
            while (starts < origins.Length && origins[starts].Row <= row)
            {
                var origin = origins[starts++];
                if (origin.MaxRow < row) continue;
                active[origin.Column] = origin; ends.Enqueue(origin, origin.MaxRow);
            }
            foreach (var origin in active.Values)
            {
                if (row == origin.Row) continue;
                cells.TryGetValue((row, origin.Column), out var original);
                if (original is not null && (!string.IsNullOrWhiteSpace(original.Text) || original.IsFormulaCell)) continue;
                if (++copies > maximumCopies)
                {
                    AddDiagnostic(new("WorkbookMergedValueExpansionBounded",
                        "Merged values exceeded the expansion limit; compare the retained source merge ranges for later rows.",
                        MarkdownDiagnosticSeverity.Warning, origin.NodeId));
                    return;
                }
                cells[(row, origin.Column)] = origin with
                {
                    Row = row, MaxRow = row,
                    OverlayNodeIds = (origin.OverlayNodeIds ?? []).Concat(original?.NodeId is { } id ? [id] : []).ToArray(),
                };
            }
        }
    }

    private static readonly string[] MetadataLabels =
    [
        "文書ID", "文書番号", "管理番号", "文書名", "作成日", "更新日", "作成者", "更新者", "状態", "版", "版数",
        "Document ID", "Document number", "Created", "Created by", "Updated", "Author", "Status", "Version", "Public Beta"
    ];

    private static bool IsHeaderValue(ReadableCell cell) => !cell.IsNumeric && !cell.IsFormula ||
        cell.IsHeaderStyled && int.TryParse(PlainText(cell.Text), out var year) && year is >= 1900 and <= 2100;

    private static bool IsNamedColumn(ReadableCell cell)
    {
        var label = PlainText(cell.Text).Trim();
        return HeaderWords.Any(word => label.Equals(word, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasColumnHeaderEvidence(SheetRow row)
    {
        var cells = row.Cells.Where(c => !string.IsNullOrWhiteSpace(c.Text)).ToArray();
        return cells.Length >= 2 && cells.All(IsHeaderValue) &&
            (cells.Count(c => c.IsBold || c.HasFill) >= Math.Max(2, (cells.Length + 1) / 2) || cells.Count(IsNamedColumn) >= 2);
    }

    private static HashSet<int> ConfirmedSchemaRows(IReadOnlyList<SheetRow> rows)
    {
        var result = new HashSet<int>();
        // A header describes its local run, not every later row that happens to use
        // the same columns. A new section or a distant note must earn its own schema.
        for (var start = 0; start < rows.Count;)
        {
            var first = rows[start];
            var table = first.Cells.Count > 0 ? first.Cells[0].Table : null;
            if (first.Cells.Count < 2 || first.Cells.Any(c => c.Table != table) ||
                table is null && !HasColumnHeaderEvidence(first))
            {
                start++;
                continue;
            }
            var columns = first.Cells.Select(c => c.Column).ToHashSet();
            var end = start + 1;
            // Empty fields may omit their source cell. Match a subset of the
            // header columns so a blank author does not shift the change text.
            while (end < rows.Count && rows[end].Number - rows[end - 1].Number <= 4 &&
                rows[end].Cells.All(c => columns.Contains(c.Column) && c.Table == table) &&
                (table is not null || !TryGetHeading(rows[end], out _, out _))) end++;
            if (table is not null || end > start + 1)
                for (var index = start; index < end; index++) result.Add(rows[index].Number);
            start = end;
        }
        return result;
    }

    private static bool IsKnownMetadataLabel(ReadableCell cell)
    {
        var label = PlainText(cell.Text).Trim().TrimEnd(':', '：');
        return MetadataLabels.Any(name => label.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsKnownMetadataLayout(SheetRow row) => row.Cells.Count is 2 or 4 &&
        Enumerable.Range(0, row.Cells.Count / 2).All(i => IsKnownMetadataLabel(row.Cells[i * 2]));

    private static bool IsReliableHeaderBand(SheetRow row, IReadOnlyList<SheetRow> preceding)
    {
        var cells = row.Cells.Where(c => !string.IsNullOrWhiteSpace(c.Text)).ToArray();
        if (cells.Length == 0 || cells.Any(c => !IsHeaderValue(c))) return false;
        if (preceding.Count == 0 && HasColumnHeaderEvidence(row)) return true;
        // Names alone identify the first header, not subsequent records whose values
        // happen to be words such as method/path or token/auth.
        // Bold is common across whole specification tables. It can support the first
        // header, but cannot promote subsequent data rows into a compound heading.
        if (preceding.Count == 0 && cells.Count(c => c.IsBold) >= Math.Max(2, (cells.Length + 1) / 2)) return true;
        // An unmerged group-header prefix may introduce additional subcolumns on
        // the next row. Uniformly bold records of the same width provide no such evidence.
        if (preceding.Count > 0 && cells.Length > preceding[^1].Cells.Count(c => !string.IsNullOrWhiteSpace(c.Text)) && cells.All(c => c.IsBold)) return true;
        // Center-across spans are explicit only when matching empty source cells
        // exist. A translated group label may repeat the parent's exact span.
        if (cells.Any(c => c.IsCenterAcross) && cells.All(c => c.IsCenterAcross || IsNamedColumn(c))) return true;
        // A plain subheader is valid inside its parent's declared merge. A merge elsewhere
        // in the prefix cannot turn arbitrary field records into header paths.
        var parents = preceding.SelectMany(r => r.Cells).Where(c => c.MaxRow >= row.Number - 1 &&
            (c.MaxColumn > c.Column || c.MaxRow > c.Row)).ToArray();
        var afterCenterAcross = preceding.Count > 0 && preceding[^1].Cells.Any(c => c.IsCenterAcross);
        if (parents.Length > 0 && cells.All(c => IsNamedColumn(c) ||
                parents.Any(p => p.Column <= c.Column && p.MaxColumn >= c.MaxColumn &&
                    (p.Column < c.Column || p.MaxColumn > c.MaxColumn)) ||
                afterCenterAcross && preceding.SelectMany(r => r.Cells).Any(p => p.IsCentered &&
                    p.Column == c.Column && p.MaxColumn == c.MaxColumn))) return true;
        return preceding.Count == 0 && cells.Length >= 2 && cells.Any(c => c.MaxColumn > c.Column || c.MaxRow > c.Row);
    }

    // A heading closes overlapping or immediately adjacent regions, matching the
    // neighbour rule used to attach indented body rows. This keeps an
    // independent table on the right intact while preventing left-side body rows from
    // being attached to a region above their own heading. Sparse range-max updates avoid
    // scanning every earlier heading for every cell or expanding full-width merges.
    private sealed class HeadingBands
    {
        private const int LastColumn = 16384;
        private readonly Dictionary<int, int> maximum = [];
        private readonly Dictionary<int, int> cover = [];

        public void Add(int from, int to, int row)
        {
            if (to < 1 || from > LastColumn) return;
            Update(1, 1, LastColumn, Math.Max(1, from), Math.Min(LastColumn, to), row);
        }

        public int Latest(int from, int to) => to < 1 || from > LastColumn ? 0 :
            Query(1, 1, LastColumn, Math.Max(1, from), Math.Min(LastColumn, to));

        private void Update(int node, int low, int high, int from, int to, int row)
        {
            maximum[node] = Math.Max(maximum.GetValueOrDefault(node), row);
            if (from <= low && high <= to) { cover[node] = Math.Max(cover.GetValueOrDefault(node), row); return; }
            var middle = (low + high) / 2;
            if (from <= middle) Update(node * 2, low, middle, from, to, row);
            if (to > middle) Update(node * 2 + 1, middle + 1, high, from, to, row);
        }

        private int Query(int node, int low, int high, int from, int to)
        {
            if (!maximum.TryGetValue(node, out var latest)) return 0;
            if (from <= low && high <= to) return latest;
            var result = cover.GetValueOrDefault(node); var middle = (low + high) / 2;
            if (from <= middle) result = Math.Max(result, Query(node * 2, low, middle, from, to));
            if (to > middle) result = Math.Max(result, Query(node * 2 + 1, middle + 1, high, from, to));
            return result;
        }
    }
}
