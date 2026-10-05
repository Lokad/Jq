using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lokad.Jq.Benchmarking;

internal sealed record AllocationBatch(double BytesPerExecution, int Gen0, int Gen1, int Gen2);
internal sealed record AllocationRow(ComparisonWorkloadInfo Workload, OutputDigest Output, AllocationBatch[] Batches);
internal sealed record AllocationReport(HostProvenance Host, string LibrarySha256, bool ServerGc,
    string Contract, bool Complete, AllocationRow[] Cases);

// Actual managed allocations, including binding, execution, host consumption
// and a per-invocation deadline. Input generation and preflight are outside.
// These measurements do not qualify CPU timing or measure native jq memory.
internal static class AllocationRunner
{
    private static bool IsReleaseBuild =>
#if DEBUG
        false;
#else
        true;
#endif

    public static async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        if (!IsReleaseBuild)
        {
            Console.Error.WriteLine("Allocation measurements require a Release build.");
            return 2;
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length || arguments[index] is not ("--case" or "--scale" or "--output")
                || !options.TryAdd(arguments[index], arguments[index + 1]))
            {
                Console.Error.WriteLine("Usage: --allocations [--case NAME] [--scale SCALE] [--output artifacts/PATH]");
                return 2;
            }
        }
        string output = Path.GetFullPath(options.GetValueOrDefault("--output", "artifacts/benchmarks/allocations.json"));
        string artifactRoot = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        if (!output.StartsWith(artifactRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Raw allocation output must stay under artifacts/.");
            return 2;
        }
        string selected = options.GetValueOrDefault("--case", "*");
        string scale = options.GetValueOrDefault("--scale", "all");
        var workloads = WorkloadCatalog.Create().Concat(CreateDiagnostics())
            .Where(w => (selected == "*" || w.Name == selected) && (scale == "all" || w.Scale == scale)).ToArray();
        if (workloads.Length == 0)
        {
            Console.Error.WriteLine("No workloads match the requested case and scale.");
            return 2;
        }
        var host = await BenchmarkReport.IdentifyHostAsync(cancellationToken).ConfigureAwait(false);
        string librarySha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(typeof(Jq).Assembly.Location, cancellationToken).ConfigureAwait(false)));
        var rows = new List<AllocationRow>();
        await SaveAsync(false).ConfigureAwait(false);
        foreach (var workload in workloads)
        {
            var verification = await BenchmarkExecution.RunAsync(workload.Arguments, workload.Input, true, cancellationToken).ConfigureAwait(false);
            if (!verification.IsSuccess)
                throw new InvalidOperationException("Allocation preflight failed: " + Encoding.UTF8.GetString(verification.Error));
            var digest = verification.Digest;
            var watch = Stopwatch.StartNew();
            int calls = 0;
            while (calls < 32 || watch.ElapsedMilliseconds < 1000)
            {
                await ExecuteAsync().ConfigureAwait(false);
                calls++;
            }
            var batches = new AllocationBatch[3];
            for (int batch = 0; batch < batches.Length; batch++)
            {
                int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
                long before = GC.GetTotalAllocatedBytes(true);
                for (int iteration = 0; iteration < 8; iteration++)
                    await ExecuteAsync().ConfigureAwait(false);
                long allocated = GC.GetTotalAllocatedBytes(true) - before;
                batches[batch] = new((double)allocated / 8, GC.CollectionCount(0) - gen0,
                    GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2);
            }
            rows.Add(new(BenchmarkReport.Describe(workload), digest, batches));
            await SaveAsync(rows.Count == workloads.Length).ConfigureAwait(false);
            Console.WriteLine(FormattableString.Invariant($"{workload.Name}/{workload.Scale}: {batches.Select(b => b.BytesPerExecution).Order().ElementAt(1):F0} managed bytes/execution"));

            async Task ExecuteAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                var result = await BenchmarkExecution.RunAsync(workload.Arguments, workload.Input, false, deadline.Token).ConfigureAwait(false);
                BenchmarkExecution.Verify(result, digest);
            }
        }
        return 0;

        async Task SaveAsync(bool complete)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output) ?? throw new InvalidOperationException("Missing artifact directory."));
            var report = new AllocationReport(host, librarySha, System.Runtime.GCSettings.IsServerGC,
                "Managed GC bytes; default execution policy; warm binding/execution/SHA-256 consumption and linked 15-second deadline; three batches of eight; no timing/native-memory claim.", complete, rows.ToArray());
            string temporary = output + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, ComparisonRunner.JsonOptions), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, output, overwrite: true);
        }
    }

    private static IEnumerable<ComparisonWorkload> CreateDiagnostics()
    {
        byte[] rows = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 256).Select(i => "{\"id\":" + i + "}\n")));
        string fields = string.Join(',', Enumerable.Range(0, 8).Select(i => "a" + i + ":(.id + " + i + ")"));
        yield return new("wide-object", "diagnostic", 256, ["-c", "{" + fields + "}"], rows, ComparisonKind.Bytes);
        yield return new("small-filter", "diagnostic", 1,
            ["-c", "def twice($x): $x * 2; {id, a:(.id|twice(.)), b:[range(0;4)], c:(.id % 3)}"],
            "{\"id\":7}"u8.ToArray(), ComparisonKind.Bytes);
        string text = new string('x', 1024) + " café🚀";
        yield return new("raw-strings", "diagnostic", 512, ["-r", ".[]"],
            JsonSerializer.SerializeToUtf8Bytes(Enumerable.Repeat(text, 512)), ComparisonKind.Bytes);
        var original = WorkloadCatalog.Create().Single(w => w.Name == "identity-compact" && w.Scale == "medium");
        yield return new("sorted-ascii", "diagnostic", 256, ["-c", "-S", "-a", "."], original.Input, ComparisonKind.Bytes);
        var large = WorkloadCatalog.Create().Single(w => w.Name == "identity-compact" && w.Scale == "large");
        yield return new("scalar-pipeline", "diagnostic", 4096, ["-c", ".[] | .id * 2"], large.Input, ComparisonKind.Bytes);
        byte[] scalars = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 4096)
            .Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n")));
        yield return new("buffered-scalars", "diagnostic", 4096, ["-c", "."], scalars, ComparisonKind.Bytes);
    }
}
