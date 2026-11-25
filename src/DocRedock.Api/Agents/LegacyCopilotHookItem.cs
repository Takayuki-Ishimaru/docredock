using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DocRedock.Api.Agents;

/// <summary>Removes only DocRedock's retired preToolUse handlers, including from a file with user additions.</summary>
internal sealed class LegacyCopilotHookItem(string path,string stopAt):ManagedItem(AgentClient.Copilot,AgentItem.Hook,path,stopAt)
{
    private static bool Owned(JsonNode? entry)=>entry is JsonObject obj&&new[]{"bash","powershell","command"}.Any(key=>
        obj[key] is JsonValue value&&value.TryGetValue<string>(out var command)&&command.Contains("docredock",StringComparison.OrdinalIgnoreCase)&&
        Regex.IsMatch(command,@"\bhook\s+copilot[""'\s]*$",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)));
    protected override Inspection Inspect(string? text,ItemContext context)
    {
        var root=AgentFiles.ParseObject(text);
        var hooks=AgentFiles.ChildObject(root,"hooks");
        if(hooks?["preToolUse"] is { } node&&node is not JsonArray)throw new System.Text.Json.JsonException("hooks.preToolUse is not an array.");
        return hooks?["preToolUse"] is JsonArray list&&list.Any(Owned)?new(AgentItemState.Outdated,"Retired restrictive hook; direct source inspection is allowed. Run docredock agents install (or uninstall) to remove it."):new(AgentItemState.Missing);
    }
    protected override string Apply(string? text,ItemContext context)=>text??"";
    protected override string? Remove(string text,ItemContext context)
    {
        var root=AgentFiles.ParseObject(text);
        if(root["hooks"] is not JsonObject hooks||hooks["preToolUse"] is not JsonArray list)return text;
        for(var i=list.Count-1;i>=0;i--)if(Owned(list[i]))list.RemoveAt(i);
        if(list.Count==0)hooks.Remove("preToolUse");
        if(hooks.Count==0)root.Remove("hooks");
        if(root.Count==0||root.Count==1&&root["version"] is JsonValue value&&value.TryGetValue<int>(out var version)&&version==1)return null;
        return AgentFiles.ToText(root,text);
    }
}
