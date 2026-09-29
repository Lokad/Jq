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
        ["empty"] = (0, 0),
        ["not"] = (0, 0),
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
        ["sqrt"] = (0, 0),
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
        ["last"] = (0, 0),
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
    };

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
