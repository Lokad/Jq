using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lokad.Jq.Benchmarking;

internal sealed record GovernorWorkload(ComparisonWorkload Workload, OutputDigest Expected);
internal sealed record GovernorCostBatch(int Pair, bool Governed, int Executions,
    double Milliseconds, double ManagedBytesPerExecution, int Gen0, int Gen1, int Gen2,
    long PeakReservedBytes, long EndReservedBytes);
internal sealed record GovernorCostRow(ComparisonWorkloadInfo Workload, int Workers,
    OutputDigest Expected, MemoryCallCounts Calls, long CountedPeakBytes, GovernorCostBatch[] Batches);
internal sealed record GovernorCostReport(string Runtime, string OS, string Architecture, int Processors,
    bool ServerGc, string LibrarySha256, string Contract, bool Complete, GovernorCostRow[] Cases);

// Local cost diagnostics, without a reference process or a timing qualification
// claim. Counted runs and uninstrumented timing/allocation runs are distinct.
internal static class GovernorRunner
{
    private static bool IsReleaseBuild =>
#if DEBUG
        false;
#else
        true;
#endif

    internal static async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        if (!IsReleaseBuild)
        {
            Console.Error.WriteLine("Governor diagnostics require a Release build.");
            return 2;
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length || arguments[index] is not ("--case" or "--iterations" or "--workers" or "--output")
                || !options.TryAdd(arguments[index], arguments[index + 1]))
                return Usage();
        }
        if (!int.TryParse(options.GetValueOrDefault("--iterations", "16"), NumberStyles.None,
            CultureInfo.InvariantCulture, out int iterations) || iterations is < 1 or > 1024)
            return Usage();
        string[] workerValues = options.GetValueOrDefault("--workers", "1,4").Split(',');
        var workers = new List<int>();
        foreach (string value in workerValues)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count)
                || count is < 1 or > 32 || workers.Contains(count)) return Usage();
            workers.Add(count);
        }
        string selected = options.GetValueOrDefault("--case", "*");
        GovernorWorkload[] workloads = CreateWorkloads().Where(item => selected == "*" || item.Workload.Name == selected).ToArray();
        if (workloads.Length == 0) return Usage();
        string output = Path.GetFullPath(options.GetValueOrDefault("--output", "artifacts/benchmarks/governor.json"));
        string artifactRoot = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        if (!output.StartsWith(artifactRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return Usage();
        string librarySha = Convert.ToHexStringLower(SHA256.HashData(
            await File.ReadAllBytesAsync(typeof(Jq).Assembly.Location, cancellationToken).ConfigureAwait(false)));
        var rows = new List<GovernorCostRow>();
        await SaveAsync(false).ConfigureAwait(false);
        foreach (GovernorWorkload item in workloads)
        {
            using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", item.Workload.Arguments, []);
            var command = Jq.TryParse(invocation) ?? throw new InvalidOperationException("Expected jq.");
            foreach (int concurrency in workers)
            {
                // Inputs and recognition stay outside all measured lanes. Both
                // lanes bind/compile each execution and verify independent bytes.
                var countedRoot = new BenchmarkMemoryBudget(256L * 1024 * 1024);
                var counted = new CountingMemoryBudget(countedRoot);
                await GroupAsync(command, item, counted, concurrency, 1).ConfigureAwait(false);
                AssertRefunded(countedRoot);
                await WarmAsync(null).ConfigureAwait(false);
                var warm = new BenchmarkMemoryBudget(256L * 1024 * 1024);
                await WarmAsync(warm).ConfigureAwait(false);
                AssertRefunded(warm);
                var batches = new List<GovernorCostBatch>();
                for (int pair = 0; pair < 3; pair++)
                {
                    bool firstGoverned = pair % 2 == 0;
                    batches.Add(await MeasureAsync(firstGoverned, pair).ConfigureAwait(false));
                    batches.Add(await MeasureAsync(!firstGoverned, pair).ConfigureAwait(false));
                }
                rows.Add(new(BenchmarkReport.Describe(item.Workload), concurrency, item.Expected,
                    counted.Counts, countedRoot.PeakBytes, batches.ToArray()));
                await SaveAsync(rows.Count == workloads.Length * workers.Count).ConfigureAwait(false);
                double baseline = batches.Where(batch => !batch.Governed).Select(batch => batch.Milliseconds / batch.Executions).Order().ElementAt(1);
                double governed = batches.Where(batch => batch.Governed).Select(batch => batch.Milliseconds / batch.Executions).Order().ElementAt(1);
                Console.WriteLine(FormattableString.Invariant($"{item.Workload.Name}, {concurrency} workers: {counted.Counts.Reservations} reserves/{counted.Counts.Resizes} resizes; {governed / baseline:F3} governed/ungoverned elapsed ratio (unqualified)."));

                async Task<GovernorCostBatch> MeasureAsync(bool governed, int pair)
                {
                    var root = new BenchmarkMemoryBudget(256L * 1024 * 1024);
                    int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
                    long allocated = GC.GetTotalAllocatedBytes(true);
                    long started = Stopwatch.GetTimestamp();
                    await GroupAsync(command, item, governed ? root : null, concurrency, iterations).ConfigureAwait(false);
                    double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    long bytes = GC.GetTotalAllocatedBytes(true) - allocated;
                    AssertRefunded(root);
                    return new(pair, governed, concurrency * iterations, milliseconds,
                        (double)bytes / (concurrency * iterations), GC.CollectionCount(0) - gen0,
                        GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2, root.PeakBytes, root.ReservedBytes);
                }

                async Task WarmAsync(IMemoryBudget? memory)
                {
                    var watch = Stopwatch.StartNew();
                    int completed = 0;
                    while (completed < 8 || watch.ElapsedMilliseconds < 1000)
                    {
                        await GroupAsync(command, item, memory, concurrency, 2).ConfigureAwait(false);
                        completed += 2;
                    }
                }
            }
        }
        return 0;

        async Task GroupAsync(Jq command, GovernorWorkload item, IMemoryBudget? memory, int concurrency, int count)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task[] tasks = Enumerable.Range(0, concurrency).Select(_ => WorkerAsync()).ToArray();
            start.SetResult();
            await Task.WhenAll(tasks).ConfigureAwait(false);

            async Task WorkerAsync()
            {
                await start.Task.ConfigureAwait(false);
                for (int iteration = 0; iteration < count; iteration++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    using var host = new BenchmarkHost(item.Workload.Input, false);
                    int status = memory is null
                        ? await command.ExecuteAsync(host, deadline.Token).ConfigureAwait(false)
                        : await command.ExecuteAsync(host, memory, deadline.Token).ConfigureAwait(false);
                    BenchmarkExecution.Verify(host.Finish(status), item.Expected);
                }
            }
        }

        async Task SaveAsync(bool complete)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output) ?? throw new InvalidOperationException("Missing artifact directory."));
            var report = new GovernorCostReport(RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount, System.Runtime.GCSettings.IsServerGC, librarySha,
                "Unqualified local diagnostics; unchanged default work policy; public execution hook versus no hook; caller-owned invocation snapshot/inputs and recognition excluded; binding/compilation, SHA-256 consumption and linked 15-second deadline included. Shared lock-based root tracks active owners only. Each lane warms for at least eight executions per worker and one second; counts collected separately; three alternating timing pairs include process-wide GC allocations/scheduling and preserve all samples. Peak bytes are reservation estimates, not process memory. Requested workers do not prove full simultaneous occupancy.",
                complete, rows.ToArray());
            string temporary = output + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, ComparisonRunner.JsonOptions), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, output, true);
        }

        static int Usage()
        {
            Console.Error.WriteLine("Usage: --governor [--case NAME] [--iterations 1..1024] [--workers 1,4] [--output artifacts/PATH]");
            return 2;
        }

        static void AssertRefunded(BenchmarkMemoryBudget root)
        {
            if (root.ReservedBytes != 0) throw new InvalidOperationException("Governor storage was not refunded.");
        }
    }

    internal static IReadOnlyList<GovernorWorkload> CreateWorkloads()
    {
        const int records = 1000;
        const int elements = 10000;
        var workloads = new List<GovernorWorkload>();
        Add("tiny", 1, ["-n", "-c", "1"], "", "1\n");
        string jsonLines = string.Concat(Enumerable.Repeat("1\n", records));
        Add("json-records", records, ["-c", "."], jsonLines, jsonLines);
        string rawLines = string.Concat(Enumerable.Repeat("x\n", records));
        Add("raw-records", records, ["-Rr", "."], rawLines, rawLines);
        Add("map", elements, ["-c", "map(. + 1)"], "[" + string.Join(',', Enumerable.Range(0, elements)) + "]",
            "[" + string.Join(',', Enumerable.Range(1, elements)) + "]\n");
        foreach (int length in new[] { 1000, 100000 })
        {
            string text = new('a', length);
            Add(length == 1000 ? "html-small" : "html-large", length, ["-r", "@html"], "\"" + text + "\"", text + "\n");
        }
        Add("implode", elements, ["-r", "implode"], "[" + string.Join(',', Enumerable.Repeat("97", elements)) + "]", new string('a', elements) + "\n");
        Add("growing-raw", records, ["-n", "-r", "range(1;1001) | \"x\" * ."], "",
            string.Concat(Enumerable.Range(1, records).Select(length => new string('x', length) + "\n")));
        Add("regex-test", records, ["-n", "-c", "range(1000) | \"abc\" | test(\"^a\")"], "", string.Concat(Enumerable.Repeat("true\n", records)));
        Add("regex-scan", records, ["-c", "[scan(\"a\")] | length"], "\"" + new string('a', records) + "\"", "1000\n");
        return workloads;

        void Add(string name, int size, string[] arguments, string input, string expected) =>
            workloads.Add(new(new ComparisonWorkload(name, "governor", size, arguments,
                Encoding.UTF8.GetBytes(input), ComparisonKind.Bytes), OutputDigest.FromBytes(Encoding.UTF8.GetBytes(expected))));
    }
}
