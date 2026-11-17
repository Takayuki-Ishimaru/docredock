using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace DocRedock.Tests.Pdf;

/// <summary>Raw-byte PDFs with Image XObjects, and a reader for the PNG files the extractor writes, for the tests of
/// embedded image extraction.</summary>
internal static class PdfImageFixtures
{
    /// <summary>An object of the PDF: the dictionary entries (without <c>&lt;&lt; &gt;&gt;</c>) and, for a stream, its bytes.</summary>
    internal sealed record PdfObject(int Id, string Entries, byte[]? Stream = null);

    /// <summary>One page: what it draws, and the XObject names it can use.</summary>
    internal sealed record PdfPage(string Content, string XObjects = "/Im1 5 0 R", string Text = "Body text");

    /// <summary>A PDF whose pages each show <see cref="PdfPage.Text"/> and draw <see cref="PdfPage.Content"/>; object ids
    /// from 100 up are the pages, so the image objects can be numbered freely below that.</summary>
    internal static byte[] Build(IReadOnlyList<PdfPage> pages, IEnumerable<PdfObject> objects, string extra = "")
    {
        var output = new MemoryStream();
        void Write(string text) => output.Write(Encoding.Latin1.GetBytes(text));
        Write("%PDF-1.5\n");
        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            var content = $"BT 10 190 Td ({page.Text}) Tj ET\n{page.Content}";
            var pageId = 100 + index * 2;
            Write($"{pageId} 0 obj << /Type /Page /MediaBox [0 0 200 200] /Contents {pageId + 1} 0 R /Resources << /XObject << {page.XObjects} >> >> >> endobj\n");
            Write($"{pageId + 1} 0 obj << /Length {content.Length} >> stream\n{content}\nendstream endobj\n");
        }
        foreach (var item in objects)
        {
            if (item.Stream is null)
            {
                Write($"{item.Id} 0 obj << {item.Entries} >> endobj\n");
                continue;
            }
            Write($"{item.Id} 0 obj << {item.Entries} /Length {item.Stream.Length} >> stream\n");
            output.Write(item.Stream);
            Write("\nendstream endobj\n");
        }
        Write(extra + "%%EOF");
        return output.ToArray();
    }

    internal static byte[] OnePage(string content, params PdfObject[] objects) =>
        Build([new PdfPage(content)], objects);

    /// <summary>zlib data for <paramref name="data"/>.</summary>
    internal static byte[] Deflate(byte[] data)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(data);
        return compressed.ToArray();
    }

    /// <summary>An image XObject with the given extra dictionary entries and (usually Flate-coded) data.</summary>
    internal static PdfObject Image(int id, int width, int height, string colorSpace, int bits, byte[] samples, string extra = "", bool flate = true) =>
        new(id, $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {colorSpace} /BitsPerComponent {bits} " +
            (flate ? "/Filter /FlateDecode " : "") + extra, flate ? Deflate(samples) : samples);

    /// <summary>A picture with a distinct value in every channel of every pixel.</summary>
    internal static byte[] Gradient(int width, int height, int channels)
    {
        var data = new byte[width * height * channels];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                for (var channel = 0; channel < channels; channel++)
                    data[(y * width + x) * channels + channel] = (byte)(x * 37 + y * 53 + channel * 91 + 7);
        return data;
    }

    /// <summary>The rows of <paramref name="data"/> PNG-filtered the way a PDF producer would, with the filter type of row
    /// <c>n</c> being <c>n % 5</c>, so that every filter is read.</summary>
    internal static byte[] PngPredicted(byte[] data, int width, int height, int bytesPerPixel)
    {
        var rowBytes = width * bytesPerPixel;
        var output = new byte[(rowBytes + 1) * height];
        for (var row = 0; row < height; row++)
        {
            var filter = row % 5;
            output[row * (rowBytes + 1)] = (byte)filter;
            for (var index = 0; index < rowBytes; index++)
            {
                int Pixel(int r, int i) => r < 0 || i < 0 ? 0 : data[r * rowBytes + i];
                var left = Pixel(row, index - bytesPerPixel);
                var up = Pixel(row - 1, index);
                var upLeft = Pixel(row - 1, index - bytesPerPixel);
                var estimate = left + up - upLeft;
                var (dl, du, dul) = (Math.Abs(estimate - left), Math.Abs(estimate - up), Math.Abs(estimate - upLeft));
                var predicted = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    _ => dl <= du && dl <= dul ? left : du <= dul ? up : upLeft,
                };
                output[row * (rowBytes + 1) + 1 + index] = unchecked((byte)(data[row * rowBytes + index] - predicted));
            }
        }
        return output;
    }

    /// <summary>A decoded 8-bit PNG: its colour type and its samples, one row after another, unfiltered.</summary>
    internal sealed record Png(int Width, int Height, int ColorType, int Channels, byte[] Samples, byte[]? Palette)
    {
        /// <summary>The samples of one pixel.</summary>
        public byte[] Pixel(int x, int y) => Samples.AsSpan((y * Width + x) * Channels, Channels).ToArray();
    }

    internal static Png ReadPng(byte[] png)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        int width = 0, height = 0, colorType = -1;
        byte[]? palette = null;
        using var compressed = new MemoryStream();
        var offset = 8;
        while (offset < png.Length)
        {
            var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            if (type == "IHDR")
            {
                width = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data);
                height = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                Assert.Equal(8, data[8]);
                colorType = data[9];
            }
            else if (type == "PLTE") palette = data.ToArray();
            else if (type == "IDAT") compressed.Write(data);
            offset += length + 12;
        }
        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException("color type " + colorType) };
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var filtered = new MemoryStream();
        zlib.CopyTo(filtered);
        var rows = filtered.ToArray();
        var rowBytes = width * channels;
        Assert.Equal((rowBytes + 1) * height, rows.Length);
        var samples = new byte[rowBytes * height];
        for (var row = 0; row < height; row++)
        {
            var filter = rows[row * (rowBytes + 1)];
            for (var index = 0; index < rowBytes; index++)
            {
                int Sample(int r, int i) => r < 0 || i < 0 ? 0 : samples[r * rowBytes + i];
                var left = Sample(row, index - channels);
                var up = Sample(row - 1, index);
                var upLeft = Sample(row - 1, index - channels);
                var estimate = left + up - upLeft;
                var (dl, du, dul) = (Math.Abs(estimate - left), Math.Abs(estimate - up), Math.Abs(estimate - upLeft));
                var predicted = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    _ => dl <= du && dl <= dul ? left : du <= dul ? up : upLeft,
                };
                samples[row * rowBytes + index] = unchecked((byte)(rows[row * (rowBytes + 1) + 1 + index] + predicted));
            }
        }
        return new Png(width, height, colorType, channels, samples, palette);
    }

    /// <summary>A minimal JPEG: a start of image, a frame header of <paramref name="components"/> components, and an end of
    /// image. The extractor reads the frame and keeps the bytes as they are.</summary>
    internal static byte[] Jpeg(int width, int height, int components, byte marker = 0xC0)
    {
        var frame = new List<byte> { 0xFF, marker, 0, (byte)(8 + components * 3), 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)components };
        for (var component = 0; component < components; component++) frame.AddRange([(byte)(component + 1), 0x11, 0]);
        return [0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, .. frame, 0xFF, 0xD9];
    }

    internal static string N(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
