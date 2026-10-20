using System;

namespace Lokad.Jq;

/// <summary>Complete borrowed-descriptor input or a read failure.</summary>
internal abstract record JqReadResult
{
    private JqReadResult() { }

    /// <summary>Bytes published only after successful EOF; their storage must not be mutated.</summary>
    internal sealed record Complete(ReadOnlyMemory<byte> Content, JqMemoryReservation Memory)
        : JqReadResult, IDisposable
    {
        public void Dispose() => Memory.Dispose();
    }

    /// <summary>Read failure; partial input is not published.</summary>
    internal sealed record Failed : JqReadResult;

}
