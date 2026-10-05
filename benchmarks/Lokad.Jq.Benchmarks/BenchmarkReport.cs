using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lokad.Jq.Benchmarking;

internal sealed record HostProvenance(string Commit, bool CleanCheckout, string Sdk, string Runtime,
    string OperatingSystem, string Architecture, string Cpu, int LogicalProcessors,
    string BenchmarkBuild, string LibraryBuild, string BenchmarkAssemblySha256);
internal sealed record PolicyProvenance(int InputBytes, int OutputBytes, long AllocationBytes,
    int ValueNodes, int StringUtf16Units, int JsonDepth);
internal sealed record ComparisonRow(ComparisonWorkloadInfo Workload, CaseVerification Verification, QualificationResult Timing);
internal sealed record ComparisonWorkloadInfo(string Name, string Scale, int Size, string[] Arguments,
    ComparisonKind Comparison, OutputDigest Input, string FilterSha256);
internal sealed record ComparisonReport(int SchemaVersion, string Mode, DateTimeOffset CreatedUtc,
    string MeasurementContract, HostProvenance Host, ReferenceIdentity Reference, PolicyProvenance Policy,
    QuietCheck MachineGate, int RequestedPairs, int RequestedCases, bool Complete, ComparisonRow[] Cases);

internal static class BenchmarkReport
{
    public const int SchemaVersion = 2;
    public const string Contract = "Warm embedded Lokad.Jq versus jq CLI; includes binding/compilation, parsing, evaluation, rendering and SHA-256 consumption; jq includes launch and pipes. No startup subtraction.";
    public const string BeginMarker = "<!-- BEGIN GENERATED COMPARISON -->";
    public const string EndMarker = "<!-- END GENERATED COMPARISON -->";

