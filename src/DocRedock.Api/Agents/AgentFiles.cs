using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocRedock.Api.Agents;

/// <summary>A text file as read: line breaks normalized to LF, with what is needed to write it back the way it was.</summary>
internal sealed record FileRead(bool Exists, string? Text, bool Crlf, bool Bom, string? Error);

/// <summary>Reading, backing up, writing and deleting the files the integration edits.</summary>
internal static class AgentFiles
{
    public const string BackupSuffix = ".docredock.bak";
    private static readonly UTF8Encoding Utf8 = new(false);

    public static FileRead Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(false, null, false, false, null);
            var bytes = File.ReadAllBytes(path);
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var text = new UTF8Encoding(false, true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            var crlf = text.Contains("\r\n", StringComparison.Ordinal);
            return new(true, crlf ? text.Replace("\r\n", "\n", StringComparison.Ordinal) : text, crlf, bom, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return new(true, null, false, false, ex.Message);
        }
    }

    private static readonly string[] ManagedMarkers =
    [
        "docredock:begin", "# >>> docredock", "mcp__docredock__", "\"docredock\"", "docredock-office", "docredock.instructions", " hook claude", " hook codex", " hook copilot",
    ];

    /// <summary>True when the text already carries something DocRedock wrote (a marker line, our server, skill or hook entry).</summary>
    public static bool HasManagedContent(string? text) =>
        text is not null && ManagedMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>Writes the text atomically (temporary file, then replace), after copying an existing file to its backup.</summary>
    public static void Write(string path, string text, FileRead? previous)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        // The backup holds the file as it was before DocRedock first touched it: a file that already carries DocRedock content
        // (an earlier item of the same install, or an earlier install) is never copied over it.
        if (previous is { Exists: true } && !HasManagedContent(previous.Text))
            File.Copy(path, path + BackupSuffix, overwrite: true);
        var body = previous is { Crlf: true } ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text;
        var bytes = Utf8.GetBytes(body);
        if (previous is { Bom: true }) bytes = [0xEF, 0xBB, 0xBF, .. bytes];
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Deletes the file, then the folders above it that are empty, up to but not including <paramref name="stopAt"/>.</summary>
    public static void Delete(string path, string stopAt)
    {
        File.Delete(path);
        var directory = Path.GetDirectoryName(path);
        var stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stopAt));
        while (directory is not null && !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), stop, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    public static readonly JsonSerializerOptions JsonWrite = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Parses a JSON object file; an absent or blank file is an empty object. Comments and trailing commas are errors,
    /// so a file that would lose them when written back is never touched.</summary>
    public static JsonObject ParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        var node = JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        return node as JsonObject ?? throw new JsonException("The top level of the file is not an object.");
    }

    /// <summary>JSON with two-space indentation and LF line breaks whatever the platform.</summary>
    public static string Pretty(JsonNode node) => node.ToJsonString(JsonWrite).Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Serializes with two-space indentation, ending with a line break when the file was absent or had one.</summary>
    public static string ToText(JsonNode node, string? original)
    {
        var text = Pretty(node);
        return original is null || original.EndsWith('\n') ? text + "\n" : text;
    }

    /// <summary>Gets the child object at <paramref name="key"/>; null when absent. Throws when it is not an object.</summary>
    public static JsonObject? ChildObject(JsonObject parent, string key)
    {
        if (!parent.TryGetPropertyValue(key, out var value) || value is null) return null;
        return value as JsonObject ?? throw new JsonException($"'{key}' is not an object.");
    }
}

/// <summary>Finds, replaces and removes a block of lines delimited by marker lines in a text file.</summary>
internal static class BlockEditor
{
    /// <summary>The line range of the first block, or null; throws when a block starts and never ends.</summary>
    public static (int Start, int End)? Find(IReadOnlyList<string> lines, Func<string, bool> isBegin, Func<string, bool> isEnd)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!isBegin(lines[i])) continue;
            for (var j = i + 1; j < lines.Count; j++) if (isEnd(lines[j])) return (i, j);
            throw new FormatException("A managed block starts but does not end.");
        }
        return null;
    }

    public static string[] Lines(string text) => text.Split('\n');

    public static string BlockText(IReadOnlyList<string> lines, (int Start, int End) range) =>
        string.Join('\n', lines.Skip(range.Start).Take(range.End - range.Start + 1));

    /// <summary>Replaces the block in place, or appends it after a blank line (or alone in an empty file).</summary>
    public static string Put(string? text, string block, Func<string, bool> isBegin, Func<string, bool> isEnd)
    {
        if (string.IsNullOrWhiteSpace(text)) return block + "\n";
        var lines = Lines(text);
        if (Find(lines, isBegin, isEnd) is { } range)
            return string.Join('\n', lines.Take(range.Start).Concat(block.Split('\n')).Concat(lines.Skip(range.End + 1)));
        return (text.EndsWith('\n') ? text : text + "\n") + "\n" + block + "\n";
    }

    /// <summary>Removes the block and the one blank line placed before it; null when nothing else is left.</summary>
    public static string? Take(string text, Func<string, bool> isBegin, Func<string, bool> isEnd)
    {
        var lines = Lines(text).ToList();
        if (Find(lines, isBegin, isEnd) is not { } range) return text;
        var start = range.Start;
        if (start > 0 && lines[start - 1].Length == 0) start--;
        lines.RemoveRange(start, range.End - start + 1);
        var result = string.Join('\n', lines);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
