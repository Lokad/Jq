using System.Globalization;
using System.Text;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("-M", "\"é\"\n7\nnull\n")]
    [InlineData("-a", "\"\\u00e9\"\n7\nnull\n")]
    [InlineData("-j", "é7null")]
    [InlineData("--raw-output0", "é\07\0null\0")]
    [InlineData("--seq", "\u001e\"é\"\n\u001e7\n\u001enull\n")]
    public async Task Jq_OutputReusePreservesRecordFraming(string option, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput((option == "--seq" ? "\u001e" : "") + "[\"é\",7,null]");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", option, ".[]")));

        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(Encoding.UTF8.GetBytes(expected), host.GetOutputBytes(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutputBytes(JqFileDescriptor.StdErr));
        Assert.Equal(3, host.AppendCallCount);
    }

    [Fact]
    public async Task Jq_OutputReuseHandlesGrowthAndNormalizedPrettyRecords()
    {
        var host = new MockFileSystem();
        string longText = new('x', 20000);
        host.SetStandardInput("\u001e[\"" + longText + "\",{\"b\":\"é🚀\",\"a\":[null]},0]");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "--seq", "--tab", "-S", "-a", ".[]")));

        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\u001e\"" + longText + "\"\n\u001e{\n\t\"a\": [\n\t\tnull\n\t],\n\t\"b\": \"\\u00e9\\ud83d\\ude80\"\n}\n\u001e0\n",
            host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(3, host.AppendCallCount);
    }

    [Fact]
    public async Task Jq_OutputReuseChargesBufferGrowthOnceWithinAnExecution()
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "range(0;1024)")));

        Assert.Equal(0, await command.ExecuteAsync(host,
            new JqExecutionPolicy { MaximumAllocationBytes = 65536 }, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Range(0, 1024)
            .Select(i => i.ToString(CultureInfo.InvariantCulture) + "\n")), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1024, host.AppendCallCount);
    }

    [Fact]
    public async Task Jq_OutputReuseKeepsQuotaFailureBeforeTheNextRecord()
    {
        var host = new MockFileSystem();
        host.AddFile("/data", "[null,false]");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".[]", "/data")));

        Assert.Equal(5, await command.ExecuteAsync(host,
            new JqExecutionPolicy { MaximumOutputBytes = 5 }, CancellationToken.None));
        Assert.Equal("null\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: output exceeds the 5-byte limit\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Jq_OutputMemoryQuotaDoesNotEscapeDuringWriterCleanup()
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "[range(0;2000)]")));

        Assert.Equal(5, await command.ExecuteAsync(host,
            new JqExecutionPolicy { MaximumAllocationBytes = 4096 }, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_OutputReuseWaitsForTheHostBeforeChangingRecordMemory(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var inner = new MockFileSystem();
        string text = new('x', 20000);
        inner.AddFile("/data", "[\"" + text + "\",7]");
        var host = new DelayedOutputHost(inner);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", ".[]", "/data")));

        Task<int> execution = command.ExecuteAsync(host, cancellation.Token);
        ReadOnlyMemory<byte> pending = await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(execution.IsCompleted);
        Assert.Equal(1, host.AppendCalls);
        Assert.Equal(Encoding.UTF8.GetBytes("\"" + text + "\"\n"), pending.ToArray());
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        }
        else
        {
            host.Release.SetResult();
            Assert.Equal(0, await execution);
            Assert.Equal("\"" + text + "\"\n7\n", inner.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal(2, host.AppendCalls);
        }
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
    }

    private sealed class DelayedOutputHost(MockFileSystem inner) : IJqHost
    {
        internal TaskCompletionSource<ReadOnlyMemory<byte>> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int AppendCalls { get; private set; }

        public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer,
            CancellationToken cancellationToken) => inner.ReadBytesAsync(descriptor, buffer, cancellationToken);

        public async Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            AppendCalls++;
            if (AppendCalls == 1)
            {
                Ready.SetResult(content);
                await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return await inner.AppendWhileOpenAsync(descriptor, content, cancellationToken).ConfigureAwait(false);
        }

        public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(path, cancellationToken);

        public Task<int> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken) =>
            inner.CloseDescriptorAsync(descriptor, cancellationToken);
    }
}
