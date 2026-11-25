using System.Text.Json;

namespace DocRedock.Markdown;

internal static class ReadableStructuredText
{
    // Detect examples without reformatting their source or treating ordinary prose as code.
    public static bool IsJsonExample(string text)
    {
        if (text.Length > 131072 || !text.Contains('\n')) return false;
        var value = text.AsSpan().TrimStart();
        if (value.IsEmpty || value[0] is not ('{' or '[')) return false;
        try
        {
            using var parsed = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            return parsed.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException) { return false; }
    }
}
