using System.Diagnostics.CodeAnalysis;

namespace Lokad.Jq.Tests;

// Test-only host allowance. Every scope and reservation refers to this root;
// the library must neither own it nor copy its remaining balance.
internal sealed class TestMemoryBudget(long limitBytes) : IMemoryBudget
{
    private readonly object _gate = new();
    private readonly long _limitBytes = limitBytes;
    private readonly List<Reservation> _reservations = [];
    private long _reservedBytes;
    private long _peakBytes;

    internal long ReservedBytes { get { lock (_gate) return _reservedBytes; } }
    internal long PeakBytes { get { lock (_gate) return _peakBytes; } }

    public IMemoryScope CreateScope() => new Scope(this, null);

    public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation) =>
        TryReserve(null, bytes, out reservation);

    private bool TryReserve(Scope? owner, long bytes,
        [NotNullWhen(true)] out IMemoryReservation? reservation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate)
        {
            owner?.CheckLive();
            if (bytes > _limitBytes - _reservedBytes)
            {
                reservation = null;
                return false;
            }
            var admitted = new Reservation(this, owner, bytes);
            _reservations.Add(admitted);
            SetReserved(_reservedBytes + bytes);
            reservation = admitted;
            return true;
        }
    }

    private void SetReserved(long bytes)
    {
        _reservedBytes = bytes;
        _peakBytes = Math.Max(_peakBytes, bytes);
    }

    private sealed class Scope(TestMemoryBudget root, Scope? parent) : IMemoryScope
    {
        private bool _disposed;

        internal void CheckLive()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            parent?.CheckLive();
        }

        internal bool IsWithin(Scope ancestor) =>
            ReferenceEquals(this, ancestor) || parent is { } owner && owner.IsWithin(ancestor);

        public IMemoryScope CreateScope()
        {
            lock (root._gate)
            {
                CheckLive();
                return new Scope(root, this);
            }
        }

        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation) =>
            root.TryReserve(this, bytes, out reservation);

        public void Dispose()
        {
            lock (root._gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (Reservation reservation in root._reservations)
                    if (reservation.Owner is { } owner && owner.IsWithin(this))
                        reservation.Dispose();
            }
        }
    }

    private sealed class Reservation(TestMemoryBudget root, Scope? owner, long bytes) : IMemoryReservation
    {
        private long _bytes = bytes;
        private bool _disposed;
        internal Scope? Owner { get; } = owner;

        public long Bytes { get { lock (root._gate) return _bytes; } }

        public bool TryResize(long bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            lock (root._gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                Owner?.CheckLive();
                long extra = bytes - _bytes;
                if (extra > root._limitBytes - root._reservedBytes) return false;
                root.SetReserved(root._reservedBytes + extra);
                _bytes = bytes;
                return true;
            }
        }

        public void Dispose()
        {
            lock (root._gate)
            {
                if (_disposed) return;
                _disposed = true;
                root._reservedBytes -= _bytes;
                _bytes = 0;
            }
        }
    }
}
