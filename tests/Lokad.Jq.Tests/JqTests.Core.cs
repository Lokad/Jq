using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("2 + 3 * 4", "14\n")]
    [InlineData("1+2", "3\n")]
    [InlineData("10 - 2 - 3", "5\n")]
    [InlineData("1 - -2", "3\n")]
    [InlineData(".5", "0.5\n")]
    [InlineData("1.", "1\n")]
    [InlineData("true | not", "false\n")]
    [InlineData("1 | not", "false\n")]
    [InlineData("null | not", "true\n")]
    [InlineData("1,2 | .+1", "2\n3\n")]
    [InlineData("3 | ., .+1", "3\n4\n")]
    [InlineData("1 + 2 as $x | -$x", "-3\n")]
    [InlineData("2-1", "1\n")]
    [InlineData("2-(-1)", "3\n")]
    [InlineData("4/-2", "-2\n")]
    [InlineData("4/ -2", "-2\n")]
    [InlineData("1 - -1", "2\n")]
    [InlineData("2--1", "3\n")]
    [InlineData("5.", "5\n")]
    [InlineData("1E5", "100000\n")]
    public async Task Jq_CorePrecedenceAndLiterals(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_AlternativePropagatesLeftErrors()
    {
        // Defined-or substitutes only for false, null, and empty: left errors
        // propagate, keeping outputs produced before the error.
        foreach (var (filter, stdout) in new (string, string)[]
        {
            ("error(\"x\") // 1", ""),
            ("(1, error(\"x\")) // 2", "1\n"),
        })
        {
            var host = new MockFileSystem();
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
            Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal(stdout, host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal("jq: error (at <unknown>): x\n", host.GetOutput(JqFileDescriptor.StdErr));
        }
    }

    [Fact]
    public async Task Jq_ChainedComparisonsDoNotParse()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "1 < 2 < 3")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("unexpected token", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }


    [Theory]
    [InlineData("[1,2,3,1] - [2]", "[\n  1,\n  3,\n  1\n]\n")]
    [InlineData("[1,2] - []", "[\n  1,\n  2\n]\n")]
    [InlineData("{\"a\":{\"x\":1,\"y\":2},\"b\":1} * {\"a\":{\"y\":3,\"z\":4}}", "{\n  \"a\": {\n    \"x\": 1,\n    \"y\": 3,\n    \"z\": 4\n  },\n  \"b\": 1\n}\n")]
    [InlineData("{\"a\":{\"x\":1}} + {\"a\":{\"y\":2}}", "{\n  \"a\": {\n    \"y\": 2\n  }\n}\n")]
    [InlineData("{\"k\": {\"a\": 0, \"c\": 3}, \"hello\": 1} as $in | {\"k\": {\"a\": 1, \"b\": 2}, \"hello\": {\"x\": 1}} * $in", "{\n  \"k\": {\n    \"a\": 0,\n    \"b\": 2,\n    \"c\": 3\n  },\n  \"hello\": 1\n}\n")]
    [InlineData("{\"k\": {\"a\": 0, \"c\": 3}, \"hello\": {\"x\": 1}} as $in | {\"k\": {\"a\": 1, \"b\": 2}, \"hello\": 1} * $in", "{\n  \"k\": {\n    \"a\": 0,\n    \"b\": 2,\n    \"c\": 3\n  },\n  \"hello\": {\n    \"x\": 1\n  }\n}\n")]
    [InlineData("\"ab\" * 2.5", "\"abab\"\n")]
    [InlineData("2 * \"ab\"", "\"abab\"\n")]
    [InlineData("2.5 * \"ab\"", "\"abab\"\n")]
    [InlineData("\"a,b\" / \",\"", "[\n  \"a\",\n  \"b\"\n]\n")]
    [InlineData("\"abc\" / \"\"", "[\n  \"a\",\n  \"b\",\n  \"c\"\n]\n")]
    [InlineData("\"\" | split(\",\")", "[]\n")]
    [InlineData("\"abc\" | split(\"\")", "[\n  \"a\",\n  \"b\",\n  \"c\"\n]\n")]
    [InlineData("{\"a\":42} | .+null", "{\n  \"a\": 42\n}\n")]
    [InlineData("null | null+.", "null\n")]
    [InlineData("[1,2,3] + [.]", "[\n  1,\n  2,\n  3,\n  null\n]\n")]
    [InlineData("11 | 42 - .", "31\n")]
    [InlineData("1 | [1,2,3,4,1] - [.,3]", "[\n  2,\n  4\n]\n")]
    [InlineData("[-1 as $x | 1,$x]", "[\n  1,\n  -1\n]\n")]
    [InlineData("4 | [10 * 20, 20 / .]", "[\n  200,\n  5\n]\n")]
    [InlineData("([1,2] + [4,5])", "[\n  1,\n  2,\n  4,\n  5\n]\n")]
    [InlineData("1 | (. + 2) * 5", "15\n")]
    [InlineData("{\"a\": 7} | .a + 1", "8\n")]
    [InlineData("{} | .a + 1", "1\n")]
    [InlineData("{a: 1} + {b: 2} + {c: 3} + {a: 42}", "{\n  \"a\": 42,\n  \"b\": 2,\n  \"c\": 3\n}\n")]
    [InlineData("{\"a\":3} | 4 - .a", "1\n")]
    [InlineData("[\"xml\", \"yaml\", \"json\"] | . - [\"xml\", \"yaml\"]", "[\n  \"json\"\n]\n")]
    [InlineData("{\"a\": 1} | .a + null", "1\n")]
    // Null absorbs through addition on both sides for every kind.
    [InlineData("\"a\" + null", "\"a\"\n")]
    [InlineData("null + \"a\"", "\"a\"\n")]
    [InlineData("[1] + null", "[\n  1\n]\n")]
    [InlineData("null + [1]", "[\n  1\n]\n")]
    [InlineData("{\"a\":1} + null", "{\n  \"a\": 1\n}\n")]
    [InlineData("null + {\"a\":1}", "{\n  \"a\": 1\n}\n")]
    [InlineData("5 + null", "5\n")]
    [InlineData("null + 5", "5\n")]
    [InlineData("null + null", "null\n")]
    // Null ranks first in the total order with kind-sensitive equality.
    [InlineData("[null < 1, 1 < null, null < null]", "[\n  true,\n  false,\n  false\n]\n")]
    [InlineData("[null == null, null == false, null == 0]", "[\n  true,\n  false,\n  false\n]\n")]
    [InlineData("[1 < \"a\", \"a\" < {}, {} < []]", "[\n  true,\n  true,\n  false\n]\n")]
    [InlineData("5 | 10 / . * 3", "6\n")]
    [InlineData("\"a, b,c,d, e\" | . / \", \"", "[\n  \"a\",\n  \"b,c,d\",\n  \"e\"\n]\n")]
    [InlineData("[true, false | not]", "[\n  false,\n  true\n]\n")]
    [InlineData("\"\" | split(\"\")", "[]\n")]
    // Only object-object collisions recurse; arrays and mixed collisions replace like jvp_object_merge_recursive.
    [InlineData("{\"a\":[1]} * {\"a\":[2]}", "{\n  \"a\": [\n    2\n  ]\n}\n")]
    [InlineData("{\"a\":{\"x\":1}} * {\"a\":5}", "{\n  \"a\": 5\n}\n")]
    [InlineData("{\"a\":5} * {\"a\":{\"x\":1}}", "{\n  \"a\": {\n    \"x\": 1\n  }\n}\n")]
    // Null merge values replace right-wins instead of recursing, since null is not an object.
    [InlineData("{\"a\":1} * {\"a\":null}", "{\n  \"a\": null\n}\n")]
    [InlineData("{\"a\":null} * {\"a\":1}", "{\n  \"a\": 1\n}\n")]
    [InlineData("{\"a\":{\"b\":1}} * {\"a\":null}", "{\n  \"a\": null\n}\n")]
    // Overwritten merge keys keep their first position while new keys append, like jv_object_set.
    [InlineData("{\"b\":1,\"a\":2} + {\"a\":3,\"c\":4}", "{\n  \"b\": 1,\n  \"a\": 3,\n  \"c\": 4\n}\n")]
    [InlineData("{\"b\":1,\"a\":2} * {\"a\":3,\"c\":4}", "{\n  \"b\": 1,\n  \"a\": 3,\n  \"c\": 4\n}\n")]
    public async Task Jq_OperatorTypeCombinations(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"6\" + 1", "cannot be added")]
    [InlineData("\"a\" - \"b\"", "cannot be subtracted")]
    [InlineData("\"a\" * {}", "cannot be multiplied")]
    [InlineData("\"a\" / 2", "cannot be divided")]
    [InlineData("\"a\" % \"b\"", "cannot be divided (remainder)")]
    [InlineData("[1, 2] * 2", "array ([1,2]) and number (2) cannot be multiplied")]
    [InlineData("2 * [1, 2]", "number (2) and array ([1,2]) cannot be multiplied")]
    [InlineData("1 + \"x\"", "number (1) and string (\"x\") cannot be added")]
    [InlineData("\"a\" * {}", "string (\"a\") and object ({}) cannot be multiplied")]
    // Arithmetic stays strict even for numeric strings; only count positions coerce.
    [InlineData("\"2\" + 1", "string (\"2\") and number (1) cannot be added")]
    [InlineData("\"6\" / 2", "string (\"6\") and number (2) cannot be divided")]
    [InlineData("123456789012345678901234567890 + \"x\"", "and string (\"x\") cannot be added")]
    // Unlike addition, subtraction and multiplication absorb no nulls (binop_minus/multiply).
    [InlineData("{\"a\":1} * null", "object ({\"a\":1}) and null (null) cannot be multiplied")]
    [InlineData("null * {\"a\":1}", "null (null) and object ({\"a\":1}) cannot be multiplied")]
    [InlineData("1 * null", "number (1) and null (null) cannot be multiplied")]
    [InlineData("null * 1", "null (null) and number (1) cannot be multiplied")]
    [InlineData("1 - null", "number (1) and null (null) cannot be subtracted")]
    [InlineData("null - 1", "null (null) and number (1) cannot be subtracted")]
    [InlineData("\"a\" * null", "string (\"a\") and null (null) cannot be multiplied")]
    [InlineData("[1] * null", "array ([1]) and null (null) cannot be multiplied")]
    [InlineData("{\"a\":1} - {\"b\":2}", "object ({\"a\":1}) and object ({\"b\":2}) cannot be subtracted")]
    // Division and remainder absorb no nulls either (binop_divide/modulo).
    [InlineData("1 / null", "number (1) and null (null) cannot be divided")]
    [InlineData("null / 1", "null (null) and number (1) cannot be divided")]
    [InlineData("\"a\" / null", "string (\"a\") and null (null) cannot be divided")]
    [InlineData("null / \"a\"", "null (null) and string (\"a\") cannot be divided")]
    [InlineData("1 % null", "number (1) and null (null) cannot be divided (remainder)")]
    [InlineData("null % 1", "null (null) and number (1) cannot be divided (remainder)")]
    [InlineData("1 / \"a\"", "number (1) and string (\"a\") cannot be divided")]
    // Remaining mismatched-kind additions fail like binop_plus.
    [InlineData("\"a\" + {}", "string (\"a\") and object ({}) cannot be added")]
    [InlineData("{} + \"a\"", "object ({}) and string (\"a\") cannot be added")]
    [InlineData("[1] + {}", "array ([1]) and object ({}) cannot be added")]
    [InlineData("{} + [1]", "object ({}) and array ([1]) cannot be added")]
    [InlineData("1 + []", "number (1) and array ([]) cannot be added")]
    public async Task Jq_MixedTypeArithmeticFails(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    // Long operands render in full while the reference truncates them with "..."; the failure stays catchable.
    [Theory]
    [InlineData("\"very-long-long-long-long-string\" | try (.-.) catch .", "\"string (\\\"very-long-long-long-long-string\\\") and string (\\\"very-long-long-long-long-string\\\") cannot be subtracted\"\n")]
    public async Task Jq_CaughtSubtractRendersFullOperands(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("empty // 42", "42\n")]
    [InlineData("(false, null, 1) // 42", "1\n")]
    [InlineData("[{\"foo\":[1,2], \"bar\": 42}, {\"foo\":[1], \"bar\": null}, {\"foo\":[null,false,3], \"bar\": 18}, {\"foo\":[], \"bar\":42}, {\"foo\": [null,false,null], \"bar\": 41}] | [.[] | [.foo[] // .bar]]", "[\n  [\n    1,\n    2\n  ],\n  [\n    1\n  ],\n  [\n    3\n  ],\n  [\n    42\n  ],\n  [\n    41\n  ]\n]\n")]
    [InlineData("{x: 1 + 2, y: false or true, z: null // 3}", "{\n  \"x\": 3,\n  \"y\": true,\n  \"z\": 3\n}\n")]
    [InlineData("{\"a\": null, \"b\": true, \"c\": false} | map_values(. // empty)", "{\n  \"b\": true\n}\n")]
    [InlineData("(false, null, 1) | . // 42", "42\n42\n1\n")]
    [InlineData("1 // 2 // 3", "1\n")]
    [InlineData("{\"foo\": 19} | .foo // 42", "19\n")]
    [InlineData("{} | .foo // 42", "42\n")]
    public async Task Jq_AlternativeFiltersGoods(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_AlternativeFallsBackAfterOnlyFalsyOutputs()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "(false, null) // (7, 8)")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("7\n8\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("1 | .foo?", "")]
    [InlineData("1 | .[0]?", "")]
    [InlineData("1 | .[]?", "")]
    [InlineData("null | .[]?", "")]
    [InlineData("[1,2] | .[error(\"x\")]?", "")]
    [InlineData("[1,2] | .[0,error(\"x\")]?", "1\n")]
    [InlineData("[1,2] | .[1,error(\"x\")]?", "2\n")]
    [InlineData("null | .[null]?", "")]
    [InlineData("5 | .[[1]]?", "")]
    [InlineData("1 | .[0:1]?", "")]
    [InlineData("(1 | .foo)?", "")]
    [InlineData("{\"a\":1} | .b?", "null\n")]
    [InlineData("null | .a?", "null\n")]
    [InlineData("(\"x\" | test(\"x\";\"z\"))?", "")]
    [InlineData("[1,[2],{\"foo\":3,\"bar\":4},{},{\"foo\":5}] | [.[]|.foo?]", "[\n  3,\n  null,\n  5\n]\n")]
    [InlineData("[1,[2],[],{\"foo\":3},{\"foo\":{\"bar\":4}},{}] | [.[]|.foo?.bar?]", "[\n  4,\n  null\n]\n")]
    [InlineData("[1,null,[],[1,[2,[[3]]]],[{}],[{\"a\":[1,[2]]}]] | [.[]|.[]?]", "[\n  1,\n  [\n    2,\n    [\n      [\n        3\n      ]\n    ]\n  ],\n  {},\n  {\n    \"a\": [\n      1,\n      [\n        2\n      ]\n    ]\n  }\n]\n")]
    [InlineData("[null,true,{\"a\":1}] | [.[]|(.a, .a)?]", "[\n  null,\n  null,\n  1,\n  1\n]\n")]
    [InlineData("[null,true,{\"a\":1}] | [[.[]|[.a,.a]]?]", "[]\n")]
    [InlineData("[{}, true, {\"a\":1}] | [.[] | .a?]", "[\n  null,\n  1\n]\n")]
    [InlineData("[\"1\", \"invalid\", \"3\", 4] | [.[] | tonumber?]", "[\n  1,\n  3,\n  4\n]\n")]
    [InlineData("0 | (.a[])?", "")]
    [InlineData("0 | (.a[0])?", "")]
    [InlineData("0 | (.a[1:2])?", "")]
    [InlineData("{\"a\":5} | .a[0]?", "")]
    [InlineData("{\"a\":5} | .a[1:2]?", "")]
    public async Task Jq_OptionalSuppressesCatchableErrors(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_IterateNullFailsStaged()
    {
        // Iterating null fails like other scalars (the reference EACH errors);
        // suppression and handlers still apply, and collectors like map
        // inherit the failure.
        foreach (string filter in new[] { ".[]", ".a[]", "map(.)", "[.[]]", "map(empty)" })
        {
            var host = new MockFileSystem();
            host.SetStandardInput("null");
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", filter)));
            Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
            Assert.Contains("cannot iterate over null", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        }

        var caught = new MockFileSystem();
        caught.SetStandardInput("null");
        var caughtTool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "try .[] catch .")));
        Assert.Equal(0, await caughtTool.ExecuteAsync(caught, CancellationToken.None));
        Assert.Equal("\"cannot iterate over null\"\n", caught.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_OptionalDoesNotSuppressQuota()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "{a:range(0;300000)}? | length")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("value budget exceeded", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_ValueErrorsInterruptConstruction()
    {
        // Object construction distributes over value outputs, so completed objects
        // escape before a later error, while array collection holds everything back.
        foreach (var (filter, stdout) in new (string, string)[]
        {
            ("{a: (1, error(\"x\"))}", "{\n  \"a\": 1\n}\n"),
            ("{a: (error(\"x\"), 1)}", ""),
            ("[(1, error(\"x\"))]", ""),
        })
        {
            var host = new MockFileSystem();
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
            Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal(stdout, host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal("jq: error (at <unknown>): x\n", host.GetOutput(JqFileDescriptor.StdErr));
        }
    }

    [Fact]
    public async Task Jq_RecursiveDescentMatchesManualExample()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[[{\"a\":1}]]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".. | .a?")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[[1]]", "[..] | length", "3\n")]
    [InlineData("[1,[[2]],{\"a\":[1]}]", "[..]", "[\n  [\n    1,\n    [\n      [\n        2\n      ]\n    ],\n    {\n      \"a\": [\n        1\n      ]\n    }\n  ],\n  1,\n  [\n    [\n      2\n    ]\n  ],\n  [\n    2\n  ],\n  2,\n  {\n    \"a\": [\n      1\n    ]\n  },\n  [\n    1\n  ],\n  1\n]\n")]
    public async Task Jq_RecursiveDescentVisitsPreOrder(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("{\"k\":\"K\"} | {(.k): 1}", "{\n  \"K\": 1\n}\n")]
    [InlineData("{\"ks\":[\"a\",\"b\"]} | {(.ks[]): 1}", "{\n  \"a\": 1\n}\n{\n  \"b\": 1\n}\n")]
    [InlineData("{\"ks\":[\"a\",\"b\"],\"vs\":[1,2]} | {((.ks[])): (.vs[])}", "{\n  \"a\": 1\n}\n{\n  \"a\": 2\n}\n{\n  \"b\": 1\n}\n{\n  \"b\": 2\n}\n")]
    [InlineData("{\"foo\":1} | .\"foo\"", "1\n")]
    [InlineData("{\"a\":{\"b\":2}} | .a.\"b\"", "2\n")]
    [InlineData("{\"a\":1} | {\"a$\\(1+1)\": 2}", "{\n  \"a$2\": 2\n}\n")]
    [InlineData("{\"a\":1, \"b\":2, \"c\":3, \"d\":\"c\"} | {a,b,(.d):.a,e:.b}", "{\n  \"a\": 1,\n  \"b\": 2,\n  \"c\": 1,\n  \"e\": 2\n}\n")]
    [InlineData("{\"user\":\"stedolan\",\"titles\":[\"JQ Primer\", \"More JQ\"]} | {(.user): .titles}", "{\n  \"stedolan\": [\n    \"JQ Primer\",\n    \"More JQ\"\n  ]\n}\n")]
    public async Task Jq_DynamicAndQuotedKeys(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    // Constant non-string keys fail at compile time, matching key validation.
    [InlineData("{(0):1}", "jq: Cannot use number (0) as object key at line 1 column 2 (filter)\n")]
    [InlineData("{(true):1}", "jq: Cannot use boolean (true) as object key at line 1 column 2 (filter)\n")]
    public async Task Jq_ConstantNonStringKeysFailAtCompile(string filter, string expectedError)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expectedError, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("# pick the field\n.foo", "1\n")]
    [InlineData("{\n\"a\": 1\n}", "{\n  \"a\": 1\n}\n")]
    public async Task Jq_CommentsAndLayouts(string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"foo\":1}");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"a\\qb\"", "invalid escape")]
    [InlineData("`", "invalid character")]
    [InlineData("1e", "invalid number")]
    [InlineData("1e+", "invalid number")]
    [InlineData("0x10", "expected End, got x10")]
    public async Task Jq_LexerErrorsIdentifyTheirSpan(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Contains("line 1 column", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_DynamicKeysFollowUpstreamChecks()
    {
        // Constant non-string keys fail at compile time (parser.y check_object_key)
        // while dynamic ones fail at evaluation (execute.c INSERT), with one wording.
        foreach (var (filter, exit, stderr) in new (string, int, string)[]
        {
            ("{(1, 2): \"x\"}", 5, "jq: error (at <unknown>): Cannot use number (1) as object key\n"),
            ("{(error(\"x\")): 1}", 5, "jq: error (at <unknown>): x\n"),
            ("{(1): \"x\"}", 3, "jq: Cannot use number (1) as object key at line 1 column 2 (filter)\n"),
            ("{(null): 1}", 3, "jq: Cannot use null (null) as object key at line 1 column 2 (filter)\n"),
        })
        {
            var host = new MockFileSystem();
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
            Assert.Equal(exit, await tool.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal(stderr, host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        }
    }

    [Fact]
    public async Task Jq_BareInterpolatedKeysReadInputFields()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"a\":1,\"b\":2,\"a$2\":4}");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "{\"a\",b,\"a$\\(1+1)\"}")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": 2,\n  \"a$2\": 4\n}\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_NestedQuotedInterpolation()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"(\":1}");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "\"a\\(.[\"(\"])b\"")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"a1b\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
