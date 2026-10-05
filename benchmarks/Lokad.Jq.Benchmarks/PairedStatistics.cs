namespace Lokad.Jq.Benchmarking;

internal sealed record PairedSample(bool LibraryFirst, int LibraryIterations, int ReferenceIterations,
    double LibraryTotalMs, double ReferenceTotalMs)
{
    public double LibraryMs => LibraryTotalMs / LibraryIterations;
    public double ReferenceMs => ReferenceTotalMs / ReferenceIterations;
}

internal sealed record TimingSummary(string Status, string Reason, double LibraryMedianMs,
    double ReferenceMedianMs, double Ratio, double RatioLow, double RatioHigh, string Interpretation);

internal static class PairedStatistics
{
    public static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 0) return 0;
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }

    public static TimingSummary Summarize(IReadOnlyList<PairedSample> samples)
    {
        if (samples.Count < 9 || samples.Any(sample => sample.LibraryIterations <= 0 || sample.ReferenceIterations <= 0
            || !double.IsFinite(sample.LibraryTotalMs) || !double.IsFinite(sample.ReferenceTotalMs)
            || sample.LibraryTotalMs < 20 || sample.ReferenceTotalMs < 20))
            return Unqualified("Need at least nine valid pairs with both samples at least 20 ms.");
        double[] library = samples.Select(sample => sample.LibraryMs).ToArray();
        double[] reference = samples.Select(sample => sample.ReferenceMs).ToArray();
        double[] logs = samples.Select(sample => Math.Log(sample.ReferenceMs / sample.LibraryMs)).ToArray();
        var first = samples.Where(sample => sample.LibraryFirst).Select(sample => Math.Log(sample.ReferenceMs / sample.LibraryMs)).ToArray();
        var second = samples.Where(sample => !sample.LibraryFirst).Select(sample => Math.Log(sample.ReferenceMs / sample.LibraryMs)).ToArray();
        if (first.Length < 4 || second.Length < 4) return Unqualified("Comparator order is insufficiently balanced.");
        if (Math.Exp(Math.Abs(Median(first) - Median(second))) > 1.10)
            return Unqualified("Ratio changes by more than 10% with comparator order.");
        if (RelativeSpread(library) > .10 || RelativeSpread(reference) > .10)
            return Unqualified("A lane's interquartile spread exceeds 10% of its median.");
        double[] bootstrap = new double[10_000];
        double[] draw = new double[logs.Length];
        var random = new Random(1729);
        for (int i = 0; i < bootstrap.Length; i++)
        {
            for (int j = 0; j < draw.Length; j++) draw[j] = logs[random.Next(logs.Length)];
            bootstrap[i] = Math.Exp(Median(draw));
        }
        Array.Sort(bootstrap);
        double low = bootstrap[249], high = bootstrap[9749];
        if (high / low > 1.15) return Unqualified("The ratio confidence interval is wider than 15%.");
        return new("Qualified", "", Median(library), Median(reference), Math.Exp(Median(logs)), low, high,
            low > 1 ? "Lokad.Jq faster" : high < 1 ? "jq faster" : "Inconclusive");

        TimingSummary Unqualified(string reason) => new("Unqualified", reason,
            Median(samples.Where(sample => sample.LibraryIterations > 0 && double.IsFinite(sample.LibraryTotalMs) && sample.LibraryTotalMs > 0).Select(sample => sample.LibraryMs)),
            Median(samples.Where(sample => sample.ReferenceIterations > 0 && double.IsFinite(sample.ReferenceTotalMs) && sample.ReferenceTotalMs > 0).Select(sample => sample.ReferenceMs)),
            0, 0, 0, "No performance claim");

        static double RelativeSpread(double[] values)
        {
            double[] sorted = values.Order().ToArray();
            return (sorted[(int)Math.Floor((sorted.Length - 1) * .75)] - sorted[(int)Math.Floor((sorted.Length - 1) * .25)]) / Median(sorted);
        }
    }

}
