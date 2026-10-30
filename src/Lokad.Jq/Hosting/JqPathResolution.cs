using System;
using System.Collections.Generic;

namespace Lokad.Jq;

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

    internal static JqPathRequest CreateRequest(string raw, JqPath currentDirectory, JqBudget budget)
    {
        budget.CheckCancellation();
        budget.ReserveMetadata(32, 1);
        budget.CheckCancellation();
        return new JqPathRequest(raw, currentDirectory);
    }

    internal static string Location(JqPathRequest request, JqBudget budget) =>
        Combine(request.CurrentDirectory.Path, request.Path, budget);

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
