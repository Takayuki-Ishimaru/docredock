using DocRedock.Core.Documents;

namespace DocRedock.Render;

public static partial class ReadableHtmlWriter
{
    private sealed partial class HtmlDocumentWriter
    {
        private readonly Dictionary<string, HashSet<(int Row, int Column)>> _cellTargets = new(StringComparer.OrdinalIgnoreCase);
        // The section id of every slide the document writes, by slide number, and the same ids by section:
        // reserved before any block is written, so that a link can name a slide that comes later.
        private readonly Dictionary<int, string> _slideAnchors = [];
        private readonly Dictionary<ReadableSection, string> _slideSectionIds = new(ReferenceEqualityComparer.Instance);

        private void IndexSlideTargets(IEnumerable<ReadableBlock> blocks)
        {
            const string prefix = "source-part-";
            if (_document.SourceFormat != DocumentFormatKind.Pptx) return;
            foreach (var section in blocks.OfType<ReadableSection>())
                if (section.Id.StartsWith(prefix, StringComparison.Ordinal) &&
                    int.TryParse(section.Id.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var order) &&
                    !_slideAnchors.ContainsKey(order + 1))
                    _slideAnchors[order + 1] = _slideSectionIds[section] = ReserveId(section.Id);
        }

        /// <summary>The href of a jump to another slide (<see cref="SlideLinks"/>): the slide's section when the
        /// document has it, otherwise null, and the label stays plain (a hidden slide the output leaves out).</summary>
        private bool TrySlideLink(string target, out string? href)
        {
            href = null;
            if (_document.SourceFormat != DocumentFormatKind.Pptx || !SlideLinks.TryParse(target, out var slide)) return false;
            if (_slideAnchors.TryGetValue(slide, out var anchor)) href = "#" + anchor;
            else _diagnostics.Add(new("HtmlSlideLinkUnavailable", "A link to a slide that is not in the output keeps its label only."));
            return true;
        }

        private void IndexCellTargets(IEnumerable<ReadableBlock> blocks, string? sheet = null)
        {
            foreach (var block in blocks)
            {
                _typedCancellation.ThrowIfCancellationRequested();
                switch (block)
                {
                    case ReadableSection section:
                        IndexCellTargets(section.Blocks, section.Origin?.SheetName ?? sheet); break;
                    case ReadableWorksheetScene scene:
                        if (scene.Grid is { } grid) IndexCellTargets([grid], sheet); break;
                    case ReadableTable table:
                        var name = table.Origin?.SheetName ?? sheet;
                        if (name is not null)
                        {
                            if (!_cellTargets.TryGetValue(name, out var targets)) _cellTargets[name] = targets = [];
                            foreach (var row in table.Rows)
                                foreach (var cell in row.Cells)
                                {
                                    if (row.SourceRow is { } r && cell.SourceColumn is { } c && cell.RowSpan > 0) targets.Add((r, c));
                                    IndexCellTargets(cell.Children, name);
                                }
                        }
                        break;
                    case ReadableDetails details: IndexCellTargets(details.Blocks, sheet); break;
                    case ReadableQuote quote: IndexCellTargets(quote.Blocks, sheet); break;
                    case ReadableDiagram diagram: IndexCellTargets(diagram.FallbackBlocks, sheet); break;
                }
            }
        }

        private string? WorkbookLink(string target, string currentSheet)
        {
            if (!WorkbookCellLinks.TryParse(target, currentSheet, out var destination)) return null;
            var name = _cellTargets.Keys.FirstOrDefault(sheet => sheet.Equals(destination!.SheetName, StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;
            var point = (Row: destination!.Row, Column: destination.Column);
            if (_cellTargets[name].Contains(point)) return "#" + WorkbookCellLinks.Id(name, point.Row, point.Column);
            var layout = _workbook?.Sheets.Values.FirstOrDefault(sheet => name.Equals(sheet.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var merge in layout?.MergedRanges ?? [])
                if (ParsePrintArea(merge) is { } range && point.Row >= range.MinRow && point.Row <= range.MaxRow &&
                    point.Column >= range.MinColumn && point.Column <= range.MaxColumn &&
                    _cellTargets[name].Contains((range.MinRow, range.MinColumn)))
                    return "#" + WorkbookCellLinks.Id(name, range.MinRow, range.MinColumn);
            return null;
        }
    }
}
