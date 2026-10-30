namespace Lokad.Jq;

// jq's scan prepends g; splits and gsub append it. Keep that spelling only
// when rendering an invalid-modifier diagnostic, without constructing flags.
internal enum JqRegexGlobalFlag { None, Prefix, Suffix }

/// <summary>jq flags shared by regex predicates and substitutions.</summary>
internal readonly record struct JqRegexOptions(JqRegexCompileOptions Pattern, JqRegexMatchOptions Match, bool Global)
{
    internal static JqRegexOptions ParseOrThrow(JqString? flags, JqRegexGlobalFlag implicitGlobal, JqBudget budget)
    {
        budget.CheckCancellation();
        ReadOnlySpan<byte> bytes = flags is null ? [] : flags.Utf8.Span;
        if (TryParse(bytes, out JqRegexOptions options, out byte unsupported)) return options;
        if (unsupported == (byte)'l')
            throw new JqException("unsupported regex flag 'l' (longest match is not implemented)");
        int length = JqBudget.BufferCapacity((long)bytes.Length + (implicitGlobal == JqRegexGlobalFlag.None ? 0 : 1)
            + " is not a valid modifier string"u8.Length, 0);
        JqString message = JqString.Create(length, (flags, implicitGlobal), static (output, state) =>
        {
            int written = 0;
            if (state.implicitGlobal == JqRegexGlobalFlag.Prefix) output[written++] = (byte)'g';
            if (state.flags is not null)
            {
                state.flags.Utf8.Span.CopyTo(output[written..]);
                written += state.flags.ByteLength;
            }
            if (state.implicitGlobal == JqRegexGlobalFlag.Suffix) output[written++] = (byte)'g';
            " is not a valid modifier string"u8.CopyTo(output[written..]);
        }, budget);
        throw new JqErrorException(message);

        bool TryParse(ReadOnlySpan<byte> text, out JqRegexOptions parsed, out byte invalid)
        {
            var pattern = JqRegexCompileOptions.None;
            var match = JqRegexMatchOptions.None;
            bool global = implicitGlobal != JqRegexGlobalFlag.None;
            for (int index = 0; index < text.Length; index++)
            {
                if ((index & 4095) == 0) budget.CheckCancellation();
                switch (text[index])
                {
                    case (byte)'g': global = true; break;
                    case (byte)'i': pattern |= JqRegexCompileOptions.Caseless; break;
                    case (byte)'m': pattern |= JqRegexCompileOptions.Multiline; break;
                    case (byte)'s': pattern |= JqRegexCompileOptions.DotAll; break;
                    case (byte)'p': pattern |= JqRegexCompileOptions.Multiline | JqRegexCompileOptions.DotAll; break;
                    case (byte)'x': pattern |= JqRegexCompileOptions.Extended; break;
                    case (byte)'n': match |= JqRegexMatchOptions.NotEmpty; break;
                    default:
                        parsed = default;
                        invalid = text[index];
                        return false;
                }
            }
            budget.CheckCancellation();
            parsed = new(pattern, match, global);
            invalid = default;
            return true;
        }
    }
}
