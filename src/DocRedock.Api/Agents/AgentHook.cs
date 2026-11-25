using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocRedock.Api.Agents;

/// <summary>A legacy hook result. Direct source inspection is no longer restricted.</summary>
public sealed record AgentHookDecision(bool Deny, string? File, string? Reason);

/// <summary>Compatibility entry points for installed hooks. Source readers and renderers defer to the client's normal permissions.</summary>
public static class AgentHook
{
    public static AgentHookDecision Decide(AgentClient client,string? eventJson,string cli="docredock")=>new(false,null,null);
    public static string Output(AgentClient client,AgentHookDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return client==AgentClient.Copilot?"{}":string.Empty;
    }
    public static string Run(AgentClient client,string? eventJson,string cli="docredock")=>client==AgentClient.Copilot?"{}":string.Empty;
    /// <summary>Only DocRedock MCP tools are eligible for Copilot's separate optional approval hook.</summary>
    public static string ApproveCopilot(string? eventJson)
    {
        try{return ApprovesCopilot(eventJson)?"{\"behavior\":\"allow\"}":"{}";}
        catch(Exception){return "{}";}
    }
    private static bool ApprovesCopilot(string? eventJson)
    {
        if(string.IsNullOrWhiteSpace(eventJson))return false;
        if(JsonNode.Parse(eventJson,null,new JsonDocumentOptions{AllowTrailingCommas=true,CommentHandling=JsonCommentHandling.Skip}) is not JsonObject root)return false;
        var name=Text(root,"toolName")??Text(root,"tool_name")??string.Empty;
        if(!name.Contains("docredock",StringComparison.OrdinalIgnoreCase))return false;
        if(RequestsSandboxBypass(root))return false;
        foreach(var key in new[]{"toolInput","toolArgs","tool_input"})
            if(RequestsSandboxBypass(Argument(root,key)))return false;
        return true;
    }
    private static bool RequestsSandboxBypass(JsonNode? node)=>node is JsonObject obj&&obj["requestSandboxBypass"] is JsonValue value&&
        (value.TryGetValue<bool>(out var flag)?flag:value.TryGetValue<string>(out var text)&&text.Equals("true",StringComparison.OrdinalIgnoreCase));
    private static string? Text(JsonObject root,string key)=>root[key] is JsonValue value&&value.TryGetValue<string>(out var text)?text:null;
    private static JsonNode? Argument(JsonObject root,string key)
    {
        if(!root.TryGetPropertyValue(key,out var node)||node is null)return null;
        if(node is JsonValue value&&value.TryGetValue<string>(out var text))
        {try{return JsonNode.Parse(text)??node;}catch(JsonException){return node;}}
        return node;
    }
}
