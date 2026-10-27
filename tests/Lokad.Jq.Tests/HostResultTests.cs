using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed class HostResultTests
{
    [Fact]
    public void HostResultStatesKeepTheirDistinctMeanings()
    {
        Assert.Null(default(JqOpenedFile).FileDescriptor);
        Assert.Null(JqOpenedFile.OpenFailure.FileDescriptor);
        Assert.Equal(new JqFileDescriptor(3), JqOpenedFile.Success(new JqFileDescriptor(3)).FileDescriptor);
        Assert.True(default(JqByteReadResult).ReachedEoF);
        Assert.False(JqByteReadResult.EndOfFile.IsError);
        Assert.True(JqByteReadResult.ReadFailure.IsError);
        Assert.Equal(0, JqByteReadResult.ReadFailure.BytesRead);
        Assert.False(JqByteReadResult.ReadFailure.ReachedEoF);
        Assert.Equal(3, JqByteReadResult.Success(3, true).BytesRead);
        Assert.True(JqByteReadResult.Success(3, true).ReachedEoF);
        Assert.False(JqByteReadResult.Success(3, false).ReachedEoF);
        Assert.Throws<ArgumentOutOfRangeException>(() => JqByteReadResult.Success(0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => JqByteReadResult.Success(-1, true));
        Assert.True(JqAppendResult.Open.CanAcceptMore);
        Assert.False(default(JqAppendResult).CanAcceptMore);
        Assert.False(JqAppendResult.Closed.IsError);
        Assert.Equal(0, JqAppendResult.Closed.ExitCode);
        Assert.True(JqAppendResult.Failure(-7).IsError);
        Assert.Equal(-7, JqAppendResult.Failure(-7).ExitCode);
        Assert.Equal(1, JqAppendResult.WriteFailure.ExitCode);
        Assert.Throws<ArgumentOutOfRangeException>(() => JqAppendResult.Failure(0));
        Assert.Null(default(JqCloseResult).Error);
        Assert.Null(JqCloseResult.Success.Error);
        var closeError = new JqOperationError("storage unavailable");
        Assert.Same(closeError, JqCloseResult.Failure(closeError).Error);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 7)]
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
        Assert.Equal(readFailure ? "jq: error (at /input:0): input read failed\n" : "", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    private sealed class ResultHost(MockFileSystem inner, bool readFailure, int appendStatus) : IJqHost
    {
        public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken) =>
            readFailure ? ValueTask.FromResult(JqByteReadResult.ReadFailure) : inner.ReadBytesAsync(descriptor, buffer, cancellationToken);

        public async Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            await inner.AppendWhileOpenAsync(descriptor, content, cancellationToken).ConfigureAwait(false);
            return descriptor != JqFileDescriptor.StdOut ? JqAppendResult.Open
                : appendStatus == 0 ? JqAppendResult.Closed : JqAppendResult.Failure(appendStatus);
        }

        public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken) => inner.OpenReadAsync(path, cancellationToken);
        public Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken) => inner.CloseDescriptorAsync(descriptor, cancellationToken);
    }
}
