using System.Diagnostics;

namespace Lokad.Jq.Benchmarking;

internal static class ReferenceProcess
{
    // An absent home and empty module path prevent private ~/.jq content from
    // entering reference execution. Nothing is created at this sentinel path.
    private static readonly string ReferenceHome = Path.Combine(Path.GetTempPath(), "lokad-jq-no-home-" + Guid.NewGuid().ToString("N"));

    public static async Task<ExecutionResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        byte[] input, bool capture, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["HOME"] = ReferenceHome;
        start.Environment.Remove("JQ_LIBRARY_PATH");
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("Reference process did not start.");
        using var host = new BenchmarkHost([], capture);
        Task exit = process.WaitForExitAsync(linked.Token);
        Task write = WriteInputAsync();
        Task output = DrainAsync(process.StandardOutput.BaseStream, JqFileDescriptor.StdOut);
        Task error = DrainAsync(process.StandardError.BaseStream, JqFileDescriptor.StdErr);
        Task[] tasks = [exit, write, output, error];
        try
        {
            // A broken pipe or output bound must cancel its peers immediately,
            // rather than waiting for a child blocked on an undrained pipe.
            foreach (Task task in tasks)
                _ = task.ContinueWith(_ => linked.Cancel(), CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            await Task.WhenAll(tasks).ConfigureAwait(false);
            return host.Finish(process.ExitCode);
        }
        catch
        {
            linked.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            // Observe all pumps before disposing streams/host. Preserve caller
            // cancellation; a deadline is reported separately as a timeout.
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (Exception) { /* The original operation failure is rethrown below. */ }
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) throw new TimeoutException("Reference process timed out.");
            var failure = tasks.Where(task => task.IsFaulted)
                .SelectMany(task => task.Exception?.Flatten().InnerExceptions.AsEnumerable() ?? [])
                .FirstOrDefault(exception => exception is not OperationCanceledException);
            if (failure is not null) throw new IOException("Reference process IO failed.", failure);
            throw;
        }

        async Task WriteInputAsync()
        {
            try { await process.StandardInput.BaseStream.WriteAsync(input, linked.Token).ConfigureAwait(false); }
            catch (IOException)
            {
                // Parser/error exits may close stdin before all input is sent.
                await exit.ConfigureAwait(false);
                if (process.ExitCode == 0) throw;
            }
            finally { process.StandardInput.Close(); }
        }

        async Task DrainAsync(Stream stream, JqFileDescriptor descriptor)
        {
            byte[] buffer = new byte[8192];
            while (true)
            {
                int count = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                if (count == 0) return;
                await host.AppendWhileOpenAsync(descriptor, buffer.AsMemory(0, count), linked.Token).ConfigureAwait(false);
            }
        }
    }
}
