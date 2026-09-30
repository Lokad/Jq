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
    [InlineData("null", ". as [$a] | $a", "null\n")]
    [InlineData("null", ". as {a: $x} | $x", "null\n")]
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
