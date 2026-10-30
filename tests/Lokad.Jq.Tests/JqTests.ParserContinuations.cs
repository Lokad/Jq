using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("binding", 60, 0)]
    [InlineData("binding", 256, 3)]
    [InlineData("pipe", 60, 0)]
    [InlineData("pipe", 256, 3)]
    [InlineData("comma", 60, 0)]
    [InlineData("comma", 256, 3)]
    public async Task Jq_InactiveContinuationsRespectParserDepth(string shape, int count, int status)
    {
        string continuation = shape switch
        {
            "binding" => ". as $x | ",
            "pipe" => ". | def f: .; ",
            _ => "., def f: .; ",
        };
        string source = "if false then " + string.Concat(Enumerable.Repeat(continuation, count)) + ". else 0 end";
        foreach (bool hosted in new[] { false, true })
        {
            var memory = new BenchmarkMemoryBudget(8 * 1024 * 1024);
            var host = new MockFileSystem();
            host.AddFile("/program", source);
            var invocation = hosted ? BuildInvocation("jq", "-nc", "-f", "/program")
                : BuildInvocation("jq", "-nc", source);
            var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
            Assert.Equal(status, await command.ExecuteAsync(host, memory, CancellationToken.None));
            Assert.Equal(status == 0 ? "0\n" : "", host.GetOutput(JqFileDescriptor.StdOut));
            if (status == 0) Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
            else Assert.Contains("filter nesting limit exceeded", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(hosted ? 1 : 0, host.ClosedDescriptors.Count);
            Assert.Equal(0, host.OpenFileCount);
            Assert.Equal(0, memory.ReservedBytes);
        }
    }

    [Theory]
    [InlineData(62, 0)]
    [InlineData(63, 3)]
    public async Task Jq_BindingDepthIncludesTheEnclosingConditional(int count, int status)
    {
        var host = new MockFileSystem();
        string source = "if false then " + string.Concat(Enumerable.Repeat(". as $x | ", count)) + ". else 0 end";
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", source)));
        Assert.Equal(status, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(status == 0 ? "0\n" : "", host.GetOutput(JqFileDescriptor.StdOut));
    }
}
