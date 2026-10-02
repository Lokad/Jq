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
    [InlineData("[1,2,3]", "path(.[[1]])", "[\n  [\n    1\n  ]\n]\n")]
    [InlineData("[1,2,3]", "path(.[[1,2]])", "[\n  [\n    1,\n    2\n  ]\n]\n")]
    [InlineData("[1,2,3]", "path(.[[9]])", "[\n  [\n    9\n  ]\n]\n")]
    [InlineData("[1,2,3]", "path(.[{\"start\":1}])", "[\n  {\n    \"start\": 1\n  }\n]\n")]
    [InlineData("[1,2,3]", "path(.[[1]][0])", "[\n  [\n    1\n  ],\n  0\n]\n")]
    [InlineData("[1,2,3]", "path(.[{\"start\":1}][0])", "[\n  {\n    \"start\": 1\n  },\n  0\n]\n")]
    [InlineData("5", "path(.[0]?)", "")]
    [InlineData("null", "path(.[null]?)", "")]
    [InlineData("[1,[[],{\"a\":2}]]", "[paths]", "[\n  [\n    0\n  ],\n  [\n    1\n  ],\n  [\n    1,\n    0\n  ],\n  [\n    1,\n    1\n  ],\n  [\n    1,\n    1,\n    \"a\"\n  ]\n]\n")]
    // Empty-string keys and empty containers enumerate like any other leaf path.
    [InlineData("{\"\":1}", "[paths]", "[\n  [\n    \"\"\n  ]\n]\n")]
    [InlineData("{\"a\":{},\"b\":[]}", "[paths]", "[\n  [\n    \"a\"\n  ],\n  [\n    \"b\"\n  ]\n]\n")]
    [InlineData("1", "path(.)", "[]\n")]
    // Scalar inputs enumerate no descent paths, while path(.) still reports the root.
    [InlineData("1", "[paths]", "[]\n")]
    [InlineData("\"a\"", "[paths]", "[]\n")]
    [InlineData("null", "[paths]", "[]\n")]
    [InlineData("null", "[path(..)]", "[\n  []\n]\n")]
    [InlineData("{\"a\":[{\"b\":0}]}", "try path(.a | map(select(.b == 0))) catch .", "\"Invalid path expression with result [{\\\"b\\\":0}]\"\n")]
    [InlineData("{\"a\":[{\"b\":0}]}", "try path(.a | map(select(.b == 0)) | .[0]) catch .", "\"Invalid path expression near attempt to access element 0 of [{\\\"b\\\":0}]\"\n")]
    [InlineData("{\"a\":[{\"b\":0}]}", "try path(.a | map(select(.b == 0)) | .c) catch .", "\"Invalid path expression near attempt to access element \\\"c\\\" of [{\\\"b\\\":0}]\"\n")]
    [InlineData("{\"a\":[{\"b\":0}]}", "try path(.a | map(select(.b == 0)) | .[]) catch .", "\"Invalid path expression near attempt to iterate through [{\\\"b\\\":0}]\"\n")]
    [InlineData("\"a\"", "try path(tostring) catch .", "\"Invalid path expression with result \\\"a\\\"\"\n")]
    [InlineData("5", "try path(floor) catch .", "\"Invalid path expression with result 5\"\n")]
    [InlineData("\"a\"", "try path(sub(\"zzz\"; \"y\")) catch .", "\"Invalid path expression with result \\\"a\\\"\"\n")]
    [InlineData("\"a\"", "try path(scan(\"a\")) catch .", "\"Invalid path expression with result \\\"a\\\"\"\n")]
    [InlineData("null", "1 | try path(1 + 0) catch .", "\"Invalid path expression with result 1\"\n")]
    [InlineData("null", "{} | try path({} * {}) catch .", "\"Invalid path expression with result {}\"\n")]
    [InlineData("null", "\"ab\" | try path(. * 1) catch .", "\"Invalid path expression with result \\\"ab\\\"\"\n")]
    [InlineData("null", "true | path(. and true)", "[]\n")]
    [InlineData("null", "1 | path(1 // 2)", "[]\n")]
    [InlineData("null", "0 | try path(range(1)) catch .", "\"Invalid path expression with result 0\"\n")]
    [InlineData("{\"a\":1}", "try path(del(.zzz)) catch .", "\"Invalid path expression with result {\\\"a\\\":1}\"\n")]
    [InlineData("{\"a\":[{\"b\":0}]}", "def f: [.[]]; try path(.a | f) catch .", "\"Invalid path expression with result [{\\\"b\\\":0}]\"\n")]
    [InlineData("{\"a\":[{\"b\":0}]}", "def id: .; path(.a | id)", "[\n  \"a\"\n]\n")]
    [InlineData("{\"a\":[{\"b\":0}]}", "def f: map(.); try path(.a | f) catch .", "\"Invalid path expression with result [{\\\"b\\\":0}]\"\n")]
    [InlineData("{\"a\":[{\"b\":0}]}", "def s: select(.b == 0); path(.a[] | s)", "[\n  \"a\",\n  0\n]\n")]
    [InlineData("{\"a\": [{\"b\": 0}]}", "try path(.a | (map(select(.b == 0)))) catch .", "\"Invalid path expression with result [{\\\"b\\\":0}]\"\n")]
    [InlineData("{\"a\": [{\"b\": 0}]}", "try path(.a | map(select(.b == 0))?) catch .", "\"Invalid path expression with result [{\\\"b\\\":0}]\"\n")]
    [InlineData("{\"a\": [{\"b\": 0}]}", "try path(if true then .a else . end) catch .", "[\n  \"a\"\n]\n")]
    [InlineData("{\"a\": [{\"b\": 0}]}", "try path(if true then map(select(.b == 0)) else .a end) catch .", "\"cannot index array with string \\\"b\\\"\"\n")]
    [InlineData("{\"a\": [{\"b\": 0}]}", "try path(.a | (map(select(.b == 0)) // .b)) catch .", "\"Invalid path expression with result [{\\\"b\\\":0}]\"\n")]
    [InlineData("{\"a\":[{\"b\":0}]}", "def m: map(select(.b == 0)); try path(.a | m) catch .", "\"Invalid path expression with result [{\\\"b\\\":0}]\"\n")]
    [InlineData("\"a\"", "try path(\"a\") catch .", "\"Invalid path expression with result \\\"a\\\"\"\n")]
    [InlineData("1", "path(1)", "[]\n")]
    [InlineData("null", "1 | path(1.0)", "[]\n")]
    [InlineData("null", "9007199254740993 | path(9007199254740993)", "[]\n")]
    [InlineData("true", "path(true)", "[]\n")]
    [InlineData("null", "path(null)", "[]\n")]
    [InlineData("[1]", "try path([1]) catch .", "\"Invalid path expression with result [1]\"\n")]
    [InlineData("\"ab\"", "try path(\"a\\(\"b\")\") catch .", "\"Invalid path expression with result \\\"ab\\\"\"\n")]
    [InlineData("-0.0", "try path(0.0) catch .", "\"Invalid path expression with result 0\"\n")]
    [InlineData("{\"a\":1}", "try path(map_values(.)) catch .", "\"Invalid path expression with result {\\\"a\\\":1}\"\n")]
    [InlineData("{\"a\":1}", "try path(with_entries(.)) catch .", "\"Invalid path expression with result {\\\"a\\\":1}\"\n")]
    [InlineData("[1]", "try path(flatten) catch .", "\"Invalid path expression with result [1]\"\n")]
    [InlineData("[[1]]", "try path(transpose) catch .", "\"Invalid path expression with result [[1]]\"\n")]
    [InlineData("[1]", "try path(sort) catch .", "\"Invalid path expression with result [1]\"\n")]
    [InlineData("[{\"a\":1}]", "try path(sort_by(.a)) catch .", "\"Invalid path expression with result [{\\\"a\\\":1}]\"\n")]
    [InlineData("[1]", "try path(unique) catch .", "\"Invalid path expression with result [1]\"\n")]
    [InlineData("[{\"a\":1}]", "try path(unique_by(.a)) catch .", "\"Invalid path expression with result [{\\\"a\\\":1}]\"\n")]
    [InlineData("[[1]]", "try path(reverse) catch .", "\"Invalid path expression with result [[1]]\"\n")]
    [InlineData("[0]", "try path(keys) catch .", "\"Invalid path expression with result [0]\"\n")]
    [InlineData("{\"a\":1}", "try path(delpaths([])) catch .", "\"Invalid path expression with result {\\\"a\\\":1}\"\n")]
    [InlineData("[1]", "try path(setpath([0]; 1)) catch .", "\"Invalid path expression with result [1]\"\n")]
    [InlineData("\"\"", "try path(@base64) catch .", "\"Invalid path expression with result \\\"\\\"\"\n")]
    // Heap variable reads travel untracked even when value-equal: upstream LOADV shares the stored pointer (execute.c LOADV/STOREV with path_intact/jv_identical), so an identity alias succeeds there and fails here.
    [InlineData("{\"a\":1}", "try path(. as $x | $x) catch .", "\"Invalid path expression with result {\\\"a\\\":1}\"\n")]
    [InlineData("{\"a\":1}", "try path({\"a\":1} as $x | $x) catch .", "\"Invalid path expression with result {\\\"a\\\":1}\"\n")]
    [InlineData("{\"a\":[{\"b\":0}]}", "try path(.a as $x | $x) catch .", "\"Invalid path expression with result [{\\\"b\\\":0}]\"\n")]
    [InlineData("1", "path(1 as $x | $x)", "[]\n")]
    [InlineData("null", "try path(1 as $x | $x) catch .", "\"Invalid path expression with result 1\"\n")]
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
    // Missing deep paths delete nothing while invalid segments fail staged, so unchanged subtrees never resurface attached.
    [InlineData("{\"a\":{\"b\":1},\"c\":2}", "delpaths([[\"a\",\"x\",\"y\"]])", "{\n  \"a\": {\n    \"b\": 1\n  },\n  \"c\": 2\n}\n")]
    [InlineData("{\"a\":{\"b\":1}}", "try delpaths([[\"a\",null]]) catch .", "\"expected a string for object key but got: null\"\n")]
    [InlineData("[1,2]", "try delpaths([[null]]) catch .", "\"expected a number for indexing an array but got: null\"\n")]
    [InlineData("{\"a\": {\"b\": 1, \"c\": 2}}", "delpaths([[\"a\", \"b\"], [\"a\"]])", "{}\n")]
    [InlineData("[0, 1, 2, 3]", "delpaths([[{\"start\": 1, \"end\": 3}]])", "[\n  0,\n  3\n]\n")]
    [InlineData("{\"a\": 1}", "delpaths([])", "{\n  \"a\": 1\n}\n")]
    [InlineData("{\"a\": 1}", "getpath([])", "{\n  \"a\": 1\n}\n")]
    [InlineData("[0, 1, 2, 3, 4, 5, 6, 7, 8, 9]", "delpaths([[{\"start\": 1.5, \"end\": 3.5}]])", "[\n  0,\n  4,\n  5,\n  6,\n  7,\n  8,\n  9\n]\n")]
    [InlineData("\"abcdef\"", "path(.[1:3]) as $p | getpath($p)", "\"bc\"\n")]
    [InlineData("[0, 1, 2, 3]", "setpath([{\"start\": 1, \"end\": 3}]; [9])", "[\n  0,\n  9,\n  3\n]\n")]
    [InlineData("[0, 1, 2, 3]", "try setpath([{\"start\": 1, \"end\": 3}]; 9) catch .", "\"A slice of an array can only be assigned another array\"\n")]
    [InlineData("[0]", "setpath([-1]; 1)", "[\n  1\n]\n")]
    // Negative setpath indices resolve from the end up to the exact first element, then fail like the reference.
    [InlineData("[1,2,3]", "setpath([-3]; 9)", "[\n  9,\n  2,\n  3\n]\n")]
    [InlineData("[1,2,3]", "try setpath([-4]; 9) catch .", "\"Out of bounds negative array index\"\n")]
    // Out-of-range reads yield null without failing, at any magnitude.
    [InlineData("[1,2,3]", ".[999999999999]", "null\n")]
    [InlineData("[1,2,3]", ".[-999999999999]", "null\n")]
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
    [InlineData("null", "setpath([\"a\",\"b\",\"c\"]; 1)", "{\n  \"a\": {\n    \"b\": {\n      \"c\": 1\n    }\n  }\n}\n")]
    [InlineData("{}", "getpath([\"a\"])", "null\n")]
    [InlineData("{\"a\":1}", "setpath([]; 5)", "5\n")]
    [InlineData("{\"a\":1}", "getpath([])", "{\n  \"a\": 1\n}\n")]
    [InlineData("{\"a\":1}", "getpath(path(.a))", "1\n")]
    [InlineData("{\"a\":0,\"b\":0}", "setpath(path(.a); 1)", "{\n  \"a\": 1,\n  \"b\": 0\n}\n")]
    [InlineData("[10,20]", "[getpath(path(.[]))]", "[\n  10,\n  20\n]\n")]
    // No enumerated paths means no setpath combinations, so the output is empty.
    [InlineData("{\"a\":1}", "setpath(path(empty); 1)", "")]
    [InlineData("null", "getpath(path(.a))", "null\n")]
    [InlineData("[1,2]", "setpath(path(.[]); 0)", "[\n  0,\n  2\n]\n[\n  1,\n  0\n]\n")]
    [InlineData("{\"a\":1}", "[getpath(paths)]", "[\n  1\n]\n")]
    [InlineData("{\"a\":1}", "delpaths([paths])", "{}\n")]
    // Mistyped descents report container and key kinds like the reference
    // probe read; fractional indices truncate toward zero; overlapping
    // deletions resolve order-independently through sorted groups.
    [InlineData("[1,2]", "try getpath([\"a\"]) catch .", "\"Cannot index array with string (\\\"a\\\")\"\n")]
    [InlineData("[10,20,30]", "getpath([1.5])", "20\n")]
    [InlineData("[10,20,30]", "getpath([-1.5])", "30\n")]
    [InlineData("{\"a\":1}", "try getpath([0]) catch .", "\"Cannot index object with number (0)\"\n")]
    [InlineData("[10,20,30]", "setpath([1.5]; 99)", "[\n  10,\n  99,\n  30\n]\n")]
    [InlineData("[10,20]", "try setpath([\"a\"]; 99) catch .", "\"Cannot index array with string (\\\"a\\\")\"\n")]
    [InlineData("{\"a\":{\"b\":1,\"c\":2}}", "delpaths([[\"a\"],[\"a\",\"b\"]])", "{}\n")]
    [InlineData("{\"a\":{\"b\":1,\"c\":2}}", "delpaths([[\"a\",\"b\"],[\"a\"]])", "{}\n")]
    // Slice updates outside arrays report component and container kinds like
    // the reference jv_set fallthrough; getpath reads slice components too.
    [InlineData("5", "try setpath([{start:0,end:1}]; [9]) catch .", "\"Cannot update field at object index of number\"\n")]
    [InlineData("true", "try setpath([{start:0,end:1}]; [9]) catch .", "\"Cannot update field at object index of boolean\"\n")]
    [InlineData("{\"a\":1}", "try setpath([{start:0,end:1}]; [9]) catch .", "\"Cannot update field at object index of object\"\n")]
    [InlineData("[10,20,30]", "getpath([{start:1,end:2}])", "[\n  20\n]\n")]
    // Fractional path-value components resolve start-down/end-up through the shared resolution in every builtin.
    [InlineData("[10,20,30]", "getpath([{start:0.5,end:2}])", "[\n  10,\n  20\n]\n")]
    [InlineData("[10,20,30]", "setpath([{start:0.5,end:2}]; [9])", "[\n  9,\n  30\n]\n")]
    [InlineData("[10,20,30]", "delpaths([[{start:0.5,end:2}]])", "[\n  30\n]\n")]
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
    // A NaN index reads null while sets fail and dels pass through.
    [InlineData("[1, 2, 3]", ".[nan]", "null\n")]
    // NaN elements still travel intact, so iterator targets resolve like the reference.
    [InlineData("[1,null,Infinity,-Infinity,NaN,-NaN]", ".[] = 1", "[\n  1,\n  1,\n  1,\n  1,\n  1,\n  1\n]\n")]
    [InlineData("[nan]", "path(.[])", "[\n  0\n]\n")]
    [InlineData("[[10, 20], 30]", "pick(first|first)", "[\n  [\n    10\n  ]\n]\n")]
    [InlineData("[1, 2]", "try pick(last) catch .", "\"Out of bounds negative array index\"\n")]
    [InlineData("[1,2,3,4]", "pick(.[2], .[0], .[0])", "[\n  1,\n  null,\n  3\n]\n")]
    // Slice and iterator path expressions rebuild through the same path synthesis.
    [InlineData("[0,1,2,3]", "pick(.[1:3])", "[\n  1,\n  2\n]\n")]
    [InlineData("[1,2]", "pick(.[])", "[\n  1,\n  2\n]\n")]
    [InlineData("[1,2]", "pick(.[0])", "[\n  1\n]\n")]
    [InlineData("{\"a\":1}", "try pick(\"a\") catch .", "\"Invalid path expression with result \\\"a\\\"\"\n")]
    [InlineData("[1,2]", "try pick(5) catch .", "\"Invalid path expression with result 5\"\n")]
    [InlineData("{\"a\":[{\"b\":1}]}", "del(getpath([\"a\",0,\"b\"]))", "{\n  \"a\": [\n    {}\n  ]\n}\n")]
    [InlineData("[0,1,2,3,4,5,6,7]", "del(.[2:4],.[0],.[-2:])", "[\n  1,\n  4,\n  5\n]\n")]
    [InlineData("[\"foo\", \"bar\", \"baz\"]", "del(.[1, 2])", "[\n  \"foo\"\n]\n")]
    [InlineData("{\"foo\": [0,1,2,3,4], \"bar\": [0,1]}", "del(.), del(empty), del((.foo,.bar,.baz) | .[2,3,0]), del(.foo[0], .bar[0], .foo, .baz.bar[0].x)", "null\n{\n  \"foo\": [\n    0,\n    1,\n    2,\n    3,\n    4\n  ],\n  \"bar\": [\n    0,\n    1\n  ]\n}\n{\n  \"foo\": [\n    1,\n    4\n  ],\n  \"bar\": [\n    1\n  ]\n}\n{\n  \"bar\": [\n    1\n  ]\n}\n")]
    [InlineData("null", "try delpaths([[range(10001) | 0]]) catch .", "\"Path too deep\"\n")]
    [InlineData("{\"a\":1}", "delpaths([[]])", "null\n")]
    [InlineData("{\"a\":1}", "delpaths([])", "{\n  \"a\": 1\n}\n")]
    [InlineData("{\"a\": 1, \"b\": {\"c\": 2, \"d\": 3}, \"e\": 4}", "pick(.a, .b.c, .x)", "{\n  \"a\": 1,\n  \"b\": {\n    \"c\": 2\n  },\n  \"x\": null\n}\n")]
    [InlineData("{\"foo\": 42, \"bar\": 9001, \"baz\": 42}", "del(.foo)", "{\n  \"bar\": 9001,\n  \"baz\": 42\n}\n")]
    [InlineData("{\"a\":[{\"b\":0}]}", "try del(.a | map(select(.b == 0))) catch .", "\"Invalid path expression with result [{\\\"b\\\":0}]\"\n")]
    [InlineData("{\"a\":[{\"b\":0}]}", "del(.a | .[])", "{\n  \"a\": []\n}\n")]
    [InlineData("{\"a\":[{\"b\":0}]}", "try pick(.a | map(.)) catch .", "\"Invalid path expression with result [{\\\"b\\\":0}]\"\n")]
    [InlineData("[{\"b\":0},{\"b\":1}]", "del(.[] | select(.b == 0))", "[\n  {\n    \"b\": 1\n  }\n]\n")]
    [InlineData("[1,2,3]", "del(.[{\"start\":1}])", "[\n  1\n]\n")]
    [InlineData("[1,2,3]", "del(.[{\"start\":1,\"end\":2}])", "[\n  1,\n  3\n]\n")]
    [InlineData("{\"a\":[{\"b\":0}]}", "pick(.a)", "{\n  \"a\": [\n    {\n      \"b\": 0\n    }\n  ]\n}\n")]
    [InlineData("{\"a\":1}", "try del(.a as $x | $x) catch .", "\"Invalid path expression with result 1\"\n")]
    // Fresh path arrays are untracked, so path() over them fails like the reference delpaths([path(f)]) and pick desugars.
    [InlineData("{\"a\":1}", "try del(paths) catch .", "\"Invalid path expression with result [\\\"a\\\"]\"\n")]
    [InlineData("{\"a\":1}", "try pick(paths) catch .", "\"Invalid path expression with result [\\\"a\\\"]\"\n")]
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
    [InlineData("try getpath(\"a\") catch .", "\"Path must be specified as an array\"\n")]
    [InlineData("try setpath(0; 1) catch .", "\"Path must be specified as an array\"\n")]
    [InlineData("try delpaths([0]) catch .", "\"Path must be specified as array, not number\"\n")]
    [InlineData("try getpath(null) catch .", "\"Path must be specified as an array\"\n")]
    [InlineData("try setpath(null; 1) catch .", "\"Path must be specified as an array\"\n")]
    [InlineData("try delpaths(null) catch .", "\"Paths must be specified as an array\"\n")]
    [InlineData("try getpath([null]) catch .", "\"expected a string for object key but got: null\"\n")]
    [InlineData("try setpath([null]; 1) catch .", "\"expected a string for object key but got: null\"\n")]
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
    [InlineData("[0,1,2,3,4,5,6,7]", ".[2:4] = ([], [\"a\",\"b\"], [\"a\",\"b\",\"c\"])", "[\n  0,\n  1,\n  4,\n  5,\n  6,\n  7\n]\n[\n  0,\n  1,\n  \"a\",\n  \"b\",\n  4,\n  5,\n  6,\n  7\n]\n[\n  0,\n  1,\n  \"a\",\n  \"b\",\n  \"c\",\n  4,\n  5,\n  6,\n  7\n]\n")]
    [InlineData("null", "(.a, .b) = range(3)", "{\n  \"a\": 0,\n  \"b\": 0\n}\n{\n  \"a\": 1,\n  \"b\": 1\n}\n{\n  \"a\": 2,\n  \"b\": 2\n}\n")]
    [InlineData("{\"a\": {\"b\": 10}, \"b\": 20}", ".a |= .b", "{\n  \"a\": 10,\n  \"b\": 20\n}\n")]
    [InlineData("{\"foo\": 42}", ".foo += 1", "{\n  \"foo\": 43\n}\n")]
    [InlineData("[{\"error\":true}]", ".[] | .error = \"no, it's OK\"", "{\n  \"error\": \"no, it's OK\"\n}\n")]
    [InlineData("{\"a\":[{\"b\":0}]}", "try ((.a | map(select(.b == 0))) = 1) catch .", "\"Invalid path expression with result [{\\\"b\\\":0}]\"\n")]
    [InlineData("{\"a\":[{\"b\":0}]}", "(.a | .[0]) = 1", "{\n  \"a\": [\n    1\n  ]\n}\n")]
    [InlineData("{\"a\":[{\"b\":0}]}", "try ((.a | [.[]]) = 1) catch .", "\"Invalid path expression with result [{\\\"b\\\":0}]\"\n")]
    [InlineData("[{\"b\":0},{\"b\":1}]", "(.[] | select(.b == 0) | .b) |= . + 1", "[\n  {\n    \"b\": 1\n  },\n  {\n    \"b\": 1\n  }\n]\n")]
    [InlineData("[1,2,3]", ".[{\"start\":1}] = [9]", "[\n  1,\n  9\n]\n")]
    [InlineData("[1,2,3]", ".[{\"start\":1,\"end\":2}] = [9,8]", "[\n  1,\n  9,\n  8,\n  3\n]\n")]
    [InlineData("[1,2,3]", ".[{\"start\":1}] |= . + [9]", "[\n  1,\n  2,\n  3,\n  9\n]\n")]
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
    [InlineData("[{\"a\":0},{\"a\":1}]", "try ((map(select(.a == 1))[].b) = 10) catch .", "\"Invalid path expression near attempt to iterate through [{\\\"a\\\":1}]\"\n")]
    // Variable-bound update targets follow the path rule: heap reads fail even on coincidence, including whole-input identity aliases (upstream LOADV shares the stored pointer, so `(. as $x | $x) = 2` succeeds there).
    [InlineData("{\"a\":1}", "try ((.a as $x | $x) = 2) catch .", "\"Invalid path expression with result 1\"\n")]
    [InlineData("{\"a\":1}", "try ((.a as $x | $x) |= . + 1) catch .", "\"Invalid path expression with result 1\"\n")]
    [InlineData("{\"a\":1}", "try ((. as $x | $x) = 2) catch .", "\"Invalid path expression with result {\\\"a\\\":1}\"\n")]
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
    [InlineData("null", "[{a:1}] | .[] | .a=999", "{\n  \"a\": 999\n}\n")]
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
