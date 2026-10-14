using System;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace Lokad.Jq;

/// <summary>
/// Independent execution-work counters and coarse storage owners. Work charges are
/// cumulative; admitted storage is refunded at record or execution lifetime boundaries.
/// </summary>
internal sealed class JqBudget : IDisposable
{
    internal const int MaximumStringLength = 8 * 1024 * 1024;
    internal const int MaximumInputBytes = 16 * 1024 * 1024;
    internal const int MaximumJsonBytes = 32 * 1024 * 1024;
    internal const int MaximumJsonBufferBytes = 64 * 1024 * 1024;
    internal const int MaximumDepth = 64;
    private readonly CancellationToken _cancellationToken;
    private long _remainingAllocationWork;
    private int _remainingNodes;
    private int _remainingInput;
    private int _remainingOutput;
    private int _evaluationDepth;
    private readonly JqMemoryReservation _executionStorage;
    private JqMemoryReservation _storage;
    private JqMemoryReservation? _recordStorage;
    private readonly List<JqMemoryReservation> _persistentStorageOwners = [];

    internal JqBudget(CancellationToken cancellationToken)
        : this(JqExecutionPolicy.Default, cancellationToken) { }

    internal JqBudget(JqExecutionPolicy policy, CancellationToken cancellationToken)
        : this(policy, null, cancellationToken) { }

    internal JqBudget(JqExecutionPolicy policy, IMemoryBudget? memoryBudget, CancellationToken cancellationToken)
    {
        Policy = policy;
        MemoryBudget = memoryBudget;
        _executionStorage = new JqMemoryReservation(memoryBudget);
        _storage = _executionStorage;
        _cancellationToken = cancellationToken;
        _remainingAllocationWork = policy.MaximumAllocationWorkBytes;
        _remainingNodes = policy.MaximumValueNodes;
        _remainingInput = policy.MaximumInputBytes;
        _remainingOutput = policy.MaximumOutputBytes;
    }

    internal JqExecutionPolicy Policy { get; }

    internal IMemoryBudget? MemoryBudget { get; }

    internal CancellationToken CancellationToken => _cancellationToken;

    internal int RemainingInput => _remainingInput;

    // One owner for each outer input evaluation. Keep the last record charged
    // through terminal diagnostics and cleanup; refund it before the next pull.
    internal void BeginRecord()
    {
        _recordStorage?.Dispose();
        _recordStorage = new JqMemoryReservation(MemoryBudget);
        _storage = _recordStorage;
    }

    internal IDisposable UseExecutionStorage() => UseStorage(_executionStorage);

    // The execution owns this region; cursors borrow it so terminal payloads
    // still retain their charge after descriptor/cursor cleanup.
    internal JqMemoryReservation CreateStorageOwner()
    {
        var owner = new JqMemoryReservation(MemoryBudget);
        _persistentStorageOwners.Add(owner);
        return owner;
    }

    internal IDisposable UseStorage(JqMemoryReservation storage)
    {
        var selection = new StorageSelection(this, _storage);
        _storage = storage;
        return selection;
    }

