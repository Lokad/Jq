using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("{\"a\": 1}", "path(.a)", "[\"a\"]\n")]
    [InlineData("[10, 20]", "path(.[])", "[0]\n[1]\n")]
    [InlineData("{\"a\": {\"b\": 1}}", "path(.a.b)", "[\"a\",\"b\"]\n")]
    [InlineData("[0, 1, 2]", "path(.[1, 2])", "[1]\n[2]\n")]
    [InlineData("{\"a\": [1]}", "path(..)", "[]\n[\"a\"]\n[\"a\",0]\n")]
    [InlineData("[0, 1, 2]", "def x: .[1, 2]; path(x)", "[1]\n[2]\n")]
    [InlineData("{\"a\": null, \"b\": null}", "path((.a as $x | .b))", "[\"b\"]\n")]
    [InlineData("[0, 1, 2, 3]", "path(.[1:2])", "[{\"start\":1,\"end\":2}]\n")]
    public async Task Jq_PathEnumeratesSegments(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_PathRejectsFreshValues()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[0, 1, 2]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "try path(reverse) catch .")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"Invalid path expression with result [2,1,0]\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("{\"bar\": 42, \"foo\": [\"a\", \"b\", \"c\", \"d\"]}", "[\"foo\", 1] as $p | getpath($p), setpath($p; 20), delpaths([$p])", "\"b\"\n{\"bar\":42,\"foo\":[\"a\",20,\"c\",\"d\"]}\n{\"bar\":42,\"foo\":[\"a\",\"c\",\"d\"]}\n")]
    [InlineData("[[0], [0, 1], [0, 1, 2]]", "[.[] | setpath([2]; 42)]", "[[0,null,42],[0,1,42],[0,1,42]]\n")]
    [InlineData("[[0], [0, 1], [0, 1, 2]]", "[.[] | getpath([2])]", "[null,null,2]\n")]
    [InlineData("[[0], [0, 1], [0, 1, 2]]", "[.[] | delpaths([[2]])]", "[[0],[0,1],[0,1]]\n")]
    [InlineData("[[{\"foo\": 2, \"x\": 1}], [{\"bar\": 2}]]", "[.[] | delpaths([[0, \"foo\"]])]", "[[{\"x\":1}],[{\"bar\":2}]]\n")]
    [InlineData("[1, 2, 3]", "delpaths([[-200]])", "[1,2,3]\n")]
    [InlineData("{\"bar\": false}", "[\"foo\", 1] as $p | getpath($p), setpath($p; 20), delpaths([$p])", "null\n{\"bar\":false,\"foo\":[null,20]}\n{\"bar\":false}\n")]
    public async Task Jq_PathBuiltinsReadWriteDelete(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("{\"foo\": [0, 1, 2, 3, 4], \"bar\": [0, 1]}", "del(.)", "null\n")]
    [InlineData("{\"foo\": [0, 1, 2, 3, 4], \"bar\": [0, 1]}", "del(empty)", "{\"foo\":[0,1,2,3,4],\"bar\":[0,1]}\n")]
    [InlineData("{\"foo\": [0, 1, 2, 3, 4], \"bar\": [0, 1]}", "del((.foo, .bar, .baz) | .[2, 3, 0])", "{\"foo\":[1,4],\"bar\":[1]}\n")]
    [InlineData("{\"foo\": [0, 1, 2, 3, 4], \"bar\": [0, 1]}", "del(.foo[0], .bar[0], .foo, .baz.bar[0].x)", "{\"bar\":[1]}\n")]
    [InlineData("null", "pick(.a.b.c)", "{\"a\":{\"b\":{\"c\":null}}}\n")]
    [InlineData("{\"a\": 1, \"b\": 2, \"c\": 3}", "pick(.a, .b)", "{\"a\":1,\"b\":2}\n")]
    public async Task Jq_DelAndPickReshape(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_DelpathsRequiresArrays()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{}");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "try delpaths(0) catch .")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"Paths must be specified as an array\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
