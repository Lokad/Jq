using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("[[1], [2]]", "reduce .[] as [$a] (0; . + $a)", "3\n")]
    [InlineData("[{\"a\": [1]}, {\"a\": 1}]", "reduce .[] as {a: [$x]} ?// {a: $x} (0; . + $x)", "2\n")]
    [InlineData("0", "1 as $y | reduce (1, 2) as $x (0; . + $x + $y)", "5\n")]
    [InlineData("null", "reduce [[1, 2, 10], [3, 4, 10]][] as [$i, $j] (0; . + $i * $j)", "14\n")]
    [InlineData("[[2,{\"j\":1}], [5,{\"j\":3}], [6,{\"j\":4}]]", "reduce .[] as [$i, {j:$j}] (0; . + $i - $j)", "5\n")]
    [InlineData("[1,2,3,4,5]", "reduce .[] as $item (0; . + $item)", "15\n")]
    [InlineData("[[1,2],[3,4],[5,6]]", "reduce .[] as [$i,$j] (0; . + $i * $j)", "44\n")]
    [InlineData("[{\"x\":\"a\",\"y\":1},{\"x\":\"b\",\"y\":2},{\"x\":\"c\",\"y\":3}]", "reduce .[] as {$x,$y} (null; .x += $x | .y += [$y])", "{\n  \"x\": \"abc\",\n  \"y\": [\n    1,\n    2,\n    3\n  ]\n}\n")]
    [InlineData("[1,2,3]", "[-foreach -.[] as $x (0; . + $x)]", "[\n  1,\n  3,\n  6\n]\n")]
    [InlineData("[1,2,3]", "[foreach .[] as $x (0; . + $x) as $x | $x]", "[\n  1,\n  3,\n  6\n]\n")]
    [InlineData("[0,1,2]", "[(label $here | .[] | if .>1 then break $here else . end), \"hi!\"]", "[\n  0,\n  1,\n  \"hi!\"\n]\n")]
    [InlineData("[0,2,1]", "[(label $here | .[] | if .>1 then break $here else . end), \"hi!\"]", "[\n  0,\n  \"hi!\"\n]\n")]
    [InlineData("[11,22,33,44,55,66,77,88,99]", "[label $out | foreach .[] as $item ([3, null]; if .[0] < 1 then break $out else [.[0] -1, $item] end; .[1])]", "[\n  11,\n  22,\n  33\n]\n")]
    [InlineData("[[2,1], [5,3], [6,4]]", "[foreach .[] as [$i, $j] (0; . + $i - $j)]", "[\n  1,\n  3,\n  5\n]\n")]
    [InlineData("[{\"a\":1}, {\"b\":2}, {\"a\":3, \"b\":4}]", "[foreach .[] as {a:$a} (0; . + $a; -.)]", "[\n  -1,\n  -1,\n  -4\n]\n")]
    [InlineData("[1,2]", "[foreach .[] / .[] as $i (0; . + $i)]", "[\n  1,\n  3,\n  3.5,\n  4.5\n]\n")]
    [InlineData("[10,9,8,7]", "[foreach .[] as $try (1 as $catch | $catch - 1; . + $try; .)]", "[\n  10,\n  19,\n  27,\n  34\n]\n")]
    [InlineData("[1, 2]", "foreach .[] as $x (0, 1; . + $x)", "1\n3\n2\n4\n")]
    [InlineData("[1,2,3,4,5]", "foreach .[] as $item (0; . + $item)", "1\n3\n6\n10\n15\n")]
    [InlineData("[1,2,3,4,5]", "foreach .[] as $item (0; . + $item; [$item, . * 2])", "[\n  1,\n  2\n]\n[\n  2,\n  6\n]\n[\n  3,\n  12\n]\n[\n  4,\n  20\n]\n[\n  5,\n  30\n]\n")]
    [InlineData("[\"foo\", \"bar\", \"baz\"]", "foreach .[] as $item (0; . + 1; {index: ., $item})", "{\n  \"index\": 1,\n  \"item\": \"foo\"\n}\n{\n  \"index\": 2,\n  \"item\": \"bar\"\n}\n{\n  \"index\": 3,\n  \"item\": \"baz\"\n}\n")]
    [InlineData("[1,2,3,4,5]", "[.[]|[.,1]|until(.[0] < 1; [.[0] - 1, .[1] * .[0]])|.[1]]", "[\n  1,\n  2,\n  6,\n  24,\n  120\n]\n")]
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
    // Multi-valued updates thread a single cell like the reference overwrite: the last output wins per item.
    [InlineData("reduce (1, 2) as $x (0; (., . + 100))", "200\n")]
    [InlineData("reduce range(3) as $x (0; . + $x, 100)", "100\n")]
    [InlineData("foreach (1, 2) as $x (0; (., . + 100))", "0\n100\n100\n200\n")]
    [InlineData("foreach (1, 2) as $x (0; (., . + 100); . * 10)", "0\n1000\n1000\n2000\n")]
    [InlineData("foreach 1 as $x ((10, 20); . + $x)", "11\n21\n")]
    [InlineData("[1, 2, 3] | [-reduce -.[] as $x (0; . + $x)]", "[\n  6\n]\n")]
    [InlineData("[1, 2] | [reduce .[] / .[] as $i (0; . + $i)]", "[\n  4.5\n]\n")]
    [InlineData("[1, 2, 3] | reduce .[] as $x (0; . + $x) as $x | $x", "6\n")]
    [InlineData("[1, 2, 3] | reduce .[] as $then (4 as $else | $else; . as $elif | . + $then * $elif)", "96\n")]
    [InlineData("null | reduce . as $n (.; .)", "null\n")]
    [InlineData("[foreach range(5) as $item (0; $item)]", "[\n  0,\n  1,\n  2,\n  3,\n  4\n]\n")]
    [InlineData("[label $if | range(10) | ., (select(. == 5) | break $if)]", "[\n  0,\n  1,\n  2,\n  3,\n  4,\n  5\n]\n")]
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
