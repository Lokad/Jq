using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_AsRunsBodyAgainstOuterInput()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"foo\":10,\"bar\":200}");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".bar as $x | .foo | . + $x")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("210\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_NestedBindingsShadowOuterOnes()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("5");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ". as $i | [(.*2 | . as $i | $i), $i]")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[\n  10,\n  5\n]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[2, 3, {\"c\": 4, \"d\": 5}]", ". as [$a, $b, {c: $c}] | $a + $b + $c", "9\n")]
    [InlineData("[[0], [0, 1], [2, 1, 0]]", ".[] as [$a, $b] | {a: $a, b: $b}", "{\n  \"a\": 0,\n  \"b\": null\n}\n{\n  \"a\": 0,\n  \"b\": 1\n}\n{\n  \"a\": 2,\n  \"b\": 1\n}\n")]
    [InlineData("{}", ". as {a: $x} | $x", "null\n")]
    [InlineData("[]", ". as [$a] | $a", "null\n")]
    [InlineData("[{\"a\":1, \"b\":[2,{\"d\":3}]}, [4, {\"b\":5, \"c\":6}, 7, 8, 9], \"foo\"]", ".[] | . as {$a, b: [$c, {$d}]} ?// [$a, {$b}, $e] ?// $f | [$a, $b, $c, $d, $e, $f]", "[\n  1,\n  null,\n  2,\n  3,\n  null,\n  null\n]\n[\n  4,\n  5,\n  null,\n  null,\n  7,\n  null\n]\n[\n  null,\n  null,\n  null,\n  null,\n  null,\n  \"foo\"\n]\n")]
    [InlineData("[[3],[4],[5],6]", ".[] | . as {a:$a} ?// {a:$a} ?// $a | $a", "[\n  3\n]\n[\n  4\n]\n[\n  5\n]\n6\n")]
    [InlineData("[[3],[4],[5],6]", ".[] as {a:$a} ?// {a:$a} ?// $a | $a", "[\n  3\n]\n[\n  4\n]\n[\n  5\n]\n6\n")]
    [InlineData("null", "[[3],[4],[5],6][] | . as {a:$a} ?// {a:$a} ?// $a | $a", "[\n  3\n]\n[\n  4\n]\n[\n  5\n]\n6\n")]
    [InlineData("[[3],[4],[5],6]", ".[] | . as {a:$a} ?// $a ?// {a:$a} | $a", "[\n  3\n]\n[\n  4\n]\n[\n  5\n]\n6\n")]
    [InlineData("[[3],[4],[5],6]", ".[] | . as $a ?// {a:$a} ?// {a:$a} | $a", "[\n  3\n]\n[\n  4\n]\n[\n  5\n]\n6\n")]
    [InlineData("null", ". as [$a] | $a", "null\n")]
    [InlineData("34324", "42 as $x | . | . | . + 432 | $x + 1", "43\n")]
    [InlineData("[0]", ". as $i | . as [$i] | $i", "0\n")]
    [InlineData("[0]", ". as [$i] | . as $i | $i", "[\n  0\n]\n")]
    [InlineData("{\"object\": {\"a\":42}, \"num\":10.0}", "[{\"a\":42},.object,10,.num,false,true,null,\"b\",[1,4]] | .[] as $x | [$x == .[]]", "[\n  true,\n  true,\n  false,\n  false,\n  false,\n  false,\n  false,\n  false,\n  false\n]\n[\n  true,\n  true,\n  false,\n  false,\n  false,\n  false,\n  false,\n  false,\n  false\n]\n[\n  false,\n  false,\n  true,\n  true,\n  false,\n  false,\n  false,\n  false,\n  false\n]\n[\n  false,\n  false,\n  true,\n  true,\n  false,\n  false,\n  false,\n  false,\n  false\n]\n[\n  false,\n  false,\n  false,\n  false,\n  true,\n  false,\n  false,\n  false,\n  false\n]\n[\n  false,\n  false,\n  false,\n  false,\n  false,\n  true,\n  false,\n  false,\n  false\n]\n[\n  false,\n  false,\n  false,\n  false,\n  false,\n  false,\n  true,\n  false,\n  false\n]\n[\n  false,\n  false,\n  false,\n  false,\n  false,\n  false,\n  false,\n  true,\n  false\n]\n[\n  false,\n  false,\n  false,\n  false,\n  false,\n  false,\n  false,\n  false,\n  true\n]\n")]
    [InlineData("5", ". as $i|[(.*2|. as $i| $i), $i]", "[\n  10,\n  5\n]\n")]
    [InlineData("{\"as\":8}", "1 as $x | \"2\" as $y | \"3\" as $z | { $x, as, $y: 4, ($z): 5, if: 6, foo: 7 }", "{\n  \"x\": 1,\n  \"as\": 8,\n  \"2\": 4,\n  \"3\": 5,\n  \"if\": 6,\n  \"foo\": 7\n}\n")]
    [InlineData("null", "[1, {c:3, d:4}] as [$a, {c:$b, b:$c}] | $a, $b, $c", "1\n3\nnull\n")]
    [InlineData("{\"as\": 1, \"str\": 2, \"exp\": 3}", ". as {as: $kw, \"str\": $str, (\"e\"+\"x\"+\"p\"): $exp} | [$kw, $str, $exp]", "[\n  1,\n  2,\n  3\n]\n")]
    [InlineData("[[1], [1, 2, 3]]", ".[] as [$a, $b] | [$b, $a]", "[\n  null,\n  1\n]\n[\n  2,\n  1\n]\n")]
    [InlineData("{\"a\":1, \"b\":[2,{\"d\":3}]}", ". as {$a, $b:[$c, $d]}| [$a, $b, $c, $d]", "[\n  1,\n  [\n    2,\n    {\n      \"d\": 3\n    }\n  ],\n  2,\n  {\n    \"d\": 3\n  }\n]\n")]
    [InlineData("null", ". as {a: $x} | $x", "null\n")]
    [InlineData("{\"a\":4,\"b\":5}", "1 as $foreach | 2 as $and | 3 as $or | { $foreach, $and, $or, a }", "{\n  \"foreach\": 1,\n  \"and\": 2,\n  \"or\": 3,\n  \"a\": 4\n}\n")]
    public async Task Jq_DestructuringBindsMissingAsNull(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_ArrayPatternRejectsObjects()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "{} as [$a] | $a")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("cannot index object", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("(1, 2) as $x | $x", "1\n2\n")]
    [InlineData("(1, 2) as $x | [$x]", "[\n  1\n]\n[\n  2\n]\n")]
    [InlineData("(1, 2) as $x | [$x, $x]", "[\n  1,\n  1\n]\n[\n  2,\n  2\n]\n")]
    [InlineData("empty as $x | 1", "")]
    [InlineData("null as $x | $x", "null\n")]
    [InlineData("1, 2 as $x | $x", "1\n2\n")]
    [InlineData("1 | 2 as $x | $x", "2\n")]
    [InlineData("1 as $x | 2 as $y | [$x,$y,$x]", "[\n  1,\n  2,\n  1\n]\n")]
    [InlineData("[1,2,3][] as $x | [[4,5,6,7][$x]]", "[\n  5\n]\n[\n  6\n]\n[\n  7\n]\n")]
    [InlineData("\"x\" as $x | \"a\"+\"y\" as $y | $x+\",\"+$y", "\"x,ay\"\n")]
    [InlineData("1 as $x | [$x,$x,$x as $x | $x]", "[\n  1,\n  1,\n  1\n]\n")]
    [InlineData("{if:0,and:1,or:2,then:3,else:4,elif:5,end:6,as:7,def:8,reduce:9,foreach:10,try:11,catch:12,label:13,import:14,include:15,module:16}", "{\n  \"if\": 0,\n  \"and\": 1,\n  \"or\": 2,\n  \"then\": 3,\n  \"else\": 4,\n  \"elif\": 5,\n  \"end\": 6,\n  \"as\": 7,\n  \"def\": 8,\n  \"reduce\": 9,\n  \"foreach\": 10,\n  \"try\": 11,\n  \"catch\": 12,\n  \"label\": 13,\n  \"import\": 14,\n  \"include\": 15,\n  \"module\": 16\n}\n")]
    public async Task Jq_BinderStreamsAndGroups(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("(1 as $x | $x), $x")]
    [InlineData("1 as $x | $y")]
    [InlineData("$undefined")]
    public async Task Jq_BindingsStayLexicallyScoped(string filter)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("undefined variable", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_SourceErrorsPropagateWithoutBinding()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "(1 | .foo) as $x | $x")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("cannot index number", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_ArgumentsStayVisibleInsideBindings()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "--arg", "name", "Bob", ". as $me | {me: $me, name: $name}")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("{\n  \"me\": null,\n  \"name\": \"Bob\"\n}\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_BindingsDoNotLeakAcrossExecutions()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "1 as $x | $x")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("1\n1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_AlternationPrefersFirstMatch()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[{\"a\": 1, \"b\": 2, \"c\": {\"d\": 3, \"e\": 4}}, {\"a\": 1, \"b\": 2, \"c\": [{\"d\": 3, \"e\": 4}]}]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".[] as {$a, $b, c: {$d, $e}} ?// {$a, $b, c: [{$d, $e}]} | {$a, $b, $d, $e}")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": 2,\n  \"d\": 3,\n  \"e\": 4\n}\n{\n  \"a\": 1,\n  \"b\": 2,\n  \"d\": 3,\n  \"e\": 4\n}\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_AlternationLeavesUnmatchedNull()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[{\"a\": 1, \"b\": 2, \"c\": {\"d\": 3, \"e\": 4}}, {\"a\": 1, \"b\": 2, \"c\": [{\"d\": 3, \"e\": 4}]}]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".[] as {$a, $b, c: {$d}} ?// {$a, $b, c: [{$e}]} | {$a, $b, $d, $e}")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": 2,\n  \"d\": 3,\n  \"e\": null\n}\n{\n  \"a\": 1,\n  \"b\": 2,\n  \"d\": null,\n  \"e\": 4\n}\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_AlternationRetriesAfterBodyErrors()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[[3]]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".[] as [$a] ?// [$b] | if $a != null then (1 | .foo) else {$a,$b} end")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("{\n  \"a\": null,\n  \"b\": 3\n}\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_AlternationExhaustionPropagatesErrors()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "[3] as {a:$a} ?// {a:$a} ?// {a:$a} | $a")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("cannot index array", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_AlternationDoesNotRepeatSuccesses()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[[3],[4],[5],6]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".[] as $a ?// {a:$a} ?// {a:$a} | $a")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[\n  3\n]\n[\n  4\n]\n[\n  5\n]\n6\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"v\" as $x | {$x}", "{\n  \"x\": \"v\"\n}\n")]
    [InlineData("\"k\" as $x | {$x: 1}", "{\n  \"k\": 1\n}\n")]
    public async Task Jq_ObjectShorthandsUseBindings(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_VariableObjectKeysMustBeStrings()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "1 as $x | {$x: 1}")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("Cannot use number (1)", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }


    [Fact]
    public async Task Jq_UndefinedObjectVariableFailsAtCompile()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "{$missing}")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("undefined variable $missing", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("[1 as $x | $x]", "[\n  1\n]\n")]
    [InlineData("{\"k\": (1 as $x | $x)}", "{\n  \"k\": 1\n}\n")]
    [InlineData("\"\\(1 as $x | $x + 1)\"", "\"2\"\n")]
    public async Task Jq_BindingsNestInsideConstructs(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("{\"x\":1}", ". as {$x: $y} | [$x, $y]", "[\n  1,\n  1\n]\n")]
    [InlineData("{\"x\":[7]}", ". as {$x: [$x]} | $x", "7\n")]
    public async Task Jq_AliasPatternsBindValueAndMatchInner(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_DeeplyNestedPatternsStayBounded()
    {
        var host = new MockFileSystem();
        var filter = ". as " + string.Concat(Enumerable.Repeat("[", 70)) + string.Concat(Enumerable.Repeat("]", 70)) + " | 1";
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("filter nesting limit exceeded", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
}
