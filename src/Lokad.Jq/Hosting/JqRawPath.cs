using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Lokad.Jq;

/// <summary>
/// Well-formed but not yet canonicalized jq path.
/// </summary>
internal sealed class JqRawPath : IParsable<JqRawPath>
{
    private JqRawPath(string raw)
    {
        Raw = raw;
    }

    /// <summary>Gets the validated path text before canonicalization.</summary>
    public string Raw { get; }

    /// <summary>Returns whether <paramref name="s"/> is a well-formed raw jq path.</summary>
    public static bool IsValid(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return false;

        for (var index = 0; index < s.Length; index++)
        {
            if (JqPath.IsForbiddenChar(s[index]))
                return false;
        }

        return true;
    }

    /// <summary>Parses a well-formed raw jq path.</summary>
    public static JqRawPath Parse(string s) => Parse(s, null);

    /// <summary>Parses a well-formed raw jq path; the format provider is ignored.</summary>
    public static JqRawPath Parse(string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!TryParse(s, provider, out var result))
        {
            throw new FormatException($"'{s}' is not a valid jq path.");
        }

        return result;
    }

    /// <summary>Attempts to parse a well-formed raw jq path; the format provider is ignored.</summary>
    public static bool TryParse(string? s, IFormatProvider? provider, [NotNullWhen(true)] out JqRawPath? result)
    {
        if (s is not null && IsValid(s))
        {
            result = new JqRawPath(s);
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>Attempts to parse a well-formed raw jq path.</summary>
    public static bool TryParse(string? s, [NotNullWhen(true)] out JqRawPath? result) =>
        TryParse(s, null, out result);

    /// <summary>Resolves this path against <paramref name="current"/> without a confinement boundary.</summary>
    public JqPath ToAbsolute(JqPath current) =>
        ResolveAbsolute(current, JqPathConfinement.None);

    /// <summary>
    /// Resolves this path against <paramref name="current"/> and rejects results outside that directory.
    /// </summary>
    public JqPath ToAbsoluteWithin(JqPath current) =>
        ResolveAbsolute(current, JqPathConfinement.CurrentDirectory);

    private JqPath ResolveAbsolute(JqPath current, JqPathConfinement confinement)
    {
        ArgumentNullException.ThrowIfNull(current);
        var combined = Raw.StartsWith('/')
            ? Raw
            : current.Path.Length == 1
                ? "/" + Raw
                : current.Path + "/" + Raw;

        var span = combined.AsSpan(1);
        List<PathSegment>? segments = null;

        foreach (var range in span.Split('/'))
        {
            var start = range.Start.Value;
            var len = range.End.Value - start;

            var isDot = len == 1 && span[start] == '.';
            var isDotDot = len == 2 && span[start] == '.' && span[start + 1] == '.';

            if (!isDot && !isDotDot && len > 0)
            {
                segments?.Add(new PathSegment(start + 1, len));
                continue;
            }

            if (segments is null)
            {
                segments = [];
                foreach (var r in span[..start].Split('/'))
                {
                    var s = r.Start.Value;
                    var l = r.End.Value - s;
                    if (l > 0)
                    {
                        segments.Add(new PathSegment(s + 1, l));
                    }
                }
            }

            if (len == 0 || isDot)
            {
                continue;
            }

            if (segments.Count == 0)
            {
                throw new JqPathException($"Cannot resolve '{Raw}': '..' goes above the root.");
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
            var totalLen = Math.Max(1, segments.Count);
            foreach (var segment in segments)
            {
                totalLen += segment.Length;
            }

            resultPath = string.Create(
                totalLen,
                new PathCreationState(combined, segments),
                static (destination, state) =>
            {
                destination[0] = '/';
                var position = 1;
                for (var index = 0; index < state.Segments.Count; index++)
                {
                    if (index > 0)
                        destination[position++] = '/';

                    var segment = state.Segments[index];
                    state.Source.AsSpan(segment.Start, segment.Length)
                        .CopyTo(destination[position..]);
                    position += segment.Length;
                }
            });
        }

        var result = new JqPath(resultPath);

        if (confinement is JqPathConfinement.CurrentDirectory && !current.IsEqualToOrParentOf(result))
        {
            throw new JqPathException(
                $"Cannot resolve '{Raw}': result '{resultPath}' escapes jail '{current.Path}'.");
        }

        return result;
    }

    /// <inheritdoc />
    public override string ToString() => Raw;

    private readonly record struct PathSegment(int Start, int Length);

    private enum JqPathConfinement
    {
        None,
        CurrentDirectory
    }

    private readonly struct PathCreationState(
        string? source,
        IReadOnlyList<PathSegment>? segments)
    {
        public string Source => source ??
            throw new InvalidOperationException("The default path creation state has no source.");

        public IReadOnlyList<PathSegment> Segments => segments ??
            throw new InvalidOperationException("The default path creation state has no segments.");
    }
}
