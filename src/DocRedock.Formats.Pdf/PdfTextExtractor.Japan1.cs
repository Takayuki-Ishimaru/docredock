using System.IO.Compression;
using System.Text;

namespace DocRedock.Formats.Pdf;

public static partial class PdfTextExtractor
{
    private static readonly Lazy<PdfToUnicodeMap> Japan1Unicode = new(() =>
    {
        using var bytes = new MemoryStream(Convert.FromBase64String(PdfJapan1UnicodeData.CMapGzipBase64));
        using var gzip = new GZipStream(bytes, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.ASCII);
        return PdfToUnicodeMap.Parse(reader.ReadToEnd(), TimeSpan.FromSeconds(5)).ReportingMissingCodes();
    });
}
