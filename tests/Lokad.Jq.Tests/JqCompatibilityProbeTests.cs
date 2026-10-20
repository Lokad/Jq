using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed class JqCompatibilityProbeTests
{
    private static byte[] Utf8(string text)
    {
        return Encoding.UTF8.GetBytes(text);
    }

    private static JqGoldenCase IdentityCase()
    {
        return new JqGoldenCase(
            "identity-match",
            ["."],
            Utf8("{\"a\":1}"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            [],
            "{\n  \"a\": 1\n}\n",
            string.Empty,
            0);
    }

    [Fact]
    public async Task Probe_IdentityMatchesExpected()
    {
        JqGoldenCase goldenCase = IdentityCase();
        JqProbeResult actual = await JqProbeRunner.RunAsync(goldenCase, CancellationToken.None);

        Assert.True(
            actual.ExitCode == goldenCase.ExpectedExit &&
            string.Equals(actual.Stdout, goldenCase.ExpectedStdout, StringComparison.Ordinal) &&
            string.Equals(actual.Stderr, goldenCase.ExpectedStderr, StringComparison.Ordinal),
            JqProbeRunner.FormatMismatch(goldenCase.Id, actual, goldenCase.ExpectedExit, goldenCase.ExpectedStdout, goldenCase.ExpectedStderr));
    }

    [Fact]
    public async Task Probe_MalformedJsonReportsInputError()
    {
        JqGoldenCase goldenCase = new JqGoldenCase(
            "malformed-json",
            ["."],
            Utf8("{\"name\":\""),
            new Dictionary<string, string>(StringComparer.Ordinal),
            [],
            string.Empty,
            "jq:",
            5);
        JqProbeResult actual = await JqProbeRunner.RunAsync(goldenCase, CancellationToken.None);

        Assert.Equal(5, actual.ExitCode);
        Assert.Empty(actual.Stdout);
        Assert.StartsWith(goldenCase.ExpectedStderr, actual.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_UnicodeScalarOutput()
    {
        JqGoldenCase goldenCase = new JqGoldenCase(
            "unicode-scalar",
            ["-r", ".text"],
            Utf8("{\"text\":\"\u00e9\U0001f680\"}"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            [],
            "\u00e9\U0001f680\n",
            string.Empty,
            0);
        JqProbeResult actual = await JqProbeRunner.RunAsync(goldenCase, CancellationToken.None);

        Assert.Equal(0, actual.ExitCode);
        Assert.Equal(goldenCase.ExpectedStdout, actual.Stdout);
        Assert.Empty(actual.Stderr);
    }

    [Fact]
    public async Task Probe_EmptyOutput()
    {
        JqGoldenCase goldenCase = new JqGoldenCase(
            "empty-output",
            ["-n", "empty"],
            Array.Empty<byte>(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            [],
            string.Empty,
            string.Empty,
            0);
        JqProbeResult actual = await JqProbeRunner.RunAsync(goldenCase, CancellationToken.None);

        Assert.Equal(0, actual.ExitCode);
        Assert.Empty(actual.Stdout);
        Assert.Empty(actual.Stderr);
    }

    [Fact]
    public void Probe_MismatchReportingDescribesDivergence()
    {
        JqProbeResult local = new JqProbeResult(0, "{\"a\":1}\n", string.Empty);
        JqReferenceResult reference = new JqReferenceResult(0, "{\"a\":2}\n", string.Empty, false, string.Empty);

        Assert.False(JqReferenceRunner.OutputsMatch(local, reference));
        string message = JqReferenceRunner.FormatReferenceMismatch("synthetic-mismatch", local, reference);
        Assert.Contains("synthetic-mismatch", message, StringComparison.Ordinal);
        Assert.Contains("{\"a\":1}", message, StringComparison.Ordinal);
        Assert.Contains("{\"a\":2}", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Probe_BlockedReferenceInputIsInterruptedAndChildReaped(bool cancel)
    {
        string pidFile = Path.Combine(Path.GetTempPath(), "jq-process-" + Guid.NewGuid().ToString("N"));
        string executable = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),
            "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        using var cancellation = new CancellationTokenSource();
        Task<JqReferenceResult> run = JqReferenceRunner.RunCommandAsync(executable,
            [typeof(CommandBenchmarks).Assembly.Location, "--process-fixture", "wait", pidFile],
            new byte[16 * 1024 * 1024], TimeSpan.FromSeconds(cancel ? 30 : 5), cancellation.Token);
        int? pid = null;
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(pidFile))
            {
                if (run.IsCompleted) await run;
                await Task.Delay(20, startup.Token);
            }
            string text = await File.ReadAllTextAsync(pidFile, startup.Token);
            pid = int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            }
            else Assert.True((await run).TimedOut);
            Assert.False(IsRunning(pid.Value), "The owned child must be reaped before the runner returns.");
        }
        finally
        {
            cancellation.Cancel();
            try { await run; }
            catch (OperationCanceledException) { }
            if (pid is int child && IsRunning(child))
            {
                using var process = Process.GetProcessById(child);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            File.Delete(pidFile);
            File.Delete(pidFile + ".tmp");
        }

        static bool IsRunning(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException) { return false; }
        }
    }

    [Fact]
    public async Task Probe_ReferencePumpPreservesExactPipesAndStatus()
    {
        string executable = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),
            "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        byte[] input = Utf8("é🚀\r\n");
        var result = await JqReferenceRunner.RunCommandAsync(executable,
            [typeof(CommandBenchmarks).Assembly.Location, "--process-fixture", "pipes"],
            input, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(new JqReferenceResult(7, Encoding.UTF8.GetString(input), "diagnostic", false, string.Empty), result);
    }

    [Theory]
    [InlineData("overflow")]
    [InlineData("overflow-error")]
    public async Task Probe_ReferenceCapturesKeepIndependentBounds(string fixture)
    {
        string executable = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),
            "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var result = await JqReferenceRunner.RunCommandAsync(executable,
            [typeof(CommandBenchmarks).Assembly.Location, "--process-fixture", fixture],
            [], TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.NotEmpty(result.Note);
        Assert.Empty(result.Stdout);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task Probe_ReferenceVersionReportsJqProfile()
    {
        if (!JqReferenceRunner.TryGetReferencePath(out string referencePath, out string skipReason))
        {
            Assert.True(true, skipReason);
            return;
        }

        JqReferenceResult result = await JqReferenceRunner.RunCommandAsync(
            referencePath,
            ["--version"],
            Array.Empty<byte>(),
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        Assert.False(result.TimedOut, "reference --version timed out");
        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("jq-", result.Stdout.Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_LocalMatchesReferenceOnIdentity()
    {
        if (!JqReferenceRunner.TryGetReferencePath(out string referencePath, out string skipReason))
        {
            Assert.True(true, skipReason);
            return;
        }

        JqGoldenCase goldenCase = IdentityCase();
        JqProbeResult local = await JqProbeRunner.RunAsync(goldenCase, CancellationToken.None);
        JqReferenceResult reference = await JqReferenceRunner.RunGoldenCaseAsync(
            referencePath,
            goldenCase,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        Assert.True(JqReferenceRunner.OutputsMatch(local, reference), JqReferenceRunner.FormatReferenceMismatch(goldenCase.Id, local, reference));
    }
}
