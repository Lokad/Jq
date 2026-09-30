using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("[1,2,3] | .[] | select(. > 1)", "2\n3\n")]
    [InlineData("[null,false,0,\"\",[],{},true] | .[] | select(.)", "0\n\"\"\n[]\n{}\ntrue\n")]
    [InlineData("{id:7,keep:true} | select(.keep)", "{\n  \"id\": 7,\n  \"keep\": true\n}\n")]
    [InlineData("false | select(true)", "false\n")]
    [InlineData("null | select(true)", "null\n")]
    [InlineData("\"kept\" | select(false,null,true,false,true)", "\"kept\"\n\"kept\"\n")]
    [InlineData("\"dropped\" | select(false,null)", "")]
    [InlineData("[1,2] | select(empty)", "")]
    [InlineData("[1,2] | [select(true,true)]", "[\n  [\n    1,\n    2\n  ],\n  [\n    1,\n    2\n  ]\n]\n")]
    public async Task Jq_SelectPreservesMatchingInput(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_SelectCountsMatchingNestedObjects()
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
            """[.items[] | select(.kind=="book" and .book.language=="French")] | length""")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("select")]
    [InlineData("select()")]
    [InlineData("select(true;false)")]
    public async Task Jq_SelectRequiresOnePredicate(string filter)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("jq: select expects one argument at line 1 column 1 (filter)\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_SelectReportsPredicateErrorsAfterEarlierResults()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "7 | select(true, (1 | .foo))")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("7\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: cannot index number with string \"foo\"\n", host.GetOutput(JqFileDescriptor.StdErr));
    }
}
