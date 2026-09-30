using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("{\"b\": 1, \"a\": 2}", "keys", "[\n  \"a\",\n  \"b\"\n]\n")]
    [InlineData("{\"b\": 1, \"a\": 2}", "keys_unsorted", "[\n  \"b\",\n  \"a\"\n]\n")]
    [InlineData("[42, 3, 35]", "keys", "[\n  0,\n  1,\n  2\n]\n")]
    [InlineData("[{\"foo\": 42}, {}]", "map(has(\"foo\"))", "[\n  true,\n  false\n]\n")]
    [InlineData("[[0, 1], [\"a\", \"b\", \"c\"]]", "map(has(2))", "[\n  false,\n  true\n]\n")]
    [InlineData("{\"a\": 1, \"b\": 2}", "to_entries", "[\n  {\n    \"key\": \"a\",\n    \"value\": 1\n  },\n  {\n    \"key\": \"b\",\n    \"value\": 2\n  }\n]\n")]
    [InlineData("[{\"key\": \"a\", \"value\": 1}, {\"Key\": \"b\", \"Value\": 2}, {\"name\": \"c\", \"value\": 3}, {\"Name\": \"d\", \"Value\": 4}]", "from_entries", "{\n  \"a\": 1,\n  \"b\": 2,\n  \"c\": 3,\n  \"d\": 4\n}\n")]
    [InlineData("{\"a\": 1, \"b\": 2}", "with_entries(.key |= \"KEY_\" + .)", "{\n  \"KEY_a\": 1,\n  \"KEY_b\": 2\n}\n")]
    [InlineData("[1, 2]", "to_entries", "[\n  {\n    \"key\": 0,\n    \"value\": 1\n  },\n  {\n    \"key\": 1,\n    \"value\": 2\n  }\n]\n")]
    [InlineData("[{\"key\": \"a\", \"value\": 1}, {\"key\": \"a\", \"value\": 2}]", "from_entries", "{\n  \"a\": 2\n}\n")]
    [InlineData("[1, \"a\", true, null, [], {}]", "map(type)", "[\n  \"number\",\n  \"string\",\n  \"boolean\",\n  \"null\",\n  \"array\",\n  \"object\"\n]\n")]
    [InlineData("[1, \"a\", true, null, [], {}]", "[(.[] | arrays), (.[] | objects), (.[] | numbers)]", "[\n  [],\n  {},\n  1\n]\n")]
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
    [InlineData("\"ab\"", "to_entries", "string (\"ab\") has no keys")]
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
[InlineData("\"ab\\u0000cd\"", "[contains(\"\"), contains(\"a\"), contains(\"ab\"), contains(\"c\"), contains(\"d\")]", "[\n  true,\n  true,\n  true,\n  true,\n  true\n]\n")]
[InlineData("\"ab\\u0000cd\"", "[contains(\"cd\"), contains(\"b\\u0000\"), contains(\"ab\\u0000\")]", "[\n  true,\n  true,\n  true\n]\n")]
[InlineData("\"ab\\u0000cd\"", "[contains(\"@\"), contains(\"\\u0000@\"), contains(\"\\u0000what\")]", "[\n  false,\n  false,\n  false\n]\n")]
[InlineData("null", "[({foo: 12, bar:13} | contains({foo: 12})), ({foo: 12} | contains({})), ({foo: 12, bar:13} | contains({baz:14}))]", "[\n  true,\n  true,\n  false\n]\n")]
[InlineData("null", "{foo: {baz: 12, blap: {bar: 13}}, bar: 14} | contains({bar: 14, foo: {blap: {}}})", "true\n")]
    [InlineData("\"ab\"", "inside(\"xaby\")", "true\n")]
    [InlineData("[]", "contains([])", "true\n")]
    [InlineData("{}", "contains({})", "true\n")]
    [InlineData("\"\"", "contains(\"a\")", "false\n")]
    [InlineData("[0, 1, 2, 3, 1, 4, 2, 5, 1, 2, 6, 7]", "indices([1, 2])", "[\n  1,\n  8\n]\n")]
    [InlineData("\"🇬🇧oo\"", "indices(\"o\")", "[\n  2,\n  3\n]\n")]
    [InlineData("[0, [1], [[2]], [[[3]]]]", "flatten", "[\n  0,\n  1,\n  2,\n  3\n]\n")]
    [InlineData("[0, [1], [[2]], [[[3]]]]", "flatten(0)", "[\n  0,\n  [\n    1\n  ],\n  [\n    [\n      2\n    ]\n  ],\n  [\n    [\n      [\n        3\n      ]\n    ]\n  ]\n]\n")]
    [InlineData("[0, [1], [[2]], [[[3]]]]", "flatten(2)", "[\n  0,\n  1,\n  2,\n  [\n    3\n  ]\n]\n")]
    [InlineData("[0, [1, [2]], [1, [[3], 2]]]", "flatten(2)", "[\n  0,\n  1,\n  2,\n  1,\n  [\n    3\n  ],\n  2\n]\n")]
    [InlineData("[[1]]", "flatten(0.5)", "[\n  1\n]\n")]
    [InlineData("{\"arr\": [1, 2, 3]}", ".sum = add(.arr[])", "{\n  \"arr\": [\n    1,\n    2,\n    3\n  ],\n  \"sum\": 6\n}\n")]
    [InlineData("[[1], [2, 3]]", "transpose", "[\n  [\n    1,\n    2\n  ],\n  [\n    null,\n    3\n  ]\n]\n")]
    [InlineData("[]", "transpose", "[]\n")]
    [InlineData("[[]]", "transpose", "[]\n")]
    [InlineData("null", "transpose", "[]\n")]
    [InlineData("{\"a\": [1, 2]}", "transpose", "[\n  [\n    1\n  ],\n  [\n    2\n  ]\n]\n")]
    [InlineData("[[],[1]]", "transpose", "[\n  [\n    null,\n    1\n  ]\n]\n")]
    [InlineData("[[1, 2], [3]]", "combinations", "[\n  1,\n  3\n]\n[\n  2,\n  3\n]\n")]
    [InlineData("[1, 2]", "combinations(2)", "[\n  1,\n  1\n]\n[\n  1,\n  2\n]\n[\n  2,\n  1\n]\n[\n  2,\n  2\n]\n")]
    [InlineData("[1, 2]", "combinations(0)", "[]\n")]
    [InlineData("[1, 2]", "combinations(-1)", "[]\n")]
    [InlineData("[1, 2, 3]", "bsearch(0, 1, 2, 3, 4)", "-1\n0\n1\n2\n-4\n")]
    [InlineData("[]", "bsearch(1)", "-1\n")]
    [InlineData("[[0, [1]]]", "flatten((1, 0))", "[\n  0,\n  [\n    1\n  ]\n]\n[\n  [\n    0,\n    [\n      1\n    ]\n  ]\n]\n")]
    [InlineData("null", "flatten", "[]\n")]
    [InlineData("null", "flatten(2)", "[]\n")]
    [InlineData("{\"a\": [1]}", "flatten", "[\n  1\n]\n")]
    [InlineData("{\"a\": [1, [2]]}", "flatten(1)", "[\n  1,\n  [\n    2\n  ]\n]\n")]
    [InlineData("[0, [1], [[2]], [[[3]]]]", "flatten(3,2,1)", "[\n  0,\n  1,\n  2,\n  3\n]\n[\n  0,\n  1,\n  2,\n  [\n    3\n  ]\n]\n[\n  0,\n  1,\n  [\n    2\n  ],\n  [\n    [\n      3\n    ]\n  ]\n]\n")]
    [InlineData("\"a,b|c,d,e||f,g,h,|,|,i,j\"", "[(index(\",\",\"|\"), rindex(\",\",\"|\")), indices(\",\",\"|\")]", "[\n  1,\n  3,\n  22,\n  19,\n  [\n    1,\n    5,\n    7,\n    12,\n    14,\n    16,\n    18,\n    20,\n    22\n  ],\n  [\n    3,\n    9,\n    10,\n    17,\n    19\n  ]\n]\n")]
    [InlineData("[\"a\",\"b\",\"c\",\"d\"]", "join(\",\",\"/\")", "\"a,b,c,d\"\n\"a/b/c/d\"\n")]
    [InlineData("[[],[\"\"],[\"\",\"\"],[\"\",\"\",\"\"]]", "[.[]|join(\"a\")]", "[\n  \"\",\n  \"\",\n  \"a\",\n  \"aa\"\n]\n")]
    [InlineData("[{\"x\": 0}, {\"x\": 1}, {\"x\": 2}]", "bsearch({\"x\": 1})", "1\n")]
    [InlineData("0", "range(3; 0; -1)", "3\n2\n1\n")]
    [InlineData("0", "range(0; 1; 0.5)", "0\n0.5\n")]
    [InlineData("0", "range(0; 2; 0.5)", "0\n0.5\n1\n1.5\n")]
    [InlineData("0", "range(2; 0; -0.5)", "2\n1.5\n1\n0.5\n")]
    [InlineData("0", "range(0.5; 2)", "0.5\n1.5\n")]
    [InlineData("0", "range(0; 1; 0)", "")]
    [InlineData("0", "range(0; 1; nan)", "")]
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
    [InlineData("\"aa\" | try [\"OK\", bsearch(0)] catch [\"KO\",.]", "[\n  \"KO\",\n  \"string (\\\"aa\\\") cannot be searched from\"\n]\n")]
