using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("[-3.5,-2.5,-1.5,-0.5,0.5,1.5,2.5,3.5] | map(floor)", "[\n  -4,\n  -3,\n  -2,\n  -1,\n  0,\n  1,\n  2,\n  3\n]\n")]
    [InlineData("[-3.5,-2.5,-1.5,0.5,1.5,2.5,3.5] | map(ceil)", "[\n  -3,\n  -2,\n  -1,\n  1,\n  2,\n  3,\n  4\n]\n")]
    [InlineData("[-3.5,-2.5,-1.5,0.5,1.5,2.5,3.5] | map(trunc)", "[\n  -3,\n  -2,\n  -1,\n  0,\n  1,\n  2,\n  3\n]\n")]
    [InlineData("[-2.5,-1.5,-0.5,0.5,1.5,2.5] | map(round)", "[\n  -3,\n  -2,\n  -1,\n  1,\n  2,\n  3\n]\n")]
    [InlineData("[0.5,1.5,2.5,3.5] | map(nearbyint)", "[\n  0,\n  2,\n  2,\n  4\n]\n")]
    [InlineData("[0.5,1.5,2.5,3.5] | map(rint)", "[\n  0,\n  2,\n  2,\n  4\n]\n")]
    [InlineData("[-0,0,-10,-1.1] | map(fabs)", "[\n  0,\n  0,\n  10,\n  1.1\n]\n")]
    [InlineData("\"abc\" | abs", "\"abc\"\n")]
    [InlineData("[1] | abs", "[\n  1\n]\n")]
    [InlineData("{\"a\":1} | abs", "{\n  \"a\": 1\n}\n")]
    [InlineData("[-5,-1.5,0,2.5] | map(abs)", "[\n  5,\n  1.5,\n  0,\n  2.5\n]\n")]
    [InlineData("(-0) | abs | tostring", "\"0\"\n")]
    [InlineData("[-0,0,-10,-1.1] | map(abs)", "[\n  0,\n  0,\n  10,\n  1.1\n]\n")]
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
    [InlineData("[-8,-1,1,8] | map(cbrt)", "[\n  -2,\n  -1,\n  1,\n  2\n]\n")]
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
    [InlineData("-10E-1000000001", "-0\n")]
    [InlineData("1 + 2 * 2 + 10 / 2", "10\n")]
    // Multi-valued math arguments combine last-argument-outer like the
    // generic C call prelude (unlike first-outer user value arguments).
    [InlineData("[pow((2,3); (2,3))]", "[\n  4,\n  9,\n  8,\n  27\n]\n")]
    // Upstream jq.test expects `20e-1` here, but its harness compares with
    // jv_equal (value equality), and `2` is the double-domain rendering of
    // that value per the numeric profile. The input literal is piped in
    // because the program ignores its input.
    [InlineData("\"I wonder what this will be?\" | 1e+0+0.001e3", "2\n")]
    [InlineData("1 | atan * 4 * 1000000|floor / 1000000", "3.141592\n")]
    [InlineData("[-1.1,1.1,1.9] | [.[]|floor]", "[\n  -2,\n  1,\n  1\n]\n")]
    [InlineData("[4,9] | [.[]|sqrt]", "[\n  2,\n  3\n]\n")]
    [InlineData("[1,0,-1] | [.[] | (1 / .)?]", "[\n  1,\n  -1\n]\n")]
    [InlineData("13911860366432393 | . - 10", "13911860366432382\n")]
    [InlineData("[13911860366432393] | .[0] - 10", "13911860366432382\n")]
    [InlineData("{\"x\":13911860366432393} | .x - 10", "13911860366432382\n")]
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
    // Official jq 1.8.2 emits 3 for cbrt(27) on Windows and
    // 3.0000000000000004 on Linux; use the same accuracy bound on both.
    [InlineData("27 | cbrt | . - 3 | abs < 1e-12", "true\n")]
    [InlineData("(-27) | cbrt | . + 3 | abs < 1e-12", "true\n")]
    [InlineData("1 | exp | . - 2.718281828459045 | abs < 1e-12", "true\n")]
    [InlineData("10 | log | . - 2.302585092994046 | abs < 1e-12", "true\n")]
    [InlineData("1 | sin | . - 0.8414709848078965 | abs < 1e-12", "true\n")]
    [InlineData("1 | atan2(.;1) | . - 0.7853981633974483 | abs < 1e-12", "true\n")]
    [InlineData("2 | pow(.;0.5) | . - 1.4142135623730951 | abs < 1e-12", "true\n")]
    [InlineData("0.7853981633974483 | tan | . - 1 | abs < 1e-12", "true\n")]
    [InlineData("1e308 | hypot(.;1e308) | (. / 1e308 - 1.4142135623730951 | abs) < 1e-12", "true\n")]
    [InlineData("[(3.141592 / 2) * (range(0;20) / 20)|cos * 1000000|floor / 1000000]", "[\n  1,\n  0.996917,\n  0.987688,\n  0.972369,\n  0.951056,\n  0.923879,\n  0.891006,\n  0.85264,\n  0.809017,\n  0.760406,\n  0.707106,\n  0.649448,\n  0.587785,\n  0.522498,\n  0.45399,\n  0.382683,\n  0.309017,\n  0.233445,\n  0.156434,\n  0.078459\n]\n")]
    [InlineData("[(3.141592 / 2) * (range(0;20) / 20)|sin * 1000000|floor / 1000000]", "[\n  0,\n  0.078459,\n  0.156434,\n  0.233445,\n  0.309016,\n  0.382683,\n  0.45399,\n  0.522498,\n  0.587785,\n  0.649447,\n  0.707106,\n  0.760405,\n  0.809016,\n  0.85264,\n  0.891006,\n  0.923879,\n  0.951056,\n  0.972369,\n  0.987688,\n  0.996917\n]\n")]
    [InlineData("[range(-52;52;1)] as $powers | [$powers[]|pow(2;.)|log2|round] == $powers", "true\n")]
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
    // Upstream exponent vectors: overflow clamps to the largest finite double on render while tiny quotients keep the uppercase exponent.
    [InlineData("1 / 1e-17", "1E+17\n")]
    [InlineData("9E999999999, 9999999999E999999990, 1E-999999999, 0.000000001E-999999990", "1.7976931348623157E+308\n1.7976931348623157E+308\n0\n0\n")]
    [InlineData("5E500000000 > 5E-5000000000, 10000E500000000 > 10000E-5000000000", "true\ntrue\n")]
    [InlineData("(1e999999999, 10e999999999) > (1e-1147483646, 0.1e-1147483646)", "true\ntrue\ntrue\ntrue\n")]
    [InlineData("nan", "null\n")]
    [InlineData("nan | type", "\"number\"\n")]
    [InlineData("have_decnum", "false\n")]
    [InlineData("have_literal_numbers", "false\n")]
    [InlineData("[-1, 1] | .[] | (infinite * .) < 0", "true\nfalse\n")]
    [InlineData("infinite, nan | type", "\"number\"\n\"number\"\n")]
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
    [InlineData("null | fabs", "null (null) number required")]
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

    [Theory]
    [InlineData("5.5 | fmod(.;2)", "1.5\n")]
    [InlineData("0 - 5.5 | fmod(.;2)", "-1.5\n")]
    [InlineData("5 | fmod(.;0)", "null\n")]
    [InlineData("5.5 | remainder(.;2)", "-0.5\n")]
    [InlineData("0 - 5.5 | remainder(.;2)", "0.5\n")]
    [InlineData("5 | remainder(.;0)", "null\n")]
    [InlineData("5.5 | drem(.;2)", "-0.5\n")]
    [InlineData("[fmax(3;7)]", "[\n  7\n]\n")]
    [InlineData("[fmin(3;7)]", "[\n  3\n]\n")]
    [InlineData("[fmax((-1 | sqrt); 5)]", "[\n  5\n]\n")]
    [InlineData("[fmin((-1 | sqrt); 5)]", "[\n  5\n]\n")]
    [InlineData("[fmax(0 * -1; 0)]", "[\n  0\n]\n")]
    [InlineData("[fmin(0 * -1; 0)]", "[\n  -0\n]\n")]
    [InlineData("fdim(5;3)", "2\n")]
    [InlineData("fdim(3;5)", "0\n")]
    [InlineData("fdim(2;2)", "0\n")]
    [InlineData("copysign(1;-1)", "-1\n")]
    [InlineData("copysign(0 * -1; 1)", "0\n")]
    [InlineData("copysign(1; 0 * -1)", "-1\n")]
    [InlineData("nextafter(1;2)", "1.0000000000000002\n")]
    [InlineData("nextafter(1;0)", "0.9999999999999999\n")]
    [InlineData("nextafter(0;1)", "5E-324\n")]
    [InlineData("nexttoward(1;2)", "1.0000000000000002\n")]
    [InlineData("ldexp(1.5;3)", "12\n")]
    [InlineData("scalb(1.5;3)", "12\n")]
    [InlineData("scalbln(1.5;3)", "12\n")]
    [InlineData("8 | logb", "3\n")]
    [InlineData("0 | logb", "-1.7976931348623157E+308\n")]
    [InlineData("1e1000 | logb", "1.7976931348623157E+308\n")]
    [InlineData("0 | log1p", "0\n")]
    [InlineData("1 | log1p | . - 0.6931471805599453 | abs < 1e-12", "true\n")]
    [InlineData("0 - 1 | log1p", "-1.7976931348623157E+308\n")]
    [InlineData("0 | expm1", "0\n")]
    [InlineData("1 | expm1 | . - 1.718281828459045 | abs < 1e-12", "true\n")]
    [InlineData("0 - 1000 | expm1", "-1\n")]
    [InlineData("6.5 | significand", "1.625\n")]
    [InlineData("0 | significand", "0\n")]
    [InlineData("3.75 | modf", "[\n  0.75,\n  3\n]\n")]
    [InlineData("0 - 3.75 | modf", "[\n  -0.75,\n  -3\n]\n")]
    [InlineData("6.5 | frexp", "[\n  0.8125,\n  3\n]\n")]
    [InlineData("0 | frexp", "[\n  0,\n  0\n]\n")]
    [InlineData("fma(2;3;4)", "10\n")]
    [InlineData("fma(0 - 2;3;0 - 4)", "-10\n")]
