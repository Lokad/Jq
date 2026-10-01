using Lokad.Jq;
using System.Text.Json.Nodes;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_NullIsAValueWhileEmptyIsNoValues()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "[null, empty] | length")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("null", "null\n")]
    [InlineData("empty", "")]
    [InlineData("\"hello\" | (null,1,null)", "null\n1\nnull\n")]
    [InlineData("[empty]", "[]\n")]
    [InlineData("[null]", "[\n  null\n]\n")]
    [InlineData("1, empty, 2", "1\n2\n")]
    [InlineData("[1,2,empty,3]", "[\n  1,\n  2,\n  3\n]\n")]
    [InlineData("null == null", "true\n")]
    public async Task Jq_NullVsEmptyStream(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("{\"b\":2,\"a\":[1,{\"x\":null}]} == {\"a\":[1,{\"x\":null}],\"b\":2}", "true\n")]
    [InlineData("[1,2] == [2,1]", "false\n")]
    [InlineData("[1,[2,3]] == [1,[2,3]]", "true\n")]
    [InlineData("{\"a\":1} == {\"a\":1,\"b\":2}", "false\n")]
    [InlineData("1 == 1.0", "true\n")]
    [InlineData("[ 10 == 10, 10 != 10, 10 != 11, 10 == 11]", "[\n  true,\n  false,\n  true,\n  false\n]\n")]
    [InlineData("[\"hello\" == \"hello\", \"hello\" != \"hello\", \"hello\" == \"world\", \"hello\" != \"world\" ]", "[\n  true,\n  false,\n  false,\n  true\n]\n")]
    [InlineData("[[1,2,3] == [1,2,3], [1,2,3] != [1,2,3], [1,2,3] == [4,5,6], [1,2,3] != [4,5,6]]", "[\n  true,\n  false,\n  false,\n  true\n]\n")]
    [InlineData("[{\"foo\":42} == {\"foo\":42},{\"foo\":42} != {\"foo\":42}, {\"foo\":42} != {\"bar\":42}, {\"foo\":42} == {\"bar\":42}]", "[\n  true,\n  false,\n  true,\n  false\n]\n")]
    [InlineData("[{\"foo\":[1,2,{\"bar\":18},\"world\"]} == {\"foo\":[1,2,{\"bar\":18},\"world\"]},{\"foo\":[1,2,{\"bar\":18},\"world\"]} == {\"foo\":[1,2,{\"bar\":19},\"world\"]}]", "[\n  true,\n  false\n]\n")]
    [InlineData("\"a\" == \"a\"", "true\n")]
    [InlineData("1 == \"1\"", "false\n")]
    [InlineData("true == 1", "false\n")]
    [InlineData("null == false", "false\n")]
    [InlineData("0 == false", "false\n")]
    [InlineData("[] == []", "true\n")]
    [InlineData("{} == {}", "true\n")]
    [InlineData("{\"a\":1, \"b\": {\"c\": 3, \"d\": 4}} | . == {\"b\": {\"d\": (4 + 1e-20), \"c\": 3}, \"a\":1}", "true\n")]
    [InlineData("[1, 1.0, \"1\", \"banana\"] | [.[] == 1]", "[\n  true,\n  true,\n  false,\n  false\n]\n")]
    [InlineData("[1, 1.0, 1.000, 100e-2, 1e+0, 0.0001e4] | map(. == 1)", "[\n  true,\n  true,\n  true,\n  true,\n  true,\n  true\n]\n")]
    [InlineData(". == false", "false\n")]
    public async Task Jq_EqualityFollowsValueSemantics(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[3 < \"a\", \"a\" < 3, null < false, false < true, true < 1, 1 < \"a\", \"a\" < [], [] < {}, {} < null]", "[\n  true,\n  false,\n  true,\n  true,\n  true,\n  true,\n  true,\n  true,\n  false\n]\n")]
    [InlineData("[1,2] < [1,3]", "true\n")]
    [InlineData("[10 >= 0, 10 >= 10, 10 >= 20, 10 <= 0, 10 <= 10, 10 <= 20]", "[\n  true,\n  true,\n  false,\n  false,\n  true,\n  true\n]\n")]
    [InlineData("[1] < [1,2]", "true\n")]
    [InlineData("[1,2] < [1,2]", "false\n")]
    [InlineData("2 | . < 5", "true\n")]
    [InlineData("{\"a\":1} < {\"b\":1}", "true\n")]
    [InlineData("{\"a\":2} > {\"a\":1}", "true\n")]
    [InlineData("{\"a\":1} < {\"a\":1,\"b\":2}", "true\n")]
    [InlineData("\"\\ue000\" < \"\U00010000\"", "true\n")]
    [InlineData("[nan < 1, 1 < nan, nan == nan]", "[\n  true,\n  false,\n  false\n]\n")]
    // Null orders strictly before NaN (reference kind difference) while NaN still compares as null against numbers.
    [InlineData("[nan > null, null < nan, nan < null, null > nan]", "[\n  true,\n  true,\n  false,\n  false\n]\n")]
    public async Task Jq_ComparisonFollowsTotalOrdering(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_MixedArrayMinMaxFollowTotalOrdering()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "{min:([1,\"a\",null,true] | min), max:([1,\"a\",null,true] | max)}")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("{\n  \"min\": null,\n  \"max\": \"a\"\n}\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
    [Fact]
    public async Task Jq_NanSortsAsNull()
    {
        // The shared total order ranks NaN immediately after null while never
        // equating it, so sorting and extrema agree with comparisons.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "{sorted: ([1, nan] | sort), minimum: ([1, nan] | min), maximum: ([1, nan] | max)}")));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("{\n  \"sorted\": [\n    null,\n    1\n  ],\n  \"minimum\": null,\n  \"maximum\": 1\n}\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    // The numeric profile is double-based (see docs/NUMERIC_PROFILE.md):
    // cases marked decimal-divergence pin double behavior that differs
    // from decimal-literal builds.
    [Theory]
    [InlineData("9007199254740992 + 1", "9007199254740992\n")]
    [InlineData("9007199254740993 == 9007199254740992", "true\n")]
    [InlineData("10000000000000000000000000000001 | . as $big | [$big, $big + 1] | map(. > 10000000000000000000000000000000) | . == if have_decnum then [true, false] else [false, false] end", "true\n")]
    [InlineData("[1, 1.000, 1.0, 100e-2] | map([., . == 1]) | tojson == if have_decnum then \"[[1,true],[1.000,true],[1.0,true],[1.00,true]]\" else \"[[1,true],[1,true],[1,true],[1,true]]\" end", "true\n")]
    [InlineData("0.12345678901234567890123456789 | . < 0.12345678901234567890123456788", "false\n")]
    [InlineData("0.1 + 0.2 == 0.3", "false\n")]
    [InlineData("1.10 | tostring", "\"1.1\"\n")]
    [InlineData("1e3", "1000\n")]
    [InlineData("9007199254740993", "9007199254740993\n")]
    [InlineData("9007199254740993 | tostring", "\"9007199254740993\"\n")]
    [InlineData("(-0.0) == 0", "true\n")]
    [InlineData("(-0.0) | tostring", "\"-0\"\n")]
    [InlineData("(-0.0)", "-0\n")]
    [InlineData("nan | tojson", "\"null\"\n")]
    [InlineData("[nan] | tojson", "\"[null]\"\n")]
    [InlineData("{\"a\": nan} | tojson", "\"{\\\"a\\\":null}\"\n")]
    public async Task Jq_NumbersFollowDoubleProfile(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("1/0", "jq: number (1) and number (0) cannot be divided because the divisor is zero\n")]
    [InlineData("1/-0.0", "jq: number (1) and number (-0) cannot be divided because the divisor is zero\n")]
    [InlineData("0/0", "jq: number (0) and number (0) cannot be divided because the divisor is zero\n")]
    [InlineData("1%0", "jq: number (1) and number (0) cannot be divided (remainder) because the divisor is zero\n")]
    public async Task Jq_ZeroDivisorsAreRuntimeErrors(string filter, string expectedError)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expectedError, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("5 % 3", "2\n")]
    [InlineData("5.5 % 2", "1\n")]
    [InlineData("7 % -3", "1\n")]
    [InlineData("7 % -1", "0\n")]
    [InlineData("(-7) % 3", "-1\n")]
    public async Task Jq_RemainderTruncatesOperands(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("((-1) | sqrt)", "null\n")]
    [InlineData("((-1) | sqrt) | type", "\"number\"\n")]
    [InlineData("((-1) | sqrt) == ((-1) | sqrt)", "false\n")]
    [InlineData("((-1) | sqrt) != ((-1) | sqrt)", "true\n")]
    [InlineData("[((-1) | sqrt)] == [((-1) | sqrt)]", "false\n")]
    [InlineData("((-1) | sqrt) < false", "true\n")]
    [InlineData("1 % ((-1) | sqrt)", "null\n")]
    [InlineData("1e1000", "1.7976931348623157E+308\n")]
    [InlineData("[((-1) | sqrt), 1]", "[\n  null,\n  1\n]\n")]
    [InlineData("{\"x\":((-1) | sqrt)}", "{\n  \"x\": null\n}\n")]
    [InlineData("[1e1000]", "[\n  1.7976931348623157E+308\n]\n")]
    [InlineData("\"nan\" | fromjson | isnan", "true\n")]
    [InlineData("\"NaN\" | fromjson", "null\n")]
    [InlineData("\"-Infinity\" | fromjson", "-1.7976931348623157E+308\n")]
    [InlineData("{\"a\": nan} | tojson | fromjson", "{\n  \"a\": null\n}\n")]
    public async Task Jq_NonFiniteValuesRenderAsJson(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_NonFiniteInputClampsOnOutput()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("1e1000");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("1.7976931348623157E+308\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_NonFiniteInputParsesLiterals()
    {
        var cases = new (string Stdin, string Expected)[]
        {
            ("nan", "null\n"),
            ("-Infinity", "-1.7976931348623157E+308\n"),
            ("[nan]", "[\n  null\n]\n"),
            ("{\"a\": nan, \"b\": 1}", "{\n  \"a\": null,\n  \"b\": 1\n}\n"),
            ("{\"a\":nan}", "{\n  \"a\": null\n}\n"),
            ("{\"a\": [1, nan]}", "{\n  \"a\": [\n    1,\n    null\n  ]\n}\n"),
        };
        foreach (var (stdin, expected) in cases)
        {
            var host = new MockFileSystem();
            host.SetStandardInput(stdin);
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".")));
            Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        }
    }

    [Fact]
    public void Jq_RepeatedEvaluationYieldsIndependentValues()
    {
        var variables = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>();
        var budget = new JqBudget(CancellationToken.None);
        using var context = new JqContext(variables, JqProgramSource.Inline, budget);
        JqFilter filter = new JqParser("{\"a\":[1]}", JqProgramSource.Inline, context.RootEnvironment, budget).Parse();

        List<System.Text.Json.Nodes.JsonNode?> first = filter.Evaluate(null, context, context.RootEnvironment).ToList();
        List<System.Text.Json.Nodes.JsonNode?> second = filter.Evaluate(null, context, context.RootEnvironment).ToList();

        Assert.Single(first);
        Assert.Single(second);
        Assert.NotSame(first[0], second[0]);
        JsonObject firstObject = Assert.IsType<JsonObject>(first[0]);
        firstObject.Add("b", 2);
        Assert.Equal("{\"a\":[1],\"b\":2}", firstObject.ToJsonString());
        JsonObject secondObject = Assert.IsType<JsonObject>(second[0]);
        Assert.Equal("{\"a\":[1]}", secondObject.ToJsonString());
    }

    [Fact]
    public void Jq_NumericSyntaxIgnoresAmbientCulture()
    {
        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
            var variables = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>();
            var budget = new JqBudget(CancellationToken.None);
            using var context = new JqContext(variables, JqProgramSource.Inline, budget);
            JqFilter filter = new JqParser("1.5 + 2.5", JqProgramSource.Inline, context.RootEnvironment, budget).Parse();
            List<System.Text.Json.Nodes.JsonNode?> results = filter.Evaluate(null, context, context.RootEnvironment).ToList();

            Assert.Single(results);
            Assert.Equal(4.0, JqRuntime.Number(results[0]));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
