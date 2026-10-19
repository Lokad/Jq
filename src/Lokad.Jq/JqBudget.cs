using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace Lokad.Jq;

/// <summary>
/// Cancellation, structural safeguards and coarse storage owners. Admitted
/// storage is refunded at record or execution lifetime boundaries.
/// </summary>
internal sealed class JqBudget : IDisposable
{
    private const int MaximumIdleSerializationBytes = 4096;
    internal const int MaximumSerializedValueBytes = 32 * 1024 * 1024;
    internal const int MaximumJsonBufferBytes = 64 * 1024 * 1024;
    internal const int MaximumDepth = 64;
    private readonly CancellationToken _cancellationToken;
    private int _evaluationDepth;
    private readonly JqStorage _executionStorage;
    private JqStorage _storage;
    private JqStorage? _recordStorage;
    private readonly List<JqStorage> _persistentStorageOwners = [];
    private JqJsonBuffer? _serializationBuffer;
    private int _serializedResults;
    private bool _disposed;

    internal JqBudget(CancellationToken cancellationToken)
        : this(JqExecutionPolicy.Default, cancellationToken) { }

    internal JqBudget(JqExecutionPolicy policy, CancellationToken cancellationToken)
        : this(policy, null, cancellationToken) { }

    internal JqBudget(JqExecutionPolicy policy, IMemoryBudget? memoryBudget, CancellationToken cancellationToken)
    {
        Policy = policy;
        MemoryBudget = memoryBudget;
        _executionStorage = new JqStorage(memoryBudget);
        _storage = _executionStorage;
        _cancellationToken = cancellationToken;
    }

    internal JqExecutionPolicy Policy { get; }

    internal IMemoryBudget? MemoryBudget { get; }

    internal CancellationToken CancellationToken => _cancellationToken;

    internal JqJsonBuffer RentSerializationBuffer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        JqJsonBuffer buffer = _serializationBuffer ?? new JqJsonBuffer(this);
        _serializationBuffer = null;
        try { buffer.Reset(); return buffer; }
        catch { buffer.Dispose(); throw; }
    }

    internal IDisposable OwnSerializationBuffer(JqJsonBuffer buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _serializedResults = Math.Min(32, _serializedResults + 1);
        // Cold one-off results retain their existing immediate refund behavior.
        if (_serializedResults < 32) return buffer;
        // The writer has ended its borrows before handoff. Reconcile only a
        // reusable buffer; cold and truly oversized results refund on return.
        if (buffer.Capacity <= MaximumIdleSerializationBytes) buffer.ReleaseReplacedStorage();
        return new SerializationReturn(this, buffer);
    }

    private sealed class SerializationReturn(JqBudget budget, JqJsonBuffer buffer) : IDisposable
    {
        private bool _returned;

        public void Dispose()
        {
            if (_returned) return;
            _returned = true;
            if (!budget._disposed && budget._serializationBuffer is null
                && buffer.AdmittedBytes <= MaximumIdleSerializationBytes)
                budget._serializationBuffer = buffer;
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
    }

    internal void ReserveValueNode()
    {
        ReserveBytes(64);
    }

    // Managed scalar clones share immutable payloads. Their small standalone
    // wrappers are untracked; container clones account for all stored wrappers
    // and backing slots, without charging borrowed strings a second time.
    [return: NotNullIfNotNull(nameof(value))]
    internal JsonNode? CopyValue(JsonNode? value, int depth, bool sortKeys)
    {
        CheckCancellation();
        CheckValueDepth(value, depth);
        if (value is JsonArray array)
        {
            ReserveBytes(64 + 24L * array.Count);
            var copy = new JsonArray();
            foreach (JsonNode? child in array)
            {
                if (child is JsonValue) ReserveBytes(64);
                copy.Add(CopyValue(child, depth + 1, sortKeys));
            }
            return copy;
        }
        if (value is JsonObject obj)
        {
            ReserveBytes(64 + 96L * obj.Count);
            if (sortKeys) ReserveBytes(32L * obj.Count);
            IEnumerable<KeyValuePair<string, JsonNode?>> entries = sortKeys
                ? obj.OrderBy(property => property.Key, StringComparer.Ordinal) : obj;
            var copy = new JsonObject();
            foreach (var entry in entries)
            {
                if (entry.Value is JsonValue) ReserveBytes(64);
                copy.Add(entry.Key, CopyValue(entry.Value, depth + 1, sortKeys));
            }
            return copy;
        }
        return value?.DeepClone();
    }

    internal void ReserveArrayItem(JsonNode? value) => ReserveBytes(value is JsonValue ? 88 : 24);

    internal void ReserveObjectItem(JsonNode? value) => ReserveBytes(value is JsonValue ? 160 : 96);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _serializationBuffer?.Dispose();
        _serializationBuffer = null;
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

    internal void Append(StringBuilder builder, ReadOnlySpan<char> text)
    {
        CheckCancellation();
        if (text.Length > int.MaxValue - builder.Length)
            throw new JqQuotaException("string result cannot be represented");
        ReserveString(text.Length);
        builder.Append(text);
    }

    internal string Finish(StringBuilder builder)
    {
        ReserveString(builder.Length);
        return builder.ToString();
    }

    internal static void CheckValueDepth(JsonNode? value, int depth)
    {
        if (depth > MaximumDepth || depth == MaximumDepth && value is JsonArray or JsonObject)
            throw new JqQuotaException("value nesting limit exceeded");
    }

    internal void EnterEvaluation()
    {
        CheckCancellation();
        if (_evaluationDepth >= 256)
            throw new JqQuotaException("filter nesting limit exceeded");
        _evaluationDepth++;
    }

    internal void LeaveEvaluation() => _evaluationDepth--;
}
