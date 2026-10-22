namespace Lokad.Jq;

/// <summary>Supplies byte IO and descriptor operations for a jq command.</summary>
public interface IJqHost
{
    /// <summary>
    /// Copies at most buffer.Length bytes. Return EOF or a failure when no bytes are available;
    /// a successful read must make progress. Do not retain the buffer after completion.
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
    Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken);

    /// <summary>
    /// Retires an owned descriptor and reports successful release or a terminal cleanup failure.
    /// Both outcomes end ownership; execution never retries and supplies an uncancelled token.
    /// Standard descriptors supplied by the caller are borrowed and are never closed by the runtime.
    /// </summary>
    Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken);
}

/// <summary>A successful read-only open, or a failure. The default value is a failure.</summary>
public readonly record struct JqOpenedFile
{
    private JqOpenedFile(JqFileDescriptor descriptor)
    {
        FileDescriptor = descriptor;
    }

    /// <summary>Gets the owned descriptor on success.</summary>
    public JqFileDescriptor? FileDescriptor { get; }

    /// <summary>Creates a successful open result.</summary>
    public static JqOpenedFile Success(JqFileDescriptor descriptor) => new(descriptor);

    /// <summary>A failed read-only open. No descriptor is transferred.</summary>
    public static JqOpenedFile OpenFailure => default;
}
