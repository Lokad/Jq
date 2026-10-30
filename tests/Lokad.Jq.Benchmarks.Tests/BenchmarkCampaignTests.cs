using System.Security.Cryptography;
using System.Text.Json;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed class BenchmarkCampaignTests
{
    [Fact]
    public void CampaignKeepsFirstQualifiedAndUnqualifiedResults()
    {
        WithDirectory(directory =>
        {
            var catalog = Catalog();
            var first = Attempt(catalog, 0, 10);
            Write(directory, "case-000000.json", first);
            Write(directory, "case-000001.json", Attempt(catalog, 0, 40));
            Write(directory, "case-000002.json", Attempt(catalog, 1, 40));
            var before = File.ReadAllBytes(Path.Combine(directory, "case-000000.json"));
            var result = ComparisonCampaign.Load(directory, catalog);
            Assert.True(result.Complete);
            Assert.Equal("Unqualified", result.Cases[0].Timing.Summary.Status);
            Assert.Equal("Qualified", result.Cases[1].Timing.Summary.Status);
            Assert.Equal(new[] { "case-000000.json", "case-000002.json" }, result.Checkpoints.Select(row => row.File));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(before)), result.Checkpoints[0].Sha256);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory, "case-000000.json")));
            Assert.Equal(JsonSerializer.Serialize(result.Cases[0], ComparisonRunner.JsonOptions),
                JsonSerializer.Serialize(ComparisonCampaign.Load(directory, catalog).Cases[0], ComparisonRunner.JsonOptions));
        });
    }

    [Fact]
    public void CampaignReconstructsAfterPauseAndInterruptedAggregateSave()
    {
        WithDirectory(directory =>
        {
            var catalog = Catalog();
            Write(directory, "verification-000000.json", catalog);
            var unfinished = Attempt(catalog, 0, 40) with { Complete = false, Cases = [] };
            Write(directory, "case-000000.json", unfinished);
            var paused = Attempt(catalog, 0, 40);
            paused = paused with { Mode = "paused", Complete = false, Cases =
                [paused.Cases[0] with { Timing = paused.Cases[0].Timing with { Summary = new("Paused", "busy", 0, 0) } }] };
            Write(directory, "case-000001.json", paused);
            // The case was saved after timing, before the producer's final Complete save.
            Write(directory, "case-000002.json", Attempt(catalog, 0, 40) with { Complete = false });
            File.WriteAllText(Path.Combine(directory, "qualification.json.tmp"), "interrupted");
            var result = ComparisonCampaign.Load(directory, catalog);
            Assert.False(result.Complete);
            Assert.Single(result.Cases);
            Assert.Equal("case-000002.json", Assert.Single(result.Checkpoints).File);
            Write(directory, "case-000003.json", Attempt(catalog, 1, 40));
            Assert.True(ComparisonCampaign.Load(directory, catalog).Complete);
        });
    }

    [Theory]
    [InlineData("source")]
    [InlineData("library")]
    [InlineData("runtime")]
    [InlineData("provider")]
    [InlineData("reference")]
    [InlineData("pairs")]
    [InlineData("workload")]
    [InlineData("output")]
    [InlineData("samples")]
    [InlineData("quiet")]
    [InlineData("pair-quiet")]
    [InlineData("timeout")]
    public void CampaignRejectsMixedOrIncompleteEvidence(string mutation)
    {
        WithDirectory(directory =>
        {
            var catalog = Catalog();
            var report = Attempt(catalog, 0, 40);
            var row = report.Cases[0];
            report = mutation switch
            {
                "source" => report with { Host = report.Host with { Commit = new string('b', 40) } },
                "library" => report with { Host = report.Host with { LibraryAssemblySha256 = "different" } },
                "runtime" => report with { Host = report.Host with { Runtime = "different" } },
                "provider" => report with { Host = report.Host with { RegexProvider = "different" } },
                "reference" => report with { Reference = report.Reference with { Sha256 = "different" } },
                "pairs" => report with { RequestedPairs = 13 },
                "workload" => report with { Cases = [row with { Workload = row.Workload with { Arguments = ["different"] } }] },
                "output" => report with { Cases = [row with { Verification = row.Verification with { LibraryOutput = OutputDigest.FromBytes([1]) } }] },
                "samples" => report with { Cases = [row with { Timing = row.Timing with { Samples = row.Timing.Samples[..8] } }] },
                "quiet" => report with { MachineGate = QuietCheck.Evaluate([80]) },
                "pair-quiet" => report with { Cases = [row with { Timing = row.Timing with
                    { QuietChecks = Enumerable.Repeat(new QuietCheck(true, "stale flag", [80]), 11).ToArray() } }] },
                _ => report with { Cases = [row with { Timing = row.Timing with { Summary = new("Timeout", "expired", 0, 0) } }] },
            };
            Write(directory, "case-000000.json", report);
            Assert.Throws<InvalidOperationException>(() => ComparisonCampaign.Load(directory, catalog));
        });
    }

    private static ComparisonReport Catalog()
    {
        string commit = new('a', 40);
        var host = new HostProvenance(commit, true, "11.0", ".NET 11", "test OS", "X64", "test CPU", 4,
            "0.3.0+" + commit, "0.3.0+" + commit, "benchmark", "library", "provider");
        var digest = OutputDigest.FromBytes([]);
        var rows = WorkloadCatalog.Create().Take(2).Select(workload => new ComparisonRow(BenchmarkReport.Describe(workload),
            new(workload.Name, workload.Scale, workload.Size, "Equivalent", "", digest, digest),
            new(new("NotMeasured", "verification", 0, 0), [], [], 0, 0))).ToArray();
        return BenchmarkReport.Create("verify", host, new("jq-1.8.2", "reference", "configuration"),
            QuietCheck.Evaluate([]), 11, 2, true, rows);
    }

    private static ComparisonReport Attempt(ComparisonReport catalog, int index, double libraryMs)
    {
        var samples = Enumerable.Range(0, 11).Select(i => new PairedSample(i % 2 == 0, 1, 1, libraryMs, 80)).ToArray();
        var timing = new QualificationResult(PairedStatistics.Summarize(samples), samples,
            Enumerable.Range(0, 11).Select(_ => QuietCheck.Evaluate([1])).ToArray(), 96, 96);
        return BenchmarkReport.Create("qualify", catalog.Host, catalog.Reference, QuietCheck.Evaluate([1, 1, 1, 1, 1]),
            11, 1, true, [catalog.Cases[index] with { Timing = timing }]);
    }

    private static void Write(string directory, string name, ComparisonReport report) =>
        File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(report, ComparisonRunner.JsonOptions));

    private static void WithDirectory(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "jq-campaign-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally { Directory.Delete(directory, true); }
    }
}
