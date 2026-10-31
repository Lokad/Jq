using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("[{a:[1]}] as $s | $s | map({a:.a}) as $r | ($r | .[0].a[0]=9) as $changed | [$s[0].a[0],$r[0].a[0],$changed[0].a[0]]", "[1,1,9]\n")]
    [InlineData("[{a:[1]}] as $s | $s | map((1,2) as $n | {a:.a,n:$n}) as $r | ($r | .[0].a[0]=9) as $changed | [$s[0].a[0],$r[0].a[0],$r[1].a[0],$changed[0].a[0],$changed[1].a[0]]", "[1,1,1,9,1]\n")]
    [InlineData("[0,0] | map_values((1,error(\"unused\")) as $n | {n:$n})", "[{\"n\":1},{\"n\":1}]\n")]
    [InlineData("{a:0,b:0} | map_values((1,error(\"unused\")) as $n | {n:$n})", "{\"a\":{\"n\":1},\"b\":{\"n\":1}}\n")]
    [InlineData("[1] as $a | [0,0] | map($a) as $r | ($r | .[0][0]=9) as $changed | [$a[0],$r[0][0],$changed[0][0],$changed[1][0]]", "[1,1,9,1]\n")]
    [InlineData("[0,1] | try map((if .==1 then error({payload:[1]}) else . end) as $x | {value:$x}) catch .", "{\"payload\":[1]}\n")]
    public async Task Jq_FreshMapResultsPreserveIndependentValuesAndLazyEffects(string filter, string expected)
    {
        var root = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", filter], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("map")]
    [InlineData("array-values")]
    [InlineData("object-values")]
    public async Task Jq_EmptyMapRootsRequireAdmissionBeforeExposure(string kind)
    {
        var root = new BenchmarkMemoryBudget(63);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(null, root, CancellationToken.None)))
        {
            JqValue input = kind == "object-values" ? new JqObject(context.Budget) : new JqArray();
            JqFilter filter = kind == "map" ? new MapFilter(new IdentityFilter()) : new MapValuesFilter(new IdentityFilter());
            var error = await Assert.ThrowsAsync<JqQuotaException>(async () =>
            {
                _ = await filter.Evaluate(input, context, context.RootEnvironment).SingleAsync();
            });
            Assert.True(error.IsMemoryAdmission);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("[0] | map([$v])", 62, 0)]
    [InlineData("[0] | map([$v])", 63, 5)]
    [InlineData("[0] | map_values([$v])", 62, 0)]
    [InlineData("[0] | map_values([$v])", 63, 5)]
    [InlineData("{a:0} | map_values([$v])", 62, 0)]
    [InlineData("{a:0} | map_values([$v])", 63, 5)]
    [InlineData("[0] | [map($v)]", 62, 0)]
    [InlineData("[0] | [map($v)]", 63, 5)]
    public async Task Jq_AdoptedMapResultsCheckDestinationDepthBeforeDiscard(string expression, int depth, int status)
    {
        string filter = "\"ready\", (null | setpath([range(" + depth + ")|0];0) as $v | (" + expression + ") | empty)";
        var root = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", filter], []);
        Assert.Equal(status, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal("\"ready\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(status == 0 ? "" : "jq: value nesting limit exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("[0] | map({payload:\"x\" * 1000000})")]
    [InlineData("[0] | map_values({payload:\"x\" * 1000000})")]
    [InlineData("{a:0} | map_values({payload:\"x\" * 1000000})")]
    public async Task Jq_AdoptedMapRefusalRemainsTerminal(string expression)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq",
            ["-nr", "\"ready\", (try (" + expression + ") catch \"caught\"), \"later\""], []);
        Assert.Equal(5, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("map({value:.})", false)]
    [InlineData("map({value:.})", true)]
    [InlineData("map_values({value:.})", false)]
    [InlineData("map_values({value:.})", true)]
    public async Task Jq_AdoptedMapOwnershipSurvivesPendingOutputAndCleanup(string expression, bool cancel)
    {
        var root = new BenchmarkMemoryBudget(128 * 1024);
        var inner = new MockFileSystem();
        inner.AddFile("/input", "[1,2]\n[3,4]\n");
        var host = new MemoryBorrowHost(inner) { DelayCleanup = true };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-c", expression, "/input"], []);
        Task<int> execution = Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, root, cancellation.Token);
        ReadOnlyMemory<byte> pending = await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        byte[] expected = "[{\"value\":1},{\"value\":2}]\n"u8.ToArray();
        long charged = root.ReservedBytes;
        try
        {
            Assert.Equal(expected, pending.ToArray());
            Assert.True(charged > 0);
            if (cancel) cancellation.Cancel();
            Assert.False(execution.IsCompleted);
            Assert.Equal(charged, root.ReservedBytes);
            Assert.Equal(expected, pending.ToArray());
        }
        finally { host.Release.TrySetResult(); }
        await host.Closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(execution.IsCompleted);
            Assert.True(root.ReservedBytes > 0);
        }
        finally { host.CloseRelease.TrySetResult(); }
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        else
        {
            Assert.Equal(0, await execution);
            Assert.Equal("[{\"value\":1},{\"value\":2}]\n[{\"value\":3},{\"value\":4}]\n", inner.GetOutput(JqFileDescriptor.StdOut));
        }
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }
}
