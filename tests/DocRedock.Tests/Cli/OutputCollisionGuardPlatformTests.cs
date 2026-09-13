using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DocRedock.Cli;

namespace DocRedock.Tests.Cli;

/// <summary>
/// Platform-specific regression coverage for the "never overwrite the original" guard
/// (<see cref="OutputCollisionGuard"/>/<see cref="FileIdentity"/>). <see cref="CliApplicationTests"/>
/// already covers the platform-independent cases (same path, relative path, symlink, hard link) on
/// every OS this suite runs on. This file adds the cases that only exist, or only mean something
/// different, on Windows or macOS: case-only file-name differences, Windows junctions/8.3 short
/// names/path spellings, macOS's /tmp symlink and Data-volume firmlink, and Unicode NFC/NFD name
/// normalization. Every assertion whose outcome depends on actual file-system behavior (rather than
/// on <see cref="OutputCollisionGuard"/>'s own fixed, OS-based case-sensitivity rule) is derived from
/// a runtime probe against a real temp file, not from an assumption about the OS.
/// </summary>
public sealed class OutputCollisionGuardPlatformTests
{
    // ------------------------------------------------------------------------------------------
    // 1. Case-only difference
    // ------------------------------------------------------------------------------------------
    // OutputCollisionGuard's PathComparer is intentionally chosen from the OS alone (Windows/macOS
    // => case-insensitive, Linux => case-sensitive; see OutputCollisionGuard.PathComparer) rather
    // than probed from the volume, so that decision is asserted directly against the OS. The probe
    // below exists to catch the (currently unseen on hosted CI, but possible on a self-hosted
    // runner with an unusual mount) case where the real volume disagrees with that OS assumption --
    // in which case we skip rather than assert an outcome that would only be correct for the
    // assumption, not for this actual volume.

    [Fact]
    public void EnsureNoCollision_treats_a_case_only_file_name_difference_as_colliding_only_where_the_volume_is_case_insensitive()
    {
        using var fixture = new TempFolder();
        var inputPath = Path.Combine(fixture.Root, "Report.DOCX");
        File.WriteAllText(inputPath, "content");
        if (!ProbeAgreesWithOsCaseAssumption(fixture.Root, out var caseInsensitive)) return;

        var outputPath = Path.Combine(fixture.Root, "report.docx");
        if (caseInsensitive)
        {
            var exception = Assert.Throws<OutputCollidesWithInputException>(
                () => OutputCollisionGuard.EnsureNoCollision([outputPath], [inputPath]));
            Assert.Contains("Output path must differ from the input path", exception.Message, StringComparison.Ordinal);
        }
        else
        {
            OutputCollisionGuard.EnsureNoCollision([outputPath], [inputPath]); // must not throw
        }
    }

    [Fact]
    public void EnsureNoCollision_treats_a_case_only_assets_directory_difference_the_same_way()
    {
        // Mirrors the file case above for the ".assets" sidecar directory an export leaves next to
        // its Markdown output: a directory-shaped candidate must collide under the same rule as a
        // file-shaped one.
        using var fixture = new TempFolder();
        var inputAssets = Path.Combine(fixture.Root, "Report.assets");
        Directory.CreateDirectory(inputAssets);
        if (!ProbeAgreesWithOsCaseAssumption(fixture.Root, out var caseInsensitive)) return;

        var outputAssets = Path.Combine(fixture.Root, "report.assets");
        if (caseInsensitive)
        {
            var exception = Assert.Throws<OutputCollidesWithInputException>(
                () => OutputCollisionGuard.EnsureNoCollision([outputAssets], [inputAssets]));
            Assert.Contains("Output path must differ from the input path", exception.Message, StringComparison.Ordinal);
        }
        else
        {
            OutputCollisionGuard.EnsureNoCollision([outputAssets], [inputAssets]); // must not throw
        }
    }

