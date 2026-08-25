using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Providers.Abstractions.Providers;

namespace DocRedock.Ocr.Tesseract;

/// <summary>
/// Uses Apple's on-device Vision framework through the checked-in Swift
/// helper. Vision is the preferred macOS provider; Tesseract remains the
/// portable fallback when the system provider is unavailable.
/// </summary>
public sealed class VisionOcrEngine : IOcrEngine
{
    private readonly string helperPath;
    private readonly string swiftExecutable;
    private readonly TimeSpan defaultTimeout;
    private readonly long maxOutputBytes;

    public VisionOcrEngine(
        string? helperPath = null,
        string swiftExecutable = "swift",
        TimeSpan? defaultTimeout = null,
        long maxOutputBytes = 1_048_576)
    {
        this.helperPath = Path.GetFullPath(helperPath ?? Path.Combine(AppContext.BaseDirectory, "vision-ocr.swift"));
        this.swiftExecutable = swiftExecutable;
        this.defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(60);
        this.maxOutputBytes = Math.Max(4096, maxOutputBytes);
    }

    public ProviderDescriptor Descriptor { get; } = new(
        "docredock.ocr.vision",
        new Version(1, 0, 0),
        1,
        new HashSet<string>(StringComparer.Ordinal) { "ocr.text", "ocr.jpn", "ocr.eng" },
        "Apple-SDK",
        "system-runtime",
        false);

