using System;

namespace Lokad.Jq;

// A 1-based line/column position in a jq program. Offsets count UTF-16 code
// units and lines break on LF; columns are UTF-16 units within the line.
internal readonly record struct JqSourceSpan(int Offset, int Line, int Column)
{
    internal static JqSourceSpan FromOffset(string source, int offset)
    {
        ArgumentNullException.ThrowIfNull(source);
        int line = 1;
        int lineStart = 0;
        int limit = Math.Clamp(offset, 0, source.Length);
        for (int index = 0; index < limit; index++)
        {
            if (source[index] == '\n')
            {
                line++;
                lineStart = index + 1;
            }
        }

        return new JqSourceSpan(limit, line, limit - lineStart + 1);
    }
}
