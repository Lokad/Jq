namespace Lokad.Jq;

// An operation result owns the admission established before its storage was
// created. Its parent scope is chosen by the caller, never reparented afterward.
internal sealed class JqMemoryValue<T>(T value, JqMemoryReservation memory) : IDisposable
{
    internal T Value { get; } = value;

    public void Dispose() => memory.Dispose();
}
