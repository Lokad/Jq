namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("read", false, false)]
    [InlineData("read", false, true)]
    [InlineData("read", true, false)]
    [InlineData("read", true, true)]
    [InlineData("append", false, false)]
    [InlineData("append", false, true)]
    [InlineData("append", true, false)]
    [InlineData("append", true, true)]
    [InlineData("open", false, false)]
    [InlineData("open", false, true)]
    [InlineData("open", true, false)]
    [InlineData("open", true, true)]
    public async Task Jq_HostGuardsPreserveSynchronousAndDeferredContractFailures(string operation, bool deferred, bool overflow)
    {
        Exception sentinel = overflow ? new OverflowException("host sentinel") : new ArgumentException("host sentinel");
        var host = new GuardFailureHost(operation, deferred, sentinel);
        string[] arguments = operation == "open" ? ["-f", "/program"] : ["-c", "."];
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments)));
        Task<int> execution = command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None);
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (deferred) Assert.False(execution.IsCompleted);
        host.Release.SetResult();
        var failure = await Assert.ThrowsAsync<JqHostFailureException>(() => execution);
        Assert.Same(sentinel, failure.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_HostReadGuardsRejectOversizedResults(bool deferred)
    {
        var host = new GuardFailureHost("oversize", deferred, new InvalidOperationException("unused"));
        Task<JqByteReadResult> reading = JqHostExtensions.GuardedReadAsync(host,
            JqFileDescriptor.StdIn, new byte[8], CancellationToken.None).AsTask();
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (deferred) Assert.False(reading.IsCompleted);
        host.Release.SetResult();
        var failure = await Assert.ThrowsAsync<JqHostFailureException>(() => reading);
        Assert.Equal("host read returned more bytes than requested", failure.Message);
        Assert.Null(failure.InnerException);
    }

    private sealed class GuardFailureHost(string operation, bool deferred, Exception sentinel) : IJqHost
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (operation == "oversize")
            {
                Started.SetResult();
                return deferred ? new ValueTask<JqByteReadResult>(OversizedAsync())
                    : ValueTask.FromResult(JqByteReadResult.Success(buffer.Length + 1, true));
            }
            if (operation == "read") return new ValueTask<JqByteReadResult>(Fail<JqByteReadResult>());
            buffer.Span[0] = (byte)'0';
            return ValueTask.FromResult(JqByteReadResult.Success(1, true));

            async Task<JqByteReadResult> OversizedAsync()
            {
                await Release.Task.ConfigureAwait(false);
                return JqByteReadResult.Success(buffer.Length + 1, true);
            }
        }

        public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            operation == "append" ? Fail<JqAppendResult>() : Task.FromResult(JqAppendResult.Open);

        public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken) => Fail<JqOpenedFile>();

        public Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No descriptor was transferred.");

        private Task<T> Fail<T>()
        {
            Started.SetResult();
            if (!deferred) throw sentinel;
            return DeferredAsync();

            async Task<T> DeferredAsync()
            {
                await Release.Task.ConfigureAwait(false);
                throw sentinel;
            }
        }
    }
}
