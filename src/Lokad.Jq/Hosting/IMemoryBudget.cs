using System.Diagnostics.CodeAnalysis;

namespace Lokad.Jq;

/// <summary>A borrowed admission hook sharing the host's allowance across cooperating consumers.</summary>
/// <remarks>
/// Admission is thread-safe and immediate. Scopes have no independent cap. The caller must
/// serialize disposal with active users and keep accounted storage within its owner's lifetime.
/// </remarks>
public interface IMemoryBudget
{
    /// <summary>Creates an empty owned scope sharing this budget's allowance.</summary>
    IMemoryScope CreateScope();

    /// <summary>Reserves a nonnegative estimate; refusal returns false and a null reservation without changing accounting.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The estimate is negative.</exception>
    /// <exception cref="ObjectDisposedException">This owner has been disposed.</exception>
    bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation);
}

/// <summary>An owned budget subtree whose non-throwing, idempotent disposal refunds and invalidates its descendants.</summary>
/// <remarks>Disposal does not cancel work or dispose runtime objects. Await users and cleanup before disposal.</remarks>
public interface IMemoryScope : IMemoryBudget, IDisposable
{
}

/// <summary>An owned estimate whose non-throwing, idempotent disposal immediately refunds its charge.</summary>
public interface IMemoryReservation : IDisposable
{
    /// <summary>Gets the current estimate, or zero after this reservation or an ancestor is disposed.</summary>
    long Bytes { get; }

    /// <summary>Replaces the estimate; immediate refusal leaves it unchanged. Zero is valid.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The estimate is negative.</exception>
    /// <exception cref="ObjectDisposedException">This reservation or an ancestor has been disposed.</exception>
    bool TryResize(long bytes);
}