    public async ValueTask<OcrAttemptResult> RecognizeAsync(OcrInput input, OcrOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Failure(OcrProcessingStatus.Unavailable, "VisionUnavailable", "Apple Vision OCR is available only on macOS.");
        if (input.Image is null || !input.Image.CanRead)
            return Failure(OcrProcessingStatus.Failed, "InputStream", "OcrInput.Image is not readable.");
        if (!File.Exists(helperPath))
            return Failure(OcrProcessingStatus.Unavailable, "HelperUnavailable", $"Vision OCR helper was not found at '{helperPath}'.");

        var swiftPath = ResolveExecutable(swiftExecutable);
        if (swiftPath is null)
            return Failure(OcrProcessingStatus.Unavailable, "ExecutableUnavailable", $"Swift executable '{swiftExecutable}' was not found.");

        var bytes = await ReadImageAsync(input.Image, cancellationToken).ConfigureAwait(false);
        if (options.PixelBudget is { } pixelBudget &&
            (pixelBudget <= 0 || bytes.LongLength > pixelBudget))
            return new(OcrProcessingStatus.SkippedByBudget, null,
                [new OcrDiagnostic("PixelBudgetExceeded", $"OCR input exceeds the configured {pixelBudget}-byte image budget.", DiagnosticSeverity.Warning)]);

        var tempRoot = Path.Combine(Path.GetTempPath(), "docredock-ocr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var inputPath = Path.Combine(tempRoot, "input" + ExtensionForMediaType(input.MediaType));
        try
        {
            await File.WriteAllBytesAsync(inputPath, bytes, cancellationToken).ConfigureAwait(false);
            var psi = new ProcessStartInfo
            {
                FileName = swiftPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(helperPath);
            psi.ArgumentList.Add(inputPath);
            foreach (var language in NormalizeLanguages(options.Languages))
                psi.ArgumentList.Add(language);

            var timeoutValue = options.Timeout ?? defaultTimeout;
            if (timeoutValue <= TimeSpan.Zero)
                return Failure(OcrProcessingStatus.Failed, "InvalidTimeout", "OCR timeout must be positive.");
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start()) return Failure(OcrProcessingStatus.Failed, "ProcessStart", "Vision OCR process could not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutValue);
            using var killRegistration = timeout.Token.Register(() => TryKill(process));
            var stdoutTask = ReadLimitedAsync(process.StandardOutput, maxOutputBytes, CancellationToken.None);
            var stderrTask = ReadLimitedAsync(process.StandardError, maxOutputBytes, CancellationToken.None);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                return Failure(OcrProcessingStatus.Failed, "Timeout", "Vision OCR timed out.");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
            var output = await stdoutTask.ConfigureAwait(false);
            var error = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                return Failure(OcrProcessingStatus.Failed, "ProcessFailed", error.Length == 0 ? $"Vision OCR exited with code {process.ExitCode}." : error);
            if (output.Length >= maxOutputBytes)
                return Failure(OcrProcessingStatus.Failed, "OutputLimit", "Vision OCR output exceeded the configured limit.");
            try
            {
                var lines = JsonSerializer.Deserialize<VisionLine[]>(output, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                var regions = lines
                    .Where(line => !string.IsNullOrWhiteSpace(line.Text) && line.Confidence >= 0)
                    .Select(line => new OcrTextRegion(line.Text, new Geometry(
                        "vision-normalized-bottom-left", line.X, line.Y, line.Width, line.Height),
                        Math.Clamp(line.Confidence, 0, 1)))
                    .ToArray();
                return new(OcrProcessingStatus.Completed, new OcrResult(string.Join("\n", regions.Select(r => r.Text)), regions), []);
            }
            catch (JsonException exception)
            {
                return Failure(OcrProcessingStatus.Failed, "InvalidOutput", $"Vision OCR returned invalid JSON: {exception.Message}");
            }
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private sealed record VisionLine(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("confidence")] double Confidence,
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("width")] double Width,
        [property: JsonPropertyName("height")] double Height);

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

    private static OcrAttemptResult Failure(OcrProcessingStatus status, string code, string message) =>
        new(status, null, [new OcrDiagnostic(code, message, DiagnosticSeverity.Warning)]);

    private static async Task<string> ReadLimitedAsync(StreamReader reader, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var builder = new System.Text.StringBuilder();
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

/// <summary>Tries the preferred local OCR engine, then a fallback only when it is unavailable.</summary>
public sealed class FallbackOcrEngine : IOcrEngine
{
    private readonly IOcrEngine primary;
    private readonly IOcrEngine fallback;

    public FallbackOcrEngine(IOcrEngine primary, IOcrEngine fallback)
    {
        this.primary = primary;
        this.fallback = fallback;
        Descriptor = new ProviderDescriptor(
            "docredock.ocr.local",
            new Version(1, 1, 0),
            1,
            new HashSet<string>(primary.Descriptor.Capabilities.Concat(fallback.Descriptor.Capabilities), StringComparer.Ordinal),
            $"{primary.Descriptor.LicenseExpression} AND {fallback.Descriptor.LicenseExpression}",
            "system-runtime",
            true);
    }

    public ProviderDescriptor Descriptor { get; }

    public async ValueTask<OcrAttemptResult> RecognizeAsync(OcrInput input, OcrOptions options, CancellationToken cancellationToken)
    {
        var result = await primary.RecognizeAsync(input, options, cancellationToken).ConfigureAwait(false);
        if (result.Status != OcrProcessingStatus.Unavailable) return result;

        var fallbackResult = await fallback.RecognizeAsync(input, options, cancellationToken).ConfigureAwait(false);
        if (fallbackResult.Status == OcrProcessingStatus.Completed)
        {
            return fallbackResult with
            {
                Diagnostics =
                [
                    .. fallbackResult.Diagnostics,
                    new OcrDiagnostic(
                        "OcrFallbackUsed",
                        $"OCR provider '{primary.Descriptor.ProviderId}' was unavailable; used '{fallback.Descriptor.ProviderId}'.",
                        DiagnosticSeverity.Information),
                ],
            };
        }

        return fallbackResult with { Diagnostics = [.. result.Diagnostics, .. fallbackResult.Diagnostics] };
    }
}

public static class OcrEngineFactory
{
    public static IOcrEngine CreateDefault()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new FallbackOcrEngine(new VisionOcrEngine(), new TesseractOcrEngine());
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new FallbackOcrEngine(new WindowsOcrEngine(), new TesseractOcrEngine());
        return new TesseractOcrEngine();
    }
}
