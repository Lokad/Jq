using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using static Lokad.Jq.JqRuntime;

namespace Lokad.Jq;

// Double-domain math builtins mirroring the reference libm registry.
// Single-input functions read the input; two- and three-input functions
// ignore it and read value arguments. Operands use kind checks so NaN
// stays a number; failures use the operand-shaped number required error.
internal static class JqMath
{
    private static readonly Dictionary<string, Func<double, double>> Unary = new(StringComparer.Ordinal)
    {
        ["floor"] = Math.Floor,
        ["ceil"] = Math.Ceiling,
        ["round"] = static value => Math.Round(value, MidpointRounding.AwayFromZero),
        ["trunc"] = Math.Truncate,
        ["nearbyint"] = static value => Math.Round(value, MidpointRounding.ToEven),
        ["rint"] = static value => Math.Round(value, MidpointRounding.ToEven),
        ["fabs"] = Math.Abs,
        ["sqrt"] = Math.Sqrt,
        ["cbrt"] = Math.Cbrt,
        ["exp"] = Math.Exp,
        ["exp2"] = static value => Math.Pow(2.0, value),
        ["exp10"] = static value => Math.Pow(10.0, value),
        ["log"] = Math.Log,
        ["log10"] = Math.Log10,
        ["log2"] = Math.Log2,
        ["sin"] = Math.Sin,
        ["cos"] = Math.Cos,
        ["tan"] = Math.Tan,
        ["asin"] = Math.Asin,
        ["acos"] = Math.Acos,
        ["atan"] = Math.Atan,
        ["sinh"] = Math.Sinh,
        ["cosh"] = Math.Cosh,
        ["tanh"] = Math.Tanh,
        ["asinh"] = Math.Asinh,
        ["atanh"] = Math.Atanh,
        ["acosh"] = Math.Acosh,
        ["logb"] = Logb,
        ["log1p"] = Log1p,
        ["expm1"] = Expm1,
        ["significand"] = Significand,
        ["tgamma"] = Gamma,
        ["gamma"] = Gamma,
        ["lgamma"] = static value => LogGammaSigned(value).Value,
        ["erf"] = Erf,
        ["erfc"] = static value => 1.0 - Erf(value),
    };

    private static readonly Dictionary<string, Func<double, double, double>> Binary = new(StringComparer.Ordinal)
    {
        ["pow"] = Math.Pow,
        ["atan2"] = Math.Atan2,
        ["hypot"] = static (x, y) =>
        {
            if (double.IsInfinity(x) || double.IsInfinity(y))
                return double.PositiveInfinity;
            if (double.IsNaN(x) || double.IsNaN(y))
                return double.NaN;
            double large = Math.Max(Math.Abs(x), Math.Abs(y));
            double small = Math.Min(Math.Abs(x), Math.Abs(y));
            if (large == 0.0)
                return 0.0;
            double ratio = small / large;
            return large * Math.Sqrt(1.0 + ratio * ratio);
        },
        ["remainder"] = Math.IEEERemainder,
        ["drem"] = Math.IEEERemainder,
        ["fmod"] = static (x, y) => x % y,
        ["fmax"] = Fmax,
        ["fmin"] = Fmin,
        ["fdim"] = Fdim,
        ["copysign"] = Math.CopySign,
        ["nextafter"] = NextAfter,
        ["nexttoward"] = NextAfter,
        ["scalb"] = Scalbn,
        ["scalbln"] = Scalbn,
        ["ldexp"] = Scalbn,
    };

