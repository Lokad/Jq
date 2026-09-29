using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("[-3.5,-2.5,-1.5,-0.5,0.5,1.5,2.5,3.5] | map(floor)", "[-4,-3,-2,-1,0,1,2,3]\n")]
    [InlineData("[-3.5,-2.5,-1.5,0.5,1.5,2.5,3.5] | map(ceil)", "[-3,-2,-1,1,2,3,4]\n")]
    [InlineData("[-3.5,-2.5,-1.5,0.5,1.5,2.5,3.5] | map(trunc)", "[-3,-2,-1,0,1,2,3]\n")]
    [InlineData("[-2.5,-1.5,-0.5,0.5,1.5,2.5] | map(round)", "[-3,-2,-1,1,2,3]\n")]
    [InlineData("[0.5,1.5,2.5,3.5] | map(nearbyint)", "[0,2,2,4]\n")]
    [InlineData("[0.5,1.5,2.5,3.5] | map(rint)", "[0,2,2,4]\n")]
    [InlineData("[-0,0,-10,-1.1] | map(fabs)", "[0,0,10,1.1]\n")]
    [InlineData("\"abc\" | abs", "\"abc\"\n")]
    [InlineData("[1] | abs", "[1]\n")]
    [InlineData("{\"a\":1} | abs", "{\"a\":1}\n")]
    [InlineData("[-5,-1.5,0,2.5] | map(abs)", "[5,1.5,0,2.5]\n")]
    [InlineData("(-0) | abs | tostring", "\"-0\"\n")]
    [InlineData("5 | pow(2;3)", "8\n")]
    [InlineData("\"ignored\" | pow(2;3)", "8\n")]
    [InlineData("\"ignored\" | atan2(1;1) | . > 0.78 and . < 0.79", "true\n")]
    public async Task Jq_MathRounding(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("4 | sqrt", "2\n")]
    [InlineData("27 | cbrt", "3\n")]
    [InlineData("0 | sqrt", "0\n")]
    [InlineData("(-1) | sqrt", "null\n")]
    [InlineData("0 | exp", "1\n")]
    [InlineData("3 | exp2", "8\n")]
    [InlineData("2 | exp10", "100\n")]
    [InlineData("1 | log", "0\n")]
    [InlineData("8 | log2", "3\n")]
    [InlineData("1000 | log10", "3\n")]
    [InlineData("0 | log", "-1.7976931348623157E+308\n")]
    [InlineData("(-1) | log", "null\n")]
    [InlineData("2 | pow(.;10)", "1024\n")]
    [InlineData("2 | pow(.;-1)", "0.5\n")]
    [InlineData("0 | pow(.;0)", "1\n")]
    [InlineData("0 | pow(.;-1)", "1.7976931348623157E+308\n")]
    [InlineData("(-1) | pow(.;0.5)", "null\n")]
    [InlineData("0 | atan2(.;1)", "0\n")]
    [InlineData("3 | hypot(.;4)", "5\n")]
    [InlineData("0 | sin", "0\n")]
    [InlineData("0 | cos", "1\n")]
    [InlineData("0 | tan", "0\n")]
    [InlineData("0 | asin", "0\n")]
    [InlineData("1 | acos", "0\n")]
    [InlineData("0 | atan", "0\n")]
    [InlineData("0 | sinh", "0\n")]
    [InlineData("0 | cosh", "1\n")]
    [InlineData("0 | tanh", "0\n")]
    [InlineData("0 | asinh", "0\n")]
    [InlineData("1 | acosh", "0\n")]
    [InlineData("0 | atanh", "0\n")]
    [InlineData("1000 | exp", "1.7976931348623157E+308\n")]
    [InlineData("(-1000) | exp", "0\n")]
    [InlineData("1e1000 | sin", "null\n")]
    public async Task Jq_MathExact(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    // Spot values below use a 1e-12 bound: transcendentals vary by ulps across C libraries.
    [InlineData("1 | exp | . - 2.718281828459045 | abs < 1e-12", "true\n")]
    [InlineData("10 | log | . - 2.302585092994046 | abs < 1e-12", "true\n")]
    [InlineData("1 | sin | . - 0.8414709848078965 | abs < 1e-12", "true\n")]
    [InlineData("1 | atan2(.;1) | . - 0.7853981633974483 | abs < 1e-12", "true\n")]
    [InlineData("2 | pow(.;0.5) | . - 1.4142135623730951 | abs < 1e-12", "true\n")]
    [InlineData("0.7853981633974483 | tan | . - 1 | abs < 1e-12", "true\n")]
    [InlineData("1e308 | hypot(.;1e308) | (. / 1e308 - 1.4142135623730951 | abs) < 1e-12", "true\n")]
    public async Task Jq_MathSpotValues(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("0 | isinfinite", "false\n")]
    [InlineData("1e1000 | isinfinite", "true\n")]
    [InlineData("(-1e1000) | isinfinite", "true\n")]
    [InlineData("1 | isfinite", "true\n")]
    [InlineData("1e1000 | isfinite", "false\n")]
    [InlineData("(-1) | sqrt | isfinite", "false\n")]
    [InlineData("\"x\" | isfinite", "false\n")]
    [InlineData("0 | isnan", "false\n")]
    [InlineData("(-1) | sqrt | isnan", "true\n")]
    [InlineData("\"x\" | isnan", "false\n")]
    [InlineData("1 | isnormal", "true\n")]
    [InlineData("0 | isnormal", "false\n")]
    [InlineData("5e-324 | isnormal", "false\n")]
    [InlineData("1e1000 | isnormal", "false\n")]
    [InlineData("(-1) | sqrt | isnormal", "false\n")]
    [InlineData("\"x\" | isnormal", "false\n")]
    [InlineData("null | isinfinite", "false\n")]
    [InlineData("infinite | isinfinite", "true\n")]
    [InlineData("infinite", "1.7976931348623157E+308\n")]
    [InlineData("nan", "null\n")]
    [InlineData("nan | type", "\"number\"\n")]
    [InlineData("have_decnum", "false\n")]
    [InlineData("have_literal_numbers", "false\n")]
    public async Task Jq_MathClassification(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"x\" | floor", "string (\"x\") number required")]
    [InlineData("true | sqrt", "boolean (true) number required")]
    [InlineData("null | sin", "null (null) number required")]
    [InlineData("5 | pow(\"x\"; 2)", "string (\"x\") number required")]
    [InlineData("pow(2; \"x\")", "string (\"x\") number required")]
    [InlineData("null | abs", "null (null) cannot be negated")]
    [InlineData("false | abs", "boolean (false) cannot be negated")]
    public async Task Jq_MathFailures(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("\"x\" | floor(1)", "expects no arguments")]
    [InlineData("pow(2)", "pow expects 2 arguments")]
    [InlineData("pow(2;3;4)", "pow expects 2 arguments")]
    public async Task Jq_MathArityIsCompileError(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
}
