namespace DocRedock.Providers.Abstractions.Providers;

public sealed record PdfRasterizationOptions(
    int Dpi = 200,
    long MaxPixelsPerPage = 40_000_000,
    long MaxTotalPixels = 200_000_000,
    TimeSpan? Timeout = null);

public sealed record RasterizedPdfPage(
    int PageNumber,
    string MediaType,
    ReadOnlyMemory<byte> Content,
    int PixelWidth,
    int PixelHeight);

/// <summary>Explicit local PDF rasterizer boundary; implementations must not fetch network resources.</summary>
public interface IPdfRasterizer
{
    ProviderDescriptor Descriptor { get; }
    ValueTask<IReadOnlyList<RasterizedPdfPage>> RasterizeAsync(
        string pdfPath,
        IReadOnlyList<int> pageNumbers,
        PdfRasterizationOptions options,
        CancellationToken cancellationToken = default);
}
