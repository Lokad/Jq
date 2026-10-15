namespace Lokad.Jq;

// Coarse value/compiler storage. Logical demand is separate from admitted
// credit; credit always belongs to this owner and still counts at the host.
// Direct buffer/result reservations keep their exact lifetime and peak rules.
internal sealed class JqStorage(IMemoryBudget? memoryBudget) : IDisposable
{
    private const int BlockBytes = 4096;
    private const int SmallIncrementBytes = 256;
    private readonly JqMemoryReservation _memory = new(memoryBudget);
    private long _used;
    private bool _exactOnly;
    private bool _disposed;

    internal long UsedBytes => _used;

    internal void ReserveBytes(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bytes < 0 || bytes > long.MaxValue - _used)
            throw new JqQuotaException("memory budget exceeded") { IsMemoryAdmission = true };
        long required = _used + bytes;
        if (required > _memory.LastAdmittedBytes)
        {
            // Small owners and substantial known copies stay exact. Bounded
            // credit amortizes small increments only after demand reaches a block.
            long target = required;
            if (!_exactOnly && bytes < SmallIncrementBytes && required > BlockBytes
                && required <= long.MaxValue - (BlockBytes - 1))
                target = ((required + BlockBytes - 1) / BlockBytes) * BlockBytes;
            if (target == required)
                _memory.Resize(required);
            else if (!_memory.TryResize(target))
            {
                // One exact attempt rejects only required storage, not optional
                // padding. Do not keep probing padding on a pressured owner.
                _exactOnly = true;
                _memory.Resize(required);
            }
        }
        _used = required;
    }

    // Cursor reconciliation is a real lifetime change. Refund both discarded
    // estimates and unused credit instead of retaining a growing high watermark.
    internal void Resize(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (_memory.LastAdmittedBytes != bytes) _memory.Resize(bytes);
        _used = bytes;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _memory.Dispose();
        _used = 0;
    }
}
