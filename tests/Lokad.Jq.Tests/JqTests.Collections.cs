using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("{\"b\": 1, \"a\": 2}", "keys", "[\"a\",\"b\"]\n")]
    [InlineData("{\"b\": 1, \"a\": 2}", "keys_unsorted", "[\"b\",\"a\"]\n")]
    [InlineData("[42, 3, 35]", "keys", "[0,1,2]\n")]
    [InlineData("[{\"foo\": 42}, {}]", "map(has(\"foo\"))", "[true,false]\n")]
    [InlineData("[[0, 1], [\"a\", \"b\", \"c\"]]", "map(has(2))", "[false,true]\n")]
    [InlineData("{\"a\": 1, \"b\": 2}", "to_entries", "[{\"key\":\"a\",\"value\":1},{\"key\":\"b\",\"value\":2}]\n")]
    [InlineData("[{\"key\": \"a\", \"value\": 1}, {\"Key\": \"b\", \"Value\": 2}, {\"name\": \"c\", \"value\": 3}, {\"Name\": \"d\", \"Value\": 4}]", "from_entries", "{\"a\":1,\"b\":2,\"c\":3,\"d\":4}\n")]
    [InlineData("{\"a\": 1, \"b\": 2}", "with_entries(.key |= \"KEY_\" + .)", "{\"KEY_a\":1,\"KEY_b\":2}\n")]
    [InlineData("[1, \"a\", true, null, [], {}]", "map(type)", "[\"number\",\"string\",\"boolean\",\"null\",\"array\",\"object\"]\n")]
    [InlineData("[1, \"a\", true, null, [], {}]", "[(.[] | arrays), (.[] | objects), (.[] | numbers)]", "[[],{},1]\n")]
    [InlineData("[0, 1, 2]", "has(-1 | sqrt)", "false\n")]
    public async Task Jq_StructuralBuiltins(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("5", "keys", "number (5) has no keys")]
    public async Task Jq_StructuralFailures(string input, string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
}
