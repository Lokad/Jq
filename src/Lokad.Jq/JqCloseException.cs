namespace Lokad.Jq;

// A reported close failure, preserving its structured origin through input
// handling rather than classifying or reconstructing it from diagnostic text.
internal sealed class JqCloseException(string path, JqOperationError error)
    : JqException("input close failed")
{
    internal string Path { get; } = path;
    internal JqOperationError Error { get; } = error;
}
