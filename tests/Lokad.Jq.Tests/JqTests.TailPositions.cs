using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("(1,2) | if true then f($n-1) else empty end", "1\n2\n")]
    [InlineData("(1,2) | (0,f($n-1))", "0\n1\n0\n2\n")]
    [InlineData("(1,2) | (empty // f($n-1))", "1\n2\n")]
    [InlineData("(1,2) | try f($n-1) catch 99", "1\n2\n")]
    [InlineData("(1,2) | f($n-1)?", "1\n2\n")]
    [InlineData("(1,2) | def g: .; f($n-1)", "1\n2\n")]
    [InlineData("(1,2) | label $out | f($n-1)", "1\n2\n")]
    [InlineData("if (true,false) then f($n-1) else 99 end", "null\n99\n")]
    [InlineData("if (true,true) then f($n-1) else empty end", "null\nnull\n")]
    [InlineData("if false then empty elif (true,false) then f($n-1) else 99 end", "null\n99\n")]
    public async Task Jq_TailRewritingPreservesPendingRoutes(string step, string expected)
    {
        var host = new MockFileSystem();
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "def f($n): if $n==0 then . else " + step + " end; f(1)")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("(.,.) | if true then f($n-1) else empty end")]
    [InlineData("if (true,true) then f($n-1) else empty end")]
    [InlineData("(.,.) | f($n-1)?")]
    public async Task Jq_PathTailsKeepEveryRoute(string step)
    {
        var host = new MockFileSystem();
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "def f($n): if $n==0 then .a else " + step + " end; {a:1} | path(f(1))")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal("[\"a\"]\n[\"a\"]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }
}
