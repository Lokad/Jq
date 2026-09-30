using System.Text;
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
            4);
        JqProbeResult actual = await JqProbeRunner.RunAsync(goldenCase, CancellationToken.None);

        Assert.Equal(4, actual.ExitCode);
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

    [Fact]
    public async Task Probe_ReferenceTimeoutKillsOwnedProcess()
    {
        string executable;
        IReadOnlyList<string> arguments;
        if (OperatingSystem.IsWindows())
        {
            executable = "ping";
            arguments = ["127.0.0.1", "-n", "6"];
        }
        else
        {
            executable = "sleep";
            arguments = ["5"];
        }

        JqReferenceResult result = await JqReferenceRunner.RunCommandAsync(
            executable,
            arguments,
            Array.Empty<byte>(),
            TimeSpan.FromMilliseconds(500),
            CancellationToken.None);

        Assert.True(result.TimedOut, "expected the sleep/ping probe to time out; note: " + result.Note);
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
