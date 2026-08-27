using System.Buffers.Binary;
using System.Text;

namespace DocRedock.Render.Fonts;

public static class OpenTypeFontInspector
{
    private const int MaxNameRecords = 4096;
    private static readonly string[] RequiredTrueTypeTables = ["head", "maxp", "hhea", "hmtx", "cmap", "glyf", "loca"];

    public static OpenTypeFontInfo Inspect(byte[] standaloneSfnt, IReadOnlySet<uint>? requiredCodePoints = null)
    {
        ArgumentNullException.ThrowIfNull(standaloneSfnt);
        var tables = ReadTables(standaloneSfnt);
        var hasCff = tables.ContainsKey("CFF ") || tables.ContainsKey("CFF2");
        var hasTrueType = !hasCff && RequiredTrueTypeTables.All(tables.ContainsKey);
        var (family, subfamily, postScriptName) = ReadNames(standaloneSfnt, tables);
        var permission = ReadEmbeddingPermission(standaloneSfnt, tables);
        var missing = new List<uint>();
        if (requiredCodePoints is not null)
        {
            if (!tables.TryGetValue("cmap", out var cmap))
                missing.AddRange(requiredCodePoints.Order());
            else
            {
                var lookup = BuildCmapLookup(standaloneSfnt, cmap);
                foreach (var codePoint in requiredCodePoints.Order())
                    if (lookup(codePoint) == 0) missing.Add(codePoint);
            }
        }

        return new OpenTypeFontInfo(
            string.IsNullOrWhiteSpace(family) ? "Unknown" : family,
            string.IsNullOrWhiteSpace(subfamily) ? "Regular" : subfamily,
            string.IsNullOrWhiteSpace(postScriptName) ? string.Empty : postScriptName,
            permission,
            missing,
            hasTrueType);
    }

    public static ushort GetGlyphId(byte[] standaloneSfnt, uint codePoint)
    {
        var tables = ReadTables(standaloneSfnt);
        if (!tables.TryGetValue("cmap", out var cmap))
            throw new InvalidDataException("PDF font does not contain a Unicode cmap table.");
        return BuildCmapLookup(standaloneSfnt, cmap)(codePoint);
    }

