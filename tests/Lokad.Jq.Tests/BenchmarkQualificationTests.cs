using Lokad.Jq.Benchmarking;
using System.Text.Json;

namespace Lokad.Jq.Tests;

public sealed class BenchmarkQualificationTests
{
    [Fact]
    public void PairedRatioHasReproducibleConfidenceBounds()
    {
        var samples = Pairs(11, 40, 80);
        var result = PairedStatistics.Summarize(samples);
        Assert.Equal("Qualified", result.Status);
        Assert.Equal(2, result.Ratio, 12);
        Assert.Equal(2, result.RatioLow, 12);
        Assert.Equal(2, result.RatioHigh, 12);
        Assert.Equal("Lokad.Jq faster", result.Interpretation);
        Assert.Equal(result, PairedStatistics.Summarize(samples));
    }

    [Fact]
    public void EqualResultsDoNotClaimAWinner()
    {
        var result = PairedStatistics.Summarize(Pairs(11, 40, 40));
        Assert.Equal("Qualified", result.Status);
        Assert.Equal("Inconclusive", result.Interpretation);
    }

    [Fact]
    public void DifferentBatchCountsAreNormalizedPerInvocation()
    {
        var samples = Enumerable.Range(0, 11).Select(i => new PairedSample(i % 2 == 0, 2, 4, 40, 80)).ToArray();
        var result = PairedStatistics.Summarize(samples);
        Assert.Equal(1, result.Ratio);
        Assert.Equal(20, result.LibraryMedianMs);
        Assert.Equal(20, result.ReferenceMedianMs);
    }

    [Fact]
    public void VariablePairsProduceBoundsAroundTheirMedian()
    {
        var samples = Enumerable.Range(0, 11).Select(i => new PairedSample(i % 2 == 0, 1, 1, 40, 79 + i * .2)).ToArray();
        var result = PairedStatistics.Summarize(samples);
        Assert.Equal("Qualified", result.Status);
        Assert.Equal(2, result.Ratio, 12);
        Assert.InRange(result.RatioLow, 1.975, 2);
        Assert.InRange(result.RatioHigh, 2, 2.025);
        Assert.Equal(result, PairedStatistics.Summarize(samples));
    }

    [Fact]
    public void TooFewShortOrInvalidSamplesAreUnqualified()
    {
        foreach (var samples in new[] { Pairs(8, 40, 80), Pairs(11, 10, 80), Pairs(11, double.NaN, 80) })
        {
            var result = PairedStatistics.Summarize(samples);
            Assert.Equal("Unqualified", result.Status);
            Assert.Equal(0, result.Ratio);
            Assert.True(double.IsFinite(result.LibraryMedianMs));
        }
    }

    [Fact]
    public void OrderEffectsAndUnstableLanesCannotBecomeWins()
    {
        var order = Enumerable.Range(0, 11).Select(i => new PairedSample(i % 2 == 0, 1, 1, 40, i % 2 == 0 ? 80 : 60)).ToArray();
        Assert.Contains("order", PairedStatistics.Summarize(order).Reason, StringComparison.Ordinal);
        var spread = Enumerable.Range(0, 11).Select(i => new PairedSample(i % 2 == 0, 1, 1, 40 + i * 4, 80 + i * 8)).ToArray();
        Assert.Equal("Unqualified", PairedStatistics.Summarize(spread).Status);
    }

    [Theory]
    [InlineData(1, 2, true)]
    [InlineData(4, 4, false)]
    [InlineData(1, 6, false)]
    [InlineData(double.NaN, 1, false)]
    public void QuietGateRejectsContendedOrUnknownMachines(double a, double b, bool expected)
    {
        Assert.Equal(expected, QuietCheck.Evaluate([a, b]).IsQuiet);
        Assert.False(QuietCheck.Evaluate([]).IsQuiet);
    }

    [Fact]
    public void ReportsHideRatiosForVerificationPauseMismatchAndUnqualifiedRows()
    {
        var workload = WorkloadCatalog.Create()[0];
        var empty = OutputDigest.FromBytes([]);
        var verified = new CaseVerification(workload.Name, workload.Scale, workload.Size, "Equivalent", "", empty, empty);
        var timing = new QualificationResult(PairedStatistics.Summarize(Pairs(11, 40, 80)), Pairs(11, 40, 80), [], 32, 32);
        var host = new HostProvenance(new string('a', 40), true, "10.0", ".NET 10", "test OS", "X64", "test CPU", 4, "build", "build", "digest");
        var reference = new ReferenceIdentity("jq-1.8.2", "digest", "test configuration");
        var row = new ComparisonRow(BenchmarkReport.Describe(workload), verified, timing);
        var report = BenchmarkReport.Create("qualify", host, reference, QuietCheck.Evaluate([1, 1]), 11, 1, true, [row]);
        var restored = Assert.IsType<ComparisonReport>(JsonSerializer.Deserialize<ComparisonReport>(
            JsonSerializer.Serialize(report, ComparisonRunner.JsonOptions), ComparisonRunner.JsonOptions));
        Assert.Equal(BenchmarkReport.Render(report), BenchmarkReport.Render(restored));
        Assert.Contains("2.00 [2.00, 2.00]", BenchmarkReport.Render(report), StringComparison.Ordinal);
        Assert.DoesNotContain("2.00 [", BenchmarkReport.Render(report with { Mode = "verify" }), StringComparison.Ordinal);
        Assert.DoesNotContain("2.00 [", BenchmarkReport.Render(report with { Mode = "paused" }), StringComparison.Ordinal);
        Assert.DoesNotContain("2.00 [", BenchmarkReport.Render(report with { Complete = false }), StringComparison.Ordinal);
        Assert.DoesNotContain("2.00 [", BenchmarkReport.Render(report with { Cases = [row with { Verification = verified with { Status = "Mismatch" } }] }), StringComparison.Ordinal);
        Assert.DoesNotContain("2.00 [", BenchmarkReport.Render(report with { Cases = [row with { Timing = timing with { Summary = timing.Summary with { Status = "Unqualified" } } }] }), StringComparison.Ordinal);
    }

    [Fact]
    public Task BoundedStressChecksHonorQuotasAndCompleteOutputPrefixes() => StressChecks.RunAsync(CancellationToken.None);

    [Theory]
    [InlineData("--jq", false)]
    [InlineData("--unknown value", false)]
    [InlineData("--jq reference --jq other", false)]
    [InlineData("--jq reference --mode qualify --pairs 11", true)]
    public void ReferenceOptionsRequireExplicitPathAndRejectMalformedFlags(string command, bool expected)
    {
        Assert.Equal(expected, ComparisonRunner.TryOptions(command.Split(' '), out _, out _));
    }

    private static PairedSample[] Pairs(int count, double libraryMs, double referenceMs) =>
        Enumerable.Range(0, count).Select(i => new PairedSample(i % 2 == 0, 1, 1, libraryMs, referenceMs)).ToArray();
}
