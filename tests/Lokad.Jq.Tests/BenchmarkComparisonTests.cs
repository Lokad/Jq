using System.Runtime.InteropServices;
using System.Text;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed class BenchmarkComparisonTests
{
    [Theory]
    [InlineData("7\n8\n", "7\n8\n", true)]
    [InlineData("7\n8\n", "8\n7\n", false)]
    [InlineData("7\n", "7\n8\n", false)]
    [InlineData("{\"a\":1,\"b\":null}", "{\"b\":null,\"a\":1.0}", true)]
    [InlineData("9007199254740992", "9007199254740993", false)]
    [InlineData("1.00000000000000000000000000001", "1.00000000000000000000000000002", false)]
    [InlineData("1e100000", "10e99999", true)]
    [InlineData("\"🚀\"", "\"\\ud83d\\ude80\"", true)]
    [InlineData("null", "false", false)]
    [InlineData("{\"a\":1,\"a\":1}", "{\"a\":1,\"a\":1}", false)]
    public void ValueComparisonPreservesStreamAndNumericIdentity(string left, string right, bool expected)
    {
        Assert.Equal(expected, OutputComparison.Equivalent(Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right), ComparisonKind.JsonValues));
    }

    [Fact]
    public void RenderingComparisonRejectsEquivalentButDifferentSpelling()
    {
        Assert.False(OutputComparison.Equivalent("1\n"u8.ToArray(), "1.0\n"u8.ToArray(), ComparisonKind.Bytes));
    }

    [Fact]
    public async Task CatalogIsDeterministicAndAllSizesCompleteUnderDefaultLimits()
    {
        var first = WorkloadCatalog.Create();
        var second = WorkloadCatalog.Create();
        Assert.Equal(38, first.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Arguments, second[i].Arguments);
            Assert.Equal(first[i].Input, second[i].Input);
            var result = await BenchmarkExecution.RunAsync(first[i].Arguments, first[i].Input, true, CancellationToken.None);
            Assert.True(result.IsSuccess, first[i].Name + ": " + Encoding.UTF8.GetString(result.Error));
            if (first[i].Name != "startup-empty") Assert.NotEmpty(result.Output);
        }
    }

    [Fact]
    public async Task SubprocessDrainsBothPipesAndPreservesBytesAndStatus()
    {
        byte[] input = Encoding.UTF8.GetBytes(new string('x', 200_000) + "é🚀");
        var result = await FixtureAsync("pipes", input, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(input, result.Output);
        Assert.Equal("diagnostic"u8.ToArray(), result.Error);
        Assert.Equal(OutputDigest.FromBytes(input), result.Digest);
    }

    [Fact]
    public async Task SubprocessDeadlineKillsAndReapsChild()
    {
        await Assert.ThrowsAsync<TimeoutException>(() =>
            FixtureAsync("wait", [], TimeSpan.FromMilliseconds(300), CancellationToken.None));
    }

    [Fact]
    public async Task SubprocessCancellationRemainsDistinctFromTimeout()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FixtureAsync("wait", [], TimeSpan.FromSeconds(30), cancellation.Token));
    }

    [Fact]
    public async Task OutputBoundFailureStopsPumpsAndReapsTheChild()
    {
        await Assert.ThrowsAsync<IOException>(() =>
            FixtureAsync("overflow", [], TimeSpan.FromSeconds(30), CancellationToken.None));
    }

    private static Task<ExecutionResult> FixtureAsync(string mode, byte[] input, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string executable = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),
            "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        return ReferenceProcess.RunAsync(executable,
            [typeof(CommandBenchmarks).Assembly.Location, "--process-fixture", mode], input, true, timeout, cancellationToken);
    }
}
