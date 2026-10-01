using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("{\"a\": 1}", "path(.a)", "[\n  \"a\"\n]\n")]
    [InlineData("{\"a\":{\"b\":1}}", "path(getpath([\"a\",\"b\"]))", "[\n  \"a\",\n  \"b\"\n]\n")]
    [InlineData("null", "path(.foo[0,1])", "[\n  \"foo\",\n  0\n]\n[\n  \"foo\",\n  1\n]\n")]
    [InlineData("[1,5,3]", "path(.[] | select(.>3))", "[\n  1\n]\n")]
    [InlineData("42", "path(.)", "[]\n")]
    [InlineData("null", "path(.a[0].b)", "[\n  \"a\",\n  0,\n  \"b\"\n]\n")]
    [InlineData("{\"a\":[{\"b\":1}]}", "[path(..)]", "[\n  [],\n  [\n    \"a\"\n  ],\n  [\n    \"a\",\n    0\n  ],\n  [\n    \"a\",\n    0,\n    \"b\"\n  ]\n]\n")]
    [InlineData("{\"a\":{\"b\":0}}", "path(.a[path(.b)[0]])", "[\n  \"a\",\n  \"b\"\n]\n")]
    [InlineData("[10, 20]", "path(.[])", "[\n  0\n]\n[\n  1\n]\n")]
    [InlineData("{\"a\": {\"b\": 1}}", "path(.a.b)", "[\n  \"a\",\n  \"b\"\n]\n")]
    [InlineData("[0, 1, 2]", "path(.[1, 2])", "[\n  1\n]\n[\n  2\n]\n")]
    [InlineData("[10, 20]", "path(first)", "[\n  0\n]\n")]
    [InlineData("[10, 20]", "path(last)", "[\n  -1\n]\n")]
    [InlineData("{\"a\": [1]}", "path(..)", "[]\n[\n  \"a\"\n]\n[\n  \"a\",\n  0\n]\n")]
    [InlineData("[0, 1, 2]", "def x: .[1, 2]; path(x)", "[\n  1\n]\n[\n  2\n]\n")]
    [InlineData("{\"a\": null, \"b\": null}", "path((.a as $x | .b))", "[\n  \"b\"\n]\n")]
    [InlineData("[0, 1, 2, 3]", "path(.[1.5:3.5])", "[\n  {\n    \"start\": 1.5,\n    \"end\": 3.5\n  }\n]\n")]
    [InlineData("[0, 1, 2, 3]", "path(.[1:2])", "[\n  {\n    \"start\": 1,\n    \"end\": 2\n  }\n]\n")]
    [InlineData("[0, 1, 2, 3]", "path(.[1:])", "[\n  {\n    \"start\": 1,\n    \"end\": null\n  }\n]\n")]
    [InlineData("[0, 1, 2, 3]", "path(.[:2])", "[\n  {\n    \"start\": null,\n    \"end\": 2\n  }\n]\n")]
    [InlineData("[0, 1, 2, 3]", "path(.[:])", "[\n  {\n    \"start\": null,\n    \"end\": null\n  }\n]\n")]
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
    public async Task Jq_PathsFilterKeepsSelectMultiplicity()
    {
        // Upstream paths(node_filter) is path(recurse|select(node_filter)):
        // every condition output counts, so later truthy probes still keep
        // the path and repeated truthy probes duplicate it.
        var host = new MockFileSystem();
        host.SetStandardInput("{\"a\": 1}");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "[paths((false, true))]")));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[\n  [\n    \"a\"\n  ]\n]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));

        var doubled = new MockFileSystem();
        doubled.SetStandardInput("{\"a\": 1}");
        var repeat = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "[paths((true, true))]")));
        Assert.Equal(0, await repeat.ExecuteAsync(doubled, CancellationToken.None));
        Assert.Equal("[\n  [\n    \"a\"\n  ],\n  [\n    \"a\"\n  ]\n]\n", doubled.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(doubled.GetOutput(JqFileDescriptor.StdErr));
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
    [InlineData("{\"bar\": 42, \"foo\": [\"a\", \"b\", \"c\", \"d\"]}", "[\"foo\", 1] as $p | getpath($p), setpath($p; 20), delpaths([$p])", "\"b\"\n{\n  \"bar\": 42,\n  \"foo\": [\n    \"a\",\n    20,\n    \"c\",\n    \"d\"\n  ]\n}\n{\n  \"bar\": 42,\n  \"foo\": [\n    \"a\",\n    \"c\",\n    \"d\"\n  ]\n}\n")]
    [InlineData("[[0], [0, 1], [0, 1, 2]]", "[.[] | setpath([2]; 42)]", "[\n  [\n    0,\n    null,\n    42\n  ],\n  [\n    0,\n    1,\n    42\n  ],\n  [\n    0,\n    1,\n    42\n  ]\n]\n")]
    [InlineData("[[0], [0, 1], [0, 1, 2]]", "[.[] | getpath([2])]", "[\n  null,\n  null,\n  2\n]\n")]
    [InlineData("[[0], [0, 1], [0, 1, 2]]", "[.[] | delpaths([[2]])]", "[\n  [\n    0\n  ],\n  [\n    0,\n    1\n  ],\n  [\n    0,\n    1\n  ]\n]\n")]
    [InlineData("[[{\"foo\": 2, \"x\": 1}], [{\"bar\": 2}]]", "[.[] | delpaths([[0, \"foo\"]])]", "[\n  [\n    {\n      \"x\": 1\n    }\n  ],\n  [\n    {\n      \"bar\": 2\n    }\n  ]\n]\n")]
    [InlineData("[1, 2, 3]", "delpaths([[-200]])", "[\n  1,\n  2,\n  3\n]\n")]
    [InlineData("{\"bar\": false}", "[\"foo\", 1] as $p | getpath($p), setpath($p; 20), delpaths([$p])", "null\n{\n  \"bar\": false,\n  \"foo\": [\n    null,\n    20\n  ]\n}\n{\n  \"bar\": false\n}\n")]
    [InlineData("[0, 1, 2, 3]", "path(.[1:3]) as $p | getpath($p)", "[\n  1,\n  2\n]\n")]
    [InlineData("[0, 1, 2, 3]", "path(.[1.5:3.5]) as $p | getpath($p)", "[\n  1,\n  2,\n  3\n]\n")]
    [InlineData("{\"a\": {\"b\": 1, \"c\": 2}}", "delpaths([[\"a\"], [\"a\", \"b\"]])", "{}\n")]
    [InlineData("{\"a\": {\"b\": 1, \"c\": 2}}", "delpaths([[\"a\", \"b\"], [\"a\"]])", "{}\n")]
    [InlineData("[0, 1, 2, 3]", "delpaths([[{\"start\": 1, \"end\": 3}]])", "[\n  0,\n  3\n]\n")]
    [InlineData("{\"a\": 1}", "delpaths([])", "{\n  \"a\": 1\n}\n")]
    [InlineData("{\"a\": 1}", "getpath([])", "{\n  \"a\": 1\n}\n")]
    [InlineData("[0, 1, 2, 3, 4, 5, 6, 7, 8, 9]", "delpaths([[{\"start\": 1.5, \"end\": 3.5}]])", "[\n  0,\n  4,\n  5,\n  6,\n  7,\n  8,\n  9\n]\n")]
    [InlineData("\"abcdef\"", "path(.[1:3]) as $p | getpath($p)", "\"bc\"\n")]
    [InlineData("[0, 1, 2, 3]", "setpath([{\"start\": 1, \"end\": 3}]; [9])", "[\n  0,\n  9,\n  3\n]\n")]
    [InlineData("[0, 1, 2, 3]", "try setpath([{\"start\": 1, \"end\": 3}]; 9) catch .", "\"A slice of an array can only be assigned another array\"\n")]
    [InlineData("[0]", "setpath([-1]; 1)", "[\n  1\n]\n")]
    [InlineData("{\"bar\": 42, \"foo\": [\"a\", \"b\", \"c\", \"d\"]}", "[\"foo\",1] as $p | getpath($p), setpath($p; 20), delpaths([$p])", "\"b\"\n{\n  \"bar\": 42,\n  \"foo\": [\n    \"a\",\n    20,\n    \"c\",\n    \"d\"\n  ]\n}\n{\n  \"bar\": 42,\n  \"foo\": [\n    \"a\",\n    \"c\",\n    \"d\"\n  ]\n}\n")]
    [InlineData("{\"bar\":false}", "[\"foo\",1] as $p | getpath($p), setpath($p; 20), delpaths([$p])", "null\n{\n  \"bar\": false,\n  \"foo\": [\n    null,\n    20\n  ]\n}\n{\n  \"bar\": false\n}\n")]
    [InlineData("[[0], [0,1], [0,1,2]]", "map(getpath([2])), map(setpath([2]; 42)), map(delpaths([[2]]))", "[\n  null,\n  null,\n  2\n]\n[\n  [\n    0,\n    null,\n    42\n  ],\n  [\n    0,\n    1,\n    42\n  ],\n  [\n    0,\n    1,\n    42\n  ]\n]\n[\n  [\n    0\n  ],\n  [\n    0,\n    1\n  ],\n  [\n    0,\n    1\n  ]\n]\n")]
    [InlineData("[[{\"foo\":2, \"x\":1}], [{\"bar\":2}]]", "map(delpaths([[0,\"foo\"]]))", "[\n  [\n    {\n      \"x\": 1\n    }\n  ],\n  [\n    {\n      \"bar\": 2\n    }\n  ]\n]\n")]
    [InlineData("{\"a\":{\"b\":1},\"x\":{\"y\":2}}", "delpaths([[\"a\",\"b\"]])", "{\n  \"a\": {},\n  \"x\": {\n    \"y\": 2\n  }\n}\n")]
    [InlineData("{\"a\":{\"b\":0, \"c\":1}}", "[getpath([\"a\",\"b\"], [\"a\",\"c\"])]", "[\n  0,\n  1\n]\n")]
    [InlineData("null", "getpath([\"a\",\"b\"])", "null\n")]
    [InlineData("null", "setpath([\"a\",\"b\"]; 1)", "{\n  \"a\": {\n    \"b\": 1\n  }\n}\n")]
    [InlineData("{\"a\":{\"b\":0}}", "setpath([\"a\",\"b\"]; 1)", "{\n  \"a\": {\n    \"b\": 1\n  }\n}\n")]
    [InlineData("null", "setpath([0,\"a\"]; 1)", "[\n  {\n    \"a\": 1\n  }\n]\n")]
    [InlineData("[]", "try [\"OK\", setpath([[1]]; 1)] catch [\"KO\", .]", "[\n  \"KO\",\n  \"expected a number for indexing an array but got: [1]\"\n]\n")]
    [InlineData("{\"hi\": \"hello\"}", "try [\"ok\", setpath([1]; 1)] catch [\"ko\", .]", "[\n  \"ko\",\n  \"Cannot index object with number (1)\"\n]\n")]
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
    [InlineData("{\"foo\": [0, 1, 2, 3, 4], \"bar\": [0, 1]}", "del(empty)", "{\n  \"foo\": [\n    0,\n    1,\n    2,\n    3,\n    4\n  ],\n  \"bar\": [\n    0,\n    1\n  ]\n}\n")]
    [InlineData("{\"foo\": [0, 1, 2, 3, 4], \"bar\": [0, 1]}", "del((.foo, .bar, .baz) | .[2, 3, 0])", "{\n  \"foo\": [\n    1,\n    4\n  ],\n  \"bar\": [\n    1\n  ]\n}\n")]
    [InlineData("{\"foo\": [0, 1, 2, 3, 4], \"bar\": [0, 1]}", "del(.foo[0], .bar[0], .foo, .baz.bar[0].x)", "{\n  \"bar\": [\n    1\n  ]\n}\n")]
    [InlineData("[1, null, 1e400, -1e400, 0.0, -0.0]", ".[] = 1", "[\n  1,\n  1,\n  1,\n  1,\n  1,\n  1\n]\n")]
    [InlineData("null", "pick(.a.b.c)", "{\n  \"a\": {\n    \"b\": {\n      \"c\": null\n    }\n  }\n}\n")]
    [InlineData("{\"a\": 1, \"b\": 2, \"c\": 3}", "pick(.a, .b)", "{\n  \"a\": 1,\n  \"b\": 2\n}\n")]
    [InlineData("{\"a\": 1}", "pick(.b)", "{\n  \"b\": null\n}\n")]
    [InlineData("{\"a\": 1}", "pick(empty)", "null\n")]
    [InlineData("[0, 1, 2, 3, 4, 5, 6, 7, 8, 9]", "del(.[1.5:3.5])", "[\n  0,\n  4,\n  5,\n  6,\n  7,\n  8,\n  9\n]\n")]
    [InlineData("[0, 1, 2, 3, 4, 5, 6, 7, 8, 9]", "del(.[1], .[-6], .[2], .[-3:9])", "[\n  0,\n  3,\n  5,\n  6,\n  9\n]\n")]
    [InlineData("[1, 2, 3]", "del(.[nan])", "[\n  1,\n  2,\n  3\n]\n")]
    [InlineData("[1, 2, 3]", "del(.[nan,nan])", "[\n  1,\n  2,\n  3\n]\n")]
    [InlineData("[[10, 20], 30]", "pick(first|first)", "[\n  [\n    10\n  ]\n]\n")]
    [InlineData("[1, 2]", "try pick(last) catch .", "\"Out of bounds negative array index\"\n")]
    [InlineData("[1,2,3,4]", "pick(.[2], .[0], .[0])", "[\n  1,\n  null,\n  3\n]\n")]
    [InlineData("{\"a\":[{\"b\":1}]}", "del(getpath([\"a\",0,\"b\"]))", "{\n  \"a\": [\n    {}\n  ]\n}\n")]
    [InlineData("[0,1,2,3,4,5,6,7]", "del(.[2:4],.[0],.[-2:])", "[\n  1,\n  4,\n  5\n]\n")]
    [InlineData("[\"foo\", \"bar\", \"baz\"]", "del(.[1, 2])", "[\n  \"foo\"\n]\n")]
    public async Task Jq_DelAndPickReshape(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("try getpath(0) catch .", "\"Path must be specified as an array\"\n")]
    [InlineData("try setpath(0; 1) catch .", "\"Path must be specified as an array\"\n")]
    [InlineData("try delpaths([0]) catch .", "\"Path must be specified as array, not number\"\n")]
    public async Task Jq_PathBuiltinsRequireArrayPaths(string filter, string expected)
    {
        // Non-array paths and path elements fail catchably with array-shaped
        // diagnostics on every path builtin, not just delpaths.
        var host = new MockFileSystem();
        host.SetStandardInput("{}");
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
    [InlineData("{\"message\": \"hello\"}", ".message = \"goodbye\"", "{\n  \"message\": \"goodbye\"\n}\n")]
    [InlineData("{\"bar\": 42}", ".foo = .bar", "{\n  \"bar\": 42,\n  \"foo\": 42\n}\n")]
    [InlineData("{\"foo\": 42}", ".foo |= . + 1", "{\n  \"foo\": 43\n}\n")]
    [InlineData("[1, 3, 5]", ".[] += 2, .[] *= 2, .[] -= 2, .[] /= 2, .[] %= 2", "[\n  3,\n  5,\n  7\n]\n[\n  2,\n  6,\n  10\n]\n[\n  -1,\n  1,\n  3\n]\n[\n  0.5,\n  1.5,\n  2.5\n]\n[\n  1,\n  1,\n  1\n]\n")]
    [InlineData("{\"foo\": 2}", ".foo += .foo", "{\n  \"foo\": 4\n}\n")]
    [InlineData("[{\"a\": 1, \"b\": 2}]", ".[0].a |= {\"old\": ., \"new\": (. + 1)}", "[\n  {\n    \"a\": {\n      \"old\": 1,\n      \"new\": 2\n    },\n    \"b\": 2\n  }\n]\n")]
    [InlineData("[{\"a\": 1, \"b\": 2}, {\"a\": 2, \"b\": 4}, {\"a\": 7, \"b\": 8}]", "def inc(x): x |= . + 1; inc(.[].a)", "[\n  {\n    \"a\": 2,\n    \"b\": 2\n  },\n  {\n    \"a\": 3,\n    \"b\": 4\n  },\n  {\n    \"a\": 8,\n    \"b\": 8\n  }\n]\n")]
    [InlineData("[0, 1, 2]", "def x: .[1, 2]; x = 10", "[\n  0,\n  10,\n  10\n]\n")]
    [InlineData("[\"a\", 1, true, null, [1], {\"k\": 1}]", ".[] = 1", "[\n  1,\n  1,\n  1,\n  1,\n  1,\n  1\n]\n")]
    [InlineData("[1, 5, 3, 0, 7]", "(.[] | select(. >= 2)) |= empty", "[\n  1,\n  0\n]\n")]
    [InlineData("[0, 1, 2, 3, 4, 5]", ".[] |= select(. % 2 == 0)", "[\n  0,\n  2,\n  4\n]\n")]
    [InlineData("{\"foo\": [0, 1, 2, 3, 4, 5]}", ".foo[1, 4, 2, 3] |= empty", "{\n  \"foo\": [\n    0,\n    5\n  ]\n}\n")]
    [InlineData("[4]", ".[2][3] = 1", "[\n  4,\n  null,\n  [\n    null,\n    null,\n    null,\n    1\n  ]\n]\n")]
    [InlineData("{\"foo\": [11], \"bar\": 42}", ".foo[2].bar = 1", "{\n  \"foo\": [\n    11,\n    null,\n    {\n      \"bar\": 1\n    }\n  ],\n  \"bar\": 42\n}\n")]
    [InlineData("{\"a\": null, \"b\": null}", "(.a as $x | .b) = \"b\"", "{\n  \"a\": null,\n  \"b\": \"b\"\n}\n")]
    [InlineData("[true, false, [5, true, [true, [false]], false]]", "(.. | select(type == \"boolean\")) |= if . then 1 else 0 end", "[\n  1,\n  0,\n  [\n    5,\n    1,\n    [\n      1,\n      [\n        0\n      ]\n    ],\n    0\n  ]\n]\n")]
    [InlineData("[1, [2, [3]]]", "(.. | numbers) |= . + 1", "[\n  2,\n  [\n    3,\n    [\n      4\n    ]\n  ]\n]\n")]
    [InlineData("{\"a\": {\"b\": [1, {\"b\": 3}]}}", "(.. | select(type == \"object\") | select((.b | type) == \"array\") | .b) |= .[0]", "{\n  \"a\": {\n    \"b\": 1\n  }\n}\n")]
    [InlineData("[\"hello\", true, false, [false], null]", ".[] //= .[0]", "[\n  \"hello\",\n  true,\n  \"hello\",\n  [\n    false\n  ],\n  \"hello\"\n]\n")]
    [InlineData("{}", ".a //= 1", "{\n  \"a\": 1\n}\n")]
    [InlineData("{}", ".a += 1", "{\n  \"a\": 1\n}\n")]
    [InlineData("[0, 1, 2, 3, 4, 5, 6, 7]", ".[2:4] = ([], [\"a\", \"b\"], [\"a\", \"b\", \"c\"])", "[\n  0,\n  1,\n  4,\n  5,\n  6,\n  7\n]\n[\n  0,\n  1,\n  \"a\",\n  \"b\",\n  4,\n  5,\n  6,\n  7\n]\n[\n  0,\n  1,\n  \"a\",\n  \"b\",\n  \"c\",\n  4,\n  5,\n  6,\n  7\n]\n")]
    [InlineData("[0, 1, 2]", ".[-1] = 5", "[\n  0,\n  1,\n  5\n]\n")]
    [InlineData("[0, 1, 2]", ".[-2] = 5", "[\n  0,\n  5,\n  2\n]\n")]
    [InlineData("[{\"error\": true}]", ".[] | .error = \"no, it is OK\"", "{\n  \"error\": \"no, it is OK\"\n}\n")]
    [InlineData("{\"a\": 0, \"b\": 0, \"c\": 0}", "(.a, .b) = (1, 2)", "{\n  \"a\": 1,\n  \"b\": 1,\n  \"c\": 0\n}\n{\n  \"a\": 2,\n  \"b\": 2,\n  \"c\": 0\n}\n")]
    [InlineData("{\"a\": 1, \"b\": 2}", ".a = empty", "")]
    [InlineData("1", ". |= try 2", "2\n")]
    [InlineData("1", ". |= try 2 catch 3", "2\n")]
    [InlineData("null", "{foo: \"bar\"} | .foo |= .?", "{\n  \"foo\": \"bar\"\n}\n")]
    [InlineData("{\"a\": 0, \"b\": 0}", ".a, .b = 1", "0\n{\n  \"a\": 0,\n  \"b\": 1\n}\n")]
    [InlineData("null", "[range(10)] | .[1.5:3.5] = [\"xyz\"]", "[\n  0,\n  \"xyz\",\n  4,\n  5,\n  6,\n  7,\n  8,\n  9\n]\n")]
    [InlineData("null", "try ([range(10)] | .[1.5:3.5] = [\"xyz\"]) catch .", "[\n  0,\n  \"xyz\",\n  4,\n  5,\n  6,\n  7,\n  8,\n  9\n]\n")]
    [InlineData("[null,{\"b\":0},{\"a\":0},{\"a\":null},{\"a\":[0,1]},{\"a\":{\"b\":1}},{\"a\":[{}]},{\"a\":[{\"c\":3}]}]", ".[] | try (getpath([\"a\",0,\"b\"]) |= 5) catch .", "{\n  \"a\": [\n    {\n      \"b\": 5\n    }\n  ]\n}\n{\n  \"b\": 0,\n  \"a\": [\n    {\n      \"b\": 5\n    }\n  ]\n}\n\"Cannot index number with number (0)\"\n{\n  \"a\": [\n    {\n      \"b\": 5\n    }\n  ]\n}\n\"Cannot index number with string (\\\"b\\\")\"\n\"Cannot index object with number (0)\"\n{\n  \"a\": [\n    {\n      \"b\": 5\n    }\n  ]\n}\n{\n  \"a\": [\n    {\n      \"c\": 3,\n      \"b\": 5\n    }\n  ]\n}\n")]
    [InlineData("{\"a\":{\"b\":0}}", "getpath([\"a\",\"b\"]) = 5", "{\n  \"a\": {\n    \"b\": 5\n  }\n}\n")]
    [InlineData("null", "getpath([\"a\",\"b\"]) = 5", "{\n  \"a\": {\n    \"b\": 5\n  }\n}\n")]
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
    [InlineData("5", "try (.a = 1) catch .", "\"cannot index number with string \\\"a\\\"\"\n")]
    [InlineData("\"s\"", "try (.a = 1) catch .", "\"cannot index string with string \\\"a\\\"\"\n")]
    [InlineData("true", "try (.error = 1) catch .", "\"cannot index boolean with string \\\"error\\\"\"\n")]
    [InlineData("[{\"a\":0},{\"a\":1}]", "try ((map(select(.a == 1))[].a) |= .+1) catch .", "\"Invalid path expression near attempt to iterate through [{\\\"a\\\":1}]\"\n")]
    [InlineData("null", "try (.foo[-2] = 0) catch .", "\"Out of bounds negative array index\"\n")]
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
        Assert.Equal("[\n  0,\n  5,\n  2,\n  3,\n  4\n]\n", host.GetOutput(JqFileDescriptor.StdOut));
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
    [InlineData("{\"a\": 0, \"b\": {\"x\": 1}}", ".a = .b | .a.x = 99", "{\n  \"a\": {\n    \"x\": 99\n  },\n  \"b\": {\n    \"x\": 1\n  }\n}\n")]
    [InlineData("{\"a\": {\"x\": 1}, \"b\": 0}", ".b = .a | .a.x = 99", "{\n  \"a\": {\n    \"x\": 99\n  },\n  \"b\": {\n    \"x\": 1\n  }\n}\n")]
    [InlineData("{\"a\": 1}", ".a |= (2, 3)", "{\n  \"a\": 2\n}\n")]
    [InlineData("{\"a\": 1}", "(.a, .a) |= . + 1", "{\n  \"a\": 3\n}\n")]
    [InlineData("{\"a\": 1, \"b\": 2}", "(.a, .b) |= . + 10", "{\n  \"a\": 11,\n  \"b\": 12\n}\n")]
    [InlineData("null", "(.a, .b) |= range(3)", "{\n  \"a\": 0,\n  \"b\": 0\n}\n")]
    [InlineData("[{\"a\":1,\"b\":2}]", ".[0].a |= {\"old\":., \"new\":(.+1)}", "[\n  {\n    \"a\": {\n      \"old\": 1,\n      \"new\": 2\n    },\n    \"b\": 2\n  }\n]\n")]
    [InlineData("{\"foo\":[0,1,2,3,4,5]}", ".foo[1,4,2,3] |= empty", "{\n  \"foo\": [\n    0,\n    5\n  ]\n}\n")]
    [InlineData("{\"a\": {\"b\": [1, {\"b\": 3}]}}", "(.. | select(type == \"object\" and has(\"b\") and (.b | type) == \"array\")|.b) |= .[0]", "{\n  \"a\": {\n    \"b\": 1\n  }\n}\n")]
    [InlineData("[true,false,[5,true,[true,[false]],false]]", "(..|select(type==\"boolean\")) |= if . then 1 else 0 end", "[\n  1,\n  0,\n  [\n    5,\n    1,\n    [\n      1,\n      [\n        0\n      ]\n    ],\n    0\n  ]\n]\n")]
    [InlineData("{}", ".a.b = 1", "{\n  \"a\": {\n    \"b\": 1\n  }\n}\n")]
    [InlineData("{}", ".a[2] = 1", "{\n  \"a\": [\n    null,\n    null,\n    1\n  ]\n}\n")]
    [InlineData("null", ".a.b.c.d.e = 1", "{\n  \"a\": {\n    \"b\": {\n      \"c\": {\n        \"d\": {\n          \"e\": 1\n        }\n      }\n    }\n  }\n}\n")]
    [InlineData("{\"a\": 1}", "setpath([]; 5)", "5\n")]
    [InlineData("[1, 2, 3]", "getpath([-1])", "3\n")]
    [InlineData("[0, 1, 2, 3]", "del(.[1:3])", "[\n  0,\n  3\n]\n")]
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
