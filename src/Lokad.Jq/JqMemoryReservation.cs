namespace Lokad.Jq;

// One coarse storage owner. Host accounting is forwarded directly; no allowance
// or governor is copied here. Calls without a host hook retain their work policy.
internal sealed class JqMemoryReservation(IMemoryBudget? memoryBudget) : IDisposable
{
    private IMemoryReservation? _reservation;
    private long _bytes;
    private bool _disposed;

    internal long Bytes => _reservation?.Bytes ?? _bytes;

    internal void Resize(long bytes)
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
                    throw new JqQuotaException("memory budget exceeded") { IsMemoryAdmission = true };
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException)
            {
                throw new JqHostFailureException("memory admission violated its contract", exception);
            }
        }
        _bytes = bytes;
    }

    internal void ReplaceBuffer(ref byte[] buffer, int capacity) =>
        ReplaceBuffer(ref buffer, capacity, 0, 0);

    internal void ReplaceBuffer(ref byte[] buffer, int capacity, int offset, int count)
    {
        long previous = Bytes;
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
        Resize(capacity);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reservation?.Dispose();
        _bytes = 0;
    }
}
