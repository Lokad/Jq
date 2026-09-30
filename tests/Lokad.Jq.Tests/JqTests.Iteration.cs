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
