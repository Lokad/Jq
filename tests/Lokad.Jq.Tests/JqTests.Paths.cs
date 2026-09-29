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
    [Theory]
    [InlineData("{\"message\": \"hello\"}", ".message = \"goodbye\"", "{\"message\":\"goodbye\"}\n")]
    [InlineData("{\"bar\": 42}", ".foo = .bar", "{\"bar\":42,\"foo\":42}\n")]
    [InlineData("{\"foo\": 42}", ".foo |= . + 1", "{\"foo\":43}\n")]
    [InlineData("[1, 3, 5]", ".[] += 2, .[] *= 2, .[] -= 2, .[] /= 2, .[] %= 2", "[3,5,7]\n[2,6,10]\n[-1,1,3]\n[0.5,1.5,2.5]\n[1,1,1]\n")]
    [InlineData("{\"foo\": 2}", ".foo += .foo", "{\"foo\":4}\n")]
    [InlineData("[{\"a\": 1, \"b\": 2}]", ".[0].a |= {\"old\": ., \"new\": (. + 1)}", "[{\"a\":{\"old\":1,\"new\":2},\"b\":2}]\n")]
    [InlineData("[{\"a\": 1, \"b\": 2}, {\"a\": 2, \"b\": 4}, {\"a\": 7, \"b\": 8}]", "def inc(x): x |= . + 1; inc(.[].a)", "[{\"a\":2,\"b\":2},{\"a\":3,\"b\":4},{\"a\":8,\"b\":8}]\n")]
    [InlineData("[0, 1, 2]", "def x: .[1, 2]; x = 10", "[0,10,10]\n")]
    [InlineData("[\"a\", 1, true, null, [1], {\"k\": 1}]", ".[] = 1", "[1,1,1,1,1,1]\n")]
    [InlineData("[1, 5, 3, 0, 7]", "(.[] | select(. >= 2)) |= empty", "[1,0]\n")]
    [InlineData("[0, 1, 2, 3, 4, 5]", ".[] |= select(. % 2 == 0)", "[0,2,4]\n")]
    [InlineData("{\"foo\": [0, 1, 2, 3, 4, 5]}", ".foo[1, 4, 2, 3] |= empty", "{\"foo\":[0,5]}\n")]
    [InlineData("[4]", ".[2][3] = 1", "[4,null,[null,null,null,1]]\n")]
    [InlineData("{\"foo\": [11], \"bar\": 42}", ".foo[2].bar = 1", "{\"foo\":[11,null,{\"bar\":1}],\"bar\":42}\n")]
    [InlineData("{\"a\": null, \"b\": null}", "(.a as $x | .b) = \"b\"", "{\"a\":null,\"b\":\"b\"}\n")]
    [InlineData("{\"a\": {\"b\": [1, {\"b\": 3}]}}", "(.. | select(type == \"object\") | select((.b | type) == \"array\") | .b) |= .[0]", "{\"a\":{\"b\":1}}\n")]
    [InlineData("[\"hello\", true, false, [false], null]", ".[] //= .[0]", "[\"hello\",true,\"hello\",[false],\"hello\"]\n")]
    [InlineData("[0, 1, 2, 3, 4, 5, 6, 7]", ".[2:4] = ([], [\"a\", \"b\"], [\"a\", \"b\", \"c\"])", "[0,1,4,5,6,7]\n[0,1,\"a\",\"b\",4,5,6,7]\n[0,1,\"a\",\"b\",\"c\",4,5,6,7]\n")]
    [InlineData("[0, 1, 2]", ".[-1] = 5", "[0,1,5]\n")]
    [InlineData("[0, 1, 2]", ".[-2] = 5", "[0,5,2]\n")]
    [InlineData("[{\"error\": true}]", ".[] | .error = \"no, it is OK\"", "{\"error\":\"no, it is OK\"}\n")]
    [InlineData("{\"a\": 0, \"b\": 0, \"c\": 0}", "(.a, .b) = (1, 2)", "{\"a\":1,\"b\":1,\"c\":0}\n{\"a\":2,\"b\":2,\"c\":0}\n")]
    [InlineData("{\"a\": 1, \"b\": 2}", ".a = empty", "")]
    [InlineData("1", ". |= try 2", "2\n")]
    [InlineData("1", ". |= try 2 catch 3", "2\n")]
    [InlineData("null", "{foo: \"bar\"} | .foo |= .?", "{\"foo\":\"bar\"}\n")]
    [InlineData("{\"a\": 0, \"b\": 0}", ".a, .b = 1", "0\n{\"a\":0,\"b\":1}\n")]
    public async Task Jq_AssignUpdatesValues(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("null", "try (.foo[-1] = 0) catch .", "\"Out of bounds negative array index\"\n")]
    [InlineData("null", "try (.[999999999] = 0) catch .", "\"Array index too large\"\n")]
    [InlineData("null", "try ([range(3)] | .[(-1 | sqrt)] = 9) catch .", "\"Cannot set array element at NaN index\"\n")]
    [InlineData("null", "try (\"foobar\" | .[1.5:3.5] = \"xyz\") catch .", "\"Cannot update string slices\"\n")]
    [InlineData("[{\"a\": 0}, {\"a\": 1}]", "try ((reverse | .[].b) = 10) catch .", "\"Invalid path expression near attempt to iterate through [{\\\"a\\\":1},{\\\"a\\\":0}]\"\n")]
    [InlineData("[0, 1, 2]", "try (def x: reverse; x = 10) catch .", "\"Invalid path expression with result [2,1,0]\"\n")]
    [InlineData("null", "try (1 = 2) catch .", "\"Invalid path expression with result 1\"\n")]
    public async Task Jq_AssignReportsFailures(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_SliceAssignTruncatesFloats()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[0, 1, 2, 3, 4]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "[range(5)] | .[1.1] = 5")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[0,5,2,3,4]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_OptionalSuppressesBadPaths()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("null");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "(.[{}] = 0)?")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_ChainedUpdatesAreCompileErrors()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", ".a = .b = 1")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("unexpected token =", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
    [Theory]
    [InlineData("{\"a\": 0, \"b\": {\"x\": 1}}", ".a = .b | .a.x = 99", "{\"a\":{\"x\":99},\"b\":{\"x\":1}}\n")]
    [InlineData("{\"a\": {\"x\": 1}, \"b\": 0}", ".b = .a | .a.x = 99", "{\"a\":{\"x\":99},\"b\":{\"x\":1}}\n")]
    [InlineData("{\"a\": 1}", ".a |= (2, 3)", "{\"a\":2}\n")]
    [InlineData("{\"a\": 1}", "(.a, .a) |= . + 1", "{\"a\":3}\n")]
    [InlineData("{\"a\": 1, \"b\": 2}", "(.a, .b) |= . + 10", "{\"a\":11,\"b\":12}\n")]
    [InlineData("{}", ".a.b = 1", "{\"a\":{\"b\":1}}\n")]
    [InlineData("{}", ".a[2] = 1", "{\"a\":[null,null,1]}\n")]
    [InlineData("null", ".a.b.c.d.e = 1", "{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":1}}}}}\n")]
    [InlineData("{\"a\": 1}", "setpath([]; 5)", "5\n")]
    [InlineData("[1, 2, 3]", "getpath([-1])", "3\n")]
    [InlineData("[0, 1, 2, 3]", "del(.[1:3])", "[0,3]\n")]
    public async Task Jq_AssignmentPreservesIsolation(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_AssignRejectsIncompatiblePaths()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"a\": 5}");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".a.b = 1")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("cannot index number", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
}
