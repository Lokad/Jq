using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("1 as $x | ($x+1) as $x | $x", "2\n")]
    [InlineData("{a:1,b:2} | .a as $x | .b as $y | [$x,$y,.]", "[1,2,{\"a\":1,\"b\":2}]\n")]
    [InlineData("0 as $x | 1 as $y | def f: $x+$y; 2 as $x | f", "1\n")]
    [InlineData("0 as $x | 1 as $y | def f($n): if $n==0 then $x+$y else f($n-1) end; f(1000)", "1\n")]
    [InlineData("null as $x | false as $y | [$x,$y]", "[null,false]\n")]
    [InlineData("{a:[1]} | .a as $x | .a as $y | .a | path($x),path($y)", "[]\n[]\n")]
    [InlineData("1 as $x | 2 as $y | ((3,4) as $z | [$x,$y,$z])", "[1,2,3]\n[1,2,4]\n")]
    [InlineData("1 as $x | 2 as $y | try (\"ready\",error([$x,$y])) catch .", "\"ready\"\n[1,2]\n")]
    public async Task Jq_BatchedBindingPrefixesPreserveLexicalValuesAndEffects(string filter, string expected)
    {
        var root = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", filter], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

}
