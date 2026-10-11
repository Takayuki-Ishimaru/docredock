using System.Text.Json;
using DocRedock.Cli;

namespace DocRedock.Tests.Cli;

public sealed class CliLicenseTests
{
    [Fact]
    public async Task License_listing_exposes_source_data_in_text_and_machine_readable_json()
    {
        var output = new StringWriter(); var error = new StringWriter();
        Assert.Equal(0, await new CliApplication(output, error).RunAsync(["licenses"]));
        Assert.Contains("Adobe-Japan1-UCS2", output.ToString());
        Assert.Contains("BSD-3-Clause", output.ToString());
        Assert.Contains("mapping-resources-pdf", output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await new CliApplication(output, error).RunAsync(["licenses", "--json"]));
        using var data = JsonDocument.Parse(output.ToString());
        var source = Assert.Single(data.RootElement.GetProperty("sourceData").EnumerateArray(),
            item => item.GetProperty("id").GetString() == "Adobe-Japan1-UCS2");
        Assert.Equal("BSD-3-Clause", source.GetProperty("license").GetString());
        Assert.Equal("6a9693361647a37996312cc57071bb79f8c06411207be7c730a83fda1254cd82", source.GetProperty("sha256").GetString());
        Assert.Equal("THIRD-PARTY-NOTICES.txt", source.GetProperty("notice").GetString());
        Assert.Empty(error.ToString());
    }
}
