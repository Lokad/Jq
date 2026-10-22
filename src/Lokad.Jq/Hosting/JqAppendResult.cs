using System;

namespace Lokad.Jq;

/// <summary>Distinguishes an open append, downstream closure, and failure.</summary>
public readonly record struct JqAppendResult
{
    private readonly DescriptorAppendKind _kind;
    private readonly int _failureExitCode;

    private JqAppendResult(DescriptorAppendKind kind, int failureExitCode)
    {
        _kind = kind;
        _failureExitCode = failureExitCode;
    }

    /// <summary>Gets whether the descriptor can accept more content.</summary>
    public bool CanAcceptMore => _kind == DescriptorAppendKind.Open;

    /// <summary>Gets whether the append failed, rather than ending because the downstream reader closed.</summary>
    public bool IsError => _kind == DescriptorAppendKind.Failure;

    /// <summary>Gets zero for success or downstream closure, or the non-zero append failure status.</summary>
    public int ExitCode => IsError ? _failureExitCode : 0;

    /// <summary>Gets a successful append result for a descriptor that remains open.</summary>
    public static JqAppendResult Open => new(DescriptorAppendKind.Open, 0);

    /// <summary>Gets a successful append result for a descriptor whose downstream reader closed.</summary>
    public static JqAppendResult Closed => default;

    /// <summary>Gets a failure for a missing, closed, or non-writable descriptor.</summary>
    public static JqAppendResult WriteFailure => Failure(1);

    /// <summary>Creates a failed append result.</summary>
    public static JqAppendResult Failure(int exitCode)
    {
        if (exitCode == 0)
            throw new ArgumentOutOfRangeException(nameof(exitCode), "A failed append must have a non-zero exit code.");

        return new JqAppendResult(DescriptorAppendKind.Failure, exitCode);
    }

    private enum DescriptorAppendKind : byte
    {
        Closed,
        Open,
        Failure
    }
}
