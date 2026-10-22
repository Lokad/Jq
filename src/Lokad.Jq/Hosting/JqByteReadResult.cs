using System;

namespace Lokad.Jq;

/// <summary>Represents a bounded raw-byte read from a descriptor.</summary>
/// <remarks>
/// A successful read copies at least one byte. A final successful read may report end-of-file together
/// with its bytes. The default value represents normal end-of-file.
/// </remarks>
public readonly record struct JqByteReadResult
{
    private readonly ByteReadKind _kind;
    private readonly bool _reachedEoF;

    private JqByteReadResult(
        ByteReadKind kind,
        int bytesRead,
        bool reachedEoF)
    {
        _kind = kind;
        _reachedEoF = reachedEoF;
        BytesRead = bytesRead;
    }

    /// <summary>Gets the number of bytes copied into the caller's buffer.</summary>
    public int BytesRead { get; }

    /// <summary>
    /// Gets whether the descriptor reached end-of-file while producing this result.
    /// A failure is terminal even when this property is <see langword="false"/>.
    /// </summary>
    public bool ReachedEoF => _kind == ByteReadKind.EndOfFile || _reachedEoF;

    /// <summary>Gets whether the descriptor reported a read failure.</summary>
    public bool IsError => _kind == ByteReadKind.Failure;

    /// <summary>Gets a normal end-of-file result.</summary>
    public static JqByteReadResult EndOfFile => default;

    /// <summary>A terminal read failure. No bytes are delivered.</summary>
    public static JqByteReadResult ReadFailure => new(ByteReadKind.Failure, 0, false);

    /// <summary>Creates a successful read result.</summary>
    public static JqByteReadResult Success(int bytesRead, bool reachedEoF)
    {
        if (bytesRead <= 0)
            throw new ArgumentOutOfRangeException(nameof(bytesRead), "A successful read must copy bytes.");

        return new JqByteReadResult(ByteReadKind.Success, bytesRead, reachedEoF);
    }

    private enum ByteReadKind : byte
    {
        EndOfFile,
        Success,
        Failure
    }
}
