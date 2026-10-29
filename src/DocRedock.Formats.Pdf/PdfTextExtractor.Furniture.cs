using System.Text.RegularExpressions;

namespace DocRedock.Formats.Pdf;

public static partial class PdfTextExtractor
{
    private static void MarkRepeatedPageFurniture(List<PdfPageText> pages)
    {
        if (pages.Count < 2) return;
        var candidates = pages.SelectMany(page => page.Regions.Select(region => (page, region)))
            .Where(item => item.page.Rotation == 0 && (item.page.CropBox ?? item.page.MediaBox) is { Height: > 0 } box &&
                item.region.Text.Trim().Length is > 0 and <= 64 &&
                (item.region.BoundingBox.Y < box.Y + box.Height * 0.05 ||
                 item.region.BoundingBox.Y > box.Y + box.Height * 0.95))
            .ToArray();
        var repeated = new HashSet<(int Page, int Id)>();
        foreach (var item in candidates)
        {
            var key = Normalize(item.region.Text);
            var box = item.page.CropBox ?? item.page.MediaBox!;
            var peers = candidates.Where(other => other.page.PageNumber != item.page.PageNumber &&
                Normalize(other.region.Text) == key &&
                Math.Abs(other.region.BoundingBox.X - item.region.BoundingBox.X) <= 12 &&
                Math.Abs((other.region.BoundingBox.Y - (other.page.CropBox ?? other.page.MediaBox)!.Y) -
                    (item.region.BoundingBox.Y - box.Y)) <= 3).Select(other => other.page.PageNumber).Distinct().Count();
            if (peers >= Math.Max(1, (int)Math.Ceiling(pages.Count * 0.5) - 1))
                repeated.Add((item.page.PageNumber, item.region.ReadingOrder));
        }
        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            pages[index] = page with { Regions = page.Regions.Select(region =>
                repeated.Contains((page.PageNumber, region.ReadingOrder)) ? region with { IsPageFurniture = true } : region).ToArray() };
        }

        static string Normalize(string text)
        {
            var trimmed = text.Trim();
            // Only page-number-shaped lines may vary numerically; do not erase dates or amounts.
            return Regex.IsMatch(trimmed, @"^(?:[-–—]\s*)?\d+(?:\s*[-–—])?$|^(?:Page|ページ)\s*\d+(?:\s*(?:of|/)\s*\d+)?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
                ? "#page-number#" : trimmed;
        }
    }
}
