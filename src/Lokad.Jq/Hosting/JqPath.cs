using System;

namespace Lokad.Jq;

/// <summary>
/// Canonical absolute path used by Lokad.Jq hosts.
/// </summary>
public sealed class JqPath
{
    /// <summary>Initializes a canonical absolute path.</summary>
    public JqPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!IsValid(path))
        {
            throw new ArgumentException($"'{path}' is not a valid absolute jq path.", nameof(path));
        }

        Path = path;
    }

    private JqPath()
    {
        Path = "/";
    }

    /// <summary>Gets the canonical absolute path text.</summary>
    public string Path { get; }

    /// <summary>Gets the canonical root path.</summary>
    public static JqPath Root { get; } = new();

    /// <summary>Returns whether <paramref name="path"/> is canonical absolute jq path text.</summary>
    public static bool IsValid(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
        {
            return false;
        }

        if (path == "/")
        {
            return true;
        }

        if (path[^1] == '/')
        {
            return false;
        }

        var segmentStart = 1;
        for (var index = 1; index <= path.Length; index++)
        {
            if (index < path.Length && path[index] != '/')
            {
                if (IsForbiddenChar(path[index]))
                    return false;

                continue;
            }

            var segmentLength = index - segmentStart;
            if (segmentLength == 0 ||
                segmentLength == 1 && path[segmentStart] == '.' ||
                segmentLength == 2 && path[segmentStart] == '.' && path[segmentStart + 1] == '.')
            {
                return false;
            }

            segmentStart = index + 1;
        }

        return true;
    }

    internal static bool IsForbiddenChar(char c) => c is <= '\x1f' or '\x7f';

    /// <inheritdoc />
    public override string ToString() => Path;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is JqPath other && Path == other.Path;

    /// <inheritdoc />
    public override int GetHashCode() => Path.GetHashCode(StringComparison.Ordinal);
}

/// <summary>Represents a failure to resolve a well-formed jq path.</summary>
/// <param name="message">The jq-shaped resolution error.</param>
public sealed class JqPathException(string message) :
    InvalidOperationException(message ?? throw new ArgumentNullException(nameof(message)));
