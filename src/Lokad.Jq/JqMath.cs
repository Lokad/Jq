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
        ["acosh"] = Math.Acosh,
        ["atanh"] = Math.Atanh,
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
    // null, booleans, and negative numbers take the negation branch.
    private static JsonNode? Abs(JqContext context, JsonNode? input)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Compare(input, JsonValue.Create(0)) < 0)
        {
            if (TypeName(input) == "number")
                return JsonValue.Create(-Number(input));
            throw new JqRuntimeException(TypeName(input) + " (" + context.Runtime.Serialize(input, false, null, false) + ") cannot be negated");
        }
        return context.Runtime.Clone(input);
    }

    private static bool Classify(string name, JsonNode? input)
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
}
