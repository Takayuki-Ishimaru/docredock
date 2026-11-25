using System.Text.Json;
using System.Text.Json.Nodes;
using DocRedock.Api.Agents;

namespace DocRedock.Tests.Agents;

public sealed class AgentDirectInspectionTests
{
    [Theory]
    [InlineData("Read", "a.docx")]
    [InlineData("read_file", "a.xlsx")]
    [InlineData("view", "a.pptx")]
    [InlineData("open_file", "a.pdf")]
    [InlineData("read_file", "a.docm")]
    public void Integration_does_not_deny_direct_source_inspection_or_grant_extra_permissions(string tool,string file)
    {
        foreach(var client in Enum.GetValues<AgentClient>())
        {
            var input=JsonSerializer.Serialize(new { tool_name=tool,toolName=tool,tool_input=new{path=file},toolArgs=new{path=file} });
            Assert.False(AgentHook.Decide(client,input).Deny);
            Assert.Equal(client==AgentClient.Copilot?"{}":"",AgentHook.Run(client,input));
            Assert.Equal("{}",AgentHook.ApproveCopilot(input));
        }
    }

    [Theory]
    [InlineData("soffice --headless --convert-to pdf report.docx")]
    [InlineData("python -c \"import openpyxl; openpyxl.load_workbook('report.xlsx')\"")]
    [InlineData("Expand-Archive 'report.pptx' -DestinationPath preview")]
    [InlineData("pdftoppm -png report.pdf preview")]
    public void Layout_renderers_and_source_SDKs_are_allowed_with_integration_enabled(string command)
    {
        foreach(var client in Enum.GetValues<AgentClient>())
        {
            var input=JsonSerializer.Serialize(new{tool_name="exec_command",toolName="powershell",tool_input=new{command},toolArgs=new{command}});
            Assert.False(AgentHook.Decide(client,input).Deny);
            Assert.Equal(client==AgentClient.Copilot?"{}":"",AgentHook.Run(client,input));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void Updating_integration_retires_only_owned_legacy_hooks_and_replaces_restrictive_instructions(bool? enforce)
    {
        using var box=new AgentSandbox();
        foreach(var client in Enum.GetValues<AgentClient>())SeedLegacyHook(box,client,mixed:true);
        box.Write(box.P(".codex","AGENTS.md"),"Personal rules.\n\n<!-- docredock:begin legacy -->\nNever open Office files directly.\n<!-- docredock:end -->\n");
        box.Write(box.P(".agents","skills","docredock-office","SKILL.md"),"---\nname: docredock-office\n---\nDocRedock: Never open Office files directly.\n");
        var before=box.Snapshot();
        var plan=box.Service.Plan(AgentSandbox.Options(enforce:enforce),box.Environment());
        Assert.Equal(before,box.Snapshot());
        Assert.Equal(3,plan.Items.Count(i=>i.Item==AgentItem.Hook&&i.Action==AgentAction.Remove));
        var report=box.Service.Install(AgentSandbox.Options(enforce:enforce),box.Environment());
        Assert.False(report.HasProblems);
        foreach(var path in new[]{box.P(".claude","settings.json"),box.P(".codex","hooks.json"),box.P(".copilot","hooks","docredock.json")})
        {
            var contents=File.ReadAllText(path);
            Assert.Contains("user-audit",contents);
            Assert.DoesNotContain(" hook ",contents);
        }
        var instructions=File.ReadAllText(box.P(".codex","AGENTS.md"));
        Assert.StartsWith("Personal rules.",instructions);
        Assert.Contains("Conversion is not a prerequisite",instructions);
        Assert.DoesNotContain("Never open",instructions);
        var skill=File.ReadAllText(box.P(".agents","skills","docredock-office","SKILL.md"));
        Assert.Contains("No conversion-first requirement",skill);
        Assert.DoesNotContain("Never open",skill);
        var updated=box.Snapshot();
        box.Service.Install(AgentSandbox.Options(enforce:enforce),box.Environment());
        Assert.Equal(updated,box.Snapshot());
        Assert.All(box.Service.Status(AgentOptions.AllClients,box.Environment()).Items.Where(i=>i.Item==AgentItem.Hook),i=>Assert.Equal(AgentItemState.Missing,i.State));
    }

    [Fact]
    public void Status_shows_a_leftover_legacy_hook_as_outdated_with_the_way_to_remove_it()
    {
        using var box=new AgentSandbox();
        foreach(var client in Enum.GetValues<AgentClient>())SeedLegacyHook(box,client,mixed:true);
        var status=box.Service.Status(AgentOptions.AllClients,box.Environment());
        foreach(var client in Enum.GetValues<AgentClient>())
        {
            var hook=AgentSandbox.Item(status,client,AgentItem.Hook);
            Assert.Equal(AgentItemState.Outdated,hook.State);
            Assert.Contains("agents install",hook.Detail,StringComparison.Ordinal);
        }
    }

    internal static void SeedLegacyHook(AgentSandbox box,AgentClient client,bool mixed=false)
    {
        var owned=new JsonObject{["type"]="command"};
        owned[client==AgentClient.Copilot?"powershell":"command"]="\""+box.Cli+"\" hook "+client.ToString().ToLowerInvariant();
        JsonObject root;
        string path;
        if(client==AgentClient.Copilot)
        {
            var entries=new JsonArray(owned);
            if(mixed)entries.Add(new JsonObject{["type"]="command",["powershell"]="user-audit"});
            root=new(){["version"]=1,["hooks"]=new JsonObject{["preToolUse"]=entries}};
            path=box.P(".copilot","hooks","docredock.json");
        }
        else
        {
            var entries=new JsonArray(owned);
            if(mixed)entries.Add(new JsonObject{["type"]="command",["command"]="user-audit"});
            root=new(){["hooks"]=new JsonObject{["PreToolUse"]=new JsonArray(new JsonObject{["matcher"]="*",["hooks"]=entries})}};
            path=client==AgentClient.Claude?box.P(".claude","settings.json"):box.P(".codex","hooks.json");
        }
        box.Write(path,root.ToJsonString());
    }
}
