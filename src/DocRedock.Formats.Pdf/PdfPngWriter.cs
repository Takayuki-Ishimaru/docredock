using System.Buffers.Binary;
using System.IO.Compression;

namespace DocRedock.Formats.Pdf;

/// <summary>A small PNG encoder for decoded PDF image samples: 8-bit grey (colour type 0), grey with alpha (4),
/// truecolour (2), truecolour with alpha (6) and palette (3). Rows are filtered adaptively (the filter with the
/// smallest sum of absolute residues is kept) and deflated with the BCL's zlib stream, so no package is needed.</summary>
internal static class PdfPngWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>The PNG file for <paramref name="data"/>: <paramref name="width"/> x <paramref name="height"/> pixels of
    /// <paramref name="channels"/> interleaved 8-bit samples per pixel, as <paramref name="colorType"/> reads them.
    /// <paramref name="palette"/> is the RGB triples of colour type 3.</summary>
    public static byte[] Encode(int width, int height, int channels, byte colorType, byte[] data, byte[]? palette = null)
    {
        var rowBytes = checked(width * channels);
        if (data.Length < checked((long)rowBytes * height)) throw new ArgumentException("Sample data is shorter than the image.", nameof(data));
        using var output = new MemoryStream();
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = 8;
        header[9] = colorType;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WriteChunk(output, "IHDR"u8, header);
        if (colorType == 3)
        {
            if (palette is not { Length: > 0 } || palette.Length % 3 != 0 || palette.Length > 768)
                throw new ArgumentException("A palette image needs 1 to 256 RGB entries.", nameof(palette));
            WriteChunk(output, "PLTE"u8, palette);
        }
        WriteChunk(output, "IDAT"u8, Deflate(width, height, channels, data));
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static byte[] Deflate(int width, int height, int channels, byte[] data)
    {
        var rowBytes = width * channels;
        using var compressed = new MemoryStream();
        // A large picture is deflated quickly rather than well: the file is a working copy of what the PDF holds.
        var level = (long)width * height > 4_000_000 ? CompressionLevel.Fastest : CompressionLevel.Optimal;
        using (var zlib = new ZLibStream(compressed, level, leaveOpen: true))
        {
            var candidates = new byte[5][];
            for (var filter = 0; filter < 5; filter++) candidates[filter] = new byte[rowBytes + 1];
            var zero = new byte[rowBytes];
            for (var row = 0; row < height; row++)
            {
                var current = data.AsSpan(row * rowBytes, rowBytes);
                ReadOnlySpan<byte> previous = row == 0 ? zero : data.AsSpan((row - 1) * rowBytes, rowBytes);
                var best = 0;
                var bestScore = long.MaxValue;
                for (var filter = 0; filter < 5; filter++)
                {
                    var line = candidates[filter];
                    line[0] = (byte)filter;
                    long score = 0;
                    for (var index = 0; index < rowBytes; index++)
                    {
                        var left = index >= channels ? current[index - channels] : (byte)0;
                        var up = previous[index];
                        var upLeft = index >= channels ? previous[index - channels] : (byte)0;
                        var predicted = filter switch
                        {
                            0 => 0,
                            1 => left,
                            2 => up,
                            3 => (left + up) / 2,
                            _ => Paeth(left, up, upLeft),
                        };
                        var residue = unchecked((byte)(current[index] - predicted));
                        line[index + 1] = residue;
                        score += residue < 128 ? residue : 256 - residue;
                    }
                    if (score < bestScore) { bestScore = score; best = filter; }
                    if (score == 0) break;
                }
                zlib.Write(candidates[best], 0, rowBytes + 1);
            }
        }
        return compressed.ToArray();
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var distanceLeft = Math.Abs(estimate - left);
        var distanceUp = Math.Abs(estimate - up);
        var distanceUpLeft = Math.Abs(estimate - upLeft);
        return distanceLeft <= distanceUp && distanceLeft <= distanceUpLeft ? left : distanceUp <= distanceUpLeft ? up : upLeft;
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        output.Write(number);
        output.Write(type);
        output.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var value in type) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        foreach (var value in data) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xFFFFFFFFu);
        output.Write(number);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var bit = 0; bit < 8; bit++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
