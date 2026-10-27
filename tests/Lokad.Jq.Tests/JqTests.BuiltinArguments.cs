using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    // jq 1.8.2: the last argument is outermost; each builtin invocation
    // finishes before the next Cartesian combination is evaluated.
    [Theory]
    [InlineData("[fma((1,2);(3,4);(10,20))]", "[13,16,14,18,23,26,24,28]\n", "")]
    [InlineData("pow((1,2)|debug;(3,4)|debug)", "1\n8\n1\n16\n", "[\"DEBUG:\",3]\n[\"DEBUG:\",1]\n[\"DEBUG:\",2]\n[\"DEBUG:\",4]\n[\"DEBUG:\",1]\n[\"DEBUG:\",2]\n")]
    [InlineData("pow(empty;(3,4)|debug)", "", "[\"DEBUG:\",3]\n[\"DEBUG:\",4]\n")]
    [InlineData("[try pow((2,error(\"x\"));(3,4)) catch .]", "[8,\"x\"]\n", "")]
    [InlineData("[range((1,2);(3,4))]", "[1,2,1,2,3,2,2,3]\n", "")]
    [InlineData("\"ab\" | [ltrimstr((\"a\",\"b\"))]", "[\"b\",\"ab\"]\n", "")]
    [InlineData("[range(5) | floor]", "[0,1,2,3,4]\n", "")]
    public async Task Jq_BuiltinArgumentsPreserveCartesianOrderAndEffects(string source, string expected, string error)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(error, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_BuiltinArgumentsStopBeforeLaterEffectsOnDownstreamClosure()
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem { AppendRemainsOpen = false };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "pow((2,error(\"later\"));(3,4))")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal("8\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, host.AppendCallCount);
        Assert.Equal(0, root.ReservedBytes);
    }
}