    public static ComparisonWorkloadInfo Describe(ComparisonWorkload workload) => new(workload.Name, workload.Scale,
        workload.Size, workload.Arguments, workload.Comparison, OutputDigest.FromBytes(workload.Input),
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(workload.Arguments[^1]))));

    public static async Task<HostProvenance> IdentifyHostAsync(CancellationToken cancellationToken)
    {
        string commit = await QueryAsync("git", ["rev-parse", "HEAD"]).ConfigureAwait(false);
        string status = await QueryAsync("git", ["status", "--porcelain", "--untracked-files=normal", "--", ".", ":(exclude)docs/BENCHMARKS.md"]).ConfigureAwait(false);
        string sdk = await QueryAsync("dotnet", ["--version"]).ConfigureAwait(false);
        string cpu = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unavailable"
            : OperatingSystem.IsLinux() ? File.ReadLines("/proc/cpuinfo").FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[^1].Trim() ?? "Unavailable"
            : "Unavailable";
        string benchmarkBuild = typeof(BenchmarkReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unavailable";
        string libraryBuild = typeof(Jq).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unavailable";
        await using var assembly = File.OpenRead(typeof(BenchmarkReport).Assembly.Location);
        string digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(assembly, cancellationToken).ConfigureAwait(false));
        return new(commit, commit != "Unavailable" && status.Length == 0, sdk, RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), cpu, Environment.ProcessorCount,
            benchmarkBuild, libraryBuild, digest);

        async Task<string> QueryAsync(string executable, string[] arguments)
        {
            var result = await ReferenceProcess.RunAsync(executable, arguments, [], true, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? Encoding.UTF8.GetString(result.Output).Trim() : "Unavailable";
        }
    }

    public static ComparisonReport Create(string mode, HostProvenance host, ReferenceIdentity reference,
        QuietCheck gate, int pairs, int requestedCases, bool complete, IReadOnlyList<ComparisonRow> rows)
    {
        var policy = JqExecutionPolicy.Default;
        return new(SchemaVersion, mode, DateTimeOffset.UtcNow, Contract, host, reference,
            new(policy.MaximumInputBytes, policy.MaximumOutputBytes, policy.MaximumAllocationBytes,
                policy.MaximumValueNodes, policy.MaximumStringLength, 64), gate, pairs, requestedCases, complete, rows.ToArray());
    }

    public static async Task SaveAsync(string path, ComparisonReport report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Missing artifact directory."));
        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, ComparisonRunner.JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    public static string Render(ComparisonReport report)
    {
        if (report.SchemaVersion != SchemaVersion) throw new InvalidOperationException("Unsupported benchmark artifact schema.");
        bool claim = report.Mode == "qualify" && report.MachineGate.IsQuiet && report.Host.CleanCheckout
            && report.Complete && report.Cases.Length == report.RequestedCases;
        var text = new StringBuilder();
        text.AppendLine(BeginMarker).AppendLine("## Recorded comparison").AppendLine();
        text.AppendLine($"Mode: {Escape(report.Mode)}. Measured revision: `{Escape(report.Host.Commit)}`. " +
            $"jq: `{Escape(report.Reference.Version)}`, SHA-256 `{Escape(report.Reference.Sha256)}`.").AppendLine();
        text.AppendLine(FormattableString.Invariant($"Recorded UTC: {report.CreatedUtc:O}. Completed {report.Cases.Length}/{report.RequestedCases} requested cases.")).AppendLine();
        text.AppendLine($"{Escape(report.Host.Runtime)}; SDK {Escape(report.Host.Sdk)}; {Escape(report.Host.OperatingSystem)}; " +
            $"{Escape(report.Host.Cpu)}; {report.Host.LogicalProcessors} logical processors.").AppendLine();
        text.AppendLine(Contract).AppendLine();
        text.AppendLine("Ratio is jq time divided by Lokad.Jq time; above 1 favors Lokad.Jq. CI is a paired-bootstrap 95% interval.").AppendLine();
        if (!claim) text.AppendLine("No qualified performance claim: this artifact is verification-only, paused, incomplete, or lacks a clean quiet-machine qualification.").AppendLine();
        text.AppendLine("| Case / scale | Size | Status | Lokad.Jq ms | jq ms | Ratio / 95% CI | Interpretation |");
        text.AppendLine("| --- | ---: | --- | ---: | ---: | --- | --- |");
        foreach (var row in report.Cases)
        {
            var timing = row.Timing.Summary;
            bool qualified = claim && row.Verification.Status == "Equivalent" && timing.Status == "Qualified";
            string status = row.Verification.Status != "Equivalent" ? row.Verification.Status : timing.Status;
            string times = qualified ? FormattableString.Invariant($"{timing.LibraryMedianMs:F3} | {timing.ReferenceMedianMs:F3} | {timing.Ratio:F2} [{timing.RatioLow:F2}, {timing.RatioHigh:F2}]") : "— | — | —";
            text.AppendLine($"| {Escape(row.Workload.Name)}/{Escape(row.Workload.Scale)} | {row.Workload.Size} | {Escape(status)} | {times} | " +
                $"{Escape(qualified ? timing.Interpretation : row.Verification.Reason.Length > 0 ? row.Verification.Reason : timing.Reason)} |");
        }
        return text.AppendLine().AppendLine(EndMarker).ToString();

        static string Escape(string value) => System.Net.WebUtility.HtmlEncode(value)
            .Replace("|", "\\|", StringComparison.Ordinal).Replace('`', '\'').Replace('\r', ' ').Replace('\n', ' ');
    }

    public static async Task RenderFileAsync(string artifactPath, CancellationToken cancellationToken)
    {
        var report = JsonSerializer.Deserialize<ComparisonReport>(await File.ReadAllTextAsync(artifactPath, cancellationToken).ConfigureAwait(false),
            ComparisonRunner.JsonOptions) ?? throw new InvalidOperationException("Missing benchmark artifact.");
        string path = "docs/BENCHMARKS.md";
        string content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        int begin = content.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin >= 0)
        {
            int end = content.IndexOf(EndMarker, begin, StringComparison.Ordinal);
            if (end < 0) throw new InvalidOperationException("Missing report end marker.");
            content = content[..begin] + Render(report) + "\n" + content[(end + EndMarker.Length)..].TrimStart('\r', '\n');
        }
        else content = content.TrimEnd() + "\n\n" + Render(report);
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
    }
}
