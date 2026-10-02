using System;

namespace Lokad.Jq;

internal class JqException : Exception
{
    internal JqException(string message)
        : base(message)
    {
    }

    internal JqException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
