using System;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

// A single top-level `import` or `include` directive. Alias is null for
// `include`; otherwise it is the plain alias name (without `$` for data).
// Metadata is the constant object following the directive, or null when absent.
internal sealed record JqModuleImport(
    string RelPath,
    string? Alias,
    bool IsData,
    JsonObject? Metadata)
{
    internal bool IsInclude => Alias is null && !IsData;

    internal bool IsOptional
    {
        get
        {
            if (Metadata is null)
                return false;
            if (Metadata.TryGetPropertyValue("optional", out JsonNode? value) && value is JsonValue scalar)
            {
                if (scalar.TryGetValue<bool>(out bool flag))
                    return flag;
            }
            return false;
        }
    }

    internal bool IsRaw
    {
        get
        {
            if (Metadata is null)
                return false;
            if (Metadata.TryGetPropertyValue("raw", out JsonNode? value) && value is JsonValue scalar)
            {
                if (scalar.TryGetValue<bool>(out bool flag))
                    return flag;
            }
            return false;
        }
    }
}

// Relative module path validation matching the reference messages.
internal static class JqModulePathValidation
{
    internal static string? Validate(string relPath, JqBudget budget)
    {
        ArgumentNullException.ThrowIfNull(relPath);
        bool backslash = false;
        for (int i = 0; i < relPath.Length; i++)
        {
            if ((i & 4095) == 0) budget.CheckCancellation();
            if (relPath[i] == (char)0) return "Module path contains a NUL byte";
            backslash |= relPath[i] == (char)92;
        }
        if (backslash)
            return Failure("Modules must be named by relative paths using \'/\', not \'\\\' (");
        int previousStart = 0, previousLength = -1, start = 0;
        for (int i = 0; i <= relPath.Length; i++)
        {
            if ((i & 4095) == 0) budget.CheckCancellation();
            if (i < relPath.Length && relPath[i] != '/') continue;
            ReadOnlySpan<char> component = relPath.AsSpan(start, i - start);
            if (component.SequenceEqual(".."))
                return Failure("Relative paths to modules may not traverse to parent directories (");
            if (previousLength == component.Length
                && component.SequenceEqual(relPath.AsSpan(previousStart, previousLength)))
            {
                const string prefix = "module names must not have equal consecutive components: ";
                budget.ReserveString(prefix.Length + (long)relPath.Length);
                return prefix + relPath;
            }
            previousStart = start;
            previousLength = component.Length;
            start = i + 1;
        }
        return null;

        string Failure(string prefix)
        {
            budget.ReserveString(prefix.Length + (long)relPath.Length + 1);
            return prefix + relPath + ")";
        }
    }
}
