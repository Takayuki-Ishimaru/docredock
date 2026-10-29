using Xunit;

namespace DocRedock.Gui.HeadlessTests;

public sealed class RevisionDefaultsDocumentationTests
{
    [Fact]
    public void Localized_guides_link_to_the_canonical_policy_defaults_without_stale_cli_claims()
    {
        var root = FindRepositoryRoot();
        var defaults = File.ReadAllText(Path.Combine(root, "docs", "reference", "revision-defaults.md"));
        Assert.Contains("| `visible` | `markup` | `markup` | `markup` |", defaults, StringComparison.Ordinal);
        Assert.Contains("| `complete` | `markup` | `markup` | `markup` |", defaults, StringComparison.Ordinal);
        Assert.Contains("| `sanitized` | `final` | `final` | `final` |", defaults, StringComparison.Ordinal);

        foreach (var path in new[]
        {
            Path.Combine(root, "docs", "ja", "user-guide.md"),
            Path.Combine(root, "docs", "en", "user-guide.md"),
            Path.Combine(root, "docs", "ja", "supported-features.md"),
            Path.Combine(root, "docs", "en", "supported-features.md"),
        })
        {
            var text = File.ReadAllText(path);
            Assert.Contains("revision-defaults.md", text, StringComparison.Ordinal);
            Assert.DoesNotContain("CLIはポリシーによらず`final`が既定", text, StringComparison.Ordinal);
            Assert.DoesNotContain("CLI defaults to `final` for every content policy", text, StringComparison.Ordinal);
            Assert.DoesNotContain("CLIは全ポリシーで`final`が既定", text, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "docs", "reference", "revision-defaults.md")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the documentation repository root.");
    }
}
