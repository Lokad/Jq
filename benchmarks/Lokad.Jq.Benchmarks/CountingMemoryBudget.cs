namespace Lokad.Jq.Benchmarking;

using System.Diagnostics.CodeAnalysis;
using System.Threading;

// Diagnostic forwarding only: the supplied host owns the allowance and its
// synchronization. Count runs separately from timing runs to avoid measuring
// this instrumentation. Shared by benchmark diagnostics and boundary tests.
internal sealed class CountingMemoryBudget(IMemoryBudget inner) : IMemoryBudget
{
    private readonly Calls _calls = new();

    internal MemoryCallCounts Counts => _calls.Snapshot();

    public IMemoryScope CreateScope()
    {
        Interlocked.Increment(ref _calls.ScopeCreations);
        return new Scope(inner.CreateScope(), _calls);
    }

    public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation) =>
        Reserve(inner, _calls, bytes, out reservation);

    private static bool Reserve(IMemoryBudget budget, Calls calls, long bytes,
        [NotNullWhen(true)] out IMemoryReservation? reservation)
    {
        Interlocked.Increment(ref calls.Reservations);
        if (!budget.TryReserve(bytes, out var admitted))
        {
            Interlocked.Increment(ref calls.Refusals);
            reservation = null;
            return false;
        }
        reservation = new Reservation(admitted, calls, bytes);
        return true;
    }

    private sealed class Scope(IMemoryScope inner, Calls calls) : IMemoryScope
    {
        public IMemoryScope CreateScope()
        {
            Interlocked.Increment(ref calls.ScopeCreations);
            return new Scope(inner.CreateScope(), calls);
        }

        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation) =>
            Reserve(inner, calls, bytes, out reservation);

        public void Dispose()
        {
            Interlocked.Increment(ref calls.ScopeDisposals);
            inner.Dispose();
        }
    }

    private sealed class Reservation(IMemoryReservation inner, Calls calls, long bytes) : IMemoryReservation
    {
        private long _bytes = bytes;

        public long Bytes
        {
            get
            {
                Interlocked.Increment(ref calls.BytesReads);
                return inner.Bytes;
            }
        }

        public bool TryResize(long bytes)
        {
            Interlocked.Increment(ref calls.Resizes);
            if (!inner.TryResize(bytes))
            {
                Interlocked.Increment(ref calls.Refusals);
                return false;
            }
            if (bytes > _bytes) Interlocked.Increment(ref calls.GrowingResizes);
            else if (bytes < _bytes) Interlocked.Increment(ref calls.ShrinkingResizes);
            else Interlocked.Increment(ref calls.SameSizeResizes);
            _bytes = bytes;
            return true;
        }

        public void Dispose()
        {
            Interlocked.Increment(ref calls.ReservationDisposals);
            inner.Dispose();
        }
    }

    private sealed class Calls
    {
        internal long ScopeCreations, Reservations, Resizes, BytesReads;
        internal long ReservationDisposals, ScopeDisposals, Refusals;
        internal long SameSizeResizes, GrowingResizes, ShrinkingResizes;

        internal MemoryCallCounts Snapshot() => new(
            Interlocked.Read(ref ScopeCreations), Interlocked.Read(ref Reservations),
            Interlocked.Read(ref Resizes), Interlocked.Read(ref BytesReads),
            Interlocked.Read(ref ReservationDisposals), Interlocked.Read(ref ScopeDisposals),
            Interlocked.Read(ref Refusals), Interlocked.Read(ref SameSizeResizes),
            Interlocked.Read(ref GrowingResizes), Interlocked.Read(ref ShrinkingResizes));
    }
}

internal readonly record struct MemoryCallCounts(long ScopeCreations, long Reservations,
    long Resizes, long BytesReads, long ReservationDisposals, long ScopeDisposals,
    long Refusals, long SameSizeResizes, long GrowingResizes, long ShrinkingResizes);
