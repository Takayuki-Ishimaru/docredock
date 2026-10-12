using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocRedock.Api;
using DocRedock.Cli;
using DocRedock.Gui;

namespace DocRedock.Tests.Api;

public sealed class V033ReviewResponseTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-v033-review-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);
    internal static string Fixture(string name) => Path.Combine(Repository(), "tests", "DocRedock.Tests", "Fixtures", "V033Review", name);
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData("visible", null, true)]
    [InlineData("complete", null, true)]
    [InlineData("sanitized", null, false)]
    [InlineData("visible", "final", false)]
    [InlineData("complete", "final", false)]
    [InlineData("sanitized", "markup", true)]
    public async Task Revision_policy_controls_every_package_surface_and_both_reader_gates(string policy, string? revisions, bool included)
    {
        var source = Fixture("spec.docx"); var before = File.ReadAllBytes(source);
        var target = Path.Combine(root, "package");
        var result = await service.ExportAiPackageAsync(new(new(source, "unused.md", ContentPolicy: policy,
            Revisions: revisions, AllowRevisionSharing: true), target));
        var package = await AiPackageManifestReader.ReadVerifiedPackageAsync(target);
        Assert.Equal(included, package.Review.Required);
        Assert.Equal(included, result.Review.Required);
        Assert.Equal(included, result.Review.SharingReview!.RevisionsIncluded);
        Assert.Equal(included, result.Review.SharingReview.DeletedTextIncluded);
        var text = string.Join("\n", Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains("assets")).Select(File.ReadAllText));
        Assert.Equal(included, text.Replace("\\", "").Contains("DELETED_REVIEW_SECRET", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllBytes(source));
        if (AiPackageReaderParityTests.Python() is { } python)
        {
            var script = Path.Combine(Repository(), "docs", "examples", "ai_package_reader.py");
            var start = new System.Diagnostics.ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { script, target, "--verify", "--fail-on-review", "--part", result.Parts[0].Id }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            var stdoutTask = process.StandardOutput.ReadToEndAsync(); var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var stdout = await stdoutTask; var stderr = await stderrTask;
            Assert.True(process.ExitCode == (included ? 3 : 0), stderr);
            if (included) Assert.Empty(stdout);
            else Assert.DoesNotContain("DELETED_REVIEW_SECRET", stdout);
        }
    }

    [Theory]
    [InlineData("reject", "visible", false)]
    [InlineData("markup", "sanitized", false)]
    public async Task Refused_revision_policies_leave_no_output_or_staging(string revisions, string policy, bool allow)
    {
        var target = Path.Combine(root, "refused.zip");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExportAiPackageAsync(new(new(Fixture("spec.docx"), "unused.md",
            ContentPolicy: policy, Revisions: revisions, AllowRevisionSharing: allow), target, AiPackageForm.Zip)));
        Assert.False(File.Exists(target)); Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task Legacy_revision_diagnostic_requires_review_and_new_sharing_fields_are_strict()
    {
        var target = Path.Combine(root, "legacy");
        await service.ExportAiPackageAsync(new(new(Fixture("spec.docx"), "unused.md"), target));
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "report.json")))!.AsObject();
        report.Remove("sharing_review"); report["review"]!["required"] = false;
        Relist(target, "report.json", report.ToJsonString());
        Assert.True((await AiPackageManifestReader.ReadVerifiedPackageAsync(target)).Review.Required);
        report["sharing_review"] = JsonNode.Parse("{\"required\":\"false\",\"revisions_present\":true,\"revisions_included\":false,\"deleted_text_included\":false,\"revisions_policy\":\"final\"}");
        Relist(target, "report.json", report.ToJsonString());
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => AiPackageManifestReader.ReadVerifiedPackageAsync(target));
        Assert.Contains("sharing_review.required", exception.Message);
    }

    [Fact]
    public async Task GUI_preview_and_actual_row_split_expose_effect_and_find_the_original_cell()
    {
        var options = new ReadableDocumentExportOptions(Fixture("sales.xlsx"), "unused.md");
        var whole = await service.PreviewAiPackageAsync(options, new(1500));
        var split = await service.PreviewAiPackageAsync(options, new(1500, true));
        Assert.Equal(1, whole.PartCount); Assert.True(whole.ExceedingParts > 0);
        Assert.True(split.PartCount > 1); Assert.True(split.MaximumPartCharacters < whole.MaximumPartCharacters);
        Assert.Contains("1分割", whole.DescribeJapanese(true));
        var result = await new GuiWorkflowService().ExportAsync(options.SourcePath, root, false, readable: true,
            aiPackage: true, tableRowBlocks: true, targetCharacters: 1500);
        Assert.Equal(split.PartCount, result.PackageSize!.PartCount);
        Assert.Equal(split.MaximumPartCharacters, result.PackageSize.MaximumPartCharacters);
        var verified = await AiPackageManifestReader.ReadVerifiedPackageAsync(result.AiPackagePath!);
        var match = Assert.Single(AiPackageSearch.Find(verified, "ＳＴＯＲＥ＿０２５０"));
        var part = verified.Parts.Single(part => part.Part.Id == match.PartId);
        Assert.Contains(part.Sources, source => source.CellAddresses?.Contains("A255") == true);
        Assert.DoesNotContain("STORE_0050", part.Markdown.Replace("\\", ""));
        Assert.Contains("STORE_0250", part.Markdown.Replace("\\", ""));
    }

    [Fact]
    public async Task CLI_package_search_read_verify_and_review_are_one_operation_with_body_only_by_default()
    {
        var target = Path.Combine(root, "package.zip");
        await service.ExportAiPackageAsync(new(new(Fixture("sales.xlsx"), "unused.md"), target, AiPackageForm.Zip, 1500, true));
        var stdout = new StringWriter(); var stderr = new StringWriter(); var cli = new CliApplication(stdout, stderr, service);
        Assert.Equal(0, await cli.RunAsync(["package", "search", target, "ＳＴＯＲＥ＿０２５０", "--json"]));
        using var found = JsonDocument.Parse(stdout.ToString());
        var id = found.RootElement.GetProperty("matches")[0].GetProperty("part_id").GetString()!;
        stdout.GetStringBuilder().Clear();
        Assert.Equal(0, await cli.RunAsync(["package", "read", target, "--part", id]));
        Assert.Contains("STORE_0250", stdout.ToString().Replace("\\", "")); Assert.DoesNotContain("source_id", stdout.ToString());
        stdout.GetStringBuilder().Clear();
        Assert.Equal(0, await cli.RunAsync(["package", "read", target, "--part", id, "--json", "--with-sources"]));
        Assert.Contains("A255", stdout.ToString());
        var reviewed = Path.Combine(root, "reviewed");
        await service.ExportAiPackageAsync(new(new(Fixture("spec.docx"), "unused.md"), reviewed));
        foreach (var args in new[] { new[] { "package", "read", reviewed, "--part", "part-0001" }, new[] { "package", "search", reviewed, "SECRET", "--json" } })
        {
            stdout.GetStringBuilder().Clear();
            Assert.Equal(11, await cli.RunAsync(args)); Assert.Empty(stdout.ToString());
        }
        stdout.GetStringBuilder().Clear();
        Assert.Equal(0, await cli.RunAsync(["package", "read", reviewed, "--part", "part-0001", "--allow-review"]));
        Assert.Contains("DELETED_REVIEW_SECRET", stdout.ToString().Replace("\\", ""));
        File.AppendAllText(Path.Combine(reviewed, "parts", "0001.md"), "TAMPERED"); stdout.GetStringBuilder().Clear();
        Assert.Equal(7, await cli.RunAsync(["package", "read", reviewed, "--part", "part-0001", "--allow-review"])); Assert.Empty(stdout.ToString());
    }

    [Fact]
    public async Task Export_gate_rejects_history_while_sanitized_default_is_final()
    {
        var cli = new CliApplication(new StringWriter(), new StringWriter(), service);
        Assert.Equal(11, await cli.RunAsync(["export", Fixture("spec.docx"), "--output", Path.Combine(root, "markup.zip"),
            "--ai-package", "zip", "--fail-on-review", "--ocr", "off"]));
        var sanitized = Path.Combine(root, "sanitized.zip");
        Assert.Equal(1, await cli.RunAsync(["export", Fixture("spec.docx"), "--output", sanitized,
            "--ai-package", "zip", "--content-policy", "sanitized", "--fail-on-review", "--ocr", "off"]));
        Assert.False((await AiPackageManifestReader.ReadVerifiedPackageAsync(sanitized)).Review.Required);
    }

    [Fact]
    public async Task Verified_snapshot_survives_later_disk_changes_and_cancellation_never_returns_body()
    {
        var target = Path.Combine(root, "snapshot");
        var exported = await service.ExportAiPackageAsync(new(new(Fixture("sales.xlsx"), "unused.md"), target));
        var snapshot = await AiPackageManifestReader.ReadVerifiedPackageAsync(target);
        File.WriteAllText(Path.Combine(target, exported.Parts[0].Path), "CHANGED");
        Assert.Contains("STORE_0250", snapshot.Parts[0].Markdown.Replace("\\", ""));
        await Assert.ThrowsAsync<AiPackageVerificationException>(() => AiPackageManifestReader.ReadVerifiedPackageAsync(target));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PreviewAiPackageAsync(new(Fixture("sales.xlsx"), "unused.md"), cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AiPackageManifestReader.ReadVerifiedPackageAsync(target, cancellation.Token));
    }

    internal static void Relist(string target, string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text); File.WriteAllBytes(Path.Combine(target, path), bytes);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "manifest.json")))!;
        var file = manifest["files"]!.AsArray().Single(file => file!["path"]!.GetValue<string>() == path)!;
        file["bytes"] = bytes.Length; file["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        File.WriteAllText(Path.Combine(target, "manifest.json"), manifest.ToJsonString());
    }
    private static string Repository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DocRedock.sln"))) return directory.FullName;
        throw new InvalidOperationException("Repository not found.");
    }
}
