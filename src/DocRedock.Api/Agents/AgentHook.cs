using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DocRedock.Api.Agents;

/// <summary>The decision of the PreToolUse hook for one tool call.</summary>
/// <param name="Deny">True when the call opens an Office file directly and is refused.</param>
/// <param name="File">The Office file the call names, when known.</param>
/// <param name="Reason">What the assistant is told when the call is denied.</param>
public sealed record AgentHookDecision(bool Deny, string? File, string? Reason);

/// <summary>The PreToolUse hook (<c>docredock hook claude|copilot|codex</c>): refuses to let an assistant open an Office file
/// directly (the file reading tool, or a shell command that unzips, parses or converts it with another tool) and points it to
/// DocRedock. PDF files are never refused. Anything not understood is allowed, and the program never fails.</summary>
public static class AgentHook
{
    private static readonly string[] OfficeExtensions = [".docx", ".docm", ".xlsx", ".xlsm", ".pptx", ".pptm"];

    private static readonly HashSet<string> ShellTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "powershell", "pwsh", "shell", "shell_command", "local_shell", "exec_command",
    };

    // Commands whose business with an Office file is not reading its content.
    private static readonly HashSet<string> HarmlessCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "git", "cp", "mv", "ls", "dir", "rm", "del", "erase", "copy", "move", "ren", "rename", "mkdir", "md", "touch", "stat", "gci", "get-childitem",
        "copy-item", "move-item", "remove-item", "rename-item", "new-item", "test-path", "get-item", "resolve-path", "ln", "chmod", "icacls", "rmdir",
    };

    private static readonly Regex ReadingTokens = new(
        @"(?<![\w.\-])(unzip|7z|7za|7zr|tar|expand-archive|system\.io\.compression|zipfile|python3?|py|node|cat|type|get-content|gc|strings|xxd|od|head|tail|less|more|pandoc|markitdown|docx2txt|libreoffice|soffice|openpyxl|python-docx|docx|pptx)(?![\w\-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex QuotedOffice = new(
        @"([""'])(?<path>[^""'\r\n]*\.(?:docx|docm|xlsx|xlsm|pptx|pptm))\1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex BareOffice = new(
        @"(?<![\w])(?<path>[^\s""'<>|;&()=,]*\.(?:docx|docm|xlsx|xlsm|pptx|pptm))(?!\w|\.\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex DocRedockWord = new(@"docredock", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Decides on a hook event (the JSON the assistant sends on standard input). Malformed or unknown input is allowed.</summary>
    public static AgentHookDecision Decide(AgentClient client, string? eventJson, string cli = "docredock")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(eventJson)) return new(false, null, null);
            if (JsonNode.Parse(eventJson, null, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) is not JsonObject root)
                return new(false, null, null);
            var name = (client == AgentClient.Copilot ? Text(root, "toolName") ?? Text(root, "tool_name") : Text(root, "tool_name") ?? Text(root, "toolName")) ?? string.Empty;
            if (name.Contains("docredock", StringComparison.OrdinalIgnoreCase)) return new(false, null, null);
            var arguments = client == AgentClient.Copilot
                ? Argument(root, "toolArgs") ?? Argument(root, "tool_input")
                : Argument(root, "tool_input") ?? Argument(root, "toolArgs");

            string? file;
            if (ShellTools.Contains(name)) file = ShellOfficeFile(Command(arguments));
            else if (IsReadingTool(name)) file = ArgumentOfficeFile(arguments);
            else file = null;
            return file is null ? new(false, null, null) : new(true, file, Reason(file, cli));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or RegexMatchTimeoutException or FormatException)
        {
            return new(false, null, null);
        }
    }

    /// <summary>The text the hook program prints for the decision: the deny JSON of the client, or nothing (Claude, Codex) / {} (Copilot).</summary>
    public static string Output(AgentClient client, AgentHookDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        if (!decision.Deny) return client == AgentClient.Copilot ? "{}" : string.Empty;
        if (client == AgentClient.Copilot)
            return new JsonObject { ["permissionDecision"] = "deny", ["permissionDecisionReason"] = decision.Reason }.ToJsonString(options);
        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse", ["permissionDecision"] = "deny", ["permissionDecisionReason"] = decision.Reason,
            },
        }.ToJsonString(options);
    }

    /// <summary>Reads the event, decides and returns what to print. Never throws.</summary>
    public static string Run(AgentClient client, string? eventJson, string cli = "docredock")
    {
        try { return Output(client, Decide(client, eventJson, cli)); }
        catch (Exception) { return client == AgentClient.Copilot ? "{}" : string.Empty; }
    }

    /// <summary>The permissionRequest hook of Copilot CLI (<c>docredock hook copilot-approve</c>): the text to print for the event.
    /// <c>{"behavior":"allow"}</c> only for one of DocRedock's MCP tools (the tool name contains "docredock", whatever format the
    /// name has) that is not asking to leave the sandbox; <c>{}</c> otherwise, which lets Copilot ask as usual. Never throws.</summary>
    public static string ApproveCopilot(string? eventJson)
    {
        try { return ApprovesCopilot(eventJson) ? "{\"behavior\":\"allow\"}" : "{}"; }
        catch (Exception) { return "{}"; }
    }

    private static bool ApprovesCopilot(string? eventJson)
    {
        if (string.IsNullOrWhiteSpace(eventJson)) return false;
        if (JsonNode.Parse(eventJson, null, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) is not JsonObject root)
            return false;
        var name = Text(root, "toolName") ?? Text(root, "tool_name") ?? string.Empty;
        if (!name.Contains("docredock", StringComparison.OrdinalIgnoreCase)) return false;
        if (RequestsSandboxBypass(root)) return false;
        foreach (var key in new[] { "toolInput", "toolArgs", "tool_input" })
            if (RequestsSandboxBypass(Argument(root, key))) return false;
        return true;
    }

    private static bool RequestsSandboxBypass(JsonNode? node) =>
        node is JsonObject obj && obj["requestSandboxBypass"] is JsonValue value
        && (value.TryGetValue<bool>(out var flag) ? flag : value.TryGetValue<string>(out var text) && text.Equals("true", StringComparison.OrdinalIgnoreCase));

    internal static string Reason(string? file, string cli) =>
        "DocRedock: read Office documents through DocRedock instead of opening them directly: call the docredock MCP tool export_markdown " +
        "(then read_source for one piece of the original if the result needs it), or run \"" + cli + "\" export \"" + (file ?? "<file>") +
        "\" -o \"" + (file ?? "<file>") + ".md\" and read that file. See the docredock-office skill.";

    // A tool that reads a file: a word "read" or "view" in its name (Read, view, read_file, mcp__fs__read_text_file,
    // readFile), not those letters inside another word ("spreadsheet", "preview", "thread").
    private static readonly Regex ReadingToolName = new(@"(?:^|[^a-z])(?:read|view)(?:$|[^a-z])|(?:^|[a-z])(?:Read|View)(?:$|[^a-z])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static bool IsReadingTool(string name) => ReadingToolName.IsMatch(name) || ReadingToolName.IsMatch(name.ToLowerInvariant());

    private static string? Text(JsonObject root, string key) => root[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // Copilot sends toolArgs as an object or as a string holding JSON.
    private static JsonNode? Argument(JsonObject root, string key)
    {
        if (!root.TryGetPropertyValue(key, out var node) || node is null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            try { return JsonNode.Parse(text) ?? node; } catch (JsonException) { return node; }
        }
        return node;
    }

    private static string Command(JsonNode? arguments)
    {
        switch (arguments)
        {
            case JsonValue value when value.TryGetValue<string>(out var text): return text;
            case JsonObject obj:
                foreach (var key in new[] { "command", "cmd", "script" })
                {
                    if (obj[key] is JsonValue text && text.TryGetValue<string>(out var command)) return command;
                    if (obj[key] is JsonArray parts) return string.Join(' ', parts.Select(part => part?.ToString() ?? string.Empty));
                }
                return string.Join('\n', Strings(obj));
            case JsonArray array: return string.Join(' ', array.Select(part => part?.ToString() ?? string.Empty));
            default: return string.Empty;
        }
    }

    private static IEnumerable<string> Strings(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text): yield return text; break;
            case JsonObject obj:
                foreach (var property in obj) foreach (var item in Strings(property.Value)) yield return item;
                break;
            case JsonArray array:
                foreach (var element in array) foreach (var item in Strings(element)) yield return item;
                break;
        }
    }

    private static string? ArgumentOfficeFile(JsonNode? arguments)
    {
        foreach (var text in Strings(arguments))
        {
            var trimmed = text.Trim().Trim('"', '\'');
            if (OfficeExtensions.Any(extension => trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                return LastSegment(trimmed);
        }
        return null;
    }

    private static string LastSegment(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    /// <summary>The Office file named by a shell command that also reads or extracts it without going through DocRedock.</summary>
    private static string? ShellOfficeFile(string command)
    {
        foreach (var segment in Segments(command))
        {
            var words = segment.Trim().Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;
            var first = LastSegment(words[0].Trim('"', '\'', '&', ' ')).ToLowerInvariant();
            if (first.EndsWith(".exe", StringComparison.Ordinal)) first = first[..^4];
            if (HarmlessCommands.Contains(first)) continue;
            if (DocRedockWord.IsMatch(segment)) continue;
            var file = QuotedOffice.Match(segment) is { Success: true } quoted ? quoted.Groups["path"].Value
                : BareOffice.Match(segment) is { Success: true } bare ? bare.Groups["path"].Value : null;
            if (file is null) continue;
            if (ReadingTokens.IsMatch(segment)) return LastSegment(file);
        }
        return null;
    }

    /// <summary>Splits a command at ; | && || and line breaks outside quotes (a here-document keeps its lines together).</summary>
    private static IEnumerable<string> Segments(string command)
    {
        var heredoc = command.Contains("<<", StringComparison.Ordinal);
        var current = new System.Text.StringBuilder();
        char quote = '\0';
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote != '\0')
            {
                current.Append(c);
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') { quote = c; current.Append(c); continue; }
            if (c is ';' or '|' || c == '&' && i + 1 < command.Length && command[i + 1] == '&' || c == '\n' && !heredoc)
            {
                if (current.Length > 0) yield return current.ToString();
                current.Clear();
                if (c is '|' or '&' && i + 1 < command.Length && command[i + 1] == c) i++;
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}
