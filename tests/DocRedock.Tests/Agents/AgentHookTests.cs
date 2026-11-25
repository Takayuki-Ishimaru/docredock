using System.Text.Json;
using System.Text.Json.Nodes;
using DocRedock.Api.Agents;

namespace DocRedock.Tests.Agents;

public sealed class AgentHookTests
{
    private const string Cli = "C:/tools/docredock.exe";

    private static string Claude(string tool, object input) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["hook_event_name"] = "PreToolUse", ["tool_name"] = tool, ["tool_input"] = input });

    private static string Bash(string command) => Claude("Bash", new { command });

    [Fact]
    public void Claude_and_Codex_source_read_hooks_use_no_op_output()
    {
        var output = AgentHook.Run(AgentClient.Claude, Claude("Read", new { file_path = "C:\\docs\\a.docx" }), Cli);
        Assert.Empty(output);
        Assert.Equal(output, AgentHook.Run(AgentClient.Codex, Claude("Read", new { file_path = "a.DOCX" }), Cli).Replace("a.DOCX", "a.docx"));
    }

    [Fact]
    public void Copilot_output_is_the_bare_decision_and_allow_is_an_empty_object()
    {
        var output = AgentHook.Run(AgentClient.Copilot, JsonSerializer.Serialize(new { toolName = "view", toolArgs = new { path = "C:\\x\\s.xlsx" } }), Cli);
        var node = JsonNode.Parse(output)!.AsObject();
        Assert.Empty(node);
        Assert.Equal("{}", AgentHook.Run(AgentClient.Copilot, JsonSerializer.Serialize(new { toolName = "view", toolArgs = new { path = "a.md" } }), Cli));
        Assert.Equal("{}", AgentHook.Run(AgentClient.Copilot, "not json", Cli));
    }

    [Fact]
    public void Copilot_tool_arguments_may_be_a_json_string_and_the_vs_code_variant_is_understood()
    {
        var asString = JsonSerializer.Serialize(new { toolName = "view", toolArgs = JsonSerializer.Serialize(new { path = "C:\\x\\report.pptx" }) });
        Assert.False(AgentHook.Decide(AgentClient.Copilot, asString, Cli).Deny);
        var vsCode = JsonSerializer.Serialize(new { hook_event_name = "PreToolUse", tool_name = "view", tool_input = new { filePath = "/x/report.docm" } });
        Assert.False(AgentHook.Decide(AgentClient.Copilot, vsCode, Cli).Deny);
        var shell = JsonSerializer.Serialize(new { toolName = "bash", toolArgs = JsonSerializer.Serialize(new { command = "unzip -p report.docx word/document.xml" }) });
        Assert.False(AgentHook.Decide(AgentClient.Copilot, shell, Cli).Deny);
        var powershell = JsonSerializer.Serialize(new { toolName = "powershell", toolArgs = new { command = "Expand-Archive .\\a.xlsx -DestinationPath x" } });
        Assert.False(AgentHook.Decide(AgentClient.Copilot, powershell, Cli).Deny);
    }

    [Theory]
    [InlineData("read_file", false)]
    [InlineData("mcp__filesystem__read_text_file", false)]
    [InlineData("readFile", false)]
    [InlineData("fileView", false)]
    [InlineData("View", false)]
    [InlineData("create_spreadsheet", false)] // "read" inside another word
    [InlineData("mcp__sheets__spreadsheetUpdate", false)]
    [InlineData("preview_document", false)]
    [InlineData("thread_reply", false)]
    public void Reading_tool_names_do_not_trigger_restrictions(string tool, bool denied)
    {
        Assert.Equal(denied, AgentHook.Decide(AgentClient.Claude, Claude(tool, new { path = "C:/x/report.xlsx" }), Cli).Deny);
    }

    [Theory]
    [InlineData("Read", "{\"file_path\":\"C:/x/a.docx\"}", false)]
    [InlineData("Read", "{\"file_path\":\"C:/x/b.XLSM\"}", false)]
    [InlineData("Read", "{\"file_path\":\"c.pptx\"}", false)]
    [InlineData("Read", "{\"file_path\":\"a.pdf\"}", false)]
    [InlineData("Read", "{\"file_path\":\"a.md\"}", false)]
    [InlineData("Read", "{\"file_path\":\"a.docx.md\"}", false)]
    [InlineData("Read", "{\"file_path\":\"a.doc\"}", false)]
    [InlineData("NotebookRead", "{\"path\":\"a.docx\"}", false)]
    [InlineData("Edit", "{\"file_path\":\"a.docx\"}", false)]
    [InlineData("Grep", "{\"pattern\":\"x\",\"path\":\"a.docx\"}", false)]
    [InlineData("mcp__docredock__export_markdown", "{\"path\":\"a.docx\"}", false)]
    [InlineData("mcp__docredock__read_source", "{\"path\":\"a.docx\"}", false)]
    [InlineData("mcp__other__read_file", "{\"path\":\"a.docx\"}", false)]
    public void File_reading_tools_remain_available_for_every_format(string tool, string input, bool deny) =>
        Assert.Equal(deny, AgentHook.Decide(AgentClient.Claude, Claude(tool, JsonNode.Parse(input)!), Cli).Deny);

    [Theory]
    [InlineData("unzip -p \"x.xlsx\" xl/sharedStrings.xml", false)]
    [InlineData("unzip -l a.docx | head", false)]
    [InlineData("python -c \"import docx; docx.Document('a.docx')\"", false)]
    [InlineData("python3 - <<'EOF'\nimport openpyxl\nopenpyxl.load_workbook('b.xlsx')\nEOF", false)]
    [InlineData("cd /tmp && 7z x deck.pptx -oout", false)]
    [InlineData("cat a.docx", false)]
    [InlineData("type C:\\docs\\a.docx", false)]
    [InlineData("Get-Content .\\a.xlsx -Raw", false)]
    [InlineData("pandoc a.docx -t plain", false)]
    [InlineData("soffice --headless --convert-to txt a.docx", false)]
    [InlineData("tar -xf a.docx", false)]
    [InlineData("strings 'my report.docx'", false)]
    [InlineData("node -e \"require('fs').readFileSync('a.pptx')\"", false)]
    [InlineData("docredock export a.docx -o -", false)]
    [InlineData("\"C:/x/DocRedock.Cli.exe\" source a.xlsx --sheet S", false)]
    [InlineData("& 'C:\\x\\DocRedock.Cli.exe' export .\\a.docx -o -", false)]
    [InlineData("docredock export a.docx -o - | head -50", false)]
    [InlineData("cp a.docx b.docx", false)]
    [InlineData("mv a.docx old/", false)]
    [InlineData("git add a.pptx", false)]
    [InlineData("git commit -m \"type of head change\" a.docx", false)]
    [InlineData("ls *.docx", false)]
    [InlineData("ls -la a.docx && echo done", false)]
    [InlineData("rm -f a.xlsx", false)]
    [InlineData("cat notes.txt", false)]
    [InlineData("python script.py", false)]
    [InlineData("unzip a.zip", false)]
    [InlineData("unzip -p a.pdf", false)]
    [InlineData("echo a.docx", false)]
    public void Shell_commands_that_read_or_render_originals_remain_available(string command, bool deny)
    {
        Assert.Equal(deny, AgentHook.Decide(AgentClient.Claude, Bash(command), Cli).Deny);
        Assert.Equal(deny, AgentHook.Decide(AgentClient.Claude, Claude("PowerShell", new { command }), Cli).Deny);
        Assert.Equal(deny, AgentHook.Decide(AgentClient.Codex, Claude("Bash", new { command }), Cli).Deny);
    }

    [Fact]
    public void Codex_shell_tool_with_a_command_array_is_checked()
    {
        var json = Claude("shell", new { command = new[] { "unzip", "-p", "a.docx" } });
        Assert.False(AgentHook.Decide(AgentClient.Codex, json, Cli).Deny);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"tool_name\":")]
    [InlineData("{\"tool_name\":42,\"tool_input\":null}")]
    [InlineData("{\"tool_name\":\"Read\"}")]
    [InlineData("{\"tool_name\":\"Bash\",\"tool_input\":{\"command\":7}}")]
    public void Malformed_or_unknown_input_is_allowed(string input)
    {
        foreach (var client in Enum.GetValues<AgentClient>())
        {
            var output = AgentHook.Run(client, input, Cli);
            Assert.Equal(client == AgentClient.Copilot ? "{}" : string.Empty, output);
        }
    }

    [Fact]
    public void Run_never_throws_on_a_null_event()
    {
        Assert.Equal(string.Empty, AgentHook.Run(AgentClient.Claude, null));
    }
}
