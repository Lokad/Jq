using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("[1, 2]", "last", "2\n")]
    [InlineData("[5, 6]", "last(.[])", "6\n")]
    [InlineData("null", "last(range(5))", "4\n")]
    public async Task Jq_InventoryLast(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_InventoryLastEmptyYieldsNothing()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "last(empty)")));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("1", "finites", "1\n")]
    [InlineData("1", "normals", "1\n")]
    [InlineData("0", "normals", "")]
    public async Task Jq_InventoryFinitesNormals(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("infinite | finites", "")]
    [InlineData("infinite | normals", "")]
    [InlineData("nan | finites", "")]
    [InlineData("1 | finites", "1\n")]
    [InlineData("\"foo\" | finites", "")]
    public async Task Jq_InventoryFinitesNullInput(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("5 | _negate", "-5\n")]
    [InlineData("-5 | -.", "5\n")]
    [InlineData("try (true | _negate) catch .", "\"boolean (true) cannot be negated\"\n")]
    public async Task Jq_InventoryNegate(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }


    [Theory]
    [InlineData("\"abcabc\" | _strindices(\"bc\")", "[\n  1,\n  4\n]\n")]
    [InlineData("\"abc\" | _strindices(\"\")", "[\n  0,\n  1,\n  2,\n  3\n]\n")]
    public async Task Jq_InventoryStrindices(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("123", "try _strindices(\"abc\") catch .", "\"number (123) cannot be searched, as it is not a string\"\n")]
    [InlineData("\"abc\"", "try _strindices(123) catch .", "\"number (123) is not a string\"\n")]
    public async Task Jq_InventoryStrindicesErrors(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"foo\" | format(\"text\")", "\"foo\"\n")]
    [InlineData("1 | format(\"json\")", "\"1\"\n")]
    [InlineData("try (\"foo\" | format(123)) catch .", "\"number (123) is not a valid format\"\n")]
    [InlineData("try (\"foo\" | format(\"bogus\")) catch .", "\"bogus is not a valid format\"\n")]
    public async Task Jq_InventoryFormat(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("builtins | any(. == \"last/1\")", "true\n")]
    [InlineData("builtins | any(. == \"_negate/0\")", "false\n")]
    [InlineData("builtins | any(.[0:1] == \"_\")", "false\n")]
    [InlineData("builtins | length > 10", "true\n")]
    [InlineData("builtins | length", "223\n")]
    [InlineData("\"-1\"|IN(builtins[] / \"/\"|.[1])", "false\n")]
    [InlineData("all(builtins[] / \"/\"; .[1]|tonumber >= 0)", "true\n")]
    [InlineData("builtins|any(.[:1] == \"_\")", "false\n")]
    public async Task Jq_InventoryBuiltins(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("try (-true) catch .", "\"boolean (true) cannot be negated\"\n")]
    [InlineData("-5", "-5\n")]
    public async Task Jq_InventoryUnaryNegate(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[3, 1, 2] | _sort_by_impl([3, 1, 2])", "[\n  1,\n  2,\n  3\n]\n")]
    [InlineData("[1, 2, 1] | _group_by_impl([[1], [2], [1]])", "[\n  [\n    1,\n    1\n  ],\n  [\n    2\n  ]\n]\n")]
    [InlineData("[1, 2, 1] | _unique_by_impl([[1], [2], [1]])", "[\n  1,\n  2\n]\n")]
    [InlineData("[3, 1, 2] | _min_by_impl([3, 1, 2])", "1\n")]
    [InlineData("[3, 1, 2] | _max_by_impl([3, 1, 2])", "3\n")]
    [InlineData("[0, [1, [[2]]]] | _flatten(1)", "[\n  0,\n  1,\n  [\n    [\n      2\n    ]\n  ]\n]\n")]
    public async Task Jq_InventoryUnderscoreImpls(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"banana\" | _match_impl(\"a\"; null; true)", "true\n")]
    [InlineData("\"banana\" | _match_impl(\"a\"; null; false) | length", "1\n")]
    [InlineData("\"banana\" | _match_impl(\"a\"; \"g\"; false) | length", "3\n")]
    [InlineData("\"banana\" | _match_impl(\"a\"; null; 1) | length", "1\n")]
    public async Task Jq_InventoryMatchImpl(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("try ([1] | _sort_by_impl([1, 2])) catch .", "\"array ([1]) and array ([1,2]) cannot be sorted, as they are not both arrays\"\n")]
    [InlineData("try ([1, 2] | _min_by_impl([1])) catch .", "\"array ([1,2]) and array ([1]) have wrong length\"\n")]
    [InlineData("try (1 | _match_impl(\"a\"; null; false)) catch .", "\"number (1) cannot be matched, as it is not a string\"\n")]
    public async Task Jq_InventoryImplErrors(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[1, 2, 1] | INDEX(.)", "{\n  \"1\": 1,\n  \"2\": 2\n}\n")]
    [InlineData("INDEX([1, 2, 1][]; .)", "{\n  \"1\": 1,\n  \"2\": 2\n}\n")]
    public async Task Jq_InventoryIndex(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[1, 2, 3] | JOIN({\"1\": \"one\", \"2\": \"two\"}; tostring)", "[\n  [\n    1,\n    \"one\"\n  ],\n  [\n    2,\n    \"two\"\n  ],\n  [\n    3,\n    null\n  ]\n]\n")]
    [InlineData("JOIN({\"a\": 1}; [\"a\", \"b\"][]; .)", "[\n  \"a\",\n  1\n]\n[\n  \"b\",\n  null\n]\n")]
    [InlineData("JOIN({\"1\": \"one\"}; [1][]; tostring; .[1])", "\"one\"\n")]
    public async Task Jq_InventoryJoin(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_InventoryUpstreamIndex()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "-n", "INDEX(range(5)|[., \"foo\\(.)\"]; .[0])")));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("{\"0\":[0,\"foo0\"],\"1\":[1,\"foo1\"],\"2\":[2,\"foo2\"],\"3\":[3,\"foo3\"],\"4\":[4,\"foo4\"]}\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_InventoryUpstreamJoin()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[[5,\"foo\"],[3,\"bar\"],[1,\"foobar\"]]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "JOIN({\"0\":[0,\"abc\"],\"1\":[1,\"bcd\"],\"2\":[2,\"def\"],\"3\":[3,\"efg\"],\"4\":[4,\"fgh\"]}; .[0]|tostring)")));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[[[5,\"foo\"],null],[[3,\"bar\"],[3,\"efg\"]],[[1,\"foobar\"],[1,\"bcd\"]]]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[.[] | arrays]", "[\n  []\n]\n")]
    [InlineData("[.[] | objects]", "[\n  {}\n]\n")]
    [InlineData("[.[] | iterables]", "[\n  [],\n  {}\n]\n")]
    [InlineData("[.[] | booleans]", "[\n  true\n]\n")]
    [InlineData("[.[] | numbers]", "[\n  1\n]\n")]
    [InlineData("[.[] | strings]", "[\n  \"a\"\n]\n")]
    [InlineData("[.[] | nulls]", "[\n  null\n]\n")]
    [InlineData("[.[] | values]", "[\n  1,\n  \"a\",\n  true,\n  [],\n  {}\n]\n")]
    [InlineData("[.[] | scalars]", "[\n  1,\n  \"a\",\n  true,\n  null\n]\n")]
    public async Task Jq_InventoryTypeSelectors(string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[1, \"a\", true, null, [], {}]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
    [Theory]
    [InlineData("_assign")]
    [InlineData("_modify")]
    [InlineData("get_search_list")]
    [InlineData("get_prog_origin")]
    [InlineData("get_jq_origin")]
    public async Task Jq_InventoryInternalNamesAreRejected(string filter)
    {
        // Parser-support helpers and host-identity queries stay outside the
        // embeddable registry; direct calls fail at compile time like unknown
        // names, and builtins/0 never advertises them.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("unsupported function", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("_assign/2")]
    [InlineData("_modify/2")]
    [InlineData("get_search_list/0")]
    [InlineData("get_prog_origin/0")]
    [InlineData("get_jq_origin/0")]
    public async Task Jq_InventoryBuiltinsOmitsInternalNames(string name)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "builtins | any(. == \"" + name + "\")")));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("false\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
