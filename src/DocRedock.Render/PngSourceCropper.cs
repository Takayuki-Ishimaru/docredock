using System.Buffers.Binary;
using System.IO.Compression;

namespace DocRedock.Render;

/// <summary>Pre-crops a PNG against fractional CSS clip insets before it reaches the PDF page, so
/// colors from the discarded boundary cannot bleed into the resampled clip edge. The fractional
/// window is snapped to the whole source pixels it retains and those pixels are copied unchanged;
/// output is an 8-bit RGBA PNG with alpha preserved (never composited onto white).
/// Stateless: invalid fractions, empty or outset regions, and unsupported PNGs yield null.</summary>
public static class PngSourceCropper
{
    private const int MaxPixels = 20_000_000;
    private const int MaxInputBytes = 64 * 1024 * 1024;
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static byte[]? TryCrop(byte[] source, double left, double top, double right, double bottom, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        if (!IsInset(left) || !IsInset(top) || !IsInset(right) || !IsInset(bottom)) return null;
        if (left + right >= 1.0 || top + bottom >= 1.0) return null;

        if (!TryDecodeRgba(source, cancellationToken, out var width, out var height, out var rgba)) return null;

        var x0 = left * width;
        var x1 = (1.0 - right) * width;
        var y0 = top * height;
        var y1 = (1.0 - bottom) * height;

        // The integer pixels the fractional window retains; taps are clamped to this window so
        // colors outside it can never bleed in.
        var retainedLeft = Math.Clamp((int)Math.Round(x0, MidpointRounding.AwayFromZero), 0, width - 1);
        var retainedRight = Math.Clamp((int)Math.Round(x1, MidpointRounding.AwayFromZero) - 1, retainedLeft, width - 1);
        var retainedTop = Math.Clamp((int)Math.Round(y0, MidpointRounding.AwayFromZero), 0, height - 1);
        var retainedBottom = Math.Clamp((int)Math.Round(y1, MidpointRounding.AwayFromZero) - 1, retainedTop, height - 1);
        // Snap the window to those whole pixels: a window that starts between two pixels would
        // resample every retained pixel and blend neighbouring colors inside the crop as well.
        x0 = retainedLeft; x1 = retainedRight + 1; y0 = retainedTop; y1 = retainedBottom + 1;
        var outWidth = retainedRight - retainedLeft + 1;
        var outHeight = retainedBottom - retainedTop + 1;

        var output = new byte[checked(outWidth * outHeight * 4)];
        var xTap = new int[outWidth];
        var xWeight = new double[outWidth];
        for (var column = 0; column < outWidth; column++)
        {
            var center = x0 + (column + 0.5) * ((x1 - x0) / outWidth) - 0.5;
            var clamped = Math.Clamp(center, retainedLeft, retainedRight);
            var tap = Math.Min((int)Math.Floor(clamped), retainedRight > retainedLeft ? retainedRight - 1 : retainedLeft);
            xTap[column] = tap;
            xWeight[column] = clamped - tap;
        }

        for (var targetRow = 0; targetRow < outHeight; targetRow++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var center = y0 + (targetRow + 0.5) * ((y1 - y0) / outHeight) - 0.5;
            var clamped = Math.Clamp(center, retainedTop, retainedBottom);
            var tap = Math.Min((int)Math.Floor(clamped), retainedBottom > retainedTop ? retainedBottom - 1 : retainedTop);
            var yWeight = clamped - tap;
            var topRow = tap;
            var bottomRow = yWeight == 0 ? tap : tap + 1;
            for (var column = 0; column < outWidth; column++)
            {
                var horizontalWeight = xWeight[column];
                var leftColumn = xTap[column];
                var rightColumn = horizontalWeight == 0 ? leftColumn : leftColumn + 1;
                var at = (targetRow * outWidth + column) * 4;
                // Weights of 0 or 1 concentrate the sample on a single retained pixel; copy it
                // byte for byte, since a premultiplied round-trip cannot preserve color at alpha 0.
                if (yWeight is 0.0 or 1.0 && horizontalWeight is 0.0 or 1.0)
                {
                    var sourceAt = ((yWeight == 1.0 ? bottomRow : topRow) * width + (horizontalWeight == 1.0 ? rightColumn : leftColumn)) * 4;
                    output[at] = rgba[sourceAt];
                    output[at + 1] = rgba[sourceAt + 1];
                    output[at + 2] = rgba[sourceAt + 2];
                    output[at + 3] = rgba[sourceAt + 3];
                    continue;
                }
                var topLeft = (topRow * width + leftColumn) * 4;
                var topRight = (topRow * width + rightColumn) * 4;
                var bottomLeft = (bottomRow * width + leftColumn) * 4;
                var bottomRight = (bottomRow * width + rightColumn) * 4;
                var wTopLeft = (1.0 - yWeight) * (1.0 - horizontalWeight);
                var wTopRight = (1.0 - yWeight) * horizontalWeight;
                var wBottomLeft = yWeight * (1.0 - horizontalWeight);
                var wBottomRight = 1.0 - wTopLeft - wTopRight - wBottomLeft;
                var alpha = (rgba[topLeft + 3] * wTopLeft + rgba[topRight + 3] * wTopRight + rgba[bottomLeft + 3] * wBottomLeft + rgba[bottomRight + 3] * wBottomRight) / 255.0;
                output[at + 3] = (byte)Math.Clamp((int)Math.Round(alpha * 255.0), 0, 255);
                for (var channel = 0; channel < 3; channel++)
                {
                    var premultiplied = Premultiplied(rgba, topLeft, channel) * wTopLeft
                        + Premultiplied(rgba, topRight, channel) * wTopRight
                        + Premultiplied(rgba, bottomLeft, channel) * wBottomLeft
                        + Premultiplied(rgba, bottomRight, channel) * wBottomRight;
                    // Un-premultiply; a fully transparent blend has no color to keep.
                    var value = alpha > 0 ? premultiplied / alpha : 0.0;
                    output[at + channel] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
                }
            }
        }

        return EncodeRgba(outWidth, outHeight, output, cancellationToken);
    }

