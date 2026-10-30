using System.Text;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_InvalidJsonStopsBeforePendingSuffixOrLaterFile(bool explicitInput)
    {
        var inner = new MockFileSystem();
        inner.AddFile("/first", "[!\n" + new string(' ', 1000000));
        inner.AddFile("/later", "42\n");
        var host = new PendingSuffixHost(inner, 3);
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var counted = new CountingMemoryBudget(memory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq",
            explicitInput ? "-nc" : "-c", explicitInput ? "input" : ".", "/first", "/later")));

        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, counted, cancellation.Token));
        Assert.Equal((explicitInput ? "jq: error (at /first:1): " : "jq: parse error: ")
            + "'!' is an invalid start of a value. LineNumber: 0 | BytePositionInLine: 1.\n",
            inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.False(host.PendingRead.Task.IsCompleted);
        Assert.Equal(1, inner.ReadBytesCallCount);
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, counted.Counts.Refusals);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("[1,\n2]", 4, "[1,2]\n")]
    [InlineData("[+InF,NaN]", 3, "[1.7976931348623157E+308,null]\n")]
    [InlineData("{\"x\":-Infinity}", 8, "{\"x\":-1.7976931348623157E+308}\n")]
    [InlineData("\"é🚀\"", 5, "\"é\\ud83d\\ude80\"\n")]
    public async Task Jq_IncompleteJsonSuspendsForItsSuffix(string input, int prefixBytes, string expected)
    {
        var inner = new MockFileSystem();
        inner.AddFile("/input", input);
        var host = new PendingSuffixHost(inner, prefixBytes);
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", ".", "/input")));
        Task<int> execution = command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token);
        await host.PendingRead.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            Assert.False(execution.IsCompleted);
            Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
            Assert.True(memory.ReservedBytes > 0);
        }
        finally { host.Release.TrySetResult(); }

        Assert.Equal(0, await execution);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), inner.GetOutputBytes(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_IncompleteJsonCancellationClosesPendingOwnedInput()
    {
        var inner = new MockFileSystem();
        inner.AddFile("/input", "[1,\n2]");
        var host = new PendingSuffixHost(inner, 4);
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "try input catch .", "/input")));
        Task<int> execution = command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token);
        await host.PendingRead.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    private sealed class PendingSuffixHost(MockFileSystem inner, int prefixBytes) : IJqHost
    {
        internal TaskCompletionSource PendingRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _first = true;

        public async ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_first)
            {
                _first = false;
                JqByteReadResult result = await inner.ReadBytesAsync(descriptor, buffer[..prefixBytes], cancellationToken).ConfigureAwait(false);
                return JqByteReadResult.Success(result.BytesRead, false);
            }
            PendingRead.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await inner.ReadBytesAsync(descriptor, buffer, cancellationToken).ConfigureAwait(false);
        }

        public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => inner.AppendWhileOpenAsync(descriptor, content, cancellationToken);
        public Task<JqOpenedFile> OpenReadAsync(JqPathRequest path, CancellationToken cancellationToken) => inner.OpenReadAsync(path, cancellationToken);
        public Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken) => inner.CloseDescriptorAsync(descriptor, cancellationToken);
    }
}
