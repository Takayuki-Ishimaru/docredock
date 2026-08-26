namespace DocRedock.Core.Documents;

/// <summary>Describes the image media types that Markdown renderers can display directly.</summary>
public static class ImageDisplayPolicy
{
    public static bool IsMarkdownDisplayable(string? mediaType) => mediaType?.Trim().ToLowerInvariant() switch
    {
        "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp" or "image/svg+xml" => true,
        "image/tiff" or "image/emf" or "image/wmf" or "application/octet-stream" => false,
        _ => false,
    };
}
