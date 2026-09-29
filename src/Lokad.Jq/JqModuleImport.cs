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
    JsonObject? Metadata,
    JqSourceSpan Span,
    JqSourceSpan PathSpan)
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
    internal static string? Validate(string relPath)
    {
        ArgumentNullException.ThrowIfNull(relPath);
        if (relPath.Contains((char)0))
            return "Module path contains a NUL byte";
        if (relPath.Contains((char)92))
            return "Modules must be named by relative paths using \'/\', not \'\\\' (" + relPath + ")";
        var components = relPath.Split((char)47);
        for (var i = 0; i < components.Length; i++)
        {
            if (components[i] == "..")
                return "Relative paths to modules may not traverse to parent directories (" + relPath + ")";
            if (i > 0 && string.Equals(components[i], components[i - 1], StringComparison.Ordinal))
                return "module names must not have equal consecutive components: " + relPath;
        }
        return null;
    }
}
