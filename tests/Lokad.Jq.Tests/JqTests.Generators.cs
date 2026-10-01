using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("[10,20,30] | .[0,2]", "10\n30\n")]
    [InlineData("(([1,2]),([3,4]))[(0,1)]", "1\n3\n2\n4\n")]
    [InlineData("{\"a\":1,\"b\":2} | .[\"a\",\"b\"]", "1\n2\n")]
    [InlineData("[10, 20, 30] | .[1e18]", "null\n")]
    [InlineData("[1,2,3] | [.[-4,-3,-2,-1,0,1,2,3]]", "[\n  null,\n  1,\n  2,\n  3,\n  1,\n  2,\n  3,\n  null\n]\n")]
    [InlineData("{\"e0\": 1, \"E1\": 2, \"E\": 3} | .e0, .E1, .E-1, .E+1", "1\n2\n2\n4\n")]
    [InlineData("[10, 20, 30] | .[-1e18]", "null\n")]
    [InlineData("[10, 20, 30] | .[3000000000]", "null\n")]
    [InlineData("[10, 20, 30] | .[1.5]", "20\n")]
    [InlineData("[10, 20, 30] | .[-2.5]", "20\n")]
    [InlineData("1000000000000000000 | [][.]", "null\n")]
    [InlineData("{\"foo\": 42} | .[\"foo\"]", "42\n")]
    [InlineData("[1,2] | [.foo?]", "[]\n")]
    [InlineData("[\"a\",\"b\",\"c\",\"d\",\"e\"] | .[:3]", "[\n  \"a\",\n  \"b\",\n  \"c\"\n]\n")]
    [InlineData("[\"a\",\"b\",\"c\",\"d\",\"e\"] | .[4,2]", "\"e\"\n\"c\"\n")]
    [InlineData("[{\"name\":\"JSON\", \"good\":true}, {\"name\":\"XML\", \"good\":false}] | .[] | .name", "\"JSON\"\n\"XML\"\n")]
    [InlineData("{\"foo\": {\"bar\": 42}, \"bar\": \"badvalue\"} | .foo | .bar", "42\n")]
    [InlineData("{\"foo\": {\"bar\": 42}, \"bar\": \"badvalue\"} | .foo.bar", "42\n")]
    [InlineData("{\"foo_bar\": 2} | .foo_bar", "2\n")]
    [InlineData("{\"foo\": {\"bar\": 42}, \"bar\": \"badvalue\"} | .[\"foo\"].bar", "42\n")]
    [InlineData("{\"foo\": {\"bar\": 20}} | .\"foo\".\"bar\"", "20\n")]
    [InlineData(".e5", "null\n")]
    [InlineData("{\"foo\":{\"bar\":4},\"baz\":\"bar\"} | .foo[.baz]", "4\n")]
    [InlineData("{\"foo\": 42} | .[\"foo\"]?", "42\n")]
    [InlineData("[\"a\"] | [.[]]", "[\n  \"a\"\n]\n")]
    [InlineData("[1,2,3] | [([5,5][]),.,.[]]", "[\n  5,\n  5,\n  [\n    1,\n    2,\n    3\n  ],\n  1,\n  2,\n  3\n]\n")]
    public async Task Jq_IndexStreamsEveryKey(string filter, string expected)
    {
        // Keys are outer: each key combines with every source value,
        // matching index-then-source evaluation with backtracking.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[0,1,2,3] | .[(1,2):3]", "[\n  1,\n  2\n]\n[\n  2\n]\n")]
    [InlineData("[0,1,2,3] | .[1:(2,3)]", "[\n  1\n]\n[\n  1,\n  2\n]\n")]
    [InlineData("[0,1,2,3] | .[(0,1):(2,3)]", "[\n  0,\n  1\n]\n[\n  0,\n  1,\n  2\n]\n[\n  1\n]\n[\n  1,\n  2\n]\n")]
    [InlineData("(([0,1,2,3]),([4,5,6,7]))[(0,1):2]", "[\n  0,\n  1\n]\n[\n  4,\n  5\n]\n[\n  1\n]\n[\n  5\n]\n")]
    [InlineData("[0,1,2] | .[empty:2]", "")]
    [InlineData("[0,1,2] | .[1:empty]", "")]
    [InlineData("[0,1,2,3,4,5,6] | [.[3:2], .[-5:4], .[:-2], .[-2:], .[3:3][1:], .[10:]]", "[\n  [],\n  [\n    2,\n    3\n  ],\n  [\n    0,\n    1,\n    2,\n    3,\n    4\n  ],\n  [\n    5,\n    6\n  ],\n  [],\n  []\n]\n")]
    [InlineData("\"abcdefghi\" | [.[3:2], .[-5:4], .[:-2], .[-2:], .[3:3][1:], .[10:]]", "[\n  \"\",\n  \"\",\n  \"abcdefg\",\n  \"hi\",\n  \"\",\n  \"\"\n]\n")]
    [InlineData("[range(10)] | .[1.2:3.5]", "[\n  1,\n  2,\n  3\n]\n")]
    [InlineData("[range(10)] | .[1.7:3.5]", "[\n  1,\n  2,\n  3\n]\n")]
    [InlineData("[range(10)] | .[1.7:4294967295]", "[\n  1,\n  2,\n  3,\n  4,\n  5,\n  6,\n  7,\n  8,\n  9\n]\n")]
    [InlineData("[range(10)] | .[1.7:-4294967296]", "[]\n")]
    [InlineData("[[range(10)] | .[1.1,1.5,1.7]]", "[\n  1,\n  1,\n  1\n]\n")]
[InlineData("[range(3)] | .[nan:1]", "[\n  0\n]\n")]
[InlineData("[range(3)] | .[1:nan]", "[\n  1,\n  2\n]\n")]
    [InlineData("[1,null,true,false,\"abcdef\",{},{\"a\":1,\"b\":2},[],[1,2,3,4,5],[1,2]] | [.[]|.[1:3]?]", "[\n  null,\n  \"bc\",\n  [],\n  [\n    2,\n    3\n  ],\n  [\n    2\n  ]\n]\n")]
    [InlineData("[-1, 1, 2, 3, 1000000000000000000] | map([1,2][0:.])", "[\n  [\n    1\n  ],\n  [\n    1\n  ],\n  [\n    1,\n    2\n  ],\n  [\n    1,\n    2\n  ],\n  [\n    1,\n    2\n  ]\n]\n")]
    public async Task Jq_SliceStreamsEveryBound(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_ObjectConstructionFormsProducts()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "{a:(1,2), b:(3,4)}")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": 3\n}\n{\n  \"a\": 1,\n  \"b\": 4\n}\n{\n  \"a\": 2,\n  \"b\": 3\n}\n{\n  \"a\": 2,\n  \"b\": 4\n}\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("{a:1, b:empty}")]
    [InlineData("{a:empty}")]
    public async Task Jq_ObjectConstructionDropsEmptyBranches(string filter)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_OptionalAccessSuppressesPerValueErrors()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "([1], 2) | .[]?")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_RequiredAccessReportsErrorsAfterAPrefix()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "([1], 2) | .[]")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Contains("cannot iterate", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"foobar\" | contains(\"foo\", \"baz\")", "true\nfalse\n")]
    [InlineData("\"foobar\" | startswith((\"foo\", \"bar\"))", "true\nfalse\n")]
    [InlineData("[1,2,3] | indices((1, 3))", "[\n  0\n]\n[\n  2\n]\n")]
    [InlineData("\"a,b\" | split((\",\", \";\"))", "[\n  \"a\",\n  \"b\"\n]\n[\n  \"a,b\"\n]\n")]
    [InlineData("\"aA\" | test((\"a\", \"b\"); (\"\", \"i\"))", "true\nfalse\ntrue\nfalse\n")]
    [InlineData("range((0, 2); 3)", "0\n1\n2\n2\n")]
    public async Task Jq_FunctionArgumentsDistribute(string filter, string expected)
    {
        // The last argument is outer (slow), the first inner (fast),
        // matching reversed call prelude order with backtracking.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"x\" | contains(empty)")]
    [InlineData("\"x\" | split(empty)")]
    [InlineData("range(empty; 3)")]
    [InlineData("range(0; empty)")]
    [InlineData("limit(empty; (1,2))")]
    [InlineData("nth(empty; (1,2))")]
    [InlineData("skip(empty; (1,2))")]
    [InlineData("while(empty; .)")]
    [InlineData("until(empty; .)")]
    [InlineData("getpath(empty)")]
    [InlineData("setpath(empty; 1)")]
    [InlineData("delpaths(empty)")]
    [InlineData("[1,2] | index(empty)")]
    public async Task Jq_EmptyArgumentsYieldNoOutputs(string filter)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_GsubFlagStreamIsOuter()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-r", "\"aA\" | gsub(\"a\"; \"X\"; (\"\", \"i\"))")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("XA\nXX\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("(1,2) + (10,20)", "11\n12\n21\n22\n")]
    [InlineData("(1,2) * (10,20)", "10\n20\n20\n40\n")]
    [InlineData("(\"a\",\"b\") + (\"c\",\"d\")", "\"ac\"\n\"bc\"\n\"ad\"\n\"bd\"\n")]
    [InlineData("(1,3) < (2,4)", "true\nfalse\ntrue\ntrue\n")]
    [InlineData("{\"k\": {\"a\": 1, \"b\": 2}} * {\"k\": {\"a\": 0,\"c\": 3}}", "{\n  \"k\": {\n    \"a\": 0,\n    \"b\": 2,\n    \"c\": 3\n  }\n}\n")]
    [InlineData("{\"a\":1} + {\"b\":2} + {\"c\":3} + {\"a\": 42}", "{\n  \"a\": 42,\n  \"b\": 2,\n  \"c\": 3\n}\n")]
    [InlineData("\"some string\" | \"asdf\" + \"jkl;\" + . + . + .", "\"asdfjkl;some stringsome stringsome string\"\n")]
    [InlineData("\"\\u0000\\u0020\\u0000\" | \"\\u0000\\u0020\\u0000\" + .", "\"\\u0000 \\u0000\\u0000 \\u0000\"\n")]
    [InlineData("[16 / 4 / 2, 16 / 4 * 2, 16 - 4 - 2, 16 - 4 + 2]", "[\n  2,\n  8,\n  10,\n  14\n]\n")]
    [InlineData("1e-19 + 1e-20 - 5e-21", "1.05E-19\n")]
    // Comma binds tighter than pipe (parser.y lists %left ',' above %right
    // '|'), so mixed collection items group as [A | ((B, C) | D)].
    [InlineData("[1 | ., 2 | .+10]", "[\n  11,\n  12\n]\n")]
    public async Task Jq_BinaryOperatorsDistribute(string filter, string expected)
    {
        // The left operand is inner (fast), matching reversed call
        // prelude order with backtracking.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"\\(1,2)\"", "\"1\"\n\"2\"\n")]
    [InlineData("\"\\(1,2)\\(3,4)\"", "\"13\"\n\"23\"\n\"14\"\n\"24\"\n")]
    [InlineData("\"a\\(empty)b\"", "")]
    // Interpolated values render through tostring: NaN as null, infinities clamped, duplicate keys last-wins.
    [InlineData("\"\\(nan)\"", "\"null\"\n")]
    [InlineData("\"\\(infinite)\"", "\"1.7976931348623157E+308\"\n")]
    [InlineData("\"\\({\"a\":1,\"a\":2})\"", "\"{\\\"a\\\":2}\"\n")]
    public async Task Jq_InterpolationsDistribute(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_ErrorsFollowTheirPrefixOutputs()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "(1, (1 | .foo)) + (10, 20)")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("11\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Contains("cannot index number", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public void Jq_ConsumerStopHaltsGeneratorsPromptly()
    {
        var variables = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>();
        using var cancellation = new CancellationTokenSource();
        var budget = new JqBudget(cancellation.Token);
        using var context = new JqContext(variables, JqProgramSource.Inline, budget);
        JqFilter filter = new JqParser("(range(0;1000000), 0) + (0, 0)", JqProgramSource.Inline, context.RootEnvironment, budget).Parse();
        using IEnumerator<System.Text.Json.Nodes.JsonNode?> results = filter.Evaluate(null, context, context.RootEnvironment).GetEnumerator();

        Assert.True(results.MoveNext());
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => results.MoveNext());
    }
}
