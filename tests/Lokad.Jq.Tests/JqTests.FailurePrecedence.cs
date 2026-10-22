using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_DiagnosticAdmissionRefusalSurvivesPendingCancellation()
    {
        var memory = new BenchmarkMemoryBudget(8);
        Assert.True(memory.TryReserve(8, out var retained));
        using (retained)
        using (var cancellation = new CancellationTokenSource())
        {
            var host = new MemoryBorrowHost(new MockFileSystem()) { DelayedDescriptor = JqFileDescriptor.StdErr };
            Task<JqAppendResult?> writing = JqDiagnosticWriter.TryWriteAsync(host, JqFileDescriptor.StdErr,
                ["large diagnostic"], true, memory, cancellation.Token);
            ReadOnlyMemory<byte> pending = await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("jq: memory budget exceeded\n"u8.ToArray(), pending.ToArray());
            cancellation.Cancel();
            Assert.False(writing.IsCompleted);
            Assert.Equal(8, memory.ReservedBytes);
            host.Release.SetResult();
            Assert.Null(await writing);
            Assert.Equal(8, memory.ReservedBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("compile", false)]
    [InlineData("compile", true)]
    [InlineData("boolean", false)]
    [InlineData("boolean", true)]
    [InlineData("detailed", false)]
    [InlineData("detailed", true)]
    public async Task Jq_ProviderFaultSurvivesCancellationDuringTheSameCallback(string stage, bool overflow)
    {
        using var cancellation = new CancellationTokenSource();
        Exception sentinel = overflow ? new OverflowException("provider sentinel") : new ArgumentException("provider sentinel");
        var engine = new ObservedRegexEngine((operation, _, _) =>
        {
            if (operation != stage) return;
            cancellation.Cancel();
            throw sentinel;
        });
        var host = new MockFileSystem();
        host.AddFile("/first", "\"x\"\n");
        host.AddFile("/later", "\"later\"\n");
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        string operation = stage == "detailed" ? "match(\"x\")" : "test(\"x\")";
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            "\"prefix\", (try " + operation + " catch \"caught\"), \"after\"", "/first", "/later")));

        var failure = await Assert.ThrowsAsync<JqHostFailureException>(() => command.ExecuteAsync(host, engine, memory, cancellation.Token));
        Assert.Same(sentinel, failure.InnerException);
        Assert.Equal("prefix\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(engine.Created, engine.Disposed);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("compile")]
    [InlineData("boolean")]
    [InlineData("detailed")]
    public async Task Jq_ProviderRefusalRetainsStatusFiveAfterLaterCancellation(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        var memory = new CountingMemoryBudget(root);
        var engine = new ObservedRegexEngine((operation, budget, _) =>
        {
            if (operation != stage) return;
            Assert.NotNull(budget);
            Assert.False(budget.TryReserve(2 * 1024 * 1024, out var refused));
            Assert.Null(refused);
            cancellation.Cancel();
            throw new JqRegexResourceException("provider admission refused", true);
        });
        var host = new MockFileSystem();
        host.AddFile("/first", "\"x\"\n");
        host.AddFile("/later", "\"later\"\n");
        string operation = stage == "detailed" ? "match(\"x\")" : "test(\"x\")";
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            "\"prefix\", (try " + operation + " catch \"caught\"), \"after\"", "/first", "/later")));

        Assert.Equal(5, await command.ExecuteAsync(host, engine, memory, cancellation.Token));
        Assert.Equal("prefix\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(1, memory.Counts.Refusals);
        Assert.Equal(engine.Created, engine.Disposed);
        Assert.Equal(0, root.ReservedBytes);
    }
}
