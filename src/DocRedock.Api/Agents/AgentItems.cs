using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DocRedock.Api.Agents;

/// <summary>What one operation needs from the caller.</summary>
internal sealed record ItemContext(AgentEnvironment Env, string Cli, bool DryRun);

internal sealed record Inspection(AgentItemState State, string? Detail = null);

/// <summary>One thing written to one file. The subclasses say how to recognize, add and remove their part of the file; the
/// base class does the reading, the backup, the atomic write and the clean-up of what was created.</summary>
internal abstract class ManagedItem(AgentClient client, AgentItem item, string path, string stopAt)
{
    public AgentClient Client { get; } = client;
    public AgentItem Item { get; } = item;
    public string Path { get; } = path;

    /// <summary>The state of the file's text (LF line breaks; null when the file does not exist).</summary>
    protected abstract Inspection Inspect(string? text, ItemContext context);
    /// <summary>The text with our part added or updated.</summary>
    protected abstract string Apply(string? text, ItemContext context);
    /// <summary>The text without our part; null when nothing else is left, so the file goes.</summary>
    protected abstract string? Remove(string text, ItemContext context);

    protected AgentItemReport Report(AgentItemState state, AgentAction action, string? detail = null) => new(Client, Item, state, action, Path, detail);

    protected (FileRead Read, Inspection Inspection) Probe(ItemContext context)
    {
        var read = AgentFiles.Read(Path);
        if (read.Error is not null) return (read, new(AgentItemState.Error, "Cannot read the file: " + read.Error));
        try { return (read, Inspect(read.Text, context)); }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            return (read, new(AgentItemState.Error, "Cannot parse the file: " + ex.Message));
        }
    }

    public virtual AgentItemReport Status(ItemContext context)
    {
        var (_, inspection) = Probe(context);
        return Report(inspection.State, AgentAction.None, inspection.Detail);
    }

    public virtual AgentItemReport Install(ItemContext context)
    {
        var (read, inspection) = Probe(context);
        if (inspection.State is AgentItemState.Conflict or AgentItemState.Error or AgentItemState.Installed)
            return Report(inspection.State, AgentAction.None, inspection.Detail);
        try
        {
            var text = Apply(read.Text, context);
            if (!context.DryRun) AgentFiles.Write(Path, text, read);
            return Report(AgentItemState.Installed, read.Exists ? AgentAction.Update : AgentAction.Create);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Report(AgentItemState.Error, AgentAction.None, "Cannot write the file: " + ex.Message);
        }
    }

    public virtual AgentItemReport Uninstall(ItemContext context)
    {
        var (read, inspection) = Probe(context);
        if (inspection.State is AgentItemState.Conflict or AgentItemState.Error)
            return Report(inspection.State, AgentAction.None, inspection.Detail);
        if (inspection.State == AgentItemState.Missing) return Report(AgentItemState.Missing, AgentAction.None);
        try
        {
            var text = Remove(read.Text!, context);
            if (!context.DryRun)
            {
                var backup = AgentFiles.Read(Path + AgentFiles.BackupSuffix);
                // Nothing else is left: a file install created goes; one that existed before install (it has a backup) is
                // put back as it was, so uninstall never deletes a user's file.
                if (text is null && !backup.Exists) AgentFiles.Delete(Path, stopAt);
                else if (text is null && backup.Error is null) AgentFiles.Write(Path, backup.Text!, backup with { Exists = false });
                else if (text is null) throw new IOException("Cannot read the backup: " + backup.Error);
                else AgentFiles.Write(Path, text, read with { Exists = false });
            }
            return Report(AgentItemState.Missing, AgentAction.Remove);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Report(AgentItemState.Error, AgentAction.None, "Cannot write the file: " + ex.Message);
        }
    }
}

/// <summary>A file that is entirely ours (a skill, a Copilot instructions or hooks file).</summary>
internal sealed class OwnFileItem(AgentClient client, AgentItem item, string path, string stopAt, Func<ItemContext, string> expected)
    : ManagedItem(client, item, path, stopAt)
{
    protected override Inspection Inspect(string? text, ItemContext context) =>
        text is null ? new(AgentItemState.Missing) : new(text == expected(context) ? AgentItemState.Installed : AgentItemState.Outdated);
    protected override string Apply(string? text, ItemContext context) => expected(context);
    protected override string? Remove(string text, ItemContext context) => null;
}

