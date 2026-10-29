using DocRedock.Api;
using DocRedock.Cli;
using DocRedock.Render;
using System.Text.Json;

namespace DocRedock.Tests.Cli;

public sealed class CliUsabilityRegressionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-cli-usability-").FullName;
    public void Dispose() => Directory.Delete(root, true);
    private async Task<string> Source(string name)
    {
        var path = Path.Combine(root, name + ".docx");
        await new MarkdownRenderer().RenderAsync("Hello world", RenderFormat.Docx, path);
        return path;
    }
    private static CliApplication App(StringWriter output, StringWriter error) => new(output, error, new DocumentService(null, null, discoverPdfRasterizer: false));

    [Fact]
    public async Task Stdout_contains_only_markdown_and_summary_goes_to_stderr()
    {
        var source = await Source("source"); var output = new StringWriter(); var error = new StringWriter();
        var exit = await App(output, error).RunAsync(["export", source, "-o", "-", "--ocr", "off"]);
        Assert.Equal(0, exit); Assert.Contains("Hello world", output.ToString()); Assert.DoesNotContain("Exported:", output.ToString()); Assert.Contains("Exported:", error.ToString());
    }

    [Fact]
    public async Task Quiet_and_json_are_distinct_and_batch_json_is_valid()
    {
        var first = await Source("first"); var second = await Source("second"); var output = new StringWriter(); var error = new StringWriter();
        Assert.Equal(0, await App(output, error).RunAsync(["export", first, "--output", Path.Combine(root,"quiet.md"), "--quiet", "--ocr", "off"]));
        Assert.Equal("", output.ToString()); Assert.Equal("", error.ToString());
        output = new StringWriter();
        Assert.Equal(0, await App(output, error).RunAsync(["export", first, second, "--output-dir", Path.Combine(root,"out"), "--json", "--ocr", "off"]));
        using var json = JsonDocument.Parse(output.ToString()); Assert.Equal(2, json.RootElement.GetProperty("results").GetArrayLength());
        Assert.True(File.Exists(Path.Combine(root,"out","first.md"))); Assert.True(File.Exists(Path.Combine(root,"out","second.md")));
    }

    [Fact]
    public async Task Batch_never_replaces_another_inputs_assets_directory()
    {
        var first = await Source("first");
        var assets = Directory.CreateDirectory(Path.Combine(root, "first.assets")).FullName;
        var second = Path.Combine(assets, "second.docx");
        await new MarkdownRenderer().RenderAsync("Do not remove", RenderFormat.Docx, second);
        var original = await File.ReadAllBytesAsync(second); var error = new StringWriter();
        var code = await App(new(), error).RunAsync(["export", first, second, "--force", "--ocr", "off"]);
        Assert.Equal((int)ExitCode.InvalidInput, code); Assert.Equal(original, await File.ReadAllBytesAsync(second));
        Assert.False(File.Exists(Path.Combine(root, "first.md")));
    }

    [Fact]
    public async Task Batch_rejects_duplicate_names_before_writing_anything()
    {
        var first = await Source("same"); var nested = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        var second = Path.Combine(nested, "same.docx"); File.Copy(first, second);
        var target = Path.Combine(root, "out"); var error = new StringWriter();
        var code = await App(new(), error).RunAsync(["export", first, second, "--output-dir", target]);
        Assert.Equal((int)ExitCode.InvalidInput, code); Assert.Contains("same output", error.ToString()); Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task Directory_batch_json_reports_legacy_files_and_converts_supported_files()
    {
        await Source("document"); await File.WriteAllTextAsync(Path.Combine(root, "old.doc"), "legacy");
        await File.WriteAllTextAsync(Path.Combine(root, "ignored.txt"), "unrelated");
        var output = new StringWriter();
        var code = await App(output, new()).RunAsync(["export", root, "--output-dir", Path.Combine(root, "out"), "--json", "--ocr", "off"]);
        Assert.Equal((int)ExitCode.Unsupported, code);
        using var json = JsonDocument.Parse(output.ToString()); var entries = json.RootElement.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length); Assert.Contains(entries, e => e.GetProperty("error").GetString()!.Contains("LibreOffice"));
        Assert.True(File.Exists(Path.Combine(root, "out", "document.md")));
    }

    [Fact]
    public async Task Cancelled_stdout_export_emits_no_body()
    {
        var source = await Source("cancel"); var output = new StringWriter(); var error = new StringWriter();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var code = await App(output, error).RunAsync(["export", source, "--output", "-"], cancelled.Token);
        Assert.Equal((int)ExitCode.InvalidInput, code); Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task Output_under_directory_alias_does_not_overwrite_input()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = await Source("source"); var alias = Path.Combine(root, "alias"); Directory.CreateSymbolicLink(alias, root);
        var output = Path.Combine(alias, "source.docx"); var original = await File.ReadAllBytesAsync(source);
        var code = await App(new(), new()).RunAsync(["export", source, "--output", output, "--force", "--ocr", "off"]);
        Assert.Equal((int)ExitCode.InvalidInput, code); Assert.Equal(original, await File.ReadAllBytesAsync(source));
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.SafeDirectories)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA2101", Justification = "libc requires UTF-8 paths.")]
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int mkfifo([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, uint mode);

    [Fact]
    public void Force_refuses_fifo_socket_and_device()
    {
        if (!OperatingSystem.IsLinux()) return;
        var fifo = Path.Combine(root, "fifo"); Assert.Equal(0, mkfifo(fifo, 0x180));
        Assert.Throws<IOException>(() => new StagedOutputTransaction([fifo], true));
        var socket = Path.Combine(root, "socket");
        using var listener = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        listener.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(socket));
        Assert.Throws<IOException>(() => new StagedOutputTransaction([socket], true));
        Assert.Throws<IOException>(() => new StagedOutputTransaction(["/dev/null"], true));
    }

    [Fact]
    public void Force_refuses_existing_and_late_symbolic_links()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Path.Combine(root, "target.md"); var link = Path.Combine(root, "output.md"); File.WriteAllText(target, "keep"); File.CreateSymbolicLink(link, target);
        Assert.Throws<IOException>(() => new StagedOutputTransaction([link], true)); Assert.Equal("keep", File.ReadAllText(target));
        File.Delete(link); using var transaction = new StagedOutputTransaction([link], true); File.WriteAllText(transaction.PathFor(link), "new"); File.CreateSymbolicLink(link, target);
        Assert.Throws<IOException>(() => transaction.Commit()); Assert.Equal("keep", File.ReadAllText(target)); Assert.NotNull(new FileInfo(link).LinkTarget);
    }
}
