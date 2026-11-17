using DocRedock.Api.SourceReading;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Providers.Abstractions.Providers;
using DocRedock.RoundTrip;
using DocRedock.VisualInference;

namespace DocRedock.Api;

public sealed partial class DocumentService
{
    /// <summary>The languages the one-picture OCR of <c>source --image --ocr</c> reads, as the export's default does.</summary>
    private static readonly string[] DefaultOcrLanguages = ["jpn", "eng"];

    /// <summary>Converts a document to its extraction graph with the options <c>export</c> uses when none are given (visible
    /// content, tracked changes as markup unless <paramref name="revisions"/> says otherwise, safe visual inference), without
    /// writing any output. The node ids are those of an export's AI package, so a node id found in one can be looked up here.
    /// OCR is off unless <paramref name="enableOcr"/> is set: it adds nodes of its own and costs time.</summary>
    internal async Task<DocumentGraph> ExtractDefaultGraphAsync(string source, bool enableOcr, string? revisions,
        CancellationToken cancellationToken) =>
        (await ExtractDefaultGraphAndAssetsAsync(source, enableOcr, revisions, cancellationToken).ConfigureAwait(false)).Graph;

    /// <summary>The graph of <see cref="ExtractDefaultGraphAsync"/> and the pictures the export would write as assets, with the
    /// ids (<c>img-0002</c>; <c>pdf-img-0005</c> for a PDF's pictures) the export prints in its image placeholders, OCR tables
    /// and <c>.assets</c> file names. Asset ids do not depend on the export's options, so these are the ones an export has.</summary>
    internal async Task<(DocumentGraph Graph, IReadOnlyList<WorkspaceAsset> Assets)> ExtractDefaultGraphAndAssetsAsync(
        string source, bool enableOcr, string? revisions, CancellationToken cancellationToken)
    {
        var options = new ReadableDocumentExportOptions(Path.GetFullPath(source), "unused.md", EnableOcr: enableOcr,
            OcrLanguages: ["jpn", "eng"], Revisions: revisions, OcrAuto: enableOcr);
        using var inferenceScope = VisualInferenceContext.Push(options.InferenceMode);
        var (graph, _, assets) = await ExtractReadableGraphAsync(options.SourcePath, options, cancellationToken).ConfigureAwait(false);
        return (graph, assets);
    }

    /// <summary>Reads the text of one picture with the OCR engine this service was given (the one the export uses). A missing
    /// or failing engine is an answer, not an error: the result says it is not available and why.</summary>
    internal async Task<SourceImageOcr> RecognizeImageAsync(ReadOnlyMemory<byte> content, string mediaType, string assetId,
        CancellationToken cancellationToken)
    {
        if (ocr is null)
            return SourceImageOcr.NotAvailable("No OCR engine is configured; run docredock doctor to see which engines can be installed.");
        var engine = ocr.Descriptor.ProviderId;
        if (mediaType is not ("image/png" or "image/jpeg" or "image/gif" or "image/bmp" or "image/tiff" or "image/webp"))
            return SourceImageOcr.NotAvailable($"OCR reads png, jpeg, gif, bmp, tiff and webp pictures; this one is {mediaType}.", engine);
        int? width = null, height = null;
        if (ImageDimensions.TryRead(content.Span, out var knownWidth, out var knownHeight))
        {
            (width, height) = (knownWidth, knownHeight);
            if (Math.Min(knownWidth, knownHeight) < MinimumOcrImageSide)
                return new SourceImageOcr(true, $"The picture is {knownWidth}x{knownHeight} pixels; OCR needs at least {MinimumOcrImageSide} pixels in each direction.",
                    engine, "image-pixels", []);
        }
        OcrAttemptResult attempt;
        try
        {
            await using var image = new MemoryStream(content.ToArray(), writable: false);
            attempt = await ocr.RecognizeAsync(new OcrInput(assetId, image, mediaType), new OcrOptions(DefaultOcrLanguages), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SourceImageOcr.NotAvailable("The OCR engine failed: " + exception.Message, engine);
        }
        if (attempt.Status != OcrProcessingStatus.Completed || attempt.Result is null)
        {
            var reason = string.Join(" ", attempt.Diagnostics.Select(item => item.Message).Where(message => !string.IsNullOrWhiteSpace(message)));
            return SourceImageOcr.NotAvailable(reason.Length > 0 ? reason : $"The OCR engine did not complete (status {attempt.Status}).", engine);
        }
        var lines = new List<SourceOcrLine>();
        var space = "image-pixels";
        foreach (var region in attempt.Result.Regions.Where(region => !string.IsNullOrWhiteSpace(region.Text)))
        {
            var box = region.BoundingBox;
            if (box is null)
            {
                lines.Add(new SourceOcrLine(region.Text, null, null, null, null, region.Confidence));
                continue;
            }
            double x = box.X, y = box.Y, w = box.Width, h = box.Height;
            // Apple Vision answers as fractions of the picture from its lower left corner; say pixels from the upper left.
            if (box.CoordinateSpace == "vision-normalized-bottom-left" && width is { } pixelWidth && height is { } pixelHeight)
                (x, y, w, h) = (box.X * pixelWidth, (1 - box.Y - box.Height) * pixelHeight, box.Width * pixelWidth, box.Height * pixelHeight);
            else space = box.CoordinateSpace;
            lines.Add(new SourceOcrLine(region.Text, Math.Round(x, 2), Math.Round(y, 2), Math.Round(w, 2), Math.Round(h, 2), region.Confidence));
        }
        return new SourceImageOcr(true, null, engine, space, lines);
    }
}
