using System.Diagnostics;
using System.Text;
using Lokad.Jq;

namespace Lokad.Jq.Tests;

// Small golden-case schema for compatibility probing. Input is UTF-8 bytes,
// arguments are already tokenized, virtual files map absolute paths to UTF-8
// text, and expectations cover exact stdout bytes, stderr bytes, and status.
// Ordered values are compared as rendered bytes; formatting is the feature
// under test, so callers must not canonicalize key order or newlines away.
internal sealed record JqGoldenCase(
    string Id,
    IReadOnlyList<string> Arguments,
    byte[] StdinUtf8,
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<JqEnvironmentVariable> Environment,
    string ExpectedStdout,
    string ExpectedStderr,
    int ExpectedExit);

internal sealed record JqProbeResult(
    int ExitCode,
    string Stdout,
    string Stderr);

internal static class JqProbeRunner
{
    internal static async Task<JqProbeResult> RunAsync(
        JqGoldenCase goldenCase,
        CancellationToken cancellationToken)
    {
        MockFileSystem host = new MockFileSystem();
        foreach (KeyValuePair<string, string> file in goldenCase.Files)
        {
            host.AddFile(file.Key, file.Value);
        }

        host.SetStandardInputBytes(goldenCase.StdinUtf8);
        JqCommandInvocation invocation = JqCommandInvocation.CreateWithStandardDescriptors(
            "jq",
            goldenCase.Arguments,
            goldenCase.Environment);
        Jq? command = Jq.TryParse(invocation);
        if (command is not Jq tool)
        {
            return new JqProbeResult(2, string.Empty, "jq: not a jq command\n");
        }

        int exitCode = await tool.ExecuteAsync(host, cancellationToken).ConfigureAwait(false);
        string stdout = host.GetOutput(JqFileDescriptor.StdOut);
        string stderr = host.GetOutput(JqFileDescriptor.StdErr);
        return new JqProbeResult(exitCode, stdout, stderr);
    }

    internal static string FormatMismatch(
        string caseId,
        JqProbeResult actual,
        int expectedExit,
        string expectedStdout,
        string expectedStderr)
    {
        StringBuilder builder = new StringBuilder();
        builder.Append("case ");
        builder.Append(caseId);
        builder.Append(": expected exit ");
        builder.Append(expectedExit);
        builder.Append(", got ");
        builder.Append(actual.ExitCode);
        builder.Append("; expected stdout ");
        builder.Append(Quote(expectedStdout));
        builder.Append(", got ");
        builder.Append(Quote(actual.Stdout));
        builder.Append("; expected stderr ");
        builder.Append(Quote(expectedStderr));
        builder.Append(", got ");
        builder.Append(Quote(actual.Stderr));
        return builder.ToString();

        static string Quote(string value)
        {
            return "'" + value + "'";
        }
    }
}

internal sealed record JqReferenceResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    string Note);

internal static class JqReferenceRunner
{
    internal const string ReferenceEnvironmentVariable = "LOKAD_JQ_REFERENCE_JQ";
    private const int MaximumCaptureBytes = 8 * 1024 * 1024;

