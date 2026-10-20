namespace Lokad.Jq;

// A catchable evaluation failure.
internal sealed class JqRuntimeException(string message) : JqException(message);
