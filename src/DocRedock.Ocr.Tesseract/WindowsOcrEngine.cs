using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Providers.Abstractions.Providers;

namespace DocRedock.Ocr.Tesseract;

/// <summary>
/// Uses the Windows inbox OCR API through a Windows PowerShell helper. Keeping
/// the WinRT calls outside this assembly lets the same CLI build run on macOS
/// and Linux without a Windows-only target framework.
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    private const int HelperUnavailableExitCode = 10;
    private readonly string helperPath;
    private readonly string powershellExecutable;
    private readonly TimeSpan defaultTimeout;
    private readonly long maxOutputBytes;

    public WindowsOcrEngine(
        string? helperPath = null,
        string? powershellExecutable = null,
        TimeSpan? defaultTimeout = null,
        long maxOutputBytes = 1_048_576)
    {
        this.helperPath = Path.GetFullPath(helperPath ?? Path.Combine(AppContext.BaseDirectory, "windows-ocr.ps1"));
        this.powershellExecutable = powershellExecutable ?? DefaultPowerShellExecutable();
        this.defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(60);
        this.maxOutputBytes = Math.Max(4096, maxOutputBytes);
    }

    public ProviderDescriptor Descriptor { get; } = new(
        "docredock.ocr.windows-media",
        new Version(1, 0, 0),
        1,
        new HashSet<string>(StringComparer.Ordinal) { "ocr.text", "ocr.jpn", "ocr.eng" },
        "Windows-SDK",
        "system-runtime",
        false);

    public async ValueTask<OcrAttemptResult> RecognizeAsync(OcrInput input, OcrOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Failure(OcrProcessingStatus.Unavailable, "WindowsOcrUnavailable", "Windows Media OCR is available only on Windows.");
        if (input.Image is null || !input.Image.CanRead)
            return Failure(OcrProcessingStatus.Failed, "InputStream", "OcrInput.Image is not readable.");
        if (!File.Exists(helperPath))
            return Failure(OcrProcessingStatus.Unavailable, "HelperUnavailable", $"Windows OCR helper was not found at '{helperPath}'.");

        var powershellPath = ResolveExecutable(powershellExecutable);
        if (powershellPath is null)
            return Failure(OcrProcessingStatus.Unavailable, "ExecutableUnavailable", $"Windows PowerShell executable '{powershellExecutable}' was not found.");

        var imageBytes = await ReadImageAsync(input.Image, cancellationToken).ConfigureAwait(false);
        if (options.PixelBudget is { } pixelBudget &&
            (pixelBudget <= 0 || imageBytes.LongLength > pixelBudget))
        {
            return new(OcrProcessingStatus.SkippedByBudget, null,
                [new OcrDiagnostic("PixelBudgetExceeded", $"OCR input exceeds the configured {pixelBudget}-byte image budget.", DiagnosticSeverity.Warning)]);
        }

        var timeoutValue = options.Timeout ?? defaultTimeout;
        if (timeoutValue <= TimeSpan.Zero)
            return Failure(OcrProcessingStatus.Failed, "InvalidTimeout", "OCR timeout must be positive.");

        var tempRoot = Path.Combine(Path.GetTempPath(), "docredock-ocr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var inputPath = Path.Combine(tempRoot, "input" + ExtensionForMediaType(input.MediaType));
        try
        {
            await File.WriteAllBytesAsync(inputPath, imageBytes, cancellationToken).ConfigureAwait(false);
            var psi = new ProcessStartInfo
            {
                FileName = powershellPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(helperPath);
            psi.ArgumentList.Add(inputPath);
            foreach (var language in NormalizeLanguages(options.Languages))
                psi.ArgumentList.Add(language);

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start())
                return Failure(OcrProcessingStatus.Failed, "ProcessStart", "Windows OCR process could not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutValue);
            using var killRegistration = timeout.Token.Register(() => TryKill(process));
            var stdoutTask = ReadLimitedAsync(process.StandardOutput, maxOutputBytes, CancellationToken.None);
            var stderrTask = ReadLimitedAsync(process.StandardError, maxOutputBytes, CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                return Failure(OcrProcessingStatus.Failed, "Timeout", "Windows OCR timed out.");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var output = await stdoutTask.ConfigureAwait(false);
            var error = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode == HelperUnavailableExitCode)
            {
                return Failure(OcrProcessingStatus.Unavailable, "WindowsOcrUnavailable",
                    MessageOrDefault(error, "Windows OCR is unavailable on this system."));
            }
            if (process.ExitCode != 0)
                return Failure(OcrProcessingStatus.Failed, "ProcessFailed", MessageOrDefault(error, $"Windows OCR exited with code {process.ExitCode}."));
            if (output.Length >= maxOutputBytes)
                return Failure(OcrProcessingStatus.Failed, "OutputLimit", "Windows OCR output exceeded the configured limit.");

            try
            {
                return new(OcrProcessingStatus.Completed, WindowsOcrJsonParser.Parse(output), []);
            }
            catch (JsonException exception)
            {
                return Failure(OcrProcessingStatus.Failed, "InvalidOutput", $"Windows OCR returned invalid JSON: {exception.Message}");
            }
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static string DefaultPowerShellExecutable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "powershell.exe";
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    private static string[] NormalizeLanguages(IReadOnlyList<string>? languages) =>
        (languages is null || languages.Count == 0 ? ["jpn", "eng"] : languages)
            .SelectMany(language => language.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(language => language.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static async Task<byte[]> ReadImageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var originalPosition = stream.CanSeek ? stream.Position : -1;
        try
        {
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            return memory.ToArray();
        }
        finally
        {
            if (originalPosition >= 0) stream.Position = originalPosition;
        }
    }

    private static string? ResolveExecutable(string configured)
    {
        if (Path.IsPathRooted(configured)) return File.Exists(configured) ? configured : null;
        if (configured.Contains(Path.DirectorySeparatorChar) || configured.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(Path.GetFullPath(configured)) ? Path.GetFullPath(configured) : null;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, configured);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string ExtensionForMediaType(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/tiff" => ".tif",
        "image/bmp" => ".bmp",
        _ => ".bin",
    };

    private static string MessageOrDefault(string message, string fallback) =>
        string.IsNullOrWhiteSpace(message) ? fallback : message.Trim();

    private static OcrAttemptResult Failure(OcrProcessingStatus status, string code, string message) =>
        new(status, null, [new OcrDiagnostic(code, message, DiagnosticSeverity.Warning)]);

    private static async Task<string> ReadLimitedAsync(StreamReader reader, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var builder = new StringBuilder();
        var count = 0L;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            var take = (int)Math.Min(read, Math.Max(0, maxBytes - count));
            if (take > 0) builder.Append(buffer, 0, take);
            count += read;
            if (count >= maxBytes)
            {
                while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) > 0) { }
                break;
            }
        }
        return builder.ToString();
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}

public static class WindowsOcrJsonParser
{
    private static readonly Regex HyphenSpacing = new(@"\s*-\s*", RegexOptions.CultureInvariant);
    private static readonly Regex NumericOrLatinHyphen = new(
        @"(?<=[0-9０-９A-Za-z])\s*[-‐‑‒–—−ー]\s*(?=[0-9０-９A-Za-z])", RegexOptions.CultureInvariant);
    private static readonly Regex JapanesePunctuationSpacing = new(
        @"\s*([:：、。・／（）［］【】「」『』])\s*", RegexOptions.CultureInvariant);
    private static readonly Regex JapaneseCharacterSpacing = new(
        @"(?<=[一-龯々〆〤ぁ-ゖァ-ヺー])\s+(?=[一-龯々〆〤ぁ-ゖァ-ヺー])", RegexOptions.CultureInvariant);
    private static readonly Regex NumericCharacterSpacing = new(
        @"(?<=[0-9０-９])\s+(?=[0-9０-９])", RegexOptions.CultureInvariant);
    private static readonly Regex NumericUnitSpacing = new(
        @"(?<=[0-9０-９,，.．])\s+(?=[円年月日時分秒%％])", RegexOptions.CultureInvariant);
    private static readonly Regex RepeatedHorizontalSpacing = new(@"[ \t]{2,}", RegexOptions.CultureInvariant);

    public static OcrResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var lines = JsonSerializer.Deserialize<WindowsOcrLine[]>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        var regions = lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .Select(line => new OcrTextRegion(NormalizeText(line.Text), new Geometry("image-pixels", line.X, line.Y, line.Width, line.Height), null))
            .ToArray();
        return new OcrResult(string.Join("\n", regions.Select(region => region.Text)), regions);
    }

    private static string NormalizeText(string text)
    {
        var normalized = HyphenSpacing.Replace(text, "-");
        normalized = NumericOrLatinHyphen.Replace(normalized, "-");
        normalized = JapanesePunctuationSpacing.Replace(normalized, "$1");
        normalized = JapaneseCharacterSpacing.Replace(normalized, string.Empty);
        normalized = NumericCharacterSpacing.Replace(normalized, string.Empty);
        normalized = NumericUnitSpacing.Replace(normalized, string.Empty);
        return RepeatedHorizontalSpacing.Replace(normalized, " ").Trim();
    }

    private sealed record WindowsOcrLine(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("width")] double Width,
        [property: JsonPropertyName("height")] double Height);
}
