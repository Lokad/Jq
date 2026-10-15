namespace Lokad.Jq;

// An operation result owns the admission established before its storage was
// created. Its parent scope is chosen by the caller, never reparented afterward.
internal sealed class JqMemoryValue<T>(T value, IDisposable owner) : IDisposable
{
    private IDisposable? _owner = owner;
    internal T Value { get; } = value;

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Dispose();
}
