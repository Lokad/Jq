using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("[{a:[1]}, {a:[2]}] as $rows | [($rows | .[0].a += [3]), $rows]", "[[{\"a\":[1,3]},{\"a\":[2]}],[{\"a\":[1]},{\"a\":[2]}]]\n")]
    [InlineData("{a:[1]} as $input | [range(0;2) | {a:$input.a}] as $rows | [($rows | .[0].a += [2]),$rows,$input]", "[[{\"a\":[1,2]},{\"a\":[1]}],[{\"a\":[1]},{\"a\":[1]}],{\"a\":[1]}]\n")]
    [InlineData("[range(0;2) | {n:nan}] | [(.[0] == .[0]),(.[0] == .[1])]", "[true,false]\n")]
    public async Task Jq_CollectedConstructedContainersStayIndependent(string filter, string output)
    {
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(output, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(62, 0)]
    [InlineData(63, 5)]
    public async Task Jq_CollectionChecksConstructedDepthBeforeDiscardingResults(int depth, int status)
    {
        var memory = new BenchmarkMemoryBudget(4 * 1024 * 1024);
        var host = new MockFileSystem();
        string filter = "\"ready\", (reduce range(0;" + depth + ") as $i (0; [.]) | [{x:.}] | empty)";
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", filter)));
        Assert.Equal(status, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("\"ready\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(status == 0 ? "" : "jq: value nesting limit exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }
}