    internal static bool TryGetReferencePath(out string path, out string skipReason)
    {
        string? configured = Environment.GetEnvironmentVariable(ReferenceEnvironmentVariable);
        if (string.IsNullOrEmpty(configured))
        {
            path = string.Empty;
            skipReason = "Set " + ReferenceEnvironmentVariable + " to an absolute independently installed jq path to enable reference comparison.";
            return false;
        }

        if (!Path.IsPathFullyQualified(configured))
        {
            throw new InvalidOperationException(ReferenceEnvironmentVariable + " must be an absolute path, got \"" + configured + "\".");
        }

        if (configured.Contains("/external/", StringComparison.Ordinal) || configured.Contains("\\external\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(ReferenceEnvironmentVariable + " must point at an independently installed executable, not an inspection checkout.");
        }

        if (!File.Exists(configured))
        {
            throw new InvalidOperationException(ReferenceEnvironmentVariable + " points at a missing file: \"" + configured + "\".");
        }

        path = configured;
        skipReason = string.Empty;
        return true;
    }

    internal static async Task<JqReferenceResult> RunGoldenCaseAsync(
        string referencePath,
        JqGoldenCase goldenCase,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // Virtual files in the golden schema exercise the hosted library only.
        // The reference process receives the same argument array and stdin bytes,
        // without shell quoting or file staging.
        return await RunCommandAsync(referencePath, goldenCase.Arguments, goldenCase.StdinUtf8, timeout, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<JqReferenceResult> RunCommandAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        byte[] stdinUtf8,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo();
        startInfo.FileName = executablePath;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new Process();
        process.StartInfo = startInfo;
        try
        {
            if (!process.Start())
            {
                return new JqReferenceResult(-1, string.Empty, string.Empty, false, "process failed to start");
            }
        }
        catch (Exception exception)
        {
            return new JqReferenceResult(-1, string.Empty, string.Empty, false, "process start failed: " + exception.Message);
        }

        async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken readToken)
        {
            using MemoryStream output = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(), readToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > MaximumCaptureBytes)
                {
                    throw new JqException("reference capture exceeds the 8 MiB limit");
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }

        Task<byte[]> stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, cancellationToken);
        Task<byte[]> stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, cancellationToken);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(stdinUtf8.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The reference may close stdin early; closing still signals EOF below.
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (InvalidOperationException)
            {
                // Process already exited; draining below still applies.
            }
        }

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(true);
            }
            catch (Exception)
            {
                // The process may have exited between the timeout and the kill.
            }

            return new JqReferenceResult(-1, string.Empty, string.Empty, true, "reference timed out");
        }

        byte[] stdoutBytes;
        byte[] stderrBytes;
        try
        {
            stdoutBytes = await stdoutTask.ConfigureAwait(false);
            stderrBytes = await stderrTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JqException exception)
        {
            try
            {
                process.Kill(true);
            }
            catch (Exception)
            {
            }

            return new JqReferenceResult(-1, string.Empty, string.Empty, false, exception.Message);
        }

        string stdout = Encoding.UTF8.GetString(stdoutBytes);
        string stderr = Encoding.UTF8.GetString(stderrBytes);
        return new JqReferenceResult(process.ExitCode, stdout, stderr, false, string.Empty);
    }

    internal static bool OutputsMatch(JqProbeResult local, JqReferenceResult reference)
    {
        if (reference.TimedOut)
        {
            return false;
        }

        return local.ExitCode == reference.ExitCode &&
            string.Equals(local.Stdout, reference.Stdout, StringComparison.Ordinal) &&
            string.Equals(local.Stderr, reference.Stderr, StringComparison.Ordinal);
    }

    internal static string FormatReferenceMismatch(
        string caseId,
        JqProbeResult local,
        JqReferenceResult reference)
    {
        StringBuilder builder = new StringBuilder();
        builder.Append("case ");
        builder.Append(caseId);
        builder.Append(": local exit ");
        builder.Append(local.ExitCode);
        builder.Append(" stdout ");
        builder.Append(Quote(local.Stdout));
        builder.Append(" stderr ");
        builder.Append(Quote(local.Stderr));
        builder.Append("; reference exit ");
        builder.Append(reference.ExitCode);
        builder.Append(" stdout ");
        builder.Append(Quote(reference.Stdout));
        builder.Append(" stderr ");
        builder.Append(Quote(reference.Stderr));
        if (reference.TimedOut)
        {
            builder.Append("; reference timed out");
        }

        if (!string.IsNullOrEmpty(reference.Note))
        {
            builder.Append("; note: ");
            builder.Append(reference.Note);
        }

        return builder.ToString();

        static string Quote(string value)
        {
            return "'" + value + "'";
        }
    }
}


