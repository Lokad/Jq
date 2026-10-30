namespace Lokad.Jq;

// Expected host failures bypass jq language handlers. The executor supplies
// diagnostic context and command status without discarding the host cause.
internal sealed class JqOperationException(string operation, string path, JqOperationError error)
    : Exception(error.Message)
{
    internal string Operation { get; } = operation;
    internal string Path { get; } = path;
    internal JqOperationError Error { get; } = error;
}
