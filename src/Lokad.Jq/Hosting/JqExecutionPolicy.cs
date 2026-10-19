using System;

namespace Lokad.Jq;

/// <summary>Immutable string-construction and per-search regex safeguards.</summary>
/// <remarks>
/// Shared live-storage admission uses IMemoryBudget. Regex work and time
/// allowances apply to each engine search; execution has no cumulative fuel.
/// Positive limits may be reduced from the tested defaults. Parser, depth, JSON-buffer,
/// and regex workspace safety ceilings remain fixed.
/// </remarks>
public sealed record JqExecutionPolicy
{
    internal const int RegexWorkCeiling = 10_000_000;
    internal static readonly TimeSpan RegexTimeCeiling = TimeSpan.FromSeconds(5);

    private int _maximumStringLength = JqBudget.MaximumStringLength;
    private int _maximumRegexWork = RegexWorkCeiling;
    private TimeSpan _maximumRegexTime = RegexTimeCeiling;

    /// <summary>Gets the existing resource allowances used when no policy is supplied.</summary>
    public static JqExecutionPolicy Default { get; } = new();

    /// <summary>Gets or initializes the maximum UTF-16 code units in a string; default 8 Mi code units.</summary>
    public int MaximumStringLength
    {
        get => _maximumStringLength;
        init => _maximumStringLength = (int)Validate(value, JqBudget.MaximumStringLength, nameof(MaximumStringLength));
    }

    /// <summary>Gets or initializes engine work steps per regex search; default 10,000,000.</summary>
    public int MaximumRegexWork
    {
        get => _maximumRegexWork;
        init => _maximumRegexWork = (int)Validate(value, RegexWorkCeiling, nameof(MaximumRegexWork));
    }

    /// <summary>Gets or initializes time per regex search, excluding compilation and replacement evaluation; default five seconds.</summary>
    public TimeSpan MaximumRegexTime
    {
        get => _maximumRegexTime;
        init
        {
            if (value <= TimeSpan.Zero || value > RegexTimeCeiling)
                throw new ArgumentOutOfRangeException(nameof(MaximumRegexTime), value, "Use a positive allowance up to five seconds.");
            _maximumRegexTime = value;
        }
    }

    private static long Validate(long value, long ceiling, string name)
    {
        if (value <= 0 || value > ceiling)
            throw new ArgumentOutOfRangeException(name, value, $"Use a positive allowance up to {ceiling}.");
        return value;
    }
}
