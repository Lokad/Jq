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
    private int _smallCharges;

    internal long UsedBytes => _used;

    internal void ReserveBytes(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bytes < 0 || bytes > long.MaxValue - _used)
            throw new JqQuotaException("memory budget exceeded") { IsMemoryAdmission = true };
        long required = _used + bytes;
        int smallCharges = bytes is > 0 and < SmallIncrementBytes ? Math.Min(32, _smallCharges + 1) : _smallCharges;
        if (required > _memory.LastAdmittedBytes)
        {
            // Cold owners and substantial known copies stay exact. Repeated
            // small charges use 256-byte credit below 4 KiB, then 4 KiB credit.
            long target = required;
            int quantum = required > BlockBytes ? BlockBytes : smallCharges == 32 ? SmallIncrementBytes : 0;
            if (!_exactOnly && bytes < SmallIncrementBytes && quantum != 0
                && required <= long.MaxValue - (BlockBytes - 1))
                target = ((required + quantum - 1) / quantum) * quantum;
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
        _smallCharges = smallCharges;
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

    // The executor has finished all users of the previous outer record. Reuse
    // its host reservation, retaining only bounded, still-charged credit. Small
    // records retain only what they already admitted; no block is prepaid here.
    internal void ReuseForNextRecord()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long retained = Math.Min(_memory.LastAdmittedBytes, BlockBytes);
        if (_memory.LastAdmittedBytes != retained) _memory.Resize(retained);
        _used = 0;
        _smallCharges = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _memory.Dispose();
        _used = 0;
    }
}
