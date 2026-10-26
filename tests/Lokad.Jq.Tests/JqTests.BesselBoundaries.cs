using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public void Jq_FirstOrderBesselValuesRetainZeroBitsAndReflectedPoles()
    {
        using var budget = new JqBudget(CancellationToken.None);
        foreach (double zero in new[] { 0.0, -0.0 })
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(zero), BitConverter.DoubleToInt64Bits(JqBessel.J1(zero)));
            foreach (double order in new[] { 1.0, 1.9, -1.0, -1.9 })
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(order < 0 ? -zero : zero),
                    BitConverter.DoubleToInt64Bits(JqBessel.Jn(budget, order, zero)));
                Assert.Equal(order < 0 ? double.PositiveInfinity : double.NegativeInfinity, JqBessel.Yn(budget, order, zero));
            }
        }
        Assert.True(double.IsNaN(JqBessel.Yn(budget, 2, 0)));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(JqBessel.Jn(budget, -3, 0)));
    }

    [Theory]
    [InlineData("copysign(0;-1)|j1|copysign(1;.)", "-1\n")]
    [InlineData("jn(1;copysign(0;-1))|copysign(1;.)", "-1\n")]
    [InlineData("jn(-1;copysign(0;-1))|copysign(1;.)", "1\n")]
    [InlineData("yn(1;0)|[isinfinite,.<0]", "[true,true]\n")]
    [InlineData("yn(1;copysign(0;-1))|[isinfinite,.<0]", "[true,true]\n")]
    [InlineData("yn(-1;0)|[isinfinite,.>0]", "[true,true]\n")]
    [InlineData("yn(-1;copysign(0;-1))|[isinfinite,.>0]", "[true,true]\n")]
    public async Task Jq_FirstOrderBesselBoundariesFollowNativeClassification(string filter, string expected)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_FirstOrderBesselStreamsStayBoundedAndStopOnClosure(bool close)
    {
        const int records = 2500;
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem { AppendRemainsOpen = !close };
        host.AddFile("/input", string.Concat(Enumerable.Repeat("-0\n", records)));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "[jn(1;.)|copysign(1;.), (yn(1;.)|isinfinite)]", "/input")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("[-1,true]\n", close ? 1 : records)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }
}
