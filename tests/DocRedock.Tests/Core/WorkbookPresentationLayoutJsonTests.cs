using System.Text.Json;
using DocRedock.Core.Documents;

namespace DocRedock.Tests.Core;

public sealed class WorkbookPresentationLayoutJsonTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":null,\"sheets\":{}}")]
    [InlineData("{\"version\":\"1\",\"sheets\":{}}")]
    [InlineData("{\"version\":true,\"sheets\":{}}")]
    [InlineData("{\"version\":1.5,\"sheets\":{}}")]
    [InlineData("{\"version\":2147483648,\"sheets\":{}}")]
    [InlineData("{\"version\":1e100,\"sheets\":{}}")]
    [InlineData("{\"version\":-1,\"sheets\":{}}")]
    [InlineData("{\"version\":0,\"sheets\":{}}")]
    [InlineData("{\"version\":2,\"sheets\":{}}")]
    public void Unsupported_or_malformed_versions_are_rejected_without_throwing(string json)
    {
        using var document = JsonDocument.Parse(json);
        var diagnostics = new List<string>();

        Assert.False(WorkbookPresentationLayoutJson.TryRead(
            document.RootElement, out var layout, diagnostics));
        Assert.Null(layout);
        Assert.InRange(diagnostics.Count, 1, WorkbookPresentationLayoutJson.MaxDiagnostics);
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Contains("version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Current_version_has_a_valid_empty_layout()
    {
        using var document = JsonDocument.Parse("{\"version\":1,\"sheets\":{}}");
        var diagnostics = new List<string>();

        Assert.True(WorkbookPresentationLayoutJson.TryRead(
            document.RootElement, out var layout, diagnostics));
        Assert.NotNull(layout);
        Assert.Equal(WorkbookPresentationLayoutJson.CurrentVersion, layout.Version);
        Assert.Empty(layout.Sheets);
        Assert.Empty(diagnostics);
    }
}
