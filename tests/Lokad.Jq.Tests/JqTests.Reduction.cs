using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("[[1], [2]]", "reduce .[] as [$a] (0; . + $a)", "3\n")]
    [InlineData("[{\"a\": [1]}, {\"a\": 1}]", "reduce .[] as {a: [$x]} ?// {a: $x} (0; . + $x)", "2\n")]
    [InlineData("0", "1 as $y | reduce (1, 2) as $x (0; . + $x + $y)", "5\n")]
    [InlineData("null", "reduce [[1, 2, 10], [3, 4, 10]][] as [$i, $j] (0; . + $i * $j)", "14\n")]
    public async Task Jq_ReduceReadsInputs(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("foreach range(0; 5) as $x (0; . + $x)", "0\n1\n3\n6\n10\n")]
    [InlineData("foreach (1, 2, 3) as $x (0; . + $x; $x)", "1\n2\n3\n")]
    [InlineData("reduce empty as $x (1, 2; .)", "1\n2\n")]
    [InlineData("reduce (1, 2) as $x (0; (., . + 100))", "0\n100\n100\n200\n")]
    [InlineData("foreach (1, 2) as $x (0; (., . + 100))", "0\n100\n0\n100\n100\n200\n")]
    [InlineData("foreach 1 as $x ((10, 20); . + $x)", "11\n21\n")]
    [InlineData("[1, 2, 3] | [-reduce -.[] as $x (0; . + $x)]", "[\n  6\n]\n")]
    [InlineData("[1, 2] | [reduce .[] / .[] as $i (0; . + $i)]", "[\n  4.5\n]\n")]
    [InlineData("[1, 2, 3] | reduce .[] as $x (0; . + $x) as $x | $x", "6\n")]
    [InlineData("[1, 2, 3] | reduce .[] as $then (4 as $else | $else; . as $elif | . + $then * $elif)", "96\n")]
    [InlineData("null | reduce . as $n (.; .)", "null\n")]
    public async Task Jq_ReduceFoldsStates(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_ForeachNeedsItems()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "foreach empty as $x (5; .)")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_ReduceEmptyUpdateKillsBranches()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "reduce (1, 2) as $x (0; empty)")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_ReduceBreakAbandonsLoop()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "[label $o | foreach (1, 2, 3) as $x (0; . + $x | if . >= 3 then break $o else . end)]")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[\n  1\n]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_ReducePropagatesUpdateErrors()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "try (reduce (1, 2) as $x (0; $x | .a)) catch \"caught\"")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"caught\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("reduce (1, 2) as $x (0; $x), $x", "undefined variable $x")]
    [InlineData("reduce (1, 2) as $x ($x; .)", "undefined variable $x")]
    public async Task Jq_ReduceScopeErrorsFailAtCompile(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_ReduceInfiniteSourceExhaustsQuota()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "def r: 1, r; reduce r as $x (0; . + $x)")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("budget exceeded", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_ReduceHonorsCancellation()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "reduce (1, 2) as $x (0; .)")));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => tool.ExecuteAsync(host, cancelled.Token));
    }
}
