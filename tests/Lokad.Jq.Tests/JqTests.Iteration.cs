using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("[0, 1, 2, 3, 4, 5, 6, 7, 8, 9]", "[limit(3; .[])]", "[0,1,2]\n")]
    [InlineData("0", "[limit(0; error)]", "[]\n")]
    [InlineData("0", "[limit(1; 1, error)]", "[1]\n")]
    [InlineData("0", "limit(2.5; (1, 2, 3, 4))", "1\n2\n3\n")]
    [InlineData("[5, 6]", "first(.[])", "5\n")]
    [InlineData("[1, 2]", "first", "1\n")]
    [InlineData("[1, 2]", "last", "2\n")]
    [InlineData("[10, 20, 30]", "nth(1)", "20\n")]
    [InlineData("0", "nth(1; (10, 20, 30))", "20\n")]
    [InlineData("0", "nth(0; empty)", "")]
    [InlineData("[1, 2, 3]", "isempty(.[])", "false\n")]
    public async Task Jq_LimitFirstNthIsempty(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_FirstEmptyYieldsNothing()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "first(empty)")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_FirstPullsLazily()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "first(range(0; 1000000000))")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("0\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_NthPullsLazily()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "nth(2; range(0; 1000000000))")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("2\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("try limit(-1; 1) catch .", "\"limit doesn't support negative count\"\n")]
    [InlineData("try nth(-1; 1) catch .", "\"nth doesn't support negative indices\"\n")]
    [InlineData("try isempty(error(\"x\")) catch .", "\"x\"\n")]
    [InlineData("isempty(empty)", "true\n")]
    public async Task Jq_IterationEdgeCases(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
