using Lokad.Jq.Benchmarking;
namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    // Independent AMOS reference values. Relative error matters for decaying J.
    [InlineData(100000.0, 99000.0, 2.481286891335687e-44, -9.093895741924307e38)]
    [InlineData(100001.0, 99000.99, 2.478925647641752e-44, -9.102466897381321e38)]
    [InlineData(100001.0, 100001.0, 0.009636911888405673, -0.0166916211131535)]
    [InlineData(150000.0, 150000.0, 0.008418646038002991, -0.01458152271677244)]
    [InlineData(1000000.0, 1000000.0, 0.004473073183377776, -0.007747590021617347)]
    public void Jq_BesselRecurrencesPreserveFiniteValuesAtLargeOrders(double order,
        double point, double expectedJ, double expectedY)
    {
        using var budget = new JqBudget(CancellationToken.None);
        Assert.InRange(Math.Abs(JqBessel.Jn(budget, order, point) / expectedJ - 1.0), 0, 2e-8);
        Assert.InRange(Math.Abs(JqBessel.Yn(budget, order, point) / expectedY - 1.0), 0, 2e-8);
    }

    [Fact]
    public void Jq_BesselTinyArgumentsPreserveSubnormalsAndActualOverflow()
    {
        // 100-digit references evaluated at these exact binary input values.
        using var budget = new JqBudget(CancellationToken.None);
        Assert.Equal(3 * double.Epsilon, JqBessel.Jn(budget, 2, 1e-161));
        Assert.Equal(double.Epsilon, JqBessel.Jn(budget, 2, 7e-162));
        Assert.InRange(Math.Abs(JqBessel.Jn(budget, 3, 1e-100) / 2.0833333333333334e-302 - 1.0), 0, 1e-12);
        Assert.Equal(0.0, JqBessel.Jn(budget, 5, 1e-300));
        Assert.InRange(Math.Abs(JqBessel.Y0(double.Epsilon) + 473.9990734230043), 0, 1e-10);
        Assert.InRange(Math.Abs(JqBessel.Y0(1e-320) + 469.15259466535355), 0, 1e-10);
        Assert.Equal(double.NegativeInfinity, JqBessel.Y1(double.Epsilon));
        Assert.Equal(double.NegativeInfinity, JqBessel.Yn(budget, 100001, double.Epsilon));
        Assert.Equal(double.PositiveInfinity, JqBessel.Yn(budget, -100001, double.Epsilon));
    }

    [Fact]
    public void Jq_BesselHugeOrdersRequireAnActualUnderflowProof()
    {
        using var budget = new JqBudget(CancellationToken.None);
        // High-precision bound exponents are about 212 and 1,574,122 respectively.
        // A large order alone cannot justify returning zero near the transition.
        Assert.True(double.IsNaN(JqBessel.Jn(budget, 1e26, Math.BitDecrement(1e26))));
        Assert.Equal(0.0, JqBessel.Jn(budget, 1e30, Math.BitDecrement(1e30)));
    }

    [Theory]
    [InlineData(-3.0, 1e-300, true)]
    [InlineData(3.0, -1e-300, true)]
    [InlineData(-3.0, -1e-300, false)]
    [InlineData(-99999.0, 0.0, true)]
    public void Jq_BesselUnderflowRetainsReflectedZeroSign(double order, double point, bool negative)
    {
        using var budget = new JqBudget(CancellationToken.None);
        double value = JqBessel.Jn(budget, order, point);
        Assert.Equal(0.0, value);
        Assert.Equal(negative, BitConverter.DoubleToInt64Bits(value) < 0);
    }

    [Theory]
    [InlineData("jn(100000.9; 99999) == jn(100000; 99999)", "true\n")]
    [InlineData("jn(100001.9; 100001) == jn(100001; 100001)", "true\n")]
    [InlineData("jn(-3; -1) == jn(3; 1)", "true\n")]
    [InlineData("yn(-3.7; 1) == -yn(3; 1)", "true\n")]
    [InlineData("[jn(1e30; 0.5), yn(1e30; 0.5)]", "[0,-1.7976931348623157E+308]\n")]
    [InlineData("[jn(1e30; 1e30), yn(1e30; 1e30)]", "[null,null]\n")]
    [InlineData("[jn(9223372036854775808; 1e20), yn(9223372036854775808; 1e20)]", "[null,null]\n")]
    [InlineData("[jn(9223372036854774784; 0.5), yn(9223372036854774784; 0.5)]", "[0,-1.7976931348623157E+308]\n")]
    [InlineData("[jn(infinite; 1), yn(infinite; 1)]", "[0,-1.7976931348623157E+308]\n")]
    [InlineData("[jn(nan; 1), yn(nan; 1), jn(2; infinite), yn(2; -1), yn(2; 0)]", "[null,null,null,null,null]\n")]
    public async Task Jq_BesselOrderConversionAndDomains(string filter, string expected)
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("jn(10000000000; 10000000000)")]
    [InlineData("jn(10000000000; 9999999999)")]
    [InlineData("yn(10000000000; 10000000000)")]
    public async Task Jq_CancellationInterruptsActiveBesselWorkAndUnwindsOwnedInputs(string expression)
    {
        using var cancellation = new CancellationTokenSource();
        var prefix = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem { BeforeByteAppend = () => prefix.TrySetResult() };
        host.AddFile("/first", "1\n2\n");
        host.AddFile("/later", "3\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            "\"prefix\", (try " + expression + " catch \"caught\"), \"after\"", "/first", "/later")));
        Task<int> execution = Task.Run(() => command.ExecuteAsync(host, memory, cancellation.Token));
        try
        {
            await prefix.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(10);
            Assert.False(execution.IsCompleted);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally { cancellation.Cancel(); }
        Assert.Equal("prefix\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, host.ReadBytesCallCount);
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_DownstreamClosureStopsBeforeLongBesselWork()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem { AppendRemainsOpen = false };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr",
            "\"prefix\", jn(10000000000; 10000000000)")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, cancellation.Token));
        Assert.Equal("prefix\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, host.AppendCallCount);
        Assert.Equal(0, memory.ReservedBytes);
    }
}
