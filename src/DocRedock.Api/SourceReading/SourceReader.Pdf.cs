using System.Globalization;
using System.Text;
using DocRedock.Formats.Pdf;

namespace DocRedock.Api.SourceReading;

public sealed partial class SourceReader
{
    private static PdfExtractionResult ExtractPdf(string path, CancellationToken cancellationToken)
    {
        // An encrypted PDF's streams cannot be read without its key: say so instead of reading garbled bytes.
        if (DocumentSourceException.IsEncryptedPdf(path)) throw DocumentSourceException.EncryptedPdf(Path.GetFileName(path));
        try { return PdfTextExtractor.Extract(path, cancellationToken: cancellationToken); }
        catch (PdfExtractionException exception) { throw new InvalidDataException($"PDF text extraction failed: {exception.Message}", exception); }
    }

    private SourceOverviewResult ReadPdfOverview(string input, string path, CancellationToken cancellationToken)
    {
        var extraction = ExtractPdf(path, cancellationToken);
        // The pictures the export writes as files: each distinct one once, on the first page that draws it, under the id the export gives it.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var images = extraction.Pages.SelectMany(page => (page.ExtractedImages ?? []).Select(image => (page.PageNumber, Image: image)))
            .Where(item => seen.Add(item.Image.AssetKey))
            .Select(item => new SourceImageInfo(item.Image.AssetKey, null, item.Image.MediaType, item.Image.Content.Length, item.PageNumber, null, null, null))
            .OrderBy(info => info.Id, StringComparer.Ordinal).ToArray();
        var shown = images.Take(SourceOverviewResult.MaxListedImages).ToArray();
        return new SourceOverviewResult
        {
            Input = input, Format = "pdf", Locator = Locator("overview"), PageCount = extraction.PageCount,
            Images = shown, ImageCount = images.Length,
            Note = "Read the text lines of one page with --page N (1 to " + extraction.PageCount.ToString(CultureInfo.InvariantCulture) + "), or a node with --node ID." +
                (images.Length > 0 ? " Read a picture with --image ID (the id the Markdown shows), and add --ocr to read its text." : "") +
                (images.Length > shown.Length ? $" The list shows the first {shown.Length} of {images.Length} pictures." : ""),
        };
    }

    /// <summary>The text of one page as the extractor found it - one line a region of text with where it is on the page - without
    /// the headings, tables and lists the export infers from it.</summary>
    private SourceTextResult ReadPdfPage(string input, string path, int number, int offset, int maxChars, CancellationToken cancellationToken)
    {
        var extraction = ExtractPdf(path, cancellationToken);
        if (number > extraction.PageCount)
            throw new SourceReadException("page_out_of_range",
                $"This PDF has {extraction.PageCount.ToString(CultureInfo.InvariantCulture)} page(s); --page must be 1 to {extraction.PageCount.ToString(CultureInfo.InvariantCulture)}.");
        var page = extraction.Pages.FirstOrDefault(candidate => candidate.PageNumber == number);
        var text = new StringBuilder();
        var lines = 0;
        if (page is not null)
        {
            foreach (var region in page.Regions.OrderBy(region => region.ReadingOrder))
            {
                AppendLine(text, region, null);
                lines++;
            }
            foreach (var hidden in page.HiddenRegions ?? [])
            {
                AppendLine(text, hidden.Region, hidden.Reason.ToString());
                lines++;
            }
        }
        var window = TextWindow.Read(new StringReader(text.ToString()), offset, maxChars);
        var hiddenCount = page?.HiddenRegions?.Count ?? 0;
        var note = "Each line is [x, y of the text's baseline start in PDF points from the page's lower left corner, font size] and the text. " +
            (page is null || lines == 0 ? "The extractor found no text on this page (a scanned page needs OCR). " : "") +
            (hiddenCount > 0 ? $"{hiddenCount.ToString(CultureInfo.InvariantCulture)} line(s) are text the page draws but a viewer does not show (marked hidden). " : "") +
            (window.NextOffset is { } next ? $"More text follows: read it with --offset {next.ToString(CultureInfo.InvariantCulture)}." : "");
        return new SourceTextResult
        {
            Input = input, Format = "pdf", Locator = Locator("page", ("page", number)), Page = number,
            Content = window.Content, Offset = offset, NextOffset = window.NextOffset, TotalChars = window.Total,
            Note = note.Trim(),
        };
    }

    private static void AppendLine(StringBuilder text, PdfTextRegion region, string? hiddenReason)
    {
        var box = region.BoundingBox;
        text.Append("[x=").Append(box.X.ToString("0.##", CultureInfo.InvariantCulture))
            .Append(" y=").Append(box.Y.ToString("0.##", CultureInfo.InvariantCulture));
        if (region.FontSize is { } size) text.Append(" size=").Append(size.ToString("0.##", CultureInfo.InvariantCulture));
        if (hiddenReason is not null) text.Append(" hidden=").Append(hiddenReason);
        text.Append("] ").Append(region.Text).Append('\n');
    }
}
