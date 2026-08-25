using System.Text.Json;
using DocRedock.Worker;

namespace DocRedock.Tests.Worker;

public sealed class WorkerProtocolTests
{
    [Fact]
    public async Task PingUsesStableRequestIdAndNoLogPayload()
    {
        var response = await WorkerHost.HandleLineAsync("{\"id\":\"req-1\",\"command\":\"ping\"}");
        Assert.True(response.Ok);
        Assert.Equal("req-1", response.Id);
        Assert.Equal("OK", response.Code);
    }

    [Fact]
    public async Task ParsesOcrTsvRegionsAndMetadata()
    {
        const string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n5\t1\t1\t1\t1\t1\t10\t20\t30\t40\t96.5\tHello\n";
        var response = await WorkerHost.HandleLineAsync(JsonSerializer.Serialize(new { id = "ocr-1", command = "parse_ocr_tsv", tsv }));
        Assert.True(response.Ok);
        var result = JsonSerializer.Deserialize<OcrTsvSummary>(JsonSerializer.Serialize(response.Result));
        Assert.NotNull(result);
        Assert.Equal("Hello", result!.Text);
        Assert.Equal(1, result.RegionCount);
        Assert.Equal(96.5, result.AverageConfidence);
    }

    [Fact]
    public async Task RejectsInvalidJsonAndUnsupportedCommandWithStableCodes()
    {
        var malformed = await WorkerHost.HandleLineAsync("not-json");
        Assert.Equal("INVALID_JSON", malformed.Code);
        var unsupported = await WorkerHost.HandleLineAsync("{\"id\":\"x\",\"command\":\"shell\"}");
        Assert.Equal("UNSUPPORTED_COMMAND", unsupported.Code);
    }

    [Fact]
    public async Task RejectsPathOutsideWorkerRoot()
    {
        var response = await WorkerHost.HandleLineAsync("{\"id\":\"p\",\"command\":\"probe\",\"path\":\"/tmp/not-allowed\"}");
        Assert.False(response.Ok);
        Assert.Equal("PATH_INVALID", response.Code);
    }
}
