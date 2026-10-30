using System;

namespace Lokad.Jq;

/// <summary>Distinguishes an open append, downstream closure, and failure.</summary>
public readonly record struct JqAppendResult
{
    private readonly DescriptorAppendKind _kind;

    private JqAppendResult(DescriptorAppendKind kind, JqOperationError? error)
    {
        _kind = kind;
        Error = error;
    }

    /// <summary>Gets whether the descriptor can accept more content.</summary>
    public bool CanAcceptMore => _kind == DescriptorAppendKind.Open;

    /// <summary>Gets whether the append failed, rather than ending because the downstream reader closed.</summary>
    public bool IsError => _kind == DescriptorAppendKind.Failure;

    /// <summary>Gets the failure cause, independently of jq command status.</summary>
    public JqOperationError? Error { get; }

    internal int ExitCode => IsError ? 1 : 0;

    /// <summary>Gets a successful append result for a descriptor that remains open.</summary>
    public static JqAppendResult Open => new(DescriptorAppendKind.Open, null);

    /// <summary>Gets a successful append result for a descriptor whose downstream reader closed.</summary>
    public static JqAppendResult Closed => default;

    /// <summary>Creates a failed append result.</summary>
    public static JqAppendResult Failure(JqOperationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new JqAppendResult(DescriptorAppendKind.Failure, error);
    }

    private enum DescriptorAppendKind : byte
    {
        Closed,
        Open,
        Failure
    }
}
