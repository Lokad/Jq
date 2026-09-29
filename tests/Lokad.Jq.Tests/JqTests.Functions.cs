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
    [InlineData("0", "[def f: 1; f]", "[1]\n")]
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
        Assert.Equal("[33,101]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_ClosuresCaptureVariables()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("\"more testing\"");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "def id(x): x; 2000 as $x | def f(x): 1 as $x | id([$x, x, x]); def g(x): 100 as $x | f($x, $x + x); g($x)")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[1,100,2100,100,2100]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_BacktrackingThroughCalls()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("999999999");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "[[20, 10][1, 0] as $x | def f: (100, 200) as $y | def g: [$x + $y, .]; . + $x | g; f[0] | [f][0][1] | f]")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[[110,130],[210,130],[110,230],[210,230],[120,160],[220,160],[120,260],[220,260]]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_FactorialRecurses()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[1, 2, 3, 4]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "def fac: if . == 1 then 1 else . * (. - 1 | fac) end; [.[] | fac]")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[1,2,6,24]\n", host.GetOutput(JqFileDescriptor.StdOut));
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

    [Fact]
    public async Task Jq_UnboundedRecursionExhaustsQuota()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "def r: r; r")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("filter nesting limit exceeded", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
}
