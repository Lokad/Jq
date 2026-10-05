using System.Globalization;
using System.Runtime.InteropServices;

namespace Lokad.Jq.Benchmarking;

internal sealed record QuietCheck(bool IsQuiet, string Reason, double[] BusyPercent)
{
    public static QuietCheck Evaluate(double[] samples)
    {
        if (samples.Length == 0 || samples.Any(value => !double.IsFinite(value) || value < 0 || value > 100))
            return new(false, "CPU accounting is unavailable; qualification needs a supported quiet machine.", samples);
        bool quiet = samples.Max() <= 5 && PairedStatistics.Median(samples) <= 3;
        return new(quiet, quiet ? "CPU gate passed." : "Background CPU exceeded the 3% median / 5% maximum gate.", samples);
    }
}

internal static class MachineQuietProbe
{
    public static async Task<QuietCheck> CheckAsync(int samples, TimeSpan interval, CancellationToken cancellationToken)
    {
        var values = new List<double>();
        for (int i = 0; i < samples; i++)
        {
            if (!TryRead(out var before)) return QuietCheck.Evaluate([]);
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            if (!TryRead(out var after) || after.Total <= before.Total || after.Idle < before.Idle)
                return QuietCheck.Evaluate([]);
            values.Add(100 * (1 - (double)(after.Idle - before.Idle) / (after.Total - before.Total)));
        }
        return QuietCheck.Evaluate(values.ToArray());

        static bool TryRead(out (ulong Total, ulong Idle) counter)
        {
            counter = default;
            if (OperatingSystem.IsWindows())
            {
                if (!GetSystemTimes(out ulong idle, out ulong kernel, out ulong user)) return false;
                counter = (kernel + user, idle);
                return true;
            }
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    string? row = File.ReadLines("/proc/stat").FirstOrDefault();
                    if (row is null) return false;
                    string[] fields = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 9 || fields[0] != "cpu") return false;
                    ulong[] ticks = new ulong[8];
                    for (int i = 0; i < ticks.Length; i++)
                        if (!ulong.TryParse(fields[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out ticks[i])) return false;
                    counter = (ticks.Aggregate(0UL, (sum, value) => sum + value), ticks[3] + ticks[4]);
                    return true;
                }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
