using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false, 64, false)]
    [InlineData(false, 65, false)]
    [InlineData(true, 64, false)]
    [InlineData(true, 65, false)]
    [InlineData(false, 65, true)]
    [InlineData(true, 65, true)]
    public async Task Jq_ArgumentStructuralQuotasStayTerminal(bool positional, int depth, bool closedError)
    {
        string json = new string('[', depth) + "0" + new string(']', depth);
        string[] args = positional ? ["-nc", "0", "--jsonargs", json] : ["-nc", "--argjson", "value", json, "0"];
        var host = new MockFileSystem { AppendRemainsOpen = !closedError };
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", args)));
        Assert.Equal(depth == 64 ? 0 : 5, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(depth == 64 ? "0\n" : "", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(depth == 64 ? "" : "jq: value nesting limit exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }
}
