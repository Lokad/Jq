namespace Lokad.Jq;

/// <summary>A host-reported operation failure, distinct from an unexpected exception.</summary>
public sealed record JqOperationError
{
    /// <summary>Initializes the host detail; the caller supplies operation and path context.</summary>
    public JqOperationError(string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        Message = message;
    }

    /// <summary>Gets the host's failure detail.</summary>
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
