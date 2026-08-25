using System.Security.Cryptography;
using System.Text;

namespace DocRedock.Core.Documents;

/// <summary>Creates content-independent IDs for original nodes and temporary IDs for new nodes.</summary>
public static class NodeIdGenerator
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string CreateForSource(string documentNamespace, DocumentFormatKind format, SourceAnchor source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentNamespace);
        ArgumentNullException.ThrowIfNull(source);

        var strongest = source.Locators.FirstOrDefault(locator => IsStable(locator.Kind));
        var locator = strongest is not null
            ? $"{strongest.Kind}:{strongest.Value}"
            : $"{source.PartUri}|{source.OriginalOrdinal}|{source.StructuralFingerprint}|" +
              string.Join("|", source.Locators.Select(item => $"{item.Kind}:{item.Value}"));
        return "n_" + ToBase32(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\u001f", documentNamespace, format.ToString(), source.PartUri, locator))))[..16];
    }

    public static string CreateNew() => "new_" + Guid.NewGuid().ToString("N").ToUpperInvariant();

    private static bool IsStable(string kind) => kind is "w14_para_id" or "cell_address" or "shape_id" or "object_ref" or "sheet_id";

    private static string ToBase32(ReadOnlySpan<byte> bytes)
    {
        var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }
}
