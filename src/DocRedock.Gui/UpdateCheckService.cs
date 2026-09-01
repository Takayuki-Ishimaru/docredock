using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace DocRedock.Gui;

public sealed record UpdateInfo(Version CurrentVersion, Version LatestVersion, Uri ReleaseUri);

public enum UpdateCheckStatus
{
    UpdateAvailable,
    UpToDate,
    Failed,
    Disabled,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update = null, string? ErrorMessage = null);

public sealed class UpdateCheckService
{
    private const int MaxResponseBytes = 512 * 1024;
    private static readonly Uri ReleasesEndpoint =
        new("https://api.github.com/repos/Takayuki-Ishimaru/docredock/releases?per_page=10");
    private static readonly HttpClient SharedClient = CreateClient();

    private readonly HttpClient _client;

    public UpdateCheckService()
        : this(SharedClient)
    {
    }

    public UpdateCheckService(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        return CheckAsync(GetCurrentVersion(), cancellationToken);
    }

    public static Version GetCurrentVersion()
    {
        return typeof(UpdateCheckService).Assembly.GetName().Version ?? new Version(0, 0);
    }

    public async Task<UpdateInfo?> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        var result = await CheckDetailedAsync(currentVersion, cancellationToken);
        return result.Status == UpdateCheckStatus.UpdateAvailable ? result.Update : null;
    }

    public async Task<UpdateCheckResult> CheckDetailedAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        if (StringComparer.Ordinal.Equals(Environment.GetEnvironmentVariable("DOCREDOCK_DISABLE_UPDATE_CHECK"), "1"))
            return new UpdateCheckResult(UpdateCheckStatus.Disabled);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesEndpoint);
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(
                new ProductInfoHeaderValue("DocRedock", FormatVersion(currentVersion)));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if ((int)response.StatusCode is 403 or 429)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, ErrorMessage: "GitHubの更新確認が制限されています。後でもう一度お試しください。");
            }

            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, ErrorMessage: "更新情報の応答が大きすぎます。");
            }

            await using var responseStream =
                await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var read = await responseStream.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > MaxResponseBytes)
                {
                    return new UpdateCheckResult(UpdateCheckStatus.Failed,
                        ErrorMessage: "更新情報の応答が大きすぎます。");
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }

            buffer.Position = 0;
            using var document = await JsonDocument.ParseAsync(
                buffer,
                cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, ErrorMessage: "更新情報の形式を確認できませんでした。");
            }

            UpdateInfo? newest = null;
            var rejectedNewerRelease = false;
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object ||
                    (release.TryGetProperty("draft", out var draft) &&
                     draft.ValueKind == JsonValueKind.True) ||
                    !release.TryGetProperty("tag_name", out var tagElement) ||
                    tagElement.ValueKind != JsonValueKind.String ||
                    !TryParseReleaseVersion(tagElement.GetString(), out var releaseVersion) ||
                    releaseVersion.CompareTo(currentVersion) <= 0)
                {
                    continue;
                }

                if (!release.TryGetProperty("html_url", out var urlElement) ||
                    urlElement.ValueKind != JsonValueKind.String ||
                    !TryGetTrustedReleaseUri(urlElement.GetString(), out var releaseUri))
                {
                    rejectedNewerRelease = true;
                    continue;
                }

                if (newest is null ||
                    releaseVersion.CompareTo(newest.LatestVersion) > 0)
                {
                    newest = new UpdateInfo(currentVersion, releaseVersion, releaseUri);
                }
            }

            if (newest is not null)
                return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, newest);
            return rejectedNewerRelease
                ? new UpdateCheckResult(UpdateCheckStatus.Failed,
                    ErrorMessage: "新しいリリースを検出しましたが、安全なリリースページを確認できませんでした。")
                : new UpdateCheckResult(UpdateCheckStatus.UpToDate);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or IOException or
                OperationCanceledException)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                ErrorMessage: exception is OperationCanceledException
                    ? "更新確認がタイムアウトしました。"
                    : "更新情報を取得できませんでした。インターネット接続を確認してください。");
        }
    }

    public static bool TryParseReleaseVersion(string? tagName, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return false;
        }

        var normalized = tagName.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var suffixIndex = normalized.IndexOf('-');
        if (suffixIndex >= 0)
        {
            normalized = normalized[..suffixIndex];
        }

        if (!Version.TryParse(normalized, out var parsedVersion) ||
            parsedVersion is null)
        {
            return false;
        }

        version = parsedVersion;
        return true;
    }

    public static bool TryGetTrustedReleaseUri(string? value, out Uri releaseUri)
    {
        releaseUri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !candidate.AbsolutePath.StartsWith(
                "/Takayuki-Ishimaru/docredock/releases/",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        releaseUri = candidate;
        return true;
    }

    public static string FormatVersion(Version version)
    {
        var componentCount = version.Revision > 0 ? 4 : version.Build > 0 ? 3 : 2;
        return version.ToString(componentCount);
    }

    private static HttpClient CreateClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }
}
