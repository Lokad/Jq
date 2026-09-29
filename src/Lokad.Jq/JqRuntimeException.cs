using System;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

// A catchable evaluation failure. Payload carries an optional JSON value for
// future try/catch handlers; until then it travels with the message and the
// executor renders diagnostics exactly as before.
internal sealed class JqRuntimeException : JqException
{
    public JqRuntimeException(string message)
        : base(message)
    {
        Payload = null;
    }

    public JqRuntimeException(string message, JsonNode? payload)
        : base(message)
    {
        Payload = payload;
    }

    public JsonNode? Payload { get; }
}
