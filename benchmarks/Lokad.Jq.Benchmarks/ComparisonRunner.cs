using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lokad.Jq.Benchmarking;

internal sealed record ReferenceIdentity(string Version, string Sha256, string BuildConfiguration);
internal sealed record CaseVerification(string Name, string Scale, int Size, string Status,
    string Reason, OutputDigest LibraryOutput, OutputDigest ReferenceOutput);

internal static class ComparisonRunner
{
    private static bool IsReleaseBuild
    {
        get
        {
#if DEBUG
            return false;
#else
            return true;
#endif
        }
    }
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        if (!TryOptions(arguments, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }
        string output = Path.GetFullPath(options.GetValueOrDefault("--output", "artifacts/benchmarks/verification.json"));
        string artifactRoot = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        if (!output.StartsWith(artifactRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Raw benchmark output must stay under artifacts/.");
            return 2;
        }
        string executable = Path.GetFullPath(options["--jq"]);
        if (!File.Exists(executable) || executable.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals("external", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine("Supply an independently installed jq executable outside external/.");
            return 2;
        }
        string mode = options.GetValueOrDefault("--mode", "verify");
        if (mode is not ("verify" or "qualify") || !int.TryParse(options.GetValueOrDefault("--pairs", "11"), out int pairs)
            || pairs < 9 || pairs > 31)
        {
            Console.Error.WriteLine("Use --mode verify|qualify and --pairs between 9 and 31.");
            return 2;
        }
        var host = await BenchmarkReport.IdentifyHostAsync(cancellationToken).ConfigureAwait(false);
        if (mode == "qualify")
        {
            if (!IsReleaseBuild)
            {
                Console.Error.WriteLine("Qualification requires a Release build.");
                return 2;
            }
            if (!host.CleanCheckout || !host.BenchmarkBuild.Contains(host.Commit, StringComparison.Ordinal)
                || !host.LibraryBuild.Contains(host.Commit, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Qualification requires a clean committed checkout and freshly built assemblies for that revision.");
                return 2;
            }
        }
        var reference = await IdentifyAsync(executable, cancellationToken).ConfigureAwait(false);
        if (reference.Version != "jq-1.8.2")
        {
            Console.Error.WriteLine("This catalog targets jq-1.8.2; the supplied executable reports " + reference.Version);
            return 2;
        }
        string selected = options.GetValueOrDefault("--case", "*");
        string scale = options.GetValueOrDefault("--scale", "all");
        var workloads = WorkloadCatalog.Create().Where(w => (selected == "*" || w.Name == selected)
            && (scale == "all" || w.Scale == scale)).ToArray();
        if (workloads.Length == 0)
        {
            Console.Error.WriteLine("No workloads match the requested case and scale.");
            return 2;
        }
        var gate = mode == "qualify"
            ? await MachineQuietProbe.CheckAsync(5, TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false)
            : new QuietCheck(false, "Correctness verification does not qualify machine quietness.", []);
        var results = new List<ComparisonRow>();
        bool complete = false;
        if (mode == "qualify" && !gate.IsQuiet)
        {
            await BenchmarkReport.SaveAsync(output, BenchmarkReport.Create("paused", host, reference, gate, pairs, workloads.Length, false, results), cancellationToken).ConfigureAwait(false);
            Console.WriteLine("Paused before timing: " + gate.Reason);
            return 3;
        }
        await SaveAsync().ConfigureAwait(false);
        foreach (var workload in workloads)
        {
            var result = await VerifyAsync(executable, workload, cancellationToken).ConfigureAwait(false);
            var timing = new QualificationResult(new("NotMeasured", "Correctness verification only.", 0, 0, 0, 0, 0, "No performance claim"), [], [], 0, 0);
            if (mode == "qualify" && result.Status == "Equivalent")
                timing = await TimingQualification.RunAsync(executable, workload, result, pairs, cancellationToken).ConfigureAwait(false);
            results.Add(new(BenchmarkReport.Describe(workload), result, timing));
            Console.WriteLine($"{workload.Name}/{workload.Scale}: {result.Status}" +
                (mode == "qualify" ? "/" + timing.Summary.Status : ""));
            if (timing.Summary.Status == "Paused") mode = "paused";
            await SaveAsync().ConfigureAwait(false);
            if (mode == "paused") return 3;
        }
        complete = true;
        await SaveAsync().ConfigureAwait(false);
        Console.WriteLine(mode == "verify" ? "Correctness verification only; no timings collected." : "Qualification checkpoint saved; render its report explicitly.");
        return results.All(row => row.Verification.Status == "Equivalent"
            && (mode == "verify" || row.Timing.Summary.Status == "Qualified")) ? 0 : 1;

        Task SaveAsync() => BenchmarkReport.SaveAsync(output,
            BenchmarkReport.Create(mode, host, reference, gate, pairs, workloads.Length, complete, results), cancellationToken);
    }

    public static async Task<ReferenceIdentity> IdentifyAsync(string executable, CancellationToken cancellationToken)
    {
        var version = await ReferenceProcess.RunAsync(executable, ["--version"], [], true,
            TimeSpan.FromSeconds(15), true, cancellationToken).ConfigureAwait(false);
        var config = await ReferenceProcess.RunAsync(executable, ["--build-configuration"], [], true,
            TimeSpan.FromSeconds(15), true, cancellationToken).ConfigureAwait(false);
        if (!version.IsSuccess || !config.IsSuccess) throw new InvalidOperationException("Reference identity queries failed.");
        await using var file = File.OpenRead(executable);
        string digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        return new(Encoding.UTF8.GetString(version.Output).Trim(), digest, Encoding.UTF8.GetString(config.Output).Trim());
    }

    public static async Task<CaseVerification> VerifyAsync(string executable, ComparisonWorkload workload,
        CancellationToken cancellationToken)
    {
        var empty = OutputDigest.FromBytes([]);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var library = await BenchmarkExecution.RunAsync(workload.Arguments, workload.Input, true, deadline.Token).ConfigureAwait(false);
            var reference = await ReferenceProcess.RunAsync(executable, workload.Arguments, workload.Input, true,
                TimeSpan.FromSeconds(15), true, cancellationToken).ConfigureAwait(false);
            string status = library.ExitCode == 5 ? "Quota" : !library.IsSuccess || !reference.IsSuccess ? "Failure"
                : OutputComparison.Equivalent(library.Output, reference.Output, workload.Comparison) ? "Equivalent" : "Mismatch";
            string reason = status == "Equivalent" ? "" : $"exit codes {library.ExitCode}/{reference.ExitCode}; " +
                $"stderr {Encoding.UTF8.GetString(library.Error)} / {Encoding.UTF8.GetString(reference.Error)}";
            return new(workload.Name, workload.Scale, workload.Size, status, reason, library.Digest, reference.Digest);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(workload.Name, workload.Scale, workload.Size, "Timeout", "Preflight exceeded its deadline.", empty, empty);
        }
        catch (JsonException)
        {
            return new(workload.Name, workload.Scale, workload.Size, "Mismatch", "Output is not a valid JSON value stream.", empty, empty);
        }
        catch (IOException)
        {
            return new(workload.Name, workload.Scale, workload.Size, "Failure", "Reference process IO failed.", empty, empty);
        }
    }

    internal static bool TryOptions(string[] arguments, out Dictionary<string, string> options, out string error)
    {
        options = new(StringComparer.Ordinal);
        error = "Usage: --compare --jq PATH [--mode verify|qualify] [--pairs 11] [--case NAME] [--scale small|medium|large|control|all] [--output artifacts/PATH]";
        for (int index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length || arguments[index] is not ("--jq" or "--case" or "--scale" or "--output" or "--mode" or "--pairs")
                || !options.TryAdd(arguments[index], arguments[index + 1])) return false;
        }
        return options.ContainsKey("--jq");
    }
}
