using System.Buffers.Binary;
using System.IO.Compression;

namespace DocRedock.Render;

internal sealed record PngRasterImage(byte[] PngBytes, int Width, int Height, byte[] RgbBytes)
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private const int MaxPixels = 20_000_000;

    public static PngRasterImage Decode(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length < Signature.Length || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
            throw new InvalidDataException("Mermaid renderer output is not a PNG image.");

        int width = 0, height = 0, bitDepth = 0, colorType = -1, interlace = -1;
        byte[]? palette = null, transparency = null;
        using var compressed = new MemoryStream();
        var offset = Signature.Length;
        var sawHeader = false;
        while (offset <= png.Length - 12)
        {
            var lengthValue = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            if (lengthValue > int.MaxValue) throw new InvalidDataException("PNG chunk is too large.");
            var length = (int)lengthValue;
            if (length > png.Length - offset - 12) throw new InvalidDataException("PNG chunk is truncated.");
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            switch (type)
            {
                case "IHDR":
                    if (sawHeader || length != 13) throw new InvalidDataException("PNG IHDR chunk is invalid.");
                    width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data));
                    height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
                    bitDepth = data[8]; colorType = data[9]; interlace = data[12]; sawHeader = true;
                    break;
                case "PLTE": palette = data.ToArray(); break;
                case "tRNS": transparency = data.ToArray(); break;
                case "IDAT": compressed.Write(data); break;
                case "IEND": offset = png.Length; continue;
            }
            offset += checked(length + 12);
        }

        if (!sawHeader || width <= 0 || height <= 0) throw new InvalidDataException("PNG has no valid dimensions.");
        if ((long)width * height > MaxPixels) throw new InvalidDataException($"Mermaid PNG exceeds the {MaxPixels:N0}-pixel limit.");
        if (bitDepth != 8 || interlace != 0) throw new InvalidDataException("Mermaid PNG must use 8-bit, non-interlaced pixels.");
        var bytesPerPixel = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"Mermaid PNG color type {colorType} is unsupported.") };
        if (colorType == 3 && (palette is null || palette.Length == 0 || palette.Length % 3 != 0))
            throw new InvalidDataException("Indexed Mermaid PNG has no valid palette.");

        var rowBytes = checked(width * bytesPerPixel);
        var expectedBytes = checked((rowBytes + 1) * height);
        var filtered = new byte[expectedBytes];
        compressed.Position = 0;
        using (var inflater = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true))
        {
            var readTotal = 0;
            while (readTotal < filtered.Length)
            {
                var read = inflater.Read(filtered, readTotal, filtered.Length - readTotal);
                if (read == 0) break;
                readTotal += read;
            }
            if (readTotal != filtered.Length || inflater.ReadByte() != -1) throw new InvalidDataException("Mermaid PNG pixel data has an unexpected length.");
        }

        var raw = new byte[checked(rowBytes * height)];
        for (var row = 0; row < height; row++)
        {
            var filter = filtered[row * (rowBytes + 1)];
            var sourceRow = filtered.AsSpan(row * (rowBytes + 1) + 1, rowBytes);
            var targetRow = raw.AsSpan(row * rowBytes, rowBytes);
            var previousRow = row == 0 ? Span<byte>.Empty : raw.AsSpan((row - 1) * rowBytes, rowBytes);
            for (var column = 0; column < rowBytes; column++)
            {
                var left = column >= bytesPerPixel ? targetRow[column - bytesPerPixel] : (byte)0;
                var up = row > 0 ? previousRow[column] : (byte)0;
                var upperLeft = row > 0 && column >= bytesPerPixel ? previousRow[column - bytesPerPixel] : (byte)0;
                var predictor = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upperLeft),
                    _ => throw new InvalidDataException($"Mermaid PNG uses unsupported filter {filter}.")
                };
                targetRow[column] = unchecked((byte)(sourceRow[column] + predictor));
            }
        }

        var rgb = new byte[checked(width * height * 3)];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            byte red, green, blue, alpha = 255;
            var at = pixel * bytesPerPixel;
            switch (colorType)
            {
                case 0: red = green = blue = raw[at]; if (transparency is { Length: >= 2 } && raw[at] == transparency[1]) alpha = 0; break;
                case 2: red = raw[at]; green = raw[at + 1]; blue = raw[at + 2]; break;
                case 3:
                    var index = raw[at];
                    if (index * 3 + 2 >= palette!.Length) throw new InvalidDataException("Mermaid PNG palette index is out of range.");
                    red = palette[index * 3]; green = palette[index * 3 + 1]; blue = palette[index * 3 + 2];
                    if (transparency is not null && index < transparency.Length) alpha = transparency[index];
                    break;
                case 4: red = green = blue = raw[at]; alpha = raw[at + 1]; break;
                case 6: red = raw[at]; green = raw[at + 1]; blue = raw[at + 2]; alpha = raw[at + 3]; break;
                default: throw new InvalidOperationException();
            }
            var rgbAt = pixel * 3;
            rgb[rgbAt] = OnWhite(red, alpha); rgb[rgbAt + 1] = OnWhite(green, alpha); rgb[rgbAt + 2] = OnWhite(blue, alpha);
        }
        return new PngRasterImage(png, width, height, rgb);
    }

    public byte[] DeflateRgb()
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) compressor.Write(RgbBytes);
        return output.ToArray();
    }

    private static byte OnWhite(byte component, byte alpha) => (byte)((component * alpha + 255 * (255 - alpha) + 127) / 255);

    private static byte Paeth(byte left, byte up, byte upperLeft)
    {
        var estimate = left + up - upperLeft;
        var leftDistance = Math.Abs(estimate - left);
        var upDistance = Math.Abs(estimate - up);
        var upperLeftDistance = Math.Abs(estimate - upperLeft);
        return leftDistance <= upDistance && leftDistance <= upperLeftDistance ? left : upDistance <= upperLeftDistance ? up : upperLeft;
    }
}
