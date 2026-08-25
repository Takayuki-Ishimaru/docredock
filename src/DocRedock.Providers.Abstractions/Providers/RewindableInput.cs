namespace DocRedock.Providers.Abstractions.Providers;

/// <summary>Provides every probe with the same seekable input and restores its position after each attempt.</summary>
public sealed class RewindableInput : IAsyncDisposable
{
    private readonly Stream stream;
    private readonly bool ownsStream;
    private readonly long initialPosition;

    public RewindableInput(Stream stream, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek) throw new ArgumentException("Non-seekable streams must be created through CreateAsync.", nameof(stream));
        this.stream = stream;
        ownsStream = !leaveOpen;
        initialPosition = stream.Position;
    }

    public Stream Stream => stream;
    public long Length => stream.Length;

    public static async ValueTask<RewindableInput> CreateAsync(Stream source, long maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.CanSeek)
        {
            if (source.Length - source.Position > maxBytes)
                throw new InvalidDataException("Input exceeds the configured probe limit.");
            return new RewindableInput(source);
        }
        var copy = new MemoryStream();
        var buffer = new byte[81_920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > maxBytes) { await copy.DisposeAsync().ConfigureAwait(false); throw new InvalidDataException("Input exceeds the configured probe limit."); }
            await copy.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        copy.Position = 0;
        return new RewindableInput(copy, leaveOpen: false);
    }

    public void Reset()
    {
        stream.Position = initialPosition;
    }

    public ValueTask DisposeAsync() => ownsStream ? stream.DisposeAsync() : ValueTask.CompletedTask;
}
