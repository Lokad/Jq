namespace Lokad.Jq;

/// <summary>Supplies all byte IO for a jq command; no real filesystem host is installed by default.</summary>
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
    /// Closes an owned descriptor. Return zero after successful release, nonzero if cleanup should
    /// be retried. The runtime retries once and uses an uncancelled token for cleanup.
    /// Standard descriptors supplied by the caller are borrowed and are never closed by the runtime.
    /// </summary>
    Task<int> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken);
}

/// <summary>A successful read-only open, or a host-provided failure description.</summary>
public sealed record JqOpenedFile
{
    private JqOpenedFile(JqFileDescriptor? descriptor, string? error)
    {
        FileDescriptor = descriptor;
        Error = error;
    }

    /// <summary>Gets the owned descriptor on success.</summary>
    public JqFileDescriptor? FileDescriptor { get; }

    /// <summary>Gets the failure description, or null on success.</summary>
    public string? Error { get; }

    /// <summary>Creates a successful open result.</summary>
    public static JqOpenedFile Success(JqFileDescriptor descriptor) => new(descriptor, null);

    /// <summary>Creates a failed open result.</summary>
    public static JqOpenedFile Failure(string error) => new(null, error);
}
