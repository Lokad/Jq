using System;
using System.Collections.Generic;

namespace Lokad.Jq;

// Central name/arity table for the current evaluator. The parser validates
// every function call before execution so unknown names and wrong overloads
// fail at compile time (stage 3) instead of during evaluation (stage 5).
// Runtime filters keep their own checks as defense; this registry is the
// single source for what the parser accepts.
internal static class JqBuiltinRegistry
{
    private static readonly Dictionary<string, (int Minimum, int Maximum)> Definitions = new(StringComparer.Ordinal)
    {
        ["builtins"] = (0, 0),
        ["modulemeta"] = (0, 0),
        ["empty"] = (0, 0),
        ["not"] = (0, 0),
        ["now"] = (0, 0),
        ["env"] = (0, 0),
        ["debug"] = (0, 1),
        ["stderr"] = (0, 0),
        ["error"] = (0, 1),
        ["halt"] = (0, 0),
        ["halt_error"] = (0, 1),
        ["select"] = (1, 1),
        ["length"] = (0, 0),
        ["type"] = (0, 0),
        ["tonumber"] = (0, 0),
        ["toboolean"] = (0, 0),
        ["tostring"] = (0, 0),
        ["utf8bytelength"] = (0, 0),
        ["tojson"] = (0, 0),
        ["fromjson"] = (0, 0),
        ["abs"] = (0, 0),
        ["floor"] = (0, 0),
        ["ceil"] = (0, 0),
        ["round"] = (0, 0),
        ["trunc"] = (0, 0),
        ["nearbyint"] = (0, 0),
        ["rint"] = (0, 0),
        ["fabs"] = (0, 0),
        ["sqrt"] = (0, 0),
        ["cbrt"] = (0, 0),
        ["exp"] = (0, 0),
        ["exp2"] = (0, 0),
        ["exp10"] = (0, 0),
        ["log"] = (0, 0),
        ["log10"] = (0, 0),
        ["log2"] = (0, 0),
        ["sin"] = (0, 0),
        ["cos"] = (0, 0),
        ["tan"] = (0, 0),
        ["asin"] = (0, 0),
        ["acos"] = (0, 0),
        ["atan"] = (0, 0),
        ["sinh"] = (0, 0),
        ["cosh"] = (0, 0),
        ["tanh"] = (0, 0),
        ["asinh"] = (0, 0),
        ["acosh"] = (0, 0),
        ["atanh"] = (0, 0),
        ["pow"] = (2, 2),
        ["atan2"] = (2, 2),
        ["hypot"] = (2, 2),
        ["isinfinite"] = (0, 0),
        ["isnan"] = (0, 0),
        ["isnormal"] = (0, 0),
        ["isfinite"] = (0, 0),
        ["infinite"] = (0, 0),
        ["nan"] = (0, 0),
        ["have_decnum"] = (0, 0),
        ["have_literal_numbers"] = (0, 0),
        ["remainder"] = (2, 2),
        ["drem"] = (2, 2),
        ["fmod"] = (2, 2),
        ["fmax"] = (2, 2),
        ["fmin"] = (2, 2),
        ["fdim"] = (2, 2),
        ["copysign"] = (2, 2),
        ["nextafter"] = (2, 2),
        ["nexttoward"] = (2, 2),
        ["scalb"] = (2, 2),
        ["scalbln"] = (2, 2),
        ["ldexp"] = (2, 2),
        ["jn"] = (2, 2),
        ["yn"] = (2, 2),
        ["fma"] = (3, 3),
        ["modf"] = (0, 0),
        ["frexp"] = (0, 0),
        ["lgamma_r"] = (0, 0),
        ["logb"] = (0, 0),
        ["log1p"] = (0, 0),
        ["expm1"] = (0, 0),
        ["significand"] = (0, 0),
        ["tgamma"] = (0, 0),
        ["gamma"] = (0, 0),
        ["lgamma"] = (0, 0),
        ["erf"] = (0, 0),
        ["erfc"] = (0, 0),
        ["j0"] = (0, 0),
        ["j1"] = (0, 0),
        ["y0"] = (0, 0),
        ["y1"] = (0, 0),
        ["add"] = (0, 1),
        ["flatten"] = (0, 1),
        ["min"] = (0, 0),
        ["max"] = (0, 0),
        ["reverse"] = (0, 0),
        ["contains"] = (1, 1),
        ["inside"] = (1, 1),
        ["indices"] = (1, 1),
        ["index"] = (1, 1),
        ["startswith"] = (1, 1),
        ["endswith"] = (1, 1),
        ["ltrimstr"] = (1, 1),
        ["rtrimstr"] = (1, 1),
        ["trimstr"] = (1, 1),
        ["trim"] = (0, 0),
        ["ltrim"] = (0, 0),
        ["rtrim"] = (0, 0),
        ["explode"] = (0, 0),
        ["implode"] = (0, 0),
        ["split"] = (1, 2),
        ["join"] = (1, 1),
        ["ascii_downcase"] = (0, 0),
        ["ascii_upcase"] = (0, 0),
        ["range"] = (1, 3),
        ["any"] = (0, 2),
        ["all"] = (0, 2),
        ["fromdate"] = (0, 0),
        ["fromdateiso8601"] = (0, 0),
        ["todate"] = (0, 0),
        ["todateiso8601"] = (0, 0),
        ["strptime"] = (1, 1),
        ["strftime"] = (1, 1),
        ["strflocaltime"] = (1, 1),
        ["gmtime"] = (0, 0),
        ["localtime"] = (0, 0),
        ["mktime"] = (0, 0),
        ["input"] = (0, 0),
        ["inputs"] = (0, 0),
        ["tostream"] = (0, 0),
        ["fromstream"] = (1, 1),
        ["truncate_stream"] = (1, 1),
        ["input_filename"] = (0, 0),
        ["input_line_number"] = (0, 0),
        ["test"] = (1, 2),
        ["match"] = (1, 2),
        ["capture"] = (1, 2),
        ["scan"] = (1, 2),
        ["splits"] = (1, 2),
        ["sub"] = (2, 3),
        ["path"] = (1, 1),
        ["del"] = (1, 1),
        ["getpath"] = (1, 1),
        ["setpath"] = (2, 2),
        ["delpaths"] = (1, 1),
        ["pick"] = (1, 1),
        ["limit"] = (2, 2),
        ["first"] = (0, 1),
        ["last"] = (0, 1),
        ["nth"] = (1, 2),
        ["isempty"] = (1, 1),
        ["rindex"] = (1, 1),
        ["skip"] = (2, 2),
        ["while"] = (2, 2),
        ["until"] = (2, 2),
        ["repeat"] = (1, 1),
        ["recurse"] = (0, 2),
        ["walk"] = (1, 1),
        ["paths"] = (0, 1),
        ["keys"] = (0, 0),
        ["keys_unsorted"] = (0, 0),
        ["has"] = (1, 1),
        ["in"] = (1, 1),
        ["map"] = (1, 1),
        ["map_values"] = (1, 1),
        ["arrays"] = (0, 0),
        ["objects"] = (0, 0),
        ["iterables"] = (0, 0),
        ["booleans"] = (0, 0),
        ["numbers"] = (0, 0),
        ["strings"] = (0, 0),
        ["nulls"] = (0, 0),
        ["values"] = (0, 0),
        ["scalars"] = (0, 0),
        ["to_entries"] = (0, 0),
        ["from_entries"] = (0, 0),
        ["with_entries"] = (1, 1),
        ["sort"] = (0, 0),
        ["sort_by"] = (1, 1),
        ["group_by"] = (1, 1),
        ["unique"] = (0, 0),
        ["unique_by"] = (1, 1),
        ["min_by"] = (1, 1),
        ["max_by"] = (1, 1),
        ["transpose"] = (0, 0),
        ["bsearch"] = (1, 1),
        ["combinations"] = (0, 1),
        ["IN"] = (1, 2),
        ["gsub"] = (2, 3),
        ["format"] = (1, 1),
        ["finites"] = (0, 0),
        ["normals"] = (0, 0),
        ["_strindices"] = (1, 1),
        ["_negate"] = (0, 0),
        ["_plus"] = (2, 2),
        ["_minus"] = (2, 2),
        ["_multiply"] = (2, 2),
        ["_divide"] = (2, 2),
        ["_mod"] = (2, 2),
        ["_equal"] = (2, 2),
        ["_notequal"] = (2, 2),
        ["_less"] = (2, 2),
        ["_lesseq"] = (2, 2),
        ["_greater"] = (2, 2),
        ["_greatereq"] = (2, 2),
        ["_assign"] = (2, 2),
        ["_modify"] = (2, 2),
        ["_sort_by_impl"] = (1, 1),
        ["_group_by_impl"] = (1, 1),
        ["_unique_by_impl"] = (1, 1),
        ["_min_by_impl"] = (1, 1),
        ["_max_by_impl"] = (1, 1),
        ["_flatten"] = (1, 1),
        ["_match_impl"] = (3, 3),
        ["INDEX"] = (1, 2),
        ["JOIN"] = (2, 4),
    };

