using System.Runtime.CompilerServices;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Lokad.Jq;

/// <summary>
/// Cancellation, structural safeguards and coarse storage owners. Admitted
/// storage is refunded at proven lifetime boundaries.
/// </summary>
internal sealed class JqBudget : IDisposable
{
    private const int MaximumIdleUtf8Bytes = 4096;
    private readonly CancellationToken _cancellationToken;
    private int _recursionDepth;
    private readonly JqStorage _executionStorage;
    private JqStorage _storage;
    private JqStorage? _recordStorage;
    private readonly List<JqStorage> _persistentStorageOwners = [];
    private JqJsonBuffer? _utf8Buffer;
    private int _serializedResults;
    private bool _disposed;
    private JqStringKeys? _stringKeys;

    internal JqStringKeys StringKeys => _stringKeys ??= new JqStringKeys(this);

    internal JqBudget(CancellationToken cancellationToken)
        : this(null, null, cancellationToken) { }

    internal JqBudget(IJqRegexEngine regexEngine, CancellationToken cancellationToken)
        : this(regexEngine, null, cancellationToken) { }

    internal JqBudget(IJqRegexEngine? regexEngine, IMemoryBudget? memoryBudget, CancellationToken cancellationToken)
    {
        RegexEngine = regexEngine;
        MemoryBudget = memoryBudget;
        _executionStorage = new JqStorage(memoryBudget);
        _storage = _executionStorage;
        _cancellationToken = cancellationToken;
    }

    internal IJqRegexEngine? RegexEngine { get; }

    internal IMemoryBudget? MemoryBudget { get; }

    internal CancellationToken CancellationToken => _cancellationToken;

    // Persistent compiler/module storage must never participate in local
    // temporary-demand reconciliation, even in white-box evaluations.
    internal JqStorage? TemporaryStorage => ReferenceEquals(_storage, _executionStorage) ? null : _storage;

    internal static int BufferCapacity(long required, long preferred)
    {
        if (required < 0 || required > Array.MaxLength)
            throw new JqQuotaException("byte buffer cannot be represented");
        return (int)Math.Min(Array.MaxLength, Math.Max(required, preferred));
    }

