using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocRedock.Render;

public sealed record MermaidRenderRequest(
    string ExecutablePath = "mmdc",
    string BackgroundColor = "white",
    int Width = 1200,
    TimeSpan? Timeout = null,
    long MaxOutputBytes = 33_554_432);

public interface IMermaidRenderer
{
    Task<byte[]> RenderPngAsync(string source, MermaidRenderRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Invokes an explicitly resolved local Mermaid CLI without a shell. The source
/// is written to a bounded temporary directory and no remote resources are
/// accepted in diagram text.
/// </summary>
public sealed class MermaidCliRenderer : IMermaidRenderer
{
    private const int MaxSourceCharacters = 1_048_576;
    private const int MaxDiagnosticCharacters = 131_072;
    private static readonly Regex RemoteReference = new(
        @"(?i)(?:\b(?:https?|ftp|file|data|javascript):|(?:src|href)\s*=|\.\.[/\\])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<byte[]> RenderPngAsync(string source, MermaidRenderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidDataException("Mermaid diagram source is empty.");
        if (source.Length > MaxSourceCharacters) throw new InvalidDataException("Mermaid diagram source exceeds the 1 MiB character limit.");
        if (RemoteReference.IsMatch(source)) throw new InvalidDataException("Mermaid diagrams cannot reference URLs, data URIs, or parent paths.");
        if (source.Contains("%%{", StringComparison.Ordinal)) throw new InvalidDataException("Mermaid init directives are not accepted; DocRedock supplies a strict renderer configuration.");
        if (request.Width is < 320 or > 4096) throw new ArgumentOutOfRangeException(nameof(request), "Mermaid width must be between 320 and 4096 pixels.");
        if (request.MaxOutputBytes is < 4096 or > 268_435_456) throw new ArgumentOutOfRangeException(nameof(request), "Mermaid output limit must be between 4 KiB and 256 MiB.");
        if (!IsBackgroundColor(request.BackgroundColor)) throw new ArgumentException("Mermaid background must be white, transparent, or a hexadecimal color.", nameof(request));

        var executable = ResolveExecutable(request.ExecutablePath)
            ?? throw new FileNotFoundException($"Mermaid CLI executable '{request.ExecutablePath}' was not found. Install @mermaid-js/mermaid-cli or pass an explicit executable path.");
        var timeoutValue = request.Timeout ?? TimeSpan.FromSeconds(60);
        if (timeoutValue <= TimeSpan.Zero || timeoutValue > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(request), "Mermaid timeout must be positive and no longer than ten minutes.");

        var tempRoot = Path.Combine(Path.GetTempPath(), "docredock-mermaid", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var inputPath = Path.Combine(tempRoot, "diagram.mmd");
        var outputPath = Path.Combine(tempRoot, "diagram.png");
        var configPath = Path.Combine(tempRoot, "mermaid-config.json");
        try
        {
            await File.WriteAllTextAsync(inputPath, source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var config = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["securityLevel"] = "strict",
                ["htmlLabels"] = false,
                ["maxTextSize"] = MaxSourceCharacters,
            });
            await File.WriteAllTextAsync(configPath, config, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = tempRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(inputPath);
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(outputPath);
            startInfo.ArgumentList.Add("-b");
            startInfo.ArgumentList.Add(request.BackgroundColor);
            startInfo.ArgumentList.Add("-w");
            startInfo.ArgumentList.Add(request.Width.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(configPath);

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("Mermaid CLI process could not start.");
            }
            catch (Win32Exception exception)
            {
                throw new InvalidOperationException($"Mermaid CLI executable '{executable}' could not be started directly.", exception);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutValue);
            using var killRegistration = timeout.Token.Register(() => TryKill(process));
            var stdoutTask = ReadLimitedAsync(process.StandardOutput, MaxDiagnosticCharacters);
            var stderrTask = ReadLimitedAsync(process.StandardError, MaxDiagnosticCharacters);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                throw new TimeoutException($"Mermaid CLI exceeded the {timeoutValue.TotalSeconds:0}-second timeout.");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new InvalidDataException(string.IsNullOrWhiteSpace(detail)
                    ? $"Mermaid CLI exited with code {process.ExitCode}."
                    : $"Mermaid CLI exited with code {process.ExitCode}: {detail.Trim()}");
            }
            if (!File.Exists(outputPath)) throw new InvalidDataException("Mermaid CLI did not produce a PNG output file.");
            var info = new FileInfo(outputPath);
            if (info.Length <= 0 || info.Length > request.MaxOutputBytes)
                throw new InvalidDataException($"Mermaid PNG output is empty or exceeds the {request.MaxOutputBytes}-byte limit.");
            return await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static bool IsBackgroundColor(string value)
    {
        if (value.Equals("white", StringComparison.OrdinalIgnoreCase) || value.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return true;
        return Regex.IsMatch(value, "^#[0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?$", RegexOptions.CultureInvariant);
    }

    private static string? ResolveExecutable(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        if (Path.IsPathRooted(configured)) return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        if (configured.Contains(Path.DirectorySeparatorChar) || configured.Contains(Path.AltDirectorySeparatorChar))
        {
            var fullPath = Path.GetFullPath(configured);
            return File.Exists(fullPath) ? fullPath : null;
        }

        var suffixes = OperatingSystem.IsWindows() && Path.GetExtension(configured).Length == 0
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.COM;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Prepend(string.Empty)
            : [string.Empty];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        foreach (var suffix in suffixes)
        {
            var candidate = Path.Combine(directory, configured + suffix);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, int maxCharacters)
    {
        var buffer = new char[4096];
        var result = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0) break;
            if (result.Length < maxCharacters) result.Append(buffer, 0, Math.Min(read, maxCharacters - result.Length));
        }
        return result.ToString();
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* process already exited */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best-effort cleanup */ }
    }
}
