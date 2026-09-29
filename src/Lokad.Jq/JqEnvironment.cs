using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

// Immutable lexical environments: invocation variables form the root and
// each binding extends a new scope. Sibling generator branches hold their
// own environments, so captures can never leak across branches.
internal sealed class JqEnvironment
{
    private readonly IReadOnlyDictionary<string, JsonNode?> _root;
    private readonly JqEnvironment? _parent;
    private readonly string? _name;
    private readonly JsonNode? _value;

    private JqEnvironment(IReadOnlyDictionary<string, JsonNode?> root)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
    }

    private JqEnvironment(JqEnvironment parent, string name, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(name);
        _root = parent._root;
        _parent = parent;
        _name = name;
        _value = value;
    }

    internal static JqEnvironment CreateRoot(IReadOnlyDictionary<string, JsonNode?> variables) =>
        new(variables);

    internal JqEnvironment Extend(string name, JsonNode? value) =>
        new(this, name, value);

    internal bool TryGetValue(string name, out JsonNode? value)
    {
        JqEnvironment? scope = this;
        while (scope is not null)
        {
            if (scope._name is not null && string.Equals(scope._name, name, StringComparison.Ordinal))
            {
                value = scope._value;
                return true;
            }
            scope = scope._parent;
        }

        return _root.TryGetValue(name, out value);
    }
}
