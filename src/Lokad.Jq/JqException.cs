using System;

namespace Lokad.Jq;

internal sealed class JqException(string message) : Exception(message);
