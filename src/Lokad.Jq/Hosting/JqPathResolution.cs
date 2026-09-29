using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lokad.Jq;

internal readonly struct JqResolvedPath(ReadOnlyMemory<byte> display, JqPath? absolute)
{
    public ReadOnlyMemory<byte> Display { get; } = display;

    public JqPath Absolute => absolute ??
        throw new InvalidOperationException("The default resolved path has no absolute path.");

    public override string ToString() => Absolute.Path;
}

/// <summary>Provides canonical path resolution at the jq host boundary.</summary>
public static class JqPathResolution
{
    /// <summary>Resolves the current directory from the first valid exported <c>PWD</c>, or returns root.</summary>
    public static JqPath ResolveCurrentDirectory(IReadOnlyList<JqEnvironmentVariable> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        foreach (var entry in environment)
        {
            if (!string.Equals(entry.Name, "PWD", StringComparison.Ordinal))
            {
                continue;
            }

            if (JqPath.IsValid(entry.Value))
            {
                return new JqPath(entry.Value);
            }

            break;
        }

        return JqPath.Root;
    }

    internal static JqResolvedPath ResolveArgument(string raw, JqPath currentDirectory)
    {
        var absolute = JqRawPath.Parse(raw, CultureInfo.InvariantCulture).ToAbsolute(currentDirectory);
        return new JqResolvedPath(Helpers.Utf8Text.Encode(raw), absolute);
    }

}
