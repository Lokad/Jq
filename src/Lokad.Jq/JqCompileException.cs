using System;

namespace Lokad.Jq;

// A filter rejected before evaluation, with its source span and program
// identity. The executor reports these at the active stage without leaking
// CLR internals.
internal sealed class JqCompileException(
    string message,
    JqSourceSpan span,
    JqProgramSource programSource) : JqException(message)
{
    public JqSourceSpan Span { get; } = span;

    public JqProgramSource ProgramSource { get; } = programSource ?? throw new ArgumentNullException(nameof(programSource));
}
