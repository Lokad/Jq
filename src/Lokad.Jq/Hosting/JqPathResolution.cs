using System;
using System.Collections.Generic;

namespace Lokad.Jq;

internal readonly struct JqResolvedPath(JqPathRequest request)
{
    public JqPathRequest Request => request ??
        throw new InvalidOperationException("The default path has no request.");

    public string Display => Request.Path;

    internal string Location(JqBudget budget) =>
        JqPathResolution.Combine(Request.CurrentDirectory.Path, Request.Path, budget);
}

/// <summary>Selects the caller's explicit current directory.</summary>
public static class JqPathResolution
{
    /// <summary>Uses the first exported <c>PWD</c> if canonical; otherwise returns root.</summary>
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

    internal static JqResolvedPath ResolveArgument(string raw, JqPath currentDirectory, JqBudget budget)
    {
        budget.CheckCancellation();
        budget.ReserveMetadata(32, 1);
        budget.CheckCancellation();
        return new JqResolvedPath(new JqPathRequest(raw, currentDirectory));
    }

    // This joins spelling only. Lookup and traversal always belong to the host.
    internal static string Combine(string directory, string path, JqBudget budget)
    {
        budget.CheckCancellation();
        if (path.StartsWith('/')) return path;
        bool separator = !directory.EndsWith('/');
        budget.ReserveString((long)directory.Length + path.Length + (separator ? 1 : 0));
        budget.CheckCancellation();
        return separator ? directory + "/" + path : directory + path;
    }

    internal static string ParentDirectory(string path, JqBudget budget)
    {
        budget.CheckCancellation();
        int slash = path.LastIndexOf('/');
        if (slash <= 0) return "/";
        budget.ReserveString(slash);
        budget.CheckCancellation();
        return path[..slash];
    }

}
