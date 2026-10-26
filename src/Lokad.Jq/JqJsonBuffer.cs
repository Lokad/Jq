using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace Lokad.Jq;

/// <summary>
/// Owns UTF-8 serialization storage and admits growth before allocation.
/// </summary>
internal sealed class JqJsonBuffer(JqBudget budget) : IBufferWriter<byte>, IDisposable
{
    private readonly ArrayBufferWriter<byte> _buffer = new();
    private readonly JqMemoryReservation _memory = new(budget.MemoryBudget);
    private bool _disposed;

    internal ReadOnlyMemory<byte> WrittenMemory => _buffer.WrittenMemory;
    internal int Capacity => _buffer.Capacity;
    internal long AdmittedBytes => _memory.LastAdmittedBytes;

    // Call only after the writer releases its spans and before lending the body.
    // ArrayBufferWriter owns a writable array; this conversion stays inside its
    // owner. Skip complete escapes so literal backslash-u text keeps its case.
    internal void LowercaseUnicodeEscapes()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        budget.CheckCancellation();
        Span<byte> body = MemoryMarshal.AsMemory(_buffer.WrittenMemory).Span;
        int scanned = 0;
        for (int index = 0; index + 1 < body.Length; index++)
        {
            if ((scanned++ & 4095) == 0) budget.CheckCancellation();
            if (body[index] != (byte)'\\' || body[++index] != (byte)'u') continue;
            for (int digit = index + 1; digit < index + 5; digit++)
                if (body[digit] >= (byte)'A' && body[digit] <= (byte)'F')
                    body[digit] = (byte)(body[digit] + 32);
            index += 4;
        }
        budget.CheckCancellation();
    }

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
        budget.CheckCancellation();
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
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        var count = Math.Max(1, sizeHint);
        if (count > _buffer.FreeCapacity)
        {
            var growth = Math.Max(count, Math.Max(256, _buffer.Capacity));
            int capacity = JqBudget.BufferCapacity((long)_buffer.WrittenCount + count,
                (long)_buffer.Capacity + growth);
            budget.CheckCancellation();
            // A new writer loan replaces the previous one. Earlier backing
            // arrays have no borrower; only the current array overlaps this
            // replacement, including a body slice used by record framing.
            _memory.Resize(checked((long)_buffer.Capacity + capacity));
            budget.CheckCancellation();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _memory.Dispose();
    }
}
