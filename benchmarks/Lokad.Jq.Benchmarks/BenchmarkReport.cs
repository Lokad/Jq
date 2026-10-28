using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lokad.Utf8Regex.Pcre2;

namespace Lokad.Jq.Benchmarking;

internal sealed record HostProvenance(string Commit, bool CleanCheckout, string Sdk, string Runtime,
    string OperatingSystem, string Architecture, string Cpu, int LogicalProcessors,
    string BenchmarkBuild, string LibraryBuild, string BenchmarkAssemblySha256, string RegexProvider);
internal sealed record PolicyProvenance(int JsonDepth);
internal sealed record ComparisonRow(ComparisonWorkloadInfo Workload, CaseVerification Verification, QualificationResult Timing);
internal sealed record ComparisonWorkloadInfo(string Name, string Scale, int Size, string[] Arguments,
    ComparisonKind Comparison, OutputDigest Input, string FilterSha256);
internal sealed record ComparisonReport(int SchemaVersion, string Mode, DateTimeOffset CreatedUtc,
    string MeasurementContract, HostProvenance Host, ReferenceIdentity Reference, PolicyProvenance Policy,
    QuietCheck MachineGate, int RequestedPairs, int RequestedCases, bool Complete, ComparisonRow[] Cases);

internal static class BenchmarkReport
{
    public const int SchemaVersion = 6;
    public const string Contract = "Warm embedded Lokad.Jq versus jq CLI; includes binding/compilation, parsing, evaluation, rendering and SHA-256 consumption; jq includes launch and pipes. No startup subtraction.";
    public const string BeginMarker = "<!-- BEGIN GENERATED COMPARISON -->";
    public const string EndMarker = "<!-- END GENERATED COMPARISON -->";
    public const string ProvenanceBeginMarker = "<!-- BEGIN GENERATED COMPARISON PROVENANCE -->";
    public const string ProvenanceEndMarker = "<!-- END GENERATED COMPARISON PROVENANCE -->";
    public const string AllocationBeginMarker = "<!-- BEGIN GENERATED ALLOCATIONS -->";
    public const string AllocationEndMarker = "<!-- END GENERATED ALLOCATIONS -->";

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
        var provider = typeof(Utf8Pcre2Regex).Assembly;
        string providerVersion = provider.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unavailable";
        await using var providerStream = File.OpenRead(provider.Location);
        string providerHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(providerStream, cancellationToken).ConfigureAwait(false));
        string providerIdentity = $"{provider.GetName().Name} {providerVersion}; SHA-256 {providerHash}";
        await using var assembly = File.OpenRead(typeof(BenchmarkReport).Assembly.Location);
        string digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(assembly, cancellationToken).ConfigureAwait(false));
        return new(commit, commit != "Unavailable" && status.Length == 0, sdk, RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), cpu, Environment.ProcessorCount,
            benchmarkBuild, libraryBuild, digest, providerIdentity);

        async Task<string> QueryAsync(string executable, string[] arguments)
        {
            var result = await ReferenceProcess.RunAsync(executable, arguments, [], true, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? Encoding.UTF8.GetString(result.Output).Trim() : "Unavailable";
        }
    }

    public static ComparisonReport Create(string mode, HostProvenance host, ReferenceIdentity reference,
        QuietCheck gate, int pairs, int requestedCases, bool complete, IReadOnlyList<ComparisonRow> rows)
    {
        return new(SchemaVersion, mode, DateTimeOffset.UtcNow, Contract, host, reference,
            new(64), gate, pairs, requestedCases, complete, rows.ToArray());
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
        text.AppendLine(BeginMarker).AppendLine("## Timing").AppendLine();
        text.AppendLine("| Case / scale | Size | Status | Lokad.Jq ms | jq ms | Lokad.Jq / jq |");
        text.AppendLine("| --- | ---: | --- | ---: | ---: | ---: |");
        var rows = report.Cases.Select(row =>
        {
            var timing = row.Timing.Summary;
            bool qualified = claim && row.Verification.Status == "Equivalent" && timing.Status == "Qualified";
            double libraryMs = qualified ? row.Timing.Samples.Average(sample => sample.LibraryMs) : 0;
            double referenceMs = qualified ? row.Timing.Samples.Average(sample => sample.ReferenceMs) : 0;
            return (Row: row, Qualified: qualified, LibraryMs: libraryMs, ReferenceMs: referenceMs,
                Ratio: qualified ? libraryMs / referenceMs : 0);
        }).OrderByDescending(item => item.Qualified).ThenByDescending(item => item.Ratio)
            .ThenBy(item => item.Row.Workload.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Row.Workload.Scale, StringComparer.Ordinal);
        var notes = new List<string>();
        foreach (var item in rows)
        {
            var row = item.Row;
            var timing = row.Timing.Summary;
            string status = row.Verification.Status != "Equivalent" ? row.Verification.Status : timing.Status;
            string times = item.Qualified ? FormattableString.Invariant($"{item.LibraryMs:F3} | {item.ReferenceMs:F3} | {item.Ratio:F3}") : "— | — | —";
            string name = $"{Escape(row.Workload.Name)}/{Escape(row.Workload.Scale)}";
            text.AppendLine($"| {name} | {row.Workload.Size} | {Escape(status)} | {times} |");
            string reason = row.Verification.Reason.Length > 0 ? row.Verification.Reason : timing.Reason;
            if (!item.Qualified && reason.Length > 0) notes.Add($"- {name}: {Escape(reason)}");
        }
        if (notes.Count > 0) text.AppendLine().AppendJoin(Environment.NewLine, notes).AppendLine();
        if (!claim) text.AppendLine().AppendLine("No qualified performance claim: this artifact is verification-only, paused, incomplete, or lacks a clean quiet-machine qualification.").AppendLine();
        return text.AppendLine().AppendLine(EndMarker).ToString();
    }

    private static string RenderProvenance(ComparisonReport report)
    {
        var text = new StringBuilder();
        text.AppendLine(ProvenanceBeginMarker).AppendLine("## Comparison provenance").AppendLine();
        text.AppendLine($"Mode: {Escape(report.Mode)}. Measured revision: `{Escape(report.Host.Commit)}`. " +
            $"jq: `{Escape(report.Reference.Version)}`, SHA-256 `{Escape(report.Reference.Sha256)}`.").AppendLine();
        text.AppendLine(FormattableString.Invariant($"Recorded UTC: {report.CreatedUtc:O}. Completed {report.Cases.Length}/{report.RequestedCases} requested cases.")).AppendLine();
        text.AppendLine($"{Escape(report.Host.Runtime)}; SDK {Escape(report.Host.Sdk)}; {Escape(report.Host.OperatingSystem)}; " +
            $"{Escape(report.Host.Cpu)}; {report.Host.LogicalProcessors} logical processors.").AppendLine();
        text.AppendLine(Contract).AppendLine();
        text.AppendLine($"Regex provider: {Escape(report.Host.RegexProvider)}.").AppendLine();
        text.AppendLine("Times are arithmetic mean milliseconds per invocation. Lokad.Jq / jq divides those means; lower is better, with 1 at parity. Rows descend by this ratio; unqualified rows follow.").AppendLine();
        return text.AppendLine(ProvenanceEndMarker).ToString();
    }

    private static string Escape(string value) => System.Net.WebUtility.HtmlEncode(value)
        .Replace("|", "\\|", StringComparison.Ordinal).Replace('`', '\'').Replace('\r', ' ').Replace('\n', ' ');

    public static async Task RenderFileAsync(string artifactPath, CancellationToken cancellationToken)
    {
        var report = JsonSerializer.Deserialize<ComparisonReport>(await File.ReadAllTextAsync(artifactPath, cancellationToken).ConfigureAwait(false),
            ComparisonRunner.JsonOptions) ?? throw new InvalidOperationException("Missing benchmark artifact.");
        string path = "docs/BENCHMARKS.md";
        string content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(path, RenderDocument(content, report), cancellationToken).ConfigureAwait(false);
    }

    internal static string RenderDocument(string content, ComparisonReport report)
    {
        content = ReplaceSection(content, BeginMarker, EndMarker, Render(report));
        return ReplaceSection(content, ProvenanceBeginMarker, ProvenanceEndMarker, RenderProvenance(report));
    }

    internal static string RenderAllocationDocument(string content, AllocationReport report) =>
        ReplaceSection(content, AllocationBeginMarker, AllocationEndMarker, RenderAllocations(report));

    internal static string RenderAllocations(AllocationReport report)
    {
        if (!report.Complete || !report.Host.CleanCheckout || report.Cases.Length == 0 ||
            !report.Host.LibraryBuild.Contains(report.Host.Commit, StringComparison.Ordinal) ||
            !report.Host.BenchmarkBuild.Contains(report.Host.Commit, StringComparison.Ordinal) ||
            report.Cases.Any(row => row.Batches.Length != 3 || row.Batches.Any(batch =>
                !double.IsFinite(batch.BytesPerExecution) || batch.BytesPerExecution < 0)))
            throw new InvalidOperationException("Allocation publication requires a complete, clean-checkout report with valid batches.");
        var text = new StringBuilder();
        text.AppendLine(AllocationBeginMarker).AppendLine("## Managed allocations").AppendLine();
        text.AppendLine("| Case / scale | Bytes / execution | Gen0 / Gen1 / Gen2 |");
        text.AppendLine("| --- | ---: | --- |");
        foreach (var row in report.Cases.Where(row => row.Workload.Scale is "large" or "diagnostic")
            .OrderByDescending(row => PairedStatistics.Median(row.Batches.Select(batch => batch.BytesPerExecution)))
            .ThenBy(row => row.Workload.Name, StringComparer.Ordinal))
        {
            string name = $"{Escape(row.Workload.Name)}/{Escape(row.Workload.Scale)}";
            double bytes = PairedStatistics.Median(row.Batches.Select(batch => batch.BytesPerExecution));
            text.AppendLine(FormattableString.Invariant($"| {name} | {bytes:F0} | {row.Batches.Sum(batch => batch.Gen0)} / {row.Batches.Sum(batch => batch.Gen1)} / {row.Batches.Sum(batch => batch.Gen2)} |"));
        }
        text.AppendLine().AppendLine("Large and diagnostic cases. Bytes are the median of three warmed batches of eight executions; GC columns total those 24 executions. These are total managed allocations, not peak heap or native jq memory.").AppendLine();
        text.AppendLine($"Measured revision `{Escape(report.Host.Commit)}`; {Escape(report.Host.Runtime)}; SDK {Escape(report.Host.Sdk)}. " +
            $"All {report.Cases.Length} cases completed. Library SHA-256 `{Escape(report.LibrarySha256)}`.").AppendLine();
        return text.AppendLine(AllocationEndMarker).ToString();
    }

    private static string ReplaceSection(string content, string beginMarker, string endMarker, string replacement)
    {
        int begin = content.IndexOf(beginMarker, StringComparison.Ordinal);
        if (begin < 0) return content.TrimEnd() + "\n\n" + replacement;
        int end = content.IndexOf(endMarker, begin, StringComparison.Ordinal);
        if (end < 0) throw new InvalidOperationException("Missing report end marker.");
        return content[..begin] + replacement + "\n" + content[(end + endMarker.Length)..].TrimStart('\r', '\n');
    }
}
