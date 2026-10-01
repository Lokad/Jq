using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("[0, 1, 2, 3, 4, 5, 6, 7, 8, 9]", "[limit(3; .[])]", "[\n  0,\n  1,\n  2\n]\n")]
    [InlineData("0", "[limit(0; error)]", "[]\n")]
    [InlineData("0", "[limit(1; 1, error)]", "[\n  1\n]\n")]
    [InlineData("0", "limit(2.5; (1, 2, 3, 4))", "1\n2\n3\n")]
    [InlineData("[5, 6]", "first(.[])", "5\n")]
    [InlineData("[1, 2]", "first", "1\n")]
    [InlineData("[1, 2]", "last", "2\n")]
    [InlineData("[10, 20, 30]", "nth(1)", "20\n")]
    [InlineData("0", "nth(1; (10, 20, 30))", "20\n")]
    [InlineData("0", "nth(0; empty)", "")]
    [InlineData("0", "limit(empty; 1)", "")]
    [InlineData("0", "try last((1, error(\"x\"))) catch .", "\"x\"\n")]
    [InlineData("[1, 2, 3]", "isempty(.[])", "false\n")]
    [InlineData("0", "isempty((1, error(\"x\")))", "false\n")]
    [InlineData("0", "first((1, error(\"x\")))", "1\n")]
    [InlineData("null", "nth(1; 0,1,error(\"foo\"))", "1\n")]
    [InlineData("10", "[first(range(.)), last(range(.))]", "[\n  0,\n  9\n]\n")]
    [InlineData("0", "[first(range(.)), last(range(.))]", "[]\n")]
    [InlineData("10", "[nth(0,5,9,10,15; range(.)), try nth(-1; range(.)) catch .]", "[\n  0,\n  5,\n  9,\n  \"nth doesn't support negative indices\"\n]\n")]
    [InlineData("10", "[first(range(.)), last(range(.)), nth(5; range(.))]", "[\n  0,\n  9,\n  5\n]\n")]
    [InlineData("10", "[range(.)]|[first, last, nth(5)]", "[\n  0,\n  9,\n  5\n]\n")]
    [InlineData("null", "try limit(-1; error) catch .", "\"limit doesn't support negative count\"\n")]
    [InlineData("null", "first(1,error(\"foo\"))", "1\n")]
    [InlineData("null", "isempty(1,error(\"foo\"))", "false\n")]
    [InlineData("null", "first(.?,.?)", "null\n")]
    [InlineData("null", "isempty(range(3))", "false\n")]
    [InlineData("null", "[first(empty), last(empty), nth(5; empty)]", "[]\n")]
    [InlineData("null", "nth(1000000000000000000; 1,2,3)", "")]
    [InlineData("", "first", "")]
    [InlineData("", "last", "")]
    [InlineData("[]", "isempty(.[])", "true\n")]
    [InlineData("null", "limit(1; empty)", "")]
    [InlineData("[]", "first(.[])", "")]
    [InlineData("[]", "last(.[])", "")]
    [InlineData("[]", "nth(0; .[])", "")]
    [InlineData("null", "nth(0; empty)", "")]
    [InlineData("[1,2,3]", "limit(0; .[])", "")]
    public async Task Jq_LimitFirstNthIsempty(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("limit((1, 2); (10, 20, 30))", "10\n10\n20\n")]
    [InlineData("nth((0, 1); (10, 20, 30))", "10\n20\n")]
    [InlineData("[1, 2, 3] | [skip(0,2,3,4; .[])]", "[\n  1,\n  2,\n  3,\n  3\n]\n")]
    [InlineData("[limit(5,7; range(9))]", "[\n  0,\n  1,\n  2,\n  3,\n  4,\n  0,\n  1,\n  2,\n  3,\n  4,\n  5,\n  6\n]\n")]
    [InlineData("[nth(5,7; range(9;0;-1))]", "[\n  4,\n  2\n]\n")]
    public async Task Jq_CountArgumentsDistribute(string filter, string expected)
    {
        // Count positions are value parameters, so each count value
        // runs the body independently like upstream $n bindings.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
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
    [InlineData("try skip(-1; error) catch .", "\"skip doesn't support negative count\"\n")]
    [InlineData("try isempty(error(\"x\")) catch .", "\"x\"\n")]
    [InlineData("isempty(empty)", "true\n")]
    [InlineData("1 | until(true; error(\"x\"))", "1\n")]
    [InlineData("1 | while(false; error(\"x\"))", "")]
    // Empty loop updates against the recursive desugars (builtin.jq while/until):
    // while yields the state before a dying update, until only yields the state
    // when its condition holds, and falsy branches stay silent either way.
    [InlineData("5 | while(true; empty)", "5\n")]
    [InlineData("5 | until(false; empty)", "")]
    [InlineData("5 | until(true; empty)", "5\n")]
    [InlineData("5 | while(false; empty)", "")]
    [InlineData("[range(0;10)]", "[\n  0,\n  1,\n  2,\n  3,\n  4,\n  5,\n  6,\n  7,\n  8,\n  9\n]\n")]
    [InlineData("[range(0;10;-1)]", "[]\n")]
    [InlineData("[range(0;-5;-1)]", "[\n  0,\n  -1,\n  -2,\n  -3,\n  -4\n]\n")]
    [InlineData("[range(4)]", "[\n  0,\n  1,\n  2,\n  3\n]\n")]
    [InlineData("[range(2; 4)]", "[\n  2,\n  3\n]\n")]
    [InlineData("range(nan)", "")]
    // NaN range bounds terminate empty here. The reference range/2 loop keeps
    // iterating while !(current >= end), which never trips on NaN, so it hangs
    // where this yields nothing; range/3 with a NaN step is empty on both sides
    // through its sign desugar.
    [InlineData("[range(0; nan)]", "[]\n")]
    [InlineData("[range(nan; 5)]", "[]\n")]
    [InlineData("[range(5; nan)]", "[]\n")]
    [InlineData("[range(nan; nan)]", "[]\n")]
    [InlineData("[range(nan; nan; nan)]", "[]\n")]
    public async Task Jq_IterationEdgeCases(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("1", "[while(.<100; .*2)]", "[\n  1,\n  2,\n  4,\n  8,\n  16,\n  32,\n  64\n]\n")]
    [InlineData("4", "[.,1]|until(.[0] < 1; [.[0] - 1, .[1] * .[0]])|.[1]", "24\n")]
    [InlineData("0", "until(. > 3; . + 1)", "4\n")]
    [InlineData("1", "[repeat(.*2, error)?]", "[\n  2\n]\n")]
    [InlineData("0", "limit(3; repeat(. + 1))", "1\n1\n1\n")]
    [InlineData("0", "limit(3; repeat(1))", "1\n1\n1\n")]
    [InlineData("[[1]]", "[recurse]", "[\n  [\n    [\n      1\n    ]\n  ],\n  [\n    1\n  ],\n  1\n]\n")]
    [InlineData("1", "[recurse(if . < 3 then . + 1 else empty end)]", "[\n  1,\n  2,\n  3\n]\n")]
    [InlineData("1", "[recurse(. + 1; . < 4)]", "[\n  1,\n  2,\n  3\n]\n")]
    [InlineData("[1, [2]]", "walk(if type == \"number\" then . + 1 else . end)", "[\n  2,\n  [\n    3\n  ]\n]\n")]
    [InlineData("{\"x\": 0}", "walk(.)", "{\n  \"x\": 0\n}\n")]
    [InlineData("{\"x\": 0}", "walk(1)", "1\n")]
    [InlineData("{\"x\": 0}", "[walk(.,1)]", "[\n  {\n    \"x\": 0\n  },\n  1\n]\n")]
    [InlineData("{\"a\": 1, \"b\": []}", "walk(select(IN({}, []) | not))", "{\n  \"a\": 1\n}\n")]
[InlineData("[{\"_a\": {\"__b\": 2}}]", "walk( if type == \"object\" then with_entries( .key |= sub( \"^_+\"; \"\") ) else . end )", "[\n  {\n    \"a\": {\n      \"b\": 2\n    }\n  }\n]\n")]
    [InlineData("{\"a\": [1]}", "[paths]", "[\n  [\n    \"a\"\n  ],\n  [\n    \"a\",\n    0\n  ]\n]\n")]
    [InlineData("[]", "[paths]", "[]\n")]
    [InlineData("{}", "[paths]", "[]\n")]
    [InlineData("{\"a\": [1]}", "[paths(type == \"number\")]", "[\n  [\n    \"a\",\n    0\n  ]\n]\n")]
    [InlineData("{\"foo\":[{\"foo\": []}, {\"foo\":[{\"foo\":[]}]}]}", "recurse(.foo[])", "{\n  \"foo\": [\n    {\n      \"foo\": []\n    },\n    {\n      \"foo\": [\n        {\n          \"foo\": []\n        }\n      ]\n    }\n  ]\n}\n{\n  \"foo\": []\n}\n{\n  \"foo\": [\n    {\n      \"foo\": []\n    }\n  ]\n}\n{\n  \"foo\": []\n}\n")]
    [InlineData("2", "recurse(. * .; . < 20)", "2\n4\n16\n")]
    [InlineData("[[4, 1, 7], [8, 5, 2], [3, 6, 9]]", "walk(if type == \"array\" then sort else . end)", "[\n  [\n    1,\n    4,\n    7\n  ],\n  [\n    2,\n    5,\n    8\n  ],\n  [\n    3,\n    6,\n    9\n  ]\n]\n")]
    [InlineData("{\"a\":1}", "[paths(type == \"number\")]", "[\n  [\n    \"a\"\n  ]\n]\n")]
    [InlineData("{\"a\":1}", "[paths(numbers)]", "[\n  [\n    \"a\"\n  ]\n]\n")]
    [InlineData("1", "def while(cond; update): def _while: if cond then ., (update | _while) else empty end; _while; [while(.<100; .*2)]", "[\n  1,\n  2,\n  4,\n  8,\n  16,\n  32,\n  64\n]\n")]
    // Array levels collect every child walk output (builtin.jq walk/map), so the
    // rebuilt array grows, shrinks, or empties with the child streams; object
    // levels keep the first output per value and drop empties (map_values).
    [InlineData("[1]", "[walk(if type == \"number\" then (., . + 10) else . end)]", "[\n  [\n    1,\n    11\n  ]\n]\n")]
    [InlineData("[[1]]", "[walk(if type == \"array\" then (., .) else . end)]", "[\n  [\n    [\n      1\n    ],\n    [\n      1\n    ]\n  ],\n  [\n    [\n      1\n    ],\n    [\n      1\n    ]\n  ]\n]\n")]
    [InlineData("{\"a\":1}", "[walk(if type == \"number\" then (., . + 10) else . end)]", "[\n  {\n    \"a\": 1\n  }\n]\n")]
    [InlineData("[1,2]", "walk(if type == \"number\" then (., . + 1) else . end)", "[\n  1,\n  2,\n  2,\n  3\n]\n")]
    [InlineData("[1]", "[walk(if type == \"array\" then empty else . end)]", "[]\n")]
    [InlineData("{\"a\":1,\"b\":2}", "walk(if type == \"number\" then (., . + 10) else . end)", "{\n  \"a\": 1,\n  \"b\": 2\n}\n")]
    // Every condition output counts: repeated truthy probes duplicate the state in both loops.
    [InlineData("0", "[while((true, true); empty)]", "[\n  0,\n  0\n]\n")]
    [InlineData("0", "[until((true, true); empty)]", "[\n  0,\n  0\n]\n")]
    [InlineData("0", "[while((true, false); empty)]", "[\n  0\n]\n")]
    [InlineData("0", "[until((false, true); empty)]", "[\n  0\n]\n")]
    // Empty traversal bodies yield just the input (recurse) or nothing (walk).
    [InlineData("1", "[recurse(empty)]", "[\n  1\n]\n")]
    [InlineData("[1,[2]]", "[recurse(empty)]", "[\n  [\n    1,\n    [\n      2\n    ]\n  ]\n]\n")]
    [InlineData("{\"a\":1}", "[walk(empty)]", "[]\n")]
    [InlineData("1", "[walk(empty)]", "[]\n")]
    // Multi-output recurse stays depth-first pre-order like the reference
    // def r (self, then each function output in turn); break abandons
    // pending siblings while keeping already-yielded outputs.
    [InlineData("[[1,2]]", "[recurse(if type == \"array\" then .[] else empty end)]", "[\n  [\n    [\n      1,\n      2\n    ]\n  ],\n  [\n    1,\n    2\n  ],\n  1,\n  2\n]\n")]
    [InlineData("{\"a\":[1],\"b\":[2]}", "[recurse(.[]?)]", "[\n  {\n    \"a\": [\n      1\n    ],\n    \"b\": [\n      2\n    ]\n  },\n  [\n    1\n  ],\n  1,\n  [\n    2\n  ],\n  2\n]\n")]
    [InlineData("[[1],[2],[3]]", "[label $o | recurse(if . == [2] then break $o elif type == \"array\" then .[] else empty end)]", "[\n  [\n    [\n      1\n    ],\n    [\n      2\n    ],\n    [\n      3\n    ]\n  ],\n  [\n    1\n  ],\n  1,\n  [\n    2\n  ]\n]\n")]
    // Conditions filter multi-output expansions while lazy consumers stop the
    // explicit-stack traversal early; multi-output while updates fan out
    // leftmost-first like the recursive desugar.
    [InlineData("[[1,2],[3]]", "[recurse(if type == \"array\" then .[] else empty end; . != 2)]", "[\n  [\n    [\n      1,\n      2\n    ],\n    [\n      3\n    ]\n  ],\n  [\n    1,\n    2\n  ],\n  1,\n  [\n    3\n  ],\n  3\n]\n")]
    [InlineData("[[1,2]]", "[limit(3; recurse(if type == \"array\" then .[] else empty end))]", "[\n  [\n    [\n      1,\n      2\n    ]\n  ],\n  [\n    1,\n    2\n  ],\n  1\n]\n")]
    [InlineData("0", "[limit(4; while(true; (1,2)))]", "[\n  0,\n  1,\n  1,\n  1\n]\n")]
    [InlineData("0", "[limit(5; while(.<10; (.+1, .+2)))]", "[\n  0,\n  1,\n  2,\n  3,\n  4\n]\n")]
    public async Task Jq_WhileUntilRepeatRecurseWalkPaths(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_WalkPropagatesSignals()
    {
        // Errors in any branch abort the whole rebuild like the reference
        // map collection (no partial collect escapes); break abandons the
        // rebuild and the outer collect yields nothing.
        foreach (var (stdin, filter, exit, stdout, stderr) in new (string, string, int, string, string)[]
        {
            ("[1,2]\n", "[walk(if . == 2 then error(\"x\") else . end)]", 5, "", "jq: error (at <stdin>:1): x\n"),
            ("[1,2]", "try ([1,2] | walk(if . == 2 then error(\"x\") else . end)) catch .", 0, "\"x\"\n", ""),
            ("[1,2]", "[label $o | [1,2] | walk(if . == 2 then break $o else . end)]", 0, "[]\n", ""),
            ("{\"a\":1,\"b\":2}\n", "[walk(if . == 2 then error(\"x\") else . end)]", 5, "", "jq: error (at <stdin>:1): x\n"),
            ("[[1],[2]]\n", "walk(if . == 2 then error(\"x\") else . end)", 5, "", "jq: error (at <stdin>:1): x\n"),
        })
        {
            var host = new MockFileSystem();
            host.SetStandardInput(stdin);
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));
            Assert.Equal(exit, await tool.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal(stdout, host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal(stderr, host.GetOutput(JqFileDescriptor.StdErr));
        }
    }

    [Fact]
    public async Task Jq_RecursePropagatesErrors()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[[1,2]]\n");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "[recurse(if . == 2 then error(\"x\") elif type == \"array\" then .[] else empty end)]")));
        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: error (at <stdin>:1): x\n", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_FirstRepeatIsLazy()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "first(repeat(1))")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_ForeachLimitStopsProducers()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "limit(3; foreach (1, 2, 3, 4) as $x (0; . + $x))")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("1\n3\n6\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
    [Fact]
    public async Task Jq_LoopConditionsDistributeLikeIf()
    {
        // Loop bodies follow the same if-distribution as their reference
        // definitions: repeated truthy probes duplicate the state while
        // falsy until-probes still recurse, and empty conditions drop.
        var duplicated = new MockFileSystem();
        var duplicate = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "[limit(4; 0 | while((true, true); . + 1))]")));
        Assert.Equal(0, await duplicate.ExecuteAsync(duplicated, CancellationToken.None));
        Assert.Equal("[\n  0,\n  0,\n  1,\n  1\n]\n", duplicated.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(duplicated.GetOutput(JqFileDescriptor.StdErr));

        var recursed = new MockFileSystem();
        var recurse = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "[limit(3; 0 | until((true, false); . + 1))]")));
        Assert.Equal(0, await recurse.ExecuteAsync(recursed, CancellationToken.None));
        Assert.Equal("[\n  0,\n  1,\n  2\n]\n", recursed.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(recursed.GetOutput(JqFileDescriptor.StdErr));

        var dropped = new MockFileSystem();
        var drop = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "[0 | while(empty; . + 1), 0 | until(empty; . + 1)]")));
        Assert.Equal(0, await drop.ExecuteAsync(dropped, CancellationToken.None));
        Assert.Equal("[]\n", dropped.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(dropped.GetOutput(JqFileDescriptor.StdErr));
    }
}
