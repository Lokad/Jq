using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public void Jq_NumberConstructionRetainsExactBitsAndOnlyUsesRepresentableIntegralStorage()
    {
        double upper = Math.ScaleB(1, 63);
        var cases = new (double Value, bool Integral)[]
        {
            (-0.0, false), (0, true), (-1, true), (1, true), (0.5, false),
            (Math.BitDecrement(upper), true), (upper, false), (Math.BitIncrement(upper), false),
            (Math.BitDecrement(-upper), false), (-upper, true), (Math.BitIncrement(-upper), true),
            (double.PositiveInfinity, false), (double.NegativeInfinity, false), (double.NaN, false)
        };
        foreach (var item in cases)
        {
            JqValue result = JqRuntime.CreateNumber(item.Value);
            Assert.Equal(BitConverter.DoubleToInt64Bits(item.Value), BitConverter.DoubleToInt64Bits(JqRuntime.Number(result)));
            Assert.Equal(item.Integral, Assert.IsAssignableFrom<JqScalar>(result).TryGetValue<long>(out _));
        }
    }

    [Theory]
    [InlineData("first(range(copysign(0;-1);1))|copysign(1;.)", "-1\n")]
    [InlineData("first(range(copysign(0;1);1))|copysign(1;.)", "1\n")]
    [InlineData("first(range(copysign(0;-1);-1;-1))|copysign(1;.)", "-1\n")]
    [InlineData("first(range(copysign(0;-1);1;1))|copysign(1;.)", "-1\n")]
    [InlineData("copysign(0;-1)|first(path(range(.;1)))", "[]\n")]
    [InlineData("[1,2]|[path(.[copysign(0;-1):])]|.[0][0].start|copysign(1;.)", "-1\n")]
    [InlineData("[1,2]|[path(.[0:copysign(0;-1)])]|.[0][0].end|copysign(1;.)", "-1\n")]
    [InlineData("pow(2;63) as $x|((first(range($x;infinite))|tostring)==($x|tostring))", "true\n")]
    [InlineData("[1,2]|pow(2;63) as $x|((path(.[0:$x])|.[0].end|tostring)==($x|tostring))", "true\n")]
    [InlineData("range(0;3)|tostring", "\"0\"\n\"1\"\n\"2\"\n")]
    public async Task Jq_NumberConstructionPreservesRangeAndSliceRepresentations(string filter, string expected)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_NumberConstructionRangeCancellationRetainsEarlierResultsAndClosesInput()
    {
        using var cancellation = new CancellationTokenSource();
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/input", "null\n");
        host.BeforeByteAppend = () => { if (host.AppendCallCount == 2) cancellation.Cancel(); };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "range(copysign(0;-1);infinite)|copysign(1;.)", "/input")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, cancellation.Token));
        Assert.Equal("-1\n1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_NumberConstructionRangesKeepOuterStreamsBoundedAndStopOnClosure(bool close)
    {
        const int records = 2500;
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem { AppendRemainsOpen = !close };
        host.AddFile("/input", string.Concat(Enumerable.Repeat("-0\n", records)));
        host.AddFile("/later", "");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "first(range(.;1))|copysign(1;.)", "/input", "/later")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("-1\n", close ? 1 : records)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(close ? 1 : 2, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }
}
