using DocRedock.Core.IO;

namespace DocRedock.Tests.Core;

public sealed class ExclusiveFileSystemTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-exclusive-日本語-").FullName;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_moves_have_one_winner_even_for_empty_directories(bool directory)
    {
        const int rounds = 128;
        var sources = new string[rounds, 2];
        var destinations = new string[rounds];
        var errors = new Exception?[rounds, 2];
        for (var round = 0; round < rounds; round++)
        {
            destinations[round] = Path.Combine(root, $"公開-{round}");
            for (var writer = 0; writer < 2; writer++)
            {
                sources[round, writer] = Path.Combine(root, $"仮-{round}-{writer}");
                if (directory) Directory.CreateDirectory(sources[round, writer]);
                else await File.WriteAllTextAsync(sources[round, writer], $"writer {writer}, round {round}");
            }
        }

        using var start = new Barrier(2);
        var workers = Enumerable.Range(0, 2).Select(writer => Task.Factory.StartNew(() =>
        {
            for (var round = 0; round < rounds; round++)
            {
                Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)), "Move writers did not reach the start barrier.");
                try
                {
                    if (directory) ExclusiveFileSystem.MoveDirectory(sources[round, writer], destinations[round]);
                    else ExclusiveFileSystem.MoveFile(sources[round, writer], destinations[round]);
                }
                catch (IOException exception) { errors[round, writer] = exception; }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));

        for (var round = 0; round < rounds; round++)
        {
            var outcomes = new[] { errors[round, 0], errors[round, 1] };
            Assert.Single(outcomes, error => error is null);
            Assert.Single(outcomes, error => error is IOException);
            var winner = errors[round, 0] is null ? 0 : 1;
            if (directory)
            {
                Assert.True(Directory.Exists(destinations[round]));
                Assert.False(Directory.Exists(sources[round, winner]));
                Assert.True(Directory.Exists(sources[round, 1 - winner]));
            }
            else
            {
                Assert.Equal($"writer {winner}, round {round}", await File.ReadAllTextAsync(destinations[round]));
                Assert.False(File.Exists(sources[round, winner]));
                Assert.True(File.Exists(sources[round, 1 - winner]));
            }
        }
    }

    [Fact]
    public async Task Existing_file_is_preserved_and_failed_move_keeps_its_source()
    {
        var source = Path.Combine(root, "stage");
        var destination = Path.Combine(root, "output");
        await File.WriteAllTextAsync(source, "new content");
        await File.WriteAllTextAsync(destination, "existing content");

        Assert.Throws<IOException>(() => ExclusiveFileSystem.MoveFile(source, destination));

        Assert.Equal("new content", await File.ReadAllTextAsync(source));
        Assert.Equal("existing content", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public void Existing_empty_directory_is_not_replaced()
    {
        var source = Path.Combine(root, "stage");
        var destination = Path.Combine(root, "output");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);

        Assert.Throws<IOException>(() => ExclusiveFileSystem.MoveDirectory(source, destination));

        Assert.True(Directory.Exists(source));
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public async Task Unix_dangling_destination_symlink_is_preserved()
    {
        if (OperatingSystem.IsWindows()) return; // Creating Windows symlinks requires an optional privilege.
        var source = Path.Combine(root, "stage");
        var destination = Path.Combine(root, "output");
        var target = Path.Combine(root, "absent");
        await File.WriteAllTextAsync(source, "new content");
        File.CreateSymbolicLink(destination, target);

        Assert.Throws<IOException>(() => ExclusiveFileSystem.MoveFile(source, destination));

        Assert.Equal(target, new FileInfo(destination).LinkTarget);
        Assert.Equal("new content", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Missing_source_reports_file_not_found_without_creating_output()
    {
        var destination = Path.Combine(root, "output");
        Assert.Throws<FileNotFoundException>(() => ExclusiveFileSystem.MoveFile(Path.Combine(root, "missing"), destination));
        Assert.False(File.Exists(destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_kind_mismatch_is_rejected_without_moving_it(bool directory)
    {
        var source = Path.Combine(root, "stage");
        var destination = Path.Combine(root, "output");
        if (directory) await File.WriteAllTextAsync(source, "file content");
        else Directory.CreateDirectory(source);

        Assert.Throws<IOException>(() =>
        {
            if (directory) ExclusiveFileSystem.MoveDirectory(source, destination);
            else ExclusiveFileSystem.MoveFile(source, destination);
        });

        Assert.True(File.Exists(source) || Directory.Exists(source));
        Assert.False(File.Exists(destination) || Directory.Exists(destination));
    }

    [Fact]
    public void Null_character_is_rejected_before_native_path_marshalling()
    {
        var destination = Path.Combine(root, "output");
        Assert.Throws<ArgumentException>(() => ExclusiveFileSystem.MoveFile(Path.Combine(root, "bad\0stage"), destination));
        Assert.False(File.Exists(destination));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
