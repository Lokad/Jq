using System.Security.Cryptography;
using System.Text.Json;

namespace Lokad.Jq.Benchmarking;

// Immutable attempts are the source of truth. The aggregate can always be
// reconstructed after interruption, and completed cases are never retried.
internal static class ComparisonCampaign
{
    public static async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        if (!ComparisonRunner.TryOptions(arguments, out var options, out string error)
            || options.GetValueOrDefault("--mode", "qualify") != "qualify"
            || !int.TryParse(options.GetValueOrDefault("--pairs", "11"), out int pairs) || pairs is < 9 or > 31)
        {
            Console.Error.WriteLine(error.Replace("--compare", "--campaign", StringComparison.Ordinal)
                + "\nCampaigns qualify cases; --output names a campaign directory.");
            return 2;
        }
        string directory = Path.GetFullPath(options.GetValueOrDefault("--output", "artifacts/benchmarks/campaign"));
        string root = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Campaign output must stay under artifacts/.");
            return 2;
        }
        Directory.CreateDirectory(directory);
        using var campaignLock = new FileStream(Path.Combine(directory, "campaign.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        string verification = NextPath(directory, "verification");
        var verifyArguments = new List<string> { "--jq", options["--jq"], "--mode", "verify",
            "--pairs", pairs.ToString(System.Globalization.CultureInfo.InvariantCulture), "--output", verification };
        foreach (string selector in new[] { "--case", "--scale" })
            if (options.TryGetValue(selector, out string? value)) verifyArguments.AddRange([selector, value]);
        int verified = await ComparisonRunner.RunAsync(verifyArguments.ToArray(), cancellationToken).ConfigureAwait(false);
        if (verified != 0) return verified;
        ComparisonReport catalog = Read(verification);
        if (!ComparisonRunner.IsQualificationBuild(catalog.Host))
            throw new InvalidOperationException("A campaign requires a clean Release checkout and fresh assemblies for that revision.");
        ComparisonReport aggregate = Load(directory, catalog);
        await SaveAsync().ConfigureAwait(false);
        foreach (var item in catalog.Cases)
        {
            var workload = item.Workload;
            while (!aggregate.Cases.Any(row => Key(row) == Key(item)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string attempt = NextPath(directory, "case");
                int status = await ComparisonRunner.RunAsync(["--jq", options["--jq"], "--mode", "qualify",
                    "--pairs", pairs.ToString(System.Globalization.CultureInfo.InvariantCulture), "--case", workload.Name,
                    "--scale", workload.Scale, "--output", attempt], cancellationToken).ConfigureAwait(false);
                aggregate = Load(directory, catalog);
                await SaveAsync().ConfigureAwait(false);
                Console.WriteLine($"Campaign: {aggregate.Cases.Length}/{catalog.Cases.Length} completed.");
                if (status == 3)
                {
                    Console.WriteLine("Quiet gate paused; attempt retained. Retrying in five seconds.");
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
                else if (status is not (0 or 1))
                    throw new InvalidOperationException($"Case attempt failed with exit {status}; checkpoint retained.");
            }
        }
        return aggregate.Cases.All(row => row.Timing.Summary.Status == "Qualified") ? 0 : 1;

        Task SaveAsync() => BenchmarkReport.SaveAsync(Path.Combine(directory, "qualification.json"), aggregate, cancellationToken);
    }

    internal static ComparisonReport Load(string directory, ComparisonReport catalog)
    {
        if (catalog.SchemaVersion != BenchmarkReport.SchemaVersion || !ComparisonRunner.IsQualificationBuild(catalog.Host)
            || !catalog.Complete || catalog.Mode != "verify" || catalog.Cases.Length != catalog.RequestedCases
            || catalog.Cases.Any(row => row.Verification.Status != "Equivalent"))
            throw new InvalidOperationException("Campaign verification must be complete and equivalent.");
        var expected = catalog.Cases.ToDictionary(Key, StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(directory, "verification-*.json"))
        {
            var previous = Read(path);
            CheckIdentity(previous, catalog, catalog.RequestedCases);
            if (previous.Mode != "verify" || previous.Cases.Length > catalog.RequestedCases)
                throw new InvalidOperationException("Invalid campaign verification checkpoint.");
            foreach (var row in previous.Cases) CheckCase(row);
        }
        var retained = new Dictionary<string, ComparisonRow>(StringComparer.Ordinal);
        var sources = new List<CampaignCheckpoint>();
        var gates = new List<double>();
        foreach (string path in Directory.EnumerateFiles(directory, "case-*.json").Order(StringComparer.Ordinal))
        {
            byte[] bytes = File.ReadAllBytes(path);
            var report = JsonSerializer.Deserialize<ComparisonReport>(bytes, ComparisonRunner.JsonOptions)
                ?? throw new InvalidOperationException("Missing campaign checkpoint.");
            CheckIdentity(report, catalog, 1);
            if (report.Cases.Length > 1 || report.Mode is not ("qualify" or "paused"))
                throw new InvalidOperationException("A case checkpoint must contain a single attempted case.");
            foreach (var row in report.Cases)
            {
                CheckCase(row);
                if (row.Timing.Summary.Status == "Paused") continue;
                if (row.Timing.Summary.Status is not ("Qualified" or "Unqualified")
                    || !report.MachineGate.IsQuiet || report.MachineGate.BusyPercent.Length != 5
                    || !QuietCheck.Evaluate(report.MachineGate.BusyPercent).IsQuiet
                    || row.Timing.Samples.Length != catalog.RequestedPairs
                    || row.Timing.QuietChecks.Length != catalog.RequestedPairs
                    || row.Timing.QuietChecks.Any(check => !check.IsQuiet || check.BusyPercent.Length != 1
                        || !QuietCheck.Evaluate(check.BusyPercent).IsQuiet)
                    || row.Timing.LibraryWarmupCalls < 96 || row.Timing.ReferenceWarmupCalls < 96
                    || row.Timing.Summary != PairedStatistics.Summarize(row.Timing.Samples))
                    throw new InvalidOperationException("Case checkpoint has incomplete or inconsistent qualification evidence.");
                if (!retained.TryAdd(Key(row), row)) continue;
                sources.Add(new(Path.GetFileName(path), Convert.ToHexStringLower(SHA256.HashData(bytes))));
                gates.AddRange(report.MachineGate.BusyPercent);
            }
        }
        bool complete = retained.Count == catalog.Cases.Length;
        return BenchmarkReport.Create(complete ? "qualify" : "paused", catalog.Host, catalog.Reference,
            new QuietCheck(sources.Count != 0, "Each retained case passed its original quiet gates.", gates.ToArray()),
            catalog.RequestedPairs, catalog.RequestedCases, complete,
            catalog.Cases.Where(row => retained.ContainsKey(Key(row))).Select(row => retained[Key(row)]).ToArray())
            with { Checkpoints = sources.ToArray() };

        void CheckCase(ComparisonRow row)
        {
            if (!expected.TryGetValue(Key(row), out var original) || !Same(row.Workload, original.Workload)
                || row.Verification != original.Verification)
                throw new InvalidOperationException("Checkpoint workload or output verification differs from this campaign.");
        }

        static bool Same<T>(T first, T second) => JsonSerializer.Serialize(first, ComparisonRunner.JsonOptions)
            == JsonSerializer.Serialize(second, ComparisonRunner.JsonOptions);
    }

    private static void CheckIdentity(ComparisonReport report, ComparisonReport catalog, int requestedCases)
    {
        if (report.SchemaVersion != BenchmarkReport.SchemaVersion || report.Host != catalog.Host
            || report.Reference != catalog.Reference || report.Policy != catalog.Policy
            || report.MeasurementContract != catalog.MeasurementContract || report.RequestedPairs != catalog.RequestedPairs
            || report.RequestedCases != requestedCases)
            throw new InvalidOperationException("Campaign checkpoint source, binaries, runtime, provider, reference or policy differs.");
    }

    private static string Key(ComparisonRow row) => row.Workload.Name + "/" + row.Workload.Scale;

    private static ComparisonReport Read(string path) => JsonSerializer.Deserialize<ComparisonReport>(
        File.ReadAllBytes(path), ComparisonRunner.JsonOptions) ?? throw new InvalidOperationException("Missing campaign report.");

    private static string NextPath(string directory, string prefix)
    {
        int next = 0;
        foreach (string path in Directory.EnumerateFiles(directory, prefix + "-*"))
        {
            string number = Path.GetFileName(path).Split('.')[0][(prefix.Length + 1)..];
            if (!int.TryParse(number, out int index) || index < 0 || index == int.MaxValue)
                throw new InvalidOperationException("Invalid campaign checkpoint filename.");
            next = Math.Max(next, index + 1);
        }
        return Path.Combine(directory, $"{prefix}-{next:D10}.json");
    }
}
