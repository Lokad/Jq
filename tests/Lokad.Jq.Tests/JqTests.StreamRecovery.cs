using System.Text.Json;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("truex\n", "Invalid literal at line 2, column 0 (need RS to resync)", "[]", "")]
    [InlineData("[1", "Truncated value at line 1, column 4", "[0]", "")]
    [InlineData("[1,", "Truncated value at line 1, column 5", "[1]", "\u001e[[0],1]\n")]
    [InlineData("[\"tail", "Truncated value at line 1, column 8", "[0]", "")]
    [InlineData("\"unterminated", "Invalid numeric literal at line 1, column 15", "[]", "")]
    [InlineData("\"tail", "Invalid literal at line 1, column 7", "[]", "")]
    [InlineData("{\"k\":", "Truncated value at line 1, column 7", "[\"k\"]", "")]
    [InlineData("[1\n", "Truncated value at line 2, column 1", "[0]", "")]
    [InlineData("1", "", "[]", "\u001e[[],1]\n")]
    [InlineData("true", "", "[]", "\u001e[[],true]\n")]
    public async Task Jq_StreamRecoveryCommitsProgressAtEachReadBoundary(string tail, string warning, string path, string prefix)
    {
        foreach (bool errors in new[] { false, true })
        foreach (bool owned in new[] { false, true })
        foreach (int chunk in new[] { 1, 3, 8192 })
        {
            var inner = new MockFileSystem();
            inner.BeforeByteAppend = () => Assert.True(inner.AppendCallCount < 16, "Recovery replayed buffered input.");
            string input = "\u001e" + tail + "\u001e[9]\n";
            if (owned) inner.AddFile("/records", input);
            else inner.SetStandardInput(input);
            var memory = new BenchmarkMemoryBudget(64 * 1024);
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ["-c", "--seq",
                errors ? "--stream-errors" : "--stream", ".", .. (owned ? new[] { "/records" } : [])])));
            Assert.Equal(0, await command.ExecuteAsync(new ChunkedHost(inner, chunk), Utf8RegexAdapter.Instance, memory, CancellationToken.None));
            string errorEvent = errors && warning.Length != 0 ? "\u001e[" + JsonSerializer.Serialize(warning) + "," + path + "]\n" : "";
            Assert.Equal(prefix + errorEvent + "\u001e[[0],9]\n\u001e[[0]]\n", inner.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal(!errors && warning.Length != 0 ? "jq: ignoring parse error: " + warning + "\n" : "", inner.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(owned ? 1 : 0, inner.ClosedDescriptors.Count);
            Assert.Equal(0, inner.OpenFileCount);
            Assert.Equal(0, memory.ReservedBytes);
            Assert.Equal(0, memory.ActiveScopes);
        }
    }

    [Theory]
    [InlineData("outer", "\u001e[[0],9]\n\u001e[[0]]\n", "jq: ignoring parse error: Invalid literal at line 2, column 0 (need RS to resync)\n")]
    [InlineData("slurp", "\u001e[[[0],9],[[0]]]\n", "jq: ignoring parse error: Invalid literal at line 2, column 0 (need RS to resync)\n")]
    [InlineData("explicit", "\u001e\"caught\"\n\u001e[[0],9]\n\u001e[[0]]\n", "")]
    public async Task Jq_StreamRecoveryPreservesEachInputConsumer(string mode, string output, string error)
    {
        var host = new MockFileSystem();
        host.AddFile("/records", "\u001etruex\n\u001e[9]\n");
        host.BeforeByteAppend = () => Assert.True(host.AppendCallCount < 16, "Recovery replayed buffered input.");
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ["-c", "--seq", "--stream",
            .. (mode == "slurp" ? new[] { "-s" } : mode == "explicit" ? ["-n"] : []),
            mode == "explicit" ? "(try input catch \"caught\"), inputs" : ".", "/records"])));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(output, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(error, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_StreamRecoveryHonorsCancellationAndClosure(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem { AppendRemainsOpen = cancel };
        host.AddFile("/records", "\u001e[7]\n\u001etruex\n\u001e[9]\n");
        if (cancel) host.BeforeByteAppend = () => { if (host.AppendCallCount == 1) cancellation.Cancel(); };
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "--seq", "--stream", ".", "/records")));
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token));
        else Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token));
        Assert.Equal("\u001e[[0],7]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_StreamRecoveryAwaitsWarningBackpressure()
    {
        var inner = new MockFileSystem();
        inner.AddFile("/records", "\u001etruex\n\u001e[9]\n");
        var host = new DelayedOutputHost(inner);
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "--seq", "--stream", ".", "/records")));
        Task<int> execution = command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None);
        ReadOnlyMemory<byte> warning = await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("jq: ignoring parse error: Invalid literal at line 2, column 0 (need RS to resync)\n"u8.ToArray(), warning.ToArray());
        Assert.False(execution.IsCompleted);
        Assert.Equal(1, host.AppendCalls);
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.True(memory.ReservedBytes > 0);
        host.Release.SetResult();
        Assert.Equal(0, await execution);
        Assert.Equal("\u001e[[0],9]\n\u001e[[0]]\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("--stream")]
    [InlineData("--stream-errors")]
    public async Task Jq_StreamRecoveryNeverResynchronizesAdmissionRefusal(string mode)
    {
        var host = new MockFileSystem();
        host.AddFile("/records", "\u001e[7]\n\u001e\"" + new string('x', 128 * 1024) + "\u001e[9]\n");
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "--seq", mode, "try . catch \"caught\"", "/records")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("\u001e[[0],7]\n\u001e[[0]]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }
}
