using System;

namespace Lokad.Jq;

/// <summary>Complete borrowed-descriptor input, a read failure, or an exceeded byte limit.</summary>
internal abstract record BoundedReadResult
{
    private BoundedReadResult() { }

    /// <summary>Bytes published only after successful EOF; their storage must not be mutated.</summary>
    internal sealed record Complete(ReadOnlyMemory<byte> Content) : BoundedReadResult;

    /// <summary>The original descriptor failure; partial input is not published.</summary>
    internal sealed record Failed(JqByteReadResult Failure) : BoundedReadResult;

    /// <summary>Input exceeded the caller's limit; partial input is not published.</summary>
    internal sealed record TooLarge : BoundedReadResult;
}
