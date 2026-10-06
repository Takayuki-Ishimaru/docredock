using System.Buffers.Binary;
using System.IO.Compression;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Providers.Abstractions.Providers;
using DocRedock.Render;

namespace DocRedock.Tests.Api;

/// <summary>Images too small to hold text (spacer pixels, hairline rules) are not sent to OCR:
/// Apple Vision rejects them and its helper used to crash with a stack dump in the warning.</summary>
public sealed class OcrImageSizeTests
{
    [Fact]
    public void Header_sizes_are_read_from_png_gif_bmp_and_jpeg()
    {
        Assert.True(ImageDimensions.TryRead(PngRasterImage.Encode(7, 2, new byte[7 * 2 * 3]), out var width, out var height));
        Assert.Equal((7, 2), (width, height));

        byte[] gif = [.. "GIF89a"u8, 0x2C, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00];
        Assert.True(ImageDimensions.TryRead(gif, out width, out height));
        Assert.Equal((300, 2), (width, height));

        Assert.True(ImageDimensions.TryRead(Bmp(40, width: 5, height: -9), out width, out height));
        Assert.Equal((5, 9), (width, height));
        Assert.True(ImageDimensions.TryRead(Bmp(12, width: 640, height: 1), out width, out height));
        Assert.Equal((640, 1), (width, height));

        Assert.True(ImageDimensions.TryRead(Jpeg(0xC2, width: 1024, height: 2, withApp0: true), out width, out height));
        Assert.Equal((1024, 2), (width, height));
        Assert.True(ImageDimensions.TryRead(Jpeg(0xC0, width: 3, height: 480, withApp0: false), out width, out height));
        Assert.Equal((3, 480), (width, height));
    }

    [Fact]
    public void Truncated_unknown_and_undefined_sizes_are_reported_as_unknown()
    {
        byte[] pngSignatureOnly = [137, 80, 78, 71, 13, 10, 26, 10];
        Assert.False(ImageDimensions.TryRead(pngSignatureOnly, out _, out _));
        Assert.False(ImageDimensions.TryRead(PngRasterImage.Encode(2, 2, new byte[12])[..20], out _, out _));
        Assert.False(ImageDimensions.TryRead("not an image at all"u8, out _, out _));
        Assert.False(ImageDimensions.TryRead([], out _, out _));
        // A JPEG whose frame header leaves the height to a later DNL marker, one cut before its
        // frame header, and one whose scan starts without a frame header.
        Assert.False(ImageDimensions.TryRead(Jpeg(0xC0, width: 100, height: 0, withApp0: false), out _, out _));
        Assert.False(ImageDimensions.TryRead(Jpeg(0xC0, width: 100, height: 2, withApp0: true)[..12], out _, out _));
        Assert.False(ImageDimensions.TryRead([0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 0, 0, 0, 0, 0, 0], out _, out _));
        // A Huffman table (C4) is not a frame header.
        Assert.False(ImageDimensions.TryRead(Jpeg(0xC4, width: 2, height: 2, withApp0: false), out _, out _));
        Assert.False(ImageDimensions.TryRead(Bmp(40, width: 0, height: 4), out _, out _));
    }

    [Fact]
    public async Task Images_under_three_pixels_across_or_high_are_not_sent_to_ocr()
    {
        var root = Path.Combine(Path.GetTempPath(), "docredock-ocr-size-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.xlsx");
            await new MarkdownRenderer().RenderAsync("# Image workbook\n\nBody", RenderFormat.Xlsx, source);
            using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
            {
                foreach (var (name, width, height) in new[] { ("image1.png", 2, 2), ("image2.png", 40, 20), ("image3.png", 600, 2), ("image4.png", 3, 3) })
                {
                    await using var media = archive.CreateEntry("xl/media/" + name).Open();
                    await media.WriteAsync(PngRasterImage.Encode(width, height, new byte[width * height * 3]));
                }
            }
            var ocr = new SizeRecordingOcrEngine();

            var result = await new DocumentService(ocr).ExportReadableAsync(new ReadableDocumentExportOptions(
                source, Path.Combine(root, "out", "source.md"), EnableOcr: true, OcrLanguages: ["jpn", "eng"]));

            Assert.Equal([(3, 3), (40, 20)], ocr.Seen.Order().ToArray());
            var skipped = result.Diagnostics.Where(item => item.Code == "OcrImageTooSmall").ToArray();
            Assert.Equal(2, skipped.Length);
            Assert.All(skipped, item => Assert.Equal(DiagnosticSeverity.Information, item.Severity));
            Assert.Contains(skipped, item => item.Message == "OCR was not run on a 2x2-pixel image; OCR needs at least 3 pixels in each direction.");
            Assert.Contains(skipped, item => item.Message.StartsWith("OCR was not run on a 600x2-pixel image", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Diagnostics, item => item.Severity >= DiagnosticSeverity.Warning && item.Code.Contains("Ocr", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Bmp(uint headerSize, int width, int height)
    {
        var bytes = new byte[14 + (int)headerSize];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), headerSize);
        if (headerSize == 12)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), (ushort)height);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        }
        return bytes;
    }

    private static byte[] Jpeg(byte frameMarker, ushort width, ushort height, bool withApp0)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        if (withApp0)
            bytes.AddRange([0xFF, 0xE0, 0x00, 0x10, .. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        bytes.AddRange([0xFF, frameMarker, 0x00, 0x11, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
            3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1]);
        bytes.AddRange([0xFF, 0xD9]);
        return [.. bytes];
    }

    private sealed class SizeRecordingOcrEngine : IOcrEngine
    {
        public List<(int Width, int Height)> Seen { get; } = [];

        public ProviderDescriptor Descriptor { get; } = new("test.ocr", new Version(1, 0), 1,
            new HashSet<string> { "ocr.text" }, "MIT", "built-in", true);

        public ValueTask<OcrAttemptResult> RecognizeAsync(OcrInput input, OcrOptions options, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            input.Image.CopyTo(buffer);
            var image = PngRasterImage.Decode(buffer.ToArray());
            Seen.Add((image.Width, image.Height));
            return ValueTask.FromResult(new OcrAttemptResult(OcrProcessingStatus.Completed, new OcrResult("", []), []));
        }
    }
}
