using System.Text.Json;
using System.Text.Json.Nodes;
using DocRedock.Api;
using DocRedock.Cli;

namespace DocRedock.Tests.Api;

public sealed class V034ReviewResponseTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-v034-review-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);
    private static string Fixture(string name) => Path.Combine(Path.GetDirectoryName(V033ReviewResponseTests.Fixture("spec.docx"))!, "..", "V034Review", name);
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData("visible", AiPackageForm.Directory)]
    [InlineData("complete", AiPackageForm.Directory)]
    [InlineData("sanitized", AiPackageForm.Directory)]
    [InlineData("visible", AiPackageForm.Zip)]
    [InlineData("complete", AiPackageForm.Zip)]
    [InlineData("sanitized", AiPackageForm.Zip)]
    public async Task Final_cell_deletion_retains_unaffected_text_and_requires_conversion_review(string policy, AiPackageForm form)
    {
        foreach (var fixture in new[] { "cell_one_cell.docx", "edge-revisions.docx" })
        {
            var source = Fixture(fixture); var original = await File.ReadAllBytesAsync(source);
            var target = Path.Combine(root, fixture + (form == AiPackageForm.Zip ? ".zip" : ".package"));
            var result = await service.ExportAiPackageAsync(new(new(source, "unused.md", ContentPolicy: policy,
                Revisions: "final", EnableOcr: false), target, form));
            var snapshot = await AiPackageManifestReader.ReadVerifiedPackageAsync(target);
            var text = string.Join("\n", snapshot.Parts.Select(part => part.Markdown)).Replace("\\", "");
            Assert.Contains("KEPT_CELL", text); Assert.DoesNotContain("DELETED_CELL", text);
            Assert.Contains("Table columns could not be resolved", text);
            if (fixture == "cell_one_cell.docx")
            {
                Assert.Contains("BEFORE_TABLE", text); Assert.Contains("AFTER_TABLE", text);
                Assert.Contains("Delete column", text); Assert.Contains("Keep column", text);
                Assert.DoesNotContain("| KEPT_CELL", text); // No guessed column/header assignment.
            }
            else
            {
                foreach (var kept in new[] { "KEPT_BODY", "KEPT_MOVE", "KEPT_ROW", "KEPT_TEXTBOX" }) Assert.Contains(kept, text);
                foreach (var deleted in new[] { "DELETED_BODY", "DELETED_MOVE", "DELETED_ROW", "DELETED_TEXTBOX" }) Assert.DoesNotContain(deleted, text);
            }
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "DocxFinalTableGridUnresolved");
            Assert.True(result.Review.Required); Assert.True(snapshot.Review.ConversionReviewRequired);
            Assert.Equal(StructureBasis.Unresolved, result.Summary.TableStructure);
            Assert.False(snapshot.Review.RevisionSharingRequired);
            Assert.Contains("残存セルの文字を行順", snapshot.ReviewMarkdown);
            Assert.Equal(original, await File.ReadAllBytesAsync(source));
            var stdout = new StringWriter(); var stderr = new StringWriter();
            var cli = new CliApplication(stdout, stderr, service);
            Assert.Equal(11, await cli.RunAsync(["package", "read", target, "--part", snapshot.Parts[0].Part.Id]));
            Assert.Empty(stdout.ToString()); Assert.Contains("package review", stderr.ToString());
            Assert.Equal(0, await cli.RunAsync(["package", "read", target, "--part", snapshot.Parts[0].Part.Id, "--allow-review"]));
            Assert.Contains("KEPT_CELL", stdout.ToString().Replace("\\", ""));
        }
    }

    [Theory]
    [InlineData("cell_control.docx", false)]
    [InlineData("cell_whole_column.docx", true)]
    public async Task Rectangular_control_and_whole_column_deletion_keep_a_table_without_new_review(string fixture, bool deleted)
    {
        var target = Path.Combine(root, "control");
        var result = await service.ExportAiPackageAsync(new(new(Fixture(fixture), "unused.md", Revisions: "final", EnableOcr: false), target));
        var snapshot = await AiPackageManifestReader.ReadVerifiedPackageAsync(target);
        var text = string.Join("\n", snapshot.Parts.Select(part => part.Markdown)).Replace("\\", "");
        Assert.Contains("| KEPT_CELL |", text);
        Assert.Equal(!deleted, text.Contains("DELETED_CELL", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "DocxFinalTableGridUnresolved");
        Assert.False(snapshot.Review.Required);
    }

    [Theory]
    [InlineData(AiPackageForm.Directory)]
    [InlineData(AiPackageForm.Zip)]
    public async Task Package_review_is_verified_metadata_and_hidden_review_is_opt_in(AiPackageForm form)
    {
        var target = Path.Combine(root, form == AiPackageForm.Zip ? "complete.zip" : "complete");
        var exported = await service.ExportAiPackageAsync(new(new(V033ReviewResponseTests.Fixture("sales.xlsx"), "unused.md",
            ContentPolicy: "complete", EnableOcr: false), target, form));
        var stdout = new StringWriter(); var stderr = new StringWriter(); var cli = new CliApplication(stdout, stderr, service);
        Assert.Equal(0, await cli.RunAsync(["package", "search", target, "SECRET_SHEET"]));
        Assert.Contains("SECRET_SHEET", stdout.ToString().Replace("\\", ""));
        foreach (var args in new[] {
            new[] { "package", "read", target, "--part", exported.Parts[0].Id, "--require-hidden-review", "--json" },
            new[] { "package", "search", target, "SECRET_SHEET", "--require-hidden-review" } })
        {
            stdout.GetStringBuilder().Clear();
            Assert.Equal(11, await cli.RunAsync(args)); Assert.Empty(stdout.ToString());
        }
        stdout.GetStringBuilder().Clear();
        Assert.Equal(0, await cli.RunAsync(["package", "review", target, "--json", "--require-hidden-review"]));
        using var json = JsonDocument.Parse(stdout.ToString()); var review = json.RootElement;
        Assert.True(review.GetProperty("verified").GetBoolean()); Assert.True(review.GetProperty("review_required").GetBoolean());
        Assert.False(review.GetProperty("conversion_review").GetProperty("required").GetBoolean());
        Assert.False(review.GetProperty("revision_sharing").GetProperty("required").GetBoolean());
        Assert.True(review.GetProperty("hidden_content").GetProperty("included").GetBoolean());
        Assert.Equal("hidden_content", Assert.Single(review.GetProperty("stop_reasons").EnumerateArray()).GetString());
        Assert.Equal(exported.Parts.Count, review.GetProperty("affected_parts").GetArrayLength());
        Assert.Equal("package", review.GetProperty("affected_parts_scope").GetString());
        Assert.False(review.TryGetProperty("markdown", out _)); Assert.False(review.TryGetProperty("matches", out _));
        Assert.DoesNotContain("INTERNAL_ONLY", stdout.ToString());
        stdout.GetStringBuilder().Clear();
        Assert.Equal(0, await cli.RunAsync(["package", "review", target]));
        Assert.Equal((await AiPackageManifestReader.ReadVerifiedPackageAsync(target)).ReviewMarkdown, stdout.ToString());
        stdout.GetStringBuilder().Clear();
        Assert.Equal(11, await cli.RunAsync(["package", "review", target, "--json", "--require-hidden-review", "--fail-on-review"]));
        stdout.GetStringBuilder().Clear();
        Assert.Equal(0, await cli.RunAsync(["package", "search", target, "SECRET_SHEET", "--require-hidden-review", "--allow-review"]));
        Assert.Contains("SECRET_SHEET", stdout.ToString().Replace("\\", ""));
    }

    [Fact]
    public async Task Review_distinguishes_conversion_from_revisions_and_refuses_tampered_or_cancelled_packages()
    {
        var target = Path.Combine(root, "revisions");
        await service.ExportAiPackageAsync(new(new(V033ReviewResponseTests.Fixture("spec.docx"), "unused.md"), target));
        var stdout = new StringWriter(); var cli = new CliApplication(stdout, new StringWriter(), service);
        Assert.Equal(0, await cli.RunAsync(["package", "review", target, "--json"]));
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.True(json.RootElement.GetProperty("revision_sharing").GetProperty("required").GetBoolean());
        Assert.False(json.RootElement.GetProperty("conversion_review").GetProperty("required").GetBoolean());
        Assert.Contains("revision_sharing", stdout.ToString()); Assert.DoesNotContain("DELETED_REVIEW_SECRET", stdout.ToString());
        stdout.GetStringBuilder().Clear();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Equal(2, await cli.RunAsync(["package", "review", target, "--json"], cancelled.Token));
        Assert.Empty(stdout.ToString());
        File.AppendAllText(Path.Combine(target, "parts", "0001.md"), "TAMPERED");
        Assert.Equal(7, await cli.RunAsync(["package", "review", target, "--json", "--allow-review"]));
        Assert.Empty(stdout.ToString());
    }

    [Fact]
    public async Task Missing_legacy_hidden_flag_in_complete_package_is_conservatively_gated()
    {
        var target = Path.Combine(root, "legacy-complete");
        await service.ExportAiPackageAsync(new(new(V033ReviewResponseTests.Fixture("sales.xlsx"), "unused.md", ContentPolicy: "complete"), target));
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "report.json")))!;
        report["summary"]!.AsObject().Remove("hidden_content_included");
        V033ReviewResponseTests.Relist(target, "report.json", report.ToJsonString());
        var snapshot = await AiPackageManifestReader.ReadVerifiedPackageAsync(target);
        Assert.Null(snapshot.Review.HiddenContentIncluded);
        Assert.Equal(new[] { "hidden_content" }, snapshot.Review.StopReasons(true, snapshot.Manifest.ContentPolicy));
        Assert.Empty(snapshot.Review.StopReasons(false, snapshot.Manifest.ContentPolicy));
    }
}
