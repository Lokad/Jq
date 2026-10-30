using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("6 | erfc > 0", "true\n")]
    [InlineData("7 | erfc > 0", "true\n")]
    [InlineData("8 | erfc > 0", "true\n")]
    [InlineData("10 | erfc > 0", "true\n")]
    [InlineData("(-1*0) as $x | [copysign(1;$x|log1p),copysign(1;$x|expm1),copysign(1;$x|modf|.[0]),copysign(1;$x|modf|.[1])]", "[-1,-1,-1,-1]\n")]
    [InlineData("0 as $x | [copysign(1;$x|log1p),copysign(1;$x|expm1),copysign(1;$x|modf|.[0]),copysign(1;$x|modf|.[1])]", "[1,1,1,1]\n")]
    [InlineData("-2 | modf | .[0] | copysign(1;.)", "-1\n")]
    [InlineData("-1 | modf | .[0] | copysign(1;.)", "-1\n")]
    [InlineData("1 | modf | .[0] | copysign(1;.)", "1\n")]
    [InlineData("2 | modf | .[0] | copysign(1;.)", "1\n")]
    [InlineData("1e-20 | [expm1 != 0, log1p != 0]", "[true,true]\n")]
    [InlineData("-1e-20 | [expm1 != 0, log1p != 0]", "[true,true]\n")]
    [InlineData("infinite | [(expm1|isinfinite),(log1p|isinfinite)]", "[true,true]\n")]
    [InlineData("(-infinite) | [expm1 == -1,erfc == 2]", "[true,true]\n")]
    [InlineData("nan | [(expm1|isnan),(log1p|isnan)]", "[true,true]\n")]
    [InlineData("[-2,-1,0,1,2] | map([erfc >= 0, erfc <= 2])", "[[true,true],[true,true],[true,true],[true,true],[true,true]]\n")]
    public async Task Jq_MathRetainsComputedSignsAndRepresentableTails(string filter, string expected)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("log1p")]
    [InlineData("expm1")]
    public void Jq_MathTinyInputsAndZeroSignsDoNotRoundAway(string name)
    {
        using var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(Utf8RegexAdapter.Instance, CancellationToken.None));
        foreach (double value in new[] { -0.0, 0.0, double.Epsilon, -double.Epsilon, 1e-20, -1e-20 })
        {
            Assert.True(JqMath.TryEvaluate(context, name, [], NativeFixtures.Create(value), out JqValue? result));
            Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(JqRuntime.Number(result)));
        }
    }

    [Theory]
    [InlineData(6.0, 2.1519736712498913e-17)]
    [InlineData(7.0, 4.1838256077794136e-23)]
    [InlineData(8.0, 1.1224297172982929e-29)]
    [InlineData(10.0, 2.088487583762545e-45)]
    public void Jq_MathErfcTailKeepsTheExistingApproximationWithoutSubtractionLoss(double value, double reference)
    {
        using var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.True(JqMath.TryEvaluate(context, "erfc", [], NativeFixtures.Create(value), out JqValue? result));
        // The existing approximation is deliberately qualified, not libm exact.
        Assert.InRange(JqRuntime.Number(result), reference * 0.9, reference * 1.1);
    }
}