    internal JqJsonBuffer RentUtf8Buffer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        JqJsonBuffer buffer = _utf8Buffer ?? new JqJsonBuffer(this);
        _utf8Buffer = null;
        try { buffer.Reset(); return buffer; }
        catch { buffer.Dispose(); throw; }
    }

    internal IDisposable OwnSerializationBuffer(JqJsonBuffer buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // The caller has ended writer/copy borrows before lending this result.
        buffer.ReleaseReplacedStorage();
        _serializedResults = Math.Min(32, _serializedResults + 1);
        // Cold one-off results retain their existing immediate refund behavior.
        if (_serializedResults < 32) return buffer;
        // Retain at most one small returned buffer after repeated results.
        return new Utf8BufferReturn(this, buffer);
    }

    internal JqMemoryValue<JqJsonBuffer> RentUtf8Builder()
    {
        JqJsonBuffer buffer = RentUtf8Buffer();
        return new JqMemoryValue<JqJsonBuffer>(buffer, new Utf8BufferReturn(this, buffer));
    }

    private sealed class Utf8BufferReturn(JqBudget budget, JqJsonBuffer buffer) : IDisposable
    {
        private bool _returned;

        public void Dispose()
        {
            if (_returned) return;
            _returned = true;
            if (!budget._disposed) buffer.ReleaseReplacedStorage();
            if (!budget._disposed && budget._utf8Buffer is null
                && buffer.AdmittedBytes <= MaximumIdleUtf8Bytes)
                budget._utf8Buffer = buffer;
            else
                buffer.Dispose();
        }
    }

    // Reuse one coarse record owner only at the safe outer-input boundary.
    // Retain at most one charged block; the last record stays fully charged
    // through terminal diagnostics and cleanup.
    internal void BeginRecord()
    {
        _recordStorage ??= new JqStorage(MemoryBudget);
        _recordStorage.ReuseForNextRecord();
        _storage = _recordStorage;
    }

    internal IDisposable UseExecutionStorage() => UseStorage(_executionStorage);

    // The execution owns this region; cursors borrow it so terminal payloads
    // still retain their charge after descriptor/cursor cleanup.
    internal JqStorage CreateStorageOwner()
    {
        var owner = new JqStorage(MemoryBudget);
        _persistentStorageOwners.Add(owner);
        return owner;
    }

    internal IDisposable UseStorage(JqStorage storage)
    {
        var selection = new StorageSelection(this, _storage);
        _storage = storage;
        return selection;
    }

    private sealed class StorageSelection(JqBudget budget, JqStorage previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            budget._storage = previous;
        }
    }

    internal void ReserveBytes(long bytes)
    {
        CheckCancellation();
        if (bytes == 0) return;
        _storage.ReserveBytes(bytes);
        CheckCancellation();
    }

    internal void ReserveMetadata(long bytes, int smallAllocations)
    {
        CheckCancellation();
        _storage.ReserveBytes(bytes, smallAllocations);
        CheckCancellation();
    }

    internal void ReserveValueNode()
    {
        ReserveBytes(64);
    }

    // Managed scalar clones share immutable payloads. Their small standalone
    // wrappers are untracked; container clones account for all stored wrappers
    // and backing slots, without charging borrowed strings a second time.
    [return: NotNullIfNotNull(nameof(value))]
    internal JqValue? CopyValue(JqValue? value, int depth)
    {
        CheckCancellation();
        JqRecursion.CheckValueDepth(value, depth);
        if (value is JqArray array)
        {
            ReserveBytes(64 + 24L * array.Count);
            var copy = new JqArray();
            foreach (JqValue? child in array)
            {
                if (child is JqScalar) ReserveBytes(64);
                copy.Add(CopyValue(child, depth + 1));
            }
            return copy;
        }
        if (value is JqObject obj)
        {
            ReserveBytes(64 + 96L * obj.Count);
            var copy = new JqObject(this);
            foreach (var entry in obj)
            {
                if (entry.Value is JqScalar) ReserveBytes(64);
                copy.Add(entry.Key, CopyValue(entry.Value, depth + 1));
            }
            return copy;
        }
        return value is JqScalar scalar ? scalar.Copy() : null;
    }

    internal void ReserveArrayItem(JqValue? value) => ReserveBytes(value is JqScalar ? 88 : 24);

    internal void ReserveObjectItem(JqValue? value) => ReserveBytes(value is JqScalar ? 160 : 96);

    // The producer guarantees a fresh container has not escaped through a
    // binding or another result. Its construction is already funded. Borrowed
    // values keep their existing copying boundary; attachment checks the depth
    // of adopted children just as copying does, before the container is exposed.
    [return: NotNullIfNotNull(nameof(value))]
    internal JqValue? CollectValue(JqValue? value, int depth, bool freshContainer)
    {
        if (freshContainer && value is JqArray or JqObject)
        {
            CheckDepth(value, depth);
            return value;
        }
        return CopyValue(value, depth);

        void CheckDepth(JqValue? child, int level)
        {
            CheckCancellation();
            JqRecursion.CheckValueDepth(child, level);
            if (child is JqArray array)
                foreach (JqValue? element in array) CheckDepth(element, level + 1);
            else if (child is JqObject obj)
                foreach (var property in obj) CheckDepth(property.Value, level + 1);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _utf8Buffer?.Dispose();
        _utf8Buffer = null;
        foreach (JqStorage owner in _persistentStorageOwners) owner.Dispose();
        _persistentStorageOwners.Clear();
        _recordStorage?.Dispose();
        _executionStorage.Dispose();
    }

    internal void CheckCancellation() => _cancellationToken.ThrowIfCancellationRequested();

    internal void ReserveString(long length)
    {
        CheckCancellation();
        if (length < 0 || length > int.MaxValue)
            throw new JqQuotaException("string result cannot be represented");
        ReserveBytes(checked(length * sizeof(char)));
    }

    // Count nested advancement, including a suspended host operation. Yielded
    // results keep their storage owners without keeping an active stack frame.
    internal ValueTask<bool> MoveNextAsync<T>(IAsyncEnumerator<T> iterator)
    {
        EnterRecursion();
        try
        {
            ValueTask<bool> moved = iterator.MoveNextAsync();
            if (moved.IsCompletedSuccessfully)
            {
                bool result = moved.Result;
                LeaveRecursion();
                return ValueTask.FromResult(result);
            }
            return AwaitMoveNextAsync(moved, this);
        }
        catch { LeaveRecursion(); throw; }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        static async ValueTask<bool> AwaitMoveNextAsync(ValueTask<bool> moved, JqBudget budget)
        {
            try { return await moved.ConfigureAwait(false); }
            finally { budget.LeaveRecursion(); }
        }
    }

    internal void EnterRecursion()
    {
        CheckCancellation();
        JqRecursion.CheckDepth(_recursionDepth + 1, "filter nesting limit exceeded");
        _recursionDepth++;
    }

    internal void LeaveRecursion() => _recursionDepth--;
}
