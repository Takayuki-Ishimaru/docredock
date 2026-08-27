using System.Buffers.Binary;
using System.Text;

namespace DocRedock.Render.Fonts;

public static class SfntFaceExtractor
{
    public const int MaxFontFileBytes = 128 * 1024 * 1024;
    public const int MaxFaces = 64;
    public const int MaxTables = 128;
    public const int MaxStandaloneBytes = 32 * 1024 * 1024;

    private const uint TtcfTag = 0x74746366;
    private const uint ChecksumMagic = 0xB1B0AFBA;

    public static byte[] ReadFontFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("PDF font was not found.", path);
        if (info.Length <= 0 || info.Length > MaxFontFileBytes)
            throw new InvalidDataException($"ERROR PdfFontTooLarge: Font files must be between 1 byte and {MaxFontFileBytes} bytes.");
        return File.ReadAllBytes(path);
    }

    public static int GetFaceCount(byte[] data)
    {
        ValidateRange(data, 0, 12, "font header");
        if (ReadUInt32(data, 0) != TtcfTag) return 1;
        var count = CheckedInt(ReadUInt32(data, 8), "TTC face count");
        if (count is < 1 or > MaxFaces)
            throw new InvalidDataException($"TTC face count must be between 1 and {MaxFaces}.");
        ValidateRange(data, 12, checked(count * 4), "TTC face offsets");
        return count;
    }

    public static byte[] ExtractFace(byte[] data, int faceIndex)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > MaxFontFileBytes)
            throw new InvalidDataException($"ERROR PdfFontTooLarge: Font file exceeds {MaxFontFileBytes} bytes.");

        var faceCount = GetFaceCount(data);
        if (faceIndex < 0 || faceIndex >= faceCount)
            throw new InvalidDataException($"Font face index {faceIndex} is outside the collection range 0..{faceCount - 1}.");

        var collection = ReadUInt32(data, 0) == TtcfTag;
        var faceOffset = collection ? CheckedInt(ReadUInt32(data, 12 + faceIndex * 4), "TTC face offset") : 0;
        ValidateRange(data, faceOffset, 12, "SFNT offset table");
        var scalerType = ReadUInt32(data, faceOffset);
        var tableCount = ReadUInt16(data, faceOffset + 4);
        if (tableCount is < 1 or > MaxTables)
            throw new InvalidDataException($"SFNT table count must be between 1 and {MaxTables}.");
        ValidateRange(data, faceOffset + 12, checked(tableCount * 16), "SFNT table directory");

        var records = new List<TableRecord>(tableCount);
        var tags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < tableCount; index++)
        {
            var entry = checked(faceOffset + 12 + index * 16);
            var tag = Encoding.ASCII.GetString(data, entry, 4);
            if (!tags.Add(tag)) throw new InvalidDataException($"Duplicate SFNT table '{tag}'.");
            var offset = CheckedInt(ReadUInt32(data, entry + 8), $"table {tag} offset");
            var length = CheckedInt(ReadUInt32(data, entry + 12), $"table {tag} length");
            ValidateRange(data, offset, length, $"table {tag}");
            records.Add(new TableRecord(tag, offset, length));
        }

        var nonEmptyRanges = records.Where(record => record.Length > 0).OrderBy(record => record.Offset).ToArray();
        for (var index = 1; index < nonEmptyRanges.Length; index++)
        {
            var previousEnd = checked(nonEmptyRanges[index - 1].Offset + nonEmptyRanges[index - 1].Length);
            if (nonEmptyRanges[index].Offset < previousEnd)
                throw new InvalidDataException("SFNT tables overlap.");
        }

        records.Sort((left, right) => StringComparer.Ordinal.Compare(left.Tag, right.Tag));
        var outputLength = checked(12 + records.Count * 16);
        foreach (var record in records) outputLength = checked(outputLength + Align4(record.Length));
        if (outputLength > MaxStandaloneBytes)
            throw new InvalidDataException($"ERROR PdfFontTooLarge: Standalone font face exceeds {MaxStandaloneBytes} bytes.");

        var output = new byte[outputLength];
        WriteUInt32(output, 0, scalerType);
        WriteUInt16(output, 4, checked((ushort)records.Count));
        var maximumPower = HighestPowerOfTwo(records.Count);
        WriteUInt16(output, 6, checked((ushort)(maximumPower * 16)));
        WriteUInt16(output, 8, checked((ushort)Log2(maximumPower)));
        WriteUInt16(output, 10, checked((ushort)(records.Count * 16 - maximumPower * 16)));

        var tableOutputOffset = 12 + records.Count * 16;
        var headOutputOffset = -1;
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var directoryOffset = 12 + index * 16;
            Encoding.ASCII.GetBytes(record.Tag, output.AsSpan(directoryOffset, 4));
            data.AsSpan(record.Offset, record.Length).CopyTo(output.AsSpan(tableOutputOffset, record.Length));
            if (record.Tag == "head")
            {
                if (record.Length < 12) throw new InvalidDataException("The head table is truncated.");
                headOutputOffset = tableOutputOffset;
                output.AsSpan(tableOutputOffset + 8, 4).Clear();
            }
            WriteUInt32(output, directoryOffset + 8, checked((uint)tableOutputOffset));
            WriteUInt32(output, directoryOffset + 12, checked((uint)record.Length));
            var checksum = ComputeChecksum(output, tableOutputOffset, record.Length);
            WriteUInt32(output, directoryOffset + 4, checksum);
            tableOutputOffset = checked(tableOutputOffset + Align4(record.Length));
        }

        if (headOutputOffset < 0) throw new InvalidDataException("SFNT face does not contain a head table.");
        var adjustment = unchecked(ChecksumMagic - ComputeChecksum(output, 0, output.Length));
        WriteUInt32(output, headOutputOffset + 8, adjustment);
        return output;
    }

    public static uint ComputeChecksum(byte[] data, int offset, int length)
    {
        ValidateRange(data, offset, length, "checksum range");
        uint sum = 0;
        var paddedLength = Align4(length);
        for (var index = 0; index < paddedLength; index += 4)
        {
            uint value = 0;
            for (var octet = 0; octet < 4; octet++)
            {
                var relative = index + octet;
                value = (value << 8) | (relative < length ? data[offset + relative] : 0u);
            }
            sum = unchecked(sum + value);
        }
        return sum;
    }

    private static int Align4(int value) => checked((value + 3) & ~3);
    private static int HighestPowerOfTwo(int value)
    {
        var result = 1;
        while (result <= value / 2) result *= 2;
        return result;
    }

    private static int Log2(int value)
    {
        var result = 0;
        while (value > 1) { value >>= 1; result++; }
        return result;
    }

    private static int CheckedInt(uint value, string field)
    {
        if (value > int.MaxValue) throw new InvalidDataException($"{field} is outside the supported range.");
        return (int)value;
    }

    private static void ValidateRange(byte[] data, int offset, int length, string field)
    {
        if (offset < 0 || length < 0 || offset > data.Length || length > data.Length - offset)
            throw new InvalidDataException($"{field} is outside the font file.");
    }

    private static ushort ReadUInt16(byte[] data, int offset)
    {
        ValidateRange(data, offset, 2, "UInt16");
        return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        ValidateRange(data, offset, 4, "UInt32");
        return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    }

    private static void WriteUInt16(byte[] data, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), value);

    private static void WriteUInt32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);

    private sealed record TableRecord(string Tag, int Offset, int Length);
}
