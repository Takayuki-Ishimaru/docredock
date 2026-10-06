using System.Buffers.Binary;

namespace DocRedock.Api;

/// <summary>Reads the pixel size from the header of a PNG, GIF, BMP or JPEG image without decoding
/// it. Any other format, or a header that is cut short or malformed, is reported as unknown.</summary>
internal static class ImageDimensions
{
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool TryRead(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(PngSignature) && bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            return Accept(BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4)), out width, out height);
        if (bytes.Length >= 10 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            return Accept(BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(8, 2)), out width, out height);
        if (bytes.Length >= 22 && bytes[0] == 'B' && bytes[1] == 'M')
            return TryReadBmp(bytes, out width, out height);
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
            return TryReadJpeg(bytes, out width, out height);
        return false;
    }

    private static bool TryReadBmp(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(14, 4));
        if (headerSize == 12)
            return Accept(BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(18, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(20, 2)), out width, out height);
        if (headerSize < 40 || bytes.Length < 26) return false;
        var signedWidth = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(18, 4));
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(22, 4));
        // A negative height marks a bitmap stored top-down.
        return signedWidth > 0 && signedHeight != int.MinValue &&
            Accept((uint)signedWidth, (uint)Math.Abs(signedHeight), out width, out height);
    }

    private static bool TryReadJpeg(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        var at = 2;
        while (at + 4 <= bytes.Length)
        {
            if (bytes[at] != 0xFF) return false;
            var marker = bytes[at + 1];
            if (marker == 0xFF) { at++; continue; }
            if (marker is 0x01 or >= 0xD0 and <= 0xD7) { at += 2; continue; }
            // The image ends, or its scan starts, before any frame header.
            if (marker is 0xD9 or 0xDA) return false;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(at + 2, 2));
            if (length < 2) return false;
            // Start-of-frame markers; C4, C8 and CC are a Huffman table, a reserved marker and arithmetic coding conditions.
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
                return at + 9 <= bytes.Length &&
                    Accept(BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(at + 7, 2)),
                        BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(at + 5, 2)), out width, out height);
            at += 2 + length;
        }
        return false;
    }

    private static bool Accept(uint candidateWidth, uint candidateHeight, out int width, out int height)
    {
        width = height = 0;
        if (candidateWidth is 0 or > int.MaxValue || candidateHeight is 0 or > int.MaxValue) return false;
        width = (int)candidateWidth;
        height = (int)candidateHeight;
        return true;
    }
}
