using Lokad.Jq.Benchmarking;
using Lokad.Jq.Examples;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_RegexExampleUsesTheShippedAdapterAndRefundsItsInvocation()
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput("\"é item-42\"");
        Assert.Equal(0, await RegexAdapterExample.RunAsync(host, memory,
            Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal("[true,{\"offset\":2,\"length\":7,\"id\":\"42\"},{\"id\":\"42\"}]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }
}