/// <summary>A block between marker lines in a text file the user also writes in (CLAUDE.md, AGENTS.md, config.toml).</summary>
internal sealed class BlockItem(AgentClient client, AgentItem item, string path, string stopAt, Func<ItemContext, string> block,
    Func<string, bool> isBegin, Func<string, bool> isEnd, Func<string[], string?>? findConflict = null) : ManagedItem(client, item, path, stopAt)
{
    protected override Inspection Inspect(string? text, ItemContext context)
    {
        if (text is null) return new(AgentItemState.Missing);
        var lines = BlockEditor.Lines(text);
        var range = BlockEditor.Find(lines, isBegin, isEnd);
        var conflict = findConflict?.Invoke(range is { } r ? lines.Take(r.Start).Concat(lines.Skip(r.End + 1)).ToArray() : lines);
        if (conflict is not null) return new(AgentItemState.Conflict, conflict);
        if (range is null) return new(AgentItemState.Missing);
        return new(BlockEditor.BlockText(lines, range.Value) == block(context) ? AgentItemState.Installed : AgentItemState.Outdated);
    }

    protected override string Apply(string? text, ItemContext context) => BlockEditor.Put(text, block(context), isBegin, isEnd);
    protected override string? Remove(string text, ItemContext context) => BlockEditor.Take(text, isBegin, isEnd);
}

/// <summary>Our server entry in a JSON file's mcpServers object.</summary>
internal class JsonServerItem(AgentClient client, string path, string stopAt, Func<ItemContext, JsonObject> expected,
    Func<JsonObject, JsonObject, bool> isCurrent) : ManagedItem(client, AgentItem.Mcp, path, stopAt)
{
    public const string ServerName = "docredock";

    /// <summary>An entry is ours when it runs "mcp" on an executable named like DocRedock.</summary>
    public static bool IsOurs(JsonObject entry) =>
        entry["args"] is JsonArray { Count: 1 } args && args[0] is JsonValue arg && arg.TryGetValue<string>(out var first) && first == "mcp"
        && entry["command"] is JsonValue command && command.TryGetValue<string>(out var path)
        && path.Contains("docredock", StringComparison.OrdinalIgnoreCase);

    protected override Inspection Inspect(string? text, ItemContext context)
    {
        var root = AgentFiles.ParseObject(text);
        var servers = AgentFiles.ChildObject(root, "mcpServers");
        if (servers is null || !servers.TryGetPropertyValue(ServerName, out var node)) return new(AgentItemState.Missing);
        if (node is not JsonObject entry || !IsOurs(entry)) return new(AgentItemState.Conflict, "An mcpServers entry named docredock exists that DocRedock did not write; it was left alone.");
        return new(isCurrent(entry, expected(context)) ? AgentItemState.Installed : AgentItemState.Outdated);
    }

    protected override string Apply(string? text, ItemContext context)
    {
        var root = AgentFiles.ParseObject(text);
        var servers = AgentFiles.ChildObject(root, "mcpServers");
        if (servers is null) { servers = new JsonObject(); root["mcpServers"] = servers; }
        servers[ServerName] = expected(context);
        return AgentFiles.ToText(root, text);
    }

    protected override string? Remove(string text, ItemContext context)
    {
        var root = AgentFiles.ParseObject(text);
        var servers = AgentFiles.ChildObject(root, "mcpServers");
        servers?.Remove(ServerName);
        if (servers is { Count: 0 }) root.Remove("mcpServers");
        return root.Count == 0 ? null : AgentFiles.ToText(root, text);
    }
}

