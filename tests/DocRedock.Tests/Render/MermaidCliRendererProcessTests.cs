using System.Diagnostics;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

public sealed class MermaidCliRendererProcessTests
{
    [Fact]
    public async Task Timeout_includes_pipes_inherited_by_a_child_after_the_cli_exits()
    {
        if (OperatingSystem.IsWindows()) return;
        using var executable = new FakeCli(leaveChild: true);
        var operation = new MermaidCliRenderer().RenderPngAsync("graph TD\nA --> B",
            new MermaidRenderRequest(executable.Path, Timeout: TimeSpan.FromMilliseconds(350)));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => operation.WaitAsync(TimeSpan.FromSeconds(4)));
            Assert.True(operation.IsCompleted, "The renderer remained blocked on inherited diagnostic pipes after its timeout.");
        }
        finally
        {
            executable.StopChild();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    [Fact]
    public async Task Caller_cancellation_interrupts_inherited_diagnostic_pipes()
    {
        if (OperatingSystem.IsWindows()) return;
        using var executable = new FakeCli(leaveChild: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        var operation = new MermaidCliRenderer().RenderPngAsync("graph TD\nA --> B",
            new MermaidRenderRequest(executable.Path, Timeout: TimeSpan.FromSeconds(10)), cancellation.Token);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(4)));
        }
        finally
        {
            executable.StopChild();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    [Fact]
    public async Task Successful_cli_output_is_returned_after_both_diagnostic_streams_are_drained()
    {
        if (OperatingSystem.IsWindows()) return;
        using var executable = new FakeCli(leaveChild: false);
        var bytes = await new MermaidCliRenderer().RenderPngAsync("graph TD\nA --> B",
            new MermaidRenderRequest(executable.Path, Timeout: TimeSpan.FromSeconds(5)));
        Assert.Equal("png", System.Text.Encoding.UTF8.GetString(bytes));
    }

    private sealed class FakeCli : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "docredock-mermaid-test-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }

        public FakeCli(bool leaveChild)
        {
            Directory.CreateDirectory(root);
            Path = System.IO.Path.Combine(root, "mmdc");
            File.WriteAllText(Path, leaveChild
                ? "#!/bin/sh\nsleep 30 &\nprintf '%s' \"$!\" > \"$0.pid\"\nexit 0\n"
                : "#!/bin/sh\nwhile [ \"$#\" -gt 0 ]; do\n  if [ \"$1\" = '-o' ]; then shift; output=$1; fi\n  shift\ndone\nprintf 'diagnostic' >&2\nprintf 'png' > \"$output\"\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void StopChild()
        {
            if (!File.Exists(Path + ".pid")) return;
            if (!int.TryParse(File.ReadAllText(Path + ".pid"), out var pid)) return;
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        public void Dispose()
        {
            StopChild();
            Directory.Delete(root, recursive: true);
        }
    }
}
