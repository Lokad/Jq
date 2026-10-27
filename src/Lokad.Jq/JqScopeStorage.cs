namespace Lokad.Jq;

// Scope heads use the current coarse owner. Tail-call argument cursors can
// take this lifetime over; genuinely captured scopes stay with the call driver.
internal sealed class JqScopeStorage : IDisposable
{
    private readonly JqBudget _budget;
    private readonly JqStorage _storage;
    private readonly bool _ownsStorage;
    private long _bytes;
    private bool _disposed;

    internal JqScopeStorage(JqBudget budget)
    {
        _budget = budget;
        _ownsStorage = budget.TemporaryStorage is null;
        _storage = budget.TemporaryStorage ?? new JqStorage(budget.MemoryBudget);
    }

    internal JqScopeStorage? Next { get; set; }

    internal void Reserve(long bytes, int smallAllocations)
    {
        _budget.CheckCancellation();
        _storage.ReserveBytes(bytes, smallAllocations);
        _bytes = checked(_bytes + bytes);
        _budget.CheckCancellation();
    }

    internal void Release(long bytes)
    {
        _storage.DiscardBytes(bytes);
        _bytes = checked(_bytes - bytes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _storage.DiscardBytes(_bytes); }
        finally { if (_ownsStorage) _storage.Dispose(); }
    }

    internal static void DisposeAll(JqScopeStorage? scope)
    {
        Exception? failure = null;
        while (scope is not null)
        {
            try { scope.Dispose(); }
            catch (Exception exception)
            {
                if (failure is null || JqErrors.IsCatchable(failure) && !JqErrors.IsCatchable(exception))
                    failure = exception;
            }
            scope = scope.Next;
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
