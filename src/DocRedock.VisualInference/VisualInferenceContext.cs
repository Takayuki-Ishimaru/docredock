using System.Threading;

namespace DocRedock.VisualInference;

/// <summary>
/// Carries the selected inference policy through the synchronous adapter APIs without
/// changing their public signatures. The value is scoped to the current async flow.
/// </summary>
public static class VisualInferenceContext
{
    private static readonly AsyncLocal<VisualInferenceMode?> Mode = new();

    public static VisualInferenceMode Current => Mode.Value ?? VisualInferenceMode.Safe;

    public static IDisposable Push(VisualInferenceMode mode)
    {
        var previous = Mode.Value;
        Mode.Value = mode;
        return new Scope(previous);
    }

    private sealed class Scope(VisualInferenceMode? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            Mode.Value = previous;
            disposed = true;
        }
    }
}
