using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false, 257)]
    [InlineData(true, 257)]
    [InlineData(false, 8192)]
    [InlineData(true, 8192)]
    public async Task Jq_ParsedInputPrefixesDoNotAccumulateAcrossTokensOrRecords(bool escaped, int chunk)
    {
        string text = escaped ? string.Concat(Enumerable.Repeat("\\u0061", 1024)) : new string('a', 1024);
        string record = "[" + string.Join(',', Enumerable.Repeat("\"" + text + "\"", 512)) + "]\n";
        var inner = new MockFileSystem();
        inner.AddFile("/input", record + record);
        var root = new BenchmarkMemoryBudget(4 * 1024 * 1024);
        var memory = new CountingMemoryBudget(root);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "length", "/input")));

        Assert.Equal(0, await command.ExecuteAsync(new ChunkedHost(inner, chunk), Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("512\n512\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.Counts.Refusals);
        Assert.Equal(0, memory.Counts.SameSizeResizes);
        Assert.Equal(0, root.ReservedBytes);
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
    }

    [Theory]
    [InlineData("[0 nan]")]
    [InlineData("[0 \n +Infinity]")]
    [InlineData("[{} -Inf]")]
    [InlineData("[\"x\" NaN]")]
    [InlineData("{\"a\":0 \"b\":nan}")]
    [InlineData("{\"a\":0,\"b\": \n nanx")]
    [InlineData("[0,\n1,\n+infinityx")]
    [InlineData("[0,\n1,\n2}")]
    [InlineData("[0,\n1,\n")]
    public async Task Jq_RetiredInputKeepsDelimiterValidationAndErrorPositions(string input)
    {
        foreach (bool explicitInput in new[] { false, true })
        {
            string[] arguments = explicitInput ? ["-nc", "input"] : ["-c", "."];
            var whole = new MockFileSystem();
            whole.SetStandardInput(input);
            var expected = await RunInputAsync(whole, arguments);
            Assert.Equal(5, expected.Exit);
            foreach (int chunk in new[] { 1, 2, 3, 7 })
            {
                var inner = new MockFileSystem();
                inner.SetStandardInput(input);
                Assert.Equal(expected, await RunInputAsync(new ChunkedHost(inner, chunk), arguments));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_ParsedInputPrefixStorageIsBoundedDuringAPendingRead(bool cancel)
    {
        var inner = new MockFileSystem();
        string item = "\"" + string.Concat(Enumerable.Repeat("\\u0061", 1024)) + "\"";
        inner.AddFile("/input", "[" + string.Join(',', Enumerable.Repeat(item, 512)) + "]");
        inner.AddFile("/later", "0");
        var host = new InputProgressHost(inner, 128);
        var memory = new BenchmarkMemoryBudget(4 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "length", "/input", "/later")));
        Task<int> execution = command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token);
        await host.Pending.Task.WaitAsync(TimeSpan.FromSeconds(10));
        long pending = memory.ReservedBytes;
        Assert.InRange(pending, 1, 2 * 1024 * 1024);
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        if (cancel) cancellation.Cancel();
        Assert.False(execution.IsCompleted);
        Assert.Equal(pending, memory.ReservedBytes);
        host.Release.SetResult();
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        else
            Assert.Equal(0, await execution);
        Assert.Equal(cancel ? "" : "512\n0\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(cancel ? 1 : 2, inner.ClosedDescriptors.Count);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    private sealed class InputProgressHost(MockFileSystem inner, int reads) : IJqHost
    {
        internal TaskCompletionSource Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (inner.ReadBytesCallCount == reads)
            {
                Pending.TrySetResult();
                // Keep the host borrow until completion, including after cancellation.
                await Release.Task.ConfigureAwait(false);
            }
            return await inner.ReadBytesAsync(descriptor, buffer, cancellationToken).ConfigureAwait(false);
        }

        public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            inner.AppendWhileOpenAsync(descriptor, content, cancellationToken);
        public Task<JqOpenedFile> OpenReadAsync(JqPathRequest path, CancellationToken cancellationToken) => inner.OpenReadAsync(path, cancellationToken);
        public Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken) => inner.CloseDescriptorAsync(descriptor, cancellationToken);
    }
}
