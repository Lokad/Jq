using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Lokad.Jq;

/// <summary>
/// Well-formed but not yet canonicalized jq path.
/// </summary>
internal sealed class JqRawPath
{
    private JqRawPath(string raw)
    {
        Raw = raw;
    }

    /// <summary>Gets the validated path text before canonicalization.</summary>
    public string Raw { get; }

    /// <summary>Returns whether <paramref name="s"/> is a well-formed raw jq path.</summary>
    private static bool IsValid(string? s, JqBudget budget)
    {
        if (string.IsNullOrEmpty(s))
            return false;

        for (var index = 0; index < s.Length; index++)
        {
            if ((index & 4095) == 0) budget.CheckCancellation();
            if (JqPath.IsForbiddenChar(s[index]))
                return false;
        }

        return true;
    }

    /// <summary>Parses a well-formed raw jq path.</summary>
    internal static JqRawPath Parse(string s, JqBudget budget)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!TryParse(s, budget, out var result))
        {
            budget.ReserveString((long)s.Length + 32);
            throw new FormatException($"'{s}' is not a valid jq path.");
        }

        return result;
    }

    /// <summary>Attempts to parse a well-formed raw jq path.</summary>
    internal static bool TryParse(string? s, JqBudget budget, [NotNullWhen(true)] out JqRawPath? result)
    {
        budget.CheckCancellation();
        if (s is not null && IsValid(s, budget))
        {
            budget.ReserveMetadata(32, 1);
            result = new JqRawPath(s);
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>Resolves this path against <paramref name="current"/>.</summary>
    internal JqPath ToAbsolute(JqPath current, JqBudget budget)
    {
        if (TryToAbsolute(current, budget, out JqPath? result)) return result;
        budget.ReserveString((long)Raw.Length + 64);
        throw new JqPathException($"Cannot resolve '{Raw}': '..' goes above the root.");
    }

    internal bool TryToAbsolute(JqPath current, JqBudget budget, [NotNullWhen(true)] out JqPath? result)
    {
        ArgumentNullException.ThrowIfNull(current);
        budget.CheckCancellation();
        if (!Raw.StartsWith('/'))
            budget.ReserveString((long)current.Path.Length + (current.Path.Length == 1 ? 0 : 1) + Raw.Length);
        var combined = Raw.StartsWith('/')
            ? Raw
            : current.Path.Length == 1
                ? "/" + Raw
                : current.Path + "/" + Raw;

        if (combined == "/")
        {
            result = JqPath.Root;
            return true;
        }
        var span = combined.AsSpan(1);
        List<PathSegment>? segments = null;
        JqScopeStorage? scratch = null;
        try
        {
            foreach (var range in span.Split('/'))
            {
                budget.CheckCancellation();
                var start = range.Start.Value;
                var len = range.End.Value - start;

                var isDot = len == 1 && span[start] == '.';
                var isDotDot = len == 2 && span[start] == '.' && span[start + 1] == '.';

                if (!isDot && !isDotDot && len > 0)
                {
                    if (segments is not null && scratch is not null) AddSegment(segments, new PathSegment(start + 1, len), scratch);
                    continue;
                }

                if (segments is null)
                {
                    scratch = new JqScopeStorage(budget);
                    scratch.Reserve(32, 1);
                    segments = [];
                    foreach (var r in span[..start].Split('/'))
                    {
                        budget.CheckCancellation();
                        var s = r.Start.Value;
                        var l = r.End.Value - s;
                        if (l > 0)
                        {
                            AddSegment(segments, new PathSegment(s + 1, l), scratch);
                        }
                    }
                }

                if (len == 0 || isDot)
                {
                    continue;
                }

                if (segments.Count == 0)
                {
                    result = null;
                    return false;
                }

                segments.RemoveAt(segments.Count - 1);
            }

            string resultPath;
            if (segments is null)
            {
                resultPath = combined;
            }
            else
            {
                long totalLen = Math.Max(1, segments.Count);
                foreach (var segment in segments)
                {
                    budget.CheckCancellation();
                    totalLen += segment.Length;
                }
                budget.ReserveString(totalLen);
                resultPath = string.Create(
                    (int)totalLen,
                    (Source: combined, Segments: segments, Budget: budget),
                    static (destination, state) =>
                {
                    destination[0] = '/';
                    var position = 1;
                    for (var index = 0; index < state.Segments.Count; index++)
                    {
                        state.Budget.CheckCancellation();
                        if (index > 0)
                            destination[position++] = '/';

                        var segment = state.Segments[index];
                        state.Source.AsSpan(segment.Start, segment.Length)
                            .CopyTo(destination[position..]);
                        position += segment.Length;
                    }
                });
            }

            budget.ReserveMetadata(32, 1);
            result = new JqPath(resultPath);
            return true;
        }
        finally { scratch?.Dispose(); }

        static void AddSegment(List<PathSegment> segments, PathSegment segment, JqScopeStorage scratch)
        {
            if (segments.Count == segments.Capacity)
            {
                int previous = segments.Capacity;
                int capacity = JqBudget.BufferCapacity((long)segments.Count + 1, Math.Max(4, 2L * previous));
                scratch.Reserve(24 + 8L * capacity, 1);
                segments.Capacity = capacity;
                if (previous != 0) scratch.Release(24 + 8L * previous);
            }
            segments.Add(segment);
        }
    }

    /// <inheritdoc />
    public override string ToString() => Raw;

    private readonly record struct PathSegment(int Start, int Length);
}
