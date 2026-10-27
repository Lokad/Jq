using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(0, "\"x\" * 1000000")]
    [InlineData(1, "\"x\" * 1000000")]
    [InlineData(2, "\"x\" * 1000000")]
    [InlineData(0, "try (\"x\" * 1000000) catch \"caught\"")]
    [InlineData(1, "try (\"x\" * 1000000) catch \"caught\"")]
    [InlineData(2, "try (\"x\" * 1000000) catch \"caught\"")]
    [InlineData(0, "(\"x\" * 1000000)?")]
    [InlineData(1, "(\"x\" * 1000000)?")]
    [InlineData(2, "(\"x\" * 1000000)?")]
    public async Task Jq_GovernedRefusalKeepsResourceStatusWhenDiagnosticOutputStops(int writeResult, string expression)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using var caller = root.CreateScope();
        Assert.True(caller.TryReserve(7, out var retained));
        using (retained)
        {
            var memory = new CountingMemoryBudget(caller);
            var inner = new MockFileSystem();
            inner.AddFile("/first", "0\n");
            inner.AddFile("/later", "1\n");
            var host = new MemoryBorrowHost(inner)
            {
                StderrResult = writeResult switch
                {
                    0 => JqAppendResult.Open,
                    1 => JqAppendResult.Closed,
                    _ => JqAppendResult.WriteFailure,
                },
            };
            host.Release.SetResult();
            string filter = "\"prefix\", " + expression + ", (\"later\" | stderr)";
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", filter, "/first", "/later")));

            Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
            Assert.Equal("prefix\n", inner.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal("jq: memory budget exceeded\n",
                inner.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(2, inner.AppendCallCount);
            Assert.Equal(1, memory.Counts.Refusals);
            Assert.Equal(1, inner.ReadBytesCallCount);
            Assert.Single(inner.ClosedDescriptors);
            Assert.Equal(0, inner.OpenFileCount);
            Assert.Equal(retained.Bytes, root.ReservedBytes);

            var laterHost = new MockFileSystem();
            var laterCommand = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "42")));
            Assert.Equal(0, await laterCommand.ExecuteAsync(laterHost, Utf8RegexAdapter.Instance, caller, CancellationToken.None));
            Assert.Equal("42\n", laterHost.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(laterHost.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(retained.Bytes, root.ReservedBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_GovernedRefusalAwaitsOwnedCleanupAndPendingStderrBeforeRefund(bool cancel)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        var inner = new MockFileSystem();
        inner.AddFile("/input", "0\n");
        var host = new MemoryBorrowHost(inner)
        {
            DelayCleanup = true,
            DelayedDescriptor = JqFileDescriptor.StdErr,
            StderrResult = JqAppendResult.Closed,
        };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            "\"prefix\", (\"x\" * 1000000)", "/input")));
        Task<int> execution = command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token);
        await host.Closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(execution.IsCompleted);
        Assert.True(memory.ReservedBytes > 0);
        Assert.Equal("prefix\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        host.CloseRelease.SetResult();

        ReadOnlyMemory<byte> pending = await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("jq: memory budget exceeded\n"u8.ToArray(), pending.ToArray());
        long charged = memory.ReservedBytes;
        Assert.True(charged >= pending.Length);
        if (cancel) cancellation.Cancel();
        Assert.False(execution.IsCompleted);
        Assert.Equal(charged, memory.ReservedBytes);
        host.Release.SetResult();
        Assert.Equal(5, await execution);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(cancel ? "" : "jq: memory budget exceeded\n", inner.GetOutput(JqFileDescriptor.StdErr));
    }
}
