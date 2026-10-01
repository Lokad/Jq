using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("7", "if true then . else empty end", "7\n")]
    [InlineData("7", "if false then empty else . end", "7\n")]
    [InlineData("true", "if . then . else empty end", "true\n")]
    [InlineData("false", "if . then empty else . end", "false\n")]
    [InlineData("7", "if false then . elif true then . else empty end", "7\n")]
    [InlineData("true", "if false then empty elif . then . else empty end", "true\n")]
    [InlineData("true", "if . and true then . else empty end", "true\n")]
    [InlineData("false", "if . or true then . else empty end", "false\n")]
    [InlineData("7", "if true then if false then empty else . end else empty end", "7\n")]
    [InlineData("7", "if true then .\nelse empty end", "7\n")]
    [InlineData("7", "if true then .\telse empty end", "7\n")]
    [InlineData("7", "if true then (.) else empty end", "7\n")]
    [InlineData("1", "if false then 2 end", "1\n")]
    [InlineData("7", "[if false then 3 end]", "[\n  7\n]\n")]
    [InlineData("7", "[if false then 3 elif false then 4 end]", "[\n  7\n]\n")]
    [InlineData("7", "[if false then 3 elif false then 4 else . end]", "[\n  7\n]\n")]
    [InlineData("7", "[if false then 3 else . end]", "[\n  7\n]\n")]
    [InlineData("1", "if true then 2 end", "2\n")]
    [InlineData("1", "if false then 2 elif true then 3 end", "3\n")]
    public async Task Jq_IdentityAtConditionalBoundaries(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        var status = await tool.ExecuteAsync(host, CancellationToken.None);

        Assert.Equal(0, status);
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[.if,.then,.else,.elif,.end,.and,.or]", "[\n  1,\n  2,\n  3,\n  4,\n  5,\n  6,\n  7\n]\n")]
    [InlineData("if true then .else else .end end", "3\n")]
    [InlineData(".child.then.end", "8\n")]
    [InlineData(".child.then.end?", "8\n")]
    [InlineData(".[\"else\"]", "3\n")]
    [InlineData(".items[0].end", "9\n")]
    public async Task Jq_ConditionalWordsRemainFieldNames(string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("""
            {"if":1,"then":2,"else":3,"elif":4,"end":5,"and":6,"or":7,
             "child":{"then":{"end":8}},"items":[{"end":9}]}
            """);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        var status = await tool.ExecuteAsync(host, CancellationToken.None);

        Assert.Equal(0, status);
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("1.and true", "true\n")]
    [InlineData("1.or false", "true\n")]
    [InlineData("if 1.then 2 else 3 end", "2\n")]
    [InlineData("if -1.then 2 else 3 end", "2\n")]
    public async Task Jq_DecimalPointsDoNotTurnKeywordsIntoFieldNames(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        var status = await tool.ExecuteAsync(host, CancellationToken.None);

        Assert.Equal(0, status);
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_ConditionalIdentityFiltersNestedObjects()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("""
            {"items":[
              {"kind":"book","book":{"language":"French"}},
              {"kind":"book","book":{"language":"English"}},
              {"kind":"magazine"}
            ]}
            """);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq",
            """[.items[] | if .kind=="book" and .book.language=="French" then . else empty end] | length""")));

        var status = await tool.ExecuteAsync(host, CancellationToken.None);

        Assert.Equal(0, status);
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
    [Theory]
    [InlineData("if (true, false) then 1 else 2 end", "1\n2\n")]
    [InlineData("if (false, true) then 1 else 2 end", "2\n1\n")]
    [InlineData("if empty then 1 else 2 end", "")]
    [InlineData("if (false, true) then 1 elif (true, false) then 2 else 3 end", "2\n3\n1\n")]
    [InlineData("[-if true then 1 else 2 end]", "[\n  -1\n]\n")]
    [InlineData("{x: if true then 1 else 2 end}", "{\n  \"x\": 1\n}\n")]
    [InlineData("if true then [.] else . end []", "null\n")]
    [InlineData("[if 1,null,2 then 3 else 4 end]", "[\n  3,\n  4,\n  3\n]\n")]
    [InlineData("[if empty then 3 else 4 end]", "[]\n")]
    [InlineData("[if 1 then 3,4 else 5 end]", "[\n  3,\n  4\n]\n")]
    [InlineData("[if null then 3 else 5,6 end]", "[\n  5,\n  6\n]\n")]
    public async Task Jq_IfDistributesOverConditionOutputs(string filter, string expected)
    {
        // The manual routes every condition output independently:
        // truthy outputs run the branch body, falsy outputs fall to
        // the rest of the chain, and an empty condition yields nothing.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
