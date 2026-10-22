using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("def f($x): if $x==1 then . | f(0|debug) else . end; f(1)", "null\n", "[\"DEBUG:\",0]\n")]
    [InlineData("def f($x): if $x==1 then (1,2) | f(0|debug) else . end; f(1)", "1\n2\n", "[\"DEBUG:\",0]\n[\"DEBUG:\",0]\n")]
    [InlineData("def f($x): if $x==1 then f((0,0)|debug) else . end; first(f(1))", "null\n", "[\"DEBUG:\",0]\n")]
    [InlineData("def f($x): if $x==1 then f((0,error(\"late\"))) else . end; first(f(1))", "null\n", "")]
    [InlineData("def f($x): if $x==1 then . | f((0,error(\"late\"))) else . end; first(f(1))", "null\n", "")]
    [InlineData("def f($x): if $x==1 then . | f(0|debug) else . end; path(f(1))", "[]\n", "[\"DEBUG:\",0]\n")]
    [InlineData("def f($x): if $x==1 then f((0,error(\"late\"))) else . end; first(path(f(1)))", "[]\n", "")]
    [InlineData("def f($x): if $x>0 then .a | f($x-1) else . end; {a:{a:3}} | path(f(2))", "[\"a\",\"a\"]\n", "")]
    public async Task Jq_UserTailsStreamArgumentsAndSourcesOnce(string source, string expected, string expectedError)
    {
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(expectedError, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("def f($x): if $x==1 then f((0,error(\"late\"))) else . end; f(1)", "null\n", "late")]
    [InlineData("def f($x): if $x==1 then (1,error(\"late\")) | f(0) else . end; f(1)", "1\n", "late")]
    public async Task Jq_UserTailsKeepPrefixBeforeDeferredErrors(string source, string expected, string diagnostic)
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", source)));
        Assert.Equal(5, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_UserPipeTailConsumesInputExactlyOnce()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("0\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "def f($n): if $n==0 then . else . | f(input) end; f(1)")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("null\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(65536, false)]
    public async Task Jq_UserTailFirstResultDoesNotInventoryBorrowedArguments(long allowance, bool refused)
    {
        var values = new JsonArray();
        for (int i = 0; i < 100000; i++) values.Add((JsonNode?)null);
        var variables = new Dictionary<string, JsonNode?> { ["values"] = values };
        using var compiler = new JqContext(variables, new JqBudget(CancellationToken.None));
        var filter = new JqParser("def f($x): if $x==1 then f($values[]) else . end; try first(f(1)) catch 42",
            JqProgramSource.Inline, compiler.RootEnvironment, compiler.Budget).Parse();
        var memory = new BenchmarkMemoryBudget(allowance);
        using (var context = new JqContext(variables, new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None)))
        {
            if (refused)
                Assert.True((await Assert.ThrowsAsync<JqQuotaException>(async () => await filter.Evaluate(null, context, context.RootEnvironment).SingleAsync())).IsMemoryAdmission);
            else
                Assert.Null(await filter.Evaluate(null, context, context.RootEnvironment).SingleAsync());
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ProvenUserTailsStayFlatWithBoundedMetadataAndFewCalls()
    {
        var host = new MockFileSystem();
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "def f($n): if $n==0 then 0 else . | f($n-1) end; f(50000)")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("0\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 150);
        Assert.Equal(0, root.ReservedBytes);
        Assert.Equal(0, root.ActiveScopes);
    }

    [Fact]
    public async Task Jq_UserTailDownstreamClosureDisposesDeferredSources()
    {
        var host = new MockFileSystem { AppendRemainsOpen = false };
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "def f($n): if $n==0 then . else (1,error(\"late\")) | f(0) end; f(1)")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_UserTailRetainsAdmissionThroughPendingOutput(bool cancel)
    {
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        var inner = new MockFileSystem();
        inner.AddFile("/input", "null");
        var host = new MemoryBorrowHost(inner) { DelayCleanup = true };
        using var cancellation = new CancellationTokenSource();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "def f($n): if $n==0 then . else (1,2) | f(0) end; f(1)", "/input")));
        Task<int> execution = command.ExecuteAsync(host, root, cancellation.Token);
        ReadOnlyMemory<byte> pending = await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        long charged = root.ReservedBytes;
        Assert.Equal("1\n"u8.ToArray(), pending.ToArray());
        Assert.True(charged >= pending.Length);
        if (cancel) cancellation.Cancel();
        Assert.False(execution.IsCompleted);
        Assert.Equal(charged, root.ReservedBytes);
        host.Release.SetResult();
        await host.Closing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(execution.IsCompleted);
        Assert.True(root.ReservedBytes > 0);
        host.CloseRelease.SetResult();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        else
        {
            Assert.Equal(0, await execution);
            Assert.Equal("1\n2\n", inner.GetOutput(JqFileDescriptor.StdOut));
        }
        Assert.Equal(0, root.ReservedBytes);
        Assert.Equal(0, root.ActiveScopes);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Single(inner.ClosedDescriptors);
    }

    [Fact]
    public async Task Jq_GovernedInfinitePipeTailCancelsAndClosesOwnedInput()
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/input", "null");
        using var cancellation = new CancellationTokenSource();
        host.BeforeByteAppend = () => cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "\"ready\", (def f: . | f; f)", "/input")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Run(() => command.ExecuteAsync(host, root, cancellation.Token)).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("\"ready\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
        Assert.Equal(0, root.ActiveScopes);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Single(host.ClosedDescriptors);
    }

    [Fact]
    public async Task Jq_UserTailContinuationGrowthRefusalIsTerminalAfterPrefix()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "\"ready\", (def f($n): if $n==0 then 0 else f(($n-1),0) end; try f(10000) catch 42)")));
        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("\"ready\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Fact]
    public async Task Jq_SequentialUserCallsReuseCoarseRecordCredit()
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "def step: .+1; 0 | until(.==10000; step)")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("10000\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 100);
        Assert.Equal(0, memory.Counts.SameSizeResizes);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_CompletedUserCallsReleaseCapturedFilterScopes()
    {
        var memory = new BenchmarkMemoryBudget(256 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "def f(g;$n): if $n==0 then g else f(1;$n-1) end; range(1000) | f(0;100)")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("1\n", 1000)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Theory]
    [InlineData("g", "g", "42\n")]
    [InlineData("g", "42", "42\n")]
    [InlineData(".", "g", "null\n")]
    [InlineData(".", "42", "null\n")]
    public async Task Jq_ForwardedFiltersStayFlatUnderSmallAllowance(string result, string next, string expected)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "def f(g;$n): if $n==0 then " + result + " else f(" + next + ";$n-1) end; f(42;50000)")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 150);
        Assert.Equal(0, memory.Counts.Refusals);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("def f(g;$n): if $n==0 then g,g else f(g;$n-1) end; 7 as $x | f($x|debug;1000)", "7\n7\n", "[\"DEBUG:\",7]\n[\"DEBUG:\",7]\n")]
    [InlineData("def f(g;$n): if $n==0 then g else f(g;$n-1) end; {a:42} | path(f(.a;1000))", "[\"a\"]\n", "")]
    [InlineData("def f(g;$n): if $n==0 then g else f(g;$n-1) end; first(f((1,error(\"late\"));1000))", "1\n", "")]
    [InlineData("def f(g;h;$n): if $n==0 then g,h else f(h;g;$n-1) end; f(1;2;1001)", "2\n1\n", "")]
    [InlineData("def f(g;$n): if $n==0 then g else f($n;$n-1) end; f(0;3)", "1\n", "")]
    public async Task Jq_ForwardedFiltersKeepLexicalLazyAndPathBehavior(string source, string expected, string diagnostic)
    {
        var root = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ForwardedInfiniteFilterTailObservesCancellation()
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem();
        host.AddFile("/input", "null");
        host.BeforeByteAppend = () => cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "\"ready\", (def f(g): f(g); f(.))", "/input")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Run(() => command.ExecuteAsync(host, root, cancellation.Token)).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("\"ready\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

}
