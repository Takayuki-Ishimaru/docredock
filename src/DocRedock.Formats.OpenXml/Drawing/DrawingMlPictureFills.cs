namespace DocRedock.Formats.OpenXml.Drawing;

/// <summary>
/// Reads the picture of an a:blipFill on a shape from its package. A picture object is a document node with an
/// exported asset; the picture of a fill is paint, so it travels inside the paint as a bounded data: URI.
/// One instance bounds the pictures of one document.
/// </summary>
public sealed class DrawingMlPictureFills(Func<string, byte[]?> readPart)
{
    /// <summary>Largest picture embedded as a fill; a larger one leaves the shape unfilled with a warning.</summary>
    public const int MaxImageBytes = 4 * 1024 * 1024;
    /// <summary>All fill pictures of one document together, counted per use.</summary>
    public const long MaxTotalBytes = 12L * 1024 * 1024;
    private readonly Dictionary<string, string?> references = new(StringComparer.Ordinal);
    private long total;

    /// <summary>The data: URI of a PNG, JPEG, GIF or WebP package part, or null with the reason it is not embedded.</summary>
    public string? Reference(string partUri, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(partUri);
        reason = null;
        if (!references.TryGetValue(partUri, out var reference))
        {
            var bytes = readPart(partUri);
            var type = bytes is null ? null : MediaType(bytes);
            reference = bytes is null || type is null || bytes.Length > MaxImageBytes ? null
                : "data:" + type + ";base64," + Convert.ToBase64String(bytes);
            references[partUri] = reference;
            if (reference is null)
                reason = bytes is null ? "its package part is missing" : type is null ? "its format is not PNG, JPEG, GIF or WebP" : "it exceeds 4 MiB";
        }
        if (reference is null) { reason ??= "it could not be embedded"; return null; }
        // Three quarters of the base64 length: the picture's bytes.
        var length = (reference.Length - reference.IndexOf(',') - 1) / 4L * 3;
        if (total + length > MaxTotalBytes) { reason = "the fill pictures of the document exceed 12 MiB"; return null; }
        total += length;
        return reference;
    }

    private static string? MediaType(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png"
        : bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF ? "image/jpeg"
        : bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)) ? "image/gif"
        : bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8) ? "image/webp"
        : null;
}
