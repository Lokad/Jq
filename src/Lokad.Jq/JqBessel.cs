using System;

namespace Lokad.Jq;

// Managed Bessel functions matching the reference libm behavior verified
// against jq 1.8.2: J for every real x, Y for x > 0 with the pole at zero
// and NaN outside the domain. Orders truncate toward zero with reflection
// for negative orders. Recurrences use
// signed 64-bit integer indices and cooperative cancellation.
// Orders outside index representation use only proven boundary results;
// otherwise they return NaN. Small arguments use ascending series (Y1 via termwise differentiation of the Y0 series)
// and large arguments use Hankel asymptotics; Jn/Yn for higher orders use
// Miller backward recurrence (J) and forward recurrence (Y).
internal static class JqBessel
{
    private const double EulerMascheroni = 0.5772156649015329;
    private const int CancelStride = 4096;

    internal static double J0(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x))
            return double.NaN;
        double ax = Math.Abs(x);
        return ax <= 10.0 ? BesselJSeries(ax, 0) : BesselHankel(ax, 0, false);
    }

    internal static double J1(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x))
            return double.NaN;
        if (x == 0.0) return x;
        double ax = Math.Abs(x);
        double value = ax <= 10.0 ? BesselJSeries(ax, 1) : BesselHankel(ax, 1, false);
        return x < 0.0 ? -value : value;
    }

    internal static double Y0(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x) || x < 0.0)
            return double.NaN;
        if (x == 0.0)
            return double.NegativeInfinity;
        return x <= 10.0 ? BesselYSeries(x, 0) : BesselHankel(x, 0, true);
    }

    internal static double Y1(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x) || x < 0.0)
            return double.NaN;
        if (x == 0.0)
            return double.NegativeInfinity;
        return x <= 10.0 ? BesselYSeries(x, 1) : BesselHankel(x, 1, true);
    }

    internal static double Jn(JqBudget budget, double order, double x)
    {
        ArgumentNullException.ThrowIfNull(budget);
        budget.CheckCancellation();
        if (double.IsNaN(order))
            return double.NaN;
        if (double.IsNaN(x) || double.IsInfinity(x))
            return double.NaN;
        if (double.IsInfinity(order)) return 0.0;
        double magnitude = Math.Truncate(Math.Abs(order));
        if (magnitude == 1.0) return order < 0.0 ? -J1(x) : J1(x);
        double point = Math.Abs(x);
        bool negative = magnitude % 2.0 != 0.0 && ((order < 0.0) != (x < 0.0));
        if (point == 0.0) return magnitude == 0.0 ? 1.0 : negative ? -0.0 : 0.0;
        if (magnitude >= 2.0 && point < magnitude && Underflows(magnitude, point))
            return negative ? -0.0 : 0.0;
        // The loop index must represent the integer order.
        if (magnitude >= -(double)long.MinValue) return double.NaN;
        long n = (long)magnitude;
        double value = Positive(n, point);
        return negative ? -value : value;

        double Positive(long n, double x)
        {
            if (n == 0) return J0(x);
            if (x < n) return Backward(n, x);
            double previous = J0(x);
            double current = J1(x);
            for (long k = 1; k < n; k++)
            {
                if ((k & (CancelStride - 1)) == 0)
                    budget.CheckCancellation();
                double next = (2.0 * k / x) * current - previous;
                previous = current;
                current = next;
            }
            return current;
        }

        // DLMF 10.10.1 supplies Jn/J(n-1) with a converged continued
        // fraction. Backward recurrence then uses J0/J1 normalization; rescale
        // before multiplication so tiny arguments cannot overflow intermediate state.
        double Backward(long n, double x)
        {
            double fraction = 2.0 * n / x;
            if (double.IsPositiveInfinity(fraction)) return 0.0;
            double c = fraction;
            double d = 0.0;
            long terms = 0;
            while (true)
            {
                if ((terms & (CancelStride - 1)) == 0) budget.CheckCancellation();
                terms++;
                double b = 2.0 * (n + (double)terms) / x;
                d = 1.0 / (b - d);
                c = b - 1.0 / c;
                double delta = c * d;
                fraction *= delta;
                if (Math.Abs(delta - 1.0) <= 8.881784197001252e-16) break;
            }
            double atN = 1.0 / fraction;
            double higher = atN;
            double lower = 1.0;
            int scale = 0;
            for (long k = n - 1; k >= 1; k--)
            {
                if ((k & (CancelStride - 1)) == 0) budget.CheckCancellation();
                double factor = 2.0 * k / x;
                if (Math.Max(Math.Abs(lower), Math.Abs(higher)) > Math.ScaleB(1.0, 500) / factor)
                {
                    lower = Math.ScaleB(lower, -512);
                    higher = Math.ScaleB(higher, -512);
                    scale -= 512;
                }
                double next = factor * lower - higher;
                higher = lower;
                lower = next;
            }
            double j0 = J0(x);
            double j1 = J1(x);
            double normalized = Math.Abs(j0) >= Math.Abs(j1) ? atN / lower * j0 : atN / higher * j1;
            return Math.ScaleB(normalized, scale);
        }

        bool Underflows(double n, double x)
        {
            // DLMF 10.14.5 bounds |Jn(x)| by exp(-n * eta) for 0 < x < n.
            // Stay outside rounding uncertainty at half the least subnormal.
            double z = x / n;
            double t = Math.Sqrt(((n - x) / n) * (1.0 + z));
            double eta;
            if (t < 0.25)
            {
                // atanh(t) - t without cancellation near the transition x = n.
                double power = t * t * t;
                double sum = 0.0;
                for (int k = 3; ; k += 2)
                {
                    double next = sum + power / k;
                    if (next == sum) break;
                    sum = next;
                    power *= t * t;
                }
                eta = sum;
            }
            else eta = Math.Acosh(n / x) - t;
            return n * eta > (Math.Log(2.0) - Math.Log(double.Epsilon)) * (1.0 + 1e-12);
        }
    }

    internal static double Yn(JqBudget budget, double order, double x)
    {
        ArgumentNullException.ThrowIfNull(budget);
        budget.CheckCancellation();
        if (double.IsNaN(order))
            return double.NaN;
        if (double.IsNaN(x) || double.IsInfinity(x) || x < 0.0)
            return double.NaN;
        double magnitude = Math.Truncate(Math.Abs(order));
        if (magnitude == 1.0) return order < 0.0 ? -Y1(x) : Y1(x);
        if (x == 0.0)
            return order == 0.0 ? double.NegativeInfinity : double.NaN;
        if (double.IsInfinity(order)) return double.NegativeInfinity;
        if (magnitude >= -(double)long.MinValue)
        {
            // For 0 < x <= 1, forward Y recurrence grows in magnitude by at
            // least k/x per step after its first terms. At these orders the
            // result necessarily overflows. Other unrepresentable orders are NaN.
            return x <= 1.0 ? double.NegativeInfinity : double.NaN;
        }
        long n = (long)magnitude;
        double value = Positive(n);
        return order < 0.0 && (n & 1) != 0 ? -value : value;

        double Positive(long n)
        {
            if (n == 0)
                return Y0(x);
            double previous = Y0(x);
            double current = Y1(x);
            for (long k = 1; k < n; k++)
            {
                if ((k & (CancelStride - 1)) == 0)
                    budget.CheckCancellation();
                double next = (2.0 * k / x) * current - previous;
                if (!double.IsFinite(next))
                    return next;
                previous = current;
                current = next;
            }
            return current;
        }
    }

    // Ascending J series for order 0 or 1 with term recurrence.
    private static double BesselJSeries(double x, int nu)
    {
        double half = x / 2.0;
        double power = 1.0;
        for (int k = 0; k < nu; k++)
            power *= half;
        double factorial = 1.0;
        for (int k = 1; k <= nu; k++)
            factorial *= k;
        double term = power / factorial;
        double sum = term;
        double square = half * half;
        for (int k = 1; k <= 100; k++)
        {
            term *= -square / (k * (k + nu));
            sum += term;
            if (Math.Abs(term) <= 1e-17 * Math.Abs(sum))
                break;
        }
        return sum;
    }

    // Ascending Y0/Y1 for 0 < x <= 8 from Abramowitz and Stegun 9.1.12 with
    // Y1 via termwise differentiation of the Y0 series.
    private static double BesselYSeries(double x, int nu)
    {
        double logTerm = Math.Log(x) - Math.Log(2.0) + EulerMascheroni;
        double half = x / 2.0;
        double unit = half * half;
        if (nu == 0)
        {
            double j0 = BesselJSeries(x, 0);
            double term = unit;
            double harmonic = 1.0;
            double series = term;
            for (int k = 2; k <= 100; k++)
            {
                term *= unit / (k * k);
                harmonic += 1.0 / k;
                double addition = (k % 2 == 1 ? 1.0 : -1.0) * harmonic * term;
                series += addition;
                if (Math.Abs(addition) <= 1e-17 * Math.Abs(series))
                    break;
            }
            return (2.0 / Math.PI) * (logTerm * j0 + series);
        }
        double j0b = BesselJSeries(x, 0);
        double j1 = BesselJSeries(x, 1);
        double termb = unit;
        double harmonicb = 1.0;
        double derivative = half;
        for (int k = 2; k <= 100; k++)
        {
            termb *= unit / (k * k);
            harmonicb += 1.0 / k;
            double addition = (k % 2 == 1 ? 1.0 : -1.0) * harmonicb * (2.0 * k * termb / x);
            derivative += addition;
            if (Math.Abs(addition) <= 1e-17 * Math.Abs(derivative))
                break;
        }
        return (2.0 / Math.PI) * (logTerm * j1 - j0b / x) - (2.0 / Math.PI) * derivative;
    }

    // Hankel asymptotics for x > 8 (DLMF 10.17.3-5): a single product
    // chain builds a_k = [(4n^2-1^2)...(4n^2-(2k-1)^2)] / (k! 8^k), whose
    // even terms form P and odd terms form Q with alternating signs.
    private static double BesselHankel(double x, int nu, bool isY)
    {
        double mu = 4.0 * nu * nu;
        double inverse = 1.0 / x;
        double term = 1.0;
        double p = 1.0;
        double q = 0.0;
        for (int k = 1; k <= 14; k++)
        {
            term *= (mu - (2.0 * k - 1.0) * (2.0 * k - 1.0)) / (k * 8.0) * inverse;
            if (k % 2 == 0)
                p += ((k / 2) % 2 == 1 ? -1.0 : 1.0) * term;
            else
                q += (((k - 1) / 2) % 2 == 1 ? -1.0 : 1.0) * term;
        }
        double chi = x - nu * Math.PI / 2.0 - Math.PI / 4.0;
        double amplitude = Math.Sqrt(2.0 / (Math.PI * x));
        double cosine = Math.Cos(chi);
        double sine = Math.Sin(chi);
        return isY ? amplitude * (p * sine + q * cosine) : amplitude * (p * cosine - q * sine);
    }

}
