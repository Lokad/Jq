using System.Diagnostics;

namespace Lokad.Jq.Benchmarking;

internal sealed record QualificationResult(TimingSummary Summary, PairedSample[] Samples,
    QuietCheck[] QuietChecks, int LibraryWarmupCalls, int ReferenceWarmupCalls);

internal static class TimingQualification
{
    public static async Task<QualificationResult> RunAsync(string executable, ComparisonWorkload workload,
        CaseVerification verified, int pairs, CancellationToken cancellationToken)
    {
        var samples = new List<PairedSample>();
        var quietChecks = new List<QuietCheck>();
        int libraryWarmup = 0, referenceWarmup = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            libraryWarmup = await WarmAsync(true).ConfigureAwait(false);
            referenceWarmup = await WarmAsync(false).ConfigureAwait(false);
            int libraryIterations = await CalibrateAsync(true).ConfigureAwait(false);
            int referenceIterations = await CalibrateAsync(false).ConfigureAwait(false);
            for (int i = 0; i < pairs; i++)
            {
                // Allow tiered compilation/GC to settle. A one-second window
                // avoids coarse tick rounding on small Linux VMs.
                await Task.Delay(500, deadline.Token).ConfigureAwait(false);
                var quiet = await MachineQuietProbe.CheckAsync(1, TimeSpan.FromSeconds(1), deadline.Token).ConfigureAwait(false);
                quietChecks.Add(quiet);
                if (!quiet.IsQuiet) return Finish("Paused", quiet.Reason);
                bool libraryFirst = i % 2 == 0;
                double libraryMs, referenceMs;
                if (libraryFirst)
                {
                    libraryMs = await TimeAsync(true, libraryIterations).ConfigureAwait(false);
                    referenceMs = await TimeAsync(false, referenceIterations).ConfigureAwait(false);
                }
                else
                {
                    referenceMs = await TimeAsync(false, referenceIterations).ConfigureAwait(false);
                    libraryMs = await TimeAsync(true, libraryIterations).ConfigureAwait(false);
                }
                samples.Add(new(libraryFirst, libraryIterations, referenceIterations, libraryMs, referenceMs));
            }
            return new(PairedStatistics.Summarize(samples), samples.ToArray(), quietChecks.ToArray(), libraryWarmup, referenceWarmup);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Finish("Timeout", "Qualification exceeded its 120-second case deadline.");
        }
        catch (TimeoutException) { return Finish("Timeout", "A timed reference invocation exceeded its deadline."); }
        catch (IOException) { return Finish("Failure", "Timed reference IO failed."); }
        catch (InvalidOperationException exception) { return Finish("Failure", exception.Message); }

        QualificationResult Finish(string status, string reason) => new(
            new(status, reason, 0, 0, 0, 0, 0, "No performance claim"), samples.ToArray(), quietChecks.ToArray(), libraryWarmup, referenceWarmup);

        async Task ExecuteAsync(bool library)
        {
            ExecutionResult result;
            if (library)
            {
                using var invocationDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                invocationDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                result = await BenchmarkExecution.RunAsync(workload.Arguments, workload.Input, false, invocationDeadline.Token).ConfigureAwait(false);
            }
            else result = await ReferenceProcess.RunAsync(executable, workload.Arguments, workload.Input, false,
                TimeSpan.FromSeconds(15), true, deadline.Token).ConfigureAwait(false);
            BenchmarkExecution.Verify(result, library ? verified.LibraryOutput : verified.ReferenceOutput);
        }

        async Task<int> WarmAsync(bool library)
        {
            var watch = Stopwatch.StartNew();
            int calls = 0;
            while (calls < 32 || watch.ElapsedMilliseconds < 1000)
            {
                await ExecuteAsync(library).ConfigureAwait(false);
                calls++;
            }
            return calls;
        }

        async Task<int> CalibrateAsync(bool library)
        {
            int iterations = 1;
            while (true)
            {
                double elapsed = await TimeAsync(library, iterations).ConfigureAwait(false);
                if (elapsed >= 40 || iterations >= 4096) return iterations;
                iterations = Math.Min(iterations * 2, 4096);
            }
        }

        async Task<double> TimeAsync(bool library, int iterations)
        {
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) await ExecuteAsync(library).ConfigureAwait(false);
            return watch.Elapsed.TotalMilliseconds;
        }
    }
}