    [Fact]
    public async Task Export_refuses_a_case_only_output_path_difference_only_where_the_volume_is_case_insensitive()
    {
        using var fixture = new TempFolder();
        var sourcePath = Path.Combine(fixture.Root, "Report.DOCX");
        CreateMinimalDocx(sourcePath);
        if (!ProbeAgreesWithOsCaseAssumption(fixture.Root, out var caseInsensitive)) return;

        var outputPath = Path.Combine(fixture.Root, "report.docx");
        var stderr = new StringWriter();
        var app = new CliApplication(new StringWriter(), stderr);

        var result = await app.RunAsync(["export", sourcePath, "--output", outputPath, "--profile", "readable", "--force"]);

        if (caseInsensitive)
        {
            Assert.Equal((int)ExitCode.InvalidInput, result);
            Assert.Contains("Output path must differ from the input path", stderr.ToString(), StringComparison.Ordinal);
            // The ".assets" sidecar directory the refused write would have produced must never
            // appear either -- the refusal happens before any output (Markdown or assets) is staged.
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "report.assets")));
        }
        else
        {
            Assert.InRange(result, (int)ExitCode.Success, (int)ExitCode.SuccessWithWarnings);
            Assert.True(File.Exists(outputPath));
        }
    }

    // ------------------------------------------------------------------------------------------
    // 2. Windows directory junction
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Windows_junction_to_the_inputs_directory_collides_through_the_file_and_through_the_junction_itself()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new TempFolder();
        var inputDirectory = Path.Combine(fixture.Root, "input");
        Directory.CreateDirectory(inputDirectory);
        var inputPath = Path.Combine(inputDirectory, "Report.docx");
        CreateMinimalDocx(inputPath);
        var junctionDirectory = Path.Combine(fixture.Root, "via-junction");

        // mklink /J needs no elevation, unlike a symbolic link, which is why it (not
        // File.CreateSymbolicLink) is used to reach the input through a second directory path.
        var mklink = new ProcessStartInfo("cmd.exe", $"""/c mklink /J "{junctionDirectory}" "{inputDirectory}" """)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using (var process = Process.Start(mklink)!)
        {
            process.WaitForExit();
            if (process.ExitCode != 0) return; // junctions unsupported on this volume; nothing to assert.
        }

        var pathThroughJunction = Path.Combine(junctionDirectory, "Report.docx");
        var fileCollision = Assert.Throws<OutputCollidesWithInputException>(
            () => OutputCollisionGuard.EnsureNoCollision([pathThroughJunction], [inputPath]));
        Assert.Contains("Output path must differ from the input path", fileCollision.Message, StringComparison.Ordinal);

        // The junction directory is a second path to the input's own parent directory: a
        // directory-shaped output (an unpack destination) landing there would back up and delete
        // the whole directory during cleanup, exactly like the same case reached without a junction.
        var parentDirectoryCollision = Assert.Throws<OutputCollidesWithInputException>(
            () => OutputCollisionGuard.EnsureNoCollision([junctionDirectory], [inputPath]));
        Assert.Contains("Output path must differ from the input path", parentDirectoryCollision.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------
    // 3. Windows path spelling variants
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Windows_path_spelling_variants_of_the_input_all_collide()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new TempFolder();
        var inputPath = Path.Combine(fixture.Root, "Report.docx");
        CreateMinimalDocx(inputPath);

        var variants = new (string Name, string Path)[]
        {
            ("forward slashes", inputPath.Replace('\\', '/')),
            ("mixed separators", fixture.Root.Replace('\\', '/') + "\\Report.docx"),
            ("trailing separator", inputPath + "\\"),
            ("\\\\?\\ long-path prefix", @"\\?\" + inputPath),
        };

        foreach (var (name, variant) in variants)
        {
            var exception = Assert.Throws<OutputCollidesWithInputException>(
                () => OutputCollisionGuard.EnsureNoCollision([variant], [inputPath]));
            Assert.True(exception.Message.Contains("Output path must differ from the input path", StringComparison.Ordinal),
                $"Variant '{name}' ({variant}) was not recognized as colliding with the input.");
        }
    }

    // ------------------------------------------------------------------------------------------
    // 4. Windows 8.3 short name
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Windows_short_name_alias_of_the_input_collides()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new TempFolder();
        var longPath = Path.Combine(fixture.Root, "docredock-collision-guard-long-input-name.docx");
        CreateMinimalDocx(longPath);

        var shortPath = GetShortPathNameOrNull(longPath);
        if (shortPath is null || string.Equals(shortPath, longPath, StringComparison.OrdinalIgnoreCase))
            return; // 8.3 short names are disabled on this volume; nothing to assert.

        // Windows may expand the short name during canonicalization, or identify it
        // by file ID. Both routes must reject the alias before any write.
        var exception = Assert.Throws<OutputCollidesWithInputException>(
            () => OutputCollisionGuard.EnsureNoCollision([shortPath], [longPath]));
        Assert.Contains("Output path must differ from the input path", exception.Message, StringComparison.Ordinal);
        Assert.Equal(File.ReadAllBytes(longPath), File.ReadAllBytes(shortPath));
    }

    [SupportedOSPlatform("windows")]
    private static string? GetShortPathNameOrNull(string longPath)
    {
        var buffer = new StringBuilder(1024);
        var length = GetShortPathName(longPath, buffer, (uint)buffer.Capacity);
        return length == 0 ? null : buffer.ToString(0, (int)length);
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

    // ------------------------------------------------------------------------------------------
    // 5. macOS /tmp symlink and Data-volume firmlink
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void MacOS_tmp_symlink_resolves_to_the_same_file_as_private_tmp()
    {
        if (!OperatingSystem.IsMacOS()) return;
        // /tmp is a symlink to /private/tmp on every stock macOS install; this is a stable OS fact,
        // not something that needs a runtime probe. File.ResolveLinkTarget only resolves a path's
        // OWN reparse point, not a symlinked ancestor directory, so this collision can only be seen
        // via FileIdentity's stat()-based comparison, which the kernel resolves transitively.
        var relative = Path.Combine("docredock-collision-guard-tests", Guid.NewGuid().ToString("N"));
        var viaPrivateTmp = Path.Combine("/private/tmp", relative, "Report.docx");
        Directory.CreateDirectory(Path.GetDirectoryName(viaPrivateTmp)!);
        try
        {
            CreateMinimalDocx(viaPrivateTmp);
            var viaTmp = Path.Combine("/tmp", relative, "Report.docx");

            var exception = Assert.Throws<OutputCollidesWithInputException>(
                () => OutputCollisionGuard.EnsureNoCollision([viaTmp], [viaPrivateTmp]));
            Assert.Contains("Output path must differ from the input path", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(Path.Combine("/private/tmp", relative), recursive: true);
        }
    }

    [Fact]
    public void MacOS_data_volume_firmlink_path_resolves_to_the_same_file()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new TempFolder();
        var inputPath = Path.Combine(fixture.Root, "Report.docx");
        CreateMinimalDocx(inputPath);
        var firmlinkPath = Path.Combine("/System/Volumes/Data", inputPath.TrimStart('/'));
        if (!File.Exists(firmlinkPath)) return; // this macOS layout has no such firmlink; nothing to assert.

        var exception = Assert.Throws<OutputCollidesWithInputException>(
            () => OutputCollisionGuard.EnsureNoCollision([firmlinkPath], [inputPath]));
        Assert.Contains("Output path must differ from the input path", exception.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------
    // 6. macOS/Linux hard link and symlink chain
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Hard_link_to_the_input_collides()
    {
        using var fixture = new TempFolder();
        var inputPath = Path.Combine(fixture.Root, "Report.docx");
        File.WriteAllText(inputPath, "content");
        var hardLink = Path.Combine(fixture.Root, "Report-hardlink.docx");
        if (!HardLinkTestHelper.TryCreate(inputPath, hardLink)) return; // unsupported file system

        var exception = Assert.Throws<OutputCollidesWithInputException>(
            () => OutputCollisionGuard.EnsureNoCollision([hardLink], [inputPath]));
        Assert.Contains("Output path must differ from the input path", exception.Message, StringComparison.Ordinal);
        Assert.Contains("hard link", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Symlink_chain_to_the_input_collides()
    {
        // Symlink creation needs Developer Mode/elevation on Windows; that combination is already
        // exercised (and skipped when unavailable) by FileIdentityTests.Symlink_resolves_to_the_same_identity_as_its_target.
        // This test targets the platforms where an unprivileged symlink chain is always available.
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new TempFolder();
        var inputPath = Path.Combine(fixture.Root, "Report.docx");
        File.WriteAllText(inputPath, "content");
        var firstLink = Path.Combine(fixture.Root, "link-1.docx");
        var secondLink = Path.Combine(fixture.Root, "link-2.docx");
        File.CreateSymbolicLink(firstLink, inputPath);
        File.CreateSymbolicLink(secondLink, firstLink);

        var exception = Assert.Throws<OutputCollidesWithInputException>(
            () => OutputCollisionGuard.EnsureNoCollision([secondLink], [inputPath]));
        Assert.Contains("Output path must differ from the input path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Symlink_to_a_different_file_with_the_same_name_in_another_directory_does_not_collide()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new TempFolder();
        var inputDirectory = Path.Combine(fixture.Root, "input");
        var otherDirectory = Path.Combine(fixture.Root, "other");
        Directory.CreateDirectory(inputDirectory);
        Directory.CreateDirectory(otherDirectory);
        var inputPath = Path.Combine(inputDirectory, "Report.docx");
        var otherPath = Path.Combine(otherDirectory, "Report.docx");
        File.WriteAllText(inputPath, "input content");
        File.WriteAllText(otherPath, "unrelated content");
        var symlinkToOther = Path.Combine(fixture.Root, "Report.docx");
        File.CreateSymbolicLink(symlinkToOther, otherPath);

        OutputCollisionGuard.EnsureNoCollision([symlinkToOther], [inputPath]); // must not throw
    }

    // ------------------------------------------------------------------------------------------
    // 7. Unicode NFC/NFD normalization
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void EnsureNoCollision_treats_NFC_and_NFD_forms_of_the_same_name_as_colliding_only_where_the_volume_normalizes_them()
    {
        // Finding, confirmed by the probe below: on macOS/APFS, the precomposed (NFC) and
        // decomposed (NFD) byte sequences for the same visible name resolve to one directory entry
        // -- File.Exists(nfdPath) is true even though only the NFC path was ever created -- so this
        // is caught by OutputCollisionGuard's FileIdentity fallback (both paths stat() to the same
        // device/inode), not by its literal-path comparer (NFC and NFD are different strings, so
        // that branch never matches on any OS). On Linux/ext4, file names are raw byte sequences
        // with no normalization, so the NFD path names a file that was never created and no
        // collision is reported.
        using var fixture = new TempFolder();
        var nfcName = "docredock-caf\u00e9-input.docx"; // precomposed "café" (U+00E9)
        var inputPath = Path.Combine(fixture.Root, nfcName);
        File.WriteAllText(inputPath, "content");
        var nfdName = nfcName.Normalize(NormalizationForm.FormD); // decomposed "cafe" + combining acute accent
        var outputPath = Path.Combine(fixture.Root, nfdName);
        var normalizationInsensitive = File.Exists(outputPath);

        if (normalizationInsensitive)
        {
            var exception = Assert.Throws<OutputCollidesWithInputException>(
                () => OutputCollisionGuard.EnsureNoCollision([outputPath], [inputPath]));
            Assert.Contains("Output path must differ from the input path", exception.Message, StringComparison.Ordinal);
        }
        else
        {
            OutputCollisionGuard.EnsureNoCollision([outputPath], [inputPath]); // must not throw
        }
    }

    // ------------------------------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Creates a probe file under <paramref name="directory"/> and checks, at runtime, whether a
    /// differently-cased path resolves to it. Returns <see langword="false"/> (meaning: skip the
    /// calling test) when that probed fact disagrees with what <see cref="OutputCollisionGuard"/>'s
    /// own OS-based comparer assumes -- an outcome that should never happen on a hosted CI runner,
    /// but would make a hardcoded OS-based expectation wrong on whatever volume produced it.
    /// </summary>
    private static bool ProbeAgreesWithOsCaseAssumption(string directory, out bool caseInsensitive)
    {
        var probePath = Path.Combine(directory, "case-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(probePath, "probe");
        try
        {
            var differentCase = probePath.Any(char.IsUpper) ? probePath.ToLowerInvariant() : probePath.ToUpperInvariant();
            caseInsensitive = File.Exists(differentCase);
        }
        finally
        {
            File.Delete(probePath);
        }

        var osAssumesCaseInsensitive = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        return caseInsensitive == osAssumesCaseInsensitive;
    }

    private static void CreateMinimalDocx(string path)
    {
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml",
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
        WriteEntry(archive, "word/document.xml",
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            "<w:body><w:p><w:r><w:t>Body</w:t></w:r></w:p></w:body></w:document>");
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private sealed class TempFolder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "docredock-collision-guard-platform-tests", Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            foreach (var directory in Directory.GetDirectories(Root))
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(directory, recursive: false);
            Directory.Delete(Root, recursive: true);
        }
    }
}