    private static bool IsInset(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0.0 && value < 1.0;

    private static double Premultiplied(byte[] rgba, int at, int channel) => rgba[at + channel] * rgba[at + 3] / 255.0;

    private static bool TryDecodeRgba(byte[] png, CancellationToken cancellationToken, out int width, out int height, out byte[] rgba)
    {
        width = height = 0;
        rgba = [];
        if (png.Length > MaxInputBytes) return false;
        if (png.Length < Signature.Length || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature)) return false;

        uint rawWidth = 0, rawHeight = 0;
        int bitDepth = 0, colorType = -1, interlace = -1;
        byte[]? palette = null, transparency = null;
        using var compressed = new MemoryStream();
        var offset = Signature.Length;
        var sawHeader = false;
        var sawEnd = false;
        while (offset <= png.Length - 12)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lengthValue = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            if (lengthValue > int.MaxValue - 12) return false;
            var length = (int)lengthValue;
            if (length > png.Length - offset - 12) return false;
            var typeBytes = png.AsSpan(offset + 4, 4);
            var type = System.Text.Encoding.ASCII.GetString(typeBytes);
            var data = png.AsSpan(offset + 8, length);
            if (BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length, 4)) != Crc32(typeBytes, data)) return false;
            switch (type)
            {
                case "IHDR":
                    if (sawHeader || length != 13) return false;
                    rawWidth = BinaryPrimitives.ReadUInt32BigEndian(data);
                    rawHeight = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    bitDepth = data[8]; colorType = data[9]; interlace = data[12]; sawHeader = true;
                    break;
                case "PLTE": palette = data.ToArray(); break;
                case "tRNS": transparency = data.ToArray(); break;
                case "IDAT": compressed.Write(data); break;
                case "IEND": sawEnd = true; offset = png.Length; continue;
            }
            offset += length + 12;
        }

        if (!sawEnd || !sawHeader || rawWidth == 0 || rawHeight == 0) return false;
        if ((long)rawWidth * rawHeight > MaxPixels) return false;
        width = (int)rawWidth;
        height = (int)rawHeight;
        if (bitDepth != 8 || interlace != 0) return false;
        var bytesPerPixel = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (bytesPerPixel == 0) return false;
        if (colorType == 3 && (palette is null || palette.Length == 0 || palette.Length % 3 != 0)) return false;

        var rowBytes = width * bytesPerPixel;
        var filtered = new byte[checked((rowBytes + 1) * height)];
        compressed.Position = 0;
        try
        {
            using var inflater = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);
            var readTotal = 0;
            while (readTotal < filtered.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = inflater.Read(filtered, readTotal, Math.Min(filtered.Length - readTotal, 1 << 20));
                if (read == 0) break;
                readTotal += read;
            }
            if (readTotal != filtered.Length || inflater.ReadByte() != -1) return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }

        var raw = new byte[rowBytes * height];
        for (var row = 0; row < height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                    _ => -1
                };
                if (predictor < 0) return false;
                targetRow[column] = unchecked((byte)(sourceRow[column] + predictor));
            }
        }

        rgba = new byte[checked(width * height * 4)];
        for (var pixel = 0L; pixel < (long)width * height; pixel++)
        {
            if ((pixel & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
            byte red, green, blue, alpha = 255;
            var at = (int)pixel * bytesPerPixel;
            switch (colorType)
            {
                case 0: red = green = blue = raw[at]; if (transparency is { Length: >= 2 } && raw[at] == transparency[1]) alpha = 0; break;
                case 2:
                    red = raw[at]; green = raw[at + 1]; blue = raw[at + 2];
                    if (transparency is { Length: >= 6 } && red == transparency[1] && green == transparency[3] && blue == transparency[5]) alpha = 0;
                    break;
                case 3:
                    var index = raw[at];
                    if (index * 3 + 2 >= palette!.Length) return false;
                    red = palette[index * 3]; green = palette[index * 3 + 1]; blue = palette[index * 3 + 2];
                    if (transparency is not null && index < transparency.Length) alpha = transparency[index];
                    break;
                case 4: red = green = blue = raw[at]; alpha = raw[at + 1]; break;
                case 6: red = raw[at]; green = raw[at + 1]; blue = raw[at + 2]; alpha = raw[at + 3]; break;
                default: return false;
            }
            var rgbaAt = (int)pixel * 4;
            rgba[rgbaAt] = red; rgba[rgbaAt + 1] = green; rgba[rgbaAt + 2] = blue; rgba[rgbaAt + 3] = alpha;
        }
        return true;
    }

    private static byte[] EncodeRgba(int width, int height, byte[] rgba, CancellationToken cancellationToken)
    {
        var rowBytes = width * 4;
        var filtered = new byte[checked((rowBytes + 1) * height)];
        for (var row = 0; row < height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rgba.AsSpan(row * rowBytes, rowBytes).CopyTo(filtered.AsSpan(row * (rowBytes + 1) + 1, rowBytes));
        }
        using var deflated = new MemoryStream();
        using (var compressor = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true)) compressor.Write(filtered);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8; header[9] = 6;
        using var output = new MemoryStream();
        output.Write(Signature);
        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "IDAT"u8, deflated.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(type, data));
        output.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in type) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        foreach (var value in data) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var index = 0u; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }

    private static byte Paeth(byte left, byte up, byte upperLeft)
    {
        var estimate = left + up - upperLeft;
        var leftDistance = Math.Abs(estimate - left);
        var upDistance = Math.Abs(estimate - up);
        var upperLeftDistance = Math.Abs(estimate - upperLeft);
        return leftDistance <= upDistance && leftDistance <= upperLeftDistance ? left : upDistance <= upperLeftDistance ? up : upperLeft;
    }
}
