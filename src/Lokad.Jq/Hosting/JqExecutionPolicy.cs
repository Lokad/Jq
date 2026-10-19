using System;

namespace Lokad.Jq;

/// <summary>Immutable execution capabilities and string-construction safeguards.</summary>
/// <remarks>
/// Shared storage admission uses IMemoryBudget. The selected borrowed regex engine
/// implements cancellation during its own operations. Execution has no cumulative fuel.
/// </remarks>
public sealed record JqExecutionPolicy
{
    private int _maximumStringLength = JqBudget.MaximumStringLength;
    private IJqRegexEngine _regexEngine = JqUtf8RegexEngine.Instance;

    /// <summary>Gets the existing resource allowances used when no policy is supplied.</summary>
    public static JqExecutionPolicy Default { get; } = new();

    /// <summary>Gets or initializes the maximum UTF-16 code units in a string; default 8 Mi code units.</summary>
    public int MaximumStringLength
    {
        get => _maximumStringLength;
        init => _maximumStringLength = (int)Validate(value, JqBudget.MaximumStringLength, nameof(MaximumStringLength));
    }

    /// <summary>Gets or initializes the borrowed regex engine; defaults to managed PCRE2.</summary>
    public IJqRegexEngine RegexEngine
    {
        get => _regexEngine;
        init => _regexEngine = value ?? throw new ArgumentNullException(nameof(RegexEngine));
    }

    private static long Validate(long value, long ceiling, string name)
    {
        if (value <= 0 || value > ceiling)
            throw new ArgumentOutOfRangeException(name, value, $"Use a positive allowance up to {ceiling}.");
        return value;
    }
}
