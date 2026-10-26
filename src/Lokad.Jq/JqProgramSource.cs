using System;

namespace Lokad.Jq;

// Identifies the program text under compilation: either inline filter text
// or a hosted file. Display paths are virtual host paths, never machine paths.
internal sealed record JqProgramSource(string Label, string? Path)
{
    internal static JqProgramSource Inline { get; } = new("filter", null);

    internal static JqProgramSource File(string displayPath, JqBudget budget)
    {
        ArgumentException.ThrowIfNullOrEmpty(displayPath);
        budget.ReserveString((long)displayPath.Length + 7);
        budget.ReserveMetadata(32, 1);
        return new("file \"" + displayPath + "\"", displayPath);
    }
}
