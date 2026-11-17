using System.Globalization;
using System.IO.Compression;
using DocRedock.Core.Documents;
using DocRedock.RoundTrip;

namespace DocRedock.Api.SourceReading;

public sealed partial class SourceReader
{
    /// <summary>File name extensions of pictures a Word, Excel or PowerPoint package stores, with their media types.</summary>
    private static readonly Dictionary<string, string> PictureTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
        [".bmp"] = "image/bmp", [".tif"] = "image/tiff", [".tiff"] = "image/tiff", [".emf"] = "image/emf", [".wmf"] = "image/wmf",
        [".svg"] = "image/svg+xml",
    };

    /// <summary>Whether a zip part is a picture: by its file name extension. An .svg is text as well as a picture, so
    /// <c>--part</c> keeps reading it as text (<paramref name="includeSvg"/> false) and <c>--image</c> returns it as a picture.</summary>
    private static bool IsPicturePart(string entryName, bool includeSvg) =>
        PictureTypes.ContainsKey(Path.GetExtension(entryName)) && (includeSvg || !string.Equals(Path.GetExtension(entryName), ".svg", StringComparison.OrdinalIgnoreCase));

    /// <summary>The picture a request names: first as a zip part (<c>word/media/image2.png</c>) of an Office file, then as an
    /// asset id (<c>img-0002</c>) of the default extraction, which is where the export's ids come from.</summary>
    private async Task<SourceImageResult> ReadImageAsync(string input, string path, DocumentFormatKind format, string name,
        string requested, bool ocr, CancellationToken cancellationToken)
    {
        requested = requested.Trim();
        if (requested.Length == 0)
            throw new SourceReadException("invalid_locator", "--image needs a picture id such as img-0002 (shown in the Markdown) or a picture part such as word/media/image2.png.");
        if (format != DocumentFormatKind.Pdf)
        {
            using var archive = OpenPackage(path);
            if (FindEntryOrNull(archive, requested) is { } entry && IsPicturePart(entry.FullName, includeSvg: true))
                return await BuildImageResultAsync(input, path, format, name, Locator("image", ("image", requested)), ReadEntry(entry), entry.FullName, null, ocr, cancellationToken).ConfigureAwait(false);
        }
        return await BuildImageResultAsync(input, path, format, name, Locator("image", ("image", requested)), null, null, requested, ocr, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The picture stored in a zip part that <c>--part</c> named, instead of a refusal for a binary part.</summary>
    private Task<SourceImageResult> ReadPicturePartAsync(string input, string path, DocumentFormatKind format, string name,
        ZipArchiveEntry entry, bool ocr, CancellationToken cancellationToken) =>
        BuildImageResultAsync(input, path, format, name, Locator("part", ("part", entry.FullName)), ReadEntry(entry), entry.FullName, null, ocr, cancellationToken);

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream(checked((int)Math.Min(entry.Length, int.MaxValue)));
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private async Task<SourceImageResult> BuildImageResultAsync(string input, string path, DocumentFormatKind format, string name,
        Dictionary<string, object?> locator, byte[]? partBytes, string? partName, string? assetId, bool ocr, CancellationToken cancellationToken)
    {
        if (format == DocumentFormatKind.Pdf && DocumentSourceException.IsEncryptedPdf(path))
            throw DocumentSourceException.EncryptedPdf(Path.GetFileName(path));
        WorkspaceAsset? asset = null;
        DocumentGraph? graph = null;
        IReadOnlyList<WorkspaceAsset> assets = [];
        try
        {
            (graph, assets) = await documents.ExtractDefaultGraphAndAssetsAsync(path, false, null, cancellationToken).ConfigureAwait(false);
        }
        // A picture part can be returned without knowing which asset it is; an asset id cannot be found without the assets.
        catch (Exception exception) when (partName is not null && exception is not OperationCanceledException) { }
        var images = assets.Where(candidate => candidate.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (partName is not null)
            asset = images.FirstOrDefault(candidate => PartUris(candidate).Any(uri => string.Equals(uri, partName, StringComparison.OrdinalIgnoreCase)));
        else
        {
            asset = images.FirstOrDefault(candidate => string.Equals(candidate.Id, assetId, StringComparison.Ordinal))
                ?? images.FirstOrDefault(candidate => string.Equals(candidate.Id, assetId, StringComparison.OrdinalIgnoreCase));
            if (asset is null)
                throw new SourceReadException("unknown_image", images.Length == 0
                    ? $"Picture '{assetId}' was not found: this document has no pictures."
                    : $"Picture '{assetId}' was not found. Pictures of this document: {string.Join(", ", images.OrderBy(candidate => candidate.Id, StringComparer.Ordinal).Take(30).Select(candidate => candidate.Id))}" +
                      (images.Length > 30 ? $", ... ({images.Length.ToString(CultureInfo.InvariantCulture)} in all; run without a locator to list them)." : ".") +
                      " Use an id as the Markdown export shows it, or a picture part such as word/media/image1.png.");
        }

        var bytes = partBytes ?? asset!.Content.ToArray();
        var storedPart = partName ?? PartUris(asset!).FirstOrDefault();
        var extension = Path.GetExtension(partName ?? asset!.FileName).ToLowerInvariant();
        var mediaType = asset?.MediaType ?? (PictureTypes.TryGetValue(extension, out var known) ? known : "application/octet-stream");
        int? width = null, height = null;
        if (ImageDimensions.TryRead(bytes, out var pixelWidth, out var pixelHeight)) (width, height) = (pixelWidth, pixelHeight);

        int? page = null, slide = null;
        string? sheet = null, alt = null;
        if (graph is not null && asset is not null)
        {
            IReadOnlyList<SourceSlideInfo>? slides = null;
            if (format == DocumentFormatKind.Pptx)
            {
                using var archive = OpenPackage(path);
                slides = ReadSlides(archive);
            }
            (page, slide, sheet, alt) = Locate(graph, asset.Id, format, slides);
        }
        var aliases = asset is null ? [] : PartUris(asset).Where(uri => !string.Equals(uri, storedPart, StringComparison.OrdinalIgnoreCase)).ToArray();
        var recognized = ocr ? await documents.RecognizeImageAsync(bytes, mediaType, asset?.Id ?? partName ?? "image", cancellationToken).ConfigureAwait(false) : null;
        return new SourceImageResult
        {
            Input = input, Format = name, Locator = locator,
            Id = asset?.Id, Part = storedPart, MediaType = mediaType, Size = bytes.Length, PixelWidth = width, PixelHeight = height,
            Page = page, Slide = slide, Sheet = sheet, AltText = alt, AliasParts = aliases,
            Extension = extension.Length > 0 ? extension : ".bin", Content = bytes, Ocr = recognized,
            Note = asset is null && partName is not null
                ? "The export does not list this picture (it could not be matched to an asset), so it has no id; the bytes are the part as stored."
                : null,
        };
    }

    /// <summary>The zip parts an asset was read from, without the leading slash (none for a PDF's picture).</summary>
    private static IEnumerable<string> PartUris(WorkspaceAsset asset) =>
        (asset.SourcePartUri is null ? [] : new[] { asset.SourcePartUri }).Concat(asset.AliasPartUris)
            .Where(uri => !uri.StartsWith("pdf:", StringComparison.Ordinal)).Select(uri => uri.TrimStart('/'));

    /// <summary>Where the export shows a picture: the PDF page, the slide or the sheet its node sits in, and the alt text or name
    /// the document gives it. A Word picture has no page here (a .docx stores no page numbers).</summary>
    private static (int? Page, int? Slide, string? Sheet, string? Alt) Locate(DocumentGraph graph, string assetId, DocumentFormatKind format,
        IReadOnlyList<SourceSlideInfo>? slides)
    {
        foreach (var partition in graph.Partitions)
            foreach (var node in partition.Nodes)
            {
                if (node.Kind != NodeKind.Image || node.Content is not ReferenceNodeContent reference || !string.Equals(reference.Reference, assetId, StringComparison.Ordinal))
                    continue;
                var alt = string.IsNullOrWhiteSpace(reference.AltText) ? null : reference.AltText;
                var partUri = partition.SourcePartUri;
                switch (format)
                {
                    case DocumentFormatKind.Pdf:
                        var pageText = (node.Source?.PartUri ?? partUri) is { } pdfPart && pdfPart.StartsWith("pdf:page:", StringComparison.Ordinal) ? pdfPart["pdf:page:".Length..] : null;
                        return (int.TryParse(pageText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null, null, null, alt);
                    case DocumentFormatKind.Pptx when partUri is not null:
                        var found = slides?.FirstOrDefault(candidate => string.Equals(candidate.Part, partUri.TrimStart('/'), StringComparison.OrdinalIgnoreCase));
                        return (null, found?.Number ?? partition.Order + 1, null, alt);
                    case DocumentFormatKind.Xlsx when partition.Id.StartsWith("sheet-", StringComparison.Ordinal):
                        return (null, null, partition.Id["sheet-".Length..], alt);
                    default:
                        return (null, null, null, alt);
                }
            }
        return (null, null, null, null);
    }

    /// <summary>The pictures an Office file shows, as the export names them, in id order.</summary>
    private static List<SourceImageInfo> ImageInfos(DocumentGraph graph, IReadOnlyList<WorkspaceAsset> assets, DocumentFormatKind format,
        IReadOnlyList<SourceSlideInfo>? slides) =>
        assets.Where(asset => asset.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(asset => asset.Id, StringComparer.Ordinal)
            .Select(asset =>
            {
                var (page, slide, sheet, alt) = Locate(graph, asset.Id, format, slides);
                return new SourceImageInfo(asset.Id, PartUris(asset).FirstOrDefault(), asset.MediaType, asset.Content.Length, page, slide, sheet, alt);
            }).ToList();

    /// <summary>Adds the list of pictures to an Office overview. A package with no media parts has none, and says so without a
    /// conversion; otherwise the list comes from the same extraction <c>--node</c> and <c>--image</c> use.</summary>
    private async Task<SourceOverviewResult> WithImagesAsync(SourceOverviewResult overview, string path, DocumentFormatKind format,
        CancellationToken cancellationToken)
    {
        if (!overview.Parts.Any(part => part.Name.Contains("/media/", StringComparison.OrdinalIgnoreCase)) && overview.PartCount <= overview.Parts.Count)
            return overview with { Images = [], ImageCount = 0 };
        try
        {
            var (graph, assets) = await documents.ExtractDefaultGraphAndAssetsAsync(path, false, null, cancellationToken).ConfigureAwait(false);
            var all = ImageInfos(graph, assets, format, overview.Slides);
            var shown = all.Take(SourceOverviewResult.MaxListedImages).ToArray();
            var more = all.Count > shown.Length ? $" The list shows the first {shown.Length} of {all.Count} pictures." : "";
            var hint = all.Count > 0 ? " Read a picture with --image ID (the id the Markdown shows), and add --ocr to read its text." : "";
            return overview with { Images = shown, ImageCount = all.Count, Note = overview.Note + hint + more };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return overview with { Note = overview.Note + " The pictures could not be listed: " + exception.Message };
        }
    }
}
