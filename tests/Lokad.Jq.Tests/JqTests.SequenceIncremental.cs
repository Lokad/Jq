using System.Text;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("\u001e1 \"tail\u001e2\n", "\u001e1\n\u001e2\n", "Truncated value at line 1, column 9")]
    [InlineData("\u001e1 [2,\u001e3\n", "\u001e1\n\u001e3\n", "Truncated value at line 1, column 7")]
    [InlineData("\u001e{\"a\":1} [2,\u001e3\n", "\u001e{\"a\":1}\n\u001e3\n", "Truncated value at line 1, column 13")]
    [InlineData("\u001e1 \"tail", "\u001e1\n", "Unfinished string at EOF at line 1, column 8")]
    [InlineData("\u001e1 [2,", "\u001e1\n", "Unfinished JSON term at EOF at line 1, column 6")]
    public async Task Jq_SequencePublishesCompleteValuesBeforeTruncatedTail(string input, string expected, string warning)
    {
        foreach (int chunk in new[] { 1, 2, 3, 8192 })
        {
            var inner = new MockFileSystem();
            inner.SetStandardInput(input);
            var memory = new BenchmarkMemoryBudget(64 * 1024);
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "--seq", "-c", ".")));
            Assert.Equal(0, await command.ExecuteAsync(new ChunkedHost(inner, chunk), memory, CancellationToken.None));
            Assert.Equal(expected, inner.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal("jq: ignoring parse error: " + warning + "\n", inner.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(0, memory.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("1\n", "1")]
    [InlineData("true\n", "true")]
    [InlineData("\"é🚀\"", "\"é\\ud83d\\ude80\"")]
    [InlineData("[1,2]", "[1,2]")]
    public async Task Jq_SequenceFirstInputDoesNotReadPastCompleteValue(string value, string expected)
    {
        var inner = new MockFileSystem();
        string prefix = "\u001e" + value;
        inner.AddFile("/first", prefix);
        inner.AddFile("/later", "\u001e99\n");
        var host = new PendingSuffixHost(inner, Encoding.UTF8.GetByteCount(prefix));
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "--seq", "-nc", "first(inputs)", "/first", "/later")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, cancellation.Token));
        Assert.False(host.PendingRead.Task.IsCompleted);
        Assert.Equal("\u001e" + expected + "\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_SequenceDiscardsLargeAbandonedPrefixAndStreamsRecords(bool malformed)
    {
        var inner = new MockFileSystem();
        string abandoned = new string('x', 256 * 1024);
        inner.AddFile("/input", (malformed ? "\u001e" : "") + abandoned + "\u001e"
            + string.Concat(Enumerable.Repeat("[1,2]\n", 32768)));
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "--seq", "empty", "/input")));
        Assert.Equal(0, await command.ExecuteAsync(new ChunkedHost(inner, 1024), memory, CancellationToken.None));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(malformed ? "jq: ignoring parse error: Truncated value at line 1, column 262146\n" : "",
            inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.Counts.Refusals);
        Assert.Equal(0, memory.Counts.SameSizeResizes);
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("\u001e[1,", 4)]
    [InlineData("\u001e\"partial", 5)]
    [InlineData("\u001exxxxx", 4)]
    public async Task Jq_SequenceCancellationClosesPendingOwnedInput(string input, int prefix)
    {
        var inner = new MockFileSystem();
        inner.AddFile("/input", input);
        var host = new PendingSuffixHost(inner, prefix);
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "--seq", "-nc", "try input catch .", "/input")));
        Task<int> execution = command.ExecuteAsync(host, memory, cancellation.Token);
        await host.PendingRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_SequenceTreeContinuesAcrossSourcesAndRetainsCapturedInputs()
    {
        var inner = new MockFileSystem();
        inner.AddFile("/first", "\u001e[\"é");
        inner.AddFile("/second", "🚀\",1]\n\u001e{\"x\":2}\n");
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "--seq", "-nc", "input as $x | input as $y | [$x,$y,$x]", "/first", "/second")));
        Assert.Equal(0, await command.ExecuteAsync(new ChunkedHost(inner, 1), memory, CancellationToken.None));
        Assert.Equal("\u001e[[\"é\\ud83d\\ude80\",1],{\"x\":2},[\"é\\ud83d\\ude80\",1]]\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(2, inner.ClosedDescriptors.Count);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_SequenceRetainsPendingHostBorrowUntilCompletion(bool cancel)
    {
        var inner = new MockFileSystem();
        inner.AddFile("/input", "\u001e[" + string.Concat(Enumerable.Repeat("1,", 8192)) + "2]\n");
        var host = new InputProgressHost(inner, 1);
        var memory = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "--seq", "-c", "length", "/input")));
        Task<int> execution = command.ExecuteAsync(host, memory, cancellation.Token);
        await host.Pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        long pending = memory.ReservedBytes;
        Assert.True(pending > 0);
        if (cancel) cancellation.Cancel();
        Assert.False(execution.IsCompleted);
        Assert.Equal(pending, memory.ReservedBytes);
        Assert.Empty(inner.ClosedDescriptors);
        host.Release.SetResult();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        else Assert.Equal(0, await execution);
        Assert.Equal(cancel ? "" : "\u001e8193\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }
}
