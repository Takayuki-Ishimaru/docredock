using System.Diagnostics;
using System.Text;

namespace DocRedock.Api;

/// <summary>Probes optional local tools without treating their names as proof of availability.</summary>
public sealed class CapabilityReporter
{
    private readonly Func<string?, string?> resolveExecutable;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<CapabilityProbeResult>> run;
    private readonly Func<CapabilityStatus>? nativeOcr;

    public CapabilityReporter(
        Func<string?, string?>? resolveExecutable = null,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<CapabilityProbeResult>>? run = null,
        Func<CapabilityStatus>? nativeOcr = null)
    {
        this.resolveExecutable = resolveExecutable ?? PdfRasterizerFactory.ResolveExecutable;
        this.run = run ?? RunBoundedAsync;
        this.nativeOcr = nativeOcr;
    }

    public async Task<IReadOnlyList<CapabilityStatus>> ReportAsync(CapabilityStatus rasterizer, CancellationToken cancellationToken = default)
    {
        var capabilities = new List<CapabilityStatus>
        {
            new("docx-readable", "ready"), new("xlsx-readable", "ready"), new("pptx-readable", "ready"), new("pdf-text", "ready"),
        };
        var tesseract = resolveExecutable("tesseract");
        if (tesseract is null)
        {
            capabilities.Add(new("ocr-engine", "unavailable", "tesseract", Action: "Install Tesseract OCR and its language data."));
            capabilities.AddRange(LanguageCapabilities("unavailable", null));
        }
        else
        {
            var probe = await run(tesseract, ["--list-langs"], cancellationToken).ConfigureAwait(false);
            if (!probe.Succeeded)
            {
                capabilities.Add(new("ocr-engine", "partial", "tesseract", tesseract, "Tesseract was found but language probing failed; run 'tesseract --list-langs'."));
                capabilities.AddRange(LanguageCapabilities("partial", tesseract));
            }
            else
            {
                var languages = probe.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(line => !line.StartsWith("List of available languages", StringComparison.OrdinalIgnoreCase))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                capabilities.Add(new("ocr-engine", "ready", "tesseract", tesseract));
                capabilities.Add(Language("jpn", languages, tesseract));
                capabilities.Add(Language("eng", languages, tesseract));
            }
        }
        capabilities.Add(nativeOcr?.Invoke() ?? NativeOcr());
        capabilities.Add(rasterizer);
        capabilities.Add(await MermaidAsync(cancellationToken).ConfigureAwait(false));
        return capabilities;
    }

    private static IEnumerable<CapabilityStatus> LanguageCapabilities(string status, string? path) =>
        [new("ocr-jpn", status, "tesseract", path, "Install the jpn traineddata package."),
         new("ocr-eng", status, "tesseract", path, "Install the eng traineddata package.")];

    private static CapabilityStatus Language(string language, ISet<string> available, string path) => available.Contains(language)
        ? new("ocr-" + language, "ready", "tesseract", path)
        : new("ocr-" + language, "unavailable", "tesseract", path, $"Install the {language} traineddata package.");

    private CapabilityStatus NativeOcr()
    {
        if (OperatingSystem.IsMacOS())
        {
            var helper = Path.Combine(AppContext.BaseDirectory, "vision-ocr.swift");
            var swift = resolveExecutable("swift");
            return File.Exists(helper) && swift is not null
                ? new("ocr-native", "partial", "apple-vision", swift, "The helper and Swift were found; native OCR is not marked ready until an image invocation succeeds.")
                : new("ocr-native", "unavailable", "apple-vision", Action: "Install Swift and keep vision-ocr.swift beside the application.");
        }
        if (OperatingSystem.IsWindows())
        {
            var helper = Path.Combine(AppContext.BaseDirectory, "windows-ocr.ps1");
            var shell = resolveExecutable(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"));
            return File.Exists(helper) && shell is not null
                ? new("ocr-native", "partial", "windows-media", shell, "Windows Media OCR is configured but its installed language packs are not probed.")
                : new("ocr-native", "unavailable", "windows-media", Action: "Keep windows-ocr.ps1 beside the application and enable Windows PowerShell.");
        }
        return new("ocr-native", "unavailable", "system", Action: "No native OCR provider is bundled for this platform; install Tesseract.");
    }

    private async Task<CapabilityStatus> MermaidAsync(CancellationToken cancellationToken)
    {
        var executable = resolveExecutable("mmdc");
        if (executable is not null)
        {
            var probe = await run(executable, ["--version"], cancellationToken).ConfigureAwait(false);
            return probe.Succeeded
                ? new("mermaid-render", "ready", "mmdc", executable)
                : new("mermaid-render", "partial", "mmdc", executable, "mmdc was found but its version probe failed.");
        }
        // A .cmd shim is deliberately never launched through a shell. It is a useful configuration hint, not readiness evidence.
        if (OperatingSystem.IsWindows() && FindPathFile("mmdc.cmd") is not null)
            return new("mermaid-render", "partial", "mmdc", Action: "A Windows .cmd shim was found but is not executed; configure an mmdc.exe path.");
        return new("mermaid-render", "unavailable", "mmdc", Action: "Install @mermaid-js/mermaid-cli or configure --mermaid-cli.");
    }

    private static string? FindPathFile(string name) => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(directory => Path.Combine(directory, name)).FirstOrDefault(File.Exists);

    internal static async Task<CapabilityProbeResult> RunBoundedAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            process = new Process { StartInfo = new ProcessStartInfo { FileName = executable, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return new(false, string.Empty);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            // Drain both redirected streams concurrently.  A cap is enforced while reading,
            // so a hostile local executable cannot make doctor allocate an arbitrary string.
            var output = ReadLimitedAsync(process.StandardOutput, 1_048_576, timeout.Token);
            var error = ReadLimitedAsync(process.StandardError, 65_536, timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
                await ObserveReadersAsync(output, error).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new(false, string.Empty);
            }
            var stdout = await output.ConfigureAwait(false);
            var stderr = await error.ConfigureAwait(false);
            return new(process.ExitCode == 0 && !stdout.Truncated && !stderr.Truncated,
                stdout.Truncated || stderr.Truncated ? string.Empty : stdout.Text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            if (process is not null) await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            TryKill(process);
            if (process is not null) await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            return new(false, string.Empty);
        }
        finally { process?.Dispose(); }
    }

    private static async Task<(string Text, bool Truncated)> ReadLimitedAsync(StreamReader reader, int maximumBytes, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var text = new StringBuilder(Math.Min(maximumBytes, 4096));
        var bytes = 0;
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return (text.ToString(), truncated);
            var available = maximumBytes - bytes;
            var take = 0;
            while (take < read && available > 0)
            {
                var byteCount = Encoding.UTF8.GetByteCount(buffer, take, 1);
                if (byteCount > available) break;
                available -= byteCount;
                bytes += byteCount;
                take++;
            }
            if (take > 0) text.Append(buffer, 0, take);
            if (take != read) truncated = true;
        }
    }

    private static void TryKill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        try { await process.WaitForExitAsync().ConfigureAwait(false); } catch (InvalidOperationException) { }
    }

    private static async Task ObserveReadersAsync(Task<(string Text, bool Truncated)> output, Task<(string Text, bool Truncated)> error)
    {
        try { await Task.WhenAll(output, error).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}

public sealed record CapabilityProbeResult(bool Succeeded, string StandardOutput);
