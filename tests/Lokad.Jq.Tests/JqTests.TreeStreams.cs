using System.Text;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(".. | select(type == \"null\")", "null\nnull\n")]
    [InlineData("paths", "[0]\n[1]\n[1,0]\n")]
    [InlineData("tostream", "[[0],null]\n[[1,0],null]\n[[1,0]]\n[[1]]\n")]
    [InlineData("flatten", "[null,null]\n")]
    public async Task Jq_TreeOperationsKeepLongInputStreamsBounded(string source, string expectedRecord)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/input", string.Concat(Enumerable.Repeat("[null,[null]]\n", 2500)));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", source, "/input")));
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat(expectedRecord, 2500)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("try error catch .", "[null,[null]]\n", false)]
    [InlineData("try error catch .", "[null,[null]]\n", true)]
    [InlineData(".. | select(type == \"null\")", "null\n", false)]
    [InlineData(".. | select(type == \"null\")", "null\n", true)]
    [InlineData("paths", "[0]\n", false)]
    [InlineData("paths", "[0]\n", true)]
    [InlineData("tostream", "[[0],null]\n", false)]
    [InlineData("tostream", "[[0],null]\n", true)]
    [InlineData("flatten", "[null,null]\n", false)]
    [InlineData("flatten", "[null,null]\n", true)]
    public async Task Jq_TreeOperationsKeepPendingOutputAdmittedUntilTheHostReturns(string source, string expected, bool cancel)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        var inner = new MockFileSystem { AppendRemainsOpen = false };
        inner.AddFile("/input", "[null,[null]]\n");
        inner.AddFile("/later", "invalid JSON");
        var host = new MemoryBorrowHost(inner);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", source, "/input", "/later")));
        Task<int> execution = command.ExecuteAsync(host, root, cancellation.Token);
        ReadOnlyMemory<byte> pending = await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        long charged = root.ReservedBytes;
        Assert.True(charged > 0);
        Assert.Equal(expectedBytes, pending.ToArray());
        Assert.Equal(1, inner.OpenFileCount);
        if (cancel) cancellation.Cancel();
        Assert.False(execution.IsCompleted);
        Assert.Equal(charged, root.ReservedBytes);
        Assert.Equal(expectedBytes, pending.ToArray());
        host.Release.SetResult();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        else
        {
            Assert.Equal(0, await execution);
            Assert.Equal(expectedBytes, inner.GetOutputBytes(JqFileDescriptor.StdOut));
            Assert.Equal(1, inner.AppendCallCount);
        }
        Assert.Empty(inner.GetOutputBytes(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }
}
