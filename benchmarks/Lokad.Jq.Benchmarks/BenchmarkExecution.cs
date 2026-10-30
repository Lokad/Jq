using System.Security.Cryptography;
using System.Text;

namespace Lokad.Jq.Benchmarking;

internal sealed record ExecutionResult(int ExitCode, byte[] Output, byte[] Error, OutputDigest Digest)
{
    public bool IsSuccess => ExitCode == 0 && Error.Length == 0;
}

internal sealed record OutputDigest(long Length, string Sha256)
{
    public static OutputDigest FromBytes(byte[] bytes) =>
        new(bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));
}

// Development-only byte host. Capture is used for correctness preflight; timing
// consumes and hashes stdout without retaining it. Diagnostics stay separate.
internal sealed class BenchmarkHost : IJqHost, IDisposable
{
    private ReadOnlyMemory<byte> _remaining;
    private readonly bool _capture;
    private readonly long _maximumOutputBytes;
    private readonly long _maximumErrorBytes;
    private readonly MemoryStream _output = new();
    private readonly MemoryStream _error = new();
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _length;

    public BenchmarkHost(byte[] input, bool capture)
        : this(input, capture, 1024 * 1024) { }

    internal BenchmarkHost(byte[] input, bool capture, long maximumErrorBytes)
        : this(input, capture, 32 * 1024 * 1024, maximumErrorBytes) { }

    internal BenchmarkHost(byte[] input, bool capture, long maximumOutputBytes, long maximumErrorBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumOutputBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumOutputBytes, 32 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumErrorBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumErrorBytes, 32 * 1024 * 1024);
        _remaining = input;
        _capture = capture;
        _maximumOutputBytes = maximumOutputBytes;
        _maximumErrorBytes = maximumErrorBytes;
    }

    public ExecutionResult Finish(int exitCode) => new(exitCode, _output.ToArray(), _error.ToArray(),
        new OutputDigest(_length, Convert.ToHexStringLower(_hash.GetHashAndReset())));

    public ValueTask<JqByteReadResult> ReadBytesAsync(
        JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (descriptor != JqFileDescriptor.StdIn)
            return ValueTask.FromResult(JqByteReadResult.Failure(0, new JqOperationError(JqOperationErrorKind.TransportFailure, "storage unavailable")));
        if (_remaining.IsEmpty)
            return ValueTask.FromResult(JqByteReadResult.EndOfFile);
        int count = Math.Min(buffer.Length, _remaining.Length);
        _remaining[..count].CopyTo(buffer);
        _remaining = _remaining[count..];
        return ValueTask.FromResult(JqByteReadResult.Success(count, _remaining.IsEmpty));
    }

    public Task<JqAppendResult> AppendWhileOpenAsync(
        JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (descriptor == JqFileDescriptor.StdOut)
        {
            if (_length + content.Length > _maximumOutputBytes)
                throw new InvalidOperationException("Benchmark stdout exceeds its capture/consumption bound.");
            _hash.AppendData(content.Span);
            _length += content.Length;
            if (_capture) _output.Write(content.Span);
        }
        else if (descriptor == JqFileDescriptor.StdErr)
        {
            if (_error.Length + content.Length > _maximumErrorBytes)
                throw new InvalidOperationException("Benchmark stderr exceeds its capture bound.");
            _error.Write(content.Span);
        }
        else return Task.FromResult(JqAppendResult.Failure(new JqOperationError(JqOperationErrorKind.TransportFailure, "write failed")));
        return Task.FromResult(JqAppendResult.Open);
    }

    public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(JqOpenedFile.Failure(new JqOperationError(JqOperationErrorKind.NotFound, "path not found")));
    }

    public Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Benchmark standard descriptors are borrowed.");

    public void Dispose()
    {
        _hash.Dispose();
        _output.Dispose();
        _error.Dispose();
    }
}

internal static class BenchmarkExecution
{
    public static Jq Recognize(string[] arguments) =>
        Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", arguments, []))
        ?? throw new InvalidOperationException("Expected jq command.");

    public static async Task<ExecutionResult> RunAsync(
        string[] arguments, byte[] input, bool capture, CancellationToken cancellationToken)
    {
        using var host = new BenchmarkHost(input, capture);
        var command = Recognize(arguments);
        int exit = await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, cancellationToken).ConfigureAwait(false);
        return host.Finish(exit);
    }

    public static void Verify(ExecutionResult result, OutputDigest expected)
    {
        if (!result.IsSuccess || result.Digest != expected)
            throw new InvalidOperationException($"Benchmark result failed: exit {result.ExitCode}, " +
                $"stdout {result.Digest.Length} bytes, stderr: {Encoding.UTF8.GetString(result.Error)}");
    }
}