    internal static bool TryEvaluate(JqContext context, string name, JsonNode?[] combo, JsonNode? input, out JsonNode? result)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(combo);
        result = null;
        if (Unary.TryGetValue(name, out Func<double, double>? unary))
        {
            if (combo.Length != 0)
                return false;
            result = JsonValue.Create(unary(RequireNumber(context.Runtime, input)));
            return true;
        }
        if (Binary.TryGetValue(name, out Func<double, double, double>? binary))
        {
            if (combo.Length != 2)
                return false;
            double first = RequireNumber(context.Runtime, combo[0]);
            double second = RequireNumber(context.Runtime, combo[1]);
            result = JsonValue.Create(binary(first, second));
            return true;
        }
        switch (name)
        {
            case "abs":
                if (combo.Length != 0)
                    return false;
                result = Abs(context, input);
                return true;
            case "isinfinite":
            case "isnan":
            case "isnormal":
            case "isfinite":
                if (combo.Length != 0)
                    return false;
                result = JsonValue.Create(Classify(name, input));
                return true;
            case "infinite":
                if (combo.Length != 0)
                    return false;
                result = JsonValue.Create(double.PositiveInfinity);
                return true;
            case "nan":
                if (combo.Length != 0)
                    return false;
                result = JsonValue.Create(double.NaN);
                return true;
            case "have_decnum":
            case "have_literal_numbers":
                if (combo.Length != 0)
                    return false;
                result = JsonValue.Create(false);
                return true;
            case "fma":
                if (combo.Length != 3)
                    return false;
                result = JsonValue.Create(Math.FusedMultiplyAdd(RequireNumber(context.Runtime, combo[0]), RequireNumber(context.Runtime, combo[1]), RequireNumber(context.Runtime, combo[2])));
                return true;
            case "modf":
                if (combo.Length != 0)
                    return false;
                result = ModfArray(RequireNumber(context.Runtime, input));
                return true;
            case "frexp":
                if (combo.Length != 0)
                    return false;
                result = FrexpArray(RequireNumber(context.Runtime, input));
                return true;
            case "lgamma_r":
                if (combo.Length != 0)
                    return false;
                (double lgammaValue, int lgammaSign) = LogGammaSigned(RequireNumber(context.Runtime, input));
                result = new JsonArray(JsonValue.Create(lgammaValue), JsonValue.Create(lgammaSign));
                return true;
            case "j0":
            case "j1":
            case "y0":
            case "y1":
                if (combo.Length != 0)
                    return false;
                throw Unavailable(name, 0);
            case "jn":
            case "yn":
                if (combo.Length != 2)
                    return false;
                throw Unavailable(name, 2);
            default:
                return false;
        }
    }

    private static double RequireNumber(JqRuntime runtime, JsonNode? node)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (TypeName(node) == "number")
            return Number(node);
        throw new JqRuntimeException(TypeName(node) + " (" + runtime.Serialize(node, false, null, false) + ") number required");
    }

    // The reference defines abs as if . < 0 then -. else . end over the total
    // order: strings, arrays, and objects above numbers pass through, while
    // null, booleans, and negative numbers take the negation branch. The reference
    // decimal order ranks -0 below +0, so abs(-0) negates to +0; that quirk
    // stays local to abs (ordering keeps signed zeros equal) while the result
    // matches the upstream vector.
    private static JsonNode? Abs(JqContext context, JsonNode? input)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Compare(input, JsonValue.Create(0)) < 0)
        {
            if (TypeName(input) == "number")
                return JsonValue.Create(-Number(input));
            throw new JqRuntimeException(TypeName(input) + " (" + context.Runtime.Serialize(input, false, null, false) + ") cannot be negated");
        }
        if (TypeName(input) == "number" && Number(input) == 0.0 && double.IsNegative(Number(input)))
            return JsonValue.Create(0.0);
        return context.Runtime.Clone(input);
    }

    internal static bool Classify(string name, JsonNode? input)
    {
        if (TypeName(input) != "number")
            return false;
        double value = Number(input);
        return name switch
        {
            "isinfinite" => double.IsInfinity(value),
            "isnan" => double.IsNaN(value),
            "isnormal" => value != 0.0 && !double.IsNaN(value) && !double.IsInfinity(value) && !double.IsSubnormal(value),
            _ => double.IsFinite(value),
        };
    }

    private static double Fmax(double x, double y)
    {
        if (double.IsNaN(x))
            return y;
        if (double.IsNaN(y))
            return x;
        return Math.Max(x, y);
    }

    private static double Fmin(double x, double y)
    {
        if (double.IsNaN(x))
            return y;
        if (double.IsNaN(y))
            return x;
        return Math.Min(x, y);
    }

    private static double Fdim(double x, double y)
    {
        if (double.IsNaN(x) || double.IsNaN(y))
            return double.NaN;
        if (x <= y)
            return 0.0;
        return x - y;
    }

    private static double NextAfter(double x, double y)
    {
        if (double.IsNaN(x) || double.IsNaN(y))
            return double.NaN;
        if (x == y)
            return y;
        return y > x ? Math.BitIncrement(x) : Math.BitDecrement(x);
    }

    private static double Scalbn(double x, double exp)
    {
        if (double.IsNaN(x) || double.IsNaN(exp))
            return double.NaN;
        double truncated = Math.Truncate(exp);
        int bounded = truncated >= int.MaxValue ? int.MaxValue : truncated <= int.MinValue ? int.MinValue : (int)truncated;
        return Math.ScaleB(x, bounded);
    }

    private static double Logb(double x)
    {
        if (x == 0.0)
            return double.NegativeInfinity;
        if (double.IsInfinity(x))
            return double.PositiveInfinity;
        if (double.IsNaN(x))
            return double.NaN;
        return (double)Math.ILogB(x);
    }

    private static double Log1p(double x)
    {
        if (x == -1.0)
            return double.NegativeInfinity;
        if (double.IsNaN(x) || x < -1.0 || double.IsPositiveInfinity(x))
            return double.IsPositiveInfinity(x) ? double.PositiveInfinity : double.NaN;
        double u = 1.0 + x;
        return Math.Log(u) - ((u - 1.0) - x) / u;
    }

    private static double Expm1(double x)
    {
        if (double.IsNaN(x) || double.IsPositiveInfinity(x))
            return x;
        if (double.IsNegativeInfinity(x))
            return -1.0;
        if (Math.Abs(x) < 0.5)
        {
            double term = x;
            double sum = x;
            for (int n = 2; n <= 20; n++)
            {
                term *= x / n;
                sum += term;
            }
            return sum;
        }
        return Math.Exp(x) - 1.0;
    }

    private static double Significand(double x)
    {
        long negated = -(long)Math.ILogB(x);
        int shift = negated > int.MaxValue ? int.MaxValue : (int)negated;
        return Math.ScaleB(x, shift);
    }

    private static JsonNode ModfArray(double x)
    {
        if (double.IsNaN(x))
            return new JsonArray(JsonValue.Create(double.NaN), JsonValue.Create(double.NaN));
        if (double.IsInfinity(x))
            return new JsonArray(JsonValue.Create(Math.CopySign(0.0, x)), JsonValue.Create(x));
        double integral = Math.Truncate(x);
        return new JsonArray(JsonValue.Create(x - integral), JsonValue.Create(integral));
    }

    private static JsonNode FrexpArray(double x)
    {
        if (x == 0.0 || double.IsNaN(x) || double.IsInfinity(x))
            return new JsonArray(JsonValue.Create(x), JsonValue.Create(0));
        int exponent = Math.ILogB(x) + 1;
        return new JsonArray(JsonValue.Create(Math.ScaleB(x, -exponent)), JsonValue.Create(exponent));
    }

    private static double Gamma(double x)
    {
        if (x < 0.0 && x == Math.Floor(x))
            return double.NaN;
        (double logAbs, int sign) = LogGammaSigned(x);
        if (double.IsNaN(logAbs))
            return double.NaN;
        if (double.IsPositiveInfinity(logAbs))
            return sign >= 0 ? double.PositiveInfinity : double.NegativeInfinity;
        return sign * Math.Exp(logAbs);
    }

    private static (double Value, int Sign) LogGammaSigned(double x)
    {
        if (double.IsNaN(x))
            return (double.NaN, 1);
        if (double.IsInfinity(x))
            return (double.PositiveInfinity, 1);
        if (x == 0.0)
            return (double.PositiveInfinity, double.IsNegative(x) ? -1 : 1);
        if (x < 0.0)
        {
            if (x == Math.Floor(x))
            {
                double magnitude = -x;
                int poleSign = magnitude < 9007199254740992.0 ? (long)magnitude % 2 == 0 ? 1 : -1 : 1;
                return (double.PositiveInfinity, poleSign);
            }
            double sine = Math.Sin(Math.PI * x);
            (double rest, _) = LogGammaSigned(1.0 - x);
            double value = Math.Log(Math.PI / Math.Abs(sine)) - rest;
            return (value, sine > 0.0 ? 1 : -1);
        }
        return (LogGammaLanczos(x), 1);
    }

    private static double LogGammaLanczos(double x)
    {
        if (x < 0.5)
            return LogGammaLanczos(x + 1.0) - Math.Log(x);
        double z = x - 1.0;
        double sum = LanczosCoefficients[0];
        for (int k = 1; k < LanczosCoefficients.Length; k++)
            sum += LanczosCoefficients[k] / (z + k);
        double t = z + 7.5;
        return 0.5 * Math.Log(2.0 * Math.PI) + ((z + 0.5) * Math.Log(t)) - t + Math.Log(sum);
    }

    private static readonly double[] LanczosCoefficients =
    [
        0.99999999999980993,
        676.5203681218851,
        -1259.1392167224028,
        771.32342877765313,
        -176.61502916214059,
        12.507343278686905,
        -0.13857109526572012,
        9.9843695780195716e-6,
        1.5056327351493116e-7,
    ];

    private static double Erf(double x)
    {
        if (double.IsNaN(x))
            return double.NaN;
        if (double.IsPositiveInfinity(x))
            return 1.0;
        if (double.IsNegativeInfinity(x))
            return -1.0;
        if (x < 0.0)
            return -ErfCore(-x);
        if (x == 0.0)
            return x;
        return ErfCore(x);
    }

    private static double ErfCore(double x)
    {
        double t = 1.0 / (1.0 + 0.3275911 * x);
        double poly = ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592);
        return 1.0 - (poly * t * Math.Exp(-x * x));
    }

    private static Exception Unavailable(string name, int arity) =>
        new JqException("Error: " + name + "/" + arity + " not found at build time");
}