[InlineData("[nan % 1, 1 % nan | isnan]", "[\n  true,\n  true\n]\n")]
    [InlineData("25 % 7", "4\n")]
    [InlineData("49732 % 472", "172\n")]
    [InlineData("[(infinite, -infinite) % (1, -1, infinite)]", "[\n  0,\n  0,\n  0,\n  0,\n  0,\n  -1\n]\n")]
    [InlineData("[-7,-6,-5,-4,-3,-2,-1,0,1,2,3,4,5,6,7] | [.[] % 7]", "[\n  0,\n  -6,\n  -5,\n  -4,\n  -3,\n  -2,\n  -1,\n  0,\n  1,\n  2,\n  3,\n  4,\n  5,\n  6,\n  0\n]\n")]
    public async Task Jq_MathRemainder(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    // Gamma values use a 1e-9 bound and erf values 1e-6 for the managed implementations.
    [InlineData("5 | tgamma | . - 24 | abs < 1e-9", "true\n")]
    [InlineData("0.5 | tgamma | . - 1.7724538509055160 | abs < 1e-9", "true\n")]
    [InlineData("0 - 0.5 | tgamma | . + 3.544907701811032 | abs < 1e-9", "true\n")]
    [InlineData("0 | tgamma", "1.7976931348623157E+308\n")]
    [InlineData("0 - 1 | tgamma", "null\n")]
    [InlineData("200 | tgamma", "1.7976931348623157E+308\n")]
    [InlineData("10 | lgamma | . - 12.801827480081469 | abs < 1e-9", "true\n")]
    [InlineData("0.5 | lgamma | . - 0.5723649429247001 | abs < 1e-9", "true\n")]
    [InlineData("0 | lgamma", "1.7976931348623157E+308\n")]
    [InlineData("0 - 1 | lgamma", "1.7976931348623157E+308\n")]
    [InlineData("0.5 | lgamma_r | .[0] - 0.5723649429247001 | abs < 1e-9", "true\n")]
    [InlineData("0.5 | lgamma_r | .[1]", "1\n")]
    [InlineData("0 - 0.5 | lgamma_r | .[0] - 1.2655121234846454 | abs < 1e-9", "true\n")]
    [InlineData("0 - 0.5 | lgamma_r | .[1]", "-1\n")]
    [InlineData("[range(-99/2;99/2;1)] as $orig | [$orig[]|pow(2;.)|log2] as $back | ($orig|keys)[]|. as $k | (($orig|.[$k])-($back|.[$k]))|if . < 0 then . * -1 else . end|select(.>.00005)", "")]
    [InlineData("0 | erf", "0\n")]
    [InlineData("1 | erf | . - 0.8427007929497149 | abs < 1e-6", "true\n")]
    [InlineData("0 - 1 | erf | . + 0.8427007929497149 | abs < 1e-6", "true\n")]
    [InlineData("0 | erfc", "1\n")]
    [InlineData("1 | erfc | . - 0.15729920705028513 | abs < 1e-6", "true\n")]
    [InlineData("0 | j0", "1\n")]
    [InlineData("0 | j1", "0\n")]
    [InlineData("0 | y0", "-1.7976931348623157E+308\n")]
    [InlineData("0 | y1", "-1.7976931348623157E+308\n")]
    [InlineData("jn(0; 0)", "1\n")]
    [InlineData("jn(2; 0)", "0\n")]
    [InlineData("yn(2; 0)", "null\n")]
    [InlineData("5 | j0 | . - -0.17759677131433851 | abs < 1e-12", "true\n")]
    [InlineData("2 | j1 | . - 0.5767248077568734 | abs < 1e-12", "true\n")]
    [InlineData("1 | y0 | . - 0.08825696421567694 | abs < 1e-12", "true\n")]
    [InlineData("30 | y1 | . - 0.08442557066174712 | abs < 1e-12", "true\n")]
    [InlineData("30 | j0 | . - -0.08636798358104031 | abs < 1e-12", "true\n")]
    [InlineData("jn(2; 10) | . - 0.25463031368512057 | abs < 1e-12", "true\n")]
    [InlineData("jn(10; 0.5) | . - 2.6131773608228023e-13 | abs < 1e-12", "true\n")]
    [InlineData("jn(100; 10) | . - 6.597316064155383e-89 | abs < 1e-12", "true\n")]
    [InlineData("yn(2; 10) | . - -0.005868082442208815 | abs < 1e-12", "true\n")]
    [InlineData("jn(2.7; 0.5) == jn(2; 0.5)", "true\n")]
    [InlineData("jn(-1; 0.5) | . - -0.24226845767487387 | abs < 1e-12", "true\n")]
    [InlineData("jn(2; -1) | . - 0.11490348493190047 | abs < 1e-12", "true\n")]
    [InlineData("yn(2; -1)", "null\n")]
    [InlineData("nan | j0", "null\n")]
    [InlineData("infinite | y0", "null\n")]
    [InlineData("jn(1e30; 0.5)", "0\n")]
    [InlineData("yn(1e30; 0.5)", "-1.7976931348623157E+308\n")]
    [InlineData("jn(nan; 0.5)", "null\n")]
    [InlineData("yn(nan; 0.5)", "null\n")]
    public async Task Jq_MathSpecial(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"x\" | tgamma", "string (\"x\") number required")]
    [InlineData("fmax(1;\"x\")", "string (\"x\") number required")]
    [InlineData("\"x\" | modf", "string (\"x\") number required")]
    [InlineData("fma(1;2;\"x\")", "string (\"x\") number required")]
    [InlineData("jn(\"a\"; 0.5)", "string (\"a\") number required")]
    [InlineData("jn(2; \"a\")", "string (\"a\") number required")]
    [InlineData("\"a\" | y1", "string (\"a\") number required")]
    public async Task Jq_MathSpecialFailures(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("tgamma(1)", "expects no arguments")]
    [InlineData("fmax(1)", "fmax expects 2 arguments")]
    [InlineData("fma(1;2)", "fma expects 3 arguments")]
    public async Task Jq_MathSpecialArityIsCompileError(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
}