    private sealed class StorageSelection(JqBudget budget, JqMemoryReservation previous) : IDisposable
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
        if (bytes < 0 || bytes > long.MaxValue - _storage.Bytes)
            throw new JqQuotaException("memory budget exceeded") { IsMemoryAdmission = true };
        if (bytes != 0) _storage.Resize(_storage.Bytes + bytes);
    }

    internal void ChargeValueNode()
    {
        ChargeNode();
        ReserveBytes(64);
    }

    internal void ChargeAllocation(long bytes)
    {
        ChargeAllocationWork(bytes);
        ReserveBytes(bytes);
    }

    // Managed scalar clones share immutable payloads. Their small standalone
    // wrappers are untracked; container clones account for all stored wrappers
    // and backing slots, without charging borrowed strings a second time.
    internal void ReserveCloneStorage(JsonNode? value)
    {
        ReserveBytes(Estimate(value));

        static long Estimate(JsonNode? value)
        {
            if (value is JsonArray array)
            {
                long bytes = 64 + 24L * array.Count;
                foreach (JsonNode? child in array)
                    bytes += child is JsonValue ? 64 : Estimate(child);
                return bytes;
            }
            if (value is JsonObject obj)
            {
                long bytes = 64 + 96L * obj.Count;
                foreach (var entry in obj)
                    bytes += entry.Value is JsonValue ? 64 : Estimate(entry.Value);
                return bytes;
            }
            return 0;
        }
    }

    internal static long EstimateTreeStorage(JsonNode? value)
    {
        if (value is null) return 0;
        long bytes = 64;
        if (value is JsonArray array)
        {
            bytes += 24L * array.Count;
            foreach (JsonNode? child in array) bytes += EstimateTreeStorage(child);
        }
        else if (value is JsonObject obj)
        {
            bytes += 96L * obj.Count;
            foreach (var entry in obj)
                bytes += 2L * entry.Key.Length + EstimateTreeStorage(entry.Value);
        }
        else if (value is JsonValue scalar && scalar.TryGetValue<string>(out string? text))
        {
            bytes += 2L * text.Length;
        }
        return bytes;
    }

    internal void ChargeClone(JsonNode? value)
    {
        ChargeTree(value);
        ReserveCloneStorage(value);
    }

    public void Dispose()
    {
        foreach (JqMemoryReservation owner in _persistentStorageOwners) owner.Dispose();
        _persistentStorageOwners.Clear();
        _recordStorage?.Dispose();
        _executionStorage.Dispose();
    }

    internal void CheckCancellation() => _cancellationToken.ThrowIfCancellationRequested();

    internal string InputLimitMessage => Policy.MaximumInputBytes == MaximumInputBytes
        ? "input exceeds the 16 MiB limit"
        : $"input exceeds the {Policy.MaximumInputBytes}-byte limit";

    internal void ChargeAllocationWork(long count)
    {
        CheckCancellation();
        if (count < 0 || count > _remainingAllocationWork)
            throw new JqQuotaException("allocation work allowance exceeded");
        _remainingAllocationWork -= count;
    }

    internal void ChargeNode()
    {
        CheckCancellation();
        if (_remainingNodes <= 0)
            throw new JqQuotaException("value budget exceeded");
        _remainingNodes--;
    }

    internal void ChargeInput(int bytes)
    {
        CheckCancellation();
        if (bytes < 0 || bytes > _remainingInput)
            throw new JqQuotaException(InputLimitMessage);
        // Conservative work charge for read, staging and decode preparation.
        ChargeAllocationWork(4L * bytes);
        _remainingInput -= bytes;
    }

    internal void ChargeOutput(int bytes)
    {
        CheckCancellation();
        if (bytes < 0 || bytes > _remainingOutput)
            throw new JqQuotaException(Policy.MaximumOutputBytes == MaximumJsonBytes
                ? "output exceeds the 32 MiB limit"
                : $"output exceeds the {Policy.MaximumOutputBytes}-byte limit");
        ChargeAllocationWork(bytes);
        _remainingOutput -= bytes;
    }

    internal void CheckStringLength(long length)
    {
        CheckCancellation();
        if (length < 0 || length > Policy.MaximumStringLength)
            throw new JqQuotaException(Policy.MaximumStringLength == MaximumStringLength
                ? "string result exceeds the 16 MiB UTF-16 limit"
                : $"string result exceeds the {Policy.MaximumStringLength} UTF-16 code unit limit");
    }

    internal void ChargeString(long length)
    {
        ChargeStringWork(length);
        ReserveBytes(length * sizeof(char));
    }

    internal void ChargeStringWork(long length)
    {
        CheckStringLength(length);
        ChargeAllocationWork(length * sizeof(char));
    }

    internal void Append(StringBuilder builder, ReadOnlySpan<char> text)
    {
        CheckStringLength((long)builder.Length + text.Length);
        ChargeString(text.Length);
        builder.Append(text);
    }

    internal string Finish(StringBuilder builder)
    {
        ChargeString(builder.Length);
        return builder.ToString();
    }

    internal void ChargeTree(JsonNode? node)
    {
        Visit(node, 0);
    }

    private void Visit(JsonNode? value, int depth)
    {
        if (depth > MaximumDepth || depth == MaximumDepth && value is JsonArray or JsonObject)
            throw new JqQuotaException("value nesting limit exceeded");
        ChargeNode();
        switch (value)
        {
            case JsonArray array:
                foreach (var item in array) Visit(item, depth + 1);
                break;
            case JsonObject obj:
                foreach (var property in obj)
                {
                    ChargeStringWork(property.Key.Length);
                    Visit(property.Value, depth + 1);
                }
                break;
            case JsonValue scalar when scalar.TryGetValue<string>(out var text):
                ChargeStringWork(text.Length);
                break;
        }
    }

    internal void EnterEvaluation()
    {
        ChargeNode();
        if (_evaluationDepth >= 256)
            throw new JqQuotaException("filter nesting limit exceeded");
        _evaluationDepth++;
    }

    internal void LeaveEvaluation() => _evaluationDepth--;
}
