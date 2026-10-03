using System;

namespace Lokad.Jq;

// Managed Bessel functions matching the reference libm behavior verified
// against jq 1.8.2: J for every real x, Y for x > 0 with the pole at zero
// and NaN outside the domain. Orders truncate toward zero like the
// reference dtoi mapping with reflection for negative orders; recurrences
// run at most MaxOrder steps with periodic cancellation checks, and larger
// or non-finite orders take documented boundary values. Small arguments
// use ascending series (Y1 via termwise differentiation of the Y0 series)
// and large arguments use Hankel asymptotics; Jn/Yn for higher orders use
// Miller backward recurrence (J) and forward recurrence (Y).
internal static class JqBessel
{
    private const double EulerMascheroni = 0.5772156649015329;
    private const long MaxOrder = 100000;
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
        if (double.IsNaN(order))
            return double.NaN;
        if (double.IsNaN(x) || double.IsInfinity(x))
            return double.NaN;
        if (Math.Abs(order) > MaxOrder)
            return 0.0;
        long n = (long)Math.Truncate(order);
        if (n < 0)
        {
            double inner = Jn(budget, -n, x);
            return double.IsNaN(inner) ? inner : (n % 2 == 0 ? inner : -inner);
        }
        if (x < 0.0)
        {
            double inner = Jn(budget, n, -x);
            return double.IsNaN(inner) ? inner : (n % 2 == 0 ? inner : -inner);
        }
        if (x == 0.0)
            return n == 0 ? 1.0 : 0.0;
        if (n == 0)
            return J0(x);
        if (n == 1)
            return J1(x);
        if (x < n)
            return BesselMiller(budget, n, x);
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

    internal static double Yn(JqBudget budget, double order, double x)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (double.IsNaN(order))
            return double.NaN;
        if (double.IsNaN(x) || double.IsInfinity(x) || x < 0.0)
            return double.NaN;
        if (x == 0.0)
            return order == 0.0 ? double.NegativeInfinity : double.NaN;
        if (Math.Abs(order) > MaxOrder)
            return order > 0.0 ? double.NegativeInfinity : OrderParity(order) ? double.PositiveInfinity : double.NegativeInfinity;
        long n = (long)Math.Truncate(order);
        if (n < 0)
        {
            double inner = Yn(budget, -n, x);
            return double.IsNaN(inner) ? inner : (n % 2 == 0 ? inner : -inner);
        }
        if (n == 0)
            return Y0(x);
        if (n == 1)
            return Y1(x);
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

    // Sign of (-1)^order for huge finite orders, which are all even past 2^52.
    private static bool OrderParity(double order) =>
        Math.Abs(order) < 4503599627370496.0 && Math.Abs((long)order % 2) == 1;

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
        double logTerm = Math.Log(x / 2.0) + EulerMascheroni;
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
        double derivative = (2.0 / x) * termb;
        for (int k = 2; k <= 100; k++)
        {
            termb *= unit / (k * k);
            harmonicb += 1.0 / k;
            double addition = (k % 2 == 1 ? 1.0 : -1.0) * harmonicb * (2.0 * k / x) * termb;
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

    // Miller backward recurrence for 0 < x < n with exact power-of-two
    // rescaling, normalized by J0 (or J1 at J0 zeros).
    private static double BesselMiller(JqBudget budget, long n, double x)
    {
        long top = n + 25;
        double next = 0.0;
        double current = 1.0;
        long rescale = 0;
        double atN = 0.0;
        long atNScale = 0;
        double at0 = 0.0;
        long at0Scale = 0;
        double at1 = 0.0;
        long at1Scale = 0;
        for (long k = top; k >= 1; k--)
        {
            if ((k & (CancelStride - 1)) == 0)
                budget.CheckCancellation();
            double previous = (2.0 * k / x) * current - next;
            next = current;
            current = previous;
            double peak = Math.Abs(current) > Math.Abs(next) ? Math.Abs(current) : Math.Abs(next);
            if (peak >= 1.2676506002282294E+30)
            {
                next *= 7.888609052210118E-31;
                current *= 7.888609052210118E-31;
                rescale++;
            }
            else if (peak > 0.0 && peak < 7.888609052210118E-31)
            {
                next *= 1.2676506002282294E+30;
                current *= 1.2676506002282294E+30;
                rescale--;
            }
            if (k - 1 == n)
            {
                atN = current;
                atNScale = rescale;
            }
            if (k - 1 == 0)
            {
                at0 = current;
                at0Scale = rescale;
            }
            if (k - 1 == 1)
            {
                at1 = current;
                at1Scale = rescale;
            }
        }
        double reference = BesselJSeries(x, 0);
        double referenceValue = at0;
        long referenceScale = at0Scale;
        if (Math.Abs(reference) <= 1e-8)
        {
            reference = BesselJSeries(x, 1);
            referenceValue = at1;
            referenceScale = at1Scale;
        }
        double scaled = (atN / referenceValue) * Math.Pow(2.0, 100.0 * (atNScale - referenceScale));
        return reference * scaled;
    }
}
