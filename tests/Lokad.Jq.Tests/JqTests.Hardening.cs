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

    [Fact]
    public async Task Jq_ReusedCommandRereadsFileVariables()
    {
        var host = new MockFileSystem();
        host.AddFile("/data", "[1]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-c", "--slurpfile", "data", "/data", "$data")));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[[1]]\n", host.GetOutput(JqFileDescriptor.StdOut));
        host.AddFile("/data", "[2]");
        var rerun = new MockFileSystem();
        rerun.AddFile("/data", "[2]");
        Assert.Equal(0, await tool.ExecuteAsync(rerun, CancellationToken.None));
        Assert.Equal("[[2]]\n", rerun.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_ReusedCommandRunsConcurrentlyWithIndependentState()
    {
        var first = new MockFileSystem();
        first.AddFile("/data", "[1]");
        var second = new MockFileSystem();
        second.AddFile("/data", "[2]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-c", "--slurpfile", "data", "/data", "$data")));
        Task<int> left = tool.ExecuteAsync(first, CancellationToken.None);
        Task<int> right = tool.ExecuteAsync(second, CancellationToken.None);
        Assert.Equal(new[] { 0, 0 }, await Task.WhenAll(left, right));
        Assert.Equal("[[1]]\n", first.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("[[2]]\n", second.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_QuotaDuringEvaluationSurvivesCloseFailure()
    {
        // The owned file stays open while evaluation trips quota; persistent
        // cleanup failures must not replace the quota error.
        var host = new MockFileSystem { CloseFailuresRemaining = 2 };
        host.AddFile("/input", "[0]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "range(0;300000) | length", "/input")));
        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("value budget exceeded", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_CancellationDuringOutputSurvivesCloseFailure()
    {
        // A host-side cancellation on the first append leaves the input file
        // open; the abandoned close must not swallow it into a close error.
        var host = new MockFileSystem { CloseFailuresRemaining = 2 };
        host.AddFile("/a", "[0]");
        host.AddFile("/b", "[0]");
        bool fired = false;
        host.BeforeByteAppend = () =>
        {
            if (!fired)
            {
                fired = true;
                throw new OperationCanceledException();
            }
        };
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", ".", "/a", "/b")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
