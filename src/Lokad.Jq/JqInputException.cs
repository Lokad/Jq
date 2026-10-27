namespace Lokad.Jq;

// Input parsing and path-resolution failures carry their command status.
// These remain catchable inside input pulls; failed opens are tracked separately
// by JqInputCursor and remain outside jq handlers.
internal sealed class JqInputException(string message, int exitCode) : JqException(message)
{
    internal int ExitCode { get; } = exitCode;
}
