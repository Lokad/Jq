using System.Security.Cryptography;
using System.Text.Json;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed class BenchmarkGovernorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CountsModeVerifiesWorkersAndRefundsWithoutMeasurementBatches(bool explicitMode)
    {
        string output = Path.Combine("artifacts", "test-governor", Guid.NewGuid() + ".json");
        var arguments = new List<string> { "--case", "tiny", "--workers", "1,4", "--output", output };
        if (explicitMode) arguments.AddRange(["--mode", "counts"]);
        try
        {
            Assert.Equal(0, await GovernorRunner.RunAsync(arguments.ToArray(), CancellationToken.None));
            var report = JsonSerializer.Deserialize<GovernorReport>(await File.ReadAllTextAsync(output), ComparisonRunner.JsonOptions);
            Assert.NotNull(report);
            Assert.Equal("counts", report.Mode);
            Assert.True(report.Complete);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(typeof(Jq).Assembly.Location))), report.LibrarySha256);
            Assert.Equal(new[] { 1, 4 }, report.Cases.Select(row => row.Workers));
            foreach (var row in report.Cases)
            {
                Assert.Equal("tiny", row.Workload.Name);
                Assert.Equal(OutputDigest.FromBytes("1\n"u8.ToArray()), row.Expected);
                Assert.Equal(OutputDigest.FromBytes([]), row.ExpectedError);
                Assert.Empty(row.Batches);
                Assert.Equal(0, row.CountedEndBytes);
                Assert.Equal(0, row.CountedEndScopes);
                Assert.Equal(row.Workers, row.Calls.ScopeCreations);
                Assert.Equal(row.Calls.ScopeCreations, row.Calls.ScopeDisposals);
                Assert.Equal(row.Calls.Reservations, row.Calls.ReservationDisposals);
                Assert.Equal(0, row.Calls.Refusals);
                Assert.Equal(0, row.Calls.BytesReads);
                Assert.Equal(0, row.Calls.SameSizeResizes);
            }
        }
        finally { File.Delete(output); }
    }

    [Fact]
    public async Task ExplicitCostModeRetainsAlternatingGovernedAndUngovernedBatches()
    {
        string output = Path.Combine("artifacts", "test-governor", Guid.NewGuid() + ".json");
        try
        {
            Assert.Equal(0, await GovernorRunner.RunAsync(["--mode", "cost", "--case", "tiny", "--workers", "1",
                "--iterations", "2", "--output", output], CancellationToken.None));
            var report = JsonSerializer.Deserialize<GovernorReport>(await File.ReadAllTextAsync(output), ComparisonRunner.JsonOptions);
            Assert.NotNull(report);
            Assert.Equal("cost", report.Mode);
            Assert.True(report.Complete);
            Assert.Contains("Unqualified local cost", report.Contract);
            var row = Assert.Single(report.Cases);
            Assert.Equal(0, row.CountedEndBytes);
            Assert.Equal(0, row.CountedEndScopes);
            Assert.Equal(new[] { true, false, false, true, true, false }, row.Batches.Select(batch => batch.Governed));
            Assert.Equal(new[] { 0, 0, 1, 1, 2, 2 }, row.Batches.Select(batch => batch.Pair));
            Assert.All(row.Batches, batch =>
            {
                Assert.Equal(2, batch.Executions);
                Assert.Equal(0, batch.EndReservedBytes);
                Assert.InRange(batch.ManagedBytesPerExecution, 0, double.MaxValue);
            });
        }
        finally { File.Delete(output); }
    }

    [Theory]
    [InlineData("--mode", "invalid")]
    [InlineData("--iterations", "1")]
    [InlineData("--workers", "1,1")]
    [InlineData("--workers", "0")]
    [InlineData("--case", "missing")]
    [InlineData("--output", "outside-governor.json")]
    public async Task InvalidGovernorOptionsFailBeforeCreatingAReport(string option, string value)
    {
        Assert.Equal(2, await GovernorRunner.RunAsync([option, value], CancellationToken.None));
    }

    [Fact]
    public async Task GovernorCancellationDoesNotPublishSuccessfulVerification()
    {
        string output = Path.Combine("artifacts", "test-governor", Guid.NewGuid() + ".json");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GovernorRunner.RunAsync(["--case", "tiny", "--output", output], cancellation.Token));
        Assert.False(File.Exists(output));
    }
}
