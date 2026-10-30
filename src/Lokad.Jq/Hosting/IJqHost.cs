namespace Lokad.Jq;

/// <summary>Supplies byte IO and descriptor operations for a jq command.</summary>
public interface IJqHost
{
    /// <summary>
    /// Copies at most buffer.Length bytes. A terminal failure may accompany a valid byte prefix.
    /// A successful read must make progress or reach EOF. Do not retain the buffer after completion.
    /// </summary>
    ValueTask<JqByteReadResult> ReadBytesAsync(
        JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>
    /// Appends bytes, respecting backpressure. The memory is valid until completion; copy it if
    /// retaining it. Distinguish downstream closure from write failure in the returned result.
    /// </summary>
    Task<JqAppendResult> AppendWhileOpenAsync(
        JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>Opens a file for reading. Successful descriptors belong to this execution until closed.</summary>
    Task<JqOpenedFile> OpenReadAsync(JqPathRequest path, CancellationToken cancellationToken);

    /// <summary>
    /// Retires an owned descriptor and reports successful release or a terminal cleanup failure.
    /// Both outcomes end ownership; execution never retries and supplies an uncancelled token.
    /// Standard descriptors supplied by the caller are borrowed and are never closed by the runtime.
    /// </summary>
    Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken);
}

/// <summary>An owned read-only descriptor or a reported error. The default value is invalid.</summary>
public readonly record struct JqOpenedFile
{
    private JqOpenedFile(JqFileDescriptor descriptor)
    {
        FileDescriptor = descriptor;
    }

    private JqOpenedFile(JqOperationError error) => Error = error;

    /// <summary>Gets the owned descriptor on success.</summary>
    public JqFileDescriptor? FileDescriptor { get; }

    /// <summary>Gets the failure cause when no descriptor was transferred.</summary>
    public JqOperationError? Error { get; }

    /// <summary>Creates a successful open result.</summary>
    public static JqOpenedFile Success(JqFileDescriptor descriptor) => new(descriptor);

    /// <summary>A failed read-only open. No descriptor is transferred.</summary>
    public static JqOpenedFile Failure(JqOperationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new JqOpenedFile(error);
    }
}
