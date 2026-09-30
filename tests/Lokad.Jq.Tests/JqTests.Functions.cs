using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("5", "def twice(f): f | f; twice(. + 1)", "7\n")]
    [InlineData("5", "def twice(f): f, f; twice(. + 1)", "6\n6\n")]
    [InlineData("1", "def f: . + 1; f", "2\n")]
    [InlineData("1", "def f: 1; def f: 2; f", "2\n")]
    [InlineData("1", "def f: 1; def f(x): x; f, f(2)", "1\n2\n")]
    [InlineData("9", "def f: def g: 1; g; f", "1\n")]
    [InlineData("0", "[def f: 1; f]", "[\n  1\n]\n")]
    [InlineData("0", "(def f: 3; f)", "3\n")]
    [InlineData("1", "1 | def f: 2; f", "2\n")]
    [InlineData("0", "def length: 42; length", "42\n")]
    public async Task Jq_DefDefinesCallableFilters(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_ValueParamsStreamPerValue()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "def f($x): $x, $x; f(1, 2)")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("1\n1\n2\n2\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_BodiesCaptureDefinitionEnvironments()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("0");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "1 as $x | def f: $x; 2 as $x | f")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_FilterArgsSeeCallSiteBindings()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("0");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "1 as $x | def f(g): 2 as $x | g; f($x)")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_FilterArgsReevaluatePerInput()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[1, 2]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "def twice(f): f, f; .[] | twice(. + 10)")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("11\n11\n12\n12\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_RedefinitionKeepsClosureEnvironments()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("1");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "def f: . + 1; def g: f; def f: . + 100; def f(a): a + . + 11; [(g | f(20)), f]")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[\n  33,\n  101\n]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_ClosuresCaptureVariables()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("\"more testing\"");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "def id(x): x; 2000 as $x | def f(x): 1 as $x | id([$x, x, x]); def g(x): 100 as $x | f($x, $x + x); g($x)")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[\n  1,\n  100,\n  2100,\n  100,\n  2100\n]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_BacktrackingThroughCalls()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("999999999");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "[[20, 10][1, 0] as $x | def f: (100, 200) as $y | def g: [$x + $y, .]; . + $x | g; f[0] | [f][0][1] | f]")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[\n  [\n    110,\n    130\n  ],\n  [\n    210,\n    130\n  ],\n  [\n    110,\n    230\n  ],\n  [\n    210,\n    230\n  ],\n  [\n    120,\n    160\n  ],\n  [\n    220,\n    160\n  ],\n  [\n    120,\n    260\n  ],\n  [\n    220,\n    260\n  ]\n]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_FactorialRecurses()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[1, 2, 3, 4]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "def fac: if . == 1 then 1 else . * (. - 1 | fac) end; [.[] | fac]")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[\n  1,\n  2,\n  6,\n  24\n]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_DefInInterpolation()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "\"\\(def f: 7; f)\"")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"7\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("def f: $x; 1 as $x | f", "undefined variable $x")]
    [InlineData("def f: def g: 1; g; g", "unsupported function g")]
    [InlineData("def a: b; def b: 1; a", "unsupported function b")]
    public async Task Jq_FunctionScopeErrorsFailAtCompile(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("def sum($n; $a): if $n <= 0 then $a else sum($n - 1; $a + $n) end; sum(2000; 0)", "2001000\n")]
    [InlineData("def down: if . <= 0 then . else . - 1 | down end; 10000 | down", "0\n")]
    [InlineData("def f($n): if $n == 0 then 0 else (1, 2) as $x | f($n - 1) end; f(2)", "0\n0\n0\n0\n")]
    [InlineData("def d: if . <= 0 then . else . - 1 | d end; (2, 5) | d", "0\n0\n")]
    [InlineData("def f($n): if $n == 0 then \"done\" else (10, 20) | f($n - 1) end; f(1)", "\"done\"\n\"done\"\n")]
    public async Task Jq_TailCallsRunWithoutNesting(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_NonTailRecursionExhaustsNesting()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "def f($n): if $n == 0 then 0 else [$n] + f($n - 1) end; f(1000)")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("filter nesting limit exceeded", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_UnboundedRecursionExhaustsQuota()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "def r: r; r")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("value budget exceeded", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
    [Fact]
    public async Task Jq_UserValueArgumentsCombineFirstOuter()
    {
        // Like the reference range/3 vector, the first value argument
        // varies slowest; filter arguments stay lazy per use.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "def f($a;$b): [$a,$b]; [f((1,2);(3,4))]")));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[\n  [\n    1,\n    3\n  ],\n  [\n    1,\n    4\n  ],\n  [\n    2,\n    3\n  ],\n  [\n    2,\n    4\n  ]\n]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
    [Fact]
    public async Task Jq_MixedValueAndFilterArgumentsInteract()
    {
        // Value arguments stream first-outer across body runs while the
        // array constructor collects each run (filter arguments stay lazy
        // per use inside the run).
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "def f($a; g): [$a, g]; [f((1, 2); (3, 4))]")));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[\n  [\n    1,\n    3,\n    4\n  ],\n  [\n    2,\n    3,\n    4\n  ]\n]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
