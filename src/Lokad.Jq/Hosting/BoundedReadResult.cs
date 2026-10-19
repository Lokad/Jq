using System;

namespace Lokad.Jq;

/// <summary>Complete borrowed-descriptor input or a read failure.</summary>
internal abstract record BoundedReadResult
{
    private BoundedReadResult() { }

    /// <summary>Bytes published only after successful EOF; their storage must not be mutated.</summary>
    internal sealed record Complete(ReadOnlyMemory<byte> Content, JqMemoryReservation Memory)
        : BoundedReadResult, IDisposable
    {
        public void Dispose() => Memory.Dispose();
    }

    /// <summary>The original descriptor failure; partial input is not published.</summary>
    internal sealed record Failed(JqByteReadResult Failure) : BoundedReadResult;

}
