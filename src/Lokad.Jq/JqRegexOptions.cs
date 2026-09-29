using PCRE;

namespace Lokad.Jq;

/// <summary>jq flags shared by regex predicates and substitutions.</summary>
internal readonly record struct JqRegexOptions(PcreOptions Pattern, PcreMatchOptions Match)
{
    internal static bool TryParse(string flags, out JqRegexOptions options, out char unsupportedFlag)
    {
        var pattern = PcreOptions.None;
        var match = PcreMatchOptions.None;
        foreach (var flag in flags)
        {
            switch (flag)
            {
                case 'g': break; // Match iteration belongs to the caller.
                case 'i': pattern |= PcreOptions.IgnoreCase; break;
                case 'm': pattern |= PcreOptions.MultiLine; break;
                case 's': pattern |= PcreOptions.Singleline; break;
                case 'p': pattern |= PcreOptions.MultiLine | PcreOptions.Singleline; break;
                case 'x': pattern |= PcreOptions.Extended; break;
                case 'n': match |= PcreMatchOptions.NotEmpty; break;
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
