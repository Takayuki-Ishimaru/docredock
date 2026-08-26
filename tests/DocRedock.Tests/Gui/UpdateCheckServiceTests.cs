using System.Net;
using System.Text;
using DocRedock.Gui;

namespace DocRedock.Tests.Gui;

public sealed class UpdateCheckServiceTests
{
    [Fact]
    public async Task CheckAsync_ReportsNewestNonDraftReleaseIncludingPrerelease()
    {
        const string releases = """
            [
              {
                "tag_name": "v9.0.0",
                "html_url": "https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v9.0.0",
                "draft": true,
                "prerelease": false
              },
              {
                "tag_name": "v0.2.0-beta.1",
                "html_url": "https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.2.0-beta.1",
                "draft": false,
                "prerelease": true
              },
              {
                "tag_name": "v0.1.2",
                "html_url": "https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.2",
                "draft": false,
                "prerelease": false
              }
            ]
            """;
        var handler = new StubHttpMessageHandler(
            _ => JsonResponse(releases));
        using var client = new HttpClient(handler);
        var service = new UpdateCheckService(client);

        var update = await service.CheckAsync(new Version(0, 1, 1));

        Assert.NotNull(update);
        Assert.Equal(new Version(0, 2, 0), update.LatestVersion);
        Assert.Equal(
            "https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.2.0-beta.1",
            update.ReleaseUri.AbsoluteUri);
        Assert.Equal(
            "https://api.github.com/repos/Takayuki-Ishimaru/docredock/releases?per_page=10",
            handler.RequestUri?.AbsoluteUri);
        Assert.Contains("DocRedock/0.1.1", handler.UserAgent);
    }

    [Fact]
    public async Task CheckAsync_ReturnsNullWhenNoReleaseIsNewer()
    {
        const string releases = """
            [
              {
                "tag_name": "v0.1.1",
                "html_url": "https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.1",
                "draft": false,
                "prerelease": false
              },
              {
                "tag_name": "v0.1.0",
                "html_url": "https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.0",
                "draft": false,
                "prerelease": false
              }
            ]
            """;
        using var client = new HttpClient(
            new StubHttpMessageHandler(_ => JsonResponse(releases)));
        var service = new UpdateCheckService(client);

        var update = await service.CheckAsync(new Version(0, 1, 1));

        Assert.Null(update);
    }

    [Fact]
    public async Task CheckAsync_IgnoresUntrustedReleaseUrl()
    {
        const string releases = """
            [
              {
                "tag_name": "v0.2.0",
                "html_url": "https://example.com/fake-download",
                "draft": false,
                "prerelease": false
              }
            ]
            """;
        using var client = new HttpClient(
            new StubHttpMessageHandler(_ => JsonResponse(releases)));
        var service = new UpdateCheckService(client);

        var update = await service.CheckAsync(new Version(0, 1, 1));

        Assert.Null(update);
    }

    [Fact]
    public async Task CheckAsync_SwallowsNetworkFailure()
    {
        using var client = new HttpClient(
            new StubHttpMessageHandler(
                _ => throw new HttpRequestException("offline")));
        var service = new UpdateCheckService(client);

        var update = await service.CheckAsync(new Version(0, 1, 1));

        Assert.Null(update);
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V1.2.3-beta.1", "1.2.3")]
    [InlineData(" 2.0 ", "2.0")]
    public void TryParseReleaseVersion_ParsesSupportedTags(
        string tagName,
        string expected)
    {
        var parsed = UpdateCheckService.TryParseReleaseVersion(
            tagName,
            out var version);

        Assert.True(parsed);
        Assert.Equal(new Version(expected), version);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string UserAgent { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            UserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(responseFactory(request));
        }
    }
}
