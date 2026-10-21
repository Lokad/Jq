using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lokad.Jq.Benchmarking;

internal sealed record GovernorWorkload(ComparisonWorkload Workload, OutputDigest Expected, OutputDigest ExpectedError)
{
    internal BenchmarkHost CreateHost() => new(Workload.Input, false, Math.Max(1024 * 1024, ExpectedError.Length));

    internal void Verify(ExecutionResult result)
    {
        if (result.ExitCode != 0 || result.Digest != Expected || OutputDigest.FromBytes(result.Error) != ExpectedError)
            throw new InvalidOperationException($"Governor workload {Workload.Name} failed: exit {result.ExitCode}, stdout/stderr digest mismatch.");
    }
}
internal sealed record GovernorCostBatch(int Pair, bool Governed, int Executions,
    double Milliseconds, double ManagedBytesPerExecution, int Gen0, int Gen1, int Gen2,
    long PeakReservedBytes, long EndReservedBytes, int PeakScopes);
internal sealed record GovernorCostRow(ComparisonWorkloadInfo Workload, int Workers,
    OutputDigest Expected, OutputDigest ExpectedError, MemoryCallCounts Calls,
    long CountedPeakBytes, int CountedPeakScopes, GovernorCostBatch[] Batches);
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
                rows.Add(new(BenchmarkReport.Describe(item.Workload), concurrency, item.Expected, item.ExpectedError,
                    counted.Counts, countedRoot.PeakBytes, countedRoot.PeakScopes, batches.ToArray()));
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
                        GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2, root.PeakBytes, root.ReservedBytes, root.PeakScopes);
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
                    using var host = item.CreateHost();
                    int status = memory is null
                        ? await command.ExecuteAsync(host, deadline.Token).ConfigureAwait(false)
                        : await command.ExecuteAsync(host, memory, deadline.Token).ConfigureAwait(false);
                    item.Verify(host.Finish(status));
                }
            }
        }

        async Task SaveAsync(bool complete)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output) ?? throw new InvalidOperationException("Missing artifact directory."));
            var report = new GovernorCostReport(RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount, System.Runtime.GCSettings.IsServerGC, librarySha,
                "Unqualified local diagnostics; shared recursion guard and provider regex settings; public execution hook versus no hook; caller-owned invocation snapshot/inputs and recognition excluded; binding/compilation, SHA-256 consumption and linked 15-second deadline included. Shared lock-based root tracks active owners only. Each lane warms for at least eight executions per worker and one second; counts collected separately; three alternating timing pairs include process-wide GC allocations/scheduling and preserve all samples. Peak bytes are reservation estimates, not process memory. Requested workers do not prove full simultaneous occupancy.",
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
            if (root.ReservedBytes != 0 || root.ActiveScopes != 0)
                throw new InvalidOperationException("Governor storage or scopes were not refunded.");
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
        string wideObject = "{" + string.Join(',', Enumerable.Range(0, 1024)
            .Select(index => "\"k" + index.ToString(CultureInfo.InvariantCulture) + "\":0")) + "}";
        string comparison = "{\"left\":" + wideObject + ",\"right\":" + wideObject + "}";
        Add("compare-wide-objects", 1024, ["-c", ".left <= .right"], comparison, "true\n");
        Add("compare-object-records", 100, ["-c", ".left <= .right"],
            string.Concat(Enumerable.Repeat(comparison + "\n", 100)), string.Concat(Enumerable.Repeat("true\n", 100)));
        string fields = string.Join(',', Enumerable.Range(0, 16)
            .Select(index => "\"k" + index.ToString(CultureInfo.InvariantCulture) + "\":0"));
        Add("sort-objects", 128, ["-c", "sort | map(.rank)"],
            "[" + string.Join(',', Enumerable.Range(0, 128).Reverse()
                .Select(rank => "{" + fields + ",\"rank\":" + rank.ToString(CultureInfo.InvariantCulture) + "}")) + "]",
            "[" + string.Join(',', Enumerable.Range(0, 128)) + "]\n");
        string sharedKey = "[" + string.Join(',', Enumerable.Range(0, 1000)) + "]";
        Add("min-shared-array-key", 200, ["-c", "--argjson", "key", sharedKey, "min_by($key)"],
            "[" + string.Join(',', Enumerable.Range(0, 200)) + "]", "0\n");
        string duplicate = "[" + string.Join(',', Enumerable.Range(0, 64)) + "]";
        Add("unique-duplicate-trees", 64, ["-c", "unique | length"],
            "[" + string.Join(',', Enumerable.Repeat(duplicate, 64)) + "]", "1\n");
        string nullRow = "[" + string.Join(',', Enumerable.Repeat("null", 128)) + "]";
        Add("transpose-null-padding", 128, ["-c", "transpose | length"],
            "[" + nullRow + "," + string.Join(',', Enumerable.Repeat("null", 127)) + "]", "128\n");
        string nullRows = "[" + string.Join(',', Enumerable.Repeat("null", records)) + "]";
        Add("join-collected-null", records, ["-c", "JOIN(null; \"missing\") | length"], nullRows, "1000\n");
        Add("join-streamed-null", records, ["-c", "JOIN(null; .[]; \"missing\") | .[0]"], nullRows,
            string.Concat(Enumerable.Repeat("null\n", records)));
        string payload = "[" + string.Join(',', Enumerable.Range(0, 1000)) + "]";
        Add("getpath-borrowed", 1000, ["-c", "getpath([\"data\"]) | length"], "{\"data\":" + payload + "}", "1000\n");
        Add("setpath-replacement", 1000, ["-nc", "--argjson", "value", payload, "setpath([]; $value) | length"], "", "1000\n");
        foreach (string operation in new[] { "+", "*" })
            Add(operation == "+" ? "object-add-discarded" : "object-merge-discarded", 1000,
                ["-c", "(. " + operation + " {drop:0}) | keys_unsorted"], "{\"drop\":" + payload + ",\"keep\":1}", "[\"drop\",\"keep\"]\n");
        string mergeLeft = "{\"data\":" + payload + "}";
        string mergeRight = "{}";
        for (int depth = 1; depth < 24; depth++)
        {
            mergeLeft = "{\"child\":" + mergeLeft + "}";
            mergeRight = "{\"child\":" + mergeRight + "}";
        }
        Add("merge-deep-spine", 24, ["-c", ".left * .right | .. | arrays | length"],
            "{\"left\":" + mergeLeft + ",\"right\":" + mergeRight + "}", "1000\n");
        Add("range-bound-products", 13, ["-nc", "[range((0,1);(2,3);(1,2))]"], "", "[0,1,0,0,1,2,0,2,1,1,1,2,1]\n");
        Add("borrowed-selectors", 1000, ["-c", "arrays | values | abs | length"], payload, "1000\n");
        Add("borrowed-extrema", 1000, ["-c", "min | length"], "[" + payload + "]", "1000\n");
        Add("caught-error-payload", 1000, ["-c", "try error catch length"], payload, "1000\n");
        Add("caught-error-records", records, ["-c", "try error catch length"],
            string.Concat(Enumerable.Repeat("[1,2]\n", records)), string.Concat(Enumerable.Repeat("2\n", records)));
        foreach (string operation in new[] { "min_by", "max_by" })
            Add(operation + "-identity-keys", records, ["-c", operation + "(.)"], payload,
                operation == "min_by" ? "0\n" : "999\n");
        foreach (bool sorted in new[] { false, true })
        {
            IEnumerable<string> names = Enumerable.Range(0, 1024).Select(index => "k" + index.ToString(CultureInfo.InvariantCulture));
            if (sorted) names = names.Order(StringComparer.Ordinal);
            Add(sorted ? "object-keys-sorted" : "object-keys-unsorted", 1024,
                ["-c", sorted ? "keys" : "keys_unsorted"], wideObject,
                "[" + string.Join(',', names.Select(name => "\"" + name + "\"")) + "]\n");
        }
        foreach (bool optional in new[] { false, true })
            Add(optional ? "path-optional-records" : "path-label-records", records,
                ["-c", optional ? "path((.a,error(\"late\"))?)" : "path(label $out | (.a,break $out))"],
                string.Concat(Enumerable.Repeat("{\"a\":1}\n", records)), string.Concat(Enumerable.Repeat("[\"a\"]\n", records)));
        Add("zero-argument-floor", records, ["-nc", "range(1000) | floor"], "",
            string.Concat(Enumerable.Range(0, records).Select(value => value.ToString(CultureInfo.InvariantCulture) + "\n")));
        string deletionRecords = string.Concat(Enumerable.Repeat("[0,1,2,3]\n", records));
        Add("delete-nested-slice", records, ["-c", "del(.[0:2][0])"], deletionRecords,
            string.Concat(Enumerable.Repeat("[1,2,3]\n", records)));
        Add("delete-mixed-slices", records, ["-c", "del(.[0:1],getpath([{start:2,end:3}]))"], deletionRecords,
            string.Concat(Enumerable.Repeat("[1,3]\n", records)));
        Add("delete-deep-path", records, ["-nc", "null | setpath([range(1000)|0];null) | delpaths([[range(1000)|0]]) | true"], "", "true\n");
        Add("slice-key-borrowed", records,
            ["-c", "--argjson", "path", "[{\"start\":0,\"end\":1,\"unused\":" + payload + "}]", "getpath($path) | length"],
            deletionRecords, string.Concat(Enumerable.Repeat("1\n", records)));
        foreach (string operation in new[] { "//=", "+=" })
            Add(operation == "//=" ? "compound-default-borrowed" : "compound-add-borrowed", 1000,
                ["-c", "--argjson", "value", payload, "(.slot " + operation + " $value) | .slot | length"], "{\"slot\":null}", "1000\n");
        foreach (string operation in new[] { "_sort_by_impl", "_group_by_impl", "_unique_by_impl" })
            Add("precomputed" + operation, records, ["-c", operation + "(.) | length"], nullRows,
                operation == "_sort_by_impl" ? "1000\n" : "1\n");
        Add("join-null-error", 1, ["-nc", "try join(\",\") catch ."], "", "\"Cannot iterate over null (null)\"\n");
        Add("descent-first-child", records, ["-c", "limit(2; ..) | null"], nullRows, "null\nnull\n");
        Add("paths-first-child", records, ["-c", "first(paths)"], nullRows, "[0]\n");
        Add("tostream-first-event", 1024, ["-c", "first(tostream)"], wideObject, "[[\"k0\"],0]\n");
        Add("flatten-flat-nulls", records, ["-c", "flatten | length"], nullRows, "1000\n");
        Add("flatten-nested-nulls", records, ["-c", "flatten | length"], "[" + nullRows + "]", "1000\n");
        Add("sorted-borrowed-outputs", records, ["-ncS", "{z:1,a:2} as $x | range(1000) | $x"], "",
            string.Concat(Enumerable.Repeat("{\"a\":2,\"z\":1}\n", records)));
        Add("sort-nulls", records, ["-c", "sort | length"], nullRows, "1000\n");
        Add("add-array-fragments", records, ["-c", "add | length"],
            "[" + string.Join(',', Enumerable.Range(0, records).Select(value => "[" + value.ToString(CultureInfo.InvariantCulture) + "]")) + "]", "1000\n");
        Add("walk-null-cursors", records, ["-c", "walk(.) | length"], nullRows, "1000\n");
        Add("with-entries-borrowed", 1000, ["-c", "with_entries(.) | .data | length"], "{\"data\":" + payload + "}", "1000\n");
        Add("while-single-tail", records, ["-nc", "0 | last(limit(1000; while(true; .+1)))"], "", "999\n");
        Add("until-user-tail", records, ["-nc", "def inc: .+1; 0 | until(. == 1000; inc)"], "", "1000\n");
        Add("recurse-single-tail", records, ["-nc", "0 | last(limit(1000; recurse(.+1)))"], "", "999\n");
        Add("recurse-lazy-siblings", records, ["-nc", "0 | [limit(3; recurse((.+1,.+2))) ]"], "", "[0,1,2]\n");
        string borrowed = new('x', 1000);
        string borrowedLines = string.Concat(Enumerable.Repeat(borrowed + "\n", records));
        Add("trim-borrowed", records, ["-Rr", "trim"], borrowedLines, borrowedLines);
        Add("slice-path", records, ["-Rr", "getpath([{start:1,end:2}])"], borrowedLines,
            string.Concat(Enumerable.Repeat("x\n", records)));
        Add("search-first", records, ["-Rc", "index(\"x\")"], borrowedLines,
            string.Concat(Enumerable.Repeat("0\n", records)));
        Add("search-no-hit", records, ["-Rc", "index(\"xxxxxxxxxxxxxxxz\")"], borrowedLines,
            string.Concat(Enumerable.Repeat("null\n", records)));
        Add("csv-quotes", records, ["-Rr", "[.] | @csv"], string.Concat(Enumerable.Repeat(new string('"', 100) + "\n", records)),
            string.Concat(Enumerable.Repeat(new string('"', 202) + "\n", records)));
        string unicode = new('é', 100);
        string unicodeLines = string.Concat(Enumerable.Repeat(unicode + "\n", records));
        Add("uri-unicode", records, ["-Rr", "@urid"], unicodeLines, unicodeLines);
        Add("base64-decode", records, ["-Rr", "@base64d"],
            string.Concat(Enumerable.Repeat(Convert.ToBase64String(Encoding.UTF8.GetBytes(unicode)) + "\n", records)), unicodeLines);
        Add("json-escaped", records, ["-ac", "."], string.Concat(Enumerable.Repeat("\"" + unicode + "\"\n", records)),
            string.Concat(Enumerable.Repeat("\"" + string.Concat(Enumerable.Repeat("\\u00e9", 100)) + "\"\n", records)));
        string privateUse = new('\ue000', 100);
        Add("tojson-escaped", records, ["-nr", "--arg", "s", privateUse, "range(1000) | $s | tojson"], "",
            string.Concat(Enumerable.Repeat("\"" + string.Concat(Enumerable.Repeat("\\ue000", 100)) + "\"\n", records)));
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
        Add("empty-stderr", records, ["-nr", "range(1000) | \"\" | stderr"], "", new string('\n', records));
        AddDiagnostic("debug", records, ["-nc", "range(1000) | debug"], "",
            string.Concat(Enumerable.Range(0, records).Select(value => value.ToString(CultureInfo.InvariantCulture) + "\n")),
            string.Concat(Enumerable.Range(0, records).Select(value => "[\"DEBUG:\"," + value.ToString(CultureInfo.InvariantCulture) + "]\n")));
        AddDiagnostic("debug-burst", records, ["-nc", "range(1000) | debug | empty"], "", "",
            string.Concat(Enumerable.Range(0, records).Select(value => "[\"DEBUG:\"," + value.ToString(CultureInfo.InvariantCulture) + "]\n")));
        AddDiagnostic("stderr-drained-1", records, ["-nr", "range(1000) | \"a\" | stderr"], "",
            string.Concat(Enumerable.Repeat("a\n", records)), new string('a', records));
        foreach (int length in new[] { 1, 4096 })
        {
            string text = new('a', length);
            AddDiagnostic("stderr-burst-" + length.ToString(CultureInfo.InvariantCulture), records,
                ["-nr", "--arg", "s", text, "range(1000) | $s | stderr | empty"], "", "",
                string.Concat(Enumerable.Repeat(text, records)));
        }
        foreach (int length in new[] { 127, 128, 2048 })
        {
            string text = new('a', length);
            Add("string-map-" + length.ToString(CultureInfo.InvariantCulture), records, ["-c", "map(. + \"x\")"],
                "[" + string.Join(',', Enumerable.Repeat("\"" + text + "\"", records)) + "]",
                "[" + string.Join(',', Enumerable.Repeat("\"" + text + "x\"", records)) + "]\n");
        }
        foreach (int length in new[] { 2048, 4095, 4032, 4033 })
        {
            string text = new('a', length);
            string lines = string.Concat(Enumerable.Repeat(text + "\n", records));
            if (length is 2048 or 4095)
                Add("raw-lines-" + length.ToString(CultureInfo.InvariantCulture), records, ["-Rr", "."], lines, lines);
            else
                Add("regex-subject-" + length.ToString(CultureInfo.InvariantCulture), records, ["-Rr", "test(\"a\")"], lines,
                    string.Concat(Enumerable.Repeat("true\n", records)));
        }
        Add("seq-records", records, ["--seq", "-c", "."], string.Concat(Enumerable.Repeat("\u001e1\n", records)),
            string.Concat(Enumerable.Repeat("\u001e1\n", records)));
        Add("stream-array", records, ["--stream", "-c", ".[1] // empty"],
            "[" + string.Join(',', Enumerable.Range(0, records)) + "]",
            string.Concat(Enumerable.Range(0, records).Select(value => value.ToString(CultureInfo.InvariantCulture) + "\n")));
        foreach (int length in new[] { 84, 85, 128 })
        {
            string quoted = "\"" + new string('a', length) + "\"";
            string lines = string.Concat(Enumerable.Repeat("\u001e" + quoted + "\n", records));
            Add("seq-strings-" + length.ToString(CultureInfo.InvariantCulture), records, ["--seq", "-c", "."], lines, lines);
        }
        foreach (int length in new[] { 96, 97, 128 })
        {
            string quoted = "\"" + new string('a', length) + "\"";
            Add("stream-strings-" + length.ToString(CultureInfo.InvariantCulture), records, ["--stream", "-c", ".[1] // empty"],
                "[" + string.Join(',', Enumerable.Repeat(quoted, records)) + "]",
                string.Concat(Enumerable.Repeat(quoted + "\n", records)));
        }
        Add("growing-regex-subject", records, ["-nc", "range(1;1001) | \"a\" * . | test(\"a\")"], "",
            string.Concat(Enumerable.Repeat("true\n", records)));
        foreach (int length in new[] { 61, 62 })
        {
            string pattern = new('a', length);
            Add("regex-result-" + length.ToString(CultureInfo.InvariantCulture), records,
                ["-nrc", "--arg", "p", pattern, "range(1000) | $p | match($p) | .length"], "",
                string.Concat(Enumerable.Repeat(length.ToString(CultureInfo.InvariantCulture) + "\n", records)));
        }
        foreach (int length in new[] { 4032, 4033 })
        {
            Add("regex-subject-alternating-" + length.ToString(CultureInfo.InvariantCulture), records,
                ["-nr", "--arg", "s", "a", "--arg", "l", new string('a', length),
                    "range(1000) | (if . % 2 == 0 then $s else $l end) | test(\"a\")"], "",
                string.Concat(Enumerable.Repeat("true\n", records)));
        }
        Add("regex-result-alternating-62", records,
            ["-nr", "--arg", "s", "a", "--arg", "l", new string('a', 62),
                "range(1000) | (if . % 2 == 0 then $s else $l end) as $p | $p | match($p) | .length"], "",
            string.Concat(Enumerable.Repeat("1\n62\n", records / 2)));
        foreach (int length in new[] { 1, 1000, 1364, 1365 })
        {
            string text = new('a', length);
            Add("tojson-" + length.ToString(CultureInfo.InvariantCulture), records,
                ["-nr", "--arg", "s", text, "range(1000) | $s | tojson"], "",
                string.Concat(Enumerable.Repeat("\"" + text + "\"\n", records)));
        }
        foreach (int length in new[] { 1193, 1194, 1364, 1365 })
        {
            string text = new('a', length);
            Add("tojson-warm-small-" + length.ToString(CultureInfo.InvariantCulture), records + 32,
                ["-nr", "--arg", "s", "a", "--arg", "l", text,
                    "(range(32) | $s | tojson), (range(1000) | (if . % 2 == 0 then $s else $l end) | tojson)"], "",
                string.Concat(Enumerable.Repeat("\"a\"\n", 32))
                    + string.Concat(Enumerable.Repeat("\"a\"\n\"" + text + "\"\n", records / 2)));
        }
        foreach (int count in new[] { 31, 32, 33 })
            Add("tojson-heat-" + count.ToString(CultureInfo.InvariantCulture), count,
                ["-nr", "range(" + count.ToString(CultureInfo.InvariantCulture) + ") | \"a\" | tojson"], "",
                string.Concat(Enumerable.Repeat("\"a\"\n", count)));
        Add("user-tail-flat", 50000, ["-nc", "def f($n): if $n==0 then 0 else . | f($n-1) end; f(50000)"], "", "0\n");
        Add("user-tail-first-borrowed", 10000, ["-nc", "--argjson", "values", "[" + string.Join(',', Enumerable.Repeat("null", 10000)) + "]",
            "def f($x): if $x==1 then f($values[]) else . end; first(f(1))"], "", "null\n");
        Add("user-tail-source-order", records, ["-nc", "def f($n): if $n==0 then . else range(1000) | f(0) end; f(1)"], "",
            string.Concat(Enumerable.Range(0, records).Select(i => i.ToString(CultureInfo.InvariantCulture) + "\n")));
        AddDiagnostic("user-tail-effects", 2, ["-nc", "def f($n): if $n==0 then . else (1,2) | f(0|debug) end; f(1)"], "",
            "1\n2\n", "[\"DEBUG:\",0]\n[\"DEBUG:\",0]\n");
        Add("fromstream-private-array", 2000, ["-nc", "fromstream((range(2000)|[[.],null]), [[1999]]) | length"], "", "2000\n");
        Add("fromstream-records", records, ["-c", "fromstream(.[])"],
            string.Concat(Enumerable.Repeat("[[[0],1],[[1],2],[[1]]]\n", records)), string.Concat(Enumerable.Repeat("[1,2]\n", records)));
        Add("truncate-stream-fractional", records, ["-c", "[truncate_stream([[0,1,2],1])]"],
            string.Concat(Enumerable.Repeat("1.5\n", records)), string.Concat(Enumerable.Repeat("[[[1,2],1]]\n", records)));
        Add("date-remainder", records, ["-c", "strptime(\"%Y-%m-%d\") | .[8]"],
            string.Concat(Enumerable.Repeat("\"2024-01-02 text\"\n", records)), string.Concat(Enumerable.Repeat("\" text\"\n", records)));
        Add("date-caught-message", records, ["-c", "try strptime(\"%Y\") catch startswith(\"date\")"],
            string.Concat(Enumerable.Repeat("\"" + new string('x', 1000) + "\"\n", records)), string.Concat(Enumerable.Repeat("true\n", records)));
        Add("user-tail-wrapped-routes", 1000, ["-nc",
            "def f($n): if $n==0 then . else range(1000) | if true then f(0) else empty end end; f(1)"], "",
            string.Concat(Enumerable.Range(0, records).Select(i => i.ToString(CultureInfo.InvariantCulture) + "\n")));
        Add("user-tail-condition-routes", 2, ["-nc",
            "def f($n): if $n==0 then . else if (true,false) then f(0) else 99 end end; f(1)"], "", "null\n99\n");
        Add("user-call-active-catch", records, ["-nc",
            "def f($n): if $n==0 then error(\"x\") else try f($n-1) catch $n end; range(1000) | f(2)"], "",
            string.Concat(Enumerable.Repeat("1\n", records)));
        Add("label-captured-shadow", 1, ["-nc",
            "[label $out | def f: break $out; (label $out | f),42]"], "", "[]\n");
        foreach (bool broken in new[] { false, true })
            Add(broken ? "label-break-reuse" : "label-scope-reuse", 10000, ["-nc",
                "reduce range(10000) as $i (0; label $out | " + (broken ? "break $out" : ".") + ")"], "",
                broken ? "null\n" : "0\n");
        Add("label-caught-payloads", records, ["-nc", "range(1000) | label $out | try break $out catch .__jq"], "",
            string.Concat(Enumerable.Range(0, records).Select(i => i.ToString(CultureInfo.InvariantCulture) + "\n")));
        Add("user-filter-forwarded", 50000, ["-nc",
            "def f(g;$n): if $n==0 then g else f(g;$n-1) end; f(42;50000)"], "", "42\n");
        Add("user-filter-unused", 50000, ["-nc",
            "def f(g;$n): if $n==0 then . else f(42;$n-1) end; f(0;50000)"], "", "null\n");
        Add("binding-scope-reuse", 10000, ["-nc", "range(10000) | null as $x | $x"], "",
            string.Concat(Enumerable.Repeat("null\n", 10000)));
        Add("pattern-scope-reuse", 10000, ["-nc", "range(10000) | null as [$x,$y] | empty"], "", "");
        Add("definition-scope-reuse", 10000, ["-nc", "range(10000) | (def local: null; local)"], "",
            string.Concat(Enumerable.Repeat("null\n", 10000)));
        Add("definition-tail-reuse", 10000, ["-nc",
            "def f($n): if $n==0 then null else def local: $n-1; f(local) end; f(10000)"], "", "null\n");
        Add("object-empty-workspace-one", 10000, ["-nc", "range(10000) | {a:empty}"], "", "");
        Add("object-empty-workspace-two", 10000, ["-nc", "range(10000) | {a:empty,b:1}"], "", "");
        Add("constant-metadata-wide", 2000, ["-nc",
            "module {items:[" + string.Join(',', Enumerable.Repeat("[0]", 2000)) + "]}; 0"], "", "0\n");
        Add("computed-path-context", records, ["-c", "[path(.a[.b]),path(.a[.b:.c])]"],
            string.Concat(Enumerable.Repeat("{\"a\":[10,20,30],\"b\":1,\"c\":2}\n", records)),
            string.Concat(Enumerable.Repeat("[[\"a\",1],[\"a\",{\"start\":1,\"end\":2}]]\n", records)));
        Add("computed-path-order", 4, ["-nc", "{a:[10,20,30],d:[40,50,60]} | path((.a,.d)[(1,2)])"], "",
            "[\"a\",1]\n[\"d\",1]\n[\"a\",2]\n[\"d\",2]\n");
        Add("argument-alias-path", 1, ["-nc", "--argjson", "data", "{\"a\":1}", "$ARGS.named.data | path($data)"], "", "[]\n");
        string argumentArray = "[" + string.Join(',', Enumerable.Repeat("null", 10000)) + "]";
        Add("argument-named-tree", 10000, ["-nc", "--argjson", "data", argumentArray, "$data | length"], "", "10000\n");
        Add("argument-positional-tree", 10000, ["-nc", "$ARGS.positional[0] | length", "--jsonargs", argumentArray], "", "10000\n");
        return workloads;

        void Add(string name, int size, string[] arguments, string input, string expected) =>
            AddDiagnostic(name, size, arguments, input, expected, "");

        void AddDiagnostic(string name, int size, string[] arguments, string input, string expected, string error) =>
            workloads.Add(new(new ComparisonWorkload(name, "governor", size, arguments,
                Encoding.UTF8.GetBytes(input), ComparisonKind.Bytes), OutputDigest.FromBytes(Encoding.UTF8.GetBytes(expected)),
                OutputDigest.FromBytes(Encoding.UTF8.GetBytes(error))));
    }
}
