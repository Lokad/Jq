using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed class HostResultTests
{
    [Fact]
    public void HostResultStatesKeepTheirDistinctMeanings()
    {
        var error = new JqOperationError(JqOperationErrorKind.PermissionDenied, "access denied");
        Assert.Equal(JqOperationErrorKind.PermissionDenied, error.Kind);
        Assert.Equal("access denied", error.Message);
        Assert.Null(JqOpenedFile.Failure(error).FileDescriptor);
        Assert.Same(error, JqOpenedFile.Failure(error).Error);
        Assert.Equal(new JqFileDescriptor(3), JqOpenedFile.Success(new JqFileDescriptor(3)).FileDescriptor);
        Assert.True(default(JqByteReadResult).ReachedEoF);
        Assert.False(JqByteReadResult.EndOfFile.IsError);
        var partial = JqByteReadResult.Failure(3, error);
        Assert.True(partial.IsError);
        Assert.Equal(3, partial.BytesRead);
        Assert.False(partial.ReachedEoF);
        Assert.Same(error, partial.Error);
        Assert.Equal(3, JqByteReadResult.Success(3, true).BytesRead);
        Assert.True(JqByteReadResult.Success(3, true).ReachedEoF);
        Assert.False(JqByteReadResult.Success(3, false).ReachedEoF);
        Assert.Throws<ArgumentOutOfRangeException>(() => JqByteReadResult.Success(0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => JqByteReadResult.Failure(-1, error));
        Assert.True(JqAppendResult.Open.CanAcceptMore);
        Assert.False(default(JqAppendResult).CanAcceptMore);
        Assert.False(JqAppendResult.Closed.IsError);
        Assert.True(JqAppendResult.Failure(error).IsError);
        Assert.Same(error, JqAppendResult.Failure(error).Error);
        Assert.Null(default(JqCloseResult).Error);
        Assert.Same(error, JqCloseResult.Failure(error).Error);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task FailureAndClosurePreserveStatusAndReleaseOwnedInput(bool readFailure, int appendStatus)
    {
        var inner = new MockFileSystem();
        inner.AddFile("/input", "42");
        var host = new ResultHost(inner, readFailure, appendStatus);
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors(
            "jq", ["-n", "inputs", "/input"], [])));

        Assert.Equal(readFailure ? 5 : appendStatus, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(readFailure ? "" : "42\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(readFailure ? "jq: error reading /input: storage unavailable\n" : appendStatus == 0 ? "" : "jq: error writing <stdout>: write failed\n", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    private sealed class ResultHost(MockFileSystem inner, bool readFailure, int appendStatus) : IJqHost
    {
        public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken) =>
            readFailure ? ValueTask.FromResult(JqByteReadResult.Failure(0, new JqOperationError(JqOperationErrorKind.TransportFailure, "storage unavailable"))) : inner.ReadBytesAsync(descriptor, buffer, cancellationToken);

        public async Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            await inner.AppendWhileOpenAsync(descriptor, content, cancellationToken).ConfigureAwait(false);
            return descriptor != JqFileDescriptor.StdOut ? JqAppendResult.Open
                : appendStatus == 0 ? JqAppendResult.Closed : JqAppendResult.Failure(new JqOperationError(JqOperationErrorKind.TransportFailure, "write failed"));
        }

        public Task<JqOpenedFile> OpenReadAsync(JqPathRequest path, CancellationToken cancellationToken) => inner.OpenReadAsync(path, cancellationToken);
        public Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken) => inner.CloseDescriptorAsync(descriptor, cancellationToken);
    }

    [Theory]
    [InlineData(JqOperationErrorKind.NotFound, "path not found")]
    [InlineData(JqOperationErrorKind.PermissionDenied, "access denied")]
    [InlineData(JqOperationErrorKind.InvalidPathKind, "not a directory")]
    [InlineData(JqOperationErrorKind.NotSupported, "operation unavailable")]
    [InlineData(JqOperationErrorKind.TransportFailure, "connection lost")]
    public async Task OpenFailuresRetainTheirHostCause(JqOperationErrorKind kind, string message)
    {
        var inner = new MockFileSystem();
        var host = new OpenErrorHost(inner, new JqOperationError(kind, message));
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors(
            "jq", ["-n", "try inputs catch \"caught\"", "/input"], [])));
        Assert.Equal(2, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: cannot open /input: " + message + "\n", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(inner.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }

    private sealed class OpenErrorHost(MockFileSystem inner, JqOperationError error) : IJqHost
    {
        public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken token) =>
            throw new InvalidOperationException("A failed open must not be read.");
        public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken token) =>
            inner.AppendWhileOpenAsync(descriptor, content, token);
        public Task<JqOpenedFile> OpenReadAsync(JqPathRequest path, CancellationToken token) => Task.FromResult(JqOpenedFile.Failure(error));
        public Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken token) =>
            throw new InvalidOperationException("A failed open transfers no descriptor.");
    }
}
