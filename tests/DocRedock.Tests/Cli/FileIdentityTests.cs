using DocRedock.Cli;

namespace DocRedock.Tests.Cli;

public sealed class FileIdentityTests
{
    [Fact]
    public void Same_path_has_equal_identity()
    {
        using var fixture = new TempFolder();
        var path = Path.Combine(fixture.Root, "a.txt");
        File.WriteAllText(path, "content");

        Assert.True(FileIdentity.TryGet(path, out var first));
        Assert.True(FileIdentity.TryGet(path, out var second));
        Assert.Equal(first, second);
    }

    [Fact]
    public void Two_separate_files_with_identical_content_have_different_identity()
    {
        using var fixture = new TempFolder();
        var pathA = Path.Combine(fixture.Root, "a.txt");
        var pathB = Path.Combine(fixture.Root, "b.txt");
        File.WriteAllText(pathA, "same content");
        File.WriteAllText(pathB, "same content");

        Assert.True(FileIdentity.TryGet(pathA, out var identityA));
        Assert.True(FileIdentity.TryGet(pathB, out var identityB));
        Assert.NotEqual(identityA, identityB);
    }

    [Fact]
    public void Hard_link_has_equal_identity_to_its_target()
    {
        using var fixture = new TempFolder();
        var original = Path.Combine(fixture.Root, "original.txt");
        var link = Path.Combine(fixture.Root, "link.txt");
        File.WriteAllText(original, "content");
        if (!HardLinkTestHelper.TryCreate(original, link)) return; // unsupported file system; nothing to assert

        Assert.True(FileIdentity.TryGet(original, out var originalIdentity));
        Assert.True(FileIdentity.TryGet(link, out var linkIdentity));
        Assert.Equal(originalIdentity, linkIdentity);
    }

    [Fact]
    public void Symlink_resolves_to_the_same_identity_as_its_target()
    {
        using var fixture = new TempFolder();
        var original = Path.Combine(fixture.Root, "original.txt");
        var link = Path.Combine(fixture.Root, "link.txt");
        File.WriteAllText(original, "content");
        try
        {
            File.CreateSymbolicLink(link, original);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return; // unsupported on this file system/platform (e.g. Windows without Developer Mode)
        }

        Assert.True(FileIdentity.TryGet(original, out var originalIdentity));
        Assert.True(FileIdentity.TryGet(link, out var linkIdentity));
        Assert.Equal(originalIdentity, linkIdentity);
    }

    [Fact]
    public void Missing_file_returns_false()
    {
        using var fixture = new TempFolder();
        var missing = Path.Combine(fixture.Root, "does-not-exist.txt");

        Assert.False(FileIdentity.TryGet(missing, out _));
    }

    private sealed class TempFolder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "docredock-file-identity-tests", Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