[InlineData("try ([range(3)] | .[nan] = 9) catch .", "\"Cannot set array element at NaN index\"\n")]
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

    [Theory]
    [InlineData("[42, [2, 5, 3, 11], 10, {\"a\": 42, \"b\": 2}, {\"a\": 42}, true, 2, [2, 6], \"hello\", null, [2, 5, 6], {\"a\": [], \"b\": 1}, \"abc\", \"ab\", [3, 10], {}, false, \"abcd\", null]", "sort", "[\n  null,\n  null,\n  false,\n  true,\n  2,\n  10,\n  42,\n  \"ab\",\n  \"abc\",\n  \"abcd\",\n  \"hello\",\n  [\n    2,\n    5,\n    3,\n    11\n  ],\n  [\n    2,\n    5,\n    6\n  ],\n  [\n    2,\n    6\n  ],\n  [\n    3,\n    10\n  ],\n  {},\n  {\n    \"a\": 42\n  },\n  {\n    \"a\": 42,\n    \"b\": 2\n  },\n  {\n    \"a\": [],\n    \"b\": 1\n  }\n]\n")]
    [InlineData("[1, 2, 5, 3, 5, 3, 1, 3]", "unique", "[\n  1,\n  2,\n  3,\n  5\n]\n")]
    [InlineData("[]", "unique", "[]\n")]
    [InlineData("0", "[nan, nan] | unique", "[\n  null,\n  null\n]\n")]
    [InlineData("0", "[nan, 1] | group_by(.)", "[\n  [\n    null\n  ],\n  [\n    1\n  ]\n]\n")]
    [InlineData("[[4, 2, \"a\"], [3, 1, \"a\"], [2, 4, \"a\"], [1, 3, \"a\"]]", "[min, max, min_by(.[1]), max_by(.[1]), min_by(.[2]), max_by(.[2])]", "[\n  [\n    1,\n    3,\n    \"a\"\n  ],\n  [\n    4,\n    2,\n    \"a\"\n  ],\n  [\n    3,\n    1,\n    \"a\"\n  ],\n  [\n    2,\n    4,\n    \"a\"\n  ],\n  [\n    4,\n    2,\n    \"a\"\n  ],\n  [\n    1,\n    3,\n    \"a\"\n  ]\n]\n")]
    [InlineData("[]", "[min, max, min_by(.), max_by(.)]", "[\n  null,\n  null,\n  null,\n  null\n]\n")]
    [InlineData("[{\"a\": 1, \"b\": 4, \"c\": 14}, {\"a\": 4, \"b\": 1, \"c\": 3}, {\"a\": 1, \"b\": 4, \"c\": 3}, {\"a\": 0, \"b\": 2, \"c\": 43}]", "(sort_by(.b) | sort_by(.a)), sort_by(.a, .b)", "[\n  {\n    \"a\": 0,\n    \"b\": 2,\n    \"c\": 43\n  },\n  {\n    \"a\": 1,\n    \"b\": 4,\n    \"c\": 14\n  },\n  {\n    \"a\": 1,\n    \"b\": 4,\n    \"c\": 3\n  },\n  {\n    \"a\": 4,\n    \"b\": 1,\n    \"c\": 3\n  }\n]\n[\n  {\n    \"a\": 0,\n    \"b\": 2,\n    \"c\": 43\n  },\n  {\n    \"a\": 1,\n    \"b\": 4,\n    \"c\": 14\n  },\n  {\n    \"a\": 1,\n    \"b\": 4,\n    \"c\": 3\n  },\n  {\n    \"a\": 4,\n    \"b\": 1,\n    \"c\": 3\n  }\n]\n")]
    [InlineData("[{\"a\": 1, \"b\": 4, \"c\": 14}, {\"a\": 4, \"b\": 1, \"c\": 3}, {\"a\": 1, \"b\": 4, \"c\": 3}, {\"a\": 0, \"b\": 2, \"c\": 43}]", "group_by(.b)", "[\n  [\n    {\n      \"a\": 4,\n      \"b\": 1,\n      \"c\": 3\n    }\n  ],\n  [\n    {\n      \"a\": 0,\n      \"b\": 2,\n      \"c\": 43\n    }\n  ],\n  [\n    {\n      \"a\": 1,\n      \"b\": 4,\n      \"c\": 14\n    },\n    {\n      \"a\": 1,\n      \"b\": 4,\n      \"c\": 3\n    }\n  ]\n]\n")]
    [InlineData("[{\"foo\": 1, \"bar\": 10}, {\"foo\": 3, \"bar\": 100}, {\"foo\": 1, \"bar\": 1}]", "group_by(.foo)", "[\n  [\n    {\n      \"foo\": 1,\n      \"bar\": 10\n    },\n    {\n      \"foo\": 1,\n      \"bar\": 1\n    }\n  ],\n  [\n    {\n      \"foo\": 3,\n      \"bar\": 100\n    }\n  ]\n]\n")]
    [InlineData("[{\"foo\": 1, \"bar\": 14}, {\"foo\": 2, \"bar\": 3}]", "max_by(.foo)", "{\n  \"foo\": 2,\n  \"bar\": 3\n}\n")]
    [InlineData("[{\"foo\": 1, \"bar\": 2}, {\"foo\": 1, \"bar\": 3}, {\"foo\": 4, \"bar\": 5}]", "unique_by(.foo)", "[\n  {\n    \"foo\": 1,\n    \"bar\": 2\n  },\n  {\n    \"foo\": 4,\n    \"bar\": 5\n  }\n]\n")]
    [InlineData("[\"chunky\", \"bacon\", \"kitten\", \"cicada\", \"asparagus\"]", "unique_by(length)", "[\n  \"bacon\",\n  \"chunky\",\n  \"asparagus\"\n]\n")]
    public async Task Jq_SortGroupUnique(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_SortRejectsNonArrays()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("5");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "try sort catch .")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"cannot be sorted, as it is not an array\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"badness\"", "any(true, error; .)", "true\n")]
    [InlineData("\"badness\"", "all(false, error; .)", "false\n")]
    [InlineData("[]", "any(not)", "false\n")]
    [InlineData("[]", "all(not)", "true\n")]
    [InlineData("[false]", "any(not)", "true\n")]
    [InlineData("[false]", "all(not)", "true\n")]
    [InlineData("[]", "[any, all]", "[\n  false,\n  true\n]\n")]
    [InlineData("null", "range(5; 10) | IN(range(10))", "true\ntrue\ntrue\ntrue\ntrue\n")]
    [InlineData("null", "range(5; 13) | IN(range(0; 10; 3))", "false\ntrue\nfalse\nfalse\ntrue\nfalse\nfalse\nfalse\n")]
    [InlineData("null", "IN(range(10; 20); range(10))", "false\n")]
    [InlineData("null", "IN(range(5; 20); range(10))", "true\n")]
    public async Task Jq_AnyAllShortCircuit(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[0, 1, 2]", "[skip(0.5; .[])]", "[\n  0,\n  1,\n  2\n]\n")]
    [InlineData("[0, 1, 2, 3, 4, 5, 6, 7, 8, 9]", "[skip(3; .[])]", "[\n  3,\n  4,\n  5,\n  6,\n  7,\n  8,\n  9\n]\n")]
    [InlineData("[]", "[skip(3; .[])]", "[]\n")]
    [InlineData("\"a,b, cd\"", "rindex(\",\")", "3\n")]
    [InlineData("\"abc\"", "rindex(\"z\")", "null\n")]
    public async Task Jq_SkipRindex(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
    [Fact]
    public async Task Jq_TransposeRejectsNonArrays()
    {
        // Rows must be arrays or null like the reference map over rows;
        // anything else fails catchably instead of padding.
        var scalar = new MockFileSystem();
        scalar.SetStandardInput("1");
        var scalarTool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "transpose")));
        Assert.Equal(5, await scalarTool.ExecuteAsync(scalar, CancellationToken.None));
        Assert.Contains("cannot iterate over number", scalar.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(scalar.GetOutput(JqFileDescriptor.StdOut));

        var row = new MockFileSystem();
        row.SetStandardInput("[[1], 5]");
        var rowTool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "transpose")));
        Assert.Equal(5, await rowTool.ExecuteAsync(row, CancellationToken.None));
        Assert.Contains("cannot iterate over number", row.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(row.GetOutput(JqFileDescriptor.StdOut));
    }
    [Fact]
    public async Task Jq_RangeVectorsMatchReferenceOrder()
    {
        // Upstream range vectors prove first-argument-outer combinations;
        // the generic builtin prelude is last-outer, so range parses
        // through its own filter.
        var cases = new (string Filter, string Expected)[]
        {
            ("[range(0,1;3,4)]", "[0,1,2,0,1,2,3,1,2,1,2,3]\n"),
            ("[range(3,5)]", "[0,1,2,0,1,2,3,4]\n"),
            ("[range(0,1;4,5;1,2)]", "[0,1,2,3,0,2,0,1,2,3,4,0,2,4,1,2,3,1,3,1,2,3,4,1,3]\n"),
            ("[range(0,1,2;4,3,2;2,3)]", "[0,2,0,3,0,2,0,0,0,1,3,1,1,1,1,1,2,2,2,2]\n"),
        };
        foreach (var (filter, expected) in cases)
        {
            var host = new MockFileSystem();
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "-n", filter)));
            Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        }
    }
}
