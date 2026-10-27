using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("alternatives", false, false)]
    [InlineData("alternatives", false, true)]
    [InlineData("alternatives", true, false)]
    [InlineData("alternatives", true, true)]
    [InlineData("array", false, false)]
    [InlineData("array", false, true)]
    [InlineData("array", true, false)]
    [InlineData("array", true, true)]
    [InlineData("definitions", false, false)]
    [InlineData("definitions", false, true)]
    [InlineData("definitions", true, false)]
    [InlineData("definitions", true, true)]
    public async Task Jq_RuntimeScopesRequireAdmissionBeforeConstruction(string shape, bool paths, bool cancel)
    {
        IReadOnlyList<BindingPattern> variables = Enumerable.Range(0, 10000)
            .Select(i => (BindingPattern)new VariablePattern("v" + i)).ToArray();
        JqFilter filter = new IdentityFilter();
        if (shape == "definitions")
            for (int i = 0; i < 10000; i++)
                filter = new DefFilter(new JqFunctionDefinition("f" + i, [], new IdentityFilter()), filter);
        else
            filter = new AsFilter(new IdentityFilter(), shape == "array" ? [new ArrayPattern(variables)] : variables, filter);
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.Cancel();
        var root = new BenchmarkMemoryBudget(0);
        var memory = new CountingMemoryBudget(root);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(),
            new JqBudget(Utf8RegexAdapter.Instance, memory, cancellation.Token)))
        {
            Func<Task> evaluate = paths
                ? async () => await filter.EvaluatePaths(new JqValuePath([], null, true), context, context.RootEnvironment).SingleAsync()
                : async () => await filter.Evaluate(null, context, context.RootEnvironment).SingleAsync();
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(evaluate);
            else Assert.True((await Assert.ThrowsAsync<JqQuotaException>(evaluate)).IsMemoryAdmission);
            Assert.Equal(cancel ? 0 : 1, memory.Counts.Refusals);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("null as $x | $x")]
    [InlineData("null as [$x,$y] | empty")]
    [InlineData("def f: null; f")]
    [InlineData("def f($n): if $n==0 then null else def local: $n-1; f(local) end; f(10000)")]
    public async Task Jq_CompletedRuntimeScopesReuseCoarseCredit(string body)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            body.Contains("10000", StringComparison.Ordinal) ? body : "range(10000) | (" + body + ")")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        string expected = body.Contains("empty", StringComparison.Ordinal) ? ""
            : body.Contains("10000", StringComparison.Ordinal) ? "null\n" : string.Concat(Enumerable.Repeat("null\n", 10000));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 150);
        Assert.Equal(0, memory.Counts.Refusals);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("null as $x ?// $y | empty")]
    [InlineData("def local: 42; empty")]
    public async Task Jq_RuntimeScopeAdmissionRefusalCannotBeCaughtAfterPrefix(string body)
    {
        const long allowance = 4 * 1024 * 1024;
        var root = new BenchmarkMemoryBudget(allowance);
        IMemoryReservation? pressure = null;
        var host = new MockFileSystem();
        host.BeforeByteAppend = () =>
        {
            if (pressure is null) Assert.True(root.TryReserve(allowance - root.ReservedBytes, out pressure));
        };
        try
        {
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", "\"ready\", (try (" + body + ") catch \"caught\")")));
            Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
            Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(Assert.IsAssignableFrom<IMemoryReservation>(pressure).Bytes, root.ReservedBytes);
        }
        finally { pressure?.Dispose(); }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("def f(g;$n): if $n==0 then g else def local: $n; f(local;$n-1) end; f(0;3)", "1\n")]
    [InlineData("def f(g;$n): if $n==0 then g else def local: .a; f(local;$n-1) end; {a:42} | path(f(.;3))", "[\"a\"]\n")]
    [InlineData("def f($n): if $n==0 then . else def local: (0,error(\"late\")); f(local) end; first(f(1))", "null\n")]
    [InlineData("null as $x ?// $y | [$x,$y]", "[null,null]\n")]
    [InlineData("\"a\" as $key | {a:42} as {($key):$x} | $x", "42\n")]
    public async Task Jq_RuntimeScopeOwnershipKeepsCapturesAndAlternativeNames(string source, string expected)
    {
        var root = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_RuntimeScopesStayAdmittedThroughPendingOutputAndCleanup(bool cancel)
    {
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        var inner = new MockFileSystem();
        inner.AddFile("/input", "null");
        var host = new MemoryBorrowHost(inner) { DelayCleanup = true };
        using var cancellation = new CancellationTokenSource();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "def f(g;$n): if $n==0 then null as $x | g else def local: 42; f(local;$n-1) end; f(0;3)", "/input")));
        Task<int> execution = command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, cancellation.Token);
        Assert.Equal("42\n"u8.ToArray(), (await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10))).ToArray());
        long charged = root.ReservedBytes;
        Assert.True(charged > 0);
        if (cancel) cancellation.Cancel();
        Assert.False(execution.IsCompleted);
        Assert.Equal(charged, root.ReservedBytes);
        host.Release.SetResult();
        await host.Closing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(root.ReservedBytes > 0);
        host.CloseRelease.SetResult();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        else Assert.Equal(0, await execution);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, root.ReservedBytes);
    }
    [Fact]
    public async Task Jq_RetainedLocalCapturesRequireAdmissionAndCloseInputOnRefusal()
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/input", "null");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "def f(g;$n): if $n==0 then g else def local: $n; f(local;$n-1) end; try f(0;10000) catch 42", "/input")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

}
