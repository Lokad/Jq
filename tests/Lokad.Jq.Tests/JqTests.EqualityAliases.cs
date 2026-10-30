using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("[nan] as $x | [$x==$x,$x!=$x]", "[true,false]\n")]
    [InlineData("{a:nan} as $x | $x==$x", "true\n")]
    [InlineData("[nan]==[nan]", "false\n")]
    [InlineData("nan as $x | $x==$x", "false\n")]
    [InlineData("[nan] as $x | $x | contains($x)", "false\n")]
    [InlineData("null|setpath([range(80)|0];0) as $x | $x==$x", "true\n")]
    public async Task Jq_EqualityRecognizesOnlySharedContainerAliases(string source, string expected)
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", source], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_SharedEqualityStillObservesCancellationAndDistinctTreeDepth()
    {
        JqValue left = NativeFixtures.Create(0), right = NativeFixtures.Create(0);
        for (int index = 0; index < 80; index++)
        {
            left = new JqArray(left);
            right = new JqArray(right);
        }
        using var cancellation = new CancellationTokenSource();
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, cancellation.Token);
        var runtime = new JqRuntime(budget);
        Assert.True(runtime.JsonEquals(left, left));
        Assert.Throws<JqQuotaException>(() => runtime.JsonEquals(left, right));
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => runtime.JsonEquals(left, left));
    }
}
