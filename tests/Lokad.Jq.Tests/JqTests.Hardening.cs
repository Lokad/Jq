using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_CombinationsHandlesDeepNarrowMatricesWithoutRecursion()
    {
        // Twenty thousand rows would need twenty thousand nested enumerator
        // frames under recursion; the odometer keeps this flat on the heap.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "[range(20000) | [0]] | combinations | length")));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("20000\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }


    [Fact]
    public async Task Jq_CombinationsCountBuildIsBudgetBounded()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "1 | combinations(1000000) | length")));
        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("value budget exceeded", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[[], 5]", "")]
    public async Task Jq_CombinationsSkipsValidationPastLeadingEmptyRow(string input, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "combinations")));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_CombinationsRejectsNonArrayRows()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[[1], 5]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "combinations")));
        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("cannot iterate over number", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_CombinationsExponentialStreamsHaltOnQuota()
    {
        string input = "[" + string.Join(",", Enumerable.Repeat("[0, 1]", 24)) + "]";
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "combinations | length")));
        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("value budget exceeded", host.GetOutput(JqFileDescriptor.StdErr));
        foreach (string line in host.GetOutput(JqFileDescriptor.StdOut).Split((char)10, StringSplitOptions.RemoveEmptyEntries))
            Assert.Equal("24", line);
    }
}
