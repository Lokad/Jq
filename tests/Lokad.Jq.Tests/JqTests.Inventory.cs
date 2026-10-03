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
    [InlineData("\"foo\" | try -.? catch .", "\"string (\\\"foo\\\") cannot be negated\"\n")]
    [InlineData("\"\u272B\u272B\u272B\u272B\u272B\u272B\u272B\u272B\" | try -. catch .", "\"string (\\\"\u272B\u272B\u272B\u272B\u272B\u272B\u272B\u272B\\\") cannot be negated\"\n")]
    [InlineData("\"xx\u272B\u272B\u272B\u272B\u272B\u272B\u272B\u272B\" | try -. catch .", "\"string (\\\"xx\u272B\u272B\u272B\u272B\u272B\u272B\u272B\u272B\\\") cannot be negated\"\n")]
    // Long values truncate like jv_dump_string_trunc; the failures stay catchable.
    [InlineData("\"very-long-long-long-long-string\" | try -. catch .", "\"string (\\\"very-long-long-long-long...\\\") cannot be negated\"\n")]
    [InlineData("null | \"x\" * range(0; 12; 2) + \"\u2606\u2606\u2606\u2606\u2606\u2606\u2606\u2606\" | try -. catch .", "\"string (\\\"☆☆☆☆☆☆☆☆\\\") cannot be negated\"\n\"string (\\\"xx☆☆☆☆☆☆☆☆\\\") cannot be negated\"\n\"string (\\\"xxxx☆☆☆☆☆☆...\\\") cannot be negated\"\n\"string (\\\"xxxxxx☆☆☆☆☆☆...\\\") cannot be negated\"\n\"string (\\\"xxxxxxxx☆☆☆☆☆...\\\") cannot be negated\"\n\"string (\\\"xxxxxxxxxx☆☆☆☆...\\\") cannot be negated\"\n")]
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
    [InlineData("\"abc\" | _strindices(\"\")", "[]\n")]
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
    // Unary minus rejects every non-number kind with the upstream payload shape.
    [InlineData("null | try -. catch .", "\"null (null) cannot be negated\"\n")]
    [InlineData("[1] | try -. catch .", "\"array ([1]) cannot be negated\"\n")]
    [InlineData("{\"a\":1} | try -. catch .", "\"object ({\\\"a\\\":1}) cannot be negated\"\n")]
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
    // NaN keys fold under their tostring spelling while missing keys collide last-wins with nulls.
    [InlineData("[{\"a\":nan},{\"a\":1}] | INDEX(.a)", "{\n  \"null\": {\n    \"a\": null\n  },\n  \"1\": {\n    \"a\": 1\n  }\n}\n")]
    [InlineData("[{\"a\":null},{}] | INDEX(.a)", "{\n  \"null\": {}\n}\n")]
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
    [InlineData("_repeat")]
    [InlineData("_until")]
    [InlineData("_while")]
    [InlineData("get_search_list")]
    [InlineData("get_prog_origin")]
    [InlineData("get_jq_origin")]
    public async Task Jq_InventoryInternalNamesAreRejected(string filter)
    {
        // The remaining parser-support placeholders (_repeat, _until, _while)
        // and host-identity queries stay outside the embeddable registry; direct
        // calls fail at compile time like unknown names, and builtins/0 never
        // advertises them. Operator internals, _assign, and _modify are real
        // callables covered below.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("unsupported function", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("_assign/2")]
    [InlineData("_modify/2")]
    [InlineData("_repeat/0")]
    [InlineData("_until/0")]
    [InlineData("_while/0")]
    [InlineData("_plus/2")]
    [InlineData("_minus/2")]
    [InlineData("_multiply/2")]
    [InlineData("_divide/2")]
    [InlineData("_mod/2")]
    [InlineData("_equal/2")]
    [InlineData("_notequal/2")]
    [InlineData("_less/2")]
    [InlineData("_lesseq/2")]
    [InlineData("_greater/2")]
    [InlineData("_greatereq/2")]
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
    [Theory]
    // Direct operator calls share the operator evaluation (the input
    // threads into both arguments; combinations stay second-outer).
    // Every row below was confirmed byte-for-byte against jq 1.8.2.
    [InlineData("0 | _plus(1;2)", "3\n")]
    [InlineData("0 | _minus(7;2)", "5\n")]
    [InlineData("0 | _multiply(3;4)", "12\n")]
    [InlineData("0 | _divide(7;2)", "3.5\n")]
    [InlineData("0 | _mod(7;3)", "1\n")]
    [InlineData("0 | _equal(1;1)", "true\n")]
    [InlineData("0 | _notequal(1;2)", "true\n")]
    [InlineData("0 | _less(1;2)", "true\n")]
    [InlineData("0 | _lesseq(2;2)", "true\n")]
    [InlineData("0 | _greater(3;2)", "true\n")]
    [InlineData("0 | _greatereq(2;2)", "true\n")]
    [InlineData("0 | _plus((1,2);(10,20))", "11\n12\n21\n22\n")]
    [InlineData("0 | _minus((1,2);(10,11))", "-9\n-8\n-10\n-9\n")]
    [InlineData("0 | _multiply((1,2);(10,11))", "10\n20\n11\n22\n")]
    [InlineData("0 | [(_equal((1,2);(1,2)))]", "[\n  true,\n  false,\n  false,\n  true\n]\n")]
    [InlineData("5 | _plus(.+1;.+2)", "13\n")]
    [InlineData("0 | _plus(empty;1)", "")]
    [InlineData("empty | _plus(1;2)", "")]
    [InlineData("try (0 | _plus(error(\"x\");1)) catch .", "\"x\"\n")]
    [InlineData("try (0 | _plus(\"a\";1)) catch .", "\"string (\\\"a\\\") and number (1) cannot be added\"\n")]
    [InlineData("try (0 | _divide(1;0)) catch .", "\"number (1) and number (0) cannot be divided because the divisor is zero\"\n")]
    [InlineData("5 | _negate | _negate", "5\n")]
    [InlineData("5 | _negate", "-5\n")]
    [InlineData("try (\"a\" | _negate) catch .", "\"string (\\\"a\\\") cannot be negated\"\n")]
    public async Task Jq_InventoryInternalBinops(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    // _assign folds setpath over enumerated paths per value and _modify
    // threads first-only updates with empty-update deletion, exactly like
    // the reference builtin.jq definitions (oracle-confirmed below).
    [InlineData("[0,1] | _assign(.[]; 9)", "[\n  9,\n  9\n]\n")]
    [InlineData("[0,1] | _assign(.[]; (9,8))", "[\n  9,\n  9\n]\n[\n  8,\n  8\n]\n")]
    [InlineData("{} | _assign(empty; 1)", "{}\n")]
    [InlineData("null | _assign(.a; 1)", "{\n  \"a\": 1\n}\n")]
    [InlineData("try ([0,1] | _assign(1; 2)) catch .", "\"Invalid path expression with result 1\"\n")]
    [InlineData("try ([0,1] | _assign(.[]; error(\"x\"))) catch .", "\"x\"\n")]
    [InlineData("{\"a\":1} | _modify(.a; .+1)", "{\n  \"a\": 2\n}\n")]
    [InlineData("{\"a\":1} | _modify((.a,.b); .+1)", "{\n  \"a\": 2,\n  \"b\": 1\n}\n")]
    [InlineData("{\"a\":1} | _modify(.a; (10,20))", "{\n  \"a\": 10\n}\n")]
    [InlineData("[3,1] | _modify(.[]; if . > 2 then empty else . end)", "[\n  1\n]\n")]
    [InlineData("{\"a\":1} | _modify(.b; .+1)", "{\n  \"a\": 1,\n  \"b\": 1\n}\n")]
    [InlineData("try ({\"a\":1} | _modify(.a; error(\"x\"))) catch .", "\"x\"\n")]
    public async Task Jq_InventoryAssignModify(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("_plus(1)", "_plus expects 2 arguments")]
    [InlineData("_assign([0])", "_assign expects 2 arguments")]
    [InlineData("_modify(.a)", "_modify expects 2 arguments")]
    public async Task Jq_InventoryInternalArity(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
}