    private static Dictionary<string, Table> ReadTables(byte[] data)
    {
        ValidateRange(data, 0, 12, "SFNT header");
        var count = ReadUInt16(data, 4);
        if (count is < 1 or > SfntFaceExtractor.MaxTables)
            throw new InvalidDataException($"SFNT table count must be between 1 and {SfntFaceExtractor.MaxTables}.");
        ValidateRange(data, 12, checked(count * 16), "SFNT table directory");
        var tables = new Dictionary<string, Table>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var at = 12 + index * 16;
            var tag = Encoding.ASCII.GetString(data, at, 4);
            var offset = CheckedInt(ReadUInt32(data, at + 8), $"{tag} offset");
            var length = CheckedInt(ReadUInt32(data, at + 12), $"{tag} length");
            ValidateRange(data, offset, length, tag);
            if (!tables.TryAdd(tag, new Table(offset, length)))
                throw new InvalidDataException($"Duplicate SFNT table '{tag}'.");
        }
        return tables;
    }

    private static FontEmbeddingPermission ReadEmbeddingPermission(byte[] data, IReadOnlyDictionary<string, Table> tables)
    {
        if (!tables.TryGetValue("OS/2", out var os2) || os2.Length < 10)
            return FontEmbeddingPermission.Unknown;
        var fsType = ReadUInt16(data, os2.Offset + 8);
        if ((fsType & 0x0002) != 0) return FontEmbeddingPermission.Restricted;
        if ((fsType & 0x0200) != 0) return FontEmbeddingPermission.BitmapOnly;
        if ((fsType & 0x0008) != 0) return FontEmbeddingPermission.Editable;
        if ((fsType & 0x0004) != 0) return FontEmbeddingPermission.PreviewAndPrint;
        return (fsType & 0x000F) == 0 ? FontEmbeddingPermission.Installable : FontEmbeddingPermission.Unknown;
    }

    private static (string Family, string Subfamily, string PostScript) ReadNames(
        byte[] data,
        IReadOnlyDictionary<string, Table> tables)
    {
        if (!tables.TryGetValue("name", out var name) || name.Length < 6) return (string.Empty, string.Empty, string.Empty);
        var count = ReadUInt16(data, name.Offset + 2);
        if (count > MaxNameRecords) throw new InvalidDataException($"OpenType name record count exceeds {MaxNameRecords}.");
        var storageOffset = ReadUInt16(data, name.Offset + 4);
        ValidateRange(data, name.Offset + 6, checked(count * 12), "name records");

        var candidates = new List<NameCandidate>();
        for (var index = 0; index < count; index++)
        {
            var at = name.Offset + 6 + index * 12;
            var platform = ReadUInt16(data, at);
            var encoding = ReadUInt16(data, at + 2);
            var language = ReadUInt16(data, at + 4);
            var nameId = ReadUInt16(data, at + 6);
            if (nameId is not (1 or 2 or 6)) continue;
            var length = ReadUInt16(data, at + 8);
            var relative = ReadUInt16(data, at + 10);
            var valueOffset = checked(name.Offset + storageOffset + relative);
            ValidateRange(data, valueOffset, length, "name string");
            var value = DecodeName(data.AsSpan(valueOffset, length), platform, encoding).Trim().TrimEnd('\0');
            if (value.Length == 0) continue;
            var score = platform is 0 or 3 ? 100 : 10;
            if (language is 0x0409 or 0x0411 or 0) score += 10;
            candidates.Add(new NameCandidate(nameId, value, score));
        }

        string Pick(int id) => candidates.Where(candidate => candidate.NameId == id)
            .OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Value, StringComparer.Ordinal)
            .Select(candidate => candidate.Value).FirstOrDefault() ?? string.Empty;
        return (Pick(1), Pick(2), Pick(6));
    }

    private static string DecodeName(ReadOnlySpan<byte> bytes, ushort platform, ushort encoding)
    {
        if (platform is 0 or 3)
        {
            if ((bytes.Length & 1) != 0) return string.Empty;
            var chars = new char[bytes.Length / 2];
            for (var index = 0; index < chars.Length; index++)
                chars[index] = (char)BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(index * 2, 2));
            return new string(chars);
        }
        return Encoding.Latin1.GetString(bytes);
    }

    private static Func<uint, ushort> BuildCmapLookup(byte[] data, Table cmap)
    {
        ValidateRange(data, cmap.Offset, Math.Min(cmap.Length, 4), "cmap header");
        var recordCount = ReadUInt16(data, cmap.Offset + 2);
        ValidateRange(data, cmap.Offset + 4, checked(recordCount * 8), "cmap records");
        Table? format12 = null;
        Table? format4 = null;
        for (var index = 0; index < recordCount; index++)
        {
            var at = cmap.Offset + 4 + index * 8;
            var platform = ReadUInt16(data, at);
            var encoding = ReadUInt16(data, at + 2);
            var relative = CheckedInt(ReadUInt32(data, at + 4), "cmap subtable offset");
            var subtableOffset = checked(cmap.Offset + relative);
            ValidateRange(data, subtableOffset, 2, "cmap subtable");
            var format = ReadUInt16(data, subtableOffset);
            if (format == 12 && (platform == 0 || platform == 3 && encoding == 10))
            {
                ValidateRange(data, subtableOffset, 16, "cmap format 12");
                var length = CheckedInt(ReadUInt32(data, subtableOffset + 4), "cmap format 12 length");
                ValidateRange(data, subtableOffset, length, "cmap format 12");
                format12 ??= new Table(subtableOffset, length);
            }
            else if (format == 4 && (platform == 0 || platform == 3 && encoding is 0 or 1))
            {
                ValidateRange(data, subtableOffset, 8, "cmap format 4");
                var length = ReadUInt16(data, subtableOffset + 2);
                ValidateRange(data, subtableOffset, length, "cmap format 4");
                format4 ??= new Table(subtableOffset, length);
            }
        }

        Func<uint, ushort>? lookup12 = format12 is null ? null : BuildFormat12Lookup(data, format12);
        Func<uint, ushort>? lookup4 = format4 is null ? null : BuildFormat4Lookup(data, format4);
        if (lookup12 is null && lookup4 is null)
            throw new InvalidDataException("PDF font does not contain a supported Unicode cmap table.");
        return codePoint =>
        {
            var glyph = lookup12?.Invoke(codePoint) ?? 0;
            return glyph != 0 ? glyph : lookup4?.Invoke(codePoint) ?? 0;
        };
    }

    private static Func<uint, ushort> BuildFormat12Lookup(byte[] data, Table table)
    {
        var groupCount = CheckedInt(ReadUInt32(data, table.Offset + 12), "cmap format 12 group count");
        ValidateRange(data, table.Offset + 16, checked(groupCount * 12), "cmap format 12 groups");
        return codePoint =>
        {
            var low = 0;
            var high = groupCount - 1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var at = table.Offset + 16 + middle * 12;
                var start = ReadUInt32(data, at);
                var end = ReadUInt32(data, at + 4);
                if (codePoint < start) high = middle - 1;
                else if (codePoint > end) low = middle + 1;
                else
                {
                    var glyph = (ulong)ReadUInt32(data, at + 8) + codePoint - start;
                    return glyph <= ushort.MaxValue ? (ushort)glyph : (ushort)0;
                }
            }
            return 0;
        };
    }

    private static Func<uint, ushort> BuildFormat4Lookup(byte[] data, Table table)
    {
        var segmentCount = ReadUInt16(data, table.Offset + 6) / 2;
        if (segmentCount == 0) throw new InvalidDataException("cmap format 4 has no segments.");
        var endAt = table.Offset + 14;
        var startAt = checked(endAt + segmentCount * 2 + 2);
        var deltaAt = checked(startAt + segmentCount * 2);
        var rangeAt = checked(deltaAt + segmentCount * 2);
        ValidateRange(data, rangeAt, segmentCount * 2, "cmap format 4 segments");
        var tableEnd = checked(table.Offset + table.Length);
        return codePoint =>
        {
            if (codePoint > ushort.MaxValue) return 0;
            var code = (ushort)codePoint;
            for (var index = 0; index < segmentCount; index++)
            {
                var end = ReadUInt16(data, endAt + index * 2);
                if (code > end) continue;
                var start = ReadUInt16(data, startAt + index * 2);
                if (code < start) return 0;
                var delta = unchecked((short)ReadUInt16(data, deltaAt + index * 2));
                var range = ReadUInt16(data, rangeAt + index * 2);
                if (range == 0) return (ushort)((code + delta) & 0xFFFF);
                var glyphAt = checked(rangeAt + index * 2 + range + (code - start) * 2);
                if (glyphAt < table.Offset || glyphAt > tableEnd - 2) return 0;
                var glyph = ReadUInt16(data, glyphAt);
                return glyph == 0 ? (ushort)0 : (ushort)((glyph + delta) & 0xFFFF);
            }
            return 0;
        };
    }

    private static int CheckedInt(uint value, string field)
    {
        if (value > int.MaxValue) throw new InvalidDataException($"{field} is outside the supported range.");
        return (int)value;
    }

    private static void ValidateRange(byte[] data, int offset, int length, string field)
    {
        if (offset < 0 || length < 0 || offset > data.Length || length > data.Length - offset)
            throw new InvalidDataException($"{field} is outside the font face.");
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

    private sealed record Table(int Offset, int Length);
    private sealed record NameCandidate(int NameId, string Value, int Score);
}
