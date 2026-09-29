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
    private readonly int _failureExitCode;
    private readonly bool _reachedEoF;

    private JqByteReadResult(
        ByteReadKind kind,
        int bytesRead,
        int failureExitCode,
        JqOperationError? error,
        bool reachedEoF)
    {
        _kind = kind;
        _failureExitCode = failureExitCode;
        Error = error;
        _reachedEoF = reachedEoF;
        BytesRead = bytesRead;
    }

    /// <summary>Gets the number of bytes copied into the caller's buffer.</summary>
    public int BytesRead { get; }

    /// <summary>Gets zero for a successful read or normal end-of-file, or a positive failure status.</summary>
    public int ExitCode => _kind switch
    {
        ByteReadKind.Success => 0,
        ByteReadKind.Failure => _failureExitCode,
        _ => 0
    };

    /// <summary>
    /// Gets whether the descriptor reached end-of-file while producing this result.
    /// A failure is terminal even when this property is <see langword="false"/>.
    /// </summary>
    public bool ReachedEoF => _kind == ByteReadKind.EndOfFile || _reachedEoF;

    /// <summary>Gets whether the descriptor reported a read failure.</summary>
    public bool IsError => _kind == ByteReadKind.Failure;

    /// <summary>The known failure cause; null for success or a failure whose cause is unknown.</summary>
    /// <remarks>A numeric status alone does not identify the cause.</remarks>
    public JqOperationError? Error { get; }

    /// <summary>Gets a normal end-of-file result.</summary>
    public static JqByteReadResult EndOfFile => default;

    /// <summary>A closed, missing or unreadable descriptor, with exit code 1 and no bytes.</summary>
    public static JqByteReadResult ReadFailure =>
        Failure(0, 1, JqOperationError.BadFileDescriptor, reachedEoF: true);

    /// <summary>Creates a successful read result.</summary>
    public static JqByteReadResult Success(int bytesRead, bool reachedEoF)
    {
        if (bytesRead <= 0)
            throw new ArgumentOutOfRangeException(nameof(bytesRead), "A successful read must copy bytes.");

        return new JqByteReadResult(ByteReadKind.Success, bytesRead, 0, error: null, reachedEoF);
    }

    /// <summary>Creates a failed read result, optionally preserving bytes read before the failure.</summary>
    public static JqByteReadResult Failure(int bytesRead, int exitCode, JqOperationError? error, bool reachedEoF)
    {
        if (bytesRead < 0)
            throw new ArgumentOutOfRangeException(nameof(bytesRead), "The byte count cannot be negative.");
        if (exitCode <= 0)
            throw new ArgumentOutOfRangeException(nameof(exitCode), "A failed read must have a positive exit code.");

        return new JqByteReadResult(ByteReadKind.Failure, bytesRead, exitCode, error, reachedEoF);
    }

    private enum ByteReadKind : byte
    {
        EndOfFile,
        Success,
        Failure
    }
}
