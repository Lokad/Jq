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
}
