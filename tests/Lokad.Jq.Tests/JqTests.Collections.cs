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

    [Theory]
    [InlineData("[1, 2, 3]", "contains([2, 3])", "true\n")]
    [InlineData("{\"a\": {\"b\": [1, 2]}}", "contains({\"a\": {\"b\": [2]}})", "true\n")]
    [InlineData("\"abc\"", "contains(\"\")", "true\n")]
    [InlineData("\"ab\"", "inside(\"xaby\")", "true\n")]
    [InlineData("[0, 1, 2, 3, 1, 4, 2, 5, 1, 2, 6, 7]", "indices([1, 2])", "[1,8]\n")]
    [InlineData("\"🇬🇧oo\"", "indices(\"o\")", "[2,3]\n")]
    [InlineData("[0, [1], [[2]], [[[3]]]]", "flatten", "[0,1,2,3]\n")]
    [InlineData("[0, [1], [[2]], [[[3]]]]", "flatten(0)", "[0,[1],[[2]],[[[3]]]]\n")]
    [InlineData("[0, [1], [[2]], [[[3]]]]", "flatten(2)", "[0,1,2,[3]]\n")]
    [InlineData("[0, [1, [2]], [1, [[3], 2]]]", "flatten(2)", "[0,1,2,1,[3],2]\n")]
    [InlineData("{\"arr\": [1, 2, 3]}", ".sum = add(.arr[])", "{\"arr\":[1,2,3],\"sum\":6}\n")]
    [InlineData("[[1], [2, 3]]", "transpose", "[[1,2],[null,3]]\n")]
    [InlineData("[]", "transpose", "[]\n")]
    [InlineData("[[1, 2], [3]]", "combinations", "[1,3]\n[2,3]\n")]
    [InlineData("[1, 2]", "combinations(2)", "[1,1]\n[1,2]\n[2,1]\n[2,2]\n")]
    [InlineData("[1, 2, 3]", "bsearch(0, 1, 2, 3, 4)", "-1\n0\n1\n2\n-4\n")]
    [InlineData("[{\"x\": 0}, {\"x\": 1}, {\"x\": 2}]", "bsearch({\"x\": 1})", "1\n")]
    [InlineData("0", "range(3; 0; -1)", "3\n2\n1\n")]
    public async Task Jq_CollectionSearchFold(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("try ([1] | contains(2)) catch .", "\"array ([1]) and number (2) cannot have their containment checked\"\n")]
    [InlineData("try flatten(-1) catch .", "\"flatten depth must not be negative\"\n")]
    [InlineData("try (5 | flatten) catch .", "\"cannot iterate over number\"\n")]
    [InlineData("try (5 | add) catch .", "\"cannot iterate over number\"\n")]
    [InlineData("try (5 | bsearch(0)) catch .", "\"number (5) cannot be searched from\"\n")]
    public async Task Jq_CollectionFailures(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