    internal static IReadOnlyList<string> ListAll()
    {
        var names = new List<string>();
        foreach (var entry in Definitions)
        {
            if (entry.Key.StartsWith("_", StringComparison.Ordinal))
                continue;
            for (var arity = entry.Value.Minimum; arity <= entry.Value.Maximum; arity++)
                names.Add(entry.Key + "/" + arity.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        names.Sort(System.StringComparer.Ordinal);
        return names;
    }

    internal static bool TryValidate(string name, int argumentCount, out string error)
    {
        if (!Definitions.TryGetValue(name, out (int Minimum, int Maximum) allowed))
        {
            error = "unsupported function " + name;
            return false;
        }

        if (argumentCount < allowed.Minimum || argumentCount > allowed.Maximum)
        {
            error = FormatArityError(name, allowed.Minimum, allowed.Maximum);
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string FormatArityError(string name, int minimum, int maximum)
    {
        if (minimum == 0 && maximum == 0)
        {
            return name + " expects no arguments";
        }

        if (minimum == 1 && maximum == 1)
        {
            return name + " expects one argument";
        }

        if (minimum == 1 && maximum == 2)
        {
            return name + " expects one or two arguments";
        }

        if (minimum == 2 && maximum == 3)
        {
            return name + " expects two or three arguments";
        }

        if (minimum == 1 && maximum == 3)
        {
            return name + " expects one to three arguments";
        }

        if (minimum == maximum)
        {
            return name + " expects " + minimum + " arguments";
        }

        return name + " expects between " + minimum + " and " + maximum + " arguments";
    }
}
