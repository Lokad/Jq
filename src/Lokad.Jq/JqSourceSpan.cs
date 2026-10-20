namespace Lokad.Jq;

// A 1-based position in a jq program. Lines break on LF; columns count UTF-16 units.
internal readonly record struct JqSourceSpan(int Line, int Column);
