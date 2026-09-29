using System;

namespace Lokad.Jq;

/// <summary>
/// Carries an LF-terminated filesystem or descriptor diagnostic, ready to write without another terminator.
/// </summary>
public readonly record struct JqOperationError
{
    /// <summary>Initializes a diagnostic that includes its LF terminator.</summary>
    public JqOperationError(string message)
    {
        if (!message.EndsWith('\n'))
            throw new ArgumentException("Operation diagnostics must end with LF.", nameof(message));

        Message = message;
    }

    /// <summary>Gets the diagnostic including its LF terminator.</summary>
    public string Message { get; }

    /// <summary>Removes only the framing LF for consumers with an unframed text contract.</summary>
    public string MessageWithoutTerminator => Message[..^1];

    /// <summary>The descriptor is missing, closed, or lacks the requested access.</summary>
    public static JqOperationError BadFileDescriptor => new("Bad file descriptor\n");
}
