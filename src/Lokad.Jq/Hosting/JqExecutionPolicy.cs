using System;

namespace Lokad.Jq;

/// <summary>Immutable execution capabilities.</summary>
/// <remarks>
/// Shared storage admission uses IMemoryBudget. The selected borrowed regex engine
/// implements cancellation during its own operations.
/// </remarks>
public sealed record JqExecutionPolicy
{
    private IJqRegexEngine _regexEngine = JqUtf8RegexEngine.Instance;

    /// <summary>Gets the default execution capabilities used when no policy is supplied.</summary>
    public static JqExecutionPolicy Default { get; } = new();

    /// <summary>Gets or initializes the borrowed regex engine; defaults to managed PCRE2.</summary>
    public IJqRegexEngine RegexEngine
    {
        get => _regexEngine;
        init => _regexEngine = value ?? throw new ArgumentNullException(nameof(RegexEngine));
    }

}