/// <summary>Claude Code's user-scope server: added with the claude command line when it is on PATH (a running Claude Code
/// rewrites ~/.claude.json and can drop a concurrent edit), otherwise written to the file.</summary>
internal sealed class ClaudeMcpItem(string path, string stopAt, Func<ItemContext, JsonObject> expected, Func<JsonObject, JsonObject, bool> isCurrent)
    : JsonServerItem(AgentClient.Claude, path, stopAt, expected, isCurrent)
{
    private static string? FindClaude(AgentEnvironment env)
    {
        foreach (var directory in env.PathDirectories)
        {
            try
            {
                if (env.IsWindows)
                {
                    foreach (var extension in env.PathExtensions.Prepend(string.Empty))
                    {
                        var candidate = System.IO.Path.Combine(directory, "claude" + extension);
                        if (File.Exists(candidate) && (extension.Length > 0 || System.IO.Path.HasExtension(candidate))) return candidate;
                    }
                }
                else
                {
                    var candidate = System.IO.Path.Combine(directory, "claude");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private ExternalCommandResult Run(ItemContext context, string claude, params string[] arguments)
    {
        var environment = new Dictionary<string, string?>
        {
            ["HOME"] = context.Env.HomeDirectory, ["USERPROFILE"] = context.Env.HomeDirectory, ["CLAUDE_CONFIG_DIR"] = context.Env.ClaudeConfigDir,
        };
        return context.Env.Runner.Run(claude, arguments, environment);
    }

    public override AgentItemReport Install(ItemContext context)
    {
        var claude = FindClaude(context.Env);
        if (claude is null) return base.Install(context);
        var (read, inspection) = Probe(context);
        if (inspection.State is AgentItemState.Conflict or AgentItemState.Error or AgentItemState.Installed)
            return Report(inspection.State, AgentAction.None, inspection.Detail);
        var action = inspection.State == AgentItemState.Outdated ? AgentAction.Update : AgentAction.Create;
        if (context.DryRun) return Report(AgentItemState.Installed, action, "via the claude command line");
        if (inspection.State == AgentItemState.Outdated)
        {
            var removed = Run(context, claude, "mcp", "remove", "--scope", "user", ServerName);
            if (removed.ExitCode != 0) return Report(AgentItemState.Error, AgentAction.None, "claude mcp remove failed: " + Trim(removed));
        }
        var added = Run(context, claude, "mcp", "add", "--scope", "user", ServerName, "--", context.Cli, "mcp");
        if (added.ExitCode != 0) return Report(AgentItemState.Error, AgentAction.None, "claude mcp add failed: " + Trim(added));
        _ = read;
        return Report(AgentItemState.Installed, action, "via the claude command line");
    }

    public override AgentItemReport Uninstall(ItemContext context)
    {
        var claude = FindClaude(context.Env);
        if (claude is null) return base.Uninstall(context);
        var (_, inspection) = Probe(context);
        if (inspection.State is AgentItemState.Conflict or AgentItemState.Error)
            return Report(inspection.State, AgentAction.None, inspection.Detail);
        if (inspection.State == AgentItemState.Missing) return Report(AgentItemState.Missing, AgentAction.None);
        if (context.DryRun) return Report(AgentItemState.Missing, AgentAction.Remove, "via the claude command line");
        var removed = Run(context, claude, "mcp", "remove", "--scope", "user", ServerName);
        return removed.ExitCode == 0
            ? Report(AgentItemState.Missing, AgentAction.Remove, "via the claude command line")
            : Report(AgentItemState.Error, AgentAction.None, "claude mcp remove failed: " + Trim(removed));
    }

    private static string Trim(ExternalCommandResult result) =>
        (string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError).Trim();
}

/// <summary>Our handler in the PreToolUse list of a settings or hooks file (Claude's settings.json, Codex's hooks.json).</summary>
internal sealed class HookEntryItem(AgentClient client, string path, string stopAt, string matcher, Func<ItemContext, JsonObject> handler, string hookClient)
    : ManagedItem(client, AgentItem.Hook, path, stopAt)
{
    private bool IsOurs(JsonNode? node) =>
        node is JsonObject entry
        && (entry["type"] is not JsonValue type || !type.TryGetValue<string>(out var kind) || kind == "command")
        && entry["command"] is JsonValue value && value.TryGetValue<string>(out var command)
        && command.Contains("docredock", StringComparison.OrdinalIgnoreCase)
        && Regex.IsMatch(command, @"\bhook\s+" + hookClient + @"[""'\s]*$", RegexOptions.CultureInvariant);

    private static JsonArray? PreToolUse(JsonObject root)
    {
        var hooks = AgentFiles.ChildObject(root, "hooks");
        if (hooks is null || !hooks.TryGetPropertyValue("PreToolUse", out var list) || list is null) return null;
        return list as JsonArray ?? throw new JsonException("hooks.PreToolUse is not an array.");
    }

    private static IEnumerable<(JsonObject Group, JsonArray Handlers, int Index)> Handlers(JsonArray list)
    {
        foreach (var group in list.OfType<JsonObject>())
            if (group["hooks"] is JsonArray handlers)
                for (var index = 0; index < handlers.Count; index++) yield return (group, handlers, index);
    }

    protected override Inspection Inspect(string? text, ItemContext context)
    {
        var root = AgentFiles.ParseObject(text);
        var list = PreToolUse(root);
        if (list is null) return new(AgentItemState.Missing);
        foreach (var (group, handlers, index) in Handlers(list))
        {
            if (!IsOurs(handlers[index])) continue;
            var sameMatcher = handlers.Count > 1 || group["matcher"]?.GetValue<string>() == matcher;
            return new(sameMatcher && JsonNode.DeepEquals(handlers[index], handler(context)) ? AgentItemState.Installed : AgentItemState.Outdated);
        }
        return new(AgentItemState.Missing);
    }

    protected override string Apply(string? text, ItemContext context)
    {
        var root = AgentFiles.ParseObject(text);
        var hooks = AgentFiles.ChildObject(root, "hooks");
        if (hooks is null) { hooks = new JsonObject(); root["hooks"] = hooks; }
        var list = PreToolUse(root);
        if (list is null) { list = new JsonArray(); hooks["PreToolUse"] = list; }
        foreach (var (group, handlers, index) in Handlers(list))
        {
            if (!IsOurs(handlers[index])) continue;
            handlers[index] = handler(context);
            if (handlers.Count == 1) group["matcher"] = matcher;
            return AgentFiles.ToText(root, text);
        }
        list.Add(new JsonObject { ["matcher"] = matcher, ["hooks"] = new JsonArray(handler(context)) });
        return AgentFiles.ToText(root, text);
    }

    protected override string? Remove(string text, ItemContext context)
    {
        var root = AgentFiles.ParseObject(text);
        var list = PreToolUse(root);
        if (list is null) return text;
        foreach (var (group, handlers, index) in Handlers(list).Where(entry => IsOurs(entry.Handlers[entry.Index])).Reverse().ToArray())
        {
            handlers.RemoveAt(index);
            if (handlers.Count == 0) list.Remove(group);
        }
        var hooks = (JsonObject)root["hooks"]!;
        if (list.Count == 0) hooks.Remove("PreToolUse");
        if (hooks.Count == 0) root.Remove("hooks");
        return root.Count == 0 ? null : AgentFiles.ToText(root, text);
    }
}

/// <summary>Our permission rule in the permissions.allow list of Claude Code's settings.json: "mcp__docredock__*" allows every tool
/// of the docredock MCP server (a glob is accepted only after the literal mcp__server__ prefix). Uninstall removes only that string,
/// and the allow list and permissions object when that leaves them empty.</summary>
internal sealed class ClaudeAllowItem(string path, string stopAt) : ManagedItem(AgentClient.Claude, AgentItem.Approval, path, stopAt)
{
    public const string Rule = "mcp__docredock__*";

    private static JsonArray? List(JsonObject root, string key)
    {
        var permissions = AgentFiles.ChildObject(root, "permissions");
        if (permissions is null || !permissions.TryGetPropertyValue(key, out var list) || list is null) return null;
        return list as JsonArray ?? throw new JsonException($"permissions.{key} is not an array.");
    }

    private static bool Has(JsonArray? list, Func<string, bool> match) =>
        list is not null && list.Any(entry => entry is JsonValue value && value.TryGetValue<string>(out var text) && match(text));

    // A rule that denies or asks for DocRedock's tools wins over our allow rule.
    private static bool Overrides(string rule) => rule == "mcp__docredock" || rule.StartsWith("mcp__docredock__", StringComparison.Ordinal);

    protected override Inspection Inspect(string? text, ItemContext context)
    {
        var root = AgentFiles.ParseObject(text);
        var allow = List(root, "allow");
        var deny = Has(List(root, "deny"), Overrides);
        var ask = Has(List(root, "ask"), Overrides);
        if (Has(allow, rule => rule == Rule))
            return new(AgentItemState.Installed, deny || ask ? "A permissions.deny or permissions.ask rule for the docredock tools takes precedence over this rule." : null);
        if (deny || ask) return new(AgentItemState.Conflict, "A permissions.deny or permissions.ask rule for the docredock tools exists that DocRedock did not write; it was left alone.");
        return new(AgentItemState.Missing);
    }

    protected override string Apply(string? text, ItemContext context)
    {
        var root = AgentFiles.ParseObject(text);
        var permissions = AgentFiles.ChildObject(root, "permissions");
        if (permissions is null) { permissions = new JsonObject(); root["permissions"] = permissions; }
        var allow = List(root, "allow");
        if (allow is null) { allow = new JsonArray(); permissions["allow"] = allow; }
        if (!Has(allow, rule => rule == Rule)) allow.Add(Rule);
        return AgentFiles.ToText(root, text);
    }

    protected override string? Remove(string text, ItemContext context)
    {
        var root = AgentFiles.ParseObject(text);
        var allow = List(root, "allow");
        if (allow is null) return text;
        foreach (var entry in allow.Where(entry => entry is JsonValue value && value.TryGetValue<string>(out var rule) && rule == Rule).ToArray()) allow.Remove(entry);
        var permissions = (JsonObject)root["permissions"]!;
        if (allow.Count == 0) permissions.Remove("allow");
        if (permissions.Count == 0) root.Remove("permissions");
        return root.Count == 0 ? null : AgentFiles.ToText(root, text);
    }
}

/// <summary>Codex's config.toml block. The Mcp item is the block itself and keeps an approval line that is there; the Approval item
/// adds or removes that one line inside the block (<c>default_tools_approval_mode = "approve"</c>). Uninstall runs the Approval
/// item first, so removing the block afterwards leaves nothing behind.</summary>
internal sealed class CodexTomlItem(AgentItem item, string path, string stopAt) : ManagedItem(AgentClient.Codex, item, path, stopAt)
{
    private static bool HasApproval(IReadOnlyList<string> lines, (int Start, int End) range) =>
        lines.Skip(range.Start).Take(range.End - range.Start + 1).Any(line => line.Trim() == AgentTemplates.TomlApprovalLine);

    protected override Inspection Inspect(string? text, ItemContext context)
    {
        if (text is null) return new(AgentItemState.Missing);
        var lines = BlockEditor.Lines(text);
        var range = BlockEditor.Find(lines, AgentTemplates.IsTomlBegin, AgentTemplates.IsTomlEnd);
        var conflict = TomlConflict.Find(range is { } r ? lines.Take(r.Start).Concat(lines.Skip(r.End + 1)).ToArray() : lines);
        if (conflict is not null) return new(AgentItemState.Conflict, conflict);
        if (range is not { } found) return new(AgentItemState.Missing);
        var approved = HasApproval(lines, found);
        if (Item == AgentItem.Approval && !approved) return new(AgentItemState.Missing);
        return new(BlockEditor.BlockText(lines, found) == AgentTemplates.TomlBlock(context.Cli, approved) ? AgentItemState.Installed : AgentItemState.Outdated);
    }

    protected override string Apply(string? text, ItemContext context)
    {
        var approved = Item == AgentItem.Approval;
        if (!approved && text is not null)
        {
            var lines = BlockEditor.Lines(text);
            if (BlockEditor.Find(lines, AgentTemplates.IsTomlBegin, AgentTemplates.IsTomlEnd) is { } range) approved = HasApproval(lines, range);
        }
        return BlockEditor.Put(text, AgentTemplates.TomlBlock(context.Cli, approved), AgentTemplates.IsTomlBegin, AgentTemplates.IsTomlEnd);
    }

    protected override string? Remove(string text, ItemContext context)
    {
        if (Item == AgentItem.Mcp) return BlockEditor.Take(text, AgentTemplates.IsTomlBegin, AgentTemplates.IsTomlEnd);
        var lines = BlockEditor.Lines(text);
        if (BlockEditor.Find(lines, AgentTemplates.IsTomlBegin, AgentTemplates.IsTomlEnd) is not { } range) return text;
        var kept = lines.Take(range.Start).Concat(lines.Skip(range.Start).Take(range.End - range.Start + 1).Where(line => line.Trim() != AgentTemplates.TomlApprovalLine))
            .Concat(lines.Skip(range.End + 1));
        return string.Join('\n', kept);
    }
}

/// <summary>Finds a table named docredock under mcp_servers that is not in our block.</summary>
internal static class TomlConflict
{
    private static string Normalize(string key) => Regex.Replace(key, @"\s+|[""']", string.Empty, RegexOptions.CultureInvariant);

    /// <summary>A message when the lines (outside our block) define mcp_servers.docredock in any TOML spelling; otherwise null.</summary>
    public static string? Find(string[] lines)
    {
        var table = string.Empty;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[')
            {
                var inner = line.TrimStart('[');
                var close = inner.IndexOf(']', StringComparison.Ordinal);
                if (close < 0) continue;
                table = Normalize(inner[..close]);
                if (IsDocredock(table)) return "A [mcp_servers.docredock] table exists that DocRedock did not write; it was left alone.";
                continue;
            }
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0) continue;
            var key = Normalize(line[..equals]);
            var full = table.Length == 0 ? key : table + "." + key;
            if (IsDocredock(full)) return "An mcp_servers.docredock setting exists that DocRedock did not write; it was left alone.";
        }
        return null;
    }

    private static bool IsDocredock(string name) => name == "mcp_servers.docredock" || name.StartsWith("mcp_servers.docredock.", StringComparison.Ordinal);
}
