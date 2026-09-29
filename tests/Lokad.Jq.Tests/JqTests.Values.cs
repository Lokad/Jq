using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_NullIsAValueWhileEmptyIsNoValues()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "[null, empty] | length")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("null", "null\n")]
    [InlineData("empty", "")]
    [InlineData("[empty]", "[]\n")]
    [InlineData("[null]", "[null]\n")]
    [InlineData("null == null", "true\n")]
    public async Task Jq_NullVsEmptyStream(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("{\"b\":2,\"a\":[1,{\"x\":null}]} == {\"a\":[1,{\"x\":null}],\"b\":2}", "true\n")]
    [InlineData("[1,2] == [2,1]", "false\n")]
    [InlineData("[1,[2,3]] == [1,[2,3]]", "true\n")]
    [InlineData("{\"a\":1} == {\"a\":1,\"b\":2}", "false\n")]
    [InlineData("1 == 1.0", "true\n")]
    [InlineData("\"a\" == \"a\"", "true\n")]
    [InlineData("1 == \"1\"", "false\n")]
    [InlineData("true == 1", "false\n")]
    [InlineData("null == false", "false\n")]
    [InlineData("0 == false", "false\n")]
    [InlineData("[] == []", "true\n")]
    [InlineData("{} == {}", "true\n")]
    public async Task Jq_EqualityFollowsValueSemantics(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[3 < \"a\", \"a\" < 3, null < false, false < true, true < 1, 1 < \"a\", \"a\" < [], [] < {}, {} < null]", "[true,false,true,true,true,true,true,true,false]\n")]
    [InlineData("[1,2] < [1,3]", "true\n")]
    [InlineData("[1] < [1,2]", "true\n")]
    [InlineData("[1,2] < [1,2]", "false\n")]
    [InlineData("{\"a\":1} < {\"b\":1}", "true\n")]
    [InlineData("{\"a\":2} > {\"a\":1}", "true\n")]
    [InlineData("{\"a\":1} < {\"a\":1,\"b\":2}", "true\n")]
    [InlineData("\"\\ue000\" < \"\U00010000\"", "true\n")]
    public async Task Jq_ComparisonFollowsTotalOrdering(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_MixedArrayMinMaxFollowTotalOrdering()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "{min:([1,\"a\",null,true] | min), max:([1,\"a\",null,true] | max)}")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("{\"min\":null,\"max\":\"a\"}\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
