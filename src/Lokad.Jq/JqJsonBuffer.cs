using System;
using System.Buffers;

namespace Lokad.Jq;

/// <summary>
/// Bounds writer reservations before ArrayBufferWriter grows. The byte builders used by shell
/// descriptors do not implement the writable-memory contract required by Utf8JsonWriter.
/// </summary>
internal sealed class JqJsonBuffer(JqBudget budget) : IBufferWriter<byte>
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    internal ReadOnlyMemory<byte> WrittenMemory => _buffer.WrittenMemory;

    // The host may retain output memory only until its append completes. The
    // executor waits for that completion before resetting this storage.
    internal void Reset() => _buffer.ResetWrittenCount();

    public void Advance(int count)
    {
        if (count > JqBudget.MaximumJsonBytes - _buffer.WrittenCount)
            throw new JqQuotaException("JSON output exceeds the 32 MiB limit");
        _buffer.Advance(count);
    }

    public Memory<byte> GetMemory(int sizeHint)
    {
        Reserve(sizeHint);
        return _buffer.GetMemory(sizeHint);
    }

    public Span<byte> GetSpan(int sizeHint)
    {
        Reserve(sizeHint);
        return _buffer.GetSpan(sizeHint);
    }

    private void Reserve(int sizeHint)
    {
        budget.CheckCancellation();
        var count = Math.Max(1, sizeHint);
        if (count > JqBudget.MaximumJsonBufferBytes - _buffer.WrittenCount)
            throw new JqQuotaException("JSON buffer exceeds the 64 MiB limit");
        if (count > _buffer.FreeCapacity)
        {
            var growth = Math.Max(count, Math.Max(256, _buffer.Capacity));
            if (growth > JqBudget.MaximumJsonBufferBytes - _buffer.Capacity)
                throw new JqQuotaException("JSON buffer exceeds the 64 MiB limit");
            budget.ChargeBytes((long)_buffer.Capacity + growth);
        }
    }
}
