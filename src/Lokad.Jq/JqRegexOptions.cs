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
                case 's': break; // Anchors already refer to the whole string.
                case 'i': pattern |= PcreOptions.IgnoreCase; break;
                case 'm':
                case 'p': pattern |= PcreOptions.DotAll; break;
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
}
