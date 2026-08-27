using System.Buffers.Binary;
using System.Text;
using DocRedock.Render.Fonts;

namespace DocRedock.Tests.Render;

public sealed class PdfFontTests
{
    [Theory]
    [InlineData(0x0000, FontEmbeddingPermission.Installable)]
    [InlineData(0x0004, FontEmbeddingPermission.PreviewAndPrint)]
    [InlineData(0x0008, FontEmbeddingPermission.Editable)]
    [InlineData(0x0002, FontEmbeddingPermission.Restricted)]
    [InlineData(0x0200, FontEmbeddingPermission.BitmapOnly)]
    public void Inspector_reads_embedding_permission(int fsType, FontEmbeddingPermission expected)
    {
        var font = BuildFont((ushort)fsType, "Fake JP", "FakeJP-Regular", 0x0041);

        var info = OpenTypeFontInspector.Inspect(font, new HashSet<uint> { 0x0041 });

        Assert.Equal(expected, info.EmbeddingPermission);
        Assert.Empty(info.MissingCodePoints);
        Assert.Equal("FakeJP-Regular", info.PostScriptName);
    }

    [Fact]
    public void Inspector_rejects_missing_glyphs_without_notdef_substitution()
    {
        var font = BuildFont(0, "Fake JP", "FakeJP-Regular", 0x0041);

        var info = OpenTypeFontInspector.Inspect(font, new HashSet<uint> { 0x0041, 0x65E5 });

        Assert.Equal([0x65E5u], info.MissingCodePoints);
    }

    [Fact]
    public void Inspector_treats_missing_os2_as_unknown()
    {
        var font = BuildFont(0, "Fake JP", "FakeJP-Regular", 0x0041, includeOs2: false);

        var info = OpenTypeFontInspector.Inspect(font);

        Assert.Equal(FontEmbeddingPermission.Unknown, info.EmbeddingPermission);
    }

