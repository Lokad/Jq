using Lokad.Utf8Regex.Pcre2;

namespace Lokad.Jq;

/// <summary>jq flags shared by regex predicates and substitutions.</summary>
internal readonly record struct JqRegexOptions(Pcre2CompileOptions Pattern, Pcre2MatchOptions Match)
{
    internal static bool TryParse(string flags, out JqRegexOptions options, out char unsupportedFlag)
    {
        var pattern = Pcre2CompileOptions.None;
        var match = Pcre2MatchOptions.None;
        foreach (var flag in flags)
        {
            switch (flag)
            {
                case 'g': break; // Match iteration belongs to the caller.
                case 'i': pattern |= Pcre2CompileOptions.Caseless; break;
                case 'm': pattern |= Pcre2CompileOptions.Multiline; break;
                case 's': pattern |= Pcre2CompileOptions.DotAll; break;
                case 'p': pattern |= Pcre2CompileOptions.Multiline | Pcre2CompileOptions.DotAll; break;
                case 'x': pattern |= Pcre2CompileOptions.Extended; break;
                case 'n': match |= Pcre2MatchOptions.NotEmpty; break;
                default:
                    options = default;
                    unsupportedFlag = flag;
                    return false;
            }
        }

        options = new JqRegexOptions(pattern, match);
        unsupportedFlag = default;
        return true;
    }

    // Upstream accepts every flag above plus l for longest match, which has
    // no PCRE2 equivalent and stays explicitly unimplemented. Invalid flags
    // fail with the reference modifier diagnostic shared by all callers.
    internal static JqRegexOptions ParseOrThrow(string flags)
    {
        if (!TryParse(flags, out JqRegexOptions options, out char unsupported))
        {
            if (unsupported == 'l')
                throw new JqException("unsupported regex flag 'l' (longest match is not implemented)");
            throw new JqException(flags + " is not a valid modifier string");
        }
        return options;
    }
}
