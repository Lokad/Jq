using System;
using System.Buffers;

namespace Lokad.Jq;

/// <summary>
/// Bounds writer reservations before ArrayBufferWriter grows. The byte builders used by shell
/// descriptors do not implement the writable-memory contract required by Utf8JsonWriter.
/// </summary>
internal sealed class JqJsonBuffer(JqBudget budget) : IBufferWriter<byte>, IDisposable
{
    private readonly ArrayBufferWriter<byte> _buffer = new();
    private readonly JqMemoryReservation _memory = new(budget.MemoryBudget);
    private bool _disposed;

    internal ReadOnlyMemory<byte> WrittenMemory => _buffer.WrittenMemory;
    internal int Capacity => _buffer.Capacity;
    internal long AdmittedBytes => _memory.LastAdmittedBytes;

    // The host may retain output memory only until its append completes. The
    // executor waits for that completion before resetting this storage.
    internal void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _buffer.ResetWrittenCount();
        ReleaseReplacedStorage();
    }

    // Growth may leave a body slice borrowing the previous backing array while
    // record framing copies it. Refund old capacity only after that copy ends.
    internal void ReleaseReplacedStorage()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_memory.LastAdmittedBytes != _buffer.Capacity)
            _memory.Resize(_buffer.Capacity);
    }

    public void Advance(int count)
    {
        if (count > JqBudget.MaximumSerializedValueBytes - _buffer.WrittenCount)
            throw new JqQuotaException("JSON output exceeds the 32 MiB limit");
        _buffer.Advance(count);
    }

    public Memory<byte> GetMemory(int sizeHint)
    {
        Reserve(sizeHint);
        try { return _buffer.GetMemory(sizeHint); }
        catch
        {
            ReleaseReplacedStorage();
            throw;
        }
    }

    public Span<byte> GetSpan(int sizeHint)
    {
        Reserve(sizeHint);
        try { return _buffer.GetSpan(sizeHint); }
        catch
        {
            ReleaseReplacedStorage();
            throw;
        }
    }

    private void Reserve(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        budget.CheckCancellation();
        var count = Math.Max(1, sizeHint);
        if (count > JqBudget.MaximumJsonBufferBytes - _buffer.WrittenCount)
            throw new JqQuotaException("JSON buffer exceeds the 64 MiB limit");
        if (count > _buffer.FreeCapacity)
        {
            var growth = Math.Max(count, Math.Max(256, _buffer.Capacity));
            if (growth > JqBudget.MaximumJsonBufferBytes - _buffer.Capacity)
                throw new JqQuotaException("JSON buffer exceeds the 64 MiB limit");
            budget.CheckCancellation();
            _memory.Resize(checked(_memory.LastAdmittedBytes + _buffer.Capacity + growth));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _memory.Dispose();
    }
}
