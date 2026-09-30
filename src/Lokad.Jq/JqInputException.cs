namespace Lokad.Jq;

// An input-stage failure with its reference exit status attached: malformed inputs
// report status 5 and missing input operands report status 2. Extending JqException
// keeps these catchable wherever input pulls evaluate inside user code.
internal sealed class JqInputException(string message, int exitCode) : JqException(message)
{
    internal int ExitCode { get; } = exitCode;
}
