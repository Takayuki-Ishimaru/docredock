using System.Text.Json;
using DocRedock.Api;
using DocRedock.Api.Agents;
using DocRedock.Cli;

namespace DocRedock.Tests.Agents;

/// <summary>The Copilot permissionRequest hook program: <c>docredock hook copilot-approve</c>.</summary>
public sealed class AgentApproveHookTests
{
    private const string Allow = "{\"behavior\":\"allow\"}";

    private static string CamelObject(string tool, object args) => JsonSerializer.Serialize(new { toolName = tool, toolInput = args });
    private static string SnakeObject(string tool, object args) => JsonSerializer.Serialize(new { tool_name = tool, tool_input = args });
    private static string CamelString(string tool, object args) => JsonSerializer.Serialize(new { toolName = tool, toolArgs = JsonSerializer.Serialize(args) });

    [Theory]
    [InlineData("docredock-export_markdown")]
    [InlineData("docredock/read_source")]
    [InlineData("mcp__docredock__read_source")]
    [InlineData("DocRedock-export_html")]
    [InlineData("mcp_docredock_package_read")]
    public void Docredock_tools_are_allowed_in_every_payload_shape(string tool)
    {
        var args = new { path = "C:/a/a.docx" };
        Assert.Equal(Allow, AgentHook.ApproveCopilot(CamelObject(tool, args)));
        Assert.Equal(Allow, AgentHook.ApproveCopilot(SnakeObject(tool, args)));
        Assert.Equal(Allow, AgentHook.ApproveCopilot(CamelString(tool, args)));
        Assert.Equal(Allow, AgentHook.ApproveCopilot(JsonSerializer.Serialize(new { toolName = tool })));
        Assert.Equal(Allow, AgentHook.ApproveCopilot(JsonSerializer.Serialize(new { toolName = tool, toolArgs = new { path = "a.docx", requestSandboxBypass = false } })));
    }

    [Theory]
    [InlineData("view")]
    [InlineData("bash")]
    [InlineData("mcp__github__create_issue")]
    [InlineData("")]
    public void Other_tools_fall_through(string tool)
    {
        Assert.Equal("{}", AgentHook.ApproveCopilot(CamelObject(tool, new { path = "a.md" })));
        Assert.Equal("{}", AgentHook.ApproveCopilot(SnakeObject(tool, new { command = "docredock export a.docx" })));
        Assert.Equal("{}", AgentHook.ApproveCopilot(CamelString(tool, new { command = "docredock export a.docx" })));
    }

    [Fact]
    public void A_sandbox_bypass_request_is_never_approved_in_any_argument_shape()
    {
        var bypass = new { path = "a.docx", requestSandboxBypass = true };
        Assert.Equal("{}", AgentHook.ApproveCopilot(CamelObject("docredock-export_markdown", bypass)));
        Assert.Equal("{}", AgentHook.ApproveCopilot(SnakeObject("docredock-export_markdown", bypass)));
        Assert.Equal("{}", AgentHook.ApproveCopilot(CamelString("docredock-export_markdown", bypass)));
        Assert.Equal("{}", AgentHook.ApproveCopilot(JsonSerializer.Serialize(new { toolName = "docredock-export_markdown", toolArgs = bypass })));
        Assert.Equal("{}", AgentHook.ApproveCopilot(JsonSerializer.Serialize(new { toolName = "docredock-export_markdown", requestSandboxBypass = true })));
        Assert.Equal("{}", AgentHook.ApproveCopilot("{\"toolName\":\"docredock-x\",\"toolInput\":{\"requestSandboxBypass\":\"true\"}}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("[1,2]")]
    [InlineData("\"docredock\"")]
    [InlineData("null")]
    [InlineData("{\"toolName\":42}")]
    [InlineData("{\"toolName\":{\"a\":\"docredock\"}}")]
    [InlineData("{\"toolName\":\"docredock-x\"")]
    public void Malformed_input_falls_through(string input) => Assert.Equal("{}", AgentHook.ApproveCopilot(input));

    [Fact]
    public void Null_input_falls_through() => Assert.Equal("{}", AgentHook.ApproveCopilot(null));

    private static async Task<(int Code, string Out, string Err)> RunAsync(string stdin, MemoryStream? stream = null, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var app = new CliApplication(output, error, new DocumentService(null, null, discoverPdfRasterizer: false), new StringReader(stdin)) { HookOutput = stream };
        var code = await app.RunAsync(args);
        return (code, output.ToString(), error.ToString());
    }

    [Theory]
    [InlineData("{\"toolName\":\"docredock-export_markdown\",\"toolInput\":{\"path\":\"a.docx\"}}", Allow)]
    [InlineData("{\"tool_name\":\"mcp__docredock__read_source\",\"tool_input\":{}}", Allow)]
    [InlineData("{\"toolName\":\"bash\",\"toolArgs\":\"{\\\"command\\\":\\\"ls\\\"}\"}", "{}")]
    [InlineData("{\"toolName\":\"docredock-x\",\"toolInput\":{\"requestSandboxBypass\":true}}", "{}")]
    [InlineData("garbage", "{}")]
    [InlineData("", "{}")]
    public async Task The_command_prints_the_decision_and_exits_0(string stdin, string expected)
    {
        var (code, output, error) = await RunAsync(stdin, null, "hook", "copilot-approve");
        Assert.Equal(0, code);
        Assert.Equal(expected, output);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public async Task The_command_exits_0_and_prints_an_empty_object_when_standard_input_fails()
    {
        var output = new StringWriter();
        var app = new CliApplication(output, new StringWriter(), new DocumentService(null, null, discoverPdfRasterizer: false), new ThrowingReader());
        Assert.Equal(0, await app.RunAsync(["hook", "copilot-approve"]));
        Assert.Equal("{}", output.ToString());
    }

    [Fact]
    public async Task The_command_writes_utf8_without_a_byte_order_mark()
    {
        var stream = new MemoryStream();
        var (code, _, _) = await RunAsync("{\"toolName\":\"docredock-資料\",\"toolInput\":{\"path\":\"C:/資料/設計書.docx\"}}", stream, "hook", "copilot-approve");
        Assert.Equal(0, code);
        var bytes = stream.ToArray();
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal(Allow, new System.Text.UTF8Encoding(false, true).GetString(bytes));

        var denied = new MemoryStream();
        await RunAsync("{\"toolName\":\"view\"}", denied, "hook", "copilot-approve");
        Assert.Equal("{}", new System.Text.UTF8Encoding(false, true).GetString(denied.ToArray()));
    }

    private sealed class ThrowingReader : TextReader
    {
        public override Task<string> ReadToEndAsync() => throw new IOException("closed");
        public override string ReadToEnd() => throw new IOException("closed");
    }
}
