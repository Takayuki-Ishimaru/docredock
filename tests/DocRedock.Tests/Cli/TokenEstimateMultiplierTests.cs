using System.Globalization;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Cli;

namespace DocRedock.Tests.Cli;

public sealed class TokenEstimateMultiplierTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-cli-token-margin-").FullName;
    public void Dispose() => Directory.Delete(root, recursive: true);
    private static CliApplication App(StringWriter output) => new(output, new StringWriter(),
        new DocumentService(null, null, discoverPdfRasterizer: false));

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.5")]
    [InlineData("101")]
    [InlineData("invalid")]
    [InlineData("1,5")]
    [InlineData(null)]
    public async Task Invalid_or_missing_value_does_not_create_output(string? margin)
    {
        var source = DocRedock.Tests.Api.TokenEstimateMultiplierTests.WriteSource(root);
        var target = Path.Combine(root, "not-created", "package");
        var args = new List<string> { "export", source, "--ai-package", "dir", "--output", target, "--token-estimate-multiplier" };
        if (margin is not null) args.Add(margin);
        Assert.Equal(2, await App(new()).RunAsync(args.ToArray()));
        Assert.False(Directory.Exists(Path.GetDirectoryName(target)));
    }

    [Fact]
    public async Task Margin_requires_package_and_force_preserves_existing_output_on_invalid_value()
    {
        var source = DocRedock.Tests.Api.TokenEstimateMultiplierTests.WriteSource(root);
        var target = Path.Combine(root, "keep.md");
        await File.WriteAllTextAsync(target, "keep");
        var app = App(new());
        Assert.Equal(2, await app.RunAsync(["export", source, "--output", target, "--force", "--token-estimate-multiplier", "2"]));
        Assert.Equal("keep", await File.ReadAllTextAsync(target));
        Assert.Equal(2, await app.RunAsync(["export", source, "--ai-package", "zip", "--output", target, "--force", "--token-estimate-multiplier", "NaN"]));
        Assert.Equal("keep", await File.ReadAllTextAsync(target));
    }

    [Theory]
    [InlineData("dir", AiPackageForm.Directory)]
    [InlineData("zip", AiPackageForm.Zip)]
    public async Task Valid_margin_uses_invariant_culture_and_reaches_every_manifest_part(string kind, AiPackageForm form)
    {
        var source = DocRedock.Tests.Api.TokenEstimateMultiplierTests.WriteSource(root);
        var target = Path.Combine(root, kind == "zip" ? "output.zip" : "output");
        var priorCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var stdout = new StringWriter();
            Assert.Equal(0, await App(stdout).RunAsync(["export", source, "--ai-package", kind, "--output", target,
                "--ocr", "off", "--chunk-chars", "512", "--token-estimate-multiplier", "1.5"]));
            var files = DocRedock.Tests.Api.AiPackageExportTests.ReadPackage(target, form);
            using var manifest = JsonDocument.Parse(files["manifest.json"]);
            Assert.Equal(1.5, manifest.RootElement.GetProperty("token_estimate_multiplier").GetDouble());
            var parts = manifest.RootElement.GetProperty("parts").EnumerateArray().ToArray();
            Assert.True(parts.Length > 1);
            foreach (var part in parts)
            {
                var text = System.Text.Encoding.UTF8.GetString(files[part.GetProperty("path").GetString()!]);
                Assert.Equal(AiTokenEstimate.Estimate(text, 1.5), part.GetProperty("estimated_tokens").GetInt32());
            }
            Assert.NotNull(await AiPackageManifestReader.ReadVerifiedPackageAsync(target));
        }
        finally { CultureInfo.CurrentCulture = priorCulture; }
    }
}
