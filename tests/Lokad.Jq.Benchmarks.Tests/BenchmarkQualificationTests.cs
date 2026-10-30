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
        Assert.Equal(.5, result.PairedMedianLibraryToJqLow, 12);
        Assert.Equal(.5, result.PairedMedianLibraryToJqHigh, 12);
        Assert.Equal(result, PairedStatistics.Summarize(samples));
    }

    [Fact]
    public void EqualResultsHaveUnitQualificationBounds()
    {
        var result = PairedStatistics.Summarize(Pairs(11, 40, 40));
        Assert.Equal("Qualified", result.Status);
        Assert.Equal(1, result.PairedMedianLibraryToJqLow);
        Assert.Equal(1, result.PairedMedianLibraryToJqHigh);
    }

    [Fact]
    public void DifferentBatchCountsAreNormalizedPerInvocation()
    {
        var samples = Enumerable.Range(0, 11).Select(i => new PairedSample(i % 2 == 0, 2, 4, 40, 80)).ToArray();
        var result = PairedStatistics.Summarize(samples);
        Assert.Equal(1, result.PairedMedianLibraryToJqLow);
        Assert.Equal(1, result.PairedMedianLibraryToJqHigh);
        Assert.All(samples, sample => { Assert.Equal(20, sample.LibraryMs); Assert.Equal(20, sample.ReferenceMs); });
    }

    [Fact]
    public void VariablePairsProduceBoundsAroundTheirMedian()
    {
        var samples = Enumerable.Range(0, 11).Select(i => new PairedSample(i % 2 == 0, 1, 1, 40, 79 + i * .2)).ToArray();
        var result = PairedStatistics.Summarize(samples);
        Assert.Equal("Qualified", result.Status);
        Assert.InRange(result.PairedMedianLibraryToJqLow, 1 / 2.025, .5);
        Assert.InRange(result.PairedMedianLibraryToJqHigh, .5, 1 / 1.975);
        Assert.Equal(result, PairedStatistics.Summarize(samples));
    }

    [Fact]
    public void TooFewShortOrInvalidSamplesAreUnqualified()
    {
        foreach (var samples in new[] { Pairs(8, 40, 80), Pairs(11, 10, 80), Pairs(11, double.NaN, 80) })
        {
            var result = PairedStatistics.Summarize(samples);
            Assert.Equal("Unqualified", result.Status);
            Assert.Equal(0, result.PairedMedianLibraryToJqLow);
            Assert.Equal(0, result.PairedMedianLibraryToJqHigh);
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
        var host = new HostProvenance(new string('a', 40), true, "11.0", ".NET 11", "test OS", "X64", "test CPU", 4, "build", "build", "digest", "library digest", "test provider");
        var reference = new ReferenceIdentity("jq-1.8.2", "digest", "test configuration");
        var row = new ComparisonRow(BenchmarkReport.Describe(workload), verified, timing);
        var report = BenchmarkReport.Create("qualify", host, reference, QuietCheck.Evaluate([1, 1]), 11, 1, true, [row]);
        var restored = Assert.IsType<ComparisonReport>(JsonSerializer.Deserialize<ComparisonReport>(
            JsonSerializer.Serialize(report, ComparisonRunner.JsonOptions), ComparisonRunner.JsonOptions));
        Assert.Equal(BenchmarkReport.Render(report), BenchmarkReport.Render(restored));
        Assert.Contains("| 40.000 | 80.000 | 0.500 |", BenchmarkReport.Render(report), StringComparison.Ordinal);
        string document = "# Benchmarks\n\n" + BenchmarkReport.BeginMarker + "\nold table\n"
            + BenchmarkReport.EndMarker + "\n\n## Managed allocations\n\n| Allocations |\n| 123 |\n\n"
            + BenchmarkReport.ProvenanceBeginMarker + "\nold provenance\n" + BenchmarkReport.ProvenanceEndMarker;
        string rendered = BenchmarkReport.RenderDocument(document, report);
        Assert.True(rendered.IndexOf("| Case / scale", StringComparison.Ordinal)
            < rendered.IndexOf("| Allocations |", StringComparison.Ordinal));
        Assert.True(rendered.IndexOf("| Allocations |", StringComparison.Ordinal)
            < rendered.IndexOf("Mode:", StringComparison.Ordinal));
        Assert.Contains("| 123 |", rendered, StringComparison.Ordinal);
        Assert.Equal(rendered, BenchmarkReport.RenderDocument(rendered, report));
        Assert.DoesNotContain("old provenance", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("0.500", BenchmarkReport.Render(report with { Mode = "verify" }), StringComparison.Ordinal);
        Assert.DoesNotContain("0.500", BenchmarkReport.Render(report with { Mode = "paused" }), StringComparison.Ordinal);
        Assert.DoesNotContain("0.500", BenchmarkReport.Render(report with { Complete = false }), StringComparison.Ordinal);
        Assert.DoesNotContain("0.500", BenchmarkReport.Render(report with { Cases = [row with { Verification = verified with { Status = "Mismatch" } }] }), StringComparison.Ordinal);
        Assert.DoesNotContain("0.500", BenchmarkReport.Render(report with { Cases = [row with { Timing = timing with { Summary = timing.Summary with { Status = "Unqualified" } } }] }), StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsUseMeanInvocationTimesAndSortDescendingBeforeRounding()
    {
        var workload = WorkloadCatalog.Create()[0];
        var empty = OutputDigest.FromBytes([]);
        var verified = new CaseVerification(workload.Name, workload.Scale, workload.Size, "Equivalent", "", empty, empty);
        var samples = Enumerable.Range(0, 11)
            .Select(i => new PairedSample(i % 2 == 0, 2, 4, i == 10 ? 96 : 80, 320)).ToArray();
        var summary = PairedStatistics.Summarize(samples);
        Assert.Equal("Qualified", summary.Status);
        var row = new ComparisonRow(BenchmarkReport.Describe(workload) with { Name = "mean" }, verified,
            new QualificationResult(summary, samples, [], 32, 32));
        var first = row with { Workload = row.Workload with { Name = "z-first" },
            Timing = row.Timing with { Samples = Pairs(11, 40.032, 80) } };
        var second = row with { Workload = row.Workload with { Name = "a-second" },
            Timing = row.Timing with { Samples = Pairs(11, 40.024, 80) } };
        var unqualified = row with { Workload = row.Workload with { Name = "unqualified" },
            Timing = row.Timing with { Samples = [], Summary = summary with { Status = "Unqualified", Reason = "Too short." } } };
        var host = new HostProvenance(new string('a', 40), true, "11.0", ".NET 11", "test OS", "X64", "test CPU", 4, "build", "build", "digest", "library digest", "test provider");
        var report = BenchmarkReport.Create("qualify", host, new("jq-1.8.2", "digest", "test configuration"),
            QuietCheck.Evaluate([1, 1]), 11, 4, true, [second, unqualified, row, first]);

        string rendered = BenchmarkReport.Render(report);
        string[] lines = rendered.Split('\n').Where(line => line.Contains("| Qualified |", StringComparison.Ordinal)
            || line.Contains("| Unqualified |", StringComparison.Ordinal)).ToArray();
        Assert.Collection(lines,
            line => Assert.StartsWith("| mean/", line),
            line => Assert.StartsWith("| z-first/", line),
            line => Assert.StartsWith("| a-second/", line),
            line => Assert.StartsWith("| unqualified/", line));
        Assert.Contains("| 40.727 | 80.000 | 0.509 |", rendered, StringComparison.Ordinal);
        Assert.Contains("| — | — | — |", lines[3], StringComparison.Ordinal);
        Assert.Contains("Too short.", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Interpretation", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("95% CI", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public Task BoundedStressChecksHonorQuotasAndCompleteOutputPrefixes() => StressChecks.RunAsync(CancellationToken.None);

    [Fact]
    public void AllocationReportsPublishAbsoluteMeasurementsAndRejectIncompleteEvidence()
    {
        string commit = new('a', 40);
        var host = new HostProvenance(commit, true, "11.0", ".NET 11", "test OS", "X64", "test CPU", 4,
            "0.3.0+" + commit, "0.3.0+" + commit, "digest", "library digest", "test provider");
        var workload = WorkloadCatalog.Create().Single(item => item.Name == "identity-compact" && item.Scale == "large");
        var row = new AllocationRow(BenchmarkReport.Describe(workload), OutputDigest.FromBytes([]),
            [new(100, 1, 0, 0), new(200, 0, 1, 0), new(1000, 0, 0, 1)]);
        var report = new AllocationReport(host, "digest", false, "test contract", true, [row]);
        string rendered = BenchmarkReport.RenderAllocations(report);
        Assert.Contains("| identity-compact/large | 200 | 1 / 1 / 1 |", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Before", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("After", rendered, StringComparison.Ordinal);
        string document = "# Benchmarks\n\nTiming stays here.\n\n" + BenchmarkReport.AllocationBeginMarker
            + "\nold allocations\n" + BenchmarkReport.AllocationEndMarker;
        string updated = BenchmarkReport.RenderAllocationDocument(document, report);
        Assert.Contains("Timing stays here.", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("old allocations", updated, StringComparison.Ordinal);
        Assert.Equal(updated, BenchmarkReport.RenderAllocationDocument(updated, report));
        Assert.Throws<InvalidOperationException>(() => BenchmarkReport.RenderAllocations(report with { Complete = false }));
        Assert.Throws<InvalidOperationException>(() => BenchmarkReport.RenderAllocations(report with { Host = host with { CleanCheckout = false } }));
        Assert.Throws<InvalidOperationException>(() => BenchmarkReport.RenderAllocations(report with { Host = host with { LibraryBuild = "stale" } }));
        Assert.Throws<InvalidOperationException>(() => BenchmarkReport.RenderAllocations(report with
            { Cases = [row with { Batches = [new(double.NaN, 0, 0, 0), new(1, 0, 0, 0), new(1, 0, 0, 0)] }] }));
    }

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
