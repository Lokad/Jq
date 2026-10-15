using System.Diagnostics.CodeAnalysis;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

// The same deterministic host contract serves tests, consumer proof and opt-in
// timing diagnostics. This adapter forwards without a separate allowance.
internal sealed class TestMemoryBudget(long limitBytes) : IMemoryBudget
{
    private readonly BenchmarkMemoryBudget _inner = new(limitBytes);

    internal long ReservedBytes => _inner.ReservedBytes;
    internal long PeakBytes => _inner.PeakBytes;

    public IMemoryScope CreateScope() => _inner.CreateScope();
    public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation) =>
        _inner.TryReserve(bytes, out reservation);
}
