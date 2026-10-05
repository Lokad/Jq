using System.Text;

namespace Lokad.Jq.Benchmarking;

internal static class StressChecks
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        // Small explicit policies expose cumulative failures without allocating
        // hundreds of megabytes or turning elapsed time into a test assertion.
        await CheckAsync(["-n", "-c", "range(0;100)"], [],
            new JqExecutionPolicy { MaximumOutputBytes = 4 }, 5, "0\n1\n").ConfigureAwait(false);
        await CheckAsync(["-n", "[range(0;1000)]"], [],
            new JqExecutionPolicy { MaximumValueNodes = 128 }, 5, "").ConfigureAwait(false);
        await CheckAsync(["-n", "-r", "\"x\" * 33"], [],
            new JqExecutionPolicy { MaximumStringLength = 32 }, 5, "").ConfigureAwait(false);
        string input = new string('[', 32) + "0" + new string(']', 32);
        await CheckAsync(["-c", "."], Encoding.UTF8.GetBytes(input), JqExecutionPolicy.Default, 0, input + "\n").ConfigureAwait(false);
        string tooDeep = new string('[', 65) + "0" + new string(']', 65);
        await CheckAsync(["-c", "."], Encoding.UTF8.GetBytes(tooDeep), JqExecutionPolicy.Default, 5, "").ConfigureAwait(false);

        async Task CheckAsync(string[] arguments, byte[] bytes, JqExecutionPolicy policy, int status, string output)
        {
            using var host = new BenchmarkHost(bytes, true);
            int exit = await BenchmarkExecution.Bind(arguments).ExecuteAsync(host, policy, cancellationToken).ConfigureAwait(false);
            var result = host.Finish(exit);
            if (result.ExitCode != status || !result.Output.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(output))
                || (status == 0 ? result.Error.Length != 0 : result.Error.Length == 0))
                throw new InvalidOperationException("Stress case failed: " + arguments[^1] + "; exit " + exit);
        }
    }
}
