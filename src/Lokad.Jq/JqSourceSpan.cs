namespace Lokad.Jq;

// A 1-based line/column position in a jq program. Offsets count UTF-16 code
// units and lines break on LF; columns are UTF-16 units within the line.
internal readonly record struct JqSourceSpan(int Offset, int Line, int Column);
