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
            if (bytes < SmallIncrementBytes && quantum != 0
                && required <= long.MaxValue - (BlockBytes - 1))
                target = ((required + quantum - 1) / quantum) * quantum;
            _memory.Resize(target);
        }
        _used = required;
        _smallCharges = smallCharges;
    }

    // Retained cursor trees keep real demand plus bounded existing credit.
    // Cold owners keep at most 256 bytes; after repeated small charges,
    // 512 bytes amortizes short text events. Larger discarded demand still refunds.
    internal void ReconcileCursor(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        long admitted = _memory.LastAdmittedBytes;
        int maximumCredit = _smallCharges == 32 ? 512 : 256;
        long credit = Math.Min(maximumCredit, Math.Max(0, admitted - bytes));
        long target = bytes + credit;
        if (admitted != target) _memory.Resize(target);
        _used = bytes;
    }

    // The caller has finished all users of the previous record or predicate. Reuse
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
