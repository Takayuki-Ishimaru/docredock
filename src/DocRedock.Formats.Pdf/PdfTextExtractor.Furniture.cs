using System.Text.RegularExpressions;

namespace DocRedock.Formats.Pdf;

public static partial class PdfTextExtractor
{
    // A running header or footer lies in this share of the page's height at its top or bottom: Word puts it
    // 15 mm from the edge of an A4 page by default, whose header ends some 18 mm (about 6%) down.
    private const double FurnitureBand = 0.12;
    // A header or footer line is short; the rule that sets it off from the body runs this share of the width.
    private const int MaxFurnitureCharacters = 64;
    private const double FurnitureRuleShare = 0.3;

    /// <summary>Marks the page's running headers and footers, which are not part of the text that flows from
    /// page to page: a short line in the band at the top or the bottom of the page that recurs at the same
    /// place on at least half of the pages (or does so with a number that counts the pages, see
    /// <see cref="MarkNumberedFurniture"/>); a short line set off from the body by a rule (above the rule under
    /// a header, below the rule over a footer), even on one page; and a page number at the very top or bottom.
    /// Text of a table is never furniture: a table's repeated header row recurs at the top of its pages.</summary>
    private static void MarkPageFurniture(List<PdfPageText> pages, IReadOnlyDictionary<int, IReadOnlyList<PdfTable>> tables)
    {
        var tableIds = pages.ToDictionary(page => page.PageNumber, page => PdfTextAccounting.TableSourceIds(tables.GetValueOrDefault(page.PageNumber)));
        bool InBand(PdfPageText page, PdfTextRegion region) =>
            page.Rotation == 0 && (page.CropBox ?? page.MediaBox) is { Height: > 0 } box &&
            region.Text.Trim().Length is > 0 and <= MaxFurnitureCharacters &&
            !PdfTextAccounting.IsTableOwned(region, tableIds[page.PageNumber]) &&
            (region.BoundingBox.Y < box.Y + box.Height * FurnitureBand || region.BoundingBox.Y > box.Y + box.Height * (1 - FurnitureBand));
        var candidates = pages.SelectMany(page => page.Regions.Select(region => (page, region)))
            .Where(item => InBand(item.page, item.region))
            .ToArray();
        var furniture = new HashSet<(int Page, int Id)>();
        if (pages.Count >= 2)
        {
            var needed = Math.Max(1, (int)Math.Ceiling(pages.Count * 0.5) - 1);
            // Lines of one text are compared with one another only.
            foreach (var group in candidates.GroupBy(item => Normalize(item.region.Text), StringComparer.Ordinal))
            {
                var items = group.ToArray();
                foreach (var (place, aligned) in FurniturePlaces(items, byNumbers: false, needed))
                    foreach (var index in place)
                    {
                        var item = items[index];
                        var box = item.page.CropBox ?? item.page.MediaBox!;
                        var peers = aligned ? needed : place.Select(other => items[other]).Where(other => other.page.PageNumber != item.page.PageNumber &&
                            Math.Abs(other.region.BoundingBox.X - item.region.BoundingBox.X) <= FurnitureXTolerance &&
                            Math.Abs((other.region.BoundingBox.Y - (other.page.CropBox ?? other.page.MediaBox)!.Y) -
                                (item.region.BoundingBox.Y - box.Y)) <= FurnitureYTolerance).Select(other => other.page.PageNumber).Distinct().Count();
                        if (peers >= needed) furniture.Add((item.page.PageNumber, item.region.ReadingOrder));
                    }
            }
            MarkNumberedFurniture(pages, candidates, InBand, furniture);
        }
        foreach (var page in pages)
        {
            var box = page.CropBox ?? page.MediaBox;
            if (box is not { Height: > 0, Width: > 0 } || page.Rotation != 0) continue;
            var inBand = page.Regions.Where(region => InBand(page, region)).ToHashSet<PdfTextRegion>(ReferenceEqualityComparer.Instance);
            if (inBand.Count == 0) continue;
            var middle = box.Y + box.Height / 2;
            var rules = (page.SeparatorRules ?? []).Where(rule => rule.Horizontal && rule.To - rule.From >= box.Width * FurnitureRuleShare)
                .Select(rule => (Y: rule.Position, Left: rule.From, Right: rule.To)).ToArray();
            // Where the body - the text that is no such line - reaches at the top and at the bottom of the page.
            var body = page.Regions.Where(region => !inBand.Contains(region)).Select(region => region.BoundingBox.Y).ToArray();
            var (bodyTop, bodyBottom) = body.Length == 0 ? (double.NegativeInfinity, double.PositiveInfinity) : (body.Max(), body.Min());
            var ys = page.Regions.Select(region => region.BoundingBox.Y).Order().ToArray();
            foreach (var region in inBand)
            {
                var y = region.BoundingBox.Y;
                var header = y > middle;
                // Nothing of the page but other such lines lies between the line and the edge of the page.
                if (header ? bodyTop >= y : bodyBottom <= y) continue;
                // The rule lies between the line and the body, and only furniture lies beyond it.
                var setOff = rules.Any(rule => header
                    ? rule.Y < y && rule.Y > box.Y + box.Height * (1 - FurnitureBand - 0.03) && bodyTop < rule.Y
                    : rule.Y > y && rule.Y < box.Y + box.Height * (FurnitureBand + 0.03) && bodyBottom > rule.Y);
                // A page number at the very top or bottom of the page (a bare number of four digits or more, a year, is none):
                // it is the one line there.
                var text = region.Text.Trim();
                var pageNumber = IsPageNumber(text) && !(text.Length >= 4 && text.All(char.IsAsciiDigit)) &&
                    (header ? ys[^1] == y && (ys.Length < 2 || ys[^2] < y) : ys[0] == y && (ys.Length < 2 || ys[1] > y));
                if (setOff || pageNumber) furniture.Add((page.PageNumber, region.ReadingOrder));
            }
        }
        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            pages[index] = page with { Regions = page.Regions.Select(region =>
                furniture.Contains((page.PageNumber, region.ReadingOrder)) ? region with { IsPageFurniture = true } : region).ToArray() };
        }
    }

    // A line set larger than the body by this much is a heading (see PdfPageProjection.HeadingLevel), not furniture.
    private const double FurnitureHeadingRatio = 1.2;
    private const double FurnitureXTolerance = 12;
    // Lines stand at the same height on two pages when their heights above the foot of the page differ by at most this.
    private const double FurnitureYTolerance = 3;

    /// <summary>Adds the running headers and footers whose text is the same on every page but for its numbers:
    /// "REVIEW FOOTER 1", "Confidential - 3 / 12", "Doc-ABC rev 7 p. 3". Such a line is one of the lines in the margin
    /// band (short, of one row, in no table) that recur at the same place on at least half of the pages, and one of a
    /// count: a number in it grows from page to page. A line set larger than the body is a heading ("PAGE 2
    /// TECHNICAL REQUIREMENTS" over each page), and a line whose numbers do not count the pages (amounts, a running
    /// total) is text of the page; neither is furniture. The lines that are the same text on every page are marked
    /// by <see cref="MarkPageFurniture"/> itself.</summary>
    private static void MarkNumberedFurniture(List<PdfPageText> pages, IReadOnlyList<(PdfPageText Page, PdfTextRegion Region)> candidates,
        Func<PdfPageText, PdfTextRegion, bool> inBand, HashSet<(int Page, int Id)> furniture)
    {
        // The size of the text of the body: the median of its long lines, unknown below three of them.
        var sizes = pages.SelectMany(page => page.Regions.Where(region => region.FontSize > 0 && region.Text.Trim().Length >= 20 && !inBand(page, region)))
            .Select(region => region.FontSize!.Value).Order().ToArray();
        var bodySize = sizes.Length >= 3 ? sizes[sizes.Length / 2] : double.PositiveInfinity;
        var lines = new List<(PdfPageText Page, PdfTextRegion Region, string Key, string[] Numbers)>();
        foreach (var (page, region) in candidates)
        {
            var text = region.Text.Trim();
            if (IsPageNumber(text) || text.Contains('\n') || text.Contains('\r') || region.FontSize >= bodySize * FurnitureHeadingRatio) continue;
            var numbers = DigitRunRegex().Matches(text).Select(match => ToNumber(match.Value)).ToArray();
            if (numbers.Length > 0) lines.Add((page, region, DigitRunRegex().Replace(text, "\u0001"), numbers));
        }
        var needed = Math.Max(1, (int)Math.Ceiling(pages.Count * 0.5) - 1);
        foreach (var group in lines.GroupBy(line => line.Key, StringComparer.Ordinal))
        {
            var items = group.ToArray();
            foreach (var (place, aligned) in FurniturePlaces(items.Select(item => (item.Page, item.Region)).ToArray(), byNumbers: true, needed))
            {
                // Lines that all stand at the same place are peers of one another: each is counted with the first line of
                // every other page, which is the same count for all the first lines.
                var firsts = aligned ? place.GroupBy(index => items[index].Page.PageNumber).Select(pageItems => pageItems.First())
                    .OrderBy(index => items[index].Page.PageNumber).ToArray() : [];
                var firstsCount = aligned && CountsPages(firsts.Select(index => items[index].Numbers).ToArray());
                var isFirst = firsts.ToHashSet();
                foreach (var index in place)
                {
                    var item = items[index];
                    if (aligned)
                    {
                        if (isFirst.Contains(index) ? firstsCount : CountsPages(firsts.Select(first =>
                                items[first].Page.PageNumber == item.Page.PageNumber ? item.Numbers : items[first].Numbers).ToArray()))
                            furniture.Add((item.Page.PageNumber, item.Region.ReadingOrder));
                        continue;
                    }
                    var box = item.Page.CropBox ?? item.Page.MediaBox!;
                    var peers = place.Select(other => items[other]).Where(other => other.Page.PageNumber != item.Page.PageNumber && SameHorizontalPlace(item.Region, other.Region) &&
                            Math.Abs((other.Region.BoundingBox.Y - (other.Page.CropBox ?? other.Page.MediaBox)!.Y) - (item.Region.BoundingBox.Y - box.Y)) <= FurnitureYTolerance)
                        .GroupBy(other => other.Page.PageNumber).Select(pageItems => pageItems.First()).ToArray();
                    if (peers.Length >= needed && CountsPages(peers.Append(item).OrderBy(line => line.Page.PageNumber).Select(line => line.Numbers).ToArray()))
                        furniture.Add((item.Page.PageNumber, item.Region.ReadingOrder));
                }
            }
        }
    }

    /// <summary>Divides lines of one text (or, <paramref name="byNumbers"/>, of one text but for its numbers) into the
    /// places within which a line has its peers - the lines at the same place on other pages - and returns, as indices of
    /// <paramref name="lines"/> in their order, the places whose lines lie on enough pages to be running furniture. A
    /// place is aligned when every line of it stands where every other does, so that each line is a peer of all the lines
    /// of it on other pages: the footer of a long document is then decided at once instead of by comparing each of its
    /// lines with every other one.</summary>
    private static IEnumerable<(int[] Place, bool Aligned)> FurniturePlaces(IReadOnlyList<(PdfPageText Page, PdfTextRegion Region)> lines,
        bool byNumbers, int needed)
    {
        var height = lines.Select(line => line.Region.BoundingBox.Y - (line.Page.CropBox ?? line.Page.MediaBox)!.Y).ToArray();
        // Where a line starts, ends and has its centre (see SameHorizontalPlace).
        double[][] across = byNumbers
            ? [lines.Select(line => line.Region.BoundingBox.X).ToArray(),
                lines.Select(line => line.Region.BoundingBox.X + line.Region.BoundingBox.Width).ToArray(),
                lines.Select(line => line.Region.BoundingBox.X + line.Region.BoundingBox.Width / 2).ToArray()]
            : [lines.Select(line => line.Region.BoundingBox.X).ToArray()];
        var parent = Enumerable.Range(0, lines.Count).ToArray();
        int Root(int index) { while (parent[index] != index) index = parent[index] = parent[parent[index]]; return index; }
        // Two lines can be peers only when no gap wider than the tolerance parts them in height, and then across.
        foreach (var row in Runs(Enumerable.Range(0, lines.Count), height, FurnitureYTolerance))
        {
            foreach (var position in across)
            {
                var order = row.OrderBy(index => position[index]).ToArray();
                for (var next = 1; next < order.Length; next++)
                    if (position[order[next]] - position[order[next - 1]] <= FurnitureXTolerance) parent[Root(order[next])] = Root(order[next - 1]);
            }
            foreach (var place in row.GroupBy(Root).Select(members => members.Order().ToArray()))
            {
                if (place.Select(index => lines[index].Page.PageNumber).Distinct().Count() - 1 < needed) continue;
                double Spread(double[] values) => place.Max(index => values[index]) - place.Min(index => values[index]);
                // Lines of one text that do not start together may stand together only by their numbers (SameHorizontalPlace).
                var aligned = Spread(height) <= FurnitureYTolerance && (Spread(across[0]) <= FurnitureXTolerance || byNumbers &&
                    place.Select(index => lines[index].Region.Text.Trim()).Distinct(StringComparer.Ordinal).Count() == place.Length &&
                    (Spread(across[1]) <= FurnitureXTolerance || Spread(across[2]) <= FurnitureXTolerance));
                yield return (place, aligned);
            }
        }
    }

    /// <summary>The runs of the indices, ordered by a value, that no gap wider than the tolerance divides.</summary>
    private static IEnumerable<int[]> Runs(IEnumerable<int> indices, double[] values, double tolerance)
    {
        var run = new List<int>();
        foreach (var index in indices.OrderBy(index => values[index]))
        {
            if (run.Count > 0 && values[index] - values[run[^1]] > tolerance) { yield return run.ToArray(); run.Clear(); }
            run.Add(index);
        }
        if (run.Count > 0) yield return run.ToArray();
    }

    /// <summary>Whether a number that the lines of consecutive pages hold grows from page to page, as a count of the
    /// pages does: the n-th number of every line is greater than the n-th number of the line before it, for one n.</summary>
    private static bool CountsPages(string[][] numbersByPage) =>
        Enumerable.Range(0, numbersByPage[0].Length).Any(position => Enumerable.Range(1, numbersByPage.Length - 1)
            .All(index => CompareNumbers(numbersByPage[index][position], numbersByPage[index - 1][position]) > 0));

    // A run of digits, ASCII or full-width, as the digits without leading zeros.
    private static string ToNumber(string digits) =>
        new string(digits.Select(digit => digit >= (char)0xFF10 ? (char)(digit - 0xFF10 + '0') : digit).ToArray()).TrimStart('0');

    private static int CompareNumbers(string left, string right) =>
        left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);

    /// <summary>Whether two lines of the margin band stand at the same place across the pages: they start at the same
    /// x or, when their texts differ (in their numbers), end or are centred at the same x, for a number of another
    /// width moves the start of a line that is set flush right or centred.</summary>
    private static bool SameHorizontalPlace(PdfTextRegion region, PdfTextRegion other)
    {
        var (a, b) = (region.BoundingBox, other.BoundingBox);
        if (Math.Abs(a.X - b.X) <= FurnitureXTolerance) return true;
        if (string.Equals(region.Text.Trim(), other.Text.Trim(), StringComparison.Ordinal)) return false;
        return Math.Abs(a.X + a.Width - (b.X + b.Width)) <= FurnitureXTolerance ||
            Math.Abs(a.X + a.Width / 2 - (b.X + b.Width / 2)) <= FurnitureXTolerance;
    }

    // ASCII digits and the full-width digits U+FF10-U+FF19.
    [GeneratedRegex("[0-9\\uFF10-\\uFF19]+", RegexOptions.CultureInvariant)]
    private static partial Regex DigitRunRegex();

    /// <summary>Joins a paragraph that runs full to the foot of a column with its rest at the head of the next column
    /// (see <see cref="ContinuesInto"/>). Running headers and footers between the two are passed over; a table or the
    /// label of a diagram is no part of a paragraph.</summary>
    private static void JoinColumnContinuations(List<PdfPageText> pages, IReadOnlyDictionary<int, IReadOnlyList<PdfTable>> tables)
    {
        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            if (!page.Regions.Any(region => region.ReadingColumn is > 0)) continue;
            var tableIds = PdfTextAccounting.TableSourceIds(tables.GetValueOrDefault(page.PageNumber));
            bool Flows(PdfTextRegion region) => !PdfTextAccounting.IsTableOwned(region, tableIds) &&
                !(page.VisualLabelNodeIds is { Count: > 0 } labels && region.SourceTextIds.Any(labels.ContainsKey));
            var flow = page.Regions.Where(region => !region.IsPageFurniture).OrderBy(region => region.ReadingOrder).ToList();
            var joined = new Dictionary<PdfTextRegion, PdfTextRegion>(ReferenceEqualityComparer.Instance);
            var removed = new HashSet<PdfTextRegion>(ReferenceEqualityComparer.Instance);
            for (var position = flow.Count - 1; position > 0; position--)
            {
                var (above, below) = (flow[position - 1], joined.GetValueOrDefault(flow[position]) ?? flow[position]);
                if (above.ReadingColumn is null || below.ReadingColumn is null || above.ReadingColumn == below.ReadingColumn ||
                    !Flows(above) || !Flows(below) || !ContinuesInto(above, below)) continue;
                joined[flow[position - 1]] = JoinContinuation(above, below);
                removed.Add(flow[position]);
            }
            if (removed.Count == 0) continue;
            // Reading order stays a count from 0 without gaps: what the projection adds after the text follows it.
            pages[index] = page with
            {
                Regions = page.Regions.Where(region => !removed.Contains(region))
                    .Select(region => joined.GetValueOrDefault(region) ?? region)
                    .OrderBy(region => region.ReadingOrder).Select((region, order) => region with { ReadingOrder = order }).ToArray()
            };
        }
    }

    /// <summary>Marks a paragraph that a page break cuts: the last text of a page that runs full to the foot of
    /// its column without closing a sentence, and the first text of the next page that goes on with it (see
    /// <see cref="ContinuesInto"/>). Running headers and footers, tables and the labels of a diagram are no text
    /// of the flow; a page whose flow ends in a table or a diagram ends no paragraph.</summary>
    private static void MarkPageContinuations(List<PdfPageText> pages, IReadOnlyDictionary<int, IReadOnlyList<PdfTable>> tables)
    {
        for (var index = 0; index + 1 < pages.Count; index++)
        {
            var (page, next) = (pages[index], pages[index + 1]);
            if (next.PageNumber != page.PageNumber + 1) continue;
            var last = FlowEdge(page, tables, last: true);
            var first = FlowEdge(next, tables, last: false);
            if (last is null || first is null || !ContinuesInto(last, first)) continue;
            var separator = JoinSeparator(last.Text.TrimEnd(), first.Text.TrimStart());
            pages[index] = page with { Regions = page.Regions.Select(region => ReferenceEquals(region, last) ? region with { ContinuesOnNextPage = true } : region).ToArray() };
            pages[index + 1] = next with { Regions = next.Regions.Select(region => ReferenceEquals(region, first) ? region with { ContinuesPreviousPage = separator } : region).ToArray() };
        }

        static PdfTextRegion? FlowEdge(PdfPageText page, IReadOnlyDictionary<int, IReadOnlyList<PdfTable>> tables, bool last)
        {
            var tableIds = PdfTextAccounting.TableSourceIds(tables.GetValueOrDefault(page.PageNumber));
            var flow = page.Regions.Where(region => !region.IsPageFurniture).OrderBy(region => region.ReadingOrder).ToArray();
            var edge = last ? flow.LastOrDefault() : flow.FirstOrDefault();
            if (edge is null || PdfTextAccounting.IsTableOwned(edge, tableIds) ||
                page.VisualLabelNodeIds is { Count: > 0 } labels && edge.SourceTextIds.Any(labels.ContainsKey)) return null;
            // A table on the page that lies past the text ends the flow (or starts it) instead.
            if (tables.GetValueOrDefault(page.PageNumber) is { Count: > 0 } pageTables &&
                pageTables.Any(table => table.Bounds is { } bounds && (last ? bounds.Y < edge.BoundingBox.Y : bounds.Y + bounds.Height > edge.BoundingBox.Y)))
                return null;
            return edge;
        }
    }

    private const string PageNumberKey = "#page-number#";

    private static string Normalize(string text)
    {
        var trimmed = text.Trim();
        // Only page-number-shaped lines may vary numerically; do not erase dates or amounts.
        return IsPageNumber(trimmed) ? PageNumberKey : trimmed;
    }

    /// <summary>Whether a line is shaped like a page number: "3", "- 3 -", "Page 3 of 9", "3 / 9", "3ページ".</summary>
    internal static bool IsPageNumber(string text) => text.Length <= MaxFurnitureCharacters && PageNumberRegex().IsMatch(text.Trim());

    [GeneratedRegex(@"^(?:[-–—]\s*)?\d+(?:\s*[-–—])?$|^(?:Page|ページ|p\.)\s*\d+(?:\s*(?:of|/)\s*\d+)?$|^\d+\s*/\s*\d+$|^\d+\s*ページ$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageNumberRegex();
}
