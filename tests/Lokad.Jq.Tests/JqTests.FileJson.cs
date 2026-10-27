using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("\uFEFF1\n[2]\n", "[1,[2]]\n")]
    [InlineData("\uFEFF", "[]\n")]
    [InlineData("", "[]\n")]
    [InlineData(" \r\n", "[]\n")]
    [InlineData("1 [2]", "[1,[2]]\n")]
    public async Task Jq_WholeFileJsonEntryPointsShareBomAndMultipleValueHandling(string content, string expected)
    {
        foreach (bool module in new[] { false, true })
        {
            var host = new MockFileSystem(); host.AddFile("/data.json", content);
            string[] arguments = module ? ["-nc", "-L", "/", "import \"data\" as $d {optional:true}; $d"]
                : ["-nc", "--slurpfile", "d", "/data.json", "$d"];
            var memory = new BenchmarkMemoryBudget(128 * 1024);
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments)));
            Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
            Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Single(host.ClosedDescriptors);
            Assert.Equal(0, host.OpenFileCount);
            Assert.Equal(0, memory.ReservedBytes);
        }
    }

    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    public async Task Jq_SharedFileJsonParserPreservesSlurpfileChildDepth(int depth)
    {
        var host = new MockFileSystem(); host.AddFile("/data.json", new string('[', depth) + "0" + new string(']', depth));
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "--slurpfile", "d", "/data.json", "$d | length")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }
}