    [Theory]
    [InlineData(0x0002)]
    [InlineData(0x0200)]
    public void Resolver_rejects_embedding_restricted_explicit_fonts(int fsType)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "restricted.ttf");
        File.WriteAllBytes(path, BuildFont((ushort)fsType, "Restricted", "Restricted-Regular", 0x0041));

        var exception = Assert.Throws<UnauthorizedAccessException>(() =>
            new PdfFontResolver().Resolve(new PdfFontRequest(new HashSet<uint> { 0x0041 }, path)));

        Assert.Contains("PdfFontEmbeddingRestricted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_rejects_missing_explicit_glyphs()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "missing.ttf");
        File.WriteAllBytes(path, BuildFont(0, "Missing", "Missing-Regular", 0x0041));

        var exception = Assert.Throws<NotSupportedException>(() =>
            new PdfFontResolver().Resolve(new PdfFontRequest(new HashSet<uint> { 0x65E5 }, path)));

        Assert.Contains("PdfFontMissingGlyphs", exception.Message, StringComparison.Ordinal);
        Assert.Contains("U+65E5", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_selects_requested_ttc_face_and_extracts_only_that_face()
    {
        using var fixture = new Fixture();
        var first = BuildFont(0, "First", "First-Regular", 0x0041);
        var second = BuildFont(0, "Second", "Second-Regular", 0x65E5);
        var path = Path.Combine(fixture.Root, "collection.ttc");
        File.WriteAllBytes(path, BuildTtc(first, second));

        var resolved = new PdfFontResolver().Resolve(
            new PdfFontRequest(new HashSet<uint> { 0x65E5 }, path, ExplicitFaceIndex: 1));

        Assert.Equal(1, resolved.FaceIndex);
        Assert.Equal("Second", resolved.FamilyName);
        Assert.Equal("Second-Regular", resolved.PostScriptName);
        Assert.True(resolved.StandaloneSfntBytes.Length < new FileInfo(path).Length);
        Assert.NotEqual(0x74746366u, ReadUInt32(resolved.StandaloneSfntBytes, 0));
    }

    [Fact]
    public void Extractor_rejects_invalid_ttc_face_offset()
    {
        var ttc = BuildTtc(
            BuildFont(0, "First", "First-Regular", 0x0041),
            BuildFont(0, "Second", "Second-Regular", 0x0042));
        WriteUInt32(ttc, 12, uint.MaxValue);

        Assert.Throws<InvalidDataException>(() => SfntFaceExtractor.ExtractFace(ttc, 0));
    }

    [Fact]
    public void Extracted_standalone_sfnt_has_valid_checksum()
    {
        var ttc = BuildTtc(
            BuildFont(0, "First", "First-Regular", 0x0041),
            BuildFont(0, "Second", "Second-Regular", 0x0042));

        var extracted = SfntFaceExtractor.ExtractFace(ttc, 1);

        Assert.Equal(0xB1B0AFBAu, SfntFaceExtractor.ComputeChecksum(extracted, 0, extracted.Length));
    }

    private static byte[] BuildFont(
        ushort fsType,
        string family,
        string postScript,
        ushort mappedCodePoint,
        bool includeOs2 = true)
    {
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["cmap"] = BuildCmap(mappedCodePoint),
            ["glyf"] = new byte[4],
            ["head"] = BuildHead(),
            ["hhea"] = BuildHhea(),
            ["hmtx"] = BuildHmtx(),
            ["loca"] = new byte[6],
            ["maxp"] = BuildMaxp(),
            ["name"] = BuildName(family, "Regular", postScript)
        };
        if (includeOs2) tables["OS/2"] = BuildOs2(fsType);
        return BuildSfnt(tables);
    }

    private static byte[] BuildSfnt(IReadOnlyDictionary<string, byte[]> source)
    {
        var tables = source.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        var directoryLength = 12 + tables.Length * 16;
        var totalLength = directoryLength + tables.Sum(pair => Align4(pair.Value.Length));
        var result = new byte[totalLength];
        WriteUInt32(result, 0, 0x00010000);
        WriteUInt16(result, 4, (ushort)tables.Length);
        var tableOffset = directoryLength;
        for (var index = 0; index < tables.Length; index++)
        {
            var directory = 12 + index * 16;
            Encoding.ASCII.GetBytes(tables[index].Key, result.AsSpan(directory, 4));
            tables[index].Value.CopyTo(result, tableOffset);
            WriteUInt32(result, directory + 8, (uint)tableOffset);
            WriteUInt32(result, directory + 12, (uint)tables[index].Value.Length);
            tableOffset += Align4(tables[index].Value.Length);
        }
        return result;
    }

    private static byte[] BuildTtc(params byte[][] faces)
    {
        var headerLength = Align4(12 + faces.Length * 4);
        var offsets = new int[faces.Length];
        var total = headerLength;
        for (var index = 0; index < faces.Length; index++)
        {
            offsets[index] = total;
            total += Align4(faces[index].Length);
        }

        var result = new byte[total];
        WriteUInt32(result, 0, 0x74746366);
        WriteUInt32(result, 4, 0x00010000);
        WriteUInt32(result, 8, (uint)faces.Length);
        for (var index = 0; index < faces.Length; index++)
        {
            WriteUInt32(result, 12 + index * 4, (uint)offsets[index]);
            faces[index].CopyTo(result, offsets[index]);
            var tableCount = ReadUInt16(result, offsets[index] + 4);
            for (var table = 0; table < tableCount; table++)
            {
                var directory = offsets[index] + 12 + table * 16;
                var relative = ReadUInt32(result, directory + 8);
                WriteUInt32(result, directory + 8, checked((uint)offsets[index] + relative));
            }
        }
        return result;
    }

    private static byte[] BuildHead()
    {
        var value = new byte[54];
        WriteUInt32(value, 0, 0x00010000);
        WriteUInt16(value, 18, 1000);
        WriteUInt16(value, 36, unchecked((ushort)-10));
        WriteUInt16(value, 38, unchecked((ushort)-200));
        WriteUInt16(value, 40, 1000);
        WriteUInt16(value, 42, 900);
        return value;
    }

    private static byte[] BuildMaxp()
    {
        var value = new byte[6];
        WriteUInt32(value, 0, 0x00010000);
        WriteUInt16(value, 4, 2);
        return value;
    }

    private static byte[] BuildHhea()
    {
        var value = new byte[36];
        WriteUInt32(value, 0, 0x00010000);
        WriteUInt16(value, 4, 800);
        WriteUInt16(value, 6, unchecked((ushort)-200));
        WriteUInt16(value, 34, 2);
        return value;
    }

    private static byte[] BuildHmtx()
    {
        var value = new byte[8];
        WriteUInt16(value, 0, 600);
        WriteUInt16(value, 4, 600);
        return value;
    }

    private static byte[] BuildOs2(ushort fsType)
    {
        var value = new byte[10];
        WriteUInt16(value, 0, 4);
        WriteUInt16(value, 8, fsType);
        return value;
    }

    private static byte[] BuildName(string family, string subfamily, string postScript)
    {
        var records = new[] { (Id: (ushort)1, Value: family), (Id: (ushort)2, Value: subfamily), (Id: (ushort)6, Value: postScript) };
        var encoded = records.Select(record => Encoding.BigEndianUnicode.GetBytes(record.Value)).ToArray();
        var storageOffset = 6 + records.Length * 12;
        var total = storageOffset + encoded.Sum(value => value.Length);
        var result = new byte[total];
        WriteUInt16(result, 2, (ushort)records.Length);
        WriteUInt16(result, 4, (ushort)storageOffset);
        var stringOffset = 0;
        for (var index = 0; index < records.Length; index++)
        {
            var at = 6 + index * 12;
            WriteUInt16(result, at, 3);
            WriteUInt16(result, at + 2, 1);
            WriteUInt16(result, at + 4, 0x0409);
            WriteUInt16(result, at + 6, records[index].Id);
            WriteUInt16(result, at + 8, (ushort)encoded[index].Length);
            WriteUInt16(result, at + 10, (ushort)stringOffset);
            encoded[index].CopyTo(result, storageOffset + stringOffset);
            stringOffset += encoded[index].Length;
        }
        return result;
    }

    private static byte[] BuildCmap(ushort codePoint)
    {
        var result = new byte[12 + 32];
        WriteUInt16(result, 2, 1);
        WriteUInt16(result, 4, 3);
        WriteUInt16(result, 6, 1);
        WriteUInt32(result, 8, 12);
        var at = 12;
        WriteUInt16(result, at, 4);
        WriteUInt16(result, at + 2, 32);
        WriteUInt16(result, at + 6, 4);
        WriteUInt16(result, at + 8, 4);
        WriteUInt16(result, at + 10, 1);
        WriteUInt16(result, at + 14, codePoint);
        WriteUInt16(result, at + 16, 0xFFFF);
        WriteUInt16(result, at + 20, codePoint);
        WriteUInt16(result, at + 22, 0xFFFF);
        WriteUInt16(result, at + 24, unchecked((ushort)(1 - codePoint)));
        WriteUInt16(result, at + 26, 1);
        return result;
    }

    private static int Align4(int value) => (value + 3) & ~3;
    private static ushort ReadUInt16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    private static uint ReadUInt32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    private static void WriteUInt16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), value);
    private static void WriteUInt32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "docredock-font-tests", Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
