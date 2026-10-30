namespace Lokad.Jq;

/// <summary>Portable causes for expected host operation failures.</summary>
public enum JqOperationErrorKind
{
    /// <summary>A failure without a more specific portable cause.</summary>
    Other,
    /// <summary>A requested path does not exist.</summary>
    NotFound,
    /// <summary>The caller lacks permission.</summary>
    PermissionDenied,
    /// <summary>A path component has the wrong kind, such as a file used as a directory.</summary>
    InvalidPathKind,
    /// <summary>The host does not support the operation.</summary>
    NotSupported,
    /// <summary>An underlying transport or storage operation failed.</summary>
    TransportFailure
}

/// <summary>A host-reported operation failure, distinct from an unexpected exception.</summary>
public sealed record JqOperationError
{
    /// <summary>Initializes the host detail; the caller supplies operation and path context.</summary>
    public JqOperationError(JqOperationErrorKind kind, string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
        Message = message;
    }

    /// <summary>Gets the portable cause.</summary>
    public JqOperationErrorKind Kind { get; }

    /// <summary>Gets the host's safe, unprefixed failure detail, suitable for diagnostics.</summary>
    public string Message { get; }
}

/// <summary>A terminal close outcome. The descriptor is retired on success or failure.</summary>
public readonly record struct JqCloseResult
{
    private JqCloseResult(JqOperationError error) => Error = error;

    /// <summary>Gets a reported cleanup failure, or null after successful release.</summary>
    public JqOperationError? Error { get; }

    /// <summary>Successful retirement. The default value also denotes success.</summary>
    public static JqCloseResult Success => default;

    /// <summary>Reports a failure after retiring the descriptor; execution must not retry.</summary>
    public static JqCloseResult Failure(JqOperationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new JqCloseResult(error);
    }
}
