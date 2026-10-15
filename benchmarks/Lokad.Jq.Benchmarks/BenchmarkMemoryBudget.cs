using System.Diagnostics.CodeAnalysis;

namespace Lokad.Jq.Benchmarking;

// Development-only representative lock-based host. Keep only active entries:
// timing must not measure a history scan of already-disposed reservations.
// The runtime package contains neither this governor nor its instrumentation.
internal sealed class BenchmarkMemoryBudget : IMemoryBudget
{
    private readonly State _state;

    internal BenchmarkMemoryBudget(long limitBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limitBytes);
        _state = new State(limitBytes);
    }

    internal long ReservedBytes { get { lock (_state.Gate) return _state.Reserved; } }
    internal long PeakBytes { get { lock (_state.Gate) return _state.Peak; } }

    public IMemoryScope CreateScope() => _state.CreateScope(null);

    public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation) =>
        _state.Reserve(null, bytes, out reservation);

    private sealed class State(long limit)
    {
        internal readonly object Gate = new();
        internal readonly HashSet<Reservation> Unscoped = [];
        internal long Reserved, Peak;

        internal void SetReserved(long bytes)
        {
            Reserved = bytes;
            Peak = Math.Max(Peak, bytes);
        }

        internal Scope CreateScope(Scope? parent)
        {
            lock (Gate)
            {
                parent?.CheckLive();
                var scope = new Scope(this, parent);
                parent?.Children.Add(scope);
                return scope;
            }
        }

        internal bool Reserve(Scope? owner, long bytes,
            [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            lock (Gate)
            {
                owner?.CheckLive();
                if (bytes > limit - Reserved) { reservation = null; return false; }
                var admitted = new Reservation(this, owner, bytes);
                (owner?.Owned ?? Unscoped).Add(admitted);
                SetReserved(Reserved + bytes);
                reservation = admitted;
                return true;
            }
        }

        internal bool Resize(Reservation reservation, long bytes)
        {
            lock (Gate)
            {
                reservation.CheckLive();
                long delta = bytes - reservation.Charge;
                if (delta > limit - Reserved) return false;
                SetReserved(Reserved + delta);
                reservation.Charge = bytes;
                return true;
            }
        }
    }

    private sealed class Scope(State state, Scope? parent) : IMemoryScope
    {
        internal readonly HashSet<Scope> Children = [];
        internal readonly HashSet<Reservation> Owned = [];
        private bool _disposed;

        internal void CheckLive() => ObjectDisposedException.ThrowIf(_disposed, this);

        public IMemoryScope CreateScope() => state.CreateScope(this);
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation) =>
            state.Reserve(this, bytes, out reservation);

        public void Dispose()
        {
            lock (state.Gate) DisposeCore();
        }

        private void DisposeCore()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var child in Children) child.DisposeCore();
            foreach (var reservation in Owned) reservation.DisposeCore(false);
            Children.Clear();
            Owned.Clear();
            if (parent is { _disposed: false }) parent.Children.Remove(this);
        }
    }

    private sealed class Reservation(State state, Scope? owner, long bytes) : IMemoryReservation
    {
        internal long Charge = bytes;
        private bool _disposed;

        internal void CheckLive() => ObjectDisposedException.ThrowIf(_disposed, this);
        public long Bytes { get { lock (state.Gate) return Charge; } }

        public bool TryResize(long bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            return state.Resize(this, bytes);
        }

        public void Dispose()
        {
            lock (state.Gate) DisposeCore(true);
        }

        internal void DisposeCore(bool remove)
        {
            if (_disposed) return;
            _disposed = true;
            state.SetReserved(state.Reserved - Charge);
            Charge = 0;
            if (remove) (owner?.Owned ?? state.Unscoped).Remove(this);
        }
    }
}
