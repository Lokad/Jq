namespace Lokad.Jq;

// One coarse storage owner. Host accounting is forwarded directly; no allowance
// or governor is copied here. Calls without a host hook keep cancellation and structural safeguards.
internal sealed class JqMemoryReservation(IMemoryBudget? memoryBudget) : IDisposable
{
    private IMemoryReservation? _reservation;
    private long _bytes;
    private bool _disposed;

    // Valid for owner-local accounting while its borrowers are live. Ancestor
    // disposal is serialized with users; every admission reaches the host.
    internal long LastAdmittedBytes => _bytes;

    internal void Resize(long bytes)
    {
        if (!TryResize(bytes))
            throw new JqQuotaException("memory budget exceeded") { IsMemoryAdmission = true };
    }

    internal bool TryResize(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (memoryBudget is not null)
        {
            try
            {
                bool admitted = _reservation is { } reservation
                    ? reservation.TryResize(bytes)
                    : memoryBudget.TryReserve(bytes, out _reservation);
                if (!admitted)
                    return false;
            }
            catch (Exception exception) when (JqErrors.IsCatchableClr(exception))
            {
                throw new JqHostFailureException("memory admission violated its contract", exception);
            }
        }
        _bytes = bytes;
        return true;
    }

    internal void ReplaceBuffer(ref byte[] buffer, int capacity) =>
        ReplaceBuffer(ref buffer, capacity, 0, 0);

    internal void ReplaceBuffer(ref byte[] buffer, int capacity, int offset, int count)
    {
        long previous = _bytes;
        Resize(checked(previous + capacity));
        try
        {
            var replacement = new byte[capacity];
            buffer.AsSpan(offset, count).CopyTo(replacement);
            buffer = replacement;
        }
        catch
        {
            Resize(previous);
            throw;
        }
        if (previous != 0) Resize(capacity);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reservation?.Dispose();
        _bytes = 0;
    }
}
